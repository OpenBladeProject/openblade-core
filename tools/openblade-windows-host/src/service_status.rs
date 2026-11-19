//! The managed ServiceMessages v80 shape shared by the installed Service and Agent candidates.
//! Preserve rich feature records verbatim; readiness consumes the core snapshot
//! and never interprets feature data as independent device or write evidence.

use std::io;
#[path = "fan_request.rs"]
mod fan_request;
pub(crate) use fan_request::FanOverride;

use openblade_windows_host::json_object::{self, nullable_object, object};
use serde::{Deserialize, Serialize};
use serde_json::{Map, Value};

use tokio::{
    io::{AsyncRead, AsyncWrite},
    time::timeout,
};

use crate::service_frame::{FRAME_TIMEOUT, invalid, is_guid, read_payload, write_frame};

pub(crate) const PIPE_NAME: &str = "OpenBlade.Service.v2";
pub(crate) const PROTOCOL_VERSION: i32 = 80;

pub(crate) fn production_path() -> String {
    format!(r"\\.\pipe\{PIPE_NAME}")
}

// LiveReadingSensors is a four-bit mask. The managed legacy default is All.
const ALL_LIVE_READINGS: u8 = 15;

// Decoded through `json_object`: managed reads the request and every nested
// struct member only from a JSON object, never a positional array.
#[derive(Clone, Deserialize, Serialize)]
#[serde(
    tag = "operation",
    rename_all = "camelCase",
    rename_all_fields = "camelCase",
    deny_unknown_fields
)]
pub(crate) enum StatusRequest {
    SetBatteryPolicy {
        version: i32,
        request_id: String,
        #[serde(deserialize_with = "object")]
        value: openblade_domain::battery_history::Policy,
    },
    SetBatteryProtection {
        version: i32,
        request_id: String,
        enabled: bool,
    },
    SetFullChargeOverride {
        version: i32,
        request_id: String,
        enabled: bool,
    },
    SetFanOverride {
        version: i32,
        request_id: String,
        #[serde(deserialize_with = "nullable_object")]
        value: Option<FanOverride>,
        #[serde(default)]
        settings: Option<Value>,
    },
    SetPerformanceOverride {
        version: i32,
        request_id: String,
        #[serde(deserialize_with = "nullable_object")]
        value: Option<PerformanceOverride>,
    },
    ReportApplicationContext {
        version: i32,
        request_id: String,
        instance_id: String,
        sequence: i64,
        #[serde(deserialize_with = "Deserialize::deserialize")]
        executable_path: Option<String>,
    },
    GetStatus {
        version: i32,
        request_id: String,
    },
    SubscribeStatus {
        version: i32,
        request_id: String,
    },
    ObserveState {
        include_live_readings: bool,
        #[serde(default = "all_live_readings")]
        requested_live_readings: u8,
        // Computed by the managed codec. Dispatch derives it from the
        // request fields so an advisory value cannot change admission.
        effective_live_readings: Option<u8>,
        version: i32,
        request_id: String,
    },
}

fn all_live_readings() -> u8 {
    ALL_LIVE_READINGS
}

impl StatusRequest {
    pub(crate) fn get(request_id: String) -> io::Result<Self> {
        let request = Self::GetStatus {
            version: PROTOCOL_VERSION,
            request_id,
        };
        request.validate()?;
        Ok(request)
    }

    #[cfg(feature = "native-agent-entry-compile-check")]
    pub(crate) fn observe_performance(request_id: String) -> io::Result<Self> {
        let request = Self::ObserveState {
            version: PROTOCOL_VERSION,
            request_id,
            include_live_readings: false,
            requested_live_readings: 0,
            effective_live_readings: Some(0),
        };
        request.validate()?;
        Ok(request)
    }

    #[cfg(feature = "installed-service-candidate")]
    pub(crate) fn decode_get(payload: &[u8]) -> io::Result<Self> {
        let request = Self::decode(payload)?;
        if !matches!(request, Self::GetStatus { .. }) {
            return Err(invalid("invalid GetStatus operation"));
        }
        Ok(request)
    }

    #[cfg(feature = "installed-service-candidate")]
    pub(crate) fn decode(payload: &[u8]) -> io::Result<Self> {
        openblade_domain::thermal_history::validate_json_members(payload)
            .map_err(|_| invalid("duplicate Service request member"))?;
        let request: Self = json_object::from_slice(payload)
            .map_err(|_| invalid("invalid Service status request"))?;
        request.validate()?;
        Ok(request)
    }

    pub(crate) fn is_subscription(&self) -> bool {
        matches!(self, Self::SubscribeStatus { .. })
    }

    pub(crate) fn is_observation(&self) -> bool {
        matches!(self, Self::ObserveState { .. })
    }

    #[cfg(feature = "installed-service-candidate")]
    pub(crate) fn requests_live_readings(&self) -> bool {
        matches!(
            self,
            Self::ObserveState {
                include_live_readings: true,
                requested_live_readings: 1..,
                ..
            }
        )
    }

    pub(crate) fn header(&self) -> (i32, &str) {
        match self {
            Self::SetBatteryPolicy {
                version,
                request_id,
                ..
            }
            | Self::SetBatteryProtection {
                version,
                request_id,
                ..
            }
            | Self::SetFullChargeOverride {
                version,
                request_id,
                ..
            } => (*version, request_id),
            Self::SetFanOverride {
                version,
                request_id,
                ..
            } => (*version, request_id),
            Self::SetPerformanceOverride {
                version,
                request_id,
                ..
            }
            | Self::ReportApplicationContext {
                version,
                request_id,
                ..
            } => (*version, request_id),
            Self::GetStatus {
                version,
                request_id,
            }
            | Self::SubscribeStatus {
                version,
                request_id,
            }
            | Self::ObserveState {
                version,
                request_id,
                ..
            } => (*version, request_id),
        }
    }

    pub(crate) fn battery_request(&self) -> Option<openblade_domain::battery_history::Request> {
        use openblade_domain::battery_history::Request;
        match self {
            Self::SetBatteryPolicy { value, .. } => Some(Request::SetBatteryPolicy {
                value: value.clone(),
            }),
            Self::SetBatteryProtection { enabled, .. } => {
                Some(Request::SetBatteryProtection { enabled: *enabled })
            }
            Self::SetFullChargeOverride { enabled, .. } => {
                Some(Request::SetFullChargeOverride { enabled: *enabled })
            }
            _ => None,
        }
    }
    fn validate(&self) -> io::Result<()> {
        if let Some(request) = self.battery_request() {
            if !cfg!(feature = "service-battery-policy-v1") {
                return Err(invalid("signed battery composition is unavailable"));
            }
            request.validate()?;
        }
        if matches!(
            self,
            Self::SetFanOverride { .. }
                | Self::SetPerformanceOverride { .. }
                | Self::ReportApplicationContext { .. }
        ) && !cfg!(feature = "service-thermal-mutation-v1")
        {
            return Err(invalid("this signed composition is read-only"));
        }
        if let Self::SetPerformanceOverride {
            value: Some(value), ..
        } = self
        {
            value.validate()?;
        }
        if let Self::SetFanOverride {
            value, settings, ..
        } = self
        {
            if !cfg!(feature = "service-fan-mutation-v2") {
                return Err(invalid("signed fan composition is unavailable"));
            }
            openblade_domain::fan_history::validate_requested(
                &serde_json::json!({"value":value,"settings":settings}),
            )?;
        }
        let (version, request_id) = self.header();
        if version != PROTOCOL_VERSION || !is_guid(request_id) {
            return Err(invalid("invalid Service status version or request ID"));
        }
        if let Self::ObserveState {
            include_live_readings,
            requested_live_readings,
            effective_live_readings,
            ..
        } = self
            && (*requested_live_readings & !ALL_LIVE_READINGS != 0
                || effective_live_readings.is_some_and(|effective| {
                    effective
                        != if *include_live_readings {
                            *requested_live_readings
                        } else {
                            0
                        }
                }))
        {
            return Err(invalid("invalid ObserveState live readings selector"));
        }
        Ok(())
    }

    #[cfg(feature = "installed-service-candidate")]
    pub(crate) fn matches(&self, other: &Self) -> bool {
        self.header().1.eq_ignore_ascii_case(other.header().1)
    }
}

#[derive(Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
enum Frame {
    Response,
}

// Check confirmation fields before Snapshot's feature maps can collapse
// duplicate members. GetStatus and SubscribeStatus keep their existing codec.
#[derive(Deserialize)]
struct ObserveReplyMembers {
    #[serde(rename = "status", deserialize_with = "object")]
    _status: ObserveStatusMembers,
}

#[derive(Deserialize)]
struct ObserveStatusMembers {
    #[serde(rename = "observationReason")]
    _observation_reason: Option<serde::de::IgnoredAny>,
    #[serde(rename = "unconfirmedWrites")]
    _unconfirmed_writes: Option<serde::de::IgnoredAny>,
    #[serde(
        rename = "confirmedState",
        default,
        deserialize_with = "nullable_object"
    )]
    _confirmed_state: Option<ObservedPerformanceMember>,
    #[serde(
        rename = "fanStopStatus",
        default,
        deserialize_with = "nullable_object"
    )]
    _fan_stop_status: Option<ObservedFanStopMember>,
}

#[derive(Deserialize)]
struct ObservedPerformanceMember {
    #[serde(rename = "performance")]
    _performance: Option<serde::de::IgnoredAny>,
}

#[derive(Deserialize)]
struct ObservedFanStopMember {
    #[serde(rename = "state")]
    _state: Option<serde::de::IgnoredAny>,
}

#[derive(Deserialize, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub(crate) struct StatusResponse {
    frame: Frame,
    version: i32,
    request_id: String,
    success: bool,
    error: Option<String>,
    status: Option<Snapshot>,
    result: Option<Value>,
    archive: Option<Value>,
    error_code: Option<i32>,
}

impl StatusResponse {
    #[cfg(feature = "service-thermal-mutation-v1")]
    pub(crate) fn mutation(request: &StatusRequest, outcome: Value) -> Self {
        Self {
            frame: Frame::Response,
            version: PROTOCOL_VERSION,
            request_id: request.header().1.to_ascii_lowercase(),
            success: true,
            error: None,
            // v80 success frames carry exactly one body. Status is published
            // separately through GetStatus, ObserveState and subscriptions.
            status: None,
            result: Some(outcome),
            archive: None,
            error_code: None,
        }
    }
    #[cfg(feature = "installed-service-candidate")]
    pub(crate) fn success(request: &StatusRequest, status: Snapshot) -> Self {
        Self {
            frame: Frame::Response,
            version: PROTOCOL_VERSION,
            request_id: request.header().1.to_ascii_lowercase(),
            success: true,
            error: None,
            status: Some(status),
            result: None,
            archive: None,
            error_code: None,
        }
    }

    #[cfg(feature = "installed-service-candidate")]
    pub(crate) fn failure(request: &StatusRequest, error: &str, error_code: Option<i32>) -> Self {
        Self {
            frame: Frame::Response,
            version: PROTOCOL_VERSION,
            request_id: request.header().1.to_ascii_lowercase(),
            success: false,
            error: Some(error.to_owned()),
            status: None,
            result: None,
            archive: None,
            error_code,
        }
    }

    #[cfg(all(test, feature = "installed-service-candidate"))]
    pub(crate) fn verify(payload: &[u8], request: &StatusRequest) -> io::Result<()> {
        Self::decode(payload, request, Some(0)).map(drop)
    }

    fn decode(
        payload: &[u8],
        request: &StatusRequest,
        expected_access: Option<i32>,
    ) -> io::Result<Snapshot> {
        let reply: Self = json_object::from_slice(payload)
            .map_err(|_| invalid("invalid Service status response"))?;
        if reply.version != PROTOCOL_VERSION
            || !reply.request_id.eq_ignore_ascii_case(request.header().1)
        {
            return Err(invalid("uncorrelated Service status response"));
        }
        if request.is_subscription()
            && !reply.success
            && reply.error_code == Some(0) // ServiceResponseErrorCode.CapacityUnavailable
            && reply.error.as_ref().is_some_and(|error| !error.trim().is_empty())
            && reply.status.is_none()
            && reply.result.is_none()
            && reply.archive.is_none()
        {
            return Err(io::Error::new(
                io::ErrorKind::WouldBlock,
                "Service subscription capacity is unavailable",
            ));
        }
        if request.is_observation()
            && !reply.success
            && reply
                .error
                .as_ref()
                .is_some_and(|error| !error.trim().is_empty())
            && reply.status.is_none()
            && reply.result.is_none()
            && reply.archive.is_none()
            && reply.error_code.is_none()
        {
            return Err(io::Error::new(
                io::ErrorKind::Unsupported,
                "Service refused fresh state observation",
            ));
        }
        if !reply.success
            || reply.error.is_some()
            || reply.result.is_some()
            || reply.archive.is_some()
            || reply.error_code.is_some()
        {
            return Err(invalid("unsuccessful Service status response"));
        }
        let status = reply
            .status
            .ok_or_else(|| invalid("Service status response has no snapshot"))?;
        if request.is_observation() {
            json_object::from_slice::<ObserveReplyMembers>(payload)
                .map_err(|_| invalid("invalid ObserveState confirmation members"))?;
        }
        status.verify(expected_access.unwrap_or(status.control_access))?;
        Ok(status)
    }
}

#[cfg(feature = "installed-service-candidate")]
pub(crate) fn encode_status_event(status: &Snapshot) -> io::Result<Vec<u8>> {
    #[derive(Serialize)]
    #[serde(tag = "frame", rename_all = "camelCase")]
    enum Event<'a> {
        StatusEvent { version: i32, status: &'a Snapshot },
    }

    crate::service_frame::encode(&Event::StatusEvent {
        version: PROTOCOL_VERSION,
        status,
    })
}

#[cfg(feature = "native-agent-entry-compile-check")]
pub(crate) async fn subscribe<S: AsyncRead + AsyncWrite + Unpin>(
    stream: &mut S,
    id: String,
    verify_peer: impl FnMut(&S) -> io::Result<()>,
) -> io::Result<Snapshot> {
    let request = StatusRequest::SubscribeStatus {
        version: PROTOCOL_VERSION,
        request_id: id,
    };
    request.validate()?;
    exchange(stream, &request, None, verify_peer).await
}

#[cfg(feature = "native-agent-entry-compile-check")]
pub(crate) async fn read_status_event(
    stream: &mut (impl AsyncRead + Unpin),
) -> io::Result<Snapshot> {
    // A plain struct, not a tagged enum: serde buffers tagged content, which
    // would let a map member read a non-integer number as a one-entry map.
    // serde_json reads an integer `-0` as a float, so typed members refuse it,
    // as `StatusResponse::decode` always has. Managed reads it as 0 but never
    // writes it; only `version` keeps reporting the mismatch it is.
    #[derive(Deserialize)]
    #[serde(deny_unknown_fields)]
    struct Event {
        frame: String,
        #[serde(deserialize_with = "event_version")]
        version: i32,
        status: Snapshot,
    }

    // With `arbitrary_precision` a `Number` keeps the exact text, so this reads
    // an Int32 as managed `TryGetInt32` does: `-0` is 0, while `-0.0`, `80.0`
    // and out-of-range integers are refused. A visitor would see `-0` and
    // `-0.0` as the same float. The scan refuses a private-token object first.
    fn event_version<'de, D: serde::Deserializer<'de>>(deserializer: D) -> Result<i32, D::Error> {
        serde_json::Number::deserialize(deserializer)?
            .as_i64()
            .and_then(|version| i32::try_from(version).ok())
            .ok_or_else(|| serde::de::Error::custom("an Int32 version"))
    }

    // The producer can go quiet when status is unchanged. Only the handshake
    // has a receive deadline. The owning watcher cancels or drops this stream.
    let payload = read_payload(stream).await?;
    let Event {
        frame,
        version,
        status,
    } = json_object::from_slice(&payload).map_err(|_| invalid("invalid Service status event"))?;
    if frame != "statusEvent" {
        return Err(invalid("invalid Service status event"));
    }
    if version != PROTOCOL_VERSION {
        return Err(invalid("Service status event version mismatch"));
    }
    status.verify(status.control_access)?;
    Ok(status)
}

type FeatureRecord = Map<String, Value>;

// Internal diagnostic provenance never crosses the v80 wire. Battery updates
// replace one family; private policy text is dropped at the redaction boundary.
#[derive(Clone, Copy)]
pub(crate) enum PrivatePolicyFamily {
    Thermal,
    Battery,
}

#[derive(Clone)]
struct OwnedDiagnostic {
    sid: String,
    message: String,
}

#[derive(Clone, Default)]
struct BatteryDiagnostic {
    public: String,
    request: Option<OwnedDiagnostic>,
}

#[derive(Clone, Default)]
struct Diagnostics {
    public: String,
    private: Option<String>,
    battery: Option<BatteryDiagnostic>,
    caller_denied: bool,
    private_unavailable: [bool; 2],
}

#[derive(Deserialize, Serialize)]
#[cfg_attr(feature = "installed-service-candidate", derive(Clone))]
#[serde(rename_all = "camelCase")]
pub(crate) struct Snapshot {
    #[serde(skip)]
    read_confirmation: Option<ReadConfirmation>,
    // Private projection authority is local evidence, never supplied by wire JSON.
    #[serde(skip)]
    private_user_sid: Option<String>,
    #[serde(skip)]
    diagnostics: Diagnostics,
    revision: i64,
    device_verified: bool,
    writes_blocked: bool,
    potential_controllers: Vec<String>,
    controller_detection_warning: Option<String>,
    external_changes: Vec<i32>,
    message: String,
    capabilities: Option<FeatureRecord>,
    confirmed_state: Option<FeatureRecord>,
    profiles: Profiles,
    desired_state: DesiredState,
    active_power_source: i32,
    #[serde(default, deserialize_with = "nullable_object")]
    live_readings: Option<LiveReadings>,
    performance_override: Option<FeatureRecord>,
    fan_override: Option<FeatureRecord>,
    lighting_override: Option<FeatureRecord>,
    full_charge_override: bool,
    control_access: i32,
    service_version: String,
    #[serde(flatten)]
    feature_fields: FeatureRecord,
}

// In-process evidence only. Never deserialized from a caller or retained status.
#[derive(Clone)]
pub(crate) struct ReadConfirmation {
    deadline: tokio::time::Instant,
    guard: std::sync::Arc<dyn Fn() -> bool + Send + Sync>,
}
impl ReadConfirmation {
    pub(crate) fn new(
        deadline: tokio::time::Instant,
        guard: impl Fn() -> bool + Send + Sync + 'static,
    ) -> Self {
        Self {
            deadline,
            guard: std::sync::Arc::new(guard),
        }
    }
    fn valid(&self) -> bool {
        tokio::time::Instant::now() < self.deadline && (self.guard)()
    }
}

// Wire values in the managed PerformancePreset and PowerSource enums.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) struct ConfirmedPerformance {
    pub(crate) preset: i32,
    pub(crate) active_power_source: i32,
}

impl Snapshot {
    #[cfg(feature = "installed-service-candidate")]
    pub(crate) fn revision(&self) -> i64 {
        self.revision
    }

    // The managed status contract requires profiles and resolved intent even
    // before configuration or device admission. These are safe defaults,
    // never a firmware read or the active user's saved preferences.
    #[cfg(feature = "installed-service-candidate")]
    pub(crate) fn observed_read_only(
        revision: i64,
        control_access: i32,
        on_battery: bool,
        unsupported_laptop: bool,
        potential_controllers: Vec<String>,
        controller_detection_warning: Option<&str>,
        message: String,
    ) -> Self {
        use serde_json::json;

        const BALANCED: i32 = 1;
        const BATTERY_SAVER: i32 = 3;
        const AUTOMATIC: i32 = 0;
        const POWER_PROFILE: i32 = 1;
        const EXTERNAL_UNKNOWN: i32 = 3;
        const BATTERY: i32 = 1;
        const DEFAULT_CHARGE_LIMIT: i32 = 80;

        let lighting = Map::from_iter([
            ("effect".into(), json!(0)),
            ("color".into(), json!({"red": 0, "green": 0, "blue": 0})),
            ("brightness".into(), json!(0)),
            ("parameters".into(), json!({"kind": "off"})),
        ]);
        let profile = |performance, custom_performance| {
            let mut feature_fields = Map::from_iter([
                (
                    "display".into(),
                    json!({
                        "refreshRateKey": null, "hdrEnabled": null, "colorProfileKey": null
                    }),
                ),
                (
                    "lightingPowerPolicy".into(),
                    json!({
                        "turnOffWhenDisplayTurnsOff": false, "idleTimeoutMinutes": null
                    }),
                ),
                ("batteryProtectionEnabled".into(), json!(true)),
            ]);
            if custom_performance {
                feature_fields.insert("customPerformance".into(), json!({"cpu": 1, "gpu": 2}));
            }
            PowerProfile {
                performance,
                fans: AUTOMATIC,
                lighting: lighting.clone(),
                feature_fields,
            }
        };
        let profiles = Profiles {
            plugged_in: profile(BALANCED, true),
            battery: profile(BATTERY_SAVER, false),
            usb_c: profile(BALANCED, false),
            battery_policy: BatteryPolicy {
                charge_limit_percent: DEFAULT_CHARGE_LIMIT,
            },
            feature_fields: Map::from_iter([(
                "windowsPowerModes".into(),
                json!({
                    "silent": null, "balanced": null, "performance": null,
                    "batterySaver": null, "batteryBalanced": null, "custom": null,
                    "turbo": null, "maximumPower": null, "gaming": null, "creator": null
                }),
            )]),
        };
        let desired_state = DesiredState {
            performance: Resolved {
                value: if on_battery { BATTERY_SAVER } else { BALANCED },
                source: POWER_PROFILE,
            },
            fans: Resolved {
                value: AUTOMATIC,
                source: POWER_PROFILE,
            },
            lighting: Resolved {
                value: lighting,
                source: POWER_PROFILE,
            },
            battery: DesiredBattery {
                configured_policy: BatteryPolicy {
                    charge_limit_percent: DEFAULT_CHARGE_LIMIT,
                },
                protection_enabled: true,
                temporary_full_charge: false,
            },
            feature_fields: Map::new(),
        };

        Self {
            read_confirmation: None,
            private_user_sid: None,
            diagnostics: Diagnostics {
                public: message.clone(),
                ..Diagnostics::default()
            },
            revision,
            device_verified: false,
            writes_blocked: true,
            potential_controllers,
            controller_detection_warning: controller_detection_warning.map(str::to_owned),
            external_changes: Vec::new(),
            message,
            capabilities: None,
            confirmed_state: None,
            profiles,
            desired_state,
            // v80 has no fully unknown power source. Use its conservative
            // unresolved encoding for both unknown and unclassified external.
            active_power_source: if on_battery {
                BATTERY
            } else {
                EXTERNAL_UNKNOWN
            },
            live_readings: None,
            performance_override: None,
            fan_override: None,
            lighting_override: None,
            full_charge_override: false,
            control_access,
            service_version: env!("CARGO_PKG_VERSION").into(),
            feature_fields: Map::from_iter([(
                "unsupportedLaptop".into(),
                json!(unsupported_laptop),
            )]),
        }
    }
    #[cfg(all(windows, feature = "installed-service-candidate"))]
    #[allow(dead_code)] // Shared with the Agent, which only decodes the reply.
    pub(crate) fn with_standard_performance(mut self, preset: i32) -> Option<Self> {
        use serde_json::json;

        // The full-adapter admission must agree with the independently
        // supplied status. Only the two decoded selectors are confirmed here.
        if self.active_power_source != 0 || !matches!(preset, 1 | 2) {
            return None;
        }
        self.confirmed_state = Some(Map::from_iter([
            ("performance".into(), json!(preset)),
            ("fans".into(), json!(0)),
            ("battery".into(), Value::Null),
            ("lighting".into(), Value::Null),
            ("hyperBoostActive".into(), Value::Null),
        ]));
        self.confirmed_performance()?;
        Some(self)
    }

    #[cfg(all(windows, feature = "service-mailbox-observation-v1"))]
    pub(crate) fn with_mailbox_read(
        mut self,
        observation: openblade_domain::thermal_observation::ObservedPerformance,
        confirmation: ReadConfirmation,
    ) -> Self {
        // The shared Agent status module does not own Service discovery types.
        let (performance, fans, hyper_boost) = observation.projection();
        self.confirmed_state = Some(Map::from_iter([
            ("performance".into(), serde_json::json!(performance)),
            ("fans".into(), serde_json::json!(fans)),
            ("battery".into(), Value::Null),
            ("lighting".into(), Value::Null),
            ("hyperBoostActive".into(), serde_json::json!(hyper_boost)),
            ("customPerformance".into(), observation.custom_levels().map_or(Value::Null, |levels| {
                serde_json::json!({ "cpu": levels.cpu as u8, "gpu": levels.gpu as u8, "maximumFans": false })
            })),
        ]));
        self.device_verified = true; // Real opened exact host/HID validation preceded this result.
        self.writes_blocked = true;
        self.capabilities = None;
        self.set_public_message("Thermal settings read. Native controls remain unavailable.");
        self.read_confirmation = Some(confirmation);
        self
    }

    fn render_message(&mut self) {
        let d = &self.diagnostics;
        let mut parts = vec![d.public.as_str()];
        if d.caller_denied {
            parts.push(
                "Public hardware observation; private configuration is unavailable to this caller.",
            );
        }
        if let Some(private) = &d.private {
            parts.push(private);
        }
        if d.private_unavailable.into_iter().any(|failed| failed) {
            parts.push("Saved private policy is unavailable; writes are blocked pending configuration reconciliation.");
        }
        if let Some(battery) = &d.battery {
            if !battery.public.is_empty() {
                parts.push(&battery.public);
            }
            if let Some(request) = &battery.request {
                if !d.caller_denied
                    && self.private_user_sid.as_deref() == Some(request.sid.as_str())
                {
                    // Ownership is checked first. Only the two already-classified
                    // battery contributions share this exact duplicate suppression.
                    if request.message != battery.public {
                        parts.push(&request.message);
                    }
                }
            }
        }
        self.message = parts
            .into_iter()
            .filter(|s| !s.is_empty())
            .collect::<Vec<_>>()
            .join(" ");
    }
    fn set_public_message(&mut self, message: &str) {
        self.diagnostics.public = message.into();
        self.render_message();
    }
    fn append_public_message(&mut self, message: &str) {
        self.diagnostics.public.push_str(message);
        self.render_message();
    }
    #[cfg(feature = "service-thermal-mutation-v1")]
    fn set_private_message(&mut self, message: &str) {
        self.diagnostics.private = Some(message.into());
        self.render_message();
    }
    pub(crate) fn has_fresh_mailbox_read(&self) -> bool {
        self.read_confirmation
            .as_ref()
            .is_some_and(ReadConfirmation::valid)
    }
    #[cfg(feature = "installed-service-candidate")]
    pub(crate) fn retained_mailbox(&self, thermal: &Self, revision: i64) -> Self {
        let mut retained = self.clone();
        retained.read_confirmation = None;
        retained.confirmed_state = thermal.confirmed_state.clone();
        retained.writes_blocked = true;
        retained.capabilities = None;
        retained.revision = revision;
        retained.append_public_message(
            " Last confirmed thermal settings retained; no fresh read is confirmed.",
        );
        retained
    }
    #[cfg(feature = "installed-service-candidate")]
    pub(crate) fn unavailable_mailbox(&self, revision: i64, reason: &str) -> Self {
        let mut unavailable = self.clone();
        unavailable.read_confirmation = None;
        unavailable.confirmed_state = None;
        unavailable.writes_blocked = true;
        unavailable.capabilities = None;
        unavailable.revision = revision;
        unavailable.append_public_message(reason);
        unavailable
    }

    #[cfg(feature = "installed-service-candidate")]
    pub(crate) fn metadata_only(&self) -> bool {
        self.confirmed_state.is_none()
    }

    #[cfg(feature = "installed-service-candidate")]
    pub(crate) fn read_revoked(mut self) -> Self {
        self.append_public_message(
            " Thermal read permission is revoked; diagnostic status remains available.",
        );
        self
    }

    #[cfg(feature = "installed-service-candidate")]
    pub(crate) fn has_same_thermal(&self, other: &Self) -> bool {
        self.confirmed_state == other.confirmed_state
    }
    #[cfg(feature = "installed-service-candidate")]
    pub(crate) fn set_observation_revision(&mut self, revision: i64) {
        self.revision = revision;
    }

    // Use only for a direct, admitted ObserveState reply. v80 can retain
    // fields during failed observations. Require the managed completion
    // guards and explicit absence of observation or write uncertainty.
    /// Managed PowerSource wire value, already bounded to 0..=3 by `verify`.
    #[cfg(openblade_display_write)]
    pub(crate) fn active_power_source(&self) -> i32 {
        self.active_power_source
    }
    pub(crate) fn confirmed_performance(&self) -> Option<ConfirmedPerformance> {
        let observing = self.feature_fields.get("observationReason")?.as_i64()? != 0;
        let unconfirmed = self.feature_fields.get("unconfirmedWrites")?.as_array()?;
        let fan_stop = self
            .feature_fields
            .get("fanStopStatus")?
            .get("state")?
            .as_i64()?;
        if !self.device_verified
            || self.writes_blocked
            || self.control_access != 0
            || self.active_power_source == 3 // ExternalUnknown
            || observing
            // Managed ObserveAsync can skip the read while the guardian owns
            // hardware. Only Unavailable, Inactive or Automatic can confirm.
            || !matches!(fan_stop, 0 | 1 | 3)
            || unconfirmed
                .iter()
                .any(|subsystem| !matches!(subsystem.as_i64(), Some(1..=3)))
            || unconfirmed
                .windows(2)
                .any(|pair| pair[0].as_i64() >= pair[1].as_i64())
        {
            return None;
        }
        let preset = self
            .confirmed_state
            .as_ref()?
            .get("performance")?
            .as_i64()?;
        (0..=9).contains(&preset).then_some(ConfirmedPerformance {
            preset: preset as i32,
            active_power_source: self.active_power_source,
        })
    }

    pub(crate) fn verify(&self, access: i32) -> io::Result<()> {
        if self.revision < 0
            || self.service_version.trim().is_empty()
            || self.service_version.trim() != self.service_version
            || self.service_version.encode_utf16().count() > 64
            || !(0..=3).contains(&self.active_power_source)
            || !(0..=2).contains(&self.control_access)
            || self.control_access != access
            || self
                .external_changes
                .iter()
                .any(|value| !(0..=3).contains(value))
        {
            return Err(invalid("invalid v80 status snapshot"));
        }
        Ok(())
    }
}

#[derive(Clone, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
struct Profiles {
    plugged_in: PowerProfile,
    battery: PowerProfile,
    usb_c: PowerProfile,
    #[serde(deserialize_with = "object")]
    battery_policy: BatteryPolicy,
    #[serde(flatten)]
    feature_fields: FeatureRecord,
}

#[derive(Clone, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
struct PowerProfile {
    performance: i32,
    fans: i32,
    lighting: FeatureRecord,
    #[serde(flatten)]
    feature_fields: FeatureRecord,
}

#[derive(Clone, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
struct BatteryPolicy {
    charge_limit_percent: i32,
}

#[derive(Clone, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
struct DesiredState {
    #[serde(deserialize_with = "object")]
    performance: Resolved<i32>,
    #[serde(deserialize_with = "object")]
    fans: Resolved<i32>,
    #[serde(deserialize_with = "object")]
    lighting: Resolved<FeatureRecord>,
    #[serde(deserialize_with = "object")]
    battery: DesiredBattery,
    #[serde(flatten)]
    feature_fields: FeatureRecord,
}

#[derive(Clone, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
struct Resolved<T> {
    value: T,
    source: i32,
}

#[derive(Clone, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
struct DesiredBattery {
    #[serde(deserialize_with = "object")]
    configured_policy: BatteryPolicy,
    protection_enabled: bool,
    temporary_full_charge: bool,
}

#[derive(Clone, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
struct LiveReadings {
    battery_power_flow_milliwatts: Option<i32>,
    fan1_rpm: Option<i32>,
    fan2_rpm: Option<i32>,
    cpu_temperature_celsius: Option<i32>,
    gpu_temperature_celsius: Option<i32>,
}

// Verify the same connection before submitting a request and before releasing
// the snapshot. A valid response does not authenticate its sender.
pub(crate) async fn exchange<S: AsyncRead + AsyncWrite + Unpin + ?Sized>(
    stream: &mut S,
    request: &StatusRequest,
    expected_access: Option<i32>,
    mut verify_peer: impl FnMut(&S) -> io::Result<()>,
) -> io::Result<Snapshot> {
    verify_peer(stream)?;
    write_frame(stream, request).await?;
    let reply = timeout(FRAME_TIMEOUT, read_payload(stream)).await??;
    let status = StatusResponse::decode(&reply, request, expected_access);
    if status.is_ok() || request.is_subscription() || request.is_observation() {
        verify_peer(stream)?;
    }
    status
}

#[allow(unsafe_code)]
pub(crate) fn request_id() -> io::Result<String> {
    use windows_sys::{Win32::System::Com::CoCreateGuid, core::GUID};

    let mut id = GUID::from_u128(0);
    // SAFETY: CoCreateGuid writes into an owned GUID-sized allocation.
    if unsafe { CoCreateGuid(&mut id) } < 0 {
        return Err(invalid("GetStatus request ID generation failed"));
    }
    let [a, b, c, d, e, f, g, h] = id.data4;
    Ok(format!(
        "{:08x}-{:04x}-{:04x}-{a:02x}{b:02x}-{c:02x}{d:02x}{e:02x}{f:02x}{g:02x}{h:02x}",
        id.data1, id.data2, id.data3
    ))
}

pub(crate) use openblade_domain::thermal_history::{PerformanceOverride, validate_gpu_tuning};

#[cfg(feature = "service-thermal-mutation-v1")]
impl Snapshot {
    pub(crate) fn battery_projection(
        mut self,
        desired: Option<openblade_domain::battery_history::Desired>,
        observation: Option<(u8, Option<bool>)>,
        message: &str,
    ) -> Self {
        // The existing IPC80 DesiredBatteryState cannot represent Off + TFC.
        // Keep that private intent internally and publish the established
        // unavailable projection; never turn it into firmware-confirmed false.
        let incompatible = desired.is_some_and(|d| d.temporary && !d.protection);
        if let Some(d) = desired.filter(|_| !incompatible) {
            self.desired_state
                .battery
                .configured_policy
                .charge_limit_percent = d.limit as i32;
            self.desired_state.battery.protection_enabled = d.protection;
            self.desired_state.battery.temporary_full_charge = d.temporary;
            self.full_charge_override = d.temporary;
        }
        if let Some((raw, temporary)) = observation {
            // Managed ConfirmedDeviceState admits nullable thermal members.
            // Preserve unrelated observations and unknowns without manufacturing
            // a thermal result to carry a confirmed battery reading.
            if !(raw == 0x50 && temporary == Some(true)) {
                let state = self.confirmed_state.get_or_insert_with(|| {
                    Map::from_iter([
                        ("performance".into(), Value::Null),
                        ("fans".into(), Value::Null),
                        ("lighting".into(), Value::Null),
                        ("hyperBoostActive".into(), Value::Null),
                    ])
                });
                state.insert("battery".into(),serde_json::json!({"protectionEnabled":raw!=0x50,"chargeLimitPercent":if raw==0x50{None}else{Some(raw&0x7f)},"temporaryFullCharge":temporary}));
            }
        }
        if !message.is_empty() {
            // Public observation replaces this family, including any previous
            // request outcome. Desired-only projection keeps current provenance.
            self.diagnostics.battery = Some(BatteryDiagnostic {
                public: message.into(),
                request: None,
            });
            self.render_message();
        }
        self.writes_blocked = true;
        if incompatible {
            self.private_policy_unavailable(PrivatePolicyFamily::Battery)
        } else {
            self
        }
    }
    pub(crate) fn without_battery_observation(mut self) -> Self {
        if let Some(state) = &mut self.confirmed_state {
            state.insert("battery".into(), Value::Null);
        }
        self
    }
    pub(crate) fn battery_public_projection(
        self,
        observation: Option<(u8, Option<bool>)>,
        message: &str,
    ) -> Self {
        // Bookkeeping changes only the public contribution. Preserve the exact
        // current private diagnostic and its SID rather than re-owning it.
        let request = self
            .diagnostics
            .battery
            .as_ref()
            .and_then(|d| d.request.clone());
        let mut projected = self.battery_projection(None, observation, message);
        if let Some(diagnostic) = &mut projected.diagnostics.battery {
            diagnostic.request = request;
        }
        projected.render_message();
        projected
    }
    pub(crate) fn battery_request_outcome(mut self, sid: &str, message: &str) -> Self {
        if !message.is_empty() {
            self.diagnostics
                .battery
                .get_or_insert_with(BatteryDiagnostic::default)
                .request = Some(OwnedDiagnostic {
                sid: sid.into(),
                message: message.into(),
            });
        }
        self.render_message();
        self
    }
    pub(crate) fn bind_private_user(mut self, sid: &str) -> Self {
        // An independently owned request diagnostic must never become the next
        // private-data owner's text, or revive after a later binding back.
        if let Some(battery) = &mut self.diagnostics.battery {
            if battery
                .request
                .as_ref()
                .is_some_and(|request| request.sid != sid)
            {
                battery.request = None;
            }
        }
        self.private_user_sid = Some(sid.to_owned());
        self.diagnostics.caller_denied = false;
        self.render_message();
        self
    }
    pub(crate) fn project_private(&self, sid: Option<&str>) -> Self {
        if self
            .private_user_sid
            .as_deref()
            .is_some_and(|owner| sid != Some(owner))
        {
            self.redact_private()
        } else {
            self.clone()
        }
    }
    pub(crate) fn redact_private(&self) -> Self {
        let mut result = self.without_private_policy();
        result.diagnostics.caller_denied = true;
        result.render_message();
        result
    }
    // Internal reconstruction is not evidence of a foreign caller. Keep an
    // actual existing caller-denial reason until an authoritative owner binds.
    pub(crate) fn without_private_policy(&self) -> Self {
        let mut result = self.clone();
        let public = Self::observed_read_only(
            self.revision,
            self.control_access,
            self.active_power_source == 1,
            false,
            vec![],
            None,
            String::new(),
        );
        result.private_user_sid = None;
        result.profiles = public.profiles;
        result.desired_state = public.desired_state;
        result.performance_override = None;
        result.fan_override = None;
        result.feature_fields.remove("fanControlOverride");
        result.lighting_override = None;
        result.full_charge_override = false;
        result.writes_blocked = true;
        result.diagnostics.private = None;
        if let Some(battery) = &mut result.diagnostics.battery {
            battery.request = None;
        }
        result.render_message();
        result
    }
    pub(crate) fn fan_intent(
        mut self,
        value: Option<&FanOverride>,
        settings: Option<Value>,
        power: i32,
        message: &str,
    ) -> Self {
        self.set_private_message(message);
        self.active_power_source = power;
        self.fan_override = value
            .and_then(|v| serde_json::to_value(v).ok())
            .and_then(|v| v.as_object().cloned());
        if let Some(settings) = settings {
            self.desired_state.fans = Resolved {
                value: settings["mode"].as_i64().unwrap_or(0) as i32,
                source: value.map_or(1, |v| if v.lifetime == 1 { 4 } else { 3 }),
            };
            self.feature_fields.insert(
                "fanControlOverride".into(),
                if value.is_some() {
                    settings.clone()
                } else {
                    Value::Null
                },
            );
            self.desired_state
                .feature_fields
                .insert("fanControl".into(), settings);
        }
        self
    }
    pub(crate) fn thermal_intent(
        mut self,
        value: Option<&PerformanceOverride>,
        destination: Option<openblade_domain::thermal_mutation::Preset>,
        message: &str,
        power: Option<i32>,
    ) -> Self {
        self.set_private_message(message);
        self.performance_override = value
            .and_then(|value| serde_json::to_value(value).ok())
            .and_then(|v| v.as_object().cloned());
        if let Some(power) = power {
            self.active_power_source = power;
        }
        if let Some(selected) = destination.map(|d| d as i32) {
            self.desired_state.performance = Resolved {
                value: selected,
                source: value.map_or(1, |v| if v.lifetime == 1 { 4 } else { 3 }),
            };
        }
        self
    }
    pub(crate) fn validate_thermal_profiles(profiles: &Value) -> io::Result<()> {
        serde_json::from_value::<Profiles>(profiles.clone())
            .map(|_| ())
            .map_err(io::Error::other)
    }
    pub(crate) fn thermal_profiles(mut self, profiles: Option<Value>) -> io::Result<Self> {
        if let Some(profiles) = profiles {
            self.profiles = serde_json::from_value(profiles).map_err(io::Error::other)?;
        }
        Ok(self)
    }
    pub(crate) fn thermal_provenance(mut self, source: Option<i32>) -> Self {
        if let Some(source) = source {
            self.desired_state.performance.source = source;
        }
        self
    }
    pub(crate) fn thermal_write_admission(mut self, applied: bool) -> Self {
        self.writes_blocked = !applied || !self.has_fresh_mailbox_read();
        self
    }
    pub(crate) fn private_policy_reconciling(mut self, family: PrivatePolicyFamily) -> Self {
        // Clear only this attempt\'s previous degradation before invoking it.
        // A nested recoverable error can then install its current contribution.
        self.diagnostics.private_unavailable[family as usize] = false;
        self.render_message();
        self
    }
    pub(crate) fn private_policy_unavailable(&self, family: PrivatePolicyFamily) -> Self {
        let mut value = self.without_private_policy();
        value.diagnostics.private_unavailable[family as usize] = true;
        value.render_message();
        value
    }
    pub(crate) fn thermal_power_source(&self) -> i32 {
        self.active_power_source
    }
    /// Only this operation's independent result may attach a new witness.
    pub(crate) fn without_thermal_observation(mut self) -> Self {
        self.read_confirmation = None;
        self.writes_blocked = true;
        self
    }
    /// Commit a mutation delta against the latest public facts, synchronously.
    /// A Stored request never copies or renews another operation's read witness.
    pub(crate) fn rebase_thermal_mutation(&self, latest: &Self) -> Self {
        let mut result = latest.without_private_policy();
        result.profiles = self.profiles.clone();
        result.performance_override = self.performance_override.clone();
        result.fan_override = self.fan_override.clone();
        result.desired_state.fans = self.desired_state.fans.clone();
        if let Some(v) = self.feature_fields.get("fanControlOverride") {
            result
                .feature_fields
                .insert("fanControlOverride".into(), v.clone());
        }
        if let Some(v) = self.desired_state.feature_fields.get("fanControl") {
            result
                .desired_state
                .feature_fields
                .insert("fanControl".into(), v.clone());
        }
        result.desired_state.performance = self.desired_state.performance.clone();
        result.diagnostics.private = self.diagnostics.private.clone();
        // Both accepted fan/performance deltas reaching this shared rebase
        // reconstructed and decoded private profiles before returning a status.
        // That resolves Thermal degradation only; Battery remains independent.
        result.diagnostics.private_unavailable[PrivatePolicyFamily::Thermal as usize] = false;
        result.render_message();
        result.read_confirmation = None;
        result.writes_blocked = true;
        if self.has_fresh_mailbox_read() {
            result.diagnostics.public = self.diagnostics.public.clone();
            result.render_message();
            result.confirmed_state = self.confirmed_state.clone();
            result.read_confirmation = self.read_confirmation.clone();
            result.device_verified = self.device_verified;
            result.writes_blocked = self.writes_blocked;
        }
        if let Some(sid) = self.private_user_sid.as_deref() {
            result = result.bind_private_user(sid);
        }
        result
    }
    pub(crate) fn inactive_mutation(&self) -> Self {
        let mut value = self.redact_private();
        value.control_access = 1;
        value.read_confirmation = None;
        value.writes_blocked = true;
        value
    }
}

#[cfg(test)]
mod object_tests;

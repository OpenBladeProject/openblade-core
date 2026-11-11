//! The managed ServiceMessages v79 shape shared by the installed Service and Agent candidates.
//! Preserve rich feature records verbatim; readiness consumes the core snapshot
//! and never interprets feature data as independent device or write evidence.

use std::io;

use serde::{Deserialize, Serialize};
use serde_json::{Map, Value};

use tokio::{
    io::{AsyncRead, AsyncWrite},
    time::timeout,
};

use crate::service_frame::{FRAME_TIMEOUT, invalid, is_guid, read_payload, write_frame};

pub(crate) const PIPE_NAME: &str = "OpenBlade.Service.v2";
pub(crate) const PROTOCOL_VERSION: i32 = 79;

pub(crate) fn production_path() -> String {
    format!(r"\\.\pipe\{PIPE_NAME}")
}

// LiveReadingSensors is a four-bit mask. The managed legacy default is All.
const ALL_LIVE_READINGS: u8 = 15;

#[derive(Deserialize, Serialize)]
#[serde(
    tag = "operation",
    rename_all = "camelCase",
    rename_all_fields = "camelCase",
    deny_unknown_fields
)]
pub(crate) enum StatusRequest {
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
        let request: Self = serde_json::from_slice(payload)
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

    fn header(&self) -> (i32, &str) {
        match self {
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

    fn validate(&self) -> io::Result<()> {
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
    #[serde(rename = "status")]
    _status: ObserveStatusMembers,
}

#[derive(Deserialize)]
struct ObserveStatusMembers {
    #[serde(rename = "observationReason")]
    _observation_reason: Option<serde::de::IgnoredAny>,
    #[serde(rename = "unconfirmedWrites")]
    _unconfirmed_writes: Option<serde::de::IgnoredAny>,
    #[serde(rename = "confirmedState")]
    _confirmed_state: Option<ObservedPerformanceMember>,
    #[serde(rename = "fanStopStatus")]
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
        let reply: Self = serde_json::from_slice(payload)
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
            serde_json::from_slice::<ObserveReplyMembers>(payload)
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
    #[derive(Deserialize)]
    #[serde(tag = "frame", rename_all = "camelCase", deny_unknown_fields)]
    enum Event {
        StatusEvent { version: i32, status: Snapshot },
    }

    // The producer can go quiet when status is unchanged. Only the handshake
    // has a receive deadline. The owning watcher cancels or drops this stream.
    let payload = read_payload(stream).await?;
    let Event::StatusEvent { version, status } =
        serde_json::from_slice(&payload).map_err(|_| invalid("invalid Service status event"))?;
    if version != PROTOCOL_VERSION {
        return Err(invalid("Service status event version mismatch"));
    }
    status.verify(status.control_access)?;
    Ok(status)
}

type FeatureRecord = Map<String, Value>;

#[derive(Deserialize, Serialize)]
#[cfg_attr(feature = "installed-service-candidate", derive(Clone))]
#[serde(rename_all = "camelCase")]
pub(crate) struct Snapshot {
    #[serde(skip)]
    read_confirmation: Option<ReadConfirmation>,
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
            // v79 has no fully unknown power source. Use its conservative
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
        observed: crate::discovery::performance::ObservedPerformance,
        confirmation: ReadConfirmation,
    ) -> Self {
        let (performance, fans, hyper_boost) = observed.projection();
        self.confirmed_state = Some(Map::from_iter([
            ("performance".into(), serde_json::json!(performance)),
            ("fans".into(), serde_json::json!(fans)),
            ("battery".into(), Value::Null),
            ("lighting".into(), Value::Null),
            ("hyperBoostActive".into(), serde_json::json!(hyper_boost)),
        ]));
        self.device_verified = true; // Real opened exact host/HID validation preceded this result.
        self.writes_blocked = true;
        self.capabilities = None;
        self.message = "Thermal settings read. Native controls remain unavailable.".into();
        self.read_confirmation = Some(confirmation);
        self
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
        retained.device_verified = thermal.device_verified;
        retained.writes_blocked = true;
        retained.capabilities = None;
        retained.revision = revision;
        if retained.control_access == 0 {
            retained.message =
                "Last confirmed thermal settings retained. Request an observation to read again."
                    .into();
        }
        retained
    }
    #[cfg(feature = "installed-service-candidate")]
    pub(crate) fn has_same_thermal(&self, other: &Self) -> bool {
        self.confirmed_state == other.confirmed_state
    }
    #[cfg(feature = "installed-service-candidate")]
    pub(crate) fn set_observation_revision(&mut self, revision: i64) {
        self.revision = revision;
    }

    // Use only for a direct, admitted ObserveState reply. v79 can retain
    // fields during failed observations. Require the managed completion
    // guards and explicit absence of observation or write uncertainty.
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
            return Err(invalid("invalid v79 status snapshot"));
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
    performance: Resolved<i32>,
    fans: Resolved<i32>,
    lighting: Resolved<FeatureRecord>,
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

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

#[derive(Deserialize, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
enum Operation {
    GetStatus,
}

#[derive(Deserialize, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub(crate) struct GetStatus {
    operation: Operation,
    version: i32,
    request_id: String,
}

impl GetStatus {
    pub(crate) fn new(request_id: String) -> io::Result<Self> {
        let request = Self {
            operation: Operation::GetStatus,
            version: PROTOCOL_VERSION,
            request_id,
        };
        request.validate()?;
        Ok(request)
    }

    #[cfg(feature = "installed-service-candidate")]
    pub(crate) fn decode(payload: &[u8]) -> io::Result<Self> {
        let request: Self =
            serde_json::from_slice(payload).map_err(|_| invalid("invalid GetStatus request"))?;
        request.validate()?;
        Ok(request)
    }

    fn validate(&self) -> io::Result<()> {
        if self.version != PROTOCOL_VERSION || !is_guid(&self.request_id) {
            return Err(invalid("invalid GetStatus version or request ID"));
        }
        Ok(())
    }

    #[cfg(feature = "installed-service-candidate")]
    pub(crate) fn matches(&self, other: &Self) -> bool {
        self.request_id.eq_ignore_ascii_case(&other.request_id)
    }
}

#[derive(Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
enum Frame {
    Response,
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
    pub(crate) fn success(request: &GetStatus, status: Snapshot) -> Self {
        Self {
            frame: Frame::Response,
            version: PROTOCOL_VERSION,
            request_id: request.request_id.to_ascii_lowercase(),
            success: true,
            error: None,
            status: Some(status),
            result: None,
            archive: None,
            error_code: None,
        }
    }

    #[cfg(all(test, feature = "installed-service-candidate"))]
    pub(crate) fn verify(payload: &[u8], request: &GetStatus) -> io::Result<()> {
        Self::decode(payload, request, Some(0)).map(drop)
    }

    fn decode(
        payload: &[u8],
        request: &GetStatus,
        expected_access: Option<i32>,
    ) -> io::Result<Snapshot> {
        let reply: Self =
            serde_json::from_slice(payload).map_err(|_| invalid("invalid GetStatus response"))?;
        if reply.version != PROTOCOL_VERSION
            || !reply.request_id.eq_ignore_ascii_case(&request.request_id)
            || !reply.success
            || reply.error.is_some()
            || reply.result.is_some()
            || reply.archive.is_some()
            || reply.error_code.is_some()
        {
            return Err(invalid("uncorrelated or unsuccessful GetStatus response"));
        }
        let status = reply
            .status
            .ok_or_else(|| invalid("GetStatus response has no snapshot"))?;
        status.verify(expected_access.unwrap_or(status.control_access))?;
        Ok(status)
    }
}

type FeatureRecord = Map<String, Value>;

#[derive(Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct Snapshot {
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

impl Snapshot {
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

#[derive(Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
struct Profiles {
    plugged_in: PowerProfile,
    battery: PowerProfile,
    usb_c: PowerProfile,
    battery_policy: BatteryPolicy,
    #[serde(flatten)]
    feature_fields: FeatureRecord,
}

#[derive(Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
struct PowerProfile {
    performance: i32,
    fans: i32,
    lighting: FeatureRecord,
    #[serde(flatten)]
    feature_fields: FeatureRecord,
}

#[derive(Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
struct BatteryPolicy {
    charge_limit_percent: i32,
}

#[derive(Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
struct DesiredState {
    performance: Resolved<i32>,
    fans: Resolved<i32>,
    lighting: Resolved<FeatureRecord>,
    battery: DesiredBattery,
    #[serde(flatten)]
    feature_fields: FeatureRecord,
}

#[derive(Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
struct Resolved<T> {
    value: T,
    source: i32,
}

#[derive(Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
struct DesiredBattery {
    configured_policy: BatteryPolicy,
    protection_enabled: bool,
    temporary_full_charge: bool,
}

#[derive(Deserialize, Serialize)]
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
    request: &GetStatus,
    expected_access: Option<i32>,
    mut verify_peer: impl FnMut(&S) -> io::Result<()>,
) -> io::Result<Snapshot> {
    verify_peer(stream)?;
    write_frame(stream, request).await?;
    let reply = timeout(FRAME_TIMEOUT, read_payload(stream)).await??;
    let status = StatusResponse::decode(&reply, request, expected_access)?;
    verify_peer(stream)?;
    Ok(status)
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

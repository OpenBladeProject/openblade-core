//! Typed refusal for a well-formed v91 request that names a managed
//! ServiceMessages operation this composition does not serve.
//!
//! Managed `ControlRequestHandler.HandleUserRequestAsync` replies to an
//! operation it does not dispatch with `Failure(requestId, UNSUPPORTED)`: a
//! correlated response with `success: false` and no body. The refusal reads
//! no state, takes no device or storage authority and records nothing, so it
//! is safe for every admitted caller. Unknown operation names, malformed or
//! duplicate-member payloads and other protocol versions are not decoded here
//! and keep closing the connection without a reply.

use std::{
    collections::{BTreeMap, BTreeSet},
    io,
    sync::OnceLock,
};

use openblade_windows_host::json_object;
use serde::Deserialize;
use serde_json::{Map, Value};

use super::{Frame, PROTOCOL_VERSION, StatusRequest, StatusResponse};
use crate::service_frame::{invalid, is_guid};

/// The managed operation inventory. `WindowsReplay_FrozenWireInventoryMatchesManagedRegistry`
/// pins `operationFamilies` to the `ServiceRequest` discriminators, so this
/// list follows the contract instead of a hand-copied copy.
const CONTRACT: &str =
    include_str!("../../../../tests/OpenBlade.Contracts.Tests/WindowsReplay/v1/service.json");

/// The literal text of managed's unsupported-operation fallback.
pub(crate) const UNSUPPORTED: &str = "IPC request operation is unsupported.";

/// A correlated request for a known managed operation that this build does
/// not dispatch. Only the header is decoded; the body is never interpreted.
#[derive(Debug)]
pub(crate) struct UnservedRequest {
    request_id: String,
}

impl UnservedRequest {
    pub(crate) fn decode(payload: &[u8]) -> io::Result<Self> {
        let refused = || invalid("invalid Service request");
        openblade_domain::thermal_history::validate_json_members(payload)
            .map_err(|_| invalid("duplicate Service request member"))?;
        let mut members: Map<String, Value> =
            json_object::from_slice(payload).map_err(|_| refused())?;
        match members.remove("operation") {
            Some(Value::String(name))
                if managed_operations().contains(&name) && !StatusRequest::serves(&name) => {}
            _ => return Err(refused()),
        }
        // An exact Int32 `PROTOCOL_VERSION`, as the served decoder reads it:
        // its float form, a string or an out-of-range integer is not this
        // protocol.
        let version = match members.remove("version") {
            Some(Value::Number(number)) => number.as_i64(),
            _ => None,
        };
        let request_id = match members.remove("requestId") {
            Some(Value::String(id)) if is_guid(&id) => id,
            _ => return Err(refused()),
        };
        if version != Some(i64::from(PROTOCOL_VERSION)) {
            return Err(invalid("invalid Service status version or request ID"));
        }
        Ok(Self { request_id })
    }
}

impl StatusResponse {
    pub(crate) fn unsupported(request: &UnservedRequest) -> Self {
        Self {
            frame: Frame::Response,
            version: PROTOCOL_VERSION,
            request_id: request.request_id.to_ascii_lowercase(),
            success: false,
            error: Some(UNSUPPORTED.to_owned()),
            status: None,
            result: None,
            archive: None,
            error_code: None,
        }
    }
}

/// Every managed operation discriminator. A contract that cannot be read, has
/// another protocol version or repeats a name yields no operations, so every
/// request then keeps the fail-closed decode.
pub(crate) fn managed_operations() -> &'static BTreeSet<String> {
    static OPERATIONS: OnceLock<BTreeSet<String>> = OnceLock::new();
    OPERATIONS.get_or_init(|| parse(CONTRACT).unwrap_or_default())
}

fn parse(contract: &str) -> Option<BTreeSet<String>> {
    #[derive(Deserialize)]
    #[serde(rename_all = "camelCase")]
    struct Contract {
        protocol: Protocol,
        operation_families: BTreeMap<String, Vec<String>>,
    }
    #[derive(Deserialize)]
    struct Protocol {
        version: i32,
    }

    let contract: Contract = serde_json::from_str(contract).ok()?;
    if contract.protocol.version != PROTOCOL_VERSION {
        return None;
    }
    let mut operations = BTreeSet::new();
    for operation in contract.operation_families.into_values().flatten() {
        if operation.is_empty() || !operations.insert(operation) {
            return None;
        }
    }
    Some(operations)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::service_status::ProfilesBody;
    use serde_json::json;

    const ID: &str = "AAAAAAAA-bbbb-4ccc-8ddd-000000000001";

    fn frame(value: &Value) -> Vec<u8> {
        serde_json::to_vec(value).unwrap()
    }

    // One valid request per variant: each decodes wherever it is compiled.
    fn variants() -> [StatusRequest; 24] {
        let id = || ID.to_owned();
        let version = PROTOCOL_VERSION;
        [
            StatusRequest::SetBatteryPolicy {
                version,
                request_id: id(),
                value: openblade_domain::battery_history::Policy {
                    charge_limit_percent: 80,
                },
            },
            StatusRequest::SetBatteryProtection {
                version,
                request_id: id(),
                enabled: true,
            },
            StatusRequest::SetFullChargeOverride {
                version,
                request_id: id(),
                enabled: true,
            },
            StatusRequest::SetFanOverride {
                version,
                request_id: id(),
                value: None,
                settings: None,
            },
            StatusRequest::SetPerformanceOverride {
                version,
                request_id: id(),
                value: None,
            },
            StatusRequest::ReportApplicationContext {
                version,
                request_id: id(),
                instance_id: id(),
                sequence: 1,
                executable_path: None,
            },
            StatusRequest::GetStatus {
                version,
                request_id: id(),
            },
            StatusRequest::SubscribeStatus {
                version,
                request_id: id(),
            },
            StatusRequest::ObserveState {
                include_live_readings: false,
                requested_live_readings: 0,
                effective_live_readings: Some(0),
                version,
                request_id: id(),
            },
            StatusRequest::UpdateProfiles {
                version,
                request_id: id(),
                profiles: ProfilesBody::sample(),
            },
            StatusRequest::SetThermalControlPolicy {
                version,
                request_id: id(),
                policy: 1,
            },
            StatusRequest::ExportDiagnostics {
                version,
                request_id: id(),
                live_readings_enabled: true,
            },
            StatusRequest::AcknowledgeThermalPolicyUpgradeDisclosure {
                version,
                request_id: id(),
            },
            StatusRequest::ReapplyChangedSettings {
                version,
                request_id: id(),
            },
            StatusRequest::SetFanStopMode {
                version,
                request_id: id(),
                mode: 1,
            },
            StatusRequest::SetFanStopSettings {
                version,
                request_id: id(),
                settings: serde_json::json!({}),
            },
            StatusRequest::ResumeAutomaticFanStop {
                version,
                request_id: id(),
            },
            StatusRequest::SetBatterySaverFanStop {
                version,
                request_id: id(),
                enabled: false,
            },
            StatusRequest::CyclePerformanceShortcut {
                version,
                request_id: id(),
                source: 1,
            },
            StatusRequest::SetPerformanceShortcutPreview {
                version,
                request_id: id(),
                session_id: id(),
                generation: 1,
                preset: None,
            },
            StatusRequest::SetHyperBoost {
                version,
                request_id: id(),
                enabled: true,
            },
            StatusRequest::RecoverHyperBoostRestore {
                version,
                request_id: id(),
            },
            StatusRequest::SetLightingOverride {
                version,
                request_id: id(),
                value: crate::service_status::LightingBody::cleared(),
            },
            StatusRequest::SetDisplayBrightnessHidUsage {
                version,
                request_id: id(),
                usage: 0x6F,
            },
        ]
    }

    #[test]
    fn served_operation_list_is_each_variant_discriminator() {
        let tags: BTreeSet<_> = variants()
            .iter()
            .map(|variant| {
                let tag = serde_json::to_value(variant).unwrap()["operation"].clone();
                assert_eq!(tag, variant.operation());
                variant.operation()
            })
            .collect();
        assert_eq!(tags, StatusRequest::OPERATIONS.into_iter().collect());
    }

    // The compiled feature table in `serves` matches what the typed decoder
    // admits in this lane, for every variant.
    #[test]
    fn served_operations_are_exactly_those_the_typed_decoder_admits() {
        for variant in variants() {
            let operation = variant.operation();
            let payload = serde_json::to_vec(&variant).unwrap();
            let decoded = StatusRequest::decode(&payload).map(|_| ());
            assert_eq!(
                decoded.is_ok(),
                StatusRequest::serves(operation),
                "{operation}: {decoded:?}"
            );
            // A served variant is never also taken as unserved.
            assert_eq!(
                UnservedRequest::decode(&payload).is_ok(),
                !StatusRequest::serves(operation),
                "{operation}"
            );
        }
    }

    #[test]
    fn contract_lists_every_managed_operation_once() {
        let operations = managed_operations();
        assert_eq!(operations.len(), 53);
        assert!(operations.contains("reportSessionPowerTransition"));
        assert!(operations.contains("updateProfiles"));
        // Main's protocol 87 and 88 operations, which no native composition serves.
        assert!(operations.contains("reportWindowsDynamicLightingAdvisory"));
        assert!(operations.contains("clearLocalDimming"));
        for served in StatusRequest::OPERATIONS {
            assert!(operations.contains(served), "{served}");
        }
        let previous = format!(
            r#"{{"protocol":{{"version":{}}},"operationFamilies":{{"a":["x"]}}}}"#,
            PROTOCOL_VERSION - 1
        );
        for broken in [
            previous.as_str(),
            r#"{"protocol":{"version":91},"operationFamilies":{"a":["x"],"b":["x"]}}"#,
            r#"{"protocol":{"version":91},"operationFamilies":{"a":[""]}}"#,
            r#"{"protocol":{"version":91}}"#,
            "[]",
        ] {
            assert_eq!(parse(broken), None, "{broken}");
        }
    }

    #[test]
    fn every_unserved_operation_decodes_its_header_and_any_body() {
        let operations: Vec<_> = managed_operations()
            .iter()
            .filter(|operation| !StatusRequest::serves(operation))
            .collect();
        assert!(operations.len() >= 53 - StatusRequest::OPERATIONS.len());
        for operation in operations {
            for body in [
                json!({}),
                json!({"profiles": {"pluggedIn": null}, "enabled": true}),
            ] {
                let mut request = json!({"operation": operation, "version": 91, "requestId": ID});
                request
                    .as_object_mut()
                    .unwrap()
                    .extend(body.as_object().unwrap().clone());
                let decoded = UnservedRequest::decode(&frame(&request)).unwrap();
                let reply = serde_json::to_value(StatusResponse::unsupported(&decoded)).unwrap();
                assert_eq!(
                    reply,
                    json!({
                        "frame": "response", "version": 91,
                        "requestId": ID.to_ascii_lowercase(), "success": false,
                        "error": UNSUPPORTED, "status": null, "result": null,
                        "archive": null, "errorCode": null,
                    })
                );
            }
        }
    }

    #[test]
    fn unknown_served_malformed_and_other_version_requests_stay_closed() {
        let header = |operation: Value, version: Value, id: Value| {
            frame(&json!({"operation": operation, "version": version, "requestId": id}))
        };
        let mut rejected = vec![
            header(json!("notAManagedOperation"), json!(91), json!(ID)),
            header(json!("UpdateProfiles"), json!(91), json!(ID)),
            header(json!(""), json!(91), json!(ID)),
            header(json!(7), json!(91), json!(ID)),
            header(Value::Null, json!(91), json!(ID)),
            header(json!("updateProfiles"), json!(51), json!(ID)),
            // The neighbouring protocols, then the current one as a string, a
            // float and an Int64 that wraps to it as an Int32.
            header(json!("updateProfiles"), json!(PROTOCOL_VERSION - 1), json!(ID)),
            header(json!("updateProfiles"), json!(PROTOCOL_VERSION + 1), json!(ID)),
            header(json!("updateProfiles"), json!(PROTOCOL_VERSION.to_string()), json!(ID)),
            header(json!("updateProfiles"), json!(f64::from(PROTOCOL_VERSION)), json!(ID)),
            header(
                json!("updateProfiles"),
                json!((1_i64 << 32) + i64::from(PROTOCOL_VERSION)),
                json!(ID),
            ),
            header(json!("updateProfiles"), json!(91), json!("not-a-guid")),
            header(
                json!("updateProfiles"),
                json!(91),
                json!("00000000-0000-0000-0000-000000000000"),
            ),
            header(json!("updateProfiles"), json!(91), json!(1)),
            frame(&json!({"operation": "updateProfiles", "version": 91})),
            frame(&json!({"version": 91, "requestId": ID})),
            frame(&json!(["updateProfiles", 91, ID])),
            frame(&json!("updateProfiles")),
            format!(
                r#"{{"operation":"updateProfiles","operation":"setHyperBoost","version":91,"requestId":"{ID}"}}"#
            )
            .into_bytes(),
            format!(
                r#"{{"operation":"updateProfiles","version":91,"requestId":"{ID}","profiles":{{"a":1,"a":2}}}}"#
            )
            .into_bytes(),
            format!(
                r#"{{"operation":"updateProfiles","version":91,"requestId":"{ID}","$serde_json::private::Number":"1"}}"#
            )
            .into_bytes(),
            b"{\"operation\":\"updateProfiles\"".to_vec(),
        ];
        // A served operation keeps its own typed decoder even when malformed.
        for served in StatusRequest::OPERATIONS
            .into_iter()
            .filter(|served| StatusRequest::serves(served))
        {
            rejected.push(header(json!(served), json!(91), json!(ID)));
        }
        for payload in rejected {
            let error = UnservedRequest::decode(&payload).unwrap_err();
            assert_eq!(
                error.kind(),
                io::ErrorKind::InvalidData,
                "{}",
                String::from_utf8_lossy(&payload)
            );
        }
    }
}

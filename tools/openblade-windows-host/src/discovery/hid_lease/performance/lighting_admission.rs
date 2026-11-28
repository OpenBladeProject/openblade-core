//! Managed `ProductionLightingEvidenceSource.IsProductionLightingImplementationApproved`
//! for the commands this host sends: the reviewed semantic admission manifest,
//! pinned by its approved SHA-256, compared with what the native codec builds,
//! and the hash-pinned Synapse oracle (`ReadValidatedOracle`), on which the
//! 00/86 layout query and the 03/0A handshake rest.
//!
//! Managed compares every section with its current implementation. Native
//! compares the sections whose commands it implements (device, control
//! envelope, the 00/84 query, brightness, logo, the 03/0B matrix with its
//! source layout pin, regional projection fingerprint and known answer, and
//! Off and Static among the supported effects) and refuses every lighting
//! write when any differs or a pin fails. The Driver-mode setters belong to
//! the keyboard Driver-mode owner's own admission; the animated effects and
//! refresh rates to the slice that renders them, under the same pin.
//! Managed validates the oracle only in its tests; native requires it in
//! production too, since no manifest section names 00/86 or 03/0A.
use super::{DEVICE, MODEL, lighting_layout};
use openblade_protocol::{
    RazerReport,
    device_mode::DeviceModeQuery,
    lighting::{
        BrightnessQuery, LightingSet, LogoEffectQuery, LogoMode, LogoStateQuery,
        matrix::{
            self, CANVAS_HOLES, ENVELOPE_LENGTH, FEATURE_REPORT_LENGTH, LayoutQuery, MatrixFrame,
            RegionalProjection, Rgb, SLOT_COUNT, TransitionSet,
        },
        percent_to_raw,
    },
};
use serde_json::{Value, json};
use sha2::{Digest, Sha256};
use std::sync::OnceLock;

/// The managed embedded resource `Rz09_0581_02e0_3_01_LightingSemanticAdmission.json`.
const MANIFEST: &[u8] = include_bytes!(
    "../../../../../../src/OpenBlade.Protocol/Fixtures/Rz09_0581_02e0_3_01_LightingSemanticAdmission.json"
);
/// Managed `ApprovedSemanticAdmissionFixtureSha256`.
const APPROVED_SHA256: &str = "9F70CE8889361D153D50BDAA14E414A71CEDEF54BD3D5FE4B6EE612BE3DEED31";
/// The managed embedded resource `Rz09_0581_02e0_3_01_LightingProduction.json`.
const ORACLE: &[u8] = include_bytes!(
    "../../../../../../src/OpenBlade.Protocol/Fixtures/Rz09_0581_02e0_3_01_LightingProduction.json"
);
/// Managed `ApprovedOracleFixtureSha256`.
const APPROVED_ORACLE_SHA256: &str =
    "5177FA9F6F91D96C9C34C5506CDA8EDC0A86EE5B5EEFA5F4322F4E4633C23521";
/// Managed `ValidateOracle`'s capture pins, in its order.
const ORACLE_CAPTURES: [(&str, &str); 8] = [
    (
        "coreCapture",
        "F05E233135E3A2AB87688EF08C282DA6949F0B32FA9CC0F486ABE823F532D314",
    ),
    (
        "reactiveCapture",
        "BFCB03633CBD1F5644AEC885E8830D9489BA5A7100FFFECA0A4DDAB347E13202",
    ),
    (
        "variantCapture",
        "8CD72FD3F0AB7AF95B21168C96C371B2C56D7410509046371D46305205E193A6",
    ),
    (
        "reactiveTimingCapture",
        "F5A45F5A27B860BFBAE294A692381493619FACBB4E9EB68828B7A459A99ED495",
    ),
    (
        "dualBreathingCapture",
        "4A99DEECC64D7BEDA6813F691CB1C71A0131B21F8EB2FFD9E19EE9F8C21F45D8",
    ),
    (
        "reactiveMainKeyMapCapture",
        "FC28B466691FE7E77A70613E455A18AB673DB2CE62E8ADC9B4DA6808E7B619EB",
    ),
    (
        "reactiveFunctionKeyMapCapture",
        "2B8D6D11EA6BE76F6AA0C9CAED4520CFC1DA0171388423A99FFAC9960F0C6F78",
    ),
    (
        "rippleCapture",
        "5A38B7B453470EFECE9A383F761EEE073290E357801C8F9E9140886A152F17A9",
    ),
];
/// Managed `BuildCurrentSemanticAdmission`'s known answer.
const KNOWN_ANSWER_TRANSACTION: u8 = 0x5a;
const KNOWN_ANSWER_COLOR: Rgb = Rgb {
    red: 0x47,
    green: 0xe1,
    blue: 0x0c,
};

/// Whether the pinned manifest and oracle admit this host's lighting
/// commands, read once per process.
pub(crate) fn approved() -> bool {
    static APPROVED: OnceLock<bool> = OnceLock::new();
    *APPROVED.get_or_init(|| approves(MANIFEST, ORACLE))
}

/// Whether `manifest` and `oracle` both admit the reviewed source maps.
fn approves(manifest: &[u8], oracle: &[u8]) -> bool {
    lighting_layout::source_maps()
        .is_some_and(|maps| admits(manifest, &maps.layout()) && oracle_admits(oracle, manifest))
}

fn sha256(bytes: &[u8]) -> String {
    Sha256::digest(bytes)
        .iter()
        .map(|byte| format!("{byte:02X}"))
        .collect()
}

fn admits(manifest: &[u8], layout: &matrix::KeyboardLayout<'_>) -> bool {
    if sha256(manifest) != APPROVED_SHA256 {
        return false;
    }
    serde_json::from_slice::<Value>(manifest).is_ok_and(|manifest| matches(&manifest, layout))
}

/// `CC/II:payload` as the manifest writes a command.
fn notation(report: &RazerReport) -> String {
    let bytes = report.encode();
    let length = usize::from(bytes[5]);
    let payload: String = bytes[8..8 + length]
        .iter()
        .map(|byte| format!("{byte:02X}"))
        .collect();
    format!("{:02X}/{:02X}:{payload}", bytes[6], bytes[7])
}

/// A setter's notation without its value byte: the manifest's `setPrefix`.
fn prefix(report: &RazerReport) -> String {
    let mut text = notation(report);
    text.truncate(text.len() - 2);
    text
}

/// Managed `Pid02E0RegionalFrameProjection.SemanticFingerprint`: a distinct
/// color per canvas slot, projected for every layout byte as spatial and as
/// per-key content, each encoded with the known-answer transaction.
fn regional_fingerprint(layout: &matrix::KeyboardLayout<'_>) -> String {
    let mut colors = [Rgb::default(); SLOT_COUNT];
    for (slot, color) in colors.iter_mut().enumerate() {
        let slot = slot as u8;
        *color = Rgb {
            red: slot.wrapping_add(1),
            green: 255 - slot,
            blue: slot ^ 0x5a,
        };
    }
    let mut hash = Sha256::new();
    for id in 0..=u8::MAX {
        let projection = RegionalProjection::new(layout, id);
        for kind in 0..2u8 {
            let canvas = MatrixFrame::new(colors, kind == 1);
            hash.update([kind]);
            hash.update(matrix::encode_feature_report(
                KNOWN_ANSWER_TRANSACTION,
                &projection.project(&canvas),
            ));
        }
    }
    hash.finalize()
        .iter()
        .map(|byte| format!("{byte:02X}"))
        .collect()
}

/// Managed `BuildCurrentSemanticAdmission`'s matrix section.
fn matrix_section(layout: &matrix::KeyboardLayout<'_>) -> Value {
    let known = matrix::encode_feature_report(
        KNOWN_ANSWER_TRANSACTION,
        &MatrixFrame::uniform(KNOWN_ANSWER_COLOR),
    );
    json!({
        "command": format!("{:02X}/{:02X}", matrix::MATRIX_CLASS, matrix::MATRIX_COMMAND),
        "envelopeLength": ENVELOPE_LENGTH,
        "featureReportLength": FEATURE_REPORT_LENGTH,
        "slotCount": SLOT_COUNT,
        "holeSlots": CANVAS_HOLES,
        "sourceLayoutSha256": lighting_layout::FIXTURE_SHA256,
        "regionalProjectionSha256": regional_fingerprint(layout),
        "knownAnswerTransactionId": KNOWN_ANSWER_TRANSACTION,
        "knownAnswerColor": format!(
            "{:02X}{:02X}{:02X}",
            KNOWN_ANSWER_COLOR.red, KNOWN_ANSWER_COLOR.green, KNOWN_ANSWER_COLOR.blue
        ),
        "knownAnswerFeatureReportSha256": sha256(&known),
    })
}

/// The exact device both reviewed documents name.
fn device(manifest: &Value) -> Value {
    json!({
        "model": MODEL,
        "vendorId": DEVICE.0,
        "productId": DEVICE.1,
        "biosVersion": manifest["device"]["biosVersion"],
    })
}

/// Managed `SemanticAdmissionMatches` over the implemented sections.
fn matches(manifest: &Value, layout: &matrix::KeyboardLayout<'_>) -> bool {
    const MODES: [LogoMode; 3] = [LogoMode::Static, LogoMode::Breathing, LogoMode::Off];
    let envelope = json!({
        "reportLength": openblade_protocol::REPORT_LENGTH,
        "maximumPayloadLength": openblade_protocol::MAXIMUM_PAYLOAD_LENGTH,
    });
    let brightness = json!({
        "query": notation(BrightnessQuery::new(0).request()),
        "setPrefix": prefix(LightingSet::brightness(0, 0).request()),
        "percent77Raw": percent_to_raw(77),
    });
    let logo = json!({
        "queryState": notation(LogoStateQuery::new(0).request()),
        "queryEffect": notation(LogoEffectQuery::new(0).request()),
        "setStatePrefix": prefix(LightingSet::logo_state(0, LogoMode::Static).request()),
        "setEffectPrefix": prefix(LightingSet::logo_effect(0, LogoMode::Static).request()),
        "supportedModes": ["static", "breathing", "off"],
    });
    // Every mode's setters share the prefixes the manifest names.
    let prefixes = MODES.iter().all(|&mode| {
        prefix(LightingSet::logo_state(0, mode).request()) == logo["setStatePrefix"]
            && prefix(LightingSet::logo_effect(0, mode).request()) == logo["setEffectPrefix"]
    });
    let effects = manifest["supportedEffects"]
        .as_array()
        .is_some_and(|effects| {
            ["off", "static"]
                .iter()
                .all(|effect| effects.contains(&json!(effect)))
        });
    prefixes
        && effects
        && manifest["schemaVersion"] == 1
        && manifest["evidenceKind"] == "semanticLightingAdmission"
        && manifest["device"]["biosVersion"].is_string()
        && manifest["device"] == device(manifest)
        && manifest["controlEnvelope"] == envelope
        && manifest["deviceMode"]["query"] == notation(DeviceModeQuery::new(0).request())
        && manifest["brightness"] == brightness
        && manifest["logo"] == logo
        && manifest["matrix"] == matrix_section(layout)
}

/// Managed `ReadValidatedOracle`: the pin, then `ValidateOracle`, whose
/// commands native compares with what its codec builds. Managed deserializes
/// with unmapped members disallowed, so an added member refuses too.
fn oracle_admits(oracle: &[u8], manifest: &[u8]) -> bool {
    if sha256(oracle) != APPROVED_ORACLE_SHA256 {
        return false;
    }
    match (
        serde_json::from_slice::<Value>(oracle),
        serde_json::from_slice::<Value>(manifest),
    ) {
        (Ok(oracle), Ok(manifest)) => oracle_matches(&oracle, &manifest),
        _ => false,
    }
}

fn oracle_matches(oracle: &Value, manifest: &Value) -> bool {
    let members = ["schemaVersion", "evidenceKind", "device", "protocol"]
        .into_iter()
        .chain(ORACLE_CAPTURES.iter().map(|(name, _)| *name));
    let complete = oracle.as_object().is_some_and(|root| {
        root.len() == 12 && members.clone().all(|name| root.contains_key(name))
    });
    let captures = ORACLE_CAPTURES.iter().all(|(name, sha)| {
        oracle[name] == json!({ "sha256": sha, "captured": true, "decoded": true })
    });
    let protocol = json!({
        "brightnessQuery": notation(BrightnessQuery::new(0).request()),
        "brightnessSetPrefix": prefix(LightingSet::brightness(0, 0).request()),
        "transitionRequests": [
            notation(LayoutQuery::new(0).request()),
            notation(DeviceModeQuery::new(0).request()),
            notation(TransitionSet::new(0).request()),
        ],
        "transitionResponses": ["00/86:010000", "00/84:0300", "03/0A:0500"],
        "matrixCommand": format!("{:02X}/{:02X}", matrix::MATRIX_CLASS, matrix::MATRIX_COMMAND),
        "matrixEnvelopeLength": ENVELOPE_LENGTH,
        "matrixFeatureReportLength": FEATURE_REPORT_LENGTH,
        "matrixSlotCount": SLOT_COUNT,
    });
    complete
        && captures
        && oracle["schemaVersion"] == 3
        && oracle["evidenceKind"] == "synapseOracleCapture"
        && manifest["device"]["biosVersion"].is_string()
        && oracle["device"] == device(manifest)
        && oracle["protocol"] == protocol
}

#[cfg(test)]
mod tests;

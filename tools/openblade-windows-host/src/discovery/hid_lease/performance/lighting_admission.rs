//! Managed `ProductionLightingEvidenceSource.IsProductionLightingImplementationApproved`
//! for the commands this host sends: the reviewed semantic admission manifest,
//! pinned by its approved SHA-256, compared with what the native codec builds.
//!
//! Managed compares every section with its current implementation. Native
//! compares the sections whose commands it implements (device, control
//! envelope, brightness, logo, and Off among the supported effects) and
//! refuses every lighting write when any differs or the pin fails. The
//! Driver-mode section belongs to the keyboard Driver-mode owner's own
//! admission; the matrix section and the lit effects are compared by the
//! slice that sends them, under the same pin.
use super::{DEVICE, MODEL};
use openblade_protocol::{
    RazerReport,
    lighting::{
        BrightnessQuery, LightingSet, LogoEffectQuery, LogoMode, LogoStateQuery, percent_to_raw,
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

/// Whether the pinned manifest admits this host's lighting commands, read
/// once per process.
pub(crate) fn approved() -> bool {
    static APPROVED: OnceLock<bool> = OnceLock::new();
    *APPROVED.get_or_init(|| admits(MANIFEST))
}

fn admits(manifest: &[u8]) -> bool {
    let digest: String = Sha256::digest(manifest)
        .iter()
        .map(|byte| format!("{byte:02X}"))
        .collect();
    if digest != APPROVED_SHA256 {
        return false;
    }
    serde_json::from_slice::<Value>(manifest).is_ok_and(|manifest| matches(&manifest))
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

/// Managed `SemanticAdmissionMatches` over the implemented sections.
fn matches(manifest: &Value) -> bool {
    const MODES: [LogoMode; 3] = [LogoMode::Static, LogoMode::Breathing, LogoMode::Off];
    let device = json!({
        "model": MODEL,
        "vendorId": DEVICE.0,
        "productId": DEVICE.1,
        "biosVersion": manifest["device"]["biosVersion"],
    });
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
    prefixes
        && manifest["schemaVersion"] == 1
        && manifest["evidenceKind"] == "semanticLightingAdmission"
        && manifest["device"]["biosVersion"].is_string()
        && manifest["device"] == device
        && manifest["controlEnvelope"] == envelope
        && manifest["brightness"] == brightness
        && manifest["logo"] == logo
        && manifest["supportedEffects"]
            .as_array()
            .is_some_and(|effects| effects.contains(&json!("off")))
}

#[cfg(test)]
mod tests {
    use super::*;

    /// The pinned manifest admits this codec, as managed's admits its own.
    #[test]
    fn the_pinned_manifest_admits_the_native_commands() {
        assert!(admits(MANIFEST));
        assert!(approved());
    }

    /// Any change to the manifest bytes fails the pin; a section that differs
    /// from the codec fails the comparison even under a matching pin.
    #[test]
    fn a_changed_manifest_or_section_admits_nothing() {
        let mut changed = MANIFEST.to_vec();
        let at = changed.iter().position(|&b| b == b'5').unwrap();
        changed[at] = b'6';
        assert!(!admits(&changed));
        let manifest: Value = serde_json::from_slice(MANIFEST).unwrap();
        for (section, member, value) in [
            ("device", "productId", json!(0x02c6)),
            ("device", "model", json!("RZ09-0528")),
            ("controlEnvelope", "reportLength", json!(91)),
            ("brightness", "query", json!("03/83:010400")),
            ("brightness", "setPrefix", json!("03/03:0104")),
            ("brightness", "percent77Raw", json!(196)),
            ("logo", "queryState", json!("03/80:010500")),
            ("logo", "setEffectPrefix", json!("03/02:0105")),
            ("logo", "supportedModes", json!(["static", "off"])),
        ] {
            let mut edited = manifest.clone();
            edited[section][member] = value;
            assert!(!matches(&edited), "{section}.{member}");
        }
        let mut edited = manifest.clone();
        edited["supportedEffects"] = json!(["static"]);
        assert!(!matches(&edited));
        assert!(matches(&manifest));
    }
}

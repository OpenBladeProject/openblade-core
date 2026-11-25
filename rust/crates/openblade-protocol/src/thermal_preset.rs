//! Exact RZ09-0581 0D/02 action pairs. This codec grants no I/O authority.
use crate::RazerReport;
pub use openblade_types::InstalledThermalPreset;
use openblade_types::ProtocolValidationFailure;

/// The firmware mode byte of the Custom pair (`performance-custom-channel-N`).
pub const CUSTOM_SELECTOR: u8 = 4;
#[derive(Clone, Debug)]
pub struct PresetPair {
    commands: [RazerReport; 2],
    selector: u8,
}
impl PresetPair {
    /// Resolve the entire reviewed pair before entering the first submission.
    pub fn new(ids: [u8; 2], destination: InstalledThermalPreset) -> Option<Self> {
        Self::with_selector(ids, destination.selector())
    }
    /// The Custom mode pair. It selects Custom with Automatic fans; the CPU
    /// and GPU levels are separate setters (`CustomLevelPair`).
    pub fn custom(ids: [u8; 2]) -> Option<Self> {
        Self::with_selector(ids, CUSTOM_SELECTOR)
    }
    /// The HyperBoost restore pair (`hyperboost-restore-balanced-channel-N`):
    /// managed writes it before leaving HyperBoost for a battery preset. Its
    /// bytes are the Balanced pair's; no new firmware command is admitted.
    pub fn hyperboost_restore(ids: [u8; 2]) -> Option<Self> {
        Self::with_selector(ids, InstalledThermalPreset::Balanced.selector())
    }
    fn with_selector(ids: [u8; 2], selector: u8) -> Option<Self> {
        if ids[0] == 0 || ids[1] == 0 || ids[0] == ids[1] {
            return None;
        }
        Some(Self {
            commands: core::array::from_fn(|i| {
                RazerReport::create(ids[i], 0x0d, 0x02, &[1, i as u8 + 1, selector, 0])
                    .expect("fixed reviewed preset payload")
            }),
            selector,
        })
    }
    pub fn commands(&self) -> &[RazerReport; 2] {
        &self.commands
    }
    pub fn acknowledge(&self, index: usize, reply: &[u8]) -> Result<(), ProtocolValidationFailure> {
        let command = self
            .commands
            .get(index)
            .ok_or(ProtocolValidationFailure::PayloadValidator)?;
        let payload = command.validate_response(reply, Some(4))?;
        if payload.as_slice() != [1, index as u8 + 1, self.selector, 0] {
            return Err(ProtocolValidationFailure::PayloadValidator);
        }
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    extern crate std;
    use super::*;
    use std::{format, string::String, vec::Vec};
    const ALL: [InstalledThermalPreset; 5] = [
        InstalledThermalPreset::Silent,
        InstalledThermalPreset::Balanced,
        InstalledThermalPreset::Performance,
        InstalledThermalPreset::BatterySaver,
        InstalledThermalPreset::BatteryBalanced,
    ];
    /// Every pair's class, id and payload are the reviewed managed RZ09-0581
    /// production fixture entries, action for action.
    #[test]
    fn every_pair_is_the_managed_rz09_0581_fixture() {
        let fixture: Vec<Entry> = serde_json::from_str(include_str!(
            "../../../../src/OpenBlade.Protocol/Fixtures/Rz09_0581_02e0_3_01_BatteryProduction.json"
        ))
        .unwrap();
        // ProductionBatteryPort's action names for each destination.
        let names = |preset| match preset {
            InstalledThermalPreset::Silent => "silent",
            InstalledThermalPreset::Balanced => "balanced",
            InstalledThermalPreset::Performance => "performance",
            InstalledThermalPreset::BatterySaver => "battery-saver",
            InstalledThermalPreset::BatteryBalanced => "battery-balanced",
        };
        for destination in ALL {
            let pair = PresetPair::new([19, 20], destination).unwrap();
            for (index, command) in pair.commands().iter().enumerate() {
                let action = format!("performance-{}-channel-{}", names(destination), index + 1);
                let entry = fixture.iter().find(|e| e.action == action).unwrap();
                assert!(entry.captured && entry.applied && entry.read_back && entry.restored);
                let encoded = command.encode();
                assert_eq!(
                    (encoded[6], encoded[7]),
                    (entry.command_class, entry.command_id)
                );
                assert_eq!(encoded[5] as usize, 4);
                assert_eq!(base64(&encoded[8..12]), entry.payload, "{action}");
            }
            assert_eq!(
                InstalledThermalPreset::from_wire(destination as i32),
                Some(destination)
            );
            assert_eq!(
                destination.is_battery(),
                matches!(destination.selector(), 3 | 6)
            );
        }
        // Custom (5) and the unadmitted presets are not preset destinations.
        for value in [-1, 5, 6, 7, 8, 9] {
            assert_eq!(InstalledThermalPreset::from_wire(value), None);
        }
        // The Custom pair and the CPU and GPU level setters, level for level.
        let pair = PresetPair::custom([19, 20]).unwrap();
        for (index, command) in pair.commands().iter().enumerate() {
            let entry = fixture_entry(
                &fixture,
                &format!("performance-custom-channel-{}", index + 1),
            );
            let encoded = command.encode();
            assert_eq!((encoded[6], encoded[7], encoded[5]), (entry.0, entry.1, 4));
            assert_eq!(base64(&encoded[8..12]), entry.2);
        }
        use crate::custom_performance::{CustomLevelPair, CustomPerformanceLevel as L};
        let levels = [(L::Low, "low"), (L::Medium, "medium"), (L::High, "high")];
        for (cpu, cpu_name) in levels {
            for (gpu, gpu_name) in levels {
                let pair = CustomLevelPair::new([21, 22], cpu, gpu).unwrap();
                for (index, name) in [
                    (0, format!("cpu-{cpu_name}")),
                    (1, format!("gpu-{gpu_name}")),
                ] {
                    let entry = fixture_entry(&fixture, &format!("custom-performance-{name}"));
                    let encoded = pair.commands()[index].encode();
                    assert_eq!((encoded[6], encoded[7], encoded[5]), (entry.0, entry.1, 3));
                    assert_eq!(base64(&encoded[8..11]), entry.2, "{name}");
                }
            }
        }
    }
    fn fixture_entry(fixture: &[Entry], action: &str) -> (u8, u8, String) {
        let entry = fixture.iter().find(|e| e.action == action).unwrap();
        assert!(entry.captured && entry.applied && entry.read_back && entry.restored);
        (entry.command_class, entry.command_id, entry.payload.clone())
    }
    #[derive(serde::Deserialize)]
    #[serde(rename_all = "camelCase")]
    struct Entry {
        action: String,
        command_class: u8,
        command_id: u8,
        payload: String,
        captured: bool,
        applied: bool,
        read_back: bool,
        restored: bool,
    }
    fn base64(bytes: &[u8]) -> String {
        const TABLE: &[u8; 64] =
            b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
        let mut out = String::new();
        for chunk in bytes.chunks(3) {
            let n =
                chunk.iter().fold(0u32, |n, b| n << 8 | u32::from(*b)) << (8 * (3 - chunk.len()));
            for i in 0..4 {
                out.push(if i <= chunk.len() {
                    TABLE[(n >> (18 - 6 * i)) as usize & 63] as char
                } else {
                    '='
                });
            }
        }
        out
    }
    /// The HyperBoost restore pair is the managed RZ09-0581 HyperBoost
    /// production fixture, byte for byte, and equal to the Balanced pair.
    #[test]
    fn the_hyperboost_restore_pair_is_the_managed_fixture_and_the_balanced_pair() {
        let fixture: Vec<Entry> = serde_json::from_str(include_str!(
            "../../../../src/OpenBlade.Protocol/Fixtures/Rz09_0581_02e0_4_00_HyperBoostProduction.json"
        ))
        .unwrap();
        let restore = PresetPair::hyperboost_restore([19, 20]).unwrap();
        let balanced = PresetPair::new([19, 20], InstalledThermalPreset::Balanced).unwrap();
        for (index, command) in restore.commands().iter().enumerate() {
            let entry = fixture_entry(
                &fixture,
                &format!("hyperboost-restore-balanced-channel-{}", index + 1),
            );
            let encoded = command.encode();
            assert_eq!((encoded[6], encoded[7], encoded[5]), (entry.0, entry.1, 4));
            assert_eq!(base64(&encoded[8..12]), entry.2);
            assert_eq!(encoded, balanced.commands()[index].encode());
            let mut reply = encoded;
            reply[0] = 2;
            assert!(restore.acknowledge(index, &reply).is_ok());
            assert!(restore.acknowledge(1 - index, &reply).is_err());
        }
        // The enable pair is HyperBoost's own selector, never a restore.
        let enable = fixture_entry(&fixture, "hyperboost-enable-channel-1");
        assert_ne!(base64(&restore.commands()[0].encode()[8..12]), enable.2);
        assert!(PresetPair::hyperboost_restore([1, 1]).is_none());
        assert!(PresetPair::hyperboost_restore([0, 1]).is_none());
    }
    #[test]
    fn complete_pair_uses_requested_destination_and_exact_echo() {
        for destination in ALL {
            let pair = PresetPair::new([19, 20], destination).unwrap();
            for (index, command) in pair.commands().iter().enumerate() {
                let mut reply = command.encode();
                assert_eq!(
                    &reply[8..12],
                    &[1, index as u8 + 1, destination.selector(), 0]
                );
                reply[0] = 2;
                assert!(pair.acknowledge(index, &reply).is_ok());
                assert!(pair.acknowledge(1 - index, &reply).is_err());
            }
        }
        let pair = PresetPair::custom([19, 20]).unwrap();
        for (index, command) in pair.commands().iter().enumerate() {
            let mut reply = command.encode();
            assert_eq!(&reply[8..12], &[1, index as u8 + 1, CUSTOM_SELECTOR, 0]);
            reply[0] = 2;
            assert!(pair.acknowledge(index, &reply).is_ok());
            assert!(pair.acknowledge(1 - index, &reply).is_err());
        }
        assert!(PresetPair::new([0, 1], InstalledThermalPreset::Balanced).is_none());
        assert!(PresetPair::new([1, 1], InstalledThermalPreset::Balanced).is_none());
        assert!(PresetPair::custom([1, 1]).is_none());
    }
}

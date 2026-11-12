//! Exact RZ09-0581 0D/02 action pair. This codec grants no I/O authority.
use crate::RazerReport;
pub use openblade_types::InstalledThermalPreset;
use openblade_types::ProtocolValidationFailure;

#[derive(Clone, Debug)]
pub struct PresetPair {
    commands: [RazerReport; 2],
    destination: InstalledThermalPreset,
}
impl PresetPair {
    /// Resolve the entire reviewed pair before entering the first submission.
    pub fn new(ids: [u8; 2], destination: InstalledThermalPreset) -> Option<Self> {
        if ids[0] == 0 || ids[1] == 0 || ids[0] == ids[1] {
            return None;
        }
        Some(Self {
            commands: core::array::from_fn(|i| {
                RazerReport::create(
                    ids[i],
                    0x0d,
                    0x02,
                    &[1, i as u8 + 1, destination.selector(), 0],
                )
                .expect("fixed reviewed preset payload")
            }),
            destination,
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
        if payload.as_slice() != [1, index as u8 + 1, self.destination.selector(), 0] {
            return Err(ProtocolValidationFailure::PayloadValidator);
        }
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn complete_pair_uses_requested_destination_and_exact_echo() {
        for destination in [
            InstalledThermalPreset::Balanced,
            InstalledThermalPreset::Performance,
        ] {
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
        assert!(PresetPair::new([0, 1], InstalledThermalPreset::Balanced).is_none());
        assert!(PresetPair::new([1, 1], InstalledThermalPreset::Balanced).is_none());
    }
}

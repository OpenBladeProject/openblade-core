//! Typed 0D/87 getter and 0D/07 setter geometry. Model/custody admission
//! belongs to the caller.
use crate::RazerReport;
use openblade_types::ProtocolValidationFailure;
pub use openblade_types::{CustomPerformanceComponent, CustomPerformanceLevel};

pub struct CustomPerformanceQuery {
    request: RazerReport,
    component: CustomPerformanceComponent,
}
impl CustomPerformanceQuery {
    pub fn new(transaction: u8, component: CustomPerformanceComponent) -> Self {
        Self {
            request: RazerReport::create(transaction, 0x0d, 0x87, &[1, component as u8, 0])
                .expect("fixed three-byte getter payload"),
            component,
        }
    }
    pub fn request(&self) -> &RazerReport {
        &self.request
    }
    pub fn decode(
        &self,
        response: &[u8],
    ) -> Result<CustomPerformanceLevel, ProtocolValidationFailure> {
        // Envelope fault precedence is shared with the managed codec.
        let payload = self.request.validate_response(response, Some(3))?;
        let [1, component, level] = payload.as_slice() else {
            return Err(ProtocolValidationFailure::PayloadValidator);
        };
        if *component != self.component as u8 {
            return Err(ProtocolValidationFailure::PayloadValidator);
        }
        match level {
            0 => Ok(CustomPerformanceLevel::Low),
            1 => Ok(CustomPerformanceLevel::Medium),
            2 => Ok(CustomPerformanceLevel::High),
            _ => Err(ProtocolValidationFailure::PayloadValidator),
        }
    }
}

/// The exact RZ09-0581 CPU and GPU level setters, 0D/07 `[1, component,
/// level]` (`custom-performance-cpu-*` and `custom-performance-gpu-*` in the
/// managed production fixture), in managed `SetCustomPerformanceAsync` order.
#[derive(Clone, Debug)]
pub struct CustomLevelPair {
    commands: [RazerReport; 2],
}
impl CustomLevelPair {
    /// Resolve both setters before entering the first submission.
    pub fn new(
        ids: [u8; 2],
        cpu: CustomPerformanceLevel,
        gpu: CustomPerformanceLevel,
    ) -> Option<Self> {
        if ids[0] == 0 || ids[1] == 0 || ids[0] == ids[1] {
            return None;
        }
        let setters = [
            (CustomPerformanceComponent::Cpu, cpu),
            (CustomPerformanceComponent::Gpu, gpu),
        ];
        Some(Self {
            commands: core::array::from_fn(|i| {
                let (component, level) = setters[i];
                RazerReport::create(ids[i], 0x0d, 0x07, &[1, component as u8, level as u8])
                    .expect("fixed reviewed level payload")
            }),
        })
    }
    pub fn commands(&self) -> &[RazerReport; 2] {
        &self.commands
    }
    /// The reply must echo the exact setter payload, as managed compares it.
    pub fn acknowledge(&self, index: usize, reply: &[u8]) -> Result<(), ProtocolValidationFailure> {
        let command = self
            .commands
            .get(index)
            .ok_or(ProtocolValidationFailure::PayloadValidator)?;
        let encoded = command.encode();
        if command.validate_response(reply, Some(3))?.as_slice() != &encoded[8..11] {
            return Err(ProtocolValidationFailure::PayloadValidator);
        }
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn level_setters_echo_their_own_component_and_level() {
        use CustomPerformanceLevel as L;
        let pair = CustomLevelPair::new([7, 8], L::Medium, L::Low).unwrap();
        for (index, expected) in [[1, 1, 1], [1, 2, 0]].iter().enumerate() {
            let mut reply = pair.commands()[index].encode();
            assert_eq!((reply[6], reply[7], reply[5]), (0x0d, 0x07, 3));
            assert_eq!(&reply[8..11], expected);
            reply[0] = 2;
            assert!(pair.acknowledge(index, &reply).is_ok());
            assert!(pair.acknowledge(1 - index, &reply).is_err());
            reply[10] ^= 1;
            assert!(pair.acknowledge(index, &reply).is_err());
        }
        assert!(pair.acknowledge(2, &pair.commands()[0].encode()).is_err());
        assert!(CustomLevelPair::new([0, 1], L::Low, L::Low).is_none());
        assert!(CustomLevelPair::new([3, 3], L::Low, L::Low).is_none());
    }
}

//! Typed 0D/87 getter geometry. Model/custody admission belongs to the caller.
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

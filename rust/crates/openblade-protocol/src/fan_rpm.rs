//! Exact RZ09-0581 fan speed, C# `FanRpmProtocol`: the read-only 0D/88 getter
//! of the capture-backed fixture
//! `evidence/legacy-captures/decoded/rz09-0581-pid-02e0-bios-3.01-fan-rpm-fixture.json`
//! (`productionWired`). This codec grants no I/O authority and builds no setter.
use crate::RazerReport;
use openblade_types::ProtocolValidationFailure;

const CLASS: u8 = 0x0d;
const QUERY: u8 = 0x88;
/// The request and its reply both carry exactly three payload bytes.
const PAYLOAD_LENGTH: i32 = 3;
/// The fixture's `scaleRpmPerUnit`.
const RPM_PER_UNIT: u16 = 100;

/// The two fan selectors the fixture reads, in managed `FanRpmSelector` order.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum FanRpmSelector {
    First = 0x01,
    Second = 0x02,
}

/// One 0D/88 getter with its caller-selected transaction ID.
pub struct FanRpmQuery {
    request: RazerReport,
    selector: FanRpmSelector,
}

impl FanRpmQuery {
    pub fn new(transaction: u8, selector: FanRpmSelector) -> Self {
        Self {
            request: RazerReport::create(transaction, CLASS, QUERY, &[0x01, selector as u8, 0x00])
                .expect("fixed three-byte fan speed query"),
            selector,
        }
    }

    pub fn request(&self) -> &RazerReport {
        &self.request
    }

    /// Envelope faults keep the shared managed precedence. A correlated success
    /// must echo `[0x01, selector]`; its third byte counts hundreds of RPM.
    pub fn decode(&self, response: &[u8]) -> Result<u16, ProtocolValidationFailure> {
        let payload = self
            .request
            .validate_response(response, Some(PAYLOAD_LENGTH))?;
        match payload.as_slice() {
            [0x01, selector, units] if *selector == self.selector as u8 => {
                Ok(u16::from(*units) * RPM_PER_UNIT)
            }
            _ => Err(ProtocolValidationFailure::PayloadValidator),
        }
    }
}

#[cfg(test)]
mod tests;

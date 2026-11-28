//! Exact RZ09-0581 MCU firmware version, C# `McuVersionProtocol`: the
//! read-only 00/87 getter of the capture-backed fixture
//! `evidence/legacy-captures/decoded/rz09-0581-pid-02e0-bios-3.01-mcu-version-fixture.json`
//! (`productionWired`, the Synapse `firmwareInfo.currentFWVersion` request
//! `[4, 0, 135]`). This codec grants no I/O authority and builds no setter.
use crate::RazerReport;
use core::fmt;
use openblade_types::ProtocolValidationFailure;

const CLASS: u8 = 0x00;
const QUERY: u8 = 0x87;
/// The request and its reply both carry exactly four payload bytes.
const PAYLOAD_LENGTH: i32 = 4;
const QUERY_ARGUMENTS: [u8; 4] = [0; 4];

/// The four version bytes in reply order (major, minor, internal, reserved),
/// written as managed `TryDecodeQuery` writes them: four decimal components
/// joined by dots.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct McuVersion(pub [u8; 4]);

impl fmt::Display for McuVersion {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        let [major, minor, internal, reserved] = self.0;
        write!(formatter, "{major}.{minor}.{internal}.{reserved}")
    }
}

/// One 00/87 getter with its caller-selected transaction ID.
pub struct McuVersionQuery {
    request: RazerReport,
}

impl McuVersionQuery {
    pub fn new(transaction: u8) -> Self {
        Self {
            request: RazerReport::create(transaction, CLASS, QUERY, &QUERY_ARGUMENTS)
                .expect("fixed four-byte MCU version query"),
        }
    }

    pub fn request(&self) -> &RazerReport {
        &self.request
    }

    /// Envelope faults keep the shared managed precedence. A correlated
    /// success must carry exactly four payload bytes, and any four bytes are a
    /// version, as managed decodes them.
    pub fn decode(&self, response: &[u8]) -> Result<McuVersion, ProtocolValidationFailure> {
        let payload = self
            .request
            .validate_response(response, Some(PAYLOAD_LENGTH))?;
        let version = payload
            .as_slice()
            .try_into()
            .map_err(|_| ProtocolValidationFailure::PayloadValidator)?;
        Ok(McuVersion(version))
    }
}

#[cfg(test)]
mod tests;

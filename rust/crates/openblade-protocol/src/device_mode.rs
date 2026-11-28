//! Exact RZ09-0581 keyboard device mode, C# `DeviceModeProtocol`: the 00/84
//! query and the 00/04 Normal and Driver setters of the reviewed managed
//! admission `Rz09_0581_02e0_3_01_LightingSemanticAdmission.json`
//! (`deviceMode`). This codec grants no I/O authority.
//!
//! The query is always built. The setters exist only with the
//! `device-mode-setters` feature, which only the host's
//! `service-keyboard-driver-mode-v1` composition enables, and in this module's
//! tests, which pin all three against the same admission. `RazerReport::create`
//! stays a public encoder, so this module cannot stop other code building
//! 00/04; the host's device-mode guard test fails when a new setter user or a
//! new report construction appears.
use crate::RazerReport;
use openblade_types::ProtocolValidationFailure;

const CLASS: u8 = 0x00;
const QUERY: u8 = 0x84;
const QUERY_ARGUMENTS: [u8; 2] = [0, 0];
/// The query reply, like each setter acknowledgement, carries exactly two bytes.
const PAYLOAD_LENGTH: i32 = 2;

/// The two firmware modes the reviewed contract names. Any other byte is
/// refused, never mapped to one of these.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum DeviceMode {
    Normal = 0x00,
    Driver = 0x03,
}

impl DeviceMode {
    /// C# `TryDecodeQuery`: the mode byte, then a zero byte.
    fn decode(payload: &[u8]) -> Option<Self> {
        match payload {
            [0x00, 0x00] => Some(Self::Normal),
            [0x03, 0x00] => Some(Self::Driver),
            _ => None,
        }
    }
}

/// The 00/84 getter with its caller-selected transaction ID.
pub struct DeviceModeQuery {
    request: RazerReport,
}

impl DeviceModeQuery {
    pub fn new(transaction: u8) -> Self {
        Self {
            request: RazerReport::create(transaction, CLASS, QUERY, &QUERY_ARGUMENTS)
                .expect("fixed two-byte device-mode query"),
        }
    }

    pub fn request(&self) -> &RazerReport {
        &self.request
    }

    /// Envelope faults keep the shared managed precedence. A correlated
    /// success must carry exactly two payload bytes naming a reviewed mode.
    pub fn decode(&self, response: &[u8]) -> Result<DeviceMode, ProtocolValidationFailure> {
        let payload = self
            .request
            .validate_response(response, Some(PAYLOAD_LENGTH))?;
        DeviceMode::decode(payload.as_slice()).ok_or(ProtocolValidationFailure::PayloadValidator)
    }
}

/// The reviewed 00/04 setter for one mode, with its caller-selected
/// transaction ID: `0000` for Normal and `0300` for Driver.
#[cfg(any(test, feature = "device-mode-setters"))]
pub struct DeviceModeSet {
    request: RazerReport,
    arguments: [u8; 2],
}

#[cfg(any(test, feature = "device-mode-setters"))]
impl DeviceModeSet {
    const ID: u8 = 0x04;

    pub fn new(transaction: u8, mode: DeviceMode) -> Self {
        let arguments = [mode as u8, 0];
        Self {
            request: RazerReport::create(transaction, CLASS, Self::ID, &arguments)
                .expect("fixed two-byte device-mode setter"),
            arguments,
        }
    }

    pub fn request(&self) -> &RazerReport {
        &self.request
    }

    /// C# `TryDecodeSet`: a correlated success whose two payload bytes echo
    /// the setter arguments exactly. It is never a mode readback.
    pub fn acknowledge(&self, response: &[u8]) -> Result<(), ProtocolValidationFailure> {
        let payload = self
            .request
            .validate_response(response, Some(PAYLOAD_LENGTH))?;
        if payload.as_slice() == self.arguments {
            Ok(())
        } else {
            Err(ProtocolValidationFailure::PayloadValidator)
        }
    }
}

#[cfg(test)]
mod tests;

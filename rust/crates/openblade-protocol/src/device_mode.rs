//! Exact RZ09-0581 keyboard device mode, C# `DeviceModeProtocol`: the 00/84
//! query and the 00/04 Normal and Driver setters of the reviewed managed
//! admission `Rz09_0581_02e0_3_01_LightingSemanticAdmission.json`
//! (`deviceMode`). This codec grants no I/O authority.
//!
//! Only the query is admitted for submission. The setters are encoded and
//! decoded in test builds only, so no production caller can construct one;
//! acquiring and releasing Driver mode is a later, separately reviewed stage.
use crate::RazerReport;
use openblade_types::ProtocolValidationFailure;

const CLASS: u8 = 0x00;
const QUERY: u8 = 0x84;
const QUERY_ARGUMENTS: [u8; 2] = [0, 0];
/// The query reply and each setter acknowledgement carry exactly two bytes.
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

/// The 00/04 setter, test builds only until Driver-mode ownership is admitted.
#[cfg(test)]
pub(crate) struct DeviceModeSet {
    request: RazerReport,
    arguments: [u8; 2],
}

#[cfg(test)]
impl DeviceModeSet {
    const ID: u8 = 0x04;

    pub(crate) fn new(transaction: u8, mode: DeviceMode) -> Self {
        let arguments = [mode as u8, 0];
        Self {
            request: RazerReport::create(transaction, CLASS, Self::ID, &arguments)
                .expect("fixed two-byte device-mode setter"),
            arguments,
        }
    }

    pub(crate) fn request(&self) -> &RazerReport {
        &self.request
    }

    /// C# `TryDecodeSet`: the acknowledgement echoes the setter arguments.
    pub(crate) fn acknowledge(&self, response: &[u8]) -> Result<(), ProtocolValidationFailure> {
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

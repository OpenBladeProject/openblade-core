//! Exact RZ09-0581 keyboard brightness and logo lighting on the control
//! mailbox, C# `LightingBrightnessProtocol` and `LogoLightingProtocol`: the
//! 03/83 and 03/80/03/82 queries and the 03/03, 03/00 and 03/02 setters of
//! the reviewed managed admission
//! `Rz09_0581_02e0_3_01_LightingSemanticAdmission.json` (`brightness` and
//! `logo`). This codec grants no I/O authority and carries none of the
//! host-rendered matrix commands.
//!
//! The queries are always built. The setters exist only with the
//! `lighting-setters` feature, which only the host's `service-lighting-v1`
//! composition enables, and in this module's tests, which pin every report
//! against the same admission. `RazerReport::create` stays a public encoder,
//! so this module cannot stop other code building these commands; the host's
//! lighting guard test fails when a new setter user or a new report
//! construction appears.
use crate::RazerReport;
use openblade_types::ProtocolValidationFailure;

const CLASS: u8 = 0x03;
const BRIGHTNESS_QUERY: u8 = 0x83;
const LOGO_STATE_QUERY: u8 = 0x80;
const LOGO_EFFECT_QUERY: u8 = 0x82;
/// Managed `PersistentStorage`, the only storage byte the contract names.
const STORAGE: u8 = 0x01;
/// The keyboard backlight LED of the brightness commands.
const BACKLIGHT_LED: u8 = 0x05;
/// Managed `LogoLedId`.
const LOGO_LED: u8 = 0x04;
const STATIC_EFFECT: u8 = 0x00;
const BREATHING_EFFECT: u8 = 0x02;
/// Every query reply and setter acknowledgement carries exactly three bytes.
const PAYLOAD_LENGTH: i32 = 3;

/// Managed `LogoLightingMode`. A state or effect byte outside the reviewed
/// contract is refused, never mapped to one of these.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum LogoMode {
    Static,
    Breathing,
    Off,
}

impl LogoMode {
    /// Managed `LogoLightingProtocol.TryDecodeMode`: disabled is Off whatever
    /// the effect byte; enabled needs the Static or Breathing effect.
    pub fn decode(enabled: bool, effect: u8) -> Option<Self> {
        match (enabled, effect) {
            (false, _) => Some(Self::Off),
            (true, STATIC_EFFECT) => Some(Self::Static),
            (true, BREATHING_EFFECT) => Some(Self::Breathing),
            _ => None,
        }
    }
}

/// Managed `LightingBrightnessProtocol.PercentToRaw`: the ceiling of
/// `percent * 255 / 100`, so the captured Synapse 77% is 0xC5 and every
/// non-zero step stays distinguishable from Off. `None` above 100.
pub fn percent_to_raw(percent: u8) -> Option<u8> {
    (percent <= 100).then(|| ((u16::from(percent) * 255).div_ceil(100)) as u8)
}

/// One report of this contract, with its caller-selected transaction ID.
fn report(transaction: u8, id: u8, arguments: &[u8; 3]) -> RazerReport {
    RazerReport::create(transaction, CLASS, id, arguments)
        .expect("fixed three-byte lighting report")
}

/// A correlated success with exactly three payload bytes.
fn payload(request: &RazerReport, response: &[u8]) -> Result<[u8; 3], ProtocolValidationFailure> {
    let payload = request.validate_response(response, Some(PAYLOAD_LENGTH))?;
    payload
        .as_slice()
        .try_into()
        .map_err(|_| ProtocolValidationFailure::PayloadLength)
}

/// The 03/83 keyboard brightness getter (`01 05 00`).
pub struct BrightnessQuery {
    request: RazerReport,
}

impl BrightnessQuery {
    pub fn new(transaction: u8) -> Self {
        Self {
            request: report(transaction, BRIGHTNESS_QUERY, &[STORAGE, BACKLIGHT_LED, 0]),
        }
    }

    pub fn request(&self) -> &RazerReport {
        &self.request
    }

    /// C# `TryDecodeQuery`: `01 05` then the raw brightness.
    pub fn decode(&self, response: &[u8]) -> Result<u8, ProtocolValidationFailure> {
        match payload(&self.request, response)? {
            [STORAGE, BACKLIGHT_LED, raw] => Ok(raw),
            _ => Err(ProtocolValidationFailure::PayloadValidator),
        }
    }
}

/// The 03/80 logo state getter (`01 04 00`).
pub struct LogoStateQuery {
    request: RazerReport,
}

impl LogoStateQuery {
    pub fn new(transaction: u8) -> Self {
        Self {
            request: report(transaction, LOGO_STATE_QUERY, &[STORAGE, LOGO_LED, 0]),
        }
    }

    pub fn request(&self) -> &RazerReport {
        &self.request
    }

    /// C# `TryDecodeState`: `01 04` then 0 (disabled) or 1 (enabled).
    pub fn decode(&self, response: &[u8]) -> Result<bool, ProtocolValidationFailure> {
        match payload(&self.request, response)? {
            [STORAGE, LOGO_LED, 0] => Ok(false),
            [STORAGE, LOGO_LED, 1] => Ok(true),
            _ => Err(ProtocolValidationFailure::PayloadValidator),
        }
    }
}

/// The 03/82 logo effect getter (`01 04 00`).
pub struct LogoEffectQuery {
    request: RazerReport,
}

impl LogoEffectQuery {
    pub fn new(transaction: u8) -> Self {
        Self {
            request: report(transaction, LOGO_EFFECT_QUERY, &[STORAGE, LOGO_LED, 0]),
        }
    }

    pub fn request(&self) -> &RazerReport {
        &self.request
    }

    /// C# `TryDecodeEffect`: `01 04` then any effect byte, which only the
    /// combined decode (`LogoMode::decode`) admits.
    pub fn decode(&self, response: &[u8]) -> Result<u8, ProtocolValidationFailure> {
        match payload(&self.request, response)? {
            [STORAGE, LOGO_LED, effect] => Ok(effect),
            _ => Err(ProtocolValidationFailure::PayloadValidator),
        }
    }
}

/// A reviewed setter whose acknowledgement must echo its arguments exactly
/// (C# `TryDecodeSet` and the logo writes' echo validators). It is never a
/// readback.
#[cfg(any(test, feature = "lighting-setters"))]
pub struct LightingSet {
    request: RazerReport,
    arguments: [u8; 3],
}

#[cfg(any(test, feature = "lighting-setters"))]
impl LightingSet {
    const BRIGHTNESS: u8 = 0x03;
    const LOGO_STATE: u8 = 0x00;
    const LOGO_EFFECT: u8 = 0x02;

    fn new(transaction: u8, id: u8, arguments: [u8; 3]) -> Self {
        Self {
            request: report(transaction, id, &arguments),
            arguments,
        }
    }

    /// The 03/03 setter: `01 05` then the raw brightness.
    pub fn brightness(transaction: u8, raw: u8) -> Self {
        Self::new(transaction, Self::BRIGHTNESS, [STORAGE, BACKLIGHT_LED, raw])
    }

    /// The 03/00 setter: `01 04` then 0 for Off and 1 otherwise.
    pub fn logo_state(transaction: u8, mode: LogoMode) -> Self {
        let enabled = u8::from(mode != LogoMode::Off);
        Self::new(transaction, Self::LOGO_STATE, [STORAGE, LOGO_LED, enabled])
    }

    /// The 03/02 setter: `01 04` then the Static or Breathing effect. Managed
    /// sends it only before enabling a lit mode, and maps Off to Static.
    pub fn logo_effect(transaction: u8, mode: LogoMode) -> Self {
        let effect = if mode == LogoMode::Breathing {
            BREATHING_EFFECT
        } else {
            STATIC_EFFECT
        };
        Self::new(transaction, Self::LOGO_EFFECT, [STORAGE, LOGO_LED, effect])
    }

    pub fn request(&self) -> &RazerReport {
        &self.request
    }

    /// A correlated success whose three payload bytes echo the setter
    /// arguments byte for byte.
    pub fn acknowledge(&self, response: &[u8]) -> Result<(), ProtocolValidationFailure> {
        if payload(&self.request, response)? == self.arguments {
            Ok(())
        } else {
            Err(ProtocolValidationFailure::PayloadValidator)
        }
    }
}

#[cfg(test)]
mod tests;

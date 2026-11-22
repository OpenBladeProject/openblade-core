//! Power-setting notices, as managed `NativeWindowsLifecycleNotificationBackend`
//! reads them (WindowsServiceLifecycleEventSource.cs:524-760).
//!
//! Three pure pieces, each pinned against managed by recorded vectors:
//!
//! - [`decode`] is `TryReadPowerSettingDword` over the bytes of one
//!   `POWERBROADCAST_SETTING`: the setting GUID must be one of the two
//!   registered settings, `DataLength` must hold a whole DWORD, and the DWORD
//!   must be 0, 1 or 2. Anything else is refused.
//! - [`classify_console_display_transition`] is
//!   `ClassifyConsoleDisplayTransition` (:539-547): only off to on or dimmed
//!   is display-on, and only a known non-off state to off is display-off.
//! - [`power_event_signal`] is the notice-to-signal mapping of
//!   `ClassifySuspendResumeNotification`, `OnConsoleDisplayNotification` and
//!   the gate filter in `Publish` (:354-359): suspend, both resumes and
//!   display-on reach the gate; display-off, AC/DC and every other notice
//!   carry no gate signal.
//!
//! Managed registers one callback per setting, so its callback knows the
//! GUID. A service handle receives both settings through one SCM handler, so
//! [`decode`] dispatches on the GUID instead. Managed's AC/DC callback never
//! reads its payload; it publishes `PowerChanged`, which does not touch the
//! gate, and the host samples the power kind on every power event anyway.
use crate::low_power_gate::Signal;

/// `POWERBROADCAST_SETTING`: a 16-byte GUID, then a little-endian `DataLength`.
pub const HEADER_LEN: usize = 20;
/// The most this decoder reads: the header and one DWORD.
pub const MAX_LEN: usize = HEADER_LEN + 4;

/// `GUID_ACDC_POWER_SOURCE`, 5D3E9A59-E9D5-4B00-A6BD-FF34FF516548, in memory order.
pub const ACDC_POWER_SOURCE: [u8; 16] = [
    0x59, 0x9A, 0x3E, 0x5D, 0xD5, 0xE9, 0x00, 0x4B, 0xA6, 0xBD, 0xFF, 0x34, 0xFF, 0x51, 0x65, 0x48,
];
/// `GUID_CONSOLE_DISPLAY_STATE`, 6FE69556-704A-47A0-8F24-C28D936FDA47, in memory order.
pub const CONSOLE_DISPLAY_STATE: [u8; 16] = [
    0x56, 0x95, 0xE6, 0x6F, 0x4A, 0x70, 0xA0, 0x47, 0x8F, 0x24, 0xC2, 0x8D, 0x93, 0x6F, 0xDA, 0x47,
];

pub const PBT_APMSUSPEND: u32 = 0x0004;
pub const PBT_APMRESUMESUSPEND: u32 = 0x0007;
pub const PBT_APMRESUMEAUTOMATIC: u32 = 0x0012;
pub const PBT_POWERSETTINGCHANGE: u32 = 0x8013;

pub const MONITOR_OFF: u32 = 0;
pub const MONITOR_ON: u32 = 1;
pub const MONITOR_DIMMED: u32 = 2;

/// One decoded setting and its DWORD.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum PowerSetting {
    PowerSource(u32),
    ConsoleDisplay(u32),
}

/// Managed `TryReadPowerSettingDword` for whichever registered GUID the
/// header names. `buffer` holds the header and at most the payload bytes
/// Windows supplied; a buffer shorter than the DWORD its header declares is
/// refused rather than read past.
pub fn decode(buffer: &[u8]) -> Option<PowerSetting> {
    let guid = buffer.get(..16)?;
    let length = u32::from_le_bytes(buffer.get(16..HEADER_LEN)?.try_into().ok()?);
    if length < 4 {
        return None;
    }
    let value = u32::from_le_bytes(buffer.get(HEADER_LEN..MAX_LEN)?.try_into().ok()?);
    if !matches!(value, MONITOR_OFF | MONITOR_ON | MONITOR_DIMMED) {
        return None;
    }
    if guid == ACDC_POWER_SOURCE {
        Some(PowerSetting::PowerSource(value))
    } else if guid == CONSOLE_DISPLAY_STATE {
        Some(PowerSetting::ConsoleDisplay(value))
    } else {
        None
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum DisplayTransition {
    Off,
    On,
}

/// Managed `ClassifyConsoleDisplayTransition`. `previous` is `None` until the
/// first valid read.
pub fn classify_console_display_transition(
    previous: Option<u32>,
    current: u32,
) -> Option<DisplayTransition> {
    match (previous, current) {
        (Some(previous), MONITOR_OFF) if previous != MONITOR_OFF => Some(DisplayTransition::Off),
        (Some(MONITOR_OFF), MONITOR_ON | MONITOR_DIMMED) => Some(DisplayTransition::On),
        _ => None,
    }
}

/// The last valid console display state, unknown until the first valid
/// read (managed `consoleDisplayState`, which starts at -1).
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct ConsoleDisplay(Option<u32>);

impl ConsoleDisplay {
    pub const fn unknown() -> Self {
        Self(None)
    }

    pub const fn state(self) -> Option<u32> {
        self.0
    }

    /// Records a valid read and classifies it against the previous one, as
    /// `OnConsoleDisplayNotification` exchanges and then classifies.
    pub fn observe(&mut self, current: u32) -> Option<DisplayTransition> {
        classify_console_display_transition(self.0.replace(current), current)
    }
}

/// The gate signal of one power event. `setting` is the decoded payload of a
/// `PBT_POWERSETTINGCHANGE` and is ignored for any other event. Only a
/// console display read updates `display`.
pub fn power_event_signal(
    display: &mut ConsoleDisplay,
    event: u32,
    setting: Option<PowerSetting>,
) -> Option<Signal> {
    match (event, setting) {
        (PBT_APMSUSPEND, _) => Some(Signal::Suspend),
        (PBT_APMRESUMESUSPEND | PBT_APMRESUMEAUTOMATIC, _) => Some(Signal::Resume),
        (PBT_POWERSETTINGCHANGE, Some(PowerSetting::ConsoleDisplay(current))) => {
            (display.observe(current) == Some(DisplayTransition::On)).then_some(Signal::DisplayOn)
        }
        _ => None,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn buffer(guid: [u8; 16], length: u32, payload: &[u8]) -> ([u8; 32], usize) {
        let mut bytes = [0; 32];
        bytes[..16].copy_from_slice(&guid);
        bytes[16..20].copy_from_slice(&length.to_le_bytes());
        bytes[20..20 + payload.len()].copy_from_slice(payload);
        (bytes, 20 + payload.len())
    }

    #[test]
    fn a_buffer_shorter_than_its_declared_dword_is_refused() {
        let (bytes, _) = buffer(CONSOLE_DISPLAY_STATE, 4, &[1, 0, 0, 0]);
        assert_eq!(decode(&bytes[..24]), Some(PowerSetting::ConsoleDisplay(1)));
        for len in [0, 15, 16, 19, 20, 21, 23] {
            assert_eq!(decode(&bytes[..len]), None, "{len} bytes");
        }
        // Bytes past the DWORD are never read.
        let (long, len) = buffer(ACDC_POWER_SOURCE, 8, &[2, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF]);
        assert_eq!(decode(&long[..len]), Some(PowerSetting::PowerSource(2)));
    }

    #[test]
    fn a_display_state_starts_unknown_and_classifies_against_the_last_valid_read() {
        let mut display = ConsoleDisplay::unknown();
        assert_eq!(display.state(), None);
        assert_eq!(display.observe(MONITOR_OFF), None);
        assert_eq!(display.observe(MONITOR_DIMMED), Some(DisplayTransition::On));
        assert_eq!(display.observe(MONITOR_ON), None);
        assert_eq!(display.observe(MONITOR_OFF), Some(DisplayTransition::Off));
        assert_eq!(display.state(), Some(MONITOR_OFF));
    }

    #[test]
    fn a_setting_outside_a_power_setting_change_neither_signals_nor_updates_the_display() {
        let mut display = ConsoleDisplay(Some(MONITOR_OFF));
        let on = Some(PowerSetting::ConsoleDisplay(MONITOR_ON));
        assert_eq!(power_event_signal(&mut display, 10, on), None);
        assert_eq!(display.state(), Some(MONITOR_OFF));
        assert_eq!(
            power_event_signal(&mut display, PBT_APMSUSPEND, on),
            Some(Signal::Suspend)
        );
        assert_eq!(display.state(), Some(MONITOR_OFF));
        let source = Some(PowerSetting::PowerSource(MONITOR_OFF));
        assert_eq!(
            power_event_signal(&mut display, PBT_POWERSETTINGCHANGE, source),
            None
        );
        assert_eq!(display.state(), Some(MONITOR_OFF));
        assert_eq!(
            power_event_signal(&mut display, PBT_POWERSETTINGCHANGE, on),
            Some(Signal::DisplayOn)
        );
    }
}

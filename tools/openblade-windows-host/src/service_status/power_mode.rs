//! The status members managed `WindowsPowerModeAutomationCoordinator.Observe`
//! reads, borrowed as the Service published them. The Agent's power-mode
//! automation decodes them; nothing else reads them through this view.
use super::Snapshot;
use serde_json::Value;

/// `Snapshot::power_mode_automation`: borrowed raw status members.
pub(crate) struct PowerModeFields<'a> {
    /// Managed `SessionControlAccess`, already bounded to 0..=2 by `verify`.
    pub(crate) control_access: i32,
    pub(crate) device_verified: bool,
    pub(crate) observation_reason: Option<&'a Value>,
    pub(crate) unconfirmed_writes: Option<&'a Value>,
    /// Managed `PowerSource`, already bounded to 0..=3 by `verify`.
    pub(crate) active_power_source: i32,
    /// `confirmedState.performance`; `None` when the state or member is absent.
    pub(crate) confirmed_performance: Option<&'a Value>,
    /// `profiles.windowsPowerModes`.
    pub(crate) windows_power_modes: Option<&'a Value>,
    /// The legacy `windowsPowerMode` of the PluggedIn, Battery and UsbC
    /// profiles, which the managed status codec requires to be clear.
    pub(crate) legacy: [Option<&'a Value>; 3],
}

impl Snapshot {
    pub(crate) fn power_mode_automation(&self) -> PowerModeFields<'_> {
        fn legacy(profile: &super::PowerProfile) -> Option<&Value> {
            profile.feature_fields.get("windowsPowerMode")
        }
        let legacy = [
            legacy(&self.profiles.plugged_in),
            legacy(&self.profiles.battery),
            legacy(&self.profiles.usb_c),
        ];
        PowerModeFields {
            control_access: self.control_access,
            device_verified: self.device_verified,
            observation_reason: self.feature_fields.get("observationReason"),
            unconfirmed_writes: self.feature_fields.get("unconfirmedWrites"),
            active_power_source: self.active_power_source,
            confirmed_performance: self
                .confirmed_state
                .as_ref()
                .and_then(|state| state.get("performance")),
            windows_power_modes: self.profiles.feature_fields.get("windowsPowerModes"),
            legacy,
        }
    }
}

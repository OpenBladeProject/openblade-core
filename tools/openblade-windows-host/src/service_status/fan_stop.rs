//! Managed `FanStopStatus` and `FanStopSettings` on the v91 wire, for a
//! composition that serves the Fan Stop requests while its guardian is
//! unavailable (`service-fan-stop-v1`).
//!
//! Managed's production composition wires `GuardianFanStopBackend` over
//! `WindowsFanStopGuardianClient`. When that client cannot verify the
//! guardian it reports `GuardianUnavailable` with no message of its own, so
//! `FanStopController.GetStatus` publishes Unavailable with the controller's
//! `UnavailableMessage` on a device with reviewed evidence, and the
//! no-evidence status on any other. Neither allows Stop or Auto.
use openblade_domain::profile_set::preferences::FanStopSettings;
use serde::Deserialize;
use serde_json::{Value, json};
use std::io;

use super::Snapshot;
use crate::service_frame::invalid;

/// Managed `FanStopController.UnavailableMessage`.
pub(crate) const UNAVAILABLE: &str =
    "Fan stop is unavailable: the service backend and controller-loss recovery are not validated.";
/// Managed `FanStopController.GetStatus` for a device without reviewed evidence.
const NO_EVIDENCE: &str = "Fan stop has no reviewed evidence for this device.";

/// Managed `FanStopUnavailableReason`.
const UNSUPPORTED_DEVICE: i32 = 1;
const GUARDIAN_UNAVAILABLE: i32 = 3;

/// Managed `FanStopAutomationState`.
pub(crate) mod automation {
    pub(crate) const DISABLED: i32 = 0;
    pub(crate) const WAITING: i32 = 1;
    pub(crate) const PAUSED: i32 = 2;
}

/// Managed `FanStopAutomationPauseReason`.
pub(crate) mod pause {
    pub(crate) const NONE: i32 = 0;
    pub(crate) const MANUAL_AUTO: i32 = 1;
    pub(crate) const SESSION_CHANGED: i32 = 3;
}

/// The request member as the managed codec reads `FanStopSettings`: every
/// member is optional and takes managed's initialiser, and unmapped members
/// are refused.
#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields, default)]
struct Body {
    end_mode: i32,
    cpu_limit_celsius: i32,
    gpu_limit_celsius: i32,
    cpu_immediate_cutoff_celsius: i32,
    gpu_immediate_cutoff_celsius: i32,
    seconds_above_limit: i32,
    automatic_enabled: bool,
    automatic_cpu_below_celsius: i32,
    automatic_gpu_below_celsius: i32,
    seconds_below_limits: i32,
}

impl Default for Body {
    fn default() -> Self {
        let d = FanStopSettings::default();
        Self {
            end_mode: d.end_mode,
            cpu_limit_celsius: d.cpu_limit_celsius,
            gpu_limit_celsius: d.gpu_limit_celsius,
            cpu_immediate_cutoff_celsius: d.cpu_immediate_cutoff_celsius,
            gpu_immediate_cutoff_celsius: d.gpu_immediate_cutoff_celsius,
            seconds_above_limit: d.seconds_above_limit,
            automatic_enabled: d.automatic_enabled,
            automatic_cpu_below_celsius: d.automatic_cpu_below_celsius,
            automatic_gpu_below_celsius: d.automatic_gpu_below_celsius,
            seconds_below_limits: d.seconds_below_limits,
        }
    }
}

/// A `setFanStopSettings` request's settings, refused unless managed
/// `FanStopSettings.IsWellFormed` admits them, as the codec refuses them.
pub(crate) fn decode_settings(value: &Value) -> io::Result<FanStopSettings> {
    let body: Body =
        serde_json::from_value(value.clone()).map_err(|_| invalid("invalid Fan Stop settings"))?;
    let settings = FanStopSettings {
        end_mode: body.end_mode,
        cpu_limit_celsius: body.cpu_limit_celsius,
        gpu_limit_celsius: body.gpu_limit_celsius,
        cpu_immediate_cutoff_celsius: body.cpu_immediate_cutoff_celsius,
        gpu_immediate_cutoff_celsius: body.gpu_immediate_cutoff_celsius,
        seconds_above_limit: body.seconds_above_limit,
        automatic_enabled: body.automatic_enabled,
        automatic_cpu_below_celsius: body.automatic_cpu_below_celsius,
        automatic_gpu_below_celsius: body.automatic_gpu_below_celsius,
        seconds_below_limits: body.seconds_below_limits,
    };
    if !settings.is_well_formed() {
        return Err(invalid("Fan Stop settings are not well formed"));
    }
    Ok(settings)
}

/// `settings` as the managed codec writes `FanStopSettings`.
fn settings_json(settings: &FanStopSettings) -> Value {
    json!({
        "endMode": settings.end_mode,
        "cpuLimitCelsius": settings.cpu_limit_celsius,
        "gpuLimitCelsius": settings.gpu_limit_celsius,
        "cpuImmediateCutoffCelsius": settings.cpu_immediate_cutoff_celsius,
        "gpuImmediateCutoffCelsius": settings.gpu_immediate_cutoff_celsius,
        "secondsAboveLimit": settings.seconds_above_limit,
        "automaticEnabled": settings.automatic_enabled,
        "automaticCpuBelowCelsius": settings.automatic_cpu_below_celsius,
        "automaticGpuBelowCelsius": settings.automatic_gpu_below_celsius,
        "secondsBelowLimits": settings.seconds_below_limits,
    })
}

/// Managed's public Fan Stop status before any user's projection: the
/// no-evidence status, with default settings and automation.
pub(crate) fn public_status() -> Value {
    json!({
        "requestedMode": 0,
        "state": 0,
        "unavailableReason": UNSUPPORTED_DEVICE,
        "canStop": false,
        "canReturnToAutomatic": false,
        "normalFanControl": null,
        "message": NO_EVIDENCE,
        "settings": settings_json(&FanStopSettings::default()),
        "automationState": automation::DISABLED,
        "automationPauseReason": pause::NONE,
    })
}

impl Snapshot {
    fn fan_stop_mut(&mut self) -> &mut Value {
        self.feature_fields
            .entry("fanStopStatus")
            .or_insert_with(public_status)
    }

    /// Managed's status on a device with reviewed Fan Stop evidence while
    /// the guardian is unavailable.
    pub(super) fn fan_stop_evident(mut self) -> Self {
        let status = self.fan_stop_mut();
        status["unavailableReason"] = GUARDIAN_UNAVAILABLE.into();
        status["message"] = UNAVAILABLE.into();
        self
    }

    /// The published status message, which managed's guardian path replies
    /// with when it refuses a mode request.
    pub(crate) fn fan_stop_message(&self) -> Option<&str> {
        self.feature_fields
            .get("fanStopStatus")?
            .get("message")?
            .as_str()
    }

    /// Managed `ProjectAutomaticFanStop` over an Unavailable status: the
    /// saved settings, and the automation state of `eligibility` (managed
    /// `AutomaticFanStopEligibility`) as the state and pause reason the
    /// settings let it show.
    pub(crate) fn with_fan_stop_automation(
        mut self,
        settings: &FanStopSettings,
        armed: bool,
        reason: i32,
    ) -> Self {
        let (state, reason) = match (settings.automatic_enabled, armed) {
            (false, _) => (automation::DISABLED, pause::NONE),
            (true, true) => (automation::WAITING, reason),
            (true, false) => (automation::PAUSED, reason),
        };
        let status = self.fan_stop_mut();
        status["settings"] = settings_json(settings);
        status["automationState"] = state.into();
        status["automationPauseReason"] = reason.into();
        self
    }

    /// Drops the user's settings and automation: managed reports them only
    /// to the active client.
    pub(super) fn without_fan_stop_automation(&mut self) {
        if let Some(status) = self.feature_fields.get_mut("fanStopStatus") {
            let public = public_status();
            for member in ["settings", "automationState", "automationPauseReason"] {
                status[member] = public[member].clone();
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn settings_decode_with_managed_defaults_and_refuse_what_the_codec_refuses() {
        assert_eq!(
            decode_settings(&json!({})).unwrap(),
            FanStopSettings::default()
        );
        let custom = decode_settings(&json!({"endMode": 1, "secondsAboveLimit": 9})).unwrap();
        assert_eq!((custom.end_mode, custom.seconds_above_limit), (1, 9));
        assert_eq!(
            settings_json(&decode_settings(&public_status()["settings"]).unwrap()),
            public_status()["settings"]
        );
        for refused in [
            json!({"endMode": 2}),
            json!({"cpuLimitCelsius": 9}),
            json!({"cpuLimitCelsius": 70}),
            json!({"secondsAboveLimit": 0}),
            json!({"secondsBelowLimits": 3601}),
            json!({"automaticEnabled": true, "automaticCpuBelowCelsius": 55}),
            json!({"endMode": "auto"}),
            json!({"cpuLimitCelsius": "55"}),
            json!({"cpuLimitCelsius": 55.5}),
            json!({"automaticEnabled": 1}),
            json!({"unknown": 0}),
            json!({"EndMode": 0}),
        ] {
            assert!(decode_settings(&refused).is_err(), "{refused}");
        }
    }

    #[test]
    fn automation_shows_only_what_the_settings_enable() {
        let base = || Snapshot::observed_read_only(0, 0, false, false, vec![], None, String::new());
        let enabled = FanStopSettings {
            automatic_enabled: true,
            ..FanStopSettings::default()
        };
        for (settings, armed, reason, state, shown) in [
            (FanStopSettings::default(), true, pause::NONE, 0, 0),
            (FanStopSettings::default(), false, pause::MANUAL_AUTO, 0, 0),
            (enabled, true, pause::NONE, 1, 0),
            (enabled, false, pause::SESSION_CHANGED, 2, 3),
        ] {
            let status = base().with_fan_stop_automation(&settings, armed, reason);
            let fan_stop = &status.feature_fields["fanStopStatus"];
            assert_eq!(fan_stop["automationState"], state);
            assert_eq!(fan_stop["automationPauseReason"], shown);
            assert_eq!(
                fan_stop["settings"]["automaticEnabled"],
                settings.automatic_enabled
            );
            let mut public = status.clone();
            public.without_fan_stop_automation();
            assert_eq!(public.feature_fields["fanStopStatus"], public_status());
        }
        let evident = base().with_fan_stop_evidence(true);
        assert_eq!(evident.fan_stop_message(), Some(UNAVAILABLE));
        assert_eq!(
            evident.feature_fields["fanStopStatus"]["unavailableReason"],
            GUARDIAN_UNAVAILABLE
        );
        assert_eq!(base().fan_stop_message(), Some(NO_EVIDENCE));
    }
}

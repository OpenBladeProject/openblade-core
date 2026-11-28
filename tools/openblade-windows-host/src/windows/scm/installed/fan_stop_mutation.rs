//! Installed `setFanStopMode`, `setFanStopSettings`,
//! `resumeAutomaticFanStop` and `setBatterySaverFanStop`, as managed
//! `SetFanStopModeAsync`, `SetFanStopSettingsAsync`,
//! `ResumeAutomaticFanStopAsync` and `SetBatterySaverFanStopAsync` serve them
//! in the production composition while its Fan Stop guardian is unavailable
//! (`WindowsFanStopGuardianClient` that cannot verify the guardian).
//!
//! No request touches hardware. Managed's guardian backend refuses both
//! modes while it is unavailable, and never resumes automatic Stop; this
//! composition has no guardian, so it replies the same. Fan Stop settings and
//! the Battery Saver preference are stored in the user's configuration
//! aggregate through the profile store's transaction. Battery Saver fan stop
//! is not admitted on RZ09-0581, so only turning it off is stored.
//!
//! A change takes the request capture as a profile save does, so a competing
//! controller, a locked session or an unknown AC status refuses it, and a
//! store failure replies `Failed` with the store's reason, where managed's
//! exception ends the request without a result (as the thermal policy
//! requests do; see `policy`).
use super::*;
use crate::installed::readiness::thermal_storage::PreferencesSave;
use crate::service_status::fan_stop::{UNAVAILABLE, pause};
use openblade_domain::profile_set::preferences::FanStopSettings;

/// Managed reply texts.
const MODE_SESSION: &str = "The active Windows session changed before fan-stop dispatch.";
const SETTINGS_UNAVAILABLE: &str =
    "Fan Stop settings require the active Windows session and an available configuration.";
const SETTINGS_ALREADY: &str = "Fan Stop settings are already saved.";
const SETTINGS_SAVED: &str = "Fan Stop settings saved.";
const RESUME_BLOCKED: &str =
    "Automatic Stop cannot resume in the current session or recovery state.";
const BATTERY_SESSION: &str =
    "The active Windows session changed before the fan preference could be saved.";
const BATTERY_UNSUPPORTED: &str = "Battery Saver fan stop is unavailable on this device.";
const BATTERY_SAVED: &str = "Battery Saver fan-stop preference saved.";
/// Managed `ConfigurationQuarantineBlockedMessage`.
const QUARANTINE_BLOCKED: &str = "OpenBlade preserved an unsupported or invalid configuration. Thermal settings are read-only until the service restarts with a valid configuration.";

/// Managed `FanStopMode.Automatic`.
const AUTOMATIC: i32 = 0;

/// Managed `ControlOperationStatus.Stored` and `Failed`.
const STORED: u8 = 0;
const FAILED: u8 = 3;

/// One Fan Stop request, as the decoder admitted it.
#[derive(Clone, Copy, Debug)]
pub(in super::super) enum FanStopRequest {
    /// Managed `FanStopMode` wire value.
    Mode(i32),
    /// Well-formed settings (`fan_stop::decode_settings`).
    Settings(FanStopSettings),
    Resume,
    /// Managed `SetBatterySaverFanStopRequest.Enabled`.
    BatterySaver(bool),
}

/// Managed `AutomaticFanStopEligibility`: whether automatic Stop is armed,
/// and the reason it last paused. Managed keeps one for the process and
/// pauses it with `SessionChanged` at every user activation, so the pair
/// that last acted owns it and any other pair sees that pause.
///
/// Managed also pauses an armed eligibility at mode changes, power changes
/// and lifecycle boundaries. Here nothing can arm Stop entry, so those
/// pauses change no behaviour and are not tracked; they would change only
/// the published pause reason (see the guide's deviations).
#[derive(Debug, Default)]
pub(super) struct Automation {
    owner: Option<(String, u32)>,
    armed: bool,
    reason: i32,
}

impl Automation {
    fn owns(&self, sid: &str, session: u32) -> bool {
        self.owner
            .as_ref()
            .is_some_and(|(owner, owned)| owner == sid && *owned == session)
    }
    /// Whether the eligibility is armed for `sid` in `session`, and its
    /// pause reason.
    fn view(&self, sid: &str, session: u32) -> (bool, i32) {
        if self.owns(sid, session) {
            (self.armed, self.reason)
        } else {
            (false, pause::SESSION_CHANGED)
        }
    }
    /// Managed's activation pause, when `sid` in `session` did not act last.
    fn activate(&mut self, sid: &str, session: u32) {
        if !self.owns(sid, session) {
            *self = Self {
                owner: Some((sid.to_owned(), session)),
                armed: false,
                reason: pause::SESSION_CHANGED,
            };
        }
    }
    /// Managed `PauseAutomaticFanStop`, and `DisableAutomaticFanStop` with
    /// no reason; true when it changed anything.
    fn pause(&mut self, reason: i32) -> bool {
        let changed = self.armed || self.reason != reason;
        self.armed = false;
        self.reason = reason;
        changed
    }
    /// Managed `ArmAutomaticFanStop`, which cannot race here: the operation
    /// gate is held.
    fn arm(&mut self) {
        self.armed = true;
        self.reason = pause::NONE;
    }
}

/// Managed `ProjectFanStopStatus` cannot read a caller's session; a caller
/// outside the control session is refused before any state changes.
fn controls_session(caller: &Caller) -> bool {
    caller.logon == Logon::Interactive && caller.control_session == Some(caller.session)
}

impl Runtime {
    /// The reply, and whether the published Fan Stop projection changed.
    /// `message` is the published Fan Stop status message, which managed's
    /// guardian path replies with when it refuses a mode.
    pub(in super::super) async fn fan_stop(
        &self,
        mailbox: MailboxSource,
        caller: Caller,
        sid: String,
        request: FanStopRequest,
        message: Option<String>,
    ) -> io::Result<(Outcome, bool)> {
        #[cfg(test)]
        self.entered.set(self.entered.get() + 1);
        // One mutation at a time, as managed's operation gate allows.
        let _serial = self.gate.lock().await;
        verify_retained_failure(&self.fatal)?;
        let blocked = |message: &str| Ok((Outcome::blocked(message), false));
        match request {
            FanStopRequest::Mode(mode) => {
                if !controls_session(&caller) {
                    return blocked(MODE_SESSION);
                }
                // Managed pauses automatic Stop before an Automatic request
                // is dispatched, whatever the backend then replies.
                let changed = mode == AUTOMATIC && {
                    let mut automation = self.fan_stop.borrow_mut();
                    automation.activate(&sid, caller.session);
                    automation.pause(pause::MANUAL_AUTO)
                };
                // The pause shows in this caller's projection, as managed
                // publishes it. A refused capture leaves managed's reply;
                // the projection then shows from the next stored request.
                if changed {
                    let _ = self.fan_stop_storage(&mailbox, &sid, caller.session)?;
                }
                Ok((
                    Outcome::blocked(message.unwrap_or_else(|| UNAVAILABLE.into())),
                    changed,
                ))
            }
            // Managed requires an available backend before anything else.
            FanStopRequest::Resume => blocked(RESUME_BLOCKED),
            FanStopRequest::Settings(settings) => {
                if !controls_session(&caller) {
                    return blocked(SETTINGS_UNAVAILABLE);
                }
                let storage = match self.fan_stop_storage(&mailbox, &sid, caller.session)? {
                    Ok(storage) => storage,
                    Err(refusal) => return Ok((refusal, false)),
                };
                self.fan_stop.borrow_mut().activate(&sid, caller.session);
                let previous = storage.fan_stop_settings()?;
                let saved = match storage.save_fan_stop_settings(settings) {
                    Ok(saved) => saved,
                    Err(error) if is_fatal_admission(&error) => return Err(error),
                    Err(error) => PreferencesSave::Rejected(unreadable(&error)),
                };
                Ok(match saved {
                    PreferencesSave::Unchanged(_) => (stored(SETTINGS_ALREADY), false),
                    PreferencesSave::Stored(_) => {
                        let mut automation = self.fan_stop.borrow_mut();
                        if !settings.automatic_enabled {
                            automation.pause(pause::NONE);
                        } else if !previous.automatic_enabled {
                            automation.arm();
                        }
                        (stored(SETTINGS_SAVED), true)
                    }
                    PreferencesSave::Rejected(message) => (failed(message), false),
                    PreferencesSave::Quarantined => (Outcome::blocked(SETTINGS_UNAVAILABLE), false),
                })
            }
            FanStopRequest::BatterySaver(enabled) => {
                if !controls_session(&caller) {
                    return blocked(BATTERY_SESSION);
                }
                let storage = match self.fan_stop_storage(&mailbox, &sid, caller.session)? {
                    Ok(storage) => storage,
                    Err(refusal) => return Ok((refusal, false)),
                };
                // Managed refuses an inadmissible configuration before it
                // refuses the capability; RZ09-0581 has no Battery Saver
                // fan stop (`SupportsBatterySaverFanStop` false).
                if enabled {
                    return blocked(if storage.configuration_admitted()? {
                        BATTERY_UNSUPPORTED
                    } else {
                        QUARANTINE_BLOCKED
                    });
                }
                let saved = match storage.save_battery_saver_fan_stop(enabled) {
                    Ok(saved) => saved,
                    Err(error) if is_fatal_admission(&error) => return Err(error),
                    Err(error) => PreferencesSave::Rejected(unreadable(&error)),
                };
                Ok(match saved {
                    PreferencesSave::Unchanged(_) => (stored(BATTERY_SAVED), false),
                    PreferencesSave::Stored(_) => (stored(BATTERY_SAVED), true),
                    PreferencesSave::Rejected(message) => (failed(message), false),
                    PreferencesSave::Quarantined => (Outcome::blocked(QUARANTINE_BLOCKED), false),
                })
            }
        }
    }

    /// The caller's store under the request capture, with its projection
    /// retained so the saved settings show in status. A refused capture, or
    /// no authenticated Reader, is its reply.
    fn fan_stop_storage(
        &self,
        mailbox: &MailboxSource,
        sid: &str,
        session: u32,
    ) -> io::Result<Result<Arc<Storage>, Outcome>> {
        let MailboxSource::Reader(reader) = mailbox else {
            return Ok(Err(Outcome::blocked(
                "Authenticated thermal custody is unavailable.",
            )));
        };
        reader.custody().verify_integrity()?;
        let (storage, _fresh) = match self.capture_intent(sid, session, reader.custody()) {
            Ok(captured) => captured,
            Err(error) if is_fatal_admission(&error) => return Err(error),
            Err(error) => {
                #[cfg(feature = "service-profile-triggers-v1")]
                if is_competing_controller(&error) {
                    self.drop_for_controller(sid, session);
                }
                return Ok(Err(Outcome::blocked(format!(
                    "Saved configuration or the current session is unavailable: {error}"
                ))));
            }
        };
        reader.retain_policy(self.thermal_policy(sid.to_owned(), session, reader.custody()));
        Ok(Ok(storage))
    }

    /// The private Fan Stop projection of `sid` in `session`: the saved
    /// settings and managed's automation state for them.
    pub(super) fn project_fan_stop(
        &self,
        projected: Snapshot,
        store: &Storage,
        sid: &str,
        session: u32,
    ) -> io::Result<Snapshot> {
        let settings = store.fan_stop_settings()?;
        let (armed, reason) = self.fan_stop.borrow().view(sid, session);
        Ok(projected.with_fan_stop_automation(&settings, armed, reason))
    }
}

fn stored(message: &str) -> Outcome {
    Outcome {
        status: STORED,
        message: message.into(),
    }
}

fn failed(message: String) -> Outcome {
    Outcome {
        status: FAILED,
        message,
    }
}

/// Every error a store save returns comes from a read before its
/// transaction, so nothing was written.
fn unreadable(error: &io::Error) -> String {
    format!(
        "The requested intent was rejected: The saved configuration could not be read: {error}. The saved configuration is unchanged."
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn automation_follows_managed_eligibility() {
        let mut automation = Automation::default();
        // Nothing has acted: every pair sees the activation pause.
        assert_eq!(automation.view("S-1", 1), (false, pause::SESSION_CHANGED));
        automation.activate("S-1", 1);
        assert_eq!(automation.view("S-1", 1), (false, pause::SESSION_CHANGED));
        automation.arm();
        assert_eq!(automation.view("S-1", 1), (true, pause::NONE));
        // Another session, or another user, sees its own activation pause,
        // and acting pauses the owner's arming.
        assert_eq!(automation.view("S-1", 2), (false, pause::SESSION_CHANGED));
        assert_eq!(automation.view("S-2", 1), (false, pause::SESSION_CHANGED));
        automation.activate("S-1", 1);
        assert_eq!(automation.view("S-1", 1), (true, pause::NONE));
        automation.activate("S-2", 1);
        automation.activate("S-1", 1);
        assert_eq!(automation.view("S-1", 1), (false, pause::SESSION_CHANGED));
        assert!(automation.pause(pause::MANUAL_AUTO));
        assert!(!automation.pause(pause::MANUAL_AUTO));
        assert_eq!(automation.view("S-1", 1), (false, pause::MANUAL_AUTO));
        automation.arm();
        assert!(automation.pause(pause::NONE));
        assert_eq!(automation.view("S-1", 1), (false, pause::NONE));
    }
}

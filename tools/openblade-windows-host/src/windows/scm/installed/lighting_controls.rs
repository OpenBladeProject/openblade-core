//! Managed `StepKeyboardBrightnessAsync` and `SetLightingRefreshRateAsync`
//! on exact RZ09-0581 (slice L3), with the lighting member of managed
//! `ResolveHardwareDesired` and `ResolveVisibleLightingBaseline`. Both need
//! the saved preferences and profiles, so both are served only with the
//! profile store.
//!
//! A step runs one job under the operation gate: the job classifies power
//! fresh (`Reader::lighting_power`), and the permit admits that class and
//! resolves the desired lighting there (`desired_lighting`) before the job
//! reads or writes any lighting:
//!
//! - desired lighting that is Off ends the job there. A decrease replies
//!   managed's minimum; an increase writes managed's visible baseline at 6%
//!   through `setLightingOverride`'s own path (`Runtime::set_lighting`), with
//!   its own fresh admission and reply;
//! - otherwise the job reads the brightness (or reuses the raw value the
//!   last step set) and, unless the step leaves it unchanged, sends the 03/03
//!   setter with its exact acknowledgement and no readback. The resulting
//!   brightness is then saved as the override with the request's lifetime,
//!   and as `LastKnownLighting`.
//!
//! A step whose setter may have been submitted unconfirmed latches the
//! lighting doubt, as every lighting job does.
use super::stream::ticks;
use super::*;
use crate::{
    discovery::performance::StepTarget,
    installed::readiness::thermal_storage::{Foreground, PreferencesSave},
    service_status::LIGHTING_PERSISTENT,
};
use openblade_domain::profile_set::LightingSettings;

/// Managed `LightingSettingsFactory.DefaultVisibleColor`.
const DEFAULT_VISIBLE_COLOR: RgbColor = RgbColor {
    red: 0x47,
    green: 0xE1,
    blue: 0x0C,
};
/// Managed's brightness for a step that wakes the keyboard from Off.
const WAKE_BRIGHTNESS: u8 = 6;

const MINIMUM: &str = "Keyboard brightness is already at its minimum.";
const LOW_POWER: &str = "Keyboard brightness input is deferred during a low-power transition.";
/// Native's refusal when no saved profile or override names the lighting to
/// step: managed always resolves its default profiles.
const UNRESOLVED: &str =
    "The saved lighting could not be resolved; keyboard brightness was not changed.";
const RATE_UNAVAILABLE: &str =
    "The lighting refresh rate cannot change while laptop controls are unavailable.";
/// Managed `ConfigurationQuarantineBlockedMessage`.
const QUARANTINE_BLOCKED: &str = "OpenBlade preserved an unsupported or invalid configuration. Thermal settings are read-only until the service restarts with a valid configuration.";

/// Managed `LightingBrightnessProtocol.RawToPercent`: `raw * 100 / 255`
/// rounded to the nearest whole percent (no tie is possible).
pub(in super::super::super) fn raw_to_percent(raw: u8) -> u8 {
    ((u32::from(raw) * 200 + 255) / 510) as u8
}

/// The lighting member of managed `ResolveHardwareDesired` at power class
/// `power`: the override in effect there, otherwise the profile's lighting
/// with the active application's override applied. `None` when neither is
/// saved. Managed's startup temporary from `LastKnownLighting` is not
/// resolved.
pub(super) fn desired_lighting(
    storage: &Storage,
    application: &Foreground,
    power: i32,
) -> io::Result<Option<LightingSettings>> {
    if let Some(value) = storage.verified_lighting_override(power)? {
        return Ok(Some(value.settings));
    }
    let layer = storage.profile_layer(application)?;
    Ok(layer
        .at(power)?
        .and_then(|resolution| resolution.profile.lighting.clone()))
}

/// Managed `ResolveVisibleLightingBaseline`: the remembered lit effect, the
/// base power profile's when it is lit (managed
/// `Automation.Profiles.ForPowerSource`, without the application layer), or
/// Static in the default color at 6%. An unclassified adapter has no
/// confirmed lit lighting to fall back on here.
fn visible_baseline(storage: &Storage, power: i32) -> io::Result<LightingSettings> {
    let lit = |lighting: &LightingSettings| {
        lighting.effect != LightingEffect::OFF && lighting.brightness > 0
    };
    if let Some(remembered) = storage.last_known_lighting()?.filter(|l| lit(l)) {
        return Ok(remembered);
    }
    let profile = if power == UNCLASSIFIED_EXTERNAL {
        None
    } else {
        storage
            .profile_layer(&Foreground::default())?
            .at(power)?
            .and_then(|resolution| resolution.profile.lighting.clone())
    };
    Ok(profile.filter(lit).unwrap_or_else(|| {
        LightingSettings::of(
            LightingEffect::STATIC,
            DEFAULT_VISIBLE_COLOR,
            WAKE_BRIGHTNESS,
            LightingParameters::Static {
                color: DEFAULT_VISIBLE_COLOR,
            },
        )
    }))
}

/// A step permit's power admission (`LightingPermit::step`): the foreground
/// the desired lighting resolves for, and what the store resolved.
pub(super) struct StepState {
    pub(super) application: Foreground,
    /// `Some` once the store resolved the desired lighting at the job's
    /// power class: `None` inside when nothing is saved.
    pub(super) desired: Mutex<Option<Option<LightingSettings>>>,
}

impl StepState {
    /// Whether the desired lighting the store resolved is lit, so the job
    /// steps the brightness.
    pub(super) fn lit(&self) -> bool {
        self.desired.lock().is_ok_and(|desired| {
            desired
                .as_ref()
                .and_then(Option::as_ref)
                .is_some_and(|lighting| lighting.effect != LightingEffect::OFF)
        })
    }

    fn desired(&self) -> Option<Option<LightingSettings>> {
        self.desired.lock().ok().and_then(|desired| desired.clone())
    }
}

/// The request as `step_keyboard_brightness` reads it.
pub(in super::super::super) struct StepRequest {
    pub(in super::super::super) caller: Caller,
    pub(in super::super::super) sid: String,
    pub(in super::super::super) increase: bool,
    /// Managed `OverrideLifetime`: 0 until the power source changes, 1
    /// persistent.
    pub(in super::super::super) lifetime: i32,
    pub(in super::super::super) deadline: Instant,
}

/// What the step under the gate leaves for the caller.
enum Stepped {
    Done(Lit),
    /// The desired lighting is Off and the step increases: this override,
    /// written through `set_lighting` after the gate is released.
    FromOff(LightingOverride),
}

impl Runtime {
    /// Managed `StepKeyboardBrightnessAsync` (module docs).
    pub(in super::super::super) async fn step_keyboard_brightness(
        &self,
        mailbox: MailboxSource,
        request: StepRequest,
    ) -> io::Result<Lit> {
        let (caller, sid, deadline) = (request.caller, request.sid.clone(), request.deadline);
        match self.step_locked(&mailbox, request).await? {
            Stepped::Done(lit) => Ok(lit),
            Stepped::FromOff(value) => {
                self.set_lighting(
                    mailbox,
                    LightingRequest {
                        caller,
                        sid,
                        value: Some(value),
                        deadline,
                    },
                )
                .await
            }
        }
    }

    async fn step_locked(
        &self,
        mailbox: &MailboxSource,
        request: StepRequest,
    ) -> io::Result<Stepped> {
        let StepRequest {
            caller,
            sid,
            increase,
            lifetime,
            deadline,
        } = request;
        let done = |outcome| Ok(Stepped::Done(Lit::refused(outcome)));
        let _serial = self.gate.lock().await;
        verify_retained_failure(&self.fatal)?;
        if caller.logon != Logon::Interactive || caller.control_session != Some(caller.session) {
            return done(Outcome::blocked(ACTIVE_USER));
        }
        let MailboxSource::Reader(reader) = mailbox else {
            return done(Outcome::blocked(CUSTODY));
        };
        reader.custody().verify_integrity()?;
        let (storage, fresh) = match self.capture_intent(&sid, caller.session, reader.custody()) {
            Ok(captured) => captured,
            Err(error) if is_fatal_admission(&error) => return Err(error),
            Err(error) => return done(unavailable(&error)),
        };
        fresh.verify_health()?;
        reader.retain_policy(self.thermal_policy(sid.clone(), caller.session, reader.custody()));
        if self.lighting_low_power() {
            return done(Outcome::blocked(LOW_POWER));
        }
        if self.lighting.latched() {
            return Ok(Stepped::Done(
                self.resolve_lighting_doubt(reader, storage, fresh, deadline)
                    .await?,
            ));
        }
        match storage.lighting_delegated() {
            Ok(false) => {}
            Ok(true) => return done(Outcome::blocked(DELEGATED)),
            Err(error) if is_fatal_admission(&error) => return Err(error),
            Err(error) => return done(unavailable(&error)),
        }
        if self.crossed_low_power() {
            self.retire_renderer();
        }
        let application = self.foreground(&sid, caller.session);
        let claim = self.lighting.claim();
        let permit = LightingPermit::new(
            self,
            fresh,
            storage.clone(),
            LightingJob::step(StepTarget {
                increase,
                cached: self.lighting.raw.get(),
            }),
            Intent::Step(StepState {
                application: application.clone(),
                desired: Mutex::new(None),
            }),
        );
        let result = reader.mutate(permit.clone(), deadline).await;
        verify_retained_failure(&self.fatal)?;
        if let Err(error) = result
            && is_fatal_admission(&error)
        {
            return Err(error);
        }
        let outcome = permit.job.outcome();
        claim.settle(outcome.in_doubt());
        let power = match permit.admission.lock().ok().and_then(|mut a| a.take()) {
            Some(Admission::Stored(power)) => power,
            Some(Admission::Refused(outcome)) => return done(outcome),
            None => {
                let refusal = match outcome {
                    LightingOutcome::Unclassified => UNCLASSIFIED_BLOCKED,
                    _ => ADMISSION,
                };
                return done(Outcome::blocked(refusal));
            }
        };
        let desired = permit.step().and_then(StepState::desired);
        let lighting = match desired {
            Some(Some(lighting)) => lighting,
            // The store could not resolve it, or nothing is saved.
            _ => return done(Outcome::blocked(UNRESOLVED)),
        };
        if lighting.effect == LightingEffect::OFF {
            if !increase {
                return done(Outcome {
                    status: APPLIED,
                    message: MINIMUM.into(),
                });
            }
            let baseline = match visible_baseline(&storage, power) {
                Ok(baseline) => baseline,
                Err(error) if is_fatal_admission(&error) => return Err(error),
                Err(error) => return done(unavailable(&error)),
            };
            let visible = LightingSettings {
                brightness: WAKE_BRIGHTNESS,
                logo_mode: lighting.logo_mode.or(baseline.logo_mode),
                ..baseline
            };
            return Ok(Stepped::FromOff(LightingOverride {
                settings: visible,
                lifetime,
            }));
        }
        let raw = match outcome {
            LightingOutcome::Stepped | LightingOutcome::Observed => permit
                .job
                .final_reading()
                .and_then(|reading| reading.brightness),
            _ => None,
        };
        let Some(raw) = raw else {
            self.lighting.raw.set(None);
            let reply = reply(&permit.job);
            // Managed's known-not-applied write is Blocked.
            let status = if outcome.in_doubt() { reply.status } else { 2 };
            return Ok(Stepped::Done(Lit {
                outcome: Outcome {
                    status,
                    message: reply.message,
                },
                publish: permit.job.confirmation() != LightingConfirmation::default(),
                confirmation: permit.job.confirmation(),
            }));
        };
        if outcome == LightingOutcome::Stepped {
            self.lighting.raw.set(Some(raw));
        }
        let percent = raw_to_percent(raw);
        let next = if percent == 0 {
            LightingSettings {
                logo_mode: lighting.logo_mode,
                ..LightingSettings::off()
            }
        } else {
            LightingSettings {
                brightness: percent,
                ..lighting
            }
        };
        if raw == 0 {
            // Managed stops the stream and ends the renderer at zero.
            self.retire_renderer();
        }
        let confirmation = permit.job.confirmation();
        let value = LightingOverride {
            settings: next.clone(),
            lifetime,
        };
        if let Err(error) = storage.save_lighting(Some(&value), power) {
            if is_fatal_admission(&error) {
                return Err(error);
            }
            return Ok(Stepped::Done(Lit {
                outcome: Outcome {
                    status: FAILED,
                    message: format!(
                        "Keyboard brightness changed, but its override could not be saved: {error}"
                    ),
                },
                publish: true,
                confirmation,
            }));
        }
        remember_lighting(&storage, &next);
        Ok(Stepped::Done(Lit {
            outcome: Outcome {
                status: APPLIED,
                message: format!("Keyboard brightness changed to {percent}%."),
            },
            publish: true,
            confirmation,
        }))
    }

    /// Managed `SetLightingRefreshRateAsync` for the 30 and 60 Hz table:
    /// the preference is saved under the request's fresh admission, then
    /// the animated cadence follows it from the current phase, as managed
    /// stops and restarts its stream without resetting the effect's start.
    /// Native's renderer update cannot fail after the save, so managed's
    /// rollback replies have no native path.
    pub(in super::super::super) async fn set_lighting_refresh_rate(
        &self,
        mailbox: &MailboxSource,
        caller: Caller,
        sid: &str,
        rate: RefreshRate,
        deadline: Instant,
    ) -> io::Result<Lit> {
        let _serial = self.gate.lock().await;
        verify_retained_failure(&self.fatal)?;
        let refused = |message: &str| Ok(Lit::refused(Outcome::blocked(message)));
        if caller.logon != Logon::Interactive || caller.control_session != Some(caller.session) {
            return refused(ACTIVE_USER);
        }
        let MailboxSource::Reader(reader) = mailbox else {
            return refused(CUSTODY);
        };
        reader.custody().verify_integrity()?;
        let (storage, fresh) = match self.capture_intent(sid, caller.session, reader.custody()) {
            Ok(captured) => captured,
            Err(error) if is_fatal_admission(&error) => return Err(error),
            Err(_) => return refused(RATE_UNAVAILABLE),
        };
        fresh.verify_health()?;
        reader.retain_policy(self.thermal_policy(sid.into(), caller.session, reader.custody()));
        match storage.lighting_delegated() {
            Ok(false) => {}
            Ok(true) => return refused(DELEGATED),
            Err(error) if is_fatal_admission(&error) => return Err(error),
            Err(_) => return refused(RATE_UNAVAILABLE),
        }
        if fresh.authorize().is_none() {
            return refused(RATE_UNAVAILABLE);
        }
        // Managed refuses the change on an unclassified adapter that fresh
        // evidence does not admit for lighting (ServiceControlRuntime.cs
        // 4770-4777): the change classifies power as a lighting job does,
        // and stores nothing there (`Intent::Admit`).
        let permit = LightingPermit::new(
            self,
            fresh,
            storage.clone(),
            LightingJob::store(),
            Intent::Admit,
        );
        let result = reader.mutate(permit.clone(), deadline).await;
        verify_retained_failure(&self.fatal)?;
        if let Err(error) = result
            && is_fatal_admission(&error)
        {
            return Err(error);
        }
        if !matches!(
            permit.admission.lock().ok().and_then(|mut a| a.take()),
            Some(Admission::Stored(_))
        ) {
            return refused(RATE_UNAVAILABLE);
        }
        let saved = match storage.save_lighting_preferences(|preferences| {
            let changed = preferences.lighting_refresh_rate != rate.wire();
            preferences.lighting_refresh_rate = rate.wire();
            changed
        }) {
            Ok(saved) => saved,
            Err(error) if is_fatal_admission(&error) => return Err(error),
            Err(error) => {
                PreferencesSave::Rejected(format!("The requested intent was rejected: {error}"))
            }
        };
        let publish = match saved {
            PreferencesSave::Stored(_) => true,
            PreferencesSave::Unchanged(_) => false,
            PreferencesSave::Quarantined => return refused(QUARANTINE_BLOCKED),
            PreferencesSave::Rejected(message) => {
                return Ok(Lit::refused(Outcome {
                    status: FAILED,
                    message,
                }));
            }
        };
        self.retime_stream(rate, Instant::now());
        Ok(Lit {
            outcome: Outcome {
                status: APPLIED,
                message: format!("Keyboard lighting is running at {}.", rate.label()),
            },
            publish,
            confirmation: LightingConfirmation::default(),
        })
    }

    /// The animated cadence at `rate` from `now`: managed's restarted
    /// stream schedules its first frame one delay after the elapsed time,
    /// keeping the effect's start and so its phase.
    fn retime_stream(&self, rate: RefreshRate, now: Instant) {
        let lighting = &self.lighting;
        lighting.rate.set(rate);
        let mut owner = lighting.owner.borrow_mut();
        if let Some(owner) = owner.as_mut()
            && let Some(stream) = owner.stream.as_mut()
        {
            let elapsed = ticks(stream.started, now);
            stream.next = elapsed + owner.renderer.effect.next_delay(elapsed, rate);
            lighting.wake.notify_one();
        }
    }
}

/// Managed `TryPersistLastKnownLightingAsync`: a failure is recorded by
/// managed's history and never changes the reply.
pub(super) fn remember_lighting(storage: &Storage, lighting: &LightingSettings) {
    let _ = storage.save_lighting_preferences(|preferences| {
        if preferences.last_known_lighting.as_ref() == Some(lighting) {
            return false;
        }
        preferences.last_known_lighting = Some(lighting.clone());
        true
    });
}

/// Managed `OverrideLifetime`'s wire values (`StepKeyboardBrightnessRequest`).
pub(in super::super::super) fn lifetime(wire: i32) -> Option<i32> {
    match wire {
        0 => Some(0),
        1 => Some(LIGHTING_PERSISTENT),
        _ => None,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::discovery::performance::BRIGHTNESS_STEP;

    #[test]
    fn raw_to_percent_rounds_as_managed() {
        // Math.Round(raw * 100d / 255).
        for raw in 0..=255u8 {
            let expected = (f64::from(raw) * 100.0 / 255.0).round() as u8;
            assert_eq!(raw_to_percent(raw), expected, "{raw}");
        }
        assert_eq!(raw_to_percent(BRIGHTNESS_STEP), 6);
        assert_eq!(raw_to_percent(0xff), 100);
    }
}

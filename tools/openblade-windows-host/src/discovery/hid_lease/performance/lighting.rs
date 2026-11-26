//! The one keyboard lighting recipe a mutation Reader runs for the Service's
//! `setLightingOverride` composition on exact RZ09-0581 (`apply_lighting`):
//! the 03/83 brightness and 03/80/03/82 logo queries, and the reviewed
//! 03/03, 03/02 and 03/00 setters, each with its exact acknowledgement and an
//! independent readback. Managed `ControlCoordinator.ApplyLightingOutcomeAsync`
//! over `ProductionLightingPort.SetLightingAsync` for a request this
//! composition admits; it carries none of the host-rendered matrix commands.
//!
//! Every exchange is preceded by a fresh admission checkpoint. A setter whose
//! acknowledgement or readback is not confirmed, and any incomplete exchange,
//! ends the job with the Reader sealed: nothing is sent after it, and the
//! uncertain setter is never replayed. Only a readback that confirms a
//! different value is a known mismatch, after which the job restores the
//! confirmed baseline once.
use super::*;
use openblade_protocol::{
    RazerReport,
    lighting::{BrightnessQuery, LightingSet, LogoEffectQuery, LogoMode, LogoStateQuery},
};
use std::sync::Mutex;

/// Managed `OwnedRendererOffObservation.OffConfirmationDelay`: the second
/// zero-brightness sample's distance from the first.
pub(crate) const OFF_CONFIRMATION_DELAY: Duration = Duration::from_millis(750);

/// Managed `QueryLogoModeCoreAsync`'s refusal of an undecodable pair.
const LOGO_MALFORMED: &str = "Logo lighting response was malformed or unsupported.";
const BRIGHTNESS_UNREAD: &str = "Lighting brightness could not be read.";
const LOGO_UNREAD: &str = "Logo lighting could not be read.";
/// Managed `SetBrightnessAndVerifyCoreAsync` and `SetLogoModeAndVerifyCoreAsync`.
const BRIGHTNESS_MISMATCH: &str = "Lighting brightness did not match independent readback.";
const LOGO_MISMATCH: &str = "Logo lighting did not match independent readback.";
const BRIGHTNESS_IN_DOUBT: &str =
    "A brightness command may have been submitted and was not confirmed.";
const LOGO_IN_DOUBT: &str = "A logo command may have been submitted and was not confirmed.";

/// What a read confirmed. `None` is not confirmed: not read, no valid reply,
/// or, for the logo, a pair outside the reviewed contract.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub(crate) struct LightingReading {
    /// The 03/83 raw brightness.
    pub(crate) brightness: Option<u8>,
    /// The 03/80 state and 03/82 effect, decoded together.
    pub(crate) logo: Option<LogoMode>,
}

/// What a write job sets: the raw brightness and, when requested, the logo.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) struct LightingTarget {
    pub(crate) brightness: u8,
    pub(crate) logo: Option<LogoMode>,
}

impl LightingTarget {
    fn matches(&self, reading: LightingReading) -> bool {
        reading.brightness == Some(self.brightness)
            && self.logo.is_none_or(|logo| reading.logo == Some(logo))
    }
}

/// How far one job got (`LightingJob::outcome`). The owner reads it after the
/// job, so a job that stops at any point, cancellation included, leaves the
/// outcome that says what may have reached the firmware.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) enum LightingOutcome {
    /// Nothing was sent: the job never ran on a bound, admitted device, or
    /// its first checkpoint refused.
    Pending,
    /// A baseline query may have been sent and the baseline was not
    /// confirmed, for this reason. No setter was sent.
    Unread(&'static str),
    /// The baseline was confirmed and the job wrote nothing: a read-only
    /// job, or one whose baseline already matched (managed `Unchanged`).
    Observed,
    /// The baseline admitted a write, but too little time remained for the
    /// recipe at the measured pace (`Pace::covers`). No setter was sent.
    Unpaced,
    /// A fresh admission checkpoint refused the first setter. Nothing was
    /// sent after the baseline.
    Refused,
    /// A fresh admission checkpoint refused the logo setters after the
    /// brightness setter was confirmed by its readback. Nothing is in doubt;
    /// the confirmed brightness change stays.
    Interrupted,
    /// A setter may have been submitted and its exact acknowledgement or its
    /// readback was not confirmed, for this reason. Nothing more was sent.
    InDoubt(&'static str),
    /// A readback confirmed a value other than the target, for this reason,
    /// and the job then restored the baseline: `Some(true)` when every
    /// restore was confirmed, `Some(false)` when a restore readback confirmed
    /// another value, and `None` when a restore is itself in doubt.
    Mismatched {
        failure: &'static str,
        restored: Option<bool>,
    },
    /// Every setter was confirmed by its own readback.
    Written,
}

/// One lighting job, carried by its permit (`mutation::Permit::lighting`): a
/// baseline read, then, for a target the baseline does not match, the
/// brightness and logo writes, and a final aggregate read (`final_reading`).
/// `None` reads the baseline and writes nothing: the read-only resolution of
/// an earlier job in doubt.
pub(crate) struct LightingJob {
    target: Option<LightingTarget>,
    /// Managed `OwnedRendererOffObservation.RequiresOffConfirmation`: a zero
    /// brightness confirms Off only after a second zero sample, 750 ms later.
    off_confirmation: bool,
    state: Mutex<(
        LightingOutcome,
        Option<LightingReading>,
        Option<LightingReading>,
    )>,
}

impl LightingJob {
    pub(crate) fn new(target: Option<LightingTarget>, off_confirmation: bool) -> Self {
        Self {
            target,
            off_confirmation,
            state: Mutex::new((LightingOutcome::Pending, None, None)),
        }
    }

    /// A finished job that reached `outcome` with `final_reading`, for the
    /// runtime's reply tests.
    #[cfg(test)]
    pub(crate) fn recorded(
        outcome: LightingOutcome,
        final_reading: Option<LightingReading>,
    ) -> Self {
        Self {
            target: None,
            off_confirmation: false,
            state: Mutex::new((outcome, None, final_reading)),
        }
    }

    /// The last outcome the job reached. A poisoned record is the most
    /// conservative one: a setter may have been submitted.
    pub(crate) fn outcome(&self) -> LightingOutcome {
        self.state
            .lock()
            .map_or(LightingOutcome::InDoubt(BRIGHTNESS_IN_DOUBT), |state| {
                state.0
            })
    }

    /// The confirmed baseline, when the job read one.
    pub(crate) fn baseline(&self) -> Option<LightingReading> {
        self.state.lock().ok().and_then(|state| state.1)
    }

    /// The final aggregate read after a confirmed write, or the baseline of a
    /// job that wrote nothing: managed `ReadLightingStateAsync`.
    pub(crate) fn final_reading(&self) -> Option<LightingReading> {
        self.state.lock().ok().and_then(|state| state.2)
    }

    fn record(&self, outcome: LightingOutcome) {
        if let Ok(mut state) = self.state.lock() {
            state.0 = outcome;
        }
    }

    fn observe(&self, baseline: Option<LightingReading>, last: Option<LightingReading>) {
        if let Ok(mut state) = self.state.lock() {
            state.1 = state.1.or(baseline);
            state.2 = last;
        }
    }
}

/// Every transaction ID a job may send, reserved before the first: the
/// baseline (4, with a second zero sample), the brightness setter and its
/// readback (2), the logo setters and readback (4), their restores (6) and
/// the final read (4).
const IDS: usize = 20;

/// One running job: its reserved IDs, handed out in order, and the job whose
/// outcome it records.
struct Run<'j> {
    ids: [u8; IDS],
    next: usize,
    job: &'j LightingJob,
}

impl Run<'_> {
    fn take(&mut self) -> u8 {
        let id = self.ids[self.next];
        self.next += 1;
        id
    }
}

/// The fixed time `Pace::covers` charges beyond the remaining exchanges: the
/// slowest exchange again, and the trailing checkpoint no reply-to-reply
/// exchange includes, at the baseline's own, from its reply to `now`, as the
/// Driver-mode setter's pace does.
fn trailing(exchanges: &Exchanges, now: Instant) -> Duration {
    let trailing = exchanges
        .last
        .map_or(Duration::ZERO, |last| now.saturating_duration_since(last));
    exchanges.slowest.saturating_add(trailing)
}

/// The exchanges of one logo write: the effect for a lit mode, the state and
/// the two readback queries.
fn logo_exchanges(mode: Option<LogoMode>) -> u32 {
    match mode {
        Some(LogoMode::Off) => 3,
        Some(_) => 4,
        None => 0,
    }
}

impl<P: Port> Reader<P, ReadAdmission> {
    /// The lighting recipe (`LightingJob`). The IDs are not pinned: no record
    /// names them, and in this process each sent ID stays quarantined until a
    /// correlated reply is read, as every unjournaled exchange's does; the
    /// unsent ones are freed at retirement.
    pub(super) async fn apply_lighting(
        &mut self,
        permit: &dyn mutation::Permit,
        job: &LightingJob,
        limit: Limit<'_>,
    ) {
        let _ = self.run_lighting(permit, job, limit).await;
    }

    async fn run_lighting(
        &mut self,
        permit: &dyn mutation::Permit,
        job: &LightingJob,
        limit: Limit<'_>,
    ) -> Option<()> {
        let mut authorize = || permit.authorize();
        let authorize: Authorize<'_, ReadAdmission> = &mut authorize;
        let mut ids = [0; IDS];
        for pair in ids.as_chunks_mut::<2>().0 {
            pair.copy_from_slice(&self.reserve_pair()?);
        }
        let run = &mut Run { ids, next: 0, job };
        let admission = self.admission;
        let pace = mutation::Pace::start(self);
        let mut attempt = Attempt {
            reader: self,
            completed: false,
        };
        attempt.checkpoint(authorize, admission, limit).ok()?;
        // From the call that may send it, the baseline is in doubt; no
        // setter has been sent.
        job.record(LightingOutcome::Unread(BRIGHTNESS_UNREAD));
        let raw = attempt
            .brightness(authorize, admission, limit, run, job.off_confirmation)
            .await?;
        let mut baseline = LightingReading {
            brightness: Some(raw),
            logo: None,
        };
        if job.target.is_none_or(|target| target.logo.is_some()) {
            job.record(LightingOutcome::Unread(LOGO_UNREAD));
            let Some(logo) = attempt.logo(authorize, admission, limit, run).await? else {
                // Both replies correlated, and their pair is outside the
                // contract: nothing is in doubt, but no write follows.
                job.record(LightingOutcome::Unread(LOGO_MALFORMED));
                attempt.completed = true;
                return Some(());
            };
            baseline.logo = Some(logo);
        }
        let Some(target) = job.target.filter(|target| !target.matches(baseline)) else {
            job.observe(Some(baseline), Some(baseline));
            job.record(LightingOutcome::Observed);
            attempt.completed = true;
            return Some(());
        };
        job.observe(Some(baseline), None);
        let writes_brightness = raw != target.brightness;
        let writes_logo = target.logo.is_some_and(|logo| baseline.logo != Some(logo));
        // The setters and their readbacks, then a restore of each or the
        // final read, whichever is longer, each charged at the measured pace,
        // with the trailing checkpoint and the off confirmation's wait as
        // fixed time. Too little time left is known not applied: the
        // baseline read leaves nothing in doubt.
        let logo = |mode| if writes_logo { logo_exchanges(mode) } else { 0 };
        let forward = u32::from(writes_brightness) * 2 + logo(target.logo);
        let restore = u32::from(writes_brightness) * 2 + logo(baseline.logo);
        let confirmation = if job.off_confirmation {
            OFF_CONFIRMATION_DELAY
        } else {
            Duration::ZERO
        };
        let fixed = trailing(&attempt.reader.exchanges, Instant::now()) + confirmation;
        if !pace.covers(attempt.reader, forward + restore.max(4), 0, fixed, limit) {
            job.record(LightingOutcome::Unpaced);
            attempt.completed = true;
            return Some(());
        }
        // Each setter starts only after its own fresh admission; a refusal
        // there sends nothing more and leaves nothing in doubt.
        let mut failure = None;
        if writes_brightness {
            if attempt.checkpoint(authorize, admission, limit).is_err() {
                job.record(LightingOutcome::Refused);
                return None;
            }
            let in_doubt = LightingOutcome::InDoubt(BRIGHTNESS_IN_DOUBT);
            if !attempt
                .set_brightness(
                    authorize,
                    admission,
                    limit,
                    run,
                    target.brightness,
                    in_doubt,
                )
                .await?
            {
                failure = Some(BRIGHTNESS_MISMATCH);
            }
        }
        let mut wrote_logo = false;
        if failure.is_none()
            && writes_logo
            && let Some(mode) = target.logo
        {
            if attempt.checkpoint(authorize, admission, limit).is_err() {
                job.record(if writes_brightness {
                    LightingOutcome::Interrupted
                } else {
                    LightingOutcome::Refused
                });
                return None;
            }
            wrote_logo = true;
            let in_doubt = LightingOutcome::InDoubt(LOGO_IN_DOUBT);
            if !attempt
                .set_logo(authorize, admission, limit, run, mode, in_doubt)
                .await?
            {
                failure = Some(LOGO_MISMATCH);
            }
        }
        if let Some(failure) = failure {
            // A confirmed mismatch: restore the confirmed baseline once,
            // brightness first, as managed's rollback does. A restore that is
            // itself in doubt stops here; a refused admission restores
            // nothing more.
            let in_doubt = LightingOutcome::Mismatched {
                failure,
                restored: None,
            };
            job.record(in_doubt);
            let mut restored = true;
            if writes_brightness {
                restored = attempt.checkpoint(authorize, admission, limit).is_ok()
                    && attempt
                        .set_brightness(authorize, admission, limit, run, raw, in_doubt)
                        .await?;
            }
            if wrote_logo && let Some(mode) = baseline.logo {
                let logo = attempt.checkpoint(authorize, admission, limit).is_ok()
                    && attempt
                        .set_logo(authorize, admission, limit, run, mode, in_doubt)
                        .await?;
                restored &= logo;
            }
            job.record(LightingOutcome::Mismatched {
                failure,
                restored: Some(restored),
            });
            attempt.completed = true;
            return Some(());
        }
        job.record(LightingOutcome::Written);
        // Managed's final aggregate read: an incomplete one leaves the state
        // unconfirmed, never in doubt, since every setter was confirmed.
        let mut reading = LightingReading {
            brightness: attempt
                .brightness(authorize, admission, limit, run, job.off_confirmation)
                .await,
            logo: None,
        };
        if reading.brightness.is_some() {
            reading.logo = attempt
                .logo(authorize, admission, limit, run)
                .await
                .flatten();
        }
        job.observe(None, Some(reading));
        attempt.completed = reading.logo.is_some();
        Some(())
    }
}

impl<P: Port> Attempt<'_, P, ReadAdmission> {
    /// One exchange of `request`, after its own checkpoint: the report bytes
    /// of a reply with a valid envelope that correlates with `request`, or
    /// `None` when that was not confirmed.
    async fn exchange(
        &mut self,
        authorize: Authorize<'_, ReadAdmission>,
        admission: ReadAdmission,
        limit: Limit<'_>,
        request: &RazerReport,
        setter: bool,
    ) -> Option<[u8; REPORT_LENGTH]> {
        self.checkpoint(authorize, admission, limit).ok()?;
        let mut frame = [0; FRAME_LENGTH];
        frame[1..].copy_from_slice(&request.encode());
        let transfer = if setter {
            Transfer::SendMutation(frame)
        } else {
            Transfer::SendGetter(frame)
        };
        self.transfer(transfer, limit).await.ok()?;
        limit.wait(SETTLE).await.ok()?;
        self.checkpoint(authorize, admission, limit).ok()?;
        let (length, reply) = self.transfer(Transfer::ReceiveReply, limit).await.ok()?;
        if !matches!(length, REPORT_LENGTH | FRAME_LENGTH) || reply[0] != 0 {
            return None;
        }
        let report: [u8; REPORT_LENGTH] = reply[1..].try_into().ok()?;
        request.validate_response(&report, None).ok()?;
        Some(report)
    }

    /// Managed `ReadStateCoreAsync`'s brightness: one 03/83 query and, when a
    /// zero must be confirmed twice and was read, a second one 750 ms later,
    /// whose value is the reading. `None` when a query was not confirmed.
    async fn brightness(
        &mut self,
        authorize: Authorize<'_, ReadAdmission>,
        admission: ReadAdmission,
        limit: Limit<'_>,
        run: &mut Run<'_>,
        off_confirmation: bool,
    ) -> Option<u8> {
        let query = BrightnessQuery::new(run.take());
        let report = self
            .exchange(authorize, admission, limit, query.request(), false)
            .await?;
        let raw = query.decode(&report).ok()?;
        if raw != 0 || !off_confirmation {
            return Some(raw);
        }
        limit.wait(OFF_CONFIRMATION_DELAY).await.ok()?;
        let query = BrightnessQuery::new(run.take());
        let report = self
            .exchange(authorize, admission, limit, query.request(), false)
            .await?;
        query.decode(&report).ok()
    }

    /// The 03/80 and 03/82 pair: `Some(None)` when both replies correlated
    /// and their pair is outside the reviewed contract (managed's content
    /// rejection), `None` when an exchange was not confirmed.
    async fn logo(
        &mut self,
        authorize: Authorize<'_, ReadAdmission>,
        admission: ReadAdmission,
        limit: Limit<'_>,
        run: &mut Run<'_>,
    ) -> Option<Option<LogoMode>> {
        let state = LogoStateQuery::new(run.take());
        let report = self
            .exchange(authorize, admission, limit, state.request(), false)
            .await?;
        let enabled = state.decode(&report).ok();
        let effect = LogoEffectQuery::new(run.take());
        let report = self
            .exchange(authorize, admission, limit, effect.request(), false)
            .await?;
        let effect = effect.decode(&report).ok();
        Some(
            enabled
                .zip(effect)
                .and_then(|(enabled, effect)| LogoMode::decode(enabled, effect)),
        )
    }

    /// One setter and its exact acknowledgement. `None` leaves it in doubt.
    async fn submit(
        &mut self,
        authorize: Authorize<'_, ReadAdmission>,
        admission: ReadAdmission,
        limit: Limit<'_>,
        set: LightingSet,
    ) -> Option<()> {
        let report = self
            .exchange(authorize, admission, limit, set.request(), true)
            .await?;
        set.acknowledge(&report).ok()
    }

    /// Managed `SetBrightnessAndVerifyCoreAsync`: the 03/03 setter, its
    /// acknowledgement and the 03/83 readback, and whether that readback
    /// confirmed `raw`. `None` leaves the setter in doubt.
    async fn set_brightness(
        &mut self,
        authorize: Authorize<'_, ReadAdmission>,
        admission: ReadAdmission,
        limit: Limit<'_>,
        run: &mut Run<'_>,
        raw: u8,
        in_doubt: LightingOutcome,
    ) -> Option<bool> {
        run.job.record(in_doubt);
        let set = LightingSet::brightness(run.take(), raw);
        self.submit(authorize, admission, limit, set).await?;
        let readback = self
            .brightness(authorize, admission, limit, run, false)
            .await?;
        Some(readback == raw)
    }

    /// Managed `SetLogoModeAndVerifyCoreAsync`: the 03/02 effect for a lit
    /// mode, the 03/00 state, then the 03/80/03/82 readback, and whether it
    /// confirmed `mode`. `None` leaves a setter in doubt; so does a readback
    /// outside the contract, as nothing then confirms the logo.
    async fn set_logo(
        &mut self,
        authorize: Authorize<'_, ReadAdmission>,
        admission: ReadAdmission,
        limit: Limit<'_>,
        run: &mut Run<'_>,
        mode: LogoMode,
        in_doubt: LightingOutcome,
    ) -> Option<bool> {
        run.job.record(in_doubt);
        if mode != LogoMode::Off {
            let set = LightingSet::logo_effect(run.take(), mode);
            self.submit(authorize, admission, limit, set).await?;
        }
        let set = LightingSet::logo_state(run.take(), mode);
        self.submit(authorize, admission, limit, set).await?;
        let readback = self.logo(authorize, admission, limit, run).await??;
        Some(readback == mode)
    }
}

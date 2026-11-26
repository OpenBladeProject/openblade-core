//! The one keyboard lighting recipe a mutation Reader runs for the Service's
//! `setLightingOverride` composition on exact RZ09-0581 (`apply_lighting`):
//! the request's power classification and store, then the 03/83 brightness
//! and 03/80/03/82 logo queries, and the reviewed 03/03, 03/02 and 03/00
//! setters, each with its exact acknowledgement and an independent readback.
//! Managed `ExecuteScopedMutationAsync`'s power gate and store, then
//! `ControlCoordinator.ApplyLightingOutcomeAsync` over
//! `ProductionLightingPort.SetLightingAsync`, for a request this composition
//! admits; it carries none of the host-rendered matrix commands.
//!
//! Every exchange is preceded by a fresh admission checkpoint. A setter whose
//! acknowledgement or readback is not confirmed, and any incomplete exchange,
//! ends the job with the Reader sealed: nothing is sent after it, and the
//! uncertain setter is never replayed. Only a readback that confirms a
//! different value is a known mismatch, after which the job restores the
//! confirmed baseline once.
use super::*;
use openblade_domain::automation::UNCLASSIFIED_EXTERNAL;
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

/// What a job leaves the published confirmed lighting members, one member at
/// a time: `None` keeps the published value, since the job changed nothing
/// that member reports; `Some(None)` clears it, since a setter for it may
/// have been submitted or its last read was outside the contract; and
/// `Some(Some(value))` is a read that confirmed `value`.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub(crate) struct LightingConfirmation {
    pub(crate) brightness: Option<Option<u8>>,
    pub(crate) logo: Option<Option<LogoMode>>,
}

/// What a write job sets: the raw brightness and, when requested, the logo.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) struct LightingTarget {
    pub(crate) brightness: u8,
    pub(crate) logo: Option<LogoMode>,
}

impl LightingTarget {
    /// Whether `reading` confirms every member this target sets.
    pub(crate) fn matches(&self, reading: LightingReading) -> bool {
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
    /// its first checkpoint refused. Nothing was stored.
    Pending,
    /// No power class was established (`Reader::lighting_power`), so nothing
    /// was stored and no lighting report was sent.
    Unclassified,
    /// The permit refused the classified power or failed to store the intent
    /// (`Permit::admit_lighting`), and says why. No lighting report was sent.
    NotAdmitted,
    /// A store-only job stored the intent and sent no lighting report.
    Stored,
    /// A baseline query may have been sent and the baseline was not
    /// confirmed, for this reason. No setter was sent.
    Unread(&'static str),
    /// Both baseline logo replies correlated and their pair is outside the
    /// reviewed contract. No setter was sent; nothing is in doubt.
    Malformed,
    /// The baseline was confirmed and the job wrote nothing: a read-only
    /// job, or one whose baseline already matched (managed `Unchanged`).
    Observed,
    /// The baseline admitted a write, but too little time remained for the
    /// recipe at the measured pace. No setter was sent.
    Unpaced,
    /// A fresh admission checkpoint refused the first setter before it was
    /// sent. Nothing was sent after the baseline.
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
    /// another value or its admission refused it before it was sent, and
    /// `None` when a restore is itself in doubt.
    Mismatched {
        failure: &'static str,
        restored: Option<bool>,
    },
    /// Every setter was confirmed by its own readback, and the final
    /// aggregate read did not confirm a member the target sets, for this
    /// reason (managed's indeterminate write with confirmation unavailable).
    Unconfirmed(&'static str),
    /// Every setter was confirmed by its own readback and the final aggregate
    /// read confirmed every member the target sets (`final_reading`).
    Written,
}

/// What a job does (`LightingJob`).
#[derive(Clone, Copy, Debug)]
enum Work {
    /// Reads the baseline and writes nothing: the read-only resolution of an
    /// earlier job in doubt. Managed refuses it before any power read.
    Resolve,
    /// Classifies power and stores the request through the permit, then reads
    /// and writes no lighting: a clear, or a value the capability table does
    /// not admit.
    Store,
    /// Classifies power, stores, then writes the target the baseline does not
    /// match.
    Write(LightingTarget),
}

/// One lighting job, carried by its permit (`mutation::Permit::lighting`).
pub(crate) struct LightingJob {
    work: Work,
    /// Managed `OwnedRendererOffObservation.RequiresOffConfirmation`: a zero
    /// brightness confirms Off only after a second zero sample, 750 ms later.
    off_confirmation: bool,
    state: Mutex<State>,
}

#[derive(Clone, Copy)]
struct State {
    outcome: LightingOutcome,
    baseline: Option<LightingReading>,
    last: Option<LightingReading>,
    confirmation: LightingConfirmation,
}

/// Which confirmed member a setter changes.
#[derive(Clone, Copy)]
enum Member {
    Brightness,
    Logo,
}

impl LightingJob {
    fn with(work: Work, off_confirmation: bool) -> Self {
        Self {
            work,
            off_confirmation,
            state: Mutex::new(State {
                outcome: LightingOutcome::Pending,
                baseline: None,
                last: None,
                confirmation: LightingConfirmation::default(),
            }),
        }
    }

    /// The read-only resolution of an earlier job in doubt: the baseline
    /// alone, with no power read and no store.
    pub(crate) fn resolution() -> Self {
        Self::with(Work::Resolve, false)
    }

    /// A request whose intent is stored and not applied: power, then the
    /// store, and no lighting report.
    pub(crate) fn store() -> Self {
        Self::with(Work::Store, false)
    }

    /// A request that writes `target`: power, the store, then the recipe.
    /// A zero brightness is confirmed Off only by a second zero sample, at
    /// the baseline and at the final read, wherever native writes it
    /// (stricter than managed, which samples twice only while it owns a
    /// renderer).
    pub(crate) fn write(target: LightingTarget) -> Self {
        Self::with(Work::Write(target), target.brightness == 0)
    }

    /// A write job whose zero brightness is confirmed by one sample, for the
    /// recipe tests that pin the setter sequence without the confirmation.
    #[cfg(test)]
    pub(crate) fn write_single_sample(target: LightingTarget) -> Self {
        Self::with(Work::Write(target), false)
    }

    /// A finished job for `target` (a store-only one without) that reached
    /// `outcome` with `final_reading`, for the runtime's reply tests.
    #[cfg(test)]
    pub(crate) fn recorded(
        target: Option<LightingTarget>,
        outcome: LightingOutcome,
        final_reading: Option<LightingReading>,
    ) -> Self {
        let work = target.map_or(Work::Store, Work::Write);
        let job = Self::with(work, false);
        if let Ok(mut state) = job.state.lock() {
            state.outcome = outcome;
            state.last = final_reading;
        }
        job
    }

    /// What a write job sets; `None` for a resolution or a store.
    pub(crate) fn target(&self) -> Option<LightingTarget> {
        match self.work {
            Work::Write(target) => Some(target),
            Work::Resolve | Work::Store => None,
        }
    }

    /// The last outcome the job reached. A poisoned record is the most
    /// conservative one: a setter may have been submitted.
    pub(crate) fn outcome(&self) -> LightingOutcome {
        self.state
            .lock()
            .map_or(LightingOutcome::InDoubt(BRIGHTNESS_IN_DOUBT), |state| {
                state.outcome
            })
    }

    /// The confirmed baseline, when the job read one.
    pub(crate) fn baseline(&self) -> Option<LightingReading> {
        self.state.lock().ok().and_then(|state| state.baseline)
    }

    /// The final aggregate read after a confirmed write, or the baseline of a
    /// job that wrote nothing: managed `ReadLightingStateAsync`.
    pub(crate) fn final_reading(&self) -> Option<LightingReading> {
        self.state.lock().ok().and_then(|state| state.last)
    }

    /// What the job leaves the published confirmed members. A poisoned
    /// record clears both: a setter may have been submitted.
    pub(crate) fn confirmation(&self) -> LightingConfirmation {
        self.state.lock().map_or(
            LightingConfirmation {
                brightness: Some(None),
                logo: Some(None),
            },
            |state| state.confirmation,
        )
    }

    fn record(&self, outcome: LightingOutcome) {
        if let Ok(mut state) = self.state.lock() {
            state.outcome = outcome;
        }
    }

    fn observe(&self, baseline: Option<LightingReading>, last: Option<LightingReading>) {
        if let Ok(mut state) = self.state.lock() {
            state.baseline = state.baseline.or(baseline);
            state.last = last;
        }
    }

    /// A read of the brightness: `Some` confirms it, `None` clears it.
    fn confirm_brightness(&self, raw: Option<u8>) {
        if let Ok(mut state) = self.state.lock() {
            state.confirmation.brightness = Some(raw);
        }
    }

    /// A read of the logo: `Some` confirms it, `None` clears it.
    fn confirm_logo(&self, mode: Option<LogoMode>) {
        if let Ok(mut state) = self.state.lock() {
            state.confirmation.logo = Some(mode);
        }
    }

    /// A setter for `member` may now reach the firmware: the member is not
    /// confirmed until its readback, and the job's outcome says the setter
    /// is in doubt.
    fn sending(&self, member: Member, in_doubt: LightingOutcome) {
        match member {
            Member::Brightness => self.confirm_brightness(None),
            Member::Logo => self.confirm_logo(None),
        }
        self.record(in_doubt);
    }
}

/// Every lighting transaction ID a job may send, reserved before the first:
/// the baseline (4, with a second zero sample), the brightness setter and its
/// readback (2), the logo setters and readback (4), their restores (6) and
/// the final read (4). The power read reserves its own.
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

/// Whether `remaining` exchanges at the measured pace and `fixed` scheduled
/// time end before the deadline. As `mutation::Pace` charges a thermal
/// recipe: the slowest exchange once and every other at the second longest,
/// each timed from the previous reply to its own. A job that has measured
/// one exchange has no second longest, so each is charged at the slowest.
/// The trailing checkpoint since the last reply is part of `fixed`. A
/// lighting job writes no journal row, so no append is charged.
fn paced(exchanges: &Exchanges, remaining: u32, fixed: Duration, limit: Limit<'_>) -> bool {
    let each = if exchanges.second.is_zero() {
        exchanges.slowest
    } else {
        exchanges.second
    };
    let now = Instant::now();
    let trailing = exchanges
        .last
        .map_or(Duration::ZERO, |last| now.saturating_duration_since(last));
    each.checked_mul(remaining.saturating_sub(1))
        .and_then(|rest| rest.checked_add(exchanges.slowest))
        .and_then(|rest| rest.checked_add(trailing))
        .and_then(|rest| rest.checked_add(fixed))
        .and_then(|rest| now.checked_add(rest))
        .is_some_and(|end| end < limit.deadline)
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
        let power_ids = match job.work {
            Work::Resolve => None,
            Work::Store | Work::Write(_) => Some(self.reserve_pair()?),
        };
        let run = &mut Run { ids, next: 0, job };
        let admission = self.admission;
        let _pace = mutation::Pace::start(self);
        {
            let mut attempt = Attempt {
                reader: &mut *self,
                completed: false,
            };
            attempt.checkpoint(authorize, admission, limit).ok()?;
            attempt.completed = true;
        }
        // Managed resolves power and stores before it reads any lighting.
        if let Some([power_id, _]) = power_ids {
            let Some((power, performance)) = self
                .lighting_power(permit, authorize, power_id, limit)
                .await
            else {
                job.record(LightingOutcome::Unclassified);
                return Some(());
            };
            if !permit.admit_lighting(power, performance) {
                job.record(LightingOutcome::NotAdmitted);
                return Some(());
            }
            if matches!(job.work, Work::Store) {
                job.record(LightingOutcome::Stored);
                return Some(());
            }
        }
        let mut attempt = Attempt {
            reader: self,
            completed: false,
        };
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
        let target = job.target();
        if target.is_none_or(|target| target.logo.is_some()) {
            job.record(LightingOutcome::Unread(LOGO_UNREAD));
            let Some(logo) = attempt.logo(authorize, admission, limit, run).await? else {
                // Both replies correlated, and their pair is outside the
                // contract: nothing is in doubt, but no write follows.
                job.record(LightingOutcome::Malformed);
                attempt.completed = true;
                return Some(());
            };
            baseline.logo = Some(logo);
        }
        let Some(target) = target.filter(|target| !target.matches(baseline)) else {
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
        // with the off confirmation's wait as fixed time. Too little time
        // left is known not applied: the baseline read leaves nothing in
        // doubt.
        let logo = |mode| if writes_logo { logo_exchanges(mode) } else { 0 };
        let forward = u32::from(writes_brightness) * 2 + logo(target.logo);
        let restore = u32::from(writes_brightness) * 2 + logo(baseline.logo);
        let final_read = 1 + u32::from(job.off_confirmation) + 2;
        let confirmation = if job.off_confirmation {
            OFF_CONFIRMATION_DELAY
        } else {
            Duration::ZERO
        };
        let remaining = forward + restore.max(final_read);
        if !paced(&attempt.reader.exchanges, remaining, confirmation, limit) {
            job.record(LightingOutcome::Unpaced);
            attempt.completed = true;
            return Some(());
        }
        // Each setter starts only after its own fresh admission; a refusal
        // there sends nothing more and leaves nothing in doubt.
        let mut failure = None;
        if writes_brightness {
            job.record(LightingOutcome::Refused);
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
            job.record(if writes_brightness {
                LightingOutcome::Interrupted
            } else {
                LightingOutcome::Refused
            });
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
            // itself in doubt stops here; one its admission refuses before it
            // is sent is not restored, and nothing more is sent.
            let unrestored = LightingOutcome::Mismatched {
                failure,
                restored: Some(false),
            };
            let in_doubt = LightingOutcome::Mismatched {
                failure,
                restored: None,
            };
            let mut restored = true;
            if writes_brightness {
                job.record(unrestored);
                let result = attempt
                    .set_brightness(authorize, admission, limit, run, raw, in_doubt)
                    .await;
                restored = restore_confirmed(job, unrestored, result)?;
            }
            if wrote_logo && let Some(mode) = baseline.logo {
                job.record(unrestored);
                let result = attempt
                    .set_logo(authorize, admission, limit, run, mode, in_doubt)
                    .await;
                restored &= restore_confirmed(job, unrestored, result)?;
            }
            job.record(LightingOutcome::Mismatched {
                failure,
                restored: Some(restored),
            });
            attempt.completed = true;
            return Some(());
        }
        // Managed's final aggregate read. Every setter was confirmed, so an
        // incomplete read leaves the state unconfirmed, never in doubt; the
        // members it would confirm are not published until a read does.
        job.confirm_brightness(None);
        job.confirm_logo(None);
        let mut reading = LightingReading {
            brightness: attempt
                .brightness(authorize, admission, limit, run, job.off_confirmation)
                .await,
            logo: None,
        };
        let mut logo_reason = LOGO_UNREAD;
        if reading.brightness.is_some() {
            match attempt.logo(authorize, admission, limit, run).await {
                Some(Some(mode)) => reading.logo = Some(mode),
                Some(None) => logo_reason = LOGO_MALFORMED,
                None => {}
            }
        }
        job.observe(None, Some(reading));
        job.record(if reading.brightness.is_none() {
            LightingOutcome::Unconfirmed(BRIGHTNESS_UNREAD)
        } else if target.logo.is_some() && reading.logo.is_none() {
            LightingOutcome::Unconfirmed(logo_reason)
        } else {
            LightingOutcome::Written
        });
        attempt.completed = reading.logo.is_some() || logo_reason == LOGO_MALFORMED;
        Some(())
    }

    /// Managed `ResolveCurrentPowerSourceAsync` and the fresh evidence of
    /// `CanAdmitUnclassifiedExternalControl` for one lighting request, under
    /// this job's admission: the class and, for an unclassified adapter, a
    /// fresh firmware performance observation, which says whether HyperBoost
    /// is engaged. Windows battery is the battery class with no adapter
    /// read. Windows AC classifies one fresh 07/8C reading, the same admitted
    /// query every thermal recipe reads, whose Windows kind must not change
    /// across it. `None` establishes no class, which stores nothing: Windows
    /// unknown, no correlated reply, a battery reading Windows contradicts
    /// (`thermal_mutation::battery_reading_contradicts_windows`), or an
    /// unclassified reading without fresh presence or whose observation
    /// failed.
    async fn lighting_power(
        &mut self,
        permit: &dyn mutation::Permit,
        authorize: Authorize<'_, ReadAdmission>,
        id: u8,
        limit: Limit<'_>,
    ) -> Option<(i32, Option<ObservedPerformance>)> {
        if permit.windows_on_battery()? {
            return Some((1, None));
        }
        let admission = self.admission;
        let reading = {
            let mut attempt = Attempt {
                reader: &mut *self,
                completed: false,
            };
            let reading = attempt.read_power(id, authorize, admission, limit).await?;
            attempt.completed = true;
            reading
        };
        if permit.windows_on_battery() != Some(false) {
            return None;
        }
        let class = reading.thermal_source(true, false)?;
        if class == 1 {
            return None;
        }
        if class != UNCLASSIFIED_EXTERNAL {
            return Some((class, None));
        }
        if !permit.external_presence() {
            return None;
        }
        let performance = self.observe(authorize, limit).await?;
        Some((class, Some(performance)))
    }
}

/// Whether one restore's `result` confirmed the baseline. A restore its
/// admission refused before it was sent left `unrestored` recorded and is
/// not restored; `None` stops the job when the restore may have been
/// submitted.
fn restore_confirmed(
    job: &LightingJob,
    unrestored: LightingOutcome,
    result: Option<bool>,
) -> Option<bool> {
    match result {
        Some(confirmed) => Some(confirmed),
        None if job.outcome() == unrestored => Some(false),
        None => None,
    }
}

impl<P: Port> Attempt<'_, P, ReadAdmission> {
    /// One exchange of `request`, after its own checkpoint: the report bytes
    /// of a reply with a valid envelope that correlates with `request`, or
    /// `None` when that was not confirmed. A setter names the member it
    /// changes and the outcome it leaves once it may be sent, which the job
    /// records only after the checkpoint before its transfer admits it.
    async fn exchange(
        &mut self,
        authorize: Authorize<'_, ReadAdmission>,
        admission: ReadAdmission,
        limit: Limit<'_>,
        request: &RazerReport,
        setter: Option<(&LightingJob, Member, LightingOutcome)>,
    ) -> Option<[u8; REPORT_LENGTH]> {
        self.checkpoint(authorize, admission, limit).ok()?;
        let mut frame = [0; FRAME_LENGTH];
        frame[1..].copy_from_slice(&request.encode());
        let transfer = match setter {
            Some((job, member, in_doubt)) => {
                job.sending(member, in_doubt);
                Transfer::SendMutation(frame)
            }
            None => Transfer::SendGetter(frame),
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
            .exchange(authorize, admission, limit, query.request(), None)
            .await?;
        let mut raw = query.decode(&report).ok()?;
        if raw == 0 && off_confirmation {
            limit.wait(OFF_CONFIRMATION_DELAY).await.ok()?;
            let query = BrightnessQuery::new(run.take());
            let report = self
                .exchange(authorize, admission, limit, query.request(), None)
                .await?;
            raw = query.decode(&report).ok()?;
        }
        run.job.confirm_brightness(Some(raw));
        Some(raw)
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
            .exchange(authorize, admission, limit, state.request(), None)
            .await?;
        let enabled = state.decode(&report).ok();
        let effect = LogoEffectQuery::new(run.take());
        let report = self
            .exchange(authorize, admission, limit, effect.request(), None)
            .await?;
        let effect = effect.decode(&report).ok();
        let mode = enabled
            .zip(effect)
            .and_then(|(enabled, effect)| LogoMode::decode(enabled, effect));
        run.job.confirm_logo(mode);
        Some(mode)
    }

    /// One setter and its exact acknowledgement. `None` leaves it in doubt
    /// once it may have been sent.
    async fn submit(
        &mut self,
        authorize: Authorize<'_, ReadAdmission>,
        admission: ReadAdmission,
        limit: Limit<'_>,
        set: LightingSet,
        sending: (&LightingJob, Member, LightingOutcome),
    ) -> Option<()> {
        let report = self
            .exchange(authorize, admission, limit, set.request(), Some(sending))
            .await?;
        set.acknowledge(&report).ok()
    }

    /// Managed `SetBrightnessAndVerifyCoreAsync`: the 03/03 setter, its
    /// acknowledgement and the 03/83 readback, and whether that readback
    /// confirmed `raw`. `None` when it was not confirmed: in doubt once the
    /// setter may have been sent (`in_doubt` is recorded then), and otherwise
    /// refused before it with the outcome the caller recorded.
    async fn set_brightness(
        &mut self,
        authorize: Authorize<'_, ReadAdmission>,
        admission: ReadAdmission,
        limit: Limit<'_>,
        run: &mut Run<'_>,
        raw: u8,
        in_doubt: LightingOutcome,
    ) -> Option<bool> {
        let set = LightingSet::brightness(run.take(), raw);
        let sending = (run.job, Member::Brightness, in_doubt);
        self.submit(authorize, admission, limit, set, sending)
            .await?;
        let readback = self
            .brightness(authorize, admission, limit, run, false)
            .await?;
        Some(readback == raw)
    }

    /// Managed `SetLogoModeAndVerifyCoreAsync`: the 03/02 effect for a lit
    /// mode, the 03/00 state, then the 03/80/03/82 readback, and whether it
    /// confirmed `mode`. `None` as for `set_brightness`; a readback outside
    /// the contract also leaves the setter in doubt, as nothing then
    /// confirms the logo.
    async fn set_logo(
        &mut self,
        authorize: Authorize<'_, ReadAdmission>,
        admission: ReadAdmission,
        limit: Limit<'_>,
        run: &mut Run<'_>,
        mode: LogoMode,
        in_doubt: LightingOutcome,
    ) -> Option<bool> {
        if mode != LogoMode::Off {
            let set = LightingSet::logo_effect(run.take(), mode);
            let sending = (run.job, Member::Logo, in_doubt);
            self.submit(authorize, admission, limit, set, sending)
                .await?;
        }
        let set = LightingSet::logo_state(run.take(), mode);
        let sending = (run.job, Member::Logo, in_doubt);
        self.submit(authorize, admission, limit, set, sending)
            .await?;
        let readback = self.logo(authorize, admission, limit, run).await??;
        Some(readback == mode)
    }
}

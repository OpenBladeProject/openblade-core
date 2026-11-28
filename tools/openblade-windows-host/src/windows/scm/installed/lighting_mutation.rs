//! Managed `SetLightingOverrideAsync` on exact RZ09-0581, slices L1 to L3:
//! the keyboard Off and the logo modes through the reviewed brightness and
//! logo commands, and the Static and streamed effects through the
//! Service-owned matrix renderer (`LightingJob`), under the operation gate.
//! A Static renderer's keepalive runs on the origin tick
//! (`Runtime::maintain_lighting`); an animated renderer's frames run on the
//! stream task (`Runtime::run_lighting_stream`), with its clean controller
//! reading from the origin tick, in `lighting_stream.rs`.
//! `stepKeyboardBrightness` and `setLightingRefreshRate` are in
//! `lighting_controls.rs`.
//!
//! The order is managed `ExecuteScopedMutationAsync`'s (ServiceControlRuntime.cs
//! 14868-15060) for the Lighting subsystem:
//!
//! | Managed | Native reply |
//! |---|---|
//! | 14903-14919 low-power gate set: store, then `DeferLightingIntentForLowPower` (20209) | Stored, the committed message; a temporary override off Windows battery is Blocked, `LOW_POWER_TEMPORARY` (see below) |
//! | 14921-14924 `PendingWriteBlockedResult` (19255) | Blocked, `PENDING`, after a read-only resolution |
//! | 14926-14943 power resolution and unclassified adapter | Blocked, `UNCLASSIFIED_BLOCKED`, in the job (`LightingPermit::store_lighting`) |
//! | 14965-14981 store (`SetLightingOverrideCoreAsync`, 14820) | Failed, `The requested intent was rejected: ...`, in the job |
//! | 15005-15013 a clear on an unclassified adapter | Stored, `CLEARED_UNCLASSIFIED` |
//! | 15034-15053 apply, prefixed with the committed message unless Applied | see `reply` |
//! | 15213-15216 `IsKeyboardLightingDelegated`, in the apply after the store | Blocked, `DELEGATED`, before the store (stricter) |
//!
//! Stricter than managed, which stores the intent in each case and then
//! refuses only the apply:
//!
//! - A keyboard delegated to Windows Dynamic Lighting is refused before the
//!   store; managed stores and replies with the committed prefix
//!   (`a_keyboard_delegated_to_dynamic_lighting_blocks_before_any_store`).
//! - A competing controller is refused by the fresh admission before the job
//!   (`capture_intent`) and at each of its checkpoints, so nothing is stored.
//!   Managed's refresh after power resolution
//!   (`RefreshControllerPresenceAndRelinquishLightingAsync`) only observes the
//!   controller and relinquishes lighting; it stores, and the apply is refused
//!   (`refused_admission_stores_and_sends_nothing`).
//! - An unclassified adapter also needs a fresh firmware observation that
//!   HyperBoost is not engaged, which managed's Lighting arm does not ask for
//!   (`admits_unclassified`).
//! - A temporary override in low power needs Windows battery; managed stores
//!   it with no power class
//!   (`a_low_power_temporary_override_needs_windows_battery_divergence`).
//!
//! Interim divergences, each named by a test:
//!
//! - Capabilities admit Off, Static and the streamed effects, with every
//!   logo mode; Reactive, Ripple and the host-input effects are stored and
//!   then refused as managed refuses an effect its capability table lacks
//!   (`deferred_effects_are_refused_by_the_table_divergence`).
//! - Clearing the override on a classified adapter stores the clear, applies
//!   no profile lighting and replies Stored, `CLEARED_UNAPPLIED`: managed
//!   applies the profile's lighting there (ControlCoordinator.cs
//!   `ApplyLightingOutcomeAsync`), which is profile reconciliation
//!   (`a_clear_is_stored_and_says_what_waits`).
//! - Power-source transitions (`ApplyFullTransitionAsync`, 14948-14956) and
//!   profile reconciliation run no lighting step, so a request stored while
//!   the low-power gate is set is not applied after standby. Its reply is
//!   managed's committed message, Stored, instead of managed's promise to
//!   apply it (`the_low_power_store_promises_no_resume_apply_interim_divergence`).
//! - At the low-power boundary the renderer ends without a write; managed
//!   turns the keyboard Off and restores it after the resume
//!   (`low_power_retires_the_renderer_without_a_write`).
//!
//! Power is classified inside the job, under the gate and the job's own
//! admission, from Windows and the admitted 07/8C reading
//! (`Reader::lighting_power`). A classified adapter, and Windows battery,
//! admit lighting. An unclassified adapter needs managed's
//! `CanAdmitUnclassifiedExternalControl` (UnclassifiedExternalPower.cs 60-77)
//! for Lighting, from native's own fresh evidence, and the stricter
//! HyperBoost observation (`admits_unclassified`).
//!
//! An uncertain submission, or a request dropped while its job may still
//! run, latches this process (`Claim`) and ends the renderer: the next
//! request runs a read-only resolution instead of any write and replies
//! managed's pending refusal, and a complete read clears the latch. Nothing
//! is replayed.
use super::{
    Caller, Fresh, Logon, MailboxSource, Outcome, Runtime, Storage, UNCLASSIFIED_BLOCKED,
    is_fatal_admission, remember_failure, verify_retained_failure,
};
use crate::{
    discovery::performance::{
        Animation, ColorMode, Effect, LightingConfirmation, LightingJob, LightingOutcome,
        LightingTarget, MatrixState, MatrixStream, ObservedPerformance, Random, ReadAdmission,
        RefreshRate, RenderTarget, Renderer, Rgb, SystemRandom, Unwritten, lighting_approved,
        mutation::{Permit, Settlement},
    },
    service_status::LightingOverride,
};
use openblade_domain::{
    automation::UNCLASSIFIED_EXTERNAL,
    profile_set::{
        ColorMode as WireColorMode, Direction, LightingDuration, LightingEffect,
        LightingParameters, LightingSettings, RgbColor,
    },
    thermal_mutation::{Destination, Progress},
};
use openblade_protocol::lighting::{LogoMode, percent_to_raw};
use std::{
    cell::{Cell, OnceCell, RefCell},
    io,
    rc::Rc,
    sync::{Arc, Mutex, atomic::AtomicBool},
    time::Duration,
};
use tokio::time::Instant;

const STORED: u8 = 0;
const APPLIED: u8 = 1;
const FAILED: u8 = 3;

const COMMITTED: &str = "Lighting override intent was stored.";
/// A clear stored while the low-power gate is set (`store_deferred`).
#[cfg(feature = "service-profile-triggers-v1")]
const CLEARED: &str = "Lighting override was cleared.";
/// A clear on a classified adapter, which applies no profile lighting until
/// profile reconciliation runs lighting.
const CLEARED_UNAPPLIED: &str =
    "Lighting override was cleared. The profile's lighting was not applied.";
/// Managed's clear on an unclassified adapter (ServiceControlRuntime.cs 15012).
const CLEARED_UNCLASSIFIED: &str =
    "Override cleared. Power-profile application waits for adapter classification.";
/// A temporary override while the low-power gate is set, off Windows battery.
#[cfg(feature = "service-profile-triggers-v1")]
const LOW_POWER_TEMPORARY: &str = "A temporary lighting override was not stored: OpenBlade does not read the power source while the laptop is entering or leaving a low-power state.";
const ALREADY: &str = "Lighting is already confirmed.";
const CONFIRMED: &str = "Lighting setting applied and confirmed by readback.";
const MISMATCH: &str = "Lighting write is indeterminate because readback did not match.";
const UNSUPPORTED: &str = "Lighting value is not supported by the verified capability table.";
const PENDING: &str =
    "Lighting was not written because OpenBlade must confirm the current laptop state first.";
/// Managed `WindowsDynamicLightingOwnsKeyboard`.
#[cfg(feature = "service-profiles-v1")]
const DELEGATED: &str = "Keyboard lighting is delegated to Windows Dynamic Lighting. OpenBlade did not write the keyboard.";
const ROLLBACK: &str =
    "The lighting change failed and the previous lighting state could not be fully restored.";
const PARTIAL: &str = "The lighting transition stopped after partial submission.";
const ACTIVE_USER: &str = "The active interactive Windows user is required.";
const CUSTODY: &str = "Authenticated thermal custody is unavailable.";
const ADMISSION: &str = "Current lighting write admission is unavailable.";
const NOT_SUBMITTED: &str =
    "Current lighting write admission is unavailable; no lighting setter was submitted.";
const UNPACED: &str = "The remaining request time cannot cover this recipe at the measured device pace; no lighting setter was submitted.";
/// Managed `AppliedWithoutReadback("Lighting")`.
const RENDERED: &str =
    "Lighting command completed, but this laptop does not report the resulting state.";
const MATRIX_UNAVAILABLE: &str =
    "The keyboard matrix collection is unavailable; no lighting setter was submitted.";

/// What a lighting request leaves for status: whether intent or a reading
/// changed, and what the job leaves the confirmed lighting members.
pub(in super::super) struct Lit {
    pub(in super::super) outcome: Outcome,
    pub(in super::super) publish: bool,
    pub(in super::super) confirmation: LightingConfirmation,
}
impl Lit {
    fn refused(outcome: Outcome) -> Self {
        Self {
            outcome,
            publish: false,
            confirmation: LightingConfirmation::default(),
        }
    }
}

/// Managed `Pid02E0LightingTiming.IdleKeepaliveInterval`: an owned Static
/// renderer's frame cadence.
pub(in super::super) const KEEPALIVE_INTERVAL: Duration = Duration::from_secs(5);
/// The bound on one keepalive frame's job, well inside its interval.
const KEEPALIVE_BUDGET: Duration = Duration::from_secs(2);

/// What lighting this process holds between requests (`Runtime::lighting`).
/// Requests and the keepalive change it only under the operation gate. Stop
/// ends the renderer without it (`Runtime::retire_renderer`): the Service is
/// stopping, the mutation worker's stop cancels a keepalive still running,
/// and the stream is settled (`Runtime::settle_lighting_stream`).
#[derive(Default)]
pub(super) struct Session {
    /// A lighting setter or matrix report may have been submitted
    /// unconfirmed: the next request resolves it read-only. While it is set
    /// nothing owns the renderer (`latch`), so no keepalive frame goes.
    doubt: Cell<bool>,
    /// The 00/86 layout byte this session read: managed reads it once per
    /// port session, before the first lit write.
    layout: Cell<Option<u8>>,
    /// The renderer this process owns: a Static one, which the origin tick
    /// keeps alive (`Runtime::maintain_lighting`), or an animated one, which
    /// the stream draws (`Runtime::stream_frame`).
    owner: RefCell<Option<Owner>>,
    /// The animated matrix stream (`MatrixStream`). Unset, as in a
    /// composition without one, refuses every animated effect as one the
    /// capability table does not admit.
    stream: OnceCell<Rc<dyn MatrixStream>>,
    /// The stream's collection may be open: an animated request opened it,
    /// or a frame did, since the lane opens it for a frame when it is not
    /// (`Runtime::stream_frame`). It stays open while an animated renderer
    /// is owned, and `close_stream` closes it.
    stream_open: Cell<bool>,
    /// The deadline of the stream frame holding the operation gate
    /// (`Runtime::stream_frame`), which the origin tick waits for.
    stream_hold: Cell<Option<Instant>>,
    /// The refresh rate the animated cadence follows (managed
    /// `LightingRefreshRate`), as the saved preferences last read it.
    rate: Cell<RefreshRate>,
    /// The raw brightness the last step set (managed
    /// `lastKnownBrightnessRaw`). Every `setLightingOverride` request and a
    /// step that set no value clear it, so the next step reads 03/83 again;
    /// keepalive and stream frames keep it.
    raw: Cell<Option<u8>>,
    /// Wakes the stream loop for a new animated renderer or cadence.
    wake: tokio::sync::Notify,
    /// A test's palette selector in place of `SystemRandom`.
    #[cfg(test)]
    random: RefCell<Option<RandomSource>>,
    /// A test's bound on one stream frame in place of `FRAME_BUDGET`.
    #[cfg(test)]
    frame_budget: Cell<Option<Duration>>,
    /// A test withholds the semantic admission from this session's
    /// requests (`approved`).
    #[cfg(test)]
    withheld: Cell<bool>,
}

/// An owned renderer, the request whose admission its frames take, and
/// when the next keepalive frame is due.
struct Owner {
    renderer: Renderer,
    sid: String,
    session: u32,
    due: Instant,
    /// The low-power generation it was drawn under (`Runtime::lighting_boundary`).
    boundary: Option<u64>,
    /// An animated renderer's stream; `None` for Static.
    stream: Option<Stream>,
}

#[path = "lighting_stream.rs"]
mod stream;
#[cfg(test)]
pub(in super::super) use stream::Next;
pub(in super::super) use stream::STREAM_SETTLE_BUDGET;
use stream::{Clean, Stream};

impl Session {
    /// The semantic admission a request's target needs
    /// (`lighting_approved`), which only a test can withhold.
    fn approved(&self) -> bool {
        #[cfg(test)]
        if self.withheld.get() {
            return false;
        }
        lighting_approved()
    }

    fn renderer(&self) -> Option<Renderer> {
        self.owner.borrow().as_ref().map(|owner| owner.renderer)
    }

    /// Ends the renderer without a write: no further keepalive or stream
    /// frame is sent, the stream's frames are recalled and its collection
    /// closes, and the next lit request starts a new renderer.
    fn retire(&self) {
        self.owner.replace(None);
        self.close_stream();
    }

    /// The palette selector a new animation draws from.
    fn random(&self) -> Box<dyn Random> {
        #[cfg(test)]
        if let Some(random) = self.random.borrow().as_ref() {
            return random();
        }
        Box::new(SystemRandom)
    }

    fn latched(&self) -> bool {
        self.doubt.get()
    }

    /// Latches the doubt and ends the renderer: nothing owns what an
    /// unconfirmed submission left, so no keepalive draws over it, and the
    /// request after the resolution draws anew.
    fn latch(&self) {
        self.doubt.set(true);
        self.retire();
    }

    /// A complete read-only resolution clears the latch.
    fn resolve(&self) {
        self.doubt.set(false);
    }

    /// Holds the session for one job that may submit (`Claim`).
    fn claim(&self) -> Claim<'_> {
        Claim {
            session: self,
            settled: false,
        }
    }

    /// The owned renderer's next keepalive instant.
    fn defer(&self, due: Instant) {
        if let Some(owner) = self.owner.borrow_mut().as_mut() {
            owner.due = due;
        }
    }

    /// The renderer a render job left (`LightingJob::matrix`): a new one
    /// owned by this request from now, the same one kept for this request's
    /// user, or none. An animated renderer's stream starts from its
    /// handshake, as managed's effect clock does, with this request's fresh
    /// admission as its first clean reading, and continues a retained one's
    /// phase. One animated renderer replacing another keeps the stream's
    /// collection open.
    fn render(
        &self,
        job: &LightingJob,
        fresh: &Arc<dyn Fresh>,
        caller: (&str, u32),
        boundary: Option<u64>,
        now: Instant,
    ) {
        let (sid, session) = caller;
        if let Some(layout) = job.layout() {
            self.layout.set(Some(layout));
        }
        match job.matrix() {
            MatrixState::Owned(renderer) => {
                let stream = renderer.effect.animated().then(|| {
                    job.take_animation().map(|animation| Stream {
                        animation,
                        // The handshake precedes this call; a job clock ahead of
                        // the runtime's (a paused test runtime) cannot start the
                        // effect in the future.
                        started: job.effect_started().map_or(now, |at| at.min(now)),
                        next: renderer.effect.next_delay(0, self.rate.get()),
                        clean: Some(Clean::new(fresh.clone(), now)),
                    })
                });
                // An animated renderer whose animation is gone has nothing
                // to draw: nothing owns what its first frame left.
                let Some(stream) = stream.map_or(Some(None), |stream| stream.map(Some)) else {
                    self.retire();
                    return;
                };
                let animated = stream.is_some();
                if animated {
                    self.owner.replace(None);
                } else {
                    self.retire();
                }
                self.owner.replace(Some(Owner {
                    renderer,
                    sid: sid.into(),
                    session,
                    due: now + KEEPALIVE_INTERVAL,
                    boundary,
                    stream,
                }));
                if animated {
                    self.wake.notify_one();
                }
            }
            MatrixState::Lost => self.retire(),
            MatrixState::Kept => {
                if job.outcome() == LightingOutcome::Rendered
                    && let Some(owner) = self.owner.borrow_mut().as_mut()
                {
                    owner.sid = sid.into();
                    owner.session = session;
                    if let Some(stream) = owner.stream.as_mut() {
                        stream.clean = Some(Clean::new(fresh.clone(), now));
                    }
                }
            }
        }
    }
}

/// One lighting job's hold on the session, from before it can submit until
/// its outcome is read (`settle`). The job runs on the worker, so a request
/// or tick dropped meanwhile leaves it running: a claim dropped unsettled
/// latches the doubt, since nothing will confirm what the job left.
#[must_use]
struct Claim<'a> {
    session: &'a Session,
    settled: bool,
}

impl Claim<'_> {
    /// The job's outcome was read: one that may have submitted unconfirmed
    /// latches the doubt.
    fn settle(mut self, in_doubt: bool) {
        self.settled = true;
        if in_doubt {
            self.session.latch();
        }
    }
}

impl Drop for Claim<'_> {
    fn drop(&mut self) {
        if !self.settled {
            self.session.latch();
        }
    }
}

/// Whether an Off job may have changed the keyboard the renderer drew, so
/// the renderer ends as managed's Off retires it. Only a job that stopped
/// before its first setter, or restored the baseline it confirmed, keeps it.
fn retires_renderer(outcome: LightingOutcome) -> bool {
    !matches!(
        outcome,
        LightingOutcome::Refused
            | LightingOutcome::Pending
            | LightingOutcome::Unclassified
            | LightingOutcome::NotAdmitted
            | LightingOutcome::Stored
            | LightingOutcome::Unavailable
            | LightingOutcome::Unread(_)
            | LightingOutcome::Malformed
            | LightingOutcome::Unpaced
            | LightingOutcome::Mismatched {
                restored: Some(true),
                ..
            }
    )
}

/// The first keepalive instant on `due`'s 5 s grid after `now` (managed
/// `AdvanceFrameDeadline`): a late frame skips the instants it missed.
fn advance(due: Instant, now: Instant) -> Instant {
    let mut next = due;
    while next <= now {
        next += KEEPALIVE_INTERVAL;
    }
    next
}

/// What the job's power admission and store decided (`store_lighting`).
enum Admission {
    /// The intent was stored at this power class.
    Stored(i32),
    /// Nothing was stored, with this reply.
    Refused(Outcome),
}

/// What a lighting job's permit does once the job has classified power
/// (`LightingPermit::store`).
enum Intent {
    /// A job that never asks for a store: a keepalive or a resolution. A
    /// store it asks for is refused.
    Nothing,
    /// Stores this override, or clears it.
    Save(Option<LightingOverride>),
    /// A brightness step's power admission, which stores nothing and
    /// resolves the desired lighting at the job's class instead.
    #[cfg(feature = "service-profiles-v1")]
    Step(controls::StepState),
    /// A refresh-rate change's power admission, which stores nothing.
    #[cfg(feature = "service-profiles-v1")]
    Admit,
}

/// One lighting job's permit: its request's fresh admission before every
/// exchange, and the power admission and store the job asks for once it has
/// classified power. It keeps no journal row.
struct LightingPermit {
    fresh: Arc<dyn Fresh>,
    storage: Arc<Storage>,
    intent: Intent,
    #[cfg(feature = "service-hyperboost-v1")]
    hyperboost: Option<super::Probes>,
    job: LightingJob,
    admission: Mutex<Option<Admission>>,
    fatal: Arc<Mutex<Option<&'static str>>>,
}
impl LightingPermit {
    /// `job`'s permit under `fresh`. A job that classifies power admits an
    /// unclassified adapter with the runtime's HyperBoost evidence
    /// (`admits_unclassified`); a keepalive and a resolution classify none.
    fn new(
        runtime: &Runtime,
        fresh: Arc<dyn Fresh>,
        storage: Arc<Storage>,
        job: LightingJob,
        intent: Intent,
    ) -> Arc<Self> {
        #[cfg(feature = "service-hyperboost-v1")]
        let hyperboost = match intent {
            Intent::Nothing => None,
            _ => runtime.hyperboost.get().cloned(),
        };
        Arc::new(Self {
            fresh,
            storage,
            intent,
            #[cfg(feature = "service-hyperboost-v1")]
            hyperboost,
            job,
            admission: Mutex::new(None),
            fatal: runtime.fatal.clone(),
        })
    }
    /// The override a `Save` stores; `None` for a clear or another intent.
    fn value(&self) -> Option<&LightingOverride> {
        match &self.intent {
            Intent::Save(value) => value.as_ref(),
            _ => None,
        }
    }
    /// A brightness step's state.
    #[cfg(feature = "service-profiles-v1")]
    fn step(&self) -> Option<&controls::StepState> {
        match &self.intent {
            Intent::Step(step) => Some(step),
            _ => None,
        }
    }
    /// Managed `CanAdmitUnclassifiedExternalControl(Lighting)`
    /// (UnclassifiedExternalPower.cs 60-77) from native's own fresh evidence,
    /// plus one stricter clause. Fresh external presence was required by the
    /// job (`Reader::lighting_power`). `Storage::pending` is the store's
    /// quarantine or an unsettled last row of the one native journal, which
    /// holds the thermal, fan and battery rows; native keeps no other
    /// battery or HyperBoost doubt, and every native HyperBoost write is a
    /// journaled thermal step, so it stands for managed's pending
    /// indeterminate HyperBoost intents. It is not managed's `WritesBlocked`
    /// status flag; each exchange's fresh admission is native's gate. With
    /// owned HyperBoost, managed's journal safety block and pending restore
    /// are its journal (`Probes`), and native's own session is its history
    /// (`Storage::owns_hyperboost`). Stricter than managed, whose Lighting arm
    /// has no `HyperBoostActive` clause: a fresh firmware observation must
    /// confirm HyperBoost inactive, and one that failed admits nothing.
    fn admits_unclassified(&self, performance: Option<ObservedPerformance>) -> bool {
        performance.is_some_and(|observed| observed != ObservedPerformance::HyperBoostObserved)
            && !self.storage.pending()
            && self.hyperboost_admits()
    }
    #[cfg(feature = "service-hyperboost-v1")]
    fn hyperboost_admits(&self) -> bool {
        #[cfg(feature = "service-profile-triggers-v1")]
        if self.storage.owns_hyperboost() {
            return false;
        }
        self.hyperboost
            .as_ref()
            .is_some_and(super::Probes::admits_unclassified_control)
    }
    #[cfg(not(feature = "service-hyperboost-v1"))]
    fn hyperboost_admits(&self) -> bool {
        true
    }
    fn store(&self, power: i32, performance: Option<ObservedPerformance>) -> Admission {
        if power == UNCLASSIFIED_EXTERNAL && !self.admits_unclassified(performance) {
            return Admission::Refused(Outcome::blocked(UNCLASSIFIED_BLOCKED));
        }
        // The class becomes current before the store, as a thermal step's
        // does, so the temporary carries this Windows power generation.
        let observed = match self.fresh.power_generation() {
            Some(generation) if self.fresh.authorize().is_some() => {
                self.storage.observe_power(power, generation)
            }
            _ => Err(io::ErrorKind::PermissionDenied.into()),
        };
        if let Err(error) = observed {
            remember_failure(&self.fatal, &error);
            return Admission::Refused(Outcome::blocked(ADMISSION));
        }
        let value = match &self.intent {
            Intent::Nothing => return Admission::Refused(Outcome::blocked(ADMISSION)),
            Intent::Save(value) => value.as_ref(),
            #[cfg(feature = "service-profiles-v1")]
            Intent::Step(step) => {
                let desired =
                    controls::desired_lighting(&self.storage, &step.application, power).ok();
                if let Ok(mut slot) = step.desired.lock() {
                    *slot = desired;
                }
                return Admission::Stored(power);
            }
            #[cfg(feature = "service-profiles-v1")]
            Intent::Admit => return Admission::Stored(power),
        };
        match self.storage.save_lighting(value, power) {
            Ok(()) => Admission::Stored(power),
            Err(error) => {
                remember_failure(&self.fatal, &error);
                Admission::Refused(rejected(&error))
            }
        }
    }
    /// The reply.
    fn finish(&self) -> Lit {
        let admission = self.admission.lock().ok().and_then(|mut a| a.take());
        let power = match admission {
            Some(Admission::Stored(power)) => power,
            Some(Admission::Refused(outcome)) => return Lit::refused(outcome),
            None => {
                let refusal = match self.job.outcome() {
                    LightingOutcome::Unclassified => UNCLASSIFIED_BLOCKED,
                    _ => ADMISSION,
                };
                return Lit::refused(Outcome::blocked(refusal));
            }
        };
        if self.value().is_none() {
            // A clear is a store-only job, which wrote nothing.
            return stored_clear(power);
        }
        let outcome = reply(&self.job);
        let outcome = if outcome.status == APPLIED {
            outcome
        } else {
            Outcome {
                status: outcome.status,
                message: format!("{COMMITTED} Hardware result: {}", outcome.message),
            }
        };
        Lit {
            outcome,
            publish: true,
            confirmation: self.job.confirmation(),
        }
    }
}
impl Permit for LightingPermit {
    fn lighting(&self) -> Option<&LightingJob> {
        Some(&self.job)
    }
    fn authorize(&self) -> Option<ReadAdmission> {
        self.fresh.authorize()
    }
    fn windows_on_battery(&self) -> Option<bool> {
        self.fresh.on_battery()
    }
    fn store_lighting(&self, power: i32, performance: Option<ObservedPerformance>) -> bool {
        let admission = self.store(power, performance);
        let stored = matches!(admission, Admission::Stored(_));
        // A step reads and writes no lighting unless the desired lighting is lit.
        #[cfg(feature = "service-profiles-v1")]
        let stored = stored && self.step().is_none_or(controls::StepState::lit);
        match self.admission.lock() {
            Ok(mut slot) => *slot = Some(admission),
            Err(_) => return false,
        }
        stored
    }
    fn resolve(
        &self,
        _: i32,
        _: ObservedPerformance,
        _: &AtomicBool,
    ) -> io::Result<Option<Destination>> {
        Ok(None)
    }
    fn failed(&self, error: &io::Error) {
        remember_failure(&self.fatal, error)
    }
    fn retired(&self) -> io::Result<Settlement> {
        verify_retained_failure(&self.fatal)?;
        self.fresh.verify_health()?;
        Ok(Settlement::Complete)
    }
    fn record(&self, _: Progress) -> io::Result<()> {
        Err(io::Error::other(
            "A keyboard lighting write keeps no thermal journal row.",
        ))
    }
}

/// A stored clear's reply. On an unclassified adapter it is managed's:
/// profile application waits for adapter classification. On a classified
/// one managed applies the profile's lighting, which native does not yet.
fn stored_clear(power: i32) -> Lit {
    let message = if power == UNCLASSIFIED_EXTERNAL {
        CLEARED_UNCLASSIFIED
    } else {
        CLEARED_UNAPPLIED
    };
    Lit {
        outcome: Outcome {
            status: STORED,
            message: message.into(),
        },
        publish: true,
        confirmation: LightingConfirmation::default(),
    }
}

/// Managed's rejected intent (`RecordRejectedIntent`).
fn rejected(error: &io::Error) -> Outcome {
    Outcome {
        status: FAILED,
        message: format!("The requested intent was rejected: {error}"),
    }
}

/// What the verified capability table admits of a request: the keyboard
/// Off, or the Static effect, which the matrix renderer draws.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum Request {
    /// The keyboard Off (well-formedness makes it black at zero brightness).
    Off(LightingTarget),
    /// A lit effect at this raw brightness, logo mode and settings color.
    Lit {
        brightness: u8,
        logo: Option<LogoMode>,
        effect: Effect,
        color: Rgb,
    },
}

fn rgb(color: &RgbColor) -> Rgb {
    Rgb {
        red: color.red,
        green: color.green,
        blue: color.blue,
    }
}

/// Managed `StarlightDuration`, in ticks.
fn starlight(duration: LightingDuration) -> Option<i64> {
    Some(match duration {
        LightingDuration::SHORT => 5_000_000,
        LightingDuration::MEDIUM => 15_000_000,
        LightingDuration::LONG => 20_000_000,
        _ => return None,
    })
}

/// Managed `ProductionLightingPort.CreateEffect` for the effects this slice
/// draws: the effect the parameters name, with each mode's required colors,
/// and the legacy Spectrum and Breathing settings without parameters.
/// Static without parameters stays refused before any write (slice L2).
fn effect(settings: &LightingSettings) -> Option<Effect> {
    use LightingParameters as P;
    let mode = |mode: WireColorMode| match mode {
        WireColorMode::SINGLE => Some(ColorMode::Single),
        WireColorMode::DUAL => Some(ColorMode::Dual),
        WireColorMode::RANDOM => Some(ColorMode::Random),
        _ => None,
    };
    // Each mode's colors: Single needs the primary and Dual both; Random
    // keeps neither (managed's `default`).
    let colors = |mode: ColorMode, primary: &Option<RgbColor>, secondary: &Option<RgbColor>| {
        let primary = primary.as_ref().map(rgb);
        let secondary = secondary.as_ref().map(rgb);
        match mode {
            ColorMode::Single => Some((Some(primary?), None)),
            ColorMode::Dual => Some((Some(primary?), Some(secondary?))),
            ColorMode::Random => Some((None, None)),
        }
    };
    Some(match (&settings.parameters, settings.effect) {
        (Some(P::Static { .. }), _) => Effect::Static,
        (Some(P::SpectrumCycling), _) | (None, LightingEffect::SPECTRUM) => Effect::Spectrum,
        (
            Some(P::Breathing {
                color_mode,
                primary_color,
                secondary_color,
            }),
            _,
        ) => {
            let mode = mode(*color_mode)?;
            let (primary, secondary) = colors(mode, primary_color, secondary_color)?;
            Effect::Breathing {
                mode,
                primary,
                secondary,
            }
        }
        (None, LightingEffect::BREATHING) => Effect::Breathing {
            mode: ColorMode::Single,
            primary: Some(rgb(&settings.color)),
            secondary: None,
        },
        (Some(P::Fire), LightingEffect::FIRE) => Effect::Fire,
        (
            Some(P::Tidal {
                primary_color,
                secondary_color,
                random_colors,
                direction,
            }),
            _,
        ) => Effect::Tidal {
            primary: rgb(primary_color),
            secondary: rgb(secondary_color),
            random: *random_colors,
            rightward: rightward(*direction)?,
        },
        (
            Some(P::Starlight {
                color_mode,
                primary_color,
                secondary_color,
                duration,
            }),
            _,
        ) => {
            let mode = mode(*color_mode)?;
            let (primary, secondary) = colors(mode, primary_color, secondary_color)?;
            Effect::Starlight {
                mode,
                primary,
                secondary,
                duration: starlight(*duration)?,
            }
        }
        (Some(P::Wave { direction }), _) => Effect::Wave {
            rightward: rightward(*direction)?,
        },
        // Managed `LightingRotationDirection`: Clockwise 0, Counterclockwise 1.
        (Some(P::Wheel { direction }), _) => Effect::Wheel {
            counterclockwise: rightward(*direction)?,
        },
        _ => return None,
    })
}

/// The second of a two-valued direction: Rightward, or Counterclockwise.
fn rightward(direction: Direction) -> Option<bool> {
    match direction {
        Direction::FIRST => Some(false),
        Direction::SECOND => Some(true),
        _ => None,
    }
}

#[cfg(test)]
type RandomSource = Box<dyn Fn() -> Box<dyn Random>>;

/// The request `settings` makes, `None` when the verified capability table
/// does not admit it or the pinned semantic admission is not `approved`
/// (`lighting_approved`). The table admits Off, Static and the streamed effects
/// with every logo mode (managed `CreateEffect`, `effect`). A Static request
/// carries its color in its parameters (managed `CreateEffect`), which
/// well-formedness makes the settings color; a legacy request without
/// parameters is refused before any write, where managed refuses it inside
/// its recipe.
fn target(settings: &LightingSettings, approved: bool) -> Option<Request> {
    if !approved || settings.independent_controls.is_some() || settings.controls_keyboard_effect {
        return None;
    }
    let logo = match settings.logo_mode.map(|mode| mode.0) {
        None => None,
        Some(0) => Some(LogoMode::Static),
        Some(1) => Some(LogoMode::Breathing),
        Some(2) => Some(LogoMode::Off),
        Some(_) => return None,
    };
    if settings.effect == LightingEffect::OFF {
        return Some(Request::Off(LightingTarget {
            brightness: 0,
            logo,
        }));
    }
    let effect = effect(settings)?;
    let color = match (effect, &settings.parameters) {
        (Effect::Static, Some(LightingParameters::Static { color })) => rgb(color),
        (Effect::Static, _) => return None,
        _ => rgb(&settings.color),
    };
    Some(Request::Lit {
        brightness: percent_to_raw(settings.brightness).filter(|&raw| raw != 0)?,
        logo,
        effect,
        color,
    })
}

/// Managed `LogoLightingMode`'s wire value.
pub(in super::super) fn logo_wire(mode: LogoMode) -> i32 {
    match mode {
        LogoMode::Static => 0,
        LogoMode::Breathing => 1,
        LogoMode::Off => 2,
    }
}

/// Managed `ControlCoordinator.ApplyScopedAsync` and
/// `ProductionLightingPort.SetLightingAsync` results for a job that stored
/// its intent, as managed words them. Whether a setter may be in doubt is
/// the outcome's own (`LightingOutcome::in_doubt`). Managed's transport
/// descriptions (`NativeFailureDescription.Describe`) carry diagnostic
/// detail native cannot read, so native names the stop. A final read that
/// could not confirm the write is managed's indeterminate write with
/// confirmation unavailable, which managed holds as a pending confirmation;
/// native latches it the same way.
fn reply(job: &LightingJob) -> Outcome {
    let failed = |message: String| Outcome {
        status: FAILED,
        message,
    };
    let applied = |message: &str| Outcome {
        status: APPLIED,
        message: message.into(),
    };
    let indeterminate = |reason: &str| failed(format!("Lighting write is indeterminate: {reason}"));
    let not_applied = |reason: &str| failed(format!("Lighting write was not applied: {reason}"));
    match job.outcome() {
        LightingOutcome::Observed => applied(ALREADY),
        LightingOutcome::Written => {
            let confirmed = job
                .target()
                .zip(job.final_reading())
                .is_some_and(|(target, reading)| target.matches(reading));
            if confirmed {
                applied(CONFIRMED)
            } else {
                failed(MISMATCH.into())
            }
        }
        LightingOutcome::Unconfirmed(reason) => indeterminate(reason),
        LightingOutcome::Mismatched {
            failure,
            restored: Some(true),
        } => indeterminate(failure),
        LightingOutcome::Mismatched { .. } => indeterminate(ROLLBACK),
        LightingOutcome::InDoubt(reason) => indeterminate(reason),
        LightingOutcome::Interrupted => indeterminate(PARTIAL),
        LightingOutcome::Unread(reason) => failed(format!(
            "Lighting could not be read before writing: {reason}"
        )),
        LightingOutcome::Malformed => failed(
            "Lighting could not be read before writing: Logo lighting response was malformed or unsupported."
                .into(),
        ),
        LightingOutcome::Unpaced => not_applied(UNPACED),
        // Managed `AppliedWithoutReadback`: the lit keyboard reports no
        // state, so the setter readbacks are the completion evidence.
        LightingOutcome::Rendered => applied(RENDERED),
        // The step composes its own reply from the raw it set (managed
        // `StepKeyboardBrightnessAsync`); this is its setter completion.
        LightingOutcome::Stepped => applied(RENDERED),
        LightingOutcome::Unavailable => not_applied(MATRIX_UNAVAILABLE),
        // A store-only job for a value outside the capability table; a
        // clear gets its own reply (`stored_clear`).
        LightingOutcome::Stored => Outcome::blocked(UNSUPPORTED),
        LightingOutcome::Refused
        | LightingOutcome::Pending
        | LightingOutcome::Unclassified
        | LightingOutcome::NotAdmitted => not_applied(NOT_SUBMITTED),
    }
}

/// The request as `set_lighting` reads it.
pub(in super::super) struct LightingRequest {
    pub(in super::super) caller: Caller,
    pub(in super::super) sid: String,
    pub(in super::super) value: Option<LightingOverride>,
    pub(in super::super) deadline: Instant,
}

impl Runtime {
    /// Managed `SetLightingOverrideAsync` (ServiceControlRuntime.cs 3269),
    /// in the order the module documents.
    pub(in super::super) async fn set_lighting(
        &self,
        mailbox: MailboxSource,
        request: LightingRequest,
    ) -> io::Result<Lit> {
        #[cfg(test)]
        self.entered.set(self.entered.get() + 1);
        let LightingRequest {
            caller,
            sid,
            value,
            deadline,
        } = request;
        let _serial = self.gate.lock().await;
        verify_retained_failure(&self.fatal)?;
        if caller.logon != Logon::Interactive || caller.control_session != Some(caller.session) {
            return Ok(Lit::refused(Outcome::blocked(ACTIVE_USER)));
        }
        let MailboxSource::Reader(reader) = mailbox else {
            return Ok(Lit::refused(Outcome::blocked(CUSTODY)));
        };
        reader.custody().verify_integrity()?;
        let (storage, fresh) = match self.capture_intent(&sid, caller.session, reader.custody()) {
            Ok(captured) => captured,
            Err(error) if is_fatal_admission(&error) => return Err(error),
            Err(error) => return Ok(Lit::refused(unavailable(&error))),
        };
        fresh.verify_health()?;
        reader.retain_policy(self.thermal_policy(sid.clone(), caller.session, reader.custody()));
        #[cfg(feature = "service-profile-triggers-v1")]
        if self.in_low_power() {
            return store_deferred(&storage, fresh.as_ref(), value.as_ref());
        }
        if self.lighting.latched() {
            return self
                .resolve_lighting_doubt(&reader, storage, fresh, deadline)
                .await;
        }
        // Stricter than managed, which stores the intent and refuses only the
        // apply with the committed prefix (15213): a delegated keyboard is
        // refused before the store. Without a profile store there are no
        // stored preferences, and managed's defaults do not delegate.
        #[cfg(feature = "service-profiles-v1")]
        match storage.lighting_delegated() {
            Ok(false) => {}
            Ok(true) => return Ok(Lit::refused(Outcome::blocked(DELEGATED))),
            Err(error) if is_fatal_admission(&error) => return Err(error),
            Err(error) => return Ok(Lit::refused(unavailable(&error))),
        }
        let mut request = value
            .as_ref()
            .and_then(|value| target(&value.settings, self.lighting.approved()));
        // A renderer drawn before a low-power boundary is not retained: the
        // keyboard may have reset under it.
        if self.crossed_low_power() {
            self.retire_renderer();
        }
        let boundary = self.lighting_boundary();
        let lighting = &self.lighting;
        #[cfg(feature = "service-profiles-v1")]
        if let Some(rate) = storage.lighting_refresh_rate().ok().flatten() {
            lighting.rate.set(rate);
        }
        // An animated effect draws its first frame from its own animation,
        // and needs the stream to continue it; without either the request
        // is one the capability table does not admit.
        let mut animation = None;
        if let Some(Request::Lit { effect, color, .. }) = request
            && effect.animated()
        {
            animation = lighting
                .stream
                .get()
                .and_then(|_| Animation::new(effect, color, lighting.random()));
            if animation.is_none() {
                request = None;
            }
        }
        // The stream's collection opens, or is confirmed open, before
        // anything is sent: an animated request whose collection does not
        // open is stored and refused, so no stream ends after an Applied
        // reply.
        let mut unopened = false;
        if animation.is_some()
            && let Some(port) = lighting.stream.get().cloned()
        {
            unopened = !self.open_stream(&port, deadline).await;
        }
        lighting.raw.set(None);
        let job = match request {
            Some(Request::Off(target)) => LightingJob::write(target),
            Some(Request::Lit { .. }) if unopened => LightingJob::unopened(),
            Some(Request::Lit {
                brightness,
                logo,
                effect,
                color,
            }) => LightingJob::render(
                RenderTarget {
                    brightness,
                    logo,
                    effect,
                    color,
                    layout: lighting.layout.get(),
                    previous: lighting.renderer(),
                },
                animation,
            ),
            None => LightingJob::store(),
        };
        let admitted = fresh.clone();
        // A write job holds the session until its outcome is read, so a
        // request dropped meanwhile latches the doubt.
        let claim = request.is_some().then(|| lighting.claim());
        let permit = LightingPermit::new(self, fresh, storage, job, Intent::Save(value));
        let result = reader.mutate(permit.clone(), deadline).await;
        verify_retained_failure(&self.fatal)?;
        if let Err(error) = result
            && is_fatal_admission(&error)
        {
            return Err(error);
        }
        let lit = permit.finish();
        // Managed `TryPersistRememberedLightingIntentAsync` after an applied
        // or unchanged lighting intent: the override just stored is the
        // desired lighting at the job's power class. A clear applies nothing.
        #[cfg(feature = "service-profiles-v1")]
        if lit.outcome.status == APPLIED
            && let Some(value) = permit.value()
        {
            controls::remember_lighting(&permit.storage, &value.settings);
        }
        match request {
            Some(Request::Off(_)) if retires_renderer(permit.job.outcome()) => lighting.retire(),
            Some(Request::Lit { .. }) => lighting.render(
                &permit.job,
                &admitted,
                (&sid, caller.session),
                boundary,
                Instant::now(),
            ),
            _ => {}
        }
        if let Some(claim) = claim {
            claim.settle(permit.job.outcome().in_doubt());
        }
        // A request that leaves no animated renderer closes the collection
        // it opened.
        if !lighting.animated() {
            lighting.close_stream();
        }
        Ok(lit)
    }

    /// Managed's pending refusal after a possible submission: a read-only
    /// job under this request's admission, which writes nothing, and whose
    /// complete read clears the latch. A correlated logo pair outside the
    /// contract is a complete read too: nothing is in doubt about what was
    /// sent, and the next write's own baseline refuses that pair. What it
    /// read is published either way.
    async fn resolve_lighting_doubt(
        &self,
        reader: &super::super::mailbox_read::ReadSource,
        storage: Arc<Storage>,
        fresh: Arc<dyn Fresh>,
        deadline: Instant,
    ) -> io::Result<Lit> {
        let permit = LightingPermit::new(
            self,
            fresh,
            storage,
            LightingJob::resolution(),
            Intent::Nothing,
        );
        let result = reader.mutate(permit.clone(), deadline).await;
        verify_retained_failure(&self.fatal)?;
        if let Err(error) = result
            && is_fatal_admission(&error)
        {
            return Err(error);
        }
        let read = matches!(
            permit.job.outcome(),
            LightingOutcome::Observed | LightingOutcome::Malformed
        );
        if read {
            self.lighting.resolve();
        }
        let confirmation = permit.job.confirmation();
        Ok(Lit {
            outcome: Outcome::blocked(PENDING),
            publish: confirmation != LightingConfirmation::default(),
            confirmation,
        })
    }

    /// The owned renderer's keepalive on the origin tick, after the power
    /// check and before the keyboard Driver-mode step: one frame when it is
    /// due at `now`, managed `RunStreamAsync`'s idle frame every 5 s. The
    /// grid starts when the request that drew the renderer completed, and a
    /// late frame skips the instants it missed (`advance`). A frame goes at
    /// the first tick at or after its instant that finds the operation gate
    /// free, so it is late by up to one 500 ms tick, plus any request
    /// holding the gate; the frame's own job then takes its time. Each frame
    /// takes the owning request's fresh write admission. A frame refused
    /// before submission keeps the renderer and waits for the next instant;
    /// one that may have been submitted unconfirmed latches the doubt, which
    /// ends the renderer. The next instant follows the tick's instant plus
    /// the frame's own time, as managed advances from the instant observed
    /// after the write. A renderer that crossed a low-power boundary ends
    /// without a write (`crossed_low_power`). Only fatal admission evidence
    /// is an error.
    ///
    /// The step skips a gate a request holds, and waits for one a stream
    /// frame holds until that frame's deadline: the stream's own frames never
    /// cost it the clean reading they need (`confirm_stream_clean`).
    pub(in super::super) async fn maintain_lighting(
        &self,
        mailbox: &MailboxSource,
        now: Instant,
    ) -> io::Result<()> {
        let started = Instant::now();
        let _serial = match self.gate.try_lock() {
            Ok(serial) => serial,
            Err(_) => {
                let Some(frame) = self.lighting.stream_hold.get() else {
                    return Ok(());
                };
                match tokio::time::timeout_at(frame, self.gate.lock()).await {
                    Ok(serial) => serial,
                    Err(_) => return Ok(()),
                }
            }
        };
        if self.crossed_low_power() {
            self.retire_renderer();
            return Ok(());
        }
        let lighting = &self.lighting;
        let Some((renderer, sid, session, due, animated)) =
            lighting.owner.borrow().as_ref().map(|owner| {
                (
                    owner.renderer,
                    owner.sid.clone(),
                    owner.session,
                    owner.due,
                    owner.stream.is_some(),
                )
            })
        else {
            return Ok(());
        };
        let MailboxSource::Reader(reader) = mailbox else {
            return Ok(());
        };
        if animated {
            return self.confirm_stream_clean(reader, &sid, session, now);
        }
        if now < due {
            return Ok(());
        }
        verify_retained_failure(&self.fatal)?;
        // The tick's instant on its own clock, after what this step took.
        let observed = || now + started.elapsed();
        reader.custody().verify_integrity()?;
        let (storage, fresh) = match self.capture(&sid, session, reader.custody()) {
            Ok(captured) => captured,
            Err(error) if is_fatal_admission(&error) => return Err(error),
            Err(_) => {
                lighting.defer(advance(due, observed()));
                return Ok(());
            }
        };
        fresh.verify_health()?;
        let claim = lighting.claim();
        let permit = LightingPermit::new(
            self,
            fresh,
            storage,
            LightingJob::keepalive(renderer),
            Intent::Nothing,
        );
        let result = reader
            .mutate(permit.clone(), Instant::now() + KEEPALIVE_BUDGET)
            .await;
        verify_retained_failure(&self.fatal)?;
        if let Err(error) = result
            && is_fatal_admission(&error)
        {
            return Err(error);
        }
        let in_doubt = permit.job.outcome().in_doubt();
        claim.settle(in_doubt);
        if !in_doubt {
            lighting.defer(advance(due, observed()));
        }
        Ok(())
    }

    /// Ends the owned renderer without a write, at Stop (managed's port
    /// disposal stops its stream) and after a low-power boundary, and
    /// forgets the session's layout byte, which the next session reads
    /// again.
    pub(in super::super) fn retire_renderer(&self) {
        self.lighting.retire();
        self.lighting.layout.set(None);
    }

    /// Whether the owned renderer crossed a low-power boundary: the gate
    /// reads set now, or a suspend or resume arrived since it was drawn. The
    /// generation catches a boundary that came and went while requests held
    /// the gate from every tick, so the renderer ends at the first tick that
    /// finds the gate free, or at the next request, whichever comes first.
    fn crossed_low_power(&self) -> bool {
        let Some(boundary) = self
            .lighting
            .owner
            .borrow()
            .as_ref()
            .map(|owner| owner.boundary)
        else {
            return false;
        };
        boundary != self.lighting_boundary() || self.lighting_low_power()
    }

    /// The low-power generation a renderer is drawn under: every accepted
    /// suspend and resume advances it. `None` without a lifecycle reading,
    /// in a composition that reads none or while it is unreadable, which
    /// `lighting_low_power` reads as low power.
    fn lighting_boundary(&self) -> Option<u64> {
        #[cfg(any(
            feature = "service-profile-triggers-v1",
            feature = "service-keyboard-driver-mode-v1"
        ))]
        if let Some(read) = self.low_power.get() {
            return read().map(|transition| transition.generation);
        }
        None
    }

    /// Whether the low-power gate reads set, unreadable or exhausted, in the
    /// compositions that read it. Without either, each frame's own write
    /// admission is the only gate.
    fn lighting_low_power(&self) -> bool {
        #[cfg(feature = "service-profile-triggers-v1")]
        if self.in_low_power() {
            return true;
        }
        #[cfg(feature = "service-keyboard-driver-mode-v1")]
        if self.driver_mode_low_power() {
            return true;
        }
        false
    }
}

#[cfg(test)]
impl Runtime {
    /// Draws every later animation's palette from `random` (`Session::random`).
    pub(in super::super) fn script_lighting_random(
        &self,
        random: impl Fn() -> Box<dyn Random> + 'static,
    ) {
        self.lighting.random.replace(Some(Box::new(random)));
    }

    /// Holds the operation gate, as a request or the origin tick in flight
    /// does.
    pub(in super::super) fn hold_lighting_gate_for_tests(&self) -> tokio::sync::MutexGuard<'_, ()> {
        self.gate.try_lock().unwrap()
    }

    /// Whether the lighting doubt is latched.
    pub(in super::super) fn lighting_latched_for_tests(&self) -> bool {
        self.lighting.latched()
    }

    /// The refresh rate the stream's cadence follows.
    #[cfg(feature = "service-profiles-v1")]
    pub(in super::super) fn lighting_rate_for_tests(&self) -> RefreshRate {
        self.lighting.rate.get()
    }

    /// When the owned renderer's next keepalive frame is due.
    pub(in super::super) fn keepalive_due_for_tests(&self) -> Option<Instant> {
        self.lighting.owner.borrow().as_ref().map(|owner| owner.due)
    }

    /// Withholds the semantic admission from later lighting requests.
    pub(in super::super) fn withhold_lighting_approval_for_tests(&self) {
        self.lighting.withheld.set(true);
    }
}

/// A capture or saved configuration that cannot be read.
fn unavailable(error: &io::Error) -> Outcome {
    Outcome::blocked(format!(
        "Saved configuration or fresh admission is unavailable: {error}"
    ))
}

/// Managed's store while the low-power gate is set, before any other gate,
/// with no read or write. Only Windows battery classifies power without an
/// adapter reading, so a temporary override needs it, as an explicit thermal
/// request's deferred store does (`triggers::deferred_store_refusal`), and is
/// otherwise refused as `LOW_POWER_TEMPORARY`: stricter than managed, which
/// stores it with no power class (ServiceControlRuntime.cs 14903-14918). A
/// persistent override and a clear carry no power class. Nothing applies the
/// stored intent after standby yet, so the reply is managed's
/// committed message, Stored, without managed's promise.
#[cfg(feature = "service-profile-triggers-v1")]
fn store_deferred(
    storage: &Storage,
    fresh: &dyn Fresh,
    value: Option<&LightingOverride>,
) -> io::Result<Lit> {
    use crate::service_status::LIGHTING_PERSISTENT;
    if !fresh.authorize_storage() {
        return Ok(Lit::refused(Outcome::blocked(ADMISSION)));
    }
    let temporary = value.is_some_and(|value| value.lifetime != LIGHTING_PERSISTENT);
    let power = if temporary {
        match (fresh.on_battery(), fresh.power_generation()) {
            (Some(true), Some(generation)) => {
                storage.observe_power(1, generation)?;
                1
            }
            (Some(true), None) => return Ok(Lit::refused(Outcome::blocked(ADMISSION))),
            _ => return Ok(Lit::refused(Outcome::blocked(LOW_POWER_TEMPORARY))),
        }
    } else {
        -1
    };
    if let Err(error) = storage.save_lighting(value, power) {
        if is_fatal_admission(&error) {
            return Err(error);
        }
        return Ok(Lit::refused(rejected(&error)));
    }
    Ok(Lit {
        outcome: Outcome {
            status: STORED,
            message: if value.is_some() { COMMITTED } else { CLEARED }.into(),
        },
        publish: true,
        confirmation: LightingConfirmation::default(),
    })
}

#[cfg(feature = "service-profiles-v1")]
#[path = "lighting_controls.rs"]
mod controls;
#[cfg(feature = "service-profiles-v1")]
pub(in super::super) use controls::{StepRequest, lifetime as step_lifetime};

#[cfg(test)]
mod tests {
    use super::*;
    use crate::discovery::performance::LightingReading;
    use openblade_domain::profile_set::RgbColor;

    fn off(logo: Option<i32>) -> LightingSettings {
        let mut settings = LightingSettings::off();
        settings.logo_mode = logo.map(openblade_domain::profile_set::LogoLightingMode);
        settings
    }

    #[test]
    fn the_table_admits_off_static_and_the_streamed_effects_with_every_logo_mode() {
        assert_eq!(
            target(&off(None), true),
            Some(Request::Off(LightingTarget {
                brightness: 0,
                logo: None
            }))
        );
        let lit = |logo: Option<i32>| {
            let color = RgbColor {
                red: 0x47,
                green: 0xe1,
                blue: 0x0c,
            };
            let mut settings = LightingSettings::of(
                LightingEffect::STATIC,
                color,
                77,
                LightingParameters::Static { color },
            );
            settings.logo_mode = logo.map(openblade_domain::profile_set::LogoLightingMode);
            settings
        };
        for (wire, mode) in [
            (0, LogoMode::Static),
            (1, LogoMode::Breathing),
            (2, LogoMode::Off),
        ] {
            let Some(Request::Off(off)) = target(&off(Some(wire)), true) else {
                panic!("an Off request");
            };
            assert_eq!(off.logo, Some(mode));
            assert_eq!(
                target(&lit(Some(wire)), true),
                Some(Request::Lit {
                    brightness: 0xc5,
                    logo: Some(mode),
                    effect: Effect::Static,
                    color: Rgb {
                        red: 0x47,
                        green: 0xe1,
                        blue: 0x0c
                    },
                })
            );
            assert_eq!(logo_wire(mode), wire);
        }
        // A legacy Static request without parameters stays refused (L2).
        let mut legacy = lit(None);
        legacy.parameters = None;
        assert_eq!(target(&legacy, true), None);
        // Managed `CreateEffect` for each streamed effect, and its refusals.
        let red = RgbColor {
            red: 200,
            green: 1,
            blue: 2,
        };
        let blue = RgbColor {
            red: 3,
            green: 4,
            blue: 250,
        };
        let (r, b) = (rgb(&red), rgb(&blue));
        let of = |effect, parameters| LightingSettings::of(effect, red, 50, parameters);
        use LightingParameters as P;
        let cases = [
            (
                of(LightingEffect::SPECTRUM, P::SpectrumCycling),
                Some(Effect::Spectrum),
            ),
            (
                of(
                    LightingEffect::BREATHING,
                    P::Breathing {
                        color_mode: WireColorMode::DUAL,
                        primary_color: Some(red),
                        secondary_color: Some(blue),
                    },
                ),
                Some(Effect::Breathing {
                    mode: ColorMode::Dual,
                    primary: Some(r),
                    secondary: Some(b),
                }),
            ),
            (
                of(
                    LightingEffect::BREATHING,
                    P::Breathing {
                        color_mode: WireColorMode::SINGLE,
                        primary_color: None,
                        secondary_color: None,
                    },
                ),
                None,
            ),
            (
                of(
                    LightingEffect::BREATHING,
                    P::Breathing {
                        color_mode: WireColorMode::RANDOM,
                        primary_color: Some(red),
                        secondary_color: None,
                    },
                ),
                Some(Effect::Breathing {
                    mode: ColorMode::Random,
                    primary: None,
                    secondary: None,
                }),
            ),
            (
                of(
                    LightingEffect::WAVE,
                    P::Wave {
                        direction: Direction::SECOND,
                    },
                ),
                Some(Effect::Wave { rightward: true }),
            ),
            (
                of(
                    LightingEffect::WHEEL,
                    P::Wheel {
                        direction: Direction::FIRST,
                    },
                ),
                Some(Effect::Wheel {
                    counterclockwise: false,
                }),
            ),
            (
                of(
                    LightingEffect::TIDAL,
                    P::Tidal {
                        primary_color: red,
                        secondary_color: blue,
                        random_colors: false,
                        direction: Direction::FIRST,
                    },
                ),
                Some(Effect::Tidal {
                    primary: r,
                    secondary: b,
                    random: false,
                    rightward: false,
                }),
            ),
            (
                of(
                    LightingEffect::STARLIGHT,
                    P::Starlight {
                        color_mode: WireColorMode::SINGLE,
                        primary_color: Some(blue),
                        secondary_color: None,
                        duration: LightingDuration::LONG,
                    },
                ),
                Some(Effect::Starlight {
                    mode: ColorMode::Single,
                    primary: Some(b),
                    secondary: None,
                    duration: 20_000_000,
                }),
            ),
            (of(LightingEffect::FIRE, P::Fire), Some(Effect::Fire)),
            // Fire parameters under another effect, and the deferred effects.
            (of(LightingEffect::STATIC, P::Fire), None),
            (
                of(
                    LightingEffect::REACTIVE,
                    P::Reactive {
                        color: red,
                        duration: LightingDuration::SHORT,
                    },
                ),
                None,
            ),
            (of(LightingEffect::RIPPLE, P::Ripple { color: red }), None),
        ];
        for (settings, expected) in cases {
            let request = target(&settings, true);
            assert_eq!(
                request,
                expected.map(|effect| Request::Lit {
                    brightness: 0x80,
                    logo: None,
                    effect,
                    color: r,
                }),
                "{settings:?}"
            );
        }
        // Managed's legacy Spectrum and Breathing without parameters.
        let mut legacy = of(LightingEffect::BREATHING, P::SpectrumCycling);
        legacy.parameters = None;
        assert_eq!(
            target(&legacy, true),
            Some(Request::Lit {
                brightness: 0x80,
                logo: None,
                effect: Effect::Breathing {
                    mode: ColorMode::Single,
                    primary: Some(r),
                    secondary: None,
                },
                color: r,
            })
        );
        let mut keyboard = off(None);
        keyboard.controls_keyboard_effect = true;
        assert_eq!(target(&keyboard, true), None);
        // Without the pinned semantic admission the table admits nothing.
        assert_eq!(target(&off(None), false), None);
        assert_eq!(target(&lit(Some(0)), false), None);
    }

    /// An Off job ends the renderer once it may have changed what the
    /// renderer drew; one that stopped before a setter, or restored its
    /// confirmed baseline, keeps it.
    #[test]
    fn an_off_job_retires_the_renderer_once_it_may_have_written() {
        for outcome in [
            LightingOutcome::Observed,
            LightingOutcome::Written,
            LightingOutcome::Interrupted,
            LightingOutcome::InDoubt("x"),
            LightingOutcome::Unconfirmed("x"),
            LightingOutcome::Mismatched {
                failure: "x",
                restored: Some(false),
            },
            LightingOutcome::Mismatched {
                failure: "x",
                restored: None,
            },
        ] {
            assert!(retires_renderer(outcome), "{outcome:?}");
        }
        for outcome in [
            LightingOutcome::Refused,
            LightingOutcome::Pending,
            LightingOutcome::Unread("x"),
            LightingOutcome::Malformed,
            LightingOutcome::Unpaced,
            LightingOutcome::Mismatched {
                failure: "x",
                restored: Some(true),
            },
        ] {
            assert!(!retires_renderer(outcome), "{outcome:?}");
        }
    }

    /// A late keepalive skips the 5 s instants it missed, on its first
    /// frame's grid (managed `AdvanceFrameDeadline`).
    #[test]
    fn the_keepalive_grid_skips_missed_instants() {
        let due = Instant::now();
        assert_eq!(advance(due, due - Duration::from_millis(1)), due);
        assert_eq!(advance(due, due), due + KEEPALIVE_INTERVAL);
        assert_eq!(
            advance(due, due + Duration::from_millis(10_500)),
            due + KEEPALIVE_INTERVAL * 3
        );
    }
    /// Every job outcome's managed reply, and which ones latch.
    #[test]
    fn each_outcome_replies_as_managed_and_only_doubt_latches() {
        let indeterminate = |reason: &str| format!("Lighting write is indeterminate: {reason}");
        let not_submitted = "Lighting write was not applied: Current lighting write admission is unavailable; no lighting setter was submitted.";
        let cases: Vec<(LightingOutcome, u8, String, bool)> = vec![
            (LightingOutcome::Observed, APPLIED, ALREADY.into(), false),
            (LightingOutcome::Written, FAILED, MISMATCH.into(), false),
            (
                LightingOutcome::Unconfirmed("Lighting brightness could not be read."),
                FAILED,
                indeterminate("Lighting brightness could not be read."),
                true,
            ),
            (
                LightingOutcome::Mismatched {
                    failure: "Lighting brightness did not match independent readback.",
                    restored: Some(true),
                },
                FAILED,
                indeterminate("Lighting brightness did not match independent readback."),
                false,
            ),
            (
                LightingOutcome::Mismatched {
                    failure: "x",
                    restored: Some(false),
                },
                FAILED,
                indeterminate(ROLLBACK),
                false,
            ),
            (
                LightingOutcome::Mismatched {
                    failure: "x",
                    restored: None,
                },
                FAILED,
                indeterminate(ROLLBACK),
                true,
            ),
            (
                LightingOutcome::InDoubt(
                    "A logo command may have been submitted and was not confirmed.",
                ),
                FAILED,
                indeterminate("A logo command may have been submitted and was not confirmed."),
                true,
            ),
            (
                LightingOutcome::Interrupted,
                FAILED,
                indeterminate(PARTIAL),
                false,
            ),
            (
                LightingOutcome::Unread("Logo lighting could not be read."),
                FAILED,
                "Lighting could not be read before writing: Logo lighting could not be read."
                    .into(),
                false,
            ),
            (
                LightingOutcome::Malformed,
                FAILED,
                "Lighting could not be read before writing: Logo lighting response was malformed or unsupported.".into(),
                false,
            ),
            (
                LightingOutcome::Unpaced,
                FAILED,
                "Lighting write was not applied: The remaining request time cannot cover this recipe at the measured device pace; no lighting setter was submitted.".into(),
                false,
            ),
            (LightingOutcome::Stored, 2, UNSUPPORTED.into(), false),
            (LightingOutcome::Refused, FAILED, not_submitted.into(), false),
            (LightingOutcome::Pending, FAILED, not_submitted.into(), false),
            (LightingOutcome::Rendered, APPLIED, RENDERED.into(), false),
            (
                LightingOutcome::Unavailable,
                FAILED,
                "Lighting write was not applied: The keyboard matrix collection is unavailable; no lighting setter was submitted.".into(),
                false,
            ),
        ];
        for (outcome, status, message, doubt) in cases {
            let job = LightingJob::recorded(None, outcome, None);
            let reply = reply(&job);
            assert_eq!(
                (reply.status, reply.message.as_str(), outcome.in_doubt()),
                (status, message.as_str(), doubt),
                "{outcome:?}"
            );
        }
    }

    /// A written job is confirmed only when its final read confirms every
    /// member its target sets (`LightingTarget::matches`).
    #[test]
    fn a_written_job_is_confirmed_by_a_matching_final_read_only() {
        let target = LightingTarget {
            brightness: 0,
            logo: Some(LogoMode::Off),
        };
        let written = |reading: LightingReading| {
            let job = LightingJob::recorded(Some(target), LightingOutcome::Written, Some(reading));
            reply(&job).message
        };
        assert_eq!(
            written(LightingReading {
                brightness: Some(0),
                logo: Some(LogoMode::Off),
            }),
            CONFIRMED
        );
        assert_eq!(
            written(LightingReading {
                brightness: Some(0),
                logo: Some(LogoMode::Static),
            }),
            MISMATCH
        );
    }
}

//! Lifecycle-driven reconciliation of owed intent (`service-profile-triggers-v1`):
//! the Service's own 500 ms tick (`Runtime::reconcile_lifecycle`) applies what
//! the console pair owes after four lifecycle transitions, as managed runs
//! `ApplyFullTransitionAsync` after them without any explicit request:
//!
//! - A gate boundary: a resume, a DisplayOn that cleared a set gate, or a new
//!   resume (managed `ProcessNativeAsync(suspending: false)`, which a DisplayOn
//!   reaches when it clears the gate, `WindowsServiceLifecycleEventSource.cs:354-364`).
//!   A boundary owes nothing by itself: managed's resume applies only through
//!   `HandlePowerSourceChangedAsync`, which applies on a changed source. It
//!   runs a debt the pair already owes, such as intent stored or deferred in
//!   low power, which managed keeps until its next transition.
//! - A Windows power-kind change, battery to AC or back (managed
//!   `HandlePowerSourceChangedAsync` applying on a changed source): the pair
//!   owes a reconciliation at every class. A change while the gate is set is
//!   owed and applied after the boundary that clears it, as managed skips
//!   `PowerChanged` in low power and its resume compares the source again.
//! - The application lease expiring with an application profile active
//!   (managed `ExpireApplicationContextAsync`): the reporter's foreground is
//!   cleared and its pair owes a reconciliation at every class, so the power
//!   profile applies.
//! - A console user other than the last one activated, including the first
//!   one after the Service starts (managed `ActivateUserAsync`, which applies
//!   a new active user's profile at its first admitted request): its pair
//!   owes a reconciliation at every class. Managed keys this on the SID, so
//!   the same user returning after a logoff or a switch owes nothing new.
//!
//! The pair is the console session's interactive user: the latest admitted
//! reporter when it reported from that session, else the console user
//! itself (`Runtime::console`), as managed's transitions need no report. The
//! tick reconciles it under the mutation gate through the request path's own
//! `reconcile_locked`: every step captures fresh exact-device, power,
//! session, ownership and lifecycle admission, writes under its own journal
//! id, and stops on ambiguity.
//!
//! A cause stays pending until an attempt's steps leave nothing owed. An
//! attempt refused before any step (no console user, a refused capture such
//! as a locked session after resume, a fenced store, unclassified power)
//! reads no firmware and spends nothing; one whose steps leave the debt owed
//! spends the debt's own `RETRIES` budget (`Reconciled::after`), which only
//! a step with nothing possibly submitted can leave owed. Either is retried
//! once the ordinary write epoch has moved, as an unlock or a later notice
//! moves it, so the tick takes at most one capture per epoch move and never
//! polls an unmoved epoch; a retry after steps also waits `PACE` after them,
//! so a burst of notices cannot spend the budget at once. An attempt refused
//! by a pending GPU clock confirmation is also retried once that fence
//! clears, which moves no epoch: while a cause is parked so, each tick checks
//! the fence, which reads nothing unless a submission is uncertain, as
//! managed reruns its deferred transition after each periodic observation
//! (`ServiceControlRuntime.cs:18085`, `:18187-18241`). A newer cause
//! supersedes a pending one.
//!
//! A capture refused because fresh competing-controller absence is not
//! confirmed drops the cause and settles the pair's debt: strict thermal
//! control stays advisory and never queues behind another controller
//! (Architecture rule 5), as managed's transition applies nothing then and
//! keeps nothing for later. The next lifecycle cause or changed report owes
//! afresh. The drop ends the user's startup reconciliation too, so that
//! cause compares confirmed drift rather than applying over it
//! (`Runtime::drop_for_controller`, which a report's refused capture takes
//! as well).
use super::*;
use std::sync::atomic::AtomicU64;

/// An inter-tick gap this long is time the Service did not run, such as
/// Modern Standby without a suspend notice: the reporter could not renew
/// then either, so the lease is renewed as at a gate boundary. Every tick
/// stamps its time after its attempt, so an attempt is no gap. A tick that
/// finds a request holding the mutation gate stamps it too, so a long
/// request is no gap, unless the gap is already this long: a request that
/// takes the gate at a wake must not erase the sleep before it.
const SLEPT: std::time::Duration = std::time::Duration::from_secs(5);
/// The least time between two attempts of one cause whose steps ran and
/// left the debt owed. Epoch moves within it only mark the cause due, so a
/// burst of notices, such as battery-level ones, takes one retry.
const PACE: std::time::Duration = std::time::Duration::from_secs(2);
/// How often the tick reads the console probe at an unmoved write epoch
/// while it names no user, so a logon is seen without a further notice.
const UNNAMED_POLL: std::time::Duration = std::time::Duration::from_secs(3);

/// What the lifecycle tick saw: the gate generation and the Windows power
/// generation, each `None` while unreadable.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
struct Mark {
    gate: Option<u64>,
    power: Option<u64>,
}
/// The lifecycle tick's state between ticks.
#[derive(Clone, Debug)]
pub(in super::super) struct Tick {
    mark: Mark,
    /// When the tick last ran, for the gap that renews the lease.
    at: Instant,
    console: Console,
    /// The cause it still owes an attempt.
    pending: Option<Pending>,
    /// When the last gate boundary moved: a power change within
    /// `thermal_drift::RESUME_WINDOW` after it is applied as the resume's.
    boundary: Option<Instant>,
}
/// What the tick knows of the console user.
#[derive(Clone, Debug, Default, PartialEq, Eq)]
struct Console {
    /// The last read named a console user.
    known: bool,
    /// The last console user activated, as managed's `activeUserSid`.
    activated: Option<String>,
    /// The write epoch and the time of the last read. A known user is read
    /// again once that epoch moves (every session notice fences it); while
    /// none is known, also every `UNNAMED_POLL`.
    read: Option<(Option<u64>, Instant)>,
}
/// A lifecycle cause whose reconciliation has not settled.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
struct Pending {
    /// A power-kind change or an activation owes every class to the pair
    /// its first resolved attempt reconciles.
    owe: bool,
    /// The ordinary write epoch at the last attempt that left the cause
    /// pending; `None` until one ran. A retry waits for that epoch to move.
    tried: Option<Option<u64>>,
    /// The last attempt was refused by a pending GPU clock confirmation
    /// (`Attempt::GpuPending`): a retry also runs once that fence clears.
    gpu_pending: bool,
    /// When the last attempt whose steps ran left the debt owed. A retry
    /// waits `PACE` after it.
    stepped: Option<Instant>,
    /// How its reconciliation treats confirmed drift: an activation applies
    /// over it, and any other cause, the lease expiry included, compares
    /// OpenBlade's own confirmed state as well (`thermal_drift::Mode`). A
    /// merged cause keeps the strongest.
    mode: thermal_drift::Mode,
    /// A power-kind change is among its causes.
    power: bool,
    /// That power change is applied within the resume window after a gate
    /// boundary, where firmware may still settle into the destination
    /// (managed applies it during resume validation,
    /// `resumePowerSourceTransitionGeneration`).
    resume: bool,
}
impl Pending {
    fn new(owe: bool) -> Self {
        Self {
            owe,
            tried: None,
            gpu_pending: false,
            stepped: None,
            mode: thermal_drift::Mode::WithPrior,
            power: false,
            resume: false,
        }
    }
    /// Whether an attempt runs at write epoch `epoch` at `now`, the GPU
    /// fence aside (`gpu_pending`).
    fn due(&self, epoch: Option<u64>, now: Instant) -> bool {
        let moved = match self.tried {
            None => true,
            Some(tried) => epoch.is_some() && epoch != tried,
        };
        moved
            && self
                .stepped
                .is_none_or(|at| now.saturating_duration_since(at) >= PACE)
    }
    /// The cause after an attempt at `epoch` refused before any step: it
    /// waits for that epoch to move. It spends nothing: the refusal read no
    /// firmware.
    fn wait(self, epoch: Option<u64>) -> Self {
        Self {
            tried: Some(epoch),
            gpu_pending: false,
            ..self
        }
    }
    /// The cause after an attempt at `epoch` refused by a pending GPU clock
    /// confirmation: it also runs once that fence clears, which moves no
    /// epoch.
    fn gpu_pending(self, epoch: Option<u64>) -> Self {
        Self {
            gpu_pending: true,
            ..self.wait(epoch)
        }
    }
    /// The cause after an attempt at `epoch` whose steps left the debt owed
    /// at `at`: it waits for that epoch to move and for `PACE`. The debt
    /// counts the retry (`Reconciled::after`).
    fn stepped(self, epoch: Option<u64>, at: Instant) -> Self {
        Self {
            stepped: Some(at),
            ..self.wait(epoch)
        }
    }
}
/// The lifecycle transitions between two ticks.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
struct Causes {
    // The gate generation moved: a suspend, resume, new resume or
    // gate-clearing DisplayOn. A DisplayOn on a clear gate, a duplicate
    // resume and a display-off do not move it.
    boundary: bool,
    // The Windows power kind changed. An unknown sample is no boundary.
    power: bool,
}
impl Causes {
    fn between(prior: Mark, now: Mark) -> Self {
        Self {
            boundary: prior.gate != now.gate,
            power: matches!((prior.power, now.power), (Some(a), Some(b)) if a != b),
        }
    }
}
/// The pair one attempt reconciles, and the process its steps record.
pub(super) struct Pair {
    pub(super) sid: String,
    pub(super) session: u32,
    pub(super) pid: u32,
}
/// How one attempt ended.
enum Attempt {
    /// Its steps ran and left nothing owed, nothing was owed, or a competing
    /// controller dropped the cause.
    Done(Option<Snapshot>),
    /// Refused before any step: retried once the write epoch has moved.
    Refused,
    /// Refused before any step by a pending GPU clock confirmation
    /// (`TriggerAdmission::ThermalPending`): retried once the write epoch has
    /// moved or the fence has cleared.
    GpuPending,
    /// Its steps left the debt owed with nothing possibly submitted:
    /// retried once the write epoch has moved and `PACE` has passed.
    Owed(Option<Snapshot>),
}

impl Runtime {
    /// One lifecycle tick: reads the gate and power generations and, when it
    /// may have changed, the console user; records any transition since the
    /// last tick as a pending cause; and while the gate is clear runs one
    /// attempt for that cause from `status`, which is read only then. Returns
    /// the reconciliation's status, to publish as a report's is.
    ///
    /// A tick that finds the mutation gate held reads nothing and keeps its
    /// state but its time, so the transition is seen by a later tick; the
    /// request holding the gate may have settled the debt by then. Debts
    /// change only under the gate, so a request's own settlement cannot
    /// overwrite a debt this tick owed, or the reverse.
    pub(in super::super::super) async fn reconcile_lifecycle(
        &self,
        mailbox: &MailboxSource,
        status: impl FnOnce() -> Snapshot,
    ) -> io::Result<Option<Snapshot>> {
        let Ok(_serial) = self.gate.try_lock() else {
            // The Service is running while a request holds the gate: no gap,
            // unless the gap before it already is one.
            if let Some(tick) = self.lifecycle.borrow_mut().as_mut() {
                let now = Instant::now();
                if now.saturating_duration_since(tick.at) < SLEPT {
                    tick.at = now;
                }
            }
            return Ok(None);
        };
        verify_retained_failure(&self.fatal)?;
        let mark = Mark {
            gate: self
                .low_power
                .get()
                .map_or(Some(0), |read| read().map(|t| t.generation)),
            power: self.power_generation.get().and_then(|read| read()),
        };
        let epoch = self.admission_epoch();
        let low = self.gate() == Gate::Low;
        let now = Instant::now();
        let prior = self.lifecycle.borrow().clone();
        let (console, activation) =
            self.observe_console(prior.as_ref().map(|tick| &tick.console), epoch, low, now);
        let (causes, expired, mut pending) = match &prior {
            // The first tick has no transition to compare, only the console
            // user's activation.
            None => (Causes::default(), false, None),
            Some(prior) => {
                let causes = Causes::between(prior.mark, mark);
                let slept = now.saturating_duration_since(prior.at) >= SLEPT;
                let expired = self.renew_or_expire_lease(causes.boundary || slept || low);
                (causes, expired, prior.pending)
            }
        };
        // The last gate boundary, for the resume window of a later power
        // change (`Pending::resume`).
        let boundary = if causes.boundary {
            Some(now)
        } else {
            prior.as_ref().and_then(|tick| tick.boundary)
        };
        let resumed = boundary
            .is_some_and(|at| now.saturating_duration_since(at) < thermal_drift::RESUME_WINDOW);
        // A new resume, a power change or low power ends a resume hold, as
        // managed clears it then or drops it at its next observation.
        if causes.boundary || causes.power || low {
            self.drift
                .lock()
                .map_err(|_| io::ErrorKind::Other)?
                .drop_hold();
        }
        if causes.boundary || causes.power || expired || activation {
            let owe = causes.power || activation || pending.is_some_and(|p| p.owe);
            // Managed publishes the power profile's desired state before the
            // lease expiry's transition reads the prior one, so OpenBlade's
            // own application preset reads as drift and pauses there, and
            // managed's default convergence loop later repairs it. Native
            // compares OpenBlade's own confirmed state at the expiry, as at
            // every other cause, and hands that state back itself, so no
            // convergence claim is spent on it (`leaseExpired` in the
            // lifecycle vectors).
            let mode = if activation {
                thermal_drift::Mode::Overwrite
            } else {
                thermal_drift::Mode::WithPrior
            };
            let power = causes.power || pending.is_some_and(|p| p.power);
            pending = Some(Pending {
                mode: pending.map_or(mode, |p| p.mode.max(mode)),
                power,
                resume: power && (resumed || pending.is_some_and(|p| p.resume)),
                ..Pending::new(owe)
            });
        }
        // A cause the GPU fence parked is due once the fence clears, at an
        // unmoved epoch too; the fence reads only while a submission is
        // uncertain, and only once the epoch has not already made it due.
        // The refused attempt itself ran only after `PACE`, so no pace is
        // left to wait here.
        let due = match pending {
            Some(p) if !low => {
                p.due(epoch, now)
                    || (p.gpu_pending
                        && !self
                            .gpu_fenced(Instant::now() + crate::service_frame::RECONCILE_BUDGET)
                            .await)
            }
            _ => false,
        };
        let Some(mut cause) = pending.filter(|_| due) else {
            self.stamp(mark, console, pending, boundary);
            return Ok(None);
        };
        let attempt = match self.lifecycle_pair() {
            None => Attempt::Refused,
            Some(pair) => {
                if std::mem::take(&mut cause.owe) {
                    // A new change at every class, as a changed report owes.
                    self.owe((pair.sid.clone(), pair.session), Debt::owed(Classes::ALL));
                }
                self.attempt(mailbox, pair, status, (cause.mode, cause.resume))
                    .await?
            }
        };
        let (pending, snapshot) = match attempt {
            Attempt::Done(snapshot) => (None, snapshot),
            Attempt::Refused => (Some(cause.wait(epoch)), None),
            Attempt::GpuPending => (Some(cause.gpu_pending(epoch)), None),
            Attempt::Owed(snapshot) => (Some(cause.stepped(epoch, Instant::now())), snapshot),
        };
        self.stamp(mark, console, pending, boundary);
        Ok(snapshot)
    }

    /// Records the tick's state, stamped now: after any attempt, so the
    /// attempt's own time is no gap.
    fn stamp(
        &self,
        mark: Mark,
        console: Console,
        pending: Option<Pending>,
        boundary: Option<Instant>,
    ) {
        *self.lifecycle.borrow_mut() = Some(Tick {
            mark,
            at: Instant::now(),
            console,
            pending,
            boundary,
        });
    }

    /// Reads the console user when it may have changed: at the first tick,
    /// after the write epoch moved, and every `UNNAMED_POLL` while none is
    /// known. Says whether a user other than the last one activated is now
    /// the console user. In low power it reads nothing: no attempt runs
    /// there, and the notice that clears the gate also moves the epoch, so
    /// the first clear tick reads. Test composition without a console probe
    /// activates no one.
    fn observe_console(
        &self,
        prior: Option<&Console>,
        epoch: Option<u64>,
        low: bool,
        now: Instant,
    ) -> (Console, bool) {
        let last = prior.cloned().unwrap_or_default();
        let Some(read) = self.console.get() else {
            return (last, false);
        };
        let current = last.read.is_some_and(|(read, at)| {
            read == epoch && (last.known || now.saturating_duration_since(at) < UNNAMED_POLL)
        });
        if low || current {
            return (last, false);
        }
        let read_at = Some((epoch, now));
        let Some((sid, _)) = read() else {
            return (
                Console {
                    known: false,
                    read: read_at,
                    ..last
                },
                false,
            );
        };
        let activation = last.activated.as_deref() != Some(sid.as_str());
        (
            Console {
                known: true,
                activated: Some(sid),
                read: read_at,
            },
            activation,
        )
    }

    /// One attempt for `pair`'s debt, with how it treats confirmed drift and
    /// whether it applies a power change seen at a resume (`Pending`).
    async fn attempt(
        &self,
        mailbox: &MailboxSource,
        Pair { sid, session, pid }: Pair,
        status: impl FnOnce() -> Snapshot,
        (mode, resume): (thermal_drift::Mode, bool),
    ) -> io::Result<Attempt> {
        let key = (sid.clone(), session);
        let Debt::Owed {
            classes: owed,
            attempts,
        } = self.debt(&key)
        else {
            return Ok(Attempt::Done(None));
        };
        let MailboxSource::Reader(reader) = mailbox else {
            return Ok(Attempt::Refused);
        };
        reader.custody().verify_integrity()?;
        // A reconciliation writes, so it takes the write capture, which
        // refuses in low power and checks the console session, its unlocked
        // state and the primary token's SID afresh.
        let (storage, fresh) = match self.capture(&sid, session, reader.custody()) {
            Ok(captured) => captured,
            Err(error) if is_fatal_admission(&error) => return Err(error),
            // Another controller may own the device: the cause is dropped
            // and nothing is kept owed for after it leaves, which would
            // overwrite what the user chose there (module comment).
            Err(error) if is_competing_controller(&error) => {
                self.drop_for_controller(&sid, session);
                return Ok(Attempt::Done(None));
            }
            // A locked or switched session, an unknown AC line status, or a
            // per-user store that cannot be opened. These are the session's
            // own state, which the user's next unlock or notice resolves and
            // which claims nothing for another controller, so the cause is
            // kept. A store directory that does not exist stays missing: the
            // native Service never creates it, so only a Service switch,
            // which starts a new runtime, provides one, and meanwhile each
            // epoch move costs one refused capture and no firmware read.
            Err(_) => return Ok(Attempt::Refused),
        };
        // As a report's: a fenced store, unclassified power or a pending GPU
        // clock confirmation runs nothing and leaves the debt as it was. The
        // cause is kept, as for a refused session: the fence is OpenBlade's
        // own unsettled journal row, a document changed outside the Service
        // or OpenBlade's own uncertain GPU submission, not another
        // controller, and it stays fenced until a read-only reconciliation
        // settles it, so a possibly submitted step never runs again
        // (`admit_trigger`). A GPU fence clears without moving the epoch, so
        // that refusal is retried once the fence clears (`Pending::gpu_pending`).
        let deadline = Instant::now() + crate::service_frame::RECONCILE_BUDGET;
        let classes =
            match admit_trigger(&storage, fresh.as_ref(), owed, self.gpu_fenced(deadline)).await? {
                TriggerAdmission::Admitted(classes) => classes,
                TriggerAdmission::ThermalPending => return Ok(Attempt::GpuPending),
                _ => return Ok(Attempt::Refused),
            };
        let reconciled = self
            .reconcile_locked(
                mailbox,
                Reconcile {
                    // An interactive user controlling `session`: the reporter
                    // as its last report was admitted, or the console user,
                    // whose token `console_user` classified as interactive.
                    // It submits nothing itself; each step's capture and every
                    // write's authorization re-check that session and user.
                    caller: Caller {
                        pid: Some(pid),
                        logon: Logon::Interactive,
                        session,
                        control_session: Some(session),
                    },
                    sid,
                    request: lifecycle_request(),
                    storage: &storage,
                    classes,
                    deadline,
                    status: status(),
                    mode,
                    resume,
                    #[cfg(feature = "service-thermal-convergence-v1")]
                    scope: None,
                },
            )
            .await?;
        let debt = reconciled.after(owed, attempts);
        self.owe(key, debt);
        // A debt left owed ran nothing possibly submitted and has retries
        // left, or was deferred (`Reconciled::after`): a step the write epoch
        // overtook, or one refused transiently at an unmoved epoch, such as
        // an adapter not classified yet. It runs again once the epoch moves.
        Ok(if matches!(debt, Debt::Owed { .. }) {
            Attempt::Owed(reconciled.snapshot)
        } else {
            Attempt::Done(reconciled.snapshot)
        })
    }

    /// Whether a lifecycle cause still owes an attempt, which runs before
    /// any convergence (`convergence_loop`).
    #[cfg(feature = "service-thermal-convergence-v1")]
    pub(super) fn lifecycle_pending(&self) -> bool {
        self.lifecycle
            .borrow()
            .as_ref()
            .is_some_and(|tick| tick.pending.is_some())
    }

    /// The console session's interactive user: the latest admitted reporter
    /// when it reported from the console session, with its process, else the
    /// console user with this Service's process as the steps' recorded
    /// caller. It is a candidate only: the attempt's capture proves the
    /// session, its unlocked state, the primary SID and logon afresh. Test
    /// composition without a console probe reads the reporter.
    pub(super) fn lifecycle_pair(&self) -> Option<Pair> {
        let reporter = self.application.borrow().as_ref().map(|a| Pair {
            sid: a.sid.clone(),
            session: a.session,
            pid: a.pid,
        });
        let Some(read) = self.console.get() else {
            return reporter;
        };
        let (sid, session) = read()?;
        Some(match reporter {
            Some(reporter) if reporter.sid == sid && reporter.session == session => reporter,
            _ => Pair {
                sid,
                session,
                pid: std::process::id(),
            },
        })
    }

    /// Renews the reporter's lease when `renew`, else clears its foreground
    /// once the lease has expired with an application profile active, as
    /// managed `SetApplicationContext(null)` does, and owes its pair every
    /// class. Says whether it expired. The reporter cannot renew while the
    /// system sleeps, and Windows `Instant` counts that time, so a boundary,
    /// low power or a gap in the ticks renews instead: otherwise the lease
    /// would expire at every wake and the next report would restore the
    /// application profile again.
    fn renew_or_expire_lease(&self, renew: bool) -> bool {
        let mut application = self.application.borrow_mut();
        let Some(application) = application.as_mut() else {
            return false;
        };
        let now = Instant::now();
        if renew {
            application.expires = application.expires.max(now + APPLICATION_LEASE);
            return false;
        }
        if now < application.expires || application.foreground.active_id().is_none() {
            return false;
        }
        application.foreground = Foreground::default();
        self.owe(
            (application.sid.clone(), application.session),
            Debt::owed(Classes::ALL),
        );
        true
    }

    /// Ends the reporter's lease now, as 15 s without a renewal would.
    #[cfg(test)]
    pub(in super::super::super) fn expire_application_lease(&self) {
        if let Some(application) = self.application.borrow_mut().as_mut() {
            application.expires = Instant::now();
        }
    }

    /// Dates the last tick, its console read and its last attempt whose
    /// steps ran `by` earlier, as that much time without a tick would.
    #[cfg(test)]
    pub(in super::super::super) fn age_lifecycle_tick(&self, by: std::time::Duration) {
        if let Some(tick) = self.lifecycle.borrow_mut().as_mut() {
            tick.at -= by;
            if let Some((_, at)) = tick.console.read.as_mut() {
                *at -= by;
            }
            if let Some(at) = tick.pending.as_mut().and_then(|p| p.stepped.as_mut()) {
                *at -= by;
            }
            if let Some(at) = tick.boundary.as_mut() {
                *at -= by;
            }
        }
    }

    /// Dates a resume hold `by` earlier, as that much time would.
    #[cfg(test)]
    pub(in super::super::super) fn age_drift_hold(&self, by: std::time::Duration) {
        self.drift.lock().unwrap().age_hold(by);
    }

    /// Forgets every drift latch, OpenBlade's own confirmed state and each
    /// user's startup reconciliation, as a Service restart does: the next
    /// reading only sets the baseline, and the next reconciliation applies
    /// over drift.
    #[cfg(test)]
    pub(in super::super::super) fn restart_drift(&self) {
        *self.drift.lock().unwrap() = Default::default();
        self.startup.borrow_mut().clear();
    }

    /// The inter-tick gap that renews the lease (`SLEPT`).
    #[cfg(test)]
    pub(in super::super::super) fn lifecycle_sleep_gap() -> std::time::Duration {
        SLEPT
    }

    /// The least time between two stepped attempts of one cause (`PACE`).
    #[cfg(test)]
    pub(in super::super::super) fn lifecycle_pace() -> std::time::Duration {
        PACE
    }

    /// How often an unnamed console user is read again (`UNNAMED_POLL`).
    #[cfg(test)]
    pub(in super::super::super) fn lifecycle_unnamed_poll() -> std::time::Duration {
        UNNAMED_POLL
    }

    /// Holds the mutation gate, as a request in flight does.
    #[cfg(test)]
    pub(in super::super::super) fn hold_mutation_gate(&self) -> tokio::sync::MutexGuard<'_, ()> {
        self.gate.try_lock().unwrap()
    }
}

/// A fresh trigger id for one lifecycle reconciliation, from which
/// `step_request` derives each step's journal id. The journal refuses a
/// second row chain under the id of its last row, so the id is distinct
/// across ticks and Service restarts: the process id, the wall clock and a
/// process-wide counter.
fn lifecycle_request() -> String {
    static NEXT: AtomicU64 = AtomicU64::new(0);
    let at = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map_or(0, |elapsed| elapsed.as_nanos());
    format!(
        "OpenBlade.LifecycleReconcile\n{}\n{at}\n{}",
        std::process::id(),
        NEXT.fetch_add(1, std::sync::atomic::Ordering::Relaxed)
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    fn mark(gate: Option<u64>, power: Option<u64>) -> Mark {
        Mark { gate, power }
    }

    #[test]
    fn only_a_moved_gate_or_a_changed_known_power_kind_is_a_cause() {
        let none = Causes::default();
        assert_eq!(
            Causes::between(mark(Some(2), Some(1)), mark(Some(2), Some(1))),
            none
        );
        assert_eq!(
            Causes::between(mark(Some(2), Some(1)), mark(Some(3), Some(1))),
            Causes {
                boundary: true,
                power: false
            }
        );
        // An unreadable gate after a readable one is a boundary; the tick
        // then reads the gate as low and runs nothing.
        assert!(Causes::between(mark(Some(2), None), mark(None, None)).boundary);
        assert_eq!(
            Causes::between(mark(Some(2), Some(1)), mark(Some(2), Some(2))),
            Causes {
                boundary: false,
                power: true
            }
        );
        // A first or failed power read is not a power boundary.
        assert_eq!(
            Causes::between(mark(Some(2), None), mark(Some(2), Some(4))),
            none
        );
        assert_eq!(
            Causes::between(mark(Some(2), Some(4)), mark(Some(2), None)),
            none
        );
    }

    #[test]
    fn lifecycle_trigger_ids_derive_distinct_step_ids() {
        let first = lifecycle_request();
        let second = lifecycle_request();
        assert_ne!(first, second);
        for step in [Step::Performance, Step::Fans] {
            let id = step_request(&first, step);
            assert!(is_step_request(&id), "{id}");
            assert_ne!(id, step_request(&second, step));
        }
    }

    #[test]
    fn a_pending_cause_waits_for_a_moved_known_epoch_without_a_budget_of_its_own() {
        let now = Instant::now();
        let fresh = Pending::new(true);
        assert!(fresh.due(None, now) && fresh.due(Some(4), now));
        let mut cause = fresh.wait(Some(4));
        assert!(cause.owe);
        assert!(!cause.due(Some(4), now));
        assert!(!cause.due(None, now));
        assert!(cause.due(Some(5), now));
        // Refusals before any step spend nothing and wait no `PACE`: the
        // cause outlasts any number of epoch moves until an attempt settles
        // it.
        for epoch in 5..50 {
            cause = cause.wait(Some(epoch));
            assert!(cause.due(Some(epoch + 1), now));
        }
        // A refusal at an unknown epoch waits for a known one.
        let unknown = Pending::new(false).wait(None);
        assert!(!unknown.due(None, now) && unknown.due(Some(1), now));
    }

    /// After steps that left the debt owed, a retry needs both a moved epoch
    /// and `PACE`; epoch moves within it only mark the cause due. A later
    /// refusal before any step keeps the pace.
    #[test]
    fn a_stepped_cause_waits_for_its_pace_as_well_as_a_moved_epoch() {
        let at = Instant::now();
        let cause = Pending::new(false).stepped(Some(4), at);
        assert!(!cause.due(Some(4), at + PACE));
        for moved in [Some(5), Some(9)] {
            assert!(!cause.due(moved, at));
            assert!(!cause.due(moved, at + PACE - std::time::Duration::from_millis(1)));
            assert!(cause.due(moved, at + PACE));
        }
        let refused = cause.wait(Some(5));
        assert!(!refused.due(Some(6), at));
        assert!(refused.due(Some(6), at + PACE));
    }

    /// The tick's three intervals.
    #[test]
    fn the_lifecycle_intervals_are_pinned() {
        assert_eq!(SLEPT, std::time::Duration::from_secs(5));
        assert_eq!(PACE, std::time::Duration::from_secs(2));
        assert_eq!(UNNAMED_POLL, std::time::Duration::from_secs(3));
    }
}

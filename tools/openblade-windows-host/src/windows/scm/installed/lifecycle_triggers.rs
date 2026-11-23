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
//! polls an unmoved epoch. A newer cause supersedes a pending one.
use super::*;
use std::sync::atomic::AtomicU64;

/// An inter-tick gap this long is time the Service did not run, such as
/// Modern Standby without a suspend notice: the reporter could not renew
/// then either, so the lease is renewed as at a gate boundary. Every tick
/// stamps its time, after its attempt and also when a request holds the
/// mutation gate, so neither an attempt nor a long request is a gap; only a
/// stall of the tick itself this long renews early.
const SLEPT: std::time::Duration = std::time::Duration::from_secs(5);

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
    /// The ordinary write epoch the tick last read.
    epoch: Option<u64>,
    console: Console,
    /// The cause it still owes an attempt.
    pending: Option<Pending>,
}
/// What the tick knows of the console user.
#[derive(Clone, Debug, Default, PartialEq, Eq)]
struct Console {
    /// The last read named a console user. While none is known, every tick
    /// reads again, so a logon is seen without a further notice.
    known: bool,
    /// The last console user activated, as managed's `activeUserSid`.
    activated: Option<String>,
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
}
impl Pending {
    fn new(owe: bool) -> Self {
        Self { owe, tried: None }
    }
    /// Whether an attempt runs at write epoch `epoch` now.
    fn due(&self, epoch: Option<u64>) -> bool {
        match self.tried {
            None => true,
            Some(tried) => epoch.is_some() && epoch != tried,
        }
    }
    /// The cause after an attempt at `epoch` that left it pending: it waits
    /// for that epoch to move. It spends nothing itself: a refusal before
    /// any step read no firmware, and the debt counts its steps' retries.
    fn wait(self, epoch: Option<u64>) -> Self {
        Self {
            tried: Some(epoch),
            ..self
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
struct Pair {
    sid: String,
    session: u32,
    pid: u32,
}
/// How one attempt ended.
enum Attempt {
    /// Its steps ran and left nothing owed, or nothing was owed.
    Done(Option<Snapshot>),
    /// Refused before any step, or its steps left the debt owed with
    /// nothing possibly submitted: retried once the write epoch has moved.
    Retry(Option<Snapshot>),
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
            // The Service is running while a request holds the gate: no gap.
            if let Some(tick) = self.lifecycle.borrow_mut().as_mut() {
                tick.at = Instant::now();
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
        let prior = self.lifecycle.borrow().clone();
        let (console, activation) = self.observe_console(prior.as_ref(), epoch);
        let low = self.gate() == Gate::Low;
        let (causes, expired, mut pending) = match &prior {
            // The first tick has no transition to compare, only the console
            // user's activation.
            None => (Causes::default(), false, None),
            Some(prior) => {
                let causes = Causes::between(prior.mark, mark);
                let slept = Instant::now().saturating_duration_since(prior.at) >= SLEPT;
                let expired = self.renew_or_expire_lease(causes.boundary || slept || low);
                (causes, expired, prior.pending)
            }
        };
        if causes.boundary || causes.power || expired || activation {
            let owe = causes.power || activation || pending.is_some_and(|p| p.owe);
            pending = Some(Pending::new(owe));
        }
        let Some(mut cause) = pending.filter(|p| !low && p.due(epoch)) else {
            self.stamp(mark, epoch, console, pending);
            return Ok(None);
        };
        let attempt = match self.lifecycle_pair() {
            None => Attempt::Retry(None),
            Some(pair) => {
                if std::mem::take(&mut cause.owe) {
                    // A new change at every class, as a changed report owes.
                    self.debts
                        .borrow_mut()
                        .insert((pair.sid.clone(), pair.session), Debt::owed(Classes::ALL));
                }
                self.attempt(mailbox, pair, status).await?
            }
        };
        let (pending, snapshot) = match attempt {
            Attempt::Done(snapshot) => (None, snapshot),
            Attempt::Retry(snapshot) => (Some(cause.wait(epoch)), snapshot),
        };
        self.stamp(mark, epoch, console, pending);
        Ok(snapshot)
    }

    /// Records the tick's state, stamped now: after any attempt, so the
    /// attempt's own time is no gap.
    fn stamp(&self, mark: Mark, epoch: Option<u64>, console: Console, pending: Option<Pending>) {
        *self.lifecycle.borrow_mut() = Some(Tick {
            mark,
            at: Instant::now(),
            epoch,
            console,
            pending,
        });
    }

    /// Reads the console user when it may have changed: at the first tick,
    /// after the write epoch moved (every session notice fences it), and
    /// while none is known. Says whether a user other than the last one
    /// activated is now the console user. Test composition without a console
    /// probe activates no one.
    fn observe_console(&self, prior: Option<&Tick>, epoch: Option<u64>) -> (Console, bool) {
        let last = prior.map(|tick| tick.console.clone()).unwrap_or_default();
        let Some(read) = self.console.get() else {
            return (last, false);
        };
        if prior.is_some_and(|tick| tick.console.known && tick.epoch == epoch) {
            return (last, false);
        }
        let Some((sid, _)) = read() else {
            return (
                Console {
                    known: false,
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
            },
            activation,
        )
    }

    /// One attempt for `pair`'s debt.
    async fn attempt(
        &self,
        mailbox: &MailboxSource,
        Pair { sid, session, pid }: Pair,
        status: impl FnOnce() -> Snapshot,
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
            return Ok(Attempt::Retry(None));
        };
        reader.custody().verify_integrity()?;
        // A reconciliation writes, so it takes the write capture, which
        // refuses in low power and checks the console session, its unlocked
        // state and the primary token's SID afresh.
        let (storage, fresh) = match self.capture(&sid, session, reader.custody()) {
            Ok(captured) => captured,
            Err(error) if is_fatal_admission(&error) => return Err(error),
            Err(_) => return Ok(Attempt::Retry(None)),
        };
        // As a report's: a fenced store or unclassified power runs nothing
        // and leaves the debt as it was. A fenced store stays fenced until
        // read-only reconciliation settles its row, so a possibly submitted
        // step never runs again.
        let TriggerAdmission::Admitted(classes) = admit_trigger(&storage, fresh.as_ref(), owed)?
        else {
            return Ok(Attempt::Retry(None));
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
                    deadline: Instant::now() + crate::service_frame::RECONCILE_BUDGET,
                    status: status(),
                },
            )
            .await?;
        let debt = reconciled.after(owed, attempts);
        self.debts.borrow_mut().insert(key, debt);
        // A debt left owed ran nothing possibly submitted and has retries
        // left, or was deferred (`Reconciled::after`): a step the write epoch
        // overtook, or one refused transiently at an unmoved epoch, such as
        // an adapter not classified yet. It runs again once the epoch moves.
        Ok(if matches!(debt, Debt::Owed { .. }) {
            Attempt::Retry(reconciled.snapshot)
        } else {
            Attempt::Done(reconciled.snapshot)
        })
    }

    /// The console session's interactive user: the latest admitted reporter
    /// when it reported from the console session, with its process, else the
    /// console user with this Service's process as the steps' recorded
    /// caller. It is a candidate only: the attempt's capture proves the
    /// session, its unlocked state, the primary SID and logon afresh. Test
    /// composition without a console probe reads the reporter.
    fn lifecycle_pair(&self) -> Option<Pair> {
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
        self.debts.borrow_mut().insert(
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

    /// Dates the last tick `by` earlier, as a stall of the Service would.
    #[cfg(test)]
    pub(in super::super::super) fn age_lifecycle_tick(&self, by: std::time::Duration) {
        if let Some(tick) = self.lifecycle.borrow_mut().as_mut() {
            tick.at -= by;
        }
    }

    /// The inter-tick gap that renews the lease (`SLEPT`).
    #[cfg(test)]
    pub(in super::super::super) fn lifecycle_sleep_gap() -> std::time::Duration {
        SLEPT
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
        let fresh = Pending::new(true);
        assert!(fresh.due(None) && fresh.due(Some(4)));
        let mut cause = fresh.wait(Some(4));
        assert!(cause.owe);
        assert!(!cause.due(Some(4)));
        assert!(!cause.due(None));
        assert!(cause.due(Some(5)));
        // Refusals before any step spend nothing: the cause outlasts any
        // number of epoch moves until an attempt settles it.
        for epoch in 5..50 {
            cause = cause.wait(Some(epoch));
            assert!(cause.due(Some(epoch + 1)));
        }
        // A refusal at an unknown epoch waits for a known one.
        let unknown = Pending::new(false).wait(None);
        assert!(!unknown.due(None) && unknown.due(Some(1)));
    }
}

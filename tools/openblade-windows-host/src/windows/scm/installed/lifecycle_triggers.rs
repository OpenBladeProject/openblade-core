//! Lifecycle-driven reconciliation of owed intent (`service-profile-triggers-v1`):
//! the Service's own 500 ms tick (`Runtime::reconcile_lifecycle`) applies what
//! the reporting pair owes after three lifecycle transitions, as managed runs
//! `ApplyFullTransitionAsync` after them without any request:
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
//!   (managed `ExpireApplicationContextAsync`): the foreground is cleared and
//!   the pair owes a reconciliation at every class, so the power profile
//!   applies.
//!
//! The tick reconciles only the latest admitted reporter's pair, under the
//! mutation gate, through the request path's own `reconcile_locked`: every
//! step captures fresh exact-device, power, session, ownership and lifecycle
//! admission, writes under its own journal id, and stops on ambiguity. A
//! fenced store, a refused capture or an unclassified Windows power source
//! runs nothing and keeps the debt. A transition consumed while the pair
//! cannot be admitted (for example a locked session after resume) leaves the
//! debt to the next report or transition; the tick never polls for one.
use super::*;
use std::sync::atomic::AtomicU64;

/// The lease a reporter holds without renewal (managed
/// `ApplicationContextLeaseDuration`).
const LEASE: std::time::Duration = std::time::Duration::from_secs(15);

/// What the lifecycle tick saw: the gate generation and the Windows power
/// generation, each `None` while unreadable.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(in super::super) struct Mark {
    gate: Option<u64>,
    power: Option<u64>,
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

impl Runtime {
    /// One lifecycle tick: reads the gate and power generations, and when a
    /// transition since the last tick leaves the reporting pair owing a
    /// reconciliation and the gate is clear, runs it once from `status`.
    /// Returns the reconciliation's status, to publish as a report's is.
    ///
    /// A tick that finds the mutation gate held reads nothing and keeps its
    /// mark, so the transition is seen by a later tick; the request holding
    /// the gate may have settled the debt by then. Debts change only under
    /// the gate, so a request's own settlement cannot overwrite a debt this
    /// tick owed, or the reverse.
    pub(in super::super::super) async fn reconcile_lifecycle(
        &self,
        mailbox: &MailboxSource,
        status: Snapshot,
    ) -> io::Result<Option<Snapshot>> {
        let Ok(_serial) = self.gate.try_lock() else {
            return Ok(None);
        };
        verify_retained_failure(&self.fatal)?;
        let now = Mark {
            gate: self
                .low_power
                .get()
                .map_or(Some(0), |read| read().map(|t| t.generation)),
            power: self.power_generation.get().and_then(|read| read()),
        };
        let Some(prior) = self.lifecycle_mark.replace(Some(now)) else {
            return Ok(None);
        };
        let causes = Causes::between(prior, now);
        let low = self.gate() == Gate::Low;
        let Some((sid, session, pid)) = self.lifecycle_pair(causes, low) else {
            return Ok(None);
        };
        let key = (sid.clone(), session);
        let expired = !low && self.expire_lease();
        if causes.power || expired {
            // A new change at every class, as a changed report owes.
            self.debts
                .borrow_mut()
                .insert(key.clone(), Debt::owed(Classes::ALL));
        }
        if low || !(causes.boundary || causes.power || expired) {
            return Ok(None);
        }
        let Debt::Owed {
            classes: owed,
            attempts,
        } = self.debt(&key)
        else {
            return Ok(None);
        };
        let MailboxSource::Reader(reader) = mailbox else {
            return Ok(None);
        };
        reader.custody().verify_integrity()?;
        // A reconciliation writes, so it takes the write capture, which
        // refuses in low power and checks the console session, its unlocked
        // state and the primary token's SID afresh.
        let (storage, fresh) = match self.capture(&sid, session, reader.custody()) {
            Ok(captured) => captured,
            Err(error) if is_fatal_admission(&error) => return Err(error),
            Err(_) => return Ok(None),
        };
        // As a report's: a fenced store or unclassified power runs nothing
        // and leaves the debt as it was.
        let classes = owed.intersection(Classes::admitted(fresh.as_ref()));
        if storage.pending() {
            return Ok(None);
        }
        fresh.verify_health()?;
        let admitted = !classes.is_empty() && fresh.authorize().is_some();
        fresh.verify_health()?;
        if !admitted {
            return Ok(None);
        }
        let reconciled = self
            .reconcile_locked(
                mailbox,
                Reconcile {
                    // The reporter's identity as its last report was admitted:
                    // an interactive user controlling `session`. It submits
                    // nothing itself; each step's capture and every write's
                    // authorization re-check that session and user.
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
                    status,
                },
            )
            .await?;
        self.debts
            .borrow_mut()
            .insert(key, reconciled.after(owed, attempts));
        Ok(reconciled.snapshot)
    }

    /// The latest admitted reporter's `(sid, session, pid)`, or `None` when
    /// no report was admitted. A gate boundary renews its lease first: the
    /// reporter cannot renew while the system sleeps, and Windows `Instant`
    /// counts that time, so the lease would otherwise expire at every resume
    /// and the next report would restore the application profile again.
    fn lifecycle_pair(&self, causes: Causes, low: bool) -> Option<(String, u32, u32)> {
        let mut application = self.application.borrow_mut();
        let application = application.as_mut()?;
        if causes.boundary || low {
            application.expires = application.expires.max(Instant::now() + LEASE);
        }
        Some((
            application.sid.clone(),
            application.session,
            application.pid,
        ))
    }

    /// Clears the reporter's foreground once its lease has expired with an
    /// application profile active, as managed `SetApplicationContext(null)`
    /// does, and says whether it did. The cleared foreground resolves the
    /// power profile, and a later report selects its pair again.
    fn expire_lease(&self) -> bool {
        let mut application = self.application.borrow_mut();
        let Some(application) = application
            .as_mut()
            .filter(|a| Instant::now() >= a.expires && a.foreground.active_id().is_some())
        else {
            return false;
        };
        application.foreground = Foreground::default();
        true
    }

    /// Ends the reporter's lease now, as 15 s without a renewal would.
    #[cfg(test)]
    pub(in super::super::super) fn expire_application_lease(&self) {
        if let Some(application) = self.application.borrow_mut().as_mut() {
            application.expires = Instant::now();
        }
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
}

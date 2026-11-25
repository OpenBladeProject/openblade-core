//! Installed `reapplyChangedSettings`: managed `ReapplyChangedSettingsAsync`
//! for the subsystems native tracks for drift, performance and fans
//! (`thermal_drift::Tracker::external`).
//!
//! Under the operation gate, in managed's order:
//!
//! - a caller outside the control session is refused;
//! - an unsettled journal row or a pending GPU clock confirmation (managed
//!   `pendingWriteConfirmations`) and a reservation still in the ledger each
//!   block before anything is written;
//! - a ledger that holds tombstones or budgets is rearmed: both lists and
//!   the terminal result are cleared by compare-and-swap on the bytes the
//!   store holds (`Storage::replace_convergence_ledger`). A replacement that
//!   fails blocks. A failed write closes the store, as managed's failed
//!   persist leaves its ledger unauthoritative; a ledger the store no longer
//!   holds as read, or one the encoder refuses, is kept unclosed;
//! - with no drift the reply is Stored;
//! - each drifted subsystem is applied from the current settings by one
//!   reconciliation scoped to those subsystems (`reconcile_locked`), which
//!   applies over the drift as managed's explicit reapply does. Every step
//!   keeps its own capture, write admission, readback and
//!   stop-on-ambiguity path; nothing is replayed.
//!
//! After a rearm is attempted, whether or not it persisted, the status is
//! republished at once, before any step runs, as managed publishes
//! AwaitingObservation right after its rearm. A request a client disconnect
//! drops after that point leaves the rearmed status published. Native
//! derives the convergence member from the ledger at each publication.
//!
//! Deviations, each because native holds no managed state for them: the
//! request capture (`capture_intent`) precedes the pending and ledger checks,
//! so a competing controller, a locked session or unknown AC status refuses
//! the request before them, as it refuses a policy change; unknown Windows
//! power blocks with managed's unclassified-adapter reply, while an adapter
//! only a step's own reading finds unclassified is that step's refusal; a
//! power class changed since the last reconciliation is the lifecycle
//! tick's, so no full transition runs first; and native has no automatic
//! fan-stop arming to pause.
use super::*;
use crate::installed::readiness::thermal_drift::{FANS, PERFORMANCE};

const SESSION: &str = "The active Windows session changed before settings could be reapplied.";
pub(in super::super::super) const PENDING: &str =
    "OpenBlade must confirm the current laptop state before settings can be reapplied.";
pub(in super::super::super) const IN_FLIGHT: &str = "OpenBlade must reconcile the reserved thermal transition before changed settings can be reapplied.";
pub(in super::super::super) const NOT_RETIRED: &str = "The durable thermal convergence suppression could not be retired, so no background retry was armed.";
const UNCHANGED: &str = "No settings changed outside OpenBlade.";
pub(in super::super::super) const UNCLASSIFIED: &str =
    "Changed settings cannot be reapplied until the external power adapter is classified.";
/// Managed's per-subsystem replies while manual fans stay on both sides of
/// a performance reapply (`RequiresManualFanPerformanceBridge`).
pub(in super::super::super) const PERFORMANCE_BRIDGE: &str = "Power mode cannot be reapplied while manual fan control is active. Switch fans to Automatic, then reapply.";
pub(in super::super::super) const FANS_BRIDGE: &str = "Fans were not reapplied because OpenBlade kept manual fan control unchanged while Power mode recovery was blocked.";

/// Managed `ControlOperationStatus`.
const STORED: u8 = 0;
const APPLIED: u8 = 1;
const FAILED: u8 = 3;

/// What one reapply did: its reply and the status its steps published.
pub(in super::super::super) struct Reapplied {
    pub(in super::super::super) outcome: Outcome,
    pub(in super::super::super) snapshot: Option<Snapshot>,
}
impl Reapplied {
    fn reply(status: u8, message: &str) -> Self {
        Self {
            outcome: Outcome {
                status,
                message: message.into(),
            },
            snapshot: None,
        }
    }
    fn blocked(message: impl Into<String>) -> Self {
        Self {
            outcome: Outcome::blocked(message),
            snapshot: None,
        }
    }
}

/// The reapply's scope: only the drifted subsystems run. It adds no
/// admission of its own; each write keeps its step's.
struct Drifted {
    performance: bool,
    fans: bool,
}
impl thermal_drift::Scope for Drifted {
    fn declares(&self, fans: bool) -> bool {
        if fans { self.fans } else { self.performance }
    }
    fn open(&self) -> bool {
        true
    }
    fn admits(&self, _fans: bool) -> bool {
        true
    }
}

/// Managed `DeviceSubsystemFormatter.FormatList`.
fn names(subsystems: &[i32]) -> String {
    subsystems
        .iter()
        .map(|&subsystem| {
            if subsystem == FANS {
                "Fans"
            } else {
                "Power mode"
            }
        })
        .collect::<Vec<_>>()
        .join(", ")
}

/// Managed's reply from what is still changed among `targets`, with each
/// remaining subsystem's distinct reason (`FormatRemainingReasons`).
fn summary(targets: &[i32], remaining: &[i32], results: &[(i32, Outcome)]) -> Outcome {
    let reapplied: Vec<i32> = targets
        .iter()
        .copied()
        .filter(|subsystem| !remaining.contains(subsystem))
        .collect();
    if remaining.is_empty() {
        return Outcome {
            status: APPLIED,
            message: format!(
                "OpenBlade reapplied {} from the current settings.",
                names(&reapplied)
            ),
        };
    }
    let mut reasons: Vec<&str> = Vec::new();
    for subsystem in remaining {
        for (_, outcome) in results.iter().filter(|(of, _)| of == subsystem) {
            let message = outcome.message.as_str();
            if !message.trim().is_empty() && !reasons.contains(&message) {
                reasons.push(message);
            }
        }
    }
    let reasons = if reasons.is_empty() {
        String::new()
    } else {
        format!(" {}", reasons.join(" "))
    };
    let still = names(remaining);
    let message = if reapplied.is_empty() {
        format!(
            "OpenBlade could not reapply the changed settings. Still changed: {still}.{reasons}"
        )
    } else {
        format!(
            "OpenBlade reapplied {}. Still changed: {still}.{reasons}",
            names(&reapplied)
        )
    };
    let status = if results.iter().any(|(_, outcome)| outcome.status == FAILED) {
        FAILED
    } else {
        2
    };
    Outcome { status, message }
}

impl Runtime {
    /// Managed `ReapplyChangedSettingsAsync` for one admitted request.
    /// `publish` publishes the status again from the ledger the store now
    /// holds; it runs as soon as a rearm was attempted, as managed publishes
    /// right after its rearm, so a request dropped later leaves no stale
    /// suppression published.
    #[allow(clippy::too_many_arguments)]
    pub(in super::super::super) async fn reapply_changed(
        &self,
        mailbox: MailboxSource,
        caller: Caller,
        sid: String,
        request: String,
        deadline: Instant,
        status: Snapshot,
        publish: &dyn Fn() -> io::Result<()>,
    ) -> io::Result<Reapplied> {
        // Managed `stillControlsSession`.
        if caller.logon != Logon::Interactive || caller.control_session != Some(caller.session) {
            return Ok(Reapplied::blocked(SESSION));
        }
        #[cfg(test)]
        self.entered.set(self.entered.get() + 1);
        // One mutation at a time, as managed's operation gate allows.
        let _serial = self.gate.lock().await;
        verify_retained_failure(&self.fatal)?;
        if Instant::now() >= deadline {
            return Ok(Reapplied::blocked(DEADLINE_PASSED));
        }
        let MailboxSource::Reader(reader) = &mailbox else {
            return Ok(Reapplied::blocked(
                "Authenticated thermal custody is unavailable.",
            ));
        };
        reader.custody().verify_integrity()?;
        let (storage, fresh) = match self.capture_intent(&sid, caller.session, reader.custody()) {
            Ok(captured) => captured,
            Err(error) if is_fatal_admission(&error) => return Err(error),
            Err(error) => {
                if is_competing_controller(&error) {
                    self.drop_for_controller(&sid, caller.session);
                }
                return Ok(Reapplied::blocked(format!(
                    "Saved configuration or the current session is unavailable: {error}"
                )));
            }
        };
        reader.retain_policy(self.thermal_policy(sid.clone(), caller.session, reader.custody()));
        if storage.pending() || self.gpu_fenced(deadline).await {
            return Ok(Reapplied::blocked(PENDING));
        }
        // A ledger the store cannot admit holds no reservation native knows
        // of, as managed's unauthoritative ledger is not rearmed either.
        let held = storage.convergence_ledger();
        if held.as_ref().is_some_and(|held| held.in_flight.is_some()) {
            return Ok(Reapplied::blocked(IN_FLIGHT));
        }
        if let Some(held) =
            held.filter(|held| !held.tombstones.is_empty() || !held.budgets.is_empty())
        {
            let next = openblade_domain::thermal_convergence::State {
                tombstones: Vec::new(),
                budgets: Vec::new(),
                terminal_disposition: None,
                terminal_affected_components: Vec::new(),
                ..held.clone()
            };
            let replaced = storage.replace_convergence_ledger(&held, &next);
            // Rearmed, or refused. A failed write, including one the file
            // changed under, closes the store; a ledger the store no longer
            // holds as read, or one the encoder refuses, is kept unclosed.
            // Either way the status is published before any step runs.
            publish()?;
            if replaced.is_err() {
                return Ok(Reapplied::blocked(NOT_RETIRED));
            }
        }
        let external = self
            .drift
            .lock()
            .map_err(|_| io::Error::from(io::ErrorKind::Other))?
            .external();
        if external.is_empty() {
            return Ok(Reapplied::reply(STORED, UNCHANGED));
        }
        let classes = Classes::admitted(fresh.as_ref());
        if classes.is_empty() {
            return Ok(Reapplied::blocked(UNCLASSIFIED));
        }
        // Managed `DeviceSubsystemOrder.ForTransition` over the drifted
        // subsystems, and its manual-fan bridge: manual fans on both sides
        // leave a performance reapply unconfirmable, so neither runs.
        let manual = mailbox.confirmed_manual_fans() || storage.outstanding_fan();
        let next_manual = match self.next_manual(&storage, &sid, caller.session, classes) {
            Ok(next_manual) => next_manual,
            Err(error) if is_fatal_admission(&error) => return Err(error),
            // The steps read the same store and report its failure.
            Err(_) => false,
        };
        let order = if fans_first(manual, next_manual) {
            [FANS, PERFORMANCE]
        } else {
            [PERFORMANCE, FANS]
        };
        let targets: Vec<i32> = order
            .into_iter()
            .filter(|subsystem| external.contains(subsystem))
            .collect();
        let performance = targets.contains(&PERFORMANCE);
        if performance && manual && next_manual {
            let results: Vec<(i32, Outcome)> = targets
                .iter()
                .map(|&subsystem| {
                    let message = if subsystem == PERFORMANCE {
                        PERFORMANCE_BRIDGE
                    } else {
                        FANS_BRIDGE
                    };
                    (subsystem, Outcome::blocked(message))
                })
                .collect();
            return Ok(Reapplied {
                outcome: summary(&targets, &targets, &results),
                snapshot: None,
            });
        }
        let reconciled = self
            .reconcile_locked(
                &mailbox,
                Reconcile {
                    caller,
                    sid,
                    request,
                    storage: &storage,
                    classes,
                    deadline,
                    status,
                    mode: thermal_drift::Mode::Overwrite,
                    resume: false,
                    scope: Some(Arc::new(Drifted {
                        performance,
                        fans: targets.contains(&FANS),
                    })),
                },
            )
            .await?;
        let remaining: Vec<i32> = self
            .drift
            .lock()
            .map_err(|_| io::Error::from(io::ErrorKind::Other))?
            .external()
            .into_iter()
            .filter(|subsystem| targets.contains(subsystem))
            .collect();
        let results: Vec<(i32, Outcome)> = reconciled
            .outcomes
            .into_iter()
            .map(|(step, outcome)| {
                let subsystem = if step == Step::Fans {
                    FANS
                } else {
                    PERFORMANCE
                };
                (subsystem, outcome)
            })
            .collect();
        Ok(Reapplied {
            outcome: summary(&targets, &remaining, &results),
            snapshot: reconciled.snapshot,
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn outcome(status: u8, message: &str) -> Outcome {
        Outcome {
            status,
            message: message.into(),
        }
    }
    fn pair(outcome: Outcome) -> (u8, String) {
        (outcome.status, outcome.message)
    }

    /// Managed `ReapplyChangedSettingsAsync`'s three summaries: everything
    /// reapplied, nothing reapplied, and a partial reapply, each with the
    /// distinct reasons of what is still changed, in transition order.
    #[test]
    fn the_summary_names_what_was_reapplied_and_what_is_still_changed() {
        let both = [FANS, PERFORMANCE];
        assert_eq!(
            pair(summary(&both, &[], &[])),
            (
                APPLIED,
                "OpenBlade reapplied Fans, Power mode from the current settings.".into()
            )
        );
        let refused = [
            (FANS, outcome(2, "Fans were refused.")),
            (PERFORMANCE, outcome(2, "Fans were refused.")),
            (PERFORMANCE, outcome(2, " ")),
        ];
        assert_eq!(
            pair(summary(&both, &both, &refused)),
            (
                2,
                "OpenBlade could not reapply the changed settings. Still changed: Fans, Power mode. Fans were refused.".into()
            )
        );
        let partial = [
            (FANS, outcome(1, "Fans applied.")),
            (PERFORMANCE, outcome(FAILED, "Power mode failed.")),
        ];
        assert_eq!(
            pair(summary(&both, &[PERFORMANCE], &partial)),
            (
                FAILED,
                "OpenBlade reapplied Fans. Still changed: Power mode. Power mode failed.".into()
            )
        );
        // A remaining subsystem whose steps gave no reason adds none.
        assert_eq!(
            pair(summary(&[PERFORMANCE], &[PERFORMANCE], &[])).1,
            "OpenBlade could not reapply the changed settings. Still changed: Power mode."
        );
    }
}

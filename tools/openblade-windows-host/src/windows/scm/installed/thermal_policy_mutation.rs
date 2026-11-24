//! Installed `setThermalControlPolicy` and
//! `acknowledgeThermalPolicyUpgradeDisclosure`: managed
//! `SetThermalControlPolicyAsync` and
//! `AcknowledgeThermalPolicyUpgradeDisclosureAsync`.
//!
//! Each stores one preference in the user's configuration aggregate and
//! applies no hardware; the policy changes no native write (see
//! `thermal_convergence`). Managed admits the change with the active session
//! and an admitted configuration only. A native change takes the request
//! capture as a profile save does, so a competing controller, a locked
//! session or an unknown AC status refuses it, and in low power the storage
//! capture stores it (a documented deviation). A store failure replies
//! `Failed` with the store's reason, where managed's exception ends the
//! request without a result.
use super::*;
use crate::installed::readiness::thermal_storage::{PolicyChange, PolicySave};

/// Managed reply texts, by request.
struct Replies {
    session: &'static str,
    recovery: &'static str,
}

const POLICY: Replies = Replies {
    session: "The active local Windows session changed before the thermal policy could be saved.",
    recovery: "Thermal policy changes are unavailable while configuration recovery is required.",
};
const DISCLOSURE: Replies = Replies {
    session: "The active local Windows session changed before the disclosure could be acknowledged.",
    recovery: "The thermal policy disclosure cannot be acknowledged while configuration recovery is required.",
};

/// Managed `ControlOperationStatus.Stored` and `Failed`.
const STORED: u8 = 0;
const FAILED: u8 = 3;

/// The managed `Stored` reply for `change` once the preferences hold it,
/// `already` when they held it before.
fn stored(change: PolicyChange, already: bool, disclosure: i32) -> String {
    use crate::service_status::thermal_convergence::{DISCLOSURE_PENDING, STRICT};
    match (change, already) {
        (PolicyChange::Set(STRICT), true) => "Strict thermal control is already active.",
        (PolicyChange::Set(_), true) => "Maintain desired thermal state is already selected.",
        (PolicyChange::Set(STRICT), false) => {
            "Strict thermal control is active; externally drifted thermal state remains advisory until an explicit command."
        }
        (PolicyChange::Set(_), false) if disclosure == DISCLOSURE_PENDING => {
            "Maintain desired thermal state is selected and will remain paused until the upgrade disclosure is acknowledged."
        }
        (PolicyChange::Set(_), false) => {
            "Maintain desired thermal state is selected; a fresh safe observation is required before background convergence."
        }
        (PolicyChange::Acknowledge, true) => {
            "The thermal policy disclosure is already acknowledged or was not required."
        }
        (PolicyChange::Acknowledge, false) => {
            "The Maintain desired state disclosure was acknowledged. A fresh safe observation is required before background convergence."
        }
    }
    .into()
}

impl Runtime {
    /// The reply, and whether the stored preferences changed.
    pub(in super::super) async fn set_thermal_policy(
        &self,
        mailbox: MailboxSource,
        caller: Caller,
        sid: String,
        change: PolicyChange,
    ) -> io::Result<(Outcome, bool)> {
        #[cfg(test)]
        self.entered.set(self.entered.get() + 1);
        // One mutation at a time, as managed's operation gate allows.
        let _serial = self.gate.lock().await;
        verify_retained_failure(&self.fatal)?;
        let replies = match change {
            PolicyChange::Set(_) => &POLICY,
            PolicyChange::Acknowledge => &DISCLOSURE,
        };
        let blocked = |message: String| Ok((Outcome::blocked(message), false));
        // Managed `stillControlsSession` and the active user.
        if caller.logon != Logon::Interactive || caller.control_session != Some(caller.session) {
            return blocked(replies.session.into());
        }
        let MailboxSource::Reader(reader) = &mailbox else {
            return blocked("Authenticated thermal custody is unavailable.".into());
        };
        reader.custody().verify_integrity()?;
        let (storage, _fresh) = match self.capture_intent(&sid, caller.session, reader.custody()) {
            Ok(captured) => captured,
            Err(error) if is_fatal_admission(&error) => return Err(error),
            Err(error) => {
                if is_competing_controller(&error) {
                    self.drop_for_controller(&sid, caller.session);
                }
                return blocked(format!(
                    "Saved configuration or the current session is unavailable: {error}"
                ));
            }
        };
        // The projection a profile save retains, so this user's policy
        // shows in status even before any thermal request.
        reader.retain_policy(self.thermal_policy(sid.clone(), caller.session, reader.custody()));
        // Every error `save_thermal_policy` returns comes from a read before
        // its transaction, so nothing was written; fatal evidence still ends
        // the Service, as a fatal capture does.
        let saved = match storage.save_thermal_policy(change) {
            Ok(saved) => saved,
            Err(error) if is_fatal_admission(&error) => return Err(error),
            Err(error) => PolicySave::Rejected(format!(
                "The requested intent was rejected: The saved configuration could not be read: {error}. The saved configuration is unchanged."
            )),
        };
        Ok(match saved {
            PolicySave::Unchanged(preferences) => (
                Outcome {
                    status: STORED,
                    message: stored(change, true, preferences.thermal_policy_upgrade_disclosure),
                },
                false,
            ),
            PolicySave::Stored(preferences) => (
                Outcome {
                    status: STORED,
                    message: stored(change, false, preferences.thermal_policy_upgrade_disclosure),
                },
                true,
            ),
            PolicySave::Rejected(message) => (
                Outcome {
                    status: FAILED,
                    message,
                },
                false,
            ),
            PolicySave::Quarantined => (Outcome::blocked(replies.recovery), false),
        })
    }
}

#[cfg(test)]
impl Runtime {
    /// The operation gate, for a test that holds it.
    pub(in super::super) fn operation_gate_for_tests(&self) -> Rc<tokio::sync::Mutex<()>> {
        self.gate.clone()
    }
}

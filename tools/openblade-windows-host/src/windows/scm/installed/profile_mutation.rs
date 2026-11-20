//! Installed `updateProfiles`: managed `UpdateProfilesAsync` up to its store.
//!
//! Saving profiles applies no hardware. Managed goes on to restore lighting
//! it had suppressed and, when the edit changes the active application's
//! intent, to run a full transition; this composition has no automatic
//! application or reconciliation yet, so it always answers for the stored
//! intent alone, as managed does on its default path.
use super::*;
use crate::installed::readiness::thermal_storage::ProfileSave;
use openblade_domain::profile_set::{ProfileSet, update::STORED_MESSAGE};

impl Runtime {
    /// Returns the reply and whether the saved set changed.
    pub(in super::super) async fn update_profiles(
        &self,
        mailbox: MailboxSource,
        caller: Caller,
        sid: String,
        profiles: &ProfileSet,
    ) -> io::Result<(Outcome, bool)> {
        #[cfg(test)]
        self.entered.set(self.entered.get() + 1);
        // One mutation at a time, as managed's operation gate allows.
        let _serial = self.gate.lock().await;
        verify_retained_failure(&self.fatal)?;
        let blocked = |message: String| Ok((Outcome::blocked(message), false));
        if caller.logon != Logon::Interactive || caller.control_session != Some(caller.session) {
            return blocked("The active interactive Windows user is required.".into());
        }
        let MailboxSource::Reader(reader) = mailbox else {
            return blocked("Authenticated thermal custody is unavailable.".into());
        };
        reader.custody().verify_integrity()?;
        // The per-user store only. No device, power or controller admission
        // is consulted: managed stores profiles without any, and nothing here
        // writes to hardware.
        let storage = match self.capture(&sid, caller.session, reader.custody()) {
            Ok((storage, _)) => storage,
            Err(error) if is_fatal_admission(&error) => return Err(error),
            Err(error) => {
                return blocked(format!(
                    "Saved configuration or the current session is unavailable: {error}"
                ));
            }
        };
        if storage.pending() {
            return blocked(storage.pending_reason()?.into());
        }
        // The same projection a thermal mutation retains, so the saved set
        // shows in this user's status even before any thermal request.
        reader.retain_policy(self.thermal_policy(sid, caller.session, reader.custody()));
        Ok(match storage.save_profiles(profiles)? {
            ProfileSave::Stored => (
                Outcome {
                    status: 0,
                    message: STORED_MESSAGE.into(),
                },
                true,
            ),
            // Managed `Failed`.
            ProfileSave::Rejected(message) => (Outcome { status: 3, message }, false),
            ProfileSave::Blocked(message) => (Outcome::blocked(message), false),
        })
    }
}

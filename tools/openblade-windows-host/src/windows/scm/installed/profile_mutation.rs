//! Installed `updateProfiles`: managed `UpdateProfilesAsync`.
//!
//! Storing profiles needs no device admission. Without
//! `service-profile-triggers-v1` saving applies no hardware and always
//! answers for the stored intent alone, as managed does on its default path.
//! With it, a save that changes the active application's thermal intent
//! applies that intent as managed's full transition does
//! (`Runtime::save_profiles_locked`), with every per-write admission.
//! Managed's restore of lighting it had suppressed is not composed here.
use super::*;
#[cfg(not(feature = "service-profile-triggers-v1"))]
use crate::installed::readiness::thermal_storage::ProfileSave;
use openblade_domain::profile_set::ProfileSet;
#[cfg(not(feature = "service-profile-triggers-v1"))]
use openblade_domain::profile_set::update::STORED_MESSAGE;

/// The reply, whether the saved set changed, and the status a
/// reconciliation the save triggered returned.
pub(in super::super) type Updated = (Outcome, bool, Option<Snapshot>);

impl Runtime {
    // Every caller, including the shared mutation tests, passes the admitted request fields individually.
    #[allow(clippy::too_many_arguments)]
    pub(in super::super) async fn update_profiles(
        &self,
        mailbox: MailboxSource,
        caller: Caller,
        sid: String,
        request: String,
        profiles: &ProfileSet,
        deadline: Instant,
        status: Snapshot,
    ) -> io::Result<Updated> {
        #[cfg(test)]
        self.entered.set(self.entered.get() + 1);
        // One mutation at a time, as managed's operation gate allows.
        let _serial = self.gate.lock().await;
        verify_retained_failure(&self.fatal)?;
        let blocked = |message: String| Ok((Outcome::blocked(message), false, None));
        if caller.logon != Logon::Interactive || caller.control_session != Some(caller.session) {
            return blocked("The active interactive Windows user is required.".into());
        }
        let MailboxSource::Reader(reader) = &mailbox else {
            return blocked("Authenticated thermal custody is unavailable.".into());
        };
        reader.custody().verify_integrity()?;
        // The per-user store. Managed stores profiles without device, power
        // or controller admission. A native save takes the write capture, so
        // a competing controller, a locked session or an unknown AC status
        // refuses it (a documented deviation). With triggers, a save in low
        // power takes the storage capture and its deferral instead.
        let (storage, fresh) = match self.capture_intent(&sid, caller.session, reader.custody()) {
            Ok(captured) => captured,
            Err(error) if is_fatal_admission(&error) => return Err(error),
            Err(error) => {
                // As a report's refused capture: nothing is owed for after
                // the controller leaves, and startup ends.
                #[cfg(feature = "service-profile-triggers-v1")]
                if is_competing_controller(&error) {
                    self.drop_for_controller(&sid, caller.session);
                }
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
        reader.retain_policy(self.thermal_policy(sid.clone(), caller.session, reader.custody()));
        #[cfg(feature = "service-profile-triggers-v1")]
        return self
            .save_profiles_locked(super::triggers::Saving {
                mailbox: &mailbox,
                caller,
                sid,
                request,
                storage: &storage,
                fresh,
                profiles,
                deadline,
                status,
            })
            .await;
        #[cfg(not(feature = "service-profile-triggers-v1"))]
        {
            // Only a reconciliation reads these, and none is composed.
            let _ = (request, fresh, deadline, status);
            Ok(match storage.save_profiles(profiles)? {
                ProfileSave::Stored => {
                    self.record_stored_profiles();
                    (
                        Outcome {
                            status: 0,
                            message: STORED_MESSAGE.into(),
                        },
                        true,
                        None,
                    )
                }
                // Managed `Failed`.
                ProfileSave::Rejected(message) => (Outcome { status: 3, message }, false, None),
                ProfileSave::Blocked(message) => (Outcome::blocked(message), false, None),
            })
        }
    }
}

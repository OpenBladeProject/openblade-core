//! Installed `updateProfiles`: managed `UpdateProfilesAsync`.
//!
//! Storing profiles needs no device admission. When an application profile
//! was active before the save and the save changes the active id, the
//! desired state or the effective laptop behaviour
//! (`SaveIntent::changed`), the saved intent is applied as managed's full
//! transition applies it (`Runtime::reconcile_locked`), with every per-write
//! admission. Managed's restore of lighting it had suppressed is not
//! composed here.
use super::*;
use crate::installed::readiness::thermal_storage::ProfileSave;
use openblade_domain::profile_set::{ProfileSet, update::STORED_MESSAGE};

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
        // The per-user store. Storing consults no device, power or controller
        // admission, as managed stores profiles without any.
        let (storage, fresh) = match self.capture(&sid, caller.session, reader.custody()) {
            Ok(captured) => captured,
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
        reader.retain_policy(self.thermal_policy(sid.clone(), caller.session, reader.custody()));
        // The terms managed compares at its active power source. Native learns
        // the external class only from the step's own adapter reading, so
        // each class fresh Windows power admits is compared
        // (`reconcile_classes`): a change at either external class applies,
        // and a step at an unchanged class finds its destination already
        // confirmed. Managed applies only on classified power, so unknown
        // power compares nothing. A store that cannot be read now applies
        // nothing after the save.
        let classes = reconcile_classes(fresh.as_ref());
        let foreground = self.foreground(&sid, caller.session);
        let intents = |foreground: &Foreground| {
            classes
                .iter()
                .map(|&class| storage.save_intent(foreground, class))
                .collect::<io::Result<Vec<_>>>()
        };
        let prior = if !classes.is_empty() && foreground.active_id().is_some() {
            match intents(&foreground)
                .and_then(|intents| Ok((intents, storage.manual_fans(&foreground)?)))
            {
                Ok(prior) => Some(prior),
                Err(error) if is_fatal_admission(&error) => return Err(error),
                Err(_) => None,
            }
        } else {
            None
        };
        match storage.save_profiles(profiles)? {
            ProfileSave::Stored => {}
            // Managed `Failed`.
            ProfileSave::Rejected(message) => {
                return Ok((Outcome { status: 3, message }, false, None));
            }
            ProfileSave::Blocked(message) => return Ok((Outcome::blocked(message), false, None)),
        }
        let stored = Outcome {
            status: 0,
            message: STORED_MESSAGE.into(),
        };
        // Managed `ProfileAutomationState.UpdateProfiles`: the active pair
        // survives only while its rule still holds its path. The save
        // committed, so a read that fails now leaves the stored reply.
        let next = match storage.retain_application(&foreground) {
            Ok(next) => next,
            Err(error) if is_fatal_admission(&error) => return Err(error),
            Err(_) => return Ok((stored, true, None)),
        };
        if let Some(application) = self
            .application
            .borrow_mut()
            .as_mut()
            .filter(|a| a.sid == sid && a.session == caller.session)
        {
            application.foreground = next.clone();
        }
        let Some((prior, prior_manual)) = prior else {
            return Ok((stored, true, None));
        };
        let changed = match intents(&next) {
            Ok(next) => prior
                .iter()
                .zip(&next)
                .any(|(prior, next)| prior.changed(next)),
            Err(error) if is_fatal_admission(&error) => return Err(error),
            Err(_) => false,
        };
        if !changed {
            return Ok((stored, true, None));
        }
        // Managed also needs a verified device with writes not blocked; every
        // write still passes its own admission.
        fresh.verify_health()?;
        let admitted = !storage.pending() && fresh.authorize().is_some();
        fresh.verify_health()?;
        if !admitted {
            return Ok((stored, true, None));
        }
        let (outcome, snapshot) = self
            .reconcile_locked(
                &mailbox,
                Reconcile {
                    caller,
                    sid,
                    request,
                    storage: &storage,
                    classes,
                    prior_manual: PriorManual(prior_manual),
                    deadline,
                    status,
                },
            )
            .await?
            .into_outcome(true, "Profiles were stored. ");
        Ok((outcome, true, snapshot))
    }
}

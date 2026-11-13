//! Installed explicit fan runtime; one original custody and one global journal.
use super::*;
use crate::service_status::FanOverride;
use crate::thermal_history::Entry;
use openblade_domain::{
    fan_history::{CONTRACT, FanRecord},
    fan_recipe::{FanIntent, Plan},
};
use serde_json::{Value, json};
pub(super) struct FanPermit {
    storage: Arc<Storage>,
    fresh: Arc<dyn Fresh>,
    request: String,
    owner: String,
    caller_pid: u32,
    session: u32,
    requested: Value,
    value: Option<FanOverride>,
    settings: Option<Value>,
    application: Option<String>,
    proof: ReadAdmission,
    selected: Mutex<Option<(i32, Value)>>,
    record: Mutex<Option<FanRecord>>,
    accepted: AtomicBool,
    owned: AtomicBool,
    release: AtomicBool,
    prepared: AtomicBool,
    fatal: Arc<Mutex<Option<&'static str>>>,
}
impl FanPermit {
    fn write(&self, progress: u8) -> io::Result<()> {
        let record = self
            .record
            .lock()
            .map_err(|_| io::ErrorKind::Other)?
            .clone()
            .ok_or(io::ErrorKind::PermissionDenied)?;
        let entry = Entry {
            request: self.request.clone(),
            owner: self.owner.clone(),
            user_sid: String::new(),
            caller_pid: self.caller_pid,
            session: self.session,
            requested: None,
            destination: 0,
            intent: record.hash()?,
            progress,
            fan: Some(record),
        };
        self.storage
            .record_fan(entry, self.cleanup())
            .inspect_err(|e| remember_failure(&self.fatal, e))
    }
}
impl Permit for FanPermit {
    fn result_admitted(&self, expected: ReadAdmission) -> bool {
        if self.cleanup() && self.storage.progress() == Some(50) {
            return verify_retained_failure(&self.fatal).is_ok()
                && self.fresh.verify_health().is_ok()
                && self.fresh.authorize_owned_stop() == Some(expected)
                && expected == self.proof;
        }
        self.authorize() == Some(expected)
    }
    fn is_fan(&self) -> bool {
        true
    }
    fn cleanup(&self) -> bool {
        self.release.load(Ordering::Acquire)
    }
    fn authorize(&self) -> Option<ReadAdmission> {
        verify_retained_failure(&self.fatal).ok()?;
        self.fresh
            .verify_health()
            .inspect_err(|e| remember_failure(&self.fatal, e))
            .ok()?;
        let proof = if self.cleanup() {
            if !self.owned.load(Ordering::Acquire) {
                return None;
            }
            self.fresh.authorize_owned_stop()
        } else {
            self.fresh.authorize()
        };
        if proof != Some(self.proof) {
            return None;
        }
        if let Some((source, settings)) = &*self.selected.lock().ok()? {
            if self
                .storage
                .desired_fan(*source, self.application.as_deref())
                .ok()?
                .as_ref()
                != Some(settings)
            {
                return None;
            }
        }
        proof
    }
    fn resolve(&self, _: i32, _: ObservedPerformance) -> io::Result<Option<Preset>> {
        Ok(None)
    }
    fn resolve_fan(
        &self,
        power: i32,
        baseline: ObservedPerformance,
    ) -> io::Result<Option<FanIntent>> {
        if self.authorize().is_none() {
            return Ok(None);
        }
        self.storage
            .save_fan(self.value.clone(), self.settings.clone(), power)?;
        self.accepted.store(true, Ordering::Release);
        let Some(settings) = self
            .storage
            .desired_fan(power, self.application.as_deref())?
        else {
            return Ok(None);
        };
        let intent = FanIntent::checked(
            settings["mode"].as_u64().unwrap_or(255) as u8,
            settings["fixedSpeedRpm"].as_u64().unwrap_or(0) as u16,
        );
        *self.selected.lock().map_err(|_| io::ErrorKind::Other)? = Some((power, settings));
        Ok(intent.filter(|i| Plan::select(baseline, *i, power).is_some()))
    }
    fn fan_record(&self) -> Option<FanRecord> {
        self.record.lock().ok()?.clone().or_else(|| {
            Some(FanRecord {
                contract: CONTRACT.into(),
                requested: self.requested.clone(),
                intent: FanIntent::checked(0, 3700)?,
                selector: 0,
                entry: false,
                source: 0,
                preflight: [[0; 2]; 2],
                ids: Vec::new(),
                cleanup: Vec::new(),
            })
        })
    }
    fn prepare_fan(&self, record: FanRecord) -> io::Result<()> {
        if self.cleanup() || self.prepared.load(Ordering::Acquire) {
            return Err(io::ErrorKind::PermissionDenied.into());
        }
        *self.record.lock().map_err(|_| io::ErrorKind::Other)? = Some(record);
        Ok(())
    }
    fn record_fan(&self, progress: u8) -> io::Result<()> {
        self.authorize().ok_or(io::ErrorKind::PermissionDenied)?;
        self.write(progress)?;
        if progress == 0 {
            self.prepared.store(true, Ordering::Release);
        }
        if progress == 30 {
            self.owned.store(true, Ordering::Release);
        }
        if progress == 50 {
            self.owned.store(false, Ordering::Release);
        }
        Ok(())
    }
    fn record(&self, _: Progress) -> io::Result<()> {
        Err(io::ErrorKind::PermissionDenied.into())
    }
    fn failed(&self, e: &io::Error) {
        remember_failure(&self.fatal, e)
    }
    fn retired(&self) -> io::Result<Settlement> {
        verify_retained_failure(&self.fatal)?;
        self.fresh.verify_health()?;
        if self.prepared.load(Ordering::Acquire) && self.storage.progress() == Some(0) {
            if let Err(e) = self.write(6) {
                self.storage.quarantine_settlement()?;
                return Ok(Settlement::Unresolved(e));
            }
        }
        Ok(Settlement::Complete)
    }
}
impl Runtime {
    pub(in super::super) async fn apply_fan(
        &self,
        mailbox: MailboxSource,
        caller: Caller,
        sid: String,
        request: String,
        value: Option<FanOverride>,
        settings: Option<Value>,
        deadline: Instant,
        status_snapshot: Snapshot,
    ) -> io::Result<(Outcome, Option<Snapshot>)> {
        let _serial = self.gate.lock().await;
        verify_retained_failure(&self.fatal)?;
        if caller.logon != Logon::Interactive
            || caller.control_session != Some(caller.session)
            || caller.pid.is_none()
        {
            return Ok((
                Outcome::blocked("The active interactive Windows user is required."),
                None,
            ));
        }
        let MailboxSource::Reader(reader) = mailbox else {
            return Ok((
                Outcome::blocked("Authenticated fan custody is unavailable."),
                None,
            ));
        };
        reader.custody().verify_integrity()?;
        let (storage, fresh) = match (self.factory)(&sid, caller.session, reader.custody()) {
            Ok(v) => v,
            Err(e) if is_fatal_admission(&e) => return Err(e),
            Err(e) => {
                return Ok((
                    Outcome::blocked(format!("Fan configuration admission failed: {e}")),
                    None,
                ));
            }
        };
        if storage.pending() {
            return Ok((Outcome::blocked(storage.pending_reason()?), None));
        }
        if storage.outstanding_fan()
            && self.fan_owner.borrow().as_ref().is_none_or(|p| {
                !p.owned.load(Ordering::Acquire)
                    || p.session != caller.session
                    || p.authorize().is_none()
            })
        {
            return Ok((
                Outcome::blocked(
                    "Fan ownership belongs to an earlier or invalid continuity; read-only reconciliation is required.",
                ),
                None,
            ));
        }
        fresh.verify_health()?;
        let Some(proof) = fresh.authorize() else {
            return Ok((
                Outcome::blocked("Current fan admission is unavailable."),
                None,
            ));
        };
        let application = self
            .application
            .borrow()
            .as_ref()
            .filter(|a| a.sid == sid && a.session == caller.session && Instant::now() < a.expires)
            .and_then(|a| a.path.clone());
        let permit = Arc::new(FanPermit {
            storage,
            fresh,
            request,
            owner: format!("{:x}", Sha256::digest(proof.generation)),
            caller_pid: caller.pid.unwrap(),
            session: caller.session,
            requested: json!({"value":value,"settings":settings}),
            value,
            settings,
            application,
            proof,
            selected: Mutex::new(None),
            record: Mutex::new(None),
            accepted: AtomicBool::new(false),
            owned: AtomicBool::new(false),
            release: AtomicBool::new(false),
            prepared: AtomicBool::new(false),
            fatal: self.fatal.clone(),
        });
        let store = permit.storage.clone();
        let policy_fresh = permit.fresh.clone();
        let policy_sid = sid.clone();
        let policy_runtime = self.clone();
        reader.retain_policy(Rc::new(move |base| {
            policy_fresh.verify_health()?;
            if policy_fresh.authorize().is_none() {
                return Ok(base.redact_private());
            }
            let power = base.thermal_power_source();
            let app = policy_runtime
                .application
                .borrow()
                .as_ref()
                .filter(|a| {
                    a.sid == policy_sid && a.session == caller.session && Instant::now() < a.expires
                })
                .and_then(|a| a.path.clone());
            let perf = store.desired(power, app.as_deref())?;
            let value = store.current_override(power)?;
            let fan_value = store.current_fan_override(power)?;
            Ok(base
                .redact_private()
                .thermal_profiles(store.profiles()?)?
                .thermal_intent(
                    value.as_ref(),
                    perf.map(|v| v.0),
                    "Saved thermal intent; firmware mode is reported independently.",
                    None,
                )
                .thermal_provenance(perf.map(|v| v.1))
                .fan_intent(
                    fan_value.as_ref(),
                    store.desired_fan(power, app.as_deref())?,
                    power,
                    "Saved fan intent; target RPM remains unreadable.",
                )
                .bind_private_user(&policy_sid))
        }));
        // Retain before awaiting: cancellation cannot discard an owned Stop obligation.
        *self.fan_pending.borrow_mut() = Some(permit.clone());
        let result = reader.mutate(permit.clone(), deadline).await;
        verify_retained_failure(&self.fatal)?;
        permit.fresh.verify_health()?;
        if permit.owned.load(Ordering::Acquire) {
            *self.fan_owner.borrow_mut() = Some(permit.clone());
        } else if !permit.storage.outstanding_fan() && !permit.storage.pending() {
            self.fan_owner.borrow_mut().take();
        }
        let status = u8::from(
            result.as_ref().is_ok_and(Option::is_some)
                && permit.authorize() == Some(proof)
                && (permit.owned.load(Ordering::Acquire)
                    || !permit.prepared.load(Ordering::Acquire)
                    || permit.storage.progress() == Some(50)),
        );
        let message = if status == 1 {
            "Fan setters completed and independent mode was confirmed. Target RPM and rotation are not readable on this backend."
        } else if permit.storage.pending() {
            "Fan submission or owned release remains unresolved; further writes are fenced."
        } else if permit.accepted.load(Ordering::Acquire) {
            "Fan intent stored. This source or observed mode needs a native recipe that is not yet implemented, or admission/transaction capacity did not complete. No applied target is claimed."
        } else {
            "Fan intent was not accepted; fresh admission or persistence did not complete."
        };
        if result.as_ref().is_err_and(is_fatal_admission) {
            return result.map(|_| unreachable!());
        }
        let message = if status == 1 && !permit.prepared.load(Ordering::Acquire) {
            "Automatic mode independently observed; no fan setter was submitted and no ownership was established."
        } else {
            message
        };
        let outcome = Outcome {
            status,
            message: message.into(),
        };
        let snapshot = if permit.accepted.load(Ordering::Acquire) {
            let (power, settings) = permit
                .selected
                .lock()
                .map_err(|_| io::ErrorKind::Other)?
                .clone()
                .unwrap_or((3, Value::Null));
            let mut base = status_snapshot
                .redact_private()
                .without_thermal_observation();
            let observed = result
                .as_ref()
                .ok()
                .copied()
                .flatten()
                .filter(|_| permit.authorize() == Some(proof));
            if let Some(observed) = observed {
                let fresh = permit.fresh.clone();
                base = base.with_mailbox_read(
                    observed,
                    ReadConfirmation::new(deadline, move || fresh.authorize() == Some(proof)),
                );
            }
            let perf = permit
                .storage
                .desired(power, permit.application.as_deref())?;
            let perf_override = permit.storage.current_override(power)?;
            base = base
                .thermal_profiles(permit.storage.profiles()?)?
                .thermal_intent(
                    perf_override.as_ref(),
                    perf.map(|v| v.0),
                    &outcome.message,
                    Some(power),
                )
                .thermal_provenance(perf.map(|v| v.1));
            Some(
                base.fan_intent(
                    permit.value.as_ref(),
                    if settings.is_null() {
                        None
                    } else {
                        Some(settings)
                    },
                    power,
                    &outcome.message,
                )
                .bind_private_user(&sid),
            )
        } else {
            None
        };
        Ok((outcome, snapshot))
    }
    pub(in super::super) fn refuse_stop_fans(&self) -> io::Result<()> {
        if self
            .fan_pending
            .borrow()
            .as_ref()
            .or(self.fan_owner.borrow().as_ref())
            .is_some_and(|p| {
                p.owned.load(Ordering::Acquire)
                    || p.storage.pending()
                    || p.storage.outstanding_fan()
            })
        {
            Err(io::Error::other(
                "Unexpected origin loss cannot authorize owned fan cleanup.",
            ))
        } else {
            Ok(())
        }
    }
    pub(in super::super) async fn stop_fans(&self, mailbox: &MailboxSource) -> io::Result<()> {
        let _serial = self.gate.lock().await;
        let MailboxSource::Reader(reader) = mailbox else {
            return Ok(());
        };
        // Always join original active work before checking positive ownership.
        reader.drain_active().await?;
        let pending = self.fan_pending.borrow().clone();
        let owner = self.fan_owner.borrow().clone();
        let latest = pending.clone().or(owner.clone());
        if latest.as_ref().is_some_and(|p| p.storage.pending()) {
            return Err(io::Error::other(
                "Fan submission remains unresolved at Stop.",
            ));
        }
        if latest.as_ref().is_none_or(|p| !p.storage.outstanding_fan()) {
            return Ok(());
        }
        let Some(permit) = pending
            .filter(|p| p.owned.load(Ordering::Acquire))
            .or(owner.filter(|p| p.owned.load(Ordering::Acquire)))
        else {
            return Err(io::Error::other(
                "Original fan cleanup ownership is unavailable.",
            ));
        };
        if permit.release.swap(true, Ordering::AcqRel) {
            return Err(io::Error::other(
                "Owned Stop cleanup was already attempted; reserved IDs will not be reused.",
            ));
        }
        let result = reader
            .mutate_cleanup(
                permit.clone(),
                Instant::now() + std::time::Duration::from_secs(3),
            )
            .await;
        if permit.storage.progress() != Some(50) || result?.is_none() {
            return Err(io::Error::other(
                "Automatic fan handback was not independently confirmed; evidence is retained.",
            ));
        }
        Ok(())
    }
}

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
    sid: String,
    authentication: Option<(u32, i32)>,
    custody: Arc<dyn ReadCustody>,
    prior: Mutex<Option<Arc<FanPermit>>>,
    requested: Value,
    value: Option<FanOverride>,
    settings: Option<Value>,
    application: Option<String>,
    proof: ReadAdmission,
    selected: Mutex<Option<(i32, Value)>>,
    // The saved preset this return to Automatic applies (see `handback_preset`).
    performance: Mutex<Option<Preset>>,
    // A saved preset was left unapplied because its GPU clock effect is unsupported.
    performance_gpu: AtomicBool,
    record: Mutex<Option<FanRecord>>,
    accepted: AtomicBool,
    owned: AtomicBool,
    release: AtomicBool,
    prepared: AtomicBool,
    joined: AtomicBool,
    registered: AtomicBool,
    fatal: Arc<Mutex<Option<&'static str>>>,
}
// A canceled/refused future which never registered a mutation worker cannot
// later submit one. Do not retain false cross-scope authority for that case.
struct FanAwaitGuard(Arc<FanPermit>);
impl Drop for FanAwaitGuard {
    fn drop(&mut self) {
        if !self.0.registered.load(Ordering::Acquire) {
            self.0.joined.store(true, Ordering::Release);
        }
    }
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
            tfc: None,
            battery: None,
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
    /// The saved preset a return to Automatic applies, resolved as a
    /// performance request resolves its destination. A configured GPU clock
    /// effect has no native pair, so then the observed selector is kept.
    fn handback_preset(&self, power: i32) -> io::Result<Option<Preset>> {
        let application = self.application.as_deref();
        if self.storage.gpu_incomplete(power, application)? {
            return Ok(None);
        }
        Ok(self.storage.desired(power, application)?.map(|(p, _)| p))
    }
}
impl Permit for FanPermit {
    fn mutation_registered(&self) {
        self.registered.store(true, Ordering::Release);
    }
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
        // Saved policy is request admission, never the release provenance of a
        // positively established owner. Cleanup preserves newer saved intent.
        if self.cleanup() {
            self.storage.verify_fan_configuration().ok()?;
        }
        if !self.cleanup() {
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
                // The preset carried by a return to Automatic is saved policy too:
                // every setter needs it to remain the current destination.
                if let Some(preset) = *self.performance.lock().ok()? {
                    if self.handback_preset(*source).ok()? != Some(preset) {
                        return None;
                    }
                }
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
    ) -> io::Result<Option<(FanIntent, Option<Preset>)>> {
        if self.authorize().is_none() {
            return Ok(None);
        }
        // Managed applies the saved preset only when fans leave saved manual
        // intent (`previousManual` in ApplyFanControlTransitionCoreOutcomeAsync).
        let leaving_manual = self
            .storage
            .desired_manual_fans(power, self.application.as_deref())?;
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
        let returning = leaving_manual && intent.is_some_and(|i| !i.manual());
        let performance = if returning {
            self.handback_preset(power)?
        } else {
            None
        };
        if returning && performance.is_none() {
            self.performance_gpu.store(
                self.storage
                    .gpu_incomplete(power, self.application.as_deref())?,
                Ordering::Release,
            );
        }
        *self.performance.lock().map_err(|_| io::ErrorKind::Other)? = performance;
        *self.selected.lock().map_err(|_| io::ErrorKind::Other)? = Some((power, settings));
        Ok(intent
            .filter(|i| Plan::select_toward(baseline, *i, power, performance).is_some())
            .map(|i| (i, performance)))
    }
    fn confirm_fan_noop(&self, source: i32, baseline: ObservedPerformance) -> io::Result<()> {
        self.authorize().ok_or(io::ErrorKind::PermissionDenied)?;
        let mut prior = self.prior.lock().map_err(|_| io::ErrorKind::Other)?;
        if let Some(owner) = prior.as_ref() {
            let record = owner
                .record
                .lock()
                .map_err(|_| io::ErrorKind::Other)?
                .clone()
                .ok_or(io::ErrorKind::PermissionDenied)?;
            let auto = Plan::select(baseline, FanIntent::checked(0, 3700).unwrap(), source)
                .ok_or(io::ErrorKind::PermissionDenied)?;
            if !owner.owned.load(Ordering::Acquire)
                || owner.release.load(Ordering::Acquire)
                || !matches!(
                    record.contract.as_str(),
                    CONTRACT | openblade_domain::fan_history::PREVIOUS_CONTRACT
                )
                || record.source != source
                || record.selector != auto.selector
                || !auto.unchanged
                || !auto.confirms(baseline)
                || self.storage.pending()
            {
                return Err(io::ErrorKind::PermissionDenied.into());
            }
            // Fresh explicit request authority, exact retained original owner bytes.
            // 51 is v3's independently observed Automatic proof: no setter ACKs.
            owner.write(51)?;
            owner.owned.store(false, Ordering::Release);
        }
        prior.take();
        Ok(())
    }
    fn fan_record(&self) -> Option<FanRecord> {
        self.record.lock().ok()?.clone().or_else(|| {
            Some(FanRecord {
                contract: if cfg!(feature = "service-battery-policy-v1") {
                    CONTRACT
                } else {
                    openblade_domain::fan_history::PREVIOUS_CONTRACT
                }
                .into(),
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
            self.prior.lock().map_err(|_| io::ErrorKind::Other)?.take();
            self.owned.store(true, Ordering::Release);
        }
        if progress == 50 {
            self.prior.lock().map_err(|_| io::ErrorKind::Other)?.take();
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
        self.joined.store(true, Ordering::Release);
        Ok(Settlement::Complete)
    }
}
impl Runtime {
    // Shared by explicit fan/performance requests and retained policy refresh.
    // Do not evict the sole original per-user Storage cache while its positive
    // in-process fan owner still owes a release. No stale proof is renewed.
    pub(super) fn capture(
        &self,
        sid: &str,
        session: u32,
        custody: Arc<dyn ReadCustody>,
    ) -> io::Result<(Arc<Storage>, Arc<dyn Fresh>)> {
        self.capture_scoped(sid, session, custody, false)
    }
    // Only explicit typed TFC false can select fresh same-SID recovery. This
    // never renews the old automatic proof or bypasses the shared fan fence.
    pub(super) fn capture_temporary_cancel(
        &self,
        sid: &str,
        session: u32,
        custody: Arc<dyn ReadCustody>,
    ) -> io::Result<(Arc<Storage>, Arc<dyn Fresh>)> {
        self.capture_scoped(sid, session, custody, true)
    }
    fn capture_scoped(
        &self,
        sid: &str,
        session: u32,
        custody: Arc<dyn ReadCustody>,
        explicit_cancel: bool,
    ) -> io::Result<(Arc<Storage>, Arc<dyn Fresh>)> {
        self.reconcile_temporary_owner()?;
        let pending = self.fan_pending.borrow().clone();
        let owner = self.fan_owner.borrow().clone();
        if pending.iter().chain(owner.iter()).any(|p| {
            (!p.joined.load(Ordering::Acquire)
                || p.storage.pending()
                || p.storage.outstanding_fan())
                && (p.sid != sid || p.session != session)
        }) {
            return Err(io::ErrorKind::PermissionDenied.into());
        }
        let recovery = explicit_cancel
            .then(|| self.temporary_cancel_recovery(sid, &custody))
            .flatten();
        if self.temporary_cross_user(sid, session) && recovery.is_none() {
            return Err(io::ErrorKind::PermissionDenied.into());
        }
        let captured = (self.factory)(sid, session, custody)?;
        if recovery
            .as_ref()
            .is_some_and(|storage| !Arc::ptr_eq(storage, &captured.0))
        {
            return Err(io::ErrorKind::PermissionDenied.into());
        }
        Ok(captured)
    }

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
        reader.drain_active().await?;
        if let Some(p) = self
            .fan_pending
            .borrow()
            .as_ref()
            .filter(|p| p.owned.load(Ordering::Acquire))
        {
            *self.fan_owner.borrow_mut() = Some(p.clone());
        }
        reader.custody().verify_integrity()?;
        let (storage, fresh) = match self.capture(&sid, caller.session, reader.custody()) {
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
        fresh.verify_health()?;
        let Some(proof) = fresh.authorize() else {
            return Ok((
                Outcome::blocked("Current fan admission is unavailable."),
                None,
            ));
        };
        // A canceled explicit Auto may already have durably closed ownership on
        // its worker. After joining it, discard only that positively settled
        // obligation; never let a stale pointer obstruct a later true no-op.
        if !storage.outstanding_fan() {
            self.fan_owner.borrow_mut().take();
        }
        let prior = self.fan_owner.borrow().clone();
        if storage.outstanding_fan()
            && prior.as_ref().is_none_or(|p| {
                !p.owned.load(Ordering::Acquire)
                    || p.release.load(Ordering::Acquire)
                    || p.sid != sid
                    || p.session != caller.session
                    || p.proof.generation != proof.generation
                    || !Arc::ptr_eq(&p.custody, &reader.custody())
                    || p.authentication != fresh.authentication()
            })
        {
            return Ok((
                Outcome::blocked(
                    "Fan ownership belongs to an earlier or invalid continuity; read-only reconciliation is required.",
                ),
                None,
            ));
        }
        let application = self
            .application
            .borrow()
            .as_ref()
            .filter(|a| a.sid == sid && a.session == caller.session && Instant::now() < a.expires)
            .and_then(|a| a.path.clone());
        if let Some(p) = &prior {
            p.fresh.verify_health()?;
        }
        let permit = Arc::new(FanPermit {
            authentication: fresh.authentication(),
            storage,
            fresh,
            request,
            owner: format!("{:x}", Sha256::digest(proof.generation)),
            caller_pid: caller.pid.unwrap(),
            session: caller.session,
            sid: sid.clone(),
            custody: reader.custody(),
            prior: Mutex::new(prior),
            requested: json!({"value":value,"settings":settings}),
            value,
            settings,
            application,
            proof,
            selected: Mutex::new(None),
            performance: Mutex::new(None),
            performance_gpu: AtomicBool::new(false),
            record: Mutex::new(None),
            accepted: AtomicBool::new(false),
            owned: AtomicBool::new(false),
            release: AtomicBool::new(false),
            prepared: AtomicBool::new(false),
            joined: AtomicBool::new(false),
            registered: AtomicBool::new(false),
            fatal: self.fatal.clone(),
        });
        let store = permit.storage.clone();
        let policy_fresh = permit.fresh.clone();
        let policy_sid = sid.clone();
        let policy_runtime = self.clone();
        reader.retain_policy(Rc::new(move |base| {
            policy_fresh.verify_health()?;
            if policy_fresh.authorize().is_none() {
                return Ok(base.without_private_policy());
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
                .without_private_policy()
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
        let awaiting = FanAwaitGuard(permit.clone());
        let result = reader.mutate(permit.clone(), deadline).await;
        drop(awaiting);
        verify_retained_failure(&self.fatal)?;
        permit.fresh.verify_health()?;
        if permit.owned.load(Ordering::Acquire) {
            *self.fan_owner.borrow_mut() = Some(permit.clone());
        } else if !permit.storage.outstanding_fan() && !permit.storage.pending() {
            self.fan_owner.borrow_mut().take();
        }
        let applied = result.as_ref().is_ok_and(Option::is_some)
            && permit.authorize() == Some(proof)
            && (permit.owned.load(Ordering::Acquire)
                || !permit.prepared.load(Ordering::Acquire)
                || permit.storage.progress() == Some(50));
        // Saved but unapplied fan intent is Blocked, as managed reports it: managed
        // has no fan deferral that returns Stored.
        let status =
            if !permit.storage.pending() && permit.accepted.load(Ordering::Acquire) && applied {
                1
            } else {
                2
            };
        let message = if status == 1 {
            "Fan setters completed and independent mode was confirmed. Target RPM and rotation are not readable on this backend."
        } else if permit.storage.pending() {
            "Fan submission or owned release remains unresolved; further writes are fenced."
        } else if permit.accepted.load(Ordering::Acquire) {
            if permit.value.is_none() {
                "Fan override was cleared. Hardware result: This source or observed mode needs a native recipe that is not yet implemented, or admission/transaction capacity did not complete. No applied target is claimed."
            } else {
                "Fan override intent was stored. Hardware result: This source or observed mode needs a native recipe that is not yet implemented, or admission/transaction capacity did not complete. No applied target is claimed."
            }
        } else {
            "Fan intent was not accepted; fresh admission or persistence did not complete."
        };
        if result.as_ref().is_err_and(is_fatal_admission) {
            return result.map(|_| unreachable!());
        }
        // A return to Automatic that carried the saved preset reports it as managed
        // does; Debug names match managed's PerformancePreset names.
        let performance = *permit
            .performance
            .lock()
            .map_err(|_| io::ErrorKind::Other)?;
        let mut message = match (status, permit.prepared.load(Ordering::Acquire), performance) {
            (1, false, Some(preset)) => format!(
                "Fans and {preset:?} performance already matched the requested state; no fan setter was submitted and no ownership was established."
            ),
            (1, false, None) => "Automatic mode independently observed; no fan setter was submitted and no ownership was established.".into(),
            (1, true, Some(preset)) => format!(
                "Fans returned to Automatic with {preset:?} performance, confirmed by independent paired readback."
            ),
            _ => message.into(),
        };
        if status == 1 && permit.performance_gpu.load(Ordering::Acquire) {
            message.push_str(
                " The saved performance intent was not applied: GPU clock handling is incomplete.",
            );
        }
        let outcome = Outcome { status, message };
        let snapshot = if permit.accepted.load(Ordering::Acquire) {
            let (power, settings) = permit
                .selected
                .lock()
                .map_err(|_| io::ErrorKind::Other)?
                .clone()
                .unwrap_or((3, Value::Null));
            let mut base = status_snapshot
                .without_private_policy()
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

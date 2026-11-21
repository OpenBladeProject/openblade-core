//! Installed explicit fan runtime; one original custody and one global journal.
use super::*;
use crate::service_status::FanOverride;
use crate::thermal_history::Entry;
use openblade_domain::{
    fan_history::{CONTRACT, FanRecord},
    fan_recipe::{FanIntent, automatic_handback},
};
use serde_json::{Value, json};
// One admitted fan request and the status it was read against.
pub(in super::super) struct FanRequest {
    pub(in super::super) caller: Caller,
    pub(in super::super) sid: String,
    pub(in super::super) request: String,
    pub(in super::super) value: Option<FanOverride>,
    pub(in super::super) settings: Option<Value>,
    pub(in super::super) deadline: Instant,
    pub(in super::super) status: Snapshot,
}
/// A fan request refused by `Refusal::Deadline` after its intent was saved.
const FAN_DEADLINE_STORED: &str = "Fan override intent was stored. Hardware result: The remaining request time cannot cover this recipe at the measured device pace; no fan setter was submitted.";
// Managed composes `Fans could not return to Automatic with the saved Custom
// performance intent.` with ApplyScopedAsync's `Unsupported("Performance")`.
const FAN_CUSTOM_UNSUPPORTED_STORED: &str = "Fan override intent was stored. Hardware result: Fans could not return to Automatic with the saved Custom performance intent. Performance value is not supported by the verified capability table.";
const FAN_CUSTOM_UNSUPPORTED_CLEARED: &str = "Fan override was cleared. Hardware result: Fans could not return to Automatic with the saved Custom performance intent. Performance value is not supported by the verified capability table.";
const FAN_DEADLINE_CLEARED: &str = "Fan override was cleared. Hardware result: The remaining request time cannot cover this recipe at the measured device pace; no fan setter was submitted.";
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
    // The saved destination this return to Automatic applies (see `saved_preset`).
    performance: Mutex<Option<Destination>>,
    // A saved destination was left unapplied because its GPU clock effect is unsupported.
    performance_gpu: AtomicBool,
    // A return to Automatic refused because the saved Custom is unsupported.
    custom_unsupported: AtomicBool,
    // The selector an earlier owner recorded, when this request's no-op closed it.
    released: Mutex<Option<u8>>,
    // A clear on unclassified external power: no profile applies, so only an
    // owned manual fan change is handed back to firmware Automatic.
    unclassified_clear: AtomicBool,
    // Refused before storage on unclassified power, or on a battery reading
    // that Windows does not confirm (`UNCLASSIFIED_BLOCKED`).
    unclassified_blocked: AtomicBool,
    // Rejected before storage by managed's fan power policy or thermal
    // combination check (`fan_override_rejected`); replied as Failed.
    rejected: Mutex<Option<String>>,
    // The saved-policy inputs (`Storage::policy_inputs`) the last passing
    // authorization resolved.
    verified: Mutex<Option<[u8; 32]>>,
    // Refused before the first row: the remaining request time cannot cover
    // the recipe at the measured device pace (`Refusal::Deadline`).
    deadline: AtomicBool,
    // This request returns saved manual fans to Automatic (`previousManual`).
    returning: AtomicBool,
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
    /// The saved destination a return to Automatic applies, resolved as a
    /// performance request resolves it. Managed writes every destination there
    /// (`ApplyDirectAutomaticFanTransitionOutcomeAsync`). A configured GPU
    /// clock effect has no native writer, so then the observed selector is kept.
    fn saved_preset(&self, power: i32) -> io::Result<Option<Destination>> {
        let application = self.application.as_deref();
        // A temporary override kept for another class has expired for both
        // reads (Storage expires it in each), as for a performance request.
        let desired = self.storage.desired(power, application)?;
        if self.storage.gpu_incomplete(power, application)? {
            return Ok(None);
        }
        Ok(desired.map(|(p, _)| p))
    }
    fn observe_power(&self, power: i32) -> io::Result<()> {
        let generation = self
            .fresh
            .power_generation()
            .ok_or(io::ErrorKind::PermissionDenied)?;
        self.storage.observe_power(power, generation)
    }
    /// Managed's checks in `SetFanOverrideCoreAsync`, run on classified
    /// power before the override is stored. Unclassified power keeps its own
    /// admission (`CanAdmitUnclassifiedExternalControl`).
    fn override_rejected(&self, power: i32) -> io::Result<Option<String>> {
        if !class_agrees_with_windows(power, self.fresh.as_ref()) {
            return Ok(None);
        }
        let Some((profile_performance, profile_fans)) = self.storage.profile_thermal(power)? else {
            return Ok(None);
        };
        let performance = self
            .storage
            .current_performance_override(power)?
            .unwrap_or(profile_performance);
        Ok(fan_override_rejected(
            self.value.as_ref().map(|v| i32::from(v.value)),
            performance,
            profile_fans,
            power,
        ))
    }
}
impl Permit for FanPermit {
    fn refused(&self, cause: Refusal) {
        match cause {
            Refusal::UnclassifiedBlocked => {
                self.unclassified_blocked.store(true, Ordering::Release);
            }
            Refusal::Deadline => self.deadline.store(true, Ordering::Release),
            _ => {}
        }
    }
    fn destination_holds(&self, power: i32) -> bool {
        // Owned Stop hands back to Automatic under the owner's selector, with
        // the same commands on every external class.
        if self.cleanup() {
            return true;
        }
        (|| -> io::Result<bool> {
            let Some((_, settings)) = self
                .selected
                .lock()
                .map_err(|_| io::ErrorKind::Other)?
                .clone()
            else {
                return Ok(false);
            };
            // A synthesized unclassified-clear handback has no saved settings
            // to compare: like owned Stop it hands back to Automatic on every
            // external class, as managed continues it. Only a carried preset
            // must still hold; a selector-2 handback without one is pinned by
            // the power fence instead.
            self.observe_power(power)?;
            if !settings.is_null()
                && self
                    .storage
                    .desired_fan(power, self.application.as_deref())?
                    != Some(settings)
            {
                return Ok(false);
            }
            if !self.returning.load(Ordering::Acquire) {
                return Ok(true);
            }
            let performance = *self.performance.lock().map_err(|_| io::ErrorKind::Other)?;
            Ok(self.saved_preset(power)? == performance)
        })()
        .inspect_err(|e| remember_failure(&self.fatal, e))
        .unwrap_or(false)
    }
    fn mutation_registered(&self) {
        self.registered.store(true, Ordering::Release);
    }
    fn windows_on_battery(&self) -> Option<bool> {
        self.fresh.on_battery()
    }
    fn result_admitted(&self, expected: ReadAdmission) -> bool {
        if self.cleanup() && matches!(self.storage.progress(), Some(50 | 51)) {
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
        if !self.cleanup()
            && let Some((source, settings)) = &*self.selected.lock().ok()?
        {
            // Saved policy is resolved again only when one of its inputs
            // changed since the last check that passed: every checkpoint still
            // reads them, but unchanged bytes are not parsed again. The inputs
            // are read first, so a change during the check is seen next time.
            let inputs = self.storage.policy_inputs().ok()?;
            if *self.verified.lock().ok()? != Some(inputs) {
                // A synthesized handback (null settings) needs saved fan intent
                // to stay cleared; any other selection needs it unchanged.
                if self
                    .storage
                    .desired_fan(*source, self.application.as_deref())
                    .ok()?
                    .as_ref()
                    != Some(settings).filter(|s| !s.is_null())
                {
                    return None;
                }
                // The preset carried by a return to Automatic is saved policy
                // too: every setter needs it to remain the current destination.
                if let Some(destination) = *self.performance.lock().ok()?
                    && self.saved_preset(*source).ok()? != Some(destination)
                {
                    return None;
                }
                *self.verified.lock().ok()? = Some(inputs);
            }
        }
        proof
    }
    fn resolve(&self, _: i32, _: ObservedPerformance) -> io::Result<Option<Destination>> {
        Ok(None)
    }
    fn resolve_fan(
        &self,
        power: i32,
        baseline: ObservedPerformance,
    ) -> io::Result<Option<(FanIntent, Option<Destination>)>> {
        if self.authorize().is_none() {
            return Ok(None);
        }
        // Managed `CanAdmitUnclassifiedExternalControl` needs confirmed
        // HyperBoost to be inactive; nothing is stored or written.
        if power == UNCLASSIFIED_EXTERNAL && baseline == ObservedPerformance::HyperBoostObserved {
            self.unclassified_blocked.store(true, Ordering::Release);
            return Ok(None);
        }
        if battery_reading_contradicts_windows(power, self.fresh.as_ref()) {
            self.unclassified_blocked.store(true, Ordering::Release);
            return Ok(None);
        }
        self.observe_power(power)?;
        if let Some(message) = self.override_rejected(power)? {
            *self.rejected.lock().map_err(|_| io::ErrorKind::Other)? = Some(message);
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
        if power == UNCLASSIFIED_EXTERNAL && self.value.is_none() {
            // Managed `ExecuteScopedMutationAsync`: with no profile to apply, a
            // clear hands back only an owned manual fan change, through
            // `RestoreOwnedAutomaticFansCoreAsync` (Automatic, no saved preset),
            // and otherwise writes nothing.
            self.unclassified_clear.store(true, Ordering::Release);
            if !self.storage.outstanding_fan() {
                return Ok(None);
            }
            *self.selected.lock().map_err(|_| io::ErrorKind::Other)? = Some((power, Value::Null));
            return Ok(FanIntent::checked(0, 3700).map(|i| (i, None)));
        }
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
        self.returning.store(returning, Ordering::Release);
        let performance = if returning {
            self.saved_preset(power)?
        } else {
            None
        };
        // Managed applies the saved Custom through
        // `ApplyDirectAutomaticFanTransitionOutcomeAsync`; its capability check
        // refuses unsupported settings before any write, so fans stay manual and
        // the reply says why. The fan intent above is still saved.
        if returning
            && performance.is_none()
            && self
                .storage
                .custom_unsupported(power, self.application.as_deref())?
        {
            self.custom_unsupported.store(true, Ordering::Release);
            *self.selected.lock().map_err(|_| io::ErrorKind::Other)? = Some((power, settings));
            return Ok(None);
        }
        if returning && performance.is_none() {
            self.performance_gpu.store(
                self.storage
                    .gpu_incomplete(power, self.application.as_deref())?,
                Ordering::Release,
            );
        }
        *self.performance.lock().map_err(|_| io::ErrorKind::Other)? = performance;
        *self.selected.lock().map_err(|_| io::ErrorKind::Other)? = Some((power, settings));
        // The Reader selects the plan; an Automatic intent without one may still
        // be firmware's own state (`automatic_handback`), which writes nothing.
        Ok(intent.map(|i| (i, performance)))
    }
    fn confirm_fan_noop(&self, _source: i32, baseline: ObservedPerformance) -> io::Result<()> {
        self.authorize().ok_or(io::ErrorKind::PermissionDenied)?;
        // An explicit reply publishes this observation, and native HyperBoost
        // with no broad capability snapshot is outside the managed decoder's
        // read parity (docs/windows-native-thermal-mutation.md). The explicit
        // path stays fenced, as before; owned Stop settles it and replies nothing.
        if baseline == ObservedPerformance::HyperBoostObserved {
            return Err(io::ErrorKind::PermissionDenied.into());
        }
        let mut prior = self.prior.lock().map_err(|_| io::ErrorKind::Other)?;
        if let Some(owner) = prior.as_ref() {
            let record = owner
                .record
                .lock()
                .map_err(|_| io::ErrorKind::Other)?
                .clone()
                .ok_or(io::ErrorKind::PermissionDenied)?;
            // Neither the owner's selector nor its power source is compared:
            // Automatic fans are firmware's own handback state under any stock
            // selector and source, and managed's
            // `ExactFanControlOwnership.ObserveTopology` clears ownership on any
            // confirmed Automatic. Writing the old selector back first would be
            // an intermediate write with no firmware reason. The 51 row repeats
            // the owner's exact record, including its recorded source.
            if !owner.owned.load(Ordering::Acquire)
                || owner.release.load(Ordering::Acquire)
                || !matches!(
                    record.contract.as_str(),
                    CONTRACT | openblade_domain::fan_history::PREVIOUS_CONTRACT
                )
                || !automatic_handback(baseline)
                || self.storage.pending()
            {
                return Err(io::ErrorKind::PermissionDenied.into());
            }
            // Fresh explicit request authority, exact retained original owner bytes.
            // 51 is v3's independently observed Automatic proof: no setter ACKs.
            owner.write(51)?;
            owner.owned.store(false, Ordering::Release);
            *self.released.lock().map_err(|_| io::ErrorKind::Other)? = Some(record.selector);
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
                custom: None,
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
        // 51 is owned Stop cleanup's no-setter Automatic proof (see `apply_fan`).
        if matches!(progress, 50 | 51) {
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
        if self.prepared.load(Ordering::Acquire)
            && self.storage.progress() == Some(0)
            && let Err(e) = self.write(6)
        {
            self.storage.quarantine_settlement()?;
            return Ok(Settlement::Unresolved(e));
        }
        self.joined.store(true, Ordering::Release);
        Ok(Settlement::Complete)
    }
}
/// Settle-only owned Stop. Every SERVICE_CONTROL_POWEREVENT and SESSIONCHANGE
/// advances the lifecycle epoch the owner's proof pins, so after an unplug, a
/// limited-adapter change, a lock or a resume the owner cannot authorize Stop.
/// This permit carries a fresh Stop-scoped capture of the same owner
/// (`Fresh::capture_settle`) and can append only the owner's exact 51 row,
/// after the Reader's fresh, agreeing Automatic readback. It refuses every
/// other journal stage, so it cannot submit a setter: each setter needs its
/// journaled stage first (31, then 40-43). Managed hands back nothing when the
/// confirmed fans are already Automatic (`RestoreOwnedAutomaticFansCoreAsync`
/// stores `OwnedManualFanHandbackBlockReason` with no write); native must read
/// that state first, so it needs this fresh read admission.
struct FanSettlePermit {
    owner: Arc<FanPermit>,
    fresh: Arc<dyn Fresh>,
    proof: ReadAdmission,
}
impl FanSettlePermit {
    fn capture(owner: &Arc<FanPermit>) -> Option<Arc<Self>> {
        verify_retained_failure(&owner.fatal).ok()?;
        if !owner.owned.load(Ordering::Acquire) || !owner.release.load(Ordering::Acquire) {
            return None;
        }
        let fresh = owner
            .fresh
            .capture_settle(&owner.sid, owner.session, owner.authentication)
            .inspect_err(|e| remember_failure(&owner.fatal, e))
            .ok()?;
        fresh
            .verify_health()
            .inspect_err(|e| remember_failure(&owner.fatal, e))
            .ok()?;
        let proof = fresh.authorize_owned_stop()?;
        // Same original custody generation; only the lifecycle epoch is
        // fresh. The logon is not compared here: `capture_settle` takes the
        // owner's SID, session and LUID and checks them against the live
        // session token on every `authorize_owned_stop`, so a permit-side
        // comparison of the copied LUID could never fail.
        (proof.generation == owner.proof.generation).then(|| {
            Arc::new(Self {
                owner: owner.clone(),
                fresh,
                proof,
            })
        })
    }
}
impl Permit for FanSettlePermit {
    fn authorize(&self) -> Option<ReadAdmission> {
        verify_retained_failure(&self.owner.fatal).ok()?;
        self.fresh
            .verify_health()
            .inspect_err(|e| remember_failure(&self.owner.fatal, e))
            .ok()?;
        if !self.owner.owned.load(Ordering::Acquire)
            || !self.owner.release.load(Ordering::Acquire)
            || self.fresh.authorize_owned_stop() != Some(self.proof)
        {
            return None;
        }
        self.owner.storage.verify_fan_configuration().ok()?;
        Some(self.proof)
    }
    fn result_admitted(&self, expected: ReadAdmission) -> bool {
        self.owner.storage.progress() == Some(51)
            && verify_retained_failure(&self.owner.fatal).is_ok()
            && self.fresh.verify_health().is_ok()
            && self.fresh.authorize_owned_stop() == Some(expected)
            && expected == self.proof
    }
    fn is_fan(&self) -> bool {
        true
    }
    fn cleanup(&self) -> bool {
        true
    }
    fn fan_record(&self) -> Option<FanRecord> {
        // Only the owner's exact retained record, never a default.
        self.owner.record.lock().ok()?.clone()
    }
    fn record_fan(&self, progress: u8) -> io::Result<()> {
        if progress != 51 {
            return Err(io::ErrorKind::PermissionDenied.into());
        }
        self.authorize().ok_or(io::ErrorKind::PermissionDenied)?;
        let contract = self
            .fan_record()
            .ok_or(io::ErrorKind::PermissionDenied)?
            .contract;
        if !matches!(
            contract.as_str(),
            CONTRACT | openblade_domain::fan_history::PREVIOUS_CONTRACT
        ) || self.owner.storage.pending()
        {
            return Err(io::ErrorKind::PermissionDenied.into());
        }
        self.owner.write(51)?;
        self.owner
            .prior
            .lock()
            .map_err(|_| io::ErrorKind::Other)?
            .take();
        self.owner.owned.store(false, Ordering::Release);
        Ok(())
    }
    fn resolve_fan(
        &self,
        _power: i32,
        _baseline: ObservedPerformance,
    ) -> io::Result<Option<(FanIntent, Option<Destination>)>> {
        Err(io::ErrorKind::PermissionDenied.into())
    }
    fn confirm_fan_noop(&self, _source: i32, _baseline: ObservedPerformance) -> io::Result<()> {
        Err(io::ErrorKind::PermissionDenied.into())
    }
    fn prepare_fan(&self, _record: FanRecord) -> io::Result<()> {
        Err(io::ErrorKind::PermissionDenied.into())
    }
    fn resolve(&self, _: i32, _: ObservedPerformance) -> io::Result<Option<Destination>> {
        Err(io::ErrorKind::PermissionDenied.into())
    }
    fn record(&self, _: Progress) -> io::Result<()> {
        Err(io::ErrorKind::PermissionDenied.into())
    }
    fn failed(&self, e: &io::Error) {
        remember_failure(&self.owner.fatal, e)
    }
    fn retired(&self) -> io::Result<Settlement> {
        verify_retained_failure(&self.owner.fatal)?;
        self.fresh.verify_health()?;
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
        fan: FanRequest,
    ) -> io::Result<(Outcome, Option<Snapshot>)> {
        let FanRequest {
            caller,
            sid,
            request,
            value,
            settings,
            deadline,
            status: status_snapshot,
        } = fan;
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
            released: Mutex::new(None),
            unclassified_clear: AtomicBool::new(false),
            unclassified_blocked: AtomicBool::new(false),
            rejected: Mutex::new(None),
            custom_unsupported: AtomicBool::new(false),
            deadline: AtomicBool::new(false),
            verified: Mutex::new(None),
            returning: AtomicBool::new(false),
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
            // Saved intent is read at an unknown class without fresh AC
            // presence (see `status_query_power`).
            let query = status_query_power(power, policy_fresh.as_ref());
            let app = policy_runtime
                .application
                .borrow()
                .as_ref()
                .filter(|a| {
                    a.sid == policy_sid && a.session == caller.session && Instant::now() < a.expires
                })
                .and_then(|a| a.path.clone());
            let perf = store.desired(query, app.as_deref())?;
            let value = store.current_override(query)?;
            let fan_value = store.current_fan_override(query)?;
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
                    store.desired_fan(query, app.as_deref())?,
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
        } else if permit.accepted.load(Ordering::Acquire)
            && permit.custom_unsupported.load(Ordering::Acquire)
        {
            if permit.value.is_none() {
                FAN_CUSTOM_UNSUPPORTED_CLEARED
            } else {
                FAN_CUSTOM_UNSUPPORTED_STORED
            }
        } else if permit.accepted.load(Ordering::Acquire) && permit.deadline.load(Ordering::Acquire)
        {
            if permit.value.is_none() {
                FAN_DEADLINE_CLEARED
            } else {
                FAN_DEADLINE_STORED
            }
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
        // A return to Automatic that carried the saved destination reports it as
        // managed does, with managed's PerformancePreset names.
        let performance = *permit
            .performance
            .lock()
            .map_err(|_| io::ErrorKind::Other)?;
        let observed = result.as_ref().ok().copied().flatten();
        // Without a native plan (battery, or Automatic fans under Silent, battery
        // selectors or Custom) a no-op can leave a carried destination unapplied:
        // claim it only when confirmed.
        let matched = performance.filter(|p| {
            observed.is_some_and(|o| openblade_domain::thermal_mutation::confirms(o, *p))
        });
        let mut message = match (status, permit.prepared.load(Ordering::Acquire), performance) {
            (1, false, Some(preset)) if matched.is_some() => format!(
                "Fans and {} performance already matched the requested state; no fan setter was submitted and no ownership was established.",
                preset.name()
            ),
            (1, false, _) => "Automatic mode independently observed; no fan setter was submitted and no ownership was established.".into(),
            (1, true, Some(preset)) => format!(
                "Fans returned to Automatic with {} performance, confirmed by independent paired readback.",
                preset.name()
            ),
            _ => message.into(),
        };
        // A no-op that closed an owner recorded under another selector claims no
        // return to that selector: name the one firmware reports instead.
        let released = *permit.released.lock().map_err(|_| io::ErrorKind::Other)?;
        if status == 1
            && let (Some(owned), Some(observed)) = (released, observed)
            && let Some(firmware) = observed.projection().0.and_then(preset_words)
            && let Some(recorded) = preset_words(if owned == 2 { 2 } else { 1 })
            && recorded != firmware
        {
            message.push_str(&format!(
                " OpenBlade's earlier fan change under {recorded} was released without a fan setter: firmware already reports Automatic fans under {firmware}."
            ));
        }
        if status == 1 && permit.performance_gpu.load(Ordering::Acquire) {
            message.push_str(
                " The saved performance intent was not applied: GPU clock handling is incomplete.",
            );
        } else if status == 1 && matched.is_none() {
            // Managed's trigger is request-level (`previousManual`), so a retry after
            // a refused return, or fans already Automatic, carries no preset. Say so
            // when the confirmed selector is not the saved native preset.
            let selected = permit
                .selected
                .lock()
                .map_err(|_| io::ErrorKind::Other)?
                .clone();
            if let (Some((power, settings)), Some(observed)) = (selected, observed)
                && settings["mode"].as_u64() == Some(0)
                && let Some(preset) = match performance {
                    Some(preset) => Some(preset),
                    // Name a saved destination this request did not carry too.
                    None => permit.saved_preset(power)?,
                }
                && !openblade_domain::thermal_mutation::confirms(observed, preset)
            {
                // The performance writer has its own admission and no fan
                // baseline gate, so a new performance request is the next step
                // whatever the fan recipe; it is not that request's precondition.
                message.push_str(&format!(
                    " The saved {} performance intent was not applied: submit a new performance request to apply it.",
                    preset.name()
                ));
            }
        }
        // A clear on unclassified power: managed replies Stored once no owned
        // change remains, and returns the failed handback otherwise.
        let rejected = permit
            .rejected
            .lock()
            .map_err(|_| io::ErrorKind::Other)?
            .clone()
            .filter(|_| !permit.accepted.load(Ordering::Acquire));
        let outcome = if let Some(rejected) = rejected {
            Outcome {
                status: 3,
                message: format!("The requested intent was rejected: {rejected}"),
            }
        } else if permit.unclassified_blocked.load(Ordering::Acquire)
            && !permit.accepted.load(Ordering::Acquire)
        {
            Outcome {
                status: 2,
                message: UNCLASSIFIED_BLOCKED.into(),
            }
        } else if permit.unclassified_clear.load(Ordering::Acquire) && !permit.storage.pending() {
            if permit.storage.outstanding_fan() {
                Outcome {
                    status: 2,
                    message: format!("Fan control could not be handed back to firmware. {message}"),
                }
            } else {
                Outcome {
                    status: 0,
                    message: UNCLASSIFIED_CLEARED.into(),
                }
            }
        } else {
            Outcome { status, message }
        };
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
        // The owner's own capture authorizes Stop only inside its lifecycle
        // epoch. After a power or session event it cannot, and only the
        // settle-only permit may run: it reads, and appends 51 on Automatic.
        let deadline = Instant::now() + std::time::Duration::from_secs(3);
        let result = if permit.authorize().is_some() {
            reader.mutate_cleanup(permit.clone(), deadline).await
        } else if let Some(settle) = FanSettlePermit::capture(&permit) {
            reader.mutate_cleanup(settle, deadline).await
        } else {
            verify_retained_failure(&self.fatal)?;
            Err(io::Error::other(
                "Fresh owned fan cleanup admission is unavailable.",
            ))
        };
        if !matches!(permit.storage.progress(), Some(50 | 51)) || result?.is_none() {
            return Err(io::Error::other(
                "Automatic fan handback was not independently confirmed; evidence is retained.",
            ));
        }
        Ok(())
    }
}
#[cfg(all(test, feature = "service-fan-mutation-v2"))]
impl Runtime {
    /// The settle-only permit owned Stop would capture, after claiming the
    /// owner's one release attempt as Stop does.
    pub(in super::super) fn settle_permit_for_tests(&self) -> Option<Arc<dyn Permit>> {
        let owner = self
            .fan_pending
            .borrow()
            .clone()
            .filter(|p| p.owned.load(Ordering::Acquire))
            .or(self.fan_owner.borrow().clone())?;
        owner.release.store(true, Ordering::Release);
        Some(FanSettlePermit::capture(&owner)?)
    }
}
/// Managed `PerformancePreset` display words for a confirmed performance wire value.
fn preset_words(wire: i32) -> Option<&'static str> {
    Some(match wire {
        0 => "Silent",
        1 => "Balanced",
        2 => "Performance",
        3 => "Battery Saver",
        4 => "Battery Balanced",
        5 => "Custom",
        _ => return None,
    })
}

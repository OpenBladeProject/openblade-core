//! Installed explicit fan runtime; one original custody and one global journal.
use super::*;
use crate::discovery::performance::mutation::RecordPins;
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
    // A reconciliation's drift decision, shared by its steps; `None` for
    // an explicit request.
    #[cfg(feature = "service-profile-triggers-v1")]
    pub(in super::super) transition: Option<Arc<thermal_drift::Transition>>,
}
/// A fan request refused by `Refusal::Deadline` after its intent was saved.
const FAN_DEADLINE_STORED: &str = "Fan override intent was stored. Hardware result: The remaining request time cannot cover this recipe at the measured device pace; no fan setter was submitted.";
// Managed composes `Fans could not return to Automatic with the saved Custom
// performance intent.` with ApplyScopedAsync's `Unsupported("Performance")`.
const FAN_CUSTOM_UNSUPPORTED_STORED: &str = "Fan override intent was stored. Hardware result: Fans could not return to Automatic with the saved Custom performance intent. Performance value is not supported by the verified capability table.";
const FAN_CUSTOM_UNSUPPORTED_CLEARED: &str = "Fan override was cleared. Hardware result: Fans could not return to Automatic with the saved Custom performance intent. Performance value is not supported by the verified capability table.";
const FAN_DEADLINE_CLEARED: &str = "Fan override was cleared. Hardware result: The remaining request time cannot cover this recipe at the measured device pace; no fan setter was submitted.";
/// A fan request refused by the shared thermal fence (`Refusal::ThermalPending`)
/// after its intent was saved: managed `ScopedThermalWriteBlockReason`.
const FAN_PENDING_STORED: &str = "Fan override intent was stored. Hardware result: A thermal write requires read-only reconciliation before another command can be submitted.";
const FAN_PENDING_CLEARED: &str = "Fan override was cleared. Hardware result: A thermal write requires read-only reconciliation before another command can be submitted.";
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
    // A reconciliation of saved intent (`Runtime::apply_fan_locked`): the
    // classes it applies at (`Classes`). It stores nothing.
    reconcile: Option<Classes>,
    // A reconciliation's own adapter reading found a classified class
    // outside its classes: nothing was accepted or written.
    unchanged: AtomicBool,
    // The classified class a reconciliation's reading found inside its classes.
    class: Mutex<Option<i32>>,
    // A reconciliation's drift decision and the exclusion it made
    // (`thermal_drift`).
    #[cfg(feature = "service-profile-triggers-v1")]
    transition: Option<Arc<thermal_drift::Transition>>,
    // Confirmed thermal drift: this request's confirmed fan write is
    // OpenBlade's own, and a possibly submitted one is settled by the next
    // reading (`record_fan`).
    #[cfg(feature = "service-profile-triggers-v1")]
    drift: Arc<Mutex<thermal_drift::Tracker>>,
    #[cfg(feature = "service-profile-triggers-v1")]
    excluded: Mutex<Option<thermal_drift::Exclusion>>,
    application: Foreground,
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
    // Refused before the first setter: the remaining request time cannot cover
    // the recipe at the measured device pace (`Refusal::Deadline`).
    deadline: AtomicBool,
    // Refused before any row or setter by the shared thermal fence
    // (`Refusal::ThermalPending`): a GPU clock submission is outstanding or
    // uncertain.
    thermal_pending: AtomicBool,
    gpu_fence: GpuFence,
    // This request returns saved manual fans to Automatic (`previousManual`).
    returning: AtomicBool,
    record: Mutex<Option<FanRecord>>,
    // The IDs the Reader pinned for `record`, until it settles (`settle_pins`).
    pins: RecordPins,
    settled: Arc<SettledPins>,
    accepted: AtomicBool,
    owned: AtomicBool,
    release: AtomicBool,
    prepared: AtomicBool,
    joined: AtomicBool,
    registered: AtomicBool,
    // A job of this owner's one cleanup attempt (`release`) registered: from
    // then on it may have sent the owner's reserved cleanup IDs, the
    // baseline read's included before any row, so no other admission may
    // run the attempt again.
    cleanup_registered: AtomicBool,
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
    /// Appends `progress` under this permit's own journal admission: the
    /// owned-Stop one for its cleanup, and the request's otherwise.
    fn write(&self, progress: u8) -> io::Result<()> {
        self.write_in(progress, self.cleanup().then_some(CleanupScope::OwnedStop))
    }
    /// Appends `progress` for this owner's exact record. A cleanup row is
    /// journaled under that cleanup scope's custody admission.
    fn write_in(&self, progress: u8, cleanup: Option<CleanupScope>) -> io::Result<()> {
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
            .record_fan(entry, cleanup)
            .inspect_err(|e| remember_failure(&self.fatal, e))
    }
    /// Queues the record's pins for the next Reader to unpin. Called only
    /// after a durable row closes the record: its own 50, 51 or 6, or a newer
    /// owner's 30 or 50 (`supersede_prior`). From then on nothing sends the
    /// record's IDs: owned Stop and the settle-only permit act only for the
    /// current owner, and the journal checks later battery IDs against the
    /// current owner's alone. A sent ID stays quarantined after unpinning.
    /// The pins move to the queue, so a second call queues nothing.
    fn settle_pins(&self) {
        self.settled.settle(&self.pins);
    }
    /// Drops the earlier owner once this request's 30 or 50 row has replaced
    /// it, and settles its record. It no longer owns anything, so owned Stop
    /// can never select it with cleanup IDs that are about to be unpinned.
    fn supersede_prior(&self) -> io::Result<()> {
        if let Some(prior) = self.prior.lock().map_err(|_| io::ErrorKind::Other)?.take() {
            prior.owned.store(false, Ordering::Release);
            prior.settle_pins();
        }
        Ok(())
    }
    /// The saved destination a return to Automatic applies, resolved as a
    /// performance request resolves it. Managed writes every destination there
    /// (`ApplyDirectAutomaticFanTransitionOutcomeAsync`). A configured GPU
    /// clock effect has no native writer, so then the observed selector is kept.
    /// `profile` is the resolution at `power` the caller read.
    fn saved_preset(
        &self,
        power: i32,
        profile: Option<&Resolution<'_>>,
    ) -> io::Result<Option<Destination>> {
        // A temporary override kept for another class has expired for both
        // reads (Storage expires it in each), as for a performance request.
        let desired = self.storage.desired(power, profile)?;
        if self.storage.gpu_incomplete(power, profile)? {
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
    fn override_rejected(&self, power: i32) -> io::Result<Option<String>> {
        override_rejected(
            &self.storage,
            self.fresh.as_ref(),
            self.value.as_ref(),
            power,
        )
    }
}
/// Managed's checks in `SetFanOverrideCoreAsync`, run on classified power
/// before the fan override `value` is stored, against managed's power source
/// (`managed_power_source`): an external reading under Windows battery is
/// checked as Battery. Unclassified power keeps its own admission
/// (`CanAdmitUnclassifiedExternalControl`).
fn override_rejected(
    storage: &Storage,
    fresh: &dyn Fresh,
    value: Option<&FanOverride>,
    power: i32,
) -> io::Result<Option<String>> {
    let Some(power) = managed_power_source(power, fresh) else {
        return Ok(None);
    };
    let Some((profile_performance, profile_fans)) = storage.profile_thermal(power)? else {
        return Ok(None);
    };
    let performance = storage
        .current_performance_override(power)?
        .unwrap_or(profile_performance);
    Ok(fan_override_rejected(
        value.map(|v| i32::from(v.value)),
        performance,
        profile_fans,
        power,
    ))
}
/// A fan request whose fresh admission refused before anything was accepted.
const FAN_ADMISSION_UNAVAILABLE: &str = "Current fan admission is unavailable.";
#[cfg(feature = "service-profile-triggers-v1")]
impl Runtime {
    /// Managed `ExecuteScopedMutationAsync` for an explicit fan request while
    /// the low-power gate is set: the override is validated and stored as
    /// `FanPermit::resolve_fan` stores it at a battery reading, and nothing is
    /// read or submitted. The reply is managed's committed message with its
    /// per-write refusal (`deferred_fan`), and the pair owes a reconciliation
    /// for the first report after the gate clears
    /// (`Runtime::owe_stored_intent`). Only Windows battery classifies power
    /// without an adapter reading (`triggers::deferred_store_refusal`).
    ///
    /// An owned fan change not yet handed back stores nothing: its
    /// continuity is proved by a write admission, which the gate refuses, and
    /// the handback it owes needs a write.
    fn store_fan_deferred(
        &self,
        store: super::triggers::DeferredStore<'_>,
        value: Option<FanOverride>,
        settings: Option<Value>,
    ) -> io::Result<Outcome> {
        use super::triggers::DeferredRefusal;
        match super::triggers::deferred_store_refusal(store.fresh) {
            Some(DeferredRefusal::Admission) => {
                return Ok(Outcome::blocked(FAN_ADMISSION_UNAVAILABLE));
            }
            Some(DeferredRefusal::Unclassified) => {
                return Ok(Outcome::blocked(UNCLASSIFIED_BLOCKED));
            }
            None => {}
        }
        if store.storage.outstanding_fan() {
            return Ok(Outcome::blocked(FAN_HANDBACK_OWED));
        }
        match self.store_fan_now(store, value, settings) {
            Err(error) if is_fatal_admission(&error) => Err(error),
            Err(_) => Ok(Outcome::blocked(FAN_NOT_ACCEPTED)),
            stored => stored,
        }
    }
    /// Every check and read of the store first, including the settings the
    /// save leaves (`Storage::plan_fan_save`), then the save and the debt
    /// together: an error before the save stores nothing, and nothing after
    /// it can fail.
    fn store_fan_now(
        &self,
        store: super::triggers::DeferredStore<'_>,
        value: Option<FanOverride>,
        settings: Option<Value>,
    ) -> io::Result<Outcome> {
        use super::triggers::BATTERY;
        let super::triggers::DeferredStore {
            storage,
            fresh,
            application,
            pair,
        } = store;
        let generation = fresh
            .power_generation()
            .ok_or(io::ErrorKind::PermissionDenied)?;
        storage.observe_power(BATTERY, generation)?;
        if let Some(message) = override_rejected(storage, fresh, value.as_ref(), BATTERY)? {
            return Ok(rejected_outcome(&message));
        }
        // A saved document the domain decoder refuses stores nothing.
        let layer = storage.profile_layer(application)?;
        let profile = layer.at(BATTERY)?;
        let profile = profile.as_ref();
        let plan = storage.plan_fan_save(value.as_ref(), settings.as_ref(), BATTERY, profile)?;
        // Managed's return to Automatic from saved manual intent
        // (`ApplyDirectAutomaticFanTransitionOutcomeAsync`) applies the saved
        // preset first, and its gate refuses that step: the reply names the
        // preset, as `FanPermit::resolve_fan` carries it.
        let returning = if plan.leaving_manual && plan.settings.as_ref().is_some_and(automatic) {
            match storage.desired(BATTERY, profile)? {
                Some((destination, _)) => Some(destination.name()),
                None if storage.custom_unsupported(BATTERY, profile)? => Some("Custom"),
                None => None,
            }
        } else {
            None
        };
        let cleared = value.is_none();
        storage.save_fan(value, settings, BATTERY)?;
        self.owe_stored_intent(pair);
        Ok(deferred_fan(cleared, returning))
    }
}
/// Whether fan `settings` decode to an Automatic intent.
#[cfg(feature = "service-profile-triggers-v1")]
fn automatic(settings: &Value) -> bool {
    FanIntent::checked(
        settings["mode"].as_u64().unwrap_or(255) as u8,
        settings["fixedSpeedRpm"].as_u64().unwrap_or(0) as u16,
    )
    .is_some_and(|intent| !intent.manual())
}
/// Managed's reply for a fan override its own checks reject.
fn rejected_outcome(message: &str) -> Outcome {
    Outcome {
        status: 3,
        message: format!("The requested intent was rejected: {message}"),
    }
}
/// The reply of an explicit fan request whose intent was stored while the
/// low-power gate was set: the committed message, a refused return to
/// Automatic with the saved preset `returning` names, and managed's per-write
/// refusal (`LOW_POWER_STEP`).
#[cfg(feature = "service-profile-triggers-v1")]
fn deferred_fan(cleared: bool, returning: Option<&str>) -> Outcome {
    let committed = if cleared {
        "Fan override was cleared."
    } else {
        "Fan override intent was stored."
    };
    let returning = returning.map_or(String::new(), |name| {
        format!("Fans could not return to Automatic with the saved {name} performance intent. ")
    });
    Outcome::blocked(format!(
        "{committed} Hardware result: {returning}{}",
        super::triggers::LOW_POWER_STEP
    ))
}
/// A fan request that stored nothing: admission or persistence failed.
const FAN_NOT_ACCEPTED: &str =
    "Fan intent was not accepted; fresh admission or persistence did not complete.";
/// A fan request in low power while an owned fan change still owes its
/// handback to firmware Automatic (`Runtime::store_fan_deferred`).
#[cfg(feature = "service-profile-triggers-v1")]
const FAN_HANDBACK_OWED: &str = "Fan intent was not stored: OpenBlade still owns a fan change that has not been handed back to firmware Automatic, and the laptop is in a low-power transition.";
impl Permit for FanPermit {
    fn thermal_fenced(&self, budget: std::time::Duration) -> bool {
        self.gpu_fence.as_ref().is_some_and(|fenced| fenced(budget))
    }
    fn refused(&self, cause: Refusal) {
        match cause {
            Refusal::UnclassifiedBlocked => {
                self.unclassified_blocked.store(true, Ordering::Release);
            }
            Refusal::Deadline => self.deadline.store(true, Ordering::Release),
            Refusal::ThermalPending => self.thermal_pending.store(true, Ordering::Release),
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
            let returning = self.returning.load(Ordering::Acquire);
            if settings.is_null() && !returning {
                return Ok(true);
            }
            // Resolved again from the saved profiles as they are now.
            let layer = self.storage.profile_layer(&self.application)?;
            let profile = layer.at(power)?;
            if !settings.is_null()
                && self.storage.desired_fan(power, profile.as_ref())? != Some(settings)
            {
                return Ok(false);
            }
            if !returning {
                return Ok(true);
            }
            let performance = *self.performance.lock().map_err(|_| io::ErrorKind::Other)?;
            Ok(self.saved_preset(power, profile.as_ref())? == performance)
        })()
        .inspect_err(|e| remember_failure(&self.fatal, e))
        .unwrap_or(false)
    }
    fn mutation_registered(&self) {
        self.registered.store(true, Ordering::Release);
        if self.release.load(Ordering::Acquire) {
            self.cleanup_registered.store(true, Ordering::Release);
        }
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
                let layer = self.storage.profile_layer(&self.application).ok()?;
                let profile = layer.at(*source).ok()?;
                // A synthesized handback (null settings) needs saved fan intent
                // to stay cleared; any other selection needs it unchanged.
                if self
                    .storage
                    .desired_fan(*source, profile.as_ref())
                    .ok()?
                    .as_ref()
                    != Some(settings).filter(|s| !s.is_null())
                {
                    return None;
                }
                // The preset carried by a return to Automatic is saved policy
                // too: every setter needs it to remain the current destination.
                if let Some(destination) = *self.performance.lock().ok()?
                    && self.saved_preset(*source, profile.as_ref()).ok()? != Some(destination)
                {
                    return None;
                }
                *self.verified.lock().ok()? = Some(inputs);
            }
        }
        // A convergence attempt's scope closes at Stop or at a refusal
        // (`thermal_drift::Scope::open`).
        #[cfg(feature = "service-profile-triggers-v1")]
        if self.transition.as_ref().is_some_and(|t| !t.open()) {
            return None;
        }
        proof
    }
    fn resolve(
        &self,
        _: i32,
        _: ObservedPerformance,
        _: &AtomicBool,
    ) -> io::Result<Option<Destination>> {
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
        // Managed applies saved intent only on classified power, so a
        // reconciliation that finds the adapter unclassified writes nothing.
        if power == UNCLASSIFIED_EXTERNAL
            && (self.reconcile.is_some() || baseline == ObservedPerformance::HyperBoostObserved)
        {
            self.unclassified_blocked.store(true, Ordering::Release);
            return Ok(None);
        }
        if battery_reading_contradicts_windows(power, self.fresh.as_ref()) {
            self.unclassified_blocked.store(true, Ordering::Release);
            return Ok(None);
        }
        self.observe_power(power)?;
        // A reconciliation applies only at the classes its trigger changed.
        if self
            .reconcile
            .is_some_and(|classes| !classes.contains(power))
        {
            self.unchanged.store(true, Ordering::Release);
            return Ok(None);
        }
        *self.class.lock().map_err(|_| io::ErrorKind::Other)? = Some(power);
        // A reconciliation validates no override, as managed
        // `ApplyFullTransitionAsync` does not.
        if self.reconcile.is_none()
            && let Some(message) = self.override_rejected(power)?
        {
            *self.rejected.lock().map_err(|_| io::ErrorKind::Other)? = Some(message);
            return Ok(None);
        }
        // One resolution for this step: saving the fan override below changes
        // no profile, and every later setter resolves again
        // (`destination_holds`, `authorize`).
        let layer = self.storage.profile_layer(&self.application)?;
        let profile = layer.at(power)?;
        let profile = profile.as_ref();
        // Managed applies the saved preset only when fans leave saved manual
        // intent (`previousManual` in ApplyFanControlTransitionCoreOutcomeAsync).
        // A reconciliation's trigger has already changed that intent, so it
        // reads the fans firmware reports instead, as its order does
        // (`DeviceSubsystemOrder.ForTransition`). It stores nothing, so the
        // override document is checked as a save would check it.
        // An explicit request reads everything its save leaves before the save
        // commits (`Storage::plan_fan_save`), so settings the domain decoder
        // refuses store nothing.
        let (leaving_manual, saved) = match self.reconcile {
            Some(_) => {
                self.storage.verify_overrides()?;
                (baseline.projection().1 != 0, None)
            }
            None => {
                let plan = self.storage.plan_fan_save(
                    self.value.as_ref(),
                    self.settings.as_ref(),
                    power,
                    profile,
                )?;
                self.storage
                    .save_fan(self.value.clone(), self.settings.clone(), power)?;
                (plan.leaving_manual, Some(plan.settings))
            }
        };
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
        let settings = match saved {
            Some(settings) => settings,
            None => self.storage.desired_fan(power, profile)?,
        };
        // Before the plan: managed resolves its write exclusions before it
        // tries a subsystem.
        #[cfg(feature = "service-profile-triggers-v1")]
        if let Some(transition) = &self.transition
            && let Some(exclusion) =
                transition.decide(baseline, drift_intent(&self.storage, power, profile)?)?
        {
            *self.excluded.lock().map_err(|_| io::ErrorKind::Other)? = Some(exclusion);
            return Ok(None);
        }
        let Some(settings) = settings else {
            return Ok(None);
        };
        let intent = FanIntent::checked(
            settings["mode"].as_u64().unwrap_or(255) as u8,
            settings["fixedSpeedRpm"].as_u64().unwrap_or(0) as u16,
        );
        let returning = leaving_manual && intent.is_some_and(|i| !i.manual());
        self.returning.store(returning, Ordering::Release);
        let performance = if returning {
            self.saved_preset(power, profile)?
        } else {
            None
        };
        // Managed applies the saved Custom through
        // `ApplyDirectAutomaticFanTransitionOutcomeAsync`; its capability check
        // refuses unsupported settings before any write, so fans stay manual and
        // the reply says why. The fan intent above is still saved.
        if returning && performance.is_none() && self.storage.custom_unsupported(power, profile)? {
            self.custom_unsupported.store(true, Ordering::Release);
            *self.selected.lock().map_err(|_| io::ErrorKind::Other)? = Some((power, settings));
            return Ok(None);
        }
        if returning && performance.is_none() {
            self.performance_gpu.store(
                self.storage.gpu_incomplete(power, profile)?,
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
        // An explicit reply publishes this observation. Its HyperBoost state now
        // decodes beside the observation-only capability record, but Automatic
        // under an observed HyperBoost is not admitted natively
        // (docs/windows-native-thermal-mutation.md), so the explicit path stays
        // fenced, as before; owned Stop settles it and replies nothing.
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
            owner.settle_pins();
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
        // A convergence attempt admits each setter's intent afresh, before
        // its row: its reservation, the HyperBoost journal and the policy
        // must still hold (`thermal_drift::Scope::admits`).
        #[cfg(feature = "service-profile-triggers-v1")]
        if (10..30).contains(&progress)
            && progress.is_multiple_of(2)
            && let Some(transition) = &self.transition
            && !transition.admits(true)
        {
            return Err(io::ErrorKind::PermissionDenied.into());
        }
        self.write(progress)?;
        // A journaled setter intent (10, 12, ...; 30 and 50 are the
        // confirmations) precedes its setter. A request dropped at its bound
        // or deadline, or failed after it, has no readback of its own, so the
        // next reading decides whether the write landed (`submitted_fans`);
        // the row it leaves unsettled still fences writes. Owned Stop
        // cleanup (40 and up) hands fans back and leaves drift alone.
        #[cfg(feature = "service-profile-triggers-v1")]
        if (10..30).contains(&progress) && progress.is_multiple_of(2) {
            let manual = self
                .record
                .lock()
                .map_err(|_| io::ErrorKind::Other)?
                .as_ref()
                .map(|record| record.intent.manual());
            let carried = *self.performance.lock().map_err(|_| io::ErrorKind::Other)?;
            if let Some(manual) = manual {
                self.drift
                    .lock()
                    .map_err(|_| io::ErrorKind::Other)?
                    .submitted_fans(manual, carried);
            }
        }
        if progress == 0 {
            self.prepared.store(true, Ordering::Release);
        }
        if progress == 30 {
            self.supersede_prior()?;
            self.owned.store(true, Ordering::Release);
        }
        // 51 is owned Stop cleanup's no-setter Automatic proof (see `apply_fan`).
        if matches!(progress, 50 | 51) {
            self.supersede_prior()?;
            self.owned.store(false, Ordering::Release);
            self.settle_pins();
        }
        Ok(())
    }
    fn retained_pins(&self, ids: &[u8]) {
        self.pins.retain(ids);
    }
    fn settled_pins(&self) -> Vec<u8> {
        self.settled.take()
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
            // Known not submitted: this Reader has retired, so the next one
            // unpins the record's IDs.
            self.settle_pins();
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
    fresh: Arc<dyn Settle>,
    proof: ReadAdmission,
    // Built at Stop (`Runtime::gpu_fence`), not the owner request's: the
    // power change that moved the owner's epoch rearms its one ungated query.
    gpu_fence: GpuFence,
}
impl FanSettlePermit {
    fn capture(owner: &Arc<FanPermit>, gpu_fence: GpuFence) -> Option<Arc<Self>> {
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
                gpu_fence,
            })
        })
    }
}
impl Permit for FanSettlePermit {
    fn mutation_registered(&self) {
        self.owner.cleanup_registered.store(true, Ordering::Release);
    }
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
    // An owned handback is fenced as every thermal writer is (managed
    // blocks prior-authority cleanup while a thermal confirmation is
    // pending), by the fence built at Stop.
    fn thermal_fenced(&self, budget: std::time::Duration) -> bool {
        self.gpu_fence.as_ref().is_some_and(|fenced| fenced(budget))
    }
    fn refused(&self, cause: Refusal) {
        self.owner.refused(cause);
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
        self.owner.supersede_prior()?;
        self.owner.owned.store(false, Ordering::Release);
        self.owner.settle_pins();
        Ok(())
    }
    fn settled_pins(&self) -> Vec<u8> {
        self.owner.settled.take()
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
    fn resolve(
        &self,
        _: i32,
        _: ObservedPerformance,
        _: &AtomicBool,
    ) -> io::Result<Option<Destination>> {
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
/// The Service's owned fan handback under its cleanup admission
/// (`Cleanup`), which needs no console user: at Stop or shutdown when the
/// owner's own capture no longer authorizes Stop, as after a logoff. It
/// never runs at a low-power boundary (`low_power_cleanup.rs`). It runs the
/// owner's exact cleanup recipe and nothing else: the fresh baseline read,
/// 51 when firmware already reports Automatic, and otherwise 31, the
/// Automatic setters' 40-43 and their confirmed 50 under the owner's
/// selector and reserved cleanup IDs. It cannot prepare, resolve or store an
/// intent, so it applies no saved destination and no manual value.
#[cfg(feature = "service-fan-mutation-v2")]
struct FanHandbackPermit {
    owner: Arc<FanPermit>,
    cleanup: Arc<dyn Cleanup>,
    proof: ReadAdmission,
    // Built at Stop, as the settle-only permit's is.
    gpu_fence: GpuFence,
}
#[cfg(feature = "service-fan-mutation-v2")]
impl FanHandbackPermit {
    fn capture(
        owner: &Arc<FanPermit>,
        cleanup: Arc<dyn Cleanup>,
        gpu_fence: GpuFence,
    ) -> Option<Arc<Self>> {
        // Owned Stop and shutdown only: a low-power boundary hands no fans
        // back (`low_power_cleanup.rs`).
        if cleanup.scope() != CleanupScope::OwnedStop {
            return None;
        }
        verify_retained_failure(&owner.fatal).ok()?;
        if !owner.owned.load(Ordering::Acquire) || !owner.release.load(Ordering::Acquire) {
            return None;
        }
        cleanup
            .verify_health()
            .inspect_err(|e| remember_failure(&owner.fatal, e))
            .ok()?;
        let proof = cleanup.authorize()?;
        // The same original custody generation the owner wrote under: a
        // custody reopened since cannot hand back another custody's record.
        (proof.generation == owner.proof.generation).then(|| {
            Arc::new(Self {
                owner: owner.clone(),
                cleanup,
                proof,
                gpu_fence,
            })
        })
    }
}
#[cfg(feature = "service-fan-mutation-v2")]
impl Permit for FanHandbackPermit {
    fn mutation_registered(&self) {
        self.owner.cleanup_registered.store(true, Ordering::Release);
    }
    fn authorize(&self) -> Option<ReadAdmission> {
        verify_retained_failure(&self.owner.fatal).ok()?;
        self.cleanup
            .verify_health()
            .inspect_err(|e| remember_failure(&self.owner.fatal, e))
            .ok()?;
        if !self.owner.owned.load(Ordering::Acquire)
            || !self.owner.release.load(Ordering::Acquire)
            || self.cleanup.authorize() != Some(self.proof)
        {
            return None;
        }
        // Cleanup preserves newer saved intent, as the owner's own does.
        self.owner.storage.verify_fan_configuration().ok()?;
        Some(self.proof)
    }
    fn result_admitted(&self, expected: ReadAdmission) -> bool {
        matches!(self.owner.storage.progress(), Some(50 | 51))
            && verify_retained_failure(&self.owner.fatal).is_ok()
            && self.cleanup.verify_health().is_ok()
            && self.cleanup.authorize() == Some(expected)
            && expected == self.proof
    }
    fn is_fan(&self) -> bool {
        true
    }
    fn cleanup(&self) -> bool {
        true
    }
    // An owned handback is fenced as every thermal writer is (managed
    // blocks prior-authority cleanup while a thermal confirmation is
    // pending), by the fence built at Stop.
    fn thermal_fenced(&self, budget: std::time::Duration) -> bool {
        self.gpu_fence.as_ref().is_some_and(|fenced| fenced(budget))
    }
    fn refused(&self, cause: Refusal) {
        self.owner.refused(cause);
    }
    fn windows_on_battery(&self) -> Option<bool> {
        self.cleanup.on_battery()
    }
    // The handback commands are the same on every external class, as for
    // the owner's own cleanup (`FanPermit::destination_holds`).
    fn destination_holds(&self, _power: i32) -> bool {
        true
    }
    fn fan_record(&self) -> Option<FanRecord> {
        // Only the owner's exact retained record, never a default.
        self.owner.record.lock().ok()?.clone()
    }
    fn record_fan(&self, progress: u8) -> io::Result<()> {
        // The cleanup stages alone: no 0, 6 or 10-30 row, so no request
        // setter or Prepared row can be journaled under this admission.
        if !matches!(progress, 31 | 40..=43 | 50 | 51) {
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
        ) || (progress == 51 && self.owner.storage.pending())
        {
            return Err(io::ErrorKind::PermissionDenied.into());
        }
        self.owner.write_in(progress, Some(self.cleanup.scope()))?;
        if matches!(progress, 50 | 51) {
            self.owner.supersede_prior()?;
            self.owner.owned.store(false, Ordering::Release);
            self.owner.settle_pins();
        }
        Ok(())
    }
    fn retained_pins(&self, ids: &[u8]) {
        self.owner.pins.retain(ids);
    }
    fn settled_pins(&self) -> Vec<u8> {
        self.owner.settled.take()
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
    fn resolve(
        &self,
        _: i32,
        _: ObservedPerformance,
        _: &AtomicBool,
    ) -> io::Result<Option<Destination>> {
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
        self.cleanup.verify_health()?;
        Ok(Settlement::Complete)
    }
}
#[cfg(feature = "service-fan-mutation-v2")]
impl Runtime {
    /// A fresh capture of the Service's cleanup admission for `scope`
    /// (`Runtime::cleanup`). `None` when it is not composed or refuses; only
    /// fatal admission evidence is an error.
    pub(super) fn capture_cleanup(
        &self,
        reader: &super::super::mailbox_read::ReadSource,
        scope: CleanupScope,
    ) -> io::Result<Option<Arc<dyn Cleanup>>> {
        let Some(factory) = self.cleanup.get() else {
            return Ok(None);
        };
        verify_retained_failure(&self.fatal)?;
        reader.custody().verify_integrity()?;
        match factory(reader.custody(), scope) {
            Ok(cleanup) => Ok(Some(cleanup)),
            Err(error) if is_fatal_admission(&error) => Err(error),
            Err(_) => {
                verify_retained_failure(&self.fatal)?;
                Ok(None)
            }
        }
    }
}
impl FanPermit {
    /// Whether this permit still owns its manual fans for `sid` in
    /// `session`: owned and not being handed back.
    fn live_for(&self, sid: &str, session: u32) -> bool {
        self.owned.load(Ordering::Acquire)
            && !self.release.load(Ordering::Acquire)
            && self.sid == sid
            && self.session == session
    }
}
#[cfg(feature = "service-thermal-convergence-v1")]
impl Runtime {
    /// Whether a live owner of the outstanding fan row holds it for `sid`
    /// in `session` (`FanPermit::live_for`): the part of the fan step's
    /// continuity check a status projection knows without an admission.
    pub(super) fn holds_live_fan_owner(&self, sid: &str, session: u32) -> bool {
        self.fan_owner
            .borrow()
            .as_ref()
            .is_some_and(|owner| owner.live_for(sid, session))
    }
}
impl Runtime {
    /// Whether the live owner of the outstanding fan row holds it for `sid`
    /// in `session` under this exact continuity: the admission generation
    /// of `proof`, the same `custody` and the same `authentication`. A fan
    /// step refuses to write over an outstanding row without it, and a
    /// convergence attempt checks it before it reserves a claim.
    pub(super) fn holds_fan_continuity(
        &self,
        sid: &str,
        session: u32,
        proof: &ReadAdmission,
        custody: &Arc<dyn ReadCustody>,
        authentication: Option<(u32, i32)>,
    ) -> bool {
        self.fan_owner.borrow().as_ref().is_some_and(|owner| {
            owner.live_for(sid, session)
                && owner.proof.generation == proof.generation
                && Arc::ptr_eq(&owner.custody, custody)
                && owner.authentication == authentication
        })
    }
}
impl Runtime {
    /// Managed `IsExactFanIntentOwned` for the saved fan `desired` settings
    /// of `sid` at class `power`: this user's fan owner has not handed its
    /// manual fans back, it wrote exactly those settings at that class, and
    /// a reading confirmed them as OpenBlade's own with no fan change since.
    #[cfg(feature = "service-profile-triggers-v1")]
    pub(in super::super) fn owns_exact_fans(
        &self,
        sid: &str,
        session: u32,
        power: i32,
        desired: Option<&Value>,
    ) -> bool {
        let Some(desired) = desired else {
            return false;
        };
        let owner = self.fan_owner.borrow().clone();
        owner.is_some_and(|permit| {
            permit.sid == sid
                && permit.session == session
                && permit.storage.outstanding_fan()
                && permit.selected.lock().is_ok_and(|selected| {
                    selected
                        .as_ref()
                        .is_some_and(|(written, settings)| *written == power && settings == desired)
                })
        }) && self
            .drift
            .lock()
            .is_ok_and(|tracker| tracker.owns_manual_fans())
    }
    // Shared by explicit fan/performance requests and retained policy refresh.
    // Do not evict the sole original per-user Storage cache while its positive
    // in-process fan owner still owes a release. No stale proof is renewed.
    pub(super) fn capture(
        &self,
        sid: &str,
        session: u32,
        custody: Arc<dyn ReadCustody>,
    ) -> io::Result<(Arc<Storage>, Arc<dyn Fresh>)> {
        self.capture_scoped(sid, session, custody, false, Capture::Write)
    }
    /// The capture of an explicit request, report or save that stores
    /// intent. While the low-power gate is set or the lifecycle is unknown,
    /// a refused write capture falls back to the storage-only one
    /// (`Capture::Storage`), which keeps every other check, including the
    /// cross-user fan and temporary fences below. Its `Fresh` authorizes no
    /// read or write, so the request stores its intent and takes the
    /// low-power deferral; a reconciliation step always takes `capture`.
    pub(super) fn capture_intent(
        &self,
        sid: &str,
        session: u32,
        custody: Arc<dyn ReadCustody>,
    ) -> io::Result<(Arc<Storage>, Arc<dyn Fresh>)> {
        let captured = self.capture(sid, session, custody.clone());
        #[cfg(feature = "service-profile-triggers-v1")]
        if captured
            .as_ref()
            .is_err_and(|error| !is_fatal_admission(error))
            && self.in_low_power()
        {
            return self.capture_scoped(sid, session, custody, false, Capture::Storage);
        }
        captured
    }
    // Only explicit typed TFC false can select fresh same-SID recovery. This
    // never renews the old automatic proof or bypasses the shared fan fence.
    pub(super) fn capture_temporary_cancel(
        &self,
        sid: &str,
        session: u32,
        custody: Arc<dyn ReadCustody>,
    ) -> io::Result<(Arc<Storage>, Arc<dyn Fresh>)> {
        self.capture_scoped(sid, session, custody, true, Capture::Write)
    }
    fn capture_scoped(
        &self,
        sid: &str,
        session: u32,
        custody: Arc<dyn ReadCustody>,
        explicit_cancel: bool,
        capture: Capture,
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
        let captured = (self.factory)(sid, session, custody, capture)?;
        if recovery
            .as_ref()
            .is_some_and(|storage| !Arc::ptr_eq(storage, &captured.0))
        {
            return Err(io::ErrorKind::PermissionDenied.into());
        }
        #[cfg(any(
            feature = "service-keyboard-driver-mode-v1",
            all(
                feature = "service-hyperboost-v1",
                feature = "service-profile-triggers-v1"
            )
        ))]
        {
            *self.captured.borrow_mut() = Some(captured.0.clone());
        }
        Ok(captured)
    }

    pub(in super::super) async fn apply_fan(
        &self,
        mailbox: MailboxSource,
        fan: FanRequest,
    ) -> io::Result<(Outcome, Option<Snapshot>)> {
        let _serial = self.gate.lock().await;
        self.apply_fan_locked(mailbox, fan, None)
            .await
            .map(Stepped::reply)
    }
    /// One fan step under the held gate: an explicit request, or, with
    /// `reconcile` holding the classes it applies at, a reconciliation of
    /// saved intent that stores nothing and keeps every per-write admission,
    /// re-check, readback and stop-on-ambiguity path of the request
    /// (`Runtime::reconcile_locked`).
    pub(super) async fn apply_fan_locked(
        &self,
        mailbox: MailboxSource,
        fan: FanRequest,
        reconcile: Option<Classes>,
    ) -> io::Result<Stepped> {
        let FanRequest {
            caller,
            sid,
            request,
            value,
            settings,
            deadline,
            status: status_snapshot,
            #[cfg(feature = "service-profile-triggers-v1")]
            transition,
        } = fan;
        verify_retained_failure(&self.fatal)?;
        if caller.logon != Logon::Interactive
            || caller.control_session != Some(caller.session)
            || caller.pid.is_none()
        {
            return Ok(Stepped::new(
                Outcome::blocked("The active interactive Windows user is required."),
                None,
            ));
        }
        let MailboxSource::Reader(reader) = mailbox else {
            return Ok(Stepped::new(
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
        // A reconciliation step writes, so it never takes a storage capture.
        let captured = if reconcile.is_some() {
            self.capture(&sid, caller.session, reader.custody())
        } else {
            self.capture_intent(&sid, caller.session, reader.custody())
        };
        let (storage, fresh) = match captured {
            Ok(v) => v,
            Err(e) if is_fatal_admission(&e) => return Err(e),
            // Nothing was read from firmware or submitted.
            Err(e) => {
                return Ok(Stepped::transient(Outcome::blocked(format!(
                    "Fan configuration admission failed: {e}"
                ))));
            }
        };
        if storage.pending() {
            return Ok(Stepped::new(
                Outcome::blocked(storage.pending_reason()?),
                None,
            ));
        }
        fresh.verify_health()?;
        let Some(proof) = fresh.authorize() else {
            // An explicit request while the low-power gate is set stores its
            // intent without any read or write, as managed stores it before
            // its write admission refuses (`store_fan_deferred`).
            #[cfg(feature = "service-profile-triggers-v1")]
            if reconcile.is_none() && self.in_low_power() {
                reader.retain_policy(self.thermal_policy(
                    sid.clone(),
                    caller.session,
                    reader.custody(),
                ));
                return self
                    .store_fan_deferred(
                        super::triggers::DeferredStore {
                            storage: &storage,
                            fresh: fresh.as_ref(),
                            application: &self.foreground(&sid, caller.session),
                            pair: (&sid, caller.session),
                        },
                        value,
                        settings,
                    )
                    .map(|outcome| Stepped::new(outcome, None));
            }
            return Ok(Stepped::new(
                Outcome::blocked(FAN_ADMISSION_UNAVAILABLE),
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
            && !self.holds_fan_continuity(
                &sid,
                caller.session,
                &proof,
                &reader.custody(),
                fresh.authentication(),
            )
        {
            return Ok(Stepped::new(
                Outcome::blocked(
                    "Fan ownership belongs to an earlier or invalid continuity; read-only reconciliation is required.",
                ),
                None,
            ));
        }
        // The gate once an explicit request holds its own write admission, for
        // a request the gate overtakes later (`overtaken_by_gate`). A
        // reconciliation reads its own before each step.
        #[cfg(feature = "service-profile-triggers-v1")]
        let mark = reconcile.is_none().then(|| self.gate_mark());
        let application = self.foreground(&sid, caller.session);
        #[cfg(feature = "service-profile-triggers-v1")]
        if let Some(classes) = reconcile
            && let Some(refused) =
                refused_by_saved_intent(&storage, &application, classes, fresh.as_ref())?
        {
            return Ok(refused);
        }
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
            reconcile,
            unchanged: AtomicBool::new(false),
            class: Mutex::new(None),
            #[cfg(feature = "service-profile-triggers-v1")]
            transition,
            #[cfg(feature = "service-profile-triggers-v1")]
            drift: self.drift.clone(),
            #[cfg(feature = "service-profile-triggers-v1")]
            excluded: Mutex::new(None),
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
            thermal_pending: AtomicBool::new(false),
            gpu_fence: self.gpu_fence(),
            verified: Mutex::new(None),
            returning: AtomicBool::new(false),
            record: Mutex::new(None),
            pins: RecordPins::default(),
            settled: self.settled_pins.clone(),
            accepted: AtomicBool::new(false),
            owned: AtomicBool::new(false),
            release: AtomicBool::new(false),
            prepared: AtomicBool::new(false),
            joined: AtomicBool::new(false),
            registered: AtomicBool::new(false),
            cleanup_registered: AtomicBool::new(false),
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
            // The override document is checked before the profiles are read.
            let value = store.current_override(query)?;
            let layer =
                store.profile_layer(&policy_runtime.foreground(&policy_sid, caller.session))?;
            let profile = layer.at(query)?;
            let perf = store.desired(query, profile.as_ref())?;
            let fan_value = store.current_fan_override(query)?;
            let base = base.without_private_policy();
            #[cfg(feature = "service-profile-triggers-v1")]
            let convergence = {
                let class = convergence_power(query, policy_fresh.as_ref(), &store)?;
                policy_runtime.thermal_convergence(
                    &base,
                    &store,
                    &policy_sid,
                    caller.session,
                    class,
                    layer.at(class)?.as_ref(),
                )?
            };
            let projected = base
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
                    store.desired_fan(query, profile.as_ref())?,
                    power,
                    "Saved fan intent; target RPM remains unreadable.",
                )
                .bind_private_user(&policy_sid);
            #[cfg(feature = "service-profile-triggers-v1")]
            let projected = projected.with_thermal_convergence(convergence);
            policy_runtime.project_saved_members(
                projected,
                &store,
                &policy_sid,
                caller.session,
                query,
            )
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
            // A reconciliation names why the store is fenced, as its
            // performance step does: its trigger submitted nothing else.
            match permit.reconcile {
                Some(_) => permit.storage.pending_reason()?,
                None => {
                    "Fan submission or owned release remains unresolved; further writes are fenced."
                }
            }
        } else if permit.accepted.load(Ordering::Acquire)
            && permit.custom_unsupported.load(Ordering::Acquire)
        {
            if permit.value.is_none() {
                FAN_CUSTOM_UNSUPPORTED_CLEARED
            } else {
                FAN_CUSTOM_UNSUPPORTED_STORED
            }
        } else if permit.thermal_pending.load(Ordering::Acquire) {
            match (
                permit.accepted.load(Ordering::Acquire),
                permit.value.is_none(),
            ) {
                (true, true) => FAN_PENDING_CLEARED,
                (true, false) => FAN_PENDING_STORED,
                (false, _) => PENDING_OTHER,
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
            FAN_NOT_ACCEPTED
        };
        // A reconciliation (value None) committed nothing: only its hardware
        // result is reported, as managed `ApplyFullTransitionAsync` reports it.
        let message = match permit.reconcile {
            Some(_) => message
                .strip_prefix("Fan override was cleared. Hardware result: ")
                .unwrap_or(message),
            None => message,
        };
        if result.as_ref().is_err_and(is_fatal_admission) {
            return result.map(|_| unreachable!());
        }
        if permit.unchanged.load(Ordering::Acquire) {
            // Nothing was accepted or written; the reconciliation replies.
            return Ok(Stepped::unchanged());
        }
        // The reconciliation's drift decision wrote nothing of either thermal
        // subsystem (managed `ExternalChangePreserved` or
        // `PreservedForHyperBoost`).
        #[cfg(feature = "service-profile-triggers-v1")]
        if let Some(exclusion) = *permit.excluded.lock().map_err(|_| io::ErrorKind::Other)? {
            return Ok(Stepped {
                class: *permit.class.lock().map_err(|_| io::ErrorKind::Other)?,
                excluded: true,
                ..Stepped::new(Outcome::blocked(exclusion.message("Fans")), None)
            });
        }
        // A job that ended before its intent was accepted, which journals
        // nothing (an adapter not classified yet, or time or admission that ran
        // out while reading; a reconciliation is never rejected), a pace
        // refusal, or a job that prepared a plan and then ran out of time or
        // admission. An accepted job that prepared nothing found no native
        // plan, which the same saved intent finds again. A job that stopped
        // after its intent row may have submitted: the journal says so, and
        // the reconciliation then retries nothing.
        let transient = observed_none(&result)
            && (!permit.accepted.load(Ordering::Acquire)
                || permit.deadline.load(Ordering::Acquire)
                || permit.thermal_pending.load(Ordering::Acquire)
                || permit.prepared.load(Ordering::Acquire));
        let class = *permit.class.lock().map_err(|_| io::ErrorKind::Other)?;
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
        // The Reader returns a readback only once it confirms the fan intent,
        // written or already reported: OpenBlade's own fans (managed
        // `ConfirmIntentional`), and its carried destination when the
        // readback confirms that too. A write without a readback is settled
        // by the next reading (`record_fan`).
        #[cfg(feature = "service-profile-triggers-v1")]
        if let Some(readback) = observed {
            let mut drift = self.drift.lock().map_err(|_| io::ErrorKind::Other)?;
            drift.own_fans_read(readback);
            if let Some(destination) = matched {
                drift.own_performance(destination);
            }
        }
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
        } else if status == 1 && matched.is_none() && permit.reconcile.is_none() {
            // Managed's trigger is request-level (`previousManual`), so a retry after
            // a refused return, or fans already Automatic, carries no preset. Say so
            // when the confirmed selector is not the saved native preset. A
            // reconciliation runs its own performance step, which reports it.
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
                    None => {
                        let layer = permit.storage.profile_layer(&permit.application)?;
                        permit.saved_preset(power, layer.at(power)?.as_ref())?
                    }
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
            rejected_outcome(&rejected)
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
        // Stored but left Blocked while the gate set, or set and cleared, with
        // nothing submitted: the deferral it would have taken had the gate set
        // first. A confirmed result, even one with no setter, and a Stored
        // reply (a clear on unclassified power) keep theirs.
        #[cfg(feature = "service-profile-triggers-v1")]
        if let Some(mark) = mark
            && outcome.status == 2
            && permit.accepted.load(Ordering::Acquire)
            && self.overtaken_by_gate(mark, &permit.storage, &permit.request)
        {
            self.owe_stored_intent((&sid, caller.session));
            let returning = match performance {
                _ if !permit.returning.load(Ordering::Acquire) => None,
                Some(preset) => Some(preset.name()),
                None => permit
                    .custom_unsupported
                    .load(Ordering::Acquire)
                    .then_some("Custom"),
            };
            return Ok(Stepped::new(
                deferred_fan(permit.value.is_none(), returning),
                None,
            ));
        }
        let project = || -> io::Result<Option<Snapshot>> {
            if !permit.accepted.load(Ordering::Acquire) {
                return Ok(None);
            }
            let (power, settings) = permit
                .selected
                .lock()
                .map_err(|_| io::ErrorKind::Other)?
                .clone()
                .unwrap_or((3, Value::Null));
            let mut base = status_snapshot
                .clone()
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
            let perf_override = permit.storage.current_override(power)?;
            let layer = permit.storage.profile_layer(&permit.application)?;
            let perf = permit.storage.desired(power, layer.at(power)?.as_ref())?;
            // From this step's own readback; the fan publication keeps it
            // (`Snapshot::rebase_thermal_mutation`).
            #[cfg(feature = "service-profile-triggers-v1")]
            let convergence = self.thermal_convergence(
                &base,
                &permit.storage,
                &sid,
                caller.session,
                power,
                layer.at(power)?.as_ref(),
            )?;
            #[cfg(feature = "service-profile-triggers-v1")]
            {
                base = base.with_thermal_convergence(convergence);
            }
            base = base
                .thermal_profiles(permit.storage.profiles()?)?
                .thermal_intent(
                    perf_override.as_ref(),
                    perf.map(|v| v.0),
                    &outcome.message,
                    Some(power),
                )
                .thermal_provenance(perf.map(|v| v.1));
            // A reconciliation reports the override layer it applied over.
            let fan_override = match permit.reconcile {
                Some(_) => permit.storage.current_fan_override(power)?,
                None => permit.value.clone(),
            };
            Ok(Some(
                base.fan_intent(
                    fan_override.as_ref(),
                    if settings.is_null() {
                        None
                    } else {
                        Some(settings)
                    },
                    power,
                    &outcome.message,
                )
                .bind_private_user(&sid),
            ))
        };
        match project() {
            Ok(snapshot) => Ok(Stepped {
                transient,
                class,
                ..Stepped::new(outcome, snapshot)
            }),
            Err(error) if permit.reconcile.is_none() || is_fatal_admission(&error) => Err(error),
            // A reconciliation may already have written: its reply must reach
            // the trigger's caller (`after_write_unreadable`).
            Err(_) => Ok(Stepped::new(after_write_unreadable(&permit.storage)?, None)),
        }
    }
    /// Whether a fan write is not settled: a fan job not yet joined, or a fan
    /// journal row not yet resolved (managed
    /// `FanStopController.BlocksOrdinaryHardware`). The keyboard Driver-mode
    /// owner writes nothing then. Fans OpenBlade holds manual and settled do
    /// not block it.
    #[cfg(feature = "service-keyboard-driver-mode-v1")]
    pub(super) fn fan_unsettled(&self) -> bool {
        self.fan_pending
            .borrow()
            .iter()
            .chain(self.fan_owner.borrow().iter())
            .any(|p| !p.joined.load(Ordering::Acquire) || p.storage.pending())
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
    /// Owned Stop's fan handback, paced to `deadline`: what is left of the
    /// source drain, whose timeout would otherwise cancel a handback that
    /// has written its 31 (`StatusSource::drain_by`). Each cleanup job also
    /// keeps its own 3 s observation bound (`MAX_OBSERVATION`).
    pub(in super::super) async fn stop_fans(
        &self,
        mailbox: &MailboxSource,
        deadline: Instant,
    ) -> io::Result<()> {
        let _serial = self.gate.lock().await;
        let MailboxSource::Reader(reader) = mailbox else {
            return Ok(());
        };
        // Always join original active work before checking positive ownership.
        reader.drain_active().await?;
        let latest = self
            .fan_pending
            .borrow()
            .clone()
            .or(self.fan_owner.borrow().clone());
        if latest.as_ref().is_some_and(|p| p.storage.pending()) {
            return Err(io::Error::other(
                "Fan submission remains unresolved at Stop.",
            ));
        }
        if latest.as_ref().is_none_or(|p| !p.storage.outstanding_fan()) {
            return Ok(());
        }
        let Some(permit) = self.owned_fan_permit() else {
            return Err(io::Error::other(
                "Original fan cleanup ownership is unavailable.",
            ));
        };
        if permit.release.swap(true, Ordering::AcqRel) {
            return Err(io::Error::other(
                "Owned Stop cleanup was already attempted; reserved IDs will not be reused.",
            ));
        }
        let result = self.run_owned_stop(reader, &permit, deadline).await;
        if !matches!(permit.storage.progress(), Some(50 | 51)) || result?.is_none() {
            return Err(io::Error::other(
                "Automatic fan handback was not independently confirmed; evidence is retained.",
            ));
        }
        Ok(())
    }
}
impl Runtime {
    /// The fan permit owned Stop cleans up after: the pending one when it
    /// owns a fan change, otherwise the confirmed owner when it does.
    fn owned_fan_permit(&self) -> Option<Arc<FanPermit>> {
        let owned = |p: &Arc<FanPermit>| p.owned.load(Ordering::Acquire);
        let pending = self.fan_pending.borrow().clone().filter(owned);
        pending.or_else(|| self.fan_owner.borrow().clone().filter(owned))
    }
    /// The owner's one Stop cleanup under the first admission that still
    /// holds. The owner's own capture authorizes Stop only inside its
    /// lifecycle epoch and console session. After a power or session event,
    /// or with nobody signed in, it cannot: the Service's own cleanup
    /// admission then runs the same handback recipe. Without it, only the
    /// settle-only permit may run: it reads, and appends 51 on Automatic.
    ///
    /// The owner's capture reads the fence its request built, which no
    /// power change has passed while that capture still authorizes; the
    /// Service's cleanup and the settle-only permit read one built here.
    ///
    /// A job the lane refused before registering sent nothing, so the next
    /// admission may run the attempt. A registered job may have sent the
    /// owner's reserved cleanup IDs, its baseline read's even before row 31,
    /// so its result is final: no later admission resends those IDs.
    async fn run_owned_stop(
        &self,
        reader: &super::super::mailbox_read::ReadSource,
        permit: &Arc<FanPermit>,
        deadline: Instant,
    ) -> io::Result<Option<ObservedPerformance>> {
        let ran = |result: &io::Result<_>| {
            permit.cleanup_registered.load(Ordering::Acquire)
                || result.as_ref().is_err_and(is_fatal_admission)
        };
        let mut result = Err(io::Error::other(
            "Fresh owned fan cleanup admission is unavailable.",
        ));
        if permit.authorize().is_some() {
            result = reader.mutate_cleanup(permit.clone(), deadline).await;
            if ran(&result) {
                return result;
            }
        }
        #[cfg(feature = "service-fan-mutation-v2")]
        if let Some(handback) = self
            .capture_cleanup(reader, CleanupScope::OwnedStop)?
            .and_then(|cleanup| FanHandbackPermit::capture(permit, cleanup, self.gpu_fence()))
        {
            result = reader.mutate_cleanup(handback, deadline).await;
            if ran(&result) {
                return result;
            }
        }
        if let Some(settle) = FanSettlePermit::capture(permit, self.gpu_fence()) {
            result = reader.mutate_cleanup(settle, deadline).await;
            if ran(&result) {
                return result;
            }
        }
        verify_retained_failure(&self.fatal)?;
        result
    }
}
#[cfg(all(test, feature = "service-fan-mutation-v2"))]
impl Runtime {
    /// The settle-only permit owned Stop would capture, after claiming the
    /// owner's one release attempt as Stop does.
    pub(in super::super) fn settle_permit_for_tests(&self) -> Option<Arc<dyn Permit>> {
        let owner = self.owned_fan_permit()?;
        owner.release.store(true, Ordering::Release);
        Some(FanSettlePermit::capture(&owner, self.gpu_fence())?)
    }
}
#[cfg(all(test, feature = "service-keyboard-driver-mode-v1"))]
impl Runtime {
    /// The handback owned Stop would capture under `scope`, after claiming
    /// the owner's one release attempt as Stop does.
    pub(in super::super) fn handback_permit_for_tests(
        &self,
        scope: CleanupScope,
    ) -> Option<Arc<dyn Permit>> {
        let owner = self.owned_fan_permit()?;
        owner.release.store(true, Ordering::Release);
        let cleanup = (self.cleanup.get()?)(owner.custody.clone(), scope).ok()?;
        Some(FanHandbackPermit::capture(
            &owner,
            cleanup,
            self.gpu_fence(),
        )?)
    }
}
/// Whether a fan job returned no reading.
fn observed_none(result: &io::Result<Option<ObservedPerformance>>) -> bool {
    result.as_ref().is_ok_and(Option::is_none)
}
/// The hardware result of a fan job that ended without a native plan for the
/// saved intent, as a reconciliation reports it.
#[cfg(feature = "service-profile-triggers-v1")]
const NO_RECIPE: &str = "This source or observed mode needs a native recipe that is not yet implemented, or admission/transaction capacity did not complete. No applied target is claimed.";
/// A reconciliation's fan step refused from saved intent alone, before any
/// firmware read or transaction pair (`Runtime::reconcile_locked`): at every
/// class its reading could find, the saved fan intent has no native plan
/// whatever the baseline. A mode `FanIntent::checked` refuses, such as
/// ManualCurve, has no recipe, and manual fans have none on battery
/// (`Plan::select` admits external sources only); the step would end with
/// `NO_RECIPE` after reading. As for the performance step
/// (`thermal_mutation::refused_by_saved_intent`), `classes` must cover every
/// class fresh Windows power admits.
#[cfg(feature = "service-profile-triggers-v1")]
fn refused_by_saved_intent(
    storage: &Storage,
    application: &Foreground,
    classes: Classes,
    fresh: &dyn Fresh,
) -> io::Result<Option<Stepped>> {
    if classes != Classes::admitted(fresh) {
        return Ok(None);
    }
    let refused = (|| {
        storage.verify_overrides()?;
        let layer = storage.profile_layer(application)?;
        for class in classes.iter() {
            // Each class is read in its own scope: a read at one class
            // expires a temporary kept for another (`Storage::unexpired`).
            let intent = storage.unexpired(|| {
                let profile = layer.at(class)?;
                Ok(storage.desired_fan(class, profile.as_ref())?.and_then(|s| {
                    FanIntent::checked(
                        s["mode"].as_u64().unwrap_or(255) as u8,
                        s["fixedSpeedRpm"].as_u64().unwrap_or(0) as u16,
                    )
                }))
            })?;
            if intent.is_some_and(|intent| !intent.manual() || class != 1) {
                return Ok(false);
            }
        }
        Ok(true)
    })();
    match refused {
        Ok(refused) => Ok(refused.then(|| Stepped::new(Outcome::blocked(NO_RECIPE), None))),
        Err(error) if is_fatal_admission(&error) => Err(error),
        Err(_) if storage.pending() => Ok(Some(Stepped::new(
            Outcome::blocked(storage.pending_reason()?),
            None,
        ))),
        Err(error) => Ok(Some(Stepped::transient(Outcome::blocked(format!(
            "Fan configuration admission failed: {error}"
        ))))),
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

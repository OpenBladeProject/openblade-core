//! Exact0581 installed battery request, retaining the shared worker and owner.
use super::*;
use crate::discovery::performance::mutation::RecordPins;
use crate::thermal_history::Entry;
use openblade_domain::battery_history::{BatteryRecord, CONTRACT, Request};

// One fixed public observation record, independent of per-user desired intent.
// Each getter owns its completion time. This history never authorizes a write.
#[derive(Default)]
pub(super) struct BatteryObservation {
    pub(super) policy: Option<(u8, Instant)>,
    temporary: Option<(bool, Instant)>,
}
impl BatteryObservation {
    fn current<T: Copy>(value: Option<(T, Instant)>, now: Instant) -> Option<T> {
        value
            .filter(|(_, at)| {
                now.saturating_duration_since(*at) < super::super::mailbox_read::RETENTION_WINDOW
            })
            .map(|(v, _)| v)
    }
    fn projection(&self, now: Instant) -> Option<(u8, Option<bool>)> {
        // v90 requires policy/protection when a battery group is present.
        // A still-young TFC observation remains internally retained at its own age.
        let raw = Self::current(self.policy, now)?;
        let temporary = Self::current(self.temporary, now);
        // The managed contract cannot represent Off + TFC true. Preserve both
        // observations internally; expose the conflicting group as unavailable.
        if raw == 0x50 && temporary == Some(true) {
            return None;
        }
        Some((raw, temporary))
    }
    fn next_expiry(&self, now: Instant) -> Option<Instant> {
        [
            self.policy.map(|(_, at)| at),
            self.temporary.map(|(_, at)| at),
        ]
        .into_iter()
        .flatten()
        .map(|at| at + super::super::mailbox_read::RETENTION_WINDOW)
        .filter(|at| *at > now)
        .min()
    }
}

/// Retries of an automatic source cancel that sent nothing at an unmoved
/// epoch (`Runtime::battery_source_event`), per enable.
#[cfg(feature = "service-temporary-charge-v1")]
const UNSENT_CANCEL_RETRIES: u8 = 2;
/// How one automatic source cancel was admitted (`battery_source_event`).
#[cfg(feature = "service-temporary-charge-v1")]
enum SourceCancel {
    /// The proof is at this tick's epoch: claim and attempt the cancel.
    Admitted {
        record: openblade_domain::temporary_charge_history::TemporaryChargeRecord,
        storage: Arc<Storage>,
        fresh: Arc<dyn Fresh>,
        proof: ReadAdmission,
    },
    /// No cancel can be attempted at this epoch: claim once and pause.
    Refused,
    /// A later notice, or low power, fenced the tick: claim nothing.
    Superseded,
}

/// What an automatic source cancel's claim took: the owner, which it marks
/// abandoned, and its intent, with the journal reservation it left in place.
/// The claim stands unless `roll_back` puts all of it back at once.
#[cfg(feature = "service-temporary-charge-v1")]
struct TemporaryClaim {
    owner: Arc<BatteryPermit>,
    taken: Option<super::super::thermal_storage::TemporaryTaken>,
}
#[cfg(feature = "service-temporary-charge-v1")]
impl TemporaryClaim {
    /// Takes `peeked` from the owner slot. `None`, with nothing taken, when
    /// the slot holds anything else.
    fn take(
        slot: &RefCell<Option<Arc<BatteryPermit>>>,
        peeked: &Arc<BatteryPermit>,
    ) -> Option<Self> {
        let owner = slot.borrow_mut().take();
        let owner = match owner {
            Some(owner) if Arc::ptr_eq(&owner, peeked) => owner,
            other => {
                *slot.borrow_mut() = other;
                return None;
            }
        };
        let taken = owner.storage.take_temporary_claim();
        owner.abandoned.store(true, Ordering::Release);
        Some(Self { owner, taken })
    }
    /// Puts back exactly what `take` took, or nothing. A newer owner, or a
    /// read since the claim that found the charge off, keeps the claim.
    fn roll_back(self, runtime: &Runtime) -> bool {
        self.put_back(runtime, |storage, taken| {
            storage.restore_temporary_claim(taken)
        })
    }
    /// Puts the owner and intent back for a retry with fresh IDs after
    /// `attempt` sent some of the enable's reserved IDs, which may still be
    /// answered, and submitted no setter (`Storage::restore_spent_claim`).
    /// Every later automatic cancel of the enable draws fresh IDs. The caller
    /// admits only an attempt that used the reserved IDs, so an enable is
    /// retried this way once.
    fn roll_back_spent(self, runtime: &Runtime, attempt: &BatteryPermit) -> bool {
        let owner = self.owner.clone();
        let restored = self.put_back(runtime, |storage, taken| {
            storage.restore_spent_claim(taken, &attempt.request)
        });
        if restored {
            owner.cancel_spent.store(true, Ordering::Release);
        }
        restored
    }
    fn put_back(
        self,
        runtime: &Runtime,
        restore: impl FnOnce(&Storage, super::super::thermal_storage::TemporaryTaken) -> bool,
    ) -> bool {
        if runtime.temporary_owner.borrow().is_some()
            || runtime.read_off_since_positive(&self.owner)
        {
            return false;
        }
        let Some(taken) = self.taken else {
            return false;
        };
        if !restore(&self.owner.storage, taken) {
            return false;
        }
        self.owner.abandoned.store(false, Ordering::Release);
        *runtime.temporary_owner.borrow_mut() = Some(self.owner);
        true
    }
}

pub(super) struct BatteryPermit {
    tfc: Mutex<Option<openblade_domain::temporary_charge_history::TemporaryChargeRecord>>,
    reserved_ids: Option<[u8; 8]>,
    // An automatic cancel's enable's reserved IDs, which an earlier attempt
    // sent: this attempt draws fresh IDs and names these in its record.
    superseded: Option<[u8; 8]>,
    // An enable whose reserved cancel IDs an attempt sent: every later
    // automatic cancel of it draws fresh IDs (`TemporaryClaim::roll_back_spent`).
    #[cfg(feature = "service-temporary-charge-v1")]
    cancel_spent: AtomicBool,
    // Automatic cancels of this enable that sent nothing and were retried at
    // an unmoved epoch (`UNSENT_CANCEL_RETRIES`).
    #[cfg(feature = "service-temporary-charge-v1")]
    unsent_retries: std::sync::atomic::AtomicU8,
    origin: Option<openblade_domain::temporary_charge_history::Origin>,
    #[cfg(feature = "service-temporary-charge-v1")]
    authentication: Option<(u32, i32)>,
    custody: Arc<dyn ReadCustody>,
    pub(super) sid: String,
    storage: Arc<Storage>,
    fresh: Arc<dyn Fresh>,
    request: String,
    requested: Request,
    owner: String,
    caller_pid: u32,
    pub(super) session: u32,
    proof: ReadAdmission,
    record: Mutex<Option<BatteryRecord>>,
    // The source-cancel IDs the Reader pinned for an enable's record, until
    // the enable no longer owns the charge, or a fresh-ID cancel's own IDs,
    // until its attempt joined (`settle_pins`).
    pins: RecordPins,
    settled: Arc<SettledPins>,
    observation: Arc<Mutex<BatteryObservation>>,
    prepared: AtomicBool,
    confirmed: AtomicBool,
    joined: AtomicBool,
    registered: AtomicBool,
    // Whether every ID of this attempt is still unsent: until the worker
    // begins the steps that send them, then as the pool reports once they stop.
    unsent: AtomicBool,
    abandoned: AtomicBool,
    positive_at: Mutex<Option<Instant>>,
    source: Arc<std::sync::atomic::AtomicU8>,
    cause: Mutex<String>,
    fatal: Arc<Mutex<Option<&'static str>>>,
}
// Only a definitively never-registered request can be retired by future drop.
// Registered workers retain their real join/submission evidence in ReadSource.
struct BatteryAwaitGuard(Arc<BatteryPermit>);
impl Drop for BatteryAwaitGuard {
    fn drop(&mut self) {
        if !self.0.registered.load(Ordering::Acquire) {
            self.0.joined.store(true, Ordering::Release);
        }
    }
}
impl BatteryPermit {
    fn write(&self, progress: u8) -> io::Result<()> {
        let tfc = self.tfc.lock().map_err(|_| io::ErrorKind::Other)?.clone();
        let record = self
            .record
            .lock()
            .map_err(|_| io::ErrorKind::Other)?
            .clone();
        let intent = match (&record, &tfc) {
            (Some(r), None) => r.hash()?,
            (None, Some(r)) => r.hash()?,
            _ => return Err(io::ErrorKind::PermissionDenied.into()),
        };
        self.storage
            .record_battery(Entry {
                tfc,
                battery: record,
                fan: None,
                request: self.request.clone(),
                owner: self.owner.clone(),
                user_sid: String::new(),
                caller_pid: self.caller_pid,
                session: self.session,
                requested: None,
                destination: 0,
                intent,
                progress,
            })
            .inspect_err(|e| remember_failure(&self.fatal, e))
    }
    fn historical_observation(&self) -> io::Result<Option<(u8, Option<bool>)>> {
        let observation = self.observation.lock().map_err(|_| io::ErrorKind::Other)?;
        Ok(observation.projection(Instant::now()))
    }
    fn reason(&self, s: &str) {
        if let Ok(mut v) = self.cause.lock() {
            *v = s.into();
        }
    }
    /// Whether this attempt is proven to have sent nothing: its worker
    /// joined, and either never began the steps that send its IDs or left
    /// every one of them held in the transaction pool. A retry resends these
    /// IDs, so one already sent could be answered by its older reply. No
    /// journal row or confirmation can exist without a request having been
    /// sent.
    #[cfg(feature = "service-temporary-charge-v1")]
    fn known_unsubmitted(&self) -> bool {
        self.joined.load(Ordering::Acquire) && self.unsent.load(Ordering::Acquire)
    }
    /// Queues an enable's source-cancel pins for whichever Reader next takes
    /// the queue (`Permit::settled_pins`), possibly one already running, to
    /// unpin at its retirement. The automatic cancel is the only sender of
    /// `cancel_ids`, and its fenced rollback trusts `Transactions::unsent` on
    /// them, which holds only while they stay pinned. So a caller settles only
    /// once no request can bear these IDs again:
    ///
    /// - the enable's own worker has joined, and the permit has left every
    ///   owner slot for good: a newer confirmed owner replaced it, its
    ///   reservation was released, or it joined without a confirmation;
    /// - no claim can put it back: a `TemporaryClaim` lives within one
    ///   `battery_source_event`, which decides its rollback before
    ///   `finish_temporary_release`, and `reconcile_temporary_owner` reaches
    ///   only permits in a slot, which a claimed owner is not;
    /// - every cancel attempt that could send these IDs has joined.
    ///
    /// The journal need not be settled: `NoMatchingReservation` can settle
    /// while `storage.pending()` holds, which is harmless because the cancel
    /// has joined, nothing resends its IDs and the pending store is fenced.
    /// A fresh-ID cancel attempt (`superseded`) pins its own IDs, which only
    /// it sends: it settles them once its worker has joined, whether its
    /// claim is rolled back or stands, since a later attempt draws new ones.
    /// The enable keeps its spent reserved IDs pinned as above.
    ///
    /// The pins move to the queue, so a second call queues nothing, and a
    /// sent ID stays quarantined.
    fn settle_pins(&self) {
        self.settled.settle(&self.pins);
    }
}
impl Permit for BatteryPermit {
    fn mutation_registered(&self) {
        self.registered.store(true, Ordering::Release);
    }
    fn observe_temporary_unsent(&self, unsent: bool) {
        self.unsent.store(unsent, Ordering::Release);
    }
    fn retained_pins(&self, ids: &[u8]) {
        self.pins.retain(ids);
    }
    fn settled_pins(&self) -> Vec<u8> {
        self.settled.take()
    }
    fn temporary_target(&self) -> Option<bool> {
        if !cfg!(feature = "service-temporary-charge-v1") {
            return None;
        }
        match self.requested {
            Request::SetFullChargeOverride { enabled } => Some(enabled),
            _ => None,
        }
    }
    fn temporary_reserved_ids(&self) -> Option<[u8; 8]> {
        self.reserved_ids
    }
    fn temporary_fresh_ids(&self) -> bool {
        self.superseded.is_some()
    }
    fn resolve_temporary_charge(
        &self,
        source: u8,
        flags: u8,
        policy: Option<u8>,
        ids: [u8; 8],
        cancel_ids: Option<[u8; 8]>,
    ) -> io::Result<Option<openblade_domain::temporary_charge_history::TemporaryChargeRecord>> {
        use openblade_domain::temporary_charge_history::{CONTRACT, TemporaryChargeRecord};
        self.authorize().ok_or(io::ErrorKind::PermissionDenied)?;
        self.source.store(source, Ordering::Release);
        let configured = self.storage.desired_battery(source)?;
        let Request::SetFullChargeOverride { enabled } = self.requested else {
            return Err(io::ErrorKind::PermissionDenied.into());
        };
        if flags & !4 != 0
            || enabled
                && (source == 1
                    || !configured.protection
                    || !policy
                        .is_some_and(|p| p != 0x50 && openblade_protocol::battery::valid_policy(p)))
            || self.origin.is_some() && source != 1
        {
            self.reason("Temporary charge admission or the complete Power Mode Control guard refused the request; no setter was submitted.");
            return Ok(None);
        }
        let record = TemporaryChargeRecord {
            contract: CONTRACT.into(),
            requested: self.requested.clone(),
            configured,
            desired: configured.requested(&self.requested),
            source,
            baseline: flags,
            target: if enabled { 4 } else { 0 },
            policy,
            ids,
            cancel_ids,
            superseded_ids: self.superseded,
            origin: self.origin.clone(),
            fan_owner: self.storage.retained_fan_binding()?,
        };
        record.validate()?;
        *self.tfc.lock().map_err(|_| io::ErrorKind::Other)? = Some(record.clone());
        Ok(Some(record))
    }
    fn is_battery(&self) -> bool {
        true
    }
    fn authorize(&self) -> Option<ReadAdmission> {
        verify_retained_failure(&self.fatal).ok()?;
        self.fresh
            .verify_health()
            .inspect_err(|e| remember_failure(&self.fatal, e))
            .ok()?;
        if let Some(record) = self.record.lock().ok()?.as_ref() {
            let expected = if self.confirmed.load(Ordering::Acquire) {
                record.desired
            } else {
                record.configured
            };
            if self.storage.desired_battery(record.source).ok()? != expected {
                return None;
            }
        }
        if let Some(record) = self.tfc.lock().ok()?.as_ref() {
            let current = self.storage.desired_battery(record.source).ok()?;
            if current.limit != record.configured.limit
                || current.protection != record.configured.protection
            {
                return None;
            }
        }
        if self.origin.is_some() && self.fresh.on_battery() != Some(true) {
            return None;
        }
        if cfg!(feature = "service-temporary-charge-v1")
            && matches!(
                self.requested,
                Request::SetFullChargeOverride { enabled: true }
            )
            && self.fresh.on_battery() != Some(false)
        {
            return None;
        }
        let p = self.fresh.authorize();
        (p == Some(self.proof)).then_some(self.proof)
    }
    fn resolve(&self, _: i32, _: ObservedPerformance) -> io::Result<Option<Destination>> {
        Ok(None)
    }
    fn record(&self, _: Progress) -> io::Result<()> {
        Err(io::ErrorKind::PermissionDenied.into())
    }
    fn resolve_battery(
        &self,
        source: u8,
        baseline: u8,
        temporary: Option<bool>,
        ids: [u8; 14],
    ) -> io::Result<Option<BatteryRecord>> {
        self.authorize().ok_or(io::ErrorKind::PermissionDenied)?;
        self.source.store(source, Ordering::Release);
        let configured = self.storage.desired_battery(source)?;
        if matches!(self.requested, Request::SetFullChargeOverride { .. }) {
            // The request is retained on this original permit until runtime response;
            // no temporary-full-charge command or hardware support claim is invented.

            self.reason("Temporary full charge is supported by this model; native enable/cancel integration is pending. The requested change was not accepted; existing desired intent and independent readings are retained.");
            return Ok(None);
        }
        if temporary != Some(false) || configured.temporary {
            self.reason("Temporary full charge is active or unknown; independent readback is required before policy changes.");
            return Ok(None);
        }
        // A newly supplied limit must be an admitted choice even while Off;
        // generic stored provenance is not permission for a new hardware target.
        if matches!(&self.requested, Request::SetBatteryPolicy { value }
            if !openblade_protocol::battery::admitted_limit(value.charge_limit_percent))
        {
            self.reason("The requested limit is outside exact0581 admitted values.");
            return Ok(None);
        }
        let desired = configured.requested(&self.requested);
        let Some(target) = desired.raw() else {
            self.reason("The configured limit is outside exact0581 admitted values.");
            return Ok(None);
        };
        let record = BatteryRecord {
            contract: CONTRACT.into(),
            requested: self.requested.clone(),
            configured,
            desired,
            source,
            baseline,
            target,
            temporary: false,
            ids,
            fan_owner: self.storage.retained_fan_binding()?,
        };
        record.validate()?;
        *self.record.lock().map_err(|_| io::ErrorKind::Other)? = Some(record.clone());
        Ok(Some(record))
    }
    fn record_battery(&self, progress: u8) -> io::Result<()> {
        self.authorize().ok_or(io::ErrorKind::PermissionDenied)?;
        self.write(progress)?;
        if progress == 0 {
            self.prepared.store(true, Ordering::Release);
        }
        if matches!(progress, 62 | 63 | 72 | 73) {
            let basic = self
                .record
                .lock()
                .map_err(|_| io::ErrorKind::Other)?
                .clone();
            let tfc = self.tfc.lock().map_err(|_| io::ErrorKind::Other)?.clone();
            let (source, desired) = match (basic, tfc) {
                (Some(r), None) => (r.source, r.desired),
                (None, Some(r)) => (r.source, r.desired),
                _ => return Err(io::ErrorKind::PermissionDenied.into()),
            };
            if let Err(e) = self.storage.commit_battery(source, desired) {
                self.storage.quarantine_settlement()?;
                return Err(e);
            }
            if matches!(progress, 72 | 73) && desired.temporary {
                *self.positive_at.lock().map_err(|_| io::ErrorKind::Other)? = Some(Instant::now());
            }
            self.confirmed.store(true, Ordering::Release);
        }
        Ok(())
    }
    fn observe_battery_policy(&self, raw: u8) {
        if let Ok(mut v) = self.observation.lock() {
            v.policy = Some((raw, Instant::now()));
        }
    }
    fn observe_battery_temporary(&self, temporary: bool) {
        if !temporary {
            self.storage.clear_battery_temporary();
        }
        if let Ok(mut v) = self.observation.lock() {
            v.temporary = Some((temporary, Instant::now()));
        }
    }
    fn failed(&self, e: &io::Error) {
        remember_failure(&self.fatal, e)
    }
    fn refused(&self, r: Refusal) {
        self.reason(match r {
            Refusal::TransactionCapacity => {
                "Complete battery transaction budget is unavailable; no setter was submitted."
            }
            Refusal::PowerChanged => "Battery operation source changed before submission.",
            // NoDirectPath, Deadline and ThermalPending are thermal-recipe
            // refusals; a battery permit never plans one.
            Refusal::CurrentAdmission
            | Refusal::UnclassifiedBlocked
            | Refusal::Deadline
            | Refusal::ThermalPending => "Fresh battery admission is unavailable.",
            #[cfg(not(feature = "service-thermal-hyperboost-exit-v1"))]
            Refusal::NoDirectPath => "Fresh battery admission is unavailable.",
        });
    }
    fn retired(&self) -> io::Result<Settlement> {
        self.joined.store(true, Ordering::Release);
        verify_retained_failure(&self.fatal)?;
        self.fresh.verify_health()?;
        if self.prepared.load(Ordering::Acquire)
            && self.storage.progress() == Some(0)
            && let Err(e) = self.write(6)
        {
            self.storage.quarantine_settlement()?;
            return Ok(Settlement::Unresolved(e));
        }
        Ok(Settlement::Complete)
    }
}
#[cfg(feature = "service-temporary-charge-v1")]
fn source_request_id(creator: &str, epoch: u64) -> String {
    // Domain-separated service event identity; creator PID/request remain
    // original provenance, never fabricated as a new authenticated pipe caller.
    let h = format!(
        "{:x}",
        Sha256::digest(
            format!("OpenBlade.TemporaryChargeSource.v1\n{creator}\n{epoch}\n").as_bytes()
        )
    );
    format!(
        "{}-{}-{}-{}-{}",
        &h[..8],
        &h[8..12],
        &h[12..16],
        &h[16..20],
        &h[20..32]
    )
}
impl Runtime {
    fn retain_battery_view(
        &self,
        reader: &super::super::mailbox_read::ReadSource,
        retained: Arc<BatteryPermit>,
    ) {
        let history = self.battery_observation.clone();
        reader.retain_battery_expiry(Rc::new(move || {
            history
                .lock()
                .ok()
                .and_then(|h| h.next_expiry(Instant::now()))
        }));
        let warning = self.battery_warning.clone();
        let release_deferred = self.release_deferred.clone();
        reader.retain_battery_policy(Rc::new(move |base| {
            verify_retained_failure(&retained.fatal)?;
            retained.fresh.verify_health()?;
            let base = base.without_battery_observation();
            let source_warning = warning.lock().map_err(|_| io::ErrorKind::Other)?.clone();
            let source_warning = if let Some(cause) = release_deferred.get() {
                Some(cause.compose(source_warning.as_deref().unwrap_or_default()))
            } else { source_warning };
            if retained.fresh.authorize() != Some(retained.proof) {
                return Ok(source_warning.as_ref().map_or(base.clone(), |message| base.battery_projection(None,None,message)));
            }
            let current = base.thermal_power_source();
            let source = if (0..=2).contains(&current) { current as u8 } else { retained.source.load(Ordering::Acquire) };
            if source > 2 { return Ok(base); }
            let observation = retained.historical_observation()?;
            let message = if retained.storage.pending() { retained.storage.pending_reason()?.into() }
                else if let Some(message) = source_warning { message }
                else if observation.is_some() { "Last independently read battery policy retained; no fresh battery read is confirmed. New writes require fresh admission.".into() }
                else { "Last battery observation expired or unavailable; new writes require fresh admission.".into() };
            let public = base.battery_projection(None,observation,&message);
            super::super::mailbox_read::project_private_policy(public,crate::service_status::PrivatePolicyFamily::Battery, |base| {
                Ok(base.battery_projection(Some(retained.storage.desired_battery(source)?),None,"").bind_private_user(&retained.sid))
            })
        }));
    }

    // Called only after the actual shared worker joins. A vanished pipe future
    // cannot discard the worker's independently confirmed process intent.
    pub(super) fn reconcile_temporary_owner(&self) -> io::Result<()> {
        // Desired memory may clear on Battery before its event worker runs.
        // A newer independent false ends the positive obligation. A consumed
        // automatic attempt may retain release-only bookkeeping after I/O error.
        let ended = self.temporary_owner.borrow().as_ref().is_some_and(|p| {
            p.abandoned.load(Ordering::Acquire) || self.read_off_since_positive(p)
        });
        if ended {
            let old = self.temporary_owner.borrow_mut().take();
            if let Some(old) = old {
                old.abandoned.store(true, Ordering::Release);
                self.finish_temporary_release(old)?;
            }
        }
        let pending = self.temporary_pending.borrow_mut().take();
        if let Some(p) = pending {
            if !p.joined.load(Ordering::Acquire) {
                *self.temporary_pending.borrow_mut() = Some(p);
                return Ok(());
            }
            if p.confirmed.load(Ordering::Acquire) && !p.storage.pending() {
                // The newer confirmed row is the journal's temporary owner
                // now, so no source cancel can be admitted for the one it
                // replaces.
                let replaced = self.temporary_owner.borrow_mut().replace(p);
                if let Some(replaced) = replaced {
                    replaced.settle_pins();
                }
            } else if p.storage.pending() {
                *self.temporary_pending.borrow_mut() = Some(p);
            } else {
                // Joined and settled without a confirmation: it owns nothing.
                p.settle_pins();
            }
        }
        Ok(())
    }
    /// Whether an independent read found the charge off at or after the
    /// owner's confirmed positive.
    fn read_off_since_positive(&self, p: &BatteryPermit) -> bool {
        let at = p.positive_at.lock().ok().and_then(|at| *at);
        self.battery_observation.lock().is_ok_and(|o| {
            o.temporary
                .is_some_and(|(value, observed)| !value && at.is_some_and(|at| observed >= at))
        })
    }
    fn release_temporary_reservation(
        &self,
        original: &BatteryPermit,
    ) -> io::Result<super::super::thermal_storage::TemporaryRelease> {
        // A live/deferred operation must keep its reservation. Storage also
        // independently requires exact origin identity and settled durable bytes.
        if !original.joined.load(Ordering::Acquire)
            || self
                .temporary_pending
                .borrow()
                .iter()
                .chain(self.temporary_owner.borrow().iter())
                .any(|p| {
                    p.request == original.request && Arc::ptr_eq(&p.storage, &original.storage)
                })
        {
            return Ok(super::super::thermal_storage::TemporaryRelease::Deferred(
                super::super::thermal_storage::ReleaseDeferred::busy(),
            ));
        }
        let record = original
            .tfc
            .lock()
            .map_err(|_| io::ErrorKind::Other)?
            .clone()
            .ok_or_else(|| {
                fatal_admission("Confirmed temporary-charge origin lost its typed record.")
            })?;
        #[cfg(test)]
        if let Some(hook) = self.release_hook.borrow().clone() {
            hook();
        }
        original.storage.release_temporary_capacity(
            &openblade_domain::temporary_charge_history::Origin {
                request: original.request.clone(),
                intent: record.hash()?,
            },
        )
    }
    fn finish_temporary_release(&self, original: Arc<BatteryPermit>) -> io::Result<()> {
        use super::super::thermal_storage::TemporaryRelease;
        match self.release_temporary_reservation(&original) {
            Ok(TemporaryRelease::Released | TemporaryRelease::NoMatchingReservation) => {
                // Out of every slot, with any claim on it decided and its
                // cancel joined (`BatteryPermit::settle_pins`).
                original.settle_pins();
                self.release_presentation(&original, None)
            }
            Ok(TemporaryRelease::Deferred(reason)) => {
                *self.temporary_owner.borrow_mut() = Some(original.clone());
                self.release_presentation(&original, Some(reason.cause))?;
                Err(reason.into_error())
            }
            Err(error) => {
                *self.temporary_owner.borrow_mut() = Some(original);
                Err(error)
            }
        }
    }
    fn release_presentation(
        &self,
        original: &Arc<BatteryPermit>,
        cause: Option<super::super::thermal_storage::ReleasePause>,
    ) -> io::Result<()> {
        if self.release_deferred.replace(cause) != cause {
            *self.release_notice.borrow_mut() = Some((original.clone(), cause));
        }
        Ok(())
    }
    pub(in super::super) fn take_release_notice(&self) -> io::Result<Option<BatteryUpdate>> {
        let Some((original, deferred)) = self.release_notice.borrow_mut().take() else {
            return Ok(None);
        };
        // Resolve observation age and diagnostics at publication, not at the
        // earlier release check. This public delta never rebinds private state.
        let observation = original.historical_observation()?;
        let warning = self
            .battery_warning
            .lock()
            .map_err(|_| fatal_admission("Battery warning state is poisoned."))?
            .clone();
        let mut message = warning.unwrap_or_else(|| {
            if observation.is_some() {
                "Independent battery observation retained; new writes require fresh admission."
                    .into()
            } else {
                "Battery observation is unavailable; new writes require fresh admission.".into()
            }
        });
        if let Some(cause) = deferred {
            message = cause.compose(&message);
        }
        Ok(Some(BatteryUpdate {
            desired: None,
            observation,
            public_message: message.clone(),
            message,
            sid: original.sid.clone(),
            public_only: true,
        }))
    }
    #[cfg(all(test, feature = "service-temporary-charge-v1"))]
    pub(in super::super) fn release_hook_for_tests(&self, hook: Rc<dyn Fn()>) {
        *self.release_hook.borrow_mut() = Some(hook);
    }
    /// Whether an owner holds the charge and no claim has taken it.
    #[cfg(all(test, feature = "service-temporary-charge-v1"))]
    pub(in super::super) fn temporary_owned_for_tests(&self) -> bool {
        self.temporary_owner
            .borrow()
            .as_ref()
            .is_some_and(|p| !p.abandoned.load(Ordering::Acquire))
    }
    #[cfg(all(test, feature = "service-temporary-charge-v1"))]
    pub(in super::super) fn release_only_for_tests(&self) -> bool {
        self.temporary_owner
            .borrow()
            .as_ref()
            .is_some_and(|p| p.abandoned.load(Ordering::Acquire))
    }
    #[cfg(feature = "service-temporary-charge-v1")]
    pub(in super::super) fn temporary_event_needed(&self, epoch: u64) -> io::Result<bool> {
        self.reconcile_temporary_owner()?;
        // A current-epoch prospective request cannot hide an older confirmed
        // obligation. Selection is deferred until the real Reader has drained.
        Ok(self
            .temporary_pending
            .borrow()
            .iter()
            .chain(self.temporary_owner.borrow().iter())
            .any(|p| p.proof.lifecycle != epoch && self.temporary_event.get() != Some(epoch)))
    }
    #[cfg(feature = "service-temporary-charge-v1")]
    pub(in super::super) fn temporary_event_observed(&self, epoch: u64) {
        self.temporary_event.set(Some(epoch));
    }
    pub(super) fn temporary_cross_user(&self, sid: &str, session: u32) -> bool {
        self.temporary_pending
            .borrow()
            .iter()
            .chain(self.temporary_owner.borrow().iter())
            .any(|p| p.sid != sid || p.session != session)
    }
    pub(super) fn temporary_cancel_recovery(
        &self,
        sid: &str,
        custody: &Arc<dyn ReadCustody>,
    ) -> Option<Arc<Storage>> {
        if self.temporary_pending.borrow().is_some() {
            return None;
        }
        self.temporary_owner
            .borrow()
            .as_ref()
            .filter(|p| {
                p.sid == sid
                    && p.joined.load(Ordering::Acquire)
                    && !p.storage.pending()
                    && Arc::ptr_eq(&p.custody, custody)
            })
            .map(|p| p.storage.clone())
    }
    #[cfg(feature = "service-temporary-charge-v1")]
    pub(in super::super) async fn battery_source_event(
        &self,
        mailbox: MailboxSource,
        epoch: u64,
        current_epoch: &dyn Fn() -> Option<u64>,
    ) -> io::Result<Option<BatteryUpdate>> {
        let _serial = self.gate.lock().await;
        verify_retained_failure(&self.fatal)?;
        let MailboxSource::Reader(reader) = mailbox else {
            return Ok(None);
        };
        reader.drain_active().await?;
        self.reconcile_temporary_owner()?;
        // Every guard pins the epoch it captured and refuses once it moves, so
        // a refusal while a later notice holds the epoch (or low power holds
        // none) is that notice's fence, not a refusal of this evidence.
        let fenced = || current_epoch() != Some(epoch);
        let unresolved = self.temporary_pending.borrow().clone();
        // Do not consume either reference or fall back to A after a possibly
        // submitted B. Its actual worker/storage fence remains authoritative.
        // Peek at the owner: nothing is claimed before the admission below.
        let original = unresolved
            .clone()
            .or_else(|| self.temporary_owner.borrow().clone());
        let Some(original) = original else {
            return Ok(None);
        };
        let admission = if unresolved.is_none()
            && !original.storage.pending()
            && original.confirmed.load(Ordering::Acquire)
        {
            self.admit_source_cancel(&original, &reader.custody(), epoch, &fenced)?
        } else {
            SourceCancel::Refused
        };
        if matches!(admission, SourceCancel::Superseded) {
            // A notice fenced this tick after it read its epoch, so this
            // Battery evidence is not current. Claim nothing: the newer epoch
            // makes the next tick read the source again and retry the cancel.
            return Ok(None);
        }
        // Genuine current Battery evidence always ends desired intent, even if
        // configuration admission fails. The obligation is claimed once, and
        // stays claimed even if later bookkeeping fails; only an attempt
        // proven to have sent nothing gives it back for the next tick.
        let mut claim = if unresolved.is_none() {
            // The gate has been held and the Reader drained since the peek, so
            // the capture's own reconcile saw the same owner. Fail closed if
            // it did not: take nothing and write nothing.
            let Some(claim) = TemporaryClaim::take(&self.temporary_owner, &original) else {
                return Ok(None);
            };
            Some(claim)
        } else {
            original.storage.clear_battery_temporary();
            None
        };
        let mut message = "Temporary charge intent cleared on Battery; cancellation is paused because original authority or independent confirmation is unavailable.".to_owned();
        let mut attempted = None;
        if let SourceCancel::Admitted {
            record,
            storage,
            fresh,
            proof,
        } = admission
        {
            // Once an attempt sent the reserved IDs, which may still be
            // answered, every later attempt draws fresh ones instead.
            let spent = original.cancel_spent.load(Ordering::Acquire);
            let permit = Arc::new(BatteryPermit {
                tfc: Mutex::new(None),
                // The only permit that carries reserved IDs. The fenced
                // rollback trusts `Transactions::unsent` on these pinned IDs,
                // so no other path may send them.
                reserved_ids: record.cancel_ids.filter(|_| !spent),
                superseded: record.cancel_ids.filter(|_| spent),
                cancel_spent: AtomicBool::new(false),
                unsent_retries: std::sync::atomic::AtomicU8::new(0),
                origin: Some(openblade_domain::temporary_charge_history::Origin {
                    request: original.request.clone(),
                    intent: record.hash()?,
                }),
                authentication: original.authentication,
                custody: original.custody.clone(),
                sid: original.sid.clone(),
                storage,
                fresh,
                request: source_request_id(&original.request, epoch),
                requested: Request::SetFullChargeOverride { enabled: false },
                owner: original.owner.clone(),
                caller_pid: original.caller_pid,
                session: original.session,
                proof,
                record: Mutex::new(None),
                pins: RecordPins::default(),
                settled: self.settled_pins.clone(),
                observation: self.battery_observation.clone(),
                prepared: AtomicBool::new(false),
                confirmed: AtomicBool::new(false),
                joined: AtomicBool::new(false),
                registered: AtomicBool::new(false),
                unsent: AtomicBool::new(true),
                abandoned: AtomicBool::new(false),
                positive_at: Mutex::new(None),
                source: self.battery_source.clone(),
                cause: Mutex::new(message.clone()),
                fatal: self.fatal.clone(),
            });
            self.retain_battery_view(&reader, permit.clone());
            let awaiting = BatteryAwaitGuard(permit.clone());
            let result = reader
                .mutate(
                    permit.clone(),
                    Instant::now() + std::time::Duration::from_secs(4),
                )
                .await;
            drop(awaiting);
            attempted = Some(permit.clone());
            verify_retained_failure(&self.fatal)?;
            if let Err(e) = result
                && is_fatal_admission(&e)
            {
                return Err(e);
            }
            // Only this attempt sends its fresh IDs, so they settle once it
            // joined; an unjoined attempt keeps them pinned.
            if permit.joined.load(Ordering::Acquire) {
                permit.settle_pins();
            }
            // An attempt that sent nothing at this tick's own epoch while its
            // admission still holds, such as one whose write port failed to
            // open, gives the claim back too, a bounded number of times:
            // nothing it did can be answered, and the next tick reads the
            // source again and retries. A refused admission still pauses
            // once, and so does the cancel once the retries are spent.
            let fence = fenced();
            let unsent = !fence
                && permit.known_unsubmitted()
                && permit.fresh.authorize() == Some(permit.proof)
                && original.unsent_retries.fetch_add(1, Ordering::AcqRel) < UNSENT_CANCEL_RETRIES;
            if (fence || unsent)
                && let Some(claim) = claim.take()
                && if permit.known_unsubmitted() {
                    claim.roll_back(self)
                } else {
                    // It sent IDs that may still be answered. Retry once with
                    // fresh ones if it used the reserved IDs, joined and
                    // submitted no setter; a fresh-ID attempt that sent
                    // pauses.
                    permit.reserved_ids.is_some()
                        && permit.joined.load(Ordering::Acquire)
                        && !permit.confirmed.load(Ordering::Acquire)
                        && claim.roll_back_spent(self, &permit)
                }
            {
                // Managed writes this cancel in the same transition; here the
                // newer epoch makes the next tick read the source and retry. At
                // an unmoved epoch, the tick's coalescing mark is cleared.
                if unsent {
                    self.temporary_event.set(None);
                }
                return Ok(None);
            }
            if permit.confirmed.load(Ordering::Acquire) && !permit.storage.pending() {
                message = "Temporary full charge canceled on Battery by independent readback; configured charge policy is retained.".into();
            }
        }
        if original.storage.pending() {
            message = original.storage.pending_reason()?.into();
        }
        *self
            .battery_warning
            .lock()
            .map_err(|_| io::ErrorKind::Other)? = Some(message.clone());
        if unresolved.is_none()
            && attempted
                .as_ref()
                .is_none_or(|p| p.joined.load(Ordering::Acquire))
            && let Err(error) = self.finish_temporary_release(original.clone())
            && !super::super::thermal_storage::is_release_deferred(&error)
        {
            return Err(error);
        }
        *self
            .battery_warning
            .lock()
            .map_err(|_| io::ErrorKind::Other)? = Some(message.clone());
        let desired = original.storage.desired_battery(1).ok();
        Ok(Some(BatteryUpdate {
            public_only: false,
            desired,
            observation: original.historical_observation()?,
            public_message: self
                .release_deferred
                .get()
                .map_or_else(|| message.clone(), |cause| cause.compose(&message)),
            message,
            sid: original.sid.clone(),
        }))
    }
    /// The single capture and authorization of an automatic source cancel,
    /// taken before the caller claims the obligation. It claims nothing: on
    /// `Superseded` the caller returns, and the owner stays for the next tick.
    #[cfg(feature = "service-temporary-charge-v1")]
    fn admit_source_cancel(
        &self,
        original: &BatteryPermit,
        custody: &Arc<dyn ReadCustody>,
        epoch: u64,
        fenced: &dyn Fn() -> bool,
    ) -> io::Result<SourceCancel> {
        let record = original
            .tfc
            .lock()
            .map_err(|_| io::ErrorKind::Other)?
            .clone();
        let Some(record) = record.filter(|r| r.desired.temporary) else {
            return Ok(SourceCancel::Refused);
        };
        let (storage, fresh) = match self.capture(&original.sid, original.session, custody.clone())
        {
            Err(e) if is_fatal_admission(&e) => return Err(e),
            Err(_) if fenced() => return Ok(SourceCancel::Superseded),
            Err(_) => return Ok(SourceCancel::Refused),
            Ok(captured) => captured,
        };
        fresh.verify_health()?;
        let Some(proof) = fresh.authorize() else {
            return Ok(if fenced() {
                SourceCancel::Superseded
            } else {
                SourceCancel::Refused
            });
        };
        // The epoch only advances, so a different one is a later notice.
        if proof.lifecycle != epoch {
            return Ok(SourceCancel::Superseded);
        }
        if proof.generation != original.proof.generation
            || !Arc::ptr_eq(&storage, &original.storage)
            || !Arc::ptr_eq(&original.custody, custody)
            || original.authentication.is_none()
            || fresh.authentication() != original.authentication
        {
            return Ok(SourceCancel::Refused);
        }
        Ok(SourceCancel::Admitted {
            record,
            storage,
            fresh,
            proof,
        })
    }
    pub(in super::super) async fn apply_battery(
        &self,
        mailbox: MailboxSource,
        caller: Caller,
        sid: String,
        request: String,
        requested: Request,
        deadline: Instant,
    ) -> io::Result<(Outcome, Option<BatteryUpdate>)> {
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
                Outcome::blocked("Authenticated battery custody is unavailable."),
                None,
            ));
        };
        reader.drain_active().await?;
        if let Err(error) = self.reconcile_temporary_owner() {
            if super::super::thermal_storage::is_release_deferred(&error) {
                return Ok((
                    Outcome::blocked(error.to_string()),
                    self.take_release_notice()?,
                ));
            }
            return Err(error);
        }
        reader.custody().verify_integrity()?;
        let captured = if cfg!(feature = "service-temporary-charge-v1")
            && matches!(requested, Request::SetFullChargeOverride { enabled: false })
        {
            self.capture_temporary_cancel(&sid, caller.session, reader.custody())
        } else {
            self.capture(&sid, caller.session, reader.custody())
        };
        let (storage, fresh) = match captured {
            Ok(v) => v,
            Err(e) if is_fatal_admission(&e) => return Err(e),
            Err(e) => {
                return Ok((
                    Outcome::blocked(format!("Battery configuration admission failed: {e}")),
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
                Outcome::blocked("Fresh battery admission is unavailable."),
                None,
            ));
        };
        #[cfg(feature = "service-temporary-charge-v1")]
        let authentication = fresh.authentication();
        let permit = Arc::new(BatteryPermit {
            tfc: Mutex::new(None),
            reserved_ids: None,
            superseded: None,
            #[cfg(feature = "service-temporary-charge-v1")]
            cancel_spent: AtomicBool::new(false),
            #[cfg(feature = "service-temporary-charge-v1")]
            unsent_retries: std::sync::atomic::AtomicU8::new(0),
            origin: None,
            #[cfg(feature = "service-temporary-charge-v1")]
            authentication,
            custody: reader.custody().clone(),
            sid: sid.clone(),
            storage,
            fresh,
            request,
            requested,
            owner: format!("{:x}", Sha256::digest(proof.generation)),
            caller_pid: caller.pid.unwrap(),
            session: caller.session,
            proof,
            record: Mutex::new(None),
            pins: RecordPins::default(),
            settled: self.settled_pins.clone(),
            observation: self.battery_observation.clone(),
            prepared: AtomicBool::new(false),
            confirmed: AtomicBool::new(false),
            joined: AtomicBool::new(false),
            registered: AtomicBool::new(false),
            unsent: AtomicBool::new(true),
            abandoned: AtomicBool::new(false),
            positive_at: Mutex::new(None),
            source: self.battery_source.clone(),
            cause: Mutex::new("Battery operation was not confirmed; no success is claimed.".into()),
            fatal: self.fatal.clone(),
        });
        self.retain_battery_view(&reader, permit.clone());
        if cfg!(feature = "service-temporary-charge-v1")
            && matches!(
                permit.requested,
                Request::SetFullChargeOverride { enabled: true }
            )
        {
            // Register prospectively; the worker may commit after caller disconnect.
            *self.temporary_pending.borrow_mut() = Some(permit.clone());
        }
        let awaiting = BatteryAwaitGuard(permit.clone());
        let result = reader.mutate(permit.clone(), deadline).await;
        drop(awaiting);
        verify_retained_failure(&self.fatal)?;
        permit.fresh.verify_health()?;
        if let Err(e) = &result {
            if is_fatal_admission(e) {
                return Err(io::Error::other(e.to_string()));
            }
            permit.reason(&format!("Battery transport or settlement failed: {e}"));
        }
        // The worker's outcome is already established. Only subsequent typed
        // capacity-release deferral is presentation/bookkeeping, not a failure
        // of this operation. Fatal/unclassified errors still propagate.
        if let Err(error) = self.reconcile_temporary_owner()
            && !super::super::thermal_storage::is_release_deferred(&error)
        {
            return Err(error);
        }
        // Battery uses a typed side channel; the thermal Option result is intentionally
        // empty. Check original live authority and deadline independently here.
        if Instant::now() >= deadline || permit.authorize() != Some(proof) {
            return Ok((
                Outcome::blocked(
                    "Battery result lost original caller/owner admission; durable evidence remains retained.",
                ),
                None,
            ));
        }
        let confirmed =
            result.is_ok() && permit.confirmed.load(Ordering::Acquire) && !permit.storage.pending();
        if confirmed {
            *self
                .battery_warning
                .lock()
                .map_err(|_| io::ErrorKind::Other)? = None;
        }
        let outcome = if permit.storage.pending() {
            Outcome::blocked(permit.storage.pending_reason()?)
        } else if confirmed {
            Outcome{status:1,message:"Battery operation confirmed by independent readback; saved charge policy is retained.".into()}
        } else {
            Outcome::blocked(
                permit
                    .cause
                    .lock()
                    .map_err(|_| io::ErrorKind::Other)?
                    .clone(),
            )
        };
        let source = permit.source.load(Ordering::Acquire);
        let desired = if source < 3 {
            Some(permit.storage.desired_battery(source)?)
        } else {
            None
        };
        let observation = permit.historical_observation()?;
        let public_message: String = if permit.storage.pending() {
            permit.storage.pending_reason()?.into()
        } else if observation.is_some() {
            "Independent battery policy observation retained; new writes require fresh admission."
                .into()
        } else {
            "Last battery observation expired or unavailable; new writes require fresh admission."
                .into()
        };
        let public_message = self.release_deferred.get().map_or_else(
            || public_message.clone(),
            |cause| cause.compose(&public_message),
        );
        let update = BatteryUpdate {
            public_only: false,
            desired,
            observation,
            public_message,
            message: outcome.message.clone(),
            sid,
        };
        Ok((outcome, Some(update)))
    }
}

// A battery-only delta; no pre-gate thermal snapshot or witness crosses the await.
pub(in super::super) struct BatteryUpdate {
    public_only: bool,
    desired: Option<openblade_domain::battery_history::Desired>,
    observation: Option<(u8, Option<bool>)>,
    public_message: String,
    message: String,
    sid: String,
}
impl BatteryUpdate {
    pub(in super::super) fn apply(self, latest: Snapshot) -> Snapshot {
        if self.public_only {
            return latest
                .without_battery_observation()
                .battery_public_projection(self.observation, &self.public_message);
        }
        // A present desired value was independently reconstructed after the
        // worker; only this family\'s previous private degradation is resolved.
        let latest = if self.desired.is_some() {
            latest.private_policy_reconciling(crate::service_status::PrivatePolicyFamily::Battery)
        } else {
            latest
        };
        let public = latest.without_battery_observation().battery_projection(
            self.desired,
            self.observation,
            &self.public_message,
        );
        public
            .bind_private_user(&self.sid)
            .battery_request_outcome(&self.sid, &self.message)
    }
}

#[cfg(test)]
mod observation_tests {
    use super::*;
    // These snapshots are constructed in-process, without a peer or publisher.
    fn emit_synthetic_fixture(raw: &[u8]) {
        println!(
            "OPENBLADE_FIXTURE_FRAME {}",
            serde_json::json!({
                "schemaVersion": 1,
                "emitter": "synthetic_projection",
                "operation": "syntheticProjection",
                "method": "statusEvent",
                "raw": std::str::from_utf8(raw).unwrap(),
            })
        );
    }
    #[test]
    fn tfc_release_public_notice_preserves_private_owner_and_each_family_degradation() {
        use crate::service_status::PrivatePolicyFamily::{Battery, Thermal};
        let old = "S-1-5-21-111-222-333-1001";
        let current = "S-1-5-21-111-222-333-1002";
        let base =
            Snapshot::observed_read_only(1, 0, false, false, vec![], None, "Public source.".into())
                .private_policy_unavailable(Thermal)
                .private_policy_unavailable(Battery)
                .bind_private_user(current)
                .battery_request_outcome(current, "CURRENT_USER_PRIVATE_SENTINEL");
        let update = BatteryUpdate {
            public_only: true,
            desired: None,
            observation: None,
            public_message: super::super::super::thermal_storage::RELEASE_PAUSED.into(),
            message: String::new(),
            sid: old.into(),
        };
        let after = update.apply(base);
        for (sid, present) in [(Some(current), true), (Some(old), false), (None, false)] {
            let view = after.project_private(sid);
            let value = serde_json::to_value(&view).unwrap();
            assert_eq!(
                value["message"]
                    .as_str()
                    .unwrap()
                    .contains("CURRENT_USER_PRIVATE_SENTINEL"),
                present
            );
            let bytes = crate::service_status::encode_status_event(&view).unwrap();
            emit_synthetic_fixture(&bytes);
        }
        for remaining in [
            after.clone().private_policy_reconciling(Thermal),
            after.clone().private_policy_reconciling(Battery),
        ] {
            assert!(
                serde_json::to_value(remaining).unwrap()["message"]
                    .as_str()
                    .unwrap()
                    .contains("private policy is unavailable")
            );
        }
    }
    #[test]
    fn independent_fields_keep_original_age_and_exclude_expired_deadlines() {
        let at = Instant::now();
        let window = super::super::super::mailbox_read::RETENTION_WINDOW;
        let mut history = BatteryObservation {
            policy: Some((0xc6, at)),
            temporary: Some((true, at + std::time::Duration::from_secs(1))),
        };
        assert_eq!(history.next_expiry(at), Some(at + window));
        assert_eq!(history.projection(at + window), None);
        assert_eq!(
            history.temporary,
            Some((true, at + std::time::Duration::from_secs(1)))
        );
        assert_eq!(
            history.next_expiry(at + window),
            Some(at + window + std::time::Duration::from_secs(1))
        );
        history.policy = Some((0x50, at + window));
        assert_eq!(
            history.projection(at + window),
            None,
            "Off plus independently retained true is incompatible"
        );
        history.policy = Some((0xc6, at + window));
        assert_eq!(history.projection(at + window), Some((0xc6, Some(true))));
        assert_eq!(
            history.projection(at + window + std::time::Duration::from_secs(1)),
            Some((0xc6, None))
        );
        assert_eq!(
            history.next_expiry(at + window * 2),
            None,
            "expired deadlines must not spin"
        );
    }
    #[test]
    fn completed_deltas_reconcile_only_their_proven_private_family() {
        use crate::service_status::PrivatePolicyFamily::{Battery, Thermal};
        let base =
            Snapshot::observed_read_only(1, 0, false, false, vec![], None, "Public source.".into())
                .private_policy_unavailable(Thermal)
                .private_policy_unavailable(Battery);
        let update = BatteryUpdate {
            public_only: false,
            desired: Some(openblade_domain::battery_history::Desired {
                limit: 80,
                protection: false,
                temporary: false,
            }),
            observation: Some((0x50, None)),
            public_message: "Public battery observation.".into(),
            message: "Battery confirmed.".into(),
            sid: "S-1-5-21-111-222-333-1001".into(),
        };
        let battery = update.apply(base.clone());
        assert!(
            serde_json::to_value(&battery).unwrap()["message"]
                .as_str()
                .unwrap()
                .contains("private policy is unavailable"),
            "battery success cannot clear the unrelated thermal failure"
        );
        let recovered = battery.private_policy_reconciling(Thermal);
        assert!(
            !serde_json::to_value(recovered).unwrap()["message"]
                .as_str()
                .unwrap()
                .contains("private policy is unavailable")
        );
        // Accepted fan/performance deltas have reconstructed private profiles
        // before reaching this shared production rebase.
        let delta = base
            .clone()
            .private_policy_reconciling(Battery)
            .thermal_profiles(None)
            .unwrap()
            .thermal_intent(None, None, "Thermal result.", None)
            .bind_private_user("S-1-5-21-111-222-333-1001");
        let thermal = delta.rebase_thermal_mutation(&base);
        assert!(
            serde_json::to_value(&thermal).unwrap()["message"]
                .as_str()
                .unwrap()
                .contains("private policy is unavailable"),
            "thermal success cannot clear the unrelated battery failure"
        );
        let recovered = thermal.private_policy_reconciling(Battery);
        assert!(
            !serde_json::to_value(recovered).unwrap()["message"]
                .as_str()
                .unwrap()
                .contains("private policy is unavailable")
        );
    }
    fn boundary_update(message: &str) -> BatteryUpdate {
        BatteryUpdate {
            public_only: false,
            desired: Some(openblade_domain::battery_history::Desired {
                limit: 80,
                protection: false,
                temporary: false,
            }),
            observation: Some((0x50, None)),
            public_message: "Public battery observation.".into(),
            message: message.into(),
            sid: "S-1-5-21-111-222-333-1001".into(),
        }
    }
    fn boundary_base() -> Snapshot {
        Snapshot::observed_read_only(
            1,
            0,
            false,
            false,
            vec![],
            None,
            "Public source uncertainty.".into(),
        )
        .battery_projection(None, Some((0xd0, None)), "Public battery observation.")
    }
    fn boundary_wire(snapshot: &Snapshot) -> serde_json::Value {
        let bytes = crate::service_status::encode_status_event(snapshot).unwrap();
        emit_synthetic_fixture(&bytes);
        serde_json::from_slice::<serde_json::Value>(&bytes).unwrap()["status"].clone()
    }
    #[test]
    fn battery_boundary_owned_outcome_is_absent_from_foreign_and_unbound_views() {
        let sid = "S-1-5-21-111-222-333-1001";
        let published = boundary_update("SYNTHETIC_REQUEST_OUTCOME").apply(boundary_base());
        let owner = boundary_wire(&published.project_private(Some(sid)));
        assert!(
            owner["message"]
                .as_str()
                .unwrap()
                .contains("SYNTHETIC_REQUEST_OUTCOME")
        );
        for view in [
            published.project_private(None),
            published.project_private(Some("S-1-5-21-111-222-333-1002")),
            published.inactive_mutation(),
        ] {
            let wire = boundary_wire(&view);
            let message = wire["message"].as_str().unwrap();
            assert!(!message.contains("SYNTHETIC_REQUEST_OUTCOME"), "{wire}");
            assert!(message.contains("Public source uncertainty."), "{wire}");
            assert!(message.contains("battery"), "{wire}");
            assert!(wire["performanceOverride"].is_null());
            assert_eq!(wire["writesBlocked"], true);
        }
        let public = boundary_base().bind_private_user(sid).project_private(None);
        assert!(
            boundary_wire(&public)["message"]
                .as_str()
                .unwrap()
                .contains("Public battery observation.")
        );
    }
    #[test]
    fn battery_boundary_later_binding_cannot_reown_or_revive_an_old_outcome() {
        let sid = "S-1-5-21-111-222-333-1001";
        let other = "S-1-5-21-111-222-333-1002";
        let published = boundary_update("SYNTHETIC_REQUEST_OUTCOME").apply(boundary_base());
        // This checks diagnostic ownership only, not simultaneous native users
        // or a reachable unrelated-private-field disclosure.
        for view in [
            published.clone().bind_private_user(other),
            published.project_private(None).bind_private_user(sid),
            published
                .project_private(Some(other))
                .bind_private_user(other),
        ] {
            let rebound = view.bind_private_user(sid);
            let wire = boundary_wire(&rebound);
            assert!(
                !wire["message"]
                    .as_str()
                    .unwrap()
                    .contains("SYNTHETIC_REQUEST_OUTCOME"),
                "{wire}"
            );
        }
    }
    #[test]
    fn battery_boundary_storage_reconciliation_is_not_caller_denial() {
        use crate::service_status::PrivatePolicyFamily::{Battery, Thermal};
        for family in [Battery, Thermal] {
            let owner = boundary_base().bind_private_user("S-1-5-21-111-222-333-1001");
            let unavailable = owner.private_policy_unavailable(family);
            let wire = boundary_wire(&unavailable);
            let message = wire["message"].as_str().unwrap();
            assert!(message.contains("configuration reconciliation"), "{wire}");
            assert!(!message.contains("to this caller"), "{wire}");
            assert!(message.contains("Public battery observation."), "{wire}");
            assert_eq!(wire["writesBlocked"], true);
            assert!(wire["performanceOverride"].is_null());
        }
    }
    #[test]
    fn battery_boundary_authoritative_owner_binding_clears_only_stale_denial() {
        use crate::service_status::PrivatePolicyFamily::{Battery, Thermal};
        let base = boundary_base()
            .bind_private_user("S-1-5-21-111-222-333-1001")
            .project_private(None)
            .private_policy_unavailable(Thermal)
            .private_policy_unavailable(Battery);
        let recovered = boundary_update("Current owner outcome.").apply(base);
        let wire = boundary_wire(&recovered);
        let message = wire["message"].as_str().unwrap();
        assert!(
            message.contains("configuration reconciliation"),
            "Thermal failure lost: {wire}"
        );
        assert!(message.contains("Current owner outcome."), "{wire}");
        assert!(!message.contains("to this caller"), "{wire}");
        let all_recovered = recovered.private_policy_reconciling(Thermal);
        assert!(
            !boundary_wire(&all_recovered)["message"]
                .as_str()
                .unwrap()
                .contains("configuration reconciliation")
        );
        let foreign = all_recovered.project_private(None);
        assert!(
            boundary_wire(&foreign)["message"]
                .as_str()
                .unwrap()
                .contains("to this caller")
        );
        assert!(
            !boundary_wire(&foreign)["message"]
                .as_str()
                .unwrap()
                .contains("Current owner outcome.")
        );
    }
}

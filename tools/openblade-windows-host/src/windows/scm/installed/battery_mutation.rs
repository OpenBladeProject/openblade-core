//! Exact0581 installed battery request, retaining the shared worker and owner.
use super::*;
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
        // v80 requires policy/protection when a battery group is present.
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

struct BatteryPermit {
    storage: Arc<Storage>,
    fresh: Arc<dyn Fresh>,
    request: String,
    requested: Request,
    owner: String,
    caller_pid: u32,
    session: u32,
    proof: ReadAdmission,
    record: Mutex<Option<BatteryRecord>>,
    observation: Arc<Mutex<BatteryObservation>>,
    prepared: AtomicBool,
    confirmed: AtomicBool,
    source: Arc<std::sync::atomic::AtomicU8>,
    cause: Mutex<String>,
    fatal: Arc<Mutex<Option<&'static str>>>,
}
impl BatteryPermit {
    fn write(&self, progress: u8) -> io::Result<()> {
        let record = self
            .record
            .lock()
            .map_err(|_| io::ErrorKind::Other)?
            .clone()
            .ok_or(io::ErrorKind::PermissionDenied)?;
        self.storage
            .record_battery(Entry {
                battery: Some(record.clone()),
                fan: None,
                request: self.request.clone(),
                owner: self.owner.clone(),
                user_sid: String::new(),
                caller_pid: self.caller_pid,
                session: self.session,
                requested: None,
                destination: 0,
                intent: record.hash()?,
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
}
impl Permit for BatteryPermit {
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
        let p = self.fresh.authorize();
        (p == Some(self.proof)).then_some(self.proof)
    }
    fn resolve(&self, _: i32, _: ObservedPerformance) -> io::Result<Option<Preset>> {
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
        if matches!(progress, 62 | 63) {
            let record = self
                .record
                .lock()
                .map_err(|_| io::ErrorKind::Other)?
                .clone()
                .ok_or(io::ErrorKind::PermissionDenied)?;
            if let Err(e) = self.storage.commit_battery(record.source, record.desired) {
                self.storage.quarantine_settlement()?;
                return Err(e);
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
            Refusal::CurrentAdmission => "Fresh battery admission is unavailable.",
        });
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
        reader.custody().verify_integrity()?;
        let (storage, fresh) = match self.capture(&sid, caller.session, reader.custody()) {
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
        let permit = Arc::new(BatteryPermit {
            storage,
            fresh,
            request,
            requested,
            owner: format!("{:x}", Sha256::digest(proof.generation)),
            caller_pid: caller.pid.unwrap(),
            session: caller.session,
            proof,
            record: Mutex::new(None),
            observation: self.battery_observation.clone(),
            prepared: AtomicBool::new(false),
            confirmed: AtomicBool::new(false),
            source: self.battery_source.clone(),
            cause: Mutex::new("Battery operation was not confirmed; no success is claimed.".into()),
            fatal: self.fatal.clone(),
        });
        // Register before worker submission. Retirement or caller cancellation
        // must not discard a committed process-scoped override. This callback
        // reads current storage under fresh read-only proof, never grants writes.
        let history = self.battery_observation.clone();
        reader.retain_battery_expiry(Rc::new(move || {
            history
                .lock()
                .ok()
                .and_then(|history| history.next_expiry(Instant::now()))
        }));
        let retained = permit.clone();
        let policy_sid = sid.clone();
        reader.retain_battery_policy(Rc::new(move |base| {
            verify_retained_failure(&retained.fatal)?;
            retained.fresh.verify_health()?;
            // Never inherit a battery field cached with a newer thermal witness.
            let base = base.without_battery_observation();
            if retained.fresh.authorize() != Some(proof) { return Ok(base); }
            let current = base.thermal_power_source();
            let source = if (0..=2).contains(&current) { current as u8 }
                else { retained.source.load(Ordering::Acquire) };
            if source > 2 { return Ok(base); }
            let observation = retained.historical_observation()?;
            let message = if retained.storage.pending() { retained.storage.pending_reason()? }
                else if observation.is_some() { "Last independently read battery policy retained; no fresh battery read is confirmed. New writes require fresh admission.".into() }
                else { "Last battery observation expired or unavailable; new writes require fresh admission.".into() };
            let public = base.battery_projection(None, observation, &message);
            super::super::mailbox_read::project_private_policy(public, crate::service_status::PrivatePolicyFamily::Battery, |base| {
                let desired = retained.storage.desired_battery(source)?;
                Ok(base.battery_projection(Some(desired), None, "").bind_private_user(&policy_sid))
            })
        }));
        let result = reader.mutate(permit.clone(), deadline).await;
        verify_retained_failure(&self.fatal)?;
        permit.fresh.verify_health()?;
        if let Err(e) = &result {
            if is_fatal_admission(e) {
                return Err(io::Error::other(e.to_string()));
            }
            permit.reason(&format!("Battery transport or settlement failed: {e}"));
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
        let outcome = if permit.storage.pending() {
            Outcome::blocked(permit.storage.pending_reason()?)
        } else if confirmed {
            Outcome{status:1,message:"Battery policy confirmed by independent readback; saved limit is retained when protection is Off.".into()}
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
        let public_message = if permit.storage.pending() {
            permit.storage.pending_reason()?.into()
        } else if observation.is_some() {
            "Independent battery policy observation retained; new writes require fresh admission."
                .into()
        } else {
            "Last battery observation expired or unavailable; new writes require fresh admission."
                .into()
        };
        let update = BatteryUpdate {
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
    desired: Option<openblade_domain::battery_history::Desired>,
    observation: Option<(u8, Option<bool>)>,
    public_message: String,
    message: String,
    sid: String,
}
impl BatteryUpdate {
    pub(in super::super) fn apply(self, latest: Snapshot) -> Snapshot {
        // A present desired value was independently reconstructed after the
        // worker; only this family\'s previous private degradation is resolved.
        let latest = if self.desired.is_some() {
            latest.private_policy_reconciling(crate::service_status::PrivatePolicyFamily::Battery)
        } else {
            latest
        };
        latest
            .without_battery_observation()
            .battery_projection(self.desired, self.observation, &self.public_message)
            .bind_private_user(&self.sid)
            .battery_request_outcome(&self.sid, &self.message)
    }
}

#[cfg(test)]
mod observation_tests {
    use super::*;
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
        println!(
            "NATIVE_THERMAL_MUTATION_RESPONSE {}",
            std::str::from_utf8(&bytes).unwrap()
        );
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

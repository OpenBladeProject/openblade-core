//! Exact0581 installed battery request, retaining the shared worker and owner.
use super::*;
use crate::thermal_history::Entry;
use openblade_domain::battery_history::{BatteryRecord, CONTRACT, Request};
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
    observation: Mutex<Option<(u8, Option<bool>)>>,
    prepared: AtomicBool,
    confirmed: AtomicBool,
    source: std::sync::atomic::AtomicU8,
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
    fn observe_battery(&self, raw: u8, temporary: Option<bool>) {
        if let Ok(mut v) = self.observation.lock() {
            *v = Some((raw, temporary));
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
        status: Snapshot,
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
            observation: Mutex::new(None),
            prepared: AtomicBool::new(false),
            confirmed: AtomicBool::new(false),
            source: std::sync::atomic::AtomicU8::new(3),
            cause: Mutex::new("Battery operation was not confirmed; no success is claimed.".into()),
            fatal: self.fatal.clone(),
        });
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
        let observation = *permit
            .observation
            .lock()
            .map_err(|_| io::ErrorKind::Other)?;
        let source = permit.source.load(Ordering::Acquire);
        let desired = if source < 3 {
            Some(permit.storage.desired_battery(source)?)
        } else {
            None
        };
        let snapshot = status
            .thermal_profiles(permit.storage.profiles()?)?
            .battery_projection(desired, observation, &outcome.message)
            .bind_private_user(&sid);
        // Retain a second policy decorator, preserving the existing fan/performance
        // projection and original fan cleanup provenance across battery requests.
        let retained = permit.clone();
        let policy_sid = sid.clone();
        reader.retain_battery_policy(Rc::new(move |base| {
            verify_retained_failure(&retained.fatal)?;
            let current = base.thermal_power_source();
            if retained.fresh.authorize() != Some(proof) {
                return Ok(base);
            }
            // Unknown coarse metadata does not erase a known configured override.
            // The retained source scopes intent only, never grants a fresh write.
            let source = if (0..=2).contains(&current) {
                current as u8
            } else {
                source
            };
            if source > 2 {
                return Ok(base);
            }
            let desired = retained.storage.desired_battery(source)?;
            let observation = *retained
                .observation
                .lock()
                .map_err(|_| io::ErrorKind::Other)?;
            Ok(base
                .battery_projection(
                    Some(desired),
                    observation,
                    "Last independently read battery policy; new writes require fresh admission.",
                )
                .bind_private_user(&policy_sid))
        }));
        Ok((outcome, Some(snapshot)))
    }
}

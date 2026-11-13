//! One installed request composition. No fake or scenario transport is imported.
use super::{
    Caller, Logon,
    mailbox_read::{MailboxSource, ReadCustody},
    thermal_native::{CustodialFiles, ProtectedFiles, WindowsGuard},
    thermal_storage::{Override, Storage},
};
use crate::{
    discovery::performance::{
        ObservedPerformance, ReadAdmission,
        mutation::{Permit, Refusal, Settlement},
    },
    loaded_origin::Origin,
    service_status::{ReadConfirmation, Snapshot},
};
use openblade_domain::thermal_mutation::{Preset, Progress, confirms};
use sha2::{Digest, Sha256};
use std::{
    cell::RefCell,
    io,
    rc::Rc,
    sync::{
        Arc, Mutex,
        atomic::{AtomicBool, Ordering},
    },
};
use tokio::time::Instant;

#[derive(Debug)]
struct FatalAdmission(&'static str);
impl std::fmt::Display for FatalAdmission {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(self.0)
    }
}
impl std::error::Error for FatalAdmission {}
pub(super) fn fatal_admission(message: &'static str) -> io::Error {
    io::Error::other(FatalAdmission(message))
}

pub(super) fn is_fatal_admission(error: &io::Error) -> bool {
    error
        .get_ref()
        .is_some_and(|cause| cause.is::<FatalAdmission>())
}

pub(super) fn verify_retained_failure(fatal: &Mutex<Option<&'static str>>) -> io::Result<()> {
    match *fatal
        .lock()
        .map_err(|_| fatal_admission("Thermal fatal evidence lock poisoned."))?
    {
        Some(message) => Err(fatal_admission(message)),
        None => Ok(()),
    }
}

pub(super) fn remember_failure(fatal: &Mutex<Option<&'static str>>, error: &io::Error) {
    if let Some(cause) = error
        .get_ref()
        .and_then(|cause| cause.downcast_ref::<FatalAdmission>())
    {
        if let Ok(mut retained) = fatal.lock() {
            retained.get_or_insert(cause.0);
        }
    }
}

pub(super) trait Fresh: Send + Sync {
    fn verify_health(&self) -> io::Result<()> {
        Ok(())
    }
    fn authorize(&self) -> Option<ReadAdmission>;
}
impl Fresh for WindowsGuard {
    fn verify_health(&self) -> io::Result<()> {
        self.verify_health()
    }
    fn authorize(&self) -> Option<ReadAdmission> {
        self.authorize()
    }
}
type Factory =
    dyn Fn(&str, u32, Arc<dyn ReadCustody>) -> io::Result<(Arc<Storage>, Arc<dyn Fresh>)>;
#[derive(Clone)]
pub(super) struct Runtime {
    factory: Rc<Factory>,
    application: Rc<RefCell<Option<Application>>>,
    gate: Rc<tokio::sync::Mutex<()>>,
    fatal: Arc<Mutex<Option<&'static str>>>,
    #[cfg(test)]
    pub(super) entered: Rc<std::cell::Cell<u64>>,
}
struct Application {
    sid: String,
    session: u32,
    pid: u32,
    instance: String,
    sequence: i64,
    path: Option<String>,
    expires: Instant,
}
#[derive(Clone, serde::Serialize)]
pub(super) struct Outcome {
    pub(super) status: u8,
    pub(super) message: String,
}
impl Outcome {
    pub(super) fn blocked(message: impl Into<String>) -> Self {
        Self {
            status: 2,
            message: message.into(),
        }
    }
}
impl Runtime {
    #[cfg(test)]
    pub(super) fn injected(
        factory: impl Fn(&str, u32, Arc<dyn ReadCustody>) -> io::Result<(Arc<Storage>, Arc<dyn Fresh>)>
        + 'static,
    ) -> Self {
        Self {
            factory: Rc::new(factory),
            application: Rc::default(),
            gate: Rc::default(),
            fatal: Arc::default(),
            #[cfg(test)]
            entered: Rc::default(),
        }
    }
    pub(super) fn native(origin: Arc<Origin>) -> Self {
        let stores = RefCell::new(None::<(String, Arc<Storage>)>);
        Self {
            factory: Rc::new(move |sid, session, custody| {
                let guard = WindowsGuard::capture(
                    origin.clone(),
                    custody.clone(),
                    sid.to_owned(),
                    session,
                )?;
                let mut stores = stores.borrow_mut();
                if stores.as_ref().is_none_or(|(current, _)| current != sid) {
                    // Trusted peer/session inputs are selected before resolving any
                    // product root. Test composition supplies its own factory.
                    if !sid.starts_with("S-1-5-21-")
                        || !sid
                            .bytes()
                            .all(|b| b.is_ascii_digit() || b"S-".contains(&b))
                    {
                        return Err(io::ErrorKind::PermissionDenied.into());
                    }
                    stores.take();
                    let root = crate::service_install_identity::known_folder(
                        &windows_sys::Win32::UI::Shell::FOLDERID_ProgramData,
                    )?
                    .join("OpenBlade")
                    .join("users")
                    .join(sid);
                    *stores = Some((
                        sid.to_owned(),
                        Storage::open_with(
                            Box::new(CustodialFiles {
                                user: ProtectedFiles::open_at(&root)?,
                                custody,
                            }),
                            sid,
                            super::thermal_native::same_path,
                        )?,
                    ));
                }
                Ok((stores.as_ref().unwrap().1.clone(), guard))
            }),
            application: Rc::default(),
            gate: Rc::default(),
            fatal: Arc::default(),
            #[cfg(test)]
            entered: Rc::default(),
        }
    }
    pub(super) fn application(
        &self,
        caller: Caller,
        sid: String,
        instance: String,
        sequence: i64,
        path: Option<String>,
    ) -> Outcome {
        if caller.logon != Logon::Interactive
            || caller.control_session != Some(caller.session)
            || caller.pid.is_none()
            || sequence <= 0
            || !crate::service_frame::is_guid(&instance)
            || path
                .as_ref()
                .is_some_and(|p| !super::thermal_storage::valid_application_path(p))
        {
            return Outcome::blocked("Application context was rejected.");
        }
        let mut current = self.application.borrow_mut();
        if current.as_ref().is_some_and(|old| {
            old.sid == sid
                && old.session == caller.session
                && old.instance.eq_ignore_ascii_case(&instance)
                && (old.pid != caller.pid.unwrap() || sequence <= old.sequence)
        }) {
            return Outcome::blocked(
                "Application context is stale or belongs to another reporter.",
            );
        }
        *current = Some(Application {
            sid,
            session: caller.session,
            pid: caller.pid.unwrap(),
            instance,
            sequence,
            path,
            expires: Instant::now() + std::time::Duration::from_secs(15),
        });
        Outcome {
            status: 0,
            message: "Application context stored; automatic native reconciliation is not enabled."
                .into(),
        }
    }
    pub(super) async fn apply(
        &self,
        mailbox: MailboxSource,
        caller: Caller,
        sid: String,
        request: String,
        value: Option<Override>,
        deadline: Instant,
        status: Snapshot,
    ) -> io::Result<(Outcome, Option<Snapshot>)> {
        #[cfg(test)]
        self.entered.set(self.entered.get() + 1);
        let _serial = self.gate.lock().await;
        verify_retained_failure(&self.fatal)?;
        // Metadata may retain a previous user's projection. New accepted intent
        // starts from the public view even when this user has no profile file.
        let status = status.redact_private().without_thermal_observation();
        if caller.logon != Logon::Interactive || caller.control_session != Some(caller.session) {
            return Ok((
                Outcome::blocked("The active interactive Windows user is required."),
                None,
            ));
        }
        let MailboxSource::Reader(reader) = mailbox else {
            return Ok((
                Outcome::blocked("Authenticated thermal custody is unavailable."),
                None,
            ));
        };
        reader.custody().verify_integrity()?;
        let (storage, fresh) = match (self.factory)(&sid, caller.session, reader.custody()) {
            Ok(v) => v,
            Err(e) if is_fatal_admission(&e) => {
                return Err(e);
            }
            Err(e) => {
                return Ok((
                    Outcome::blocked(format!(
                        "Saved thermal configuration or fresh admission is unavailable: {e}"
                    )),
                    None,
                ));
            }
        };
        fresh.verify_health()?;
        if storage.pending() {
            return Ok((Outcome::blocked(storage.pending_reason()?), None));
        }
        let proof = fresh.authorize();
        fresh.verify_health()?;
        let Some(proof) = proof else {
            return Ok((
                Outcome::blocked(
                    "Current thermal admission is unavailable; no intent or command was submitted.",
                ),
                None,
            ));
        };
        let application = self
            .application
            .borrow()
            .as_ref()
            .filter(|a| a.sid == sid && a.session == caller.session && Instant::now() < a.expires)
            .and_then(|a| a.path.clone());
        let runtime = self.clone();
        let policy_sid = sid.clone();
        let custody = reader.custody();
        reader.retain_policy(Rc::new(move |base| {
            verify_retained_failure(&runtime.fatal)?;
            let base = base.redact_private();
            let (store, fresh) =
                match (runtime.factory)(&policy_sid, caller.session, custody.clone()) {
                    Ok(value) => value,
                    Err(error) if is_fatal_admission(&error) => return Err(error),
                    Err(_) => return Ok(base.private_policy_unavailable()),
                };
            fresh.verify_health()?;
            let admitted = fresh.authorize().is_some();
            fresh.verify_health()?;
            if !admitted {
                return Ok(base);
            }
            let power = base.thermal_power_source();
            let application = runtime
                .application
                .borrow()
                .as_ref()
                .filter(|a| {
                    a.sid == policy_sid && a.session == caller.session && Instant::now() < a.expires
                })
                .and_then(|a| a.path.clone());
            let value = store.current_override(power)?;
            let desired = store.desired(power, application.as_deref())?;
            Ok(base
                .thermal_profiles(store.profiles()?)?
                .thermal_intent(
                    value.as_ref(),
                    desired.map(|(p, _)| p),
                    "Saved performance policy; current hardware is reported independently.",
                    None,
                )
                .thermal_provenance(desired.map(|(_, s)| s))
                .bind_private_user(&policy_sid))
        }));
        let intent = format!("{:x}", Sha256::digest(serde_json::to_vec(&value)?));
        let permit = Arc::new(RequestPermit {
            storage,
            fresh,
            request,
            caller_pid: caller.pid.ok_or(io::ErrorKind::PermissionDenied)?,
            session: caller.session,
            owner: format!("{:x}", Sha256::digest(proof.generation)),
            intent,
            value,
            application,
            accepted: AtomicBool::new(false),
            destination: Mutex::new(None),
            power: Mutex::new(None),
            selected_source: Mutex::new(None),
            cause: Mutex::new(Cause::CurrentAdmission),
            fatal: self.fatal.clone(),
        });
        let result = reader.mutate(permit.clone(), deadline).await;
        permit.verify_failure()?;
        let result = match result {
            Err(error) if is_fatal_admission(&error) => return Err(error),
            result => result,
        };
        permit.fresh.verify_health()?;
        let final_proof = if Instant::now() < deadline {
            permit.fresh.authorize()
        } else {
            None
        };
        permit.fresh.verify_health()?;
        if final_proof != Some(proof) {
            return Ok((
                Outcome::blocked(
                    "Thermal result lost caller or owner admission; submission evidence is retained.",
                ),
                None,
            ));
        }
        let destination = *permit
            .destination
            .lock()
            .map_err(|_| io::ErrorKind::Other)?;
        if let Err(error) = &result {
            permit.set_cause(Cause::Transport(error.to_string()));
        }
        let observed = result
            .ok()
            .flatten()
            .filter(|value| destination.is_some_and(|destination| confirms(*value, destination)));
        let accepted = permit.accepted.load(Ordering::Acquire);
        let requested_applied = permit
            .value
            .as_ref()
            .is_none_or(|v| destination.is_some_and(|d| v.value == d as i32));
        let outcome = if observed.is_some() && requested_applied {
            Outcome{status:1,message:"Bounded thermal preset confirmed by independent paired readback. GPU clock handling and broad device capabilities remain unavailable; other native controls are incomplete.".into()}
        } else if permit.storage.configuration_failed() {
            Outcome::blocked(
                "Saved configuration replacement failed. Intent was not accepted; no thermal command was submitted. Storage reconciliation is required.",
            )
        } else if permit.storage.pending() {
            Outcome::blocked(if permit.storage.progress().is_none() {
                permit.storage.pending_reason()?
            } else if permit.storage.progress() == Some(0) {
                "Intent stored and journal prepared; no mutation submission intent was reached. Explicit read-only reconciliation is required."
            } else {
                "Intent stored; a thermal command may have been submitted. Read-only reconciliation is required and no mutation will be replayed."
            })
        } else if accepted {
            Outcome {
                status: 0,
                message: format!(
                    "Performance intent stored. {}",
                    permit
                        .cause
                        .lock()
                        .map_err(|_| io::ErrorKind::Other)?
                        .message()
                ),
            }
        } else {
            Outcome::blocked(
                "Performance intent was not accepted; admission or protected persistence failed.",
            )
        };
        let power = *permit.power.lock().map_err(|_| io::ErrorKind::Other)?;
        let intent_value = permit.value.clone();
        let selected_source = *permit
            .selected_source
            .lock()
            .map_err(|_| io::ErrorKind::Other)?;
        let status = if let Some(observed) = observed {
            let fresh = permit.fresh.clone();
            status.with_mailbox_read(
                observed,
                ReadConfirmation::new(deadline, move || fresh.authorize() == Some(proof)),
            )
        } else {
            status
        };
        let status = if accepted {
            status.thermal_profiles(permit.storage.profiles()?)?
        } else {
            status
        };
        let snapshot = accepted.then(|| {
            status
                .thermal_intent(intent_value.as_ref(), destination, &outcome.message, power)
                .bind_private_user(&sid)
                .thermal_provenance(selected_source)
                .thermal_write_admission(outcome.status == 1)
        });
        Ok((outcome, snapshot))
    }
}
struct RequestPermit {
    caller_pid: u32,
    session: u32,
    storage: Arc<Storage>,
    fresh: Arc<dyn Fresh>,
    request: String,
    owner: String,
    intent: String,
    value: Option<Override>,
    application: Option<String>,
    accepted: AtomicBool,
    destination: Mutex<Option<Preset>>,
    power: Mutex<Option<i32>>,
    selected_source: Mutex<Option<i32>>,
    cause: Mutex<Cause>,
    // Reader refusal is Option-valued; retain typed trust loss across that boundary.
    fatal: Arc<Mutex<Option<&'static str>>>,
}
#[derive(Debug)]
enum Cause {
    CurrentAdmission,
    Gpu,
    ManualDeferred,
    NoEligiblePolicy,
    TransactionCapacity,
    PowerChanged,
    Storage(String),
    Transport(String),
}
impl Cause {
    fn message(&self) -> String {
        match self {
            Self::CurrentAdmission => "Current admission or independent readback did not complete; no successful application is reported.".into(),
            Self::Gpu => "GPU clock handling is incomplete; no thermal setter was submitted.".into(),
            Self::ManualDeferred => "Performance is saved, not applied. Manual fan intent is preserved. Submit a new performance request after selecting Automatic fans.".into(),
            Self::NoEligiblePolicy => "No supported performance destination is selected by the current power and profile policy.".into(),
            Self::TransactionCapacity => "Thermal transaction ID capacity is exhausted; no setter was submitted.".into(),
            Self::PowerChanged => "The independently observed power source changed or no longer admits this destination.".into(),
            Self::Storage(e) => format!("Protected storage could not record the next step: {e}"),
            Self::Transport(e) => format!("Thermal transport or ownership did not complete: {e}"),
        }
    }
}
impl RequestPermit {
    fn remember_failure(&self, error: &io::Error) {
        remember_failure(&self.fatal, error);
    }
    fn verify_failure(&self) -> io::Result<()> {
        verify_retained_failure(&self.fatal)
    }
    fn set_cause(&self, cause: Cause) {
        if let Ok(mut current) = self.cause.lock() {
            *current = cause;
        }
    }
}
impl Permit for RequestPermit {
    fn failed(&self, error: &io::Error) {
        self.remember_failure(error);
    }
    fn authorize(&self) -> Option<ReadAdmission> {
        self.verify_failure().ok()?;
        self.fresh.authorize()
    }
    fn resolve(&self, power: i32, baseline: ObservedPerformance) -> io::Result<Option<Preset>> {
        self.verify_failure()?;
        (|| {
            if self.accepted.load(Ordering::Acquire) {
                return Err(io::ErrorKind::PermissionDenied.into());
            }
            self.storage
                .save(self.value.clone(), power)
                .inspect_err(|e| self.set_cause(Cause::Storage(e.to_string())))?;
            self.accepted.store(true, Ordering::Release);
            *self.power.lock().map_err(|_| io::ErrorKind::Other)? = Some(power);
            if self
                .storage
                .gpu_incomplete(power, self.application.as_deref())?
            {
                self.set_cause(Cause::Gpu);
                return Ok(None);
            }
            let desired = self.storage.desired(power, self.application.as_deref())?;
            *self
                .selected_source
                .lock()
                .map_err(|_| io::ErrorKind::Other)? = desired.map(|(_, s)| s);
            let desired = desired.map(|(p, _)| p);
            *self.destination.lock().map_err(|_| io::ErrorKind::Other)? = desired;
            if openblade_domain::thermal_mutation::defer_manual_performance(
                baseline,
                power,
                self.storage
                    .desired_manual_fans(power, self.application.as_deref())?,
            ) {
                self.set_cause(Cause::ManualDeferred);
                return Ok(None);
            }
            if desired.is_none() {
                self.set_cause(Cause::NoEligiblePolicy);
            }
            Ok(desired)
        })()
        .inspect_err(|error| self.remember_failure(error))
    }
    fn refused(&self, cause: Refusal) {
        self.set_cause(match cause {
            Refusal::TransactionCapacity => Cause::TransactionCapacity,
            Refusal::CurrentAdmission => Cause::CurrentAdmission,
            Refusal::PowerChanged => Cause::PowerChanged,
        });
    }
    fn retired(&self) -> io::Result<Settlement> {
        self.verify_failure()?;
        // Transport retirement is already positive. Settlement is attempted once;
        // an uncertain append keeps the original evidence and global write fence.
        self.fresh.verify_health()?;
        let result = if self.accepted.load(Ordering::Acquire) && self.storage.progress() == Some(0)
        {
            self.record_progress(Progress::KnownNotSubmitted)
        } else {
            Ok(())
        };
        self.fresh.verify_health()?;
        match result {
            Ok(()) => Ok(Settlement::Complete),
            Err(error) if is_fatal_admission(&error) => Err(error),
            Err(error) => {
                self.storage.quarantine_settlement()?;
                Ok(Settlement::Unresolved(error))
            }
        }
    }
    fn record(&self, progress: Progress) -> io::Result<()> {
        self.verify_failure()?;
        self.authorize().ok_or(io::ErrorKind::PermissionDenied)?;
        self.record_progress(progress)
            .inspect_err(|e| self.set_cause(Cause::Storage(e.to_string())))
    }
}
impl RequestPermit {
    fn record_progress(&self, progress: Progress) -> io::Result<()> {
        let destination = self
            .destination
            .lock()
            .map_err(|_| io::ErrorKind::Other)?
            .ok_or(io::ErrorKind::PermissionDenied)?;
        self.storage
            .record(
                &self.request,
                &self.owner,
                destination,
                &self.intent,
                &self.value,
                self.caller_pid,
                self.session,
                progress,
            )
            .inspect_err(|error| self.remember_failure(error))
    }
}

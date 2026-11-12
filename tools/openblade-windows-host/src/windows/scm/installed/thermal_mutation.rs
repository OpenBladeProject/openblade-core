//! One installed request composition. No fake or scenario transport is imported.
use super::{
    Caller, Logon,
    mailbox_read::{MailboxSource, ReadCustody},
    thermal_native::{CustodialFiles, ProtectedFiles, WindowsGuard},
    thermal_storage::{Override, Storage},
};
use crate::{
    discovery::performance::{ReadAdmission, mutation::Permit},
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

pub(super) trait Fresh: Send + Sync {
    fn authorize(&self) -> Option<ReadAdmission>;
}
impl Fresh for WindowsGuard {
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
        let _serial = self.gate.lock().await;
        // Metadata may retain a previous user's projection. New accepted intent
        // starts from the public view even when this user has no profile file.
        let status = status.redact_private();
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
        let (storage, fresh) = match (self.factory)(&sid, caller.session, reader.custody()) {
            Ok(v) => v,
            Err(e) => {
                return Ok((
                    Outcome::blocked(format!(
                        "Saved thermal configuration or fresh admission is unavailable: {e}"
                    )),
                    None,
                ));
            }
        };
        if storage.pending() {
            return Ok((
                Outcome::blocked(
                    "An earlier thermal submission requires read-only reconciliation.",
                ),
                None,
            ));
        }
        let proof = fresh.authorize().ok_or(io::ErrorKind::PermissionDenied)?;
        let application = self
            .application
            .borrow()
            .as_ref()
            .filter(|a| a.sid == sid && a.session == caller.session && Instant::now() < a.expires)
            .and_then(|a| a.path.clone());
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
        });
        let result = reader.mutate(permit.clone(), deadline).await;
        if Instant::now() >= deadline || permit.fresh.authorize() != Some(proof) {
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
        } else if permit.storage.pending() {
            Outcome::blocked(if permit.storage.progress() == Some(0) {
                "Intent stored and journal prepared; no mutation submission intent was reached. Explicit read-only reconciliation is required."
            } else {
                "Intent stored; a thermal command may have been submitted. Read-only reconciliation is required and no mutation will be replayed."
            })
        } else if accepted {
            Outcome{status:0,message:"Performance intent stored. Applicable power policy, selected profile, or GPU clock effects prevent complete application of this request.".into()}
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
}
impl Permit for RequestPermit {
    fn authorize(&self) -> Option<ReadAdmission> {
        self.fresh.authorize()
    }
    fn resolve(&self, power: i32) -> io::Result<Option<Preset>> {
        if self.accepted.load(Ordering::Acquire) {
            return Err(io::ErrorKind::PermissionDenied.into());
        }
        self.storage.save(self.value.clone(), power)?;
        self.accepted.store(true, Ordering::Release);
        *self.power.lock().map_err(|_| io::ErrorKind::Other)? = Some(power);
        if self
            .storage
            .gpu_incomplete(power, self.application.as_deref())?
        {
            return Ok(None);
        }
        let desired = self.storage.desired(power, self.application.as_deref())?;
        *self
            .selected_source
            .lock()
            .map_err(|_| io::ErrorKind::Other)? = desired.map(|(_, s)| s);
        let desired = desired.map(|(p, _)| p);
        *self.destination.lock().map_err(|_| io::ErrorKind::Other)? = desired;
        Ok(desired)
    }
    fn record(&self, progress: Progress) -> io::Result<()> {
        self.authorize().ok_or(io::ErrorKind::PermissionDenied)?;
        let destination = self
            .destination
            .lock()
            .map_err(|_| io::ErrorKind::Other)?
            .ok_or(io::ErrorKind::PermissionDenied)?;
        self.storage.record(
            &self.request,
            &self.owner,
            destination,
            &self.intent,
            &self.value,
            self.caller_pid,
            self.session,
            progress,
        )
    }
}

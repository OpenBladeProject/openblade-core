//! One installed request composition. No fake or scenario transport is imported.
#[cfg(not(test))]
use super::thermal_native::{CustodialFiles, ProtectedFiles};
use super::{
    Caller, Logon,
    mailbox_read::{MailboxSource, ReadCustody},
    thermal_native::{SettleGuard, WindowsGuard},
    thermal_storage::{Override, Storage},
};
#[cfg(not(test))]
use crate::loaded_origin::Origin;
use crate::{
    discovery::performance::{
        ObservedPerformance, ReadAdmission,
        mutation::{Permit, Refusal, Settlement},
    },
    service_status::{ReadConfirmation, Snapshot},
};
use openblade_domain::{
    automation::UNCLASSIFIED_EXTERNAL,
    thermal_mutation::{Preset, Progress, confirms},
};
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
        && let Ok(mut retained) = fatal.lock()
    {
        retained.get_or_insert(cause.0);
    }
}

#[path = "battery_mutation.rs"]
mod battery;
#[cfg(feature = "service-temporary-charge-v1")]
pub(super) use battery::BatteryUpdate;
#[path = "fan_mutation.rs"]
mod fan;
pub(super) use fan::FanRequest;
pub(super) trait Fresh: Send + Sync {
    fn on_battery(&self) -> Option<bool> {
        None
    }
    /// Fresh positive Windows AC presence (ACLineStatus online). It is never
    /// wattage evidence; it only lets an unclassified 07/8C reading count.
    fn external_presence(&self) -> bool {
        self.on_battery() == Some(false)
    }
    fn authentication(&self) -> Option<(u32, i32)> {
        None
    }
    fn authorize_owned_stop(&self) -> Option<ReadAdmission> {
        None
    }
    fn verify_health(&self) -> io::Result<()> {
        Ok(())
    }
    fn authorize(&self) -> Option<ReadAdmission>;
    /// Settle-only Stop admission for this owner's exact identity at the
    /// current lifecycle epoch (`WindowsGuard::capture_settle`). The result
    /// authorizes owned-stop reads only; its `authorize` is always None.
    fn capture_settle(
        &self,
        _sid: &str,
        _session: u32,
        _authentication: Option<(u32, i32)>,
    ) -> io::Result<Arc<dyn Fresh>> {
        Err(io::ErrorKind::PermissionDenied.into())
    }
}
impl Fresh for SettleGuard {
    fn authentication(&self) -> Option<(u32, i32)> {
        Some(self.authentication())
    }
    fn authorize_owned_stop(&self) -> Option<ReadAdmission> {
        self.authorize_owned_stop()
    }
    fn verify_health(&self) -> io::Result<()> {
        self.verify_health()
    }
    // Never request admission: a settle capture cannot start a request.
    fn authorize(&self) -> Option<ReadAdmission> {
        None
    }
}
impl Fresh for WindowsGuard {
    fn capture_settle(
        &self,
        sid: &str,
        session: u32,
        authentication: Option<(u32, i32)>,
    ) -> io::Result<Arc<dyn Fresh>> {
        Ok(self.capture_settle(sid, session, authentication)?)
    }
    fn on_battery(&self) -> Option<bool> {
        self.on_battery()
    }
    fn authentication(&self) -> Option<(u32, i32)> {
        Some(self.authentication())
    }
    fn authorize_owned_stop(&self) -> Option<ReadAdmission> {
        self.authorize_owned_stop()
    }
    fn verify_health(&self) -> io::Result<()> {
        self.verify_health()
    }
    fn authorize(&self) -> Option<ReadAdmission> {
        self.authorize()
    }
}
type Factory =
    dyn Fn(&str, u32, Arc<dyn ReadCustody>) -> io::Result<(Arc<Storage>, Arc<dyn Fresh>)>;
type ReleaseNotice = (
    Arc<battery::BatteryPermit>,
    Option<super::thermal_storage::ReleasePause>,
);
#[cfg(test)]
type ReleaseHook = Rc<dyn Fn()>;
#[derive(Clone)]
pub(super) struct Runtime {
    factory: Rc<Factory>,
    application: Rc<RefCell<Option<Application>>>,
    fan_owner: Rc<RefCell<Option<Arc<fan::FanPermit>>>>,
    temporary_owner: Rc<RefCell<Option<Arc<battery::BatteryPermit>>>>,
    temporary_pending: Rc<RefCell<Option<Arc<battery::BatteryPermit>>>>,
    release_deferred: Rc<std::cell::Cell<Option<super::thermal_storage::ReleasePause>>>,
    release_notice: Rc<RefCell<Option<ReleaseNotice>>>,
    #[cfg(test)]
    release_hook: Rc<RefCell<Option<ReleaseHook>>>,
    #[cfg(feature = "service-temporary-charge-v1")]
    temporary_event: Rc<std::cell::Cell<Option<u64>>>,
    fan_pending: Rc<RefCell<Option<Arc<fan::FanPermit>>>>,
    gate: Rc<tokio::sync::Mutex<()>>,
    // Public battery history is bounded independently of request/user intent.
    // Only independent reads update its timestamp; sibling publications cannot.
    battery_observation: Arc<Mutex<battery::BatteryObservation>>,
    battery_source: Arc<std::sync::atomic::AtomicU8>,
    battery_warning: Arc<Mutex<Option<String>>>,
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
/// Managed `ExecuteScopedMutationAsync` reply for a cleared explicit override
/// on unclassified external power, which selects no power profile.
pub(super) const UNCLASSIFIED_CLEARED: &str =
    "Override cleared. Power-profile application waits for adapter classification.";
/// Managed `ValidateUnclassifiedPerformanceOverride`, as `RecordRejectedIntent`
/// reports it (Failed).
const UNCLASSIFIED_REJECTED: &str = "The requested intent was rejected: This request needs classified power or includes native overclocking; ordinary external presence does not admit it.";
/// Managed `PublishScopedUnavailable` (Blocked) when an unclassified adapter
/// reading lacks fresh Windows AC presence or the confirmed state is HyperBoost
/// (`CanAdmitUnclassifiedExternalControl`); no intent is stored.
pub(super) const UNCLASSIFIED_BLOCKED: &str = "Hardware application was not confirmed. The external power adapter could not be classified; overrides and control writes are blocked.";
/// The class a status projection reads saved intent at: battery, AC with fresh
/// presence (the base's 3), or unknown (-1) otherwise.
pub(super) fn status_query_power(base: i32, fresh: &dyn Fresh) -> i32 {
    if base == 1 || fresh.external_presence() {
        base
    } else {
        -1
    }
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
        mailbox: &MailboxSource,
        factory: impl Fn(&str, u32, Arc<dyn ReadCustody>) -> io::Result<(Arc<Storage>, Arc<dyn Fresh>)>
        + 'static,
    ) -> Self {
        Self {
            factory: Rc::new(factory),
            application: Rc::default(),
            fan_owner: Rc::default(),
            temporary_owner: Rc::default(),
            temporary_pending: Rc::default(),
            release_deferred: Rc::default(),
            release_notice: Rc::default(),
            #[cfg(test)]
            release_hook: Rc::default(),
            #[cfg(feature = "service-temporary-charge-v1")]
            temporary_event: Rc::default(),
            fan_pending: Rc::default(),
            gate: Rc::default(),
            battery_observation: Arc::default(),
            battery_source: Arc::new(std::sync::atomic::AtomicU8::new(3)),
            battery_warning: Arc::default(),
            fatal: mailbox.fatal_evidence(),
            #[cfg(test)]
            entered: Rc::default(),
        }
    }
    #[cfg(not(test))]
    pub(super) fn native(origin: Arc<Origin>, mailbox: &MailboxSource) -> Self {
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
            fan_owner: Rc::default(),
            temporary_owner: Rc::default(),
            temporary_pending: Rc::default(),
            release_deferred: Rc::default(),
            release_notice: Rc::default(),
            #[cfg(test)]
            release_hook: Rc::default(),
            #[cfg(feature = "service-temporary-charge-v1")]
            temporary_event: Rc::default(),
            fan_pending: Rc::default(),
            gate: Rc::default(),
            battery_observation: Arc::default(),
            battery_source: Arc::new(std::sync::atomic::AtomicU8::new(3)),
            battery_warning: Arc::default(),
            fatal: mailbox.fatal_evidence(),
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
    // Every caller, including the shared mutation tests, passes the admitted request fields individually.
    #[allow(clippy::too_many_arguments)]
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
        let status = status
            .without_private_policy()
            .without_thermal_observation();
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
        let (storage, fresh) = match self.capture(&sid, caller.session, reader.custody()) {
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
            let base = base.without_private_policy();
            let (store, fresh) = match runtime.capture(&policy_sid, caller.session, custody.clone())
            {
                Ok(value) => value,
                Err(error) if is_fatal_admission(&error) => return Err(error),
                Err(_) => {
                    return Ok(base.private_policy_unavailable(
                        crate::service_status::PrivatePolicyFamily::Thermal,
                    ));
                }
            };
            fresh.verify_health()?;
            let admitted = fresh.authorize().is_some();
            fresh.verify_health()?;
            if !admitted {
                return Ok(base);
            }
            let power = base.thermal_power_source();
            // The status base reports 3 for any power that is not battery,
            // Unknown included. A failed or unknown Windows read is not a power
            // boundary (managed `CaptureControlPowerSource`), so without fresh
            // AC presence saved intent is read at an unknown class (-1), which
            // retires no temporary.
            let query = status_query_power(power, fresh.as_ref());
            let application = runtime
                .application
                .borrow()
                .as_ref()
                .filter(|a| {
                    a.sid == policy_sid && a.session == caller.session && Instant::now() < a.expires
                })
                .and_then(|a| a.path.clone());
            let value = store.current_override(query)?;
            let desired = store.desired(query, application.as_deref())?;
            Ok(base
                .thermal_profiles(store.profiles()?)?
                .thermal_intent(
                    value.as_ref(),
                    desired.map(|(p, _)| p),
                    "Saved performance policy; current hardware is reported independently.",
                    None,
                )
                .thermal_provenance(desired.map(|(_, s)| s))
                .fan_intent(
                    store.current_fan_override(query)?.as_ref(),
                    store.desired_fan(query, application.as_deref())?,
                    power,
                    "Saved thermal policy; firmware mode is reported independently.",
                )
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
            Outcome::blocked(permit.storage.pending_reason()?)
        } else if accepted {
            let cause = permit.cause.lock().map_err(|_| io::ErrorKind::Other)?;
            if matches!(
                *cause,
                Cause::ManualDeferred | Cause::AwaitingClassification
            ) {
                // Managed's reply for this external-power manual-fan deferral
                // (ServiceControlRuntime.DeferredPerformanceIntentMessage), or for a
                // clear while the adapter is unclassified. As in
                // managed, a fan request that returns the fans to Automatic
                // applies the saved preset in the same pair (FanPermit).
                Outcome {
                    status: 0,
                    message: cause.message(),
                }
            } else {
                // Saved but unapplied: Blocked, prefixed as ExecuteScopedMutationAsync
                // prefixes the hardware result after committing managed intent.
                let committed = if permit.value.is_none() {
                    "Performance override was cleared."
                } else {
                    "Performance override intent was stored."
                };
                // The policy can select and confirm another destination while the
                // requested value itself stays saved but unapplied.
                let reason = if observed.is_some() && !requested_applied {
                    "The current power and profile policy selects another performance destination, which is confirmed; the requested value is saved but not applied.".into()
                } else {
                    cause.message()
                };
                Outcome::blocked(format!("{committed} Hardware result: {reason}"))
            }
        } else if matches!(
            *permit.cause.lock().map_err(|_| io::ErrorKind::Other)?,
            Cause::UnclassifiedRejected
        ) {
            Outcome {
                status: 3,
                message: UNCLASSIFIED_REJECTED.into(),
            }
        } else if matches!(
            *permit.cause.lock().map_err(|_| io::ErrorKind::Other)?,
            Cause::UnclassifiedBlocked
        ) {
            Outcome::blocked(UNCLASSIFIED_BLOCKED)
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
    AwaitingClassification,
    FanHandbackPending,
    Gpu,
    ManualDeferred,
    NoEligiblePolicy,
    TransactionCapacity,
    PowerChanged,
    Storage(String),
    Transport(String),
    UnclassifiedBlocked,
    UnclassifiedRejected,
}
impl Cause {
    fn message(&self) -> String {
        match self {
            Self::CurrentAdmission => "Current admission or independent readback did not complete; no successful application is reported.".into(),
            Self::AwaitingClassification => UNCLASSIFIED_CLEARED.into(),
            Self::FanHandbackPending => "OpenBlade still owns a fan change that has not been handed back to firmware Automatic; no thermal setter was submitted.".into(),
            Self::Gpu => "GPU clock handling is incomplete; no thermal setter was submitted.".into(),
            // Without the fan composition no native request returns the fans,
            // so that build keeps the explicit resubmission instruction.
            Self::ManualDeferred if cfg!(feature = "service-fan-mutation-v2") => "Performance intent was stored and will be applied when fans return to Automatic.".into(),
            Self::ManualDeferred => "Performance intent stored. Performance is saved, not applied. Manual fan intent is preserved. Submit a new performance request after selecting Automatic fans.".into(),
            Self::NoEligiblePolicy => "No supported performance destination is selected by the current power and profile policy.".into(),
            Self::TransactionCapacity => "Thermal transaction ID capacity is exhausted; no setter was submitted.".into(),
            Self::PowerChanged => "The independently observed power source changed or no longer admits this destination.".into(),
            Self::Storage(e) => format!("Protected storage could not record the next step: {e}"),
            Self::Transport(e) => format!("Thermal transport or ownership did not complete: {e}"),
            Self::UnclassifiedBlocked => UNCLASSIFIED_BLOCKED.into(),
            Self::UnclassifiedRejected => UNCLASSIFIED_REJECTED.into(),
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
    fn observe_power(&self, power: i32) -> io::Result<()> {
        let admission = self
            .fresh
            .authorize()
            .ok_or(io::ErrorKind::PermissionDenied)?;
        self.storage.observe_power(power, admission.lifecycle)
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
    fn external_presence(&self) -> bool {
        self.fresh.external_presence()
    }
    fn destination_holds(&self, power: i32) -> bool {
        (|| -> io::Result<bool> {
            let Some(destination) = *self.destination.lock().map_err(|_| io::ErrorKind::Other)?
            else {
                return Ok(false);
            };
            self.observe_power(power)?;
            let application = self.application.as_deref();
            Ok(!self.storage.gpu_incomplete(power, application)?
                && self.storage.desired(power, application)?.map(|(p, _)| p) == Some(destination))
        })()
        .inspect_err(|error| self.remember_failure(error))
        .unwrap_or(false)
    }
    fn resolve(&self, power: i32, baseline: ObservedPerformance) -> io::Result<Option<Preset>> {
        self.verify_failure()?;
        (|| {
            if self.accepted.load(Ordering::Acquire) {
                return Err(io::ErrorKind::PermissionDenied.into());
            }
            let unclassified = power == UNCLASSIFIED_EXTERNAL;
            // Managed `CanAdmitUnclassifiedExternalControl` needs confirmed
            // HyperBoost to be inactive, and the HyperBoost exit needs classified
            // power, so nothing is stored or written.
            if unclassified && baseline == ObservedPerformance::HyperBoostObserved {
                self.set_cause(Cause::UnclassifiedBlocked);
                return Ok(None);
            }
            // Managed `ValidateUnclassifiedPerformanceOverride` rejects native
            // overclocking there before any intent is stored.
            if unclassified
                && self
                    .value
                    .as_ref()
                    .is_some_and(|v| v.gpu_clock_tuning.is_some())
            {
                self.set_cause(Cause::UnclassifiedRejected);
                return Ok(None);
            }
            self.observe_power(power)?;
            self.storage
                .save(self.value.clone(), power)
                .inspect_err(|e| self.set_cause(Cause::Storage(e.to_string())))?;
            self.accepted.store(true, Ordering::Release);
            *self.power.lock().map_err(|_| io::ErrorKind::Other)? = Some(power);
            // A clear selects no profile there: nothing is written until the
            // adapter is classified (managed `ExecuteScopedMutationAsync`).
            if unclassified && self.value.is_none() {
                self.set_cause(Cause::AwaitingClassification);
                return Ok(None);
            }
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
            // Stored promises that the fan request returning the fans applies the
            // intent. That request writes only Balanced or Performance, so
            // without such a destination the intent is reported unapplied.
            if desired.is_none() {
                self.set_cause(Cause::NoEligiblePolicy);
                return Ok(None);
            }
            if openblade_domain::thermal_mutation::defer_manual_performance(
                baseline,
                power,
                self.storage
                    .desired_manual_fans(power, self.application.as_deref())?,
            ) {
                self.set_cause(Cause::ManualDeferred);
                return Ok(None);
            }
            // An owned fan row without its handback is not a manual-fan deferral:
            // the saved fan intent may already be Automatic or cleared.
            if self.storage.outstanding_fan() {
                self.set_cause(Cause::FanHandbackPending);
                return Ok(None);
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
            Refusal::UnclassifiedBlocked => Cause::UnclassifiedBlocked,
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

#[cfg(all(test, feature = "service-battery-policy-v1"))]
impl Runtime {
    pub(super) fn battery_clock_for_tests(&self) -> Rc<dyn Fn() -> Option<Instant>> {
        let history = self.battery_observation.clone();
        Rc::new(move || history.lock().unwrap().policy.map(|(_, at)| at))
    }
}

use std::{
    io,
    sync::{
        Arc,
        atomic::{AtomicBool, Ordering},
    },
    thread::{Builder, JoinHandle},
    time::Duration,
};

use tokio::{
    sync::watch,
    time::{Instant, sleep, timeout_at},
};

use super::{
    Caller, Observation, Pending, Snapshot, StatusSource,
    subscription::{Publication, REFRESH_INTERVAL},
};
use crate::service_frame::invalid;

#[cfg(any(test, feature = "service-hardware-compile-check"))]
use super::FirmwareSource;

const REFRESH_BUDGET: Duration = Duration::from_secs(1);
const CONTROLLER_UNKNOWN: &str = "Competing controller observation is unavailable.";

enum DeviceMetadata {
    Unsupported,
    Unavailable,
    MatchedHost { model: String, hid: HidMetadata },
}

enum HidMetadata {
    NoCandidate,
    Candidate,
    Ambiguous,
    Unavailable,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum Power {
    Battery,
    ExternalUnknown,
    Unknown,
}

struct Evidence {
    device: DeviceMetadata,
    power: Power,
    controllers: Vec<String>,
    controller_warning: Option<&'static str>,
}

#[derive(PartialEq, Eq)]
struct StatusFields {
    message: String,
    on_battery: bool,
    unsupported_laptop: bool,
    controllers: Vec<String>,
    controller_warning: Option<&'static str>,
}

type ReadEvidence = dyn Fn(&AtomicBool) -> Evidence + Send + Sync;

struct Refresh {
    cancel: Arc<AtomicBool>,
    worker: JoinHandle<Option<Evidence>>,
}

impl Refresh {
    async fn wait(&self) {
        while !self.worker.is_finished() {
            sleep(Duration::from_millis(5)).await;
        }
    }

    fn finish(self) -> io::Result<Option<Evidence>> {
        self.worker
            .join()
            .map_err(|_| io::Error::other("status observer panicked"))
    }
}

struct ObservedStatus {
    read: Option<Arc<ReadEvidence>>,
    #[cfg(any(test, feature = "service-hardware-compile-check"))]
    firmware: Option<FirmwareSource>,
    refresh: Option<Refresh>,
    last: Option<(i64, StatusFields)>,
    observed_at: Option<Instant>,
    publisher: Option<watch::Sender<Arc<Publication>>>,
    metadata: Option<Arc<Publication>>,
}

impl ObservedStatus {
    fn new(read: impl Fn(&AtomicBool) -> Evidence + Send + Sync + 'static) -> Self {
        Self {
            read: Some(Arc::new(read)),
            #[cfg(any(test, feature = "service-hardware-compile-check"))]
            firmware: None,
            refresh: None,
            last: None,
            observed_at: None,
            publisher: None,
            metadata: None,
        }
    }

    async fn refresh(&mut self) -> io::Result<Option<Evidence>> {
        let read = self
            .read
            .as_ref()
            .ok_or_else(|| invalid("status source retired"))?;
        if let Some(prior) = self.refresh.take() {
            // A timed-out or canceled request keeps ownership of its worker.
            // Never queue a replacement behind a still-running observation.
            if !prior.worker.is_finished() {
                self.refresh = Some(prior);
                return Ok(None);
            }
            prior.finish()?;
        }
        let deadline = Instant::now() + REFRESH_BUDGET;
        let cancel = Arc::new(AtomicBool::new(false));
        let worker_cancel = cancel.clone();
        let read = read.clone();
        // Synchronous OS calls can stall. Keep them off the SCM Tokio runtime
        // so an unconfirmed drain cannot block runtime teardown. An unfinished
        // thread is only released on failed retirement/process exit.
        let worker = Builder::new()
            .name("openblade-status-observer".to_owned())
            .spawn(move || {
                (!worker_cancel.load(Ordering::Relaxed)).then(|| read(&worker_cancel))
            })?;
        let refresh = self.refresh.insert(Refresh { cancel, worker });
        if timeout_at(deadline, refresh.wait()).await.is_err() {
            refresh.cancel.store(true, Ordering::Relaxed);
            return Ok(None);
        }
        let fresh = Instant::now() < deadline;
        let evidence = self.refresh.take().expect("completed refresh").finish()?;
        Ok(evidence.filter(|_| fresh))
    }

    async fn observe_since(&mut self, requested: Instant) -> io::Result<()> {
        if self.read.is_none() {
            return Err(invalid("status source retired"));
        }
        if self
            .observed_at
            .is_some_and(|observed| observed > requested)
        {
            return Ok(());
        }
        let observed = async {
            let evidence = self.refresh().await?;
            self.publish(evidence)?;
            self.observed_at = Some(Instant::now());
            Ok(())
        }
        .await;
        if observed.is_err() {
            // A fatal observer failure closes the feed before any other
            // connection can reuse its last successful publication.
            self.read = None;
            self.observed_at = None;
            self.publisher = None;
        }
        observed
    }

    fn publish(&mut self, evidence: Option<Evidence>) -> io::Result<()> {
        let message = match evidence.as_ref().map(|current| &current.device) {
            Some(DeviceMetadata::Unsupported) => {
                "No verified supported device is available.".to_owned()
            }
            Some(DeviceMetadata::MatchedHost { model, hid }) => {
                let detail = match hid {
                    HidMetadata::NoCandidate => "no matching control HID metadata",
                    HidMetadata::Candidate => "matching control HID metadata",
                    HidMetadata::Ambiguous => "ambiguous control HID metadata",
                    HidMetadata::Unavailable => "control HID metadata unavailable",
                };
                if cfg!(feature = "service-mailbox-observation-v1") {
                    format!(
                        "Matched {model} host ({detail}). Request a thermal observation to read its settings. Native controls remain unavailable."
                    )
                } else {
                    format!(
                        "Matched {model} host ({detail}). Firmware observation denied: adapter, activity and managed drain admission are unavailable."
                    )
                }
            }
            Some(DeviceMetadata::Unavailable) | None => {
                if cfg!(feature = "service-mailbox-observation-v1") {
                    "Device identity is unavailable. No fresh thermal observation is confirmed."
                        .to_owned()
                } else {
                    "Device identity is unavailable. Firmware observation denied: adapter, activity and managed drain admission are unavailable.".to_owned()
                }
            }
        };
        let unsupported_laptop = matches!(
            evidence.as_ref().map(|current| &current.device),
            Some(DeviceMetadata::Unsupported)
        );
        let (power, controllers, controller_warning) = evidence.map_or(
            (Power::Unknown, vec![], Some(CONTROLLER_UNKNOWN)),
            |current| {
                (
                    current.power,
                    current.controllers,
                    current.controller_warning,
                )
            },
        );
        let fields = StatusFields {
            message,
            on_battery: power == Power::Battery,
            unsupported_laptop,
            controllers,
            controller_warning,
        };
        // Keep one revision across caller projections. Unobservable evidence
        // changes (such as unknown versus unclassified external power) do not
        // create a new status revision.
        let revision = match &self.last {
            Some((_, last)) if *last == fields => return Ok(()),
            Some((revision, _)) => (*revision)
                .max(
                    self.publisher
                        .as_ref()
                        .map_or(0, |p| p.borrow().active.revision()),
                )
                .checked_add(1)
                .ok_or_else(|| invalid("status revision exhausted"))?,
            None => 0,
        };
        let project = |access, message| {
            Snapshot::observed_read_only(
                revision,
                access,
                fields.on_battery,
                fields.unsupported_laptop,
                fields.controllers.clone(),
                fields.controller_warning,
                message,
            )
        };
        let publication = Arc::new(Publication {
            active: project(0, fields.message.clone()),
            inactive: project(
                1,
                "Another Windows session currently controls OpenBlade hardware.".to_owned(),
            ),
        });
        self.metadata = Some(publication.clone());
        if let Some(publisher) = &self.publisher {
            publisher.send_replace(publication);
        } else {
            self.publisher = Some(watch::Sender::new(publication));
        }
        self.last = Some((revision, fields));
        Ok(())
    }
}

impl StatusSource for ObservedStatus {
    fn snapshot(&mut self, caller: Caller, requested: Instant) -> Pending<'_, Snapshot> {
        Box::pin(async move {
            let access = caller.access()?;
            self.observe_since(requested).await?;
            let publication = self.publisher.as_ref().expect("observed status").borrow();
            Ok(if access == 0 {
                publication.active.clone()
            } else {
                publication.inactive.clone()
            })
        })
    }

    fn observe_performance(&mut self, caller: Caller, deadline: Instant) -> Observation {
        #[cfg(not(any(test, feature = "service-hardware-compile-check")))]
        {
            let _ = (caller, deadline);
            Box::pin(async { Ok(None) })
        }
        #[cfg(any(test, feature = "service-hardware-compile-check"))]
        self.firmware.as_ref().map_or_else(
            || Box::pin(async { Ok(None) }) as Observation,
            |firmware| firmware.observe(caller, deadline),
        )
    }

    fn publications(&self) -> Option<watch::Receiver<Arc<Publication>>> {
        self.publisher.as_ref().map(watch::Sender::subscribe)
    }

    fn refresh_publications(&mut self) -> Pending<'_, Instant> {
        Box::pin(async {
            if self.read.is_none() {
                return Err(invalid("status source retired"));
            }
            if let Some(due) = self.observed_at.map(|observed| observed + REFRESH_INTERVAL)
                && Instant::now() < due
            {
                return Ok(due);
            }
            self.observe_since(Instant::now()).await?;
            Ok(self.observed_at.expect("observed status") + REFRESH_INTERVAL)
        })
    }

    fn drain(&mut self) -> Pending<'_, ()> {
        Box::pin(async move {
            self.read = None;
            self.last = None;
            self.observed_at = None;
            self.publisher = None;
            let firmware = async {
                #[cfg(any(test, feature = "service-hardware-compile-check"))]
                if let Some(firmware) = &self.firmware {
                    firmware.stop().await?;
                }
                Ok(())
            };
            let metadata = async {
                if let Some(refresh) = &self.refresh {
                    refresh.cancel.store(true, Ordering::Relaxed);
                    refresh.wait().await;
                }
                if let Some(refresh) = self.refresh.take() {
                    refresh.finish()?;
                }
                Ok(())
            };
            let (metadata, firmware) = tokio::join!(metadata, firmware);
            metadata.and(firmware)
        })
    }
}

impl Drop for ObservedStatus {
    fn drop(&mut self) {
        if let Some(refresh) = &self.refresh {
            refresh.cancel.store(true, Ordering::Relaxed);
        }
    }
}

#[cfg(not(test))]
#[path = "../lifecycle.rs"]
#[allow(dead_code)]
mod lifecycle;

#[cfg(not(test))]
pub(super) fn native_status_source(
    origin: Arc<crate::loaded_origin::Origin>,
) -> io::Result<Box<dyn StatusSource>> {
    #[cfg(feature = "service-mailbox-observation-v1")]
    let mailbox = super::mailbox_read::MailboxSource::acquire(origin.clone())?;
    #[cfg(feature = "service-thermal-mutation-v1")]
    return Ok(Box::new(GuardedStatus {
        inner: ObservedStatus::new(read_windows),
        mutation: Some(super::thermal_mutation::Runtime::native(
            origin.clone(),
            &mailbox,
        )),
        origin,
        mailbox,
    }));
    #[cfg(not(feature = "service-thermal-mutation-v1"))]
    Ok(compose_guarded_status(
        ObservedStatus::new(read_windows),
        origin,
        #[cfg(feature = "service-mailbox-observation-v1")]
        mailbox,
    ))
}
#[cfg(any(test, not(feature = "service-thermal-mutation-v1")))]
fn compose_guarded_status(
    inner: ObservedStatus,
    origin: Arc<dyn StatusOrigin>,
    #[cfg(feature = "service-mailbox-observation-v1")] mailbox: super::mailbox_read::MailboxSource,
) -> Box<dyn StatusSource> {
    Box::new(GuardedStatus {
        inner,
        origin,
        #[cfg(feature = "service-thermal-mutation-v1")]
        mutation: None,
        #[cfg(feature = "service-mailbox-observation-v1")]
        mailbox,
    })
}

pub(super) trait StatusOrigin: Send + Sync {
    // Only the temporary-charge source events read the lifecycle epoch.
    #[cfg_attr(not(feature = "service-temporary-charge-v1"), allow(dead_code))]
    fn power_epoch(&self) -> Option<u64> {
        None
    }
    fn verify(&self) -> io::Result<()>;
    fn revoke(&self);
    fn normal_stop(&self) {
        self.revoke();
    }
    fn admit_owned_stop(&self) -> io::Result<()> {
        Err(io::ErrorKind::PermissionDenied.into())
    }
    #[cfg(feature = "service-keyboard-driver-mode-v1")]
    fn low_power_wake(&self) -> Option<Arc<tokio::sync::Notify>> {
        None
    }
    // Only the metadata-only composition drains through the origin; the mailbox reader drains otherwise.
    #[cfg_attr(feature = "service-mailbox-observation-v1", allow(dead_code))]
    fn confirm_metadata_drain(&self) -> io::Result<()>;
    fn confirm_transport_drain(&self) -> io::Result<()>;
}
impl StatusOrigin for crate::loaded_origin::Origin {
    fn power_epoch(&self) -> Option<u64> {
        self.lifecycle.active()
    }
    fn normal_stop(&self) {
        self.revoke_for_stop()
    }
    fn admit_owned_stop(&self) -> io::Result<()> {
        self.verify_owned_stop()
    }
    #[cfg(feature = "service-keyboard-driver-mode-v1")]
    fn low_power_wake(&self) -> Option<Arc<tokio::sync::Notify>> {
        Some(self.lifecycle.suspended())
    }
    fn verify(&self) -> io::Result<()> {
        self.verify()
    }
    fn revoke(&self) {
        self.revoke();
    }
    fn confirm_metadata_drain(&self) -> io::Result<()> {
        self.confirm_metadata_drain()
    }
    fn confirm_transport_drain(&self) -> io::Result<()> {
        self.confirm_transport_drain()
    }
}
struct GuardedStatus {
    #[cfg(feature = "service-thermal-mutation-v1")]
    mutation: Option<super::thermal_mutation::Runtime>,
    inner: ObservedStatus,
    origin: Arc<dyn StatusOrigin>,
    #[cfg(feature = "service-mailbox-observation-v1")]
    mailbox: super::mailbox_read::MailboxSource,
}
impl GuardedStatus {
    #[cfg(feature = "service-temporary-charge-v1")]
    fn publish_battery_update(
        &self,
        update: super::thermal_mutation::BatteryUpdate,
    ) -> io::Result<()> {
        if let Some(publisher) = &self.inner.publisher {
            let mut status = update.apply(publisher.borrow().active.clone());
            status.set_observation_revision(
                publisher
                    .borrow()
                    .active
                    .revision()
                    .checked_add(1)
                    .ok_or_else(|| invalid("status revision exhausted"))?,
            );
            publisher.send_replace(Arc::new(Publication {
                inactive: status.inactive_mutation(),
                active: status,
            }));
        }
        Ok(())
    }
}
/// Publishes the status a reconciliation of saved intent returned, as a
/// performance request's is: rebased on the latest publication, projected
/// with this user's policy, retained by the Reader and given the next
/// revision.
#[cfg(feature = "service-profile-triggers-v1")]
fn publish_reconciled(
    publisher: &watch::Sender<Arc<Publication>>,
    mailbox: &super::mailbox_read::MailboxSource,
    status: Snapshot,
) -> io::Result<Snapshot> {
    let mut status = status.rebase_thermal_mutation(&publisher.borrow().active);
    if let super::mailbox_read::MailboxSource::Reader(reader) = mailbox {
        status = reader.current_policy(status)?;
        reader.retain_mutation(&status);
    }
    status.set_observation_revision(
        publisher
            .borrow()
            .active
            .revision()
            .checked_add(1)
            .ok_or_else(|| invalid("status revision exhausted"))?,
    );
    publisher.send_replace(Arc::new(Publication {
        inactive: status.inactive_mutation(),
        active: status.clone(),
    }));
    Ok(status)
}
impl StatusSource for GuardedStatus {
    #[cfg(feature = "service-profile-triggers-v1")]
    fn reconcile_lifecycle(&mut self) -> Option<super::Reconciling> {
        let runtime = self.mutation.clone()?;
        let publisher = self.inner.publisher.clone()?;
        let mailbox = self.mailbox.clone();
        Some(Box::pin(async move {
            // The latest status is cloned only for an attempt that runs.
            let status = || publisher.borrow().active.clone();
            if let Some(status) = runtime.reconcile_lifecycle(&mailbox, status).await? {
                publish_reconciled(&publisher, &mailbox, status)?;
            }
            Ok(())
        }))
    }
    #[cfg(feature = "service-keyboard-driver-mode-v1")]
    fn low_power_wake(&self) -> Option<Arc<tokio::sync::Notify>> {
        self.origin.low_power_wake()
    }
    #[cfg(feature = "service-keyboard-driver-mode-v1")]
    fn maintain_driver_mode(&mut self) -> Option<super::Reconciling> {
        let runtime = self.mutation.clone()?;
        let mailbox = self.mailbox.clone();
        Some(Box::pin(async move {
            // A step failure continues the loop (managed `RunStepAsync`); only
            // fatal admission evidence stops the Service. The step publishes
            // through the read cadence every status projects
            // (`ReadSource::record_driver_mode`). A new suspend generation
            // is prepared first (managed `PrepareForLowPowerCoreAsync`).
            runtime.prepare_for_low_power(&mailbox).await?;
            runtime.maintain_driver_mode(&mailbox).await
        }))
    }
    fn process_power_source(&mut self) -> Pending<'_, ()> {
        Box::pin(async move {
            #[cfg(feature = "service-temporary-charge-v1")]
            if let (Some(runtime), Some(epoch)) = (&self.mutation, self.origin.power_epoch()) {
                let needed = match runtime.temporary_event_needed(epoch) {
                    Ok(needed) => needed,
                    Err(error) if super::thermal_storage::is_release_deferred(&error) => false,
                    Err(error) => return Err(error),
                };
                if let Some(update) = runtime.take_release_notice()? {
                    self.origin.verify()?;
                    self.publish_battery_update(update)?;
                }
                if needed {
                    self.origin.verify()?;
                    let evidence = self.inner.refresh().await?;
                    if let Some(evidence) = evidence {
                        let battery = evidence.power == Power::Battery;
                        let known_source = evidence.power != Power::Unknown;
                        // The SCM ingress already advanced the fence. Coalesce
                        // hints by epoch; metadata never authorizes a HID write.
                        if known_source {
                            runtime.temporary_event_observed(epoch);
                        }
                        self.inner.publish(Some(evidence))?;
                        if battery {
                            // The cancel rereads the epoch to tell a refusal
                            // caused by a later notice from a current one.
                            let update = match runtime
                                .battery_source_event(self.mailbox.clone(), epoch, &|| {
                                    self.origin.power_epoch()
                                })
                                .await
                            {
                                Ok(update) => update,
                                Err(error)
                                    if super::thermal_storage::is_release_deferred(&error) =>
                                {
                                    None
                                }
                                Err(error) => return Err(error),
                            };
                            if let Some(update) = update {
                                self.publish_battery_update(update)?;
                            }
                            if let Some(update) = runtime.take_release_notice()? {
                                self.publish_battery_update(update)?;
                            }
                        }
                    }
                    self.origin.verify()?;
                }
            }
            Ok(())
        })
    }

    #[cfg(feature = "service-thermal-mutation-v1")]
    fn mutate(
        &mut self,
        caller: Caller,
        sid: String,
        request: crate::service_status::StatusRequest,
        deadline: Instant,
    ) -> super::Mutation {
        let Some(runtime) = self.mutation.clone() else {
            return Box::pin(async {
                Ok((
                    super::thermal_mutation::Outcome::blocked(
                        "Signed mutation runtime is unavailable.",
                    ),
                    None,
                ))
            });
        };
        let mailbox = self.mailbox.clone();
        let publisher = self.inner.publisher.clone();
        Box::pin(async move {
            let Some(publisher) = publisher else {
                return Ok((
                    super::thermal_mutation::Outcome::blocked("Status is not initialized."),
                    None,
                ));
            };
            if let Some(battery) = request.battery_request() {
                let request_id = request.header().1.to_owned();
                let (outcome, update) = runtime
                    .apply_battery(mailbox.clone(), caller, sid, request_id, battery, deadline)
                    .await?;
                if let Some(update) = update {
                    // Only the battery/intent result is rebased after the gate.
                    // Preserve current unrelated facts, diagnostics and witness age.
                    let mut status = update.apply(publisher.borrow().active.clone());
                    status.set_observation_revision(
                        publisher
                            .borrow()
                            .active
                            .revision()
                            .checked_add(1)
                            .ok_or_else(|| invalid("status revision exhausted"))?,
                    );
                    publisher.send_replace(Arc::new(Publication {
                        inactive: status.inactive_mutation(),
                        active: status.clone(),
                    }));
                    return Ok((outcome, Some(status)));
                }
                return Ok((outcome, None));
            }
            match request {
                crate::service_status::StatusRequest::SetFanOverride {
                    request_id,
                    value,
                    settings,
                    ..
                } => {
                    let status = publisher.borrow().active.clone();
                    let (outcome, status) = runtime
                        .apply_fan(
                            mailbox.clone(),
                            super::thermal_mutation::FanRequest {
                                caller,
                                sid,
                                request: request_id,
                                value,
                                settings,
                                deadline,
                                status,
                                #[cfg(feature = "service-profile-triggers-v1")]
                                transition: None,
                            },
                        )
                        .await?;
                    if let Some(status) = status {
                        let mut status = status.rebase_thermal_mutation(&publisher.borrow().active);
                        if let super::mailbox_read::MailboxSource::Reader(reader) = &mailbox {
                            // The fan step projected its own policy; the drift
                            // its readback confirmed is published as every
                            // other status's is.
                            #[cfg(feature = "service-profile-triggers-v1")]
                            {
                                status = reader.with_drift(status)?;
                            }
                            reader.retain_mutation(&status);
                        }
                        status.set_observation_revision(
                            publisher
                                .borrow()
                                .active
                                .revision()
                                .checked_add(1)
                                .ok_or_else(|| invalid("status revision exhausted"))?,
                        );
                        publisher.send_replace(Arc::new(Publication {
                            inactive: status.inactive_mutation(),
                            active: status.clone(),
                        }));
                        Ok((outcome, Some(status)))
                    } else {
                        Ok((outcome, None))
                    }
                }
                #[cfg(not(feature = "service-profile-triggers-v1"))]
                crate::service_status::StatusRequest::ReportApplicationContext {
                    instance_id,
                    sequence,
                    executable_path,
                    ..
                } => Ok((
                    runtime.application(caller, sid, instance_id, sequence, executable_path),
                    None,
                )),
                #[cfg(feature = "service-profile-triggers-v1")]
                crate::service_status::StatusRequest::ReportApplicationContext {
                    request_id,
                    instance_id,
                    sequence,
                    executable_path,
                    ..
                } => {
                    let status = publisher.borrow().active.clone();
                    let (outcome, status) = runtime
                        .application(
                            mailbox.clone(),
                            caller,
                            sid,
                            request_id,
                            instance_id,
                            sequence,
                            executable_path,
                            deadline,
                            status,
                        )
                        .await?;
                    // Only a reconciliation the report triggered returns a
                    // status, published as a performance request's is.
                    let Some(status) = status else {
                        return Ok((outcome, None));
                    };
                    let status = publish_reconciled(&publisher, &mailbox, status)?;
                    Ok((outcome, Some(status)))
                }
                #[cfg(feature = "service-profiles-v1")]
                crate::service_status::StatusRequest::UpdateProfiles {
                    request_id,
                    profiles,
                    ..
                } => {
                    let Some(profiles) = profiles.profiles().cloned() else {
                        return Ok((
                            super::thermal_mutation::Outcome::blocked(
                                "Unsupported mutation operation.",
                            ),
                            None,
                        ));
                    };
                    let status = publisher.borrow().active.clone();
                    let (outcome, stored, reconciled) = runtime
                        .update_profiles(
                            mailbox.clone(),
                            caller,
                            sid,
                            request_id,
                            &profiles,
                            deadline,
                            status,
                        )
                        .await?;
                    if !stored {
                        return Ok((outcome, None));
                    }
                    // Managed publishes the saved set with its Stored reply.
                    // Start from the latest status, rebased with any
                    // reconciliation result, and project this user again.
                    let mut status = match reconciled {
                        Some(status) => status.rebase_thermal_mutation(&publisher.borrow().active),
                        None => publisher.borrow().active.clone(),
                    };
                    if let super::mailbox_read::MailboxSource::Reader(reader) = &mailbox {
                        status = reader.current_policy(status)?;
                        reader.retain_mutation(&status);
                    }
                    status.set_observation_revision(
                        publisher
                            .borrow()
                            .active
                            .revision()
                            .checked_add(1)
                            .ok_or_else(|| invalid("status revision exhausted"))?,
                    );
                    publisher.send_replace(Arc::new(Publication {
                        inactive: status.inactive_mutation(),
                        active: status.clone(),
                    }));
                    Ok((outcome, Some(status)))
                }
                crate::service_status::StatusRequest::SetPerformanceOverride {
                    request_id,
                    value,
                    ..
                } => {
                    let status = publisher.borrow().active.clone();
                    let (outcome, status) = runtime
                        .apply(
                            mailbox.clone(),
                            caller,
                            sid,
                            request_id,
                            value,
                            deadline,
                            status,
                        )
                        .await?;
                    if let Some(status) = status {
                        let mut status = status.rebase_thermal_mutation(&publisher.borrow().active);
                        if let super::mailbox_read::MailboxSource::Reader(reader) = &mailbox {
                            status = reader.current_policy(status)?;
                            reader.retain_mutation(&status);
                        }
                        status.set_observation_revision(
                            publisher
                                .borrow()
                                .active
                                .revision()
                                .checked_add(1)
                                .ok_or_else(|| invalid("status revision exhausted"))?,
                        );
                        publisher.send_replace(Arc::new(Publication {
                            inactive: status.inactive_mutation(),
                            active: status.clone(),
                        }));
                        Ok((outcome, Some(status)))
                    } else {
                        Ok((outcome, None))
                    }
                }
                _ => Ok((
                    super::thermal_mutation::Outcome::blocked("Unsupported mutation operation."),
                    None,
                )),
            }
        })
    }

    fn observe_performance(&mut self, caller: Caller, deadline: Instant) -> Observation {
        #[cfg(feature = "service-mailbox-observation-v1")]
        {
            if caller.access().ok() != Some(0) {
                return Box::pin(async { Ok(None) });
            }
            self.inner.publisher.as_ref().map_or_else(
                || Box::pin(async { Ok(None) }) as Observation,
                |published| {
                    self.mailbox.observe(
                        published.borrow().active.clone(),
                        deadline,
                        published.clone(),
                    )
                },
            )
        }
        #[cfg(not(feature = "service-mailbox-observation-v1"))]
        {
            let _ = (caller, deadline);
            let reason = "Firmware acquisition is unavailable in this installed metadata runtime.";
            Box::pin(async move { Err(io::Error::new(io::ErrorKind::PermissionDenied, reason)) })
        }
    }
    fn verify_origin(&self) -> io::Result<()> {
        self.origin.verify()
    }
    fn confirm_drained(&self) -> io::Result<()> {
        #[cfg(feature = "service-mailbox-observation-v1")]
        return self.mailbox.confirm_drained();
        #[cfg(not(feature = "service-mailbox-observation-v1"))]
        self.origin.confirm_metadata_drain()
    }
    fn publication_guard(&self) -> std::rc::Rc<dyn Fn() -> io::Result<()>> {
        let origin = self.origin.clone();
        std::rc::Rc::new(move || origin.verify())
    }
    fn snapshot(&mut self, caller: Caller, requested: Instant) -> Pending<'_, Snapshot> {
        Box::pin(async move {
            self.origin.verify()?;
            let value = self.inner.snapshot(caller, requested).await?;
            #[cfg(feature = "service-mailbox-observation-v1")]
            if let Some(publisher) = &self.inner.publisher {
                self.mailbox.decorate(
                    publisher,
                    self.inner
                        .metadata
                        .as_ref()
                        .ok_or_else(|| invalid("metadata not observed"))?
                        .clone(),
                )?;
                let access = caller.access()?;
                let current = publisher.borrow();
                let value = if access == 0 {
                    current.active.clone()
                } else {
                    current.inactive.clone()
                };
                self.origin.verify()?;
                return Ok(value);
            }
            self.origin.verify()?;
            Ok(value)
        })
    }
    fn publications(&self) -> Option<watch::Receiver<Arc<Publication>>> {
        self.inner.publications()
    }
    fn refresh_publications(&mut self) -> Pending<'_, Instant> {
        Box::pin(async move {
            self.origin.verify()?;
            // Expire presentation before a slow metadata observation can defer it.
            #[cfg(feature = "service-mailbox-observation-v1")]
            if let (Some(publisher), Some(metadata)) = (&self.inner.publisher, &self.inner.metadata)
            {
                self.mailbox.decorate(publisher, metadata.clone())?;
            }
            let due = self.inner.refresh_publications().await?;
            #[cfg(feature = "service-mailbox-observation-v1")]
            if let Some(publisher) = &self.inner.publisher {
                self.mailbox.decorate(
                    publisher,
                    self.inner
                        .metadata
                        .as_ref()
                        .ok_or_else(|| invalid("metadata not observed"))?
                        .clone(),
                )?;
            }
            self.origin.verify()?;
            #[cfg(feature = "service-mailbox-observation-v1")]
            let due = self
                .mailbox
                .next_refresh()
                .map_or(due, |expiry| expiry.min(due));
            Ok(due)
        })
    }
    fn drain(&mut self) -> Pending<'_, ()> {
        // Cleanup remains possible after revocation; retained origin and package
        // guards outlive listener/worker drain. It grants no firmware authority.
        Box::pin(async move {
            self.origin.normal_stop();
            let mut result = self.inner.drain().await;
            #[cfg(feature = "service-thermal-mutation-v1")]
            if let Some(runtime) = &self.mutation {
                // Managed shutdown order: the keyboard Driver-mode release,
                // then the fan handback. The keyboard's outcome is a diagnostic
                // (`Runtime::driver_mode_release`): only fatal admission
                // evidence fails the Stop, and nothing it does skips the fan
                // step. The first error is kept.
                #[cfg(feature = "service-keyboard-driver-mode-v1")]
                {
                    let keyboard = if self.origin.admit_owned_stop().is_ok() {
                        runtime.release_driver_mode(&self.mailbox).await
                    } else {
                        runtime.refuse_release_driver_mode();
                        Ok(())
                    };
                    if result.is_ok() {
                        result = keyboard;
                    }
                }
                let fans = if self.origin.admit_owned_stop().is_ok() {
                    runtime.stop_fans(&self.mailbox).await
                } else {
                    runtime.refuse_stop_fans()
                };
                if result.is_ok() {
                    result = fans;
                }
            }
            #[cfg(feature = "service-mailbox-observation-v1")]
            {
                let drained = self.mailbox.stop().await;
                if result.is_ok() {
                    result = drained;
                }
            }
            self.origin.revoke();
            result
        })
    }
}

#[cfg(not(test))]
fn read_windows(cancel: &AtomicBool) -> Evidence {
    use crate::discovery::{StatusHid, StatusMetadata, observe_status_metadata};
    use openblade_platform_windows::controller_presence::{
        self, DetectionWarning, Scan, process_inventory,
    };

    let power = match lifecycle::native::observe().power {
        lifecycle::PowerSource::Battery => Power::Battery,
        lifecycle::PowerSource::ExternalUnknown => Power::ExternalUnknown,
        lifecycle::PowerSource::Unknown => Power::Unknown,
    };
    let device = match observe_status_metadata(cancel) {
        Ok(StatusMetadata::Unsupported) => DeviceMetadata::Unsupported,
        Ok(StatusMetadata::MatchedHost { model, hid }) => DeviceMetadata::MatchedHost {
            model,
            hid: match hid {
                StatusHid::NoCandidate => HidMetadata::NoCandidate,
                StatusHid::Candidate => HidMetadata::Candidate,
                StatusHid::Ambiguous => HidMetadata::Ambiguous,
                StatusHid::Unavailable => HidMetadata::Unavailable,
            },
        },
        Ok(StatusMetadata::Unavailable) | Err(_) => DeviceMetadata::Unavailable,
    };
    if cancel.load(Ordering::Relaxed) {
        return Evidence {
            device,
            power,
            controllers: vec![],
            controller_warning: Some(CONTROLLER_UNKNOWN),
        };
    }
    let inventory = process_inventory::read();
    let rows = inventory.process_rows();
    let processes = if inventory.is_complete() {
        Scan::Complete(&rows)
    } else {
        Scan::Incomplete(&rows)
    };
    // Names can reveal possible controllers. No SCM or LampArray scan and
    // no image provenance means this source cannot establish their absence.
    let controllers = controller_presence::observe(
        processes,
        Scan::Incomplete(&[]),
        None,
        Scan::Incomplete(&[]),
        0,
    );
    let warning = match controllers.warning() {
        Some(DetectionWarning::ServiceScan) => {
            "Competing controller service observation is unavailable."
        }
        _ => CONTROLLER_UNKNOWN,
    };
    Evidence {
        device,
        power,
        controllers: controllers.potential_controllers().to_vec(),
        controller_warning: Some(warning),
    }
}

#[cfg(test)]
#[path = "status_source_tests.rs"]
mod tests;

#[cfg(test)]
pub(super) use tests::{in_memory_source, in_memory_source_with_firmware};

#[cfg(all(test, feature = "service-mailbox-observation-v1"))]
pub(super) use tests::in_memory_guarded_no_reader;

/// The injected source's metadata bit for production-like controller
/// evidence: no SCM scan, so a detection warning is always published.
#[cfg(all(test, feature = "service-thermal-mutation-v1"))]
pub(super) const PRODUCTION_CONTROLLERS: u8 = 0x10;

#[cfg(all(test, feature = "service-thermal-mutation-v1"))]
pub(super) fn injected_mutation_source(
    origin: Arc<dyn StatusOrigin>,
    mailbox: super::mailbox_read::MailboxSource,
    runtime: super::thermal_mutation::Runtime,
    metadata: Arc<std::sync::atomic::AtomicU8>,
    // Runs first on each status-observer thread.
    observer: Arc<dyn Fn() + Send + Sync>,
) -> Box<dyn StatusSource> {
    Box::new(GuardedStatus {
        origin,
        mailbox,
        mutation: Some(runtime),
        inner: ObservedStatus::new(move |_| {
            observer();
            // The low bits select the metadata; `PRODUCTION_CONTROLLERS`
            // adds the detection warning production evidence always carries.
            let metadata = metadata.load(std::sync::atomic::Ordering::Acquire);
            Evidence {
                device: if metadata & !PRODUCTION_CONTROLLERS == 3 {
                    DeviceMetadata::Unavailable
                } else {
                    DeviceMetadata::MatchedHost {
                        model: "Razer Blade 16 (2025) RZ09-0581".into(),
                        hid: HidMetadata::Candidate,
                    }
                },
                power: match metadata & !PRODUCTION_CONTROLLERS {
                    1 => Power::Battery,
                    4 => Power::Unknown,
                    _ => Power::ExternalUnknown,
                },
                controllers: vec![],
                controller_warning: (metadata & PRODUCTION_CONTROLLERS != 0)
                    .then_some(CONTROLLER_UNKNOWN),
            }
        }),
    })
}

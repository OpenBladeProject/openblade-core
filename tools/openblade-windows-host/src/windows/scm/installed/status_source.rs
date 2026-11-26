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
#[cfg(feature = "service-hyperboost-v1")]
use crate::service_status::HyperBoostStatus;

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

/// Whether managed's production factory would publish reviewed Fan Stop
/// evidence for this host: only the exact model it admits
/// (`FAN_STOP_EVIDENCE_MODEL`), whatever its HID metadata says.
fn has_fan_stop_evidence(device: Option<&DeviceMetadata>) -> bool {
    matches!(
        device,
        Some(DeviceMetadata::MatchedHost { model, .. })
            if model == crate::service_status::FAN_STOP_EVIDENCE_MODEL
    )
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
    /// Managed's HyperBoost status members, read with this evidence
    /// (`observe_hyperboost`).
    #[cfg(feature = "service-hyperboost-v1")]
    hyperboost: HyperBoostStatus,
}

#[derive(PartialEq, Eq)]
struct StatusFields {
    message: String,
    on_battery: bool,
    unsupported_laptop: bool,
    // The host matched the model with reviewed Fan Stop evidence.
    fan_stop_evidence: bool,
    controllers: Vec<String>,
    controller_warning: Option<&'static str>,
    #[cfg(feature = "service-hyperboost-v1")]
    hyperboost: HyperBoostStatus,
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
            self.close();
        }
        observed
    }

    /// One Windows power observation outside `observe_since`, as the
    /// lifecycle power-source pass and the temporary-charge source event make
    /// it: the observed power and whether its publication changed, or `None`
    /// when the refresh yields no evidence. A failure closes the feed as
    /// `observe_since` does.
    async fn observe_power(&mut self) -> io::Result<Option<(Power, bool)>> {
        let observed = async {
            let Some(evidence) = self.refresh().await? else {
                return Ok(None);
            };
            let power = evidence.power;
            Ok(Some((power, self.publish(Some(evidence))?)))
        }
        .await;
        if observed.is_err() {
            self.close();
        }
        observed
    }

    /// A fatal observer failure closes the feed before any other connection
    /// can reuse its last successful publication.
    fn close(&mut self) {
        self.read = None;
        self.observed_at = None;
        self.publisher = None;
    }

    /// Publishes the observed metadata; false when it repeats the last one.
    fn publish(&mut self, evidence: Option<Evidence>) -> io::Result<bool> {
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
        let fan_stop_evidence =
            has_fan_stop_evidence(evidence.as_ref().map(|current| &current.device));
        // The pending restore the last status reported stands until a
        // journal read establishes another (`HyperBoostStatus::resolved`).
        // Nothing observed establishes none of the members and keeps it,
        // or keeps none: an unreadable journal after that is still the
        // first answer.
        #[cfg(feature = "service-hyperboost-v1")]
        let hyperboost = {
            let prior = self
                .last
                .as_ref()
                .and_then(|(_, last)| last.hyperboost.restore_pending);
            match evidence.as_ref() {
                Some(current) => current.hyperboost.clone().resolved(prior),
                None => HyperBoostStatus {
                    restore_pending: prior,
                    ..HyperBoostStatus::UNOBSERVED
                },
            }
        };
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
            fan_stop_evidence,
            controllers,
            controller_warning,
            #[cfg(feature = "service-hyperboost-v1")]
            hyperboost,
        };
        // Keep one revision across caller projections. Unobservable evidence
        // changes (such as unknown versus unclassified external power) do not
        // create a new status revision.
        let revision = match &self.last {
            Some((_, last)) if *last == fields => return Ok(false),
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
            let status = Snapshot::observed_read_only(
                revision,
                access,
                fields.on_battery,
                fields.unsupported_laptop,
                fields.controllers.clone(),
                fields.controller_warning,
                message,
            )
            .with_fan_stop_evidence(fields.fan_stop_evidence);
            #[cfg(feature = "service-hyperboost-v1")]
            let status = status.with_hyperboost(&fields.hyperboost);
            status
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
        Ok(true)
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
        self.drain_by(Instant::now() + super::SOURCE_DRAIN_TIMEOUT)
    }
    fn drain_by(&mut self, deadline: Instant) -> Pending<'_, ()> {
        Box::pin(async move {
            self.begin_drain();
            let (observer, firmware) =
                tokio::join!(retire_observer(&mut self.refresh, deadline), async {
                    #[cfg(any(test, feature = "service-hardware-compile-check"))]
                    if let Some(firmware) = &self.firmware {
                        firmware.stop().await?;
                    }
                    Ok(())
                });
            observer.and(firmware)
        })
    }
}

impl ObservedStatus {
    /// The first step of a drain: nothing is published or observed again,
    /// and a running observation is told to stop. It waits for nothing.
    fn begin_drain(&mut self) {
        self.read = None;
        self.last = None;
        self.observed_at = None;
        self.publisher = None;
        if let Some(refresh) = &self.refresh {
            refresh.cancel.store(true, Ordering::Relaxed);
        }
    }
}

/// The reply of a drain whose observer was still running at its deadline.
const OBSERVER_RUNNING: &str = "The status observer did not stop before the drain deadline.";

/// Waits for the observation `begin_drain` cancelled to return, until
/// `deadline` at most.
///
/// The observer runs synchronous OS calls on its own thread and holds no
/// device handle, gate or journal, so nothing a drain does next depends on
/// it. A call that never returns is therefore reported, not waited for: the
/// drain fails, as an unconfirmed retirement always has, and the thread is
/// released with the process.
async fn retire_observer(refresh: &mut Option<Refresh>, deadline: Instant) -> io::Result<()> {
    let Some(running) = refresh.as_ref() else {
        return Ok(());
    };
    if timeout_at(deadline, running.wait()).await.is_err() {
        return Err(io::Error::new(io::ErrorKind::TimedOut, OBSERVER_RUNNING));
    }
    refresh
        .take()
        .map_or(Ok(()), |retired| retired.finish().map(drop))
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
        power_epoch: None,
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
        power_epoch: None,
        #[cfg(feature = "service-thermal-mutation-v1")]
        mutation: None,
        #[cfg(feature = "service-mailbox-observation-v1")]
        mailbox,
    })
}

pub(super) trait StatusOrigin: Send + Sync {
    // The lifecycle epoch: `process_power_source` observes Windows power once
    // per epoch, and the temporary-charge source events coalesce by it.
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
    /// The lifecycle epoch whose Windows power the published metadata last
    /// observed (`process_power_source`).
    power_epoch: Option<u64>,
    #[cfg(feature = "service-mailbox-observation-v1")]
    mailbox: super::mailbox_read::MailboxSource,
}
impl GuardedStatus {
    /// Managed classifies the new power source when its power-change handler
    /// starts, before the transition, and releasing a stale power-only write
    /// block publishes that source (`ChangePowerSourceCore`,
    /// `ReleasePowerOnlyWriteBlockForClassifiedSource`). Native holds no such
    /// block: each step reads 07/8C under its own fresh Windows power
    /// admission. The published source is what the two share. Status metadata
    /// otherwise follows Windows power only at the subscription refresh, so a
    /// lifecycle reconciliation on the same origin tick rebased the new
    /// destination on the previous source. Each lifecycle epoch (an SCM power,
    /// session or suspend notice) is therefore observed once, before the tick
    /// reconciles. `observed` says that the temporary-charge source event has
    /// already attempted this tick's observation. One attempt settles the
    /// epoch: a refresh that yields no evidence is not retried, because each
    /// attempt can hold the source lock for the refresh budget, and the 2 s
    /// subscription refresh publishes the source instead. The source admits
    /// no thermal write; the Agent's display automation and display-power
    /// admission act on it.
    async fn publish_power_source(&mut self, epoch: Option<u64>, observed: bool) -> io::Result<()> {
        if epoch.is_none() || epoch == self.power_epoch {
            return Ok(());
        }
        self.power_epoch = epoch;
        if observed {
            return Ok(());
        }
        self.origin.verify()?;
        // Unchanged metadata, as across a lock or a notice that keeps the
        // Windows power kind, publishes nothing.
        let Some((_, changed)) = self.inner.observe_power().await? else {
            return Ok(());
        };
        // As `refresh_publications` does after its observation: project the
        // retained thermal state and the user's policy over the new metadata.
        #[cfg(feature = "service-mailbox-observation-v1")]
        if changed
            && let (Some(publisher), Some(metadata)) = (&self.inner.publisher, &self.inner.metadata)
        {
            self.mailbox.decorate(publisher, metadata.clone())?;
        }
        #[cfg(not(feature = "service-mailbox-observation-v1"))]
        let _ = changed;
        self.origin.verify()
    }
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
/// A performance shortcut's status is published the same way.
#[cfg(any(
    feature = "service-profile-triggers-v1",
    feature = "service-fan-stop-v1",
    feature = "service-performance-shortcut-v1",
    feature = "service-hyperboost-v1"
))]
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
    publish_next(publisher, status)
}
/// Publishes the latest status projected with the Reader's held shortcut
/// preview. A preview reads nothing, so the Reader retains nothing: its read
/// witness and retention window stay those of the last real read.
#[cfg(feature = "service-performance-shortcut-v1")]
fn publish_preview(
    publisher: &watch::Sender<Arc<Publication>>,
    reader: &super::mailbox_read::ReadSource,
) -> io::Result<Snapshot> {
    let latest = publisher.borrow().active.clone();
    publish_next(publisher, reader.current_policy(latest)?)
}
/// Sends `status` with the revision after the latest publication's.
#[cfg(any(
    feature = "service-profile-triggers-v1",
    feature = "service-fan-stop-v1",
    feature = "service-performance-shortcut-v1",
    feature = "service-hyperboost-v1",
    feature = "service-lighting-v1"
))]
fn publish_next(
    publisher: &watch::Sender<Arc<Publication>>,
    mut status: Snapshot,
) -> io::Result<Snapshot> {
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
    #[cfg(all(
        feature = "service-diagnostics-export-v1",
        feature = "service-thermal-mutation-v1"
    ))]
    fn diagnostics(&self) -> crate::diagnostic_export::Sources {
        let mut sources = crate::diagnostic_export::Sources::service();
        if let Some(runtime) = &self.mutation {
            sources.history = runtime.diagnostic_history();
        }
        sources
    }
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
            // Then the convergence loop, which may ask for its own periodic
            // read (D5): outside any ObserveState, so it requests no live
            // reading.
            #[cfg(feature = "service-thermal-convergence-v1")]
            {
                let status = || publisher.borrow().active.clone();
                let converged = runtime.converge(&mailbox, status).await?;
                if let Some(status) = converged.snapshot {
                    publish_reconciled(&publisher, &mailbox, status)?;
                }
                if converged.observe {
                    let deadline =
                        Instant::now() + super::thermal_mutation::CONVERGENCE_OBSERVE_BUDGET;
                    let current = publisher.borrow().active.clone();
                    let observe = mailbox.observe(current, deadline, publisher.clone());
                    // As an ObserveState's read: bounded by its deadline, and
                    // a failed or late read is no reading, so the tick goes on
                    // carrying low-power and lifecycle work. Only fatal
                    // admission evidence ends the Service.
                    if let Ok(Err(error)) = timeout_at(deadline, observe).await
                        && super::thermal_mutation::is_fatal_admission(&error)
                    {
                        return Err(error);
                    }
                }
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
            let epoch = self.origin.power_epoch();
            // Whether the temporary-charge source event below already
            // attempted this tick's Windows power observation.
            #[cfg_attr(not(feature = "service-temporary-charge-v1"), allow(unused_mut))]
            let mut observed = false;
            #[cfg(feature = "service-temporary-charge-v1")]
            if let (Some(runtime), Some(epoch)) = (&self.mutation, epoch) {
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
                    // One observation per tick: with or without evidence, the
                    // power-source pass below does not refresh again.
                    observed = true;
                    if let Some((power, _)) = self.inner.observe_power().await? {
                        let battery = power == Power::Battery;
                        let known_source = power != Power::Unknown;
                        // The SCM ingress already advanced the fence. Coalesce
                        // hints by epoch; metadata never authorizes a HID write.
                        if known_source {
                            runtime.temporary_event_observed(epoch);
                        }
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
            self.publish_power_source(epoch, observed).await
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
                #[cfg(feature = "service-thermal-convergence-v1")]
                crate::service_status::StatusRequest::ReapplyChangedSettings {
                    request_id, ..
                } => {
                    let status = publisher.borrow().active.clone();
                    // The latest status, projected again with the ledger the
                    // rearm left, published before any step runs.
                    let republish = || {
                        let latest = publisher.borrow().active.clone();
                        publish_reconciled(&publisher, &mailbox, latest).map(drop)
                    };
                    let reapplied = runtime
                        .reapply_changed(
                            mailbox.clone(),
                            caller,
                            sid,
                            request_id,
                            deadline,
                            status,
                            &republish,
                        )
                        .await?;
                    let Some(status) = reapplied.snapshot else {
                        return Ok((reapplied.outcome, None));
                    };
                    let status = publish_reconciled(&publisher, &mailbox, status)?;
                    Ok((reapplied.outcome, Some(status)))
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
                #[cfg(feature = "service-profile-triggers-v1")]
                crate::service_status::StatusRequest::SetThermalControlPolicy { .. }
                | crate::service_status::StatusRequest::AcknowledgeThermalPolicyUpgradeDisclosure {
                    ..
                } => {
                    use super::thermal_storage::PolicyChange;
                    let change = match request {
                        crate::service_status::StatusRequest::SetThermalControlPolicy {
                            policy,
                            ..
                        } => PolicyChange::Set(policy),
                        _ => PolicyChange::Acknowledge,
                    };
                    let (outcome, stored) = runtime
                        .set_thermal_policy(mailbox.clone(), caller, sid, change)
                        .await?;
                    if !stored {
                        return Ok((outcome, None));
                    }
                    // Managed publishes the new convergence status with its
                    // Stored reply: project this user again on the latest.
                    let mut status = publisher.borrow().active.clone();
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
                #[cfg(feature = "service-fan-stop-v1")]
                crate::service_status::StatusRequest::SetFanStopMode { .. }
                | crate::service_status::StatusRequest::SetFanStopSettings { .. }
                | crate::service_status::StatusRequest::ResumeAutomaticFanStop { .. }
                | crate::service_status::StatusRequest::SetBatterySaverFanStop { .. } => {
                    use super::thermal_mutation::FanStopRequest;
                    use crate::service_status::StatusRequest as Request;
                    let request = match request {
                        Request::SetFanStopMode { mode, .. } => FanStopRequest::Mode(mode),
                        Request::SetFanStopSettings { settings, .. } => FanStopRequest::Settings(
                            crate::service_status::fan_stop::decode_settings(&settings)?,
                        ),
                        Request::SetBatterySaverFanStop { enabled, .. } => {
                            FanStopRequest::BatterySaver(enabled)
                        }
                        _ => FanStopRequest::Resume,
                    };
                    let message = publisher
                        .borrow()
                        .active
                        .fan_stop_message()
                        .map(str::to_owned);
                    let (outcome, changed) = runtime
                        .fan_stop(mailbox.clone(), caller, sid, request, message)
                        .await?;
                    if !changed {
                        return Ok((outcome, None));
                    }
                    // Managed publishes the saved settings, profile and
                    // automation state with its reply: project this user
                    // again on the latest status.
                    let latest = publisher.borrow().active.clone();
                    let status = publish_reconciled(&publisher, &mailbox, latest)?;
                    Ok((outcome, Some(status)))
                }
                // Managed `SetLightingOverrideAsync`, slice L1
                // (`Runtime::set_lighting`). Its status projects the stored
                // override and the lighting members this request read.
                #[cfg(feature = "service-lighting-v1")]
                crate::service_status::StatusRequest::SetLightingOverride { value, .. } => {
                    let status = publisher.borrow().active.clone();
                    let lit = runtime
                        .set_lighting(
                            mailbox.clone(),
                            super::thermal_mutation::LightingRequest {
                                caller,
                                sid,
                                value: value.value().cloned(),
                                deadline,
                                status,
                            },
                        )
                        .await?;
                    if !lit.publish {
                        return Ok((lit.outcome, None));
                    }
                    let mut status = publisher.borrow().active.clone();
                    if let super::mailbox_read::MailboxSource::Reader(reader) = &mailbox {
                        status = reader.current_policy(status)?;
                        if let Some(reading) = lit.reading {
                            let logo = reading.logo.map(super::thermal_mutation::logo_wire);
                            reader.record_lighting(reading.brightness, logo);
                            status = status.with_lighting_reading(reading.brightness, logo);
                        }
                    }
                    let status = publish_next(&publisher, status)?;
                    Ok((lit.outcome, Some(status)))
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
                #[cfg(feature = "service-performance-shortcut-v1")]
                crate::service_status::StatusRequest::CyclePerformanceShortcut {
                    request_id,
                    source,
                    ..
                } => {
                    // The codec admits only a defined source.
                    let source = super::thermal_mutation::ShortcutSource::from_wire(source)
                        .ok_or_else(|| invalid("performance shortcut source is undefined"))?;
                    let status = publisher.borrow().active.clone();
                    let (outcome, status) = runtime
                        .cycle_shortcut(
                            mailbox.clone(),
                            caller,
                            sid,
                            request_id,
                            source,
                            deadline,
                            status,
                        )
                        .await?;
                    let Some(status) = status else {
                        return Ok((outcome, None));
                    };
                    let status = publish_reconciled(&publisher, &mailbox, status)?;
                    Ok((outcome, Some(status)))
                }
                // Managed `SetHyperBoostCoreAsync`: one owned entry or exit
                // under the thermal gate (`Runtime::set_hyperboost`).
                #[cfg(feature = "service-hyperboost-v1")]
                crate::service_status::StatusRequest::SetHyperBoost {
                    request_id,
                    enabled,
                    ..
                } => {
                    let status = publisher.borrow().active.clone();
                    let (outcome, status) = runtime
                        .set_hyperboost(
                            mailbox.clone(),
                            caller,
                            sid,
                            request_id,
                            enabled,
                            deadline,
                            status,
                        )
                        .await?;
                    let Some(status) = status else {
                        return Ok((outcome, None));
                    };
                    let status = publish_reconciled(&publisher, &mailbox, status)?;
                    Ok((outcome, Some(status)))
                }
                // Managed `RecoverHyperBoostRestoreAsync`: native answers from
                // managed's ownership journal and writes nothing, so no
                // status follows (`Runtime::recover_hyperboost_restore`).
                #[cfg(feature = "service-hyperboost-v1")]
                crate::service_status::StatusRequest::RecoverHyperBoostRestore { .. } => {
                    Ok((runtime.recover_hyperboost_restore(caller).await?, None))
                }
                // Managed publishes the preview without the operation gate: it
                // stores no intent and reads or writes no firmware.
                #[cfg(feature = "service-performance-shortcut-v1")]
                crate::service_status::StatusRequest::SetPerformanceShortcutPreview {
                    session_id,
                    generation,
                    preset,
                    ..
                } => {
                    use super::thermal_mutation::Outcome;
                    if caller.logon != super::Logon::Interactive
                        || caller.control_session != Some(caller.session)
                    {
                        return Ok((
                            Outcome::blocked("The active interactive Windows user is required."),
                            None,
                        ));
                    }
                    let super::mailbox_read::MailboxSource::Reader(reader) = &mailbox else {
                        return Ok((
                            Outcome::blocked("Performance shortcut previews are unavailable."),
                            None,
                        ));
                    };
                    let (message, changed) =
                        reader.set_shortcut_preview(&session_id, generation, preset);
                    let outcome = Outcome {
                        status: 0,
                        message: message.into(),
                    };
                    if !changed {
                        return Ok((outcome, None));
                    }
                    let status = publish_preview(&publisher, reader)?;
                    Ok((outcome, Some(status)))
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
        self.drain_by(Instant::now() + super::SOURCE_DRAIN_TIMEOUT)
    }
    #[cfg_attr(not(feature = "service-thermal-mutation-v1"), allow(unused_variables))]
    fn drain_by(&mut self, deadline: Instant) -> Pending<'_, ()> {
        // Cleanup remains possible after revocation; retained origin and package
        // guards outlive listener/worker drain. It grants no firmware authority.
        Box::pin(async move {
            self.origin.normal_stop();
            // No convergence write is admitted once Stop begins.
            #[cfg(feature = "service-thermal-convergence-v1")]
            if let Some(runtime) = &self.mutation {
                runtime.stop_convergence();
            }
            // The observer is told to stop here and awaited last
            // (`retire_observer`). It holds no device handle or gate, so the
            // cleanup below does not depend on it, and an OS call stalled in
            // it must not take the time the keyboard release, the fan
            // handback and the HyperBoost exit are paced to.
            self.inner.begin_drain();
            #[cfg(any(test, feature = "service-hardware-compile-check"))]
            let mut result = match &self.inner.firmware {
                Some(firmware) => firmware.stop().await,
                None => Ok(()),
            };
            #[cfg(not(any(test, feature = "service-hardware-compile-check")))]
            let mut result = Ok(());
            #[cfg(feature = "service-thermal-mutation-v1")]
            if let Some(runtime) = &self.mutation {
                // Managed shutdown order: the keyboard Driver-mode release,
                // then the fan handback. The keyboard's outcome is a diagnostic
                // (`Runtime::driver_mode_release`): only fatal admission
                // evidence fails the Stop, and nothing it does skips the fan
                // step. The first error is kept. The fan handback is paced to
                // what is left of the drain (`deadline`), whose end cancels it.
                //
                // One limit is the mutation worker's (`Worker::stop`): the
                // release cancels a job still running, and every job step
                // checks that between its device and OS calls, a HyperBoost
                // request's pad scan included (`Permit::resolve`). A single
                // call that does not return still holds the job thread; the
                // fan handback then waits for it at `drain_active`, and the
                // drain ends at its caller's bound with the steps below
                // not run.
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
                    runtime.stop_fans(&self.mailbox, deadline).await
                } else {
                    runtime.refuse_stop_fans()
                };
                if result.is_ok() {
                    result = fans;
                }
                // Owned HyperBoost leaves last (`Runtime::stop_hyperboost`).
                // Managed exits it first (`PrepareForShutdownAsync`), on the
                // caller's budget, and then gives the keyboard and the fans
                // a separate reserve. Native has the one drain deadline, so
                // the exit goes after them and takes only what they left:
                // - an exit ahead of them could spend their time, and one
                //   that stopped after a setter leaves an unsettled row, at
                //   which the keyboard release and the fan handback both
                //   refuse (`Release::Unsettled`, "remains unresolved"). Last,
                //   no HyperBoost failure, refusal or timeout can skip either;
                // - a fan change OpenBlade still owns refuses every exit, and
                //   the handback closes it without a setter, since HyperBoost
                //   reads Automatic fans.
                // A fan or keyboard failure never skips the exit either: it
                // runs whatever they returned, and only an unsettled journal
                // row, which fences every write, stops it. (A job thread
                // held in one native call is the limit named above.)
                #[cfg(all(
                    feature = "service-hyperboost-v1",
                    feature = "service-profile-triggers-v1"
                ))]
                {
                    let hyperboost = if self.origin.admit_owned_stop().is_ok() {
                        runtime.stop_hyperboost(&self.mailbox, deadline).await
                    } else {
                        runtime.refuse_stop_hyperboost()
                    };
                    if result.is_ok() {
                        result = hyperboost;
                    }
                }
            }
            #[cfg(feature = "service-mailbox-observation-v1")]
            {
                let drained = self.mailbox.stop().await;
                if result.is_ok() {
                    result = drained;
                }
            }
            let observer = retire_observer(&mut self.inner.refresh, deadline).await;
            if result.is_ok() {
                result = observer;
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
            #[cfg(feature = "service-hyperboost-v1")]
            hyperboost: HyperBoostStatus::UNOBSERVED,
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
        #[cfg(feature = "service-hyperboost-v1")]
        hyperboost: observe_hyperboost(
            &device,
            &controllers,
            // The probes the HyperBoost requests read (`Runtime::native`),
            // composed only with the lifecycle triggers.
            #[cfg(all(not(test), feature = "service-profile-triggers-v1"))]
            Some(&super::thermal_mutation::Probes::native()),
            #[cfg(not(all(not(test), feature = "service-profile-triggers-v1")))]
            None,
            cancel,
        ),
        device,
        power,
        controllers: controllers.potential_controllers().to_vec(),
        controller_warning: Some(warning),
    }
}

/// Managed's HyperBoost status members from one metadata observation.
///
/// - The blocking controllers are this observation's own
///   (`Observation::hyperboost_blockers`), whatever the host.
/// - The pad and managed's ownership journal are read only on the host of
///   the model whose HyperBoost requests native serves
///   (`hyperboost::MODEL`), through the probes those requests read, and only
///   where the composition has them. Managed publishes the pad with a
///   verified device whose capabilities name a HyperBoost access
///   (`ResolvePadlessHyperBoostEligibility`); this status carries no such
///   capability, so the matched host stands in for it. Every other host, a
///   failed pad scan and a composition without probes report Unknown and no
///   pending restore.
/// - Both reads run on the observer's thread after its last other check of
///   `cancelled`, so neither starts once it is set, and the pad scan takes
///   the flag: a refresh past its budget and a drain both end the scan
///   instead of waiting for it. A journal that could not be read, or was
///   not read for the cancellation, establishes no pending restore either
///   way (`HyperBoostStatus::resolved`).
#[cfg(feature = "service-hyperboost-v1")]
fn observe_hyperboost(
    device: &DeviceMetadata,
    controllers: &openblade_platform_windows::controller_presence::Observation,
    probes: Option<&super::thermal_mutation::Probes>,
    cancelled: &AtomicBool,
) -> HyperBoostStatus {
    let probes = probes.filter(|_| {
        matches!(device, DeviceMetadata::MatchedHost { model, .. }
            if model == super::thermal_mutation::HYPERBOOST_MODEL)
    });
    let live = || !cancelled.load(Ordering::Relaxed);
    let (pad, restore_pending) = match probes {
        // Native serves no HyperBoost on this host and reads neither probe.
        None => (0, Some(false)),
        Some(probes) => (
            if live() {
                probes.eligibility(cancelled)
            } else {
                0
            },
            live().then(|| probes.restore_pending()).flatten(),
        ),
    };
    HyperBoostStatus {
        pad,
        restore_pending,
        blockers: Some(HyperBoostStatus::blocking(
            controllers.potential_controllers(),
            controllers.hyperboost_blockers(),
        )),
    }
}

#[cfg(test)]
#[path = "status_source_tests.rs"]
mod tests;

#[cfg(test)]
pub(super) use tests::{in_memory_source, in_memory_source_with_firmware};

#[cfg(all(test, feature = "service-mailbox-observation-v1"))]
pub(super) use tests::in_memory_guarded_no_reader;

/// A complete observation that found no controller.
#[cfg(all(test, feature = "service-hyperboost-v1"))]
fn no_controllers() -> openblade_platform_windows::controller_presence::Observation {
    use openblade_platform_windows::controller_presence::{Scan, observe};
    observe(
        Scan::Complete(&[]),
        Scan::Complete(&[]),
        None,
        Scan::Complete(&[]),
        0,
    )
}

/// The injected source's metadata bit for production-like controller
/// evidence: no SCM scan, so a detection warning is always published.
#[cfg(all(test, feature = "service-thermal-mutation-v1"))]
pub(super) const PRODUCTION_CONTROLLERS: u8 = 0x10;
/// The injected source's metadata bit that holds each observation until its
/// refresh budget expires, so the refresh yields no evidence.
#[cfg(all(test, feature = "service-thermal-mutation-v1"))]
pub(super) const OBSERVER_EXPIRES: u8 = 0x20;
/// The injected source's metadata bit that panics each observation, the
/// native observer failure.
#[cfg(all(test, feature = "service-thermal-mutation-v1"))]
pub(super) const OBSERVER_PANICS: u8 = 0x40;

/// The probes the injected source's metadata observer reads for the
/// HyperBoost status members, as `read_windows` reads the native ones.
#[cfg(all(test, feature = "service-hyperboost-v1"))]
pub(super) type InjectedHyperBoost = super::thermal_mutation::Probes;
#[cfg(all(
    test,
    feature = "service-thermal-mutation-v1",
    not(feature = "service-hyperboost-v1")
))]
pub(super) type InjectedHyperBoost = ();

#[cfg(all(test, feature = "service-thermal-mutation-v1"))]
pub(super) fn injected_mutation_source(
    origin: Arc<dyn StatusOrigin>,
    mailbox: super::mailbox_read::MailboxSource,
    runtime: super::thermal_mutation::Runtime,
    metadata: Arc<std::sync::atomic::AtomicU8>,
    // Runs first on each status-observer thread.
    observer: Arc<dyn Fn() + Send + Sync>,
    #[cfg_attr(not(feature = "service-hyperboost-v1"), allow(unused_variables))]
    hyperboost: InjectedHyperBoost,
) -> Box<dyn StatusSource> {
    Box::new(GuardedStatus {
        origin,
        mailbox,
        mutation: Some(runtime),
        power_epoch: None,
        inner: ObservedStatus::new(move |cancel| {
            observer();
            // The low bits select the metadata; `PRODUCTION_CONTROLLERS`
            // adds the detection warning production evidence always carries.
            let flags = metadata.load(std::sync::atomic::Ordering::Acquire);
            assert!(flags & OBSERVER_PANICS == 0, "injected observer failure");
            while flags & OBSERVER_EXPIRES != 0 && !cancel.load(Ordering::Acquire) {
                std::thread::park_timeout(Duration::from_millis(1));
            }
            let metadata = flags & !(OBSERVER_EXPIRES | OBSERVER_PANICS);
            let device = if metadata & !PRODUCTION_CONTROLLERS == 3 {
                DeviceMetadata::Unavailable
            } else {
                DeviceMetadata::MatchedHost {
                    model: "RZ09-0581".into(),
                    hid: HidMetadata::Candidate,
                }
            };
            Evidence {
                #[cfg(feature = "service-hyperboost-v1")]
                hyperboost: observe_hyperboost(
                    &device,
                    &no_controllers(),
                    Some(&hyperboost),
                    cancel,
                ),
                device,
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

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
            Some((revision, _)) => revision
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
    let mailbox = super::mailbox_read::ReadSource::acquire(origin.clone())?;
    Ok(Box::new(GuardedStatus {
        inner: ObservedStatus::new(read_windows),
        origin,
        #[cfg(feature = "service-mailbox-observation-v1")]
        mailbox,
    }))
}
#[cfg(not(test))]
struct GuardedStatus {
    inner: ObservedStatus,
    origin: Arc<crate::loaded_origin::Origin>,
    #[cfg(feature = "service-mailbox-observation-v1")]
    mailbox: super::mailbox_read::ReadSource,
}
#[cfg(not(test))]
impl StatusSource for GuardedStatus {
    fn observe_performance(&mut self, caller: Caller, deadline: Instant) -> Observation {
        #[cfg(feature = "service-mailbox-observation-v1")]
        {
            if caller.access().ok() != Some(0) {
                return Box::pin(async { Ok(None) });
            }
            return self.inner.publisher.as_ref().map_or_else(
                || Box::pin(async { Ok(None) }) as Observation,
                |published| {
                    self.mailbox.observe(
                        published.borrow().active.clone(),
                        deadline,
                        published.clone(),
                    )
                },
            );
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
                self.mailbox.decorate(publisher)?;
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
            let due = self.inner.refresh_publications().await?;
            #[cfg(feature = "service-mailbox-observation-v1")]
            if let Some(publisher) = &self.inner.publisher {
                self.mailbox.decorate(publisher)?;
            }
            self.origin.verify()?;
            Ok(due)
        })
    }
    fn drain(&mut self) -> Pending<'_, ()> {
        // Cleanup remains possible after revocation; retained origin and package
        // guards outlive listener/worker drain. It grants no firmware authority.
        Box::pin(async move {
            self.origin.revoke();
            self.inner.drain().await?;
            #[cfg(feature = "service-mailbox-observation-v1")]
            self.mailbox.stop().await?;
            Ok(())
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

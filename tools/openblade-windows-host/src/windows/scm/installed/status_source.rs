use std::{
    io,
    sync::{
        Arc,
        atomic::{AtomicBool, Ordering},
    },
    thread::{Builder, JoinHandle},
    time::Duration,
};

use tokio::time::{Instant, sleep, timeout_at};

use super::{Caller, Pending, Snapshot, StatusSource};
use crate::service_frame::invalid;

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
    refresh: Option<Refresh>,
    last: Option<(i64, StatusFields)>,
}

impl ObservedStatus {
    fn new(read: impl Fn(&AtomicBool) -> Evidence + Send + Sync + 'static) -> Self {
        Self {
            read: Some(Arc::new(read)),
            refresh: None,
            last: None,
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

    fn project(&mut self, access: i32, evidence: Option<Evidence>) -> io::Result<Snapshot> {
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
                format!("Matched {model} host ({detail}). Control ownership is unverified.")
            }
            Some(DeviceMetadata::Unavailable) | None => {
                "Device identity is unavailable. Control ownership is unverified.".to_owned()
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
            Some((revision, last)) if *last == fields => *revision,
            Some((revision, _)) => revision
                .checked_add(1)
                .ok_or_else(|| invalid("status revision exhausted"))?,
            None => 0,
        };
        let message = if access == 1 {
            "Another Windows session currently controls OpenBlade hardware.".to_owned()
        } else {
            fields.message.clone()
        };
        let status = Snapshot::observed_read_only(
            revision,
            access,
            fields.on_battery,
            fields.unsupported_laptop,
            fields.controllers.clone(),
            fields.controller_warning,
            message,
        );
        self.last = Some((revision, fields));
        Ok(status)
    }
}

impl StatusSource for ObservedStatus {
    fn snapshot(&mut self, caller: Caller) -> Pending<'_, Snapshot> {
        Box::pin(async move {
            let access = caller.access()?;
            let evidence = self.refresh().await?;
            self.project(access, evidence)
        })
    }

    fn drain(&mut self) -> Pending<'_, ()> {
        Box::pin(async move {
            self.read = None;
            self.last = None;
            if let Some(refresh) = &self.refresh {
                refresh.cancel.store(true, Ordering::Relaxed);
                refresh.wait().await;
            }
            if let Some(refresh) = self.refresh.take() {
                refresh.finish()?;
            }
            Ok(())
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
pub(super) fn native_status_source() -> Box<dyn StatusSource> {
    Box::new(ObservedStatus::new(read_windows))
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

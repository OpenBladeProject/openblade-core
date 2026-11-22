//! Read-only keyboard Driver-mode status, managed `KeyboardDriverModeActive`.
//!
//! Managed publishes the confirmed firmware mode, not ownership:
//! `ProductionLightingPort.MaintainDriverModeAsync` returns true for a device
//! already in Driver mode without claiming it, and the release publishes false
//! only after Normal reads back. This stage reads 00/84 and never writes, so the
//! published value is that read: true for Driver, false for Normal and null when
//! no valid reply was confirmed. A status for an unverified device is null.
//!
//! Managed reads once at startup and then every 15 s
//! (`ServicePeriodicObservationLoop.ObservationInterval`). The native Service
//! has no hardware loop: each fresh thermal observation is one serialized,
//! freshly admitted read job, and the query rides the first job once the last
//! result is 15 s old. Between queries a verified status repeats the last
//! result, as managed status does between its ticks. A result is bound to the
//! read admission it was read under, so any lifecycle, custody or generation
//! change makes it unknown until a new job reads it.
use crate::discovery::performance::{DeviceModeRead, ReadAdmission};
use openblade_protocol::device_mode::DeviceMode;
use std::{cell::Cell, time::Duration};
use tokio::time::Instant;

/// Managed `ServicePeriodicObservationLoop.ObservationInterval`.
pub(super) const INTERVAL: Duration = Duration::from_secs(15);

#[derive(Clone, Copy)]
struct Observed {
    admission: ReadAdmission,
    active: Option<bool>,
    at: Instant,
}

#[derive(Default)]
pub(super) struct Cadence(Cell<Option<Observed>>);

impl Cadence {
    /// Whether a read job under `admission` should also query 00/84.
    pub(super) fn due(&self, admission: Option<ReadAdmission>, now: Instant) -> bool {
        admission.is_some_and(|admission| self.current(admission, now).is_none())
    }

    /// Record what a job read under `admission`. A job that did not query
    /// leaves the last result in place; a failed query is unknown until the
    /// next interval, as managed publishes null until its next tick.
    pub(super) fn record(&self, admission: ReadAdmission, read: DeviceModeRead, now: Instant) {
        let active = match read {
            DeviceModeRead::NotRequested => return,
            DeviceModeRead::Unconfirmed => None,
            DeviceModeRead::Confirmed(mode) => Some(mode == DeviceMode::Driver),
        };
        self.0.set(Some(Observed {
            admission,
            active,
            at: now,
        }));
    }

    /// The value a verified status read under `admission` publishes.
    pub(super) fn active(&self, admission: ReadAdmission, now: Instant) -> Option<bool> {
        self.current(admission, now)?.active
    }

    fn current(&self, admission: ReadAdmission, now: Instant) -> Option<Observed> {
        self.0.get().filter(|observed| {
            observed.admission == admission && now.saturating_duration_since(observed.at) < INTERVAL
        })
    }

    #[cfg(test)]
    fn age_for_tests(&self, by: Duration) {
        if let Some(observed) = self.0.get() {
            self.0.set(Some(Observed {
                at: observed.at.checked_sub(by).unwrap(),
                ..observed
            }));
        }
    }
}

#[cfg(test)]
#[path = "driver_mode_tests.rs"]
mod tests;

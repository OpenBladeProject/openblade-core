//! Read-only keyboard Driver-mode status, managed `KeyboardDriverModeActive`
//! (`service-keyboard-driver-mode-read-v1`).
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
//! result is 15 s old. Until then, each fresh read under the same admission
//! publishes the cached result without reading it again. A result is bound to
//! the read admission it was read under, so any lifecycle, custody or
//! generation change makes it unknown until a new job reads it.
use crate::discovery::performance::{DeviceModeRead, ReadAdmission};
use openblade_protocol::device_mode::DeviceMode;
use std::{cell::Cell, time::Duration};
use tokio::time::Instant;

/// Managed `ServicePeriodicObservationLoop.ObservationInterval`.
pub(in super::super) const INTERVAL: Duration = Duration::from_secs(15);

#[derive(Clone, Copy)]
struct Observed {
    admission: ReadAdmission,
    active: Option<bool>,
    at: Instant,
}

#[derive(Default)]
pub(super) struct Cadence(Cell<Option<Observed>>);

impl Cadence {
    /// Whether a read job under `admission`, starting at `now`, should also
    /// query 00/84.
    pub(super) fn due(&self, admission: ReadAdmission, now: Instant) -> bool {
        self.current(admission, now).is_none()
    }

    /// Record what the job that started at `now` read under `admission`. A
    /// job that sent no query leaves the last result in place; a failed query
    /// is unknown until the next interval, as managed publishes null until its
    /// next tick.
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

    /// Whether any result is held.
    #[cfg(all(test, feature = "service-keyboard-driver-mode-v1"))]
    pub(super) fn recorded(&self) -> bool {
        self.0.get().is_some()
    }

    /// Dates the held result `by` earlier, as if that much time had passed.
    #[cfg(all(test, feature = "service-keyboard-driver-mode-v1"))]
    pub(super) fn age(&self, by: Duration) {
        if let Some(mut observed) = self.0.get() {
            observed.at = observed.at.checked_sub(by).unwrap_or(observed.at);
            self.0.set(Some(observed));
        }
    }

    /// The value that a verified status read under `admission` by the job
    /// that started at `now` publishes.
    pub(super) fn active(&self, admission: ReadAdmission, now: Instant) -> Option<bool> {
        self.current(admission, now)?.active
    }

    /// `active` for a result read by a job that started after `since`.
    #[cfg(feature = "service-keyboard-driver-mode-v1")]
    pub(super) fn active_since(
        &self,
        admission: ReadAdmission,
        now: Instant,
        since: Instant,
    ) -> Option<bool> {
        self.current(admission, now)
            .filter(|observed| observed.at > since)?
            .active
    }

    fn current(&self, admission: ReadAdmission, now: Instant) -> Option<Observed> {
        self.0.get().filter(|observed| {
            observed.admission == admission && now.saturating_duration_since(observed.at) < INTERVAL
        })
    }
}

#[cfg(test)]
#[path = "driver_mode_tests.rs"]
mod tests;

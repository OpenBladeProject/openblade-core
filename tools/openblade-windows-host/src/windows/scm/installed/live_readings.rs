//! Managed live readings and battery health on the installed read source
//! (`service-live-readings-v1`).
//!
//! Fan speeds are the capture-backed exact RZ09-0581 0D/88 pair
//! (`FanRpmRead`), sent only by a read job whose ObserveState asked for
//! `FanSpeeds`, on the same serialized, freshly admitted job and custody as
//! the thermal read.
//!
//! An unconfirmed pair quarantines the transaction ID it sent for the rest of
//! the process, and the pool is shared with every thermal read and mutation.
//! So after a pair whose first SET went out (`FanPairSends`, counted on the
//! worker as it sends) and that confirmed nothing, the pair is not sent again
//! for `FIRST_BACKOFF`, doubling per consecutive failure up to
//! `MAX_BACKOFF`; a confirmed pair ends the back-off. A job that sent no SET
//! (a failed thermal read, a worker that did not start, too little time, a
//! sealed Reader) leaves the pair due and the pause unchanged, whatever it
//! brought back. A pair sent by an observation that was dropped before its
//! result arrived counts when the next job is admitted.
//!
//! Managed publishes null after one failed pair. Here the last confirmed pair
//! under the same read admission stays published through `RETAINED_MISSES`
//! consecutive jobs that confirmed none (failed, backing off or not sent)
//! within `RETENTION_WINDOW` of its read. A pair seconds old is still the
//! last confirmed speed, and blanking it for one transient miss at the tray's
//! 2 s cadence would show a failure rather than the laptop (AGENTS.md rule 4).
//! A retained pair is that earlier confirmation, not a fresh one: it never
//! outlives the thermal read it was confirmed beside, never crosses another
//! admission, and any mutation drops it (`mutating`), because a write may
//! change the fans. An unreadable pair is never published as confirmed.
//!
//! Battery power flow and battery health are Windows reads with managed
//! semantics (`WindowsPowerStateProvider`): power flow is captured for an
//! ObserveState that asks for `BatteryPower` and is null on any failure;
//! health is captured once at the first status, again after the first
//! verified observation while it is null, and again on a power-source change
//! while it is null. A captured value is never cleared or recaptured.
//!
//! GPU temperature is managed live readings' NVML read behind its D0 gate,
//! with its workload, idle reuse and dwell (`gpu_temperature`), on a worker
//! of its own beside the job. CPU
//! temperature is not read here: the CPU temperature worker is unchanged, so
//! it stays null.
use super::RETENTION_WINDOW;
use crate::discovery::performance::{FanPairSends, FanRpmRead, ReadAdmission};
use std::{
    cell::{Cell, RefCell},
    io,
    rc::Rc,
    time::Duration,
};
use tokio::time::Instant;

#[cfg(windows)]
#[path = "live_readings_windows.rs"]
mod windows;
#[cfg(windows)]
pub(super) use windows::Windows;

/// The managed v91 codec refuses a fan speed above this.
const MAXIMUM_RPM: u16 = 20_000;
/// Consecutive unconfirmed fan reads that still publish the last pair.
const RETAINED_MISSES: u8 = 2;
/// The pause after an unconfirmed pair: the Driver-mode query's interval.
const FIRST_BACKOFF: Duration = Duration::from_secs(15);
/// The longest pause, so a pair that never answers costs one transaction ID
/// per five minutes: the 253-ID pool then lasts about 21 hours.
const MAX_BACKOFF: Duration = Duration::from_secs(300);
/// Managed `MaximumReasonableBatteryPowerMilliwatts`.
const MAXIMUM_BATTERY_POWER: i32 = 1_000_000;
/// The managed codec's battery capacity bound, in mWh.
const MAXIMUM_CAPACITY: i64 = 1_000_000;

/// The live readings the current observation asked for; none outside an
/// ObserveState, such as a trigger's read.
pub(super) fn requested() -> u8 {
    crate::service_status::requested_live_readings()
}

/// The Windows battery reads, injectable so tests never call Windows.
pub(super) trait Capture {
    /// `SystemBatteryState`: whether a battery is present and its raw rate,
    /// or None when the call failed.
    fn battery_state(&self) -> Option<(bool, u32)>;
    /// The (design, full charge) capacities of each battery that reports
    /// absolute capacity.
    fn battery_capacities(&self) -> io::Result<Vec<(u32, u32)>>;
}

/// No Windows battery source: every read is unavailable.
pub(super) struct Unavailable;
impl Capture for Unavailable {
    fn battery_state(&self) -> Option<(bool, u32)> {
        None
    }
    fn battery_capacities(&self) -> io::Result<Vec<(u32, u32)>> {
        Err(io::ErrorKind::Unsupported.into())
    }
}

/// Managed `WindowsPowerStateProvider.MapBatteryPowerFlow`: milliwatts into
/// (positive) or out of (negative) the battery, or null without a battery or
/// for the unknown or an unreasonable rate.
pub(super) fn map_battery_power_flow(present: bool, rate: u32) -> Option<i32> {
    let rate = rate as i32;
    (present
        && rate != i32::MIN
        && (-MAXIMUM_BATTERY_POWER..=MAXIMUM_BATTERY_POWER).contains(&rate))
    .then_some(rate)
}

/// Managed `WindowsPowerStateProvider.MapBatteryHealth`: the summed
/// (design, full charge) capacities of every battery that reports both, or
/// null when either sum is out of range or full charge exceeds twice design.
pub(super) fn map_battery_health(capacities: &[(u32, u32)]) -> Option<(i32, i32)> {
    let unknown = |capacity: u32| capacity == 0 || capacity == u32::MAX;
    let (design, full) = capacities
        .iter()
        .filter(|(design, full)| !unknown(*design) && !unknown(*full))
        .fold((0i64, 0i64), |(design, full), (d, f)| {
            (design + i64::from(*d), full + i64::from(*f))
        });
    let bounded = |total: i64| (1..=MAXIMUM_CAPACITY).contains(&total);
    (bounded(design) && bounded(full) && full <= design * 2).then_some((design as i32, full as i32))
}

#[derive(Clone, Copy)]
struct Fans {
    admission: ReadAdmission,
    rpm: [u16; 2],
    read: Instant,
    misses: u8,
}

/// Consecutive unconfirmed sent pairs, the instant the next may be sent, and
/// the sent pairs (`FanPairSends::count`) already accounted for.
#[derive(Clone, Copy, Default)]
struct Backoff {
    failures: u32,
    until: Option<Instant>,
    accounted: u64,
}

impl Backoff {
    /// The state after the pairs counted up to `sent`: a confirmed pair ends
    /// the pause, and otherwise a pair sent since the last account doubles
    /// it. With no new pair sent, an unconfirmed outcome changes nothing.
    fn account(self, sent: u64, confirmed: bool, now: Instant) -> Self {
        if confirmed {
            return Self {
                accounted: sent,
                ..Self::default()
            };
        }
        if sent == self.accounted {
            return self;
        }
        let failures = self.failures.saturating_add(1);
        let doublings = failures.saturating_sub(1).min(16);
        let pause = FIRST_BACKOFF
            .saturating_mul(1 << doublings)
            .min(MAX_BACKOFF);
        Self {
            failures,
            until: Some(now + pause),
            accounted: sent,
        }
    }
}

#[derive(Clone, Copy, Default)]
struct Health {
    started: bool,
    value: Option<(i32, i32)>,
    verification_retry: bool,
    power_source: Option<i32>,
}

/// The installed read source's live-reading state.
pub(super) struct Host {
    capture: Rc<dyn Capture>,
    fans: Cell<Option<Fans>>,
    sends: FanPairSends,
    backoff: Cell<Backoff>,
    health: RefCell<Health>,
}

impl Host {
    pub(super) fn new(capture: Rc<dyn Capture>) -> Self {
        Self {
            capture,
            fans: Cell::default(),
            sends: FanPairSends::default(),
            backoff: Cell::default(),
            health: RefCell::default(),
        }
    }

    /// Managed `CaptureBatteryPowerFlowMilliwatts`.
    pub(super) fn battery_power_flow(&self) -> Option<i32> {
        let (present, rate) = self.capture.battery_state()?;
        map_battery_power_flow(present, rate)
    }

    /// The counter an asked-for job passes to the worker to send the pair at
    /// `now`, or None while a back-off lasts. A pair an earlier, dropped
    /// observation sent is accounted for first, as unconfirmed.
    pub(super) fn fan_query(&self, now: Instant) -> Option<FanPairSends> {
        self.settle(FanRpmRead::NotRequested, now);
        let due = self.backoff.get().until.is_none_or(|until| now >= until);
        due.then(|| self.sends.clone())
    }

    /// Records what a job that was given the counter brought back:
    /// `NotRequested` when it brought back no reading. Only a pair it sent
    /// and did not confirm extends the back-off.
    pub(super) fn settle(&self, read: FanRpmRead, now: Instant) {
        let confirmed = matches!(read, FanRpmRead::Confirmed(_));
        let backoff = self.backoff.get();
        self.backoff
            .set(backoff.account(self.sends.count(), confirmed, now));
    }

    #[cfg(test)]
    pub(super) fn end_backoff_for_tests(&self) {
        self.backoff.set(Backoff {
            until: None,
            ..self.backoff.get()
        });
    }

    /// A mutation is about to submit: a pair read before it no longer
    /// describes the fans.
    #[cfg(feature = "service-thermal-mutation-v1")]
    pub(super) fn mutating(&self) {
        self.fans.set(None);
    }

    /// The fan speeds to publish for a job under `admission` that asked for
    /// them and read `read` (`NotRequested` when it did not send the pair).
    pub(super) fn fans(
        &self,
        admission: ReadAdmission,
        read: FanRpmRead,
        now: Instant,
    ) -> Option<[u16; 2]> {
        match read {
            FanRpmRead::Confirmed(rpm) if rpm.iter().all(|rpm| *rpm <= MAXIMUM_RPM) => {
                self.fans.set(Some(Fans {
                    admission,
                    rpm,
                    read: now,
                    misses: 0,
                }));
                Some(rpm)
            }
            _ => self.missed(Some(admission), now),
        }
    }

    /// One asked-for fan read that confirmed nothing. A job that brought
    /// back no reading has no admission to compare: it counts as a miss.
    pub(super) fn missed(
        &self,
        admission: Option<ReadAdmission>,
        now: Instant,
    ) -> Option<[u16; 2]> {
        let mut fans = self.fans.take()?;
        fans.misses = fans.misses.saturating_add(1);
        let retained = fans.misses <= RETAINED_MISSES
            && now.saturating_duration_since(fans.read) < RETENTION_WINDOW
            && admission.is_none_or(|admission| admission == fans.admission);
        if retained {
            self.fans.set(Some(fans));
        }
        retained.then_some(fans.rpm).filter(|_| admission.is_some())
    }

    /// Managed battery health: the first call captures it, as the managed
    /// initial snapshot does; later calls capture it again while it is null
    /// and the power source changed.
    pub(super) fn power_source(&self, source: i32) {
        let mut health = self.health.borrow_mut();
        if !health.started {
            health.started = true;
            health.value = self.capture_health();
            health.verification_retry = health.value.is_none();
        } else if health.power_source.is_some_and(|known| known != source) && health.value.is_none()
        {
            health.value = self.capture_health();
        }
        health.power_source = Some(source);
    }

    /// Managed `CaptureBatteryHealthAfterFirstVerificationCore`: one more
    /// capture after the first verified observation, while health is null.
    pub(super) fn verified(&self) {
        let mut health = self.health.borrow_mut();
        if !health.started {
            // No status preceded this observation: it is both captures.
            health.started = true;
            health.value = self.capture_health();
        } else if health.verification_retry && health.value.is_none() {
            health.value = self.capture_health();
        }
        health.verification_retry = false;
    }

    pub(super) fn battery_health(&self) -> Option<(i32, i32)> {
        self.health.borrow().value
    }

    // Managed logs a failed capture; its capacity stays unavailable.
    fn capture_health(&self) -> Option<(i32, i32)> {
        map_battery_health(&self.capture.battery_capacities().ok()?)
    }
}

#[cfg(test)]
#[path = "live_readings_tests.rs"]
mod tests;

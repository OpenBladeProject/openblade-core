//! Policy for the explicitly selected Windows-hook SnapTap backend.
//! The native adapter is compiled on Windows and has no product caller.

use std::panic::{AssertUnwindSafe, catch_unwind};

const ADMISSION_REFRESH_MS: u64 = 250;
const ADMISSION_MAX_AGE_MS: u64 = 500;
const TICKS_PER_MS: u64 = 10_000;

// The exact strings admitted by RazerSnapTapDriverProfiles.Find at d6b003ca.
// This does not admit a driver, keyboard collection, or other model capability.
const PRODUCTS: [&str; 7] = [
    "Blade 16 - RZ09-0581",
    "Blade 16 - RZ09-0528",
    "Blade 16 - RZ09-0483",
    "Blade 14 - RZ09-0482",
    "Blade 14 - RZ09-0530",
    "Blade 15 Base Model (Early 2020) - RZ09-0328",
    "Blade 18 - RZ09-0484",
];

pub struct Admission<'a> {
    pub bios_manufacturer: &'a str,
    pub bios_product: &'a str,
    pub system_manufacturer: &'a str,
    pub system_product: &'a str,
    pub active_console: bool,
    pub controllers_complete: bool,
    pub competing_controller: bool,
}

impl Admission<'_> {
    pub fn rejection(&self) -> Option<Reason> {
        if self.bios_manufacturer != "Razer"
            || self.system_manufacturer != "Razer"
            || self.bios_product != self.system_product
            || !PRODUCTS.contains(&self.bios_product)
        {
            Some(Reason::UnsupportedModel)
        } else if !self.active_console {
            Some(Reason::InactiveConsoleSession)
        } else if !self.controllers_complete {
            Some(Reason::IncompleteControllerObservation)
        } else if self.competing_controller {
            Some(Reason::CompetingController)
        } else {
            None
        }
    }
}

/// Observations supplied by the owner. This fixture never reads or writes a journal.
pub struct OwnershipAdmission {
    pub driver_marker_clear: bool,
    pub windows_marker_acquired: bool,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Reason {
    UnsupportedModel,
    InactiveConsoleSession,
    IncompleteControllerObservation,
    CompetingController,
    KeyboardSpeedUnavailable,
    DriverOwnershipRecoveryRequired,
    WindowsOwnershipRecoveryRequired,
    AdmissionExpired,
    ForegroundOrLayoutChanged,
    HeldOutputUncertain,
    OutputAdmissionRejected,
    OutputCountMismatch,
    OutputFailed,
    HookCallbackFailed,
    HookLoopFailed,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Failure {
    pub reason: Reason,
    pub output_uncertain: bool,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Status {
    Paused,
    WaitingForRelease,
    Armed,
    Active,
    Failed,
    Off,
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct Context {
    pub foreground: usize,
    pub layout: usize,
}

impl Context {
    fn valid(self) -> bool {
        self.foreground != 0 && self.layout != 0
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Key {
    A,
    D,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Stroke {
    pub key: Key,
    pub down: bool,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Batch {
    One(Stroke),
    Pair(Stroke, Stroke),
}

impl Batch {
    pub const fn count(self) -> usize {
        match self {
            Self::One(_) => 1,
            Self::Pair(..) => 2,
        }
    }
}

#[derive(Clone, Copy)]
pub struct Observation {
    /// Monotonic time in 100 ns ticks; shared by callback, timer, and sink.
    pub at: u64,
    pub context: Context,
    pub active_console: bool,
    pub stop_requested: bool,
}

#[derive(Clone, Copy)]
pub enum Submission {
    Count { accepted: usize, completed_at: u64 },
    Uncertain,
}

/// A fresh observation immediately precedes each submission of at most two strokes.
pub trait Output {
    fn observe(&mut self) -> Observation;
    fn submit(&mut self, batch: Batch) -> Submission;
}

pub struct HookEvent {
    pub scan: u32,
    pub flags: u32,
    pub message: usize,
}

impl HookEvent {
    fn stroke(&self) -> Option<Stroke> {
        if self.flags & 1 != 0 {
            return None;
        }
        let key = match self.scan {
            0x1e => Key::A,
            0x20 => Key::D,
            _ => return None,
        };
        let down = match self.message {
            0x100 | 0x104 => true,
            0x101 | 0x105 => false,
            _ => return None,
        };
        Some(Stroke { key, down })
    }
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct Decision {
    pub handled: bool,
    pub suppress: bool,
}

#[derive(Clone, Copy)]
struct Repeat {
    started: u64,
    due: u64,
    emitted: bool,
}

pub struct SnapTap {
    target: Context,
    checked_at: u64,
    cadence: u64,
    older: Option<Key>,
    newer: Option<Key>,
    winner: Option<Key>,
    repeat: Option<Repeat>,
    enabled: bool,
    confirmed_input: bool,
    failure: Option<Failure>,
    stopped: bool,
}

impl SnapTap {
    /// Startup is driven by synthetic observations. None means canceled before arming.
    pub fn start(
        at: u64,
        admission: &Admission<'_>,
        keyboard_speed: Option<u32>,
        stop_requested: bool,
        ownership: OwnershipAdmission,
    ) -> Result<Option<Self>, Reason> {
        if !ownership.driver_marker_clear {
            return Err(Reason::DriverOwnershipRecoveryRequired);
        }
        if let Some(reason) = admission.rejection() {
            return Err(reason);
        }
        let speed = keyboard_speed
            .filter(|speed| *speed <= 31)
            .ok_or(Reason::KeyboardSpeedUnavailable)?;
        if stop_requested {
            return Ok(None);
        }
        if !ownership.windows_marker_acquired {
            return Err(Reason::WindowsOwnershipRecoveryRequired);
        }
        Ok(Some(Self {
            target: Context::default(),
            checked_at: at,
            cadence: (10_000_000.0 / (2.5 + 27.5 * f64::from(speed) / 31.0)) as u64,
            older: None,
            newer: None,
            winner: None,
            repeat: None,
            enabled: false,
            confirmed_input: false,
            failure: None,
            stopped: false,
        }))
    }

    pub fn status(&self) -> Status {
        if self.failure.is_some() {
            Status::Failed
        } else if self.stopped {
            Status::Off
        } else if self.enabled {
            if self.confirmed_input {
                Status::Active
            } else {
                Status::Armed
            }
        } else if self.target.valid() {
            Status::WaitingForRelease
        } else {
            Status::Paused
        }
    }

    pub fn winner(&self) -> Option<Key> {
        self.winner
    }

    pub fn failure(&self) -> Option<Failure> {
        self.failure
    }

    fn live(&self) -> bool {
        !self.stopped && self.failure.is_none()
    }

    pub fn refresh_due(&self, at: u64) -> bool {
        self.live()
            && at
                .checked_sub(self.checked_at)
                .is_some_and(|age| age >= ADMISSION_REFRESH_MS * TICKS_PER_MS)
    }

    /// The future message pump supplies a newly captured snapshot when refresh is due.
    pub fn refresh(&mut self, at: u64, admission: &Admission<'_>) {
        if self.refresh_due(at) {
            self.checked_at = at;
            if let Some(reason) = admission.rejection() {
                self.fail(reason, false);
            }
        }
    }

    fn current(&self, observation: Observation) -> bool {
        observation.active_console
            && observation
                .at
                .checked_sub(self.checked_at)
                .is_some_and(|age| age <= ADMISSION_MAX_AGE_MS * TICKS_PER_MS)
    }

    fn context_matches(&self, context: Context) -> bool {
        context.valid() && context == self.target
    }

    fn observe_context(&mut self, context: Context) -> bool {
        if !self.live() {
            return false;
        }
        if !self.context_matches(context) {
            if self.winner.is_some() {
                self.fail(Reason::ForegroundOrLayoutChanged, true);
                return false;
            }
            self.enabled = false;
            self.repeat = None;
            self.target = context;
            self.confirmed_input = false;
        }
        context.valid()
    }

    /// Both release observations must be true: the global virtual-key sweep and
    /// the layout-specific A/D scan mapping. Disabled physical edges also count.
    pub fn reconcile(
        &mut self,
        context: Context,
        all_virtual_keys_released: bool,
        mapped_ad_released: bool,
    ) {
        if self.observe_context(context)
            && !self.enabled
            && self.older.is_none()
            && self.newer.is_none()
            && all_virtual_keys_released
            && mapped_ad_released
        {
            self.enabled = true;
        }
    }

    pub fn on_key(
        &mut self,
        observation: Observation,
        event: Option<HookEvent>,
        sink: &mut impl Output,
    ) -> Decision {
        let Some(event) = event else {
            // Negative hook code or no payload.
            return Decision::default();
        };
        if !self.live() || observation.stop_requested || event.flags & 0x12 != 0 {
            return Decision::default();
        }
        // Like the managed callback, check admission before filtering unrelated physical keys.
        if !self.current(observation) {
            self.fail(Reason::AdmissionExpired, false);
            return Decision::default();
        }
        let Some(stroke) = event.stroke() else {
            return Decision::default();
        };
        let context = self.observe_context(observation.context);
        if !self.live() {
            return Decision::default();
        }
        let held = self.track(stroke);
        if !context || !self.enabled {
            return Decision::default();
        }
        let result = self.decide(stroke, held, sink);
        self.confirmed_input |= result.handled;
        result
    }

    fn track(&mut self, stroke: Stroke) -> bool {
        let key = Some(stroke.key);
        let held = self.older == key || self.newer == key;
        if stroke.down && !held {
            if self.older.is_none() {
                self.older = key;
            } else {
                self.newer = key;
            }
        } else if !stroke.down && held {
            if self.older == key {
                self.older = self.newer.take();
            } else {
                self.newer = None;
            }
        }
        held
    }

    fn decide(&mut self, stroke: Stroke, held: bool, sink: &mut impl Output) -> Decision {
        let desired = self.newer.or(self.older);
        let key = Some(stroke.key);
        if desired == self.winner {
            let suppress = if stroke.down && key == self.winner {
                self.repeat.take().is_some_and(|repeat| repeat.emitted)
            } else {
                stroke.down && held && desired != key
            };
            return Decision {
                handled: true,
                suppress,
            };
        }

        self.repeat = None;
        let release = self.winner.map(|key| Stroke { key, down: false });
        if stroke.down {
            // The fresh physical down retains its original device identity.
            if let Some(release) = release
                && self
                    .emit(Batch::One(release), desired, false, sink)
                    .is_none()
            {
                return Decision {
                    handled: false,
                    suppress: true,
                };
            }
            self.winner = desired;
            return Decision {
                handled: true,
                suppress: false,
            };
        }

        if let (Some(release), Some(key)) = (release, desired) {
            let restored = self.emit(
                Batch::Pair(release, Stroke { key, down: true }),
                desired,
                false,
                sink,
            );
            if let Some(started) = restored {
                self.repeat = Some(Repeat {
                    started,
                    due: self.cadence,
                    emitted: false,
                });
            }
            return Decision {
                handled: restored.is_some(),
                suppress: restored.is_some(),
            };
        }

        self.winner = None;
        Decision {
            handled: true,
            suppress: false,
        }
    }

    /// Call only for the owned timer, after reconciliation on the message pump.
    pub fn repeat(&mut self, at: u64, stop_requested: bool, sink: &mut impl Output) {
        let (Some(mut repeat), Some(key)) = (self.repeat, self.winner) else {
            return;
        };
        if stop_requested || !self.live() || !self.enabled {
            return;
        }
        let Some(elapsed) = at.checked_sub(repeat.started) else {
            return;
        };
        if elapsed < repeat.due {
            return;
        }
        // Keep timer phase but do not emit a burst after missed deadlines.
        repeat.due = (elapsed - elapsed % self.cadence).saturating_add(self.cadence);
        if self
            .emit(
                Batch::One(Stroke { key, down: true }),
                Some(key),
                false,
                sink,
            )
            .is_some()
        {
            repeat.emitted = true;
            self.repeat = Some(repeat);
        }
    }

    pub fn stop(&mut self, observation: Observation, sink: &mut impl Output) {
        if !self.live() {
            return;
        }
        self.repeat = None;
        self.enabled = false;
        if let Some(key) = self.winner {
            if !observation.active_console || !self.context_matches(observation.context) {
                self.fail(Reason::HeldOutputUncertain, true);
            } else {
                // Cancellation permits this one owned release; the fresh sink
                // admission and focus/layout checks still apply.
                self.emit(Batch::One(Stroke { key, down: false }), None, true, sink);
            }
        }
        self.stopped = true;
    }

    fn emit(
        &mut self,
        batch: Batch,
        desired: Option<Key>,
        stopping: bool,
        sink: &mut impl Output,
    ) -> Option<u64> {
        // A sink failure may follow a submission. Contain an unwind here so
        // the owner cannot resume or clean up an ambiguous session.
        let result = catch_unwind(AssertUnwindSafe(|| {
            let fresh = sink.observe();
            if !self.current(fresh)
                || !self.context_matches(fresh.context)
                || (!stopping && fresh.stop_requested)
            {
                return None;
            }
            Some(sink.submit(batch))
        }));
        match result {
            Ok(None) => {
                self.fail(Reason::OutputAdmissionRejected, false);
                None
            }
            Ok(Some(Submission::Count {
                accepted,
                completed_at,
            })) if accepted == batch.count() => {
                self.winner = desired;
                Some(completed_at)
            }
            Ok(Some(Submission::Count { .. })) => {
                self.fail(Reason::OutputCountMismatch, true);
                None
            }
            Ok(Some(Submission::Uncertain)) | Err(_) => {
                self.fail(Reason::OutputFailed, true);
                None
            }
        }
    }

    fn fail(&mut self, reason: Reason, may_have_submitted: bool) {
        self.failure.get_or_insert(Failure {
            reason,
            output_uncertain: may_have_submitted || self.winner.is_some(),
        });
        self.winner = None;
        self.repeat = None;
        self.enabled = false;
    }
}

#[cfg(any(windows, test))]
mod adapter;

#[cfg(test)]
mod tests;

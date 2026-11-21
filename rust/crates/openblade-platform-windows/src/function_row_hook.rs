//! Current-user WindowsHook adapter. The host supplies verified identity, Fn
//! status and ownership observations, and executes typed actions off-thread.
//! The low-level hook has desktop scope. An exact host observation admits the
//! managed backend, while the callback packet itself has no device identity.

use std::{
    sync::{
        Arc, Mutex, OnceLock,
        atomic::{AtomicBool, AtomicU64, AtomicUsize, Ordering::SeqCst},
        mpsc::{Receiver, SyncSender, sync_channel},
    },
    time::{Duration, Instant},
};

use openblade_domain::function_row::{
    Backend, Device, Event, Lifecycle, Observation, Phase, PrimaryMode, Status, Stroke, WindowsHook,
};

pub mod executor;
mod native;
#[cfg(test)]
mod tests;

const ACTION_CAPACITY: usize = 32;
const DRAIN_LIMIT: Duration = Duration::from_millis(250);
static OWNED: AtomicBool = AtomicBool::new(false);

/// Proofs supplied by the current-user host before claiming this backend.
/// The startup guard must have checked the driver journal outside the hook.
#[derive(Clone, Copy, Debug, Default)]
pub struct StartupAdmission {
    pub requested_backend: Option<Backend>,
    pub local_session_confirmed: bool,
    pub startup_recovery_clear: bool,
    pub driver_can_transfer: bool,
    pub previous_windows_hook_retired: bool,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum HookError {
    StartupRefused,
    Cancelled,
    AlreadyOwned,
    WakeEvent(u32),
    Install(u32),
    MessageLoop(MessageLoopError),
    Unhook {
        code: u32,
        message_loop: Option<MessageLoopError>,
    },
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum MessageLoopError {
    Native(u32),
    UnexpectedQuit,
}

/// Sticky evidence from this run. A stopped thread alone cannot prove handoff.
#[derive(Clone, Copy, Debug)]
pub enum Recovery {
    DeliveryLost = 1,
    ReleaseLost = 2,
    OutputUncertain = 4,
    KeysHeld = 8,
    UnhookUnconfirmed = 16,
    MessageLoopFailed = 32,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ActionFailure {
    NotSubmitted,
    PossiblySubmitted,
}

/// Pack the Windows-only projection and its expiry into one atomic word.
/// Relative microseconds retain a coherent deadline without a reader lock.
struct AdmissionSnapshot {
    origin: Instant,
    word: AtomicU64,
}

const OBSERVATION_BITS: u32 = 8;
const BACKEND_BIT: u64 = 1 << 2;
const DRIVER_MODE_BIT: u64 = 1 << 3;
const FN_SHIFT: u32 = 4;
const PRIMARY_SHIFT: u32 = 6;
const TWO_BITS: u64 = 0b11;

impl Default for AdmissionSnapshot {
    fn default() -> Self {
        Self {
            origin: Instant::now(),
            word: AtomicU64::new(0),
        }
    }
}

impl AdmissionSnapshot {
    fn ticks(&self, instant: Instant) -> u64 {
        let ticks = instant.saturating_duration_since(self.origin).as_micros();
        ticks.min(u128::from(u64::MAX >> OBSERVATION_BITS)) as u64
    }

    fn publish(&self, observation: Observation, valid_until: Instant) {
        let device = match observation.exact_device {
            Some(Device::Rz09_0581) => 1,
            Some(Device::Rz09_0528) => 2,
            _ => 0,
        };
        let backend = if observation.selected_backend == Some(Backend::WindowsHook) {
            BACKEND_BIT
        } else {
            0
        };
        let driver_mode = if observation.status == (Status::FirmwareDriverMode { active: true }) {
            DRIVER_MODE_BIT
        } else {
            0
        };
        let function = observation
            .fn_pressed
            .map_or(0, |pressed| 1 + u64::from(pressed));
        let primary = match observation.primary_mode {
            Some(PrimaryMode::FunctionKeys) => 1,
            Some(PrimaryMode::MultimediaKeys) => 2,
            None => 0,
        };
        let word = (self.ticks(valid_until) << OBSERVATION_BITS)
            | device
            | backend
            | driver_mode
            | (function << FN_SHIFT)
            | (primary << PRIMARY_SHIFT);
        self.word.store(word, SeqCst);
    }

    fn read(&self, now: Instant) -> Observation {
        let word = self.word.load(SeqCst);
        if self.ticks(now) >= word >> OBSERVATION_BITS {
            return UNKNOWN;
        }
        Observation {
            exact_device: match word & TWO_BITS {
                1 => Some(Device::Rz09_0581),
                2 => Some(Device::Rz09_0528),
                _ => None,
            },
            selected_backend: (word & BACKEND_BIT != 0).then_some(Backend::WindowsHook),
            status: if word & DRIVER_MODE_BIT != 0 {
                Status::FirmwareDriverMode { active: true }
            } else {
                Status::Unknown
            },
            fn_pressed: match (word >> FN_SHIFT) & TWO_BITS {
                1 => Some(false),
                2 => Some(true),
                _ => None,
            },
            primary_mode: match (word >> PRIMARY_SHIFT) & TWO_BITS {
                1 => Some(PrimaryMode::FunctionKeys),
                2 => Some(PrimaryMode::MultimediaKeys),
                _ => None,
            },
            lifecycle: Lifecycle::Active,
        }
    }
}

#[derive(Default)]
struct Shared {
    admission: AdmissionSnapshot,
    wake: Mutex<Option<Arc<native::Wake>>>,
    stop: AtomicBool,
    recovery: AtomicUsize,
    pending: AtomicUsize,
    retired: AtomicBool,
    owns_slot: AtomicBool,
    handoff_ready: AtomicBool,
    slot: OnceLock<&'static AtomicBool>,
    consumer_ready: AtomicBool,
    worker_pending: AtomicBool,
    cancel_actions: AtomicBool,
}

impl Shared {
    fn notify(&self) {
        // The callback may record a delivery failure. If another sender
        // holds the lock, its wake covers this caller's sticky change too.
        if let Ok(wake) = self.wake.try_lock()
            && let Some(wake) = wake.as_ref()
        {
            wake.signal();
        }
    }

    fn flag(&self, reason: Recovery) {
        if self.recovery.fetch_or(reason as usize, SeqCst) == 0 {
            self.notify();
        }
    }

    fn finish(&self) {
        self.retired.store(true, SeqCst);
        self.release_slot_if_safe();
    }

    fn release_slot_if_safe(&self) {
        const BLOCKERS: usize = Recovery::ReleaseLost as usize
            | Recovery::OutputUncertain as usize
            | Recovery::KeysHeld as usize
            | Recovery::UnhookUnconfirmed as usize;
        if !self.retired.load(SeqCst)
            || self.pending.load(SeqCst) != 0
            || self.worker_pending.load(SeqCst)
            || self.recovery.load(SeqCst) & BLOCKERS != 0
        {
            return;
        }
        if let Some(slot) = self.slot.get() {
            if !self.owns_slot.swap(false, SeqCst) {
                return;
            }
            slot.store(false, SeqCst);
        }
        // Publish after releasing the slot so a replacement can start as
        // soon as it observes clean retirement.
        self.handoff_ready.store(true, SeqCst);
    }
}

const UNKNOWN: Observation = Observation {
    exact_device: None,
    selected_backend: None,
    status: Status::Unknown,
    fn_pressed: None,
    primary_mode: None,
    lifecycle: Lifecycle::Active,
};

#[derive(Clone)]
pub struct HookControl(Arc<Shared>);

impl HookControl {
    /// Replace all callback inputs together. The host controls expiry and may
    /// invalidate the snapshot immediately on status, session or ownership loss.
    pub fn publish(&self, observation: Observation, valid_until: Instant) {
        if observation.lifecycle == Lifecycle::Stopping {
            self.stop();
        }
        self.0.admission.publish(observation, valid_until);
    }

    pub fn invalidate(&self) {
        self.0.admission.word.store(0, SeqCst);
    }

    pub fn stop(&self) {
        if !self.0.stop.swap(true, SeqCst) {
            self.0.notify();
        }
    }

    /// Stop action admission and skip queued Down and Repeat effects.
    /// Held releases and the final worker reset remain eligible.
    pub fn cancel(&self) {
        self.0.cancel_actions.store(true, SeqCst);
        self.stop();
    }

    pub fn needs_recovery(&self, reason: Recovery) -> bool {
        self.0.recovery.load(SeqCst) & reason as usize != 0
    }

    /// This run has no remaining native or output ownership. A refused start
    /// gives no information about the independent owner that caused refusal.
    pub fn retired_cleanly(&self) -> bool {
        self.0.handoff_ready.load(SeqCst)
    }
}

struct Delivery {
    shared: Arc<Shared>,
    events: [Option<Event>; 2],
    in_flight: bool,
}

impl Drop for Delivery {
    fn drop(&mut self) {
        if self.events.iter().any(Option::is_some) {
            self.shared.flag(Recovery::DeliveryLost);
        }
        if self
            .events
            .iter()
            .flatten()
            .any(|event| event.phase == Phase::Up)
        {
            self.shared.flag(Recovery::ReleaseLost);
        }
        if self.in_flight {
            self.shared.flag(Recovery::OutputUncertain);
        }
        self.shared.pending.fetch_sub(1, SeqCst);
        self.shared.release_slot_if_safe();
    }
}

/// Single consumer. Invoke the executor only here, away from the hook thread.
pub struct HookActions(Receiver<Delivery>, Arc<Shared>);

impl Drop for HookActions {
    fn drop(&mut self) {
        self.1.consumer_ready.store(false, SeqCst);
        self.1.notify();
    }
}

impl HookActions {
    /// Wait for one batch. Returns false after the hook closes its sender.
    /// No event is retried, including when the executor unwinds.
    pub fn dispatch_next(
        &mut self,
        mut execute: impl FnMut(Event) -> Result<(), ActionFailure>,
    ) -> bool {
        let Ok(mut delivery) = self.0.recv() else {
            return false;
        };
        for index in 0..delivery.events.len() {
            let Some(event) = delivery.events[index] else {
                continue;
            };
            if delivery.shared.recovery.load(SeqCst) != 0 {
                break;
            }
            delivery.in_flight = true;
            let result = execute(event);
            delivery.in_flight = result == Err(ActionFailure::PossiblySubmitted);
            if result.is_err() {
                break;
            }
            delivery.events[index] = None;
        }
        true
    }
}

/// Run on a dedicated current-user message thread. The native Agent role
/// `agent-function-row-v1` runs it on RZ09-0581; packaging still refuses that
/// role.
pub struct FunctionRowHook {
    shared: Arc<Shared>,
    sender: SyncSender<Delivery>,
}

impl FunctionRowHook {
    pub fn new() -> (Self, HookControl, HookActions) {
        Self::with_capacity(ACTION_CAPACITY)
    }

    fn with_capacity(capacity: usize) -> (Self, HookControl, HookActions) {
        let shared = Arc::new(Shared {
            consumer_ready: AtomicBool::new(true),
            ..Shared::default()
        });
        let (sender, receiver) = sync_channel(capacity);
        (
            Self {
                shared: Arc::clone(&shared),
                sender,
            },
            HookControl(Arc::clone(&shared)),
            HookActions(receiver, shared),
        )
    }

    pub fn run(self, admission: StartupAdmission) -> Result<(), HookError> {
        self.run_with(admission, &mut native::WindowsMessages::default(), &OWNED)
    }

    fn run_with<M: Messages>(
        self,
        admission: StartupAdmission,
        messages: &mut M,
        ownership: &'static AtomicBool,
    ) -> Result<(), HookError> {
        let shared = Arc::clone(&self.shared);
        let result = self.run_registered(admission, messages, ownership);
        shared.finish();
        result
    }

    fn run_registered<M: Messages>(
        self,
        admission: StartupAdmission,
        messages: &mut M,
        ownership: &'static AtomicBool,
    ) -> Result<(), HookError> {
        let startup = self.shared.admission.read(messages.now());
        let Some(device @ (Device::Rz09_0581 | Device::Rz09_0528)) = startup.exact_device else {
            return Err(HookError::StartupRefused);
        };
        if admission.requested_backend != Some(Backend::WindowsHook)
            || !admission.local_session_confirmed
            || !admission.startup_recovery_clear
            || !admission.driver_can_transfer
            || !admission.previous_windows_hook_retired
            || startup.selected_backend != Some(Backend::WindowsHook)
        {
            return Err(HookError::StartupRefused);
        }
        if self.shared.stop.load(SeqCst) || !self.shared.consumer_ready.load(SeqCst) {
            return Err(HookError::Cancelled);
        }
        if ownership.swap(true, SeqCst) {
            return Err(HookError::AlreadyOwned);
        }
        let _ = self.shared.slot.set(ownership);
        self.shared.owns_slot.store(true, SeqCst);
        let shared = Arc::clone(&self.shared);
        let mut callback = Callback {
            hook: self,
            device,
            policy: WindowsHook::default(),
            released_keys: 0,
            draining: false,
        };
        let handle = messages.install(&shared)?;
        let mut drain_deadline = None;
        let result = loop {
            let now = messages.now();
            if callback.draining
                || shared.stop.load(SeqCst)
                || !shared.consumer_ready.load(SeqCst)
                || shared.recovery.load(SeqCst) != 0
            {
                callback.draining = true;
                let deadline = *drain_deadline.get_or_insert(now + DRAIN_LIMIT);
                if !callback.policy.has_suppressed_keys() || now >= deadline {
                    break Ok(());
                }
            }
            let wait = drain_deadline.map(|deadline| deadline.saturating_duration_since(now));
            match messages.pump(&mut callback, wait) {
                Ok(true) => {}
                Ok(false) => break Err(MessageLoopError::UnexpectedQuit),
                Err(code) => break Err(MessageLoopError::Native(code)),
            }
        };
        if result.is_err() {
            shared.flag(Recovery::MessageLoopFailed);
        }
        if callback.policy.has_suppressed_keys() {
            shared.flag(Recovery::KeysHeld);
            shared.flag(Recovery::ReleaseLost);
        }
        if let Err(code) = messages.uninstall(handle, &shared) {
            shared.flag(Recovery::UnhookUnconfirmed);
            return Err(HookError::Unhook {
                code,
                message_loop: result.err(),
            });
        }
        result.map_err(HookError::MessageLoop)
    }
}

trait Messages {
    type Handle;

    fn now(&self) -> Instant;
    fn install(&mut self, shared: &Arc<Shared>) -> Result<Self::Handle, HookError>;
    fn pump(&mut self, callback: &mut Callback, wait: Option<Duration>) -> Result<bool, u32>;
    fn uninstall(&mut self, handle: Self::Handle, shared: &Shared) -> Result<(), u32>;
}

struct Callback {
    hook: FunctionRowHook,
    device: Device,
    policy: WindowsHook,
    released_keys: u16,
    draining: bool,
}

impl Callback {
    fn route(&mut self, stroke: Stroke, was_down: bool, now: Instant) -> bool {
        let shared = &self.hook.shared;
        let mut observation = shared.admission.read(now);
        self.draining |= shared.stop.load(SeqCst)
            || !shared.consumer_ready.load(SeqCst)
            || shared.recovery.load(SeqCst) != 0;
        if observation.exact_device != Some(self.device) {
            observation = UNKNOWN;
        }
        if self.draining {
            observation.lifecycle = Lifecycle::Stopping;
        }

        // Commit a new suppression only after queuing all its actions. A stale
        // release and the new down must succeed or fail as one batch.
        let mut candidate = self.policy.clone();
        let decision = candidate.handle(stroke, was_down, observation);
        let events = [decision.stale_release, decision.event];
        if events.iter().all(Option::is_none) {
            self.policy = candidate;
            return decision.suppress;
        }
        if shared.recovery.load(SeqCst) == 0 {
            shared.pending.fetch_add(1, SeqCst);
            let delivery = Delivery {
                shared: Arc::clone(shared),
                events,
                in_flight: false,
            };
            if self.hook.sender.try_send(delivery).is_ok() {
                self.policy = candidate;
                return decision.suppress;
            }
        }

        // Preserve old pairing even on failure. A fresh down which reached
        // Windows must pass its matching up. Never replay an undelivered event.
        self.draining = true;
        observation.lifecycle = Lifecycle::Stopping;
        let fallback = self.policy.handle(stroke, was_down, observation);
        if [fallback.stale_release, fallback.event]
            .into_iter()
            .flatten()
            .any(|event| event.phase == Phase::Up)
        {
            shared.flag(Recovery::ReleaseLost);
        }
        fallback.suppress
    }
}

//! Current-user WindowsHook adapter. The host supplies verified identity, Fn
//! status and ownership observations, and executes typed actions off-thread.
//! The low-level hook has desktop scope. An exact host observation admits the
//! managed backend, while the callback packet itself has no device identity.

use std::{
    sync::{
        Arc, Mutex, OnceLock,
        atomic::{AtomicBool, AtomicUsize, Ordering::SeqCst},
        mpsc::{Receiver, SyncSender, sync_channel},
    },
    time::{Duration, Instant},
};

use openblade_domain::function_row::{
    Backend, Device, Event, Lifecycle, Observation, Phase, Status, Stroke, WindowsHook,
};

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
    Install(u32),
    MessageLoop(u32),
    UnexpectedQuit,
    Unhook(u32),
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

#[derive(Default)]
struct Shared {
    admission: Mutex<Option<(Observation, Instant)>>,
    stop: AtomicBool,
    recovery: AtomicUsize,
    pending: AtomicUsize,
    retired: AtomicBool,
    owns_slot: AtomicBool,
    slot: OnceLock<&'static AtomicBool>,
    consumer_ready: AtomicBool,
}

impl Shared {
    fn observation(&self, now: Instant) -> Observation {
        self.admission
            .try_lock()
            .ok()
            .and_then(|snapshot| *snapshot)
            .filter(|(_, valid_until)| now < *valid_until)
            .map_or(UNKNOWN, |(observation, _)| observation)
    }

    fn flag(&self, reason: Recovery) {
        self.recovery.fetch_or(reason as usize, SeqCst);
    }

    fn clean(&self) -> bool {
        self.retired.load(SeqCst)
            && self.pending.load(SeqCst) == 0
            && self.recovery.load(SeqCst) == 0
    }

    fn release_slot_if_clean(&self) {
        if self.clean()
            && self.owns_slot.swap(false, SeqCst)
            && let Some(slot) = self.slot.get()
        {
            slot.store(false, SeqCst);
        }
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
        if let Ok(mut current) = self.0.admission.lock() {
            *current = Some((observation, valid_until));
        }
    }

    pub fn invalidate(&self) {
        if let Ok(mut current) = self.0.admission.lock() {
            *current = None;
        }
    }

    pub fn stop(&self) {
        self.0.stop.store(true, SeqCst);
    }

    pub fn needs_recovery(&self, reason: Recovery) -> bool {
        self.0.recovery.load(SeqCst) & reason as usize != 0
    }

    pub fn retired_cleanly(&self) -> bool {
        self.0.clean()
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
        self.shared.release_slot_if_clean();
    }
}

/// Single consumer. Invoke the executor only here, away from the hook thread.
pub struct HookActions(Receiver<Delivery>, Arc<Shared>);

impl Drop for HookActions {
    fn drop(&mut self) {
        self.1.consumer_ready.store(false, SeqCst);
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

/// Run on a dedicated current-user message thread. This adapter has no
/// production caller in this stage.
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
        self.run_with(admission, &mut native::WindowsMessages, &OWNED)
    }

    fn run_with<M: Messages>(
        self,
        admission: StartupAdmission,
        messages: &mut M,
        ownership: &'static AtomicBool,
    ) -> Result<(), HookError> {
        let startup = self.shared.observation(messages.now());
        let Some(device @ (Device::Rz09_0581 | Device::Rz09_0528)) = startup.exact_device else {
            return Err(HookError::StartupRefused);
        };
        if admission.requested_backend != Some(Backend::WindowsHook)
            || !admission.local_session_confirmed
            || !admission.startup_recovery_clear
            || !admission.driver_can_transfer
            || !admission.previous_windows_hook_retired
            || startup.selected_backend != Some(Backend::WindowsHook)
            || startup.lifecycle != Lifecycle::Active
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
            draining: false,
        };
        let handle = match messages.install() {
            Ok(handle) => handle,
            Err(code) => {
                shared.retired.store(true, SeqCst);
                shared.release_slot_if_clean();
                return Err(HookError::Install(code));
            }
        };

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
                if !callback.policy.has_active_keys() || now >= deadline {
                    break Ok(());
                }
            }
            match messages.pump(&mut callback) {
                Ok(true) => {}
                Ok(false) => break Err(HookError::UnexpectedQuit),
                Err(code) => break Err(HookError::MessageLoop(code)),
            }
        };
        if result.is_err() {
            shared.flag(Recovery::MessageLoopFailed);
        }
        if callback.policy.has_active_keys() {
            shared.flag(Recovery::KeysHeld);
        }
        if callback.policy.has_suppressed_keys() {
            shared.flag(Recovery::ReleaseLost);
        }
        let unhook = messages.uninstall(handle).map_err(HookError::Unhook);
        if unhook.is_err() {
            shared.flag(Recovery::UnhookUnconfirmed);
        }
        drop(callback);
        shared.retired.store(true, SeqCst);
        shared.release_slot_if_clean();
        unhook.and(result)
    }
}

trait Messages {
    type Handle;

    fn now(&self) -> Instant;
    fn install(&mut self) -> Result<Self::Handle, u32>;
    fn pump(&mut self, callback: &mut Callback) -> Result<bool, u32>;
    fn uninstall(&mut self, handle: Self::Handle) -> Result<(), u32>;
}

struct Callback {
    hook: FunctionRowHook,
    device: Device,
    policy: WindowsHook,
    draining: bool,
}

impl Callback {
    fn route(&mut self, stroke: Stroke, was_down: bool, now: Instant) -> bool {
        let shared = &self.hook.shared;
        let mut observation = shared.observation(now);
        self.draining |= observation.lifecycle == Lifecycle::Stopping
            || shared.stop.load(SeqCst)
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

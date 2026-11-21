use super::*;
use openblade_domain::function_row::{Action, PrimaryMode};

const ADMITTED: StartupAdmission = StartupAdmission {
    requested_backend: Some(Backend::WindowsHook),
    local_session_confirmed: true,
    startup_recovery_clear: true,
    driver_can_transfer: true,
    previous_windows_hook_retired: true,
};

const ACTIVE: Observation = Observation {
    exact_device: Some(Device::Rz09_0581),
    selected_backend: Some(Backend::WindowsHook),
    status: Status::FirmwareDriverMode { active: true },
    fn_pressed: Some(false),
    primary_mode: Some(PrimaryMode::MultimediaKeys),
    lifecycle: Lifecycle::Active,
};

struct Fake<F> {
    now: Instant,
    on_pump: F,
    calls: Vec<&'static str>,
    install_error: Option<u32>,
    uninstall_error: Option<u32>,
    waits: Vec<Option<Duration>>,
}

impl<F> Fake<F> {
    fn new(on_pump: F) -> Self {
        Self {
            now: Instant::now(),
            on_pump,
            calls: Vec::new(),
            install_error: None,
            uninstall_error: None,
            waits: Vec::new(),
        }
    }
}

impl<F: FnMut(&mut Callback, Instant) -> Result<bool, u32>> Messages for Fake<F> {
    type Handle = u32;

    fn now(&self) -> Instant {
        self.now
    }

    fn install(&mut self, _: &Arc<Shared>) -> Result<u32, HookError> {
        self.calls.push("install");
        self.install_error
            .map_or(Ok(42), |code| Err(HookError::Install(code)))
    }

    fn pump(&mut self, callback: &mut Callback, wait: Option<Duration>) -> Result<bool, u32> {
        self.calls.push("pump");
        self.waits.push(wait);
        self.now += Duration::from_millis(50);
        (self.on_pump)(callback, self.now)
    }

    fn uninstall(&mut self, handle: u32, _: &Shared) -> Result<(), u32> {
        self.calls.push("uninstall");
        assert_eq!(handle, 42);
        self.uninstall_error.map_or(Ok(()), Err)
    }
}

fn no_pump(_: &mut Callback, _: Instant) -> Result<bool, u32> {
    panic!("unexpected pump");
}

fn key(scan_code: u16, down: bool) -> Stroke {
    Stroke { scan_code, down }
}

fn expect_actions(mut actions: HookActions, expected: &[(Action, Phase)]) {
    let mut received = Vec::new();
    while actions.dispatch_next(|event| {
        received.push((event.action, event.phase));
        Ok(())
    }) {}
    assert_eq!(received, expected);
}

#[test]
fn startup_refuses_unproved_ownership_non_windows_stale_and_cancelled() {
    static SLOT: AtomicBool = AtomicBool::new(false);
    for admission in [
        StartupAdmission {
            requested_backend: Some(Backend::RazerDriver),
            ..ADMITTED
        },
        StartupAdmission {
            local_session_confirmed: false,
            ..ADMITTED
        },
        StartupAdmission {
            startup_recovery_clear: false,
            ..ADMITTED
        },
        StartupAdmission {
            driver_can_transfer: false,
            ..ADMITTED
        },
        StartupAdmission {
            previous_windows_hook_retired: false,
            ..ADMITTED
        },
    ] {
        let (hook, control, _actions) = FunctionRowHook::new();
        let mut messages = Fake::new(no_pump);
        control.publish(ACTIVE, messages.now + Duration::from_secs(1));
        assert_eq!(
            hook.run_with(admission, &mut messages, &SLOT),
            Err(HookError::StartupRefused)
        );
        assert!(messages.calls.is_empty());
        assert!(control.retired_cleanly());
    }

    let mut messages = Fake::new(no_pump);
    let (hook, control, _actions) = FunctionRowHook::new();
    control.publish(ACTIVE, messages.now);
    assert_eq!(
        hook.run_with(ADMITTED, &mut messages, &SLOT),
        Err(HookError::StartupRefused)
    );

    let (hook, control, _actions) = FunctionRowHook::new();
    control.publish(ACTIVE, messages.now + Duration::from_secs(1));
    control.stop();
    assert_eq!(
        hook.run_with(ADMITTED, &mut messages, &SLOT),
        Err(HookError::Cancelled)
    );

    let (hook, control, actions) = FunctionRowHook::new();
    control.publish(ACTIVE, messages.now + Duration::from_secs(1));
    drop(actions);
    assert_eq!(
        hook.run_with(ADMITTED, &mut messages, &SLOT),
        Err(HookError::Cancelled)
    );
    assert!(messages.calls.is_empty());
    assert!(!SLOT.load(SeqCst));
}

#[test]
fn a_held_key_keeps_pairing_when_admission_expires_and_on_stop() {
    static SLOT: AtomicBool = AtomicBool::new(false);
    let (hook, control, actions) = FunctionRowHook::new();
    let mut step = 0;
    let mut messages = Fake::new(|callback: &mut Callback, now| {
        step += 1;
        match step {
            1 => assert!(callback.route(key(0x3c, true), false, now)),
            2 => {
                assert!(callback.route(key(0x3c, true), true, now));
                // Fresh F3 fails open after expiry, including after readmission.
                assert!(!callback.route(key(0x3d, true), false, now));
                control.publish(ACTIVE, now + Duration::from_secs(1));
                assert!(!callback.route(key(0x3d, true), true, now));
                assert!(!callback.route(key(0x3d, false), false, now));
                control.stop();
            }
            3 => {
                assert!(callback.route(key(0x3c, true), true, now));
                assert!(!callback.route(key(0x44, true), false, now));
                assert!(!callback.route(key(0x44, false), false, now));
                assert!(callback.route(key(0x3c, false), false, now));
            }
            _ => panic!("did not retire after release"),
        }
        Ok(true)
    });
    control.publish(ACTIVE, messages.now + Duration::from_millis(75));
    assert_eq!(hook.run_with(ADMITTED, &mut messages, &SLOT), Ok(()));
    assert_eq!(
        messages.calls,
        ["install", "pump", "pump", "pump", "uninstall"]
    );
    assert!(!control.retired_cleanly());
    assert!(SLOT.load(SeqCst));

    let (second, control2, _actions) = FunctionRowHook::new();
    control2.publish(ACTIVE, messages.now + Duration::from_secs(1));
    let mut other = Fake::new(no_pump);
    assert_eq!(
        second.run_with(ADMITTED, &mut other, &SLOT),
        Err(HookError::AlreadyOwned)
    );
    assert!(control2.retired_cleanly());
    assert!(SLOT.load(SeqCst));
    expect_actions(
        actions,
        &[
            (Action::VolumeDown, Phase::Down),
            (Action::VolumeDown, Phase::Repeat),
            (Action::VolumeDown, Phase::Up),
        ],
    );
    assert!(control.retired_cleanly());
    assert!(!SLOT.load(SeqCst));
}

#[test]
fn stale_release_is_delivered_before_replacement_down() {
    static SLOT: AtomicBool = AtomicBool::new(false);
    let (hook, control, actions) = FunctionRowHook::new();
    let mut messages = Fake::new(|callback: &mut Callback, now| {
        assert!(!callback.route(key(0x3c, true), true, now)); // Started mid-press.
        assert!(callback.route(key(0x3c, true), false, now));
        assert!(callback.route(key(0x3c, true), false, now));
        assert!(callback.route(key(0x3c, false), false, now));
        control.stop();
        Ok(true)
    });
    control.publish(ACTIVE, messages.now + Duration::from_secs(1));
    assert_eq!(hook.run_with(ADMITTED, &mut messages, &SLOT), Ok(()));
    expect_actions(
        actions,
        &[
            (Action::VolumeDown, Phase::Down),
            (Action::VolumeDown, Phase::Up),
            (Action::VolumeDown, Phase::Down),
            (Action::VolumeDown, Phase::Up),
        ],
    );
    assert!(control.retired_cleanly());
}

#[test]
fn saturation_passes_fresh_press_and_keeps_lost_release_visible() {
    static SLOT: AtomicBool = AtomicBool::new(false);
    let (hook, control, actions) = FunctionRowHook::with_capacity(1);
    let mut messages = Fake::new(|callback: &mut Callback, now| {
        assert!(callback.route(key(0x3c, true), false, now));
        assert!(!callback.route(key(0x3d, true), false, now)); // Rejected batch.
        assert!(!callback.route(key(0x3d, false), false, now));
        assert!(callback.route(key(0x3c, false), false, now));
        Ok(true)
    });
    control.publish(ACTIVE, messages.now + Duration::from_secs(1));
    assert_eq!(hook.run_with(ADMITTED, &mut messages, &SLOT), Ok(()));
    expect_actions(actions, &[]);
    assert!(control.needs_recovery(Recovery::DeliveryLost));
    assert!(control.needs_recovery(Recovery::ReleaseLost));
    assert!(!control.needs_recovery(Recovery::OutputUncertain));
    assert!(!control.retired_cleanly());
    assert!(SLOT.load(SeqCst));
}

#[test]
fn ambiguous_action_is_never_replayed_and_queued_release_is_lost() {
    static SLOT: AtomicBool = AtomicBool::new(false);
    let (hook, control, mut actions) = FunctionRowHook::new();
    let mut calls = 0;
    let mut messages = Fake::new(|callback: &mut Callback, now| {
        assert!(callback.route(key(0x3c, true), false, now));
        assert!(callback.route(key(0x3c, false), false, now));
        assert!(actions.dispatch_next(|event| {
            calls += 1;
            assert_eq!(event.phase, Phase::Down);
            Err(ActionFailure::PossiblySubmitted)
        }));
        assert!(!callback.route(key(0x3d, true), false, now));
        assert!(!callback.route(key(0x3d, false), false, now));
        Ok(true)
    });
    control.publish(ACTIVE, messages.now + Duration::from_secs(1));
    assert_eq!(hook.run_with(ADMITTED, &mut messages, &SLOT), Ok(()));
    assert_eq!(calls, 1);
    expect_actions(actions, &[]);
    assert!(control.needs_recovery(Recovery::OutputUncertain));
    assert!(control.needs_recovery(Recovery::ReleaseLost));
    assert!(!control.retired_cleanly());
}

#[test]
fn drain_deadline_and_failed_unhook_prevent_handoff() {
    static SLOT: AtomicBool = AtomicBool::new(false);
    let (hook, control, actions) = FunctionRowHook::new();
    let mut first = true;
    let mut messages = Fake::new(|callback: &mut Callback, now| {
        if std::mem::take(&mut first) {
            assert!(callback.route(key(0x3c, true), false, now));
            control.stop();
        }
        Ok(true)
    });
    control.publish(ACTIVE, messages.now + Duration::from_secs(1));
    messages.uninstall_error = Some(5);
    assert_eq!(
        hook.run_with(ADMITTED, &mut messages, &SLOT),
        Err(HookError::Unhook {
            code: 5,
            message_loop: None
        })
    );
    assert_eq!(
        messages
            .calls
            .iter()
            .filter(|&&call| call == "pump")
            .count(),
        6
    );
    assert_eq!(messages.calls.last(), Some(&"uninstall"));
    expect_actions(actions, &[]);
    assert!(control.needs_recovery(Recovery::KeysHeld));
    assert!(control.needs_recovery(Recovery::ReleaseLost));
    assert!(control.needs_recovery(Recovery::UnhookUnconfirmed));
    assert!(!control.retired_cleanly());
    assert!(SLOT.load(SeqCst));
}

#[test]
fn install_failure_retires_without_unhook_and_pump_failure_still_unhooks() {
    static SLOT: AtomicBool = AtomicBool::new(false);
    let (hook, control, _actions) = FunctionRowHook::new();
    let mut messages = Fake::new(no_pump);
    messages.install_error = Some(7);
    control.publish(ACTIVE, messages.now + Duration::from_secs(1));
    assert_eq!(
        hook.run_with(ADMITTED, &mut messages, &SLOT),
        Err(HookError::Install(7))
    );
    assert_eq!(messages.calls, ["install"]);
    assert!(control.retired_cleanly());
    assert!(!SLOT.load(SeqCst));

    let (hook, control, _actions) = FunctionRowHook::new();
    let mut messages = Fake::new(|_: &mut Callback, _| Err(8));
    messages.uninstall_error = Some(9);
    control.publish(ACTIVE, messages.now + Duration::from_secs(1));
    assert_eq!(
        hook.run_with(ADMITTED, &mut messages, &SLOT),
        Err(HookError::Unhook {
            code: 9,
            message_loop: Some(MessageLoopError::Native(8))
        })
    );
    assert_eq!(messages.calls, ["install", "pump", "uninstall"]);
    assert!(control.needs_recovery(Recovery::MessageLoopFailed));
    assert!(!control.retired_cleanly());
}

#[test]
fn stopping_snapshot_retires_without_another_edge_or_waiting_for_passed_keys() {
    static SLOT: AtomicBool = AtomicBool::new(false);
    let (hook, control, actions) = FunctionRowHook::new();
    let mut once = true;
    let mut messages = Fake::new(|callback: &mut Callback, now| {
        assert!(std::mem::take(&mut once), "unexpected second pump");
        assert!(!callback.route(key(0x3e, true), true, now));
        control.publish(
            Observation {
                lifecycle: Lifecycle::Stopping,
                ..ACTIVE
            },
            now,
        );
        Ok(true)
    });
    control.publish(ACTIVE, messages.now + Duration::from_secs(1));
    assert_eq!(hook.run_with(ADMITTED, &mut messages, &SLOT), Ok(()));
    assert_eq!(messages.calls, ["install", "pump", "uninstall"]);
    expect_actions(actions, &[]);
    assert!(control.retired_cleanly());
    assert!(!control.needs_recovery(Recovery::KeysHeld));
    assert!(!SLOT.load(SeqCst));
}

#[test]
fn definite_delivery_loss_can_retire_when_no_release_or_output_is_uncertain() {
    static SLOT: AtomicBool = AtomicBool::new(false);
    let (hook, control, actions) = FunctionRowHook::new();
    let mut messages = Fake::new(|callback: &mut Callback, _| {
        callback.hook.shared.flag(Recovery::DeliveryLost);
        Ok(true)
    });
    control.publish(ACTIVE, messages.now + Duration::from_secs(1));
    assert_eq!(hook.run_with(ADMITTED, &mut messages, &SLOT), Ok(()));
    expect_actions(actions, &[]);
    assert!(control.needs_recovery(Recovery::DeliveryLost));
    assert!(control.retired_cleanly());
    assert!(!SLOT.load(SeqCst));
}

#[test]
fn atomic_admission_retains_exact_layer_and_expiry_together() {
    let snapshot = AdmissionSnapshot::default();
    let before = snapshot.origin + Duration::from_micros(1);
    let expiry = before + Duration::from_micros(1);
    for exact_device in [Some(Device::Rz09_0581), Some(Device::Rz09_0528)] {
        for fn_pressed in [None, Some(false), Some(true)] {
            for primary_mode in [
                None,
                Some(PrimaryMode::FunctionKeys),
                Some(PrimaryMode::MultimediaKeys),
            ] {
                let observation = Observation {
                    exact_device,
                    fn_pressed,
                    primary_mode,
                    ..ACTIVE
                };
                snapshot.publish(observation, expiry);
                assert_eq!(snapshot.read(before), observation);
                assert_eq!(snapshot.read(expiry), UNKNOWN);
            }
        }
    }
    snapshot.publish(
        Observation {
            exact_device: Some(Device::Rz09_0484),
            selected_backend: Some(Backend::RazerDriver),
            status: Status::HostManagedFn {
                stroke_admitted: true,
            },
            ..ACTIVE
        },
        expiry,
    );
    let observation = snapshot.read(before);
    assert_eq!(observation.exact_device, None);
    assert_eq!(observation.selected_backend, None);
    assert_eq!(observation.status, Status::Unknown);
}

#[path = "executor_tests.rs"]
mod executor_tests;

/// Counts heap operations on threads that opt in, so a callback can be shown
/// to run without the allocator, whose lock could exceed the hook timeout.
#[allow(unsafe_code)]
mod allocations {
    use std::{
        alloc::{GlobalAlloc, Layout, System},
        cell::Cell,
    };

    struct Counting;

    thread_local! {
        static COUNTING: Cell<bool> = const { Cell::new(false) };
        static COUNT: Cell<usize> = const { Cell::new(0) };
    }

    fn note() {
        if COUNTING.with(Cell::get) {
            COUNT.with(|count| count.set(count.get() + 1));
        }
    }

    // SAFETY: every operation forwards to System unchanged.
    unsafe impl GlobalAlloc for Counting {
        unsafe fn alloc(&self, layout: Layout) -> *mut u8 {
            note();
            unsafe { System.alloc(layout) }
        }

        unsafe fn dealloc(&self, pointer: *mut u8, layout: Layout) {
            note();
            unsafe { System.dealloc(pointer, layout) }
        }

        unsafe fn realloc(&self, pointer: *mut u8, layout: Layout, size: usize) -> *mut u8 {
            note();
            unsafe { System.realloc(pointer, layout, size) }
        }
    }

    #[global_allocator]
    static ALLOCATOR: Counting = Counting;

    /// The heap operations `body` performs on this thread.
    pub(super) fn during<T>(body: impl FnOnce() -> T) -> (T, usize) {
        COUNT.with(|count| count.set(0));
        COUNTING.with(|counting| counting.set(true));
        let result = body();
        COUNTING.with(|counting| counting.set(false));
        (result, COUNT.with(Cell::get))
    }
}

#[test]
fn the_callback_neither_allocates_nor_blocks_and_drops_on_a_full_queue() {
    static SLOT: AtomicBool = AtomicBool::new(false);
    let (hook, control, actions) = FunctionRowHook::with_capacity(2);
    let shared = Arc::clone(&control.0);
    let mut messages = Fake::new(|callback: &mut Callback, now| {
        // A held wake lock must not stall the flag that saturation raises: a
        // callback that waited for it would never return.
        let _wake = shared.wake.lock().unwrap();
        let (routed, heap) = allocations::during(|| {
            [
                callback.route(key(0x3c, true), false, now),
                callback.route(key(0x3c, false), false, now),
                // The queue is full and nothing consumes it: dropped, passed.
                callback.route(key(0x3d, true), false, now),
                callback.route(key(0x3d, false), false, now),
                callback.route(key(0x01, true), false, now),
            ]
        });
        assert_eq!(routed, [true, true, false, false, false]);
        assert_eq!(heap, 0, "the hook callback allocated");
        Ok(true)
    });
    control.publish(ACTIVE, messages.now + Duration::from_secs(1));
    assert_eq!(hook.run_with(ADMITTED, &mut messages, &SLOT), Ok(()));
    assert!(control.needs_recovery(Recovery::DeliveryLost));
    assert!(!control.needs_recovery(Recovery::ReleaseLost));
    // After a loss nothing queued runs, and the queued release is lost.
    expect_actions(actions, &[]);
    assert!(control.needs_recovery(Recovery::ReleaseLost));
    assert!(!control.retired_cleanly());
}

#[test]
fn routing_to_a_parked_worker_does_not_allocate() {
    // Production's worker waits in `recv`, so every send wakes it.
    static SLOT: AtomicBool = AtomicBool::new(false);
    let (hook, control, mut actions) = FunctionRowHook::new();
    // How often the worker has entered `recv`: once before the first
    // delivery and once after each one.
    let entered = Arc::new(std::sync::atomic::AtomicUsize::new(0));
    let consumer = std::thread::spawn({
        let entered = Arc::clone(&entered);
        move || {
            let mut delivered = 0;
            loop {
                entered.fetch_add(1, SeqCst);
                if !actions.dispatch_next(|_| Ok(())) {
                    return delivered;
                }
                delivered += 1;
            }
        }
    });
    let parked = |deliveries: usize| {
        // Until the worker has taken every delivery so far and come back to
        // `recv`. Parking only strengthens the test; a worker still on its way
        // into `recv` cannot make it fail.
        let deadline = Instant::now() + Duration::from_secs(10);
        while entered.load(SeqCst) != deliveries + 1 {
            assert!(
                Instant::now() < deadline,
                "the worker did not return to recv"
            );
            std::thread::yield_now();
        }
    };
    let mut pumps = 0;
    let mut heap = Vec::new();
    let mut messages = Fake::new(|callback: &mut Callback, now| {
        pumps += 1;
        if pumps == 1 {
            for (index, (stroke, was_down)) in [
                (key(0x3c, true), false),
                (key(0x3c, true), true),
                (key(0x3c, false), false),
                (key(0x3d, true), false),
                (key(0x3d, false), false),
            ]
            .into_iter()
            .enumerate()
            {
                // Each route, the repeat included, queues one delivery.
                parked(index);
                let (suppressed, operations) =
                    allocations::during(|| callback.route(stroke, was_down, now));
                assert!(suppressed, "{stroke:?}");
                heap.push(operations);
            }
            parked(5);
            control.cancel();
        }
        Ok(true)
    });
    control.publish(ACTIVE, messages.now + Duration::from_secs(60));
    assert_eq!(hook.run_with(ADMITTED, &mut messages, &SLOT), Ok(()));
    assert_eq!(consumer.join().unwrap(), 5);
    assert_eq!(heap, [0; 5], "the hook callback allocated");
}

#[test]
fn a_stopping_hook_waits_no_longer_than_the_drain_limit() {
    // Windows removes a low-level hook whose callback exceeds
    // LowLevelHooksTimeout (300 ms by default); the drain stays below it.
    const LOW_LEVEL_HOOKS_TIMEOUT: Duration = Duration::from_millis(300);
    const { assert!(DRAIN_LIMIT.as_millis() < LOW_LEVEL_HOOKS_TIMEOUT.as_millis()) };
    static SLOT: AtomicBool = AtomicBool::new(false);
    let (hook, control, _actions) = FunctionRowHook::new();
    let stopped_at = std::cell::Cell::new(None);
    let mut messages = Fake::new(|callback: &mut Callback, now| {
        if stopped_at.get().is_none() {
            // Held, and its release never arrives.
            assert!(callback.route(key(0x3c, true), false, now));
            control.cancel();
            stopped_at.set(Some(now));
        }
        Ok(true)
    });
    control.publish(ACTIVE, messages.now + Duration::from_secs(1));
    assert_eq!(hook.run_with(ADMITTED, &mut messages, &SLOT), Ok(()));
    let stopped_at = stopped_at.get().unwrap();
    // The fake clock advances 50 ms per pump; the drain ends at its deadline.
    assert!(messages.now - stopped_at <= DRAIN_LIMIT);
    assert_eq!(messages.waits[0], None);
    assert!(
        messages.waits[1..]
            .iter()
            .all(|wait| wait.is_some_and(|wait| wait <= DRAIN_LIMIT))
    );
    assert_eq!(messages.calls.last(), Some(&"uninstall"));
    assert!(control.needs_recovery(Recovery::KeysHeld));
    assert!(SLOT.load(SeqCst));
}

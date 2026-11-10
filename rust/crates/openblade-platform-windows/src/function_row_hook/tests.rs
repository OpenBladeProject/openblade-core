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
}

impl<F> Fake<F> {
    fn new(on_pump: F) -> Self {
        Self {
            now: Instant::now(),
            on_pump,
            calls: Vec::new(),
            install_error: None,
            uninstall_error: None,
        }
    }
}

impl<F: FnMut(&mut Callback, Instant) -> Result<bool, u32>> Messages for Fake<F> {
    type Handle = u32;

    fn now(&self) -> Instant {
        self.now
    }

    fn install(&mut self) -> Result<u32, u32> {
        self.calls.push("install");
        self.install_error.map_or(Ok(42), Err)
    }

    fn pump(&mut self, callback: &mut Callback) -> Result<bool, u32> {
        self.calls.push("pump");
        self.now += Duration::from_millis(50);
        (self.on_pump)(callback, self.now)
    }

    fn uninstall(&mut self, handle: u32) -> Result<(), u32> {
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
        Err(HookError::Unhook(5))
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
    control.publish(ACTIVE, messages.now + Duration::from_secs(1));
    assert_eq!(
        hook.run_with(ADMITTED, &mut messages, &SLOT),
        Err(HookError::MessageLoop(8))
    );
    assert_eq!(messages.calls, ["install", "pump", "uninstall"]);
    assert!(control.needs_recovery(Recovery::MessageLoopFailed));
    assert!(!control.retired_cleanly());
}

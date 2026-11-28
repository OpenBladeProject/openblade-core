use openblade_domain::function_row::{
    Action, Backend, Device, DriverFailure, DriverOutput, DriverProgress, Event, HookDecision,
    Lifecycle, Observation, Phase, PrimaryMode, RazerDriver, Status, Stroke, WindowsHook,
};

fn firmware(device: Device, backend: Backend) -> Observation {
    Observation {
        exact_device: Some(device),
        selected_backend: Some(backend),
        status: Status::FirmwareDriverMode { active: true },
        fn_pressed: Some(false),
        primary_mode: Some(PrimaryMode::MultimediaKeys),
        lifecycle: Lifecycle::Active,
    }
}

fn host_fn(stroke_admitted: bool) -> Observation {
    Observation {
        status: Status::HostManagedFn { stroke_admitted },
        ..firmware(Device::Rz09_0484, Backend::RazerDriver)
    }
}

fn stroke(scan_code: u16, down: bool) -> Stroke {
    Stroke { scan_code, down }
}

fn event(scan_code: u16, action: Action, phase: Phase) -> Event {
    Event {
        scan_code,
        action,
        phase,
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum Operation {
    Publish(Event),
    Forward(Stroke),
    Reset,
}

#[derive(Default)]
struct Script {
    observed: Vec<Operation>,
    fail: Option<Operation>,
}

impl Script {
    fn record(&mut self, operation: Operation) -> Result<(), Operation> {
        self.observed.push(operation);
        if self.fail == Some(operation) {
            Err(operation)
        } else {
            Ok(())
        }
    }
}

impl DriverOutput for Script {
    type Error = Operation;

    fn publish(&mut self, event: Event) -> Result<(), Self::Error> {
        self.record(Operation::Publish(event))
    }

    fn forward(&mut self, stroke: Stroke) -> Result<(), Self::Error> {
        self.record(Operation::Forward(stroke))
    }

    fn reset_actions(&mut self) {
        self.observed.push(Operation::Reset);
    }
}

// Managed oracle: RazerBladeFunctionRowMediaInput.TryMap and
// RazerBladeFunctionRowInputState.IsRepeatable (SpecialKeyRawInput.cs).
const ROW_CASES: [(u16, Action, bool); 12] = [
    (0x3b, Action::VolumeMute, false),
    (0x3c, Action::VolumeDown, true),
    (0x3d, Action::VolumeUp, true),
    (0x3e, Action::Project, false),
    (0x3f, Action::PreviousTrack, false),
    (0x40, Action::PlayPause, false),
    (0x41, Action::NextTrack, false),
    (0x42, Action::DisplayBrightnessDown, true),
    (0x43, Action::DisplayBrightnessUp, true),
    (0x44, Action::KeyboardBrightnessDown, true),
    (0x57, Action::KeyboardBrightnessUp, true),
    (0x58, Action::PrintScreen, false),
];

#[test]
fn all_typed_actions_have_paired_edges_and_managed_repeat_rules() {
    for device in [Device::Rz09_0581, Device::Rz09_0528] {
        let observation = firmware(device, Backend::WindowsHook);
        let mut hook = WindowsHook::default();
        for (scan, action, repeatable) in ROW_CASES {
            assert_eq!(
                hook.handle(stroke(scan, true), false, observation),
                HookDecision {
                    suppress: true,
                    event: Some(event(scan, action, Phase::Down)),
                    ..HookDecision::default()
                }
            );
            let repeated = hook.handle(stroke(scan, true), true, observation);
            assert!(repeated.suppress);
            assert_eq!(
                repeated.event,
                repeatable.then(|| event(scan, action, Phase::Repeat))
            );
            let released = hook.handle(stroke(scan, false), false, observation);
            assert!(released.suppress);
            assert_eq!(released.event, Some(event(scan, action, Phase::Up)));
            assert_eq!(
                hook.handle(stroke(scan, false), false, observation),
                HookDecision::default()
            );
            assert!(!hook.has_active_keys());
        }
    }
}

#[test]
fn windows_hook_admission_requires_exact_blade16_selected_hook_and_known_layer() {
    let base = firmware(Device::Rz09_0581, Backend::WindowsHook);
    let denied = [
        Observation {
            exact_device: None,
            ..base
        },
        Observation {
            exact_device: Some(Device::Rz09_0484),
            ..base
        },
        Observation {
            selected_backend: None,
            ..base
        },
        Observation {
            selected_backend: Some(Backend::RazerDriver),
            ..base
        },
        Observation {
            status: Status::Unknown,
            ..base
        },
        Observation {
            status: Status::FirmwareDriverMode { active: false },
            ..base
        },
        Observation {
            fn_pressed: None,
            ..base
        },
        Observation {
            primary_mode: None,
            ..base
        },
        Observation {
            lifecycle: Lifecycle::Stopping,
            ..base
        },
        Observation {
            fn_pressed: Some(true),
            ..base
        },
        Observation {
            primary_mode: Some(PrimaryMode::FunctionKeys),
            ..base
        },
        Observation {
            selected_backend: Some(Backend::WindowsHook),
            ..host_fn(true)
        },
    ];
    for observation in denied {
        let mut hook = WindowsHook::default();
        let down = stroke(0x43, true);
        assert_eq!(
            hook.handle(down, false, observation),
            HookDecision::default()
        );
        assert!(hook.has_active_keys());
        assert_eq!(hook.handle(down, true, base), HookDecision::default());
        assert_eq!(
            hook.handle(stroke(0x43, false), false, base),
            HookDecision::default()
        );
        assert!(!hook.has_active_keys());
    }
    assert_eq!(
        WindowsHook::default().handle(stroke(0x45, true), false, base),
        HookDecision::default()
    );
}

#[test]
fn hook_new_physical_down_closes_stale_press_before_emitting_new_action() {
    let mut hook = WindowsHook::default();
    let observation = firmware(Device::Rz09_0528, Backend::WindowsHook);
    let down = stroke(0x43, true);
    let lost = Observation {
        status: Status::Unknown,
        ..observation
    };
    assert_eq!(hook.handle(down, false, lost), HookDecision::default());
    assert_eq!(
        hook.handle(down, false, observation).event,
        Some(event(0x43, Action::DisplayBrightnessUp, Phase::Down))
    );
    let repeat = hook.handle(down, true, lost);
    assert!(repeat.suppress);
    assert_eq!(
        repeat.event,
        Some(event(0x43, Action::DisplayBrightnessUp, Phase::Repeat))
    );
    let next = hook.handle(down, false, observation);
    assert_eq!(
        next.stale_release,
        Some(event(0x43, Action::DisplayBrightnessUp, Phase::Up))
    );
    assert_eq!(
        next.event,
        Some(event(0x43, Action::DisplayBrightnessUp, Phase::Down))
    );
}

#[test]
fn hook_recovery_never_starts_an_action_and_can_let_the_paired_up_through() {
    let mut hook = WindowsHook::default();
    let observation = firmware(Device::Rz09_0581, Backend::WindowsHook);
    let down = stroke(0x43, true);
    let up = stroke(0x43, false);
    assert!(hook.handle(down, false, observation).suppress);
    assert!(!hook.recover_missed_edge(down, true, observation).reinstall);
    assert!(
        !hook
            .recover_missed_edge(down, false, host_fn(true))
            .reinstall
    );
    assert!(hook.recover_missed_edge(down, false, observation).reinstall);
    assert!(hook.handle(down, true, observation).suppress);
    let release = hook.handle(up, false, observation);
    assert!(!release.suppress);
    assert_eq!(
        release.event,
        Some(event(0x43, Action::DisplayBrightnessUp, Phase::Up))
    );
    assert!(
        hook.recover_missed_edge(down, false, observation)
            .release
            .is_none()
    );
    assert_eq!(
        hook.handle(down, true, observation),
        HookDecision::default()
    );
    assert!(hook.recover_missed_edge(up, false, observation).reinstall);
    assert!(!hook.has_active_keys());

    assert!(hook.handle(down, false, observation).suppress);
    assert_eq!(
        hook.recover_missed_edge(up, false, observation).release,
        Some(event(0x43, Action::DisplayBrightnessUp, Phase::Up))
    );
    assert_eq!(hook.handle(up, false, observation), HookDecision::default());
}

#[test]
fn driver_routes_both_fn_layers_on_all_three_exact_devices() {
    for device in [Device::Rz09_0581, Device::Rz09_0528, Device::Rz09_0484] {
        let base = if device == Device::Rz09_0484 {
            // The host supplies fresh stroke admission on the exact 02A0 path.
            host_fn(true)
        } else {
            firmware(device, Backend::RazerDriver)
        };
        for primary in [PrimaryMode::MultimediaKeys, PrimaryMode::FunctionKeys] {
            for fn_pressed in [true, false] {
                let mut driver = RazerDriver::new(device);
                let mut script = Script::default();
                let obs = Observation {
                    fn_pressed: Some(fn_pressed),
                    primary_mode: Some(primary),
                    ..base
                };
                let down = stroke(0x3b, true);
                let up = stroke(0x3b, false);
                assert_eq!(
                    driver.handle(down, obs, &mut script),
                    Ok(DriverProgress::Continue)
                );
                driver.handle(up, obs, &mut script).unwrap();
                if fn_pressed == (primary == PrimaryMode::FunctionKeys) {
                    assert_eq!(
                        script.observed,
                        [
                            Operation::Publish(event(0x3b, Action::VolumeMute, Phase::Down)),
                            Operation::Publish(event(0x3b, Action::VolumeMute, Phase::Up)),
                        ]
                    );
                } else {
                    assert_eq!(
                        script.observed,
                        [Operation::Forward(down), Operation::Forward(up)]
                    );
                }
            }
        }
    }
}

#[test]
fn a_fresh_fn_stroke_check_and_the_exact_path_are_required() {
    for (device, obs) in [
        (Device::Rz09_0484, host_fn(false)),
        (
            Device::Rz09_0484,
            Observation {
                fn_pressed: None,
                ..host_fn(true)
            },
        ),
        (
            Device::Rz09_0484,
            firmware(Device::Rz09_0484, Backend::RazerDriver),
        ),
        (
            Device::Rz09_0581,
            Observation {
                exact_device: Some(Device::Rz09_0581),
                ..host_fn(true)
            },
        ),
    ] {
        let ready = if device == Device::Rz09_0484 {
            host_fn(true)
        } else {
            firmware(device, Backend::RazerDriver)
        };
        let mut driver = RazerDriver::new(device);
        let mut script = Script::default();
        let down = stroke(0x43, true);
        let up = stroke(0x43, false);
        driver.handle(down, obs, &mut script).unwrap();
        driver.handle(down, ready, &mut script).unwrap();
        driver.handle(up, ready, &mut script).unwrap();
        assert_eq!(
            script.observed,
            [
                Operation::Forward(down),
                Operation::Forward(down),
                Operation::Forward(up)
            ]
        );
    }
}

#[test]
fn driver_held_action_remains_paired_after_fn_or_admission_changes() {
    let mut driver = RazerDriver::new(Device::Rz09_0484);
    let mut script = Script::default();
    let ready = host_fn(true);
    let down = stroke(0x43, true);
    let up = stroke(0x43, false);
    driver.handle(down, ready, &mut script).unwrap();
    driver.handle(down, host_fn(false), &mut script).unwrap();
    // Changing the Fn layer keeps the latched action once stroke admission returns.
    driver
        .handle(
            down,
            Observation {
                fn_pressed: Some(true),
                ..ready
            },
            &mut script,
        )
        .unwrap();
    driver.handle(up, host_fn(false), &mut script).unwrap();
    driver.handle(up, ready, &mut script).unwrap();
    assert_eq!(
        script.observed,
        [
            Operation::Publish(event(0x43, Action::DisplayBrightnessUp, Phase::Down)),
            Operation::Publish(event(0x43, Action::DisplayBrightnessUp, Phase::Repeat)),
            Operation::Publish(event(0x43, Action::DisplayBrightnessUp, Phase::Up)),
            Operation::Forward(up),
        ]
    );
}

#[test]
fn stopping_drains_held_keys_before_transfer_and_starts_no_media() {
    let mut driver = RazerDriver::new(Device::Rz09_0528);
    let mut script = Script::default();
    let active = firmware(Device::Rz09_0528, Backend::RazerDriver);
    let stop = Observation {
        selected_backend: Some(Backend::WindowsHook),
        lifecycle: Lifecycle::Stopping,
        ..active
    };
    let down = stroke(0x43, true);
    let up = stroke(0x43, false);
    let other_down = stroke(0x57, true);
    let other_up = stroke(0x57, false);

    driver.handle(down, active, &mut script).unwrap();
    driver.handle(down, stop, &mut script).unwrap();
    driver.handle(other_down, stop, &mut script).unwrap();
    assert_eq!(
        driver.handle(up, stop, &mut script),
        Ok(DriverProgress::Continue)
    );
    assert_eq!(
        driver.handle(other_up, active, &mut script),
        Ok(DriverProgress::Retire)
    );
    assert!(!driver.retired_cleanly());
    driver.cleanup(&mut script).unwrap();
    assert!(driver.retired_cleanly());
    assert_eq!(
        driver.handle(down, active, &mut script),
        Ok(DriverProgress::Retire)
    );
    driver.cleanup(&mut script).unwrap();
    assert_eq!(
        script.observed,
        [
            Operation::Publish(event(0x43, Action::DisplayBrightnessUp, Phase::Down)),
            Operation::Forward(other_down),
            Operation::Publish(event(0x43, Action::DisplayBrightnessUp, Phase::Up)),
            Operation::Forward(other_up),
            Operation::Reset,
        ]
    );
}

#[test]
fn identity_loss_retires_without_forwarding_and_cleans_up_old_media() {
    for changed in [None, Some(Device::Rz09_0528)] {
        let mut driver = RazerDriver::new(Device::Rz09_0581);
        let mut script = Script::default();
        let active = firmware(Device::Rz09_0581, Backend::RazerDriver);
        driver
            .handle(stroke(0x43, true), active, &mut script)
            .unwrap();
        let changed = Observation {
            exact_device: changed,
            ..active
        };
        assert_eq!(
            driver.handle(stroke(0x43, false), changed, &mut script),
            Ok(DriverProgress::Retire)
        );
        driver.cleanup(&mut script).unwrap();
        assert!(driver.has_tracked_keys());
        assert!(!driver.retired_cleanly());
        assert_eq!(
            script.observed,
            [
                Operation::Publish(event(0x43, Action::DisplayBrightnessUp, Phase::Down)),
                Operation::Publish(event(0x43, Action::DisplayBrightnessUp, Phase::Up)),
                Operation::Reset,
            ]
        );
    }
}

#[test]
fn possibly_submitted_forward_is_never_replayed_even_if_no_key_remains() {
    let down = stroke(0x43, true);
    let up = stroke(0x43, false);
    for failed in [down, up] {
        let mut driver = RazerDriver::new(Device::Rz09_0581);
        let mut script = Script {
            fail: Some(Operation::Forward(failed)),
            ..Script::default()
        };
        let obs = Observation {
            status: Status::Unknown,
            ..firmware(Device::Rz09_0581, Backend::RazerDriver)
        };
        if failed == up {
            driver.handle(down, obs, &mut script).unwrap();
        }
        assert_eq!(
            driver.handle(failed, obs, &mut script),
            Err(DriverFailure::Forward(Operation::Forward(failed)))
        );
        assert!(driver.output_uncertain());
        assert_eq!(driver.has_tracked_keys(), failed.down);
        assert_eq!(
            driver.handle(failed, obs, &mut script),
            Err(DriverFailure::Faulted)
        );
        driver.cleanup(&mut script).unwrap();
        assert!(!driver.retired_cleanly());
        assert_eq!(
            script
                .observed
                .iter()
                .filter(|&&op| op == Operation::Forward(failed))
                .count(),
            1
        );
        assert_eq!(script.observed.last(), Some(&Operation::Reset));
    }
}

#[test]
fn unexpected_driver_scan_faults_without_submitting_output() {
    let mut driver = RazerDriver::new(Device::Rz09_0581);
    let mut script = Script::default();
    let obs = firmware(Device::Rz09_0581, Backend::RazerDriver);
    assert_eq!(
        driver.handle(stroke(0x45, true), obs, &mut script),
        Err(DriverFailure::UnexpectedScan)
    );
    assert_eq!(
        driver.handle(stroke(0x43, true), obs, &mut script),
        Err(DriverFailure::Faulted)
    );
    assert!(!driver.output_uncertain());
    driver.cleanup(&mut script).unwrap();
    assert!(!driver.retired_cleanly());
    assert_eq!(script.observed, [Operation::Reset]);
}

#[test]
fn cleanup_attempts_each_release_once_then_resets_even_on_failure() {
    let mut driver = RazerDriver::new(Device::Rz09_0581);
    let down_event = Operation::Publish(event(0x43, Action::DisplayBrightnessUp, Phase::Down));
    let up_event = Operation::Publish(event(0x43, Action::DisplayBrightnessUp, Phase::Up));
    let mut script = Script {
        fail: Some(up_event),
        ..Script::default()
    };
    let obs = firmware(Device::Rz09_0581, Backend::RazerDriver);
    driver.handle(stroke(0x43, true), obs, &mut script).unwrap();
    driver.handle(stroke(0x57, true), obs, &mut script).unwrap();
    // A host drain deadline can retire with outstanding physical keys.
    assert_eq!(driver.cleanup(&mut script), Err(up_event));
    assert!(!driver.retired_cleanly());
    driver.cleanup(&mut script).unwrap();
    assert_eq!(
        script.observed,
        [
            down_event,
            Operation::Publish(event(0x57, Action::KeyboardBrightnessUp, Phase::Down)),
            up_event,
            Operation::Publish(event(0x57, Action::KeyboardBrightnessUp, Phase::Up)),
            Operation::Reset,
        ]
    );
}

#[test]
fn failed_action_publication_faults_but_still_releases_and_resets() {
    let mut driver = RazerDriver::new(Device::Rz09_0484);
    let down_event = Operation::Publish(event(0x43, Action::DisplayBrightnessUp, Phase::Down));
    let up_event = Operation::Publish(event(0x43, Action::DisplayBrightnessUp, Phase::Up));
    let mut script = Script {
        fail: Some(down_event),
        ..Script::default()
    };
    let obs = host_fn(true);
    let down = stroke(0x43, true);
    assert_eq!(
        driver.handle(down, obs, &mut script),
        Err(DriverFailure::Publish(down_event))
    );
    assert!(!driver.output_uncertain());
    assert_eq!(
        driver.handle(down, obs, &mut script),
        Err(DriverFailure::Faulted)
    );
    driver.cleanup(&mut script).unwrap();
    assert_eq!(script.observed, [down_event, up_event, Operation::Reset]);
}

#[test]
fn an_already_intercepted_edge_is_forwarded_if_selection_changes_during_read() {
    let mut driver = RazerDriver::new(Device::Rz09_0528);
    let mut script = Script::default();
    let active = firmware(Device::Rz09_0528, Backend::RazerDriver);
    let changed = Observation {
        selected_backend: Some(Backend::WindowsHook),
        ..active
    };
    let down = stroke(0x43, true);
    let up = stroke(0x43, false);
    assert_eq!(
        driver.handle(down, changed, &mut script),
        Ok(DriverProgress::Continue)
    );
    assert_eq!(
        driver.handle(up, active, &mut script),
        Ok(DriverProgress::Retire)
    );
    driver.cleanup(&mut script).unwrap();
    assert!(driver.retired_cleanly());
    assert_eq!(
        script.observed,
        [
            Operation::Forward(down),
            Operation::Forward(up),
            Operation::Reset
        ]
    );
}

#[test]
fn hook_recovery_requires_selection_or_a_key_left_to_reconcile() {
    let mut hook = WindowsHook::default();
    let active = firmware(Device::Rz09_0581, Backend::WindowsHook);
    let switched = firmware(Device::Rz09_0581, Backend::RazerDriver);
    let down = stroke(0x43, true);
    let up = stroke(0x43, false);

    assert!(!hook.recover_missed_edge(down, false, switched).reinstall);
    assert!(!hook.has_active_keys());
    assert!(hook.handle(down, false, active).suppress);
    let recovered = hook.recover_missed_edge(up, false, switched);
    assert!(recovered.reinstall);
    assert_eq!(
        recovered.release,
        Some(event(0x43, Action::DisplayBrightnessUp, Phase::Up))
    );
    assert!(!hook.recover_missed_edge(down, false, switched).reinstall);
}

#[test]
fn hook_first_seen_repeat_passes_through_until_the_matching_up() {
    let mut hook = WindowsHook::default();
    let active = firmware(Device::Rz09_0581, Backend::WindowsHook);
    let down = stroke(0x3d, true);
    let up = stroke(0x3d, false);
    assert_eq!(hook.handle(down, true, active), HookDecision::default());
    assert_eq!(hook.handle(down, true, active), HookDecision::default());
    assert_eq!(hook.handle(up, false, active), HookDecision::default());
    assert!(!hook.has_active_keys());
    assert_eq!(
        hook.handle(down, false, active).event,
        Some(event(0x3d, Action::VolumeUp, Phase::Down))
    );
}

#[test]
fn stopping_hook_closes_held_actions_without_emitting_more_repeats() {
    let mut hook = WindowsHook::default();
    let active = firmware(Device::Rz09_0581, Backend::WindowsHook);
    let stopping = Observation {
        lifecycle: Lifecycle::Stopping,
        ..active
    };
    let down = stroke(0x43, true);
    assert!(hook.handle(down, false, active).suppress);
    assert_eq!(
        hook.handle(down, true, stopping),
        HookDecision {
            suppress: true,
            ..HookDecision::default()
        }
    );
    let release = hook.handle(stroke(0x43, false), false, stopping);
    assert!(release.suppress);
    assert_eq!(
        release.event,
        Some(event(0x43, Action::DisplayBrightnessUp, Phase::Up))
    );
    assert!(!hook.has_active_keys());
}

#[test]
fn transient_unknown_selection_passes_the_press_without_retiring_the_driver() {
    let mut driver = RazerDriver::new(Device::Rz09_0528);
    let mut script = Script::default();
    let active = firmware(Device::Rz09_0528, Backend::RazerDriver);
    let unknown = Observation {
        selected_backend: None,
        ..active
    };
    let down = stroke(0x43, true);
    let up = stroke(0x43, false);

    driver.handle(down, unknown, &mut script).unwrap();
    assert_eq!(
        driver.handle(up, active, &mut script),
        Ok(DriverProgress::Continue)
    );
    driver.handle(down, active, &mut script).unwrap();
    assert_eq!(
        script.observed,
        [
            Operation::Forward(down),
            Operation::Forward(up),
            Operation::Publish(event(0x43, Action::DisplayBrightnessUp, Phase::Down)),
        ]
    );
}

#[test]
fn failed_up_publication_remains_a_retirement_fault_after_reset() {
    let mut driver = RazerDriver::new(Device::Rz09_0581);
    let down = stroke(0x43, true);
    let up = stroke(0x43, false);
    let down_event = Operation::Publish(event(0x43, Action::DisplayBrightnessUp, Phase::Down));
    let up_event = Operation::Publish(event(0x43, Action::DisplayBrightnessUp, Phase::Up));
    let mut script = Script {
        fail: Some(up_event),
        ..Script::default()
    };
    let active = firmware(Device::Rz09_0581, Backend::RazerDriver);
    driver.handle(down, active, &mut script).unwrap();
    assert_eq!(
        driver.handle(up, active, &mut script),
        Err(DriverFailure::Publish(up_event))
    );
    assert!(!driver.has_tracked_keys());
    assert!(!driver.output_uncertain());
    driver.cleanup(&mut script).unwrap();
    assert!(!driver.retired_cleanly());
    assert_eq!(script.observed, [down_event, up_event, Operation::Reset]);
}

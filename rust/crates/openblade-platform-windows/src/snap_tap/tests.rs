use super::*;

const PRODUCT: &str = "Blade 16 - RZ09-0581";
const TARGET: Context = Context {
    foreground: 1,
    layout: 1,
};

fn admission() -> Admission<'static> {
    Admission {
        bios_manufacturer: "Razer",
        bios_product: PRODUCT,
        system_manufacturer: "Razer",
        system_product: PRODUCT,
        active_console: true,
        controllers_complete: true,
        competing_controller: false,
    }
}

fn ownership() -> OwnershipAdmission {
    OwnershipAdmission {
        driver_marker_clear: true,
        windows_marker_acquired: true,
    }
}

fn start() -> SnapTap {
    SnapTap::start(0, &admission(), Some(31), false, ownership())
        .unwrap()
        .unwrap()
}

fn observation(context: Context, at: u64) -> Observation {
    Observation {
        context,
        at,
        active_console: true,
        stop_requested: false,
    }
}

struct Fake {
    observation: Observation,
    submitted: Vec<Batch>,
    count_override: Option<i64>,
    delay: u64,
}

impl Fake {
    fn new() -> Self {
        Self {
            observation: observation(TARGET, 0),
            submitted: Vec::new(),
            count_override: None,
            delay: 0,
        }
    }
}

impl Output for Fake {
    fn observe(&mut self) -> Observation {
        self.observation
    }

    fn submit(&mut self, batch: Batch) -> Submission {
        self.submitted.push(batch);
        match self.count_override.take() {
            Some(-1) => Submission::Uncertain,
            Some(-2) => panic!("scripted unwind after a possible submission"),
            count => Submission::Count {
                accepted: count.map_or(batch.count(), |count| usize::try_from(count).unwrap()),
                completed_at: self.observation.at + self.delay,
            },
        }
    }
}

fn format_stroke(stroke: Stroke) -> String {
    format!("{:?}{}", stroke.key, if stroke.down { "+" } else { "-" })
}

fn format_batch(batch: Batch) -> String {
    match batch {
        Batch::One(stroke) => format_stroke(stroke),
        Batch::Pair(first, second) => format!("{},{}", format_stroke(first), format_stroke(second)),
    }
}

#[test]
fn managed_windows_hook_trace() {
    let fixture = include_str!("../../../../../tests/Fixtures/Parity/SnapTap/windows-hook.txt");
    let mut state = start();
    let mut fake = Fake::new();
    let mut case = "";
    for (index, line) in fixture.lines().enumerate() {
        if line.starts_with('#') || line.is_empty() {
            continue;
        }
        let cols: Vec<_> = line.split('|').collect();
        if cols[0] == "case" {
            case = cols[1];
            state = start();
            fake = Fake::new();
            continue;
        }
        let label = format!("{case}:{}", index + 1);
        let args: Vec<i64> = cols[1].split(',').map(|x| x.parse().unwrap()).collect();
        let ctx = |i| Context {
            foreground: usize::try_from(args[i]).unwrap(),
            layout: usize::try_from(args[i + 1]).unwrap(),
        };
        let decision = match cols[0] {
            "arm" => {
                state.reconcile(ctx(0), args[2] != 0, args[3] != 0);
                "-"
            }
            "key" => {
                fake.observation = observation(ctx(3), 0);
                fake.count_override = args.get(5).copied();
                let result = state.on_key(
                    fake.observation,
                    Some(HookEvent {
                        scan: u32::try_from(args[0]).unwrap(),
                        flags: u32::try_from(args[1]).unwrap(),
                        message: usize::try_from(args[2]).unwrap(),
                    }),
                    &mut fake,
                );
                match (result.handled, result.suppress) {
                    (false, false) => "pass",
                    (true, false) => "forward",
                    (true, true) => "suppress",
                    (false, true) => "block",
                }
            }
            "stop" => {
                fake.observation = Observation {
                    active_console: args[2] != 0,
                    stop_requested: true,
                    ..observation(ctx(0), 0)
                };
                fake.count_override = args.get(3).copied();
                state.stop(fake.observation, &mut fake);
                "-"
            }
            op => panic!("unknown operation: {op}"),
        };
        assert_eq!(decision, cols[2], "{label}");
        let winner = match state.winner() {
            Some(Key::A) => "A",
            Some(Key::D) => "D",
            None => "-",
        };
        assert_eq!(winner, cols[3], "{label}");
        assert_eq!(format!("{:?}", state.status()), cols[4], "{label}");
        let failure = state.failure().map_or_else(
            || "-".into(),
            |f| {
                format!(
                    "{:?}{}",
                    f.reason,
                    if f.output_uncertain { "!" } else { "" }
                )
            },
        );
        assert_eq!(failure, cols[5], "{label}");
        let actual = fake
            .submitted
            .drain(..)
            .map(format_batch)
            .collect::<Vec<_>>()
            .join(";");
        assert_eq!(
            if actual.is_empty() { "-" } else { &actual },
            cols[6],
            "{label}"
        );
    }
}

fn key(state: &mut SnapTap, fake: &mut Fake, scan: u32, down: bool) -> Decision {
    state.on_key(
        fake.observation,
        Some(HookEvent {
            scan,
            flags: 0,
            message: if down { 0x100 } else { 0x101 },
        }),
        fake,
    )
}

fn restored() -> (SnapTap, Fake) {
    let mut state = start();
    let mut fake = Fake::new();
    state.reconcile(TARGET, true, true);
    key(&mut state, &mut fake, 0x1e, true);
    key(&mut state, &mut fake, 0x20, true);
    fake.delay = 5;
    key(&mut state, &mut fake, 0x20, false);
    fake.submitted.clear();
    (state, fake)
}

#[test]
fn restored_repeat_keeps_phase_and_yields_to_hardware() {
    let (mut state, mut fake) = restored();
    let interval = 333_333;
    for (at, batches) in [
        (interval + 4, 0),
        (interval + 5, 1),
        (4 * interval + 10, 2),
        (5 * interval + 4, 2),
        (5 * interval + 5, 3),
    ] {
        fake.observation.at = at;
        state.repeat(at, false, &mut fake);
        assert_eq!(fake.submitted.len(), batches);
    }
    assert!(fake.submitted.iter().all(|b| *b
        == Batch::One(Stroke {
            key: Key::A,
            down: true
        })));
    assert!(key(&mut state, &mut fake, 0x1e, true).suppress);
    assert!(!key(&mut state, &mut fake, 0x1e, true).suppress);
    state.repeat(6 * interval + 5, false, &mut fake);
    assert_eq!(fake.submitted.len(), 3);

    let (mut state, mut fake) = restored();
    assert!(!key(&mut state, &mut fake, 0x1e, true).suppress);
    state.repeat(interval + 5, false, &mut fake);
    assert!(fake.submitted.is_empty());
}

#[test]
fn cancellation_bypasses_callback_and_timer_but_allows_one_confirmed_release() {
    let (mut state, mut fake) = restored();
    fake.observation.at = 400_000;
    fake.observation.stop_requested = true;
    assert_eq!(key(&mut state, &mut fake, 0x1e, false), Decision::default());
    state.repeat(400_000, true, &mut fake);
    assert!(fake.submitted.is_empty());
    state.stop(fake.observation, &mut fake);
    state.stop(fake.observation, &mut fake);
    state.repeat(500_000, false, &mut fake);
    assert_eq!(
        fake.submitted,
        [Batch::One(Stroke {
            key: Key::A,
            down: false
        })]
    );
    assert_eq!(state.status(), Status::Off);
}

#[test]
fn sink_rechecks_and_uncertain_repeat_is_never_retried() {
    for result in [-1, -2] {
        let (mut state, mut fake) = restored();
        fake.observation.at = 400_000;
        fake.count_override = Some(result);
        state.repeat(400_000, false, &mut fake);
        state.repeat(800_000, false, &mut fake);
        state.stop(fake.observation, &mut fake);
        assert_eq!(fake.submitted.len(), 1);
        assert_eq!(
            state.failure(),
            Some(Failure {
                reason: Reason::OutputFailed,
                output_uncertain: true
            })
        );
    }

    for fresh in [
        Observation {
            context: Context {
                layout: 2,
                ..TARGET
            },
            ..observation(TARGET, 0)
        },
        Observation {
            active_console: false,
            ..observation(TARGET, 0)
        },
        Observation {
            stop_requested: true,
            ..observation(TARGET, 0)
        },
        observation(TARGET, ADMISSION_MAX_AGE_MS * TICKS_PER_MS + 1),
    ] {
        let mut state = start();
        let mut fake = Fake::new();
        state.reconcile(TARGET, true, true);
        key(&mut state, &mut fake, 0x1e, true);
        fake.observation = fresh;
        let decision = state.on_key(
            observation(TARGET, 0),
            Some(HookEvent {
                scan: 0x20,
                flags: 0,
                message: 0x100,
            }),
            &mut fake,
        );
        assert_eq!(
            decision,
            Decision {
                handled: false,
                suppress: true
            }
        );
        assert_eq!(
            state.failure(),
            Some(Failure {
                reason: Reason::OutputAdmissionRejected,
                output_uncertain: true
            })
        );
        assert!(fake.submitted.is_empty());
    }
}

#[test]
fn exact_admission_expiry_and_ownership_gates() {
    for product in PRODUCTS {
        assert_eq!(
            Admission {
                bios_product: product,
                system_product: product,
                ..admission()
            }
            .rejection(),
            None
        );
    }
    for (snapshot, reason) in [
        (
            Admission {
                system_manufacturer: "razer",
                ..admission()
            },
            Reason::UnsupportedModel,
        ),
        (
            Admission {
                system_product: PRODUCTS[1],
                ..admission()
            },
            Reason::UnsupportedModel,
        ),
        (
            Admission {
                bios_product: "Blade",
                system_product: "Blade",
                ..admission()
            },
            Reason::UnsupportedModel,
        ),
        (
            Admission {
                active_console: false,
                ..admission()
            },
            Reason::InactiveConsoleSession,
        ),
        (
            Admission {
                controllers_complete: false,
                competing_controller: true,
                ..admission()
            },
            Reason::IncompleteControllerObservation,
        ),
        (
            Admission {
                competing_controller: true,
                ..admission()
            },
            Reason::CompetingController,
        ),
    ] {
        assert_eq!(
            SnapTap::start(0, &snapshot, Some(31), false, ownership()).err(),
            Some(reason)
        );
    }
    assert_eq!(
        SnapTap::start(0, &admission(), Some(32), false, ownership()).err(),
        Some(Reason::KeyboardSpeedUnavailable)
    );
    assert_eq!(
        SnapTap::start(
            0,
            &admission(),
            Some(31),
            false,
            OwnershipAdmission {
                driver_marker_clear: false,
                ..ownership()
            }
        )
        .err(),
        Some(Reason::DriverOwnershipRecoveryRequired)
    );
    assert_eq!(
        SnapTap::start(
            0,
            &admission(),
            Some(31),
            false,
            OwnershipAdmission {
                windows_marker_acquired: false,
                ..ownership()
            }
        )
        .err(),
        Some(Reason::WindowsOwnershipRecoveryRequired)
    );
    assert!(
        SnapTap::start(0, &admission(), Some(31), true, ownership())
            .unwrap()
            .is_none()
    );

    let mut state = start();
    let mut fake = Fake::new();
    state.reconcile(TARGET, true, true);
    let stale = observation(TARGET, ADMISSION_MAX_AGE_MS * TICKS_PER_MS + 1);
    assert_eq!(state.on_key(stale, None, &mut fake), Decision::default());
    assert_eq!(
        state.on_key(
            stale,
            Some(HookEvent {
                scan: 0x20,
                flags: 0x10,
                message: 0x100
            }),
            &mut fake
        ),
        Decision::default()
    );
    assert_eq!(state.status(), Status::Armed);
    assert_eq!(
        state.on_key(
            stale,
            Some(HookEvent {
                scan: 0x21,
                flags: 0,
                message: 0x100
            }),
            &mut fake
        ),
        Decision::default()
    );
    assert_eq!(
        state.failure(),
        Some(Failure {
            reason: Reason::AdmissionExpired,
            output_uncertain: false
        })
    );

    let mut state = start();
    let refresh = ADMISSION_REFRESH_MS * TICKS_PER_MS;
    assert!(!state.refresh_due(refresh - 1));
    assert!(state.refresh_due(refresh));
    state.refresh(refresh, &admission());
    assert!(state.current(observation(
        TARGET,
        refresh + ADMISSION_MAX_AGE_MS * TICKS_PER_MS
    )));
    state.reconcile(TARGET, true, true);
    fake.observation.at = refresh;
    key(&mut state, &mut fake, 0x20, true);
    state.refresh(
        2 * refresh,
        &Admission {
            competing_controller: true,
            ..admission()
        },
    );
    state.stop(fake.observation, &mut fake);
    assert_eq!(
        state.failure(),
        Some(Failure {
            reason: Reason::CompetingController,
            output_uncertain: true
        })
    );
    assert!(fake.submitted.is_empty());
}

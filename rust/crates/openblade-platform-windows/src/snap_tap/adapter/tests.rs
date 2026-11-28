use super::*;
use crate::snap_tap::{Batch, Key, Observation, Stroke, Submission};
use std::{cell::RefCell, collections::VecDeque, rc::Rc};

mod supervisor;

const TIMER: usize = 7;
const TARGET: Context = Context {
    foreground: 5,
    layout: 9,
};

#[derive(Clone, Copy)]
enum Step {
    Key {
        scan: u32,
        flags: u32,
        message: usize,
    },
    Tick {
        id: usize,
        at: u64,
    },
    Cancel,
    SignalStop,
    WaitForSupervisorStop,
    Disconnect,
    RevokeAtStop,
    Revoke,
    ReceiveError,
    Quit,
}

#[derive(Debug, PartialEq, Eq)]
enum Event {
    Install,
    StartTimer,
    Forward(u32, u32, usize),
    Suppress(u32, usize),
    Dispatch(usize),
    Submit(Batch),
    Unhook,
    KillTimer,
}

struct State {
    at: u64,
    stop: bool,
    console: bool,
    competing: bool,
    observed: usize,
    install_error: bool,
    timer_setup_error: bool,
    retirement_errors: bool,
    results: VecDeque<Submission>,
    events: Vec<Event>,
}

impl Default for State {
    fn default() -> Self {
        Self {
            at: 0,
            stop: false,
            console: true,
            competing: false,
            observed: 0,
            install_error: false,
            timer_setup_error: false,
            retirement_errors: false,
            results: VecDeque::new(),
            events: Vec::new(),
        }
    }
}

#[derive(Clone)]
struct Fake(Rc<RefCell<State>>);

impl Output for Fake {
    fn observe(&mut self) -> Observation {
        let mut state = self.0.borrow_mut();
        state.observed += 1;
        Observation {
            at: state.at,
            context: TARGET,
            active_console: state.console,
            stop_requested: state.stop,
        }
    }

    fn submit(&mut self, batch: Batch) -> Submission {
        let mut state = self.0.borrow_mut();
        state.events.push(Event::Submit(batch));
        state.results.pop_front().unwrap_or(Submission::Count {
            accepted: batch.count(),
            completed_at: state.at,
        })
    }
}

impl Input for Fake {
    fn admission(&mut self) -> Admission<'_> {
        let state = self.0.borrow();
        Admission {
            bios_manufacturer: "Razer",
            bios_product: "Blade 16 - RZ09-0581",
            system_manufacturer: "Razer",
            system_product: "Blade 16 - RZ09-0581",
            active_console: state.console,
            controllers_complete: true,
            competing_controller: state.competing,
        }
    }

    fn stop_requested(&self) -> bool {
        self.0.borrow().stop
    }

    fn keyboard_speed(&self) -> Option<u32> {
        Some(31)
    }

    fn released(&self, context: Context) -> (bool, bool) {
        assert_eq!(context, TARGET);
        (true, true)
    }
}

struct FakeMessages {
    state: Fake,
    steps: VecDeque<Step>,
}

impl Messages for FakeMessages {
    type Hook = u32;
    type Message = usize;

    fn install(&mut self) -> Result<u32, u32> {
        let mut state = self.state.0.borrow_mut();
        state.events.push(Event::Install);
        if state.install_error { Err(11) } else { Ok(1) }
    }

    fn start_timer(&mut self) -> Result<usize, u32> {
        let mut state = self.state.0.borrow_mut();
        state.events.push(Event::StartTimer);
        if state.timer_setup_error {
            Err(12)
        } else {
            Ok(TIMER)
        }
    }

    fn receive(&mut self, route: &mut Route<'_>) -> Result<Option<usize>, u32> {
        // GetMessage may deliver several sent hook callbacks before a queued
        // message is retrieved. Preserve that distinction in the script.
        loop {
            match self.steps.pop_front().expect("script must terminate") {
                Step::Key {
                    scan,
                    flags,
                    message,
                } => {
                    let event = HookEvent {
                        scan,
                        flags,
                        message,
                    };
                    let bypass = flags & 0x12 != 0 || event.stroke().is_none();
                    let before = self.state.0.borrow().observed;
                    let suppress = route(event);
                    let mut state = self.state.0.borrow_mut();
                    if bypass {
                        assert_eq!(state.observed, before);
                        assert!(!suppress);
                    }
                    state.events.push(if suppress {
                        Event::Suppress(scan, message)
                    } else {
                        Event::Forward(scan, flags, message)
                    });
                }
                Step::Tick { id, at } => {
                    self.state.0.borrow_mut().at = at;
                    return Ok(Some(id));
                }
                Step::SignalStop => self.state.0.borrow_mut().stop = true,
                Step::WaitForSupervisorStop => {
                    let mut byte = [0];
                    assert_eq!(
                        std::io::Read::read(&mut std::io::stdin(), &mut byte).unwrap(),
                        0
                    );
                    self.state.0.borrow_mut().stop = true;
                    return Ok(Some(TIMER));
                }
                Step::Cancel => {
                    self.state.0.borrow_mut().stop = true;
                    return Ok(Some(TIMER));
                }
                Step::RevokeAtStop => {
                    let mut state = self.state.0.borrow_mut();
                    state.competing = true;
                    state.stop = true;
                    return Ok(Some(TIMER));
                }
                Step::Disconnect => {
                    self.state.0.borrow_mut().console = false;
                    self.state.0.borrow_mut().stop = true;
                    return Ok(Some(TIMER));
                }
                Step::Revoke => {
                    let mut state = self.state.0.borrow_mut();
                    state.at = 2_500_000;
                    state.competing = true;
                    return Ok(Some(TIMER));
                }
                Step::ReceiveError => return Err(13),
                Step::Quit => return Ok(None),
            }
        }
    }

    fn timer_id(message: &usize) -> Option<usize> {
        Some(*message)
    }

    fn dispatch(&mut self, message: usize, _route: &mut Route<'_>) {
        self.state
            .0
            .borrow_mut()
            .events
            .push(Event::Dispatch(message));
    }

    fn unhook(&mut self, hook: u32) -> Result<(), u32> {
        assert_eq!(hook, 1);
        let mut state = self.state.0.borrow_mut();
        state.events.push(Event::Unhook);
        if state.retirement_errors {
            Err(14)
        } else {
            Ok(())
        }
    }

    fn kill_timer(&mut self, timer: usize) -> Result<(), u32> {
        assert_eq!(timer, TIMER);
        let mut state = self.state.0.borrow_mut();
        state.events.push(Event::KillTimer);
        if state.retirement_errors {
            Err(15)
        } else {
            Ok(())
        }
    }
}

fn run_fake(
    state: State,
    steps: impl IntoIterator<Item = Step>,
) -> (Result<Option<Retirement>, Reason>, Vec<Event>) {
    let fake = Fake(Rc::new(RefCell::new(state)));
    let result = run(
        fake.clone(),
        FakeMessages {
            state: fake.clone(),
            steps: steps.into_iter().collect(),
        },
        OwnershipAdmission {
            driver_marker_clear: true,
            windows_marker_acquired: true,
        },
    );
    let events = Rc::try_unwrap(fake.0).ok().unwrap().into_inner().events;
    (result, events)
}

fn key(scan: u32, message: usize) -> Step {
    Step::Key {
        scan,
        flags: 0,
        message,
    }
}

fn stroke(key: Key, down: bool) -> Stroke {
    Stroke { key, down }
}

const ARM: Step = Step::Tick { id: TIMER, at: 0 };

#[test]
fn handoff_restoration_and_owned_timer_preserve_message_order() {
    let (result, events) = run_fake(
        State::default(),
        [
            ARM,
            key(0x1e, 0x100),
            key(0x12, 0x100),
            Step::Key {
                scan: 0x20,
                flags: 0x10,
                message: 0x100,
            },
            Step::Key {
                scan: 0x20,
                flags: 0x02,
                message: 0x104,
            },
            Step::Key {
                scan: 0x20,
                flags: 0x01,
                message: 0x104,
            },
            key(0x20, 0x104),
            key(0x20, 0x105),
            Step::Tick { id: 8, at: 350_000 },
            Step::Tick {
                id: TIMER,
                at: 360_000,
            },
            Step::Cancel,
        ],
    );

    assert_eq!(
        result,
        Ok(Some(Retirement {
            policy_status: Status::Off,
            policy_failure: None,
            loop_failure: None,
            unhook_error: None,
            timer_error: None,
        }))
    );
    assert_eq!(
        events,
        [
            Event::Install,
            Event::StartTimer,
            Event::Dispatch(TIMER),
            Event::Forward(0x1e, 0, 0x100),
            Event::Forward(0x12, 0, 0x100),
            Event::Forward(0x20, 0x10, 0x100),
            Event::Forward(0x20, 0x02, 0x104),
            Event::Forward(0x20, 0x01, 0x104),
            Event::Submit(Batch::One(stroke(Key::A, false))),
            Event::Forward(0x20, 0, 0x104),
            Event::Submit(Batch::Pair(stroke(Key::D, false), stroke(Key::A, true))),
            Event::Suppress(0x20, 0x105),
            Event::Dispatch(8),
            Event::Submit(Batch::One(stroke(Key::A, true))),
            Event::Dispatch(TIMER),
            Event::Submit(Batch::One(stroke(Key::A, false))),
            Event::Unhook,
            Event::KillTimer,
        ]
    );
}

#[test]
fn short_or_ambiguous_restoration_never_replays_and_original_release_passes() {
    for (submission, reason) in [
        (
            Submission::Count {
                accepted: 0,
                completed_at: 0,
            },
            Reason::OutputCountMismatch,
        ),
        (
            Submission::Count {
                accepted: 1,
                completed_at: 0,
            },
            Reason::OutputCountMismatch,
        ),
        (Submission::Uncertain, Reason::OutputFailed),
    ] {
        let (result, events) = run_fake(
            State {
                results: [
                    Submission::Count {
                        accepted: 1,
                        completed_at: 0,
                    },
                    submission,
                ]
                .into(),
                ..State::default()
            },
            [
                ARM,
                key(0x1e, 0x100),
                key(0x20, 0x100),
                key(0x20, 0x101),
                Step::Cancel,
            ],
        );

        let report = result.unwrap().unwrap();
        assert_eq!(report.policy_status, Status::Failed);
        assert_eq!(
            report.policy_failure,
            Some(Failure {
                reason,
                output_uncertain: true
            })
        );
        assert_eq!(
            &events[6..],
            [
                Event::Submit(Batch::Pair(stroke(Key::D, false), stroke(Key::A, true))),
                Event::Forward(0x20, 0, 0x101),
                Event::Unhook,
                Event::KillTimer,
            ]
        );
    }
}

#[test]
fn disconnect_or_revoked_admission_retires_without_sending_a_release() {
    for (end, reason) in [
        (Step::Disconnect, Reason::HeldOutputUncertain),
        (Step::RevokeAtStop, Reason::HeldOutputUncertain),
        (Step::Revoke, Reason::CompetingController),
    ] {
        let (result, events) = run_fake(State::default(), [ARM, key(0x1e, 0x100), end]);
        let report = result.unwrap().unwrap();
        assert_eq!(
            report.policy_failure,
            Some(Failure {
                reason,
                output_uncertain: true
            })
        );
        assert_eq!(&events[4..], [Event::Unhook, Event::KillTimer]);
    }
}

#[test]
fn startup_and_message_failures_retire_only_acquired_resources() {
    let (result, events) = run_fake(
        State {
            install_error: true,
            ..State::default()
        },
        [],
    );
    assert_eq!(
        result.unwrap().unwrap().loop_failure,
        Some(LoopFailure::Install(11))
    );
    assert_eq!(events, [Event::Install]);

    let (result, events) = run_fake(
        State {
            timer_setup_error: true,
            ..State::default()
        },
        [],
    );
    assert_eq!(
        result.unwrap().unwrap().loop_failure,
        Some(LoopFailure::TimerSetup(12))
    );
    assert_eq!(events, [Event::Install, Event::StartTimer, Event::Unhook]);

    let (result, events) = run_fake(
        State {
            retirement_errors: true,
            ..State::default()
        },
        [Step::ReceiveError],
    );
    assert_eq!(
        result.unwrap().unwrap(),
        Retirement {
            policy_status: Status::Failed,
            policy_failure: Some(Failure {
                reason: Reason::HookLoopFailed,
                output_uncertain: false,
            }),
            loop_failure: Some(LoopFailure::Receive(13)),
            unhook_error: Some(14),
            timer_error: Some(15),
        }
    );
    assert_eq!(
        events,
        [
            Event::Install,
            Event::StartTimer,
            Event::Unhook,
            Event::KillTimer
        ]
    );
}

#[test]
fn initial_cancellation_or_competing_controller_does_not_install() {
    let (result, events) = run_fake(
        State {
            stop: true,
            ..State::default()
        },
        [],
    );
    assert_eq!(result, Ok(None));
    assert!(events.is_empty());

    let (result, events) = run_fake(
        State {
            competing: true,
            ..State::default()
        },
        [],
    );
    assert_eq!(result, Err(Reason::CompetingController));
    assert!(events.is_empty());
}

#[test]
fn quit_releases_confirmed_output_before_retirement() {
    let (result, events) = run_fake(State::default(), [ARM, key(0x1e, 0x100), Step::Quit]);
    assert_eq!(result.unwrap().unwrap().policy_status, Status::Off);
    assert_eq!(
        &events[4..],
        [
            Event::Submit(Batch::One(stroke(Key::A, false))),
            Event::Unhook,
            Event::KillTimer,
        ]
    );
}

#[test]
fn failed_handoff_suppresses_the_new_press_and_retires_without_replay() {
    let (result, events) = run_fake(
        State {
            results: [Submission::Count {
                accepted: 0,
                completed_at: 0,
            }]
            .into(),
            ..State::default()
        },
        [ARM, key(0x1e, 0x100), key(0x20, 0x100), Step::Cancel],
    );
    assert_eq!(
        result.unwrap().unwrap().policy_failure,
        Some(Failure {
            reason: Reason::OutputCountMismatch,
            output_uncertain: true
        }),
    );
    assert_eq!(
        &events[4..],
        [
            Event::Submit(Batch::One(stroke(Key::A, false))),
            Event::Suppress(0x20, 0x100),
            Event::Unhook,
            Event::KillTimer,
        ],
    );
}

#[test]
fn forwarded_edges_after_cancellation_cannot_be_released_by_retirement() {
    let (result, events) = run_fake(
        State::default(),
        [
            ARM,
            key(0x1e, 0x100),
            Step::SignalStop,
            key(0x1e, 0x101),
            key(0x1e, 0x100),
            Step::Cancel,
        ],
    );
    assert_eq!(result.unwrap().unwrap().policy_status, Status::Off);
    assert_eq!(
        &events[4..],
        [
            Event::Forward(0x1e, 0, 0x101),
            Event::Forward(0x1e, 0, 0x100),
            Event::Unhook,
            Event::KillTimer,
        ],
    );
}

#[test]
fn message_failure_with_a_winner_poisons_output_before_retirement() {
    let (result, events) = run_fake(
        State::default(),
        [ARM, key(0x1e, 0x100), Step::ReceiveError],
    );
    let report = result.unwrap().unwrap();
    assert_eq!(report.loop_failure, Some(LoopFailure::Receive(13)));
    assert_eq!(
        report.policy_failure,
        Some(Failure {
            reason: Reason::HookLoopFailed,
            output_uncertain: true
        }),
    );
    assert_eq!(&events[4..], [Event::Unhook, Event::KillTimer]);
}

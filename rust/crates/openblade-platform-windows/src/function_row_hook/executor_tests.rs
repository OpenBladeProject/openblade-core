use super::*;
use crate::function_row_hook::executor::{
    ActionEffects, Direction, DisplayBrightnessUsage, EffectOutcome, MediaEffect, WorkerReport,
};
use std::collections::VecDeque;

use Direction::{Decrease, Increase};
use DisplayBrightnessUsage::{Held, Released};
use EffectOutcome::{Confirmed, NoOp, NotSubmitted, PossiblySubmitted, Unsupported};

const CLEAN: WorkerReport = WorkerReport {
    action_failure: None,
    reset: Ok(()),
};

#[derive(Debug, PartialEq, Eq)]
enum Call {
    Media(MediaEffect),
    KeyboardUntilPowerSourceChange(Direction),
    Native(DisplayBrightnessUsage),
    Wmi(Direction),
    Osd(u8),
}

struct Script {
    calls: VecDeque<(Call, EffectOutcome<u8>)>,
    after: Box<dyn FnMut(&Call) + Send>,
}

impl Script {
    fn new(calls: impl IntoIterator<Item = (Call, EffectOutcome<u8>)>) -> Self {
        Self {
            calls: calls.into_iter().collect(),
            after: Box::new(|_| {}),
        }
    }

    fn next(&mut self, call: Call) -> EffectOutcome<u8> {
        let (expected, outcome) = self.calls.pop_front().expect("unexpected effect");
        assert_eq!(call, expected);
        (self.after)(&call);
        outcome
    }

    fn unit(&mut self, call: Call) -> EffectOutcome<()> {
        match self.next(call) {
            Confirmed(_) => Confirmed(()),
            NoOp => NoOp,
            Unsupported => Unsupported,
            NotSubmitted => NotSubmitted,
            PossiblySubmitted => PossiblySubmitted,
        }
    }

    fn finished(&self) {
        assert!(self.calls.is_empty(), "missing effects: {:?}", self.calls);
    }
}

impl ActionEffects for Script {
    fn media(&mut self, effect: MediaEffect) -> EffectOutcome<()> {
        self.unit(Call::Media(effect))
    }

    fn step_keyboard_brightness_until_power_source_change(
        &mut self,
        direction: Direction,
    ) -> EffectOutcome<()> {
        self.unit(Call::KeyboardUntilPowerSourceChange(direction))
    }

    fn display_brightness_usage(&mut self, usage: DisplayBrightnessUsage) -> EffectOutcome<()> {
        self.unit(Call::Native(usage))
    }

    fn adjust_wmi_brightness(&mut self, direction: Direction) -> EffectOutcome<u8> {
        self.next(Call::Wmi(direction))
    }

    fn show_brightness_osd(&mut self, confirmed_level: u8) {
        assert_eq!(self.unit(Call::Osd(confirmed_level)), Confirmed(()));
    }
}

fn run(
    strokes: impl IntoIterator<Item = (u16, bool, bool)>,
    script: &mut Script,
) -> (WorkerReport, HookControl, &'static AtomicBool) {
    let slot = Box::leak(Box::new(AtomicBool::new(false)));
    let (hook, control, worker) = FunctionRowHook::with_executor();
    let mut strokes = strokes.into_iter();
    let mut messages = Fake::new(|callback: &mut Callback, now| {
        if let Some((scan, down, repeat)) = strokes.next() {
            assert!(callback.route(key(scan, down), repeat, now));
        } else {
            control.stop();
        }
        Ok(true)
    });
    control.publish(ACTIVE, messages.now + Duration::from_secs(10));
    assert_eq!(hook.run_with(ADMITTED, &mut messages, slot), Ok(()));
    assert_eq!(messages.calls.last(), Some(&"uninstall"));
    assert!(slot.load(SeqCst));
    assert!(!control.retired_cleanly());
    let result = worker.run(script);
    script.finished();
    (result, control, slot)
}

#[test]
fn every_canonical_key_drains_through_the_typed_executor() {
    use Call::*;
    use MediaEffect::*;
    let mut script = Script::new(
        [
            Media(VolumeMute),
            Media(VolumeDown),
            Media(VolumeDown),
            Media(VolumeUp),
            Media(VolumeUp),
            Media(Project),
            Media(PreviousTrack),
            Media(PlayPause),
            Media(NextTrack),
            Native(Held(Decrease)),
            Native(Held(Decrease)),
            Native(Released),
            Native(Held(Increase)),
            Native(Held(Increase)),
            Native(Released),
            KeyboardUntilPowerSourceChange(Decrease),
            KeyboardUntilPowerSourceChange(Decrease),
            KeyboardUntilPowerSourceChange(Increase),
            KeyboardUntilPowerSourceChange(Increase),
            Media(PrintScreen),
        ]
        .map(|call| (call, Confirmed(0))),
    );
    let strokes = [
        0x3b, 0x3c, 0x3d, 0x3e, 0x3f, 0x40, 0x41, 0x42, 0x43, 0x44, 0x57, 0x58,
    ]
    .into_iter()
    .flat_map(|scan| {
        [
            (scan, true, false),
            (scan, true, true),
            (scan, false, false),
        ]
    });
    let (result, control, slot) = run(strokes, &mut script);
    assert_eq!(result, CLEAN);
    assert!(control.retired_cleanly());
    assert!(!slot.load(SeqCst));
}

#[test]
fn fallback_is_held_per_key_and_osd_requires_a_confirmed_level() {
    use Call::*;
    let mut script = Script::new([
        (Native(Held(Decrease)), NotSubmitted),
        (Wmi(Decrease), Confirmed(30)),
        (Osd(30), Confirmed(0)),
        (Native(Held(Increase)), Confirmed(0)),
        (Wmi(Decrease), NoOp),
        (Wmi(Decrease), Unsupported),
        (Native(Held(Increase)), Confirmed(0)),
        (Native(Released), Confirmed(0)),
    ]);
    let (result, control, _) = run(
        [
            (0x42, true, false),
            (0x43, true, false),
            (0x42, true, true),
            (0x42, true, true),
            (0x42, false, false),
            (0x43, true, true),
            (0x43, false, false),
        ],
        &mut script,
    );
    assert_eq!(result, CLEAN);
    assert!(control.retired_cleanly());
}

#[test]
fn switching_native_direction_releases_the_previous_owner_before_pressing() {
    use Call::Native;
    let mut script = Script::new(
        [
            Native(Held(Decrease)),
            Native(Released),
            Native(Held(Increase)),
            Native(Released),
        ]
        .map(|call| (call, Confirmed(0))),
    );
    let (result, control, _) = run(
        [
            (0x42, true, false),
            (0x43, true, false),
            (0x42, true, true),
            (0x42, false, false),
            (0x43, false, false),
        ],
        &mut script,
    );
    assert_eq!(result, CLEAN);
    assert!(control.retired_cleanly());
}

#[test]
fn confirmed_noop_and_unsupported_native_down_do_not_select_wmi() {
    for outcome in [NoOp, Unsupported] {
        let mut script = Script::new([(Call::Native(Held(Decrease)), outcome)]);
        let (result, control, _) = run(
            [
                (0x42, true, false),
                (0x42, true, true),
                (0x42, false, false),
            ],
            &mut script,
        );
        assert_eq!(result, CLEAN);
        assert!(control.retired_cleanly());
    }
}

#[test]
fn ambiguous_press_or_refresh_neutralizes_once_and_never_falls_back_or_replays() {
    for first in [PossiblySubmitted, Confirmed(0)] {
        let mut calls = vec![(Call::Native(Held(Decrease)), first)];
        if first == Confirmed(0) {
            calls.push((Call::Native(Held(Decrease)), PossiblySubmitted));
        }
        calls.push((Call::Native(Released), Confirmed(0)));
        let mut script = Script::new(calls);
        let (result, control, slot) = run(
            [
                (0x42, true, false),
                (0x42, true, true),
                (0x42, true, true),
                (0x42, false, false),
            ],
            &mut script,
        );
        assert_eq!(
            result,
            WorkerReport {
                action_failure: Some(ActionFailure::PossiblySubmitted),
                reset: Ok(())
            }
        );
        assert!(control.needs_recovery(Recovery::OutputUncertain));
        assert!(control.needs_recovery(Recovery::ReleaseLost));
        assert!(!control.retired_cleanly());
        assert!(slot.load(SeqCst));
    }
}

#[test]
fn failed_release_does_not_switch_routes_and_only_proven_non_submission_can_reset() {
    use Call::Native;
    for outcome in [NotSubmitted, PossiblySubmitted, NoOp, Unsupported] {
        let mut calls = vec![
            (Native(Held(Decrease)), Confirmed(0)),
            (Native(Released), outcome),
        ];
        if outcome == NotSubmitted {
            calls.push((Native(Released), NotSubmitted));
        }
        let mut script = Script::new(calls);
        let (result, control, slot) = run(
            [
                (0x42, true, false),
                (0x43, true, false),
                (0x42, false, false),
                (0x43, false, false),
            ],
            &mut script,
        );
        let failure = if outcome == PossiblySubmitted {
            ActionFailure::PossiblySubmitted
        } else {
            ActionFailure::NotSubmitted
        };
        assert_eq!(
            result,
            WorkerReport {
                action_failure: Some(failure),
                reset: Err(failure)
            }
        );
        assert_eq!(
            control.needs_recovery(Recovery::OutputUncertain),
            outcome == PossiblySubmitted
        );
        assert!(control.needs_recovery(Recovery::ReleaseLost));
        assert!(!control.retired_cleanly());
        assert!(slot.load(SeqCst));
    }
}

#[test]
fn cancellation_skips_queued_ordinary_effects_but_drains_the_held_release() {
    let (hook, control, worker) = FunctionRowHook::with_executor();
    static SLOT: AtomicBool = AtomicBool::new(false);
    let mut script = Script::new([
        (Call::Native(Held(Increase)), Confirmed(0)),
        (Call::Native(Released), Confirmed(0)),
    ]);
    let canceller = control.clone();
    script.after = Box::new(move |call| {
        assert!(!canceller.retired_cleanly());
        if *call == Call::Native(Held(Increase)) {
            canceller.cancel();
        }
    });
    let mut messages = Fake::new(|callback: &mut Callback, now| {
        for (scan, down, repeat) in [
            (0x43, true, false),
            (0x43, true, true),
            (0x3b, true, false),
            (0x43, false, false),
            (0x3b, false, false),
        ] {
            assert!(callback.route(key(scan, down), repeat, now));
        }
        control.stop();
        Ok(true)
    });
    control.publish(ACTIVE, messages.now + Duration::from_secs(1));
    assert_eq!(hook.run_with(ADMITTED, &mut messages, &SLOT), Ok(()));
    assert_eq!(worker.run(&mut script), CLEAN);
    script.finished();
    assert!(control.retired_cleanly());
    assert!(!SLOT.load(SeqCst));
}

#[test]
fn live_worker_resets_after_a_missing_up_and_handoff_stays_blocked() {
    static SLOT: AtomicBool = AtomicBool::new(false);
    run_without_up(&SLOT, false);
}

#[test]
fn panic_during_reset_keeps_release_and_output_ownership_blocked() {
    static SLOT: AtomicBool = AtomicBool::new(false);
    run_without_up(&SLOT, true);
}

fn run_without_up(slot: &'static AtomicBool, reset_panics: bool) {
    let (hook, control, worker) = FunctionRowHook::with_executor();
    let (pressed, received) = sync_channel(1);
    let observer = control.clone();
    let mut script = Script::new([
        (Call::Native(Held(Increase)), Confirmed(0)),
        (Call::Native(Released), Confirmed(0)),
    ]);
    script.after = Box::new(move |call| {
        assert!(!observer.retired_cleanly());
        assert!(slot.load(SeqCst));
        if *call == Call::Native(Held(Increase)) {
            pressed.send(()).unwrap();
        } else if reset_panics {
            panic!("scripted unwind during native release");
        }
    });
    let consumer = std::thread::spawn(move || {
        let result =
            std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| worker.run(&mut script)));
        if reset_panics {
            assert!(result.is_err());
        } else {
            assert_eq!(result.unwrap(), CLEAN);
        }
        script.finished();
    });
    let mut admitted = false;
    let mut messages = Fake::new(|callback: &mut Callback, now| {
        if !admitted {
            admitted = true;
            assert!(callback.route(key(0x43, true), false, now));
            received.recv_timeout(Duration::from_secs(2)).unwrap();
            control.cancel();
        }
        Ok(true)
    });
    control.publish(ACTIVE, messages.now + Duration::from_secs(1));
    assert_eq!(hook.run_with(ADMITTED, &mut messages, slot), Ok(()));
    consumer.join().unwrap();
    assert_eq!(
        control.needs_recovery(Recovery::OutputUncertain),
        reset_panics
    );
    assert!(control.needs_recovery(Recovery::KeysHeld));
    assert!(control.needs_recovery(Recovery::ReleaseLost));
    assert!(!control.retired_cleanly());
    assert!(slot.load(SeqCst));
}

#[test]
fn ambiguous_wmi_adjustment_stops_without_osd_retry_or_native_release() {
    let mut script = Script::new([
        (Call::Native(Held(Decrease)), NotSubmitted),
        (Call::Wmi(Decrease), PossiblySubmitted),
    ]);
    let (report, control, _) = run(
        [
            (0x42, true, false),
            (0x42, true, true),
            (0x42, false, false),
        ],
        &mut script,
    );
    assert_eq!(
        report,
        WorkerReport {
            action_failure: Some(ActionFailure::PossiblySubmitted),
            reset: Ok(())
        }
    );
    assert!(control.needs_recovery(Recovery::OutputUncertain));
    assert!(!control.retired_cleanly());
}

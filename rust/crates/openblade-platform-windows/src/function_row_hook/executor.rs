//! Typed current-user action boundary for the function-row queue.

use super::{ActionFailure, FunctionRowHook, HookActions, HookControl, Recovery, SeqCst};
use openblade_domain::function_row::{Action, Event, Phase};

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum Direction {
    Decrease,
    Increase,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum DisplayBrightnessUsage {
    Held(Direction),
    Released,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum MediaEffect {
    VolumeMute,
    VolumeDown,
    VolumeUp,
    Project,
    PreviousTrack,
    PlayPause,
    NextTrack,
    PrintScreen,
}

/// Only the effect boundary can establish whether submission was possible.
/// NoOp and Unsupported are expected dispositions. NotSubmitted is a
/// positive submission-stage finding and is the only initial native outcome
/// that permits WMI fallback.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum EffectOutcome<T> {
    Confirmed(T),
    NoOp,
    Unsupported,
    NotSubmitted,
    PossiblySubmitted,
}

impl<T> EffectOutcome<T> {
    fn delivery_result(self) -> Result<(), ActionFailure> {
        match self {
            Self::Confirmed(_) | Self::NoOp | Self::Unsupported => Ok(()),
            Self::NotSubmitted => Err(ActionFailure::NotSubmitted),
            Self::PossiblySubmitted => Err(ActionFailure::PossiblySubmitted),
        }
    }
}

/// The current-user consumer is the sole caller. Each method represents one
/// bounded operation. Only an applied native usage may return Confirmed.
pub trait ActionEffects {
    fn media(&mut self, effect: MediaEffect) -> EffectOutcome<()>;
    fn step_keyboard_brightness_until_power_source_change(
        &mut self,
        direction: Direction,
    ) -> EffectOutcome<()>;
    fn display_brightness_usage(&mut self, usage: DisplayBrightnessUsage) -> EffectOutcome<()>;
    fn adjust_wmi_brightness(&mut self, direction: Direction) -> EffectOutcome<u8>;
    fn show_brightness_osd(&mut self, confirmed_level: u8);
}

#[derive(Clone, Copy, PartialEq, Eq)]
enum NativeState {
    Held,
    PressUncertain,
    ReleaseUncertain,
    ReleaseUnconfirmed,
}

struct NativeHold {
    direction: Direction,
    state: NativeState,
}

#[derive(Default)]
struct Executor {
    // There is one native HID usage, plus an independent WMI route per key.
    native: Option<NativeHold>,
    wmi_decrease: bool,
    wmi_increase: bool,
}

impl Executor {
    fn execute(
        &mut self,
        event: Event,
        effects: &mut impl ActionEffects,
    ) -> Result<(), ActionFailure> {
        use Action::*;
        let media = match event.action {
            DisplayBrightnessDown => {
                return self.display(Direction::Decrease, event.phase, effects);
            }
            DisplayBrightnessUp => {
                return self.display(Direction::Increase, event.phase, effects);
            }
            _ if event.phase == Phase::Up => return Ok(()),
            KeyboardBrightnessDown | KeyboardBrightnessUp => {
                let direction = if event.action == KeyboardBrightnessDown {
                    Direction::Decrease
                } else {
                    Direction::Increase
                };
                return effects
                    .step_keyboard_brightness_until_power_source_change(direction)
                    .delivery_result();
            }
            VolumeMute => MediaEffect::VolumeMute,
            VolumeDown => MediaEffect::VolumeDown,
            VolumeUp => MediaEffect::VolumeUp,
            Project => MediaEffect::Project,
            PreviousTrack => MediaEffect::PreviousTrack,
            PlayPause => MediaEffect::PlayPause,
            NextTrack => MediaEffect::NextTrack,
            PrintScreen => MediaEffect::PrintScreen,
        };
        effects.media(media).delivery_result()
    }

    fn wmi_route(&mut self, direction: Direction) -> &mut bool {
        match direction {
            Direction::Decrease => &mut self.wmi_decrease,
            Direction::Increase => &mut self.wmi_increase,
        }
    }

    fn display(
        &mut self,
        direction: Direction,
        phase: Phase,
        effects: &mut impl ActionEffects,
    ) -> Result<(), ActionFailure> {
        let owns_native = self
            .native
            .as_ref()
            .is_some_and(|hold| hold.direction == direction);
        match phase {
            Phase::Down if owns_native || *self.wmi_route(direction) => Ok(()),
            Phase::Down => {
                self.release_native(effects)?;
                self.native = Some(NativeHold {
                    direction,
                    state: NativeState::PressUncertain,
                });
                match effects.display_brightness_usage(DisplayBrightnessUsage::Held(direction)) {
                    EffectOutcome::Confirmed(()) => {
                        self.native.as_mut().unwrap().state = NativeState::Held;
                        Ok(())
                    }
                    EffectOutcome::PossiblySubmitted => Err(ActionFailure::PossiblySubmitted),
                    outcome => {
                        self.native = None;
                        if outcome == EffectOutcome::NotSubmitted {
                            *self.wmi_route(direction) = true;
                            Self::wmi_step(direction, effects)
                        } else {
                            outcome.delivery_result()
                        }
                    }
                }
            }
            Phase::Repeat if *self.wmi_route(direction) => Self::wmi_step(direction, effects),
            Phase::Repeat if owns_native => {
                let hold = self.native.as_mut().unwrap();
                if hold.state != NativeState::Held {
                    return Err(ActionFailure::PossiblySubmitted);
                }
                // This repeats the held service usage to refresh its watchdog.
                hold.state = NativeState::PressUncertain;
                match effects.display_brightness_usage(DisplayBrightnessUsage::Held(direction)) {
                    EffectOutcome::PossiblySubmitted => Err(ActionFailure::PossiblySubmitted),
                    outcome => {
                        hold.state = NativeState::Held;
                        require_applied(outcome)
                    }
                }
            }
            Phase::Up if owns_native => self.release_native(effects),
            Phase::Up => {
                *self.wmi_route(direction) = false;
                Ok(())
            }
            Phase::Repeat => Ok(()),
        }
    }

    fn wmi_step(
        direction: Direction,
        effects: &mut impl ActionEffects,
    ) -> Result<(), ActionFailure> {
        let outcome = effects.adjust_wmi_brightness(direction);
        if let EffectOutcome::Confirmed(level) = outcome {
            effects.show_brightness_osd(level);
        }
        outcome.delivery_result()
    }

    fn release_native(&mut self, effects: &mut impl ActionEffects) -> Result<(), ActionFailure> {
        let Some(hold) = self.native.as_mut() else {
            return Ok(());
        };
        let prior = hold.state;
        match prior {
            NativeState::ReleaseUncertain => return Err(ActionFailure::PossiblySubmitted),
            NativeState::ReleaseUnconfirmed => return Err(ActionFailure::NotSubmitted),
            NativeState::Held | NativeState::PressUncertain => {}
        }
        hold.state = NativeState::ReleaseUncertain;
        match effects.display_brightness_usage(DisplayBrightnessUsage::Released) {
            EffectOutcome::Confirmed(()) => {
                self.native = None;
                Ok(())
            }
            EffectOutcome::NotSubmitted => {
                hold.state = prior;
                Err(ActionFailure::NotSubmitted)
            }
            EffectOutcome::NoOp | EffectOutcome::Unsupported => {
                hold.state = NativeState::ReleaseUnconfirmed;
                Err(ActionFailure::NotSubmitted)
            }
            EffectOutcome::PossiblySubmitted => Err(ActionFailure::PossiblySubmitted),
        }
    }

    fn reset(&mut self, effects: &mut impl ActionEffects) -> Result<(), ActionFailure> {
        self.wmi_decrease = false;
        self.wmi_increase = false;
        // Neutralization is a distinct effect from a possibly submitted press.
        // A possibly submitted release itself is never retried.
        self.release_native(effects)
    }
}

fn require_applied(outcome: EffectOutcome<()>) -> Result<(), ActionFailure> {
    match outcome {
        EffectOutcome::Confirmed(()) => Ok(()),
        EffectOutcome::PossiblySubmitted => Err(ActionFailure::PossiblySubmitted),
        EffectOutcome::NoOp | EffectOutcome::Unsupported | EffectOutcome::NotSubmitted => {
            Err(ActionFailure::NotSubmitted)
        }
    }
}

/// Single current-user consumer. The caller supplies the effects and owns
/// their lifetime. The native Agent role `agent-function-row-v1` runs it with
/// effects that submit nothing.
pub struct ActionWorker {
    actions: HookActions,
    executor: Executor,
}

#[derive(Debug, Eq, PartialEq)]
pub struct WorkerReport {
    pub action_failure: Option<ActionFailure>,
    pub reset: Result<(), ActionFailure>,
}

impl ActionWorker {
    /// Drain until the hook drops its sender, then perform one reset. A
    /// cancellation skips queued ordinary effects while retaining cleanup.
    pub fn run(mut self, effects: &mut impl ActionEffects) -> WorkerReport {
        let shared = std::sync::Arc::clone(&self.actions.1);
        let cancelled = &shared.cancel_actions;
        let mut action_failure = None;
        while self.actions.dispatch_next(|event| {
            if cancelled.load(SeqCst) && event.phase != Phase::Up {
                return Ok(());
            }
            let result = self.executor.execute(event, effects);
            action_failure = result.err();
            result
        }) {}
        let reset = self.executor.reset(effects);
        WorkerReport {
            action_failure,
            reset,
        }
    }
}

impl Drop for ActionWorker {
    fn drop(&mut self) {
        let shared = &self.actions.1;
        if let Some(hold) = &self.executor.native {
            shared.flag(Recovery::ReleaseLost);
            if matches!(
                hold.state,
                NativeState::PressUncertain | NativeState::ReleaseUncertain
            ) {
                shared.flag(Recovery::OutputUncertain);
            }
        }
        shared.worker_pending.store(false, SeqCst);
        shared.release_slot_if_safe();
    }
}

impl FunctionRowHook {
    /// Construct the typed queue and its consumer together so clean handoff
    /// waits for the worker's reset as well as queue and hook retirement.
    pub fn with_executor() -> (Self, HookControl, ActionWorker) {
        let (hook, control, actions) = Self::new();
        hook.shared.worker_pending.store(true, SeqCst);
        let worker = ActionWorker {
            actions,
            executor: Executor::default(),
        };
        (hook, control, worker)
    }
}

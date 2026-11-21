//! The action effects of this stage: none is admitted.
//!
//! Managed `FunctionRowActionExecutor` sends media keys and Win+P through
//! `WindowsSpecialKeyInputSender`, steps keyboard brightness and holds the
//! display-brightness HID usage through Service commands, and falls back to
//! `WmiDisplayBrightnessController` with the Tray OSD. The native Agent has no
//! media sender, and Service commands are not admitted in this stage, so every
//! action is a no-op recorded once per action. The display usage reports
//! `Unsupported`, never `NotSubmitted`, so the executor selects no WMI route.
//!
//! A no-op behind a suppressed key would swallow it. The admission keeps Fn
//! unknown in this stage, so the policy suppresses no fresh press and these
//! effects are unreachable in the installed role.
use openblade_domain::function_row::Action;
use openblade_platform_windows::{
    agent_diagnostics::{Event, FunctionRowNotice},
    function_row_hook::executor::{
        ActionEffects, Direction, DisplayBrightnessUsage, EffectOutcome, MediaEffect,
    },
};

pub(crate) struct StageEffects {
    noted: u16,
    record: fn(Event),
}

impl StageEffects {
    pub(crate) fn new(record: fn(Event)) -> Self {
        Self { noted: 0, record }
    }

    fn not_admitted<T>(&mut self, action: Action) -> EffectOutcome<T> {
        let bit = 1 << action as u16;
        if self.noted & bit == 0 {
            self.noted |= bit;
            (self.record)(Event::FunctionRow(FunctionRowNotice::EffectNotAdmitted(
                action,
            )));
        }
        EffectOutcome::Unsupported
    }
}

impl ActionEffects for StageEffects {
    fn media(&mut self, effect: MediaEffect) -> EffectOutcome<()> {
        self.not_admitted(match effect {
            MediaEffect::VolumeMute => Action::VolumeMute,
            MediaEffect::VolumeDown => Action::VolumeDown,
            MediaEffect::VolumeUp => Action::VolumeUp,
            MediaEffect::Project => Action::Project,
            MediaEffect::PreviousTrack => Action::PreviousTrack,
            MediaEffect::PlayPause => Action::PlayPause,
            MediaEffect::NextTrack => Action::NextTrack,
            MediaEffect::PrintScreen => Action::PrintScreen,
        })
    }

    fn step_keyboard_brightness_until_power_source_change(
        &mut self,
        direction: Direction,
    ) -> EffectOutcome<()> {
        self.not_admitted(match direction {
            Direction::Decrease => Action::KeyboardBrightnessDown,
            Direction::Increase => Action::KeyboardBrightnessUp,
        })
    }

    fn display_brightness_usage(&mut self, usage: DisplayBrightnessUsage) -> EffectOutcome<()> {
        match usage {
            DisplayBrightnessUsage::Held(Direction::Decrease) => {
                self.not_admitted(Action::DisplayBrightnessDown)
            }
            DisplayBrightnessUsage::Held(Direction::Increase) => {
                self.not_admitted(Action::DisplayBrightnessUp)
            }
            // Nothing was ever held, so there is nothing to release.
            DisplayBrightnessUsage::Released => EffectOutcome::NoOp,
        }
    }

    fn adjust_wmi_brightness(&mut self, direction: Direction) -> EffectOutcome<u8> {
        self.not_admitted(match direction {
            Direction::Decrease => Action::DisplayBrightnessDown,
            Direction::Increase => Action::DisplayBrightnessUp,
        })
    }

    fn show_brightness_osd(&mut self, _confirmed_level: u8) {}
}

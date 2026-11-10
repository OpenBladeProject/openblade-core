//! Deterministic function-row policy for the Windows session host.
//!
//! The caller supplies one observation per edge. Native identity, ownership,
//! Fn sampling, the stop deadline, and action execution belong to the host.

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum Device {
    Rz09_0581, // 1532:02E0
    Rz09_0528, // 1532:02C6
    Rz09_0484, // 1532:02A0
}

impl Device {
    fn uses_firmware_driver_mode(self) -> bool {
        matches!(self, Self::Rz09_0581 | Self::Rz09_0528)
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum Backend {
    WindowsHook,
    RazerDriver,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum Status {
    Unknown,
    FirmwareDriverMode {
        active: bool,
    },
    /// This represents the stroke check. Informational Fn status stays with the host.
    HostManagedFn {
        stroke_admitted: bool,
    },
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum PrimaryMode {
    FunctionKeys,
    MultimediaKeys,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum Lifecycle {
    Active,
    Stopping,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct Observation {
    pub exact_device: Option<Device>,
    /// The effective backend after the host's ownership and recovery guard.
    pub selected_backend: Option<Backend>,
    pub status: Status,
    pub fn_pressed: Option<bool>,
    pub primary_mode: Option<PrimaryMode>,
    pub lifecycle: Lifecycle,
}

impl Observation {
    fn firmware_path(self) -> bool {
        self.exact_device
            .is_some_and(Device::uses_firmware_driver_mode)
    }

    fn admits_actions(self, backend: Backend) -> bool {
        if self.lifecycle != Lifecycle::Active
            || self.selected_backend != Some(backend)
            || self.fn_pressed.is_none()
            || self.primary_mode.is_none()
        {
            return false;
        }

        match self.status {
            Status::FirmwareDriverMode { active } => active && self.firmware_path(),
            Status::HostManagedFn { stroke_admitted } => {
                backend == Backend::RazerDriver
                    && self.exact_device == Some(Device::Rz09_0484)
                    && stroke_admitted
            }
            Status::Unknown => false,
        }
    }

    fn admits_media(self, backend: Backend) -> bool {
        self.admits_actions(backend)
            && matches!(
                (self.primary_mode, self.fn_pressed),
                (Some(PrimaryMode::MultimediaKeys), Some(false))
                    | (Some(PrimaryMode::FunctionKeys), Some(true))
            )
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct Stroke {
    pub scan_code: u16,
    pub down: bool,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum Action {
    VolumeMute,
    VolumeDown,
    VolumeUp,
    Project,
    PreviousTrack,
    PlayPause,
    NextTrack,
    DisplayBrightnessDown,
    DisplayBrightnessUp,
    KeyboardBrightnessDown,
    KeyboardBrightnessUp,
    PrintScreen,
}

impl Action {
    fn repeats(self) -> bool {
        matches!(
            self,
            Self::VolumeDown
                | Self::VolumeUp
                | Self::DisplayBrightnessDown
                | Self::DisplayBrightnessUp
                | Self::KeyboardBrightnessDown
                | Self::KeyboardBrightnessUp
        )
    }
}

const ROW: [(u16, Action); 12] = [
    (0x3b, Action::VolumeMute),
    (0x3c, Action::VolumeDown),
    (0x3d, Action::VolumeUp),
    (0x3e, Action::Project),
    (0x3f, Action::PreviousTrack),
    (0x40, Action::PlayPause),
    (0x41, Action::NextTrack),
    (0x42, Action::DisplayBrightnessDown),
    (0x43, Action::DisplayBrightnessUp),
    (0x44, Action::KeyboardBrightnessDown),
    (0x57, Action::KeyboardBrightnessUp),
    (0x58, Action::PrintScreen),
];

fn key_index(stroke: Stroke) -> Option<usize> {
    ROW.iter()
        .position(|&(scan_code, _)| scan_code == stroke.scan_code)
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum Phase {
    Down,
    Repeat,
    Up,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct Event {
    pub scan_code: u16,
    pub action: Action,
    pub phase: Phase,
}

fn event(index: usize, phase: Phase) -> Event {
    let (scan_code, action) = ROW[index];
    Event {
        scan_code,
        action,
        phase,
    }
}

#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
enum Press {
    #[default]
    Idle,
    Passed,
    Suppressed {
        pass_release: bool,
    },
}

impl Press {
    fn take_release(&mut self, index: usize) -> Option<(Event, bool)> {
        match core::mem::take(self) {
            Self::Suppressed { pass_release } => Some((event(index, Phase::Up), !pass_release)),
            _ => None,
        }
    }

    fn handle(
        &mut self,
        index: usize,
        down: bool,
        admits_media: bool,
        admits_repeat: bool,
    ) -> (Option<Event>, bool) {
        if !down {
            return self
                .take_release(index)
                .map_or((None, false), |(up, suppress)| (Some(up), suppress));
        }

        match *self {
            Self::Suppressed { .. } => (
                (admits_repeat && ROW[index].1.repeats()).then(|| event(index, Phase::Repeat)),
                true,
            ),
            Self::Passed => (None, false),
            Self::Idle if admits_media => {
                *self = Self::Suppressed {
                    pass_release: false,
                };
                (Some(event(index, Phase::Down)), true)
            }
            Self::Idle => {
                *self = Self::Passed;
                (None, false)
            }
        }
    }
}

#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct HookDecision {
    pub suppress: bool,
    /// A missed release is closed before the event for the new edge.
    pub stale_release: Option<Event>,
    pub event: Option<Event>,
}

#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct HookRecovery {
    pub reinstall: bool,
    pub release: Option<Event>,
}

#[derive(Clone, Default)]
pub struct WindowsHook {
    keys: [Press; ROW.len()],
}

impl WindowsHook {
    pub fn has_suppressed_keys(&self) -> bool {
        self.keys
            .iter()
            .any(|press| matches!(press, Press::Suppressed { .. }))
    }

    pub fn has_active_keys(&self) -> bool {
        self.keys.iter().any(|press| *press != Press::Idle)
    }

    pub fn handle(
        &mut self,
        stroke: Stroke,
        was_down: bool,
        observation: Observation,
    ) -> HookDecision {
        let Some(index) = key_index(stroke) else {
            return HookDecision::default();
        };
        let press = &mut self.keys[index];
        let stale_release = if stroke.down && !was_down {
            press.take_release(index).map(|(up, _)| up)
        } else {
            None
        };
        // The managed hook repeats a latched action even as Fn or service
        // knowledge changes. Only admission of a new press uses that knowledge.
        let (event, suppress) = press.handle(
            index,
            stroke.down,
            // If the hook starts mid-press, the original down may already be
            // visible to Windows. Keep the first observed repeat and up paired.
            !was_down && observation.admits_media(Backend::WindowsHook),
            observation.lifecycle == Lifecycle::Active,
        );
        HookDecision {
            suppress,
            stale_release,
            event,
        }
    }

    /// The target edge may already have reached the foreground application.
    pub fn recover_missed_edge(
        &mut self,
        stroke: Stroke,
        observed_by_hook: bool,
        observation: Observation,
    ) -> HookRecovery {
        if observed_by_hook
            || !observation.firmware_path()
            || (observation.selected_backend != Some(Backend::WindowsHook)
                && !self.has_active_keys())
        {
            return HookRecovery::default();
        }
        let Some(index) = key_index(stroke) else {
            return HookRecovery::default();
        };
        let press = &mut self.keys[index];
        let release = if stroke.down {
            match press {
                Press::Suppressed { pass_release } => *pass_release = true,
                _ => *press = Press::Passed,
            }
            None
        } else {
            press.take_release(index).map(|(up, _)| up)
        };
        HookRecovery {
            reinstall: true,
            release,
        }
    }
}

/// Scriptable boundary. Forward is called only for intercepted originals that
/// would otherwise never reach Windows. An error may follow submission.
pub trait DriverOutput {
    type Error;

    fn publish(&mut self, event: Event) -> Result<(), Self::Error>;
    fn forward(&mut self, stroke: Stroke) -> Result<(), Self::Error>;
    fn reset_actions(&mut self);
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum DriverProgress {
    Continue,
    /// The host must retire the subscription and call cleanup.
    Retire,
}

#[derive(Debug, Eq, PartialEq)]
pub enum DriverFailure<E> {
    Faulted,
    UnexpectedScan,
    Publish(E),
    Forward(E),
}

pub struct RazerDriver {
    device: Device,
    keys: [Press; ROW.len()],
    draining: bool,
    retired: bool,
    cleaned_up: bool,
    faulted: bool,
    output_uncertain: bool,
}

impl RazerDriver {
    pub fn new(device: Device) -> Self {
        Self {
            device,
            keys: [Press::Idle; ROW.len()],
            draining: false,
            retired: false,
            cleaned_up: false,
            faulted: false,
            output_uncertain: false,
        }
    }

    pub fn has_tracked_keys(&self) -> bool {
        self.keys.iter().any(|press| *press != Press::Idle)
    }

    pub fn output_uncertain(&self) -> bool {
        self.output_uncertain
    }

    pub fn retired_cleanly(&self) -> bool {
        // This is the processor's result. The host must still independently
        // establish that the retired subscription restored its owned hooks.
        self.cleaned_up && !self.faulted && !self.has_tracked_keys() && !self.output_uncertain
    }

    /// Processes an already intercepted stroke. The host can retire an idle
    /// driver before reading again, or call cleanup when the drain expires.
    /// After `Retire`, stop reading that subscription, including queued edges.
    pub fn handle<O: DriverOutput>(
        &mut self,
        stroke: Stroke,
        observation: Observation,
        output: &mut O,
    ) -> Result<DriverProgress, DriverFailure<O::Error>> {
        if self.faulted {
            return Err(DriverFailure::Faulted);
        }

        self.draining |= observation.lifecycle == Lifecycle::Stopping
            || observation.selected_backend == Some(Backend::WindowsHook);
        if self.retired || observation.exact_device != Some(self.device) {
            self.retired = true;
            return Ok(DriverProgress::Retire);
        }

        let Some(index) = key_index(stroke) else {
            self.faulted = true;
            return Err(DriverFailure::UnexpectedScan);
        };
        let (event, suppress) = self.keys[index].handle(
            index,
            stroke.down,
            !self.draining && observation.admits_media(Backend::RazerDriver),
            !self.draining && observation.admits_actions(Backend::RazerDriver),
        );

        if let Some(event) = event
            && let Err(error) = output.publish(event)
        {
            self.faulted = true;
            return Err(DriverFailure::Publish(error));
        }

        if !suppress {
            self.output_uncertain = true;
            if let Err(error) = output.forward(stroke) {
                self.faulted = true;
                return Err(DriverFailure::Forward(error));
            }
            self.output_uncertain = false;
        }
        if self.draining && !self.has_tracked_keys() {
            self.retired = true;
            Ok(DriverProgress::Retire)
        } else {
            Ok(DriverProgress::Continue)
        }
    }

    /// Idempotent retirement cleanup, including after the host's stop deadline.
    /// Releases published media and always resets action state. Tracked physical
    /// keys and uncertain forwarding remain evidence for ownership recovery.
    pub fn cleanup<O: DriverOutput>(&mut self, output: &mut O) -> Result<(), O::Error> {
        self.retired = true;
        if self.cleaned_up {
            return Ok(());
        }
        self.cleaned_up = true;
        let mut first_failure = None;
        for (index, press) in self.keys.iter().enumerate() {
            if matches!(press, Press::Suppressed { .. })
                && let Err(error) = output.publish(event(index, Phase::Up))
            {
                first_failure.get_or_insert(error);
            }
        }
        output.reset_actions();
        first_failure.map_or(Ok(()), Err)
    }
}

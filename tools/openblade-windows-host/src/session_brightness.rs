//! Brightness transport for the private fixture process. No COM or Windows
//! display API is called by this module.

use std::{
    io::{self, Write},
    thread,
    time::Duration,
};

use openblade_platform_windows::{
    brightness::{
        Acknowledgement, CallBudget, MethodRow, NativeFailure, ReadingRow, Submission,
        TopologyFailure, WindowsBrightnessWorker, WmiBrightnessNative,
    },
    display::{
        InternalDisplayTopology,
        scripted::{MONITOR_INSTANCE, Scenario},
    },
};

use crate::session_display::journal::SharedJournal;

#[derive(Clone, Copy)]
pub(super) enum WmiScenario {
    Available,
    Unavailable,
    ReplacedPanel,
    SiblingReadback,
    StalledWrite,
    StalledAdmission,
    StartsAt70,
    StartsAt10,
}

/// The host chooses COM policy before starting its one worker. This executable
/// has only a scripted policy: no process security setup, apartment, or WMI
/// connection. A native owner must establish process COM security before
/// constructing a worker and is outside this executable's admission policy.
pub(super) struct ScriptedComPolicy;

impl ScriptedComPolicy {
    pub(super) fn start(
        display: Scenario,
        scenario: WmiScenario,
        journal: SharedJournal,
    ) -> io::Result<WindowsBrightnessWorker> {
        WindowsBrightnessWorker::start_scripted(move || ScriptedBrightness {
            display,
            scenario,
            replaced: false,
            submitted: false,
            percent: match scenario {
                WmiScenario::StartsAt70 => 70,
                WmiScenario::StartsAt10 => 10,
                _ => 40,
            },
            journal,
        })
    }
}

pub(super) struct ScriptedBrightness {
    display: Scenario,
    scenario: WmiScenario,
    replaced: bool,
    submitted: bool,
    percent: u8,
    journal: SharedJournal,
}

impl ScriptedBrightness {
    pub(super) const POWER_SOURCE: u8 = 0; // PluggedIn in the managed v2 contract.

    fn signal_fixture(phase: &str) {
        let mut stdout = io::stdout().lock();
        let _ = writeln!(stdout, "{phase}").and_then(|()| stdout.flush());
    }

    fn wait_until_cutoff(remaining: impl Fn() -> Result<Duration, NativeFailure>) -> NativeFailure {
        loop {
            match remaining() {
                Ok(time) => thread::sleep(time.min(Duration::from_millis(10))),
                Err(failure) => return failure,
            }
        }
    }
}

impl WmiBrightnessNative for ScriptedBrightness {
    type Method = String;

    fn topology(
        &mut self,
        budget: &CallBudget<'_>,
    ) -> Result<InternalDisplayTopology, TopologyFailure> {
        budget.remaining().map_err(TopologyFailure::Native)?;
        self.display
            .topology(self.replaced)
            .map_err(TopologyFailure::Display)
    }

    fn active_readings(
        &mut self,
        budget: &CallBudget<'_>,
        _: usize,
    ) -> Result<Vec<ReadingRow>, NativeFailure> {
        budget.remaining()?;
        if matches!(self.scenario, WmiScenario::Unavailable) {
            return Err(NativeFailure::MissingData);
        }
        // Even a sibling that reports the requested level cannot confirm
        // the exact submitted InstanceName.
        let suffix = if self.submitted && matches!(self.scenario, WmiScenario::SiblingReadback) {
            1
        } else {
            0
        };
        Ok(vec![ReadingRow {
            instance: Some(format!("{MONITOR_INSTANCE}_{suffix}")),
            percent: Some(self.percent),
            levels: Some(vec![100, 70, 40, 10, 40]),
            data_failure: None,
        }])
    }

    fn active_methods(
        &mut self,
        budget: &CallBudget<'_>,
        _: usize,
    ) -> Result<Vec<MethodRow<Self::Method>>, NativeFailure> {
        budget.remaining()?;
        if matches!(self.scenario, WmiScenario::StalledAdmission) {
            Self::signal_fixture("BRIGHTNESS ADMISSION");
            return Err(Self::wait_until_cutoff(|| budget.remaining()));
        }
        self.replaced |= matches!(self.scenario, WmiScenario::ReplacedPanel);
        let instance = format!("{MONITOR_INSTANCE}_0");
        Ok(vec![MethodRow {
            instance: Some(instance.clone()),
            handle: instance,
        }])
    }

    fn set_brightness(
        &mut self,
        method: Self::Method,
        level: u8,
        submission: &mut Submission<'_>,
    ) -> Result<Acknowledgement, NativeFailure> {
        assert_eq!(method, format!("{MONITOR_INSTANCE}_0"));
        assert!(!self.replaced);
        let InternalDisplayTopology::Single(display) = self
            .display
            .topology(false)
            .map_err(|_| NativeFailure::InvalidData)?
        else {
            return Err(NativeFailure::InvalidData);
        };
        // Claim the worker attempt before accessing the shared request slot.
        // This gate does no fixture I/O. The effect still requires durable intent.
        submission.begin()?;
        if !self
            .journal
            .lock()
            .is_ok_and(|mut journal| journal.begin_brightness(&display, self.percent, level))
        {
            return Err(NativeFailure::InvalidData);
        }
        submission.remaining()?;
        self.submitted = true;
        self.percent = level;
        if matches!(self.scenario, WmiScenario::StalledWrite) {
            Self::signal_fixture("BRIGHTNESS SUBMITTED");
            return Err(Self::wait_until_cutoff(|| submission.remaining()));
        }
        Ok(Acknowledgement::Status(0))
    }
}

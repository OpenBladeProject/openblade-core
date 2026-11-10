use openblade_protocol::{REPORT_LENGTH, RazerReport, ReportPayload};

use crate::performance_runtime::device::{Admission, Channel};

const THERMAL: u8 = 0x0d;
const POWER: u8 = 0x07;
const PERFORMANCE_SELECTOR: u8 = 0x02;
const FULL_ADAPTER: [u8; 2] = [0x11, 0x11];
const FIXED_TARGET: u8 = 40;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(super) enum Topology {
    Automatic = 0,
    Manual = 1,
}

impl Topology {
    pub(super) fn wire(self) -> i32 {
        match self {
            Self::Automatic => 0,
            // The getter reports manual topology. It cannot read the fixed RPM.
            Self::Manual => 3,
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum Command {
    ReadTopology(Channel),
    ReadPower,
    SetTopology(Channel, Topology),
    SetFixedTarget(Channel),
}

impl Command {
    fn report(self, transaction: u8) -> RazerReport {
        let (class, id, payload): (_, _, &[_]) = match self {
            Self::ReadTopology(channel) => (THERMAL, 0x82, &[1, channel.number(), 0, 0]),
            Self::ReadPower => (POWER, 0x8c, &[0, 0]),
            Self::SetTopology(channel, topology) => (
                THERMAL,
                0x02,
                &[1, channel.number(), PERFORMANCE_SELECTOR, topology as u8],
            ),
            Self::SetFixedTarget(channel) => (THERMAL, 0x01, &[1, channel.number(), FIXED_TARGET]),
        };
        RazerReport::create(transaction, class, id, payload).expect("bounded typed fan command")
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(super) enum RecipeResult {
    Unsubmitted,
    Acknowledged,
    Uncertain,
    #[cfg(test)]
    Canceled,
}

#[derive(Clone, Copy, Default, PartialEq, Eq)]
pub(super) enum Fault {
    #[default]
    None,
    LoseFirstModeResponse,
    #[cfg(test)]
    CancelBeforeSubmission,
    #[cfg(test)]
    CancelAfterFirstSubmission,
    #[cfg(test)]
    LoseFullPowerAfterFirstSubmission,
    #[cfg(test)]
    LoseControllerAfterFinalSubmission,
    #[cfg(test)]
    LoseFinalConfirmation,
}

pub(super) struct Device {
    pub(super) admission: Admission,
    pub(super) fault: Fault,
    transport: ScriptedTransport,
    transaction: u8,
    #[cfg(test)]
    pub(super) trace: Vec<String>,
}

impl Device {
    pub(super) fn new() -> Self {
        Self {
            admission: Admission::scripted(),
            fault: Fault::None,
            transport: ScriptedTransport {
                topology: [Topology::Automatic; 2],
                targets: [0; 2],
                power: FULL_ADAPTER,
                readable: true,
            },
            transaction: 0,
            #[cfg(test)]
            trace: Vec::new(),
        }
    }

    pub(super) fn admits_writes(&self) -> bool {
        self.admission.admits_writes()
    }

    pub(super) fn observe(&mut self) -> Option<Topology> {
        self.transaction = 0;
        self.read_pair()
    }

    pub(super) fn restore_scripted_pair(&mut self, topology: Topology) {
        self.transport.topology = [topology; 2];
    }

    pub(super) fn apply(&mut self, baseline: Topology, target: Topology) -> RecipeResult {
        // The managed port reads its own baseline after the scoped service read.
        self.transaction = 0;
        if !self.admits_writes() || self.read_pair() != Some(baseline) {
            return RecipeResult::Unsubmitted;
        }
        let fault = std::mem::take(&mut self.fault);
        #[cfg(test)]
        if fault == Fault::CancelBeforeSubmission {
            return RecipeResult::Canceled;
        }
        let mut submitted = false;
        let changes_topology = baseline != target;
        let result = (|| {
            if changes_topology {
                // On full adapter power both selectors retain Performance.
                for channel in Channel::BOTH {
                    self.submit(
                        Command::SetTopology(channel, target),
                        true,
                        &mut submitted,
                        fault,
                    )?;
                }
            }
            if target == Topology::Manual {
                // The exact port confirms that the mode pair retained Performance
                // before sending either target.
                if changes_topology && self.read_pair() != Some(Topology::Manual) {
                    return None;
                }
                for channel in Channel::BOTH {
                    self.submit(
                        Command::SetFixedTarget(channel),
                        changes_topology,
                        &mut submitted,
                        fault,
                    )?;
                }
            }
            if changes_topology && self.read_pair() != Some(target) {
                return None;
            }
            Some(())
        })();
        match (result, submitted) {
            (Some(()), true) => RecipeResult::Acknowledged,
            (None, true) => RecipeResult::Uncertain,
            _ => RecipeResult::Unsubmitted,
        }
    }

    fn submit(
        &mut self,
        command: Command,
        check_adapter: bool,
        submitted: &mut bool,
        fault: Fault,
    ) -> Option<()> {
        if check_adapter && self.exchange(Command::ReadPower)?.as_slice() != FULL_ADAPTER {
            return None;
        }
        if !self.admits_writes() {
            return None;
        }
        #[cfg(test)]
        if *submitted && fault == Fault::CancelAfterFirstSubmission {
            return None;
        }
        *submitted = true;
        let reply = self.exchange(command)?;
        if fault == Fault::LoseFirstModeResponse
            && matches!(command, Command::SetTopology(Channel::First, _))
        {
            return None;
        }
        #[cfg(test)]
        {
            if fault == Fault::LoseFullPowerAfterFirstSubmission
                && command == Command::SetTopology(Channel::First, Topology::Manual)
            {
                self.transport.power = [0, 0];
            }
            if command == Command::SetFixedTarget(Channel::Second) {
                if fault == Fault::LoseFinalConfirmation {
                    self.transport.readable = false;
                }
                if fault == Fault::LoseControllerAfterFinalSubmission {
                    self.admission.no_competing_controller = false;
                }
            }
        }
        let expected = command.report(0).encode();
        (reply.as_slice() == &expected[8..8 + reply.as_slice().len()]).then_some(())
    }

    fn read_pair(&mut self) -> Option<Topology> {
        if !self.admission.admits_readback() {
            return None;
        }
        let read = |device: &mut Self, channel: Channel| {
            let reply = device.exchange(Command::ReadTopology(channel))?;
            match reply.as_slice() {
                [1, number, PERFORMANCE_SELECTOR, flag] if *number == channel.number() => {
                    match flag {
                        0 => Some(Topology::Automatic),
                        1 => Some(Topology::Manual),
                        _ => None,
                    }
                }
                _ => None,
            }
        };
        let first = read(self, Channel::First)?;
        let second = read(self, Channel::Second)?;
        (first == second).then_some(first)
    }

    fn exchange(&mut self, command: Command) -> Option<ReportPayload> {
        self.transaction = self.transaction.checked_add(1)?;
        let request = command.report(self.transaction);
        let bytes = request.encode();
        #[cfg(test)]
        {
            let payload: String = bytes[8..8 + usize::from(bytes[5])]
                .iter()
                .map(|byte| format!("{byte:02X}"))
                .collect();
            self.trace
                .push(format!("{:02X}{:02X}:{payload}", bytes[6], bytes[7]));
        }
        let reply = self.transport.exchange(&bytes)?;
        request
            .validate_response(&reply, Some(i32::from(bytes[5])))
            .ok()
    }
}

// The only effects are writes into these scripted fields. In particular the
// getter has no access to the stored targets, matching the managed contract.
struct ScriptedTransport {
    topology: [Topology; 2],
    targets: [u8; 2],
    power: [u8; 2],
    readable: bool,
}

impl ScriptedTransport {
    fn exchange(&mut self, request: &[u8; REPORT_LENGTH]) -> Option<[u8; REPORT_LENGTH]> {
        let class = request[6];
        let id = request[7];
        let args = &request[8..8 + usize::from(request[5])];
        let index = |channel: u8| usize::from(channel - 1);
        let response: &[_] = match (class, id, args) {
            (POWER, 0x8c, [0, 0]) => &self.power,
            (THERMAL, 0x82, [1, channel @ 1..=2, 0, 0]) if self.readable => &[
                1,
                *channel,
                PERFORMANCE_SELECTOR,
                self.topology[index(*channel)] as u8,
            ],
            (THERMAL, 0x02, [1, channel @ 1..=2, PERFORMANCE_SELECTOR, flag @ 0..=1]) => {
                self.topology[index(*channel)] = if *flag == 0 {
                    Topology::Automatic
                } else {
                    Topology::Manual
                };
                args
            }
            (THERMAL, 0x01, [1, channel @ 1..=2, FIXED_TARGET])
                if self.topology == [Topology::Manual; 2] =>
            {
                self.targets[index(*channel)] = FIXED_TARGET;
                args
            }
            _ => return None,
        };
        let mut reply = RazerReport::create(request[1], class, id, response)
            .ok()?
            .encode();
        reply[0] = 2;
        Some(reply)
    }
}

#[cfg(test)]
impl Device {
    pub(super) fn targets(&self) -> [u8; 2] {
        self.transport.targets
    }

    pub(super) fn set_readable(&mut self, readable: bool) {
        self.transport.readable = readable;
    }

    pub(super) fn set_full_adapter(&mut self, full: bool) {
        self.transport.power = if full { FULL_ADAPTER } else { [0, 0] };
    }
}

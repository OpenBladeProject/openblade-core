//! Typed RZ09-0581 replay. The only transport is an in-process fake.

use openblade_protocol::{REPORT_LENGTH, RazerReport};

const CLASS: u8 = 0x0d;
const GET: u8 = 0x82;
const SET: u8 = 0x02;
const IDENTITY: (&str, u16, u16) = ("RZ09-0581", 0x1532, 0x02e0);

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(super) enum Preset {
    Balanced = 1,
    Performance = 2,
}

impl TryFrom<i32> for Preset {
    type Error = ();

    fn try_from(value: i32) -> Result<Self, ()> {
        match value {
            1 => Ok(Self::Balanced),
            2 => Ok(Self::Performance),
            _ => Err(()),
        }
    }
}

impl Preset {
    fn selector(self) -> u8 {
        match self {
            Self::Balanced => 0,
            Self::Performance => 2,
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) enum Channel {
    First,
    Second,
}

impl Channel {
    pub(crate) const BOTH: [Self; 2] = [Self::First, Self::Second];

    pub(crate) fn number(self) -> u8 {
        match self {
            Self::First => 1,
            Self::Second => 2,
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(super) enum Command {
    Read(Channel),
    Set(Channel, Preset),
}

impl Command {
    pub(super) fn spec(self) -> (u8, u8, [u8; 4]) {
        let (id, channel, selector) = match self {
            Self::Read(channel) => (GET, channel, 0),
            Self::Set(channel, preset) => (SET, channel, preset.selector()),
        };
        (CLASS, id, [1, channel.number(), selector, 0])
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(super) enum RecipeResult {
    Blocked,
    BaselineUnavailable,
    Unchanged,
    MatchedAtPort,
    Canceled,
    KnownNotApplied,
    Acknowledged(Preset),
    Indeterminate(Preset),
}

#[derive(Clone, Copy, PartialEq, Eq)]
pub(super) enum Fault {
    None,
    CancelBeforeFirst,
    LoseSecondResponse,
    #[cfg(test)]
    WrongFirstEcho,
    #[cfg(test)]
    TargetDuringPortBaseline,
    #[cfg(test)]
    DenySecond,
    #[cfg(test)]
    LoseConfirmation,
}

#[derive(Clone, Copy)]
pub(crate) struct Admission {
    pub(crate) identity: (&'static str, u16, u16),
    pub(crate) full_adapter: bool,
    pub(crate) active_session: bool,
    pub(crate) no_competing_controller: bool,
    pub(crate) owns_transport: bool,
    pub(crate) awake: bool,
}

impl Admission {
    pub(crate) fn scripted() -> Self {
        Self {
            identity: IDENTITY,
            full_adapter: true,
            active_session: true,
            no_competing_controller: true,
            owns_transport: true,
            awake: true,
        }
    }

    pub(crate) fn admits_intent(&self) -> bool {
        self.full_adapter && self.active_session
    }

    pub(crate) fn admits_writes(&self) -> bool {
        self.admits_intent() && self.admits_readback() && self.no_competing_controller
    }

    pub(crate) fn admits_readback(&self) -> bool {
        self.identity == IDENTITY && self.owns_transport && self.awake
    }
}

pub(super) struct Device {
    pub(super) admission: Admission,
    transport: FakeTransport,
    transaction: u8,
    fault: Fault,
    #[cfg(test)]
    pub(super) commands: Vec<Command>,
}

impl Device {
    pub(super) fn new() -> Self {
        Self {
            admission: Admission::scripted(),
            transport: FakeTransport {
                channels: [Preset::Balanced; 2],
                unreadable_queries: 0,
            },
            transaction: 0,
            fault: Fault::None,
            #[cfg(test)]
            commands: Vec::new(),
        }
    }

    pub(super) fn arm(&mut self, fault: Fault) {
        self.fault = fault;
    }

    pub(super) fn admits_intent(&self) -> bool {
        self.admission.admits_intent()
    }

    pub(super) fn admits_writes(&self) -> bool {
        self.admission.admits_writes()
    }

    fn admits_readback(&self) -> bool {
        self.admission.admits_readback()
    }

    pub(super) fn apply(&mut self, preset: Preset) -> RecipeResult {
        if !self.admits_writes() {
            return RecipeResult::Blocked;
        }

        // Each scripted operation gets a fresh, synchronous correlation scope.
        // There is no queued reply from an earlier operation.
        self.transaction = 0;

        // The scoped coordinator compares its fresh baseline first. The
        // production port then independently checks its own thermal baseline.

        let baseline = match self.read_pair() {
            Some(current) if current == preset => return RecipeResult::Unchanged,
            Some(current) => current,
            None => return RecipeResult::BaselineUnavailable,
        };

        #[cfg(test)]
        if self.fault == Fault::TargetDuringPortBaseline {
            self.fault = Fault::None;
            self.replace_observed(preset);
        }
        match self.read_pair() {
            Some(current) if current == preset => return RecipeResult::MatchedAtPort,
            None => return RecipeResult::BaselineUnavailable,
            _ => {}
        }

        let fault = std::mem::replace(&mut self.fault, Fault::None);
        if fault == Fault::CancelBeforeFirst {
            return RecipeResult::Canceled;
        }

        for channel in Channel::BOTH {
            #[cfg(test)]
            if fault == Fault::DenySecond && channel == Channel::Second {
                self.admission.owns_transport = false;
            }
            if !self.admits_writes() {
                return match channel {
                    Channel::First => RecipeResult::KnownNotApplied,
                    Channel::Second => RecipeResult::Indeterminate(baseline),
                };
            }

            let command = Command::Set(channel, preset);
            if self.exchange(command, fault) != Some(command.spec().2) {
                return RecipeResult::Indeterminate(baseline);
            }
        }
        RecipeResult::Acknowledged(baseline)
    }

    pub(super) fn observe(&mut self) -> Option<Preset> {
        if !self.admits_readback() {
            return None;
        }
        self.transaction = 0;
        self.read_pair()
    }

    fn read_pair(&mut self) -> Option<Preset> {
        let [first, second] = Channel::BOTH;
        let [1, 1, left, 0] = self.exchange(Command::Read(first), Fault::None)? else {
            return None;
        };
        let [1, 2, right, 0] = self.exchange(Command::Read(second), Fault::None)? else {
            return None;
        };
        match (left, right) {
            (0, 0) => Some(Preset::Balanced),
            (2, 2) => Some(Preset::Performance),
            _ => None,
        }
    }

    fn exchange(&mut self, command: Command, fault: Fault) -> Option<[u8; 4]> {
        #[cfg(test)]
        self.commands.push(command);
        let (class, id, payload) = command.spec();
        self.transaction = self.transaction.checked_add(1)?;
        let report = RazerReport::create(self.transaction, class, id, &payload).ok()?;
        let reply = self.transport.exchange(&report.encode(), fault)?;
        report
            .validate_response(&reply, Some(4))
            .ok()?
            .as_slice()
            .try_into()
            .ok()
    }

    #[cfg(test)]
    pub(super) fn replace_observed(&mut self, preset: Preset) {
        self.transport.channels = [preset; 2];
    }
}

// Models two device channels and the reply boundary. A submitted command may
// change its channel even if its echo is missing or invalid.
struct FakeTransport {
    channels: [Preset; 2],
    unreadable_queries: u8,
}

impl FakeTransport {
    fn exchange(
        &mut self,
        request: &[u8; REPORT_LENGTH],
        fault: Fault,
    ) -> Option<[u8; REPORT_LENGTH]> {
        let (class, id) = (request[6], request[7]);
        let [1, channel @ 1..=2, selector, 0] = request[8..12] else {
            return None;
        };
        let index = usize::from(channel - 1);
        let observed = match (class, id, selector) {
            (CLASS, GET, 0) => {
                if self.unreadable_queries != 0 {
                    self.unreadable_queries -= 1;
                    return None;
                }
                self.channels[index]
            }
            (CLASS, SET, 0 | 2) => {
                let preset = if selector == 0 {
                    Preset::Balanced
                } else {
                    Preset::Performance
                };
                self.channels[index] = preset;
                if channel == 2 && fault == Fault::LoseSecondResponse {
                    self.unreadable_queries = 1;
                    return None;
                }
                #[cfg(test)]
                {
                    if channel == 1 && fault == Fault::WrongFirstEcho {
                        return Self::reply(request, [1, 2, selector, 0]);
                    }
                    if channel == 2 && fault == Fault::LoseConfirmation {
                        self.unreadable_queries = 2;
                    }
                }
                preset
            }
            _ => return None,
        };
        Self::reply(request, [1, channel, observed.selector(), 0])
    }

    fn reply(request: &[u8; REPORT_LENGTH], payload: [u8; 4]) -> Option<[u8; REPORT_LENGTH]> {
        let mut reply = RazerReport::create(request[1], request[6], request[7], &payload)
            .ok()?
            .encode();
        reply[0] = 2;
        Some(reply)
    }
}

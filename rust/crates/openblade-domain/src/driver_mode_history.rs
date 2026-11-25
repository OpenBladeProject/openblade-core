//! The native Service's durable keyboard Driver-mode ownership record
//! (`owner\keyboard-driver-mode.bin`): its strict codec and the transitions
//! a writer may make. The host owns the file, custody and every admission.
//!
//! The record says what the last Driver-mode job that claimed ownership may
//! have reached in firmware, so a later process, or Setup, can tell a known
//! state from one in doubt. It is canonical ASCII, one field per line, each
//! ending in `\n`:
//!
//! ```text
//! OpenBlade.NativeDriverMode.v1
//! service-keyboard-driver-mode-v1/rz09-0581-v1
//! <sequence>  decimal, no leading zero: 1 for the first record, +1 per replace
//! <owner>     64 lowercase hex: the writing generation's Active owner nonce
//! <state>     AcquireIntent | Owned | ReleaseIntent | Clear
//! <prior>     Owned | Clear: what an intent reverts to; a settled state's own
//! <ids>       b,s,r,x: the job's baseline, setter, readback and spare IDs
//! ```
//!
//! The four IDs are distinct and in 1..=254. A settle or a read-only
//! resolution keeps the IDs of the intent it settles, whose setter may have
//! been sent. A revert, whose job is known to have sent no setter, takes the
//! IDs of the record its claim replaced (`Record::revert`). Either way the
//! newest 00/04 reply belongs to the IDs the record names, except after an
//! intent whose setter was never sent and that was not reverted: a claim
//! whose write failed after it landed, which closes the writer, a revert
//! that a release's share had no room for or its writer's admission refused,
//! or a process that ended between the claim and its setter or revert. That
//! intent, and what later settles it, names IDs that sent no 00/04, and the
//! newest 00/04 reply is the earlier record's. Anything else, another
//! header, contract or state name included, is refused: a later version
//! needs a new header, which this reader refuses.
use std::prelude::rust_2024::*;
use std::{format, io};

/// The first line: the format and its version.
pub const HEADER: &str = "OpenBlade.NativeDriverMode.v1";
/// The second line: the feature and model the record belongs to.
pub const CONTRACT: &str = "service-keyboard-driver-mode-v1/rz09-0581-v1";
/// The most bytes a reader takes; a valid record is far smaller.
pub const MAX_BYTES: usize = 2048;
/// The transaction IDs one Driver-mode job reserves before its first send.
pub const IDS: usize = 4;

/// Where ownership stands. An intent is written before its job may submit
/// a setter, and is in doubt until a settle, revert or read resolves it.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum State {
    /// An acquire job claimed ownership and may have submitted 00/04:0300.
    AcquireIntent,
    /// A confirmed acquire, or a read of Driver resolving one in doubt.
    Owned,
    /// A release job may have submitted 00/04:0000.
    ReleaseIntent,
    /// Nothing is owned and nothing is in doubt.
    Clear,
}

/// A state with nothing in doubt: what an intent reverts to when its job is
/// known not to have submitted the setter.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Settled {
    Owned,
    Clear,
}

/// Which job claims ownership.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Intent {
    Acquire,
    Release,
}

impl State {
    fn name(self) -> &'static str {
        match self {
            Self::AcquireIntent => "AcquireIntent",
            Self::Owned => "Owned",
            Self::ReleaseIntent => "ReleaseIntent",
            Self::Clear => "Clear",
        }
    }

    fn parse(name: &str) -> Option<Self> {
        [
            Self::AcquireIntent,
            Self::Owned,
            Self::ReleaseIntent,
            Self::Clear,
        ]
        .into_iter()
        .find(|state| state.name() == name)
    }

    /// The settled state this is, `None` for an intent.
    pub fn settled(self) -> Option<Settled> {
        match self {
            Self::Owned => Some(Settled::Owned),
            Self::Clear => Some(Settled::Clear),
            Self::AcquireIntent | Self::ReleaseIntent => None,
        }
    }
}

impl From<Settled> for State {
    fn from(settled: Settled) -> Self {
        match settled {
            Settled::Owned => Self::Owned,
            Settled::Clear => Self::Clear,
        }
    }
}

impl From<Intent> for State {
    fn from(intent: Intent) -> Self {
        match intent {
            Intent::Acquire => Self::AcquireIntent,
            Intent::Release => Self::ReleaseIntent,
        }
    }
}

fn deny() -> io::Error {
    io::Error::new(
        io::ErrorKind::PermissionDenied,
        "The keyboard Driver-mode record is invalid or unsupported.",
    )
}

/// One valid record. Only `parse`, `claim`, `settle` and `revert` make one, so every
/// value is canonical and its `bytes` are exactly what a reader admitted.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Record {
    sequence: u64,
    owner: String,
    state: State,
    prior: Settled,
    ids: [u8; IDS],
}

impl Record {
    /// The record `bytes` encode, refusing any byte outside the grammar.
    pub fn parse(bytes: &[u8]) -> io::Result<Self> {
        if bytes.is_empty()
            || bytes.len() > MAX_BYTES
            || bytes
                .iter()
                .any(|b| *b != b'\n' && !(0x20..=0x7e).contains(b))
        {
            return Err(deny());
        }
        let text = std::str::from_utf8(bytes).map_err(|_| deny())?;
        let fields: Vec<&str> = text.split('\n').collect();
        let [header, contract, sequence, owner, state, prior, ids, end] = fields[..] else {
            return Err(deny());
        };
        if header != HEADER || contract != CONTRACT || !end.is_empty() {
            return Err(deny());
        }
        let ids: Vec<u8> = ids
            .split(',')
            .map(|id| id.parse().map_err(|_| deny()))
            .collect::<io::Result<_>>()?;
        let record = Self {
            sequence: sequence.parse().map_err(|_| deny())?,
            owner: owner.into(),
            state: State::parse(state).ok_or_else(deny)?,
            prior: State::parse(prior)
                .and_then(State::settled)
                .ok_or_else(deny)?,
            ids: ids.try_into().map_err(|_| deny())?,
        };
        record.validate()?;
        // Only the canonical spelling is admitted: no sign, leading zero or
        // other number a parser would also accept.
        if record.bytes() != bytes {
            return Err(deny());
        }
        Ok(record)
    }

    /// The canonical encoding.
    pub fn bytes(&self) -> Vec<u8> {
        let [b, s, r, x] = self.ids;
        format!(
            "{HEADER}\n{CONTRACT}\n{}\n{}\n{}\n{}\n{b},{s},{r},{x}\n",
            self.sequence,
            self.owner,
            self.state.name(),
            State::from(self.prior).name(),
        )
        .into_bytes()
    }

    /// The field rules `parse` applies after the grammar: a sequence from 1,
    /// a 64-hex owner, a settled state that is its own prior, and four
    /// distinct IDs a pool can issue.
    pub fn validate(&self) -> io::Result<()> {
        let mut seen = [false; 256];
        let ids_valid = self.ids.iter().all(|id| {
            (1..=254).contains(id) && !std::mem::replace(&mut seen[usize::from(*id)], true)
        });
        if self.sequence == 0
            || self.owner.len() != 64
            || !self
                .owner
                .bytes()
                .all(|b| b.is_ascii_digit() || (b'a'..=b'f').contains(&b))
            || self.state.settled().is_some_and(|own| own != self.prior)
            || !ids_valid
        {
            return Err(deny());
        }
        Ok(())
    }

    /// The record a job writes before it may submit its setter, after
    /// `prior` (`None` while there is no record yet). Refused over an intent.
    pub fn claim(
        prior: Option<&Self>,
        intent: Intent,
        owner: &str,
        ids: [u8; IDS],
    ) -> io::Result<Self> {
        let settled = match prior {
            None => Settled::Clear,
            Some(prior) => prior.state.settled().ok_or_else(deny)?,
        };
        let next = Self {
            sequence: next_sequence(prior).ok_or_else(deny)?,
            owner: owner.into(),
            state: intent.into(),
            prior: settled,
            ids,
        };
        next.admitted_after(prior)
    }

    /// The settled record that follows this one, with its IDs: an intent's
    /// settle or resolution, or an owned record found Normal. A revert, which
    /// takes the IDs of the record the claim replaced, is `revert`.
    pub fn settle(&self, to: Settled, owner: &str) -> io::Result<Self> {
        let next = Self {
            sequence: next_sequence(Some(self)).ok_or_else(deny)?,
            owner: owner.into(),
            state: to.into(),
            prior: to,
            ids: self.ids,
        };
        next.admitted_after(Some(self))
    }

    /// The revert of this intent, its setter known not to have been sent:
    /// its prior, on the IDs of `replaced`, the record its claim replaced
    /// (`None`: there was none, and the intent keeps its own), so the newest
    /// 00/04 reply still belongs to the IDs the record names. Refused unless
    /// `replaced` is the record this intent was claimed over: its
    /// predecessor in sequence, in the intent's prior state.
    pub fn revert(&self, replaced: Option<&Self>, owner: &str) -> io::Result<Self> {
        if self.state.settled().is_some() {
            return Err(deny());
        }
        let ids = match replaced {
            None if self.sequence == 1 => self.ids,
            Some(replaced)
                if next_sequence(Some(replaced)) == Some(self.sequence)
                    && replaced.state.settled() == Some(self.prior) =>
            {
                replaced.ids
            }
            _ => return Err(deny()),
        };
        let next = Self {
            sequence: next_sequence(Some(self)).ok_or_else(deny)?,
            owner: owner.into(),
            state: self.prior.into(),
            prior: self.prior,
            ids,
        };
        next.admitted_after(Some(self))
    }

    fn admitted_after(self, prior: Option<&Self>) -> io::Result<Self> {
        self.validate()?;
        if follows(prior, &self) {
            Ok(self)
        } else {
            Err(deny())
        }
    }

    pub fn sequence(&self) -> u64 {
        self.sequence
    }

    /// The writing generation's Active owner nonce.
    pub fn owner(&self) -> &str {
        &self.owner
    }

    pub fn state(&self) -> State {
        self.state
    }

    /// What an intent reverts to when known not submitted; a settled
    /// record's own state.
    pub fn prior(&self) -> Settled {
        self.prior
    }

    /// The baseline, setter, readback and spare IDs of the job that wrote
    /// the last intent.
    pub fn ids(&self) -> [u8; IDS] {
        self.ids
    }
}

fn next_sequence(prior: Option<&Record>) -> Option<u64> {
    prior.map_or(Some(1), |prior| prior.sequence.checked_add(1))
}

/// Whether `next` may replace `prior` (`None`: no record yet). Each write
/// adds one to the sequence. The writer's owner is not compared: a later
/// generation resolves an earlier one's intent. The edges:
///
/// - a claim, from no record, `Clear` or `Owned`, into either intent whose
///   prior is that settled state (`Clear` for no record), on new IDs;
/// - out of `AcquireIntent`, into `Owned` (confirmed, or read Driver) or
///   `Clear` (read Normal), or back to its prior (known not submitted);
/// - out of `ReleaseIntent`, into `Clear` (confirmed, or read Normal), or
///   back to its prior (known not submitted, or read Driver over an `Owned`
///   prior);
/// - `Owned` into `Clear`, a release that read Normal.
///
/// Every edge out of a record keeps its IDs unless it enters an intent or
/// returns to the intent's prior. A revert takes the IDs of the record the
/// claim replaced (`Record::revert`), which this table cannot see, so an
/// edge into the prior admits any IDs. Those IDs are the writer's to get
/// right, not a store's: the host's record store admits a replace by this
/// table alone, and only its ledger builds a revert from the replaced record
/// and keeps a settle or a resolution into that state on the intent's IDs.
/// An intent never follows an intent, and no settled state repeats itself.
pub fn follows(prior: Option<&Record>, next: &Record) -> bool {
    if next.validate().is_err() || next_sequence(prior) != Some(next.sequence) {
        return false;
    }
    let Some(prior) = prior else {
        return next.state.settled().is_none() && next.prior == Settled::Clear;
    };
    let kept = next.ids == prior.ids;
    let reverted = next.state == State::from(prior.prior);
    match (prior.state, next.state) {
        (State::Clear | State::Owned, State::AcquireIntent | State::ReleaseIntent) => {
            prior.state.settled() == Some(next.prior)
        }
        (State::AcquireIntent, State::Owned | State::Clear)
        | (State::ReleaseIntent, State::Clear) => kept || reverted,
        (State::Owned, State::Clear) => kept,
        (State::ReleaseIntent, State::Owned) => reverted,
        _ => false,
    }
}

#[cfg(test)]
#[path = "driver_mode_history_tests.rs"]
mod tests;

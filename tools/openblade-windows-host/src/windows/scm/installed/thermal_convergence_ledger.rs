//! The user's thermal convergence ledger, `thermal-convergence.json`, which
//! native shares with managed (`ThermalConvergenceStateStore`): the same
//! per-user directory, schema and members (`openblade_domain::thermal_convergence`).
//!
//! The store reads the ledger once, when it opens, and holds the state and
//! the exact bytes it was read from. It fails closed: a file that is
//! unreadable, unparsable, of another user, invalid or written by a newer
//! schema closes convergence for this user, and the store never writes over
//! it. So does any write whose result it cannot confirm, since a
//! reservation that may be durable must never be mistaken for one that is
//! not. A closed ledger stays closed until the store is opened again: at a
//! user switch or a Service restart, as managed reloads it only at
//! activation.
//!
//! Writes are compare-and-swap on the held bytes through the store's own
//! atomic `Files::replace`, so a ledger changed outside the Service is never
//! overwritten.
use super::{Files, Storage, denied};
use openblade_domain::thermal_convergence::{self as ledger, LEDGER, Load};
use std::io;

/// What the store holds of the ledger.
#[derive(Clone, Debug)]
pub(super) enum Ledger {
    /// The admitted state and the bytes it was read from or last written
    /// as; `None` while the file does not exist.
    Authoritative {
        state: Box<ledger::State>,
        bytes: Option<Vec<u8>>,
    },
    /// Not authoritative: managed's `thermalConvergenceLedgerAuthoritative`
    /// is false.
    Closed,
}

impl Ledger {
    /// The ledger at open: a missing file is the empty state.
    pub(super) fn load(files: &dyn Files, sid: &str) -> Self {
        let Ok(bytes) = files.read(LEDGER) else {
            return Self::Closed;
        };
        match ledger::decode(bytes.as_deref(), sid) {
            Load::Admitted { state, .. } => Self::Authoritative { state, bytes },
            Load::Quarantined(_) => Self::Closed,
        }
    }
}

impl Storage {
    /// The ledger state, or `None` while it is not authoritative.
    pub(in super::super) fn convergence_ledger(&self) -> Option<ledger::State> {
        match &self.state.lock().ok()?.ledger {
            Ledger::Authoritative { state, .. } => Some((**state).clone()),
            Ledger::Closed => None,
        }
    }

    /// Replaces the ledger with `next` only while it still holds `expected`,
    /// in memory and on disk. Any refusal or failure after the encode closes
    /// the ledger: the file may or may not hold `next`, so neither may be
    /// relied on. A `next` managed would not persist is refused before
    /// anything is written, and closes nothing.
    pub(in super::super) fn replace_convergence_ledger(
        &self,
        expected: &ledger::State,
        next: &ledger::State,
    ) -> io::Result<()> {
        let mut s = self.state.lock().map_err(|_| denied())?;
        let Ledger::Authoritative { state, bytes } = &s.ledger else {
            return Err(denied());
        };
        if **state != *expected {
            return Err(denied());
        }
        let (encoded, _) = ledger::encode(next, &self.user_sid)
            .map_err(|invalid| io::Error::new(io::ErrorKind::InvalidData, invalid.0))?;
        // A stage left by an earlier failed write never took effect: the
        // replace below renames only a stage it wrote and checked itself.
        let _ = self.files.discard_stage(LEDGER);
        let written = self.files.replace(LEDGER, bytes.as_deref(), &encoded);
        s.ledger = match written {
            Ok(()) => Ledger::Authoritative {
                state: Box::new(next.clone()),
                bytes: Some(encoded),
            },
            Err(_) => Ledger::Closed,
        };
        written
    }

    /// Whether the file still holds exactly the bytes the store last read or
    /// wrote (managed `RevalidateThermalConvergenceReservationAsync`). A
    /// change, or a read that fails, closes the ledger.
    pub(in super::super) fn convergence_ledger_holds(&self) -> bool {
        let Ok(mut s) = self.state.lock() else {
            return false;
        };
        let Ledger::Authoritative { bytes, .. } = &s.ledger else {
            return false;
        };
        let holds = self
            .files
            .read(LEDGER)
            .is_ok_and(|current| current == *bytes);
        if !holds {
            s.ledger = Ledger::Closed;
        }
        holds
    }
}

#[cfg(test)]
#[path = "thermal_convergence_ledger_tests.rs"]
mod tests;

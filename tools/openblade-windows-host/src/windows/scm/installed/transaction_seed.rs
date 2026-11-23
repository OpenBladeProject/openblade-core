//! Transaction IDs whose replies an earlier Service process may have left in
//! the mailbox, recovered from the thermal journal before the first job.
//!
//! The firmware reply mailbox outlives the handle and the process
//! (`performance::transactions`), but the pool's quarantine lives in memory,
//! so a new process could issue an ID that bears a reply left there. A
//! request whose SET did not take effect would then read that reply, which
//! only the ID, class and command tell apart. Reading a reply does not clear
//! it: within a process the pool frees an answered ID only once a later
//! request with the same class and command is answered. A new process has
//! answered nothing, so the newest answered ID of each command an earlier
//! process sent is as exposed as one it sent without a reply.
//!
//! The journal is the only record that survives the process. It names fan,
//! battery and temporary-charge IDs; performance rows and read-only Readers
//! name none. These are the IDs it names that may bear such a reply:
//!
//! - every ID that each family's newest request may have sent, whatever its
//!   progress: its replies are the newest for each command it sent;
//! - the reservations of the owners still live at the end of the journal
//!   (`thermal_history::Owners`): the cleanup IDs of the fan record that
//!   owns manual fans, and the cancellation IDs of the temporary-charge
//!   enable that owns the charge. An attempt that sends them can fail before
//!   it journals a row;
//! - those an earlier known-not-submitted (6) request may have sent after
//!   its Prepared row: no setter was submitted, but a getter may have been,
//!   without a reply;
//! - every `superseded_ids` set, which a cancel names because an earlier
//!   attempt sent it without a correlated reply.
//!
//! The first two are the live set; the other two are history, newest first,
//! as far as `LIMIT` allows. An earlier request's reply also stays the
//! newest for a command no later request of its family sent, such as a
//! setter the newest request never reached; the seed does not name those.
use std::{io, sync::Mutex};

use super::thermal_mutation::{is_fatal_admission, remember_failure};
use crate::thermal_history::{Entry, rows_with_owners};
use openblade_domain::{battery_history, temporary_charge_history, thermal_mutation::Progress};

/// The most IDs one fan record names: two preflight pairs, at most six chain
/// pairs (four setters, the independent read, and an entry's intermediate
/// pair or a return's Custom levels, never both, since an entry is manual and
/// Custom levels are automatic) and five cleanup pairs, which only a manual
/// record reserves (`FanRecord::validate`).
const FAN: usize = 2 * (2 + 6 + 5);
/// The cleanup IDs a manual fan record reserves.
const FAN_CLEANUP: usize = 2 * 5;
/// A temporary-charge request may send its IDs and, as a fresh-ID cancel,
/// the superseded IDs it replaces.
const TEMPORARY: usize = 2 * temporary_charge_history::EXCHANGES;
/// The largest live set: each family's newest request, and the reservations
/// of a fan owner and a temporary-charge owner older than their family's
/// newest request.
const LIVE: usize = FAN
    + battery_history::EXCHANGES
    + TEMPORARY
    + FAN_CLEANUP
    + temporary_charge_history::EXCHANGES;

/// The most IDs reserved at once: a live fan owner's cleanup and a
/// temporary-charge owner's cancellation, the next request (a fan record is
/// the largest) and a read pair.
const DEMAND: usize = FAN_CLEANUP + temporary_charge_history::EXCHANGES + FAN + 2;

/// The most IDs one start quarantines, so the pool never starves. It holds
/// the largest live set (74), so the live set is always whole, and the rest
/// holds the newest history. 80 leaves 174 of the 254 IDs: more than three
/// times the most reserved at once (46), and about 120 failed reads can
/// still quarantine one ID each.
pub(super) const LIMIT: usize = 80;
const _: () = assert!(LIMIT >= LIVE && 254 - LIMIT >= 3 * DEMAND);

/// Quarantines IDs in one transaction pool (`Transactions::quarantine`).
pub(super) type Quarantine<'a> = dyn Fn(&[u8]) + 'a;

/// Quarantines what `unanswered` finds in the journal `read` returns. A read
/// that fails is retried once, unless it failed fatal admission, which
/// `fatal` keeps as the source's own reads keep it. A journal that still
/// cannot be read or validated quarantines nothing, as before this seed
/// existed, and an absent one has nothing to find, so neither blocks startup.
pub(super) fn quarantine_unanswered(
    read: &dyn Fn() -> io::Result<Option<Vec<u8>>>,
    fatal: &Mutex<Option<&'static str>>,
    quarantine: &Quarantine<'_>,
) {
    let journal = read().or_else(|error| {
        if is_fatal_admission(&error) {
            Err(error)
        } else {
            read()
        }
    });
    match journal {
        Ok(Some(journal)) => {
            if let Ok(ids) = unanswered(&journal) {
                quarantine(&ids);
            }
        }
        Ok(None) => {}
        Err(error) => remember_failure(fatal, &error),
    }
}

/// The IDs `journal` names that may bear a reply left in the mailbox (see
/// the module notes): the live set first, then the history newest first,
/// without repeats and at most `LIMIT`.
fn unanswered(journal: &[u8]) -> io::Result<Vec<u8>> {
    // Every ID the newest fan, battery and temporary-charge request may have sent.
    let mut newest: [Vec<u8>; 3] = Default::default();
    let mut history = Vec::new();
    let (_, _, owners) = rows_with_owners(journal, |entry, _| {
        if let Some((family, ids)) = sent(entry) {
            newest[family] = ids;
        }
        if entry.progress == Progress::KnownNotSubmitted as u8 {
            history.push(after_prepared(entry));
        } else if let Some(superseded) = entry.tfc.as_ref().and_then(|t| t.superseded_ids) {
            history.push(superseded.to_vec());
        }
        Ok(())
    })?;
    let fan_owner = owners
        .fan
        .and_then(|owner| owner.fan)
        .map(|fan| fan.cleanup);
    let temporary_owner = owners
        .temporary
        .and_then(|owner| owner.tfc)
        .and_then(|t| t.cancel_ids);
    let live = newest
        .into_iter()
        .flatten()
        .chain(fan_owner.into_iter().flatten().flatten())
        .chain(temporary_owner.into_iter().flatten());
    let mut seen = [false; 256];
    Ok(live
        .chain(history.into_iter().rev().flatten())
        .filter(|id| !std::mem::replace(&mut seen[usize::from(*id)], true))
        .take(LIMIT)
        .collect())
}

/// The family of `entry`'s record (fan 0, battery 1, temporary charge 2) and
/// every ID its request may have sent: a fan record's preflight, chain and
/// cleanup IDs, a battery record's IDs, and a temporary-charge record's IDs
/// and superseded IDs. An enable's cancellation IDs are a later cancel's to
/// send, and a performance row names none.
fn sent(entry: &Entry) -> Option<(usize, Vec<u8>)> {
    if let Some(fan) = &entry.fan {
        let pairs = fan.preflight.iter().chain(&fan.ids).chain(&fan.cleanup);
        Some((0, pairs.flatten().copied().collect()))
    } else if let Some(battery) = &entry.battery {
        Some((1, battery.ids.to_vec()))
    } else {
        entry.tfc.as_ref().map(|t| {
            let superseded = t.superseded_ids.iter().flatten();
            (2, t.ids.iter().chain(superseded).copied().collect())
        })
    }
}

/// The IDs `entry`'s request may send after its Prepared row: all of `sent`
/// but a fan record's preflight, whose replies were read before that row, and
/// its cleanup, which only the owner's Stop sends, in rows of its own.
fn after_prepared(entry: &Entry) -> Vec<u8> {
    match &entry.fan {
        Some(fan) => fan.ids.iter().flatten().copied().collect(),
        None => sent(entry).map(|(_, ids)| ids).unwrap_or_default(),
    }
}

#[cfg(test)]
#[path = "transaction_seed_tests.rs"]
mod tests;

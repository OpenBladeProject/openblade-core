//! Transaction IDs whose replies an earlier Service process may have left in
//! the mailbox, recovered from the thermal journal and the keyboard
//! Driver-mode record before the first job.
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
//! Two records survive the process. The keyboard Driver-mode record
//! (`openblade_domain::driver_mode_history`) names the four IDs of the newest
//! Driver-mode job that may have sent 00/04: only a claiming job sends it,
//! and it records its IDs before it sends the setter. A settle or a
//! read-only resolution keeps them, and a revert, whose job sent no setter,
//! returns to the IDs of the record its claim replaced, so the newest 00/04
//! reply belongs to the IDs the record names. It does not after an intent
//! whose setter was never sent and that was not reverted:
//!
//! - a claim whose write failed after it landed, which closes the writing
//!   process's record;
//! - a release whose share had no room for the revert;
//! - a revert its writer's admission refused, such as an acquire whose
//!   setter checkpoint a suspend refused: its ordinary revert is refused in
//!   low power, and the low-power release later reads Normal and settles the
//!   intent `Clear` on its own IDs;
//! - a process that ended between the claim and its setter or revert.
//!
//! That intent, or what settles it, names IDs that sent no 00/04, and the
//! newest 00/04 reply, the earlier record's, is not seeded; a later 00/04
//! that read it would still need its independent 00/84 readback to confirm.
//!
//! The seed is exact for 00/04 only. A revert also drops the reverted job's
//! answered baseline 00/84 ID, whose reply is then the newest 00/84 reply,
//! unlike the journal's rule below. That reply names the mode the job read,
//! which its unsent setter did not change, and no read-only 00/84 that a read
//! job sends is seeded either. A 00/84 read alone never confirms a write: a
//! setter is confirmed only after its own exact acknowledgement.
//!
//! The record's IDs join the live set, ahead of the journal's. An absent,
//! unreadable or refused record adds none: a refused one closes the keyboard
//! owner for the process, so no 00/04 job runs to read a stale reply.
//!
//! The journal names fan, battery and temporary-charge IDs; performance rows
//! and read-only Readers name none. These are the IDs it names that may bear
//! such a reply:
//!
//! - every ID that each family's newest request may have sent, whatever its
//!   progress: its replies are the newest for each command it sent;
//! - the reservations of the owners still live at the end of the journal
//!   (`thermal_history::Owners`): the cleanup IDs of the fan record that
//!   owns manual fans, and the cancellation IDs of the temporary-charge
//!   enable that owns the charge. An attempt that sends them can fail before
//!   it journals a row;
//! - those of an earlier known-not-submitted (6) request (`known_unsent`):
//!   no setter was submitted, but its preflight was answered and a chain
//!   getter may have been sent without a reply. Its preflight can hold the
//!   newest reply of its command, because the newest fan row can belong to
//!   an older record: an owner's settle-only 51 after a retired request
//!   sends no 07/8C;
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
/// The IDs the keyboard Driver-mode record names: its job's baseline,
/// setter, readback and spare. Every build counts them, so the limit does
/// not depend on the composition.
const DRIVER_MODE: usize = 4;
#[cfg(feature = "service-keyboard-driver-mode-v1")]
const _: () = assert!(DRIVER_MODE == openblade_domain::driver_mode_history::IDS);
/// The largest live set: the Driver-mode record's IDs, each family's newest
/// request, and the reservations of a fan owner and a temporary-charge owner
/// older than their family's newest request.
const LIVE: usize = DRIVER_MODE
    + FAN
    + battery_history::EXCHANGES
    + TEMPORARY
    + FAN_CLEANUP
    + temporary_charge_history::EXCHANGES;

/// The pair a Custom selector reply reserves for its CPU and GPU levels,
/// beside the pair that read the selector.
const CUSTOM_LEVELS: usize = 2;
/// The most IDs reserved at once:
/// - a live fan owner, which pins its whole record until it settles
///   (`FanPermit::settle_pins`), and a temporary-charge owner's cancellation;
/// - the next request: a fan record is the largest, and its preflight
///   reserves Custom levels beside it. Mutation jobs run one at a time
///   (`ReadSource::mutate`), so a 00/04 Driver-mode job, which reserves
///   four, is such a request too;
/// - a read: a selector pair and its Custom levels, which this counts.
///
/// A read job can hold more: with live readings and the Driver-mode read it
/// also holds its 00/84, 0D/88 and 00/87 pairs until it retires, 10 IDs in
/// all. That peak, 72, still fits twice in the 174 IDs `LIMIT` leaves, so the
/// assertion below holds, but the 66 and the margin in `LIMIT`'s note count a
/// read as 4.
const DEMAND: usize =
    FAN + temporary_charge_history::EXCHANGES + FAN + CUSTOM_LEVELS + 2 + CUSTOM_LEVELS;

/// The most IDs one start quarantines, so the pool never starves. It holds
/// the largest live set (78), so the live set is always whole, and the rest
/// holds the newest history. 80 leaves 174 of the 254 IDs: twice the most
/// reserved at once (66), so about 108 IDs can still be quarantined by
/// exchanges that fail later before that peak could starve. Three peaks
/// (198) fit beside no limit that holds the live set (254 - 78 = 176), so
/// the margin is two.
pub(super) const LIMIT: usize = 80;
const _: () = assert!(LIMIT >= LIVE && 254 - LIMIT >= 2 * DEMAND);

/// Quarantines IDs in one transaction pool (`Transactions::quarantine`).
pub(super) type Quarantine<'a> = dyn Fn(&[u8]) + 'a;

/// Quarantines the IDs of the Driver-mode record `driver_mode` returns,
/// then what `unanswered` finds in the journal `journal` returns. Each read
/// that fails is retried once, unless it failed fatal admission, which
/// `fatal` keeps as the source's own reads keep it. A journal that still
/// cannot be read or validated adds nothing, as before this seed existed,
/// and an unreadable or refused record adds nothing either; an absent one
/// has nothing to find, so none of them blocks startup.
pub(super) fn quarantine_unanswered(
    journal: &dyn Fn() -> io::Result<Option<Vec<u8>>>,
    driver_mode: &dyn Fn() -> io::Result<Option<[u8; DRIVER_MODE]>>,
    fatal: &Mutex<Option<&'static str>>,
    quarantine: &Quarantine<'_>,
) {
    let record = retried(driver_mode, fatal).flatten();
    let journal = retried(journal, fatal)
        .flatten()
        .and_then(|journal| unanswered(&journal).ok());
    if record.is_some() || journal.is_some() {
        quarantine(&seeded(record, journal.unwrap_or_default()));
    }
}

/// `read`'s result, read again once after a failure that is not fatal
/// admission. A failure that remains is kept in `fatal` when it is fatal.
fn retried<T>(read: &dyn Fn() -> io::Result<T>, fatal: &Mutex<Option<&'static str>>) -> Option<T> {
    read()
        .or_else(|error| {
            if is_fatal_admission(&error) {
                Err(error)
            } else {
                read()
            }
        })
        .map_err(|error| remember_failure(fatal, &error))
        .ok()
}

/// The record's IDs ahead of the journal's, without repeats and at most
/// `LIMIT`. The journal's start with its whole live set, and `LIVE` counts
/// the record's beside it, so the live set stays whole.
fn seeded(record: Option<[u8; DRIVER_MODE]>, journal: Vec<u8>) -> Vec<u8> {
    let mut seen = [false; 256];
    record
        .into_iter()
        .flatten()
        .chain(journal)
        .filter(|id| !std::mem::replace(&mut seen[usize::from(*id)], true))
        .take(LIMIT)
        .collect()
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
            history.push(known_unsent(entry));
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

/// The IDs a known-not-submitted request may have left a reply under: all of
/// `sent` but a fan record's cleanup, which only the owner's Stop sends, in
/// rows of its own.
fn known_unsent(entry: &Entry) -> Vec<u8> {
    match &entry.fan {
        Some(fan) => {
            let pairs = fan.preflight.iter().chain(&fan.ids);
            pairs.flatten().copied().collect()
        }
        None => sent(entry).map(|(_, ids)| ids).unwrap_or_default(),
    }
}

#[cfg(test)]
#[path = "transaction_seed_tests.rs"]
mod tests;

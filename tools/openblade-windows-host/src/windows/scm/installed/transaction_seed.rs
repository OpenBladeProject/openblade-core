//! Transaction IDs an earlier Service process may have sent without reading
//! their reply, recovered from the thermal journal before the first Reader.
//!
//! The firmware reply mailbox outlives the handle and the process
//! (`performance::transactions`), but the pool's quarantine lives in memory,
//! so a new process could issue an ID whose reply an earlier one left unread.
//! A request whose SET did not take effect would then read that reply, which
//! only the ID, class and command tell apart. The journal is the only record
//! that survives the process. It names fan, battery and temporary-charge IDs;
//! performance rows and read-only Readers name none. These are the IDs it
//! names that may have been sent without a correlated reply:
//!
//! - those the final request may have sent, when its last row is unsettled.
//!   No request may follow an unsettled row (`thermal_history::rows`), so
//!   only the final one can be;
//! - those a known-not-submitted (6) request may have sent: no setter was
//!   submitted, but a getter may have been, without a reply;
//! - every `superseded_ids` set, which a cancel names because an earlier
//!   attempt sent it without a correlated reply;
//! - the reservations of the owners still live at the end of the journal:
//!   the cleanup IDs of the fan record that owns manual fans, and the
//!   cancellation IDs of the temporary-charge enable that owns the charge. An
//!   attempt that sends them can fail before it journals a row.
//!
//! A request that reached its family's confirmed terminal read the reply to
//! every request it sent, since recipes never resend, and a fan record's
//! preflight replies were read before its Prepared row was written.
use std::io;

use crate::thermal_history::{Entry, rows};
use openblade_domain::thermal_mutation::settled;

/// The most IDs one start quarantines, so the pool never starves. The live
/// set comes first and is always whole: the final request carries at most 14
/// (a battery record), a fan owner's cleanup 10 and a temporary-charge
/// owner's cancellation 8, so 32 at most. The rest holds the newest
/// known-not-submitted and superseded sets. 48 leaves 206 of the 254 IDs,
/// more than twice what a live fan owner, the next fan change, a
/// temporary-charge enable and a read reserve at once, and still lets about
/// 200 failed reads quarantine one ID each.
pub(super) const LIMIT: usize = 48;

/// Quarantines IDs in one transaction pool (`Transactions::quarantine`).
pub(super) type Quarantine<'a> = dyn Fn(&[u8]) + 'a;

/// Quarantines what `unanswered` finds in `journal`. A journal that cannot be
/// read or validated quarantines nothing, as before this seed existed, and an
/// absent one has nothing to find, so neither blocks startup.
pub(super) fn quarantine_unanswered(
    journal: io::Result<Option<Vec<u8>>>,
    quarantine: &Quarantine<'_>,
) {
    if let Ok(Some(journal)) = journal
        && let Ok(ids) = unanswered(&journal)
    {
        quarantine(&ids);
    }
}

/// The IDs `journal` names that may have been sent without a correlated
/// reply (see the module notes): the live set first, then the rest newest
/// first, without repeats and at most `LIMIT`.
fn unanswered(journal: &[u8]) -> io::Result<Vec<u8>> {
    let mut history = Vec::new();
    // The live owners, tracked as `thermal_history::rows` tracks them.
    let mut fan_owner = None;
    let mut temporary_owner = None;
    let (_, last) = rows(journal, |entry, _| {
        // `carried` includes a superseded set.
        if entry.progress == 6 {
            history.push(carried(entry));
        } else if let Some(superseded) = entry.tfc.as_ref().and_then(|t| t.superseded_ids) {
            history.push(superseded.to_vec());
        }
        if let Some(fan) = &entry.fan {
            if entry.progress == 30 {
                fan_owner = Some(fan.clone());
            } else if matches!(entry.progress, 50 | 51) {
                fan_owner = None;
            }
        }
        if let Some(t) = &entry.tfc
            && matches!(entry.progress, 72 | 73)
        {
            temporary_owner = t.desired.temporary.then_some(t.cancel_ids).flatten();
        }
        Ok(())
    })?;
    let mut live = Vec::new();
    if let Some(last) = last.filter(|last| !settled(last.progress)) {
        live.extend(carried(&last));
    }
    if let Some(fan) = fan_owner {
        live.extend(fan.cleanup.iter().flatten());
    }
    live.extend(temporary_owner.into_iter().flatten());
    let mut seen = [false; 256];
    Ok(live
        .into_iter()
        .chain(history.into_iter().rev().flatten())
        .filter(|id| !std::mem::replace(&mut seen[usize::from(*id)], true))
        .take(LIMIT)
        .collect())
}

/// The IDs the request of `entry` may have sent by the time it wrote that
/// row: a fan setter chain's `ids`, or its owned cleanup's `cleanup` once the
/// cleanup has begun (31, 40 to 43); a battery record's `ids`; and a
/// temporary-charge record's `ids` and `superseded_ids`. A performance row
/// names none.
fn carried(entry: &Entry) -> Vec<u8> {
    if let Some(fan) = &entry.fan {
        let pairs = if matches!(entry.progress, 31 | 40..=43) {
            &fan.cleanup
        } else {
            &fan.ids
        };
        pairs.iter().flatten().copied().collect()
    } else if let Some(battery) = &entry.battery {
        battery.ids.to_vec()
    } else if let Some(t) = &entry.tfc {
        t.ids
            .iter()
            .chain(t.superseded_ids.iter().flatten())
            .copied()
            .collect()
    } else {
        Vec::new()
    }
}

#[cfg(test)]
#[path = "transaction_seed_tests.rs"]
mod tests;

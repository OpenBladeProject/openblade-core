//! Report transaction IDs and the proof that lets one be issued again.
//!
//! The firmware answers a SET_FEATURE request by placing a reply in a mailbox
//! that GET_FEATURE reads. The mailbox belongs to the device, not to a handle:
//! canceling Windows I/O or reopening the handle does not drain it, and an
//! uncertain request may still leave or later produce its reply. If a new
//! request's SET does not take effect, its GET returns that older reply, which
//! only the transaction ID, class and command tell apart. So an ID is issued
//! again only when no reply bearing it can be read again:
//!
//! - an ID that was never sent is free once its Reader has retired;
//! - an ID whose correlated terminal reply was read is free once a later
//!   request with the same class and command is answered: the firmware
//!   answers each request once, and the newer reply replaced it whether the
//!   firmware keeps one reply buffer or one per command;
//! - an ID sent without such a reply (canceled, timed out, busy, malformed or
//!   uncorrelated) is never issued again in this process, even after the
//!   handle is reopened;
//! - a pinned ID is never freed. A durable record that names IDs for later
//!   use or for the journal's cross-family checks keeps them pinned until it
//!   settles; `unpin` then returns them to the rules above, so a sent one
//!   stays quarantined.
//!
//! Issue is round-robin from the last issued ID, so a freed ID returns only
//! after every other free ID has been issued. The process pool starts its
//! round at a random ID: every process would otherwise issue the same IDs
//! first, which a previous process's replies left in the mailbox may bear.
//! The mailbox also outlives the process, so before its first job the Service
//! quarantines the IDs its journal names whose replies, answered or not, an
//! earlier process may have left there (`quarantine`).

use openblade_protocol::{REPORT_LENGTH, RazerReport};
use std::{
    hash::{BuildHasher, RandomState},
    sync::{Mutex, MutexGuard},
};

use super::{FRAME_LENGTH, Frame};

// 0 is reserved by the report API and the durable records refuse 255.
const FIRST: u8 = 1;
const LAST: u8 = 254;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum State {
    Free,
    /// Reserved by a Reader and not yet sent.
    Held,
    /// Submitted without a correlated terminal reply.
    Sent,
    /// Its terminal reply was read; free once this class and command are
    /// answered again.
    Answered {
        class: u8,
        command: u8,
    },
}

#[derive(Clone, Copy)]
struct Id {
    state: State,
    pinned: bool,
}

struct Pool {
    ids: [Id; 256],
    last: u8,
    // `last` is drawn at random before the first reservation.
    unseeded: bool,
}

impl Pool {
    fn id(&mut self, id: u8) -> &mut Id {
        &mut self.ids[usize::from(id)]
    }
}

/// The `last` from which `seed` starts the round, uniformly over 1..=254.
const fn last_before(seed: u64) -> u8 {
    (seed % LAST as u64) as u8
}

pub(super) struct Transactions(Mutex<Pool>);

impl Transactions {
    /// A pool whose first reservation starts at 1.
    pub(super) const fn new() -> Self {
        Self::after(0, false)
    }

    /// The process pool: its first reservation starts at a random ID.
    pub(super) const fn randomized() -> Self {
        Self::after(0, true)
    }

    /// A fresh pool whose first reservation starts at `first`.
    #[cfg(test)]
    pub(super) const fn starting_at(first: u8) -> Self {
        Self::after(first - 1, false)
    }

    /// A fresh pool whose round starts where `seed` puts it.
    #[cfg(test)]
    pub(super) const fn seeded(seed: u64) -> Self {
        Self::after(last_before(seed), false)
    }

    const fn after(last: u8, unseeded: bool) -> Self {
        Self(Mutex::new(Pool {
            ids: [Id {
                state: State::Free,
                pinned: false,
            }; 256],
            last,
            unseeded,
        }))
    }

    // A poisoned pool issues and frees nothing.
    fn pool(&self) -> Option<MutexGuard<'_, Pool>> {
        let mut pool = self.0.lock().ok()?;
        if pool.unseeded {
            pool.last = last_before(RandomState::new().hash_one(0u8));
            pool.unseeded = false;
        }
        Some(pool)
    }

    /// Poisons the pool, as a panic while it was locked would.
    #[cfg(test)]
    pub(super) fn poison(&self) {
        let poisoned = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
            let _pool = self.0.lock();
            panic!("the pool lock is poisoned on purpose");
        }));
        assert!(poisoned.is_err() && self.0.is_poisoned());
    }

    /// Two free IDs, held until they are sent or released. `None` when fewer
    /// than two are free.
    pub(super) fn reserve_pair(&self) -> Option<[u8; 2]> {
        let mut pool = self.pool()?;
        let mut pair = [0; 2];
        let mut found = 0;
        let mut id = pool.last;
        for _ in FIRST..=LAST {
            id = if id >= LAST { FIRST } else { id + 1 };
            if pool.id(id).state == State::Free {
                pair[found] = id;
                found += 1;
                if found == pair.len() {
                    for reserved in pair {
                        pool.id(reserved).state = State::Held;
                    }
                    pool.last = id;
                    return Some(pair);
                }
            }
        }
        None
    }

    /// Called before a request bearing `id` is submitted. `false` when the
    /// pool cannot record it, so nothing may be submitted.
    pub(super) fn sent(&self, id: u8) -> bool {
        let Some(mut pool) = self.pool() else {
            return false;
        };
        pool.id(id).state = State::Sent;
        true
    }

    /// A correlated terminal reply to the request bearing `id` was read.
    pub(super) fn answered(&self, id: u8, class: u8, command: u8) {
        let Some(mut pool) = self.pool() else {
            return;
        };
        let answered = State::Answered { class, command };
        for (other, entry) in pool.ids.iter_mut().enumerate() {
            if entry.state == answered && !entry.pinned && other != usize::from(id) {
                entry.state = State::Free;
            }
        }
        let entry = pool.id(id);
        if entry.state == State::Sent {
            entry.state = answered;
        }
    }

    /// Whether every one of `ids` is still held: reserved and never sent. A
    /// freed ID may have been answered, so it does not count. `false` when
    /// the pool cannot tell.
    pub(super) fn unsent(&self, ids: &[u8]) -> bool {
        self.pool()
            .is_some_and(|mut pool| ids.iter().all(|id| pool.id(*id).state == State::Held))
    }

    /// Treats each free `id` as sent without a correlated reply, so this
    /// process never issues it: an earlier process may have sent it, and a
    /// reply bearing it may still be in the mailbox. An ID this process holds
    /// or has sent keeps its state, and so does every pinned ID, which is one
    /// of those.
    #[cfg(feature = "service-thermal-mutation-v1")]
    pub(super) fn quarantine(&self, ids: &[u8]) {
        if let Some(mut pool) = self.pool() {
            for id in ids.iter().copied().filter(|id| (FIRST..=LAST).contains(id)) {
                let entry = pool.id(id);
                if entry.state == State::Free {
                    entry.state = State::Sent;
                }
            }
        }
    }

    /// Frees `id` if it was never sent and is not pinned.
    pub(super) fn release(&self, id: u8) {
        if let Some(mut pool) = self.pool() {
            let entry = pool.id(id);
            if entry.state == State::Held && !entry.pinned {
                entry.state = State::Free;
            }
        }
    }

    /// Keeps `id` out of the pool until `unpin`, whatever its state.
    pub(super) fn pin(&self, id: u8) {
        if let Some(mut pool) = self.pool() {
            pool.id(id).pinned = true;
        }
    }

    /// Unpins `ids` and frees each one that was never sent. A sent ID stays
    /// quarantined, and an answered one is freed by the next answer to its
    /// class and command, as if it had never been pinned. An ID that is not
    /// pinned is left alone: it was already returned and may have been
    /// issued again, so unpinning twice frees nothing.
    ///
    /// An unsent ID it frees can be issued at once, so it is called only at
    /// a Reader's retirement, for IDs no Reader or record can send again.
    pub(super) fn unpin(&self, ids: impl IntoIterator<Item = u8>) {
        if let Some(mut pool) = self.pool() {
            for id in ids {
                let entry = pool.id(id);
                if entry.pinned {
                    entry.pinned = false;
                    if entry.state == State::Held {
                        entry.state = State::Free;
                    }
                }
            }
        }
    }

    /// Whether `id` is pinned.
    #[cfg(test)]
    pub(super) fn pinned(&self, id: u8) -> bool {
        self.pool().is_none_or(|mut pool| pool.id(id).pinned)
    }

    /// How many IDs could be issued now.
    #[cfg(test)]
    pub(super) fn free(&self) -> usize {
        self.pool().map_or(0, |mut pool| {
            (FIRST..=LAST)
                .filter(|id| pool.id(*id).state == State::Free)
                .count()
        })
    }
}

/// The (ID, class, command) of `sent` when `reply` is its correlated terminal
/// reply: checksum, ID, class and command match, and the status is success
/// or a completed FAILED/NOT_SUPPORTED rejection. Busy, new and timeout
/// replies are not terminal.
pub(super) fn terminal_reply(sent: &Frame, length: usize, reply: &Frame) -> Option<(u8, u8, u8)> {
    let report = &sent[1..];
    let (id, class, command) = (report[1], report[6], report[7]);
    let payload = report.get(8..8 + usize::from(report[5]))?;
    let request = RazerReport::create(id, class, command, payload).ok()?;
    let response = &reply[1..];
    (matches!(length, REPORT_LENGTH | FRAME_LENGTH)
        && reply[0] == 0
        && (request.validate_response(response, None).is_ok()
            || request.is_completed_rejection(response)))
    .then_some((id, class, command))
}

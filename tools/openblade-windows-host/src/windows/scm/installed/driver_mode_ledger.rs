//! The keyboard Driver-mode owner's hold on its durable record
//! (`openblade_domain::driver_mode_history`, `owner\keyboard-driver-mode.bin`).
//!
//! The ledger reads the record once, through the original owner custody, and
//! holds it. It replaces the record only by compare-and-swap on the record it
//! holds and only along the domain's edges, and the owner's in-memory state
//! is derived from the held record (`Standing`), which changes only after a
//! replace returned `Ok`. Memory therefore never runs ahead of the record.
//!
//! Any write that fails, refused or not, closes the ledger for the rest of
//! the process, and nothing reopens it: after an error the file may hold
//! either the old record or the new one, and a closed ledger runs no
//! acquire, maintain or release write. A settle, which follows a job and
//! never admits a setter, first checks its writer's custody admission and,
//! refused, writes nothing and keeps the ledger open: the record keeps its
//! intent, and with it the doubt, for a later read. A claim has no such
//! pass. A record that cannot be read or is refused (corrupt, unknown, a
//! leftover stage, an owner never Active) closes it too; the file is kept
//! as it is. Fatal admission evidence is kept in
//! the Service's retained failure and still fails the caller.
use super::*;
use openblade_domain::driver_mode_history::{IDS, Intent, Record, Settled, State};
use std::sync::atomic::AtomicU64;
use std::time::Duration;

/// An estimate of one staged replace in the protected owner directory
/// (create, write, flush, readback, rename, verify), pending an attended
/// timing on RZ09-0581: what a claim charges for each of its writes before
/// it is made, and the least a settle after it is charged.
pub(super) const REPLACE_FLOOR: Duration = Duration::from_millis(25);

/// The custody admission a record write holds: the custody's ordinary read
/// admission for an acquire and the read-only resolutions, or the owned
/// cleanup admission of the release's scope.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(super) enum Writer {
    Ordinary,
    Cleanup(CleanupScope),
}

/// Where ownership stands, as the held record says.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(super) enum Standing {
    /// No record, or a `Clear` one: nothing owned, nothing in doubt.
    Clear,
    Owned,
    /// An acquire setter may have been submitted unconfirmed.
    AcquireIntent,
    /// A release setter may have been submitted unconfirmed; `prior` is
    /// what ownership was before it.
    ReleaseIntent {
        prior: Settled,
    },
    /// Unreadable, refused, or a write failed: no record write, and so no
    /// setter, for the rest of the process.
    Closed,
}

impl Standing {
    /// Whether OpenBlade has claimed Driver mode or may have: an owned
    /// claim, an acquire in doubt, or a release in doubt over an owned one.
    #[cfg(test)]
    pub(super) fn owns(self) -> bool {
        matches!(
            self,
            Self::Owned
                | Self::AcquireIntent
                | Self::ReleaseIntent {
                    prior: Settled::Owned
                }
        )
    }
}

enum Held {
    Open {
        /// The record the file holds, `None` while there is none.
        record: Option<Record>,
        /// While `record` is an intent this process claimed: the record the
        /// claim replaced, whose IDs its revert restores (`None` when there
        /// was none, or for any other record).
        replaced: Option<Record>,
    },
    Closed,
}

impl Held {
    fn record(&self) -> Option<&Record> {
        match self {
            Self::Open { record, .. } => record.as_ref(),
            Self::Closed => None,
        }
    }
}

pub(super) struct Ledger {
    custody: Arc<dyn ReadCustody>,
    fatal: Arc<Mutex<Option<&'static str>>>,
    held: Mutex<Held>,
    /// The last replace's measured length, in nanoseconds.
    measured: AtomicU64,
    // Acquire claims recorded, so a test can tell a reverted claim from none.
    #[cfg(test)]
    claims: std::sync::atomic::AtomicUsize,
}

impl Ledger {
    /// The ledger over the record `custody` holds now. Only fatal admission
    /// evidence is an error; any other failure to read or admit the record
    /// closes the ledger.
    pub(super) fn load(
        custody: Arc<dyn ReadCustody>,
        fatal: &Arc<Mutex<Option<&'static str>>>,
    ) -> io::Result<Self> {
        let held = match custody.driver_mode_record_read() {
            Ok(record) => Held::Open {
                record,
                replaced: None,
            },
            Err(error) if is_fatal_admission(&error) => {
                remember_failure(fatal, &error);
                return Err(error);
            }
            Err(_) => Held::Closed,
        };
        Ok(Self {
            custody,
            fatal: fatal.clone(),
            held: Mutex::new(held),
            measured: AtomicU64::new(0),
            #[cfg(test)]
            claims: Default::default(),
        })
    }

    pub(super) fn standing(&self) -> Standing {
        let Ok(held) = self.held.lock() else {
            return Standing::Closed;
        };
        let Held::Open { record, .. } = &*held else {
            return Standing::Closed;
        };
        let Some(record) = record else {
            return Standing::Clear;
        };
        match record.state() {
            State::Clear => Standing::Clear,
            State::Owned => Standing::Owned,
            State::AcquireIntent => Standing::AcquireIntent,
            State::ReleaseIntent => Standing::ReleaseIntent {
                prior: record.prior(),
            },
        }
    }

    /// The allowance of a settle after a job: the longest of `REPLACE_FLOOR`
    /// and the last measured replace. After a job that wrote its claim that
    /// is the claim's own length. After one that wrote none, such as a
    /// release whose baseline read Normal, it is an earlier write's, however
    /// long ago, so one slow earlier write can skip a settle that would have
    /// fitted. A skipped settle leaves the record as it was, as any settle
    /// the share has no room for does: `Owned`, from which the next step
    /// acquires again, or an intent for a later read to resolve. The next
    /// write measures afresh.
    pub(super) fn write_allowance(&self) -> Duration {
        REPLACE_FLOOR.max(Duration::from_nanos(self.measured.load(Ordering::Acquire)))
    }

    /// What the job charges for its claim's record writes before it makes
    /// the claim: `REPLACE_FLOOR` each, never a measured replace. A refused
    /// job writes nothing and so measures nothing, so one slow write charged
    /// here would refuse every later claim for the rest of the process. The
    /// job checks its pace again once the claim has returned, with the
    /// claim's real time spent, and sends no setter when the exchanges after
    /// it no longer fit (`DeviceModeStep::Claimed`). A release over an
    /// acquire in doubt writes twice (`claim`).
    pub(super) fn claim_allowance(&self, intent: Intent) -> Duration {
        let writes = match (intent, self.standing()) {
            (Intent::Release, Standing::AcquireIntent) => 2,
            _ => 1,
        };
        REPLACE_FLOOR * writes
    }

    /// Records the claim of a job whose baseline read admitted its setter:
    /// the intent naming its reserved `ids`. The setter may be sent only
    /// after this returns `Ok`.
    ///
    /// A release that finds an acquire in doubt resolves it first: its own
    /// baseline just read Driver, which is what the read-only resolution
    /// records (`AcquireIntent -> Owned`), and only then claims, so the
    /// record never holds an intent over an intent.
    pub(super) fn claim(&self, writer: Writer, intent: Intent, ids: [u8; IDS]) -> io::Result<()> {
        let mut held = self.lock()?;
        if intent == Intent::Release && held_state(&held) == Some(State::AcquireIntent) {
            self.settle_held(&mut held, writer, Settled::Owned)?;
        }
        let Held::Open { record: prior, .. } = &*held else {
            return Err(closed());
        };
        let prior = prior.clone();
        let next = self
            .custody
            .driver_mode_record_owner()
            .and_then(|owner| Record::claim(prior.as_ref(), intent, &owner, ids));
        self.replace(&mut held, writer, next, prior)?;
        #[cfg(test)]
        if intent == Intent::Acquire {
            self.claims.fetch_add(1, Ordering::AcqRel);
        }
        Ok(())
    }

    /// Replaces the held record with the settled state `to`, keeping its
    /// IDs: an intent's settle, its read-only resolution, or `Owned -> Clear`
    /// after a release read Normal. `false` when `writer`'s custody admission
    /// is refused before anything is written: the record, and so the doubt,
    /// stay as they are, and the ledger stays open for a later read to
    /// resolve it.
    pub(super) fn settle(&self, writer: Writer, to: Settled) -> io::Result<bool> {
        let mut held = self.lock()?;
        if !self.admits(writer) {
            return Ok(false);
        }
        self.settle_held(&mut held, writer, to).map(|()| true)
    }

    /// Returns the held intent, whose setter is known not to have been
    /// submitted, to its prior on the IDs of the record its claim replaced
    /// (`Record::revert`): that record's 00/04 reply is still the newest.
    /// Refused before writing as `settle` is.
    pub(super) fn revert(&self, writer: Writer) -> io::Result<bool> {
        let mut held = self.lock()?;
        let next = match &*held {
            Held::Open {
                record: Some(record),
                replaced,
            } if record.state().settled().is_none() => self
                .custody
                .driver_mode_record_owner()
                .and_then(|owner| record.revert(replaced.as_ref(), &owner)),
            _ => return self.close(&mut held, closed()).map(|()| false),
        };
        if !self.admits(writer) {
            return Ok(false);
        }
        self.replace(&mut held, writer, next, None).map(|()| true)
    }

    /// Whether the custody admission `writer` names holds now, as the
    /// replace checks it first.
    fn admits(&self, writer: Writer) -> bool {
        match writer {
            Writer::Ordinary => self.custody.authorize().is_some(),
            Writer::Cleanup(scope) => self.custody.authorize_cleanup(scope).is_some(),
        }
    }

    fn settle_held(&self, held: &mut Held, writer: Writer, to: Settled) -> io::Result<()> {
        let Some(record) = held.record() else {
            return self.close(held, closed());
        };
        let next = self
            .custody
            .driver_mode_record_owner()
            .and_then(|owner| record.settle(to, &owner));
        self.replace(held, writer, next, None)
    }

    /// Writes `next` over the held record, and holds it only once written,
    /// with `replaced` when `next` is this process's claim over it. Every
    /// error closes the ledger.
    fn replace(
        &self,
        held: &mut Held,
        writer: Writer,
        next: io::Result<Record>,
        replaced: Option<Record>,
    ) -> io::Result<()> {
        let Held::Open {
            record: expected, ..
        } = &*held
        else {
            return Err(closed());
        };
        let next = match next {
            Ok(next) => next,
            Err(error) => return self.close(held, error),
        };
        let started = std::time::Instant::now();
        let written = match writer {
            Writer::Ordinary => self
                .custody
                .driver_mode_record_replace(expected.as_ref(), &next),
            Writer::Cleanup(scope) => {
                self.custody
                    .driver_mode_record_replace_cleanup(scope, expected.as_ref(), &next)
            }
        };
        let took = u64::try_from(started.elapsed().as_nanos()).unwrap_or(u64::MAX);
        self.measured.store(took, Ordering::Release);
        match written {
            Ok(()) => {
                *held = Held::Open {
                    record: Some(next),
                    replaced,
                };
                Ok(())
            }
            Err(error) => self.close(held, error),
        }
    }

    fn close(&self, held: &mut Held, error: io::Error) -> io::Result<()> {
        remember_failure(&self.fatal, &error);
        *held = Held::Closed;
        Err(error)
    }

    fn lock(&self) -> io::Result<std::sync::MutexGuard<'_, Held>> {
        self.held.lock().map_err(|_| closed())
    }

    /// Acquire claims recorded so far.
    #[cfg(test)]
    pub(super) fn claims(&self) -> usize {
        self.claims.load(Ordering::Acquire)
    }
}

fn held_state(held: &Held) -> Option<State> {
    held.record().map(Record::state)
}

fn closed() -> io::Error {
    io::Error::new(
        io::ErrorKind::PermissionDenied,
        "The keyboard Driver-mode record is closed for this process.",
    )
}

#[cfg(test)]
#[path = "driver_mode_ledger_tests.rs"]
mod tests;

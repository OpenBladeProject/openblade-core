//! The read-only 00/87 MCU firmware version query on the same serialized
//! native Reader custody as the thermal observation, after managed
//! `McuVersionCommandClient`: one exchange with four zero argument bytes.
//! Only the getter is built here (`openblade_protocol::mcu_version`); the
//! host's guard test fails when a new report construction appears.
//!
//! The command is admitted for the exact RZ09-0581 (1532:02E0) alone, by the
//! capture-backed fixture
//! `evidence/legacy-captures/decoded/rz09-0581-pid-02e0-bios-3.01-mcu-version-fixture.json`.
//! It needs no admission of its own: a Reader exists only for that exact
//! identity (`reviewed_selection`), which is the fixture's. Another model
//! has no Reader, so it is never queried.
use super::fan_rpm::{EXCHANGE, QUERY_RESERVE};
use super::*;
use openblade_protocol::mcu_version::{McuVersion, McuVersionQuery};
use std::sync::{
    Arc,
    atomic::{AtomicU64, Ordering},
};

/// The query's own budget: one exchange with its checkpoints and settle. A
/// stalled query ends here rather than at the job deadline.
const QUERY_BUDGET: Duration = Duration::from_secs(1);

/// How many queries this process has started to send: shared by the read
/// source that asks for the query and the worker thread that sends it. A
/// query counts just before its SET is submitted, on the worker, so one whose
/// observation was dropped after that SET, and whose result therefore never
/// reaches the read source, still counts.
#[derive(Clone, Debug, Default)]
pub(crate) struct McuQuerySends(Arc<AtomicU64>);

impl McuQuerySends {
    pub(crate) fn count(&self) -> u64 {
        self.0.load(Ordering::Acquire)
    }

    fn sending(&self) {
        self.0.fetch_add(1, Ordering::AcqRel);
    }

    /// Counts a query as its SET would.
    #[cfg(test)]
    pub(crate) fn sent_for_tests(&self) {
        self.sending();
    }
}

/// What one read job learned about the MCU firmware version.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub(crate) enum McuVersionRead {
    /// Not queried: not asked, the Reader was already sealed, or the query's
    /// budget could not cover its exchange.
    #[default]
    NotRequested,
    /// Queried, but no valid correlated reply was confirmed within the
    /// query's budget (`McuQuerySends` tells whether its SET went out).
    /// Unknown, never a guessed version.
    Unconfirmed,
    Confirmed(McuVersion),
}

impl<P: Port, A: ObservationProof> Reader<P, A> {
    /// The MCU version query within its own budget, ending `QUERY_RESERVE`
    /// before the job deadline, and only when that budget covers its
    /// exchange. An incomplete query seals this Reader like any incomplete
    /// exchange, so it sends nothing more. `sends` counts it at its SET.
    pub(super) async fn read_mcu_version(
        &mut self,
        authorize: Authorize<'_, A>,
        limit: Limit<'_>,
        sends: &McuQuerySends,
    ) -> McuVersionRead {
        if self.poisoned {
            return McuVersionRead::NotRequested;
        }
        let now = Instant::now();
        let Some(deadline) = limit
            .deadline
            .checked_sub(QUERY_RESERVE)
            .map(|deadline| deadline.min(now + QUERY_BUDGET))
            .filter(|deadline| deadline.saturating_duration_since(now) >= EXCHANGE)
        else {
            return McuVersionRead::NotRequested;
        };
        let query = Limit { deadline, ..limit };
        let admission = self.admission;
        let mut attempt = Attempt {
            reader: self,
            completed: false,
        };
        let version = attempt
            .query_mcu_version(authorize, admission, query, sends)
            .await;
        attempt.completed = version.is_some();
        version.map_or(McuVersionRead::Unconfirmed, McuVersionRead::Confirmed)
    }
}

impl<P: Port, A: ObservationProof> Attempt<'_, P, A> {
    async fn query_mcu_version(
        &mut self,
        authorize: Authorize<'_, A>,
        admission: A,
        limit: Limit<'_>,
        sends: &McuQuerySends,
    ) -> Option<McuVersion> {
        self.checkpoint(authorize, admission, limit).ok()?;
        // IDs are reserved in pairs. The query sends the first; the second is
        // never sent, so no reply can be attributed to it, and retirement
        // frees it.
        let [transaction, _] = self.reader.reserve_pair()?;
        // A slow entry checkpoint can spend the time the budget check found:
        // the SET then stays unsent and its ID free, rather than quarantined
        // by a timeout (as `fan_rpm` checks before each of its SETs).
        if limit.deadline.saturating_duration_since(Instant::now()) < EXCHANGE {
            return None;
        }
        let query = McuVersionQuery::new(transaction);
        let mut frame = [0; FRAME_LENGTH];
        frame[1..].copy_from_slice(&query.request().encode());
        sends.sending();
        self.transfer(Transfer::SendGetter(frame), limit)
            .await
            .ok()?;
        limit.wait(SETTLE).await.ok()?;
        self.checkpoint(authorize, admission, limit).ok()?;
        let (length, frame) = self.transfer(Transfer::ReceiveReply, limit).await.ok()?;
        if !matches!(length, REPORT_LENGTH | FRAME_LENGTH) || frame[0] != 0 {
            return None;
        }
        let version = query.decode(&frame[1..]).ok()?;
        self.checkpoint(authorize, admission, limit).ok()?;
        Some(version)
    }
}

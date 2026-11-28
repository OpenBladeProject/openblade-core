//! The read-only 0D/88 fan speed queries on the same serialized native Reader
//! custody as the thermal observation, after managed `FanRpmQueryPort`: the
//! first selector, then the second, each its own exchange, and both or none.
//! Only the getter is built here (`openblade_protocol::fan_rpm`); the host's
//! guard test fails when a new report construction appears.
use super::*;
use openblade_protocol::fan_rpm::{FanRpmQuery, FanRpmSelector};
use std::sync::{
    Arc,
    atomic::{AtomicU64, Ordering},
};

/// The pair's own budget: two exchanges with their checkpoints and settles.
/// A stalled query ends here rather than at the job deadline.
const QUERY_BUDGET: Duration = Duration::from_secs(1);
/// Job time the queries never take, kept for retirement, the final admission
/// check and publication, so a timed-out query cannot cost the thermal read.
pub(super) const QUERY_RESERVE: Duration = Duration::from_millis(500);
/// The time one SET or one GET is allowed when deciding whether to send.
const TRANSFER: Duration = Duration::from_millis(50);
/// What one exchange needs to complete: its SET, the settle and its GET. A
/// SET sent without it is likely to time out, which quarantines its ID for
/// the rest of the process, so no SET is sent with less.
pub(super) const EXCHANGE: Duration = TRANSFER.saturating_mul(2).saturating_add(SETTLE);

/// How many pairs this process has started to send: shared by the read
/// source that asks for the pair and the worker thread that sends it. A pair
/// counts just before its first SET is submitted, on the worker, so a pair
/// whose observation was dropped after that SET, and whose result therefore
/// never reaches the read source, still counts.
#[derive(Clone, Debug, Default)]
pub(crate) struct FanPairSends(Arc<AtomicU64>);

impl FanPairSends {
    pub(crate) fn count(&self) -> u64 {
        self.0.load(Ordering::Acquire)
    }

    fn sending(&self) {
        self.0.fetch_add(1, Ordering::AcqRel);
    }

    /// Counts a pair as its first SET would.
    #[cfg(test)]
    pub(crate) fn sent_for_tests(&self) {
        self.sending();
    }
}

/// What one read job learned about the two fan speeds.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub(crate) enum FanRpmRead {
    /// Not queried: not asked, the Reader was already sealed, or the pair's
    /// budget could not cover both exchanges.
    #[default]
    NotRequested,
    /// Queried, but the pair was not confirmed within its budget, whether
    /// or not a SET went out (`FanPairSends` tells). Unknown, never a
    /// guessed speed.
    Unconfirmed,
    /// Both selectors, in RPM, from one job.
    Confirmed([u16; 2]),
}

impl<P: Port, A: ObservationProof> Reader<P, A> {
    /// Both fan speed queries within their own budget, ending `QUERY_RESERVE`
    /// before the job deadline, and only when that budget covers both
    /// exchanges. An incomplete pair seals this Reader like any incomplete
    /// exchange, so it sends nothing more. `sends` counts the pair at its
    /// first SET.
    pub(super) async fn read_fan_rpm(
        &mut self,
        authorize: Authorize<'_, A>,
        limit: Limit<'_>,
        sends: &FanPairSends,
    ) -> FanRpmRead {
        if self.poisoned {
            return FanRpmRead::NotRequested;
        }
        let now = Instant::now();
        let Some(deadline) = limit
            .deadline
            .checked_sub(QUERY_RESERVE)
            .map(|deadline| deadline.min(now + QUERY_BUDGET))
            .filter(|deadline| {
                deadline.saturating_duration_since(now) >= EXCHANGE.saturating_mul(2)
            })
        else {
            return FanRpmRead::NotRequested;
        };
        let query = Limit { deadline, ..limit };
        let admission = self.admission;
        let mut attempt = Attempt {
            reader: self,
            completed: false,
        };
        let speeds = attempt
            .read_fan_speeds(authorize, admission, query, sends)
            .await;
        attempt.completed = speeds.is_some();
        speeds.map_or(FanRpmRead::Unconfirmed, FanRpmRead::Confirmed)
    }
}

impl<P: Port, A: ObservationProof> Attempt<'_, P, A> {
    async fn read_fan_speeds(
        &mut self,
        authorize: Authorize<'_, A>,
        admission: A,
        limit: Limit<'_>,
        sends: &FanPairSends,
    ) -> Option<[u16; 2]> {
        self.checkpoint(authorize, admission, limit).ok()?;
        let transactions = self.reader.reserve_pair()?;
        let mut speeds = [0; 2];
        for (index, (transaction, selector)) in transactions
            .into_iter()
            .zip([FanRpmSelector::First, FanRpmSelector::Second])
            .enumerate()
        {
            // A first exchange that ran long leaves the second unsent, and
            // its unsent ID free, rather than quarantined by a timeout.
            if limit.deadline.saturating_duration_since(Instant::now()) < EXCHANGE {
                return None;
            }
            let query = FanRpmQuery::new(transaction, selector);
            let mut frame = [0; FRAME_LENGTH];
            frame[1..].copy_from_slice(&query.request().encode());
            if index == 0 {
                sends.sending();
            }
            self.transfer(Transfer::SendGetter(frame), limit)
                .await
                .ok()?;
            limit.wait(SETTLE).await.ok()?;
            self.checkpoint(authorize, admission, limit).ok()?;
            let (length, frame) = self.transfer(Transfer::ReceiveReply, limit).await.ok()?;
            if !matches!(length, REPORT_LENGTH | FRAME_LENGTH) || frame[0] != 0 {
                return None;
            }
            speeds[index] = query.decode(&frame[1..]).ok()?;
            self.checkpoint(authorize, admission, limit).ok()?;
        }
        Some(speeds)
    }
}

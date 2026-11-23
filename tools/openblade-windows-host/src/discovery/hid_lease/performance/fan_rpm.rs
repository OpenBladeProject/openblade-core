//! The read-only 0D/88 fan speed queries on the same serialized native Reader
//! custody as the thermal observation, after managed `FanRpmQueryPort`: the
//! first selector, then the second, each its own exchange, and both or none.
//! Only the getter is built here (`openblade_protocol::fan_rpm`); the host's
//! guard test fails when a new report construction appears.
use super::*;
use openblade_protocol::fan_rpm::{FanRpmQuery, FanRpmSelector};

/// The pair's own budget: two exchanges with their checkpoints and settles.
/// A stalled query ends here rather than at the job deadline.
const QUERY_BUDGET: Duration = Duration::from_secs(1);
/// Job time the queries never take, kept for retirement, the final admission
/// check and publication, so a timed-out query cannot cost the thermal read.
const QUERY_RESERVE: Duration = Duration::from_millis(500);

/// What one read job learned about the two fan speeds.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub(crate) enum FanRpmRead {
    /// Not queried: not asked, the Reader was already sealed, or too little
    /// job time remained to send the first query.
    #[default]
    NotRequested,
    /// Sent, but the pair was not confirmed within its budget. Unknown,
    /// never a guessed speed.
    Unconfirmed,
    /// Both selectors, in RPM, from one job.
    Confirmed([u16; 2]),
}

impl<P: Port, A: ObservationProof> Reader<P, A> {
    /// Both fan speed queries within their own budget, ending `QUERY_RESERVE`
    /// before the job deadline. An incomplete pair seals this Reader like any
    /// incomplete exchange, so it sends nothing more.
    pub(super) async fn read_fan_rpm(
        &mut self,
        authorize: Authorize<'_, A>,
        limit: Limit<'_>,
    ) -> FanRpmRead {
        if self.poisoned {
            return FanRpmRead::NotRequested;
        }
        let now = Instant::now();
        let Some(deadline) = limit
            .deadline
            .checked_sub(QUERY_RESERVE)
            .map(|deadline| deadline.min(now + QUERY_BUDGET))
            .filter(|deadline| *deadline > now)
        else {
            return FanRpmRead::NotRequested;
        };
        let query = Limit { deadline, ..limit };
        let admission = self.admission;
        let mut attempt = Attempt {
            reader: self,
            completed: false,
        };
        let speeds = attempt.read_fan_speeds(authorize, admission, query).await;
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
    ) -> Option<[u16; 2]> {
        self.checkpoint(authorize, admission, limit).ok()?;
        let transactions = self.reader.reserve_pair()?;
        let mut speeds = [0; 2];
        for (index, (transaction, selector)) in transactions
            .into_iter()
            .zip([FanRpmSelector::First, FanRpmSelector::Second])
            .enumerate()
        {
            let query = FanRpmQuery::new(transaction, selector);
            let mut frame = [0; FRAME_LENGTH];
            frame[1..].copy_from_slice(&query.request().encode());
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

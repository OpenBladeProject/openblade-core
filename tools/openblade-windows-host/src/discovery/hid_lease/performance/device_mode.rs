//! The read-only 00/84 keyboard device-mode query on the same serialized
//! native Reader custody as the thermal observation. Only the query is built
//! here; this Reader has no 00/04 setter path. The host's device-mode guard
//! test fails when a non-test setter or a new report construction appears.
use super::*;
use openblade_protocol::device_mode::{DeviceMode, DeviceModeQuery};

/// The query's own budget: three identity checkpoints, the settle and one
/// exchange. A stalled query ends here rather than at the job deadline.
const QUERY_BUDGET: Duration = Duration::from_secs(1);
/// Job time the query never takes, kept for retirement, the final admission
/// check and publication, so a timed-out query cannot cost the thermal read.
const QUERY_RESERVE: Duration = Duration::from_millis(500);

/// What one read job learned about the keyboard device mode.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub(crate) enum DeviceModeRead {
    /// Not queried: not asked, or too little job time remained to send it.
    #[default]
    NotRequested,
    /// Sent, but no valid correlated reply was confirmed within the query's
    /// budget. Unknown, never a guessed mode.
    Unconfirmed,
    Confirmed(DeviceMode),
}

impl<P: Port, A: ObservationProof> Reader<P, A> {
    /// One read job: the thermal observation, then, only when asked and only
    /// after that observation completed, one 00/84 query within its own
    /// budget, ending `QUERY_RESERVE` before the job deadline. A failed query
    /// seals this Reader like any incomplete exchange, and this Reader sends
    /// nothing more; the thermal result it followed is still returned.
    pub(super) async fn observe_job(
        &mut self,
        authorize: Authorize<'_, A>,
        limit: Limit<'_>,
        device_mode: bool,
    ) -> Option<(ObservedPerformance, DeviceModeRead)> {
        let performance = self.observe(&mut *authorize, limit).await?;
        if !device_mode {
            return Some((performance, DeviceModeRead::NotRequested));
        }
        let now = Instant::now();
        let Some(deadline) = limit
            .deadline
            .checked_sub(QUERY_RESERVE)
            .map(|deadline| deadline.min(now + QUERY_BUDGET))
            .filter(|deadline| *deadline > now)
        else {
            return Some((performance, DeviceModeRead::NotRequested));
        };
        let query = Limit { deadline, ..limit };
        let admission = self.admission;
        let mut attempt = Attempt {
            reader: self,
            completed: false,
        };
        let mode = attempt.read_device_mode(authorize, admission, query).await;
        attempt.completed = mode.is_some();
        Some((
            performance,
            mode.map_or(DeviceModeRead::Unconfirmed, DeviceModeRead::Confirmed),
        ))
    }
}

impl<P: Port, A: ObservationProof> Attempt<'_, P, A> {
    async fn read_device_mode(
        &mut self,
        authorize: Authorize<'_, A>,
        admission: A,
        limit: Limit<'_>,
    ) -> Option<DeviceMode> {
        self.checkpoint(authorize, admission, limit).ok()?;
        // IDs are reserved in pairs. The query sends the first; the second is
        // never sent, so no reply can be attributed to it, and retirement
        // frees it.
        let [transaction, _] = self.reader.reserve_pair()?;
        let query = DeviceModeQuery::new(transaction);
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
        let mode = query.decode(&frame[1..]).ok()?;
        self.checkpoint(authorize, admission, limit).ok()?;
        Some(mode)
    }
}

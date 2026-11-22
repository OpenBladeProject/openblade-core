//! The read-only 00/84 keyboard device-mode query on the same serialized
//! native Reader custody as the thermal observation. This Reader can submit no
//! 00/04 setter: the protocol crate builds those reports in its own tests only.
use super::*;
use openblade_protocol::device_mode::{DeviceMode, DeviceModeQuery};

/// What one read job learned about the keyboard device mode.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub(crate) enum DeviceModeRead {
    /// The job was not asked to read it.
    #[default]
    NotRequested,
    /// Asked, but no valid correlated reply was confirmed. Unknown, never a
    /// guessed mode.
    Unconfirmed,
    Confirmed(DeviceMode),
}

impl<P: Port, A: ObservationProof> Reader<P, A> {
    /// One read job: the thermal observation, then, only when asked and only
    /// after that observation completed, one 00/84 query. A failed query seals
    /// this Reader like any incomplete exchange and is never retried here; the
    /// thermal result it followed stands.
    pub(super) async fn observe_job(
        &mut self,
        authorize: Authorize<'_, A>,
        limit: Limit<'_>,
        device_mode: bool,
    ) -> Option<(ObservedPerformance, DeviceModeRead)> {
        let performance = self.observe(authorize, limit).await?;
        if !device_mode {
            return Some((performance, DeviceModeRead::NotRequested));
        }
        let admission = self.admission;
        let mut attempt = Attempt {
            reader: self,
            completed: false,
        };
        let mode = attempt.read_device_mode(authorize, admission, limit).await;
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
        // IDs are reserved in process-wide pairs. The query sends the first;
        // the second is never sent, so no reply can be attributed to it.
        let [transaction, _] = self.reader.port.reserve_pair()?;
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

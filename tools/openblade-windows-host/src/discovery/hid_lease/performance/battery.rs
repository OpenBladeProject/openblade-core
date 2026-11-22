//! Battery commands on the same serialized, original native Reader custody.
use super::mutation::{Permit, Refusal};
use super::*;
use openblade_domain::battery_history::EXCHANGES;
use openblade_protocol::battery as codec;
impl<P: Port> Reader<P, ReadAdmission> {
    pub(super) async fn apply_battery(
        &mut self,
        permit: &dyn Permit,
        limit: Limit<'_>,
    ) -> Option<()> {
        // No baseline, intent or setter before the entire actual exchange fits.
        // Retained fan cleanup IDs remain reserved.
        let mut ids = [0; EXCHANGES];
        for pair in ids.as_chunks_mut::<2>().0 {
            pair.copy_from_slice(&self.reserve_pair()?);
        }
        let admission = self.admission;
        let mut authorize = || permit.authorize();
        let mut a = Attempt {
            reader: self,
            completed: false,
        };
        let source = a
            .read_power(ids[0], &mut authorize, admission, limit)
            .await?
            .source()? as u8;
        let q = codec::policy_query(ids[1]);
        let raw = codec::policy(
            &q,
            &a.battery_exchange(&q, false, &mut authorize, admission, limit)
                .await?[1..],
        )
        .ok()?;
        permit.observe_battery_policy(raw);
        let q = codec::temporary_query(ids[2]);
        let temporary = match a
            .battery_exchange(&q, false, &mut authorize, admission, limit)
            .await
        {
            Some(reply) => codec::temporary(&q, &reply[1..]).ok(),
            None => None,
        };
        if let Some(temporary) = temporary {
            permit.observe_battery_temporary(temporary);
        }
        let record = permit.resolve_battery(source, raw, temporary, ids).ok()??;
        record.validate().ok()?;
        a.checkpoint(&mut authorize, admission, limit).ok()?;
        permit.record_battery(0).ok()?;
        if raw == record.target {
            permit.record_battery(63).ok()?;
            a.completed = true;
            return Some(());
        }
        let now = a
            .read_power(ids[3], &mut authorize, admission, limit)
            .await?
            .source()?;
        if now != source as i32 {
            permit.refused(Refusal::PowerChanged);
            return None;
        }
        // Battery and known limited external power are valid policy sources; no Full gate.
        a.checkpoint(&mut authorize, admission, limit).ok()?;
        permit.record_battery(60).ok()?;
        let setter = codec::setter(ids[4], record.target)?;
        let reply = a
            .battery_exchange(&setter, true, &mut authorize, admission, limit)
            .await?;
        codec::acknowledge(&setter, &reply[1..], record.target).ok()?;
        permit.record_battery(61).ok()?;
        for (n, id) in ids[5..].iter().enumerate() {
            if n > 0 {
                limit
                    .wait(Duration::from_millis(codec::CONFIRMATION_INTERVAL_MS))
                    .await
                    .ok()?;
            }
            let query = codec::policy_query(*id);
            let reply = a
                .battery_exchange(&query, false, &mut authorize, admission, limit)
                .await?;
            let observed = codec::policy(&query, &reply[1..]).ok()?;
            // Every independently valid getter is observation, even a third
            // value that cannot settle the requested target or permit replay.
            permit.observe_battery_policy(observed);
            if observed == record.target {
                a.checkpoint(&mut authorize, admission, limit).ok()?;
                permit.record_battery(62).ok()?;
                a.completed = true;
                return Some(());
            }
            if observed != raw {
                return None;
            }
        }
        None
    }
}
impl<P: Port> Attempt<'_, P, ReadAdmission> {
    pub(super) async fn battery_exchange(
        &mut self,
        q: &RazerReport,
        write: bool,
        authorize: Authorize<'_, ReadAdmission>,
        admission: ReadAdmission,
        limit: Limit<'_>,
    ) -> Option<Frame> {
        self.checkpoint(authorize, admission, limit).ok()?;
        let mut frame = [0; FRAME_LENGTH];
        frame[1..].copy_from_slice(&q.encode());
        self.transfer(
            if write {
                Transfer::SendMutation(frame)
            } else {
                Transfer::SendGetter(frame)
            },
            limit,
        )
        .await
        .ok()?;
        limit.wait(SETTLE).await.ok()?;
        self.checkpoint(authorize, admission, limit).ok()?;
        let (length, reply) = self.transfer(Transfer::ReceiveReply, limit).await.ok()?;
        if !matches!(length, REPORT_LENGTH | FRAME_LENGTH) || reply[0] != 0 {
            return None;
        }
        Some(reply)
    }
}

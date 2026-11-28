//! TFC uses the existing serialized Reader, never a second HID owner.
use super::{
    mutation::{Permit, Refusal},
    *,
};
use openblade_domain::temporary_charge_history::EXCHANGES;
use openblade_protocol::battery as codec;

fn reserve(mut pair: impl FnMut() -> Option<[u8; 2]>) -> Option<[u8; EXCHANGES]> {
    let mut ids = [0; EXCHANGES];
    for chunk in ids.as_chunks_mut::<2>().0 {
        chunk.copy_from_slice(&pair()?);
    }
    Some(ids)
}

impl<P: Port> Reader<P, ReadAdmission> {
    pub(super) async fn apply_temporary_charge(
        &mut self,
        permit: &dyn Permit,
        enabled: bool,
        limit: Limit<'_>,
    ) -> Option<()> {
        // Enable reserves both its own operation and one process-scoped source
        // cancellation before any GET/Prepared. The record carries the
        // cancellation IDs to a later Reader, so they are retained until the
        // enable's ownership settles (`BatteryPermit::settle_pins`); no fan
        // ID is reused. A cancel replacing reserved IDs an earlier attempt
        // sent draws fresh ones the same way, which `retain` hands to its own
        // record once that names them.
        let ids = match permit.temporary_reserved_ids() {
            Some(v) => v,
            None if permit.temporary_fresh_ids() => reserve(|| self.reserve_retained_pair())?,
            None => reserve(|| self.reserve_pair())?,
        };
        // Only these steps send the IDs. The pool marks an ID sent before its
        // request is submitted, so once they stop it alone tells whether any
        // of them left.
        permit.observe_temporary_unsent(false);
        let applied = self.temporary_charge(permit, enabled, ids, limit).await;
        permit.observe_temporary_unsent(self.port.transactions().unsent(&ids));
        applied
    }

    async fn temporary_charge(
        &mut self,
        permit: &dyn Permit,
        enabled: bool,
        ids: [u8; EXCHANGES],
        limit: Limit<'_>,
    ) -> Option<()> {
        let cancel_ids = if enabled {
            Some(reserve(|| self.reserve_retained_pair())?)
        } else {
            None
        };
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
        let query = codec::temporary_query(ids[1]);
        let flags = codec::power_flags(
            &query,
            &a.battery_exchange(&query, false, &mut authorize, admission, limit)
                .await?[1..],
        )
        .ok()?;
        permit.observe_battery_temporary(flags & 4 != 0);
        // Policy is an enable predicate only. An unavailable policy cannot
        // prevent a fresh flags-only direct cancellation.
        let policy = if enabled {
            let q = codec::policy_query(ids[2]);
            let p = codec::policy(
                &q,
                &a.battery_exchange(&q, false, &mut authorize, admission, limit)
                    .await?[1..],
            )
            .ok()?;
            permit.observe_battery_policy(p);
            Some(p)
        } else {
            None
        };
        let record = permit
            .resolve_temporary_charge(source, flags, policy, ids, cancel_ids)
            .ok()??;
        a.reader.retain(permit);
        record.validate().ok()?;
        a.checkpoint(&mut authorize, admission, limit).ok()?;
        permit.record_battery(0).ok()?;
        if flags == record.target {
            permit.record_battery(73).ok()?;
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
        let query = codec::temporary_query(ids[4]);
        let flags = codec::power_flags(
            &query,
            &a.battery_exchange(&query, false, &mut authorize, admission, limit)
                .await?[1..],
        )
        .ok()?;
        permit.observe_battery_temporary(flags & 4 != 0);
        // SET replaces the complete byte. Never clear boost/fan/dimming or
        // merge guessed bits. A changed baseline ends this operation read-only.
        if flags != record.baseline || !codec::temporary_write_admitted(flags) {
            return None;
        }
        a.checkpoint(&mut authorize, admission, limit).ok()?;
        permit.record_battery(70).ok()?;
        let setter = codec::temporary_setter(ids[5], enabled);
        let reply = a
            .battery_exchange(&setter, true, &mut authorize, admission, limit)
            .await?;
        codec::acknowledge(&setter, &reply[1..], record.target).ok()?;
        permit.record_battery(71).ok()?;
        let query = codec::temporary_query(ids[6]);
        let observed = codec::power_flags(
            &query,
            &a.battery_exchange(&query, false, &mut authorize, admission, limit)
                .await?[1..],
        )
        .ok()?;
        permit.observe_battery_temporary(observed & 4 != 0);
        if observed != record.target {
            return None;
        }
        a.checkpoint(&mut authorize, admission, limit).ok()?;
        permit.record_battery(72).ok()?;
        a.completed = true;
        Some(())
    }
}

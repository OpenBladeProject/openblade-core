//! Actual fan actuation on the existing serialized Reader, including retained Stop IDs.
use super::mutation::{Permit, Refusal};
use super::*;
use openblade_domain::{
    fan_history::CONTRACT,
    fan_recipe::{FanIntent, Plan},
};
use openblade_protocol::fan::FanPair;
impl<P: Port> Reader<P, ReadAdmission> {
    pub(super) async fn apply_fan(
        &mut self,
        permit: &dyn Permit,
        limit: Limit<'_>,
    ) -> Option<ObservedPerformance> {
        let admission = self.admission;
        let mut authorize = || permit.authorize();
        let cleanup = permit.cleanup();
        let (baseline, power, preflight) = if cleanup {
            let r = permit.fan_record()?;
            let mut a = Attempt {
                reader: self,
                completed: false,
            };
            let b = a
                .read_reserved_pair(&mut authorize, admission, limit, r.cleanup[0], false)
                .await?;
            let p = a
                .read_power(r.cleanup[1][0], &mut authorize, admission, limit)
                .await?;
            a.completed = true;
            (b, p, r.preflight)
        } else {
            let modes = self.port.reserve_pair()?;
            let power = self.port.reserve_pair()?;
            let mut a = Attempt {
                reader: self,
                completed: false,
            };
            let b = a
                .read_reserved_pair(&mut authorize, admission, limit, modes, false)
                .await?;
            let p = a
                .read_power(power[0], &mut authorize, admission, limit)
                .await?;
            a.completed = true;
            (b, p, [modes, power])
        };
        let source = power.source()?;
        let (plan, record, ids) = if cleanup {
            let r = permit.fan_record()?;
            let owned = Plan::select(baseline, r.intent, source)?;
            // Fresh generic-manual topology retains only this already established owner.
            if !owned.confirms(baseline) || owned.selector != r.selector || source != r.source {
                return None;
            }
            let plan = Plan::select(baseline, FanIntent::checked(0, 3700)?, source)?;
            (plan, r.clone(), r.cleanup[2..].to_vec())
        } else {
            let intent = permit.resolve_fan(source, baseline).ok()??;
            let plan = Plan::select(baseline, intent, source)?;
            if plan.unchanged {
                permit.confirm_fan_noop(source, baseline).ok()?;
                return Some(baseline);
            } // No ownership, no journal or setter.
            let mut reserve = || {
                self.port.reserve_pair().or_else(|| {
                    permit.refused(Refusal::TransactionCapacity);
                    None
                })
            };
            let mut ids = Vec::new();
            for _ in 0..plan.pairs() {
                ids.push(reserve()?);
            }
            let mut cleanup_ids = Vec::new();
            if intent.manual() {
                for _ in 0..5 {
                    cleanup_ids.push(reserve()?);
                }
            }
            // Request bytes are supplied by the retained permit, never client recipe bytes.
            let mut record = permit.fan_record()?;
            record.contract = CONTRACT.into();
            record.intent = intent;
            record.selector = plan.selector;
            record.entry = plan.entry;
            record.source = source;
            record.preflight = preflight;
            record.ids = ids.clone();
            record.cleanup = cleanup_ids;
            record.validate().ok()?;
            permit.prepare_fan(record.clone()).ok()?;
            (plan, record, ids)
        };
        let mut a = Attempt {
            reader: self,
            completed: false,
        };
        a.checkpoint(&mut authorize, admission, limit).ok()?;
        permit.record_fan(if cleanup { 31 } else { 0 }).ok()?;
        let mut groups = Vec::new();
        if plan.entry {
            groups.push(FanPair::state(ids[0], plan.selector, true)?);
        }
        if plan.intent.manual() {
            groups.push(FanPair::target(
                ids[usize::from(plan.entry)],
                plan.intent.rpm,
            )?);
        } else {
            groups.push(FanPair::state(ids[0], plan.selector, false)?);
        }
        let setters = plan.setters();
        let power_start = groups.len();
        let mut step = 0usize;
        let mut last_state = None;
        let mut last_target = None;
        for (group_index, group) in groups.iter().enumerate() {
            if group_index == 1 && plan.intermediate {
                let observed = a
                    .read_reserved_pair(&mut authorize, admission, limit, ids[ids.len() - 2], false)
                    .await?;
                if !plan.confirms(observed) {
                    return None;
                }
            }
            for (index, command) in group.commands().iter().enumerate() {
                if group_index == 1 {
                    if let Some(at) = last_state {
                        sleep_until(limit.deadline.min(at + Duration::from_millis(210))).await;
                        limit.check().ok()?;
                    }
                }
                if let Some(at) = last_target {
                    sleep_until(limit.deadline.min(at + Duration::from_millis(65))).await;
                    limit.check().ok()?;
                }
                let current = a
                    .read_power(
                        ids[power_start + step / 2][step % 2],
                        &mut authorize,
                        admission,
                        limit,
                    )
                    .await?;
                if current.source() != Some(record.source)
                    || !current.admits(if plan.selector == 2 {
                        openblade_domain::thermal_mutation::Preset::Performance
                    } else {
                        openblade_domain::thermal_mutation::Preset::Balanced
                    })
                {
                    permit.refused(Refusal::PowerChanged);
                    return None;
                }
                a.checkpoint(&mut authorize, admission, limit).ok()?;
                permit
                    .record_fan(if cleanup { 40 } else { 10 } + step as u8 * 2)
                    .ok()?;
                a.checkpoint(&mut authorize, admission, limit).ok()?;
                let mut frame = [0; FRAME_LENGTH];
                frame[1..].copy_from_slice(&command.encode());
                a.transfer(Transfer::SendMutation(frame), limit)
                    .await
                    .ok()?;
                if plan.intent.manual() && (!plan.entry || group_index == 1) {
                    last_target = Some(Instant::now());
                } else {
                    last_state = Some(Instant::now());
                }
                limit.wait(SETTLE).await.ok()?;
                a.checkpoint(&mut authorize, admission, limit).ok()?;
                let (length, reply) = a.transfer(Transfer::ReceiveReply, limit).await.ok()?;
                if !matches!(length, REPORT_LENGTH | FRAME_LENGTH) || reply[0] != 0 {
                    return None;
                }
                group.acknowledge(index, &reply[1..]).ok()?;
                permit
                    .record_fan(if cleanup { 41 } else { 11 } + step as u8 * 2)
                    .ok()?;
                step += 1;
            }
        }
        if step != setters {
            return None;
        }
        let observed = a
            .read_reserved_pair(&mut authorize, admission, limit, *ids.last()?, false)
            .await?;
        if !plan.confirms(observed) {
            return None;
        }
        a.checkpoint(&mut authorize, admission, limit).ok()?;
        permit
            .record_fan(if plan.intent.manual() { 30 } else { 50 })
            .ok()?;
        a.completed = true;
        Some(observed)
    }
}

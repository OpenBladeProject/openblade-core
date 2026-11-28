//! Actual fan actuation on the existing serialized Reader, including retained Stop IDs.
use super::mutation::{EXCHANGES_BEFORE_CHECK, Pace, Permit, Refusal, Setters};
use super::*;
use openblade_domain::{
    fan_history::CONTRACT,
    fan_recipe::{FanIntent, Plan, automatic_handback},
};
use openblade_protocol::fan::FanPair;
impl<P: Port> Reader<P, ReadAdmission> {
    pub(super) async fn apply_fan(
        &mut self,
        permit: &dyn Permit,
        limit: Limit<'_>,
    ) -> Option<ObservedPerformance> {
        let admission = self.admission;
        let fence = super::mutation::PowerFence::new(permit);
        let mut authorize = || fence.authorize();
        let cleanup = permit.cleanup();
        let mut pace = Pace::start(self);
        let (baseline, power, preflight) = if cleanup {
            let r = permit.fan_record()?;
            let mut a = Attempt {
                reader: self,
                completed: false,
            };
            let b = a
                .read_reserved_pair(&mut authorize, admission, limit, r.cleanup[0], false)
                .await?;
            // Automatic fans in this fresh, agreeing 0D/82 pair are firmware's
            // own handback state, under any selector and power source; neither
            // is the owned manual-fan obligation, so 07/8C is not read for it.
            // Managed writes nothing then (`OwnedManualFanHandbackBlockReason`:
            // "already uses firmware-managed Automatic mode") and
            // `ObserveTopology` clears ownership, so settle with 51 before any
            // plan exists: battery and recipe-less selectors never reach a setter.
            // A disagreeing or undecodable readback has no baseline. Stop
            // publishes nothing and reads no Custom levels: Custom with Automatic
            // fans settles whatever its levels (managed would reject a High/High
            // read and keep ownership; see docs/windows-rust-native-fans.md).
            if automatic_handback(b) {
                a.completed = true;
                permit.record_fan(51).ok()?;
                return Some(b);
            }
            let p = a
                .read_power(r.cleanup[1][0], &mut authorize, admission, limit)
                .await?;
            a.completed = true;
            (b, p, r.preflight)
        } else {
            // The record names these preflight IDs, so they are pinned with
            // the recipe's.
            let modes = self.reserve_retained_pair()?;
            let power = self.reserve_retained_pair()?;
            let mut a = Attempt {
                reader: self,
                completed: false,
            };
            // An explicit reply publishes this baseline, so it uses the same
            // Custom getter admission as every other native observation: a
            // Custom selector is reported with its read levels, and High/High
            // Automatic (rejected by native and managed reads) has no baseline.
            let b = a
                .read_reserved_pair(&mut authorize, admission, limit, modes, true)
                .await?;
            let p = a
                .read_power(power[0], &mut authorize, admission, limit)
                .await?;
            a.completed = true;
            (b, p, [modes, power])
        };
        let source = fence.admit(power)?;
        let (plan, ids, carried, budget) = if cleanup {
            let r = permit.fan_record()?;
            let owned = Plan::select(baseline, r.intent, source)?;
            // Fresh generic-manual topology retains only this already established
            // owner. Its power class may differ from the recorded one: the
            // handback commands are the same on every external class, and managed
            // owned Stop admits any fresh external reading that agrees with
            // Windows AC. The rows keep the recorded source.
            if !owned.confirms(baseline) || owned.selector != r.selector {
                return None;
            }
            // A thermal submission still unresolved fences the handback too
            // (managed `ScopedThermalWriteBlockReason`, prior-authority cleanup
            // included).
            if permit.thermal_fenced(limit.deadline.saturating_duration_since(Instant::now())) {
                permit.refused(Refusal::ThermalPending);
                return None;
            }
            let plan = Plan::select(baseline, FanIntent::checked(0, 3700)?, source)?;
            (plan, r.cleanup[2..].to_vec(), None, None)
        } else {
            // Owned Stop cleanup above keeps the owner's selector: shutdown hands
            // back to firmware without applying saved settings. An explicit
            // return to Automatic writes the saved destination's own setters.
            let (intent, performance) = permit.resolve_fan(source, baseline).ok()??;
            let plan = Plan::select_toward(baseline, intent, source, performance);
            // Automatic requested and already reported: a true no-op when the
            // plan is unchanged or no native plan exists (battery, or Automatic
            // fans under Silent, battery selectors and Custom). Nothing is
            // written; the reply names any saved destination left unapplied. A
            // plan that changes the destination applies it.
            // The permit refuses HyperBoost here (managed read parity).
            if !intent.manual() && automatic_handback(baseline) && plan.is_none_or(|p| p.unchanged)
            {
                permit.confirm_fan_noop(source, baseline).ok()?;
                return Some(baseline);
            } // No ownership, no journal or setter.
            let plan = plan?;
            if permit.thermal_fenced(limit.deadline.saturating_duration_since(Instant::now())) {
                permit.refused(Refusal::ThermalPending);
                return None;
            }
            // A carried destination is applied through managed
            // `SetPerformanceAsync` or `SetCustomPerformanceAsync`, which
            // refuse Windows battery before any ordinary command
            // (`allowBattery: IsBatterySelectorCommand`) but admit a battery
            // preset's selectors there unchecked. Native requires Windows AC
            // presence for every carried destination, a battery preset
            // included (stricter; a documented deviation). Without it nothing
            // is prepared.
            if performance.is_some() && !fence.require_presence() {
                permit.refused(Refusal::PowerChanged);
                return None;
            }
            let mut reserve = || self.reserve_retained_pair();
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
            let (remaining, fixed) = pace_of(&plan);
            // Request bytes are supplied by the retained permit, never client recipe bytes.
            let mut record = permit.fan_record()?;
            record.contract = if cfg!(feature = "service-battery-policy-v1") {
                CONTRACT
            } else {
                openblade_domain::fan_history::PREVIOUS_CONTRACT
            }
            .into();
            record.intent = intent;
            record.selector = plan.selector;
            record.entry = plan.entry;
            record.source = source;
            record.preflight = preflight;
            record.ids = ids.clone();
            record.cleanup = cleanup_ids;
            record.custom = plan
                .custom_levels()
                .map(|levels| [levels.cpu as u8, levels.gpu as u8]);
            record.validate().ok()?;
            permit.prepare_fan(record.clone()).ok()?;
            (plan, ids, performance, Some((remaining, fixed)))
        };
        // Managed keeps an observed AC selector only in the class it first read
        // (`ProductionBatteryPort` acPreset with `EnsureThermalWritePowerAsync`),
        // so a transition that writes the Performance state pair without a
        // carried preset, owned Stop included, stops on a class change. Managed
        // has no acPreset for a target-only change under manual fans, and
        // Balanced has no such pin. The class is this operation's own first
        // reading, not the recorded one, so a steady Stop on another external
        // class still completes.
        if plan.selector == 2 && carried.is_none() && (plan.entry || !plan.intent.manual()) {
            fence.pin_class();
        }
        let mut a = Attempt {
            reader: self,
            completed: false,
        };
        // Owned cleanup is paced once, before its 31: a handback that cannot
        // finish by its deadline ends with no row and no setter, so it leaves
        // nothing unsettled. Its baseline reads may already have used the
        // owner's reserved IDs, so the owner's one attempt stays spent.
        if cleanup {
            let (remaining, fixed) = pace_of(&plan);
            if !Pace::covers_before_rows(a.reader, remaining, plan.setters() as u32, fixed, limit) {
                a.completed = true;
                return None;
            }
        }
        a.checkpoint(&mut authorize, admission, limit).ok()?;
        pace.prepared(a.reader, || permit.record_fan(if cleanup { 31 } else { 0 }))
            .ok()?;
        // The setters take the first reserved IDs in order; the power readings,
        // any intermediate read and the final reads follow.
        let mut next = ids.iter().copied();
        let mut groups = Vec::new();
        if let Some(destination) = plan.destination {
            // The saved destination's own setters, with the performance
            // writer's codec and bytes: no fan pair first.
            groups.push(Group::Destination(Setters::new(destination, &mut || {
                next.next()
            })?));
        } else {
            if plan.entry {
                groups.push(Group::Fan(FanPair::state(
                    next.next()?,
                    plan.selector,
                    true,
                )?));
            }
            groups.push(Group::Fan(if plan.intent.manual() {
                FanPair::target(next.next()?, plan.intent.rpm)?
            } else {
                FanPair::state(next.next()?, plan.selector, false)?
            }));
        }
        let setters = plan.setters();
        let power_start = ids.len() - next.len();
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
            for (index, command) in group.commands().into_iter().enumerate() {
                if group_index == 1
                    && let Some(at) = last_state
                {
                    sleep_until(limit.deadline.min(at + Duration::from_millis(210))).await;
                    limit.check().ok()?;
                }
                if let Some(at) = last_target {
                    sleep_until(limit.deadline.min(at + Duration::from_millis(65))).await;
                    limit.check().ok()?;
                }
                // Managed brackets each fan command's query with Windows
                // samples too (`AdmitOrdinaryThermalCommandAsync`).
                let Some(windows) = fence.before_query() else {
                    permit.refused(Refusal::PowerChanged);
                    return None;
                };
                let current = a
                    .read_power(
                        ids[power_start + step / 2][step % 2],
                        &mut authorize,
                        admission,
                        limit,
                    )
                    .await?;
                // The commands are the same on every external class; a class
                // change continues only while the permit's destination holds.
                if !fence.admits(current, source, windows) {
                    permit.refused(Refusal::PowerChanged);
                    return None;
                }
                // Paced once, before the first intent row (`mutation::Pace`);
                // owned Stop cleanup is not paced. A refusal here submitted
                // nothing: retirement records it as known not submitted (row
                // 6) and returns the record's IDs to the pool.
                if step == 0
                    && let Some((remaining, fixed)) = budget
                    && !pace.covers(
                        a.reader,
                        remaining - EXCHANGES_BEFORE_CHECK,
                        setters as u32,
                        fixed,
                        limit,
                    )
                {
                    a.completed = true;
                    permit.refused(Refusal::Deadline);
                    return None;
                }
                // From here a setter may reach the device, and a later Reader
                // or the journal's cross-family checks may rely on the
                // record's IDs, so they stay pinned until the record settles
                // (`FanPermit::settle_pins`). Owned Stop cleanup reserved none
                // of its own.
                a.reader.retain(permit);
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
                group.acknowledge(index, &reply[1..])?;
                permit
                    .record_fan(if cleanup { 41 } else { 11 } + step as u8 * 2)
                    .ok()?;
                step += 1;
            }
        }
        if step != setters {
            return None;
        }
        // Custom settles only with its levels read back, as the performance
        // writer's final read does.
        let observed = match plan.custom_levels() {
            Some(_) => {
                let final_ids = *ids.get(ids.len().checked_sub(2)?)?;
                a.read_reserved_custom(&mut authorize, admission, limit, final_ids, *ids.last()?)
                    .await?
            }
            None => {
                a.read_reserved_pair(&mut authorize, admission, limit, *ids.last()?, false)
                    .await?
            }
        };
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
/// The exchanges a fan plan has left before its first setter, and its
/// scheduled spacing. Each setter is one power read and its own exchange; the
/// intermediate pair, the final pair and any Custom levels follow. An entry
/// waits up to 210 ms before its target pair, and a manual target pair 65 ms
/// between its commands.
fn pace_of(plan: &Plan) -> (u32, Duration) {
    let remaining = 2 * plan.setters() as u32
        + 2 * u32::from(plan.intermediate)
        + 2
        + 2 * u32::from(plan.custom_levels().is_some());
    let fixed = Duration::from_millis(
        if plan.entry { 210 } else { 0 } + if plan.intent.manual() { 65 } else { 0 },
    );
    (remaining, fixed)
}
/// One run of setters: a fan state or target pair, or a carried destination's
/// own setters.
enum Group {
    Fan(FanPair),
    Destination(Setters),
}
impl Group {
    fn commands(&self) -> Vec<&RazerReport> {
        match self {
            Self::Fan(pair) => pair.commands().iter().collect(),
            Self::Destination(setters) => setters.commands().collect(),
        }
    }
    fn acknowledge(&self, index: usize, reply: &[u8]) -> Option<()> {
        match self {
            Self::Fan(pair) => pair.acknowledge(index, reply).ok(),
            Self::Destination(setters) => setters.acknowledge(index, reply),
        }
    }
}

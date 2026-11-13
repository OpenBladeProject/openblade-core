//! Typed mutation on the existing native port and process-wide transaction pool.
use super::*;
use openblade_domain::thermal_mutation::{Plan, Preset, Progress, confirms};
use openblade_protocol::thermal_preset::PresetPair;
use std::sync::Arc;

/// Internal host evidence. Clients cannot construct a permit or journal progress.
/// The implementation retains the actual owner, original request, loaded epoch,
/// user configuration and durable journal through callback retirement.
pub(crate) trait Permit: Send + Sync {
    /// Called synchronously before the Reader registers a native mutation worker.
    fn mutation_registered(&self) {}
    fn authorize(&self) -> Option<ReadAdmission>;
    /// Result publication is distinct from permission to submit another command.
    fn result_admitted(&self, expected: ReadAdmission) -> bool {
        self.authorize() == Some(expected)
    }
    fn is_battery(&self) -> bool {
        false
    }
    fn resolve_battery(
        &self,
        _source: u8,
        _baseline: u8,
        _temporary: Option<bool>,
        _ids: [u8; 14],
    ) -> io::Result<Option<openblade_domain::battery_history::BatteryRecord>> {
        Err(io::ErrorKind::PermissionDenied.into())
    }
    fn record_battery(&self, _progress: u8) -> io::Result<()> {
        Err(io::ErrorKind::PermissionDenied.into())
    }
    fn observe_battery(&self, _raw: u8, _temporary: Option<bool>) {}
    fn is_fan(&self) -> bool {
        false
    }
    fn resolve_fan(
        &self,
        _power: i32,
        _baseline: ObservedPerformance,
    ) -> io::Result<Option<openblade_domain::fan_recipe::FanIntent>> {
        Ok(None)
    }
    fn confirm_fan_noop(&self, _source: i32, _baseline: ObservedPerformance) -> io::Result<()> {
        Ok(())
    }
    fn prepare_fan(&self, _record: openblade_domain::fan_history::FanRecord) -> io::Result<()> {
        Err(io::ErrorKind::PermissionDenied.into())
    }
    fn fan_record(&self) -> Option<openblade_domain::fan_history::FanRecord> {
        None
    }
    fn record_fan(&self, _progress: u8) -> io::Result<()> {
        Err(io::ErrorKind::PermissionDenied.into())
    }
    fn cleanup(&self) -> bool {
        false
    }
    fn resolve(&self, power: i32, baseline: ObservedPerformance) -> io::Result<Option<Preset>>;
    fn refused(&self, _cause: Refusal) {}
    fn failed(&self, _error: &io::Error) {}
    fn retired(&self) -> io::Result<Settlement> {
        Ok(Settlement::Complete)
    }
    fn record(&self, progress: Progress) -> io::Result<()>;
}

/// Positive native retirement is independent from durable settlement.
pub(crate) enum Settlement {
    Complete,
    Unresolved(io::Error),
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum Refusal {
    TransactionCapacity,
    CurrentAdmission,
    PowerChanged,
}

impl<P: Port> Reader<P, ReadAdmission> {
    pub(super) async fn apply_preset(
        &mut self,
        permit: &dyn Permit,
        limit: Limit<'_>,
    ) -> Option<ObservedPerformance> {
        if permit.is_battery() {
            self.apply_battery(permit, limit).await;
            // Battery results travel through their typed permit, never a fabricated thermal value.
            return None;
        }
        if permit.is_fan() {
            return self.apply_fan(permit, limit).await;
        }
        let mut authorize = || permit.authorize();
        let baseline = self.observe(&mut authorize, limit).await?;
        let initial_power_ids = self.port.reserve_pair()?;
        let admission = self.admission;
        let power = {
            let mut attempt = Attempt {
                reader: self,
                completed: false,
            };
            let power = attempt
                .read_power(initial_power_ids[0], &mut authorize, admission, limit)
                .await?;
            attempt.completed = true;
            power
        };
        let power_source = power.source()?;
        let destination = permit.resolve(power_source, baseline).ok()??;
        if Plan::from_observation(baseline, destination) == Plan::Unchanged {
            return Some(baseline);
        }
        // Reserve all remaining commands, including the independent final pair,
        // before journaling/submitting. Exhaustion cannot strand a partial pair.
        let mut reserve = || {
            self.port.reserve_pair().or_else(|| {
                permit.refused(Refusal::TransactionCapacity);
                None
            })
        };
        let pair = PresetPair::new(reserve()?, destination)?;
        let power_ids = reserve()?;
        let final_ids = reserve()?;
        let admission = self.admission;
        let mut attempt = Attempt {
            reader: self,
            completed: false,
        };
        attempt.checkpoint(&mut authorize, admission, limit).ok()?;
        permit.record(Progress::Prepared).ok()?;
        for (index, command) in pair.commands().iter().enumerate() {
            attempt.checkpoint(&mut authorize, admission, limit).ok()?;
            let power = attempt
                .read_power(power_ids[index], &mut authorize, admission, limit)
                .await?;
            // Balanced admits limited external power; Performance requires the
            // exact decoder's sufficient-adapter comparison, never ACLineStatus.
            if power.source() != Some(power_source) || !power.admits(destination) {
                permit.refused(Refusal::PowerChanged);
                return None;
            }
            attempt.checkpoint(&mut authorize, admission, limit).ok()?;
            permit
                .record(if index == 0 {
                    Progress::FirstIntent
                } else {
                    Progress::SecondIntent
                })
                .ok()?;
            attempt.checkpoint(&mut authorize, admission, limit).ok()?;
            let mut frame = [0; FRAME_LENGTH];
            frame[1..].copy_from_slice(&command.encode());
            attempt
                .transfer(Transfer::SendMutation(frame), limit)
                .await
                .ok()?;
            limit.wait(SETTLE).await.ok()?;
            attempt.checkpoint(&mut authorize, admission, limit).ok()?;
            let (length, frame) = attempt.transfer(Transfer::ReceiveReply, limit).await.ok()?;
            if !matches!(length, REPORT_LENGTH | FRAME_LENGTH) || frame[0] != 0 {
                return None;
            }
            pair.acknowledge(index, &frame[1..]).ok()?;
            permit
                .record(if index == 0 {
                    Progress::FirstAcknowledged
                } else {
                    Progress::SecondAcknowledged
                })
                .ok()?;
        }
        let observed = attempt
            .read_reserved_pair(&mut authorize, admission, limit, final_ids, false)
            .await?;
        if !confirms(observed, destination) {
            return None;
        }
        attempt.checkpoint(&mut authorize, admission, limit).ok()?;
        permit.record(Progress::Confirmed).ok()?;
        attempt.completed = true;
        Some(observed)
    }
}
#[derive(Clone, Copy)]
pub(super) struct Power {
    actual: u16,
    recommended: u16,
}
impl Power {
    pub(super) fn source(self) -> Option<i32> {
        if self.recommended == 0 {
            None
        } else if self.actual == 0 {
            Some(1)
        } else if self.actual >= self.recommended {
            Some(0)
        } else {
            Some(2)
        }
    }
    pub(super) fn admits(self, destination: Preset) -> bool {
        // Managed battery policy admits BatterySaver/BatteryBalanced, not
        // these AC/limited-external destinations (automation::performance_source_eligible).
        self.actual > 0
            && self.recommended > 0
            && (destination == Preset::Balanced || self.actual >= self.recommended)
    }
}
fn watts(level: u8) -> Option<u16> {
    const LEVELS: [u16; 16] = [
        0, 40, 45, 60, 65, 80, 90, 100, 120, 130, 150, 180, 200, 230, 250, 310,
    ];
    match level {
        0x11 => Some(280),
        0x12 => Some(330),
        0x13 => Some(400),
        _ => LEVELS.get(level as usize).copied(),
    }
}
impl<P: Port> Attempt<'_, P, ReadAdmission> {
    pub(super) async fn read_power(
        &mut self,
        id: u8,
        authorize: Authorize<'_, ReadAdmission>,
        admission: ReadAdmission,
        limit: Limit<'_>,
    ) -> Option<Power> {
        self.checkpoint(authorize, admission, limit).ok()?;
        let query = RazerReport::create(id, 7, 0x8c, &[0, 0]).ok()?;
        let mut frame = [0; FRAME_LENGTH];
        frame[1..].copy_from_slice(&query.encode());
        self.transfer(Transfer::SendGetter(frame), limit)
            .await
            .ok()?;
        limit.wait(SETTLE).await.ok()?;
        self.checkpoint(authorize, admission, limit).ok()?;
        let (length, frame) = self.transfer(Transfer::ReceiveReply, limit).await.ok()?;
        if !matches!(length, REPORT_LENGTH | FRAME_LENGTH) || frame[0] != 0 {
            return None;
        }
        let payload = query.validate_response(&frame[1..], Some(2)).ok()?;
        let [actual, recommended] = payload.as_slice() else {
            return None;
        };
        Some(Power {
            actual: watts(*actual)?,
            recommended: watts(*recommended)?,
        })
    }
}

#[cfg(windows)]
pub(crate) fn native_mutation_worker(permit: Arc<dyn Permit>) -> Worker<ReadAdmission> {
    super::windows::mutation_worker(permit)
}

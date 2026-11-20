//! Typed mutation on the existing native port and process-wide transaction pool.
use super::*;
use openblade_domain::{
    automation::UNCLASSIFIED_EXTERNAL,
    thermal_mutation::{Plan, Preset, Progress, confirms},
};
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
    fn temporary_target(&self) -> Option<bool> {
        None
    }
    fn temporary_reserved_ids(&self) -> Option<[u8; 8]> {
        None
    }
    fn resolve_temporary_charge(
        &self,
        _source: u8,
        _flags: u8,
        _policy: Option<u8>,
        _ids: [u8; 8],
        _cancel_ids: Option<[u8; 8]>,
    ) -> io::Result<Option<openblade_domain::temporary_charge_history::TemporaryChargeRecord>> {
        Err(io::ErrorKind::PermissionDenied.into())
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
    fn observe_battery_policy(&self, _raw: u8) {}
    fn observe_battery_temporary(&self, _temporary: bool) {}
    fn is_fan(&self) -> bool {
        false
    }
    /// The fan intent, plus the saved preset a return to Automatic applies.
    fn resolve_fan(
        &self,
        _power: i32,
        _baseline: ObservedPerformance,
    ) -> io::Result<Option<(openblade_domain::fan_recipe::FanIntent, Option<Preset>)>> {
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
    /// The Windows power kind, read at the moment of the call: `Some(false)`
    /// is AC (ACLineStatus online), `Some(true)` battery and `None` unknown. It
    /// is never wattage evidence.
    fn windows_on_battery(&self) -> Option<bool> {
        None
    }
    /// Fresh positive Windows AC presence. An unclassified 07/8C reading admits
    /// an ordinary thermal setter only with it.
    fn external_presence(&self) -> bool {
        self.windows_on_battery() == Some(false)
    }
    /// Whether this operation's resolved destination is unchanged for a fresh
    /// external class that differs from its first one (see `PowerFence::admits`).
    fn destination_holds(&self, _power: i32) -> bool {
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
    /// The adapter query completed without a class and Windows AC presence is
    /// not fresh: managed refuses the request before any intent is stored.
    UnclassifiedBlocked,
}

impl<P: Port> Reader<P, ReadAdmission> {
    pub(super) async fn apply_preset(
        &mut self,
        permit: &dyn Permit,
        limit: Limit<'_>,
    ) -> Option<ObservedPerformance> {
        if let Some(enabled) = permit.temporary_target() {
            self.apply_temporary_charge(permit, enabled, limit).await;
            return None;
        }
        if permit.is_battery() {
            self.apply_battery(permit, limit).await;
            // Battery results travel through their typed permit, never a fabricated thermal value.
            return None;
        }
        if permit.is_fan() {
            return self.apply_fan(permit, limit).await;
        }
        let fence = PowerFence::new(permit);
        let mut authorize = || fence.authorize();
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
        let power_source = fence.admit(power)?;
        let destination = permit.resolve(power_source, baseline).ok()??;
        if Plan::from_observation(baseline, destination) == Plan::Unchanged {
            return Some(baseline);
        }
        // Managed `requireExternalPowerOnDirectHyperBoostExit`: a direct exit to
        // a non-Balanced preset needs every command's fresh reading in the same
        // classified external class, so a class change stops it without
        // replay. Managed's Balanced exit applies no class rule.
        if baseline == ObservedPerformance::HyperBoostObserved && destination != Preset::Balanced {
            fence.pin_class();
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
            // Each command needs a fresh external reading from the exact decoder,
            // never ACLineStatus alone, that agrees with Windows AC presence.
            if !fence.admits(power, power_source) {
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
/// Fresh-power admission for one ordinary thermal operation, after managed
/// `AdmitOrdinaryThermalCommandAsync` (f6502f85). An unclassified reading needs
/// fresh Windows AC presence. A performance setter needs it on every external
/// class (`allowBattery: false`); a fan setter only on unclassified power,
/// since managed admits a classified fan command while Windows reports battery
/// (`allowBattery: true`). The presence a reading was admitted with must hold
/// at every later checkpoint (managed `OrdinarySourceUnchanged` rechecks the
/// Windows source at submission), so losing it stops before the next command.
pub(super) struct PowerFence<'a> {
    permit: &'a dyn Permit,
    fans: bool,
    external: AtomicBool,
    pinned: AtomicBool,
}
impl<'a> PowerFence<'a> {
    pub(super) fn new(permit: &'a dyn Permit) -> Self {
        Self {
            permit,
            fans: permit.is_fan(),
            external: AtomicBool::new(false),
            pinned: AtomicBool::new(false),
        }
    }
    pub(super) fn authorize(&self) -> Option<ReadAdmission> {
        let admission = self.permit.authorize()?;
        if self.external.load(Ordering::Acquire) && !self.permit.external_presence() {
            self.permit.refused(Refusal::PowerChanged);
            return None;
        }
        Some(admission)
    }
    /// Every later reading must be in the first reading's class: managed pins
    /// it for a direct HyperBoost exit and for a fan transition that keeps an
    /// AC selector (`EnsureThermalWritePowerAsync`).
    pub(super) fn pin_class(&self) {
        self.pinned.store(true, Ordering::Release);
    }
    /// The admitted class, and whether later checkpoints need presence.
    fn source(&self, power: Power) -> Option<(i32, bool)> {
        let windows = self.permit.windows_on_battery();
        let presence = windows == Some(false);
        let source = power.thermal_source(presence, self.fans && windows == Some(true))?;
        Some((source, presence && source != 1))
    }
    /// The class of the operation's first reading.
    pub(super) fn admit(&self, power: Power) -> Option<i32> {
        let Some((source, external)) = self.source(power) else {
            if power == Power::Unclassified {
                self.permit.refused(Refusal::UnclassifiedBlocked);
            }
            return None;
        };
        self.external.store(external, Ordering::Release);
        Some(source)
    }
    /// A setter-time reading against the first reading's class `source`.
    /// Managed admits any fresh external reading that agrees with Windows AC
    /// presence; a class change between setters, either way between full,
    /// limited and unclassified, continues only while the class is not pinned
    /// and the permit's destination is the same for the new class, so a
    /// profile that differs there stops the pair. Battery always stops it.
    pub(super) fn admits(&self, power: Power, source: i32) -> bool {
        let Some((current, external)) = self.source(power) else {
            return false;
        };
        let admitted = if current == source {
            source != 1
        } else {
            !self.pinned.load(Ordering::Acquire)
                && current != 1
                && source != 1
                && self.permit.destination_holds(current)
        };
        if admitted {
            self.external.store(external, Ordering::Release);
        }
        admitted
    }
}
/// One fresh 07/8C adapter query.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(super) enum Power {
    /// Both levels decode and the recommended level is nonzero.
    Classified { actual: u16, recommended: u16 },
    /// A completed query that classifies nothing: a correlated FAILED or
    /// NOT_SUPPORTED rejection, or a reply whose actual level is unmapped or
    /// whose recommended level is unmapped or zero. Managed reports it as
    /// `AdapterPowerState.Unknown` with `UnclassifiedAdapterResponseAvailable`.
    /// It is never full, limited or battery power.
    Unclassified,
}
impl Power {
    fn decode(actual: u8, recommended: u8) -> Self {
        match (watts(actual), watts(recommended)) {
            (Some(actual), Some(recommended @ 1..)) => Self::Classified {
                actual,
                recommended,
            },
            _ => Self::Unclassified,
        }
    }
    /// The classified class: 0 full, 1 battery, 2 limited. Battery policy and
    /// temporary charge use only this, so unclassified power never admits them.
    pub(super) fn source(self) -> Option<i32> {
        match self {
            Self::Classified { recommended: 0, .. } | Self::Unclassified => None,
            Self::Classified { actual: 0, .. } => Some(1),
            Self::Classified {
                actual,
                recommended,
            } => Some(if actual >= recommended { 0 } else { 2 }),
        }
    }
    /// The class an ordinary thermal setter is admitted on. Every external
    /// class, unclassified (3) included, counts with fresh positive Windows AC
    /// presence: managed `AdmitOrdinaryThermalCommandAsync` requires the fresh
    /// adapter reading and the Windows external source to agree. A classified
    /// class also counts `unchecked`, when managed admits the command without
    /// any check: a fan command while Windows reports battery. Battery needs no
    /// presence; no ordinary setter is admitted on it.
    pub(super) fn thermal_source(self, presence: bool, unchecked: bool) -> Option<i32> {
        match self {
            Self::Unclassified => presence.then_some(UNCLASSIFIED_EXTERNAL),
            classified => classified
                .source()
                .filter(|s| *s == 1 || presence || unchecked),
        }
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
        // A correlated terminal rejection completed the query without wattage;
        // every other validation failure is no reading at all.
        if query.is_completed_rejection(&frame[1..]) {
            return Some(Power::Unclassified);
        }
        let payload = query.validate_response(&frame[1..], Some(2)).ok()?;
        let [actual, recommended] = payload.as_slice() else {
            return None;
        };
        // Managed maps a success reply to unclassified power only with single
        // report geometry (`WindowsRazerHidSession`,
        // `UnclassifiedAdapterResponseAvailable`); without it, no reading.
        Some(Power::decode(*actual, *recommended)).filter(|power| {
            *power != Power::Unclassified
                || openblade_protocol::has_single_report_geometry(&frame[1..])
        })
    }
}

#[cfg(windows)]
pub(crate) fn native_mutation_worker(permit: Arc<dyn Permit>) -> Worker<ReadAdmission> {
    super::windows::mutation_worker(permit)
}

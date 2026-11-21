//! Typed mutation on the existing native port and process-wide transaction pool.
use super::*;
use openblade_domain::{
    automation::UNCLASSIFIED_EXTERNAL,
    thermal_mutation::{Destination, Plan, Progress, admits_class, confirms, pins_class},
};
use openblade_protocol::{custom_performance::CustomLevelPair, thermal_preset::PresetPair};
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
    /// The fan intent, plus the saved destination a return to Automatic applies.
    fn resolve_fan(
        &self,
        _power: i32,
        _baseline: ObservedPerformance,
    ) -> io::Result<Option<(openblade_domain::fan_recipe::FanIntent, Option<Destination>)>> {
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
    fn resolve(&self, power: i32, baseline: ObservedPerformance)
    -> io::Result<Option<Destination>>;
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
    /// No admitted native sequence reaches the destination from the confirmed
    /// state (`Plan::NoDirectPath`); nothing was prepared or submitted.
    NoDirectPath,
    /// The request's remaining time cannot cover the rest of the recipe at the
    /// pace this operation measured (`Pace`); nothing was prepared or
    /// submitted.
    Deadline,
}

/// One operation's measured device pace: when it began and how many
/// exchanges the Reader had completed then. Before the first journal row an
/// operation checks that the rest of its recipe fits the request deadline at
/// that pace, so a slow device refuses a recipe it cannot finish instead of
/// timing out between setters.
#[derive(Clone, Copy)]
pub(super) struct Pace {
    started: Instant,
    exchanges: u32,
}
impl Pace {
    pub(super) fn start<P: Port, A>(reader: &Reader<P, A>) -> Self {
        Self {
            started: Instant::now(),
            exchanges: reader.exchanges,
        }
    }
    /// Whether `remaining` more exchanges at the mean time per exchange
    /// measured since the start, plus `fixed` scheduled waits, end by the
    /// deadline. The mean counts everything the operation did, storage and
    /// admission included, so it does not undercount.
    pub(super) fn covers<P: Port, A>(
        self,
        reader: &Reader<P, A>,
        remaining: u32,
        fixed: Duration,
        limit: Limit<'_>,
    ) -> bool {
        let done = reader.exchanges.saturating_sub(self.exchanges).max(1);
        let now = Instant::now();
        (now.saturating_duration_since(self.started) / done)
            .checked_mul(remaining)
            .and_then(|rest| rest.checked_add(fixed))
            .and_then(|rest| now.checked_add(rest))
            .is_some_and(|end| end <= limit.deadline)
    }
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
        let pace = Pace::start(self);
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
        match Plan::from_observation(baseline, destination) {
            Plan::Unchanged => return Some(baseline),
            Plan::NoDirectPath => {
                permit.refused(Refusal::NoDirectPath);
                return None;
            }
            Plan::Direct { .. } => {}
        }
        // Managed `requireExternalPowerOnDirectHyperBoostExit`: a direct exit to
        // a non-Balanced preset needs every command's fresh reading in the same
        // classified external class, so a class change stops it without
        // replay. Managed's Balanced exit applies no class rule.
        if pins_class(baseline, destination) {
            fence.pin_class();
        }
        fence.bind_destination(destination);
        // Reserve all remaining commands, including the independent final read
        // and its Custom level getters, before journaling/submitting.
        // Exhaustion cannot strand a partial recipe.
        let mut reserve = || {
            self.port.reserve_pair().or_else(|| {
                permit.refused(Refusal::TransactionCapacity);
                None
            })
        };
        let setters = Setters::new(destination, &mut reserve)?;
        let mut power_ids = Vec::new();
        for _ in 0..destination.setters().div_ceil(2) {
            power_ids.push(reserve()?);
        }
        let final_ids = reserve()?;
        let level_ids = match destination {
            Destination::Custom(_) => Some(reserve()?),
            Destination::Preset(_) => None,
        };
        // Each setter is one power read and its own exchange; the final pair
        // and any Custom levels follow.
        let remaining = 2 * destination.setters() as u32 + 2 + 2 * u32::from(level_ids.is_some());
        if !pace.covers(self, remaining, Duration::ZERO, limit) {
            permit.refused(Refusal::Deadline);
            return None;
        }
        let admission = self.admission;
        let mut attempt = Attempt {
            reader: self,
            completed: false,
        };
        attempt.checkpoint(&mut authorize, admission, limit).ok()?;
        permit.record(Progress::Prepared).ok()?;
        for (index, command) in setters.commands().enumerate() {
            let (intent, acknowledged) = Progress::setter(destination, index)?;
            attempt.checkpoint(&mut authorize, admission, limit).ok()?;
            let power = attempt
                .read_power(
                    power_ids[index / 2][index % 2],
                    &mut authorize,
                    admission,
                    limit,
                )
                .await?;
            // Each command needs a fresh external reading from the exact decoder,
            // never ACLineStatus alone, that agrees with Windows AC presence.
            if !fence.admits(power, power_source) {
                permit.refused(Refusal::PowerChanged);
                return None;
            }
            attempt.checkpoint(&mut authorize, admission, limit).ok()?;
            permit.record(intent).ok()?;
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
            setters.acknowledge(index, &frame[1..])?;
            permit.record(acknowledged).ok()?;
        }
        // Custom settles only with its levels read back (managed
        // `verifyFinalState`); a mismatch or an unreadable level confirms
        // nothing and nothing is replayed.
        let observed = match level_ids {
            Some(level_ids) => {
                attempt
                    .read_reserved_custom(&mut authorize, admission, limit, final_ids, level_ids)
                    .await?
            }
            None => {
                attempt
                    .read_reserved_pair(&mut authorize, admission, limit, final_ids, false)
                    .await?
            }
        };
        if !confirms(observed, destination) {
            return None;
        }
        attempt.checkpoint(&mut authorize, admission, limit).ok()?;
        permit.record(Progress::Confirmed).ok()?;
        attempt.completed = true;
        Some(observed)
    }
}
/// A destination's reviewed setters in submission order: its 0D/02 pair, then
/// for Custom the 0D/07 CPU and GPU levels (managed `SetCustomPerformanceAsync`).
/// The fan return to Automatic writes the same setters (`apply_fan`).
pub(super) struct Setters {
    pair: PresetPair,
    levels: Option<CustomLevelPair>,
}
impl Setters {
    pub(super) fn new(
        destination: Destination,
        reserve: &mut impl FnMut() -> Option<[u8; 2]>,
    ) -> Option<Self> {
        Some(match destination {
            Destination::Preset(preset) => Self {
                pair: PresetPair::new(reserve()?, preset)?,
                levels: None,
            },
            Destination::Custom(levels) => Self {
                pair: PresetPair::custom(reserve()?)?,
                levels: Some(CustomLevelPair::new(reserve()?, levels.cpu, levels.gpu)?),
            },
        })
    }
    pub(super) fn commands(&self) -> impl Iterator<Item = &RazerReport> {
        self.pair
            .commands()
            .iter()
            .chain(self.levels.iter().flat_map(|levels| levels.commands()))
    }
    /// The exact echo of setter `index`.
    pub(super) fn acknowledge(&self, index: usize, reply: &[u8]) -> Option<()> {
        match (index, &self.levels) {
            (0 | 1, _) => self.pair.acknowledge(index, reply).ok(),
            (2 | 3, Some(levels)) => levels.acknowledge(index - 2, reply).ok(),
            _ => None,
        }
    }
}
/// Fresh-power admission for one ordinary thermal operation, after managed
/// `AdmitOrdinaryThermalCommandAsync` (f6502f85). An unclassified reading needs
/// fresh Windows AC presence. A performance setter needs it on every external
/// class (`allowBattery: false`), and so does a fan setter that carries a
/// performance preset. Other fan setters need it only on unclassified power,
/// since managed admits such a classified command while Windows reports
/// battery (`allowBattery: true`). The presence a reading was admitted with must hold
/// at every later checkpoint (managed `OrdinarySourceUnchanged` rechecks the
/// Windows source at submission), so losing it stops before the next command.
/// A battery preset's pair is also admitted on battery, read with Windows
/// battery (`thermal_mutation::admits_class`); that Windows battery reading
/// must then hold at every later checkpoint in the same way.
pub(super) struct PowerFence<'a> {
    permit: &'a dyn Permit,
    // Whether Windows battery admits a classified reading with no check: a fan
    // command that carries no performance preset.
    unchecked: AtomicBool,
    external: AtomicBool,
    battery: AtomicBool,
    pinned: AtomicBool,
    // A performance request's resolved destination; fan permits bind none.
    destination: std::sync::OnceLock<Destination>,
}
impl<'a> PowerFence<'a> {
    pub(super) fn new(permit: &'a dyn Permit) -> Self {
        Self {
            permit,
            unchecked: AtomicBool::new(permit.is_fan()),
            external: AtomicBool::new(false),
            battery: AtomicBool::new(false),
            pinned: AtomicBool::new(false),
            destination: std::sync::OnceLock::new(),
        }
    }
    pub(super) fn authorize(&self) -> Option<ReadAdmission> {
        let admission = self.permit.authorize()?;
        if self.external.load(Ordering::Acquire) && !self.permit.external_presence()
            || self.battery.load(Ordering::Acquire)
                && self.permit.windows_on_battery() != Some(true)
        {
            self.permit.refused(Refusal::PowerChanged);
            return None;
        }
        Some(admission)
    }
    /// Setter-time readings are then admitted by the destination's own class
    /// rule (`thermal_mutation::admits_class`).
    pub(super) fn bind_destination(&self, destination: Destination) {
        let _ = self.destination.set(destination);
    }
    /// Every later reading must be in the first reading's class: managed pins
    /// it for a direct HyperBoost exit and for a fan transition that keeps an
    /// AC selector (`EnsureThermalWritePowerAsync`).
    pub(super) fn pin_class(&self) {
        self.pinned.store(true, Ordering::Release);
    }
    /// A fan return to Automatic that carries a saved preset applies it
    /// through managed `SetPerformanceAsync` (`allowBattery: false`): the
    /// operation needs fresh Windows AC presence now and at every later
    /// reading and checkpoint. False when presence is missing.
    pub(super) fn require_presence(&self) -> bool {
        self.unchecked.store(false, Ordering::Release);
        let presence = self.permit.external_presence();
        if presence {
            self.external.store(true, Ordering::Release);
        }
        presence
    }
    /// The admitted class and the Windows power kind it was read with.
    fn source(&self, power: Power) -> Option<(i32, Option<bool>)> {
        let windows = self.permit.windows_on_battery();
        let presence = windows == Some(false);
        let unchecked = self.unchecked.load(Ordering::Acquire) && windows == Some(true);
        Some((power.thermal_source(presence, unchecked)?, windows))
    }
    /// The class of the operation's first reading.
    pub(super) fn admit(&self, power: Power) -> Option<i32> {
        let Some((source, windows)) = self.source(power) else {
            if power == Power::Unclassified {
                self.permit.refused(Refusal::UnclassifiedBlocked);
            }
            return None;
        };
        self.external
            .store(windows == Some(false) && source != 1, Ordering::Release);
        Some(source)
    }
    /// A setter-time reading against the first reading's class `source`.
    /// Managed admits any fresh external reading that agrees with Windows AC
    /// presence; a class change between setters, either way between full,
    /// limited and unclassified, continues only while the class is not pinned
    /// and the permit's destination is the same for the new class, so a
    /// profile that differs there stops the pair. A change to or from battery
    /// always stops it. Battery itself admits only a bound battery preset.
    pub(super) fn admits(&self, power: Power, source: i32) -> bool {
        let Some((current, windows)) = self.source(power) else {
            return false;
        };
        let class_admitted = match self.destination.get() {
            Some(destination) => admits_class(*destination, current, windows),
            None => current != 1,
        };
        let admitted = class_admitted
            && (current == source
                || !self.pinned.load(Ordering::Acquire)
                    && current != 1
                    && source != 1
                    && self.permit.destination_holds(current));
        if admitted {
            self.external
                .store(windows == Some(false) && current != 1, Ordering::Release);
            self.battery.store(current == 1, Ordering::Release);
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

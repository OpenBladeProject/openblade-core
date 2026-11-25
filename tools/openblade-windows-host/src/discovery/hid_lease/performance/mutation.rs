//! Typed mutation on the existing native port and process-wide transaction pool.
use super::*;
#[cfg(feature = "service-thermal-hyperboost-exit-v1")]
use openblade_domain::thermal_mutation::restored;
use openblade_domain::{
    automation::UNCLASSIFIED_EXTERNAL,
    thermal_mutation::{
        Destination, Plan, Progress, admits_class, admits_windows, confirms, pins_class,
    },
};
use openblade_protocol::{custom_performance::CustomLevelPair, thermal_preset::PresetPair};
use std::sync::{Arc, Mutex, atomic::Ordering};

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
    /// Whether a temporary-charge cancel without reserved IDs draws fresh
    /// ones that its record names in place of its enable's reserved IDs, which
    /// an earlier attempt may have sent. They are pinned like those
    /// (`Reader::reserve_retained_pair`) and handed to `retained_pins`.
    fn temporary_fresh_ids(&self) -> bool {
        false
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
    /// Called with `false` before a temporary-charge operation may send its
    /// IDs, and again once it stops, with whether the transaction pool proves
    /// that none was sent.
    fn observe_temporary_unsent(&self, _unsent: bool) {}
    /// The keyboard Driver-mode write job this permit carries, if it is one
    /// (`Reader::apply_device_mode`). Its result travels through the job.
    #[cfg(feature = "service-keyboard-driver-mode-v1")]
    fn device_mode(&self) -> Option<&super::DeviceModeJob> {
        None
    }
    /// This job's own budget from its start, after the lane wait, when it is
    /// shorter than the request recipe budget.
    #[cfg(feature = "service-keyboard-driver-mode-v1")]
    fn budget(&self) -> Option<std::time::Duration> {
        None
    }
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
    /// The record this permit resolved names `ids`, which the Reader pinned
    /// for it (`Reader::retain`). They stay pinned until the permit settles
    /// them; by default they stay pinned for the process.
    fn retained_pins(&self, _ids: &[u8]) {}
    /// Pins of records that have durably settled since the last call: no
    /// request bearing them can be sent for those records again. The Reader
    /// unpins them when it retires.
    fn settled_pins(&self) -> Vec<u8> {
        Vec::new()
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

/// The IDs a Reader pinned for one durable record (`Permit::retained_pins`).
#[derive(Default)]
pub(crate) struct RecordPins(Mutex<Vec<u8>>);

impl RecordPins {
    pub(crate) fn retain(&self, ids: &[u8]) {
        // A poisoned lock records nothing, so those IDs stay pinned.
        if let Ok(mut pins) = self.0.lock() {
            pins.extend_from_slice(ids);
        }
    }
}

/// Pins of settled records, waiting for a Reader to retire and unpin them
/// (`Permit::settled_pins`). The permits of one runtime share it, so a record
/// that settles after its own Reader retired is unpinned by the next one.
#[derive(Default)]
pub(crate) struct SettledPins(Mutex<Vec<u8>>);

impl SettledPins {
    /// Queues `record`'s pins and leaves it none, so settling a record again
    /// queues nothing. A poisoned lock queues nothing, and the pins stay.
    pub(crate) fn settle(&self, record: &RecordPins) {
        if let (Ok(mut pins), Ok(mut queue)) = (record.0.lock(), self.0.lock()) {
            queue.append(&mut pins);
        }
    }

    pub(crate) fn take(&self) -> Vec<u8> {
        self.0
            .lock()
            .map(|mut queue| std::mem::take(&mut *queue))
            .unwrap_or_default()
    }

    #[cfg(all(test, feature = "service-fan-mutation-v2"))]
    pub(crate) fn peek(&self) -> Vec<u8> {
        self.0.lock().map(|queue| queue.clone()).unwrap_or_default()
    }
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
    #[cfg(not(feature = "service-thermal-hyperboost-exit-v1"))]
    NoDirectPath,
    /// The request's remaining time cannot cover the rest of the recipe at the
    /// pace this operation measured (`Pace`). Only the Prepared row was
    /// written and the first setter's power reading taken; no setter was
    /// submitted.
    Deadline,
}

/// The journal rows each setter appends after the pace check: its intent row
/// before the send and its acknowledged row after the reply.
pub(super) const APPENDS_PER_SETTER: u32 = 2;
/// The closing row after the final read: Confirmed, or the fan recipe's
/// settled row.
pub(super) const CLOSING_APPENDS: u32 = 1;
/// The exchanges a recipe has completed when it checks its pace: the first
/// setter's own 07/8C reading.
pub(super) const EXCHANGES_BEFORE_CHECK: u32 = 1;

/// One operation's measured pace. A recipe checks it once, after its Prepared
/// row and its first setter's own 07/8C reading and before that setter's
/// intent row, so a device or journal too slow to finish refuses the recipe
/// with nothing submitted instead of timing out between setters. With `E`
/// exchanges and `A` appends left, the recipe runs only if
///
/// `now + slowest + (E - 1) × second + A × append + fixed < deadline`.
///
/// The comparison is strict because `Limit::check` fails at the deadline.
///
/// - `slowest` and `second` are the two longest exchanges since `start`
///   (`Exchanges`), each timed from the previous reply to its own. They
///   charge the work before each send with the exchange: discovery and
///   authorization before the first, and every checkpoint's authorizations,
///   custody check and origin verification. One stall is charged once, at
///   its own length, and every other exchange at the second longest. A
///   single slow read or append is not a slow device, so it does not refuse
///   a recipe that has seconds to spare. A device that is slow throughout has two slow samples by the
///   check, the recipe's first 07/8C reading and the first setter's own, so
///   the second longest carries it.
/// - `append` is the Prepared append, timed directly (`prepared`). Every
///   later row goes through the same journal replace, so each of the
///   `A = APPENDS_PER_SETTER × setters + CLOSING_APPENDS` rows left is
///   charged at that length.
/// - `fixed` is the recipe's scheduled spacing that no exchange includes.
///
/// Excluded: retirement after the closing row, and any growth after the check
/// in the work per exchange, since every remaining exchange is charged at a
/// measured one.
pub(super) struct Pace {
    append: Duration,
}
impl Pace {
    pub(super) fn start<P: Port, A>(reader: &mut Reader<P, A>) -> Self {
        reader.exchanges = Exchanges {
            last: Some(Instant::now()),
            ..Exchanges::default()
        };
        Self {
            append: Duration::ZERO,
        }
    }
    /// Writes the Prepared row through `append` and times it. The exchange
    /// after it is timed from the end of the append, which `covers` charges
    /// for every row left instead.
    pub(super) fn prepared<P: Port, A>(
        &mut self,
        reader: &mut Reader<P, A>,
        append: impl FnOnce() -> io::Result<()>,
    ) -> io::Result<()> {
        let started = Instant::now();
        let written = append();
        self.append = Instant::now().saturating_duration_since(started);
        if let Some(last) = reader.exchanges.last.as_mut() {
            *last += self.append;
        }
        written
    }
    /// Whether the `exchanges` and the rows of `setters` still to come,
    /// plus `fixed` scheduled waits, end by the deadline at this pace.
    pub(super) fn covers<P: Port, A>(
        &self,
        reader: &Reader<P, A>,
        exchanges: u32,
        setters: u32,
        fixed: Duration,
        limit: Limit<'_>,
    ) -> bool {
        let appends = APPENDS_PER_SETTER * setters + CLOSING_APPENDS;
        ends_by(
            &reader.exchanges,
            exchanges,
            appends,
            self.append,
            fixed,
            limit,
        )
    }
    /// `covers` before a recipe's first row, for owned fan cleanup, which
    /// writes no Prepared row before its 31. No append is timed yet, so each
    /// row left, the 31 included, is charged at the second longest exchange
    /// so far, as every exchange but one is: one stall is charged once, at
    /// its own length, and does not refuse a handback with seconds to spare,
    /// while a device slow throughout has two slow baseline exchanges by now.
    /// A cleanup that cannot finish by its deadline ends with no row and no
    /// setter, instead of leaving an unsettled 31 or 4x row.
    pub(super) fn covers_before_rows<P: Port, A>(
        reader: &Reader<P, A>,
        exchanges: u32,
        setters: u32,
        fixed: Duration,
        limit: Limit<'_>,
    ) -> bool {
        let measured = &reader.exchanges;
        let appends = 1 + APPENDS_PER_SETTER * setters + CLOSING_APPENDS;
        ends_by(measured, exchanges, appends, measured.second, fixed, limit)
    }
}
/// Whether `exchanges` at the measured pace, `appends` rows of `append` each
/// and `fixed` scheduled waits end before the deadline (`Pace`).
fn ends_by(
    measured: &Exchanges,
    exchanges: u32,
    appends: u32,
    append: Duration,
    fixed: Duration,
    limit: Limit<'_>,
) -> bool {
    measured
        .second
        .checked_mul(exchanges.saturating_sub(1))
        .and_then(|rest| rest.checked_add(measured.slowest))
        .and_then(|rest| rest.checked_add(append.checked_mul(appends)?))
        .and_then(|rest| rest.checked_add(fixed))
        .and_then(|rest| Instant::now().checked_add(rest))
        .is_some_and(|end| end < limit.deadline)
}

impl<P: Port> Reader<P, ReadAdmission> {
    pub(super) async fn apply_preset(
        &mut self,
        permit: &dyn Permit,
        limit: Limit<'_>,
    ) -> Option<ObservedPerformance> {
        self.exhausted = false;
        let observed = self.apply(permit, limit).await;
        // Any reservation may be the one that fails, the baseline
        // observation's included, so the cause is named here once.
        if observed.is_none() && self.exhausted {
            permit.refused(Refusal::TransactionCapacity);
        }
        // Taken after the operation, so the records it settled are included;
        // they are unpinned once this Reader has retired.
        self.settled.extend(permit.settled_pins());
        observed
    }

    async fn apply(
        &mut self,
        permit: &dyn Permit,
        limit: Limit<'_>,
    ) -> Option<ObservedPerformance> {
        // A 00/04 write runs its own recipe and reads no thermal state.
        #[cfg(feature = "service-keyboard-driver-mode-v1")]
        if let Some(job) = permit.device_mode() {
            self.apply_device_mode(permit, job, limit).await;
            return None;
        }
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
        let mut pace = Pace::start(self);
        let baseline = self.observe(&mut authorize, limit).await?;
        let initial_power_ids = self.reserve_pair()?;
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
        let restoring = match Plan::from_observation(baseline, destination) {
            Plan::Unchanged => return Some(baseline),
            #[cfg(not(feature = "service-thermal-hyperboost-exit-v1"))]
            Plan::NoDirectPath => {
                permit.refused(Refusal::NoDirectPath);
                return None;
            }
            #[cfg(feature = "service-thermal-hyperboost-exit-v1")]
            Plan::RestoreThenBattery { .. } => true,
            Plan::Direct { .. } => false,
        };
        // Managed `requireExternalPowerOnDirectHyperBoostExit`: a direct exit to
        // a non-Balanced preset needs every command's fresh reading in the same
        // classified external class, so a class change stops it without
        // replay. Managed's Balanced exit applies no class rule, and neither
        // does its restore sequence to a battery preset.
        if pins_class(baseline, destination) {
            fence.pin_class();
        }
        // Every setter, the restore pair's included, is admitted by the
        // destination's own class rule on its own fresh reading. Managed
        // classifies the adapter once for the restore sequence and checks no
        // later command: an unplug or a plug-in that still admits the battery
        // preset continues here as there, while an unclassified reading or a
        // Windows disagreement stops before the next command (stricter).
        fence.bind_destination(destination);
        // Reserve all remaining commands, including the independent final read
        // and its Custom level getters, before journaling/submitting.
        // Exhaustion cannot strand a partial recipe.
        let mut reserve = || self.reserve_pair();
        // Leaving HyperBoost for a battery preset: the restore pair, its two
        // power readings and the intermediate independent read come first.
        #[cfg(feature = "service-thermal-hyperboost-exit-v1")]
        let restore = match restoring {
            true => Some(Restore {
                pair: PresetPair::hyperboost_restore(reserve()?)?,
                power: reserve()?,
                read: reserve()?,
            }),
            false => None,
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
        // Each setter is one power read and its own exchange; the restore
        // pair's intermediate read, the final pair and any Custom levels
        // follow.
        let all_setters = destination.setters() as u32 + 2 * u32::from(restoring);
        let remaining =
            2 * all_setters + 2 + 2 * u32::from(level_ids.is_some()) + 2 * u32::from(restoring);
        let admission = self.admission;
        let mut attempt = Attempt {
            reader: self,
            completed: false,
        };
        attempt.checkpoint(&mut authorize, admission, limit).ok()?;
        pace.prepared(attempt.reader, || permit.record(Progress::Prepared))
            .ok()?;
        let context = SetterContext {
            permit,
            fence: &fence,
            pace: &pace,
            source: power_source,
            admission,
            limit,
            // Paced once, before the operation's first intent row (`Pace`).
            remaining: (remaining - EXCHANGES_BEFORE_CHECK, all_setters),
        };
        #[cfg(feature = "service-thermal-hyperboost-exit-v1")]
        if let Some(restore) = &restore {
            for (index, command) in restore.pair.commands().iter().enumerate() {
                let progress = Progress::restore_setter(index)?;
                let power_id = restore.power[index];
                let acknowledge = |reply: &[u8]| restore.pair.acknowledge(index, reply).ok();
                attempt
                    .submit_setter(
                        &context,
                        &mut authorize,
                        power_id,
                        command,
                        progress,
                        index == 0,
                        acknowledge,
                    )
                    .await?;
            }
            // Managed reads the full state before any battery setter and
            // requires Balanced with Automatic fans. Anything else, or no
            // reading, stops with the restore pair's 83 row unsettled: the
            // outcome is indeterminate, so no battery setter is sent and
            // nothing is replayed.
            let observed = attempt
                .read_reserved_pair(&mut authorize, admission, limit, restore.read, false)
                .await?;
            if !restored(observed) {
                return None;
            }
        }
        for (index, command) in setters.commands().enumerate() {
            let progress = Progress::setter(destination, index)?;
            let power_id = power_ids[index / 2][index % 2];
            let paced = index == 0 && !restoring;
            let acknowledge = |reply: &[u8]| setters.acknowledge(index, reply);
            attempt
                .submit_setter(
                    &context,
                    &mut authorize,
                    power_id,
                    command,
                    progress,
                    paced,
                    acknowledge,
                )
                .await?;
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
/// What every setter of one performance recipe shares.
struct SetterContext<'c> {
    permit: &'c dyn Permit,
    fence: &'c PowerFence<'c>,
    pace: &'c Pace,
    source: i32,
    admission: ReadAdmission,
    limit: Limit<'c>,
    /// The exchanges and setters left at the pace check.
    remaining: (u32, u32),
}
/// Managed's HyperBoost restore pair before a battery preset, with its
/// reserved power readings and intermediate independent read.
#[cfg(feature = "service-thermal-hyperboost-exit-v1")]
struct Restore {
    pair: PresetPair,
    power: [u8; 2],
    read: [u8; 2],
}
impl<P: Port> Attempt<'_, P, ReadAdmission> {
    /// One setter: its own fresh 07/8C reading, which the fence must admit,
    /// then its intent row, the send, the exact echo and its acknowledged
    /// row. `paced` checks the operation's pace before the intent row.
    #[allow(clippy::too_many_arguments)]
    async fn submit_setter(
        &mut self,
        context: &SetterContext<'_>,
        authorize: Authorize<'_, ReadAdmission>,
        power_id: u8,
        command: &RazerReport,
        (intent, acknowledged): (Progress, Progress),
        paced: bool,
        acknowledge: impl FnOnce(&[u8]) -> Option<()>,
    ) -> Option<()> {
        let (admission, limit, permit) = (context.admission, context.limit, context.permit);
        // This setter is admitted by its own reading, not by the source an
        // earlier reading carried (`PowerFence::release`).
        context.fence.release();
        self.checkpoint(authorize, admission, limit).ok()?;
        let Some(windows) = context.fence.before_query() else {
            permit.refused(Refusal::PowerChanged);
            return None;
        };
        let power = self
            .read_power(power_id, authorize, admission, limit)
            .await?;
        // Each command needs a fresh external reading from the exact decoder,
        // never ACLineStatus alone, that agrees with Windows AC presence.
        if !context.fence.admits(power, context.source, windows) {
            permit.refused(Refusal::PowerChanged);
            return None;
        }
        // A refusal here submitted nothing: retirement settles the Prepared
        // row as known not submitted. Retirement frees the IDs reserved
        // above that were never sent.
        let (exchanges, setters) = context.remaining;
        if paced
            && !context
                .pace
                .covers(self.reader, exchanges, setters, Duration::ZERO, limit)
        {
            self.completed = true;
            permit.refused(Refusal::Deadline);
            return None;
        }
        self.checkpoint(authorize, admission, limit).ok()?;
        permit.record(intent).ok()?;
        self.checkpoint(authorize, admission, limit).ok()?;
        let mut frame = [0; FRAME_LENGTH];
        frame[1..].copy_from_slice(&command.encode());
        self.transfer(Transfer::SendMutation(frame), limit)
            .await
            .ok()?;
        // Submitted: a power change from here on makes this setter no more
        // ambiguous, so it no longer stops the recipe (`PowerFence::release`).
        context.fence.release();
        limit.wait(SETTLE).await.ok()?;
        self.checkpoint(authorize, admission, limit).ok()?;
        let (length, frame) = self.transfer(Transfer::ReceiveReply, limit).await.ok()?;
        if !matches!(length, REPORT_LENGTH | FRAME_LENGTH) || frame[0] != 0 {
            return None;
        }
        acknowledge(&frame[1..])?;
        permit.record(acknowledged).ok()
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
/// battery (`allowBattery: true`). A battery preset's setters are also
/// admitted on battery, read with Windows battery
/// (`thermal_mutation::admits_class`). The Windows source a reading was
/// admitted with must hold at every later checkpoint until it is released
/// (managed `OrdinarySourceUnchanged` rechecks it at submission), so losing it
/// stops before the command is sent. A performance setter releases it once
/// its SET is sent (`release`): managed admits each command on its own fresh
/// reading, so the next setter's reading decides again. A fan operation
/// keeps it to its end. Each setter's reading is bracketed by Windows samples,
/// as managed brackets its query: one before the query that must admit the
/// command (`before_query`) and one after the reply that must equal it.
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
    /// through managed `SetPerformanceAsync`, which refuses Windows battery
    /// for its ordinary commands: the operation needs fresh Windows AC
    /// presence now and at every later reading and checkpoint. Managed admits
    /// a carried battery preset's selectors on Windows battery; native
    /// requires presence for it too (stricter, documented). False when
    /// presence is missing.
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
    /// Samples Windows before a setter's 07/8C query, as managed
    /// `AdmitOrdinaryThermalCommandAsync` captures its source first. None when
    /// that sample already refuses the command, which then stops before its
    /// query: an unknown kind, or Windows battery for any destination but a
    /// battery preset (`thermal_mutation::admits_windows`). Such a sample
    /// admits no reading after an unchanged reply either; refusing here sends
    /// no query that cannot admit the command, as managed throws before
    /// querying. Fan permits bind no destination; a fan setter keeps the
    /// presence it was admitted with (`authorize`), so its checkpoints refuse
    /// a lost presence as before.
    pub(super) fn before_query(&self) -> Option<WindowsSample> {
        let windows = self.permit.windows_on_battery();
        let admitted = match self.destination.get() {
            Some(destination) => admits_windows(*destination, windows),
            None => windows.is_some(),
        };
        admitted.then_some(WindowsSample(windows))
    }
    /// A setter-time reading against the first reading's class `source`,
    /// with the Windows kind sampled `before` its query.
    /// Managed admits each command on its own fresh reading: an external
    /// reading that agrees with Windows AC presence, or for a battery preset
    /// Windows battery (`AdmitOrdinaryThermalCommandAsync`). A class change
    /// between setters, among full, limited, unclassified and battery,
    /// therefore continues whenever the new reading still admits the
    /// destination. What stops, with its reason:
    /// - a Windows kind after the reply that differs from the one sampled
    ///   before the query (managed `capturePowerSource() != source`). A kind
    ///   that changes and returns while the query is in flight is seen by
    ///   neither side;
    /// - a reading the destination's class rule refuses: unclassified power
    ///   for a battery preset, battery for any other destination, or a
    ///   reading that disagrees with Windows (managed refuses that command).
    ///   It is decided before `destination_holds`, so such a reading never
    ///   becomes the current class and retires no temporary override
    ///   (`battery_reading_contradicts_windows`);
    /// - any change while the class is pinned (managed
    ///   `requireExternalPowerOnDirectHyperBoostExit` and the fan acPreset);
    /// - a change after which the permit's destination no longer holds: the
    ///   request was resolved for the first class, so a profile that differs
    ///   on the new class, or a temporary override that the change retired,
    ///   no longer stands (native deviation; managed resolves once).
    ///
    /// Fan permits bind no destination and admit no battery reading; their
    /// recipes start only on external power (`fan_recipe::Plan::select`).
    pub(super) fn admits(&self, power: Power, source: i32, before: WindowsSample) -> bool {
        let Some((current, windows)) = self.source(power) else {
            return false;
        };
        if windows != before.0 {
            return false;
        }
        let class_admitted = match self.destination.get() {
            Some(destination) => admits_class(*destination, current, windows),
            None => current != 1,
        };
        let admitted = class_admitted
            && (current == source
                || !self.pinned.load(Ordering::Acquire) && self.permit.destination_holds(current));
        if admitted {
            self.external
                .store(windows == Some(false) && current != 1, Ordering::Release);
            self.battery.store(current == 1, Ordering::Release);
        }
        admitted
    }
    /// Ends the Windows source a performance setter was admitted with, once
    /// its SET is sent and again before the next setter's own reading.
    /// Managed checks it only between a command's admission and its
    /// submission (`OrdinarySourceUnchanged`), so a change after a submitted
    /// setter stops nothing by itself: its reply is still read, and the
    /// independent reads after the last setter confirm what firmware holds,
    /// as managed's do. The next setter is not admitted on the released
    /// source: its Windows sample before the query must admit the command,
    /// and its reading must agree with an unchanged sample after the reply
    /// (`before_query`, `admits`), as managed brackets each query.
    pub(super) fn release(&self) {
        self.external.store(false, Ordering::Release);
        self.battery.store(false, Ordering::Release);
    }
}
/// The Windows power kind sampled before a setter's 07/8C query
/// (`PowerFence::before_query`), which its reading must still see after the
/// reply (`PowerFence::admits`).
#[derive(Clone, Copy)]
pub(super) struct WindowsSample(Option<bool>);
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

/// Quarantines `ids` in the pool every native worker shares
/// (`Transactions::quarantine`).
#[cfg(windows)]
pub(crate) fn quarantine_native(ids: &[u8]) {
    super::windows::quarantine(ids);
}

/// Whether `quarantine` withholds a free ID of the pool every native worker
/// shares.
#[cfg(all(test, windows))]
pub(crate) fn quarantines_native_pool(quarantine: &dyn Fn(&[u8])) -> bool {
    super::windows::quarantines_process_pool(quarantine)
}

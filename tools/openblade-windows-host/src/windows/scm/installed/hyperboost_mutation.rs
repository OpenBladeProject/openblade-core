//! Owned HyperBoost on exact RZ09-0581 (`setHyperBoost`), after managed
//! `ServiceControlRuntime.SetHyperBoostCoreAsync` (`ServiceControlRuntime.cs`
//! 6840-7857) and `ProductionBatteryPort.SetHyperBoostCoreAsync`
//! (`ProductionBatteryPort.cs` 869-940).
//!
//! A request is one performance step under the thermal gate
//! (`Runtime::apply_locked`): the same caller, custody, fresh exact-device and
//! controller admission, durable journal, per-setter power admission, GPU
//! thermal fence (`Permit::thermal_fenced`), independent readback and
//! stop-on-ambiguity as every native thermal write. It stores no intent.
//!
//! - Entry writes the `hyperboost-enable` pair and settles only on an
//!   independent read of HyperBoost with Automatic fans. Only the pad-less path
//!   is admitted: the cooling pad is observed read-only by its hardware IDs
//!   before the request and before each enable setter, and any pad or failed
//!   observation refuses the entry. The pad-present path needs an interface
//!   contract that is not reviewed for native use.
//! - Exit writes the saved performance destination at the fresh class directly
//!   (`Plan::Direct`), or through managed's restore pair for a battery preset
//!   (`Plan::RestoreThenBattery`). Managed first writes its owned base and then
//!   restores the saved intent; on RZ09-0581 every saved base is a direct exit
//!   (`CanExitHyperBoostDirectlyTo`), so native writes the destination once.
//!   A saved manual fan intent is left to the fan writer.
//! - An exit is submitted even when the saved intent cannot be restored, as
//!   managed's is (`restoreIntentSkipped`, 7118-7127): a saved GPU clock
//!   effect this exit cannot restore, or no saved destination at the class.
//!   It then writes managed's unowned exit,
//!   the Balanced restore pair (`ownedHyperBoostBaseMode ?? Balanced`, 7066;
//!   `hyperboost-restore-balanced` has the Balanced pair's payloads), settles on
//!   Balanced with Automatic fans, leaves the saved intent unchanged and says
//!   what it did not apply (`Skipped`).
//! - Managed's HyperBoost ownership journal is read before the request and
//!   before each setter. Native writes nothing on managed's behalf, so a journal
//!   managed has not relinquished, or one that cannot be read, refuses both.
//!
//! Native ownership is the thermal history's (`thermal_history::hyperboost_owned`,
//! `Storage::owns_hyperboost`): a settled HyperBoost entry with no later
//! confirmed exit or HyperBoost retirement. With the lifecycle triggers, an
//! owned session leaves at the battery boundary of a reconciliation (`Exit`),
//! and at the coordinated Stop and a confirmed suspend under the Service's
//! cleanup admission (`cleanup`); without them no probe is composed, so
//! nothing enters. There is no display-off exit (see "Owned HyperBoost" in
//! docs/windows-native-thermal-mutation.md).
use super::*;
use openblade_domain::thermal_observation::ObservedPerformance;

#[cfg(feature = "service-profile-triggers-v1")]
#[path = "hyperboost_cleanup.rs"]
pub(super) mod cleanup;

/// The cooling pad's USB identity (managed `HyperBoostEligibilitySource`,
/// 1532:0F43), as Windows names it in an interface's hardware IDs.
#[cfg(all(not(test), feature = "service-profile-triggers-v1"))]
pub(super) const PAD_HARDWARE_ID: &str = "VID_1532&PID_0F43";

/// Managed `SetHyperBoostCoreAsync` replies, literally.
pub(super) const ALREADY_ACTIVE: &str = "HyperBoost is already active.";
pub(super) const ALREADY_OFF: &str = "HyperBoost is already off.";
pub(super) const UNAVAILABLE: &str = "HyperBoost control is unavailable right now.";
pub(super) const ENTERED: &str = "HyperBoost is active through the admitted pad-less path; no cooling pad is required for this owned session.";
pub(super) const EXTERNAL_POWER: &str = "HyperBoost requires a confirmed external power source.";
pub(super) const INTENT_NOT_ADMITTED: &str = "The current external-power performance or fan intent is not admitted for this verified device.";
pub(super) const PAD_ABSENCE: &str = "Pad-less HyperBoost requires a fresh observation confirming that no target cooling-pad device is present. Ambiguous or failed enumeration is blocked.";
/// Managed `ProductionBatteryPort.SetHyperBoostCoreAsync`'s entry precondition
/// message for the pad-less base (`PadlessEntryBaseMode`, Balanced), which a
/// battery preset with Automatic fans reaches.
pub(super) const ENTRY_BASE: &str = "HyperBoost requires confirmed Balanced performance with Automatic fans, or admitted direct entry from manual fan control.";
/// Native replies with no managed counterpart.
pub(super) const PAD_PRESENT: &str = "HyperBoost with the cooling pad connected is not available in this build. Disconnect the cooling pad to use pad-less HyperBoost.";
pub(super) const JOURNAL_PENDING: &str = "HyperBoost recovery from the previous OpenBlade service is pending. Start that service once so it can finish, then try again.";
/// An exit refused while a fan change this Service owns is outstanding. A
/// HyperBoost reading reports Automatic fans, so owned Stop closes that
/// change without a setter, and the exit is admitted after a restart.
pub(super) const FAN_HANDBACK_EXIT: &str = "HyperBoost was not turned off because OpenBlade still owns an earlier fan change. The fans are already on firmware Automatic. Restart OpenBlade to hand them back, then turn HyperBoost off.";

/// A read-only observation of the cooling pad.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(in super::super) enum Pad {
    Absent,
    Present,
    // Observed only by the native probe, which the lifecycle triggers compose.
    #[cfg_attr(not(feature = "service-profile-triggers-v1"), allow(dead_code))]
    ObservationFailed,
}

/// The read-only inputs of owned HyperBoost. The native composition reads
/// Windows; tests inject both.
#[derive(Clone)]
pub(in super::super) struct Probes {
    pad: Arc<dyn Fn() -> Pad + Send + Sync>,
    journal: hyperboost_journal::Journal,
}
impl Probes {
    // Without the lifecycle triggers only tests compose probes.
    #[cfg_attr(not(feature = "service-profile-triggers-v1"), allow(dead_code))]
    pub(in super::super) fn new(
        pad: impl Fn() -> Pad + Send + Sync + 'static,
        journal: hyperboost_journal::Journal,
    ) -> Self {
        Self {
            pad: Arc::new(pad),
            journal,
        }
    }
    #[cfg(all(not(test), feature = "service-profile-triggers-v1"))]
    pub(super) fn native() -> Self {
        Self::new(observe_pad, Arc::new(hyperboost_journal::read))
    }
    /// Whether managed's journal admits a native HyperBoost write now:
    /// absent, or recognized and Relinquished (`journal_safe`). A read that
    /// fails admits nothing.
    fn journal_safe(&self) -> bool {
        (self.journal)().is_ok_and(|bytes| hyperboost_journal::journal_safe(bytes.as_deref()))
    }
}
/// The present HID interfaces' hardware IDs, read without opening any
/// interface (`hid_hardware_id_present`). Managed opens each interface with
/// zero access and matches 1532:0F43 by its attributes; native admits absence
/// only when no hardware ID names the pad and every ID was read.
#[cfg(all(not(test), feature = "service-profile-triggers-v1"))]
fn observe_pad() -> Pad {
    match crate::discovery::windows::hid_hardware_id_present(
        &std::sync::atomic::AtomicBool::new(false),
        PAD_HARDWARE_ID,
    ) {
        Ok(false) => Pad::Absent,
        Ok(true) => Pad::Present,
        Err(_) => Pad::ObservationFailed,
    }
}

/// Why an exit wrote managed's Balanced restore and left the saved intent
/// unapplied (managed `restoreIntentSkipped`).
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(super) enum Skipped {
    /// The saved intent carries a GPU clock effect this step cannot restore
    /// (`Cause::Gpu`).
    Gpu,
    /// No saved destination at this class (`Cause::NoEligiblePolicy`).
    NoPolicy,
}
impl Skipped {
    /// The saved-intent cause `select` blocked the destination with, when
    /// the exit skips the restore for it. None for every other cause, which
    /// still refuses the exit. An outstanding owned fan change never gets
    /// here: the exit refuses it before `select` (`resolve_hyperboost`).
    /// `Cause::ManualDeferred` never reaches an exit either: HyperBoost reads
    /// with Automatic fans (`defer_manual_performance`).
    fn from_cause(cause: &Cause) -> Option<Self> {
        match cause {
            Cause::Gpu => Some(Self::Gpu),
            Cause::NoEligiblePolicy => Some(Self::NoPolicy),
            _ => None,
        }
    }
    /// The reply, after managed's skipped-restore reply (7833-7835): the
    /// laptop stays in Balanced, and the saved intent was not applied, with
    /// native's own reason. The reason is the one found at the class the exit
    /// resolved: a classified class change between its setters keeps the exit
    /// (`Permit::restores_only`) and its reason, as managed resolves its
    /// restore intent and reply once.
    fn reply(self, saved: Option<Destination>) -> String {
        let saved = saved.map_or_else(|| "saved".to_owned(), |d| format!("saved {}", label(d)));
        let reason = match self {
            Self::Gpu => format!(
                "The {saved} setting includes GPU clock tuning that this exit can't restore, so it was not applied."
            ),
            Self::NoPolicy => {
                "No saved performance setting applies to this power source, so none was applied."
                    .to_owned()
            }
        };
        format!(
            "HyperBoost is off and the laptop stays in Balanced until the power source changes. {reason}"
        )
    }
}
/// Managed's unowned exit destination (`ownedHyperBoostBaseMode ?? Balanced`).
const RESTORE_BASE: Destination =
    Destination::Preset(openblade_domain::thermal_mutation::Preset::Balanced);

/// One HyperBoost request, carried by its permit.
pub(super) struct Step {
    pub(super) enable: bool,
    probes: Probes,
    /// The saved destination at the fresh reading's class, which the status
    /// projects as desired: neither an entry nor a restore-only exit
    /// changes the saved intent.
    saved: Mutex<Option<Destination>>,
    /// Set when the exit writes managed's Balanced restore only.
    skipped: Mutex<Option<Skipped>>,
    /// The fresh reading an exit found already out of HyperBoost, from which
    /// a lifecycle exit settles ownership read-only (`Exited`).
    exited: Exited,
}
/// Where an exit leaves the fresh reading that found firmware already out of
/// HyperBoost (managed `RestoreFirmwareExitedHyperBoostForPowerSourceTransitionAsync`):
/// nothing is written to firmware, and the lifecycle exit that kept this slot
/// marks the reading OpenBlade's own and settles ownership in the journal.
pub(super) type Exited = Arc<Mutex<Option<ObservedPerformance>>>;
impl Step {
    pub(super) fn new(enable: bool, probes: Probes) -> Self {
        Self {
            enable,
            probes,
            saved: Mutex::new(None),
            skipped: Mutex::new(None),
            exited: Exited::default(),
        }
    }
    /// A lifecycle exit, with the `Exited` slot its caller keeps.
    #[cfg(feature = "service-profile-triggers-v1")]
    pub(super) fn lifecycle(probes: Probes) -> (Self, Exited) {
        let step = Self::new(false, probes);
        let exited = step.exited.clone();
        (step, exited)
    }
    pub(super) fn saved(&self) -> io::Result<Option<Destination>> {
        self.saved
            .lock()
            .map(|saved| *saved)
            .map_err(|_| io::ErrorKind::Other.into())
    }
    fn skipped(&self) -> io::Result<Option<Skipped>> {
        self.skipped
            .lock()
            .map(|skipped| *skipped)
            .map_err(|_| io::ErrorKind::Other.into())
    }
    /// Whether this exit writes managed's Balanced restore without the saved
    /// intent (`Permit::restores_only`). A poisoned lock reads as false, so
    /// the ordinary class rule applies.
    pub(super) fn restores_only(&self) -> bool {
        self.skipped().ok().flatten().is_some()
    }
}

/// Whether managed admits a direct pad-less entry from this confirmed state on
/// exact RZ09-0581 (`SetHyperBoostCoreAsync` 7044-7084 with
/// `SupportsDirectManualFanTransitions`, and the port's entry precondition,
/// `ProductionBatteryPort.cs` 914-922): Silent, Balanced, Performance and
/// Custom with Automatic fans enter from themselves (all four are ReadWrite
/// bases, `HyperBoostBaseModeAdmissionSource`), and a manual-fan state enters
/// directly (`directManualEntry && Fans == Maximum`; this getter projects every
/// manual state as Maximum, 3). A battery preset with Automatic fans falls back
/// to the Balanced base, which the confirmed state does not match.
pub(super) fn entry_base_admitted(baseline: ObservedPerformance) -> bool {
    use ObservedPerformance as O;
    match baseline {
        O::SilentAutomatic | O::BalancedAutomatic | O::PerformanceAutomatic => true,
        O::Custom { manual: false, .. } => true,
        O::BatterySaverAutomatic | O::BatteryBalancedAutomatic | O::HyperBoostObserved => false,
        manual => manual.projection().1 == 3,
    }
}

/// Managed `PerformanceLabel` (`ServiceControlRuntime.cs` 22976-22988) for
/// a destination an exit restores.
fn label(destination: Destination) -> &'static str {
    use openblade_domain::thermal_mutation::Preset;
    match destination {
        Destination::Preset(Preset::Silent) => "Quiet",
        Destination::Preset(Preset::Balanced) => "Balanced",
        Destination::Preset(Preset::Performance) => "Performance",
        Destination::Preset(Preset::BatterySaver) => "Battery Saver",
        Destination::Preset(Preset::BatteryBalanced) => "Battery Balanced",
        Destination::Custom(_) => "Custom",
        Destination::HyperBoost => "HyperBoost",
    }
}

impl RequestPermit {
    /// `resolve` for a HyperBoost step, after the shared power checks. Each
    /// gate cites the managed line it ports; a refusal sets its cause and
    /// stores and writes nothing.
    pub(super) fn resolve_hyperboost(
        &self,
        step: &Step,
        power: i32,
        baseline: ObservedPerformance,
    ) -> io::Result<Option<Destination>> {
        let refuse = |message: &'static str, status: u8| {
            self.set_cause(Cause::HyperBoost { message, status });
            Ok(None)
        };
        // Native only: managed owns the recovery of its own journal
        // (`ReconcileUnexpectedExitAsync`, 11456-11648), and native makes no
        // write on its behalf, so a journal managed has not relinquished
        // refuses entry and exit alike.
        if !step.probes.journal_safe() {
            return refuse(JOURNAL_PENDING, 2);
        }
        let active = baseline == ObservedPerformance::HyperBoostObserved;
        // 6965-7002: already in the requested state. Nothing is read from the
        // saved intent and nothing is written. Native deviation in order:
        // managed's WritesBlocked refusal (6943-6952), which includes an
        // unconfirmed write, comes first; native replies here before its GPU
        // fence, so a pending GPU confirmation does not turn this reply into
        // a refusal. Neither side writes.
        if active == step.enable {
            if !active {
                *step.exited.lock().map_err(|_| io::ErrorKind::Other)? = Some(baseline);
            }
            return refuse(if active { ALREADY_ACTIVE } else { ALREADY_OFF }, 1);
        }
        let layer = self.storage.profile_layer(&self.application)?;
        let profile = layer.at(power)?;
        if !step.enable {
            // Exit: the saved destination at this fresh class, as managed's
            // exit restores the saved intent (`ResolveHyperBoostExitIntent`).
            // Unclassified power already refused (managed's exit needs a
            // classified source). A battery class is a battery preset's restore
            // sequence (`Plan::RestoreThenBattery`).
            //
            // Native only, before any saved intent is resolved: the durable
            // thermal journal admits no performance row while a fan change this
            // Service owns is outstanding (`thermal_history` refuses it on
            // read), so an exit row, restore or skipped, would leave the journal
            // unreadable and owned Stop unable to hand the fans back. Checked
            // here rather than through `select`, whose saved-intent causes come
            // first. Managed has no such journal.
            if self.storage.outstanding_fan() {
                return refuse(FAN_HANDBACK_EXIT, 2);
            }
            let selection = select(&self.storage, power, profile.as_ref(), baseline, false)?;
            let Some(cause) = selection.blocked else {
                let Some(desired) = selection.desired else {
                    // `select` names its cause whenever it selects nothing.
                    return Ok(None);
                };
                self.accept(power, step, baseline, desired.0, Some(desired))?;
                return Ok(Some(desired.0));
            };
            // 7118-7127: only an entry needs an admitted intent. An exit the
            // saved intent cannot complete still leaves HyperBoost through
            // managed's unowned exit, the Balanced restore, and stores nothing.
            // Every admission but the saved intent's still applies: journal,
            // GPU fence, custody, device, power class and readback
            // (`Skipped::from_cause`).
            let Some(skipped) = Skipped::from_cause(&cause) else {
                self.set_cause(cause);
                return Ok(None);
            };
            let saved = self.storage.desired(power, profile.as_ref())?;
            *step.skipped.lock().map_err(|_| io::ErrorKind::Other)? = Some(skipped);
            self.accept(power, step, baseline, RESTORE_BASE, saved)?;
            return Ok(Some(RESTORE_BASE));
        }
        // 7004-7023: a fresh pad observation selects the path. Native admits
        // only the pad-less one, so a pad refuses here; a failed observation
        // selects pad-less, whose absence check (7142-7159) refuses below.
        let pad = (step.probes.pad)();
        if pad == Pad::Present {
            return refuse(PAD_PRESENT, 2);
        }
        // 7024-7032 (a fresh confirmed state) holds for every reading the
        // Reader decodes. 7044-7084 select the base; its refusal is the
        // port's, checked last below.
        // Managed resolves an unclassified external source's restore intent
        // and then refuses the source at 7134-7140. Native's saved layer has
        // no destination for that class, so the same refusal comes first.
        if power == openblade_domain::automation::UNCLASSIFIED_EXTERNAL {
            return refuse(EXTERNAL_POWER, 2);
        }
        // 7121-7132: the saved intent this class restores must be admitted.
        // An owned fan change without its handback, or a GPU clock effect the
        // exit could not restore, is not.
        if self.storage.outstanding_fan() {
            self.set_cause(Cause::FanHandbackPending);
            return Ok(None);
        }
        let Some(saved) = self.storage.desired(power, profile.as_ref())? else {
            return refuse(INTENT_NOT_ADMITTED, 2);
        };
        if self.storage.gpu_incomplete(power, profile.as_ref())? {
            return refuse(INTENT_NOT_ADMITTED, 2);
        }
        // 7134-7140: `PowerSourcePolicy.IsExternal`, a classified external
        // adapter with fresh Windows AC presence. Each enable setter is
        // admitted again on its own reading (`admits_class`).
        if !openblade_domain::thermal_mutation::admits_class(
            Destination::HyperBoost,
            power,
            Permit::windows_on_battery(self),
        ) {
            return refuse(EXTERNAL_POWER, 2);
        }
        // 7142-7159: fresh pad absence.
        if pad != Pad::Absent {
            return refuse(PAD_ABSENCE, 2);
        }
        // The port's entry precondition (`ProductionBatteryPort.cs` 914-922),
        // which managed reaches only after every runtime gate above: a battery
        // preset with Automatic fans is refused here on external power, and
        // reports external power on battery.
        if !entry_base_admitted(baseline) {
            return refuse(ENTRY_BASE, 2);
        }
        self.accept(power, step, baseline, Destination::HyperBoost, Some(saved))?;
        Ok(Some(Destination::HyperBoost))
    }
    /// The step is admitted toward `destination`: its rows may be written,
    /// and a Prepared-only retirement settles them (`retired`). `saved` is
    /// the saved destination at this class with its provenance, which the
    /// status projects as desired.
    fn accept(
        &self,
        power: i32,
        step: &Step,
        baseline: ObservedPerformance,
        destination: Destination,
        saved: Option<(Destination, i32)>,
    ) -> io::Result<()> {
        // The override document the saved intent resolves over is checked as
        // a reconciliation checks it: nothing is stored.
        self.storage
            .verify_overrides()
            .inspect_err(|e| self.set_cause(Cause::Storage(e.to_string())))?;
        *step.saved.lock().map_err(|_| io::ErrorKind::Other)? = saved.map(|(d, _)| d);
        *self.destination.lock().map_err(|_| io::ErrorKind::Other)? = Some(destination);
        *self
            .selected_source
            .lock()
            .map_err(|_| io::ErrorKind::Other)? = saved.map(|(_, source)| source);
        // The drift ledger's starting point for a stopped setter (`record`).
        #[cfg(feature = "service-profile-triggers-v1")]
        {
            *self.baseline.lock().map_err(|_| io::ErrorKind::Other)? = Some(baseline);
        }
        #[cfg(not(feature = "service-profile-triggers-v1"))]
        let _ = baseline;
        self.accepted.store(true, Ordering::Release);
        *self.power.lock().map_err(|_| io::ErrorKind::Other)? = Some(power);
        Ok(())
    }
    /// Managed's per-command revalidation of the enable pair
    /// (`revalidateEligibilityPerCommand`, `ApplyHyperBoostCommandOnceAsync`
    /// 6806-6812): the pad must still be absent. Managed's journal is read
    /// again before every setter of either direction.
    pub(super) fn hyperboost_admits_setter(&self, step: &Step) -> bool {
        if !step.probes.journal_safe() {
            self.set_cause(Cause::HyperBoost {
                message: JOURNAL_PENDING,
                status: 2,
            });
            return false;
        }
        if step.enable && (step.probes.pad)() != Pad::Absent {
            self.set_cause(Cause::HyperBoost {
                message: PAD_ABSENCE,
                status: 2,
            });
            return false;
        }
        true
    }
    /// The reply of a HyperBoost step. `confirmed` is the independent read
    /// of the step's destination.
    pub(super) fn hyperboost_outcome(
        &self,
        step: &Step,
        confirmed: bool,
        destination: Option<Destination>,
    ) -> io::Result<Outcome> {
        if confirmed {
            let skipped = step.skipped()?;
            let message = match (step.enable, destination, skipped) {
                (true, _, _) => ENTERED.into(),
                (false, Some(_), Some(skipped)) => skipped.reply(step.saved()?),
                // Managed's reply names the restored performance and fan
                // intent; native restores the performance destination only.
                (false, Some(destination), None) => format!(
                    "HyperBoost is off and the prior {} performance is restored.",
                    label(destination)
                ),
                (false, None, _) => return Err(io::ErrorKind::Other.into()),
            };
            return Ok(Outcome { status: 1, message });
        }
        // A setter may have been submitted: its row stays unsettled and
        // fences writes until a read-only reconciliation, and nothing is
        // retried.
        if self.storage.pending() {
            return Ok(Outcome::blocked(self.storage.pending_reason()?));
        }
        let cause = self.cause.lock().map_err(|_| io::ErrorKind::Other)?;
        Ok(match &*cause {
            Cause::HyperBoost { message, status } => Outcome {
                status: *status,
                message: (*message).into(),
            },
            Cause::UnclassifiedBlocked => Outcome::blocked(UNCLASSIFIED_BLOCKED),
            cause => Outcome::blocked(cause.message()),
        })
    }
}

/// One lifecycle exit of owned HyperBoost (`Runtime::exit_owned_hyperboost_locked`):
/// the trigger and the user whose saved intent the exit restores.
#[cfg(feature = "service-profile-triggers-v1")]
pub(super) struct Exit<'a> {
    pub(super) caller: Caller,
    pub(super) sid: &'a str,
    /// The trigger's id, from which the exit's journal id is derived
    /// (`triggers::step_id`).
    pub(super) request: &'a str,
    pub(super) storage: &'a Storage,
    pub(super) deadline: Instant,
    /// The exit is dropped here, as a reconciliation step is at its bound.
    pub(super) bound: Instant,
    pub(super) status: Snapshot,
}
/// What one lifecycle exit did.
#[cfg(feature = "service-profile-triggers-v1")]
pub(super) struct LifecycleExit {
    pub(super) stepped: Stepped,
    /// A setter of it may have been submitted (`triggers::submission_possible`).
    pub(super) possible: bool,
    /// HyperBoost is off by the exit's own readback, or firmware had already
    /// left it and ownership settled read-only (`Stepped::unchanged`).
    pub(super) settled: bool,
    /// It had not finished at its bound and was dropped.
    pub(super) unfinished: bool,
}
/// The journal step name of a lifecycle exit (`triggers::step_id`).
#[cfg(feature = "service-profile-triggers-v1")]
const EXIT_STEP: &str = "hyperboost";

#[cfg(feature = "service-profile-triggers-v1")]
impl Runtime {
    /// Managed's exit of owned HyperBoost at the battery boundary of a
    /// reconciliation (`ExitOwnedHyperBoostForPowerSourceTransitionAsync`),
    /// under the held gate. `None`
    /// when there is nothing to exit: no HyperBoost probes, or native does
    /// not own HyperBoost (`Runtime::owns_hyperboost`), so a HyperBoost
    /// reading stays external and is excluded as before.
    ///
    /// Otherwise one H2 exit step (`Step::lifecycle`) with every admission of
    /// an explicit exit: fresh exact-device, controller, custody, journal,
    /// managed's ownership journal, GPU fence, power class and readback, and
    /// the saved destination at the fresh class (`Plan::RestoreThenBattery`
    /// on battery, or managed's Balanced restore when the saved intent cannot
    /// be restored). Nothing is retried: a step that may have submitted
    /// leaves its row unsettled, which fences the store.
    ///
    /// When the exit's fresh reading shows firmware already out of
    /// HyperBoost, nothing is written to firmware. As managed
    /// `RestoreFirmwareExitedHyperBoostForPowerSourceTransitionAsync` does,
    /// the reading becomes OpenBlade's own (`ConfirmIntentional` for
    /// Performance and Fans), so the transition after it applies the saved
    /// intent instead of pausing on drift, and ownership settles in the
    /// journal (`settle_exited`).
    pub(super) async fn exit_owned_hyperboost_locked(
        &self,
        mailbox: &MailboxSource,
        exit: Exit<'_>,
    ) -> io::Result<Option<LifecycleExit>> {
        let Exit {
            caller,
            sid,
            request,
            storage,
            deadline,
            bound,
            status,
        } = exit;
        let Some(probes) = self.hyperboost.get().cloned() else {
            return Ok(None);
        };
        if !storage.owns_hyperboost() {
            return Ok(None);
        }
        let id = triggers::step_id(request, EXIT_STEP);
        // An observation since the entry showed firmware out of HyperBoost,
        // so nothing here is OpenBlade's: whatever firmware shows now stays
        // external and is neither exited nor adopted. Only the journal is
        // settled, at this first admitted write since.
        if self.left_hyperboost() {
            self.settle_ownership(mailbox, caller, sid, &id)?;
            return Ok(None);
        }
        let (step, exited) = Step::lifecycle(probes);
        let run = self.apply_locked(
            mailbox.clone(),
            ThermalStep {
                caller,
                sid: sid.to_owned(),
                request: id.clone(),
                value: None,
                reconcile: None,
                transition: None,
                #[cfg(feature = "service-performance-shortcut-v1")]
                shortcut: None,
                hyperboost: Some(step),
                deadline,
                status,
            },
        );
        let stepped = tokio::time::timeout_at(bound, run).await;
        let possible = triggers::submission_possible(storage, &id);
        let Ok(stepped) = stepped else {
            // Dropped at its bound: the Reader keeps the worker, and a row
            // past its intent stays unsettled, as for any reconciliation step.
            let outcome = if possible {
                Outcome {
                    status: 3,
                    message: triggers::UNFINISHED.into(),
                }
            } else {
                Outcome::blocked(triggers::UNFINISHED_UNSUBMITTED)
            };
            return Ok(Some(LifecycleExit {
                stepped: Stepped {
                    transient: !possible,
                    ..Stepped::new(outcome, None)
                },
                possible,
                settled: false,
                unfinished: true,
            }));
        };
        let mut stepped = stepped?;
        let reading = *exited.lock().map_err(|_| io::ErrorKind::Other)?;
        let settled = match (stepped.outcome.status, reading) {
            // Firmware had left: the reading ends the ownership whether or
            // not the journal can record it now, so the transition goes on
            // and its own steps name whatever refuses them.
            (1, Some(reading)) => {
                stepped.unchanged = true;
                self.settle_exited(mailbox, caller, sid, &id, reading)?;
                true
            }
            (status, _) => status == 1,
        };
        Ok(Some(LifecycleExit {
            stepped,
            possible,
            settled,
            unfinished: false,
        }))
    }

    /// The read-only settlement of an exit whose fresh reading found
    /// firmware already out of HyperBoost, with no earlier observation
    /// against the entry: the reading is OpenBlade's own (managed
    /// `driftTracker.Observe` then `ConfirmIntentional` for Performance and
    /// Fans), which also ends the ownership in memory
    /// (`Tracker::left_hyperboost`), and the journal records it
    /// (`settle_ownership`).
    fn settle_exited(
        &self,
        mailbox: &MailboxSource,
        caller: Caller,
        sid: &str,
        id: &str,
        reading: ObservedPerformance,
    ) -> io::Result<bool> {
        self.drift
            .lock()
            .map_err(|_| io::ErrorKind::Other)?
            .own_reading(reading);
        self.settle_ownership(mailbox, caller, sid, id)
    }

    /// Whether a reading since the last confirmed entry showed firmware out
    /// of HyperBoost (`Tracker::left_hyperboost`). A poisoned tracker reads
    /// as left: a missed exit is recoverable, and exiting a HyperBoost that
    /// is not OpenBlade's is not.
    pub(super) fn left_hyperboost(&self) -> bool {
        self.drift
            .lock()
            .map_or(true, |drift| drift.left_hyperboost())
    }

    /// Whether native owns the HyperBoost a lifecycle exit may leave: the
    /// history says so (`Storage::owns_hyperboost`) and no reading since
    /// the entry contradicted it.
    pub(super) fn owns_hyperboost(&self, storage: &Storage) -> bool {
        storage.owns_hyperboost() && !self.left_hyperboost()
    }

    /// Records in the journal that an ownership the history still shows has
    /// ended with no firmware write: the HyperBoost row of `id` Prepared and
    /// KnownNotSubmitted (`Storage::settle_hyperboost`) under a fresh
    /// capture's write authorization. Says whether it was recorded. A
    /// refused capture, an unsettled row or an outstanding owned fan change
    /// leaves the journal as it was, for the next admitted exit to settle,
    /// while the tracker keeps the ownership ended for this process; a
    /// failed append fences the store as every append does.
    fn settle_ownership(
        &self,
        mailbox: &MailboxSource,
        caller: Caller,
        sid: &str,
        id: &str,
    ) -> io::Result<bool> {
        let MailboxSource::Reader(reader) = mailbox else {
            return Ok(false);
        };
        let (storage, fresh) = match self.capture(sid, caller.session, reader.custody()) {
            Ok(captured) => captured,
            Err(error) if is_fatal_admission(&error) => return Err(error),
            Err(_) => return Ok(false),
        };
        if !storage.owns_hyperboost() || storage.pending() || storage.outstanding_fan() {
            return Ok(false);
        }
        fresh.verify_health()?;
        let proof = fresh.authorize();
        fresh.verify_health()?;
        let (Some(proof), Some(pid)) = (proof, caller.pid) else {
            return Ok(false);
        };
        let owner = format!("{:x}", Sha256::digest(proof.generation));
        match storage.settle_hyperboost(id, &owner, pid, caller.session, None) {
            Ok(()) => Ok(true),
            Err(error) if is_fatal_admission(&error) => Err(error),
            Err(_) => Ok(false),
        }
    }
}

impl Runtime {
    /// Managed `SetHyperBoostAsync`: one owned entry or exit, a performance
    /// step under the same gate, admission, journal, readback and
    /// stop-on-ambiguity as `apply`. Without its probes nothing is read or
    /// written (managed 6943-6952).
    #[allow(clippy::too_many_arguments)]
    pub(in super::super) async fn set_hyperboost(
        &self,
        mailbox: MailboxSource,
        caller: Caller,
        sid: String,
        request: String,
        enabled: bool,
        deadline: Instant,
        status: Snapshot,
    ) -> io::Result<(Outcome, Option<Snapshot>)> {
        #[cfg(test)]
        self.entered.set(self.entered.get() + 1);
        let Some(probes) = self.hyperboost.get().cloned() else {
            return Ok((Outcome::blocked(UNAVAILABLE), None));
        };
        let _serial = self.gate.lock().await;
        let step = Step::new(enabled, probes);
        #[cfg(feature = "service-profile-triggers-v1")]
        let (exited, settle) = (
            step.exited.clone(),
            (mailbox.clone(), sid.clone(), request.clone()),
        );
        let stepped = self
            .apply_locked(
                mailbox,
                ThermalStep {
                    caller,
                    sid,
                    request,
                    value: None,
                    reconcile: None,
                    #[cfg(feature = "service-profile-triggers-v1")]
                    transition: None,
                    #[cfg(feature = "service-performance-shortcut-v1")]
                    shortcut: None,
                    hyperboost: Some(step),
                    deadline,
                    status,
                },
            )
            .await?;
        // An explicit exit that found HyperBoost already off ends native
        // ownership, as managed's clears `ownsHyperBoost` and retires its
        // journal there (6965-6994): a HyperBoost entered outside OpenBlade
        // afterwards is not left by a lifecycle exit. The journal records it
        // when this request's capture admits the write.
        #[cfg(feature = "service-profile-triggers-v1")]
        if exited.lock().map_err(|_| io::ErrorKind::Other)?.is_some() {
            self.drift
                .lock()
                .map_err(|_| io::ErrorKind::Other)?
                .leave_hyperboost();
            let (mailbox, sid, request) = settle;
            let id = triggers::step_id(&request, EXIT_STEP);
            self.settle_ownership(&mailbox, caller, &sid, &id)?;
        }
        Ok(stepped.reply())
    }
}

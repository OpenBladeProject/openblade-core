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
//! - Managed's HyperBoost ownership journal is read before the request and
//!   before each setter. Native writes nothing on managed's behalf, so a journal
//!   managed has not relinquished, or one that cannot be read, refuses both.
//!
//! Native records no ownership of its own beyond the journal rows: lifecycle
//! exits (battery boundary, low power, shutdown) are not part of this slice.
use super::*;
use openblade_domain::thermal_observation::ObservedPerformance;

/// The cooling pad's USB identity (managed `HyperBoostEligibilitySource`,
/// 1532:0F43), as Windows names it in an interface's hardware IDs.
#[cfg(not(test))]
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

/// A read-only observation of the cooling pad.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(in super::super) enum Pad {
    Absent,
    Present,
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
    pub(in super::super) fn new(
        pad: impl Fn() -> Pad + Send + Sync + 'static,
        journal: hyperboost_journal::Journal,
    ) -> Self {
        Self {
            pad: Arc::new(pad),
            journal,
        }
    }
    #[cfg(not(test))]
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
#[cfg(not(test))]
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

/// One HyperBoost request, carried by its permit.
pub(super) struct Step {
    pub(super) enable: bool,
    probes: Probes,
    /// The fresh reading the request was resolved from.
    baseline: Mutex<Option<ObservedPerformance>>,
    /// The saved destination at that reading's class, which the status
    /// projects as desired: an entry changes no saved intent.
    saved: Mutex<Option<Destination>>,
}
impl Step {
    pub(super) fn new(enable: bool, probes: Probes) -> Self {
        Self {
            enable,
            probes,
            baseline: Mutex::new(None),
            saved: Mutex::new(None),
        }
    }
    pub(super) fn saved(&self) -> io::Result<Option<Destination>> {
        self.saved
            .lock()
            .map(|saved| *saved)
            .map_err(|_| io::ErrorKind::Other.into())
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
        *step.baseline.lock().map_err(|_| io::ErrorKind::Other)? = Some(baseline);
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
        // 6965-7002: already in the requested state. An exit writes nothing;
        // an entry settles on its own reading (`Plan::Unchanged`).
        if active == step.enable && !step.enable {
            return refuse(ALREADY_OFF, 1);
        }
        let layer = self.storage.profile_layer(&self.application)?;
        let profile = layer.at(power)?;
        if active == step.enable {
            let saved = self.storage.desired(power, profile.as_ref())?;
            self.accept(power, step, baseline, Destination::HyperBoost, saved)?;
            return Ok(Some(Destination::HyperBoost));
        }
        if !step.enable {
            // Exit: the saved destination at this fresh class, as managed's
            // exit restores the saved intent (`ResolveHyperBoostExitIntent`).
            // Unclassified power already refused (managed's exit needs a
            // classified source). A battery class is a battery preset's restore
            // sequence (`Plan::RestoreThenBattery`).
            let selection = select(&self.storage, power, profile.as_ref(), baseline, false)?;
            if let Some(cause) = selection.blocked {
                self.set_cause(cause);
                return Ok(None);
            }
            let Some(desired) = selection.desired else {
                // `select` names its cause whenever it selects nothing.
                return Ok(None);
            };
            self.accept(power, step, baseline, desired.0, Some(desired))?;
            return Ok(Some(desired.0));
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
            let baseline = *step.baseline.lock().map_err(|_| io::ErrorKind::Other)?;
            let message = match (step.enable, destination) {
                (true, _) if baseline == Some(ObservedPerformance::HyperBoostObserved) => {
                    ALREADY_ACTIVE.into()
                }
                (true, _) => ENTERED.into(),
                // Managed's reply names the restored performance and fan
                // intent; native restores the performance destination only.
                (false, Some(destination)) => format!(
                    "HyperBoost is off and the prior {} performance is restored.",
                    label(destination)
                ),
                (false, None) => return Err(io::ErrorKind::Other.into()),
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
        self.apply_locked(
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
                hyperboost: Some(Step::new(enabled, probes)),
                deadline,
                status,
            },
        )
        .await
        .map(Stepped::reply)
    }
}

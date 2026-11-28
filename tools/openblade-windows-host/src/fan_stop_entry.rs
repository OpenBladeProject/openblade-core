//! Fixture-only replay of the SO6120 manual Stop entry checks. The result
//! describes a decision at a supplied instant; it cannot dispatch a command.

use std::time::Duration;

pub mod worker;

const MAX_AGE: Duration = Duration::from_secs(2);
const METADATA_AGE: Duration = Duration::from_secs(5);
const ENTRY_LIMIT: Duration = Duration::from_secs(45);
pub const FIRMWARE_SHA256: &str =
    "8537E9FD55B7B35504D8D87B0191ACC4391B67365B3FE72F6E1029A424C9E707";
pub const CONTRACT_SHA256: &str =
    "B6D70649DB4EC280ECFA62D0C0BE663F0284DD1D1C73311A1203DF6DE066F8C5";
pub const DRIVER_SHA256: &str = "F8965FDCE668692C3785AFA3559159F9A18287BC0D53ABB21902895A8ECF221B";

/// Monotonic offsets on the replay clock. The run binding prevents reuse from
/// another activation; the fixture producer is responsible for using one clock.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct Span {
    pub run: u64,
    pub started: Duration,
    pub completed: Duration,
}

impl Span {
    fn fresh(self, run: u64, at: Duration, limit: Duration) -> bool {
        self.run == run
            && self.started <= self.completed
            && self.completed <= at
            && at - self.started <= limit
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Power {
    UsbC,
    Battery,
    FullAdapter,
    Unknown,
}

#[derive(Clone, Copy, Debug)]
pub struct PowerRead {
    pub capture: Span,
    pub resolved: bool,
    pub source: Power,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum PerformanceReadback {
    Balanced,
    Other,
    Unknown,
}

#[derive(Clone, Copy, Debug)]
pub struct VerifiedProfileRead {
    pub run: u64,
    pub profile: PerformanceReadback,
}

/// Preflight observations from the service's serialized entry, without the
/// continuation snapshot's placeholder power/temperature flags.
#[derive(Clone, Copy, Debug)]
pub struct ServiceEntry {
    pub caller_controls_session: bool,
    pub supported: bool,
    pub recovery_required: bool,
    pub command_context_admitted: bool,
    pub prior_guardian_owns_hardware: bool,
    pub entry_context_open: bool,
    pub transaction_resolved: bool,
    pub readback: Option<VerifiedProfileRead>,
    pub power: Power,
}

#[derive(Clone, Copy, Debug)]
pub struct HidCandidate<'a> {
    pub path: &'a str,
    pub identity: Option<(u16, u16, u16, u16, u16)>, // VID, PID, page, usage, feature length
}

/// The first two fields of the host are available only after the managed
/// dual-registry exact identity check. SKU and BIOS do not gate this service
/// contract; the exact EC version does.
#[derive(Clone, Copy, Debug)]
pub struct OwnerMetadata<'a> {
    pub capture: Span,
    pub x64: bool,
    pub host: Option<(&'a str, &'a str)>,
    pub ec_version: Option<(u16, u16)>,
    pub hid: &'a [HidCandidate<'a>],
    pub system_session: bool,
    pub reviewed_driver: Option<ReviewedDriver<'a>>,
    pub controller_count: Option<usize>,
    pub console_session: u32,
    pub service_state: ServiceState,
    pub writer_count: usize,
    pub original_writer_present: bool,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ServiceState {
    Running,
    StoppedOrStopPending,
    UnknownOrOther,
}

#[derive(Clone, Copy, Debug)]
pub struct ReviewedDriver<'a> {
    // Results of the managed configured/actual path, SCM type/running and DOS target checks.
    pub path_and_registration_match: bool,
    pub bytes: u32,
    pub sha256: &'a str,
}

#[derive(Clone, Copy, Debug)]
pub struct OwnerBinding<'a> {
    pub run: u64,
    pub device: &'a str,
    pub firmware: &'a str,
    pub contract_sha256: &'a str,
    pub firmware_sha256: &'a str,
    /// Managed journal/consent/controller-session and original service identity checks.
    pub journal_matches_owner: bool,
    pub exclusive_delegation: bool,
    pub bound_console: u32,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ThermalPolicy {
    Auto,
    Custom { cpu_cutoff: i32, gpu_cutoff: i32 },
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Readings {
    pub cpu: Option<i32>,
    pub gpu: Option<i32>,
    pub gpu_inactive: bool,
    pub gpu_powered_down: bool,
}

#[derive(Clone, Copy, Debug)]
pub enum ThermalRead {
    NotAttempted,
    Unavailable,
    Observed {
        started: Duration,
        reading: Readings,
        canceled: bool,
    },
}

#[derive(Clone, Copy, Debug)]
pub struct SafetyObservation<T = ThermalRead> {
    /// Starts before the volatile check, so thermal acquisition cannot extend expiry.
    pub capture: Span,
    pub volatile: VolatileEvidence,
    pub thermal: T,
}

#[derive(Clone, Copy, Debug)]
pub struct VolatileEvidence {
    pub capture: Span,
    pub session: ConsoleEvidence,
    pub controllers_absent: Option<bool>,
    pub original_service_is_sole_writer: Option<bool>,
}

#[derive(Clone, Copy, Debug)]
pub struct ConsoleEvidence {
    pub console: u32,
    pub system_session: bool,
    pub ac_line: Option<bool>,
}

impl ConsoleEvidence {
    fn active(self, bound: u32) -> bool {
        self.system_session && self.console == bound && valid_console(bound)
    }
}

#[derive(Clone, Copy, Debug)]
pub struct ManualEntry<'a, T = ThermalRead, R = bool> {
    pub service: ServiceEntry,
    pub binding: OwnerBinding<'a>,
    pub owner: OwnerMetadata<'a>,
    pub owner_check_at: Duration,
    pub initial_check_at: Duration,
    /// Preparation readiness is independent of a completed temperature observation.
    pub cpu_reader_ready: R,
    /// Managed immutable settings validation precedes owner startup. This
    /// replay consumes its verdict; it does not parse user preferences.
    pub settings_valid: bool,
    pub policy: ThermalPolicy,
    pub initial: Option<SafetyObservation<T>>,
    pub initial_power: Option<PowerRead>,
    pub entry_started: Duration,
    pub refreshed_at: Duration,
    pub entry_canceled: bool,
    pub entry_volatile: VolatileEvidence,
    /// A new typed read only when the prior one expired or AC changed.
    pub refreshed_power: Option<PowerRead>,
    pub entry: Option<SafetyObservation<T>>,
    pub apply: ApplyEvidence,
    /// A newer completed sampler observation at the cached boundary, if any.
    pub newer_safety: Option<SafetyObservation<T>>,
}

#[derive(Clone, Copy, Debug)]
pub struct ApplyEvidence {
    pub now: Duration,
    pub canceled: bool,
    pub console: ConsoleEvidence,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Refusal {
    DispatchSession,
    UnavailableOrRecovery,
    CommandContext,
    PreviousOwner,
    EntryContext,
    TransactionUnresolved,
    ServiceAdmission,
    InvalidSettings,
    MetadataStale,
    JournalBinding,
    HostMismatch,
    EcVersionMismatch,
    HidIdentityUnavailable,
    OwnerSession,
    DriverMismatch,
    ControllerDetected,
    Delegation,
    ActiveConsoleSession,
    ServiceRunning,
    CpuReaderPreparation,
    InitialSafety(SafetyRefusal),
    InitialPower,
    EntryExpired,
    EntryCanceled,
    EntryVolatile(VolatileRefusal),
    EntryPowerRead,
    EntryPower,
    EntryThermal(SafetyRefusal),
    EntryCached(CachedRefusal),
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum SafetyRefusal {
    MissingOrExpired,
    Volatile(VolatileRefusal),
    NotObserved(ThermalNotObserved),
    Thermal(ThermalDiagnostic),
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum VolatileRefusal {
    Metadata,
    Console,
    AcLineUnknown,
    Controllers,
    WriterOwnership,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum CachedRefusal {
    Chronology,
    ConsoleOrAc,
    PowerExpired,
    Safety(SafetyRefusal),
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ThermalNotObserved {
    NotAttempted,
    Unavailable,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct ThermalDiagnostic {
    pub policy: ThermalPolicy,
    pub reading: Readings,
    pub age: Option<Duration>,
    pub canceled: bool,
    pub decision: Option<ThermalDecision>,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct ThermalDecision {
    pub rule: ThermalRule,
    pub cpu: Option<(i32, i32)>, // actual, limit
    pub gpu: Option<(i32, i32)>,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ThermalRule {
    AutoCpuLimit,
    ImmediateCutoff,
}

fn valid_console(console: u32) -> bool {
    console != 0 && console != u32::MAX
}

fn exact_hid(candidates: &[HidCandidate<'_>]) -> bool {
    let prefix = r"\\?\hid#vid_1532&pid_02e0";
    let mut exact = 0;
    for candidate in candidates {
        if !candidate
            .path
            .get(..prefix.len())
            .is_some_and(|p| p.eq_ignore_ascii_case(prefix))
            || !matches!(
                candidate.path.as_bytes().get(prefix.len()),
                Some(b'&' | b'#')
            )
        {
            return false;
        }
        match candidate.identity {
            Some((0x1532, 0x02e0, 1, 2, 91)) => exact += 1,
            Some((0x1532, 0x02e0, ..)) => {}
            _ => return false,
        }
    }
    exact == 1
}

fn assess_volatile(
    e: VolatileEvidence,
    binding: OwnerBinding<'_>,
    at: Duration,
) -> Result<(), VolatileRefusal> {
    if !e.capture.fresh(binding.run, at, MAX_AGE) {
        return Err(VolatileRefusal::Metadata);
    }
    if !e.session.active(binding.bound_console) {
        return Err(VolatileRefusal::Console);
    }
    if e.session.ac_line.is_none() {
        return Err(VolatileRefusal::AcLineUnknown);
    }
    if e.controllers_absent != Some(true) {
        return Err(VolatileRefusal::Controllers);
    }
    if e.original_service_is_sole_writer != Some(true) {
        return Err(VolatileRefusal::WriterOwnership);
    }
    Ok(())
}

impl<T> SafetyObservation<T> {
    fn apply(
        self,
        binding: OwnerBinding<'_>,
        at: Duration,
        policy: ThermalPolicy,
        assess: impl FnOnce(Self, Duration, ThermalPolicy) -> Result<(), SafetyRefusal>,
    ) -> Result<(), SafetyRefusal> {
        if !self.capture.fresh(binding.run, at, MAX_AGE) {
            return Err(SafetyRefusal::MissingOrExpired);
        }
        if self.volatile.capture.started < self.capture.started {
            return Err(SafetyRefusal::Volatile(VolatileRefusal::Metadata));
        }
        // A reading from a different attempt must not decorate this refusal.
        assess_volatile(self.volatile, binding, self.capture.completed)
            .map_err(SafetyRefusal::Volatile)?;
        assess(self, at, policy)
    }
}

fn scripted_thermal(
    observation: SafetyObservation,
    _at: Duration,
    policy: ThermalPolicy,
) -> Result<(), SafetyRefusal> {
    let SafetyObservation {
        capture,
        volatile,
        thermal,
    } = observation;
    match thermal {
        ThermalRead::NotAttempted => {
            Err(SafetyRefusal::NotObserved(ThermalNotObserved::NotAttempted))
        }
        ThermalRead::Unavailable => {
            Err(SafetyRefusal::NotObserved(ThermalNotObserved::Unavailable))
        }
        ThermalRead::Observed {
            started,
            reading,
            canceled,
        } => {
            let age = (started >= volatile.capture.completed)
                .then(|| capture.completed.checked_sub(started))
                .flatten();
            assess_thermal(policy, reading, age, canceled).map_err(SafetyRefusal::Thermal)
        }
    }
}

fn assess_thermal(
    policy: ThermalPolicy,
    reading: Readings,
    age: Option<Duration>,
    canceled: bool,
) -> Result<(), ThermalDiagnostic> {
    let mut diagnostic = ThermalDiagnostic {
        policy,
        reading,
        age,
        canceled,
        decision: None,
    };
    if canceled || !age.is_some_and(|age| age <= MAX_AGE) {
        return Err(diagnostic);
    }
    let valid = |t: i32| (10..=125).contains(&t);
    let Some(cpu) = reading.cpu.filter(|t| valid(*t)) else {
        return Err(diagnostic);
    };
    let trigger = |actual, limit| (actual > limit).then_some((actual, limit));
    let (rule, cpu, gpu) = match policy {
        ThermalPolicy::Auto if reading.gpu_inactive => {
            (ThermalRule::AutoCpuLimit, trigger(cpu, 55), None)
        }
        ThermalPolicy::Custom {
            cpu_cutoff,
            gpu_cutoff,
        } => (
            ThermalRule::ImmediateCutoff,
            trigger(cpu, cpu_cutoff),
            if reading.gpu_powered_down {
                None
            } else {
                let Some(gpu) = reading.gpu.filter(|t| valid(*t)) else {
                    return Err(diagnostic);
                };
                trigger(gpu, gpu_cutoff)
            },
        ),
        ThermalPolicy::Auto => return Err(diagnostic),
    };
    if cpu.is_some() || gpu.is_some() {
        diagnostic.decision = Some(ThermalDecision { rule, cpu, gpu });
        Err(diagnostic)
    } else {
        Ok(())
    }
}

fn entry_window<T, R>(
    e: &ManualEntry<'_, T, R>,
    at: Duration,
    canceled: bool,
) -> Result<(), Refusal> {
    if canceled {
        return Err(Refusal::EntryCanceled);
    }
    if !at
        .checked_sub(e.entry_started)
        .is_some_and(|age| age < ENTRY_LIMIT)
    {
        return Err(Refusal::EntryExpired);
    }
    Ok(())
}

/// Replay manual Stop entry and the application of its evidence at the cached
/// boundary. A success is only a fixture verdict. It confers no ownership.
pub fn replay(e: &ManualEntry<'_>) -> Result<(), Refusal> {
    replay_core(e, e.cpu_reader_ready, scripted_thermal)
}

fn replay_core<T: Copy, R>(
    e: &ManualEntry<'_, T, R>,
    prepared: bool,
    assess: impl Fn(SafetyObservation<T>, Duration, ThermalPolicy) -> Result<(), SafetyRefusal>,
) -> Result<(), Refusal> {
    let (s, b, o) = (e.service, e.binding, e.owner);
    if !s.caller_controls_session {
        return Err(Refusal::DispatchSession);
    }
    if !s.supported || s.recovery_required {
        return Err(Refusal::UnavailableOrRecovery);
    }
    if !s.command_context_admitted {
        return Err(Refusal::CommandContext);
    }
    if s.prior_guardian_owns_hardware {
        return Err(Refusal::PreviousOwner);
    }
    if !s.entry_context_open {
        return Err(Refusal::EntryContext);
    }
    if !s.transaction_resolved {
        return Err(Refusal::TransactionUnresolved);
    }
    if !s
        .readback
        .is_some_and(|read| read.run == b.run && read.profile == PerformanceReadback::Balanced)
        || s.power != Power::UsbC
    {
        return Err(Refusal::ServiceAdmission);
    }

    let cutoff_valid = match e.policy {
        ThermalPolicy::Auto => true,
        ThermalPolicy::Custom {
            cpu_cutoff,
            gpu_cutoff,
        } => (10..=100).contains(&cpu_cutoff) && (10..=100).contains(&gpu_cutoff),
    };
    if !e.settings_valid || !cutoff_valid {
        return Err(Refusal::InvalidSettings);
    }
    if b.run == 0 || !o.capture.fresh(b.run, e.owner_check_at, METADATA_AGE) {
        return Err(Refusal::MetadataStale);
    }
    if !b.journal_matches_owner
        || b.device != "RZ09-0581/1532:02E0"
        || b.firmware != "SO6120-EC1.04"
        || b.contract_sha256 != CONTRACT_SHA256
        || b.firmware_sha256 != FIRMWARE_SHA256
    {
        return Err(Refusal::JournalBinding);
    }
    if !o.x64 || o.host != Some(("Razer", "Blade 16 - RZ09-0581")) {
        return Err(Refusal::HostMismatch);
    }
    if o.ec_version != Some((1, 4)) {
        return Err(Refusal::EcVersionMismatch);
    }
    if !exact_hid(o.hid) {
        return Err(Refusal::HidIdentityUnavailable);
    }
    if !o.system_session {
        return Err(Refusal::OwnerSession);
    }
    if !o.reviewed_driver.is_some_and(|driver| {
        driver.path_and_registration_match
            && driver.bytes == 15008
            && driver.sha256.eq_ignore_ascii_case(DRIVER_SHA256)
    }) {
        return Err(Refusal::DriverMismatch);
    }
    if o.controller_count != Some(0) {
        return Err(Refusal::ControllerDetected);
    }
    let original_or_exited = match o.writer_count {
        0 => o.service_state == ServiceState::StoppedOrStopPending,
        1 => o.original_writer_present,
        _ => false,
    };
    if !b.exclusive_delegation || !original_or_exited {
        return Err(Refusal::Delegation);
    }
    if !valid_console(b.bound_console) || o.console_session != b.bound_console {
        return Err(Refusal::ActiveConsoleSession);
    }
    if o.service_state != ServiceState::Running {
        return Err(Refusal::ServiceRunning);
    }
    if !prepared {
        return Err(Refusal::CpuReaderPreparation);
    }
    let initial = e
        .initial
        .filter(|proof| proof.capture.started >= e.owner_check_at)
        .ok_or(Refusal::InitialSafety(SafetyRefusal::MissingOrExpired))?;
    initial
        .apply(b, e.initial_check_at, e.policy, &assess)
        .map_err(Refusal::InitialSafety)?;
    let initial_power = e
        .initial_power
        .filter(|power| {
            power.resolved
                && power.source == Power::UsbC
                && power.capture.started >= e.initial_check_at
                && power.capture.completed <= e.entry_started
                && power
                    .capture
                    .fresh(b.run, power.capture.completed, METADATA_AGE)
        })
        .ok_or(Refusal::InitialPower)?;

    entry_window(e, e.refreshed_at, e.entry_canceled)?;
    if e.entry_volatile.capture.started < e.entry_started {
        return Err(Refusal::EntryVolatile(VolatileRefusal::Metadata));
    }
    assess_volatile(e.entry_volatile, b, e.refreshed_at).map_err(Refusal::EntryVolatile)?;
    let power = if initial_power.capture.fresh(b.run, e.refreshed_at, MAX_AGE)
        && e.entry_volatile.session.ac_line == Some(true)
    {
        initial_power
    } else {
        e.refreshed_power
            .filter(|power| {
                power.resolved
                    && power.capture.started >= e.entry_volatile.capture.completed
                    && power.capture.completed <= e.refreshed_at
                    && power
                        .capture
                        .fresh(b.run, power.capture.completed, METADATA_AGE)
            })
            .ok_or(Refusal::EntryPowerRead)?
    };
    if power.source != Power::UsbC {
        return Err(Refusal::EntryPower);
    }
    let proof = e.entry.unwrap_or(initial);
    if proof.capture.started < initial.capture.started {
        return Err(Refusal::EntryThermal(SafetyRefusal::MissingOrExpired));
    }
    proof
        .apply(b, e.refreshed_at, e.policy, &assess)
        .map_err(Refusal::EntryThermal)?;
    entry_window(e, e.apply.now, e.apply.canceled)?;
    let cached_proof = e.newer_safety.unwrap_or(proof);
    if e.apply.now < e.refreshed_at {
        return Err(Refusal::EntryCached(CachedRefusal::Chronology));
    }
    if !e.apply.console.active(b.bound_console) || e.apply.console.ac_line != Some(true) {
        return Err(Refusal::EntryCached(CachedRefusal::ConsoleOrAc));
    }
    if !power.capture.fresh(b.run, e.apply.now, MAX_AGE) {
        return Err(Refusal::EntryCached(CachedRefusal::PowerExpired));
    }
    if cached_proof.capture.started < proof.capture.started {
        return Err(Refusal::EntryCached(CachedRefusal::Chronology));
    }
    cached_proof
        .apply(b, e.apply.now, e.policy, &assess)
        .map_err(|reason| Refusal::EntryCached(CachedRefusal::Safety(reason)))
}

#[cfg(test)]
mod tests;

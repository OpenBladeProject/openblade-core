//! NVIDIA GPU clock offsets on the exact RZ09-0581 through NVML, the
//! backend of managed `NvidiaGpuClockOffsetController` and the GPU half of
//! the managed `WindowsVerifiedBladeDeviceProvider`. NVML is NVIDIA's driver
//! API, not a laptop firmware command.
//!
//! Three layers, each owning one concern:
//!
//! - [`Controller`] is managed `NvidiaGpuClockOffsetController`: one NVML
//!   session per query or write, the exact one-GPU name admission, the P0
//!   range query, the core-then-memory write with its readback, and the
//!   independent restore judged by a fresh readback. `controller_vectors`
//!   in the tests replays the managed-recorded `gpu-clock-offset-vectors.json`.
//! - [`Management`] is the provider's management state machine
//!   (`GpuClockOffsetManagementState`): what OpenBlade confirmed it owns, the
//!   policy clip of what it publishes, and the read-only resolution of an
//!   uncertain submission. Like managed, uncertainty lives in memory only:
//!   a restart begins at `Unknown`, and the next write's preflight query or
//!   the one ungated read resolves what the driver holds.
//! - [`Worker`] runs both on a thread of its own, so no NVML call blocks the
//!   async runtime, the HID read job or the GPU temperature sampler, whose
//!   session lease it does not share. A call has a budget: one that has not
//!   started when it runs out, or whose caller stops waiting, never starts,
//!   and one that has is abandoned and finishes on the worker, which records
//!   the driver's result. A call that panics is abandoned too: the worker
//!   keeps serving, and a submission the panic interrupted is uncertain.
//!
//! The worker serializes single calls only. A paired performance write spans
//! several (the preflight, the firmware write, then [`Management::submit`] or
//! [`Management::mark_reset_confirmed`]), and a resume's
//! [`Management::invalidate`] or a [`Management::relinquish`] could otherwise
//! land between them. The integration must hold one exclusive thermal lane
//! from the preflight through the completion, as managed holds
//! `EnterHardwareOperationAsync` around `ExecuteProductionWriteAsync`, its
//! relinquish and its invalidation. `submit` rechecks the rules that need no
//! NVML session and `mark_reset_confirmed` refuses a withdrawn reset, but
//! neither replaces that lane.
//!
//! Nothing here writes at Stop, shutdown or a low-power transition: managed
//! leaves the offsets in the driver then, and only a resume invalidates its
//! confirmation ([`Management::invalidate`]).
// Only `service-gpu-clock-tuning-v1` composes the backend, through the
// explicit performance path (`thermal_gpu`); `setGpuClockTuning` is a later
// slice.
#![cfg_attr(not(test), allow(dead_code))]

use openblade_domain::profile_set::model::GpuClockOffsets;
use std::{
    ops::RangeInclusive,
    panic::{self, AssertUnwindSafe},
    sync::{
        Arc,
        atomic::{AtomicBool, AtomicU8, Ordering},
        mpsc::{Receiver, SyncSender, TrySendError, sync_channel},
    },
    thread,
    time::Duration,
};
use tokio::sync::oneshot;

#[cfg(windows)]
// Only `service-gpu-clock-tuning-v1` composes the writer (`production`).
#[allow(dead_code)]
#[path = "gpu_clock_offsets_windows.rs"]
mod windows;

/// `NVML_SUCCESS`.
pub(super) const SUCCESS: i32 = 0;
/// `nvmlClockOffset_v1`: the structure's version and size.
const CLOCK_OFFSET_VERSION: u32 = (1 << 24) | 24;
/// `NVML_PSTATE_0`, the editable P-state.
const PSTATE_ZERO: i32 = 0;
/// NVML exposes GDDR memory offsets at twice the editable P-state delta that
/// NVAPI reports and tuning applications display.
const MEMORY_MULTIPLIER: i32 = 2;
/// The one GPU name managed admits on the exact RZ09-0581.
pub(super) const ADMITTED_NAME: &str = "NVIDIA GeForce RTX 5090 Laptop GPU";
/// Managed's `StringBuilder(128)` for `nvmlDeviceGetName`.
pub(super) const NAME_CAPACITY: usize = 128;
/// Managed `GpuClockOffsetSettings` contract bounds.
const ABSOLUTE: RangeInclusive<i32> = -10_000..=10_000;

/// `nvmlClockType_t`.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
#[repr(i32)]
pub(super) enum ClockKind {
    Graphics = 0,
    Memory = 2,
}

/// `nvmlClockOffset_v1_t`.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub(super) struct ClockOffset {
    pub(super) version: u32,
    pub(super) kind: i32,
    pub(super) pstate: i32,
    pub(super) current: i32,
    pub(super) minimum: i32,
    pub(super) maximum: i32,
}

impl ClockOffset {
    /// Managed `QueryRange`'s request: the P0 offsets of `kind`.
    fn query(kind: ClockKind) -> Self {
        Self {
            version: CLOCK_OFFSET_VERSION,
            kind: kind as i32,
            pstate: PSTATE_ZERO,
            ..Self::default()
        }
    }

    /// Managed `CreateWrite`: a request at P0 carrying `megahertz` in the
    /// editable unit, which NVML takes doubled for memory. None when the
    /// doubled value does not fit, which well-formed offsets never reach.
    fn set(kind: ClockKind, megahertz: i32) -> Option<Self> {
        let current = match kind {
            ClockKind::Graphics => megahertz,
            ClockKind::Memory => megahertz.checked_mul(MEMORY_MULTIPLIER)?,
        };
        Some(Self {
            current,
            ..Self::query(kind)
        })
    }
}

/// The NVML functions managed's clock-offset controller imports. Production
/// loads them through the Service's one NVML loader; tests script them.
pub(super) trait Nvml {
    /// `nvmlInit_v2`.
    fn init(&mut self) -> i32;
    /// `nvmlShutdown`.
    fn shutdown(&mut self) -> i32;
    /// `nvmlDeviceGetCount_v2`.
    fn device_count(&mut self) -> Result<u32, i32>;
    /// `nvmlDeviceGetHandleByIndex_v2`.
    fn device(&mut self, index: u32) -> Result<usize, i32>;
    /// `nvmlDeviceGetName` into a buffer of [`NAME_CAPACITY`] bytes.
    fn name(&mut self, device: usize, name: &mut [u8; NAME_CAPACITY]) -> i32;
    /// `nvmlDeviceGetClockOffsets`.
    fn clock_offsets(&mut self, device: usize, info: &mut ClockOffset) -> i32;
    /// `nvmlDeviceSetClockOffsets`.
    fn set_clock_offsets(&mut self, device: usize, info: &mut ClockOffset) -> i32;
}

/// A boxed backend, so the composition can hold production NVML or a
/// scripted one behind one worker type.
impl<T: Nvml + ?Sized> Nvml for Box<T> {
    fn init(&mut self) -> i32 {
        (**self).init()
    }
    fn shutdown(&mut self) -> i32 {
        (**self).shutdown()
    }
    fn device_count(&mut self) -> Result<u32, i32> {
        (**self).device_count()
    }
    fn device(&mut self, index: u32) -> Result<usize, i32> {
        (**self).device(index)
    }
    fn name(&mut self, device: usize, name: &mut [u8; NAME_CAPACITY]) -> i32 {
        (**self).name(device, name)
    }
    fn clock_offsets(&mut self, device: usize, info: &mut ClockOffset) -> i32 {
        (**self).clock_offsets(device, info)
    }
    fn set_clock_offsets(&mut self, device: usize, info: &mut ClockOffset) -> i32 {
        (**self).set_clock_offsets(device, info)
    }
}

/// Managed `GpuClockOffsetRange`, in the editable unit.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(super) struct Range {
    pub(super) minimum: i32,
    pub(super) maximum: i32,
    pub(super) current: i32,
}

impl Range {
    fn is_well_formed(&self) -> bool {
        self.minimum >= *ABSOLUTE.start()
            && self.maximum <= *ABSOLUTE.end()
            && self.minimum <= self.maximum
            && (self.minimum..=self.maximum).contains(&self.current)
    }
}

/// Managed `GpuClockOffsetCapabilities`: the admitted GPU, its ranges and its
/// current offsets.
#[derive(Clone, Debug, PartialEq, Eq)]
pub(super) struct Capabilities {
    pub(super) name: String,
    pub(super) core: Range,
    pub(super) memory: Range,
}

impl Capabilities {
    pub(super) fn current(&self) -> GpuClockOffsets {
        GpuClockOffsets {
            core_megahertz: self.core.current,
            memory_megahertz: self.memory.current,
        }
    }

    /// Managed `GpuClockOffsetCapabilities.Supports`: a bounds check only.
    pub(super) fn supports(&self, offsets: GpuClockOffsets) -> bool {
        well_formed(offsets)
            && (self.core.minimum..=self.core.maximum).contains(&offsets.core_megahertz)
            && (self.memory.minimum..=self.memory.maximum).contains(&offsets.memory_megahertz)
    }
}

/// Managed `GpuClockOffsetSettings.IsWellFormed`.
pub(super) fn well_formed(offsets: GpuClockOffsets) -> bool {
    ABSOLUTE.contains(&offsets.core_megahertz) && ABSOLUTE.contains(&offsets.memory_megahertz)
}

/// The model's admitted GPU overclock envelope (managed
/// `GpuOverclockCapabilities`). Managed declares no step for either range,
/// and never applies one in the Service: every bound is compared inline.
#[derive(Clone, Debug, PartialEq, Eq)]
pub(super) struct Policy {
    pub(super) core: RangeInclusive<i32>,
    pub(super) memory: RangeInclusive<i32>,
}

/// Managed `GetRz090581GpuOverclockCapabilities`: the Synapse-visible
/// envelope on the exact RZ09-0581, admitted whatever the BIOS once the
/// exact device identity succeeds.
pub(super) const RZ09_0581: Policy = Policy {
    core: -1000..=400,
    memory: -1000..=3000,
};

impl Policy {
    /// The envelope half of managed `GpuClockOffsetsSupported`.
    fn admits(&self, offsets: GpuClockOffsets) -> bool {
        self.core.contains(&offsets.core_megahertz)
            && self.memory.contains(&offsets.memory_megahertz)
    }

    /// Managed `ApplyGpuPolicy`: the driver's ranges narrowed to the
    /// envelope, or None when they do not overlap or the current offsets lie
    /// outside it. The raw driver range is never published.
    pub(super) fn clip(&self, driver: &Capabilities) -> Option<Capabilities> {
        let narrow = |range: &Range, envelope: &RangeInclusive<i32>| {
            let minimum = range.minimum.max(*envelope.start());
            let maximum = range.maximum.min(*envelope.end());
            (minimum <= maximum && (minimum..=maximum).contains(&range.current)).then_some(Range {
                minimum,
                maximum,
                current: range.current,
            })
        };
        Some(Capabilities {
            name: driver.name.clone(),
            core: narrow(&driver.core, &self.core)?,
            memory: narrow(&driver.memory, &self.memory)?,
        })
    }
}

/// How a refused or failed GPU write leaves the driver.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(super) enum Failure {
    /// Managed `CapabilityUnavailableException`: refused before any NVML
    /// session wrote anything.
    Unavailable,
    /// Managed `WriteSubmissionState.KnownNotApplied`.
    NotApplied,
    /// Managed `WriteSubmissionState.Indeterminate`: the driver may hold
    /// offsets nothing confirmed.
    Indeterminate,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub(super) struct WriteError {
    pub(super) failure: Failure,
    pub(super) message: String,
}

impl WriteError {
    fn new(failure: Failure, message: impl Into<String>) -> Self {
        Self {
            failure,
            message: message.into(),
        }
    }
}

/// Why one step of a session failed: managed `BladeWriteException` thrown
/// inside the session (`Refused`) or `NvmlClockOffsetException` (`Fault`).
enum Step {
    Refused(&'static str),
    Fault(String),
}

/// Managed `EnsureSuccess`'s fault.
fn fault(status: i32, action: &str) -> Step {
    Step::Fault(format!(
        "Unable to {action}; NVIDIA NVML returned status {status}."
    ))
}

fn ensure(status: i32, action: &str) -> Result<(), Step> {
    if status == SUCCESS {
        Ok(())
    } else {
        Err(fault(status, action))
    }
}

/// What a write had done when it failed.
#[derive(Default)]
struct Progress {
    device: usize,
    prior: Option<GpuClockOffsets>,
    core: bool,
    memory: bool,
}

/// Managed `NvidiaGpuClockOffsetController` over one [`Nvml`].
pub(super) struct Controller<N> {
    nvml: N,
    /// Whether an `nvmlInit_v2` has not been shut down yet: only after a
    /// panic unwound out of a query or write.
    session: bool,
}

impl<N: Nvml> Controller<N> {
    pub(super) fn new(nvml: N) -> Self {
        Self {
            nvml,
            session: false,
        }
    }

    /// Managed `Query`: the admitted GPU's capabilities, or None for any
    /// failure. The session is shut down only when it started.
    pub(super) fn query(&mut self) -> Option<Capabilities> {
        if self.init() != SUCCESS {
            return None;
        }
        let capabilities = self.open().and_then(|device| self.capabilities(device));
        self.shutdown();
        capabilities.ok()
    }

    /// Managed `Set`: the driver range of the admitted GPU must contain the
    /// offsets, then core and memory are written and read back. A failure
    /// after either write restores both channels it touched, each attempted
    /// whatever the other did, and judges the result from a fresh readback.
    pub(super) fn set(&mut self, offsets: GpuClockOffsets) -> Result<Capabilities, WriteError> {
        let requests = match (
            well_formed(offsets),
            ClockOffset::set(ClockKind::Graphics, offsets.core_megahertz),
            ClockOffset::set(ClockKind::Memory, offsets.memory_megahertz),
        ) {
            (true, Some(core), Some(memory)) => [core, memory],
            _ => {
                return Err(WriteError::new(
                    Failure::NotApplied,
                    "GPU clock offsets are invalid.",
                ));
            }
        };
        let status = self.init();
        if status != SUCCESS {
            return Err(WriteError::new(
                Failure::NotApplied,
                format!(
                    "GPU clock tuning failed. Unable to initialize NVIDIA NVML; NVIDIA NVML returned status {status}."
                ),
            ));
        }
        let mut progress = Progress::default();
        let result = match self.attempt(offsets, requests, &mut progress) {
            Ok(confirmed) => Ok(confirmed),
            Err(Step::Refused(message)) => Err(WriteError::new(Failure::NotApplied, message)),
            Err(Step::Fault(message)) => Err(self.recover(&progress, &message)),
        };
        self.shutdown();
        result
    }

    fn attempt(
        &mut self,
        offsets: GpuClockOffsets,
        [core, memory]: [ClockOffset; 2],
        progress: &mut Progress,
    ) -> Result<Capabilities, Step> {
        progress.device = self.open()?;
        let prior = self.capabilities(progress.device)?;
        progress.prior = Some(prior.current());
        if !prior.supports(offsets) {
            return Err(Step::Refused(
                "GPU clock offsets are outside the ranges reported by the NVIDIA driver.",
            ));
        }
        progress.core = true;
        self.write(progress.device, core, "set the GPU core clock offset")?;
        progress.memory = true;
        self.write(progress.device, memory, "set the GPU memory clock offset")?;
        let confirmed = self.capabilities(progress.device)?;
        let current = confirmed.current();
        if current != offsets {
            return Err(Step::Fault(format!(
                "NVIDIA confirmed Core={};Memory={} instead of Core={};Memory={}.",
                current.core_megahertz,
                current.memory_megahertz,
                offsets.core_megahertz,
                offsets.memory_megahertz
            )));
        }
        Ok(confirmed)
    }

    /// Managed `Set`'s operational-failure arm: KnownNotApplied when the
    /// prior offsets were restored or nothing was written, else
    /// Indeterminate.
    fn recover(&mut self, progress: &Progress, message: &str) -> WriteError {
        let restored = progress.prior.is_some_and(|prior| {
            self.restore(progress.device, prior, progress.core, progress.memory)
        });
        let failure = if restored || (!progress.core && !progress.memory) {
            Failure::NotApplied
        } else {
            Failure::Indeterminate
        };
        WriteError::new(
            failure,
            if restored {
                format!("GPU clock tuning failed and the previous offsets were restored. {message}")
            } else {
                format!("GPU clock tuning failed. {message}")
            },
        )
    }

    /// Managed `TryRestore`: the readback decides, not the write status, so a
    /// refused write that left the prior offsets in place is not applied.
    fn restore(&mut self, device: usize, prior: GpuClockOffsets, core: bool, memory: bool) -> bool {
        // The prior offsets come from a well-formed driver reading, so both
        // requests exist.
        let core = core
            .then(|| ClockOffset::set(ClockKind::Graphics, prior.core_megahertz))
            .flatten();
        let memory = memory
            .then(|| ClockOffset::set(ClockKind::Memory, prior.memory_megahertz))
            .flatten();
        if let Some(core) = core {
            let _ = self.write(device, core, "restore the GPU core clock offset");
        }
        if let Some(memory) = memory {
            let _ = self.write(device, memory, "restore the GPU memory clock offset");
        }
        self.capabilities(device)
            .is_ok_and(|restored| restored.current() == prior)
    }

    /// Managed `OpenAdmittedDevice`: exactly one NVIDIA GPU, at index 0.
    fn open(&mut self) -> Result<usize, Step> {
        let count = self
            .nvml
            .device_count()
            .map_err(|status| fault(status, "enumerate NVIDIA GPUs"))?;
        if count != 1 {
            return Err(Step::Fault(
                "GPU clock tuning requires exactly one NVIDIA GPU.".into(),
            ));
        }
        self.nvml
            .device(0)
            .map_err(|status| fault(status, "open NVIDIA GPU 0"))
    }

    /// Managed `QueryCapabilities`: the name is rechecked for every query and
    /// write, so an earlier success admits no GPU selected later.
    fn capabilities(&mut self, device: usize) -> Result<Capabilities, Step> {
        let mut name = [0u8; NAME_CAPACITY];
        ensure(
            self.nvml.name(device, &mut name),
            "read the NVIDIA GPU name",
        )?;
        let end = name
            .iter()
            .position(|byte| *byte == 0)
            .unwrap_or(NAME_CAPACITY);
        if &name[..end] != ADMITTED_NAME.as_bytes() {
            return Err(Step::Fault(
                "GPU clock tuning is unavailable for this NVIDIA GPU.".into(),
            ));
        }
        Ok(Capabilities {
            name: ADMITTED_NAME.into(),
            core: self.range(device, ClockKind::Graphics, "GPU core clock offset")?,
            memory: self.range(device, ClockKind::Memory, "GPU memory clock offset")?,
        })
    }

    /// Managed `QueryRange`, in the editable unit.
    fn range(&mut self, device: usize, kind: ClockKind, label: &str) -> Result<Range, Step> {
        let mut info = ClockOffset::query(kind);
        ensure(
            self.nvml.clock_offsets(device, &mut info),
            &format!("read the {label}"),
        )?;
        let divisor = match kind {
            ClockKind::Graphics => 1,
            ClockKind::Memory => MEMORY_MULTIPLIER,
        };
        if info.minimum % divisor != 0 || info.maximum % divisor != 0 || info.current % divisor != 0
        {
            return Err(Step::Fault(format!(
                "NVIDIA returned an unrepresentable {label} value."
            )));
        }
        let range = Range {
            minimum: info.minimum / divisor,
            maximum: info.maximum / divisor,
            current: info.current / divisor,
        };
        if !range.is_well_formed() {
            return Err(Step::Fault(format!(
                "NVIDIA returned an invalid {label} range."
            )));
        }
        Ok(range)
    }

    fn write(&mut self, device: usize, mut request: ClockOffset, action: &str) -> Result<(), Step> {
        ensure(self.nvml.set_clock_offsets(device, &mut request), action)
    }

    /// `nvmlInit_v2`, recording that a session is open.
    fn init(&mut self) -> i32 {
        let status = self.nvml.init();
        self.session = status == SUCCESS;
        status
    }

    /// `nvmlShutdown` of the open session, if any.
    fn shutdown(&mut self) {
        if std::mem::take(&mut self.session) {
            let _ = self.nvml.shutdown();
        }
    }
}

/// Managed `GpuClockOffsetManagementState`.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(super) enum State {
    Unknown,
    Managed,
    ResetConfirmed,
    Relinquished,
    RelinquishedDefaultConfirmed,
    /// An NVML submission may have changed the offsets and nothing confirmed
    /// the result.
    SubmissionUncertain,
}

/// Whether the dGPU runs a workload (managed
/// `WindowsGpuActivitySource.IsActiveAsync`), or None when unknown. Read only
/// where managed reads it, so an idle GPU is not woken.
pub(super) type Activity = Box<dyn FnMut() -> Option<bool>>;

/// The provider's GPU clock-offset management over one [`Controller`].
pub(super) struct Management<N> {
    controller: Controller<N>,
    activity: Activity,
    policy: Policy,
    state: State,
    /// The managed observation: the confirmed offsets within the envelope.
    confirmed: Option<Capabilities>,
    /// Whether the uncertain submission was a default reset.
    uncertain_reset: bool,
    /// The state before the first uncertain submission.
    uncertain_prior: State,
    /// Whether the one query that ignores the dGPU activity has run.
    ungated_query_attempted: bool,
    /// The retain flag of the NVML submission in progress, read only if a
    /// panic unwinds out of it.
    submitting: Option<bool>,
}

/// The admission rules that need no NVML session: well-formed offsets, and a
/// default reset for any write that relinquishes.
fn request_rules(offsets: GpuClockOffsets, retain: bool) -> Result<(), WriteError> {
    if !well_formed(offsets) {
        return Err(WriteError::new(
            Failure::NotApplied,
            "GPU clock offsets are invalid.",
        ));
    }
    if !retain && offsets != GpuClockOffsets::default() {
        return Err(WriteError::new(
            Failure::NotApplied,
            "Relinquishing GPU clock tuning requires a default-offset reset.",
        ));
    }
    Ok(())
}

/// Managed's refusal of offsets outside the driver's ranges or the envelope.
fn outside_ranges() -> WriteError {
    WriteError::new(
        Failure::Unavailable,
        "The requested GPU clock offsets are outside the ranges reported by the NVIDIA driver.",
    )
}

impl<N: Nvml> Management<N> {
    /// A fresh management, as after a Service start: `Unknown`, nothing
    /// confirmed and nothing uncertain.
    pub(super) fn new(nvml: N, activity: Activity, policy: Policy) -> Self {
        Self {
            controller: Controller::new(nvml),
            activity,
            policy,
            state: State::Unknown,
            confirmed: None,
            uncertain_reset: false,
            uncertain_prior: State::Unknown,
            ungated_query_attempted: false,
            submitting: None,
        }
    }

    pub(super) fn state(&self) -> State {
        self.state
    }

    /// Managed `ReadManagedGpuClockOffsetsAsync`: the offsets OpenBlade
    /// manages, refreshed from the driver only while the dGPU is active, and
    /// none while they are not managed. An uncertain submission is resolved
    /// by one query whatever the activity, then only while it is active.
    pub(super) fn read(&mut self) -> Option<Capabilities> {
        if self.state == State::SubmissionUncertain && !self.ungated_query_attempted {
            self.ungated_query_attempted = true;
            return self.resolve_uncertain();
        }
        if !matches!(self.state, State::Managed | State::SubmissionUncertain) {
            return None;
        }
        let active = (self.activity)() == Some(true);
        if self.state == State::SubmissionUncertain {
            return if active {
                self.resolve_uncertain()
            } else {
                None
            };
        }
        if !active {
            return self.confirmed.clone();
        }
        // An optional observation cannot invalidate confirmed state: a failed
        // query keeps the confirmation.
        if let Some(observed) = self.controller.query() {
            self.confirmed = self.policy.clip(&observed);
        }
        self.confirmed.clone()
    }

    /// Managed `QueryGpuClockOffsets`: the write path's range query, which
    /// also resolves an uncertain submission read-only first, so the prior
    /// state of a failed write is what the driver showed.
    pub(super) fn query_for_write(&mut self) -> Option<Capabilities> {
        let observed = self.controller.query()?;
        if self.state == State::SubmissionUncertain {
            self.ungated_query_attempted = true;
            self.resolve_observation(&observed);
        }
        Some(observed)
    }

    /// The admission half of managed `SetGpuClockOffsetsAsync` and of the
    /// paired performance write's preflight: well-formed offsets, a reset
    /// for any write that relinquishes, then the driver's fresh ranges and
    /// the envelope. Nothing is written.
    pub(super) fn preflight(
        &mut self,
        offsets: GpuClockOffsets,
        retain: bool,
    ) -> Result<(), WriteError> {
        request_rules(offsets, retain)?;
        match self.query_for_write() {
            None => Err(WriteError::new(
                Failure::Unavailable,
                "GPU clock tuning is unavailable.",
            )),
            Some(driver) if !driver.supports(offsets) || !self.policy.admits(offsets) => {
                Err(outside_ranges())
            }
            Some(_) => Ok(()),
        }
    }

    /// Managed `SetGpuClockOffsetsAndUpdateManagement`: the NVML write and
    /// the state it leaves. A confirmed write is `Managed` (or
    /// `ResetConfirmed` for a reset that relinquishes) with the clipped
    /// observation. A write that may have changed the offsets withdraws
    /// every earlier confirmation and stays uncertain until a driver read
    /// resolves it. A write known not to have applied changes nothing.
    ///
    /// The rules that need no NVML session, well-formed offsets, a reset for
    /// a write that relinquishes and the envelope, are checked again here,
    /// so a paired write's submission cannot pass offsets its preflight
    /// never saw. The driver's ranges are the controller's to check.
    pub(super) fn submit(
        &mut self,
        offsets: GpuClockOffsets,
        retain: bool,
    ) -> Result<Option<Capabilities>, WriteError> {
        request_rules(offsets, retain)?;
        if !self.policy.admits(offsets) {
            return Err(outside_ranges());
        }
        self.submitting = Some(retain);
        let result = self.controller.set(offsets);
        self.submitting = None;
        match result {
            Ok(confirmed) => {
                self.state = if retain {
                    State::Managed
                } else {
                    State::ResetConfirmed
                };
                self.confirmed = retain.then(|| self.policy.clip(&confirmed)).flatten();
                Ok(self.confirmed.clone())
            }
            Err(error) => {
                if error.failure != Failure::NotApplied {
                    self.enter_uncertain(retain);
                }
                Err(error)
            }
        }
    }

    /// A submission that may have changed the offsets: no earlier managed
    /// or default-reset confirmation survives it, and it stays uncertain
    /// until a driver read resolves it. A second uncertain submission keeps
    /// the state before the first.
    fn enter_uncertain(&mut self, retain: bool) {
        if self.state != State::SubmissionUncertain {
            self.uncertain_prior = self.state;
        }
        self.uncertain_reset = !retain;
        self.ungated_query_attempted = false;
        self.state = State::SubmissionUncertain;
        self.confirmed = None;
    }

    /// After a call panicked on the worker: the NVML session it left open is
    /// shut down, and a submission it interrupted is uncertain, since the
    /// driver may hold its offsets. A panic outside a submission left the
    /// state as the last completed step recorded it.
    fn recover_from_panic(&mut self) {
        self.controller.shutdown();
        if let Some(retain) = self.submitting.take() {
            self.enter_uncertain(retain);
        }
    }

    /// Managed `SetGpuClockOffsetsAsync`: [`preflight`](Self::preflight),
    /// then [`submit`](Self::submit).
    pub(super) fn set(
        &mut self,
        offsets: GpuClockOffsets,
        retain: bool,
    ) -> Result<Option<Capabilities>, WriteError> {
        self.preflight(offsets, retain)?;
        self.submit(offsets, retain)
    }

    /// Managed `HasConfirmedDefaultReset`: a paired write that relinquishes
    /// may reuse a default reset the device still holds as confirmed.
    pub(super) fn has_confirmed_default_reset(&self) -> bool {
        matches!(
            self.state,
            State::ResetConfirmed | State::RelinquishedDefaultConfirmed
        )
    }

    /// The paired write's reuse of a confirmed reset (managed
    /// `SetGpuClockOffsetManagementState(ResetConfirmed)`). Refused, changing
    /// nothing, when the reset is no longer confirmed, for example because a
    /// resume withdrew it after the preflight decided to reuse it; the caller
    /// must then submit the reset. Returns whether the reset was reused.
    #[must_use]
    pub(super) fn mark_reset_confirmed(&mut self) -> bool {
        if !self.has_confirmed_default_reset() {
            return false;
        }
        self.state = State::ResetConfirmed;
        self.confirmed = None;
        true
    }

    /// Managed `RelinquishGpuClockOffsetManagementAsync`: OpenBlade stops
    /// managing the offsets and writes nothing. An uncertain submission is
    /// resolved read-only first, by the one ungated query or while the dGPU
    /// is active. If the driver cannot be read it stays uncertain and nothing
    /// is released, so offsets nothing confirmed are never treated as known.
    /// Returns whether management was released.
    pub(super) fn relinquish(&mut self) -> bool {
        if self.state == State::SubmissionUncertain {
            let attempt = !self.ungated_query_attempted || (self.activity)() == Some(true);
            if attempt {
                self.ungated_query_attempted = true;
                self.resolve_uncertain();
            }
            if self.state == State::SubmissionUncertain {
                return false;
            }
        }
        let default_confirmed = self.has_confirmed_default_reset()
            || self.state == State::Managed
                && self
                    .confirmed
                    .as_ref()
                    .is_some_and(|c| c.current() == GpuClockOffsets::default());
        self.state = if default_confirmed {
            State::RelinquishedDefaultConfirmed
        } else {
            State::Relinquished
        };
        self.confirmed = None;
        true
    }

    /// Managed `InvalidateGpuClockOffsetConfirmationCore`, at a resume: a
    /// confirmation from before the low-power boundary is no longer evidence.
    /// Returns whether the state needs revalidation.
    pub(super) fn invalidate(&mut self) -> bool {
        let revalidate = matches!(self.state, State::Managed | State::ResetConfirmed);
        match self.state {
            State::Managed => self.confirmed = None,
            State::ResetConfirmed => self.state = State::Unknown,
            State::RelinquishedDefaultConfirmed => self.state = State::Relinquished,
            _ => {}
        }
        revalidate
    }

    /// Managed `RearmUncertainGpuClockOffsetResolution`: a power change
    /// allows one more query whatever the dGPU activity.
    pub(super) fn rearm(&mut self) {
        if self.state == State::SubmissionUncertain {
            self.ungated_query_attempted = false;
        }
    }

    /// Managed `ResolveUncertainGpuClockOffsetSubmissionCore`: one read-only
    /// query, or nothing when the driver cannot be read.
    fn resolve_uncertain(&mut self) -> Option<Capabilities> {
        let observed = self.controller.query()?;
        self.resolve_observation(&observed)
    }

    /// Managed `ResolveUncertainGpuClockOffsetObservationCore`. Defaults
    /// after a reset, or after a write that left a confirmed default in
    /// place, restore that confirmed default. Anything else is offsets
    /// OpenBlade submitted, so it is `Managed`, with the reading clipped to
    /// the envelope: a reading outside it is managed with no confirmation.
    fn resolve_observation(&mut self, observed: &Capabilities) -> Option<Capabilities> {
        if observed.current() == GpuClockOffsets::default()
            && (self.uncertain_reset
                || matches!(
                    self.uncertain_prior,
                    State::ResetConfirmed | State::RelinquishedDefaultConfirmed
                ))
        {
            self.state = if self.uncertain_reset {
                State::ResetConfirmed
            } else {
                self.uncertain_prior
            };
            self.confirmed = None;
            return None;
        }
        self.state = State::Managed;
        self.confirmed = self.policy.clip(observed);
        self.confirmed.clone()
    }
}

/// How a [`Worker`] call ended.
#[derive(Debug, PartialEq, Eq)]
pub(super) enum Called<R> {
    Done(R),
    /// The call never started: the worker was busy with another and one more
    /// was queued, it had stopped, or the budget ran out first. Nothing was
    /// submitted.
    NotStarted,
    /// The call started and returned no result: the budget ran out, so it
    /// finishes on the worker, which records what the driver did, or it
    /// panicked, and the worker recovered its state. A write is
    /// indeterminate to its caller.
    Abandoned,
}

const QUEUED: u8 = 0;
const STARTED: u8 = 1;
const WITHDRAWN: u8 = 2;

/// A caller's hold on its queued call. The worker starts the call only by
/// moving it from queued to started; the caller withdraws it when it stops
/// waiting, and on drop, so a cancelled call never starts later with nobody
/// observing it.
struct Claim(Arc<AtomicU8>);

impl Claim {
    /// Whether the call was withdrawn before it started.
    fn withdraw(&self) -> bool {
        self.0
            .compare_exchange(QUEUED, WITHDRAWN, Ordering::AcqRel, Ordering::Acquire)
            .is_ok()
    }

    fn started(&self) -> bool {
        self.0.load(Ordering::Acquire) == STARTED
    }
}

impl Drop for Claim {
    fn drop(&mut self) {
        self.withdraw();
    }
}

/// One call, run on the worker with its management.
type Run<N> = Box<dyn FnOnce(&mut Management<N>) + Send>;
type Job<N> = (Arc<AtomicU8>, Run<N>);

/// The thread that owns one [`Management`]. At most one call runs and one
/// waits; a further call is refused at once. Dropping the worker stops it
/// after the call in progress, without waiting: nothing is written at Stop.
pub(super) struct Worker<N: 'static> {
    jobs: SyncSender<Job<N>>,
    stopped: Arc<AtomicBool>,
}

impl<N: 'static> Drop for Worker<N> {
    fn drop(&mut self) {
        // A queued call must not start once its owner has gone.
        self.stopped.store(true, Ordering::Release);
    }
}

impl<N: Nvml + 'static> Worker<N> {
    /// Starts the thread and builds the management on it, so the NVML
    /// library handle lives where it is called.
    pub(super) fn spawn(
        build: impl FnOnce() -> Management<N> + Send + 'static,
    ) -> std::io::Result<Self> {
        let (jobs, queue) = sync_channel::<Job<N>>(1);
        let stopped = Arc::new(AtomicBool::new(false));
        let running = stopped.clone();
        thread::Builder::new()
            .name("openblade-gpu-clock-offsets".into())
            .spawn(move || run(build(), queue, &running))?;
        Ok(Self { jobs, stopped })
    }

    /// Runs `job` on the worker within `budget`. Dropping the future before
    /// the call starts withdraws it; dropping it after leaves the call to
    /// finish on the worker, as when the budget runs out.
    pub(super) async fn call<R: Send + 'static>(
        &self,
        budget: Duration,
        job: impl FnOnce(&mut Management<N>) -> R + Send + 'static,
    ) -> Called<R> {
        let claim = Claim(Arc::new(AtomicU8::new(QUEUED)));
        let (reply, mut result) = oneshot::channel();
        let job: Run<N> = Box::new(move |management| {
            let _ = reply.send(job(management));
        });
        match self.jobs.try_send((claim.0.clone(), job)) {
            Ok(()) => {}
            Err(TrySendError::Full(_) | TrySendError::Disconnected(_)) => {
                return Called::NotStarted;
            }
        }
        match tokio::time::timeout(budget, &mut result).await {
            Ok(Ok(value)) => Called::Done(value),
            // A started call that sent nothing panicked; one never started
            // was dropped unrun by a stopped worker.
            Ok(Err(_)) if claim.started() => Called::Abandoned,
            Ok(Err(_)) => Called::NotStarted,
            Err(_) if claim.withdraw() => Called::NotStarted,
            // It started; it may have just finished.
            Err(_) => match result.try_recv() {
                Ok(value) => Called::Done(value),
                Err(_) => Called::Abandoned,
            },
        }
    }

    /// [`call`](Self::call) for a caller off the async runtime, or one that
    /// must measure `budget` on the real clock: it blocks this thread until
    /// the call ends or `budget` runs out, with the same withdrawal,
    /// abandonment and panic rules.
    #[cfg(feature = "service-gpu-clock-tuning-v1")]
    pub(super) fn call_blocking<R: Send + 'static>(
        &self,
        budget: Duration,
        job: impl FnOnce(&mut Management<N>) -> R + Send + 'static,
    ) -> Called<R> {
        let claim = Claim(Arc::new(AtomicU8::new(QUEUED)));
        let (reply, result) = sync_channel(1);
        let job: Run<N> = Box::new(move |management| {
            let _ = reply.send(job(management));
        });
        match self.jobs.try_send((claim.0.clone(), job)) {
            Ok(()) => {}
            Err(TrySendError::Full(_) | TrySendError::Disconnected(_)) => {
                return Called::NotStarted;
            }
        }
        use std::sync::mpsc::RecvTimeoutError;
        match result.recv_timeout(budget) {
            Ok(value) => Called::Done(value),
            // As in `call`: a started call that sent nothing panicked.
            Err(RecvTimeoutError::Disconnected) if claim.started() => Called::Abandoned,
            Err(RecvTimeoutError::Disconnected) => Called::NotStarted,
            Err(RecvTimeoutError::Timeout) if claim.withdraw() => Called::NotStarted,
            Err(RecvTimeoutError::Timeout) => match result.try_recv() {
                Ok(value) => Called::Done(value),
                Err(_) => Called::Abandoned,
            },
        }
    }
}

/// The production worker: System32 NVML and the WDDM activity counters on
/// exact RZ09-0581's envelope, both built on the worker thread.
#[cfg(all(windows, not(test)))]
pub(super) fn production() -> std::io::Result<Worker<Box<dyn Nvml>>> {
    Worker::spawn(|| {
        Management::new(
            Box::new(windows::WindowsNvml::default()) as Box<dyn Nvml>,
            windows::activity(),
            RZ09_0581,
        )
    })
}

fn run<N: Nvml>(mut management: Management<N>, queue: Receiver<Job<N>>, stopped: &AtomicBool) {
    // The queue disconnects when the worker is dropped; a call still queued
    // then is dropped unrun.
    while let Ok((claim, job)) = queue.recv() {
        if stopped.load(Ordering::Acquire) {
            break;
        }
        if claim
            .compare_exchange(QUEUED, STARTED, Ordering::AcqRel, Ordering::Acquire)
            .is_err()
        {
            continue;
        }
        // A panicking call must not end the worker: its in-memory record of
        // an uncertain submission is the only one. The management is
        // recovered before the next call, so a broken invariant is not
        // observed.
        if panic::catch_unwind(AssertUnwindSafe(|| job(&mut management))).is_err() {
            management.recover_from_panic();
        }
    }
}

#[cfg(test)]
#[path = "gpu_clock_offsets_fake.rs"]
pub(super) mod fake;
#[cfg(test)]
#[path = "gpu_clock_offsets_tests.rs"]
mod tests;

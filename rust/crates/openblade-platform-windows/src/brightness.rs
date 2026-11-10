//! Current-user internal-panel WMI brightness policy and native operation boundary.

use std::time::{Duration, Instant};

use crate::display::{DisplayReadError, EndpointId, InternalDisplayTopology};

#[cfg(test)]
mod tests;
mod windows;
mod worker;

use worker::RequestGate;
pub use worker::WindowsBrightnessWorker;

const CALL_LIMIT: Duration = Duration::from_secs(2);
const MAX_ROWS: usize = 64;
const MAX_LEVEL_ENTRIES: usize = 256;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum BrightnessRequest {
    SetPercent(u8),
    StepDown,
    StepUp,
}

#[derive(Debug, PartialEq, Eq)]
pub struct BrightnessSnapshot {
    pub percent: u8,
    pub supported_levels: Vec<u8>,
}

#[derive(Debug, PartialEq, Eq)]
pub enum BrightnessOutcome {
    Unchanged(BrightnessSnapshot),
    Confirmed(BrightnessSnapshot),
}

#[derive(Debug, PartialEq, Eq)]
pub struct BrightnessFailure {
    pub reason: BrightnessError,
    pub submission_may_have_occurred: bool,
    pub state: Option<BrightnessSnapshot>,
}

#[derive(Debug, PartialEq, Eq)]
pub enum BrightnessError {
    InvalidPercent,
    Topology(DisplayReadError),
    PanelUnavailable,
    AmbiguousPanel,
    PanelChanged,
    ReadingUnavailable,
    AmbiguousReading,
    InvalidReading,
    ControllerUnavailable,
    AmbiguousController,
    UnsupportedLevel,
    Native(NativeFailure),
    Rejected(u32),
    InvalidAcknowledgement,
    Unconfirmed,
    SubmissionUnresolved,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum NativeFailure {
    Cancelled,
    DeadlineExceeded,
    Status(i32),
    InvalidData,
    MissingData,
    AmbiguousData,
    WorkerBusy,
    WorkerDisconnected,
    SubmissionAlreadyStarted,
}

#[derive(Debug, PartialEq, Eq)]
pub enum Acknowledgement {
    Status(u32),
    Boolean(bool),
    Invalid,
}

/// A decoded row from the active WmiMonitorBrightness query. Missing or
/// incorrectly typed properties remain absent instead of becoming zero.
pub struct ReadingRow {
    pub instance: Option<String>,
    pub percent: Option<u8>,
    pub levels: Option<Vec<u8>>,
}

/// A decoded active WmiMonitorBrightnessMethods row. The handle denotes this
/// exact WMI object path, including the full InstanceName key.
pub struct MethodRow<T> {
    pub instance: Option<String>,
    pub handle: T,
}

pub struct CallBudget<'a> {
    deadline: Instant,
    cancelled: &'a dyn Fn() -> bool,
    submission_gate: Option<&'a RequestGate>,
}

impl CallBudget<'_> {
    /// Use the remaining time to bound waits. A provider request that outlives
    /// this budget must stay isolated from subsequent operations.
    pub fn remaining(&self) -> Result<Duration, NativeFailure> {
        if (self.cancelled)() {
            return Err(NativeFailure::Cancelled);
        }
        time_left(self.deadline)
    }
}

fn time_left(deadline: Instant) -> Result<Duration, NativeFailure> {
    deadline
        .checked_duration_since(Instant::now())
        .filter(|remaining| !remaining.is_zero())
        .ok_or(NativeFailure::DeadlineExceeded)
}

/// The native layer calls begin immediately before its one possible
/// WmiSetBrightness invocation. All fallible method preparation precedes it.
pub struct Submission<'a> {
    budget: &'a CallBudget<'a>,
    completion_deadline: Option<Instant>,
}

impl Submission<'_> {
    pub fn begin(&mut self) -> Result<(), NativeFailure> {
        if self.completion_deadline.is_some() {
            return Err(NativeFailure::SubmissionAlreadyStarted);
        }
        self.budget.remaining()?;
        if let Some(gate) = self.budget.submission_gate {
            gate.begin()?;
        }
        self.completion_deadline = Some(Instant::now() + CALL_LIMIT);
        Ok(())
    }

    /// A started call has a separate completion window and ignores caller
    /// cancellation. The transport returns a response or a timeout from that wait.
    pub fn remaining(&self) -> Result<Duration, NativeFailure> {
        match self.completion_deadline {
            Some(deadline) => time_left(deadline),
            None => self.budget.remaining(),
        }
    }
}

#[derive(Debug, PartialEq, Eq)]
pub enum TopologyFailure {
    Display(DisplayReadError),
    Native(NativeFailure),
}

/// Current-user Windows boundary. Implementations must stop enumeration at the
/// supplied row limit (return InvalidData on overflow), honor the shared
/// deadline, and never enqueue a late mutation or replay an invocation.
/// The same instance owns display settings and function-row brightness.
pub trait WmiBrightnessNative {
    type Method;

    fn topology(
        &mut self,
        budget: &CallBudget<'_>,
    ) -> Result<InternalDisplayTopology, TopologyFailure>;

    /// Decode the active Root\WMI WmiMonitorBrightness query. Bound each raw
    /// Level array to 256 entries before allocation.
    fn active_readings(
        &mut self,
        budget: &CallBudget<'_>,
        row_limit: usize,
    ) -> Result<Vec<ReadingRow>, NativeFailure>;

    /// Decode the active Root\WMI WmiMonitorBrightnessMethods query. Keep
    /// the native object path opaque and avoid interpolating instance strings.
    fn active_methods(
        &mut self,
        budget: &CallBudget<'_>,
        row_limit: usize,
    ) -> Result<Vec<MethodRow<Self::Method>>, NativeFailure>;

    /// Set the exact level on the admitted method object with WMI Timeout=0.
    /// Return native errors with their status and use the submission boundary
    /// even when the call ultimately rejects the request.
    fn set_brightness(
        &mut self,
        method: Self::Method,
        level: u8,
        submission: &mut Submission<'_>,
    ) -> Result<Acknowledgement, NativeFailure>;
}

pub struct WmiBrightness<N> {
    native: N,
    pause: Option<WritePause>,
}

enum WritePause {
    NativeUncertain,
    AwaitingReadback(SubmittedTarget),
}

struct SubmittedTarget {
    panel: Panel,
    instance: String,
    percent: u8,
}

impl SubmittedTarget {
    fn matches_panel(&self, panel: &Panel, reading: &Reading) -> bool {
        self.panel.same_as(panel) && self.instance.eq_ignore_ascii_case(&reading.instance)
    }
}

impl<N: WmiBrightnessNative> WmiBrightness<N> {
    pub fn new(native: N) -> Self {
        Self {
            native,
            pause: None,
        }
    }

    /// The caller can still query state after an uncertain mutation. A read
    /// alone cannot establish that a timed-out provider invocation has drained.
    pub fn read(
        &mut self,
        cancelled: impl Fn() -> bool,
    ) -> Result<BrightnessSnapshot, BrightnessError> {
        self.read_with_budget(&budget(&cancelled))
    }

    fn read_with_budget(
        &mut self,
        admission: &CallBudget<'_>,
    ) -> Result<BrightnessSnapshot, BrightnessError> {
        let (panel, reading) = self.observe(admission)?;
        if let Some(WritePause::AwaitingReadback(submitted)) = &self.pause
            && submitted.matches_panel(&panel, &reading)
            && submitted.percent == reading.snapshot.percent
        {
            self.pause = None;
        }
        Ok(reading.snapshot)
    }

    /// Resolve intent and perform at most one write. Keep the same owner alive
    /// across held-key repeats and subsequent settings requests.
    pub fn apply(
        &mut self,
        request: BrightnessRequest,
        cancelled: impl Fn() -> bool,
    ) -> Result<BrightnessOutcome, BrightnessFailure> {
        self.apply_with_budget(request, &budget(&cancelled))
    }

    fn apply_with_budget(
        &mut self,
        request: BrightnessRequest,
        admission: &CallBudget<'_>,
    ) -> Result<BrightnessOutcome, BrightnessFailure> {
        let mut submitted = false;
        let mut state = None;
        let result = self.apply_core(request, admission, &mut submitted, &mut state);
        result.map_err(|reason| BrightnessFailure {
            reason,
            submission_may_have_occurred: submitted,
            state,
        })
    }

    fn apply_core(
        &mut self,
        request: BrightnessRequest,
        admission: &CallBudget<'_>,
        submitted: &mut bool,
        state: &mut Option<BrightnessSnapshot>,
    ) -> Result<BrightnessOutcome, BrightnessError> {
        if matches!(request, BrightnessRequest::SetPercent(101..)) {
            return Err(BrightnessError::InvalidPercent);
        }
        if self.pause.is_some() {
            return Err(BrightnessError::SubmissionUnresolved);
        }

        let (panel, baseline) = self.observe(admission)?;
        let target = select_level(request, &baseline.snapshot);
        let Some(target) = target.filter(|target| *target != baseline.snapshot.percent) else {
            return Ok(BrightnessOutcome::Unchanged(baseline.snapshot));
        };
        *state = Some(baseline.snapshot);

        let methods = checked(admission, |budget| {
            self.native
                .active_methods(budget, MAX_ROWS)
                .map_err(BrightnessError::Native)
        })?;
        let method = unique_matching(
            methods,
            |row| row.instance.as_deref(),
            |name| name.eq_ignore_ascii_case(&baseline.instance),
            BrightnessError::ControllerUnavailable,
            BrightnessError::AmbiguousController,
        )?
        .handle;

        *state = None;
        let (fresh_panel, fresh) = self.observe(admission)?;
        if !panel.same_as(&fresh_panel) || !fresh.instance.eq_ignore_ascii_case(&baseline.instance)
        {
            return Err(BrightnessError::PanelChanged);
        }
        if matches!(request, BrightnessRequest::SetPercent(_))
            && !fresh.snapshot.supported_levels.contains(&target)
        {
            *state = Some(fresh.snapshot);
            return Err(BrightnessError::UnsupportedLevel);
        }
        let Some(target) =
            select_level(request, &fresh.snapshot).filter(|level| *level != fresh.snapshot.percent)
        else {
            return Ok(BrightnessOutcome::Unchanged(fresh.snapshot));
        };
        // WMI enumeration may outlive the CCD observation used to select its
        // row. Recheck topology before allowing native submission.
        if !panel.same_as(&self.panel(admission)?) {
            return Err(BrightnessError::PanelChanged);
        }
        *state = Some(fresh.snapshot);

        let mut submission = Submission {
            budget: admission,
            completion_deadline: None,
        };
        let response = self.native.set_brightness(method, target, &mut submission);
        *submitted = submission.completion_deadline.is_some();
        if !*submitted {
            return Err(BrightnessError::Native(
                response.err().unwrap_or(NativeFailure::InvalidData),
            ));
        }

        let ack = response
            .map_err(BrightnessError::Native)
            .and_then(|ack| match ack {
                Acknowledgement::Status(0) | Acknowledgement::Boolean(_) => Ok(()),
                Acknowledgement::Status(status) => Err(BrightnessError::Rejected(status)),
                Acknowledgement::Invalid => Err(BrightnessError::InvalidAcknowledgement),
            });

        // Always replace the pre-write snapshot with an independent observation
        // or no state. A caller's cancellation cannot discard this readback.
        *state = None;
        let target = SubmittedTarget {
            panel,
            instance: baseline.instance,
            percent: target,
        };
        let readback = self
            .observe(&budget(&|| false))
            .and_then(|(panel, observed)| {
                if target.matches_panel(&panel, &observed) {
                    Ok(observed.snapshot)
                } else {
                    Err(BrightnessError::PanelChanged)
                }
            });

        let readback = match readback {
            Ok(observed) if ack.is_ok() && observed.percent == target.percent => {
                return Ok(BrightnessOutcome::Confirmed(observed));
            }
            other => other,
        };
        self.pause = Some(if ack.is_ok() {
            WritePause::AwaitingReadback(target)
        } else {
            WritePause::NativeUncertain
        });
        match readback {
            Ok(observed) => {
                *state = Some(observed);
                Err(ack.err().unwrap_or(BrightnessError::Unconfirmed))
            }
            Err(error) => Err(ack.err().unwrap_or(error)),
        }
    }

    fn observe(&mut self, budget: &CallBudget<'_>) -> Result<(Panel, Reading), BrightnessError> {
        let panel = self.panel(budget)?;
        let rows = checked(budget, |budget| {
            self.native
                .active_readings(budget, MAX_ROWS)
                .map_err(BrightnessError::Native)
        })?;
        let row = unique_matching(
            rows,
            |row| row.instance.as_deref(),
            |name| panel.matches(name),
            BrightnessError::ReadingUnavailable,
            BrightnessError::AmbiguousReading,
        )?;
        Ok((panel, Reading::decode(row)?))
    }

    fn panel(&mut self, budget: &CallBudget<'_>) -> Result<Panel, BrightnessError> {
        let topology = checked(budget, |budget| {
            self.native.topology(budget).map_err(|error| match error {
                TopologyFailure::Display(error) => BrightnessError::Topology(error),
                TopologyFailure::Native(error) => BrightnessError::Native(error),
            })
        })?;
        Panel::from_topology(topology)
    }
}

fn budget(cancelled: &dyn Fn() -> bool) -> CallBudget<'_> {
    CallBudget {
        deadline: Instant::now() + CALL_LIMIT,
        cancelled,
        submission_gate: None,
    }
}

fn checked<T>(
    budget: &CallBudget<'_>,
    read: impl FnOnce(&CallBudget<'_>) -> Result<T, BrightnessError>,
) -> Result<T, BrightnessError> {
    budget.remaining().map_err(BrightnessError::Native)?;
    let value = read(budget)?;
    budget.remaining().map_err(BrightnessError::Native)?;
    Ok(value)
}

struct Panel {
    source: EndpointId,
    target: EndpointId,
    path: String,
    instance_prefix: String,
}

impl Panel {
    fn from_topology(topology: InternalDisplayTopology) -> Result<Self, BrightnessError> {
        let display = match topology {
            InternalDisplayTopology::Single(display) => display,
            InternalDisplayTopology::MultipleInternalDisplays => {
                return Err(BrightnessError::AmbiguousPanel);
            }
            _ => return Err(BrightnessError::PanelUnavailable),
        };
        let path = display
            .monitor_name
            .ok()
            .and_then(|name| name.device_path)
            .and_then(|path| path.into_string().ok())
            .ok_or(BrightnessError::PanelUnavailable)?;
        let device = path.strip_prefix(r"\\?\").unwrap_or(&path);
        let mut parts = device.split('#');
        let (Some(kind), Some(model), Some(device_instance)) =
            (parts.next(), parts.next(), parts.next())
        else {
            return Err(BrightnessError::PanelUnavailable);
        };
        if !kind.eq_ignore_ascii_case("DISPLAY")
            || [model, device_instance]
                .iter()
                .any(|part| !valid_identity(part) || part.contains('\\'))
        {
            return Err(BrightnessError::PanelUnavailable);
        }
        let instance_prefix = format!("{kind}\\{model}\\{device_instance}");
        Ok(Self {
            source: display.source,
            target: display.target,
            path,
            instance_prefix,
        })
    }

    fn same_as(&self, other: &Self) -> bool {
        self.source == other.source
            && self.target == other.target
            && self.path.eq_ignore_ascii_case(&other.path)
    }

    fn matches(&self, name: &str) -> bool {
        if self.instance_prefix.eq_ignore_ascii_case(name) {
            return true;
        }
        name.rsplit_once('_').is_some_and(|(prefix, suffix)| {
            self.instance_prefix.eq_ignore_ascii_case(prefix)
                && !suffix.is_empty()
                && suffix.bytes().all(|byte| byte.is_ascii_digit())
        })
    }
}

struct Reading {
    instance: String,
    snapshot: BrightnessSnapshot,
}

impl Reading {
    fn decode(row: ReadingRow) -> Result<Self, BrightnessError> {
        let (Some(instance), Some(percent @ 0..=100), Some(mut levels)) =
            (row.instance, row.percent, row.levels)
        else {
            return Err(BrightnessError::InvalidReading);
        };
        if levels.len() > MAX_LEVEL_ENTRIES {
            return Err(BrightnessError::InvalidReading);
        }
        levels.retain(|level| *level <= 100);
        levels.sort_unstable();
        levels.dedup();
        if levels.is_empty() {
            return Err(BrightnessError::InvalidReading);
        }
        Ok(Self {
            instance,
            snapshot: BrightnessSnapshot {
                percent,
                supported_levels: levels,
            },
        })
    }
}

fn valid_identity(name: &str) -> bool {
    !name.is_empty() && name.len() <= 512 && name.bytes().all(|byte| byte.is_ascii_graphic())
}

fn unique_matching<T>(
    rows: Vec<T>,
    identity: impl Fn(&T) -> Option<&str>,
    matches: impl Fn(&str) -> bool,
    missing: BrightnessError,
    ambiguous: BrightnessError,
) -> Result<T, BrightnessError> {
    if rows.len() > MAX_ROWS {
        return Err(BrightnessError::Native(NativeFailure::InvalidData));
    }
    let mut selected = None;
    for row in rows {
        let name = identity(&row)
            .filter(|name| !name.trim().is_empty())
            .ok_or(BrightnessError::Native(NativeFailure::InvalidData))?;
        if matches(name) {
            if !valid_identity(name) {
                return Err(BrightnessError::Native(NativeFailure::InvalidData));
            }
            if selected.replace(row).is_some() {
                return Err(ambiguous);
            }
        }
    }
    selected.ok_or(missing)
}

fn select_level(request: BrightnessRequest, snapshot: &BrightnessSnapshot) -> Option<u8> {
    let current = snapshot.percent;
    let levels = snapshot.supported_levels.iter().copied();
    match request {
        BrightnessRequest::SetPercent(percent) => {
            levels.min_by_key(|level| (level.abs_diff(percent), *level))
        }
        BrightnessRequest::StepDown => {
            let mut lower = levels.rev().filter(|level| *level < current);
            lower
                .find(|level| *level <= current.saturating_sub(10))
                .or_else(|| {
                    snapshot
                        .supported_levels
                        .first()
                        .copied()
                        .filter(|level| *level < current)
                })
        }
        BrightnessRequest::StepUp => {
            let mut higher = levels.filter(|level| *level > current);
            higher
                .find(|level| *level >= current.saturating_add(10).min(100))
                .or_else(|| {
                    snapshot
                        .supported_levels
                        .last()
                        .copied()
                        .filter(|level| *level > current)
                })
        }
    }
}

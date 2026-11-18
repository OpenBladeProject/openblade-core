//! Current-user internal-panel WMI brightness policy and native operation boundary.

use std::time::{Duration, Instant};

use crate::display::{
    ActiveInternalDisplay, DisplayReadError, EndpointId, InternalDisplayTopology,
};

#[cfg(test)]
mod tests;
mod windows;
mod worker;

pub use windows::{ProcessSecurityApartment, initialize_process_security, process_security_status};
use worker::RequestGate;
pub use worker::WindowsBrightnessWorker;

/// The exact CCD endpoints and monitor path selected by the display owner.
pub struct BrightnessTarget(Panel);

impl BrightnessTarget {
    pub fn from_display(display: &ActiveInternalDisplay) -> Result<Self, BrightnessError> {
        Panel::from_display(display).map(Self)
    }
}

/// CCD identity admitted for observation only. It cannot be supplied to a setter.
pub struct BrightnessQueryTarget {
    source: EndpointId,
    target: EndpointId,
    path: String,
    instance_prefix: String,
}

impl BrightnessQueryTarget {
    pub fn from_display(display: &ActiveInternalDisplay) -> Result<Self, BrightnessError> {
        let path = display
            .monitor_name
            .as_ref()
            .ok()
            .and_then(|name| name.device_path.as_ref())
            .and_then(|path| path.to_str())
            .ok_or(BrightnessError::PanelUnavailable)?;
        let mut parts = path.strip_prefix(r"\\?\").unwrap_or(path).split('#');
        let (Some(kind), Some(model), Some(instance)) = (parts.next(), parts.next(), parts.next())
        else {
            return Err(BrightnessError::PanelUnavailable);
        };
        if !kind.eq_ignore_ascii_case("DISPLAY") || path.encode_utf16().count() > 512 {
            return Err(BrightnessError::PanelUnavailable);
        }
        Ok(Self {
            source: display.source,
            target: display.target,
            path: path.to_owned(),
            instance_prefix: format!("{kind}\\{model}\\{instance}"),
        })
    }

    fn matches(&self, name: &str) -> bool {
        if name.encode_utf16().count() > 512 {
            return false;
        }
        // WindowsDisplayBrightness.MatchesMonitor removes the last underscore
        // suffix only when it parses as a signed 32-bit integer.
        let bare = match name.rsplit_once('_') {
            Some((prefix, suffix))
                if !prefix.is_empty()
                    && suffix
                        .trim_matches(|c: char| matches!(c, ' ' | '\t'..='\r'))
                        .parse::<i32>()
                        .is_ok() =>
            {
                prefix
            }
            _ => name,
        };
        windows::ordinal_equal(&self.instance_prefix, bare)
    }
}

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
    /// The fenced fresh topology no longer identifies the admitted panel.
    TargetChanged,
    /// The host's lifetime, session or power admission no longer holds.
    AdmissionDenied,
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
    /// A row with a known identity may have missing or malformed values.
    pub data_failure: Option<NativeFailure>,
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
    fence: Option<&'a dyn SubmissionFence>,
}

/// Re-checked at the one possible WmiSetBrightness invocation, after method
/// rebinding and parameter preparation. Either refusal occurs before the
/// request gate opens, so it can never be a possible submission.
pub use crate::display::SubmissionFence;

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
    panel: Option<&'a Panel>,
}

impl Submission<'_> {
    pub fn begin(&mut self) -> Result<(), NativeFailure> {
        if self.completion_deadline.is_some() {
            return Err(NativeFailure::SubmissionAlreadyStarted);
        }
        self.budget.remaining()?;
        if let Some(fence) = self.budget.fence {
            let fresh = fence
                .topology()
                .ok()
                .and_then(|topology| Panel::from_topology(topology).ok());
            if !self
                .panel
                .zip(fresh.as_ref())
                .is_some_and(|(admitted, fresh)| admitted.same_as(fresh))
            {
                return Err(NativeFailure::TargetChanged);
            }
            if !fence.admit() {
                return Err(NativeFailure::AdmissionDenied);
            }
            self.budget.remaining()?;
        }
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

    /// Query-only projection of active readings. Native WMI uses the same three
    /// selected properties as WindowsDisplayBrightness.Query. The stricter
    /// schema and identity checks for write admission live in active_readings.
    fn query_readings(
        &mut self,
        budget: &CallBudget<'_>,
        row_limit: usize,
    ) -> Result<Vec<ReadingRow>, NativeFailure> {
        self.active_readings(budget, row_limit)
    }

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
        self.read_with_budget(None, &budget(&cancelled))
    }

    fn read_with_budget(
        &mut self,
        expected: Option<&BrightnessTarget>,
        admission: &CallBudget<'_>,
    ) -> Result<BrightnessSnapshot, BrightnessError> {
        let (panel, reading) = self.observe(expected, admission)?;
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
        self.apply_with_budget(request, None, &budget(&cancelled))
    }

    fn apply_with_budget(
        &mut self,
        request: BrightnessRequest,
        expected: Option<&BrightnessTarget>,
        admission: &CallBudget<'_>,
    ) -> Result<BrightnessOutcome, BrightnessFailure> {
        let mut submitted = false;
        let mut state = None;
        let result = self.apply_core(request, expected, admission, &mut submitted, &mut state);
        result.map_err(|reason| BrightnessFailure {
            reason,
            submission_may_have_occurred: submitted,
            state,
        })
    }

    fn apply_core(
        &mut self,
        request: BrightnessRequest,
        expected: Option<&BrightnessTarget>,
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

        let (panel, baseline) = self.observe(expected, admission)?;
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
        let (fresh_panel, fresh) = self.observe(expected, admission)?;
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
            panel: Some(&panel),
        };
        let response = self.native.set_brightness(method, target, &mut submission);
        *submitted = submission.completion_deadline.is_some();
        if !*submitted {
            return Err(match response.err().unwrap_or(NativeFailure::InvalidData) {
                NativeFailure::TargetChanged => BrightnessError::PanelChanged,
                failure => BrightnessError::Native(failure),
            });
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
            .observe(expected, &budget(&|| false))
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

    fn observe(
        &mut self,
        expected: Option<&BrightnessTarget>,
        budget: &CallBudget<'_>,
    ) -> Result<(Panel, Reading), BrightnessError> {
        let panel = self.panel(budget)?;
        if expected.is_some_and(|target| !target.0.same_as(&panel)) {
            return Err(BrightnessError::PanelChanged);
        }
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
        // A composed display snapshot must not carry a WMI observation made
        // across a target replacement, even when the instance prefix is reused.
        if expected.is_some() && !panel.same_as(&self.panel(budget)?) {
            return Err(BrightnessError::PanelChanged);
        }
        Ok((panel, Reading::decode(row)?))
    }

    fn query(
        &mut self,
        expected: &BrightnessQueryTarget,
        budget: &CallBudget<'_>,
    ) -> Result<BrightnessSnapshot, BrightnessError> {
        self.verify_query_target(expected, budget)?;
        let rows = checked(budget, |budget| {
            self.native
                .query_readings(budget, MAX_ROWS)
                .map_err(BrightnessError::Native)
        })?;
        self.verify_query_target(expected, budget)?;
        if rows.len() > MAX_ROWS {
            return Err(BrightnessError::Native(NativeFailure::InvalidData));
        }
        let mut readings = rows
            .into_iter()
            .filter(|row| {
                row.instance
                    .as_deref()
                    .is_some_and(|name| expected.matches(name))
            })
            .filter_map(|row| Reading::decode(row).ok());
        let reading = readings.next().ok_or(BrightnessError::ReadingUnavailable)?;
        if readings.next().is_some() {
            return Err(BrightnessError::AmbiguousReading);
        }
        Ok(reading.snapshot)
    }

    fn verify_query_target(
        &mut self,
        expected: &BrightnessQueryTarget,
        budget: &CallBudget<'_>,
    ) -> Result<(), BrightnessError> {
        let InternalDisplayTopology::Single(display) = self.topology(budget)? else {
            return Err(BrightnessError::PanelChanged);
        };
        if display.source != expected.source
            || display.target != expected.target
            || !display
                .monitor_name
                .as_ref()
                .ok()
                .and_then(|name| name.device_path.as_ref())
                .and_then(|p| p.to_str())
                .is_some_and(|path| windows::ordinal_equal(path, &expected.path))
        {
            return Err(BrightnessError::PanelChanged);
        }
        Ok(())
    }

    fn panel(&mut self, budget: &CallBudget<'_>) -> Result<Panel, BrightnessError> {
        Panel::from_topology(self.topology(budget)?)
    }

    fn topology(
        &mut self,
        budget: &CallBudget<'_>,
    ) -> Result<InternalDisplayTopology, BrightnessError> {
        checked(budget, |budget| {
            self.native.topology(budget).map_err(|error| match error {
                TopologyFailure::Display(error) => BrightnessError::Topology(error),
                TopologyFailure::Native(error) => BrightnessError::Native(error),
            })
        })
    }
}

fn budget(cancelled: &dyn Fn() -> bool) -> CallBudget<'_> {
    CallBudget {
        deadline: Instant::now() + CALL_LIMIT,
        cancelled,
        submission_gate: None,
        fence: None,
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
        Self::from_display(&display)
    }

    fn from_display(display: &ActiveInternalDisplay) -> Result<Self, BrightnessError> {
        let path = display
            .monitor_name
            .as_ref()
            .ok()
            .and_then(|name| name.device_path.as_ref())
            .and_then(|path| path.to_str())
            .ok_or(BrightnessError::PanelUnavailable)?;
        let device = path.strip_prefix(r"\\?\").unwrap_or(path);
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
            path: path.to_owned(),
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
        if let Some(failure) = row.data_failure {
            return Err(BrightnessError::Native(failure));
        }
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

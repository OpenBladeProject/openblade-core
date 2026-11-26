//! Managed display-brightness HID input on exact RZ09-0581: the caching half
//! of `WindowsRazerDisplayBrightnessHidTransport`
//! (src/OpenBlade.Service/WindowsRazerDisplayBrightnessHidTransport.cs:90-173),
//! the reply mapping and warning of `DisplayBrightnessHidController` (62-97)
//! and the held-usage state machine of `ServiceControlRuntime`
//! (`SetDisplayBrightnessHidUsageAsync` and its release core, 3409-3510).
//!
//! The usage reaches the laptop as RzCommon's type-10 Consumer Control
//! input: one IOCTL whose completion is the only confirmation the driver
//! API gives. There is no read of the input state, so an IOCTL that may have
//! been submitted is never replayed: a press that may have reached the
//! driver is recorded as held before it is started, and only a confirmed
//! release clears it. A press withdrawn before it started submitted nothing
//! and leaves the held state as it was.
//!
//! Every Windows call runs on one of the lane's two threads (`Lane`), never
//! on the Service runtime: submissions on one, in order, and availability
//! resolutions on the other, so a probe never delays a press or a release.
//! Managed serializes the two under one lock; a resolution shares no handle
//! with a write and never seeds its path cache, so native runs them apart.
//! The resolution and the write (`Device`) are
//! `display_brightness_hid_windows.rs`'s; tests script them.

use std::{
    cell::RefCell,
    collections::HashMap,
    io,
    panic::{self, AssertUnwindSafe},
    sync::{
        Arc, Mutex,
        atomic::{AtomicBool, AtomicU64, Ordering},
        mpsc::{Receiver, SyncSender, sync_channel},
    },
    thread,
    time::Duration,
};

use tokio::time::Instant;
use windows_sys::Win32::Foundation::{
    ERROR_DEVICE_NOT_CONNECTED, ERROR_FILE_NOT_FOUND, ERROR_INVALID_HANDLE, ERROR_PATH_NOT_FOUND,
};

use super::super::super::withdrawal_lane::{Called, Claim, Lane as Calls, Recover, Waiting};
use super::super::Outcome;
pub(crate) use crate::service_status::DisplayBrightnessHidUsage as Usage;

/// Managed `SubmitInputIoctl` and the type-10 control buffer
/// (`CreateControlBuffer`).
pub(crate) const SUBMIT_INPUT_IOCTL: u32 = 0x8888_3020;
pub(crate) const CONSUMER_CONTROL_INPUT_TYPE: u32 = 10;
pub(crate) const CONTROL_BUFFER_LENGTH: usize = 32;
pub(crate) const USAGE_OFFSET: usize = 8;

/// Managed `DisplayBrightnessHidAvailability`.
pub(crate) const AVAILABILITY_UNKNOWN: u8 = 0;
pub(crate) const AVAILABILITY_AVAILABLE: u8 = 1;
pub(crate) const AVAILABILITY_UNAVAILABLE: u8 = 2;

pub(crate) const SUBMITTED: &str = "The native display-brightness input state was submitted.";
pub(crate) const PATH_UNAVAILABLE: &str =
    "The native display-brightness input path is unavailable.";
pub(crate) const UNCONFIRMED: &str = "The display-brightness HID state could not be confirmed.";
pub(crate) const REMAINS_ASSERTED: &str = "The native display-brightness input remains asserted.";
pub(crate) const DEVICE_REQUIRED: &str =
    "Native display-brightness input requires the verified admitted laptop device.";
/// Native only: the submission did not finish within its budget. It may
/// have reached the driver, or, for a release owed at a cleanup boundary,
/// it is still queued and runs after the call in progress.
pub(crate) const UNFINISHED: &str = "the submission did not finish within its budget";

/// The longest one submission may take on the lane, and what a request keeps
/// of its deadline to answer after it.
pub(crate) const CALL_BUDGET: Duration = Duration::from_secs(2);
const ANSWER_MARGIN: Duration = Duration::from_millis(250);
/// How long a published availability stands before the status observer asks
/// the lane for another resolution.
pub(crate) const AVAILABILITY_LIFETIME: Duration = Duration::from_secs(10);

/// Managed `CreateControlBuffer`: RzCommon type 10 maps the little-endian
/// usage at +8 to Consumer Control report 1.
pub(crate) fn control_buffer(usage: Usage) -> [u8; CONTROL_BUFFER_LENGTH] {
    let mut buffer = [0; CONTROL_BUFFER_LENGTH];
    buffer[4..8].copy_from_slice(&CONSUMER_CONTROL_INPUT_TYPE.to_le_bytes());
    buffer[USAGE_OFFSET..USAGE_OFFSET + 2].copy_from_slice(&usage.wire().to_le_bytes());
    buffer
}

/// Managed `DisplayBrightnessHidWriteStatus`.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) enum WriteStatus {
    Applied,
    Unavailable,
    Failed,
}

/// Managed `DisplayBrightnessHidWriteResult`.
#[derive(Clone, Debug, PartialEq, Eq)]
pub(crate) struct WriteResult {
    pub(crate) status: WriteStatus,
    pub(crate) error: Option<String>,
}

impl WriteResult {
    pub(crate) fn applied() -> Self {
        Self {
            status: WriteStatus::Applied,
            error: None,
        }
    }

    pub(crate) fn unavailable(error: Option<String>) -> Self {
        Self {
            status: WriteStatus::Unavailable,
            error,
        }
    }

    pub(crate) fn failed(error: Option<String>) -> Self {
        Self {
            status: WriteStatus::Failed,
            error,
        }
    }
}

/// Managed `Exception.Message`, as the driver checks word it.
pub(crate) use crate::driver_trust::error_message as message;

/// How far one managed `WriteUsageToPath` got before it ended.
#[derive(Clone, Debug, PartialEq, Eq)]
pub(crate) enum Reached {
    /// `CreateFile` failed with this Win32 error and its text.
    Open { error: u32, message: String },
    /// The opened path no longer validates as the exact control interface.
    Path(String),
    /// The virtual Consumer Control device did not validate.
    Consumer(WriteResult),
    /// The IOCTL ran; its failure text, or none when it succeeded.
    Ioctl(Option<String>),
}

/// Managed `WriteUsageToPath`'s result for how far the write got.
/// Unavailable arises only before the IOCTL: an absent device at the open,
/// a path that no longer validates, or a virtual device whose package is not
/// trusted. That is what lets the transport resolve once more without
/// replaying anything. An IOCTL that ran and failed is Failed whatever its
/// error, since it may have submitted.
pub(crate) fn written(reached: Reached) -> WriteResult {
    match reached {
        Reached::Open { error, message } => {
            if matches!(
                error,
                ERROR_FILE_NOT_FOUND
                    | ERROR_PATH_NOT_FOUND
                    | ERROR_INVALID_HANDLE
                    | ERROR_DEVICE_NOT_CONNECTED
            ) {
                WriteResult::unavailable(Some(message))
            } else {
                WriteResult::failed(Some(message))
            }
        }
        Reached::Path(failure) => WriteResult::unavailable(Some(failure)),
        Reached::Consumer(validation) => validation,
        Reached::Ioctl(None) => WriteResult::applied(),
        Reached::Ioctl(Some(error)) => WriteResult::failed(Some(error)),
    }
}

/// What a managed device search saw of the candidates it did not admit:
/// whether the exact device was among them, with why it failed, and the
/// last candidate that could not be inspected. Its verdicts are managed
/// `ResolveDevicePath`'s and `ValidateVirtualConsumerDevice`'s ends, which
/// tell a device that is absent (Unavailable) from one that is present but
/// unproven (Failed).
#[derive(Debug, Default)]
pub(crate) struct Search {
    exact: Option<Option<String>>,
    inspection: Option<String>,
}

impl Search {
    /// A candidate that could not be inspected.
    pub(crate) fn inspection_failed(&mut self, failure: String) {
        self.inspection = Some(failure);
    }

    /// The exact device, seen and not admitted, for `failure` or for none
    /// it could name.
    pub(crate) fn exact_seen(&mut self, failure: Option<String>) {
        self.exact = Some(failure);
    }

    /// Managed `ResolveDevicePath`'s end: an exact control interface seen
    /// fails with its own reason, whatever else failed; otherwise a failed
    /// inspection fails and nothing found is Unavailable.
    pub(crate) fn control_verdict(self) -> WriteResult {
        match (self.exact, self.inspection) {
            (Some(failure), _) => WriteResult::failed(Some(failure.unwrap_or_else(|| {
                "The exact Razer control interface failed identity validation.".into()
            }))),
            (None, None) => WriteResult::unavailable(None),
            (None, inspection) => WriteResult::failed(inspection),
        }
    }

    /// Managed `ValidateVirtualConsumerDevice`'s end: an exact device seen
    /// or a failed inspection fails, with the inspection's reason when
    /// there is one; nothing found is Unavailable.
    pub(crate) fn consumer_verdict(self) -> WriteResult {
        if self.exact.is_none() && self.inspection.is_none() {
            return WriteResult::unavailable(None);
        }
        WriteResult::failed(Some(self.inspection.unwrap_or_else(|| {
            "The exact Razer virtual Consumer Control device failed identity validation.".into()
        })))
    }
}

/// Managed `ResolveDevicePath` and `WriteUsageToPath`. An error is managed's
/// caught exception: the write reports it as Failed and availability as
/// Unknown.
pub(crate) trait Device: Send + 'static {
    /// The exact control interface path with Applied, or none with why.
    fn resolve(&mut self) -> io::Result<(Option<String>, WriteResult)>;
    /// Opens `path`, validates it and the virtual consumer device again,
    /// and submits `usage`.
    fn write(&mut self, path: &str, usage: Usage) -> io::Result<WriteResult>;
}

/// Managed `WindowsRazerDisplayBrightnessHidTransport`'s cache: a path that
/// was written is reused until a write through it reports Unavailable, which
/// happens only before an IOCTL (`written`), and is then resolved once again.
pub(crate) struct Transport<D> {
    device: D,
    cached: Option<String>,
}

impl<D: Device> Transport<D> {
    pub(crate) fn new(device: D) -> Self {
        Self {
            device,
            cached: None,
        }
    }

    /// Managed `GetAvailability`: a resolution only, which never seeds the
    /// cache.
    pub(crate) fn availability(&mut self) -> u8 {
        match self.device.resolve() {
            Ok((Some(_), _)) => AVAILABILITY_AVAILABLE,
            Ok((None, resolution)) if resolution.status == WriteStatus::Unavailable => {
                AVAILABILITY_UNAVAILABLE
            }
            _ => AVAILABILITY_UNKNOWN,
        }
    }

    /// Managed `SetUsage`.
    pub(crate) fn set_usage(&mut self, usage: Usage) -> WriteResult {
        self.set_core(usage)
            .unwrap_or_else(|error| WriteResult::failed(Some(message(&error))))
    }

    fn set_core(&mut self, usage: Usage) -> io::Result<WriteResult> {
        if let Some(path) = self.cached.clone() {
            let cached = self.device.write(&path, usage)?;
            if cached.status != WriteStatus::Unavailable {
                return Ok(cached);
            }
            self.cached = None;
        }
        let (path, resolution) = self.device.resolve()?;
        let Some(path) = path else {
            return Ok(resolution);
        };
        let write = self.device.write(&path, usage)?;
        if write.status == WriteStatus::Applied {
            self.cached = Some(path);
        }
        Ok(write)
    }

    /// A call that panicked leaves no path it could have validated.
    fn recover_from_panic(&mut self) {
        self.cached = None;
    }
}

/// Managed `IDisplayBrightnessHidTransport`: what each lane thread owns.
pub(crate) trait Hid: Send + 'static {
    fn availability(&mut self) -> u8;
    fn set_usage(&mut self, usage: Usage) -> WriteResult;
    /// Called after a call panicked, before the next one.
    fn recover_from_panic(&mut self) {}
}

impl<D: Device> Hid for Transport<D> {
    fn availability(&mut self) -> u8 {
        Transport::availability(self)
    }

    fn set_usage(&mut self, usage: Usage) -> WriteResult {
        Transport::set_usage(self, usage)
    }

    fn recover_from_panic(&mut self) {
        Transport::recover_from_panic(self);
    }
}

impl Recover for Box<dyn Hid> {
    fn recover_from_panic(&mut self) {
        Hid::recover_from_panic(&mut **self);
    }
}

/// Managed `DisplayBrightnessHidController.SetUsageAsync`'s reply.
pub(crate) fn reply(write: &WriteResult) -> Outcome {
    let (status, message) = match write.status {
        WriteStatus::Applied => (1, SUBMITTED.to_owned()),
        WriteStatus::Unavailable => (2, PATH_UNAVAILABLE.to_owned()),
        WriteStatus::Failed => match write.error.as_deref() {
            Some(error) if !error.trim().is_empty() => {
                (3, format!("{}: {error}", UNCONFIRMED.trim_end_matches('.')))
            }
            _ => (3, UNCONFIRMED.to_owned()),
        },
    };
    Outcome { status, message }
}

/// Managed `ILogger<DisplayBrightnessHidController>`'s category.
pub(crate) const CONTROLLER_CATEGORY: &str = "OpenBlade.Service.DisplayBrightnessHidController";
/// Managed `DiagnosticTrace`'s category, under which managed traces a driver
/// package it could not verify.
pub(crate) const TRACE_CATEGORY: &str = "OpenBlade.DiagnosticTrace";

/// A warning line: its category and message.
pub(crate) type Sink = Box<dyn Fn(&str, &str) + Send + Sync>;

/// What the lane records for diagnostics: managed's controller warning for
/// an Unavailable write that carries an error, and the stage, reason and
/// installed state of a driver package the resolution rejected. The wire
/// replies carry none of it. A rejection is recorded when it differs from
/// the last one recorded for its service, so a status probe every
/// `AVAILABILITY_LIFETIME` does not fill the bounded history with one
/// repeated line; a trusted capture forgets it. Without a sink nothing is
/// recorded.
#[derive(Default)]
pub(crate) struct Log {
    sink: Option<Sink>,
    rejected: Mutex<HashMap<String, String>>,
}

impl Log {
    /// A log into `sink`; the Service composes one only with the export.
    #[cfg(any(test, feature = "service-diagnostics-export-v1"))]
    pub(crate) fn new(sink: Sink) -> Self {
        Self {
            sink: Some(sink),
            rejected: Mutex::default(),
        }
    }

    fn warning(&self, category: &str, message: &str) {
        if let Some(sink) = &self.sink {
            sink(category, message);
        }
    }

    /// Managed `DisplayBrightnessHidController`'s warning.
    pub(crate) fn unavailable(&self, error: &str) {
        self.warning(
            CONTROLLER_CATEGORY,
            &format!("Display-brightness HID access is unavailable: {error}"),
        );
    }

    /// A package capture for `service`: a rejection is recorded once until
    /// it changes or the package is trusted again.
    pub(crate) fn package(&self, service: &str, package: &crate::driver_trust::package::Package) {
        use crate::driver_trust::package::Package;
        let mut rejected = self
            .rejected
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        let Package::Rejected {
            installed,
            stage,
            reason,
            message,
        } = package
        else {
            rejected.remove(service);
            return;
        };
        let line = format!(
            "Driver package {service} was rejected at {stage} ({reason}, installed: {installed}): {message}"
        );
        if rejected.get(service) != Some(&line) {
            rejected.insert(service.to_owned(), line.clone());
            drop(rejected);
            self.warning(TRACE_CATEGORY, &line);
        }
    }
}

/// The last availability the probe thread resolved, and when.
type Published = Mutex<(u8, Option<std::time::Instant>)>;

const CALL_THREAD: &str = "openblade-display-brightness-hid";
const PROBE_THREAD: &str = "openblade-display-brightness-probe";
/// Request submissions that may wait behind the one in progress. A release
/// owed at a cleanup boundary has one more place, so withdrawn requests the
/// thread has not yet dropped never refuse it (`withdrawal_lane::Lane`).
const WAITING: usize = 2;

/// How the last owed release the lane took ended, by its ticket. The lane
/// records it even when its caller stopped waiting, so a release that
/// answers late still settles the held state (`Held::owed`).
type Finished = Mutex<Option<(u64, WriteStatus)>>;

/// Records an owed release's end when dropped: its write's status, or
/// Failed when it panicked or the lane dropped it unrun.
struct Settles {
    finished: Arc<Finished>,
    ticket: u64,
    status: WriteStatus,
}

impl Drop for Settles {
    fn drop(&mut self) {
        *self
            .finished
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner) = Some((self.ticket, self.status));
    }
}

/// The transport's two threads: submissions in order on one
/// (`withdrawal_lane::Lane`), availability resolutions on the other.
pub(crate) struct Lane {
    calls: Calls<Box<dyn Hid>>,
    probes: SyncSender<()>,
    probing: Arc<AtomicBool>,
    published: Arc<Published>,
    stopped: Arc<AtomicBool>,
    log: Arc<Log>,
    tickets: AtomicU64,
    finished: Arc<Finished>,
}

impl Drop for Lane {
    fn drop(&mut self) {
        self.stopped.store(true, Ordering::Release);
    }
}

impl Lane {
    /// Starts both threads and builds a transport on each.
    pub(crate) fn spawn<H: Hid>(
        log: Arc<Log>,
        build: impl Fn() -> H + Send + Sync + 'static,
    ) -> io::Result<Self> {
        let build = Arc::new(build);
        let (probes, requests) = sync_channel(1);
        let probing = Arc::new(AtomicBool::new(false));
        let published = Arc::new(Mutex::new((AVAILABILITY_UNKNOWN, None)));
        let stopped = Arc::new(AtomicBool::new(false));
        {
            let build = build.clone();
            let probing = probing.clone();
            let published = published.clone();
            let stopped = stopped.clone();
            thread::Builder::new()
                .name(PROBE_THREAD.into())
                .spawn(move || probe(build(), &requests, &probing, &published, &stopped))?;
        }
        let calls = Calls::spawn(CALL_THREAD, WAITING, move || {
            Box::new(build()) as Box<dyn Hid>
        })?;
        Ok(Self {
            calls,
            probes,
            probing,
            published,
            stopped,
            log,
            tickets: AtomicU64::new(0),
            finished: Arc::default(),
        })
    }

    /// Submits `usage` within `budget`. Dropping the future before the call
    /// starts withdraws it; after, the call finishes on the lane unobserved.
    #[cfg(test)]
    pub(crate) async fn submit(&self, usage: Usage, budget: Duration) -> Called<WriteResult> {
        self.submit_claimed(&Claim::default(), Waiting::Withdraws, usage, budget)
            .await
    }

    /// Submits `usage` under `claim`, which the caller keeps to learn
    /// whether it ever started (`withdrawal_lane::Lane::call_claimed`).
    async fn submit_claimed(
        &self,
        claim: &Claim,
        waiting: Waiting,
        usage: Usage,
        budget: Duration,
    ) -> Called<WriteResult> {
        self.call(claim, waiting, budget, move |hid| hid.set_usage(usage))
            .await
    }

    /// Submits the release a cleanup boundary owes: it stays queued
    /// (`Waiting::Stays`), and its ticket names how it ends (`owed_end`).
    async fn submit_owed(&self, budget: Duration) -> (Called<WriteResult>, u64) {
        let ticket = self.tickets.fetch_add(1, Ordering::AcqRel) + 1;
        let settles = Settles {
            finished: self.finished.clone(),
            ticket,
            status: WriteStatus::Failed,
        };
        let called = self
            .call(&Claim::default(), Waiting::Stays, budget, move |hid| {
                // The whole guard, so it records however the call ends.
                let mut settles = settles;
                let write = hid.set_usage(Usage::Released);
                settles.status = write.status;
                write
            })
            .await;
        (called, ticket)
    }

    /// How the owed release `ticket` ended, or None while it is queued or
    /// running. A later one that ended means it ended too, unobserved, so
    /// it counts as not applied.
    fn owed_end(&self, ticket: u64) -> Option<WriteStatus> {
        let finished = *self
            .finished
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        match finished {
            Some((last, status)) if last == ticket => Some(status),
            Some((last, _)) if last > ticket => Some(WriteStatus::Failed),
            _ => None,
        }
    }

    async fn call(
        &self,
        claim: &Claim,
        waiting: Waiting,
        budget: Duration,
        job: impl FnOnce(&mut Box<dyn Hid>) -> WriteResult + Send + 'static,
    ) -> Called<WriteResult> {
        let called = self.calls.call_claimed(claim, waiting, budget, job).await;
        if let Called::Done(write) = &called
            && write.status == WriteStatus::Unavailable
            && let Some(error) = write
                .error
                .as_deref()
                .filter(|error| !error.trim().is_empty())
        {
            self.log.unavailable(error);
        }
        called
    }

    /// The last resolved availability. A missing or stale one asks the
    /// probe thread for another without waiting for it, so the status
    /// observer never waits on a Windows call: the next status publishes
    /// the answer.
    pub(crate) fn availability(&self) -> u8 {
        let (availability, resolved) = *self
            .published
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        let stale = resolved.is_none_or(|at| at.elapsed() >= AVAILABILITY_LIFETIME);
        if stale
            && self
                .probing
                .compare_exchange(false, true, Ordering::AcqRel, Ordering::Acquire)
                .is_ok()
            && self.probes.try_send(()).is_err()
        {
            self.probing.store(false, Ordering::Release);
        }
        availability
    }

    /// Ages the published resolution by `by`, as time passing would.
    #[cfg(test)]
    pub(crate) fn age_availability_for_tests(&self, by: Duration) {
        let mut published = self.published.lock().unwrap();
        if let Some(at) = published.1 {
            published.1 = Some(at.checked_sub(by).expect("the clock reaches back"));
        }
    }
}

fn probe<H: Hid>(
    mut hid: H,
    requests: &Receiver<()>,
    probing: &AtomicBool,
    published: &Published,
    stopped: &AtomicBool,
) {
    // The requests disconnect when the lane is dropped.
    while requests.recv().is_ok() {
        if stopped.load(Ordering::Acquire) {
            break;
        }
        let resolved =
            panic::catch_unwind(AssertUnwindSafe(|| hid.availability())).unwrap_or_else(|_| {
                hid.recover_from_panic();
                AVAILABILITY_UNKNOWN
            });
        *published
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner) =
            (resolved, Some(std::time::Instant::now()));
        probing.store(false, Ordering::Release);
    }
}

/// Managed's three runtime fields (`ServiceControlRuntime.cs:770-772`), and
/// native's ticket of an owed release still on the lane. They are in memory
/// only, as managed's are: a restarted Service starts with a neutralization
/// pending, so its first press releases first.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) struct Held {
    pub(crate) asserted: Usage,
    pub(crate) release_required: bool,
    pub(crate) neutralization_pending: bool,
    /// An owed release that had not answered when its boundary stopped
    /// waiting (`release_owed`). It is the next usage the driver sees, so
    /// `asserted` reads Released while it is queued and a press of the same
    /// usage is sent again; `release_required` stays set until a release is
    /// confirmed, by it (`reconcile`) or by a later one.
    pub(crate) owed: Option<u64>,
}

impl Default for Held {
    fn default() -> Self {
        Self {
            asserted: Usage::Released,
            release_required: false,
            neutralization_pending: true,
            owed: None,
        }
    }
}

impl Held {
    /// What a confirmed release leaves.
    const RELEASED: Self = Self {
        asserted: Usage::Released,
        release_required: false,
        neutralization_pending: false,
        owed: None,
    };
}

const APPLIED: u8 = 1;
const BLOCKED: u8 = 2;

fn called_reply(called: Called<WriteResult>) -> Outcome {
    match called {
        Called::Done(write) => reply(&write),
        Called::NotStarted => Outcome::blocked(PATH_UNAVAILABLE),
        Called::Abandoned => reply(&WriteResult::failed(Some(UNFINISHED.to_owned()))),
    }
}

async fn submit(
    lane: Option<&Lane>,
    claim: &Claim,
    waiting: Waiting,
    usage: Usage,
    budget: Duration,
) -> Outcome {
    let Some(lane) = lane else {
        return Outcome::blocked(PATH_UNAVAILABLE);
    };
    called_reply(lane.submit_claimed(claim, waiting, usage, budget).await)
}

/// A request's share of its deadline at `now`: what is left of it after the
/// answer.
fn request_budget(deadline: Instant, now: Instant) -> Duration {
    deadline
        .saturating_duration_since(now)
        .saturating_sub(ANSWER_MARGIN)
        .min(CALL_BUDGET)
}

/// A cleanup boundary's share at `now`: all of it up to `until`, since its
/// caller answers nobody.
fn owed_budget(until: Instant, now: Instant) -> Duration {
    until.saturating_duration_since(now).min(CALL_BUDGET)
}

/// Managed: only an Applied release clears the held state.
fn settle_release(held: &RefCell<Held>, outcome: &Outcome) {
    if outcome.status == APPLIED {
        *held.borrow_mut() = Held::RELEASED;
    }
}

/// Settles an owed release that answered after its boundary stopped
/// waiting: Applied clears the held state, anything else leaves the release
/// owed for the next one.
fn reconcile(held: &RefCell<Held>, lane: Option<&Lane>) {
    let Some(ticket) = held.borrow().owed else {
        return;
    };
    match lane.map_or(Some(WriteStatus::Failed), |lane| lane.owed_end(ticket)) {
        None => {}
        Some(WriteStatus::Applied) => *held.borrow_mut() = Held::RELEASED,
        Some(_) => held.borrow_mut().owed = None,
    }
}

/// Managed `ReleaseDisplayBrightnessUsageCoreAsync` for a request: withdrawn
/// if it has not started when the request stops waiting.
pub(crate) async fn release(
    held: &RefCell<Held>,
    lane: Option<&Lane>,
    deadline: Instant,
) -> Outcome {
    let budget = request_budget(deadline, Instant::now());
    let outcome = submit(
        lane,
        &Claim::default(),
        Waiting::Withdraws,
        Usage::Released,
        budget,
    )
    .await;
    settle_release(held, &outcome);
    outcome
}

/// The release core at a cleanup boundary (Stop, a confirmed suspend, the
/// maintenance step's neutralization), when the held state `owes` one,
/// after any owed release that answered late is settled. No answer is kept
/// back, so the whole time to `until` is the release's, and it is never
/// withdrawn. Queued behind a call still running, a press whose request is
/// gone included, it runs after it, however long this caller waits. One
/// that has not answered by `until` is reported unfinished and stays owed
/// (`Held::owed`); while it is queued no boundary queues another. It may
/// answer after Stop confirmed the drain, on the lane thread that outlives
/// the drain, and then settles nothing further: its release is what the
/// driver is left with.
pub(crate) async fn release_owed(
    held: &RefCell<Held>,
    lane: Option<&Lane>,
    until: Instant,
    owes: impl Fn(&Held) -> bool,
) -> Option<Outcome> {
    reconcile(held, lane);
    if !owes(&held.borrow()) {
        return None;
    }
    if held.borrow().owed.is_some() {
        return Some(reply(&WriteResult::failed(Some(UNFINISHED.to_owned()))));
    }
    let Some(lane) = lane else {
        return Some(Outcome::blocked(PATH_UNAVAILABLE));
    };
    let (called, ticket) = lane.submit_owed(owed_budget(until, Instant::now())).await;
    let abandoned = matches!(called, Called::Abandoned);
    let outcome = called_reply(called);
    settle_release(held, &outcome);
    if abandoned {
        let mut held = held.borrow_mut();
        held.asserted = Usage::Released;
        held.owed = Some(ticket);
    }
    // It may have answered while this stopped waiting.
    reconcile(held, Some(lane));
    Some(outcome)
}

/// Puts back the state a press replaced when the press never started. An
/// owed release queued meanwhile stays owed: it still runs.
fn restore(held: &RefCell<Held>, prior: Held) {
    let mut held = held.borrow_mut();
    let owed = held.owed;
    *held = Held { owed, ..prior };
}

/// Restores the held state a press replaced when the press never started:
/// refused at once, withdrawn at its budget, or withdrawn because its
/// request was dropped first. A press that started keeps it held.
struct Pressing<'a> {
    held: &'a RefCell<Held>,
    prior: Held,
    claim: Claim,
}

impl Drop for Pressing<'_> {
    fn drop(&mut self) {
        if self.claim.withdraw() {
            restore(self.held, self.prior);
        }
    }
}

/// Managed `SetDisplayBrightnessHidUsageAsync` after its operation gate. The
/// caller holds the gate. `admit` is managed's device check at the same
/// point: `None` refuses, and the check it returns is asked again just
/// before the press, after any neutralizing release. A repeat of the held
/// usage and the release a different press owes come before it, as in
/// managed, so neither waits on device admission. Native has no Fan Stop
/// guardian, so managed's guardian block does not arise.
pub(crate) async fn set_usage<A: Fn() -> bool>(
    held: &RefCell<Held>,
    lane: Option<&Lane>,
    usage: Usage,
    deadline: Instant,
    admit: impl FnOnce() -> io::Result<Option<A>>,
) -> io::Result<Outcome> {
    reconcile(held, lane);
    if usage == Usage::Released {
        return Ok(release(held, lane, deadline).await);
    }
    let current = *held.borrow();
    if current.release_required && current.asserted == usage {
        return Ok(Outcome {
            status: APPLIED,
            message: REMAINS_ASSERTED.to_owned(),
        });
    }
    if current.release_required {
        let released = release(held, lane, deadline).await;
        if released.status != APPLIED {
            return Ok(released);
        }
    }
    let Some(authorized) = admit()? else {
        return Ok(Outcome::blocked(DEVICE_REQUIRED));
    };
    if held.borrow().neutralization_pending {
        let neutralized = release(held, lane, deadline).await;
        if neutralized.status != APPLIED {
            return Ok(neutralized);
        }
    }
    if !authorized() {
        return Ok(Outcome::blocked(DEVICE_REQUIRED));
    }
    // A press that may reach the driver is held before it starts, so a
    // request dropped or timed out after it started still owes its release
    // and the press is never sent again. One that never started submitted
    // nothing, so its prior state comes back (`Pressing`), as does a
    // refusal; managed records any other result.
    let pressing = Pressing {
        held,
        prior: *held.borrow(),
        claim: Claim::default(),
    };
    {
        let mut held = held.borrow_mut();
        held.asserted = usage;
        held.release_required = true;
    }
    let outcome = submit(
        lane,
        &pressing.claim,
        Waiting::Withdraws,
        usage,
        request_budget(deadline, Instant::now()),
    )
    .await;
    if outcome.status == BLOCKED {
        restore(held, pressing.prior);
    }
    Ok(outcome)
}

#[cfg(test)]
#[path = "display_brightness_hid_tests.rs"]
mod tests;

#[cfg(test)]
#[path = "display_brightness_hid_fake.rs"]
pub(crate) mod fake;

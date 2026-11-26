//! Managed display-brightness HID input on exact RZ09-0581: the caching half
//! of `WindowsRazerDisplayBrightnessHidTransport`
//! (src/OpenBlade.Service/WindowsRazerDisplayBrightnessHidTransport.cs:90-173),
//! the reply mapping of `DisplayBrightnessHidController` (62-97) and the
//! held-usage state machine of `ServiceControlRuntime`
//! (`SetDisplayBrightnessHidUsageAsync` and its release core, 3409-3510).
//!
//! The usage reaches the laptop as RzCommon's type-10 Consumer Control
//! input: one IOCTL whose completion is the only confirmation the driver
//! API gives. There is no read of the input state, so an IOCTL that may have
//! been submitted is never replayed: a press that may have reached the
//! driver is recorded as held before it is started, and only a confirmed
//! release clears it.
//!
//! Every Windows call runs on the lane's own thread (`Lane`), never on the
//! Service runtime. The resolution and the write (`Device`) are
//! `display_brightness_hid_windows.rs`'s; tests script them.

use std::{
    cell::RefCell,
    io,
    panic::{self, AssertUnwindSafe},
    sync::{
        Arc, Mutex,
        atomic::{AtomicBool, AtomicU8, Ordering},
        mpsc::{Receiver, SyncSender, TrySendError, sync_channel},
    },
    thread,
    time::Duration,
};

use tokio::{sync::oneshot, time::Instant};

use super::super::Outcome;

/// Managed `DisplayBrightnessHidUsage`.
pub(crate) const RELEASED: u16 = 0x0000;
#[cfg_attr(not(test), allow(dead_code))] // Validated by `StatusRequest::validate`.
pub(crate) const INCREMENT: u16 = 0x006F;
#[cfg_attr(not(test), allow(dead_code))]
pub(crate) const DECREMENT: u16 = 0x0070;

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
/// Native only: the submission started on the lane thread and did not
/// finish within the request's budget, so it may have reached the driver.
pub(crate) const UNFINISHED: &str = "the submission did not finish within its budget";

/// The longest one submission may take on the lane, and what a request keeps
/// of its deadline to answer after it.
const CALL_BUDGET: Duration = Duration::from_secs(2);
const ANSWER_MARGIN: Duration = Duration::from_millis(250);
/// How long a published availability stands before the status observer asks
/// the lane for another resolution.
pub(crate) const AVAILABILITY_LIFETIME: Duration = Duration::from_secs(10);

/// Managed `CreateControlBuffer`: RzCommon type 10 maps the little-endian
/// usage at +8 to Consumer Control report 1.
pub(crate) fn control_buffer(usage: u16) -> [u8; CONTROL_BUFFER_LENGTH] {
    let mut buffer = [0; CONTROL_BUFFER_LENGTH];
    buffer[4..8].copy_from_slice(&CONSUMER_CONTROL_INPUT_TYPE.to_le_bytes());
    buffer[USAGE_OFFSET..USAGE_OFFSET + 2].copy_from_slice(&usage.to_le_bytes());
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

/// Managed `ResolveDevicePath` and `WriteUsageToPath`. An error is managed's
/// caught exception: the write reports it as Failed and availability as
/// Unknown.
pub(crate) trait Device: Send + 'static {
    /// The exact control interface path with Applied, or none with why.
    fn resolve(&mut self) -> io::Result<(Option<String>, WriteResult)>;
    /// Opens `path`, validates it and the virtual consumer device again,
    /// and submits `usage`.
    fn write(&mut self, path: &str, usage: u16) -> io::Result<WriteResult>;
}

/// Managed `WindowsRazerDisplayBrightnessHidTransport`'s cache: a path that
/// was written is reused until a write through it reports Unavailable, which
/// happens only before an IOCTL, and is then resolved once again.
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
    pub(crate) fn set_usage(&mut self, usage: u16) -> WriteResult {
        self.set_core(usage)
            .unwrap_or_else(|error| WriteResult::failed(Some(error.to_string())))
    }

    fn set_core(&mut self, usage: u16) -> io::Result<WriteResult> {
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

/// Managed `IDisplayBrightnessHidTransport`: what the lane thread owns.
pub(crate) trait Hid: Send + 'static {
    fn availability(&mut self) -> u8;
    fn set_usage(&mut self, usage: u16) -> WriteResult;
    /// Called after a call panicked, before the next one.
    fn recover_from_panic(&mut self) {}
}

impl<D: Device> Hid for Transport<D> {
    fn availability(&mut self) -> u8 {
        Transport::availability(self)
    }

    fn set_usage(&mut self, usage: u16) -> WriteResult {
        Transport::set_usage(self, usage)
    }

    fn recover_from_panic(&mut self) {
        Transport::recover_from_panic(self);
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

/// How a lane submission ended.
#[derive(Debug, PartialEq, Eq)]
pub(crate) enum Called {
    Done(WriteResult),
    /// The submission never started: the lane was busy with a call and one
    /// more was queued, it had stopped, or the budget ran out first. Nothing
    /// was submitted.
    NotStarted,
    /// The submission started and returned nothing within the budget, or it
    /// panicked. It may have reached the driver.
    Abandoned,
}

const QUEUED: u8 = 0;
const STARTED: u8 = 1;
const WITHDRAWN: u8 = 2;

/// A caller's hold on its queued submission: the lane starts it only by
/// moving it from queued to started, and the caller withdraws it when it
/// stops waiting, so a submission nobody observes never starts later.
struct Claim(Arc<AtomicU8>);

impl Claim {
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

enum Job {
    /// A resolution for the published availability.
    Probe,
    Set {
        usage: u16,
        claim: Arc<AtomicU8>,
        reply: oneshot::Sender<WriteResult>,
    },
}

/// The last availability the lane resolved, and when.
type Published = Mutex<(u8, Option<std::time::Instant>)>;

/// The thread that owns the transport. A submission waits behind at most the
/// call in progress and one probe; a further one is refused at once.
pub(crate) struct Lane {
    jobs: SyncSender<Job>,
    probing: Arc<AtomicBool>,
    published: Arc<Published>,
    stopped: Arc<AtomicBool>,
}

impl Drop for Lane {
    fn drop(&mut self) {
        self.stopped.store(true, Ordering::Release);
    }
}

impl Lane {
    /// Starts the thread and builds the transport on it.
    pub(crate) fn spawn<H: Hid>(build: impl FnOnce() -> H + Send + 'static) -> io::Result<Self> {
        let (jobs, queue) = sync_channel(2);
        let probing = Arc::new(AtomicBool::new(false));
        let published = Arc::new(Mutex::new((AVAILABILITY_UNKNOWN, None)));
        let stopped = Arc::new(AtomicBool::new(false));
        let lane = Self {
            jobs,
            probing: probing.clone(),
            published: published.clone(),
            stopped: stopped.clone(),
        };
        thread::Builder::new()
            .name("openblade-display-brightness-hid".into())
            .spawn(move || run(build(), queue, &probing, &published, &stopped))?;
        Ok(lane)
    }

    /// Submits `usage` within `budget`. Dropping the future before the call
    /// starts withdraws it; after, the call finishes on the lane unobserved.
    pub(crate) async fn submit(&self, usage: u16, budget: Duration) -> Called {
        let claim = Claim(Arc::new(AtomicU8::new(QUEUED)));
        let (reply, mut result) = oneshot::channel();
        let job = Job::Set {
            usage,
            claim: claim.0.clone(),
            reply,
        };
        if let Err(TrySendError::Full(_) | TrySendError::Disconnected(_)) = self.jobs.try_send(job)
        {
            return Called::NotStarted;
        }
        match tokio::time::timeout(budget, &mut result).await {
            Ok(Ok(write)) => Called::Done(write),
            // A started call that sent nothing panicked; one never started
            // was dropped unrun by a stopped lane.
            Ok(Err(_)) if claim.started() => Called::Abandoned,
            Ok(Err(_)) => Called::NotStarted,
            Err(_) if claim.withdraw() => Called::NotStarted,
            Err(_) => match result.try_recv() {
                Ok(write) => Called::Done(write),
                Err(_) => Called::Abandoned,
            },
        }
    }

    /// The last resolved availability. A missing or stale one asks the lane
    /// for another without waiting for it, so the status observer never
    /// waits on a Windows call: the next status publishes the answer.
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
            && self.jobs.try_send(Job::Probe).is_err()
        {
            self.probing.store(false, Ordering::Release);
        }
        availability
    }
}

fn run<H: Hid>(
    mut transport: H,
    queue: Receiver<Job>,
    probing: &AtomicBool,
    published: &Published,
    stopped: &AtomicBool,
) {
    // The queue disconnects when the lane is dropped; a job still queued
    // then is dropped unrun.
    while let Ok(job) = queue.recv() {
        if stopped.load(Ordering::Acquire) {
            break;
        }
        match job {
            Job::Probe => {
                let resolved = panic::catch_unwind(AssertUnwindSafe(|| transport.availability()))
                    .unwrap_or(AVAILABILITY_UNKNOWN);
                *published
                    .lock()
                    .unwrap_or_else(std::sync::PoisonError::into_inner) =
                    (resolved, Some(std::time::Instant::now()));
                probing.store(false, Ordering::Release);
            }
            Job::Set {
                usage,
                claim,
                reply,
            } => {
                if claim
                    .compare_exchange(QUEUED, STARTED, Ordering::AcqRel, Ordering::Acquire)
                    .is_err()
                {
                    continue;
                }
                match panic::catch_unwind(AssertUnwindSafe(|| transport.set_usage(usage))) {
                    Ok(write) => {
                        let _ = reply.send(write);
                    }
                    Err(_) => transport.recover_from_panic(),
                }
            }
        }
    }
}

/// Managed's three runtime fields (`ServiceControlRuntime.cs:770-772`). They
/// are in memory only, as managed's are: a restarted Service starts with a
/// neutralization pending, so its first press releases first.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) struct Held {
    pub(crate) asserted: u16,
    pub(crate) release_required: bool,
    pub(crate) neutralization_pending: bool,
}

impl Default for Held {
    fn default() -> Self {
        Self {
            asserted: RELEASED,
            release_required: false,
            neutralization_pending: true,
        }
    }
}

const APPLIED: u8 = 1;
const BLOCKED: u8 = 2;

async fn submit(lane: Option<&Lane>, usage: u16, deadline: Instant) -> Outcome {
    let Some(lane) = lane else {
        return Outcome::blocked(PATH_UNAVAILABLE);
    };
    let budget = deadline
        .saturating_duration_since(Instant::now())
        .saturating_sub(ANSWER_MARGIN)
        .min(CALL_BUDGET);
    match lane.submit(usage, budget).await {
        Called::Done(write) => reply(&write),
        Called::NotStarted => Outcome::blocked(PATH_UNAVAILABLE),
        Called::Abandoned => reply(&WriteResult::failed(Some(UNFINISHED.to_owned()))),
    }
}

/// Managed `ReleaseDisplayBrightnessUsageCoreAsync`: only an Applied release
/// clears the held state.
pub(crate) async fn release(
    held: &RefCell<Held>,
    lane: Option<&Lane>,
    deadline: Instant,
) -> Outcome {
    let outcome = submit(lane, RELEASED, deadline).await;
    if outcome.status == APPLIED {
        *held.borrow_mut() = Held {
            asserted: RELEASED,
            release_required: false,
            neutralization_pending: false,
        };
    }
    outcome
}

/// Managed `SetDisplayBrightnessHidUsageAsync` after its operation gate. The
/// caller holds the gate. `admit` is managed's device check at the same
/// point: `None` refuses, and the check it returns is asked again just
/// before the press, after any neutralizing release. Native has no Fan Stop
/// guardian, so managed's guardian block does not arise.
pub(crate) async fn set_usage<A: Fn() -> bool>(
    held: &RefCell<Held>,
    lane: Option<&Lane>,
    usage: u16,
    deadline: Instant,
    admit: impl FnOnce() -> io::Result<Option<A>>,
) -> io::Result<Outcome> {
    if usage == RELEASED {
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
    // request dropped or timed out part way still owes its release and the
    // press is never sent again. Only a refusal, which submitted nothing,
    // restores the prior state, as managed records any other result.
    let prior = *held.borrow();
    {
        let mut held = held.borrow_mut();
        held.asserted = usage;
        held.release_required = true;
    }
    let outcome = submit(lane, usage, deadline).await;
    if outcome.status == BLOCKED {
        *held.borrow_mut() = prior;
    }
    Ok(outcome)
}

#[cfg(test)]
#[path = "display_brightness_hid_tests.rs"]
mod tests;

#[cfg(test)]
#[path = "display_brightness_hid_fake.rs"]
pub(crate) mod fake;

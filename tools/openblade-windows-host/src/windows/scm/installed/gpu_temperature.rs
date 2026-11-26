//! Managed GPU temperature on the installed read source
//! (`service-live-readings-v1`).
//!
//! Managed reads it for an ObserveState that asks for `GpuTemperature`
//! (`ReadOptionalLiveSensorsAsync`, then `SafeWindowsFanTemperatureSource.
//! ReadForLiveReadingsAsync`) from NVIDIA's user-mode NVML, behind two
//! Windows reads that never call an NVIDIA API:
//!
//! 1. A workload: the WDDM GPU-engine counters of the NVIDIA adapter,
//!    collected twice 150 ms apart (`WindowsGpuActivitySource`). A process
//!    other than System (PID 4) above 0.1 % is a workload; System's copy
//!    engine pages video memory at the adapter's own power transitions.
//! 2. The adapter's latest D-state from SetupAPI `CM_POWER_DATA`
//!    (`WindowsNvidiaGpuPowerState`).
//!
//! NVML is called only for a D0 adapter, and a D-state other than D0 read
//! again right after the call discards the value. With a workload every
//! sample reads it. Without one (idle or unavailable counters) a sample
//! reuses the last valid D0 reading for `IDLE_INTERVAL`, and only while
//! every sample since has reported D0; otherwise it reads NVML once the
//! samples have reported D0 for `IDLE_DWELL`. So an idle GPU held in D0 is
//! read at most once per `IDLE_INTERVAL` while its reads return a valid
//! temperature and are not cancelled, and NVIDIA's own brief wakes open no
//! NVML. Any sample that is not D0, including the recheck after a read,
//! drops the reading and restarts the dwell, except one cancelled right
//! after its first D-state read, which observes nothing, as managed's does.
//!
//! The session (`nvmlInit_v2` to `nvmlShutdown`) stays open only across
//! consecutive samples that read a valid temperature from an active, D0 GPU,
//! and for at most `LEASE` after the last one. Any other sample ends it
//! before it returns, an idle read included, and so does a cancellation
//! after the D-state read; one during the workload read leaves it to its
//! lease, as managed's does. A sample starting within `SAMPLE_BUDGET` of the
//! lease's end ends it first, and the D-state read ends one whose lease ran
//! out during the Windows reads, so only a stalled Windows read can hold it
//! past the lease. So a sample neither wakes a GPU that is not in D0 nor
//! keeps it awake past a workload by more than the lease.
//!
//! The reads run on a worker thread this module owns, never on the HID read
//! job. An observation waits for its sample at most `AFTER_READING` after
//! its own reading and never later than `RESERVE` before its deadline. One
//! sample runs at a time and one request may wait for it; any further
//! request gets none. A request whose observation has gone is skipped.
//! Stop cancels a sample as managed's token does: it stops after the step
//! it is in, the workload, a D-state read or the NVML read, without waiting
//! for the worker, which ends the session when the call it is in returns.
//! An observation that stops waiting, and the request's own time, which
//! ends `RESERVE` before the observation's deadline, stop the sample the
//! same way only until it has started its NVML read. After that the sample
//! still rechecks the D-state and keeps a valid reading, as managed keeps
//! one its token did not cancel, so a read that outlasts the wait spares
//! the next idle samples their own. A sample that ends past its request's
//! time publishes nothing.
//!
//! A missing driver, adapter or NVML, a failed call, a powered-down GPU and a
//! sensor outside managed's 10..=125 C all publish null, as managed does. A
//! value the managed codec refuses (above 115 C) is published as null rather
//! than refusing the whole reply.
use std::{
    cell::RefCell,
    collections::BTreeMap,
    sync::{
        Arc,
        atomic::{AtomicBool, Ordering},
        mpsc::{Receiver, RecvTimeoutError, SyncSender, TrySendError, sync_channel},
    },
    thread,
    time::{Duration, Instant},
};
use tokio::sync::oneshot;

#[cfg(windows)]
#[path = "gpu_temperature_windows.rs"]
mod windows;
#[cfg(windows)]
pub(super) use windows::sampler as windows_sampler;

/// Managed `SafeWindowsFanTemperatureSource.GpuSessionIdleTimeout`: how long
/// a session outlives the last active sample when no sample follows.
pub(super) const LEASE: Duration = Duration::from_secs(5);
/// Managed `IdleGpuLiveReadingDwell`: how long the samples must have
/// reported D0 before one without a workload reads NVML.
const IDLE_DWELL: Duration = Duration::from_secs(3);
/// Managed `IdleGpuLiveReadingInterval`: how long a sample without a
/// workload reuses the last D0 reading, and so the spacing of its reads.
const IDLE_INTERVAL: Duration = Duration::from_secs(10);
/// The part of an observation's time the sample leaves to the reply: the
/// read job's own reserve.
pub(super) const RESERVE: Duration = Duration::from_millis(500);
/// How long an observation whose reading has arrived still waits for its
/// sample, so a slow sample holds neither the reply nor the read source.
const AFTER_READING: Duration = Duration::from_millis(250);
/// Managed's plausible sensor range (`ReadCoreAsync`).
const PLAUSIBLE: std::ops::RangeInclusive<i32> = 10..=125;
/// Managed `NvidiaNvmlTemperatureSession`'s own bound.
const MAXIMUM_SENSOR: u32 = 125;
/// The GPU temperatures the managed v91 codec writes.
const PUBLISHED: std::ops::RangeInclusive<i32> = 0..=115;
/// `NVML_TEMPERATURE_GPU`.
const TEMPERATURE_GPU: u32 = 0;

/// The adapter's latest D-state, as managed `FanStopGpuPowerState`.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(super) enum Power {
    Unknown,
    /// D0.
    On,
    /// D3 (no D3cold claim).
    Down,
}

/// The Windows reads that gate NVML. Neither calls an NVIDIA API.
pub(super) trait Probes {
    /// Managed `WindowsGpuActivitySource.IsActiveAsync`, its sample window
    /// included: whether an NVIDIA workload runs, or None when the counters
    /// are unavailable.
    fn workload(&mut self) -> Option<bool>;
    /// Managed `WindowsNvidiaGpuPowerState.Read`.
    fn power(&mut self) -> Power;
}

/// Managed `WindowsGpuActivitySource.SampleDelayMilliseconds`.
pub(super) const SAMPLE_WINDOW: Duration = Duration::from_millis(150);
/// The workload window and the D-state read: a session whose lease ends
/// within it ends before a sample starts.
const SAMPLE_BUDGET: Duration = Duration::from_millis(250);
/// The Windows System process, whose NVIDIA copy-engine entries are video
/// memory paging at the adapter's own power transitions, not a workload.
const SYSTEM_PROCESS: i32 = 4;
/// Managed `WindowsGpuProcessActivity.ActiveThresholdPercent`.
const ACTIVE_PERCENT: f64 = 0.1;

/// Managed `WindowsGpuActivitySource.IsActiveAsync` over one fresh query
/// (`open`, managed `Refresh`): the first read primes it and its result is
/// ignored, and the read after `pause` decides. None when the query cannot
/// be opened or that read is unavailable.
pub(super) fn workload<Q>(
    open: impl FnOnce() -> Option<Q>,
    mut read: impl FnMut(&mut Q) -> Option<BTreeMap<i32, f64>>,
    pause: impl FnOnce(Duration),
) -> Option<bool> {
    let mut query = open()?;
    let _ = read(&mut query);
    pause(SAMPLE_WINDOW);
    let processes = read(&mut query)?;
    Some(
        processes
            .iter()
            .any(|(pid, percent)| *pid != SYSTEM_PROCESS && *percent > ACTIVE_PERCENT),
    )
}

/// `PCI\VEN_10DE&`: the NVIDIA PCI vendor of a display-class instance ID.
const NVIDIA_INSTANCE: &str = r"PCI\VEN_10DE&";
/// `REG_BINARY`.
const BINARY: u32 = 3;
/// `CM_POWER_DATA`: fourteen 32-bit fields, its size first.
pub(super) const POWER_DATA: usize = 56;

/// One step of a display-class device enumeration.
pub(super) enum Enumerated {
    /// The enumeration ended (`ERROR_NO_MORE_ITEMS`).
    End,
    /// The enumeration or the device's instance ID failed.
    Failed,
    Device(String),
}

/// Managed `WindowsNvidiaGpuPowerState.Read` over an enumeration of the
/// present display-class devices: the D-state of the one NVIDIA PCI device,
/// or Unknown for none, several, a failed step or a property that is not a
/// complete `CM_POWER_DATA`. `power_data` is the type and bytes of the
/// `SPDRP_DEVICE_POWER_DATA` property of the device `device` returned last.
pub(super) fn nvidia_power(
    mut device: impl FnMut(u32) -> Enumerated,
    mut power_data: impl FnMut() -> Option<(u32, Vec<u8>)>,
) -> Power {
    let mut power = Power::Unknown;
    let mut matches = 0;
    for index in 0..u32::MAX {
        let instance = match device(index) {
            Enumerated::End if matches == 1 => return power,
            Enumerated::End | Enumerated::Failed => return Power::Unknown,
            Enumerated::Device(instance) => instance,
        };
        let nvidia = instance
            .get(..NVIDIA_INSTANCE.len())
            .is_some_and(|prefix| prefix.eq_ignore_ascii_case(NVIDIA_INSTANCE));
        if !nvidia {
            continue;
        }
        matches += 1;
        if matches != 1 {
            return Power::Unknown;
        }
        let Some((kind, data)) = power_data() else {
            return Power::Unknown;
        };
        power = classify_power_data(&data, kind);
    }
    Power::Unknown
}

/// Managed `WindowsNvidiaGpuPowerState.ClassifyPowerData`.
pub(super) fn classify_power_data(data: &[u8], kind: u32) -> Power {
    let field = |offset: usize| {
        data.get(offset..offset + 4)
            .map(|bytes| u32::from_le_bytes([bytes[0], bytes[1], bytes[2], bytes[3]]))
    };
    if kind != BINARY || data.len() != POWER_DATA || field(0) != Some(POWER_DATA as u32) {
        return Power::Unknown;
    }
    field(4).map_or(Power::Unknown, classify)
}

/// Managed `WindowsNvidiaGpuPowerState.Classify`: `PowerDeviceD0` and
/// `PowerDeviceD3`; any other state is Unknown.
pub(super) fn classify(state: u32) -> Power {
    match state {
        1 => Power::On,
        4 => Power::Down,
        _ => Power::Unknown,
    }
}

/// The four NVML calls managed makes, each returning its `nvmlReturn_t`.
pub(super) trait Nvml {
    fn init(&mut self) -> i32;
    /// `nvmlDeviceGetHandleByIndex_v2`.
    fn device(&mut self, index: u32) -> Result<usize, i32>;
    /// `nvmlDeviceGetTemperature`.
    fn temperature(&mut self, device: usize, sensor: u32) -> Result<u32, i32>;
    fn shutdown(&mut self) -> i32;
}

/// Managed `IGpuTemperatureSession`: a reader whose driver session outlives
/// one read, closed by its owner.
pub(super) trait TemperatureSession {
    fn try_read(&mut self) -> Option<i32>;
    fn release(&mut self);
}

/// Managed `NvidiaNvmlTemperatureSession`: initializes NVML on the first read
/// and keeps it; a failed handle or temperature call shuts it down.
pub(super) struct NvmlSession<N: Nvml> {
    nvml: N,
    initialized: bool,
}

impl<N: Nvml> NvmlSession<N> {
    pub(super) fn new(nvml: N) -> Self {
        Self {
            nvml,
            initialized: false,
        }
    }
}

/// Dropping the session ends it, so no owner can leave NVML started.
impl<N: Nvml> Drop for NvmlSession<N> {
    fn drop(&mut self) {
        self.release();
    }
}

impl<N: Nvml> TemperatureSession for NvmlSession<N> {
    fn try_read(&mut self) -> Option<i32> {
        if !self.initialized {
            if self.nvml.init() != 0 {
                return None;
            }
            self.initialized = true;
        }
        let Ok(device) = self.nvml.device(0) else {
            self.release();
            return None;
        };
        let Ok(celsius) = self.nvml.temperature(device, TEMPERATURE_GPU) else {
            self.release();
            return None;
        };
        // The range check needs no release: the sampler releases every
        // sample without a valid temperature.
        (celsius <= MAXIMUM_SENSOR).then_some(celsius as i32)
    }

    fn release(&mut self) {
        if !self.initialized {
            return;
        }
        self.initialized = false;
        // Managed traces a failed shutdown and changes nothing else.
        let _ = self.nvml.shutdown();
    }
}

/// Managed's two GPU readers, which share `ReadCoreAsync`.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum Reader {
    /// `ReadForLiveReadingsAsync`: the Service's only reader.
    LiveReadings,
    /// `ReadAsync`, which leaves an idle GPU unread. Only the managed
    /// vectors use it, where they interleave both readers over one D0 state.
    #[cfg(test)]
    Ordinary,
}

/// What ends a sample before its last step.
pub(super) trait Cancellation {
    /// Managed's cancellation token, Stop. It ends the sample after any step.
    fn cancelled(&self) -> bool;
    /// No observation waits for the sample any more, or its time ran out. It
    /// ends the sample only before the NVML read: a reading taken after it
    /// still spares the next idle samples their own.
    fn unwanted(&self) -> bool;

    /// Whether a sample that has not read NVML yet stops.
    fn stops_before_read(&self) -> bool {
        self.cancelled() || self.unwanted()
    }
}

/// Managed `SafeWindowsFanTemperatureSource` on the live-readings path
/// (`ReadForLiveReadingsAsync`): active samples read and renew the session,
/// and idle D0 samples reuse a recent reading or read once the dwell has
/// passed, then end the session.
pub(super) struct Sampler {
    probes: Box<dyn Probes>,
    session: Box<dyn TemperatureSession>,
    clock: Box<dyn Fn() -> Instant>,
    /// `LEASE`, which tests shorten.
    lease: Duration,
    /// When the last renewing sample ended (`lastGpuReadTimestamp`), while
    /// the session is held (`gpuIdleReleasePending`).
    renewed: Option<Instant>,
    /// When the samples began reporting D0 without a break
    /// (`gpuPoweredOnSinceTimestamp`).
    on_since: Option<Instant>,
    /// The last valid D0 reading and when it was taken, kept only while
    /// every sample since has reported D0 (`lastPoweredOnGpuReading`).
    last_on: Option<(i32, Instant)>,
}

impl Sampler {
    pub(super) fn new(
        probes: Box<dyn Probes>,
        session: Box<dyn TemperatureSession>,
        clock: Box<dyn Fn() -> Instant>,
    ) -> Self {
        Self {
            probes,
            session,
            clock,
            lease: LEASE,
            renewed: None,
            on_since: None,
            last_on: None,
        }
    }

    /// One live-readings sample.
    pub(super) fn sample(&mut self, cancel: &dyn Cancellation) -> Option<i32> {
        self.read(Reader::LiveReadings, cancel)
    }

    /// One sample of `reader` (`ReadCoreAsync`). `cancel` is checked where
    /// managed checks its token, and an unwanted sample also stops there
    /// until it has started its NVML read: one that stops before the power
    /// read leaves the session as it is, and one that stops after it
    /// releases the session.
    ///
    /// Managed's timer ends a session on time even while a sample reads
    /// Windows state. Here a session whose lease would end within
    /// `SAMPLE_BUDGET` ends before the workload read, and the D-state read
    /// ends one whose lease ran out during the Windows reads, before NVML
    /// is called. So the sampler's own reads keep a session past its lease
    /// only while one Windows read stalls.
    fn read(&mut self, reader: Reader, cancel: &dyn Cancellation) -> Option<i32> {
        self.expire_within(SAMPLE_BUDGET);
        if cancel.stops_before_read() {
            return None;
        }
        let active = self.probes.workload() == Some(true);
        if cancel.stops_before_read() {
            return None;
        }
        let celsius = self.gated(active, reader, cancel);
        // A valid temperature of an active GPU renews the session; anything
        // else, an idle reading included, ends it.
        if active && celsius.is_some() {
            self.renewed = Some((self.clock)());
        } else {
            self.release();
        }
        celsius
    }

    /// The power gate, the idle reuse and dwell, the read and the recheck: a
    /// valid temperature of a D0 GPU, or None.
    fn gated(&mut self, active: bool, reader: Reader, cancel: &dyn Cancellation) -> Option<i32> {
        let power = self.probes.power();
        self.expire();
        if cancel.stops_before_read() {
            return None;
        }
        self.observe(power);
        // D3 or unknown power never opens NVML, even when a workload counter
        // still reports activity.
        if power != Power::On {
            return None;
        }
        if !active {
            // `ReadAsync` leaves an idle GPU unread.
            if reader != Reader::LiveReadings {
                return None;
            }
            // Every sample since this reading has reported D0.
            if let Some(celsius) = self.recent() {
                return Some(celsius);
            }
            if !self.dwelled() {
                return None;
            }
        }
        let celsius = self.session.try_read().filter(|c| PLAUSIBLE.contains(c));
        // The read is made: only managed's token stops the recheck and the
        // reading it keeps.
        if cancel.cancelled() {
            return None;
        }
        let still_on = self.probes.power() == Power::On;
        if !still_on {
            // Managed observes the recheck as Unknown before its next
            // cancellation check.
            self.observe(Power::Unknown);
        }
        if cancel.cancelled() || !still_on {
            return None;
        }
        if let Some(celsius) = celsius {
            self.last_on = Some((celsius, (self.clock)()));
        }
        celsius
    }

    /// Managed `ObserveGpuPower`: D0 starts the dwell if it is not running;
    /// anything else ends it and drops the reading.
    fn observe(&mut self, power: Power) {
        if power == Power::On {
            self.on_since.get_or_insert_with(|| (self.clock)());
        } else {
            self.on_since = None;
            self.last_on = None;
        }
    }

    /// The last D0 reading, while it is younger than `IDLE_INTERVAL`.
    fn recent(&self) -> Option<i32> {
        let now = (self.clock)();
        self.last_on
            .filter(|(_, at)| now.saturating_duration_since(*at) < IDLE_INTERVAL)
            .map(|(celsius, _)| celsius)
    }

    /// Whether the samples have reported D0 for at least `IDLE_DWELL`.
    fn dwelled(&self) -> bool {
        let now = (self.clock)();
        self.on_since
            .is_some_and(|since| now.saturating_duration_since(since) >= IDLE_DWELL)
    }

    /// How long the held session lasts without another sample, or None.
    pub(super) fn remaining_lease(&self) -> Option<Duration> {
        self.renewed
            .map(|renewed| (renewed + self.lease).saturating_duration_since((self.clock)()))
    }

    /// Managed `ReleaseIdleGpuSession`: ends a held session whose lease has
    /// run out, and leaves a renewed one.
    pub(super) fn expire(&mut self) {
        self.expire_within(Duration::ZERO);
    }

    /// Ends a held session whose lease ends within `margin`.
    fn expire_within(&mut self, margin: Duration) {
        if self
            .remaining_lease()
            .is_some_and(|remaining| remaining <= margin)
        {
            self.release();
        }
    }

    /// Managed `ReleaseGpuSession`.
    pub(super) fn release(&mut self) {
        self.renewed = None;
        self.session.release();
    }
}

/// Builds the worker's sampler on the worker thread, so the Windows and NVML
/// handles never leave it.
pub(super) type Factory = Box<dyn FnOnce() -> Sampler + Send>;

struct Request {
    reply: oneshot::Sender<Option<i32>>,
    until: Instant,
}

impl Request {
    /// Whether the request's time, `RESERVE` before its observation's
    /// deadline, has run out.
    fn expired(&self) -> bool {
        Instant::now() >= self.until
    }
}

/// One request's sample: Stop is managed's token, and a dropped reply or
/// the end of the request's time leaves the sample unwanted.
struct Sampling<'a> {
    request: &'a Request,
    stopped: &'a AtomicBool,
}

impl Cancellation for Sampling<'_> {
    fn cancelled(&self) -> bool {
        self.stopped.load(Ordering::Acquire)
    }

    /// The observation dropped its reply, at `AFTER_READING` or because it
    /// went, or the request's time has run out.
    fn unwanted(&self) -> bool {
        self.request.reply.is_closed() || self.request.expired()
    }
}

/// The worker: one sample per request, ending a held session at the end of
/// its lease between requests. Dropping the sampler when Stop disconnects it
/// ends a held session.
fn run(factory: Factory, requests: Receiver<Request>, stopped: Arc<AtomicBool>) {
    let mut sampler = factory();
    loop {
        let request = match sampler.remaining_lease() {
            Some(remaining) => match requests.recv_timeout(remaining) {
                Ok(request) => request,
                Err(RecvTimeoutError::Timeout) => {
                    sampler.expire();
                    continue;
                }
                Err(RecvTimeoutError::Disconnected) => break,
            },
            None => match requests.recv() {
                Ok(request) => request,
                Err(_) => break,
            },
        };
        // Stop cancels a running sample and every waiting request, and a
        // request whose observation went or whose time ran out ends before
        // the workload read. A reply nobody waits for is dropped here, and so
        // is one that ended past its request's time, even before its
        // observation asks for it.
        let celsius = sampler.sample(&Sampling {
            request: &request,
            stopped: &stopped,
        });
        if !request.expired() {
            let _ = request.reply.send(celsius);
        }
    }
}

enum State {
    /// No source, Stop, or a worker that could not start or has ended.
    Absent,
    /// The source, handed to the worker by the first request.
    Idle(Factory),
    Running(SyncSender<Request>),
}

/// The installed read source's GPU temperature worker.
pub(super) struct Host {
    state: RefCell<State>,
    /// Set by Stop; the worker checks it at each step of a sample.
    stopped: Arc<AtomicBool>,
}

/// A requested sample, published only if it arrives in time.
pub(super) struct Pending {
    reply: oneshot::Receiver<Option<i32>>,
    until: tokio::time::Instant,
}

impl Pending {
    /// The temperature to publish, asked for once the observation's reading
    /// has arrived: None when the sample read none or did not arrive within
    /// `AFTER_READING`, or by its deadline. Dropping the reply cancels the
    /// sample.
    pub(super) async fn temperature(self) -> Option<i32> {
        let until = self.until.min(tokio::time::Instant::now() + AFTER_READING);
        tokio::time::timeout_at(until, self.reply)
            .await
            .ok()?
            .ok()
            .flatten()
            .filter(|celsius| PUBLISHED.contains(celsius))
    }
}

impl Host {
    /// No source: no sample is ever taken.
    pub(super) fn none() -> Self {
        Self::with_state(State::Absent)
    }

    pub(super) fn new(factory: Factory) -> Self {
        Self::with_state(State::Idle(factory))
    }

    fn with_state(state: State) -> Self {
        Self {
            state: RefCell::new(state),
            stopped: Arc::default(),
        }
    }

    /// A sample for an observation that publishes by `deadline`, or None
    /// without a source, while a sample still runs, or without time for one.
    /// The first request starts the worker.
    pub(super) fn request(&self, deadline: tokio::time::Instant) -> Option<Pending> {
        let until = deadline.checked_sub(RESERVE)?;
        if until <= tokio::time::Instant::now() {
            return None;
        }
        let mut state = self.state.borrow_mut();
        if let State::Idle(_) = &*state
            && let State::Idle(factory) = std::mem::replace(&mut *state, State::Absent)
        {
            *state = Self::start(factory, self.stopped.clone());
        }
        let State::Running(requests) = &*state else {
            return None;
        };
        let (reply, receiver) = oneshot::channel();
        match requests.try_send(Request {
            reply,
            until: until.into_std(),
        }) {
            Ok(()) => Some(Pending {
                reply: receiver,
                until,
            }),
            Err(TrySendError::Full(_)) => None,
            Err(TrySendError::Disconnected(_)) => {
                *state = State::Absent;
                None
            }
        }
    }

    /// Stops the worker without waiting for it: a running sample makes no
    /// further call after the one it is in, a waiting request is skipped,
    /// and the worker ends the session and exits.
    pub(super) fn stop(&self) {
        self.stopped.store(true, Ordering::Release);
        *self.state.borrow_mut() = State::Absent;
    }

    fn start(factory: Factory, stopped: Arc<AtomicBool>) -> State {
        // One request may wait while a sample runs; any other is refused.
        let (requests, receiver) = sync_channel(1);
        match thread::Builder::new()
            .name("gpu-temperature".into())
            .spawn(move || run(factory, receiver, stopped))
        {
            Ok(_) => State::Running(requests),
            Err(_) => State::Absent,
        }
    }
}

#[cfg(test)]
#[path = "gpu_temperature_tests.rs"]
mod tests;

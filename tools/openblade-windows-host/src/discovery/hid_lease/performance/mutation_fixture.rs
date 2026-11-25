//! Explicit in-memory native-port inputs. No Windows constructor, path or token.
use super::mutation::Permit;
use super::worker::Reading;
use super::*;
use std::sync::{Arc, Mutex, atomic::Ordering};
/// Edits one raw 07/8C reply frame in place.
pub(crate) type PowerEdit = Arc<dyn Fn(&mut [u8; FRAME_LENGTH]) + Send + Sync>;
#[derive(Default)]
pub(crate) struct State {
    pub(crate) battery_getter_hook: Option<Arc<dyn Fn(u8, usize) + Send + Sync>>,
    pub(crate) battery_raw: u8,
    pub(crate) temporary_raw: u8,
    pub(crate) temporary_reads: usize,
    pub(crate) temporary_replies: std::collections::VecDeque<u8>,
    pub(crate) temporary_bad_ack: bool,
    pub(crate) temporary_fail_read: bool,
    pub(crate) temporary_bad_read: bool,
    pub(crate) battery_reads: usize,
    pub(crate) battery_replies: std::collections::VecDeque<u8>,
    pub(crate) battery_bad_ack: bool,
    pub(crate) battery_bad_read: bool,
    pub(crate) battery_fail_read: bool,
    pub(crate) sent: Vec<(u8, u8, Vec<u8>, u8)>,
    pub(crate) sent_at: Vec<Instant>,
    pub(crate) corrupt_ack: Option<usize>,
    pub(crate) writes: usize,
    pub(crate) fail_write: Option<usize>,
    pub(crate) mismatch: bool,
    // Thermal mode reads report Custom once this many setters have been accepted.
    pub(crate) mismatch_after: Option<usize>,
    // Thermal mode reads report this (selector, fans) once this many setters
    // have been accepted, whatever the setters wrote.
    pub(crate) reads_after: Option<(usize, u8, u8)>,
    pub(crate) final_read_timeout: bool,
    pub(crate) final_read_malformed: bool,
    pub(crate) on_power: Option<Arc<dyn Fn(usize) + Send + Sync>>,
    pub(crate) on_mutation_retired: Option<Arc<dyn Fn() + Send + Sync>>,
    pub(crate) mutation_worker_error: Option<Arc<dyn Fn() -> io::Error + Send + Sync>>,
    // The next this many write jobs fail to open their port, before any
    // exchange, as `NativePort::open` can (`windows::mutation_worker`).
    pub(crate) mutation_open_fails: usize,
    // A read job fails after its port retires, so its worker's stop stays unconfirmed.
    pub(crate) read_worker_error: Option<Arc<dyn Fn() -> io::Error + Send + Sync>>,
    pub(crate) actual: u8,
    pub(crate) recommended: u8,
    pub(crate) selectors: [u8; 2],
    pub(crate) fans: [u8; 2],
    pub(crate) custom_levels: [u8; 2],
    // Acknowledge 0D/07 level setters without applying them.
    pub(crate) ignore_levels: bool,
    pub(crate) retired: usize,
    pub(crate) opened: usize,
    pub(crate) power_reads: usize,
    pub(crate) change_power: Option<(usize, u8)>,
    // Raw 07/8C report status (0 means success, 2): 3 FAILED and 5
    // NOT_SUPPORTED are correlated rejections. The second field switches it
    // from that power read on.
    pub(crate) power_status: u8,
    pub(crate) change_power_status: Option<(usize, u8)>,
    // Windows ACLineStatus the test custody reports instead of deriving it
    // from the adapter level: Some(None) is unknown.
    pub(crate) windows_on_battery: Option<Option<bool>>,
    pub(crate) change_windows: Option<(usize, Option<bool>)>,
    // Applied at the first port check after that power read's reply was
    // received: after the Reader's setter-time class decision, before its SET.
    pub(crate) change_windows_after_reply: Option<(usize, Option<bool>)>,
    // Edits every raw 07/8C reply frame (byte 0 is the HID report ID).
    pub(crate) power_edit: Option<PowerEdit>,
    // That power read's reply completes with a receive timeout.
    pub(crate) power_timeout: Option<usize>,
    pub(crate) held_read: Option<Arc<AtomicBool>>,
    pub(crate) held_write: Option<(usize, Arc<AtomicBool>)>,
    /// Notified each time a held read or write begins to hold.
    pub(crate) holding: Arc<tokio::sync::Notify>,
    pub(crate) before_write: Option<Arc<dyn Fn(usize) + Send + Sync>>,
    // Every transfer completes this long after its submission, on the
    // runtime's clock (paused in the direct Reader tests).
    pub(crate) latency: Option<Duration>,
    /// Extra latency on each 07/8C power read, on top of `latency`.
    pub(crate) power_latency: Option<Duration>,
    /// Extra latency on that 07/8C power read only (1-based), on top of
    /// `latency` and `power_latency`.
    pub(crate) power_stall: Option<(usize, Duration)>,
    /// Extra latency on each listed sent request (1-based, every class), on
    /// top of the others.
    pub(crate) exchange_stall: Vec<(usize, Duration)>,
    /// Extra latency on each 0D/01, 0D/02 and 0D/07 setter, on top of
    /// `latency`.
    pub(crate) setter_latency: Option<Duration>,
    /// The keyboard device mode a 00/84 query reports (0 Normal, 3 Driver),
    /// which an accepted 00/04 setter changes.
    pub(crate) device_mode: u8,
    pub(crate) device_mode_queries: usize,
    pub(crate) device_mode_sets: usize,
    /// Called with each 00/84 query's number (1-based) as it is sent, with
    /// the fixture state locked.
    pub(crate) device_mode_on_query: Option<Arc<dyn Fn(usize) + Send + Sync>>,
    /// Called with each 00/04 setter's ID and mode byte as it is sent, with
    /// the fixture state locked.
    pub(crate) device_mode_on_set: Option<Arc<dyn Fn(u8, u8) + Send + Sync>>,
    /// That 00/84 query (1-based) completes with a receive timeout.
    pub(crate) device_mode_timeout: Option<usize>,
    /// The 00/04 setter fails at submission, applying nothing.
    pub(crate) device_mode_fail_set: bool,
    /// The 00/04 setter is acknowledged without changing the mode.
    pub(crate) device_mode_ignore_set: bool,
    /// The 00/04 acknowledgement echoes another mode.
    pub(crate) device_mode_bad_ack: bool,
    // Every 00/84 reply times out.
    pub(crate) device_mode_mute: bool,
    // Each keyboard 00/04 or 00/84 exchange completes this long after it is
    // sent, on the real clock, so a job that stalls costs real time.
    #[cfg(feature = "service-keyboard-driver-mode-v1")]
    pub(crate) device_mode_latency: Option<Duration>,
    // A keyboard Driver-mode write job's worker retires this long after its
    // port does, on the real clock, as a cancelled exchange that is slow to
    // retire would.
    #[cfg(feature = "service-keyboard-driver-mode-v1")]
    pub(crate) device_mode_retire_delay: Option<Duration>,
}
/// When the test sent the request a job serves, on the test's clock. They sit
/// beside the state, not in it: the state is locked for as long as a test hook
/// runs, and the rig must be able to set and clear them meanwhile.
#[derive(Default)]
struct Arrivals {
    /// A recipe on paused time takes its deadline as that request's budget from
    /// its own start (see `worker`), once.
    recipe: Option<Instant>,
    /// Every observation job of the request on paused time does the same, as a
    /// recipe does with `recipe`, so the host's time between the request and
    /// the job does not count. Not taken: the request clears it.
    observation: Option<Instant>,
}
#[derive(Clone)]
pub(crate) struct Fixture {
    pub(crate) state: Arc<Mutex<State>>,
    arrivals: Arc<Mutex<Arrivals>>,
    transactions: Arc<Transactions>,
    threads: Arc<Threads>,
}
/// Counted threads: started, and exited as their thread-local destructors run.
#[derive(Default)]
struct Threads {
    started: std::sync::atomic::AtomicUsize,
    exited: std::sync::atomic::AtomicUsize,
    hooked: std::sync::atomic::AtomicUsize,
}
struct Exited(Arc<Threads>);
impl Drop for Exited {
    fn drop(&mut self) {
        self.0.exited.fetch_add(1, Ordering::Release);
    }
}
thread_local! {
    static EXITED: std::cell::Cell<Option<Exited>> = const { std::cell::Cell::new(None) };
}
impl Fixture {
    pub(crate) fn new() -> Self {
        Self {
            state: Arc::new(Mutex::new(State {
                battery_raw: 0xd0,
                actual: 15,
                recommended: 15,
                selectors: [4, 4],
                fans: [1, 1],
                custom_levels: [2, 2],
                ..State::default()
            })),
            transactions: Arc::new(Transactions::new()),
            threads: Arc::default(),
            arrivals: Arc::default(),
        }
    }
    pub(crate) fn reserve_for_tests(&self, pairs: usize) {
        for _ in 0..pairs {
            assert!(self.transactions.reserve_pair().is_some());
        }
    }
    pub(crate) fn exhaust(&self) {
        while self.transactions.reserve_pair().is_some() {}
    }
    /// Those of `ids` the pool keeps pinned.
    pub(crate) fn pinned_transactions(&self, ids: &[u8]) -> Vec<u8> {
        ids.iter()
            .copied()
            .filter(|id| self.transactions.pinned(*id))
            .collect()
    }
    /// `Transactions::quarantine` on this port's pool.
    pub(crate) fn quarantine(&self, ids: &[u8]) {
        self.transactions.quarantine(ids);
    }
    /// IDs the pool can issue now.
    pub(crate) fn free_transactions(&self) -> usize {
        self.transactions.free()
    }
    /// Counts the current thread as running until it exits. Every job thread
    /// counts itself; a rig counts the other threads it waits on.
    pub(crate) fn count_thread(&self) {
        self.threads.started.fetch_add(1, Ordering::AcqRel);
        EXITED.set(Some(Exited(self.threads.clone())));
    }
    /// Counted threads that have exited. The count can lead a thread's
    /// finished state by its last instructions.
    pub(crate) fn exited_threads(&self) -> usize {
        self.threads.exited.load(Ordering::Acquire)
    }
    /// Runs a test hook, counting it while it runs. A hook that parks its thread
    /// is a wait the test chose, which no clock the rig holds may hide.
    pub(crate) fn hooked<R>(&self, hook: impl FnOnce() -> R) -> R {
        struct Running<'a>(&'a Threads);
        impl Drop for Running<'_> {
            fn drop(&mut self) {
                self.0.hooked.fetch_sub(1, Ordering::AcqRel);
            }
        }
        self.threads.hooked.fetch_add(1, Ordering::AcqRel);
        let _running = Running(&self.threads);
        hook()
    }
    /// Sets the arrivals of the request the next jobs serve (see `Arrivals`).
    pub(crate) fn arrive(&self, recipe: Option<Instant>, observation: Option<Instant>) {
        let mut arrivals = self.arrivals.lock().unwrap();
        arrivals.recipe = recipe;
        arrivals.observation = observation;
    }
    /// Test hooks running now.
    pub(crate) fn hooks_running(&self) -> usize {
        self.threads.hooked.load(Ordering::Acquire)
    }
    /// Counted threads that have started and not exited.
    pub(crate) fn running_threads(&self) -> usize {
        let exited = self.exited_threads();
        self.threads.started.load(Ordering::Acquire) - exited
    }
    pub(crate) fn read(
        &self,
        authorize: Arc<dyn Fn() -> Option<ReadAdmission> + Send + Sync>,
    ) -> Worker<ReadAdmission> {
        self.worker(authorize, None)
    }
    pub(crate) fn write(&self, permit: Arc<dyn Permit>) -> Worker<ReadAdmission> {
        let auth = permit.clone();
        self.worker(Arc::new(move || auth.authorize()), Some(permit))
    }
    fn worker(
        &self,
        authorize: Arc<dyn Fn() -> Option<ReadAdmission> + Send + Sync>,
        permit: Option<Arc<dyn Permit>>,
    ) -> Worker<ReadAdmission> {
        let fixture = self.clone();
        Worker::new(
            move |job| {
                fixture.count_thread();
                let Some(admission) = authorize() else {
                    return Ok(None);
                };
                if permit.is_some() {
                    let mut s = fixture.state.lock().unwrap();
                    if s.mutation_open_fails > 0 {
                        s.mutation_open_fails -= 1;
                        return Ok(None);
                    }
                }
                // A thermal or fan recipe, as opposed to a battery or
                // temporary-charge one.
                let thermal = permit.as_ref().is_some_and(|permit| {
                    !permit.is_battery() && permit.temporary_target().is_none()
                });
                let (paused, arrival) = {
                    let mut s = fixture.state.lock().unwrap();
                    s.opened += 1;
                    let latency = s.latency.is_some()
                        || s.power_latency.is_some()
                        || s.power_stall.is_some()
                        || !s.exchange_stall.is_empty()
                        || s.setter_latency.is_some();
                    #[cfg(feature = "service-keyboard-driver-mode-v1")]
                    let real = s.device_mode_latency.is_some();
                    #[cfg(not(feature = "service-keyboard-driver-mode-v1"))]
                    let real = false;
                    let paused = (latency || permit.is_none() || thermal)
                        && s.held_read.is_none()
                        && s.held_write.is_none()
                        && !real;
                    let arrival = if !paused {
                        None
                    } else {
                        let mut arrivals = fixture.arrivals.lock().unwrap();
                        if permit.is_some() {
                            arrivals.recipe.take()
                        } else {
                            arrivals.observation
                        }
                    };
                    (paused, arrival)
                };
                let mut reader = Reader::new(fixture.port(), admission);
                // An observation, a thermal or fan recipe, and any job with a
                // latency run on paused time unless the job holds a transfer
                // for another thread to release. The clock starts at the real
                // instant the job starts and advances only through the job's
                // own waits, so neither a slow device nor a loaded host costs it
                // time, and the deadline is compared with exact virtual
                // progress. A paused recipe, a battery or temporary-charge one
                // with a latency included, also takes the whole request budget
                // from its own start, so the host's time between the request and
                // the job does not count either. Battery and temporary-charge
                // recipes otherwise keep the real clock: the tests age their
                // results on the test's clock, which a paused job's waits would
                // run ahead of.
                tokio::runtime::Builder::new_current_thread()
                    .enable_time()
                    .start_paused(paused)
                    .build()?
                    .block_on(async {
                        let mut limit = job.limit();
                        if paused && let Some(arrival) = arrival {
                            limit.deadline =
                                Instant::now() + limit.deadline.saturating_duration_since(arrival);
                        }
                        let observed = if let Some(permit) = &permit {
                            reader.apply_preset(permit.as_ref(), limit).await
                        } else {
                            reader.observe(&mut || authorize(), limit).await
                        };
                        reader.retire(None).await?;
                        #[cfg(feature = "service-keyboard-driver-mode-v1")]
                        if permit.as_ref().is_some_and(|p| p.device_mode().is_some()) {
                            let delay = fixture.state.lock().unwrap().device_mode_retire_delay;
                            if let Some(delay) = delay {
                                std::thread::sleep(delay);
                            }
                        }
                        if permit.is_some() {
                            let hook = fixture.state.lock().unwrap().on_mutation_retired.clone();
                            if let Some(hook) = hook {
                                fixture.hooked(|| hook());
                            }
                        }
                        let error = {
                            let state = fixture.state.lock().unwrap();
                            if permit.is_some() {
                                state.mutation_worker_error.clone()
                            } else {
                                state.read_worker_error.clone()
                            }
                        };
                        if let Some(error) = error {
                            return Err(error());
                        }
                        let observed = observed.filter(|_| {
                            permit.as_ref().map_or_else(
                                || authorize() == Some(admission),
                                |p| p.result_admitted(admission),
                            )
                        });
                        Ok(observed.map(|performance| Reading::thermal(admission, performance)))
                    })
            },
            None,
        )
    }
}
impl Fixture {
    fn port(&self) -> MemoryPort {
        MemoryPort {
            fixture: self.clone(),
            pending: None,
            reply: None,
            retired: false,
            held: None,
            reply_timeout: false,
            completion_error: None,
            ready_at: None,
        }
    }
}
struct MemoryPort {
    fixture: Fixture,
    pending: Option<(usize, Frame)>,
    reply: Option<Frame>,
    retired: bool,
    held: Option<Arc<AtomicBool>>,
    reply_timeout: bool,
    completion_error: Option<io::ErrorKind>,
    ready_at: Option<Instant>,
}
impl Port for MemoryPort {
    fn transactions(&self) -> &Transactions {
        &self.fixture.transactions
    }
    fn verify(&mut self, limit: Limit<'_>) -> io::Result<()> {
        if self.pending.is_none() && self.reply.is_none() {
            let mut s = self.fixture.state.lock().unwrap();
            if let Some((at, windows)) = s.change_windows_after_reply
                && at == s.power_reads
            {
                s.windows_on_battery = Some(windows);
                s.change_windows_after_reply = None;
            }
        }
        limit.check()
    }
    fn submit(&mut self, transfer: Transfer, limit: Limit<'_>) -> io::Result<()> {
        limit.check()?;
        assert!(!self.retired && self.pending.is_none());
        self.ready_at = self
            .fixture
            .state
            .lock()
            .unwrap()
            .latency
            .map(|latency| Instant::now() + latency);
        match transfer {
            Transfer::ReceiveReply => {
                if self.reply_timeout {
                    self.completion_error = Some(io::ErrorKind::TimedOut);
                    self.reply_timeout = false;
                }
                self.pending = Some((91, self.reply.take().ok_or(io::ErrorKind::UnexpectedEof)?));
            }
            Transfer::SendGetter(frame) | Transfer::SendMutation(frame) => {
                let mut s = self.fixture.state.lock().unwrap();
                let class = frame[7];
                let command = frame[8];
                let id = frame[2];
                let payload = &frame[9..9 + frame[6] as usize];
                s.sent.push((class, command, payload.to_vec(), id));
                s.sent_at.push(Instant::now());
                let extra = match (class, command) {
                    (7, 0x8c) => {
                        let stall = s
                            .power_stall
                            .filter(|(at, _)| *at == s.power_reads + 1)
                            .map(|(_, stall)| stall);
                        s.power_latency.unwrap_or_default() + stall.unwrap_or_default()
                    }
                    (13, 1 | 2 | 7) => s.setter_latency.unwrap_or_default(),
                    #[cfg(feature = "service-keyboard-driver-mode-v1")]
                    (0, 0x04 | 0x84) => s.device_mode_latency.unwrap_or_default(),
                    _ => Duration::ZERO,
                } + s
                    .exchange_stall
                    .iter()
                    .filter(|(at, _)| *at == s.sent.len())
                    .map(|(_, stall)| *stall)
                    .sum::<Duration>();
                if !extra.is_zero() {
                    self.ready_at = Some(self.ready_at.unwrap_or_else(Instant::now) + extra);
                }
                if class == 7
                    && matches!(command, 0x92 | 0x8f)
                    && let Some(hook) = &s.battery_getter_hook
                {
                    let reads = s.battery_reads;
                    self.fixture.hooked(|| hook(command, reads));
                }
                let response = match (class, command) {
                    (13, 1 | 2 | 7) => {
                        if let Some(check) = &s.before_write {
                            let number = s.writes + 1;
                            self.fixture.hooked(|| check(number));
                        }
                        assert_eq!(payload.len(), if command == 2 { 4 } else { 3 });
                        assert_eq!(payload[0], 1);
                        if command == 2 {
                            assert!(payload[3] <= 1);
                        }
                        let index = (payload[1] - 1) as usize;
                        if command == 2 {
                            s.selectors[index] = payload[2];
                            s.fans[index] = payload[3];
                        }
                        // 0D/07 [1, component, level]: CPU 1, GPU 2.
                        if command == 7 && !s.ignore_levels {
                            assert!(payload[2] <= 2);
                            s.custom_levels[index] = payload[2];
                        }
                        s.writes += 1;
                        if let Some((number, release)) = &s.held_write
                            && *number == s.writes
                        {
                            self.held = Some(release.clone());
                            s.holding.notify_one();
                        }
                        if s.fail_write == Some(s.writes) {
                            return Err(io::ErrorKind::Other.into());
                        }
                        payload.to_vec()
                    }
                    (13, 0x82) => {
                        if let Some(release) = &s.held_read {
                            self.held = Some(release.clone());
                            s.holding.notify_one();
                        }
                        let index = (payload[1] - 1) as usize;
                        vec![
                            1,
                            payload[1],
                            match s.reads_after {
                                Some((at, selector, _)) if at == s.writes => selector,
                                _ if s.mismatch && s.writes == 2
                                    || s.mismatch_after == Some(s.writes) =>
                                {
                                    4
                                }
                                _ => s.selectors[index],
                            },
                            match s.reads_after {
                                Some((at, _, fans)) if at == s.writes => fans,
                                _ => s.fans[index],
                            },
                        ]
                    }
                    (13, 0x87) => vec![1, payload[1], s.custom_levels[(payload[1] - 1) as usize]],
                    (7, 0x92) => {
                        s.battery_reads += 1;
                        if s.battery_reads > 1 {
                            self.reply_timeout = s.battery_fail_read;
                        }
                        vec![if s.battery_reads > 1 {
                            s.battery_replies.pop_front().unwrap_or(s.battery_raw)
                        } else {
                            s.battery_raw
                        }]
                    }
                    (7, 0x8f) => {
                        s.temporary_reads += 1;
                        self.reply_timeout = s.temporary_fail_read;
                        vec![s.temporary_replies.pop_front().unwrap_or(s.temporary_raw)]
                    }
                    (7, 0x0f) => {
                        if let Some(check) = &s.before_write {
                            let number = s.writes + 1;
                            self.fixture.hooked(|| check(number));
                        }
                        assert_eq!(payload.len(), 1);
                        assert!(matches!(payload[0], 0 | 4));
                        s.writes += 1;
                        s.temporary_raw = payload[0];
                        if let Some((n, release)) = &s.held_write
                            && *n == s.writes
                        {
                            self.held = Some(release.clone());
                            s.holding.notify_one();
                        }
                        if s.fail_write == Some(s.writes) {
                            return Err(io::ErrorKind::Other.into());
                        }
                        vec![if s.temporary_bad_ack {
                            payload[0] ^ 1
                        } else {
                            payload[0]
                        }]
                    }
                    (7, 0x12) => {
                        if let Some(check) = &s.before_write {
                            let number = s.writes + 1;
                            self.fixture.hooked(|| check(number));
                        }
                        assert_eq!(payload.len(), 1);
                        s.writes += 1;
                        s.battery_raw = payload[0];
                        if let Some((number, release)) = &s.held_write
                            && *number == s.writes
                        {
                            self.held = Some(release.clone());
                            s.holding.notify_one();
                        }
                        if s.fail_write == Some(s.writes) {
                            return Err(io::ErrorKind::Other.into());
                        }
                        vec![if s.battery_bad_ack {
                            payload[0] ^ 1
                        } else {
                            payload[0]
                        }]
                    }
                    (7, 0x8c) => {
                        s.power_reads += 1;
                        if let Some(hook) = &s.on_power {
                            let reads = s.power_reads;
                            self.fixture.hooked(|| hook(reads));
                        }
                        if let Some((at, actual)) = s.change_power
                            && at == s.power_reads
                        {
                            s.actual = actual;
                        }
                        if let Some((at, status)) = s.change_power_status
                            && at == s.power_reads
                        {
                            s.power_status = status;
                        }
                        if let Some((at, windows)) = s.change_windows
                            && at == s.power_reads
                        {
                            s.windows_on_battery = Some(windows);
                        }
                        vec![s.actual, s.recommended]
                    }
                    (0, 0x84) => {
                        assert_eq!(payload, [0, 0]);
                        s.device_mode_queries += 1;
                        if let Some(hook) = &s.device_mode_on_query {
                            let queries = s.device_mode_queries;
                            self.fixture.hooked(|| hook(queries));
                        }
                        if s.device_mode_timeout == Some(s.device_mode_queries)
                            || s.device_mode_mute
                        {
                            self.reply_timeout = true;
                        }
                        vec![s.device_mode, 0]
                    }
                    (0, 0x04) => {
                        assert!(matches!(payload, [0 | 3, 0]), "{payload:?}");
                        s.device_mode_sets += 1;
                        if let Some(hook) = &s.device_mode_on_set {
                            let mode = payload[0];
                            self.fixture.hooked(|| hook(id, mode));
                        }
                        if s.device_mode_fail_set {
                            return Err(io::ErrorKind::Other.into());
                        }
                        if !s.device_mode_ignore_set {
                            s.device_mode = payload[0];
                        }
                        let mut ack = payload.to_vec();
                        if s.device_mode_bad_ack {
                            ack[0] ^= 3;
                        }
                        ack
                    }
                    _ => panic!("unexpected typed fixture command {class:x}/{command:x}"),
                };
                let mut reply = [0; FRAME_LENGTH];
                reply[1..].copy_from_slice(
                    &RazerReport::create(id, class, command, &response)
                        .unwrap()
                        .encode(),
                );
                reply[1] = 2;
                if class == 7 && command == 0x8c && s.power_status != 0 {
                    reply[1] = s.power_status;
                }
                if class == 7 && command == 0x8c {
                    if let Some(edit) = &s.power_edit {
                        edit(&mut reply);
                    }
                    if s.power_timeout == Some(s.power_reads) {
                        self.reply_timeout = true;
                    }
                }
                if class == 13 && matches!(command, 1 | 2 | 7) && s.corrupt_ack == Some(s.writes) {
                    reply[2] ^= 1;
                }
                if class == 13 && command == 0x82 && s.writes == 2 {
                    self.reply_timeout = s.final_read_timeout;
                    if s.final_read_malformed {
                        reply[0] = 1;
                    }
                }
                if class == 7 && command == 0x92 && s.battery_reads > 1 && s.battery_bad_read {
                    reply[0] = 1;
                }
                if class == 7 && command == 0x8f && s.temporary_bad_read {
                    reply[0] = 1;
                }
                self.reply = Some(reply);
                self.pending = Some((0, frame));
            }
        }
        Ok(())
    }
    fn poll(&mut self) -> Option<io::Result<(usize, Frame)>> {
        if self
            .held
            .as_ref()
            .is_some_and(|release| !release.load(std::sync::atomic::Ordering::Acquire))
            || self.ready_at.is_some_and(|at| Instant::now() < at)
        {
            return None;
        }
        self.held = None;
        if let Some(error) = self.completion_error.take() {
            self.pending.take();
            return Some(Err(error.into()));
        }
        self.pending.take().map(Ok)
    }
    fn cancel(&mut self) {}
    fn poll_retirement(&mut self) -> io::Result<bool> {
        if self
            .held
            .as_ref()
            .is_some_and(|release| !release.load(std::sync::atomic::Ordering::Acquire))
        {
            return Ok(false);
        }
        self.pending = None;
        if !self.retired {
            self.fixture.state.lock().unwrap().retired += 1;
            self.retired = true;
        }
        Ok(true)
    }
}
#[path = "mutation_pace_tests.rs"]
mod pace_tests;
#[path = "mutation_pool_tests.rs"]
mod pool_tests;

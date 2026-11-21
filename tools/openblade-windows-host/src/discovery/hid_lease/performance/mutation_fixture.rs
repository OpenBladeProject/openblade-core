//! Explicit in-memory native-port inputs. No Windows constructor, path or token.
use super::mutation::Permit;
use super::worker::Reading;
use super::*;
use std::sync::{Arc, Mutex};
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
    pub(crate) final_read_timeout: bool,
    pub(crate) final_read_malformed: bool,
    pub(crate) on_power: Option<Arc<dyn Fn(usize) + Send + Sync>>,
    pub(crate) on_mutation_retired: Option<Arc<dyn Fn() + Send + Sync>>,
    pub(crate) mutation_worker_error: Option<Arc<dyn Fn() -> io::Error + Send + Sync>>,
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
    /// When the test sent the request that a thermal or fan recipe serves, on
    /// the test's clock. A recipe on paused time takes its deadline as that
    /// request's budget from its own start (see `worker`).
    pub(crate) arrival: Option<Instant>,
}
#[derive(Clone)]
pub(crate) struct Fixture {
    pub(crate) state: Arc<Mutex<State>>,
    transactions: Arc<Transactions>,
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
                let Some(admission) = authorize() else {
                    return Ok(None);
                };
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
                    let paused = (latency || permit.is_none() || thermal)
                        && s.held_read.is_none()
                        && s.held_write.is_none();
                    let arrival = if thermal { s.arrival.take() } else { None };
                    (paused, arrival)
                };
                let mut reader = Reader::new(fixture.port(), admission);
                // An observation, a thermal or fan recipe, and any job with a
                // latency run on paused time unless the job holds a transfer
                // for another thread to release. The clock starts at the real
                // instant the job starts and advances only through the job's
                // own waits, so neither a slow device nor a loaded host costs it
                // time, and the deadline is compared with exact virtual
                // progress. A recipe also takes the whole request budget from
                // its own start, so the host's time between the request and the
                // job does not count either. Battery and temporary-charge
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
                        if permit.is_some() {
                            let hook = fixture.state.lock().unwrap().on_mutation_retired.clone();
                            if let Some(hook) = hook {
                                hook();
                            }
                            let error = fixture.state.lock().unwrap().mutation_worker_error.clone();
                            if let Some(error) = error {
                                return Err(error());
                            }
                        }
                        let observed = observed.filter(|_| {
                            permit.as_ref().map_or_else(
                                || authorize() == Some(admission),
                                |p| p.result_admitted(admission),
                            )
                        });
                        Ok(observed.map(|performance| Reading {
                            admission,
                            performance,
                        }))
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
    fn reserve_pair(&mut self) -> Option<[u8; 2]> {
        self.fixture.transactions.reserve_pair()
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
                    hook(command, s.battery_reads);
                }
                let response = match (class, command) {
                    (13, 1 | 2 | 7) => {
                        if let Some(check) = &s.before_write {
                            check(s.writes + 1);
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
                        }
                        if s.fail_write == Some(s.writes) {
                            return Err(io::ErrorKind::Other.into());
                        }
                        payload.to_vec()
                    }
                    (13, 0x82) => {
                        if let Some(release) = &s.held_read {
                            self.held = Some(release.clone());
                        }
                        let index = (payload[1] - 1) as usize;
                        vec![
                            1,
                            payload[1],
                            if s.mismatch && s.writes == 2 || s.mismatch_after == Some(s.writes) {
                                4
                            } else {
                                s.selectors[index]
                            },
                            s.fans[index],
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
                            check(s.writes + 1);
                        }
                        assert_eq!(payload.len(), 1);
                        assert!(matches!(payload[0], 0 | 4));
                        s.writes += 1;
                        s.temporary_raw = payload[0];
                        if let Some((n, release)) = &s.held_write
                            && *n == s.writes
                        {
                            self.held = Some(release.clone());
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
                            check(s.writes + 1);
                        }
                        assert_eq!(payload.len(), 1);
                        s.writes += 1;
                        s.battery_raw = payload[0];
                        if let Some((number, release)) = &s.held_write
                            && *number == s.writes
                        {
                            self.held = Some(release.clone());
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
                            hook(s.power_reads);
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

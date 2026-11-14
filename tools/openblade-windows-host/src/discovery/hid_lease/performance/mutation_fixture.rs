//! Explicit in-memory native-port inputs. No Windows constructor, path or token.
use super::mutation::Permit;
use super::worker::Reading;
use super::*;
use std::sync::{Arc, Mutex};
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
    pub(crate) retired: usize,
    pub(crate) opened: usize,
    pub(crate) power_reads: usize,
    pub(crate) change_power: Option<(usize, u8)>,
    pub(crate) held_read: Option<Arc<AtomicBool>>,
    pub(crate) held_write: Option<(usize, Arc<AtomicBool>)>,
    pub(crate) before_write: Option<Arc<dyn Fn(usize) + Send + Sync>>,
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
                fixture.state.lock().unwrap().opened += 1;
                let mut reader = Reader::new(
                    MemoryPort {
                        fixture: fixture.clone(),
                        pending: None,
                        reply: None,
                        retired: false,
                        held: None,
                        reply_timeout: false,
                        completion_error: None,
                    },
                    admission,
                );
                tokio::runtime::Builder::new_current_thread()
                    .enable_time()
                    .build()?
                    .block_on(async {
                        let observed = if let Some(permit) = &permit {
                            reader.apply_preset(permit.as_ref(), job.limit()).await
                        } else {
                            reader.observe(&mut || authorize(), job.limit()).await
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
struct MemoryPort {
    fixture: Fixture,
    pending: Option<(usize, Frame)>,
    reply: Option<Frame>,
    retired: bool,
    held: Option<Arc<AtomicBool>>,
    reply_timeout: bool,
    completion_error: Option<io::ErrorKind>,
}
impl Port for MemoryPort {
    fn reserve_pair(&mut self) -> Option<[u8; 2]> {
        self.fixture.transactions.reserve_pair()
    }
    fn verify(&mut self, limit: Limit<'_>) -> io::Result<()> {
        limit.check()
    }
    fn submit(&mut self, transfer: Transfer, limit: Limit<'_>) -> io::Result<()> {
        limit.check()?;
        assert!(!self.retired && self.pending.is_none());
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
                if class == 7 && matches!(command, 0x92 | 0x8f) {
                    if let Some(hook) = &s.battery_getter_hook {
                        hook(command, s.battery_reads);
                    }
                }
                let response = match (class, command) {
                    (13, 1 | 2) => {
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
                        s.writes += 1;
                        if let Some((number, release)) = &s.held_write {
                            if *number == s.writes {
                                self.held = Some(release.clone());
                            }
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
                            if s.mismatch && s.writes == 2 {
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
                        if let Some((n, release)) = &s.held_write {
                            if *n == s.writes {
                                self.held = Some(release.clone());
                            }
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
                        if let Some((number, release)) = &s.held_write {
                            if *number == s.writes {
                                self.held = Some(release.clone());
                            }
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
                        if let Some((at, actual)) = s.change_power {
                            if at == s.power_reads {
                                s.actual = actual;
                            }
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
                if class == 13 && matches!(command, 1 | 2) && s.corrupt_ack == Some(s.writes) {
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

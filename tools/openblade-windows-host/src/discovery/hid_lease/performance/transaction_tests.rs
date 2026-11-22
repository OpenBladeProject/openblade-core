//! The transaction-ID pool against a modelled firmware reply mailbox. One
//! device owns the pool and a single mailbox; every read opens a new port and
//! Reader on it, as each native job opens a new handle.
use super::*;
use openblade_protocol::device_mode::DeviceMode;
use std::{
    collections::VecDeque,
    sync::{Arc, Mutex},
};

const PROOF: ReadAdmission = ReadAdmission {
    lifecycle: 1,
    generation: [7; 32],
};

/// What the modelled firmware does with one request.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum Firmware {
    /// Replaces the mailbox with this request's success reply.
    Answer,
    /// Replaces the mailbox with a busy reply bearing this request's ID.
    Busy,
    /// Never takes effect: the mailbox keeps the older reply.
    Ignore,
    /// Takes effect only later (`Device::late`); its GET never completes.
    Stall,
}

struct Device {
    transactions: Transactions,
    mailbox: Mutex<Frame>,
    // Steps for the next requests; every other request is answered.
    plan: Mutex<VecDeque<Firmware>>,
    sent: Mutex<Vec<Frame>>,
    selector: u8,
}

impl Device {
    fn new(selector: u8) -> Arc<Self> {
        Arc::new(Self {
            transactions: Transactions::new(),
            mailbox: Mutex::new([0; FRAME_LENGTH]),
            plan: Mutex::default(),
            sent: Mutex::default(),
            selector,
        })
    }

    fn then(&self, steps: impl IntoIterator<Item = Firmware>) {
        self.plan.lock().unwrap().extend(steps);
    }

    /// How many requests bore `id`.
    fn sends(&self, id: u8) -> usize {
        let sent = self.sent.lock().unwrap();
        sent.iter().filter(|frame| frame[2] == id).count()
    }

    /// The firmware answers the last request that bore `id`, after its GET
    /// was abandoned.
    fn late(&self, id: u8) {
        let request = *self
            .sent
            .lock()
            .unwrap()
            .iter()
            .rev()
            .find(|frame| frame[2] == id)
            .unwrap();
        *self.mailbox.lock().unwrap() = answer(&request, 2, self.selector);
    }

    fn port(self: &Arc<Self>) -> MailboxPort {
        MailboxPort {
            device: self.clone(),
            pending: None,
            stalled: false,
        }
    }
}

/// The reply the firmware gives `request`, with `status`.
fn answer(request: &Frame, status: u8, selector: u8) -> Frame {
    let report = &request[1..];
    let (id, class, command, channel) = (report[1], report[6], report[7], report[9]);
    let level = [1, channel, channel];
    let thermal = [1, channel, selector, 0];
    let payload: &[u8] = match (class, command) {
        (CLASS, GET_THERMAL) => &thermal,
        (CLASS, 0x87) => &level,
        (0, 0x84) => &[3, 0],
        _ => panic!("unexpected request {class:02x}/{command:02x}"),
    };
    let mut frame = [0; FRAME_LENGTH];
    frame[1..].copy_from_slice(
        &RazerReport::create(id, class, command, payload)
            .unwrap()
            .encode(),
    );
    frame[1] = status;
    frame
}

struct MailboxPort {
    device: Arc<Device>,
    pending: Option<(usize, Frame)>,
    // The last SET stalled, so its GET never completes.
    stalled: bool,
}

impl Port for MailboxPort {
    fn transactions(&self) -> &Transactions {
        &self.device.transactions
    }
    fn verify(&mut self, limit: Limit<'_>) -> io::Result<()> {
        limit.check()
    }
    fn submit(&mut self, transfer: Transfer, limit: Limit<'_>) -> io::Result<()> {
        limit.check()?;
        assert!(self.pending.is_none());
        match transfer {
            Transfer::SendGetter(frame) => {
                let device = &self.device;
                device.sent.lock().unwrap().push(frame);
                let step = device.plan.lock().unwrap().pop_front();
                let mut mailbox = device.mailbox.lock().unwrap();
                match step.unwrap_or(Firmware::Answer) {
                    Firmware::Answer => *mailbox = answer(&frame, 2, device.selector),
                    Firmware::Busy => *mailbox = answer(&frame, 1, device.selector),
                    Firmware::Ignore => {}
                    Firmware::Stall => self.stalled = true,
                }
                self.pending = Some((0, frame));
            }
            #[cfg(feature = "service-thermal-mutation-v1")]
            Transfer::SendMutation(_) => panic!("read-only fixture received a mutation"),
            Transfer::ReceiveReply if self.stalled => {}
            Transfer::ReceiveReply => {
                self.pending = Some((FRAME_LENGTH, *self.device.mailbox.lock().unwrap()));
            }
        }
        Ok(())
    }
    fn poll(&mut self) -> Option<io::Result<(usize, Frame)>> {
        self.pending.take().map(Ok)
    }
    fn cancel(&mut self) {}
    fn poll_retirement(&mut self) -> io::Result<bool> {
        self.pending = None;
        Ok(true)
    }
}

fn limit(cancelled: &AtomicBool) -> Limit<'_> {
    Limit {
        deadline: Instant::now() + Duration::from_secs(2),
        cancelled,
    }
}

/// One read job on a new port and Reader, retired before it returns.
async fn read(device: &Arc<Device>) -> Option<ObservedPerformance> {
    let cancelled = AtomicBool::new(false);
    let mut reader = Reader::new(device.port(), PROOF);
    let observed = reader.observe(&mut || Some(PROOF), limit(&cancelled)).await;
    reader.retire(None).await.unwrap();
    observed
}

#[tokio::test(start_paused = true)]
async fn unbounded_successful_reads_never_exhaust_the_pool() {
    let device = Device::new(0);
    for _ in 0..5_000 {
        assert_eq!(
            read(&device).await,
            Some(ObservedPerformance::BalancedAutomatic)
        );
    }
    // 10 000 IDs went through the 254 in the pool. Only the last reply can
    // still be read, so every other ID is free.
    assert_eq!(device.sent.lock().unwrap().len(), 10_000);
    assert!((1..=254).all(|id| device.sends(id) >= 39));
    assert_eq!(device.transactions.free(), 253);
}

#[tokio::test(start_paused = true)]
async fn unbounded_custom_reads_and_device_mode_jobs_never_exhaust_the_pool() {
    let device = Device::new(4);
    for _ in 0..2_000 {
        assert!(matches!(
            read(&device).await,
            Some(ObservedPerformance::Custom {
                manual: false,
                levels: Some(_),
            })
        ));
    }
    // The last 0D/82 and 0D/87 replies are the only ones still readable.
    assert_eq!(device.transactions.free(), 252);

    let device = Device::new(0);
    for _ in 0..2_000 {
        let cancelled = AtomicBool::new(false);
        let mut reader = Reader::new(device.port(), PROOF);
        let observed = reader
            .observe_job(&mut || Some(PROOF), limit(&cancelled), true)
            .await;
        reader.retire(None).await.unwrap();
        assert_eq!(
            observed,
            Some((
                ObservedPerformance::BalancedAutomatic,
                DeviceModeRead::Confirmed(DeviceMode::Driver)
            ))
        );
    }
    // Each query's unsent second ID was released; the last 0D/82 and 00/84
    // replies are the only ones still readable.
    assert_eq!(device.sent.lock().unwrap().len(), 6_000);
    assert_eq!(device.transactions.free(), 252);
}

#[tokio::test(start_paused = true)]
async fn an_uncertain_exchange_never_frees_its_id_even_on_a_new_handle() {
    for failure in [Firmware::Busy, Firmware::Ignore, Firmware::Stall] {
        let device = Device::new(0);
        assert!(read(&device).await.is_some());
        // ID 3 is sent and gets a busy reply, the older reply 2, or no reply.
        device.then([failure]);
        assert_eq!(read(&device).await, None, "{failure:?}");
        if failure == Firmware::Stall {
            // Its reply arrives after the handle retired.
            device.late(3);
        }
        // Thousands of later reads on new handles wrap the pool many times
        // and never issue 3 again.
        for _ in 0..2_000 {
            assert!(read(&device).await.is_some(), "{failure:?}");
        }
        assert_eq!(device.sends(3), 1, "{failure:?}");
        // The unsent 4 was released; 3 and the last reply's ID remain.
        assert!(device.sends(4) > 1, "{failure:?}");
        assert_eq!(device.transactions.free(), 252, "{failure:?}");
    }
}

#[tokio::test(start_paused = true)]
async fn a_late_reply_is_never_accepted_for_a_new_request() {
    let device = Device::new(0);
    // ID 1 is answered; the SET for 2 does not take effect, so the mailbox
    // keeps reply 1 and the read fails.
    device.then([Firmware::Answer, Firmware::Ignore]);
    assert_eq!(read(&device).await, None);
    let stale = *device.mailbox.lock().unwrap();
    // A new request bearing 1 for the same channel would accept it.
    let reissued = device.sent.lock().unwrap()[0];
    assert!(transactions::terminal_reply(&reissued, FRAME_LENGTH, &stale).is_some());
    // While nothing replaces it, every read fails on a mismatched reply, and
    // 1 is not issued again however often the pool wraps.
    for _ in 0..150 {
        device.then([Firmware::Ignore]);
        assert_eq!(read(&device).await, None);
    }
    // The pool wrapped: the second read reserved 4 and released it unsent,
    // and a later read sent it.
    assert_eq!(device.sends(4), 1);
    assert_eq!(device.sends(1), 1);
    // Once a newer 0D/82 reply replaced it, 1 is issued again and its new
    // request reads its own reply.
    for _ in 0..200 {
        assert!(read(&device).await.is_some());
    }
    assert!(device.sends(1) > 1);
    assert_eq!(*device.mailbox.lock().unwrap(), {
        let last = *device.sent.lock().unwrap().last().unwrap();
        answer(&last, 2, 0)
    });
}

#[test]
fn an_answered_id_is_freed_only_by_a_later_answer_to_the_same_command() {
    let transactions = Transactions::new();
    let [a, b] = transactions.reserve_pair().unwrap();
    assert!(transactions.sent(a));
    transactions.answered(a, CLASS, GET_THERMAL);
    // Another command's reply proves nothing if the firmware keeps one reply
    // per command.
    assert!(transactions.sent(b));
    transactions.answered(b, 7, 0x8c);
    assert_eq!(transactions.free(), 252);
    let [c, d] = transactions.reserve_pair().unwrap();
    assert!(transactions.sent(c));
    transactions.answered(c, CLASS, GET_THERMAL);
    // a is free; b waits for 07/8C, c for 0D/82, and d is held.
    assert_eq!(transactions.free(), 251);
    transactions.release(d);
    assert_eq!(transactions.free(), 252);
    // A sent ID that is released or retired stays quarantined.
    let [e, f] = transactions.reserve_pair().unwrap();
    assert!(transactions.sent(e));
    transactions.release(e);
    transactions.release(f);
    assert_eq!(transactions.free(), 251);
}

#[test]
fn freed_ids_return_only_after_every_other_free_id() {
    let transactions = Transactions::new();
    let first = transactions.reserve_pair().unwrap();
    assert_eq!(first, [1, 2]);
    for id in first {
        transactions.release(id);
    }
    for pair in 1u8..127 {
        assert_eq!(
            transactions.reserve_pair(),
            Some([pair * 2 + 1, pair * 2 + 2])
        );
    }
    assert_eq!(transactions.reserve_pair(), Some([1, 2]));
    assert_eq!(transactions.reserve_pair(), None);
}

#[test]
fn pinned_ids_are_never_freed_until_unpinned() {
    let transactions = Transactions::new();
    let [a, b] = transactions.reserve_pair().unwrap();
    transactions.pin(a, true);
    transactions.pin(b, true);
    assert!(transactions.sent(a));
    transactions.answered(a, CLASS, GET_THERMAL);
    let [c, _] = transactions.reserve_pair().unwrap();
    assert!(transactions.sent(c));
    transactions.answered(c, CLASS, GET_THERMAL);
    transactions.release(b);
    assert_eq!(transactions.free(), 250);
    // Unpinned, b is released and a waits for the next 0D/82 answer.
    transactions.pin(a, false);
    transactions.pin(b, false);
    transactions.release(b);
    assert_eq!(transactions.free(), 251);
    let [e, _] = transactions.reserve_pair().unwrap();
    assert!(transactions.sent(e));
    transactions.answered(e, CLASS, GET_THERMAL);
    // a and c are free; e is the readable reply and the other is held.
    assert_eq!(transactions.free(), 251);
}

#[test]
fn only_a_correlated_terminal_reply_answers_a_request() {
    let mut request = [0; FRAME_LENGTH];
    request[1..].copy_from_slice(
        &RazerReport::create(9, CLASS, GET_THERMAL, &[1, 1, 0, 0])
            .unwrap()
            .encode(),
    );
    let reply = |status: u8| answer(&request, status, 0);
    let answered = Some((9, CLASS, GET_THERMAL));
    assert_eq!(
        transactions::terminal_reply(&request, 90, &reply(2)),
        answered
    );
    assert_eq!(
        transactions::terminal_reply(&request, 91, &reply(2)),
        answered
    );
    // A completed FAILED or NOT_SUPPORTED rejection is terminal too.
    for status in [3, 5] {
        assert_eq!(
            transactions::terminal_reply(&request, 91, &reply(status)),
            answered
        );
    }
    // New, busy and timeout replies are not.
    for status in [0, 1, 4] {
        assert_eq!(
            transactions::terminal_reply(&request, 91, &reply(status)),
            None
        );
    }
    let corrupt = |byte: usize| {
        let mut frame = reply(2);
        frame[byte] ^= 0x40;
        transactions::terminal_reply(&request, 91, &frame)
    };
    // Report ID, transaction, class, command and checksum.
    for byte in [0, 2, 7, 8, 89] {
        assert_eq!(corrupt(byte), None, "{byte}");
    }
    assert_eq!(transactions::terminal_reply(&request, 89, &reply(2)), None);
}

#[test]
fn quarantined_ids_exhaust_the_pool() {
    let transactions = Transactions::new();
    while let Some(pair) = transactions.reserve_pair() {
        for id in pair {
            assert!(transactions.sent(id));
            transactions.release(id);
        }
    }
    assert_eq!(transactions.free(), 0);
    let mut reader = Reader::new(Device::new(0).port(), PROOF);
    reader.port.device = Arc::new(Device {
        transactions,
        mailbox: Mutex::new([0; FRAME_LENGTH]),
        plan: Mutex::default(),
        sent: Mutex::default(),
        selector: 0,
    });
    assert_eq!(reader.reserve_pair(), None);
    assert!(reader.exhausted);
}

//! The transaction-ID pool against a modelled firmware reply mailbox. One
//! device owns the pool and a single mailbox; every read or setter job opens
//! a new port and Reader on it, as each native job opens a new handle.
use super::*;
use openblade_protocol::device_mode::DeviceMode;
use std::{
    collections::VecDeque,
    sync::{Arc, Mutex, atomic::Ordering},
};

// The setter the modelled firmware echoes.
const SET_THERMAL: u8 = 0x02;

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
    /// Answers, but Windows reports the submission failed.
    Fail,
}

struct Device {
    transactions: Transactions,
    mailbox: Mutex<Frame>,
    // Steps for the next requests; every other request is answered.
    plan: Mutex<VecDeque<Firmware>>,
    sent: Mutex<Vec<Frame>>,
    selector: u8,
    // Retirement is never confirmed.
    unretired: AtomicBool,
}

impl Device {
    fn new(selector: u8) -> Arc<Self> {
        Arc::new(Self {
            transactions: Transactions::new(),
            mailbox: Mutex::new([0; FRAME_LENGTH]),
            plan: Mutex::default(),
            sent: Mutex::default(),
            selector,
            unretired: AtomicBool::new(false),
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

/// The reply the firmware gives `request`, with `status`. A setter's reply
/// echoes its arguments.
fn answer(request: &Frame, status: u8, selector: u8) -> Frame {
    let report = &request[1..];
    let (id, class, command, channel) = (report[1], report[6], report[7], report[9]);
    let level = [1, channel, channel];
    let thermal = [1, channel, selector, 0];
    let payload: &[u8] = match (class, command) {
        (CLASS, GET_THERMAL) => &thermal,
        (CLASS, 0x87) => &level,
        (0, 0x84) => &[3, 0],
        (CLASS, SET_THERMAL) => &report[8..8 + usize::from(report[5])],
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

impl MailboxPort {
    /// A getter's or setter's SET reaches the firmware, which takes the next
    /// planned step.
    fn send(&mut self, frame: Frame) -> io::Result<()> {
        let device = &self.device;
        device.sent.lock().unwrap().push(frame);
        let step = device.plan.lock().unwrap().pop_front();
        let mut mailbox = device.mailbox.lock().unwrap();
        match step.unwrap_or(Firmware::Answer) {
            Firmware::Answer => *mailbox = answer(&frame, 2, device.selector),
            Firmware::Busy => *mailbox = answer(&frame, 1, device.selector),
            Firmware::Ignore => {}
            Firmware::Stall => self.stalled = true,
            Firmware::Fail => {
                *mailbox = answer(&frame, 2, device.selector);
                return Err(io::ErrorKind::BrokenPipe.into());
            }
        }
        self.pending = Some((0, frame));
        Ok(())
    }
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
            Transfer::SendGetter(frame) => self.send(frame)?,
            #[cfg(feature = "service-thermal-mutation-v1")]
            Transfer::SendMutation(frame) => self.send(frame)?,
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
        Ok(!self.device.unretired.load(Ordering::Acquire))
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
async fn unbounded_answered_custom_reads_and_device_mode_jobs_never_exhaust_the_pool() {
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
async fn an_unconfirmed_retirement_keeps_even_its_unsent_ids() {
    async fn job(device: &Arc<Device>) -> io::Result<()> {
        let cancelled = AtomicBool::new(false);
        let mut reader = Reader::new(device.port(), PROOF);
        let observed = reader
            .observe_job(&mut || Some(PROOF), limit(&cancelled), true)
            .await;
        assert!(observed.is_some());
        reader
            .retire(Some(Instant::now() + Duration::from_secs(1)))
            .await
    }
    let device = Device::new(0);
    job(&device).await.unwrap();
    let free = device.transactions.free();
    device.unretired.store(true, Ordering::Release);
    assert_eq!(
        job(&device).await.unwrap_err().kind(),
        io::ErrorKind::TimedOut
    );
    // Its replies replaced the first job's, which frees those, but the query's
    // unsent second ID could still be sent until retirement is confirmed.
    assert_eq!(device.transactions.free(), free - 1);
}

#[tokio::test(start_paused = true)]
async fn an_uncertain_exchange_never_frees_its_id_even_on_a_new_handle() {
    for failure in [
        Firmware::Busy,
        Firmware::Ignore,
        Firmware::Stall,
        Firmware::Fail,
    ] {
        let device = Device::new(0);
        assert!(read(&device).await.is_some());
        // ID 3 is sent and gets a busy reply, the older reply 2 or no reply,
        // or its submission fails after reaching the firmware.
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
fn an_answer_frees_the_older_answered_id_of_its_command() {
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
fn only_held_ids_count_as_unsent() {
    let transactions = Transactions::new();
    let [a, b] = transactions.reserve_pair().unwrap();
    assert!(transactions.unsent(&[a, b]));
    assert!(transactions.sent(a));
    assert!(!transactions.unsent(&[a, b]));
    assert!(transactions.unsent(&[b]));
    // Answered, then freed by a newer answer: it was still sent.
    transactions.answered(a, CLASS, GET_THERMAL);
    assert!(transactions.sent(b));
    transactions.answered(b, CLASS, GET_THERMAL);
    assert!(!transactions.unsent(&[a]));
    // A released ID is free, not held.
    let [c, d] = transactions.reserve_pair().unwrap();
    transactions.release(c);
    assert!(!transactions.unsent(&[c]));
    assert!(transactions.unsent(&[d]));
    transactions.poison();
    assert!(!transactions.unsent(&[d]));
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
    transactions.pin(a);
    transactions.pin(b);
    assert!(transactions.sent(a));
    transactions.answered(a, CLASS, GET_THERMAL);
    let [c, _] = transactions.reserve_pair().unwrap();
    assert!(transactions.sent(c));
    transactions.answered(c, CLASS, GET_THERMAL);
    transactions.release(b);
    assert_eq!(transactions.free(), 250);
    // Unpinned, the unsent b is free and a waits for the next 0D/82 answer.
    transactions.unpin([a, b]);
    assert_eq!(transactions.free(), 251);
    let [e, _] = transactions.reserve_pair().unwrap();
    assert!(transactions.sent(e));
    transactions.answered(e, CLASS, GET_THERMAL);
    // a and c are free; e is the readable reply and the other is held.
    assert_eq!(transactions.free(), 251);
}

#[test]
fn unpinning_frees_only_the_unsent_and_never_an_id_issued_again() {
    let transactions = Transactions::new();
    let [quarantined, answered] = transactions.reserve_pair().unwrap();
    let [unsent, other] = transactions.reserve_pair().unwrap();
    for id in [quarantined, answered, unsent, other] {
        transactions.pin(id);
    }
    // Sent without a terminal reply, answered, and never sent.
    assert!(transactions.sent(quarantined));
    assert!(transactions.sent(answered));
    transactions.answered(answered, CLASS, GET_THERMAL);
    assert_eq!(transactions.free(), 250);
    transactions.unpin([quarantined, answered, unsent]);
    assert_eq!(transactions.free(), 251);
    // The unsent ID is issued again, now held by a Reader that has not
    // pinned it. Unpinning it again must not free that Reader's ID.
    let reissued = drain_until(&transactions, unsent);
    transactions.unpin([unsent]);
    assert!(transactions.unsent(&[reissued]));
    transactions.release(reissued);
    // The answered ID waits for the next 0D/82 answer, as if never pinned;
    // the quarantined one is never issued again.
    let [reply, held] = transactions.reserve_pair().unwrap();
    assert!(transactions.sent(reply));
    transactions.answered(reply, CLASS, GET_THERMAL);
    transactions.release(held);
    // An odd number of free IDs leaves one unissued per round, so the
    // rounds are taken together.
    let issued: Vec<u8> = (0..3).flat_map(|_| drain(&transactions)).collect();
    assert!(issued.contains(&answered) && issued.contains(&unsent));
    assert!(!issued.contains(&quarantined) && !issued.contains(&other));
    // A pinned ID that was never unpinned stays out, and a poisoned pool
    // frees nothing.
    transactions.poison();
    transactions.unpin([other]);
    assert_eq!(transactions.free(), 0);
}

/// Reserves pairs until `id` is issued, releasing every other ID unsent.
/// Returns `id`, held.
fn drain_until(transactions: &Transactions, id: u8) -> u8 {
    loop {
        let pair = transactions.reserve_pair().unwrap();
        for other in pair.into_iter().filter(|other| *other != id) {
            transactions.release(other);
        }
        if pair.contains(&id) {
            return id;
        }
    }
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
    let corrupt = |byte: usize, checksum: bool| {
        let mut frame = reply(2);
        frame[byte] ^= 0x40;
        if checksum {
            frame[89] = openblade_protocol::calculate_checksum(&frame[1..]).unwrap();
        }
        transactions::terminal_reply(&request, 91, &frame)
    };
    // A well-formed reply to another transaction, class or command.
    for byte in [2, 7, 8] {
        assert_eq!(corrupt(byte, true), None, "{byte}");
    }
    // The report ID and the checksum itself.
    for byte in [0, 89] {
        assert_eq!(corrupt(byte, false), None, "{byte}");
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
        unretired: AtomicBool::new(false),
    });
    assert_eq!(reader.reserve_pair(), None);
    assert!(reader.exhausted);
}

/// Reserves every pair the pool can issue, then releases them unsent.
fn drain(transactions: &Transactions) -> Vec<u8> {
    let mut issued = Vec::new();
    while let Some(pair) = transactions.reserve_pair() {
        issued.extend(pair);
    }
    for id in &issued {
        transactions.release(*id);
    }
    issued
}

#[test]
fn an_answer_to_another_class_or_command_frees_nothing() {
    let transactions = Transactions::new();
    let answer = |class, command| {
        let [id, held] = transactions.reserve_pair().unwrap();
        assert!(transactions.sent(id));
        transactions.answered(id, class, command);
        transactions.release(held);
        id
    };
    let a = answer(CLASS, GET_THERMAL);
    // The same command in another class, then another command in this class.
    answer(7, GET_THERMAL);
    answer(CLASS, 0x87);
    assert!(!drain(&transactions).contains(&a));
    // Only a newer 0D/82 answer replaces a's reply.
    answer(CLASS, GET_THERMAL);
    assert!(drain(&transactions).contains(&a));
}

#[tokio::test(start_paused = true)]
async fn a_poisoned_pool_submits_nothing() {
    let device = Device::new(0);
    let mut reader = Reader::new(device.port(), PROOF);
    let [id, _] = reader.reserve_pair().unwrap();
    device.transactions.poison();
    assert!(!device.transactions.sent(id));
    let mut frame = [0; FRAME_LENGTH];
    frame[1..].copy_from_slice(
        &RazerReport::create(id, CLASS, GET_THERMAL, &[1, 1, 0, 0])
            .unwrap()
            .encode(),
    );
    let cancelled = AtomicBool::new(false);
    let mut attempt = Attempt {
        reader: &mut reader,
        completed: false,
    };
    let sent = attempt
        .transfer(Transfer::SendGetter(frame), limit(&cancelled))
        .await;
    drop(attempt);
    assert!(sent.is_err());
    assert!(device.sent.lock().unwrap().is_empty());
    // It issues nothing either.
    assert_eq!(reader.reserve_pair(), None);
    reader.retire(None).await.unwrap();
}

#[test]
fn a_seeded_pool_starts_its_round_where_the_seed_puts_it() {
    assert_eq!(Transactions::seeded(0).reserve_pair(), Some([1, 2]));
    assert_eq!(Transactions::seeded(253).reserve_pair(), Some([254, 1]));
    assert_eq!(Transactions::seeded(254 + 9).reserve_pair(), Some([10, 11]));
    // The process pool draws its start once, then issues every ID once per
    // round in order.
    let transactions = Transactions::randomized();
    let mut issued = Vec::new();
    while let Some(pair) = transactions.reserve_pair() {
        issued.extend(pair);
    }
    assert_eq!(issued.len(), 254);
    assert!(
        issued
            .windows(2)
            .all(|ids| ids[1] == if ids[0] == 254 { 1 } else { ids[0] + 1 })
    );
}

/// One setter job on a new port and Reader: a 0D/02 setter, then its reply,
/// retired before it returns. Returns the setter's ID and whether its echo
/// was read.
#[cfg(feature = "service-thermal-mutation-v1")]
async fn set(device: &Arc<Device>) -> (u8, bool) {
    let cancelled = AtomicBool::new(false);
    let mut reader = Reader::new(device.port(), PROOF);
    let [id, _] = reader.reserve_pair().unwrap();
    let setter = RazerReport::create(id, CLASS, SET_THERMAL, &[1, 1, 0, 0]).unwrap();
    let mut frame = [0; FRAME_LENGTH];
    frame[1..].copy_from_slice(&setter.encode());
    let mut attempt = Attempt {
        reader: &mut reader,
        completed: false,
    };
    let echoed = async {
        attempt
            .transfer(Transfer::SendMutation(frame), limit(&cancelled))
            .await
            .ok()?;
        let (_, reply) = attempt
            .transfer(Transfer::ReceiveReply, limit(&cancelled))
            .await
            .ok()?;
        setter.validate_response(&reply[1..], None).ok()
    }
    .await
    .is_some();
    attempt.completed = echoed;
    drop(attempt);
    reader.retire(None).await.unwrap();
    (id, echoed)
}

#[cfg(feature = "service-thermal-mutation-v1")]
#[tokio::test(start_paused = true)]
async fn a_setter_id_returns_only_after_a_later_echo_of_its_command() {
    // Reads never replace an echo; the next setter's echo does.
    let device = Device::new(0);
    let (first, echoed) = set(&device).await;
    assert!(echoed);
    for _ in 0..300 {
        assert!(read(&device).await.is_some());
    }
    assert_eq!(device.sends(first), 1);
    let (second, echoed) = set(&device).await;
    assert!(echoed);
    let since = device.sent.lock().unwrap().len();
    for _ in 0..300 {
        assert!(read(&device).await.is_some());
    }
    assert!(device.sends(first) > 1);
    assert!(
        device.sent.lock().unwrap()[since..]
            .iter()
            .all(|frame| frame[2] != second)
    );

    // A setter without its echo keeps its ID out of the pool, whatever
    // answers follow on new handles.
    for failure in [
        Firmware::Busy,
        Firmware::Ignore,
        Firmware::Stall,
        Firmware::Fail,
    ] {
        let device = Device::new(0);
        assert!(read(&device).await.is_some());
        device.then([failure]);
        let (uncertain, echoed) = set(&device).await;
        assert!(!echoed, "{failure:?}");
        if failure == Firmware::Stall {
            device.late(uncertain);
        }
        for _ in 0..3 {
            assert!(set(&device).await.1, "{failure:?}");
        }
        for _ in 0..300 {
            assert!(read(&device).await.is_some(), "{failure:?}");
        }
        assert_eq!(device.sends(uncertain), 1, "{failure:?}");
    }
}

/// A permit that keeps the pins of one record and settles them on request.
#[cfg(feature = "service-thermal-mutation-v1")]
#[derive(Default)]
struct Owner {
    pins: super::mutation::RecordPins,
    settled: super::mutation::SettledPins,
}
#[cfg(feature = "service-thermal-mutation-v1")]
impl super::mutation::Permit for Owner {
    fn authorize(&self) -> Option<ReadAdmission> {
        None
    }
    fn resolve(
        &self,
        _: i32,
        _: ObservedPerformance,
        _: &AtomicBool,
    ) -> io::Result<Option<openblade_domain::thermal_mutation::Destination>> {
        Ok(None)
    }
    fn record(&self, _: openblade_domain::thermal_mutation::Progress) -> io::Result<()> {
        Ok(())
    }
    fn retained_pins(&self, ids: &[u8]) {
        self.pins.retain(ids);
    }
    fn settled_pins(&self) -> Vec<u8> {
        self.settled.take()
    }
}

#[cfg(feature = "service-thermal-mutation-v1")]
#[tokio::test(start_paused = true)]
async fn a_retained_pair_returns_only_once_its_record_settles_and_a_reader_retires() {
    use super::mutation::Permit;
    let device = Device::new(0);
    let transactions = &device.transactions;
    let owner = Owner::default();
    let mut first = Reader::new(device.port(), PROOF);
    let [sent, unsent] = first.reserve_retained_pair().unwrap();
    first.retain(&owner);
    // Sent without a correlated terminal reply.
    assert!(transactions.sent(sent));
    first.retire(None).await.unwrap();
    // Retained across the Reader's retirement and every round of the pool.
    for _ in 0..3 {
        let issued = drain(transactions);
        assert!(!issued.contains(&sent) && !issued.contains(&unsent));
    }
    // The record settles while a later Reader holds IDs of its own, and
    // twice: the second settle queues nothing.
    let mut second = Reader::new(device.port(), PROOF);
    let own = second.reserve_pair().unwrap();
    owner.settled.settle(&owner.pins);
    owner.settled.settle(&owner.pins);
    second.settled.extend(owner.settled_pins());
    assert_eq!(second.settled, [sent, unsent]);
    assert!(owner.settled_pins().is_empty());
    // Nothing returns before that Reader retires.
    assert_eq!(transactions.free(), 250);
    second.retire(None).await.unwrap();
    assert_eq!(transactions.free(), 253);
    let issued: Vec<u8> = (0..3).flat_map(|_| drain(transactions)).collect();
    assert!(issued.contains(&unsent) && own.iter().all(|id| issued.contains(id)));
    assert!(!issued.contains(&sent), "a sent ID stays quarantined");
}

// A Reader whose retirement is never confirmed may still have a request in
// flight, so it frees none of its pins: neither its own provisional pair nor
// a settled record's pins it took.
#[cfg(feature = "service-thermal-mutation-v1")]
#[tokio::test(start_paused = true)]
async fn an_unconfirmed_retirement_frees_no_pins() {
    use super::mutation::Permit;
    let device = Device::new(0);
    let transactions = &device.transactions;
    let owner = Owner::default();
    let mut first = Reader::new(device.port(), PROOF);
    let retained = first.reserve_retained_pair().unwrap();
    first.retain(&owner);
    first.retire(None).await.unwrap();
    owner.settled.settle(&owner.pins);
    let mut second = Reader::new(device.port(), PROOF);
    let provisional = second.reserve_retained_pair().unwrap();
    second.settled.extend(owner.settled_pins());
    let free = transactions.free();
    device.unretired.store(true, Ordering::Release);
    let deadline = Instant::now() + Duration::from_millis(50);
    assert!(second.retire(Some(deadline)).await.is_err());
    assert_eq!(transactions.free(), free);
    for _ in 0..3 {
        let issued = drain(transactions);
        assert!(
            retained
                .iter()
                .chain(&provisional)
                .all(|id| !issued.contains(id))
        );
    }
}

// A restarted Service opens the same device with a fresh pool. The Service
// quarantines the IDs its journal names that an earlier process may have
// left unanswered (`transaction_seed`), because the mailbox outlived it.
#[cfg(feature = "service-thermal-mutation-v1")]
#[tokio::test(start_paused = true)]
async fn a_restarted_pool_never_issues_an_id_an_earlier_process_left_unanswered() {
    // The earlier process's SET bearing 1 stalls, its read is abandoned and
    // the firmware answers it later, after that process has gone.
    let earlier = Device::new(0);
    earlier.then([Firmware::Stall]);
    assert_eq!(read(&earlier).await, None);
    earlier.late(1);
    let stale = *earlier.mailbox.lock().unwrap();
    let restart = || {
        let device = Device::new(0);
        *device.mailbox.lock().unwrap() = stale;
        device
    };
    // Unseeded, the new pool issues 1 first; its SET does not take effect
    // and the read accepts the earlier process's reply as its own.
    let unseeded = restart();
    unseeded.then([Firmware::Ignore]);
    assert!(read(&unseeded).await.is_some());
    assert_eq!(unseeded.sends(1), 1);
    // Seeded, 1 is never issued: the same failure reads a reply bearing
    // another ID and is refused, and later reads are answered.
    let seeded = restart();
    seeded.transactions.quarantine(&[1]);
    seeded.then([Firmware::Ignore]);
    assert_eq!(read(&seeded).await, None);
    for _ in 0..300 {
        assert!(read(&seeded).await.is_some());
    }
    assert_eq!(seeded.sends(1), 0);
}

#[cfg(feature = "service-thermal-mutation-v1")]
#[test]
fn quarantine_takes_only_free_ids() {
    let transactions = Transactions::new();
    let [held, pinned] = transactions.reserve_pair().unwrap();
    transactions.pin(pinned);
    let [answered, free] = transactions.reserve_pair().unwrap();
    assert!(transactions.sent(answered));
    transactions.answered(answered, CLASS, GET_THERMAL);
    transactions.release(free);
    let before = transactions.free();
    // 0 and 255 are never issued, so they are ignored.
    transactions.quarantine(&[0, held, pinned, answered, free, 255]);
    assert_eq!(transactions.free(), before - 1);
    // The others keep their own rules: the held and pinned IDs were never
    // sent, and the answered one returns after its command's next answer.
    transactions.release(held);
    transactions.unpin([pinned]);
    let [next, spare] = transactions.reserve_pair().unwrap();
    assert!(transactions.sent(next));
    transactions.answered(next, CLASS, GET_THERMAL);
    transactions.release(spare);
    let issued = drain(&transactions);
    for id in [held, pinned, answered] {
        assert!(issued.contains(&id), "{id}");
    }
    assert!(!issued.contains(&free));
}

// The keyboard Driver-mode record names its newest claiming job's four IDs
// (`transaction_seed`), so a restarted pool seeded from it never issues them.
// Reissued, the setter ID would accept the acknowledgement the earlier
// process's 00/04 left in the mailbox, which echoes the same arguments.
#[cfg(feature = "service-keyboard-driver-mode-v1")]
#[test]
fn a_restarted_pool_never_issues_the_driver_mode_records_ids() {
    use openblade_protocol::device_mode::DeviceModeSet;
    // A fresh pool issues 1 to 4 first: the earlier job's baseline, setter,
    // readback and spare, as its record names them.
    let earlier = Transactions::new();
    let [baseline, set] = earlier.reserve_pair().unwrap();
    let [readback, spare] = earlier.reserve_pair().unwrap();
    let record = [baseline, set, readback, spare];
    assert_eq!(record, [1, 2, 3, 4]);
    // Its setter's success acknowledgement stays in the mailbox.
    let mut stale = [0; FRAME_LENGTH];
    stale[1..].copy_from_slice(
        &DeviceModeSet::new(set, DeviceMode::Driver)
            .request()
            .encode(),
    );
    stale[1] = 2;
    // Unseeded, the restarted pool reissues the setter ID, whose setter
    // would take that acknowledgement as its own.
    let unseeded = Transactions::new();
    let [_, reissued] = unseeded.reserve_pair().unwrap();
    assert_eq!(reissued, set);
    let reused = DeviceModeSet::new(reissued, DeviceMode::Driver);
    assert!(reused.acknowledge(&stale[1..]).is_ok());
    // Seeded, none of the four is issued, and a setter on any ID it does
    // issue refuses the stale acknowledgement.
    let seeded = Transactions::new();
    seeded.quarantine(&record);
    assert_eq!(seeded.free(), 254 - record.len());
    for _ in 0..3 {
        let issued = drain(&seeded);
        assert!(!issued.iter().any(|id| record.contains(id)), "{issued:?}");
        for id in issued {
            let fresh = DeviceModeSet::new(id, DeviceMode::Driver);
            assert!(fresh.acknowledge(&stale[1..]).is_err(), "{id}");
        }
    }
}

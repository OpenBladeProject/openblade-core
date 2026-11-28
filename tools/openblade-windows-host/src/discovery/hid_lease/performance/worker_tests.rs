use super::{
    tests::{ADMITTED, Script},
    worker::{Reading, Worker},
    *,
};
use std::sync::{
    Arc, Mutex,
    atomic::{AtomicU8, AtomicUsize, Ordering},
    mpsc,
};
use tokio::{sync::oneshot, time::timeout};

#[derive(Clone, Copy, PartialEq, Eq)]
pub(crate) enum BlockAt {
    Verify,
    Submit,
    GpuSubmit,
    Retire,
}

struct Block {
    at: BlockAt,
    entered: oneshot::Sender<()>,
    release: mpsc::Receiver<()>,
}

pub(crate) struct Held {
    pub(crate) entered: oneshot::Receiver<()>,
    pub(crate) release: mpsc::Sender<()>,
    pub(crate) submitted: Arc<AtomicUsize>,
    pub(crate) cancelled: Arc<AtomicBool>,
}

struct BlockingPort {
    script: Script,
    block: Option<Block>,
    submitted: Arc<AtomicUsize>,
    cancelled: Arc<AtomicBool>,
}

impl BlockingPort {
    fn block(&mut self, at: BlockAt) {
        if self.block.as_ref().is_some_and(|block| block.at == at) {
            let block = self.block.take().unwrap();
            let _ = block.entered.send(());
            // Releasing or dropping the test controller lets the fake native call return.
            let _ = block.release.recv();
        }
    }
}

impl Port for BlockingPort {
    fn transactions(&self) -> &Transactions {
        self.script.transactions()
    }

    fn verify(&mut self, limit: Limit<'_>) -> io::Result<()> {
        self.block(BlockAt::Verify);
        self.script.verify(limit)
    }

    fn submit(&mut self, transfer: Transfer, limit: Limit<'_>) -> io::Result<()> {
        limit.check()?;
        let gpu =
            matches!(&transfer, Transfer::SendGetter(frame) if frame[8] == 0x87 && frame[10] == 2);
        self.script.submit(transfer, limit)?;
        self.submitted.fetch_add(1, Ordering::AcqRel);
        self.block(if gpu {
            BlockAt::GpuSubmit
        } else {
            BlockAt::Submit
        });
        Ok(())
    }

    fn poll(&mut self) -> Option<io::Result<(usize, Frame)>> {
        self.script.poll()
    }

    fn cancel(&mut self) {
        self.cancelled.store(true, Ordering::Release);
        self.script.cancel();
    }

    fn poll_retirement(&mut self) -> io::Result<bool> {
        self.block(BlockAt::Retire);
        self.script.poll_retirement()
    }
}

pub(crate) fn blocking_worker(at: BlockAt, fence: Option<&'static AtomicBool>) -> (Worker, Held) {
    blocking_worker_with_authority(at, fence, || Some(ADMITTED))
}

pub(crate) fn blocking_worker_with_authority<A: ObservationProof>(
    at: BlockAt,
    fence: Option<&'static AtomicBool>,
    authorize: impl Fn() -> Option<A> + Send + Sync + 'static,
) -> (Worker<A>, Held) {
    blocking_worker_with_script(at, fence, authorize, || Script::pair([2, 2]))
}

pub(crate) fn blocking_custom_worker_with_authority<A: ObservationProof>(
    at: BlockAt,
    authorize: impl Fn() -> Option<A> + Send + Sync + 'static,
) -> (Worker<A>, Held) {
    blocking_worker_with_script(at, None, authorize, || {
        Script::custom(0, &[1, 1, 1], &[1, 2, 2], 1)
    })
}
fn blocking_worker_with_script<A: ObservationProof>(
    at: BlockAt,
    fence: Option<&'static AtomicBool>,
    authorize: impl Fn() -> Option<A> + Send + Sync + 'static,
    script: impl Fn() -> Script + Send + Sync + 'static,
) -> (Worker<A>, Held) {
    let (entered, seen) = oneshot::channel();
    let (release, released) = mpsc::channel();
    let block = Mutex::new(Some(Block {
        at,
        entered,
        release: released,
    }));
    let submitted = Arc::new(AtomicUsize::new(0));
    let cancelled = Arc::new(AtomicBool::new(false));
    let held = Held {
        entered: seen,
        release,
        submitted: submitted.clone(),
        cancelled: cancelled.clone(),
    };
    let worker = Worker::new(
        move |job| {
            let Some(admission) = authorize().filter(|admission| admission.readable()) else {
                return Ok(None);
            };
            if !admission.matches_session(job.session) {
                return Ok(None);
            }
            let port = BlockingPort {
                script: script(),
                block: block.lock().unwrap().take(),
                submitted: submitted.clone(),
                cancelled: cancelled.clone(),
            };
            let mut reader = Reader::new(port, admission);
            tokio::runtime::Builder::new_current_thread()
                .enable_time()
                .build()?
                .block_on(async {
                    let performance = reader.observe(&mut || authorize(), job.limit()).await;
                    reader.retire(None).await?;
                    Ok(performance.map(|performance| Reading::thermal(admission, performance)))
                })
        },
        fence,
    );
    (worker, held)
}

const TEST_LIMIT: Duration = Duration::from_secs(2);

pub(crate) fn in_memory_worker(run: impl Fn() -> io::Result<()> + Send + Sync + 'static) -> Worker {
    Worker::new(move |_| run().map(|()| None), None)
}

#[tokio::test]
async fn firmware_worker_drop_revokes_before_or_during_submission() {
    for (at, submitted) in [(BlockAt::Verify, 0), (BlockAt::Submit, 1)] {
        let (worker, held) = blocking_worker(at, None);
        let deadline = Instant::now() + TEST_LIMIT;
        {
            let read = worker.observe(deadline, None);
            tokio::pin!(read);
            tokio::select! {
                reached = timeout(TEST_LIMIT, held.entered) => reached.unwrap().unwrap(),
                _ = &mut read => panic!("read returned before reaching the fake Win32 call"),
            }
            assert!(worker.observe(deadline, None).await.unwrap().is_none());
        }
        assert!(
            timeout(Duration::from_millis(20), worker.stop())
                .await
                .is_err()
        );
        held.release.send(()).unwrap();
        timeout(TEST_LIMIT, worker.stop()).await.unwrap().unwrap();
        assert_eq!(held.submitted.load(Ordering::Acquire), submitted);
        assert!(held.cancelled.load(Ordering::Acquire));
        assert!(
            worker
                .observe(Instant::now() + TEST_LIMIT, None)
                .await
                .unwrap()
                .is_none()
        );
    }
}

#[tokio::test]
async fn firmware_worker_rejects_late_success_and_fences_replacement_during_callback_drain() {
    static FENCE: AtomicBool = AtomicBool::new(false);
    let (worker, held) = blocking_worker(BlockAt::Retire, Some(&FENCE));
    let (replacement, next) = blocking_worker(BlockAt::Verify, Some(&FENCE));
    // The read's deadline leaves the job time to reach the callback drain on
    // any host. Paused time then expires it with no real wait, so the success
    // is late however long the host took to reach the drain.
    let budget = Duration::from_secs(60);
    let deadline = Instant::now() + budget;
    let read = worker.observe_with_budget(deadline, None, budget);
    tokio::pin!(read);
    tokio::select! {
        reached = timeout(budget, held.entered) => reached.unwrap().unwrap(),
        _ = &mut read => panic!("read returned before callback drain"),
    }
    assert_eq!(held.submitted.load(Ordering::Acquire), 4);
    tokio::time::pause();
    tokio::time::advance(deadline.saturating_duration_since(Instant::now())).await;
    assert!(read.await.unwrap().is_none());
    // The drain and the replacement run on OS threads, so the rest of the test
    // waits on the real clock.
    tokio::time::resume();
    assert!(
        worker
            .observe(Instant::now() + TEST_LIMIT, None)
            .await
            .unwrap()
            .is_none()
    );
    assert!(
        replacement
            .observe(Instant::now() + TEST_LIMIT, None)
            .await
            .unwrap()
            .is_none()
    );
    assert_eq!(next.submitted.load(Ordering::Acquire), 0);
    assert!(
        timeout(Duration::from_millis(20), worker.stop())
            .await
            .is_err()
    );
    held.release.send(()).unwrap();
    timeout(TEST_LIMIT, worker.stop()).await.unwrap().unwrap();
    assert!(!FENCE.load(Ordering::Acquire));

    let read = replacement.observe(Instant::now() + TEST_LIMIT, Some(ADMITTED.session));
    let unblock = async {
        timeout(TEST_LIMIT, next.entered).await.unwrap().unwrap();
        next.release.send(()).unwrap();
    };
    let (reading, ()) = tokio::join!(read, unblock);
    let reading = reading.unwrap().unwrap();
    assert_eq!(reading.admission, ADMITTED);
    assert_eq!(
        reading.performance,
        ObservedPerformance::PerformanceAutomatic
    );
    replacement.stop().await.unwrap();
}

#[tokio::test]
async fn firmware_worker_refuses_wrong_session_and_expired_requests() {
    let (worker, held) = blocking_worker(BlockAt::Verify, None);
    assert!(
        worker
            .observe(Instant::now(), None)
            .await
            .unwrap()
            .is_none()
    );
    assert!(
        worker
            .observe(Instant::now() + TEST_LIMIT, Some(ADMITTED.session + 1))
            .await
            .unwrap()
            .is_none()
    );
    assert_eq!(held.submitted.load(Ordering::Acquire), 0);
    worker.stop().await.unwrap();
}

#[tokio::test]
async fn firmware_worker_quarantines_unconfirmed_retirement() {
    static FENCE: AtomicBool = AtomicBool::new(false);
    let worker: Worker = Worker::new(
        |_| Err(io::Error::other("unconfirmed callback drain")),
        Some(&FENCE),
    );
    assert!(
        worker
            .observe(Instant::now() + TEST_LIMIT, None)
            .await
            .is_err()
    );
    assert!(worker.stop().await.is_err());
    drop(worker);
    let replacement: Worker = Worker::new(|_| panic!("no replacement permitted"), Some(&FENCE));
    assert!(
        replacement
            .observe(Instant::now() + TEST_LIMIT, None)
            .await
            .unwrap()
            .is_none()
    );
}

pub(crate) fn failing_worker<A: ObservationProof>() -> Worker<A> {
    Worker::new(
        |_| Err(io::Error::other("injected native retirement failure")),
        None,
    )
}

// The installed path owns a private Reader for each claimed whole observation.
// This fake replaces only feature I/O; Worker, Reader and retirement are real.
pub(crate) struct CustomIo {
    transactions: Transactions,
    pub(crate) getters: AtomicUsize,
    pub(crate) retirements: AtomicUsize,
    pub(crate) fan: AtomicU8,
    pub(crate) cpu: AtomicU8,
    pub(crate) gpu: AtomicU8,
}
impl CustomIo {
    pub(crate) fn new() -> Arc<Self> {
        Arc::new(Self {
            transactions: Transactions::new(),
            getters: AtomicUsize::new(0),
            retirements: AtomicUsize::new(0),
            fan: AtomicU8::new(0),
            cpu: AtomicU8::new(1),
            gpu: AtomicU8::new(2),
        })
    }
    /// Quarantines every free ID, as that many unanswered exchanges would.
    pub(crate) fn quarantine_free_ids(&self) {
        while let Some(pair) = self.transactions.reserve_pair() {
            for id in pair {
                assert!(self.transactions.sent(id));
            }
        }
    }
}
struct CustomPort {
    io: Arc<CustomIo>,
    reply: Frame,
    pending: Option<(usize, Frame)>,
}
impl Port for CustomPort {
    fn transactions(&self) -> &Transactions {
        &self.io.transactions
    }
    fn verify(&mut self, limit: Limit<'_>) -> io::Result<()> {
        limit.check()
    }
    fn submit(&mut self, transfer: Transfer, limit: Limit<'_>) -> io::Result<()> {
        limit.check()?;
        assert!(self.pending.is_none());
        self.pending = Some(match transfer {
            Transfer::SendGetter(frame) => {
                self.io.getters.fetch_add(1, Ordering::AcqRel);
                let transaction = frame[2];
                let channel = frame[10];
                self.reply = if frame[8] == 0x82 {
                    super::tests::reply(
                        transaction,
                        &[1, channel, 4, self.io.fan.load(Ordering::Acquire)],
                    )
                } else {
                    assert_eq!(frame[8], 0x87);
                    let level = if channel == 1 {
                        &self.io.cpu
                    } else {
                        &self.io.gpu
                    };
                    super::tests::custom_reply(
                        transaction,
                        &[1, channel, level.load(Ordering::Acquire)],
                    )
                };
                (0, frame)
            }
            #[cfg(feature = "service-thermal-mutation-v1")]
            Transfer::SendMutation(_) => panic!("read-only fixture received a mutation"),
            Transfer::ReceiveReply => (91, self.reply),
        });
        Ok(())
    }
    fn poll(&mut self) -> Option<io::Result<(usize, Frame)>> {
        self.pending.take().map(Ok)
    }
    fn cancel(&mut self) {}
    fn poll_retirement(&mut self) -> io::Result<bool> {
        self.pending.take();
        self.io.retirements.fetch_add(1, Ordering::AcqRel);
        Ok(true)
    }
}
pub(crate) fn custom_io_worker(
    io: Arc<CustomIo>,
    authorize: impl Fn() -> Option<ReadAdmission> + Send + Sync + 'static,
) -> Worker<ReadAdmission> {
    Worker::new(
        move |job| {
            let Some(admission) = authorize() else {
                return Ok(None);
            };
            let mut reader = Reader::new(
                CustomPort {
                    io: io.clone(),
                    reply: [0; FRAME_LENGTH],
                    pending: None,
                },
                admission,
            );
            tokio::runtime::Builder::new_current_thread()
                .enable_time()
                .start_paused(true)
                .build()?
                .block_on(async {
                    let performance = reader.observe(&mut || authorize(), job.limit()).await;
                    reader.retire(None).await?;
                    Ok(performance.map(|performance| Reading::thermal(admission, performance)))
                })
        },
        None,
    )
}

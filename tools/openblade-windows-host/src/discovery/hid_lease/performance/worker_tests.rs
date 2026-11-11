use super::{
    tests::{ADMITTED, Script},
    worker::{Reading, Worker},
    *,
};
use std::sync::{
    Arc, Mutex,
    atomic::{AtomicUsize, Ordering},
    mpsc,
};
use tokio::{sync::oneshot, time::timeout};

#[derive(Clone, Copy, PartialEq, Eq)]
pub(crate) enum BlockAt {
    Verify,
    Submit,
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
    fn reserve_pair(&mut self) -> Option<[u8; 2]> {
        self.script.reserve_pair()
    }

    fn verify(&mut self, limit: Limit<'_>) -> io::Result<()> {
        self.block(BlockAt::Verify);
        self.script.verify(limit)
    }

    fn submit(&mut self, transfer: Transfer, limit: Limit<'_>) -> io::Result<()> {
        limit.check()?;
        self.script.submit(transfer, limit)?;
        self.submitted.fetch_add(1, Ordering::AcqRel);
        self.block(BlockAt::Submit);
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
                script: Script::pair([2, 2]),
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
                    Ok(performance.map(|performance| Reading {
                        admission,
                        performance,
                    }))
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
    let deadline = Instant::now() + Duration::from_millis(500);
    let read = worker.observe(deadline, None);
    tokio::pin!(read);
    tokio::select! {
        reached = timeout(TEST_LIMIT, held.entered) => reached.unwrap().unwrap(),
        _ = &mut read => panic!("read returned before callback drain"),
    }
    assert_eq!(held.submitted.load(Ordering::Acquire), 4);
    assert!(read.await.unwrap().is_none());
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

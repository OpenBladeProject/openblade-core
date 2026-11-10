use super::*;

use std::{
    collections::VecDeque,
    sync::atomic::{AtomicBool, Ordering},
};
use tokio::{sync::oneshot::error::TryRecvError, task::yield_now, time::advance};

struct ScriptedProcess {
    closed: Arc<AtomicBool>,
    waits: Mutex<VecDeque<Result<bool, Fault>>>,
}

impl ProcessOwner for ScriptedProcess {
    fn close_job(&mut self) {
        self.closed.store(true, Ordering::SeqCst);
    }

    fn exited(&self) -> Result<bool, Fault> {
        self.waits.lock().unwrap().pop_front().unwrap_or(Ok(false))
    }
}

#[tokio::test(start_paused = true)]
async fn pre_creation_failures_keep_native_stage_and_code() {
    for (stage, code) in [
        (Stage::ConfigureJob, 6),
        (Stage::JobList, 87),
        (Stage::CreateProcessWithJob, 5),
    ] {
        let (_stop_tx, stop) = watch::channel(None);
        let (exit, seen) = watch::channel(Exit::Pending);
        let (deliver, stream) = oneshot::channel::<Result<(), Fault>>();
        let log = FaultLog::default();
        let fault = Fault::new(stage, io::Error::from_raw_os_error(code));
        run_generation(
            async move { Launch::<ScriptedProcess, ()>::NoChild(fault) },
            stop,
            exit,
            deliver,
            log.clone(),
        )
        .await;
        assert_eq!(stream.await.unwrap(), Err(fault));
        assert_eq!(*seen.borrow(), Exit::Confirmed);
        assert_eq!(log.lock().unwrap().transport, Some(fault));
    }
}

#[tokio::test(start_paused = true)]
async fn stop_during_launch_never_delivers_the_late_stream() {
    let (request_stop, stop) = watch::channel(None);
    let (exit, seen) = watch::channel(Exit::Pending);
    let (deliver, mut stream) = oneshot::channel();
    let (complete_launch, pending_launch) = oneshot::channel();
    let closed = Arc::new(AtomicBool::new(false));
    let task = tokio::spawn(run_generation(
        async move { pending_launch.await.unwrap() },
        stop,
        exit,
        deliver,
        FaultLog::default(),
    ));
    yield_now().await;
    assert_eq!(stream.try_recv(), Err(TryRecvError::Empty));
    request_stop.send_replace(Some(Instant::now()));
    assert_eq!(*seen.borrow(), Exit::Pending);
    assert!(
        complete_launch
            .send(Launch::Started(
                ScriptedProcess {
                    closed: closed.clone(),
                    waits: Mutex::new(VecDeque::from([Ok(false), Ok(true)])),
                },
                Ok(())
            ))
            .is_ok()
    );
    yield_now().await;
    assert!(closed.load(Ordering::SeqCst));
    assert_eq!(*seen.borrow(), Exit::Pending);
    advance(EXIT_POLL_INTERVAL).await;
    task.await.unwrap();
    assert_eq!(*seen.borrow(), Exit::Confirmed);
    assert_eq!(stream.try_recv(), Err(TryRecvError::Closed));
}

#[tokio::test(start_paused = true)]
async fn pipe_registration_failure_waits_for_exact_exit_after_a_failed_wait() {
    let (_stop_tx, stop) = watch::channel(None);
    let (exit, seen) = watch::channel(Exit::Pending);
    let (deliver, stream) = oneshot::channel::<Result<(), Fault>>();
    let log = FaultLog::default();
    let closed = Arc::new(AtomicBool::new(false));
    let registration = Fault::new(Stage::RegisterPipe, io::Error::from_raw_os_error(87));
    let wait = Fault::new(Stage::ObserveExit, io::Error::from_raw_os_error(6));
    let observed_close = closed.clone();
    let task = tokio::spawn(run_generation(
        async move {
            Launch::Started(
                ScriptedProcess {
                    closed: observed_close,
                    waits: Mutex::new(VecDeque::from([Err(wait), Ok(false), Ok(true)])),
                },
                Err(registration),
            )
        },
        stop,
        exit,
        deliver,
        log.clone(),
    ));
    assert_eq!(stream.await.unwrap(), Err(registration));
    assert!(closed.load(Ordering::SeqCst));
    assert_eq!(*seen.borrow(), Exit::Unobservable);
    assert_eq!(
        *log.lock().unwrap(),
        NativeDiagnostics {
            transport: Some(registration),
            observation: Some(wait),
        }
    );
    advance(EXIT_POLL_INTERVAL).await;
    yield_now().await;
    assert_eq!(*seen.borrow(), Exit::Unobservable);
    advance(EXIT_POLL_INTERVAL).await;
    task.await.unwrap();
    assert_eq!(*seen.borrow(), Exit::Confirmed);
}

#[tokio::test(start_paused = true)]
async fn lost_launch_result_does_not_certify_no_child() {
    let (_stop_tx, stop) = watch::channel(None);
    let (exit, seen) = watch::channel(Exit::Pending);
    let (deliver, stream) = oneshot::channel::<Result<(), Fault>>();
    let log = FaultLog::default();
    let fault = Fault::new(Stage::LaunchTask, io::ErrorKind::Other.into());
    run_generation(
        async move { Launch::<ScriptedProcess, ()>::Uncertain(fault) },
        stop,
        exit,
        deliver,
        log.clone(),
    )
    .await;
    assert_eq!(stream.await.unwrap(), Err(fault));
    assert_eq!(*seen.borrow(), Exit::Unobservable);
    assert_eq!(log.lock().unwrap().transport, Some(fault));
}

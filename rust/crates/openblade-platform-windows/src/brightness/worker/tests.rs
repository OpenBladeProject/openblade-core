use super::*;

struct HeldNative {
    entered: SyncSender<()>,
    release: Receiver<()>,
    dropping: SyncSender<()>,
    finish_drop: Receiver<()>,
}

impl WmiBrightnessNative for HeldNative {
    type Method = ();

    fn topology(&mut self, _: &CallBudget<'_>) -> Result<InternalDisplayTopology, TopologyFailure> {
        self.entered.send(()).unwrap();
        self.release.recv_timeout(Duration::from_secs(5)).unwrap();
        Ok(InternalDisplayTopology::NoInternalDisplay)
    }

    fn active_readings(
        &mut self,
        _: &CallBudget<'_>,
        _: usize,
    ) -> Result<Vec<ReadingRow>, NativeFailure> {
        unreachable!()
    }

    fn active_methods(
        &mut self,
        _: &CallBudget<'_>,
        _: usize,
    ) -> Result<Vec<MethodRow<()>>, NativeFailure> {
        unreachable!()
    }

    fn set_brightness(
        &mut self,
        _: (),
        _: u8,
        _: &mut Submission<'_>,
    ) -> Result<Acknowledgement, NativeFailure> {
        unreachable!()
    }
}

impl Drop for HeldNative {
    fn drop(&mut self) {
        self.dropping.send(()).unwrap();
        self.finish_drop
            .recv_timeout(Duration::from_secs(5))
            .unwrap();
    }
}

#[test]
fn cancelled_request_stays_busy_and_shutdown_joins_through_native_drop() {
    for explicit_shutdown in [true, false] {
        let (entered, observing) = mpsc::sync_channel(1);
        let (release, wait_for_release) = mpsc::sync_channel(1);
        let (dropping, observing_drop) = mpsc::sync_channel(1);
        let (finish_drop, wait_for_drop) = mpsc::sync_channel(1);
        let mut worker = WindowsBrightnessWorker::spawn(move || HeldNative {
            entered,
            release: wait_for_release,
            dropping,
            finish_drop: wait_for_drop,
        })
        .unwrap();
        let target = BrightnessQueryTarget::from_display(
            &ActiveInternalDisplay::brightness_fixture(r"DISPLAY#Panel#ID#{guid}", 2),
        )
        .unwrap();
        let first = std::cell::Cell::new(true);
        assert_eq!(
            worker.read_query_target(target, || {
                if first.replace(false) {
                    return false;
                }
                observing.recv_timeout(Duration::from_secs(5)).unwrap();
                true
            }),
            Err(BrightnessError::Native(NativeFailure::Cancelled))
        );
        assert_eq!(
            worker.read(|| false),
            Err(BrightnessError::Native(NativeFailure::WorkerBusy))
        );
        let finished_request = Arc::clone(worker.pending.as_ref().unwrap());
        let (joined, observing_join) = mpsc::sync_channel(1);
        let owner = std::thread::spawn(move || {
            if explicit_shutdown {
                worker.shutdown().unwrap();
            } else {
                drop(worker);
            }
            joined.send(()).unwrap();
        });
        release.send(()).unwrap();
        observing_drop.recv_timeout(Duration::from_secs(5)).unwrap();
        assert!(finished_request.load(SeqCst));
        assert_eq!(observing_join.try_recv(), Err(mpsc::TryRecvError::Empty));
        finish_drop.send(()).unwrap();
        observing_join.recv_timeout(Duration::from_secs(5)).unwrap();
        owner.join().unwrap();
    }
}

#[test]
fn links_native_worker_without_starting_it() {
    std::hint::black_box(
        WindowsBrightnessWorker::start as fn() -> io::Result<WindowsBrightnessWorker>,
    );
}

#[test]
fn queued_definitive_reply_wins_at_deadline() {
    for started in [false, true] {
        let (sender, receiver) = mpsc::sync_channel(1);
        sender.send(7).unwrap();
        let gate = RequestGate::default();
        if started {
            gate.begin().unwrap();
        }
        let expired = Instant::now() - Duration::from_secs(1);
        assert_eq!(
            wait_for_reply(receiver, &gate, expired, expired, &|| false),
            Ok(7)
        );
    }
}

#[test]
fn cancellation_wins_before_begin_and_cannot_clear_started_state() {
    let gate = RequestGate::default();
    assert!(!gate.cancel());
    assert_eq!(gate.begin(), Err(NativeFailure::Cancelled));

    let gate = RequestGate::default();
    assert_eq!(gate.begin(), Ok(()));
    assert!(gate.cancel());
    assert_eq!(gate.begin(), Err(NativeFailure::SubmissionAlreadyStarted));
}

#[test]
fn timeout_after_start_blocks_writes_while_worker_is_live_and_after_it_drains() {
    let (requests, receiver) = mpsc::sync_channel::<Request>(1);
    let (started, observing_start) = mpsc::sync_channel(0);
    let (release, blocked) = mpsc::sync_channel(0);
    let thread = std::thread::spawn(move || {
        let request = receiver.recv().unwrap();
        request.gate.begin().unwrap();
        started.send(()).unwrap();
        blocked.recv().unwrap();
        request.finished.store(true, SeqCst);
    });
    let mut worker = WindowsBrightnessWorker {
        requests: Some(requests),
        thread: Some(thread),
        pending: None,
        submission_unresolved: false,
    };
    // Wait for the start marker on the first cancellation poll. This makes the
    // ordering independent of when the test worker is scheduled.
    let poll = std::cell::Cell::new(0_u8);
    let cancel = || {
        match poll.get() {
            0 => poll.set(1),
            1 => {
                observing_start
                    .recv_timeout(Duration::from_secs(5))
                    .unwrap();
                poll.set(2);
            }
            _ => {}
        }
        false
    };
    let result = worker.execute(Duration::from_millis(75), &cancel, None, |reply| {
        Operation::Apply(BrightnessRequest::SetPercent(50), None, reply)
    });
    assert_eq!(result, Err((NativeFailure::DeadlineExceeded, true)));
    assert_eq!(
        worker.read(|| false),
        Err(BrightnessError::Native(NativeFailure::WorkerBusy))
    );
    release.send(()).unwrap();
    worker.join().unwrap();
    assert_eq!(
        worker
            .apply(BrightnessRequest::StepUp, || false)
            .unwrap_err()
            .reason,
        BrightnessError::SubmissionUnresolved
    );
}

#[test]
fn disconnected_reply_retains_possible_submission() {
    let (requests, receiver) = mpsc::sync_channel::<Request>(1);
    let thread = std::thread::spawn(move || {
        let request = receiver.recv().unwrap();
        request.gate.begin().unwrap();
    });
    let mut worker = WindowsBrightnessWorker {
        requests: Some(requests),
        thread: Some(thread),
        pending: None,
        submission_unresolved: false,
    };
    let result = worker.execute(CALL_LIMIT, &|| false, None, |reply| {
        Operation::Apply(BrightnessRequest::StepDown, None, reply)
    });
    worker.join().unwrap();
    assert_eq!(result, Err((NativeFailure::WorkerDisconnected, true)));
    assert!(worker.submission_unresolved);
}

#[test]
fn pre_submit_cancellation_keeps_stalled_worker_busy_without_a_write_pause() {
    let (requests, receiver) = mpsc::sync_channel::<Request>(1);
    let (ready, observing_ready) = mpsc::sync_channel(0);
    let (release, blocked) = mpsc::sync_channel(0);
    let thread = std::thread::spawn(move || {
        let request = receiver.recv().unwrap();
        ready.send(()).unwrap();
        blocked.recv().unwrap();
        assert_eq!(request.gate.begin(), Err(NativeFailure::Cancelled));
        request.finished.store(true, SeqCst);
    });
    let mut worker = WindowsBrightnessWorker {
        requests: Some(requests),
        thread: Some(thread),
        pending: None,
        submission_unresolved: false,
    };
    let first_poll = std::cell::Cell::new(true);
    let result = worker.apply(BrightnessRequest::StepUp, || {
        if first_poll.replace(false) {
            return false;
        }
        observing_ready
            .recv_timeout(Duration::from_secs(5))
            .unwrap();
        true
    });
    assert_eq!(
        result.unwrap_err(),
        BrightnessFailure {
            reason: BrightnessError::Native(NativeFailure::Cancelled),
            submission_may_have_occurred: false,
            state: None,
        }
    );
    assert_eq!(
        worker.read(|| false),
        Err(BrightnessError::Native(NativeFailure::WorkerBusy))
    );
    assert!(!worker.submission_unresolved);
    release.send(()).unwrap();
    worker.shutdown().unwrap();
}

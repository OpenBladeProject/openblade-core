use super::protocol::MilliCelsius;
use super::*;

use tokio::{
    io::{DuplexStream, duplex},
    time::{advance, sleep},
};

fn child() -> (
    PrivateChild<DuplexStream>,
    DuplexStream,
    watch::Receiver<Option<Instant>>,
    watch::Sender<Exit>,
) {
    let (io, peer) = duplex(64);
    let (stop, stopped) = watch::channel(None);
    let (exited, exit) = watch::channel(Exit::Pending);
    (PrivateChild { io, stop, exit }, peer, stopped, exited)
}

fn no_attach() -> io::Result<PrivateChild<DuplexStream>> {
    panic!("existing child must still own the worker slot");
}

async fn request(peer: &mut DuplexStream, expected: i64) {
    let mut frame = [0; 8];
    peer.read_exact(&mut frame).await.unwrap();
    assert_eq!(i64::from_le_bytes(frame), expected);
}

fn reply(sequence: i64, status: i32, milli: i32) -> [u8; 20] {
    let mut frame = [0; 20];
    frame[..4].copy_from_slice(b"CPT1");
    frame[4..12].copy_from_slice(&sequence.to_le_bytes());
    frame[12..16].copy_from_slice(&status.to_le_bytes());
    frame[16..].copy_from_slice(&milli.to_le_bytes());
    frame
}

async fn respond(peer: &mut DuplexStream, sequence: i64, status: i32, milli: i32) {
    request(peer, sequence).await;
    peer.write_all(&reply(sequence, status, milli))
        .await
        .unwrap();
}

#[test]
fn managed_geometry_status_and_rounding() {
    let seq = Sequence::new(0x0102_0304_0506_0708).unwrap();
    assert_eq!(seq.encode(), [8, 7, 6, 5, 4, 3, 2, 1]);
    assert_eq!(Sequence::decode(&seq.encode()), Some(seq));
    assert_eq!(Sequence::decode(&[0; 7]), None);
    assert_eq!(Sequence::decode(&[0; 8]), None);
    assert_eq!(Sequence::decode(&[255; 8]), None);

    let seq = Sequence::new(2).unwrap();
    for (status, milli, expected) in [
        (0, 0, Some(Reply::Unavailable)),
        (2, 0, Some(Reply::Ready)),
        (3, 0, Some(Reply::TerminalUnavailable)),
        (
            1,
            10_000,
            Some(Reply::Value(MilliCelsius::new(10_000).unwrap())),
        ),
        (
            1,
            50_001,
            Some(Reply::Value(MilliCelsius::new(50_001).unwrap())),
        ),
        (
            1,
            125_000,
            Some(Reply::Value(MilliCelsius::new(125_000).unwrap())),
        ),
        (0, 1, None),
        (2, -1, None),
        (3, 1, None),
        (4, 0, None),
        (1, 9_999, None),
        (1, 125_001, None),
    ] {
        let frame = reply(2, status, milli);
        assert_eq!(decode_reply(&frame, seq), expected);
        if let Some(expected) = expected {
            assert_eq!(expected.encode(seq), frame);
        }
    }
    assert_eq!(decode_reply(&reply(3, 1, 50_000), seq), None);
    assert_eq!(decode_reply(&reply(2, 1, 50_000)[..19], seq), None);
    let mut bad_magic = reply(2, 1, 50_000);
    bad_magic[0] = 0;
    assert_eq!(decode_reply(&bad_magic, seq), None);
}

#[tokio::test(start_paused = true)]
async fn ready_is_separate_and_freshness_includes_time_in_flight() {
    let (child, mut peer, stopped, _exited) = child();
    let mut owner = Supervisor::default();
    let script = tokio::spawn(async move {
        respond(&mut peer, 1, 2, 0).await;
        request(&mut peer, 2).await;
        sleep(Duration::from_millis(400)).await;
        let frame = reply(2, 1, 50_001);
        peer.write_all(&frame[..7]).await.unwrap();
        tokio::task::yield_now().await;
        peer.write_all(&frame[7..]).await.unwrap();
        respond(&mut peer, 3, 0, 0).await;
    });
    assert!(owner.prepare(|| Ok(child)).await);
    assert_eq!(owner.diagnostics().last_failure, None);
    let start = Instant::now();
    let sample = owner.sample(no_attach).await.unwrap();
    let now = Instant::now();
    assert_eq!(
        sample.celsius_for(start, now, Duration::from_millis(400)),
        Some(51)
    );
    assert_eq!(
        sample.celsius_for(start, now, Duration::from_millis(399)),
        None
    );
    assert_eq!(
        sample.celsius_for(start + Duration::from_millis(1), now, READ_TIMEOUT),
        None
    );
    assert_eq!(sample.celsius_for(start, start, READ_TIMEOUT), None);
    assert!(owner.sample(no_attach).await.is_none());
    assert_eq!(owner.diagnostics().last_failure, Some(Failure::Unavailable));
    assert!(stopped.borrow().is_none());
    script.await.unwrap();
}

#[tokio::test(start_paused = true)]
async fn sample_faults_stop_the_child_and_report_only_observed_outcomes() {
    for (response, count, expected) in [
        (reply(2, 2, 0), 20, Failure::MalformedReply),
        (reply(2, 3, 0), 20, Failure::TerminalUnavailable),
        (reply(1, 1, 50_000), 20, Failure::MalformedReply),
        (reply(2, 1, 50_000), 19, Failure::Unavailable),
        (reply(2, 1, 50_000), 0, Failure::Unavailable),
    ] {
        let (child, mut peer, stopped, _exited) = child();
        let mut owner = Supervisor::default();
        let script = tokio::spawn(async move {
            respond(&mut peer, 1, 2, 0).await;
            request(&mut peer, 2).await;
            peer.write_all(&response[..count]).await.unwrap();
        });
        assert!(owner.prepare(|| Ok(child)).await);
        assert!(owner.sample(no_attach).await.is_none());
        assert_eq!(owner.diagnostics().last_failure, Some(expected));
        assert!(stopped.borrow().is_some());
        script.await.unwrap();
    }
}

#[tokio::test(start_paused = true)]
async fn timeout_discards_late_sample_and_waits_for_confirmed_exit() {
    let (first, mut peer, stopped, exited) = child();
    let mut owner = Supervisor::default();
    let script = tokio::spawn(async move {
        respond(&mut peer, 1, 2, 0).await;
        request(&mut peer, 2).await;
        sleep(READ_TIMEOUT + Duration::from_millis(1)).await;
        assert!(peer.write_all(&reply(2, 1, 51_000)).await.is_err());
    });
    assert!(owner.prepare(|| Ok(first)).await);
    assert!(owner.sample(no_attach).await.is_none());
    assert_eq!(owner.diagnostics().last_failure, Some(Failure::Timeout));
    assert!(stopped.borrow().is_some());
    assert!(!owner.diagnostics().exit_unconfirmed);
    advance(STOP_TIMEOUT).await;
    script.await.unwrap();
    assert!(owner.diagnostics().exit_unconfirmed);
    assert!(owner.sample(no_attach).await.is_none());
    exited.send_replace(Exit::Confirmed);
    assert!(!owner.diagnostics().exit_unconfirmed);

    let (second, mut peer, _, _exited) = child();
    let script = tokio::spawn(async move {
        respond(&mut peer, 3, 2, 0).await;
        respond(&mut peer, 4, 1, 45_000).await;
    });
    let start = Instant::now();
    let sample = owner.sample(|| Ok(second)).await.unwrap();
    assert_eq!(
        sample.celsius_for(start, Instant::now(), READ_TIMEOUT),
        Some(45)
    );
    assert_eq!(owner.diagnostics().last_failure, None);
    script.await.unwrap();
}

#[tokio::test(start_paused = true)]
async fn cancellation_of_active_sample_retires_stream_even_if_exit_observation_is_lost() {
    let (child, mut peer, stopped, exited) = child();
    let mut owner = Supervisor::default();
    let (prepared, ()) = tokio::join!(owner.prepare(|| Ok(child)), respond(&mut peer, 1, 2, 0),);
    assert!(prepared);
    {
        let mut active = Box::pin(owner.sample(no_attach));
        tokio::select! {
            _ = &mut active => panic!("no response was sent"),
            () = request(&mut peer, 2) => {}
        }
        // Cancel after the peer received the request.
    }
    assert!(stopped.borrow().is_some());
    assert_eq!(owner.diagnostics().last_failure, None);
    drop(exited);
    advance(Duration::from_secs(40)).await;
    assert!(owner.diagnostics().exit_unconfirmed);
    assert!(!owner.prepare(no_attach).await);
}

#[tokio::test(start_paused = true)]
async fn canceled_or_timed_out_warmup_wait_does_not_stop_initialization() {
    let (child, mut peer, stopped, _exited) = child();
    let mut owner = Supervisor::default();
    {
        let mut warmup = Box::pin(owner.sample(|| Ok(child)));
        tokio::select! {
            _ = &mut warmup => panic!("readiness is still withheld"),
            () = request(&mut peer, 1) => {}
        }
    }
    assert!(stopped.borrow().is_none());
    assert!(owner.sample(no_attach).await.is_none());
    assert_eq!(owner.diagnostics().last_failure, Some(Failure::Timeout));
    assert!(stopped.borrow().is_none());
    peer.write_all(&reply(1, 2, 0)).await.unwrap();
    assert!(owner.prepare(no_attach).await);
    let start = Instant::now();
    let (sample, ()) = tokio::join!(owner.sample(no_attach), respond(&mut peer, 2, 1, 47_000));
    assert_eq!(
        sample
            .unwrap()
            .celsius_for(start, Instant::now(), READ_TIMEOUT),
        Some(47)
    );
}

#[tokio::test(start_paused = true)]
async fn late_readiness_does_not_shorten_the_first_sample_budget() {
    let (child, mut peer, stopped, _exited) = child();
    let mut owner = Supervisor::default();
    let script = tokio::spawn(async move {
        request(&mut peer, 1).await;
        sleep(Duration::from_millis(600)).await;
        peer.write_all(&reply(1, 2, 0)).await.unwrap();
        peer
    });
    assert!(owner.sample(|| Ok(child)).await.is_none());
    assert_eq!(owner.diagnostics().last_failure, Some(Failure::Unavailable));
    assert!(stopped.borrow().is_none());
    let mut peer = script.await.unwrap();
    let (sample, ()) = tokio::join!(owner.sample(no_attach), async {
        request(&mut peer, 2).await;
        sleep(Duration::from_millis(600)).await;
        peer.write_all(&reply(2, 1, 49_000)).await.unwrap();
    });
    assert!(sample.is_some());
}

#[tokio::test(start_paused = true)]
async fn initialization_has_its_own_deadline_and_accepts_only_ready() {
    for status in [None, Some(1), Some(3)] {
        let (child, mut peer, mut stopped, _exited) = child();
        let mut owner = Supervisor::default();
        let (ready, ()) = tokio::join!(owner.prepare(|| Ok(child)), async {
            request(&mut peer, 1).await;
            if let Some(status) = status {
                let milli = if status == 1 { 50_000 } else { 0 };
                peer.write_all(&reply(1, status, milli)).await.unwrap();
            }
            stopped.wait_for(Option::is_some).await.unwrap();
        });
        assert!(!ready);
        assert!(stopped.borrow().is_some());
        assert!(owner.sample(no_attach).await.is_none());
    }
}

#[tokio::test(start_paused = true)]
async fn cooldown_never_returns_cached_values_and_success_resets_it() {
    let (child, mut peer, stopped, _exited) = child();
    let mut owner = Supervisor::default();
    let script = tokio::spawn(async move {
        respond(&mut peer, 1, 2, 0).await;
        respond(&mut peer, 2, 0, 0).await;
        respond(&mut peer, 3, 0, 0).await;
        respond(&mut peer, 4, 0, 0).await;
        respond(&mut peer, 5, 1, 52_000).await;
        respond(&mut peer, 6, 0, 0).await;
        respond(&mut peer, 7, 1, 53_000).await;
    });
    assert!(owner.prepare(|| Ok(child)).await);
    assert!(owner.sample(no_attach).await.is_none());
    assert!(owner.sample(no_attach).await.is_none());
    assert!(owner.sample(no_attach).await.is_none()); // No request during cooldown.
    advance(Duration::from_secs(1)).await;
    assert!(owner.sample(no_attach).await.is_none());
    advance(Duration::from_millis(1_999)).await;
    assert!(owner.sample(no_attach).await.is_none());
    advance(Duration::from_millis(1)).await;
    assert!(owner.sample(no_attach).await.is_some());
    assert_eq!(owner.diagnostics().last_failure, None);
    assert!(owner.sample(no_attach).await.is_none());
    assert!(owner.sample(no_attach).await.is_some()); // First failure after success has no cooldown.
    assert!(stopped.borrow().is_none());
    script.await.unwrap();
}

#[tokio::test(start_paused = true)]
async fn idle_exit_is_checked_before_writing_and_drop_requests_stop() {
    let (first, mut peer, _stopped, exited) = child();
    let mut owner = Supervisor::default();
    let (ready, ()) = tokio::join!(owner.prepare(|| Ok(first)), respond(&mut peer, 1, 2, 0));
    assert!(ready);
    exited.send_replace(Exit::Confirmed);
    assert!(owner.sample(no_attach).await.is_none());
    assert_eq!(peer.read(&mut [0; 1]).await.unwrap(), 0);
    assert_eq!(owner.diagnostics().last_failure, None);

    let (next, mut peer, stopped, _exited) = child();
    let (ready, ()) = tokio::join!(owner.prepare(|| Ok(next)), respond(&mut peer, 2, 2, 0));
    assert!(ready);
    drop(owner);
    assert!(stopped.borrow().is_some());
}

#[tokio::test(start_paused = true)]
async fn unattended_startup_stop_can_report_a_pending_exit() {
    let (child, mut peer, mut stopped, _exited) = child();
    let mut owner = Supervisor::default();
    let (sample, ()) = tokio::join!(owner.sample(|| Ok(child)), request(&mut peer, 1));
    assert!(sample.is_none());
    assert!(stopped.borrow().is_none());
    stopped.wait_for(Option::is_some).await.unwrap();
    advance(STOP_TIMEOUT).await;
    assert!(owner.diagnostics().exit_unconfirmed);
    assert!(!owner.prepare(no_attach).await);
}

#[tokio::test(start_paused = true)]
async fn attach_failures_back_off_without_spending_wire_sequences() {
    let mut owner = Supervisor::default();
    for _ in 0..2 {
        assert!(
            !owner
                .prepare(|| Err(io::Error::other("scripted attach failure")))
                .await
        );
        assert_eq!(owner.diagnostics().last_failure, Some(Failure::Unavailable));
    }
    assert!(!owner.prepare(no_attach).await);
    advance(Duration::from_secs(1)).await;

    let (child, mut peer, _, _exited) = child();
    let (ready, ()) = tokio::join!(owner.prepare(|| Ok(child)), respond(&mut peer, 1, 2, 0));
    assert!(ready);
}

#[tokio::test(start_paused = true)]
async fn deadline_before_send_preserves_the_ready_worker_and_sequence() {
    let (child, mut peer, stopped, _exited) = child();
    let mut owner = Supervisor::default();
    let (ready, ()) = tokio::join!(owner.prepare(|| Ok(child)), respond(&mut peer, 1, 2, 0));
    assert!(ready);
    assert!(owner.sample_ready(true, Instant::now()).await.is_none());
    assert_eq!(owner.diagnostics().last_failure, Some(Failure::Timeout));
    assert!(stopped.borrow().is_none());
    assert!(!owner.defer_cold_sampling);
    let (value, ()) = tokio::join!(owner.sample(no_attach), respond(&mut peer, 2, 1, 50_000));
    assert!(value.is_some());
}

#[tokio::test(start_paused = true)]
async fn idle_lost_exit_observation_is_visible_without_sampling() {
    let (child, mut peer, stopped, exited) = child();
    let mut owner = Supervisor::default();
    let (ready, ()) = tokio::join!(owner.prepare(|| Ok(child)), respond(&mut peer, 1, 2, 0));
    assert!(ready);
    drop(exited);
    assert!(owner.diagnostics().exit_unconfirmed);
    assert!(stopped.borrow().is_none());
    assert!(owner.sample(no_attach).await.is_none());
    assert_eq!(peer.read(&mut [0; 1]).await.unwrap(), 0);
    assert!(stopped.borrow().is_some());
    assert_eq!(owner.diagnostics().last_failure, Some(Failure::Unavailable));
    assert!(!owner.prepare(no_attach).await);
}

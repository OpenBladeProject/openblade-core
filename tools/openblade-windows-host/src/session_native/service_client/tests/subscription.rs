use super::*;
use std::{cell::RefCell, collections::VecDeque};

mod generations;

const FRAMES: &[u8] = include_bytes!("../../../../tests/fixtures/subscribe-status-v91.frames");
const NEXT_ID: &str = "11111111-2222-4333-8444-555555555556";
const REPLACEMENT_ID: &str = "11111111-2222-4333-8444-555555555563";

fn frames() -> [&'static [u8]; 3] {
    let mut remaining = FRAMES;
    let frames = std::array::from_fn(|_| {
        let length = i32::from_le_bytes(remaining[..4].try_into().unwrap()) as usize;
        let (frame, rest) = remaining.split_at(length + 4);
        remaining = rest;
        frame
    });
    assert!(remaining.is_empty());
    frames
}

async fn accept(server: &mut DuplexStream, id: &str) {
    let mut request = vec![0; frames()[0].len()];
    server.read_exact(&mut request).await.unwrap();
    let expected = std::str::from_utf8(&frames()[0][4..])
        .unwrap()
        .replace(REQUEST_ID, id);
    assert_eq!(&request[..4], &frames()[0][..4]);
    assert_eq!(
        &request[4..],
        expected.as_bytes(),
        "request differs from managed codec"
    );
}

fn edit_frame(frame: &[u8], edit: impl FnOnce(&mut serde_json::Value)) -> Vec<u8> {
    let mut json = serde_json::from_slice(&frame[4..]).unwrap();
    edit(&mut json);
    let payload = serde_json::to_vec(&json).unwrap();
    [(payload.len() as i32).to_le_bytes().as_slice(), &payload].concat()
}

fn revision(status: &StatusObservation) -> i64 {
    status.require_fresh().unwrap();
    serde_json::to_value(&status.snapshot).unwrap()["revision"]
        .as_i64()
        .unwrap()
}

pub(in crate::session_native) fn fixture_initial()
-> io::Result<((DuplexStream, DuplexStream), SubscriptionInitial)> {
    tokio::runtime::Builder::new_current_thread()
        .enable_time()
        .build()?
        .block_on(async {
            let (client, mut server) = duplex(16_384);
            let authority = Authority::live();
            let open = open_on(
                &authority,
                || Ok(REQUEST_ID.into()),
                async || Ok(client),
                &|_| Ok((PID, 0)),
            );
            let send = async {
                accept(&mut server, REQUEST_ID).await;
                server.write_all(frames()[1]).await.unwrap();
            };
            let (result, ()) = tokio::join!(open, send);
            let (client, _, _, initial) = result?;
            Ok(((client, server), initial))
        })
}

#[tokio::test(start_paused = true)]
async fn managed_subscription_replays_initial_and_events_after_idle_then_cancels_partial_event() {
    let (client, mut server) = duplex(16_384);
    let mut client = Some(client);
    let authority = Authority::live();
    let (stop, stopped) = oneshot::channel();
    let (updates, mut received) = tokio::sync::mpsc::unbounded_channel();
    let watch = watch_on(
        Some(&authority),
        async { stopped.await.unwrap() },
        || Ok(REQUEST_ID.into()),
        async || Ok(client.take().expect("healthy stream was reopened")),
        |_| Ok((PID, 0)),
        |update| {
            updates.send(update).unwrap();
            Ok(())
        },
    );
    let drive = async {
        accept(&mut server, REQUEST_ID).await;
        assert!(
            received.try_recv().is_err(),
            "connected before the initial snapshot"
        );
        server.write_all(frames()[1]).await.unwrap();
        let Some(StatusUpdate::Opened(initial)) = received.recv().await else {
            panic!("expected initial snapshot")
        };
        let initial = initial.into_observation();
        let initial_revision = revision(&initial);
        advance(Duration::from_secs(60)).await;
        assert_eq!(
            initial.require_fresh().unwrap_err().kind(),
            io::ErrorKind::TimedOut
        );
        assert!(received.try_recv().is_err());

        server.write_all(frames()[2]).await.unwrap();
        let Some(StatusUpdate::Event(status)) = received.recv().await else {
            panic!("expected a status event")
        };
        assert_eq!(revision(&status), initial_revision + 1);
        server.write_all(&frames()[2][..8]).await.unwrap();
        tokio::task::yield_now().await;
        stop.send(()).unwrap();
        assert_eq!(server.read(&mut [0]).await.unwrap(), 0);
        assert!(received.try_recv().is_err());
    };
    assert_eq!(
        tokio::join!(watch, drive).0.unwrap_err().kind(),
        io::ErrorKind::Interrupted
    );
}

#[tokio::test(start_paused = true)]
async fn subscription_open_deadline_discards_partial_initial_and_cancels_backoff() {
    let (client, mut server) = duplex(16_384);
    let mut client = Some(client);
    let authority = Authority::live();
    let (stop, stopped) = oneshot::channel();
    let (retry, waiting) = oneshot::channel();
    let mut retry = Some(retry);
    let start = Instant::now();
    let watch = watch_on(
        Some(&authority),
        async { stopped.await.unwrap() },
        || Ok(REQUEST_ID.into()),
        async || Ok(client.take().expect("cancellation did not stop backoff")),
        |_| Ok((PID, 0)),
        |update| {
            assert!(matches!(update, StatusUpdate::Reconnecting));
            retry.take().unwrap().send(start.elapsed()).unwrap();
            Ok(())
        },
    );
    let drive = async {
        accept(&mut server, REQUEST_ID).await;
        server.write_all(&frames()[1][..8]).await.unwrap();
        assert_eq!(waiting.await.unwrap(), STATUS_BUDGET);
        assert_eq!(server.read(&mut [0]).await.unwrap(), 0);
        stop.send(()).unwrap();
    };
    let (result, ()) = tokio::join!(watch, drive);
    assert_eq!(result.unwrap_err().kind(), io::ErrorKind::Interrupted);
}

#[tokio::test]
async fn subscription_rejects_uncorrelated_initial_and_malformed_events() {
    let [_, initial, event] = frames();
    for (first, next) in [
        (
            edit_frame(initial, |f| f["requestId"] = NEXT_ID.into()),
            None,
        ),
        (edit_frame(initial, |f| f["version"] = 51.into()), None),
        (
            edit_frame(initial, |f| f["status"] = serde_json::Value::Null),
            None,
        ),
        (event.to_vec(), None),
        (initial.to_vec(), Some(initial.to_vec())),
        (
            initial.to_vec(),
            Some(edit_frame(event, |f| f["version"] = 51.into())),
        ),
        (
            initial.to_vec(),
            Some(edit_frame(event, |f| f["status"] = serde_json::Value::Null)),
        ),
        (initial.to_vec(), Some(vec![1, 0, 0, 0, b'{'])),
        (initial.to_vec(), Some(65_537_i32.to_le_bytes().to_vec())),
    ] {
        let (client, mut server) = duplex(16_384);
        let mut client = Some(client);
        let authority = Authority::live();
        let opened = Cell::new(false);
        let expect_open = next.is_some();
        let watch = watch_on(
            Some(&authority),
            pending(),
            || Ok(REQUEST_ID.into()),
            async || Ok(client.take().expect("protocol failure was retried")),
            |_| Ok((PID, 0)),
            |update| {
                assert!(matches!(update, StatusUpdate::Opened(_)));
                assert!(!opened.replace(true));
                Ok(())
            },
        );
        let drive = async {
            accept(&mut server, REQUEST_ID).await;
            server.write_all(&first).await.unwrap();
            if let Some(next) = next {
                server.write_all(&next).await.unwrap();
            }
            assert_eq!(server.read(&mut [0]).await.unwrap(), 0);
        };
        let (result, ()) = tokio::join!(watch, drive);
        assert_eq!(result.unwrap_err().kind(), io::ErrorKind::InvalidData);
        assert_eq!(opened.get(), expect_open);
    }
}

#[tokio::test]
async fn subscription_discards_snapshot_if_peer_or_authority_is_lost() {
    for lose_on_open in [false, true] {
        for change in [None, NonZeroU32::new(PID + 1), NonZeroU32::new(PID)] {
            let authority = Authority::live();
            let session = Cell::new(0);
            let (client, mut server) = duplex(16_384);
            let mut client = Some(client);
            let (opened, ready) = oneshot::channel();
            let mut opened = Some(opened);
            let lose_proof = || {
                authority.pid.set(change);
                if change == NonZeroU32::new(PID) {
                    session.set(3);
                }
            };
            let watch = watch_on(
                Some(&authority),
                pending(),
                || Ok(REQUEST_ID.into()),
                async || Ok(client.take().expect("peer failure was retried")),
                |_| Ok((PID, session.get())),
                |update| {
                    assert!(!lose_on_open, "unverified initial snapshot was published");
                    assert!(matches!(update, StatusUpdate::Opened(_)));
                    opened.take().unwrap().send(()).unwrap();
                    Ok(())
                },
            );
            let drive = async {
                accept(&mut server, REQUEST_ID).await;
                if lose_on_open {
                    lose_proof();
                }
                server.write_all(frames()[1]).await.unwrap();
                if !lose_on_open {
                    ready.await.unwrap();
                    lose_proof();
                    server.write_all(frames()[2]).await.unwrap();
                }
                assert_eq!(server.read(&mut [0]).await.unwrap(), 0);
            };
            assert_eq!(
                tokio::join!(watch, drive).0.unwrap_err().kind(),
                io::ErrorKind::PermissionDenied
            );
        }
    }
}

#[tokio::test(start_paused = true)]
async fn interrupted_subscription_backs_off_and_resets_after_stable_idle() {
    let (client, mut first) = duplex(16_384);
    let (capacity, mut busy) = duplex(16_384);
    let (replacement, mut last) = duplex(16_384);
    let mut connections = VecDeque::from([
        Some(client),
        Some(capacity),
        None,
        None,
        None,
        None,
        None,
        None,
        Some(replacement),
        None,
    ]);
    let attempts = RefCell::new(Vec::new());
    let (updates, mut received) = tokio::sync::mpsc::unbounded_channel();
    let (stop, stopped) = oneshot::channel();
    let authority = Authority::live();
    let start = Instant::now();
    let mut ids = 55..;
    let watch = watch_on(
        Some(&authority),
        async { stopped.await.unwrap() },
        || {
            Ok(format!(
                "11111111-2222-4333-8444-5555555555{}",
                ids.next().unwrap()
            ))
        },
        async || {
            attempts.borrow_mut().push(start.elapsed());
            connections
                .pop_front()
                .expect("unexpected reconnect")
                .ok_or_else(|| io::ErrorKind::NotFound.into())
        },
        |_| Ok((PID, 0)),
        |update| {
            updates.send(update).unwrap();
            Ok(())
        },
    );
    let drive = async {
        accept(&mut first, REQUEST_ID).await;
        first.write_all(frames()[1]).await.unwrap();
        first.write_all(frames()[2]).await.unwrap();
        let Some(StatusUpdate::Opened(_)) = received.recv().await else {
            panic!("expected first open")
        };
        let Some(StatusUpdate::Event(event)) = received.recv().await else {
            panic!("expected first event")
        };
        let latest = revision(&event);
        first.write_all(&frames()[2][..8]).await.unwrap();
        drop(first);
        accept(&mut busy, NEXT_ID).await;
        let capacity = edit_frame(frames()[1], |response| {
            response["requestId"] = NEXT_ID.into();
            response["success"] = false.into();
            response["error"] = "capacity unavailable".into();
            response["errorCode"] = 0.into();
            response["status"] = serde_json::Value::Null;
        });
        busy.write_all(&capacity).await.unwrap();
        for _ in 0..8 {
            assert!(matches!(
                received.recv().await,
                Some(StatusUpdate::Reconnecting)
            ));
        }
        accept(&mut last, REPLACEMENT_ID).await;
        let replacement = edit_frame(frames()[1], |response| {
            response["requestId"] = REPLACEMENT_ID.into();
        });
        last.write_all(&replacement).await.unwrap();
        let Some(StatusUpdate::Opened(initial)) = received.recv().await else {
            panic!("expected replacement initial snapshot")
        };
        assert!(revision(&initial.into_observation()) < latest);
        advance(MAX_RECONNECT_DELAY).await;
        drop(last);
        assert!(matches!(
            received.recv().await,
            Some(StatusUpdate::Reconnecting)
        ));
        assert!(matches!(
            received.recv().await,
            Some(StatusUpdate::Reconnecting)
        ));
        stop.send(()).unwrap();
    };
    assert_eq!(
        tokio::join!(watch, drive).0.unwrap_err().kind(),
        io::ErrorKind::Interrupted
    );
    let millis: Vec<_> = attempts
        .into_inner()
        .iter()
        .map(Duration::as_millis)
        .collect();
    assert_eq!(
        millis,
        [0, 250, 750, 1750, 3750, 7750, 12750, 17750, 22750, 28000]
    );
}

#[tokio::test(start_paused = true)]
async fn retained_idle_pipe_monitor_rejects_changed_peer_and_closes_partial_frame_without_reopen() {
    let (client, mut server) = duplex(16_384);
    let mut client = Some(client);
    let authority = Authority::live();
    let peer = std::rc::Rc::new(Cell::new((PID, 0)));
    let (opened, mut received) = tokio::sync::mpsc::unbounded_channel();
    let watch = watch_monitored(
        Some(&authority),
        pending::<()>(),
        || Ok(REQUEST_ID.into()),
        async || Ok(client.take().expect("denied peer must not reconnect")),
        |_| Ok((PID, 0)),
        |_| {
            let peer = peer.clone();
            Ok(Some(Box::new(move || Ok(peer.get()))))
        },
        |update| {
            opened.send(update).unwrap();
            Ok(())
        },
    );
    let drive = async {
        accept(&mut server, REQUEST_ID).await;
        server.write_all(frames()[1]).await.unwrap();
        assert!(matches!(
            received.recv().await,
            Some(StatusUpdate::Opened(_))
        ));
        server.write_all(&frames()[2][..8]).await.unwrap();
        tokio::task::yield_now().await;
        peer.set((PID + 1, 0));
        advance(Duration::from_millis(500)).await;
        assert_eq!(server.read(&mut [0]).await.unwrap(), 0);
        assert!(received.try_recv().is_err());
    };
    let (result, ()) = tokio::join!(watch, drive);
    assert_eq!(result.unwrap_err().kind(), io::ErrorKind::PermissionDenied);
}

#[tokio::test(start_paused = true)]
async fn idle_checks_after_a_suspend_run_once_not_in_a_burst() {
    let (client, mut server) = duplex(16_384);
    let mut client = Some(client);
    let authority = Authority::live();
    let checks = std::rc::Rc::new(Cell::new(0usize));
    let (stop, stopped) = oneshot::channel();
    let (opened, mut received) = tokio::sync::mpsc::unbounded_channel();
    let watch = watch_monitored(
        Some(&authority),
        async { stopped.await.unwrap() },
        || Ok(REQUEST_ID.into()),
        async || Ok(client.take().expect("a healthy idle stream was reopened")),
        |_| Ok((PID, 0)),
        |_| {
            let checks = checks.clone();
            Ok(Some(Box::new(move || {
                checks.set(checks.get() + 1);
                Ok((PID, 0))
            })))
        },
        |update| {
            opened.send(update).unwrap();
            Ok(())
        },
    );
    let drive = async {
        accept(&mut server, REQUEST_ID).await;
        server.write_all(frames()[1]).await.unwrap();
        assert!(matches!(
            received.recv().await,
            Some(StatusUpdate::Opened(_))
        ));
        let settle = || async {
            for _ in 0..1_000 {
                tokio::task::yield_now().await;
            }
        };
        settle().await;
        assert_eq!(checks.get(), 1, "the first check is immediate");
        // Windows `Instant` keeps counting through sleep, so the clock jumps.
        for suspended in [Duration::from_secs(600), Duration::from_secs(8 * 3600)] {
            let before = checks.get();
            advance(suspended).await;
            settle().await;
            assert_eq!(checks.get() - before, 1, "{suspended:?}");
        }
        assert!(received.try_recv().is_err());
        stop.send(()).unwrap();
    };
    assert_eq!(
        tokio::join!(watch, drive).0.unwrap_err().kind(),
        io::ErrorKind::Interrupted
    );
}

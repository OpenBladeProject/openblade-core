use super::*;
use tokio::{
    io::AsyncReadExt,
    time::{advance, sleep},
};

const REPLAY: &[u8] = include_bytes!("../../../../tests/fixtures/subscribe-status-v79.frames");
const ID: &str = "11111111-2222-4333-8444-555555555555";

fn replay() -> [&'static [u8]; 3] {
    let mut remaining = REPLAY;
    let frames = std::array::from_fn(|_| {
        let length = u32::from_le_bytes(remaining[..4].try_into().unwrap()) as usize;
        let (frame, rest) = remaining.split_at(4 + length);
        remaining = rest;
        frame
    });
    assert!(remaining.is_empty());
    frames
}

fn frame_value(frame: &[u8]) -> Value {
    serde_json::from_slice(&frame[4..]).unwrap()
}

fn publication(active: Value) -> Arc<Publication> {
    assert_eq!(active["controlAccess"], 0);
    let mut inactive = active.clone();
    // This fixture models the publisher's separate views. Sentinel values
    // make it observable if the listener sends the wrong one after rotation.
    inactive["message"] = json!("Inactive session view");
    inactive["controlAccess"] = json!(1);
    inactive["profiles"]["pluggedIn"]["performance"] = json!(1);
    Arc::new(Publication {
        active: serde_json::from_value(active).unwrap(),
        inactive: serde_json::from_value(inactive).unwrap(),
    })
}

type Ingress = mpsc::UnboundedSender<Box<dyn Stream>>;

fn connect(tx: &Ingress, capacity: usize) -> DuplexStream {
    let (client, server) = duplex(capacity);
    tx.send(test_stream(server, SYSTEM, Fault::None)).unwrap();
    client
}

async fn send_subscribe(client: &mut DuplexStream) -> Value {
    client.write_all(replay()[0]).await.unwrap();
    read_json(client).await
}

async fn read_json(client: &mut DuplexStream) -> Value {
    serde_json::from_slice(&read_payload(client).await.unwrap()).unwrap()
}

async fn assert_eof(client: &mut DuplexStream) {
    let mut trailing = Vec::new();
    client.read_to_end(&mut trailing).await.unwrap();
    assert!(trailing.is_empty());
}

async fn get_status(tx: &Ingress) {
    let mut client = connect(tx, 128);
    let request = GetStatus::new(ID.to_owned()).unwrap();
    exchange(&mut client, &request, Some(0), |_| Ok(()))
        .await
        .unwrap();
}

#[tokio::test(start_paused = true)]
async fn installed_subscription_keeps_managed_event_published_during_initial_write() {
    let [request, expected_initial, expected_event] = replay();
    let initial = frame_value(expected_initial);
    let event = frame_value(expected_event);
    let (publish, feed) = watch::channel(publication(initial["status"].clone()));
    let events = Events::default();
    let (ports, tx) = ports_with_feed(Fault::None, &events, Some(feed));
    let (shutdown, stop) = oneshot::channel();
    let (failed, _failure) = oneshot::channel();
    let (_, driver) = prepare(PID, stop, failed, Ok(ports)).await.unwrap();
    let clients = async {
        let mut client = connect(&tx, 64);
        client.write_all(request).await.unwrap();
        let length = client.read_i32_le().await.unwrap();
        assert!(length > 64);
        publish.send_replace(publication(event["status"].clone()));
        let mut payload = vec![0; length as usize];
        client.read_exact(&mut payload).await.unwrap();
        assert_eq!(serde_json::from_slice::<Value>(&payload).unwrap(), initial);
        assert_eq!(read_json(&mut client).await, event);

        publish.send_replace(publication(initial["status"].clone()));
        publish.send_replace(publication(event["status"].clone()));
        // Both the initial and last delivered revision are stale for this stream.
        assert!(
            timeout(Duration::from_millis(5), client.read_u8())
                .await
                .is_err()
        );
        shutdown.send(()).unwrap();
        assert_eof(&mut client).await;
    };
    let (result, ()) = tokio::join!(driver, clients);
    result.unwrap();
    assert_eq!(*events.borrow(), ["connect", "snapshot", "close", "drain"]);
}

#[tokio::test(start_paused = true)]
async fn installed_subscription_quota_disconnect_rotation_and_get_status_coexist() {
    let initial = frame_value(replay()[1]);
    let (_publish, feed) = watch::channel(publication(initial["status"].clone()));
    let (ports, tx) = ports_with_feed(Fault::None, &Events::default(), Some(feed));
    let (shutdown, stop) = oneshot::channel();
    let (failed, _failure) = oneshot::channel();
    let (_, driver) = prepare(PID, stop, failed, Ok(ports)).await.unwrap();
    let clients = async {
        let mut subscribers = Vec::new();
        for _ in 0..MAX_SUBSCRIPTIONS {
            let mut client = connect(&tx, 128);
            assert_eq!(send_subscribe(&mut client).await, initial);
            subscribers.push(client);
        }
        let mut overflow = connect(&tx, 128);
        assert_eq!(
            send_subscribe(&mut overflow).await,
            json!({
                "frame": "response", "version": 79, "requestId": ID, "success": false,
                "error": "Status subscription capacity is unavailable.", "status": null,
                "result": null, "archive": null, "errorCode": 0,
            })
        );
        assert_eof(&mut overflow).await;
        get_status(&tx).await;

        drop(subscribers.pop());
        sleep(Duration::from_millis(1)).await;
        let mut replacement = connect(&tx, 128);
        assert_eq!(send_subscribe(&mut replacement).await, initial);
        replacement.write_u8(1).await.unwrap();
        assert_eof(&mut replacement).await;

        advance(Duration::from_secs(15 * 60)).await;
        for mut client in subscribers {
            assert_eof(&mut client).await;
        }
        let mut after_rotation = connect(&tx, 128);
        assert_eq!(send_subscribe(&mut after_rotation).await, initial);
        shutdown.send(()).unwrap();
        assert_eof(&mut after_rotation).await;
    };
    let (result, ()) = tokio::join!(driver, clients);
    result.unwrap();
}

#[tokio::test(start_paused = true)]
async fn installed_subscription_coalesces_slow_readers_and_times_out_blocked_writes() {
    let initial = frame_value(replay()[1]);
    let mut current = frame_value(replay()[2])["status"].clone();
    let revision = current["revision"].as_i64().unwrap();
    let (publish, feed) = watch::channel(publication(initial["status"].clone()));
    let (ports, tx) = ports_with_feed(Fault::None, &Events::default(), Some(feed));
    let (shutdown, stop) = oneshot::channel();
    let (failed, _failure) = oneshot::channel();
    let (_, driver) = prepare(PID, stop, failed, Ok(ports)).await.unwrap();
    let clients = async {
        let mut slow = connect(&tx, 64);
        assert_eq!(send_subscribe(&mut slow).await, initial);
        publish.send_replace(publication(current.clone()));
        let length = slow.read_i32_le().await.unwrap();
        assert!(length > 64);
        get_status(&tx).await;
        for delta in 1..=3 {
            current["revision"] = json!(revision + delta);
            publish.send_replace(publication(current.clone()));
        }
        let mut payload = vec![0; length as usize];
        slow.read_exact(&mut payload).await.unwrap();
        assert_eq!(
            serde_json::from_slice::<Value>(&payload).unwrap()["status"]["revision"],
            revision
        );
        assert_eq!(read_json(&mut slow).await["status"], current);

        current["revision"] = json!(revision + 4);
        publish.send_replace(publication(current));
        let length = slow.read_i32_le().await.unwrap();
        sleep(FRAME_TIMEOUT + Duration::from_millis(1)).await;
        let mut partial = Vec::new();
        slow.read_to_end(&mut partial).await.unwrap();
        assert!(partial.len() < length as usize);
        get_status(&tx).await;
        shutdown.send(()).unwrap();
    };
    let (result, ()) = tokio::join!(driver, clients);
    result.unwrap();
}

#[cfg(feature = "native-agent-entry-compile-check")]
#[tokio::test(start_paused = true)]
async fn installed_subscription_exchanges_with_phase_10g_client_and_rechecks_peer() {
    use crate::service_status::{read_status_event, subscribe};

    let initial = frame_value(replay()[1]);
    let mut current = frame_value(replay()[2])["status"].clone();
    let revision = current["revision"].as_i64().unwrap();
    let (publish, feed) = watch::channel(publication(initial["status"].clone()));
    let (ports, tx) = ports_with_feed(Fault::None, &Events::default(), Some(feed));
    let (shutdown, stop) = oneshot::channel();
    let (failed, _failure) = oneshot::channel();
    let (_, driver) = prepare(PID, stop, failed, Ok(ports)).await.unwrap();
    let clients = async {
        let peer_live = Rc::new(Cell::new(true));
        let console = Rc::new(Cell::new(Some(SETUP.session)));
        let (mut client, pipe) = duplex(128);
        tx.send(Box::new(TestStream {
            pipe,
            pid_reads: Cell::new(0),
            read: 0,
            caller: SETUP,
            control_session: Rc::new({
                let console = Rc::clone(&console);
                move || Ok(console.get())
            }),
            fault: Fault::None,
            peer_live: Rc::clone(&peer_live),
        }))
        .unwrap();
        let mut checks = 0;
        let opened = subscribe(&mut client, ID.to_owned(), |_| {
            checks += 1;
            Ok(())
        })
        .await
        .unwrap();
        assert_eq!(checks, 2);
        opened.verify(0).unwrap();
        assert_eq!(serde_json::to_value(opened).unwrap(), initial["status"]);

        console.set(None);
        publish.send_replace(publication(current.clone()));
        let inactive = read_status_event(&mut client).await.unwrap();
        inactive.verify(1).unwrap();
        let inactive = serde_json::to_value(inactive).unwrap();
        assert_eq!(inactive["message"], "Inactive session view");
        assert_eq!(inactive["revision"], revision);

        peer_live.set(false);
        current["revision"] = json!(revision + 1);
        publish.send_replace(publication(current));
        assert_eof(&mut client).await;
        get_status(&tx).await;
        shutdown.send(()).unwrap();
    };
    let (result, ()) = tokio::join!(driver, clients);
    result.unwrap();
}

#[tokio::test(start_paused = true)]
async fn installed_subscription_without_a_live_publisher_returns_no_snapshot() {
    let initial = frame_value(replay()[1]);
    let (publish, feed) = watch::channel(publication(initial["status"].clone()));
    drop(publish);
    for feed in [None, Some(feed)] {
        let (ports, tx) = ports_with_feed(Fault::None, &Events::default(), feed);
        let (shutdown, stop) = oneshot::channel();
        let (failed, _failure) = oneshot::channel();
        let (_, driver) = prepare(PID, stop, failed, Ok(ports)).await.unwrap();
        let clients = async {
            let mut client = connect(&tx, 128);
            assert_eq!(
                send_subscribe(&mut client).await,
                json!({
                    "frame": "response", "version": 79, "requestId": ID, "success": false,
                    "error": "Status publication is unavailable.", "status": null,
                    "result": null, "archive": null, "errorCode": null,
                })
            );
            assert_eof(&mut client).await;
            get_status(&tx).await;
            shutdown.send(()).unwrap();
        };
        let (result, ()) = tokio::join!(driver, clients);
        result.unwrap();
    }
}

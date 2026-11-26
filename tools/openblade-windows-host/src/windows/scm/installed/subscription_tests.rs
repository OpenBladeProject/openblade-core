use super::*;
use tokio::{
    io::AsyncReadExt,
    time::{advance, sleep},
};

const REPLAY: &[u8] = include_bytes!("../../../../tests/fixtures/subscribe-status-v91.frames");
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
    let request = StatusRequest::get(ID.to_owned()).unwrap();
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
async fn in_memory_observe_state_refusal_coexists_with_get_status_and_subscription() {
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
                "frame": "response", "version": 91, "requestId": ID, "success": false,
                "error": "Status subscription capacity is unavailable.", "status": null,
                "result": null, "archive": null, "errorCode": 0,
            })
        );
        assert_eof(&mut overflow).await;
        get_status(&tx).await;

        let mut observer = connect(&tx, 128);
        let replay = include_bytes!("../../../../tests/fixtures/observe-state-v91.frames");
        let length = u32::from_le_bytes(replay[..4].try_into().unwrap()) as usize;
        observer.write_all(&replay[..4 + length]).await.unwrap();
        let refused = read_json(&mut observer).await;
        assert_eq!(refused["requestId"], ID);
        assert_eq!(refused["success"], false);
        assert_eq!(refused["status"], Value::Null);
        assert_eof(&mut observer).await;

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
            request_read: Cell::new(None),
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
                    "frame": "response", "version": 91, "requestId": ID, "success": false,
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

fn in_memory_ports(
    read: impl Fn(&std::sync::atomic::AtomicBool) -> bool + Send + Sync + 'static,
) -> (Ports, Ingress) {
    let (mut ports, ingress) = ports(Fault::None, &Events::default());
    *Rc::get_mut(&mut ports.source).unwrap().get_mut() = status_source::in_memory_source(read);
    (ports, ingress)
}

fn in_memory_get(tx: &Ingress) -> (DuplexStream, oneshot::Receiver<()>) {
    let (client, pipe) = duplex(128);
    let (read, seen) = oneshot::channel();
    tx.send(Box::new(TestStream {
        pipe,
        pid_reads: Cell::new(0),
        read: 0,
        caller: SYSTEM,
        control_session: Rc::new(|| Ok(None)),
        fault: Fault::None,
        peer_live: Rc::new(Cell::new(true)),
        request_read: Cell::new(Some(read)),
    }))
    .unwrap();
    (client, seen)
}

struct RefreshDrop(Events, &'static str);

impl Drop for RefreshDrop {
    fn drop(&mut self) {
        self.0.borrow_mut().push(self.1);
    }
}

struct RefreshLifecycleSource {
    publisher: Option<watch::Sender<Arc<Publication>>>,
    due: Instant,
    snapshots: usize,
    queue_refresh: bool,
    entered: mpsc::UnboundedSender<()>,
    events: Events,
}

impl StatusSource for RefreshLifecycleSource {
    fn snapshot(&mut self, caller: Caller, _: Instant) -> Pending<'_, Snapshot> {
        Box::pin(async move {
            self.snapshots += 1;
            if self.queue_refresh && self.snapshots > 1 {
                let _drop = RefreshDrop(self.events.clone(), "snapshot-dropped");
                self.entered.send(()).unwrap();
                return pending().await;
            }
            let mut status = managed_status();
            status["controlAccess"] = json!(caller.access()?);
            Ok(serde_json::from_value(status)?)
        })
    }

    fn publications(&self) -> Option<watch::Receiver<Arc<Publication>>> {
        self.publisher.as_ref().map(watch::Sender::subscribe)
    }

    fn refresh_publications(&mut self) -> Pending<'_, Instant> {
        Box::pin(async move {
            if Instant::now() < self.due {
                return Ok(self.due);
            }
            assert!(!self.queue_refresh, "queued refresh survived into drain");
            let _drop = RefreshDrop(self.events.clone(), "refresh-dropped");
            self.entered.send(()).unwrap();
            pending().await
        })
    }

    fn drain(&mut self) -> Pending<'_, ()> {
        Box::pin(async move {
            let expected = if self.queue_refresh {
                "snapshot-dropped"
            } else {
                "refresh-dropped"
            };
            assert!(self.events.borrow().contains(&expected));
            self.events.borrow_mut().push("drain");
            self.publisher = None;
            Ok(())
        })
    }
}

#[tokio::test(start_paused = true)]
async fn in_memory_shutdown_drops_refresh_holding_or_queued_for_source_before_drain() {
    for queue_refresh in [false, true] {
        let events = Events::default();
        let (publisher, _) = watch::channel(publication(managed_status()));
        let (entered, mut running) = mpsc::unbounded_channel();
        let (mut ports, tx) = ports(Fault::None, &events);
        *Rc::get_mut(&mut ports.source).unwrap().get_mut() = Box::new(RefreshLifecycleSource {
            publisher: Some(publisher),
            due: Instant::now() + subscription::REFRESH_INTERVAL,
            snapshots: 0,
            queue_refresh,
            entered,
            events: events.clone(),
        });
        let (shutdown, stop) = oneshot::channel();
        let (failed, mut failure) = oneshot::channel();
        let (_, driver) = prepare(PID, stop, failed, Ok(ports)).await.unwrap();
        let driver_polls = Cell::new(0usize);
        let mut driver = Box::pin(driver);
        let driver = std::future::poll_fn(|cx| {
            driver_polls.set(driver_polls.get() + 1);
            std::future::Future::poll(driver.as_mut(), cx)
        });
        let clients = async {
            let mut subscriber = connect(&tx, 128);
            assert_eq!(send_subscribe(&mut subscriber).await["success"], true);
            let mut get = None;
            if queue_refresh {
                advance(subscription::REFRESH_INTERVAL / 2).await;
                let (mut client, _) = in_memory_get(&tx);
                write_frame(&mut client, &StatusRequest::get(ID.to_owned()).unwrap())
                    .await
                    .unwrap();
                running.recv().await.unwrap();
                get = Some(client);
            }
            advance(if queue_refresh {
                subscription::REFRESH_INTERVAL / 2
            } else {
                subscription::REFRESH_INTERVAL
            })
            .await;
            if queue_refresh {
                // Require a completed driver poll after the refresh deadline.
                // LocalSet::run_until polls serve_connections, whose select
                // polls the due refresh before connections/ingress. GetStatus
                // still holds the source mutex, so refresh queues for it.
                let before = driver_polls.get();
                while driver_polls.get() == before {
                    tokio::task::yield_now().await;
                }
            } else {
                running.recv().await.unwrap();
            }
            shutdown.send(()).unwrap();
            assert_eof(&mut subscriber).await;
            if let Some(mut get) = get {
                assert_eof(&mut get).await;
            }
        };
        let (result, ()) = timeout(subscription::REFRESH_INTERVAL + DRAIN_TIMEOUT, async {
            tokio::join!(driver, clients)
        })
        .await
        .expect("refresh scope must end before source drain");
        result.unwrap();
        assert!(matches!(
            failure.try_recv(),
            Err(oneshot::error::TryRecvError::Closed)
        ));
        let events = events.borrow();
        assert_eq!(events.last(), Some(&"drain"));
        assert!(
            events.iter().position(|event| *event == "close").unwrap()
                < events.iter().position(|event| *event == "drain").unwrap()
        );
    }
}

#[tokio::test]
async fn in_memory_candidate_shares_initial_refresh_with_get_and_drains_one_pending_worker() {
    use std::sync::{
        Mutex as ThreadMutex,
        atomic::{AtomicUsize, Ordering},
        mpsc as thread_channel,
    };

    timeout(Duration::from_secs(8), async {
        let scans = Arc::new(AtomicUsize::new(0));
        let (entered, mut running) = mpsc::unbounded_channel();
        let (release_first, wait_first) = thread_channel::channel();
        let (release_stop, wait_stop) = thread_channel::channel();
        let (canceled, mut saw_cancel) = mpsc::unbounded_channel();
        let wait_first = ThreadMutex::new(wait_first);
        let wait_stop = ThreadMutex::new(wait_stop);
        let (ports, tx) = in_memory_ports({
            let scans = Arc::clone(&scans);
            move |cancel| match scans.fetch_add(1, Ordering::Relaxed) {
                0 => false,
                1 => {
                    entered.send(1).unwrap();
                    wait_first
                        .lock()
                        .unwrap()
                        .recv_timeout(Duration::from_secs(5))
                        .unwrap();
                    true
                }
                2 => {
                    entered.send(2).unwrap();
                    while !cancel.load(Ordering::Relaxed) {
                        std::thread::park_timeout(Duration::from_millis(1));
                    }
                    canceled.send(()).unwrap();
                    wait_stop
                        .lock()
                        .unwrap()
                        .recv_timeout(Duration::from_secs(5))
                        .unwrap();
                    false
                }
                _ => panic!("observation work exceeded its single pending worker"),
            }
        });
        let (shutdown, stop) = oneshot::channel();
        let (failed, _failure) = oneshot::channel();
        let (_, driver) = prepare(PID, stop, failed, Ok(ports)).await.unwrap();
        let clients = async {
            sleep(subscription::REFRESH_INTERVAL).await;
            assert_eq!(scans.load(Ordering::Relaxed), 1, "polling without demand");
            let mut first = connect(&tx, 128);
            first.write_all(replay()[0]).await.unwrap();
            assert_eq!(running.recv().await, Some(1));

            let mut second = connect(&tx, 128);
            second.write_all(replay()[0]).await.unwrap();
            let (mut get, read) = in_memory_get(&tx);
            let request = StatusRequest::get(ID.to_owned()).unwrap();
            write_frame(&mut get, &request).await.unwrap();
            read.await.unwrap();
            assert_eq!(scans.load(Ordering::Relaxed), 2);
            release_first.send(()).unwrap();
            let (first_reply, second_reply, get_reply) = tokio::join!(
                read_json(&mut first),
                read_json(&mut second),
                read_json(&mut get)
            );
            assert_eq!(first_reply["status"]["revision"], 1);
            assert_eq!(first_reply["status"]["activePowerSource"], 1);
            assert_eq!(first_reply["status"], second_reply["status"]);
            assert_eq!(first_reply["status"], get_reply["status"]);
            assert_eq!(scans.load(Ordering::Relaxed), 2);

            let (mut observing, _) = in_memory_get(&tx);
            write_frame(&mut observing, &request).await.unwrap();
            assert_eq!(running.recv().await, Some(2));
            let mut pending = vec![observing, first, second];
            for _ in 0..4 {
                let (mut queued, read) = in_memory_get(&tx);
                write_frame(&mut queued, &request).await.unwrap();
                read.await.unwrap();
                pending.push(queued);
            }
            shutdown.send(()).unwrap();
            saw_cancel.recv().await.unwrap();
            for mut client in pending {
                assert_eof(&mut client).await;
            }
            assert_eq!(scans.load(Ordering::Relaxed), 3);
            release_stop.send(()).unwrap();
        };
        let (result, ()) = tokio::join!(driver, clients);
        result.unwrap();
    })
    .await
    .unwrap();
}

#[cfg(feature = "native-agent-entry-compile-check")]
#[tokio::test]
async fn in_memory_phase_10g_watcher_receives_native_observer_change_through_the_current_protocol()
{
    use crate::session_native::service_client::{
        InstalledServiceAuthority, InstalledServiceGeneration, ServiceAuthorityError, StatusUpdate,
        watch_on,
    };
    use std::{
        num::NonZeroU32,
        sync::atomic::{AtomicBool, Ordering},
    };

    struct Authority;
    struct Generation(NonZeroU32);
    impl InstalledServiceGeneration for Generation {
        fn verify_live_process(&self) -> Result<NonZeroU32, ServiceAuthorityError> {
            Ok(self.0)
        }
    }
    impl InstalledServiceAuthority for Authority {
        fn acquire_generation(
            &self,
        ) -> Result<Box<dyn InstalledServiceGeneration + '_>, ServiceAuthorityError> {
            Ok(Box::new(Generation(NonZeroU32::new(PID).unwrap())))
        }
    }

    timeout(Duration::from_secs(6), async {
        let battery = Arc::new(AtomicBool::new(false));
        let (ports, tx) = in_memory_ports({
            let battery = Arc::clone(&battery);
            move |_| battery.load(Ordering::Relaxed)
        });
        let (shutdown, stop) = oneshot::channel();
        let (failed, _failure) = oneshot::channel();
        let (_, driver) = prepare(PID, stop, failed, Ok(ports)).await.unwrap();
        let (stop_agent, canceled) = oneshot::channel();
        let (publish, mut updates) = mpsc::unbounded_channel();
        let watcher = watch_on(
            Some(&Authority),
            async { canceled.await.unwrap() },
            || Ok(ID.to_owned()),
            async || Ok(connect(&tx, 128)),
            |_| Ok((PID, 0)),
            |update| {
                publish.send(update).unwrap();
                Ok(())
            },
        );
        let clients = async {
            let StatusUpdate::Opened(initial) = updates.recv().await.unwrap() else {
                panic!("expected the correlated v91 subscription response");
            };
            let initial = initial.into_observation().snapshot;
            initial.verify(0).unwrap();
            assert_eq!(initial.revision(), 0);

            let (mut remote, pipe) = duplex(128);
            tx.send(test_stream(
                pipe,
                Caller {
                    logon: Logon::RemoteInteractive,
                    ..SETUP
                },
                Fault::None,
            ))
            .unwrap();
            let initial = send_subscribe(&mut remote).await;
            assert_eq!(initial["status"]["revision"], 0);
            assert_eq!(initial["status"]["controlAccess"], 1);

            battery.store(true, Ordering::Relaxed);
            let StatusUpdate::Event(event) = updates.recv().await.unwrap() else {
                panic!("expected a changed observation on the existing v91 stream");
            };
            event.require_fresh().unwrap();
            event.snapshot.verify(0).unwrap();
            let active = serde_json::to_value(event.snapshot).unwrap();
            let inactive = read_json(&mut remote).await;
            assert_eq!(active["revision"], 1);
            assert_eq!(active["activePowerSource"], 1);
            assert!(active["message"].as_str().unwrap().contains("RZ09-0581"));
            assert_eq!(active["writesBlocked"], true);
            assert_eq!(inactive["frame"], "statusEvent");
            assert_eq!(inactive["status"]["revision"], active["revision"]);
            assert_eq!(inactive["status"]["activePowerSource"], 1);
            assert_eq!(inactive["status"]["controlAccess"], 1);
            assert_eq!(
                inactive["status"]["message"],
                "Another Windows session currently controls OpenBlade hardware."
            );
            stop_agent.send(()).unwrap();
            shutdown.send(()).unwrap();
        };
        let (result, watch_result, ()) = tokio::join!(driver, watcher, clients);
        result.unwrap();
        assert_eq!(watch_result.unwrap_err().kind(), io::ErrorKind::Interrupted);
    })
    .await
    .unwrap();
}

#[tokio::test]
async fn in_memory_firmware_observation_allows_status_and_subscription_progress() {
    use crate::discovery::performance::worker_tests::{BlockAt, blocking_worker};
    use std::sync::atomic::{AtomicBool, Ordering};

    const OBSERVE: &[u8] = include_bytes!("../../../../tests/fixtures/observe-state-v91.frames");
    let length = u32::from_le_bytes(OBSERVE[..4].try_into().unwrap()) as usize;
    let (request, response) = OBSERVE.split_at(4 + length);
    let mut guards = frame_value(response)["status"].clone();
    guards["confirmedState"] = Value::Null;
    let status: Snapshot = serde_json::from_value(guards).unwrap();
    let (worker, held) = blocking_worker(BlockAt::Retire, None);
    let firmware = FirmwareSource::new(worker, move |admission| {
        (admission.epoch == 10).then(|| status.clone())
    });

    timeout(Duration::from_secs(5), async {
        let has_metadata = Arc::new(AtomicBool::new(false));
        let (mut ports, tx) = ports(Fault::None, &Events::default());
        *Rc::get_mut(&mut ports.source).unwrap().get_mut() =
            status_source::in_memory_source_with_firmware(
                {
                    let has_metadata = has_metadata.clone();
                    move || has_metadata.load(Ordering::Acquire)
                },
                firmware,
            );
        let (shutdown, stop) = oneshot::channel();
        let (failed, _failure) = oneshot::channel();
        let (_, driver) = prepare(PID, stop, failed, Ok(ports)).await.unwrap();
        let clients = async {
            let mut watcher = connect(&tx, 128);
            let initial = send_subscribe(&mut watcher).await;
            assert_eq!(initial["status"]["revision"], 0);

            let mut observation = connect(&tx, 128);
            observation.write_all(request).await.unwrap();
            held.entered.await.unwrap();
            assert_eq!(held.submitted.load(Ordering::Acquire), 4);

            let mut competing = connect(&tx, 128);
            competing.write_all(request).await.unwrap();
            let refused = read_json(&mut competing).await;
            assert_eq!(refused["success"], false);
            assert_eq!(refused["status"], Value::Null);

            has_metadata.store(true, Ordering::Release);
            get_status(&tx).await;
            let event = read_json(&mut watcher).await;
            assert_eq!(event["frame"], "statusEvent");
            assert_eq!(event["status"]["revision"], 1);
            assert!(
                event["status"]["message"]
                    .as_str()
                    .unwrap()
                    .contains("matching control HID metadata")
            );
            assert_eq!(event["status"]["confirmedState"], Value::Null);

            held.release.send(()).unwrap();
            let observed = read_json(&mut observation).await;
            assert_eq!(observed["requestId"], ID);
            assert_eq!(observed["success"], true);
            assert_eq!(
                observed["status"]["confirmedState"],
                json!({
                    "performance": 2,
                    "fans": 0,
                    "battery": null,
                    "lighting": null,
                    "hyperBoostActive": null,
                })
            );
            shutdown.send(()).unwrap();
        };
        let (result, ()) = tokio::join!(driver, clients);
        result.unwrap();
    })
    .await
    .unwrap();
}

#[tokio::test]
async fn in_memory_firmware_shutdown_reports_pending_native_ownership() {
    use crate::discovery::performance::worker_tests::{BlockAt, blocking_worker};
    use std::sync::atomic::Ordering;

    const OBSERVE: &[u8] = include_bytes!("../../../../tests/fixtures/observe-state-v91.frames");
    let length = u32::from_le_bytes(OBSERVE[..4].try_into().unwrap()) as usize;
    let request = &OBSERVE[..4 + length];
    let (worker, held) = blocking_worker(BlockAt::Submit, None);
    let firmware = FirmwareSource::new(worker.clone(), |_| None);
    let (mut ports, tx) = ports(Fault::None, &Events::default());
    *Rc::get_mut(&mut ports.source).unwrap().get_mut() =
        status_source::in_memory_source_with_firmware(|| false, firmware);
    let (shutdown, stop) = oneshot::channel();
    let (failed, _failure) = oneshot::channel();
    let (_, driver) = prepare(PID, stop, failed, Ok(ports)).await.unwrap();

    let clients = async {
        let mut client = connect(&tx, 128);
        client.write_all(request).await.unwrap();
        held.entered.await.unwrap();
        shutdown.send(()).unwrap();
        let mut tail = Vec::new();
        client.read_to_end(&mut tail).await.unwrap();
        assert!(tail.is_empty());
    };
    let (result, ()) = timeout(Duration::from_secs(5), async {
        tokio::join!(driver, clients)
    })
    .await
    .unwrap();
    assert_eq!(result.unwrap_err().kind(), io::ErrorKind::TimedOut);
    assert_eq!(held.submitted.load(Ordering::Acquire), 1);

    held.release.send(()).unwrap();
    timeout(DRAIN_TIMEOUT, worker.stop())
        .await
        .unwrap()
        .unwrap();
    assert!(held.cancelled.load(Ordering::Acquire));
    assert_eq!(held.submitted.load(Ordering::Acquire), 1);
}

#[cfg(feature = "service-mailbox-observation-v1")]
#[tokio::test]
async fn in_memory_guarded_no_reader_serves_status_subscription_and_own_transport_drain() {
    timeout(Duration::from_secs(5), async {
        let (mut ports, tx) = ports(Fault::None, &Events::default());
        let (source, origin) = status_source::in_memory_guarded_no_reader();
        *Rc::get_mut(&mut ports.source).unwrap().get_mut() = source;
        let (shutdown, stop) = oneshot::channel();
        let (failed, _failure) = oneshot::channel();
        let (_, driver) = prepare(PID, stop, failed, Ok(ports)).await.unwrap();
        let clients = async {
            let mut watcher = connect(&tx, 128);
            let initial = send_subscribe(&mut watcher).await;
            assert!(
                initial["status"]["message"]
                    .as_str()
                    .unwrap()
                    .contains("before native I/O")
            );
            assert!(initial["status"]["confirmedState"].is_null());
            get_status(&tx).await;
            shutdown.send(()).unwrap();
        };
        let (result, ()) = tokio::join!(driver, clients);
        result.unwrap();
        assert_eq!(
            origin
                .transport_drains
                .load(std::sync::atomic::Ordering::Acquire),
            1
        );
    })
    .await
    .unwrap();
}

// Pure async source: its refresh holds the production source mutex across an
// origin tick. No worker, OS supplier or hardware runs.
struct HeldPublicationSource {
    inner: TestSource,
    armed: Rc<Cell<bool>>,
    entered: Rc<tokio::sync::Notify>,
    release: Rc<tokio::sync::Notify>,
    completed: Rc<Cell<bool>>,
    origin_lost: Rc<Cell<bool>>,
    origin_checks: Rc<Cell<usize>>,
    publish: watch::Sender<Arc<Publication>>,
    event: Arc<Publication>,
}

impl StatusSource for HeldPublicationSource {
    fn snapshot(&mut self, caller: Caller, requested: Instant) -> Pending<'_, Snapshot> {
        self.inner.snapshot(caller, requested)
    }
    fn publications(&self) -> Option<watch::Receiver<Arc<Publication>>> {
        self.inner.publications()
    }
    fn refresh_publications(&mut self) -> Pending<'_, Instant> {
        Box::pin(async move {
            if self.armed.replace(false) {
                self.entered.notify_one();
                self.release.notified().await;
                self.completed.set(true);
                if !self.origin_lost.get() {
                    self.publish.send_replace(Arc::clone(&self.event));
                }
            }
            Ok(Instant::now() + subscription::REFRESH_INTERVAL)
        })
    }
    fn verify_origin(&self) -> io::Result<()> {
        self.origin_checks.set(self.origin_checks.get() + 1);
        if self.origin_lost.get() {
            Err(io::Error::other("injected retained origin lost"))
        } else {
            Ok(())
        }
    }
    fn drain(&mut self) -> Pending<'_, ()> {
        self.inner.drain()
    }
}

#[tokio::test(start_paused = true)]
async fn edge_outer_held_refresh_preserves_origin_checks_and_shutdown() {
    // 0: release delivers an event; 1: origin loss fails the runtime;
    // 2: STOP cancels the held async refresh and drains the source.
    for mode in 0..3 {
        let initial = frame_value(replay()[1]);
        let event = frame_value(replay()[2]);
        let (publish, feed) = watch::channel(publication(initial["status"].clone()));
        let events = Events::default();
        let (mut ports, tx) = ports_with_feed(Fault::None, &events, None);
        let armed = Rc::new(Cell::new(false));
        let entered = Rc::new(tokio::sync::Notify::new());
        let release = Rc::new(tokio::sync::Notify::new());
        let completed = Rc::new(Cell::new(false));
        let origin_lost = Rc::new(Cell::new(false));
        let origin_checks = Rc::new(Cell::new(0));
        ports.source = Rc::new(Mutex::new(Box::new(HeldPublicationSource {
            inner: TestSource {
                fault: Fault::None,
                events: events.clone(),
                publications: Some(feed),
            },
            armed: Rc::clone(&armed),
            entered: Rc::clone(&entered),
            release: Rc::clone(&release),
            completed: Rc::clone(&completed),
            origin_lost: Rc::clone(&origin_lost),
            origin_checks: Rc::clone(&origin_checks),
            publish,
            event: publication(event["status"].clone()),
        })));
        let source = Rc::clone(&ports.source);
        let (shutdown, stop) = oneshot::channel();
        let (failed, failure) = oneshot::channel();
        let (_, driver) = prepare(PID, stop, failed, Ok(ports)).await.unwrap();
        let clients = async {
            let mut client = connect(&tx, 128);
            assert_eq!(send_subscribe(&mut client).await, initial);
            armed.set(true);
            advance(subscription::REFRESH_INTERVAL).await;
            entered.notified().await;
            assert!(source.try_lock().is_err(), "refresh must own source mutex");
            eprintln!("ORIGIN_CYCLE held mode={mode}");
            let checks_before = origin_checks.get();
            // Poll the outer select after the origin deadline, while held.
            advance(Duration::from_millis(501)).await;
            tokio::task::yield_now().await;
            assert_eq!(origin_checks.get(), checks_before);
            assert!(!completed.get());
            eprintln!("ORIGIN_CYCLE tick-polled mode={mode}");
            if mode == 2 {
                shutdown.send(()).unwrap();
            } else {
                origin_lost.set(mode == 1);
                release.notify_one();
                eprintln!("ORIGIN_CYCLE released mode={mode}");
                if mode == 0 {
                    assert_eq!(read_json(&mut client).await, event);
                    get_status(&tx).await;
                    assert!(origin_checks.get() > checks_before);
                } else {
                    failure.await.unwrap();
                    assert!(origin_checks.get() > checks_before);
                }
                assert!(completed.get());
                shutdown.send(()).unwrap();
            }
            assert_eof(&mut client).await;
        };
        let (result, ()) = timeout(Duration::from_secs(10), async {
            tokio::join!(driver, clients)
        })
        .await
        .expect("outer runtime must poll refresh, origin and STOP concurrently");
        if mode == 1 {
            assert_eq!(
                result.unwrap_err().to_string(),
                "injected retained origin lost"
            );
        } else {
            result.unwrap();
        }
        assert_eq!(completed.get(), mode != 2);
        assert!(events.borrow().ends_with(&["close", "drain"]));
    }
}

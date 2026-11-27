#[cfg(feature = "service-diagnostics-export-v1")]
#[path = "readiness_tests/export.rs"]
mod export;
#[cfg(any(
    feature = "service-lifecycle-shutdown-v1",
    feature = "service-session-power-relay-v1"
))]
#[path = "readiness_tests/lifecycle_operations.rs"]
mod lifecycle_operations;
#[path = "readiness_tests/observation.rs"]
mod observation;
#[path = "readiness_tests/unserved.rs"]
mod unserved;

use std::{
    cell::{Cell, RefCell},
    future::pending,
    pin::Pin,
    rc::Rc,
    task::{Context, Poll},
};

use serde_json::{Value, json};
use tokio::{
    io::{AsyncWriteExt, DuplexStream, ReadBuf, duplex},
    sync::mpsc,
};
use windows_sys::Win32::{
    Foundation::{ERROR_GEN_FAILURE, NO_ERROR},
    System::Services::{
        SERVICE_RUNNING, SERVICE_START_PENDING, SERVICE_STOP_PENDING, SERVICE_STOPPED,
        SERVICE_WIN32_OWN_PROCESS,
    },
};

use super::*;
use crate::installed::{IMAGE_NAME, ProcessEvidence, run_candidate};
use crate::scm_host::Controls;
use crate::scm_identity::ScmEvidence;

const PID: u32 = 42;
const CONTRACT: &str =
    include_str!("../../../../../../tests/OpenBlade.Contracts.Tests/WindowsReplay/v1/service.json");
const STATUS: &str = include_str!(
    "../../../../../../tests/OpenBlade.Contracts.Tests/WindowsReplay/v1/service-status-event.json"
);

const SYSTEM: Caller = Caller {
    pid: Some(PID),
    logon: Logon::System,
    session: 0,
    control_session: None,
};
const SETUP: Caller = Caller {
    pid: Some(77),
    logon: Logon::Interactive,
    session: 3,
    control_session: None,
};

#[derive(Clone, Copy, Default, PartialEq, Eq)]
enum Fault {
    #[default]
    None,
    FirstListener,
    Acl,
    RemoteListener,
    WrongName,
    WrongPeer,
    PeerChanged,
    WrongCaller,
    InvalidStatus,
    SourceUnavailable,
    SessionUnavailable,
    ProbeStalled,
    ListenerLost,
    ListenerDrain,
    RuntimeDrain,
    DrainStalled,
}

struct TestStream {
    pipe: DuplexStream,
    pid_reads: Cell<usize>,
    read: usize,
    caller: Caller,
    control_session: ControlSession,
    fault: Fault,
    peer_live: Rc<Cell<bool>>,
    request_read: Cell<Option<oneshot::Sender<()>>>,
}

impl AsyncRead for TestStream {
    fn poll_read(
        mut self: Pin<&mut Self>,
        cx: &mut Context<'_>,
        buf: &mut ReadBuf<'_>,
    ) -> Poll<io::Result<()>> {
        let before = buf.filled().len();
        let result = Pin::new(&mut self.pipe).poll_read(cx, buf);
        self.read += buf.filled().len() - before;
        result
    }
}

impl AsyncWrite for TestStream {
    fn poll_write(
        mut self: Pin<&mut Self>,
        cx: &mut Context<'_>,
        data: &[u8],
    ) -> Poll<io::Result<usize>> {
        Pin::new(&mut self.pipe).poll_write(cx, data)
    }

    fn poll_flush(mut self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<io::Result<()>> {
        Pin::new(&mut self.pipe).poll_flush(cx)
    }

    fn poll_shutdown(mut self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<io::Result<()>> {
        Pin::new(&mut self.pipe).poll_shutdown(cx)
    }
}

impl Stream for TestStream {
    #[cfg(feature = "service-thermal-mutation-v1")]
    fn user_sid_after_read(&self) -> io::Result<String> {
        self.caller_after_read()?;
        Ok("S-1-5-21-111-222-333-1001".into())
    }
    /// A scripted caller is SYSTEM by its logon, and never an administrator.
    #[cfg(feature = "service-lifecycle-shutdown-v1")]
    fn system_or_administrator_after_read(&self) -> bool {
        windows::system_or_administrator(
            self.caller_after_read()
                .map(|caller| caller.logon == Logon::System),
            || Ok(false),
        )
    }
    /// One poll that registers no wake: an end of stream or an error is a
    /// closed peer. A single-request connection keeps no later bytes, so a
    /// byte it consumes is one the exchange would refuse anyway.
    #[cfg(feature = "service-lifecycle-shutdown-v1")]
    fn peer_closed(&mut self) -> bool {
        let mut byte = [0];
        let mut buf = ReadBuf::new(&mut byte);
        let mut cx = Context::from_waker(std::task::Waker::noop());
        match Pin::new(&mut self.pipe).poll_read(&mut cx, &mut buf) {
            Poll::Ready(Ok(())) => buf.filled().is_empty(),
            Poll::Ready(Err(_)) => true,
            Poll::Pending => false,
        }
    }
    fn server_pid(&self) -> io::Result<u32> {
        let reads = self.pid_reads.get();
        self.pid_reads.set(reads + 1);
        if self.fault == Fault::WrongPeer || (self.fault == Fault::PeerChanged && reads > 0) {
            return Ok(PID + 1);
        }
        assert_eq!(self.read > 0, reads > 0);
        Ok(PID)
    }

    fn control_session(&self) -> io::Result<Option<u32>> {
        if self.fault == Fault::SessionUnavailable {
            return Err(io::Error::other("session authority unavailable"));
        }
        assert!(
            self.read > 4,
            "session authority was queried before reading"
        );
        (self.control_session)()
    }

    fn caller_after_read(&self) -> io::Result<Caller> {
        assert!(
            self.read > 4,
            "client was resolved before its frame was read"
        );
        if !self.peer_live.get() {
            return Err(denied("scripted peer lost"));
        }
        if let Some(read) = self.request_read.take() {
            let _ = read.send(());
        }
        Ok(self.caller)
    }
}

fn test_stream(pipe: DuplexStream, caller: Caller, fault: Fault) -> Box<dyn Stream> {
    test_stream_with_authority(
        pipe,
        caller,
        fault,
        Rc::new(move || Ok(caller.control_session)),
    )
}

fn test_stream_with_authority(
    pipe: DuplexStream,
    caller: Caller,
    fault: Fault,
    control_session: ControlSession,
) -> Box<dyn Stream> {
    Box::new(TestStream {
        pipe,
        pid_reads: Cell::new(0),
        read: 0,
        caller,
        control_session,
        fault,
        peer_live: Rc::new(Cell::new(true)),
        request_read: Cell::new(None),
    })
}

type Events = Rc<RefCell<Vec<&'static str>>>;

struct TestEndpoint {
    fault: Fault,
    probe: RefCell<Option<DuplexStream>>,
    inbound: tokio::sync::Mutex<mpsc::UnboundedReceiver<Box<dyn Stream>>>,
    events: Events,
}

impl Endpoint for TestEndpoint {
    fn listener(&self) -> io::Result<Listener> {
        Ok(Listener {
            pipe_name: if self.fault == Fault::WrongName {
                "private.test"
            } else {
                PIPE_NAME
            },
            first_instance: self.fault != Fault::FirstListener,
            managed_acl: self.fault != Fault::Acl,
            local_only: self.fault != Fault::RemoteListener,
        })
    }

    fn accept(&self) -> Pending<'_, Box<dyn Stream>> {
        Box::pin(async move {
            self.inbound
                .lock()
                .await
                .recv()
                .await
                .ok_or_else(|| io::Error::new(io::ErrorKind::BrokenPipe, "listener lost"))
        })
    }

    fn connect_probe(&self) -> Pending<'_, Box<dyn Stream>> {
        Box::pin(async move {
            self.events.borrow_mut().push("connect");
            if self.fault == Fault::ProbeStalled {
                return pending().await;
            }
            Ok(test_stream(
                self.probe.borrow_mut().take().unwrap(),
                SYSTEM,
                self.fault,
            ))
        })
    }

    fn retire(&mut self) -> Pending<'_, ()> {
        Box::pin(async move {
            self.events.borrow_mut().push("close");
            self.inbound.get_mut().close();
            while self.inbound.get_mut().try_recv().is_ok() {}
            self.probe.get_mut().take();
            match self.fault {
                Fault::ListenerDrain => Err(io::Error::other("listener drain failed")),
                Fault::DrainStalled => pending().await,
                _ => Ok(()),
            }
        })
    }
}

struct TestSource {
    fault: Fault,
    events: Events,
    publications: Option<watch::Receiver<Arc<Publication>>>,
}

impl StatusSource for TestSource {
    fn snapshot(&mut self, caller: Caller, _requested: Instant) -> Pending<'_, Snapshot> {
        Box::pin(async move {
            self.events.borrow_mut().push("snapshot");
            if self.fault == Fault::SourceUnavailable {
                return Err(io::Error::other("source unavailable"));
            }
            let mut snapshot = managed_status();
            snapshot["controlAccess"] = json!(caller.access()?);
            if self.fault == Fault::InvalidStatus {
                snapshot["revision"] = json!(-1);
            }
            Ok(serde_json::from_value(snapshot)?)
        })
    }

    fn drain(&mut self) -> Pending<'_, ()> {
        Box::pin(async move {
            self.events.borrow_mut().push("drain");
            if self.fault == Fault::RuntimeDrain {
                return Err(io::Error::other("runtime drain failed"));
            }
            Ok(())
        })
    }

    fn publications(&self) -> Option<watch::Receiver<Arc<Publication>>> {
        self.publications.clone()
    }
}

fn managed_status() -> Value {
    serde_json::from_str::<Value>(STATUS).unwrap()["status"].clone()
}

fn ports(fault: Fault, events: &Events) -> (Ports, mpsc::UnboundedSender<Box<dyn Stream>>) {
    ports_with_feed(fault, events, None)
}

fn ports_with_feed(
    fault: Fault,
    events: &Events,
    publications: Option<watch::Receiver<Arc<Publication>>>,
) -> (Ports, mpsc::UnboundedSender<Box<dyn Stream>>) {
    let (client, server) = duplex(128);
    let (tx, rx) = mpsc::unbounded_channel();
    let mut caller = SYSTEM;
    if fault == Fault::WrongCaller {
        caller.pid = Some(PID + 1);
    }
    tx.send(test_stream(server, caller, fault)).unwrap();
    (
        Ports {
            endpoint: Box::new(TestEndpoint {
                fault,
                probe: RefCell::new(Some(client)),
                inbound: tokio::sync::Mutex::new(rx),
                events: events.clone(),
            }),
            source: Rc::new(Mutex::new(Box::new(TestSource {
                fault,
                events: events.clone(),
                publications,
            }))),
        },
        tx,
    )
}

async fn lifecycle(
    controls: &Controls,
    ports: io::Result<Ports>,
    events: &Events,
    action: impl FnOnce(),
) -> io::Result<()> {
    lifecycle_reporting(controls, ports, events, action, None).await
}

async fn lifecycle_reporting(
    controls: &Controls,
    ports: io::Result<Ports>,
    events: &Events,
    action: impl FnOnce(),
    report_failure: Option<&str>,
) -> io::Result<()> {
    let mut action = Some(action);
    run_candidate(
        controls,
        controls.arm(),
        || {
            Ok(ProcessEvidence {
                has_arguments: false,
                selected_image: None,
                program_files: r"C:\Program Files".into(),
                image: std::path::Path::new(r"C:\Program Files")
                    .join("OpenBlade")
                    .join(IMAGE_NAME),
                pid: PID,
                local_system: true,
                session: 0,
            })
        },
        |_| {
            Ok(ScmEvidence {
                process_id: PID,
                service_type: SERVICE_WIN32_OWN_PROCESS,
                state: SERVICE_START_PENDING,
                matches_config: true,
            })
        },
        |_| Ok(()),
        async |launch, (), shutdown, failed| prepare(launch.pid, shutdown, failed, ports).await,
        |status| {
            let event = match (status.dwCurrentState, status.dwWin32ExitCode) {
                (SERVICE_START_PENDING, NO_ERROR) => "starting",
                (SERVICE_RUNNING, NO_ERROR) => "running",
                (SERVICE_STOP_PENDING, NO_ERROR) => "stopping",
                (SERVICE_STOPPED, NO_ERROR) => "stopped",
                (SERVICE_STOPPED, ERROR_GEN_FAILURE) => "failed",
                _ => panic!("unexpected SCM status"),
            };
            events.borrow_mut().push(event);
            if event == "running" {
                action.take().unwrap()();
            }
            if report_failure == Some(event) {
                return Err(io::Error::other("SCM report failed"));
            }
            Ok(())
        },
    )
    .await
}

#[tokio::test(start_paused = true)]
async fn unproven_listener_peer_source_or_timed_out_probe_never_runs() {
    for fault in [
        Fault::FirstListener,
        Fault::Acl,
        Fault::RemoteListener,
        Fault::WrongName,
        Fault::WrongPeer,
        Fault::PeerChanged,
        Fault::WrongCaller,
        Fault::InvalidStatus,
        Fault::SourceUnavailable,
        Fault::ProbeStalled,
    ] {
        let events = Events::default();
        let (ports, _tx) = ports(fault, &events);
        let controls = Controls::new();
        assert!(
            lifecycle(&controls, Ok(ports), &events, || panic!(
                "unexpected Running"
            ))
            .await
            .is_err()
        );
        let events = events.borrow();
        assert_eq!(events[0], "starting");
        assert_eq!(events[events.len() - 3..], ["close", "drain", "failed"]);
        if !matches!(
            fault,
            Fault::InvalidStatus | Fault::SourceUnavailable | Fault::PeerChanged
        ) {
            assert!(!events.contains(&"snapshot"));
        }
    }

    let events = Events::default();
    let controls = Controls::new();
    let ports = Err(io::Error::new(
        io::ErrorKind::Unsupported,
        "ports unavailable",
    ));
    let error = lifecycle(&controls, ports, &events, || panic!("unexpected Running"))
        .await
        .unwrap_err();
    assert_eq!(error.kind(), io::ErrorKind::Unsupported);
    assert_eq!(*events.borrow(), ["starting", "failed"]);
}

#[tokio::test(start_paused = true)]
async fn stop_cancels_a_pending_probe_and_waits_for_retirement() {
    let controls = Controls::new();
    let events = Events::default();
    let (ports, _tx) = ports(Fault::ProbeStalled, &events);
    let stop = async {
        while !events.borrow().contains(&"connect") {
            tokio::task::yield_now().await;
        }
        controls.request_stop();
    };
    let (result, ()) = tokio::join!(
        lifecycle(&controls, Ok(ports), &events, || panic!(
            "unexpected Running"
        )),
        stop
    );
    result.unwrap();
    assert_eq!(
        *events.borrow(),
        ["starting", "connect", "close", "drain", "stopped"]
    );
}

#[tokio::test(start_paused = true)]
async fn listener_loss_or_unconfirmed_drain_fails_stop() {
    for fault in [
        Fault::ListenerLost,
        Fault::ListenerDrain,
        Fault::RuntimeDrain,
        Fault::DrainStalled,
    ] {
        let controls = Controls::new();
        let events = Events::default();
        let (ports, tx) = ports(fault, &events);
        let mut tx = Some(tx);
        let action = || {
            if fault == Fault::ListenerLost {
                drop(tx.take());
            } else {
                controls.request_stop();
            }
        };
        let error = lifecycle(&controls, Ok(ports), &events, action)
            .await
            .unwrap_err();
        match fault {
            Fault::ListenerLost => assert_eq!(error.to_string(), "listener lost"),
            Fault::ListenerDrain => assert_eq!(error.to_string(), "listener drain failed"),
            Fault::RuntimeDrain => assert_eq!(error.to_string(), "runtime drain failed"),
            Fault::DrainStalled => assert_eq!(error.kind(), io::ErrorKind::TimedOut),
            _ => unreachable!(),
        }
        assert_eq!(
            *events.borrow(),
            [
                "starting", "connect", "snapshot", "running", "stopping", "close", "drain",
                "failed"
            ]
        );
    }
}

#[tokio::test(start_paused = true)]
async fn readiness_serves_managed_setup_then_closes_partial_exchange_on_stop() {
    let controls = Controls::new();
    let events = Events::default();
    let (ports, tx) = ports(Fault::None, &events);
    let (running, ready) = oneshot::channel();
    let clients = async {
        ready.await.unwrap();
        let contract: Value = serde_json::from_str(CONTRACT).unwrap();
        let input = &contract["requests"][0]["input"];
        assert_eq!(input["operation"], "getStatus");
        assert_eq!(input["version"], 91);

        let console = Rc::new(Cell::new(u32::MAX));
        let reads = Rc::new(Cell::new(0));
        let authority: ControlSession = {
            let console = Rc::clone(&console);
            let reads = Rc::clone(&reads);
            Rc::new(move || {
                reads.set(reads.get() + 1);
                Ok(crate::console_session::from_windows(console.get()))
            })
        };
        for (caller, console_id, expected_access) in [
            (
                Caller {
                    logon: Logon::Other,
                    ..SETUP
                },
                SETUP.session,
                None,
            ),
            (
                Caller {
                    logon: Logon::RemoteInteractive,
                    control_session: Some(SETUP.session),
                    ..SETUP
                },
                SETUP.session,
                Some(1),
            ),
            (SYSTEM, u32::MAX, Some(0)),
            (SETUP, SETUP.session, Some(0)),
            (SETUP, SETUP.session + 1, Some(1)),
            (SETUP, u32::MAX, Some(1)),
            (SETUP, SETUP.session, Some(0)),
        ] {
            console.set(console_id);
            let reads_before = reads.get();
            let (mut client, server) = duplex(128);
            tx.send(test_stream_with_authority(
                server,
                caller,
                Fault::None,
                Rc::clone(&authority),
            ))
            .unwrap();
            write_frame(&mut client, input).await.unwrap();
            let response = read_payload(&mut client).await;
            // Thermal compositions re-admit the peer, including its control session,
            // before projecting caller-private status (725ddcf3).
            let per_read = if cfg!(feature = "service-thermal-mutation-v1") {
                2
            } else {
                1
            };
            assert_eq!(
                reads.get() - reads_before,
                per_read * usize::from(caller.logon == Logon::Interactive)
            );
            let Some(expected_access) = expected_access else {
                assert_eq!(response.unwrap_err().kind(), io::ErrorKind::UnexpectedEof);
                continue;
            };
            let mut expected_status = managed_status();
            expected_status["controlAccess"] = json!(expected_access);
            assert_eq!(
                serde_json::from_slice::<Value>(&response.unwrap()).unwrap(),
                json!({
                    "frame": "response", "version": 91, "requestId": input["requestId"],
                    "success": true, "error": null, "status": expected_status,
                    "result": null, "archive": null, "errorCode": null,
                })
            );
        }

        let (mut client, server) = duplex(128);
        tx.send(test_stream(server, SETUP, Fault::None)).unwrap();
        client.write_all(&[10]).await.unwrap();
        tokio::task::yield_now().await;
        controls.request_stop();
        assert_eq!(
            read_payload(&mut client).await.unwrap_err().kind(),
            io::ErrorKind::UnexpectedEof
        );
    };
    let (result, ()) = tokio::join!(
        lifecycle(&controls, Ok(ports), &events, || running.send(()).unwrap()),
        clients
    );
    result.unwrap();
    assert_eq!(
        *events.borrow(),
        [
            "starting", "connect", "snapshot", "running", "snapshot", "snapshot", "snapshot",
            "snapshot", "snapshot", "snapshot", "stopping", "close", "drain", "stopped"
        ]
    );
}

#[test]
fn closed_get_status_contract_rejects_mutation_duplicates_and_uncorrelated_responses() {
    let id = "11111111-2222-4333-8444-555555555555";
    let contract: Value = serde_json::from_str(CONTRACT).unwrap();
    let input = &contract["requests"][0]["input"];
    let request = StatusRequest::decode_get(&serde_json::to_vec(input).unwrap()).unwrap();
    assert!(request.matches(&StatusRequest::get(id.to_owned()).unwrap()));
    for rejected in [
        json!({"operation": "shutdownOpenBlade", "version": 91, "requestId": id}).to_string(),
        json!({"operation": "subscribeStatus", "version": 91, "requestId": id}).to_string(),
        json!({"operation": "getStatus", "version": 51, "requestId": id}).to_string(),
        json!({"operation": "getStatus", "version": 91, "requestId": "not-a-guid"}).to_string(),
        json!({"operation": "getStatus", "version": 91, "requestId": id, "value": null})
            .to_string(),
        format!(r#"{{"operation":"getStatus","version":91,"version":91,"requestId":"{id}"}}"#),
    ] {
        assert!(StatusRequest::decode_get(rejected.as_bytes()).is_err());
    }

    let response = json!({
        "frame": "response", "version": 91, "requestId": id, "success": true,
        "status": managed_status(), "error": null, "result": null, "archive": null,
    });
    let check =
        |reply: &Value| StatusResponse::verify(&serde_json::to_vec(reply).unwrap(), &request);
    check(&response).unwrap();
    for (key, value) in [
        ("frame", json!("statusEvent")),
        ("version", json!(51)),
        ("requestId", json!("00000000-0000-0000-0000-000000000001")),
        ("success", json!(false)),
        ("status", Value::Null),
        ("status", json!({"revision": 1})),
        ("status", json!({})),
        ("errorCode", json!(0)),
        ("archive", json!({})),
    ] {
        let mut rejected = response.clone();
        rejected[key] = value;
        assert!(check(&rejected).is_err());
    }
}

#[tokio::test]
async fn session_authority_failure_is_runtime_failure_before_status_read() {
    let events = Events::default();
    let mut source = TestSource {
        fault: Fault::None,
        events: events.clone(),
        publications: None,
    };
    let (server, mut client) = duplex(1024);
    let stream = test_stream(server, SETUP, Fault::SessionUnavailable);
    write_frame(
        &mut client,
        &StatusRequest::get(probe_id().unwrap()).unwrap(),
    )
    .await
    .unwrap();
    let result = serve(&mut source, std::future::ready(Ok(stream)), None).await;
    assert!(matches!(result, Err(ExchangeError::Runtime(error))
        if error.to_string() == "session authority unavailable"));
    assert!(events.borrow().is_empty());
}

#[path = "subscription_tests.rs"]
mod subscription_tests;

struct LifetimeSource {
    inner: TestSource,
    state: Rc<crate::loaded_origin::lifetime::Lifetime>,
    ack_failure: bool,
}
impl StatusSource for LifetimeSource {
    fn snapshot(&mut self, caller: Caller, at: Instant) -> Pending<'_, Snapshot> {
        self.inner.snapshot(caller, at)
    }
    fn verify_origin(&self) -> io::Result<()> {
        self.state.verify()
    }
    fn drain(&mut self) -> Pending<'_, ()> {
        self.inner.drain()
    }
    fn confirm_drained(&self) -> io::Result<()> {
        self.inner.events.borrow_mut().push("ack");
        if self.ack_failure {
            Err(io::Error::other("ack failed"))
        } else {
            Ok(())
        }
    }
}
async fn lifetime_ports(
    fault: Fault,
    events: &Events,
    state: Rc<crate::loaded_origin::lifetime::Lifetime>,
    ack_failure: bool,
) -> (Ports, mpsc::UnboundedSender<Box<dyn Stream>>) {
    let (ports, tx) = ports(fault, events);
    *ports.source.lock().await = Box::new(LifetimeSource {
        inner: TestSource {
            fault,
            events: events.clone(),
            publications: None,
        },
        state,
        ack_failure,
    });
    (ports, tx)
}

#[tokio::test(start_paused = true)]
async fn running_report_stop_before_first_drive_tick_is_expected_only_after_real_shutdown() {
    let controls = Controls::new();
    let events = Events::default();
    let state = Rc::new(crate::loaded_origin::lifetime::Lifetime::default());
    let (ports, _tx) = lifetime_ports(Fault::None, &events, state.clone(), false).await;
    lifecycle(&controls, Ok(ports), &events, || {
        state.stop();
        controls.request_stop();
    })
    .await
    .unwrap();
    assert_eq!(
        *events.borrow(),
        [
            "starting", "connect", "snapshot", "running", "stopping", "close", "drain", "ack",
            "stopped"
        ]
    );
}

#[tokio::test(start_paused = true)]
async fn stop_ingress_one_poll_before_channel_delivery_waits_for_shutdown_and_ack() {
    let events = Events::default();
    let state = Rc::new(crate::loaded_origin::lifetime::Lifetime::default());
    let (ports, _tx) = lifetime_ports(Fault::None, &events, state.clone(), false).await;
    let (shutdown, receiver) = oneshot::channel();
    let (failed, notified) = oneshot::channel();
    state.stop();
    let drive = ports.drive(receiver, failed);
    tokio::pin!(drive);
    tokio::select! { biased; result = &mut drive => panic!("STOP needs real shutdown: {result:?}"), _ = tokio::task::yield_now() => {} }
    notified.await.unwrap();
    assert!(events.borrow().is_empty());
    shutdown.send(()).unwrap();
    drive.await.unwrap();
    assert_eq!(*events.borrow(), ["close", "drain", "ack"]);
}

#[tokio::test(start_paused = true)]
async fn typed_stop_never_masks_trust_loss_cleanup_or_ack_failure() {
    for (fault, trust_lost, ack_failure, message) in [
        (Fault::None, true, false, "loaded origin trust was lost"),
        (Fault::ListenerDrain, false, false, "listener drain failed"),
        (Fault::RuntimeDrain, false, false, "runtime drain failed"),
        (Fault::None, false, true, "ack failed"),
    ] {
        let controls = Controls::new();
        let events = Events::default();
        let state = Rc::new(crate::loaded_origin::lifetime::Lifetime::default());
        let (ports, _tx) = lifetime_ports(fault, &events, state.clone(), ack_failure).await;
        let error = lifecycle(&controls, Ok(ports), &events, || {
            if trust_lost {
                state.lose_trust();
            }
            state.stop();
            controls.request_stop();
        })
        .await
        .unwrap_err();
        assert_eq!(error.to_string(), message);
        assert_eq!(events.borrow().last(), Some(&"failed"));
        let events = events.borrow();
        assert!(
            events.iter().position(|e| *e == "close").unwrap()
                < events.iter().position(|e| *e == "drain").unwrap()
        );
    }
}

#[tokio::test(start_paused = true)]
async fn expected_stop_with_lost_shutdown_is_failure_and_still_drains() {
    let events = Events::default();
    let state = Rc::new(crate::loaded_origin::lifetime::Lifetime::default());
    let (ports, _tx) = lifetime_ports(Fault::None, &events, state.clone(), false).await;
    let (shutdown, receiver) = oneshot::channel();
    let (failed, _notified) = oneshot::channel();
    state.stop();
    drop(shutdown);
    let error = ports.drive(receiver, failed).await.unwrap_err();
    assert_eq!(error.kind(), io::ErrorKind::BrokenPipe);
    assert_eq!(*events.borrow(), ["close", "drain", "ack"]);
}

#[tokio::test(start_paused = true)]
async fn typed_stop_does_not_hide_real_scm_report_failures() {
    for phase in ["running", "stopping", "stopped"] {
        let controls = Controls::new();
        let events = Events::default();
        let state = Rc::new(crate::loaded_origin::lifetime::Lifetime::default());
        let (ports, _tx) = lifetime_ports(Fault::None, &events, state.clone(), false).await;
        let error = lifecycle_reporting(
            &controls,
            Ok(ports),
            &events,
            || {
                state.stop();
                controls.request_stop();
            },
            Some(phase),
        )
        .await
        .unwrap_err();
        assert_eq!(error.to_string(), "SCM report failed");
        let events = events.borrow();
        assert!(
            events.iter().position(|e| *e == "close").unwrap()
                < events.iter().position(|e| *e == "drain").unwrap()
        );
        assert!(events.contains(&"ack"));
    }
}

#[cfg(feature = "service-thermal-mutation-v1")]
#[path = "readiness_tests/mutation.rs"]
mod mutation;

#[cfg(feature = "service-thermal-mutation-v1")]
#[path = "readiness_tests/private_mutation.rs"]
mod private_mutation;

struct CountingSource {
    inner: TestSource,
    origin_checks: Rc<Cell<usize>>,
    power_checks: Rc<Cell<usize>>,
}
impl StatusSource for CountingSource {
    fn snapshot(&mut self, caller: Caller, at: Instant) -> Pending<'_, Snapshot> {
        self.inner.snapshot(caller, at)
    }
    fn verify_origin(&self) -> io::Result<()> {
        self.origin_checks.set(self.origin_checks.get() + 1);
        Ok(())
    }
    fn process_power_source(&mut self) -> Pending<'_, ()> {
        self.power_checks.set(self.power_checks.get() + 1);
        Box::pin(async { Ok(()) })
    }
    fn drain(&mut self) -> Pending<'_, ()> {
        self.inner.drain()
    }
}

#[tokio::test(start_paused = true)]
async fn a_suspend_is_followed_by_one_origin_and_power_check_not_a_burst() {
    let events = Events::default();
    let (ports, _tx) = ports(Fault::None, &events);
    let origin_checks = Rc::new(Cell::new(0));
    let power_checks = Rc::new(Cell::new(0));
    *ports.source.lock().await = Box::new(CountingSource {
        inner: TestSource {
            fault: Fault::None,
            events: events.clone(),
            publications: None,
        },
        origin_checks: origin_checks.clone(),
        power_checks: power_checks.clone(),
    });
    let (shutdown, receiver) = oneshot::channel();
    let (failed, _notified) = oneshot::channel();
    let drive = ports.drive(receiver, failed);
    tokio::pin!(drive);
    let settle = || async {
        for _ in 0..5_000 {
            tokio::task::yield_now().await;
        }
    };
    let checks = || (origin_checks.get(), power_checks.get());
    let script = async {
        settle().await;
        assert_eq!(checks(), (1, 1), "the first tick checks once");
        // A suspend is time that passes with no runtime turn; the paused
        // clock models it as one jump.
        for jump in [Duration::from_secs(600), Duration::from_secs(8 * 3600)] {
            let (origin_before, power_before) = checks();
            tokio::time::advance(jump).await;
            settle().await;
            let (origin, power) = checks();
            assert!(
                (1..=2).contains(&(origin - origin_before)),
                "origin checks after {jump:?}: {}",
                origin - origin_before
            );
            assert!(
                (1..=2).contains(&(power - power_before)),
                "power checks after {jump:?}: {}",
                power - power_before
            );
        }
        shutdown.send(()).unwrap();
    };
    tokio::select! {
        biased;
        result = &mut drive => panic!("drive ended early: {result:?}"),
        () = script => {}
    }
    drive.await.unwrap();
}

#[cfg(feature = "service-profile-triggers-v1")]
type SharedSource = Rc<Mutex<Box<dyn StatusSource>>>;
/// The serving loop's origin tick runs the lifecycle reconciliation after
/// its power check, awaits it with the source lock released, and runs it once
/// per tick.
#[cfg(feature = "service-profile-triggers-v1")]
struct LifecycleSource {
    inner: CountingSource,
    // The serving loop's source, set once the loop's ports exist.
    source: Rc<RefCell<Option<SharedSource>>>,
    reconciled: Rc<Cell<usize>>,
    unlocked: Rc<Cell<usize>>,
}
#[cfg(feature = "service-profile-triggers-v1")]
impl StatusSource for LifecycleSource {
    fn snapshot(&mut self, caller: Caller, at: Instant) -> Pending<'_, Snapshot> {
        self.inner.snapshot(caller, at)
    }
    fn verify_origin(&self) -> io::Result<()> {
        self.inner.verify_origin()
    }
    fn process_power_source(&mut self) -> Pending<'_, ()> {
        self.inner.process_power_source()
    }
    fn drain(&mut self) -> Pending<'_, ()> {
        self.inner.drain()
    }
    fn reconcile_lifecycle(&mut self) -> Option<Reconciling> {
        let power = self.inner.power_checks.get();
        let source = self.source.borrow().clone();
        let reconciled = self.reconciled.clone();
        let unlocked = self.unlocked.clone();
        Some(Box::pin(async move {
            // Created after this tick's power check.
            assert_eq!(power, reconciled.get() + 1);
            if source.is_some_and(|source| source.try_lock().is_ok()) {
                unlocked.set(unlocked.get() + 1);
            }
            reconciled.set(reconciled.get() + 1);
            Ok(())
        }))
    }
}

#[cfg(feature = "service-profile-triggers-v1")]
#[tokio::test(start_paused = true)]
async fn a_serving_loop_tick_runs_the_lifecycle_reconciliation() {
    let events = Events::default();
    let (ports, _tx) = ports(Fault::None, &events);
    let origin_checks = Rc::new(Cell::new(0));
    let power_checks = Rc::new(Cell::new(0));
    let reconciled = Rc::new(Cell::new(0));
    let unlocked = Rc::new(Cell::new(0));
    let source = Rc::new(RefCell::new(None));
    *ports.source.lock().await = Box::new(LifecycleSource {
        inner: CountingSource {
            inner: TestSource {
                fault: Fault::None,
                events: events.clone(),
                publications: None,
            },
            origin_checks: origin_checks.clone(),
            power_checks: power_checks.clone(),
        },
        source: source.clone(),
        reconciled: reconciled.clone(),
        unlocked: unlocked.clone(),
    });
    *source.borrow_mut() = Some(ports.source.clone());
    let (shutdown, receiver) = oneshot::channel();
    let (failed, _notified) = oneshot::channel();
    let drive = ports.drive(receiver, failed);
    tokio::pin!(drive);
    let settle = || async {
        for _ in 0..5_000 {
            tokio::task::yield_now().await;
        }
    };
    let script = async {
        settle().await;
        assert_eq!((power_checks.get(), reconciled.get()), (1, 1));
        for ticks in 2..=4 {
            tokio::time::advance(Duration::from_millis(500)).await;
            settle().await;
            assert_eq!((power_checks.get(), reconciled.get()), (ticks, ticks));
        }
        assert_eq!(unlocked.get(), reconciled.get());
        shutdown.send(()).unwrap();
    };
    tokio::select! {
        biased;
        result = &mut drive => panic!("drive ended early: {result:?}"),
        () = script => {}
    }
    drive.await.unwrap();
}

/// The lifecycle source with a keyboard Driver-mode step that records where
/// the tick runs it.
#[cfg(all(
    feature = "service-keyboard-driver-mode-v1",
    feature = "service-profile-triggers-v1"
))]
struct DriverModeSource {
    inner: LifecycleSource,
    maintained: Rc<Cell<usize>>,
    unlocked: Rc<Cell<usize>>,
}
#[cfg(all(
    feature = "service-keyboard-driver-mode-v1",
    feature = "service-profile-triggers-v1"
))]
impl StatusSource for DriverModeSource {
    fn snapshot(&mut self, caller: Caller, at: Instant) -> Pending<'_, Snapshot> {
        self.inner.snapshot(caller, at)
    }
    fn verify_origin(&self) -> io::Result<()> {
        self.inner.verify_origin()
    }
    fn process_power_source(&mut self) -> Pending<'_, ()> {
        self.inner.process_power_source()
    }
    fn drain(&mut self) -> Pending<'_, ()> {
        self.inner.drain()
    }
    fn reconcile_lifecycle(&mut self) -> Option<Reconciling> {
        // This tick's keyboard step already ran.
        assert_eq!(self.maintained.get(), self.inner.reconciled.get() + 1);
        self.inner.reconcile_lifecycle()
    }
    fn maintain_driver_mode(&mut self) -> Option<Reconciling> {
        let power = self.inner.inner.power_checks.get();
        let reconciled = self.inner.reconciled.get();
        let source = self.inner.source.borrow().clone();
        let (maintained, unlocked) = (self.maintained.clone(), self.unlocked.clone());
        Some(Box::pin(async move {
            // After this tick's power check, before its reconciliation.
            assert_eq!(
                (power, reconciled),
                (maintained.get() + 1, maintained.get())
            );
            if source.is_some_and(|source| source.try_lock().is_ok()) {
                unlocked.set(unlocked.get() + 1);
            }
            maintained.set(maintained.get() + 1);
            Ok(())
        }))
    }
}

/// Managed step order on every tick: the power-source classification, the
/// keyboard Driver-mode step, then the lifecycle reconciliation. The step is
/// awaited with the source lock released.
#[cfg(all(
    feature = "service-keyboard-driver-mode-v1",
    feature = "service-profile-triggers-v1"
))]
#[tokio::test(start_paused = true)]
async fn a_serving_loop_tick_runs_the_driver_mode_step_after_the_power_check() {
    let events = Events::default();
    let (ports, _tx) = ports(Fault::None, &events);
    let power_checks = Rc::new(Cell::new(0));
    let reconciled = Rc::new(Cell::new(0));
    let maintained = Rc::new(Cell::new(0));
    let unlocked = Rc::new(Cell::new(0));
    let source = Rc::new(RefCell::new(None));
    *ports.source.lock().await = Box::new(DriverModeSource {
        inner: LifecycleSource {
            inner: CountingSource {
                inner: TestSource {
                    fault: Fault::None,
                    events: events.clone(),
                    publications: None,
                },
                origin_checks: Rc::default(),
                power_checks: power_checks.clone(),
            },
            source: source.clone(),
            reconciled: reconciled.clone(),
            unlocked: Rc::default(),
        },
        maintained: maintained.clone(),
        unlocked: unlocked.clone(),
    });
    *source.borrow_mut() = Some(ports.source.clone());
    let (shutdown, receiver) = oneshot::channel();
    let (failed, _notified) = oneshot::channel();
    let drive = ports.drive(receiver, failed);
    tokio::pin!(drive);
    let settle = || async {
        for _ in 0..5_000 {
            tokio::task::yield_now().await;
        }
    };
    let script = async {
        settle().await;
        for ticks in 1..=4 {
            if ticks > 1 {
                tokio::time::advance(Duration::from_millis(500)).await;
                settle().await;
            }
            assert_eq!(
                (power_checks.get(), maintained.get(), reconciled.get()),
                (ticks, ticks, ticks)
            );
        }
        assert_eq!(unlocked.get(), maintained.get());
        shutdown.send(()).unwrap();
    };
    tokio::select! {
        biased;
        result = &mut drive => panic!("drive ended early: {result:?}"),
        () = script => {}
    }
    drive.await.unwrap();
}

/// The serving loop's shared source, set once the ports exist.
#[cfg(feature = "service-keyboard-driver-mode-v1")]
type LoopSource = Rc<RefCell<Option<Rc<Mutex<Box<dyn StatusSource>>>>>>;

/// The counting source with a keyboard Driver-mode step, for the ownership
/// build without lifecycle triggers.
#[cfg(feature = "service-keyboard-driver-mode-v1")]
struct KeyboardTickSource {
    inner: CountingSource,
    source: LoopSource,
    maintained: Rc<Cell<usize>>,
    unlocked: Rc<Cell<usize>>,
}
#[cfg(feature = "service-keyboard-driver-mode-v1")]
impl StatusSource for KeyboardTickSource {
    fn snapshot(&mut self, caller: Caller, at: Instant) -> Pending<'_, Snapshot> {
        self.inner.snapshot(caller, at)
    }
    fn verify_origin(&self) -> io::Result<()> {
        self.inner.verify_origin()
    }
    fn process_power_source(&mut self) -> Pending<'_, ()> {
        self.inner.process_power_source()
    }
    fn drain(&mut self) -> Pending<'_, ()> {
        self.inner.drain()
    }
    fn maintain_driver_mode(&mut self) -> Option<Reconciling> {
        let power = self.inner.power_checks.get();
        let source = self.source.borrow().clone();
        let (maintained, unlocked) = (self.maintained.clone(), self.unlocked.clone());
        Some(Box::pin(async move {
            // After this tick's power check.
            assert_eq!(power, maintained.get() + 1);
            if source.is_some_and(|source| source.try_lock().is_ok()) {
                unlocked.set(unlocked.get() + 1);
            }
            maintained.set(maintained.get() + 1);
            Ok(())
        }))
    }
}

/// Every serving-loop tick of the ownership build runs the keyboard
/// Driver-mode step after its power check, with the source lock released.
#[cfg(feature = "service-keyboard-driver-mode-v1")]
#[tokio::test(start_paused = true)]
async fn a_serving_loop_tick_runs_the_keyboard_driver_mode_step() {
    let events = Events::default();
    let (ports, _tx) = ports(Fault::None, &events);
    let power_checks = Rc::new(Cell::new(0));
    let maintained = Rc::new(Cell::new(0));
    let unlocked = Rc::new(Cell::new(0));
    let source = Rc::new(RefCell::new(None));
    *ports.source.lock().await = Box::new(KeyboardTickSource {
        inner: CountingSource {
            inner: TestSource {
                fault: Fault::None,
                events: events.clone(),
                publications: None,
            },
            origin_checks: Rc::default(),
            power_checks: power_checks.clone(),
        },
        source: source.clone(),
        maintained: maintained.clone(),
        unlocked: unlocked.clone(),
    });
    *source.borrow_mut() = Some(ports.source.clone());
    let (shutdown, receiver) = oneshot::channel();
    let (failed, _notified) = oneshot::channel();
    let drive = ports.drive(receiver, failed);
    tokio::pin!(drive);
    let settle = || async {
        for _ in 0..5_000 {
            tokio::task::yield_now().await;
        }
    };
    let script = async {
        settle().await;
        for ticks in 1..=4 {
            if ticks > 1 {
                tokio::time::advance(Duration::from_millis(500)).await;
                settle().await;
            }
            assert_eq!((power_checks.get(), maintained.get()), (ticks, ticks));
        }
        assert_eq!(unlocked.get(), maintained.get());
        shutdown.send(()).unwrap();
    };
    tokio::select! {
        biased;
        result = &mut drive => panic!("drive ended early: {result:?}"),
        () = script => {}
    }
    drive.await.unwrap();
}

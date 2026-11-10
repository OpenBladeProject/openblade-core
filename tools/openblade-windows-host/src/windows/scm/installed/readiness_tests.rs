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
}

impl StatusSource for TestSource {
    fn snapshot(&mut self, caller: Caller) -> Pending<'_, Snapshot> {
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
}

fn managed_status() -> Value {
    serde_json::from_str::<Value>(STATUS).unwrap()["status"].clone()
}

fn ports(fault: Fault, events: &Events) -> (Ports, mpsc::UnboundedSender<Box<dyn Stream>>) {
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
            source: Box::new(TestSource {
                fault,
                events: events.clone(),
            }),
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
    let mut action = Some(action);
    run_candidate(
        controls,
        controls.arm(),
        || {
            Ok(ProcessEvidence {
                has_arguments: false,
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
        assert_eq!(input["version"], 79);

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
            assert_eq!(
                reads.get() - reads_before,
                usize::from(caller.logon == Logon::Interactive)
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
                    "frame": "response", "version": 79, "requestId": input["requestId"],
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
fn closed_v79_get_status_contract_rejects_mutation_duplicates_and_uncorrelated_responses() {
    let id = "11111111-2222-4333-8444-555555555555";
    let contract: Value = serde_json::from_str(CONTRACT).unwrap();
    let input = &contract["requests"][0]["input"];
    let request = GetStatus::decode(&serde_json::to_vec(input).unwrap()).unwrap();
    assert!(request.matches(&GetStatus::new(id.to_owned()).unwrap()));
    for rejected in [
        json!({"operation": "shutdownOpenBlade", "version": 79, "requestId": id}).to_string(),
        json!({"operation": "getStatus", "version": 51, "requestId": id}).to_string(),
        json!({"operation": "getStatus", "version": 79, "requestId": "not-a-guid"}).to_string(),
        json!({"operation": "getStatus", "version": 79, "requestId": id, "value": null})
            .to_string(),
        format!(r#"{{"operation":"getStatus","version":79,"version":79,"requestId":"{id}"}}"#),
    ] {
        assert!(GetStatus::decode(rejected.as_bytes()).is_err());
    }

    let response = json!({
        "frame": "response", "version": 79, "requestId": id, "success": true,
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
    };
    let (server, mut client) = duplex(1024);
    let stream = test_stream(server, SETUP, Fault::SessionUnavailable);
    write_frame(&mut client, &GetStatus::new(probe_id().unwrap()).unwrap())
        .await
        .unwrap();
    let result = serve(&mut source, std::future::ready(Ok(stream)), None).await;
    assert!(matches!(result, Err(ExchangeError::Runtime(error))
        if error.to_string() == "session authority unavailable"));
    assert!(events.borrow().is_empty());
}

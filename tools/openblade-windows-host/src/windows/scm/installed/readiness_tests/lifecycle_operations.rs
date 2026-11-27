//! `shutdownOpenBlade` and `reportSessionPowerTransition` through the real
//! framed dispatcher over a scripted source: no SCM, pipe, Windows token or
//! lifecycle supplier is reached. The managed vectors recorded by
//! `ServiceSessionPowerTransitionCoordinatorTests.NativeLifecycleVectors` are
//! replayed here, and each documented deviation is asserted literally.
use std::sync::{
    Arc,
    atomic::{AtomicUsize, Ordering},
};
use std::time::Duration;

#[cfg(feature = "service-session-power-relay-v1")]
use openblade_domain::low_power_gate::{LowPowerGate, Signal, View};

use super::*;
use crate::installed::readiness::session_lifecycle::Ports;
#[cfg(feature = "service-session-power-relay-v1")]
use crate::installed::readiness::session_lifecycle::{
    ACCEPTED, INACTIVE, NEWER, Relay, Report, STALE, UNAUTHORIZED, UNAVAILABLE,
};
#[cfg(feature = "service-lifecycle-shutdown-v1")]
use crate::installed::readiness::session_lifecycle::{
    ADMINISTRATOR, Quiesced, SHUTDOWN_DELAY, SHUTTING_DOWN, ShutdownSignal,
};

const VECTORS: &str = include_str!("../../../../../tests/data/session-lifecycle-vectors.json");
/// The managed vectors' QPC clock: 1 kHz, starting at 10000.
const START: i64 = 10_000;
/// Which of the two operations this build compiles.
const SHUTDOWN: bool = cfg!(feature = "service-lifecycle-shutdown-v1");
const RELAY: bool = cfg!(feature = "service-session-power-relay-v1");

/// A caller's stream with the administrator evidence its token would carry.
struct AdminStream {
    inner: Box<dyn Stream>,
    #[cfg_attr(not(feature = "service-lifecycle-shutdown-v1"), allow(dead_code))]
    administrator: bool,
}
impl AsyncRead for AdminStream {
    fn poll_read(
        mut self: Pin<&mut Self>,
        cx: &mut Context<'_>,
        b: &mut ReadBuf<'_>,
    ) -> Poll<io::Result<()>> {
        Pin::new(&mut *self.inner).poll_read(cx, b)
    }
}
impl AsyncWrite for AdminStream {
    fn poll_write(
        mut self: Pin<&mut Self>,
        cx: &mut Context<'_>,
        b: &[u8],
    ) -> Poll<io::Result<usize>> {
        Pin::new(&mut *self.inner).poll_write(cx, b)
    }
    fn poll_flush(mut self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<io::Result<()>> {
        Pin::new(&mut *self.inner).poll_flush(cx)
    }
    fn poll_shutdown(mut self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<io::Result<()>> {
        Pin::new(&mut *self.inner).poll_shutdown(cx)
    }
}
impl Stream for AdminStream {
    fn server_pid(&self) -> io::Result<u32> {
        self.inner.server_pid()
    }
    fn caller_after_read(&self) -> io::Result<Caller> {
        self.inner.caller_after_read()
    }
    fn control_session(&self) -> io::Result<Option<u32>> {
        self.inner.control_session()
    }
    fn user_sid_after_read(&self) -> io::Result<String> {
        self.inner.user_sid_after_read()
    }
    #[cfg(feature = "service-lifecycle-shutdown-v1")]
    fn administrator_after_read(&self) -> bool {
        self.inner.caller_after_read().is_ok() && self.administrator
    }
}

/// A source with only the lifecycle ports: any other operation fails.
struct LifecycleSource {
    ports: Ports,
    // Held by a test to keep an admitted operation in flight.
    #[cfg_attr(not(feature = "service-lifecycle-shutdown-v1"), allow(dead_code))]
    gate: Rc<tokio::sync::Mutex<()>>,
}
impl StatusSource for LifecycleSource {
    #[cfg(feature = "service-lifecycle-shutdown-v1")]
    fn shutdown(&self) -> Option<(Rc<ShutdownSignal>, Quiesced)> {
        let gate = self.gate.clone();
        let signal = self.ports.shutdown.clone()?;
        Some((signal, Box::pin(async move { drop(gate.lock().await) })))
    }
    #[cfg(feature = "service-session-power-relay-v1")]
    fn session_power_relay(&self) -> Option<Rc<Relay>> {
        self.ports.relay.clone()
    }
    fn snapshot(&mut self, _: Caller, _: Instant) -> Pending<'_, Snapshot> {
        Box::pin(async { Err(io::Error::other("no status in the lifecycle source")) })
    }
    fn drain(&mut self) -> Pending<'_, ()> {
        Box::pin(async { Ok(()) })
    }
}

/// The QPC clock and gate a relay reads, as the managed vectors drive them.
/// `None` is an exhausted lifecycle.
#[cfg(feature = "service-session-power-relay-v1")]
#[derive(Clone)]
struct RelayClock {
    now: Rc<Cell<i64>>,
    gate: Rc<RefCell<Option<LowPowerGate>>>,
}
#[cfg(feature = "service-session-power-relay-v1")]
impl RelayClock {
    fn new() -> Self {
        Self {
            now: Rc::new(Cell::new(START)),
            gate: Rc::new(RefCell::new(Some(LowPowerGate::new(
                std::num::NonZeroU64::new(1000).unwrap(),
            )))),
        }
    }
    fn relay(&self) -> Relay {
        let (gate, now) = (self.gate.clone(), self.now.clone());
        Relay::new(
            move || gate.borrow().as_ref().map(LowPowerGate::view),
            move |timestamp| {
                let elapsed = u64::try_from(now.get().checked_sub(timestamp)?).ok()?;
                Some(Duration::from_millis(elapsed))
            },
        )
    }
    fn scm(&self, signal: Signal) {
        if let Some(gate) = self.gate.borrow_mut().as_mut() {
            let _ = gate.apply(signal, self.now.get());
        }
    }
}

struct Fixture {
    source: Rc<Mutex<Box<dyn StatusSource>>>,
    #[cfg_attr(not(feature = "service-lifecycle-shutdown-v1"), allow(dead_code))]
    gate: Rc<tokio::sync::Mutex<()>>,
    stops: Arc<AtomicUsize>,
    #[cfg(feature = "service-session-power-relay-v1")]
    clock: RelayClock,
}
impl Fixture {
    fn new(delay: Duration, shutdown: bool, relay: bool) -> Self {
        let stops = Arc::new(AtomicUsize::new(0));
        #[cfg(feature = "service-session-power-relay-v1")]
        let clock = RelayClock::new();
        let gate = Rc::new(tokio::sync::Mutex::new(()));
        let _ = (delay, shutdown, relay);
        let ports = Ports {
            #[cfg(feature = "service-lifecycle-shutdown-v1")]
            shutdown: shutdown.then(|| {
                let counted = stops.clone();
                Rc::new(ShutdownSignal::new(
                    delay,
                    Arc::new(move || {
                        counted.fetch_add(1, Ordering::AcqRel);
                    }),
                ))
            }),
            #[cfg(feature = "service-session-power-relay-v1")]
            relay: relay.then(|| Rc::new(clock.relay())),
        };
        Self {
            source: Rc::new(Mutex::new(Box::new(LifecycleSource {
                ports,
                gate: gate.clone(),
            }))),
            gate,
            stops,
            #[cfg(feature = "service-session-power-relay-v1")]
            clock,
        }
    }
    fn stops(&self) -> usize {
        self.stops.load(Ordering::Acquire)
    }
    async fn stops_reaching(&self, expected: usize) -> usize {
        stops_reaching(&self.stops, expected).await
    }
    fn serve(
        &self,
        caller: Caller,
        administrator: bool,
    ) -> (
        DuplexStream,
        impl std::future::Future<Output = Result<(), ExchangeError>> + use<>,
    ) {
        let (client, server) = duplex(65536);
        let stream = Box::new(AdminStream {
            inner: test_stream(server, caller, Fault::None),
            administrator,
        });
        let server = serve_connection(
            stream,
            self.source.clone(),
            None,
            Rc::new(Subscriptions::new(2)),
        );
        (client, server)
    }
    /// The reply to `payload` exactly as the encoder wrote it, or `None`
    /// when the connection closed without one.
    async fn exchange(
        &self,
        payload: &[u8],
        caller: Caller,
        administrator: bool,
    ) -> (Result<(), ExchangeError>, Option<Vec<u8>>) {
        let (mut client, server) = self.serve(caller, administrator);
        let client = async move {
            crate::service_frame::write_payload(&mut client, payload)
                .await
                .unwrap();
            crate::service_frame::read_payload(&mut client).await.ok()
        };
        tokio::join!(server, client)
    }
}

const INTERACTIVE: Caller = Caller {
    pid: Some(PID),
    logon: Logon::Interactive,
    session: 1,
    control_session: Some(1),
};
const OTHER: Caller = Caller {
    pid: Some(PID),
    logon: Logon::Other,
    session: 0,
    control_session: None,
};

fn vector_caller(row: &Value) -> Caller {
    let logon = match row["logon"].as_str().unwrap() {
        "system" => Logon::System,
        "interactive" => Logon::Interactive,
        "remoteInteractive" => Logon::RemoteInteractive,
        "other" => Logon::Other,
        other => panic!("unknown vector logon {other}"),
    };
    Caller {
        pid: Some(PID),
        logon,
        session: row["session"].as_u64().unwrap() as u32,
        // The vectors' session authority reports console session 1.
        control_session: Some(1),
    }
}

fn shutdown_request(n: u8) -> Vec<u8> {
    serde_json::to_vec(&json!({
        "operation": "shutdownOpenBlade",
        "version": 91,
        "requestId": format!("aaaaaaaa-bbbb-4ccc-8ddd-{n:012}"),
    }))
    .unwrap()
}

fn report_request(n: u8, timestamp: i64, suspending: bool) -> Vec<u8> {
    serde_json::to_vec(&json!({
        "operation": "reportSessionPowerTransition",
        "version": 91,
        "requestId": format!("aaaaaaaa-bbbb-4ccc-8ddd-{n:012}"),
        "instanceId": "aaaaaaaa-bbbb-4ccc-8ddd-000000000501",
        "instanceStartedQpc": 1000,
        "sequence": n,
        "timestampQpc": timestamp,
        "suspending": suspending,
    }))
    .unwrap()
}

fn reply(bytes: Option<Vec<u8>>) -> Value {
    serde_json::from_slice::<Value>(&bytes.expect("a reply")).unwrap()
}

/// The stops counted once `expected` have run, bounded: a stop runs on its
/// own task after the reply, so a short count is waited for. The count is
/// then read after a settle, so a stop beyond `expected` is still seen.
pub(super) async fn stops_reaching(stops: &AtomicUsize, expected: usize) -> usize {
    let deadline = tokio::time::Instant::now() + Duration::from_secs(30);
    while stops.load(Ordering::Acquire) < expected && tokio::time::Instant::now() < deadline {
        tokio::time::sleep(Duration::from_millis(1)).await;
    }
    tokio::time::sleep(Duration::from_millis(20)).await;
    stops.load(Ordering::Acquire)
}

/// Every managed reply of a compiled operation, member for member: the
/// managed serializer orders members differently, as every native reply
/// does. Managed waits for an accepted report's completion before its
/// deadline reply; native gives that reply at once, with the same members.
#[tokio::test]
async fn managed_lifecycle_replies_replay() {
    let vectors: Value = serde_json::from_str(VECTORS).unwrap();
    let frames = vectors["frames"].as_array().unwrap();
    assert_eq!(frames.len(), 14);
    let mut replayed = 0;
    for row in frames {
        let name = row["name"].as_str().unwrap();
        let compiled = if name.starts_with("shutdown/") {
            SHUTDOWN
        } else {
            RELAY
        };
        if !compiled {
            continue;
        }
        let fixture = Fixture::new(Duration::ZERO, true, row["relay"] == true);
        let (served, response) = fixture
            .exchange(
                row["request"].as_str().unwrap().as_bytes(),
                vector_caller(&row["caller"]),
                row["caller"]["administrator"] == true,
            )
            .await;
        assert!(served.is_ok(), "{name}: {:?}", served.err());
        let shutdowns = usize::try_from(row["shutdowns"].as_u64().unwrap()).unwrap();
        let managed: Value = serde_json::from_str(row["response"].as_str().unwrap()).unwrap();
        assert_eq!(reply(response), managed, "{name}");
        assert_eq!(fixture.stops_reaching(shutdowns).await, shutdowns, "{name}");
        replayed += 1;
    }
    let expected = usize::from(SHUTDOWN) * 8 + usize::from(RELAY) * 6;
    assert_eq!(replayed, expected);
}

/// Only the shutdown admits a caller that is neither SYSTEM nor interactive.
/// Every other operation keeps closing its connection without a reply, an
/// administrator's included.
#[tokio::test]
async fn only_the_shutdown_admits_a_non_interactive_caller() {
    let request = |operation: &str, n: u8, body: Value| {
        let mut request = json!({
            "operation": operation,
            "version": 91,
            "requestId": format!("aaaaaaaa-bbbb-4ccc-8ddd-{n:012}"),
        });
        let body = body.as_object().unwrap().clone();
        request.as_object_mut().unwrap().extend(body);
        serde_json::to_vec(&request).unwrap()
    };
    let refused = [
        request("getStatus", 31, json!({})),
        request(
            "setPerformanceOverride",
            32,
            json!({"value": {"value": 1, "lifetime": 1, "customPerformance": null, "gpuClockTuning": null}}),
        ),
        request("clearLocalDimming", 33, json!({})),
        // Where the relay is not compiled, its operation is an unsupported one.
        report_request(1, START, true),
    ];
    for payload in refused {
        let fixture = Fixture::new(Duration::ZERO, true, true);
        let (served, response) = fixture.exchange(&payload, OTHER, true).await;
        assert!(
            matches!(served, Err(ExchangeError::Client(_))),
            "{}",
            String::from_utf8_lossy(&payload)
        );
        assert!(response.is_none());
    }
    let fixture = Fixture::new(Duration::ZERO, true, true);
    let (served, response) = fixture.exchange(&shutdown_request(1), OTHER, true).await;
    if SHUTDOWN {
        assert_eq!(
            reply(response)["result"]["message"],
            "OpenBlade is shutting down."
        );
    } else {
        // Where the shutdown is not compiled, it closes as every other
        // operation does.
        assert!(matches!(served, Err(ExchangeError::Client(_))) && response.is_none());
    }
}

/// An operation this composition did not compile is answered as managed
/// answers one it does not dispatch.
#[tokio::test]
async fn an_uncompiled_operation_is_unsupported() {
    let fixture = Fixture::new(Duration::ZERO, true, true);
    for (compiled, payload) in [
        (SHUTDOWN, shutdown_request(1)),
        (RELAY, report_request(1, START, true)),
    ] {
        if compiled {
            continue;
        }
        let (_, response) = fixture.exchange(&payload, INTERACTIVE, true).await;
        let response = reply(response);
        assert_eq!(response["success"], false);
        assert_eq!(response["error"], "IPC request operation is unsupported.");
    }
    assert_eq!(fixture.stops(), 0);
}

#[cfg(feature = "service-lifecycle-shutdown-v1")]
mod shutdown {
    use super::*;

    #[tokio::test(start_paused = true)]
    async fn shutdown_stops_once_after_the_managed_delay() {
        let fixture = Fixture::new(SHUTDOWN_DELAY, true, false);
        let (_, response) = fixture.exchange(&shutdown_request(1), SYSTEM, false).await;
        assert_eq!(
            reply(response)["result"],
            json!({"status": 0, "message": SHUTTING_DOWN})
        );
        tokio::time::sleep(SHUTDOWN_DELAY - Duration::from_millis(1)).await;
        assert_eq!(fixture.stops(), 0, "the reply is written before the stop");
        // A repeat inside the delay is answered and stops nothing more.
        let (_, response) = fixture.exchange(&shutdown_request(2), SYSTEM, false).await;
        assert_eq!(reply(response)["result"]["status"], 0);
        tokio::time::sleep(Duration::from_millis(2)).await;
        assert_eq!(fixture.stops(), 1);
        let (_, response) = fixture.exchange(&shutdown_request(3), SYSTEM, false).await;
        assert_eq!(reply(response)["success"], true);
        tokio::time::sleep(SHUTDOWN_DELAY * 2).await;
        assert_eq!(fixture.stops(), 1);
    }

    /// The administrator evidence is the caller's own token, read after the
    /// request; evidence that cannot be read is no administrator.
    #[tokio::test]
    async fn shutdown_admits_system_and_administrators_of_any_logon_only() {
        let remote = Caller {
            logon: Logon::RemoteInteractive,
            ..INTERACTIVE
        };
        let inactive = Caller {
            session: 2,
            ..INTERACTIVE
        };
        for (caller, administrator, admitted) in [
            (SYSTEM, false, true),
            (INTERACTIVE, true, true),
            (inactive, true, true),
            (remote, true, true),
            (OTHER, true, true),
            (INTERACTIVE, false, false),
            (inactive, false, false),
            (remote, false, false),
            (OTHER, false, false),
        ] {
            let fixture = Fixture::new(Duration::ZERO, true, false);
            let (served, response) = fixture
                .exchange(&shutdown_request(1), caller, administrator)
                .await;
            assert!(served.is_ok());
            let response = reply(response);
            if admitted {
                assert_eq!(response["result"]["message"], SHUTTING_DOWN);
                assert_eq!(fixture.stops_reaching(1).await, 1);
            } else {
                assert_eq!(response["error"], ADMINISTRATOR);
                assert_eq!(fixture.stops_reaching(0).await, 0);
            }
        }
    }

    /// The shutdown waits for the operation in flight before it replies or
    /// stops anything, as managed waits for its user-request gate.
    #[tokio::test]
    async fn shutdown_waits_for_the_operation_in_flight() {
        let fixture = Fixture::new(Duration::ZERO, true, false);
        let held = fixture.gate.lock().await;
        let request = shutdown_request(1);
        let exchange = fixture.exchange(&request, SYSTEM, false);
        tokio::pin!(exchange);
        assert!(
            tokio::time::timeout(Duration::from_millis(50), &mut exchange)
                .await
                .is_err()
        );
        assert_eq!(fixture.stops(), 0);
        drop(held);
        let (served, response) = exchange.await;
        assert!(served.is_ok());
        assert_eq!(reply(response)["result"]["status"], 0);
        assert_eq!(fixture.stops_reaching(1).await, 1);
    }

    /// A client that leaves while the shutdown waits cancels it: nothing is
    /// stopped, then or when the operation in flight ends, and the next
    /// request is served.
    #[tokio::test]
    async fn a_shutdown_cancelled_while_waiting_never_stops() {
        let fixture = Fixture::new(Duration::ZERO, true, false);
        let held = fixture.gate.lock().await;
        let (mut client, server) = fixture.serve(SYSTEM, false);
        let client = async move {
            crate::service_frame::write_payload(&mut client, &shutdown_request(1))
                .await
                .unwrap();
            tokio::time::sleep(Duration::from_millis(20)).await;
            drop(client);
        };
        let (served, ()) = tokio::join!(server, client);
        assert!(matches!(served, Err(ExchangeError::Client(_))));
        drop(held);
        assert_eq!(fixture.stops_reaching(0).await, 0);
        let (_, response) = fixture.exchange(&shutdown_request(2), SYSTEM, false).await;
        assert_eq!(reply(response)["result"]["status"], 0);
        assert_eq!(fixture.stops_reaching(1).await, 1);
    }

    /// A composition that compiled the shutdown without its port answers it
    /// as unsupported and stops nothing.
    #[tokio::test]
    async fn a_shutdown_without_its_port_is_unsupported() {
        let fixture = Fixture::new(Duration::ZERO, false, false);
        let (_, response) = fixture.exchange(&shutdown_request(1), SYSTEM, false).await;
        let response = reply(response);
        assert_eq!(response["success"], false);
        assert_eq!(response["error"], crate::service_status::UNSUPPORTED);
        assert_eq!(fixture.stops(), 0);
    }
}

#[cfg(feature = "service-session-power-relay-v1")]
mod relay {
    use std::collections::BTreeSet;

    use super::*;

    fn report(row: &Value) -> Report<'_> {
        Report {
            sid: row["sid"].as_str().unwrap(),
            session: row["session"].as_u64().unwrap() as u32,
            instance: row["instance"].as_str().unwrap(),
            started: row["started"].as_i64().unwrap(),
            sequence: row["sequence"].as_i64().unwrap(),
            timestamp: row["timestamp"].as_i64().unwrap(),
            suspending: row["suspending"].as_bool().unwrap(),
        }
    }

    /// Every managed relay case. Each rejection is managed's; an accepted
    /// report is managed's deadline reply (the documented deviation).
    #[test]
    fn managed_relay_cases_replay_with_the_accepted_reply_literal() {
        let vectors: Value = serde_json::from_str(VECTORS).unwrap();
        let cases = vectors["relay"].as_array().unwrap();
        assert_eq!(cases.len(), 19);
        let (mut accepted, mut rejected) = (0, BTreeSet::new());
        for case in cases {
            let name = case["name"].as_str().unwrap();
            let clock = RelayClock::new();
            let relay = clock.relay();
            for step in case["steps"].as_array().unwrap() {
                if let Some(advance) = step["advance"].as_i64() {
                    clock.now.set(clock.now.get() + advance);
                } else if let Some(signal) = step["scm"].as_str() {
                    clock.scm(match signal {
                        "suspend" => Signal::Suspend,
                        "resume" => Signal::Resume,
                        "displayOn" => Signal::DisplayOn,
                        other => panic!("{name}: unknown signal {other}"),
                    });
                } else {
                    let native = relay.report(&report(&step["report"]));
                    let message = step["message"].as_str().unwrap();
                    if [UNAUTHORIZED, STALE, NEWER].contains(&message) {
                        assert_eq!(step["status"], 2, "{name}");
                        assert_eq!(native, Ok((2, message)), "{name}");
                        rejected.insert(message);
                    } else {
                        // Deviation: managed answers an accepted report with
                        // its queued work's own result. Native queues none
                        // and gives managed's deadline reply at once.
                        assert!(
                            matches!(
                                message,
                                "Synthetic Off confirmed."
                                    | "Resume notification processing completed."
                            ),
                            "{name}: {message}"
                        );
                        assert_eq!(step["status"], 1, "{name}");
                        assert_eq!(native, Ok((0, ACCEPTED)), "{name}");
                        accepted += 1;
                    }
                }
            }
        }
        assert_eq!(accepted, 22);
        assert_eq!(rejected, BTreeSet::from([UNAUTHORIZED, STALE, NEWER]));
    }

    /// A report is answered without waiting for the operation in flight;
    /// a repeat, a late report and a future one are refused from the relay's
    /// own record, and the gate is SCM's alone.
    #[tokio::test]
    async fn reports_are_answered_at_once_and_never_reach_the_gate() {
        let fixture = Fixture::new(Duration::ZERO, true, true);
        let _held = fixture.gate.lock().await;
        let (_, response) = fixture
            .exchange(&report_request(1, START, true), INTERACTIVE, false)
            .await;
        assert_eq!(
            reply(response)["result"],
            json!({"status": 0, "message": ACCEPTED})
        );
        for (payload, message) in [
            (report_request(1, START, true), STALE),
            (report_request(2, START - 1, false), NEWER),
            (report_request(3, START + 1, false), UNAUTHORIZED),
        ] {
            let (_, response) = fixture.exchange(&payload, INTERACTIVE, false).await;
            assert_eq!(
                reply(response)["result"],
                json!({"status": 2, "message": message})
            );
        }
        assert_eq!(
            fixture.clock.gate.borrow().as_ref().map(LowPowerGate::view),
            Some(View {
                latest: 0,
                active: false
            })
        );
        assert_eq!(fixture.stops(), 0);
        // Another session's caller is refused before the relay reads anything.
        let (_, response) = fixture
            .exchange(
                &report_request(4, START, false),
                Caller {
                    session: 2,
                    ..INTERACTIVE
                },
                false,
            )
            .await;
        assert_eq!(reply(response)["error"], INACTIVE);
    }

    /// A composition that compiled the relay without its owner answers
    /// managed's unavailable failure.
    #[tokio::test]
    async fn a_relay_without_its_owner_is_unavailable() {
        let fixture = Fixture::new(Duration::ZERO, false, false);
        let (_, response) = fixture
            .exchange(&report_request(1, START, true), INTERACTIVE, false)
            .await;
        assert_eq!(reply(response)["error"], UNAVAILABLE);
    }

    /// An exhausted lifecycle reads no gate, so the relay cannot judge a
    /// report: it answers the unavailable failure and records nothing.
    #[test]
    fn an_unreadable_gate_is_unavailable_to_the_relay() {
        let clock = RelayClock::new();
        let relay = clock.relay();
        let gate = clock.gate.borrow_mut().take();
        let row = json!({
            "sid": "S-1-5-21-111-222-333-1001", "session": 1,
            "instance": "AAAAAAAA-bbbb-4ccc-8ddd-000000000501", "started": 1000,
            "sequence": 1, "timestamp": START, "suspending": true,
        });
        assert_eq!(relay.report(&report(&row)), Err(UNAVAILABLE));
        *clock.gate.borrow_mut() = gate;
        // Nothing was recorded: the same report is accepted once the gate
        // reads again, and its instance compares without regard to case.
        assert_eq!(relay.report(&report(&row)), Ok((0, ACCEPTED)));
        let mut lower = row.clone();
        lower["instance"] = json!("aaaaaaaa-bbbb-4ccc-8ddd-000000000501");
        assert_eq!(relay.report(&report(&lower)), Ok((2, STALE)));
    }
}

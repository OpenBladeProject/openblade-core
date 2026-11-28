//! Native exportDiagnostics over the readiness endpoint: managed's admission
//! and refusal replies, one export at a time, cancellation on disconnect,
//! the time bound and Stop, and the archive the managed codec and provider
//! read back (`NativeDiagnosticArchive_ReadsThroughTheManagedCodec...`).
use super::*;
use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};
use tokio::io::AsyncReadExt;

use super::super::export::{EXPORT_BUDGET, INACTIVE};
use crate::diagnostic_export::{
    self as export, Cancel, Failure, History, HostMetadata, Limits, Metadata, Sources, Timestamp,
    tests::{archive_texts, sequence},
};

const ID: &str = "8F3C2A71-5D4E-4B6A-9C1F-2E7D0A9B4C31";
const VECTORS: &str = include_str!("../../../../../tests/data/diagnostic-export-vectors.json");
const SAMPLES: &str = "diagnostic-export-native.json";
#[cfg(feature = "service-thermal-mutation-v1")]
const PRIVATE_SAMPLES: &str = "diagnostic-export-native-private.json";
const REGENERATE: &str = "OPENBLADE_REGENERATE_NATIVE_DIAGNOSTIC_ARCHIVES";

const ACTIVE: Caller = Caller {
    control_session: Some(3),
    ..SETUP
};
const INACTIVE_CALLER: Caller = Caller {
    control_session: Some(4),
    ..SETUP
};
const REMOTE: Caller = Caller {
    logon: Logon::RemoteInteractive,
    ..SETUP
};
const SERVICE_ACCOUNT: Caller = Caller {
    logon: Logon::Other,
    ..SETUP
};

fn at(text: &str) -> Timestamp {
    Timestamp::parse(text).unwrap_or_else(|| panic!("{text}"))
}

struct Fixed(HostMetadata);

impl Metadata for Fixed {
    fn capture(&self, _cancel: &Cancel) -> Result<HostMetadata, Failure> {
        Ok(self.0.clone())
    }
}

fn metadata(tokens: &[&str], captured_at: &str) -> HostMetadata {
    HostMetadata {
        windows_version: "Microsoft Windows NT 10.0.26300.0".into(),
        tokens: tokens.iter().map(|token| (*token).to_owned()).collect(),
        captured_at: at(captured_at),
        local_time: at(captured_at),
    }
}

fn fixed() -> Arc<dyn Metadata> {
    Arc::new(Fixed(metadata(&[], "2026-11-23T10:00:01.12+00:00")))
}

struct Failing(&'static str);

impl Metadata for Failing {
    fn capture(&self, _cancel: &Cancel) -> Result<HostMetadata, Failure> {
        Err(Failure::new(self.0))
    }
}

/// Signals entry, then holds the build until it is released or canceled.
#[derive(Default)]
struct Held {
    entered: AtomicBool,
    release: AtomicBool,
    canceled: AtomicBool,
    active: AtomicUsize,
    most: AtomicUsize,
    started: AtomicUsize,
}

impl Held {
    fn released() -> Arc<Self> {
        let held = Arc::new(Self::default());
        held.release.store(true, Ordering::Release);
        held
    }

    async fn wait(&self, flag: impl Fn(&Self) -> bool) {
        let deadline = std::time::Instant::now() + Duration::from_secs(10);
        while !flag(self) {
            assert!(
                std::time::Instant::now() < deadline,
                "the build never progressed"
            );
            tokio::task::yield_now().await;
            std::thread::sleep(Duration::from_millis(1));
        }
    }
}

struct HeldMetadata(Arc<Held>);

impl Metadata for HeldMetadata {
    fn capture(&self, cancel: &Cancel) -> Result<HostMetadata, Failure> {
        let held = &self.0;
        let active = held.active.fetch_add(1, Ordering::AcqRel) + 1;
        held.most.fetch_max(active, Ordering::AcqRel);
        held.started.fetch_add(1, Ordering::AcqRel);
        held.entered.store(true, Ordering::Release);
        let deadline = std::time::Instant::now() + Duration::from_secs(10);
        let result = loop {
            if let Err(failure) = cancel.check() {
                held.canceled.store(true, Ordering::Release);
                break Err(failure);
            }
            if held.release.load(Ordering::Acquire) {
                // Long enough for an unserialized second export to enter.
                std::thread::sleep(Duration::from_millis(20));
                break Ok(metadata(&[], "2026-11-23T10:00:01.12+00:00"));
            }
            if std::time::Instant::now() > deadline {
                break Err(Failure::new("never released"));
            }
            std::thread::sleep(Duration::from_millis(1));
        };
        held.active.fetch_sub(1, Ordering::AcqRel);
        result
    }
}

struct ExportSource {
    inner: TestSource,
    sources: Sources,
}

impl StatusSource for ExportSource {
    fn snapshot(&mut self, caller: Caller, requested: Instant) -> Pending<'_, Snapshot> {
        self.inner.snapshot(caller, requested)
    }

    fn drain(&mut self) -> Pending<'_, ()> {
        self.inner.drain()
    }

    fn diagnostics(&self) -> Sources {
        self.sources.clone()
    }
}

type Shared = Rc<Mutex<Box<dyn StatusSource>>>;

fn empty_history() -> Arc<History> {
    Arc::new(History::new(
        Limits::DEFAULT,
        Vec::new(),
        Box::new(|| Timestamp::from_ticks(0)),
    ))
}

fn shared(history: Arc<History>, metadata: Arc<dyn Metadata>, events: &Events) -> Shared {
    Rc::new(Mutex::new(Box::new(ExportSource {
        inner: TestSource {
            fault: Fault::None,
            events: events.clone(),
            publications: None,
        },
        sources: Sources { history, metadata },
    })))
}

fn request(live_readings_enabled: bool) -> Vec<u8> {
    serde_json::to_vec(&json!({
        "operation": "exportDiagnostics", "version": PROTOCOL_VERSION, "requestId": ID,
        "liveReadingsEnabled": live_readings_enabled,
    }))
    .unwrap()
}

async fn exchange_on(
    stream: Box<dyn Stream>,
    client: DuplexStream,
    source: Shared,
    payload: &[u8],
) -> (Result<(), ExchangeError>, Vec<u8>) {
    exchange_with(
        Rc::new(Subscriptions::new(1)),
        stream,
        client,
        source,
        payload,
    )
    .await
}

/// One connection of a server whose state, the export gate included, is
/// `server`.
async fn exchange_with(
    server: Rc<Subscriptions>,
    stream: Box<dyn Stream>,
    mut client: DuplexStream,
    source: Shared,
    payload: &[u8],
) -> (Result<(), ExchangeError>, Vec<u8>) {
    let served = serve_connection(stream, source, None, server);
    let sent = async {
        crate::service_frame::write_payload(&mut client, payload)
            .await
            .unwrap();
        let mut reply = Vec::new();
        client.read_to_end(&mut reply).await.unwrap();
        reply
    };
    tokio::join!(served, sent)
}

async fn exchange(
    caller: Caller,
    source: Shared,
    live: bool,
) -> (Result<(), ExchangeError>, Vec<u8>) {
    exchange_sharing(Rc::new(Subscriptions::new(1)), caller, source, live).await
}

async fn exchange_sharing(
    server: Rc<Subscriptions>,
    caller: Caller,
    source: Shared,
    live: bool,
) -> (Result<(), ExchangeError>, Vec<u8>) {
    let (client, stream) = duplex(1 << 20);
    exchange_with(
        server,
        test_stream(stream, caller, Fault::None),
        client,
        source,
        &request(live),
    )
    .await
}

fn reply_value(reply: &[u8]) -> Value {
    let length = i32::from_le_bytes(reply[..4].try_into().unwrap());
    assert_eq!(length as usize, reply.len() - 4);
    assert!(reply[4..].starts_with(br#"{"frame":"response","#));
    serde_json::from_slice(&reply[4..]).unwrap()
}

fn failure(error: &str) -> Value {
    json!({
        "frame": "response", "version": PROTOCOL_VERSION, "requestId": ID.to_ascii_lowercase(),
        "success": false, "error": error, "status": null, "result": null,
        "archive": null, "errorCode": null,
    })
}

fn decode64(text: &str) -> Vec<u8> {
    const ALPHABET: &[u8; 64] = b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    let mut bytes = Vec::new();
    for chunk in text.as_bytes().chunks(4) {
        let mut value = 0u32;
        let mut count = 0;
        for byte in chunk.iter().filter(|byte| **byte != b'=') {
            let digit = ALPHABET.iter().position(|letter| letter == byte).unwrap() as u32;
            value |= digit << (18 - 6 * count);
            count += 1;
        }
        bytes.extend(value.to_be_bytes()[1..count].iter());
    }
    bytes
}

fn archive_entries(reply: &Value) -> Vec<(String, String)> {
    assert_eq!(reply["success"], json!(true), "{reply}");
    assert_eq!(reply["error"], Value::Null);
    assert_eq!(reply["status"], Value::Null);
    let archive = &reply["archive"];
    let name = archive["suggestedFileName"].as_str().unwrap();
    assert!(
        name.starts_with("OpenBlade-diagnostics-") && name.ends_with("Z.zip"),
        "{name}"
    );
    let bytes = decode64(archive["zipBytes"].as_str().unwrap());
    assert_eq!(
        export::base64(&bytes),
        archive["zipBytes"].as_str().unwrap()
    );
    archive_texts(&bytes)
}

const ENTRIES: [&str; 10] = [
    "versions.json",
    "device.json",
    "device-verification.json",
    "capabilities.json",
    "configuration.json",
    "command-results.json",
    "service-history.json",
    "firmware-queries.json",
    "hyperboost-ownership.json",
    "logs.jsonl",
];

#[tokio::test]
async fn system_and_the_console_caller_export_the_archive() {
    for caller in [SYSTEM, ACTIVE] {
        let events = Events::default();
        let (result, reply) =
            exchange(caller, shared(empty_history(), fixed(), &events), true).await;
        result.unwrap();
        let entries = archive_entries(&reply_value(&reply));
        let names: Vec<&str> = entries.iter().map(|(name, _)| name.as_str()).collect();
        assert_eq!(names, ENTRIES);
        let configuration = &entries[4].1;
        assert!(
            configuration.contains("\"liveReadings.enabled\": \"True\""),
            "{configuration}"
        );
        assert_eq!(entries[9].1, "");
        // The archive reads only the published status.
        assert_eq!(*events.borrow(), ["snapshot"]);
    }
}

fn vector_caller(name: &str) -> Caller {
    match name {
        "inactiveInteractive" => INACTIVE_CALLER,
        "remoteInteractive" => REMOTE,
        "notInteractive" => SERVICE_ACCOUNT,
        "system" => SYSTEM,
        other => panic!("{other}"),
    }
}

/// Managed `ControlRequestHandler` refusals replayed. The session refusals
/// and failure replies match byte for byte in value; a non-interactive
/// caller closes the connection, the endpoint-wide native convention. An
/// oversized archive reaches its reply through the same failure path that
/// `an_archive_past_the_bound_is_refused_with_the_managed_message` covers,
/// so its message is supplied by the metadata here; the unreadable history
/// is a real poisoned history.
#[tokio::test]
async fn managed_refusal_replies_are_replayed() {
    let vectors: Value = serde_json::from_str(VECTORS).unwrap();
    for vector in vectors["refusals"].as_array().unwrap() {
        let name = vector["name"].as_str().unwrap();
        let caller = vector_caller(vector["caller"].as_str().unwrap());
        let events = Events::default();
        let history = empty_history();
        let metadata: Arc<dyn Metadata> = match vector["failure"].as_str() {
            Some("Diagnostic history is unavailable.") => {
                history.poison();
                fixed()
            }
            Some("Diagnostic archive exceeds the 16 MiB limit.") => {
                Arc::new(Failing("Diagnostic archive exceeds the 16 MiB limit."))
            }
            Some(other) => panic!("{other}"),
            None => fixed(),
        };
        let (result, reply) = exchange(caller, shared(history, metadata, &events), false).await;
        if caller.logon == Logon::Other {
            assert!(matches!(result, Err(ExchangeError::Client(_))), "{name}");
            assert!(reply.is_empty(), "{name}");
            assert!(events.borrow().is_empty(), "{name}");
            continue;
        }
        result.unwrap();
        let mut expect = vector["expect"].clone();
        assert_eq!(reply_value(&reply), expect.take(), "{name}");
        if vector["failure"].is_null() {
            assert!(events.borrow().is_empty(), "{name} read the status");
        }
    }
    assert_eq!(
        failure(INACTIVE),
        vectors["refusals"][0]["expect"],
        "the native refusal text"
    );
}

#[tokio::test]
async fn a_host_metadata_failure_is_the_managed_failure_reply() {
    let events = Events::default();
    let source = shared(
        empty_history(),
        Arc::new(Failing("The Windows version is unavailable.")),
        &events,
    );
    let (result, reply) = exchange(SYSTEM, source, false).await;
    result.unwrap();
    assert_eq!(
        reply_value(&reply),
        failure("Diagnostic export failed: The Windows version is unavailable.")
    );
}

#[tokio::test]
async fn a_status_read_failure_is_a_runtime_failure_without_a_reply() {
    let events = Events::default();
    let source: Shared = Rc::new(Mutex::new(Box::new(ExportSource {
        inner: TestSource {
            fault: Fault::SourceUnavailable,
            events: events.clone(),
            publications: None,
        },
        sources: Sources {
            history: empty_history(),
            metadata: fixed(),
        },
    })));
    let (result, reply) = exchange(SYSTEM, source, false).await;
    assert!(matches!(result, Err(ExchangeError::Runtime(_))));
    assert!(reply.is_empty());
}

#[tokio::test]
async fn an_unexported_capability_member_fails_closed_with_a_reply() {
    struct Custom(TestSource, Sources);
    impl StatusSource for Custom {
        fn snapshot(&mut self, caller: Caller, requested: Instant) -> Pending<'_, Snapshot> {
            Box::pin(async move {
                let mut status = serde_json::to_value(self.0.snapshot(caller, requested).await?)?;
                status["capabilities"]["observedPowerSourceBehaviors"] = json!(["Unknown"]);
                Ok(serde_json::from_value(status)?)
            })
        }
        fn drain(&mut self) -> Pending<'_, ()> {
            self.0.drain()
        }
        fn diagnostics(&self) -> Sources {
            self.1.clone()
        }
    }
    let events = Events::default();
    let inner = TestSource {
        fault: Fault::None,
        events: events.clone(),
        publications: None,
    };
    let sources = Sources {
        history: empty_history(),
        metadata: fixed(),
    };
    let source: Shared = Rc::new(Mutex::new(Box::new(Custom(inner, sources))));
    let (result, reply) = exchange(SYSTEM, source, false).await;
    result.unwrap();
    assert_eq!(
        reply_value(&reply),
        failure(
            "Diagnostic export failed: The capability member observedPowerSourceBehaviors \
             has no native diagnostic summary."
        )
    );
}

#[tokio::test]
async fn concurrent_exports_build_one_at_a_time() {
    let held = Held::released();
    let source = shared(
        empty_history(),
        Arc::new(HeldMetadata(held.clone())),
        &Events::default(),
    );
    let server = Rc::new(Subscriptions::new(1));
    let (first, second, third) = tokio::join!(
        exchange_sharing(server.clone(), SYSTEM, source.clone(), false),
        exchange_sharing(server.clone(), ACTIVE, source.clone(), false),
        exchange_sharing(server.clone(), SYSTEM, source.clone(), true),
    );
    for (result, reply) in [first, second, third] {
        result.unwrap();
        archive_entries(&reply_value(&reply));
    }
    assert_eq!(held.most.load(Ordering::Acquire), 1);
}

/// A requester that disconnects mid-build cancels it and gets no reply; the
/// next export is not held by it.
#[tokio::test]
async fn a_disconnect_cancels_the_build_and_frees_the_export() {
    let held = Arc::new(Held::default());
    let source = shared(
        empty_history(),
        Arc::new(HeldMetadata(held.clone())),
        &Events::default(),
    );
    let (mut client, server) = duplex(1 << 20);
    let served = serve_connection(
        test_stream(server, SYSTEM, Fault::None),
        source.clone(),
        None,
        Rc::new(Subscriptions::new(1)),
    );
    let gone = async {
        crate::service_frame::write_payload(&mut client, &request(false))
            .await
            .unwrap();
        held.wait(|held| held.entered.load(Ordering::Acquire)).await;
        drop(client);
    };
    let (result, ()) = tokio::join!(served, gone);
    assert!(
        matches!(result, Err(ExchangeError::Client(_))),
        "{result:?}"
    );
    held.wait(|held| held.canceled.load(Ordering::Acquire))
        .await;

    let next = shared(empty_history(), fixed(), &Events::default());
    let (result, reply) = timeout(Duration::from_secs(5), exchange(SYSTEM, next, false))
        .await
        .unwrap();
    result.unwrap();
    archive_entries(&reply_value(&reply));
}

/// Stop aborts each connection task (`JoinSet::shutdown`): dropping the
/// exchange cancels its build and releases the export gate.
#[tokio::test]
async fn stop_during_an_export_cancels_the_build() {
    let held = Arc::new(Held::default());
    let source = shared(
        empty_history(),
        Arc::new(HeldMetadata(held.clone())),
        &Events::default(),
    );
    let (mut client, server) = duplex(1 << 20);
    let served = serve_connection(
        test_stream(server, SYSTEM, Fault::None),
        source,
        None,
        Rc::new(Subscriptions::new(1)),
    );
    tokio::select! {
        _ = served => panic!("the export finished while held"),
        () = async {
            crate::service_frame::write_payload(&mut client, &request(false)).await.unwrap();
            held.wait(|held| held.entered.load(Ordering::Acquire)).await;
        } => {}
    }
    held.wait(|held| held.canceled.load(Ordering::Acquire))
        .await;
    let mut rest = Vec::new();
    client.read_to_end(&mut rest).await.unwrap();
    assert!(rest.is_empty(), "a stopped export replied");

    let next = shared(empty_history(), fixed(), &Events::default());
    let (result, reply) = timeout(Duration::from_secs(5), exchange(SYSTEM, next, false))
        .await
        .unwrap();
    result.unwrap();
    archive_entries(&reply_value(&reply));
}

#[tokio::test(start_paused = true)]
async fn a_build_past_its_time_bound_fails_with_a_reply() {
    let held = Arc::new(Held::default());
    let source = shared(
        empty_history(),
        Arc::new(HeldMetadata(held.clone())),
        &Events::default(),
    );
    let (mut client, server) = duplex(1 << 20);
    let served = serve_connection(
        test_stream(server, SYSTEM, Fault::None),
        source,
        None,
        Rc::new(Subscriptions::new(1)),
    );
    let sent = async {
        crate::service_frame::write_payload(&mut client, &request(false))
            .await
            .unwrap();
        held.wait(|held| held.entered.load(Ordering::Acquire)).await;
        tokio::time::advance(EXPORT_BUDGET + Duration::from_millis(1)).await;
        let mut reply = Vec::new();
        client.read_to_end(&mut reply).await.unwrap();
        reply
    };
    let (result, reply) = tokio::join!(served, sent);
    result.unwrap();
    assert_eq!(
        reply_value(&reply),
        failure(
            "Diagnostic export failed: The diagnostic archive did not complete within its time bound."
        )
    );
    held.wait(|held| held.canceled.load(Ordering::Acquire))
        .await;
}

/// A build that cannot be interrupted: it ignores cancellation and runs until
/// released, as a stalled OS call does.
struct StubbornMetadata(Arc<Held>);

impl Metadata for StubbornMetadata {
    fn capture(&self, _cancel: &Cancel) -> Result<HostMetadata, Failure> {
        let held = &self.0;
        held.active.fetch_add(1, Ordering::AcqRel);
        held.started.fetch_add(1, Ordering::AcqRel);
        held.entered.store(true, Ordering::Release);
        let deadline = std::time::Instant::now() + Duration::from_secs(10);
        while !held.release.load(Ordering::Acquire) && std::time::Instant::now() < deadline {
            std::thread::sleep(Duration::from_millis(1));
        }
        held.active.fetch_sub(1, Ordering::AcqRel);
        Ok(metadata(&[], "2026-11-23T10:00:01.12+00:00"))
    }
}

/// A build that outlives its time bound keeps the export gate: an export
/// that arrives meanwhile gets the same failure at once and starts no second
/// build. Once the build ends, exports run again.
#[tokio::test(start_paused = true)]
async fn an_export_beside_an_unfinished_timed_out_build_fails_without_a_second_build() {
    let held = Arc::new(Held::default());
    let stubborn = shared(
        empty_history(),
        Arc::new(StubbornMetadata(held.clone())),
        &Events::default(),
    );
    let server = Rc::new(Subscriptions::new(1));
    let time_bound = failure(
        "Diagnostic export failed: The diagnostic archive did not complete within its time bound.",
    );
    let (first, ()) = tokio::join!(
        exchange_sharing(server.clone(), SYSTEM, stubborn.clone(), false),
        async {
            held.wait(|held| held.entered.load(Ordering::Acquire)).await;
            tokio::time::advance(EXPORT_BUDGET + Duration::from_millis(1)).await;
        },
    );
    first.0.unwrap();
    assert_eq!(reply_value(&first.1), time_bound);
    assert!(server.exports().is_building());

    // No time passes: the second export is not waiting for a bound.
    let (result, reply) = exchange_sharing(server.clone(), ACTIVE, stubborn, true).await;
    result.unwrap();
    assert_eq!(reply_value(&reply), time_bound);
    assert_eq!(held.started.load(Ordering::Acquire), 1);

    held.release.store(true, Ordering::Release);
    held.wait(|_| !server.exports().is_building()).await;
    let next = shared(empty_history(), fixed(), &Events::default());
    let (result, reply) = exchange_sharing(server, SYSTEM, next, false).await;
    result.unwrap();
    archive_entries(&reply_value(&reply));
}

/// The archive holds the caller's private projection: a caller that loses
/// the console session during the build gets no archive.
#[tokio::test]
async fn a_caller_that_loses_the_session_mid_build_gets_no_archive() {
    let held = Arc::new(Held::default());
    let source = shared(
        empty_history(),
        Arc::new(HeldMetadata(held.clone())),
        &Events::default(),
    );
    let console = Rc::new(Cell::new(Some(ACTIVE.session)));
    let (mut client, server) = duplex(1 << 20);
    let stream = test_stream_with_authority(server, ACTIVE, Fault::None, {
        let console = console.clone();
        Rc::new(move || Ok(console.get()))
    });
    let served = serve_connection(stream, source, None, Rc::new(Subscriptions::new(1)));
    let sent = async {
        crate::service_frame::write_payload(&mut client, &request(false))
            .await
            .unwrap();
        held.wait(|held| held.entered.load(Ordering::Acquire)).await;
        console.set(Some(ACTIVE.session + 1));
        held.release.store(true, Ordering::Release);
        let mut reply = Vec::new();
        client.read_to_end(&mut reply).await.unwrap();
        reply
    };
    let (result, reply) = tokio::join!(served, sent);
    assert!(
        matches!(&result, Err(ExchangeError::Client(error)) if error.kind() == io::ErrorKind::PermissionDenied),
        "{result:?}"
    );
    assert!(reply.is_empty());
}

#[tokio::test]
async fn a_caller_inactive_before_the_status_read_is_refused() {
    let events = Events::default();
    let source = shared(empty_history(), fixed(), &events);
    let (client, server) = duplex(1 << 20);
    // Active when the request is read, inactive at the export's own check.
    let calls = Rc::new(Cell::new(0));
    let stream = test_stream_with_authority(server, ACTIVE, Fault::None, {
        let calls = calls.clone();
        Rc::new(move || {
            calls.set(calls.get() + 1);
            Ok(Some(ACTIVE.session + u32::from(calls.get() > 1)))
        })
    });
    let (result, reply) = exchange_on(stream, client, source, &request(false)).await;
    result.unwrap();
    assert_eq!(reply_value(&reply), failure(INACTIVE));
    assert!(events.borrow().is_empty());
}

#[test]
fn the_compiled_identity_ends_with_the_refused_marker() {
    let identity = std::str::from_utf8(&crate::NATIVE_ARTIFACT_IDENTITY).unwrap();
    let fields: Vec<&str> = identity.split('\n').collect();
    assert_eq!(fields[2], "service");
    assert!(
        fields[8]
            .split(',')
            .any(|marker| marker == "service-diagnostics-export-v1"),
        "{}",
        fields[8]
    );
    assert!(
        crate::native_package::startup_policy_for_tests(&crate::NATIVE_ARTIFACT_IDENTITY).is_err()
    );
}

/// The status a sample's source serves, beyond the managed fixture.
#[derive(Clone, Copy, Default)]
struct Served {
    /// Edits the status the managed codec reads.
    edit: Option<fn(&mut Value)>,
    /// Binds the status's private data to this owner.
    #[cfg(feature = "service-thermal-mutation-v1")]
    owner: Option<&'static str>,
}

impl Served {
    /// The status served at `access`, the control access of the caller.
    fn status(self, access: i32) -> Snapshot {
        let mut status = managed_status();
        status["controlAccess"] = json!(access);
        if let Some(edit) = self.edit {
            edit(&mut status);
        }
        let status: Snapshot = serde_json::from_value(status).unwrap();
        #[cfg(feature = "service-thermal-mutation-v1")]
        let status = match self.owner {
            Some(owner) => status.bind_private_user(owner),
            None => status,
        };
        status
    }
}

/// An export source that serves a sample's status.
struct ServedSource(ExportSource, Served);

impl StatusSource for ServedSource {
    fn snapshot(&mut self, caller: Caller, requested: Instant) -> Pending<'_, Snapshot> {
        let served = self.1;
        Box::pin(async move {
            // The access check and the event log are the fixture's.
            self.0.snapshot(caller, requested).await?;
            Ok(served.status(caller.access()?))
        })
    }

    fn drain(&mut self) -> Pending<'_, ()> {
        self.0.drain()
    }

    fn diagnostics(&self) -> Sources {
        self.0.diagnostics()
    }
}

struct Sample {
    name: &'static str,
    caller: Caller,
    served: Served,
    history: Vec<(&'static str, String)>,
    tokens: &'static [&'static str],
    captured_at: &'static str,
    live: bool,
}

/// Replays each sample through the endpoint and renders what the managed
/// reader test replays: the status event the caller was served, the history
/// the managed side records, the host metadata and the response frame.
async fn record_samples(samples: &[Sample]) -> String {
    let mut recorded = Vec::new();
    for sample in samples {
        let times = sample.history.iter().map(|(time, _)| at(time)).collect();
        let history = Arc::new(History::new(Limits::DEFAULT, Vec::new(), sequence(times)));
        for (_, summary) in &sample.history {
            history.record_summary("Stored", summary);
        }
        let metadata = Arc::new(Fixed(metadata(sample.tokens, sample.captured_at)));
        let source: Shared = Rc::new(Mutex::new(Box::new(ServedSource(
            ExportSource {
                inner: TestSource {
                    fault: Fault::None,
                    events: Events::default(),
                    publications: None,
                },
                sources: Sources { history, metadata },
            },
            sample.served,
        ))));
        let (result, reply) = exchange(sample.caller, source, sample.live).await;
        result.unwrap();
        archive_entries(&reply_value(&reply));

        // The archive is built from the status after the private projection
        // of a thermal composition: the managed provider gets the same one.
        let status = sample.served.status(0);
        #[cfg(feature = "service-thermal-mutation-v1")]
        let status = status.project_private(
            (sample.caller.logon == Logon::Interactive).then_some("S-1-5-21-111-222-333-1001"),
        );
        let status = crate::service_status::encode_status_event(&status).unwrap();
        recorded.push(json!({
            "name": sample.name,
            "status": String::from_utf8(status).unwrap(),
            "response": String::from_utf8(reply[4..].to_vec()).unwrap(),
            "history": sample.history.iter().map(|(time, summary)| json!({
                "at": time, "status": "Stored", "summary": summary,
            })).collect::<Vec<_>>(),
            "tokens": sample.tokens,
            "windowsVersion": "Microsoft Windows NT 10.0.26300.0",
            "capturedAt": sample.captured_at,
            "liveReadingsEnabled": sample.live,
        }));
    }
    let mut text = String::from("{\"samples\":[\n");
    text.push_str(
        &recorded
            .iter()
            .map(|sample| serde_json::to_string(sample).unwrap())
            .collect::<Vec<_>>()
            .join(",\n"),
    );
    text.push_str("\n]}\n");
    text
}

/// Compares `text` with the recorded `file`, or rerecords it.
fn assert_recorded(file: &str, text: &str) {
    let path = std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("tests/data")
        .join(file);
    if std::env::var_os(REGENERATE).is_some() {
        std::fs::write(&path, text).unwrap();
    }
    assert_eq!(std::fs::read_to_string(&path).unwrap(), text);
}

/// Records native responses for the managed reader test
/// (`NativeDiagnosticArchive_ReadsThroughTheManagedCodecAndMatchesTheManagedProvider`).
/// The thermal composition's private projection leaves these statuses as
/// served, so every composition records the same file.
#[tokio::test]
async fn native_archives_match_the_recorded_managed_reader_input() {
    let samples = [
        Sample {
            name: "system with an empty history",
            caller: SYSTEM,
            served: Served::default(),
            history: Vec::new(),
            tokens: &[],
            captured_at: "2026-11-23T10:00:01.12+00:00",
            live: false,
        },
        Sample {
            name: "console caller with redacted and truncated history",
            caller: ACTIVE,
            served: Served::default(),
            history: vec![
                ("2026-11-23T09:59:00+00:00", "Stored profiles for tester on CONTOSO-PC.".into()),
                (
                    "2026-11-23T09:59:01.5+00:00",
                    "Synced https://tester:secret@example.test/profile\r\nwith Authorization: Bearer abc.def"
                        .into(),
                ),
                ("2026-11-23T09:59:02.0000001+00:00", "x".repeat(5000)),
            ],
            tokens: &["tester", "CONTOSO-PC", "0.1.0-test"],
            captured_at: "2026-11-23T23:59:59.9999999+00:00",
            live: true,
        },
        // The members ported from the managed build beside the fixture's
        // defaults: a hyperBoostEntryRestriction, a read-write firmware gaming
        // GPU overclock and a Windows Dynamic Lighting advisory that may
        // control the keyboard.
        Sample {
            name: "capability and advisory members with non-default values",
            caller: SYSTEM,
            served: Served {
                edit: Some(|status| {
                    status["windowsDynamicLightingAdvisory"] = json!(2);
                    // The managed codec admits an entry restriction only beside the
                    // HyperBoost access it restricts.
                    status["capabilities"]["hyperBoostAccess"] = json!(2);
                    status["capabilities"]["hyperBoostEntryRestriction"] = json!(0);
                    status["capabilities"]["firmwareGamingGpuOverclockAccess"] = json!(2);
                }),
                ..Served::default()
            },
            history: Vec::new(),
            tokens: &[],
            captured_at: "2026-11-23T10:00:01.12+00:00",
            live: false,
        },
        // The capability record of the full composition, with every typed
        // record native publishes (`capability_record`).
        Sample {
            name: "identified capability record of every group",
            caller: SYSTEM,
            served: Served {
                edit: Some(|status| {
                    let vectors: Value = serde_json::from_str(include_str!(
                        "../../../../../tests/data/capability-record-vectors.json"
                    ))
                    .unwrap();
                    status["capabilities"] = vectors["rows"]
                        .as_array()
                        .unwrap()
                        .iter()
                        .find(|row| row["name"] == "every group")
                        .unwrap()["record"]
                        .clone();
                }),
                ..Served::default()
            },
            history: Vec::new(),
            tokens: &[],
            captured_at: "2026-11-23T10:00:01.12+00:00",
            live: false,
        },
    ];
    assert_recorded(SAMPLES, &record_samples(&samples).await);
}

/// The private projection of a thermal composition: a caller that does not
/// own the saved private data is exported the redacted status, and its owner
/// the full one. The managed provider is given the status the caller was
/// served, so the same archive entries must come out.
#[cfg(feature = "service-thermal-mutation-v1")]
#[tokio::test]
async fn private_projections_match_the_recorded_managed_reader_input() {
    const CALLER: &str = "S-1-5-21-111-222-333-1001";
    const OTHER: &str = "S-1-5-21-111-222-333-1002";
    let sample = |name, caller, owner| Sample {
        name,
        caller,
        served: Served {
            owner: Some(owner),
            ..Served::default()
        },
        history: Vec::new(),
        tokens: &[],
        captured_at: "2026-11-23T10:00:01.12+00:00",
        live: false,
    };
    let samples = [
        sample("console caller that owns the private data", ACTIVE, CALLER),
        sample(
            "console caller that does not own the private data",
            ACTIVE,
            OTHER,
        ),
        sample("system with no private data of its own", SYSTEM, CALLER),
    ];
    assert_recorded(PRIVATE_SAMPLES, &record_samples(&samples).await);
}

/// A bounded history keeps the newest entries: the managed retention seen
/// through the endpoint.
#[tokio::test]
async fn the_exported_history_is_the_retained_window() {
    let limits = Limits {
        entries: 2,
        ..Limits::DEFAULT
    };
    let times = (0..3)
        .map(|second| at(&format!("2026-11-23T10:00:0{second}+00:00")))
        .collect();
    let history = Arc::new(History::new(limits, Vec::new(), sequence(times)));
    for summary in ["first", "second", "third"] {
        history.record_summary("Stored", summary);
    }
    let (result, reply) =
        exchange(SYSTEM, shared(history, fixed(), &Events::default()), false).await;
    result.unwrap();
    let entries = archive_entries(&reply_value(&reply));
    let results: Value = serde_json::from_str(&entries[5].1).unwrap();
    let details: Vec<&str> = results
        .as_array()
        .unwrap()
        .iter()
        .map(|result| result["detail"].as_str().unwrap())
        .collect();
    assert_eq!(details, ["second", "third"]);
    let history: Value = serde_json::from_str(&entries[6].1).unwrap();
    assert_eq!(history["evictedEntries"], json!(1));
}

/// Managed `MaximumDiagnosticResponseBytes`: a frame of exactly 24 MiB is
/// written and one byte more is refused before anything is sent.
#[tokio::test]
async fn the_archive_response_frame_has_the_managed_bound() {
    const BOUND: usize = 24 * 1024 * 1024;
    let frame = |length: usize| Value::String("a".repeat(length - 2));
    let mut written = Vec::new();
    let at_bound = crate::service_frame::encode_archive_frame(&frame(BOUND)).unwrap();
    crate::service_frame::write_encoded_archive_frame(&mut written, &at_bound)
        .await
        .unwrap();
    assert_eq!(written.len(), BOUND + 4);
    assert_eq!(&written[..4], &(BOUND as i32).to_le_bytes());
    let error = crate::service_frame::encode_archive_frame(&frame(BOUND + 1))
        .err()
        .unwrap();
    assert_eq!(error.kind(), io::ErrorKind::InvalidData);
}

/// A status origin replaced while the archive was built fails the Service's
/// exchange instead of answering with the old origin's status.
#[tokio::test]
async fn an_origin_replaced_during_the_build_is_a_runtime_failure() {
    struct Replaced(ExportSource, Rc<Cell<bool>>);
    impl StatusSource for Replaced {
        fn snapshot(&mut self, caller: Caller, requested: Instant) -> Pending<'_, Snapshot> {
            self.0.snapshot(caller, requested)
        }
        fn drain(&mut self) -> Pending<'_, ()> {
            self.0.drain()
        }
        fn diagnostics(&self) -> Sources {
            self.0.diagnostics()
        }
        fn verify_origin(&self) -> io::Result<()> {
            if self.1.get() {
                return Err(io::Error::other("status origin replaced"));
            }
            Ok(())
        }
    }
    let held = Arc::new(Held::default());
    let replaced = Rc::new(Cell::new(false));
    let source: Shared = Rc::new(Mutex::new(Box::new(Replaced(
        ExportSource {
            inner: TestSource {
                fault: Fault::None,
                events: Events::default(),
                publications: None,
            },
            sources: Sources {
                history: empty_history(),
                metadata: Arc::new(HeldMetadata(held.clone())),
            },
        },
        replaced.clone(),
    ))));
    let (mut client, server) = duplex(1 << 20);
    let served = serve_connection(
        test_stream(server, SYSTEM, Fault::None),
        source,
        None,
        Rc::new(Subscriptions::new(1)),
    );
    let sent = async {
        crate::service_frame::write_payload(&mut client, &request(false))
            .await
            .unwrap();
        held.wait(|held| held.entered.load(Ordering::Acquire)).await;
        replaced.set(true);
        held.release.store(true, Ordering::Release);
        let mut reply = Vec::new();
        client.read_to_end(&mut reply).await.unwrap();
        reply
    };
    let (result, reply) = tokio::join!(served, sent);
    assert!(
        matches!(result, Err(ExchangeError::Runtime(_))),
        "{result:?}"
    );
    assert!(reply.is_empty());
}

/// A status the status codec refuses fails the Service's exchange, as a GET
/// does, instead of exporting it.
#[tokio::test]
async fn an_invalid_status_is_a_runtime_failure_without_a_reply() {
    let source: Shared = Rc::new(Mutex::new(Box::new(ExportSource {
        inner: TestSource {
            fault: Fault::InvalidStatus,
            events: Events::default(),
            publications: None,
        },
        sources: Sources {
            history: empty_history(),
            metadata: fixed(),
        },
    })));
    let (result, reply) = exchange(SYSTEM, source, false).await;
    assert!(
        matches!(result, Err(ExchangeError::Runtime(_))),
        "{result:?}"
    );
    assert!(reply.is_empty());
}

/// An origin already replaced when the export starts reads no status.
#[tokio::test]
async fn an_origin_replaced_before_the_export_reads_no_status() {
    struct Replaced(ExportSource);
    impl StatusSource for Replaced {
        fn snapshot(&mut self, caller: Caller, requested: Instant) -> Pending<'_, Snapshot> {
            self.0.snapshot(caller, requested)
        }
        fn drain(&mut self) -> Pending<'_, ()> {
            self.0.drain()
        }
        fn diagnostics(&self) -> Sources {
            self.0.diagnostics()
        }
        fn verify_origin(&self) -> io::Result<()> {
            Err(io::Error::other("status origin replaced"))
        }
    }
    let events = Events::default();
    let source: Shared = Rc::new(Mutex::new(Box::new(Replaced(ExportSource {
        inner: TestSource {
            fault: Fault::None,
            events: events.clone(),
            publications: None,
        },
        sources: Sources {
            history: empty_history(),
            metadata: fixed(),
        },
    }))));
    let (result, reply) = exchange(SYSTEM, source, false).await;
    assert!(
        matches!(result, Err(ExchangeError::Runtime(_))),
        "{result:?}"
    );
    assert!(reply.is_empty());
    assert!(events.borrow().is_empty(), "the status was read");
}

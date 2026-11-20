use super::*;
/// A setter a test supplier does not script. The endpoint awaits its supplier
/// inline, so the panic fails the test at the unexpected call.
#[cfg(openblade_display_write)]
fn unscripted(operation: &str) -> ! {
    panic!("test supplier received unscripted {operation}")
}
/// The stubs of the setters a test supplier does not script, each behind its
/// own feature, so every supplier refuses unexpected writes the same way.
macro_rules! unscripted_writes {
    ($($setter:ident),+) => {
        $(unscripted_writes!(@ $setter);)+
    };
    (@ set_brightness) => {
        #[cfg(feature = "agent-display-brightness-write-v1")]
        async fn set_brightness(
            &mut self,
            _: SetBrightness,
            _: Arc<AtomicBool>,
        ) -> io::Result<WriteOutcome> {
            unscripted("setBrightness")
        }
    };
    (@ set_hdr) => {
        #[cfg(feature = "agent-display-hdr-write-v1")]
        async fn set_hdr(
            &mut self,
            _: SetHdr,
            _: Arc<AtomicBool>,
        ) -> io::Result<ManualWriteOutcome> {
            unscripted("setHdr")
        }
    };
    (@ set_refresh_rate) => {
        #[cfg(feature = "agent-display-refresh-write-v1")]
        async fn set_refresh_rate(
            &mut self,
            _: SetRefreshRate,
            _: Arc<AtomicBool>,
        ) -> io::Result<ManualWriteOutcome> {
            unscripted("setRefreshRate")
        }
    };
    (@ set_color_profile) => {
        #[cfg(feature = "agent-display-color-write-v1")]
        async fn set_color_profile(
            &mut self,
            _: SetColorProfile,
            _: Arc<AtomicBool>,
        ) -> io::Result<ManualWriteOutcome> {
            unscripted("setColorProfile")
        }
    };
    (@ apply_profile) => {
        #[cfg(feature = "agent-display-profile-apply-v1")]
        async fn apply_profile(
            &mut self,
            _: ApplyProfile,
            _: Arc<AtomicBool>,
        ) -> io::Result<ManualWriteOutcome> {
            unscripted("applyProfile")
        }
    };
}
#[path = "brightness_lifetime_tests.rs"]
mod brightness_lifetime;
use serde_json::{Value, json};
use std::{
    cell::Cell,
    sync::{Condvar, Mutex},
};
const ID: &str = "9f876742-1234-4321-abcd-d3ea3e1cf102";
struct Script {
    outcome: Option<Snapshot>,
    calls: usize,
}
impl Supplier for Script {
    async fn acquire(&mut self, _canceled: Arc<AtomicBool>) -> io::Result<Snapshot> {
        self.calls += 1;
        Ok(self
            .outcome
            .take()
            .expect("unexpected repeated display query"))
    }
    unscripted_writes!(
        set_brightness,
        set_hdr,
        set_refresh_rate,
        set_color_profile,
        apply_profile
    );
}
/// Encode like the managed codec: the `operation` discriminator first (the
/// json! map would otherwise sort it after `expectedPowerSource`).
fn request(mut body: Value) -> Vec<u8> {
    let Some(operation) = body.as_object_mut().and_then(|map| map.remove("operation")) else {
        return raw_request(&serde_json::to_vec(&body).unwrap());
    };
    let rest = serde_json::to_string(&body).unwrap();
    let separator = if rest == "{}" { "" } else { "," };
    let encoded = format!("{{\"operation\":{operation}{separator}{}", &rest[1..]);
    raw_request(encoded.as_bytes())
}
fn raw_request(body: &[u8]) -> Vec<u8> {
    [(body.len() as i32).to_le_bytes().as_slice(), body].concat()
}
fn query() -> Vec<u8> {
    request(json!({"version":2,"requestId":ID,"operation":"query"}))
}

/// Managed `SessionDisplayPipeServer` (SessionDisplayPipeServer.cs:9-10): the
/// 5 s `QueryDispatchTimeout` for a query and the 30 s `MutationDispatchTimeout`
/// for every selected write.
#[test]
fn every_selected_write_has_the_managed_mutation_deadline() {
    let deadline = |body: Value| {
        Request::parse(&request(body)[4..])
            .unwrap()
            .operation
            .deadline()
    };
    let common = |operation: &str| {
        let mut body = json!({"version":2,"requestId":ID,"operation":operation});
        body["expectedPowerSource"] = 0.into();
        body
    };
    assert_eq!(
        deadline(json!({"version":2,"requestId":ID,"operation":"query"})),
        Duration::from_secs(5)
    );
    let mut writes: Vec<Value> = Vec::new();
    if cfg!(feature = "agent-display-brightness-write-v1") {
        let mut body = common("setBrightness");
        body["percent"] = 50.into();
        writes.push(body);
    }
    if cfg!(feature = "agent-display-hdr-write-v1") {
        let mut body = common("setHdr");
        body["enabled"] = true.into();
        writes.push(body);
    }
    if cfg!(feature = "agent-display-refresh-write-v1") {
        let mut body = common("setRefreshRate");
        body["refreshRateKey"] = "fixed-240".into();
        writes.push(body);
    }
    if cfg!(feature = "agent-display-color-write-v1") {
        let mut body = common("setColorProfile");
        body["colorProfileKey"] = "sRGB_CTR.icm".into();
        writes.push(body);
    }
    if cfg!(feature = "agent-display-profile-apply-v1") {
        let mut body = common("applyProfile");
        body["settings"] = json!({"refreshRateKey":null,"hdrEnabled":null,
            "colorProfileKey":null,"brightnessPercent":50,"isConfigured":true});
        writes.push(body);
    }
    for body in writes {
        assert_eq!(deadline(body.clone()), Duration::from_secs(30), "{body}");
    }
}
async fn exchange(
    script: &mut impl Supplier,
    bytes: &[u8],
) -> (Result<(), ServeError>, Vec<u8>, usize) {
    let (mut server, mut client) = tokio::io::duplex(128 * 1024);
    client.write_all(bytes).await.unwrap();
    let (_sender, mut stop) = watch::channel(false);
    let admissions = Cell::new(0);
    let (result, bytes) = tokio::join!(
        async {
            let result = serve(
                &mut server,
                &mut stop,
                script,
                |_| {
                    admissions.set(admissions.get() + 1);
                    Ok(())
                },
                || Ok(()),
            )
            .await;
            drop(server);
            result
        },
        async {
            let mut bytes = Vec::new();
            client.read_to_end(&mut bytes).await.unwrap();
            bytes
        },
    );
    (result, bytes, admissions.get())
}
fn capture(name: &str, frame: &[u8], expected: &Value) {
    capture_row(json!({"case":name,"expectedStatus":0,"expectedState":expected,"frame":frame}));
}
/// One lock serializes every corpus row appended by parallel tests.
fn capture_row(row: Value) {
    if let Some(path) = std::env::var_os("OPENBLADE_NATIVE_AGENT_DISPLAY_FRAMES") {
        use std::io::Write;
        static LOCK: Mutex<()> = Mutex::new(());
        let _lock = LOCK.lock().unwrap();
        let mut file = std::fs::OpenOptions::new()
            .create(true)
            .append(true)
            .open(path)
            .unwrap();
        writeln!(file, "{row}").unwrap();
    }
}
#[tokio::test]
async fn actual_dispatcher_frames_cover_injected_observations_and_production_decoder_shape() {
    for case in [
        "available",
        "no-panel",
        "ambiguous",
        "hdr-unknown",
        "absent-profiles",
        "missing-refresh",
        "brightness-zero",
        "brightness-hundred",
        "brightness-all101",
        "brightness-valid-and-malformed",
        "brightness-wmi-failure",
        "brightness-missing",
        "brightness-foreign",
        "brightness-malformed",
        "brightness-ambiguous",
        "brightness-target-changed",
    ] {
        let snapshot = observation::fixture_query(case);
        let expected = serde_json::to_value(&snapshot).unwrap();
        let mut supplier = Script {
            outcome: Some(snapshot),
            calls: 0,
        };
        let (result, bytes, admissions) = exchange(&mut supplier, &query()).await;
        result.unwrap();
        assert_eq!(supplier.calls, 1);
        assert_eq!(admissions, 2);
        assert_eq!(
            i32::from_le_bytes(bytes[..4].try_into().unwrap()) as usize,
            bytes.len() - 4
        );
        let body: Value = serde_json::from_slice(&bytes[4..]).unwrap();
        assert_eq!(body["requestId"], ID);
        assert_eq!(body["status"], 0);
        assert!(body["error"].is_null());
        assert_eq!(body["state"], expected);
        if !case.starts_with("brightness-") {
            assert!(body["state"]["brightnessPercent"].is_null());
            assert_eq!(body["state"]["supportedBrightnessLevels"], json!([]));
        }
        capture(case, &bytes, &expected);
    }
}
#[tokio::test]
async fn malformed_oversized_and_mutation_requests_reach_no_supplier() {
    let mut inputs = vec![
        (-1i32).to_le_bytes().to_vec(),
        (MAX_FRAME + 1).to_le_bytes().to_vec(),
        raw_request(b"{"),
        request(json!({"version":1,"requestId":ID,"operation":"query"})),
        request(
            json!({"version":2,"requestId":"00000000-0000-0000-0000-000000000000","operation":"query"}),
        ),
        request(json!({"version":2,"requestId":ID,"operation":"query","expectedPowerSource":0})),
        raw_request(
            format!(r#"{{"version":2,"version":2,"requestId":"{ID}","operation":"query"}}"#)
                .as_bytes(),
        ),
    ];
    for operation in [
        "applyProfile",
        "setBrightness",
        "setHdr",
        "setRefreshRate",
        "setColorProfile",
    ] {
        inputs.push(request(
            json!({"version":2,"requestId":ID,"operation":operation}),
        ));
    }
    for input in inputs {
        let mut supplier = Script {
            outcome: None,
            calls: 0,
        };
        let (result, bytes, _) = exchange(&mut supplier, &input).await;
        assert!(matches!(result, Err(ServeError::Connection(_))));
        assert!(bytes.is_empty());
        assert_eq!(supplier.calls, 0);
    }
}
#[tokio::test]
async fn snapshot_and_frame_limits_emit_truthful_unavailable_without_truncation() {
    for large_frame in [false, true] {
        let mut value = serde_json::to_value(observation::fixture_query("available")).unwrap();
        if large_frame {
            let profiles: Vec<_> = (0..64).map(|i| json!({"id":format!("profile-{i}"),"name":"x".repeat(4096),"isActive":false})).collect();
            value["colorProfiles"] = json!(profiles);
        } else {
            value["currentRefreshKey"] = json!("😀".repeat(513));
        }
        let mut supplier = Script {
            outcome: Some(serde_json::from_value(value).unwrap()),
            calls: 0,
        };
        let (result, bytes, _) = exchange(&mut supplier, &query()).await;
        result.unwrap();
        let body: Value = serde_json::from_slice(&bytes[4..]).unwrap();
        assert_eq!(body["status"], 0);
        assert_eq!(body["state"]["isAvailable"], false);
        assert!(body["state"]["currentRefreshKey"].is_null());
        assert!(bytes.len() <= 4 + MAX_FRAME as usize);
        capture(
            if large_frame {
                "frame-limit"
            } else {
                "utf16-key-limit"
            },
            &bytes,
            &body["state"],
        );
    }
}

struct Blocker {
    started: Arc<AtomicBool>,
    canceled: Arc<AtomicBool>,
    done: Arc<AtomicBool>,
    gate: Arc<(Mutex<bool>, Condvar)>,
}
impl Blocker {
    fn new() -> Self {
        Self {
            started: Arc::default(),
            canceled: Arc::default(),
            done: Arc::default(),
            gate: Arc::new((Mutex::new(false), Condvar::new())),
        }
    }
}
impl Supplier for Blocker {
    async fn acquire(&mut self, canceled: Arc<AtomicBool>) -> io::Result<Snapshot> {
        let started = self.started.clone();
        let done = self.done.clone();
        let gate = self.gate.clone();
        let work = tokio::task::spawn_blocking(move || {
            started.store(true, Ordering::Release);
            let (lock, event) = &*gate;
            let mut released = lock.lock().unwrap();
            while !*released {
                released = event.wait(released).unwrap();
            }
            done.store(true, Ordering::Release);
            Ok(Snapshot::unavailable("Injected blocking read completed."))
        });
        tokio::pin!(work);
        loop {
            tokio::select! {
                result = &mut work => return result.unwrap(),
                _ = sleep(Duration::from_millis(1)) => {
                    if canceled.load(Ordering::Acquire) { self.canceled.store(true, Ordering::Release); }
                }
            }
        }
    }
    unscripted_writes!(
        set_brightness,
        set_hdr,
        set_refresh_rate,
        set_color_profile,
        apply_profile
    );
}
async fn wait_flag(flag: &AtomicBool) {
    timeout(Duration::from_secs(5), async {
        while !flag.load(Ordering::Acquire) {
            sleep(Duration::from_millis(1)).await;
        }
    })
    .await
    .unwrap();
}
fn release(gate: &Arc<(Mutex<bool>, Condvar)>) {
    let (lock, event) = &**gate;
    *lock.lock().unwrap() = true;
    event.notify_all();
}
#[tokio::test]
async fn stop_disconnect_deadline_and_origin_loss_retain_owner_until_blocking_read_completes() {
    for reason in ["stop", "disconnect", "deadline", "origin"] {
        let mut supplier = Blocker::new();
        let started = supplier.started.clone();
        let canceled = supplier.canceled.clone();
        let done = supplier.done.clone();
        let gate = supplier.gate.clone();
        let (mut server, mut client) = tokio::io::duplex(8192);
        client.write_all(&query()).await.unwrap();
        let (signal, mut stop) = watch::channel(false);
        let origin_live = Cell::new(true);
        let owned = Cell::new(true);
        let (result, ()) = tokio::join!(
            async {
                let result = serve(
                    &mut server,
                    &mut stop,
                    &mut supplier,
                    |_| Ok(()),
                    || {
                        if origin_live.get() {
                            Ok(())
                        } else {
                            Err(io::Error::from_raw_os_error(5))
                        }
                    },
                )
                .await;
                owned.set(false);
                result
            },
            async {
                wait_flag(&started).await;
                match reason {
                    "stop" => {
                        signal.send(true).unwrap();
                    }
                    "disconnect" => {
                        drop(client);
                    }
                    "origin" => origin_live.set(false),
                    "deadline" => (),
                    _ => unreachable!(),
                }
                // A deadline may take 5s; give the injected cancellation watcher
                // enough wall time while the blocking call stays held independently.
                timeout(Duration::from_secs(8), async {
                    while !canceled.load(Ordering::Acquire) {
                        sleep(Duration::from_millis(1)).await;
                    }
                })
                .await
                .unwrap();
                assert!(owned.get());
                assert!(!done.load(Ordering::Acquire));
                release(&gate);
            },
        );
        assert!(done.load(Ordering::Acquire));
        assert!(!owned.get());
        if reason == "origin" {
            assert!(matches!(result, Err(ServeError::Fatal(_))));
        } else {
            result.unwrap();
        }
    }
}

#[tokio::test]
async fn composed_stop_and_origin_loss_join_blocking_supplier_and_sibling() {
    for origin_loss in [false, true] {
        let mut supplier = Blocker::new();
        let started = supplier.started.clone();
        let gate = supplier.gate.clone();
        let done = supplier.done.clone();
        let canceled = supplier.canceled.clone();
        let owned = Cell::new(true);
        let sibling_done = Cell::new(false);
        let (signal, mut shutdown) = watch::channel(false);
        let (mut server, mut client) = tokio::io::duplex(8192);
        client.write_all(&query()).await.unwrap();
        let (result, ()) = tokio::join!(
            async {
                use super::super::feature_lifetime::{feature, run_all};
                let result = run_all(
                    async {
                        stopped(&mut shutdown).await;
                        if origin_loss {
                            Err(io::Error::other("injected Origin loss"))
                        } else {
                            Ok(())
                        }
                    },
                    std::future::pending(),
                    vec![
                        feature(|mut stop| {
                            let sibling_done = &sibling_done;
                            async move {
                                stopped(&mut stop).await;
                                sibling_done.set(true);
                                Ok(())
                            }
                        }),
                        feature(|mut stop| async move {
                            serve(&mut server, &mut stop, &mut supplier, |_| Ok(()), || Ok(()))
                                .await
                                .map_err(|error| match error {
                                    ServeError::Fatal(error) | ServeError::Connection(error) => {
                                        error
                                    }
                                })
                        }),
                    ],
                )
                .await;
                owned.set(false);
                result
            },
            async {
                wait_flag(&started).await;
                signal.send(true).unwrap();
                wait_flag(&canceled).await;
                assert!(owned.get() && sibling_done.get());
                assert!(!done.load(Ordering::Acquire));
                release(&gate);
            },
        );
        assert_eq!(result.is_err(), origin_loss);
        assert!(!owned.get() && sibling_done.get());
        assert!(done.load(Ordering::Acquire));
    }
}
#[tokio::test]
async fn refused_peer_then_valid_peer_survives_but_same_kind_origin_failure_is_fatal() {
    for origin_failure in [false, true] {
        let mut supplier = Script {
            outcome: Some(observation::fixture_query("available")),
            calls: 0,
        };
        let mut servers = std::collections::VecDeque::new();
        let mut clients = Vec::new();
        for _ in 0..2 {
            let (server, mut client) = tokio::io::duplex(8192);
            client.write_all(&query()).await.unwrap();
            servers.push_back(server);
            clients.push(client);
        }
        let admissions = Cell::new(0);
        let accepts = Cell::new(0);
        let checks = Cell::new(0);
        let (signal, mut stop) = watch::channel(false);
        let (result, ()) = tokio::join!(
            run_connections(
                &mut stop,
                &mut supplier,
                || {
                    accepts.set(accepts.get() + 1);
                    let next = servers.pop_front();
                    async move {
                        match next {
                            Some(server) => Ok(server),
                            None => std::future::pending().await,
                        }
                    }
                },
                |_| {
                    admissions.set(admissions.get() + 1);
                    if admissions.get() == 1 {
                        Err(io::Error::from_raw_os_error(5))
                    } else {
                        Ok(())
                    }
                },
                || {
                    checks.set(checks.get() + 1);
                    if origin_failure && checks.get() == 2 {
                        Err(io::Error::from_raw_os_error(5))
                    } else {
                        Ok(())
                    }
                }
            ),
            async {
                if origin_failure {
                    return;
                }
                let mut first = Vec::new();
                clients[0].read_to_end(&mut first).await.unwrap();
                assert!(first.is_empty());
                let body = read_frame(&mut clients[1]).await.unwrap();
                assert_eq!(serde_json::from_slice::<Value>(&body).unwrap()["status"], 0);
                signal.send(true).unwrap();
            },
        );
        if origin_failure {
            let error = result.unwrap_err();
            let mut events = Vec::new();
            diagnostics::terminal(&error, |e| events.push(e));
            assert!(matches!(
                events.as_slice(),
                [
                    openblade_platform_windows::agent_diagnostics::Event::Origin(
                        io::ErrorKind::PermissionDenied,
                        Some(5)
                    )
                ]
            ));
            assert_eq!(accepts.get(), 1);
            assert_eq!(supplier.calls, 0);
        } else {
            result.unwrap();
            assert_eq!(supplier.calls, 1);
            assert!(accepts.get() >= 2);
        }
    }
}

fn admitted_large_snapshot(target_body: usize) -> Snapshot {
    let mut state = serde_json::to_value(observation::fixture_query("available")).unwrap();
    state["activeColorProfileId"] = Value::Null;
    state["colorProfiles"] = json!(
        (0..16)
            .map(|i| json!({
                "id":format!("profile-{i:02}.icm"),"name":"Profile","isActive":false,
            }))
            .collect::<Vec<_>>()
    );
    let initial = frame(ID, 0, None, serde_json::from_value(state.clone()).unwrap()).unwrap();
    let mut remaining = target_body.checked_sub(initial.len() - 4).unwrap();
    for profile in state["colorProfiles"].as_array_mut().unwrap() {
        let name = profile["name"].as_str().unwrap();
        let add = remaining.min(4096 - name.len());
        profile["name"] = json!(format!("{name}{}", "p".repeat(add)));
        remaining -= add;
    }
    assert_eq!(remaining, 0, "fixture must fit admitted count/text bounds");
    let snapshot: Snapshot = serde_json::from_value(state).unwrap();
    assert!(snapshot.valid());
    snapshot
}

#[tokio::test]
async fn admitted_large_and_maximum_frames_use_production_dispatcher_and_decoder_corpus() {
    for (name, body_bytes) in [
        ("admitted-large-16384", 16384 - 4),
        ("admitted-max-65540", MAX_FRAME as usize),
    ] {
        let snapshot = admitted_large_snapshot(body_bytes);
        let expected = serde_json::to_value(&snapshot).unwrap();
        let mut supplier = Script {
            outcome: Some(snapshot),
            calls: 0,
        };
        let (result, bytes, _) = exchange(&mut supplier, &query()).await;
        result.unwrap();
        assert_eq!(bytes.len(), body_bytes + 4);
        assert_eq!(
            i32::from_le_bytes(bytes[..4].try_into().unwrap()) as usize,
            body_bytes
        );
        let response: Value = serde_json::from_slice(&bytes[4..]).unwrap();
        assert_eq!(response["state"]["isAvailable"], true);
        assert_eq!(response["state"], expected);
        capture(name, &bytes, &expected);
    }
}

#[allow(unsafe_code)]
fn private_pipe_endpoint() -> (openblade_windows_host::pipe::Endpoint, String) {
    let mut id = windows_sys::core::GUID {
        data1: 0,
        data2: 0,
        data3: 0,
        data4: [0; 8],
    };
    // Generates only a random test identifier, without observing any supplier.
    assert_eq!(
        unsafe { windows_sys::Win32::System::Com::CoCreateGuid(&mut id) },
        0
    );
    let tail: String = id.data4[2..].iter().map(|b| format!("{b:02x}")).collect();
    let guid = format!(
        "{:08x}-{:04x}-{:04x}-{:02x}{:02x}-{tail}",
        id.data1, id.data2, id.data3, id.data4[0], id.data4[1]
    );
    let name = format!(r"\\.\pipe\OpenBlade.Test.{guid}");
    (Endpoint::new(name.clone(), 2).unwrap(), name)
}

// A separate owned thread closes an unread peer within 8s even if native
// cancellation stalls the current-thread runtime. It is joined on every exit.
struct UnreadPeer {
    close: std::sync::mpsc::SyncSender<()>,
    worker: Option<std::thread::JoinHandle<()>>,
}
impl UnreadPeer {
    fn open(name: &str, request: Option<Vec<u8>>) -> Self {
        use std::{io::Write, os::windows::fs::OpenOptionsExt};
        use windows_sys::Win32::Storage::FileSystem::SECURITY_IDENTIFICATION;
        let name = name.to_owned();
        let (close, closed) = std::sync::mpsc::sync_channel(1);
        let (ready, opened) = std::sync::mpsc::sync_channel(1);
        let worker = std::thread::spawn(move || {
            let outcome = (|| -> io::Result<std::fs::File> {
                let mut peer = std::fs::OpenOptions::new()
                    .read(true)
                    .write(true)
                    .security_qos_flags(SECURITY_IDENTIFICATION)
                    .open(name)?;
                if let Some(request) = request {
                    peer.write_all(&request)?;
                }
                Ok(peer)
            })();
            match outcome {
                Ok(peer) => {
                    let _ = ready.send(Ok(()));
                    let _ = closed.recv_timeout(Duration::from_secs(8));
                    drop(peer);
                }
                Err(error) => {
                    let _ = ready.send(Err(error));
                }
            }
        });
        let owner = Self {
            close,
            worker: Some(worker),
        };
        opened
            .recv_timeout(Duration::from_secs(2))
            .unwrap()
            .unwrap();
        owner
    }
}
impl Drop for UnreadPeer {
    fn drop(&mut self) {
        let _ = self.close.try_send(());
        if let Some(worker) = self.worker.take() {
            worker.join().unwrap();
        }
    }
}
#[tokio::test]
async fn owned_private_pipe_unread_admitted_frame_retires_then_normal_peer_receives_full_frame() {
    use tokio::net::windows::named_pipe::ClientOptions;
    use windows_sys::Win32::Storage::FileSystem::SECURITY_IDENTIFICATION;
    for body_bytes in [16384 - 4, MAX_FRAME as usize] {
        let (endpoint, name) = private_pipe_endpoint();
        let claim = endpoint.claim().unwrap();
        let mut server = endpoint.listen().unwrap();
        // The fixed name is private to this test and this client is owned here.
        let unread = UnreadPeer::open(&name, Some(query()));
        timeout(Duration::from_secs(2), server.connect())
            .await
            .unwrap()
            .unwrap();
        let (_signal, mut stop) = watch::channel(false);
        let mut supplier = Script {
            outcome: Some(admitted_large_snapshot(body_bytes)),
            calls: 0,
        };
        let began = std::time::Instant::now();
        let result = serve(
            &mut server,
            &mut stop,
            &mut supplier,
            |pipe| endpoint.identity.authorize(pipe),
            || Ok(()),
        )
        .await;
        assert!(
            matches!(result, Err(ServeError::Connection(_))),
            "an unread pending write must meet its deadline"
        );
        // Dropping the connection cancels and positively retires only its own
        // native write before releasing the counted instance. Keep peer unread.
        drop(server);
        assert!(began.elapsed() < Duration::from_secs(4));
        let mut next = endpoint.next_listener().await.unwrap();
        let mut reader = ClientOptions::new()
            .security_qos_flags(SECURITY_IDENTIFICATION)
            .open(&name)
            .unwrap();
        reader.write_all(&query()).await.unwrap();
        timeout(Duration::from_secs(2), next.connect())
            .await
            .unwrap()
            .unwrap();
        let snapshot = admitted_large_snapshot(body_bytes);
        let expected = frame(
            ID,
            0,
            None,
            serde_json::from_value(serde_json::to_value(&snapshot).unwrap()).unwrap(),
        )
        .unwrap();
        let mut supplier = Script {
            outcome: Some(snapshot),
            calls: 0,
        };
        let (closed, after_close) = tokio::sync::oneshot::channel();
        let (sent, received) = tokio::join!(
            async {
                let result = serve(
                    &mut next,
                    &mut stop,
                    &mut supplier,
                    |pipe| endpoint.identity.authorize(pipe),
                    || Ok(()),
                )
                .await;
                drop(next);
                let _ = closed.send(());
                result
            },
            async {
                let mut bytes = vec![0; expected.len()];
                let split = bytes.len() - 4096;
                timeout(Duration::from_secs(3), async {
                    reader.read_exact(&mut bytes[..split]).await.unwrap();
                    // Leave the tail unread until the server has retired its
                    // completed write and closed. Success must preserve it.
                    after_close.await.unwrap();
                    reader.read_exact(&mut bytes[split..]).await.unwrap();
                })
                .await
                .unwrap();
                bytes
            },
        );
        sent.unwrap();
        assert_eq!(
            received, expected,
            "normal close must preserve every response byte"
        );
        drop(reader);
        drop(unread);
        drop(claim);
        // Prove all counted server handles (including writes) retired, without
        // relying on a no-op flush or a new protocol acknowledgement.
        sleep(Duration::from_millis(50)).await;
        let fresh = tokio::net::windows::named_pipe::ServerOptions::new()
            .first_pipe_instance(true)
            .reject_remote_clients(true)
            .max_instances(2)
            .create(&name)
            .unwrap();
        drop(fresh);
    }
}

#[tokio::test]
async fn owned_pending_write_cancellation_releases_slot_and_preserves_bounded_write_contract() {
    let (endpoint, name) = private_pipe_endpoint();
    let claim = endpoint.claim().unwrap();
    let mut server = endpoint.listen().unwrap();
    let peer = UnreadPeer::open(&name, None);
    timeout(Duration::from_secs(2), server.connect())
        .await
        .unwrap()
        .unwrap();
    let body = frame(ID, 0, None, admitted_large_snapshot(MAX_FRAME as usize)).unwrap();
    let mut write = Box::pin(server.write_all(&body));
    std::future::poll_fn(|cx| match write.as_mut().poll(cx) {
        std::task::Poll::Pending => std::task::Poll::Ready(()),
        std::task::Poll::Ready(result) => {
            panic!("unread full frame unexpectedly completed: {result:?}")
        }
    })
    .await;
    // Equivalent to a Stop-selected losing write future: no write completion
    // was reported, and the connection still owns its native storage.
    drop(write);
    let began = std::time::Instant::now();
    drop(server);
    assert!(began.elapsed() < Duration::from_secs(2));
    let mut next = endpoint.next_listener().await.unwrap();
    assert_eq!(
        openblade_windows_host::pipe::MAX_WRITE_BYTES,
        256 * 1024 + 4
    );
    let invalid = vec![0; openblade_windows_host::pipe::MAX_WRITE_BYTES + 1];
    let error = next.write_all(&invalid).await.unwrap_err();
    assert_eq!(error.kind(), io::ErrorKind::InvalidInput);
    drop(next);
    drop(peer);
    drop(claim);
    sleep(Duration::from_millis(50)).await;
    let fresh = tokio::net::windows::named_pipe::ServerOptions::new()
        .first_pipe_instance(true)
        .reject_remote_clients(true)
        .max_instances(2)
        .create(&name)
        .unwrap();
    drop(fresh);
}

/// Selected manual brightness writes through the production dispatcher.
#[cfg(feature = "agent-display-brightness-write-v1")]
mod brightness_writes {
    use super::*;
    use recovery::Status;

    struct Writes {
        outcome: Option<(Status, Option<&'static str>, Snapshot)>,
        received: Vec<SetBrightness>,
        queries: usize,
    }
    impl Writes {
        fn new(status: Status, error: Option<&'static str>, state: Snapshot) -> Self {
            Self {
                outcome: Some((status, error, state)),
                received: Vec::new(),
                queries: 0,
            }
        }
    }
    impl Supplier for Writes {
        async fn acquire(&mut self, _canceled: Arc<AtomicBool>) -> io::Result<Snapshot> {
            self.queries += 1;
            Ok(Snapshot::unavailable("unused"))
        }
        async fn set_brightness(
            &mut self,
            request: SetBrightness,
            _canceled: Arc<AtomicBool>,
        ) -> io::Result<WriteOutcome> {
            self.received.push(request);
            let (status, error, state) = self.outcome.take().expect("one write");
            Ok(WriteOutcome {
                status,
                error,
                state,
            })
        }
        unscripted_writes!(set_hdr, set_refresh_rate, set_color_profile, apply_profile);
    }
    fn set(expected: Value, percent: Value) -> Vec<u8> {
        request(json!({
            "operation":"setBrightness","version":2,"requestId":ID,
            "expectedPowerSource":expected,"percent":percent,
        }))
    }
    fn capture_write(name: &str, frame: &[u8], status: Status, error: Option<&str>, state: &Value) {
        capture_row(
            json!({"case":name,"expectedStatus":status as u8,"expectedError":error,
            "expectedState":state,"frame":frame}),
        );
    }

    #[tokio::test]
    async fn every_write_status_reaches_the_managed_response_shape() {
        use brightness_write::{CHANGED_POWER, CHECKPOINT, PAUSED, UNAVAILABLE, UNCONFIRMED};
        let state = || observation::fixture_query("brightness-hundred");
        for (case, status, error) in [
            ("write-succeeded", Status::Succeeded, None),
            ("write-blocked-paused", Status::Blocked, Some(PAUSED)),
            (
                "write-blocked-checkpoint",
                Status::Blocked,
                Some(CHECKPOINT),
            ),
            ("write-blocked-power", Status::Blocked, Some(CHANGED_POWER)),
            ("write-failed", Status::Failed, Some(UNAVAILABLE)),
            (
                "write-indeterminate",
                Status::Indeterminate,
                Some(UNCONFIRMED),
            ),
        ] {
            let expected = serde_json::to_value(state()).unwrap();
            let mut supplier = Writes::new(status, error, state());
            // Managed polymorphic order: discriminator first, then members.
            let (result, bytes, admissions) =
                exchange(&mut supplier, &set(json!(1), json!(64))).await;
            result.unwrap();
            assert_eq!(admissions, 2);
            assert_eq!(supplier.queries, 0);
            assert_eq!(
                supplier.received,
                [SetBrightness {
                    expected_power_source: 1,
                    percent: 64
                }]
            );
            let body: Value = serde_json::from_slice(&bytes[4..]).unwrap();
            assert_eq!(body["requestId"], ID);
            assert_eq!(body["status"], status as u8);
            assert_eq!(body["error"], json!(error));
            assert_eq!(body["state"], expected);
            capture_write(case, &bytes, status, error, &expected);
        }
    }

    #[tokio::test]
    async fn invalid_write_members_and_other_mutations_reach_no_supplier() {
        let mut inputs = vec![
            set(json!(4), json!(50)),
            set(json!(-1), json!(50)),
            set(json!(0), json!(101)),
            set(json!(0), json!(-1)),
            set(json!(0), json!(50.5)),
            set(json!(0), json!("50")),
            set(json!("PluggedIn"), json!(50)),
            set(Value::Null, json!(50)),
            set(json!(0), Value::Null),
            request(json!({"version":2,"requestId":ID,"operation":"setBrightness","percent":50})),
            request(
                json!({"version":2,"requestId":ID,"operation":"setBrightness","expectedPowerSource":0}),
            ),
            request(
                json!({"version":2,"requestId":ID,"operation":"setBrightness",
                "expectedPowerSource":0,"percent":50,"enabled":true}),
            ),
            request(
                json!({"version":1,"requestId":ID,"operation":"setBrightness",
                "expectedPowerSource":0,"percent":50}),
            ),
            request(json!({"version":2,"requestId":ID,"operation":"query","percent":50})),
            request(
                json!({"version":2,"requestId":ID,"operation":"applyProfile",
                "expectedPowerSource":0,"settings":{}}),
            ),
        ];
        // Builds without the refresh-rate write refuse it entirely.
        if !cfg!(feature = "agent-display-refresh-write-v1") {
            inputs.push(request(
                json!({"version":2,"requestId":ID,"operation":"setRefreshRate",
            "expectedPowerSource":0,"refreshRateKey":"fixed-240"}),
            ));
        }
        inputs.push(raw_request(
            format!(
                r#"{{"operation":"setBrightness","version":2,"requestId":"{ID}","expectedPowerSource":0,"percent":50,"percent":60}}"#
            )
            .as_bytes(),
        ));
        // A valid setHdr reaches no brightness-only supplier.
        if !cfg!(feature = "agent-display-hdr-write-v1") {
            inputs.push(request(
                json!({"version":2,"requestId":ID,"operation":"setHdr",
            "expectedPowerSource":0,"enabled":true}),
            ));
        }
        for input in inputs {
            let mut supplier = Writes::new(Status::Succeeded, None, Snapshot::unavailable("x"));
            let (result, bytes, _) = exchange(&mut supplier, &input).await;
            assert!(matches!(result, Err(ServeError::Connection(_))));
            assert!(bytes.is_empty());
            assert!(supplier.received.is_empty());
            assert_eq!(supplier.queries, 0);
        }
    }

    /// A write that only finishes after its gate opens, recording whether
    /// cancellation was requested and whether it completed.
    struct HeldWrite {
        gate: Arc<(Mutex<bool>, Condvar)>,
        canceled: Arc<AtomicBool>,
        recorded: Arc<AtomicBool>,
        started: Arc<AtomicBool>,
    }
    impl Supplier for HeldWrite {
        async fn acquire(&mut self, _canceled: Arc<AtomicBool>) -> io::Result<Snapshot> {
            unreachable!("write test received a query")
        }
        async fn set_brightness(
            &mut self,
            _request: SetBrightness,
            canceled: Arc<AtomicBool>,
        ) -> io::Result<WriteOutcome> {
            let gate = self.gate.clone();
            let seen = self.canceled.clone();
            let recorded = self.recorded.clone();
            let started = self.started.clone();
            tokio::task::spawn_blocking(move || {
                started.store(true, Ordering::Release);
                let (lock, event) = &*gate;
                let mut open = lock.lock().unwrap();
                while !*open {
                    open = event.wait(open).unwrap();
                }
                seen.store(canceled.load(Ordering::Acquire), Ordering::Release);
                recorded.store(true, Ordering::Release);
                Ok(WriteOutcome {
                    status: Status::Indeterminate,
                    error: Some(brightness_write::UNCONFIRMED),
                    state: Snapshot::unavailable("recorded"),
                })
            })
            .await
            .unwrap()
        }
        unscripted_writes!(set_hdr, set_refresh_rate, set_color_profile, apply_profile);
    }

    #[tokio::test]
    async fn stop_and_client_loss_join_the_recorded_write_and_send_nothing() {
        for client_loss in [false, true] {
            let mut supplier = HeldWrite {
                gate: Arc::new((Mutex::new(false), Condvar::new())),
                canceled: Arc::default(),
                recorded: Arc::default(),
                started: Arc::default(),
            };
            let gate = supplier.gate.clone();
            let started = supplier.started.clone();
            let recorded = supplier.recorded.clone();
            let (mut server, mut client) = tokio::io::duplex(1024);
            client.write_all(&set(json!(0), json!(40))).await.unwrap();
            let (sender, mut stop) = watch::channel(false);
            let (result, bytes) = tokio::join!(
                async {
                    let result =
                        serve(&mut server, &mut stop, &mut supplier, |_| Ok(()), || Ok(())).await;
                    drop(server);
                    result
                },
                async {
                    wait_flag(&started).await;
                    if client_loss {
                        client.write_all(&[0]).await.unwrap();
                    } else {
                        sender.send(true).unwrap();
                    }
                    // The server must still be waiting on the blocking write.
                    sleep(Duration::from_millis(50)).await;
                    assert!(!recorded.load(Ordering::Acquire));
                    release(&gate);
                    let mut bytes = Vec::new();
                    client.read_to_end(&mut bytes).await.unwrap();
                    bytes
                },
            );
            result.unwrap();
            assert!(bytes.is_empty(), "an abandoned write sends no reply");
            assert!(supplier.recorded.load(Ordering::Acquire));
            assert!(supplier.canceled.load(Ordering::Acquire));
        }
    }

    #[tokio::test]
    async fn origin_loss_during_a_write_joins_the_record_then_stops_the_endpoint() {
        let mut supplier = HeldWrite {
            gate: Arc::new((Mutex::new(false), Condvar::new())),
            canceled: Arc::default(),
            recorded: Arc::default(),
            started: Arc::default(),
        };
        let gate = supplier.gate.clone();
        let started = supplier.started.clone();
        let recorded = supplier.recorded.clone();
        let lost = Arc::new(AtomicBool::new(false));
        let (mut server, mut client) = tokio::io::duplex(1024);
        client.write_all(&set(json!(1), json!(40))).await.unwrap();
        let (_sender, mut stop) = watch::channel(false);
        let verify = {
            let lost = lost.clone();
            move || {
                if lost.load(Ordering::Acquire) {
                    Err(io::Error::other("injected Origin loss"))
                } else {
                    Ok(())
                }
            }
        };
        let (result, bytes) = tokio::join!(
            async {
                let result = serve(&mut server, &mut stop, &mut supplier, |_| Ok(()), verify).await;
                drop(server);
                result
            },
            async {
                wait_flag(&started).await;
                lost.store(true, Ordering::Release);
                // Origin polling notices the loss but must keep the write owned.
                sleep(ORIGIN_POLL * 2).await;
                assert!(!recorded.load(Ordering::Acquire));
                release(&gate);
                let mut bytes = Vec::new();
                client.read_to_end(&mut bytes).await.unwrap();
                bytes
            },
        );
        assert!(matches!(result, Err(ServeError::Fatal(_))));
        assert!(bytes.is_empty());
        assert!(supplier.recorded.load(Ordering::Acquire));
        assert!(supplier.canceled.load(Ordering::Acquire));
    }
}

/// Selected manual HDR writes through the production dispatcher.
#[cfg(feature = "agent-display-hdr-write-v1")]
mod hdr_writes {
    use super::*;
    use hdr_write::{CANCELED, NOT_SUPPORTED, PANEL_CHANGED, PAUSED, STALE, UNCONFIRMED, rejected};
    use manual_write::{MULTIPLE, NO_INTERNAL};
    use recovery::Status;
    use std::borrow::Cow;
    use write::{CHANGED_POWER, CHECKPOINT, STALE_AFTER, UNCONFIRMED_POWER};

    struct Writes {
        outcome: Option<(Status, Option<Cow<'static, str>>, Snapshot)>,
        received: Vec<SetHdr>,
        queries: usize,
    }
    impl Writes {
        fn new(status: Status, error: Option<Cow<'static, str>>, state: Snapshot) -> Self {
            Self {
                outcome: Some((status, error, state)),
                received: Vec::new(),
                queries: 0,
            }
        }
    }
    impl Supplier for Writes {
        async fn acquire(&mut self, _canceled: Arc<AtomicBool>) -> io::Result<Snapshot> {
            self.queries += 1;
            Ok(Snapshot::unavailable("unused"))
        }
        unscripted_writes!(
            set_brightness,
            set_refresh_rate,
            set_color_profile,
            apply_profile
        );
        async fn set_hdr(
            &mut self,
            request: SetHdr,
            _canceled: Arc<AtomicBool>,
        ) -> io::Result<ManualWriteOutcome> {
            self.received.push(request);
            let (status, error, state) = self.outcome.take().expect("one write");
            Ok(ManualWriteOutcome {
                status,
                error,
                state,
            })
        }
    }
    fn set(expected: Value, enabled: Value) -> Vec<u8> {
        request(json!({
            "operation":"setHdr","version":2,"requestId":ID,
            "expectedPowerSource":expected,"enabled":enabled,
        }))
    }
    fn capture_write(name: &str, frame: &[u8], status: Status, error: Option<&str>, state: &Value) {
        capture_row(
            json!({"case":name,"expectedStatus":status as u8,"expectedError":error,
            "expectedState":state,"frame":frame}),
        );
    }

    #[tokio::test]
    async fn every_hdr_status_reaches_the_managed_response_shape() {
        let state = || observation::fixture_query("available");
        let text = |error: &'static str| Some(Cow::Borrowed(error));
        let cases: [(&str, Status, Option<Cow<'static, str>>); 14] = [
            ("hdr-write-succeeded", Status::Succeeded, None),
            ("hdr-write-blocked-paused", Status::Blocked, text(PAUSED)),
            (
                "hdr-write-blocked-checkpoint",
                Status::Blocked,
                text(CHECKPOINT),
            ),
            (
                "hdr-write-blocked-unconfirmed-power",
                Status::Blocked,
                text(UNCONFIRMED_POWER),
            ),
            (
                "hdr-write-blocked-power",
                Status::Blocked,
                text(CHANGED_POWER),
            ),
            ("hdr-write-blocked-stale", Status::Blocked, text(STALE)),
            (
                "hdr-write-blocked-panel",
                Status::Blocked,
                text(PANEL_CHANGED),
            ),
            (
                "hdr-write-blocked-canceled",
                Status::Blocked,
                text(CANCELED),
            ),
            (
                "hdr-write-failed-unsupported",
                Status::Failed,
                text(NOT_SUPPORTED),
            ),
            (
                "hdr-write-failed-no-panel",
                Status::Failed,
                text(NO_INTERNAL),
            ),
            ("hdr-write-failed-multiple", Status::Failed, text(MULTIPLE)),
            (
                "hdr-write-indeterminate-rejected",
                Status::Indeterminate,
                Some(rejected(87)),
            ),
            (
                "hdr-write-indeterminate-unconfirmed",
                Status::Indeterminate,
                text(UNCONFIRMED),
            ),
            (
                "hdr-write-indeterminate-stale",
                Status::Indeterminate,
                text(STALE_AFTER),
            ),
        ];
        for (index, (case, status, error)) in cases.into_iter().enumerate() {
            let enabled = index % 2 == 0;
            let expected = serde_json::to_value(state()).unwrap();
            let mut supplier = Writes::new(status, error.clone(), state());
            // Managed polymorphic order: discriminator first, then members.
            let (result, bytes, admissions) =
                exchange(&mut supplier, &set(json!(2), json!(enabled))).await;
            result.unwrap();
            assert_eq!(admissions, 2);
            assert_eq!(supplier.queries, 0);
            assert_eq!(
                supplier.received,
                [SetHdr {
                    expected_power_source: 2,
                    enabled
                }]
            );
            let body: Value = serde_json::from_slice(&bytes[4..]).unwrap();
            assert_eq!(body["requestId"], ID);
            assert_eq!(body["status"], status as u8);
            assert_eq!(body["error"], json!(error.as_deref()));
            assert_eq!(body["state"], expected);
            capture_write(case, &bytes, status, error.as_deref(), &expected);
        }
        assert_eq!(
            rejected(87),
            "Windows could not change HDR: Win32 error 87."
        );
    }

    #[tokio::test]
    async fn invalid_hdr_members_and_other_mutations_reach_no_supplier() {
        let mut inputs = vec![
            set(json!(4), json!(true)),
            set(json!(-1), json!(true)),
            set(json!(0.5), json!(true)),
            set(json!("PluggedIn"), json!(true)),
            set(Value::Null, json!(true)),
            set(json!(0), Value::Null),
            set(json!(0), json!("true")),
            set(json!(0), json!(1)),
            set(json!(0), json!(0)),
            set(json!(0), json!([true])),
            request(json!({"version":2,"requestId":ID,"operation":"setHdr","enabled":true})),
            request(json!({"version":2,"requestId":ID,"operation":"setHdr","expectedPowerSource":0})),
            request(json!({"version":2,"requestId":ID,"operation":"setHdr",
                "expectedPowerSource":0,"enabled":true,"percent":50})),
            request(json!({"version":1,"requestId":ID,"operation":"setHdr",
                "expectedPowerSource":0,"enabled":true})),
            request(json!({"version":2,"requestId":ID,"operation":"query","enabled":true})),
            request(json!({"version":2,"requestId":ID,"operation":"setBrightness",
                "expectedPowerSource":0,"enabled":true})),
            request(json!({"version":2,"requestId":ID,"operation":"setBrightness",
                "expectedPowerSource":0,"percent":50,"enabled":false})),
            request(json!({"version":2,"requestId":ID,"operation":"applyProfile",
                "expectedPowerSource":0,"settings":{}})),
            raw_request(
                format!(
                    r#"{{"operation":"setHdr","version":2,"requestId":"{ID}","expectedPowerSource":0,"enabled":true,"enabled":false}}"#
                )
                .as_bytes(),
            ),
        ];
        // Builds without the refresh-rate write refuse it entirely.
        if !cfg!(feature = "agent-display-refresh-write-v1") {
            inputs.push(request(
                json!({"version":2,"requestId":ID,"operation":"setRefreshRate",
            "expectedPowerSource":0,"refreshRateKey":"fixed-240"}),
            ));
        }
        // An HDR-only build has no brightness mutation at all.
        if !cfg!(feature = "agent-display-brightness-write-v1") {
            inputs.push(request(
                json!({"version":2,"requestId":ID,"operation":"setBrightness",
            "expectedPowerSource":0,"percent":50}),
            ));
        }
        // Builds without the color-profile write refuse it entirely.
        if !cfg!(feature = "agent-display-color-write-v1") {
            inputs.push(request(
                json!({"version":2,"requestId":ID,"operation":"setColorProfile",
            "expectedPowerSource":0,"colorProfileKey":"x"}),
            ));
        }
        for input in inputs {
            let mut supplier = Writes::new(Status::Succeeded, None, Snapshot::unavailable("x"));
            let (result, bytes, _) = exchange(&mut supplier, &input).await;
            assert!(matches!(result, Err(ServeError::Connection(_))));
            assert!(bytes.is_empty());
            assert!(supplier.received.is_empty());
            assert_eq!(supplier.queries, 0);
        }
    }

    /// An HDR write that only finishes after its gate opens, recording whether
    /// cancellation was requested and whether it completed.
    struct HeldWrite {
        gate: Arc<(Mutex<bool>, Condvar)>,
        canceled: Arc<AtomicBool>,
        recorded: Arc<AtomicBool>,
        started: Arc<AtomicBool>,
    }
    impl HeldWrite {
        fn new() -> Self {
            Self {
                gate: Arc::new((Mutex::new(false), Condvar::new())),
                canceled: Arc::default(),
                recorded: Arc::default(),
                started: Arc::default(),
            }
        }
    }
    impl Supplier for HeldWrite {
        async fn acquire(&mut self, _canceled: Arc<AtomicBool>) -> io::Result<Snapshot> {
            unreachable!("write test received a query")
        }
        unscripted_writes!(
            set_brightness,
            set_refresh_rate,
            set_color_profile,
            apply_profile
        );
        async fn set_hdr(
            &mut self,
            _request: SetHdr,
            canceled: Arc<AtomicBool>,
        ) -> io::Result<ManualWriteOutcome> {
            let gate = self.gate.clone();
            let seen = self.canceled.clone();
            let recorded = self.recorded.clone();
            let started = self.started.clone();
            tokio::task::spawn_blocking(move || {
                started.store(true, Ordering::Release);
                let (lock, event) = &*gate;
                let mut open = lock.lock().unwrap();
                while !*open {
                    open = event.wait(open).unwrap();
                }
                seen.store(canceled.load(Ordering::Acquire), Ordering::Release);
                recorded.store(true, Ordering::Release);
                Ok(ManualWriteOutcome {
                    status: Status::Indeterminate,
                    error: Some(Cow::Borrowed(UNCONFIRMED)),
                    state: Snapshot::unavailable("recorded"),
                })
            })
            .await
            .unwrap()
        }
    }

    #[tokio::test]
    async fn stop_and_client_loss_join_the_recorded_hdr_write_and_send_nothing() {
        for client_loss in [false, true] {
            let mut supplier = HeldWrite::new();
            let gate = supplier.gate.clone();
            let started = supplier.started.clone();
            let recorded = supplier.recorded.clone();
            let (mut server, mut client) = tokio::io::duplex(1024);
            client.write_all(&set(json!(0), json!(true))).await.unwrap();
            let (sender, mut stop) = watch::channel(false);
            let (result, bytes) = tokio::join!(
                async {
                    let result =
                        serve(&mut server, &mut stop, &mut supplier, |_| Ok(()), || Ok(())).await;
                    drop(server);
                    result
                },
                async {
                    wait_flag(&started).await;
                    if client_loss {
                        client.write_all(&[0]).await.unwrap();
                    } else {
                        sender.send(true).unwrap();
                    }
                    // The server must still be waiting on the blocking write.
                    sleep(Duration::from_millis(50)).await;
                    assert!(!recorded.load(Ordering::Acquire));
                    release(&gate);
                    let mut bytes = Vec::new();
                    client.read_to_end(&mut bytes).await.unwrap();
                    bytes
                },
            );
            result.unwrap();
            assert!(bytes.is_empty(), "an abandoned write sends no reply");
            assert!(supplier.recorded.load(Ordering::Acquire));
            assert!(supplier.canceled.load(Ordering::Acquire));
        }
    }

    #[tokio::test]
    async fn origin_loss_during_an_hdr_write_joins_the_record_then_stops_the_endpoint() {
        let mut supplier = HeldWrite::new();
        let gate = supplier.gate.clone();
        let started = supplier.started.clone();
        let recorded = supplier.recorded.clone();
        let lost = Arc::new(AtomicBool::new(false));
        let (mut server, mut client) = tokio::io::duplex(1024);
        client
            .write_all(&set(json!(1), json!(false)))
            .await
            .unwrap();
        let (_sender, mut stop) = watch::channel(false);
        let verify = {
            let lost = lost.clone();
            move || {
                if lost.load(Ordering::Acquire) {
                    Err(io::Error::other("injected Origin loss"))
                } else {
                    Ok(())
                }
            }
        };
        let (result, bytes) = tokio::join!(
            async {
                let result = serve(&mut server, &mut stop, &mut supplier, |_| Ok(()), verify).await;
                drop(server);
                result
            },
            async {
                wait_flag(&started).await;
                lost.store(true, Ordering::Release);
                // Origin polling notices the loss but must keep the write owned.
                sleep(ORIGIN_POLL * 2).await;
                assert!(!recorded.load(Ordering::Acquire));
                release(&gate);
                let mut bytes = Vec::new();
                client.read_to_end(&mut bytes).await.unwrap();
                bytes
            },
        );
        assert!(matches!(result, Err(ServeError::Fatal(_))));
        assert!(bytes.is_empty());
        assert!(supplier.recorded.load(Ordering::Acquire));
        assert!(supplier.canceled.load(Ordering::Acquire));
    }
}

/// Selected manual refresh-rate writes through the production dispatcher.
#[cfg(feature = "agent-display-refresh-write-v1")]
mod refresh_writes {
    use super::*;
    use manual_write::{MULTIPLE, NO_INTERNAL};
    use recovery::Status;
    use refresh_write::{
        CANCELED, PANEL_CHANGED, PAUSED, STALE, UNAVAILABLE, UNCONFIRMED, apply_failed,
        not_confirmed, validation_rejected,
    };
    use std::borrow::Cow;
    use write::{CHANGED_POWER, CHECKPOINT, STALE_AFTER, UNCONFIRMED_POWER};

    struct Writes {
        outcome: Option<(Status, Option<Cow<'static, str>>, Snapshot)>,
        received: Vec<SetRefreshRate>,
        queries: usize,
    }
    impl Writes {
        fn new(status: Status, error: Option<Cow<'static, str>>, state: Snapshot) -> Self {
            Self {
                outcome: Some((status, error, state)),
                received: Vec::new(),
                queries: 0,
            }
        }
    }
    impl Supplier for Writes {
        async fn acquire(&mut self, _canceled: Arc<AtomicBool>) -> io::Result<Snapshot> {
            self.queries += 1;
            Ok(Snapshot::unavailable("unused"))
        }
        unscripted_writes!(set_brightness, set_hdr, set_color_profile, apply_profile);
        async fn set_refresh_rate(
            &mut self,
            request: SetRefreshRate,
            _canceled: Arc<AtomicBool>,
        ) -> io::Result<ManualWriteOutcome> {
            self.received.push(request);
            let (status, error, state) = self.outcome.take().expect("one write");
            Ok(ManualWriteOutcome {
                status,
                error,
                state,
            })
        }
    }
    fn set(expected: Value, key: Value) -> Vec<u8> {
        request(json!({
            "operation":"setRefreshRate","version":2,"requestId":ID,
            "expectedPowerSource":expected,"refreshRateKey":key,
        }))
    }
    fn capture_write(name: &str, frame: &[u8], status: Status, error: Option<&str>, state: &Value) {
        capture_row(
            json!({"case":name,"expectedStatus":status as u8,"expectedError":error,
            "expectedState":state,"frame":frame}),
        );
    }

    #[tokio::test]
    async fn every_refresh_status_reaches_the_managed_response_shape() {
        let state = || observation::fixture_query("available");
        let text = |error: &'static str| Some(Cow::Borrowed(error));
        let cases: [(&str, Status, Option<Cow<'static, str>>); 17] = [
            ("refresh-write-succeeded", Status::Succeeded, None),
            (
                "refresh-write-blocked-paused",
                Status::Blocked,
                text(PAUSED),
            ),
            (
                "refresh-write-blocked-checkpoint",
                Status::Blocked,
                text(CHECKPOINT),
            ),
            (
                "refresh-write-blocked-unconfirmed-power",
                Status::Blocked,
                text(UNCONFIRMED_POWER),
            ),
            (
                "refresh-write-blocked-power",
                Status::Blocked,
                text(CHANGED_POWER),
            ),
            ("refresh-write-blocked-stale", Status::Blocked, text(STALE)),
            (
                "refresh-write-blocked-panel",
                Status::Blocked,
                text(PANEL_CHANGED),
            ),
            (
                "refresh-write-blocked-canceled",
                Status::Blocked,
                text(CANCELED),
            ),
            (
                "refresh-write-failed-unavailable",
                Status::Failed,
                text(UNAVAILABLE),
            ),
            (
                "refresh-write-failed-validation",
                Status::Failed,
                Some(validation_rejected(87)),
            ),
            // Deliberate deviation: managed PrepareRefresh throws before
            // PrepareRefreshAfterSnapshot and its dispatcher reports Indeterminate;
            // nothing can have been submitted, so the native writer reports Failed.
            (
                "refresh-write-failed-no-panel",
                Status::Failed,
                text(NO_INTERNAL),
            ),
            (
                "refresh-write-failed-multiple",
                Status::Failed,
                text(MULTIPLE),
            ),
            (
                "refresh-write-indeterminate-apply",
                Status::Indeterminate,
                Some(apply_failed(1610)),
            ),
            (
                "refresh-write-indeterminate-unconfirmed",
                Status::Indeterminate,
                text(UNCONFIRMED),
            ),
            (
                "refresh-write-indeterminate-readback",
                Status::Indeterminate,
                Some(not_confirmed(NO_INTERNAL)),
            ),
            (
                "refresh-write-indeterminate-stale",
                Status::Indeterminate,
                text(STALE_AFTER),
            ),
            (
                "refresh-write-indeterminate-unrefreshed",
                Status::Indeterminate,
                text(UNCONFIRMED),
            ),
        ];
        for (index, (case, status, error)) in cases.into_iter().enumerate() {
            let key = if index % 2 == 0 {
                "fixed-240"
            } else {
                "dynamic-60-240"
            };
            let state = if case.ends_with("-unrefreshed") {
                Snapshot::unavailable(write::UNREFRESHED)
            } else {
                state()
            };
            let expected = serde_json::to_value(&state).unwrap();
            let mut supplier = Writes::new(status, error.clone(), state);
            // Managed polymorphic order: discriminator first, then members.
            let (result, bytes, admissions) =
                exchange(&mut supplier, &set(json!(2), json!(key))).await;
            result.unwrap();
            assert_eq!(admissions, 2);
            assert_eq!(supplier.queries, 0);
            assert_eq!(
                supplier.received,
                [SetRefreshRate {
                    expected_power_source: 2,
                    key: key.into(),
                }]
            );
            let body: Value = serde_json::from_slice(&bytes[4..]).unwrap();
            assert_eq!(body["requestId"], ID);
            assert_eq!(body["status"], status as u8);
            assert_eq!(body["error"], json!(error.as_deref()));
            assert_eq!(body["state"], expected);
            capture_write(case, &bytes, status, error.as_deref(), &expected);
        }
        assert_eq!(
            validation_rejected(87),
            "The refresh-rate configuration could not be prepared: Windows rejected the \
             requested display configuration: Win32 error 87."
        );
        assert_eq!(
            apply_failed(1610),
            "The refresh-rate change failed: Windows could not apply the requested display \
             configuration: Win32 error 1610."
        );
    }

    #[tokio::test]
    async fn managed_key_limits_reach_the_supplier_exactly() {
        // ValidKey counts UTF-16 units: 512 surrogate pairs are exactly 1024.
        for key in [
            "fixed-240".to_string(),
            "\u{1F600}".repeat(512),
            "a".repeat(1024),
        ] {
            let mut supplier = Writes::new(Status::Succeeded, None, Snapshot::unavailable("x"));
            let (result, bytes, _) = exchange(&mut supplier, &set(json!(0), json!(key))).await;
            result.unwrap();
            assert!(!bytes.is_empty());
            assert_eq!(supplier.received.len(), 1);
            assert_eq!(supplier.received[0].key, key);
        }
    }

    #[tokio::test]
    async fn invalid_refresh_members_and_other_mutations_reach_no_supplier() {
        let id = ID;
        let mut inputs = vec![
            set(json!(4), json!("fixed-240")),
            set(json!(-1), json!("fixed-240")),
            set(json!(0.5), json!("fixed-240")),
            set(json!("PluggedIn"), json!("fixed-240")),
            set(Value::Null, json!("fixed-240")),
            set(json!(0), Value::Null),
            set(json!(0), json!("")),
            set(json!(0), json!("   ")),
            set(json!(0), json!(" fixed-240")),
            set(json!(0), json!("fixed-240 ")),
            set(json!(0), json!("fixed-240\n")),
            set(json!(0), json!("fixed\u{1}240")),
            set(json!(0), json!("fixed\u{85}240")),
            set(json!(0), json!("\u{1F600}".repeat(513))),
            set(json!(0), json!("a".repeat(1025))),
            set(json!(0), json!(240)),
            set(json!(0), json!(["fixed-240"])),
            set(json!(0), json!({"key":"fixed-240"})),
            request(json!({"version":2,"requestId":ID,"operation":"setRefreshRate",
                "refreshRateKey":"fixed-240"})),
            request(json!({"version":2,"requestId":ID,"operation":"setRefreshRate",
                "expectedPowerSource":0})),
            request(json!({"version":2,"requestId":ID,"operation":"setRefreshRate",
                "expectedPowerSource":0,"refreshRateKey":"fixed-240","percent":50})),
            request(json!({"version":2,"requestId":ID,"operation":"setRefreshRate",
                "expectedPowerSource":0,"refreshRateKey":"fixed-240","enabled":true})),
            request(json!({"version":2,"requestId":ID,"operation":"setRefreshRate",
                "expectedPowerSource":0,"refreshRateKey":"fixed-240","colorProfileKey":"x"})),
            request(json!({"version":1,"requestId":ID,"operation":"setRefreshRate",
                "expectedPowerSource":0,"refreshRateKey":"fixed-240"})),
            request(json!({"version":2,"requestId":ID,"operation":"query",
                "refreshRateKey":"fixed-240"})),
            request(json!({"version":2,"requestId":ID,"operation":"setHdr",
                "expectedPowerSource":0,"enabled":true,"refreshRateKey":"fixed-240"})),
            request(json!({"version":2,"requestId":ID,"operation":"setBrightness",
                "expectedPowerSource":0,"percent":50,"refreshRateKey":"fixed-240"})),
            request(json!({"version":2,"requestId":ID,"operation":"applyProfile",
                "expectedPowerSource":0,"settings":{}})),
            raw_request(
                format!(
                    r#"{{"operation":"setRefreshRate","version":2,"requestId":"{id}","expectedPowerSource":0,"refreshRateKey":"fixed-60","refreshRateKey":"fixed-240"}}"#
                )
                .as_bytes(),
            ),
            raw_request(
                format!(
                    r#"{{"version":2,"operation":"setRefreshRate","requestId":"{id}","expectedPowerSource":0,"refreshRateKey":"fixed-240"}}"#
                )
                .as_bytes(),
            ),
            raw_request(
                format!(
                    r#"{{"operation":{{"setRefreshRate":null}},"version":2,"requestId":"{id}","expectedPowerSource":0,"refreshRateKey":"fixed-240"}}"#
                )
                .as_bytes(),
            ),
        ];
        // Builds without the other writes refuse them entirely.
        if !cfg!(feature = "agent-display-hdr-write-v1") {
            inputs.push(request(
                json!({"version":2,"requestId":ID,"operation":"setHdr",
            "expectedPowerSource":0,"enabled":true}),
            ));
        }
        if !cfg!(feature = "agent-display-brightness-write-v1") {
            inputs.push(request(
                json!({"version":2,"requestId":ID,"operation":"setBrightness",
            "expectedPowerSource":0,"percent":50}),
            ));
        }
        // Builds without the color-profile write refuse it entirely.
        if !cfg!(feature = "agent-display-color-write-v1") {
            inputs.push(request(
                json!({"version":2,"requestId":ID,"operation":"setColorProfile",
            "expectedPowerSource":0,"colorProfileKey":"x"}),
            ));
        }
        for input in inputs {
            let mut supplier = Writes::new(Status::Succeeded, None, Snapshot::unavailable("x"));
            let (result, bytes, _) = exchange(&mut supplier, &input).await;
            assert!(matches!(result, Err(ServeError::Connection(_))));
            assert!(bytes.is_empty());
            assert!(supplier.received.is_empty());
            assert_eq!(supplier.queries, 0);
        }
    }

    /// A refresh write that only finishes after its gate opens, recording
    /// whether cancellation was requested and whether it completed.
    struct HeldWrite {
        gate: Arc<(Mutex<bool>, Condvar)>,
        canceled: Arc<AtomicBool>,
        recorded: Arc<AtomicBool>,
        started: Arc<AtomicBool>,
    }
    impl Supplier for HeldWrite {
        async fn acquire(&mut self, _canceled: Arc<AtomicBool>) -> io::Result<Snapshot> {
            unreachable!("write test received a query")
        }
        unscripted_writes!(set_brightness, set_hdr, set_color_profile, apply_profile);
        async fn set_refresh_rate(
            &mut self,
            _request: SetRefreshRate,
            canceled: Arc<AtomicBool>,
        ) -> io::Result<ManualWriteOutcome> {
            let gate = self.gate.clone();
            let seen = self.canceled.clone();
            let recorded = self.recorded.clone();
            let started = self.started.clone();
            tokio::task::spawn_blocking(move || {
                started.store(true, Ordering::Release);
                let (lock, event) = &*gate;
                let mut open = lock.lock().unwrap();
                while !*open {
                    open = event.wait(open).unwrap();
                }
                seen.store(canceled.load(Ordering::Acquire), Ordering::Release);
                recorded.store(true, Ordering::Release);
                Ok(ManualWriteOutcome {
                    status: Status::Indeterminate,
                    error: Some(Cow::Borrowed(UNCONFIRMED)),
                    state: Snapshot::unavailable("recorded"),
                })
            })
            .await
            .unwrap()
        }
    }

    #[tokio::test]
    async fn stop_and_client_loss_join_the_recorded_refresh_write_and_send_nothing() {
        for client_loss in [false, true] {
            let mut supplier = HeldWrite {
                gate: Arc::new((Mutex::new(false), Condvar::new())),
                canceled: Arc::default(),
                recorded: Arc::default(),
                started: Arc::default(),
            };
            let gate = supplier.gate.clone();
            let started = supplier.started.clone();
            let recorded = supplier.recorded.clone();
            let (mut server, mut client) = tokio::io::duplex(1024);
            client
                .write_all(&set(json!(0), json!("fixed-240")))
                .await
                .unwrap();
            let (sender, mut stop) = watch::channel(false);
            let (result, bytes) = tokio::join!(
                async {
                    let result =
                        serve(&mut server, &mut stop, &mut supplier, |_| Ok(()), || Ok(())).await;
                    drop(server);
                    result
                },
                async {
                    wait_flag(&started).await;
                    if client_loss {
                        client.write_all(&[0]).await.unwrap();
                    } else {
                        sender.send(true).unwrap();
                    }
                    // The server must still be waiting on the blocking write.
                    sleep(Duration::from_millis(50)).await;
                    assert!(!recorded.load(Ordering::Acquire));
                    release(&gate);
                    let mut bytes = Vec::new();
                    client.read_to_end(&mut bytes).await.unwrap();
                    bytes
                },
            );
            result.unwrap();
            assert!(bytes.is_empty(), "an abandoned write sends no reply");
            assert!(supplier.recorded.load(Ordering::Acquire));
            assert!(supplier.canceled.load(Ordering::Acquire));
        }
    }
}

#[cfg(feature = "agent-display-color-write-v1")]
mod color_writes {
    use super::*;
    use color_write::{CANCELED, HDR_ON, PANEL_CHANGED, PAUSED, STALE, UNAVAILABLE, UNCONFIRMED};
    use manual_write::{MULTIPLE, NO_INTERNAL};
    use recovery::Status;
    use std::borrow::Cow;
    use write::{CHANGED_POWER, CHECKPOINT, STALE_AFTER, UNCONFIRMED_POWER};

    const RESTORED: &str = "The color-profile change failed: Windows could not associate the \
                            color profile The previous color-profile configuration was restored.";
    const UNRESTORED: &str = "The color-profile change failed: Windows could not change the \
                              color-profile scope (Win32 error 5) The previous color-profile \
                              configuration could not be restored: Windows did not confirm \
                              removal of the color-profile association and default.";

    struct Writes {
        outcome: Option<(Status, Option<Cow<'static, str>>, Snapshot)>,
        received: Vec<SetColorProfile>,
        queries: usize,
    }
    impl Writes {
        fn new(status: Status, error: Option<Cow<'static, str>>, state: Snapshot) -> Self {
            Self {
                outcome: Some((status, error, state)),
                received: Vec::new(),
                queries: 0,
            }
        }
    }
    impl Supplier for Writes {
        async fn acquire(&mut self, _canceled: Arc<AtomicBool>) -> io::Result<Snapshot> {
            self.queries += 1;
            Ok(Snapshot::unavailable("unused"))
        }
        unscripted_writes!(set_brightness, set_hdr, set_refresh_rate, apply_profile);
        async fn set_color_profile(
            &mut self,
            request: SetColorProfile,
            _canceled: Arc<AtomicBool>,
        ) -> io::Result<ManualWriteOutcome> {
            self.received.push(request);
            let (status, error, state) = self.outcome.take().expect("one write");
            Ok(ManualWriteOutcome {
                status,
                error,
                state,
            })
        }
    }
    fn set(expected: Value, key: Value) -> Vec<u8> {
        request(json!({
            "operation":"setColorProfile","version":2,"requestId":ID,
            "expectedPowerSource":expected,"colorProfileKey":key,
        }))
    }
    fn capture_write(name: &str, frame: &[u8], status: Status, error: Option<&str>, state: &Value) {
        capture_row(
            json!({"case":name,"expectedStatus":status as u8,"expectedError":error,
            "expectedState":state,"frame":frame}),
        );
    }

    #[tokio::test]
    async fn every_color_status_reaches_the_managed_response_shape() {
        let state = || observation::fixture_query("available");
        let text = |error: &'static str| Some(Cow::Borrowed(error));
        let cases: [(&str, Status, Option<Cow<'static, str>>); 21] = [
            ("color-write-succeeded", Status::Succeeded, None),
            ("color-write-blocked-paused", Status::Blocked, text(PAUSED)),
            (
                "color-write-blocked-checkpoint",
                Status::Blocked,
                text(CHECKPOINT),
            ),
            (
                "color-write-blocked-unconfirmed-power",
                Status::Blocked,
                text(UNCONFIRMED_POWER),
            ),
            (
                "color-write-blocked-power",
                Status::Blocked,
                text(CHANGED_POWER),
            ),
            ("color-write-blocked-stale", Status::Blocked, text(STALE)),
            (
                "color-write-blocked-panel",
                Status::Blocked,
                text(PANEL_CHANGED),
            ),
            (
                "color-write-blocked-canceled",
                Status::Blocked,
                text(CANCELED),
            ),
            (
                "color-write-failed-unavailable",
                Status::Failed,
                text(UNAVAILABLE),
            ),
            ("color-write-failed-hdr-on", Status::Failed, text(HDR_ON)),
            // Deliberate deviation: managed reads a failed advanced-color
            // query as SDR; the native writer refuses unconfirmed SDR.
            (
                "color-write-failed-hdr-unknown",
                Status::Failed,
                text(observation::HDR_UNKNOWN),
            ),
            // Deliberate deviation: managed PrepareColorProfile throws before
            // its transaction and its dispatcher reports Indeterminate;
            // nothing can have been submitted, so the native writer reports Failed.
            (
                "color-write-failed-no-panel",
                Status::Failed,
                text(NO_INTERNAL),
            ),
            (
                "color-write-failed-multiple",
                Status::Failed,
                text(MULTIPLE),
            ),
            (
                "color-write-failed-read",
                Status::Failed,
                text("Windows could not determine the active color-profile scope"),
            ),
            (
                "color-write-failed-monitor",
                Status::Failed,
                text("Windows did not report one active monitor device for the internal display."),
            ),
            (
                "color-write-indeterminate-restored",
                Status::Indeterminate,
                text(RESTORED),
            ),
            (
                "color-write-indeterminate-unrestored",
                Status::Indeterminate,
                text(UNRESTORED),
            ),
            (
                "color-write-indeterminate-unconfirmed",
                Status::Indeterminate,
                text(UNCONFIRMED),
            ),
            (
                "color-write-indeterminate-unverified",
                Status::Indeterminate,
                Some(Cow::Owned(format!(
                    "{} {MULTIPLE}",
                    color_write::UNVERIFIED
                ))),
            ),
            (
                "color-write-indeterminate-stale",
                Status::Indeterminate,
                text(STALE_AFTER),
            ),
            (
                "color-write-indeterminate-unrefreshed",
                Status::Indeterminate,
                text(UNCONFIRMED),
            ),
        ];
        for (index, (case, status, error)) in cases.into_iter().enumerate() {
            let key = if index % 2 == 0 {
                "sRGB_CTR.icm"
            } else {
                r"C:\Windows\System32\spool\drivers\color\Native_CTR.icm"
            };
            let state = if case.ends_with("-unrefreshed") {
                Snapshot::unavailable(write::UNREFRESHED)
            } else {
                state()
            };
            let expected = serde_json::to_value(&state).unwrap();
            let mut supplier = Writes::new(status, error.clone(), state);
            let (result, bytes, admissions) =
                exchange(&mut supplier, &set(json!(1), json!(key))).await;
            result.unwrap();
            assert_eq!(admissions, 2);
            assert_eq!(supplier.queries, 0);
            assert_eq!(
                supplier.received,
                [SetColorProfile {
                    expected_power_source: 1,
                    key: key.into(),
                }]
            );
            let body: Value = serde_json::from_slice(&bytes[4..]).unwrap();
            assert_eq!(body["requestId"], ID);
            assert_eq!(body["status"], status as u8);
            assert_eq!(body["error"], json!(error.as_deref()));
            assert_eq!(body["state"], expected);
            capture_write(case, &bytes, status, error.as_deref(), &expected);
        }
    }

    #[tokio::test]
    async fn managed_key_limits_reach_the_supplier_exactly() {
        // ValidKey counts UTF-16 units: 512 surrogate pairs are exactly 1024.
        for key in [
            "sRGB_CTR.icm".to_string(),
            "Caf\u{e9} profile.icm".to_string(),
            "\u{1F600}".repeat(512),
            "a".repeat(1024),
        ] {
            let mut supplier = Writes::new(Status::Succeeded, None, Snapshot::unavailable("x"));
            let (result, bytes, _) = exchange(&mut supplier, &set(json!(0), json!(key))).await;
            result.unwrap();
            assert!(!bytes.is_empty());
            assert_eq!(supplier.received.len(), 1);
            assert_eq!(supplier.received[0].key, key);
        }
    }

    #[tokio::test]
    async fn invalid_color_members_and_other_mutations_reach_no_supplier() {
        let id = ID;
        let mut inputs = vec![
            set(json!(4), json!("sRGB_CTR.icm")),
            set(json!(-1), json!("sRGB_CTR.icm")),
            set(json!(0.5), json!("sRGB_CTR.icm")),
            set(json!("PluggedIn"), json!("sRGB_CTR.icm")),
            set(Value::Null, json!("sRGB_CTR.icm")),
            set(json!(0), Value::Null),
            set(json!(0), json!("")),
            set(json!(0), json!("   ")),
            set(json!(0), json!(" sRGB_CTR.icm")),
            set(json!(0), json!("sRGB_CTR.icm ")),
            set(json!(0), json!("sRGB_CTR.icm\n")),
            set(json!(0), json!("sRGB\u{1}CTR.icm")),
            set(json!(0), json!("sRGB\u{85}CTR.icm")),
            set(json!(0), json!("\u{1F600}".repeat(513))),
            set(json!(0), json!("a".repeat(1025))),
            set(json!(0), json!(1)),
            set(json!(0), json!(["sRGB_CTR.icm"])),
            set(json!(0), json!({"id":"sRGB_CTR.icm"})),
            request(json!({"version":2,"requestId":ID,"operation":"setColorProfile",
                "colorProfileKey":"sRGB_CTR.icm"})),
            request(json!({"version":2,"requestId":ID,"operation":"setColorProfile",
                "expectedPowerSource":0})),
            request(json!({"version":2,"requestId":ID,"operation":"setColorProfile",
                "expectedPowerSource":0,"colorProfileKey":"sRGB_CTR.icm","percent":50})),
            request(json!({"version":2,"requestId":ID,"operation":"setColorProfile",
                "expectedPowerSource":0,"colorProfileKey":"sRGB_CTR.icm","enabled":true})),
            request(json!({"version":2,"requestId":ID,"operation":"setColorProfile",
                "expectedPowerSource":0,"colorProfileKey":"sRGB_CTR.icm",
                "refreshRateKey":"fixed-240"})),
            request(json!({"version":1,"requestId":ID,"operation":"setColorProfile",
                "expectedPowerSource":0,"colorProfileKey":"sRGB_CTR.icm"})),
            request(json!({"version":2,"requestId":ID,"operation":"query",
                "colorProfileKey":"sRGB_CTR.icm"})),
            request(json!({"version":2,"requestId":ID,"operation":"setHdr",
                "expectedPowerSource":0,"enabled":true,"colorProfileKey":"sRGB_CTR.icm"})),
            request(json!({"version":2,"requestId":ID,"operation":"setBrightness",
                "expectedPowerSource":0,"percent":50,"colorProfileKey":"sRGB_CTR.icm"})),
            request(json!({"version":2,"requestId":ID,"operation":"applyProfile",
                "expectedPowerSource":0,"settings":{}})),
            raw_request(
                format!(
                    r#"{{"operation":"setColorProfile","version":2,"requestId":"{id}","expectedPowerSource":0,"colorProfileKey":"Native_CTR.icm","colorProfileKey":"sRGB_CTR.icm"}}"#
                )
                .as_bytes(),
            ),
            raw_request(
                format!(
                    r#"{{"version":2,"operation":"setColorProfile","requestId":"{id}","expectedPowerSource":0,"colorProfileKey":"sRGB_CTR.icm"}}"#
                )
                .as_bytes(),
            ),
            raw_request(
                format!(
                    r#"{{"operation":{{"setColorProfile":null}},"version":2,"requestId":"{id}","expectedPowerSource":0,"colorProfileKey":"sRGB_CTR.icm"}}"#
                )
                .as_bytes(),
            ),
        ];
        // Builds without the other writes refuse them entirely.
        if !cfg!(feature = "agent-display-hdr-write-v1") {
            inputs.push(request(
                json!({"version":2,"requestId":ID,"operation":"setHdr",
            "expectedPowerSource":0,"enabled":true}),
            ));
        }
        if !cfg!(feature = "agent-display-brightness-write-v1") {
            inputs.push(request(
                json!({"version":2,"requestId":ID,"operation":"setBrightness",
            "expectedPowerSource":0,"percent":50}),
            ));
        }
        if !cfg!(feature = "agent-display-refresh-write-v1") {
            inputs.push(request(
                json!({"version":2,"requestId":ID,"operation":"setRefreshRate",
            "expectedPowerSource":0,"refreshRateKey":"fixed-240"}),
            ));
        }
        for input in inputs {
            let mut supplier = Writes::new(Status::Succeeded, None, Snapshot::unavailable("x"));
            let (result, bytes, _) = exchange(&mut supplier, &input).await;
            assert!(matches!(result, Err(ServeError::Connection(_))));
            assert!(bytes.is_empty());
            assert!(supplier.received.is_empty());
            assert_eq!(supplier.queries, 0);
        }
    }

    /// A color write that only finishes after its gate opens, recording
    /// whether cancellation was requested and whether it completed.
    struct HeldWrite {
        gate: Arc<(Mutex<bool>, Condvar)>,
        canceled: Arc<AtomicBool>,
        recorded: Arc<AtomicBool>,
        started: Arc<AtomicBool>,
    }
    impl Supplier for HeldWrite {
        async fn acquire(&mut self, _canceled: Arc<AtomicBool>) -> io::Result<Snapshot> {
            unreachable!("write test received a query")
        }
        unscripted_writes!(set_brightness, set_hdr, set_refresh_rate, apply_profile);
        async fn set_color_profile(
            &mut self,
            _request: SetColorProfile,
            canceled: Arc<AtomicBool>,
        ) -> io::Result<ManualWriteOutcome> {
            let gate = self.gate.clone();
            let seen = self.canceled.clone();
            let recorded = self.recorded.clone();
            let started = self.started.clone();
            tokio::task::spawn_blocking(move || {
                started.store(true, Ordering::Release);
                let (lock, event) = &*gate;
                let mut open = lock.lock().unwrap();
                while !*open {
                    open = event.wait(open).unwrap();
                }
                seen.store(canceled.load(Ordering::Acquire), Ordering::Release);
                recorded.store(true, Ordering::Release);
                Ok(ManualWriteOutcome {
                    status: Status::Indeterminate,
                    error: Some(Cow::Borrowed(UNCONFIRMED)),
                    state: Snapshot::unavailable("recorded"),
                })
            })
            .await
            .unwrap()
        }
    }

    #[tokio::test]
    async fn stop_and_client_loss_join_the_recorded_color_write_and_send_nothing() {
        for client_loss in [false, true] {
            let mut supplier = HeldWrite {
                gate: Arc::new((Mutex::new(false), Condvar::new())),
                canceled: Arc::default(),
                recorded: Arc::default(),
                started: Arc::default(),
            };
            let gate = supplier.gate.clone();
            let started = supplier.started.clone();
            let recorded = supplier.recorded.clone();
            let (mut server, mut client) = tokio::io::duplex(1024);
            client
                .write_all(&set(json!(0), json!("sRGB_CTR.icm")))
                .await
                .unwrap();
            let (sender, mut stop) = watch::channel(false);
            let (result, bytes) = tokio::join!(
                async {
                    let result =
                        serve(&mut server, &mut stop, &mut supplier, |_| Ok(()), || Ok(())).await;
                    drop(server);
                    result
                },
                async {
                    wait_flag(&started).await;
                    if client_loss {
                        client.write_all(&[0]).await.unwrap();
                    } else {
                        sender.send(true).unwrap();
                    }
                    // The server must still be waiting on the blocking write.
                    sleep(Duration::from_millis(50)).await;
                    assert!(!recorded.load(Ordering::Acquire));
                    release(&gate);
                    let mut bytes = Vec::new();
                    client.read_to_end(&mut bytes).await.unwrap();
                    bytes
                },
            );
            result.unwrap();
            assert!(bytes.is_empty(), "an abandoned write sends no reply");
            assert!(supplier.recorded.load(Ordering::Acquire));
            assert!(supplier.canceled.load(Ordering::Acquire));
        }
    }
}

#[cfg(feature = "agent-display-profile-apply-v1")]
mod profile_applies {
    use super::*;
    use profile_apply::{PAUSED, Settings, UNAVAILABLE, UNCONFIRMED};
    use recovery::Status;
    use std::borrow::Cow;
    use write::{CHANGED_POWER, CHECKPOINT, STALE_AFTER, UNCONFIRMED_POWER};

    struct Writes {
        outcome: Option<(Status, Option<Cow<'static, str>>, Snapshot)>,
        received: Vec<ApplyProfile>,
        queries: usize,
    }
    impl Writes {
        fn new(status: Status, error: Option<Cow<'static, str>>, state: Snapshot) -> Self {
            Self {
                outcome: Some((status, error, state)),
                received: Vec::new(),
                queries: 0,
            }
        }
    }
    impl Supplier for Writes {
        async fn acquire(&mut self, _canceled: Arc<AtomicBool>) -> io::Result<Snapshot> {
            self.queries += 1;
            Ok(Snapshot::unavailable("unused"))
        }
        unscripted_writes!(set_brightness, set_hdr, set_refresh_rate, set_color_profile);
        async fn apply_profile(
            &mut self,
            request: ApplyProfile,
            _canceled: Arc<AtomicBool>,
        ) -> io::Result<ManualWriteOutcome> {
            self.received.push(request);
            let (status, error, state) = self.outcome.take().expect("one write");
            Ok(ManualWriteOutcome {
                status,
                error,
                state,
            })
        }
    }
    /// The managed codec's encoding of `DisplayProfileSettings`.
    fn settings(refresh: Value, hdr: Value, color: Value, brightness: Value) -> Value {
        let configured =
            !(refresh.is_null() && hdr.is_null() && color.is_null() && brightness.is_null());
        json!({"refreshRateKey":refresh,"hdrEnabled":hdr,"colorProfileKey":color,
            "brightnessPercent":brightness,"isConfigured":configured})
    }
    fn full() -> Value {
        settings(
            json!("fixed-240"),
            json!(true),
            json!("sRGB_CTR.icm"),
            json!(75),
        )
    }
    fn apply(expected: Value, settings: Value) -> Vec<u8> {
        request(json!({
            "operation":"applyProfile","version":2,"requestId":ID,
            "expectedPowerSource":expected,"settings":settings,
        }))
    }
    fn capture_write(name: &str, frame: &[u8], status: Status, error: Option<&str>, state: &Value) {
        capture_row(
            json!({"case":name,"expectedStatus":status as u8,"expectedError":error,
            "expectedState":state,"frame":frame}),
        );
    }

    #[tokio::test]
    async fn every_profile_status_reaches_the_managed_response_shape() {
        let state = || observation::fixture_query("available");
        let text = |error: &'static str| Some(Cow::Borrowed(error));
        let cases: [(&str, Status, Option<Cow<'static, str>>); 15] = [
            ("profile-succeeded", Status::Succeeded, None),
            ("profile-blocked-paused", Status::Blocked, text(PAUSED)),
            (
                "profile-blocked-checkpoint",
                Status::Blocked,
                text(CHECKPOINT),
            ),
            (
                "profile-blocked-unconfirmed-power",
                Status::Blocked,
                text(UNCONFIRMED_POWER),
            ),
            (
                "profile-blocked-power",
                Status::Blocked,
                text(CHANGED_POWER),
            ),
            (
                "profile-blocked-step-canceled",
                Status::Blocked,
                text(hdr_write::CANCELED),
            ),
            (
                "profile-failed-unavailable",
                Status::Failed,
                text(UNAVAILABLE),
            ),
            (
                "profile-failed-message",
                Status::Failed,
                Some(Cow::Owned("The internal display is not active.".into())),
            ),
            (
                "profile-failed-brightness",
                Status::Failed,
                text(brightness_write::UNAVAILABLE),
            ),
            (
                "profile-failed-unconfirmed",
                Status::Failed,
                text(UNCONFIRMED),
            ),
            (
                "profile-failed-step",
                Status::Failed,
                text(refresh_write::UNAVAILABLE),
            ),
            (
                "profile-indeterminate-unconfirmed",
                Status::Indeterminate,
                text(UNCONFIRMED),
            ),
            (
                "profile-indeterminate-step",
                Status::Indeterminate,
                text(color_write::UNCONFIRMED),
            ),
            (
                "profile-indeterminate-stale",
                Status::Indeterminate,
                text(STALE_AFTER),
            ),
            (
                "profile-indeterminate-unrefreshed",
                Status::Indeterminate,
                text(hdr_write::UNCONFIRMED),
            ),
        ];
        for (index, (case, status, error)) in cases.into_iter().enumerate() {
            let state = if case.ends_with("-unrefreshed") {
                Snapshot::unavailable(write::UNREFRESHED)
            } else {
                state()
            };
            let source = index as u8 % 3;
            let expected = serde_json::to_value(&state).unwrap();
            let mut supplier = Writes::new(status, error.clone(), state);
            let (result, bytes, admissions) =
                exchange(&mut supplier, &apply(json!(source), full())).await;
            result.unwrap();
            assert_eq!(admissions, 2);
            assert_eq!(supplier.queries, 0);
            assert_eq!(
                supplier.received,
                [ApplyProfile {
                    expected_power_source: source,
                    settings: Settings {
                        refresh_rate_key: Some("fixed-240".into()),
                        hdr_enabled: Some(true),
                        color_profile_key: Some("sRGB_CTR.icm".into()),
                        brightness_percent: Some(75),
                    },
                }]
            );
            let body: Value = serde_json::from_slice(&bytes[4..]).unwrap();
            assert_eq!(body["requestId"], ID);
            assert_eq!(body["status"], status as u8);
            assert_eq!(body["error"], json!(error.as_deref()));
            assert_eq!(body["state"], expected);
            capture_write(case, &bytes, status, error.as_deref(), &expected);
        }
    }

    #[tokio::test]
    async fn managed_settings_reach_the_supplier_exactly() {
        let cases = [
            (
                settings(Value::Null, Value::Null, Value::Null, Value::Null),
                Settings::default(),
            ),
            (
                settings(
                    json!("a".repeat(1024)),
                    Value::Null,
                    Value::Null,
                    Value::Null,
                ),
                Settings {
                    refresh_rate_key: Some("a".repeat(1024)),
                    ..Settings::default()
                },
            ),
            (
                settings(Value::Null, json!(false), Value::Null, Value::Null),
                Settings {
                    hdr_enabled: Some(false),
                    ..Settings::default()
                },
            ),
            (
                settings(
                    Value::Null,
                    Value::Null,
                    json!("\u{1F600}".repeat(512)),
                    Value::Null,
                ),
                Settings {
                    color_profile_key: Some("\u{1F600}".repeat(512)),
                    ..Settings::default()
                },
            ),
            (
                settings(Value::Null, Value::Null, Value::Null, json!(0)),
                Settings {
                    brightness_percent: Some(0),
                    ..Settings::default()
                },
            ),
            (
                settings(Value::Null, Value::Null, Value::Null, json!(100)),
                Settings {
                    brightness_percent: Some(100),
                    ..Settings::default()
                },
            ),
        ];
        for (source, (wire, expected)) in cases.into_iter().enumerate() {
            let source = source as u8 % 3;
            let mut supplier = Writes::new(Status::Succeeded, None, Snapshot::unavailable("x"));
            let (result, bytes, _) = exchange(&mut supplier, &apply(json!(source), wire)).await;
            result.unwrap();
            assert!(!bytes.is_empty());
            assert_eq!(
                supplier.received,
                [ApplyProfile {
                    expected_power_source: source,
                    settings: expected,
                }]
            );
        }
    }

    #[tokio::test]
    async fn invalid_profile_members_reach_no_supplier() {
        let id = ID;
        let with = |member: &str, value: Value| {
            let mut wire = full();
            wire[member] = value;
            wire
        };
        let without = |member: &str| {
            let mut wire = full();
            wire.as_object_mut().unwrap().remove(member);
            wire
        };
        let mut inputs = vec![
            apply(json!(3), full()),
            apply(json!(-1), full()),
            apply(json!(0.5), full()),
            apply(json!("PluggedIn"), full()),
            apply(Value::Null, full()),
            apply(json!(0), Value::Null),
            apply(json!(0), json!({})),
            apply(json!(0), json!([])),
            apply(json!(0), json!("settings")),
            apply(json!(0), with("refreshRateKey", json!(""))),
            apply(json!(0), with("refreshRateKey", json!("   "))),
            apply(json!(0), with("refreshRateKey", json!(" fixed-240"))),
            apply(json!(0), with("refreshRateKey", json!("fixed\u{7}240"))),
            apply(json!(0), with("refreshRateKey", json!("a".repeat(1025)))),
            apply(json!(0), with("refreshRateKey", json!(240))),
            apply(json!(0), with("colorProfileKey", json!(""))),
            apply(json!(0), with("colorProfileKey", json!("sRGB_CTR.icm "))),
            apply(
                json!(0),
                with("colorProfileKey", json!("\u{1F600}".repeat(513))),
            ),
            apply(json!(0), with("hdrEnabled", json!("true"))),
            apply(json!(0), with("hdrEnabled", json!(1))),
            apply(json!(0), with("brightnessPercent", json!(101))),
            apply(json!(0), with("brightnessPercent", json!(-1))),
            apply(json!(0), with("brightnessPercent", json!(50.5))),
            apply(json!(0), with("brightnessPercent", json!("50"))),
            apply(json!(0), with("isConfigured", Value::Null)),
            apply(json!(0), with("isConfigured", json!(false))),
            apply(
                json!(0),
                settings(Value::Null, Value::Null, Value::Null, Value::Null)
                    .as_object()
                    .map(|map| {
                        let mut map = map.clone();
                        map.insert("isConfigured".into(), json!(true));
                        Value::Object(map)
                    })
                    .unwrap(),
            ),
            apply(json!(0), with("extra", json!(1))),
            apply(json!(0), with("RefreshRateKey", json!("fixed-240"))),
            apply(json!(0), without("refreshRateKey")),
            apply(json!(0), without("hdrEnabled")),
            apply(json!(0), without("colorProfileKey")),
            apply(json!(0), without("brightnessPercent")),
            apply(json!(0), without("isConfigured")),
            request(
                json!({"version":2,"requestId":ID,"operation":"applyProfile",
                "settings":full()}),
            ),
            request(
                json!({"version":2,"requestId":ID,"operation":"applyProfile",
                "expectedPowerSource":0}),
            ),
            request(
                json!({"version":2,"requestId":ID,"operation":"applyProfile",
                "expectedPowerSource":0,"settings":full(),"percent":50}),
            ),
            request(
                json!({"version":2,"requestId":ID,"operation":"applyProfile",
                "expectedPowerSource":0,"settings":full(),"enabled":true}),
            ),
            request(
                json!({"version":2,"requestId":ID,"operation":"applyProfile",
                "expectedPowerSource":0,"settings":full(),"refreshRateKey":"fixed-240"}),
            ),
            request(
                json!({"version":2,"requestId":ID,"operation":"applyProfile",
                "expectedPowerSource":0,"settings":full(),"colorProfileKey":"sRGB_CTR.icm"}),
            ),
            request(
                json!({"version":1,"requestId":ID,"operation":"applyProfile",
                "expectedPowerSource":0,"settings":full()}),
            ),
            request(json!({"version":2,"requestId":ID,"operation":"query","settings":full()})),
            request(json!({"version":2,"requestId":ID,"operation":"setHdr",
                "expectedPowerSource":0,"enabled":true,"settings":full()})),
            request(
                json!({"version":2,"requestId":ID,"operation":"setBrightness",
                "expectedPowerSource":0,"percent":50,"settings":full()}),
            ),
            request(
                json!({"version":2,"requestId":ID,"operation":"setRefreshRate",
                "expectedPowerSource":0,"refreshRateKey":"fixed-240","settings":full()}),
            ),
            request(
                json!({"version":2,"requestId":ID,"operation":"setColorProfile",
                "expectedPowerSource":0,"colorProfileKey":"sRGB_CTR.icm","settings":full()}),
            ),
        ];
        let settings = full().to_string();
        for raw in [
            format!(
                r#"{{"operation":"applyProfile","version":2,"requestId":"{id}","expectedPowerSource":0,"settings":{settings},"settings":{settings}}}"#
            ),
            format!(
                r#"{{"version":2,"operation":"applyProfile","requestId":"{id}","expectedPowerSource":0,"settings":{settings}}}"#
            ),
            format!(
                r#"{{"operation":{{"applyProfile":null}},"version":2,"requestId":"{id}","expectedPowerSource":0,"settings":{settings}}}"#
            ),
            format!(
                r#"{{"operation":"applyProfile","version":2,"requestId":"{id}","expectedPowerSource":0,"settings":{{"refreshRateKey":"fixed-240","refreshRateKey":null,"hdrEnabled":null,"colorProfileKey":null,"brightnessPercent":null,"isConfigured":true}}}}"#
            ),
        ] {
            inputs.push(raw_request(raw.as_bytes()));
        }
        for input in inputs {
            let mut supplier = Writes::new(Status::Succeeded, None, Snapshot::unavailable("x"));
            let (result, bytes, _) = exchange(&mut supplier, &input).await;
            assert!(
                matches!(result, Err(ServeError::Connection(_))),
                "{}",
                String::from_utf8_lossy(&input[4..])
            );
            assert!(bytes.is_empty());
            assert!(supplier.received.is_empty());
            assert_eq!(supplier.queries, 0);
        }
    }

    /// A profile that only finishes after its gate opens, recording whether
    /// cancellation was requested and whether it completed.
    struct HeldWrite {
        gate: Arc<(Mutex<bool>, Condvar)>,
        canceled: Arc<AtomicBool>,
        recorded: Arc<AtomicBool>,
        started: Arc<AtomicBool>,
    }
    impl Supplier for HeldWrite {
        async fn acquire(&mut self, _canceled: Arc<AtomicBool>) -> io::Result<Snapshot> {
            unreachable!("write test received a query")
        }
        unscripted_writes!(set_brightness, set_hdr, set_refresh_rate, set_color_profile);
        async fn apply_profile(
            &mut self,
            _request: ApplyProfile,
            canceled: Arc<AtomicBool>,
        ) -> io::Result<ManualWriteOutcome> {
            let gate = self.gate.clone();
            let seen = self.canceled.clone();
            let recorded = self.recorded.clone();
            let started = self.started.clone();
            tokio::task::spawn_blocking(move || {
                started.store(true, Ordering::Release);
                let (lock, event) = &*gate;
                let mut open = lock.lock().unwrap();
                while !*open {
                    open = event.wait(open).unwrap();
                }
                seen.store(canceled.load(Ordering::Acquire), Ordering::Release);
                recorded.store(true, Ordering::Release);
                Ok(ManualWriteOutcome {
                    status: Status::Indeterminate,
                    error: Some(Cow::Borrowed(UNCONFIRMED)),
                    state: Snapshot::unavailable("recorded"),
                })
            })
            .await
            .unwrap()
        }
    }

    #[tokio::test]
    async fn stop_and_client_loss_join_the_recorded_profile_and_send_nothing() {
        for client_loss in [false, true] {
            let mut supplier = HeldWrite {
                gate: Arc::new((Mutex::new(false), Condvar::new())),
                canceled: Arc::default(),
                recorded: Arc::default(),
                started: Arc::default(),
            };
            let gate = supplier.gate.clone();
            let started = supplier.started.clone();
            let recorded = supplier.recorded.clone();
            let (mut server, mut client) = tokio::io::duplex(1024);
            client.write_all(&apply(json!(0), full())).await.unwrap();
            let (sender, mut stop) = watch::channel(false);
            let (result, bytes) = tokio::join!(
                async {
                    let result =
                        serve(&mut server, &mut stop, &mut supplier, |_| Ok(()), || Ok(())).await;
                    drop(server);
                    result
                },
                async {
                    wait_flag(&started).await;
                    if client_loss {
                        client.write_all(&[0]).await.unwrap();
                    } else {
                        sender.send(true).unwrap();
                    }
                    // The server must still be waiting on the blocking write.
                    sleep(Duration::from_millis(50)).await;
                    assert!(!recorded.load(Ordering::Acquire));
                    release(&gate);
                    let mut bytes = Vec::new();
                    client.read_to_end(&mut bytes).await.unwrap();
                    bytes
                },
            );
            result.unwrap();
            assert!(bytes.is_empty(), "an abandoned profile sends no reply");
            assert!(supplier.recorded.load(Ordering::Acquire));
            assert!(supplier.canceled.load(Ordering::Acquire));
        }
    }
}

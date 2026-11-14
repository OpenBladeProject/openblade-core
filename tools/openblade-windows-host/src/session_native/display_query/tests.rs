use super::*;
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
}
fn request(body: Value) -> Vec<u8> {
    raw_request(&serde_json::to_vec(&body).unwrap())
}
fn raw_request(body: &[u8]) -> Vec<u8> {
    [(body.len() as i32).to_le_bytes().as_slice(), body].concat()
}
fn query() -> Vec<u8> {
    request(json!({"version":2,"requestId":ID,"operation":"query"}))
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
    if let Some(path) = std::env::var_os("OPENBLADE_NATIVE_AGENT_DISPLAY_FRAMES") {
        use std::io::Write;
        static LOCK: Mutex<()> = Mutex::new(());
        let _lock = LOCK.lock().unwrap();
        let mut file = std::fs::OpenOptions::new()
            .create(true)
            .append(true)
            .open(path)
            .unwrap();
        writeln!(
            file,
            "{}",
            json!({"case":name,"expectedStatus":0,"expectedState":expected,"frame":frame})
        )
        .unwrap();
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
        assert!(body["state"]["brightnessPercent"].is_null());
        assert_eq!(body["state"]["supportedBrightnessLevels"], json!([]));
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
    let initial = frame(ID, serde_json::from_value(state.clone()).unwrap()).unwrap();
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
    use windows_sys::Win32::Storage::FileSystem::SECURITY_IDENTIFICATION;
    let (endpoint, name) = private_pipe_endpoint();
    let claim = endpoint.claim().unwrap();
    let mut server = endpoint.listen().unwrap();
    let peer = UnreadPeer::open(&name, None);
    timeout(Duration::from_secs(2), server.connect())
        .await
        .unwrap()
        .unwrap();
    let body = frame(ID, admitted_large_snapshot(MAX_FRAME as usize)).unwrap();
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

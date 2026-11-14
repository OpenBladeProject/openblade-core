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

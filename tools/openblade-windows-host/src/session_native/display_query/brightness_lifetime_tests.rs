use super::*;
use openblade_platform_windows::{
    brightness::{
        Acknowledgement, CallBudget, MethodRow, NativeFailure, ReadingRow, Submission,
        TopologyFailure, WindowsBrightnessWorker, WmiBrightnessNative,
    },
    display::{
        InternalDisplayTopology,
        scripted::{RefreshTiming, Scenario},
    },
};
use std::sync::atomic::AtomicUsize;

#[derive(Default)]
struct Gate(Mutex<bool>, Condvar);
impl Gate {
    fn wait(&self) {
        let mut open = self.0.lock().unwrap();
        while !*open {
            open = self.1.wait(open).unwrap();
        }
    }
    fn release(&self) {
        *self.0.lock().unwrap() = true;
        self.1.notify_all();
    }
}
#[derive(Default)]
struct Control {
    call: Gate,
    local_cleanup: Gate,
    owner_cleanup: Gate,
    started: AtomicBool,
    cleaning: AtomicBool,
    cleaned: AtomicBool,
    dropping: AtomicBool,
    dropped: AtomicBool,
    calls: AtomicUsize,
    panic: AtomicBool,
}
struct ReleaseAll(Arc<Control>);
impl Drop for ReleaseAll {
    fn drop(&mut self) {
        self.0.call.release();
        self.0.local_cleanup.release();
        self.0.owner_cleanup.release();
    }
}
// Models a request-local COM enumerator/VARIANT destructor separately from
// the connection/apartment owner. Both execute on the real brightness thread.
struct LocalCleanup(Arc<Control>);
impl Drop for LocalCleanup {
    fn drop(&mut self) {
        self.0.cleaning.store(true, Ordering::Release);
        self.0.local_cleanup.wait();
        self.0.cleaned.store(true, Ordering::Release);
    }
}
struct HeldWmi(Arc<Control>);
impl WmiBrightnessNative for HeldWmi {
    type Method = ();
    fn topology(&mut self, _: &CallBudget<'_>) -> Result<InternalDisplayTopology, TopologyFailure> {
        Ok(Scenario::Available
            .topology_with_hdr(false, RefreshTiming::default(), false)
            .unwrap())
    }
    fn active_readings(
        &mut self,
        _: &CallBudget<'_>,
        _: usize,
    ) -> Result<Vec<ReadingRow>, NativeFailure> {
        let call = self.0.calls.fetch_add(1, Ordering::AcqRel);
        assert!(
            !self.0.panic.load(Ordering::Acquire),
            "injected WMI worker panic"
        );
        if call == 0 {
            self.0.started.store(true, Ordering::Release);
            let _cleanup = LocalCleanup(self.0.clone());
            self.0.call.wait();
        } else {
            assert!(
                self.0.cleaned.load(Ordering::Acquire),
                "native reads overlapped cleanup"
            );
        }
        let InternalDisplayTopology::Single(panel) = self.topology_dummy() else {
            panic!("panel")
        };
        let path = panel
            .monitor_name
            .unwrap()
            .device_path
            .unwrap()
            .into_string()
            .unwrap();
        let prefix = path
            .strip_prefix(r"\\?\")
            .unwrap_or(&path)
            .split('#')
            .take(3)
            .collect::<Vec<_>>()
            .join(r"\");
        Ok(vec![ReadingRow {
            instance: Some(format!("{prefix}_0")),
            percent: Some(50),
            levels: Some(vec![100, 0, 50]),
            data_failure: None,
        }])
    }
    fn active_methods(
        &mut self,
        _: &CallBudget<'_>,
        _: usize,
    ) -> Result<Vec<MethodRow<()>>, NativeFailure> {
        panic!("query cannot enumerate setters")
    }
    fn set_brightness(
        &mut self,
        _: (),
        _: u8,
        _: &mut Submission<'_>,
    ) -> Result<Acknowledgement, NativeFailure> {
        panic!("query cannot mutate")
    }
}
impl HeldWmi {
    fn topology_dummy(&self) -> InternalDisplayTopology {
        Scenario::Available
            .topology_with_hdr(false, RefreshTiming::default(), false)
            .unwrap()
    }
}
impl Drop for HeldWmi {
    fn drop(&mut self) {
        self.0.dropping.store(true, Ordering::Release);
        self.0.owner_cleanup.wait();
        self.0.dropped.store(true, Ordering::Release);
    }
}

struct OwnedSupplier {
    owner: observation::SharedBrightness,
}
impl OwnedSupplier {
    fn held(control: Arc<Control>) -> Self {
        let worker = WindowsBrightnessWorker::start_scripted(move || HeldWmi(control)).unwrap();
        Self {
            owner: Arc::new(Mutex::new(observation::BrightnessOwner::with_worker(
                worker,
            ))),
        }
    }
}
impl Supplier for OwnedSupplier {
    async fn acquire(&mut self, canceled: Arc<AtomicBool>) -> io::Result<Snapshot> {
        let owner = self.owner.clone();
        tokio::task::spawn_blocking(move || observation::fixture_query_owned(owner, &canceled))
            .await
            .unwrap()
    }
    unscripted_writes!(
        set_brightness,
        set_hdr,
        set_refresh_rate,
        set_color_profile,
        apply_profile
    );
    async fn drain(&mut self) -> io::Result<()> {
        observation::drain_brightness(self.owner.clone()).await
    }
}
type Incoming = Arc<tokio::sync::Mutex<tokio::sync::mpsc::Receiver<tokio::io::DuplexStream>>>;
fn incoming() -> (tokio::sync::mpsc::Sender<tokio::io::DuplexStream>, Incoming) {
    let (send, recv) = tokio::sync::mpsc::channel(1);
    (send, Arc::new(tokio::sync::Mutex::new(recv)))
}
async fn next(incoming: Incoming) -> io::Result<tokio::io::DuplexStream> {
    incoming
        .lock()
        .await
        .recv()
        .await
        .ok_or_else(|| io::Error::other("injected listener closed"))
}
async fn reply(
    send: &tokio::sync::mpsc::Sender<tokio::io::DuplexStream>,
    limit: Duration,
) -> (Value, Vec<u8>) {
    let (server, mut client) = tokio::io::duplex(8192);
    client.write_all(&query()).await.unwrap();
    send.send(server).await.unwrap();
    let body = timeout(limit, read_frame(&mut client))
        .await
        .expect("partial query missed deadline")
        .expect("partial query closed without a frame");
    let bytes = [
        (body.len() as i32).to_le_bytes().as_slice(),
        body.as_slice(),
    ]
    .concat();
    (serde_json::from_slice(&body).unwrap(), bytes)
}
fn partial(body: &Value) {
    assert_eq!(body["status"], 0);
    let state = &body["state"];
    assert_eq!(state["isAvailable"], true);
    assert_eq!(state["width"], 2560);
    assert_eq!(state["currentRefreshKey"], "fixed-240");
    assert_eq!(state["activeColorProfileId"], "Synthetic.icm");
    assert_eq!(state["hdrSupported"], true);
    assert!(state["brightnessPercent"].is_null());
    assert_eq!(state["supportedBrightnessLevels"], json!([]));
}

// Offline-only verification hook: execute the explicitly supplied production
// managed codec oracle while request-local native cleanup remains held.
async fn decode_while_held(case: &str, bytes: &[u8], body: &Value, control: &Control) {
    let Some(oracle) = std::env::var_os("OPENBLADE_NATIVE_DISPLAY_LIVE_ORACLE") else {
        return;
    };
    let dir = std::path::PathBuf::from(
        std::env::var_os("OPENBLADE_NATIVE_DISPLAY_LIVE_RECEIPTS").unwrap(),
    );
    assert!(control.started.load(Ordering::Acquire) && !control.cleaned.load(Ordering::Acquire));
    assert_eq!(control.calls.load(Ordering::Acquire), 1);
    let input = dir.join(format!("live-{case}.jsonl"));
    std::fs::write(
        &input,
        format!(
            "{}\n",
            json!({"case":case,"expectedStatus":0,"expectedState":body["state"],"frame":bytes})
        ),
    )
    .unwrap();
    let output = tokio::task::spawn_blocking(move || {
        std::process::Command::new("dotnet")
            .arg(oracle)
            .arg(input)
            .output()
    })
    .await
    .unwrap()
    .unwrap();
    let stdout = String::from_utf8(output.stdout).unwrap();
    let stderr = String::from_utf8(output.stderr).unwrap();
    std::fs::write(
        dir.join(format!("live-{case}.json")),
        format!(
            "{}\n",
            json!({
                "case":case,"exitCode":output.status.code(),"stdout":stdout,"stderr":stderr,
                "nativeCalls":control.calls.load(Ordering::Acquire),
                "nativeCallStillHeld":!control.cleaning.load(Ordering::Acquire),
                "localCleanupStarted":control.cleaning.load(Ordering::Acquire),
                "localCleanupFinished":control.cleaned.load(Ordering::Acquire),
                "ownerDropFinished":control.dropped.load(Ordering::Acquire),
            })
        ),
    )
    .unwrap();
    assert!(
        output.status.success(),
        "live managed decode failed: {stdout} {stderr}"
    );
    assert!(!control.cleaned.load(Ordering::Acquire) && !control.dropped.load(Ordering::Acquire));
    assert_eq!(control.calls.load(Ordering::Acquire), 1);
}

#[tokio::test]
async fn timed_out_wmi_keeps_partial_queries_available_through_cleanup_then_recovers() {
    let control = Arc::new(Control::default());
    let _release = ReleaseAll(control.clone());
    control.owner_cleanup.release();
    let mut supplier = OwnedSupplier::held(control.clone());
    let (send, incoming) = incoming();
    let (signal, mut stop) = watch::channel(false);
    let (result, ()) = tokio::join!(
        run_connections(
            &mut stop,
            &mut supplier,
            || next(incoming.clone()),
            |_| Ok(()),
            || Ok(())
        ),
        async {
            let (body, bytes) = reply(&send, Duration::from_secs(4)).await;
            partial(&body);
            assert!(control.started.load(Ordering::Acquire));
            assert!(!control.cleaning.load(Ordering::Acquire));
            capture("timeout-partial-held-call", &bytes, &body["state"]);
            decode_while_held("timeout-partial-held-call", &bytes, &body, &control).await;
            let (body, bytes) = reply(&send, Duration::from_secs(2)).await;
            partial(&body);
            assert_eq!(control.calls.load(Ordering::Acquire), 1);
            capture("busy-partial-held-call", &bytes, &body["state"]);
            decode_while_held("busy-partial-held-call", &bytes, &body, &control).await;
            control.call.release();
            wait_flag(&control.cleaning).await;
            let (body, bytes) = reply(&send, Duration::from_secs(2)).await;
            partial(&body);
            assert!(!control.cleaned.load(Ordering::Acquire));
            assert_eq!(control.calls.load(Ordering::Acquire), 1);
            capture("busy-partial-held-local-cleanup", &bytes, &body["state"]);
            decode_while_held("busy-partial-held-local-cleanup", &bytes, &body, &control).await;
            control.local_cleanup.release();
            let (body, bytes) = timeout(Duration::from_secs(3), async {
                loop {
                    let (body, bytes) = reply(&send, Duration::from_secs(2)).await;
                    if body["state"]["brightnessPercent"] == 50 {
                        break (body, bytes);
                    }
                    partial(&body);
                    sleep(Duration::from_millis(1)).await;
                }
            })
            .await
            .unwrap();
            assert_eq!(control.calls.load(Ordering::Acquire), 2);
            assert_eq!(
                body["state"]["supportedBrightnessLevels"],
                json!([0, 50, 100])
            );
            capture(
                "recovered-after-confirmed-local-cleanup",
                &bytes,
                &body["state"],
            );
            signal.send(true).unwrap();
        },
    );
    result.unwrap();
    assert!(control.dropped.load(Ordering::Acquire));
}

#[tokio::test]
async fn pair_stop_and_origin_failure_join_pending_call_local_cleanup_and_owner_drop() {
    for origin_loss in [false, true] {
        let control = Arc::new(Control::default());
        let _release = ReleaseAll(control.clone());
        let mut supplier = OwnedSupplier::held(control.clone());
        let (send, incoming) = incoming();
        let (signal, mut shutdown) = watch::channel(false);
        let owned = Cell::new(true);
        let sibling_done = Cell::new(false);
        let (result, ()) = tokio::join!(
            async {
                use super::super::super::feature_lifetime::{feature, run_all};
                let result = run_all(
                    async {
                        stopped(&mut shutdown).await;
                        if origin_loss {
                            Err(diagnostics::origin(io::Error::from_raw_os_error(5)))
                        } else {
                            Ok(())
                        }
                    },
                    std::future::pending(),
                    vec![
                        feature(|mut stop| {
                            let done = &sibling_done;
                            async move {
                                stopped(&mut stop).await;
                                done.set(true);
                                Ok(())
                            }
                        }),
                        feature(|mut stop| async move {
                            run_connections(
                                &mut stop,
                                &mut supplier,
                                || next(incoming.clone()),
                                |_| Ok(()),
                                || Ok(()),
                            )
                            .await
                        }),
                    ],
                )
                .await;
                owned.set(false);
                result
            },
            async {
                let (body, bytes) = reply(&send, Duration::from_secs(4)).await;
                partial(&body);
                assert!(!control.cleaned.load(Ordering::Acquire));
                let case = if origin_loss {
                    "partial-before-origin-loss-drain"
                } else {
                    "partial-before-stop-drain"
                };
                capture(case, &bytes, &body["state"]);
                decode_while_held(case, &bytes, &body, &control).await;
                signal.send(true).unwrap();
                timeout(Duration::from_secs(3), async {
                    while !sibling_done.get() {
                        tokio::task::yield_now().await;
                    }
                })
                .await
                .unwrap();
                assert!(owned.get() && !control.dropped.load(Ordering::Acquire));
                control.call.release();
                wait_flag(&control.cleaning).await;
                assert!(owned.get());
                control.local_cleanup.release();
                wait_flag(&control.dropping).await;
                assert!(owned.get() && !control.dropped.load(Ordering::Acquire));
                assert_eq!(control.calls.load(Ordering::Acquire), 1);
                control.owner_cleanup.release();
            },
        );
        assert_eq!(result.is_err(), origin_loss);
        assert!(!owned.get() && sibling_done.get());
        assert!(control.dropped.load(Ordering::Acquire));
    }
}

#[tokio::test]
async fn worker_failure_stops_feature_and_joins_owner_instead_of_replacing_it() {
    let control = Arc::new(Control::default());
    let _release = ReleaseAll(control.clone());
    control.panic.store(true, Ordering::Release);
    let mut supplier = OwnedSupplier::held(control.clone());
    let (send, incoming) = incoming();
    let (_signal, mut stop) = watch::channel(false);
    let owned = Cell::new(true);
    let (result, ()) = tokio::join!(
        async {
            let result = run_connections(
                &mut stop,
                &mut supplier,
                || next(incoming.clone()),
                |_| Ok(()),
                || Ok(()),
            )
            .await;
            owned.set(false);
            result
        },
        async {
            let (server, mut client) = tokio::io::duplex(8192);
            client.write_all(&query()).await.unwrap();
            send.send(server).await.unwrap();
            wait_flag(&control.dropping).await;
            let mut bytes = Vec::new();
            timeout(Duration::from_secs(3), client.read_to_end(&mut bytes))
                .await
                .unwrap()
                .unwrap();
            assert!(
                bytes.is_empty(),
                "a failed thread must not claim a partial success"
            );
            assert!(owned.get() && !control.dropped.load(Ordering::Acquire));
            assert_eq!(control.calls.load(Ordering::Acquire), 1);
            control.owner_cleanup.release();
        },
    );
    assert!(
        result
            .unwrap_err()
            .to_string()
            .contains("brightness worker disconnected")
    );
    assert!(!owned.get() && control.dropped.load(Ordering::Acquire));
    assert_eq!(control.calls.load(Ordering::Acquire), 1);
}

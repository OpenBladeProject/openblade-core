use super::*;
use serde_json::Value;
use std::{cell::Cell, sync::atomic::AtomicUsize};
const ID: &str = "9f876742-1234-4321-abcd-d3ea3e1cf102";
struct Script {
    outcome: Option<Result<Vec<u8>, Failure>>,
    started: Arc<AtomicBool>,
    retired: Arc<AtomicBool>,
    wait_cancel: bool,
    probes: usize,
}
impl Script {
    fn new(outcome: Result<Vec<u8>, Failure>) -> Self {
        Self {
            outcome: Some(outcome),
            started: Arc::new(AtomicBool::new(false)),
            retired: Arc::new(AtomicBool::new(false)),
            wait_cancel: false,
            probes: 0,
        }
    }
}
impl Supplier for Script {
    async fn acquire(&mut self, canceled: Arc<AtomicBool>) -> Result<Vec<u8>, Failure> {
        self.started.store(true, Ordering::Release);
        if self.wait_cancel {
            while !canceled.load(Ordering::Acquire) {
                sleep(Duration::from_millis(1)).await;
            }
        }
        self.retired.store(true, Ordering::Release);
        self.outcome
            .take()
            .expect("supplier invoked more than once")
    }
    fn reconcile_retirement(&mut self) -> Result<(), Failure> {
        self.probes += 1;
        if self.probes < 3 {
            Err(Failure::StopUnconfirmed)
        } else {
            Ok(())
        }
    }
}
fn request() -> Vec<u8> {
    let body = format!(r#"{{"version":1,"requestId":"{ID}"}}"#).into_bytes();
    [
        i32::try_from(body.len()).unwrap().to_le_bytes().as_slice(),
        &body,
    ]
    .concat()
}
async fn exchange(script: &mut Script, body: &[u8]) -> (io::Result<()>, Vec<u8>, usize) {
    let (mut server, mut client) = tokio::io::duplex(512 * 1024);
    client.write_all(body).await.unwrap();
    let (_stop, mut stop) = watch::channel(false);
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
fn capture(name: &str, frame: &[u8], expected: u8) {
    if let Some(path) = std::env::var_os("OPENBLADE_NATIVE_AGENT_BATTERY_FRAMES") {
        use std::io::Write;
        static LOCK: std::sync::Mutex<()> = std::sync::Mutex::new(());
        let _lock = LOCK.lock().unwrap();
        let mut output = std::fs::OpenOptions::new()
            .create(true)
            .append(true)
            .open(path)
            .unwrap();
        writeln!(
            output,
            "{}",
            serde_json::json!({"case":name,"expectedStatus":expected,"frame":frame})
        )
        .unwrap();
    }
}
#[tokio::test]
async fn selected_dispatcher_emits_managed_frames_from_acquired_xml_and_failures() {
    for (name, result, status) in [
        (
            "success",
            Ok(include_bytes!("../../session_battery/timeline.xml").to_vec()),
            0,
        ),
        ("malformed", Ok(b"<BatteryReport>".to_vec()), 1),
        ("oversized", Ok(vec![b' '; 4 * 1024 * 1024 + 1]), 1),
        ("output-custody", Err(Failure::Output), 1),
        ("process-exit", Err(Failure::ProcessExit), 1),
        ("acquisition-timeout", Err(Failure::Timeout), 1),
        ("scratch", Err(Failure::Scratch), 1),
        ("cleanup", Err(Failure::Cleanup), 1),
        ("start", Err(Failure::Start), 2),
        ("wait", Err(Failure::Wait), 2),
    ] {
        let mut script = Script::new(result);
        let (result, frame, admissions) = exchange(&mut script, &request()).await;
        result.unwrap();
        assert_eq!(admissions, 2, "fresh admission before dispatch and reply");
        assert!(script.retired.load(Ordering::Acquire));
        assert_eq!(
            i32::from_le_bytes(frame[..4].try_into().unwrap()) as usize,
            frame.len() - 4
        );
        let body: Value = serde_json::from_slice(&frame[4..]).unwrap();
        assert_eq!(body["status"], status);
        assert_eq!(body["requestId"], ID);
        assert_eq!(body["report"].is_null(), status != 0);
        capture(name, &frame, status);
    }
}
#[tokio::test]
async fn malformed_envelopes_never_invoke_acquisition() {
    for body in [
        (-1i32).to_le_bytes().to_vec(),
        (MAX_FRAME + 1).to_le_bytes().to_vec(),
        {
            let body = format!(r#"{{"version":1,"requestId":"{ID}","command":"powercfg"}}"#);
            [
                i32::try_from(body.len()).unwrap().to_le_bytes().as_slice(),
                body.as_bytes(),
            ]
            .concat()
        },
        {
            let body = format!(r#"{{"version":1,"requestId":"{ID}","VERSION":1}}"#);
            [
                i32::try_from(body.len()).unwrap().to_le_bytes().as_slice(),
                body.as_bytes(),
            ]
            .concat()
        },
    ] {
        let mut script = Script::new(Err(Failure::Start));
        let (result, bytes, _) = exchange(&mut script, &body).await;
        assert!(result.is_err());
        assert!(bytes.is_empty());
        assert!(!script.started.load(Ordering::Acquire));
    }
}
#[tokio::test]
async fn rejected_peer_or_stale_origin_never_starts_supplier() {
    for stale in [false, true] {
        let (mut server, mut client) = tokio::io::duplex(4096);
        client.write_all(&request()).await.unwrap();
        let (_tx, mut stop) = watch::channel(false);
        let mut script = Script::new(Err(Failure::Start));
        let result = serve(
            &mut server,
            &mut stop,
            &mut script,
            |_| {
                if stale {
                    Ok(())
                } else {
                    Err(io::Error::new(
                        io::ErrorKind::PermissionDenied,
                        "injected peer mismatch",
                    ))
                }
            },
            || {
                Err(io::Error::new(
                    io::ErrorKind::PermissionDenied,
                    "injected stale origin",
                ))
            },
        )
        .await;
        assert_eq!(result.unwrap_err().kind(), io::ErrorKind::PermissionDenied);
        assert!(!script.started.load(Ordering::Acquire));
    }
}
#[tokio::test(start_paused = true)]
async fn cancellation_disconnect_extra_bytes_deadline_and_origin_loss_join_supplier_without_reply()
{
    for cause in ["stop", "disconnect", "extra", "deadline", "origin"] {
        let (mut server, mut client) = tokio::io::duplex(4096);
        client.write_all(&request()).await.unwrap();
        let (tx, mut stop) = watch::channel(false);
        let mut script = Script::new(Err(Failure::Cancelled));
        script.wait_cancel = true;
        let started = script.started.clone();
        let retired = script.retired.clone();
        let changed = Arc::new(AtomicBool::new(false));
        let changed_verify = changed.clone();
        let (result, bytes) = tokio::join!(
            async {
                let result = serve(
                    &mut server,
                    &mut stop,
                    &mut script,
                    |_| Ok(()),
                    || {
                        if changed_verify.load(Ordering::Acquire) {
                            Err(io::Error::new(io::ErrorKind::PermissionDenied, "stale"))
                        } else {
                            Ok(())
                        }
                    },
                )
                .await;
                assert!(
                    retired.load(Ordering::Acquire),
                    "must join before returning"
                );
                drop(server);
                result
            },
            async {
                while !started.load(Ordering::Acquire) {
                    tokio::task::yield_now().await;
                }
                match cause {
                    "stop" => {
                        tx.send(true).unwrap();
                    }
                    "disconnect" => {
                        drop(client);
                        return Vec::new();
                    }
                    "extra" => {
                        client.write_all(b"x").await.unwrap();
                    }
                    "origin" => {
                        changed.store(true, Ordering::Release);
                    }
                    _ => (),
                }
                let mut bytes = Vec::new();
                client.read_to_end(&mut bytes).await.unwrap();
                bytes
            }
        );
        if cause == "origin" {
            assert_eq!(result.unwrap_err().kind(), io::ErrorKind::PermissionDenied);
        } else {
            result.unwrap();
        }
        assert!(bytes.is_empty(), "{cause}");
    }
}
#[tokio::test(start_paused = true)]
async fn uncertain_retirement_keeps_request_owned_until_exact_reconciliation() {
    let mut script = Script::new(Err(Failure::StopUnconfirmed));
    let (result, frame, _) = exchange(&mut script, &request()).await;
    assert!(result.is_err());
    assert!(frame.is_empty());
    assert_eq!(script.probes, 3);
}
#[tokio::test]
async fn stop_before_request_does_not_admit_work() {
    let (mut server, _client) = tokio::io::duplex(4096);
    let (_tx, mut stop) = watch::channel(true);
    let mut script = Script::new(Err(Failure::Start));
    serve(&mut server, &mut stop, &mut script, |_| Ok(()), || Ok(()))
        .await
        .unwrap();
    assert!(!script.started.load(Ordering::Acquire));
}
#[tokio::test]
async fn identity_is_rechecked_after_acquisition_before_emitting_frame() {
    let (mut server, mut client) = tokio::io::duplex(4096);
    client.write_all(&request()).await.unwrap();
    let (_tx, mut stop) = watch::channel(false);
    let calls = AtomicUsize::new(0);
    let mut script = Script::new(Ok(
        include_bytes!("../../session_battery/timeline.xml").to_vec()
    ));
    let error = serve(
        &mut server,
        &mut stop,
        &mut script,
        |_| {
            if calls.fetch_add(1, Ordering::AcqRel) == 0 {
                Ok(())
            } else {
                Err(io::Error::new(
                    io::ErrorKind::PermissionDenied,
                    "peer changed",
                ))
            }
        },
        || Ok(()),
    )
    .await
    .unwrap_err();
    assert_eq!(error.kind(), io::ErrorKind::PermissionDenied);
    assert!(script.retired.load(Ordering::Acquire));
}

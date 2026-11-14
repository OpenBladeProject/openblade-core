//! Actual fake-endpoint serving/origin loop and deterministic OS-worker race.
use super::*;
use crate::installed::readiness::thermal_storage::{RELEASE_PAUSED, is_release_deferred};

fn source_battery(r: &Rig) {
    r.port.state.lock().unwrap().actual = 0;
    r.metadata.store(1, Ordering::Release);
    r.custody.lifecycle.ingress(
        windows_sys::Win32::System::Services::SERVICE_CONTROL_POWEREVENT,
        10,
    );
}
fn get_request() -> Value {
    json!({"operation":"getStatus","version":80,"requestId":"bbbbbbbb-cccc-dddd-eeee-000000000901"})
}
fn performance_request(n: u32) -> Value {
    json!({"operation":"setPerformanceOverride","version":80,"requestId":format!("aaaaaaaa-bbbb-cccc-dddd-{n:012}"),"value":value(1,1)})
}
async fn enable(r: &Rig, n: u32) {
    r.get().await;
    let response = r
        .request(battery_request(n, "setFullChargeOverride", json!(true)))
        .await;
    assert_eq!(response["result"]["status"], 1, "{response}");
}
async fn wait_release_view(r: &Rig, paused: bool) {
    let mut publications = r.source.lock().await.publications().unwrap();
    tokio::time::timeout(Duration::from_secs(5), async {
        loop {
            let bytes = crate::service_status::encode_status_event(
                &publications.borrow().active.project_private(Some(SID)),
            )
            .unwrap();
            let value: Value = serde_json::from_slice(&bytes).unwrap();
            if value["status"]["message"]
                .as_str()
                .unwrap()
                .contains(RELEASE_PAUSED)
                == paused
            {
                println!(
                    "NATIVE_THERMAL_MUTATION_RESPONSE {}",
                    std::str::from_utf8(&bytes).unwrap()
                );
                return;
            }
            publications.changed().await.unwrap();
        }
    })
    .await
    .unwrap();
}

#[tokio::test]
async fn tfc_release_actual_loop_defers_read_and_serves_blocked_requests_then_retries_only_bookkeeping()
 {
    let r = Rig::new(MemoryFiles::default());
    let events: Events = Rc::default();
    let controls = Controls::new();
    let (mut ports, tx) = ports(Fault::None, &events);
    ports.source = r.source.clone();
    let (running, ready) = oneshot::channel();
    let service = lifecycle(&controls, Ok(ports), &events, move || {
        running.send(()).unwrap();
    });
    let exercise = async {
        ready.await.unwrap();
        let response = outer_request(
            &tx,
            battery_request(900, "setFullChargeOverride", json!(true)),
        )
        .await;
        assert_eq!(response["result"]["status"], 1, "{response}");
        let quota = r.storage.temporary_capacity_for_tests();
        assert!(quota.0 > 0);
        r.custody.fresh_refuse.store(true, Ordering::Release);
        r.files.release_fault.store(1, Ordering::Release);
        source_battery(&r);
        wait_release_view(&r, true).await;
        assert_eq!(r.storage.temporary_capacity_for_tests(), quota);
        assert!(r.runtime.release_only_for_tests());
        assert!(!r.storage.pending());
        assert_eq!(r.port.state.lock().unwrap().writes, 1);
        r.custody.fresh_refuse.store(false, Ordering::Release);
        let status = outer_request(&tx, get_request()).await;
        assert!(status["success"].as_bool().unwrap());
        assert!(
            status["status"]["message"]
                .as_str()
                .unwrap()
                .contains(RELEASE_PAUSED)
        );
        let before = r.port.state.lock().unwrap().sent.len();
        for request in [
            battery_request(902, "setFullChargeOverride", json!(false)),
            fan_request(903, Some(1), 3900),
            performance_request(904),
        ] {
            let response = outer_request(&tx, request).await;
            assert_eq!(response["result"]["status"], 2, "{response}");
            assert!(
                response["result"]["message"]
                    .as_str()
                    .unwrap()
                    .contains(RELEASE_PAUSED)
            );
        }
        assert_eq!(
            r.port.state.lock().unwrap().sent.len(),
            before,
            "deferred admission submits no getter/setter"
        );
        // No new epoch, callback invocation or service restart. The real 500ms
        // origin future retries its own release-only bookkeeping.
        let epoch = r.custody.lifecycle.active();
        r.files.release_fault.store(0, Ordering::Release);
        wait_release_view(&r, false).await;
        assert_eq!(r.custody.lifecycle.active(), epoch);
        assert_eq!(r.storage.temporary_capacity_for_tests(), (0, None));
        assert!(!r.runtime.release_only_for_tests());
        assert_eq!(r.port.state.lock().unwrap().writes, 1);
        assert_eq!(
            r.port.state.lock().unwrap().temporary_raw,
            4,
            "quota release does not confirm hardware canceled"
        );
        let response = outer_request(
            &tx,
            battery_request(905, "setFullChargeOverride", json!(false)),
        )
        .await;
        assert_eq!(response["result"]["status"], 1, "{response}");
        assert_eq!(fan_rows(&r).last().unwrap()["tfc"]["origin"], Value::Null);
        assert_eq!(r.port.state.lock().unwrap().writes, 2);
        controls.handle(windows_sys::Win32::System::Services::SERVICE_CONTROL_STOP);
    };
    let (result, ()) = tokio::time::timeout(Duration::from_secs(10), async {
        tokio::join!(service, exercise)
    })
    .await
    .unwrap();
    result.unwrap();
}

#[tokio::test]
async fn tfc_release_actual_loop_preserves_trust_mismatch_unclassified_and_custody_failures() {
    for fault in [2, 3, 4, 5, 6] {
        let r = Rig::new(MemoryFiles::default());
        let events: Events = Rc::default();
        let controls = Controls::new();
        let (mut ports, tx) = ports(Fault::None, &events);
        ports.source = r.source.clone();
        let (running, ready) = oneshot::channel();
        let service = lifecycle(&controls, Ok(ports), &events, move || {
            running.send(()).unwrap();
        });
        let exercise = async {
            ready.await.unwrap();
            let response = outer_request(
                &tx,
                battery_request(910, "setFullChargeOverride", json!(true)),
            )
            .await;
            assert_eq!(response["result"]["status"], 1, "{response}");
            r.custody.fresh_refuse.store(true, Ordering::Release);
            if fault == 6 {
                r.custody.integrity_lost.store(true, Ordering::Release);
            } else {
                r.files.release_fault.store(fault, Ordering::Release);
            }
            source_battery(&r);
        };
        let (result, ()) = tokio::time::timeout(Duration::from_secs(5), async {
            tokio::join!(service, exercise)
        })
        .await
        .unwrap();
        let error = result.unwrap_err();
        assert!(
            !is_release_deferred(&error),
            "fault {fault} cannot be converted to recoverable bookkeeping"
        );
        assert_eq!(
            r.port.state.lock().unwrap().writes,
            1,
            "no cleanup replay after fatal/fenced condition"
        );
    }
}

fn padded_history(target: usize) -> Vec<u8> {
    let mut bytes = Vec::new();
    let mut n = 300000;
    while bytes.len() + 900 < target {
        for progress in [0, 6] {
            let row = json!({"request":format!("aaaaaaaa-bbbb-cccc-dddd-{n:012}"),"owner":"7".repeat(64),"user_sid":SID,"caller_pid":44,"session":7,"requested":null,"destination":1,"intent":format!("{:x}",<sha2::Sha256 as sha2::Digest>::digest(b"null")),"progress":progress});
            bytes.extend(serde_json::to_vec(&row).unwrap());
            bytes.push(b'\n');
        }
        n += 1;
    }
    let pad = target - bytes.len();
    bytes.splice(
        bytes.len() - 1..bytes.len() - 1,
        std::iter::repeat_n(b' ', pad),
    );
    bytes
}

#[tokio::test]
async fn tfc_release_actual_worker_prepared_race_keeps_origin_until_settlement_and_next_tick() {
    // Measure the unchanged encoded operation sizes so the subsequent ordinary
    // reservation/roll exercises the actual segment boundary without a matrix.
    let probe = Rig::new(MemoryFiles::default());
    enable(&probe, 920).await;
    let quota = probe.storage.temporary_capacity_for_tests().0;
    probe.port.state.lock().unwrap().temporary_raw = 0;
    assert_eq!(
        probe
            .request(battery_request(
                921,
                "setBatteryPolicy",
                json!({"chargeLimitPercent":70})
            ))
            .await["result"]["status"],
        1
    );
    let span = probe
        .files
        .read("thermal-mutation.journal")
        .unwrap()
        .unwrap()
        .len();
    probe.stop().await;
    let files = MemoryFiles::default();
    files.roll_settled.store(true, Ordering::Release);
    files.bytes.lock().unwrap().insert(
        "thermal-mutation.journal".into(),
        padded_history(crate::thermal_history::SEGMENT - span - quota - 400),
    );
    let r = Rig::new(files);
    let events: Events = Rc::default();
    let controls = Controls::new();
    let (mut ports, tx) = ports(Fault::None, &events);
    ports.source = r.source.clone();
    let (running, ready) = oneshot::channel();
    let service = lifecycle(&controls, Ok(ports), &events, move || {
        running.send(()).unwrap();
    });
    let exercise = async {
        ready.await.unwrap();
        assert_eq!(
            outer_request(
                &tx,
                battery_request(922, "setFullChargeOverride", json!(true))
            )
            .await["result"]["status"],
            1
        );
        let original = r.storage.temporary_capacity_for_tests();
        let at_prepare = Arc::new(tokio::sync::Notify::new());
        let at = at_prepare.clone();
        let (allow_prepared_tx, allow_prepared_rx) = std::sync::mpsc::channel();
        let (prepared_tx, prepared_rx) = std::sync::mpsc::channel();
        let (finish_tx, finish_rx) = std::sync::mpsc::channel();
        let allow = StdMutex::new(allow_prepared_rx);
        let finish = StdMutex::new(finish_rx);
        r.storage
            .battery_append_hook_for_tests(Arc::new(move |stage| match stage {
                "before-prepared" => {
                    at.notify_one();
                    allow
                        .lock()
                        .unwrap()
                        .recv_timeout(Duration::from_secs(3))
                        .unwrap();
                }
                "prepared" => {
                    prepared_tx.send(()).unwrap();
                    finish
                        .lock()
                        .unwrap()
                        .recv_timeout(Duration::from_secs(3))
                        .unwrap();
                }
                _ => panic!("unexpected worker boundary"),
            }));
        let once = std::cell::Cell::new(false);
        r.runtime.release_hook_for_tests(Rc::new(move || {
            if once.replace(true) {
                return;
            }
            allow_prepared_tx.send(()).unwrap();
            prepared_rx.recv_timeout(Duration::from_secs(3)).unwrap();
        }));
        r.port.state.lock().unwrap().temporary_raw = 0;
        let mut policy = Box::pin(outer_request(
            &tx,
            battery_request(923, "setBatteryPolicy", json!({"chargeLimitPercent":70})),
        ));
        tokio::select! {_=&mut policy=>panic!("policy unexpectedly settled"),_=at_prepare.notified()=>{}}
        // The origin tick sees independent false before the worker can append.
        // Its release hook then commits actual Prepared before the release lock.
        wait_release_view(&r, true).await;
        assert_eq!(r.storage.progress(), Some(0));
        assert!(r.storage.pending());
        assert!(r.runtime.release_only_for_tests());
        assert_eq!(r.storage.temporary_capacity_for_tests(), original);
        r.files.release_fault.store(1, Ordering::Release);
        finish_tx.send(()).unwrap();
        let response = policy.await;
        assert_eq!(
            response["result"]["status"], 2,
            "hardware evidence retained while bookkeeping remains deferred: {response}"
        );
        assert_eq!(r.storage.progress(), Some(62));
        assert!(!r.storage.pending());
        assert_eq!(r.storage.temporary_capacity_for_tests(), original);
        assert_eq!(r.port.state.lock().unwrap().writes, 2);
        r.files.release_fault.store(0, Ordering::Release);
        wait_release_view(&r, false).await;
        assert_eq!(r.storage.temporary_capacity_for_tests(), (0, None));
        assert!(!r.runtime.release_only_for_tests());
        assert_eq!(
            r.port.state.lock().unwrap().writes,
            2,
            "tick performs no hardware replay"
        );
        let performance = outer_request(&tx, performance_request(924)).await;
        assert_eq!(performance["result"]["status"], 1, "{performance}");
        let cancel = outer_request(
            &tx,
            battery_request(925, "setFullChargeOverride", json!(false)),
        )
        .await;
        assert_eq!(cancel["result"]["status"], 1, "{cancel}");
        assert_eq!(fan_rows(&r).last().unwrap()["progress"], 73);
        assert_eq!(fan_rows(&r).last().unwrap()["tfc"]["origin"], Value::Null);
        assert!(r.files.read("retained-segment").unwrap().is_some());
        controls.handle(windows_sys::Win32::System::Services::SERVICE_CONTROL_STOP);
    };
    let (result, ()) = tokio::time::timeout(Duration::from_secs(10), async {
        tokio::join!(service, exercise)
    })
    .await
    .unwrap();
    result.unwrap();
}

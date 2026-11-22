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
/// One origin tick, as the serving loop's interval runs it.
async fn origin_tick(r: &Rig) {
    r.source.lock().await.process_power_source().await.unwrap();
}
fn get_request() -> Value {
    json!({"operation":"getStatus","version":85,"requestId":"bbbbbbbb-cccc-dddd-eeee-000000000901"})
}
fn performance_request(n: u32) -> Value {
    json!({"operation":"setPerformanceOverride","version":85,"requestId":format!("aaaaaaaa-bbbb-cccc-dddd-{n:012}"),"value":value(1,1)})
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
                emit_fixture_frame(
                    "publisher_projection",
                    "waitReleaseView",
                    "statusEvent",
                    &bytes,
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
    prepared_race(false).await;
}
#[tokio::test]
async fn tfc_release_outcome_cause_changes_publish_while_release_remains_paused() {
    prepared_race(true).await;
}
/// Bounds only waits that explicit sequencing already orders, so it turns a
/// hang into a failure and never decides the race.
const HANG_GUARD: Duration = Duration::from_secs(120);
/// One mutation on the served source, answered as the pipe's `result`.
/// The pipe's budget is wall clock, and the padded journal costs about a
/// second of debug-build CPU per request, which a loaded host can stretch past
/// it. The worker, the origin loop and the outcome are unchanged.
async fn source_mutation(r: &Rig, request: Value) -> Value {
    let request =
        crate::service_status::StatusRequest::decode(&serde_json::to_vec(&request).unwrap())
            .unwrap();
    let mutation = r.source.lock().await.mutate(
        USER,
        SID.into(),
        request,
        tokio::time::Instant::now() + HANG_GUARD,
    );
    serde_json::to_value(mutation.await.unwrap().0).unwrap()
}
async fn prepared_race(quarantine_after_read_pause: bool) {
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
    // Every request goes straight to the source, so the pipe sender only
    // keeps the serving loop accepting.
    let (mut ports, _tx) = ports(Fault::None, &events);
    ports.source = r.source.clone();
    let (running, ready) = oneshot::channel();
    let service = lifecycle(&controls, Ok(ports), &events, move || {
        running.send(()).unwrap();
    });
    let exercise = async {
        ready.await.unwrap();
        assert_eq!(
            source_mutation(
                &r,
                battery_request(922, "setFullChargeOverride", json!(true))
            )
            .await["status"],
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
                    allow.lock().unwrap().recv_timeout(HANG_GUARD).unwrap();
                }
                "prepared" => {
                    prepared_tx.send(()).unwrap();
                    finish.lock().unwrap().recv_timeout(HANG_GUARD).unwrap();
                }
                _ => panic!("unexpected worker boundary"),
            }));
        let once = std::cell::Cell::new(false);
        r.runtime.release_hook_for_tests(Rc::new(move || {
            if once.replace(true) {
                return;
            }
            allow_prepared_tx.send(()).unwrap();
            prepared_rx.recv_timeout(HANG_GUARD).unwrap();
        }));
        r.port.state.lock().unwrap().temporary_raw = 0;
        let mut policy = Box::pin(source_mutation(
            &r,
            battery_request(923, "setBatteryPolicy", json!({"chargeLimitPercent":70})),
        ));
        tokio::select! {_=&mut policy=>panic!("policy unexpectedly settled"),_=at_prepare.notified()=>{}}
        // The origin tick sees independent false before the worker can append.
        // Its release hook then commits actual Prepared before the release lock.
        // Run that tick now rather than wait for the loop's 500 ms interval; an
        // interval tick that wins the race runs the same once-only hook.
        origin_tick(&r).await;
        wait_release_view(&r, true).await;
        let busy = immediate_battery_view(&r, Some(SID)).await;
        assert!(
            busy["message"]
                .as_str()
                .unwrap()
                .contains("until active work settles")
        );
        assert_eq!(r.storage.progress(), Some(0));
        assert!(r.storage.pending());
        assert!(r.runtime.release_only_for_tests());
        assert_eq!(r.storage.temporary_capacity_for_tests(), original);
        r.files.release_fault.store(1, Ordering::Release);
        finish_tx.send(()).unwrap();
        let response = policy.await;
        assert_eq!(
            response["status"], 1,
            "confirmed settled operation must survive bookkeeping deferral: {response}"
        );
        assert_eq!(r.storage.progress(), Some(62));
        assert!(!r.storage.pending());
        let published = immediate_battery_view(&r, Some(SID)).await;
        assert_eq!(
            published["desiredState"]["battery"]["configuredPolicy"]["chargeLimitPercent"],
            70
        );
        assert_eq!(
            published["confirmedState"]["battery"]["chargeLimitPercent"],
            70
        );
        assert!(
            published["message"]
                .as_str()
                .unwrap()
                .contains("Battery operation confirmed")
        );
        assert!(
            published["message"]
                .as_str()
                .unwrap()
                .contains("check is retried")
        );
        assert!(
            !published["message"]
                .as_str()
                .unwrap()
                .contains("until active work settles")
        );
        if quarantine_after_read_pause {
            // Exercise the same production quarantine boundary here solely to
            // prove cause coalescing across three continuously paused states.
            // The separate failed-settlement case proves real worker quarantine.
            r.storage.quarantine_settlement().unwrap();
            origin_tick(&r).await;
            let changed = immediate_battery_view(&r, Some(SID)).await;
            let reason = r.storage.pending_reason().unwrap();
            assert!(changed["message"].as_str().unwrap().contains(reason));
            assert!(
                !changed["message"]
                    .as_str()
                    .unwrap()
                    .contains("check is retried")
            );
            assert_eq!(
                changed["desiredState"]["battery"]["configuredPolicy"]["chargeLimitPercent"],
                70
            );
            assert!(
                changed["message"]
                    .as_str()
                    .unwrap()
                    .contains("Battery operation confirmed")
            );
            assert_eq!(r.storage.temporary_capacity_for_tests(), original);
            assert_eq!(r.port.state.lock().unwrap().writes, 2);
            controls.handle(windows_sys::Win32::System::Services::SERVICE_CONTROL_STOP);
            return;
        }
        assert_eq!(r.storage.temporary_capacity_for_tests(), original);
        assert_eq!(r.port.state.lock().unwrap().writes, 2);
        r.files.release_fault.store(0, Ordering::Release);
        origin_tick(&r).await;
        wait_release_view(&r, false).await;
        assert_eq!(r.storage.temporary_capacity_for_tests(), (0, None));
        assert!(!r.runtime.release_only_for_tests());
        assert_eq!(
            r.port.state.lock().unwrap().writes,
            2,
            "tick performs no hardware replay"
        );
        let performance = source_mutation(&r, performance_request(924)).await;
        assert_eq!(performance["status"], 1, "{performance}");
        let cancel = source_mutation(
            &r,
            battery_request(925, "setFullChargeOverride", json!(false)),
        )
        .await;
        assert_eq!(cancel["status"], 1, "{cancel}");
        assert_eq!(fan_rows(&r).last().unwrap()["progress"], 73);
        assert_eq!(fan_rows(&r).last().unwrap()["tfc"]["origin"], Value::Null);
        assert!(r.files.read("retained-segment").unwrap().is_some());
        controls.handle(windows_sys::Win32::System::Services::SERVICE_CONTROL_STOP);
    };
    let (result, ()) = tokio::time::timeout(HANG_GUARD, async { tokio::join!(service, exercise) })
        .await
        .unwrap();
    result.unwrap();
}

async fn immediate_battery_view(r: &Rig, sid: Option<&str>) -> Value {
    let publications = r.source.lock().await.publications().unwrap();
    let snapshot = publications.borrow().active.clone().project_private(sid);
    let bytes = crate::service_status::encode_status_event(&snapshot).unwrap();
    emit_fixture_frame(
        "publisher_projection",
        "immediateBatteryView",
        "statusEvent",
        &bytes,
    );
    serde_json::from_slice::<Value>(&bytes).unwrap()["status"].clone()
}
async fn policy_and_enable(r: &Rig, n: u32) {
    r.get().await;
    let response = r
        .request(battery_request(
            n,
            "setBatteryPolicy",
            json!({"chargeLimitPercent":70}),
        ))
        .await;
    assert_eq!(response["result"]["status"], 1, "{response}");
    enable(r, n + 1).await;
}
fn assert_canceled_policy(view: &Value) {
    assert_eq!(
        view["desiredState"]["battery"]["configuredPolicy"]["chargeLimitPercent"], 70,
        "full desired update missing: {view}"
    );
    assert_eq!(
        view["desiredState"]["battery"]["temporaryFullCharge"],
        false
    );
    assert_eq!(view["fullChargeOverride"], false);
    assert_eq!(view["confirmedState"]["battery"]["chargeLimitPercent"], 70);
    assert_eq!(
        view["confirmedState"]["battery"]["temporaryFullCharge"],
        false
    );
}
#[tokio::test]
async fn tfc_release_outcome_explicit_cancel_keeps_settled_result_and_immediate_owned_delta() {
    let r = Rig::new(MemoryFiles::default());
    policy_and_enable(&r, 940).await;
    let quota = r.storage.temporary_capacity_for_tests();
    r.files.release_fault.store(1, Ordering::Release);
    let response = r
        .request(battery_request(942, "setFullChargeOverride", json!(false)))
        .await;
    assert_eq!(r.storage.progress(), Some(72));
    assert!(!r.storage.pending());
    assert_eq!(
        response["result"]["status"], 1,
        "settled cancel replaced by bookkeeping: {response}"
    );
    let owned = immediate_battery_view(&r, Some(SID)).await;
    assert_canceled_policy(&owned);
    assert!(
        owned["message"]
            .as_str()
            .unwrap()
            .contains("Battery operation confirmed")
    );
    assert!(!owned["message"].as_str().unwrap().contains(RELEASE_PAUSED));
    let foreign = immediate_battery_view(&r, Some("S-1-5-21-111-222-333-1002")).await;
    assert!(
        !foreign["message"]
            .as_str()
            .unwrap()
            .contains("Battery operation confirmed")
    );
    assert_ne!(
        foreign["desiredState"]["battery"]["configuredPolicy"]["chargeLimitPercent"],
        70
    );
    assert!(quota.0 > 0);
    assert_eq!(r.storage.temporary_capacity_for_tests(), (0, None));
    assert!(!r.runtime.release_only_for_tests());
    let writes = r.port.state.lock().unwrap().writes;
    assert_eq!(writes, 3);
    // Settled explicit cancellation has already cleared its reservation. A tagged
    // release-read fault is unreachable here; a no-op cancel needs no setter.
    let blocked = r
        .request(battery_request(943, "setFullChargeOverride", json!(false)))
        .await;
    assert_eq!(blocked["result"]["status"], 1);
    assert_eq!(r.storage.progress(), Some(73));
    assert_eq!(r.port.state.lock().unwrap().writes, writes);
    r.files.release_fault.store(0, Ordering::Release);
    r.source.lock().await.process_power_source().await.unwrap();
    assert_eq!(r.storage.temporary_capacity_for_tests(), (0, None));
    assert_eq!(r.port.state.lock().unwrap().writes, writes);
    let restored = immediate_battery_view(&r, Some(SID)).await;
    assert!(
        !restored["message"]
            .as_str()
            .unwrap()
            .contains(RELEASE_PAUSED)
    );
    assert!(
        restored["message"]
            .as_str()
            .unwrap()
            .contains("Battery operation confirmed")
    );
    r.stop().await;
}
#[tokio::test]
async fn tfc_release_outcome_automatic_cancel_publishes_full_delta_before_callback_repair() {
    let r = Rig::new(MemoryFiles::default());
    policy_and_enable(&r, 950).await;
    let enabled = fan_rows(&r).last().unwrap().clone();
    let quota = r.storage.temporary_capacity_for_tests();
    r.files.release_fault.store(1, Ordering::Release);
    source_battery(&r);
    r.source.lock().await.process_power_source().await.unwrap();
    let canceled = fan_rows(&r).last().unwrap().clone();
    assert_eq!(canceled["progress"], 72);
    assert_eq!(canceled["tfc"]["ids"], enabled["tfc"]["cancel_ids"]);
    assert!(!r.storage.pending());
    let owned = immediate_battery_view(&r, Some(SID)).await;
    assert_canceled_policy(&owned);
    assert!(
        owned["message"]
            .as_str()
            .unwrap()
            .contains("canceled on Battery by independent readback")
    );
    let foreign = immediate_battery_view(&r, Some("S-1-5-21-111-222-333-1002")).await;
    assert_ne!(
        foreign["desiredState"]["battery"]["configuredPolicy"]["chargeLimitPercent"],
        70
    );
    assert!(quota.0 > 0);
    assert_eq!(r.storage.temporary_capacity_for_tests(), (0, None));
    assert!(!r.runtime.release_only_for_tests());
    assert_eq!(r.port.state.lock().unwrap().writes, 3);
    // Existing authorized callback reconstructs without another explicit mutation.
    r.get().await;
    assert_canceled_policy(&immediate_battery_view(&r, Some(SID)).await);
    r.files.release_fault.store(0, Ordering::Release);
    r.source.lock().await.process_power_source().await.unwrap();
    assert_eq!(r.storage.temporary_capacity_for_tests(), (0, None));
    assert_eq!(r.port.state.lock().unwrap().writes, 3);
    r.stop().await;
}
#[tokio::test]
async fn tfc_release_outcome_quarantined_cancel_keeps_reconciliation_cause_with_stale_proof() {
    let r = Rig::new(MemoryFiles::default());
    policy_and_enable(&r, 960).await;
    r.files.fail_battery_settled.store(true, Ordering::Release);
    let response = r
        .request(battery_request(962, "setFullChargeOverride", json!(false)))
        .await;
    assert_eq!(response["result"]["status"], 2);
    assert!(r.storage.pending());
    let reason = r.storage.pending_reason().unwrap();
    assert!(
        response["result"]["message"]
            .as_str()
            .unwrap()
            .contains(reason),
        "actual pending cause replaced: {response}"
    );
    let immediate = immediate_battery_view(&r, Some(SID)).await;
    assert_eq!(
        immediate["message"]
            .as_str()
            .unwrap()
            .matches(reason)
            .count(),
        1
    );
    assert!(
        !immediate["message"]
            .as_str()
            .unwrap()
            .contains("check is retried")
    );
    // Epoch change invalidates original proof; public cause must remain authoritative.
    source_battery(&r);
    r.source.lock().await.process_power_source().await.unwrap();
    let blocked = r
        .request(battery_request(
            963,
            "setBatteryPolicy",
            json!({"chargeLimitPercent":75}),
        ))
        .await;
    assert_eq!(blocked["result"]["status"], 2);
    assert!(
        blocked["result"]["message"]
            .as_str()
            .unwrap()
            .contains(reason)
    );
    r.get().await;
    let stale = immediate_battery_view(&r, Some(SID)).await;
    assert_eq!(
        stale["message"].as_str().unwrap().matches(reason).count(),
        1
    );
    assert!(
        !stale["message"]
            .as_str()
            .unwrap()
            .contains("check is retried")
    );
    assert_eq!(r.port.state.lock().unwrap().writes, 3);
    assert!(r.runtime.release_only_for_tests());
    r.stop().await;
}

#[tokio::test]
async fn tfc_release_outcome_refused_automatic_cancel_retains_full_intent_without_invented_readback()
 {
    let r = Rig::new(MemoryFiles::default());
    policy_and_enable(&r, 970).await;
    let quota = r.storage.temporary_capacity_for_tests();
    let enabled = fan_rows(&r).last().unwrap().clone();
    r.files.release_fault.store(1, Ordering::Release);
    r.custody.authentication.fetch_add(1, Ordering::AcqRel);
    source_battery(&r);
    r.source.lock().await.process_power_source().await.unwrap();
    assert_eq!(fan_rows(&r).last().unwrap(), &enabled);
    assert_eq!(r.port.state.lock().unwrap().writes, 2);
    let owned = immediate_battery_view(&r, Some(SID)).await;
    assert_eq!(
        owned["desiredState"]["battery"]["configuredPolicy"]["chargeLimitPercent"], 70,
        "event delta omitted: {owned}"
    );
    assert_eq!(
        owned["desiredState"]["battery"]["temporaryFullCharge"],
        false
    );
    assert_eq!(owned["fullChargeOverride"], false);
    assert_eq!(
        owned["confirmedState"]["battery"]["temporaryFullCharge"], true,
        "intent clearing is not hardware confirmation"
    );
    assert!(
        owned["message"]
            .as_str()
            .unwrap()
            .contains("cancellation is paused")
    );
    assert!(owned["message"].as_str().unwrap().contains(RELEASE_PAUSED));
    let foreign = immediate_battery_view(&r, Some("S-1-5-21-111-222-333-1002")).await;
    assert_ne!(
        foreign["desiredState"]["battery"]["configuredPolicy"]["chargeLimitPercent"],
        70
    );
    assert_eq!(r.storage.temporary_capacity_for_tests(), quota);
    assert!(r.runtime.release_only_for_tests());
    r.files.release_fault.store(0, Ordering::Release);
    r.source.lock().await.process_power_source().await.unwrap();
    assert_eq!(r.storage.temporary_capacity_for_tests(), (0, None));
    assert_eq!(r.port.state.lock().unwrap().writes, 2);
    let recovered = immediate_battery_view(&r, Some(SID)).await;
    assert_eq!(
        recovered["desiredState"]["battery"]["configuredPolicy"]["chargeLimitPercent"],
        70
    );
    assert_eq!(
        recovered["desiredState"]["battery"]["temporaryFullCharge"],
        false
    );
    assert!(
        recovered["message"]
            .as_str()
            .unwrap()
            .contains("cancellation is paused")
    );
    assert!(
        !recovered["message"]
            .as_str()
            .unwrap()
            .contains(RELEASE_PAUSED)
    );
    assert_eq!(
        recovered["confirmedState"]["battery"]["temporaryFullCharge"],
        true
    );
    r.stop().await;
}
#[tokio::test]
async fn tfc_release_a_power_setting_read_during_the_battery_event_does_not_pause_the_cancel() {
    use openblade_domain::power_setting::{PBT_POWERSETTINGCHANGE, PowerSetting};
    use windows_sys::Win32::System::Services::SERVICE_CONTROL_POWEREVENT;
    // An unplug delivers PBT_APMPOWERSTATUSCHANGE and, with B2's registration,
    // a GUID_ACDC_POWER_SOURCE read. The read lands after the tick took its
    // epoch and before the cancel is authorized. Managed fences nothing on
    // that read, so the one-shot cancel is written on this tick, exactly once.
    let r = Rig::new(MemoryFiles::default());
    policy_and_enable(&r, 950).await;
    let before = r.port.state.lock().unwrap().writes;
    source_battery(&r);
    *r.custody.notice_during_authorize.lock().unwrap() = Some((
        SERVICE_CONTROL_POWEREVENT,
        PBT_POWERSETTINGCHANGE,
        Some(PowerSetting::PowerSource(1)),
    ));
    r.source.lock().await.process_power_source().await.unwrap();
    assert!(
        r.custody.notice_during_authorize.lock().unwrap().is_none(),
        "the read was delivered inside the authorization"
    );
    for _ in 0..4 {
        r.source.lock().await.process_power_source().await.unwrap();
    }
    assert_eq!(r.port.state.lock().unwrap().writes - before, 1);
    let view = immediate_battery_view(&r, Some(SID)).await;
    assert_eq!(
        view["desiredState"]["battery"]["temporaryFullCharge"],
        false
    );
    assert_eq!(
        view["confirmedState"]["battery"]["temporaryFullCharge"],
        false
    );
    assert!(
        view["message"]
            .as_str()
            .unwrap()
            .contains("canceled on Battery by independent readback"),
        "{view}"
    );
    r.stop().await;
}

// A fencing notice delivered inside one fresh authorization of the battery
// tick, after the tick read its epoch. Production guards pin the epoch they
// capture and refuse once it moves (`WindowsGuard::authorize_for`), as the
// scoped fixture (`CapturedFresh`) does. The unscoped fixture instead answers
// with a proof at the newer epoch.
type Notice = (
    u32,
    u32,
    Option<openblade_domain::power_setting::PowerSetting>,
);
const POWER: u32 = windows_sys::Win32::System::Services::SERVICE_CONTROL_POWEREVENT;
const SECOND_UNPLUG: Notice = (POWER, 10, None); // PBT_APMPOWERSTATUSCHANGE
const RESUME: Notice = (
    POWER,
    openblade_domain::power_setting::PBT_APMRESUMEAUTOMATIC,
    None,
);
const SUSPEND: Notice = (POWER, openblade_domain::power_setting::PBT_APMSUSPEND, None);
const SESSION: Notice = (
    windows_sys::Win32::System::Services::SERVICE_CONTROL_SESSIONCHANGE,
    8, // WTS_SESSION_UNLOCK
    None,
);
fn writes(r: &Rig) -> usize {
    r.port.state.lock().unwrap().writes
}
fn scoped_rig() -> Rig {
    let r = Rig::new(MemoryFiles::default());
    r.custody.scoped_fresh.store(true, Ordering::Release);
    r
}
/// Runs one tick with `notice` armed for the fresh authorization after
/// `position` others, and reports whether the notice landed in that tick.
async fn notice_tick(r: &Rig, notice: Notice, position: usize) -> bool {
    r.custody.notice_skip.store(position, Ordering::Release);
    *r.custody.notice_during_authorize.lock().unwrap() = Some(notice);
    origin_tick(r).await;
    let landed = r
        .custody
        .notice_during_authorize
        .lock()
        .unwrap()
        .take()
        .is_none();
    r.custody.notice_skip.store(0, Ordering::Release);
    landed
}
async fn assert_canceled_once(r: &Rig, before: usize) {
    for _ in 0..4 {
        origin_tick(r).await;
    }
    assert_eq!(writes(r) - before, 1);
    assert!(!r.storage.pending());
    let view = immediate_battery_view(r, Some(SID)).await;
    assert_canceled_policy(&view);
    assert!(
        view["message"]
            .as_str()
            .unwrap()
            .contains("canceled on Battery by independent readback"),
        "{view}"
    );
}
/// A refusal at the tick's own epoch claims the cancel once and pauses it.
async fn assert_paused_once(r: &Rig, before: usize) {
    let view = immediate_battery_view(r, Some(SID)).await;
    assert!(
        view["message"]
            .as_str()
            .unwrap()
            .contains("cancellation is paused"),
        "{view}"
    );
    for _ in 0..3 {
        source_battery(r);
        origin_tick(r).await;
    }
    assert_eq!(writes(r), before, "a claimed obligation is never retried");
}

/// Where a notice landed relative to the cancel's submission.
#[derive(Clone, Copy, Debug, PartialEq, PartialOrd)]
enum Window {
    /// No submission intent was journaled.
    Unsubmitted,
    /// Submission intent (progress 70) was journaled; no setter was sent.
    IntentJournaled,
    /// The setter was sent.
    Submitted,
}
/// One Battery tick with `notice` at `position`, then quiet ticks (after a
/// resume if it suspended). `None` once the tick has no such authorization.
async fn window_row(notice: Notice, position: usize) -> Option<(Window, bool)> {
    let r = scoped_rig();
    policy_and_enable(&r, 980).await;
    let before = writes(&r);
    source_battery(&r);
    let epoch = r.custody.lifecycle.active();
    if !notice_tick(&r, notice, position).await {
        r.stop().await;
        return None;
    }
    assert_ne!(r.custody.lifecycle.active(), epoch);
    let sent = writes(&r) - before;
    let window = match (sent, r.storage.pending()) {
        (0, false) => Window::Unsubmitted,
        (0, true) => Window::IntentJournaled,
        _ => Window::Submitted,
    };
    // A Prepared row this attempt retired as known unsubmitted.
    let retired = r.storage.progress() == Some(6);
    if window == Window::Unsubmitted {
        // Deferred before or after the claim, the intent is as it was.
        assert!(!r.runtime.release_only_for_tests(), "position {position}");
        assert!(r.storage.temporary_desired(), "position {position}");
    }
    if r.custody.lifecycle.active().is_none() {
        for _ in 0..2 {
            origin_tick(&r).await;
        }
        assert_eq!(writes(&r) - before, sent, "position {position}: low power");
        r.custody.lifecycle.ingress(
            POWER,
            openblade_domain::power_setting::PBT_APMRESUMEAUTOMATIC,
        );
    }
    let reads = r.port.state.lock().unwrap().temporary_reads;
    for _ in 0..4 {
        origin_tick(&r).await;
    }
    let total = writes(&r) - before;
    match window {
        Window::Unsubmitted => {
            assert_eq!(total, 1, "position {position}: written once later");
            assert_eq!(r.port.state.lock().unwrap().temporary_raw, 0);
            assert!(!r.storage.pending());
        }
        Window::IntentJournaled => {
            assert_eq!(total, 0, "position {position}: no setter after its intent");
            assert!(r.storage.pending());
        }
        Window::Submitted => {
            assert_eq!(total, 1, "position {position}: never replayed");
            // Not even reattempted: a retry would read the charge first.
            assert_eq!(
                r.port.state.lock().unwrap().temporary_reads,
                reads,
                "position {position}"
            );
        }
    }
    r.stop().await;
    Some((window, retired))
}
async fn window_map(notice: Notice, name: &str) {
    let mut rows = Vec::new();
    while let Some(row) = window_row(notice, rows.len()).await {
        rows.push(row);
        assert!(
            rows.len() < 64,
            "a tick has a bounded set of authorizations"
        );
    }
    // Past submission intent, no later position counts as unsubmitted.
    assert!(
        rows.windows(2).all(|w| w[0].0 <= w[1].0),
        "{name}: {rows:?}"
    );
    let count = |window| rows.iter().filter(|row| row.0 == window).count();
    let retired = rows.iter().filter(|row| row.1).count();
    // One letter per position: u unsubmitted, r unsubmitted after a
    // Prepared-only retirement, i intent journaled, s submitted.
    let map: String = rows
        .iter()
        .map(|row| match row {
            (Window::Unsubmitted, false) => 'u',
            (Window::Unsubmitted, true) => 'r',
            (Window::IntentJournaled, _) => 'i',
            (Window::Submitted, _) => 's',
        })
        .collect();
    println!(
        "{name}: {map}; {} positions; {} unsubmitted ({retired} after a Prepared-only retirement), {} with intent journaled, {} submitted",
        rows.len(),
        count(Window::Unsubmitted),
        count(Window::IntentJournaled),
        count(Window::Submitted),
    );
    // Both pre-claim authorizations (the capture's and the admission's), and
    // an attempt after a Prepared-only retirement, are retried.
    assert!(rows[..2].iter().all(|row| row.0 == Window::Unsubmitted));
    assert!(retired > 0, "{name}: {rows:?}");
    assert!(count(Window::Submitted) > 0, "{name}: {rows:?}");
}
#[tokio::test]
async fn tfc_release_a_second_unplug_at_each_authorization_of_the_tick_writes_the_cancel_once() {
    window_map(SECOND_UNPLUG, "second unplug").await;
}
#[tokio::test]
async fn tfc_release_a_session_notice_at_each_authorization_of_the_tick_writes_the_cancel_once() {
    window_map(SESSION, "session notice").await;
}
#[tokio::test]
async fn tfc_release_a_suspend_at_each_authorization_of_the_tick_writes_the_cancel_after_resume() {
    window_map(SUSPEND, "suspend").await;
}
#[tokio::test]
async fn tfc_release_a_notice_on_every_tick_defers_the_cancel_until_a_quiet_tick_writes_it_once() {
    // Each deferral needs a strictly newer epoch, and only an SCM notice
    // advances one, so retries are bounded by notices: there is no loop.
    let r = scoped_rig();
    policy_and_enable(&r, 984).await;
    let before = writes(&r);
    source_battery(&r);
    for (notice, position) in [
        (SECOND_UNPLUG, 0),
        (SESSION, 1),
        (RESUME, 4),
        (SECOND_UNPLUG, 12),
        (SESSION, 20),
    ] {
        assert!(notice_tick(&r, notice, position).await);
        assert_eq!(writes(&r), before, "a fenced attempt submits nothing");
        assert!(!r.storage.pending());
        assert!(!r.runtime.release_only_for_tests(), "nothing stays claimed");
    }
    origin_tick(&r).await;
    assert_eq!(writes(&r) - before, 1);
    assert_canceled_once(&r, before).await;
    r.stop().await;
}

// The unscoped fixture's proof arrives at the newer epoch.
async fn later_epoch_proof_defers_the_cancel(notice: Notice) {
    let r = Rig::new(MemoryFiles::default());
    policy_and_enable(&r, 980).await;
    let before = writes(&r);
    source_battery(&r);
    let epoch = r.custody.lifecycle.active().unwrap();
    assert!(notice_tick(&r, notice, 0).await);
    assert_eq!(r.custody.lifecycle.active(), Some(epoch + 1));
    assert_eq!(writes(&r), before, "a fenced attempt submits nothing");
    assert!(!r.runtime.release_only_for_tests(), "nothing is claimed");
    origin_tick(&r).await;
    assert_eq!(writes(&r) - before, 1, "the next tick writes the cancel");
    assert_canceled_once(&r, before).await;
    r.stop().await;
}
#[tokio::test]
async fn tfc_release_a_proof_from_the_epoch_of_a_second_unplug_defers_the_cancel_to_the_next_tick()
{
    later_epoch_proof_defers_the_cancel(SECOND_UNPLUG).await;
}
#[tokio::test]
async fn tfc_release_a_proof_from_the_epoch_of_a_resume_defers_the_cancel_to_the_next_tick() {
    later_epoch_proof_defers_the_cancel(RESUME).await;
}
#[tokio::test]
async fn tfc_release_a_proof_from_the_epoch_of_a_session_notice_defers_the_cancel_to_the_next_tick()
{
    later_epoch_proof_defers_the_cancel(SESSION).await;
}

#[tokio::test]
async fn tfc_release_a_refused_authorization_at_the_tick_epoch_pauses_once() {
    let r = scoped_rig();
    policy_and_enable(&r, 986).await;
    let before = writes(&r);
    // Hold the release so the claimed, abandoned owner stays observable.
    r.files.release_fault.store(1, Ordering::Release);
    r.custody.fresh_refuse.store(true, Ordering::Release);
    source_battery(&r);
    origin_tick(&r).await;
    assert!(r.runtime.release_only_for_tests(), "claimed by the refusal");
    let view = immediate_battery_view(&r, Some(SID)).await;
    assert_eq!(
        view["confirmedState"]["battery"]["temporaryFullCharge"],
        true
    );
    r.custody.fresh_refuse.store(false, Ordering::Release);
    r.files.release_fault.store(0, Ordering::Release);
    assert_paused_once(&r, before).await;
    assert!(!r.runtime.release_only_for_tests(), "its release settled");
    r.stop().await;
}
/// One Battery tick whose `position`th authorization meets a competing
/// controller at the tick's own epoch, then quiet ticks: the writes it sent
/// and whether it ended unsubmitted. `None` once the tick has no such
/// authorization.
async fn refusal_row(position: usize) -> Option<(usize, bool)> {
    let r = scoped_rig();
    policy_and_enable(&r, 990).await;
    let before = writes(&r);
    source_battery(&r);
    let epoch = r.custody.lifecycle.active();
    r.custody.refuse_at.store(position + 1, Ordering::Release);
    origin_tick(&r).await;
    if r.custody.refuse_at.swap(0, Ordering::AcqRel) != 0 {
        r.stop().await;
        return None;
    }
    assert_eq!(r.custody.lifecycle.active(), epoch);
    r.custody.fresh_refuse.store(false, Ordering::Release);
    let sent = writes(&r) - before;
    let unsubmitted = sent == 0 && !r.storage.pending();
    if unsubmitted {
        // Nothing fenced the tick, so the claim stands.
        assert_paused_once(&r, before).await;
    } else {
        for _ in 0..3 {
            source_battery(&r);
            origin_tick(&r).await;
        }
        assert_eq!(writes(&r) - before, sent, "position {position}");
    }
    r.stop().await;
    Some((sent, unsubmitted))
}
#[tokio::test]
async fn tfc_release_a_refusal_at_each_authorization_of_the_tick_epoch_is_never_retried() {
    let mut rows = Vec::new();
    while let Some(row) = refusal_row(rows.len()).await {
        rows.push(row);
        assert!(
            rows.len() < 64,
            "a tick has a bounded set of authorizations"
        );
    }
    assert!(rows.iter().all(|row| row.0 <= 1), "{rows:?}");
    // One letter per position: p paused unsubmitted, i intent journaled,
    // s submitted.
    let map: String = rows
        .iter()
        .map(|row| match row {
            (_, true) => 'p',
            (0, false) => 'i',
            _ => 's',
        })
        .collect();
    println!("refusal at the tick epoch: {map}; {} positions", rows.len());
    // Before and after the claim, an unsubmitted cancel pauses.
    assert!(rows[..3].iter().all(|row| row.1), "{rows:?}");
}
#[tokio::test]
async fn tfc_release_a_refusal_raced_by_a_notice_defers_once_then_pauses_at_the_new_epoch() {
    // The refusal outlives the notice, so the retry meets it at an unchanged
    // epoch: one deferral per notice, then the usual pause.
    let r = scoped_rig();
    policy_and_enable(&r, 987).await;
    let before = writes(&r);
    r.custody.fresh_refuse.store(true, Ordering::Release);
    source_battery(&r);
    assert!(notice_tick(&r, SECOND_UNPLUG, 0).await);
    assert!(!r.runtime.release_only_for_tests(), "deferred, not claimed");
    r.files.release_fault.store(1, Ordering::Release);
    origin_tick(&r).await;
    assert!(
        r.runtime.release_only_for_tests(),
        "claimed at the new epoch"
    );
    r.files.release_fault.store(0, Ordering::Release);
    r.custody.fresh_refuse.store(false, Ordering::Release);
    assert_paused_once(&r, before).await;
    r.stop().await;
}
#[tokio::test]
async fn tfc_release_a_refused_capture_after_a_session_switch_pauses_once() {
    // Another session holds the console at the tick's own epoch.
    let r = scoped_rig();
    policy_and_enable(&r, 990).await;
    let before = writes(&r);
    r.files.release_fault.store(1, Ordering::Release);
    r.custody.current_session.store(8, Ordering::Release);
    source_battery(&r);
    origin_tick(&r).await;
    assert!(r.runtime.release_only_for_tests(), "claimed by the refusal");
    r.files.release_fault.store(0, Ordering::Release);
    r.custody.current_session.store(7, Ordering::Release);
    assert_paused_once(&r, before).await;
    assert!(!r.runtime.release_only_for_tests(), "its release settled");
    r.stop().await;
}
#[tokio::test]
async fn tfc_release_a_changed_owner_generation_pauses_the_cancel_once() {
    let r = Rig::new(MemoryFiles::default());
    policy_and_enable(&r, 991).await;
    let before = writes(&r);
    r.custody.generation.store(8, Ordering::Release);
    source_battery(&r);
    origin_tick(&r).await;
    assert_paused_once(&r, before).await;
    r.stop().await;
}
#[tokio::test]
async fn tfc_release_a_replaced_user_storage_pauses_the_cancel_once() {
    let r = Rig::new(MemoryFiles::default());
    policy_and_enable(&r, 992).await;
    let before = writes(&r);
    r.custody.other_storage.store(true, Ordering::Release);
    source_battery(&r);
    origin_tick(&r).await;
    assert_paused_once(&r, before).await;
    r.stop().await;
}
#[tokio::test]
async fn tfc_release_an_owner_without_a_logon_identity_pauses_the_cancel_once() {
    let r = Rig::new(MemoryFiles::default());
    r.custody
        .authentication
        .store(usize::MAX, Ordering::Release);
    policy_and_enable(&r, 993).await;
    let before = writes(&r);
    source_battery(&r);
    origin_tick(&r).await;
    assert_paused_once(&r, before).await;
    r.stop().await;
}
#[tokio::test]
async fn tfc_release_pending_storage_pauses_the_cancel_without_a_getter() {
    let r = Rig::new(MemoryFiles::default());
    policy_and_enable(&r, 994).await;
    let before = writes(&r);
    r.storage.quarantine_settlement().unwrap();
    let reads = r.port.state.lock().unwrap().temporary_reads;
    for _ in 0..4 {
        source_battery(&r);
        origin_tick(&r).await;
    }
    assert_eq!(
        r.port.state.lock().unwrap().temporary_reads,
        reads,
        "no getter"
    );
    assert_eq!(writes(&r), before);
    r.stop().await;
}
#[tokio::test]
async fn tfc_release_a_fatal_capture_during_the_battery_event_ends_the_tick_without_a_write() {
    let r = Rig::new(MemoryFiles::default());
    policy_and_enable(&r, 995).await;
    let before = writes(&r);
    source_battery(&r);
    r.fatal_capture.store(true, Ordering::Release);
    let error = r
        .source
        .lock()
        .await
        .process_power_source()
        .await
        .unwrap_err();
    assert!(!is_release_deferred(&error), "{error}");
    assert_eq!(writes(&r), before);
}
#[tokio::test]
async fn tfc_release_a_possibly_submitted_cancel_after_a_deferral_is_never_retried() {
    let r = scoped_rig();
    policy_and_enable(&r, 988).await;
    let before = writes(&r);
    source_battery(&r);
    assert!(notice_tick(&r, SECOND_UNPLUG, 12).await);
    assert_eq!(writes(&r), before);
    r.port.state.lock().unwrap().temporary_bad_ack = true;
    origin_tick(&r).await;
    assert_eq!(writes(&r) - before, 1);
    assert!(r.storage.pending(), "the setter's result is uncertain");
    r.port.state.lock().unwrap().temporary_bad_ack = false;
    for _ in 0..3 {
        source_battery(&r);
        origin_tick(&r).await;
    }
    assert_eq!(writes(&r) - before, 1, "no setter replay");
    assert!(r.storage.pending());
    r.stop().await;
}

#[tokio::test]
async fn tfc_release_actual_loop_releases_an_ended_quota_while_a_pipe_policy_is_parked() {
    // The serving loop's own origin tick runs while a pipe-served battery
    // request waits before its Prepared row, and the request still answers.
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
            battery_request(940, "setFullChargeOverride", json!(true)),
        )
        .await;
        assert_eq!(response["result"]["status"], 1, "{response}");
        assert!(r.storage.temporary_capacity_for_tests().0 > 0);
        let parked = Arc::new(tokio::sync::Notify::new());
        let at = parked.clone();
        let (allow_tx, allow_rx) = std::sync::mpsc::channel::<()>();
        let allow = StdMutex::new(allow_rx);
        r.storage
            .battery_append_hook_for_tests(Arc::new(move |stage| {
                if stage == "before-prepared" {
                    at.notify_one();
                    allow.lock().unwrap().recv_timeout(HANG_GUARD).unwrap();
                }
            }));
        // The policy's own read sees the charge off, which ends the positive
        // obligation that the origin tick then releases.
        r.port.state.lock().unwrap().temporary_raw = 0;
        let mut policy = Box::pin(outer_request(
            &tx,
            battery_request(941, "setBatteryPolicy", json!({"chargeLimitPercent":70})),
        ));
        tokio::select! {v=&mut policy=>panic!("policy settled before Prepared: {v}"),_=parked.notified()=>{}}
        tokio::time::timeout(HANG_GUARD, async {
            while r.storage.temporary_capacity_for_tests() != (0, None) {
                tokio::time::sleep(Duration::from_millis(10)).await;
            }
        })
        .await
        .unwrap();
        assert_eq!(r.storage.progress(), Some(72), "the policy is still parked");
        allow_tx.send(()).unwrap();
        let response = policy.await;
        assert_eq!(response["result"]["status"], 1, "{response}");
        assert_eq!(r.storage.progress(), Some(62));
        assert_eq!(writes(&r), 2);
        controls.handle(windows_sys::Win32::System::Services::SERVICE_CONTROL_STOP);
    };
    let (result, ()) = tokio::time::timeout(HANG_GUARD, async { tokio::join!(service, exercise) })
        .await
        .unwrap();
    result.unwrap();
}
#[tokio::test]
async fn tfc_release_battery_evidence_behind_a_possibly_submitted_enable_reports_its_reconciliation()
 {
    // The unresolved enable holds the journal: the event takes no owner,
    // writes nothing and says why the cancel waits.
    let r = Rig::new(MemoryFiles::default());
    r.get().await;
    let release = Arc::new(AtomicBool::new(false));
    r.port.state.lock().unwrap().held_write = Some((1, release.clone()));
    let mut request =
        Box::pin(r.request(battery_request(995, "setFullChargeOverride", json!(true))));
    tokio::select! {
        _ = &mut request => panic!("held setter unexpectedly completed"),
        _ = async { while writes(&r) != 1 { tokio::time::sleep(Duration::from_millis(5)).await; } } => {}
    }
    drop(request);
    release.store(true, Ordering::Release);
    r.reader.drain_active().await.unwrap();
    assert!(r.storage.pending());
    let before = immediate_battery_view(&r, Some(SID)).await;
    source_battery(&r);
    origin_tick(&r).await;
    let view = immediate_battery_view(&r, Some(SID)).await;
    let reason = r.storage.pending_reason().unwrap();
    assert!(
        view["message"].as_str().unwrap().ends_with(reason),
        "{before} -> {view}"
    );
    assert_eq!(writes(&r), 1);
    assert!(r.storage.pending());
    r.stop().await;
}

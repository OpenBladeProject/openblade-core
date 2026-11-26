//! Installed `setThermalControlPolicy` and
//! `acknowledgeThermalPolicyUpgradeDisclosure` through the real framed
//! dispatcher, and the `thermalConvergence` member the status then carries.
//! The store is the in-memory model; no hardware, pipe or operating-system
//! supplier is reached, and every case asserts that no mailbox command was
//! written. The managed replies are replayed from
//! `tests/data/thermal-convergence-vectors.json`.
use super::*;

const VECTORS: &str = include_str!("../../../../../tests/data/thermal-convergence-vectors.json");
const PREFERENCES: &str = "battery-preferences.json";
const RECOVERY: &str =
    "Thermal policy changes are unavailable while configuration recovery is required.";
const DISCLOSURE_RECOVERY: &str = "The thermal policy disclosure cannot be acknowledged while configuration recovery is required.";
const SESSION: &str =
    "The active local Windows session changed before the thermal policy could be saved.";
const DISCLOSURE_SESSION: &str =
    "The active local Windows session changed before the disclosure could be acknowledged.";
const STRICT_STORED: &str = "Strict thermal control is active; externally drifted thermal state remains advisory until an explicit command.";

fn set_policy(n: u8, policy: Value) -> Value {
    json!({
        "operation": "setThermalControlPolicy", "version": 91,
        "requestId": format!("aaaaaaaa-bbbb-cccc-dddd-{n:012}"),
        "policy": policy,
    })
}

fn acknowledge(n: u8) -> Value {
    json!({
        "operation": "acknowledgeThermalPolicyUpgradeDisclosure", "version": 91,
        "requestId": format!("aaaaaaaa-bbbb-cccc-dddd-{n:012}"),
    })
}

fn result(status: u8, message: &str, n: u8) -> Value {
    json!({
        "frame": "response", "version": 91,
        "requestId": format!("aaaaaaaa-bbbb-cccc-dddd-{n:012}"),
        "success": true, "error": null, "status": null,
        "result": {"status": status, "message": message},
        "archive": null, "errorCode": null,
    })
}

/// The saved fixture aggregate with `policy` and `disclosure`.
fn files_with(policy: i32, disclosure: i32) -> MemoryFiles {
    let files = MemoryFiles::default();
    files.profiles(1);
    let mut document = files.document(PREFERENCES);
    document["preferences"]["thermalControlPolicy"] = json!(policy);
    document["preferences"]["thermalPolicyUpgradeDisclosure"] = json!(disclosure);
    files.put(PREFERENCES, document);
    files.commit_configuration();
    files
}

fn stored(files: &MemoryFiles) -> (i64, i64) {
    let document = files.document(PREFERENCES);
    (
        document["preferences"]["thermalControlPolicy"]
            .as_i64()
            .unwrap(),
        document["preferences"]["thermalPolicyUpgradeDisclosure"]
            .as_i64()
            .unwrap(),
    )
}

fn no_hardware(rig: &Rig) {
    let port = rig.port.state.lock().unwrap();
    assert_eq!(port.writes, 0, "a policy change writes no mailbox command");
}

/// One framed exchange; empty when the Service closes without answering.
async fn exchange_as(rig: &Rig, request: &Value, caller: Caller) -> Vec<u8> {
    let (reply, served) = exchange(rig, request, caller).await;
    assert!(
        !matches!(served, Err(ExchangeError::Runtime(_))),
        "a client fault never fails the Service"
    );
    reply
}

/// One framed exchange and how the connection ended.
async fn exchange(
    rig: &Rig,
    request: &Value,
    caller: Caller,
) -> (Vec<u8>, Result<(), ExchangeError>) {
    let (mut client, server) = duplex(65536);
    let source = rig.source.clone();
    let publications = source.lock().await.publications();
    let served = serve_connection(
        Box::new(SidStream {
            inner: test_stream(server, caller, Fault::None),
            sid: SID.to_owned(),
        }),
        source,
        publications,
        Rc::new(Subscriptions::new(2)),
    );
    let payload = serde_json::to_vec(request).unwrap();
    let exchange = async {
        crate::service_frame::write_payload(&mut client, &payload)
            .await
            .unwrap();
        let mut reply = Vec::new();
        tokio::io::AsyncReadExt::read_to_end(&mut client, &mut reply)
            .await
            .unwrap();
        reply
    };
    let (served, reply) = tokio::join!(served, exchange);
    (reply, served)
}

async fn convergence(rig: &Rig) -> Value {
    rig.get().await["status"]["thermalConvergence"].clone()
}

fn paused(policy: i32, disclosure: i32, reason: i32) -> Value {
    json!({
        "policy": policy, "disclosure": disclosure, "state": 3,
        "affectedComponents": [], "reason": reason, "explicitReapplyRequired": false,
    })
}

#[tokio::test]
async fn managed_reply_vectors_replay_through_the_dispatcher() {
    let vectors: Value = serde_json::from_str(VECTORS).unwrap();
    let mut replayed = 0;
    for row in vectors["replies"].as_array().unwrap() {
        let name = row["name"].as_str().unwrap();
        let policy = row["policy"].as_i64().unwrap() as i32;
        let disclosure = row["disclosure"].as_i64().unwrap() as i32;
        let files = files_with(policy, disclosure);
        let rig = Rig::new(files.clone());
        rig.get().await;
        if row["quarantined"] == true {
            // Another writer leaves a transaction record after the open.
            files.bytes.lock().unwrap().insert(
                "battery-configuration.commit.json.recovery-required".into(),
                b"{}".to_vec(),
            );
        }
        let prior = files.bytes.lock().unwrap().clone();
        let request = match row["request"]["setPolicy"].as_i64() {
            Some(policy) => set_policy(1, json!(policy)),
            None => acknowledge(1),
        };
        if row["sessionChanged"] == true {
            // Native admits a mutation from the control session only, so a
            // caller outside it is closed without a reply where managed's
            // `stillControlsSession` answers Blocked.
            let other = Caller {
                control_session: Some(9),
                ..USER
            };
            assert!(
                exchange_as(&rig, &request, other).await.is_empty(),
                "{name}"
            );
            assert_eq!(*files.bytes.lock().unwrap(), prior, "{name}");
            assert_eq!(row["stored"]["written"], false, "{name}");
            no_hardware(&rig);
            rig.stop().await;
            continue;
        }

        let response = rig.request(request).await;

        assert_eq!(response["result"], row["result"], "{name}");
        let written = *files.bytes.lock().unwrap() != prior;
        assert_eq!(json!(written), row["stored"]["written"], "{name}");
        let (stored_policy, stored_disclosure) = stored(&files);
        assert_eq!(json!(stored_policy), row["stored"]["policy"], "{name}");
        assert_eq!(
            json!(stored_disclosure),
            row["stored"]["disclosure"],
            "{name}"
        );
        no_hardware(&rig);
        replayed += 1;
        rig.stop().await;
    }
    assert_eq!(replayed, 12);
}

/// A fresh exact-device reading (observeState), then the published member.
async fn observed(rig: &Rig, n: u8) -> Value {
    let observed = rig
        .request(json!({"operation":"observeState","version":91,"requestId":format!("eeeeeeee-bbbb-cccc-dddd-{n:012}"),"includeLiveReadings":false,"requestedLiveReadings":0,"effectiveLiveReadings":0}))
        .await;
    assert_eq!(observed["success"], true, "{observed}");
    convergence(rig).await
}

/// The rig on battery, reading `selector` with Automatic fans. Native status
/// reports every external class as the coarse class 3, which is unsettled
/// power, so the battery class is the one a status classifies.
fn on_battery(rig: &Rig, selector: u8) {
    let mut state = rig.port.state.lock().unwrap();
    state.actual = 0;
    state.selectors = [selector; 2];
    state.fans = [0; 2];
    drop(state);
    rig.metadata.store(1, Ordering::Release);
}

fn status_of(state: i32, components: &[i32], reason: i32, explicit: bool) -> Value {
    json!({
        "policy": 0, "disclosure": 0, "state": state, "affectedComponents": components,
        "reason": reason, "explicitReapplyRequired": explicit,
    })
}

#[tokio::test]
async fn a_stored_policy_is_published_and_strict_stays_advisory() {
    let files = files_with(0, 0);
    let rig = Rig::new(files.clone());
    // Nothing is projected before this user's first request retains it, and
    // without a fresh reading the power class is not settled.
    assert!(convergence(&rig).await.is_null());
    assert_eq!(
        rig.request(set_policy(1, json!(0))).await["result"]["status"],
        0
    );
    assert_eq!(convergence(&rig).await, paused(0, 0, 10));
    // On AC the status carries the coarse class 3, so power is unsettled.
    assert_eq!(observed(&rig, 1).await, paused(0, 0, 10));
    // On battery the fixture reads Battery Saver with Automatic fans, as saved.
    on_battery(&rig, 3);
    assert_eq!(observed(&rig, 2).await, status_of(0, &[], 0, false));

    let publications = rig.source.lock().await.publications().unwrap();
    assert_eq!(
        rig.request(set_policy(2, json!(1))).await,
        result(0, STRICT_STORED, 2)
    );

    // The stored change is published with its reply, before any status read.
    let publication = publications.borrow().clone();
    let active = serde_json::to_value(&publication.active).unwrap();
    assert_eq!(active["thermalConvergence"], paused(1, 0, 1));
    assert_eq!(
        rig.request(set_policy(3, json!(1))).await,
        result(0, "Strict thermal control is already active.", 3)
    );

    assert_eq!(convergence(&rig).await, paused(1, 0, 1));
    assert_eq!(stored(&files), (1, 0));
    no_hardware(&rig);
    // Only the owner's view carries the private policy.
    let publication = publications.borrow().clone();
    let inactive = serde_json::to_value(&publication.inactive).unwrap();
    assert!(inactive.get("thermalConvergence").is_none(), "{inactive}");
    let active = serde_json::to_value(&publication.active).unwrap();
    assert_eq!(active["thermalConvergence"], paused(1, 0, 1));
    let public = rig
        .request_as(
            json!({"operation":"getStatus","version":91,"requestId":"bbbbbbbb-cccc-dddd-eeee-000000000077"}),
            USER,
            "S-1-5-21-111-222-333-1002",
        )
        .await;
    assert!(
        public["status"].get("thermalConvergence").is_none(),
        "{public}"
    );
    rig.stop().await;
}

#[tokio::test]
async fn a_pending_disclosure_pauses_maintain_until_acknowledged() {
    let files = files_with(0, 1);
    let rig = Rig::new(files.clone());
    on_battery(&rig, 3);
    rig.request(set_policy(1, json!(0))).await;
    assert_eq!(observed(&rig, 1).await, paused(0, 1, 2));

    let response = rig.request(acknowledge(2)).await;

    assert_eq!(response["result"]["status"], 0);
    assert_eq!(stored(&files), (0, 2));
    let mut matched = status_of(0, &[], 0, false);
    matched["disclosure"] = json!(2);
    assert_eq!(convergence(&rig).await, matched);
    no_hardware(&rig);
    rig.stop().await;
}

/// Only the owner's view carries the member, so the views that do not carry
/// it skip its store reads (`Snapshot::carries_thermal_convergence`).
#[tokio::test]
async fn only_the_owners_view_reads_the_saved_policy() {
    let files = files_with(0, 0);
    let rig = Rig::new(files.clone());
    assert_eq!(
        rig.request(set_policy(1, json!(1))).await["result"]["status"],
        0
    );
    let publications = rig.source.lock().await.publications().unwrap();
    let before = files.preferences_reads.load(Ordering::Acquire);
    assert_eq!(convergence(&rig).await, paused(1, 0, 1));
    let reads = files.preferences_reads.load(Ordering::Acquire) - before;
    let publication = publications.borrow().clone();
    let inactive = serde_json::to_value(&publication.inactive).unwrap();
    assert!(inactive.get("thermalConvergence").is_none(), "{inactive}");
    // The owner's projection alone: each other view that derived the
    // member would read the saved policy once more. The same projection
    // reads the saved Fan Stop settings when that composition serves them.
    let expected = if cfg!(feature = "service-fan-stop-v1") {
        7
    } else {
        5
    };
    assert_eq!(reads, expected, "preferences reads for one publication");
    rig.stop().await;
}

#[tokio::test]
async fn a_saved_drift_is_published_without_a_write_under_either_policy() {
    // Saved Battery Saver against a Balanced reading.
    let files = files_with(0, 0);
    let rig = Rig::new(files.clone());
    on_battery(&rig, 0);
    rig.request(set_policy(1, json!(0))).await;
    // Managed would converge; native has no loop and says so.
    assert_eq!(observed(&rig, 1).await, paused(0, 0, 21));

    rig.request(set_policy(2, json!(1))).await;

    assert_eq!(convergence(&rig).await, paused(1, 0, 1));
    rig.request(set_policy(3, json!(0))).await;
    assert_eq!(observed(&rig, 2).await, paused(0, 0, 21));
    assert_eq!(rig.port.state.lock().unwrap().selectors, [0; 2]);
    no_hardware(&rig);
    rig.stop().await;
}

#[tokio::test]
async fn a_competing_controller_is_published_as_managed_reports_it() {
    let files = files_with(0, 0);
    let rig = Rig::new(files.clone());
    on_battery(&rig, 3);
    rig.request(set_policy(1, json!(0))).await;
    assert_eq!(observed(&rig, 1).await, status_of(0, &[], 0, false));
    // A competing controller refuses the capture, so the private projection
    // is unavailable rather than guessed.
    rig.custody.controller.store(true, Ordering::Release);
    assert!(observed(&rig, 2).await.is_null());
    rig.custody.controller.store(false, Ordering::Release);
    assert_eq!(observed(&rig, 3).await, status_of(0, &[], 0, false));
    no_hardware(&rig);
    rig.stop().await;
}

const POWER: u32 = 13;
const POWER_STATUS: u32 = 10;
const SUSPEND: u32 = 4;
const RESUME: u32 = 18;

/// Windows reports `battery` (or AC), with the SCM power notice that samples
/// the Windows power kind before the lifecycle sees it (`native::entry`).
fn power_notice(rig: &Rig, battery: bool) {
    rig.port.state.lock().unwrap().windows_on_battery = Some(Some(battery));
    rig.metadata
        .store(if battery { 1 } else { 0 }, Ordering::Release);
    Fresh::power_generation(rig.custody.as_ref());
    rig.custody.lifecycle.ingress(POWER, POWER_STATUS);
}

/// The projection needs a write capture, which low power refuses, so the
/// member is absent there; it never reads the reconciliation's gate probe,
/// whose reads the trigger tests count.
#[tokio::test]
async fn low_power_leaves_the_member_absent_without_reading_the_gate() {
    let files = files_with(0, 0);
    let rig = Rig::new(files.clone());
    let gate_reads = Rc::new(std::cell::Cell::new(0u32));
    let counted = gate_reads.clone();
    let lifecycle = rig.custody.clone();
    rig.runtime.read_low_power(move || {
        counted.set(counted.get() + 1);
        lifecycle.lifecycle.low_power()
    });
    let epoch = rig.custody.clone();
    rig.runtime
        .read_admission_epoch(move || epoch.lifecycle.active());
    on_battery(&rig, 3);
    rig.request(set_policy(1, json!(0))).await;
    assert_eq!(observed(&rig, 1).await, status_of(0, &[], 0, false));

    rig.custody.lifecycle.ingress(POWER, SUSPEND);

    assert!(convergence(&rig).await.is_null());
    rig.custody.lifecycle.ingress(POWER, RESUME);
    assert_eq!(convergence(&rig).await, status_of(0, &[], 0, false));
    assert_eq!(gate_reads.get(), 0);
    no_hardware(&rig);
    rig.stop().await;
}

/// Status reports AC as the coarse class 3. The class this user's own step
/// classified stands in for it until the Windows power kind changes.
#[tokio::test]
async fn ac_is_classified_by_the_last_step_until_the_power_kind_changes() {
    let files = files_with(0, 0);
    let rig = Rig::new(files.clone());
    rig.request(set_policy(1, json!(0))).await;
    assert_eq!(convergence(&rig).await, paused(0, 0, 10));

    let applied = rig.set(2, value(2, 1)).await;

    assert_eq!(applied["result"]["status"], 1, "{applied}");
    assert_eq!(convergence(&rig).await, status_of(0, &[], 0, false));
    assert_eq!(observed(&rig, 1).await, status_of(0, &[], 0, false));
    // A notice that keeps the Windows kind keeps the class.
    power_notice(&rig, false);
    assert_eq!(convergence(&rig).await, status_of(0, &[], 0, false));
    // Battery: the battery class is the status's own, and the saved Battery
    // Saver differs from the confirmed override.
    power_notice(&rig, true);
    assert_eq!(convergence(&rig).await, paused(0, 0, 21));
    // Back on AC, the class read before the battery switch is stale.
    power_notice(&rig, false);
    assert_eq!(convergence(&rig).await, paused(0, 0, 10));
    assert_eq!(observed(&rig, 2).await, paused(0, 0, 10));
    rig.stop().await;
}

/// The member is derived at the class this user's step classified at the
/// current Windows power generation (`convergence_power`): PluggedIn or
/// USB-C. An unclassified or battery reading kept at that generation, an
/// older generation, no generation, or no fresh AC presence keeps the
/// status's class, as battery and an unknown class do.
#[test]
fn the_member_uses_only_a_class_classified_at_this_generation() {
    use crate::installed::readiness::thermal_mutation::convergence_power;
    struct Windows(Option<bool>, Option<u64>);
    impl Fresh for Windows {
        fn on_battery(&self) -> Option<bool> {
            self.0
        }
        fn power_generation(&self) -> Option<u64> {
            self.1
        }
        fn authorize(&self) -> Option<ReadAdmission> {
            None
        }
    }
    let files = MemoryFiles::default();
    files.profiles(1);
    let storage = Storage::open(Box::new(files), SID).unwrap();
    let class = |query, battery, generation| {
        convergence_power(query, &Windows(battery, generation), &storage).unwrap()
    };
    assert_eq!(class(3, Some(false), Some(7)), 3);
    for (observed, expected) in [(0, 0), (2, 2), (3, 3), (1, 3)] {
        storage.observe_power(observed, 7).unwrap();
        assert_eq!(class(3, Some(false), Some(7)), expected, "{observed}");
        assert_eq!(class(3, Some(false), Some(8)), 3, "{observed}");
        assert_eq!(class(3, Some(false), None), 3, "{observed}");
        assert_eq!(class(3, None, Some(7)), 3, "{observed}");
    }
    storage.observe_power(2, 7).unwrap();
    assert_eq!(class(1, Some(true), Some(7)), 1);
    assert_eq!(class(-1, Some(false), Some(7)), -1);
}

/// The fan request's own publication and the projection it retains read
/// the same classified power, so the member does not flip between them, and
/// manual fans OpenBlade wrote match while fans it did not write are
/// ambiguous.
#[tokio::test]
async fn a_fan_request_publishes_the_member_its_projection_keeps() {
    let rig = fan_rig(0, 0);
    rig.request(set_policy(1, json!(0))).await;
    assert_eq!(convergence(&rig).await, paused(0, 0, 10));
    let publications = rig.source.lock().await.publications().unwrap();

    let result = rig.request(fan_request(2, Some(1), 3700)).await;

    assert_eq!(result["result"]["status"], 1, "{result}");
    let published = serde_json::to_value(&publications.borrow().active).unwrap();
    let matched = status_of(0, &[], 0, false);
    assert_eq!(published["thermalConvergence"], matched, "{published}");
    assert_eq!(convergence(&rig).await, matched);
    assert_eq!(observed(&rig, 1).await, matched);
    // A fan change outside OpenBlade ends the ownership; manual fans that
    // return are no longer OpenBlade's own.
    rig.port.state.lock().unwrap().fans = [0; 2];
    assert_eq!(
        observed(&rig, 2).await,
        status_of(3, &[], 21, false),
        "Automatic against saved manual fans would converge"
    );
    rig.port.state.lock().unwrap().fans = [1; 2];
    assert_eq!(observed(&rig, 3).await, status_of(3, &[1], 16, true));
    rig.stop().await;
}

/// A journal fence leaves the saved policy standing and pauses for
/// reconciliation, as managed's pending write confirmations do; only a
/// configuration fence reports the quarantine.
#[tokio::test]
async fn a_journal_fence_pauses_for_reconciliation_under_the_saved_policy() {
    let files = files_with(0, 0);
    files.fail_prepared.store(1, Ordering::Release);
    let rig = Rig::new(files.clone());
    on_battery(&rig, 3);
    rig.request(set_policy(1, json!(0))).await;
    assert_eq!(observed(&rig, 1).await, status_of(0, &[], 0, false));

    let refused = rig.set(2, value(4, 1)).await;

    let message = refused["result"]["message"].as_str().unwrap();
    assert!(
        message.contains("journal preparation could not be confirmed"),
        "{refused}"
    );
    assert_eq!(convergence(&rig).await, paused(0, 0, 17));
    // The configuration is still admitted, so the policy can change.
    assert_eq!(
        rig.request(set_policy(3, json!(1))).await,
        result(0, STRICT_STORED, 3)
    );
    assert_eq!(convergence(&rig).await, paused(1, 0, 1));
    assert_eq!(stored(&files), (1, 0));
    rig.stop().await;
}

/// A store read that fails before the transaction replies Failed and the
/// Service keeps serving; fatal evidence still ends it.
#[tokio::test]
async fn a_store_read_error_fails_the_change_and_fatal_evidence_stays_fatal() {
    let files = files_with(0, 1);
    let rig = Rig::new(files.clone());
    rig.get().await;
    let prior = files.bytes.lock().unwrap().clone();
    files.fail_preferences_read.store(1, Ordering::Release);

    let response = rig.request(set_policy(1, json!(1))).await;

    assert_eq!(response["result"]["status"], 3, "{response}");
    let message = response["result"]["message"].as_str().unwrap();
    assert!(
        message.starts_with(
            "The requested intent was rejected: The saved configuration could not be read: "
        ) && message.ends_with(". The saved configuration is unchanged."),
        "{message}"
    );
    assert_eq!(*files.bytes.lock().unwrap(), prior);
    assert_eq!(
        rig.request(acknowledge(2)).await["result"]["status"],
        0,
        "the Service still serves"
    );

    no_hardware(&rig);
    rig.stop().await;

    // A fresh Service, so the save's own read is the first one: nothing
    // projects this user's policy before the request.
    let files = files_with(0, 1);
    let rig = Rig::new(files.clone());
    rig.get().await;
    let prior = files.bytes.lock().unwrap().clone();
    files.fail_preferences_read.store(2, Ordering::Release);
    let (reply, served) = exchange(&rig, &set_policy(3, json!(1)), USER).await;
    assert!(reply.is_empty(), "fatal evidence is not answered");
    assert!(
        matches!(served, Err(ExchangeError::Runtime(_))),
        "fatal evidence ends the Service"
    );
    assert_eq!(files.fail_preferences_read.load(Ordering::Acquire), 0);
    assert_eq!(*files.bytes.lock().unwrap(), prior);
    no_hardware(&rig);
    rig.stop().await;
}

/// A policy request waits behind a held operation gate for up to a
/// thermal request's budget and is answered.
#[tokio::test]
async fn a_policy_request_behind_a_held_gate_is_answered() {
    let files = files_with(0, 0);
    let rig = Rig::new(files.clone());
    rig.get().await;
    let gate = rig.runtime.operation_gate_for_tests();
    let held = gate.lock().await;
    let release = async {
        tokio::time::sleep(std::time::Duration::from_secs(3)).await;
        drop(held);
    };

    let (response, ()) = tokio::join!(rig.request(set_policy(1, json!(1))), release);

    assert_eq!(response, result(0, STRICT_STORED, 1));
    rig.stop().await;
}

#[tokio::test]
async fn a_competing_controller_or_another_session_refuses_the_change() {
    let files = files_with(0, 1);
    let rig = Rig::new(files.clone());
    rig.get().await;
    let prior = files.bytes.lock().unwrap().clone();
    assert_eq!(rig.runtime.owed_attempts(SID, 7), Some(0));
    // The session refusal keeps the pair's debt; the controller refusal
    // drops it and ends startup, as a refused save's capture does
    // (`Runtime::drop_for_controller`).
    for (n, refuse, owed) in [(1u8, 1u8, Some(0)), (2, 0, None)] {
        match refuse {
            0 => rig.custody.controller.store(true, Ordering::Release),
            _ => rig.custody.current_session.store(8, Ordering::Release),
        }
        for request in [set_policy(n, json!(1)), acknowledge(n)] {
            let response = rig.request(request).await;
            assert_eq!(response["result"]["status"], 2, "{response}");
            let message = response["result"]["message"].as_str().unwrap();
            assert!(
                message.starts_with("Saved configuration or the current session is unavailable: "),
                "{message}"
            );
        }
        rig.custody.controller.store(false, Ordering::Release);
        rig.custody.current_session.store(7, Ordering::Release);
        assert_eq!(rig.runtime.owed_attempts(SID, 7), owed, "{refuse}");
    }
    assert_eq!(*files.bytes.lock().unwrap(), prior);
    no_hardware(&rig);
    // Once admitted again, the change is stored.
    assert_eq!(
        rig.request(set_policy(3, json!(1))).await["result"]["status"],
        0
    );
    assert_eq!(stored(&files), (1, 1));
    rig.stop().await;
}

/// A write the store refuses is restored and replied as Failed with the
/// store's reason; nothing is published, and the next admitted change is
/// stored.
#[tokio::test]
async fn a_refused_store_write_fails_the_change_and_keeps_the_aggregate() {
    let files = files_with(0, 1);
    let rig = Rig::new(files.clone());
    rig.get().await;
    let prior = files.bytes.lock().unwrap().clone();
    let publications = rig.source.lock().await.publications().unwrap();
    files.fail.store(true, Ordering::Release);

    for (request, n) in [(set_policy(1, json!(1)), 1), (acknowledge(2), 2)] {
        assert_eq!(
            rig.request(request).await,
            result(
                3,
                "The requested intent was rejected: The configuration aggregate could not be replaced. The saved configuration is unchanged.",
                n
            )
        );
    }

    files.fail.store(false, Ordering::Release);
    assert_eq!(*files.bytes.lock().unwrap(), prior);
    let active = serde_json::to_value(&publications.borrow().active).unwrap();
    assert!(
        matches!(
            active["thermalConvergence"]["policy"].as_i64(),
            None | Some(0)
        ),
        "{active}"
    );
    assert_eq!(
        rig.request(set_policy(3, json!(1))).await["result"]["status"],
        0
    );
    assert_eq!(stored(&files), (1, 1));
    no_hardware(&rig);
    rig.stop().await;
}

#[tokio::test]
async fn a_caller_outside_the_control_session_is_closed_without_a_change() {
    let files = files_with(0, 1);
    let rig = Rig::new(files.clone());
    rig.get().await;
    let prior = files.bytes.lock().unwrap().clone();
    for caller in [
        Caller {
            control_session: Some(9),
            ..USER
        },
        Caller {
            control_session: None,
            ..USER
        },
    ] {
        for request in [set_policy(1, json!(1)), acknowledge(1)] {
            assert!(exchange_as(&rig, &request, caller).await.is_empty());
        }
    }
    // SYSTEM reaches the request and is answered with managed's session
    // refusal, as managed answers a caller that does not hold the session.
    let system = Caller {
        logon: Logon::System,
        ..USER
    };
    for (request, message) in [
        (set_policy(1, json!(1)), SESSION),
        (acknowledge(1), DISCLOSURE_SESSION),
    ] {
        let reply = exchange_as(&rig, &request, system).await;
        assert!(reply.len() > 4, "SYSTEM is answered");
        let reply: Value = serde_json::from_slice(&reply[4..]).unwrap();
        assert_eq!(reply, result(2, message, 1));
    }
    assert_eq!(*files.bytes.lock().unwrap(), prior);
    no_hardware(&rig);
    rig.stop().await;
}

#[tokio::test]
async fn a_preserved_aggregate_blocks_both_requests_untouched() {
    for tamper in [
        |files: &MemoryFiles| {
            files.bytes.lock().unwrap().insert(
                "battery-configuration.commit.json.recovery-required".into(),
                b"{}".to_vec(),
            );
        },
        |files: &MemoryFiles| {
            files
                .bytes
                .lock()
                .unwrap()
                .get_mut(PREFERENCES)
                .unwrap()
                .push(b' ');
        },
    ] {
        let files = files_with(0, 1);
        let rig = Rig::new(files.clone());
        rig.get().await;
        tamper(&files);
        let prior = files.bytes.lock().unwrap().clone();

        assert_eq!(
            rig.request(set_policy(1, json!(1))).await,
            result(2, RECOVERY, 1)
        );
        assert_eq!(
            rig.request(acknowledge(2)).await,
            result(2, DISCLOSURE_RECOVERY, 2)
        );

        assert_eq!(*files.bytes.lock().unwrap(), prior);
        // A preserved aggregate makes the private projection unavailable,
        // where managed reports Strict with ConfigurationQuarantined.
        assert!(convergence(&rig).await.is_null());
        no_hardware(&rig);
        rig.stop().await;
    }
}

#[tokio::test]
async fn a_frame_managed_refuses_is_closed_without_a_change() {
    let files = files_with(0, 1);
    let rig = Rig::new(files.clone());
    rig.get().await;
    let prior = files.bytes.lock().unwrap().clone();
    let mut unknown = set_policy(1, json!(1));
    unknown["extra"] = json!(true);
    let mut missing = set_policy(1, json!(1));
    missing.as_object_mut().unwrap().remove("policy");
    let mut acknowledged = acknowledge(1);
    acknowledged["policy"] = json!(1);
    let mut old = set_policy(1, json!(1));
    old["version"] = json!(PROTOCOL_VERSION - 1);
    for request in [
        set_policy(1, json!(2)),
        set_policy(1, json!(-1)),
        set_policy(1, json!("1")),
        set_policy(1, json!(1.0)),
        set_policy(1, json!(null)),
        unknown,
        missing,
        acknowledged,
        old,
    ] {
        assert!(
            exchange_as(&rig, &request, USER).await.is_empty(),
            "{request}"
        );
    }
    assert_eq!(*files.bytes.lock().unwrap(), prior);
    no_hardware(&rig);
    rig.stop().await;
}

/// Managed `IsExactFanIntentOwned`: this user's fan owner wrote exactly the
/// saved fan settings at the class, still owes their release, and a reading
/// confirmed them with no fan change since.
#[tokio::test]
async fn only_this_users_exact_written_manual_fans_are_owned() {
    let rig = fan_rig(0, 0);
    rig.get().await;
    let owns = |sid: &str, session: u32, power: i32| {
        let desired = saved_fan(&rig.storage, power).unwrap();
        rig.runtime
            .owns_exact_fans(sid, session, power, desired.as_ref())
    };
    assert!(!owns(SID, 7, 0), "nothing is owned before a fan write");

    let result = rig.request(fan_request(1, Some(1), 3700)).await;

    assert_eq!(result["result"]["status"], 1, "{result}");
    assert!(owns(SID, 7, 0));
    assert!(!owns("S-1-5-21-111-222-333-1002", 7, 0));
    assert!(!owns(SID, 8, 0));
    assert!(
        !owns(SID, 7, 2),
        "the write was made at the full-power class"
    );
    assert!(!rig.runtime.owns_exact_fans(SID, 7, 0, None));
    let mut other = saved_fan(&rig.storage, 0).unwrap().unwrap();
    other["fixedSpeedRpm"] = json!(4000);
    assert!(!rig.runtime.owns_exact_fans(SID, 7, 0, Some(&other)));
    // A fan change outside OpenBlade ends the ownership.
    rig.port.state.lock().unwrap().fans = [0; 2];
    observed(&rig, 1).await;
    assert!(!owns(SID, 7, 0));
    rig.stop().await;
}

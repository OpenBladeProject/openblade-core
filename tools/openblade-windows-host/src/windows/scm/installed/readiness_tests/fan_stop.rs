//! Installed `setFanStopMode`, `setFanStopSettings`, `resumeAutomaticFanStop`
//! and `setBatterySaverFanStop` through the real framed dispatcher, and the
//! `fanStopStatus` member the status then carries. The store is the in-memory
//! model; no hardware, pipe or operating-system supplier is reached, and every
//! case asserts that no mailbox command was written. Managed's replies and
//! statuses are replayed from `tests/data/fan-stop-vectors.json`.
use super::*;
use crate::service_status::fan_stop::{UNAVAILABLE, public_status};

const VECTORS: &str = include_str!("../../../../../tests/data/fan-stop-vectors.json");
const PROFILES: &str = "profiles.json";
const PREFERENCES: &str = "battery-preferences.json";
const RECORD: &str = "battery-configuration.commit.json.recovery-required";

fn id(n: u8) -> String {
    format!("aaaaaaaa-bbbb-cccc-dddd-{n:012}")
}

fn mode(n: u8, mode: Value) -> Value {
    json!({"operation": "setFanStopMode", "version": 90, "requestId": id(n), "mode": mode})
}

fn settings(n: u8, settings: Value) -> Value {
    json!({"operation": "setFanStopSettings", "version": 90, "requestId": id(n), "settings": settings})
}

fn resume(n: u8) -> Value {
    json!({"operation": "resumeAutomaticFanStop", "version": 90, "requestId": id(n)})
}

fn battery_saver(n: u8, enabled: Value) -> Value {
    json!({"operation": "setBatterySaverFanStop", "version": 90, "requestId": id(n), "enabled": enabled})
}

/// A vector row's request as the framed request the Tray sends.
fn request(n: u8, row: &Value) -> Value {
    if let Some(value) = row.get("mode") {
        mode(n, value.clone())
    } else if let Some(value) = row.get("settings") {
        settings(n, value.clone())
    } else if let Some(value) = row.get("batterySaver") {
        battery_saver(n, value.clone())
    } else {
        resume(n)
    }
}

/// `members` over managed's default settings, as the managed codec reads them.
fn settings_over_defaults(members: &Value) -> Value {
    let mut merged = public_status()["settings"].clone();
    for (name, value) in members.as_object().into_iter().flatten() {
        merged[name] = value.clone();
    }
    merged
}

/// The saved fixture aggregate with a row's saved settings and Battery
/// profile flag, as managed saves them before the row's requests.
fn files_with(saved: &Value) -> MemoryFiles {
    let files = MemoryFiles::default();
    files.profiles(1);
    let mut document = files.document(PREFERENCES);
    document["preferences"]["fanStopSettings"] =
        settings_over_defaults(saved.get("settings").unwrap_or(&json!({})));
    files.put(PREFERENCES, document);
    let mut document = files.document(PROFILES);
    document["profiles"]["battery"]["stopFansInBatterySaver"] = json!(saved["stopFans"] == true);
    files.put(PROFILES, document);
    files.commit_configuration();
    files
}

fn stored(files: &MemoryFiles) -> (Value, Value) {
    (
        files.document(PREFERENCES)["preferences"]["fanStopSettings"].clone(),
        files.document(PROFILES)["profiles"]["battery"]["stopFansInBatterySaver"].clone(),
    )
}

fn no_hardware(rig: &Rig) {
    let port = rig.port.state.lock().unwrap();
    assert_eq!(
        port.writes, 0,
        "a Fan Stop request writes no mailbox command"
    );
}

/// One framed exchange; empty when the Service closes without answering.
async fn exchange_as(rig: &Rig, request: &Value, caller: Caller) -> Vec<u8> {
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
    assert!(
        !matches!(served, Err(ExchangeError::Runtime(_))),
        "a client fault never fails the Service"
    );
    reply
}

async fn fan_stop(rig: &Rig) -> Value {
    rig.get().await["status"]["fanStopStatus"].clone()
}

/// `status` without the caller's saved settings and automation, which
/// native projects only once this user's request has captured the store.
fn unprojected(status: &Value) -> Value {
    let mut status = status.clone();
    let public = public_status();
    for member in ["settings", "automationState", "automationPauseReason"] {
        status[member] = public[member].clone();
    }
    status
}

#[tokio::test]
async fn managed_vectors_replay_through_the_dispatcher() {
    let vectors: Value = serde_json::from_str(VECTORS).unwrap();
    let mut replayed = 0;
    for row in vectors["sequences"].as_array().unwrap() {
        let name = row["name"].as_str().unwrap();
        let files = files_with(&row["saved"]);
        let rig = Rig::new(files.clone());
        if row["evidence"] == false {
            // Without matched host metadata there is no reviewed evidence.
            rig.metadata.store(3, Ordering::Release);
        }
        assert_eq!(fan_stop(&rig).await, unprojected(&row["initial"]), "{name}");
        if row["quarantined"] == true {
            // Managed appends a space to profiles.json before activation.
            // This Service opens the store at start and refuses to start
            // over a preserved aggregate, so the space lands after it.
            files
                .bytes
                .lock()
                .unwrap()
                .get_mut(PROFILES)
                .unwrap()
                .push(b' ');
        }
        let prior = files.bytes.lock().unwrap().clone();
        let requests = row["requests"].as_array().unwrap();
        let steps = row["steps"].as_array().unwrap();
        assert_eq!(requests.len(), steps.len(), "{name}");

        if row["sessionChanged"] == true {
            // Native admits a request from the control session only, so a
            // caller outside it is closed without a reply where managed's
            // `stillControlsSession` answers Blocked.
            let other = Caller {
                control_session: Some(9),
                ..USER
            };
            for (n, step) in requests.iter().enumerate() {
                let request = request(n as u8 + 1, step);
                assert!(
                    exchange_as(&rig, &request, other).await.is_empty(),
                    "{name}"
                );
            }
            assert_eq!(*files.bytes.lock().unwrap(), prior, "{name}");
            assert_eq!(row["stored"]["written"], false, "{name}");
            no_hardware(&rig);
            rig.stop().await;
            continue;
        }

        for (n, (step, expected)) in requests.iter().zip(steps).enumerate() {
            let response = rig.request(request(n as u8 + 1, step)).await;
            assert_eq!(response["result"], expected["result"], "{name} step {n}");
            assert_eq!(fan_stop(&rig).await, expected["fanStop"], "{name} step {n}");
        }

        let written = *files.bytes.lock().unwrap() != prior;
        assert_eq!(json!(written), row["stored"]["written"], "{name}");
        let (settings, stop_fans) = stored(&files);
        // A preserved aggregate is not read back here; managed's own bytes
        // stay as they are.
        if row["quarantined"] != true {
            assert_eq!(settings, row["stored"]["settings"], "{name}");
            assert_eq!(stop_fans, row["stored"]["stopFans"], "{name}");
        }
        no_hardware(&rig);
        replayed += 1;
        rig.stop().await;
    }
    assert_eq!(replayed, 19);
}

/// Saved settings and the Battery Saver flag survive a restart, and the
/// restarted Service projects them from its first request.
#[tokio::test]
async fn saved_settings_survive_a_restart() {
    let files = files_with(&json!({"stopFans": true}));
    let rig = Rig::new(files.clone());
    rig.get().await;
    let saved = json!({"endMode": 1, "automaticEnabled": true, "secondsBelowLimits": 60});
    assert_eq!(
        rig.request(settings(1, saved.clone())).await["result"]["status"],
        0
    );
    assert_eq!(
        rig.request(battery_saver(2, json!(false))).await["result"]["status"],
        0
    );
    let before = fan_stop(&rig).await;
    assert_eq!(before["automationState"], 1, "enabling arms it: {before}");
    rig.stop().await;

    let rig = Rig::restarted(files.clone());
    assert_eq!(
        stored(&files),
        (settings_over_defaults(&saved), json!(false))
    );
    let response = rig.request(settings(3, saved.clone())).await;
    assert_eq!(
        response["result"]["message"],
        "Fan Stop settings are already saved."
    );
    let after = fan_stop(&rig).await;
    assert_eq!(after["settings"], settings_over_defaults(&saved));
    // A new Service is managed's new process: its eligibility starts paused.
    assert_eq!(after["automationState"], 2, "{after}");
    assert_eq!(
        rig.request(battery_saver(4, json!(false))).await["result"]["status"],
        0
    );
    assert_eq!(
        stored(&files),
        (settings_over_defaults(&saved), json!(false))
    );
    no_hardware(&rig);
    rig.stop().await;
}

/// A refused store write fails the request, keeps the aggregate and the
/// published projection, and the Service still saves afterwards.
#[tokio::test]
async fn a_refused_store_write_fails_the_change_and_keeps_the_aggregate() {
    let files = files_with(&json!({"stopFans": true}));
    let rig = Rig::new(files.clone());
    rig.get().await;
    let prior = files.bytes.lock().unwrap().clone();
    files.fail.store(true, Ordering::Release);
    let restored = "The requested intent was rejected: The configuration aggregate could not be replaced. The saved configuration is unchanged.";
    for request in [
        settings(1, json!({"endMode": 1})),
        battery_saver(2, json!(false)),
    ] {
        let response = rig.request(request).await;
        assert_eq!(
            response["result"],
            json!({"status": 3, "message": restored}),
            "{response}"
        );
    }
    files.fail.store(false, Ordering::Release);
    assert_eq!(*files.bytes.lock().unwrap(), prior);
    assert_eq!(fan_stop(&rig).await["settings"]["endMode"], 0);
    assert_eq!(
        rig.request(settings(3, json!({"endMode": 1}))).await["result"]["status"],
        0
    );
    assert_eq!(stored(&files).0["endMode"], 1);
    no_hardware(&rig);
    rig.stop().await;
}

/// A transaction record another writer left closes the store to both
/// stored requests; Stop and Resume still answer managed's refusals.
#[tokio::test]
async fn a_recovery_record_blocks_the_stored_requests_untouched() {
    let files = files_with(&json!({}));
    let rig = Rig::new(files.clone());
    rig.get().await;
    files
        .bytes
        .lock()
        .unwrap()
        .insert(RECORD.into(), b"{}".to_vec());
    let prior = files.bytes.lock().unwrap().clone();
    for (request, message) in [
        (
            settings(1, json!({"endMode": 1})),
            "Fan Stop settings require the active Windows session and an available configuration.",
        ),
        (
            battery_saver(2, json!(false)),
            "OpenBlade preserved an unsupported or invalid configuration. Thermal settings are read-only until the service restarts with a valid configuration.",
        ),
        (mode(3, json!(1)), UNAVAILABLE),
        (
            resume(4),
            "Automatic Stop cannot resume in the current session or recovery state.",
        ),
    ] {
        let response = rig.request(request).await;
        assert_eq!(response["result"]["status"], 2, "{response}");
        assert_eq!(response["result"]["message"], message, "{response}");
    }
    assert_eq!(*files.bytes.lock().unwrap(), prior);
    no_hardware(&rig);
    rig.stop().await;
}

/// Frames managed's codec refuses are closed without a change.
#[tokio::test]
async fn a_frame_managed_refuses_is_closed_without_a_change() {
    let files = files_with(&json!({}));
    let rig = Rig::new(files.clone());
    rig.get().await;
    let prior = files.bytes.lock().unwrap().clone();
    for request in [
        mode(1, json!(2)),
        mode(1, json!(-1)),
        mode(1, json!("stop")),
        settings(1, json!({"endMode": 2})),
        settings(1, json!({"secondsAboveLimit": 0})),
        settings(1, json!({"unknown": 0})),
        settings(1, json!(null)),
        settings(1, json!([])),
        battery_saver(1, json!(1)),
        battery_saver(1, json!(null)),
        json!({"operation": "resumeAutomaticFanStop", "version": 90, "requestId": id(1), "extra": 0}),
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

/// Another user's view publishes the unprojected status.
#[tokio::test]
async fn another_users_view_has_no_saved_settings() {
    let files = files_with(&json!({}));
    let rig = Rig::new(files.clone());
    rig.get().await;
    assert_eq!(
        rig.request(settings(1, json!({"endMode": 1, "automaticEnabled": true})))
            .await["result"]["status"],
        0
    );
    let own = fan_stop(&rig).await;
    assert_eq!(own["settings"]["endMode"], 1);
    let other = rig
        .request_as(
            json!({"operation": "getStatus", "version": 90, "requestId": id(2)}),
            USER,
            "S-1-5-21-1-2-3-1002",
        )
        .await;
    assert_eq!(other["status"]["fanStopStatus"], unprojected(&own));
    no_hardware(&rig);
    rig.stop().await;
}

/// SYSTEM reaches the requests and is answered with the managed session
/// refusals, as managed answers a caller that does not hold the session;
/// nothing is stored and automatic Stop is not paused for it.
#[tokio::test]
async fn a_caller_that_does_not_hold_the_session_is_refused_untouched() {
    let files = files_with(&json!({"settings": {"automaticEnabled": true}, "stopFans": true}));
    let rig = Rig::new(files.clone());
    rig.get().await;
    let prior = files.bytes.lock().unwrap().clone();
    let system = Caller {
        logon: Logon::System,
        ..USER
    };
    for (request, message) in [
        (
            mode(1, json!(0)),
            "The active Windows session changed before fan-stop dispatch.",
        ),
        (
            settings(2, json!({"endMode": 1})),
            "Fan Stop settings require the active Windows session and an available configuration.",
        ),
        (
            battery_saver(3, json!(false)),
            "The active Windows session changed before the fan preference could be saved.",
        ),
        (
            battery_saver(4, json!(true)),
            "The active Windows session changed before the fan preference could be saved.",
        ),
    ] {
        let reply = exchange_as(&rig, &request, system).await;
        assert!(reply.len() > 4, "SYSTEM is answered");
        let reply: Value = serde_json::from_slice(&reply[4..]).unwrap();
        assert_eq!(
            reply["result"],
            json!({"status": 2, "message": message}),
            "{request}"
        );
    }
    assert_eq!(*files.bytes.lock().unwrap(), prior);
    // The owner's first request then shows the activation pause, not the
    // Automatic pause SYSTEM would have taken.
    assert_eq!(
        rig.request(settings(5, json!({"automaticEnabled": true})))
            .await["result"]["message"],
        "Fan Stop settings are already saved."
    );
    let status = fan_stop(&rig).await;
    assert_eq!(
        (&status["automationState"], &status["automationPauseReason"]),
        (&json!(2), &json!(3)),
        "{status}"
    );
    no_hardware(&rig);
    rig.stop().await;
}

use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};

use serde_json::json;
use tokio::io::DuplexStream;
use tokio::task::JoinHandle;

use super::*;

const REPLAY: &str =
    include_str!("../../../../tests/OpenBlade.Contracts.Tests/WindowsReplay/v1/service.json");
const RUNTIME: &str =
    include_str!("../../../../tests/OpenBlade.Service.Tests/WindowsReplay/v1/service-runtime.json");
const LEGACY_REQUEST: &str =
    include_str!("../../../../rust/crates/openblade-status-cli/fixtures/request.json");
const ID: &str = "11111111-2222-4333-8444-555555555555";

fn connect(
    status: Arc<Status>,
    slots: Arc<Semaphore>,
    session: Session,
    buffer: usize,
) -> (DuplexStream, JoinHandle<io::Result<()>>) {
    let (client, server) = tokio::io::duplex(buffer);
    let task = tokio::spawn(async move { serve(server, |_| Ok(session), &status, &slots).await });
    (client, task)
}

async fn send(client: &mut DuplexStream, payload: &[u8]) {
    client.write_i32_le(payload.len() as i32).await.unwrap();
    client.write_all(payload).await.unwrap();
}

async fn receive(client: &mut DuplexStream, discriminator: &str) -> Value {
    let payload = timeout(FRAME_TIMEOUT, read_payload(client))
        .await
        .unwrap()
        .unwrap();
    // System.Text.Json expects polymorphic metadata before ordinary members.
    let prefix = format!("{{\"frame\":\"{discriminator}\",");
    assert!(payload.starts_with(prefix.as_bytes()));
    serde_json::from_slice(&payload).unwrap()
}

#[tokio::test]
async fn replays_managed_v79_reads_and_the_full_status_event() {
    let runtime: Value = serde_json::from_str(RUNTIME).unwrap();
    let fixture: Value = serde_json::from_str(CURRENT_FIXTURE).unwrap();
    let status = Arc::new(Status::fixture().unwrap());
    let slots = Arc::new(Semaphore::new(1));

    for entry in runtime["dispatch"]
        .as_array()
        .unwrap()
        .iter()
        .filter(|entry| {
            matches!(
                entry["input"]["operation"].as_str(),
                Some("getStatus" | "subscribeStatus")
            )
        })
    {
        let session = if entry["session"] == "active" {
            Session::Active
        } else {
            Session::Background
        };
        let (mut client, server) = connect(status.clone(), slots.clone(), session, 128);
        send(&mut client, &serde_json::to_vec(&entry["input"]).unwrap()).await;
        let initial = receive(&mut client, "response").await;

        let subscribe = entry["input"]["operation"] == "subscribeStatus";
        let mut expected = fixture["status"].clone();
        if matches!(session, Session::Background) {
            expected["controlAccess"] = entry["expected"]["controlAccess"].clone();
            expected["message"] =
                "Another Windows session currently controls OpenBlade hardware.".into();
        }
        if subscribe {
            let event = receive(&mut client, "statusEvent").await;
            assert_eq!(event, fixture);
            expected["revision"] = (event["status"]["revision"].as_i64().unwrap() - 1).into();
            assert_eq!(slots.available_permits(), 0);
        }
        assert_eq!(
            initial,
            json!({
                "frame": "response",
                "version": 79,
                "requestId": ID,
                "success": true,
                "error": null,
                "status": expected,
                "result": null,
                "archive": null,
                "errorCode": null
            })
        );

        if subscribe {
            drop(client);
        } else {
            assert_eq!(client.read(&mut [0]).await.unwrap(), 0);
        }
        server.await.unwrap().unwrap();
        assert_eq!(slots.available_permits(), 1);
    }
}

#[tokio::test]
async fn performance_mutation_requires_explicit_v79_mode_active_session_and_strict_fields() {
    let (_advance, input) = tokio::sync::mpsc::channel(1);
    let (performance, _drive) = Status::fixture().unwrap().with_performance(input).unwrap();
    let performance = Arc::new(performance);
    let passive = Arc::new(Status::fixture().unwrap());
    let slots = Arc::new(Semaphore::new(1));

    for (status, version, session) in [
        (passive, 79, Session::Active),
        (performance.clone(), 51, Session::Active),
        (performance.clone(), 79, Session::Background),
    ] {
        let (mut client, server) = connect(status, slots.clone(), session, 256);
        let request = json!({
            "operation": "setPerformanceOverride", "version": version, "requestId": ID,
            "value": {"value": 2, "lifetime": 1}
        });
        send(&mut client, &serde_json::to_vec(&request).unwrap()).await;
        assert_eq!(receive(&mut client, "response").await["success"], false);
        server.await.unwrap().unwrap();
    }

    for fields in ["", r#","value":{"value":2,"lifetime":0,"lifetime":1}"#] {
        let (mut client, server) =
            connect(performance.clone(), slots.clone(), Session::Active, 256);
        let request = format!(
            r#"{{"operation":"setPerformanceOverride","version":79,"requestId":"{ID}"{fields}}}"#
        );
        send(&mut client, request.as_bytes()).await;
        assert_eq!(
            server.await.unwrap().unwrap_err().kind(),
            ErrorKind::InvalidData
        );
        assert_eq!(client.read(&mut [0]).await.unwrap(), 0);
    }

    let replay = performance.replay.as_ref().unwrap();
    assert_eq!(replay.borrow()["confirmedState"]["performance"], 1);
    assert_eq!(replay.borrow()["performanceOverride"], Value::Null);

    let (mut client, server) = connect(performance.clone(), slots, Session::Active, 256);
    let request = json!({
        "operation": "setPerformanceOverride", "version": 79, "requestId": ID,
        "value": {"value": 2, "lifetime": 1}
    });
    send(&mut client, &serde_json::to_vec(&request).unwrap()).await;
    assert_eq!(
        receive(&mut client, "response").await["result"]["status"],
        1
    );
    server.await.unwrap().unwrap();
    assert_eq!(
        replay.borrow()["performanceOverride"],
        json!({
            "value": 2, "lifetime": 1, "customPerformance": null, "gpuClockTuning": null,
        })
    );
}

#[tokio::test]
async fn replay_publication_during_initial_write_remains_pending() {
    let (advance, input) = tokio::sync::mpsc::channel(1);
    let (status, drive) = Status::fixture().unwrap().with_replay(input);
    let mut published = status.replay.as_ref().unwrap().clone();
    let drive = tokio::spawn(drive);
    let slots = Arc::new(Semaphore::new(1));
    let (mut client, server) = connect(Arc::new(status), slots.clone(), Session::Active, 64);
    let request = json!({"operation":"subscribeStatus","version":79,"requestId":ID});
    send(&mut client, &serde_json::to_vec(&request).unwrap()).await;

    // Reading the prefix proves the server captured its initial revision.
    // The body is larger than the duplex buffer and cannot finish yet.
    let length = timeout(FRAME_TIMEOUT, client.read_i32_le())
        .await
        .unwrap()
        .unwrap();
    assert!(length > 64);
    advance.send(()).await.unwrap();
    timeout(FRAME_TIMEOUT, published.changed())
        .await
        .unwrap()
        .unwrap();
    let mut body = vec![0; length as usize];
    timeout(FRAME_TIMEOUT, client.read_exact(&mut body))
        .await
        .unwrap()
        .unwrap();
    let initial: Value = serde_json::from_slice(&body).unwrap();
    assert_eq!(initial["status"]["revision"], 1);
    let event = receive(&mut client, "statusEvent").await;
    assert_eq!(event["status"]["revision"], 2);
    assert_eq!(event["status"]["functionKeyPrimary"], Value::Null);

    drop(client);
    server.await.unwrap().unwrap();
    assert_eq!(slots.available_permits(), 1);
    drop(advance);
    drive.await.unwrap().unwrap();
}

#[tokio::test]
async fn replays_v51_request_and_emits_only_the_cli_whitelist() {
    let status = Arc::new(Status::fixture().unwrap());
    let (mut client, server) = connect(status, Arc::new(Semaphore::new(0)), Session::Active, 128);
    send(&mut client, LEGACY_REQUEST.trim_end().as_bytes()).await;
    let response = receive(&mut client, "response").await;
    let mut expected: Value = serde_json::from_str(LEGACY_FIXTURE).unwrap();
    let fields = expected["status"].as_object_mut().unwrap();
    for key in ["message", "profiles", "unknownExtension"] {
        fields.remove(key).unwrap();
    }
    assert_eq!(response, expected);
    assert_eq!(client.read(&mut [0]).await.unwrap(), 0);
    server.await.unwrap().unwrap();
}

#[tokio::test]
async fn rejects_malformed_envelopes_and_denies_every_other_operation() {
    let replay: Value = serde_json::from_str(REPLAY).unwrap();
    let status = Arc::new(Status::fixture().unwrap());
    let slots = Arc::new(Semaphore::new(0));
    for entry in replay["requests"]
        .as_array()
        .unwrap()
        .iter()
        .filter(|entry| {
            matches!(
                entry["id"].as_str(),
                Some(
                    "unknown-outer-field"
                        | "incompatible-version"
                        | "empty-id"
                        | "duplicate-version"
                        | "oversize-prefix-only"
                        | "negative-prefix-only"
                        | "short-body"
                )
            )
        })
    {
        let (mut client, server) = connect(status.clone(), slots.clone(), Session::Active, 1024);
        if let Some(length) = entry["declaredLength"].as_i64() {
            client.write_i32_le(length as i32).await.unwrap();
            client.shutdown().await.unwrap();
        } else if let Some(payload) = entry["json"].as_str() {
            send(&mut client, payload.as_bytes()).await;
        } else {
            send(&mut client, &serde_json::to_vec(&entry["input"]).unwrap()).await;
        }
        assert_eq!(client.read(&mut [0]).await.unwrap(), 0, "{}", entry["id"]);
        let error = server.await.unwrap().unwrap_err();
        let expected_kind = if entry["failure"] == "endOfStream" {
            ErrorKind::UnexpectedEof
        } else {
            ErrorKind::InvalidData
        };
        assert_eq!(error.kind(), expected_kind, "{}", entry["id"]);
    }

    let families = replay["operationFamilies"].as_object().unwrap();
    for version in [51, 79] {
        for operation in families
            .values()
            .flat_map(|items| items.as_array().unwrap())
        {
            if operation == "getStatus" || (version == 79 && operation == "subscribeStatus") {
                continue;
            }
            let (mut client, server) = connect(status.clone(), slots.clone(), Session::Active, 128);
            let request = json!({"operation": operation, "version": version, "requestId": ID});
            send(&mut client, &serde_json::to_vec(&request).unwrap()).await;
            let rejection = receive(&mut client, "response").await;
            assert_eq!(rejection["success"], false, "{operation}");
            assert_eq!(rejection["version"], version);
            assert_eq!(rejection["requestId"], ID);
            assert!(rejection["status"].is_null());
            assert!(rejection["errorCode"].is_null());
            server.await.unwrap().unwrap();
        }
    }
}

#[tokio::test]
async fn authorizes_complete_payload_before_json_and_never_replies_to_denied_peer() {
    let status = Status::fixture().unwrap();
    let slots = Semaphore::new(1);
    let called = AtomicBool::new(false);
    let (mut client, server) = tokio::io::duplex(64);
    let (result, ()) = tokio::join!(
        serve(
            server,
            |_| {
                called.store(true, Ordering::Relaxed);
                Err(io::Error::from(ErrorKind::PermissionDenied))
            },
            &status,
            &slots
        ),
        async {
            client.write_i32_le(4).await.unwrap();
            client.write_all(b"?").await.unwrap();
            tokio::task::yield_now().await;
            assert!(!called.load(Ordering::Relaxed));
            client.write_all(b"???").await.unwrap();
            assert_eq!(client.read(&mut [0]).await.unwrap(), 0);
        }
    );
    assert!(called.load(Ordering::Relaxed));
    assert_eq!(result.unwrap_err().kind(), ErrorKind::PermissionDenied);
    assert_eq!(slots.available_permits(), 1);
}

#[tokio::test]
async fn preserves_one_shot_reads_at_capacity_and_releases_cancelled_subscriptions() {
    let status = Arc::new(Status::fixture().unwrap());
    let slots = Arc::new(Semaphore::new(1));
    let subscribe = br#"{"operation":"subscribeStatus","version":79,"requestId":"11111111-2222-4333-8444-555555555555"}"#;
    let (mut first, first_task) = connect(status.clone(), slots.clone(), Session::Background, 128);
    send(&mut first, subscribe).await;
    let initial = receive(&mut first, "response").await;
    let event = receive(&mut first, "statusEvent").await;
    assert_eq!(initial["status"]["controlAccess"], 1);
    assert_eq!(event["status"]["controlAccess"], 1);

    let (mut second, second_task) = connect(status.clone(), slots.clone(), Session::Active, 128);
    send(&mut second, subscribe).await;
    let rejected = receive(&mut second, "response").await;
    assert_eq!(rejected["success"], false);
    assert_eq!(rejected["requestId"], ID);
    assert_eq!(rejected["errorCode"], 0);
    assert!(rejected["status"].is_null());
    second_task.await.unwrap().unwrap();

    let (mut read, read_task) = connect(status.clone(), slots.clone(), Session::Background, 128);
    send(&mut read, LEGACY_REQUEST.trim_end().as_bytes()).await;
    assert_eq!(
        receive(&mut read, "response").await["status"]["controlAccess"],
        1
    );
    read_task.await.unwrap().unwrap();

    first_task.abort();
    assert!(first_task.await.unwrap_err().is_cancelled());
    assert_eq!(first.read(&mut [0]).await.unwrap(), 0);
    assert_eq!(slots.available_permits(), 1);

    let (mut reconnected, reconnected_task) = connect(status, slots.clone(), Session::Active, 128);
    send(&mut reconnected, subscribe).await;
    assert_eq!(receive(&mut reconnected, "response").await["success"], true);
    receive(&mut reconnected, "statusEvent").await;
    reconnected.write_all(b"x").await.unwrap();
    reconnected_task.await.unwrap().unwrap();
    assert_eq!(slots.available_permits(), 1);
}

#[tokio::test]
async fn bounds_partial_reads_and_stalled_subscription_writes() {
    let status = Arc::new(Status::fixture().unwrap());
    let slots = Arc::new(Semaphore::new(1));
    let (mut idle, idle_task) = connect(status.clone(), slots.clone(), Session::Active, 128);
    idle.write_i32_le(8).await.unwrap();
    idle.write_all(b"x").await.unwrap();
    let (mut stalled, stalled_task) = connect(status, slots.clone(), Session::Active, 128);
    let subscribe = br#"{"operation":"subscribeStatus","version":79,"requestId":"11111111-2222-4333-8444-555555555555"}"#;
    send(&mut stalled, subscribe).await;

    assert_eq!(
        idle_task.await.unwrap().unwrap_err().kind(),
        ErrorKind::TimedOut
    );
    assert_eq!(
        stalled_task.await.unwrap().unwrap_err().kind(),
        ErrorKind::TimedOut
    );
    assert_eq!(slots.available_permits(), 1);
}

use super::*;
use serde_json::{Value, json};

const ID: &str = "11111111-2222-4333-8444-555555555555";
const REPLAY: &str =
    include_str!("../../../../tests/OpenBlade.Session.Tests/WindowsReplay/v1/session.json");

#[test]
fn accepts_the_managed_gpu_vectors_and_rejects_bad_envelopes() {
    let replay: Value = serde_json::from_str(REPLAY).unwrap();
    let family = &replay["families"]["gpu"];
    assert_eq!(family["version"], VERSION);
    assert_eq!(family["maxMessageBytes"], MAX_FRAME_BYTES);
    for vector in family["requests"].as_array().unwrap() {
        if let Some(input) = vector.get("input") {
            let parsed = Request::parse(&serde_json::to_vec(input).unwrap());
            assert_eq!(parsed.is_err(), vector.get("failure").is_some(), "{vector}");
        }
    }
    for invalid in [
        format!(r#"{{"operation":"query","version":2,"version":2,"requestId":"{ID}"}}"#),
        format!(r#"{{"operation":"query","operation":"close","version":2,"requestId":"{ID}"}}"#),
        r#"{"operation":"query","version":2,"requestId":"00000000-0000-0000-0000-000000000000"}"#
            .into(),
        format!(r#"{{"operation":"future","version":2,"requestId":"{ID}"}}"#),
    ] {
        assert!(Request::parse(invalid.as_bytes()).is_err(), "{invalid}");
    }
}

#[test]
fn rejects_malformed_close_before_dispatch() {
    let valid =
        json!({"processId":41, "startTimeUtcTicks":504911232000000041_i64, "name":"active-game"});
    let bad = |force, targets| {
        let body = json!({"operation":"close", "version":2, "requestId":ID, "force":force, "targets":targets});
        assert!(
            Request::parse(&serde_json::to_vec(&body).unwrap()).is_err(),
            "{body}"
        );
    };
    bad(false, json!([]));
    bad(
        true,
        json!([valid, {"processId":42, "startTimeUtcTicks":1, "name":"other"}]),
    );
    bad(false, json!([valid, valid]));
    bad(
        false,
        json!([{"processId":0, "startTimeUtcTicks":1, "name":"app"}]),
    );
    bad(
        false,
        json!([{"processId":41, "startTimeUtcTicks":0, "name":"app"}]),
    );
    bad(
        false,
        json!([{"processId":41, "startTimeUtcTicks":1, "name":" bad"}]),
    );
    bad(
        false,
        json!([{"processId":41, "startTimeUtcTicks":1, "name":"😀".repeat(131)}]),
    );
    let oversized: Vec<_> = (1..=129)
        .map(|pid| json!({"processId":pid, "startTimeUtcTicks":1, "name":"app"}))
        .collect();
    bad(false, json!(oversized));
}

#[test]
fn wire_projection_preserves_inventory_eligibility_without_granting_actions() {
    let inventory = scripted::sample(Scenario::Available, &AtomicBool::new(false)).unwrap();
    assert_eq!(inventory.apps.len(), 3);
    assert!(inventory.apps[0].can_close());
    assert!(inventory.apps[1].can_close());
    assert!(!inventory.apps[2].can_close());
    assert_eq!(inventory.omissions.len(), 2);

    let frame = Response::inventory(ID, inventory).unwrap().frame().unwrap();
    let response: Value = serde_json::from_slice(&frame[4..]).unwrap();
    assert_eq!(response["requestId"], ID);
    assert_eq!(response["status"], SUCCEEDED);
    assert_eq!(response["apps"][0]["processId"], 41);
    assert_eq!(
        response["apps"][0]["startTimeUtcTicks"],
        504911232000000041_i64
    );
    assert_eq!(response["apps"][1]["utilizationPercent"], 0.0);
    assert_eq!(response["apps"][0]["blockReason"], NO_ACTIONS);
    assert_eq!(
        response["apps"][2]["blockReason"],
        "The process belongs to another Windows session."
    );
    assert!(
        response["apps"]
            .as_array()
            .unwrap()
            .iter()
            .all(|row| row["canClose"] == false)
    );
    assert_eq!(response["results"], json!([]));
}

#[test]
fn large_inventory_keeps_its_ordered_prefix_inside_the_frame_budget() {
    let inventory = scripted::sample(Scenario::Large, &AtomicBool::new(false)).unwrap();
    assert_eq!(inventory.apps.len(), MAXIMUM_APPS);
    assert!(inventory.truncated);
    let frame = Response::inventory(ID, inventory).unwrap().frame().unwrap();
    let len = i32::from_le_bytes(frame[..4].try_into().unwrap());
    assert_eq!(len as usize, frame.len() - 4);
    assert!(len <= MAX_FRAME_BYTES);
    let response: Value = serde_json::from_slice(&frame[4..]).unwrap();
    assert_eq!(response["status"], PARTIAL);
    assert_eq!(response["error"], TRUNCATED);
    let apps = response["apps"].as_array().unwrap();
    assert!(!apps.is_empty());
    assert!(apps.len() < MAXIMUM_APPS);
    for (index, app) in apps.iter().enumerate() {
        assert_eq!(app["processId"], index + 1);
    }
}

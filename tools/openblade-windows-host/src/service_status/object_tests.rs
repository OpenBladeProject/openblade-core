//! Service v80 frames are read only from JSON objects, like the managed
//! IpcMessageCodec. Every row of the shared fixture is also run through the
//! managed codec (IpcMessageCodecTests.ObjectFrames.cs).
use super::*;
use openblade_windows_host::json_object;

const FIXTURE: &str = include_str!("../../tests/fixtures/service-object-frames.json");
const GET_STATUS: &[u8] = include_bytes!("../../tests/fixtures/get-status-v80.frames");
const OBSERVE_STATE: &[u8] = include_bytes!("../../tests/fixtures/observe-state-v80.frames");
const SUBSCRIBE_STATUS: &[u8] = include_bytes!("../../tests/fixtures/subscribe-status-v80.frames");

fn rows(group: &str) -> Vec<Value> {
    let fixture: Value = serde_json::from_str(FIXTURE).unwrap();
    let rows = match group {
        "requests" => &fixture["requests"],
        _ => &fixture["serverFrames"]["rows"],
    };
    rows.as_array().unwrap().clone()
}

fn text<'a>(row: &'a Value, member: &str) -> &'a str {
    row[member].as_str().unwrap()
}

fn payloads(mut frames: &[u8]) -> Vec<&[u8]> {
    let mut payloads = Vec::new();
    while !frames.is_empty() {
        let length = i32::from_le_bytes(frames[..4].try_into().unwrap()) as usize;
        payloads.push(&frames[4..4 + length]);
        frames = &frames[4 + length..];
    }
    payloads
}

/// Valid rows are exact managed-written bytes (the managed test re-encodes
/// them). The object-only parser yields the same request as plain serde.
#[test]
fn managed_request_objects_decode_exactly_as_before() {
    let valid: Vec<_> = rows("requests")
        .into_iter()
        .filter(|row| row["valid"] == true)
        .collect();
    assert_eq!(valid.len(), 9);
    for row in valid {
        let frame = text(&row, "frame").as_bytes();
        let object: StatusRequest = json_object::from_slice(frame).unwrap();
        let plain: StatusRequest = serde_json::from_slice(frame).unwrap();
        assert_eq!(
            serde_json::to_vec(&object).unwrap(),
            serde_json::to_vec(&plain).unwrap(),
            "{}",
            row["case"]
        );
    }
}

#[test]
fn request_arrays_are_refused_by_the_parser_at_every_position() {
    let refused: Vec<_> = rows("requests")
        .into_iter()
        .filter(|row| row["valid"] == false && row["nativeRefusal"].is_null())
        .collect();
    assert_eq!(refused.len(), 13);
    for row in refused {
        let frame = text(&row, "frame").as_bytes();
        assert!(
            json_object::from_slice::<StatusRequest>(frame).is_err(),
            "{}",
            row["case"]
        );
    }
}

/// The installed Service decoder: arrays fail at the parser, with its
/// unchanged fixed message, before any composition or semantic check.
#[cfg(feature = "installed-service-candidate")]
#[test]
fn service_decoder_refuses_request_arrays_like_managed() {
    const PARSE: &str = "invalid Service status request";
    for row in rows("requests") {
        let case = &row["case"];
        let result = StatusRequest::decode(text(&row, "frame").as_bytes());
        if row["valid"] == true {
            if let Err(error) = &result {
                assert_ne!(error.to_string(), PARSE, "{case}");
            }
            // Battery policy selects every mutation family in these rows.
            if cfg!(feature = "service-battery-policy-v1") {
                result.unwrap_or_else(|error| panic!("{case}: {error}"));
            }
        } else {
            let error = result
                .err()
                .unwrap_or_else(|| panic!("{case} was accepted"));
            if row["nativeRefusal"].is_null() {
                assert_eq!(error.to_string(), PARSE, "{case}");
            }
        }
    }
}

/// The managed-written frame for a row's base, after its positional edit.
fn server_frame(row: &Value) -> (StatusRequest, Vec<u8>) {
    let (frames, index) = match text(row, "base") {
        "get-status" => (GET_STATUS, 1),
        "observe-state" => (OBSERVE_STATE, 1),
        "status-event" => (SUBSCRIBE_STATUS, 2),
        base => panic!("unknown base {base}"),
    };
    let payloads = payloads(frames);
    let request = serde_json::from_slice(payloads[0]).unwrap();
    let Some(members) = row["positional"].as_array() else {
        return (request, payloads[index].to_vec());
    };
    let mut frame: Value = serde_json::from_slice(payloads[index]).unwrap();
    let mut target = &mut frame;
    for key in row["path"].as_array().unwrap() {
        target = target.get_mut(key.as_str().unwrap()).unwrap();
    }
    let array = members
        .iter()
        .map(|name| target.get(name.as_str().unwrap()).unwrap().clone())
        .collect();
    *target = Value::Array(array);
    (request, serde_json::to_vec(&frame).unwrap())
}

#[test]
fn status_response_arrays_are_refused_at_every_object_position() {
    let mut checked = 0;
    for row in rows("serverFrames") {
        if text(&row, "base") == "status-event" {
            continue;
        }
        let case = &row["case"];
        let (request, frame) = server_frame(&row);
        let result = StatusResponse::decode(&frame, &request, None);
        if row["valid"] == true {
            let snapshot = result.unwrap_or_else(|error| panic!("{case}: {error}"));
            let plain: StatusResponse = serde_json::from_slice(&frame).unwrap();
            assert_eq!(
                serde_json::to_value(snapshot).unwrap(),
                serde_json::to_value(plain.status.unwrap()).unwrap(),
                "{case}"
            );
            // The positional edits re-encode; key order alone is not a refusal.
            let reordered = serde_json::to_vec(&serde_json::from_slice::<Value>(&frame).unwrap());
            StatusResponse::decode(&reordered.unwrap(), &request, None).unwrap();
        } else {
            let error = result
                .err()
                .unwrap_or_else(|| panic!("{case} was accepted"));
            assert!(
                [
                    "invalid Service status response",
                    "invalid ObserveState confirmation members"
                ]
                .contains(&error.to_string().as_str()),
                "{case}: {error}"
            );
        }
        checked += 1;
    }
    assert_eq!(checked, 11);
}

#[cfg(feature = "native-agent-entry-compile-check")]
#[tokio::test]
async fn status_event_arrays_are_refused_at_every_object_position() {
    let mut checked = 0;
    for row in rows("serverFrames") {
        if text(&row, "base") != "status-event" {
            continue;
        }
        let case = &row["case"];
        let (_, payload) = server_frame(&row);
        let framed = [&(payload.len() as i32).to_le_bytes()[..], &payload].concat();
        let result = read_status_event(&mut framed.as_slice()).await;
        if row["valid"] == true {
            result.unwrap_or_else(|error| panic!("{case}: {error}"));
        } else {
            let error = result
                .err()
                .unwrap_or_else(|| panic!("{case} was accepted"));
            assert_eq!(error.to_string(), "invalid Service status event", "{case}");
        }
        checked += 1;
    }
    assert_eq!(checked, 4);
}

/// The ObserveState confirmation pass reads members that the snapshot keeps
/// as maps or opaque values; its own nested structs are object-only too, and
/// an absent member stays absent.
#[test]
fn observe_confirmation_members_are_object_only() {
    for accepted in [
        r#"{"status":{}}"#,
        r#"{"status":{"confirmedState":null,"fanStopStatus":null}}"#,
        r#"{"status":{"confirmedState":{"performance":1},"fanStopStatus":{"state":0}}}"#,
    ] {
        json_object::from_slice::<ObserveReplyMembers>(accepted.as_bytes())
            .unwrap_or_else(|error| panic!("{accepted}: {error}"));
    }
    for refused in [
        r#"[{}]"#,
        r#"{"status":[null,null,null,null]}"#,
        r#"{"status":{"confirmedState":[1]}}"#,
        r#"{"status":{"fanStopStatus":[0]}}"#,
    ] {
        assert!(
            json_object::from_slice::<ObserveReplyMembers>(refused.as_bytes()).is_err(),
            "{refused}"
        );
    }
}

//! Service v91 frames are read only from JSON objects, like the managed
//! IpcMessageCodec. Every row of the shared fixture is also run through the
//! managed codec (IpcMessageCodecTests.ObjectFrames.cs).
use super::*;
use openblade_windows_host::json_object;
use serde_json::json;

const FIXTURE: &str = include_str!("../../tests/fixtures/service-object-frames.json");
const GET_STATUS: &[u8] = include_bytes!("../../tests/fixtures/get-status-v91.frames");
const OBSERVE_STATE: &[u8] = include_bytes!("../../tests/fixtures/observe-state-v91.frames");
const SUBSCRIBE_STATUS: &[u8] = include_bytes!("../../tests/fixtures/subscribe-status-v91.frames");

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
    assert_eq!(valid.len(), 11);
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
    assert_eq!(refused.len(), 23);
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

/// The managed-written frame for a row's base, after its edit.
fn server_frame(row: &Value) -> (StatusRequest, Vec<u8>) {
    let (frames, index) = match text(row, "base") {
        "get-status" => (GET_STATUS, 1),
        "observe-state" => (OBSERVE_STATE, 1),
        "status-event" => (SUBSCRIBE_STATUS, 2),
        base => panic!("unknown base {base}"),
    };
    let payloads = payloads(frames);
    let request = serde_json::from_slice(payloads[0]).unwrap();
    if row["positional"].is_null() && row["value"].is_null() {
        return (request, payloads[index].to_vec());
    }
    let mut frame: Value = serde_json::from_slice(payloads[index]).unwrap();
    let mut target = &mut frame;
    for key in row["path"].as_array().unwrap() {
        // A value row may add a member; positional rows need an object there.
        target = &mut target[key.as_str().unwrap()];
    }
    // Spliced as text: a parsed Value would already read a private token.
    if let Some(text) = row["value"].as_str() {
        const MARK: &str = "\"object-frame-value\"";
        *target = Value::String(MARK.trim_matches('"').to_owned());
        let frame = serde_json::to_string(&frame).unwrap();
        assert_eq!(frame.matches(MARK).count(), 1);
        return (request, frame.replace(MARK, text).into_bytes());
    }
    let array = match &row["positional"] {
        Value::String(all) if all == "*" => target.as_object().unwrap().values().cloned().collect(),
        members => members
            .as_array()
            .unwrap()
            .iter()
            .map(|name| target.get(name.as_str().unwrap()).unwrap().clone())
            .collect(),
    };
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
            match row["nativeError"].as_str() {
                Some(expected) => assert_eq!(error.to_string(), expected, "{case}"),
                None => assert!(
                    [
                        "invalid Service status response",
                        "invalid ObserveState confirmation members"
                    ]
                    .contains(&error.to_string().as_str()),
                    "{case}: {error}"
                ),
            }
        }
        checked += 1;
    }
    assert_eq!(checked, 30);
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
            let snapshot = result.unwrap_or_else(|error| panic!("{case}: {error}"));
            let frame: Value = serde_json::from_slice(&payload).unwrap();
            let plain: Snapshot = serde_json::from_value(frame["status"].clone()).unwrap();
            assert_eq!(
                serde_json::to_value(snapshot).unwrap(),
                serde_json::to_value(plain).unwrap(),
                "{case}"
            );
        } else {
            let error = result
                .err()
                .unwrap_or_else(|| panic!("{case} was accepted"));
            let expected = row["nativeError"]
                .as_str()
                .unwrap_or("invalid Service status event");
            assert_eq!(error.to_string(), expected, "{case}");
        }
        checked += 1;
    }
    assert_eq!(checked, 26);
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

/// Managed requires `liveReadings` (a required ServiceStatusSnapshot
/// constructor parameter). The native reader has always read an absent member
/// as none, and still does.
#[test]
fn absent_live_readings_stay_absent() {
    let (request, frame) = server_frame(&rows("serverFrames")[0]);
    let mut frame: Value = serde_json::from_slice(&frame).unwrap();
    let status = frame["status"].as_object_mut().unwrap();
    assert!(status.remove("liveReadings").unwrap().is_object());
    let frame = serde_json::to_vec(&frame).unwrap();
    let snapshot = StatusResponse::decode(&frame, &request, None).unwrap();
    assert!(snapshot.live_readings.is_none());
}

/// Only a Service built with the read-only Driver-mode feature writes the
/// `keyboardDriverModeActive` member; every other Service status, the
/// metadata-only one included, omits it.
#[cfg(all(
    feature = "installed-service-candidate",
    not(feature = "service-keyboard-driver-mode-read-v1")
))]
#[test]
fn a_service_without_the_driver_mode_read_writes_no_driver_mode_member() {
    for access in [0, 1] {
        let status =
            Snapshot::observed_read_only(0, access, false, false, vec![], None, "Awaiting.".into());
        let status = serde_json::to_value(status).unwrap();
        assert!(status.get("keyboardDriverModeActive").is_none());
    }
}

/// A source the Service only retained confirms no display power context, as
/// the managed display coordinator treats it as ExternalUnknown.
#[cfg(openblade_display_write)]
#[test]
fn retained_power_source_is_unconfirmed_for_display_admission() {
    let (request, frame) = server_frame(&rows("serverFrames")[0]);
    let mut frame: Value = serde_json::from_slice(&frame).unwrap();
    frame["status"]["activePowerSource"] = json!(1);
    for (reason, expected) in [(None, 1), (Some(0), 1), (Some(2), 1), (Some(3), 3)] {
        let status = frame["status"].as_object_mut().unwrap();
        match reason {
            Some(reason) => status.insert("observationReason".into(), json!(reason)),
            None => status.remove("observationReason"),
        };
        let payload = serde_json::to_vec(&frame).unwrap();
        let snapshot = StatusResponse::decode(&payload, &request, None).unwrap();
        assert_eq!(snapshot.display_power_source(), expected, "{reason:?}");
    }
}

/// Managed `DisplayAutomationCoordinator.Observe` manual-external evidence:
/// an ExternalUnknown or retained source, external power, a verified device
/// and Active control, all from the same status.
#[cfg(openblade_display_write)]
#[test]
fn manual_external_power_needs_every_managed_condition() {
    let (request, frame) = server_frame(&rows("serverFrames")[0]);
    let base: Value = serde_json::from_slice(&frame).unwrap();
    let decode = |change: &dyn Fn(&mut serde_json::Map<String, Value>)| {
        let mut frame = base.clone();
        let status = frame["status"].as_object_mut().unwrap();
        status.insert("activePowerSource".into(), json!(3));
        status.insert("externalPowerPresent".into(), json!(true));
        status.insert("deviceVerified".into(), json!(true));
        status.insert("controlAccess".into(), json!(0));
        change(status);
        let payload = serde_json::to_vec(&frame).unwrap();
        StatusResponse::decode(&payload, &request, None)
            .unwrap()
            .display_manual_external_power()
    };
    assert!(decode(&|_| {}));
    assert!(decode(&|status| {
        status.insert("activePowerSource".into(), json!(1));
        status.insert("observationReason".into(), json!(3));
    }));
    for (name, member, value) in [
        ("confirmed source", "activePowerSource", json!(1)),
        ("no external power", "externalPowerPresent", json!(false)),
        ("unverified device", "deviceVerified", json!(false)),
        ("inactive session", "controlAccess", json!(1)),
    ] {
        assert!(
            !decode(&|status| {
                status.insert(member.into(), value.clone());
            }),
            "{name}"
        );
    }
    assert!(!decode(&|status| {
        status.remove("externalPowerPresent");
    }));
}

/// JSON pointers of every object in `value`, the root included.
fn object_pointers(value: &Value, pointer: String, out: &mut Vec<String>) {
    match value {
        Value::Object(members) => {
            for (name, member) in members {
                let name = name.replace('~', "~0").replace('/', "~1");
                object_pointers(member, format!("{pointer}/{name}"), out);
            }
            out.push(pointer);
        }
        Value::Array(items) => {
            for (index, item) in items.iter().enumerate() {
                object_pointers(item, format!("{pointer}/{index}"), out);
            }
        }
        _ => {}
    }
}

/// `frame` with each object position replaced by an array of its values, and
/// by a non-integer number, with the replaced pointer and the new value.
fn reshaped(frame: &[u8]) -> Vec<(String, Value, Vec<u8>)> {
    let frame: Value = serde_json::from_slice(frame).unwrap();
    let mut pointers = Vec::new();
    object_pointers(&frame, String::new(), &mut pointers);
    let number: Value = serde_json::from_str("1.5").unwrap();
    let mut mutants = Vec::new();
    for pointer in pointers {
        let members = frame.pointer(&pointer).unwrap().as_object().unwrap();
        let array = Value::Array(members.values().cloned().collect());
        for shape in [array, number.clone()] {
            let mut mutant = frame.clone();
            *mutant.pointer_mut(&pointer).unwrap() = shape.clone();
            let bytes = serde_json::to_vec(&mutant).unwrap();
            mutants.push((pointer.clone(), shape, bytes));
        }
    }
    mutants
}

/// An accepted mutant must hold the array or number it was given at that
/// position: no derived struct or map read it as an object.
fn assert_kept(decoded: &Value, pointer: &str, shape: &Value, case: &Value) {
    let kept = decoded.pointer(pointer);
    assert!(kept == Some(shape), "{case}: {pointer} became {kept:?}");
}

/// A structural guard over every object position of every valid fixture
/// frame, beyond the named rows: each is refused as an array or a number, or
/// kept as given in an opaque member.
#[test]
fn no_object_position_reads_an_array_or_a_number_as_an_object() {
    let (mut mutants, mut accepted) = (0, 0);
    for row in rows("requests") {
        if row["valid"] != true {
            continue;
        }
        for (pointer, shape, frame) in reshaped(text(&row, "frame").as_bytes()) {
            mutants += 1;
            if let Ok(request) = json_object::from_slice::<StatusRequest>(&frame) {
                accepted += 1;
                let decoded = serde_json::to_value(request).unwrap();
                assert_kept(&decoded, &pointer, &shape, &row["case"]);
            }
        }
    }
    for row in rows("serverFrames") {
        if row["valid"] != true || text(&row, "base") == "status-event" {
            continue;
        }
        let (request, frame) = server_frame(&row);
        for (pointer, shape, frame) in reshaped(&frame) {
            mutants += 1;
            if let Ok(snapshot) = StatusResponse::decode(&frame, &request, None) {
                accepted += 1;
                let pointer = pointer.strip_prefix("/status").unwrap();
                let decoded = serde_json::to_value(snapshot).unwrap();
                assert_kept(&decoded, pointer, &shape, &row["case"]);
            }
        }
    }
    // Opaque members (fan settings, unbound status fields) keep any shape.
    assert!(mutants > 100 && accepted > 0 && accepted < mutants);
}

#[cfg(feature = "native-agent-entry-compile-check")]
#[tokio::test]
async fn no_status_event_object_position_reads_an_array_or_a_number_as_an_object() {
    let row = rows("serverFrames")
        .into_iter()
        .find(|row| row["valid"] == true && text(row, "base") == "status-event")
        .unwrap();
    let (_, frame) = server_frame(&row);
    let (mut mutants, mut accepted) = (0, 0);
    for (pointer, shape, payload) in reshaped(&frame) {
        mutants += 1;
        let framed = [&(payload.len() as i32).to_le_bytes()[..], &payload].concat();
        if let Ok(snapshot) = read_status_event(&mut framed.as_slice()).await {
            accepted += 1;
            let pointer = pointer.strip_prefix("/status").unwrap();
            let decoded = serde_json::to_value(snapshot).unwrap();
            assert_kept(&decoded, pointer, &shape, &row["case"]);
        }
    }
    assert!(mutants > 100 && accepted > 0 && accepted < mutants);
}

/// serde_json reads an integer `-0` as a float, so typed status members
/// refuse it in a response and in an event. Managed reads it as 0 but never
/// writes it.
#[test]
fn integer_negative_zero_in_typed_response_members_is_refused() {
    for path in [
        json!(["version"]),
        json!(["status", "revision"]),
        json!(["status", "activePowerSource"]),
        json!(["status", "controlAccess"]),
    ] {
        let row = json!({"base": "get-status", "path": path, "value": "-0"});
        let (request, frame) = server_frame(&row);
        let error = StatusResponse::decode(&frame, &request, None)
            .err()
            .unwrap();
        assert_eq!(
            error.to_string(),
            "invalid Service status response",
            "{path}"
        );
    }
}

#[cfg(feature = "native-agent-entry-compile-check")]
#[tokio::test]
async fn integer_negative_zero_in_typed_event_members_is_refused() {
    for (path, message) in [
        (json!(["version"]), "Service status event version mismatch"),
        (
            json!(["status", "revision"]),
            "invalid Service status event",
        ),
        (
            json!(["status", "activePowerSource"]),
            "invalid Service status event",
        ),
        (
            json!(["status", "controlAccess"]),
            "invalid Service status event",
        ),
    ] {
        let row = json!({"base": "status-event", "path": path, "value": "-0"});
        let (_, payload) = server_frame(&row);
        let framed = [&(payload.len() as i32).to_le_bytes()[..], &payload].concat();
        let error = read_status_event(&mut framed.as_slice())
            .await
            .err()
            .unwrap();
        assert_eq!(error.to_string(), message, "{path}");
    }
}

/// A laptop control set in the opaque capabilities must be an array when
/// present: managed `IsWellFormed` refuses a null one (05ec5381), which the
/// fixture rows pin through both codecs, and reads an absent one as empty.
#[test]
fn laptop_control_sets_are_arrays_when_present() {
    let (request, frame) = server_frame(&rows("serverFrames")[0]);
    let base: Value = serde_json::from_slice(&frame).unwrap();
    let decode = |status: &Value| {
        StatusResponse::decode(&serde_json::to_vec(status).unwrap(), &request, None)
    };
    for set in ["oneShotLaptopControls", "hostManagedLaptopControls"] {
        let mut absent = base.clone();
        let capabilities = absent["status"]["capabilities"].as_object_mut().unwrap();
        assert!(capabilities.remove(set).is_some(), "{set}");
        decode(&absent).unwrap_or_else(|error| panic!("{set}: {error}"));
        for shape in [Value::Null, json!({}), json!(1), json!("")] {
            let mut edited = base.clone();
            edited["status"]["capabilities"][set] = shape.clone();
            let error = decode(&edited)
                .err()
                .unwrap_or_else(|| panic!("{set} as {shape} was accepted"));
            assert_eq!(error.to_string(), "invalid v91 status snapshot", "{set}");
        }
    }
}

//! Frames and dispatcher responses from the production managed Session GPU
//! codec and dispatcher (tests/oracles/gpu_apps `requests-emit` and
//! `dispatch-emit`). Native accepts exactly the managed-valid request rows,
//! and maps the same scan and close results to the same response.
use super::*;
use openblade_platform_windows::gpu_apps::{ActionResult as Outcome, BlockReason, GpuApp};
use serde_json::Value;

const REQUESTS: &str = include_str!("../../../tests/fixtures/gpu-requests/managed-requests.jsonl");
const DISPATCH: &str = include_str!("../../../tests/fixtures/gpu-requests/managed-dispatch.jsonl");

fn base64(text: &str) -> Vec<u8> {
    let value = |byte: u8| match byte {
        b'A'..=b'Z' => byte - b'A',
        b'a'..=b'z' => byte - b'a' + 26,
        b'0'..=b'9' => byte - b'0' + 52,
        b'+' => 62,
        b'/' => 63,
        _ => panic!("invalid base64"),
    };
    let bytes: Vec<u8> = text.bytes().filter(|byte| *byte != b'=').collect();
    let mut out = Vec::with_capacity(bytes.len() * 3 / 4);
    for chunk in bytes.chunks(4) {
        let mut acc = 0u32;
        for (index, byte) in chunk.iter().enumerate() {
            acc |= u32::from(value(*byte)) << (18 - 6 * index);
        }
        out.extend_from_slice(&acc.to_be_bytes()[1..chunk.len()]);
    }
    out
}

fn encode64(bytes: &[u8]) -> String {
    const TABLE: &[u8] = b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    let mut out = String::new();
    for chunk in bytes.chunks(3) {
        let mut acc = 0u32;
        for (index, byte) in chunk.iter().enumerate() {
            acc |= u32::from(*byte) << (16 - 8 * index);
        }
        for index in 0..4 {
            out.push(if index <= chunk.len() {
                TABLE[(acc >> (18 - 6 * index) & 63) as usize] as char
            } else {
                '='
            });
        }
    }
    out
}

fn body(frame: &[u8]) -> &[u8] {
    let (length, body) = frame.split_at(4);
    assert_eq!(
        u32::from_le_bytes(length.try_into().unwrap()) as usize,
        body.len(),
        "length prefix"
    );
    body
}

#[test]
fn managed_request_frames_parse_exactly_when_managed_accepts_them() {
    let mut checked = 0;
    for line in REQUESTS.lines().filter(|line| !line.is_empty()) {
        let row: Value = serde_json::from_str(line).unwrap();
        let case = row["case"].as_str().unwrap();
        let valid = row["valid"].as_bool().unwrap();
        let frame = base64(row["frame"].as_str().unwrap());
        let parsed = Request::parse(body(&frame));
        assert_eq!(parsed.is_ok(), valid, "{case}: {:?}", parsed.err());
        checked += 1;
    }
    assert_eq!(checked, 65, "every fixture row is checked");
}

/// Managed reads these (the oracle prints them as accepted observations) but
/// never writes them; native requires the written encoding.
#[test]
fn managed_lenient_encodings_are_refused_and_tolerated_ones_accepted() {
    let id = "9f876742-1234-4321-abcd-d3ea3e1cf102";
    let target = r#"{"processId":"41","startTimeUtcTicks":638900000000000000,"name":"Game"}"#;
    for lenient in [
        format!(r#"{{"operation":"query","version":2,"version":2,"requestId":"{id}"}}"#),
        format!(r#"{{"operation":"query","Version":2,"RequestId":"{id}"}}"#),
        format!(r#"{{"operation":"query","version":"2","requestId":"{id}"}}"#),
        format!(
            r#"{{"operation":"close","version":2,"requestId":"{id}","force":false,"targets":[{target}]}}"#
        ),
    ] {
        assert!(Request::parse(lenient.as_bytes()).is_err(), "{lenient}");
    }
    let upper = format!(
        r#"{{"operation":"query","version":2,"requestId":"{}"}}"#,
        id.to_uppercase()
    );
    assert_eq!(
        Request::parse(upper.as_bytes()).unwrap(),
        Request::Query {
            request_id: id.into()
        }
    );
    let spaced = format!(r#" {{ "operation" : "query" , "version" : 2 , "requestId" : "{id}" }} "#);
    assert!(Request::parse(spaced.as_bytes()).is_ok());
}

fn leak(text: &str) -> &'static str {
    Box::leak(text.to_owned().into_boxed_str())
}

fn optional(value: &Value) -> Option<&'static str> {
    value.as_str().map(leak)
}

fn scan_apps(scan: &Value) -> GpuAppInventory {
    let apps = scan["apps"]
        .as_array()
        .unwrap()
        .iter()
        .map(|app| {
            let block_reason = app["blockReason"].as_str().map(|text| {
                [
                    BlockReason::OtherSession,
                    BlockReason::OtherAccount,
                    BlockReason::Protected,
                ]
                .into_iter()
                .find(|reason| reason.message() == text)
                .expect("a managed block reason")
            });
            assert_eq!(app["canClose"], block_reason.is_none());
            GpuApp {
                process_id: app["processId"].as_i64().unwrap() as i32,
                start_time_utc_ticks: app["startTimeUtcTicks"].as_i64().unwrap(),
                name: app["name"].as_str().unwrap().into(),
                utilization_percent: app["utilizationPercent"].as_f64().unwrap(),
                block_reason,
            }
        })
        .collect();
    GpuAppInventory {
        apps,
        truncated: false,
        omissions: Default::default(),
    }
}

/// Numbers compare by value: managed writes `100` where serde writes `100.0`.
fn normalized(mut response: Value) -> Value {
    for app in response["apps"].as_array_mut().unwrap() {
        let percent = app["utilizationPercent"].as_f64().unwrap();
        app["utilizationPercent"] = Value::from(percent);
    }
    response
}

fn capture(case: &str, frame: &[u8]) {
    if let Some(path) = std::env::var_os("OPENBLADE_NATIVE_AGENT_GPU_FRAMES") {
        use std::io::Write;
        static LOCK: std::sync::Mutex<()> = std::sync::Mutex::new(());
        let _guard = LOCK.lock().unwrap();
        let row = serde_json::json!({"case": case, "frame": encode64(frame)});
        let mut file = std::fs::OpenOptions::new()
            .create(true)
            .append(true)
            .open(path)
            .unwrap();
        writeln!(file, "{row}").unwrap();
    }
}

/// The response mapping only: the scan the managed dispatcher saw and the
/// results it reported become the inventory and report given to the native
/// mapping, which must then write the managed response (status, error, app
/// projection and both 128-entry caps). The platform test
/// `managed_dispatch_close_rows_are_derived_by_the_decision` derives those
/// results from the same rows through the real close decision.
#[test]
fn managed_dispatcher_responses_match_the_native_mapping() {
    let mut checked = 0;
    for line in DISPATCH.lines().filter(|line| !line.is_empty()) {
        let row: Value = serde_json::from_str(line).unwrap();
        let case = row["case"].as_str().unwrap();
        let request = Request::parse(body(&base64(row["request"].as_str().unwrap()))).unwrap();
        let expected = &row["response"];
        let available = row["scan"]["isAvailable"].as_bool().unwrap();
        let (answer, query) = match &request {
            Request::Query { request_id } => (
                answer_query(
                    request_id,
                    if available {
                        Ok(scan_apps(&row["scan"]))
                    } else {
                        Err(QueryUnavailable::Counters)
                    },
                ),
                true,
            ),
            Request::Close { request_id, .. } => {
                let report = Report {
                    results: expected["results"]
                        .as_array()
                        .unwrap()
                        .iter()
                        .map(|result| Outcome {
                            process_id: result["processId"].as_i64().unwrap() as i32,
                            start_time_utc_ticks: result["startTimeUtcTicks"].as_i64().unwrap(),
                            error: optional(&result["error"]),
                        })
                        .collect(),
                    error: optional(&expected["error"]),
                };
                let result = if available {
                    Ok((scan_apps(&row["scan"]), report))
                } else {
                    Err(QueryUnavailable::Counters)
                };
                (answer_close(request_id, result), false)
            }
        };
        let Answer::Reply(response) = answer else {
            panic!("{case}: no reply");
        };
        let frame = response.frame(query).unwrap();
        let actual: Value = serde_json::from_slice(body(&frame)).unwrap();
        assert_eq!(normalized(actual), normalized(expected.clone()), "{case}");
        capture(case, &frame);
        checked += 1;
    }
    assert_eq!(checked, 13, "every dispatcher row is checked");
}

/// Native-only replies (and the lost-query reply) are managed-decodable.
#[test]
fn native_only_replies_are_valid_managed_responses() {
    let id = "9f876742-1234-4321-abcd-d3ea3e1cf102";
    for (case, error) in [
        ("native-dispatch-failure", DISPATCH_FAILURE),
        ("native-counters-unavailable", COUNTERS_UNAVAILABLE),
    ] {
        let frame = Response::failed(id, error).frame(false).unwrap();
        let actual: Value = serde_json::from_slice(body(&frame)).unwrap();
        assert_eq!(actual["status"], 2);
        assert_eq!(actual["error"], error);
        capture(case, &frame);
    }
    let name = "\u{e9}".repeat(260);
    let apps = (1..=128)
        .map(|process_id| GpuApp {
            process_id,
            start_time_utc_ticks: 638_900_000_000_000_000,
            name: name.clone(),
            utilization_percent: 1.0,
            block_reason: None,
        })
        .collect();
    let Answer::Reply(response) = answer_query(
        id,
        Ok(GpuAppInventory {
            apps,
            truncated: false,
            omissions: Default::default(),
        }),
    ) else {
        panic!("no reply");
    };
    let frame = response.frame(true).unwrap();
    assert!(frame.len() <= MAX_FRAME as usize + 4);
    let trimmed: Value = serde_json::from_slice(body(&frame)).unwrap();
    assert_eq!(trimmed["error"], TRIMMED);
    assert!(trimmed["apps"].as_array().unwrap().len() < 128);
    capture("native-trimmed-query", &frame);
}

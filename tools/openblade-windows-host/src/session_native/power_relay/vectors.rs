//! Managed parity: `session-power-relay-vectors.json`, recorded by
//! `SessionPowerTransitionRelayNativeVectorTests` from the managed
//! `IpcMessageCodec` and the real `SessionPowerTransitionRelay`. Each encoding
//! is the exact payload managed writes, or a refusal before any byte. Each
//! notification case feeds the same native types to the native relay with
//! the managed clock (instance start and every timestamp at QPC 100) over a
//! sink that answers at once, as the managed sink does, so each report ends
//! before the next notification. Native differs in no case.
use serde_json::{Map, Value, json};

use super::{tests::relay, *};
use std::sync::{Arc, atomic::AtomicI64};

const VECTORS: &str = include_str!("../../../tests/data/session-power-relay-vectors.json");

fn members<'a>(value: &'a Value, allowed: &[&str]) -> &'a Map<String, Value> {
    let object = value.as_object().unwrap();
    for member in object.keys() {
        assert!(allowed.contains(&member.as_str()), "unexpected {member}");
    }
    object
}

fn guid(value: &Value) -> [u8; 16] {
    let hex: String = value.as_str().unwrap().split('-').collect();
    u128::from_str_radix(&hex, 16).unwrap().to_be_bytes()
}

#[test]
fn every_managed_encoding_matches_the_native_request() {
    let root: Value = serde_json::from_str(VECTORS).unwrap();
    let root = members(&root, &["comment", "encodings", "notifications"]);
    assert!(root["comment"].is_string());
    let cases = root["encodings"].as_array().unwrap();
    assert!(cases.len() >= 9, "the reviewed cases are present");
    for case in cases {
        let case = members(
            case,
            &[
                "name",
                "request",
                "instance",
                "started",
                "sequence",
                "timestamp",
                "suspending",
                "expect",
            ],
        );
        let request = wire::Request {
            request_id: guid(&case["request"]),
            instance: guid(&case["instance"]),
            started: case["started"].as_i64().unwrap(),
            transition: Transition {
                sequence: case["sequence"].as_i64().unwrap(),
                timestamp: case["timestamp"].as_i64().unwrap(),
                suspending: case["suspending"].as_bool().unwrap(),
            },
        };
        let observed = match request.encode() {
            Ok(payload) => json!({"payload": String::from_utf8(payload).unwrap()}),
            Err(error) => {
                assert_eq!(error.kind(), io::ErrorKind::InvalidData);
                json!({"refused": true})
            }
        };
        assert_eq!(observed, case["expect"], "{}", case["name"]);
    }
}

#[test]
fn every_managed_notification_sequence_sends_the_same_requests() {
    let root: Value = serde_json::from_str(VECTORS).unwrap();
    let cases = root["notifications"].as_array().unwrap();
    assert!(cases.len() >= 5, "the reviewed cases are present");
    for case in cases {
        let case = members(case, &["name", "types", "expect"]);
        let relay = relay(&Arc::new(AtomicI64::new(100)));
        let mut sent = Vec::new();
        for kind in case["types"].as_array().unwrap() {
            relay.notify(u32::try_from(kind.as_u64().unwrap()).unwrap());
            // The sink answers at once: every started report ends here.
            loop {
                match relay.next() {
                    Next::Send(transition, _) => {
                        assert_eq!(transition.timestamp, 100);
                        sent.push(json!({
                            "sequence": transition.sequence,
                            "suspending": transition.suspending,
                        }));
                        relay.finish(transition);
                    }
                    Next::Idle => break,
                    next => panic!("{}: unexpected {next:?}", case["name"]),
                }
            }
        }
        relay.dispose();
        assert_eq!(Value::from(sent), case["expect"], "{}", case["name"]);
    }
}

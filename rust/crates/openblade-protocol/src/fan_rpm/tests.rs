extern crate std;
use super::*;
use crate::{REPORT_LENGTH, calculate_checksum};
use serde_json::Value;
use std::{format, string::String, vec::Vec};

fn hex(bytes: &[u8]) -> String {
    bytes.iter().map(|byte| format!("{byte:02X}")).collect()
}

fn unhex(text: &str) -> Vec<u8> {
    (0..text.len())
        .step_by(2)
        .map(|i| u8::from_str_radix(&text[i..i + 2], 16).unwrap())
        .collect()
}

fn selector(value: &Value) -> FanRpmSelector {
    match value.as_u64().unwrap() {
        1 => FanRpmSelector::First,
        2 => FanRpmSelector::Second,
        other => panic!("unknown selector {other}"),
    }
}

/// The capture-backed exact RZ09-0581 exchanges: each request is the captured
/// envelope and each captured reply decodes to the fixture's RPM.
#[test]
fn queries_and_replies_are_the_captured_rz09_0581_exchanges() {
    let fixture: Value = serde_json::from_str(include_str!(
        "../../../../../evidence/legacy-captures/decoded/rz09-0581-pid-02e0-bios-3.01-fan-rpm-fixture.json"
    ))
    .unwrap();
    assert_eq!(fixture["device"]["model"], "RZ09-0581");
    assert_eq!(fixture["device"]["vendorId"], "1532");
    assert_eq!(fixture["device"]["productId"], "02E0");
    assert_eq!(fixture["readOnly"], true);
    assert_eq!(fixture["productionWired"], true);
    assert_eq!(fixture["command"]["classHex"], "0D");
    assert_eq!(fixture["command"]["idHex"], "88");
    assert_eq!(fixture["command"]["payloadLength"], PAYLOAD_LENGTH);
    assert_eq!(fixture["command"]["scaleRpmPerUnit"], RPM_PER_UNIT);
    let exchanges = fixture["exchanges"].as_array().unwrap();
    assert_eq!(exchanges.len(), 4);
    for exchange in exchanges {
        let transaction =
            u8::from_str_radix(exchange["transactionIdHex"].as_str().unwrap(), 16).unwrap();
        let query = FanRpmQuery::new(transaction, selector(&exchange["selector"]));
        // The captured envelopes are the 90-byte reports without the OS prefix.
        assert_eq!(
            hex(&query.request().encode()),
            exchange["requestEnvelopeHex"]
                .as_str()
                .unwrap()
                .to_ascii_uppercase()
        );
        let reply = unhex(exchange["responseEnvelopeHex"].as_str().unwrap());
        assert_eq!(
            query.decode(&reply).map(u64::from),
            Ok(exchange["expectedRpm"].as_u64().unwrap())
        );
    }
}

fn vectors() -> Value {
    serde_json::from_str(include_str!(
        "../../../../../tools/openblade-windows-host/tests/data/live-reading-vectors.json"
    ))
    .unwrap()
}

/// The managed recorder's construction: status 2, the row payload, its edits,
/// the recomputed checksum (inverted on request) and the envelope length.
fn response(request: &RazerReport, row: &Value) -> Vec<u8> {
    let bytes = request.encode();
    let payload = unhex(row["payload"].as_str().unwrap());
    let mut response = RazerReport::create(bytes[1], bytes[6], bytes[7], &payload)
        .unwrap()
        .encode();
    response[0] = 2;
    if let Some(edits) = row.get("edits") {
        for (offset, value) in edits.as_object().unwrap() {
            let offset: usize = offset.parse().unwrap();
            assert_ne!(offset, 88);
            response[offset] = value.as_u64().unwrap() as u8;
        }
    }
    response[88] = calculate_checksum(&response).unwrap();
    if row.get("corruptChecksum").is_some() {
        response[88] ^= 0xff;
    }
    let mut response = response.to_vec();
    let envelope = row
        .get("envelope")
        .map_or(REPORT_LENGTH, |n| n.as_u64().unwrap() as usize);
    response.resize(envelope, 0);
    response
}

#[test]
fn requests_and_replies_match_the_managed_codec_vectors() {
    let vectors = vectors();
    let mut requests = std::collections::BTreeMap::new();
    for row in vectors["requests"].as_array().unwrap() {
        let transaction = row["transactionId"].as_u64().unwrap() as u8;
        let selector = selector(&row["selector"]);
        let query = FanRpmQuery::new(transaction, selector);
        assert_eq!(hex(&query.request().encode()), row["report"]);
        requests.insert(row["name"].as_str().unwrap(), (transaction, selector));
    }
    assert_eq!(requests.len(), 2);
    let replies = vectors["replies"].as_array().unwrap();
    for row in replies {
        let name = row["name"].as_str().unwrap();
        let (transaction, selector) = requests[row["request"].as_str().unwrap()];
        let query = FanRpmQuery::new(transaction, selector);
        let response = response(query.request(), row);
        assert_eq!(hex(&response), row["response"], "{name}");
        let decoded = match query.decode(&response) {
            Ok(rpm) => Value::from(rpm),
            Err(failure) => Value::from(format!("{failure:?}")),
        };
        assert_eq!(row["expect"], decoded, "{name}");
    }
    for expect in [
        "PayloadValidator",
        "PayloadLength",
        "Status",
        "TransactionMismatch",
        "CommandClassMismatch",
        "CommandIdMismatch",
        "Checksum",
        "EnvelopeLength",
    ] {
        assert!(
            replies.iter().any(|row| row["expect"] == expect),
            "{expect}"
        );
    }
}

/// Every unit byte scales by 100; any other leading byte or selector is refused.
#[test]
fn only_the_selector_echo_decodes() {
    for (selector, other) in [
        (FanRpmSelector::First, FanRpmSelector::Second),
        (FanRpmSelector::Second, FanRpmSelector::First),
    ] {
        let query = FanRpmQuery::new(7, selector);
        for units in 0..=255u8 {
            for (leading, returned, ok) in [
                (1, selector as u8, true),
                (1, other as u8, false),
                (0, selector as u8, false),
                (2, selector as u8, false),
            ] {
                let mut reply = RazerReport::create(7, CLASS, QUERY, &[leading, returned, units])
                    .unwrap()
                    .encode();
                reply[0] = 2;
                reply[88] = calculate_checksum(&reply).unwrap();
                let expected = if ok {
                    Ok(u16::from(units) * 100)
                } else {
                    Err(ProtocolValidationFailure::PayloadValidator)
                };
                assert_eq!(query.decode(&reply), expected);
            }
        }
    }
}

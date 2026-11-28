extern crate std;
use super::*;
use crate::calculate_checksum;
use serde_json::Value;
use std::{format, string::String, string::ToString, vec::Vec};

fn hex(bytes: &[u8]) -> String {
    bytes.iter().map(|byte| format!("{byte:02X}")).collect()
}

fn unhex(text: &str) -> Vec<u8> {
    (0..text.len())
        .step_by(2)
        .map(|i| u8::from_str_radix(&text[i..i + 2], 16).unwrap())
        .collect()
}

/// The capture-backed exact RZ09-0581 exchange: the request is the captured
/// envelope and the captured reply, with its four bytes after the payload,
/// decodes to the fixture's version.
#[test]
fn the_query_and_reply_are_the_captured_rz09_0581_exchange() {
    let fixture: Value = serde_json::from_str(include_str!(
        "../../../../../evidence/legacy-captures/decoded/rz09-0581-pid-02e0-bios-3.01-mcu-version-fixture.json"
    ))
    .unwrap();
    assert_eq!(fixture["device"]["model"], "RZ09-0581");
    assert_eq!(fixture["device"]["vendorId"], "1532");
    assert_eq!(fixture["device"]["productId"], "02E0");
    assert_eq!(fixture["readOnly"], true);
    assert_eq!(fixture["productionWired"], true);
    let command = &fixture["command"];
    assert_eq!(command["classHex"], "00");
    assert_eq!(command["idHex"], "87");
    assert_eq!(command["payloadLength"], PAYLOAD_LENGTH);
    assert_eq!(command["requestPayloadHex"], hex(&QUERY_ARGUMENTS));
    let exchange = &fixture["exchange"];
    let transaction =
        u8::from_str_radix(exchange["transactionIdHex"].as_str().unwrap(), 16).unwrap();
    let query = McuVersionQuery::new(transaction);
    assert_eq!(
        hex(&query.request().encode()),
        exchange["requestEnvelopeHex"]
            .as_str()
            .unwrap()
            .to_ascii_uppercase()
    );
    let reply = unhex(exchange["responseEnvelopeHex"].as_str().unwrap());
    let version = query.decode(&reply).unwrap();
    assert_eq!(
        hex(&version.0),
        exchange["expectedResponsePayloadHex"]
            .as_str()
            .unwrap()
            .to_ascii_uppercase()
    );
    assert_eq!(version.to_string(), exchange["decodedVersion"]);
    assert_eq!(
        fixture["synapsePublishedCode"]["requestDescriptor"],
        serde_json::json!([PAYLOAD_LENGTH, CLASS, QUERY])
    );
}

fn vectors() -> Value {
    serde_json::from_str(include_str!(
        "../../../../../tools/openblade-windows-host/tests/data/mcu-version-vectors.json"
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
        .map_or(crate::REPORT_LENGTH, |n| n.as_u64().unwrap() as usize);
    response.resize(envelope, 0);
    response
}

#[test]
fn requests_and_replies_match_the_managed_codec_vectors() {
    let vectors = vectors();
    let mut requests = std::collections::BTreeMap::new();
    for row in vectors["requests"].as_array().unwrap() {
        let transaction = row["transactionId"].as_u64().unwrap() as u8;
        let query = McuVersionQuery::new(transaction);
        assert_eq!(hex(&query.request().encode()), row["report"]);
        requests.insert(row["name"].as_str().unwrap(), transaction);
    }
    assert_eq!(requests.len(), 2);
    let replies = vectors["replies"].as_array().unwrap();
    for row in replies {
        let name = row["name"].as_str().unwrap();
        let query = McuVersionQuery::new(requests[row["request"].as_str().unwrap()]);
        let response = response(query.request(), row);
        assert_eq!(hex(&response), row["response"], "{name}");
        let decoded = match query.decode(&response) {
            Ok(version) => version.to_string(),
            Err(failure) => format!("{failure:?}"),
        };
        assert_eq!(row["expect"], decoded, "{name}");
    }
    for expect in [
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

/// Every four-byte payload is a version, each byte one decimal component in
/// reply order.
#[test]
fn every_four_byte_payload_formats_as_four_components() {
    let query = McuVersionQuery::new(7);
    for byte in 0..=255u8 {
        let payload = [byte, byte.wrapping_add(1), byte.wrapping_mul(3), 255 - byte];
        let mut reply = RazerReport::create(7, CLASS, QUERY, &payload)
            .unwrap()
            .encode();
        reply[0] = 2;
        reply[88] = calculate_checksum(&reply).unwrap();
        let version = query.decode(&reply).unwrap();
        assert_eq!(version, McuVersion(payload));
        assert_eq!(
            version.to_string(),
            format!(
                "{}.{}.{}.{}",
                payload[0], payload[1], payload[2], payload[3]
            )
        );
    }
}

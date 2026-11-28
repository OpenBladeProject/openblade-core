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

fn vectors() -> Value {
    serde_json::from_str(include_str!(
        "../../../../../tools/openblade-windows-host/tests/data/keyboard-driver-mode-vectors.json"
    ))
    .unwrap()
}

/// `CC/II:payload` in the managed semantic admission's notation.
fn notation(report: &RazerReport) -> String {
    let bytes = report.encode();
    let length = usize::from(bytes[5]);
    format!(
        "{:02X}/{:02X}:{}",
        bytes[6],
        bytes[7],
        hex(&bytes[8..8 + length])
    )
}

/// Every report this codec builds is one of the three reviewed managed
/// production commands for exact RZ09-0581, in that admission's own file.
#[test]
fn commands_are_the_reviewed_rz09_0581_semantic_admission() {
    let admission: Value = serde_json::from_str(include_str!(
        "../../../../../src/OpenBlade.Protocol/Fixtures/Rz09_0581_02e0_3_01_LightingSemanticAdmission.json"
    ))
    .unwrap();
    let device = &admission["device"];
    assert_eq!(device["model"], "RZ09-0581");
    assert_eq!(device["vendorId"], 0x1532);
    assert_eq!(device["productId"], 0x02e0);
    let mode = &admission["deviceMode"];
    assert_eq!(mode.as_object().unwrap().len(), 3);
    assert_eq!(mode["query"], notation(DeviceModeQuery::new(9).request()));
    assert_eq!(
        mode["setNormal"],
        notation(DeviceModeSet::new(9, DeviceMode::Normal).request())
    );
    assert_eq!(
        mode["setDriver"],
        notation(DeviceModeSet::new(9, DeviceMode::Driver).request())
    );
}

fn request(name: &str, transaction: u8) -> RazerReport {
    match name {
        "query" => DeviceModeQuery::new(transaction).request().clone(),
        "setNormal" => DeviceModeSet::new(transaction, DeviceMode::Normal)
            .request()
            .clone(),
        "setDriver" => DeviceModeSet::new(transaction, DeviceMode::Driver)
            .request()
            .clone(),
        _ => panic!("unknown request {name}"),
    }
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

fn failure(failure: ProtocolValidationFailure) -> String {
    format!("{failure:?}")
}

#[test]
fn requests_and_replies_match_the_managed_codec_vectors() {
    let vectors = vectors();
    let mut ids = std::collections::BTreeMap::new();
    for row in vectors["requests"].as_array().unwrap() {
        let name = row["name"].as_str().unwrap();
        let transaction = row["transactionId"].as_u64().unwrap() as u8;
        assert_eq!(
            hex(&request(name, transaction).encode()),
            row["report"],
            "{name}"
        );
        ids.insert(name, transaction);
    }
    assert_eq!(ids.len(), 3);
    let replies = vectors["replies"].as_array().unwrap();
    for row in replies {
        let name = row["name"].as_str().unwrap();
        let kind = row["request"].as_str().unwrap();
        let transaction = ids[kind];
        let response = response(&request(kind, transaction), row);
        assert_eq!(hex(&response), row["response"], "{name}");
        let decoded = match kind {
            "query" => DeviceModeQuery::new(transaction)
                .decode(&response)
                .map_or_else(failure, |mode| format!("{mode:?}")),
            "setNormal" | "setDriver" => {
                let mode = if kind == "setNormal" {
                    DeviceMode::Normal
                } else {
                    DeviceMode::Driver
                };
                DeviceModeSet::new(transaction, mode)
                    .acknowledge(&response)
                    .map_or_else(failure, |()| "Acknowledged".into())
            }
            _ => unreachable!(),
        };
        assert_eq!(row["expect"], decoded, "{name}");
    }
    // Both modes, every other mode byte tried and each envelope fault appear.
    for expect in [
        "Normal",
        "Driver",
        "Acknowledged",
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

/// No mode byte other than the two reviewed ones decodes, whatever its
/// second byte, and a correlated reply of any other length is refused.
#[test]
fn only_the_two_reviewed_modes_decode() {
    let query = DeviceModeQuery::new(7);
    for mode in 0..=255u8 {
        for second in [0, 1, 3, 0xff] {
            let mut reply = RazerReport::create(7, 0, 0x84, &[mode, second])
                .unwrap()
                .encode();
            reply[0] = 2;
            reply[88] = calculate_checksum(&reply).unwrap();
            let expected = match (mode, second) {
                (0, 0) => Ok(DeviceMode::Normal),
                (3, 0) => Ok(DeviceMode::Driver),
                _ => Err(ProtocolValidationFailure::PayloadValidator),
            };
            assert_eq!(query.decode(&reply), expected, "{mode:02X}{second:02X}");
        }
    }
    for length in [0usize, 1, 3, 80] {
        let payload = [3u8; 80];
        let mut reply = RazerReport::create(7, 0, 0x84, &payload[..length])
            .unwrap()
            .encode();
        reply[0] = 2;
        reply[88] = calculate_checksum(&reply).unwrap();
        assert_eq!(
            query.decode(&reply),
            Err(ProtocolValidationFailure::PayloadLength)
        );
    }
}

#[test]
fn setter_acknowledgement_is_the_exact_echo() {
    for (mode, other) in [
        (DeviceMode::Normal, DeviceMode::Driver),
        (DeviceMode::Driver, DeviceMode::Normal),
    ] {
        let set = DeviceModeSet::new(5, mode);
        let mut ack = set.request().encode();
        ack[0] = 2;
        assert_eq!(set.acknowledge(&ack), Ok(()));
        let mut wrong = DeviceModeSet::new(5, other).request().encode();
        wrong[0] = 2;
        assert_eq!(
            set.acknowledge(&wrong),
            Err(ProtocolValidationFailure::PayloadValidator)
        );
        // A setter acknowledgement is never a mode readback.
        assert_eq!(
            DeviceModeQuery::new(5).decode(&ack),
            Err(ProtocolValidationFailure::CommandIdMismatch)
        );
    }
}

//! Reads the same strict, literal JSON baseline as the C# tests; no golden copies.
use std::collections::HashSet;

use openblade_protocol::{RazerReport, encode_report};
use openblade_types::ReportEncodingError;
use serde::{Deserialize, Deserializer};

const JSON: &str = include_str!("../../../../tests/Fixtures/Parity/v1/razer-report.json");
const REVISION: &str = "123936f3ea69881d21bd16a7a09529930c9a2c6f";

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Fixture {
    schema_version: u32,
    suite: String,
    source_revision: String,
    encoding: Vec<Encoding>,
    responses: Vec<Response>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Request {
    transaction_id: u8,
    command_class: u8,
    command_id: u8,
    payload_hex: Hex,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Encoding {
    id: String,
    request: Request,
    destination_length: usize,
    #[serde(deserialize_with = "required_nullable")]
    expected_error: Option<EncodingFailure>,
    #[serde(deserialize_with = "required_nullable")]
    expected_report_hex: Option<Hex>,
}

#[derive(Clone, Copy, Debug, Deserialize, Eq, PartialEq)]
#[serde(rename_all = "kebab-case")]
enum EncodingFailure {
    PayloadTooLarge,
    DestinationLength,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Response {
    id: String,
    request: Request,
    response_hex: Hex,
    #[serde(deserialize_with = "required_nullable")]
    expected_payload_length: Option<i32>,
    #[serde(deserialize_with = "required_nullable")]
    expected_failure: Option<Failure>,
    #[serde(deserialize_with = "required_nullable")]
    expected_payload_hex: Option<Hex>,
}

#[derive(Debug, Deserialize)]
#[serde(deny_unknown_fields)]
struct Failure {
    name: String,
    value: u32,
}

// deserialize_with makes a nullable field mandatory, unlike serde's Option default.
fn required_nullable<'de, D, T>(deserializer: D) -> Result<Option<T>, D::Error>
where
    D: Deserializer<'de>,
    T: Deserialize<'de>,
{
    Option::deserialize(deserializer)
}

#[derive(Debug)]
struct Hex(Vec<u8>);

impl<'de> Deserialize<'de> for Hex {
    fn deserialize<D: Deserializer<'de>>(deserializer: D) -> Result<Self, D::Error> {
        let text = String::deserialize(deserializer)?;
        if !text.len().is_multiple_of(2)
            || !text
                .bytes()
                .all(|byte| byte.is_ascii_digit() || (b'A'..=b'F').contains(&byte))
        {
            return Err(serde::de::Error::custom("expected uppercase full-byte hex"));
        }
        let digit = |byte: u8| {
            if byte <= b'9' {
                byte - b'0'
            } else {
                byte - b'A' + 10
            }
        };
        Ok(Self(
            text.as_bytes()
                .as_chunks::<2>()
                .0
                .iter()
                .map(|pair| digit(pair[0]) * 16 + digit(pair[1]))
                .collect(),
        ))
    }
}

fn parse(json: &str) -> Result<Fixture, String> {
    let fixture: Fixture = serde_json::from_str(json).map_err(|error| error.to_string())?;
    if fixture.schema_version != 1
        || fixture.suite != "razer-report"
        || fixture.source_revision != REVISION
    {
        return Err("unsupported schema, suite or provenance".into());
    }
    for ids in [
        fixture
            .encoding
            .iter()
            .map(|row| &row.id)
            .collect::<Vec<_>>(),
        fixture.responses.iter().map(|row| &row.id).collect(),
    ] {
        let mut seen = HashSet::new();
        if ids.is_empty()
            || ids
                .iter()
                .any(|id| id.trim().is_empty() || !seen.insert(*id))
        {
            return Err("empty section, blank ID or duplicate ID".into());
        }
    }
    for row in &fixture.encoding {
        if row.expected_error.is_none() != row.expected_report_hex.is_some() {
            return Err(format!("{}: incoherent encoding expectation", row.id));
        }
        if row
            .expected_report_hex
            .as_ref()
            .is_some_and(|hex| hex.0.len() != 90)
        {
            return Err(format!("{}: expected report is not 90 bytes", row.id));
        }
    }
    for row in &fixture.responses {
        if row.expected_failure.is_none() != row.expected_payload_hex.is_some() {
            return Err(format!("{}: incoherent response expectation", row.id));
        }
        if let Some(failure) = &row.expected_failure {
            let names = [
                "ReportId",
                "EnvelopeLength",
                "Checksum",
                "TransactionMismatch",
                "CommandClassMismatch",
                "CommandIdMismatch",
                "Status",
                "PayloadLength",
                "PayloadValidator",
            ];
            if names.get(failure.value as usize).copied() != Some(failure.name.as_str()) {
                return Err(format!("{}: unknown failure name/value", row.id));
            }
        }
    }
    Ok(fixture)
}

fn encoding_case(id: &str) {
    let fixture = parse(JSON).unwrap();
    let row = fixture.encoding.iter().find(|row| row.id == id).expect(id);
    let request = &row.request;
    let mut destination = vec![0xA5; row.destination_length];
    let result = encode_report(
        request.transaction_id,
        request.command_class,
        request.command_id,
        &request.payload_hex.0,
        &mut destination,
    );
    let instance = RazerReport::create(
        request.transaction_id,
        request.command_class,
        request.command_id,
        &request.payload_hex.0,
    );
    match row.expected_error {
        Some(EncodingFailure::PayloadTooLarge) => {
            let error = ReportEncodingError::PayloadTooLarge {
                actual: request.payload_hex.0.len(),
            };
            assert_eq!(result, Err(error), "{id}: static encoder");
            assert_eq!(instance, Err(error), "{id}: creation");
        }
        Some(EncodingFailure::DestinationLength) => {
            let error = ReportEncodingError::DestinationLength {
                actual: row.destination_length,
            };
            assert_eq!(result, Err(error), "{id}: static encoder");
            assert_eq!(
                instance.unwrap().encode_into(&mut destination),
                Err(error),
                "{id}: instance encoder"
            );
        }
        None => {
            assert_eq!(result, Ok(()), "{id}");
            let expected = &row.expected_report_hex.as_ref().unwrap().0;
            assert_eq!(&destination, expected, "{id}: static encoder");
            let report = instance.unwrap();
            assert_eq!(report.encode().as_slice(), expected, "{id}: owned encoder");
            destination.fill(0xA5);
            report.encode_into(&mut destination).unwrap();
            assert_eq!(&destination, expected, "{id}: instance encoder");
            return;
        }
    }
    assert!(
        destination.iter().all(|byte| *byte == 0xA5),
        "{id}: rejected encoding changed output"
    );
}

fn response_case(id: &str) {
    let fixture = parse(JSON).unwrap();
    let row = fixture.responses.iter().find(|row| row.id == id).expect(id);
    let request = &row.request;
    let report = RazerReport::create(
        request.transaction_id,
        request.command_class,
        request.command_id,
        &request.payload_hex.0,
    )
    .unwrap();
    let mut response = row.response_hex.0.clone();
    let result = report.validate_response(&response, row.expected_payload_length);
    if let Some(failure) = &row.expected_failure {
        let error = result.expect_err(id);
        assert_eq!(format!("{error:?}"), failure.name, "{id}: failure name");
        assert_eq!(error as u32, failure.value, "{id}: failure number");
    } else {
        let payload = result.expect(id);
        let expected = &row.expected_payload_hex.as_ref().unwrap().0;
        assert_eq!(payload.as_slice(), expected, "{id}");
        response.fill(0xA5);
        assert_eq!(payload.as_slice(), expected, "{id}: payload must be owned");
    }
}

macro_rules! encoding_cases {
    ($($name:ident => $id:literal),+ $(,)?) => { $(#[test] fn $name() { encoding_case($id); })+ };
}
macro_rules! response_cases {
    ($($name:ident => $id:literal),+ $(,)?) => { $(#[test] fn $name() { response_case($id); })+ };
}

encoding_cases! {
    encoding_known_balanced => "known-balanced",
    encoding_empty_payload => "empty-payload",
    encoding_maximum_zero_payload => "maximum-zero-payload",
    encoding_maximum_sequential_payload => "maximum-sequential-payload",
    encoding_nonzero_reused_buffer => "nonzero-reused-buffer",
    encoding_payload_81 => "payload-81",
    encoding_destination_89 => "destination-89",
    encoding_destination_91 => "destination-91",
    encoding_payload_before_destination_length => "payload-before-destination-length",
}
response_cases! {
    response_success_known_payload => "success-known-payload",
    response_success_unspecified_length => "success-unspecified-length",
    response_success_empty => "success-empty",
    response_success_maximum => "success-maximum",
    response_envelope_89 => "envelope-89",
    response_envelope_91 => "envelope-91",
    response_checksum => "checksum",
    response_transaction => "transaction",
    response_command_class => "command-class",
    response_command_id => "command-id",
    response_status_new => "status-new",
    response_status_not_supported => "status-not-supported",
    response_status_unknown => "status-unknown",
    response_declared_payload_81 => "declared-payload-81",
    response_contract_length_mismatch => "contract-length-mismatch",
    response_checksum_before_transaction => "checksum-before-transaction",
    response_transaction_before_class => "transaction-before-class",
    response_class_before_id_and_status => "class-before-id-and-status",
    response_id_before_status => "id-before-status",
    response_status_before_payload_length => "status-before-payload-length",
}

#[test]
fn every_canonical_report_case_is_exercised() {
    let fixture = parse(JSON).unwrap();
    assert_eq!(
        fixture.encoding.len(),
        9,
        "add an ID-based test for each new case"
    );
    assert_eq!(
        fixture.responses.len(),
        20,
        "add an ID-based test for each new case"
    );
    for row in fixture.encoding {
        encoding_case(&row.id);
    }
    for row in fixture.responses {
        response_case(&row.id);
    }
}

#[test]
fn reader_rejects_malformed_contract() {
    let original: serde_json::Value = serde_json::from_str(JSON).unwrap();
    let mutations = [
        ("/schemaVersion", serde_json::json!(2)),
        ("/schemaVersion", serde_json::json!("1")),
        ("/suite", serde_json::json!("other")),
        ("/sourceRevision", serde_json::json!("bad")),
        ("/encoding/0/id", serde_json::json!(" ")),
        ("/encoding/1/id", serde_json::json!("known-balanced")),
        (
            "/responses/1/id",
            serde_json::json!("success-known-payload"),
        ),
        ("/encoding/0/request/transactionId", serde_json::json!("28")),
        ("/encoding/0/request/transactionId", serde_json::json!(256)),
        ("/encoding/0/request/commandClass", serde_json::json!(-1)),
        ("/encoding/0/request/payloadHex", serde_json::json!("aa")),
        ("/encoding/0/request/payloadHex", serde_json::json!("A")),
        ("/encoding/0/request/payloadHex", serde_json::json!("GG")),
        ("/encoding/0/expectedError", serde_json::json!("other")),
        ("/responses/4/expectedFailure/value", serde_json::json!("1")),
        (
            "/responses/4/expectedFailure/name",
            serde_json::json!("Other"),
        ),
        ("/responses/0/expectedPayloadLength", serde_json::json!(1.0)),
        ("/encoding", serde_json::json!([])),
        ("/responses", serde_json::json!([])),
    ];
    for (path, value) in mutations {
        let mut changed = original.clone();
        *changed.pointer_mut(path).unwrap() = value;
        assert!(
            parse(&changed.to_string()).is_err(),
            "accepted malformed {path}"
        );
    }
    // Every required field, including nullable fields, must be present; duplicate
    // fields must be rejected before serde_json::Value could collapse them.
    for path in [
        "",
        "/encoding/0",
        "/encoding/0/request",
        "/responses/0",
        "/responses/4/expectedFailure",
    ] {
        let object = original.pointer(path).unwrap().as_object().unwrap();
        for key in object.keys() {
            let mut changed = original.clone();
            changed
                .pointer_mut(path)
                .unwrap()
                .as_object_mut()
                .unwrap()
                .remove(key);
            assert!(
                parse(&changed.to_string()).is_err(),
                "accepted missing {path}/{key}"
            );
        }
        let mut changed = original.clone();
        changed
            .pointer_mut(path)
            .unwrap()
            .as_object_mut()
            .unwrap()
            .insert("unexpected".into(), serde_json::json!(true));
        assert!(
            parse(&changed.to_string()).is_err(),
            "accepted unknown field at {path}"
        );
    }
    for (field, value) in [
        ("schemaVersion", "1"),
        ("transactionId", "28"),
        ("expectedError", "null"),
        ("expectedFailure", "null"),
        ("name", "\"EnvelopeLength\""),
    ] {
        let needle = format!("\"{field}\": {value}");
        let duplicate = JSON.replacen(&needle, &format!("{needle}, {needle}"), 1);
        assert_ne!(duplicate, JSON, "negative test input missing");
        assert!(parse(&duplicate).is_err(), "accepted duplicate {field}");
    }
}

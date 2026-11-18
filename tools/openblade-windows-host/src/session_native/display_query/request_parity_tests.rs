//! Request frames written or refused by the managed `SessionDisplayIpcCodec`
//! (tests/oracles/display_query `requests-emit`), parsed by the native
//! endpoint. Each build accepts exactly the operations it selected.
use super::Request;

const FIXTURE: &str =
    include_str!("../../../tests/fixtures/display-requests/managed-requests.jsonl");

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

/// Operations this build selected; the managed codec supports more.
fn selected(case: &str) -> bool {
    let operation = case.split('-').next().unwrap();
    match operation {
        "query" => true,
        "setBrightness" => cfg!(feature = "agent-display-brightness-write-v1"),
        "setHdr" => cfg!(feature = "agent-display-hdr-write-v1"),
        _ => false,
    }
}

#[test]
fn managed_request_frames_parse_exactly_for_selected_operations() {
    let mut checked = 0;
    for line in FIXTURE.lines().filter(|line| !line.is_empty()) {
        let row: serde_json::Value = serde_json::from_str(line).unwrap();
        let case = row["case"].as_str().unwrap();
        let valid = row["valid"].as_bool().unwrap();
        let frame = base64(row["frame"].as_str().unwrap());
        let (length, body) = frame.split_at(4);
        assert_eq!(
            u32::from_le_bytes(length.try_into().unwrap()) as usize,
            body.len(),
            "{case}: length prefix"
        );
        let parsed = Request::parse(body);
        assert_eq!(
            parsed.is_ok(),
            valid && selected(case),
            "{case}: {:?}",
            parsed.err()
        );
        checked += 1;
    }
    assert_eq!(checked, 38, "every fixture row is checked");
}

#[test]
fn duplicate_members_and_late_operation_are_refused() {
    // Managed accepts a duplicate (last wins); native refuses it (stricter).
    let duplicate = br#"{"operation":"query","version":2,"version":2,"requestId":"9f876742-1234-4321-abcd-d3ea3e1cf102"}"#;
    assert!(Request::parse(duplicate).is_err());
    // Managed refuses a late discriminator; so does native.
    let late =
        br#"{"version":2,"operation":"query","requestId":"9f876742-1234-4321-abcd-d3ea3e1cf102"}"#;
    assert!(Request::parse(late).is_err());
    let spaced = b" {\r\n \"operation\":\"query\",\"version\":2,\"requestId\":\"9f876742-1234-4321-abcd-d3ea3e1cf102\"}";
    assert!(Request::parse(spaced).is_ok());
}

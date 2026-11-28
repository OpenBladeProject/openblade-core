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
// The arms are per-feature cfg! values, so a build that enables every feature folds them to constants.
#[allow(clippy::match_like_matches_macro)]
fn selected(case: &str) -> bool {
    let operation = case.split('-').next().unwrap();
    match operation {
        "query" => true,
        "setBrightness" => cfg!(feature = "agent-display-brightness-write-v1"),
        "setHdr" => cfg!(feature = "agent-display-hdr-write-v1"),
        "setRefreshRate" => cfg!(feature = "agent-display-refresh-write-v1"),
        "setColorProfile" => cfg!(feature = "agent-display-color-write-v1"),
        "applyProfile" => cfg!(feature = "agent-display-profile-apply-v1"),
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
    assert_eq!(checked, 81, "every fixture row is checked");
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

#[test]
fn object_form_discriminators_are_refused_like_managed() {
    let id = "9f876742-1234-4321-abcd-d3ea3e1cf102";
    let query = format!(r#"{{"operation":{{"query":null}},"version":2,"requestId":"{id}"}}"#);
    assert!(Request::parse(query.as_bytes()).is_err());
    let hdr = format!(
        r#"{{"operation":{{"setHdr":null}},"version":2,"requestId":"{id}","expectedPowerSource":2,"enabled":true}}"#
    );
    assert!(Request::parse(hdr.as_bytes()).is_err());
    let brightness = format!(
        r#"{{"operation":{{"setBrightness":null}},"version":2,"requestId":"{id}","expectedPowerSource":2,"percent":5}}"#
    );
    assert!(Request::parse(brightness.as_bytes()).is_err());
    let string = format!(r#"{{"operation":"query","version":2,"requestId":"{id}"}}"#);
    assert!(Request::parse(string.as_bytes()).is_ok());
}

/// Managed reads these (the oracle prints them as accepted observations) but
/// never writes them: its codec always writes every settings member, with
/// camelCase names, a number percent and the computed `isConfigured`.
/// Native requires that written shape (stricter).
#[cfg(feature = "agent-display-profile-apply-v1")]
#[test]
fn managed_lenient_profile_settings_are_refused() {
    let id = "9f876742-1234-4321-abcd-d3ea3e1cf102";
    let profile = |settings: &str| {
        format!(
            r#"{{"operation":"applyProfile","version":2,"requestId":"{id}","expectedPowerSource":0,"settings":{settings}}}"#
        )
    };
    let written = profile(
        r#"{"refreshRateKey":null,"hdrEnabled":true,"colorProfileKey":null,"brightnessPercent":null,"isConfigured":true}"#,
    );
    assert!(Request::parse(written.as_bytes()).is_ok());
    for settings in [
        r#"{"refreshRateKey":null,"hdrEnabled":true,"colorProfileKey":null,"isConfigured":true}"#,
        r#"{"refreshRateKey":null,"hdrEnabled":true,"colorProfileKey":null,"brightnessPercent":null}"#,
        r#"{"refreshRateKey":null,"hdrEnabled":true,"colorProfileKey":null,"brightnessPercent":null,"isConfigured":false}"#,
        r#"{"RefreshRateKey":null,"HdrEnabled":true,"ColorProfileKey":null,"BrightnessPercent":null,"IsConfigured":true}"#,
        r#"{"refreshRateKey":null,"hdrEnabled":null,"colorProfileKey":null,"brightnessPercent":"50","isConfigured":true}"#,
        // Unconfigured, so a defaulted `isConfigured` would equal the computed one.
        r#"{"refreshRateKey":null,"hdrEnabled":null,"colorProfileKey":null,"brightnessPercent":null}"#,
    ] {
        assert!(
            Request::parse(profile(settings).as_bytes()).is_err(),
            "{settings}"
        );
    }
}

/// Managed System.Text.Json rejects a JSON array for an object type; the
/// serde derive alone would bind the members positionally.
#[cfg(feature = "agent-display-profile-apply-v1")]
#[test]
fn positional_settings_arrays_are_refused_like_managed() {
    use super::{Object, WireSettings};
    let array = r#"[null,true,null,null,true]"#;
    assert!(serde_json::from_str::<WireSettings>(array).is_ok());
    assert!(serde_json::from_str::<Object<WireSettings>>(array).is_err());
    let object = r#"{"refreshRateKey":null,"hdrEnabled":true,"colorProfileKey":null,"brightnessPercent":null,"isConfigured":true}"#;
    assert!(serde_json::from_str::<Object<WireSettings>>(object).is_ok());

    let id = "9f876742-1234-4321-abcd-d3ea3e1cf102";
    for settings in [array, r#"["fixed-120",false,"sRGB.icm",50,true]"#] {
        let frame = format!(
            r#"{{"operation":"applyProfile","version":2,"requestId":"{id}","expectedPowerSource":0,"settings":{settings}}}"#
        );
        assert!(Request::parse(frame.as_bytes()).is_err(), "{settings}");
    }
}

extern crate std;
use super::*;
use crate::{REPORT_LENGTH, calculate_checksum};
use serde_json::Value;
use std::{format, string::String, vec::Vec};

fn hex(bytes: &[u8]) -> String {
    bytes.iter().map(|byte| format!("{byte:02X}")).collect()
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

/// A setter's notation without its value byte: the admission's `setPrefix`.
fn prefix(report: &RazerReport) -> String {
    let mut text = notation(report);
    text.truncate(text.len() - 2);
    text
}

/// A correlated success reply to `request` carrying `payload`.
fn reply(request: &RazerReport, payload: &[u8]) -> Vec<u8> {
    let bytes = request.encode();
    let mut reply = RazerReport::create(bytes[1], bytes[6], bytes[7], payload)
        .unwrap()
        .encode();
    reply[0] = 2;
    reply[88] = calculate_checksum(&reply).unwrap();
    reply.to_vec()
}

/// Every report this codec builds is one of the reviewed managed production
/// brightness and logo commands for exact RZ09-0581, in that admission's own
/// file, and the percent conversion gives its captured 77% value.
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
    let brightness = &admission["brightness"];
    assert_eq!(brightness.as_object().unwrap().len(), 3);
    assert_eq!(
        brightness["query"],
        notation(BrightnessQuery::new(9).request())
    );
    for raw in [0, 1, 0xc5, 0xff] {
        assert_eq!(
            brightness["setPrefix"],
            prefix(LightingSet::brightness(9, raw).request())
        );
    }
    assert_eq!(brightness["percent77Raw"], percent_to_raw(77).unwrap());
    let logo = &admission["logo"];
    assert_eq!(logo.as_object().unwrap().len(), 5);
    assert_eq!(
        logo["queryState"],
        notation(LogoStateQuery::new(9).request())
    );
    assert_eq!(
        logo["queryEffect"],
        notation(LogoEffectQuery::new(9).request())
    );
    for mode in [LogoMode::Static, LogoMode::Breathing, LogoMode::Off] {
        assert_eq!(
            logo["setStatePrefix"],
            prefix(LightingSet::logo_state(9, mode).request())
        );
        assert_eq!(
            logo["setEffectPrefix"],
            prefix(LightingSet::logo_effect(9, mode).request())
        );
    }
    assert_eq!(
        logo["supportedModes"],
        serde_json::json!(["static", "breathing", "off"])
    );
}

/// The exact reports, transaction ID included, as managed builds them.
#[test]
fn reports_encode_the_managed_arguments() {
    for (report, expected) in [
        (BrightnessQuery::new(0x21).request().clone(), "03/83:010500"),
        (LogoStateQuery::new(0x21).request().clone(), "03/80:010400"),
        (LogoEffectQuery::new(0x21).request().clone(), "03/82:010400"),
        (
            LightingSet::brightness(0x21, 0).request().clone(),
            "03/03:010500",
        ),
        (
            LightingSet::brightness(0x21, 0xc5).request().clone(),
            "03/03:0105C5",
        ),
        (
            LightingSet::logo_state(0x21, LogoMode::Off)
                .request()
                .clone(),
            "03/00:010400",
        ),
        (
            LightingSet::logo_state(0x21, LogoMode::Static)
                .request()
                .clone(),
            "03/00:010401",
        ),
        (
            LightingSet::logo_state(0x21, LogoMode::Breathing)
                .request()
                .clone(),
            "03/00:010401",
        ),
        (
            LightingSet::logo_effect(0x21, LogoMode::Static)
                .request()
                .clone(),
            "03/02:010400",
        ),
        (
            LightingSet::logo_effect(0x21, LogoMode::Breathing)
                .request()
                .clone(),
            "03/02:010402",
        ),
        (
            LightingSet::logo_effect(0x21, LogoMode::Off)
                .request()
                .clone(),
            "03/02:010400",
        ),
    ] {
        assert_eq!(notation(&report), expected);
        assert_eq!(report.encode()[1], 0x21, "{expected}");
    }
}

/// Managed `PercentToRaw` is `Math.Ceiling(percent * 255 / 100d)` for 1-100
/// and 0 for 0; above 100 it throws.
#[test]
fn percent_to_raw_is_the_managed_ceiling() {
    for percent in 0..=100u8 {
        let expected = if percent == 0 {
            0
        } else {
            libm_ceil(f64::from(percent) * 255.0 / 100.0) as u8
        };
        assert_eq!(percent_to_raw(percent), Some(expected), "{percent}");
    }
    for percent in 101..=255u8 {
        assert_eq!(percent_to_raw(percent), None, "{percent}");
    }
}

/// `f64::ceil` needs std; the values here are small and positive.
fn libm_ceil(value: f64) -> f64 {
    let truncated = value as u64 as f64;
    if truncated < value {
        truncated + 1.0
    } else {
        truncated
    }
}

/// Only `01 05 raw` decodes as a brightness, and only `01 04 00|01` as a
/// logo state; the effect getter takes any effect byte after `01 04`.
#[test]
fn queries_decode_only_the_reviewed_payloads() {
    let brightness = BrightnessQuery::new(7);
    let state = LogoStateQuery::new(7);
    let effect = LogoEffectQuery::new(7);
    for first in [0u8, 1, 2] {
        for second in [3u8, 4, 5] {
            for value in [0u8, 1, 2, 0xc5, 0xff] {
                let body = [first, second, value];
                assert_eq!(
                    brightness.decode(&reply(brightness.request(), &body)),
                    if (first, second) == (1, 5) {
                        Ok(value)
                    } else {
                        Err(ProtocolValidationFailure::PayloadValidator)
                    },
                    "{body:?}"
                );
                assert_eq!(
                    state.decode(&reply(state.request(), &body)),
                    match (first, second, value) {
                        (1, 4, 0) => Ok(false),
                        (1, 4, 1) => Ok(true),
                        _ => Err(ProtocolValidationFailure::PayloadValidator),
                    },
                    "{body:?}"
                );
                assert_eq!(
                    effect.decode(&reply(effect.request(), &body)),
                    if (first, second) == (1, 4) {
                        Ok(value)
                    } else {
                        Err(ProtocolValidationFailure::PayloadValidator)
                    },
                    "{body:?}"
                );
            }
        }
    }
    for length in [0usize, 2, 4, 80] {
        let body = [1u8; 80];
        let response = reply(brightness.request(), &body[..length]);
        assert_eq!(
            brightness.decode(&response),
            Err(ProtocolValidationFailure::PayloadLength)
        );
    }
}

/// Managed `TryDecodeMode`.
#[test]
fn logo_mode_decodes_as_managed() {
    for effect in 0..=255u8 {
        assert_eq!(LogoMode::decode(false, effect), Some(LogoMode::Off));
        assert_eq!(
            LogoMode::decode(true, effect),
            match effect {
                0 => Some(LogoMode::Static),
                2 => Some(LogoMode::Breathing),
                _ => None,
            }
        );
    }
}

/// An acknowledgement must correlate and echo the arguments exactly; the
/// envelope faults keep the shared managed precedence.
#[test]
fn setters_acknowledge_only_an_exact_echo() {
    let set = LightingSet::brightness(5, 0);
    assert_eq!(set.acknowledge(&reply(set.request(), &[1, 5, 0])), Ok(()));
    for body in [[1, 5, 1], [1, 4, 0], [0, 5, 0]] {
        assert_eq!(
            set.acknowledge(&reply(set.request(), &body)),
            Err(ProtocolValidationFailure::PayloadValidator)
        );
    }
    let logo = LightingSet::logo_state(5, LogoMode::Off);
    assert_eq!(logo.acknowledge(&reply(logo.request(), &[1, 4, 0])), Ok(()));
    assert_eq!(
        logo.acknowledge(&reply(logo.request(), &[1, 4, 1])),
        Err(ProtocolValidationFailure::PayloadValidator)
    );
    let mut faults = Vec::new();
    for edit in [1usize, 6, 7, 0, 88] {
        let mut response = reply(set.request(), &[1, 5, 0]);
        response[edit] ^= if edit == 0 { 1 } else { 0x40 };
        if edit != 88 {
            response[88] = calculate_checksum(&response).unwrap();
        }
        faults.push(set.acknowledge(&response));
    }
    assert_eq!(
        faults,
        [
            Err(ProtocolValidationFailure::TransactionMismatch),
            Err(ProtocolValidationFailure::CommandClassMismatch),
            Err(ProtocolValidationFailure::CommandIdMismatch),
            Err(ProtocolValidationFailure::Status),
            Err(ProtocolValidationFailure::Checksum),
        ]
    );
    assert_eq!(
        set.acknowledge(&reply(set.request(), &[1, 5, 0])[..REPORT_LENGTH - 1]),
        Err(ProtocolValidationFailure::EnvelopeLength)
    );
}

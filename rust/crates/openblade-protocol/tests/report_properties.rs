//! Deterministic boundary/property corpus: software envelope checks only.

use openblade_protocol::{
    MAXIMUM_PAYLOAD_LENGTH, REPORT_LENGTH, RazerReport, calculate_checksum, encode_report,
};
use openblade_types::{ProtocolValidationFailure as Failure, ReportEncodingError};

fn request() -> RazerReport {
    RazerReport::create(0x11, 0x22, 0x33, &[]).unwrap()
}

// Test construction only. Expected checksum coverage is checked independently below.
fn repair_checksum(bytes: &mut [u8]) {
    bytes[88] = bytes[2..88].iter().copied().reduce(|a, b| a ^ b).unwrap();
}

fn successful_response() -> [u8; REPORT_LENGTH] {
    let mut bytes = [0; REPORT_LENGTH];
    bytes[0] = 2;
    bytes[1] = 0x11;
    bytes[6] = 0x22;
    bytes[7] = 0x33;
    bytes[88] = 0x11;
    bytes
}

#[test]
fn all_bounded_payload_and_destination_lengths_preserve_failure_output() {
    for payload_length in 0..=256 {
        let payload = vec![0xD3; payload_length];
        let instance = RazerReport::create(0x11, 0x22, 0x33, &payload);
        if payload_length > MAXIMUM_PAYLOAD_LENGTH {
            assert_eq!(
                instance,
                Err(ReportEncodingError::PayloadTooLarge {
                    actual: payload_length
                })
            );
        } else {
            assert!(instance.is_ok());
        }
        for destination_length in 0..=181 {
            let mut destination = vec![0xA5; destination_length];
            let actual = encode_report(0x11, 0x22, 0x33, &payload, &mut destination);
            if payload_length > MAXIMUM_PAYLOAD_LENGTH {
                assert_eq!(
                    actual,
                    Err(ReportEncodingError::PayloadTooLarge {
                        actual: payload_length
                    })
                );
                assert!(destination.iter().all(|&value| value == 0xA5));
            } else if destination_length != REPORT_LENGTH {
                assert_eq!(
                    actual,
                    Err(ReportEncodingError::DestinationLength {
                        actual: destination_length
                    })
                );
                assert!(destination.iter().all(|&value| value == 0xA5));
                assert_eq!(
                    instance.as_ref().unwrap().encode_into(&mut destination),
                    actual
                );
                assert!(destination.iter().all(|&value| value == 0xA5));
            } else {
                assert_eq!(actual, Ok(()));
                assert_eq!(
                    &destination[..8],
                    &[0, 0x11, 0, 0, 0, payload_length as u8, 0x22, 0x33]
                );
                assert_eq!(&destination[8..8 + payload_length], payload.as_slice());
                assert!(
                    destination[8 + payload_length..88]
                        .iter()
                        .all(|&value| value == 0)
                );
                assert_eq!(destination[89], 0);
                assert_eq!(destination.as_slice(), instance.as_ref().unwrap().encode());
            }
        }
    }
}

#[test]
fn checksum_covers_exactly_offsets_two_through_eighty_seven() {
    for offset in 0..REPORT_LENGTH {
        for bit in 0..8 {
            let mut bytes = [0; REPORT_LENGTH];
            bytes[offset] = 1 << bit;
            let expected = if (2..=87).contains(&offset) {
                1 << bit
            } else {
                0
            };
            assert_eq!(
                calculate_checksum(&bytes),
                Ok(expected),
                "offset {offset}, bit {bit}"
            );
        }
    }
    for length in (0..=512).filter(|&length| length != REPORT_LENGTH) {
        assert_eq!(
            calculate_checksum(&vec![0; length]),
            Err(Failure::EnvelopeLength)
        );
        assert_eq!(
            request().validate_response(&vec![0; length], None),
            Err(Failure::EnvelopeLength)
        );
    }
}

#[test]
fn created_request_and_successful_response_own_their_payloads() {
    for length in 0..=MAXIMUM_PAYLOAD_LENGTH {
        let mut source: Vec<u8> = (0..length).map(|value| value as u8).collect();
        let report = RazerReport::create(0x11, 0x22, 0x33, &source).unwrap();
        source.fill(0xFF);
        let mut encoded = report.encode();
        assert_eq!(
            &encoded[8..8 + length],
            (0..length).map(|value| value as u8).collect::<Vec<_>>()
        );
        encoded[0] = 2;
        let owned = report
            .validate_response(&encoded, Some(length as i32))
            .unwrap();
        encoded.fill(0xFF);
        assert_eq!(
            owned.as_slice(),
            (0..length).map(|value| value as u8).collect::<Vec<_>>()
        );
        assert_eq!(report.encode()[89], 0);
    }
}

#[test]
fn every_combination_of_response_faults_preserves_validation_precedence() {
    let precedence = [
        Failure::EnvelopeLength,
        Failure::Checksum,
        Failure::TransactionMismatch,
        Failure::CommandClassMismatch,
        Failure::CommandIdMismatch,
        Failure::Status,
        Failure::PayloadLength,
    ];
    for mask in 1_u8..128 {
        let mut response = successful_response().to_vec();
        if mask & 4 != 0 {
            response[1] ^= 1;
        }
        if mask & 8 != 0 {
            response[6] ^= 1;
        }
        if mask & 16 != 0 {
            response[7] ^= 1;
        }
        if mask & 32 != 0 {
            response[0] = 5;
        }
        if mask & 64 != 0 {
            response[5] = 81;
        }
        repair_checksum(&mut response);
        if mask & 2 != 0 {
            response[88] ^= 1;
        }
        if mask & 1 != 0 {
            response.push(0);
        }
        let expected = precedence[mask.trailing_zeros() as usize];
        assert_eq!(
            request().validate_response(&response, None),
            Err(expected),
            "fault mask {mask:07b}"
        );
    }
}

#[test]
fn only_success_status_is_accepted_and_expected_lengths_remain_signed() {
    for status in 0..=u8::MAX {
        let mut response = successful_response();
        response[0] = status;
        let result = request().validate_response(&response, None);
        if status == 2 {
            assert!(result.is_ok());
        } else {
            assert_eq!(result, Err(Failure::Status));
        }
    }
    for declared in 0..=u8::MAX {
        let mut response = successful_response();
        response[5] = declared;
        repair_checksum(&mut response);
        for expected in [
            None,
            Some(-1),
            Some(i32::MIN),
            Some(0),
            Some(80),
            Some(81),
            Some(i32::MAX),
            Some(i32::from(declared)),
        ] {
            let result = request().validate_response(&response, expected);
            if usize::from(declared) <= MAXIMUM_PAYLOAD_LENGTH
                && expected.is_none_or(|value| value == i32::from(declared))
            {
                assert_eq!(result.unwrap().as_slice().len(), usize::from(declared));
            } else {
                assert_eq!(result, Err(Failure::PayloadLength));
            }
        }
    }
}

#[test]
fn reserved_bytes_and_trailing_byte_follow_the_existing_csharp_contract() {
    let mut response = successful_response();
    response[2] = 0xA5;
    response[3] = 0x5A;
    response[4] = 0xFF;
    response[87] = 0x96;
    response[89] = 0x69;
    repair_checksum(&mut response);
    assert_eq!(
        request()
            .validate_response(&response, Some(0))
            .unwrap()
            .as_slice(),
        &[]
    );
    response[89] ^= 0xFF;
    assert!(request().validate_response(&response, Some(0)).is_ok());
}

#[test]
fn deterministic_malformed_corpus_never_panics_and_success_is_bounded_and_copied() {
    let mut state = 0xB1AD_EC0D_3729_58A1_u64;
    let mut next = || {
        state ^= state << 13;
        state ^= state >> 7;
        state ^= state << 17;
        state
    };
    let mut successes_without_expected_length = 0;
    let mut successes_with_expected_length = 0;
    for case in 0..20_000 {
        let length = if case % 2 == 0 {
            REPORT_LENGTH
        } else {
            (next() % 258) as usize
        };
        let mut response: Vec<u8> = (0..length).map(|_| next() as u8).collect();
        let mut expected = match case % 4 {
            0 => None,
            1 => Some(-1),
            2 => Some((next() % 81) as i32),
            _ => Some(i32::MAX),
        };
        // Include coherent success paths among random envelopes instead of only
        // exercising early rejection. Other cases keep arbitrary malformed bytes.
        if case % 8 == 0 {
            response[0] = 2;
            response[1] = 0x11;
            response[5] %= 81;
            response[6] = 0x22;
            response[7] = 0x33;
            repair_checksum(&mut response);
            // Alternate the coherent cases between no contract length and an
            // exact contract length so both successful API paths stay covered.
            expected = (case % 16 == 8).then_some(i32::from(response[5]));
        }
        let saved = response.clone();
        if let Ok(payload) = request().validate_response(&response, expected) {
            if expected.is_some() {
                successes_with_expected_length += 1;
            } else {
                successes_without_expected_length += 1;
            }
            assert_eq!(length, REPORT_LENGTH);
            assert_eq!(saved[0], 2);
            assert_eq!((saved[1], saved[6], saved[7]), (0x11, 0x22, 0x33));
            assert!(payload.as_slice().len() <= MAXIMUM_PAYLOAD_LENGTH);
            assert_eq!(payload.as_slice().len(), usize::from(saved[5]));
            assert!(expected.is_none_or(|value| value == i32::from(saved[5])));
            assert_eq!(payload.as_slice(), &saved[8..8 + usize::from(saved[5])]);
            response.fill(0xFF);
            assert_eq!(payload.as_slice(), &saved[8..8 + usize::from(saved[5])]);
        }
    }
    assert!(successes_without_expected_length >= 1_250);
    assert!(successes_with_expected_length >= 1_250);
}

#[test]
fn diagnostic_numeric_values_are_stable_including_host_owned_failures() {
    assert_eq!(Failure::ReportId as u32, 0);
    assert_eq!(Failure::EnvelopeLength as u32, 1);
    assert_eq!(Failure::Checksum as u32, 2);
    assert_eq!(Failure::TransactionMismatch as u32, 3);
    assert_eq!(Failure::CommandClassMismatch as u32, 4);
    assert_eq!(Failure::CommandIdMismatch as u32, 5);
    assert_eq!(Failure::Status as u32, 6);
    assert_eq!(Failure::PayloadLength as u32, 7);
    assert_eq!(Failure::PayloadValidator as u32, 8);
}

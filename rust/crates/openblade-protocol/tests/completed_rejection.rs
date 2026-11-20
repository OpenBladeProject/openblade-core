//! The 07/8C adapter-query rejection that C# `OrdinaryThermalPowerAdmission`
//! treats as a completed query without a classification.
use openblade_protocol::{
    REPORT_LENGTH, RazerReport, calculate_checksum, has_single_report_geometry,
};

fn query() -> RazerReport {
    RazerReport::create(0x21, 0x07, 0x8c, &[0, 0]).unwrap()
}

fn reply(status: u8, edit: impl FnOnce(&mut [u8; REPORT_LENGTH])) -> [u8; REPORT_LENGTH] {
    let mut response = RazerReport::create(0x21, 0x07, 0x8c, &[0x0f, 0x0f])
        .unwrap()
        .encode();
    response[0] = status;
    edit(&mut response);
    response[88] = calculate_checksum(&response).unwrap();
    response
}

#[test]
fn only_correlated_failed_or_not_supported_single_reports_complete() {
    let q = query();
    for status in [3, 5] {
        assert!(q.is_completed_rejection(&reply(status, |_| {})));
        // The rejection body is ignored, including an empty one.
        assert!(q.is_completed_rejection(&reply(status, |r| r[5] = 0)));
    }
    // Success is not a rejection, and new, busy or timeout never completed.
    for status in [0, 1, 2, 4, 6, 0xff] {
        assert!(
            !q.is_completed_rejection(&reply(status, |_| {})),
            "{status}"
        );
    }
}

#[test]
fn correlation_and_geometry_failures_are_not_completed() {
    let q = query();
    for edit in [
        (|r: &mut [u8; REPORT_LENGTH]| r[1] = 0x22) as fn(&mut [u8; REPORT_LENGTH]),
        |r| r[6] = 0x0d,
        |r| r[7] = 0x8d,
        |r| r[2] = 1,
        |r| r[3] = 1,
        |r| r[4] = 1,
        |r| r[5] = 81,
        |r| r[89] = 1,
    ] {
        assert!(!q.is_completed_rejection(&reply(3, edit)));
    }
    let mut corrupt = reply(3, |_| {});
    corrupt[88] ^= 1;
    assert!(!q.is_completed_rejection(&corrupt));
    assert!(!q.is_completed_rejection(&corrupt[..REPORT_LENGTH - 1]));
}

#[test]
fn single_report_geometry_is_shared_by_success_and_rejection() {
    // Managed requires the same geometry of a success reply it maps to
    // unclassified power (`WindowsRazerHidSession`) as of a rejection.
    for status in [2, 3, 5] {
        assert!(has_single_report_geometry(&reply(status, |_| {})));
        for edit in [
            (|r: &mut [u8; REPORT_LENGTH]| r[2] = 1) as fn(&mut [u8; REPORT_LENGTH]),
            |r| r[3] = 1,
            |r| r[4] = 1,
            |r| r[5] = 81,
            |r| r[89] = 1,
        ] {
            assert!(!has_single_report_geometry(&reply(status, edit)));
        }
    }
    assert!(!has_single_report_geometry(
        &reply(2, |_| {})[..REPORT_LENGTH - 1]
    ));
}

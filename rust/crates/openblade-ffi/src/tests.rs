use super::*;
use std::mem::{align_of, offset_of, size_of};
use std::ptr::{null, null_mut};
fn request() -> ObReportRequestV1 {
    ObReportRequestV1 {
        struct_size: 8,
        transaction_id: 1,
        command_class: 2,
        command_id: 3,
        reserved: 0,
    }
}
fn options() -> ObValidationOptionsV1 {
    ObValidationOptionsV1 {
        struct_size: 12,
        expected_payload_length: 0,
        has_expected_payload_length: 0,
        reserved: [0; 3],
    }
}
fn error() -> ObErrorV1 {
    ObErrorV1 {
        struct_size: 16,
        category: 99,
        detail: 99,
        required_length: 99,
    }
}
fn response(payload: &[u8]) -> [u8; 90] {
    let mut data = RazerReport::create(1, 2, 3, payload).unwrap().encode();
    data[0] = 2;
    data
}
#[test]
fn layouts() {
    assert_eq!(
        (
            size_of::<ObReportRequestV1>(),
            align_of::<ObReportRequestV1>()
        ),
        (8, 4)
    );
    assert_eq!(
        (
            size_of::<ObValidationOptionsV1>(),
            align_of::<ObValidationOptionsV1>()
        ),
        (12, 4)
    );
    assert_eq!((size_of::<ObErrorV1>(), align_of::<ObErrorV1>()), (16, 4));
    assert_eq!(
        [
            offset_of!(ObReportRequestV1, struct_size),
            offset_of!(ObReportRequestV1, transaction_id),
            offset_of!(ObReportRequestV1, command_class),
            offset_of!(ObReportRequestV1, command_id),
            offset_of!(ObReportRequestV1, reserved)
        ],
        [0, 4, 5, 6, 7]
    );
    assert_eq!(
        [
            offset_of!(ObValidationOptionsV1, struct_size),
            offset_of!(ObValidationOptionsV1, expected_payload_length),
            offset_of!(ObValidationOptionsV1, has_expected_payload_length),
            offset_of!(ObValidationOptionsV1, reserved)
        ],
        [0, 4, 8, 9]
    );
    assert_eq!(
        [
            offset_of!(ObErrorV1, struct_size),
            offset_of!(ObErrorV1, category),
            offset_of!(ObErrorV1, detail),
            offset_of!(ObErrorV1, required_length)
        ],
        [0, 4, 8, 12]
    );
    assert_eq!(ob_abi_version(), 0x10000);
}
#[test]
fn encode_buffers_and_arguments() {
    let mut req = request();
    let mut err = error();
    let mut written = 77;
    // SAFETY: nonnull pointers reference live stack slots and disjoint ranges; nulls test admission.
    unsafe {
        assert_eq!(
            ob_v1_encode_report(
                OB_ABI_VERSION,
                &req,
                null(),
                0,
                null_mut(),
                0,
                &mut written,
                &mut err
            ),
            3
        );
        assert_eq!((written, err.required_length), (90, 90));
        for capacity in [0, 1, 89, 90, 91] {
            let mut output = [0xA5; 94];
            let status = ob_v1_encode_report(
                OB_ABI_VERSION,
                &req,
                null(),
                0,
                output.as_mut_ptr().add(2),
                capacity,
                &mut written,
                &mut err,
            );
            if capacity < 90 {
                assert_eq!(status, 3);
                assert_eq!(output, [0xA5; 94]);
            } else {
                assert_eq!(status, 0);
                assert_eq!(
                    &output[2..92],
                    RazerReport::create(1, 2, 3, &[]).unwrap().encode()
                );
                assert_eq!(&output[..2], &[0xA5; 2]);
                assert_eq!(&output[92..], &[0xA5; 2]);
                assert_eq!((err.detail, err.required_length), (0, 0));
            }
        }
        for (request, payload, len, output, capacity, slot, detail) in [
            (
                null(),
                null(),
                0,
                null_mut(),
                0,
                &mut written as *mut u32,
                5,
            ),
            (&req, null(), 1, null_mut(), 0, &mut written, 5),
            (&req, null(), 81, null_mut(), 0, &mut written, 1),
            (&req, null(), 0, null_mut(), 1, &mut written, 5),
            (&req, null(), 0, null_mut(), 0, null_mut(), 5),
        ] {
            assert_eq!(
                ob_v1_encode_report(
                    OB_ABI_VERSION,
                    request,
                    payload,
                    len,
                    output,
                    capacity,
                    slot,
                    &mut err
                ),
                2
            );
            assert_eq!(err.detail, detail);
        }
        for size in [0, 4, 7, 9, u32::MAX] {
            req.struct_size = size;
            assert_eq!(
                ob_v1_encode_report(
                    OB_ABI_VERSION,
                    &req,
                    null(),
                    0,
                    null_mut(),
                    0,
                    &mut written,
                    &mut err
                ),
                2
            );
            assert_eq!(err.detail, 6);
        }
        req.struct_size = 8;
        req.reserved = 1;
        assert_eq!(
            ob_v1_encode_report(
                OB_ABI_VERSION,
                &req,
                null(),
                0,
                null_mut(),
                0,
                &mut written,
                &mut err
            ),
            2
        );
        assert_eq!(err.detail, 3);
    }
}
#[test]
fn version_and_error_record_precedence() {
    let mut err = error();
    err.struct_size = 15;
    let saved = err;
    let mut written = 77;
    // SAFETY: only output slots are read; mismatched version prevents operation input access.
    unsafe {
        assert_eq!(
            ob_v1_encode_report(
                0,
                null(),
                null(),
                u32::MAX,
                null_mut(),
                u32::MAX,
                &mut written,
                &mut err
            ),
            2
        );
        assert_eq!(err, saved);
        assert_eq!(written, 0);
        err.struct_size = 16;
        assert_eq!(
            ob_v1_encode_report(
                0,
                null(),
                null(),
                u32::MAX,
                null_mut(),
                u32::MAX,
                &mut written,
                &mut err
            ),
            1
        );
        assert_eq!(
            ob_v1_validate_response(
                0,
                null(),
                null(),
                u32::MAX,
                null(),
                u32::MAX,
                null(),
                null_mut(),
                0,
                &mut written,
                &mut err
            ),
            1
        );
    }
}
#[test]
fn response_options_protocol_and_buffers() {
    let req = request();
    let mut opts = options();
    let mut err = error();
    let mut written = 77;
    let mut output = [0xA5; 84];
    let valid = response(&[5, 6]);
    // SAFETY: live immutable input records and disjoint mutable output buffers.
    unsafe {
        macro_rules! call {
            ($opts:expr,$data:expr,$capacity:expr) => {{
                let data: &[u8] = $data;
                ob_v1_validate_response(
                    OB_ABI_VERSION,
                    &req,
                    null(),
                    0,
                    data.as_ptr(),
                    data.len() as u32,
                    $opts,
                    output.as_mut_ptr().add(2),
                    $capacity,
                    &mut written,
                    &mut err,
                )
            }};
        }
        assert_eq!(call!(null(), &valid, 80), 2);
        assert_eq!(err.detail, 5);
        for size in [0, 11, 13, u32::MAX] {
            opts.struct_size = size;
            assert_eq!(call!(&opts, &valid, 80), 2);
            assert_eq!(err.detail, 6);
        }
        opts.struct_size = 12;
        for flag in [2, 255] {
            opts.has_expected_payload_length = flag;
            assert_eq!(call!(&opts, &valid, 80), 2);
            assert_eq!(err.detail, 3);
        }
        opts.has_expected_payload_length = 0;
        for index in 0..3 {
            opts.reserved[index] = 1;
            assert_eq!(call!(&opts, &valid, 80), 2);
            opts.reserved[index] = 0;
        }
        opts.expected_payload_length = -1;
        assert_eq!(call!(&opts, &valid, 1), 3);
        assert_eq!(written, 2);
        assert_eq!(output, [0xA5; 84]);
        opts.has_expected_payload_length = 1;
        assert_eq!(call!(&opts, &valid, 80), 4);
        assert_eq!(err.detail, 7);
        assert_eq!(written, 0);
        let mut faults = valid;
        faults[88] ^= 1;
        faults[1] ^= 1;
        assert_eq!(call!(&opts, &faults, 0), 4);
        assert_eq!(err.detail, 2);
        assert_eq!(call!(&opts, &[], 0), 4);
        assert_eq!(err.detail, 1);
        opts.has_expected_payload_length = 0;
        assert_eq!(call!(&opts, &valid, 80), 0);
        assert_eq!(written, 2);
        assert_eq!(&output[2..4], &[5, 6]);
        assert!(output[..2].iter().chain(&output[4..]).all(|x| *x == 0xA5));
        let empty = response(&[]);
        assert_eq!(
            ob_v1_validate_response(
                OB_ABI_VERSION,
                &req,
                null(),
                0,
                empty.as_ptr(),
                90,
                &opts,
                null_mut(),
                0,
                &mut written,
                null_mut()
            ),
            0
        );
        assert_eq!(written, 0);
        assert_eq!(
            ob_v1_validate_response(
                OB_ABI_VERSION,
                &req,
                null(),
                0,
                null(),
                90,
                &opts,
                null_mut(),
                0,
                &mut written,
                &mut err
            ),
            2
        );
        assert_eq!(err.detail, 5);
    }
}
#[test]
fn both_exports_contain_panics_before_publication() {
    let req = request();
    let opts = options();
    let valid = response(&[7]);
    for validate in [false, true] {
        let mut output = [0xA5; 90];
        let mut written = 77;
        let mut err = error();
        PANIC_NEXT.with(|flag| flag.set(true));
        // SAFETY: live records/buffers, disjoint writable ranges.
        let status = unsafe {
            if validate {
                ob_v1_validate_response(
                    OB_ABI_VERSION,
                    &req,
                    null(),
                    0,
                    valid.as_ptr(),
                    90,
                    &opts,
                    output.as_mut_ptr(),
                    90,
                    &mut written,
                    &mut err,
                )
            } else {
                ob_v1_encode_report(
                    OB_ABI_VERSION,
                    &req,
                    null(),
                    0,
                    output.as_mut_ptr(),
                    90,
                    &mut written,
                    &mut err,
                )
            }
        };
        assert_eq!(
            (
                status,
                written,
                err.category,
                err.detail,
                err.required_length
            ),
            (5, 0, 5, 0, 0)
        );
        assert_eq!(output, [0xA5; 90]);
    }
}
#[test]
fn concurrent_calls() {
    let threads: Vec<_> = (0..12)
        .map(|id| {
            std::thread::spawn(move || {
                for length in 0..=80 {
                    let mut req = request();
                    req.transaction_id = id;
                    let payload = [id; 80];
                    let mut output = [0; 90];
                    let mut written = 0;
                    // SAFETY: each thread owns disjoint live buffers.
                    unsafe {
                        assert_eq!(
                            ob_v1_encode_report(
                                OB_ABI_VERSION,
                                &req,
                                payload.as_ptr(),
                                length,
                                output.as_mut_ptr(),
                                90,
                                &mut written,
                                null_mut()
                            ),
                            0
                        );
                    }
                    assert_eq!(written, 90);
                    assert_eq!(output[1], id);
                    assert_eq!(&output[8..8 + length as usize], &payload[..length as usize]);
                }
            })
        })
        .collect();
    for thread in threads {
        thread.join().unwrap();
    }
}

#[test]
fn payload_bounds_and_failed_output_integrity() {
    let req = request();
    for length in 0..=100 {
        let payload = [0xD3; 100];
        let mut output = [0xA5; 94];
        let mut written = 77;
        let mut err = error();
        // SAFETY: the declared input range fits payload; outputs are disjoint and bounded.
        let status = unsafe {
            ob_v1_encode_report(
                OB_ABI_VERSION,
                &req,
                payload.as_ptr(),
                length,
                output.as_mut_ptr().add(2),
                90,
                &mut written,
                &mut err,
            )
        };
        assert_eq!(&output[..2], &[0xA5; 2]);
        assert_eq!(&output[92..], &[0xA5; 2]);
        if length > 80 {
            assert_eq!((status, err.detail, written), (2, 1, 0));
            assert_eq!(output, [0xA5; 94]);
        } else {
            assert_eq!(status, 0);
            assert_eq!(
                &output[10..10 + length as usize],
                &payload[..length as usize]
            );
        }
    }
}

#[test]
fn every_invalid_presence_flag_and_malformed_response_preserve_output() {
    let req = request();
    let mut opts = options();
    let valid = response(&[7]);
    let mut output = [0xA5; 80];
    let mut written = 77;
    let mut err = error();
    for flag in 2..=255 {
        opts.has_expected_payload_length = flag;
        // SAFETY: live immutable input records and disjoint writable output ranges.
        let status = unsafe {
            ob_v1_validate_response(
                OB_ABI_VERSION,
                &req,
                null(),
                0,
                valid.as_ptr(),
                90,
                &opts,
                output.as_mut_ptr(),
                80,
                &mut written,
                &mut err,
            )
        };
        assert_eq!((status, err.detail, written), (2, 3, 0));
        assert_eq!(output, [0xA5; 80]);
    }
    opts.has_expected_payload_length = 0;
    let mut seed = 0x129A_8E43u32;
    for case in 0..2000 {
        let mut input = [0; 100];
        for byte in &mut input {
            seed ^= seed << 13;
            seed ^= seed >> 17;
            seed ^= seed << 5;
            *byte = seed as u8;
        }
        let length = case % 101;
        // SAFETY: input length is at most 100; writable outputs are separate allocations.
        let status = unsafe {
            ob_v1_validate_response(
                OB_ABI_VERSION,
                &req,
                null(),
                0,
                input.as_ptr(),
                length,
                &opts,
                output.as_mut_ptr(),
                80,
                &mut written,
                &mut err,
            )
        };
        assert_eq!(status, 4);
        assert_eq!(written, 0);
        assert_eq!(output, [0xA5; 80]);
    }
}

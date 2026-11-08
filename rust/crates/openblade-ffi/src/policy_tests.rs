use crate::policy::*;
use crate::{OB_ABI_VERSION, ObErrorV1, PANIC_NEXT, ob_abi_version, ob_v1_encode_report};
use core::mem::{align_of, offset_of, size_of};
use core::ptr::{null, null_mut};

fn scalar(value: i32) -> ObOptionalI32V1 {
    ObOptionalI32V1 {
        value,
        present: 1,
        ..Default::default()
    }
}
fn key(offset: u32, length: u32) -> ObOptionalUtf8V1 {
    ObOptionalUtf8V1 {
        offset,
        length,
        present: 1,
        ..Default::default()
    }
}
fn display() -> ObDisplayProjectionV1 {
    ObDisplayProjectionV1 {
        struct_size: 44,
        ..Default::default()
    }
}
fn profile() -> ObProfileProjectionV1 {
    ObProfileProjectionV1 {
        struct_size: 88,
        performance: scalar(1),
        fans: scalar(0),
        display: display(),
        ..Default::default()
    }
}
fn output() -> ObProfileProjectionV1 {
    ObProfileProjectionV1 {
        struct_size: 88,
        fan_mode: -123,
        fixed_speed_rpm: -456,
        ..Default::default()
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

#[test]
fn policy_layouts_are_pointer_width_independent() {
    assert_eq!(
        (size_of::<ObOptionalI32V1>(), align_of::<ObOptionalI32V1>()),
        (8, 4)
    );
    assert_eq!(
        (
            offset_of!(ObOptionalI32V1, value),
            offset_of!(ObOptionalI32V1, present),
            offset_of!(ObOptionalI32V1, reserved)
        ),
        (0, 4, 5)
    );
    assert_eq!(
        (
            size_of::<ObOptionalUtf8V1>(),
            align_of::<ObOptionalUtf8V1>()
        ),
        (12, 4)
    );
    assert_eq!(
        (
            offset_of!(ObOptionalUtf8V1, offset),
            offset_of!(ObOptionalUtf8V1, length),
            offset_of!(ObOptionalUtf8V1, present),
            offset_of!(ObOptionalUtf8V1, reserved)
        ),
        (0, 4, 8, 9)
    );
    assert_eq!(
        (
            size_of::<ObDisplayProjectionV1>(),
            align_of::<ObDisplayProjectionV1>()
        ),
        (44, 4)
    );
    assert_eq!(
        (
            offset_of!(ObDisplayProjectionV1, struct_size),
            offset_of!(ObDisplayProjectionV1, refresh_rate_key),
            offset_of!(ObDisplayProjectionV1, hdr_enabled),
            offset_of!(ObDisplayProjectionV1, color_profile_key),
            offset_of!(ObDisplayProjectionV1, brightness_percent)
        ),
        (0, 4, 16, 24, 36)
    );
    assert_eq!(
        (
            size_of::<ObProfileProjectionV1>(),
            align_of::<ObProfileProjectionV1>()
        ),
        (88, 4)
    );
    assert_eq!(offset_of!(ObProfileProjectionV1, struct_size), 0);
    assert_eq!(offset_of!(ObProfileProjectionV1, performance), 4);
    assert_eq!(offset_of!(ObProfileProjectionV1, fans), 12);
    assert_eq!(offset_of!(ObProfileProjectionV1, fan_mode), 20);
    assert_eq!(offset_of!(ObProfileProjectionV1, fixed_speed_rpm), 24);
    assert_eq!(offset_of!(ObProfileProjectionV1, has_fan_control), 28);
    assert_eq!(offset_of!(ObProfileProjectionV1, has_display), 29);
    assert_eq!(offset_of!(ObProfileProjectionV1, reserved), 30);
    assert_eq!(offset_of!(ObProfileProjectionV1, gaming_mode_enabled), 32);
    assert_eq!(offset_of!(ObProfileProjectionV1, display), 40);
    assert_eq!(offset_of!(ObProfileProjectionV1, fan_control_source), 84);
}

#[test]
fn utf8_query_and_insufficient_capacity_leave_both_outputs_untouched() {
    let keys = "base色".as_bytes();
    let mut baseline = profile();
    baseline.has_display = 1;
    baseline.display.refresh_rate_key = key(0, 4);
    baseline.display.color_profile_key = key(4, 3);
    for capacity in [0, 6] {
        let mut result = output();
        let sentinel = result;
        let mut bytes = [0xA5; 8];
        let mut written = 99;
        let mut err = error();
        // SAFETY: live disjoint records/buffers, capacity covers its writable range.
        let status = unsafe {
            ob_v1_apply_profile(
                OB_ABI_VERSION,
                &baseline,
                null(),
                keys.as_ptr(),
                7,
                &mut result,
                if capacity == 0 {
                    null_mut()
                } else {
                    bytes.as_mut_ptr()
                },
                capacity,
                &mut written,
                &mut err,
            )
        };
        assert_eq!(
            (
                status,
                written,
                err.category,
                err.detail,
                err.required_length
            ),
            (3, 7, 3, 0, 7)
        );
        assert_eq!(result, sentinel);
        assert_eq!(bytes, [0xA5; 8]);
    }
    let mut result = output();
    let mut bytes = [0xA5; 8];
    let mut written = 99;
    let mut err = error();
    // SAFETY: all records and buffers live and disjoint.
    let status = unsafe {
        ob_v1_apply_profile(
            OB_ABI_VERSION,
            &baseline,
            null(),
            keys.as_ptr(),
            7,
            &mut result,
            bytes.as_mut_ptr(),
            8,
            &mut written,
            &mut err,
        )
    };
    assert_eq!(
        (
            status,
            written,
            err.category,
            err.detail,
            err.required_length
        ),
        (0, 7, 0, 0, 0)
    );
    assert_eq!(&bytes[..7], keys);
    assert_eq!(bytes[7], 0xA5);
    assert_eq!(
        (
            result.display.refresh_rate_key.offset,
            result.display.color_profile_key.offset
        ),
        (0, 4)
    );
}

#[test]
fn malformed_policy_inputs_fail_before_publication() {
    let invalid_utf8 = [0xFF];
    let mut cases = Vec::new();
    let mut p = profile();
    p.performance.present = 2;
    cases.push((p, 3));
    let mut p = profile();
    p.performance.reserved[1] = 1;
    cases.push((p, 3));
    let mut p = profile();
    p.has_fan_control = 2;
    cases.push((p, 3));
    let mut p = profile();
    p.has_display = 2;
    cases.push((p, 3));
    let mut p = profile();
    p.reserved[0] = 1;
    cases.push((p, 3));
    let mut p = profile();
    p.fan_control_source = 1;
    cases.push((p, 3));
    let mut p = profile();
    p.gaming_mode_enabled = scalar(2);
    cases.push((p, 3));
    let mut p = profile();
    p.performance.present = 0;
    cases.push((p, 5));
    let mut p = profile();
    p.fans.present = 0;
    cases.push((p, 5));
    let mut p = profile();
    p.struct_size = 87;
    cases.push((p, 6));
    let mut p = profile();
    p.has_display = 1;
    p.display.struct_size = 43;
    cases.push((p, 6));
    let mut p = profile();
    p.has_display = 1;
    p.display.hdr_enabled = scalar(-1);
    cases.push((p, 3));
    let mut p = profile();
    p.has_display = 1;
    p.display.refresh_rate_key = key(0, 1);
    cases.push((p, 4));
    let mut p = profile();
    p.has_display = 1;
    p.display.refresh_rate_key = key(1, 1);
    cases.push((p, 2));
    let mut p = profile();
    p.has_display = 1;
    p.display.refresh_rate_key = key(u32::MAX, 1);
    cases.push((p, 2));
    let mut p = profile();
    p.has_display = 1;
    p.display.refresh_rate_key.present = 2;
    cases.push((p, 3));
    let mut p = profile();
    p.has_display = 1;
    p.display.color_profile_key.reserved[2] = 1;
    cases.push((p, 3));
    for (baseline, detail) in cases {
        let mut result = output();
        let sentinel = result;
        let mut bytes = [0xA5; 8];
        let mut written = 99;
        let mut err = error();
        // SAFETY: even rejected records have accessible size prefixes; full records and buffers live/disjoint.
        let status = unsafe {
            ob_v1_apply_profile(
                OB_ABI_VERSION,
                &baseline,
                null(),
                invalid_utf8.as_ptr(),
                1,
                &mut result,
                bytes.as_mut_ptr(),
                8,
                &mut written,
                &mut err,
            )
        };
        assert_eq!(
            (
                status,
                written,
                err.category,
                err.detail,
                err.required_length
            ),
            (2, 0, 2, detail, 0)
        );
        assert_eq!(result, sentinel);
        assert_eq!(bytes, [0xA5; 8]);
    }
}

#[test]
fn empty_absent_false_zero_and_dormant_payloads_are_distinct() {
    let mut baseline = display();
    baseline.refresh_rate_key = key(0, 1);
    baseline.hdr_enabled = scalar(1);
    baseline.brightness_percent = scalar(70);
    let mut value = display();
    value.refresh_rate_key = key(0, 0);
    value.color_profile_key.offset = u32::MAX;
    value.color_profile_key.length = u32::MAX;
    value.hdr_enabled = scalar(0);
    value.brightness_percent = scalar(0);
    let mut result = display();
    let mut written = 99;
    let mut err = error();
    // SAFETY: valid records and one readable key byte; no output storage needed for empty result.
    let status = unsafe {
        ob_v1_apply_display(
            OB_ABI_VERSION,
            &baseline,
            &value,
            b"x".as_ptr(),
            1,
            &mut result,
            null_mut(),
            0,
            &mut written,
            &mut err,
        )
    };
    assert_eq!((status, written), (0, 0));
    assert_eq!(
        (
            result.refresh_rate_key.present,
            result.refresh_rate_key.length,
            result.color_profile_key.present
        ),
        (1, 0, 0)
    );
    assert_eq!(result.hdr_enabled, scalar(0));
    assert_eq!(result.brightness_percent, scalar(0));
    // Absent nested objects are not interpreted, even with stale nested size/payload.
    let mut baseline = profile();
    baseline.display.struct_size = 1;
    baseline.fan_mode = i32::MIN;
    baseline.fixed_speed_rpm = i32::MAX;
    let mut result = output();
    // SAFETY: all declared record ranges remain live and disjoint.
    let status = unsafe {
        ob_v1_apply_profile(
            OB_ABI_VERSION,
            &baseline,
            null(),
            null(),
            0,
            &mut result,
            null_mut(),
            0,
            &mut written,
            &mut err,
        )
    };
    assert_eq!(
        (status, result.has_display, result.has_fan_control),
        (0, 0, 0)
    );
}

#[test]
fn legacy_synthesis_is_the_only_scalar_enum_validation_in_merge() {
    let mut baseline = profile();
    baseline.performance = scalar(999);
    baseline.fans = scalar(-999);
    baseline.has_display = 1;
    baseline.display.brightness_percent = scalar(255);
    let mut value = ObProfileProjectionV1 {
        struct_size: 88,
        ..Default::default()
    };
    let mut result = output();
    let mut written = 99;
    let mut err = error();
    // SAFETY: disjoint live records; empty keys and outputs.
    unsafe {
        assert_eq!(
            ob_v1_apply_profile(
                OB_ABI_VERSION,
                &baseline,
                &value,
                null(),
                0,
                &mut result,
                null_mut(),
                0,
                &mut written,
                &mut err
            ),
            0
        );
        assert_eq!(
            (
                result.performance.value,
                result.fans.value,
                result.display.brightness_percent.value
            ),
            (999, -999, 255)
        );
        value.fans = scalar(999);
        let sentinel = result;
        assert_eq!(
            ob_v1_apply_profile(
                OB_ABI_VERSION,
                &baseline,
                &value,
                null(),
                0,
                &mut result,
                null_mut(),
                0,
                &mut written,
                &mut err
            ),
            2
        );
        assert_eq!((err.detail, written), (3, 0));
        assert_eq!(result, sentinel);
        value.has_fan_control = 1;
        value.fan_mode = -123;
        value.fixed_speed_rpm = -456;
        assert_eq!(
            ob_v1_apply_profile(
                OB_ABI_VERSION,
                &baseline,
                &value,
                null(),
                0,
                &mut result,
                null_mut(),
                0,
                &mut written,
                &mut err
            ),
            0
        );
    }
    assert_eq!(
        (
            result.fans.value,
            result.fan_mode,
            result.fixed_speed_rpm,
            result.fan_control_source
        ),
        (999, -123, -456, 2)
    );
}

#[test]
fn policy_admission_precedence_and_required_pointers() {
    let baseline = profile();
    let mut result = output();
    let sentinel = result;
    let mut written = 99;
    let mut err = error();
    // SAFETY: nulls are intentional admission cases, all nonnull pointers live/disjoint.
    unsafe {
        err.struct_size = 15;
        assert_eq!(
            ob_v1_apply_profile(
                999,
                null(),
                null(),
                null(),
                0,
                &mut result,
                null_mut(),
                0,
                &mut written,
                &mut err
            ),
            2
        );
        assert_eq!((err.category, err.detail, written), (99, 99, 0));
        err = error();
        assert_eq!(
            ob_v1_apply_profile(
                999,
                null(),
                null(),
                null(),
                0,
                &mut result,
                null_mut(),
                0,
                &mut written,
                &mut err
            ),
            1
        );
        assert_eq!((err.detail, written), (0, 0));
        assert_eq!(
            ob_v1_apply_profile(
                OB_ABI_VERSION,
                null(),
                null(),
                null(),
                0,
                &mut result,
                null_mut(),
                0,
                &mut written,
                &mut err
            ),
            2
        );
        assert_eq!(err.detail, 5);
        result.struct_size = 87;
        assert_eq!(
            ob_v1_apply_profile(
                OB_ABI_VERSION,
                null(),
                null(),
                null(),
                0,
                &mut result,
                null_mut(),
                0,
                &mut written,
                &mut err
            ),
            2
        );
        assert_eq!(err.detail, 6);
        result = sentinel;
        assert_eq!(
            ob_v1_apply_profile(
                OB_ABI_VERSION,
                &baseline,
                null(),
                null(),
                0,
                &mut result,
                null_mut(),
                0,
                null_mut(),
                &mut err
            ),
            2
        );
        assert_eq!(err.detail, 5);
        assert_eq!(
            ob_v1_apply_profile(
                OB_ABI_VERSION,
                &baseline,
                null(),
                null(),
                0,
                null_mut(),
                null_mut(),
                0,
                &mut written,
                &mut err
            ),
            2
        );
        assert_eq!(err.detail, 5);
        assert_eq!(
            ob_v1_apply_profile(
                OB_ABI_VERSION,
                &baseline,
                null(),
                null(),
                1,
                &mut result,
                null_mut(),
                0,
                &mut written,
                &mut err
            ),
            2
        );
        assert_eq!(err.detail, 5);
        assert_eq!(
            ob_v1_apply_profile(
                OB_ABI_VERSION,
                &baseline,
                null(),
                null(),
                0,
                &mut result,
                null_mut(),
                1,
                &mut written,
                &mut err
            ),
            2
        );
        assert_eq!(err.detail, 5);
        assert_eq!(
            ob_v1_apply_profile(
                OB_ABI_VERSION,
                &baseline,
                null(),
                null(),
                0,
                &mut result,
                null_mut(),
                0,
                &mut written,
                null_mut()
            ),
            0
        );
    }
}

#[test]
fn both_policy_exports_contain_panics_before_record_or_byte_publication() {
    let baseline = profile();
    for (panic_kind, display_call) in [(1, false), (2, false), (1, true), (2, true)] {
        let mut result = output();
        let sentinel = result;
        let mut display_result = display();
        let display_sentinel = display_result;
        let mut bytes = [0xA5; 8];
        let mut written = 99;
        let mut err = error();
        PANIC_NEXT.with(|flag| flag.set(panic_kind));
        // SAFETY: all records/buffers live and disjoint; private injection precedes publication.
        let status = unsafe {
            if display_call {
                ob_v1_apply_display(
                    OB_ABI_VERSION,
                    &baseline.display,
                    null(),
                    null(),
                    0,
                    &mut display_result,
                    bytes.as_mut_ptr(),
                    8,
                    &mut written,
                    &mut err,
                )
            } else {
                ob_v1_apply_profile(
                    OB_ABI_VERSION,
                    &baseline,
                    null(),
                    null(),
                    0,
                    &mut result,
                    bytes.as_mut_ptr(),
                    8,
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
        assert_eq!(result, sentinel);
        assert_eq!(display_result, display_sentinel);
        assert_eq!(bytes, [0xA5; 8]);
    }
}

#[test]
fn independent_policy_calls_share_no_state_and_codec_v1_still_works() {
    assert_eq!(ob_policy_abi_version(), OB_ABI_VERSION);
    assert_eq!(ob_abi_version(), OB_ABI_VERSION);
    let threads: Vec<_> = (0..8)
        .map(|value| {
            std::thread::spawn(move || {
                let mut baseline = profile();
                baseline.performance = scalar(value);
                let mut result = output();
                let mut written = 99;
                let mut err = error();
                for _ in 0..32 {
                    // SAFETY: each thread owns every writable range and its immutable baseline.
                    let status = unsafe {
                        ob_v1_apply_profile(
                            OB_ABI_VERSION,
                            &baseline,
                            null(),
                            null(),
                            0,
                            &mut result,
                            null_mut(),
                            0,
                            &mut written,
                            &mut err,
                        )
                    };
                    assert_eq!((status, written, result.performance.value), (0, 0, value));
                }
            })
        })
        .collect();
    for thread in threads {
        thread.join().unwrap();
    }
    let request = crate::ObReportRequestV1 {
        struct_size: 8,
        transaction_id: 1,
        command_class: 2,
        command_id: 3,
        reserved: 0,
    };
    let mut bytes = [0; 90];
    let mut written = 0;
    let mut err = error();
    // SAFETY: independent live buffers/records preserve the original v1 codec contract.
    let status = unsafe {
        ob_v1_encode_report(
            OB_ABI_VERSION,
            &request,
            null(),
            0,
            bytes.as_mut_ptr(),
            90,
            &mut written,
            &mut err,
        )
    };
    assert_eq!((status, written, err.category), (0, 90, 0));
}

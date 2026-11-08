use crate::automation::*;
use crate::{ObErrorV1, PANIC_NEXT};
use std::{
    mem::{align_of, offset_of, size_of},
    ptr,
};

fn request() -> ObAutomationRequestV1 {
    ObAutomationRequestV1 {
        struct_size: 16,
        operation: 1,
        has_profile: 1,
        has_confirmed: 1,
        has_temporary: 1,
        has_persistent: 1,
        external_unknown: 1,
        ..Default::default()
    }
}
fn result() -> ObAutomationSelectionV1 {
    ObAutomationSelectionV1 {
        struct_size: 12,
        origin: 99,
        provenance: 98,
    }
}
fn error() -> ObErrorV1 {
    ObErrorV1 {
        struct_size: 16,
        category: 99,
        detail: 98,
        required_length: 97,
    }
}
#[test]
fn automation_layout_and_version_are_frozen() {
    assert_eq!(ob_automation_abi_version(), 0x10000);
    assert_eq!(
        (
            size_of::<ObAutomationRequestV1>(),
            align_of::<ObAutomationRequestV1>()
        ),
        (16, 4)
    );
    assert_eq!(
        (
            size_of::<ObAutomationSelectionV1>(),
            align_of::<ObAutomationSelectionV1>()
        ),
        (12, 4)
    );
    assert_eq!(offset_of!(ObAutomationRequestV1, operation), 4);
    assert_eq!(offset_of!(ObAutomationRequestV1, has_profile), 8);
    assert_eq!(offset_of!(ObAutomationRequestV1, has_confirmed), 9);
    assert_eq!(offset_of!(ObAutomationRequestV1, has_temporary), 10);
    assert_eq!(offset_of!(ObAutomationRequestV1, has_persistent), 11);
    assert_eq!(offset_of!(ObAutomationRequestV1, external_unknown), 12);
    assert_eq!(offset_of!(ObAutomationRequestV1, from_application), 13);
    assert_eq!(offset_of!(ObAutomationRequestV1, reserved), 14);
    assert_eq!(offset_of!(ObAutomationSelectionV1, origin), 4);
    assert_eq!(offset_of!(ObAutomationSelectionV1, provenance), 8);
}

#[test]
fn validation_precedes_publication_and_version_precedes_inputs() {
    let mut output = result();
    let sentinel = output;
    let mut written = 999;
    let mut error = error();
    // SAFETY: all supplied nonnull ranges are accessible, aligned, and disjoint.
    unsafe {
        assert_eq!(
            ob_v1_select_automation(2, ptr::null(), &mut output, &mut written, &mut error),
            1
        );
        assert_eq!((output, written, error.detail), (sentinel, 0, 0));
        assert_eq!(
            ob_v1_select_automation(0x10000, ptr::null(), &mut output, &mut written, &mut error),
            2
        );
        assert_eq!((output, error.detail), (sentinel, 5));
        assert_eq!(
            ob_v1_select_automation(
                0x10000,
                &request(),
                ptr::null_mut(),
                &mut written,
                &mut error
            ),
            2
        );
        assert_eq!(error.detail, 5);
        assert_eq!(
            ob_v1_select_automation(
                0x10000,
                &request(),
                &mut output,
                ptr::null_mut(),
                &mut error
            ),
            2
        );
        assert_eq!((output, error.detail), (sentinel, 5));
        let size_prefix = 4_u32;
        assert_eq!(
            ob_v1_select_automation(
                0x10000,
                ptr::from_ref(&size_prefix).cast(),
                &mut output,
                &mut written,
                &mut error
            ),
            2
        );
        assert_eq!((output, error.detail), (sentinel, 6));
        output.struct_size = 4;
        assert_eq!(
            ob_v1_select_automation(0x10000, &request(), &mut output, &mut written, &mut error),
            2
        );
        assert_eq!((output.origin, error.detail), (99, 6));
        error.struct_size = 4;
        let old_error = error;
        assert_eq!(
            ob_v1_select_automation(2, ptr::null(), &mut output, &mut written, &mut error),
            2
        );
        assert_eq!(error, old_error);
    }
}

#[test]
fn every_invalid_flag_reserved_byte_and_operation_is_rejected() {
    for index in 0..9 {
        let mut input = request();
        match index {
            0 => input.has_profile = 2,
            1 => input.has_confirmed = 2,
            2 => input.has_temporary = 2,
            3 => input.has_persistent = 2,
            4 => input.external_unknown = 2,
            5 => input.from_application = 2,
            6 => input.reserved[0] = 1,
            7 => input.reserved[1] = 1,
            _ => input.operation = 2,
        }
        let mut output = result();
        let mut written = 999;
        let mut error = error();
        // SAFETY: owned records and slots are disjoint and fully accessible.
        unsafe {
            assert_eq!(
                ob_v1_select_automation(0x10000, &input, &mut output, &mut written, &mut error),
                2
            );
        }
        assert_eq!((output, written, error.detail), (result(), 0, 3));
    }
}

#[test]
fn successful_selection_has_no_variable_output_and_error_is_optional() {
    let mut output = result();
    let mut written = 999;
    // SAFETY: owned accessible disjoint input/output.
    unsafe {
        assert_eq!(
            ob_v1_select_automation(
                0x10000,
                &request(),
                &mut output,
                &mut written,
                ptr::null_mut()
            ),
            0
        );
    }
    assert_eq!((output.origin, output.provenance, written), (1, 0, 0));
}

#[test]
fn automation_contains_both_panic_payloads_before_publication() {
    for panic in [1, 2] {
        let mut output = result();
        let mut written = 999;
        let mut error = error();
        PANIC_NEXT.with(|flag| flag.set(panic));
        // SAFETY: owned accessible disjoint records; test-only panic stays inside boundary.
        unsafe {
            assert_eq!(
                ob_v1_select_automation(0x10000, &request(), &mut output, &mut written, &mut error),
                5
            );
        }
        assert_eq!(
            (output, written, error.category, error.detail),
            (result(), 0, 5, 0)
        );
    }
}

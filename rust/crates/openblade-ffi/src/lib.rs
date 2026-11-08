//! Stateless C ABI for the pure report codec. No hardware or runtime ownership.
#![deny(unsafe_op_in_unsafe_fn)]

use openblade_protocol::RazerReport;
use std::panic::{AssertUnwindSafe, catch_unwind};
use std::{mem, ptr, slice};

pub const OB_ABI_VERSION: u32 = 0x0001_0000;

#[repr(C)]
#[derive(Clone, Copy, Debug)]
pub struct ObReportRequestV1 {
    pub struct_size: u32,
    pub transaction_id: u8,
    pub command_class: u8,
    pub command_id: u8,
    pub reserved: u8,
}

#[repr(C)]
#[derive(Clone, Copy, Debug)]
pub struct ObValidationOptionsV1 {
    pub struct_size: u32,
    pub expected_payload_length: i32,
    pub has_expected_payload_length: u8,
    pub reserved: [u8; 3],
}

#[repr(C)]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct ObErrorV1 {
    pub struct_size: u32,
    pub category: u32,
    pub detail: u32,
    pub required_length: u32,
}

#[derive(Clone, Copy)]
struct Failure(u32, u32, u32);
const ABSENT: Failure = Failure(2, 5, 0);
const RECORD_SIZE: Failure = Failure(2, 6, 0);
const FLAG: Failure = Failure(2, 3, 0);

#[unsafe(no_mangle)]
pub extern "C" fn ob_abi_version() -> u32 {
    OB_ABI_VERSION
}

// Safety: callers of these private adapters uphold the exported pointer contract.
// Read only the leading size until the complete record's exact size is admitted.
unsafe fn request(pointer: *const ObReportRequestV1) -> Result<ObReportRequestV1, Failure> {
    if pointer.is_null() {
        return Err(ABSENT);
    }
    // SAFETY: nonnull pointer has an accessible, aligned u32 size prefix.
    if unsafe { pointer.cast::<u32>().read() } != mem::size_of::<ObReportRequestV1>() as u32 {
        return Err(RECORD_SIZE);
    }
    // SAFETY: caller supplies the admitted complete record.
    let record = unsafe { pointer.read() };
    if record.reserved != 0 {
        return Err(FLAG);
    }
    Ok(record)
}

unsafe fn options(pointer: *const ObValidationOptionsV1) -> Result<Option<i32>, Failure> {
    if pointer.is_null() {
        return Err(ABSENT);
    }
    // SAFETY: nonnull pointer has an accessible, aligned u32 size prefix.
    if unsafe { pointer.cast::<u32>().read() } != mem::size_of::<ObValidationOptionsV1>() as u32 {
        return Err(RECORD_SIZE);
    }
    // SAFETY: caller supplies the admitted complete record.
    let record = unsafe { pointer.read() };
    if record.has_expected_payload_length > 1 || record.reserved != [0; 3] {
        return Err(FLAG);
    }
    Ok((record.has_expected_payload_length == 1).then_some(record.expected_payload_length))
}

unsafe fn bytes<'a>(pointer: *const u8, length: u32) -> Result<&'a [u8], Failure> {
    if length == 0 {
        return Ok(&[]);
    }
    if pointer.is_null() {
        return Err(ABSENT);
    }
    if u64::from(length) > isize::MAX as u64 {
        return Err(Failure(2, 2, 0));
    }
    // SAFETY: caller provides a valid accessible immutable range for the call.
    Ok(unsafe { slice::from_raw_parts(pointer, length as usize) })
}

unsafe fn make_report(
    pointer: *const ObReportRequestV1,
    payload: *const u8,
    length: u32,
) -> Result<RazerReport, Failure> {
    // SAFETY: forwarded exported pointer contract.
    let request = unsafe { request(pointer)? };
    if length > 80 {
        return Err(Failure(2, 1, 0));
    }
    // SAFETY: forwarded exported pointer contract; bounded before constructing slice.
    let payload = unsafe { bytes(payload, length)? };
    RazerReport::create(
        request.transaction_id,
        request.command_class,
        request.command_id,
        payload,
    )
    .map_err(|_| Failure(2, 1, 0))
}

unsafe fn publish(bytes: &[u8], output: *mut u8, capacity: u32) -> Result<u32, Failure> {
    if output.is_null() && capacity != 0 {
        return Err(ABSENT);
    }
    if capacity < bytes.len() as u32 {
        return Err(Failure(3, 0, bytes.len() as u32));
    }
    #[cfg(test)]
    panic_injection();
    if !bytes.is_empty() {
        // SAFETY: admitted capacity covers bytes; caller guarantees disjoint writable output.
        unsafe { ptr::copy_nonoverlapping(bytes.as_ptr(), output, bytes.len()) };
    }
    Ok(bytes.len() as u32)
}

// All input processing, computation and result publication are inside this boundary.
// Error/length publication uses only checked caller-owned output slots and cannot panic.
unsafe fn boundary(
    version: u32,
    written: *mut u32,
    error: *mut ObErrorV1,
    operation: impl FnOnce() -> Result<u32, Failure>,
) -> u32 {
    let outcome = match catch_unwind(AssertUnwindSafe(|| {
        if !error.is_null() {
            // SAFETY: caller provides an aligned readable size prefix, even for invalid sizes.
            if unsafe { error.cast::<u32>().read() } != mem::size_of::<ObErrorV1>() as u32 {
                return Err(RECORD_SIZE);
            }
        }
        if version != OB_ABI_VERSION {
            return Err(Failure(1, 0, 0));
        }
        if written.is_null() {
            return Err(ABSENT);
        }
        operation()
    })) {
        Ok(result) => result,
        Err(payload) => {
            // A panic payload can itself panic on Drop. Never run arbitrary
            // destructors outside the unwind boundary. Deliberately leak this
            // exceptional payload rather than unwind across C; normal calls leak nothing.
            mem::forget(payload);
            Err(Failure(5, 0, 0))
        }
    };
    let (category, detail, required, length) = match outcome {
        Ok(length) => (0, 0, 0, length),
        Err(Failure(category, detail, required)) => (
            category,
            detail,
            required,
            if category == 3 { required } else { 0 },
        ),
    };
    if !written.is_null() {
        // SAFETY: caller provides a disjoint writable u32 slot.
        unsafe { written.write(length) };
    }
    if !error.is_null() {
        // SAFETY: caller provides an aligned readable prefix. Invalid-size records stay untouched.
        if unsafe { error.cast::<u32>().read() } == mem::size_of::<ObErrorV1>() as u32 {
            // SAFETY: exact-size writable record is guaranteed by caller.
            unsafe {
                error.write(ObErrorV1 {
                    struct_size: 16,
                    category,
                    detail,
                    required_length: required,
                })
            };
        }
    }
    category
}

/// Encode exactly 90 protocol bytes, excluding the Windows report-ID prefix.
///
/// # Safety
/// Nonnull pointers must be aligned, valid and accessible for their declared ranges
/// for the entire call. Writable ranges must be mutually disjoint and disjoint
/// from all input ranges. Immutable inputs may alias or be shared concurrently
/// if no caller mutates them during the calls.
/// Records require a readable u32 size prefix. Only an exact recognized size
/// requires the complete record; rejected sizes read only that prefix.
/// Null slices are accepted only at length zero; null output only at capacity zero.
/// `written_or_required` is required; `error` is optional. No pointer is retained.
#[allow(clippy::too_many_arguments)]
#[unsafe(no_mangle)]
pub unsafe extern "C" fn ob_v1_encode_report(
    abi_version: u32,
    request: *const ObReportRequestV1,
    payload: *const u8,
    payload_length: u32,
    output: *mut u8,
    capacity: u32,
    written_or_required: *mut u32,
    error: *mut ObErrorV1,
) -> u32 {
    // SAFETY: the caller upholds the documented pointer contract for all adapters.
    unsafe {
        boundary(abi_version, written_or_required, error, || {
            let report = make_report(request, payload, payload_length)?;
            publish(&report.encode(), output, capacity)
        })
    }
}

/// Validate a response and copy its payload (at most 80 bytes).
/// `options` is required; its presence flag controls the signed expected length.
///
/// # Safety
/// The same pointer, lifetime, alignment and output disjointness requirements as
/// [`ob_v1_encode_report`] apply, including the response and options ranges.
#[allow(clippy::too_many_arguments)]
#[unsafe(no_mangle)]
pub unsafe extern "C" fn ob_v1_validate_response(
    abi_version: u32,
    request: *const ObReportRequestV1,
    payload: *const u8,
    payload_length: u32,
    response: *const u8,
    response_length: u32,
    options: *const ObValidationOptionsV1,
    output: *mut u8,
    capacity: u32,
    written_or_required: *mut u32,
    error: *mut ObErrorV1,
) -> u32 {
    // SAFETY: the caller upholds the documented pointer contract for all adapters.
    unsafe {
        boundary(abi_version, written_or_required, error, || {
            let report = make_report(request, payload, payload_length)?;
            let expected = self::options(options)?;
            let response = bytes(response, response_length)?;
            let result = report
                .validate_response(response, expected)
                .map_err(|failure| Failure(4, failure as u32, 0))?;
            publish(result.as_slice(), output, capacity)
        })
    }
}

#[cfg(test)]
std::thread_local! { static PANIC_NEXT: std::cell::Cell<u8> = const { std::cell::Cell::new(0) }; }
#[cfg(test)]
fn panic_injection() {
    struct PanickingDrop;
    impl Drop for PanickingDrop {
        fn drop(&mut self) {
            panic!("caught payload destructor must never run across C");
        }
    }
    match PANIC_NEXT.with(|flag| flag.replace(0)) {
        1 => panic!("private boundary containment test"),
        2 => std::panic::panic_any(PanickingDrop),
        _ => {}
    }
}

#[cfg(test)]
mod tests;

// Additive policy surface; existing codec exports and negotiation stay frozen.
pub mod policy;
#[cfg(test)]
mod policy_tests;

//! Independently negotiated automation selection. All values and state remain host-owned.
use crate::{ABSENT, FLAG, ObErrorV1, RECORD_SIZE, boundary_for_version};
use openblade_domain::automation::{self, Provenance, ValueOrigin};
use std::mem;

pub const OB_AUTOMATION_ABI_VERSION: u32 = 0x0001_0000;

#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct ObAutomationRequestV1 {
    pub struct_size: u32,
    pub operation: u32,
    pub has_profile: u8,
    pub has_confirmed: u8,
    pub has_temporary: u8,
    pub has_persistent: u8,
    pub external_unknown: u8,
    pub from_application: u8,
    pub reserved: [u8; 2],
}

#[repr(C)]
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct ObAutomationSelectionV1 {
    pub struct_size: u32,
    pub origin: u32,
    pub provenance: u32,
}

#[unsafe(no_mangle)]
pub extern "C" fn ob_automation_abi_version() -> u32 {
    OB_AUTOMATION_ABI_VERSION
}

/// Select a borrowed host value without receiving or retaining that value.
/// # Safety
/// All nonnull pointers are aligned and accessible for the call. Records need a readable
/// u32 prefix; exact sizes require the complete record. Output ranges are mutually
/// disjoint and disjoint from inputs. written is required; error is optional.
/// No result changes on failure. Success reports zero variable bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn ob_v1_select_automation(
    abi_version: u32,
    request: *const ObAutomationRequestV1,
    result: *mut ObAutomationSelectionV1,
    written: *mut u32,
    error: *mut ObErrorV1,
) -> u32 {
    // SAFETY: forwarded exported pointer contract, including output disjointness.
    unsafe {
        boundary_for_version(
            abi_version,
            OB_AUTOMATION_ABI_VERSION,
            written,
            error,
            || {
                if result.is_null() || request.is_null() {
                    return Err(ABSENT);
                }
                if result.cast::<u32>().read() != mem::size_of::<ObAutomationSelectionV1>() as u32
                    || request.cast::<u32>().read()
                        != mem::size_of::<ObAutomationRequestV1>() as u32
                {
                    return Err(RECORD_SIZE);
                }
                let request = request.read();
                if request.reserved != [0; 2]
                    || [
                        request.has_profile,
                        request.has_confirmed,
                        request.has_temporary,
                        request.has_persistent,
                        request.external_unknown,
                        request.from_application,
                    ]
                    .iter()
                    .any(|flag| *flag > 1)
                {
                    return Err(FLAG);
                }
                let selection = match request.operation {
                    0 => automation::performance(
                        request.has_temporary == 1,
                        request.has_persistent == 1,
                        request.from_application == 1,
                    ),
                    1 => automation::lighting(
                        request.has_profile == 1,
                        request.has_confirmed == 1,
                        request.has_temporary == 1,
                        request.has_persistent == 1,
                        request.external_unknown == 1,
                        request.from_application == 1,
                    ),
                    _ => return Err(FLAG),
                };
                let record = ObAutomationSelectionV1 {
                    struct_size: 12,
                    origin: match selection.origin {
                        ValueOrigin::Profile => 0,
                        ValueOrigin::Confirmed => 1,
                        ValueOrigin::Temporary => 2,
                        ValueOrigin::Persistent => 3,
                    },
                    provenance: match selection.provenance {
                        Provenance::ConfirmedFirmwareState => 0,
                        Provenance::PowerProfile => 1,
                        Provenance::ApplicationProfile => 2,
                        Provenance::TemporaryOverride => 3,
                        Provenance::PersistentOverride => 4,
                    },
                };
                #[cfg(test)]
                crate::panic_injection();
                result.write(record);
                Ok(0)
            },
        )
    }
}

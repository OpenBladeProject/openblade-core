//! Read-only WCS boundary. The SDK owns returned buffers until LocalFree.
#![allow(unsafe_code)]

use std::{
    cmp::Ordering,
    ffi::{OsStr, OsString},
    fs::File,
    io::Read,
    os::windows::ffi::{OsStrExt, OsStringExt},
    path::PathBuf,
    ptr::{null, null_mut},
};

use windows_sys::{
    Win32::{
        Foundation::{ERROR_FILE_NOT_FOUND, LUID, LocalFree},
        Globalization::{
            CSTR_EQUAL, CSTR_GREATER_THAN, CSTR_LESS_THAN, CompareStringEx, CompareStringOrdinal,
            NORM_IGNORECASE,
        },
        System::SystemInformation::GetSystemDirectoryW,
        UI::ColorSystem::{
            CPST_STANDARD_DISPLAY_COLOR_MODE, CPT_ICC, ColorProfileGetDisplayDefault,
            ColorProfileGetDisplayList, ColorProfileGetDisplayUserScope,
            WCS_PROFILE_MANAGEMENT_SCOPE, WCS_PROFILE_MANAGEMENT_SCOPE_CURRENT_USER,
            WCS_PROFILE_MANAGEMENT_SCOPE_SYSTEM_WIDE,
        },
    },
    core::PWSTR,
};

use super::{
    ActiveInternalDisplay, ProfileRead, ProfileReadError, ProfileReads, ProfileScope, present,
    valid_monitor_rgb_header,
};

const FILE_NOT_FOUND: i32 = (0x8007_0000 | ERROR_FILE_NOT_FOUND) as i32;
const MAX_ASSOCIATIONS: usize = 4096;
const MAX_PROFILE_NAME: usize = 32_768;
const PRESETS: [&str; 5] = [
    "Native_CTR.icm",
    "DisplayP3_CTR.icm",
    "sRGB_CTR.icm",
    "BT709_CTR.icm",
    "AdobeRGB_CTR.icm",
];

pub(super) struct WindowsProfiles;

fn display_address(display: &ActiveInternalDisplay) -> (LUID, u32) {
    // WCS takes the target adapter paired with the source ID, even if the
    // source and target adapters differ.
    (
        LUID {
            LowPart: display.target.adapter_low,
            HighPart: display.target.adapter_high,
        },
        display.source.id,
    )
}

fn native_scope(scope: ProfileScope) -> WCS_PROFILE_MANAGEMENT_SCOPE {
    match scope {
        ProfileScope::SystemWide => WCS_PROFILE_MANAGEMENT_SCOPE_SYSTEM_WIDE,
        ProfileScope::CurrentUser => WCS_PROFILE_MANAGEMENT_SCOPE_CURRENT_USER,
    }
}

fn observed_scope(scope: WCS_PROFILE_MANAGEMENT_SCOPE) -> Result<ProfileScope, ProfileReadError> {
    match scope {
        WCS_PROFILE_MANAGEMENT_SCOPE_SYSTEM_WIDE => Ok(ProfileScope::SystemWide),
        WCS_PROFILE_MANAGEMENT_SCOPE_CURRENT_USER => Ok(ProfileScope::CurrentUser),
        _ => Err(ProfileReadError::UnknownScope(scope)),
    }
}

pub(super) fn returned(operation: ProfileRead, hresult: i32) -> Result<bool, ProfileReadError> {
    if hresult == FILE_NOT_FOUND && operation != ProfileRead::Scope {
        Ok(false)
    } else if hresult < 0 {
        Err(ProfileReadError::Native { operation, hresult })
    } else {
        Ok(true)
    }
}

// The API allocates one list buffer, including the backing strings, or one
// standalone default name. Windows documents one LocalFree per returned pointer.
struct LocalBuffer<T>(*mut T);

impl<T> Drop for LocalBuffer<T> {
    fn drop(&mut self) {
        if !self.0.is_null() {
            // SAFETY: This is the original allocation returned by WCS.
            unsafe {
                LocalFree(self.0.cast());
            }
        }
    }
}

/// A non-null pointer must name a NUL-terminated UTF-16 string owned by a live WCS buffer.
unsafe fn profile_name(
    pointer: PWSTR,
    operation: ProfileRead,
) -> Result<Option<OsString>, ProfileReadError> {
    if pointer.is_null() {
        return Ok(None);
    }
    let mut name = Vec::new();
    for index in 0..MAX_PROFILE_NAME {
        // SAFETY: The caller guarantees valid storage through the first NUL.
        let unit = unsafe { *pointer.add(index) };
        if unit == 0 {
            return Ok(Some(OsString::from_wide(&name)));
        }
        name.push(unit);
    }
    Err(ProfileReadError::InvalidResponse(operation))
}

impl ProfileReads for WindowsProfiles {
    fn scope(&mut self, display: &ActiveInternalDisplay) -> Result<ProfileScope, ProfileReadError> {
        let (adapter, source_id) = display_address(display);
        let mut scope = -1;
        // SAFETY: WCS receives a writable scope out parameter.
        let status = unsafe { ColorProfileGetDisplayUserScope(adapter, source_id, &mut scope) };
        returned(ProfileRead::Scope, status)?;
        observed_scope(scope)
    }

    fn associations(
        &mut self,
        display: &ActiveInternalDisplay,
        scope: ProfileScope,
    ) -> Result<Vec<OsString>, ProfileReadError> {
        let (adapter, source_id) = display_address(display);
        let mut list = LocalBuffer::<PWSTR>(null_mut());
        let mut count = 0;
        // SAFETY: WCS initializes the buffer and count through writable out parameters.
        let status = unsafe {
            ColorProfileGetDisplayList(
                native_scope(scope),
                adapter,
                source_id,
                &mut list.0,
                &mut count,
            )
        };
        if !returned(ProfileRead::Associations, status)? || count == 0 {
            return Ok(Vec::new());
        }
        if list.0.is_null() || count as usize > MAX_ASSOCIATIONS {
            return Err(ProfileReadError::InvalidResponse(ProfileRead::Associations));
        }

        let mut profiles = Vec::with_capacity(count as usize);
        for index in 0..count as usize {
            // SAFETY: A successful WCS result contains count entries, each null
            // or NUL-terminated. The list guard stays live through the copy.
            let profile = unsafe { profile_name(*list.0.add(index), ProfileRead::Associations)? };
            profiles.extend(profile.filter(|name| present(name)));
        }
        Ok(profiles)
    }

    fn default_profile(
        &mut self,
        display: &ActiveInternalDisplay,
        scope: ProfileScope,
    ) -> Result<Option<OsString>, ProfileReadError> {
        let (adapter, source_id) = display_address(display);
        let mut name = LocalBuffer::<u16>(null_mut());
        // SAFETY: WCS initializes the name through a writable out parameter.
        let status = unsafe {
            ColorProfileGetDisplayDefault(
                native_scope(scope),
                adapter,
                source_id,
                CPT_ICC,
                CPST_STANDARD_DISPLAY_COLOR_MODE,
                &mut name.0,
            )
        };
        if !returned(ProfileRead::Default, status)? {
            return Ok(None);
        }
        // SAFETY: Successful WCS default name is null or NUL-terminated and still live.
        unsafe { profile_name(name.0, ProfileRead::Default) }
    }

    fn presets(&mut self) -> Vec<OsString> {
        let mut directory = [0; 260];
        // SAFETY: The array is writable and the size is given in UTF-16 units.
        let length = unsafe { GetSystemDirectoryW(directory.as_mut_ptr(), directory.len() as u32) };
        if length == 0 || length as usize >= directory.len() {
            return Vec::new();
        }
        let directory = PathBuf::from(OsString::from_wide(&directory[..length as usize]))
            .join("spool")
            .join("drivers")
            .join("color");
        PRESETS
            .iter()
            .filter_map(|name| {
                let path = directory.join(name);
                let mut header = [0; 128];
                File::open(&path).ok()?.read_exact(&mut header).ok()?;
                valid_monitor_rgb_header(&header).then(|| path.into_os_string())
            })
            .collect()
    }
}

pub(super) fn ordinal_ignore_case(left: &OsStr, right: &OsStr) -> Ordering {
    if left.is_empty() || right.is_empty() {
        return left.cmp(right);
    }
    let a: Vec<u16> = left.encode_wide().collect();
    let b: Vec<u16> = right.encode_wide().collect();
    let (Ok(a_len), Ok(b_len)) = (i32::try_from(a.len()), i32::try_from(b.len())) else {
        return left.cmp(right);
    };
    // SAFETY: Both vectors contain the specified number of readable UTF-16 units.
    let result = unsafe { CompareStringOrdinal(a.as_ptr(), a_len, b.as_ptr(), b_len, 1) };
    comparison(result).unwrap_or_else(|| left.cmp(right))
}

pub(super) fn compare_labels(left: &str, right: &str) -> Ordering {
    if left.is_empty() || right.is_empty() {
        return left.cmp(right);
    }
    let a: Vec<u16> = left.encode_utf16().collect();
    let b: Vec<u16> = right.encode_utf16().collect();
    let (Ok(a_len), Ok(b_len)) = (i32::try_from(a.len()), i32::try_from(b.len())) else {
        return left.cmp(right);
    };
    // SAFETY: Null selects the Windows user locale and default NLS options.
    // Both vectors contain the specified number of readable UTF-16 units.
    let result = unsafe {
        CompareStringEx(
            null(),
            NORM_IGNORECASE,
            a.as_ptr(),
            a_len,
            b.as_ptr(),
            b_len,
            null(),
            null(),
            0,
        )
    };
    comparison(result).unwrap_or_else(|| ordinal_ignore_case(OsStr::new(left), OsStr::new(right)))
}

fn comparison(result: i32) -> Option<Ordering> {
    match result {
        CSTR_LESS_THAN => Some(Ordering::Less),
        CSTR_EQUAL => Some(Ordering::Equal),
        CSTR_GREATER_THAN => Some(Ordering::Greater),
        _ => None,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::display::EndpointId;

    #[test]
    fn wcs_address_combines_target_adapter_and_source_id() {
        let display = ActiveInternalDisplay {
            source: EndpointId {
                adapter_low: 1,
                adapter_high: 2,
                id: 3,
            },
            target: EndpointId {
                adapter_low: 4,
                adapter_high: 5,
                id: 6,
            },
            source_name: OsString::new(),
            monitor_name: Err(2),
            desktop_rate: None,
            physical_rate: None,
            supports_virtual_mode: false,
            boost_refresh_rate: false,
        };
        let (adapter, id) = display_address(&display);
        assert_eq!((adapter.LowPart, adapter.HighPart, id), (4, 5, 3));
    }

    #[test]
    fn scope_round_trips_only_the_two_reviewed_values() {
        for scope in [ProfileScope::SystemWide, ProfileScope::CurrentUser] {
            assert_eq!(observed_scope(native_scope(scope)), Ok(scope));
        }
        assert_eq!(observed_scope(-1), Err(ProfileReadError::UnknownScope(-1)));
    }
}

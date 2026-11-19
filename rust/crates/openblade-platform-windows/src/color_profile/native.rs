//! WCS boundary: the read-only observation calls, plus the fixed-purpose
//! setters of the production color-profile writer. The SDK owns returned
//! buffers until LocalFree.
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

/// WinSDK icm.h `CLASS_MONITOR` ('mntr'); windows-sys 0.61.2 does not export it.
#[cfg(any(test, feature = "display-color-write"))]
const CLASS_MONITOR: u32 = u32::from_be_bytes(*b"mntr");
/// Bound on `EnumDisplayDevicesW` monitors per GDI source. A source that never
/// ends its list yields no monitors, so no single active monitor is chosen.
#[cfg(any(test, feature = "display-color-write"))]
const MAX_MONITORS: u32 = 64;

/// A NUL-terminated copy for a `PCWSTR` input.
#[cfg(any(test, feature = "display-color-write"))]
fn wide(value: &OsStr) -> Vec<u16> {
    value.encode_wide().chain([0]).collect()
}

/// A fixed `DISPLAY_DEVICEW` text field up to its first NUL (managed
/// `ByValTStr` with `TrimEnd('\0')`).
#[cfg(any(test, feature = "display-color-write"))]
fn fixed_text(field: &[u16]) -> OsString {
    let end = field
        .iter()
        .position(|&unit| unit == 0)
        .unwrap_or(field.len());
    OsString::from_wide(&field[..end])
}

// The production color-profile writer's Windows call sites, reachable only
// through `write::WindowsColorProfileNative`. Unit-test builds exclude them,
// so no test can enumerate monitors or change a profile. Association and
// default operations always use the current-user scope and standard ICC,
// like managed `Add/Remove/SetDefaultStandardColorProfile...`.

/// Managed `GetMonitorDeviceKey`: every monitor of this GDI source.
#[cfg(all(feature = "display-color-write", not(test)))]
pub(super) fn monitors(source_name: &OsStr) -> Vec<super::Monitor> {
    use windows_sys::Win32::{
        Graphics::Gdi::{DISPLAY_DEVICE_ACTIVE, DISPLAY_DEVICEW, EnumDisplayDevicesW},
        UI::WindowsAndMessaging::EDD_GET_DEVICE_INTERFACE_NAME,
    };
    let source = wide(source_name);
    bounded_monitors(|index| {
        let mut device = DISPLAY_DEVICEW {
            cb: size_of::<DISPLAY_DEVICEW>() as u32,
            ..Default::default()
        };
        // SAFETY: The source name is NUL-terminated and the writable packet
        // carries its own size, for this synchronous call only.
        let found = unsafe {
            EnumDisplayDevicesW(
                source.as_ptr(),
                index,
                &mut device,
                EDD_GET_DEVICE_INTERFACE_NAME,
            )
        };
        (found != 0).then(|| super::Monitor {
            interface: fixed_text(&device.DeviceID),
            key: fixed_text(&device.DeviceKey),
            active: device.StateFlags & DISPLAY_DEVICE_ACTIVE != 0,
        })
    })
}

/// The enumeration rule of [`monitors`]: indices from zero until `next`
/// reports none. A list still unfinished after [`MAX_MONITORS`] entries yields
/// no monitors, so no single active monitor is chosen from a partial list.
#[cfg(any(test, feature = "display-color-write"))]
fn bounded_monitors(mut next: impl FnMut(u32) -> Option<super::Monitor>) -> Vec<super::Monitor> {
    let mut monitors = Vec::new();
    for index in 0..MAX_MONITORS {
        match next(index) {
            Some(monitor) => monitors.push(monitor),
            None => return monitors,
        }
    }
    Vec::new()
}

/// `ColorProfileAddDisplayAssociation`, neither default nor advanced color.
#[cfg(all(feature = "display-color-write", not(test)))]
pub(super) fn add_display_association(display: &ActiveInternalDisplay, profile: &OsStr) -> i32 {
    let (adapter, source_id) = display_address(display);
    let profile = wide(profile);
    // SAFETY: The profile name is NUL-terminated and live for the call.
    unsafe {
        windows_sys::Win32::UI::ColorSystem::ColorProfileAddDisplayAssociation(
            WCS_PROFILE_MANAGEMENT_SCOPE_CURRENT_USER,
            profile.as_ptr(),
            adapter,
            source_id,
            0,
            0,
        )
    }
}

/// `ColorProfileRemoveDisplayAssociation`, keeping any advanced-color one.
#[cfg(all(feature = "display-color-write", not(test)))]
pub(super) fn remove_display_association(display: &ActiveInternalDisplay, profile: &OsStr) -> i32 {
    let (adapter, source_id) = display_address(display);
    let profile = wide(profile);
    // SAFETY: The profile name is NUL-terminated and live for the call.
    unsafe {
        windows_sys::Win32::UI::ColorSystem::ColorProfileRemoveDisplayAssociation(
            WCS_PROFILE_MANAGEMENT_SCOPE_CURRENT_USER,
            profile.as_ptr(),
            adapter,
            source_id,
            0,
        )
    }
}

/// `ColorProfileSetDisplayDefaultAssociation` for the standard ICC default.
#[cfg(all(feature = "display-color-write", not(test)))]
pub(super) fn set_display_default(display: &ActiveInternalDisplay, profile: &OsStr) -> i32 {
    let (adapter, source_id) = display_address(display);
    let profile = wide(profile);
    // SAFETY: The profile name is NUL-terminated and live for the call.
    unsafe {
        windows_sys::Win32::UI::ColorSystem::ColorProfileSetDisplayDefaultAssociation(
            WCS_PROFILE_MANAGEMENT_SCOPE_CURRENT_USER,
            profile.as_ptr(),
            CPT_ICC,
            CPST_STANDARD_DISPLAY_COLOR_MODE,
            adapter,
            source_id,
        )
    }
}

/// `WcsSetUsePerUserProfiles(CLASS_MONITOR)` on the monitor device key;
/// a failure carries `GetLastError`.
#[cfg(all(feature = "display-color-write", not(test)))]
pub(super) fn use_per_user_profiles(monitor_key: &OsStr, enabled: bool) -> Result<(), u32> {
    let key = wide(monitor_key);
    // SAFETY: The device key is NUL-terminated and live for the call.
    let changed = unsafe {
        windows_sys::Win32::UI::ColorSystem::WcsSetUsePerUserProfiles(
            key.as_ptr(),
            CLASS_MONITOR,
            i32::from(enabled),
        )
    };
    if changed != 0 {
        Ok(())
    } else {
        // SAFETY: Reads this thread's last error immediately after the call.
        Err(unsafe { windows_sys::Win32::Foundation::GetLastError() })
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::display::EndpointId;

    #[test]
    fn color_profile_setter_constants_text_and_linkage() {
        // Retain the SDK imports without calling them; unit-test builds have
        // no route to the production call sites.
        use windows_sys::{
            Win32::{
                Foundation::LUID,
                Graphics::Gdi::{DISPLAY_DEVICEW, EnumDisplayDevicesW},
                UI::ColorSystem::{
                    COLORPROFILESUBTYPE, COLORPROFILETYPE, ColorProfileAddDisplayAssociation,
                    ColorProfileRemoveDisplayAssociation, ColorProfileSetDisplayDefaultAssociation,
                    WcsSetUsePerUserProfiles,
                },
            },
            core::{BOOL, HRESULT, PCWSTR},
        };
        type Scope = WCS_PROFILE_MANAGEMENT_SCOPE;
        let _ = std::hint::black_box((
            ColorProfileAddDisplayAssociation
                as unsafe extern "system" fn(Scope, PCWSTR, LUID, u32, BOOL, BOOL) -> HRESULT,
            ColorProfileRemoveDisplayAssociation
                as unsafe extern "system" fn(Scope, PCWSTR, LUID, u32, BOOL) -> HRESULT,
            ColorProfileSetDisplayDefaultAssociation
                as unsafe extern "system" fn(
                    Scope,
                    PCWSTR,
                    COLORPROFILETYPE,
                    COLORPROFILESUBTYPE,
                    LUID,
                    u32,
                ) -> HRESULT,
            WcsSetUsePerUserProfiles as unsafe extern "system" fn(PCWSTR, u32, BOOL) -> BOOL,
            EnumDisplayDevicesW
                as unsafe extern "system" fn(PCWSTR, u32, *mut DISPLAY_DEVICEW, u32) -> BOOL,
        ));
        // Managed WindowsDisplayNative: ClassMonitor, ColorProfileTypeIcc,
        // ColorProfileSubtypeStandardDisplay and CurrentUserScope.
        assert_eq!(CLASS_MONITOR, 0x6D6E_7472);
        assert_eq!((CPT_ICC, CPST_STANDARD_DISPLAY_COLOR_MODE), (0, 7));
        assert_eq!(WCS_PROFILE_MANAGEMENT_SCOPE_CURRENT_USER, 1);
        assert_eq!(MAX_MONITORS, 64);
        assert_eq!(
            wide(OsStr::new("ab")),
            [u16::from(b'a'), u16::from(b'b'), 0]
        );
        let mut field = [0u16; 8];
        field[..3].copy_from_slice(&[u16::from(b'k'), u16::from(b'e'), u16::from(b'y')]);
        field[4] = u16::from(b'x');
        assert_eq!(fixed_text(&field), "key");
        assert_eq!(fixed_text(&[u16::from(b'z'); 2]), "zz");
    }

    #[test]
    fn wcs_address_combines_target_adapter_and_source_id() {
        let mut display = ActiveInternalDisplay::brightness_fixture("synthetic", 6);
        display.source = EndpointId {
            adapter_low: 1,
            adapter_high: 2,
            id: 3,
        };
        display.target = EndpointId {
            adapter_low: 4,
            adapter_high: 5,
            id: 6,
        };
        let (adapter, id) = display_address(&display);
        assert_eq!((adapter.LowPart, adapter.HighPart, id), (4, 5, 3));
    }

    #[test]
    fn monitor_enumeration_stops_at_the_end_and_rejects_an_unbounded_list() {
        let monitor = |index: u32| crate::color_profile::Monitor {
            interface: format!("monitor{index}").into(),
            key: format!("key{index}").into(),
            active: index == 1,
        };
        let mut asked = Vec::new();
        let listed = bounded_monitors(|index| {
            asked.push(index);
            (index < 2).then(|| monitor(index))
        });
        assert_eq!(asked, [0, 1, 2]);
        assert_eq!(
            listed.iter().map(|m| m.key.clone()).collect::<Vec<_>>(),
            ["key0", "key1"]
        );
        // The last admitted index still ends the list.
        let last = MAX_MONITORS - 1;
        assert_eq!(
            bounded_monitors(|index| (index < last).then(|| monitor(index))).len(),
            last as usize
        );
        // A source that still reports an entry at the bound yields none, and
        // nothing past the bound is asked for.
        let mut highest = 0;
        let unbounded = bounded_monitors(|index| {
            highest = index;
            Some(monitor(index))
        });
        assert!(unbounded.is_empty());
        assert_eq!(highest, MAX_MONITORS - 1);
    }

    #[test]
    fn scope_round_trips_only_the_two_reviewed_values() {
        for scope in [ProfileScope::SystemWide, ProfileScope::CurrentUser] {
            assert_eq!(observed_scope(native_scope(scope)), Ok(scope));
        }
        assert_eq!(observed_scope(-1), Err(ProfileReadError::UnknownScope(-1)));
    }
}

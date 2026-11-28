//! Windows SDK calls and union decoding.
#![allow(unsafe_code)]

use super::refresh::{DriverMode, RefreshApi};
use super::*;
use windows_sys::Win32::{
    Devices::Display::{
        DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_PREFERRED_MODE, DISPLAYCONFIG_DEVICE_INFO_TYPE,
        DISPLAYCONFIG_MODE_INFO_TYPE_TARGET, DISPLAYCONFIG_TARGET_MODE,
        DISPLAYCONFIG_TARGET_PREFERRED_MODE, DISPLAYCONFIG_VIDEO_SIGNAL_INFO,
        DisplayConfigGetDeviceInfo, GetDisplayConfigBufferSizes, QueryDisplayConfig,
    },
    Foundation::ERROR_INVALID_DATA,
    Graphics::Gdi::{
        DEVMODEW, DISPLAYCONFIG_COLOR_ENCODING, DISPLAYCONFIG_PATH_MODE_IDX_INVALID,
        DISPLAYCONFIG_PATH_TARGET_MODE_IDX_INVALID, DM_INTERLACED, EnumDisplaySettingsExW,
    },
    System::Registry::{HKEY_LOCAL_MACHINE, RRF_RT_REG_BINARY, RegGetValueW},
};

// WinSDK 10.0.26100.0 wingdi.h. windows-sys 0.61.2 exposes only INFO (type 9).
const GET_ADVANCED_COLOR_INFO_2: DISPLAYCONFIG_DEVICE_INFO_TYPE = 15;
pub(super) const HDR_SUPPORTED: u32 = 1 << 4;
pub(super) const HDR_USER_ENABLED: u32 = 1 << 5;

#[repr(C)]
pub(super) struct GetAdvancedColorInfo2 {
    pub(super) header: DISPLAYCONFIG_DEVICE_INFO_HEADER,
    pub(super) value: u32,
    color_encoding: DISPLAYCONFIG_COLOR_ENCODING,
    pub(super) bits_per_color_channel: u32,
    active_color_mode: i32,
}

const _: () = {
    assert!(size_of::<GetAdvancedColorInfo2>() == 36);
    assert!(align_of::<GetAdvancedColorInfo2>() == 4);
    assert!(std::mem::offset_of!(GetAdvancedColorInfo2, header) == 0);
    assert!(std::mem::offset_of!(GetAdvancedColorInfo2, value) == 20);
    assert!(std::mem::offset_of!(GetAdvancedColorInfo2, bits_per_color_channel) == 28);
};

impl GetAdvancedColorInfo2 {
    pub(super) fn new(adapter_id: LUID, id: u32) -> Self {
        Self {
            header: DISPLAYCONFIG_DEVICE_INFO_HEADER {
                r#type: GET_ADVANCED_COLOR_INFO_2,
                size: size_of::<Self>() as u32,
                adapterId: adapter_id,
                id,
            },
            value: 0,
            color_encoding: 0,
            bits_per_color_channel: 0,
            active_color_mode: 0,
        }
    }
}

// WinSDK wingdi.h: SET_HDR_STATE (16), with only enableHdr in bit zero.
#[cfg(any(test, feature = "scripted-display", feature = "display-hdr-write"))]
#[repr(C)]
pub(super) struct SetHdrState {
    pub(super) header: DISPLAYCONFIG_DEVICE_INFO_HEADER,
    pub(super) value: u32,
}

#[cfg(any(test, feature = "scripted-display", feature = "display-hdr-write"))]
impl SetHdrState {
    pub(super) fn new(target: EndpointId, enabled: bool) -> Self {
        Self {
            header: DISPLAYCONFIG_DEVICE_INFO_HEADER {
                r#type: 16,
                size: size_of::<Self>() as u32,
                adapterId: LUID {
                    LowPart: target.adapter_low,
                    HighPart: target.adapter_high,
                },
                id: target.id,
            },
            value: u32::from(enabled),
        }
    }
}

#[cfg(any(test, feature = "scripted-display", feature = "display-hdr-write"))]
const _: () = {
    assert!(size_of::<SetHdrState>() == 24);
    assert!(align_of::<SetHdrState>() == 4);
    assert!(std::mem::offset_of!(SetHdrState, header) == 0);
    assert!(std::mem::offset_of!(SetHdrState, value) == 20);
};

// Compile and link the ABI in the platform unit-test binary only. Neither
// the private fixture nor any shipping entry point can reference this setter.
#[cfg(test)]
fn set_hdr_state(target: EndpointId, enabled: bool) -> i32 {
    let packet = SetHdrState::new(target, enabled);
    // SAFETY: The packet starts with the initialized SDK header and is
    // valid for the entire synchronous input-only call.
    unsafe {
        windows_sys::Win32::Devices::Display::DisplayConfigSetDeviceInfo(
            std::ptr::from_ref(&packet).cast(),
        )
    }
}

// The one production SET_HDR_STATE call site, reachable only through
// `hdr::WindowsHdrNative`. Unit-test builds exclude it so no test can submit.
#[cfg(all(feature = "display-hdr-write", not(test)))]
pub(super) fn submit_hdr_state(target: EndpointId, enabled: bool) -> u32 {
    let packet = SetHdrState::new(target, enabled);
    // SAFETY: The packet starts with the initialized SDK header and is
    // valid for the entire synchronous input-only call.
    (unsafe {
        windows_sys::Win32::Devices::Display::DisplayConfigSetDeviceInfo(
            std::ptr::from_ref(&packet).cast(),
        )
    }) as u32
}

#[cfg(test)]
#[test]
fn hdr_setter_packet_and_linkage() {
    let _ = std::hint::black_box(set_hdr_state as fn(EndpointId, bool) -> i32);
    let target = EndpointId {
        adapter_low: 0x1234,
        adapter_high: -2,
        id: 7,
    };
    for (enabled, expected) in [(false, 0), (true, 1)] {
        let packet = SetHdrState::new(target, enabled);
        assert_eq!(packet.header.r#type, 16);
        assert_eq!(packet.header.size, 24);
        assert_eq!(
            EndpointId::new(packet.header.adapterId, packet.header.id),
            target
        );
        assert_eq!(packet.value, expected);
    }
}

// The one production SetDisplayConfig call site, reachable only through
// `refresh_write::WindowsRefreshNative` and only with the managed
// ValidateTopology or ApplyTopology flags. Unit-test builds exclude it so no
// test can validate or apply a configuration.
#[cfg(all(feature = "display-refresh-write", not(test)))]
pub(super) fn validate_display_config(
    paths: &[DISPLAYCONFIG_PATH_INFO],
    modes: &[DISPLAYCONFIG_MODE_INFO],
) -> u32 {
    set_display_config(paths, modes, super::refresh_packet::VALIDATE)
}

#[cfg(all(feature = "display-refresh-write", not(test)))]
pub(super) fn apply_display_config(
    paths: &[DISPLAYCONFIG_PATH_INFO],
    modes: &[DISPLAYCONFIG_MODE_INFO],
) -> u32 {
    set_display_config(paths, modes, super::refresh_packet::APPLY)
}

#[cfg(all(feature = "display-refresh-write", not(test)))]
fn set_display_config(
    paths: &[DISPLAYCONFIG_PATH_INFO],
    modes: &[DISPLAYCONFIG_MODE_INFO],
    flags: u32,
) -> u32 {
    // Both arrays come from one bounded query (at most MAX_ENTRIES each).
    let (path_count, mode_count) = (paths.len() as u32, modes.len() as u32);
    // SAFETY: Both arrays are initialized and live for the whole synchronous
    // input-only call; the counts are their lengths.
    (unsafe {
        windows_sys::Win32::Devices::Display::SetDisplayConfig(
            path_count,
            paths.as_ptr(),
            mode_count,
            modes.as_ptr(),
            flags,
        )
    }) as u32
}

#[cfg(all(
    test,
    any(feature = "scripted-display", feature = "display-refresh-write")
))]
#[test]
fn refresh_setter_flags_and_linkage() {
    // Retain the SDK import without calling it; this build has no route to
    // the production call site.
    let _ = std::hint::black_box(
        windows_sys::Win32::Devices::Display::SetDisplayConfig
            as unsafe extern "system" fn(
                u32,
                *const DISPLAYCONFIG_PATH_INFO,
                u32,
                *const DISPLAYCONFIG_MODE_INFO,
                u32,
            ) -> i32,
    );
    // Managed ValidateTopology and ApplyTopology flags.
    assert_eq!(super::refresh_packet::VALIDATE, 0x28060);
    assert_eq!(super::refresh_packet::APPLY, 0x282a0);
}

pub(super) struct WindowsDisplay;

impl DisplayApi for WindowsDisplay {
    fn buffer_sizes(&mut self, flags: u32) -> Result<(u32, u32), u32> {
        let (mut paths, mut modes) = (0, 0);
        // SAFETY: Both out-pointers reference initialized, writable u32s.
        let status = unsafe { GetDisplayConfigBufferSizes(flags, &mut paths, &mut modes) };
        if status == ERROR_SUCCESS {
            Ok((paths, modes))
        } else {
            Err(status)
        }
    }

    fn query(
        &mut self,
        flags: u32,
        counts: &mut (u32, u32),
        paths: &mut [DISPLAYCONFIG_PATH_INFO],
        modes: &mut [DISPLAYCONFIG_MODE_INFO],
    ) -> u32 {
        assert!(counts.0 as usize <= paths.len() && counts.1 as usize <= modes.len());
        // SAFETY: Both output buffers hold at least the advertised counts;
        // their slices and count pointers are disjoint. The active-path query
        // requires a null topology ID pointer.
        unsafe {
            QueryDisplayConfig(
                flags,
                &mut counts.0,
                paths.as_mut_ptr(),
                &mut counts.1,
                modes.as_mut_ptr(),
                std::ptr::null_mut(),
            )
        }
    }

    fn source_name(&mut self, request: &mut DISPLAYCONFIG_SOURCE_DEVICE_NAME) -> u32 {
        // SAFETY: Use the whole packet's pointer (the header is its first
        // field). The initialized header specifies the source type and size.
        unsafe { DisplayConfigGetDeviceInfo(std::ptr::from_mut(request).cast()) as u32 }
    }

    fn target_name(&mut self, request: &mut DISPLAYCONFIG_TARGET_DEVICE_NAME) -> u32 {
        // SAFETY: Use the whole packet's pointer. The initialized header
        // specifies the target type and size.
        unsafe { DisplayConfigGetDeviceInfo(std::ptr::from_mut(request).cast()) as u32 }
    }

    fn advanced_color(&mut self, request: &mut GetAdvancedColorInfo2) -> u32 {
        // SAFETY: The whole writable packet begins with a header initialized
        // with its SDK type, size, and target endpoint.
        unsafe { DisplayConfigGetDeviceInfo(std::ptr::from_mut(request).cast()) as u32 }
    }
}

impl RefreshApi for WindowsDisplay {
    fn mode(&mut self, source: &[u16], index: u32) -> Option<DriverMode> {
        assert_eq!(source.last(), Some(&0));
        let mut mode = DEVMODEW {
            dmSize: size_of::<DEVMODEW>() as u16,
            ..Default::default()
        };
        // SAFETY: source is NUL terminated and mode is writable. dmSize
        // describes the struct and dmDriverExtra is zero.
        if unsafe { EnumDisplaySettingsExW(source.as_ptr(), index, &mut mode, 0) } == 0 {
            return None;
        }
        Some(DriverMode {
            width: mode.dmPelsWidth,
            height: mode.dmPelsHeight,
            hertz: mode.dmDisplayFrequency,
            // SAFETY: The display interpretation of the initialized u32 union.
            interlaced: unsafe { mode.Anonymous2.dmDisplayFlags } & DM_INTERLACED != 0,
        })
    }

    fn preferred_mode(&mut self, target: EndpointId) -> Result<DISPLAYCONFIG_TARGET_MODE, u32> {
        let mut preferred = DISPLAYCONFIG_TARGET_PREFERRED_MODE {
            header: DISPLAYCONFIG_DEVICE_INFO_HEADER {
                r#type: DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_PREFERRED_MODE,
                size: size_of::<DISPLAYCONFIG_TARGET_PREFERRED_MODE>() as u32,
                adapterId: LUID {
                    LowPart: target.adapter_low,
                    HighPart: target.adapter_high,
                },
                id: target.id,
            },
            ..Default::default()
        };
        // SAFETY: The header describes the initialized, writable packet.
        let status =
            unsafe { DisplayConfigGetDeviceInfo(std::ptr::from_mut(&mut preferred).cast()) } as u32;
        if status == ERROR_SUCCESS {
            Ok(preferred.targetMode)
        } else {
            Err(status)
        }
    }

    fn edid(&mut self, subkey: &[u16]) -> Result<Vec<u8>, u32> {
        // One base block and up to 255 extensions. An oversized value
        // returns ERROR_MORE_DATA and its partial contents are discarded.
        const MAX_EDID_BYTES: usize = 128 * 256;
        const VALUE: [u16; 5] = [b'E' as u16, b'D' as u16, b'I' as u16, b'D' as u16, 0];
        assert_eq!(subkey.last(), Some(&0));
        let mut data = vec![0_u8; MAX_EDID_BYTES];
        let mut length = data.len() as u32;
        // SAFETY: Both strings are NUL terminated. data holds length bytes,
        // length is writable and the optional type output is omitted.
        let status = unsafe {
            RegGetValueW(
                HKEY_LOCAL_MACHINE,
                subkey.as_ptr(),
                VALUE.as_ptr(),
                RRF_RT_REG_BINARY,
                std::ptr::null_mut(),
                data.as_mut_ptr().cast(),
                &mut length,
            )
        };
        if status != ERROR_SUCCESS {
            return Err(status);
        }
        if length as usize > data.len() {
            return Err(ERROR_INVALID_DATA);
        }
        data.truncate(length as usize);
        Ok(data)
    }
}

pub(super) fn active_signal(
    path: &DISPLAYCONFIG_PATH_INFO,
    modes: &[DISPLAYCONFIG_MODE_INFO],
    target: EndpointId,
) -> Result<SignalTiming, DisplayReadError> {
    let index = target_mode_index(path, modes, target)?;
    // SAFETY: The discriminant and endpoint matched this path's target.
    let signal = unsafe { modes[index].Anonymous.targetMode.targetVideoSignalInfo };
    Ok(signal_timing(signal))
}

pub(super) fn target_mode_index(
    path: &DISPLAYCONFIG_PATH_INFO,
    modes: &[DISPLAYCONFIG_MODE_INFO],
    target: EndpointId,
) -> Result<usize, DisplayReadError> {
    // SAFETY: Both index layouts are initialized 32-bit words. Virtual mode
    // packs the target index into the high 16 bits and the desktop image below.
    let index_word = unsafe { path.targetInfo.Anonymous.modeInfoIdx };
    let (index, invalid) = if path.flags & DISPLAYCONFIG_PATH_SUPPORT_VIRTUAL_MODE != 0 {
        (index_word >> 16, DISPLAYCONFIG_PATH_TARGET_MODE_IDX_INVALID)
    } else {
        (index_word, DISPLAYCONFIG_PATH_MODE_IDX_INVALID)
    };
    if index == invalid {
        return Err(DisplayReadError::MissingTargetMode);
    }
    let mode = modes
        .get(index as usize)
        .ok_or(DisplayReadError::InvalidTargetModeIndex)?;
    if mode.infoType != DISPLAYCONFIG_MODE_INFO_TYPE_TARGET
        || EndpointId::new(mode.adapterId, mode.id) != target
    {
        return Err(DisplayReadError::MismatchedTargetMode);
    }
    Ok(index as usize)
}

pub(super) fn signal_timing(signal: DISPLAYCONFIG_VIDEO_SIGNAL_INFO) -> SignalTiming {
    SignalTiming {
        width: signal.activeSize.cx,
        height: signal.activeSize.cy,
        scanline_ordering: signal.scanLineOrdering,
        rate: RefreshRate::observed(signal.vSyncFreq),
    }
}

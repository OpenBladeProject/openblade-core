//! Windows SDK calls and union decoding.
#![allow(unsafe_code)]

use super::*;
use windows_sys::Win32::{
    Devices::Display::{
        DISPLAYCONFIG_DEVICE_INFO_TYPE, DISPLAYCONFIG_MODE_INFO_TYPE_TARGET,
        DisplayConfigGetDeviceInfo, GetDisplayConfigBufferSizes, QueryDisplayConfig,
    },
    Graphics::Gdi::{
        DISPLAYCONFIG_COLOR_ENCODING, DISPLAYCONFIG_PATH_MODE_IDX_INVALID,
        DISPLAYCONFIG_PATH_TARGET_MODE_IDX_INVALID,
    },
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

pub(super) fn physical_rate(
    path: &DISPLAYCONFIG_PATH_INFO,
    modes: &[DISPLAYCONFIG_MODE_INFO],
    target: EndpointId,
) -> Result<Option<RefreshRate>, DisplayReadError> {
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
    // SAFETY: The discriminant and endpoint matched this path's target.
    let signal = unsafe { mode.Anonymous.targetMode.targetVideoSignalInfo };
    Ok(RefreshRate::observed(signal.vSyncFreq))
}

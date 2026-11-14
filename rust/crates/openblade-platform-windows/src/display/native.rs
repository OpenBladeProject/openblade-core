//! Windows SDK calls and union decoding.
#![allow(unsafe_code)]

use super::*;
use windows_sys::Win32::{
    Devices::Display::{
        DISPLAYCONFIG_MODE_INFO_TYPE_TARGET, DisplayConfigGetDeviceInfo,
        GetDisplayConfigBufferSizes, QueryDisplayConfig,
    },
    Graphics::Gdi::{
        DISPLAYCONFIG_PATH_MODE_IDX_INVALID, DISPLAYCONFIG_PATH_TARGET_MODE_IDX_INVALID,
    },
};

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

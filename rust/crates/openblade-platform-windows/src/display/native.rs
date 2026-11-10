//! Windows SDK calls. The rest of the adapter accepts scripted responses.
#![allow(unsafe_code)]

use super::*;

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

#![allow(unsafe_code)]

use super::{Descriptor, LeaseSource, blocked};
use crate::discovery::windows::WindowsMetadata;
use std::{
    ffi::c_void,
    io,
    os::windows::io::{AsRawHandle, FromRawHandle, OwnedHandle},
    ptr::{null, null_mut},
    sync::atomic::{AtomicBool, Ordering},
};
use windows_sys::Win32::{
    Devices::{
        DeviceAndDriverInstallation::{
            CM_MapCrToWin32Err, CM_NOTIFY_ACTION, CM_NOTIFY_ACTION_DEVICEQUERYREMOVE,
            CM_NOTIFY_ACTION_DEVICEREMOVECOMPLETE, CM_NOTIFY_ACTION_DEVICEREMOVEPENDING,
            CM_NOTIFY_EVENT_DATA, CM_NOTIFY_FILTER, CM_NOTIFY_FILTER_0_1,
            CM_NOTIFY_FILTER_TYPE_DEVICEHANDLE, CM_Register_Notification,
            CM_Unregister_Notification, CR_SUCCESS, HCMNOTIFICATION,
        },
        HumanInterfaceDevice::{
            HIDD_ATTRIBUTES, HIDP_CAPS, HIDP_STATUS_SUCCESS, HidD_FreePreparsedData,
            HidD_GetAttributes, HidD_GetPreparsedData, HidP_GetCaps, PHIDP_PREPARSED_DATA,
        },
    },
    Foundation::{ERROR_GEN_FAILURE, ERROR_SUCCESS, INVALID_HANDLE_VALUE},
    Storage::FileSystem::{
        CreateFileW, FILE_FLAG_OVERLAPPED, FILE_SHARE_READ, FILE_SHARE_WRITE, OPEN_EXISTING,
    },
};

impl LeaseSource for WindowsMetadata {
    type Handle = MetadataHandle;

    fn open_metadata(&self, path: &str) -> io::Result<MetadataHandle> {
        MetadataHandle::open(path, 0)
    }

    fn describe(&self, handle: &MetadataHandle) -> io::Result<Descriptor> {
        handle.removal.check()?;
        let raw = handle.file.as_raw_handle();
        let mut attributes = HIDD_ATTRIBUTES {
            Size: size_of::<HIDD_ATTRIBUTES>() as u32,
            ..Default::default()
        };
        // SAFETY: The handle is borrowed for the call; the output is initialized.
        if !unsafe { HidD_GetAttributes(raw, &mut attributes) } {
            return Err(io::Error::last_os_error());
        }

        let mut preparsed = 0;
        // SAFETY: The handle is still live and the output is writable.
        if !unsafe { HidD_GetPreparsedData(raw, &mut preparsed) } {
            return Err(io::Error::last_os_error());
        }
        if preparsed == 0 {
            return Err(blocked("missing HID preparsed data"));
        }
        let preparsed = PreparsedData(preparsed);
        let mut caps = HIDP_CAPS::default();
        // SAFETY: PreparsedData owns this pointer through the call.
        if unsafe { HidP_GetCaps(preparsed.0, &mut caps) } != HIDP_STATUS_SUCCESS {
            return Err(blocked("failed to read HID caps"));
        }
        handle.removal.check()?;
        Ok(Descriptor {
            device: (attributes.VendorID, attributes.ProductID),
            usage: (caps.UsagePage, caps.Usage),
            feature_bytes: caps.FeatureReportByteLength,
            version: attributes.VersionNumber,
        })
    }
}

pub(super) struct MetadataHandle {
    // Stop callbacks before closing the handle registered with ConfigMgr.
    removal: RemovalWatch,
    file: OwnedHandle,
}

impl MetadataHandle {
    pub(super) fn open(path: &str, access: u32) -> io::Result<Self> {
        let path: Vec<u16> = path.encode_utf16().chain([0]).collect();
        // SAFETY: SetupAPI supplied the bounded interface name. Null security
        // attributes make the returned overlapped handle noninheritable.
        let raw = unsafe {
            CreateFileW(
                path.as_ptr(),
                access,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                null(),
                OPEN_EXISTING,
                FILE_FLAG_OVERLAPPED,
                null_mut(),
            )
        };
        if raw == INVALID_HANDLE_VALUE {
            return Err(io::Error::last_os_error());
        }
        // SAFETY: CreateFileW returned an owned file handle.
        let file = unsafe { OwnedHandle::from_raw_handle(raw) };
        let removal = RemovalWatch::register(&file)?;
        Ok(Self { removal, file })
    }

    #[cfg(feature = "firmware-performance-compile-check")]
    pub(super) fn retire(&mut self) -> io::Result<()> {
        self.removal.unregister()
    }
}

impl AsRawHandle for MetadataHandle {
    fn as_raw_handle(&self) -> std::os::windows::io::RawHandle {
        self.file.as_raw_handle()
    }
}

struct PreparsedData(PHIDP_PREPARSED_DATA);

impl Drop for PreparsedData {
    fn drop(&mut self) {
        // SAFETY: This pointer came from HidD_GetPreparsedData and is freed once.
        unsafe { HidD_FreePreparsedData(self.0) };
    }
}

struct RemovalWatch {
    registration: HCMNOTIFICATION,
    revoked: *mut AtomicBool,
}

impl RemovalWatch {
    fn register(file: &OwnedHandle) -> io::Result<Self> {
        let mut filter = CM_NOTIFY_FILTER {
            cbSize: size_of::<CM_NOTIFY_FILTER>() as u32,
            FilterType: CM_NOTIFY_FILTER_TYPE_DEVICEHANDLE,
            ..Default::default()
        };
        filter.u.DeviceHandle = CM_NOTIFY_FILTER_0_1 {
            hTarget: file.as_raw_handle(),
        };
        let revoked = Box::into_raw(Box::new(AtomicBool::new(false)));
        let mut registration = null_mut();
        // SAFETY: The callback sees only a stable atomic, which remains
        // allocated until unregistration completes.
        let result = unsafe {
            CM_Register_Notification(&filter, revoked.cast(), Some(notify), &mut registration)
        };
        if result != CR_SUCCESS {
            // SAFETY: No registration owns the context on failure.
            unsafe { drop(Box::from_raw(revoked)) };
            // SAFETY: Both arguments are scalar error codes.
            let error = unsafe { CM_MapCrToWin32Err(result, ERROR_GEN_FAILURE) };
            return Err(io::Error::from_raw_os_error(error as i32));
        }
        Ok(Self {
            registration,
            revoked,
        })
    }

    fn check(&self) -> io::Result<()> {
        // SAFETY: This allocation lives until the watch is dropped.
        if unsafe { (*self.revoked).load(Ordering::Acquire) } {
            return Err(blocked("opened HID removal was observed"));
        }
        Ok(())
    }

    fn unregister(&mut self) -> io::Result<()> {
        if self.registration.is_null() {
            return Ok(());
        }
        // SAFETY: The callback only touches its atomic. It never waits on
        // this thread or accesses the file handle.
        let result = unsafe { CM_Unregister_Notification(self.registration) };
        if result != CR_SUCCESS {
            // SAFETY: Both arguments are scalar error codes.
            let error = unsafe { CM_MapCrToWin32Err(result, ERROR_GEN_FAILURE) };
            return Err(io::Error::from_raw_os_error(error as i32));
        }
        self.registration = null_mut();
        // SAFETY: Unregistration drained callbacks.
        unsafe { drop(Box::from_raw(self.revoked)) };
        Ok(())
    }
}

impl Drop for RemovalWatch {
    fn drop(&mut self) {
        // Keep the callback context alive if metadata-only retirement fails.
        let _ = self.unregister();
    }
}

unsafe extern "system" fn notify(
    _registration: HCMNOTIFICATION,
    context: *const c_void,
    action: CM_NOTIFY_ACTION,
    _data: *const CM_NOTIFY_EVENT_DATA,
    _data_size: u32,
) -> u32 {
    if matches!(
        action,
        CM_NOTIFY_ACTION_DEVICEQUERYREMOVE
            | CM_NOTIFY_ACTION_DEVICEREMOVEPENDING
            | CM_NOTIFY_ACTION_DEVICEREMOVECOMPLETE
    ) {
        // SAFETY: The registered context is an AtomicBool, retained until
        // unregistration drains all callbacks.
        unsafe { (*context.cast::<AtomicBool>()).store(true, Ordering::Release) };
    }
    ERROR_SUCCESS
}

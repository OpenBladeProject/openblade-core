//! The Windows battery reads of managed `WindowsPowerStateProvider` and
//! `WindowsBatteryHealthReader`: `CallNtPowerInformation(SystemBatteryState)`
//! and the battery class IOCTLs. Both are read-only queries.
#![allow(unsafe_code)]

use super::Capture;
use std::{
    ffi::c_void,
    io,
    mem::{offset_of, size_of},
    ptr::{null, null_mut},
};
use windows_sys::Win32::{
    Devices::DeviceAndDriverInstallation::{
        DIGCF_DEVICEINTERFACE, DIGCF_PRESENT, HDEVINFO, SP_DEVICE_INTERFACE_DATA,
        SP_DEVICE_INTERFACE_DETAIL_DATA_W, SetupDiDestroyDeviceInfoList,
        SetupDiEnumDeviceInterfaces, SetupDiGetClassDevsW, SetupDiGetDeviceInterfaceDetailW,
    },
    Foundation::{
        CloseHandle, ERROR_NO_MORE_ITEMS, GENERIC_READ, GetLastError, HANDLE, INVALID_HANDLE_VALUE,
    },
    Storage::FileSystem::{CreateFileW, FILE_SHARE_READ, FILE_SHARE_WRITE, OPEN_EXISTING},
    System::{
        IO::DeviceIoControl,
        Power::{
            BATTERY_CAPACITY_RELATIVE, BATTERY_INFORMATION, BATTERY_QUERY_INFORMATION,
            BatteryInformation, CallNtPowerInformation, GUID_DEVICE_BATTERY,
            IOCTL_BATTERY_QUERY_INFORMATION, IOCTL_BATTERY_QUERY_TAG, SystemBatteryState,
        },
    },
};

/// Managed enumerates every present battery interface; a laptop has one or
/// two. More than this is refused rather than enumerated without bound.
const MAX_BATTERIES: u32 = 16;
const MAX_INTERFACE_PATH: usize = 2048;

/// `SYSTEM_BATTERY_STATE` with its BOOLEAN members as bytes: the kernel
/// writes them, and a Rust `bool` must never hold another value.
#[repr(C)]
#[derive(Default)]
#[allow(dead_code)] // Only presence and rate are read, as managed reads them.
struct BatteryState {
    ac_on_line: u8,
    battery_present: u8,
    charging: u8,
    discharging: u8,
    spare: [u8; 3],
    tag: u8,
    max_capacity: u32,
    remaining_capacity: u32,
    rate: u32,
    estimated_time: u32,
    default_alert1: u32,
    default_alert2: u32,
}

pub(in super::super) struct Windows;

impl Capture for Windows {
    fn battery_state(&self) -> Option<(bool, u32)> {
        let mut state = BatteryState::default();
        // SAFETY: No input buffer. The output is a writable SYSTEM_BATTERY_STATE
        // layout of exactly the advertised size.
        let status = unsafe {
            CallNtPowerInformation(
                SystemBatteryState,
                null(),
                0,
                (&mut state as *mut BatteryState).cast(),
                size_of::<BatteryState>() as u32,
            )
        };
        (status == 0).then_some((state.battery_present != 0, state.rate))
    }

    fn battery_capacities(&self) -> io::Result<Vec<(u32, u32)>> {
        // SAFETY: The class GUID is valid; the optional inputs are null.
        let raw = unsafe {
            SetupDiGetClassDevsW(
                &GUID_DEVICE_BATTERY,
                null(),
                null_mut(),
                DIGCF_PRESENT | DIGCF_DEVICEINTERFACE,
            )
        };
        if raw == INVALID_HANDLE_VALUE as HDEVINFO {
            return Err(io::Error::last_os_error());
        }
        let set = DeviceInfoSet(raw);
        let mut capacities = Vec::new();
        for index in 0..=MAX_BATTERIES {
            let mut interface = SP_DEVICE_INTERFACE_DATA {
                cbSize: size_of::<SP_DEVICE_INTERFACE_DATA>() as u32,
                ..Default::default()
            };
            // SAFETY: The set is owned for the loop, the GUID is valid, and
            // the initialized output supplies its documented size.
            if unsafe {
                SetupDiEnumDeviceInterfaces(
                    set.0,
                    null(),
                    &GUID_DEVICE_BATTERY,
                    index,
                    &mut interface,
                )
            } == 0
            {
                // SAFETY: The last error of the failed call on this thread.
                return if unsafe { GetLastError() } == ERROR_NO_MORE_ITEMS {
                    Ok(capacities)
                } else {
                    Err(io::Error::last_os_error())
                };
            }
            if index == MAX_BATTERIES {
                break;
            }
            let Some(path) = set.path(&interface)? else {
                continue;
            };
            if let Some(information) = read_capacity(&path)
                && information.Capabilities & BATTERY_CAPACITY_RELATIVE == 0
            {
                capacities.push((
                    information.DesignedCapacity,
                    information.FullChargedCapacity,
                ));
            }
        }
        Err(io::Error::other("too many battery interfaces"))
    }
}

struct DeviceInfoSet(HDEVINFO);

impl Drop for DeviceInfoSet {
    fn drop(&mut self) {
        // SAFETY: A successful SetupDiGetClassDevsW transferred ownership here.
        unsafe { SetupDiDestroyDeviceInfoList(self.0) };
    }
}

#[repr(C)]
struct InterfaceDetail {
    cb_size: u32,
    path: [u16; MAX_INTERFACE_PATH],
}

impl DeviceInfoSet {
    /// The interface's terminated device path. Managed fails the capture
    /// when the detail cannot be read; a blank path is skipped.
    fn path(&self, interface: &SP_DEVICE_INTERFACE_DATA) -> io::Result<Option<Vec<u16>>> {
        let mut detail = InterfaceDetail {
            cb_size: size_of::<SP_DEVICE_INTERFACE_DETAIL_DATA_W>() as u32,
            path: [0; MAX_INTERFACE_PATH],
        };
        let mut bytes = 0;
        // SAFETY: The aligned detail buffer has the SDK layout (DWORD then
        // UTF-16), the platform cbSize and the advertised writable capacity.
        if unsafe {
            SetupDiGetDeviceInterfaceDetailW(
                self.0,
                interface,
                (&mut detail as *mut InterfaceDetail).cast(),
                size_of::<InterfaceDetail>() as u32,
                &mut bytes,
                null_mut(),
            )
        } == 0
        {
            return Err(io::Error::last_os_error());
        }
        let units = (bytes as usize)
            .saturating_sub(offset_of!(InterfaceDetail, path))
            .div_euclid(size_of::<u16>())
            .min(MAX_INTERFACE_PATH);
        let path = &detail.path[..units];
        let Some(end) = path.iter().position(|unit| *unit == 0) else {
            return Ok(None);
        };
        let path = &path[..end];
        if String::from_utf16(path).is_ok_and(|path| path.trim().is_empty()) {
            return Ok(None);
        }
        Ok(Some(path.iter().copied().chain([0]).collect()))
    }
}

struct Handle(HANDLE);

impl Drop for Handle {
    fn drop(&mut self) {
        // SAFETY: A successful CreateFileW transferred ownership here.
        unsafe { CloseHandle(self.0) };
    }
}

/// Managed `TryReadCapacity`: the battery's tag, then its
/// `BatteryInformation`, or None when either is unavailable.
fn read_capacity(path: &[u16]) -> Option<BATTERY_INFORMATION> {
    // SAFETY: The path is terminated; no security attributes or template.
    let raw = unsafe {
        CreateFileW(
            path.as_ptr(),
            GENERIC_READ,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            null(),
            OPEN_EXISTING,
            0,
            null_mut(),
        )
    };
    if raw == INVALID_HANDLE_VALUE {
        return None;
    }
    let handle = Handle(raw);
    let wait: u32 = 0;
    let mut tag: u32 = 0;
    let mut returned = 0;
    // SAFETY: The handle is open; both buffers are u32 of the stated size and
    // the call is synchronous.
    let tagged = unsafe {
        DeviceIoControl(
            handle.0,
            IOCTL_BATTERY_QUERY_TAG,
            (&wait as *const u32).cast::<c_void>(),
            size_of::<u32>() as u32,
            (&mut tag as *mut u32).cast(),
            size_of::<u32>() as u32,
            &mut returned,
            null_mut(),
        )
    } != 0;
    if !tagged || tag == 0 {
        return None;
    }
    let query = BATTERY_QUERY_INFORMATION {
        BatteryTag: tag,
        InformationLevel: BatteryInformation,
        AtRate: 0,
    };
    let mut information = BATTERY_INFORMATION::default();
    // SAFETY: The handle is open; the input and output are the SDK structs of
    // the stated sizes and the call is synchronous.
    let read = unsafe {
        DeviceIoControl(
            handle.0,
            IOCTL_BATTERY_QUERY_INFORMATION,
            (&query as *const BATTERY_QUERY_INFORMATION).cast(),
            size_of::<BATTERY_QUERY_INFORMATION>() as u32,
            (&mut information as *mut BATTERY_INFORMATION).cast(),
            size_of::<BATTERY_INFORMATION>() as u32,
            &mut returned,
            null_mut(),
        )
    } != 0;
    read.then_some(information)
}

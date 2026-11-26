//! Managed `WindowsRazerDisplayBrightnessHidTransport`'s Windows half
//! (src/OpenBlade.Service/WindowsRazerDisplayBrightnessHidTransport.cs:175-776)
//! for the exact RZ09-0581 stack only: `RZCONTROL\VID_1532&PID_02E0&MI_00`
//! on RzCommon under its USB MI_02 interface, and the virtual Consumer
//! Control collection `RZVIRTUAL\VID_1532&PID_02E0&MI_00&COL03` on
//! RzDev_02e0 under its USB MI_01 interface, each bound to its installed,
//! catalog-trusted package (`driver_trust::package`). The native Service
//! admits no other model, so no other model's stack is listed.
//!
//! Resolution and validation only read SetupAPI, configuration manager and
//! registry state. The write opens the exact interface for GENERIC_WRITE,
//! validates the path and the virtual device again, and sends one IOCTL.
#![allow(unsafe_code)]

use std::{
    ffi::c_void,
    io,
    mem::size_of,
    os::windows::io::{FromRawHandle, OwnedHandle},
    ptr::{null, null_mut},
    sync::Arc,
};

use windows_sys::{
    Win32::{
        Devices::DeviceAndDriverInstallation::{
            CM_Get_Device_ID_Size, CM_Get_Device_IDW, CM_Get_Parent, CR_SUCCESS,
            DIGCF_DEVICEINTERFACE, DIGCF_PRESENT, HDEVINFO, SP_DEVICE_INTERFACE_DATA,
            SP_DEVICE_INTERFACE_DETAIL_DATA_W, SP_DEVINFO_DATA, SPDRP_SERVICE,
            SetupDiDestroyDeviceInfoList, SetupDiEnumDeviceInfo, SetupDiEnumDeviceInterfaces,
            SetupDiGetClassDevsW, SetupDiGetDeviceInterfaceDetailW,
            SetupDiGetDeviceRegistryPropertyW,
        },
        Foundation::{
            ERROR_INSUFFICIENT_BUFFER, ERROR_NO_MORE_ITEMS, GENERIC_WRITE, GetLastError,
            INVALID_HANDLE_VALUE,
        },
        Storage::FileSystem::{CreateFileW, FILE_SHARE_READ, FILE_SHARE_WRITE, OPEN_EXISTING},
        System::IO::DeviceIoControl,
    },
    core::GUID,
};

use super::hid::{
    Device, Log, Reached, SUBMIT_INPUT_IOCTL, Usage, WriteResult, WriteStatus, control_buffer,
    message, written,
};
use crate::driver_trust::package::{self, Package};

/// Managed `ControlInterfaceGuid` and `HidClassGuid`.
const CONTROL_INTERFACE: GUID = GUID::from_u128(0xe3be005d_d130_4910_88ff_09ae02f680e9);
const HID_CLASS: GUID = GUID::from_u128(0x745a17a0_74d3_11d0_b6fe_00a0c90f57da);
const CONTROL_SERVICE: &str = "RzCommon";
/// Managed spells the control package's hardware ID this way.
const CONTROL_HARDWARE_ID: &str = r"RAZER\DeviceContol";
/// Managed `Rz09_0581Stack`.
const CONTROL_PREFIX: &str = r"RZCONTROL\VID_1532&PID_02E0&MI_00\";
const CONTROL_ANCESTOR: &str = r"USB\VID_1532&PID_02E0&MI_02\";
const CONSUMER_PREFIX: &str = r"RZVIRTUAL\VID_1532&PID_02E0&MI_00&COL03\";
const CONSUMER_ANCESTOR: &str = r"USB\VID_1532&PID_02E0&MI_01\";
const CONSUMER_SERVICE: &str = "RzDev_02e0";
const CONSUMER_HARDWARE_ID: &str = r"RZVIRTUAL\VID_1532&PID_02E0&MI_00&COL03";
const MAXIMUM_ANCESTORS: usize = 16;
/// The devices and interfaces one enumeration reads.
const MAXIMUM_ENTRIES: u32 = 4096;
const MAXIMUM_PROPERTY: u32 = 64 * 1024;

fn wide(text: &str) -> Vec<u16> {
    text.encode_utf16().chain([0]).collect()
}

fn starts_with_ci(text: &str, prefix: &str) -> bool {
    crate::driver_trust::paths::starts_with_ci(text, prefix)
}

fn os_message(error: u32) -> String {
    message(&io::Error::from_raw_os_error(error as i32))
}

fn last_error() -> u32 {
    // SAFETY: The last error of the failed call on this thread.
    unsafe { GetLastError() }
}

/// The production device: SetupAPI, configuration manager, the installed
/// packages and the RzCommon IOCTL.
pub(super) struct Windows {
    log: Arc<Log>,
}

impl Windows {
    /// A device whose package captures `log` records.
    pub(super) fn new(log: Arc<Log>) -> Self {
        Self { log }
    }
}

impl Device for Windows {
    fn resolve(&mut self) -> io::Result<(Option<String>, WriteResult)> {
        resolve_path(&self.log)
    }

    fn write(&mut self, path: &str, usage: Usage) -> io::Result<WriteResult> {
        write_usage(path, usage, &self.log)
    }
}

fn package(log: &Log, instance: &str, service: &str, hardware_id: &str) -> Package {
    // The process-wide trust cache is shared by both lane threads; it locks
    // itself only around its own entries, never across WinVerifyTrust.
    let package = package::capture(
        &crate::driver_trust::windows::Machine,
        package::trust_cache(),
        std::time::Instant::now(),
        Some(instance),
        service,
        hardware_id,
    );
    log.package(service, &package);
    package
}

struct DeviceSet(HDEVINFO);

impl Drop for DeviceSet {
    fn drop(&mut self) {
        // SAFETY: A successful SetupDiGetClassDevsW transferred ownership here.
        unsafe { SetupDiDestroyDeviceInfoList(self.0) };
    }
}

impl DeviceSet {
    fn open(class: &GUID, flags: u32) -> io::Result<Self> {
        // SAFETY: The class GUID is valid; no enumerator or window is passed.
        let raw = unsafe { SetupDiGetClassDevsW(class, null(), null_mut(), flags) };
        if raw == INVALID_HANDLE_VALUE as HDEVINFO {
            return Err(io::Error::last_os_error());
        }
        Ok(Self(raw))
    }

    /// The interface at `index`, None after the last.
    fn interface(&self, index: u32) -> io::Result<Option<SP_DEVICE_INTERFACE_DATA>> {
        let mut data = SP_DEVICE_INTERFACE_DATA {
            cbSize: size_of::<SP_DEVICE_INTERFACE_DATA>() as u32,
            ..Default::default()
        };
        // SAFETY: The set is owned and the output carries its size.
        if unsafe {
            SetupDiEnumDeviceInterfaces(self.0, null(), &CONTROL_INTERFACE, index, &mut data)
        } == 0
        {
            return match last_error() {
                ERROR_NO_MORE_ITEMS => Ok(None),
                error => Err(io::Error::from_raw_os_error(error as i32)),
            };
        }
        Ok(Some(data))
    }

    /// Managed `GetDevicePath`, with the interface's device.
    fn path(&self, interface: &SP_DEVICE_INTERFACE_DATA) -> io::Result<(String, SP_DEVINFO_DATA)> {
        let mut required = 0u32;
        // SAFETY: A null buffer asks only for the size.
        unsafe {
            SetupDiGetDeviceInterfaceDetailW(
                self.0,
                interface,
                null_mut(),
                0,
                &mut required,
                null_mut(),
            )
        };
        let first = last_error();
        if required == 0 || required > MAXIMUM_PROPERTY {
            return Err(io::Error::from_raw_os_error(first as i32));
        }
        // u32 storage keeps the detail's alignment.
        let mut buffer = vec![0u32; (required as usize).div_ceil(4)];
        let detail = buffer
            .as_mut_ptr()
            .cast::<SP_DEVICE_INTERFACE_DETAIL_DATA_W>();
        // SAFETY: The buffer is aligned and at least the required size.
        unsafe { (*detail).cbSize = size_of::<SP_DEVICE_INTERFACE_DETAIL_DATA_W>() as u32 };
        let mut device = SP_DEVINFO_DATA {
            cbSize: size_of::<SP_DEVINFO_DATA>() as u32,
            ..Default::default()
        };
        // SAFETY: The buffer has the advertised size and the device output its size.
        if unsafe {
            SetupDiGetDeviceInterfaceDetailW(
                self.0,
                interface,
                detail,
                required,
                null_mut(),
                &mut device,
            )
        } == 0
        {
            return Err(io::Error::last_os_error());
        }
        let offset = 4 / size_of::<u16>();
        let units: &[u16] = {
            // SAFETY: The buffer holds `required` initialized bytes.
            let all = unsafe {
                std::slice::from_raw_parts(buffer.as_ptr().cast::<u16>(), required as usize / 2)
            };
            &all[offset.min(all.len())..]
        };
        let end = units
            .iter()
            .position(|unit| *unit == 0)
            .unwrap_or(units.len());
        Ok((String::from_utf16_lossy(&units[..end]), device))
    }

    fn device(&self, index: u32) -> io::Result<Option<SP_DEVINFO_DATA>> {
        let mut device = SP_DEVINFO_DATA {
            cbSize: size_of::<SP_DEVINFO_DATA>() as u32,
            ..Default::default()
        };
        // SAFETY: The set is owned and the output carries its size.
        if unsafe { SetupDiEnumDeviceInfo(self.0, index, &mut device) } == 0 {
            return match last_error() {
                ERROR_NO_MORE_ITEMS => Ok(None),
                error => Err(io::Error::from_raw_os_error(error as i32)),
            };
        }
        Ok(Some(device))
    }

    /// Managed `GetDeviceRegistryString(SPDRP_SERVICE)`.
    fn service(&self, device: &SP_DEVINFO_DATA) -> io::Result<String> {
        let mut required = 0u32;
        // SAFETY: A null buffer asks only for the size.
        unsafe {
            SetupDiGetDeviceRegistryPropertyW(
                self.0,
                device,
                SPDRP_SERVICE,
                null_mut(),
                null_mut(),
                0,
                &mut required,
            )
        };
        let first = last_error();
        if required == 0 || first != ERROR_INSUFFICIENT_BUFFER || required > MAXIMUM_PROPERTY {
            return Err(io::Error::from_raw_os_error(first as i32));
        }
        let mut buffer = vec![0u8; required as usize];
        // SAFETY: The buffer has the advertised size.
        if unsafe {
            SetupDiGetDeviceRegistryPropertyW(
                self.0,
                device,
                SPDRP_SERVICE,
                null_mut(),
                buffer.as_mut_ptr(),
                required,
                null_mut(),
            )
        } == 0
        {
            return Err(io::Error::last_os_error());
        }
        let mut units: Vec<u16> = buffer
            .as_chunks::<2>()
            .0
            .iter()
            .map(|pair| u16::from_le_bytes([pair[0], pair[1]]))
            .collect();
        while units.last() == Some(&0) {
            units.pop();
        }
        Ok(String::from_utf16_lossy(&units))
    }
}

/// Managed `GetDeviceInstanceId`; its error is managed's
/// `InvalidOperationException` message.
fn instance_id(instance: u32) -> Result<String, String> {
    let mut length = 0u32;
    // SAFETY: The output is writable.
    let status = unsafe { CM_Get_Device_ID_Size(&mut length, instance, 0) };
    if status != CR_SUCCESS {
        return Err(format!(
            "CM_Get_Device_ID_Size failed with CONFIGRET 0x{status:08X}."
        ));
    }
    let mut buffer = vec![0u16; length as usize + 1];
    // SAFETY: The buffer has the advertised capacity.
    let status =
        unsafe { CM_Get_Device_IDW(instance, buffer.as_mut_ptr(), buffer.len() as u32, 0) };
    if status != CR_SUCCESS {
        return Err(format!(
            "CM_Get_Device_ID failed with CONFIGRET 0x{status:08X}."
        ));
    }
    let end = buffer
        .iter()
        .position(|unit| *unit == 0)
        .unwrap_or(buffer.len());
    Ok(String::from_utf16_lossy(&buffer[..end]))
}

/// Managed `HasExactAncestor`: the device itself or one of its first 16
/// ancestors.
fn has_exact_ancestor(instance: u32, prefix: &str) -> Result<bool, String> {
    let mut current = instance;
    for _ in 0..MAXIMUM_ANCESTORS {
        if starts_with_ci(&instance_id(current)?, prefix) {
            return Ok(true);
        }
        let mut parent = 0;
        // SAFETY: The output is writable.
        if unsafe { CM_Get_Parent(&mut parent, current, 0) } != CR_SUCCESS {
            break;
        }
        current = parent;
    }
    Ok(false)
}

/// Managed `IsExactControlDevice`: Ok(true) admits it; Err carries the
/// identity failure, Ok(false) none.
fn is_exact_control_device(
    set: &DeviceSet,
    device: &SP_DEVINFO_DATA,
    log: &Log,
) -> Result<bool, String> {
    let instance = instance_id(device.DevInst)?;
    if !starts_with_ci(&instance, CONTROL_PREFIX) {
        return Ok(false);
    }
    let service = set.service(device).map_err(|error| message(&error))?;
    if !crate::driver_trust::paths::eq_ci(&service, CONTROL_SERVICE) {
        return Err(format!(
            "The exact Razer control device uses unexpected service '{service}'."
        ));
    }
    if !has_exact_ancestor(device.DevInst, CONTROL_ANCESTOR)? {
        return Err("The Razer control interface has no admitted physical-device ancestor.".into());
    }
    let package = package(log, &instance, CONTROL_SERVICE, CONTROL_HARDWARE_ID);
    if package.trusted() {
        return Ok(true);
    }
    Err(package
        .failure()
        .unwrap_or("The Razer control driver installed package is missing or not trusted.")
        .to_owned())
}

/// Managed `ResolveDevicePath(stack)` for the one stack.
fn resolve_path(log: &Log) -> io::Result<(Option<String>, WriteResult)> {
    let set = DeviceSet::open(&CONTROL_INTERFACE, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE)?;
    let mut exact_observed = false;
    let mut exact_failure = None;
    let mut inspection_failure = None;
    for index in 0..MAXIMUM_ENTRIES {
        let Some(interface) = set.interface(index)? else {
            break;
        };
        let (path, device) = match set.path(&interface) {
            Ok(found) => found,
            Err(error) => {
                inspection_failure = Some(message(&error));
                continue;
            }
        };
        match is_exact_control_device(&set, &device, log) {
            Ok(true) => {
                let consumer = validate_virtual_consumer_device(log);
                if consumer.status != WriteStatus::Applied {
                    return Ok((None, consumer));
                }
                return Ok((Some(path), WriteResult::applied()));
            }
            result => {
                let failure = result.err();
                if failure.is_some() {
                    inspection_failure.clone_from(&failure);
                }
                if instance_id(device.DevInst).is_ok_and(|id| starts_with_ci(&id, CONTROL_PREFIX)) {
                    exact_observed = true;
                    exact_failure = failure;
                }
            }
        }
    }
    Ok(if exact_observed {
        (
            None,
            WriteResult::failed(Some(exact_failure.unwrap_or_else(|| {
                "The exact Razer control interface failed identity validation.".into()
            }))),
        )
    } else {
        match inspection_failure {
            None => (None, WriteResult::unavailable(None)),
            failure => (None, WriteResult::failed(failure)),
        }
    })
}

/// Managed `ValidateVirtualConsumerDevice`: Applied admits the device. An
/// untrusted package is Unavailable, as managed reports it.
fn validate_virtual_consumer_device(log: &Log) -> WriteResult {
    let set = match DeviceSet::open(&HID_CLASS, DIGCF_PRESENT) {
        Ok(set) => set,
        Err(error) => return WriteResult::failed(Some(message(&error))),
    };
    let mut exact_observed = false;
    let mut inspection_failure = None;
    for index in 0..MAXIMUM_ENTRIES {
        let device = match set.device(index) {
            Ok(Some(device)) => device,
            Ok(None) => break,
            Err(error) => return WriteResult::failed(Some(message(&error))),
        };
        let instance = match instance_id(device.DevInst) {
            Ok(instance) => instance,
            Err(error) => {
                inspection_failure = Some(error);
                continue;
            }
        };
        if !starts_with_ci(&instance, CONSUMER_PREFIX) {
            continue;
        }
        exact_observed = true;
        let service = match set.service(&device) {
            Ok(service) => service,
            Err(error) => {
                inspection_failure = Some(message(&error));
                continue;
            }
        };
        // As managed: the ancestor is walked only for the exact service.
        if !crate::driver_trust::paths::eq_ci(&service, CONSUMER_SERVICE) {
            continue;
        }
        match has_exact_ancestor(device.DevInst, CONSUMER_ANCESTOR) {
            Ok(true) => {
                let package = package(log, &instance, CONSUMER_SERVICE, CONSUMER_HARDWARE_ID);
                return if package.trusted() {
                    WriteResult::applied()
                } else {
                    WriteResult::unavailable(Some(
                        package
                            .failure()
                            .unwrap_or(
                                "The Razer Consumer Control driver installed package is missing or not trusted.",
                            )
                            .to_owned(),
                    ))
                };
            }
            Ok(_) => {}
            Err(error) => inspection_failure = Some(error),
        }
    }
    if exact_observed || inspection_failure.is_some() {
        WriteResult::failed(Some(inspection_failure.unwrap_or_else(|| {
            "The exact Razer virtual Consumer Control device failed identity validation.".into()
        })))
    } else {
        WriteResult::unavailable(None)
    }
}

/// Managed `ValidateExactPath` for the one stack.
fn validate_exact_path(path: &str, log: &Log) -> Result<(), String> {
    let set = DeviceSet::open(&CONTROL_INTERFACE, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE)
        .map_err(|error| message(&error))?;
    for index in 0..MAXIMUM_ENTRIES {
        let interface = match set.interface(index) {
            Ok(Some(interface)) => interface,
            Ok(None) => break,
            Err(error) => return Err(message(&error)),
        };
        let Ok((current, device)) = set.path(&interface) else {
            continue;
        };
        if !crate::driver_trust::paths::eq_ci(path, &current) {
            continue;
        }
        return match is_exact_control_device(&set, &device, log) {
            Ok(true) => Ok(()),
            Ok(false) => {
                Err("The cached Razer control path does not belong to an admitted device.".into())
            }
            Err(failure) => Err(failure),
        };
    }
    Err("The cached Razer interface path is no longer present.".into())
}

/// Managed `WriteUsageToPath`.
fn write_usage(path: &str, usage: Usage, log: &Log) -> io::Result<WriteResult> {
    Ok(written(reach(path, usage, log)))
}

/// How far one write gets (`hid::written` classifies it).
fn reach(path: &str, usage: Usage, log: &Log) -> Reached {
    let name = wide(path);
    // SAFETY: The path is terminated; no security or template is passed.
    let raw = unsafe {
        CreateFileW(
            name.as_ptr(),
            GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            null(),
            OPEN_EXISTING,
            0,
            null_mut(),
        )
    };
    if raw == INVALID_HANDLE_VALUE {
        let error = last_error();
        return Reached::Open {
            error,
            message: os_message(error),
        };
    }
    // SAFETY: A successful CreateFileW transferred ownership here.
    let handle = unsafe { OwnedHandle::from_raw_handle(raw) };
    if let Err(failure) = validate_exact_path(path, log) {
        return Reached::Path(failure);
    }
    let consumer = validate_virtual_consumer_device(log);
    if consumer.status != WriteStatus::Applied {
        return Reached::Consumer(consumer);
    }
    let buffer = control_buffer(usage);
    let mut returned = 0u32;
    // SAFETY: The handle is open for writing, the input buffer has the
    // advertised length, and no output or overlapped structure is passed.
    let submitted = unsafe {
        DeviceIoControl(
            std::os::windows::io::AsRawHandle::as_raw_handle(&handle),
            SUBMIT_INPUT_IOCTL,
            buffer.as_ptr().cast::<c_void>(),
            buffer.len() as u32,
            null_mut(),
            0,
            &mut returned,
            null_mut(),
        )
    };
    Reached::Ioctl((submitted == 0).then(|| os_message(last_error())))
}

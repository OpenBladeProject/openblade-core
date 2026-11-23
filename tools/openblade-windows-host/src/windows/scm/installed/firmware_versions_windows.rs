//! The Windows metadata reads behind managed `WindowsFirmwareVersionSource`
//! and `WindowsDeviceFirmwareVersionReader`: registry values, the USB4 root
//! router's device property and the touchpad's HID attributes. All of them
//! are read-only. The HID handles are opened with no access, so no report
//! can be read or written through them, and only for interfaces whose
//! hardware IDs name the touchpad. Interface enumeration is the discovery
//! scan's (`discovery::windows::hid_interfaces`).
#![allow(unsafe_code)]

use super::{Capture, HidInterface, HidPath, Router, Value, VideoInstance};
use crate::discovery::windows::hid_interfaces;
use std::{
    io,
    mem::size_of,
    os::windows::io::{AsRawHandle, FromRawHandle, OwnedHandle},
    ptr::{null, null_mut},
    sync::atomic::AtomicBool,
};
use windows_sys::{
    Win32::{
        Devices::{
            DeviceAndDriverInstallation::{
                DIGCF_ALLCLASSES, DIGCF_PRESENT, HDEVINFO, SP_DEVINFO_DATA,
                SetupDiDestroyDeviceInfoList, SetupDiEnumDeviceInfo, SetupDiGetClassDevsW,
                SetupDiGetDeviceInstanceIdW, SetupDiGetDevicePropertyW,
            },
            HumanInterfaceDevice::{
                HIDD_ATTRIBUTES, HIDP_CAPS, HidD_FreePreparsedData, HidD_GetAttributes,
                HidD_GetPreparsedData, HidP_GetCaps, PHIDP_PREPARSED_DATA,
            },
            Properties::{
                DEVPKEY_Device_HardwareIds, DEVPKEY_Device_Parent, DEVPKEY_Device_Service,
            },
        },
        Foundation::{
            DEVPROPKEY, ERROR_FILE_NOT_FOUND, ERROR_INSUFFICIENT_BUFFER, ERROR_NO_MORE_ITEMS,
            ERROR_PATH_NOT_FOUND, ERROR_SUCCESS, GetLastError, INVALID_HANDLE_VALUE,
        },
        Storage::FileSystem::{CreateFileW, FILE_SHARE_READ, FILE_SHARE_WRITE, OPEN_EXISTING},
        System::Registry::{
            HKEY, HKEY_LOCAL_MACHINE, KEY_READ, KEY_WOW64_64KEY, REG_DWORD, REG_MULTI_SZ,
            REG_QWORD, REG_SZ, RRF_RT_ANY, RegCloseKey, RegEnumKeyExW, RegGetValueW, RegOpenKeyExW,
        },
    },
    core::GUID,
};

const VIDEO_KEY: &str = r"SYSTEM\CurrentControlSet\Control\Video";
/// The longest registry value read; a firmware string is far shorter.
const MAX_VALUE_BYTES: usize = 64 * 1024;
/// Registry key names are at most 255 characters.
const MAX_KEY_NAME: usize = 256;
/// Managed enumerates every key; a laptop has a few adapters.
const MAX_ADAPTERS: u32 = 64;
const MAX_INSTANCES: u32 = 64;
/// The USB4 devices of one laptop.
const MAX_ROUTERS: u32 = 256;
const MAX_INSTANCE_ID: usize = 1024;
const MAX_PROPERTY_BYTES: usize = 8 * 1024;
/// `DEVPROP_TYPE_STRING` and `DEVPROP_TYPE_STRING_LIST`.
const PROPERTY_STRING: u32 = 0x12;
const PROPERTY_STRING_LIST: u32 = 0x2012;
/// Managed `ReadFirmwareVersion` reads `DEVPKEY` {540b947e-...}, 18.
const FIRMWARE_VERSION_KEY: DEVPROPKEY = DEVPROPKEY {
    fmtid: GUID::from_u128(0x540b947e_8b40_45bc_a8a2_6a0b894cbda2),
    pid: 18,
};

pub(in super::super) struct Windows;

impl Capture for Windows {
    fn registry(&self, key: &str, name: &str) -> io::Result<Value> {
        match Key::open(HKEY_LOCAL_MACHINE, key)? {
            Some(key) => key.value(name),
            None => Ok(Value::Missing),
        }
    }

    fn video_adapters(&self) -> io::Result<Vec<Vec<VideoInstance>>> {
        let Some(root) = Key::open(HKEY_LOCAL_MACHINE, VIDEO_KEY)? else {
            return Ok(Vec::new());
        };
        let mut adapters = Vec::new();
        for adapter in root.subkey_names(MAX_ADAPTERS)? {
            let Some(adapter) = root.child(&adapter)? else {
                continue;
            };
            let mut instances = Vec::new();
            for instance in adapter.subkey_names(MAX_INSTANCES)? {
                let Some(instance) = adapter.child(&instance)? else {
                    continue;
                };
                instances.push(VideoInstance {
                    adapter: instance.value("HardwareInformation.AdapterString")?,
                    bios: instance.value("HardwareInformation.BiosString")?,
                });
            }
            adapters.push(instances);
        }
        Ok(adapters)
    }

    fn thunderbolt_routers(&self) -> io::Result<Vec<Router>> {
        // SAFETY: The enumerator text is terminated and no class GUID or window
        // is passed.
        let raw = unsafe {
            SetupDiGetClassDevsW(
                null(),
                wide("USB4").as_ptr(),
                null_mut(),
                DIGCF_PRESENT | DIGCF_ALLCLASSES,
            )
        };
        if raw == INVALID_HANDLE_VALUE as HDEVINFO {
            return Err(io::Error::last_os_error());
        }
        let set = DeviceInfoSet(raw);
        let mut routers = Vec::new();
        for index in 0..=MAX_ROUTERS {
            let mut device = SP_DEVINFO_DATA {
                cbSize: size_of::<SP_DEVINFO_DATA>() as u32,
                ..Default::default()
            };
            // SAFETY: The set is owned here and the output carries its size.
            if unsafe { SetupDiEnumDeviceInfo(set.0, index, &mut device) } == 0 {
                // SAFETY: The last error of the failed call on this thread.
                return if unsafe { GetLastError() } == ERROR_NO_MORE_ITEMS {
                    Ok(routers)
                } else {
                    Err(io::Error::last_os_error())
                };
            }
            if index == MAX_ROUTERS {
                break;
            }
            let Some(instance_id) = set.instance_id(&device) else {
                continue;
            };
            // Managed reads the properties only of the root router's instances.
            if !instance_id
                .to_ascii_uppercase()
                .starts_with(&format!("{}\\", super::THUNDERBOLT_ROUTER))
            {
                continue;
            }
            routers.push(Router {
                firmware_version: set
                    .property(&device, &FIRMWARE_VERSION_KEY, PROPERTY_STRING)
                    .map(|bytes| decode_string_property(&bytes)),
                hardware_ids: set
                    .property(&device, &DEVPKEY_Device_HardwareIds, PROPERTY_STRING_LIST)
                    .map(|bytes| decode_list_property(&bytes))
                    .unwrap_or_default(),
                service: set
                    .property(&device, &DEVPKEY_Device_Service, PROPERTY_STRING)
                    .map(|bytes| decode_string_property(&bytes)),
                parent: set
                    .property(&device, &DEVPKEY_Device_Parent, PROPERTY_STRING)
                    .map(|bytes| decode_string_property(&bytes)),
                instance_id,
            });
        }
        Err(io::Error::other("too many USB4 devices"))
    }

    fn hid_paths(&self) -> io::Result<Vec<HidPath>> {
        // The discovery scan already reads each interface's path and devnode
        // hardware IDs without opening it.
        Ok(hid_interfaces(&AtomicBool::new(false))?
            .into_iter()
            .map(|(path, hardware_ids)| HidPath { path, hardware_ids })
            .collect())
    }

    fn hid_attributes(&self, path: &str) -> Option<HidInterface> {
        describe(&wide(path))
    }
}

fn wide(text: &str) -> Vec<u16> {
    text.encode_utf16().chain([0]).collect()
}

/// A registry key opened for reading in the 64-bit view, as .NET's
/// `Registry.LocalMachine` opens it in a 64-bit process.
struct Key(HKEY);

impl Drop for Key {
    fn drop(&mut self) {
        // SAFETY: A successful RegOpenKeyExW transferred ownership here.
        unsafe { RegCloseKey(self.0) };
    }
}

impl Key {
    /// The subkey, or None when it does not exist.
    fn open(parent: HKEY, path: &str) -> io::Result<Option<Self>> {
        let mut raw = null_mut();
        // SAFETY: The path is terminated and the output is writable.
        let status = unsafe {
            RegOpenKeyExW(
                parent,
                wide(path).as_ptr(),
                0,
                KEY_READ | KEY_WOW64_64KEY,
                &mut raw,
            )
        };
        match status {
            ERROR_SUCCESS => Ok(Some(Self(raw))),
            ERROR_FILE_NOT_FOUND | ERROR_PATH_NOT_FOUND => Ok(None),
            error => Err(io::Error::from_raw_os_error(error as i32)),
        }
    }

    fn child(&self, name: &str) -> io::Result<Option<Self>> {
        Self::open(self.0, name)
    }

    /// At most `limit` subkey names; more is an error rather than a guess.
    fn subkey_names(&self, limit: u32) -> io::Result<Vec<String>> {
        let mut names = Vec::new();
        for index in 0..=limit {
            let mut buffer = [0u16; MAX_KEY_NAME];
            let mut length = buffer.len() as u32;
            // SAFETY: The buffer has the advertised capacity; the optional
            // outputs are null.
            let status = unsafe {
                RegEnumKeyExW(
                    self.0,
                    index,
                    buffer.as_mut_ptr(),
                    &mut length,
                    null(),
                    null_mut(),
                    null_mut(),
                    null_mut(),
                )
            };
            match status {
                ERROR_SUCCESS if index == limit => {
                    return Err(io::Error::other("too many registry subkeys"));
                }
                ERROR_SUCCESS => names.push(String::from_utf16_lossy(&buffer[..length as usize])),
                _ if status == ERROR_NO_MORE_ITEMS => return Ok(names),
                error => return Err(io::Error::from_raw_os_error(error as i32)),
            }
        }
        unreachable!("the loop returns at the limit")
    }

    /// `RegistryKey.GetValue`: a missing value is `Missing`, and a type the
    /// managed code does not read, or one too large, is `Other`.
    fn value(&self, name: &str) -> io::Result<Value> {
        let name = wide(name);
        let mut kind = 0;
        let mut size = 0u32;
        // SAFETY: A null buffer asks only for the size.
        let status = unsafe {
            RegGetValueW(
                self.0,
                null(),
                name.as_ptr(),
                RRF_RT_ANY,
                &mut kind,
                null_mut(),
                &mut size,
            )
        };
        match status {
            ERROR_SUCCESS => {}
            ERROR_FILE_NOT_FOUND | ERROR_PATH_NOT_FOUND => return Ok(Value::Missing),
            error => return Err(io::Error::from_raw_os_error(error as i32)),
        }
        if size as usize > MAX_VALUE_BYTES {
            return Ok(Value::Other);
        }
        let mut data = vec![0u8; size as usize];
        // SAFETY: The buffer has the advertised size.
        let status = unsafe {
            RegGetValueW(
                self.0,
                null(),
                name.as_ptr(),
                RRF_RT_ANY,
                &mut kind,
                data.as_mut_ptr().cast(),
                &mut size,
            )
        };
        match status {
            ERROR_SUCCESS => {}
            ERROR_FILE_NOT_FOUND | ERROR_PATH_NOT_FOUND => return Ok(Value::Missing),
            // The value grew between the two calls: it is not read.
            _ => return Ok(Value::Other),
        }
        data.truncate(size as usize);
        Ok(decode_value(kind, &data))
    }
}

/// A registry value of `kind` as managed sees it.
fn decode_value(kind: u32, data: &[u8]) -> Value {
    match kind {
        REG_SZ => Value::Text(decode_registry_text(data)),
        REG_MULTI_SZ => Value::Multi(decode_multi(data)),
        REG_DWORD => data.try_into().map_or(Value::Other, |bytes| {
            Value::Number(i64::from(i32::from_le_bytes(bytes)))
        }),
        REG_QWORD => data.try_into().map_or(Value::Other, |bytes| {
            Value::Number(i64::from_le_bytes(bytes))
        }),
        _ => Value::Other,
    }
}

fn units(data: &[u8]) -> Vec<u16> {
    data.as_chunks::<2>()
        .0
        .iter()
        .map(|pair| u16::from_le_bytes(*pair))
        .collect()
}

/// .NET removes one trailing terminator from a REG_SZ.
fn decode_registry_text(data: &[u8]) -> String {
    let mut text = units(data);
    if text.last() == Some(&0) {
        text.pop();
    }
    String::from_utf16_lossy(&text)
}

/// REG_MULTI_SZ strings; trailing empty entries are dropped.
fn decode_multi(data: &[u8]) -> Vec<String> {
    let mut text = units(data);
    while text.last() == Some(&0) {
        text.pop();
    }
    if text.is_empty() {
        return Vec::new();
    }
    text.split(|unit| *unit == 0)
        .map(String::from_utf16_lossy)
        .collect()
}

/// Managed `Marshal.PtrToStringUni(...).TrimEnd('\0')`: the text up to the
/// first terminator.
fn decode_string_property(data: &[u8]) -> String {
    let text = units(data);
    let end = text
        .iter()
        .position(|unit| *unit == 0)
        .unwrap_or(text.len());
    String::from_utf16_lossy(&text[..end])
}

/// Managed `ReadStringListProperty`: the non-blank entries, trimmed.
fn decode_list_property(data: &[u8]) -> Vec<String> {
    units(data)
        .split(|unit| *unit == 0)
        .map(String::from_utf16_lossy)
        .filter(|item| !item.trim().is_empty())
        .map(|item| item.trim().to_owned())
        .collect()
}

struct DeviceInfoSet(HDEVINFO);

impl Drop for DeviceInfoSet {
    fn drop(&mut self) {
        // SAFETY: A successful SetupDiGetClassDevsW transferred ownership here.
        unsafe { SetupDiDestroyDeviceInfoList(self.0) };
    }
}

impl DeviceInfoSet {
    fn instance_id(&self, device: &SP_DEVINFO_DATA) -> Option<String> {
        let mut buffer = [0u16; MAX_INSTANCE_ID];
        // SAFETY: The device belongs to the set and the buffer has the
        // advertised capacity.
        let read = unsafe {
            SetupDiGetDeviceInstanceIdW(
                self.0,
                device,
                buffer.as_mut_ptr(),
                buffer.len() as u32,
                null_mut(),
            )
        };
        let end = buffer.iter().position(|unit| *unit == 0)?;
        (read != 0).then(|| String::from_utf16_lossy(&buffer[..end]))
    }

    /// A device property of exactly `expected` type, or None when it is
    /// absent, of another type or larger than a property can be.
    fn property(
        &self,
        device: &SP_DEVINFO_DATA,
        key: &DEVPROPKEY,
        expected: u32,
    ) -> Option<Vec<u8>> {
        let mut kind = 0;
        let mut required = 0;
        // SAFETY: A null buffer asks only for the size; the key is valid.
        let sized = unsafe {
            SetupDiGetDevicePropertyW(
                self.0,
                device,
                key,
                &mut kind,
                null_mut(),
                0,
                &mut required,
                0,
            )
        };
        // SAFETY: The last error of the call above on this thread.
        let error = unsafe { GetLastError() };
        if sized != 0 || required == 0 || error != ERROR_INSUFFICIENT_BUFFER {
            return None;
        }
        if required as usize > MAX_PROPERTY_BYTES {
            return None;
        }
        let mut data = vec![0u8; required as usize];
        // SAFETY: The buffer has the advertised size.
        let read = unsafe {
            SetupDiGetDevicePropertyW(
                self.0,
                device,
                key,
                &mut kind,
                data.as_mut_ptr(),
                required,
                &mut required,
                0,
            )
        };
        (read != 0 && kind == expected).then_some(data)
    }
}

struct Preparsed(PHIDP_PREPARSED_DATA);

impl Drop for Preparsed {
    fn drop(&mut self) {
        // SAFETY: HidD_GetPreparsedData returned this pointer, freed once.
        unsafe { HidD_FreePreparsedData(self.0) };
    }
}

/// Managed `WindowsHidEnumerator.FindCollections` for one interface: the
/// path opened with no access and shared for reading and writing, then its
/// attributes and top-level usage. None skips an interface that cannot be
/// opened or described, as managed does.
fn describe(path: &[u16]) -> Option<HidInterface> {
    // SAFETY: The path is terminated; there are no security attributes or
    // template. Access 0 grants no read or write of any report.
    let raw = unsafe {
        CreateFileW(
            path.as_ptr(),
            0,
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
    // SAFETY: CreateFileW returned an owned file handle, closed on drop.
    let handle = unsafe { OwnedHandle::from_raw_handle(raw) };
    let raw = handle.as_raw_handle();
    let mut attributes = HIDD_ATTRIBUTES {
        Size: size_of::<HIDD_ATTRIBUTES>() as u32,
        ..Default::default()
    };
    // SAFETY: The handle is open for the call; the output is initialized.
    if !unsafe { HidD_GetAttributes(raw, &mut attributes) } {
        return None;
    }
    let mut preparsed = 0;
    // SAFETY: The handle is open and the output is writable.
    if !unsafe { HidD_GetPreparsedData(raw, &mut preparsed) } || preparsed == 0 {
        return None;
    }
    let preparsed = Preparsed(preparsed);
    let mut caps = HIDP_CAPS::default();
    // SAFETY: Preparsed owns this pointer through the call.
    if unsafe { HidP_GetCaps(preparsed.0, &mut caps) } < 0 {
        return None;
    }
    Some(HidInterface {
        vendor_id: attributes.VendorID,
        product_id: attributes.ProductID,
        usage_page: caps.UsagePage,
        usage: caps.Usage,
        version: attributes.VersionNumber,
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    fn bytes(text: &[u16]) -> Vec<u8> {
        text.iter().flat_map(|unit| unit.to_le_bytes()).collect()
    }

    fn utf16(text: &str) -> Vec<u16> {
        text.encode_utf16().collect()
    }

    /// Managed reads a REG_SZ as its text without one trailing terminator, a
    /// DWORD as a signed int and a QWORD as a long. Any other type, or a
    /// numeric of the wrong width, is not a value the managed code reads.
    #[test]
    fn registry_values_decode_as_managed_reads_them() {
        let mut text = utf16("3.01");
        text.push(0);
        assert_eq!(
            decode_value(REG_SZ, &bytes(&text)),
            Value::Text("3.01".into())
        );
        assert_eq!(
            decode_value(REG_SZ, &bytes(&utf16("3.01"))),
            Value::Text("3.01".into())
        );
        assert_eq!(
            decode_value(REG_DWORD, &0xffff_ffffu32.to_le_bytes()),
            Value::Number(-1)
        );
        assert_eq!(
            decode_value(REG_DWORD, &7u32.to_le_bytes()),
            Value::Number(7)
        );
        assert_eq!(decode_value(REG_DWORD, &[1, 2]), Value::Other);
        assert_eq!(
            decode_value(REG_QWORD, &(1i64 << 40).to_le_bytes()),
            Value::Number(1 << 40)
        );
        assert_eq!(decode_value(REG_QWORD, &[0; 4]), Value::Other);
        // REG_BINARY and REG_NONE.
        assert_eq!(decode_value(3, &[1, 2, 3]), Value::Other);
        assert_eq!(decode_value(0, &[]), Value::Other);
    }

    #[test]
    fn multi_strings_drop_only_trailing_empty_entries() {
        let mut text = utf16("A");
        text.extend([0]);
        text.extend(utf16("B"));
        text.extend([0, 0, 0]);
        assert_eq!(
            decode_value(REG_MULTI_SZ, &bytes(&text)),
            Value::Multi(vec!["A".into(), "B".into()])
        );
        let mut middle = utf16("A");
        middle.extend([0, 0]);
        middle.extend(utf16("B"));
        middle.extend([0, 0]);
        assert_eq!(
            decode_multi(&bytes(&middle)),
            vec!["A".to_owned(), String::new(), "B".to_owned()]
        );
        assert_eq!(decode_multi(&bytes(&[0, 0])), Vec::<String>::new());
    }

    /// A string property is its text up to the first terminator; a list is
    /// its non-blank entries, trimmed.
    #[test]
    fn device_properties_decode_as_managed_reads_them() {
        let mut text = utf16("1.2.3");
        text.extend([0, u16::from(b'x')]);
        assert_eq!(decode_string_property(&bytes(&text)), "1.2.3");
        let mut list = utf16(" A ");
        list.extend([0, 0]);
        list.extend(utf16("   "));
        list.extend([0]);
        list.extend(utf16("B"));
        list.extend([0, 0]);
        assert_eq!(decode_list_property(&bytes(&list)), vec!["A", "B"]);
    }
}

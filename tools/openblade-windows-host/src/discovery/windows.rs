#![allow(unsafe_code)]

use super::{
    DiscoverySource, HostObservation, Interface, MAX_INTERFACES, MAX_REGISTRY_TEXT, Platform,
    RegistrySource, RegistryText, ScanState, check_cancelled, discover, pnp,
};
use std::{
    io,
    mem::{offset_of, size_of},
    ptr::{null, null_mut},
    sync::atomic::{AtomicBool, Ordering},
};
use windows_sys::{
    Wdk::System::SystemServices::RtlGetVersion,
    Win32::{
        Devices::{
            DeviceAndDriverInstallation::{
                DIGCF_DEVICEINTERFACE, DIGCF_PRESENT, HDEVINFO, SP_DEVICE_INTERFACE_DATA,
                SP_DEVICE_INTERFACE_DETAIL_DATA_W, SP_DEVINFO_DATA, SPDRP_HARDWAREID,
                SetupDiDestroyDeviceInfoList, SetupDiEnumDeviceInterfaces, SetupDiGetClassDevsW,
                SetupDiGetDeviceInterfaceDetailW, SetupDiGetDeviceRegistryPropertyW,
            },
            HumanInterfaceDevice::HidD_GetHidGuid,
        },
        Foundation::{
            ERROR_FILE_NOT_FOUND, ERROR_MORE_DATA, ERROR_NO_MORE_ITEMS, ERROR_PATH_NOT_FOUND,
            ERROR_SUCCESS, ERROR_UNSUPPORTED_TYPE, GetLastError, INVALID_HANDLE_VALUE,
        },
        System::{
            Console::{CTRL_BREAK_EVENT, CTRL_C_EVENT, SetConsoleCtrlHandler},
            Registry::{
                HKEY_LOCAL_MACHINE, REG_MULTI_SZ, RRF_NOEXPAND, RRF_RT_REG_EXPAND_SZ,
                RRF_RT_REG_SZ, RRF_SUBKEY_WOW6464KEY, RegGetValueW,
            },
            SystemInformation::{IMAGE_FILE_MACHINE_AMD64, OSVERSIONINFOW},
            Threading::{GetCurrentProcess, IsWow64Process2},
        },
    },
    core::GUID,
};

const BIOS_KEY: &str = r"HARDWARE\DESCRIPTION\System\BIOS";
const SYSTEM_INFORMATION_KEY: &str = r"SYSTEM\CurrentControlSet\Control\SystemInformation";
const CURRENT_VERSION_KEY: &str = r"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
const MAX_INTERFACE_PATH: usize = 2048;
const MAX_HARDWARE_ID_UNITS: usize = 4096;

static CANCELLED: AtomicBool = AtomicBool::new(false);

pub(crate) fn run() -> io::Result<()> {
    if std::env::args_os().len() != 1 {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "discovery takes no arguments",
        ));
    }
    // SAFETY: The callback only sets an atomic flag and uses the required system ABI.
    if unsafe { SetConsoleCtrlHandler(Some(cancel), 1) } == 0 {
        return Err(io::Error::last_os_error());
    }
    let snapshot = discover(&WindowsMetadata, &CANCELLED)?;
    check_cancelled(&CANCELLED)?;
    use io::Write;
    writeln!(io::stdout().lock(), "{snapshot}")
}

unsafe extern "system" fn cancel(event: u32) -> i32 {
    if matches!(event, CTRL_C_EVENT | CTRL_BREAK_EVENT) {
        CANCELLED.store(true, Ordering::Relaxed);
        1
    } else {
        0
    }
}

struct WindowsMetadata;

impl DiscoverySource for WindowsMetadata {
    fn read_host(&self, cancelled: &AtomicBool) -> io::Result<HostObservation> {
        let platform = platform(registry_text(CURRENT_VERSION_KEY, "InstallationType"));
        let read = |key, value| {
            check_cancelled(cancelled)?;
            Ok::<_, io::Error>(registry_text(key, value))
        };
        let source = |key| {
            Ok::<_, io::Error>(RegistrySource {
                manufacturer: read(key, "SystemManufacturer")?,
                product_name: read(key, "SystemProductName")?,
            })
        };
        Ok(HostObservation {
            platform,
            bios: source(BIOS_KEY)?,
            system_information: source(SYSTEM_INFORMATION_KEY)?,
            sku: read(BIOS_KEY, "SystemSKU")?,
            bios_version: read(BIOS_KEY, "BIOSVersion")?,
        })
    }

    fn scan_hid(
        &self,
        cancelled: &AtomicBool,
        mut visit: impl FnMut(Option<Interface>) -> io::Result<()>,
    ) -> io::Result<ScanState> {
        check_cancelled(cancelled)?;
        let mut class = GUID::from_u128(0);
        // SAFETY: The GUID output is writable. The two optional inputs are null.
        let raw = unsafe {
            HidD_GetHidGuid(&mut class);
            SetupDiGetClassDevsW(
                &class,
                null(),
                null_mut(),
                DIGCF_PRESENT | DIGCF_DEVICEINTERFACE,
            )
        };
        if raw == INVALID_HANDLE_VALUE as HDEVINFO {
            return Ok(ScanState::Unavailable);
        }
        let set = DeviceInfoSet(raw);
        // Probe one extra index so an exact-size scan can still report completeness.
        for index in 0..=MAX_INTERFACES {
            check_cancelled(cancelled)?;
            let mut interface = SP_DEVICE_INTERFACE_DATA {
                cbSize: size_of::<SP_DEVICE_INTERFACE_DATA>() as u32,
                ..Default::default()
            };
            // SAFETY: The set is owned for the loop, the class GUID is valid, and the
            // initialized output struct supplies its documented size.
            if unsafe {
                SetupDiEnumDeviceInterfaces(set.0, null(), &class, index as u32, &mut interface)
            } == 0
            {
                // SAFETY: Read the last error on the same thread immediately after failure.
                return Ok(if unsafe { GetLastError() } == ERROR_NO_MORE_ITEMS {
                    ScanState::Complete
                } else {
                    ScanState::Incomplete
                });
            }
            if index == MAX_INTERFACES {
                break;
            }
            visit(set.interface(&interface, cancelled)?)?;
        }
        Ok(ScanState::Incomplete)
    }
}

fn platform(installation_type: RegistryText) -> Platform {
    match installation_type.value() {
        Some("Client") => {}
        Some(_) => return Platform::Unsupported,
        None => return Platform::Unavailable,
    }

    let mut version = OSVERSIONINFOW {
        dwOSVersionInfoSize: size_of::<OSVERSIONINFOW>() as u32,
        ..Default::default()
    };
    let mut native = 0;
    let mut process = 0;
    // SAFETY: The version and machine outputs are writable. GetCurrentProcess
    // provides a pseudo-handle that does not require closing.
    let succeeded = unsafe {
        RtlGetVersion(&mut version) == 0
            && IsWow64Process2(GetCurrentProcess(), &mut process, &mut native) != 0
    };
    if !succeeded {
        Platform::Unavailable
    } else if native == IMAGE_FILE_MACHINE_AMD64
        && version.dwMajorVersion == 10
        && version.dwMinorVersion == 0
        && version.dwBuildNumber >= 22000
    {
        Platform::Supported
    } else {
        Platform::Unsupported
    }
}

fn registry_text(key: &str, value: &str) -> RegistryText {
    let key: Vec<u16> = key.encode_utf16().chain([0]).collect();
    let value: Vec<u16> = value.encode_utf16().chain([0]).collect();
    let mut buffer = [0u16; MAX_REGISTRY_TEXT + 1];
    let mut bytes = size_of_val(&buffer) as u32;
    // SAFETY: The input strings are terminated. The aligned output has exactly
    // the advertised capacity. The predefined HKLM root does not need closing.
    let result = unsafe {
        RegGetValueW(
            HKEY_LOCAL_MACHINE,
            key.as_ptr(),
            value.as_ptr(),
            RRF_RT_REG_SZ | RRF_RT_REG_EXPAND_SZ | RRF_NOEXPAND | RRF_SUBKEY_WOW6464KEY,
            null_mut(),
            buffer.as_mut_ptr().cast(),
            &mut bytes,
        )
    };
    match result {
        ERROR_SUCCESS => {
            wide_bytes(&buffer, bytes).map_or(RegistryText::Malformed, RegistryText::from_utf16)
        }
        ERROR_FILE_NOT_FOUND | ERROR_PATH_NOT_FOUND => RegistryText::Missing,
        ERROR_MORE_DATA | ERROR_UNSUPPORTED_TYPE => RegistryText::Malformed,
        _ => RegistryText::Unavailable,
    }
}

fn wide_bytes(buffer: &[u16], bytes: u32) -> Option<&[u16]> {
    let bytes = bytes as usize;
    if !bytes.is_multiple_of(size_of::<u16>()) {
        return None;
    }
    buffer.get(..bytes / size_of::<u16>())
}

struct DeviceInfoSet(HDEVINFO);

impl Drop for DeviceInfoSet {
    fn drop(&mut self) {
        // SAFETY: A successful SetupDiGetClassDevsW transferred ownership to this value.
        unsafe { SetupDiDestroyDeviceInfoList(self.0) };
    }
}

#[repr(C)]
struct InterfaceDetail {
    cb_size: u32,
    path: [u16; MAX_INTERFACE_PATH],
}

impl DeviceInfoSet {
    fn interface(
        &self,
        interface: &SP_DEVICE_INTERFACE_DATA,
        cancelled: &AtomicBool,
    ) -> io::Result<Option<Interface>> {
        check_cancelled(cancelled)?;
        let mut detail = InterfaceDetail {
            cb_size: size_of::<SP_DEVICE_INTERFACE_DETAIL_DATA_W>() as u32,
            path: [0; MAX_INTERFACE_PATH],
        };
        let mut device = SP_DEVINFO_DATA {
            cbSize: size_of::<SP_DEVINFO_DATA>() as u32,
            ..Default::default()
        };
        let mut bytes = 0;
        // SAFETY: The aligned detail buffer has the SDK field layout (DWORD
        // followed by UTF-16), supplies the platform-specific cbSize, and is
        // writable for the full advertised capacity. The set owns the interface.
        let read = unsafe {
            SetupDiGetDeviceInterfaceDetailW(
                self.0,
                interface,
                (&mut detail as *mut InterfaceDetail).cast(),
                size_of::<InterfaceDetail>() as u32,
                &mut bytes,
                &mut device,
            )
        };
        if read == 0 {
            return Ok(None);
        }
        let Some(path) = bytes
            .checked_sub(offset_of!(InterfaceDetail, path) as u32)
            .and_then(|bytes| wide_bytes(&detail.path, bytes))
            .and_then(|units| {
                units
                    .iter()
                    .position(|unit| *unit == 0)
                    .map(|end| &units[..end])
            })
            .and_then(|path| String::from_utf16(path).ok())
            .filter(|path| !path.is_empty() && !path.chars().any(char::is_control))
        else {
            return Ok(None);
        };
        check_cancelled(cancelled)?;
        Ok(Some(Interface {
            path,
            hardware_ids: self.hardware_ids(&device),
        }))
    }

    fn hardware_ids(&self, device: &SP_DEVINFO_DATA) -> Option<Vec<String>> {
        let mut buffer = [0u16; MAX_HARDWARE_ID_UNITS];
        let mut kind = 0;
        let mut bytes = 0;
        // SAFETY: This devnode belongs to the set. All output pointers are
        // writable and the aligned property buffer has the advertised capacity.
        let read = unsafe {
            SetupDiGetDeviceRegistryPropertyW(
                self.0,
                device,
                SPDRP_HARDWAREID,
                &mut kind,
                buffer.as_mut_ptr().cast(),
                size_of_val(&buffer) as u32,
                &mut bytes,
            )
        };
        if read == 0 || kind != REG_MULTI_SZ {
            return None;
        }
        pnp::hardware_ids(wide_bytes(&buffer, bytes)?)
    }
}

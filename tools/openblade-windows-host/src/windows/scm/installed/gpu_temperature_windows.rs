//! The Windows reads behind managed GPU temperature: the WDDM GPU-engine
//! counters (`openblade_platform_windows::gpu_apps::NvidiaActivityQuery`),
//! the display-class `SPDRP_DEVICE_POWER_DATA` property and NVML.
//!
//! The counters and the power property are read-only Windows queries that
//! open no GPU device and call no NVIDIA API. NVML is NVIDIA's user-mode
//! driver library, loaded through the Service's one NVML loader
//! (`nvml_library`, System32 only, its dependencies included) and only once
//! the gates have passed, where managed's first `[DllImport("nvml.dll")]`
//! call loads it. Once loaded it stays loaded for the process, as managed's
//! import does: a worker that Stop no longer waits for may still be inside a
//! call, so the module is never unloaded under it.
#![allow(unsafe_code)]

use super::super::super::nvml_library::{self, LIBRARY_NOT_FOUND};
use super::{
    Enumerated, Factory, Nvml, NvmlSession, POWER_DATA, Power, Probes, Sampler, nvidia_power,
    workload,
};
use openblade_platform_windows::gpu_apps::NvidiaActivityQuery;
use std::{
    cell::Cell,
    ffi::c_void,
    mem::size_of,
    ptr::{null, null_mut},
    thread,
    time::Instant,
};
use windows_sys::Win32::{
    Devices::DeviceAndDriverInstallation::{
        DIGCF_PRESENT, GUID_DEVCLASS_DISPLAY, HDEVINFO, SP_DEVINFO_DATA, SPDRP_DEVICE_POWER_DATA,
        SetupDiDestroyDeviceInfoList, SetupDiEnumDeviceInfo, SetupDiGetClassDevsW,
        SetupDiGetDeviceInstanceIdW, SetupDiGetDeviceRegistryPropertyW,
    },
    Foundation::{ERROR_NO_MORE_ITEMS, GetLastError, INVALID_HANDLE_VALUE},
};

/// Managed reads at most this many characters of an instance ID.
const MAX_INSTANCE_ID: usize = 512;

/// The production sampler, built on the worker thread.
pub(in super::super) fn sampler() -> Factory {
    Box::new(|| {
        Sampler::new(
            Box::new(WindowsProbes),
            Box::new(NvmlSession::new(WindowsNvml::default())),
            Box::new(Instant::now),
        )
    })
}

struct WindowsProbes;

impl Probes for WindowsProbes {
    fn workload(&mut self) -> Option<bool> {
        workload(
            || NvidiaActivityQuery::open().ok(),
            |query| query.read().ok(),
            thread::sleep,
        )
    }

    fn power(&mut self) -> Power {
        // SAFETY: The class GUID is a constant; the other inputs are null.
        let raw = unsafe {
            SetupDiGetClassDevsW(&GUID_DEVCLASS_DISPLAY, null(), null_mut(), DIGCF_PRESENT)
        };
        if raw == INVALID_HANDLE_VALUE as HDEVINFO {
            return Power::Unknown;
        }
        let set = DeviceInfoSet(raw);
        // The property is read for the device just enumerated.
        let current = Cell::new(SP_DEVINFO_DATA::default());
        nvidia_power(
            |index| {
                let mut device = SP_DEVINFO_DATA {
                    cbSize: size_of::<SP_DEVINFO_DATA>() as u32,
                    ..Default::default()
                };
                // SAFETY: The set is owned here and the output carries its size.
                if unsafe { SetupDiEnumDeviceInfo(set.0, index, &mut device) } == 0 {
                    // SAFETY: The last error of the failed call on this thread.
                    return if unsafe { GetLastError() } == ERROR_NO_MORE_ITEMS {
                        Enumerated::End
                    } else {
                        Enumerated::Failed
                    };
                }
                current.set(device);
                set.instance_id(&device)
                    .map_or(Enumerated::Failed, Enumerated::Device)
            },
            || set.power_data(&current.get()),
        )
    }
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

    /// The device's `CM_POWER_DATA` property and its type, when it is exactly
    /// that size.
    fn power_data(&self, device: &SP_DEVINFO_DATA) -> Option<(u32, Vec<u8>)> {
        let mut data = vec![0u8; POWER_DATA];
        let (mut kind, mut size) = (0, 0);
        // SAFETY: The device belongs to the set, the buffer has the advertised
        // capacity and both outputs are writable.
        let read = unsafe {
            SetupDiGetDeviceRegistryPropertyW(
                self.0,
                device,
                SPDRP_DEVICE_POWER_DATA,
                &mut kind,
                data.as_mut_ptr(),
                data.len() as u32,
                &mut size,
            )
        };
        (read != 0 && size as usize == POWER_DATA).then_some((kind, data))
    }
}

type Init = unsafe extern "C" fn() -> i32;
type Shutdown = unsafe extern "C" fn() -> i32;
type DeviceByIndex = unsafe extern "C" fn(u32, *mut *mut c_void) -> i32;
type Temperature = unsafe extern "C" fn(*mut c_void, u32, *mut u32) -> i32;

/// The four NVML functions managed imports.
struct Library {
    init: Init,
    shutdown: Shutdown,
    device: DeviceByIndex,
    temperature: Temperature,
}

/// NVML, loaded by the first `init`. A failed load is retried by the next,
/// as managed's import is.
#[derive(Default)]
struct WindowsNvml {
    library: Option<Library>,
}

impl Nvml for WindowsNvml {
    fn init(&mut self) -> i32 {
        if self.library.is_none() {
            self.library = load();
        }
        match &self.library {
            // SAFETY: The resolved nvmlInit_v2 takes no arguments.
            Some(library) => unsafe { (library.init)() },
            None => LIBRARY_NOT_FOUND,
        }
    }

    fn device(&mut self, index: u32) -> Result<usize, i32> {
        let library = self.library.as_ref().ok_or(LIBRARY_NOT_FOUND)?;
        let mut device = null_mut();
        // SAFETY: The output is a writable nvmlDevice_t.
        let status = unsafe { (library.device)(index, &mut device) };
        if status == 0 {
            Ok(device as usize)
        } else {
            Err(status)
        }
    }

    fn temperature(&mut self, device: usize, sensor: u32) -> Result<u32, i32> {
        let library = self.library.as_ref().ok_or(LIBRARY_NOT_FOUND)?;
        let mut celsius = 0;
        // SAFETY: The device is the handle NVML returned in this session and
        // the output is writable.
        let status = unsafe { (library.temperature)(device as *mut c_void, sensor, &mut celsius) };
        if status == 0 {
            Ok(celsius)
        } else {
            Err(status)
        }
    }

    fn shutdown(&mut self) -> i32 {
        match &self.library {
            // SAFETY: The resolved nvmlShutdown takes no arguments.
            Some(library) => unsafe { (library.shutdown)() },
            None => LIBRARY_NOT_FOUND,
        }
    }
}

/// nvml.dll from System32 with all four functions, or None.
fn load() -> Option<Library> {
    nvml_library::load(resolve)
}

fn resolve(module: &nvml_library::Module) -> Option<Library> {
    let (init, shutdown, device, temperature) = (
        module.symbol(c"nvmlInit_v2")?,
        module.symbol(c"nvmlShutdown")?,
        module.symbol(c"nvmlDeviceGetHandleByIndex_v2")?,
        module.symbol(c"nvmlDeviceGetTemperature")?,
    );
    // SAFETY: Each export has the NVML signature named by its type, in the
    // C calling convention of the x64 Windows ABI.
    unsafe {
        Some(Library {
            init: std::mem::transmute::<unsafe extern "system" fn() -> isize, Init>(init),
            shutdown: std::mem::transmute::<unsafe extern "system" fn() -> isize, Shutdown>(
                shutdown,
            ),
            device: std::mem::transmute::<unsafe extern "system" fn() -> isize, DeviceByIndex>(
                device,
            ),
            temperature: std::mem::transmute::<unsafe extern "system" fn() -> isize, Temperature>(
                temperature,
            ),
        })
    }
}

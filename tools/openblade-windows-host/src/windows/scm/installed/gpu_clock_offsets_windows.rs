//! The seven NVML functions managed `NvidiaNvmlClockOffsetApi` imports,
//! loaded through the Service's one NVML loader (`nvml_library`, System32
//! only). Nothing here runs in tests: every write test scripts [`Nvml`].
//!
//! All seven are resolved when the library loads, where managed's
//! `DllImport` binds each on its first call. A missing library or function
//! therefore fails `init` with `NVML_ERROR_LIBRARY_NOT_FOUND` (12), and the
//! controller's message names that status where managed's carries the .NET
//! load exception's text. A driver that exports the getter but not the
//! setter makes every query fail, so a write is Unavailable at its
//! preflight; managed queries and then fails the set KnownNotApplied.
//! Neither submits anything. A failed load is retried by the next `init`, so
//! a driver installed while the Service runs is found; that costs one
//! `LoadLibraryExW` per attempt only while NVML is missing or incomplete.
#![allow(unsafe_code)]

use super::super::nvml_library::{self, LIBRARY_NOT_FOUND};
use super::{ClockOffset, NAME_CAPACITY, Nvml};
use std::ffi::c_void;

type Init = unsafe extern "C" fn() -> i32;
type Shutdown = unsafe extern "C" fn() -> i32;
type DeviceCount = unsafe extern "C" fn(*mut u32) -> i32;
type DeviceByIndex = unsafe extern "C" fn(u32, *mut *mut c_void) -> i32;
type DeviceName = unsafe extern "C" fn(*mut c_void, *mut u8, u32) -> i32;
type ClockOffsets = unsafe extern "C" fn(*mut c_void, *mut ClockOffset) -> i32;

struct Library {
    init: Init,
    shutdown: Shutdown,
    count: DeviceCount,
    device: DeviceByIndex,
    name: DeviceName,
    get: ClockOffsets,
    set: ClockOffsets,
}

/// NVML, loaded by the first `init`. A failed load is retried by the next,
/// as managed's import is.
#[derive(Default)]
pub(in super::super) struct WindowsNvml {
    library: Option<Library>,
}

impl WindowsNvml {
    fn library(&self) -> Result<&Library, i32> {
        self.library.as_ref().ok_or(LIBRARY_NOT_FOUND)
    }
}

fn status(status: i32) -> Result<(), i32> {
    if status == super::SUCCESS {
        Ok(())
    } else {
        Err(status)
    }
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

    fn shutdown(&mut self) -> i32 {
        match &self.library {
            // SAFETY: The resolved nvmlShutdown takes no arguments.
            Some(library) => unsafe { (library.shutdown)() },
            None => LIBRARY_NOT_FOUND,
        }
    }

    fn device_count(&mut self) -> Result<u32, i32> {
        let library = self.library()?;
        let mut count = 0;
        // SAFETY: The output is a writable unsigned int.
        status(unsafe { (library.count)(&mut count) })?;
        Ok(count)
    }

    fn device(&mut self, index: u32) -> Result<usize, i32> {
        let library = self.library()?;
        let mut device = std::ptr::null_mut();
        // SAFETY: The output is a writable nvmlDevice_t.
        status(unsafe { (library.device)(index, &mut device) })?;
        Ok(device as usize)
    }

    fn name(&mut self, device: usize, name: &mut [u8; NAME_CAPACITY]) -> i32 {
        match self.library() {
            // SAFETY: The device is the handle NVML returned in this session
            // and the buffer has the advertised capacity.
            Ok(library) => unsafe {
                (library.name)(
                    device as *mut c_void,
                    name.as_mut_ptr(),
                    NAME_CAPACITY as u32,
                )
            },
            Err(status) => status,
        }
    }

    fn clock_offsets(&mut self, device: usize, info: &mut ClockOffset) -> i32 {
        match self.library() {
            // SAFETY: The device is the handle NVML returned in this session
            // and the structure is a versioned nvmlClockOffset_v1_t.
            Ok(library) => unsafe { (library.get)(device as *mut c_void, info) },
            Err(status) => status,
        }
    }

    fn set_clock_offsets(&mut self, device: usize, info: &mut ClockOffset) -> i32 {
        match self.library() {
            // SAFETY: As `clock_offsets`.
            Ok(library) => unsafe { (library.set)(device as *mut c_void, info) },
            Err(status) => status,
        }
    }
}

/// nvml.dll from System32 with all seven functions, or None.
fn load() -> Option<Library> {
    nvml_library::load(|module| {
        let (init, shutdown, count, device, name, get, set) = (
            module.symbol(c"nvmlInit_v2")?,
            module.symbol(c"nvmlShutdown")?,
            module.symbol(c"nvmlDeviceGetCount_v2")?,
            module.symbol(c"nvmlDeviceGetHandleByIndex_v2")?,
            module.symbol(c"nvmlDeviceGetName")?,
            module.symbol(c"nvmlDeviceGetClockOffsets")?,
            module.symbol(c"nvmlDeviceSetClockOffsets")?,
        );
        type Export = unsafe extern "system" fn() -> isize;
        // SAFETY: Each export has the NVML signature named by its type, in
        // the C calling convention of the x64 Windows ABI.
        unsafe {
            Some(Library {
                init: std::mem::transmute::<Export, Init>(init),
                shutdown: std::mem::transmute::<Export, Shutdown>(shutdown),
                count: std::mem::transmute::<Export, DeviceCount>(count),
                device: std::mem::transmute::<Export, DeviceByIndex>(device),
                name: std::mem::transmute::<Export, DeviceName>(name),
                get: std::mem::transmute::<Export, ClockOffsets>(get),
                set: std::mem::transmute::<Export, ClockOffsets>(set),
            })
        }
    })
}

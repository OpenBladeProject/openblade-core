//! The one place the Service loads NVIDIA's user-mode `nvml.dll`, for the
//! GPU temperature reader and the GPU clock-offset backend.
//!
//! The library is loaded only from `%windir%\System32`, where the NVIDIA
//! display driver installs it. `LOAD_LIBRARY_SEARCH_SYSTEM32` applies to its
//! dependencies too, so neither the current directory nor `PATH` is ever
//! searched. Like managed's `[DllImport("nvml.dll")]`, the load checks no
//! signature. A Windows trust and catalog-membership check of the loaded
//! file belongs in `load`, so both callers get it at once; it is a tracked
//! gate before any GPU clock-offset writer is packaged. A bare-name
//! `LoadLibraryExW` returns any `nvml.dll` already loaded in the process,
//! whatever its directory and the search flags, so that check must verify
//! the loaded module's own path (`GetModuleFileNameW`) as well as its
//! signature. Once a function of the library may have been called it stays
//! loaded for the process: a worker that Stop no longer waits for may still
//! be inside a call.
// Its callers are the live GPU temperature reader and, with
// `service-gpu-clock-tuning-v1`, the clock-offset backend. The allowance is
// kept for the live readings alone so that every other composition, and
// every test build, may leave parts of it unused.
#![cfg_attr(any(test, not(feature = "service-live-readings-v1")), allow(dead_code))]
#![allow(unsafe_code)]

use std::{ffi::CStr, ptr::null_mut};
use windows_sys::{
    Win32::{
        Foundation::{FARPROC, FreeLibrary, HMODULE},
        System::LibraryLoader::{GetProcAddress, LOAD_LIBRARY_SEARCH_SYSTEM32, LoadLibraryExW},
    },
    core::w,
};

/// `NVML_ERROR_LIBRARY_NOT_FOUND`: nvml.dll or one of the functions a caller
/// needs is missing, which managed sees as `DllNotFoundException` or
/// `EntryPointNotFoundException`.
pub(super) const LIBRARY_NOT_FOUND: i32 = 12;

/// A loaded nvml.dll whose exports a caller is resolving.
pub(super) struct Module(HMODULE);

impl Module {
    /// The export `name`, or None.
    pub(super) fn symbol(&self, name: &CStr) -> FARPROC {
        // SAFETY: The module is loaded and the name is NUL-terminated.
        unsafe { GetProcAddress(self.0, name.as_ptr().cast()) }
    }
}

/// nvml.dll from System32, resolved by `resolve`, or None. A module whose
/// resolution fails is unloaded again, since none of its functions was
/// called; a resolved one stays loaded for the process.
pub(super) fn load<T>(resolve: impl FnOnce(&Module) -> Option<T>) -> Option<T> {
    // SAFETY: A constant name. LOAD_LIBRARY_SEARCH_SYSTEM32 searches only
    // System32 for the library and its dependencies.
    let module =
        unsafe { LoadLibraryExW(w!("nvml.dll"), null_mut(), LOAD_LIBRARY_SEARCH_SYSTEM32) };
    if module.is_null() {
        return None;
    }
    let module = Module(module);
    let resolved = resolve(&module);
    if resolved.is_none() {
        // SAFETY: The load above took this reference and nothing in the
        // module ran.
        unsafe { FreeLibrary(module.0) };
    }
    resolved
}

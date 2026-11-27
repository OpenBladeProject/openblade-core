//! powrprof.dll, loaded from System32 at run time. The power-mode supplier and
//! the session power relay resolve their functions here rather than through a
//! load-time import: a load-time import is resolved before the Agent restricts
//! its DLL search path, and a Windows build without one of the functions would
//! stop the whole Agent from loading instead of failing only that role, which
//! is how managed `DllImport` fails too.
#![allow(unsafe_code)]

use windows_sys::Win32::{
    Foundation::{FreeLibrary, HMODULE},
    System::LibraryLoader::{GetProcAddress, LOAD_LIBRARY_SEARCH_SYSTEM32, LoadLibraryExW},
};

/// One untyped export. Each caller transmutes it to its documented signature.
pub(super) type Function = unsafe extern "system" fn() -> isize;

/// powrprof.dll from System32, released on drop.
pub(super) struct Library(HMODULE);

impl Library {
    /// The System32 copy, or `None` when Windows cannot load it.
    pub(super) fn load() -> Option<Self> {
        let name: Vec<u16> = "powrprof.dll".encode_utf16().chain([0]).collect();
        // SAFETY: a NUL-terminated name; the search is restricted to System32.
        let module = unsafe {
            LoadLibraryExW(
                name.as_ptr(),
                std::ptr::null_mut(),
                LOAD_LIBRARY_SEARCH_SYSTEM32,
            )
        };
        (!module.is_null()).then_some(Self(module))
    }

    /// The NUL-terminated export `name`, valid while this library is held.
    pub(super) fn function(&self, name: &[u8]) -> Option<Function> {
        debug_assert_eq!(name.last(), Some(&0));
        // SAFETY: a NUL-terminated export name on a held module.
        unsafe { GetProcAddress(self.0, name.as_ptr()) }
    }
}

impl Drop for Library {
    fn drop(&mut self) {
        // SAFETY: releases the reference `load` took.
        unsafe { FreeLibrary(self.0) };
    }
}

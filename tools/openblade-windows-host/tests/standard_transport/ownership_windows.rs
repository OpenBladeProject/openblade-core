//! Win32 resources for the offline ownership fixture.

use serde::{Deserialize, Serialize};
use std::{
    fs::{File, OpenOptions},
    io,
    mem::{MaybeUninit, size_of},
    os::windows::{
        fs::OpenOptionsExt,
        io::{AsRawHandle, FromRawHandle, OwnedHandle},
    },
    path::Path,
    process::Child,
    ptr,
};
use windows_sys::Win32::{
    Foundation::{HANDLE_FLAG_INHERIT, INVALID_HANDLE_VALUE, SetHandleInformation},
    Security::Cryptography::{BCRYPT_USE_SYSTEM_PREFERRED_RNG, BCryptGenRandom},
    Storage::FileSystem::{BY_HANDLE_FILE_INFORMATION, GetFileInformationByHandle},
    System::Console::{GetStdHandle, STD_ERROR_HANDLE, STD_INPUT_HANDLE, STD_OUTPUT_HANDLE},
    System::JobObjects::{
        AssignProcessToJobObject, CreateJobObjectW, JOB_OBJECT_LIMIT_ACTIVE_PROCESS,
        JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE, JOBOBJECT_EXTENDED_LIMIT_INFORMATION,
        JobObjectExtendedLimitInformation, SetInformationJobObject,
    },
};

pub(super) fn attempt() -> io::Result<[u8; 16]> {
    let mut bytes = [0; 16];
    // SAFETY: the buffer is writable for its fixed length. The system RNG
    // flag requires a null algorithm handle.
    let status = unsafe {
        BCryptGenRandom(
            ptr::null_mut(),
            bytes.as_mut_ptr(),
            bytes.len() as u32,
            BCRYPT_USE_SYSTEM_PREFERRED_RNG,
        )
    };
    if status != 0 {
        return Err(io::Error::other(format!("BCryptGenRandom: {status:#x}")));
    }
    Ok(bytes)
}

pub(super) fn exclusive(path: impl AsRef<Path>, create: bool) -> io::Result<File> {
    OpenOptions::new()
        .read(true)
        .write(true)
        .create_new(create)
        .share_mode(0)
        .open(path)
}

#[derive(Clone, Copy, Debug, Deserialize, Eq, PartialEq, Serialize)]
pub(super) struct FileIdentity {
    volume: u32,
    index: u64,
}

pub(super) fn identity(file: &File) -> io::Result<FileIdentity> {
    let mut info = MaybeUninit::<BY_HANDLE_FILE_INFORMATION>::uninit();
    // SAFETY: the file keeps the handle alive and info has the required size.
    let info = unsafe {
        if GetFileInformationByHandle(file.as_raw_handle(), info.as_mut_ptr()) == 0 {
            return Err(io::Error::last_os_error());
        }
        info.assume_init()
    };
    Ok(FileIdentity {
        volume: info.dwVolumeSerialNumber,
        index: (u64::from(info.nFileIndexHigh) << 32) | u64::from(info.nFileIndexLow),
    })
}

// Command uses inheritable child stdio. Clear the supervisor's inherited
// endpoints before it starts a worker so the worker receives only its own
// protocol endpoints. The fixture opens no other inheritable handles.
pub(super) fn isolate_child_stdio() -> io::Result<()> {
    for kind in [STD_INPUT_HANDLE, STD_OUTPUT_HANDLE, STD_ERROR_HANDLE] {
        // SAFETY: these process-owned standard handles stay open, and clearing
        // inheritance leaves them usable by this process.
        unsafe {
            let handle = GetStdHandle(kind);
            if handle.is_null()
                || handle == INVALID_HANDLE_VALUE
                || SetHandleInformation(handle, HANDLE_FLAG_INHERIT, 0) == 0
            {
                return Err(io::Error::last_os_error());
            }
        }
    }
    Ok(())
}

pub(super) struct WorkerJob(OwnedHandle);

impl WorkerJob {
    pub(super) fn new() -> io::Result<Self> {
        // SAFETY: null security attributes give us a non-inheritable handle.
        // The job is unnamed and the new handle is owned exactly once.
        let job = unsafe {
            let handle = CreateJobObjectW(ptr::null(), ptr::null());
            if handle.is_null() {
                return Err(io::Error::last_os_error());
            }
            OwnedHandle::from_raw_handle(handle)
        };
        let mut limits = JOBOBJECT_EXTENDED_LIMIT_INFORMATION::default();
        limits.BasicLimitInformation.LimitFlags =
            JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | JOB_OBJECT_LIMIT_ACTIVE_PROCESS;
        limits.BasicLimitInformation.ActiveProcessLimit = 1;
        // SAFETY: both the handle and the correctly sized limit structure
        // remain valid through the call.
        if unsafe {
            SetInformationJobObject(
                job.as_raw_handle(),
                JobObjectExtendedLimitInformation,
                ptr::from_ref(&limits).cast(),
                size_of::<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>() as u32,
            )
        } == 0
        {
            return Err(io::Error::last_os_error());
        }
        Ok(Self(job))
    }

    pub(super) fn contain(&self, child: &Child) -> io::Result<()> {
        // SAFETY: both owned handles remain alive through the call. The child
        // waits on its private input and cannot open the fake lanes yet.
        if unsafe { AssignProcessToJobObject(self.0.as_raw_handle(), child.as_raw_handle()) } == 0 {
            return Err(io::Error::last_os_error());
        }
        Ok(())
    }
}

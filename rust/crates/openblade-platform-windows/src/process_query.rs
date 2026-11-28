//! Shared queries on held process and token handles.
#![allow(unsafe_code)]

use std::{
    os::windows::io::{AsRawHandle, FromRawHandle, OwnedHandle},
    ptr::null_mut,
};

use windows_sys::Win32::{
    Foundation::{FILETIME, GetLastError, HANDLE},
    Security::{
        GetTokenInformation, SECURITY_MAX_SID_SIZE, TOKEN_QUERY, TOKEN_USER, TokenSessionId,
        TokenUser,
    },
    System::Threading::{
        GetProcessTimes, OpenProcess, OpenProcessToken, PROCESS_ACCESS_RIGHTS,
        PROCESS_QUERY_LIMITED_INFORMATION, QueryFullProcessImageNameW,
    },
};

pub(crate) fn open_process(
    pid: u32,
    additional_access: PROCESS_ACCESS_RIGHTS,
) -> Result<OwnedHandle, u32> {
    // SAFETY: A non-inheritable process handle is requested for this PID.
    let raw = unsafe {
        OpenProcess(
            PROCESS_QUERY_LIMITED_INFORMATION | additional_access,
            0,
            pid,
        )
    };
    if raw.is_null() {
        // SAFETY: Read the error immediately after the failed call.
        return Err(unsafe { GetLastError() });
    }
    // SAFETY: A successful OpenProcess returns a uniquely owned real handle.
    Ok(unsafe { OwnedHandle::from_raw_handle(raw) })
}

pub(crate) fn filetime(value: FILETIME) -> u64 {
    (u64::from(value.dwHighDateTime) << 32) | u64::from(value.dwLowDateTime)
}

pub(crate) fn process_times(process: &OwnedHandle) -> Option<(u64, u64)> {
    let mut created = FILETIME::default();
    let mut exited = FILETIME::default();
    let mut kernel = FILETIME::default();
    let mut user = FILETIME::default();
    // SAFETY: All outputs are distinct and writable and the process is held.
    (unsafe {
        GetProcessTimes(
            process.as_raw_handle(),
            &mut created,
            &mut exited,
            &mut kernel,
            &mut user,
        )
    } != 0)
        .then(|| (filetime(created), filetime(exited)))
}

pub(crate) fn image_path(process: &OwnedHandle) -> Option<Vec<u16>> {
    const MAX_PATH_UNITS: usize = 32_768;
    let mut path = vec![0; MAX_PATH_UNITS];
    let mut units = path.len() as u32;
    // SAFETY: The process is held and the writable buffer has the advertised capacity.
    if unsafe {
        QueryFullProcessImageNameW(process.as_raw_handle(), 0, path.as_mut_ptr(), &mut units)
    } == 0
    {
        return None;
    }
    path.get(..units as usize)?;
    path.truncate(units as usize);
    Some(path)
}

pub(crate) fn open_process_token(process: HANDLE) -> Option<OwnedHandle> {
    let mut raw = null_mut();
    // SAFETY: The caller holds the process handle and the output is writable.
    if unsafe { OpenProcessToken(process, TOKEN_QUERY, &mut raw) } == 0 || raw.is_null() {
        return None;
    }
    // SAFETY: OpenProcessToken returned a uniquely owned real token handle.
    Some(unsafe { OwnedHandle::from_raw_handle(raw) })
}

pub(crate) fn token_session(token: &OwnedHandle) -> Option<u32> {
    let mut session = 0_u32;
    let mut used = 0;
    // SAFETY: TokenSessionId writes a DWORD and a separate length.
    if unsafe {
        GetTokenInformation(
            token.as_raw_handle(),
            TokenSessionId,
            (&mut session as *mut u32).cast(),
            4,
            &mut used,
        )
    } == 0
        || used != 4
    {
        return None;
    }
    Some(session)
}

/// Canonical binary user SID and session of a held process's primary token.
pub(crate) fn token_identity(process: HANDLE) -> Option<(Vec<u8>, u32)> {
    let token = open_process_token(process)?;
    let session = token_session(&token)?;
    let mut used = 0;

    const TOKEN_USER_BYTES: usize = size_of::<TOKEN_USER>();
    const BUFFER_WORDS: usize =
        (TOKEN_USER_BYTES + SECURITY_MAX_SID_SIZE as usize).div_ceil(size_of::<usize>());
    let mut buffer = [0_usize; BUFFER_WORDS];
    let base = buffer.as_mut_ptr().cast::<u8>();
    let capacity = size_of_val(&buffer);
    // SAFETY: The initialized buffer is aligned for TOKEN_USER and has space
    // for the maximum binary SID.
    if unsafe {
        GetTokenInformation(
            token.as_raw_handle(),
            TokenUser,
            base.cast(),
            capacity as u32,
            &mut used,
        )
    } == 0
        || !(TOKEN_USER_BYTES..=capacity).contains(&(used as usize))
    {
        return None;
    }
    // SAFETY: A complete aligned TOKEN_USER was returned inside the buffer.
    let sid = unsafe { (*base.cast::<TOKEN_USER>()).User.Sid };
    let offset = sid
        .addr()
        .checked_sub(base.addr())
        .filter(|&offset| offset >= TOKEN_USER_BYTES)?;
    // SAFETY: The validated output length fits in the initialized buffer.
    let payload = unsafe { std::slice::from_raw_parts(base, used as usize) }.get(offset..)?;
    let [1, subauthorities, ..] = payload else {
        return None;
    };
    let length = 8 + 4 * usize::from(*subauthorities);
    if length > SECURITY_MAX_SID_SIZE as usize {
        return None;
    }
    Some((payload.get(..length)?.to_vec(), session))
}

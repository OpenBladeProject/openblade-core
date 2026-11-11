//! Scratch-only standard-token owner/DACL policy; never installed authority.
#![allow(unsafe_code)]
use crate::{
    windows_protected_path::{denied, dword, section, sid, word},
    windows_token::{check, process_token},
};
use std::{io, mem::size_of_val, os::windows::io::AsRawHandle, slice};
use windows_sys::Win32::{
    Security::{
        GetLengthSid, GetTokenInformation, IsWellKnownSid, SE_DACL_PRESENT, SE_DACL_PROTECTED,
        SE_SELF_RELATIVE, SECURITY_MAX_SID_SIZE, TOKEN_USER, TokenElevation, TokenUser,
        WinLocalSystemSid,
    },
    Storage::FileSystem::FILE_ALL_ACCESS,
};

/// Owner from an already admitted scratch root, without querying any process.
pub(crate) fn retained_owner(file: &std::fs::File) -> io::Result<Vec<u8>> {
    let mut owner = None;
    crate::windows_protected_path::verify_descriptor(file, |bytes| {
        owner = Some(sid(section(bytes, 4)?)?.to_vec());
        Some(true)
    })?;
    owner.ok_or_else(|| denied("scratch owner is unavailable"))
}

pub(crate) fn owner() -> io::Result<Vec<u8>> {
    let token = process_token()?;
    let mut elevated = 0u32;
    let mut returned = 0;
    let mut user = [0usize;
        (size_of::<TOKEN_USER>() + SECURITY_MAX_SID_SIZE as usize).div_ceil(size_of::<usize>())];
    // SAFETY: The token is live and each output buffer is aligned and sized
    // for the information class. Windows keeps the returned SID in user.
    unsafe {
        check(GetTokenInformation(
            token.as_raw_handle(),
            TokenElevation,
            (&mut elevated as *mut u32).cast(),
            size_of_val(&elevated) as u32,
            &mut returned,
        ))?;
        check(GetTokenInformation(
            token.as_raw_handle(),
            TokenUser,
            user.as_mut_ptr().cast(),
            size_of_val(&user) as u32,
            &mut returned,
        ))?;
        let principal = (*user.as_ptr().cast::<TOKEN_USER>()).User.Sid;
        if elevated != 0 || principal.is_null() || IsWellKnownSid(principal, WinLocalSystemSid) != 0
        {
            return Err(denied(
                "receipt rehearsal requires a standard Windows token",
            ));
        }
        let size = GetLengthSid(principal) as usize;
        if !(8..=SECURITY_MAX_SID_SIZE as usize).contains(&size) {
            return Err(denied("invalid scratch owner SID"));
        }
        Ok(slice::from_raw_parts(principal.cast(), size).to_vec())
    }
}

// Match Setup's scratch-only owner/DACL contract on the same retained
// handles used for I/O. Inherited and explicit copies of the same grant
// may coexist on a file after the C# broker uses File.Replace.
pub(crate) fn accepts(bytes: &[u8], owner: &[u8], directory: bool, root: bool) -> Option<bool> {
    let control = word(bytes, 2)?;
    if *bytes.first()? != 1
        || control & (SE_SELF_RELATIVE | SE_DACL_PRESENT) != SE_SELF_RELATIVE | SE_DACL_PRESENT
        || root && control & SE_DACL_PROTECTED == 0
        || sid(section(bytes, 4)?)? != owner
    {
        return Some(false);
    }
    let acl = section(bytes, 16)?;
    let count = word(acl, 4)?;
    if !matches!(acl.first()?, 2 | 4) || !(1..=if directory { 1 } else { 2 }).contains(&count) {
        return Some(false);
    }
    let expected = if directory { 3 } else { 0 }; // Object and container inherit.
    let inherited = if root { 0 } else { 0x10 };
    let mut aces = acl.get(8..usize::from(word(acl, 2)?))?;
    let mut previous = None;
    for _ in 0..count {
        let length = usize::from(word(aces, 2)?);
        let ace = aces.get(..length)?;
        let flags = *ace.get(1)?;
        let trustee = sid(ace.get(8..)?)?;
        if *ace.first()? != 0
            || flags & !inherited != expected
            || previous == Some(flags)
            || dword(ace, 4)? != FILE_ALL_ACCESS
            || trustee != owner
            || trustee.len() + 8 != length
        {
            return Some(false);
        }
        previous = Some(flags);
        aces = &aces[length..];
    }
    Some(true)
}

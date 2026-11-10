//! Standard-token NTFS rehearsal for both candidate receipt phases.

#[cfg(windows)]
#[path = "../../src/candidate_receipt.rs"]
mod candidate_receipt;
#[cfg(windows)]
#[path = "../../src/service_image.rs"]
mod service_image;
#[cfg(windows)]
#[allow(dead_code)]
#[path = "../../src/service_install_identity.rs"]
mod service_install_identity;
#[cfg(windows)]
#[allow(dead_code)]
#[path = "../../src/windows_protected_path.rs"]
mod windows_protected_path;
#[cfg(windows)]
#[allow(dead_code)]
#[path = "../../src/windows_token.rs"]
mod windows_token;

#[cfg(windows)]
mod scratch {
    #![allow(unsafe_code)]

    use std::{
        io::{self, Read},
        mem::size_of_val,
        os::windows::io::{AsRawHandle, FromRawHandle, OwnedHandle},
        slice,
    };
    use windows_sys::Win32::{
        Foundation::{FILETIME, WAIT_TIMEOUT},
        Security::{
            GetLengthSid, GetTokenInformation, IsWellKnownSid, SE_DACL_PRESENT, SE_DACL_PROTECTED,
            SE_SELF_RELATIVE, SECURITY_MAX_SID_SIZE, TOKEN_USER, TokenElevation, TokenUser,
            WinLocalSystemSid,
        },
        Storage::FileSystem::FILE_ALL_ACCESS,
        System::Threading::{
            GetProcessTimes, OpenProcess, PROCESS_QUERY_LIMITED_INFORMATION, PROCESS_SYNCHRONIZE,
            WaitForSingleObject,
        },
        UI::Shell::FOLDERID_LocalAppData,
    };

    use crate::{
        candidate_receipt,
        service_install_identity::{INSTALL_DIRECTORY, known_folder},
        windows_protected_path::{denied, dword, section, sid, verify_descriptor, word},
        windows_token::{check, process_token},
    };

    pub(super) fn run() -> io::Result<()> {
        let owner = scratch_owner()?;
        // The fixture protocol conveys the scratch GUID and receipt digest.
        // Startup also supplies the current binding digest and live Setup
        // identity from the authenticated scratch rendezvous.
        const COMMITTED_INPUT: usize = 68;
        const STARTUP_INPUT: usize = 112;
        let mut input = Vec::new();
        io::stdin()
            .take((STARTUP_INPUT + 1) as u64)
            .read_to_end(&mut input)?;
        if !matches!(input.len(), COMMITTED_INPUT | STARTUP_INPUT) {
            return Err(denied("invalid scratch receipt commitment"));
        }
        let scope = std::str::from_utf8(&input[..36]).map_err(io::Error::other)?;
        let short = scope.replace('-', "");
        if !candidate_receipt::lower_hex(&short, 32)
            || short == "00000000000000000000000000000000"
            || !scope.split('-').map(str::len).eq([8, 4, 4, 4, 12])
        {
            return Err(denied("expected an exact scratch GUID"));
        }
        let expected = input[36..68]
            .try_into()
            .expect("checked fixture input length");
        let install = known_folder(&FOLDERID_LocalAppData)?
            .join("Temp")
            .join(format!("OpenBlade.CandidateReceipt-{short}"))
            .join("program-files")
            .join(INSTALL_DIRECTORY);
        let check_directory = |file: &std::fs::File, index, _, ancestor_count| {
            // The last two ancestors are the scratch root and its
            // program-files child. Earlier handles pin location only.
            let scratch = ancestor_count - 2;
            if index >= scratch {
                verify_descriptor(file, |bytes| accepts(bytes, &owner, true, index == scratch))?;
            }
            Ok(())
        };
        let check_file = |file: &std::fs::File| {
            verify_descriptor(file, |bytes| accepts(bytes, &owner, false, false))
        };
        if input.len() == STARTUP_INPUT {
            let active = input[68..100]
                .try_into()
                .expect("checked fixture input length");
            let pid = u32::from_le_bytes(
                input[100..104]
                    .try_into()
                    .expect("checked fixture input length"),
            );
            let started = u64::from_le_bytes(
                input[104..112]
                    .try_into()
                    .expect("checked fixture input length"),
            );
            let setup = pin_setup(pid, started)?;
            let _startup = candidate_receipt::read_provisional(
                &install,
                scope,
                check_directory,
                check_file,
                expected,
                candidate_receipt::ActiveBinding {
                    digest: active,
                    setup_identity: input[100..112]
                        .try_into()
                        .expect("checked fixture input length"),
                },
            )?;
            require_running(&setup)?;
        } else {
            let _disk =
                candidate_receipt::read(&install, scope, check_directory, check_file, || {
                    Some(expected)
                })?;
        }
        Ok(())
    }

    fn pin_setup(pid: u32, started: u64) -> io::Result<OwnedHandle> {
        // SAFETY: Query and wait only, with no handle inheritance.
        let handle = unsafe {
            OpenProcess(
                PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_SYNCHRONIZE,
                0,
                pid,
            )
        };
        if handle.is_null() {
            return Err(denied("provisional Setup process is unavailable"));
        }
        // SAFETY: OpenProcess returned a new, owned handle.
        let process = unsafe { OwnedHandle::from_raw_handle(handle) };
        let mut created = FILETIME::default();
        let mut exit = FILETIME::default();
        let mut kernel = FILETIME::default();
        let mut user = FILETIME::default();
        // SAFETY: The process is retained and the output values are writable.
        check(unsafe { GetProcessTimes(handle, &mut created, &mut exit, &mut kernel, &mut user) })?;
        if (u64::from(created.dwHighDateTime) << 32 | u64::from(created.dwLowDateTime)) != started {
            return Err(denied("provisional Setup process generation differs"));
        }
        require_running(&process)?;
        Ok(process)
    }

    fn require_running(process: &OwnedHandle) -> io::Result<()> {
        // SAFETY: The borrowed process handle has wait access and is live.
        if unsafe { WaitForSingleObject(process.as_raw_handle(), 0) } != WAIT_TIMEOUT {
            return Err(denied("provisional Setup process is no longer active"));
        }
        Ok(())
    }

    fn scratch_owner() -> io::Result<Vec<u8>> {
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
            if elevated != 0
                || principal.is_null()
                || IsWellKnownSid(principal, WinLocalSystemSid) != 0
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
    fn accepts(bytes: &[u8], owner: &[u8], directory: bool, root: bool) -> Option<bool> {
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
}

fn main() -> std::process::ExitCode {
    #[cfg(windows)]
    if std::env::args_os().nth(1).is_none() {
        return match scratch::run() {
            Ok(()) => std::process::ExitCode::SUCCESS,
            Err(error) => {
                eprintln!("{error}");
                std::process::ExitCode::from(3)
            }
        };
    }
    std::process::ExitCode::from(2)
}

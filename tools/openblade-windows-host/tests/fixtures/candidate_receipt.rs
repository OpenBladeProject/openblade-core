//! Standard-token NTFS rehearsal for both candidate receipt phases.

#[cfg(windows)]
#[path = "../../src/candidate_receipt.rs"]
mod candidate_receipt;
#[cfg(windows)]
#[allow(dead_code)] // The shared adapter also carries the installed status methods.
#[path = "../../src/windows/scm/installed/readiness_windows.rs"]
mod readiness_pipe;
#[cfg(windows)]
#[path = "../../src/service_frame.rs"]
mod service_frame;
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
        io::{self, Read, Write},
        mem::size_of_val,
        os::windows::{
            ffi::OsStrExt,
            io::{AsRawHandle, FromRawHandle, OwnedHandle},
        },
        path::Path,
        slice,
    };
    use windows_sys::Win32::{
        Foundation::{FILETIME, WAIT_TIMEOUT},
        Globalization::{CSTR_EQUAL, CompareStringOrdinal},
        Security::{
            GetLengthSid, GetTokenInformation, IsWellKnownSid, SE_DACL_PRESENT, SE_DACL_PROTECTED,
            SE_SELF_RELATIVE, SECURITY_MAX_SID_SIZE, TOKEN_USER, TokenElevation, TokenUser,
            WinLocalSystemSid,
        },
        Storage::FileSystem::FILE_ALL_ACCESS,
        System::Threading::{
            GetProcessTimes, OpenProcess, PROCESS_QUERY_LIMITED_INFORMATION, PROCESS_SYNCHRONIZE,
            QueryFullProcessImageNameW, WaitForSingleObject,
        },
        UI::Shell::FOLDERID_LocalAppData,
    };

    use crate::{
        candidate_receipt,
        readiness_pipe::{Logon, NativeEndpoint, Stream},
        service_frame::{FRAME_TIMEOUT, is_guid, read_payload, write_frame},
        service_install_identity::{INSTALL_DIRECTORY, known_folder},
        windows_protected_path::{denied, dword, section, sid, verify_descriptor, word},
        windows_token::{check, process_token},
    };
    use serde::{Deserialize, Serialize};
    use sha2::{Digest, Sha256};
    use std::time::Duration;
    use tokio::{io::AsyncReadExt, time::timeout};

    const STARTUP_TIMEOUT: Duration = Duration::from_secs(10);

    #[derive(Deserialize, Serialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct StartupFrame {
        frame: String,
        version: u32,
        request_id: String,
        deployment_request_id: String,
        step: u8,
        commitment: String,
    }

    pub(super) fn run() -> io::Result<()> {
        let owner = scratch_owner()?;
        // Only committed disk inspection accepts a digest on stdin. Startup
        // accepts routing and the inherited broker launch secret there.
        const COMMITTED_INPUT: usize = 68;
        const PIPE_INPUT: usize = 36 + 36 + 32;
        let mut input = Vec::new();
        io::stdin()
            .take((PIPE_INPUT + 1) as u64)
            .read_to_end(&mut input)?;
        if !matches!(input.len(), COMMITTED_INPUT | PIPE_INPUT) {
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
        let scratch = known_folder(&FOLDERID_LocalAppData)?
            .join("Temp")
            .join(format!("OpenBlade.CandidateReceipt-{short}"));
        let install = scratch.join("program-files").join(INSTALL_DIRECTORY);
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
        if input.len() == PIPE_INPUT {
            let endpoint_id = std::str::from_utf8(&input[36..72]).map_err(io::Error::other)?;
            let launch_secret_digest = Sha256::digest(&input[72..]).into();
            let receiver = scratch
                .join("receiver")
                .join("OpenBlade.CandidateReceipt.Scratch.exe");
            tokio::runtime::Builder::new_current_thread()
                .enable_time()
                .build()?
                .block_on(serve_startup(
                    endpoint_id,
                    &receiver,
                    |first, setup_identity| {
                        candidate_receipt::read_provisional(
                            &install,
                            scope,
                            check_directory,
                            check_file,
                            candidate_receipt::digest(&first.commitment[..64])?,
                            candidate_receipt::ActiveBinding {
                                digest: candidate_receipt::digest(&first.commitment[64..128])?,
                                setup_identity,
                                deployment_request_id: &first.deployment_request_id,
                                launch_secret_digest,
                            },
                        )
                    },
                ))?;
        } else {
            let expected = input[36..68]
                .try_into()
                .expect("checked fixture input length");
            let _disk =
                candidate_receipt::read(&install, scope, check_directory, check_file, || {
                    Some(expected)
                })?;
        }
        Ok(())
    }

    async fn serve_startup(
        endpoint_id: &str,
        receiver: &Path,
        verify: impl FnOnce(
            &StartupFrame,
            [u8; 12],
        ) -> io::Result<candidate_receipt::ProvisionalStartup>,
    ) -> io::Result<()> {
        let (mut endpoint, session) = NativeEndpoint::open_scratch(endpoint_id)?;
        // Tell the launcher it can connect after the first instance is parked.
        // This byte carries no readiness evidence.
        let mut output = io::stdout().lock();
        output.write_all(b"1")?;
        output.flush()?;
        drop(output);
        let mut stream = timeout(STARTUP_TIMEOUT, endpoint.accept()).await??;
        let (mut first, setup_pid) = read_request(&mut *stream, 1, session).await?;
        let (setup, identity) = pin_setup(setup_pid, receiver)?;
        let identity_hex: String = identity.iter().map(|byte| format!("{byte:02x}")).collect();
        if first.commitment[128..] != identity_hex {
            return Err(denied("provisional Setup process generation differs"));
        }
        let startup = verify(&first, identity)?;
        require_running(&setup)?;
        first.frame = "provisionalReady".into();
        write_frame(&mut *stream, &first).await?;
        let (mut readback, readback_pid) = read_request(&mut *stream, 2, session).await?;
        require_running(&setup)?;
        if readback_pid != setup_pid
            || readback.deployment_request_id != first.deployment_request_id
            || readback.commitment != first.commitment
            || readback.request_id == first.request_id
        {
            return Err(denied(
                "scratch readiness readback differs from the retained Setup proof",
            ));
        }
        // Release the receipt and both roles for Setup's independent commit
        // verification. Keep this connection and process alive for that decision.
        drop(startup);
        readback.frame = "provisionalReady".into();
        write_frame(&mut *stream, &readback).await?;
        let mut extra = [0];
        if timeout(Duration::from_secs(20), stream.read(&mut extra)).await?? != 0 {
            return Err(denied("unexpected data after scratch readiness readback"));
        }
        drop(stream);
        endpoint.retire().await
    }

    async fn read_request(
        stream: &mut dyn Stream,
        step: u8,
        session: u32,
    ) -> io::Result<(StartupFrame, u32)> {
        let limit = if step == 1 {
            STARTUP_TIMEOUT
        } else {
            FRAME_TIMEOUT
        };
        let bytes = timeout(limit, read_payload(stream)).await??;
        let caller = stream.caller_after_read()?;
        if caller.session != session
            || !matches!(caller.logon, Logon::Interactive | Logon::RemoteInteractive)
        {
            return Err(denied("scratch readiness reached a different Setup peer"));
        }
        let pid = caller
            .pid
            .filter(|pid| *pid != 0)
            .ok_or_else(|| denied("scratch readiness has no kernel-observed Setup peer"))?;
        let frame: StartupFrame = serde_json::from_slice(&bytes)?;
        if frame.frame != "verifyProvisional"
            || frame.version != 79
            || !is_guid(&frame.request_id)
            || !is_guid(&frame.deployment_request_id)
            || frame.step != step
            || !candidate_receipt::lower_hex(&frame.commitment, 152)
        {
            return Err(denied(
                "scratch readiness request differs from the verified startup",
            ));
        }
        Ok((frame, pid))
    }

    fn pin_setup(pid: u32, receiver: &Path) -> io::Result<(OwnedHandle, [u8; 12])> {
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
        let expected: Vec<u16> = receiver.as_os_str().encode_wide().collect();
        let mut image = [0u16; 512];
        let mut len = image.len() as u32;
        // SAFETY: The retained process and the writable name buffer are live.
        check(unsafe { QueryFullProcessImageNameW(handle, 0, image.as_mut_ptr(), &mut len) })?;
        // SAFETY: Both paths have the specified UTF-16 length.
        if unsafe {
            CompareStringOrdinal(
                image.as_ptr(),
                len as i32,
                expected.as_ptr(),
                expected.len() as i32,
                1,
            )
        } != CSTR_EQUAL
        {
            return Err(denied(
                "provisional Setup peer is not the fixed scratch receiver image",
            ));
        }
        require_running(&process)?;
        let mut identity = [0; 12];
        identity[..4].copy_from_slice(&pid.to_le_bytes());
        identity[4..8].copy_from_slice(&created.dwLowDateTime.to_le_bytes());
        identity[8..].copy_from_slice(&created.dwHighDateTime.to_le_bytes());
        Ok((process, identity))
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

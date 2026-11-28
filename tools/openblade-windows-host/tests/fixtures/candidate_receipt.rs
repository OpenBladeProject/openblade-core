//! Standard-token NTFS rehearsal for both candidate receipt phases.

#[cfg(windows)]
#[path = "../../src/candidate_receipt.rs"]
mod candidate_receipt;
#[cfg(windows)]
#[allow(dead_code)] // This fixture only reads the server identity; it retains no monitor.
#[path = "../../src/pipe_server_identity.rs"]
mod pipe_server_identity;
#[cfg(windows)]
#[allow(dead_code)] // The shared adapter also carries the installed status methods.
#[path = "../../src/windows/scm/installed/readiness_windows.rs"]
mod readiness_pipe;
#[cfg(windows)]
#[path = "../../src/retained_process.rs"]
mod retained_process;
#[cfg(windows)]
mod scratch_child_channels;
#[cfg(windows)]
#[path = "../../src/scratch_receipt_policy.rs"]
mod scratch_receipt_policy;
#[cfg(windows)]
#[path = "../../src/scratch_receiver_closure.rs"]
mod scratch_receiver_closure;
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
        os::windows::{
            ffi::OsStrExt,
            io::{AsRawHandle, FromRawHandle, OwnedHandle},
        },
        path::Path,
    };
    use windows_sys::Win32::{
        Foundation::{FILETIME, WAIT_TIMEOUT},
        Globalization::{CSTR_EQUAL, CompareStringOrdinal},
        System::Threading::{
            GetProcessTimes, OpenProcess, PROCESS_QUERY_INFORMATION, PROCESS_SYNCHRONIZE,
            QueryFullProcessImageNameW, WaitForSingleObject,
        },
        UI::Shell::FOLDERID_LocalAppData,
    };

    use crate::{
        candidate_receipt,
        readiness_pipe::{Logon, NativeEndpoint, Stream},
        service_frame::{FRAME_TIMEOUT, is_guid, read_payload, write_frame},
        service_install_identity::{INSTALL_DIRECTORY, known_folder},
        windows_protected_path::{denied, verify_descriptor},
        windows_token::check,
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
        version: i32,
        request_id: String,
        deployment_request_id: String,
        step: u8,
        commitment: String,
    }

    pub(super) fn run(source: &mut dyn Read, output: &mut dyn Write) -> io::Result<()> {
        let owner = crate::scratch_receipt_policy::owner()?;
        // Only committed disk inspection accepts a digest on stdin. Startup
        // accepts routing and the inherited broker launch secret there.
        const COMMITTED_INPUT: usize = 68;
        const PIPE_INPUT: usize = 36 + 36 + 32;
        let mut input = Vec::new();
        source
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
                verify_descriptor(file, |bytes| {
                    crate::scratch_receipt_policy::accepts(bytes, &owner, true, index == scratch)
                })?;
            }
            Ok(())
        };
        let check_file = |file: &std::fs::File| {
            verify_descriptor(file, |bytes| {
                crate::scratch_receipt_policy::accepts(bytes, &owner, false, false)
            })
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
                    output,
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
        output: &mut dyn Write,
        verify: impl FnOnce(
            &StartupFrame,
            [u8; 12],
        ) -> io::Result<candidate_receipt::ProvisionalStartup>,
    ) -> io::Result<()> {
        let (mut endpoint, session) = NativeEndpoint::open_scratch(endpoint_id)?;
        // Tell the launcher it can connect after the first instance is parked.
        // This byte carries no readiness evidence.
        output.write_all(b"1")?;
        output.flush()?;
        let mut stream = timeout(STARTUP_TIMEOUT, endpoint.accept()).await??;
        let (mut first, setup_pid) = read_request(&mut *stream, 1, session).await?;
        let (setup, identity) = pin_setup(setup_pid, receiver)?;
        candidate_receipt::verify_setup_generation(&first.commitment, identity)?;
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
        let mut startup = startup;
        startup.require_unchanged()?;
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
            || frame.version != crate::service_frame::PROTOCOL_VERSION
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
        // Request full process-query rights for mitigation-policy readback;
        // do not rely on the narrower rights sufficient for an image path.
        // SAFETY: Query and wait only, with no handle inheritance.
        let handle =
            unsafe { OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_SYNCHRONIZE, 0, pid) };
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
        crate::scratch_receiver_closure::require_process_policies(process.as_raw_handle())?;
        // SAFETY: The borrowed process handle has wait access and is live.
        if unsafe { WaitForSingleObject(process.as_raw_handle(), 0) } != WAIT_TIMEOUT {
            return Err(denied("provisional Setup process is no longer active"));
        }
        Ok(())
    }
}

fn main() -> std::process::ExitCode {
    #[cfg(windows)]
    {
        use std::io::Write;
        let args: Vec<String> = std::env::args().skip(1).collect();
        let result = if args.is_empty() {
            scratch::run(&mut std::io::stdin().lock(), &mut std::io::stdout().lock())
        } else {
            match scratch_child_channels::open(&args) {
                Ok([mut input, mut output, mut error]) => {
                    let result = scratch::run(&mut input, &mut output);
                    if let Err(ref failure) = result {
                        let _ = writeln!(error, "{failure}");
                    }
                    return if result.is_ok() {
                        std::process::ExitCode::SUCCESS
                    } else {
                        std::process::ExitCode::from(3)
                    };
                }
                Err(error) => Err(error),
            }
        };
        return match result {
            Ok(()) => std::process::ExitCode::SUCCESS,
            Err(error) => {
                eprintln!("{error}");
                std::process::ExitCode::from(3)
            }
        };
    }
    #[cfg(not(windows))]
    std::process::ExitCode::from(2)
}

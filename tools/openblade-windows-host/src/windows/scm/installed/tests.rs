use std::{cell::RefCell, future, ptr::null_mut};

use windows_sys::Win32::{
    Foundation::{ERROR_GEN_FAILURE, NO_ERROR},
    System::Services::{SERVICE_RUNNING, SERVICE_STOP_PENDING, SERVICE_STOPPED},
};

use super::*;
use crate::scm_identity as identity;

const PROGRAM_FILES: &str = r"C:\Program Files";
const PID: u32 = 42;

fn process() -> ProcessEvidence {
    ProcessEvidence {
        has_arguments: false,
        selected_image: None,
        program_files: PROGRAM_FILES.into(),
        image: PathBuf::from(PROGRAM_FILES)
            .join("OpenBlade")
            .join(IMAGE_NAME),
        pid: PID,
        local_system: true,
        session: 0,
    }
}

fn registration() -> ScmEvidence {
    ScmEvidence {
        process_id: PID,
        service_type: SERVICE_WIN32_OWN_PROCESS,
        state: SERVICE_START_PENDING,
        matches_config: true,
    }
}

fn readiness() -> Readiness {
    Readiness {
        pipe_name: PIPE_NAME,
        server_pid: PID,
        protocol_version: PROTOCOL_VERSION,
        correlated_status: true,
    }
}

#[test]
fn only_the_installed_process_can_dispatch() {
    for rejected in [
        ProcessEvidence {
            has_arguments: true,
            ..process()
        },
        ProcessEvidence {
            image: "openblade-windows-host.exe".into(),
            ..process()
        },
        ProcessEvidence {
            image: "openblade-windows-scm-vm.exe".into(),
            ..process()
        },
        ProcessEvidence {
            image: PathBuf::from(r"C:\Program Files\OpenBlade\.\OpenBlade.Service.exe"),
            ..process()
        },
        ProcessEvidence {
            local_system: false,
            ..process()
        },
        ProcessEvidence {
            session: 1,
            ..process()
        },
        ProcessEvidence {
            pid: 0,
            ..process()
        },
    ] {
        assert_eq!(
            dispatch_candidate(|| Ok(rejected), || panic!("unexpected dispatcher"))
                .unwrap_err()
                .kind(),
            io::ErrorKind::PermissionDenied
        );
    }
    dispatch_candidate(|| Ok(process()), || Ok(())).unwrap();
    assert_eq!(
        LaunchIdentity::verify(process()).unwrap().image_command(),
        r#""C:\Program Files\OpenBlade\OpenBlade.Service.exe""#,
    );
}

#[test]
#[allow(unsafe_code)]
fn callback_accepts_only_the_installed_name_and_no_start_arguments() {
    for (name, argc, allowed) in [
        (NAME, 1, true),
        (NAME, 2, false),
        ("OpenBlade Service", 1, false),
        ("OpenBladeRustReadOnlyDev", 1, false),
        ("OpenBladeRustPhase5jVm", 1, false),
    ] {
        let mut name: Vec<_> = name.encode_utf16().chain([0]).collect();
        let mut argv = [name.as_mut_ptr(), null_mut()];
        // SAFETY: argv has argc entries and the first is NUL-terminated.
        assert_eq!(
            unsafe { identity::callback_matches(NAME, argc, argv.as_mut_ptr()) },
            allowed
        );
    }
}

#[tokio::test]
async fn failed_identity_or_prerequisites_never_prepare_the_endpoint() {
    for (scm, allowed) in [
        (
            ScmEvidence {
                matches_config: false,
                ..registration()
            },
            false,
        ),
        (
            ScmEvidence {
                process_id: 0,
                ..registration()
            },
            false,
        ),
        (
            ScmEvidence {
                process_id: PID + 1,
                ..registration()
            },
            false,
        ),
        (
            ScmEvidence {
                state: SERVICE_RUNNING,
                ..registration()
            },
            false,
        ),
        (
            ScmEvidence {
                service_type: 0x20,
                ..registration()
            },
            false,
        ),
        (registration(), true),
    ] {
        let controls = Controls::new();
        let phases = RefCell::new(Vec::new());
        let result = run_candidate::<(), future::Ready<io::Result<()>>>(
            &controls,
            controls.arm(),
            || Ok(process()),
            |_| Ok(scm),
            |_| {
                assert!(allowed);
                Err(denied("missing installed candidate admission"))
            },
            async |_, _, _, _| panic!("unexpected endpoint preparation"),
            |status| {
                phases
                    .borrow_mut()
                    .push((status.dwCurrentState, status.dwWin32ExitCode));
                Ok(())
            },
        )
        .await;
        assert!(result.is_err());
        assert_eq!(
            phases.into_inner(),
            [
                (SERVICE_START_PENDING, NO_ERROR),
                (SERVICE_STOPPED, ERROR_GEN_FAILURE)
            ]
        );
    }
}

#[tokio::test]
async fn readiness_and_stop_order() {
    for (ready, expected_running) in [
        (
            Readiness {
                pipe_name: "OpenBlade.Rust.ReadOnly.Dev.scm",
                ..readiness()
            },
            false,
        ),
        (
            Readiness {
                server_pid: PID + 1,
                ..readiness()
            },
            false,
        ),
        (
            Readiness {
                protocol_version: 51,
                ..readiness()
            },
            false,
        ),
        (
            Readiness {
                correlated_status: false,
                ..readiness()
            },
            false,
        ),
        (readiness(), true),
    ] {
        let controls = Controls::new();
        let events = RefCell::new(Vec::new());
        let result = run_candidate(
            &controls,
            controls.arm(),
            || {
                events.borrow_mut().push("process");
                Ok(process())
            },
            |_| {
                events.borrow_mut().push("scm");
                Ok(registration())
            },
            |_| {
                events.borrow_mut().push("admission");
                Ok(())
            },
            async |_, (), shutdown, _| {
                events.borrow_mut().push("prepare");
                Ok((ready, async {
                    shutdown.await.unwrap();
                    events.borrow_mut().push("retired");
                    Ok(())
                }))
            },
            |status| {
                let event = match status.dwCurrentState {
                    SERVICE_START_PENDING => "starting",
                    SERVICE_RUNNING => {
                        controls.request_stop();
                        "running"
                    }
                    SERVICE_STOP_PENDING => "stopping",
                    SERVICE_STOPPED if status.dwWin32ExitCode == NO_ERROR => "stopped",
                    SERVICE_STOPPED => "failed",
                    _ => panic!("unexpected state"),
                };
                events.borrow_mut().push(event);
                Ok(())
            },
        )
        .await;
        assert_eq!(result.is_ok(), expected_running);
        let mut expected = vec!["starting", "process", "scm", "admission", "prepare"];
        if expected_running {
            expected.extend(["running", "stopping", "retired", "stopped"]);
        } else {
            expected.extend(["retired", "failed"]);
        }
        assert_eq!(events.into_inner(), expected);
    }
}

#[tokio::test]
async fn stop_before_prepare_cancels_startup() {
    let controls = Controls::new();
    let phases = RefCell::new(Vec::new());
    run_candidate::<(), future::Ready<io::Result<()>>>(
        &controls,
        controls.arm(),
        || Ok(process()),
        |_| Ok(registration()),
        |_| {
            controls.request_stop();
            Ok(())
        },
        async |_, _, _, _| panic!("prepare must not run after STOP"),
        |status| {
            phases
                .borrow_mut()
                .push((status.dwCurrentState, status.dwWin32ExitCode));
            Ok(())
        },
    )
    .await
    .unwrap();
    assert_eq!(
        phases.into_inner(),
        [
            (SERVICE_START_PENDING, NO_ERROR),
            (SERVICE_STOPPED, NO_ERROR)
        ]
    );
}

#[tokio::test]
async fn stop_during_prepare_still_reports_retirement_failure() {
    let controls = Controls::new();
    let phases = RefCell::new(Vec::new());
    let error = run_candidate(
        &controls,
        controls.arm(),
        || Ok(process()),
        |_| Ok(registration()),
        |_| Ok(()),
        async |_, (), shutdown, _| {
            controls.request_stop();
            Ok((readiness(), async {
                shutdown.await.unwrap();
                Err(io::Error::other("provider retirement failed"))
            }))
        },
        |status| {
            phases
                .borrow_mut()
                .push((status.dwCurrentState, status.dwWin32ExitCode));
            Ok(())
        },
    )
    .await
    .unwrap_err();
    assert_eq!(error.to_string(), "provider retirement failed");
    assert_eq!(
        phases.into_inner(),
        [
            (SERVICE_START_PENDING, NO_ERROR),
            (SERVICE_STOPPED, ERROR_GEN_FAILURE)
        ]
    );
}

#[tokio::test(start_paused = true)]
async fn lost_scm_stop_sender_is_not_a_successful_stop() {
    let controls = Controls::new();
    let (sender, receiver) = oneshot::channel();
    drop(sender);
    let statuses = RefCell::new(Vec::new());
    let result = run_candidate(
        &controls,
        receiver,
        || Ok(process()),
        |_| Ok(registration()),
        |_| Ok(()),
        async |_, (), _, _| Ok((readiness(), std::future::pending::<io::Result<()>>())),
        |status| {
            statuses
                .borrow_mut()
                .push((status.dwCurrentState, status.dwWin32ExitCode));
            Ok(())
        },
    )
    .await;
    assert_eq!(result.unwrap_err().kind(), io::ErrorKind::BrokenPipe);
    assert_eq!(
        statuses.borrow().last(),
        Some(&(SERVICE_STOPPED, ERROR_GEN_FAILURE))
    );
}

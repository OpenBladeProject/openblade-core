use std::{cell::Cell, time::Duration};

use openblade_windows_host::configuration_writer::fixture;
use tokio::{
    io::{AsyncReadExt, AsyncWriteExt},
    net::windows::named_pipe::ClientOptions,
    sync::mpsc,
    time::timeout,
};

use super::*;
use crate::windows::{
    PREFIX,
    pipe::Endpoint,
    prepare_battery_profiles, prepare_protected,
    tests::{assert_closed, open_pipe, read_status, runtime, test_id},
};

fn phase(status: &SERVICE_STATUS) -> (u32, u32, u32) {
    (
        status.dwCurrentState,
        status.dwControlsAccepted,
        status.dwWin32ExitCode,
    )
}

#[test]
fn scripted_controls_run_the_composed_fixture_and_retire_the_endpoint() {
    runtime().block_on(async {
        let controls = Controls(Mutex::new(None));
        let id = test_id();
        let path = format!("{PREFIX}{id}");
        let (ready, wait_ready) = oneshot::channel();
        let mut ready = Some(ready);
        let (writer_held, wait_writer_held) = oneshot::channel();
        let stopped = Cell::new(false);
        let (_steps, advance) = mpsc::channel(1);
        let mut phases = Vec::new();
        let host = run_service(
            &controls,
            controls.arm(),
            &id,
            Session::Active,
            |shutdown| {
                let fixture = fixture::Fixture::create(true)?;
                let (profiles, writer) = fixture.compose_battery(false)?;
                let prepared = prepare_battery_profiles(
                    Some(advance),
                    shutdown,
                    profiles,
                    Some(writer),
                    true,
                )?;
                writer_held
                    .send(prepared.0.hold_profile_writer())
                    .ok()
                    .unwrap();
                Ok(prepared)
            },
            |status| {
                phases.push(phase(status));
                if status.dwCurrentState == SERVICE_STOPPED {
                    stopped.set(true);
                }
                match status.dwCurrentState {
                    SERVICE_RUNNING => ready.take().unwrap().send(()).unwrap(),
                    SERVICE_START_PENDING | SERVICE_STOPPED => assert_eq!(
                        ClientOptions::new().open(&path).unwrap_err().kind(),
                        io::ErrorKind::NotFound
                    ),
                    _ => {}
                }
                Ok(())
            },
        );
        let client = async {
            wait_ready.await.unwrap();
            read_status(&path).await;
            let writer = wait_writer_held.await.unwrap();
            let mut pipe = open_pipe(&path).await;
            pipe.write_all(&[10]).await.unwrap();
            assert_eq!(controls.handle(SERVICE_CONTROL_SHUTDOWN), NO_ERROR);
            assert_eq!(controls.handle(SERVICE_CONTROL_STOP), NO_ERROR);
            assert_closed(pipe.read(&mut [0]).await);
            assert!(!stopped.get());
            let endpoint = Endpoint::new(path.clone()).unwrap();
            assert_eq!(
                endpoint.claim().unwrap_err().kind(),
                io::ErrorKind::PermissionDenied
            );
            drop(writer);
        };
        let (result, ()) = timeout(Duration::from_secs(5), async { tokio::join!(host, client) })
            .await
            .unwrap();
        result.unwrap();
        assert_eq!(
            phases,
            [
                (SERVICE_START_PENDING, 0, NO_ERROR),
                (
                    SERVICE_RUNNING,
                    SERVICE_ACCEPT_STOP | SERVICE_ACCEPT_SHUTDOWN,
                    NO_ERROR
                ),
                (SERVICE_STOP_PENDING, 0, NO_ERROR),
                (SERVICE_STOPPED, 0, NO_ERROR),
            ]
        );
    });
}

#[test]
fn scripted_stop_during_fixture_admission_never_publishes_running() {
    runtime().block_on(async {
        let controls = Controls(Mutex::new(None));
        let id = test_id();
        let (_steps, advance) = mpsc::channel(1);
        let mut phases = Vec::new();
        let result = run_service(
            &controls,
            controls.arm(),
            &id,
            Session::Active,
            |shutdown| {
                let (profiles, writer) = fixture::Fixture::create(true)?.compose_battery(false)?;
                assert_eq!(controls.handle(SERVICE_CONTROL_STOP), NO_ERROR);
                prepare_battery_profiles(Some(advance), shutdown, profiles, Some(writer), true)
            },
            |status| {
                phases.push(phase(status));
                Ok(())
            },
        )
        .await;
        assert_eq!(result.unwrap_err().kind(), io::ErrorKind::Interrupted);
        let late_ready = controls.publish(Phase::Running, &mut |_| panic!("RUNNING after STOP"));
        assert_eq!(late_ready.unwrap_err().kind(), io::ErrorKind::Interrupted);
        assert_eq!(
            phases,
            [
                (SERVICE_START_PENDING, 0, NO_ERROR),
                (SERVICE_STOPPED, 0, ERROR_GEN_FAILURE),
            ]
        );
        assert_eq!(
            ClientOptions::new()
                .open(&format!("{PREFIX}{id}"))
                .unwrap_err()
                .kind(),
            io::ErrorKind::NotFound
        );
    });
}

#[test]
fn scripted_startup_refusals_never_publish_running() {
    runtime().block_on(async {
        let controls = Controls(Mutex::new(None));
        let id = test_id();
        let path = format!("{PREFIX}{id}");
        let endpoint = Endpoint::new(path.clone()).unwrap();
        let claim = endpoint.claim().unwrap();

        let (_steps, advance) = mpsc::channel(1);
        assert_startup_refused(&controls, &id, |shutdown| {
            let (profiles, writer) = fixture::Fixture::create(true)?.compose_battery(false)?;
            prepare_battery_profiles(Some(advance), shutdown, profiles, Some(writer), true)
        })
        .await;
        assert_eq!(
            endpoint.claim().unwrap_err().kind(),
            io::ErrorKind::PermissionDenied
        );
        drop(claim);

        let fixture = fixture::Fixture::create(false).unwrap();
        fixture.seed().unwrap();
        fixture.update(true).unwrap_err();
        let (_steps, advance) = mpsc::channel(1);
        let error = assert_startup_refused(&controls, &id, |shutdown| {
            let (profiles, writer) = fixture.compose_battery(false)?;
            prepare_battery_profiles(Some(advance), shutdown, profiles, Some(writer), true)
        })
        .await;
        assert!(
            error.to_string().contains("Preserve(RecoveryRequired)"),
            "{error}"
        );

        let (_steps, advance) = mpsc::channel(1);
        let error = assert_startup_refused(&controls, &id, |shutdown| {
            prepare_protected(std::path::Path::new("unadmitted"), Some(advance), shutdown)
        })
        .await;
        assert!(
            error.to_string().contains("dedicated Phase 4j fixture"),
            "{error}"
        );
    });
}

async fn assert_startup_refused<F: Future<Output = io::Result<()>>>(
    controls: &Controls,
    id: &str,
    prepare: impl FnOnce(oneshot::Receiver<()>) -> io::Result<(Status, Option<F>)>,
) -> io::Error {
    let mut phases = Vec::new();
    let error = run_service(
        controls,
        controls.arm(),
        id,
        Session::Active,
        prepare,
        |status| {
            phases.push(phase(status));
            Ok(())
        },
    )
    .await
    .unwrap_err();
    assert_eq!(
        phases,
        [
            (SERVICE_START_PENDING, 0, NO_ERROR),
            (SERVICE_STOPPED, 0, ERROR_GEN_FAILURE),
        ]
    );
    error
}

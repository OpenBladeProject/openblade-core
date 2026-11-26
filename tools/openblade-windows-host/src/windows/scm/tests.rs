use std::cell::Cell;

use openblade_windows_host::configuration_writer::fixture;
use tokio::{
    io::{AsyncReadExt, AsyncWriteExt},
    net::windows::named_pipe::ClientOptions,
    sync::mpsc,
    time::timeout,
};

use windows_sys::Win32::{
    Foundation::{ERROR_GEN_FAILURE, NO_ERROR},
    System::Services::{
        SERVICE_CONTROL_SHUTDOWN, SERVICE_CONTROL_STOP, SERVICE_RUNNING, SERVICE_START_PENDING,
        SERVICE_STOP_PENDING, SERVICE_STOPPED,
    },
};

use super::*;
use crate::battery_runtime::{ProfileReplay, ProfileSaveBarrier};
use crate::scm_host::{Phase, control};
use crate::windows::{
    PREFIX,
    pipe::Endpoint,
    prepare_battery_profiles, prepare_integrated, prepare_protected,
    tests::{HANG_GUARD, assert_closed, open_pipe, read_status, runtime, test_id},
};

// Spelled out instead of reusing scm_host's mask, so a production change fails here.
// STOP (0x1) | SHUTDOWN (0x4), plus POWEREVENT (0x40) | SESSIONCHANGE (0x80) when
// the thermal composition is compiled.
const EXPECTED_RUNNING_CONTROLS: u32 = if cfg!(feature = "service-thermal-mutation-v1") {
    0xC5
} else {
    0x05
};

fn phase(status: &SERVICE_STATUS) -> (u32, u32, u32) {
    (
        status.dwCurrentState,
        status.dwControlsAccepted,
        status.dwWin32ExitCode,
    )
}

#[test]
fn native_control_trampoline_signals_the_registered_stop_channel() {
    use std::ptr::null_mut;
    use windows_sys::Win32::{
        Foundation::NO_ERROR,
        System::Services::{SERVICE_CONTROL_INTERROGATE, SERVICE_CONTROL_STOP},
    };

    let mut stopping = CONTROLS.arm();
    // SAFETY: this callback ignores its event and context pointers.
    unsafe {
        assert_eq!(
            control(SERVICE_CONTROL_INTERROGATE, 0, null_mut(), null_mut()),
            NO_ERROR
        );
        assert_eq!(
            stopping.try_recv(),
            Err(oneshot::error::TryRecvError::Empty)
        );
        for _ in 0..2 {
            assert_eq!(
                control(SERVICE_CONTROL_STOP, 0, null_mut(), null_mut()),
                NO_ERROR
            );
        }
    }
    assert_eq!(stopping.try_recv(), Ok(()));
}

#[test]
fn scripted_controls_wait_for_both_the_accepted_save_and_owned_renderer() {
    runtime().block_on(async {
        let controls = Controls::new();
        let id = test_id();
        let path = format!("{PREFIX}{id}");
        let (ready, wait_ready) = oneshot::channel();
        let mut ready = Some(ready);
        let (request, wait_request) = oneshot::channel();
        let (entered, wait_entered) = oneshot::channel();
        let (resume, wait_resume) = oneshot::channel();
        let (saved, wait_saved) = oneshot::channel();
        let stopped = Cell::new(false);
        let (_steps, advance) = mpsc::channel(1);
        let (release_port, wait_release) = oneshot::channel();
        let (watch, wait_watch) = oneshot::channel();
        let mut phases = Vec::new();
        let host = run_service(
            &controls,
            controls.arm(),
            &id,
            Session::Active,
            |shutdown| {
                let fixture = fixture::Fixture::create(true)?;
                let (profiles, writer) = fixture.compose_battery(false)?;
                let mut changed = profiles.clone();
                changed["batteryPolicy"]["chargeLimitPercent"] = 75.into();
                request
                    .send(serde_json::to_vec(&serde_json::json!({
                        "operation": "updateProfiles",
                        "version": 91,
                        "requestId": "11111111-2222-4333-8444-555555555555",
                        "profiles": changed
                    }))?)
                    .unwrap();
                let prepared = prepare_integrated(
                    Some(advance),
                    Some(wait_release),
                    shutdown,
                    profiles,
                    writer,
                    false,
                )?;
                let status = prepared.0.set_profile_save_barrier(ProfileSaveBarrier {
                    entered,
                    resume: wait_resume,
                    saved,
                });
                assert!(watch.send(status).is_ok());
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
            let mut status = wait_watch.await.unwrap();
            status
                .wait_for(|value| value["message"] == "Renderer frame in flight.")
                .await
                .unwrap();
            let request = wait_request.await.unwrap();
            let mut pipe = open_pipe(&path).await;
            pipe.write_i32_le(request.len() as i32).await.unwrap();
            pipe.write_all(&request).await.unwrap();
            wait_entered.await.unwrap();
            assert_eq!(controls.handle(SERVICE_CONTROL_SHUTDOWN), NO_ERROR);
            assert_eq!(controls.handle(SERVICE_CONTROL_STOP), NO_ERROR);
            assert_closed(pipe.read(&mut [0]).await);
            assert!(!stopped.get());
            let endpoint = Endpoint::new(path.clone(), super::super::PIPE_INSTANCES).unwrap();
            assert_eq!(
                endpoint.claim().unwrap_err().kind(),
                io::ErrorKind::PermissionDenied
            );
            resume.send(()).unwrap();
            assert!(
                wait_saved.await.unwrap(),
                "the admitted fixture write did not commit"
            );
            let drained = status
                .wait_for(|value| value["message"] == "Frame drained. Renderer port still owned.")
                .await
                .unwrap()
                .clone();
            assert_eq!(
                drained["profiles"]["batteryPolicy"]["chargeLimitPercent"],
                75
            );
            assert_eq!(drained["lightingRendererOwnership"], 2);
            assert!(drained["confirmedState"]["lighting"].is_null());
            assert!(!stopped.get());
            assert_eq!(
                endpoint.claim().unwrap_err().kind(),
                io::ErrorKind::PermissionDenied
            );
            release_port.send(()).unwrap();
            status
                .wait_for(|value| value["message"] == "Renderer port retired.")
                .await
                .unwrap();
        };
        let (result, ()) = timeout(HANG_GUARD, async { tokio::join!(host, client) })
            .await
            .unwrap();
        result.unwrap();
        assert_eq!(
            phases,
            [
                (SERVICE_START_PENDING, 0, NO_ERROR),
                (SERVICE_RUNNING, EXPECTED_RUNNING_CONTROLS, NO_ERROR),
                (SERVICE_STOP_PENDING, 0, NO_ERROR),
                (SERVICE_STOPPED, 0, NO_ERROR),
            ]
        );
    });
}

#[test]
fn scripted_stop_during_fixture_admission_never_publishes_running() {
    runtime().block_on(async {
        let controls = Controls::new();
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
                prepare_battery_profiles(
                    Some(advance),
                    shutdown,
                    profiles,
                    Some(writer),
                    ProfileReplay::Thermal,
                )
            },
            |status| {
                phases.push(phase(status));
                Ok(())
            },
        )
        .await;
        result.unwrap();
        let late_ready = controls.publish(Phase::Running, &mut |_| panic!("RUNNING after STOP"));
        assert_eq!(late_ready.unwrap_err().kind(), io::ErrorKind::Interrupted);
        assert_eq!(
            phases,
            [
                (SERVICE_START_PENDING, 0, NO_ERROR),
                (SERVICE_STOPPED, 0, NO_ERROR),
            ]
        );
        assert_eq!(
            ClientOptions::new()
                .open(format!("{PREFIX}{id}"))
                .unwrap_err()
                .kind(),
            io::ErrorKind::NotFound
        );
    });
}

#[test]
fn stop_can_arrive_during_running_status_io() {
    let controls = Controls::new();
    let mut stop = controls.arm();
    controls
        .publish(Phase::Running, &mut |_| {
            assert_eq!(controls.handle(SERVICE_CONTROL_STOP), NO_ERROR);
            Ok(())
        })
        .unwrap();
    stop.try_recv().unwrap();
}

#[test]
fn scripted_startup_refusals_never_publish_running() {
    runtime().block_on(async {
        let controls = Controls::new();
        let id = test_id();
        let path = format!("{PREFIX}{id}");
        let endpoint = Endpoint::new(path.clone(), super::super::PIPE_INSTANCES).unwrap();
        let claim = endpoint.claim().unwrap();

        let (_steps, advance) = mpsc::channel(1);
        assert_startup_refused(&controls, &id, |shutdown| {
            let (profiles, writer) = fixture::Fixture::create(true)?.compose_battery(false)?;
            prepare_battery_profiles(
                Some(advance),
                shutdown,
                profiles,
                Some(writer),
                ProfileReplay::Thermal,
            )
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
            prepare_battery_profiles(
                Some(advance),
                shutdown,
                profiles,
                Some(writer),
                ProfileReplay::Thermal,
            )
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

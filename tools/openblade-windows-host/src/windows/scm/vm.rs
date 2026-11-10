//! Explicit developer build for an independently authorized disposable Windows VM.

use std::time::{Duration, Instant};

use tokio::sync::mpsc;
use windows_sys::Win32::System::Services::{
    SERVICE_START_PENDING, SERVICE_STOP_PENDING, SERVICE_STOPPED,
};

use super::{
    CONTROLS, Phase, SERVICE_STATUS_HANDLE, Session, dispatch_named, io, oneshot,
    registered_service, report, run_service, status_for,
};
use crate::windows::{fixture_admission as admission, prepare_integrated};
use openblade_windows_host::configuration_writer::fixture::BatteryProfileStore;

const PIPE_ID: &str = "phase5j-vm";
const POLL: Duration = Duration::from_millis(20);
const START_LIMIT: Duration = Duration::from_secs(12);
const STOP_LIMIT: Duration = Duration::from_secs(20);

pub(in crate::windows) fn entry() -> io::Result<()> {
    admission::require_process()?;
    dispatch_named(admission::NAME, service_main)
}

unsafe extern "system" fn service_main(argc: u32, argv: *mut *mut u16) {
    // SAFETY: SCM supplied this argv for the duration of the callback.
    if !unsafe { admission::callback_matches(argc, argv) } {
        // An own-process service can be registered under a different name.
        // Exit without registering a control handler for that service.
        std::process::exit(1);
    }
    // Do not unwind across the native ServiceMain callback.
    if std::panic::catch_unwind(service).is_err() {
        std::process::abort();
    }
}

fn service() {
    registered_service(admission::NAME, |runtime, handle, stop| {
        let admitted = (|| {
            report(handle, &status_for(Phase::Starting))?;
            let admitted = admission::admit()?;
            require_absent(&admitted, admission::START_MARKER)?;
            require_absent(&admitted, admission::RENDERER_MARKER)?;
            Ok::<_, io::Error>(admitted)
        })();
        let result = match admitted {
            Ok(admitted) => runtime.block_on(run_admitted(&admitted, handle, stop)),
            Err(error) => Err(error),
        };
        // The shared lifecycle has drained the writer and pipe claim and the
        // admitted image and directory pins have now also been dropped.
        let _ = report(
            handle,
            &status_for(Phase::Stopped {
                failed: result.is_err(),
            }),
        );
    });
}

async fn run_admitted(
    admitted: &admission::FixtureAdmission,
    handle: SERVICE_STATUS_HANDLE,
    stop: oneshot::Receiver<()>,
) -> io::Result<()> {
    // The VM tests IPC and STOP, so power remains at the initial scripted state.
    let (_steps, advance) = mpsc::channel(1);
    let (release, wait_release) = oneshot::channel();
    let (stopping, wait_stopping) = oneshot::channel();
    let mut stopping = Some(stopping);
    let service = run_service(
        &CONTROLS,
        stop,
        PIPE_ID,
        Session::Active,
        |shutdown| {
            wait_for_start(admitted)?;
            require_absent(admitted, admission::RENDERER_MARKER)?;
            let (profiles, writer) = BatteryProfileStore::open_vm_protected()?;
            prepare_integrated(
                Some(advance),
                Some(wait_release),
                shutdown,
                profiles,
                writer,
            )
        },
        |status| {
            if status.dwCurrentState == SERVICE_STOPPED {
                // ServiceMain publishes the sole terminal status after releasing
                // all fixture resources, including its admission handles.
                return Ok(());
            }
            let mut status = *status;
            if status.dwCurrentState == SERVICE_START_PENDING {
                status.dwCheckPoint = 2;
                status.dwWaitHint = 15_000;
            }
            if status.dwCurrentState == SERVICE_STOP_PENDING {
                status.dwWaitHint = 25_000;
                let stopping = stopping.take();
                let absent = require_absent(admitted, admission::RENDERER_MARKER);
                report(handle, &status)?;
                absent?;
                if let Some(stopping) = stopping {
                    let _ = stopping.send(());
                }
                return Ok(());
            }
            report(handle, &status)
        },
    );
    let inputs = release_on_stop(wait_stopping, release, || {
        admitted.marker(admission::RENDERER_MARKER)
    });
    tokio::pin!(service, inputs);
    tokio::select! {
        biased;
        result = &mut service => result,
        inputs = &mut inputs => service.await.and(inputs),
    }
}

fn require_absent(admitted: &admission::FixtureAdmission, marker: &str) -> io::Result<()> {
    if admitted.marker(marker)? {
        return Err(io::Error::other(format!(
            "early VM fixture marker {marker}"
        )));
    }
    Ok(())
}

fn wait_for_start(admitted: &admission::FixtureAdmission) -> io::Result<()> {
    // SCM accepts no controls in START_PENDING. Bound this startup gate within
    // the published wait hint, before the writer or pipe is opened.
    let deadline = Instant::now() + START_LIMIT;
    while Instant::now() < deadline {
        if admitted.marker(admission::START_MARKER)? {
            return Ok(());
        }
        std::thread::sleep(POLL);
    }
    Err(io::Error::new(
        io::ErrorKind::TimedOut,
        "VM fixture startup gate",
    ))
}

async fn release_on_stop(
    stopping: oneshot::Receiver<()>,
    release: oneshot::Sender<()>,
    mut marker: impl FnMut() -> io::Result<bool>,
) -> io::Result<()> {
    stopping.await.map_err(io::Error::other)?;
    tokio::time::timeout(STOP_LIMIT, async {
        while !marker()? {
            tokio::time::sleep(POLL).await;
        }
        Ok::<_, io::Error>(())
    })
    .await??;
    release
        .send(())
        .map_err(|()| io::Error::other("renderer release receiver closed"))
}

#[cfg(test)]
mod tests {
    use super::*;

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
                super::super::control(SERVICE_CONTROL_INTERROGATE, 0, null_mut(), null_mut()),
                NO_ERROR
            );
            assert_eq!(
                stopping.try_recv(),
                Err(oneshot::error::TryRecvError::Empty)
            );
            for _ in 0..2 {
                assert_eq!(
                    super::super::control(SERVICE_CONTROL_STOP, 0, null_mut(), null_mut()),
                    NO_ERROR
                );
            }
        }
        assert_eq!(stopping.try_recv(), Ok(()));
    }

    #[tokio::test]
    async fn renderer_release_is_checked_only_after_stop_and_rejects_bad_input() {
        for admitted in [true, false] {
            let (stop, stopping) = oneshot::channel();
            let (release, mut released) = oneshot::channel();
            let mut checked = false;
            let input = release_on_stop(stopping, release, || {
                checked = true;
                if admitted {
                    Ok(true)
                } else {
                    Err(io::Error::new(io::ErrorKind::PermissionDenied, "test ACL"))
                }
            });
            let client = async {
                tokio::task::yield_now().await;
                assert_eq!(
                    released.try_recv(),
                    Err(oneshot::error::TryRecvError::Empty)
                );
                stop.send(()).unwrap();
                released.await
            };
            let (result, released) = tokio::join!(input, client);
            assert!(checked);
            assert_eq!(result.is_ok(), admitted);
            assert_eq!(released.is_ok(), admitted);
        }
    }
}

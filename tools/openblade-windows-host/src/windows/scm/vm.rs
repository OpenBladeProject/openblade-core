//! Explicit developer build for an independently authorized disposable Windows VM.

use std::time::Duration;

use tokio::sync::mpsc;
use windows_sys::Win32::System::Services::{
    SERVICE_START_PENDING, SERVICE_STATUS_HANDLE, SERVICE_STOP_PENDING, SERVICE_STOPPED,
};

use super::{Session, io, oneshot, run_service};
use crate::scm_host::{CONTROLS, Phase, dispatch_named, registered_service, report, status_for};
use crate::windows::{fixture_admission as admission, prepare_integrated};
use openblade_windows_host::configuration_writer::{
    fixture::BatteryProfileStore, protected::ProtectedStorage,
};

const PIPE_ID: &str = "phase5j-vm";
const POLL: Duration = Duration::from_millis(100);
const START_LIMIT: Duration = Duration::from_secs(12);
// The runbook allows 15 s from peer launch to STOP_PENDING. Its earlier
// Off request already starts the shared 30 s renderer release deadline.
const STOP_LIMIT: Duration = Duration::from_secs(12);

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
            let (admitted, data) = admission::admit()?;
            require_absent(&admitted, admission::START_MARKER)?;
            require_absent(&admitted, admission::RENDERER_MARKER)?;
            Ok::<_, io::Error>((admitted, data))
        })();
        let result = match admitted {
            Ok((admitted, data)) => runtime.block_on(run_admitted(&admitted, data, handle, stop)),
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
    data: ProtectedStorage,
    handle: SERVICE_STATUS_HANDLE,
    mut stop: oneshot::Receiver<()>,
) -> io::Result<()> {
    let mut starting = status_for(Phase::Starting);
    starting.dwCheckPoint = 2;
    starting.dwWaitHint = 15_000;
    report(handle, &starting)?;
    tokio::select! {
        biased;
        _ = &mut stop => return Ok(()),
        result = wait_for_marker(START_LIMIT, || admitted.marker(admission::START_MARKER)) => result?,
    }

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
            require_absent(admitted, admission::RENDERER_MARKER)?;
            let (profiles, writer) = BatteryProfileStore::open_vm_protected(data)?;
            prepare_integrated(
                Some(advance),
                Some(wait_release),
                shutdown,
                profiles,
                writer,
                false,
            )
        },
        |status| {
            if matches!(
                status.dwCurrentState,
                SERVICE_START_PENDING | SERVICE_STOPPED
            ) {
                // ServiceMain reports the gated startup and the sole terminal
                // status after releasing all fixture resources.
                return Ok(());
            }
            if status.dwCurrentState == SERVICE_STOP_PENDING {
                let stopping = stopping.take();
                let mut status = *status;
                status.dwWaitHint = 15_000;
                if !CONTROLS.stop_requested() {
                    // A failed drive can also enter STOP_PENDING. It cannot
                    // authorize an operator acknowledgement.
                    return report(handle, &status);
                }
                let absent = require_absent(admitted, admission::RENDERER_MARKER);
                report(handle, &status)?;
                absent?;
                if let Some(stopping) = stopping {
                    let _ = stopping.send(());
                }
                return Ok(());
            }
            report(handle, status)
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

async fn release_on_stop(
    stopping: oneshot::Receiver<()>,
    release: oneshot::Sender<()>,
    marker: impl FnMut() -> io::Result<bool>,
) -> io::Result<()> {
    if stopping.await.is_err() {
        return Ok(());
    }
    wait_for_marker(STOP_LIMIT, marker).await?;
    release
        .send(())
        .map_err(|()| io::Error::other("renderer release receiver closed"))
}

async fn wait_for_marker(
    limit: Duration,
    mut marker: impl FnMut() -> io::Result<bool>,
) -> io::Result<()> {
    tokio::time::timeout(limit, async {
        while !marker()? {
            tokio::time::sleep(POLL).await;
        }
        Ok(())
    })
    .await?
}

#[cfg(test)]
mod tests {
    use super::*;

    #[tokio::test]
    async fn aborting_without_a_stop_never_reads_the_renderer_marker() {
        let (stopping, wait_stopping) = oneshot::channel();
        let (release, released) = oneshot::channel();
        drop(stopping);
        release_on_stop(wait_stopping, release, || panic!("no native STOP"))
            .await
            .unwrap();
        assert!(released.await.is_err());
    }

    #[tokio::test]
    async fn an_absent_marker_times_out() {
        let error = wait_for_marker(Duration::ZERO, || Ok(false))
            .await
            .unwrap_err();
        assert_eq!(error.kind(), io::ErrorKind::TimedOut);
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

//! Installed SCM candidate. This boundary never borrows the development host's pipe or data.

use std::{ffi::OsString, future::Future, io, path::PathBuf};

use tokio::sync::oneshot;
use windows_sys::Win32::System::Services::{
    SERVICE_START_PENDING, SERVICE_STATUS, SERVICE_WIN32_OWN_PROCESS,
};

use crate::scm_host::{Controls, Phase, check_starting, normalize_startup_stop, run_lifecycle};
use crate::scm_identity::ScmEvidence;
use crate::service_install_identity::{IMAGE_NAME, INSTALL_DIRECTORY};

#[cfg(not(test))]
#[path = "installed/native.rs"]
mod native;
#[cfg(not(test))]
pub(crate) use native::entry;
#[path = "installed/readiness.rs"]
mod readiness;
// Only the thermal composition accepts power events, so only it registers
// the power settings that arrive as them.
#[cfg(feature = "service-thermal-mutation-v1")]
#[cfg_attr(test, allow(dead_code))]
#[path = "installed/power_notifications.rs"]
mod power_notifications;

const NAME: &str = "OpenBlade";

// Test seam shared by the durable-owner, thermal-native and private-mutation fixtures.
#[cfg(all(test, feature = "service-mailbox-observation-v1"))]
pub(crate) type FileVerifier =
    std::sync::Arc<dyn Fn(&std::fs::File) -> io::Result<()> + Send + Sync>;
use crate::service_status::{PIPE_NAME, PROTOCOL_VERSION};

struct ProcessEvidence {
    has_arguments: bool,
    program_files: PathBuf,
    selected_image: Option<PathBuf>,
    image: PathBuf,
    pid: u32,
    local_system: bool,
    session: u32,
}

struct LaunchIdentity {
    native_origin: bool,
    image: PathBuf,
    pid: u32,
}

impl LaunchIdentity {
    fn verify(process: ProcessEvidence) -> io::Result<Self> {
        let native_origin = process.selected_image.is_some();
        let expected = process.selected_image.unwrap_or_else(|| {
            process
                .program_files
                .join(INSTALL_DIRECTORY)
                .join(IMAGE_NAME)
        });
        if process.has_arguments
            || !process.program_files.is_absolute()
            || process.image.as_os_str() != expected.as_os_str()
            || process.pid == 0
            || !process.local_system
            || process.session != 0
        {
            return Err(denied("unexpected installed service process identity"));
        }
        Ok(Self {
            native_origin,
            image: expected,
            pid: process.pid,
        })
    }

    fn image_command(&self) -> OsString {
        let mut command = OsString::from("\"");
        command.push(&self.image);
        command.push("\"");
        command
    }

    fn verify_registration(&self, scm: ScmEvidence) -> io::Result<()> {
        if !scm.matches_config
            || scm.service_type != SERVICE_WIN32_OWN_PROCESS
            || scm.state != SERVICE_START_PENDING
            || !(scm.process_id == self.pid || self.native_origin && scm.process_id == 0)
        {
            // SCM may omit the PID in START_PENDING. For installed admission,
            // missing PID evidence must fail until there is an independent binding.
            return Err(denied("unverified installed SCM registration or process"));
        }
        Ok(())
    }
}

fn dispatch_candidate(
    process: impl FnOnce() -> io::Result<ProcessEvidence>,
    dispatch: impl FnOnce() -> io::Result<()>,
) -> io::Result<()> {
    LaunchIdentity::verify(process()?)?;
    dispatch()
}

// The installed endpoint provider must establish listener ownership and a
// matching, successful current GetStatus response with a status snapshot. The
// candidate seam consumes this evidence before publishing RUNNING.
struct Readiness {
    pipe_name: &'static str,
    server_pid: u32,
    protocol_version: i32,
    correlated_status: bool,
}

impl Readiness {
    fn verify(self, launch: &LaunchIdentity) -> io::Result<()> {
        if self.pipe_name != PIPE_NAME
            || self.server_pid != launch.pid
            || self.protocol_version != PROTOCOL_VERSION
            || !self.correlated_status
        {
            return Err(denied("installed status IPC is not ready"));
        }
        Ok(())
    }
}

async fn run_candidate<A, F: Future<Output = io::Result<()>>>(
    controls: &Controls,
    stop: oneshot::Receiver<()>,
    process: impl FnOnce() -> io::Result<ProcessEvidence>,
    registration: impl FnOnce(&LaunchIdentity) -> io::Result<ScmEvidence>,
    admit: impl FnOnce(&LaunchIdentity) -> io::Result<A>,
    prepare: impl AsyncFnOnce(
        &LaunchIdentity,
        A,
        oneshot::Receiver<()>,
        oneshot::Sender<()>,
    ) -> io::Result<(Readiness, F)>,
    mut publish: impl FnMut(&SERVICE_STATUS) -> io::Result<()>,
) -> io::Result<()> {
    let stop_lost = std::cell::Cell::new(false);
    let stop = async {
        if stop.await.is_err() {
            stop_lost.set(true);
        }
    };
    tokio::pin!(stop);
    run_lifecycle(
        |phase| controls.publish(phase, &mut publish),
        async |report| {
            let outcome = async {
            check_starting(stop.as_mut()).await?;
            let launch = LaunchIdentity::verify(process()?)?;
            launch.verify_registration(registration(&launch)?)?;
            let admission = admit(&launch)?;
            check_starting(stop.as_mut()).await?;

            let (shutdown, retiring) = oneshot::channel();
            let mut shutdown = Some(shutdown);
            let (endpoint_failed, failure) = oneshot::channel();
            let failure = async {
                if failure.await.is_err() {
                    std::future::pending::<()>().await;
                }
            };
            let (readiness, drive) = {
                let preparing = prepare(&launch, admission, retiring, endpoint_failed);
                tokio::pin!(preparing);
                tokio::select! {
                    biased;
                    result = &mut preparing => result?,
                    () = &mut stop => {
                        // The provider must finish its bounded retirement. Dropping
                        // preparation here would discard its drain outcome.
                        let _ = shutdown.take().unwrap().send(());
                        preparing.await?
                    }
                }
            };
            let startup = if shutdown.is_none() {
                Err(crate::scm_host::startup_stopped())
            } else {
                check_starting(stop.as_mut())
                    .await
                    .and_then(|()| readiness.verify(&launch))
                    .and_then(|()| report(Phase::Running))
            };
            tokio::pin!(drive);

            let (result, stopping, drive_pending) = match startup {
                Ok(()) => {
                    let (result, pending) = tokio::select! {
                        biased;
                        () = &mut stop => (Ok(()), true),
                        () = failure => (Ok(()), true),
                        result = &mut drive => (
                            result.and(Err(io::Error::other("installed endpoint stopped unexpectedly"))),
                            false,
                        ),
                    };
                    (result, report(Phase::Stopping), pending)
                }
                Err(error) => (Err(error), Ok(()), true),
            };
            // Await provider retirement even when readiness or reporting failed.
            if let Some(shutdown) = shutdown {
                let _ = shutdown.send(());
            }
            let retired = if drive_pending { drive.await } else { Ok(()) };
            normalize_startup_stop(result).and(stopping).and(retired)
            }.await;
            if stop_lost.get() {
                normalize_startup_stop(outcome).and(Err(io::Error::new(io::ErrorKind::BrokenPipe, "SCM stop channel was lost")))
            } else { outcome }
        },
    )
    .await
}

fn denied(message: &'static str) -> io::Error {
    io::Error::new(io::ErrorKind::PermissionDenied, message)
}

#[cfg(test)]
#[path = "installed/tests.rs"]
mod tests;

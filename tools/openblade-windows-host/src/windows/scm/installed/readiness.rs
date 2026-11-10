//! Installed v79 GetStatus only. Ports must own their resources and acknowledge retirement.

use std::{future::Future, io, rc::Rc, time::Duration};

#[cfg(test)]
use crate::service_frame::write_frame;
#[cfg(test)]
use tokio::io::{AsyncRead, AsyncWrite};
use tokio::{sync::oneshot, time::timeout};

use super::{Readiness, denied};
use crate::{
    scm_host::startup_stopped,
    service_frame::{FRAME_TIMEOUT, read_payload},
    service_status::{
        GetStatus, PIPE_NAME, PROTOCOL_VERSION, Snapshot, StatusResponse, exchange,
        production_path, request_id as probe_id,
    },
};

#[path = "readiness_windows.rs"]
mod windows;
use windows::{Caller, ControlSession, Logon, Pending, Stream};

#[path = "status_source.rs"]
mod status_source;

const PROBE_TIMEOUT: Duration = Duration::from_secs(4);
const DRAIN_TIMEOUT: Duration = Duration::from_secs(2);

fn windows_control_session() -> ControlSession {
    Rc::new(|| Ok(crate::console_session::observe()))
}

struct Listener {
    pipe_name: &'static str,
    // Must come from an exclusive first-instance create, held continuously.
    first_instance: bool,
    // Protected managed DACL: Network deny, SYSTEM/admin full, Users read/write.
    managed_acl: bool,
    local_only: bool,
}

impl Listener {
    fn verify(self) -> io::Result<()> {
        if self.pipe_name != PIPE_NAME
            || !self.first_instance
            || !self.managed_acl
            || !self.local_only
        {
            return Err(denied("unverified installed status listener"));
        }
        Ok(())
    }
}

impl Caller {
    fn access(self) -> io::Result<i32> {
        match self.logon {
            Logon::System => Ok(0),
            Logon::Interactive if self.control_session == Some(self.session) => Ok(0),
            Logon::Interactive | Logon::RemoteInteractive => Ok(1),
            Logon::Other => Err(denied(
                "installed status requires SYSTEM or an interactive caller",
            )),
        }
    }
}

// Concrete implementations must bind all evidence to the live connection, not
// to a name lookup. Dropping pending I/O must leave no detached work; dropping
// an accepted stream must close it. Retire confirms listener and I/O closure.
trait Endpoint {
    fn listener(&self) -> io::Result<Listener>;
    fn accept(&self) -> Pending<'_, Box<dyn Stream>>;
    fn connect_probe(&self) -> Pending<'_, Box<dyn Stream>>;
    fn retire(&mut self) -> Pending<'_, ()>;
}

impl Endpoint for windows::NativeEndpoint {
    fn listener(&self) -> io::Result<Listener> {
        self.verify_managed_listener(&production_path())?;
        Ok(Listener {
            pipe_name: PIPE_NAME,
            first_instance: true,
            managed_acl: true,
            local_only: true,
        })
    }

    fn accept(&self) -> Pending<'_, Box<dyn Stream>> {
        windows::NativeEndpoint::accept(self)
    }

    fn connect_probe(&self) -> Pending<'_, Box<dyn Stream>> {
        windows::NativeEndpoint::connect_probe(self)
    }

    fn retire(&mut self) -> Pending<'_, ()> {
        windows::NativeEndpoint::retire(self)
    }
}

// Owns an already admitted runtime source. Every request takes a fresh status
// for the resolved caller, including the managed inactive-session projection.
trait StatusSource {
    fn snapshot(&mut self, caller: Caller) -> Pending<'_, Snapshot>;
    fn drain(&mut self) -> Pending<'_, ()>;
}

struct Ports {
    endpoint: Box<dyn Endpoint>,
    source: Box<dyn StatusSource>,
}

#[cfg(not(test))]
pub(super) async fn prepare_installed_endpoint(
    launch: &super::LaunchIdentity,
    admission: super::native::AdmittedRuntime,
    shutdown: oneshot::Receiver<()>,
    failed: oneshot::Sender<()>,
) -> io::Result<(Readiness, impl Future<Output = io::Result<()>> + use<>)> {
    admission.verify_launch(launch)?;
    let source = status_source::native_status_source();
    let endpoint = windows::NativeEndpoint::open_managed(
        &production_path(),
        launch.pid,
        windows_control_session(),
    )?;
    prepare(
        launch.pid,
        shutdown,
        failed,
        Ok(Ports {
            endpoint: Box::new(endpoint),
            source,
        }),
    )
    .await
}

async fn prepare(
    pid: u32,
    mut shutdown: oneshot::Receiver<()>,
    failed: oneshot::Sender<()>,
    ports: io::Result<Ports>,
) -> io::Result<(Readiness, impl Future<Output = io::Result<()>> + use<>)> {
    let mut ports = ports?;
    let result = tokio::select! {
        biased;
        result = timeout(PROBE_TIMEOUT, ports.probe(pid)) => result.map_err(io::Error::from).and_then(|r| r),
        signal = &mut shutdown => signal.map_err(|_| lost_shutdown()).and_then(|()| Err(startup_stopped())),
    };
    match result {
        Ok(()) => Ok((
            Readiness {
                pipe_name: PIPE_NAME,
                server_pid: pid,
                protocol_version: PROTOCOL_VERSION,
                correlated_status: true,
            },
            ports.drive(shutdown, failed),
        )),
        Err(error) => {
            // In particular, an unconfirmed drain overrides startup cancellation.
            ports.retire().await?;
            Err(error)
        }
    }
}

impl Ports {
    async fn probe(&mut self, pid: u32) -> io::Result<()> {
        self.endpoint.listener()?.verify()?;
        let request = GetStatus::new(probe_id()?)?;
        let endpoint = &*self.endpoint;
        let served = serve(&mut *self.source, endpoint.accept(), Some((pid, &request)));
        let probe = async {
            let mut client = endpoint.connect_probe().await?;
            exchange(&mut *client, &request, Some(0), |stream| {
                verify_peer(stream, pid)
            })
            .await
            .map(drop)
        };
        // Keep the accepted server handle open until the client validates the
        // response and rechecks the server PID on the original connection.
        let (_server, ()) =
            tokio::try_join!(async { served.await.map_err(io::Error::from) }, probe)?;
        endpoint.listener()?.verify()
    }

    async fn drive(
        mut self,
        mut shutdown: oneshot::Receiver<()>,
        failed: oneshot::Sender<()>,
    ) -> io::Result<()> {
        let (result, signal_consumed) = loop {
            let accepted = tokio::select! {
                biased;
                result = self.endpoint.accept() => result,
                signal = &mut shutdown => break (signal.map_err(|_| lost_shutdown()), true),
            };
            let stream = match accepted {
                Ok(stream) => stream,
                Err(error) => break (Err(error), false),
            };
            match shutdown.try_recv() {
                Ok(()) => break (Ok(()), true),
                Err(oneshot::error::TryRecvError::Closed) => break (Err(lost_shutdown()), true),
                Err(oneshot::error::TryRecvError::Empty) => {}
            }
            let exchange = serve(&mut *self.source, std::future::ready(Ok(stream)), None);
            match tokio::select! {
                biased;
                result = exchange => result,
                signal = &mut shutdown => break (signal.map_err(|_| lost_shutdown()), true),
            } {
                Ok(_) | Err(ExchangeError::Client(_)) => {}
                Err(ExchangeError::Runtime(error)) => break (Err(error), false),
            }
            // Ensure a continuously ready listener cannot starve STOP.
            tokio::task::yield_now().await;
        };
        if !signal_consumed {
            // Notify SCM before a bounded drain so the service is no longer
            // reported as running after the listener or source failed.
            let _ = failed.send(());
            let _ = shutdown.await;
        }
        result.and(self.retire().await)
    }

    async fn retire(&mut self) -> io::Result<()> {
        // Stop ingress before draining the source. Attempt both even when
        // listener ownership or retirement cannot be confirmed.
        let owned = self.endpoint.listener().and_then(Listener::verify);
        let listener = timeout(DRAIN_TIMEOUT, self.endpoint.retire()).await;
        let runtime = timeout(DRAIN_TIMEOUT, self.source.drain()).await;
        owned.and(listener?).and(runtime?)
    }
}

fn lost_shutdown() -> io::Error {
    io::Error::new(
        io::ErrorKind::BrokenPipe,
        "installed shutdown signal was lost",
    )
}

fn verify_peer(stream: &dyn Stream, pid: u32) -> io::Result<()> {
    if stream.server_pid()? != pid {
        return Err(denied("status probe reached a different server process"));
    }
    Ok(())
}

#[derive(Debug)]
enum ExchangeError {
    Client(io::Error),
    Runtime(io::Error),
}

impl From<ExchangeError> for io::Error {
    fn from(value: ExchangeError) -> Self {
        match value {
            ExchangeError::Client(error) | ExchangeError::Runtime(error) => error,
        }
    }
}

async fn serve(
    source: &mut dyn StatusSource,
    accept: impl Future<Output = io::Result<Box<dyn Stream>>>,
    probe: Option<(u32, &GetStatus)>,
) -> Result<Box<dyn Stream>, ExchangeError> {
    use ExchangeError::{Client, Runtime};
    let mut stream = accept.await.map_err(Runtime)?;
    let payload = timeout(FRAME_TIMEOUT, read_payload(&mut *stream))
        .await
        .map_err(|e| Client(e.into()))?
        .map_err(Client)?;
    // Windows impersonation uses the token from the last read on this handle.
    let mut caller = stream.caller_after_read().map_err(Client)?;
    let request = GetStatus::decode(&payload).map_err(Client)?;
    if let Some((pid, expected)) = probe
        && (caller.pid != Some(pid)
            || caller.logon != Logon::System
            || caller.session != 0
            || !request.matches(expected))
    {
        return Err(Client(denied(
            "unverified installed self-probe caller or request",
        )));
    }
    if caller.logon == Logon::Interactive {
        // Failure of the admitted session authority is a runtime failure,
        // not an inactive session or a bad client.
        caller.control_session = stream.control_session().map_err(Runtime)?;
    }
    let access = caller.access().map_err(Client)?;
    let status = timeout(FRAME_TIMEOUT, source.snapshot(caller))
        .await
        .map_err(|e| Runtime(e.into()))?
        .map_err(Runtime)?;
    status.verify(access).map_err(Runtime)?;
    let response = StatusResponse::success(&request, status);
    // Encoding errors (including oversized source data) are runtime failures,
    // while an individual client disconnect or write timeout is isolated.
    let payload = crate::service_frame::encode(&response).map_err(Runtime)?;
    crate::service_frame::write_payload(&mut *stream, &payload)
        .await
        .map_err(Client)?;
    Ok(stream)
}

#[cfg(test)]
#[path = "readiness_tests.rs"]
mod tests;

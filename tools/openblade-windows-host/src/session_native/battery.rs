//! Selected installed Battery.v1 endpoint. No fixture scenarios enter this composition.
use super::feature_lifetime::stopped;
use openblade_platform_windows::battery_report::{self, Failure};
use openblade_windows_host::json_object;
use openblade_windows_host::pipe::Endpoint;
use std::{
    future::Future,
    io,
    sync::{
        Arc,
        atomic::{AtomicBool, Ordering},
    },
    time::Duration,
};
use tokio::{
    io::{AsyncRead, AsyncReadExt, AsyncWrite, AsyncWriteExt},
    sync::watch,
    time::{sleep, timeout},
};

#[path = "../session_battery/report.rs"]
mod report;
#[path = "../session_battery/timestamp.rs"]
mod timestamp;
#[path = "../session_battery/wire.rs"]
mod wire;
#[path = "../session_battery/xml.rs"]
mod xml;
use super::diagnostics;
use openblade_platform_windows::agent_diagnostics::{Event, record};
use wire::{MAX_FRAME, Request, Response, read_frame_body};

const FRAME_TIMEOUT: Duration = Duration::from_secs(2);
const DISPATCH_TIMEOUT: Duration = Duration::from_secs(20);
const ORIGIN_POLL: Duration = Duration::from_millis(250);

trait Supplier {
    fn acquire(
        &mut self,
        canceled: Arc<AtomicBool>,
    ) -> impl Future<Output = Result<Vec<u8>, Failure>>;
    fn reconcile_retirement(&mut self) -> Result<(), Failure>;
}
struct WindowsSupplier;
impl Supplier for WindowsSupplier {
    async fn acquire(&mut self, canceled: Arc<AtomicBool>) -> Result<Vec<u8>, Failure> {
        tokio::task::spawn_blocking(move || battery_report::acquire(&canceled))
            .await
            .map_err(|_| Failure::Wait)?
    }
    fn reconcile_retirement(&mut self) -> Result<(), Failure> {
        battery_report::reconcile_retirement()
    }
}

struct Cancellation(Arc<AtomicBool>);
impl Drop for Cancellation {
    fn drop(&mut self) {
        self.0.store(true, Ordering::Release);
    }
}

/// The first instance remains held while connections and owned process work drain.
/// One listener, one serialized request, no unbounded queue or spawned connection tasks.
pub(super) async fn run(
    origin: Arc<crate::loaded_origin::Origin>,
    mut stop: watch::Receiver<bool>,
) -> io::Result<()> {
    super::verify_origin(&origin)?;
    let endpoint = Endpoint::current_user_battery().map_err(diagnostics::listener)?;
    let _claim = endpoint.claim().map_err(diagnostics::claim)?;
    let mut supplier = WindowsSupplier;
    run_connections(
        &mut stop,
        &mut supplier,
        || async {
            let pipe = endpoint.next_listener().await?;
            pipe.connect().await?;
            Ok(pipe)
        },
        |pipe| endpoint.identity.authorize(pipe),
        || origin.verify(),
        record,
    )
    .await
}

/// Only errors belonging to an accepted connection are contained here. Listener
/// acquisition, installed-origin verification and supplier custody remain fatal.
async fn run_connections<S, F>(
    stop: &mut watch::Receiver<bool>,
    supplier: &mut impl Supplier,
    mut accept: impl FnMut() -> F,
    authorize: impl Fn(&S) -> io::Result<()>,
    verify: impl Fn() -> io::Result<()>,
    emit: impl Fn(Event),
) -> io::Result<()>
where
    S: AsyncRead + AsyncWrite + Unpin,
    F: Future<Output = io::Result<S>>,
{
    loop {
        if *stop.borrow() {
            return Ok(());
        }
        verify().map_err(diagnostics::origin)?;
        let mut pipe = tokio::select! {
            biased;
            _ = stopped(stop) => return Ok(()),
            result = accept() => result.map_err(diagnostics::listener)?,
        };
        match serve(&mut pipe, stop, supplier, &authorize, &verify, &emit).await {
            Ok(()) => (),
            Err(ServeError::Connection(error)) => drop(error),
            Err(ServeError::Fatal(error)) => return Err(error),
        }
    }
}

#[derive(Debug)]
enum ServeError {
    Connection(io::Error),
    Fatal(io::Error),
}

impl From<io::Error> for ServeError {
    fn from(error: io::Error) -> Self {
        Self::Connection(error)
    }
}

impl From<tokio::time::error::Elapsed> for ServeError {
    fn from(error: tokio::time::error::Elapsed) -> Self {
        Self::Connection(error.into())
    }
}

#[cfg(test)]
impl ServeError {
    fn into_io(self) -> io::Error {
        match self {
            Self::Connection(error) | Self::Fatal(error) => error,
        }
    }

    fn kind(&self) -> io::ErrorKind {
        match self {
            Self::Connection(error) | Self::Fatal(error) => error.kind(),
        }
    }
}

async fn origin_invalid(verify: &impl Fn() -> io::Result<()>) -> io::Error {
    loop {
        if let Err(error) = verify() {
            return error;
        }
        sleep(ORIGIN_POLL).await;
    }
}

/// Same typed admission/dispatch/frame path is exercised with injected suppliers.
async fn serve<S: AsyncRead + AsyncWrite + Unpin>(
    pipe: &mut S,
    stop: &mut watch::Receiver<bool>,
    supplier: &mut impl Supplier,
    authorize: impl Fn(&S) -> io::Result<()>,
    verify: impl Fn() -> io::Result<()>,
    emit: impl Fn(Event),
) -> Result<(), ServeError> {
    if *stop.borrow() {
        return Ok(());
    }
    let body = tokio::select! {
        biased;
        _ = stopped(stop) => return Ok(()),
        result = timeout(FRAME_TIMEOUT, read_frame_body(pipe, MAX_FRAME)) => result??,
    };
    // Read first: impersonation must use the current message token, not a cached peer.
    verify().map_err(|error| ServeError::Fatal(diagnostics::origin(error)))?;
    authorize(pipe)?;
    let request: Request = json_object::public_tokens(&body)
        .and_then(|()| serde_json::from_slice(&body))
        .map_err(|error| io::Error::new(io::ErrorKind::InvalidData, error))?;
    verify().map_err(|error| ServeError::Fatal(diagnostics::origin(error)))?;
    if *stop.borrow() {
        return Ok(());
    }
    let canceled = Cancellation(Arc::new(AtomicBool::new(false)));
    enum End {
        Complete(Result<Vec<u8>, Failure>),
        Retire,
        Stale(io::Error),
    }
    let end = {
        let work = supplier.acquire(canceled.0.clone());
        tokio::pin!(work);
        let mut extra = [0u8];
        let end = tokio::select! {
            biased;
            _ = stopped(stop) => End::Retire,
            error = origin_invalid(&verify) => End::Stale(error),
            _ = sleep(DISPATCH_TIMEOUT) => End::Retire,
            _ = pipe.read(&mut extra) => End::Retire,
            result = &mut work => End::Complete(result),
        };
        match end {
            End::Complete(result) => End::Complete(result),
            reason => {
                canceled.0.store(true, Ordering::Release);
                // Never abandon spawn_blocking or overlap the next acquisition.
                let result = work.await;
                if result == Err(Failure::StopUnconfirmed) {
                    End::Complete(result)
                } else {
                    reason
                }
            }
        }
    };
    let result = match end {
        End::Retire => return Ok(()),
        End::Stale(error) => return Err(ServeError::Fatal(diagnostics::origin(error))),
        End::Complete(Err(Failure::StopUnconfirmed)) => {
            emit(Event::BatteryRetirement(Failure::StopUnconfirmed));
            // Preserve the endpoint lease while the platform retains the original
            // job/process/directory. No new report is admitted during uncertainty.
            // Each probe is nonblocking; Stop never reports a fictitious clean drain.
            loop {
                match supplier.reconcile_retirement() {
                    Err(Failure::StopUnconfirmed) => sleep(ORIGIN_POLL).await,
                    result => {
                        result.map_err(|failure| {
                            let event = Event::BatteryRetirement(failure);
                            emit(event);
                            ServeError::Fatal(diagnostics::attempted(
                                io::Error::other("battery report retirement cleanup failed"),
                                event,
                            ))
                        })?;
                        break;
                    }
                }
            }
            return Err(ServeError::Fatal(diagnostics::attempted(
                io::Error::other("battery report process retirement was uncertain"),
                Event::BatteryRetirement(Failure::StopUnconfirmed),
            )));
        }
        End::Complete(result) => result,
    };
    if *stop.borrow() {
        return Ok(());
    }
    let result = match result {
        Ok(xml) => Ok(report::parse(&xml)),
        Err(Failure::Cancelled) => return Ok(()),
        Err(Failure::Start | Failure::Wait) => Err(()),
        Err(_) => Ok(None),
    };
    let frame = Response::from_result(&request.request_id, result).frame()?;
    // Parsing/encoding are bounded but may take time; bind the emitted frame to
    // the fresh origin and current message identity after those steps too.
    verify().map_err(|error| ServeError::Fatal(diagnostics::origin(error)))?;
    authorize(pipe)?;
    tokio::select! {
        biased;
        _ = stopped(stop) => Ok(()),
        result = timeout(FRAME_TIMEOUT, pipe.write_all(&frame)) => { result??; Ok(()) },
    }
}

#[cfg(test)]
mod tests;

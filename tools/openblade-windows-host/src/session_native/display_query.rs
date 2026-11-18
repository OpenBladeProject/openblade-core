//! Selected installed Display.v2 endpoint. The real supplier performs
//! coherent CCD/refresh/HDR/WCS and targeted, owned WMI brightness reads.
//! A separately selected build adds manual `setBrightness` only; every other
//! mutation stays outside this endpoint.
use super::diagnostics;
use openblade_windows_host::pipe::Endpoint;
use serde::Deserialize;
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
#[cfg(feature = "agent-display-brightness-write-v1")]
mod brightness_write;
mod observation;
#[cfg(feature = "agent-display-brightness-write-v1")]
mod recovery;
#[cfg(feature = "agent-display-brightness-write-v1")]
use brightness_write::{SetBrightness, WriteOutcome};
use observation::Snapshot;

const MAX_FRAME: i32 = 65_536;
const FRAME_TIMEOUT: Duration = Duration::from_secs(2);
const DISPATCH_TIMEOUT: Duration = Duration::from_secs(5);
/// Managed `SessionDisplayRequestDispatcher.MutationDispatchTimeout`.
#[cfg(feature = "agent-display-brightness-write-v1")]
const MUTATION_DISPATCH_TIMEOUT: Duration = Duration::from_secs(30);
const ORIGIN_POLL: Duration = Duration::from_millis(250);

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Envelope {
    version: i32,
    request_id: String,
    operation: Name,
    #[cfg(feature = "agent-display-brightness-write-v1")]
    #[serde(default, deserialize_with = "present")]
    expected_power_source: Option<i32>,
    #[cfg(feature = "agent-display-brightness-write-v1")]
    #[serde(default, deserialize_with = "present")]
    percent: Option<i32>,
}
/// Absent members stay `None`; an explicit JSON null is malformed.
#[cfg(feature = "agent-display-brightness-write-v1")]
fn present<'de, D: serde::Deserializer<'de>>(deserializer: D) -> Result<Option<i32>, D::Error> {
    i32::deserialize(deserializer).map(Some)
}
// The closed operation set rejects every unselected mutation before any
// supplier is invoked.
#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
enum Name {
    Query,
    #[cfg(feature = "agent-display-brightness-write-v1")]
    SetBrightness,
}
#[derive(Clone, Copy)]
enum Operation {
    Query,
    #[cfg(feature = "agent-display-brightness-write-v1")]
    SetBrightness(SetBrightness),
}
struct Request {
    request_id: String,
    operation: Operation,
}
impl Request {
    fn parse(body: &[u8]) -> io::Result<Self> {
        let envelope: Envelope = serde_json::from_slice(body).map_err(invalid)?;
        let id = &envelope.request_id;
        if envelope.version != 2
            || id.len() != 36
            || !id.bytes().enumerate().all(|(i, b)| {
                if matches!(i, 8 | 13 | 18 | 23) {
                    b == b'-'
                } else {
                    b.is_ascii_hexdigit()
                }
            })
            || id == "00000000-0000-0000-0000-000000000000"
        {
            return Err(invalid("invalid Display query envelope"));
        }
        let operation = match envelope.operation {
            #[cfg(not(feature = "agent-display-brightness-write-v1"))]
            Name::Query => Operation::Query,
            #[cfg(feature = "agent-display-brightness-write-v1")]
            Name::Query => match (envelope.expected_power_source, envelope.percent) {
                (None, None) => Operation::Query,
                _ => return Err(invalid("invalid Display query members")),
            },
            // Managed IsConfirmed (PluggedIn, Battery, UsbC) and byte percent <= 100.
            #[cfg(feature = "agent-display-brightness-write-v1")]
            Name::SetBrightness => match (envelope.expected_power_source, envelope.percent) {
                (Some(source @ 0..=2), Some(percent @ 0..=100)) => {
                    Operation::SetBrightness(SetBrightness {
                        expected_power_source: source as u8,
                        percent: percent as u8,
                    })
                }
                _ => return Err(invalid("invalid Display setBrightness members")),
            },
        };
        Ok(Self {
            request_id: envelope.request_id,
            operation,
        })
    }
}
fn invalid(error: impl Into<Box<dyn std::error::Error + Send + Sync>>) -> io::Error {
    io::Error::new(io::ErrorKind::InvalidData, error)
}

trait Supplier {
    fn acquire(&mut self, canceled: Arc<AtomicBool>) -> impl Future<Output = io::Result<Snapshot>>;
    /// Resolves only after an admitted operation recorded its certainty, even
    /// when cancellation was requested first.
    #[cfg(feature = "agent-display-brightness-write-v1")]
    fn set_brightness(
        &mut self,
        request: SetBrightness,
        canceled: Arc<AtomicBool>,
    ) -> impl Future<Output = io::Result<WriteOutcome>>;
    async fn drain(&mut self) -> io::Result<()> {
        Ok(())
    }
}
struct WindowsSupplier {
    brightness: observation::SharedBrightness,
    #[cfg(feature = "agent-display-brightness-write-v1")]
    writer: brightness_write::Writer,
}
impl Supplier for WindowsSupplier {
    async fn acquire(&mut self, canceled: Arc<AtomicBool>) -> io::Result<Snapshot> {
        if canceled.load(Ordering::Acquire) {
            return Err(io::ErrorKind::Interrupted.into());
        }
        let brightness = self.brightness.clone();
        #[cfg(feature = "agent-display-brightness-write-v1")]
        let writer = self.writer.clone();
        tokio::task::spawn_blocking(move || {
            let snapshot = observation::query(&canceled, brightness)?;
            // Managed ResumeAfterSuccessfulExplicitRead after every completed query.
            #[cfg(feature = "agent-display-brightness-write-v1")]
            writer.explicit_read(&snapshot)?;
            Ok(snapshot)
        })
        .await
        .map_err(|_| io::Error::other("display read worker failed"))?
    }
    #[cfg(feature = "agent-display-brightness-write-v1")]
    async fn set_brightness(
        &mut self,
        request: SetBrightness,
        canceled: Arc<AtomicBool>,
    ) -> io::Result<WriteOutcome> {
        let brightness = self.brightness.clone();
        let writer = self.writer.clone();
        tokio::task::spawn_blocking(move || writer.set_brightness(&brightness, request, &canceled))
            .await
            .map_err(|_| io::Error::other("display write worker failed"))?
    }
    async fn drain(&mut self) -> io::Result<()> {
        observation::drain_brightness(self.brightness.clone()).await
    }
}
struct Cancellation(Arc<AtomicBool>);
impl Drop for Cancellation {
    fn drop(&mut self) {
        self.0.store(true, Ordering::Release);
    }
}

pub(super) async fn run(
    origin: Arc<crate::loaded_origin::Origin>,
    mut stop: watch::Receiver<bool>,
    #[cfg(feature = "agent-display-brightness-write-v1")]
    power: super::power_admission::PowerAdmission,
) -> io::Result<()> {
    super::verify_origin(&origin)?;
    let endpoint = Endpoint::current_user_display().map_err(diagnostics::listener)?;
    let _claim = endpoint.claim().map_err(diagnostics::listener)?;
    let mut supplier = WindowsSupplier {
        brightness: Default::default(),
        #[cfg(feature = "agent-display-brightness-write-v1")]
        writer: {
            let origin = origin.clone();
            tokio::task::spawn_blocking(move || brightness_write::Writer::load(origin, power))
                .await
                .map_err(|_| io::Error::other("display recovery load failed"))?
        },
    };
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
    )
    .await
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
async fn stopped(stop: &mut watch::Receiver<bool>) {
    loop {
        if *stop.borrow_and_update() {
            return;
        }
        if stop.changed().await.is_err() {
            return;
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
async fn run_connections<S, F>(
    stop: &mut watch::Receiver<bool>,
    supplier: &mut impl Supplier,
    accept: impl FnMut() -> F,
    authorize: impl Fn(&S) -> io::Result<()>,
    verify: impl Fn() -> io::Result<()>,
) -> io::Result<()>
where
    S: AsyncRead + AsyncWrite + Unpin,
    F: Future<Output = io::Result<S>>,
{
    let result = serve_connections(stop, supplier, accept, authorize, verify).await;
    // Every loop exit (Stop, Origin, listener, supplier failure) closes and joins
    // persistent WMI ownership while the caller still holds its claim/Origin.
    // Preserve the causal operation error if retirement also reports failure.
    let drained = supplier.drain().await;
    result.and(drained)
}

async fn serve_connections<S, F>(
    stop: &mut watch::Receiver<bool>,
    supplier: &mut impl Supplier,
    mut accept: impl FnMut() -> F,
    authorize: impl Fn(&S) -> io::Result<()>,
    verify: impl Fn() -> io::Result<()>,
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
        match serve(&mut pipe, stop, supplier, &authorize, &verify).await {
            Ok(()) | Err(ServeError::Connection(_)) => (),
            Err(ServeError::Fatal(error)) => return Err(error),
        }
    }
}
async fn read_frame(pipe: &mut (impl AsyncRead + Unpin)) -> io::Result<Vec<u8>> {
    let length = pipe.read_i32_le().await?;
    if !(1..=MAX_FRAME).contains(&length) {
        return Err(invalid("invalid Display frame length"));
    }
    let mut body = vec![0; length as usize];
    pipe.read_exact(&mut body).await?;
    Ok(body)
}
/// One managed `SessionDisplayResponse`. `error` is null exactly when the
/// status is Succeeded (0).
fn frame(id: &str, status: u8, error: Option<&str>, snapshot: Snapshot) -> io::Result<Vec<u8>> {
    debug_assert_eq!(status == 0, error.is_none());
    let snapshot = if snapshot.valid() {
        snapshot
    } else {
        Snapshot::unavailable("Windows returned display data outside the query limits.")
    };
    let encode = |state: Snapshot| {
        serde_json::to_vec(&serde_json::json!({
            "version":2, "requestId":id, "status":status, "error":error, "state":state,
        }))
        .map_err(invalid)
    };
    let mut body = encode(snapshot)?;
    if body.len() > MAX_FRAME as usize {
        body = encode(Snapshot::unavailable(
            "Windows returned display data outside the frame limit.",
        ))?;
    }
    let mut frame = Vec::with_capacity(4 + body.len());
    frame.extend((body.len() as i32).to_le_bytes());
    frame.extend(body);
    Ok(frame)
}

enum Reply {
    Query(Snapshot),
    #[cfg(feature = "agent-display-brightness-write-v1")]
    Write(WriteOutcome),
}

/// Every started blocking operation is awaited, including on deadline,
/// disconnect, Stop and origin loss. Windows CCD/WCS/WMI have no safe per-call
/// cancellation; retirement is conditional on that call returning, with no
/// false bounded-stop promise. Endpoint and original Origin custody stay held
/// throughout this wait. A canceled write still records its certainty.
async fn serve<S: AsyncRead + AsyncWrite + Unpin>(
    pipe: &mut S,
    stop: &mut watch::Receiver<bool>,
    supplier: &mut impl Supplier,
    authorize: impl Fn(&S) -> io::Result<()>,
    verify: impl Fn() -> io::Result<()>,
) -> Result<(), ServeError> {
    if *stop.borrow() {
        return Ok(());
    }
    let body = tokio::select! {
        biased;
        _ = stopped(stop) => return Ok(()),
        result = timeout(FRAME_TIMEOUT, read_frame(pipe)) => result??,
    };
    // Impersonation uses the just-read message, with fresh SID/session/LUID.
    verify().map_err(|e| ServeError::Fatal(diagnostics::origin(e)))?;
    authorize(pipe)?;
    let request = Request::parse(&body)?;
    verify().map_err(|e| ServeError::Fatal(diagnostics::origin(e)))?;
    if *stop.borrow() {
        return Ok(());
    }
    let canceled = Cancellation(Arc::new(AtomicBool::new(false)));
    let operation = request.operation;
    let deadline = match operation {
        Operation::Query => DISPATCH_TIMEOUT,
        #[cfg(feature = "agent-display-brightness-write-v1")]
        Operation::SetBrightness(_) => MUTATION_DISPATCH_TIMEOUT,
    };
    enum End {
        Complete(io::Result<Reply>),
        Retire,
        Stale(io::Error),
    }
    let end = {
        let flag = canceled.0.clone();
        let work = async {
            match operation {
                Operation::Query => supplier.acquire(flag).await.map(Reply::Query),
                #[cfg(feature = "agent-display-brightness-write-v1")]
                Operation::SetBrightness(request) => {
                    supplier.set_brightness(request, flag).await.map(Reply::Write)
                }
            }
        };
        tokio::pin!(work);
        let mut extra = [0u8];
        let end = tokio::select! {
            biased;
            _ = stopped(stop) => End::Retire,
            error = origin_invalid(&verify) => End::Stale(error),
            _ = sleep(deadline) => End::Retire,
            _ = pipe.read(&mut extra) => End::Retire,
            result = &mut work => End::Complete(result),
        };
        match end {
            End::Complete(result) => End::Complete(result),
            reason => {
                canceled.0.store(true, Ordering::Release);
                // Retain and join the actual work future; cancellation is a
                // request, never evidence that spawn_blocking has completed.
                match work.await {
                    Err(error) if error.kind() != io::ErrorKind::Interrupted => {
                        End::Complete(Err(error))
                    }
                    _ => reason,
                }
            }
        }
    };
    let reply = match end {
        End::Retire => return Ok(()),
        End::Stale(error) => return Err(ServeError::Fatal(diagnostics::origin(error))),
        End::Complete(Err(error)) if error.kind() == io::ErrorKind::Interrupted => return Ok(()),
        End::Complete(result) => result.map_err(ServeError::Fatal)?,
    };
    if *stop.borrow() {
        return Ok(());
    }
    let frame = match reply {
        Reply::Query(snapshot) => frame(&request.request_id, 0, None, snapshot)?,
        #[cfg(feature = "agent-display-brightness-write-v1")]
        Reply::Write(outcome) => frame(
            &request.request_id,
            outcome.status as u8,
            outcome.error,
            outcome.state,
        )?,
    };
    verify().map_err(|e| ServeError::Fatal(diagnostics::origin(e)))?;
    authorize(pipe)?;
    tokio::select! {
        biased;
        _ = stopped(stop) => Ok(()),
        result = timeout(FRAME_TIMEOUT, pipe.write_all(&frame)) => { result??; Ok(()) },
    }
}
#[cfg(test)]
mod tests;

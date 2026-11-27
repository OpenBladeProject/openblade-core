//! Selected installed Session GPU v2 endpoint (`agent-gpu-v1`): the managed
//! `SessionGpuPipeServer` over `SessionGpuRequestDispatcher` and
//! `WindowsGpuAppManager`, on the managed Gpu.v2 pipe name and protected
//! one-SID ACL. Query, close and force close use the production sampling and
//! retained-handle close backend in `openblade-platform-windows`.
//!
//! Deliberate differences from managed (also listed in `rust/README.md`):
//! - Requests are read in the encoding the managed codec writes: camelCase
//!   member names, JSON numbers, no duplicate members, `operation` first.
//!   Managed also reads other casing, numbers given as strings and duplicate
//!   members (last wins); native refuses those.
//! - A client disconnect or an extra byte during dispatch cancels the
//!   operation like Stop or the 20 s deadline: unattempted targets stay
//!   unattempted and no reply is written. Managed ignores the client until
//!   the dispatch ends.
//! - A response whose app list exceeds the 64 KiB frame keeps the largest
//!   ordered prefix that fits (a query then reports partial success).
//!   Managed fails the write and closes the connection. A query trimmed this
//!   way says that only part of the list is shown.
//! - A force close that terminated the process but cannot observe its exit
//!   within 5 s is reported as unconfirmed, not as success. See the platform
//!   backend for the remaining process-level differences.
use super::diagnostics;
use super::feature_lifetime::stopped;
use openblade_platform_windows::gpu_apps::{
    self, GpuAppInventory, Kind, MAXIMUM_APPS, QueryUnavailable, Report, Target,
};
use openblade_windows_host::{
    json_object::{self, Object},
    pipe::Endpoint,
};
use serde::{Deserialize, Serialize};
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

const VERSION: i32 = 2;
const MAX_FRAME: i32 = 64 * 1024;
/// Managed `SessionGpuPipeServer.FrameTimeout` (read and write).
const FRAME_TIMEOUT: Duration = Duration::from_secs(2);
/// Managed `SessionGpuPipeServer.DispatchTimeout`.
const DISPATCH_TIMEOUT: Duration = Duration::from_secs(20);
const ORIGIN_POLL: Duration = Duration::from_millis(250);
const SUCCEEDED: u8 = 0;
const PARTIAL: u8 = 1;
const FAILED: u8 = 2;
/// Managed `WindowsGpuAppManager.ScanAsync` for an unavailable sample.
const COUNTERS_UNAVAILABLE: &str = "Windows GPU activity counters are unavailable.";
/// Managed `SessionGpuRequestDispatcher` for a query above the app cap.
const TRUNCATED: &str = "Only the first 128 GPU applications are shown.";
/// Managed `SessionGpuPipeServer` reply for an unexpected dispatch failure.
const DISPATCH_FAILURE: &str = "The Session Agent could not complete the GPU app operation.";
/// Native only: a query whose app list was cut to fit the 64 KiB frame.
const TRIMMED: &str = "Only part of the GPU application list fits in the reply.";

#[derive(Debug, PartialEq)]
enum Request {
    Query {
        request_id: String,
    },
    Close {
        request_id: String,
        kind: Kind,
        targets: Vec<WireTarget>,
    },
}

#[derive(Debug, PartialEq, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct WireTarget {
    process_id: i32,
    start_time_utc_ticks: i64,
    name: String,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Envelope {
    #[serde(deserialize_with = "operation_name")]
    operation: Name,
    version: i32,
    request_id: String,
    #[serde(default, deserialize_with = "present")]
    force: Option<bool>,
    #[serde(default, deserialize_with = "present")]
    targets: Option<Vec<Object<WireTarget>>>,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
enum Name {
    Query,
    Close,
}

/// The managed discriminator is a JSON string; serde's externally tagged
/// form (`{"query":null}`) would otherwise also select a unit variant.
fn operation_name<'de, D: serde::Deserializer<'de>>(deserializer: D) -> Result<Name, D::Error> {
    let name = String::deserialize(deserializer)?;
    Name::deserialize(serde::de::value::StrDeserializer::<D::Error>::new(&name))
}

/// Absent members stay `None`; an explicit JSON null is malformed (managed
/// requires `force` and a non-null `targets` on close).
fn present<'de, D: serde::Deserializer<'de>, T: Deserialize<'de>>(
    deserializer: D,
) -> Result<Option<T>, D::Error> {
    T::deserialize(deserializer).map(Some)
}

impl Request {
    /// Managed `SessionGpuIpcCodec.ReadRequestAsync`: version 2, a non-empty
    /// GUID, and for close 1..=128 distinct valid targets, exactly one when
    /// forced. A version or force-count mismatch is refused here, before the
    /// dispatcher's own checks could answer it, exactly as managed.
    fn parse(body: &[u8]) -> io::Result<Self> {
        // System.Text.Json polymorphism requires the discriminator first.
        if !operation_first(body) {
            return Err(invalid("Session GPU request operation must be first"));
        }
        let envelope: Envelope = json_object::from_slice(body).map_err(invalid)?;
        if envelope.version != VERSION || !valid_request_id(&envelope.request_id) {
            return Err(invalid("invalid Session GPU envelope"));
        }
        // Managed writes the GUID in lower case whatever the client sent.
        let request_id = envelope.request_id.to_ascii_lowercase();
        match (envelope.operation, envelope.force, envelope.targets) {
            (Name::Query, None, None) => Ok(Self::Query { request_id }),
            (Name::Close, Some(force), Some(targets)) => {
                let targets: Vec<_> = targets.into_iter().map(|Object(target)| target).collect();
                let mut seen = std::collections::BTreeSet::new();
                if targets.is_empty()
                    || targets.len() > MAXIMUM_APPS
                    || (force && targets.len() != 1)
                    || !targets.iter().all(|target| {
                        target.process_id > 0
                            && target.start_time_utc_ticks > 0
                            && valid_name(&target.name)
                            && seen.insert(target.process_id)
                    })
                {
                    return Err(invalid("invalid Session GPU close targets"));
                }
                let kind = if force { Kind::ForceClose } else { Kind::Close };
                Ok(Self::Close {
                    request_id,
                    kind,
                    targets,
                })
            }
            _ => Err(invalid("invalid Session GPU request members")),
        }
    }

    fn request_id(&self) -> &str {
        match self {
            Self::Query { request_id } | Self::Close { request_id, .. } => request_id,
        }
    }
}

/// The first member name is literally `"operation"` (an escaped spelling is
/// refused; stricter than managed only for an encoding it never writes).
fn operation_first(body: &[u8]) -> bool {
    let rest = body.trim_ascii_start();
    rest.strip_prefix(b"{")
        .is_some_and(|rest| rest.trim_ascii_start().starts_with(b"\"operation\""))
}

/// The System.Text.Json `Guid` reader's "D" format, not the empty GUID.
fn valid_request_id(id: &str) -> bool {
    id.len() == 36
        && id.bytes().enumerate().all(|(position, byte)| {
            if matches!(position, 8 | 13 | 18 | 23) {
                byte == b'-'
            } else {
                byte.is_ascii_hexdigit()
            }
        })
        && id.bytes().any(|byte| !matches!(byte, b'0' | b'-'))
}

/// Managed `ValidName`: not blank, trimmed, at most 260 UTF-16 units and no
/// control character. .NET `IsWhiteSpace` and Rust's trim differ only for
/// U+0085, which the control-character rule already rejects.
fn valid_name(name: &str) -> bool {
    !name.is_empty()
        && name.trim() == name
        && name.encode_utf16().count() <= 260
        && !name.chars().any(char::is_control)
}

fn invalid(error: impl Into<Box<dyn std::error::Error + Send + Sync>>) -> io::Error {
    io::Error::new(io::ErrorKind::InvalidData, error)
}

/// Managed `SessionGpuResponse`, members in the managed record order.
#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
struct Response {
    version: i32,
    request_id: String,
    status: u8,
    error: Option<&'static str>,
    apps: Vec<App>,
    results: Vec<ActionResult>,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
struct App {
    process_id: i32,
    start_time_utc_ticks: i64,
    name: String,
    utilization_percent: f64,
    can_close: bool,
    block_reason: Option<&'static str>,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
struct ActionResult {
    process_id: i32,
    start_time_utc_ticks: i64,
    succeeded: bool,
    error: Option<&'static str>,
}

impl Response {
    fn failed(request_id: &str, error: &'static str) -> Self {
        Self {
            version: VERSION,
            request_id: request_id.into(),
            status: FAILED,
            error: Some(error),
            apps: Vec::new(),
            results: Vec::new(),
        }
    }

    /// Managed `Response(...)`: the apps and results lists keep their first
    /// 128 entries.
    fn with_apps(
        request_id: &str,
        status: u8,
        error: Option<&'static str>,
        inventory: GpuAppInventory,
    ) -> Self {
        let apps = inventory
            .apps
            .into_iter()
            .take(MAXIMUM_APPS)
            .map(|app| App {
                can_close: app.can_close(),
                block_reason: app.block_reason.map(|reason| reason.message()),
                process_id: app.process_id,
                start_time_utc_ticks: app.start_time_utc_ticks,
                name: app.name,
                utilization_percent: app.utilization_percent,
            })
            .collect();
        Self {
            version: VERSION,
            request_id: request_id.into(),
            status,
            error,
            apps,
            results: Vec::new(),
        }
    }

    /// Managed `ValidateResponse` before a write. An invalid projected app is
    /// a failed write in managed too: the connection closes without a reply.
    fn validate(&self) -> io::Result<()> {
        let valid = (self.status == SUCCEEDED) == self.error.is_none()
            && self.status <= FAILED
            && self.apps.len() <= MAXIMUM_APPS
            && self.results.len() <= MAXIMUM_APPS
            && self.apps.iter().all(|app| {
                app.process_id > 0
                    && app.start_time_utc_ticks > 0
                    && valid_name(&app.name)
                    && app.utilization_percent.is_finite()
                    && app.utilization_percent >= 0.0
                    && app.can_close == app.block_reason.is_none()
            })
            && self.results.iter().all(|result| {
                result.process_id > 0
                    && result.start_time_utc_ticks > 0
                    && result.succeeded == result.error.is_none()
            });
        if valid {
            Ok(())
        } else {
            Err(invalid("invalid Session GPU response"))
        }
    }

    fn frame(mut self, query: bool) -> io::Result<Vec<u8>> {
        self.validate()?;
        let limit = MAX_FRAME as usize;
        let mut body = serde_json::to_vec(&self).map_err(invalid)?;
        if body.len() > limit {
            if query {
                self.status = PARTIAL;
                self.error = Some(TRIMMED);
            }
            // Keep the largest ordered prefix of apps that fits the byte cap.
            let apps = std::mem::take(&mut self.apps);
            let mut length = serde_json::to_vec(&self).map_err(invalid)?.len();
            for app in apps {
                length += usize::from(!self.apps.is_empty())
                    + serde_json::to_vec(&app).map_err(invalid)?.len();
                if length > limit {
                    break;
                }
                self.apps.push(app);
            }
            body = serde_json::to_vec(&self).map_err(invalid)?;
        }
        if body.is_empty() || body.len() > limit {
            return Err(invalid("invalid Session GPU response length"));
        }
        let mut frame = Vec::with_capacity(4 + body.len());
        frame.extend((body.len() as i32).to_le_bytes());
        frame.extend(body);
        Ok(frame)
    }
}

/// The worker that owned a blocking OS call failed without a result.
#[derive(Debug)]
struct WorkerLost;

type QueryResult = Result<GpuAppInventory, QueryUnavailable>;
type CloseResult = Result<(GpuAppInventory, Report), QueryUnavailable>;

trait Supplier {
    /// A fresh scan (managed `ScanAsync`).
    fn query(
        &mut self,
        cancelled: Arc<AtomicBool>,
    ) -> impl Future<Output = Result<QueryResult, WorkerLost>>;
    /// A fresh complete scan, matched to the targets, then the close. Resolves
    /// only after the blocking work returned, even when cancellation was
    /// requested first, so every submission is accounted for.
    fn close(
        &mut self,
        targets: Vec<Target>,
        kind: Kind,
        cancelled: Arc<AtomicBool>,
    ) -> impl Future<Output = Result<CloseResult, WorkerLost>>;
}

#[cfg_attr(test, allow(dead_code, reason = "tests use scripted suppliers"))]
struct WindowsSupplier;

impl Supplier for WindowsSupplier {
    async fn query(&mut self, cancelled: Arc<AtomicBool>) -> Result<QueryResult, WorkerLost> {
        tokio::task::spawn_blocking(move || gpu_apps::sample_windows_gpu_apps(&cancelled))
            .await
            .map_err(|_| WorkerLost)
    }

    async fn close(
        &mut self,
        targets: Vec<Target>,
        kind: Kind,
        cancelled: Arc<AtomicBool>,
    ) -> Result<CloseResult, WorkerLost> {
        tokio::task::spawn_blocking(move || {
            gpu_apps::close_windows_gpu_apps(&targets, kind, &cancelled)
        })
        .await
        .map_err(|_| WorkerLost)
    }
}

/// What a finished dispatch says to the client.
enum Answer {
    Reply(Response),
    /// Managed: an `OperationCanceledException` writes nothing.
    Silent,
}

fn status(error: Option<&'static str>, results: &[ActionResult]) -> u8 {
    if error.is_none() {
        SUCCEEDED
    } else if results.iter().all(|result| !result.succeeded) {
        FAILED
    } else {
        PARTIAL
    }
}

fn unavailable(request_id: &str, error: QueryUnavailable) -> Answer {
    Answer::Reply(Response::failed(
        request_id,
        match error {
            QueryUnavailable::Cancelled => return Answer::Silent,
            // Managed reads the current user once at startup and compares
            // names with the invariant culture; neither has an unavailable
            // result, so a failure there is the unexpected-failure reply.
            QueryUnavailable::CurrentUser | QueryUnavailable::NameComparison => DISPATCH_FAILURE,
            QueryUnavailable::Counters
            | QueryUnavailable::UnsupportedAdapter
            | QueryUnavailable::AdapterDiscovery(_)
            | QueryUnavailable::AdapterChanged
            | QueryUnavailable::MissingData
            | QueryUnavailable::MalformedCounters
            | QueryUnavailable::NativeCounter(_) => COUNTERS_UNAVAILABLE,
        },
    ))
}

/// Managed `SessionGpuRequestDispatcher` for a completed query.
fn answer_query(request_id: &str, result: QueryResult) -> Answer {
    match result {
        Ok(inventory) => {
            let truncated = inventory.truncated || inventory.apps.len() > MAXIMUM_APPS;
            Answer::Reply(if truncated {
                Response::with_apps(request_id, PARTIAL, Some(TRUNCATED), inventory)
            } else {
                Response::with_apps(request_id, SUCCEEDED, None, inventory)
            })
        }
        Err(error) => unavailable(request_id, error),
    }
}

/// Managed `SessionGpuRequestDispatcher` for a completed close: stale
/// confirmations, then action results, then unattempted targets, each list
/// capped at 128, beside the scan's apps.
fn answer_close(request_id: &str, result: CloseResult) -> Answer {
    let (inventory, report) = match result {
        Ok(value) => value,
        Err(error) => return unavailable(request_id, error),
    };
    let results: Vec<_> = report
        .results
        .into_iter()
        .take(MAXIMUM_APPS)
        .map(|result| ActionResult {
            process_id: result.process_id,
            start_time_utc_ticks: result.start_time_utc_ticks,
            succeeded: result.error.is_none(),
            error: result.error,
        })
        .collect();
    let mut response = Response::with_apps(
        request_id,
        status(report.error, &results),
        report.error,
        inventory,
    );
    response.results = results;
    Answer::Reply(response)
}

struct Cancellation(Arc<AtomicBool>);
impl Drop for Cancellation {
    fn drop(&mut self) {
        self.0.store(true, Ordering::Release);
    }
}

/// One listener and one serialized request; no queue or spawned connection
/// task. The claimed first instance stays held while the endpoint drains.
#[cfg_attr(
    test,
    allow(dead_code, reason = "composed only by the installed monitor")
)]
pub(super) async fn run(
    origin: Arc<crate::loaded_origin::Origin>,
    mut stop: watch::Receiver<bool>,
) -> io::Result<()> {
    super::verify_origin(&origin)?;
    let endpoint = Endpoint::current_user_gpu().map_err(diagnostics::listener)?;
    let _claim = endpoint.claim().map_err(diagnostics::claim)?;
    run_connections(
        &mut stop,
        &mut WindowsSupplier,
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

async fn origin_invalid(verify: &impl Fn() -> io::Result<()>) -> io::Error {
    loop {
        if let Err(error) = verify() {
            return error;
        }
        sleep(ORIGIN_POLL).await;
    }
}

/// Only an accepted connection's errors are contained here. Listener,
/// installed-origin and lost close-worker failures are fatal.
async fn run_connections<S, F>(
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
            Ok(()) => (),
            Err(ServeError::Connection(error)) => drop(error),
            Err(ServeError::Fatal(error)) => return Err(error),
        }
    }
}

enum Work {
    Query(Result<QueryResult, WorkerLost>),
    Close(Result<CloseResult, WorkerLost>),
}

/// Every started blocking operation is awaited, including on deadline,
/// disconnect, Stop and origin loss; cancellation is a request, never
/// evidence that the worker returned. Stop, the deadline and a disconnect
/// end without a reply, as a managed cancellation does.
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
    let request_id = request.request_id().to_owned();
    let query = matches!(request, Request::Query { .. });
    // No close is refused for an earlier unconfirmed one. Every close takes
    // its own fresh scan and re-reads each identity on the handle it acts on,
    // so an app an earlier close already ended is reported as changed, and
    // repeating a termination that did apply is harmless. This keeps the
    // Tray's one-request-per-app force-close loop working as with managed.
    let canceled = Cancellation(Arc::new(AtomicBool::new(false)));
    let flag = canceled.0.clone();
    let work = async {
        match request {
            Request::Query { .. } => Work::Query(supplier.query(flag).await),
            Request::Close { kind, targets, .. } => {
                let targets = targets
                    .into_iter()
                    .map(|target| Target {
                        process_id: target.process_id,
                        start_time_utc_ticks: target.start_time_utc_ticks,
                        name: target.name,
                    })
                    .collect();
                Work::Close(supplier.close(targets, kind, flag).await)
            }
        }
    };
    tokio::pin!(work);
    let mut extra = [0u8];
    let end = tokio::select! {
        biased;
        _ = stopped(stop) => None,
        error = origin_invalid(&verify) => Some(Err(error)),
        _ = sleep(DISPATCH_TIMEOUT) => None,
        _ = pipe.read(&mut extra) => None,
        work = &mut work => Some(Ok(work)),
    };
    let (work, reply) = match end {
        Some(Ok(work)) => (work, true),
        interrupted => {
            canceled.0.store(true, Ordering::Release);
            let work = work.await;
            if let Some(Err(error)) = interrupted {
                return Err(ServeError::Fatal(diagnostics::origin(error)));
            }
            (work, false)
        }
    };
    let answer = match work {
        Work::Close(Err(WorkerLost)) => {
            // A lost close worker may have submitted an action. Never report
            // or retry it; end the endpoint, and with it the Agent.
            return Err(ServeError::Fatal(io::Error::other(
                "the GPU close worker ended without a result",
            )));
        }
        Work::Query(Err(WorkerLost)) => {
            Answer::Reply(Response::failed(&request_id, DISPATCH_FAILURE))
        }
        Work::Query(Ok(result)) => answer_query(&request_id, result),
        Work::Close(Ok(result)) => answer_close(&request_id, result),
    };
    let Answer::Reply(response) = answer else {
        return Ok(());
    };
    if !reply || *stop.borrow() {
        return Ok(());
    }
    let frame = response.frame(query)?;
    verify().map_err(|e| ServeError::Fatal(diagnostics::origin(e)))?;
    authorize(pipe)?;
    tokio::select! {
        biased;
        _ = stopped(stop) => Ok(()),
        result = timeout(FRAME_TIMEOUT, pipe.write_all(&frame)) => { result??; Ok(()) },
    }
}

async fn read_frame(pipe: &mut (impl AsyncRead + Unpin)) -> io::Result<Vec<u8>> {
    let length = pipe.read_i32_le().await?;
    if !(1..=MAX_FRAME).contains(&length) {
        return Err(invalid("invalid Session GPU frame length"));
    }
    let mut body = vec![0; length as usize];
    pipe.read_exact(&mut body).await?;
    Ok(body)
}

#[cfg(test)]
#[path = "gpu/request_parity_tests.rs"]
mod request_parity_tests;
#[cfg(test)]
mod tests;

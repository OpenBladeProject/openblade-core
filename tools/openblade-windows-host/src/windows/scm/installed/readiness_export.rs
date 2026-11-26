//! Managed `ControlRequestHandler.ExportDiagnosticsAsync` over this
//! Service's status: the default control admission, one export at a time,
//! the archive built off the runtime thread, and the managed failure reply.
//!
//! Admission: SYSTEM and the console session's interactive caller export; an
//! inactive or remote interactive caller gets managed's session refusal. A
//! non-interactive caller closes the connection, the endpoint-wide native
//! convention, where managed replies with its non-interactive refusal. Native
//! activates no user first: the export reads only published status, so
//! managed's activation failures and its session-changed `Blocked` result
//! have no native step. Instead the archive, which carries the caller's
//! private status projection, is answered only while the caller still
//! controls the session; otherwise the connection closes.

use std::sync::atomic::{AtomicBool, Ordering};

use super::*;
use crate::diagnostic_export::{self as export, Cancel, CancelOnDrop, Failure};
use crate::service_frame::{ArchiveFrame, encode_archive_frame};

/// Managed's refusal of an interactive caller that does not control the
/// console session (`ServiceRequestAccessPolicy.Evaluate`).
pub(super) const INACTIVE: &str = "Another Windows session currently controls OpenBlade hardware.";

/// How long the archive build may take before the export fails. Managed has
/// no bound; native bounds the build so a stalled one cannot hold the export
/// gate.
pub(super) const EXPORT_BUDGET: Duration = Duration::from_secs(20);

/// The failure of a build that passed its bound, replied again to an export
/// that arrives while that build is still running.
pub(super) const TIME_BOUND: &str =
    "The diagnostic archive did not complete within its time bound.";

/// The export state one server owns. Managed serializes an export with every
/// finite request through its user request gate. Native reads only published
/// status, so only exports are serialized: a second export waits for the
/// first.
///
/// A build that passes its bound is canceled but cannot be interrupted: it
/// ends at its next cancellation check. Until it does, a further export is
/// answered with the same time-bound failure instead of starting a second
/// build beside it, so unfinished builds never accumulate. This is the rule
/// of the status source's observation worker, which never queues a
/// replacement behind a running one.
pub(super) struct Exports {
    turn: tokio::sync::Semaphore,
    building: Arc<AtomicBool>,
}

impl Exports {
    pub(super) fn new() -> Self {
        Self {
            turn: tokio::sync::Semaphore::new(1),
            building: Arc::default(),
        }
    }

    /// Marks a build as running until the returned guard drops, on the
    /// blocking thread that runs it.
    fn begin(&self) -> Building {
        self.building.store(true, Ordering::Release);
        Building(self.building.clone())
    }

    pub(super) fn is_building(&self) -> bool {
        self.building.load(Ordering::Acquire)
    }
}

struct Building(Arc<AtomicBool>);

impl Drop for Building {
    fn drop(&mut self) {
        self.0.store(false, Ordering::Release);
    }
}

pub(super) async fn serve_export(
    stream: &mut dyn Stream,
    request: &StatusRequest,
    caller: Caller,
    source: &Mutex<Box<dyn StatusSource>>,
    exports: &Exports,
) -> Result<(), ExchangeError> {
    use ExchangeError::{Client, Runtime};
    // `refresh_caller` refuses a non-interactive caller before any read.
    let StatusRequest::ExportDiagnostics {
        live_readings_enabled,
        ..
    } = *request
    else {
        return Err(Runtime(io::Error::other("not an export request")));
    };
    let reply = |response: StatusResponse| encode_archive_frame(&response).map_err(Client);
    let refused = || reply(StatusResponse::failure(request, INACTIVE, None));
    let frame = if refresh_caller(stream, caller)?.access().map_err(Client)? != 0 {
        refused()?
    } else {
        let _turn = exports
            .turn
            .acquire()
            .await
            .map_err(|error| Runtime(io::Error::other(error)))?;
        if exports.is_building() {
            reply(StatusResponse::failure(
                request,
                &Failure::new(TIME_BOUND).reply(),
                None,
            ))?
        } else {
            match capture(stream, caller, source).await? {
                None => refused()?,
                Some((status, sources)) => {
                    let built = build(
                        stream,
                        exports,
                        request,
                        status,
                        sources,
                        live_readings_enabled,
                    )
                    .await?;
                    // The archive holds this caller's private projection.
                    if refresh_caller(stream, caller)?.access().map_err(Client)? != 0 {
                        return Err(Client(io::ErrorKind::PermissionDenied.into()));
                    }
                    source.lock().await.verify_origin().map_err(Runtime)?;
                    match built {
                        Ok(frame) => frame,
                        Err(failure) => {
                            reply(StatusResponse::failure(request, &failure.reply(), None))?
                        }
                    }
                }
            }
        }
    };
    crate::service_frame::write_encoded_archive_frame(stream, &frame)
        .await
        .map_err(Client)
}

/// The status a GET would answer this caller with, and the diagnostic
/// sources, under the source lock. `None` when the caller no longer
/// controls the session.
async fn capture(
    stream: &dyn Stream,
    caller: Caller,
    source: &Mutex<Box<dyn StatusSource>>,
) -> Result<Option<(serde_json::Value, export::Sources)>, ExchangeError> {
    use ExchangeError::{Client, Runtime};
    let mut source = source.lock().await;
    source.verify_origin().map_err(Runtime)?;
    let current = refresh_caller(stream, caller)?;
    if current.access().map_err(Client)? != 0 {
        return Ok(None);
    }
    let status = timeout(FRAME_TIMEOUT, source.snapshot(current, Instant::now()))
        .await
        .map_err(|e| Runtime(e.into()))?
        .map_err(Runtime)?;
    status.verify(0).map_err(Runtime)?;
    #[cfg(feature = "service-thermal-mutation-v1")]
    let status = project_private_status(stream, current, &status)?;
    // The archive reads this value by member name and writes no status
    // record whole, so the member order serde_json gives it never reaches
    // the archive (`an_archive_writes_no_lighting_parameters_from_the_status`).
    let status = serde_json::to_value(&status).map_err(|e| Runtime(e.into()))?;
    Ok(Some((status, source.diagnostics())))
}

/// Builds the archive and its response frame on the blocking pool, where the
/// compression, base64 and JSON encoding of up to 16 MiB do not stall the
/// runtime. A disconnect, the budget or a dropped connection task (Stop)
/// cancels it; a disconnect closes without a reply.
async fn build(
    stream: &mut dyn Stream,
    exports: &Exports,
    request: &StatusRequest,
    status: serde_json::Value,
    sources: export::Sources,
    live_readings_enabled: bool,
) -> Result<Result<ArchiveFrame, Failure>, ExchangeError> {
    use ExchangeError::Client;
    let cancel = Cancel::default();
    // Every return, and a dropped task, cancels the build.
    let _cancel = CancelOnDrop(cancel.clone());
    let building = exports.begin();
    let task = tokio::task::spawn_blocking({
        let cancel = cancel.clone();
        let request = request.clone();
        move || {
            // Held until the thread is done, whether or not this export
            // still waits for it.
            let _building = building;
            let history = sources.history.snapshot()?;
            let metadata = sources.metadata.capture(&cancel)?;
            let archive = export::build(
                export::Request {
                    status,
                    history,
                    metadata,
                    live_readings_enabled,
                },
                &cancel,
            )?;
            cancel.check()?;
            let export::Archive { file_name, bytes } = archive;
            let zip_bytes = export::base64(&bytes);
            drop(bytes);
            // Under the managed response bound for the 16 MiB archive bound.
            encode_archive_frame(&StatusResponse::archive(&request, file_name, zip_bytes))
                .map_err(|_| Failure::new("The diagnostic archive is too large to send."))
        }
    });
    let mut probe = [0];
    let joined = tokio::select! {
        biased;
        disconnected = stream.read(&mut probe) => {
            disconnected.map_err(Client)?;
            return Err(Client(io::ErrorKind::BrokenPipe.into()));
        }
        joined = timeout(EXPORT_BUDGET, task) => joined,
    };
    match joined {
        Err(_) => Ok(Err(Failure::new(TIME_BOUND))),
        // A panicking build fails this export only.
        Ok(Err(error)) => Err(Client(io::Error::other(error))),
        Ok(Ok(result)) => Ok(result),
    }
}

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

use super::*;
use crate::diagnostic_export::{self as export, Cancel, CancelOnDrop, Failure};

/// Managed's refusal of an interactive caller that does not control the
/// console session (`ServiceRequestAccessPolicy.Evaluate`).
pub(super) const INACTIVE: &str = "Another Windows session currently controls OpenBlade hardware.";

/// How long the archive build may take before the export fails. Managed has
/// no bound; native bounds the build so a stalled one cannot hold the export
/// gate.
pub(super) const EXPORT_BUDGET: Duration = Duration::from_secs(20);

/// Managed serializes an export with every finite request through its user
/// request gate. Native reads only published status, so only exports are
/// serialized: a second export waits for the first.
static EXPORTS: tokio::sync::Semaphore = tokio::sync::Semaphore::const_new(1);

pub(super) async fn serve_export(
    stream: &mut dyn Stream,
    request: &StatusRequest,
    caller: Caller,
    source: &Mutex<Box<dyn StatusSource>>,
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
    let refused = || StatusResponse::failure(request, INACTIVE, None);
    let response = if refresh_caller(stream, caller)?.access().map_err(Client)? != 0 {
        refused()
    } else {
        let _export = EXPORTS
            .acquire()
            .await
            .map_err(|error| Runtime(io::Error::other(error)))?;
        match capture(stream, caller, source).await? {
            None => refused(),
            Some((status, sources)) => {
                let built = build(stream, status, sources, live_readings_enabled).await?;
                // The archive holds this caller's private projection.
                if refresh_caller(stream, caller)?.access().map_err(Client)? != 0 {
                    return Err(Client(io::ErrorKind::PermissionDenied.into()));
                }
                source.lock().await.verify_origin().map_err(Runtime)?;
                match built {
                    Ok(archive) => StatusResponse::archive(
                        request,
                        archive.file_name,
                        export::base64(&archive.bytes),
                    ),
                    Err(failure) => StatusResponse::failure(request, &failure.reply(), None),
                }
            }
        }
    };
    crate::service_frame::write_archive_frame(stream, &response)
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
    let status = serde_json::to_value(&status).map_err(|e| Runtime(e.into()))?;
    Ok(Some((status, source.diagnostics())))
}

/// Builds the archive on the blocking pool. A disconnect, the budget or a
/// dropped connection task (Stop) cancels it; a disconnect closes without a
/// reply.
async fn build(
    stream: &mut dyn Stream,
    status: serde_json::Value,
    sources: export::Sources,
    live_readings_enabled: bool,
) -> Result<Result<export::Archive, Failure>, ExchangeError> {
    use ExchangeError::Client;
    let cancel = Cancel::default();
    // Every return, and a dropped task, cancels the build.
    let _cancel = CancelOnDrop(cancel.clone());
    let task = tokio::task::spawn_blocking({
        let cancel = cancel.clone();
        move || {
            let history = sources.history.snapshot()?;
            let metadata = sources.metadata.capture(&cancel)?;
            export::build(
                export::Request {
                    status,
                    history,
                    metadata,
                    live_readings_enabled,
                },
                &cancel,
            )
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
        Err(_) => Ok(Err(Failure::new(
            "The diagnostic archive did not complete within its time bound.",
        ))),
        // A panicking build fails this export only.
        Ok(Err(error)) => Err(Client(io::Error::other(error))),
        Ok(Ok(result)) => Ok(result),
    }
}

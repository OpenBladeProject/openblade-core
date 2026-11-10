//! Version 2 of the managed Session GPU wire contract on a private query-only pipe.

use std::{
    io,
    sync::{
        Arc,
        atomic::{AtomicBool, Ordering},
    },
    time::Duration,
};

use openblade_platform_windows::gpu_apps::{
    GpuAppInventory, MAXIMUM_APPS, QueryUnavailable,
    scripted::{self, Scenario},
};
use openblade_windows_host::pipe::Identity;
use serde::{Deserialize, Serialize};
use tokio::{
    io::{AsyncReadExt, AsyncWriteExt},
    net::windows::named_pipe::NamedPipeServer,
    time::{sleep, timeout},
};

use crate::session_windows::valid_request_id;

const VERSION: i32 = 2;
const MAX_FRAME_BYTES: i32 = 65_536;
const FRAME_TIMEOUT: Duration = Duration::from_secs(2);
const SCAN_TIMEOUT: Duration = Duration::from_secs(4);
const SUCCEEDED: u8 = 0;
const PARTIAL: u8 = 1;
const FAILED: u8 = 2;
const NO_ACTIONS: &str = "Close GPU apps is unsupported on the private query endpoint.";
const TRUNCATED: &str = "Only part of the GPU application inventory fits in this response.";

#[derive(Debug, Deserialize)]
#[serde(
    tag = "operation",
    rename_all = "camelCase",
    rename_all_fields = "camelCase",
    deny_unknown_fields
)]
enum Request {
    Query {
        version: i32,
        request_id: String,
    },
    Close {
        version: i32,
        request_id: String,
        force: bool,
        targets: Vec<Target>,
    },
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Target {
    process_id: i32,
    start_time_utc_ticks: i64,
    name: String,
}

impl Request {
    fn parse(body: &[u8]) -> io::Result<Self> {
        let request: Self = serde_json::from_slice(body).map_err(invalid_data)?;
        let (version, id) = match &request {
            Self::Query {
                version,
                request_id,
            } => (version, request_id),
            Self::Close {
                version,
                request_id,
                force,
                targets,
            } => {
                let mut seen = std::collections::BTreeSet::new();
                if targets.is_empty()
                    || targets.len() > MAXIMUM_APPS
                    || (*force && targets.len() != 1)
                    || targets.iter().any(|target| {
                        target.process_id <= 0
                            || target.start_time_utc_ticks <= 0
                            || !valid_name(&target.name)
                            || !seen.insert(target.process_id)
                    })
                {
                    return Err(invalid_data("invalid Session GPU close targets"));
                }
                (version, request_id)
            }
        };
        if *version != VERSION || !valid_request_id(id) {
            return Err(invalid_data("invalid Session GPU envelope"));
        }
        Ok(request)
    }
}

fn valid_name(name: &str) -> bool {
    // .NET IsWhiteSpace differs from Rust's trim for U+0085, but that
    // character is already rejected by the shared control-character rule.
    !name.is_empty()
        && name.trim() == name
        && name.encode_utf16().count() <= 260
        && !name.chars().any(char::is_control)
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct Response<'a> {
    version: i32,
    request_id: &'a str,
    status: u8,
    error: Option<&'static str>,
    apps: Vec<App>,
    results: [(); 0],
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct App {
    process_id: i32,
    start_time_utc_ticks: i64,
    name: String,
    utilization_percent: f64,
    can_close: bool,
    block_reason: &'static str,
}

impl<'a> Response<'a> {
    fn new(request_id: &'a str, status: u8, error: Option<&'static str>) -> Self {
        Self {
            version: VERSION,
            request_id,
            status,
            error,
            apps: Vec::new(),
            results: [],
        }
    }

    fn inventory(request_id: &'a str, inventory: GpuAppInventory) -> io::Result<Self> {
        let mut response = if inventory.truncated {
            Self::new(request_id, PARTIAL, Some(TRUNCATED))
        } else {
            Self::new(request_id, SUCCEEDED, None)
        };
        for app in inventory.apps {
            if app.process_id <= 0
                || app.start_time_utc_ticks <= 0
                || !valid_name(&app.name)
                || !app.utilization_percent.is_finite()
                || app.utilization_percent < 0.0
            {
                return Err(invalid_data("invalid projected GPU application"));
            }
            response.apps.push(App {
                process_id: app.process_id,
                start_time_utc_ticks: app.start_time_utc_ticks,
                name: app.name,
                utilization_percent: app.utilization_percent,
                can_close: false,
                block_reason: app
                    .block_reason
                    .map_or(NO_ACTIONS, |reason| reason.message()),
            });
        }
        Ok(response)
    }

    fn frame(mut self) -> io::Result<Vec<u8>> {
        let limit = MAX_FRAME_BYTES as usize;
        let mut body = serde_json::to_vec(&self).map_err(invalid_data)?;
        if body.len() > limit {
            self.status = PARTIAL;
            self.error = Some(TRUNCATED);
            // Preserve the largest ordered prefix that fits the byte cap.
            let apps = std::mem::take(&mut self.apps);
            let mut length = serde_json::to_vec(&self).map_err(invalid_data)?.len();
            for app in apps {
                length += usize::from(!self.apps.is_empty())
                    + serde_json::to_vec(&app).map_err(invalid_data)?.len();
                if length > limit {
                    break;
                }
                self.apps.push(app);
            }
            body = serde_json::to_vec(&self).map_err(invalid_data)?;
        }
        if body.is_empty() || body.len() > limit {
            return Err(invalid_data("invalid Session GPU response length"));
        }
        let mut frame = Vec::with_capacity(4 + body.len());
        frame.extend((body.len() as i32).to_le_bytes());
        frame.extend(body);
        Ok(frame)
    }
}

fn invalid_data(error: impl Into<Box<dyn std::error::Error + Send + Sync>>) -> io::Error {
    io::Error::new(io::ErrorKind::InvalidData, error)
}

struct CancelScan(Arc<AtomicBool>);

impl Drop for CancelScan {
    fn drop(&mut self) {
        self.0.store(true, Ordering::Relaxed);
    }
}

pub(super) async fn serve(
    pipe: &mut NamedPipeServer,
    identity: &Identity,
    scenario: Scenario,
) -> io::Result<()> {
    let body = timeout(FRAME_TIMEOUT, async {
        let length = pipe.read_i32_le().await?;
        if !(1..=MAX_FRAME_BYTES).contains(&length) {
            return Err(invalid_data("invalid Session GPU request length"));
        }
        let mut body = vec![0; length as usize];
        pipe.read_exact(&mut body).await?;
        Ok(body)
    })
    .await??;

    // The read supplies the token for this message. No dispatch before admission.
    identity.authorize(pipe)?;
    let request = Request::parse(&body)?;
    let frame = match request {
        Request::Close { request_id, .. } => {
            Response::new(&request_id, FAILED, Some(NO_ACTIONS)).frame()?
        }
        Request::Query { request_id, .. } => {
            let cancel = CancelScan(Arc::new(AtomicBool::new(false)));
            let worker_cancel = Arc::clone(&cancel.0);
            let mut worker =
                tokio::task::spawn_blocking(move || scripted::sample(scenario, &worker_cancel));
            let mut extra = [0];
            let response = tokio::select! {
                biased;
                _ = pipe.read(&mut extra) => {
                    drop(cancel);
                    // Retire the old scan before accepting another client.
                    let _ = worker.await.map_err(io::Error::other)?;
                    return Ok(());
                }
                result = &mut worker => match result.map_err(io::Error::other)? {
                    Ok(inventory) => Response::inventory(&request_id, inventory)?,
                    Err(QueryUnavailable::MissingData) => Response::new(&request_id, FAILED, Some("GPU activity data is unavailable.")),
                    Err(_) => Response::new(&request_id, FAILED, Some("GPU application inventory is unavailable.")),
                },
                _ = sleep(SCAN_TIMEOUT) => {
                    drop(cancel);
                    let _ = worker.await.map_err(io::Error::other)?;
                    Response::new(&request_id, FAILED, Some("The GPU inventory scan timed out."))
                }
            };
            response.frame()?
        }
    };
    timeout(FRAME_TIMEOUT, pipe.write_all(&frame)).await??;
    Ok(())
}

#[cfg(test)]
mod tests;

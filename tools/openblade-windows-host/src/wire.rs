use std::future::pending;
use std::io::{self, ErrorKind};
use std::sync::Arc;
use std::time::Duration;

use serde::{Deserialize, Serialize};
use serde_json::{Map, Value};
use tokio::io::{AsyncRead, AsyncReadExt, AsyncWrite, AsyncWriteExt};
use tokio::sync::{Semaphore, watch};
use tokio::time::timeout;

use crate::{
    battery_policy::{Intent, Limit},
    battery_runtime::{self, LimitValue, Scenario},
    performance_runtime,
};
use openblade_domain::recovery::ScopedStatus;

const CURRENT_VERSION: i32 = 79;
const LEGACY_VERSION: i32 = 51;
const MAX_FRAME_BYTES: i32 = 65_536;
const FRAME_TIMEOUT: Duration = Duration::from_secs(2);

pub(crate) const CURRENT_FIXTURE: &str = include_str!(
    "../../../tests/OpenBlade.Contracts.Tests/WindowsReplay/v1/service-status-event.json"
);
const LEGACY_FIXTURE: &str =
    include_str!("../../../rust/crates/openblade-status-cli/fixtures/response.json");

#[derive(Clone, Copy)]
pub enum Session {
    Active,
    Background,
}

pub struct Status {
    active: Projection,
    replay: Option<watch::Receiver<Arc<Value>>>,
    battery: Option<Arc<Scenario>>,
    performance: Option<Arc<performance_runtime::Scenario>>,
    background: Projection,
}

struct Projection {
    current: Value,
    initial: Value,
    legacy: Value,
}

impl Status {
    pub fn fixture() -> io::Result<Self> {
        let event: Value = serde_json::from_str(CURRENT_FIXTURE)?;
        let response: Value = serde_json::from_str(LEGACY_FIXTURE)?;
        if event["frame"] != "statusEvent"
            || event["version"] != CURRENT_VERSION
            || response["frame"] != "response"
            || response["version"] != LEGACY_VERSION
            || response["success"] != true
        {
            return Err(invalid("unexpected embedded status fixture envelope"));
        }

        let current = event["status"].clone();
        let mut legacy = select_fields(
            &response["status"],
            &[
                "revision",
                "deviceVerified",
                "activePowerSource",
                "controlAccess",
                "liveReadings",
            ],
        )?;
        legacy["liveReadings"] = select_fields(
            &legacy["liveReadings"],
            &[
                "batteryPowerFlowMilliwatts",
                "fan1Rpm",
                "fan2Rpm",
                "cpuTemperatureCelsius",
                "gpuTemperatureCelsius",
            ],
        )?;

        let mut background = current.clone();
        // The replay body already has safe default profiles and no user overrides.
        // Only the inactive-session access and message differ for this snapshot.
        background["controlAccess"] = 1.into();
        background["message"] =
            "Another Windows session currently controls OpenBlade hardware.".into();
        let mut background_legacy = legacy.clone();
        background_legacy["controlAccess"] = 1.into();

        Ok(Self {
            active: Projection::new(current, legacy)?,
            replay: None,
            battery: None,
            performance: None,
            background: Projection::new(background, background_legacy)?,
        })
    }

    pub fn with_battery(
        mut self,
        advance: tokio::sync::mpsc::Receiver<()>,
    ) -> io::Result<(Self, impl Future<Output = io::Result<()>>)> {
        let scenario = Arc::new(Scenario::new(self.active.current.clone())?);
        self.replay = Some(scenario.subscribe());
        self.battery = Some(Arc::clone(&scenario));
        let drive = async move { scenario.drive(advance).await };
        Ok((self, drive))
    }

    pub fn with_performance(
        mut self,
        advance: tokio::sync::mpsc::Receiver<()>,
    ) -> io::Result<(Self, impl Future<Output = io::Result<()>>)> {
        let scenario = Arc::new(performance_runtime::Scenario::new(
            self.active.current.clone(),
        )?);
        self.replay = Some(scenario.subscribe());
        self.performance = Some(Arc::clone(&scenario));
        let drive = async move { scenario.drive(advance).await };
        Ok((self, drive))
    }

    pub fn with_replay(
        mut self,
        advance: tokio::sync::mpsc::Receiver<()>,
    ) -> (Self, impl Future<Output = io::Result<()>>) {
        let (status, drive) = crate::runtime::start(self.active.current.clone(), advance);
        self.replay = Some(status);
        (self, drive)
    }

    fn for_session(&self, session: Session) -> &Projection {
        match session {
            Session::Active => &self.active,
            Session::Background => &self.background,
        }
    }
}

impl Projection {
    fn new(current: Value, legacy: Value) -> io::Result<Self> {
        let revision = current["revision"]
            .as_i64()
            .filter(|revision| *revision > 0)
            .ok_or_else(|| invalid("embedded status fixture has no preceding revision"))?;
        // A subscription must observe a strict increase. Keep the event itself
        // equal to the managed fixture and give the initial response its predecessor.
        let mut initial = current.clone();
        initial["revision"] = (revision - 1).into();
        Ok(Self {
            current,
            initial,
            legacy,
        })
    }
}

fn select_fields(source: &Value, keys: &[&str]) -> io::Result<Value> {
    keys.iter()
        .map(|&key| {
            let value = source
                .get(key)
                .ok_or_else(|| invalid("missing embedded status field"))?;
            Ok((key.to_owned(), value.clone()))
        })
        .collect::<io::Result<Map<_, _>>>()
        .map(Value::Object)
}

#[derive(Serialize)]
struct OperationResult {
    status: i32,
    message: &'static str,
}

impl From<battery_runtime::Result> for OperationResult {
    fn from(result: battery_runtime::Result) -> Self {
        Self::scoped(result.status, result.message)
    }
}

impl OperationResult {
    fn scoped(result: ScopedStatus, message: &'static str) -> Self {
        let status = match result {
            ScopedStatus::Applied | ScopedStatus::Unchanged => 1,
            ScopedStatus::Blocked => 2,
            ScopedStatus::Failed => 3,
        };
        Self { status, message }
    }
}

// Decode the original bytes to retain duplicate-field rejection, including
// nested policy fields. A map projection would silently discard duplicates.
#[derive(Deserialize)]
#[serde(tag = "operation", rename_all = "camelCase", deny_unknown_fields)]
enum BatteryRequest {
    SetBatteryPolicy {
        #[serde(rename = "version")]
        _version: i32,
        #[serde(rename = "requestId")]
        _request_id: String,
        value: LimitValue,
    },
    SetBatteryProtection {
        #[serde(rename = "version")]
        _version: i32,
        #[serde(rename = "requestId")]
        _request_id: String,
        enabled: bool,
    },
}

impl BatteryRequest {
    fn decode(payload: &[u8]) -> io::Result<Intent> {
        match serde_json::from_slice(payload).map_err(|_| invalid("invalid battery request"))? {
            Self::SetBatteryPolicy { value, .. } => Limit::new(value.charge_limit_percent)
                .map(Intent::Limit)
                .ok_or_else(|| invalid("invalid battery limit")),
            Self::SetBatteryProtection { enabled, .. } => Ok(Intent::Protection(enabled)),
        }
    }
}

#[derive(Deserialize)]
#[serde(tag = "operation", rename_all = "camelCase", deny_unknown_fields)]
enum PerformanceRequest {
    SetPerformanceOverride {
        #[serde(rename = "version")]
        _version: i32,
        #[serde(rename = "requestId")]
        _request_id: String,
        // Require the field while allowing the explicit null used to clear.
        #[serde(deserialize_with = "Deserialize::deserialize")]
        value: Option<performance_runtime::Override>,
    },
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Request {
    operation: String,
    version: i32,
    request_id: String,
    #[serde(flatten)]
    operation_fields: Map<String, Value>,
}

#[derive(Serialize)]
#[serde(
    tag = "frame",
    rename_all = "camelCase",
    rename_all_fields = "camelCase"
)]
enum Frame<'a> {
    Response {
        version: i32,
        request_id: &'a str,
        success: bool,
        error: Option<&'static str>,
        status: Option<&'a Value>,
        result: Option<OperationResult>,
        archive: Option<()>,
        error_code: Option<i32>,
    },
    StatusEvent {
        version: i32,
        status: &'a Value,
    },
}

impl Request {
    fn decode(payload: &[u8]) -> io::Result<Self> {
        let mut request: Self =
            serde_json::from_slice(payload).map_err(|_| invalid("invalid request envelope"))?;
        if !matches!(request.version, CURRENT_VERSION | LEGACY_VERSION)
            || !is_guid(&request.request_id)
        {
            return Err(invalid("invalid request version or ID"));
        }
        if matches!(request.operation.as_str(), "getStatus" | "subscribeStatus")
            && !request.operation_fields.is_empty()
        {
            return Err(invalid("unexpected status request fields"));
        }
        request.request_id.make_ascii_lowercase();
        Ok(request)
    }

    fn response<'a>(
        &'a self,
        status: Option<&'a Value>,
        error: Option<&'static str>,
        error_code: Option<i32>,
    ) -> Frame<'a> {
        Frame::Response {
            version: self.version,
            request_id: &self.request_id,
            success: status.is_some(),
            error,
            status,
            result: None,
            archive: None,
            error_code,
        }
    }
    fn operation_response(&self, result: OperationResult) -> Frame<'_> {
        Frame::Response {
            version: self.version,
            request_id: &self.request_id,
            success: true,
            error: None,
            status: None,
            result: Some(result),
            archive: None,
            error_code: None,
        }
    }
}

fn is_guid(id: &str) -> bool {
    id.len() == 36
        && id.bytes().enumerate().all(|(index, byte)| match index {
            8 | 13 | 18 | 23 => byte == b'-',
            _ => byte.is_ascii_hexdigit(),
        })
        && id != "00000000-0000-0000-0000-000000000000"
}

pub async fn serve<S, A>(
    mut io: S,
    authorize: A,
    status: &Status,
    subscriptions: &Semaphore,
) -> io::Result<()>
where
    S: AsyncRead + AsyncWrite + Unpin,
    A: FnOnce(&S) -> io::Result<Session>,
{
    let payload = timeout(FRAME_TIMEOUT, read_payload(&mut io)).await??;
    // Windows supplies the token associated with the last pipe read. Resolve it
    // after the exact first payload and before parsing any client-supplied JSON.
    let session = authorize(&io)?;
    let projected = status.for_session(session);
    let replay = match session {
        Session::Active => status.replay.as_ref(),
        Session::Background => None,
    };
    let request = Request::decode(&payload)?;
    match (request.version, request.operation.as_str()) {
        (LEGACY_VERSION, "getStatus") => {
            write_frame(
                &mut io,
                request.response(Some(&projected.legacy), None, None),
            )
            .await
        }
        (CURRENT_VERSION, "getStatus") => {
            let latest = replay.map(|status| Arc::clone(&status.borrow()));
            let current = latest.as_deref().unwrap_or(&projected.current);
            write_frame(&mut io, request.response(Some(current), None, None)).await
        }
        (CURRENT_VERSION, "subscribeStatus") => {
            let Ok(_slot) = subscriptions.try_acquire() else {
                return write_frame(
                    &mut io,
                    request.response(
                        None,
                        Some("Status subscription capacity is unavailable."),
                        Some(0),
                    ),
                )
                .await;
            };
            // Mark the exact initial snapshot seen before writing it. A publication
            // during that write stays pending in this receiver, including on reconnect.
            let mut replay = replay.cloned();
            let latest = replay
                .as_mut()
                .map(|status| Arc::clone(&status.borrow_and_update()));
            let initial = latest.as_deref().unwrap_or(&projected.initial);
            write_frame(&mut io, request.response(Some(initial), None, None)).await?;

            let (mut reader, mut writer) = tokio::io::split(io);
            let mut probe = [0];
            tokio::select! {
                disconnected = reader.read(&mut probe) => disconnected.map(|_| ()),
                streamed = async {
                    if let Some(mut replay) = replay {
                        while replay.changed().await.is_ok() {
                            let latest = Arc::clone(&replay.borrow_and_update());
                            write_status_event(&mut writer, &latest).await?;
                        }
                        return Ok(());
                    }
                    // Retain the original fixed replay for the existing modes.
                    write_status_event(&mut writer, &projected.current).await?;
                    pending::<io::Result<()>>().await
                } => streamed,
            }
        }
        (CURRENT_VERSION, "setBatteryPolicy" | "setBatteryProtection")
            if matches!(session, Session::Active) && status.battery.is_some() =>
        {
            let intent = BatteryRequest::decode(&payload)?;
            let result = status
                .battery
                .as_ref()
                .ok_or_else(|| invalid("missing battery scenario"))?
                .apply(intent)?;
            write_frame(&mut io, request.operation_response(result.into())).await
        }
        (CURRENT_VERSION, "setPerformanceOverride")
            if matches!(session, Session::Active) && status.performance.is_some() =>
        {
            let PerformanceRequest::SetPerformanceOverride { value, .. } =
                serde_json::from_slice(&payload)
                    .map_err(|_| invalid("invalid performance request"))?;
            let result = status
                .performance
                .as_ref()
                .ok_or_else(|| invalid("missing performance scenario"))?
                .apply(value)?;
            let result = OperationResult::scoped(result.status, result.message);
            write_frame(&mut io, request.operation_response(result)).await
        }
        _ => {
            write_frame(
                &mut io,
                request.response(
                    None,
                    Some("This development endpoint only serves cached status."),
                    None,
                ),
            )
            .await
        }
    }
}

async fn write_status_event(
    writer: &mut (impl AsyncWrite + Unpin),
    status: &Value,
) -> io::Result<()> {
    write_frame(
        writer,
        Frame::StatusEvent {
            version: CURRENT_VERSION,
            status,
        },
    )
    .await
}

async fn read_payload(io: &mut (impl AsyncRead + Unpin)) -> io::Result<Vec<u8>> {
    let length = io.read_i32_le().await?;
    if !(1..=MAX_FRAME_BYTES).contains(&length) {
        return Err(invalid("invalid request length"));
    }
    let mut payload = vec![0; length as usize];
    io.read_exact(&mut payload).await?;
    Ok(payload)
}

async fn write_frame(io: &mut (impl AsyncWrite + Unpin), frame: Frame<'_>) -> io::Result<()> {
    let payload = serde_json::to_vec(&frame)?;
    let length = i32::try_from(payload.len())
        .ok()
        .filter(|length| (1..=MAX_FRAME_BYTES).contains(length))
        .ok_or_else(|| invalid("invalid response length"))?;
    timeout(FRAME_TIMEOUT, async {
        io.write_i32_le(length).await?;
        io.write_all(&payload).await?;
        io.flush().await
    })
    .await?
}

fn invalid(message: &'static str) -> io::Error {
    io::Error::new(ErrorKind::InvalidData, message)
}

#[cfg(test)]
mod tests;

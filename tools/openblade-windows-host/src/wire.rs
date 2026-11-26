use std::future::pending;
use std::io;
#[cfg(test)]
use std::io::ErrorKind;
use std::sync::Arc;

use serde::{Deserialize, Serialize};
use serde_json::{Map, Value};
#[cfg(test)]
use tokio::io::AsyncWriteExt;
use tokio::io::{AsyncRead, AsyncReadExt, AsyncWrite};
use tokio::sync::{Semaphore, watch};
use tokio::time::timeout;

use crate::{
    battery_policy::{Intent, Limit},
    battery_runtime::{self, LimitValue, Scenario},
    fan_runtime, performance_runtime,
};
use openblade_domain::recovery::ScopedStatus;

const CURRENT_VERSION: i32 = 91;
const LEGACY_VERSION: i32 = 51;
#[path = "service_frame.rs"]
mod service_frame;
use service_frame::{FRAME_TIMEOUT, invalid, read_payload};

pub(crate) use service_frame::is_guid;

async fn write_frame(io: &mut (impl AsyncWrite + Unpin), frame: Frame<'_>) -> io::Result<()> {
    service_frame::write_frame(io, &frame).await
}

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
    #[cfg(windows)]
    application: Option<Arc<crate::application_runtime::Scenario>>,
    replay: Option<watch::Receiver<Arc<Value>>>,
    battery: Option<Arc<Scenario>>,
    performance: Option<Arc<performance_runtime::Scenario>>,
    fan: Option<Arc<fan_runtime::Scenario>>,
    #[cfg(windows)]
    fan_stop: Option<Arc<crate::fan_stop_fixture::Scenario>>,
    background: Projection,
    #[cfg(windows)]
    composed_thermal: bool,
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
            #[cfg(windows)]
            application: None,
            replay: None,
            battery: None,
            performance: None,
            fan: None,
            #[cfg(windows)]
            fan_stop: None,
            background: Projection::new(background, background_legacy)?,
            #[cfg(windows)]
            composed_thermal: false,
        })
    }

    #[cfg(windows)]
    pub fn with_application_context(
        mut self,
        advance: tokio::sync::mpsc::Receiver<()>,
    ) -> (Self, impl Future<Output = io::Result<()>>) {
        let scenario = Arc::new(crate::application_runtime::Scenario::new(
            self.active.current.clone(),
        ));
        self.replay = Some(scenario.subscribe());
        self.application = Some(Arc::clone(&scenario));
        (self, async move { scenario.drive(advance).await })
    }

    pub fn with_battery(
        self,
        advance: tokio::sync::mpsc::Receiver<()>,
    ) -> io::Result<(Self, impl Future<Output = io::Result<()>>)> {
        let scenario = Arc::new(Scenario::new(self.active.current.clone())?);
        Ok(self.attach_battery(scenario, advance))
    }

    #[cfg(windows)]
    pub fn with_battery_profiles(
        mut self,
        advance: tokio::sync::mpsc::Receiver<()>,
        profiles: Value,
        writer: Option<openblade_windows_host::configuration_writer::fixture::BatteryProfileStore>,
        replay: crate::battery_runtime::ProfileReplay,
        shutdown: tokio::sync::oneshot::Receiver<()>,
        release: Option<tokio::sync::oneshot::Receiver<()>>,
    ) -> io::Result<(Self, impl Future<Output = io::Result<()>>)> {
        use crate::battery_runtime::ProfileReplay;

        if matches!(replay, ProfileReplay::Integrated { .. }) != release.is_some() {
            return Err(io::Error::other(
                "Unexpected integrated port acknowledgement channel.",
            ));
        }
        let scenario = Arc::new(Scenario::composed(
            self.active.current.clone(),
            profiles,
            writer,
            replay,
        )?);
        self.composed_thermal = matches!(
            replay,
            ProfileReplay::Thermal | ProfileReplay::Integrated { .. }
        );
        self.replay = Some(scenario.subscribe());
        // The power script is the only mutation entry point in this mode.
        if !matches!(replay, ProfileReplay::Power { .. }) {
            self.battery = Some(Arc::clone(&scenario));
        }
        let drive = async move {
            if let Some(release) = release {
                return scenario.drive_integrated(advance, shutdown, release).await;
            }
            let replay = async {
                match replay {
                    ProfileReplay::Battery => scenario.drive(advance).await,
                    ProfileReplay::Thermal => scenario.drive_thermal(advance).await,
                    ProfileReplay::FullCharge => scenario.drive_full_charge(advance).await,
                    ProfileReplay::LightingOff { owned, uncertain } => {
                        scenario.drive_lighting(advance, owned, uncertain).await
                    }
                    ProfileReplay::Power { lose_response } => {
                        scenario.drive_power_profile(advance, lose_response).await
                    }
                    ProfileReplay::Integrated { .. } => unreachable!(),
                }
            };
            tokio::select! {
                biased;
                _ = shutdown => Ok(()),
                result = replay => result,
            }
        };
        Ok((self, drive))
    }

    #[cfg(windows)]
    pub async fn take_profile_writer(
        &self,
    ) -> Option<openblade_windows_host::configuration_writer::fixture::BatteryProfileStore> {
        match &self.battery {
            Some(scenario) => scenario.take_profile_writer().await,
            None => None,
        }
    }

    #[cfg(all(windows, test))]
    pub fn set_profile_save_barrier(
        &self,
        barrier: crate::battery_runtime::ProfileSaveBarrier,
    ) -> watch::Receiver<Arc<Value>> {
        let scenario = self.battery.as_ref().unwrap();
        scenario.set_profile_save_barrier(barrier);
        scenario.subscribe()
    }

    fn attach_battery(
        mut self,
        scenario: Arc<Scenario>,
        advance: tokio::sync::mpsc::Receiver<()>,
    ) -> (Self, impl Future<Output = io::Result<()>>) {
        self.replay = Some(scenario.subscribe());
        self.battery = Some(Arc::clone(&scenario));
        let drive = async move { scenario.drive(advance).await };
        (self, drive)
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

    pub fn with_fan(
        mut self,
        advance: tokio::sync::mpsc::Receiver<()>,
    ) -> io::Result<(Self, impl Future<Output = io::Result<()>>)> {
        let scenario = Arc::new(fan_runtime::Scenario::new(self.active.current.clone())?);
        self.replay = Some(scenario.subscribe());
        self.fan = Some(Arc::clone(&scenario));
        let drive = async move { scenario.drive(advance).await };
        Ok((self, drive))
    }

    #[cfg(windows)]
    pub fn with_fan_stop(
        mut self,
        advance: tokio::sync::mpsc::Receiver<()>,
        case: crate::fan_stop_fixture::Case,
    ) -> (Self, impl Future<Output = io::Result<()>>) {
        let scenario = Arc::new(crate::fan_stop_fixture::Scenario::new(
            self.active.current.clone(),
            case,
        ));
        self.replay = Some(scenario.subscribe());
        self.fan_stop = Some(Arc::clone(&scenario));
        (self, async move { scenario.drive(advance).await })
    }

    pub fn with_replay(
        mut self,
        advance: tokio::sync::mpsc::Receiver<()>,
    ) -> (Self, impl Future<Output = io::Result<()>>) {
        let (status, drive) = crate::runtime::start(self.active.current.clone(), advance);
        self.replay = Some(status);
        (self, drive)
    }

    #[cfg(windows)]
    pub fn with_durable(
        mut self,
        percent: u8,
        update: Option<crate::runtime::DurableUpdate>,
        shutdown: tokio::sync::oneshot::Receiver<()>,
    ) -> io::Result<(Self, impl Future<Output = io::Result<()>>)> {
        let (status, drive) =
            crate::runtime::start_durable(self.active.current.clone(), percent, update, shutdown)?;
        self.replay = Some(status);
        Ok((self, drive))
    }

    fn admits_performance(&self) -> bool {
        #[cfg(windows)]
        if self.composed_thermal {
            return true;
        }
        self.performance.is_some()
    }

    fn admits_fan(&self) -> bool {
        #[cfg(windows)]
        if self.composed_thermal {
            return true;
        }
        self.fan.is_some()
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
#[serde(tag = "operation", deny_unknown_fields)]
enum BatteryRequest {
    #[serde(rename = "setBatteryPolicy")]
    Policy {
        #[serde(rename = "version")]
        _version: i32,
        #[serde(rename = "requestId")]
        _request_id: String,
        value: LimitValue,
    },
    #[serde(rename = "setFullChargeOverride")]
    FullCharge {
        #[serde(rename = "version")]
        _version: i32,
        #[serde(rename = "requestId")]
        _request_id: String,
        enabled: bool,
    },
    #[serde(rename = "setBatteryProtection")]
    Protection {
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
            Self::Policy { value, .. } => Limit::new(value.charge_limit_percent)
                .map(Intent::Limit)
                .ok_or_else(|| invalid("invalid battery limit")),
            Self::Protection { enabled, .. } => Ok(Intent::Protection(enabled)),
            Self::FullCharge { enabled, .. } => Ok(Intent::FullCharge(enabled)),
        }
    }
}

#[cfg(windows)]
#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct LightingRequest {
    #[serde(rename = "operation")]
    _operation: String,
    #[serde(rename = "version")]
    _version: i32,
    #[serde(rename = "requestId")]
    _request_id: String,
    #[serde(deserialize_with = "Deserialize::deserialize")]
    value: Option<battery_runtime::lighting::LightingOverride>,
}

#[cfg(windows)]
#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct UpdateProfilesRequest {
    #[serde(rename = "operation")]
    _operation: String,
    #[serde(rename = "version")]
    _version: i32,
    #[serde(rename = "requestId")]
    _request_id: String,
    profiles: Value,
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
#[serde(tag = "operation", rename_all = "camelCase", deny_unknown_fields)]
enum FanRequest {
    SetFanOverride {
        #[serde(rename = "version")]
        _version: i32,
        #[serde(rename = "requestId")]
        _request_id: String,
        #[serde(deserialize_with = "Deserialize::deserialize")]
        value: Option<fan_runtime::Override>,
        settings: Option<fan_runtime::Settings>,
    },
}

#[cfg(windows)]
#[derive(Deserialize)]
#[serde(tag = "operation", rename_all = "camelCase", deny_unknown_fields)]
enum FanStopRequest {
    SetFanStopMode {
        #[serde(rename = "version")]
        _version: i32,
        #[serde(rename = "requestId")]
        _request_id: String,
        mode: u8,
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
                            let snapshot = &*latest;
                            write_status_event(&mut writer, snapshot).await?;
                        }
                        return Ok(());
                    }
                    // Retain the original fixed replay for the existing modes.
                    write_status_event(&mut writer, &projected.current).await?;
                    pending::<io::Result<()>>().await
                } => streamed,
            }
        }
        #[cfg(windows)]
        (CURRENT_VERSION, "reportApplicationContext")
            if status
                .battery
                .as_ref()
                .is_some_and(|scenario| scenario.accepts_application()) =>
        {
            use crate::application_runtime::{INACTIVE, Report};
            let report = Report::decode(&payload)?;
            if !matches!(session, Session::Active) {
                return write_frame(&mut io, request.response(None, Some(INACTIVE), None)).await;
            }
            let scenario = status
                .battery
                .as_ref()
                .expect("guarded application runtime");
            let result = scenario.report_application(&report).await?;
            if result.status == openblade_domain::recovery::ScopedStatus::Blocked {
                return write_frame(&mut io, request.response(None, Some(result.message), None))
                    .await;
            }
            write_frame(
                &mut io,
                request.operation_response(OperationResult {
                    status: 0,
                    message: result.message,
                }),
            )
            .await
        }
        #[cfg(windows)]
        (CURRENT_VERSION, "reportApplicationContext") if status.application.is_some() => {
            use crate::application_runtime::{INACTIVE, Report};
            let report = Report::decode(&payload)?;
            let scenario = status
                .application
                .as_ref()
                .expect("guarded application scenario");
            if !matches!(session, Session::Active) || !scenario.active() {
                return write_frame(&mut io, request.response(None, Some(INACTIVE), None)).await;
            }
            let (status, message) = scenario.report(&report);
            write_frame(
                &mut io,
                request.operation_response(OperationResult { status, message }),
            )
            .await
        }
        #[cfg(windows)]
        (CURRENT_VERSION, "updateProfiles")
            if matches!(session, Session::Active)
                && status
                    .battery
                    .as_ref()
                    .is_some_and(|scenario| scenario.accepts_profiles()) =>
        {
            openblade_windows_host::configuration::validate_json_members(&payload)
                .map_err(|_| invalid("invalid profile request members"))?;
            let update: UpdateProfilesRequest =
                serde_json::from_slice(&payload).map_err(|_| invalid("invalid profile request"))?;
            let limit: LimitValue =
                serde_json::from_value(update.profiles["batteryPolicy"].clone())
                    .map_err(|_| invalid("invalid saved battery policy"))?;
            let limit = Limit::new(limit.charge_limit_percent)
                .ok_or_else(|| invalid("invalid saved battery limit"))?;
            let scenario = Arc::clone(
                status
                    .battery
                    .as_ref()
                    .ok_or_else(|| invalid("missing battery scenario"))?,
            );
            let (status, message) = scenario.save_profiles(update.profiles, limit).await?;
            write_frame(
                &mut io,
                request.operation_response(OperationResult { status, message }),
            )
            .await
        }
        (
            CURRENT_VERSION,
            operation @ ("setBatteryPolicy" | "setBatteryProtection" | "setFullChargeOverride"),
        ) if matches!(session, Session::Active)
            && status.battery.as_ref().is_some_and(|scenario| {
                scenario.accepts_battery(operation == "setFullChargeOverride")
            }) =>
        {
            let intent = BatteryRequest::decode(&payload)?;
            let result = status
                .battery
                .as_ref()
                .ok_or_else(|| invalid("missing battery scenario"))?
                .apply(intent)
                .await?;
            write_frame(&mut io, request.operation_response(result.into())).await
        }
        #[cfg(windows)]
        (CURRENT_VERSION, "setLightingOverride")
            if matches!(session, Session::Active)
                && status
                    .battery
                    .as_ref()
                    .is_some_and(|scenario| scenario.accepts_lighting()) =>
        {
            let lighting: LightingRequest = serde_json::from_slice(&payload)
                .map_err(|_| invalid("invalid lighting request"))?;
            let scenario = status
                .battery
                .as_ref()
                .ok_or_else(|| invalid("missing lighting scenario"))?;
            let mut probe = [0];
            let mut started_retirement = false;
            let result = tokio::select! {
                biased;
                disconnected = io.read(&mut probe) => {
                    match disconnected {
                        Ok(0) => {}
                        Err(error) if error.kind() == io::ErrorKind::BrokenPipe => {}
                        Err(error) => return Err(error),
                        Ok(_) => return Err(invalid("unexpected data while awaiting lighting response")),
                    }
                    // The losing request future and its reservation have
                    // been dropped. The separate retirement task is still live.
                    if started_retirement {
                        scenario.lighting_wait_disconnected().await?;
                    }
                    return Ok(());
                }
                applied = scenario.apply_lighting(lighting.value, &mut started_retirement) => applied?,
            };
            write_frame(&mut io, request.operation_response(result.into())).await
        }
        (CURRENT_VERSION, "setPerformanceOverride")
            if matches!(session, Session::Active) && status.admits_performance() =>
        {
            let PerformanceRequest::SetPerformanceOverride { value, .. } =
                serde_json::from_slice(&payload)
                    .map_err(|_| invalid("invalid performance request"))?;
            #[cfg(windows)]
            if status.composed_thermal {
                let result = status
                    .battery
                    .as_ref()
                    .ok_or_else(|| invalid("missing composed scenario"))?
                    .apply_performance(value)
                    .await?;
                return write_frame(&mut io, request.operation_response(result.into())).await;
            }
            let result = status
                .performance
                .as_ref()
                .ok_or_else(|| invalid("missing performance scenario"))?
                .apply(value)?;
            let result = OperationResult::scoped(result.status, result.message);
            write_frame(&mut io, request.operation_response(result)).await
        }
        #[cfg(windows)]
        (CURRENT_VERSION, "setFanStopMode")
            if matches!(session, Session::Active) && status.fan_stop.is_some() =>
        {
            use crate::fan_stop_fixture::{Mode, Response};
            let FanStopRequest::SetFanStopMode { mode, .. } = serde_json::from_slice(&payload)
                .map_err(|_| invalid("invalid Fan Stop request"))?;
            let mode = match mode {
                0 => Mode::Automatic,
                1 => Mode::Stop,
                _ => return Err(invalid("invalid Fan Stop mode")),
            };
            let scenario = status.fan_stop.as_ref().expect("guarded Fan Stop scenario");
            let (outcome, message) = match scenario.begin(mode)? {
                Response::Immediate(result) => result,
                Response::Pending(waiter) => {
                    let mut probe = [0];
                    tokio::select! {
                        biased;
                        result = waiter => result.map_err(|_| io::Error::other("Fan Stop fixture ended"))?,
                        disconnected = io.read(&mut probe) => {
                            // Any abandoned Stop wait gets independent cleanup;
                            // malformed traffic must not strand the scripted owner.
                            scenario.disconnected(mode)?;
                            match disconnected {
                                Ok(0) => {}
                                Err(error) if error.kind() == io::ErrorKind::BrokenPipe => {}
                                Err(error) => return Err(error),
                                Ok(_) => return Err(invalid("unexpected data while awaiting Fan Stop response")),
                            }
                            return Ok(());
                        }
                    }
                }
            };
            write_frame(
                &mut io,
                request.operation_response(OperationResult::scoped(outcome, message)),
            )
            .await
        }
        #[cfg(windows)]
        (CURRENT_VERSION, "setFanOverride")
            if matches!(session, Session::Active) && status.fan_stop.is_some() =>
        {
            let FanRequest::SetFanOverride {
                value, settings, ..
            } = serde_json::from_slice(&payload).map_err(|_| invalid("invalid fan request"))?;
            let (outcome, message) = status
                .fan_stop
                .as_ref()
                .expect("guarded Fan Stop scenario")
                .ordinary_fan(value, settings)?;
            write_frame(
                &mut io,
                request.operation_response(OperationResult::scoped(outcome, message)),
            )
            .await
        }
        (CURRENT_VERSION, "setFanOverride")
            if matches!(session, Session::Active) && status.admits_fan() =>
        {
            let FanRequest::SetFanOverride {
                value, settings, ..
            } = serde_json::from_slice(&payload).map_err(|_| invalid("invalid fan request"))?;
            #[cfg(windows)]
            if status.composed_thermal {
                let result = status
                    .battery
                    .as_ref()
                    .ok_or_else(|| invalid("missing composed scenario"))?
                    .apply_fans(value, settings)
                    .await?;
                return write_frame(&mut io, request.operation_response(result.into())).await;
            }
            let result = status
                .fan
                .as_ref()
                .ok_or_else(|| invalid("missing fan scenario"))?
                .apply(value, settings)?;
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

#[cfg(test)]
mod tests;

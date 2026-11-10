use std::{
    sync::{
        Arc, Mutex,
        atomic::{AtomicBool, Ordering::SeqCst},
    },
    time::Duration,
};

use openblade_platform_windows::{
    brightness::{
        BrightnessError, BrightnessOutcome, BrightnessRequest, BrightnessSnapshot,
        BrightnessTarget, NativeFailure, WindowsBrightnessWorker,
    },
    display::{DisplayReadError, InternalDisplayRefresh, scripted::Scenario},
};

use crate::{session_brightness::ScriptedBrightness, session_windows::valid_request_id};
use openblade_windows_host::pipe::Identity;
use serde::Deserialize;
use serde_json::{Value, json};
use tokio::{
    io::{AsyncReadExt, AsyncWriteExt},
    net::windows::named_pipe::NamedPipeServer,
    time::timeout,
};

mod color;
use color::ColorDisplay;
pub(super) use color::ColorFixture;
mod hdr;
pub(super) use hdr::HdrFixture;
use hdr::HdrObservation;
mod panel;
use panel::ScriptedDisplay;
mod refresh;
pub(super) use refresh::RefreshFixture;

const VERSION: i32 = 2;
const MAX_FRAME_BYTES: i32 = 65_536;
const FRAME_TIMEOUT: Duration = Duration::from_secs(2);

const SUCCEEDED: i32 = 0;
const BLOCKED: i32 = 1;
const FAILED: i32 = 2;
const INDETERMINATE: i32 = 3;

pub(super) struct SessionDisplay {
    panel: ScriptedDisplay,
    color: ColorDisplay,
    brightness: WindowsBrightnessWorker,
    writes_paused: bool,
}

impl SessionDisplay {
    pub(super) fn scripted(
        scenario: Scenario,
        brightness: WindowsBrightnessWorker,
        hdr: HdrFixture,
        refresh: RefreshFixture,
        color: ColorFixture,
    ) -> Self {
        let mut panel = ScriptedDisplay::new(scenario, hdr, refresh);
        panel.enabled = color == ColorFixture::Disabled;
        Self {
            panel,
            color: ColorDisplay::new(color),
            brightness,
            writes_paused: false,
        }
    }

    fn response(
        &mut self,
        query: Query,
        cancelled: impl Fn() -> bool,
    ) -> Result<(Vec<u8>, bool), Rejected> {
        let context = if matches!(
            query.operation,
            Operation::SetHdr { .. } | Operation::SetRefreshRate { .. }
        ) {
            self.panel.context(false)
        } else {
            None
        };
        let reading = self.panel.read(false);
        let observed_hdr = HdrObservation::from_refresh(&reading);
        let (mut state, target, display_error) = display_state(&reading);
        let (status, error, possible_submission) = match query.operation {
            Operation::Query {} => {
                self.color.project(&reading, &mut state);
                if let Some(target) = target
                    && let Ok(brightness) = self.brightness.read_target(target, &cancelled)
                {
                    set_snapshot(&mut state, brightness);
                }
                let status = if display_error.is_some() {
                    FAILED
                } else {
                    SUCCEEDED
                };
                (status, display_error, false)
            }
            Operation::SetRefreshRate {
                expected_power_source,
                refresh_rate_key,
            } => {
                if self.writes_paused {
                    (
                        BLOCKED,
                        Some(
                            "Refresh-rate writes are paused after an indeterminate submission."
                                .into(),
                        ),
                        false,
                    )
                } else if let Some(error) = display_error {
                    (FAILED, Some(error), false)
                } else {
                    let result = self.panel.apply_refresh(
                        (expected_power_source, &refresh_rate_key),
                        context,
                        &reading,
                        &mut state,
                        &cancelled,
                    );
                    self.writes_paused = result.0 == INDETERMINATE;
                    if result.0 == SUCCEEDED && self.color.enabled() {
                        self.color.project(&self.panel.read(false), &mut state);
                    }
                    result
                }
            }
            Operation::SetHdr {
                expected_power_source,
                enabled,
            } => {
                if self.writes_paused {
                    (
                        BLOCKED,
                        Some("HDR writes are paused after an indeterminate submission.".into()),
                        false,
                    )
                } else {
                    let result = self.panel.apply_hdr(
                        expected_power_source,
                        enabled,
                        context,
                        observed_hdr.ok_or_else(|| {
                            display_error.unwrap_or_else(|| {
                                "The internal display is unavailable; HDR was not submitted.".into()
                            })
                        }),
                        &mut state,
                        &cancelled,
                    );
                    self.writes_paused = result.0 == INDETERMINATE;
                    if result.0 == SUCCEEDED && self.color.enabled() {
                        self.color.project(&self.panel.read(false), &mut state);
                    }
                    result
                }
            }
            Operation::SetColorProfile {
                expected_power_source,
                color_profile_key,
            } => {
                if self.writes_paused {
                    (
                        BLOCKED,
                        Some("ICC writes are paused after an indeterminate submission.".into()),
                        false,
                    )
                } else {
                    let result = self.color.apply(
                        (expected_power_source, &color_profile_key),
                        &self.panel,
                        &reading,
                        &mut state,
                        &cancelled,
                    );
                    self.writes_paused = result.0 == INDETERMINATE;
                    result
                }
            }
            Operation::SetBrightness {
                expected_power_source,
                percent,
            } => {
                if self.writes_paused {
                    (
                        BLOCKED,
                        Some(
                            "Brightness writes are paused after an indeterminate submission."
                                .into(),
                        ),
                        false,
                    )
                } else if expected_power_source != ScriptedBrightness::POWER_SOURCE {
                    (BLOCKED, Some("The expected power source does not match the scripted session; brightness was not submitted.".into()), false)
                } else if let Some(target) = target {
                    self.color.project(&reading, &mut state);
                    match self.brightness.apply_target(
                        BrightnessRequest::SetPercent(percent),
                        target,
                        &cancelled,
                    ) {
                        Ok(BrightnessOutcome::Unchanged(snapshot)) => {
                            set_snapshot(&mut state, snapshot);
                            state["message"] =
                                json!("Brightness was already at the selected level.");
                            (SUCCEEDED, None, false)
                        }
                        Ok(BrightnessOutcome::Confirmed(snapshot)) => {
                            set_snapshot(&mut state, snapshot);
                            (SUCCEEDED, None, true)
                        }
                        Err(failure) if failure.submission_may_have_occurred => {
                            // A later query may observe brightness, but cannot
                            // authorize another write in this private agent.
                            self.writes_paused = true;
                            if let Some(snapshot) = failure.state {
                                set_snapshot(&mut state, snapshot);
                            }
                            (
                                INDETERMINATE,
                                Some(format!(
                                    "Brightness submission is indeterminate ({:?}); writes are paused.",
                                    failure.reason
                                )),
                                true,
                            )
                        }
                        Err(failure) => {
                            let error = if failure.reason
                                == BrightnessError::Native(NativeFailure::Cancelled)
                            {
                                "Brightness was cancelled before submission.".into()
                            } else {
                                format!("Brightness was not submitted ({:?}).", failure.reason)
                            };
                            (FAILED, Some(error), false)
                        }
                    }
                } else {
                    (
                        FAILED,
                        Some(display_error.unwrap_or_else(|| {
                            "Brightness was not submitted; the internal display is unavailable."
                                .into()
                        })),
                        false,
                    )
                }
            }
        };
        let frame = encode(&query.request_id, status, error, state);
        if frame.is_err() && possible_submission {
            self.writes_paused = true;
        }
        Ok((frame?, possible_submission))
    }
}

fn set_snapshot(state: &mut serde_json::Value, brightness: BrightnessSnapshot) {
    state["brightnessPercent"] = json!(brightness.percent);
    state["supportedBrightnessLevels"] = json!(brightness.supported_levels);
}

fn display_state(
    reading: &Result<InternalDisplayRefresh, DisplayReadError>,
) -> (serde_json::Value, Option<BrightnessTarget>, Option<String>) {
    let mut state = json!({
        "isAvailable": false,
        "displayName": null,
        "width": 0,
        "height": 0,
        "currentRefreshKey": null,
        "refreshOptions": [],
        "hdrSupported": false,
        "hdrEnabled": false,
        "colorProfilesEnabled": false,
        "activeColorProfileId": null,
        "colorProfiles": [],
        "message": null,
        "bitsPerColorChannel": null,
        "brightnessPercent": null,
        "supportedBrightnessLevels": [],
    });
    let mut target = None;
    let error = match reading {
        Ok(InternalDisplayRefresh::Single(snapshot)) => {
            let brightness_target = BrightnessTarget::from_display(&snapshot.display).ok();
            match (
                &snapshot.display.monitor_name,
                &snapshot.display.advanced_color,
            ) {
                (Ok(name), Ok(hdr)) => {
                    target = brightness_target;
                    state["isAvailable"] = json!(true);
                    state["displayName"] = json!(
                        name.friendly_name
                            .as_deref()
                            .unwrap_or(&snapshot.display.source_name)
                            .to_string_lossy()
                    );
                    state["width"] = json!(snapshot.width);
                    state["height"] = json!(snapshot.height);
                    state["currentRefreshKey"] = json!(snapshot.current_refresh_key);
                    state["refreshOptions"] = json!(
                        snapshot
                            .refresh_options
                            .iter()
                            .map(|option| json!({
                                "key": option.key,
                                "name": option.name,
                                "minimumHertz": option.minimum_hertz,
                                "maximumHertz": option.maximum_hertz,
                                "desktopHertz": option.desktop_hertz,
                                "isDynamic": option.is_dynamic,
                            }))
                            .collect::<Vec<_>>()
                    );
                    state["hdrSupported"] = json!(hdr.hdr_supported);
                    state["hdrEnabled"] = json!(hdr.hdr_user_enabled);
                    state["bitsPerColorChannel"] = json!(hdr.bits_per_color_channel);
                    state["message"] = json!(snapshot.message);
                    None
                }
                (Err(status), _) => {
                    Some(format!("Target name read failed: Windows status {status}."))
                }
                (_, Err(error)) => Some(format!("HDR read failed: {error:?}.")),
            }
        }
        Ok(unavailable) => {
            let message = match unavailable {
                InternalDisplayRefresh::Empty => "No active display topology is available.",
                InternalDisplayRefresh::NoInternalDisplay => {
                    "No active internal display was found."
                }
                InternalDisplayRefresh::MultipleInternalDisplays => {
                    "Multiple active internal displays were found."
                }
                InternalDisplayRefresh::Single(_) => unreachable!(),
            };
            state["message"] = json!(message);
            None
        }
        Err(error) => Some(format!("Display read failed: {error:?}.")),
    };

    (state, target, error)
}

fn encode(
    request_id: &str,
    status: i32,
    error: Option<String>,
    state: serde_json::Value,
) -> Result<Vec<u8>, Rejected> {
    let body = serde_json::to_vec(&json!({
        "version": VERSION,
        "requestId": request_id,
        "status": status,
        "error": error,
        "state": state,
    }))
    .map_err(|_| Rejected::InvalidResponse)?;
    if !(1..=MAX_FRAME_BYTES as usize).contains(&body.len()) {
        return Err(Rejected::InvalidResponse);
    }
    let mut frame = Vec::with_capacity(4 + body.len());
    frame.extend_from_slice(&(body.len() as i32).to_le_bytes());
    frame.extend_from_slice(&body);
    Ok(frame)
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct Query {
    version: i32,
    request_id: String,
    #[serde(flatten)]
    operation: Operation,
}

#[derive(Debug, Deserialize)]
#[serde(
    tag = "operation",
    rename_all = "camelCase",
    rename_all_fields = "camelCase",
    deny_unknown_fields
)]
enum Operation {
    Query {},
    SetRefreshRate {
        expected_power_source: u8,
        refresh_rate_key: String,
    },
    SetHdr {
        expected_power_source: u8,
        enabled: bool,
    },
    SetColorProfile {
        expected_power_source: u8,
        color_profile_key: String,
    },
    SetBrightness {
        expected_power_source: u8,
        percent: u8,
    },
}

impl Query {
    fn parse(body: &[u8]) -> Result<Self, Rejected> {
        let query: Self = serde_json::from_slice(body).map_err(|_| Rejected::InvalidRequest)?;
        let valid = match query.operation {
            Operation::Query {} => true,
            Operation::SetRefreshRate {
                expected_power_source,
                ref refresh_rate_key,
            } => expected_power_source <= 2 && valid_key(refresh_rate_key),
            Operation::SetHdr {
                expected_power_source,
                ..
            } => expected_power_source <= 2,
            Operation::SetColorProfile {
                expected_power_source,
                ref color_profile_key,
            } => expected_power_source <= 2 && valid_key(color_profile_key),
            Operation::SetBrightness {
                expected_power_source,
                percent,
            } => expected_power_source <= 2 && percent <= 100,
        };
        if !valid {
            return Err(Rejected::InvalidRequest);
        }
        if query.version != VERSION {
            return Err(Rejected::UnsupportedVersion);
        }
        if !valid_request_id(&query.request_id) {
            return Err(Rejected::InvalidRequest);
        }
        Ok(query)
    }
}

fn valid_key(key: &str) -> bool {
    !key.is_empty()
        && key.encode_utf16().take(1025).count() <= 1024
        && key.trim() == key
        && !key.chars().any(char::is_control)
}

#[derive(Debug, PartialEq, Eq)]
pub(super) enum Rejected {
    ReadDeadline,
    ReadFailed,
    InvalidLength,
    Unauthorized,
    InvalidRequest,
    UnsupportedVersion,
    InvalidResponse,
    WorkerFailed,
    WriteDeadline,
    WriteFailed,
}

pub(super) async fn serve(
    pipe: &mut NamedPipeServer,
    identity: &Identity,
    session: &Arc<Mutex<SessionDisplay>>,
    stopping: &Arc<AtomicBool>,
) -> Result<(), Rejected> {
    let body = timeout(FRAME_TIMEOUT, async {
        let length = pipe.read_i32_le().await.map_err(|_| Rejected::ReadFailed)?;
        if !(1..=MAX_FRAME_BYTES).contains(&length) {
            return Err(Rejected::InvalidLength);
        }
        let mut body = vec![0; length as usize];
        pipe.read_exact(&mut body)
            .await
            .map_err(|_| Rejected::ReadFailed)?;
        Ok(body)
    })
    .await
    .map_err(|_| Rejected::ReadDeadline)??;

    // Impersonate only to inspect the token for the message just read.
    identity
        .authorize(pipe)
        .map_err(|_| Rejected::Unauthorized)?;
    let query = Query::parse(&body)?;
    let cancelled = Arc::new(AtomicBool::new(false));
    let mut task = tokio::task::spawn_blocking({
        let session = Arc::clone(session);
        let stopping = Arc::clone(stopping);
        let cancelled = Arc::clone(&cancelled);
        move || {
            session
                .lock()
                .map_err(|_| Rejected::WorkerFailed)?
                .response(query, || stopping.load(SeqCst) || cancelled.load(SeqCst))
        }
    });

    // A connection is one request. EOF, a broken pipe, or another byte while
    // the job is running cancels admission, never an already submitted write.
    let mut extra = [0];
    let (result, disconnected) = tokio::select! {
        biased;
        _ = pipe.read(&mut extra) => {
            cancelled.store(true, SeqCst);
            (task.await, true)
        }
        result = &mut task => (result, false),
    };
    let (frame, possible_submission) = result.map_err(|_| Rejected::WorkerFailed)??;
    let delivery = if disconnected {
        Err(Rejected::WriteFailed)
    } else {
        match timeout(FRAME_TIMEOUT, pipe.write_all(&frame)).await {
            Ok(result) => result.map_err(|_| Rejected::WriteFailed),
            Err(_) => Err(Rejected::WriteDeadline),
        }
    };
    if delivery.is_err() && possible_submission {
        session
            .lock()
            .map_err(|_| Rejected::WorkerFailed)?
            .writes_paused = true;
    }
    delivery
}

#[cfg(test)]
mod tests {
    use super::*;

    const REPLAY: &str =
        include_str!("../../../tests/OpenBlade.Session.Tests/WindowsReplay/v1/session.json");

    #[test]
    fn only_query_and_typed_display_setters_are_admitted_from_the_display_replay() {
        let replay: Value = serde_json::from_str(REPLAY).unwrap();
        let display = &replay["families"]["display"];
        assert_eq!(display["version"], VERSION);
        assert_eq!(display["maxMessageBytes"], MAX_FRAME_BYTES);
        for vector in display["requests"].as_array().unwrap() {
            if let Some(input) = vector.get("input") {
                let body = serde_json::to_vec(input).unwrap();
                let result = Query::parse(&body);
                match vector["id"].as_str().unwrap() {
                    "query" | "setRefreshRate" | "setHdr" | "setColorProfile" | "setBrightness" => {
                        assert_eq!(result.unwrap().request_id, input["requestId"]);
                    }
                    "old-version" => {
                        assert_eq!(result.unwrap_err(), Rejected::UnsupportedVersion);
                    }
                    _ => assert_eq!(result.unwrap_err(), Rejected::InvalidRequest),
                }
            }
        }
    }

    #[test]
    fn rejects_malformed_envelopes_and_operation_fields() {
        for body in [
            r#"{"operation":"setColorProfile","version":2,"requestId":"11111111-2222-4333-8444-555555555555","expectedPowerSource":0}"#,
            r#"{"operation":"setColorProfile","version":2,"requestId":"11111111-2222-4333-8444-555555555555","expectedPowerSource":3,"colorProfileKey":"Native.icm"}"#,
            r#"{"operation":"setColorProfile","version":2,"requestId":"11111111-2222-4333-8444-555555555555","expectedPowerSource":0,"colorProfileKey":" "}"#,
            r#"{"operation":"setColorProfile","version":2,"requestId":"11111111-2222-4333-8444-555555555555","expectedPowerSource":0,"colorProfileKey":"Native.icm","scope":1}"#,
            r#"{"operation":"setRefreshRate","version":2,"requestId":"11111111-2222-4333-8444-555555555555","expectedPowerSource":0}"#,
            r#"{"operation":"setRefreshRate","version":2,"requestId":"11111111-2222-4333-8444-555555555555","expectedPowerSource":0,"refreshRateKey":" fixed-60"}"#,
            r#"{"operation":"setRefreshRate","version":2,"requestId":"11111111-2222-4333-8444-555555555555","expectedPowerSource":0,"refreshRateKey":"fixed-\u000060"}"#,
            r#"{"operation":"setRefreshRate","version":2,"requestId":"11111111-2222-4333-8444-555555555555","expectedPowerSource":3,"refreshRateKey":"fixed-60"}"#,
            r#"{"operation":"setRefreshRate","version":2,"requestId":"11111111-2222-4333-8444-555555555555","expectedPowerSource":0,"refreshRateKey":"fixed-60","targetId":4}"#,
            r#"{"operation":"setHdr","version":2,"requestId":"11111111-2222-4333-8444-555555555555","enabled":true,"expectedPowerSource":3}"#,
            r#"{"operation":"setHdr","version":2,"requestId":"11111111-2222-4333-8444-555555555555","enabled":1,"expectedPowerSource":0}"#,
            r#"{"operation":"setHdr","version":2,"requestId":"11111111-2222-4333-8444-555555555555","expectedPowerSource":0}"#,
            r#"{"operation":"setHdr","version":2,"requestId":"11111111-2222-4333-8444-555555555555","enabled":false,"expectedPowerSource":0,"targetId":4}"#,
            r#"{"operation":"query","version":2,"version":2,"requestId":"11111111-2222-4333-8444-555555555555"}"#,
            r#"{"operation":"query","version":2,"requestId":"00000000-0000-0000-0000-000000000000"}"#,
            r#"{"operation":"query","version":2,"requestId":"11111111-2222-4333-8444-555555555555","percent":20}"#,
            r#"{"operation":"setBrightness","version":2,"requestId":"11111111-2222-4333-8444-555555555555","percent":101,"expectedPowerSource":0}"#,
            r#"{"operation":"setBrightness","version":2,"requestId":"11111111-2222-4333-8444-555555555555","percent":20,"expectedPowerSource":3}"#,
            r#"{"operation":"setBrightness","version":2,"requestId":"11111111-2222-4333-8444-555555555555","percent":20}"#,
            r#"{"operation":"setBrightness","version":2,"requestId":"11111111-2222-4333-8444-555555555555","percent":20,"percent":30,"expectedPowerSource":0}"#,
        ] {
            assert_eq!(
                Query::parse(body.as_bytes()).unwrap_err(),
                Rejected::InvalidRequest
            );
        }
    }
}

//! Private replay of the managed battery report endpoint.
use std::{future::pending, io, time::Duration};

use openblade_windows_host::pipe::Identity;
use serde::{
    Deserialize, Serialize,
    de::{Error, MapAccess, Visitor},
};
use tokio::{
    io::{AsyncRead, AsyncReadExt, AsyncWriteExt},
    net::windows::named_pipe::NamedPipeServer,
    time::timeout,
};

use crate::session_windows::{read_frame_body, valid_request_id};

mod report;
mod timestamp;
mod xml;

const VERSION: i32 = 1;
const MAX_FRAME: i32 = 256 * 1024;
const FRAME_TIMEOUT: Duration = Duration::from_secs(2);
const DISPATCH_TIMEOUT: Duration = Duration::from_secs(20);
const UNAVAILABLE: &str = "Windows could not provide a battery report.";
const FAILED: &str = "The Session Agent could not read the Windows battery report.";
const TIMELINE: &[u8] = include_bytes!("session_battery/timeline.xml");

#[derive(Clone, Copy)]
pub(super) enum Scenario {
    Available,
    Missing,
    Malformed,
    Oversized,
    Failed,
    Stalled,
}

impl Scenario {
    pub(super) fn parse(value: &str) -> Option<Self> {
        match value {
            "available" => Some(Self::Available),
            "missing" => Some(Self::Missing),
            "malformed" => Some(Self::Malformed),
            "oversized" => Some(Self::Oversized),
            "failed" => Some(Self::Failed),
            "stalled" => Some(Self::Stalled),
            _ => None,
        }
    }

    async fn read(self) -> Result<Option<report::Report>, ()> {
        match self {
            Self::Available => Ok(report::parse(TIMELINE)),
            Self::Missing => Ok(None),
            Self::Malformed => Ok(report::parse(b"<BatteryReport>")),
            Self::Oversized => Ok(report::parse(&vec![b' '; xml::MAX_BYTES + 1])),
            Self::Failed => Err(()),
            Self::Stalled => pending().await,
        }
    }
}

struct Request {
    request_id: String,
}

#[derive(Deserialize)]
#[serde(untagged)]
enum Version {
    Number(i32),
    Text(String),
}

impl<'de> Deserialize<'de> for Request {
    fn deserialize<D: serde::Deserializer<'de>>(deserializer: D) -> Result<Self, D::Error> {
        struct RequestVisitor;

        impl<'de> Visitor<'de> for RequestVisitor {
            type Value = Request;

            fn expecting(&self, out: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
                out.write_str("a typed Session Battery request")
            }

            fn visit_map<M: MapAccess<'de>>(self, mut map: M) -> Result<Request, M::Error> {
                let mut version = None;
                let mut request_id = None;
                // Web JSON recognizes known property names case-insensitively,
                // and the managed codec rejects duplicates before binding.
                while let Some(key) = map.next_key::<String>()? {
                    if key.eq_ignore_ascii_case("version") && version.is_none() {
                        version = Some(map.next_value::<Version>()?);
                    } else if key.eq_ignore_ascii_case("requestId") && request_id.is_none() {
                        request_id = Some(map.next_value::<String>()?);
                    } else {
                        return Err(M::Error::custom("unexpected Session Battery member"));
                    }
                }
                let version = match version {
                    Some(Version::Number(value)) => Some(value),
                    Some(Version::Text(value)) => value.parse::<i32>().ok(),
                    None => None,
                };
                let request_id = request_id.filter(|id| valid_request_id(id));
                match (version, request_id) {
                    (Some(VERSION), Some(request_id)) => Ok(Request { request_id }),
                    _ => Err(M::Error::custom("invalid Session Battery envelope")),
                }
            }
        }

        deserializer.deserialize_map(RequestVisitor)
    }
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct Response<'a> {
    version: i32,
    request_id: &'a str,
    status: u8,
    error: Option<&'static str>,
    report: Option<report::Report>,
}

impl<'a> Response<'a> {
    fn from_result(request_id: &'a str, result: Result<Option<report::Report>, ()>) -> Self {
        let (status, error, report) = match result {
            Ok(Some(report)) => (0, None, Some(report)),
            Ok(None) => (1, Some(UNAVAILABLE), None),
            Err(()) => (2, Some(FAILED), None),
        };
        Self {
            version: VERSION,
            request_id,
            status,
            error,
            report,
        }
    }

    fn frame(&self) -> io::Result<Vec<u8>> {
        if self
            .report
            .as_ref()
            .is_some_and(|report| !report.valid_on_wire())
        {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                "invalid Session Battery report",
            ));
        }
        let body = serde_json::to_vec(self)
            .map_err(|error| io::Error::new(io::ErrorKind::InvalidData, error))?;
        if body.is_empty() || body.len() > MAX_FRAME as usize {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                "invalid Session Battery response length",
            ));
        }
        let mut frame = Vec::with_capacity(4 + body.len());
        frame.extend((body.len() as i32).to_le_bytes());
        frame.extend(body);
        Ok(frame)
    }
}

pub(super) async fn serve(
    pipe: &mut NamedPipeServer,
    identity: &Identity,
    scenario: Scenario,
) -> io::Result<()> {
    let body = timeout(FRAME_TIMEOUT, read_frame_body(pipe, MAX_FRAME)).await??;
    // The read supplies the message token. Dispatch only after current-user
    // and session admission via the shared pipe owner.
    identity.authorize(pipe)?;
    let request: Request = serde_json::from_slice(&body)
        .map_err(|error| io::Error::new(io::ErrorKind::InvalidData, error))?;
    let Some(result) = dispatch(pipe, scenario).await else {
        return Ok(());
    };
    let frame = Response::from_result(&request.request_id, result).frame()?;
    timeout(FRAME_TIMEOUT, pipe.write_all(&frame)).await??;
    Ok(())
}

async fn dispatch(
    pipe: &mut (impl AsyncRead + Unpin),
    scenario: Scenario,
) -> Option<Result<Option<report::Report>, ()>> {
    let mut extra = [0];
    // Disconnect, unexpected extra bytes, and deadline all retire the
    // connection without a reply. The in-memory provider can be dropped.
    tokio::select! {
        biased;
        _ = pipe.read(&mut extra) => None,
        result = timeout(DISPATCH_TIMEOUT, scenario.read()) => result.ok(),
    }
}

#[cfg(test)]
mod tests;

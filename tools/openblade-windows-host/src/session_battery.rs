//! Private replay of the managed battery report endpoint.
use std::{future::pending, io, time::Duration};

use openblade_windows_host::json_object;
use openblade_windows_host::pipe::{Connection as NamedPipeServer, Identity};
use tokio::{
    io::{AsyncRead, AsyncReadExt, AsyncWriteExt},
    time::timeout,
};

mod wire;
#[cfg(test)]
use wire::{FAILED, UNAVAILABLE};
use wire::{MAX_FRAME, Request, Response, read_frame_body};

// Keep the acquisition seam compiled and disconnected from the private replay.
#[cfg_attr(not(test), allow(dead_code))]
mod acquisition;
mod report;
mod timestamp;
mod xml;

const FRAME_TIMEOUT: Duration = Duration::from_secs(2);
const DISPATCH_TIMEOUT: Duration = Duration::from_secs(20);
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

pub(super) async fn serve(
    pipe: &mut NamedPipeServer,
    identity: &Identity,
    scenario: Scenario,
) -> io::Result<()> {
    let body = timeout(FRAME_TIMEOUT, read_frame_body(pipe, MAX_FRAME)).await??;
    // The read supplies the message token. Dispatch only after current-user
    // and session admission via the shared pipe owner.
    identity.authorize(pipe)?;
    let request: Request = json_object::public_tokens(&body)
        .and_then(|()| serde_json::from_slice(&body))
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

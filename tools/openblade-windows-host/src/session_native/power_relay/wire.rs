//! The managed v91 `reportSessionPowerTransition` request and its reply.
//! Encoding refuses exactly the bodies `IpcMessageCodec` refuses, before any
//! byte is written, and writes the members in managed order.
use std::io;

use openblade_application_reporter::Guid;
use openblade_windows_host::reporter_pipe::{Response, guid_text};
use serde::Serialize;

use super::Transition;
use crate::service_status::PROTOCOL_VERSION;

/// One request: a transition of one relay instance under a fresh request id.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(super) struct Request {
    pub(super) request_id: [u8; 16],
    pub(super) instance: [u8; 16],
    pub(super) started: i64,
    pub(super) transition: Transition,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct Wire {
    operation: &'static str,
    instance_id: String,
    instance_started_qpc: i64,
    sequence: i64,
    timestamp_qpc: i64,
    suspending: bool,
    version: i32,
    request_id: String,
}

fn invalid() -> io::Error {
    io::Error::new(
        io::ErrorKind::InvalidData,
        "invalid session power transition request",
    )
}

impl Request {
    pub(super) fn request_id_text(&self) -> String {
        guid_text(Guid(self.request_id))
    }

    /// The payload managed writes after the length prefix. Managed
    /// `IpcMessageCodec` refuses an empty instance or request id, a start at
    /// or before zero, a sequence at or before zero, and a timestamp before
    /// the start.
    pub(super) fn encode(&self) -> io::Result<Vec<u8>> {
        let Transition {
            sequence,
            timestamp,
            suspending,
        } = self.transition;
        if self.instance == [0; 16]
            || self.request_id == [0; 16]
            || self.started <= 0
            || sequence <= 0
            || timestamp < self.started
        {
            return Err(invalid());
        }
        Ok(serde_json::to_vec(&Wire {
            operation: "reportSessionPowerTransition",
            instance_id: guid_text(Guid(self.instance)),
            instance_started_qpc: self.started,
            sequence,
            timestamp_qpc: timestamp,
            suspending,
            version: PROTOCOL_VERSION,
            request_id: self.request_id_text(),
        })?)
    }
}

/// The Service's answer to one request: the `ControlOperationStatus` of a
/// result, or `None` for a refusal. Anything else, including a reply for
/// another request, is invalid.
pub(super) fn decode_reply(reply: &[u8], request_id: &str) -> io::Result<Option<u8>> {
    let response = Response::decode(reply, request_id)?;
    if response.status.is_some() {
        return Err(invalid());
    }
    match (response.success, response.result, response.error) {
        (true, Some(result), None) if response.error_code.is_none() && result.status <= 3 => {
            Ok(Some(result.status))
        }
        (false, None, Some(error)) if !error.trim().is_empty() => Ok(None),
        _ => Err(invalid()),
    }
}

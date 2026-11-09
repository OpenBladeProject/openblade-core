use std::{io, time::Duration};

use openblade_windows_host::pipe::Identity;
use serde::Deserialize;
use serde_json::Value;
use tokio::{
    io::{AsyncReadExt, AsyncWriteExt},
    net::windows::named_pipe::NamedPipeServer,
    time::timeout,
};

const VERSION: i32 = 2;
const MAX_FRAME_BYTES: i32 = 65_536;
const FRAME_TIMEOUT: Duration = Duration::from_secs(2);
const FIXTURES: &str = include_str!(
    "../../../tests/OpenBlade.Session.Tests/WindowsReplay/v1/session-display-query.json"
);

pub(super) struct DisplayFixture(Value);

impl DisplayFixture {
    pub(super) fn load(scenario: &str) -> io::Result<Self> {
        if !matches!(scenario, "available" | "unavailable") {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "expected an available or unavailable display fixture",
            ));
        }
        let mut fixtures: Value = serde_json::from_str(FIXTURES)?;
        let response = fixtures[scenario].take();
        if response["version"] != VERSION
            || response["status"] != 0
            || response.get("error") != Some(&Value::Null)
            || response["state"]["isAvailable"] != (scenario == "available")
        {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                "embedded Session Display response is invalid",
            ));
        }
        Ok(Self(response))
    }

    fn response(&self, request: Query) -> Result<Vec<u8>, Rejected> {
        let mut response = self.0.clone();
        response["requestId"] = Value::String(request.request_id);
        let body = serde_json::to_vec(&response).map_err(|_| Rejected::InvalidFixture)?;
        if !(1..=MAX_FRAME_BYTES as usize).contains(&body.len()) {
            return Err(Rejected::InvalidFixture);
        }
        let mut frame = Vec::with_capacity(4 + body.len());
        frame.extend_from_slice(&(body.len() as i32).to_le_bytes());
        frame.extend_from_slice(&body);
        Ok(frame)
    }
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Query {
    operation: Operation,
    version: i32,
    request_id: String,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
enum Operation {
    Query,
}

impl Query {
    fn parse(body: &[u8]) -> Result<Self, Rejected> {
        let query: Self = serde_json::from_slice(body).map_err(|_| Rejected::InvalidRequest)?;
        let Operation::Query = query.operation;
        if query.version != VERSION {
            return Err(Rejected::UnsupportedVersion);
        }
        if !valid_request_id(&query.request_id) {
            return Err(Rejected::InvalidRequest);
        }
        Ok(query)
    }
}

fn valid_request_id(id: &str) -> bool {
    id.len() == 36
        && id.bytes().enumerate().all(|(position, byte)| {
            if matches!(position, 8 | 13 | 18 | 23) {
                byte == b'-'
            } else {
                byte.is_ascii_hexdigit()
            }
        })
        && id != "00000000-0000-0000-0000-000000000000"
}

#[derive(Debug, PartialEq, Eq)]
pub(super) enum Rejected {
    ReadDeadline,
    ReadFailed,
    InvalidLength,
    Unauthorized,
    InvalidRequest,
    UnsupportedVersion,
    InvalidFixture,
    WriteDeadline,
    WriteFailed,
}

pub(super) async fn serve(
    pipe: &mut NamedPipeServer,
    identity: &Identity,
    fixture: &DisplayFixture,
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
    let frame = fixture.response(Query::parse(&body)?)?;
    timeout(FRAME_TIMEOUT, pipe.write_all(&frame))
        .await
        .map_err(|_| Rejected::WriteDeadline)?
        .map_err(|_| Rejected::WriteFailed)
}

#[cfg(test)]
mod tests {
    use super::*;

    const REPLAY: &str =
        include_str!("../../../tests/OpenBlade.Session.Tests/WindowsReplay/v1/session.json");

    #[test]
    fn only_canonical_query_is_admitted_from_the_display_replay() {
        let replay: Value = serde_json::from_str(REPLAY).unwrap();
        let display = &replay["families"]["display"];
        assert_eq!(display["version"], VERSION);
        assert_eq!(display["maxMessageBytes"], MAX_FRAME_BYTES);
        for vector in display["requests"].as_array().unwrap() {
            if let Some(input) = vector.get("input") {
                let body = serde_json::to_vec(input).unwrap();
                let result = Query::parse(&body);
                match vector["id"].as_str().unwrap() {
                    "query" => assert_eq!(result.unwrap().request_id, input["requestId"]),
                    "old-version" => {
                        assert_eq!(result.unwrap_err(), Rejected::UnsupportedVersion);
                    }
                    _ => assert_eq!(result.unwrap_err(), Rejected::InvalidRequest),
                }
            }
        }
    }

    #[test]
    fn query_rejects_duplicate_fields_and_empty_correlation() {
        for body in [
            r#"{"operation":"query","version":2,"version":2,"requestId":"11111111-2222-4333-8444-555555555555"}"#,
            r#"{"operation":"query","version":2,"requestId":"00000000-0000-0000-0000-000000000000"}"#,
        ] {
            assert_eq!(
                Query::parse(body.as_bytes()).unwrap_err(),
                Rejected::InvalidRequest
            );
        }
    }
}

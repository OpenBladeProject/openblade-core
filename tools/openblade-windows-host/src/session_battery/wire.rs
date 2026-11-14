//! Shared Battery.v1 wire contract for replay and the selected native endpoint.
use super::report;
use serde::{
    Deserialize, Serialize,
    de::{Error, MapAccess, Visitor},
};
use std::io;
pub(super) const VERSION: i32 = 1;
pub(super) const MAX_FRAME: i32 = 256 * 1024;
pub(super) const UNAVAILABLE: &str = "Windows could not provide a battery report.";
pub(super) const FAILED: &str = "The Session Agent could not read the Windows battery report.";

pub(super) struct Request {
    pub(super) request_id: String,
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
pub(super) struct Response<'a> {
    version: i32,
    request_id: &'a str,
    status: u8,
    error: Option<&'static str>,
    report: Option<report::Report>,
}

impl<'a> Response<'a> {
    pub(super) fn from_result(
        request_id: &'a str,
        result: Result<Option<report::Report>, ()>,
    ) -> Self {
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

    pub(super) fn frame(&self) -> io::Result<Vec<u8>> {
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

pub(super) async fn read_frame_body(
    pipe: &mut (impl tokio::io::AsyncRead + Unpin),
    limit: i32,
) -> io::Result<Vec<u8>> {
    use tokio::io::AsyncReadExt;
    let length = pipe.read_i32_le().await?;
    if !(1..=limit).contains(&length) {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "invalid Session request length",
        ));
    }
    let mut body = vec![0; length as usize];
    pipe.read_exact(&mut body).await?;
    Ok(body)
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

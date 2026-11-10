//! Private development transport for the existing v79 application reporter.

use std::io::{self, ErrorKind};

use openblade_application_reporter::{Guid, ReportApplicationContextRequest, ReportOutcome};
use serde::{Deserialize, Serialize};
use serde_json::Value;
use tokio::{
    io::{AsyncReadExt, AsyncWriteExt},
    net::windows::named_pipe::NamedPipeClient,
};

pub const VERSION: i32 = 79;
const MAX_FRAME: i32 = 65_536;

fn invalid() -> io::Error {
    io::Error::new(
        ErrorKind::InvalidData,
        "invalid private v79 application frame",
    )
}

pub fn guid_text(Guid(bytes): Guid) -> String {
    let hex = format!("{:032x}", u128::from_be_bytes(bytes));
    format!(
        "{}-{}-{}-{}-{}",
        &hex[..8],
        &hex[8..12],
        &hex[12..16],
        &hex[16..20],
        &hex[20..],
    )
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct ReportWire<'a> {
    operation: &'static str,
    version: i32,
    request_id: String,
    instance_id: String,
    sequence: i64,
    executable_path: Option<&'a str>,
}

pub fn encode(request: &ReportApplicationContextRequest) -> io::Result<Vec<u8>> {
    let request_id = guid_text(request.request_id);
    if request.version != VERSION || request.sequence <= 0 {
        return Err(invalid());
    }
    let payload = serde_json::to_vec(&ReportWire {
        operation: "reportApplicationContext",
        version: request.version,
        request_id,
        instance_id: guid_text(request.instance_id),
        sequence: request.sequence,
        executable_path: request.executable_path.as_deref(),
    })?;
    Ok(payload)
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Response {
    frame: String,
    version: i32,
    request_id: String,
    pub success: bool,
    pub error: Option<String>,
    pub status: Option<Value>,
    pub result: Option<ResultBody>,
    archive: Option<()>,
    pub error_code: Option<i32>,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct ResultBody {
    pub status: u8,
    message: String,
    // These optional managed result fields are only applicable to other operations.
    #[serde(rename = "hyperBoostRestoredPerformance")]
    _hyper_boost_restored_performance: Option<()>,
    #[serde(rename = "cpuTuning")]
    _cpu_tuning: Option<()>,
}

impl Response {
    pub fn decode(payload: &[u8], request_id: &str) -> io::Result<Self> {
        let response: Self = serde_json::from_slice(payload).map_err(|_| invalid())?;
        if response.frame != "response"
            || response.version != VERSION
            || !response.request_id.eq_ignore_ascii_case(request_id)
            || response.archive.is_some()
        {
            return Err(invalid());
        }
        Ok(response)
    }

    pub fn report_outcome(self) -> io::Result<(ReportOutcome, String)> {
        if self.status.is_some() {
            return Err(invalid());
        }
        match (self.success, self.result, self.error, self.error_code) {
            (true, Some(result), None, None) => {
                let outcome = match result.status {
                    0 => ReportOutcome::Stored,
                    1 => ReportOutcome::Applied,
                    2 | 3 => ReportOutcome::Rejected,
                    _ => return Err(invalid()),
                };
                Ok((outcome, result.message))
            }
            (false, None, Some(error), None) if !error.trim().is_empty() => {
                Ok((ReportOutcome::Rejected, error))
            }
            _ => Err(invalid()),
        }
    }
}

pub async fn send(mut pipe: NamedPipeClient, payload: &[u8]) -> io::Result<NamedPipeClient> {
    let length = i32::try_from(payload.len()).map_err(|_| invalid())?;
    if !(1..=MAX_FRAME).contains(&length) {
        return Err(invalid());
    }
    pipe.write_i32_le(length).await?;
    pipe.write_all(payload).await?;
    Ok(pipe)
}

pub async fn receive(mut pipe: NamedPipeClient, request_id: &str) -> io::Result<Response> {
    let length = pipe.read_i32_le().await?;
    if !(1..=MAX_FRAME).contains(&length) {
        return Err(invalid());
    }
    let mut payload = vec![0; length as usize];
    pipe.read_exact(&mut payload).await?;
    Response::decode(&payload, request_id)
}

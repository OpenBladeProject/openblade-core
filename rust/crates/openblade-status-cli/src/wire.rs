use serde::{
    Deserialize, Serialize,
    de::{self, MapAccess, SeqAccess, Visitor},
};
use serde_json::{Map, Value};
use std::{collections::HashSet, fmt, time::Duration};
use tokio::io::{AsyncRead, AsyncReadExt, AsyncWrite, AsyncWriteExt};

pub const VERSION: i32 = 51;
pub const MAX_FRAME: usize = 64 * 1024;
pub const IO_TIMEOUT: Duration = Duration::from_secs(2);
pub const OVERALL_TIMEOUT: Duration = Duration::from_secs(5);

#[derive(Debug, PartialEq, Eq)]
pub enum Error {
    #[cfg(not(windows))]
    UnsupportedTransport,
    Unavailable,
    Timeout,
    Cancelled,
    CancellationSetup,
    Io,
    InvalidFrame,
    InvalidJson,
    MismatchedResponse,
    ServiceRejected,
    InvalidStatus,
}
impl fmt::Display for Error {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(match self {
            #[cfg(not(windows))]
            Self::UnsupportedTransport => "unsupported transport (Windows named pipe required)",
            Self::Unavailable => "service pipe unavailable or access denied",
            Self::Timeout => "deadline exceeded",
            Self::Cancelled => "cancelled",
            Self::CancellationSetup => "cancellation handler setup failed",
            Self::Io => "pipe I/O failed",
            Self::InvalidFrame => "invalid or truncated frame",
            Self::InvalidJson => "malformed or duplicate JSON",
            Self::MismatchedResponse => "response ID, kind or version mismatch",
            Self::ServiceRejected => "service rejected status request",
            Self::InvalidStatus => "missing or invalid status",
        })
    }
}

// Closed DTO: there is no generic operation, JSON-input or control-request API.
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct GetStatusRequest<'a> {
    operation: &'static str,
    version: i32,
    request_id: &'a str,
}
pub fn request(id: &str) -> Vec<u8> {
    serde_json::to_vec(&GetStatusRequest {
        operation: "getStatus",
        version: VERSION,
        request_id: id,
    })
    .expect("fixed request contains only serializable values")
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Response {
    frame: String,
    version: i32,
    request_id: String,
    success: bool,
    error: Option<String>,
    error_code: Option<i32>,
    result: Option<Value>,
    archive: Option<Value>,
    status: Option<Status>,
}
#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Status {
    revision: i64,
    device_verified: bool,
    active_power_source: i32,
    control_access: i32,
    live_readings: Option<Readings>,
}
#[derive(Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
struct Readings {
    battery_power_flow_milliwatts: Option<i32>,
    fan1_rpm: Option<i32>,
    fan2_rpm: Option<i32>,
    cpu_temperature_celsius: Option<i32>,
    gpu_temperature_celsius: Option<i32>,
}
impl Readings {
    fn valid(&self) -> bool {
        self.battery_power_flow_milliwatts
            .is_none_or(|v| (-1_000_000..=1_000_000).contains(&v))
            && [self.fan1_rpm, self.fan2_rpm]
                .into_iter()
                .all(|v| v.is_none_or(|v| (0..=20_000).contains(&v)))
            && [self.cpu_temperature_celsius, self.gpu_temperature_celsius]
                .into_iter()
                .all(|v| v.is_none_or(|v| (0..=115).contains(&v)))
    }
}
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct Summary {
    service_presence: &'static str,
    response_protocol: i32,
    source: &'static str,
    telemetry_freshness: &'static str,
    // These values are the service's report, not independent hardware discovery.
    service_reported_device_verified: bool,
    revision: i64,
    active_power_source: i32,
    control_access: i32,
    live_readings: Option<Readings>,
}

// Serde owns JSON grammar, UTF-8, depth and numeric parsing. This visitor rejects
// duplicate keys in EVERY object, including ignored extensions, before typed decoding.
// Unicode uppercase folding is conservative for expanding case mappings. The
// actual contract names are ASCII; values and output names are never folded.
struct Unique(Value);
impl<'de> Deserialize<'de> for Unique {
    fn deserialize<D: serde::Deserializer<'de>>(deserializer: D) -> Result<Self, D::Error> {
        struct UniqueVisitor;
        impl<'de> Visitor<'de> for UniqueVisitor {
            type Value = Unique;
            fn expecting(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
                f.write_str("JSON without duplicate keys")
            }
            fn visit_bool<E: de::Error>(self, v: bool) -> Result<Unique, E> {
                Ok(Unique(v.into()))
            }
            fn visit_i64<E: de::Error>(self, v: i64) -> Result<Unique, E> {
                Ok(Unique(v.into()))
            }
            fn visit_u64<E: de::Error>(self, v: u64) -> Result<Unique, E> {
                Ok(Unique(v.into()))
            }
            fn visit_f64<E: de::Error>(self, v: f64) -> Result<Unique, E> {
                serde_json::Number::from_f64(v)
                    .map(|n| Unique(Value::Number(n)))
                    .ok_or_else(|| E::custom("invalid number"))
            }
            fn visit_str<E: de::Error>(self, v: &str) -> Result<Unique, E> {
                Ok(Unique(v.into()))
            }
            fn visit_string<E: de::Error>(self, v: String) -> Result<Unique, E> {
                Ok(Unique(v.into()))
            }
            fn visit_unit<E: de::Error>(self) -> Result<Unique, E> {
                Ok(Unique(Value::Null))
            }
            fn visit_seq<A: SeqAccess<'de>>(self, mut seq: A) -> Result<Unique, A::Error> {
                let mut values = Vec::new();
                while let Some(Unique(value)) = seq.next_element()? {
                    values.push(value);
                }
                Ok(Unique(Value::Array(values)))
            }
            fn visit_map<A: MapAccess<'de>>(self, mut map: A) -> Result<Unique, A::Error> {
                let mut keys = HashSet::new();
                let mut values = Map::new();
                while let Some(key) = map.next_key::<String>()? {
                    if !keys.insert(key.to_uppercase()) {
                        return Err(de::Error::custom("duplicate key"));
                    }
                    let Unique(value) = map.next_value()?;
                    values.insert(key, value);
                }
                Ok(Unique(Value::Object(values)))
            }
        }
        deserializer.deserialize_any(UniqueVisitor)
    }
}

pub fn summarize(bytes: &[u8], id: &str) -> Result<String, Error> {
    if bytes.is_empty() || bytes.len() > MAX_FRAME {
        return Err(Error::InvalidFrame);
    }
    let Unique(value) = serde_json::from_slice(bytes).map_err(|_| Error::InvalidJson)?;
    let response: Response = serde_json::from_value(value).map_err(|_| Error::InvalidJson)?;
    if response.version != VERSION
        || response.frame != "response"
        || !response.request_id.eq_ignore_ascii_case(id)
    {
        return Err(Error::MismatchedResponse);
    }
    if !response.success {
        return Err(Error::ServiceRejected);
    }
    if response.error_code.is_some()
        || response.error.is_some()
        || response.result.is_some()
        || response.archive.is_some()
    {
        return Err(Error::InvalidStatus);
    }
    let status = response.status.ok_or(Error::InvalidStatus)?;
    if status.revision < 0 || status.live_readings.as_ref().is_some_and(|r| !r.valid()) {
        return Err(Error::InvalidStatus);
    }
    serde_json::to_string(&Summary {
        service_presence: "responded",
        response_protocol: response.version,
        source: "cached-service-snapshot",
        telemetry_freshness: "unknown",
        service_reported_device_verified: status.device_verified,
        revision: status.revision,
        active_power_source: status.active_power_source,
        control_access: status.control_access,
        live_readings: status.live_readings,
    })
    .map_err(|_| Error::InvalidStatus)
}

pub async fn write_request<S: AsyncWrite + Unpin>(stream: &mut S, id: &str) -> Result<(), Error> {
    let bytes = request(id);
    let length = i32::try_from(bytes.len()).map_err(|_| Error::InvalidFrame)?;
    if bytes.is_empty() || bytes.len() > MAX_FRAME {
        return Err(Error::InvalidFrame);
    }
    tokio::time::timeout(IO_TIMEOUT, async {
        stream
            .write_all(&length.to_le_bytes())
            .await
            .map_err(|_| Error::Io)?;
        stream.write_all(&bytes).await.map_err(|_| Error::Io)
    })
    .await
    .map_err(|_| Error::Timeout)?
}
pub async fn read_frame<S: AsyncRead + Unpin>(stream: &mut S) -> Result<Vec<u8>, Error> {
    tokio::time::timeout(IO_TIMEOUT, async {
        let mut prefix = [0; 4];
        stream
            .read_exact(&mut prefix)
            .await
            .map_err(|_| Error::InvalidFrame)?;
        let length = i32::from_le_bytes(prefix);
        if length <= 0 || length as usize > MAX_FRAME {
            return Err(Error::InvalidFrame);
        }
        let mut bytes = vec![0; length as usize];
        stream
            .read_exact(&mut bytes)
            .await
            .map_err(|_| Error::InvalidFrame)?;
        Ok(bytes)
    })
    .await
    .map_err(|_| Error::Timeout)?
}

pub async fn exchange<S: AsyncRead + AsyncWrite + Unpin>(
    stream: &mut S,
    id: &str,
) -> Result<String, Error> {
    write_request(stream, id).await?;
    let bytes = read_frame(stream).await?;
    summarize(&bytes, id)
}

pub async fn bounded<F, C>(operation: F, cancellation: C) -> Result<String, Error>
where
    F: std::future::Future<Output = Result<String, Error>>,
    C: std::future::Future<Output = Result<(), Error>>,
{
    tokio::select! {
        biased;
        signal = cancellation => Err(signal.err().unwrap_or(Error::Cancelled)),
        result = tokio::time::timeout(OVERALL_TIMEOUT, operation) => result.map_err(|_| Error::Timeout)?,
    }
}

#[cfg(test)]
mod tests;

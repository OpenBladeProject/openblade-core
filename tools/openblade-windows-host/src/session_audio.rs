//! Private synthetic read of the pinned managed Session Audio v2 contract.

use std::{io, time::Duration};

use openblade_windows_host::pipe::Identity;
use serde::{Deserialize, Deserializer, Serialize, de};
use tokio::{
    io::{AsyncReadExt, AsyncWriteExt},
    net::windows::named_pipe::NamedPipeServer,
    time::timeout,
};

use crate::session_windows::valid_request_id;

const VERSION: i32 = 2;
const MAX_FRAME_BYTES: i32 = 64 * 1024;
const FRAME_TIMEOUT: Duration = Duration::from_secs(2);
const READ_TIMEOUT: Duration = Duration::from_secs(10);

#[derive(Clone, Copy, Deserialize, Serialize)]
#[serde(try_from = "u8", into = "u8")]
#[repr(u8)]
enum AudioEndpoint {
    Speakers = 0,
    Headphones = 1,
}

impl TryFrom<u8> for AudioEndpoint {
    type Error = &'static str;

    fn try_from(value: u8) -> Result<Self, Self::Error> {
        match value {
            0 => Ok(Self::Speakers),
            1 => Ok(Self::Headphones),
            _ => Err("unknown Session Audio endpoint"),
        }
    }
}

impl From<AudioEndpoint> for u8 {
    fn from(endpoint: AudioEndpoint) -> Self {
        endpoint as Self
    }
}

#[derive(Deserialize)]
#[serde(tag = "operation", deny_unknown_fields)]
enum Request {
    #[serde(rename = "read")]
    Read {
        #[serde(deserialize_with = "web_integer")]
        version: i32,
        #[serde(rename = "requestId")]
        request_id: String,
        endpoint: AudioEndpoint,
    },
}

// Managed JsonSerializerDefaults.Web accepts quoted numbers. Keep string enum
// names out of the contract. Case and field names use the canonical writer shape.
fn web_integer<'de, D: Deserializer<'de>>(deserializer: D) -> Result<i32, D::Error> {
    #[derive(Deserialize)]
    #[serde(untagged)]
    enum Integer {
        Number(i32),
        Quoted(String),
    }

    match Integer::deserialize(deserializer)? {
        Integer::Number(value) => Ok(value),
        Integer::Quoted(value) => value.parse().map_err(de::Error::custom),
    }
}

#[derive(Clone, Copy)]
#[repr(u8)]
enum Status {
    Succeeded = 0,
    Unavailable = 1,
}

#[derive(Clone, Copy)]
#[repr(u8)]
enum Availability {
    ComponentsUnavailable = 2,
    AvailableReadOnly = 5,
}

#[derive(Clone, Copy)]
#[repr(u8)]
enum FeatureAccess {
    Unavailable = 2,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct State {
    endpoint: AudioEndpoint,
    spatial_enabled: bool,
    equalizer_enabled: bool,
    selected_preset_id: Option<String>,
    presets: [(); 0],
    equalizer: u8,
    application_profiles: u8,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct Response<'a> {
    version: i32,
    request_id: &'a str,
    status: u8,
    availability: u8,
    error: Option<&'static str>,
    state: Option<State>,
}

#[derive(Clone, Copy)]
pub(super) enum Scenario {
    Available,
    Unavailable,
    Stalled,
}

impl Scenario {
    pub(super) fn parse(value: &str) -> Option<Self> {
        match value {
            "available" => Some(Self::Available),
            "unavailable" => Some(Self::Unavailable),
            "stalled" => Some(Self::Stalled),
            _ => None,
        }
    }

    async fn read(self, request_id: &str, endpoint: AudioEndpoint) -> Response<'_> {
        let (status, availability, error, state) = match (self, endpoint) {
            (Self::Stalled, AudioEndpoint::Headphones) => std::future::pending().await,
            (Self::Unavailable, _) => (
                Status::Unavailable,
                Availability::ComponentsUnavailable,
                Some("The synthetic Session Audio provider has no state."),
                None,
            ),
            _ => (
                Status::Succeeded,
                Availability::AvailableReadOnly,
                None,
                Some(State {
                    endpoint,
                    spatial_enabled: matches!(endpoint, AudioEndpoint::Speakers),
                    equalizer_enabled: false,
                    selected_preset_id: None,
                    presets: [],
                    equalizer: FeatureAccess::Unavailable as u8,
                    application_profiles: FeatureAccess::Unavailable as u8,
                }),
            ),
        };
        // baseAudio is omitted. The fixture has no native or installation proof.
        Response {
            version: VERSION,
            request_id,
            status: status as u8,
            availability: availability as u8,
            error,
            state,
        }
    }
}

async fn read_request(pipe: &mut NamedPipeServer, identity: &Identity) -> io::Result<Request> {
    let mut prefix = [0; 4];
    pipe.read_exact(&mut prefix).await?;
    let length = i32::from_le_bytes(prefix);
    if !(1..=MAX_FRAME_BYTES).contains(&length) {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "invalid Session Audio frame length",
        ));
    }

    let mut body = vec![0; length as usize];
    pipe.read_exact(&mut body).await?;
    // The completed frame supplies the token used for admission.
    identity.authorize(pipe)?;
    serde_json::from_slice(&body).map_err(|error| io::Error::new(io::ErrorKind::InvalidData, error))
}

pub(super) async fn serve(
    pipe: &mut NamedPipeServer,
    identity: &Identity,
    scenario: Scenario,
) -> io::Result<()> {
    let Ok(Ok(Request::Read {
        version,
        request_id,
        endpoint,
    })) = timeout(FRAME_TIMEOUT, read_request(pipe, identity)).await
    else {
        return Ok(());
    };
    if version != VERSION || !valid_request_id(&request_id) {
        return Ok(());
    }

    // This provider has no external work. Dropping its future on deadline or
    // host stop ends it immediately. An expired read has no typed response.
    let Ok(response) = timeout(READ_TIMEOUT, scenario.read(&request_id, endpoint)).await else {
        return Ok(());
    };
    let body = serde_json::to_vec(&response).map_err(io::Error::other)?;
    if !(1..=MAX_FRAME_BYTES as usize).contains(&body.len()) {
        return Err(io::Error::other(
            "invalid synthetic Session Audio response length",
        ));
    }
    let _ = timeout(FRAME_TIMEOUT, async {
        pipe.write_all(&(body.len() as i32).to_le_bytes()).await?;
        pipe.write_all(&body).await?;
        pipe.flush().await
    })
    .await;
    Ok(())
}

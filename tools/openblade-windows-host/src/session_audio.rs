//! Private synthetic fixture for the pinned managed Session Audio v2 contract.

use std::{io, time::Duration};

use openblade_windows_host::pipe::{Connection as NamedPipeServer, Identity};
use serde::{Deserialize, Deserializer, Serialize, de};
use tokio::{
    io::{AsyncReadExt, AsyncWriteExt},
    time::timeout,
};

use crate::session_windows::valid_request_id;

mod runtime;
use runtime::{Evidence, FixtureWorker, Operation, Runtime};

const VERSION: i32 = 2;
const MAX_FRAME_BYTES: i32 = 64 * 1024;
const FRAME_TIMEOUT: Duration = Duration::from_secs(2);
const READ_TIMEOUT: Duration = Duration::from_secs(10);

#[derive(Clone, Copy, Debug, PartialEq, Eq, Deserialize, Serialize)]
#[serde(try_from = "u8", into = "u8")]
#[repr(u8)]
enum AudioEndpoint {
    Speakers = 0,
    Headphones = 1,
}

impl AudioEndpoint {
    fn index(self) -> usize {
        self as usize
    }

    fn other(self) -> Self {
        match self {
            Self::Speakers => Self::Headphones,
            Self::Headphones => Self::Speakers,
        }
    }
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
    Read(RequestHeader),
    #[serde(rename = "connect")]
    Connect(RequestHeader),
    #[serde(rename = "setSpatial")]
    SetSpatial(SpatialRequest),
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct RequestHeader {
    #[serde(deserialize_with = "web_integer")]
    version: i32,
    request_id: String,
    endpoint: AudioEndpoint,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct SpatialRequest {
    #[serde(deserialize_with = "web_integer")]
    version: i32,
    request_id: String,
    endpoint: AudioEndpoint,
    enabled: bool,
}

impl Request {
    fn parts(&self) -> (i32, &str, AudioEndpoint, Operation) {
        match self {
            Self::Read(header) => (
                header.version,
                &header.request_id,
                header.endpoint,
                Operation::Read,
            ),
            Self::Connect(header) => (
                header.version,
                &header.request_id,
                header.endpoint,
                Operation::Connect,
            ),
            Self::SetSpatial(request) => (
                request.version,
                &request.request_id,
                request.endpoint,
                Operation::SetSpatial(request.enabled),
            ),
        }
    }
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

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
#[repr(u8)]
enum Status {
    Succeeded = 0,
    Unavailable = 1,
    Failed = 2,
    Indeterminate = 3,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
#[repr(u8)]
enum Availability {
    UnsupportedModel = 1,
    ComponentsUnavailable = 2,
    ControllerActive = 3,
    Available = 4,
    AvailableReadOnly = 5,
    LegacyComponentsUnavailable = 6,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
#[repr(u8)]
enum FeatureAccess {
    ObservedOnly = 1,
    Unavailable = 2,
}

#[derive(Debug, Serialize)]
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

impl State {
    fn spatial(endpoint: AudioEndpoint, spatial_enabled: bool, equalizer: FeatureAccess) -> Self {
        Self {
            endpoint,
            spatial_enabled,
            equalizer_enabled: false,
            selected_preset_id: None,
            presets: [],
            equalizer: equalizer as u8,
            application_profiles: FeatureAccess::Unavailable as u8,
        }
    }
}

#[derive(Debug, Serialize)]
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
    Modern,
    Legacy,
    Ambiguous,
}

impl Scenario {
    pub(super) fn parse(value: &str) -> Option<Self> {
        match value {
            "available" => Some(Self::Available),
            "unavailable" => Some(Self::Unavailable),
            "stalled" => Some(Self::Stalled),
            "modern" => Some(Self::Modern),
            "legacy" => Some(Self::Legacy),
            "ambiguous" => Some(Self::Ambiguous),
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
                Some(State::spatial(
                    endpoint,
                    endpoint == AudioEndpoint::Speakers,
                    FeatureAccess::Unavailable,
                )),
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

pub(super) struct Fixture {
    scenario: Scenario,
    runtime: Option<(Evidence, Runtime, FixtureWorker)>,
}

impl Fixture {
    pub(super) fn new(scenario: Scenario) -> Self {
        let runtime = match scenario {
            Scenario::Modern | Scenario::Legacy | Scenario::Ambiguous => {
                let evidence = Evidence::fixture(matches!(scenario, Scenario::Legacy));
                Some((
                    evidence,
                    Runtime::default(),
                    FixtureWorker::new(evidence, matches!(scenario, Scenario::Ambiguous)),
                ))
            }
            _ => None,
        };
        Self { scenario, runtime }
    }

    pub(super) async fn serve(
        &mut self,
        pipe: &mut NamedPipeServer,
        identity: &Identity,
    ) -> io::Result<()> {
        let Ok(Ok(request)) = timeout(FRAME_TIMEOUT, read_request(pipe, identity)).await else {
            return Ok(());
        };
        let Some(response) = self.dispatch(&request).await else {
            return Ok(());
        };
        let sent = timeout(FRAME_TIMEOUT, write_response(pipe, &response)).await;
        if !matches!(sent, Ok(Ok(())))
            && matches!(request, Request::SetSpatial(_))
            && response.status == Status::Succeeded as u8
            && let Some((_, runtime, _)) = &mut self.runtime
        {
            // The next connection must reconcile a confirmed change whose
            // response might not have reached the initiating client.
            runtime.pause();
        }
        Ok(())
    }

    async fn dispatch<'a>(&mut self, request: &'a Request) -> Option<Response<'a>> {
        let (version, request_id, endpoint, operation) = request.parts();
        if version != VERSION || !valid_request_id(request_id) {
            return None;
        }

        let response = if let Some((evidence, runtime, worker)) = &mut self.runtime {
            let result = timeout(
                READ_TIMEOUT,
                runtime.fixture_dispatch(operation, endpoint, *evidence, worker),
            )
            .await;
            let Ok(result) = result else {
                // If dispatch was interrupted during submission, Runtime
                // retains the pause even though this connection has no reply.
                return None;
            };
            let (status, availability, error, state) = match result {
                Ok((availability, state)) => (Status::Succeeded, availability, None, Some(state)),
                Err(refusal) => (
                    refusal.status,
                    refusal.availability,
                    Some(refusal.error),
                    None,
                ),
            };
            Response {
                version: VERSION,
                request_id,
                status: status as u8,
                availability: availability as u8,
                error,
                state,
            }
        } else {
            if !matches!(operation, Operation::Read) {
                return None;
            }
            let Ok(response) =
                timeout(READ_TIMEOUT, self.scenario.read(request_id, endpoint)).await
            else {
                return None;
            };
            response
        };
        Some(response)
    }
}

async fn write_response(pipe: &mut NamedPipeServer, response: &Response<'_>) -> io::Result<()> {
    let body = serde_json::to_vec(response).map_err(io::Error::other)?;
    if !(1..=MAX_FRAME_BYTES as usize).contains(&body.len()) {
        return Err(io::Error::other(
            "invalid synthetic Session Audio response length",
        ));
    }
    pipe.write_all(&(body.len() as i32).to_le_bytes()).await?;
    pipe.write_all(&body).await?;
    pipe.flush().await
}

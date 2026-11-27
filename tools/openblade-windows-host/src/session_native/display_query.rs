//! Selected installed Display.v2 endpoint. The real supplier performs
//! coherent CCD/refresh/HDR/WCS and targeted, owned WMI brightness reads.
//! Separately selected builds add manual `setBrightness`, `setHdr`,
//! `setRefreshRate`, `setColorProfile` and/or `applyProfile` (which selects
//! all four writes it composes); every other mutation stays outside this
//! endpoint.
use super::diagnostics;
use super::feature_lifetime::stopped;
use openblade_windows_host::json_object;
#[cfg(feature = "agent-display-profile-apply-v1")]
use openblade_windows_host::json_object::Object;
use openblade_windows_host::pipe::Endpoint;
use serde::Deserialize;
use std::{
    future::Future,
    io,
    sync::{
        Arc,
        atomic::{AtomicBool, Ordering},
    },
    time::Duration,
};
use tokio::{
    io::{AsyncRead, AsyncReadExt, AsyncWrite, AsyncWriteExt},
    sync::watch,
    time::{sleep, timeout},
};
#[cfg(feature = "agent-display-automation-v1")]
mod automation;
#[cfg(feature = "agent-display-brightness-write-v1")]
mod brightness_write;
#[cfg(feature = "agent-display-color-write-v1")]
mod color_write;
#[cfg(feature = "agent-display-hdr-write-v1")]
mod hdr_write;
#[cfg(openblade_display_manual_write)]
mod manual_write;
mod observation;
#[cfg(feature = "agent-display-profile-apply-v1")]
mod profile_apply;
#[cfg(openblade_display_write)]
mod recovery;
#[cfg(feature = "agent-display-refresh-write-v1")]
mod refresh_write;
#[cfg(openblade_display_write)]
mod write;
#[cfg(feature = "agent-display-automation-v1")]
pub(super) use automation::Automation;
#[cfg(feature = "agent-display-brightness-write-v1")]
use brightness_write::{SetBrightness, WriteOutcome};
#[cfg(feature = "agent-display-color-write-v1")]
use color_write::SetColorProfile;
#[cfg(feature = "agent-display-hdr-write-v1")]
use hdr_write::SetHdr;
#[cfg(openblade_display_manual_write)]
use manual_write::ManualWriteOutcome;
use observation::Snapshot;
#[cfg(feature = "agent-display-profile-apply-v1")]
use profile_apply::{ApplyProfile, Settings};
#[cfg(feature = "agent-display-refresh-write-v1")]
use refresh_write::SetRefreshRate;

const MAX_FRAME: i32 = 65_536;
const FRAME_TIMEOUT: Duration = Duration::from_secs(2);
/// Managed `SessionDisplayPipeServer.QueryDispatchTimeout`
/// (SessionDisplayPipeServer.cs:9).
const DISPATCH_TIMEOUT: Duration = Duration::from_secs(5);
/// Managed `SessionDisplayPipeServer.MutationDispatchTimeout`
/// (SessionDisplayPipeServer.cs:10).
#[cfg(openblade_display_write)]
const MUTATION_DISPATCH_TIMEOUT: Duration = Duration::from_secs(30);
const ORIGIN_POLL: Duration = Duration::from_millis(250);

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Envelope {
    version: i32,
    request_id: String,
    #[serde(deserialize_with = "operation_name")]
    operation: Name,
    #[cfg(openblade_display_write)]
    #[serde(default, deserialize_with = "present")]
    expected_power_source: Option<i32>,
    #[cfg(feature = "agent-display-brightness-write-v1")]
    #[serde(default, deserialize_with = "present")]
    percent: Option<i32>,
    #[cfg(feature = "agent-display-hdr-write-v1")]
    #[serde(default, deserialize_with = "present")]
    enabled: Option<bool>,
    #[cfg(feature = "agent-display-refresh-write-v1")]
    #[serde(default, deserialize_with = "present")]
    refresh_rate_key: Option<String>,
    #[cfg(feature = "agent-display-color-write-v1")]
    #[serde(default, deserialize_with = "present")]
    color_profile_key: Option<String>,
    #[cfg(feature = "agent-display-profile-apply-v1")]
    #[serde(default, deserialize_with = "present")]
    // Nested derived structs are read only from an object (`json_object`). The
    // envelope is an object because `operation_first` requires a leading `{`.
    settings: Option<Object<WireSettings>>,
}
/// Managed `DisplayProfileSettings` exactly as its codec writes it: an
/// object with every member present, the setting members nullable, and the
/// computed `isConfigured` (managed ignores it on read; native requires the
/// written value). Stricter than managed only for encodings managed never
/// writes, for example a missing `brightnessPercent` or `isConfigured`
/// (managed requires the other members too), an inconsistent or null
/// `isConfigured`, duplicate members, other member-name casing and a string
/// percent. A positional array is refused like managed (`Object`).
#[cfg(feature = "agent-display-profile-apply-v1")]
#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct WireSettings {
    #[serde(deserialize_with = "nullable")]
    refresh_rate_key: Option<String>,
    #[serde(deserialize_with = "nullable")]
    hdr_enabled: Option<bool>,
    #[serde(deserialize_with = "nullable")]
    color_profile_key: Option<String>,
    #[serde(deserialize_with = "nullable")]
    brightness_percent: Option<i32>,
    is_configured: bool,
}
#[cfg(feature = "agent-display-profile-apply-v1")]
impl WireSettings {
    /// Managed `SessionDisplayIpcCodec.ValidSettings`: each present key is a
    /// ValidKey and the byte percent is at most 100.
    fn validate(self) -> Option<Settings> {
        let key = |key: &Option<String>| key.as_deref().is_none_or(observation::valid_key);
        if !key(&self.refresh_rate_key) || !key(&self.color_profile_key) {
            return None;
        }
        let brightness_percent = match self.brightness_percent {
            None => None,
            Some(percent @ 0..=100) => Some(percent as u8),
            Some(_) => return None,
        };
        let settings = Settings {
            refresh_rate_key: self.refresh_rate_key,
            hdr_enabled: self.hdr_enabled,
            color_profile_key: self.color_profile_key,
            brightness_percent,
        };
        (settings.configured() == self.is_configured).then_some(settings)
    }
}
/// A required member that may be JSON null.
#[cfg(feature = "agent-display-profile-apply-v1")]
fn nullable<'de, D: serde::Deserializer<'de>, T: Deserialize<'de>>(
    deserializer: D,
) -> Result<Option<T>, D::Error> {
    Option::<T>::deserialize(deserializer)
}
/// The managed discriminator is a JSON string. serde's externally tagged form
/// (`{"setHdr":null}`) would otherwise also select a unit variant.
fn operation_name<'de, D: serde::Deserializer<'de>>(deserializer: D) -> Result<Name, D::Error> {
    let name = String::deserialize(deserializer)?;
    Name::deserialize(serde::de::value::StrDeserializer::<D::Error>::new(&name))
}
/// Absent members stay `None`; an explicit JSON null is malformed.
#[cfg(openblade_display_write)]
fn present<'de, D: serde::Deserializer<'de>, T: Deserialize<'de>>(
    deserializer: D,
) -> Result<Option<T>, D::Error> {
    T::deserialize(deserializer).map(Some)
}
// The closed operation set rejects every unselected mutation before any
// supplier is invoked.
#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
enum Name {
    Query,
    #[cfg(feature = "agent-display-brightness-write-v1")]
    SetBrightness,
    #[cfg(feature = "agent-display-hdr-write-v1")]
    SetHdr,
    #[cfg(feature = "agent-display-refresh-write-v1")]
    SetRefreshRate,
    #[cfg(feature = "agent-display-color-write-v1")]
    SetColorProfile,
    #[cfg(feature = "agent-display-profile-apply-v1")]
    ApplyProfile,
}
enum Operation {
    Query,
    #[cfg(feature = "agent-display-brightness-write-v1")]
    SetBrightness(SetBrightness),
    #[cfg(feature = "agent-display-hdr-write-v1")]
    SetHdr(SetHdr),
    #[cfg(feature = "agent-display-refresh-write-v1")]
    SetRefreshRate(SetRefreshRate),
    #[cfg(feature = "agent-display-color-write-v1")]
    SetColorProfile(SetColorProfile),
    #[cfg(feature = "agent-display-profile-apply-v1")]
    ApplyProfile(ApplyProfile),
}
impl Operation {
    /// Managed `SessionDisplayPipeServer` (SessionDisplayPipeServer.cs:9-10):
    /// `QueryDispatchTimeout` for a query, `MutationDispatchTimeout` for
    /// every mutation.
    fn deadline(&self) -> Duration {
        match self {
            Self::Query => DISPATCH_TIMEOUT,
            #[cfg(feature = "agent-display-brightness-write-v1")]
            Self::SetBrightness(_) => MUTATION_DISPATCH_TIMEOUT,
            #[cfg(feature = "agent-display-hdr-write-v1")]
            Self::SetHdr(_) => MUTATION_DISPATCH_TIMEOUT,
            #[cfg(feature = "agent-display-refresh-write-v1")]
            Self::SetRefreshRate(_) => MUTATION_DISPATCH_TIMEOUT,
            #[cfg(feature = "agent-display-color-write-v1")]
            Self::SetColorProfile(_) => MUTATION_DISPATCH_TIMEOUT,
            #[cfg(feature = "agent-display-profile-apply-v1")]
            Self::ApplyProfile(_) => MUTATION_DISPATCH_TIMEOUT,
        }
    }
}
struct Request {
    request_id: String,
    operation: Operation,
}
impl Request {
    fn parse(body: &[u8]) -> io::Result<Self> {
        // System.Text.Json polymorphism requires the discriminator first; the
        // managed reader refuses a request whose first member is anything else.
        if !operation_first(body) {
            return Err(invalid("Display request operation must be first"));
        }
        let envelope: Envelope = json_object::from_slice(body).map_err(invalid)?;
        let id = &envelope.request_id;
        if envelope.version != 2
            || id.len() != 36
            || !id.bytes().enumerate().all(|(i, b)| {
                if matches!(i, 8 | 13 | 18 | 23) {
                    b == b'-'
                } else {
                    b.is_ascii_hexdigit()
                }
            })
            || id == "00000000-0000-0000-0000-000000000000"
        {
            return Err(invalid("invalid Display query envelope"));
        }
        #[cfg(openblade_display_write)]
        let power = envelope.expected_power_source;
        #[cfg(feature = "agent-display-brightness-write-v1")]
        let percent = envelope.percent;
        #[cfg(all(
            openblade_display_write,
            not(feature = "agent-display-brightness-write-v1")
        ))]
        let percent: Option<i32> = None;
        #[cfg(feature = "agent-display-hdr-write-v1")]
        let enabled = envelope.enabled;
        #[cfg(all(openblade_display_write, not(feature = "agent-display-hdr-write-v1")))]
        let enabled: Option<bool> = None;
        #[cfg(feature = "agent-display-refresh-write-v1")]
        let key = envelope.refresh_rate_key;
        #[cfg(all(
            openblade_display_write,
            not(feature = "agent-display-refresh-write-v1")
        ))]
        let key: Option<String> = None;
        #[cfg(feature = "agent-display-color-write-v1")]
        let color = envelope.color_profile_key;
        #[cfg(all(openblade_display_write, not(feature = "agent-display-color-write-v1")))]
        let color: Option<String> = None;
        #[cfg(feature = "agent-display-profile-apply-v1")]
        let settings = envelope.settings.map(|Object(settings)| settings);
        #[cfg(all(
            openblade_display_write,
            not(feature = "agent-display-profile-apply-v1")
        ))]
        let settings: Option<()> = None;
        // Each operation accepts exactly its own managed members.
        let operation = match envelope.operation {
            #[cfg(not(openblade_display_write))]
            Name::Query => Operation::Query,
            #[cfg(openblade_display_write)]
            Name::Query => match (power, percent, enabled, key, color, settings) {
                (None, None, None, None, None, None) => Operation::Query,
                _ => return Err(invalid("invalid Display query members")),
            },
            // Managed IsManualSource (PluggedIn, Battery, UsbC or ExternalUnknown)
            // and byte percent <= 100. ExternalUnknown is admitted only by a
            // manual-external power context (managed ManualExternalPowerPresent).
            #[cfg(feature = "agent-display-brightness-write-v1")]
            Name::SetBrightness => match (power, percent, enabled, key, color, settings) {
                (Some(source @ 0..=3), Some(percent @ 0..=100), None, None, None, None) => {
                    Operation::SetBrightness(SetBrightness {
                        expected_power_source: source as u8,
                        percent: percent as u8,
                    })
                }
                _ => return Err(invalid("invalid Display setBrightness members")),
            },
            // Managed IsManualSource and a required JSON Boolean.
            #[cfg(feature = "agent-display-hdr-write-v1")]
            Name::SetHdr => match (power, percent, enabled, key, color, settings) {
                (Some(source @ 0..=3), None, Some(enabled), None, None, None) => {
                    Operation::SetHdr(SetHdr {
                        expected_power_source: source as u8,
                        enabled,
                    })
                }
                _ => return Err(invalid("invalid Display setHdr members")),
            },
            // Managed IsManualSource and ValidKey (non-blank, trimmed, at most
            // 1024 UTF-16 units, no control characters).
            #[cfg(feature = "agent-display-refresh-write-v1")]
            Name::SetRefreshRate => match (power, percent, enabled, key, color, settings) {
                (Some(source @ 0..=3), None, None, Some(key), None, None)
                    if observation::valid_key(&key) =>
                {
                    Operation::SetRefreshRate(SetRefreshRate {
                        expected_power_source: source as u8,
                        key,
                    })
                }
                _ => return Err(invalid("invalid Display setRefreshRate members")),
            },
            // Managed IsManualSource and ValidKey, like setRefreshRate.
            #[cfg(feature = "agent-display-color-write-v1")]
            Name::SetColorProfile => match (power, percent, enabled, key, color, settings) {
                (Some(source @ 0..=3), None, None, None, Some(key), None)
                    if observation::valid_key(&key) =>
                {
                    Operation::SetColorProfile(SetColorProfile {
                        expected_power_source: source as u8,
                        key,
                    })
                }
                _ => return Err(invalid("invalid Display setColorProfile members")),
            },
            // Managed IsConfirmed and ValidSettings.
            #[cfg(feature = "agent-display-profile-apply-v1")]
            Name::ApplyProfile => match (power, percent, enabled, key, color, settings) {
                (Some(source @ 0..=2), None, None, None, None, Some(settings)) => {
                    match settings.validate() {
                        Some(settings) => Operation::ApplyProfile(ApplyProfile {
                            expected_power_source: source as u8,
                            settings,
                        }),
                        None => return Err(invalid("invalid Display applyProfile settings")),
                    }
                }
                _ => return Err(invalid("invalid Display applyProfile members")),
            },
        };
        Ok(Self {
            request_id: envelope.request_id,
            operation,
        })
    }
}
fn invalid(error: impl Into<Box<dyn std::error::Error + Send + Sync>>) -> io::Error {
    io::Error::new(io::ErrorKind::InvalidData, error)
}

trait Supplier {
    fn acquire(&mut self, canceled: Arc<AtomicBool>) -> impl Future<Output = io::Result<Snapshot>>;
    /// Resolves only after an admitted operation recorded its certainty, even
    /// when cancellation was requested first.
    #[cfg(feature = "agent-display-brightness-write-v1")]
    fn set_brightness(
        &mut self,
        request: SetBrightness,
        canceled: Arc<AtomicBool>,
    ) -> impl Future<Output = io::Result<WriteOutcome>>;
    /// The same custody contract as `set_brightness`.
    #[cfg(feature = "agent-display-hdr-write-v1")]
    fn set_hdr(
        &mut self,
        request: SetHdr,
        canceled: Arc<AtomicBool>,
    ) -> impl Future<Output = io::Result<ManualWriteOutcome>>;
    /// The same custody contract as `set_brightness`.
    #[cfg(feature = "agent-display-refresh-write-v1")]
    fn set_refresh_rate(
        &mut self,
        request: SetRefreshRate,
        canceled: Arc<AtomicBool>,
    ) -> impl Future<Output = io::Result<ManualWriteOutcome>>;
    /// The same custody contract as `set_brightness`.
    #[cfg(feature = "agent-display-color-write-v1")]
    fn set_color_profile(
        &mut self,
        request: SetColorProfile,
        canceled: Arc<AtomicBool>,
    ) -> impl Future<Output = io::Result<ManualWriteOutcome>>;
    /// The same custody contract as `set_brightness`.
    #[cfg(feature = "agent-display-profile-apply-v1")]
    fn apply_profile(
        &mut self,
        request: ApplyProfile,
        canceled: Arc<AtomicBool>,
    ) -> impl Future<Output = io::Result<ManualWriteOutcome>>;
    async fn drain(&mut self) -> io::Result<()> {
        Ok(())
    }
}
struct WindowsSupplier {
    brightness: observation::SharedBrightness,
    #[cfg(openblade_display_write)]
    writer: write::Writer,
}
impl Supplier for WindowsSupplier {
    async fn acquire(&mut self, canceled: Arc<AtomicBool>) -> io::Result<Snapshot> {
        if canceled.load(Ordering::Acquire) {
            return Err(io::ErrorKind::Interrupted.into());
        }
        let brightness = self.brightness.clone();
        #[cfg(openblade_display_write)]
        let writer = self.writer.clone();
        tokio::task::spawn_blocking(move || {
            // Managed ResumeAfterSuccessfulExplicitRead after every completed query.
            #[cfg(openblade_display_write)]
            return writer.query(&brightness, &canceled);
            #[cfg(not(openblade_display_write))]
            observation::query(&canceled, brightness)
        })
        .await
        .map_err(|_| io::Error::other("display read worker failed"))?
    }
    #[cfg(feature = "agent-display-brightness-write-v1")]
    async fn set_brightness(
        &mut self,
        request: SetBrightness,
        canceled: Arc<AtomicBool>,
    ) -> io::Result<WriteOutcome> {
        let brightness = self.brightness.clone();
        let writer = self.writer.clone();
        tokio::task::spawn_blocking(move || writer.set_brightness(&brightness, request, &canceled))
            .await
            .map_err(|_| io::Error::other("display write worker failed"))?
    }
    #[cfg(feature = "agent-display-hdr-write-v1")]
    async fn set_hdr(
        &mut self,
        request: SetHdr,
        canceled: Arc<AtomicBool>,
    ) -> io::Result<ManualWriteOutcome> {
        let brightness = self.brightness.clone();
        let writer = self.writer.clone();
        tokio::task::spawn_blocking(move || writer.set_hdr(&brightness, request, &canceled))
            .await
            .map_err(|_| io::Error::other("display write worker failed"))?
    }
    #[cfg(feature = "agent-display-refresh-write-v1")]
    async fn set_refresh_rate(
        &mut self,
        request: SetRefreshRate,
        canceled: Arc<AtomicBool>,
    ) -> io::Result<ManualWriteOutcome> {
        let brightness = self.brightness.clone();
        let writer = self.writer.clone();
        tokio::task::spawn_blocking(move || {
            writer.set_refresh_rate(&brightness, &request, &canceled)
        })
        .await
        .map_err(|_| io::Error::other("display write worker failed"))?
    }
    #[cfg(feature = "agent-display-color-write-v1")]
    async fn set_color_profile(
        &mut self,
        request: SetColorProfile,
        canceled: Arc<AtomicBool>,
    ) -> io::Result<ManualWriteOutcome> {
        let brightness = self.brightness.clone();
        let writer = self.writer.clone();
        tokio::task::spawn_blocking(move || {
            writer.set_color_profile(&brightness, &request, &canceled)
        })
        .await
        .map_err(|_| io::Error::other("display write worker failed"))?
    }
    #[cfg(feature = "agent-display-profile-apply-v1")]
    async fn apply_profile(
        &mut self,
        request: ApplyProfile,
        canceled: Arc<AtomicBool>,
    ) -> io::Result<ManualWriteOutcome> {
        let brightness = self.brightness.clone();
        let writer = self.writer.clone();
        tokio::task::spawn_blocking(move || writer.apply_profile(&brightness, &request, &canceled))
            .await
            .map_err(|_| io::Error::other("display write worker failed"))?
    }
    async fn drain(&mut self) -> io::Result<()> {
        observation::drain_brightness(self.brightness.clone()).await
    }
}
struct Cancellation(Arc<AtomicBool>);
impl Drop for Cancellation {
    fn drop(&mut self) {
        self.0.store(true, Ordering::Release);
    }
}

pub(super) async fn run(
    origin: Arc<crate::loaded_origin::Origin>,
    stop: watch::Receiver<bool>,
    #[cfg(openblade_display_write)] power: super::power_admission::PowerAdmission,
    #[cfg(feature = "agent-display-automation-v1")] automation: Automation,
) -> io::Result<()> {
    super::verify_origin(&origin)?;
    let endpoint = Endpoint::current_user_display().map_err(diagnostics::listener)?;
    let _claim = endpoint.claim().map_err(diagnostics::claim)?;
    #[cfg(feature = "agent-display-automation-v1")]
    return run_automated(&origin, &endpoint, stop, power, automation).await;
    #[cfg(not(feature = "agent-display-automation-v1"))]
    {
        let mut stop = stop;
        let mut supplier = WindowsSupplier {
            brightness: Default::default(),
            #[cfg(openblade_display_write)]
            writer: {
                let origin = origin.clone();
                let stop = stop.clone();
                tokio::task::spawn_blocking(move || write::Writer::load(origin, power, stop))
                    .await
                    .map_err(|_| io::Error::other("display recovery load failed"))?
            },
        };
        run_connections(
            &mut stop,
            &mut supplier,
            || async {
                let pipe = endpoint.next_listener().await?;
                pipe.connect().await?;
                Ok(pipe)
            },
            |pipe| endpoint.identity.authorize(pipe),
            || origin.verify(),
        )
        .await
    }
}

/// The endpoint beside the display automation runner, as one lifetime:
/// either ending (Stop, Origin loss, a fatal error) stops the other through
/// the local stop that every write fence also observes, and the brightness
/// owner is drained only after both have returned.
#[cfg(feature = "agent-display-automation-v1")]
async fn run_automated(
    origin: &Arc<crate::loaded_origin::Origin>,
    endpoint: &Endpoint,
    mut stop: watch::Receiver<bool>,
    power: super::power_admission::PowerAdmission,
    automation: Automation,
) -> io::Result<()> {
    let (local, local_stop) = watch::channel(false);
    let brightness = observation::SharedBrightness::default();
    let writer = write::Writer::new(
        automation.recovery(),
        origin.clone(),
        power,
        local_stop.clone(),
    );
    let applier = writer.applier(brightness.clone());
    let mut supplier = WindowsSupplier { brightness, writer };
    let mut serving_stop = local_stop.clone();
    let mut automation_stop = local_stop;
    let result = {
        let work = async {
            let served = async {
                let result = serve_connections(
                    &mut serving_stop,
                    &mut supplier,
                    || async {
                        let pipe = endpoint.next_listener().await?;
                        pipe.connect().await?;
                        Ok(pipe)
                    },
                    |pipe| endpoint.identity.authorize(pipe),
                    || origin.verify(),
                )
                .await;
                let _ = local.send(true);
                result
            };
            let automated = async {
                let result = automation::serve(&automation, applier, &mut automation_stop).await;
                let _ = local.send(true);
                result
            };
            let (served, automated) = tokio::join!(served, automated);
            served.and(automated)
        };
        tokio::pin!(work);
        tokio::select! {
            result = &mut work => result,
            () = stopped(&mut stop) => {
                let _ = local.send(true);
                (&mut work).await
            }
        }
    };
    // Every exit closes and joins persistent WMI ownership while the caller
    // still holds its claim and Origin, as `run_connections` does.
    let drained = supplier.drain().await;
    result.and(drained)
}

#[derive(Debug)]
enum ServeError {
    #[allow(dead_code)] // The connection error is carried for Debug output only.
    Connection(io::Error),
    Fatal(io::Error),
}
impl From<io::Error> for ServeError {
    fn from(error: io::Error) -> Self {
        Self::Connection(error)
    }
}
impl From<tokio::time::error::Elapsed> for ServeError {
    fn from(error: tokio::time::error::Elapsed) -> Self {
        Self::Connection(error.into())
    }
}
async fn origin_invalid(verify: &impl Fn() -> io::Result<()>) -> io::Error {
    loop {
        if let Err(error) = verify() {
            return error;
        }
        sleep(ORIGIN_POLL).await;
    }
}
#[cfg(any(test, not(feature = "agent-display-automation-v1")))]
async fn run_connections<S, F>(
    stop: &mut watch::Receiver<bool>,
    supplier: &mut impl Supplier,
    accept: impl FnMut() -> F,
    authorize: impl Fn(&S) -> io::Result<()>,
    verify: impl Fn() -> io::Result<()>,
) -> io::Result<()>
where
    S: AsyncRead + AsyncWrite + Unpin,
    F: Future<Output = io::Result<S>>,
{
    let result = serve_connections(stop, supplier, accept, authorize, verify).await;
    // Every loop exit (Stop, Origin, listener, supplier failure) closes and joins
    // persistent WMI ownership while the caller still holds its claim/Origin.
    // Preserve the causal operation error if retirement also reports failure.
    let drained = supplier.drain().await;
    result.and(drained)
}

async fn serve_connections<S, F>(
    stop: &mut watch::Receiver<bool>,
    supplier: &mut impl Supplier,
    mut accept: impl FnMut() -> F,
    authorize: impl Fn(&S) -> io::Result<()>,
    verify: impl Fn() -> io::Result<()>,
) -> io::Result<()>
where
    S: AsyncRead + AsyncWrite + Unpin,
    F: Future<Output = io::Result<S>>,
{
    loop {
        if *stop.borrow() {
            return Ok(());
        }
        verify().map_err(diagnostics::origin)?;
        let mut pipe = tokio::select! {
            biased;
            _ = stopped(stop) => return Ok(()),
            result = accept() => result.map_err(diagnostics::listener)?,
        };
        match serve(&mut pipe, stop, supplier, &authorize, &verify).await {
            Ok(()) | Err(ServeError::Connection(_)) => (),
            Err(ServeError::Fatal(error)) => return Err(error),
        }
    }
}
async fn read_frame(pipe: &mut (impl AsyncRead + Unpin)) -> io::Result<Vec<u8>> {
    let length = pipe.read_i32_le().await?;
    if !(1..=MAX_FRAME).contains(&length) {
        return Err(invalid("invalid Display frame length"));
    }
    let mut body = vec![0; length as usize];
    pipe.read_exact(&mut body).await?;
    Ok(body)
}
/// One managed `SessionDisplayResponse`. `error` is null exactly when the
/// status is Succeeded (0).
fn frame(id: &str, status: u8, error: Option<&str>, snapshot: Snapshot) -> io::Result<Vec<u8>> {
    debug_assert_eq!(status == 0, error.is_none());
    let snapshot = if snapshot.valid() {
        snapshot
    } else {
        Snapshot::unavailable("Windows returned display data outside the query limits.")
    };
    let encode = |state: Snapshot| {
        serde_json::to_vec(&serde_json::json!({
            "version":2, "requestId":id, "status":status, "error":error, "state":state,
        }))
        .map_err(invalid)
    };
    let mut body = encode(snapshot)?;
    if body.len() > MAX_FRAME as usize {
        body = encode(Snapshot::unavailable(
            "Windows returned display data outside the frame limit.",
        ))?;
    }
    let mut frame = Vec::with_capacity(4 + body.len());
    frame.extend((body.len() as i32).to_le_bytes());
    frame.extend(body);
    Ok(frame)
}

enum Reply {
    Query(Snapshot),
    #[cfg(feature = "agent-display-brightness-write-v1")]
    Write(WriteOutcome),
    #[cfg(openblade_display_manual_write)]
    Manual(ManualWriteOutcome),
}

/// Every started blocking operation is awaited, including on deadline,
/// disconnect, Stop and origin loss. Windows CCD/WCS/WMI have no safe per-call
/// cancellation; retirement is conditional on that call returning, with no
/// false bounded-stop promise. Endpoint and original Origin custody stay held
/// throughout this wait. A canceled write still records its certainty.
async fn serve<S: AsyncRead + AsyncWrite + Unpin>(
    pipe: &mut S,
    stop: &mut watch::Receiver<bool>,
    supplier: &mut impl Supplier,
    authorize: impl Fn(&S) -> io::Result<()>,
    verify: impl Fn() -> io::Result<()>,
) -> Result<(), ServeError> {
    if *stop.borrow() {
        return Ok(());
    }
    let body = tokio::select! {
        biased;
        _ = stopped(stop) => return Ok(()),
        result = timeout(FRAME_TIMEOUT, read_frame(pipe)) => result??,
    };
    // Impersonation uses the just-read message, with fresh SID/session/LUID.
    verify().map_err(|e| ServeError::Fatal(diagnostics::origin(e)))?;
    authorize(pipe)?;
    let request = Request::parse(&body)?;
    verify().map_err(|e| ServeError::Fatal(diagnostics::origin(e)))?;
    if *stop.borrow() {
        return Ok(());
    }
    let canceled = Cancellation(Arc::new(AtomicBool::new(false)));
    let operation = request.operation;
    let deadline = operation.deadline();
    // A short-lived local; boxing the reply would add an allocation to every request.
    #[allow(clippy::large_enum_variant)]
    enum End {
        Complete(io::Result<Reply>),
        Retire,
        Stale(io::Error),
    }
    let end = {
        let flag = canceled.0.clone();
        let work = async {
            match operation {
                Operation::Query => supplier.acquire(flag).await.map(Reply::Query),
                #[cfg(feature = "agent-display-brightness-write-v1")]
                Operation::SetBrightness(request) => supplier
                    .set_brightness(request, flag)
                    .await
                    .map(Reply::Write),
                #[cfg(feature = "agent-display-hdr-write-v1")]
                Operation::SetHdr(request) => {
                    supplier.set_hdr(request, flag).await.map(Reply::Manual)
                }
                #[cfg(feature = "agent-display-refresh-write-v1")]
                Operation::SetRefreshRate(request) => supplier
                    .set_refresh_rate(request, flag)
                    .await
                    .map(Reply::Manual),
                #[cfg(feature = "agent-display-color-write-v1")]
                Operation::SetColorProfile(request) => supplier
                    .set_color_profile(request, flag)
                    .await
                    .map(Reply::Manual),
                #[cfg(feature = "agent-display-profile-apply-v1")]
                Operation::ApplyProfile(request) => supplier
                    .apply_profile(request, flag)
                    .await
                    .map(Reply::Manual),
            }
        };
        tokio::pin!(work);
        let mut extra = [0u8];
        let end = tokio::select! {
            biased;
            _ = stopped(stop) => End::Retire,
            error = origin_invalid(&verify) => End::Stale(error),
            _ = sleep(deadline) => End::Retire,
            _ = pipe.read(&mut extra) => End::Retire,
            result = &mut work => End::Complete(result),
        };
        match end {
            End::Complete(result) => End::Complete(result),
            reason => {
                canceled.0.store(true, Ordering::Release);
                // Retain and join the actual work future; cancellation is a
                // request, never evidence that spawn_blocking has completed.
                match work.await {
                    Err(error) if error.kind() != io::ErrorKind::Interrupted => {
                        End::Complete(Err(error))
                    }
                    _ => reason,
                }
            }
        }
    };
    let reply = match end {
        End::Retire => return Ok(()),
        End::Stale(error) => return Err(ServeError::Fatal(diagnostics::origin(error))),
        End::Complete(Err(error)) if error.kind() == io::ErrorKind::Interrupted => return Ok(()),
        End::Complete(result) => result.map_err(ServeError::Fatal)?,
    };
    if *stop.borrow() {
        return Ok(());
    }
    let frame = match reply {
        Reply::Query(snapshot) => frame(&request.request_id, 0, None, snapshot)?,
        #[cfg(feature = "agent-display-brightness-write-v1")]
        Reply::Write(outcome) => frame(
            &request.request_id,
            outcome.status as u8,
            outcome.error,
            outcome.state,
        )?,
        #[cfg(openblade_display_manual_write)]
        Reply::Manual(outcome) => frame(
            &request.request_id,
            outcome.status as u8,
            outcome.error.as_deref(),
            outcome.state,
        )?,
    };
    verify().map_err(|e| ServeError::Fatal(diagnostics::origin(e)))?;
    authorize(pipe)?;
    tokio::select! {
        biased;
        _ = stopped(stop) => Ok(()),
        result = timeout(FRAME_TIMEOUT, pipe.write_all(&frame)) => { result??; Ok(()) },
    }
}
/// The first member name is literally `"operation"` (an escaped spelling is
/// refused; stricter than managed only for an encoding it never writes).
fn operation_first(body: &[u8]) -> bool {
    let rest = body.trim_ascii_start();
    rest.strip_prefix(b"{")
        .is_some_and(|rest| rest.trim_ascii_start().starts_with(b"\"operation\""))
}

#[cfg(test)]
#[path = "display_query/request_parity_tests.rs"]
mod request_parity_tests;
#[cfg(all(test, openblade_display_write))]
#[path = "display_query/test_support.rs"]
mod test_support;
#[cfg(test)]
mod tests;

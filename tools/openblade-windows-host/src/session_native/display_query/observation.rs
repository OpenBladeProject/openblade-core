//! Read-only Display v2 query for the current-user desktop.
use openblade_platform_windows::{
    brightness::{
        BrightnessError, BrightnessQueryTarget, BrightnessSnapshot, WindowsBrightnessWorker,
        process_security_status,
    },
    color_profile::{ColorProfileSnapshot, HdrState, ProfileReadError, query_color_profiles},
    display::{
        ActiveInternalDisplay, DisplayReadError, InternalDisplayRefresh, InternalDisplayTopology,
        RefreshSnapshot, read_internal_display, read_internal_display_refresh,
    },
};
use serde::{Deserialize, Serialize};
use std::{
    io,
    sync::{
        Arc, Mutex,
        atomic::{AtomicBool, Ordering},
    },
};

#[derive(Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub(super) struct Snapshot {
    is_available: bool,
    display_name: Option<String>,
    width: i32,
    height: i32,
    current_refresh_key: Option<String>,
    refresh_options: Vec<RefreshOption>,
    hdr_supported: bool,
    hdr_enabled: bool,
    color_profiles_enabled: bool,
    active_color_profile_id: Option<String>,
    color_profiles: Vec<ColorProfile>,
    message: Option<String>,
    bits_per_color_channel: Option<i32>,
    brightness_percent: Option<u8>,
    supported_brightness_levels: Vec<u8>,
}

#[derive(Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct RefreshOption {
    key: String,
    name: String,
    minimum_hertz: i32,
    maximum_hertz: i32,
    desktop_hertz: i32,
    is_dynamic: bool,
}

#[derive(Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct ColorProfile {
    id: String,
    name: String,
    is_active: bool,
}

const MAX_OPTIONS: usize = 64;
const MAX_TEXT: usize = 4096;
const MAX_KEY: usize = 1024;
const OUT_OF_BOUNDS: &str =
    "The display observation could not be represented in the display protocol.";
const CHANGED: &str = "Windows could not reconfirm the internal display topology and state.";
pub(super) const HDR_UNKNOWN: &str =
    "Windows could not confirm HDR state for the internal display.";

fn bounded(value: &str, limit: usize) -> bool {
    value.encode_utf16().take(limit + 1).count() <= limit
}

/// Managed `SessionDisplayIpcCodec.ValidKey`.
pub(super) fn valid_key(key: &str) -> bool {
    bounded(key, MAX_KEY)
        && !key.is_empty()
        && key.trim() == key
        && !key.chars().any(char::is_control)
}

fn valid_name(name: &str) -> bool {
    bounded(name, MAX_TEXT) && !name.trim().is_empty()
}

impl Snapshot {
    pub(super) fn unavailable(message: &str) -> Self {
        Self {
            is_available: false,
            display_name: None,
            width: 0,
            height: 0,
            current_refresh_key: None,
            refresh_options: Vec::new(),
            hdr_supported: false,
            hdr_enabled: false,
            color_profiles_enabled: false,
            active_color_profile_id: None,
            color_profiles: Vec::new(),
            message: Some(message.into()),
            bits_per_color_channel: None,
            brightness_percent: None,
            supported_brightness_levels: Vec::new(),
        }
    }

    pub(super) fn valid(&self) -> bool {
        self.width >= 0
            && self.height >= 0
            && self.refresh_options.len() <= MAX_OPTIONS
            && self.color_profiles.len() <= MAX_OPTIONS
            && self.current_refresh_key.as_deref().is_none_or(valid_key)
            && self
                .active_color_profile_id
                .as_deref()
                .is_none_or(valid_key)
            && self.message.as_deref().is_none_or(|s| bounded(s, MAX_TEXT))
            && self
                .refresh_options
                .iter()
                .all(|r| valid_key(&r.key) && valid_name(&r.name))
            && self
                .color_profiles
                .iter()
                .all(|p| valid_key(&p.id) && valid_name(&p.name))
            && self.brightness_percent.is_none_or(|percent| percent <= 100)
            && self.supported_brightness_levels.len() <= 101
            && self
                .supported_brightness_levels
                .iter()
                .all(|level| *level <= 100)
            && self
                .supported_brightness_levels
                .windows(2)
                .all(|pair| pair[0] < pair[1])
            && (self.brightness_percent.is_some() == !self.supported_brightness_levels.is_empty())
    }

    /// Managed DisplayAutomationCoordinator.HasCompleteReadback.
    #[cfg(openblade_display_write)]
    pub(super) fn confirms(&self, fields: super::recovery::Fields) -> bool {
        let text = |value: &Option<String>| value.as_deref().is_some_and(|s| !s.trim().is_empty());
        self.is_available
            && (!fields.refresh || text(&self.current_refresh_key))
            && (!fields.hdr || self.hdr_supported)
            && (!fields.color
                || self.hdr_enabled
                || (self.color_profiles_enabled && text(&self.active_color_profile_id)))
            && (!fields.brightness
                || self.brightness_percent.is_some()
                    && !self.supported_brightness_levels.is_empty())
    }

    /// The native write gate's reopening rule: whether this snapshot observed
    /// the current state of `fields`. Refresh, HDR and color need only an
    /// available snapshot. It always carries a complete refresh read even
    /// when the mode matches no option (issue #884: a 60 Hz desktop over a
    /// 240 Hz signal, or a desktop-only apply); managed has no such gate and a
    /// manual change is the way out of that state. It always completed the
    /// advanced-color read (an unreadable one makes it unavailable), so it
    /// observed whether HDR is supported and its enabled state. An unsupported
    /// panel is a definitive observation too: setHdr is refused before any
    /// submission while its own read shows HDR unsupported, and applyProfile
    /// then skips HDR, so no HDR write can follow until a fresh read shows
    /// support. Requiring support instead would refuse forever a profile that
    /// holds an HDR setting on such a panel and that managed applies. It also
    /// always completed the WCS read (an unreadable scope, association list
    /// or default makes it unavailable), and under confirmed SDR with no
    /// listed default it shows no active profile, which only a color write can
    /// change. With HDR on, `HasCompleteReadback` already accepts the color
    /// field and setColorProfile is refused before any submission. Brightness
    /// keeps `HasCompleteReadback`: a snapshot without a reading did not
    /// observe it, and while it is unreadable every request that writes
    /// brightness fails in managed too (setBrightness and applyProfile
    /// "Display brightness is unavailable."), so no gate here holds a request
    /// that could succeed. `confirms` already requires an available snapshot,
    /// so dropping its refresh-key, HDR-support and color conditions is the
    /// whole rule.
    #[cfg(openblade_display_write)]
    pub(super) fn observes(&self, fields: super::recovery::Fields) -> bool {
        self.confirms(super::recovery::Fields {
            refresh: false,
            hdr: false,
            color: false,
            ..fields
        })
    }

    #[cfg(feature = "agent-display-brightness-write-v1")]
    pub(super) fn brightness(&self) -> Option<u8> {
        self.brightness_percent
    }

    /// The HDR user preference when this available snapshot supports HDR
    /// (managed `ResolveHdrMutationResult` reconciliation).
    #[cfg(feature = "agent-display-hdr-write-v1")]
    pub(super) fn hdr(&self) -> Option<bool> {
        (self.is_available && self.hdr_supported).then_some(self.hdr_enabled)
    }

    /// The classified current refresh option of this available snapshot.
    #[cfg(feature = "agent-display-refresh-write-v1")]
    pub(super) fn refresh_key(&self) -> Option<&str> {
        self.current_refresh_key
            .as_deref()
            .filter(|_| self.is_available)
    }

    /// The active color-profile ID of this available snapshot under
    /// confirmed SDR (managed `ActiveColorProfileId`).
    #[cfg(feature = "agent-display-color-write-v1")]
    pub(super) fn color_profile(&self) -> Option<&str> {
        self.active_color_profile_id
            .as_deref()
            .filter(|_| self.is_available && self.color_profiles_enabled)
    }

    /// The raw members managed `ApplyProfileCoreAsync` reads, unfiltered by
    /// availability, as the managed snapshot exposes them.
    #[cfg(feature = "agent-display-profile-apply-v1")]
    pub(super) fn observed(&self) -> Observed<'_> {
        Observed {
            available: self.is_available,
            message: self.message.as_deref(),
            refresh_key: self.current_refresh_key.as_deref(),
            hdr_supported: self.hdr_supported,
            hdr_enabled: self.hdr_enabled,
            color_profile: self.active_color_profile_id.as_deref(),
            brightness: self.brightness_percent,
            levels: &self.supported_brightness_levels,
        }
    }
}

/// Managed `DisplaySettingsSnapshot` members of one snapshot, by reference.
#[cfg(feature = "agent-display-profile-apply-v1")]
pub(super) struct Observed<'a> {
    pub(super) available: bool,
    pub(super) message: Option<&'a str>,
    pub(super) refresh_key: Option<&'a str>,
    pub(super) hdr_supported: bool,
    pub(super) hdr_enabled: bool,
    pub(super) color_profile: Option<&'a str>,
    pub(super) brightness: Option<u8>,
    pub(super) levels: &'a [u8],
}

trait DisplayReads {
    fn brightness(
        &mut self,
        panel: &ActiveInternalDisplay,
        canceled: &AtomicBool,
    ) -> io::Result<Result<BrightnessSnapshot, BrightnessError>>;
    fn refresh(&mut self) -> Result<InternalDisplayRefresh, DisplayReadError>;
    fn topology(&mut self) -> Result<InternalDisplayTopology, DisplayReadError>;
    fn profiles(
        &mut self,
        panel: &ActiveInternalDisplay,
        hdr: HdrState,
    ) -> Result<ColorProfileSnapshot, ProfileReadError>;
}

pub(super) type SharedBrightness = Arc<Mutex<BrightnessOwner>>;

/// The feature owns one native thread/COM connection across requests. Reply
/// failure is independent of retirement: pending native work stays on this
/// same owner and makes subsequent brightness reads busy, never replacements.
pub(super) struct BrightnessOwner {
    worker: Option<WindowsBrightnessWorker>,
    closed: bool,
    start: Starter,
}

/// Starts a worker when none is owned. The outer error is a process security
/// refusal; the inner one is a worker that never started (submitted nothing).
type Starter = Box<
    dyn FnMut() -> Result<
            io::Result<WindowsBrightnessWorker>,
            openblade_platform_windows::brightness::NativeFailure,
        > + Send,
>;

impl Default for BrightnessOwner {
    fn default() -> Self {
        Self {
            worker: None,
            closed: false,
            start: Box::new(|| {
                process_security_status()?;
                Ok(WindowsBrightnessWorker::start())
            }),
        }
    }
}

impl BrightnessOwner {
    pub(super) fn read(
        &mut self,
        target: BrightnessQueryTarget,
        canceled: &AtomicBool,
    ) -> io::Result<Result<BrightnessSnapshot, BrightnessError>> {
        if self.closed {
            return Err(io::Error::other("brightness owner is closed"));
        }
        if self.worker.is_none() {
            self.worker =
                match (self.start)() {
                    Err(error) => return Ok(Err(BrightnessError::Native(error))),
                    Ok(Ok(worker)) => Some(worker),
                    Ok(Err(_)) => return Ok(Err(BrightnessError::Native(
                        openblade_platform_windows::brightness::NativeFailure::WorkerDisconnected,
                    ))),
                };
        }
        let result = self
            .worker
            .as_mut()
            .expect("owned worker")
            .read_query_target(target, || canceled.load(Ordering::Acquire));
        // An existing worker's lost reply indicates thread failure, unlike an
        // ordinary WMI/COM error. Stop the feature and positively join it rather
        // than hide a panic as permanent partial availability or replace it.
        if matches!(
            result,
            Err(BrightnessError::Native(
                openblade_platform_windows::brightness::NativeFailure::WorkerDisconnected,
            ))
        ) {
            return Err(io::Error::other("brightness worker disconnected"));
        }
        Ok(result)
    }

    /// One fenced manual write on the same feature-owned worker as queries.
    /// A busy worker (a prior call still retiring) refuses without submitting.
    #[cfg(feature = "agent-display-brightness-write-v1")]
    pub(super) fn apply(
        &mut self,
        percent: u8,
        target: openblade_platform_windows::brightness::BrightnessTarget,
        fence: Arc<dyn openblade_platform_windows::brightness::SubmissionFence>,
        canceled: &AtomicBool,
    ) -> io::Result<
        Result<
            openblade_platform_windows::brightness::BrightnessOutcome,
            openblade_platform_windows::brightness::BrightnessFailure,
        >,
    > {
        use openblade_platform_windows::brightness::{BrightnessFailure, BrightnessRequest};
        if self.closed {
            return Err(io::Error::other("brightness owner is closed"));
        }
        let unavailable = |reason| BrightnessFailure {
            reason,
            submission_may_have_occurred: false,
            state: None,
        };
        if self.worker.is_none() {
            // A worker that never started submitted nothing; only a lost reply
            // from an existing worker reports WorkerDisconnected (fatal).
            self.worker = match (self.start)() {
                Err(error) => return Ok(Err(unavailable(BrightnessError::Native(error)))),
                Ok(Ok(worker)) => Some(worker),
                Ok(Err(_)) => return Ok(Err(unavailable(BrightnessError::ControllerUnavailable))),
            };
        }
        let result = self
            .worker
            .as_mut()
            .expect("owned worker")
            .apply_target_fenced(
                BrightnessRequest::SetPercent(percent),
                target,
                fence,
                || canceled.load(Ordering::Acquire),
            );
        Ok(result)
    }

    /// Join the current worker and let the next request start a fresh one.
    /// Only called when no request is pending on it (a completed read).
    #[cfg(feature = "agent-display-brightness-write-v1")]
    pub(super) fn retire_worker(&mut self) -> io::Result<()> {
        match self.worker.take() {
            Some(worker) => worker.shutdown(),
            None => Ok(()),
        }
    }

    pub(super) fn shutdown(&mut self) -> io::Result<()> {
        self.closed = true;
        match self.worker.take() {
            Some(worker) => worker.shutdown(),
            None => Ok(()),
        }
    }

    /// A test owner never reaches the Windows starter: it refuses to replace
    /// its worker.
    #[cfg(test)]
    pub(super) fn with_worker(worker: WindowsBrightnessWorker) -> Self {
        Self {
            worker: Some(worker),
            closed: false,
            start: Box::new(|| Ok(Err(io::Error::other("test owner cannot start a worker")))),
        }
    }

    /// A test owner that never starts a worker, for writes that read no brightness.
    #[cfg(all(test, openblade_display_manual_write))]
    pub(super) fn inert() -> Self {
        Self {
            worker: None,
            closed: false,
            start: Box::new(|| Ok(Err(io::Error::other("test owner cannot start a worker")))),
        }
    }

    /// A test owner whose replacement workers come from `start`.
    #[cfg(all(test, feature = "agent-display-brightness-write-v1"))]
    pub(super) fn with_starter(
        mut start: impl FnMut() -> io::Result<WindowsBrightnessWorker> + Send + 'static,
    ) -> Self {
        Self {
            worker: None,
            closed: false,
            start: Box::new(move || Ok(start())),
        }
    }
}

pub(super) async fn drain_brightness(owner: SharedBrightness) -> io::Result<()> {
    tokio::task::spawn_blocking(move || {
        // A poisoned presentation-side lock must not skip the owned join.
        owner
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner)
            .shutdown()
    })
    .await
    .map_err(|_| io::Error::other("brightness owner drain failed"))?
}

struct WindowsDisplayReads {
    brightness: SharedBrightness,
}

impl DisplayReads for WindowsDisplayReads {
    fn brightness(
        &mut self,
        panel: &ActiveInternalDisplay,
        canceled: &AtomicBool,
    ) -> io::Result<Result<BrightnessSnapshot, BrightnessError>> {
        let target = match BrightnessQueryTarget::from_display(panel) {
            Ok(target) => target,
            Err(error) => return Ok(Err(error)),
        };
        self.brightness
            .lock()
            .map_err(|_| io::Error::other("brightness owner state failed"))?
            .read(target, canceled)
    }

    fn refresh(&mut self) -> Result<InternalDisplayRefresh, DisplayReadError> {
        read_internal_display_refresh()
    }

    fn topology(&mut self) -> Result<InternalDisplayTopology, DisplayReadError> {
        read_internal_display()
    }

    fn profiles(
        &mut self,
        panel: &ActiveInternalDisplay,
        hdr: HdrState,
    ) -> Result<ColorProfileSnapshot, ProfileReadError> {
        query_color_profiles(panel, hdr)
    }
}

pub(super) fn query(canceled: &AtomicBool, brightness: SharedBrightness) -> io::Result<Snapshot> {
    observe(&mut WindowsDisplayReads { brightness }, canceled)
}

fn check_cancellation(canceled: &AtomicBool) -> io::Result<()> {
    if canceled.load(Ordering::Acquire) {
        Err(io::ErrorKind::Interrupted.into())
    } else {
        Ok(())
    }
}

fn observe(reads: &mut impl DisplayReads, canceled: &AtomicBool) -> io::Result<Snapshot> {
    check_cancellation(canceled)?;
    let refresh = reads.refresh();
    check_cancellation(canceled)?;
    let refresh = match refresh {
        Ok(InternalDisplayRefresh::Single(refresh)) => refresh,
        Ok(InternalDisplayRefresh::Empty) => {
            return Ok(Snapshot::unavailable(
                "No active display topology is available.",
            ));
        }
        Ok(InternalDisplayRefresh::NoInternalDisplay) => {
            return Ok(Snapshot::unavailable(
                "No active internal display was found.",
            ));
        }
        Ok(InternalDisplayRefresh::MultipleInternalDisplays) => {
            return Ok(Snapshot::unavailable(
                "Multiple active internal displays were found.",
            ));
        }
        Err(_) => {
            return Ok(Snapshot::unavailable(
                "Windows could not read the internal display.",
            ));
        }
    };
    let display = &refresh.display;
    let identity = display
        .monitor_name
        .as_ref()
        .ok()
        .and_then(|name| name.device_path.as_deref())
        .and_then(|path| path.to_str());
    if identity.is_none_or(|path| path.trim().is_empty()) {
        return Ok(Snapshot::unavailable(
            "Windows could not identify the internal display.",
        ));
    }

    // Each CCD read resolves the target from its own path/mode arrays. Compare
    // resolved endpoints, monitor path, source name, timings and HDR, never indices.
    let before = reads.topology();
    check_cancellation(canceled)?;
    let Ok(InternalDisplayTopology::Single(before)) = before else {
        return Ok(Snapshot::unavailable(CHANGED));
    };
    // The v2 HDR fields are non-nullable. With unknown HDR we cannot project
    // an available snapshot or ask WCS for the SDR default.
    let hdr = match &before.advanced_color {
        Ok(state) if state.hdr_user_enabled => HdrState::On,
        Ok(_) => HdrState::Off,
        Err(_) => return Ok(Snapshot::unavailable(HDR_UNKNOWN)),
    };
    if display.advanced_color.is_err() {
        return Ok(Snapshot::unavailable(HDR_UNKNOWN));
    }
    if before != *display {
        return Ok(Snapshot::unavailable(CHANGED));
    }
    let profiles = reads.profiles(&before, hdr);
    check_cancellation(canceled)?;
    let Ok(profiles) = profiles else {
        return Ok(Snapshot::unavailable(
            "Windows could not read the internal display color profiles.",
        ));
    };
    let brightness = reads.brightness(&before, canceled)?;
    check_cancellation(canceled)?;
    if matches!(brightness, Err(BrightnessError::PanelChanged)) {
        return Ok(Snapshot::unavailable(CHANGED));
    }
    let after = reads.topology();
    check_cancellation(canceled)?;
    // WCS reads are separate OS calls. Discard results if the bracketing
    // observations differ; matching observations do not make the calls atomic.
    if !matches!(after, Ok(InternalDisplayTopology::Single(after)) if after == before) {
        return Ok(Snapshot::unavailable(CHANGED));
    }
    Ok(project(*refresh, profiles, brightness.ok())
        .unwrap_or_else(|| Snapshot::unavailable(OUT_OF_BOUNDS)))
}

fn project(
    refresh: RefreshSnapshot,
    profiles: ColorProfileSnapshot,
    brightness: Option<BrightnessSnapshot>,
) -> Option<Snapshot> {
    if refresh.refresh_options.len() > MAX_OPTIONS || profiles.profiles.len() > MAX_OPTIONS {
        return None;
    }
    let monitor = refresh.display.monitor_name.ok()?;
    let display_name = match monitor.friendly_name {
        Some(name) => {
            let name = name.into_string().ok()?;
            if name.trim().is_empty() {
                String::from("Internal display")
            } else {
                name
            }
        }
        None => String::from("Internal display"),
    };
    if !bounded(&display_name, MAX_TEXT) {
        return None;
    }
    let hdr = refresh.display.advanced_color.ok()?;
    let active_color_profile_id = match profiles.selected {
        Some(id) => Some(id.into_string().ok()?),
        None => None,
    };
    let color_profiles = profiles
        .profiles
        .into_iter()
        .map(|profile| {
            Some(ColorProfile {
                id: profile.id.into_string().ok()?,
                name: profile.name,
                is_active: profile.active,
            })
        })
        .collect::<Option<Vec<_>>>()?;
    let snapshot = Snapshot {
        is_available: true,
        display_name: Some(display_name),
        width: i32::try_from(refresh.width).ok()?,
        height: i32::try_from(refresh.height).ok()?,
        current_refresh_key: refresh.current_refresh_key,
        refresh_options: refresh
            .refresh_options
            .into_iter()
            .map(|option| RefreshOption {
                key: option.key,
                name: option.name,
                minimum_hertz: option.minimum_hertz,
                maximum_hertz: option.maximum_hertz,
                desktop_hertz: option.desktop_hertz,
                is_dynamic: option.is_dynamic,
            })
            .collect(),
        hdr_supported: hdr.hdr_supported,
        hdr_enabled: hdr.hdr_user_enabled,
        color_profiles_enabled: profiles.hdr == HdrState::Off,
        active_color_profile_id,
        color_profiles,
        message: refresh.message.map(str::to_owned),
        bits_per_color_channel: hdr.bits_per_color_channel,
        brightness_percent: brightness.as_ref().map(|reading| reading.percent),
        supported_brightness_levels: brightness
            .map(|reading| reading.supported_levels)
            .unwrap_or_default(),
    };
    snapshot.valid().then_some(snapshot)
}

#[cfg(test)]
#[path = "observation_tests.rs"]
mod tests;
#[cfg(test)]
pub(super) use tests::{fixture_query, fixture_query_owned};

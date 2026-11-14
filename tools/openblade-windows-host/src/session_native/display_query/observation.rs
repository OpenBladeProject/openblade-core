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
    sync::atomic::{AtomicBool, Ordering},
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
const HDR_UNKNOWN: &str = "Windows could not confirm HDR state for the internal display.";

fn bounded(value: &str, limit: usize) -> bool {
    value.encode_utf16().take(limit + 1).count() <= limit
}

fn valid_key(key: &str) -> bool {
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

struct WindowsDisplayReads;

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
        if let Err(error) = process_security_status() {
            return Ok(Err(BrightnessError::Native(error)));
        }
        match WindowsBrightnessWorker::start() {
            Ok(worker) => owned_brightness(worker, target, canceled),
            Err(_) => Ok(Err(BrightnessError::Native(
                openblade_platform_windows::brightness::NativeFailure::WorkerDisconnected,
            ))),
        }
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

// Called only on the owned blocking supplier. A timed-out reply is not COM
// retirement: shutdown closes admission, cancels pending work, and joins the
// actual worker including proxy/apartment destruction before returning.
pub(super) fn owned_brightness(
    mut worker: WindowsBrightnessWorker,
    target: BrightnessQueryTarget,
    canceled: &AtomicBool,
) -> io::Result<Result<BrightnessSnapshot, BrightnessError>> {
    let result = worker.read_query_target(target, || canceled.load(Ordering::Acquire));
    worker.shutdown()?;
    Ok(result)
}

pub(super) fn query(canceled: &AtomicBool) -> io::Result<Snapshot> {
    observe(&mut WindowsDisplayReads, canceled)
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
pub(super) use tests::fixture_query;

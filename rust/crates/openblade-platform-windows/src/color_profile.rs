//! ICC/SDR profile observations for an active internal display on the current desktop.

use std::{
    cmp::Ordering,
    ffi::{OsStr, OsString},
    os::windows::ffi::{OsStrExt, OsStringExt},
};

use crate::display::ActiveInternalDisplay;

mod native;
#[cfg(feature = "scripted-display")]
pub mod scripted;
#[cfg(test)]
mod tests;
#[cfg(any(test, feature = "scripted-display"))]
mod transaction;
#[cfg(feature = "display-color-write")]
pub mod write;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum HdrState {
    Off,
    On,
    Unknown,
}

impl HdrState {
    /// The HDR user preference of one CCD observation. A failed
    /// advanced-color read is Unknown, never SDR. A snapshot reads the SDR
    /// default, and the color writer selects a profile, only under confirmed
    /// SDR. The writer's restoration after a possible submission is the one
    /// exception: after a readback under known HDR on it reads the
    /// current-user state and restores the default, the association it added
    /// and the scope to their baseline; under Unknown it writes nothing.
    pub fn of(display: &ActiveInternalDisplay) -> Self {
        match &display.advanced_color {
            Ok(state) if state.hdr_user_enabled => Self::On,
            Ok(_) => Self::Off,
            Err(_) => Self::Unknown,
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ProfileScope {
    SystemWide,
    CurrentUser,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ProfileRead {
    Scope,
    Associations,
    Default,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ProfileReadError {
    Native {
        operation: ProfileRead,
        hresult: i32,
    },
    UnknownScope(i32),
    InvalidResponse(ProfileRead),
}

#[derive(Debug, PartialEq, Eq)]
pub struct ColorProfile {
    pub id: OsString,
    pub name: String,
    pub active: bool,
}

#[derive(Debug, PartialEq, Eq)]
pub struct ColorProfileSnapshot {
    pub scope: ProfileScope,
    pub hdr: HdrState,
    pub profiles: Vec<ColorProfile>,
    pub selected: Option<OsString>,
}

/// Read the selected scope and its ICC profiles. HDR is supplied by the caller.
/// Only a confirmed SDR observation permits reading the SDR default.
pub fn query_color_profiles(
    display: &ActiveInternalDisplay,
    hdr: HdrState,
) -> Result<ColorProfileSnapshot, ProfileReadError> {
    snapshot(&mut native::WindowsProfiles, display, hdr)
}

/// The read-only WCS boundary for one display: the selected scope, a scope's
/// associations and standard ICC default, and the vendor presets.
pub trait ProfileReads {
    fn scope(&mut self, display: &ActiveInternalDisplay) -> Result<ProfileScope, ProfileReadError>;
    fn associations(
        &mut self,
        display: &ActiveInternalDisplay,
        scope: ProfileScope,
    ) -> Result<Vec<OsString>, ProfileReadError>;
    fn default_profile(
        &mut self,
        display: &ActiveInternalDisplay,
        scope: ProfileScope,
    ) -> Result<Option<OsString>, ProfileReadError>;
    fn presets(&mut self) -> Vec<OsString>;
}

fn snapshot(
    api: &mut (impl ProfileReads + ?Sized),
    display: &ActiveInternalDisplay,
    hdr: HdrState,
) -> Result<ColorProfileSnapshot, ProfileReadError> {
    let scope = api.scope(display)?;
    let mut candidates = api.associations(display, scope)?;
    candidates.extend(api.presets());
    let default = if hdr == HdrState::Off {
        api.default_profile(display, scope)?
    } else {
        None
    };
    let mut unique: Vec<_> = candidates
        .into_iter()
        .enumerate()
        .filter(|(_, id)| present(id))
        .map(|(index, id)| (index, file_name(&id), id))
        .collect();
    unique.sort_by(|(_, a, _), (_, b, _)| native::ordinal_ignore_case(a, b));
    unique.dedup_by(|(_, a, _), (_, b, _)| native::ordinal_ignore_case(a, b) == Ordering::Equal);
    // Preserve the first observed spelling and the input order of tied labels.
    unique.sort_unstable_by_key(|(index, _, _)| *index);
    let default_leaf = default.as_deref().map(file_name);
    let mut profiles = Vec::with_capacity(unique.len());
    for (_, leaf, id) in unique {
        let active = default_leaf
            .as_deref()
            .is_some_and(|default| native::ordinal_ignore_case(&leaf, default) == Ordering::Equal);
        profiles.push(ColorProfile {
            name: friendly_name(&leaf),
            id,
            active,
        });
    }
    profiles.sort_by(|a, b| {
        sort_order(&a.name)
            .cmp(&sort_order(&b.name))
            .then_with(|| native::compare_labels(&a.name, &b.name))
    });
    let selected = profiles
        .iter()
        .find(|profile| profile.active)
        .map(|profile| profile.id.clone());
    Ok(ColorProfileSnapshot {
        scope,
        hdr,
        profiles,
        selected,
    })
}

fn present(value: &OsStr) -> bool {
    !value.to_string_lossy().trim().is_empty()
}

// Windows Path.GetFileName treats a trailing separator as an empty name and
// only recognizes a drive colon as part of the root. Keep the raw ID unchanged.
fn file_name(value: &OsStr) -> OsString {
    let units: Vec<u16> = value.encode_wide().collect();
    let root = if units.get(1) == Some(&u16::from(b':'))
        && u8::try_from(units[0]).is_ok_and(|drive| drive.is_ascii_alphabetic())
    {
        2
    } else {
        0
    };
    let index = units
        .iter()
        .rposition(|&unit| unit == u16::from(b'/') || unit == u16::from(b'\\'))
        .map_or(root, |index| index + 1);
    OsString::from_wide(&units[index..])
}

/// Managed `ProfilesMatch`: the same ID, or the same Windows file name,
/// compared ordinally ignoring case.
#[cfg(any(test, feature = "scripted-display", feature = "display-color-write"))]
pub fn profiles_match(left: &OsStr, right: &OsStr) -> bool {
    profile_ids_equal(left, right) || profile_ids_equal(&file_name(left), &file_name(right))
}

/// Managed `string.Equals(left, right, StringComparison.OrdinalIgnoreCase)`
/// of two whole profile IDs, without the file-name equivalence of
/// [`profiles_match`]. Managed `DisplayProfileApplier` compares this way.
#[cfg(any(test, feature = "scripted-display", feature = "display-color-write"))]
pub fn profile_ids_equal(left: &OsStr, right: &OsStr) -> bool {
    native::ordinal_ignore_case(left, right) == Ordering::Equal
}

/// One `EnumDisplayDevicesW(EDD_GET_DEVICE_INTERFACE_NAME)` monitor of a GDI
/// source: managed `DisplayMonitorDeviceIdentity`.
#[cfg(any(test, feature = "scripted-display", feature = "display-color-write"))]
pub struct Monitor {
    pub interface: OsString,
    pub key: OsString,
    pub active: bool,
}

/// Why managed `GetMonitorDeviceKey` found no WCS device key for the panel.
#[cfg(any(test, feature = "scripted-display", feature = "display-color-write"))]
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum MonitorKeyError {
    /// The CCD target reported no monitor device path.
    NoDevicePath,
    NoActiveMonitor,
    MultipleActiveMonitors,
    /// The one matching monitor reported a blank device key.
    NoDeviceKey,
}

/// The panel's monitor device path, which identifies its WCS monitor.
#[cfg(any(test, feature = "scripted-display", feature = "display-color-write"))]
fn monitor_path(display: &ActiveInternalDisplay) -> Option<&OsStr> {
    display.monitor_name.as_ref().ok()?.device_path.as_deref()
}

/// Managed `DisplayMonitorDeviceKeyResolver.Resolve`: exactly one active
/// monitor whose interface name equals the path (ordinal, ignoring case),
/// with a non-blank device key.
#[cfg(any(test, feature = "scripted-display", feature = "display-color-write"))]
fn resolve_monitor_key(path: &OsStr, monitors: Vec<Monitor>) -> Result<OsString, MonitorKeyError> {
    let mut matches = monitors.into_iter().filter(|monitor| {
        monitor.active && native::ordinal_ignore_case(&monitor.interface, path) == Ordering::Equal
    });
    let first = matches.next().ok_or(MonitorKeyError::NoActiveMonitor)?;
    if matches.next().is_some() {
        return Err(MonitorKeyError::MultipleActiveMonitors);
    }
    if !present(&first.key) {
        return Err(MonitorKeyError::NoDeviceKey);
    }
    Ok(first.key)
}

fn friendly_name(leaf: &OsStr) -> String {
    let leaf = leaf.to_string_lossy();
    let name = leaf
        .rsplit_once('.')
        .map_or(leaf.as_ref(), |(stem, _)| stem);
    let normalized = name.replace(['_', '-', '.', ' '], "").to_uppercase();
    match normalized.as_str() {
        value if value.contains("DCIP3") || value.contains("DISPLAYP3") => "DCI-P3",
        value if value.contains("SRGB") => "sRGB",
        value if value.contains("REC709") || value.contains("BT709") => "Rec. 709",
        value if value.contains("ADOBERGB") => "Adobe RGB",
        value if value.contains("NATIVE") => "Native",
        "BLADE" => "Factory calibration",
        _ => name,
    }
    .to_owned()
}

fn sort_order(name: &str) -> u8 {
    match name {
        "Native" => 0,
        "DCI-P3" => 1,
        "sRGB" => 2,
        "Rec. 709" => 3,
        "Adobe RGB" => 4,
        _ => 5,
    }
}

fn valid_monitor_rgb_header(header: &[u8; 128]) -> bool {
    &header[12..16] == b"mntr" && &header[16..20] == b"RGB " && &header[36..40] == b"acsp"
}

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

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum HdrState {
    Off,
    On,
    Unknown,
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

trait ProfileReads {
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
    api: &mut impl ProfileReads,
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

#[cfg(any(test, feature = "scripted-display"))]
fn profiles_match(left: &OsStr, right: &OsStr) -> bool {
    native::ordinal_ignore_case(left, right) == Ordering::Equal
        || native::ordinal_ignore_case(&file_name(left), &file_name(right)) == Ordering::Equal
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

//! Read-only refresh projection of the managed v2 QueryDisplay snapshot.

use super::*;
use std::{collections::BTreeSet, ffi::OsStr, os::windows::ffi::OsStrExt};
use windows_sys::Win32::{
    Devices::Display::{
        DISPLAYCONFIG_SCANLINE_ORDERING_PROGRESSIVE, DISPLAYCONFIG_SCANLINE_ORDERING_UNSPECIFIED,
        DISPLAYCONFIG_TARGET_MODE,
    },
    Graphics::Gdi::ENUM_CURRENT_SETTINGS,
};

pub(super) mod edid;
#[cfg(test)]
pub(super) mod tests;

const MAX_DRIVER_MODES: u32 = 4096;

impl SignalTiming {
    pub(super) fn matches_size(self, width: u32, height: u32) -> bool {
        (self.width, self.height) == (width, height) && width != 0 && height != 0
    }

    pub(super) fn is_progressive(self) -> bool {
        matches!(
            self.scanline_ordering,
            DISPLAYCONFIG_SCANLINE_ORDERING_UNSPECIFIED
                | DISPLAYCONFIG_SCANLINE_ORDERING_PROGRESSIVE
        )
    }
}

#[derive(Debug, PartialEq, Eq)]
pub enum InternalDisplayRefresh {
    Empty,
    NoInternalDisplay,
    MultipleInternalDisplays,
    Single(Box<RefreshSnapshot>),
}

/// The refresh fields and their supporting observations. HDR, color profiles,
/// and brightness require separate reads before building a v2 response.
#[derive(Debug, PartialEq, Eq)]
pub struct RefreshSnapshot {
    pub display: ActiveInternalDisplay,
    pub width: u32,
    pub height: u32,
    /// GDI's current DEVMODE rate is independent of the two CCD rates.
    pub current_mode_hertz: u32,
    pub current_refresh_key: Option<String>,
    pub message: Option<&'static str>,
    pub refresh_options: Vec<RefreshOption>,
    pub preferred_rate: TimingEvidence<RefreshRate>,
    pub edid_range: TimingEvidence<VerticalRange>,
}

#[derive(Debug, PartialEq, Eq)]
pub enum TimingEvidence<T> {
    /// The path did not support this query or lacked an admitted lookup key.
    NotQueried,
    /// The read succeeded without usable evidence of this kind.
    Unavailable,
    Observed(T),
    NativeFailure(u32),
}

impl<T> TimingEvidence<T> {
    fn from_read<U>(read: Option<Result<U, u32>>, value: Option<T>) -> Self {
        match read {
            None => Self::NotQueried,
            Some(Err(status)) => Self::NativeFailure(status),
            Some(Ok(_)) => value.map_or(Self::Unavailable, Self::Observed),
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RefreshOption {
    pub key: String,
    pub name: String,
    pub minimum_hertz: i32,
    pub maximum_hertz: i32,
    pub desktop_hertz: i32,
    pub is_dynamic: bool,
}

impl RefreshOption {
    fn fixed(hertz: i32) -> Self {
        Self {
            key: format!("fixed-{hertz}"),
            name: format!("{hertz} Hz"),
            minimum_hertz: hertz,
            maximum_hertz: hertz,
            desktop_hertz: hertz,
            is_dynamic: false,
        }
    }

    fn dynamic(minimum: i32, maximum: i32, desktop: i32) -> Self {
        Self {
            key: format!("dynamic-{minimum}-{maximum}"),
            name: format!("Dynamic ({minimum}-{maximum} Hz)"),
            minimum_hertz: minimum,
            maximum_hertz: maximum,
            desktop_hertz: desktop,
            is_dynamic: true,
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct VerticalRange {
    pub minimum_hertz: u8,
    pub maximum_hertz: u8,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(super) struct DriverMode {
    pub(super) width: u32,
    pub(super) height: u32,
    pub(super) hertz: u32,
    pub(super) interlaced: bool,
}

pub(super) trait RefreshApi: DisplayApi {
    fn mode(&mut self, source: &[u16], index: u32) -> Option<DriverMode>;
    fn preferred_mode(&mut self, target: EndpointId) -> Result<DISPLAYCONFIG_TARGET_MODE, u32>;
    fn edid(&mut self, subkey: &[u16]) -> Result<Vec<u8>, u32>;
}

pub(super) fn read_refresh_with(
    api: &mut impl RefreshApi,
) -> Result<InternalDisplayRefresh, DisplayReadError> {
    observe_refresh(api, None, |_, _, _| {})
}

// Supplies the last CCD frame and the timing evidence from the bracketed read.
// Mutation preparation uses those arrays together, never an index from another query.
// `requested` is the key of a mutation observation (managed `BuildSnapshot`
// with a requested key) and `None` for an ordinary read. A mutation also reads
// the preferred timing on a non-virtual path, as managed `FindFixedTargetMode`
// does, and offers an exact requested Dynamic maximum besides the default one.
pub(super) fn observe_refresh(
    api: &mut impl RefreshApi,
    requested: Option<&str>,
    evidence: impl FnOnce(Topology, Option<DISPLAYCONFIG_TARGET_MODE>, Vec<edid::DetailedTiming>),
) -> Result<InternalDisplayRefresh, DisplayReadError> {
    let display = match read_with(api)? {
        InternalDisplayTopology::Empty => return Ok(InternalDisplayRefresh::Empty),
        InternalDisplayTopology::NoInternalDisplay => {
            return Ok(InternalDisplayRefresh::NoInternalDisplay);
        }
        InternalDisplayTopology::MultipleInternalDisplays => {
            return Ok(InternalDisplayRefresh::MultipleInternalDisplays);
        }
        InternalDisplayTopology::Single(display) => display,
    };
    let source: Vec<_> = display.source_name.encode_wide().chain([0]).collect();
    let current = api
        .mode(&source, ENUM_CURRENT_SETTINGS)
        .ok_or(DisplayReadError::CurrentModeUnavailable)?;
    if !(1..=i32::MAX as u32).contains(&current.width)
        || !(1..=i32::MAX as u32).contains(&current.height)
        || current.hertz > i32::MAX as u32
    {
        return Err(DisplayReadError::InvalidCurrentMode);
    }
    let driver_rates = driver_rates(api, &source, current)?;
    let active = display.active_signal;
    let preferred = (display.supports_virtual_mode || requested.is_some())
        .then(|| api.preferred_mode(display.target));
    let preferred_mode = preferred.and_then(Result::ok).filter(|mode| {
        accept_preferred(active, native::signal_timing(mode.targetVideoSignalInfo)).is_some()
    });
    let preferred_timing = preferred_mode
        .filter(|_| display.supports_virtual_mode)
        .map(|mode| native::signal_timing(mode.targetVideoSignalInfo));
    let preferred_rate = preferred_timing.and_then(|timing| timing.rate);
    let edid = display
        .monitor_name
        .as_ref()
        .ok()
        .and_then(|name| name.device_path.as_deref())
        .and_then(monitor_subkey)
        .map(|subkey| api.edid(&subkey));
    // The Windows reads are separate. Reject evidence gathered across an
    // observed topology change. This does not make the OS calls atomic.
    if api.mode(&source, ENUM_CURRENT_SETTINGS) != Some(current) {
        return Err(DisplayReadError::TopologyChanging);
    }
    let topology = query_topology(api)?;
    if !matches!(resolve_internal_display(api, &topology.paths, &topology.modes)?, InternalDisplayTopology::Single(next) if next == display)
    {
        return Err(DisplayReadError::TopologyChanging);
    }
    let bytes = edid
        .as_ref()
        .and_then(|read| read.as_deref().ok())
        .unwrap_or_default();
    let range = edid::vertical_range(bytes);
    let timings = edid::detailed_timings(bytes)?;
    for rate in [display.desktop_rate, display.physical_rate, preferred_rate]
        .into_iter()
        .flatten()
    {
        if normalize(Some(rate)).is_none() {
            return Err(DisplayReadError::RefreshRateOverflow);
        }
    }
    let refresh_options = build_options(
        &display,
        current,
        &driver_rates,
        preferred_timing,
        &timings,
        range,
        requested,
    );
    let current_refresh_key = classify(&display, &refresh_options).map(|option| option.key.clone());

    let display_supports_virtual_mode = display.supports_virtual_mode;
    evidence(topology, preferred_mode, timings);
    Ok(InternalDisplayRefresh::Single(Box::new(RefreshSnapshot {
        display,
        width: current.width,
        height: current.height,
        current_mode_hertz: current.hertz,
        message: current_refresh_key
            .is_none()
            .then_some("The current refresh-rate mode was not reported as available by Windows."),
        current_refresh_key,
        refresh_options,
        preferred_rate: TimingEvidence::from_read(
            preferred.filter(|_| display_supports_virtual_mode),
            preferred_rate,
        ),
        edid_range: TimingEvidence::from_read(edid, range),
    })))
}

fn driver_rates(
    api: &mut impl RefreshApi,
    source: &[u16],
    current: DriverMode,
) -> Result<BTreeSet<i32>, DisplayReadError> {
    let mut rates = BTreeSet::new();
    for index in 0..MAX_DRIVER_MODES {
        // EnumDisplaySettingsExW defines FALSE as the enumeration terminator.
        let Some(mode) = api.mode(source, index) else {
            return Ok(rates);
        };
        if (mode.width, mode.height) == (current.width, current.height)
            && (20..=1000).contains(&mode.hertz)
            && !mode.interlaced
        {
            rates.insert(mode.hertz as i32);
        }
    }
    if api.mode(source, MAX_DRIVER_MODES).is_none() {
        Ok(rates)
    } else {
        Err(DisplayReadError::DriverModeLimit)
    }
}

fn build_options(
    display: &ActiveInternalDisplay,
    current: DriverMode,
    driver_rates: &BTreeSet<i32>,
    preferred: Option<SignalTiming>,
    timings: &[edid::DetailedTiming],
    range: Option<VerticalRange>,
    requested: Option<&str>,
) -> Vec<RefreshOption> {
    // Mirrors the ordinary read path in BuildRefreshOptions. GDI's current
    // frequency can support visibility, but cannot classify the CCD pair.
    let physical = normalize(display.physical_rate);
    let preferred_hertz = preferred.and_then(|timing| normalize(timing.rate));
    let mut advertised: BTreeSet<_> = timings
        .iter()
        .filter(|timing| (timing.width, timing.height) == (current.width, current.height))
        .map(|timing| timing.hertz)
        .collect();
    advertised.insert(current.hertz as i32);
    advertised.extend(physical);
    advertised.extend(preferred_hertz);
    let fixed = select_fixed_rates(driver_rates, &advertised);
    let mut options: Vec<_> = fixed.iter().copied().map(RefreshOption::fixed).collect();
    if !display.supports_virtual_mode {
        return options;
    }

    // Managed SelectDynamicTargetModes accepts a progressive active or
    // preferred timing directly. EDID alternatives also need a GDI mode and
    // a representable signal at the active target size.
    let active = display.active_signal;
    let mut maxima = BTreeSet::new();
    if active.is_progressive() {
        maxima.extend(physical.filter(|&rate| rate >= 120));
    }
    maxima.extend(preferred_hertz.filter(|&rate| rate >= 120));
    maxima.extend(
        timings
            .iter()
            .filter(|timing| {
                timing.hertz >= 120
                    && driver_rates.contains(&timing.hertz)
                    && active.is_progressive()
                    && active.matches_size(timing.width, timing.height)
                    && timing.ratios_fit()
            })
            .map(|timing| timing.hertz),
    );

    let current_maximum =
        physical.filter(|rate| display.boost_refresh_rate && maxima.contains(rate));
    let candidates = current_maximum.into_iter().chain(
        maxima
            .into_iter()
            .rev()
            .filter(|rate| Some(*rate) != current_maximum),
    );
    // Managed AddDynamicRefreshOptions: one default Dynamic choice, plus an
    // exact requested one while its timing is still supported.
    let mut added_default = false;
    for maximum in candidates {
        let Some(&desktop) = fixed.iter().find(|&&rate| rate >= 60 && rate < maximum) else {
            continue;
        };
        let minimum = range
            .filter(|range| i32::from(range.maximum_hertz) >= maximum)
            .map_or(desktop, |range| i32::from(range.minimum_hertz));
        let option = RefreshOption::dynamic(minimum, maximum, desktop);
        let matched = requested == Some(option.key.as_str());
        if !added_default || matched {
            options.push(option);
            added_default = true;
        }
        if matched || requested.is_none() {
            break;
        }
    }
    options
}

fn accept_preferred(active: SignalTiming, candidate: SignalTiming) -> Option<SignalTiming> {
    (candidate.matches_size(active.width, active.height)
        && candidate.is_progressive()
        && candidate.rate.is_some())
    .then_some(candidate)
}

fn select_fixed_rates(driver: &BTreeSet<i32>, advertised: &BTreeSet<i32>) -> Vec<i32> {
    driver.intersection(advertised).copied().collect()
}

pub(super) fn normalize(rate: Option<RefreshRate>) -> Option<i32> {
    let rate = rate?;
    let hertz = (f64::from(rate.numerator) / f64::from(rate.denominator)).round();
    (hertz <= f64::from(i32::MAX)).then_some(hertz as i32)
}

pub(super) fn classify<'a>(
    display: &ActiveInternalDisplay,
    options: &'a [RefreshOption],
) -> Option<&'a RefreshOption> {
    let desktop = normalize(display.desktop_rate)?;
    let physical = normalize(display.physical_rate)?;
    options.iter().find(|option| {
        option.is_dynamic == display.boost_refresh_rate
            && option.desktop_hertz == desktop
            && option.maximum_hertz == physical
    })
}

fn monitor_subkey(path: &OsStr) -> Option<Vec<u16>> {
    let units: Vec<_> = path.encode_wide().collect();
    let mut parts = units.split(|&unit| unit == u16::from(b'#'));
    let prefix = parts.next()?;
    let hardware = parts.next()?;
    let instance = parts.next()?;
    let display = b"DISPLAY";
    let suffix = prefix.get(prefix.len().checked_sub(display.len())?..)?;
    if !suffix.iter().zip(display).all(|(&unit, &ascii)| {
        u8::try_from(unit).is_ok_and(|byte| byte.eq_ignore_ascii_case(&ascii))
    }) || [hardware, instance]
        .iter()
        .any(|part| part.is_empty() || part.iter().any(|&unit| matches!(unit, 0 | 47 | 92)))
    {
        return None;
    }
    Some(
        "SYSTEM\\CurrentControlSet\\Enum\\DISPLAY\\"
            .encode_utf16()
            .chain(hardware.iter().copied())
            .chain([u16::from(b'\\')])
            .chain(instance.iter().copied())
            .chain("\\Device Parameters".encode_utf16())
            .chain([0])
            .collect(),
    )
}

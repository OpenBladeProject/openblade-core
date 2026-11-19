//! CCD packet preparation for the refresh setters: the private scripted
//! fixture and the production `refresh_write` writer share it.
#![allow(unsafe_code)]

use super::{
    refresh::{edid, normalize},
    *,
};
use windows_sys::Win32::{
    Devices::Display::{
        DISPLAYCONFIG_2DREGION, DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE, DISPLAYCONFIG_TARGET_MODE,
        DISPLAYCONFIG_VIDEO_SIGNAL_INFO, SDC_APPLY, SDC_SAVE_TO_DATABASE,
        SDC_USE_SUPPLIED_DISPLAY_CONFIG, SDC_VALIDATE, SDC_VIRTUAL_MODE_AWARE,
        SDC_VIRTUAL_REFRESH_RATE_AWARE,
    },
    Graphics::Gdi::{
        DISPLAYCONFIG_PATH_MODE_IDX_INVALID, DISPLAYCONFIG_PATH_SOURCE_MODE_IDX_INVALID,
        DISPLAYCONFIG_PATH_TARGET_MODE_IDX_INVALID,
    },
};

const COMMON: u32 =
    SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_VIRTUAL_MODE_AWARE | SDC_VIRTUAL_REFRESH_RATE_AWARE;
pub(super) const VALIDATE: u32 = COMMON | SDC_VALIDATE;
pub(super) const APPLY: u32 = COMMON | SDC_APPLY | SDC_SAVE_TO_DATABASE;

/// Why a SetDisplayConfig request could not be built from one CCD frame.
/// Every variant precedes validation and submission.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PrepareError {
    /// Managed `PrepareRefreshTopology`: the admitted path, its source mode
    /// or its target mode is no longer in the frame.
    PathChanged,
    /// Managed `PrepareRefreshPath`: no admitted physical timing for the
    /// requested Dynamic maximum.
    DynamicTimingUnavailable,
    /// The option's desktop rate is not representable (managed `checked`).
    InvalidOption,
}

pub(super) struct RefreshPacket {
    topology: Topology,
    path: usize,
    target: EndpointId,
}

impl RefreshPacket {
    pub(super) fn prepare(
        mut topology: Topology,
        display: &ActiveInternalDisplay,
        option: &RefreshOption,
        preferred: Option<DISPLAYCONFIG_TARGET_MODE>,
        timings: &[edid::DetailedTiming],
    ) -> Result<Self, PrepareError> {
        use PrepareError::{DynamicTimingUnavailable, InvalidOption, PathChanged};
        let mut matches = topology.paths.iter().enumerate().filter(|(_, path)| {
            path.flags & DISPLAYCONFIG_PATH_ACTIVE != 0
                && EndpointId::new(path.sourceInfo.adapterId, path.sourceInfo.id) == display.source
                && EndpointId::new(path.targetInfo.adapterId, path.targetInfo.id) == display.target
        });
        let (index, path) = matches.next().ok_or(PathChanged)?;
        if matches.next().is_some() || !valid_source(path, &topology.modes, display.source) {
            return Err(PathChanged);
        }

        let mode_index = native::target_mode_index(path, &topology.modes, display.target)
            .map_err(|_| PathChanged)?;
        // SAFETY: target_mode_index checked the type and endpoint for this frame.
        let active = unsafe { topology.modes[mode_index].Anonymous.targetMode };
        let timing = native::signal_timing(active.targetVideoSignalInfo);
        let wanted = |mode: DISPLAYCONFIG_TARGET_MODE| {
            normalize(RefreshRate::observed(mode.targetVideoSignalInfo.vSyncFreq))
                == Some(option.maximum_hertz)
        };
        let selected = Some(active)
            .filter(|mode| wanted(*mode) && (!option.is_dynamic || timing.is_progressive()))
            .or_else(|| preferred.filter(|mode| wanted(*mode)))
            .or_else(|| {
                timings
                    .iter()
                    .filter(|candidate| candidate.hertz == option.maximum_hertz)
                    .find_map(|candidate| detailed_mode(active.targetVideoSignalInfo, *candidate))
            });

        let path = &mut topology.paths[index];
        path.targetInfo.refreshRate = DISPLAYCONFIG_RATIONAL {
            Numerator: u32::try_from(option.desktop_hertz).map_err(|_| InvalidOption)?,
            Denominator: 1,
        };
        if option.is_dynamic {
            if path.flags & DISPLAYCONFIG_PATH_SUPPORT_VIRTUAL_MODE == 0 {
                return Err(DynamicTimingUnavailable);
            }
            path.flags |= PATH_BOOST_REFRESH_RATE;
        } else {
            path.flags &= !PATH_BOOST_REFRESH_RATE;
        }
        if let Some(mode) = selected {
            topology.modes[mode_index].Anonymous.targetMode = mode;
            if !option.is_dynamic {
                path.targetInfo.refreshRate = mode.targetVideoSignalInfo.vSyncFreq;
            }
        } else if option.is_dynamic {
            return Err(DynamicTimingUnavailable);
        } else {
            // Omitting just the target mode lets Windows select the signal.
            // The source and virtual desktop image indices belong to this frame.
            let word = target_word(path);
            path.targetInfo.Anonymous.modeInfoIdx =
                if path.flags & DISPLAYCONFIG_PATH_SUPPORT_VIRTUAL_MODE != 0 {
                    (DISPLAYCONFIG_PATH_TARGET_MODE_IDX_INVALID << 16) | (word & 0xffff)
                } else {
                    DISPLAYCONFIG_PATH_MODE_IDX_INVALID
                };
        }
        Ok(Self {
            topology,
            path: index,
            target: display.target,
        })
    }

    #[cfg(test)]
    pub(super) fn index_words(&self) -> (u32, u32) {
        let path = self.selected_path();
        (source_word(path), target_word(path))
    }

    pub(super) fn selected_path(&self) -> &DISPLAYCONFIG_PATH_INFO {
        &self.topology.paths[self.path]
    }

    pub(super) fn selected_mode(&self) -> Option<DISPLAYCONFIG_TARGET_MODE> {
        let index =
            native::target_mode_index(self.selected_path(), &self.topology.modes, self.target)
                .ok()?;
        // SAFETY: target_mode_index verified the discriminant and target.
        Some(unsafe { self.topology.modes[index].Anonymous.targetMode })
    }
}

// The production submissions, through the one native SetDisplayConfig call
// site. Unit-test builds exclude them so no test can validate or apply a
// configuration.
#[cfg(all(feature = "display-refresh-write", not(test)))]
impl RefreshPacket {
    /// `SDC_VALIDATE`: Windows checks the supplied configuration only.
    pub(super) fn validate(&self) -> u32 {
        native::validate_display_config(&self.topology.paths, &self.topology.modes)
    }

    /// The one `SDC_APPLY | SDC_SAVE_TO_DATABASE` submission.
    pub(super) fn apply(&self) -> u32 {
        native::apply_display_config(&self.topology.paths, &self.topology.modes)
    }
}

fn target_word(path: &DISPLAYCONFIG_PATH_INFO) -> u32 {
    // SAFETY: Both CCD target index layouts occupy one initialized word.
    unsafe { path.targetInfo.Anonymous.modeInfoIdx }
}

fn source_word(path: &DISPLAYCONFIG_PATH_INFO) -> u32 {
    // SAFETY: Both CCD source index layouts occupy one initialized word.
    unsafe { path.sourceInfo.Anonymous.modeInfoIdx }
}

fn valid_source(
    path: &DISPLAYCONFIG_PATH_INFO,
    modes: &[DISPLAYCONFIG_MODE_INFO],
    source: EndpointId,
) -> bool {
    let word = source_word(path);
    let (index, invalid) = if path.flags & DISPLAYCONFIG_PATH_SUPPORT_VIRTUAL_MODE != 0 {
        (word >> 16, DISPLAYCONFIG_PATH_SOURCE_MODE_IDX_INVALID)
    } else {
        (word, DISPLAYCONFIG_PATH_MODE_IDX_INVALID)
    };
    index != invalid
        && modes.get(index as usize).is_some_and(|mode| {
            mode.infoType == DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE
                && EndpointId::new(mode.adapterId, mode.id) == source
        })
}

fn detailed_mode(
    mut signal: DISPLAYCONFIG_VIDEO_SIGNAL_INFO,
    timing: edid::DetailedTiming,
) -> Option<DISPLAYCONFIG_TARGET_MODE> {
    let active = native::signal_timing(signal);
    if !active.is_progressive() || !active.matches_size(timing.width, timing.height) {
        return None;
    }
    signal.pixelRate = timing.pixel_rate;
    signal.hSyncFreq = edid::rational(timing.pixel_rate, u64::from(timing.total_width))?;
    signal.vSyncFreq = edid::rational(
        timing.pixel_rate,
        u64::from(timing.total_width) * u64::from(timing.total_height),
    )?;
    signal.totalSize = DISPLAYCONFIG_2DREGION {
        cx: timing.total_width,
        cy: timing.total_height,
    };
    Some(DISPLAYCONFIG_TARGET_MODE {
        targetVideoSignalInfo: signal,
    })
}

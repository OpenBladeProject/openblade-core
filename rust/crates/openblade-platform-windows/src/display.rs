//! Active internal display observations for the current Windows desktop.

use std::{ffi::OsString, os::windows::ffi::OsStringExt};
use windows_sys::Win32::{
    Devices::Display::{
        DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME, DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
        DISPLAYCONFIG_DEVICE_INFO_HEADER, DISPLAYCONFIG_MODE_INFO,
        DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_EMBEDDED,
        DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL, DISPLAYCONFIG_OUTPUT_TECHNOLOGY_LVDS,
        DISPLAYCONFIG_OUTPUT_TECHNOLOGY_UDI_EMBEDDED, DISPLAYCONFIG_PATH_INFO,
        DISPLAYCONFIG_RATIONAL, DISPLAYCONFIG_SOURCE_DEVICE_NAME, DISPLAYCONFIG_TARGET_DEVICE_NAME,
        QDC_ONLY_ACTIVE_PATHS, QDC_VIRTUAL_MODE_AWARE, QDC_VIRTUAL_REFRESH_RATE_AWARE,
    },
    Foundation::{ERROR_INSUFFICIENT_BUFFER, ERROR_SUCCESS, LUID},
    Graphics::Gdi::{DISPLAYCONFIG_PATH_ACTIVE, DISPLAYCONFIG_PATH_SUPPORT_VIRTUAL_MODE},
};

mod native;
#[cfg(test)]
mod tests;

const QUERY_FLAGS: u32 =
    QDC_ONLY_ACTIVE_PATHS | QDC_VIRTUAL_MODE_AWARE | QDC_VIRTUAL_REFRESH_RATE_AWARE;
const MAX_ATTEMPTS: usize = 5;
// Bound allocation even if the driver reports implausible counts.
const MAX_ENTRIES: u32 = 4096;

// WinSDK wingdi.h defines this flag; windows-sys 0.61.2 does not export it.
const PATH_BOOST_REFRESH_RATE: u32 = 0x10;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct EndpointId {
    pub adapter_low: u32,
    pub adapter_high: i32,
    pub id: u32,
}

impl EndpointId {
    fn new(adapter: LUID, id: u32) -> Self {
        Self {
            adapter_low: adapter.LowPart,
            adapter_high: adapter.HighPart,
            id,
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct RefreshRate {
    pub numerator: u32,
    pub denominator: u32,
}

impl RefreshRate {
    fn observed(rate: DISPLAYCONFIG_RATIONAL) -> Option<Self> {
        (rate.Numerator != 0 && rate.Denominator != 0).then_some(Self {
            numerator: rate.Numerator,
            denominator: rate.Denominator,
        })
    }
}

#[derive(Debug, PartialEq, Eq)]
pub struct MonitorName {
    pub friendly_name: Option<OsString>,
    pub device_path: Option<OsString>,
}

#[derive(Debug, PartialEq, Eq)]
pub struct ActiveInternalDisplay {
    pub source: EndpointId,
    pub target: EndpointId,
    pub source_name: OsString,
    /// Target name is best-effort. An error retains the native status.
    pub monitor_name: Result<MonitorName, u32>,
    /// Desktop/virtual rate reported by the path.
    pub desktop_rate: Option<RefreshRate>,
    /// Physical signal rate reported by the matching target mode.
    pub physical_rate: Option<RefreshRate>,
    /// Whether the path uses the virtual-mode-aware layout.
    pub supports_virtual_mode: bool,
    /// The OS path flag for DRR. Rates alone do not establish DRR state.
    pub boost_refresh_rate: bool,
}

#[derive(Debug, PartialEq, Eq)]
pub enum InternalDisplayTopology {
    Empty,
    NoInternalDisplay,
    MultipleInternalDisplays,
    Single(ActiveInternalDisplay),
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Operation {
    BufferSizes,
    ActivePaths,
    SourceName,
}

#[derive(Debug, PartialEq, Eq)]
pub enum DisplayReadError {
    Native { operation: Operation, status: u32 },
    TopologyChanging,
    BufferLimit,
    InvalidReturnedCounts,
    MissingTargetMode,
    InvalidTargetModeIndex,
    MismatchedTargetMode,
    MissingSourceName,
}

/// Read the active topology and resolve the single internal path using the
/// same connector selection as the managed display query. Call from the
/// current-user desktop. Endpoint IDs describe this observation only.
pub fn read_internal_display() -> Result<InternalDisplayTopology, DisplayReadError> {
    read_with(&mut native::WindowsDisplay)
}

trait DisplayApi {
    fn buffer_sizes(&mut self, flags: u32) -> Result<(u32, u32), u32>;

    fn query(
        &mut self,
        flags: u32,
        counts: &mut (u32, u32),
        paths: &mut [DISPLAYCONFIG_PATH_INFO],
        modes: &mut [DISPLAYCONFIG_MODE_INFO],
    ) -> u32;

    fn source_name(&mut self, request: &mut DISPLAYCONFIG_SOURCE_DEVICE_NAME) -> u32;
    fn target_name(&mut self, request: &mut DISPLAYCONFIG_TARGET_DEVICE_NAME) -> u32;
}

fn read_with(api: &mut impl DisplayApi) -> Result<InternalDisplayTopology, DisplayReadError> {
    for _ in 0..MAX_ATTEMPTS {
        let capacities =
            api.buffer_sizes(QUERY_FLAGS)
                .map_err(|status| DisplayReadError::Native {
                    operation: Operation::BufferSizes,
                    status,
                })?;
        if capacities.0 > MAX_ENTRIES || capacities.1 > MAX_ENTRIES {
            return Err(DisplayReadError::BufferLimit);
        }

        // Both pointers must be non-null, even when the advertised counts are zero.
        let mut paths = vec![DISPLAYCONFIG_PATH_INFO::default(); capacities.0.max(1) as usize];
        let mut modes = vec![DISPLAYCONFIG_MODE_INFO::default(); capacities.1.max(1) as usize];
        let mut counts = capacities;
        match api.query(QUERY_FLAGS, &mut counts, &mut paths, &mut modes) {
            ERROR_INSUFFICIENT_BUFFER => continue,
            ERROR_SUCCESS => {}
            status => {
                return Err(DisplayReadError::Native {
                    operation: Operation::ActivePaths,
                    status,
                });
            }
        }

        if counts.0 > capacities.0 || counts.1 > capacities.1 {
            return Err(DisplayReadError::InvalidReturnedCounts);
        }

        return resolve_internal_display(
            api,
            &paths[..counts.0 as usize],
            &modes[..counts.1 as usize],
        );
    }
    Err(DisplayReadError::TopologyChanging)
}

fn resolve_internal_display(
    api: &mut impl DisplayApi,
    paths: &[DISPLAYCONFIG_PATH_INFO],
    modes: &[DISPLAYCONFIG_MODE_INFO],
) -> Result<InternalDisplayTopology, DisplayReadError> {
    if paths.is_empty() {
        return Ok(InternalDisplayTopology::Empty);
    }
    let mut internal = paths.iter().filter(|path| {
        path.flags & DISPLAYCONFIG_PATH_ACTIVE != 0
            && matches!(
                path.targetInfo.outputTechnology,
                DISPLAYCONFIG_OUTPUT_TECHNOLOGY_LVDS
                    | DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_EMBEDDED
                    | DISPLAYCONFIG_OUTPUT_TECHNOLOGY_UDI_EMBEDDED
                    | DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL
            )
    });
    let Some(path) = internal.next() else {
        return Ok(InternalDisplayTopology::NoInternalDisplay);
    };
    if internal.next().is_some() {
        return Ok(InternalDisplayTopology::MultipleInternalDisplays);
    }

    let source = EndpointId::new(path.sourceInfo.adapterId, path.sourceInfo.id);
    let target = EndpointId::new(path.targetInfo.adapterId, path.targetInfo.id);
    let physical_rate = native::physical_rate(path, modes, target)?;
    let mut source_name = DISPLAYCONFIG_SOURCE_DEVICE_NAME {
        header: DISPLAYCONFIG_DEVICE_INFO_HEADER {
            r#type: DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
            size: size_of::<DISPLAYCONFIG_SOURCE_DEVICE_NAME>() as u32,
            adapterId: path.sourceInfo.adapterId,
            id: source.id,
        },
        ..Default::default()
    };
    let status = api.source_name(&mut source_name);
    if status != ERROR_SUCCESS {
        return Err(DisplayReadError::Native {
            operation: Operation::SourceName,
            status,
        });
    }

    Ok(InternalDisplayTopology::Single(ActiveInternalDisplay {
        source,
        target,
        source_name: native_name(&source_name.viewGdiDeviceName)
            .ok_or(DisplayReadError::MissingSourceName)?,
        monitor_name: monitor_name(api, path),
        desktop_rate: RefreshRate::observed(path.targetInfo.refreshRate),
        physical_rate,
        supports_virtual_mode: path.flags & DISPLAYCONFIG_PATH_SUPPORT_VIRTUAL_MODE != 0,
        boost_refresh_rate: path.flags & PATH_BOOST_REFRESH_RATE != 0,
    }))
}

fn monitor_name(
    api: &mut impl DisplayApi,
    path: &DISPLAYCONFIG_PATH_INFO,
) -> Result<MonitorName, u32> {
    let mut name = DISPLAYCONFIG_TARGET_DEVICE_NAME {
        header: DISPLAYCONFIG_DEVICE_INFO_HEADER {
            r#type: DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
            size: size_of::<DISPLAYCONFIG_TARGET_DEVICE_NAME>() as u32,
            adapterId: path.targetInfo.adapterId,
            id: path.targetInfo.id,
        },
        ..Default::default()
    };
    let status = api.target_name(&mut name);
    if status != ERROR_SUCCESS {
        return Err(status);
    }
    Ok(MonitorName {
        friendly_name: native_name(&name.monitorFriendlyDeviceName),
        device_path: native_name(&name.monitorDevicePath),
    })
}

fn native_name(units: &[u16]) -> Option<OsString> {
    // The SDK defines these arrays as NUL-terminated strings.
    let end = units.iter().position(|unit| *unit == 0)?;
    (end != 0).then(|| OsString::from_wide(&units[..end]))
}

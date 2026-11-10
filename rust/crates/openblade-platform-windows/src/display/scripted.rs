//! Synthetic native packets for the private Windows Session Agent replay.

use super::refresh::{DriverMode, RefreshApi};
use super::*;
use windows_sys::Win32::{
    Devices::Display::{
        DISPLAYCONFIG_MODE_INFO_0, DISPLAYCONFIG_MODE_INFO_TYPE_TARGET, DISPLAYCONFIG_TARGET_MODE,
        DISPLAYCONFIG_VIDEO_SIGNAL_INFO,
    },
    Foundation::{ERROR_ACCESS_DENIED, ERROR_NOT_SUPPORTED},
    Graphics::Gdi::ENUM_CURRENT_SETTINGS,
};

pub const MONITOR_INSTANCE: &str = r"DISPLAY\SYN0001\FIXTURE";
const MONITOR_PATH: &str = r"\\?\DISPLAY#SYN0001#FIXTURE#{SYNTHETIC}";

#[derive(Clone, Copy, PartialEq, Eq)]
pub enum Scenario {
    Available,
    Unavailable,
    StaleTarget,
    NativeError,
    HdrError,
}

impl Scenario {
    pub fn topology(self, replaced: bool) -> Result<InternalDisplayTopology, DisplayReadError> {
        read_with(&mut Script {
            scenario: self,
            target: if replaced { 5 } else { 4 },
        })
    }

    pub fn read(self) -> Result<InternalDisplayRefresh, DisplayReadError> {
        refresh::read_refresh_with(&mut Script {
            scenario: self,
            target: 4,
        })
    }
}

struct Script {
    scenario: Scenario,
    target: u32,
}

const ADAPTER: LUID = LUID {
    LowPart: 1,
    HighPart: 0,
};
const WIDTH: u32 = 2560;
const HEIGHT: u32 = 1600;

impl DisplayApi for Script {
    fn buffer_sizes(&mut self, flags: u32) -> Result<(u32, u32), u32> {
        assert_eq!(flags, QUERY_FLAGS);
        Ok(if self.scenario == Scenario::Unavailable {
            (0, 0)
        } else {
            (1, 1)
        })
    }

    fn query(
        &mut self,
        flags: u32,
        counts: &mut (u32, u32),
        paths: &mut [DISPLAYCONFIG_PATH_INFO],
        modes: &mut [DISPLAYCONFIG_MODE_INFO],
    ) -> u32 {
        assert_eq!(flags, QUERY_FLAGS);
        match self.scenario {
            Scenario::NativeError => return ERROR_ACCESS_DENIED,
            Scenario::Unavailable => {
                assert_eq!(*counts, (0, 0));
                return ERROR_SUCCESS;
            }
            _ => {}
        }

        // Each CCD frame is internally coherent. Only the endpoint changes
        // across the two observations, with the GDI name and timings reused.
        if self.scenario == Scenario::StaleTarget {
            self.target += 1;
        }
        assert_eq!(*counts, (1, 1));
        let path = &mut paths[0];
        path.flags = DISPLAYCONFIG_PATH_ACTIVE;
        path.sourceInfo.adapterId = ADAPTER;
        path.sourceInfo.id = 3;
        path.targetInfo.adapterId = ADAPTER;
        path.targetInfo.id = self.target;
        path.targetInfo.outputTechnology = DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_EMBEDDED;
        path.targetInfo.Anonymous.modeInfoIdx = 0;
        path.targetInfo.refreshRate = DISPLAYCONFIG_RATIONAL {
            Numerator: 60,
            Denominator: 1,
        };
        modes[0] = DISPLAYCONFIG_MODE_INFO {
            infoType: DISPLAYCONFIG_MODE_INFO_TYPE_TARGET,
            id: self.target,
            adapterId: ADAPTER,
            Anonymous: DISPLAYCONFIG_MODE_INFO_0 {
                targetMode: DISPLAYCONFIG_TARGET_MODE {
                    targetVideoSignalInfo: DISPLAYCONFIG_VIDEO_SIGNAL_INFO {
                        vSyncFreq: DISPLAYCONFIG_RATIONAL {
                            Numerator: 240,
                            Denominator: 1,
                        },
                        ..Default::default()
                    },
                },
            },
        };
        ERROR_SUCCESS
    }

    fn source_name(&mut self, request: &mut DISPLAYCONFIG_SOURCE_DEVICE_NAME) -> u32 {
        assert_endpoint(&request.header, 3);
        request.viewGdiDeviceName[0] = u16::from(b'S');
        ERROR_SUCCESS
    }

    fn target_name(&mut self, request: &mut DISPLAYCONFIG_TARGET_DEVICE_NAME) -> u32 {
        assert_endpoint(&request.header, self.target);
        for (unit, value) in request
            .monitorFriendlyDeviceName
            .iter_mut()
            .zip("Synthetic panel".encode_utf16())
        {
            *unit = value;
        }
        for (unit, value) in request
            .monitorDevicePath
            .iter_mut()
            .zip(MONITOR_PATH.encode_utf16())
        {
            *unit = value;
        }
        ERROR_SUCCESS
    }

    fn advanced_color(&mut self, request: &mut native::GetAdvancedColorInfo2) -> u32 {
        assert_endpoint(&request.header, self.target);
        assert_eq!(request.header.r#type, 15);
        assert_eq!(request.header.size, 36);
        // HDR supported, user preference enabled, policy limited.
        // The managed oracle's hdr_user_enabled_with_policy_limit row.
        request.value = 56;
        request.bits_per_color_channel = 10;
        if self.scenario == Scenario::HdrError {
            ERROR_NOT_SUPPORTED
        } else {
            ERROR_SUCCESS
        }
    }
}

fn assert_endpoint(header: &DISPLAYCONFIG_DEVICE_INFO_HEADER, id: u32) {
    assert_eq!(
        EndpointId::new(header.adapterId, header.id),
        EndpointId::new(ADAPTER, id)
    );
}

impl RefreshApi for Script {
    fn mode(&mut self, source: &[u16], index: u32) -> Option<DriverMode> {
        assert_eq!(source, [u16::from(b'S'), 0]);
        let hertz = match index {
            ENUM_CURRENT_SETTINGS | 0 => 60,
            1 => 240,
            _ => return None,
        };
        Some(DriverMode {
            width: WIDTH,
            height: HEIGHT,
            hertz,
            interlaced: false,
        })
    }

    fn preferred_mode(&mut self, _: EndpointId) -> Result<SignalTiming, u32> {
        unreachable!("script uses the legacy mode index layout");
    }

    fn edid(&mut self, _: &[u16]) -> Result<Vec<u8>, u32> {
        Err(ERROR_NOT_SUPPORTED)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn split_rates_and_hdr_are_observed_together_without_boost() {
        let InternalDisplayRefresh::Single(snapshot) = Scenario::Available.read().unwrap() else {
            panic!("expected a single display");
        };
        assert_eq!(snapshot.display.desktop_rate.unwrap().numerator, 60);
        assert_eq!(snapshot.display.physical_rate.unwrap().numerator, 240);
        assert!(!snapshot.display.boost_refresh_rate);
        assert_eq!(snapshot.current_refresh_key, None);
        assert_eq!(
            snapshot.display.advanced_color,
            Ok(AdvancedColorState {
                hdr_supported: true,
                hdr_user_enabled: true,
                bits_per_color_channel: Some(10),
            })
        );
        assert_eq!(
            Scenario::StaleTarget.read(),
            Err(DisplayReadError::TopologyChanging)
        );
    }
}

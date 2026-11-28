//! Synthetic native packets for the private Windows Session Agent replay.

use super::*;
use super::{
    refresh::{DriverMode, RefreshApi},
    refresh_packet::{APPLY, RefreshPacket, VALIDATE},
};
use windows_sys::Win32::{
    Devices::Display::{
        DISPLAYCONFIG_2DREGION, DISPLAYCONFIG_MODE_INFO_0,
        DISPLAYCONFIG_MODE_INFO_TYPE_DESKTOP_IMAGE, DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE,
        DISPLAYCONFIG_MODE_INFO_TYPE_TARGET, DISPLAYCONFIG_TARGET_MODE,
        DISPLAYCONFIG_VIDEO_SIGNAL_INFO,
    },
    Foundation::{ERROR_ACCESS_DENIED, ERROR_NOT_SUPPORTED},
    Graphics::Gdi::ENUM_CURRENT_SETTINGS,
};

pub const MONITOR_INSTANCE: &str = r"DISPLAY\SYN0001\FIXTURE";
pub(crate) const MONITOR_PATH: &str = r"\\?\DISPLAY#SYN0001#FIXTURE#{SYNTHETIC}";

#[derive(Clone, Copy, PartialEq, Eq)]
pub enum Scenario {
    Available,
    Dynamic,
    Unavailable,
    StaleTarget,
    NativeError,
    HdrError,
    HdrUnsupported,
}

impl Scenario {
    pub fn topology(self, replaced: bool) -> Result<InternalDisplayTopology, DisplayReadError> {
        self.topology_with_hdr(true, RefreshTiming::default(), replaced)
    }

    pub fn read(self) -> Result<InternalDisplayRefresh, DisplayReadError> {
        self.read_with_hdr(true, RefreshTiming::default(), false)
    }

    pub fn topology_with_hdr(
        self,
        enabled: bool,
        timing: RefreshTiming,
        replaced: bool,
    ) -> Result<InternalDisplayTopology, DisplayReadError> {
        read_with(&mut self.script(enabled, timing, replaced))
    }

    pub fn read_with_hdr(
        self,
        enabled: bool,
        timing: RefreshTiming,
        replaced: bool,
    ) -> Result<InternalDisplayRefresh, DisplayReadError> {
        refresh::read_refresh_with(&mut self.script(enabled, timing, replaced))
    }

    pub fn prepare_refresh(
        self,
        enabled: bool,
        timing: RefreshTiming,
        replaced: bool,
        key: &str,
    ) -> Result<(InternalDisplayRefresh, Option<PreparedRefresh>), DisplayReadError> {
        let mut evidence = None;
        let reading = refresh::observe_refresh(
            &mut self.script(enabled, timing, replaced),
            Some(key),
            |topology, preferred, timings| evidence = Some((topology, preferred, timings)),
        )?;
        let packet = match (&reading, evidence) {
            (InternalDisplayRefresh::Single(snapshot), Some((topology, preferred, timings))) => {
                snapshot
                    .refresh_options
                    .iter()
                    .find(|option| option.key == key)
                    .and_then(|option| {
                        RefreshPacket::prepare(
                            topology,
                            &snapshot.display,
                            option,
                            preferred,
                            &timings,
                        )
                        .ok()
                    })
                    .map(PreparedRefresh)
            }
            _ => None,
        };
        Ok((reading, packet))
    }

    /// The production writer's observation over this script.
    #[cfg(feature = "display-refresh-write")]
    pub fn observe_refresh(
        self,
        enabled: bool,
        timing: RefreshTiming,
        replaced: bool,
        key: &str,
    ) -> Result<super::refresh_write::RefreshObservation, DisplayReadError> {
        super::refresh_write::RefreshObservation::observe_with(
            &mut self.script(enabled, timing, replaced),
            key,
        )
    }

    fn script(self, hdr_enabled: bool, timing: RefreshTiming, replaced: bool) -> Script {
        Script {
            scenario: self,
            target: if replaced { 5 } else { 4 },
            hdr_enabled,
            timing,
        }
    }
}

/// The typed setter effect for the private fixture. This is deliberately
/// separate from the read adapter and has no route to User32.
pub fn submit_hdr(target: EndpointId, enabled: bool) -> bool {
    let packet = native::SetHdrState::new(target, enabled);
    assert_endpoint(&packet.header, 4);
    assert_eq!(packet.header.r#type, 16);
    assert_eq!(packet.header.size, 24);
    assert!(packet.value <= 1);
    packet.value != 0
}

#[derive(Clone, Copy)]
pub struct RefreshTiming {
    desktop: DISPLAYCONFIG_RATIONAL,
    physical: DISPLAYCONFIG_TARGET_MODE,
    boost: bool,
}

impl Default for RefreshTiming {
    fn default() -> Self {
        Self {
            desktop: rate(60),
            physical: signal(rate(240)),
            boost: false,
        }
    }
}

pub struct PreparedRefresh(RefreshPacket);

impl PreparedRefresh {
    pub fn validate(self, rejected: bool) -> Option<ValidatedRefresh> {
        // Scripted SDC_VALIDATE has no mutation effect. No native setter is linked here.
        assert_eq!(VALIDATE, 0x28060);
        (!rejected).then_some(ValidatedRefresh(self.0))
    }
}

pub struct ValidatedRefresh(RefreshPacket);

impl ValidatedRefresh {
    pub fn submit(self) -> RefreshTiming {
        assert_eq!(APPLY, 0x282a0);
        applied(&self.0)
    }
}

/// The timing the scripted panel reports after `packet` is applied.
fn applied(packet: &RefreshPacket) -> RefreshTiming {
    let path = packet.selected_path();
    let desktop = path.targetInfo.refreshRate;
    RefreshTiming {
        desktop,
        physical: packet.selected_mode().unwrap_or_else(|| signal(desktop)),
        boost: path.flags & PATH_BOOST_REFRESH_RATE != 0,
    }
}

#[cfg(feature = "display-refresh-write")]
impl super::refresh_write::RefreshPlan {
    /// The timing the scripted panel reports after this plan is applied. A
    /// plan that omits the target timing models Windows selecting the signal
    /// at the desktop rate.
    pub fn scripted_timing(&self) -> RefreshTiming {
        applied(&self.0)
    }
}

impl RefreshTiming {
    /// A non-boosted timing with these desktop and physical signal rates.
    pub fn fixed(desktop: u32, physical: u32) -> Self {
        Self {
            desktop: rate(desktop),
            physical: signal(rate(physical)),
            boost: false,
        }
    }
}

fn rate(hertz: u32) -> DISPLAYCONFIG_RATIONAL {
    DISPLAYCONFIG_RATIONAL {
        Numerator: hertz,
        Denominator: 1,
    }
}

fn signal(rate: DISPLAYCONFIG_RATIONAL) -> DISPLAYCONFIG_TARGET_MODE {
    DISPLAYCONFIG_TARGET_MODE {
        targetVideoSignalInfo: DISPLAYCONFIG_VIDEO_SIGNAL_INFO {
            vSyncFreq: rate,
            activeSize: DISPLAYCONFIG_2DREGION {
                cx: WIDTH,
                cy: HEIGHT,
            },
            ..Default::default()
        },
    }
}

struct Script {
    scenario: Scenario,
    target: u32,
    hdr_enabled: bool,
    timing: RefreshTiming,
}

const ADAPTER: LUID = LUID {
    LowPart: 1,
    HighPart: 0,
};
const WIDTH: u32 = 2560;
const HEIGHT: u32 = 1600;

impl Script {
    fn mode_count(&self) -> u32 {
        if self.scenario == Scenario::Dynamic {
            3
        } else {
            2
        }
    }
}

impl DisplayApi for Script {
    fn buffer_sizes(&mut self, flags: u32) -> Result<(u32, u32), u32> {
        assert_eq!(flags, QUERY_FLAGS);
        Ok(if self.scenario == Scenario::Unavailable {
            (0, 0)
        } else {
            (1, self.mode_count())
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
        assert_eq!(*counts, (1, self.mode_count()));
        let path = &mut paths[0];
        path.flags = DISPLAYCONFIG_PATH_ACTIVE;
        let virtual_mode = self.scenario == Scenario::Dynamic;
        if virtual_mode {
            path.flags |= DISPLAYCONFIG_PATH_SUPPORT_VIRTUAL_MODE;
        }
        if self.timing.boost {
            path.flags |= PATH_BOOST_REFRESH_RATE;
        }
        path.sourceInfo.adapterId = ADAPTER;
        path.sourceInfo.id = 3;
        path.sourceInfo.Anonymous.modeInfoIdx = if virtual_mode { 0xffff } else { 0 };
        path.targetInfo.adapterId = ADAPTER;
        path.targetInfo.id = self.target;
        path.targetInfo.outputTechnology = DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_EMBEDDED;
        path.targetInfo.Anonymous.modeInfoIdx = if virtual_mode { (1 << 16) | 2 } else { 1 };
        path.targetInfo.refreshRate = self.timing.desktop;
        modes[0] = DISPLAYCONFIG_MODE_INFO {
            infoType: DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE,
            id: 3,
            adapterId: ADAPTER,
            ..Default::default()
        };
        if virtual_mode {
            modes[2] = DISPLAYCONFIG_MODE_INFO {
                infoType: DISPLAYCONFIG_MODE_INFO_TYPE_DESKTOP_IMAGE,
                id: self.target,
                adapterId: ADAPTER,
                ..Default::default()
            };
        }
        modes[1] = DISPLAYCONFIG_MODE_INFO {
            infoType: DISPLAYCONFIG_MODE_INFO_TYPE_TARGET,
            id: self.target,
            adapterId: ADAPTER,
            Anonymous: DISPLAYCONFIG_MODE_INFO_0 {
                targetMode: self.timing.physical,
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
        // The SDK packet's active mode remains SDR, regardless of preference.
        request.value = if self.hdr_enabled && self.scenario != Scenario::HdrUnsupported {
            1 << 3 // Policy limited.
        } else {
            0
        };
        if self.scenario != Scenario::HdrUnsupported {
            request.value |= native::HDR_SUPPORTED;
        }
        if self.hdr_enabled {
            request.value |= native::HDR_USER_ENABLED;
        }
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

    fn preferred_mode(&mut self, target: EndpointId) -> Result<DISPLAYCONFIG_TARGET_MODE, u32> {
        assert_eq!(target, EndpointId::new(ADAPTER, self.target));
        if self.scenario == Scenario::Dynamic {
            Ok(signal(DISPLAYCONFIG_RATIONAL {
                Numerator: 240_000,
                Denominator: 1_001,
            }))
        } else {
            Err(ERROR_NOT_SUPPORTED)
        }
    }

    fn edid(&mut self, _: &[u16]) -> Result<Vec<u8>, u32> {
        Err(ERROR_NOT_SUPPORTED)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn fixed_fallback_omits_only_the_target_mode_index() {
        for (scenario, expected) in [
            (Scenario::Available, (0, u32::MAX)),
            (Scenario::Dynamic, (0xffff, 0xffff_0002)),
        ] {
            let (_, packet) = scenario
                .prepare_refresh(true, RefreshTiming::default(), false, "fixed-60")
                .unwrap();
            let packet = packet.unwrap().0;
            assert_eq!(packet.index_words(), expected);
            assert!(packet.selected_mode().is_none());
            let path = packet.selected_path();
            assert_eq!(path.targetInfo.refreshRate.Numerator, 60);
            assert_eq!(path.flags & PATH_BOOST_REFRESH_RATE, 0);
        }
    }

    #[test]
    fn dynamic_and_fixed_keep_the_admitted_fractional_physical_signal() {
        let scenario = Scenario::Dynamic;
        let sixty = RefreshTiming {
            desktop: rate(60),
            physical: signal(rate(60)),
            boost: false,
        };
        let (_, packet) = scenario
            .prepare_refresh(true, sixty, false, "dynamic-60-240")
            .unwrap();
        let packet = packet.unwrap();
        assert_eq!(packet.0.index_words(), (0xffff, 0x1_0002));
        let dynamic = packet.validate(false).unwrap().submit();
        assert!(dynamic.boost);
        assert_eq!(dynamic.desktop.Numerator, 60);
        let physical = dynamic.physical.targetVideoSignalInfo.vSyncFreq;
        assert_eq!((physical.Numerator, physical.Denominator), (240_000, 1_001));

        let (_, packet) = scenario
            .prepare_refresh(true, dynamic, false, "fixed-240")
            .unwrap();
        let fixed = packet.unwrap().validate(false).unwrap().submit();
        assert!(!fixed.boost);
        let physical = fixed.physical.targetVideoSignalInfo.vSyncFreq;
        assert_eq!((physical.Numerator, physical.Denominator), (240_000, 1_001));
        assert_eq!(
            (fixed.desktop.Numerator, fixed.desktop.Denominator),
            (240_000, 1_001)
        );
    }

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

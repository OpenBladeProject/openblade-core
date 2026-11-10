use super::*;
use std::{collections::VecDeque, hint::black_box};
use windows_sys::Win32::Foundation::{ERROR_ACCESS_DENIED, ERROR_NOT_SUPPORTED};

const SOURCE: LUID = LUID {
    LowPart: 9,
    HighPart: -2,
};
const TARGET: LUID = LUID {
    LowPart: 8,
    HighPart: 1,
};

struct Frame {
    sizes: Result<(u32, u32), u32>,
    result: u32,
    counts: (u32, u32),
    paths: Vec<DISPLAYCONFIG_PATH_INFO>,
    modes: Vec<DISPLAYCONFIG_MODE_INFO>,
}

impl Frame {
    fn success(paths: Vec<DISPLAYCONFIG_PATH_INFO>, modes: Vec<DISPLAYCONFIG_MODE_INFO>) -> Self {
        let counts = (paths.len() as u32, modes.len() as u32);
        Self {
            sizes: Ok(counts),
            result: ERROR_SUCCESS,
            counts,
            paths,
            modes,
        }
    }

    fn status(result: u32) -> Self {
        Self {
            result,
            ..Self::success(vec![], vec![])
        }
    }
}

struct Script {
    frames: VecDeque<Frame>,
    pending: Option<Frame>,
    source_status: u32,
    target_status: u32,
    reads: (usize, usize),
}

impl Script {
    fn new(frames: impl IntoIterator<Item = Frame>) -> Self {
        Self {
            frames: frames.into_iter().collect(),
            pending: None,
            source_status: ERROR_SUCCESS,
            target_status: ERROR_SUCCESS,
            reads: (0, 0),
        }
    }
}

impl DisplayApi for Script {
    fn buffer_sizes(&mut self, flags: u32) -> Result<(u32, u32), u32> {
        assert_eq!(flags, 0x52);
        assert!(self.pending.is_none());
        let frame = self.frames.pop_front().expect("unexpected sizing attempt");
        let sizes = frame.sizes?;
        if sizes.0 <= MAX_ENTRIES && sizes.1 <= MAX_ENTRIES {
            self.pending = Some(frame);
        }
        Ok(sizes)
    }

    fn query(
        &mut self,
        flags: u32,
        counts: &mut (u32, u32),
        paths: &mut [DISPLAYCONFIG_PATH_INFO],
        modes: &mut [DISPLAYCONFIG_MODE_INFO],
    ) -> u32 {
        assert_eq!(flags, 0x52);
        let frame = self.pending.take().expect("query requires fresh sizes");
        assert_eq!(Ok(*counts), frame.sizes);
        assert_eq!(paths.len(), counts.0.max(1) as usize);
        assert_eq!(modes.len(), counts.1.max(1) as usize);
        paths[..frame.paths.len()].copy_from_slice(&frame.paths);
        modes[..frame.modes.len()].copy_from_slice(&frame.modes);
        *counts = frame.counts;
        frame.result
    }

    fn source_name(&mut self, request: &mut DISPLAYCONFIG_SOURCE_DEVICE_NAME) -> u32 {
        self.reads.0 += 1;
        assert_eq!(
            request.header.r#type,
            DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME
        );
        assert_eq!(request.header.size as usize, size_of_val(request));
        assert_eq!(
            EndpointId::new(request.header.adapterId, request.header.id),
            EndpointId::new(SOURCE, 3)
        );
        request.viewGdiDeviceName[0] = b'S'.into();
        self.source_status
    }

    fn target_name(&mut self, request: &mut DISPLAYCONFIG_TARGET_DEVICE_NAME) -> u32 {
        self.reads.1 += 1;
        assert_eq!(
            request.header.r#type,
            DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME
        );
        assert_eq!(request.header.size as usize, size_of_val(request));
        assert_eq!(
            EndpointId::new(request.header.adapterId, request.header.id),
            EndpointId::new(TARGET, 5)
        );
        request.monitorFriendlyDeviceName[0] = b'T'.into();
        request.monitorDevicePath[0] = b'P'.into();
        self.target_status
    }
}

fn path(index_word: u32, flags: u32) -> DISPLAYCONFIG_PATH_INFO {
    let mut path = DISPLAYCONFIG_PATH_INFO {
        flags,
        ..Default::default()
    };
    path.sourceInfo.adapterId = SOURCE;
    path.sourceInfo.id = 3;
    path.targetInfo.adapterId = TARGET;
    path.targetInfo.id = 5;
    path.targetInfo.outputTechnology = DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_EMBEDDED;
    path.targetInfo.Anonymous.modeInfoIdx = index_word;
    path.targetInfo.refreshRate = DISPLAYCONFIG_RATIONAL {
        Numerator: 60,
        Denominator: 1,
    };
    path
}

fn target_mode() -> DISPLAYCONFIG_MODE_INFO {
    let signal = DISPLAYCONFIG_VIDEO_SIGNAL_INFO {
        vSyncFreq: DISPLAYCONFIG_RATIONAL {
            Numerator: 240_000,
            Denominator: 1001,
        },
        ..Default::default()
    };
    DISPLAYCONFIG_MODE_INFO {
        infoType: DISPLAYCONFIG_MODE_INFO_TYPE_TARGET,
        id: 5,
        adapterId: TARGET,
        Anonymous: DISPLAYCONFIG_MODE_INFO_0 {
            targetMode: DISPLAYCONFIG_TARGET_MODE {
                targetVideoSignalInfo: signal,
            },
        },
    }
}

#[test]
fn links_windows_entry_point_without_calling_it() {
    black_box(read_internal_display as fn() -> _);
}

#[test]
fn keeps_desktop_rate_and_physical_signal_separate_with_or_without_drr() {
    // Compare the two inputs to managed RefreshRateClassifierTests. A fixed
    // path may still report a higher physical signal. The boost bit is independent.
    for boost in [0, PATH_BOOST_REFRESH_RATE] {
        let packed = path(
            (1 << 16) | 0xFFFF,
            PATH_ACTIVE | PATH_SUPPORT_VIRTUAL_MODE | boost,
        );
        let legacy = path(1, PATH_ACTIVE | boost);
        for internal in [packed, legacy] {
            let mut external = internal;
            external.targetInfo.outputTechnology = DISPLAYCONFIG_OUTPUT_TECHNOLOGY_HDMI;
            let frame = Frame::success(
                vec![external, internal],
                vec![DISPLAYCONFIG_MODE_INFO::default(), target_mode()],
            );
            let mut api = Script::new([frame]);
            let Ok(InternalDisplayTopology::Single(display)) = read_with(&mut api) else {
                panic!("expected single internal display");
            };
            assert_eq!(display.source, EndpointId::new(SOURCE, 3));
            assert_eq!(display.target, EndpointId::new(TARGET, 5));
            assert_eq!(display.source_name, "S");
            assert_eq!(
                display.monitor_name,
                Ok(MonitorName {
                    friendly_name: Some("T".into()),
                    device_path: Some("P".into()),
                })
            );
            assert_eq!(
                display.desktop_rate,
                Some(RefreshRate {
                    numerator: 60,
                    denominator: 1
                })
            );
            assert_eq!(
                display.physical_rate,
                Some(RefreshRate {
                    numerator: 240_000,
                    denominator: 1001
                })
            );
            assert_eq!(display.boost_refresh_rate, boost != 0);
            assert_eq!(api.reads, (1, 1));
        }
    }
}

#[test]
fn distinguishes_empty_non_internal_and_ambiguous_topology() {
    let internal = path(0, PATH_ACTIVE);
    let mut external = internal;
    external.targetInfo.outputTechnology = DISPLAYCONFIG_OUTPUT_TECHNOLOGY_HDMI;
    let inactive = path(0, 0);
    for (paths, expected) in [
        (vec![], InternalDisplayTopology::Empty),
        (
            vec![external, inactive],
            InternalDisplayTopology::NoInternalDisplay,
        ),
        (
            vec![internal, internal],
            InternalDisplayTopology::MultipleInternalDisplays,
        ),
    ] {
        let mut api = Script::new([Frame::success(paths, vec![])]);
        assert_eq!(read_with(&mut api), Ok(expected));
        assert_eq!(api.reads, (0, 0));
    }
}

#[test]
fn resizes_from_fresh_counts_and_uses_returned_lengths() {
    let mut raced = Frame::status(ERROR_INSUFFICIENT_BUFFER);
    raced.counts = (u32::MAX, u32::MAX);
    let mut stable = Frame::success(
        vec![path(0, PATH_ACTIVE), path(0, PATH_ACTIVE)],
        vec![target_mode()],
    );
    stable.sizes = Ok((2, 3));
    stable.counts = (1, 1);
    let mut api = Script::new([raced, stable]);
    assert!(matches!(
        read_with(&mut api),
        Ok(InternalDisplayTopology::Single(_))
    ));
    assert!(api.frames.is_empty());
}

#[test]
fn topology_churn_stops_after_five_attempts() {
    let mut api = Script::new((0..5).map(|_| Frame::status(ERROR_INSUFFICIENT_BUFFER)));
    assert_eq!(read_with(&mut api), Err(DisplayReadError::TopologyChanging));
    assert!(api.frames.is_empty());
    assert_eq!(api.reads, (0, 0));
}

#[test]
fn rejects_invalid_counts_at_both_boundaries() {
    for counts in [(MAX_ENTRIES + 1, 0), (0, MAX_ENTRIES + 1)] {
        let mut frame = Frame::status(ERROR_SUCCESS);
        frame.sizes = Ok(counts);
        assert_eq!(
            read_with(&mut Script::new([frame])),
            Err(DisplayReadError::BufferLimit)
        );
    }
    for counts in [(1, 0), (0, 1)] {
        let mut frame = Frame::status(ERROR_SUCCESS);
        frame.counts = counts;
        assert_eq!(
            read_with(&mut Script::new([frame])),
            Err(DisplayReadError::InvalidReturnedCounts)
        );
    }
}

#[test]
fn rejects_missing_and_out_of_range_target_indices() {
    for (index, flags, error) in [
        (u32::MAX, PATH_ACTIVE, DisplayReadError::MissingTargetMode),
        (
            0xFFFF_0000,
            PATH_ACTIVE | PATH_SUPPORT_VIRTUAL_MODE,
            DisplayReadError::MissingTargetMode,
        ),
        (1, PATH_ACTIVE, DisplayReadError::InvalidTargetModeIndex),
        (
            1 << 16,
            PATH_ACTIVE | PATH_SUPPORT_VIRTUAL_MODE,
            DisplayReadError::InvalidTargetModeIndex,
        ),
    ] {
        let mut frame =
            Frame::success(vec![path(index, flags)], vec![target_mode(), target_mode()]);
        frame.counts.1 = 1;
        let mut api = Script::new([frame]);
        assert_eq!(read_with(&mut api), Err(error));
        assert_eq!(api.reads, (0, 0));
    }
}

#[test]
fn rejects_a_mode_with_the_wrong_type_or_endpoint() {
    let expected = target_mode();
    for mode in [
        DISPLAYCONFIG_MODE_INFO {
            infoType: DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE,
            ..expected
        },
        DISPLAYCONFIG_MODE_INFO { id: 6, ..expected },
        DISPLAYCONFIG_MODE_INFO {
            adapterId: SOURCE,
            ..expected
        },
    ] {
        let frame = Frame::success(vec![path(0, PATH_ACTIVE)], vec![mode]);
        assert_eq!(
            read_with(&mut Script::new([frame])),
            Err(DisplayReadError::MismatchedTargetMode)
        );
    }
}

#[test]
fn missing_rates_are_independent_and_target_lookup_keeps_its_status() {
    for missing_desktop in [true, false] {
        let mut internal = path(0, PATH_ACTIVE | PATH_BOOST_REFRESH_RATE);
        let mut mode = target_mode();
        if missing_desktop {
            internal.targetInfo.refreshRate.Numerator = 0;
        } else {
            mode.Anonymous.targetMode = DISPLAYCONFIG_TARGET_MODE::default();
        }
        let mut api = Script::new([Frame::success(vec![internal], vec![mode])]);
        api.target_status = ERROR_ACCESS_DENIED;
        let Ok(InternalDisplayTopology::Single(display)) = read_with(&mut api) else {
            panic!("expected single internal display");
        };
        assert_eq!(display.desktop_rate.is_none(), missing_desktop);
        assert_eq!(display.physical_rate.is_none(), !missing_desktop);
        assert_eq!(display.monitor_name, Err(ERROR_ACCESS_DENIED));
    }
}

#[test]
fn preserves_native_failure_stage_and_status() {
    let mut denied = Frame::status(ERROR_SUCCESS);
    denied.sizes = Err(ERROR_ACCESS_DENIED);
    for (frame, operation, status) in [
        (denied, Operation::BufferSizes, ERROR_ACCESS_DENIED),
        (
            Frame::status(ERROR_ACCESS_DENIED),
            Operation::ActivePaths,
            ERROR_ACCESS_DENIED,
        ),
        (
            Frame::status(ERROR_NOT_SUPPORTED),
            Operation::ActivePaths,
            ERROR_NOT_SUPPORTED,
        ),
    ] {
        assert_eq!(
            read_with(&mut Script::new([frame])),
            Err(DisplayReadError::Native { operation, status })
        );
    }
    let mut api = Script::new([Frame::success(
        vec![path(0, PATH_ACTIVE)],
        vec![target_mode()],
    )]);
    api.source_status = ERROR_ACCESS_DENIED;
    assert_eq!(
        read_with(&mut api),
        Err(DisplayReadError::Native {
            operation: Operation::SourceName,
            status: ERROR_ACCESS_DENIED,
        })
    );
    assert_eq!(api.reads, (1, 0));
}

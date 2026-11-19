use super::refresh::{DriverMode, RefreshApi};
use super::*;
use std::{collections::VecDeque, hint::black_box};
use windows_sys::Win32::Graphics::Gdi::ENUM_CURRENT_SETTINGS;
use windows_sys::Win32::{
    Devices::Display::{
        DISPLAYCONFIG_2DREGION, DISPLAYCONFIG_MODE_INFO_0, DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE,
        DISPLAYCONFIG_MODE_INFO_TYPE_TARGET, DISPLAYCONFIG_OUTPUT_TECHNOLOGY_HDMI,
        DISPLAYCONFIG_TARGET_MODE, DISPLAYCONFIG_VIDEO_SIGNAL_INFO,
    },
    Foundation::{ERROR_ACCESS_DENIED, ERROR_NOT_SUPPORTED},
};

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
    source_name: [u16; 32],
    target_status: u32,
    reads: (usize, usize),
    current_mode: Option<DriverMode>,
    current_recheck: Option<DriverMode>,
    current_reads: usize,
    indexed_modes: VecDeque<DriverMode>,
    indexed_reads: u32,
    unbounded_modes: bool,
    preferred_calls: usize,
    preferred: Option<Result<SignalTiming, u32>>,
    monitor_path: Option<&'static str>,
    edid: Option<Result<Vec<u8>, u32>>,
    expected_target: EndpointId,
    color_status: u32,
    color_value: u32,
    color_depth: u32,
    color_reads: usize,
}

impl Script {
    fn new(frames: impl IntoIterator<Item = Frame>) -> Self {
        Self {
            frames: frames.into_iter().collect(),
            pending: None,
            source_status: ERROR_SUCCESS,
            source_name: std::array::from_fn(|i| if i == 0 { u16::from(b'S') } else { 0 }),
            target_status: ERROR_SUCCESS,
            reads: (0, 0),
            current_mode: None,
            current_recheck: None,
            current_reads: 0,
            indexed_modes: VecDeque::new(),
            indexed_reads: 0,
            unbounded_modes: false,
            preferred_calls: 0,
            preferred: None,
            monitor_path: None,
            edid: None,
            expected_target: EndpointId::new(TARGET, 5),
            color_status: ERROR_SUCCESS,
            color_value: 0,
            color_depth: 0,
            color_reads: 0,
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
        request.viewGdiDeviceName = self.source_name;
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
            self.expected_target
        );
        request.monitorFriendlyDeviceName[0] = b'T'.into();
        if let Some(path) = self.monitor_path {
            for (unit, value) in request
                .monitorDevicePath
                .iter_mut()
                .zip(path.encode_utf16())
            {
                *unit = value;
            }
        } else {
            request.monitorDevicePath[0] = b'P'.into();
        }
        self.target_status
    }

    fn advanced_color(&mut self, request: &mut native::GetAdvancedColorInfo2) -> u32 {
        self.color_reads += 1;
        assert_eq!(request.header.r#type, 15);
        assert_eq!(request.header.size, 36);
        assert_eq!(
            EndpointId::new(request.header.adapterId, request.header.id),
            self.expected_target
        );
        assert_eq!((request.value, request.bits_per_color_channel), (0, 0));
        request.value = self.color_value;
        request.bits_per_color_channel = self.color_depth;
        self.color_status
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
    black_box(read_internal_display_refresh as fn() -> _);
}

#[test]
fn keeps_desktop_rate_and_physical_signal_separate_with_or_without_drr() {
    // Compare the two inputs to managed RefreshRateClassifierTests. A fixed
    // path may still report a higher physical signal. The boost bit is independent.
    for boost in [0, PATH_BOOST_REFRESH_RATE] {
        let packed = path(
            (1 << 16) | 0xFFFF,
            DISPLAYCONFIG_PATH_ACTIVE | DISPLAYCONFIG_PATH_SUPPORT_VIRTUAL_MODE | boost,
        );
        let legacy = path(1, DISPLAYCONFIG_PATH_ACTIVE | boost);
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
            assert_eq!(
                display.supports_virtual_mode,
                internal.flags & DISPLAYCONFIG_PATH_SUPPORT_VIRTUAL_MODE != 0
            );
            assert_eq!(display.boost_refresh_rate, boost != 0);
            assert_eq!(api.reads, (1, 1));
        }
    }
}

#[test]
fn distinguishes_empty_non_internal_and_ambiguous_topology() {
    let internal = path(0, DISPLAYCONFIG_PATH_ACTIVE);
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
        assert_eq!(api.color_reads, 0);
        assert_eq!(api.reads, (0, 0));
    }
}

#[test]
fn advanced_color_matches_managed_query_projection_and_retains_native_status() {
    let fixture = include_str!(
        "../../../../../tests/OpenBlade.Display.Tests/Fixtures/display-advanced-color.csv"
    );
    for line in fixture.lines().skip(1) {
        let fields: Vec<_> = line.split(',').collect();
        let [
            case,
            status,
            value,
            depth,
            supported,
            enabled,
            projected_depth,
        ] = fields[..]
        else {
            panic!("invalid Advanced Color fixture row: {line}");
        };
        let mut api = Script::new([Frame::success(
            vec![path(0, DISPLAYCONFIG_PATH_ACTIVE)],
            vec![target_mode()],
        )]);
        api.color_status = status.parse().unwrap();
        api.color_value = value.parse().unwrap();
        api.color_depth = depth.parse().unwrap();

        let InternalDisplayTopology::Single(display) = read_with(&mut api).unwrap() else {
            panic!("{case}: expected a single active internal target");
        };
        let expected = if api.color_status == ERROR_SUCCESS {
            Ok(AdvancedColorState {
                hdr_supported: supported.parse().unwrap(),
                hdr_user_enabled: enabled.parse().unwrap(),
                bits_per_color_channel: (!projected_depth.is_empty())
                    .then(|| projected_depth.parse().unwrap()),
            })
        } else {
            Err(AdvancedColorReadError::Native {
                status: api.color_status,
            })
        };
        assert_eq!(display.advanced_color, expected, "{case}");
        assert_eq!(api.color_reads, 1, "{case}");
    }
}

#[test]
fn advanced_color_uses_only_the_target_from_the_successful_topology_query() {
    let old_path = path(0, DISPLAYCONFIG_PATH_ACTIVE);
    let mut raced = Frame::success(vec![old_path], vec![target_mode()]);
    raced.result = ERROR_INSUFFICIENT_BUFFER;

    let mut new_path = old_path;
    new_path.targetInfo.adapterId = LUID {
        LowPart: 45,
        HighPart: -4,
    };
    new_path.targetInfo.id = 19;
    let new_mode = DISPLAYCONFIG_MODE_INFO {
        adapterId: new_path.targetInfo.adapterId,
        id: new_path.targetInfo.id,
        ..target_mode()
    };
    let stable = Frame::success(vec![new_path], vec![new_mode]);
    let mut api = Script::new([raced, stable]);
    api.expected_target = EndpointId::new(new_path.targetInfo.adapterId, new_path.targetInfo.id);
    api.color_depth = i32::MAX as u32 + 1;
    let InternalDisplayTopology::Single(display) = read_with(&mut api).unwrap() else {
        panic!("expected the new internal target");
    };
    assert_eq!(display.target, api.expected_target);
    assert_eq!(api.color_reads, 1);
    assert_eq!(
        display.advanced_color,
        Err(AdvancedColorReadError::InvalidBitsPerColorChannel(
            api.color_depth
        ))
    );
}

#[test]
fn resizes_from_fresh_counts_and_uses_returned_lengths() {
    let mut raced = Frame::status(ERROR_INSUFFICIENT_BUFFER);
    raced.counts = (u32::MAX, u32::MAX);
    let mut stable = Frame::success(
        vec![
            path(0, DISPLAYCONFIG_PATH_ACTIVE),
            path(0, DISPLAYCONFIG_PATH_ACTIVE),
        ],
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
        (
            u32::MAX,
            DISPLAYCONFIG_PATH_ACTIVE,
            DisplayReadError::MissingTargetMode,
        ),
        (
            0xFFFF_0000,
            DISPLAYCONFIG_PATH_ACTIVE | DISPLAYCONFIG_PATH_SUPPORT_VIRTUAL_MODE,
            DisplayReadError::MissingTargetMode,
        ),
        (
            1,
            DISPLAYCONFIG_PATH_ACTIVE,
            DisplayReadError::InvalidTargetModeIndex,
        ),
        (
            1 << 16,
            DISPLAYCONFIG_PATH_ACTIVE | DISPLAYCONFIG_PATH_SUPPORT_VIRTUAL_MODE,
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
        let frame = Frame::success(vec![path(0, DISPLAYCONFIG_PATH_ACTIVE)], vec![mode]);
        assert_eq!(
            read_with(&mut Script::new([frame])),
            Err(DisplayReadError::MismatchedTargetMode)
        );
    }
}

#[test]
fn missing_rates_are_independent_and_target_lookup_keeps_its_status() {
    for missing_desktop in [true, false] {
        let mut internal = path(0, DISPLAYCONFIG_PATH_ACTIVE | PATH_BOOST_REFRESH_RATE);
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
        vec![path(0, DISPLAYCONFIG_PATH_ACTIVE)],
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

#[test]
fn accepts_exact_capacity_limit() {
    let mut frame = Frame::status(ERROR_SUCCESS);
    frame.sizes = Ok((MAX_ENTRIES, MAX_ENTRIES));
    assert_eq!(
        read_with(&mut Script::new([frame])),
        Ok(InternalDisplayTopology::Empty)
    );
}

#[test]
fn rejects_empty_and_unterminated_source_names() {
    for name in [[0; 32], [u16::from(b'S'); 32]] {
        let frame = Frame::success(
            vec![path(0, DISPLAYCONFIG_PATH_ACTIVE)],
            vec![target_mode()],
        );
        let mut api = Script::new([frame]);
        api.source_name = name;
        assert_eq!(
            read_with(&mut api),
            Err(DisplayReadError::MissingSourceName)
        );
        assert_eq!(api.reads, (1, 0));
    }
}

impl RefreshApi for Script {
    fn mode(&mut self, source: &[u16], index: u32) -> Option<DriverMode> {
        assert_eq!(source, &[b'S'.into(), 0]);
        if index == ENUM_CURRENT_SETTINGS {
            self.current_reads += 1;
            return match self.current_reads {
                1 => {
                    assert_eq!(self.indexed_reads, 0);
                    self.current_mode
                }
                2 => self.current_recheck.or(self.current_mode),
                _ => panic!("unexpected current-mode read"),
            };
        }

        assert_eq!(index, self.indexed_reads);
        self.indexed_reads += 1;
        if self.unbounded_modes {
            return Some(mode_at(60));
        }
        self.indexed_modes.pop_front()
    }

    fn preferred_mode(&mut self, target: EndpointId) -> Result<DISPLAYCONFIG_TARGET_MODE, u32> {
        assert_eq!(target, EndpointId::new(TARGET, 5));
        self.preferred_calls += 1;
        self.preferred
            .expect("unexpected preferred mode read")
            .map(|signal| {
                let rate = signal.rate.unwrap();
                DISPLAYCONFIG_TARGET_MODE {
                    targetVideoSignalInfo: DISPLAYCONFIG_VIDEO_SIGNAL_INFO {
                        activeSize: DISPLAYCONFIG_2DREGION {
                            cx: signal.width,
                            cy: signal.height,
                        },
                        scanLineOrdering: signal.scanline_ordering,
                        vSyncFreq: DISPLAYCONFIG_RATIONAL {
                            Numerator: rate.numerator,
                            Denominator: rate.denominator,
                        },
                        ..Default::default()
                    },
                }
            })
    }

    fn edid(&mut self, subkey: &[u16]) -> Result<Vec<u8>, u32> {
        assert_eq!(
            String::from_utf16(subkey).unwrap(),
            "SYSTEM\\CurrentControlSet\\Enum\\DISPLAY\\PANEL\\INSTANCE\\Device Parameters\0"
        );
        self.edid.take().expect("unexpected EDID read")
    }
}

fn mode_at(hertz: u32) -> DriverMode {
    DriverMode {
        width: 2560,
        height: 1600,
        hertz,
        interlaced: false,
    }
}

fn refresh_script(flags: u32) -> Script {
    let path = path(0, DISPLAYCONFIG_PATH_ACTIVE | flags);
    let mut mode = target_mode();
    mode.Anonymous.targetMode = DISPLAYCONFIG_TARGET_MODE {
        targetVideoSignalInfo: DISPLAYCONFIG_VIDEO_SIGNAL_INFO {
            activeSize: windows_sys::Win32::Devices::Display::DISPLAYCONFIG_2DREGION {
                cx: 2560,
                cy: 1600,
            },
            vSyncFreq: DISPLAYCONFIG_RATIONAL {
                Numerator: 240_000,
                Denominator: 1001,
            },
            ..Default::default()
        },
    };
    let mut script = Script::new([
        Frame::success(vec![path], vec![mode]),
        Frame::success(vec![path], vec![mode]),
    ]);
    script.current_mode = Some(mode_at(60));
    script
}

#[test]
fn refresh_rechecks_the_selected_source_and_target() {
    for boost in [0, PATH_BOOST_REFRESH_RATE] {
        let mut api = refresh_script(DISPLAYCONFIG_PATH_SUPPORT_VIRTUAL_MODE | boost);
        api.preferred = Some(Ok(SignalTiming {
            width: 2560,
            height: 1600,
            scanline_ordering: 1,
            rate: Some(RefreshRate {
                numerator: 165,
                denominator: 1,
            }),
        }));
        api.monitor_path = Some(r"\\?\DISPLAY#PANEL#INSTANCE#{fixture}");
        api.edid = Some(Err(ERROR_ACCESS_DENIED));
        api.indexed_modes = [
            mode_at(240),
            mode_at(60),
            mode_at(165),
            mode_at(60),
            mode_at(90),
            mode_at(1001),
            DriverMode {
                interlaced: true,
                ..mode_at(120)
            },
            DriverMode {
                width: 1920,
                ..mode_at(144)
            },
        ]
        .into();
        let InternalDisplayRefresh::Single(snapshot) =
            refresh::read_refresh_with(&mut api).unwrap()
        else {
            panic!("expected refresh snapshot");
        };
        assert_eq!(api.reads, (2, 2));
        assert!(api.frames.is_empty());
        assert_eq!(api.current_reads, 2);
        assert_eq!(api.indexed_reads, 9);
        assert_eq!(api.preferred_calls, 1);
        assert!(api.edid.is_none());
        assert_eq!(
            (snapshot.width, snapshot.height, snapshot.current_mode_hertz),
            (2560, 1600, 60)
        );
        assert_eq!(
            snapshot.edid_range,
            TimingEvidence::NativeFailure(ERROR_ACCESS_DENIED)
        );
        assert_eq!(snapshot.display.desktop_rate.unwrap().numerator, 60);
        assert_eq!(snapshot.display.physical_rate.unwrap().numerator, 240_000);
        assert_eq!(snapshot.display.boost_refresh_rate, boost != 0);
        assert!(snapshot.display.supports_virtual_mode);
        assert_eq!(
            snapshot
                .refresh_options
                .iter()
                .map(|option| option.key.as_str())
                .collect::<Vec<_>>(),
            ["fixed-60", "fixed-165", "fixed-240", "dynamic-60-240"]
        );
        assert_eq!(
            snapshot.current_refresh_key.as_deref(),
            (boost != 0).then_some("dynamic-60-240")
        );
        assert_eq!(snapshot.message.is_some(), boost == 0);
    }
}

#[test]
fn refresh_retains_native_failures_and_bounds_driver_mode_enumeration() {
    let mut missing = refresh_script(0);
    missing.current_mode = None;
    assert_eq!(
        refresh::read_refresh_with(&mut missing),
        Err(DisplayReadError::CurrentModeUnavailable)
    );
    assert_eq!(missing.indexed_reads, 0);

    let mut invalid = refresh_script(0);
    invalid.current_mode = Some(DriverMode {
        width: 0,
        ..mode_at(60)
    });
    assert_eq!(
        refresh::read_refresh_with(&mut invalid),
        Err(DisplayReadError::InvalidCurrentMode)
    );

    let mut exact = refresh_script(0);
    exact.indexed_modes = vec![mode_at(60); 4096].into();
    assert!(matches!(
        refresh::read_refresh_with(&mut exact),
        Ok(InternalDisplayRefresh::Single(_))
    ));
    assert_eq!(exact.indexed_reads, 4097);

    let mut unbounded = refresh_script(0);
    unbounded.unbounded_modes = true;
    assert_eq!(
        refresh::read_refresh_with(&mut unbounded),
        Err(DisplayReadError::DriverModeLimit)
    );
    assert_eq!(unbounded.indexed_reads, 4097);

    let mut denied = Script::new([Frame::status(ERROR_ACCESS_DENIED)]);
    assert_eq!(
        refresh::read_refresh_with(&mut denied),
        Err(DisplayReadError::Native {
            operation: Operation::ActivePaths,
            status: ERROR_ACCESS_DENIED,
        })
    );
}

#[test]
fn refresh_does_not_read_modes_for_ambiguous_topology_or_guess_options() {
    let active = path(0, DISPLAYCONFIG_PATH_ACTIVE);
    let mut ambiguous = Script::new([Frame::success(vec![active, active], vec![])]);
    assert_eq!(
        refresh::read_refresh_with(&mut ambiguous),
        Ok(InternalDisplayRefresh::MultipleInternalDisplays)
    );
    assert_eq!(ambiguous.reads, (0, 0));

    let mut no_driver = refresh_script(DISPLAYCONFIG_PATH_SUPPORT_VIRTUAL_MODE);
    no_driver.preferred = Some(Err(ERROR_NOT_SUPPORTED));
    let InternalDisplayRefresh::Single(snapshot) =
        refresh::read_refresh_with(&mut no_driver).unwrap()
    else {
        panic!("expected refresh snapshot");
    };
    assert_eq!(
        snapshot.preferred_rate,
        TimingEvidence::NativeFailure(ERROR_NOT_SUPPORTED)
    );
    assert!(snapshot.refresh_options.is_empty());
    assert!(snapshot.current_refresh_key.is_none());

    let mut legacy = refresh_script(0);
    legacy.indexed_modes = [mode_at(60), mode_at(240)].into();
    let InternalDisplayRefresh::Single(snapshot) = refresh::read_refresh_with(&mut legacy).unwrap()
    else {
        panic!("expected refresh snapshot");
    };
    assert_eq!(legacy.preferred_calls, 0);
    assert_eq!(snapshot.preferred_rate, TimingEvidence::NotQueried);
    assert_eq!(snapshot.edid_range, TimingEvidence::NotQueried);
    assert!(
        snapshot
            .refresh_options
            .iter()
            .all(|option| !option.is_dynamic)
    );
    assert!(snapshot.current_refresh_key.is_none());
}

#[test]
fn refresh_discards_evidence_if_gdi_or_ccd_changes_during_reads() {
    let mut gdi = refresh_script(0);
    gdi.current_recheck = Some(DriverMode {
        width: 1920,
        ..mode_at(60)
    });
    assert_eq!(
        refresh::read_refresh_with(&mut gdi),
        Err(DisplayReadError::TopologyChanging)
    );

    let mut ccd = refresh_script(0);
    ccd.frames.back_mut().unwrap().paths[0]
        .targetInfo
        .refreshRate
        .Numerator = 120;
    assert_eq!(
        refresh::read_refresh_with(&mut ccd),
        Err(DisplayReadError::TopologyChanging)
    );
}

#[test]
fn refresh_distinguishes_missing_lookup_from_an_empty_timing_read() {
    let mut api = refresh_script(DISPLAYCONFIG_PATH_SUPPORT_VIRTUAL_MODE);
    api.monitor_path = Some(r"\\?\DISPLAY#PANEL#INSTANCE#{fixture}");
    api.edid = Some(Ok(Vec::new()));
    api.preferred = Some(Ok(SignalTiming {
        width: 1920,
        height: 1080,
        scanline_ordering: 1,
        rate: Some(RefreshRate {
            numerator: 240,
            denominator: 1,
        }),
    }));
    let InternalDisplayRefresh::Single(snapshot) = refresh::read_refresh_with(&mut api).unwrap()
    else {
        panic!("expected refresh snapshot");
    };
    assert_eq!(snapshot.edid_range, TimingEvidence::Unavailable);
    assert_eq!(snapshot.preferred_rate, TimingEvidence::Unavailable);
}

/// The production mutation observation over a scripted CCD/GDI/EDID read,
/// through the plan that SDC_VALIDATE would receive. Validation is refused
/// so nothing past it runs; no Windows call is made.
#[cfg(feature = "display-refresh-write")]
mod mutation {
    use super::*;
    use crate::display::{
        refresh::tests::{set_checksum, type7_edid, write_timing},
        refresh_write::{
            RefreshError, RefreshFailure, RefreshNative, RefreshObservation, RefreshPlan,
            set_internal_refresh,
        },
    };
    use windows_sys::Win32::Devices::Display::DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE;

    const PANEL: &str = r"\\?\DISPLAY#PANEL#INSTANCE#{fixture}";

    /// One validated plan: desktop rate, boost and the explicit target signal.
    type Planned = (
        Option<RefreshRate>,
        bool,
        Option<DISPLAYCONFIG_VIDEO_SIGNAL_INFO>,
    );

    struct Validating {
        api: Script,
        validated: Vec<Planned>,
    }

    impl RefreshNative for Validating {
        fn observe(&mut self, key: &str) -> Result<RefreshObservation, DisplayReadError> {
            RefreshObservation::observe_with(&mut self.api, key)
        }

        fn validate(&mut self, plan: &RefreshPlan) -> u32 {
            self.validated.push((
                plan.desktop_rate(),
                plan.boost(),
                plan.0
                    .selected_mode()
                    .map(|mode| mode.targetVideoSignalInfo),
            ));
            ERROR_NOT_SUPPORTED
        }

        fn apply(&mut self, _: &RefreshPlan) -> u32 {
            unreachable!("validation is refused")
        }

        fn read(&mut self) -> Result<InternalDisplayRefresh, DisplayReadError> {
            unreachable!("validation is refused")
        }
    }

    struct NoFence;

    impl SubmissionFence for NoFence {
        fn topology(&self) -> Result<InternalDisplayTopology, DisplayReadError> {
            unreachable!("validation is refused")
        }

        fn admit(&self) -> bool {
            unreachable!("validation is refused")
        }
    }

    /// A 60 Hz desktop over a 239.76 Hz signal with a valid source mode, as
    /// a writable frame needs (the read path does not check the source mode).
    fn writable(flags: u32) -> Script {
        let virtual_mode = flags & DISPLAYCONFIG_PATH_SUPPORT_VIRTUAL_MODE != 0;
        let mut path = path(
            if virtual_mode { (1 << 16) | 0xffff } else { 1 },
            DISPLAYCONFIG_PATH_ACTIVE | flags,
        );
        path.sourceInfo.Anonymous.modeInfoIdx = if virtual_mode { 0xffff } else { 0 };
        let source = DISPLAYCONFIG_MODE_INFO {
            infoType: DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE,
            id: 3,
            adapterId: SOURCE,
            ..Default::default()
        };
        let mut target = target_mode();
        target.Anonymous.targetMode = DISPLAYCONFIG_TARGET_MODE {
            targetVideoSignalInfo: DISPLAYCONFIG_VIDEO_SIGNAL_INFO {
                activeSize: DISPLAYCONFIG_2DREGION { cx: 2560, cy: 1600 },
                scanLineOrdering: 1,
                vSyncFreq: DISPLAYCONFIG_RATIONAL {
                    Numerator: 240_000,
                    Denominator: 1001,
                },
                ..Default::default()
            },
        };
        let mut script = Script::new([
            Frame::success(vec![path], vec![source, target]),
            Frame::success(vec![path], vec![source, target]),
        ]);
        script.current_mode = Some(mode_at(60));
        script.monitor_path = Some(PANEL);
        script
    }

    fn preferred(numerator: u32, denominator: u32) -> Option<Result<SignalTiming, u32>> {
        Some(Ok(SignalTiming {
            width: 2560,
            height: 1600,
            scanline_ordering: 1,
            rate: Some(RefreshRate {
                numerator,
                denominator,
            }),
        }))
    }

    fn validate(api: Script, key: &str) -> (Result<(), RefreshFailure>, Validating) {
        let mut native = Validating {
            api,
            validated: Vec::new(),
        };
        let result = set_internal_refresh(&mut native, key, &NoFence, &|| false).map(|_| ());
        (result, native)
    }

    fn refused(reason: RefreshError) -> Result<(), RefreshFailure> {
        Err(RefreshFailure {
            reason,
            submission_may_have_occurred: false,
        })
    }

    fn rate(numerator: u32, denominator: u32) -> Option<RefreshRate> {
        Some(RefreshRate {
            numerator,
            denominator,
        })
    }

    fn terms(rational: DISPLAYCONFIG_RATIONAL) -> (u32, u32) {
        (rational.Numerator, rational.Denominator)
    }

    const REJECTED: RefreshError = RefreshError::ValidationRejected(ERROR_NOT_SUPPORTED);

    #[test]
    fn real_edid_timing_reaches_the_plan_with_unreduced_terms() {
        // Issue #884: the panel's 60 Hz DisplayID Type 7 timing.
        let mut edid = type7_edid(1);
        write_timing(&mut edid[136..156], 2560, 1600, 268_620_000, 2720, 1646);
        set_checksum(&mut edid[..128]);
        set_checksum(&mut edid[128..]);
        let mut api = writable(DISPLAYCONFIG_PATH_SUPPORT_VIRTUAL_MODE);
        api.indexed_modes = [mode_at(60), mode_at(240)].into();
        api.preferred = Some(Err(ERROR_NOT_SUPPORTED));
        api.edid = Some(Ok(edid));
        let (result, native) = validate(api, "fixed-60");
        assert_eq!(result, refused(REJECTED));
        let [(desktop, boost, Some(signal))] = native.validated[..] else {
            panic!("expected one explicit-timing plan");
        };
        // FromRatio keeps both representable ratios as the EDID states them.
        assert_eq!(terms(signal.vSyncFreq), (268_620_000, 4_477_120));
        assert_eq!(terms(signal.hSyncFreq), (268_620_000, 2720));
        assert_eq!(signal.pixelRate, 268_620_000);
        assert_eq!((signal.totalSize.cx, signal.totalSize.cy), (2720, 1646));
        assert_eq!(desktop, rate(268_620_000, 4_477_120));
        assert!(!boost);
    }

    #[test]
    fn mutation_reads_the_preferred_timing_on_a_non_virtual_path() {
        let script = || {
            let mut api = writable(0);
            api.indexed_modes = [mode_at(60), mode_at(240)].into();
            api.preferred = preferred(60_000, 1001);
            api.edid = Some(Err(ERROR_ACCESS_DENIED));
            api
        };
        // The ordinary read of a non-virtual path never asks for it.
        let mut read = script();
        assert!(matches!(
            refresh::read_refresh_with(&mut read),
            Ok(InternalDisplayRefresh::Single(_))
        ));
        assert_eq!(read.preferred_calls, 0);

        // Managed FindFixedTargetMode: the preferred 60 Hz timing is exact.
        let (result, native) = validate(script(), "fixed-60");
        assert_eq!(result, refused(REJECTED));
        assert_eq!(native.api.preferred_calls, 1);
        let [(desktop, false, Some(signal))] = native.validated[..] else {
            panic!("expected one fixed explicit-timing plan");
        };
        assert_eq!(terms(signal.vSyncFreq), (60_000, 1001));
        assert_eq!(desktop, rate(60_000, 1001));
    }

    #[test]
    fn requested_dynamic_maximum_is_offered_beside_the_default_one() {
        // Active 239.76 Hz and preferred 165 Hz: both are Dynamic maxima.
        let script = || {
            let mut api = writable(DISPLAYCONFIG_PATH_SUPPORT_VIRTUAL_MODE);
            api.indexed_modes = [mode_at(60), mode_at(165), mode_at(240)].into();
            api.preferred = preferred(165, 1);
            api.edid = Some(Err(ERROR_ACCESS_DENIED));
            api
        };
        // An ordinary read keeps one Dynamic choice, the highest maximum.
        let InternalDisplayRefresh::Single(read) =
            refresh::read_refresh_with(&mut script()).unwrap()
        else {
            panic!("expected refresh snapshot");
        };
        let keys: Vec<_> = read
            .refresh_options
            .iter()
            .map(|option| option.key.as_str())
            .collect();
        assert_eq!(
            keys,
            ["fixed-60", "fixed-165", "fixed-240", "dynamic-60-240"]
        );

        // Managed AddDynamicRefreshOptions also offers the exact request, so
        // its admitted 165 Hz timing is applied with boost at 60 Hz.
        let (result, native) = validate(script(), "dynamic-60-165");
        assert_eq!(result, refused(REJECTED));
        let [(desktop, true, Some(signal))] = native.validated[..] else {
            panic!("expected one boosted plan");
        };
        assert_eq!(desktop, rate(60, 1));
        assert_eq!(terms(signal.vSyncFreq), (165, 1));

        // A Dynamic key that no maximum produces is still unavailable.
        let (result, native) = validate(script(), "dynamic-60-200");
        assert_eq!(result, refused(RefreshError::OptionUnavailable));
        assert!(native.validated.is_empty());
    }

    #[test]
    fn a_non_virtual_preferred_rate_selects_a_timing_but_offers_no_option() {
        // Preferred 165 Hz differs from the 60 Hz current mode. On a
        // non-virtual path it only supplies FindFixedTargetMode's timing;
        // managed BuildRefreshOptions does not advertise it as a rate.
        let script = || {
            let mut api = writable(0);
            api.indexed_modes = [mode_at(60), mode_at(165), mode_at(240)].into();
            api.preferred = preferred(165, 1);
            api.edid = Some(Err(ERROR_ACCESS_DENIED));
            api
        };
        let (result, native) = validate(script(), "fixed-165");
        assert_eq!(result, refused(RefreshError::OptionUnavailable));
        assert_eq!(native.api.preferred_calls, 1);
        assert!(native.validated.is_empty());
        // The admitted fixed rates still validate.
        let (result, native) = validate(script(), "fixed-240");
        assert_eq!(result, refused(REJECTED));
        assert_eq!(native.validated.len(), 1);
    }
}

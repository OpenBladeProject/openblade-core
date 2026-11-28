//! Every refresh writer branch against a scripted native boundary and fence.
//! No test can construct the Windows native (it is excluded from this build),
//! so no SetDisplayConfig call, VALIDATE included, can run here.
use super::*;
use crate::display::{
    AdvancedColorState, EndpointId, InternalDisplayTopology, MonitorName, RefreshOption,
    RefreshSnapshot, TimingEvidence, native, refresh::classify,
};
use std::{
    collections::VecDeque,
    sync::{
        Arc, Mutex,
        atomic::{AtomicBool, Ordering},
    },
};
use windows_sys::Win32::{
    Devices::Display::{
        DISPLAYCONFIG_2DREGION, DISPLAYCONFIG_MODE_INFO, DISPLAYCONFIG_MODE_INFO_0,
        DISPLAYCONFIG_MODE_INFO_TYPE_DESKTOP_IMAGE, DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE,
        DISPLAYCONFIG_MODE_INFO_TYPE_TARGET, DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_EMBEDDED,
        DISPLAYCONFIG_PATH_INFO, DISPLAYCONFIG_RATIONAL, DISPLAYCONFIG_VIDEO_SIGNAL_INFO,
    },
    Foundation::LUID,
    Graphics::Gdi::{
        DISPLAYCONFIG_PATH_ACTIVE, DISPLAYCONFIG_PATH_MODE_IDX_INVALID,
        DISPLAYCONFIG_PATH_SUPPORT_VIRTUAL_MODE,
    },
};
const ADAPTER: LUID = LUID {
    LowPart: 7,
    HighPart: 0,
};
const PATH: &str = r"\\?\DISPLAY#SYN0001#FIXTURE#{guid}";
const WIDTH: u32 = 2560;
const HEIGHT: u32 = 1600;

fn rate(numerator: u32, denominator: u32) -> DISPLAYCONFIG_RATIONAL {
    DISPLAYCONFIG_RATIONAL {
        Numerator: numerator,
        Denominator: denominator,
    }
}

fn hz(hertz: u32) -> DISPLAYCONFIG_RATIONAL {
    rate(hertz, 1)
}

fn signal(vsync: DISPLAYCONFIG_RATIONAL) -> DISPLAYCONFIG_TARGET_MODE {
    DISPLAYCONFIG_TARGET_MODE {
        targetVideoSignalInfo: DISPLAYCONFIG_VIDEO_SIGNAL_INFO {
            activeSize: DISPLAYCONFIG_2DREGION {
                cx: WIDTH,
                cy: HEIGHT,
            },
            vSyncFreq: vsync,
            scanLineOrdering: 1,
            ..Default::default()
        },
    }
}

fn fixed(hertz: i32) -> RefreshOption {
    RefreshOption {
        key: format!("fixed-{hertz}"),
        name: format!("{hertz} Hz"),
        minimum_hertz: hertz,
        maximum_hertz: hertz,
        desktop_hertz: hertz,
        is_dynamic: false,
    }
}

fn dynamic(minimum: i32, maximum: i32, desktop: i32) -> RefreshOption {
    RefreshOption {
        key: format!("dynamic-{minimum}-{maximum}"),
        name: format!("Dynamic ({minimum}-{maximum} Hz)"),
        minimum_hertz: minimum,
        maximum_hertz: maximum,
        desktop_hertz: desktop,
        is_dynamic: true,
    }
}

/// The CCD path state of the scripted panel.
#[derive(Clone, Copy)]
struct Panel {
    desktop: DISPLAYCONFIG_RATIONAL,
    physical: DISPLAYCONFIG_TARGET_MODE,
    boost: bool,
    virtual_mode: bool,
    target: u32,
}

impl Panel {
    /// Issue #884: a 60 Hz desktop over a 240 Hz active signal, DRR off.
    fn split() -> Self {
        Self {
            desktop: hz(60),
            physical: signal(hz(240)),
            boost: false,
            virtual_mode: true,
            target: 5,
        }
    }

    fn display(&self) -> ActiveInternalDisplay {
        let physical = native::signal_timing(self.physical.targetVideoSignalInfo);
        ActiveInternalDisplay {
            source: EndpointId::new(ADAPTER, 3),
            target: EndpointId::new(ADAPTER, self.target),
            source_name: r"\\.\DISPLAY1".into(),
            monitor_name: Ok(MonitorName {
                friendly_name: None,
                device_path: Some(PATH.into()),
            }),
            advanced_color: Ok(AdvancedColorState {
                hdr_supported: true,
                hdr_user_enabled: false,
                bits_per_color_channel: Some(10),
            }),
            desktop_rate: RefreshRate::observed(self.desktop),
            physical_rate: physical.rate,
            active_signal: physical,
            supports_virtual_mode: self.virtual_mode,
            boost_refresh_rate: self.boost,
        }
    }

    fn topology(&self) -> Topology {
        let mut path = DISPLAYCONFIG_PATH_INFO {
            flags: DISPLAYCONFIG_PATH_ACTIVE,
            ..Default::default()
        };
        if self.virtual_mode {
            path.flags |= DISPLAYCONFIG_PATH_SUPPORT_VIRTUAL_MODE;
        }
        if self.boost {
            path.flags |= PATH_BOOST_REFRESH_RATE;
        }
        path.sourceInfo.adapterId = ADAPTER;
        path.sourceInfo.id = 3;
        path.sourceInfo.Anonymous.modeInfoIdx = if self.virtual_mode { 0xffff } else { 0 };
        path.targetInfo.adapterId = ADAPTER;
        path.targetInfo.id = self.target;
        path.targetInfo.outputTechnology = DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_EMBEDDED;
        path.targetInfo.Anonymous.modeInfoIdx = if self.virtual_mode { (1 << 16) | 2 } else { 1 };
        path.targetInfo.refreshRate = self.desktop;
        let mut modes = vec![
            DISPLAYCONFIG_MODE_INFO {
                infoType: DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE,
                id: 3,
                adapterId: ADAPTER,
                ..Default::default()
            },
            DISPLAYCONFIG_MODE_INFO {
                infoType: DISPLAYCONFIG_MODE_INFO_TYPE_TARGET,
                id: self.target,
                adapterId: ADAPTER,
                Anonymous: DISPLAYCONFIG_MODE_INFO_0 {
                    targetMode: self.physical,
                },
            },
        ];
        if self.virtual_mode {
            modes.push(DISPLAYCONFIG_MODE_INFO {
                infoType: DISPLAYCONFIG_MODE_INFO_TYPE_DESKTOP_IMAGE,
                id: self.target,
                adapterId: ADAPTER,
                ..Default::default()
            });
        }
        Topology {
            paths: vec![path],
            modes,
        }
    }

    fn snapshot(&self, options: &[RefreshOption]) -> RefreshSnapshot {
        let display = self.display();
        let current_refresh_key = classify(&display, options).map(|option| option.key.clone());
        RefreshSnapshot {
            display,
            width: WIDTH,
            height: HEIGHT,
            current_mode_hertz: 60,
            message: current_refresh_key.is_none().then_some("unclassified"),
            current_refresh_key,
            refresh_options: options.to_vec(),
            preferred_rate: TimingEvidence::NotQueried,
            edid_range: TimingEvidence::NotQueried,
        }
    }

    fn reading(&self, options: &[RefreshOption]) -> InternalDisplayRefresh {
        InternalDisplayRefresh::Single(Box::new(self.snapshot(options)))
    }
}

/// How a successful APPLY changes the scripted panel.
#[derive(Clone, Copy, PartialEq, Eq)]
enum Effect {
    /// Windows applies the complete plan.
    Plan,
    /// Only the desktop rate and boost flag change; the signal stays.
    DesktopOnly,
    Nothing,
}

type Log = Arc<Mutex<Vec<&'static str>>>;

struct Native {
    panel: Panel,
    options: Vec<RefreshOption>,
    preferred: Option<DISPLAYCONFIG_TARGET_MODE>,
    timings: Vec<edid::DetailedTiming>,
    observed: Option<Result<InternalDisplayRefresh, DisplayReadError>>,
    readbacks: VecDeque<Result<InternalDisplayRefresh, DisplayReadError>>,
    validate_status: u32,
    apply_status: u32,
    effect: Effect,
    log: Log,
    validated: Vec<(Option<RefreshRate>, bool, Option<RefreshRate>)>,
    applied: Vec<(Option<RefreshRate>, bool, Option<RefreshRate>)>,
    /// The target mode of the last applied plan.
    submitted: Option<DISPLAYCONFIG_TARGET_MODE>,
}

impl Native {
    fn new(panel: Panel, options: Vec<RefreshOption>) -> Self {
        Self {
            panel,
            options,
            preferred: None,
            timings: Vec::new(),
            observed: None,
            readbacks: VecDeque::new(),
            validate_status: 0,
            apply_status: 0,
            effect: Effect::Plan,
            log: Log::default(),
            validated: Vec::new(),
            applied: Vec::new(),
            submitted: None,
        }
    }

    fn calls(&self) -> Vec<&'static str> {
        self.log.lock().unwrap().clone()
    }
}

fn describe(plan: &RefreshPlan) -> (Option<RefreshRate>, bool, Option<RefreshRate>) {
    (plan.desktop_rate(), plan.boost(), plan.target_rate())
}

impl RefreshNative for Native {
    fn observe(&mut self, _: &str) -> Result<RefreshObservation, DisplayReadError> {
        self.log.lock().unwrap().push("observe");
        let reading = match self.observed.take() {
            Some(reading) => reading?,
            None => self.panel.reading(&self.options),
        };
        let evidence = matches!(reading, InternalDisplayRefresh::Single(_)).then(|| Evidence {
            topology: self.panel.topology(),
            preferred: self.preferred,
            timings: self.timings.clone(),
        });
        Ok(RefreshObservation { reading, evidence })
    }

    fn validate(&mut self, plan: &RefreshPlan) -> u32 {
        self.log.lock().unwrap().push("validate");
        self.validated.push(describe(plan));
        self.validate_status
    }

    fn apply(&mut self, plan: &RefreshPlan) -> u32 {
        self.log.lock().unwrap().push("apply");
        self.applied.push(describe(plan));
        self.submitted = plan.0.selected_mode();
        let path = plan.0.selected_path();
        match self.effect {
            Effect::Plan => {
                self.panel.desktop = path.targetInfo.refreshRate;
                self.panel.boost = plan.boost();
                self.panel.physical = plan
                    .0
                    .selected_mode()
                    .unwrap_or_else(|| signal(path.targetInfo.refreshRate));
            }
            Effect::DesktopOnly => {
                self.panel.desktop = path.targetInfo.refreshRate;
                self.panel.boost = plan.boost();
            }
            Effect::Nothing => {}
        }
        self.apply_status
    }

    fn read(&mut self) -> Result<InternalDisplayRefresh, DisplayReadError> {
        self.log.lock().unwrap().push("read");
        self.readbacks
            .pop_front()
            .unwrap_or_else(|| Ok(self.panel.reading(&self.options)))
    }
}

struct Fence {
    topology: Mutex<Option<Result<InternalDisplayTopology, DisplayReadError>>>,
    admit: bool,
    log: Log,
    /// Set by `admit`, to model cancellation racing the fence.
    cancel: Option<Arc<AtomicBool>>,
}

impl Fence {
    fn over(native: &Native) -> Self {
        Self {
            topology: Mutex::new(Some(Ok(InternalDisplayTopology::Single(
                native.panel.display(),
            )))),
            admit: true,
            log: native.log.clone(),
            cancel: None,
        }
    }
}

impl SubmissionFence for Fence {
    fn topology(&self) -> Result<InternalDisplayTopology, DisplayReadError> {
        self.log.lock().unwrap().push("fence");
        self.topology
            .lock()
            .unwrap()
            .take()
            .expect("one fenced topology read")
    }

    fn admit(&self) -> bool {
        self.log.lock().unwrap().push("admit");
        if let Some(cancel) = &self.cancel {
            cancel.store(true, Ordering::SeqCst);
        }
        self.admit
    }
}

fn run(native: &mut Native, key: &str, fence: &Fence) -> Result<RefreshOutcome, RefreshFailure> {
    set_internal_refresh(native, key, fence, &|| false)
}

fn refused(reason: RefreshError) -> Result<RefreshOutcome, RefreshFailure> {
    Err(RefreshFailure {
        reason,
        submission_may_have_occurred: false,
    })
}

fn uncertain(reason: RefreshError) -> Result<RefreshOutcome, RefreshFailure> {
    Err(RefreshFailure {
        reason,
        submission_may_have_occurred: true,
    })
}

fn observed(rational: DISPLAYCONFIG_RATIONAL) -> Option<RefreshRate> {
    RefreshRate::observed(rational)
}

/// The panel's 60 Hz DisplayID timing at the active size.
fn panel_timing_60() -> edid::DetailedTiming {
    edid::DetailedTiming {
        width: WIDTH,
        height: HEIGHT,
        hertz: 60,
        pixel_rate: 268_620_000,
        total_width: 2720,
        total_height: 1646,
    }
}

const FULL: [&str; 6] = ["observe", "validate", "fence", "admit", "apply", "read"];

#[test]
fn fixed_rate_submits_the_panel_timing_and_requires_both_rates_to_read_back() {
    // Issue #884: the 60 Hz request carries the panel's explicit timing, so
    // both the desktop rate and the active signal change.
    let mut native = Native::new(Panel::split(), vec![fixed(60), fixed(240)]);
    native.timings = vec![panel_timing_60()];
    let fence = Fence::over(&native);
    assert_eq!(
        run(&mut native, "fixed-60", &fence),
        Ok(RefreshOutcome::Confirmed)
    );
    // Managed DisplayConfigRational.FromRatio keeps the representable EDID
    // terms unreduced for both the vertical and the horizontal rate.
    let vsync = rate(268_620_000, 4_477_120);
    let expected = (observed(vsync), false, observed(vsync));
    assert_eq!(native.validated, [expected]);
    assert_eq!(native.applied, [expected]);
    let signal = native.submitted.unwrap().targetVideoSignalInfo;
    assert_eq!(
        (signal.hSyncFreq.Numerator, signal.hSyncFreq.Denominator),
        (268_620_000, 2720)
    );
    assert_eq!(
        (signal.vSyncFreq.Numerator, signal.vSyncFreq.Denominator),
        (268_620_000, 4_477_120)
    );
    assert_eq!(signal.pixelRate, 268_620_000);
    assert_eq!((signal.totalSize.cx, signal.totalSize.cy), (2720, 1646));
    assert_eq!(native.calls(), FULL);
}

#[test]
fn fixed_rate_uses_the_active_or_preferred_timing_before_edid() {
    // The active signal already runs at the requested maximum.
    let mut native = Native::new(Panel::split(), vec![fixed(60), fixed(240)]);
    let fence = Fence::over(&native);
    assert_eq!(
        run(&mut native, "fixed-240", &fence),
        Ok(RefreshOutcome::Confirmed)
    );
    assert_eq!(
        native.applied,
        [(observed(hz(240)), false, observed(hz(240)))]
    );

    // Windows' preferred 60 Hz timing precedes an EDID timing.
    let preferred = rate(60_000, 1_001);
    let mut native = Native::new(Panel::split(), vec![fixed(60), fixed(240)]);
    native.preferred = Some(signal(preferred));
    native.timings = vec![panel_timing_60()];
    let fence = Fence::over(&native);
    assert_eq!(
        run(&mut native, "fixed-60", &fence),
        Ok(RefreshOutcome::Confirmed)
    );
    assert_eq!(
        native.applied,
        [(observed(preferred), false, observed(preferred))]
    );
}

#[test]
fn fixed_rate_omits_the_target_timing_only_without_an_exact_timing() {
    for virtual_mode in [true, false] {
        let mut panel = Panel::split();
        panel.virtual_mode = virtual_mode;
        let mut native = Native::new(panel, vec![fixed(60), fixed(240)]);
        // A timing at another size is not exact.
        native.timings = vec![edid::DetailedTiming {
            width: 1920,
            ..panel_timing_60()
        }];
        let fence = Fence::over(&native);
        assert_eq!(
            run(&mut native, "fixed-60", &fence),
            Ok(RefreshOutcome::Confirmed)
        );
        assert_eq!(native.validated, [(observed(hz(60)), false, None)]);
        assert_eq!(native.applied, native.validated);
    }

    // The omission fallback must still pass validation; a refusal is
    // certain, consults no fence and applies nothing.
    let mut native = Native::new(Panel::split(), vec![fixed(60), fixed(240)]);
    native.validate_status = 87;
    let fence = Fence::over(&native);
    assert_eq!(
        run(&mut native, "fixed-60", &fence),
        refused(RefreshError::ValidationRejected(87))
    );
    assert_eq!(native.validated, [(observed(hz(60)), false, None)]);
    assert_eq!(native.calls(), ["observe", "validate"]);
}

#[test]
fn dynamic_rate_sets_boost_desktop_rate_and_the_admitted_physical_timing() {
    let mut panel = Panel::split();
    panel.physical = signal(hz(60));
    let options = vec![fixed(60), fixed(240), dynamic(48, 240, 60)];
    let mut native = Native::new(panel, options.clone());
    let preferred = rate(240_000, 1_001);
    native.preferred = Some(signal(preferred));
    let fence = Fence::over(&native);
    assert_eq!(
        run(&mut native, "dynamic-48-240", &fence),
        Ok(RefreshOutcome::Confirmed)
    );
    assert_eq!(
        native.applied,
        [(observed(hz(60)), true, observed(preferred))]
    );

    // Without an admitted physical timing, Dynamic is not prepared at all.
    let mut native = Native::new(panel, options);
    let fence = Fence::over(&native);
    assert_eq!(
        run(&mut native, "dynamic-48-240", &fence),
        refused(RefreshError::Prepare(
            PrepareError::DynamicTimingUnavailable
        ))
    );
    assert_eq!(native.calls(), ["observe"]);
}

#[test]
fn current_unavailable_and_unreadable_requests_submit_nothing() {
    // The fresh read already classifies the request (both rates and boost).
    let mut panel = Panel::split();
    panel.physical = signal(hz(60));
    let mut native = Native::new(panel, vec![fixed(60), fixed(240)]);
    let fence = Fence::over(&native);
    assert_eq!(
        run(&mut native, "fixed-60", &fence),
        Ok(RefreshOutcome::Unchanged)
    );
    assert_eq!(native.calls(), ["observe"]);

    for key in ["fixed-120", "dynamic-48-240", "FIXED-60"] {
        let mut native = Native::new(Panel::split(), vec![fixed(60), fixed(240)]);
        let fence = Fence::over(&native);
        assert_eq!(
            run(&mut native, key, &fence),
            refused(RefreshError::OptionUnavailable)
        );
        assert_eq!(native.calls(), ["observe"]);
    }

    for (reading, reason) in [
        (
            Ok(InternalDisplayRefresh::Empty),
            RefreshError::NoInternalDisplay,
        ),
        (
            Ok(InternalDisplayRefresh::NoInternalDisplay),
            RefreshError::NoInternalDisplay,
        ),
        (
            Ok(InternalDisplayRefresh::MultipleInternalDisplays),
            RefreshError::MultipleInternalDisplays,
        ),
        (
            Err(DisplayReadError::TopologyChanging),
            RefreshError::Topology(DisplayReadError::TopologyChanging),
        ),
    ] {
        let mut native = Native::new(Panel::split(), vec![fixed(60)]);
        native.observed = Some(reading);
        let fence = Fence::over(&native);
        assert_eq!(run(&mut native, "fixed-60", &fence), refused(reason));
        assert_eq!(native.calls(), ["observe"]);
    }
}

#[test]
fn fence_and_cancellation_refuse_after_validation_and_before_apply() {
    let fresh = || Native::new(Panel::split(), vec![fixed(60), fixed(240)]);

    // Admission (Origin, session, power generation, Stop) no longer holds.
    let mut native = fresh();
    let mut fence = Fence::over(&native);
    fence.admit = false;
    assert_eq!(
        run(&mut native, "fixed-240", &fence),
        refused(RefreshError::AdmissionDenied)
    );
    assert_eq!(native.calls(), ["observe", "validate", "fence", "admit"]);

    // The fenced panel is a different target, or its rates changed since
    // the frame the plan was built from.
    let mut replaced = Panel::split();
    replaced.target = 6;
    let mut rate_changed = Panel::split();
    rate_changed.desktop = hz(240);
    for fenced in [
        Ok(InternalDisplayTopology::Single(replaced.display())),
        Ok(InternalDisplayTopology::Single(rate_changed.display())),
        Ok(InternalDisplayTopology::MultipleInternalDisplays),
        Err(DisplayReadError::TopologyChanging),
    ] {
        let mut native = fresh();
        let fence = Fence::over(&native);
        *fence.topology.lock().unwrap() = Some(fenced);
        assert_eq!(
            run(&mut native, "fixed-240", &fence),
            refused(RefreshError::PanelChanged)
        );
        assert_eq!(native.calls(), ["observe", "validate", "fence"]);
    }

    // Cancelled after validation: the fence is never consulted.
    let mut native = fresh();
    let fence = Fence::over(&native);
    assert_eq!(
        set_internal_refresh(&mut native, "fixed-240", &fence, &|| true),
        refused(RefreshError::Cancelled)
    );
    assert_eq!(native.calls(), ["observe", "validate"]);

    // Cancellation racing the fence still precedes the submission.
    let mut native = fresh();
    let cancel = Arc::new(AtomicBool::new(false));
    let mut fence = Fence::over(&native);
    fence.cancel = Some(cancel.clone());
    let cancelled = || cancel.load(Ordering::SeqCst);
    assert_eq!(
        set_internal_refresh(&mut native, "fixed-240", &fence, &cancelled),
        refused(RefreshError::Cancelled)
    );
    assert_eq!(native.calls(), ["observe", "validate", "fence", "admit"]);
}

#[test]
fn apply_error_is_uncertain_even_when_the_readback_matches() {
    for effect in [Effect::Plan, Effect::Nothing] {
        let mut native = Native::new(Panel::split(), vec![fixed(60), fixed(240)]);
        native.apply_status = 87;
        native.effect = effect;
        let fence = Fence::over(&native);
        assert_eq!(
            run(&mut native, "fixed-240", &fence),
            uncertain(RefreshError::Rejected(87))
        );
        assert_eq!(native.calls(), FULL, "the readback always runs");
    }
}

#[test]
fn readback_must_show_the_same_panel_with_both_rates() {
    // Desktop rate confirmed, active signal still 240 Hz: not fixed-60.
    let mut native = Native::new(Panel::split(), vec![fixed(60), fixed(240)]);
    native.timings = vec![panel_timing_60()];
    native.effect = Effect::DesktopOnly;
    let fence = Fence::over(&native);
    assert_eq!(
        run(&mut native, "fixed-60", &fence),
        uncertain(RefreshError::Unconfirmed(Readback::Mismatch))
    );
    assert_eq!(native.applied.len(), 1);

    let mut replaced = Panel::split();
    replaced.target = 6;
    replaced.desktop = hz(240);
    let options = vec![fixed(60), fixed(240)];
    for (readback, expected) in [
        (
            Err(DisplayReadError::CurrentModeUnavailable),
            Readback::Failed(DisplayReadError::CurrentModeUnavailable),
        ),
        (
            Ok(InternalDisplayRefresh::NoInternalDisplay),
            Readback::NoInternalDisplay,
        ),
        (
            Ok(InternalDisplayRefresh::MultipleInternalDisplays),
            Readback::MultipleInternalDisplays,
        ),
        // The same key read back from a replaced panel does not confirm.
        (Ok(replaced.reading(&options)), Readback::PanelChanged),
    ] {
        let mut native = Native::new(Panel::split(), options.clone());
        native.readbacks.push_back(readback);
        let fence = Fence::over(&native);
        assert_eq!(
            run(&mut native, "fixed-240", &fence),
            uncertain(RefreshError::Unconfirmed(expected))
        );
        assert_eq!(native.calls(), FULL);
    }
}

#[test]
fn packet_encoding_keeps_the_frame_indices_it_does_not_change() {
    // The setter linkage and flags are pinned once, in `native`.
    let words = |plan: &RefreshPlan| plan.0.index_words();
    let plan = |panel: Panel, option: RefreshOption, timings: Vec<edid::DetailedTiming>| {
        RefreshPacket::prepare(panel.topology(), &panel.display(), &option, None, &timings)
            .map(RefreshPlan)
            .unwrap()
    };
    let mut non_virtual = Panel::split();
    non_virtual.virtual_mode = false;
    // Explicit timing keeps every index; omission invalidates only the
    // target index (and keeps the virtual desktop image index).
    let explicit = plan(Panel::split(), fixed(60), vec![panel_timing_60()]);
    assert_eq!(words(&explicit), (0xffff, 0x1_0002));
    assert!(explicit.target_rate().is_some());
    let omitted = plan(Panel::split(), fixed(60), vec![]);
    assert_eq!(words(&omitted), (0xffff, 0xffff_0002));
    let omitted = plan(non_virtual, fixed(60), vec![]);
    assert_eq!(words(&omitted), (0, DISPLAYCONFIG_PATH_MODE_IDX_INVALID));
    assert_eq!(omitted.target_rate(), None);
    assert!(!omitted.boost());

    // A boosted path returning to a fixed rate clears the boost flag.
    let mut boosted = Panel::split();
    boosted.boost = true;
    assert!(!plan(boosted, fixed(240), vec![]).boost());

    // A frame without the admitted path cannot be prepared.
    let mut moved = Panel::split();
    moved.target = 6;
    assert!(matches!(
        RefreshPacket::prepare(
            moved.topology(),
            &Panel::split().display(),
            &fixed(60),
            None,
            &[]
        ),
        Err(PrepareError::PathChanged)
    ));
}

fn prepare(
    panel: Panel,
    topology: Topology,
    option: RefreshOption,
    preferred: Option<DISPLAYCONFIG_TARGET_MODE>,
) -> Result<RefreshPlan, PrepareError> {
    RefreshPacket::prepare(topology, &panel.display(), &option, preferred, &[]).map(RefreshPlan)
}

#[test]
fn active_timing_precedes_preferred_unless_dynamic_needs_a_progressive_one() {
    // Managed PrepareRefreshTopology: an active signal at the requested
    // maximum is kept even when Windows prefers another timing for it.
    let panel = Panel::split();
    let preferred = signal(rate(240_000, 1_001));
    let plan = prepare(panel, panel.topology(), fixed(240), Some(preferred)).unwrap();
    assert_eq!(plan.target_rate(), observed(hz(240)));

    // Dynamic accepts the active signal only when it is progressive.
    let mut interlaced = Panel::split();
    interlaced.physical.targetVideoSignalInfo.scanLineOrdering = 2;
    let option = dynamic(48, 240, 60);
    let plan = prepare(
        interlaced,
        interlaced.topology(),
        option.clone(),
        Some(preferred),
    )
    .unwrap();
    assert_eq!(plan.target_rate(), observed(rate(240_000, 1_001)));
    assert!(plan.boost());
    let progressive = prepare(panel, panel.topology(), option, Some(preferred)).unwrap();
    assert_eq!(progressive.target_rate(), observed(hz(240)));
}

#[test]
fn frame_must_hold_exactly_one_admitted_path_with_its_source_mode() {
    let panel = Panel::split();
    // A duplicate active path for the same endpoints is ambiguous.
    let mut duplicated = panel.topology();
    duplicated.paths.push(duplicated.paths[0]);
    assert!(matches!(
        prepare(panel, duplicated, fixed(240), None),
        Err(PrepareError::PathChanged)
    ));
    // An inactive duplicate is not a candidate.
    let mut inactive = panel.topology();
    let mut idle = inactive.paths[0];
    idle.flags &= !DISPLAYCONFIG_PATH_ACTIVE;
    inactive.paths.push(idle);
    assert!(prepare(panel, inactive, fixed(240), None).is_ok());

    // The source index must name this source's mode in the same frame.
    let mut invalid = panel.topology();
    invalid.paths[0].sourceInfo.Anonymous.modeInfoIdx = 0xffff_ffff;
    let mut wrong_type = panel.topology();
    wrong_type.paths[0].sourceInfo.Anonymous.modeInfoIdx = 1 << 16 | 0xffff;
    let mut other_source = panel.topology();
    other_source.modes[0].id = 4;
    let mut out_of_range = panel.topology();
    out_of_range.paths[0].sourceInfo.Anonymous.modeInfoIdx = 9 << 16 | 0xffff;
    for topology in [invalid, wrong_type, other_source, out_of_range] {
        assert!(matches!(
            prepare(panel, topology, fixed(240), None),
            Err(PrepareError::PathChanged)
        ));
    }
}

#[test]
fn dynamic_requires_a_virtual_mode_path_even_with_an_admitted_timing() {
    let mut legacy = Panel::split();
    legacy.virtual_mode = false;
    let preferred = signal(rate(240_000, 1_001));
    assert!(matches!(
        prepare(
            legacy,
            legacy.topology(),
            dynamic(48, 240, 60),
            Some(preferred)
        ),
        Err(PrepareError::DynamicTimingUnavailable)
    ));
    let panel = Panel::split();
    assert!(
        prepare(
            panel,
            panel.topology(),
            dynamic(48, 240, 60),
            Some(preferred)
        )
        .is_ok()
    );
}

/// The readback uses the shared panel identity: a renamed GDI source on the
/// same endpoints and device path still confirms (managed refresh has no
/// identity check), while another source endpoint is a changed panel.
#[test]
fn readback_identity_ignores_the_gdi_source_name_only() {
    let panel = Panel {
        desktop: hz(240),
        ..Panel::split()
    };
    let options = [fixed(60), fixed(240)];
    let admitted = panel.display();
    let mut renamed = panel.snapshot(&options);
    assert_eq!(renamed.current_refresh_key.as_deref(), Some("fixed-240"));
    renamed.display.source_name = r"\.\DISPLAY7".into();
    assert!(
        confirm(
            Ok(InternalDisplayRefresh::Single(Box::new(renamed))),
            &admitted,
            "fixed-240"
        )
        .is_ok()
    );
    let mut moved = panel.snapshot(&options);
    moved.display.source.id = 4;
    assert!(matches!(
        confirm(
            Ok(InternalDisplayRefresh::Single(Box::new(moved))),
            &admitted,
            "fixed-240"
        ),
        Err(Readback::PanelChanged)
    ));
}

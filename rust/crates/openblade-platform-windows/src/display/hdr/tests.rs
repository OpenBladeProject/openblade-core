//! Every HDR writer branch against a scripted native boundary and fence.
//! No test can construct the Windows native (it is excluded from this build).
use super::*;
use crate::display::{AdvancedColorReadError, MonitorName};
use std::{
    cell::Cell,
    collections::VecDeque,
    sync::{
        Mutex,
        atomic::{AtomicBool, AtomicUsize, Ordering},
    },
};

const PATH: &str = r"\\?\DISPLAY#SYN0001#FIXTURE#{guid}";
const NOT_SUPPORTED: u32 = 50;

/// Advanced color as (supported, user enabled), or a native read failure.
type Hdr = Result<(bool, bool), u32>;

fn display(target: u32, path: Option<&str>, hdr: Hdr) -> ActiveInternalDisplay {
    let mut display = ActiveInternalDisplay::brightness_fixture(PATH, target);
    display.monitor_name = match path {
        Some(path) => Ok(MonitorName {
            friendly_name: None,
            device_path: Some(path.into()),
        }),
        None => Err(NOT_SUPPORTED),
    };
    display.advanced_color = hdr
        .map(|(supported, enabled)| AdvancedColorState {
            hdr_supported: supported,
            hdr_user_enabled: supported && enabled,
            bits_per_color_channel: Some(10),
        })
        .map_err(|status| AdvancedColorReadError::Native { status });
    display
}

fn single(hdr: Hdr) -> Result<InternalDisplayTopology, DisplayReadError> {
    Ok(InternalDisplayTopology::Single(display(2, Some(PATH), hdr)))
}

fn admitted() -> HdrTarget {
    HdrTarget::from_display(&display(2, Some(PATH), Ok((true, false))))
}

struct Script {
    reads: VecDeque<Result<InternalDisplayTopology, DisplayReadError>>,
    status: u32,
    submissions: Vec<(EndpointId, bool)>,
}

impl Script {
    fn new(
        reads: impl IntoIterator<Item = Result<InternalDisplayTopology, DisplayReadError>>,
    ) -> Self {
        Self {
            reads: reads.into_iter().collect(),
            status: 0,
            submissions: Vec::new(),
        }
    }
}

impl HdrNative for Script {
    fn topology(&mut self) -> Result<InternalDisplayTopology, DisplayReadError> {
        self.reads.pop_front().expect("unexpected topology read")
    }
    fn submit(&mut self, target: EndpointId, enabled: bool) -> u32 {
        self.submissions.push((target, enabled));
        self.status
    }
}

struct Fence {
    topology: Mutex<Option<Result<InternalDisplayTopology, DisplayReadError>>>,
    admit: bool,
    checks: AtomicUsize,
    /// Set by `admit`, to model cancellation racing the fence.
    cancel: AtomicBool,
    cancel_on_admit: bool,
}

impl Fence {
    fn new(topology: Result<InternalDisplayTopology, DisplayReadError>, admit: bool) -> Self {
        Self {
            topology: Mutex::new(Some(topology)),
            admit,
            checks: AtomicUsize::new(0),
            cancel: AtomicBool::new(false),
            cancel_on_admit: false,
        }
    }
    fn current() -> Self {
        Self::new(single(Ok((true, false))), true)
    }
}

impl SubmissionFence for Fence {
    fn topology(&self) -> Result<InternalDisplayTopology, DisplayReadError> {
        self.checks.fetch_add(1, Ordering::SeqCst);
        self.topology
            .lock()
            .unwrap()
            .take()
            .expect("one fence read")
    }
    fn admit(&self) -> bool {
        if self.cancel_on_admit {
            self.cancel.store(true, Ordering::SeqCst);
        }
        self.admit
    }
}

fn run(native: &mut Script, enabled: bool, fence: &Fence) -> Result<HdrOutcome, HdrFailure> {
    set_internal_hdr(native, &admitted(), enabled, fence, &|| {
        fence.cancel.load(Ordering::SeqCst)
    })
}

fn refused(reason: HdrError) -> Result<HdrOutcome, HdrFailure> {
    Err(HdrFailure {
        reason,
        submission_may_have_occurred: false,
    })
}

fn uncertain(reason: HdrError) -> Result<HdrOutcome, HdrFailure> {
    Err(HdrFailure {
        reason,
        submission_may_have_occurred: true,
    })
}

#[test]
fn one_fenced_submission_to_the_admitted_target_is_confirmed_by_readback() {
    for enabled in [true, false] {
        let mut native = Script::new([single(Ok((true, !enabled))), single(Ok((true, enabled)))]);
        let fence = Fence::new(single(Ok((true, !enabled))), true);
        assert_eq!(run(&mut native, enabled, &fence), Ok(HdrOutcome::Confirmed));
        assert_eq!(
            native.submissions,
            [(display(2, None, Ok((true, true))).target, enabled)]
        );
        assert_eq!(fence.checks.load(Ordering::SeqCst), 1);
        assert!(native.reads.is_empty(), "one fresh read and one readback");
    }
}

#[test]
fn matching_state_submits_nothing_and_skips_the_fence() {
    for enabled in [true, false] {
        let mut native = Script::new([single(Ok((true, enabled)))]);
        let fence = Fence::current();
        assert_eq!(run(&mut native, enabled, &fence), Ok(HdrOutcome::Unchanged));
        assert!(native.submissions.is_empty());
        assert_eq!(fence.checks.load(Ordering::SeqCst), 0);
    }
}

#[test]
fn unsupported_or_unreadable_advanced_color_is_not_supported_without_submission() {
    for hdr in [Ok((false, false)), Err(NOT_SUPPORTED)] {
        for enabled in [true, false] {
            let mut native = Script::new([single(hdr)]);
            let fence = Fence::current();
            assert_eq!(
                run(&mut native, enabled, &fence),
                refused(HdrError::NotSupported)
            );
            assert!(native.submissions.is_empty());
            assert_eq!(fence.checks.load(Ordering::SeqCst), 0);
        }
    }
}

#[test]
fn fresh_read_failures_and_replaced_targets_refuse_before_submission() {
    let cases = [
        (
            Err(DisplayReadError::TopologyChanging),
            HdrError::Topology(DisplayReadError::TopologyChanging),
        ),
        (
            Ok(InternalDisplayTopology::Empty),
            HdrError::NoInternalDisplay,
        ),
        (
            Ok(InternalDisplayTopology::NoInternalDisplay),
            HdrError::NoInternalDisplay,
        ),
        (
            Ok(InternalDisplayTopology::MultipleInternalDisplays),
            HdrError::MultipleInternalDisplays,
        ),
        (
            Ok(InternalDisplayTopology::Single(display(
                3,
                Some(PATH),
                Ok((true, false)),
            ))),
            HdrError::PanelChanged,
        ),
        (
            Ok(InternalDisplayTopology::Single(display(
                2,
                Some(r"\\?\DISPLAY#SYN0002#FIXTURE#{guid}"),
                Ok((true, false)),
            ))),
            HdrError::PanelChanged,
        ),
        (
            Ok(InternalDisplayTopology::Single(display(
                2,
                None,
                Ok((true, false)),
            ))),
            HdrError::PanelChanged,
        ),
    ];
    for (read, reason) in cases {
        let mut native = Script::new([read]);
        let fence = Fence::current();
        assert_eq!(run(&mut native, true, &fence), refused(reason));
        assert!(native.submissions.is_empty());
        assert_eq!(fence.checks.load(Ordering::SeqCst), 0);
    }
}

#[test]
fn device_path_identity_ignores_ascii_case_only() {
    let mut native = Script::new([
        Ok(InternalDisplayTopology::Single(display(
            2,
            Some(&PATH.to_ascii_lowercase()),
            Ok((true, false)),
        ))),
        single(Ok((true, true))),
    ]);
    let fence = Fence::current();
    assert_eq!(run(&mut native, true, &fence), Ok(HdrOutcome::Confirmed));
    assert_eq!(native.submissions.len(), 1);
}

#[test]
fn cancellation_and_every_fence_refusal_submit_nothing() {
    // Canceled before the fence: it is never consulted.
    let mut native = Script::new([single(Ok((true, false)))]);
    let fence = Fence::current();
    fence.cancel.store(true, Ordering::SeqCst);
    assert_eq!(run(&mut native, true, &fence), refused(HdrError::Cancelled));
    assert_eq!(fence.checks.load(Ordering::SeqCst), 0);
    assert!(native.submissions.is_empty());

    for (topology, admit, reason) in [
        (
            Err(DisplayReadError::TopologyChanging),
            true,
            HdrError::PanelChanged,
        ),
        (
            Ok(InternalDisplayTopology::NoInternalDisplay),
            true,
            HdrError::PanelChanged,
        ),
        (
            Ok(InternalDisplayTopology::Single(display(
                5,
                Some(PATH),
                Ok((true, false)),
            ))),
            true,
            HdrError::PanelChanged,
        ),
        (single(Ok((true, false))), false, HdrError::AdmissionDenied),
    ] {
        let mut native = Script::new([single(Ok((true, false)))]);
        let fence = Fence::new(topology, admit);
        assert_eq!(run(&mut native, true, &fence), refused(reason));
        assert_eq!(fence.checks.load(Ordering::SeqCst), 1);
        assert!(native.submissions.is_empty());
    }

    // Canceled while the fence ran.
    let mut native = Script::new([single(Ok((true, false)))]);
    let mut fence = Fence::current();
    fence.cancel_on_admit = true;
    assert_eq!(run(&mut native, true, &fence), refused(HdrError::Cancelled));
    assert!(native.submissions.is_empty());
}

#[test]
fn readback_classifies_every_possible_submission() {
    const INVALID_PARAMETER: u32 = 87;
    let replaced = || {
        Ok(InternalDisplayTopology::Single(display(
            5,
            Some(PATH),
            Ok((true, true)),
        )))
    };
    let cases: [(u32, Result<InternalDisplayTopology, DisplayReadError>, _); 8] = [
        // ResolveHdrMutationResult: an error with a matching readback succeeds.
        (
            INVALID_PARAMETER,
            single(Ok((true, true))),
            Ok(HdrOutcome::Confirmed),
        ),
        (
            INVALID_PARAMETER,
            single(Ok((true, false))),
            uncertain(HdrError::Rejected(INVALID_PARAMETER)),
        ),
        (
            INVALID_PARAMETER,
            Err(DisplayReadError::TopologyChanging),
            uncertain(HdrError::Rejected(INVALID_PARAMETER)),
        ),
        (
            0,
            single(Ok((true, false))),
            uncertain(HdrError::Unconfirmed),
        ),
        (
            0,
            Err(DisplayReadError::TopologyChanging),
            uncertain(HdrError::Unconfirmed),
        ),
        (
            0,
            single(Err(NOT_SUPPORTED)),
            uncertain(HdrError::Unconfirmed),
        ),
        (
            0,
            single(Ok((false, false))),
            uncertain(HdrError::Unconfirmed),
        ),
        (0, replaced(), uncertain(HdrError::Unconfirmed)),
    ];
    for (status, readback, expected) in cases {
        let mut native = Script::new([single(Ok((true, false))), readback]);
        native.status = status;
        let fence = Fence::current();
        assert_eq!(run(&mut native, true, &fence), expected);
        assert_eq!(native.submissions.len(), 1, "never replayed");
    }

    // Disabling while support disappears is not a confirmation either.
    let mut native = Script::new([single(Ok((true, true))), single(Ok((false, false)))]);
    let fence = Fence::new(single(Ok((true, true))), true);
    assert_eq!(
        run(&mut native, false, &fence),
        uncertain(HdrError::Unconfirmed)
    );
}

#[test]
fn cancellation_after_submission_still_reads_back() {
    let mut native = Script::new([single(Ok((true, false))), single(Ok((true, true)))]);
    let fence = Fence::current();
    let calls = Cell::new(0);
    // False through both pre-submission checks, then true.
    let cancelled = || {
        calls.set(calls.get() + 1);
        calls.get() > 2
    };
    assert_eq!(
        set_internal_hdr(&mut native, &admitted(), true, &fence, &cancelled),
        Ok(HdrOutcome::Confirmed)
    );
    assert_eq!(calls.get(), 2, "no cancellation check after the submission");
}

#[test]
fn target_selection_mirrors_the_single_internal_path_rule() {
    assert!(HdrTarget::from_topology(single(Ok((true, false))).unwrap()).is_ok());
    for (topology, reason) in [
        (InternalDisplayTopology::Empty, HdrError::NoInternalDisplay),
        (
            InternalDisplayTopology::NoInternalDisplay,
            HdrError::NoInternalDisplay,
        ),
        (
            InternalDisplayTopology::MultipleInternalDisplays,
            HdrError::MultipleInternalDisplays,
        ),
    ] {
        assert_eq!(HdrTarget::from_topology(topology).unwrap_err(), reason);
    }
}

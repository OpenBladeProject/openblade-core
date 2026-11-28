use std::{
    cell::Cell,
    collections::VecDeque,
    sync::atomic::{AtomicUsize, Ordering},
};

use crate::display::ActiveInternalDisplay;

use super::*;

const PATH: &str = r"\\?\DISPLAY#SDC4226#4&abc&0&UID8388688#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
const INSTANCE: &str = r"display\sdc4226\4&ABC&0&uid8388688_0";
const SIBLING: &str = r"DISPLAY\SDC4226\4&abc&0&UID8388688_1";
const EXTERNAL: &str = r"DISPLAY\OTHER\4&abc&0&UID8388688_0";

enum Call {
    Topology(InternalDisplayTopology),
    TopologyError(TopologyFailure),
    Read(Result<Vec<ReadingRow>, NativeFailure>),
    Methods(Vec<MethodRow<u32>>),
    Set {
        start: bool,
        level: u8,
        response: Result<Acknowledgement, NativeFailure>,
    },
}

struct Script<'a> {
    calls: VecDeque<Call>,
    cancel_after_start: Option<&'a Cell<bool>>,
    cancel_before_start: Option<&'a Cell<bool>>,
}

impl Script<'_> {
    fn new(calls: impl IntoIterator<Item = Call>) -> Self {
        Self {
            calls: calls.into_iter().collect(),
            cancel_after_start: None,
            cancel_before_start: None,
        }
    }
}

impl WmiBrightnessNative for Script<'_> {
    type Method = u32;

    fn topology(
        &mut self,
        budget: &CallBudget<'_>,
    ) -> Result<InternalDisplayTopology, TopologyFailure> {
        assert!(budget.remaining().is_ok());
        match self.calls.pop_front() {
            Some(Call::Topology(value)) => Ok(value),
            Some(Call::TopologyError(error)) => Err(error),
            _ => panic!("unexpected topology query"),
        }
    }

    fn active_readings(
        &mut self,
        budget: &CallBudget<'_>,
        row_limit: usize,
    ) -> Result<Vec<ReadingRow>, NativeFailure> {
        assert!(budget.remaining().is_ok());
        assert_eq!(row_limit, 64);
        let Some(Call::Read(result)) = self.calls.pop_front() else {
            panic!("unexpected active-reading query");
        };
        result
    }

    fn active_methods(
        &mut self,
        budget: &CallBudget<'_>,
        row_limit: usize,
    ) -> Result<Vec<MethodRow<u32>>, NativeFailure> {
        assert!(budget.remaining().is_ok());
        assert_eq!(row_limit, 64);
        let Some(Call::Methods(rows)) = self.calls.pop_front() else {
            panic!("unexpected active-method query");
        };
        Ok(rows)
    }

    fn set_brightness(
        &mut self,
        method: u32,
        level: u8,
        submission: &mut Submission<'_>,
    ) -> Result<Acknowledgement, NativeFailure> {
        let Some(Call::Set {
            start,
            level: expected,
            response,
        }) = self.calls.pop_front()
        else {
            panic!("unexpected write");
        };
        assert_eq!(method, 42);
        assert_eq!(level, expected);
        if start {
            if let Some(cancelled) = self.cancel_before_start {
                cancelled.set(true);
            }
            submission.begin()?;
            if let Some(cancelled) = self.cancel_after_start {
                cancelled.set(true);
            }
            assert!(submission.remaining().is_ok());
            assert_eq!(
                submission.begin(),
                Err(NativeFailure::SubmissionAlreadyStarted)
            );
        }
        response
    }
}

fn topology(path: &str, target: u32) -> Call {
    Call::Topology(InternalDisplayTopology::Single(
        ActiveInternalDisplay::brightness_fixture(path, target),
    ))
}

fn row(instance: &str, percent: u8, levels: &[u8]) -> ReadingRow {
    ReadingRow {
        instance: Some(instance.into()),
        percent: Some(percent),
        levels: Some(levels.into()),
        data_failure: None,
    }
}

fn method(instance: &str, handle: u32) -> MethodRow<u32> {
    MethodRow {
        instance: Some(instance.into()),
        handle,
    }
}

fn observe(instance: &str, percent: u8, levels: &[u8]) -> [Call; 2] {
    [
        topology(PATH, 2),
        Call::Read(Ok(vec![row(instance, percent, levels)])),
    ]
}

fn change(response: Result<Acknowledgement, NativeFailure>, readback: [Call; 2]) -> VecDeque<Call> {
    observe(INSTANCE, 30, &[30, 40])
        .into_iter()
        .chain([Call::Methods(vec![
            method(EXTERNAL, 0),
            method(&INSTANCE.to_ascii_uppercase(), 42),
        ])])
        .chain(observe(INSTANCE, 30, &[30, 40]))
        .chain([topology(PATH, 2)])
        .chain([Call::Set {
            start: true,
            level: 40,
            response,
        }])
        .chain(readback)
        .collect()
}

fn snapshot(percent: u8, supported_levels: &[u8]) -> BrightnessSnapshot {
    BrightnessSnapshot {
        percent,
        supported_levels: supported_levels.into(),
    }
}

fn query_fixture(
    rows: Vec<ReadingRow>,
    last_target: u32,
) -> Result<BrightnessSnapshot, BrightnessError> {
    let expected =
        BrightnessQueryTarget::from_display(&ActiveInternalDisplay::brightness_fixture(PATH, 2))
            .unwrap();
    let mut adapter = WmiBrightness::new(Script::new([
        topology(PATH, 2),
        Call::Read(Ok(rows)),
        topology(PATH, last_target),
    ]));
    adapter.query(&expected, &budget(&|| false))
}

#[test]
fn query_counts_only_valid_matching_rows_and_preserves_full_percent_range() {
    let levels: Vec<_> = (0..=100).collect();
    let input: Vec<_> = levels.iter().rev().copied().chain([100, 0, 255]).collect();
    for percent in [0, 100] {
        let mut missing = row(INSTANCE, 10, &[10]);
        missing.instance = None;
        let rows = vec![
            missing,
            row(EXTERNAL, 50, &[50]),
            row(SIBLING, 8, &[200, 250]),
            row(INSTANCE, 101, &[100]),
            row(INSTANCE, percent, &input),
        ];
        assert_eq!(query_fixture(rows, 2), Ok(snapshot(percent, &levels)));
    }
    assert_eq!(
        query_fixture(vec![row(EXTERNAL, 50, &[50]), row(INSTANCE, 9, &[255])], 2),
        Err(BrightnessError::ReadingUnavailable)
    );
    assert_eq!(
        query_fixture(vec![row(INSTANCE, 0, &[0]), row(SIBLING, 0, &[0])], 2),
        Err(BrightnessError::AmbiguousReading)
    );
    assert_eq!(
        query_fixture(vec![row(INSTANCE, 50, &[50])], 3),
        Err(BrightnessError::PanelChanged)
    );
    assert_eq!(
        query_fixture((0..65).map(|_| row(INSTANCE, 10, &[10])).collect(), 2),
        Err(BrightnessError::Native(NativeFailure::InvalidData))
    );
}

#[test]
fn query_monitor_matches_unicode_and_only_strips_an_int32_suffix() {
    let panel = ActiveInternalDisplay::brightness_fixture(r"\\?\DISPLAY#Écran#ID_A#{guid}", 2);
    assert!(BrightnessTarget::from_display(&panel).is_err());
    let target = BrightnessQueryTarget::from_display(&panel).unwrap();
    for suffix in ["", "_0", "_-2147483648", "_+2147483647", "_  +12\t"] {
        assert!(target.matches(&format!(r"display\écran\id_a{suffix}")));
    }
    for suffix in ["_2147483648", "_-2147483649", "_", "_0xf", "_1_2", "_１２"] {
        assert!(!target.matches(&format!(r"display\écran\id_a{suffix}")));
    }
    let numeric_id = ActiveInternalDisplay::brightness_fixture(r"DISPLAY#Panel#ID_1", 2);
    let target = BrightnessQueryTarget::from_display(&numeric_id).unwrap();
    assert!(!target.matches(r"DISPLAY\Panel\ID_1"));
    assert!(target.matches(r"DISPLAY\Panel\ID_1_0"));
}

#[test]
fn malformed_reading_values_matter_only_after_identity_selection() {
    let invalid = |instance: &str, failure| ReadingRow {
        instance: Some(instance.into()),
        percent: None,
        levels: None,
        data_failure: Some(failure),
    };
    let mut adapter = WmiBrightness::new(Script::new([
        topology(PATH, 2),
        Call::Read(Ok(vec![
            invalid(EXTERNAL, NativeFailure::InvalidData),
            row(INSTANCE, 30, &[30]),
        ])),
        topology(PATH, 2),
        Call::Read(Ok(vec![invalid(INSTANCE, NativeFailure::MissingData)])),
    ]));
    assert_eq!(adapter.read(|| false), Ok(snapshot(30, &[30])));
    assert_eq!(
        adapter.read(|| false),
        Err(BrightnessError::Native(NativeFailure::MissingData))
    );
}

#[test]
fn matches_published_managed_monitor_fixture() {
    let Call::Topology(topology) = topology(PATH, 2) else {
        unreachable!()
    };
    let panel = Panel::from_topology(topology).unwrap();
    for (instance, expected) in [
        (r"DISPLAY\SDC4226\4&abc&0&UID8388688_0", true),
        (r"display\sdc4226\4&ABC&0&uid8388688_12", true),
        (r"DISPLAY\SDC4226\4&abc&0&UID8388688", true),
        (r"DISPLAY\SDC4226\4&abc&0&UID8388689_0", false),
        (EXTERNAL, false),
        (r"DISPLAY\SDC4226\4&abc&0&UID8388688_suffix", false),
        ("", false),
    ] {
        assert_eq!(panel.matches(instance), expected, "{instance}");
    }
}

#[test]
fn read_reports_one_internal_panel_and_normalizes_finite_levels() {
    let mut controller = WmiBrightness::new(Script::new([
        topology(PATH, 2),
        Call::Read(Ok(vec![
            row(EXTERNAL, 80, &[80]),
            row(r"DISPLAY\VIRTUAL\Nichtgerät", 95, &[95]),
            row(INSTANCE, 25, &[100, 30, 20, 0, 20, 255]),
        ])),
    ]));
    assert_eq!(
        controller.read(|| false),
        Ok(snapshot(25, &[0, 20, 30, 100]))
    );
    assert!(controller.native.calls.is_empty());
}

#[test]
fn rejects_absent_or_ambiguous_topology_and_malformed_rows() {
    for (calls, expected) in [
        (
            vec![Call::TopologyError(TopologyFailure::Display(
                DisplayReadError::TopologyChanging,
            ))],
            BrightnessError::Topology(DisplayReadError::TopologyChanging),
        ),
        (
            vec![Call::Topology(InternalDisplayTopology::NoInternalDisplay)],
            BrightnessError::PanelUnavailable,
        ),
        (
            vec![Call::Topology(
                InternalDisplayTopology::MultipleInternalDisplays,
            )],
            BrightnessError::AmbiguousPanel,
        ),
        (
            vec![topology(r"\\?\USB#SDC4226#4&abc&0&UID8388688#{guid}", 2)],
            BrightnessError::PanelUnavailable,
        ),
        (
            observe(EXTERNAL, 30, &[30]).into(),
            BrightnessError::ReadingUnavailable,
        ),
        (
            observe(INSTANCE, 101, &[30]).into(),
            BrightnessError::InvalidReading,
        ),
        (
            observe(INSTANCE, 30, &[255]).into(),
            BrightnessError::InvalidReading,
        ),
        (
            observe(INSTANCE, 30, &[30; 257]).into(),
            BrightnessError::InvalidReading,
        ),
        (
            vec![
                topology(PATH, 2),
                Call::Read(Ok(vec![
                    row(INSTANCE, 30, &[30]),
                    ReadingRow {
                        instance: Some(SIBLING.into()),
                        percent: None,
                        levels: None,
                        data_failure: None,
                    },
                ])),
            ],
            BrightnessError::AmbiguousReading,
        ),
        (
            vec![
                topology(PATH, 2),
                Call::Read(Ok(vec![ReadingRow {
                    instance: None,
                    percent: Some(30),
                    levels: Some(vec![30]),
                    data_failure: None,
                }])),
            ],
            BrightnessError::Native(NativeFailure::InvalidData),
        ),
        (
            vec![
                topology(PATH, 2),
                Call::Read(Ok((0..65).map(|_| row(EXTERNAL, 30, &[30])).collect())),
            ],
            BrightnessError::Native(NativeFailure::InvalidData),
        ),
        (
            vec![
                topology(PATH, 2),
                Call::Read(Err(NativeFailure::Status(-2))),
            ],
            BrightnessError::Native(NativeFailure::Status(-2)),
        ),
    ] {
        let mut controller = WmiBrightness::new(Script::new(calls));
        assert_eq!(controller.read(|| false), Err(expected));
        assert!(controller.native.calls.is_empty());
    }
}

#[test]
fn managed_slider_and_function_row_level_fixtures() {
    use BrightnessRequest::{SetPercent, StepDown, StepUp};

    let coarse = &[0, 25, 50, 75, 100];
    let dense: Vec<_> = (0..=100).collect();
    for (percent, levels, request, expected) in [
        (25, &[0, 20, 30, 100][..], SetPercent(0), Some(0)),
        (25, &[0, 20, 30, 100][..], SetPercent(25), Some(20)),
        (25, &[0, 20, 30, 100][..], SetPercent(26), Some(30)),
        (25, &[0, 20, 30, 100][..], SetPercent(100), Some(100)),
        (50, coarse, StepDown, Some(25)),
        (50, coarse, StepUp, Some(75)),
        (60, coarse, StepDown, Some(50)),
        (60, coarse, StepUp, Some(75)),
        (92, &dense, StepDown, Some(82)),
        (92, &dense, StepUp, Some(100)),
        (5, &[0, 1, 2, 3, 4, 5, 10], StepDown, Some(0)),
        (96, &[90, 96, 97, 98, 99, 100], StepUp, Some(100)),
        (0, coarse, StepDown, None),
        (100, coarse, StepUp, None),
        (50, &[48, 49, 50, 51, 52], StepDown, Some(48)),
        (50, &[48, 49, 50, 51, 52], StepUp, Some(52)),
    ] {
        assert_eq!(select_level(request, &snapshot(percent, levels)), expected);
    }
}

#[test]
fn boundaries_and_current_levels_do_not_invoke_the_setter() {
    for request in [BrightnessRequest::SetPercent(99), BrightnessRequest::StepUp] {
        let mut controller = WmiBrightness::new(Script::new(observe(INSTANCE, 100, &[30, 100])));
        assert_eq!(
            controller.apply(request, || false),
            Ok(BrightnessOutcome::Unchanged(snapshot(100, &[30, 100])))
        );
        assert!(controller.native.calls.is_empty());
    }
    let mut controller = WmiBrightness::new(Script::new([]));
    let failure = controller
        .apply(BrightnessRequest::SetPercent(101), || false)
        .unwrap_err();
    assert_eq!(failure.reason, BrightnessError::InvalidPercent);
    assert!(!failure.submission_may_have_occurred);
}

#[test]
fn controller_admission_requires_one_full_active_instance_match() {
    for (methods, reason) in [
        (
            vec![method(SIBLING, 2)],
            BrightnessError::ControllerUnavailable,
        ),
        (
            vec![
                method(INSTANCE, 42),
                method(&INSTANCE.to_ascii_uppercase(), 7),
            ],
            BrightnessError::AmbiguousController,
        ),
        (
            vec![MethodRow {
                instance: None,
                handle: 42,
            }],
            BrightnessError::Native(NativeFailure::InvalidData),
        ),
    ] {
        let mut controller = WmiBrightness::new(Script::new(
            observe(INSTANCE, 30, &[30, 40])
                .into_iter()
                .chain([Call::Methods(methods)]),
        ));
        let failure = controller
            .apply(BrightnessRequest::SetPercent(40), || false)
            .unwrap_err();
        assert_eq!(failure.reason, reason);
        assert!(!failure.submission_may_have_occurred);
        assert!(controller.native.calls.is_empty());
    }
}

#[test]
fn stale_endpoint_instance_or_supported_level_stops_before_submit() {
    for (fresh, request, expected) in [
        (
            [
                topology(PATH, 3),
                Call::Read(Ok(vec![row(INSTANCE, 30, &[30, 40])])),
            ],
            BrightnessRequest::SetPercent(40),
            BrightnessError::PanelChanged,
        ),
        (
            observe(SIBLING, 30, &[30, 40]),
            BrightnessRequest::SetPercent(40),
            BrightnessError::PanelChanged,
        ),
        (
            observe(INSTANCE, 30, &[30, 50]),
            BrightnessRequest::SetPercent(40),
            BrightnessError::UnsupportedLevel,
        ),
    ] {
        let mut controller = WmiBrightness::new(Script::new(
            observe(INSTANCE, 30, &[30, 40])
                .into_iter()
                .chain([Call::Methods(vec![method(INSTANCE, 42)])])
                .chain(fresh),
        ));
        let failure = controller.apply(request, || false).unwrap_err();
        assert_eq!(failure.reason, expected);
        assert!(!failure.submission_may_have_occurred);
        assert!(controller.native.calls.is_empty());
    }
}

#[test]
fn published_acknowledgement_matrix_requires_same_instance_and_level() {
    for (ack, readback, expected) in [
        (
            Acknowledgement::Boolean(false),
            observe(INSTANCE, 40, &[30, 40]),
            None,
        ),
        (
            Acknowledgement::Boolean(true),
            observe(&INSTANCE.to_ascii_uppercase(), 40, &[30, 40]),
            None,
        ),
        (
            Acknowledgement::Status(0),
            observe(INSTANCE, 40, &[30, 40]),
            None,
        ),
        (
            Acknowledgement::Boolean(false),
            observe(INSTANCE, 30, &[30, 40]),
            Some(BrightnessError::Unconfirmed),
        ),
        (
            Acknowledgement::Boolean(true),
            observe(SIBLING, 40, &[30, 40]),
            Some(BrightnessError::PanelChanged),
        ),
        (
            Acknowledgement::Boolean(true),
            [topology(PATH, 2), Call::Read(Ok(vec![]))],
            Some(BrightnessError::ReadingUnavailable),
        ),
        (
            Acknowledgement::Status(5),
            observe(INSTANCE, 40, &[30, 40]),
            Some(BrightnessError::Rejected(5)),
        ),
        (
            Acknowledgement::Invalid,
            observe(INSTANCE, 40, &[30, 40]),
            Some(BrightnessError::InvalidAcknowledgement),
        ),
    ] {
        let mut controller = WmiBrightness::new(Script::new(change(Ok(ack), readback)));
        let result = controller.apply(BrightnessRequest::SetPercent(41), || false);
        if let Some(reason) = expected {
            let failure = result.unwrap_err();
            assert_eq!(failure.reason, reason);
            assert!(failure.submission_may_have_occurred);
        } else {
            assert_eq!(
                result,
                Ok(BrightnessOutcome::Confirmed(snapshot(40, &[30, 40])))
            );
        }
        assert!(controller.native.calls.is_empty());
    }
}

#[test]
fn after_uncertain_native_call_read_remains_available_but_writes_are_paused() {
    let mut calls = change(
        Err(NativeFailure::DeadlineExceeded),
        observe(INSTANCE, 30, &[30, 40]),
    );
    calls.extend(observe(INSTANCE, 40, &[30, 40]));
    let mut controller = WmiBrightness::new(Script::new(calls));
    let failure = controller
        .apply(BrightnessRequest::StepUp, || false)
        .unwrap_err();
    assert_eq!(
        failure.reason,
        BrightnessError::Native(NativeFailure::DeadlineExceeded)
    );
    assert!(failure.submission_may_have_occurred);
    assert_eq!(failure.state, Some(snapshot(30, &[30, 40])));
    assert_eq!(controller.read(|| false), Ok(snapshot(40, &[30, 40])));
    let blocked = controller
        .apply(BrightnessRequest::StepDown, || false)
        .unwrap_err();
    assert_eq!(blocked.reason, BrightnessError::SubmissionUnresolved);
    assert!(!blocked.submission_may_have_occurred);
    assert!(controller.native.calls.is_empty());
}

#[test]
fn preparation_failure_and_pre_submission_cancellation_leave_no_uncertain_write() {
    for (start, error) in [
        (false, NativeFailure::Status(-1)),
        (true, NativeFailure::Cancelled),
    ] {
        let mut calls = change(
            Err(NativeFailure::Status(-1)),
            observe(INSTANCE, 30, &[30, 40]),
        );
        calls.truncate(6);
        calls.push_back(Call::Set {
            start,
            level: 40,
            response: Err(NativeFailure::Status(-1)),
        });
        let cancelled = Cell::new(false);
        let mut script = Script::new(calls);
        script.cancel_before_start = Some(&cancelled);
        let mut controller = WmiBrightness::new(script);
        let failure = controller
            .apply(BrightnessRequest::StepUp, || cancelled.get())
            .unwrap_err();
        assert_eq!(failure.reason, BrightnessError::Native(error));
        assert!(!failure.submission_may_have_occurred);
        assert!(controller.pause.is_none());
        assert!(controller.native.calls.is_empty());
    }
}

#[test]
fn cancellation_after_submission_does_not_hide_independent_readback() {
    let cancelled = Cell::new(false);
    let mut script = Script::new(change(
        Ok(Acknowledgement::Status(0)),
        observe(INSTANCE, 40, &[30, 40]),
    ));
    script.cancel_after_start = Some(&cancelled);
    let mut controller = WmiBrightness::new(script);
    assert_eq!(
        controller.apply(BrightnessRequest::StepUp, || cancelled.get()),
        Ok(BrightnessOutcome::Confirmed(snapshot(40, &[30, 40])))
    );
    assert!(cancelled.get());
    assert!(controller.native.calls.is_empty());
}

#[test]
fn expired_budget_prevents_native_work_and_submission() {
    let budget = CallBudget {
        deadline: Instant::now(),
        cancelled: &|| false,
        submission_gate: None,
        fence: None,
    };
    let mut controller = WmiBrightness::new(Script::new([]));
    assert!(matches!(
        controller.observe(None, &budget),
        Err(BrightnessError::Native(NativeFailure::DeadlineExceeded))
    ));
    let mut submission = Submission {
        budget: &budget,
        completion_deadline: None,
        panel: None,
    };
    assert_eq!(submission.begin(), Err(NativeFailure::DeadlineExceeded));
    assert!(submission.completion_deadline.is_none());
}

#[test]
fn topology_changed_during_fresh_wmi_enumeration_never_submits() {
    let mut calls = change(
        Ok(Acknowledgement::Status(0)),
        observe(INSTANCE, 40, &[30, 40]),
    );
    calls.truncate(5);
    calls.push_back(topology(PATH, 3));
    let mut controller = WmiBrightness::new(Script::new(calls));
    let failure = controller
        .apply(BrightnessRequest::StepUp, || false)
        .unwrap_err();
    assert_eq!(failure.reason, BrightnessError::PanelChanged);
    assert!(!failure.submission_may_have_occurred);
    assert_eq!(failure.state, None);
    assert!(controller.native.calls.is_empty());
}

#[test]
fn fresh_levels_and_current_drive_the_first_and_only_submission() {
    for (request, fresh, target) in [
        (BrightnessRequest::SetPercent(45), 30, 45),
        (BrightnessRequest::StepUp, 35, 45),
    ] {
        let calls = observe(INSTANCE, 30, &[30, 40])
            .into_iter()
            .chain([Call::Methods(vec![method(INSTANCE, 42)])])
            .chain(observe(INSTANCE, fresh, &[30, 35, 40, 45]))
            .chain([
                topology(PATH, 2),
                Call::Set {
                    start: true,
                    level: target,
                    response: Ok(Acknowledgement::Status(0)),
                },
            ])
            .chain(observe(INSTANCE, target, &[30, 35, 40, 45]));
        let mut controller = WmiBrightness::new(Script::new(calls));
        assert_eq!(
            controller.apply(request, || false),
            Ok(BrightnessOutcome::Confirmed(snapshot(
                target,
                &[30, 35, 40, 45]
            )))
        );
        assert!(controller.native.calls.is_empty());
    }
}

#[test]
fn only_same_instance_target_readback_can_resolve_acknowledged_uncertainty() {
    let mut calls = change(
        Ok(Acknowledgement::Boolean(false)),
        observe(INSTANCE, 30, &[30, 40]),
    );
    calls.extend(observe(SIBLING, 40, &[30, 40]));
    calls.extend(observe(INSTANCE, 40, &[30, 40]));
    calls.extend(observe(INSTANCE, 40, &[30, 40]));
    let mut controller = WmiBrightness::new(Script::new(calls));
    let failure = controller
        .apply(BrightnessRequest::StepUp, || false)
        .unwrap_err();
    assert_eq!(failure.reason, BrightnessError::Unconfirmed);
    assert!(failure.submission_may_have_occurred);
    assert_eq!(controller.read(|| false), Ok(snapshot(40, &[30, 40])));
    assert_eq!(
        controller
            .apply(BrightnessRequest::StepUp, || false)
            .unwrap_err()
            .reason,
        BrightnessError::SubmissionUnresolved
    );
    assert_eq!(controller.read(|| false), Ok(snapshot(40, &[30, 40])));
    assert_eq!(
        controller.apply(BrightnessRequest::StepUp, || false),
        Ok(BrightnessOutcome::Unchanged(snapshot(40, &[30, 40])))
    );
    assert!(controller.native.calls.is_empty());
}

#[test]
fn submission_gets_its_own_completion_budget() {
    let admission = CallBudget {
        deadline: Instant::now() + Duration::from_millis(500),
        cancelled: &|| false,
        submission_gate: None,
        fence: None,
    };
    let mut submission = Submission {
        budget: &admission,
        completion_deadline: None,
        panel: None,
    };
    submission.begin().unwrap();
    assert!(submission.completion_deadline.unwrap() > admission.deadline);
    assert_eq!(
        submission.begin(),
        Err(NativeFailure::SubmissionAlreadyStarted)
    );
}

struct Fence {
    target: u32,
    admit: bool,
    topologies: AtomicUsize,
    admissions: AtomicUsize,
}

impl SubmissionFence for Fence {
    fn topology(&self) -> Result<InternalDisplayTopology, DisplayReadError> {
        self.topologies.fetch_add(1, Ordering::SeqCst);
        Ok(InternalDisplayTopology::Single(
            ActiveInternalDisplay::brightness_fixture(PATH, self.target),
        ))
    }
    fn admit(&self) -> bool {
        self.admissions.fetch_add(1, Ordering::SeqCst);
        self.admit
    }
}

// A targeted observation brackets the WMI read with two topology reads.
fn targeted_change(submit: bool) -> VecDeque<Call> {
    let observe = |percent| {
        [
            topology(PATH, 2),
            Call::Read(Ok(vec![row(INSTANCE, percent, &[30, 40])])),
            topology(PATH, 2),
        ]
    };
    let mut calls: VecDeque<Call> = observe(30)
        .into_iter()
        .chain([Call::Methods(vec![method(INSTANCE, 42)])])
        .chain(observe(30))
        .chain([
            topology(PATH, 2),
            Call::Set {
                start: true,
                level: 40,
                response: Ok(Acknowledgement::Status(0)),
            },
        ])
        .collect();
    if submit {
        calls.extend(observe(40));
    }
    calls
}

fn fenced_apply(
    fence: &Fence,
    calls: VecDeque<Call>,
) -> (Result<BrightnessOutcome, BrightnessFailure>, usize) {
    let gate = worker::RequestGate::default();
    let budget = CallBudget {
        deadline: Instant::now() + CALL_LIMIT,
        cancelled: &|| false,
        submission_gate: Some(&gate),
        fence: Some(fence),
    };
    let mut controller = WmiBrightness::new(Script::new(calls));
    let target =
        BrightnessTarget::from_display(&ActiveInternalDisplay::brightness_fixture(PATH, 2))
            .unwrap();
    let result =
        controller.apply_with_budget(BrightnessRequest::SetPercent(40), Some(&target), &budget);
    let pending = controller.native.calls.len();
    // A refused fence must leave the gate unopened: a later submission
    // attempt on the same request is cancelled rather than reported started.
    (result, pending + usize::from(gate.begin().is_err()))
}

#[test]
fn fence_rechecks_target_and_admission_after_preparation_before_the_gate() {
    for (target, admit, reason) in [
        (3, true, BrightnessError::PanelChanged),
        (
            2,
            false,
            BrightnessError::Native(NativeFailure::AdmissionDenied),
        ),
    ] {
        let fence = Fence {
            target,
            admit,
            topologies: AtomicUsize::new(0),
            admissions: AtomicUsize::new(0),
        };
        // The full targeted preflight reaches the one Set call, whose begin is
        // refused by the fence. No readback follows a refused submission.
        let (result, leftover) = fenced_apply(&fence, targeted_change(false));
        let failure = result.unwrap_err();
        assert_eq!(failure.reason, reason);
        assert!(!failure.submission_may_have_occurred);
        assert_eq!(leftover, 0, "gate stayed closed and no readback was taken");
        assert_eq!(fence.topologies.load(Ordering::SeqCst), 1);
        assert_eq!(
            fence.admissions.load(Ordering::SeqCst),
            usize::from(target == 2)
        );
    }
}

#[test]
fn admitted_fence_allows_exactly_one_submission_and_readback() {
    let fence = Fence {
        target: 2,
        admit: true,
        topologies: AtomicUsize::new(0),
        admissions: AtomicUsize::new(0),
    };
    let (result, leftover) = fenced_apply(&fence, targeted_change(true));
    assert_eq!(
        result,
        Ok(BrightnessOutcome::Confirmed(snapshot(40, &[30, 40])))
    );
    // Gate already started: the extra probe fails with SubmissionAlreadyStarted.
    assert_eq!(leftover, 1);
    assert_eq!(fence.topologies.load(Ordering::SeqCst), 1);
    assert_eq!(fence.admissions.load(Ordering::SeqCst), 1);
}

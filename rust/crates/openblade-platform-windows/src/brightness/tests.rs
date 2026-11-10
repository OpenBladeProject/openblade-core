use std::{cell::Cell, collections::VecDeque};

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
    };
    let mut controller = WmiBrightness::new(Script::new([]));
    assert!(matches!(
        controller.observe(&budget),
        Err(BrightnessError::Native(NativeFailure::DeadlineExceeded))
    ));
    let mut submission = Submission {
        budget: &budget,
        completion_deadline: None,
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
    };
    let mut submission = Submission {
        budget: &admission,
        completion_deadline: None,
    };
    submission.begin().unwrap();
    assert!(submission.completion_deadline.unwrap() > admission.deadline);
    assert_eq!(
        submission.begin(),
        Err(NativeFailure::SubmissionAlreadyStarted)
    );
}

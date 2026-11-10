use super::*;

fn settings() -> Settings {
    serde_json::from_value(json!({
        "mode": 1,
        "fixedSpeedRpm": 4000,
        "manualCurve": {
            "cpu": [
                {"temperatureCelsius": 40, "speedRpm": 2000},
                {"temperatureCelsius": 90, "speedRpm": 5400}
            ],
            "gpu": [
                {"temperatureCelsius": 35, "speedRpm": 2000},
                {"temperatureCelsius": 85, "speedRpm": 5400}
            ],
            "useHigherTemperatureForBoth": true
        }
    }))
    .unwrap()
}

fn setters(device: &Device) -> Vec<&str> {
    device
        .trace
        .iter()
        .map(String::as_str)
        .filter(|report| report.starts_with("0D01") || report.starts_with("0D02"))
        .collect()
}

#[test]
fn typed_reports_match_the_actual_managed_port_oracle() {
    let oracle: std::collections::BTreeMap<String, Vec<String>> = serde_json::from_str(
        include_str!("../../../../tests/Fixtures/Parity/rz09-0581-ordinary-fan-transitions.json"),
    )
    .unwrap();
    let mut device = Device::new();
    assert_eq!(
        device.apply(Topology::Automatic, Topology::Manual),
        RecipeResult::Acknowledged
    );
    assert_eq!(device.targets(), [40, 40]);
    assert_eq!(device.trace, oracle["entry"]);
    device.trace.clear();

    assert_eq!(
        device.apply(Topology::Manual, Topology::Automatic),
        RecipeResult::Acknowledged
    );
    assert_eq!(device.trace, oracle["handback"]);
}

#[test]
fn first_observation_does_not_own_fans_but_a_confirmed_write_allows_handback() {
    for topology in [Topology::Automatic, Topology::Manual] {
        let mut device = Device::new();
        device.restore_scripted_pair(topology);
        let mut controller = Controller::new(device);
        assert_eq!(controller.confirmed, Some(topology));
        assert_eq!(
            controller.handback().unwrap().status,
            ScopedStatus::Unchanged
        );
        assert!(setters(&controller.device).is_empty());

        assert_eq!(
            controller.set_fixed(settings()).unwrap().status,
            ScopedStatus::Applied
        );
        assert!(controller.owned);
        assert_eq!(controller.confirmed, Some(Topology::Manual));
        if topology == Topology::Manual {
            assert_eq!(setters(&controller.device), ["0D01:010128", "0D01:010228"]);
        }
        controller.device.trace.clear();
        assert_eq!(controller.handback().unwrap().status, ScopedStatus::Applied);
        assert_eq!(
            setters(&controller.device),
            ["0D02:01010200", "0D02:01020200"]
        );
        assert!(!controller.owned);
        assert_eq!(controller.confirmed, Some(Topology::Automatic));
        assert_eq!(controller.saved.unwrap().fixed_speed_rpm, 4000);
    }
}

#[test]
fn cancellation_before_submission_is_clean_and_after_submission_blocks_until_readable() {
    let mut controller = Controller::new(Device::new());
    controller.device.fault = Fault::CancelBeforeSubmission;
    assert!(
        matches!(controller.set_fixed(settings()), Err(error) if error.kind() == io::ErrorKind::Interrupted)
    );
    assert!(setters(&controller.device).is_empty());
    assert!(!controller.pending);
    assert!(controller.saved.is_some());

    controller.device.fault = Fault::CancelAfterFirstSubmission;
    assert_eq!(
        controller.set_fixed(settings()).unwrap().status,
        ScopedStatus::Failed
    );
    assert!(controller.pending);
    assert!(!controller.owned);
    assert_eq!(controller.confirmed, Some(Topology::Automatic));
    assert_eq!(setters(&controller.device), ["0D02:01010201"]);
    controller.device.trace.clear();
    assert_eq!(
        controller.set_fixed(settings()).unwrap().status,
        ScopedStatus::Blocked
    );
    assert_eq!(controller.handback().unwrap().status, ScopedStatus::Blocked);
    assert!(controller.device.trace.is_empty());

    controller.observe();
    assert!(controller.pending);
    controller.device.restore_scripted_pair(Topology::Automatic);
    controller.observe();
    assert!(!controller.pending);
    assert!(setters(&controller.device).is_empty());
    assert_eq!(
        controller.set_fixed(settings()).unwrap().status,
        ScopedStatus::Applied
    );
}

#[test]
fn unknown_confirmation_cannot_be_upgraded_to_exact_ownership_by_topology() {
    let mut controller = Controller::new(Device::new());
    controller.device.fault = Fault::LoseFinalConfirmation;
    assert_eq!(
        controller.set_fixed(settings()).unwrap().status,
        ScopedStatus::Failed
    );
    assert_eq!(controller.device.targets(), [40, 40]);
    assert!(controller.pending);
    assert!(!controller.owned);
    assert_eq!(controller.confirmed, Some(Topology::Automatic));

    controller.device.set_readable(true);
    controller.device.trace.clear();
    controller.observe();
    assert_eq!(controller.confirmed, Some(Topology::Manual));
    assert!(!controller.pending);
    assert!(!controller.owned);
    assert_eq!(
        controller.handback().unwrap().status,
        ScopedStatus::Unchanged
    );
    assert!(setters(&controller.device).is_empty());
}

#[test]
fn exact_identity_controller_and_power_are_checked_before_writes_and_again_before_ownership() {
    let mut controller = Controller::new(Device::new());
    controller.device.admission.identity = ("RZ09-0528", 0x1532, 0x02c6);
    assert_eq!(
        controller.set_fixed(settings()).unwrap().status,
        ScopedStatus::Blocked
    );
    controller.device.admission = crate::performance_runtime::device::Admission::scripted();
    controller.device.admission.no_competing_controller = false;
    assert_eq!(
        controller.set_fixed(settings()).unwrap().status,
        ScopedStatus::Blocked
    );
    controller.device.admission = crate::performance_runtime::device::Admission::scripted();
    controller.device.set_full_adapter(false);
    assert_eq!(
        controller.set_fixed(settings()).unwrap().status,
        ScopedStatus::Failed
    );
    assert!(setters(&controller.device).is_empty());
    assert!(!controller.pending);

    controller.device.set_full_adapter(true);
    controller.device.fault = Fault::LoseFullPowerAfterFirstSubmission;
    assert_eq!(
        controller.set_fixed(settings()).unwrap().status,
        ScopedStatus::Failed
    );
    assert_eq!(setters(&controller.device), ["0D02:01010201"]);
    assert!(controller.pending);
    controller.device.set_full_adapter(true);
    controller.device.restore_scripted_pair(Topology::Automatic);
    controller.observe();

    controller.device.fault = Fault::LoseControllerAfterFinalSubmission;
    assert_eq!(
        controller.set_fixed(settings()).unwrap().status,
        ScopedStatus::Applied
    );
    assert!(!controller.owned);
    controller.device.admission.no_competing_controller = true;
    controller.device.trace.clear();
    assert_eq!(
        controller.handback().unwrap().status,
        ScopedStatus::Unchanged
    );
    assert!(controller.device.trace.is_empty());
}

#[test]
fn ambiguous_handback_does_not_retry_or_change_saved_intent() {
    let mut controller = Controller::new(Device::new());
    assert_eq!(
        controller.set_fixed(settings()).unwrap().status,
        ScopedStatus::Applied
    );
    controller.device.trace.clear();
    controller.device.fault = Fault::LoseFirstModeResponse;
    assert_eq!(controller.handback().unwrap().status, ScopedStatus::Failed);
    assert_eq!(controller.confirmed, Some(Topology::Manual));
    assert_eq!(setters(&controller.device), ["0D02:01010200"]);
    assert!(controller.pending);
    assert!(!controller.owned);

    controller.device.trace.clear();
    assert_eq!(controller.handback().unwrap().status, ScopedStatus::Blocked);
    controller.device.restore_scripted_pair(Topology::Automatic);
    controller.observe();
    assert_eq!(
        controller.handback().unwrap().status,
        ScopedStatus::Unchanged
    );
    assert!(setters(&controller.device).is_empty());
    assert_eq!(controller.saved.unwrap().fixed_speed_rpm, 4000);
}

#[test]
fn known_mismatching_scoped_readback_reports_drift_without_pending() {
    let mut controller = Controller::new(Device::new());
    controller.device.fault = Fault::RevertAfterPortConfirmation;

    assert_eq!(
        controller.set_fixed(settings()).unwrap().status,
        ScopedStatus::Failed
    );
    assert_eq!(controller.confirmed, Some(Topology::Automatic));
    assert!(controller.external);
    assert!(!controller.pending);
    assert!(!controller.owned);
    assert_eq!(controller.saved.as_ref().unwrap().fixed_speed_rpm, 4000);
    assert_eq!(
        controller.set_fixed(settings()).unwrap().status,
        ScopedStatus::Applied
    );
    assert!(!controller.external);
}

#[test]
fn observing_the_split_step_retires_an_unconsumed_fault() {
    let mut controller = Controller::new(Device::new());
    Step::ArmSplit.run(&mut controller).unwrap();
    Step::ObserveSplit.run(&mut controller).unwrap();
    Step::ObserveRestoredPair.run(&mut controller).unwrap();
    assert!(setters(&controller.device).is_empty());
    assert_eq!(
        controller.set_fixed(settings()).unwrap().status,
        ScopedStatus::Applied
    );
    assert!(!controller.pending);
}

#[test]
fn unchanged_status_keeps_the_current_revision_and_does_not_publish() {
    let event: Value = serde_json::from_str(crate::wire::CURRENT_FIXTURE).unwrap();
    let scenario = Scenario::new(event["status"].clone()).unwrap();
    let mut status = scenario.subscribe();
    let request = || scenario.apply(None, None).unwrap();
    assert_eq!(request().status, ScopedStatus::Blocked);
    let revision = status.borrow_and_update()["revision"].clone();

    assert_eq!(request().status, ScopedStatus::Blocked);
    assert!(!status.has_changed().unwrap());
    assert_eq!(status.borrow()["revision"], revision);
}

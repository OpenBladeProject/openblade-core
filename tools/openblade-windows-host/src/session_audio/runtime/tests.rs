use super::*;
use AudioEndpoint::{Headphones, Speakers};
use Operation::{Connect, Read, SetSpatial};

#[tokio::test]
async fn fresh_host_and_components_admission_precedes_worker_access() {
    let exact = Evidence::fixture(false);
    let cases = [
        (
            Evidence {
                interactive_session: false,
                ..exact
            },
            Availability::UnsupportedModel,
        ),
        (
            Evidence {
                exact_razer_host: false,
                ..exact
            },
            Availability::UnsupportedModel,
        ),
        (
            Evidence {
                exact_usb_and_codec: false,
                ..exact
            },
            Availability::UnsupportedModel,
        ),
        (
            Evidence {
                pid: 0x02C6,
                ..exact
            },
            Availability::UnsupportedModel,
        ),
        (
            Evidence {
                controllers_absent: false,
                ..exact
            },
            Availability::ControllerActive,
        ),
        (
            Evidence {
                trusted_stack: None,
                ..exact
            },
            Availability::ComponentsUnavailable,
        ),
        (
            Evidence {
                trusted_stack: None,
                ..Evidence::fixture(true)
            },
            Availability::LegacyComponentsUnavailable,
        ),
    ];
    for (evidence, availability) in cases {
        let mut runtime = Runtime::default();
        let mut worker = FixtureWorker::new(exact, false);
        runtime
            .fixture_dispatch(Read, Speakers, exact, &mut worker)
            .await
            .unwrap();
        let refused = runtime
            .fixture_dispatch(SetSpatial(false), Speakers, evidence, &mut worker)
            .await
            .unwrap_err();
        assert_eq!(refused.availability, availability);
        assert_eq!(worker.submissions, 0);
        assert_eq!(
            runtime
                .fixture_dispatch(SetSpatial(false), Speakers, exact, &mut worker)
                .await
                .unwrap_err()
                .status,
            Status::Unavailable
        );
    }

    let read_only = Evidence {
        mutation_allowed: false,
        ..exact
    };
    let mut runtime = Runtime::default();
    let mut worker = FixtureWorker::new(read_only, false);
    assert_eq!(
        runtime
            .fixture_dispatch(Read, Speakers, read_only, &mut worker)
            .await
            .unwrap()
            .0,
        Availability::AvailableReadOnly
    );
    assert_eq!(
        runtime
            .fixture_dispatch(SetSpatial(false), Speakers, read_only, &mut worker)
            .await
            .unwrap_err()
            .status,
        Status::Unavailable
    );
    assert_eq!(worker.submissions, 0);
}

#[tokio::test]
async fn modern_first_read_probes_both_outputs_but_only_confirms_the_requested_one() {
    let exact = Evidence::fixture(false);
    let missing_headphones = Evidence {
        outputs: [Some(1), None],
        ..exact
    };
    let mut runtime = Runtime::default();
    let mut worker = FixtureWorker::new(missing_headphones, false);
    assert_eq!(
        runtime
            .fixture_dispatch(Read, Speakers, missing_headphones, &mut worker)
            .await
            .unwrap_err()
            .status,
        Status::Failed
    );
    assert!(!runtime.probed);

    worker.binding = exact.admit().ok();
    runtime
        .fixture_dispatch(Read, Speakers, exact, &mut worker)
        .await
        .unwrap();
    assert!(runtime.probed);
    assert_eq!(
        runtime
            .fixture_dispatch(SetSpatial(true), Headphones, exact, &mut worker)
            .await
            .unwrap_err()
            .status,
        Status::Unavailable
    );
    assert_eq!(worker.submissions, 0);
}

#[tokio::test]
async fn legacy_proves_the_active_output_without_a_modern_probe() {
    let mut evidence = Evidence::fixture(true);
    let mut runtime = Runtime::default();
    let mut worker = FixtureWorker::new(evidence, false);
    let (_, state) = runtime
        .fixture_dispatch(Read, Speakers, evidence, &mut worker)
        .await
        .unwrap();
    assert_eq!(state.equalizer, FeatureAccess::ObservedOnly as u8);
    assert_eq!(state.application_profiles, FeatureAccess::Unavailable as u8);
    assert_eq!(
        runtime
            .fixture_dispatch(Read, Headphones, evidence, &mut worker)
            .await
            .unwrap_err()
            .status,
        Status::Failed
    );

    evidence.active_output = Headphones;
    worker.binding = evidence.admit().ok();
    assert_eq!(
        runtime
            .fixture_dispatch(SetSpatial(false), Speakers, evidence, &mut worker)
            .await
            .unwrap_err()
            .status,
        Status::Unavailable
    );
    assert_eq!(worker.submissions, 0);
    runtime
        .fixture_dispatch(Read, Headphones, evidence, &mut worker)
        .await
        .unwrap();
    assert!(
        runtime
            .fixture_dispatch(SetSpatial(true), Headphones, evidence, &mut worker)
            .await
            .unwrap()
            .1
            .spatial_enabled
    );
}

#[tokio::test]
async fn changed_binding_before_submission_invalidates_both_confirmations() {
    let exact = Evidence::fixture(false);
    for changed in [
        Evidence {
            trusted_stack: Some(2),
            ..exact
        },
        Evidence {
            outputs: [Some(3), Some(2)],
            ..exact
        },
    ] {
        let mut runtime = Runtime::default();
        let mut worker = FixtureWorker::new(exact, false);
        for endpoint in [Speakers, Headphones] {
            runtime
                .fixture_dispatch(Read, endpoint, exact, &mut worker)
                .await
                .unwrap();
        }
        let fresh = std::cell::Cell::new(exact);
        let refused = runtime
            .dispatch(
                SetSpatial(false),
                Speakers,
                || fresh.replace(changed),
                &mut worker,
            )
            .await
            .unwrap_err();
        assert_eq!(refused.status, Status::Unavailable);
        assert_eq!(worker.submissions, 0);
        assert_eq!(runtime.confirmed, [false; 2]);
        assert!(!runtime.probed);
        assert_eq!(
            runtime
                .fixture_dispatch(Read, Speakers, changed, &mut worker)
                .await
                .unwrap_err()
                .status,
            Status::Failed,
            "stale worker identity must not confirm the new binding"
        );
        worker.binding = changed.admit().ok();
        runtime
            .fixture_dispatch(Read, Speakers, changed, &mut worker)
            .await
            .unwrap();
        assert!(
            runtime
                .fixture_dispatch(SetSpatial(false), Speakers, changed, &mut worker)
                .await
                .is_ok()
        );
    }
}

#[tokio::test]
async fn lost_admission_after_submission_is_indeterminate() {
    let exact = Evidence::fixture(false);
    let mut runtime = Runtime::default();
    let mut worker = FixtureWorker::new(exact, false);
    runtime
        .fixture_dispatch(Read, Speakers, exact, &mut worker)
        .await
        .unwrap();
    let checks = std::cell::Cell::new(0);
    let refused = runtime
        .dispatch(
            SetSpatial(false),
            Speakers,
            || {
                let check = checks.get();
                checks.set(check + 1);
                Evidence {
                    controllers_absent: check < 2,
                    ..exact
                }
            },
            &mut worker,
        )
        .await
        .unwrap_err();
    assert_eq!(refused.status, Status::Indeterminate);
    assert_eq!(worker.submissions, 1);
    assert!(runtime.paused);
}

#[tokio::test]
async fn dropping_a_submitted_operation_retains_pause_across_reentry() {
    use std::{
        future::Future,
        task::{Context, Poll, Waker},
    };

    let exact = Evidence::fixture(false);
    let mut runtime = Runtime::default();
    let mut worker = FixtureWorker::new(exact, false);
    runtime
        .fixture_dispatch(Read, Speakers, exact, &mut worker)
        .await
        .unwrap();
    worker.pending = true;
    {
        let mut future = std::pin::pin!(runtime.fixture_dispatch(
            SetSpatial(false),
            Speakers,
            exact,
            &mut worker
        ));
        assert!(matches!(
            future
                .as_mut()
                .poll(&mut Context::from_waker(Waker::noop())),
            Poll::Pending
        ));
    }
    worker.pending = false;
    for operation in [SetSpatial(true), Connect] {
        assert_eq!(
            runtime
                .fixture_dispatch(operation, Speakers, exact, &mut worker)
                .await
                .unwrap_err()
                .status,
            Status::Unavailable
        );
    }
    assert_eq!(worker.submissions, 1);
    assert!(
        !runtime
            .fixture_dispatch(Read, Speakers, exact, &mut worker)
            .await
            .unwrap()
            .1
            .spatial_enabled
    );
    assert_eq!(worker.submissions, 1, "reconciliation must not replay");
    assert!(
        runtime
            .fixture_dispatch(SetSpatial(true), Speakers, exact, &mut worker)
            .await
            .unwrap()
            .1
            .spatial_enabled
    );
}

#[tokio::test]
async fn acknowledgement_alone_cannot_confirm_value_or_output() {
    struct FaultyWorker {
        worker: FixtureWorker,
        wrong_target: bool,
        submitted: bool,
    }

    impl Worker for FaultyWorker {
        fn read(&self, target: Target) -> Option<Reading> {
            if self.submitted && self.wrong_target {
                return self.worker.read(Target {
                    endpoint: target.endpoint.other(),
                    ..target
                });
            }
            self.worker.read(target)
        }

        async fn set_spatial(&mut self, _: Target, _: bool) -> Submission {
            self.submitted = true;
            Submission::Acknowledged
        }
    }

    let exact = Evidence::fixture(false);
    for wrong_target in [false, true] {
        let mut runtime = Runtime::default();
        let mut worker = FaultyWorker {
            worker: FixtureWorker::new(exact, false),
            wrong_target,
            submitted: false,
        };
        runtime
            .dispatch(Read, Speakers, || exact, &mut worker)
            .await
            .unwrap();
        let refused = runtime
            .dispatch(SetSpatial(false), Speakers, || exact, &mut worker)
            .await
            .unwrap_err();
        assert_eq!(refused.status, Status::Indeterminate);
        assert!(runtime.paused);
        assert_eq!(runtime.confirmed, [false; 2]);
    }
}

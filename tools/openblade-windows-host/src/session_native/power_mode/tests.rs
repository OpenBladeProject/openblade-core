//! Managed `WindowsPowerModeAutomationCoordinator` and its loop over the managed
//! v91 status fixture and a scripted supplier. No test reaches Windows; the
//! retry delay runs on the paused clock.
use super::*;
use serde_json::{Value, json};
use std::{cell::RefCell, collections::VecDeque, rc::Rc};

const FRAMES: &[u8] = include_bytes!("../../../tests/fixtures/get-status-v91.frames");

/// Managed `WindowsPowerModeAssociations` member names by `PerformancePreset`.
pub(super) const PRESETS: [&str; 10] = [
    "silent",
    "balanced",
    "performance",
    "batterySaver",
    "batteryBalanced",
    "custom",
    "turbo",
    "maximumPower",
    "gaming",
    "creator",
];

/// The fixture status as raw JSON: verified, Active, on `source` with
/// `performance` confirmed and only its association set to `mode`.
pub(super) fn status_value(source: i32, performance: Option<i32>, mode: Option<i32>) -> Value {
    let request = i32::from_le_bytes(FRAMES[..4].try_into().unwrap()) as usize;
    let mut frame: Value = serde_json::from_slice(&FRAMES[4 + request + 4..]).unwrap();
    let mut status = frame["status"].take();
    status["deviceVerified"] = json!(true);
    status["controlAccess"] = json!(0);
    status["activePowerSource"] = json!(source);
    status["observationReason"] = json!(0);
    status["unconfirmedWrites"] = json!([]);
    status["confirmedState"]["performance"] = json!(performance);
    if let Some(performance) = performance {
        status["profiles"]["windowsPowerModes"][PRESETS[performance as usize]] = json!(mode);
    }
    status
}

pub(super) fn snapshot(value: Value) -> Snapshot {
    serde_json::from_value(value).unwrap()
}

fn decoded(value: Value) -> Option<Observation> {
    status::decode(
        snapshot(value).power_mode_automation(),
        &mut status::Associations::default(),
    )
}

fn observation(source: u8, performance: u8, mode: Option<Mode>) -> Option<Observation> {
    Some(Observation {
        target: Target {
            supply: if source == 1 { Supply::Dc } else { Supply::Ac },
            performance,
            mode,
        },
        source,
    })
}

#[test]
fn a_confirmed_preset_selects_its_own_association_on_its_supply() {
    for (source, performance, mode, expected) in [
        (0, 0, 0, Mode::BestPowerEfficiency),
        (0, 1, 1, Mode::Balanced),
        (0, 2, 2, Mode::BestPerformance),
        (0, 5, 2, Mode::BestPerformance),
        (1, 3, 0, Mode::BestPowerEfficiency),
        (1, 4, 1, Mode::Balanced),
        (2, 9, 2, Mode::BestPerformance),
    ] {
        assert_eq!(
            decoded(status_value(source, Some(performance), Some(mode))),
            observation(source as u8, performance as u8, Some(expected)),
        );
    }
    // Another preset's association is not this one's.
    let mut value = status_value(0, Some(1), None);
    value["profiles"]["windowsPowerModes"]["performance"] = json!(2);
    assert_eq!(decoded(value), observation(0, 1, None));
}

#[test]
fn an_unconfirmed_status_is_unavailable() {
    let unconfirmed: [fn(&mut Value); 8] = [
        |s| s["controlAccess"] = json!(1),
        |s| s["controlAccess"] = json!(2),
        |s| s["deviceVerified"] = json!(false),
        |s| s["observationReason"] = json!(3),
        |s| s["unconfirmedWrites"] = json!([0]),
        |s| s["unconfirmedWrites"] = json!([0, 1]),
        |s| s["activePowerSource"] = json!(3),
        // FixedMaximum: Balanced with manual fans reports no preset.
        |s| s["confirmedState"]["performance"] = Value::Null,
    ];
    for change in unconfirmed {
        let mut value = status_value(0, Some(1), Some(2));
        change(&mut value);
        assert_eq!(decoded(value), None);
    }
    // Other observation reasons and other unconfirmed subsystems confirm.
    for change in [
        (|s: &mut Value| s["observationReason"] = json!(1)) as fn(&mut Value),
        |s| s["observationReason"] = json!(2),
        |s| s["unconfirmedWrites"] = json!([1, 2, 3]),
        |s| {
            s.as_object_mut().unwrap().remove("observationReason");
        },
        // Managed defaults an absent list to empty.
        |s| {
            s.as_object_mut().unwrap().remove("unconfirmedWrites");
        },
    ] {
        let mut value = status_value(0, Some(1), Some(2));
        change(&mut value);
        assert_eq!(
            decoded(value),
            observation(0, 1, Some(Mode::BestPerformance))
        );
    }
}

#[test]
fn a_status_managed_would_refuse_is_unavailable() {
    let refused: [fn(&mut Value); 15] = [
        |s| s["profiles"]["windowsPowerModes"]["balanced"] = json!(3),
        |s| s["profiles"]["windowsPowerModes"]["balanced"] = json!(-1),
        |s| s["profiles"]["windowsPowerModes"]["balanced"] = json!("Balanced"),
        |s| s["profiles"]["windowsPowerModes"]["unknown"] = json!(null),
        // Another preset's undefined mode refuses the whole list.
        |s| s["profiles"]["windowsPowerModes"]["creator"] = json!(7),
        |s| s["profiles"]["windowsPowerModes"] = Value::Null,
        |s| {
            s["profiles"]
                .as_object_mut()
                .unwrap()
                .remove("windowsPowerModes");
        },
        |s| s["profiles"]["battery"]["windowsPowerMode"] = json!(1),
        |s| s["observationReason"] = json!(4),
        |s| s["observationReason"] = json!("None"),
        |s| s["unconfirmedWrites"] = json!([2, 1]),
        |s| s["unconfirmedWrites"] = json!([1, 1]),
        |s| s["unconfirmedWrites"] = json!([4]),
        // Managed refuses an explicit null.
        |s| s["unconfirmedWrites"] = Value::Null,
        |s| s["confirmedState"]["performance"] = json!(10),
    ];
    for change in refused {
        let mut value = status_value(0, Some(1), Some(2));
        change(&mut value);
        assert_eq!(decoded(value), None);
    }
    // A cleared legacy association and an absent turbo-or-later member are
    // what managed writes and reads.
    let mut value = status_value(0, Some(1), Some(2));
    value["profiles"]["pluggedIn"]["windowsPowerMode"] = Value::Null;
    value["profiles"]["windowsPowerModes"]
        .as_object_mut()
        .unwrap()
        .remove("creator");
    assert_eq!(
        decoded(value),
        observation(0, 1, Some(Mode::BestPerformance))
    );
}

#[test]
fn the_association_cache_follows_every_change_of_the_list() {
    let mut associations = status::Associations::default();
    let mut decode =
        |value: Value| status::decode(snapshot(value).power_mode_automation(), &mut associations);
    let balanced = observation(0, 1, Some(Mode::Balanced));
    assert_eq!(decode(status_value(0, Some(1), Some(1))), balanced);
    assert_eq!(decode(status_value(0, Some(1), Some(1))), balanced);
    // An edited list, a refused one and the original again each decode anew.
    assert_eq!(
        decode(status_value(0, Some(1), Some(2))),
        observation(0, 1, Some(Mode::BestPerformance))
    );
    let mut refused = status_value(0, Some(1), Some(1));
    refused["profiles"]["windowsPowerModes"]["creator"] = json!(7);
    assert_eq!(decode(refused.clone()), None);
    assert_eq!(decode(refused), None);
    assert_eq!(decode(status_value(0, Some(1), Some(1))), balanced);
}

fn observe(
    automation: &Automation,
    source: i32,
    performance: i32,
    mode: Option<i32>,
) -> Classification {
    automation
        .observe(&snapshot(status_value(source, Some(performance), mode)))
        .unwrap()
}

#[test]
fn observation_schedules_only_a_changed_target_with_an_association() {
    let automation = Automation::new();
    assert_eq!(
        observe(&automation, 0, 1, Some(2)),
        Classification::ApplyScheduled
    );
    // The same supply, preset and mode, from UsbC, is the same target.
    assert_eq!(
        observe(&automation, 2, 1, Some(2)),
        Classification::AlreadyObserved
    );
    assert_eq!(
        observe(&automation, 0, 1, Some(2)),
        Classification::AlreadyObserved
    );
    // Only the latest target is scheduled.
    assert_eq!(
        observe(&automation, 1, 1, Some(2)),
        Classification::ApplyScheduled
    );
    let apply = automation.take().unwrap().unwrap();
    assert_eq!(
        (apply.generation, apply.source, apply.supply, apply.mode),
        (2, 1, Supply::Dc, Mode::BestPerformance)
    );
    assert!(automation.take().unwrap().is_none());
    // A cleared association cancels the running apply and schedules nothing.
    assert_eq!(
        observe(&automation, 1, 1, None),
        Classification::LeftUnchanged
    );
    assert!(apply.cancel.is_canceled());
    assert!(automation.take().unwrap().is_none());
    assert_eq!(
        observe(&automation, 1, 1, None),
        Classification::AlreadyObserved
    );
}

#[test]
fn unavailable_cancels_and_forgets_so_the_same_target_applies_again() {
    let automation = Automation::new();
    assert_eq!(
        observe(&automation, 0, 2, Some(2)),
        Classification::ApplyScheduled
    );
    let running = automation.take().unwrap().unwrap();
    assert_eq!(
        observe(&automation, 0, 2, Some(2)),
        Classification::AlreadyObserved
    );
    automation.unavailable().unwrap();
    assert!(running.cancel.is_canceled());
    assert_eq!(
        observe(&automation, 0, 2, Some(2)),
        Classification::ApplyScheduled
    );
    // An unconfirmed status does the same, and an unconfirmed slot never runs.
    let mut value = status_value(0, Some(2), Some(2));
    value["deviceVerified"] = json!(false);
    assert_eq!(
        automation.observe(&snapshot(value)).unwrap(),
        Classification::IgnoredUnavailable
    );
    assert!(automation.take().unwrap().is_none());
    assert_eq!(
        observe(&automation, 0, 2, Some(2)),
        Classification::ApplyScheduled
    );
}

#[test]
fn a_canceled_or_superseded_apply_never_publishes() {
    let automation = Automation::new();
    observe(&automation, 0, 1, Some(1));
    let first = automation.take().unwrap().unwrap();
    observe(&automation, 0, 2, Some(2));
    let second = automation.take().unwrap().unwrap();
    let succeeded = Some(Applied::Succeeded { changed: true });
    assert_eq!(automation.complete(&first, succeeded).unwrap(), None);
    assert_eq!(automation.last_result(), None);
    let published = automation.complete(&second, succeeded).unwrap().unwrap();
    assert_eq!(
        (published.generation, published.mode),
        (2, Mode::BestPerformance)
    );
    assert_eq!(automation.last_result(), Some(published));
    // A result for the current generation after its cancellation is dropped too.
    observe(&automation, 0, 3, Some(0));
    let third = automation.take().unwrap().unwrap();
    automation.unavailable().unwrap();
    assert_eq!(automation.complete(&third, succeeded).unwrap(), None);
    assert_eq!(automation.last_result(), Some(published));
}

/// A scripted Windows power mode: each call takes the next result
/// (default success) and may hold until released.
#[derive(Default)]
struct Script {
    calls: RefCell<Vec<(Supply, Mode)>>,
    results: RefCell<VecDeque<Applied>>,
    hold: RefCell<Option<tokio::sync::oneshot::Receiver<()>>>,
    entered: Notify,
}

impl Script {
    async fn call(&self, supply: Supply, mode: Mode, cancel: Arc<Cancel>) -> io::Result<Applied> {
        self.calls.borrow_mut().push((supply, mode));
        self.entered.notify_one();
        let hold = self.hold.borrow_mut().take();
        if let Some(hold) = hold {
            let _ = hold.await;
        }
        if cancel.is_canceled() {
            return Ok(Applied::Canceled);
        }
        Ok(self
            .results
            .borrow_mut()
            .pop_front()
            .unwrap_or(Applied::Succeeded { changed: true }))
    }
}

const READ: Applied = Applied::Failed(Failure::ReadFailed(5));

struct Rig {
    automation: Automation,
    script: Rc<Script>,
    reports: Rc<RefCell<Vec<Outcome>>>,
    stop: watch::Sender<bool>,
}

impl Rig {
    fn new() -> Self {
        Self {
            automation: Automation::new(),
            script: Rc::default(),
            reports: Rc::default(),
            stop: watch::channel(false).0,
        }
    }

    async fn serve(&self, verify: impl Fn() -> io::Result<()>) -> io::Result<()> {
        let script = self.script.clone();
        let reports = self.reports.clone();
        serve(
            &self.automation,
            async move |supply, mode, cancel| script.call(supply, mode, cancel).await,
            async |cancel| delay(cancel, RETRY_DELAY).await,
            &mut self.stop.subscribe(),
            verify,
            move |outcome| reports.borrow_mut().push(*outcome),
        )
        .await
    }

    fn calls(&self) -> Vec<(Supply, Mode)> {
        self.script.calls.borrow().clone()
    }
}

/// Let the runner reach its next wait.
async fn settle() {
    for _ in 0..16 {
        tokio::task::yield_now().await;
    }
}

#[tokio::test(start_paused = true)]
async fn a_failed_apply_is_retried_once_after_the_delay() {
    let rig = Rig::new();
    rig.script.results.borrow_mut().extend([READ]);
    let serving = rig.serve(|| Ok(()));
    let driving = async {
        observe(&rig.automation, 1, 3, Some(0));
        settle().await;
        assert_eq!(rig.calls(), [(Supply::Dc, Mode::BestPowerEfficiency)]);
        tokio::time::advance(RETRY_DELAY - Duration::from_millis(1)).await;
        settle().await;
        assert_eq!(rig.calls().len(), 1, "the retry waits the whole delay");
        tokio::time::advance(Duration::from_millis(1)).await;
        settle().await;
        assert_eq!(rig.calls().len(), 2);
        let _ = rig.stop.send(true);
    };
    let (served, ()) = tokio::join!(serving, driving);
    served.unwrap();
    let outcome = rig.automation.last_result().unwrap();
    assert_eq!(outcome.applied, Applied::Succeeded { changed: true });
    assert_eq!((outcome.generation, outcome.source), (1, 1));
    // The observer sees each published result once.
    assert_eq!(*rig.reports.borrow(), [outcome]);
}

#[tokio::test(start_paused = true)]
async fn a_second_failure_is_published() {
    let rig = Rig::new();
    let set = Applied::Failed(Failure::SetFailed(87));
    rig.script.results.borrow_mut().extend([READ, set]);
    let serving = rig.serve(|| Ok(()));
    let driving = async {
        observe(&rig.automation, 0, 2, Some(2));
        settle().await;
        tokio::time::advance(RETRY_DELAY).await;
        settle().await;
        let _ = rig.stop.send(true);
    };
    let (served, ()) = tokio::join!(serving, driving);
    served.unwrap();
    assert_eq!(rig.calls().len(), 2);
    let outcome = rig.automation.last_result().unwrap();
    assert_eq!(outcome.applied, set);
    assert_eq!(*rig.reports.borrow(), [outcome]);
}

#[tokio::test(start_paused = true)]
async fn a_newer_target_during_the_delay_replaces_the_retry() {
    let rig = Rig::new();
    rig.script.results.borrow_mut().extend([READ]);
    let serving = rig.serve(|| Ok(()));
    let driving = async {
        observe(&rig.automation, 0, 1, Some(1));
        settle().await;
        assert_eq!(
            observe(&rig.automation, 1, 3, Some(0)),
            Classification::ApplyScheduled
        );
        settle().await;
        // The canceled retry never runs; the new target applies at once.
        assert_eq!(
            rig.calls(),
            [
                (Supply::Ac, Mode::Balanced),
                (Supply::Dc, Mode::BestPowerEfficiency)
            ]
        );
        tokio::time::advance(RETRY_DELAY * 2).await;
        settle().await;
        let _ = rig.stop.send(true);
    };
    let (served, ()) = tokio::join!(serving, driving);
    served.unwrap();
    assert_eq!(rig.calls().len(), 2);
    assert_eq!(rig.automation.last_result().unwrap().generation, 2);
}

#[tokio::test(start_paused = true)]
async fn stop_cancels_the_running_apply_and_waits_for_it() {
    let rig = Rig::new();
    let (release, hold) = tokio::sync::oneshot::channel();
    *rig.script.hold.borrow_mut() = Some(hold);
    let serving = rig.serve(|| Ok(()));
    let driving = async {
        observe(&rig.automation, 0, 1, Some(1));
        rig.script.entered.notified().await;
        let _ = rig.stop.send(true);
        settle().await;
        // The supplier call is still running, so the runner has not ended.
        assert!(!rig.automation.lock().unwrap().retired);
        let _ = release.send(());
    };
    let (served, ()) = tokio::join!(serving, driving);
    served.unwrap();
    assert_eq!(rig.calls().len(), 1);
    assert_eq!(rig.automation.last_result(), None);
    // Nothing is scheduled after the runner ends.
    assert_eq!(
        observe(&rig.automation, 1, 3, Some(0)),
        Classification::IgnoredUnavailable
    );
    assert!(rig.automation.take().unwrap().is_none());
}

#[tokio::test(start_paused = true)]
async fn stop_during_the_delay_ends_without_a_retry() {
    let rig = Rig::new();
    rig.script.results.borrow_mut().extend([READ]);
    let serving = rig.serve(|| Ok(()));
    let driving = async {
        observe(&rig.automation, 0, 1, Some(1));
        settle().await;
        let _ = rig.stop.send(true);
    };
    let (served, ()) = tokio::join!(serving, driving);
    served.unwrap();
    assert_eq!(rig.calls().len(), 1);
    assert_eq!(rig.automation.last_result(), None);
}

#[tokio::test(start_paused = true)]
async fn a_failed_origin_check_ends_the_role_before_windows_is_called() {
    let rig = Rig::new();
    observe(&rig.automation, 0, 1, Some(1));
    // Bounded, so a role that kept serving fails here instead of hanging.
    let error = tokio::time::timeout(
        Duration::from_secs(60),
        rig.serve(|| Err(io::Error::from(io::ErrorKind::PermissionDenied))),
    )
    .await
    .expect("the origin failure must end the role")
    .unwrap_err();
    assert_eq!(error.kind(), io::ErrorKind::PermissionDenied);
    assert!(rig.calls().is_empty());
    assert!(rig.automation.lock().unwrap().retired);
}

#[tokio::test(start_paused = true)]
async fn a_failed_worker_ends_the_role() {
    let automation = Automation::new();
    observe(&automation, 0, 1, Some(1));
    let (_stop, mut receiver) = watch::channel(false);
    let error = serve(
        &automation,
        async |_, _, _| Err(io::Error::other("power-mode worker failed")),
        async |_| true,
        &mut receiver,
        || Ok(()),
        |_| {},
    )
    .await
    .unwrap_err();
    assert_eq!(error.to_string(), "power-mode worker failed");
    assert_eq!(automation.last_result(), None);
}

/// The production call path: the supplier runs on the blocking pool, sees the
/// cancellation, and comes back for the next call.
#[tokio::test]
async fn the_blocking_call_returns_the_supplier_with_its_result() {
    struct Counting(u32);
    impl Supplier for Counting {
        fn apply(&mut self, supply: Supply, mode: Mode, canceled: &AtomicBool) -> Applied {
            self.0 += 1;
            if canceled.load(SeqCst) {
                return Applied::Canceled;
            }
            assert_eq!((supply, mode), (Supply::Ac, Mode::Balanced));
            Applied::Succeeded {
                changed: self.0 == 1,
            }
        }
    }
    let mut supplier = Some(Counting(0));
    let cancel = Arc::new(Cancel::default());
    let first = blocking(&mut supplier, Supply::Ac, Mode::Balanced, cancel.clone()).await;
    assert_eq!(first.unwrap(), Applied::Succeeded { changed: true });
    cancel.cancel();
    let second = blocking(&mut supplier, Supply::Ac, Mode::Balanced, cancel).await;
    assert_eq!(second.unwrap(), Applied::Canceled);
    assert_eq!(supplier.unwrap().0, 2);
}

#[tokio::test(start_paused = true)]
async fn the_delay_ends_early_only_on_cancellation() {
    let cancel = Cancel::default();
    let started = tokio::time::Instant::now();
    assert!(delay(&cancel, RETRY_DELAY).await);
    assert_eq!(started.elapsed(), RETRY_DELAY);
    cancel.cancel();
    assert!(!delay(&cancel, RETRY_DELAY).await);
    assert_eq!(started.elapsed(), RETRY_DELAY);
}

#[test]
fn published_statuses_follow_the_managed_loop() {
    use crate::session_native::service_client::{StatusUpdate, tests::fixture_initial};
    let automation = Automation::new();
    let (_streams, initial) = fixture_initial().unwrap();
    // The fixture's unverified status is unconfirmed.
    publish_status(&automation, &StatusUpdate::Opened(initial), || Ok(())).unwrap();
    assert!(automation.take().unwrap().is_none());
    observe(&automation, 0, 1, Some(1));
    assert!(automation.take().unwrap().is_some());
    publish_status(&automation, &StatusUpdate::Reconnecting, || Ok(())).unwrap();
    assert_eq!(
        observe(&automation, 0, 1, Some(1)),
        Classification::ApplyScheduled
    );
    // A lost Origin refuses the status and marks the automation unavailable.
    let error = publish_status(
        &automation,
        &StatusUpdate::Opened(fixture_initial().unwrap().1),
        || Err(io::Error::from(io::ErrorKind::PermissionDenied)),
    )
    .unwrap_err();
    assert_eq!(error.kind(), io::ErrorKind::PermissionDenied);
    assert!(automation.take().unwrap().is_none());
    assert_eq!(
        observe(&automation, 0, 1, Some(1)),
        Classification::ApplyScheduled
    );
}

#[tokio::test(start_paused = true)]
async fn the_origin_is_checked_again_before_the_retry() {
    let rig = Rig::new();
    rig.script.results.borrow_mut().extend([READ]);
    observe(&rig.automation, 0, 1, Some(1));
    let checks = std::cell::Cell::new(0);
    let error = tokio::time::timeout(
        Duration::from_secs(60),
        rig.serve(|| {
            checks.set(checks.get() + 1);
            if checks.get() == 1 {
                Ok(())
            } else {
                Err(io::Error::from(io::ErrorKind::PermissionDenied))
            }
        }),
    )
    .await
    .expect("the origin failure must end the role")
    .unwrap_err();
    assert_eq!(error.kind(), io::ErrorKind::PermissionDenied);
    // The failed first attempt ran; the retry did not.
    assert_eq!(rig.calls(), [(Supply::Ac, Mode::Balanced)]);
    assert_eq!(rig.automation.last_result(), None);
    assert!(rig.automation.lock().unwrap().retired);
}

#[test]
fn a_status_event_is_refused_when_the_origin_check_fails() {
    use crate::session_native::service_client::{StatusUpdate, tests::fixture_initial};
    let event = |value| {
        let mut observation = fixture_initial().unwrap().1.into_observation();
        observation.snapshot = snapshot(value);
        StatusUpdate::Event(observation)
    };
    let automation = Automation::new();
    publish_status(
        &automation,
        &event(status_value(0, Some(1), Some(1))),
        || Ok(()),
    )
    .unwrap();
    assert!(automation.take().unwrap().is_some());
    // A new target behind a lost Origin is refused and schedules nothing.
    let error = publish_status(
        &automation,
        &event(status_value(0, Some(2), Some(2))),
        || Err(io::Error::from(io::ErrorKind::PermissionDenied)),
    )
    .unwrap_err();
    assert_eq!(error.kind(), io::ErrorKind::PermissionDenied);
    assert!(automation.take().unwrap().is_none());
    // The refusal marked the automation unavailable.
    assert_eq!(
        observe(&automation, 0, 1, Some(1)),
        Classification::ApplyScheduled
    );
}

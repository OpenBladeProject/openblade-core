use super::*;
use crate::gpu_apps::{BlockReason, GpuApp};
use std::{cell::RefCell, rc::Rc, sync::Arc};

const USER: CurrentUser<'_> = CurrentUser {
    sid: b"user",
    session_id: 9,
};
const ACTUAL: Observation<'_> = Observation {
    process_id: 41,
    start_time_utc_ticks: 100,
    user_sid: USER.sid,
    session_id: USER.session_id,
    image_path: r"C:\Fixture\GAME.EXE",
    exited: false,
    owning_process_id: Some(41),
};

#[derive(Debug, PartialEq, Eq)]
enum Event {
    Retain(i32),
    Observe(i32),
    Submit(i32, Kind),
    Drop(i32),
}

#[derive(Clone)]
struct Fake {
    events: Rc<RefCell<Vec<Event>>>,
    observation: Result<Option<Observation<'static>>, ()>,
    submission: Submission,
    cancel_on_observe: Option<Arc<AtomicBool>>,
}

struct Retained {
    fake: Fake,
    requested: i32,
    observation: Observation<'static>,
}

impl Backend for Fake {
    type Retained = Retained;

    fn retain(&mut self, requested: i32) -> Result<Option<Retained>, ()> {
        self.events.borrow_mut().push(Event::Retain(requested));
        self.observation.map(|observation| {
            observation.map(|observation| Retained {
                fake: self.clone(),
                requested,
                observation,
            })
        })
    }
}

impl RetainedTarget for Retained {
    fn observe(&self) -> Observation<'_> {
        self.fake
            .events
            .borrow_mut()
            .push(Event::Observe(self.requested));
        if let Some(cancelled) = &self.fake.cancel_on_observe {
            cancelled.store(true, Relaxed);
        }
        self.observation
    }

    fn submit(&mut self, kind: Kind) -> Submission {
        self.fake
            .events
            .borrow_mut()
            .push(Event::Submit(self.requested, kind));
        self.fake.submission
    }
}

impl Drop for Retained {
    fn drop(&mut self) {
        self.fake
            .events
            .borrow_mut()
            .push(Event::Drop(self.requested));
    }
}

fn fake(observation: Result<Option<Observation<'static>>, ()>, submission: Submission) -> Fake {
    Fake {
        events: Default::default(),
        observation,
        submission,
        cancel_on_observe: None,
    }
}

fn app(pid: i32, name: &str, block_reason: Option<BlockReason>) -> GpuApp {
    GpuApp {
        process_id: pid,
        start_time_utc_ticks: 100,
        name: name.into(),
        utilization_percent: 0.0,
        block_reason,
    }
}

fn target(pid: i32, name: &str) -> Target {
    Target {
        process_id: pid,
        start_time_utc_ticks: 100,
        name: name.into(),
    }
}

fn run(
    apps: Vec<GpuApp>,
    targets: &[Target],
    kind: Kind,
    cancelled: &AtomicBool,
    backend: &mut Fake,
) -> Report {
    decide(
        &GpuAppInventory {
            apps,
            truncated: false,
            omissions: Default::default(),
        },
        targets,
        kind,
        &USER,
        |left, right| left.to_ascii_uppercase().cmp(&right.to_ascii_uppercase()),
        cancelled,
        backend,
    )
}

#[test]
fn both_actions_use_only_the_exact_retained_owner() {
    for kind in [Kind::Close, Kind::ForceClose] {
        let mut backend = fake(Ok(Some(ACTUAL)), Submission::Accepted);
        // A fresh scan can rank the confirmed app outside the response cap.
        let mut apps = (1000..1000 + MAXIMUM_APPS as i32)
            .map(|pid| app(pid, "other", None))
            .collect::<Vec<_>>();
        apps.push(app(41, "Game", None));
        let report = run(
            apps,
            &[target(41, "gAmE")],
            kind,
            &AtomicBool::new(false),
            &mut backend,
        );
        assert_eq!(report.error, None);
        assert_eq!(report.results.len(), 1);
        assert_eq!(report.results[0].error, None);
        assert_eq!(
            *backend.events.borrow(),
            [
                Event::Retain(41),
                Event::Observe(41),
                Event::Submit(41, kind),
                Event::Drop(41)
            ]
        );
    }
}

#[test]
fn rejects_helper_stale_blocked_and_invalid_confirmations_without_retaining() {
    let mut backend = fake(Ok(Some(ACTUAL)), Submission::Accepted);
    let apps = || {
        vec![
            app(41, "Game", None),
            app(42, "dwm", Some(BlockReason::Protected)),
            app(43, "other-session", Some(BlockReason::OtherSession)),
            app(44, "other-account", Some(BlockReason::OtherAccount)),
        ]
    };
    let mut reused = target(41, "Game");
    reused.start_time_utc_ticks -= 1;
    let report = run(
        apps(),
        &[
            target(42, "dwm"),
            target(46, "Game"),
            reused,
            target(43, "other-session"),
            target(44, "other-account"),
        ],
        Kind::Close,
        &AtomicBool::new(false),
        &mut backend,
    );
    assert_eq!(
        report
            .results
            .iter()
            .map(|r| (r.process_id, r.error))
            .collect::<Vec<_>>(),
        [
            (46, Some(STALE)),
            (41, Some(STALE)),
            (42, Some(BlockReason::Protected.message())),
            (43, Some(BlockReason::OtherSession.message())),
            (44, Some(BlockReason::OtherAccount.message())),
        ]
    );
    for (kind, targets) in [
        (
            Kind::ForceClose,
            vec![target(41, "Game"), target(46, "Game")],
        ),
        (Kind::Close, vec![target(41, "Game"), target(41, "Game")]),
    ] {
        let report = run(
            apps(),
            &targets,
            kind,
            &AtomicBool::new(false),
            &mut backend,
        );
        assert!(report.error.is_some());
        assert!(report.results.is_empty());
    }
    assert!(backend.events.borrow().is_empty());
}

#[test]
fn retained_observation_must_match_and_is_always_released() {
    for observation in [
        Observation {
            exited: true,
            ..ACTUAL
        },
        Observation {
            process_id: 46,
            ..ACTUAL
        },
        Observation {
            owning_process_id: None,
            ..ACTUAL
        },
        Observation {
            owning_process_id: Some(46),
            ..ACTUAL
        },
        Observation {
            start_time_utc_ticks: 101,
            ..ACTUAL
        },
        Observation {
            user_sid: b"other",
            ..ACTUAL
        },
        Observation {
            session_id: 10,
            ..ACTUAL
        },
        Observation {
            image_path: r"C:\Fixture\different.exe",
            ..ACTUAL
        },
        Observation {
            image_path: r"C:\Fixture\game.dll",
            ..ACTUAL
        },
        Observation {
            image_path: "",
            ..ACTUAL
        },
    ] {
        let mut backend = fake(Ok(Some(observation)), Submission::Accepted);
        let report = run(
            vec![app(41, "Game", None)],
            &[target(41, "Game")],
            Kind::ForceClose,
            &AtomicBool::new(false),
            &mut backend,
        );
        assert_eq!(report.results[0].error, Some(CHANGED));
        assert_eq!(
            *backend.events.borrow(),
            [Event::Retain(41), Event::Observe(41), Event::Drop(41)]
        );
    }

    // A presentation row alone is not permission, even with no block flag.
    let mut backend = fake(
        Ok(Some(Observation {
            image_path: r"C:\Windows\dwm.exe",
            ..ACTUAL
        })),
        Submission::Accepted,
    );
    let report = run(
        vec![app(41, "dwm", None)],
        &[target(41, "dwm")],
        Kind::Close,
        &AtomicBool::new(false),
        &mut backend,
    );
    assert_eq!(
        report.results[0].error,
        Some("OpenBlade does not close protected processes.")
    );
    assert_eq!(
        *backend.events.borrow(),
        [Event::Retain(41), Event::Observe(41), Event::Drop(41)]
    );
}

#[test]
fn matches_managed_unavailable_no_window_and_definite_failure_results() {
    for (observation, submission, expected) in [
        (Ok(None), Submission::Accepted, CHANGED),
        (Err(()), Submission::Accepted, ACTION_FAILED),
        (
            Ok(Some(ACTUAL)),
            Submission::NoWindow,
            "The application has no window that can be closed gracefully.",
        ),
        (Ok(Some(ACTUAL)), Submission::Rejected, ACTION_FAILED),
    ] {
        let mut backend = fake(observation, submission);
        let report = run(
            vec![app(41, "Game", None)],
            &[target(41, "Game")],
            Kind::Close,
            &AtomicBool::new(false),
            &mut backend,
        );
        assert_eq!(report.results[0].error, Some(expected));
    }
}

#[test]
fn cancellation_after_retaining_blocks_submission_and_further_retains() {
    let mut backend = fake(Ok(Some(ACTUAL)), Submission::Accepted);
    let cancel = Arc::new(AtomicBool::new(false));
    backend.cancel_on_observe = Some(Arc::clone(&cancel));
    let report = run(
        vec![app(41, "Game", None), app(42, "Other", None)],
        &[target(41, "Game"), target(42, "Other")],
        Kind::Close,
        &cancel,
        &mut backend,
    );
    assert_eq!(report.error, Some(STOPPED));
    assert_eq!(report.results.len(), 2);
    assert!(
        report
            .results
            .iter()
            .all(|r| r.error == Some(NOT_ATTEMPTED))
    );
    assert_eq!(
        *backend.events.borrow(),
        [Event::Retain(41), Event::Observe(41), Event::Drop(41)]
    );
}

#[test]
fn ambiguous_submission_has_no_retry_fallback_or_next_target() {
    for kind in [Kind::Close, Kind::ForceClose] {
        let mut backend = fake(Ok(Some(ACTUAL)), Submission::Uncertain);
        let mut targets = vec![target(41, "Game")];
        if kind == Kind::Close {
            targets.push(target(42, "Other"));
        }
        let report = run(
            vec![app(41, "Game", None), app(42, "Other", None)],
            &targets,
            kind,
            &AtomicBool::new(false),
            &mut backend,
        );
        assert_eq!(report.error, Some(UNCERTAIN));
        assert_eq!(report.results.len(), targets.len());
        assert_eq!(report.results[0].error, Some(UNCERTAIN));
        if kind == Kind::Close {
            assert_eq!(report.results[1].error, Some(NOT_ATTEMPTED));
        }
        assert_eq!(
            *backend.events.borrow(),
            [
                Event::Retain(41),
                Event::Observe(41),
                Event::Submit(41, kind),
                Event::Drop(41)
            ]
        );
    }
}

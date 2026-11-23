//! Display automation over injected fakes: every managed trigger, coalescing,
//! retry and stop gates, concurrent events while a transition applies, and the
//! runner's lifetime. The display is a scripted applier or a scripted
//! `ProfileHost` under the real profile composition; the clock is the
//! injected retry delay. No Windows display, file or session call is used.
use super::*;
use crate::session_native::{
    display_query::{
        color_write::SetColorProfile,
        hdr_write::SetHdr,
        manual_write::{Attempt, ManualWriteOutcome},
        profile_apply::{PAUSED, ProfileHost},
        recovery::{Cancel, DURABILITY, Observation},
        refresh_write::{self, SetRefreshRate},
        test_support::Memory,
        write::{CHANGED_POWER, UNCONFIRMED_POWER, manual_turn},
    },
    power_admission::PowerContext,
};
use serde_json::json;
use std::{
    cell::RefCell,
    collections::VecDeque,
    sync::{
        MutexGuard,
        atomic::{AtomicUsize, Ordering},
    },
};

const GAME: &str = "0f8fad5b-d9cb-469f-a165-70867728950e";
const FAILED: &str = "The scripted profile failed.";
const UNCERTAIN: &str = "The scripted profile may have been submitted.";

pub(super) fn settings(
    refresh: Option<&str>,
    hdr: Option<bool>,
    color: Option<&str>,
    brightness: Option<u8>,
) -> Settings {
    Settings {
        refresh_rate_key: refresh.map(str::to_owned),
        hdr_enabled: hdr,
        color_profile_key: color.map(str::to_owned),
        brightness_percent: brightness,
    }
}

fn target(source: u8, application: Option<Settings>) -> Target {
    Target {
        source,
        application: application.as_ref().map(|_| GAME.to_owned()),
        base: settings(None, None, None, Some(40)),
        application_settings: application,
    }
}

/// The scripted panel as a full managed snapshot.
#[derive(Clone)]
pub(super) struct Panel {
    pub(super) available: bool,
    pub(super) refresh: Option<String>,
    pub(super) hdr_supported: bool,
    pub(super) hdr: bool,
    pub(super) color: Option<String>,
    pub(super) brightness: Option<u8>,
}

impl Default for Panel {
    fn default() -> Self {
        Self {
            available: true,
            refresh: Some("fixed-240".into()),
            hdr_supported: true,
            hdr: false,
            color: Some("srgb".into()),
            brightness: Some(50),
        }
    }
}

impl Panel {
    pub(super) fn snapshot(&self) -> Snapshot {
        if !self.available {
            return Snapshot::unavailable("The scripted panel is unavailable.");
        }
        serde_json::from_value(json!({
            "isAvailable": true,
            "displayName": "Synthetic panel",
            "width": 2560,
            "height": 1600,
            "currentRefreshKey": self.refresh,
            "refreshOptions": [],
            "hdrSupported": self.hdr_supported,
            "hdrEnabled": self.hdr,
            "colorProfilesEnabled": true,
            "activeColorProfileId": self.color,
            "colorProfiles": [],
            "message": null,
            "bitsPerColorChannel": 8,
            "brightnessPercent": self.brightness,
            "supportedBrightnessLevels": [0, 20, 40, 50, 60, 80, 100],
        }))
        .unwrap()
    }
}

type During = Box<dyn FnMut(&AtomicBool) + Send>;

/// A scripted applier: each apply takes the next result (default Succeeded)
/// and may run `during` while it holds the display.
#[derive(Default)]
pub(super) struct Scripted {
    pub(super) panel: Panel,
    pub(super) query_fails: bool,
    pub(super) results: VecDeque<(Status, Option<&'static str>, Fields)>,
    pub(super) applied: Vec<(Settings, PowerContext)>,
    pub(super) queries: usize,
    /// Every apply also reports a fatal reply failure.
    fatal: bool,
    during: Option<During>,
}

impl Applier for Scripted {
    fn query(&mut self, canceled: &AtomicBool) -> io::Result<Snapshot> {
        self.queries += 1;
        if self.query_fails {
            return Err(io::Error::other("scripted query failure"));
        }
        if canceled.load(Ordering::Acquire) {
            return Ok(Snapshot::unavailable("canceled"));
        }
        Ok(self.panel.snapshot())
    }

    fn apply(
        &mut self,
        request: &ApplyProfile,
        admitted: PowerContext,
        canceled: &AtomicBool,
    ) -> io::Result<Applied> {
        self.applied.push((request.settings.clone(), admitted));
        if let Some(during) = &mut self.during {
            during(canceled);
        }
        let (status, error, unresolved) =
            self.results
                .pop_front()
                .unwrap_or((Status::Succeeded, None, Fields::default()));
        Ok(Applied {
            outcome: ManualWriteOutcome {
                status,
                error: error.map(Cow::Borrowed),
                state: self.panel.snapshot(),
            },
            unresolved,
            fatal: self
                .fatal
                .then(|| io::Error::other("scripted fatal reply failure")),
        })
    }
}

/// (manual hold, paused, uncertain) and (in flight, cancellation hold, manual
/// pending, pending source).
type Flags = ((bool, bool, Fields), (bool, bool, bool, Option<u8>));

pub(super) struct Rig {
    pub(super) file: Memory,
    pub(super) power: PowerAdmission,
    pub(super) automation: Automation<Memory>,
}

impl Rig {
    pub(super) fn new() -> Self {
        Self::with(Memory::default())
    }

    pub(super) fn with(file: Memory) -> Self {
        let power = PowerAdmission::default();
        let automation = Automation::new(Recovery::load(file.clone()), power.clone());
        Self {
            file,
            power,
            automation,
        }
    }

    pub(super) fn recovery(&self) -> MutexGuard<'_, Recovery<Memory>> {
        self.automation.recovery.lock().unwrap()
    }

    /// The watcher's order: power admission takes the status, then automation.
    pub(super) fn observe(&self, target: Target) -> Observation {
        self.power.observed(i32::from(target.source), false);
        self.recovery().observe(target)
    }

    pub(super) fn unavailable(&self, manual: bool) -> Observation {
        self.power.observed(3, manual);
        self.recovery().mark_unavailable(manual)
    }

    /// Runs the scheduled transition, if any, with a recording delay that is
    /// canceled like managed `Task.Delay(delay, token)`.
    pub(super) fn run(
        &self,
        applier: &mut impl Applier,
    ) -> Option<(io::Result<()>, Vec<Duration>)> {
        let transition = self.recovery().take_transition()?;
        let delays = RefCell::new(Vec::new());
        let result = run_transition(
            &self.automation.recovery,
            applier,
            &transition,
            |cancel, delay| {
                delays.borrow_mut().push(delay);
                !cancel.canceled()
            },
        );
        Some((result, delays.into_inner()))
    }

    /// (manual hold, paused, uncertain) and (in flight, cancellation hold,
    /// manual pending, pending source).
    pub(super) fn flags(&self) -> Flags {
        let recovery = self.recovery();
        (recovery.flags(), recovery.automatic_flags())
    }

    fn paused(&self) -> bool {
        self.flags().0.1
    }

    fn in_flight(&self) -> bool {
        self.flags().1.0
    }

    fn last(&self) -> (Status, Option<String>) {
        let recovery = self.recovery();
        let last = recovery.last_result().expect("a recorded transition");
        (last.status, last.error.as_deref().map(str::to_owned))
    }
}

const IDLE: Flags = (
    (
        false,
        false,
        Fields {
            refresh: false,
            hdr: false,
            color: false,
            brightness: false,
        },
    ),
    (false, false, false, None),
);

#[test]
fn first_confirmed_status_records_the_source_and_schedules_only_an_application() {
    let rig = Rig::new();
    assert_eq!(
        rig.observe(target(1, None)),
        Observation::BaselineEstablished
    );
    assert!(rig.recovery().scheduled().is_none());
    assert_eq!(rig.file.record()["LastReconciledPowerSource"], 1);

    let rig = Rig::new();
    let game = settings(Some("fixed-60"), None, None, None);
    assert_eq!(
        rig.observe(target(0, Some(game))),
        Observation::TransitionScheduled
    );
    let recovery = rig.recovery();
    let scheduled = recovery.scheduled().unwrap();
    assert_eq!(scheduled.target.source, 0);
    assert_eq!(Some(scheduled.admitted), rig.power.capture());
}

#[test]
fn a_profile_change_without_an_application_on_the_same_source_is_not_applied() {
    let rig = Rig::new();
    rig.observe(target(1, None));
    let mut changed = target(1, None);
    changed.base = settings(Some("fixed-60"), Some(true), None, Some(80));
    assert_eq!(rig.observe(changed), Observation::SameSource);
    assert!(rig.recovery().scheduled().is_none());
}

#[test]
fn a_source_change_applies_the_power_profile_and_clears_application_ownership() {
    let rig = Rig::new();
    let game = settings(Some("fixed-60"), None, None, None);
    rig.observe(target(1, Some(game)));
    rig.run(&mut Scripted::default()).unwrap().0.unwrap();
    assert!(rig.file.record()["Baseline"].is_object());
    assert_eq!(rig.file.record()["OwnedRefreshRate"], true);

    let mut plugged = target(0, None);
    plugged.base = settings(Some("fixed-120"), None, None, Some(80));
    assert_eq!(rig.observe(plugged), Observation::TransitionScheduled);
    let record = rig.file.record();
    assert_eq!(record["LastReconciledPowerSource"], 0);
    assert!(record["Baseline"].is_null());
    assert_eq!(record["OwnedRefreshRate"], false);
    assert_eq!(record["ManualHold"], false);
    let mut applier = Scripted::default();
    rig.run(&mut applier).unwrap().0.unwrap();
    // The power profile alone: nothing is restored from the old baseline.
    assert_eq!(
        applier.applied[0].0,
        settings(Some("fixed-120"), None, None, Some(80))
    );
    assert_eq!(applier.applied[0].1.source, 0);
    assert_eq!(rig.last(), (Status::Succeeded, None));
}

#[test]
fn an_application_captures_its_baseline_and_restores_only_what_it_owned() {
    let rig = Rig::new();
    rig.observe(target(1, None));
    let game = settings(Some("fixed-60"), Some(true), None, None);
    assert_eq!(
        rig.observe(target(1, Some(game))),
        Observation::TransitionScheduled
    );
    let mut applier = Scripted::default();
    rig.run(&mut applier).unwrap().0.unwrap();
    assert_eq!(applier.queries, 1, "the baseline is read once");
    // The override wins; brightness still follows the power profile.
    assert_eq!(
        applier.applied[0].0,
        settings(Some("fixed-60"), Some(true), None, Some(40))
    );
    let record = rig.file.record();
    assert_eq!(
        record["Baseline"],
        json!({"RefreshRateKey": "fixed-240", "HdrEnabled": false,
            "ColorProfileKey": "srgb", "BrightnessPercent": 50})
    );
    assert_eq!(record["ApplicationProfileId"], GAME);
    assert_eq!(record["OwnedRefreshRate"], true);
    assert_eq!(record["OwnedHdr"], true);
    assert_eq!(record["AutomaticWritesPaused"], false);

    // The application exits: owned fields return to the baseline.
    assert_eq!(
        rig.observe(target(1, None)),
        Observation::TransitionScheduled
    );
    let mut applier = Scripted::default();
    rig.run(&mut applier).unwrap().0.unwrap();
    assert_eq!(applier.queries, 0);
    assert_eq!(
        applier.applied[0].0,
        settings(Some("fixed-240"), Some(false), None, Some(40))
    );
    let record = rig.file.record();
    assert!(record["Baseline"].is_null());
    assert!(record["ApplicationProfileId"].is_null());
    assert_eq!(record["OwnedRefreshRate"], false);
}

#[test]
fn an_unavailable_baseline_read_writes_nothing() {
    let rig = Rig::new();
    rig.observe(target(1, None));
    rig.observe(target(
        1,
        Some(settings(Some("fixed-60"), None, None, None)),
    ));
    let mut applier = Scripted {
        panel: Panel {
            available: false,
            ..Panel::default()
        },
        ..Scripted::default()
    };
    rig.run(&mut applier).unwrap().0.unwrap();
    assert!(applier.applied.is_empty());
    assert_eq!(rig.last(), (Status::Succeeded, None));
    assert!(rig.file.record()["Baseline"].is_null());
    assert_eq!(rig.flags(), IDLE);
}

#[test]
fn an_application_cannot_own_a_refresh_rate_it_cannot_restore() {
    let rig = Rig::new();
    rig.observe(target(1, None));
    rig.observe(target(
        1,
        Some(settings(Some("fixed-60"), Some(true), None, None)),
    ));
    let mut applier = Scripted {
        panel: Panel {
            refresh: None,
            ..Panel::default()
        },
        ..Scripted::default()
    };
    rig.run(&mut applier).unwrap().0.unwrap();
    assert_eq!(
        applier.applied[0].0,
        settings(None, Some(true), None, Some(40))
    );
    assert_eq!(rig.file.record()["OwnedRefreshRate"], false);
}

#[test]
fn recorded_refresh_ownership_without_a_restorable_rate_is_blocked() {
    let file = Memory::with(&format!(
        r#"{{"SchemaVersion":4,"LastReconciledPowerSource":1,"ApplicationProfileId":"{GAME}","Baseline":{{"RefreshRateKey":null,"HdrEnabled":null,"ColorProfileKey":null,"BrightnessPercent":null}},"OwnedRefreshRate":true}}"#
    ));
    let rig = Rig::with(file);
    // A restored baseline makes the first observation of the source a change.
    assert_eq!(
        rig.observe(target(1, None)),
        Observation::TransitionScheduled
    );
    let mut applier = Scripted::default();
    rig.run(&mut applier).unwrap().0.unwrap();
    assert_eq!(applier.applied.len(), 1);
    assert_eq!(
        rig.last(),
        (Status::Blocked, Some(REFRESH_UNRESTORABLE.to_owned()))
    );
}

#[test]
fn scheduling_coalesces_to_the_latest_target() {
    let rig = Rig::new();
    rig.observe(target(1, None));
    rig.observe(target(
        1,
        Some(settings(Some("fixed-60"), None, None, None)),
    ));
    let first = rig.recovery().scheduled().unwrap().cancel.clone();
    rig.observe(target(
        1,
        Some(settings(Some("fixed-120"), None, None, None)),
    ));
    assert!(first.canceled(), "the replaced transition is canceled");
    let mut applier = Scripted::default();
    rig.run(&mut applier).unwrap().0.unwrap();
    assert!(rig.run(&mut applier).is_none(), "one slot, one transition");
    assert_eq!(applier.applied.len(), 1);
    assert_eq!(
        applier.applied[0].0.refresh_rate_key.as_deref(),
        Some("fixed-120")
    );
}

#[test]
fn a_failed_attempt_is_retried_once_after_the_delay_and_never_pauses() {
    for (first, second) in [
        (Status::Failed, Status::Failed),
        (Status::Blocked, Status::Blocked),
        (Status::Failed, Status::Succeeded),
    ] {
        let rig = Rig::new();
        rig.observe(target(1, None));
        rig.observe(target(0, None));
        let mut applier = Scripted {
            results: [
                (first, Some(FAILED), Fields::default()),
                (
                    second,
                    (second != Status::Succeeded).then_some(FAILED),
                    Fields::default(),
                ),
            ]
            .into(),
            ..Scripted::default()
        };
        let (result, delays) = rig.run(&mut applier).unwrap();
        result.unwrap();
        assert_eq!(applier.applied.len(), 2, "{first:?} {second:?}");
        assert_eq!(delays, [RETRY_DELAY]);
        assert_eq!(rig.last().0, second);
        assert_eq!(rig.flags(), IDLE);
        assert_eq!(rig.file.record()["AutomaticWritesPaused"], false);
    }
}

#[test]
fn an_ambiguous_result_stops_further_writes_until_an_explicit_read() {
    let rig = Rig::new();
    rig.observe(target(1, None));
    let mut plugged = target(0, None);
    plugged.base = settings(Some("fixed-60"), Some(true), None, None);
    rig.observe(plugged.clone());
    let mut applier = Scripted {
        results: [(Status::Indeterminate, Some(UNCERTAIN), Fields::REFRESH)].into(),
        ..Scripted::default()
    };
    let (result, delays) = rig.run(&mut applier).unwrap();
    result.unwrap();
    assert_eq!(applier.applied.len(), 1, "never replayed");
    assert!(delays.is_empty());
    assert_eq!(rig.flags().0, (true, true, plugged.base.fields()));
    assert_eq!(rig.recovery().unresolved(), Fields::REFRESH);
    let record = rig.file.record();
    assert_eq!(record["ManualHold"], true);
    assert_eq!(record["AutomaticWritesPaused"], true);

    // Later triggers record their source but schedule nothing.
    assert_eq!(rig.observe(target(1, None)), Observation::SameSource);
    assert_eq!(
        rig.observe(target(
            1,
            Some(settings(Some("fixed-60"), None, None, None))
        )),
        Observation::SameSource
    );
    assert!(rig.recovery().scheduled().is_none());

    // A read that misses a field keeps the pause; a complete one resumes the
    // latest target.
    assert!(!rig.recovery().explicit_read(&Snapshot::unavailable("gone")));
    assert!(rig.recovery().explicit_read(&Panel::default().snapshot()));
    assert_eq!(rig.recovery().unresolved(), Fields::default());
    let recovery = rig.recovery();
    let scheduled = recovery.scheduled().unwrap();
    assert_eq!(scheduled.target.application.as_deref(), Some(GAME));
}

#[test]
fn a_transition_touching_an_unresolved_field_is_refused() {
    let rig = Rig::new();
    rig.observe(target(1, None));
    // A manual profile whose final reply did not show its confirmed refresh
    // step fails, so managed resumes; the native gate keeps refresh closed.
    assert!(rig.recovery().begin(Fields::REFRESH, Some(1)));
    rig.recovery().complete_with(
        Status::Failed,
        &Panel::default().snapshot(),
        Fields::REFRESH,
    );
    assert!(!rig.paused());
    let mut plugged = target(0, None);
    plugged.base = settings(Some("fixed-60"), None, None, None);
    assert_eq!(rig.observe(plugged), Observation::TransitionScheduled);
    let mut applier = Scripted::default();
    rig.run(&mut applier).unwrap().0.unwrap();
    assert!(applier.applied.is_empty());
    assert_eq!(rig.last(), (Status::Blocked, Some(PAUSED.to_owned())));
    assert!(!rig.in_flight());
}

#[test]
fn a_checkpoint_failure_at_admission_writes_nothing_and_pauses() {
    let rig = Rig::new();
    rig.observe(target(1, None));
    rig.observe(target(0, None));
    rig.file
        .allow
        .store(rig.file.writes.load(Ordering::SeqCst), Ordering::SeqCst);
    let mut applier = Scripted::default();
    rig.run(&mut applier).unwrap().0.unwrap();
    assert!(applier.applied.is_empty());
    assert_eq!(rig.last(), (Status::Blocked, Some(DURABILITY.to_owned())));
    let ((hold, paused, _), (in_flight, ..)) = rig.flags();
    assert!(hold && paused);
    assert!(!in_flight, "no write is in flight");
}

#[test]
fn a_fatal_failure_records_an_ambiguous_pause_and_ends_the_feature() {
    let rig = Rig::new();
    rig.observe(target(1, None));
    rig.observe(target(
        1,
        Some(settings(Some("fixed-60"), None, None, None)),
    ));
    let mut applier = Scripted {
        query_fails: true,
        ..Scripted::default()
    };
    assert!(rig.run(&mut applier).unwrap().0.is_err());
    assert_eq!(rig.last().0, Status::Indeterminate);
    assert!(rig.paused());
    assert_eq!(rig.file.record()["AutomaticWritesPaused"], true);
}

/// Managed `OperationCanceledException` from the retry delay: an attempt the
/// change refused is not retried, and automation pauses with the
/// cancellation hold until a read; that read resumes the latest target.
#[test]
fn a_power_change_during_apply_cancels_and_holds_until_a_read() {
    let rig = Rig::new();
    rig.observe(target(1, None));
    rig.observe(target(0, None));
    let recovery = rig.automation.recovery();
    let power = rig.power.clone();
    let mut applier = Scripted {
        // The fence then refuses the stale submission: nothing was written.
        results: [(
            Status::Blocked,
            Some(refresh_write::STALE),
            Fields::default(),
        )]
        .into(),
        during: Some(Box::new(move |canceled| {
            // The in-flight write is durable before any device interaction.
            assert!(recovery.lock().unwrap().automatic_flags().0, "in flight");
            power.observed(2, false);
            let observed = recovery.lock().unwrap().observe(target(2, None));
            assert_eq!(observed, Observation::SameSource);
            assert!(canceled.load(Ordering::Acquire));
        })),
        ..Scripted::default()
    };
    let (result, delays) = rig.run(&mut applier).unwrap();
    result.unwrap();
    assert_eq!(delays, [RETRY_DELAY], "the canceled delay ends the retry");
    assert_eq!(applier.applied.len(), 1);
    let ((hold, paused, _), (in_flight, cancellation_hold, _, pending)) = rig.flags();
    assert!(hold && paused && cancellation_hold);
    assert!(!in_flight);
    assert_eq!(pending, None);
    assert!(rig.recovery().scheduled().is_none());
    assert!(rig.recovery().explicit_read(&Panel::default().snapshot()));
    let recovery = rig.recovery();
    let scheduled = recovery.scheduled().unwrap();
    assert_eq!(scheduled.target.source, 2);
    assert_eq!(Some(scheduled.admitted), rig.power.capture());
}

/// A canceled write that still completed lifts the cancellation hold and
/// schedules the latest target on its own admission.
#[test]
fn a_write_completing_after_a_change_resumes_the_latest_target() {
    let rig = Rig::new();
    rig.observe(target(1, None));
    rig.observe(target(0, None));
    let recovery = rig.automation.recovery();
    let power = rig.power.clone();
    let mut applier = Scripted {
        during: Some(Box::new(move |_| {
            power.observed(2, false);
            recovery.lock().unwrap().observe(target(2, None));
        })),
        ..Scripted::default()
    };
    rig.run(&mut applier).unwrap().0.unwrap();
    assert_eq!(rig.flags().0, (false, false, Fields::default()));
    let recovery = rig.recovery();
    let scheduled = recovery.scheduled().unwrap();
    assert_eq!(scheduled.target.source, 2);
    assert_eq!(Some(scheduled.admitted), rig.power.capture());
}

#[test]
fn an_ambiguous_write_canceled_by_a_change_stays_paused() {
    let rig = Rig::new();
    rig.observe(target(1, None));
    rig.observe(target(0, None));
    let recovery = rig.automation.recovery();
    let power = rig.power.clone();
    let mut applier = Scripted {
        results: [(Status::Indeterminate, Some(UNCERTAIN), Fields::BRIGHTNESS)].into(),
        during: Some(Box::new(move |_| {
            power.observed(2, false);
            recovery.lock().unwrap().observe(target(2, None));
        })),
        ..Scripted::default()
    };
    rig.run(&mut applier).unwrap().0.unwrap();
    assert!(rig.recovery().scheduled().is_none());
    assert!(rig.paused());
    assert!(rig.flags().1.1, "the hold waits for a read");
    assert_eq!(rig.recovery().unresolved(), Fields::BRIGHTNESS);
}

#[test]
fn an_application_change_during_apply_holds_then_resumes_it() {
    let rig = Rig::new();
    rig.observe(target(1, None));
    rig.observe(target(
        1,
        Some(settings(Some("fixed-60"), None, None, None)),
    ));
    let recovery = rig.automation.recovery();
    let mut applier = Scripted {
        during: Some(Box::new(move |_| {
            let observed = recovery.lock().unwrap().observe(target(1, None));
            assert_eq!(observed, Observation::SameSource);
            let (hold, paused, _) = recovery.lock().unwrap().flags();
            assert!(hold && paused, "held while in flight");
        })),
        ..Scripted::default()
    };
    rig.run(&mut applier).unwrap().0.unwrap();
    let recovery = rig.recovery();
    let scheduled = recovery.scheduled().unwrap();
    assert_eq!(scheduled.target.application, None);
}

#[test]
fn an_unconfirmed_source_cancels_and_a_recovered_one_reschedules_the_pending_target() {
    let rig = Rig::new();
    rig.observe(target(1, None));
    rig.observe(target(0, None));
    let canceled = rig.recovery().scheduled().unwrap().cancel.clone();
    assert_eq!(rig.unavailable(false), Observation::IgnoredUnconfirmed);
    assert!(canceled.canceled());
    assert!(rig.recovery().scheduled().is_none());
    // Repeated evidence is no new context; manual-external evidence is.
    assert_eq!(rig.unavailable(false), Observation::IgnoredUnconfirmed);
    assert_eq!(rig.unavailable(true), Observation::IgnoredUnconfirmed);
    assert_eq!(
        rig.observe(target(0, None)),
        Observation::TransitionScheduled
    );
}

#[test]
fn an_unconfirmed_source_holds_an_in_flight_write() {
    let rig = Rig::new();
    rig.observe(target(1, None));
    rig.observe(target(0, None));
    let recovery = rig.automation.recovery();
    let power = rig.power.clone();
    let mut applier = Scripted {
        during: Some(Box::new(move |_| {
            power.observed(3, false);
            recovery.lock().unwrap().mark_unavailable(false);
        })),
        ..Scripted::default()
    };
    rig.run(&mut applier).unwrap().0.unwrap();
    // The write completed: the hold lifts, but nothing is scheduled while
    // the latest target's source is unconfirmed.
    assert!(!rig.paused());
    assert_eq!(rig.flags().1.3, Some(0));
    assert!(rig.recovery().scheduled().is_none());
}

#[test]
fn a_resume_without_confirmed_power_waits_for_the_source_to_return() {
    let rig = Rig::new();
    rig.observe(target(1, None));
    rig.observe(target(0, None));
    let mut applier = Scripted {
        results: [(Status::Indeterminate, Some(UNCERTAIN), Fields::default())].into(),
        ..Scripted::default()
    };
    rig.run(&mut applier).unwrap().0.unwrap();
    rig.unavailable(false);
    assert!(rig.recovery().explicit_read(&Panel::default().snapshot()));
    assert!(
        rig.recovery().scheduled().is_none(),
        "no admission to write on"
    );
    assert_eq!(rig.flags().1.3, Some(0));
    assert_eq!(
        rig.observe(target(0, None)),
        Observation::TransitionScheduled
    );
}

#[test]
fn a_manual_operation_cancels_the_transition_and_keeps_it_from_unpausing() {
    let rig = Rig::new();
    rig.observe(target(1, None));
    rig.observe(target(0, None));
    let recovery = rig.automation.recovery();
    let mut applier = Scripted {
        during: Some(Box::new(move |canceled| {
            let mut recovery = recovery.lock().unwrap();
            assert!(recovery.begin(Fields::HDR, Some(0)));
            assert!(canceled.load(Ordering::Acquire));
        })),
        ..Scripted::default()
    };
    rig.run(&mut applier).unwrap().0.unwrap();
    assert!(rig.paused(), "the manual operation still owns the pause");
    assert!(rig.flags().1.2, "manual operation pending");
    assert_eq!(rig.file.record()["AutomaticWritesPaused"], true);
    // A failed manual write restores the pause it inherited from the write
    // that was in flight when it began.
    rig.recovery()
        .complete(Status::Failed, &Snapshot::unavailable("refused"));
    assert!(rig.paused());
    assert!(!rig.flags().1.2);
}

#[test]
fn a_manual_write_preempts_the_running_transition_before_the_gate() {
    let rig = Rig::new();
    rig.observe(target(1, None));
    rig.observe(target(0, None));
    let cancel = rig.recovery().scheduled().unwrap().cancel.clone();
    let gate = Gate::default();
    // The running transition holds the gate until it sees its cancellation,
    // so a mutation that waited for the gate first would never get it.
    let (held, ready) = std::sync::mpsc::channel();
    let holder = {
        let gate = gate.clone();
        let cancel = cancel.clone();
        std::thread::spawn(move || {
            let _turn = gate.lock().unwrap();
            held.send(()).unwrap();
            !cancel.sleep(Duration::from_secs(5))
        })
    };
    ready.recv().unwrap();
    let turn = manual_turn(&rig.automation.recovery, &gate, true).unwrap();
    assert!(
        holder.join().unwrap(),
        "canceled while the transition held the gate"
    );
    assert!(rig.recovery().take_transition().is_none());
    drop(turn);

    // A query takes the same gate without canceling anything.
    assert_eq!(
        rig.observe(target(2, None)),
        Observation::TransitionScheduled
    );
    let cancel = rig.recovery().scheduled().unwrap().cancel.clone();
    drop(manual_turn(&rig.automation.recovery, &gate, false).unwrap());
    assert!(!cancel.canceled());
    assert!(rig.recovery().scheduled().is_some());
}

#[test]
fn a_superseded_transition_is_refused_at_admission() {
    let rig = Rig::new();
    rig.observe(target(1, None));
    let mut plugged = target(0, None);
    plugged.base = settings(Some("fixed-60"), None, None, None);
    rig.observe(plugged);
    let superseded = rig.recovery().take_transition().unwrap();
    let mut usbc = target(2, None);
    usbc.base = settings(Some("fixed-120"), None, None, None);
    assert_eq!(rig.observe(usbc), Observation::TransitionScheduled);
    let mut applier = Scripted::default();
    run_transition(
        &rig.automation.recovery,
        &mut applier,
        &superseded,
        |_, _| true,
    )
    .unwrap();
    assert!(applier.applied.is_empty());
    assert!(rig.recovery().last_result().is_none());
    assert!(!rig.in_flight());
}

#[test]
fn a_pending_target_waits_while_paused_or_held() {
    let rig = Rig::new();
    rig.observe(target(1, None));
    rig.observe(target(0, None));
    let mut applier = Scripted {
        results: [(Status::Indeterminate, Some(UNCERTAIN), Fields::default())].into(),
        ..Scripted::default()
    };
    rig.run(&mut applier).unwrap().0.unwrap();
    rig.unavailable(false);
    assert!(rig.recovery().explicit_read(&Panel::default().snapshot()));
    assert_eq!(rig.flags().1.3, Some(0), "the resumption waits for power");
    // A manual operation pauses and holds before the source returns.
    assert!(rig.recovery().begin(Fields::HDR, None));
    assert_eq!(rig.observe(target(0, None)), Observation::SameSource);
    assert!(rig.recovery().scheduled().is_none());
    // Its confirmation unpauses, and the hold alone still keeps it waiting.
    rig.recovery()
        .complete(Status::Succeeded, &Panel::default().snapshot());
    assert!(!rig.paused());
    rig.unavailable(false);
    assert_eq!(rig.observe(target(0, None)), Observation::SameSource);
    assert!(rig.recovery().scheduled().is_none());
}

#[test]
fn a_resumption_never_takes_another_source_admission() {
    // The watcher admits a status's power before automation observes it, so
    // an explicit read between the two must not schedule the last observed
    // source on the next source's admission.
    let rig = Rig::new();
    rig.observe(target(1, None));
    rig.observe(target(0, None));
    let mut applier = Scripted {
        results: [(Status::Indeterminate, Some(UNCERTAIN), Fields::default())].into(),
        ..Scripted::default()
    };
    rig.run(&mut applier).unwrap().0.unwrap();
    rig.power.observed(2, false);
    assert!(rig.recovery().explicit_read(&Panel::default().snapshot()));
    assert!(rig.recovery().scheduled().is_none());
    assert_eq!(rig.flags().1.3, Some(0));
    // The status power admitted then arrives and schedules its own source.
    assert_eq!(
        rig.observe(target(2, None)),
        Observation::TransitionScheduled
    );
    assert_eq!(rig.recovery().scheduled().unwrap().admitted.source, 2);
}

#[test]
fn a_switched_console_mid_apply_is_refused_and_retried_without_a_trigger() {
    let rig = Rig::new();
    rig.observe(target(1, None));
    rig.observe(target(0, None));
    let switched = Arc::new(AtomicBool::new(true));
    let mut applier = HostApplier(PanelHost::new(rig.power.clone(), switched.clone()));
    let transition = rig.recovery().take_transition().unwrap();
    let delays = AtomicUsize::new(0);
    // The console is switched to another session for the first attempt and back
    // in the delay. A locked but active console is not refused, as in managed.
    run_transition(
        &rig.automation.recovery,
        &mut applier,
        &transition,
        |_, _| {
            delays.fetch_add(1, Ordering::SeqCst);
            switched.store(false, Ordering::SeqCst);
            true
        },
    )
    .unwrap();
    assert_eq!(delays.load(Ordering::SeqCst), 1);
    assert_eq!(applier.0.writes, ["brightness 40"]);
    assert_eq!(rig.last(), (Status::Succeeded, None));
    // A session switch is no trigger of its own.
    assert!(rig.recovery().scheduled().is_none());
}

/// A manual mutation that preempts a running transition, in the production
/// order: `preempt` before the gate, the transition's result, then `begin` and
/// `complete` under the gate. Managed begins before the transition records,
/// so the transition neither unpauses nor resumes: no power generation moves
/// after the mutation captured its power, nothing resumed waits on the gate
/// to re-latch a pause, and the manual result alone ends the pause.
#[test]
fn a_preempted_transition_records_as_under_a_begun_manual_operation() {
    let rig = Rig::new();
    rig.observe(target(1, None));
    rig.observe(target(0, None));
    let recovery = rig.automation.recovery();
    let power = rig.power.clone();
    let captured = Arc::new(Mutex::new(None));
    let slot = captured.clone();
    let mut applier = Scripted {
        during: Some(Box::new(move |_| {
            // The source is lost and comes back while the write is in
            // flight, so its completion would resume the latest target.
            power.observed(3, false);
            recovery.lock().unwrap().mark_unavailable(false);
            power.observed(0, false);
            recovery.lock().unwrap().observe(target(0, None));
            // The mutation preempts before the gate and captures power
            // under it, before the transition records.
            recovery.lock().unwrap().preempt();
            *slot.lock().unwrap() = power.capture();
        })),
        ..Scripted::default()
    };
    rig.run(&mut applier).unwrap().0.unwrap();
    let manual = captured.lock().unwrap().expect("confirmed power");
    assert!(rig.power.current(manual), "the mutation's power is current");
    assert!(rig.recovery().scheduled().is_none(), "nothing resumed");
    let ((hold, paused, _), (in_flight, cancellation_hold, manual_pending, _)) = rig.flags();
    assert!(hold && paused && manual_pending);
    assert!(!in_flight && !cancellation_hold);

    assert!(rig.recovery().begin(Fields::HDR, Some(0)));
    rig.recovery()
        .complete(Status::Succeeded, &Panel::default().snapshot());
    assert!(!rig.paused());
    // No late transition re-latched a pause, so a read re-applies nothing
    // over the manual change.
    assert!(!rig.recovery().explicit_read(&Panel::default().snapshot()));
    assert!(rig.recovery().scheduled().is_none());
    assert_eq!(applier.applied.len(), 1);
}

#[test]
fn a_preempted_retry_leaves_a_failed_manual_operation_its_inherited_pause() {
    let rig = Rig::new();
    rig.observe(target(1, None));
    rig.observe(target(0, None));
    let recovery = rig.automation.recovery();
    let mut attempt = 0;
    let stale = (
        Status::Blocked,
        Some(refresh_write::STALE),
        Fields::default(),
    );
    let mut applier = Scripted {
        results: [stale, stale].into(),
        // The mutation preempts the second attempt.
        during: Some(Box::new(move |_| {
            attempt += 1;
            if attempt == 2 {
                recovery.lock().unwrap().preempt();
            }
        })),
        ..Scripted::default()
    };
    let (result, delays) = rig.run(&mut applier).unwrap();
    result.unwrap();
    assert_eq!(delays.len(), 1);
    assert!(
        rig.paused(),
        "a canceled attempt under a manual operation pauses"
    );
    assert!(rig.recovery().begin(Fields::HDR, Some(0)));
    rig.recovery()
        .complete(Status::Failed, &Snapshot::unavailable("refused"));
    assert!(
        rig.paused(),
        "managed restores the pause the write in flight left"
    );
    assert_eq!(rig.file.record()["AutomaticWritesPaused"], true);
}

#[test]
fn a_manual_turn_holds_scheduling_until_it_ends() {
    let rig = Rig::new();
    rig.observe(target(1, None));
    let gate = Gate::default();
    let turn = manual_turn(&rig.automation.recovery, &gate, true).unwrap();
    assert!(rig.flags().1.2, "pending from the preempt");
    // A status before the mutation's begin is observed as managed observes
    // one after `BeginManualOperation`: paused, so nothing is scheduled.
    assert_eq!(rig.observe(target(0, None)), Observation::SameSource);
    assert!(rig.recovery().scheduled().is_none());
    // A mutation refused before its begin leaves nothing pending.
    drop(turn);
    assert!(!rig.flags().1.2);
    assert_eq!(
        rig.observe(target(2, None)),
        Observation::TransitionScheduled
    );
}

/// Managed has no unresolved-field gate and applies the target; native keeps
/// it pending and applies it once an explicit read resolves the field.
#[test]
fn a_target_refused_for_an_unresolved_field_applies_after_a_resolving_read() {
    let rig = Rig::new();
    rig.observe(target(1, None));
    assert!(rig.recovery().begin(Fields::REFRESH, Some(1)));
    rig.recovery()
        .complete(Status::Indeterminate, &Snapshot::unavailable("unknown"));
    // A later manual write with a full readback unpauses; refresh stays
    // unresolved for the native gate.
    assert!(rig.recovery().begin(Fields::HDR, Some(1)));
    rig.recovery()
        .complete(Status::Succeeded, &Panel::default().snapshot());
    assert!(!rig.paused());
    let mut plugged = target(0, None);
    plugged.base = settings(Some("fixed-60"), None, None, None);
    assert_eq!(rig.observe(plugged), Observation::TransitionScheduled);
    let mut applier = Scripted::default();
    rig.run(&mut applier).unwrap().0.unwrap();
    assert!(applier.applied.is_empty());
    assert_eq!(rig.last(), (Status::Blocked, Some(PAUSED.to_owned())));
    assert_eq!(rig.flags().1.3, Some(0), "the target stays pending");
    // An unavailable read resolves nothing.
    assert!(
        !rig.recovery()
            .explicit_read(&Snapshot::unavailable("unread"))
    );
    assert!(rig.recovery().scheduled().is_none());
    assert!(!rig.recovery().explicit_read(&Panel::default().snapshot()));
    rig.run(&mut applier).unwrap().0.unwrap();
    assert_eq!(
        applier.applied[0].0,
        settings(Some("fixed-60"), None, None, None)
    );
    assert_eq!(rig.last(), (Status::Succeeded, None));
    // Applied, so a further read schedules nothing.
    assert!(!rig.recovery().explicit_read(&Panel::default().snapshot()));
    assert!(rig.recovery().scheduled().is_none());
}

#[test]
fn a_resolving_read_reschedules_only_a_target_the_gate_still_refuses() {
    let rig = Rig::new();
    rig.observe(target(1, None));
    assert!(rig.recovery().begin(Fields::REFRESH, Some(1)));
    rig.recovery().complete_with(
        Status::Failed,
        &Panel::default().snapshot(),
        Fields::REFRESH,
    );
    let mut plugged = target(0, None);
    plugged.base = settings(Some("fixed-60"), None, None, None);
    rig.observe(plugged);
    let mut applier = Scripted::default();
    rig.run(&mut applier).unwrap().0.unwrap();
    assert_eq!(rig.last(), (Status::Blocked, Some(PAUSED.to_owned())));
    // A later target that the gate admits supersedes the refused one.
    assert_eq!(
        rig.observe(target(2, None)),
        Observation::TransitionScheduled
    );
    rig.run(&mut applier).unwrap().0.unwrap();
    assert_eq!(rig.last(), (Status::Succeeded, None));
    // The read that resolves refresh then has nothing to reschedule.
    assert!(!rig.recovery().explicit_read(&Panel::default().snapshot()));
    assert!(rig.recovery().scheduled().is_none());

    // A manual operation after the refusal holds it, as any target is held.
    let rig = Rig::new();
    rig.observe(target(1, None));
    assert!(rig.recovery().begin(Fields::REFRESH, Some(1)));
    rig.recovery().complete_with(
        Status::Failed,
        &Panel::default().snapshot(),
        Fields::REFRESH,
    );
    let mut plugged = target(0, None);
    plugged.base = settings(Some("fixed-60"), None, None, None);
    rig.observe(plugged);
    rig.run(&mut applier).unwrap().0.unwrap();
    assert!(rig.recovery().begin(Fields::HDR, Some(0)));
    rig.recovery()
        .complete(Status::Succeeded, &Panel::default().snapshot());
    assert!(!rig.paused());
    assert!(!rig.recovery().explicit_read(&Panel::default().snapshot()));
    assert!(rig.recovery().scheduled().is_none());
}

#[test]
fn a_fatal_reply_failure_is_recorded_once_and_ends_the_feature() {
    let rig = Rig::new();
    rig.observe(target(1, None));
    rig.observe(target(0, None));
    let mut applier = Scripted {
        results: [(Status::Failed, Some(FAILED), Fields::default())].into(),
        fatal: true,
        ..Scripted::default()
    };
    let (result, delays) = rig.run(&mut applier).unwrap();
    assert!(result.is_err());
    assert!(delays.is_empty(), "a fatal failure is never retried");
    assert_eq!(applier.applied.len(), 1);
    assert_eq!(rig.last(), (Status::Failed, Some(FAILED.to_owned())));
    assert!(!rig.in_flight());
    assert!(!rig.paused());
}

#[test]
fn a_checkpoint_failure_at_completion_keeps_the_write_uncertain_and_pauses() {
    let rig = Rig::new();
    rig.observe(target(1, None));
    rig.observe(target(0, None));
    // The write-ahead checkpoint is saved; the completion's is not.
    rig.file
        .allow
        .store(rig.file.writes.load(Ordering::SeqCst) + 1, Ordering::SeqCst);
    let mut applier = Scripted::default();
    rig.run(&mut applier).unwrap().0.unwrap();
    assert_eq!(applier.applied.len(), 1);
    assert_eq!(rig.last(), (Status::Blocked, Some(DURABILITY.to_owned())));
    let ((hold, paused, uncertain), (in_flight, ..)) = rig.flags();
    assert!(hold && paused && !in_flight);
    assert_eq!(uncertain, Fields::BRIGHTNESS);
    assert_eq!(rig.file.record()["AutomaticWritesPaused"], true);
    assert_eq!(rig.file.record()["UncertainBrightness"], true);
}

#[test]
fn a_checkpoint_failure_while_observing_latches_and_schedules_nothing() {
    // The first confirmed status.
    let rig = Rig::new();
    rig.file.allow.store(0, Ordering::SeqCst);
    assert_eq!(
        rig.observe(target(
            1,
            Some(settings(Some("fixed-60"), None, None, None))
        )),
        Observation::BaselineEstablished
    );
    assert!(rig.recovery().scheduled().is_none());
    let ((hold, paused, _), _) = rig.flags();
    assert!(hold && paused);

    // A source change.
    let rig = Rig::new();
    rig.observe(target(1, None));
    rig.file
        .allow
        .store(rig.file.writes.load(Ordering::SeqCst), Ordering::SeqCst);
    assert_eq!(rig.observe(target(0, None)), Observation::SameSource);
    assert!(rig.recovery().scheduled().is_none());
    let ((hold, paused, _), _) = rig.flags();
    assert!(hold && paused);
}

#[test]
fn a_transition_whose_power_is_no_longer_confirmed_is_refused_after_one_read() {
    let rig = Rig::new();
    rig.observe(target(1, None));
    rig.observe(target(0, None));
    // Power admission withdraws before automation hears of it.
    rig.power.observed(3, false);
    let mut applier = HostApplier(PanelHost::new(rig.power.clone(), Arc::default()));
    let (result, delays) = rig.run(&mut applier).unwrap();
    result.unwrap();
    assert_eq!(delays.len(), 1, "Blocked is retried once");
    assert!(applier.0.writes.is_empty());
    assert_eq!(
        rig.last(),
        (Status::Blocked, Some(UNCONFIRMED_POWER.to_owned()))
    );
    assert!(!rig.paused());
}

#[test]
fn an_already_matching_display_submits_nothing() {
    let rig = Rig::new();
    rig.observe(target(1, None));
    let mut plugged = target(0, None);
    plugged.base = settings(Some("fixed-240"), Some(false), Some("SRGB"), Some(50));
    rig.observe(plugged);
    let mut applier = HostApplier(PanelHost::new(rig.power.clone(), Arc::default()));
    rig.run(&mut applier).unwrap().0.unwrap();
    assert!(applier.0.writes.is_empty());
    assert_eq!(rig.last(), (Status::Succeeded, None));
    assert_eq!(rig.flags(), IDLE);
}

#[test]
fn a_differing_display_is_written_once_in_managed_order() {
    let rig = Rig::new();
    rig.observe(target(1, None));
    let mut plugged = target(0, None);
    plugged.base = settings(Some("fixed-60"), Some(false), Some("dci-p3"), Some(80));
    rig.observe(plugged);
    let mut applier = HostApplier(PanelHost::new(rig.power.clone(), Arc::default()));
    rig.run(&mut applier).unwrap().0.unwrap();
    assert_eq!(
        applier.0.writes,
        ["refresh fixed-60", "color dci-p3", "brightness 80"]
    );
    assert_eq!(rig.last(), (Status::Succeeded, None));
}

#[test]
fn a_topology_change_mid_apply_stops_before_any_later_field() {
    // Refused before submission: Blocked, retried once, nothing paused.
    let rig = Rig::new();
    rig.observe(target(1, None));
    let mut plugged = target(0, None);
    plugged.base = settings(Some("fixed-60"), Some(true), None, Some(80));
    rig.observe(plugged.clone());
    let mut host = PanelHost::new(rig.power.clone(), Arc::default());
    let changed = || {
        Some(Attempt::Done(
            Status::Blocked,
            Cow::Borrowed(refresh_write::PANEL_CHANGED),
        ))
    };
    host.script = [changed(), changed()].into();
    let mut applier = HostApplier(host);
    let (result, delays) = rig.run(&mut applier).unwrap();
    result.unwrap();
    assert_eq!(delays.len(), 1);
    assert!(applier.0.writes.is_empty(), "no later field was written");
    assert_eq!(
        rig.last(),
        (
            Status::Blocked,
            Some(refresh_write::PANEL_CHANGED.to_owned())
        )
    );
    assert!(!rig.paused());

    // Changed after a possible submission: Indeterminate, paused, no retry.
    let rig = Rig::new();
    rig.observe(target(1, None));
    rig.observe(plugged);
    let mut host = PanelHost::new(rig.power.clone(), Arc::default());
    host.script = [Some(Attempt::Done(
        Status::Indeterminate,
        Cow::Borrowed("The internal display changed after the refresh rate was submitted."),
    ))]
    .into();
    let mut applier = HostApplier(host);
    let (result, delays) = rig.run(&mut applier).unwrap();
    result.unwrap();
    assert!(delays.is_empty());
    assert!(applier.0.writes.is_empty());
    assert_eq!(rig.last().0, Status::Indeterminate);
    assert!(rig.paused());
    assert_eq!(rig.recovery().unresolved(), Fields::REFRESH);
}

#[test]
fn a_transition_on_a_superseded_admission_is_refused_before_any_write() {
    let rig = Rig::new();
    rig.observe(target(1, None));
    let mut plugged = target(0, None);
    plugged.base = settings(Some("fixed-60"), None, None, None);
    rig.observe(plugged);
    let transition = rig.recovery().take_transition().unwrap();
    // A new subscription with the same source is a new generation.
    rig.power.opened(0, false);
    let mut applier = HostApplier(PanelHost::new(rig.power.clone(), Arc::default()));
    run_transition(
        &rig.automation.recovery,
        &mut applier,
        &transition,
        |_, _| true,
    )
    .unwrap();
    assert!(applier.0.writes.is_empty());
    assert_eq!(
        rig.last(),
        (Status::Blocked, Some(CHANGED_POWER.to_owned()))
    );
}

#[test]
fn stop_during_the_retry_delay_pauses_like_managed_cancellation() {
    let rig = Rig::new();
    rig.observe(target(1, None));
    rig.observe(target(0, None));
    let transition = rig.recovery().take_transition().unwrap();
    let mut applier = Scripted {
        results: [(Status::Failed, Some(FAILED), Fields::default())].into(),
        ..Scripted::default()
    };
    run_transition(
        &rig.automation.recovery,
        &mut applier,
        &transition,
        |cancel, delay| {
            cancel.cancel();
            cancel.sleep(delay)
        },
    )
    .unwrap();
    assert_eq!(applier.applied.len(), 1);
    let ((hold, paused, _), (in_flight, ..)) = rig.flags();
    assert!(hold && paused);
    assert!(!in_flight);
    assert_eq!(rig.file.record()["AutomaticWritesPaused"], true);
}

#[test]
fn the_cancellable_delay_wakes_on_cancel() {
    let cancel = Arc::new(Cancel::default());
    assert!(cancel.sleep(Duration::ZERO));
    let waker = cancel.clone();
    let thread = std::thread::spawn(move || {
        std::thread::sleep(Duration::from_millis(20));
        waker.cancel();
    });
    let started = std::time::Instant::now();
    assert!(!cancel.sleep(Duration::from_secs(30)));
    assert!(started.elapsed() < Duration::from_secs(10));
    thread.join().unwrap();
    assert!(
        !cancel.sleep(Duration::ZERO),
        "a canceled delay never waits"
    );
}

/// A runner applier: each apply reports its source, then, when `block`, waits
/// until it is canceled (bounded), as a write fence would refuse.
struct Blocking {
    started: tokio::sync::mpsc::UnboundedSender<u8>,
    applied: Arc<AtomicUsize>,
    block: bool,
}

impl Applier for Blocking {
    fn query(&mut self, _: &AtomicBool) -> io::Result<Snapshot> {
        Ok(Panel::default().snapshot())
    }

    fn apply(
        &mut self,
        request: &ApplyProfile,
        _: PowerContext,
        canceled: &AtomicBool,
    ) -> io::Result<Applied> {
        self.applied.fetch_add(1, Ordering::SeqCst);
        let _ = self.started.send(request.expected_power_source);
        let deadline = std::time::Instant::now() + Duration::from_secs(10);
        while self.block
            && !canceled.load(Ordering::Acquire)
            && std::time::Instant::now() < deadline
        {
            std::thread::sleep(Duration::from_millis(1));
        }
        let (status, error) = if canceled.load(Ordering::Acquire) {
            (
                Status::Blocked,
                Some(Cow::Borrowed(refresh_write::CANCELED)),
            )
        } else {
            (Status::Succeeded, None)
        };
        Ok(Applied {
            outcome: ManualWriteOutcome {
                status,
                error,
                state: Panel::default().snapshot(),
            },
            unresolved: Fields::default(),
            fatal: None,
        })
    }
}

/// Waits (bounded) until the transition for `source` completed.
async fn settled(rig: &Rig, source: u8) {
    for _ in 0..5000 {
        let done = rig.recovery().last_result().map(|last| last.source) == Some(source);
        if done && !rig.in_flight() {
            return;
        }
        tokio::time::sleep(Duration::from_millis(1)).await;
    }
    panic!("the transition did not settle");
}

/// The loop runs on Service status alone: the tray, its queries and the
/// Display pipe take no part, so a tray exit leaves it running.
#[tokio::test]
async fn the_runner_follows_status_alone_without_any_tray_request() {
    let rig = Rig::new();
    let (started, mut seen) = tokio::sync::mpsc::unbounded_channel();
    let applied = Arc::new(AtomicUsize::new(0));
    let applier = Blocking {
        started,
        applied: applied.clone(),
        block: false,
    };
    let (stop, mut stopped) = watch::channel(false);
    let automation = rig.automation.clone();
    let runner = tokio::spawn(async move { serve(&automation, applier, &mut stopped).await });
    rig.observe(target(1, None));
    for source in [0, 2, 1] {
        assert_eq!(
            rig.observe(target(source, None)),
            Observation::TransitionScheduled
        );
        assert_eq!(seen.recv().await, Some(source));
        settled(&rig, source).await;
    }
    stop.send(true).unwrap();
    runner.await.unwrap().unwrap();
    assert_eq!(applied.load(Ordering::SeqCst), 3);
}

#[tokio::test]
async fn agent_stop_during_apply_cancels_joins_and_pauses_like_managed() {
    let rig = Rig::new();
    let (started, mut seen) = tokio::sync::mpsc::unbounded_channel();
    let applied = Arc::new(AtomicUsize::new(0));
    let applier = Blocking {
        started,
        applied: applied.clone(),
        block: true,
    };
    let (stop, mut stopped) = watch::channel(false);
    let automation = rig.automation.clone();
    let runner = tokio::spawn(async move { serve(&automation, applier, &mut stopped).await });
    rig.observe(target(1, None));
    rig.observe(target(0, None));
    assert_eq!(seen.recv().await, Some(0));
    assert!(rig.in_flight(), "in flight while it applies");
    stop.send(true).unwrap();
    // The runner returns only after the blocking apply returned and recorded.
    runner.await.unwrap().unwrap();
    assert_eq!(applied.load(Ordering::SeqCst), 1, "never replayed");
    let ((hold, paused, _), (in_flight, ..)) = rig.flags();
    assert!(hold && paused && !in_flight);
    assert_eq!(rig.file.record()["AutomaticWritesPaused"], true);
    // A later trigger runs nowhere.
    rig.observe(target(2, None));
    assert_eq!(applied.load(Ordering::SeqCst), 1);
}

#[tokio::test]
async fn a_fatal_transition_ends_the_runner() {
    let rig = Rig::new();
    let (_stop, mut stopped) = watch::channel(false);
    rig.observe(target(1, None));
    rig.observe(target(
        1,
        Some(settings(Some("fixed-60"), None, None, None)),
    ));
    let applier = Scripted {
        query_fails: true,
        ..Scripted::default()
    };
    assert!(serve(&rig.automation, applier, &mut stopped).await.is_err());
}

/// A scripted `ProfileHost` under the real profile composition: each write
/// takes the next scripted attempt, or applies itself to the panel. While
/// `switched` away from the console session, or on a superseded admission,
/// every write is refused by its fence as the real one is (the fence's
/// active-console and power checks).
struct PanelHost {
    panel: Panel,
    power: PowerAdmission,
    switched: Arc<AtomicBool>,
    script: VecDeque<Option<Attempt>>,
    writes: Vec<String>,
}

impl PanelHost {
    fn new(power: PowerAdmission, switched: Arc<AtomicBool>) -> Self {
        Self {
            panel: Panel::default(),
            power,
            switched,
            script: VecDeque::new(),
            writes: Vec::new(),
        }
    }

    fn write(
        &mut self,
        admitted: PowerContext,
        apply: impl FnOnce(&mut Panel) -> String,
    ) -> Attempt {
        if let Some(Some(attempt)) = self.script.pop_front() {
            return attempt;
        }
        if self.switched.load(Ordering::SeqCst) || !self.power.current(admitted) {
            return Attempt::Done(Status::Blocked, Cow::Borrowed(refresh_write::STALE));
        }
        let write = apply(&mut self.panel);
        self.writes.push(write);
        Attempt::Confirmed
    }
}

impl ProfileHost for PanelHost {
    fn power(&self) -> Option<PowerContext> {
        self.power.capture()
    }

    fn query(&mut self, _: &SharedBrightness, _: &AtomicBool) -> io::Result<Snapshot> {
        Ok(self.panel.snapshot())
    }

    fn refresh(
        &mut self,
        request: &SetRefreshRate,
        admitted: PowerContext,
        _: &AtomicBool,
    ) -> Attempt {
        self.write(admitted, |panel| {
            panel.refresh = Some(request.key.clone());
            format!("refresh {}", request.key)
        })
    }

    fn hdr(&mut self, request: &SetHdr, admitted: PowerContext, _: &AtomicBool) -> Attempt {
        self.write(admitted, |panel| {
            panel.hdr = request.enabled;
            format!("hdr {}", request.enabled)
        })
    }

    fn color(
        &mut self,
        request: &SetColorProfile,
        admitted: PowerContext,
        _: &AtomicBool,
    ) -> Attempt {
        self.write(admitted, |panel| {
            panel.color = Some(request.key.clone());
            format!("color {}", request.key)
        })
    }

    fn brightness(
        &mut self,
        _: &SharedBrightness,
        percent: u8,
        admitted: PowerContext,
        _: &AtomicBool,
    ) -> super::super::brightness_write::Attempt {
        match self.write(admitted, |panel| {
            panel.brightness = Some(percent);
            format!("brightness {percent}")
        }) {
            Attempt::Confirmed => Ok((Status::Succeeded, None, Some(percent))),
            Attempt::Done(status, _) => Ok((status, Some(refresh_write::STALE), None)),
            _ => unreachable!("the panel host only confirms or refuses"),
        }
    }
}

/// A brightness owner that can never start the Windows worker.
fn inert() -> SharedBrightness {
    Arc::new(Mutex::new(
        super::super::observation::BrightnessOwner::inert(),
    ))
}

/// The real transition composition over a `PanelHost`.
struct HostApplier(PanelHost);

impl Applier for HostApplier {
    fn query(&mut self, canceled: &AtomicBool) -> io::Result<Snapshot> {
        self.0.query(&inert(), canceled)
    }

    fn apply(
        &mut self,
        request: &ApplyProfile,
        admitted: PowerContext,
        canceled: &AtomicBool,
    ) -> io::Result<Applied> {
        Ok(profile_apply::transition(
            &mut self.0,
            &inert(),
            request,
            admitted,
            canceled,
        ))
    }
}

#[path = "automation_status_tests.rs"]
mod status;
#[path = "automation_vectors.rs"]
mod vectors;

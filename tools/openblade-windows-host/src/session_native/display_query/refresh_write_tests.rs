//! Manual setRefreshRate through the shared Rust refresh writer against a
//! scripted native boundary, with injected power, fence, reply query and
//! durable storage. No Windows supplier, endpoint, CCD or SetDisplayConfig
//! call (VALIDATE included) or hardware is used.
use super::*;
use crate::session_native::display_query::{
    ccd_write::CHANGING,
    observation::fixture_query,
    test_support::{Memory, Rig},
    write::{CHANGED_POWER, CHECKPOINT, STALE_AFTER, UNCONFIRMED_POWER},
};
use openblade_platform_windows::display::SubmissionFence;
use openblade_platform_windows::display::{
    DisplayReadError, InternalDisplayRefresh, InternalDisplayTopology, Operation, RefreshRate,
    refresh_write::{RefreshObservation, RefreshPlan},
    scripted::{RefreshTiming, Scenario},
};
use std::{
    cell::Cell,
    collections::VecDeque,
    sync::{Arc, atomic::AtomicUsize},
};

/// What one APPLY does to the scripted panel, whatever status it returns.
#[derive(Clone, Copy, PartialEq, Eq)]
enum Effect {
    /// The plan's desktop rate and its target timing, or a signal at the
    /// desktop rate when the target index is omitted.
    Plan,
    /// Only the desktop rate changes; the active signal keeps its timing.
    DesktopOnly,
    Nothing,
}

fn hertz(rate: RefreshRate) -> u32 {
    (rate.numerator + rate.denominator / 2) / rate.denominator
}

/// The scripted panel: a non-boosted fixed timing over the shared fixture,
/// which offers fixed-60 and fixed-240 and reads no EDID or preferred timing.
struct Panel {
    desktop: u32,
    physical: u32,
    replaced: bool,
    /// Observations and readbacks consumed before the panel's own state.
    observe_script: VecDeque<Result<RefreshObservation, DisplayReadError>>,
    read_script: VecDeque<Result<InternalDisplayRefresh, DisplayReadError>>,
    validate_status: u32,
    apply_status: u32,
    effect: Effect,
    observations: usize,
    reads: usize,
    /// The explicit target signal rate of every validated and applied plan.
    validated: Vec<Option<u32>>,
    applied: Vec<Option<u32>>,
    /// Set after the submission boundary (cancellation racing a write).
    cancel_on_apply: Option<Arc<AtomicBool>>,
}

impl Panel {
    fn timing(&self) -> RefreshTiming {
        RefreshTiming::fixed(self.desktop, self.physical)
    }

    fn key(&self) -> Option<String> {
        match Scenario::Available.read_with_hdr(true, self.timing(), self.replaced) {
            Ok(InternalDisplayRefresh::Single(snapshot)) => snapshot.current_refresh_key,
            _ => None,
        }
    }
}

impl RefreshNative for Panel {
    fn observe(&mut self) -> Result<RefreshObservation, DisplayReadError> {
        self.observations += 1;
        self.observe_script.pop_front().unwrap_or_else(|| {
            Scenario::Available.observe_refresh(true, self.timing(), self.replaced)
        })
    }

    fn validate(&mut self, plan: &RefreshPlan) -> u32 {
        self.validated.push(plan.target_rate().map(hertz));
        self.validate_status
    }

    fn apply(&mut self, plan: &RefreshPlan) -> u32 {
        self.applied.push(plan.target_rate().map(hertz));
        assert!(!plan.boost(), "the fixture offers fixed options only");
        if let Some(cancel) = &self.cancel_on_apply {
            cancel.store(true, Ordering::SeqCst);
        }
        let desktop = hertz(plan.desktop_rate().unwrap());
        match self.effect {
            Effect::Plan => {
                self.physical = plan.target_rate().map_or(desktop, hertz);
                self.desktop = desktop;
            }
            Effect::DesktopOnly => self.desktop = desktop,
            Effect::Nothing => {}
        }
        self.apply_status
    }

    fn read(&mut self) -> Result<InternalDisplayRefresh, DisplayReadError> {
        self.reads += 1;
        self.read_script.pop_front().unwrap_or_else(|| {
            Scenario::Available.read_with_hdr(true, self.timing(), self.replaced)
        })
    }
}

struct TestFence {
    timing: RefreshTiming,
    changed: bool,
    admit: bool,
    checks: Arc<AtomicUsize>,
}

impl SubmissionFence for TestFence {
    fn topology(&self) -> Result<InternalDisplayTopology, DisplayReadError> {
        self.checks.fetch_add(1, Ordering::SeqCst);
        if self.changed {
            return Ok(InternalDisplayTopology::NoInternalDisplay);
        }
        Scenario::Available.topology_with_hdr(true, self.timing, false)
    }

    fn admit(&self) -> bool {
        self.admit
    }
}

struct Host {
    panel: Panel,
    power: Option<PowerContext>,
    /// Power reported by every capture after the first (post-success drift).
    later: Option<Option<PowerContext>>,
    captures: Cell<usize>,
    changed: bool,
    admit: bool,
    fence_checks: Arc<AtomicUsize>,
    queries: usize,
    /// The reply query shows this refresh key instead of the panel's.
    reply_key: Option<Option<&'static str>>,
    /// The reply query fails fatally.
    fail_query: bool,
}

impl Host {
    fn admitted(source: u8, desktop: u32, physical: u32) -> Self {
        Self {
            panel: Panel {
                desktop,
                physical,
                replaced: false,
                observe_script: VecDeque::new(),
                read_script: VecDeque::new(),
                validate_status: 0,
                apply_status: 0,
                effect: Effect::Plan,
                observations: 0,
                reads: 0,
                validated: Vec::new(),
                applied: Vec::new(),
                cancel_on_apply: None,
            },
            power: Some(PowerContext {
                source,
                generation: 7,
            }),
            later: None,
            captures: Cell::new(0),
            changed: false,
            admit: true,
            fence_checks: Arc::default(),
            queries: 0,
            reply_key: None,
            fail_query: false,
        }
    }

    fn submissions(&self) -> usize {
        self.panel.applied.len()
    }

    /// Validations, fence checks and submissions: any device interaction past
    /// the first observation.
    fn interactions(&self) -> (usize, usize, usize) {
        (
            self.panel.validated.len(),
            self.fence_checks.load(Ordering::SeqCst),
            self.submissions(),
        )
    }
}

impl CcdHost for Host {
    type Native = Panel;

    fn power(&self) -> Option<PowerContext> {
        let capture = self.captures.get();
        self.captures.set(capture + 1);
        match self.later {
            Some(later) if capture > 0 => later,
            _ => self.power,
        }
    }

    fn fence(&self, admitted: PowerContext) -> Arc<dyn SubmissionFence> {
        assert_eq!(
            Some(admitted),
            self.power,
            "fence binds the captured context"
        );
        Arc::new(TestFence {
            timing: self.panel.timing(),
            changed: self.changed,
            admit: self.admit,
            checks: self.fence_checks.clone(),
        })
    }

    fn native(&mut self) -> &mut Panel {
        &mut self.panel
    }

    fn query(&mut self, _: &SharedBrightness, canceled: &AtomicBool) -> io::Result<Snapshot> {
        self.queries += 1;
        if self.fail_query {
            return Err(io::Error::other("injected reply query failure"));
        }
        if canceled.load(Ordering::SeqCst) {
            return Err(io::ErrorKind::Interrupted.into());
        }
        let key = match self.reply_key {
            Some(key) => key.map(String::from),
            None => self.panel.key(),
        };
        Ok(snapshot(key.as_deref()))
    }
}

/// A reply query with the given classified refresh option.
fn snapshot(key: Option<&str>) -> Snapshot {
    let mut value = serde_json::to_value(fixture_query("available")).unwrap();
    value["currentRefreshKey"] = key.into();
    serde_json::from_value(value).unwrap()
}

/// The manual request through the shared rig.
trait Set {
    fn set(&self, host: &mut Host, expected: u8, key: &str) -> io::Result<CcdWriteOutcome>;
    fn set_canceled(
        &self,
        host: &mut Host,
        expected: u8,
        key: &str,
        canceled: &AtomicBool,
    ) -> io::Result<CcdWriteOutcome>;
}

impl Set for Rig {
    fn set(&self, host: &mut Host, expected: u8, key: &str) -> io::Result<CcdWriteOutcome> {
        self.set_canceled(host, expected, key, &AtomicBool::new(false))
    }

    fn set_canceled(
        &self,
        host: &mut Host,
        expected: u8,
        key: &str,
        canceled: &AtomicBool,
    ) -> io::Result<CcdWriteOutcome> {
        set_refresh_rate(
            host,
            &self.owner,
            &self.recovery,
            &SetRefreshRate {
                expected_power_source: expected,
                key: key.into(),
            },
            canceled,
        )
    }
}

fn result(outcome: &CcdWriteOutcome) -> (Status, Option<&str>) {
    (outcome.status, outcome.error.as_deref())
}

fn flag(record: &serde_json::Value, name: &str) -> bool {
    record[name].as_bool().unwrap()
}

#[test]
fn requested_option_is_validated_fenced_applied_once_and_recorded() {
    let rig = Rig::new(Memory::default());
    // Issue #884: a 60 Hz desktop over a 240 Hz signal classifies no option.
    let mut host = Host::admitted(1, 60, 240);
    assert_eq!(host.panel.key(), None);
    let outcome = rig.set(&mut host, 1, "fixed-240").unwrap();
    assert_eq!(result(&outcome), (Status::Succeeded, None));
    assert_eq!(outcome.state.refresh_key(), Some("fixed-240"));
    // The active 240 Hz timing is supplied explicitly to VALIDATE and APPLY.
    assert_eq!(host.panel.validated, [Some(240)]);
    assert_eq!(host.panel.applied, [Some(240)]);
    assert_eq!((host.panel.desktop, host.panel.physical), (240, 240));
    // One observation, one fence check, one independent readback, one reply.
    assert_eq!((host.panel.observations, host.panel.reads), (1, 1));
    assert_eq!(host.fence_checks.load(Ordering::SeqCst), 1);
    assert_eq!(host.queries, 1);
    // Begin and complete both reached durable storage before returning.
    assert_eq!(rig.file.writes.load(Ordering::SeqCst), 2);
    let record = rig.file.record();
    assert_eq!(record["LastReconciledPowerSource"], 1);
    assert!(flag(&record, "ManualHold"));
    assert!(!flag(&record, "AutomaticWritesPaused"));
    assert!(!flag(&record, "UncertainRefreshRate"));
    assert!(!rig.unresolved().refresh);
}

#[test]
fn current_option_submits_nothing_like_managed() {
    let rig = Rig::new(Memory::default());
    let mut host = Host::admitted(1, 240, 240);
    let outcome = rig.set(&mut host, 1, "fixed-240").unwrap();
    assert_eq!(result(&outcome), (Status::Succeeded, None));
    // Managed PrepareRefresh returns Success(before) without validation.
    assert_eq!(host.interactions(), (0, 0, 0));
    assert_eq!((host.panel.observations, host.panel.reads), (1, 0));
    let record = rig.file.record();
    assert!(!flag(&record, "AutomaticWritesPaused") && !flag(&record, "UncertainRefreshRate"));

    // An interrupted reply query cannot make an unsubmitted no-op uncertain.
    let outcome = rig
        .set_canceled(&mut host, 1, "fixed-240", &AtomicBool::new(true))
        .unwrap();
    assert_eq!(result(&outcome), (Status::Succeeded, None));
    assert_eq!(outcome.state.refresh_key(), None);
    assert_eq!(host.interactions(), (0, 0, 0));
    assert!(!rig.unresolved().refresh);
}

#[test]
fn explicit_target_timing_is_submitted_when_known_and_omitted_only_otherwise() {
    // The fixture reads no EDID or preferred mode, so a 60 Hz signal is not
    // known while a 240 Hz one is active: Windows must select it, and that
    // request still has to validate first.
    let rig = Rig::new(Memory::default());
    let mut host = Host::admitted(0, 240, 240);
    let outcome = rig.set(&mut host, 0, "fixed-60").unwrap();
    assert_eq!(result(&outcome), (Status::Succeeded, None));
    assert_eq!(host.panel.validated, [None]);
    assert_eq!(host.panel.applied, [None]);
    assert_eq!((host.panel.desktop, host.panel.physical), (60, 60));

    // From the split timing the active 240 Hz signal is known and supplied.
    let mut host = Host::admitted(0, 60, 240);
    let outcome = rig.set(&mut host, 0, "fixed-240").unwrap();
    assert_eq!(result(&outcome), (Status::Succeeded, None));
    assert_eq!(host.panel.validated, [Some(240)]);
    assert_eq!(host.panel.applied, [Some(240)]);
}

#[test]
fn unknown_or_unavailable_keys_fail_without_validation() {
    let rig = Rig::new(Memory::default());
    for key in ["fixed-75", "dynamic-60-240", "Fixed-240", "fixed-240 "] {
        let mut host = Host::admitted(1, 60, 240);
        let outcome = rig.set(&mut host, 1, key).unwrap();
        assert_eq!(
            result(&outcome),
            (Status::Failed, Some(UNAVAILABLE)),
            "{key}"
        );
        assert_eq!(host.interactions(), (0, 0, 0));
        assert_eq!(host.panel.reads, 0);
    }
    let record = rig.file.record();
    assert!(!flag(&record, "AutomaticWritesPaused") && !flag(&record, "UncertainRefreshRate"));
    assert!(!rig.unresolved().refresh);
}

#[test]
fn refused_validation_fails_without_apply() {
    let rig = Rig::new(Memory::default());
    let mut host = Host::admitted(1, 60, 240);
    host.panel.validate_status = 87;
    let outcome = rig.set(&mut host, 1, "fixed-240").unwrap();
    assert_eq!(
        result(&outcome),
        (
            Status::Failed,
            Some(
                "The refresh-rate configuration could not be prepared: Windows rejected the \
                 requested display configuration: Win32 error 87."
            )
        )
    );
    assert_eq!(host.interactions(), (1, 0, 0));
    assert_eq!(host.panel.reads, 0);
    assert!(!rig.unresolved().refresh);
    assert!(!flag(&rig.file.record(), "UncertainRefreshRate"));
}

#[test]
fn observation_failures_fail_without_validation() {
    let cases: [(Result<RefreshObservation, DisplayReadError>, &str); 4] = [
        (
            Scenario::Unavailable.observe_refresh(true, RefreshTiming::default(), false),
            NO_INTERNAL,
        ),
        (
            Scenario::NativeError.observe_refresh(true, RefreshTiming::default(), false),
            "Windows could not read the active display configuration: Win32 error 5.",
        ),
        (Err(DisplayReadError::TopologyChanging), CHANGING),
        (
            Err(DisplayReadError::Native {
                operation: Operation::BufferSizes,
                status: 31,
            }),
            "Windows could not size the active display configuration: Win32 error 31.",
        ),
    ];
    for (observation, expected) in cases {
        let rig = Rig::new(Memory::default());
        let mut host = Host::admitted(1, 60, 240);
        host.panel.observe_script.push_back(observation);
        let outcome = rig.set(&mut host, 1, "fixed-240").unwrap();
        assert_eq!(result(&outcome), (Status::Failed, Some(expected)));
        assert_eq!(host.interactions(), (0, 0, 0));
        assert!(!rig.unresolved().refresh);
    }
}

#[test]
fn denied_admission_and_fence_refusals_never_submit() {
    let rig = Rig::new(Memory::legacy(0));

    // Unconfirmed power: blocked after a durable manual hold, never observed.
    let mut host = Host::admitted(0, 60, 240);
    host.power = None;
    let outcome = rig.set(&mut host, 0, "fixed-240").unwrap();
    assert_eq!(result(&outcome), (Status::Blocked, Some(UNCONFIRMED_POWER)));
    assert_eq!(host.panel.observations, 0);
    assert_eq!(host.queries, 1, "only the reply query ran");
    let record = rig.file.record();
    assert!(!flag(&record, "AutomaticWritesPaused") && !flag(&record, "UncertainRefreshRate"));

    // Changed power source.
    let mut host = Host::admitted(1, 60, 240);
    let outcome = rig.set(&mut host, 2, "fixed-240").unwrap();
    assert_eq!(result(&outcome), (Status::Blocked, Some(CHANGED_POWER)));
    assert_eq!(host.panel.observations, 0);

    // Origin, session, power generation or Stop changed after validation.
    let mut host = Host::admitted(1, 60, 240);
    host.admit = false;
    let outcome = rig.set(&mut host, 1, "fixed-240").unwrap();
    assert_eq!(result(&outcome), (Status::Blocked, Some(STALE)));
    assert_eq!(host.interactions(), (1, 1, 0));

    // The fence's fresh topology no longer shows the observed panel.
    let mut host = Host::admitted(1, 60, 240);
    host.changed = true;
    let outcome = rig.set(&mut host, 1, "fixed-240").unwrap();
    assert_eq!(result(&outcome), (Status::Blocked, Some(PANEL_CHANGED)));
    assert_eq!(host.interactions(), (1, 1, 0));

    // The observed panel was replaced before the fence's fresh read.
    let mut host = Host::admitted(1, 60, 240);
    host.panel.replaced = true;
    let outcome = rig.set(&mut host, 1, "fixed-240").unwrap();
    assert_eq!(result(&outcome), (Status::Blocked, Some(PANEL_CHANGED)));
    assert_eq!(host.submissions(), 0);

    // Canceled before submission (client loss, deadline or Stop).
    let mut host = Host::admitted(1, 60, 240);
    let outcome = rig
        .set_canceled(&mut host, 1, "fixed-240", &AtomicBool::new(true))
        .unwrap();
    assert_eq!(result(&outcome), (Status::Blocked, Some(CANCELED)));
    assert_eq!(host.interactions(), (1, 0, 0));

    // Blocked completions restore the inherited (unpaused) state.
    let record = rig.file.record();
    assert!(!flag(&record, "AutomaticWritesPaused") && !flag(&record, "UncertainRefreshRate"));
    assert!(!rig.unresolved().refresh);
    let mut host = Host::admitted(1, 60, 240);
    assert_eq!(
        rig.set(&mut host, 1, "fixed-240").unwrap().status,
        Status::Succeeded
    );
}

#[test]
fn checkpoint_failure_before_submission_blocks_without_device_interaction() {
    let file = Memory::legacy(1);
    file.allow.store(0, Ordering::SeqCst);
    let rig = Rig::new(file);
    let mut host = Host::admitted(1, 60, 240);
    let outcome = rig.set(&mut host, 1, "fixed-240").unwrap();
    assert_eq!(result(&outcome), (Status::Blocked, Some(CHECKPOINT)));
    assert_eq!(host.panel.observations, 0);
    assert_eq!(host.interactions(), (0, 0, 0));

    // No prior checkpoint and no confirmed source: nothing can be saved.
    let rig = Rig::new(Memory::default());
    let mut host = Host::admitted(1, 60, 240);
    host.power = None;
    assert_eq!(
        rig.set(&mut host, 1, "fixed-240").unwrap().error.as_deref(),
        Some(CHECKPOINT)
    );
    assert_eq!((host.panel.observations, host.submissions()), (0, 0));
}

#[test]
fn apply_error_is_uncertain_and_pauses_without_replay_across_restart_until_explicit_read() {
    for effect in [Effect::Nothing, Effect::Plan] {
        let rig = Rig::new(Memory::default());
        let mut host = Host::admitted(2, 60, 240);
        host.panel.apply_status = 1610;
        host.panel.effect = effect;
        let outcome = rig.set(&mut host, 2, "fixed-240").unwrap();
        // Managed ExecuteRefreshMutation: even a matching readback cannot turn
        // a submission error into success.
        assert_eq!(
            result(&outcome),
            (
                Status::Indeterminate,
                Some(
                    "The refresh-rate change failed: Windows could not apply the requested \
                     display configuration: Win32 error 1610."
                )
            )
        );
        assert_eq!((host.submissions(), host.panel.reads), (1, 1));
        let record = rig.file.record();
        assert!(flag(&record, "AutomaticWritesPaused") && flag(&record, "UncertainRefreshRate"));
        assert!(!flag(&record, "UncertainHdr") && !flag(&record, "UncertainBrightness"));
        assert_eq!(rig.unresolved(), Fields::REFRESH);

        // Same process: refused before any device interaction, never replayed.
        host.panel.apply_status = 0;
        let observations = host.panel.observations;
        let blocked = rig.set(&mut host, 2, "fixed-240").unwrap();
        assert_eq!(result(&blocked), (Status::Blocked, Some(PAUSED)));
        assert_eq!(
            (host.submissions(), host.panel.observations),
            (1, observations)
        );

        // Restart: the durable checkpoint restores the gate.
        let restarted = Rig::new(rig.file.clone());
        assert!(restarted.unresolved().refresh);
        let blocked = restarted.set(&mut host, 2, "fixed-240").unwrap();
        assert_eq!(result(&blocked), (Status::Blocked, Some(PAUSED)));
        assert_eq!(host.submissions(), 1);

        // A read without a classified option keeps the gate; one with it reopens.
        restarted.explicit_read(&Snapshot::unavailable("gone"));
        restarted.explicit_read(&snapshot(None));
        assert!(restarted.unresolved().refresh);
        restarted.explicit_read(&snapshot(Some("fixed-60")));
        assert!(!restarted.unresolved().refresh);
        let record = restarted.file.record();
        assert!(!flag(&record, "AutomaticWritesPaused") && !flag(&record, "UncertainRefreshRate"));
        host.panel.effect = Effect::Plan;
        let outcome = restarted.set(&mut host, 2, "fixed-60").unwrap();
        assert_eq!(result(&outcome), (Status::Succeeded, None));
        assert_eq!(host.submissions(), 2);
    }
}

#[test]
fn desktop_match_without_the_signal_is_uncertain() {
    let rig = Rig::new(Memory::default());
    let mut host = Host::admitted(1, 240, 240);
    host.panel.effect = Effect::DesktopOnly;
    let outcome = rig.set(&mut host, 1, "fixed-60").unwrap();
    assert_eq!((host.panel.desktop, host.panel.physical), (60, 240));
    assert_eq!(result(&outcome), (Status::Indeterminate, Some(UNCONFIRMED)));
    assert_eq!(host.submissions(), 1);
    assert!(rig.unresolved().refresh);
    assert!(flag(&rig.file.record(), "UncertainRefreshRate"));
}

#[test]
fn readback_failures_after_apply_are_uncertain_with_managed_texts() {
    let cases: [(Result<InternalDisplayRefresh, DisplayReadError>, &str); 4] = [
        (
            Err(DisplayReadError::Native {
                operation: Operation::ActivePaths,
                status: 5,
            }),
            "The refresh-rate change could not be confirmed: Windows could not read the active \
             display configuration: Win32 error 5.",
        ),
        (
            Ok(InternalDisplayRefresh::NoInternalDisplay),
            "The refresh-rate change could not be confirmed: No active internal laptop display \
             was found.",
        ),
        (
            Ok(InternalDisplayRefresh::MultipleInternalDisplays),
            "The refresh-rate change could not be confirmed: More than one active internal \
             display was found, so OpenBlade did not choose one automatically.",
        ),
        // A different panel reads back the requested option.
        (
            Scenario::Available.read_with_hdr(true, RefreshTiming::fixed(240, 240), true),
            UNCONFIRMED,
        ),
    ];
    for (read, expected) in cases {
        let rig = Rig::new(Memory::default());
        let mut host = Host::admitted(1, 60, 240);
        host.panel.read_script.push_back(read);
        let outcome = rig.set(&mut host, 1, "fixed-240").unwrap();
        assert_eq!(result(&outcome), (Status::Indeterminate, Some(expected)));
        assert_eq!((host.submissions(), host.panel.reads), (1, 1));
        assert_eq!(rig.unresolved(), Fields::REFRESH);
    }
}

#[test]
fn reply_that_does_not_show_a_confirmed_change_is_indeterminate() {
    let rig = Rig::new(Memory::default());
    let mut host = Host::admitted(1, 60, 240);
    host.reply_key = Some(Some("fixed-60"));
    let outcome = rig.set(&mut host, 1, "fixed-240").unwrap();
    assert_eq!(result(&outcome), (Status::Indeterminate, Some(UNCONFIRMED)));
    assert_eq!(host.submissions(), 1);
    assert!(rig.unresolved().refresh);
}

#[test]
fn power_change_after_a_successful_change_is_indeterminate() {
    let drifted = Some(PowerContext {
        source: 0,
        generation: 8,
    });
    for (desktop, submitted) in [(60, true), (240, false)] {
        let rig = Rig::new(Memory::default());
        let mut host = Host::admitted(1, desktop, 240);
        host.later = Some(drifted);
        let outcome = rig.set(&mut host, 1, "fixed-240").unwrap();
        // Managed ExecuteSetterAsync.EnsureCurrent, applied to a submitted
        // change and to an unchanged request alike.
        assert_eq!(result(&outcome), (Status::Indeterminate, Some(STALE_AFTER)));
        assert_eq!(host.submissions(), usize::from(submitted));
        assert_eq!(host.captures.get(), 2);
        assert!(rig.unresolved().refresh);
        assert!(flag(&rig.file.record(), "AutomaticWritesPaused"));
    }

    // Lost confirmation after success is also stale.
    let rig = Rig::new(Memory::default());
    let mut host = Host::admitted(1, 60, 240);
    host.later = Some(None);
    let outcome = rig.set(&mut host, 1, "fixed-240").unwrap();
    assert_eq!(result(&outcome), (Status::Indeterminate, Some(STALE_AFTER)));
}

#[test]
fn cancellation_after_submission_is_recorded_without_replay() {
    let rig = Rig::new(Memory::default());
    let mut host = Host::admitted(1, 60, 240);
    let canceled = Arc::new(AtomicBool::new(false));
    host.panel.cancel_on_apply = Some(canceled.clone());
    let outcome = rig
        .set_canceled(&mut host, 1, "fixed-240", &canceled)
        .unwrap();
    // The writer's readback ran after APPLY, but the interrupted reply query
    // cannot show the change, so the record is uncertain.
    assert_eq!((host.submissions(), host.panel.reads), (1, 1));
    assert_eq!(result(&outcome), (Status::Indeterminate, Some(UNCONFIRMED)));
    assert_eq!(outcome.state.refresh_key(), None);
    assert!(rig.unresolved().refresh);
    host.panel.cancel_on_apply = None;
    let blocked = rig.set(&mut host, 1, "fixed-60").unwrap();
    assert_eq!(result(&blocked), (Status::Blocked, Some(PAUSED)));
    assert_eq!(host.submissions(), 1);
}

#[test]
fn fatal_reply_query_records_certainty_before_returning() {
    let rig = Rig::new(Memory::default());
    let mut host = Host::admitted(1, 60, 240);
    host.fail_query = true;
    let error = rig
        .set(&mut host, 1, "fixed-240")
        .err()
        .expect("a fatal query stops the feature");
    assert_eq!(error.to_string(), "injected reply query failure");
    assert_eq!(host.submissions(), 1);
    let record = rig.file.record();
    assert!(flag(&record, "AutomaticWritesPaused") && flag(&record, "UncertainRefreshRate"));
    assert!(rig.unresolved().refresh);
}

#[test]
fn refresh_uncertainty_gates_only_refresh_and_other_gates_never_block_it() {
    // A refresh Indeterminate leaves HDR and brightness writable.
    let rig = Rig::new(Memory::default());
    let mut host = Host::admitted(1, 60, 240);
    host.panel.effect = Effect::Nothing;
    let outcome = rig.set(&mut host, 1, "fixed-240").unwrap();
    assert_eq!(outcome.status, Status::Indeterminate);
    let unresolved = rig.unresolved();
    assert_eq!(unresolved, Fields::REFRESH);
    assert!(!unresolved.hdr && !unresolved.brightness);
    let record = rig.file.record();
    assert!(!flag(&record, "UncertainHdr") && !flag(&record, "UncertainBrightness"));

    // A paused managed checkpoint with HDR and brightness uncertain does not
    // gate a refresh write; the managed pause keeps every affected field.
    let file = Memory::with(
        r#"{"SchemaVersion":4,"LastReconciledPowerSource":1,"ManualHold":true,"AutomaticWritesPaused":true,"UncertainHdr":true,"UncertainBrightness":true}"#,
    );
    let rig = Rig::new(file);
    let both = Fields {
        hdr: true,
        brightness: true,
        ..Fields::default()
    };
    assert_eq!(rig.unresolved(), both);
    let mut host = Host::admitted(1, 60, 240);
    let outcome = rig.set(&mut host, 1, "fixed-240").unwrap();
    assert_eq!(result(&outcome), (Status::Succeeded, None));
    assert_eq!(host.submissions(), 1);
    assert_eq!(rig.unresolved(), both);
    let record = rig.file.record();
    assert!(flag(&record, "AutomaticWritesPaused"));
    assert!(flag(&record, "UncertainRefreshRate") && flag(&record, "UncertainHdr"));

    // A read with a refresh option but no brightness clears only refresh.
    let file = Memory::with(
        r#"{"SchemaVersion":4,"LastReconciledPowerSource":1,"ManualHold":true,"AutomaticWritesPaused":true,"UncertainRefreshRate":true,"UncertainBrightness":true}"#,
    );
    let rig = Rig::new(file);
    assert_eq!(
        rig.set(&mut Host::admitted(1, 60, 240), 1, "fixed-240")
            .unwrap()
            .error
            .as_deref(),
        Some(PAUSED)
    );
    rig.explicit_read(&snapshot(Some("fixed-240")));
    assert_eq!(rig.unresolved(), Fields::BRIGHTNESS);
    assert!(flag(&rig.file.record(), "AutomaticWritesPaused"));
}

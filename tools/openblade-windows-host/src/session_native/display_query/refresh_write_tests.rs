//! Manual setRefreshRate through the shared Rust refresh writer against a
//! scripted native boundary, with injected power, fence, reply query and
//! durable storage. No Windows supplier, endpoint, CCD or SetDisplayConfig
//! call (VALIDATE included) or hardware is used.
use super::*;
use crate::session_native::display_query::{
    manual_write::CHANGING,
    observation::fixture_query,
    test_support::{FencedTopology, Host, Memory, Rig, TestPanel, flag, result},
};
use openblade_platform_windows::display::{
    DisplayReadError, InternalDisplayRefresh, Operation, RefreshRate,
    refresh_write::{PrepareError, RefreshObservation, RefreshPlan},
    scripted::{RefreshTiming, Scenario},
};
use std::{collections::VecDeque, sync::Arc};

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
    /// The reply query shows this refresh key instead of the panel's.
    reply_key: Option<Option<&'static str>>,
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
    fn observe(&mut self, key: &str) -> Result<RefreshObservation, DisplayReadError> {
        self.observations += 1;
        self.observe_script.pop_front().unwrap_or_else(|| {
            Scenario::Available.observe_refresh(true, self.timing(), self.replaced, key)
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

impl TestPanel for Panel {
    type Value = &'static str;
    type Request = SetRefreshRate;

    fn request(expected_power_source: u8, key: &'static str) -> SetRefreshRate {
        SetRefreshRate {
            expected_power_source,
            key: key.into(),
        }
    }

    fn fenced(&self) -> FencedTopology {
        let timing = self.timing();
        Box::new(move || Scenario::Available.topology_with_hdr(true, timing, false))
    }

    fn reply(&self) -> Snapshot {
        let key = match self.reply_key {
            Some(key) => key.map(String::from),
            None => self.key(),
        };
        snapshot(key.as_deref())
    }
}

/// An admitted host over a panel at these desktop and signal rates.
fn admitted(source: u8, desktop: u32, physical: u32) -> Host<Panel> {
    Host::admitted(
        source,
        Panel {
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
            reply_key: None,
        },
    )
}

impl Host<Panel> {
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

/// A reply query with the given classified refresh option.
fn snapshot(key: Option<&str>) -> Snapshot {
    let mut value = serde_json::to_value(fixture_query("available")).unwrap();
    value["currentRefreshKey"] = key.into();
    serde_json::from_value(value).unwrap()
}

#[test]
fn requested_option_is_validated_fenced_applied_once_and_recorded() {
    let rig = Rig::new(Memory::default());
    // Issue #884: a 60 Hz desktop over a 240 Hz signal classifies no option.
    let mut host = admitted(1, 60, 240);
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
    let mut host = admitted(1, 240, 240);
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
    let mut host = admitted(0, 240, 240);
    let outcome = rig.set(&mut host, 0, "fixed-60").unwrap();
    assert_eq!(result(&outcome), (Status::Succeeded, None));
    assert_eq!(host.panel.validated, [None]);
    assert_eq!(host.panel.applied, [None]);
    assert_eq!((host.panel.desktop, host.panel.physical), (60, 60));

    // From the split timing the active 240 Hz signal is known and supplied.
    let mut host = admitted(0, 60, 240);
    let outcome = rig.set(&mut host, 0, "fixed-240").unwrap();
    assert_eq!(result(&outcome), (Status::Succeeded, None));
    assert_eq!(host.panel.validated, [Some(240)]);
    assert_eq!(host.panel.applied, [Some(240)]);
}

#[test]
fn unknown_or_unavailable_keys_fail_without_validation() {
    let rig = Rig::new(Memory::default());
    for key in ["fixed-75", "dynamic-60-240", "Fixed-240", "fixed-240 "] {
        let mut host = admitted(1, 60, 240);
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
    let mut host = admitted(1, 60, 240);
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
    let cases = || -> [(Result<RefreshObservation, DisplayReadError>, &str); 4] {
        [
            (
                Scenario::Unavailable.observe_refresh(
                    true,
                    RefreshTiming::default(),
                    false,
                    "fixed-240",
                ),
                NO_INTERNAL,
            ),
            (
                Scenario::NativeError.observe_refresh(
                    true,
                    RefreshTiming::default(),
                    false,
                    "fixed-240",
                ),
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
        ]
    };
    // Managed refresh never reconciles a refusal with the reply: it fails
    // even when the panel already shows the request (240/240).
    for desktop in [60, 240] {
        for (observation, expected) in cases() {
            let rig = Rig::new(Memory::default());
            let mut host = admitted(1, desktop, 240);
            assert_eq!(
                host.panel.reply().refresh_key() == Some("fixed-240"),
                desktop == 240
            );
            host.panel.observe_script.push_back(observation);
            let outcome = rig.set(&mut host, 1, "fixed-240").unwrap();
            assert_eq!(result(&outcome), (Status::Failed, Some(expected)));
            assert_eq!(host.interactions(), (0, 0, 0));
            assert!(!rig.unresolved().refresh);
        }
    }
}

#[test]
fn fence_refusals_never_submit() {
    // Power admission precedes the attempt: `manual_write` tests it once.
    let rig = Rig::new(Memory::legacy(0));

    // Origin, session, power generation or Stop changed after validation.
    let mut host = admitted(1, 60, 240);
    host.admit = false;
    let outcome = rig.set(&mut host, 1, "fixed-240").unwrap();
    assert_eq!(result(&outcome), (Status::Blocked, Some(STALE)));
    assert_eq!(host.interactions(), (1, 1, 0));

    // The fence's fresh topology no longer shows the observed panel.
    let mut host = admitted(1, 60, 240);
    host.changed = true;
    let outcome = rig.set(&mut host, 1, "fixed-240").unwrap();
    assert_eq!(result(&outcome), (Status::Blocked, Some(PANEL_CHANGED)));
    assert_eq!(host.interactions(), (1, 1, 0));

    // The observed panel was replaced before the fence's fresh read.
    let mut host = admitted(1, 60, 240);
    host.panel.replaced = true;
    let outcome = rig.set(&mut host, 1, "fixed-240").unwrap();
    assert_eq!(result(&outcome), (Status::Blocked, Some(PANEL_CHANGED)));
    assert_eq!(host.submissions(), 0);

    // Canceled before submission (client loss, deadline or Stop).
    let mut host = admitted(1, 60, 240);
    let outcome = rig
        .set_canceled(&mut host, 1, "fixed-240", &AtomicBool::new(true))
        .unwrap();
    assert_eq!(result(&outcome), (Status::Blocked, Some(CANCELED)));
    assert_eq!(host.interactions(), (1, 0, 0));

    // Blocked completions restore the inherited (unpaused) state.
    let record = rig.file.record();
    assert!(!flag(&record, "AutomaticWritesPaused") && !flag(&record, "UncertainRefreshRate"));
    assert!(!rig.unresolved().refresh);
    let mut host = admitted(1, 60, 240);
    assert_eq!(
        rig.set(&mut host, 1, "fixed-240").unwrap().status,
        Status::Succeeded
    );
}

#[test]
fn apply_error_is_uncertain_and_pauses_without_replay_across_restart_until_explicit_read() {
    for effect in [Effect::Nothing, Effect::Plan] {
        let rig = Rig::new(Memory::default());
        let mut host = admitted(2, 60, 240);
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

        // Same process: refused before any device interaction, never replayed,
        // with the reply query's snapshot.
        host.panel.apply_status = 0;
        let (observations, queries) = (host.panel.observations, host.queries);
        let blocked = rig.set(&mut host, 2, "fixed-240").unwrap();
        assert_eq!(result(&blocked), (Status::Blocked, Some(PAUSED)));
        assert_eq!(host.queries, queries + 1);
        assert_eq!(
            serde_json::to_value(&blocked.state).unwrap(),
            serde_json::to_value(host.panel.reply()).unwrap()
        );
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

        // An unavailable read observed nothing: the gate stays closed.
        restarted.explicit_read(&Snapshot::unavailable("gone"));
        assert!(restarted.unresolved().refresh);
        // The panel's own read reopens the native gate even when its mode
        // matches no option (issue #884: still 60 Hz over 240 Hz); the managed
        // pause resumes only once a read classifies the mode.
        let classified = host.panel.key().is_some();
        assert_eq!(classified, effect == Effect::Plan);
        restarted.explicit_read(&host.panel.reply());
        assert!(!restarted.unresolved().refresh);
        let record = restarted.file.record();
        assert_eq!(flag(&record, "AutomaticWritesPaused"), !classified);
        assert_eq!(flag(&record, "UncertainRefreshRate"), !classified);
        // The repairing request is admitted; its confirmed result resumes.
        host.panel.effect = Effect::Plan;
        let outcome = restarted.set(&mut host, 2, "fixed-240").unwrap();
        assert_eq!(result(&outcome), (Status::Succeeded, None));
        assert_eq!(host.submissions(), if classified { 1 } else { 2 });
        let record = restarted.file.record();
        assert!(!flag(&record, "AutomaticWritesPaused") && !flag(&record, "UncertainRefreshRate"));
    }
}

#[test]
fn desktop_only_apply_is_uncertain_and_an_unclassified_read_admits_the_repair() {
    let rig = Rig::new(Memory::default());
    let mut host = admitted(1, 240, 240);
    host.panel.effect = Effect::DesktopOnly;
    let outcome = rig.set(&mut host, 1, "fixed-60").unwrap();
    assert_eq!((host.panel.desktop, host.panel.physical), (60, 240));
    assert_eq!(result(&outcome), (Status::Indeterminate, Some(UNCONFIRMED)));
    assert_eq!(host.submissions(), 1);
    assert!(rig.unresolved().refresh);
    assert!(flag(&rig.file.record(), "UncertainRefreshRate"));

    // The gate survives a restart and refuses without device interaction.
    let restarted = Rig::new(rig.file.clone());
    let observations = host.panel.observations;
    let blocked = restarted.set(&mut host, 1, "fixed-60").unwrap();
    assert_eq!(result(&blocked), (Status::Blocked, Some(PAUSED)));
    assert_eq!(host.panel.observations, observations);

    // The 60 Hz desktop over the 240 Hz signal classifies no option, but an
    // explicit read observed it: the gate reopens, the managed pause stays.
    let read = host.panel.reply();
    assert_eq!(read.refresh_key(), None);
    restarted.explicit_read(&read);
    assert!(!restarted.unresolved().refresh);
    let record = restarted.file.record();
    assert!(flag(&record, "AutomaticWritesPaused") && flag(&record, "UncertainRefreshRate"));

    // The repairing request runs and its confirmation resumes automation.
    host.panel.effect = Effect::Plan;
    let outcome = restarted.set(&mut host, 1, "fixed-60").unwrap();
    assert_eq!(result(&outcome), (Status::Succeeded, None));
    assert_eq!(host.submissions(), 2);
    assert_eq!((host.panel.desktop, host.panel.physical), (60, 60));
    let record = restarted.file.record();
    assert!(!flag(&record, "AutomaticWritesPaused") && !flag(&record, "UncertainRefreshRate"));
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
        let mut host = admitted(1, 60, 240);
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
    let mut host = admitted(1, 60, 240);
    host.panel.reply_key = Some(Some("fixed-60"));
    let outcome = rig.set(&mut host, 1, "fixed-240").unwrap();
    assert_eq!(result(&outcome), (Status::Indeterminate, Some(UNCONFIRMED)));
    assert_eq!(host.submissions(), 1);
    assert!(rig.unresolved().refresh);
}

#[test]
fn cancellation_after_submission_is_recorded_without_replay() {
    let rig = Rig::new(Memory::default());
    let mut host = admitted(1, 60, 240);
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
fn refresh_uncertainty_gates_only_refresh_and_other_gates_never_block_it() {
    // A refresh Indeterminate leaves HDR and brightness writable.
    let rig = Rig::new(Memory::default());
    let mut host = admitted(1, 60, 240);
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
    let mut host = admitted(1, 60, 240);
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
        rig.set(&mut admitted(1, 60, 240), 1, "fixed-240")
            .unwrap()
            .error
            .as_deref(),
        Some(PAUSED)
    );
    rig.explicit_read(&snapshot(Some("fixed-240")));
    assert_eq!(rig.unresolved(), Fields::BRIGHTNESS);
    assert!(flag(&rig.file.record(), "AutomaticWritesPaused"));
}

/// Every writer refusal before APPLY, with its managed status and text.
#[test]
fn refusals_map_to_managed_statuses_and_texts() {
    let attempt = |reason| match refused(reason) {
        Attempt::Failed {
            error,
            reply_resolves,
        } => {
            // Managed refresh never reconciles a failure with the reply.
            assert!(!reply_resolves);
            (Status::Failed, error.into_owned())
        }
        Attempt::Done(status, error) => (status, error.into_owned()),
        Attempt::Unchanged | Attempt::Confirmed => panic!("a refusal is not a result"),
    };
    let prepared = "The refresh-rate configuration could not be prepared: ";
    for (reason, status, text) in [
        (
            RefreshError::Prepare(PrepareError::PathChanged),
            Status::Failed,
            format!(
                "{prepared}The internal display path changed before the refresh-rate setting \
                 could be applied."
            ),
        ),
        (
            RefreshError::Prepare(PrepareError::DynamicTimingUnavailable),
            Status::Failed,
            format!(
                "{prepared}Windows no longer reports the requested Dynamic refresh-rate timing."
            ),
        ),
        (
            RefreshError::Prepare(PrepareError::InvalidOption),
            Status::Failed,
            format!("{prepared}Arithmetic operation resulted in an overflow."),
        ),
        (
            RefreshError::ValidationRejected(87),
            Status::Failed,
            format!(
                "{prepared}Windows rejected the requested display configuration: Win32 error 87."
            ),
        ),
        (
            RefreshError::MultipleInternalDisplays,
            Status::Failed,
            MULTIPLE.into(),
        ),
        (
            RefreshError::NoInternalDisplay,
            Status::Failed,
            NO_INTERNAL.into(),
        ),
        (
            RefreshError::OptionUnavailable,
            Status::Failed,
            UNAVAILABLE.into(),
        ),
        (
            RefreshError::PanelChanged,
            Status::Blocked,
            PANEL_CHANGED.into(),
        ),
        (RefreshError::AdmissionDenied, Status::Blocked, STALE.into()),
        (RefreshError::Cancelled, Status::Blocked, CANCELED.into()),
    ] {
        assert_eq!(attempt(reason), (status, text));
    }
}

//! Manual setHdr through the shared Rust HDR writer against a scripted native
//! boundary, with injected power, fence, reply query and durable storage.
//! No Windows supplier, endpoint, CCD call or hardware is used.
use super::*;
use crate::session_native::display_query::{
    ccd_write::CHANGING,
    observation::fixture_query,
    test_support::{Memory, Rig},
    write::{CHANGED_POWER, CHECKPOINT, STALE_AFTER, UNCONFIRMED_POWER},
};
use openblade_platform_windows::display::SubmissionFence;
use openblade_platform_windows::display::{
    DisplayReadError, EndpointId, InternalDisplayTopology, Operation,
    scripted::{RefreshTiming, Scenario},
};
use std::{
    cell::Cell,
    collections::VecDeque,
    sync::{Arc, atomic::AtomicUsize},
};

fn topology(
    supported: bool,
    enabled: bool,
    replaced: bool,
) -> Result<InternalDisplayTopology, DisplayReadError> {
    let scenario = if supported {
        Scenario::Available
    } else {
        Scenario::HdrUnsupported
    };
    scenario.topology_with_hdr(enabled, RefreshTiming::default(), replaced)
}

/// A reply query with the given advanced-color projection.
fn snapshot(supported: bool, enabled: bool) -> Snapshot {
    let mut value = serde_json::to_value(fixture_query("available")).unwrap();
    value["hdrSupported"] = supported.into();
    value["hdrEnabled"] = (supported && enabled).into();
    serde_json::from_value(value).unwrap()
}

/// The scripted panel and SET_HDR_STATE boundary.
struct Panel {
    supported: bool,
    enabled: bool,
    replaced: bool,
    /// Reads consumed before the panel's own state.
    script: VecDeque<Result<InternalDisplayTopology, DisplayReadError>>,
    status: u32,
    /// Whether a submission changes the panel, whatever status it returns.
    apply: bool,
    reads: usize,
    submissions: Vec<bool>,
    /// Set after the submission boundary (cancellation racing a write).
    cancel_on_submit: Option<Arc<AtomicBool>>,
}

impl HdrNative for Panel {
    fn topology(&mut self) -> Result<InternalDisplayTopology, DisplayReadError> {
        self.reads += 1;
        self.script
            .pop_front()
            .unwrap_or_else(|| topology(self.supported, self.enabled, self.replaced))
    }

    fn submit(&mut self, _: EndpointId, enabled: bool) -> u32 {
        self.submissions.push(enabled);
        if let Some(cancel) = &self.cancel_on_submit {
            cancel.store(true, Ordering::SeqCst);
        }
        if self.apply && self.supported {
            self.enabled = enabled;
        }
        self.status
    }
}

struct TestFence {
    panel: (bool, bool, bool),
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
        let (supported, enabled, replaced) = self.panel;
        topology(supported, enabled, replaced)
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
    /// The reply query shows the opposite HDR state.
    stale_reply: bool,
    /// The reply query fails fatally.
    fail_query: bool,
}

impl Host {
    fn admitted(source: u8, enabled: bool) -> Self {
        Self {
            panel: Panel {
                supported: true,
                enabled,
                replaced: false,
                script: VecDeque::new(),
                status: 0,
                apply: true,
                reads: 0,
                submissions: Vec::new(),
                cancel_on_submit: None,
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
            stale_reply: false,
            fail_query: false,
        }
    }

    fn submissions(&self) -> usize {
        self.panel.submissions.len()
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
            panel: (
                self.panel.supported,
                self.panel.enabled,
                self.panel.replaced,
            ),
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
        Ok(snapshot(
            self.panel.supported,
            self.panel.enabled != self.stale_reply,
        ))
    }
}

/// The manual request through the shared rig.
trait Set {
    fn set(&self, host: &mut Host, expected: u8, enabled: bool) -> io::Result<CcdWriteOutcome>;
    fn set_canceled(
        &self,
        host: &mut Host,
        expected: u8,
        enabled: bool,
        canceled: &AtomicBool,
    ) -> io::Result<CcdWriteOutcome>;
}

impl Set for Rig {
    fn set(&self, host: &mut Host, expected: u8, enabled: bool) -> io::Result<CcdWriteOutcome> {
        self.set_canceled(host, expected, enabled, &AtomicBool::new(false))
    }

    fn set_canceled(
        &self,
        host: &mut Host,
        expected: u8,
        enabled: bool,
        canceled: &AtomicBool,
    ) -> io::Result<CcdWriteOutcome> {
        set_hdr(
            host,
            &self.owner,
            &self.recovery,
            SetHdr {
                expected_power_source: expected,
                enabled,
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
fn requested_state_is_submitted_once_confirmed_and_recorded() {
    let rig = Rig::new(Memory::default());
    let mut host = Host::admitted(1, false);
    let outcome = rig.set(&mut host, 1, true).unwrap();
    assert_eq!(result(&outcome), (Status::Succeeded, None));
    assert_eq!(outcome.state.hdr(), Some(true));
    assert_eq!(host.panel.submissions, [true]);
    // Target read, fresh pre-submission read and independent readback.
    assert_eq!(host.panel.reads, 3);
    assert_eq!(host.fence_checks.load(Ordering::SeqCst), 1);
    assert_eq!(host.queries, 1);
    // Begin and complete both reached durable storage before returning.
    assert_eq!(rig.file.writes.load(Ordering::SeqCst), 2);
    let record = rig.file.record();
    assert_eq!(record["LastReconciledPowerSource"], 1);
    assert!(flag(&record, "ManualHold"));
    assert!(!flag(&record, "AutomaticWritesPaused"));
    assert!(!flag(&record, "UncertainHdr") && !flag(&record, "UncertainBrightness"));

    // The current state never submits again; the opposite state does.
    let unchanged = rig.set(&mut host, 1, true).unwrap();
    assert_eq!(result(&unchanged), (Status::Succeeded, None));
    assert_eq!(host.submissions(), 1);
    assert_eq!(host.fence_checks.load(Ordering::SeqCst), 1);
    let disabled = rig.set(&mut host, 1, false).unwrap();
    assert_eq!(result(&disabled), (Status::Succeeded, None));
    assert_eq!(disabled.state.hdr(), Some(false));
    assert_eq!(host.panel.submissions, [true, false]);
}

#[test]
fn denied_admission_and_fence_refusals_never_submit() {
    let rig = Rig::new(Memory::legacy(0));

    // Unconfirmed power: blocked after a durable manual hold, never read.
    let mut host = Host::admitted(0, false);
    host.power = None;
    let outcome = rig.set(&mut host, 0, true).unwrap();
    assert_eq!(result(&outcome), (Status::Blocked, Some(UNCONFIRMED_POWER)));
    assert_eq!(host.panel.reads, 0);
    assert_eq!(host.queries, 1, "only the reply query ran");
    let record = rig.file.record();
    assert!(!flag(&record, "AutomaticWritesPaused") && !flag(&record, "UncertainHdr"));

    // Changed power source.
    let mut host = Host::admitted(1, false);
    let outcome = rig.set(&mut host, 2, true).unwrap();
    assert_eq!(result(&outcome), (Status::Blocked, Some(CHANGED_POWER)));
    assert_eq!(host.panel.reads, 0);

    // Origin, session, power generation or Stop changed after preparation.
    let mut host = Host::admitted(1, false);
    host.admit = false;
    let outcome = rig.set(&mut host, 1, true).unwrap();
    assert_eq!(result(&outcome), (Status::Blocked, Some(STALE)));
    assert_eq!(host.fence_checks.load(Ordering::SeqCst), 1);
    assert_eq!(host.submissions(), 0);

    // The fence's fresh topology no longer shows the admitted panel.
    let mut host = Host::admitted(1, false);
    host.changed = true;
    let outcome = rig.set(&mut host, 1, true).unwrap();
    assert_eq!(result(&outcome), (Status::Blocked, Some(PANEL_CHANGED)));
    assert_eq!(host.submissions(), 0);

    // The panel was replaced between target selection and the fresh read.
    let mut host = Host::admitted(1, false);
    host.panel.script.push_back(topology(true, false, false));
    host.panel.replaced = true;
    let outcome = rig.set(&mut host, 1, true).unwrap();
    assert_eq!(result(&outcome), (Status::Blocked, Some(PANEL_CHANGED)));
    assert_eq!(host.fence_checks.load(Ordering::SeqCst), 0);
    assert_eq!(host.submissions(), 0);

    // Canceled before submission (client loss, deadline or Stop).
    let mut host = Host::admitted(1, false);
    let outcome = rig
        .set_canceled(&mut host, 1, true, &AtomicBool::new(true))
        .unwrap();
    assert_eq!(result(&outcome), (Status::Blocked, Some(CANCELED)));
    assert_eq!(host.submissions(), 0);

    // Blocked completions restore the inherited (unpaused) state.
    let record = rig.file.record();
    assert!(!flag(&record, "AutomaticWritesPaused") && !flag(&record, "UncertainHdr"));
    assert!(!rig.unresolved().hdr);
    let mut host = Host::admitted(1, false);
    assert_eq!(
        rig.set(&mut host, 1, true).unwrap().status,
        Status::Succeeded
    );
}

#[test]
fn preparation_failures_fail_unless_a_fresh_read_shows_the_request() {
    let native = |status| DisplayReadError::Native {
        operation: Operation::ActivePaths,
        status,
    };
    type Read = fn() -> Result<InternalDisplayTopology, DisplayReadError>;
    let cases: [(Read, &str); 5] = [
        (
            || Ok(InternalDisplayTopology::NoInternalDisplay),
            NO_INTERNAL,
        ),
        (|| Ok(InternalDisplayTopology::Empty), NO_INTERNAL),
        (
            || Ok(InternalDisplayTopology::MultipleInternalDisplays),
            MULTIPLE,
        ),
        (
            || {
                Err(DisplayReadError::Native {
                    operation: Operation::ActivePaths,
                    status: 5,
                })
            },
            "Windows could not read the active display configuration: Win32 error 5.",
        ),
        (|| Err(DisplayReadError::TopologyChanging), CHANGING),
    ];
    for (read, expected) in cases {
        let rig = Rig::new(Memory::default());
        let mut host = Host::admitted(1, false);
        host.panel.script.push_back(read());
        let outcome = rig.set(&mut host, 1, true).unwrap();
        assert_eq!(result(&outcome), (Status::Failed, Some(expected)));
        assert_eq!(
            (host.submissions(), host.fence_checks.load(Ordering::SeqCst)),
            (0, 0)
        );
        let record = rig.file.record();
        assert!(!flag(&record, "AutomaticWritesPaused") && !flag(&record, "UncertainHdr"));
        assert!(!rig.unresolved().hdr);

        // Managed ResolveHdrMutationResult: the reply already shows the request.
        let mut host = Host::admitted(1, false);
        host.panel.script.push_back(read());
        let outcome = rig.set(&mut host, 1, false).unwrap();
        assert_eq!(result(&outcome), (Status::Succeeded, None));
        assert_eq!(host.submissions(), 0);
    }

    // The fresh pre-submission read is unreadable after target selection.
    let rig = Rig::new(Memory::default());
    let mut host = Host::admitted(1, false);
    host.panel.script.push_back(topology(true, false, false));
    host.panel.script.push_back(Err(native(31)));
    let outcome = rig.set(&mut host, 1, true).unwrap();
    assert_eq!(
        result(&outcome),
        (
            Status::Failed,
            Some("Windows could not read the active display configuration: Win32 error 31.")
        )
    );
    assert_eq!(host.submissions(), 0);
}

#[test]
fn unsupported_panel_fails_without_submission() {
    let rig = Rig::new(Memory::default());
    let mut host = Host::admitted(1, false);
    host.panel.supported = false;
    for enabled in [true, false] {
        let outcome = rig.set(&mut host, 1, enabled).unwrap();
        assert_eq!(result(&outcome), (Status::Failed, Some(NOT_SUPPORTED)));
        assert_eq!(outcome.state.hdr(), None);
    }
    assert_eq!(
        (host.submissions(), host.fence_checks.load(Ordering::SeqCst)),
        (0, 0)
    );
    assert!(!rig.unresolved().hdr);
}

#[test]
fn checkpoint_failure_before_submission_blocks_without_device_interaction() {
    let file = Memory::legacy(1);
    file.allow.store(0, Ordering::SeqCst);
    let rig = Rig::new(file);
    let mut host = Host::admitted(1, false);
    let outcome = rig.set(&mut host, 1, true).unwrap();
    assert_eq!(result(&outcome), (Status::Blocked, Some(CHECKPOINT)));
    assert_eq!(host.panel.reads, 0);
    assert_eq!(host.fence_checks.load(Ordering::SeqCst), 0);
    assert_eq!(host.submissions(), 0);

    // No prior checkpoint and no confirmed source: nothing can be saved.
    let rig = Rig::new(Memory::default());
    let mut host = Host::admitted(1, false);
    host.power = None;
    assert_eq!(
        rig.set(&mut host, 1, true).unwrap().error.as_deref(),
        Some(CHECKPOINT)
    );
    assert_eq!((host.panel.reads, host.submissions()), (0, 0));
}

#[test]
fn possible_submission_pauses_without_replay_across_restart_until_explicit_read() {
    for (status, expected) in [
        (0, UNCONFIRMED),
        (87, "Windows could not change HDR: Win32 error 87."),
    ] {
        let rig = Rig::new(Memory::default());
        let mut host = Host::admitted(2, false);
        host.panel.apply = false;
        host.panel.status = status;
        let outcome = rig.set(&mut host, 2, true).unwrap();
        assert_eq!(result(&outcome), (Status::Indeterminate, Some(expected)));
        assert_eq!(host.submissions(), 1);
        let record = rig.file.record();
        assert!(flag(&record, "AutomaticWritesPaused") && flag(&record, "UncertainHdr"));
        assert!(!flag(&record, "UncertainBrightness"));
        assert_eq!(rig.unresolved(), Fields::HDR);

        // Same process: refused before any device interaction, never replayed.
        host.panel.apply = true;
        host.panel.status = 0;
        let reads = host.panel.reads;
        let blocked = rig.set(&mut host, 2, true).unwrap();
        assert_eq!(result(&blocked), (Status::Blocked, Some(PAUSED)));
        assert_eq!((host.submissions(), host.panel.reads), (1, reads));

        // Restart: the durable checkpoint restores the gate.
        let restarted = Rig::new(rig.file.clone());
        assert!(restarted.unresolved().hdr);
        let blocked = restarted.set(&mut host, 2, true).unwrap();
        assert_eq!(result(&blocked), (Status::Blocked, Some(PAUSED)));
        assert_eq!(host.submissions(), 1);

        // A read that cannot observe HDR keeps the gate; one that does reopens it.
        restarted.explicit_read(&Snapshot::unavailable("gone"));
        restarted.explicit_read(&snapshot(false, false));
        assert!(restarted.unresolved().hdr);
        restarted.explicit_read(&snapshot(true, false));
        assert!(!restarted.unresolved().hdr);
        let record = restarted.file.record();
        assert!(!flag(&record, "AutomaticWritesPaused") && !flag(&record, "UncertainHdr"));
        let outcome = restarted.set(&mut host, 2, true).unwrap();
        assert_eq!(result(&outcome), (Status::Succeeded, None));
        assert_eq!(host.panel.submissions, [true, true]);
    }
}

#[test]
fn rejected_submission_whose_readback_matches_succeeds() {
    let rig = Rig::new(Memory::default());
    let mut host = Host::admitted(1, false);
    host.panel.status = 31;
    let outcome = rig.set(&mut host, 1, true).unwrap();
    assert_eq!(result(&outcome), (Status::Succeeded, None));
    assert_eq!(host.submissions(), 1);
    assert!(!rig.unresolved().hdr);
    assert!(!flag(&rig.file.record(), "AutomaticWritesPaused"));
}

#[test]
fn reply_that_does_not_show_a_confirmed_change_is_indeterminate() {
    let rig = Rig::new(Memory::default());
    let mut host = Host::admitted(1, false);
    host.stale_reply = true;
    let outcome = rig.set(&mut host, 1, true).unwrap();
    assert_eq!(result(&outcome), (Status::Indeterminate, Some(UNCONFIRMED)));
    assert_eq!(host.submissions(), 1);
    assert!(rig.unresolved().hdr);
    assert!(flag(&rig.file.record(), "UncertainHdr"));
}

#[test]
fn power_change_after_a_successful_change_is_indeterminate() {
    let drifted = Some(PowerContext {
        source: 0,
        generation: 8,
    });
    for initial in [false, true] {
        let rig = Rig::new(Memory::default());
        let mut host = Host::admitted(1, initial);
        host.later = Some(drifted);
        let outcome = rig.set(&mut host, 1, true).unwrap();
        // Managed ExecuteSetterAsync.EnsureCurrent, applied to a submitted
        // change and to an unchanged request alike.
        assert_eq!(result(&outcome), (Status::Indeterminate, Some(STALE_AFTER)));
        assert_eq!(host.submissions(), usize::from(!initial));
        assert_eq!(host.captures.get(), 2);
        assert!(rig.unresolved().hdr);
        assert!(flag(&rig.file.record(), "AutomaticWritesPaused"));
    }

    // Lost confirmation after success is also stale.
    let rig = Rig::new(Memory::default());
    let mut host = Host::admitted(1, false);
    host.later = Some(None);
    let outcome = rig.set(&mut host, 1, true).unwrap();
    assert_eq!(result(&outcome), (Status::Indeterminate, Some(STALE_AFTER)));
}

#[test]
fn cancellation_after_submission_is_recorded_without_replay() {
    let rig = Rig::new(Memory::default());
    let mut host = Host::admitted(1, false);
    let canceled = Arc::new(AtomicBool::new(false));
    host.panel.cancel_on_submit = Some(canceled.clone());
    let outcome = rig.set_canceled(&mut host, 1, true, &canceled).unwrap();
    // The writer's readback ran after the submission, but the interrupted
    // reply query cannot show the change, so the record is uncertain.
    assert_eq!(host.submissions(), 1);
    assert_eq!(result(&outcome), (Status::Indeterminate, Some(UNCONFIRMED)));
    assert_eq!(
        outcome.state.hdr(),
        None,
        "the reply is the unavailable refresh"
    );
    assert!(rig.unresolved().hdr);
    host.panel.cancel_on_submit = None;
    let blocked = rig.set(&mut host, 1, false).unwrap();
    assert_eq!(result(&blocked), (Status::Blocked, Some(PAUSED)));
    assert_eq!(host.submissions(), 1);
}

#[test]
fn unchanged_request_is_never_uncertain_even_if_the_reply_read_is_interrupted() {
    let rig = Rig::new(Memory::default());
    let mut host = Host::admitted(1, true);
    let outcome = rig
        .set_canceled(&mut host, 1, true, &AtomicBool::new(true))
        .unwrap();
    assert_eq!(result(&outcome), (Status::Succeeded, None));
    assert_eq!(outcome.state.hdr(), None);
    assert_eq!(host.submissions(), 0);
    assert!(!rig.unresolved().hdr);
}

#[test]
fn fatal_reply_query_records_certainty_before_returning() {
    let rig = Rig::new(Memory::default());
    let mut host = Host::admitted(1, false);
    host.fail_query = true;
    let error = rig
        .set(&mut host, 1, true)
        .err()
        .expect("a fatal query stops the feature");
    assert_eq!(error.to_string(), "injected reply query failure");
    assert_eq!(host.submissions(), 1);
    let record = rig.file.record();
    assert!(flag(&record, "AutomaticWritesPaused") && flag(&record, "UncertainHdr"));
    assert!(rig.unresolved().hdr);

    // A refused request still reports its reply query failure.
    let blocked = rig.set(&mut host, 1, true).err().unwrap();
    assert_eq!(blocked.to_string(), "injected reply query failure");
    assert_eq!(host.submissions(), 1);
}

#[test]
fn uncertain_completion_persistence_latches_the_hdr_gate() {
    let rig = Rig::new(Memory::default());
    let mut host = Host::admitted(0, false);
    // Begin persists; the completion write then fails after a confirmed change.
    rig.file.allow.store(1, Ordering::SeqCst);
    let outcome = rig.set(&mut host, 0, true).unwrap();
    assert_eq!(outcome.status, Status::Succeeded);
    assert_eq!(rig.unresolved(), Fields::HDR);
    let record = rig.file.record();
    assert!(flag(&record, "AutomaticWritesPaused") && flag(&record, "UncertainHdr"));
    rig.file.allow.store(usize::MAX, Ordering::SeqCst);
    assert_eq!(
        rig.set(&mut host, 0, false).unwrap().error.as_deref(),
        Some(PAUSED)
    );
    assert_eq!(host.submissions(), 1);
}

#[test]
fn hdr_and_brightness_write_gates_are_independent() {
    // A paused managed checkpoint with only brightness uncertain.
    let file = Memory::with(
        r#"{"SchemaVersion":4,"LastReconciledPowerSource":1,"ManualHold":true,"AutomaticWritesPaused":true,"UncertainBrightness":true}"#,
    );
    let rig = Rig::new(file);
    assert_eq!(rig.unresolved(), Fields::BRIGHTNESS);
    let mut host = Host::admitted(1, false);
    let outcome = rig.set(&mut host, 1, true).unwrap();
    assert_eq!(result(&outcome), (Status::Succeeded, None));
    assert_eq!(host.submissions(), 1);
    // The managed pause keeps every affected field until all read back.
    assert_eq!(rig.unresolved(), Fields::BRIGHTNESS);
    let record = rig.file.record();
    assert!(flag(&record, "AutomaticWritesPaused"));
    assert!(flag(&record, "UncertainHdr") && flag(&record, "UncertainBrightness"));

    // An HDR-only uncertainty gates HDR; a read without brightness clears it
    // alone and leaves the managed pause in place.
    let file = Memory::with(
        r#"{"SchemaVersion":4,"LastReconciledPowerSource":1,"ManualHold":true,"AutomaticWritesPaused":true,"UncertainHdr":true,"UncertainBrightness":true}"#,
    );
    let rig = Rig::new(file);
    let mut host = Host::admitted(1, false);
    assert_eq!(
        rig.set(&mut host, 1, true).unwrap().error.as_deref(),
        Some(PAUSED)
    );
    rig.explicit_read(&snapshot(true, false));
    assert_eq!(rig.unresolved(), Fields::BRIGHTNESS);
    assert!(flag(&rig.file.record(), "AutomaticWritesPaused"));
    assert_eq!(
        rig.set(&mut host, 1, true).unwrap().status,
        Status::Succeeded
    );
}

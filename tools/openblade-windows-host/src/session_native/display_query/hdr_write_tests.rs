//! Manual setHdr through the shared Rust HDR writer against a scripted native
//! boundary, with injected power, fence, reply query and durable storage.
//! No Windows supplier, endpoint, CCD call or hardware is used.
use super::*;
use crate::session_native::display_query::{
    manual_write::CHANGING,
    observation::fixture_query,
    test_support::{FencedTopology, Host, Memory, Rig, TestPanel, flag, result},
};
use openblade_platform_windows::display::{
    DisplayReadError, EndpointId, InternalDisplayTopology, Operation,
    scripted::{RefreshTiming, Scenario},
};
use std::{collections::VecDeque, sync::Arc};

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
    /// The reply query shows the opposite HDR state.
    stale_reply: bool,
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

impl TestPanel for Panel {
    type Value = bool;
    type Request = SetHdr;

    fn request(expected_power_source: u8, enabled: bool) -> SetHdr {
        SetHdr {
            expected_power_source,
            enabled,
        }
    }

    fn fenced(&self) -> FencedTopology {
        let (supported, enabled, replaced) = (self.supported, self.enabled, self.replaced);
        Box::new(move || topology(supported, enabled, replaced))
    }

    fn reply(&self) -> Snapshot {
        snapshot(self.supported, self.enabled != self.stale_reply)
    }
}

/// An admitted host over a supported panel in the given HDR state.
fn admitted(source: u8, enabled: bool) -> Host<Panel> {
    Host::admitted(
        source,
        Panel {
            supported: true,
            enabled,
            replaced: false,
            script: VecDeque::new(),
            status: 0,
            apply: true,
            reads: 0,
            submissions: Vec::new(),
            cancel_on_submit: None,
            stale_reply: false,
        },
    )
}

impl Host<Panel> {
    fn submissions(&self) -> usize {
        self.panel.submissions.len()
    }
}

#[test]
fn requested_state_is_submitted_once_confirmed_and_recorded() {
    let rig = Rig::new(Memory::default());
    let mut host = admitted(1, false);
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
fn fence_refusals_never_submit() {
    // Power admission precedes the attempt: `manual_write` tests it once.
    let rig = Rig::new(Memory::legacy(0));

    // Origin, session, power generation or Stop changed after preparation.
    let mut host = admitted(1, false);
    host.admit = false;
    let outcome = rig.set(&mut host, 1, true).unwrap();
    assert_eq!(result(&outcome), (Status::Blocked, Some(STALE)));
    assert_eq!(host.fence_checks.load(Ordering::SeqCst), 1);
    assert_eq!(host.submissions(), 0);

    // The fence's fresh topology no longer shows the admitted panel.
    let mut host = admitted(1, false);
    host.changed = true;
    let outcome = rig.set(&mut host, 1, true).unwrap();
    assert_eq!(result(&outcome), (Status::Blocked, Some(PANEL_CHANGED)));
    assert_eq!(host.submissions(), 0);

    // The panel was replaced between target selection and the fresh read.
    let mut host = admitted(1, false);
    host.panel.script.push_back(topology(true, false, false));
    host.panel.replaced = true;
    let outcome = rig.set(&mut host, 1, true).unwrap();
    assert_eq!(result(&outcome), (Status::Blocked, Some(PANEL_CHANGED)));
    assert_eq!(host.fence_checks.load(Ordering::SeqCst), 0);
    assert_eq!(host.submissions(), 0);

    // Canceled before submission (client loss, deadline or Stop).
    let mut host = admitted(1, false);
    let outcome = rig
        .set_canceled(&mut host, 1, true, &AtomicBool::new(true))
        .unwrap();
    assert_eq!(result(&outcome), (Status::Blocked, Some(CANCELED)));
    assert_eq!(host.submissions(), 0);

    // Blocked completions restore the inherited (unpaused) state.
    let record = rig.file.record();
    assert!(!flag(&record, "AutomaticWritesPaused") && !flag(&record, "UncertainHdr"));
    assert!(!rig.unresolved().hdr);
    let mut host = admitted(1, false);
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
        let mut host = admitted(1, false);
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
        let mut host = admitted(1, false);
        host.panel.script.push_back(read());
        let outcome = rig.set(&mut host, 1, false).unwrap();
        assert_eq!(result(&outcome), (Status::Succeeded, None));
        assert_eq!(host.submissions(), 0);
    }

    // The fresh pre-submission read is unreadable after target selection.
    let rig = Rig::new(Memory::default());
    let mut host = admitted(1, false);
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
    let mut host = admitted(1, false);
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

    // Managed PrepareHdrCore returns NotSupported directly; unlike a topology
    // refusal it is not reconciled with a reply that shows the request.
    let mut host = admitted(1, false);
    // Target selection and the writer's own read both see it unsupported.
    host.panel.script.push_back(topology(false, false, false));
    host.panel.script.push_back(topology(false, false, false));
    assert_eq!(host.panel.reply().hdr(), Some(false));
    let outcome = rig.set(&mut host, 1, false).unwrap();
    assert_eq!(result(&outcome), (Status::Failed, Some(NOT_SUPPORTED)));
    assert_eq!(outcome.state.hdr(), Some(false));
    assert_eq!(host.submissions(), 0);
}

#[test]
fn possible_submission_pauses_without_replay_across_restart_until_explicit_read() {
    for (status, expected) in [
        (0, UNCONFIRMED),
        (87, "Windows could not change HDR: Win32 error 87."),
    ] {
        let rig = Rig::new(Memory::default());
        let mut host = admitted(2, false);
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

        // A read that cannot observe HDR keeps the gate.
        restarted.explicit_read(&Snapshot::unavailable("gone"));
        assert!(restarted.unresolved().hdr);
        // An available read observed HDR even while it reports no support:
        // the native gate reopens, the managed pause keeps HasCompleteReadback.
        restarted.explicit_read(&snapshot(false, false));
        assert!(!restarted.unresolved().hdr);
        let record = restarted.file.record();
        assert!(flag(&record, "AutomaticWritesPaused") && flag(&record, "UncertainHdr"));
        // Nothing can stack on that state: while its own read shows HDR
        // unsupported, setHdr is refused before any device submission.
        host.panel.supported = false;
        let refused = restarted.set(&mut host, 2, true).unwrap();
        assert_eq!(result(&refused), (Status::Failed, Some(NOT_SUPPORTED)));
        assert_eq!(host.submissions(), 1);
        assert!(!restarted.unresolved().hdr);
        host.panel.supported = true;
        // A complete readback also resumes the managed pause.
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
    let mut host = admitted(1, false);
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
    let mut host = admitted(1, false);
    host.panel.stale_reply = true;
    let outcome = rig.set(&mut host, 1, true).unwrap();
    assert_eq!(result(&outcome), (Status::Indeterminate, Some(UNCONFIRMED)));
    assert_eq!(host.submissions(), 1);
    assert!(rig.unresolved().hdr);
    assert!(flag(&rig.file.record(), "UncertainHdr"));
}

#[test]
fn cancellation_after_submission_is_recorded_without_replay() {
    let rig = Rig::new(Memory::default());
    let mut host = admitted(1, false);
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
    let mut host = admitted(1, true);
    let outcome = rig
        .set_canceled(&mut host, 1, true, &AtomicBool::new(true))
        .unwrap();
    assert_eq!(result(&outcome), (Status::Succeeded, None));
    assert_eq!(outcome.state.hdr(), None);
    assert_eq!(host.submissions(), 0);
    assert!(!rig.unresolved().hdr);
}

#[test]
fn uncertain_completion_persistence_latches_the_hdr_gate() {
    let rig = Rig::new(Memory::default());
    let mut host = admitted(0, false);
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
    let mut host = admitted(1, false);
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
    let mut host = admitted(1, false);
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

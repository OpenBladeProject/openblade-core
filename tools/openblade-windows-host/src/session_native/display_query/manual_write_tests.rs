//! The shared manual-operation frame over a trivial request with no device
//! behind it: the pause gate before the checkpoint, the checkpoint before any
//! attempt, power admission, reply reconciliation, staleness after success,
//! and certainty recorded before a fatal reply query returns. The HDR,
//! refresh and color tests cover only what their own attempts add.
use super::*;
use crate::session_native::display_query::{
    observation::fixture_query,
    recovery::Fields,
    test_support::{FencedTopology, Host, Memory, Rig, TestPanel, flag, result},
    write::{CHANGED_POWER, CHECKPOINT},
};
use openblade_platform_windows::display::InternalDisplayTopology;
use std::sync::atomic::Ordering;

const PAUSED: &str = "The trivial write is paused.";
const UNCONFIRMED: &str = "The trivial write was not confirmed.";
const FAILED: &str = "The trivial write failed.";
const REFUSED: &str = "The trivial write was refused.";

/// One scripted attempt and whether the reply query shows the request.
struct Probe {
    attempt: Option<Attempt>,
    attempts: usize,
    shown: bool,
}

/// The trivial request, written to the refresh field.
struct Trivial {
    expected_power_source: u8,
}

impl ManualRequest<Host<Probe>> for Trivial {
    const FIELD: Field = Field {
        fields: Fields::REFRESH,
        paused: PAUSED,
        unconfirmed: UNCONFIRMED,
    };

    fn expected_power_source(&self) -> u8 {
        self.expected_power_source
    }

    fn attempt(&self, host: &mut Host<Probe>, _: PowerContext, _: &AtomicBool) -> Attempt {
        let probe = host.native();
        probe.attempts += 1;
        probe.attempt.take().expect("one scripted attempt")
    }

    fn shown_by(&self, state: &Snapshot) -> bool {
        shows(state)
    }
}

impl TestPanel for Probe {
    type Value = ();
    type Request = Trivial;

    fn request(expected_power_source: u8, (): ()) -> Trivial {
        Trivial {
            expected_power_source,
        }
    }

    fn fenced(&self) -> FencedTopology {
        Box::new(|| Ok(InternalDisplayTopology::NoInternalDisplay))
    }

    /// The available fixture, with HDR standing for "shows the request".
    fn reply(&self) -> Snapshot {
        let mut value = serde_json::to_value(fixture_query("available")).unwrap();
        value["hdrEnabled"] = self.shown.into();
        serde_json::from_value(value).unwrap()
    }
}

/// Whether the reply shows the request: its fixture HDR flag, read through
/// the wire shape because the typed accessors are per-operation.
fn shows(state: &Snapshot) -> bool {
    serde_json::to_value(state).unwrap()["hdrEnabled"] == true
}

fn host(source: u8, attempt: Attempt, shown: bool) -> Host<Probe> {
    Host::admitted(
        source,
        Probe {
            attempt: Some(attempt),
            attempts: 0,
            shown,
        },
    )
}

fn failed(reply_resolves: bool) -> Attempt {
    Attempt::Failed {
        error: Cow::Borrowed(FAILED),
        reply_resolves,
    }
}

fn done(status: Status) -> Attempt {
    Attempt::Done(status, Cow::Borrowed(REFUSED))
}

#[test]
fn the_pause_gate_precedes_the_checkpoint_begin() {
    let rig = Rig::new(Memory::legacy(1));
    let mut first = host(1, Attempt::Confirmed, false);
    let outcome = rig.set(&mut first, 1, ()).unwrap();
    assert_eq!(result(&outcome), (Status::Indeterminate, Some(UNCONFIRMED)));
    let bytes = rig.file.bytes.lock().unwrap().clone();
    let writes = rig.file.writes.load(Ordering::SeqCst);

    // Across an Agent restart the gate refuses before begin can write.
    let rig = Rig::new(rig.file.clone());
    let mut paused = host(1, Attempt::Confirmed, true);
    let outcome = rig.set(&mut paused, 1, ()).unwrap();
    assert_eq!(result(&outcome), (Status::Blocked, Some(PAUSED)));
    assert!(shows(&outcome.state), "the reply is still queried");
    assert_eq!((paused.native().attempts, paused.queries), (0, 1));
    assert_eq!(rig.file.writes.load(Ordering::SeqCst), writes);
    assert_eq!(*rig.file.bytes.lock().unwrap(), bytes);

    // A successful explicit read reopens the field.
    rig.explicit_read(&outcome.state);
    let outcome = rig.set(&mut paused, 1, ()).unwrap();
    assert_eq!(result(&outcome), (Status::Succeeded, None));
    assert_eq!(paused.native().attempts, 1);
}

#[test]
fn a_checkpoint_failure_blocks_before_any_attempt() {
    let file = Memory::legacy(1);
    file.allow.store(0, Ordering::SeqCst);
    let rig = Rig::new(file);
    let mut refused = host(1, Attempt::Confirmed, true);
    let outcome = rig.set(&mut refused, 1, ()).unwrap();
    assert_eq!(result(&outcome), (Status::Blocked, Some(CHECKPOINT)));
    assert_eq!((refused.native().attempts, refused.queries), (0, 1));
    assert!(!rig.unresolved().refresh);

    // No prior checkpoint and no confirmed source: managed saves an
    // ExternalUnknown manual hold, then refuses the unconfirmed power.
    let rig = Rig::new(Memory::default());
    let mut unconfirmed = host(1, Attempt::Confirmed, true);
    unconfirmed.power = None;
    let outcome = rig.set(&mut unconfirmed, 1, ()).unwrap();
    assert_eq!(result(&outcome), (Status::Blocked, Some(UNCONFIRMED_POWER)));
    assert_eq!(unconfirmed.native().attempts, 0);
    assert_eq!(rig.file.record()["LastReconciledPowerSource"], 3);
}

#[test]
fn a_refused_request_still_returns_a_fatal_reply_query() {
    // A paused field, then a checkpoint that cannot be saved: neither request
    // is admitted, and neither hides the failed reply behind Blocked.
    let paused = Rig::new(Memory::default());
    let mut first = host(1, Attempt::Confirmed, false);
    let outcome = paused.set(&mut first, 1, ()).unwrap();
    assert_eq!(result(&outcome), (Status::Indeterminate, Some(UNCONFIRMED)));
    let file = Memory::legacy(1);
    file.allow.store(0, Ordering::SeqCst);
    for rig in [paused, Rig::new(file)] {
        let mut refused = host(1, Attempt::Confirmed, true);
        refused.fail_query = true;
        let error = rig
            .set(&mut refused, 1, ())
            .err()
            .expect("a fatal query stops the feature");
        assert_eq!(error.to_string(), "injected reply query failure");
        assert_eq!((refused.native().attempts, refused.queries), (0, 1));
    }
}

#[test]
fn power_admission_blocks_after_the_hold_without_an_attempt() {
    let rig = Rig::new(Memory::legacy(0));

    // Unconfirmed power: admitted under the durable hold, never attempted.
    let mut unconfirmed = host(0, Attempt::Confirmed, true);
    unconfirmed.power = None;
    let outcome = rig.set(&mut unconfirmed, 0, ()).unwrap();
    assert_eq!(result(&outcome), (Status::Blocked, Some(UNCONFIRMED_POWER)));
    assert_eq!((unconfirmed.native().attempts, unconfirmed.queries), (0, 1));

    // A power source other than the request's.
    let mut changed = host(1, Attempt::Confirmed, true);
    let outcome = rig.set(&mut changed, 2, ()).unwrap();
    assert_eq!(result(&outcome), (Status::Blocked, Some(CHANGED_POWER)));
    assert_eq!(changed.native().attempts, 0);

    // An ExternalUnknown request against a confirmed context changed source.
    let mut external = host(0, Attempt::Confirmed, true);
    let outcome = rig.set(&mut external, 3, ()).unwrap();
    assert_eq!(result(&outcome), (Status::Blocked, Some(CHANGED_POWER)));
    assert_eq!(external.native().attempts, 0);

    // A manual-external context is unconfirmed: it refuses a confirmed request
    // with the unconfirmed message, as managed ValidateExpectedAsync does.
    let mut manual = host(3, Attempt::Confirmed, true);
    let outcome = rig.set(&mut manual, 1, ()).unwrap();
    assert_eq!(result(&outcome), (Status::Blocked, Some(UNCONFIRMED_POWER)));
    assert_eq!(manual.native().attempts, 0);

    // Blocked completions restore the inherited (unpaused) state.
    let record = rig.file.record();
    assert!(!flag(&record, "AutomaticWritesPaused") && !flag(&record, "UncertainRefreshRate"));
    assert!(!rig.unresolved().refresh);
}

#[test]
fn the_reply_query_reconciles_each_attempt() {
    let cases = [
        // Nothing submitted: a reply that misses the request is not doubt.
        (Attempt::Unchanged, false, Status::Succeeded, None, false),
        (Attempt::Confirmed, true, Status::Succeeded, None, false),
        (
            Attempt::Confirmed,
            false,
            Status::Indeterminate,
            Some(UNCONFIRMED),
            true,
        ),
        // Managed ResolveHdrMutationResult: a reply showing the request wins.
        (failed(true), true, Status::Succeeded, None, false),
        (failed(true), false, Status::Failed, Some(FAILED), false),
        (failed(false), true, Status::Failed, Some(FAILED), false),
        (
            done(Status::Blocked),
            true,
            Status::Blocked,
            Some(REFUSED),
            false,
        ),
        (
            done(Status::Indeterminate),
            true,
            Status::Indeterminate,
            Some(REFUSED),
            true,
        ),
    ];
    for (index, (attempt, shown, status, error, uncertain)) in cases.into_iter().enumerate() {
        let rig = Rig::new(Memory::default());
        let mut host = host(1, attempt, shown);
        let outcome = rig.set(&mut host, 1, ()).unwrap();
        assert_eq!(result(&outcome), (status, error), "case {index}");
        assert_eq!(shows(&outcome.state), shown, "case {index}");
        assert_eq!(
            (host.native().attempts, host.queries),
            (1, 1),
            "case {index}"
        );
        assert_eq!(rig.unresolved().refresh, uncertain, "case {index}");
        assert_eq!(
            flag(&rig.file.record(), "UncertainRefreshRate"),
            uncertain,
            "case {index}"
        );
    }
}

#[test]
fn a_successful_setter_whose_power_changed_is_stale() {
    // The admitted context is source 1, generation 7.
    let drifted = |source, generation| Some(PowerContext { source, generation });
    // Managed ExecuteSetterAsync.EnsureCurrent, applied to a submitted change
    // and to an unchanged request alike, and to lost confirmation. Either
    // field drifting alone is stale.
    for (attempt, later) in [
        (Attempt::Confirmed, drifted(0, 8)),
        (Attempt::Confirmed, drifted(0, 7)),
        (Attempt::Confirmed, drifted(1, 8)),
        (Attempt::Unchanged, drifted(0, 8)),
        (Attempt::Confirmed, None),
    ] {
        let rig = Rig::new(Memory::default());
        let mut host = host(1, attempt, true);
        host.later = Some(later);
        let outcome = rig.set(&mut host, 1, ()).unwrap();
        assert_eq!(result(&outcome), (Status::Indeterminate, Some(STALE_AFTER)));
        assert_eq!(host.captures.get(), 2);
        assert!(rig.unresolved().refresh);
        assert!(flag(&rig.file.record(), "AutomaticWritesPaused"));
    }

    // Only a success is re-checked; a failure keeps its own result.
    let rig = Rig::new(Memory::default());
    let mut host = host(1, failed(false), true);
    host.later = Some(drifted(0, 8));
    let outcome = rig.set(&mut host, 1, ()).unwrap();
    assert_eq!(result(&outcome), (Status::Failed, Some(FAILED)));
    assert!(!rig.unresolved().refresh);
}

#[test]
fn a_manual_external_context_admits_an_external_unknown_request() {
    // Managed ManualExternalPowerPresent: the manual setter runs. A confirmed
    // source already on record stays the checkpoint's source.
    let rig = Rig::new(Memory::legacy(1));
    let mut admitted = host(3, Attempt::Confirmed, true);
    let outcome = rig.set(&mut admitted, 3, ()).unwrap();
    assert_eq!(result(&outcome), (Status::Succeeded, None));
    assert_eq!(admitted.native().attempts, 1);
    assert_eq!(rig.file.record()["LastReconciledPowerSource"], 1);

    // With no confirmed source, the hold records ExternalUnknown, as managed
    // `lastConfirmedPowerSource ?? ExternalUnknown` does.
    let rig = Rig::new(Memory::default());
    let mut first = host(3, Attempt::Confirmed, true);
    let outcome = rig.set(&mut first, 3, ()).unwrap();
    assert_eq!(result(&outcome), (Status::Succeeded, None));
    let record = rig.file.record();
    assert_eq!(record["LastReconciledPowerSource"], 3);
    assert!(flag(&record, "ManualHold"));

    // Managed EnsureCurrent: losing the evidence, or a confirmed source in a
    // new generation, makes the successful setter stale.
    let drifted = |source, generation| Some(PowerContext { source, generation });
    for later in [None, drifted(3, 8), drifted(0, 8)] {
        let rig = Rig::new(Memory::legacy(0));
        let mut host = host(3, Attempt::Confirmed, true);
        host.later = Some(later);
        let outcome = rig.set(&mut host, 3, ()).unwrap();
        assert_eq!(result(&outcome), (Status::Indeterminate, Some(STALE_AFTER)));
    }
}

#[test]
fn a_fatal_reply_query_records_certainty_before_returning() {
    for (attempt, gated) in [(Attempt::Confirmed, true), (Attempt::Unchanged, false)] {
        let rig = Rig::new(Memory::default());
        let mut host = host(1, attempt, true);
        host.fail_query = true;
        let error = rig
            .set(&mut host, 1, ())
            .err()
            .expect("a fatal query stops the feature");
        assert_eq!(error.to_string(), "injected reply query failure");
        assert_eq!(host.native().attempts, 1);
        // The unreadable reply keeps the managed durable pause either way;
        // only a possible submission closes the native write gate.
        let record = rig.file.record();
        assert!(flag(&record, "AutomaticWritesPaused") && flag(&record, "UncertainRefreshRate"));
        assert_eq!(rig.unresolved().refresh, gated);
    }
}

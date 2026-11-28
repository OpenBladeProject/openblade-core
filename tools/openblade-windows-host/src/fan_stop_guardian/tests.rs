use super::*;

const OP: OperationId = NonZeroU128::new(1).unwrap();
const FOREIGN: OperationId = NonZeroU128::new(2).unwrap();
const SERVICE: ProcessIdentity = ProcessIdentity {
    pid: 12,
    start_utc_ticks: 100,
};
const OWNER: ProcessIdentity = ProcessIdentity {
    pid: 34,
    start_utc_ticks: 200,
};

#[derive(Debug, PartialEq, Eq)]
enum Call {
    Acquire(OperationId),
    Reserve(Reservation),
    Dispatch(OperationId),
    ArchiveUnlaunched(Reservation),
    Auto(OperationId),
    Exit(ProcessIdentity),
    ReadResult(OperationId),
    ArchiveCompleted(Reservation),
    Release(OperationId),
}

struct Script {
    calls: Vec<Call>,
    acquire: bool,
    reservation: Reserve,
    launch: Launch,
    archive: bool,
    signal: AutoSignal,
    exit: Exit,
    durable: Result<Snapshot, EvidenceError>,
}

impl Default for Script {
    fn default() -> Self {
        Self {
            calls: vec![],
            acquire: true,
            reservation: Reserve::Durable,
            launch: Launch::Dispatched,
            archive: true,
            signal: AutoSignal::Latched,
            exit: Exit::Pending,
            durable: Err(EvidenceError::Lost),
        }
    }
}

impl Effects for Script {
    fn acquire_delegation(&mut self, op: OperationId) -> bool {
        self.calls.push(Call::Acquire(op));
        self.acquire
    }
    fn release_delegation(&mut self, op: OperationId) {
        self.calls.push(Call::Release(op));
    }
    fn reserve(&mut self, reservation: Reservation) -> Reserve {
        self.calls.push(Call::Reserve(reservation));
        self.reservation
    }
    fn dispatch(&mut self, op: OperationId) -> Launch {
        self.calls.push(Call::Dispatch(op));
        self.launch
    }
    fn archive_unlaunched(&mut self, reservation: Reservation) -> bool {
        self.calls.push(Call::ArchiveUnlaunched(reservation));
        self.archive
    }
    fn signal_auto(&mut self, op: OperationId) -> AutoSignal {
        self.calls.push(Call::Auto(op));
        self.signal.clone()
    }
    fn observe_exit(&mut self, owner: ProcessIdentity) -> Exit {
        self.calls.push(Call::Exit(owner));
        self.exit
    }
    fn read_owner_result(&mut self, op: OperationId) -> Result<Snapshot, EvidenceError> {
        self.calls.push(Call::ReadResult(op));
        self.durable.clone()
    }
    fn archive_completed(&mut self, reservation: Reservation) -> bool {
        self.calls.push(Call::ArchiveCompleted(reservation));
        self.archive
    }
}

fn reservation() -> Reservation {
    Reservation {
        operation: OP,
        service: SERVICE,
        settings: Settings::default(),
    }
}

fn starting() -> Snapshot {
    Snapshot {
        operation: OP,
        revision: 1,
        phase: Phase::Starting,
        confirmed_state: ObservedState::Unknown,
        stop_confirmed: false,
        auto_confirmed: false,
        cleanup_confirmed: false,
        journal_complete: false,
        native_released: false,
        owner_exited: false,
        command_pending: true,
        unresolved: false,
        reason: None,
        failure: None,
        observed_power: Some(Power::UsbC),
        end_reason: EndReason::None,
        known_not_submitted: false,
    }
}

fn stopped() -> Snapshot {
    Snapshot {
        revision: 3,
        phase: Phase::Stopped,
        confirmed_state: ObservedState::Stopped,
        stop_confirmed: true,
        command_pending: false,
        ..starting()
    }
}

fn completed() -> Snapshot {
    Snapshot {
        revision: 5,
        phase: Phase::Completed,
        confirmed_state: ObservedState::Automatic,
        auto_confirmed: true,
        cleanup_confirmed: true,
        journal_complete: true,
        native_released: true,
        end_reason: EndReason::CallerRequest,
        ..stopped()
    }
}

fn refused() -> Snapshot {
    Snapshot {
        revision: 2,
        phase: Phase::Refused,
        confirmed_state: ObservedState::Ordinary,
        native_released: true,
        command_pending: false,
        known_not_submitted: true,
        ..starting()
    }
}

fn begin(script: &mut Script) -> Replay {
    let mut replay = Replay::new(StartupDisposition::Clear);
    assert_eq!(
        replay.start(script, reservation(), Ok(()), true, false),
        Start::Dispatched
    );
    replay.authenticate_owner(OP, OWNER);
    replay
}

fn exit(script: &mut Script, durable: Snapshot) {
    script.exit = Exit::Observed {
        owner: OWNER,
        code: 0,
    };
    script.durable = Ok(durable);
}

fn assert_fence(replay: &Replay, script: &Script, reason: RestartReason) {
    assert_eq!(replay.disposition(), Disposition::RestartRequired(reason));
    assert!(replay.holds_delegation());
    assert!(!script.calls.contains(&Call::Release(OP)));
}

#[test]
fn lifecycle_reserves_first_and_requires_independent_exit_receipt_and_archive() {
    let mut script = Script::default();
    let mut replay = begin(&mut script);
    assert_eq!(
        script.calls,
        [
            Call::Acquire(OP),
            Call::Reserve(reservation()),
            Call::Dispatch(OP)
        ]
    );
    assert_eq!(replay.disposition(), Disposition::Delegated);
    assert!(replay.accepted().is_none());
    replay.observe(Ok(starting()));
    replay.observe(Ok(stopped()));
    assert_eq!(replay.accepted(), Some(&stopped()));
    assert_eq!(replay.accepted(), Some(&stopped())); // cached rereads are harmless
    assert_eq!(
        replay.continue_owned(Some(Continuation {
            operation: OP,
            can_stop: true,
            observed_power: Some(Power::UsbC),
        })),
        ContinuationAction::Heartbeat(Some(Power::UsbC))
    );
    assert_eq!(replay.request_auto(&mut script, false), Auto::Waiting);
    replay.observe(Ok(Snapshot {
        revision: 4,
        phase: Phase::ReturningToAutomatic,
        confirmed_state: ObservedState::Automatic,
        auto_confirmed: true,
        ..stopped()
    }));
    assert_eq!(replay.poll_handback(&mut script), None);
    assert_eq!(script.calls.last(), Some(&Call::Auto(OP)));
    replay.observe(Ok(completed()));
    assert_eq!(replay.poll_handback(&mut script), None);
    assert!(replay.holds_delegation());
    assert_eq!(replay.accepted().map(|s| s.owner_exited), Some(false));
    exit(&mut script, completed());
    assert_eq!(replay.poll_handback(&mut script), Some(Handback::Automatic));
    assert_eq!(replay.disposition(), Disposition::Available);
    assert_eq!(replay.poll_handback(&mut script), Some(Handback::Automatic));
    assert_eq!(replay.request_auto(&mut script, false), Auto::NoOwner);
    assert_eq!(
        script.calls,
        [
            Call::Acquire(OP),
            Call::Reserve(reservation()),
            Call::Dispatch(OP),
            Call::Auto(OP),
            Call::Exit(OWNER),
            Call::Exit(OWNER),
            Call::ReadResult(OP),
            Call::ArchiveCompleted(reservation()),
            Call::Release(OP)
        ]
    );
}

#[test]
fn autonomous_completion_preserves_only_observed_stop_history() {
    for stop_confirmed in [false, true] {
        let mut script = Script::default();
        let mut replay = begin(&mut script);
        let terminal = Snapshot {
            stop_confirmed,
            ..completed()
        };
        exit(&mut script, terminal.clone());
        replay.observe(Ok(terminal));
        assert_eq!(replay.poll_handback(&mut script), Some(Handback::Automatic));
        assert_eq!(replay.accepted().unwrap().stop_confirmed, stop_confirmed);
        assert!(!script.calls.contains(&Call::Auto(OP)));
    }
}

#[test]
fn never_submitted_refusal_requires_owner_exit_and_is_not_automatic() {
    let mut script = Script::default();
    let mut replay = begin(&mut script);
    replay.observe(Ok(refused()));
    assert_eq!(replay.poll_handback(&mut script), None);
    assert!(replay.holds_delegation());
    exit(&mut script, refused());
    assert_eq!(
        replay.poll_handback(&mut script),
        Some(Handback::NeverSubmitted)
    );
    let accepted = replay.accepted().unwrap();
    assert!(!accepted.stop_confirmed && !accepted.auto_confirmed);
    assert_eq!(accepted.confirmed_state, ObservedState::Ordinary);
    assert!(!script.calls.contains(&Call::Auto(OP)));
}

#[test]
fn malformed_foreign_stale_or_regressing_owner_evidence_is_sticky() {
    for bad in [
        Err(EvidenceError::Malformed),
        Ok(Snapshot {
            operation: FOREIGN,
            ..completed()
        }),
        Ok(Snapshot {
            revision: -1,
            ..completed()
        }),
        Ok(Snapshot {
            revision: 2,
            ..completed()
        }),
        Ok(Snapshot {
            revision: 3,
            reason: Some("changed".into()),
            ..stopped()
        }),
        Ok(stopped()), // a duplicate live frame is not a cache read
        Ok(Snapshot {
            stop_confirmed: false,
            ..completed()
        }),
        Ok(Snapshot {
            owner_exited: true,
            ..completed()
        }),
        Ok(Snapshot {
            phase: Phase::Idle,
            ..completed()
        }),
        Ok(Snapshot {
            reason: Some("💧".repeat(513)),
            ..completed()
        }),
    ] {
        let mut script = Script::default();
        let mut replay = begin(&mut script);
        replay.observe(Ok(stopped()));
        replay.observe(bad);
        exit(&mut script, completed());
        replay.observe(Ok(completed()));
        assert_eq!(replay.poll_handback(&mut script), None);
        assert_eq!(replay.request_auto(&mut script, false), Auto::Fenced);
        assert_fence(&replay, &script, RestartReason::InvalidEvidence);
        assert_eq!(
            script.calls,
            [
                Call::Acquire(OP),
                Call::Reserve(reservation()),
                Call::Dispatch(OP)
            ]
        );
    }
}

#[test]
fn foreign_unowned_or_unauthenticated_observation_cannot_be_adopted() {
    let mut script = Script::default();
    let mut idle = Replay::new(StartupDisposition::Clear);
    idle.observe(Ok(stopped()));
    assert_eq!(
        idle.start(&mut script, reservation(), Ok(()), true, false),
        Start::Fenced
    );
    assert!(script.calls.is_empty());
    let mut replay = Replay::new(StartupDisposition::Clear);
    assert_eq!(
        replay.start(&mut script, reservation(), Ok(()), true, false),
        Start::Dispatched
    );
    replay.authenticate_owner(FOREIGN, OWNER);
    replay.observe(Ok(completed()));
    assert_fence(&replay, &script, RestartReason::OwnerIdentity);
}

#[test]
fn auto_is_latched_once_and_canceled_wait_does_not_prevent_late_handback() {
    let mut script = Script::default();
    let mut replay = begin(&mut script);
    assert_eq!(replay.request_auto(&mut script, true), Auto::Canceled);
    assert!(!script.calls.contains(&Call::Auto(OP)));
    assert_eq!(replay.cancel_entry_wait(&mut script), Auto::Waiting);
    assert_eq!(replay.request_auto(&mut script, true), Auto::Canceled);
    assert_eq!(replay.request_auto(&mut script, false), Auto::Waiting);
    assert!(replay.holds_delegation());
    exit(&mut script, completed());
    replay.observe(Ok(completed()));
    assert_eq!(replay.poll_handback(&mut script), Some(Handback::Automatic));
    assert_eq!(
        script
            .calls
            .iter()
            .filter(|call| **call == Call::Auto(OP))
            .count(),
        1
    );
}

#[test]
fn continuation_loss_signals_auto_without_new_entry_or_reuse_of_old_evidence() {
    let mut script = Script::default();
    let mut replay = begin(&mut script);
    replay.observe(Ok(stopped()));
    for evidence in [
        Some(Continuation {
            operation: FOREIGN,
            can_stop: true,
            observed_power: Some(Power::UsbC),
        }),
        Some(Continuation {
            operation: OP,
            can_stop: false,
            observed_power: None,
        }),
        Some(Continuation {
            operation: OP,
            can_stop: true,
            observed_power: Some(Power::UsbC),
        }),
        None,
    ] {
        assert_eq!(
            replay.continue_owned(evidence),
            ContinuationAction::Automatic
        );
    }
    assert_eq!(
        script.calls,
        [
            Call::Acquire(OP),
            Call::Reserve(reservation()),
            Call::Dispatch(OP)
        ]
    );
}

#[test]
fn ambiguous_auto_or_lost_channel_never_replays_or_adopts_late_evidence() {
    for reason in [RestartReason::AutoSubmission, RestartReason::LostEvidence] {
        let mut script = Script {
            signal: AutoSignal::Uncertain,
            ..Script::default()
        };
        let mut replay = begin(&mut script);
        replay.observe(Ok(stopped()));
        if reason == RestartReason::AutoSubmission {
            assert_eq!(replay.request_auto(&mut script, false), Auto::Fenced);
        } else {
            replay.observe(Err(EvidenceError::Lost));
        }
        exit(&mut script, completed());
        replay.observe(Ok(completed()));
        assert_eq!(replay.poll_handback(&mut script), None);
        assert_eq!(replay.request_auto(&mut script, false), Auto::Fenced);
        assert_eq!(
            replay.start(&mut script, reservation(), Ok(()), true, false),
            Start::Fenced
        );
        assert_fence(&replay, &script, reason);
        assert_eq!(
            script
                .calls
                .iter()
                .filter(|call| **call == Call::Auto(OP))
                .count(),
            usize::from(reason == RestartReason::AutoSubmission)
        );
    }
}

#[test]
fn autonomous_completion_racing_auto_refusal_still_needs_terminal_proof() {
    for verified in [false, true] {
        let mut script = Script {
            signal: AutoSignal::NotSubmitted(Some(completed())),
            ..Script::default()
        };
        let mut replay = begin(&mut script);
        if verified {
            exit(&mut script, completed());
        }
        if verified {
            assert_eq!(
                replay.request_auto(&mut script, false),
                Auto::Finished(Handback::Automatic)
            );
        } else {
            assert_eq!(replay.request_auto(&mut script, false), Auto::Fenced);
            assert_fence(&replay, &script, RestartReason::AutoSubmission);
        }
        assert_eq!(
            script
                .calls
                .iter()
                .filter(|call| **call == Call::Auto(OP))
                .count(),
            1
        );
    }
}

#[test]
fn partial_terminal_receipt_cannot_substitute_one_proof_for_another() {
    for partial in [
        Snapshot {
            confirmed_state: ObservedState::Stopped,
            ..completed()
        },
        Snapshot {
            auto_confirmed: false,
            ..completed()
        },
        Snapshot {
            cleanup_confirmed: false,
            ..completed()
        },
        Snapshot {
            journal_complete: false,
            ..completed()
        },
        Snapshot {
            native_released: false,
            ..completed()
        },
        Snapshot {
            command_pending: true,
            ..completed()
        },
        Snapshot {
            known_not_submitted: false,
            ..refused()
        },
        Snapshot {
            stop_confirmed: true,
            ..refused()
        },
        Snapshot {
            confirmed_state: ObservedState::Automatic,
            ..refused()
        },
    ] {
        let mut script = Script::default();
        let mut replay = begin(&mut script);
        exit(&mut script, partial.clone());
        replay.observe(Ok(partial));
        assert_eq!(replay.poll_handback(&mut script), None);
        assert_fence(&replay, &script, RestartReason::IncompleteHandback);
        assert!(
            !script
                .calls
                .contains(&Call::ArchiveCompleted(reservation()))
        );
    }
}

#[test]
fn terminal_also_needs_matching_retained_process_matching_durable_result_and_archive() {
    for (owner_exit, durable, archive, reason) in [
        (Exit::Lost, Ok(completed()), true, RestartReason::Exit),
        (
            Exit::Observed {
                owner: ProcessIdentity {
                    start_utc_ticks: 201,
                    ..OWNER
                },
                code: 0,
            },
            Ok(completed()),
            true,
            RestartReason::Exit,
        ),
        (
            Exit::Observed {
                owner: OWNER,
                code: 1,
            },
            Ok(completed()),
            true,
            RestartReason::Exit,
        ),
        (
            Exit::Observed {
                owner: OWNER,
                code: 0,
            },
            Err(EvidenceError::Lost),
            true,
            RestartReason::DurableResult,
        ),
        (
            Exit::Observed {
                owner: OWNER,
                code: 0,
            },
            Ok(Snapshot {
                reason: Some("different".into()),
                ..completed()
            }),
            true,
            RestartReason::DurableResult,
        ),
        (
            Exit::Observed {
                owner: OWNER,
                code: 0,
            },
            Ok(Snapshot {
                operation: FOREIGN,
                ..completed()
            }),
            true,
            RestartReason::DurableResult,
        ),
        (
            Exit::Observed {
                owner: OWNER,
                code: 0,
            },
            Ok(completed()),
            false,
            RestartReason::Archive,
        ),
    ] {
        let mut script = Script {
            exit: owner_exit,
            durable,
            archive,
            ..Script::default()
        };
        let mut replay = begin(&mut script);
        replay.observe(Ok(completed()));
        assert_eq!(replay.poll_handback(&mut script), None);
        assert_fence(&replay, &script, reason);
        let effect_count = script.calls.len();
        assert_eq!(replay.poll_handback(&mut script), None);
        assert_eq!(script.calls.len(), effect_count);
    }
}

#[test]
fn incomplete_dispatch_only_releases_on_proven_absence_or_archived_never_launch() {
    for (reserve, launch, archive, reason) in [
        (Reserve::ProvenAbsent, Launch::Uncertain, false, None),
        (Reserve::Durable, Launch::ProvenNotLaunched, true, None),
        (
            Reserve::Durable,
            Launch::ProvenNotLaunched,
            false,
            Some(RestartReason::Archive),
        ),
        (
            Reserve::Uncertain,
            Launch::Dispatched,
            true,
            Some(RestartReason::Reservation),
        ),
        (
            Reserve::Durable,
            Launch::Uncertain,
            true,
            Some(RestartReason::Launch),
        ),
    ] {
        let mut script = Script {
            reservation: reserve,
            launch,
            archive,
            ..Script::default()
        };
        let mut replay = Replay::new(StartupDisposition::Clear);
        let start = replay.start(&mut script, reservation(), Ok(()), true, false);
        assert_eq!(
            script.calls.contains(&Call::Dispatch(OP)),
            reserve == Reserve::Durable
        );
        assert_eq!(
            script
                .calls
                .contains(&Call::ArchiveUnlaunched(reservation())),
            reserve == Reserve::Durable && launch == Launch::ProvenNotLaunched
        );
        if let Some(reason) = reason {
            assert_eq!(start, Start::Fenced);
            replay.authenticate_owner(OP, OWNER);
            exit(&mut script, completed());
            replay.observe(Ok(completed()));
            assert_eq!(replay.poll_handback(&mut script), None);
            assert_fence(&replay, &script, reason);
        } else {
            assert_eq!(start, Start::NotSubmitted);
            assert_eq!(replay.disposition(), Disposition::Available);
            assert_eq!(script.calls.last(), Some(&Call::Release(OP)));
        }
    }
}

#[test]
fn restart_never_adopts_an_old_reservation_and_late_proof_does_not_clear_service_timeout() {
    for startup in [StartupDisposition::Unresolved, StartupDisposition::Unknown] {
        let mut script = Script::default();
        let mut replay = Replay::new(startup);
        assert_eq!(
            replay.disposition(),
            Disposition::RestartRequired(RestartReason::Startup(startup))
        );
        assert_eq!(
            replay.start(&mut script, reservation(), Ok(()), true, false),
            Start::Fenced
        );
        assert_eq!(replay.request_auto(&mut script, false), Auto::Fenced);
        assert!(script.calls.is_empty());
    }

    let mut script = Script::default();
    let mut replay = begin(&mut script);
    replay.expire_stop_confirmation();
    assert_eq!(replay.continue_owned(None), ContinuationAction::Automatic);
    assert_fence(&replay, &script, RestartReason::ConfirmationExpired);
    assert_eq!(replay.request_auto(&mut script, false), Auto::Fenced);
    exit(&mut script, completed());
    replay.observe(Ok(completed()));
    assert_eq!(replay.poll_handback(&mut script), Some(Handback::Automatic));
    assert!(!replay.holds_delegation());
    assert_eq!(
        replay.disposition(),
        Disposition::RestartRequired(RestartReason::ConfirmationExpired)
    );
}

#[test]
fn canceled_or_unavailable_entry_has_no_reservation_and_active_run_blocks_new_entry() {
    let mut script = Script::default();
    let mut replay = Replay::new(StartupDisposition::Clear);
    assert_eq!(
        replay.start(&mut script, reservation(), Ok(()), true, true),
        Start::Canceled
    );
    assert_eq!(
        replay.start(&mut script, reservation(), Ok(()), false, false),
        Start::NotSubmitted
    );
    assert!(script.calls.is_empty());
    script.acquire = false;
    assert_eq!(
        replay.start(&mut script, reservation(), Ok(()), true, false),
        Start::NotSubmitted
    );
    assert_eq!(script.calls, [Call::Acquire(OP)]);
    script.acquire = true;
    assert_eq!(
        replay.start(&mut script, reservation(), Ok(()), true, false),
        Start::Dispatched
    );
    let replacement = Reservation {
        operation: FOREIGN,
        ..reservation()
    };
    assert_eq!(
        replay.start(&mut script, replacement, Ok(()), true, false),
        Start::Fenced
    );
    assert!(!script.calls.contains(&Call::Acquire(FOREIGN)));
}

#[test]
fn new_run_cannot_heartbeat_before_authentication_or_use_a_prior_terminal_result() {
    let mut script = Script::default();
    let mut replay = begin(&mut script);
    exit(&mut script, completed());
    replay.observe(Ok(completed()));
    assert_eq!(replay.poll_handback(&mut script), Some(Handback::Automatic));
    let next = Reservation {
        operation: FOREIGN,
        ..reservation()
    };
    assert_eq!(
        replay.start(&mut script, next, Ok(()), true, false),
        Start::Dispatched
    );
    assert!(replay.accepted().is_none());
    assert_eq!(
        replay.continue_owned(None),
        ContinuationAction::AwaitingOwner
    );
    assert_eq!(replay.poll_handback(&mut script), None);
    replay.authenticate_owner(FOREIGN, OWNER);
    replay.observe(Ok(completed()));
    assert_eq!(
        replay.disposition(),
        Disposition::RestartRequired(RestartReason::InvalidEvidence)
    );
    assert!(replay.holds_delegation());
    assert!(!script.calls.contains(&Call::Release(FOREIGN)));
}

#[test]
fn entry_refusal_and_invalid_settings_never_reach_delegation() {
    let mut replay = Replay::new(StartupDisposition::Clear);
    let mut script = Script::default();
    assert_eq!(
        replay.start(
            &mut script,
            reservation(),
            Err(Refusal::InitialPower),
            true,
            false
        ),
        Start::EntryRefused(Refusal::InitialPower)
    );
    let invalid = Reservation {
        settings: Settings {
            cpu_cutoff: 54,
            ..Settings::default()
        },
        ..reservation()
    };
    assert_eq!(
        replay.start(&mut script, invalid, Ok(()), true, false),
        Start::NotSubmitted
    );
    assert!(script.calls.is_empty());
}

#[test]
fn owner_failure_cannot_be_replaced_by_a_later_clean_snapshot() {
    for broken in [
        Snapshot {
            failure: Some("cleanup failed".into()),
            ..completed()
        },
        Snapshot {
            unresolved: true,
            ..completed()
        },
        Snapshot {
            phase: Phase::RecoveryRequired,
            ..completed()
        },
    ] {
        let mut script = Script::default();
        let mut replay = begin(&mut script);
        replay.observe(Ok(broken));
        exit(&mut script, completed());
        replay.observe(Ok(completed()));
        assert_eq!(replay.poll_handback(&mut script), None);
        assert_eq!(replay.request_auto(&mut script, false), Auto::Fenced);
        assert_fence(&replay, &script, RestartReason::OwnerUnresolved);
    }
}

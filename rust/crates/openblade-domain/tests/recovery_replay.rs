use openblade_domain::recovery::*;
use serde_json::Value;

const FIXTURE: &str =
    include_str!("../../../../tests/OpenBlade.Service.Tests/WindowsReplay/v1/service-runtime.json");
const REVISION: JournalRevision = [0x21; 32];
const REPLACEMENT: JournalRevision = [0x42; 32];
const IDENTITY: &str = "synthetic-device";
const FUNCTION_KEYS: LaptopValue = LaptopValue::FunctionKeyPrimary(FunctionKeyMode::FunctionKeys);

fn laptop_journal(revision: JournalRevision, entries: &[PendingEntry]) -> LaptopJournal<'_> {
    LaptopJournal::Valid {
        identity: IDENTITY,
        revision,
        entries,
    }
}

#[test]
fn laptop_recovery_matches_managed_executor() {
    let fixture: Value = serde_json::from_str(FIXTURE).unwrap();
    for case in fixture["laptopRecovery"].as_array().unwrap() {
        let id = case["id"].as_str().unwrap();
        let input = &case["input"];
        let expected = &case["expected"];
        let requested = match input["requested"].as_str().unwrap() {
            "FunctionKeys" => FUNCTION_KEYS,
            "Microphone" => LaptopValue::MicrophoneMuteIndicator(true),
            other => panic!("{id}: unexpected request {other}"),
        };
        let readback = LaptopReadback {
            function_key_primary: input["readback"].as_str().map(|mode| match mode {
                "FunctionKeys" => FunctionKeyMode::FunctionKeys,
                "MultimediaKeys" => FunctionKeyMode::MultimediaKeys,
                other => panic!("{id}: unexpected readback {other}"),
            }),
            ..LaptopReadback::default()
        };
        let entries = [PendingEntry {
            requested,
            reconciliation_required: true,
        }];
        let action = input["action"].as_str().unwrap();
        let settlement = match action {
            "Restart" => match plan_laptop_recovery(
                laptop_journal(REVISION, &entries),
                input["identity"].as_str(),
                None,
            ) {
                LaptopRecoveryPlan::ReadOnly(plan) => plan.finish(readback),
                LaptopRecoveryPlan::Pause(_) => None,
                other => panic!("{id}: unexpected plan {other:?}"),
            },
            "Acknowledged" => {
                complete_laptop_intent(REVISION, requested, LiveCompletion::Acknowledged(readback))
                    .ok()
            }
            "CanceledDuring" | "Indeterminate" => {
                assert_eq!(
                    complete_laptop_intent(REVISION, requested, LiveCompletion::MayHaveSubmitted),
                    Err(PauseReason::PossibleSubmission),
                    "{id}"
                );
                None
            }
            "CanceledBefore" => {
                let loaded = match input["cleanupRevision"].as_str().unwrap() {
                    "Intent" => REVISION,
                    "Replacement" => REPLACEMENT,
                    other => panic!("{id}: unexpected cleanup revision {other}"),
                };
                let plan = plan_laptop_recovery(
                    laptop_journal(loaded, &entries),
                    Some(IDENTITY),
                    Some(REVISION),
                );
                let journal = match plan {
                    LaptopRecoveryPlan::StorageOnly(
                        NonSubmissionCleanup::RestorePriorDocument { expected_revision },
                    ) => {
                        assert_eq!(expected_revision, loaded, "{id}");
                        "Removed"
                    }
                    LaptopRecoveryPlan::StorageOnly(NonSubmissionCleanup::ForgetProof) => {
                        "Replacement"
                    }
                    other => panic!("{id}: unexpected plan {other:?}"),
                };
                assert_eq!(expected["journal"], journal, "{id}");
                continue;
            }
            other => panic!("{id}: unexpected action {other}"),
        };
        let journal = match settlement {
            Some(batch) => {
                assert_eq!(batch.expected_revision, REVISION, "{id}");
                assert_eq!(batch.changes[1..], [None; 3], "{id}");
                let change = batch.changes[0].unwrap();
                assert_eq!(change.requested, requested, "{id}");
                assert_eq!(
                    change.observed,
                    readback
                        .function_key_primary
                        .map(LaptopValue::FunctionKeyPrimary),
                    "{id}"
                );
                match change.change {
                    EntryChange::Remove => "Removed",
                    EntryChange::KeepTombstone => "Tombstone",
                }
            }
            None => "Pending",
        };
        assert_eq!(expected["journal"], journal, "{id}");
    }
}

#[test]
fn cleanup_has_precedence_even_if_no_record_can_be_read() {
    assert_eq!(
        plan_laptop_recovery(LaptopJournal::Paused, None, None),
        LaptopRecoveryPlan::Pause(PauseReason::JournalUnavailable)
    );
    assert_eq!(
        plan_laptop_recovery(LaptopJournal::Paused, None, Some(REVISION)),
        LaptopRecoveryPlan::StorageOnly(NonSubmissionCleanup::KeepProof)
    );
    assert_eq!(
        plan_laptop_recovery(LaptopJournal::Missing, None, Some(REVISION)),
        LaptopRecoveryPlan::StorageOnly(NonSubmissionCleanup::ForgetProof)
    );
}

#[test]
fn one_revision_batches_known_readbacks_and_leaves_unknown_entries_pending() {
    let requests = [
        LaptopValue::StartupAnimation(true),
        FUNCTION_KEYS,
        LaptopValue::MicrophoneMuteIndicator(true),
        LaptopValue::GamingMode(true),
    ];
    let entries = requests.map(|requested| PendingEntry {
        requested,
        reconciliation_required: true,
    });
    let LaptopRecoveryPlan::ReadOnly(plan) =
        plan_laptop_recovery(laptop_journal(REVISION, &entries), Some(IDENTITY), None)
    else {
        panic!("expected a read-only plan");
    };
    assert!(plan.getters().eq([
        LaptopGetter::StartupAnimation,
        LaptopGetter::FunctionKeyPrimary,
        LaptopGetter::GamingMode,
    ]));
    let batch = plan
        .finish(LaptopReadback {
            startup_animation: Some(true),
            function_key_primary: None,
            gaming_mode: Some(false),
        })
        .unwrap();
    assert_eq!(batch.expected_revision, REVISION);
    assert_eq!(
        batch.changes,
        [
            Some(EntrySettlement {
                requested: requests[0],
                observed: Some(requests[0]),
                change: EntryChange::Remove
            }),
            Some(EntrySettlement {
                requested: requests[3],
                observed: Some(LaptopValue::GamingMode(false)),
                change: EntryChange::KeepTombstone
            }),
            None,
            None,
        ]
    );
}

#[test]
fn pending_or_foreign_journal_cannot_pass_write_admission() {
    let entry = PendingEntry {
        requested: FUNCTION_KEYS,
        reconciliation_required: true,
    };
    let other = LaptopValue::GamingMode(true);
    let pending = [entry];
    let journal = laptop_journal(REVISION, &pending);
    assert_eq!(
        check_laptop_journal_for_write(journal, Some(IDENTITY), FUNCTION_KEYS, false),
        Err(JournalWriteBlock::ReconciliationRequired)
    );
    assert_eq!(
        check_laptop_journal_for_write(journal, Some(IDENTITY), other, true),
        Err(JournalWriteBlock::ReconciliationRequired)
    );
    let settled = [PendingEntry {
        reconciliation_required: false,
        ..entry
    }];
    let journal = laptop_journal(REVISION, &settled);
    assert_eq!(
        check_laptop_journal_for_write(journal, Some("another-device"), other, true),
        Err(JournalWriteBlock::ExactIdentityUnavailable)
    );
    assert_eq!(
        check_laptop_journal_for_write(journal, Some(IDENTITY), FUNCTION_KEYS, false),
        Err(JournalWriteBlock::AutomaticReplay)
    );
    assert_eq!(
        check_laptop_journal_for_write(journal, Some(IDENTITY), FUNCTION_KEYS, true),
        Ok(())
    );
    assert_eq!(
        check_laptop_journal_for_write(journal, Some(IDENTITY), other, false),
        Ok(())
    );
}

#[test]
fn thermal_completion_matches_managed_scoped_trace() {
    let fixture: Value = serde_json::from_str(FIXTURE).unwrap();
    for case in fixture["thermal"].as_array().unwrap() {
        let id = case["id"].as_str().unwrap();
        let input = &case["input"];
        let expected = &case["expected"];
        let status = match input["result"].as_str().unwrap() {
            "Applied" => ScopedStatus::Applied,
            "Unchanged" => ScopedStatus::Unchanged,
            "Blocked" => ScopedStatus::Blocked,
            "Failed" => ScopedStatus::Failed,
            other => panic!("unexpected result {other}"),
        };
        let certainty = input["submissionCertainty"]
            .as_str()
            .map(|value| match value {
                "Indeterminate" => SubmissionCertainty::Indeterminate,
                "KnownNotApplied" => SubmissionCertainty::KnownNotApplied,
                other => panic!("unexpected certainty {other}"),
            });
        let result = classify_scoped_thermal_outcome(ScopedThermalOutcome {
            status,
            certainty,
            write_attempted: input["writeAttempted"].as_bool().unwrap(),
            has_readback: !input["readback"].is_null(),
            readback_matched: input["readbackMatched"].as_bool().unwrap(),
            confirmation_unavailable: input["confirmationUnavailable"].as_bool().unwrap(),
        });
        assert_eq!(
            expected["progress"],
            format!("{:?}", result.progress),
            "{id}"
        );
        assert_eq!(expected["stage"], format!("{:?}", result.stage), "{id}");
        assert_eq!(
            expected["disposition"],
            format!("{:?}", result.disposition),
            "{id}"
        );
        assert_eq!(
            expected["certainty"].as_str(),
            result
                .certainty
                .map(|certainty| format!("{certainty:?}"))
                .as_deref(),
            "{id}"
        );
    }
}

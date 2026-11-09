use openblade_domain::recovery::*;
use serde_json::Value;

const FIXTURE: &str =
    include_str!("../../../../tests/OpenBlade.Service.Tests/WindowsReplay/v1/service-runtime.json");
const REVISION: JournalRevision = [0x21; 32];
const IDENTITY: &str = "synthetic-device";

fn laptop_value(value: &Value) -> Option<LaptopValue> {
    Some(match value.as_str()? {
        "FunctionKeys" => LaptopValue::FunctionKeyPrimary(FunctionKeyMode::FunctionKeys),
        "MultimediaKeys" => LaptopValue::FunctionKeyPrimary(FunctionKeyMode::MultimediaKeys),
        "Microphone" => LaptopValue::MicrophoneMuteIndicator(true),
        other => panic!("unexpected laptop value {other}"),
    })
}

#[test]
fn laptop_recovery_matches_managed_executor() {
    let fixture: Value = serde_json::from_str(FIXTURE).unwrap();
    for case in fixture["laptopRecovery"].as_array().unwrap() {
        let id = case["id"].as_str().unwrap();
        let input = &case["input"];
        let expected = &case["expected"];
        let requested = laptop_value(&input["requested"]).unwrap();
        let readback = laptop_value(&input["readback"]);
        let observation = readback.map_or(LaptopObservation::Unavailable, LaptopObservation::Read);
        let action = input["action"].as_str().unwrap();
        let result = match action {
            "Restart" => recover_laptop_entry(
                JournalEntry::Valid {
                    identity: IDENTITY,
                    revision: REVISION,
                    entry: PendingEntry {
                        requested,
                        reconciliation_required: true,
                    },
                },
                input["identity"].as_str(),
                observation,
            ),
            "Acknowledged" => complete_laptop_intent(
                REVISION,
                requested,
                LiveCompletion::Acknowledged(observation),
            ),
            "CanceledDuring" | "Indeterminate" => {
                complete_laptop_intent(REVISION, requested, LiveCompletion::MayHaveSubmitted)
            }
            "CanceledBefore" => {
                // The managed test cancels after write-ahead persistence and
                // verifies that storage-only cleanup restores the prior document.
                assert_eq!(expected["journal"], "Removed", "{id}");
                assert_eq!(
                    clean_up_known_non_submission(
                        REVISION,
                        JournalLoadRevision::Readable(Some(REVISION))
                    ),
                    NonSubmissionCleanup::RestorePriorDocument {
                        expected_revision: REVISION
                    },
                    "{id}"
                );
                continue;
            }
            other => panic!("unexpected action {other}"),
        };
        let journal = match result {
            LaptopRecovery::Settle {
                expected_revision,
                requested: saved_request,
                observed,
                change,
            } => {
                assert_eq!(expected_revision, REVISION, "{id}");
                assert_eq!(saved_request, requested, "{id}");
                assert_eq!(observed, readback, "{id}");
                match change {
                    EntryChange::Remove => "Removed",
                    EntryChange::KeepTombstone => "Tombstone",
                }
            }
            LaptopRecovery::Pause(_) => "Pending",
            other => panic!("{id}: unexpected decision {other:?}"),
        };
        assert_eq!(expected["journal"], journal, "{id}");
    }
}

#[test]
fn recovery_preserves_paused_records_and_ignores_replaced_intents() {
    let read = LaptopObservation::NotRead;
    assert_eq!(
        recover_laptop_entry(JournalEntry::Paused, Some(IDENTITY), read),
        LaptopRecovery::Pause(PauseReason::JournalUnavailable)
    );
    assert_eq!(
        recover_laptop_entry(JournalEntry::Missing, Some(IDENTITY), read),
        LaptopRecovery::NoPendingEntry
    );
    assert_eq!(
        clean_up_known_non_submission(REVISION, JournalLoadRevision::Paused),
        NonSubmissionCleanup::KeepProof
    );
    for replacement in [None, Some([0x42; 32])] {
        assert_eq!(
            clean_up_known_non_submission(REVISION, JournalLoadRevision::Readable(replacement)),
            NonSubmissionCleanup::ForgetProof
        );
    }
}

#[test]
fn read_only_plan_and_tombstones_are_control_specific() {
    let requested = LaptopValue::GamingMode(true);
    let mut entry = PendingEntry {
        requested,
        reconciliation_required: true,
    };
    let journal = JournalEntry::Valid {
        identity: IDENTITY,
        revision: REVISION,
        entry,
    };
    assert_eq!(
        recover_laptop_entry(journal, Some(IDENTITY), LaptopObservation::NotRead),
        LaptopRecovery::ReadOnly(LaptopGetter::GamingMode)
    );
    assert_eq!(
        recover_laptop_entry(
            journal,
            Some(IDENTITY),
            LaptopObservation::Read(LaptopValue::StartupAnimation(true))
        ),
        LaptopRecovery::Pause(PauseReason::ReadbackUnavailable)
    );
    entry.reconciliation_required = false;
    assert!(tombstone_blocks_automatic(entry, requested));
    assert!(!tombstone_blocks_automatic(
        entry,
        LaptopValue::GamingMode(false)
    ));
    assert!(!tombstone_blocks_automatic(
        entry,
        LaptopValue::StartupAnimation(true)
    ));
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
        let result = classify_scoped_thermal(ScopedThermalOutcome {
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

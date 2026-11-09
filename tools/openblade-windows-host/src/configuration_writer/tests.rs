use super::*;
use crate::configuration::Reason;
use serde::Deserialize;
use serde_json::Value;
use sha2::{Digest, Sha256};
use std::panic::{AssertUnwindSafe, catch_unwind};
use windows_sys::Win32::Storage::FileSystem::FILE_SHARE_DELETE;

const REPLAY: &str = include_str!(
    "../../../../tests/OpenBlade.Service.Tests/WindowsReplay/v1/configuration-aggregate.json"
);

#[derive(Deserialize)]
struct Replay {
    legacy: String,
    prior: [String; 3],
    committed: [String; 3],
    recovery: Value,
}

fn replay() -> Replay {
    serde_json::from_str(REPLAY).unwrap()
}

fn bytes(documents: &[String; 3]) -> [&[u8]; 3] {
    documents.each_ref().map(|text| text.as_bytes())
}

fn injected() -> io::Error {
    io::Error::other("injected boundary failure")
}

fn assert_committed(report: Report) {
    assert!(matches!(report.outcome, Outcome::Committed), "{report:?}");
    assert!(report.artifact_errors.is_empty(), "{report:?}");
}

fn assert_restored(report: Report) {
    assert!(report.artifact_errors.is_empty(), "{report:?}");
    let Outcome::Restored(error) = report.outcome else {
        panic!("{report:?}");
    };
    assert_eq!(error.to_string(), injected().to_string());
}

fn assert_unchanged(report: Report) -> io::Error {
    let Outcome::Unchanged(error) = report.outcome else {
        panic!("{report:?}");
    };
    error
}

fn assert_preserved(report: Report, failed_rollback: bool) {
    let Outcome::RecoveryRequired { cause, resolution } = report.outcome else {
        panic!("{report:?}");
    };
    assert!(!cause.to_string().is_empty());
    assert_eq!(resolution.is_some(), failed_rollback, "{resolution:?}");
}

fn assert_primaries(store: &TestStore, expected: [Option<&[u8]>; 3]) {
    let actual = store.capture().unwrap();
    assert_eq!(actual.each_ref().map(Option::as_deref), expected);
}

fn assert_only_primaries_and_lock(store: &TestStore, populated: bool) {
    let expected = if populated { 4 } else { 1 };
    assert_eq!(fs::read_dir(&store.root).unwrap().count(), expected);
}

fn assert_restart_refuses(store: &TestStore, intent: [&[u8]; 3]) {
    for _ in 0..2 {
        assert_eq!(
            store.restart_inspection().unwrap(),
            Inspection::Preserve(Reason::RecoveryRequired)
        );
        let report = store.commit(intent, |_| panic!("quarantined writer reached mutation"));
        assert!(
            matches!(
                report.outcome,
                Outcome::Refused(Inspection::Preserve(Reason::RecoveryRequired))
            ),
            "{report:?}"
        );
    }
}

fn read_record(store: &TestStore) -> Value {
    // Match the record's held write and delete access, while requesting only read.
    let file = OpenOptions::new()
        .read(true)
        .share_mode(FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE)
        .open(store.path(RECOVERY_REQUIRED))
        .unwrap();
    serde_json::from_reader(file).unwrap()
}

#[test]
fn matches_managed_transaction_fixture() {
    let store = TestStore::new().unwrap();
    let fixture = replay();
    let prior = &fixture.prior;
    for (name, contents) in NAMES.into_iter().zip(bytes(prior)) {
        store.create_durable(name, contents).unwrap();
    }
    assert_eq!(
        store.restart_inspection().unwrap(),
        Inspection::CommittedEnvelope {
            schema: CURRENT_SCHEMA
        }
    );

    let mut replaced = Vec::new();
    let report = store.commit(bytes(&fixture.committed), |boundary| {
        if boundary == Boundary::RecordFlushed {
            let record = read_record(&store);
            let id = record["transactionId"].as_str().unwrap();
            assert_eq!(id.len(), 32);
            assert!(
                id.bytes()
                    .all(|byte| byte.is_ascii_hexdigit() && !byte.is_ascii_uppercase())
            );
            let entries = record["targets"].as_array().unwrap();
            assert_eq!(entries.len(), 3);
            assert_primaries(&store, bytes(prior).map(Some));
            for (index, name) in NAMES.into_iter().enumerate() {
                let target = store.path(name);
                let prefix = format!("{name}.transaction-{id}");
                let entry = &entries[index];
                assert_eq!(entry["path"], serde_json::to_value(target).unwrap());
                assert_eq!(entry["priorExists"], true);
                assert_eq!(
                    entry["backupPath"],
                    serde_json::to_value(store.path(&format!("{prefix}.backup"))).unwrap()
                );
                assert_eq!(
                    entry["restorePath"],
                    serde_json::to_value(store.path(&format!("{prefix}.restore"))).unwrap()
                );
                store.expect(&format!("{prefix}.backup"), Some(prior[index].as_bytes()))?;
                store.expect(
                    &format!("{prefix}.stage"),
                    Some(fixture.committed[index].as_bytes()),
                )?;
            }
            let normalized = record
                .to_string()
                .replace(id, "0123456789abcdef0123456789abcdef")
                .replace(
                    &store.root.display().to_string().replace('\\', "\\\\"),
                    r"C:\\Fixture",
                );
            assert_eq!(
                serde_json::from_str::<Value>(&normalized).unwrap(),
                fixture.recovery
            );
        }
        if let Boundary::AfterReplace(index) = boundary {
            replaced.push(index);
            assert_primaries(
                &store,
                std::array::from_fn(|position| {
                    Some(
                        if position <= index {
                            &fixture.committed[position]
                        } else {
                            &prior[position]
                        }
                        .as_bytes(),
                    )
                }),
            );
        }
        Ok(())
    });
    assert_committed(report);
    assert_eq!(replaced, [0, 1, 2]);
    assert_only_primaries_and_lock(&store, true);
    assert_eq!(
        store.restart_inspection().unwrap(),
        Inspection::CommittedEnvelope {
            schema: CURRENT_SCHEMA
        }
    );
    let written = store
        .capture()
        .unwrap()
        .map(|item| String::from_utf8(item.unwrap()).unwrap());
    assert_eq!(written, fixture.committed);
}

#[test]
#[ignore = "requires managed input on redirected stdin"]
fn exchange_managed_bytes() {
    use std::io::IsTerminal;

    assert!(
        !io::stdin().is_terminal(),
        "expected redirected managed input"
    );
    let bounded_input = io::stdin().lock().take(MAX_DOCUMENT * 24);
    let [prior, intent]: [[Vec<u8>; 3]; 2] = serde_json::from_reader(bounded_input).unwrap();
    let store = TestStore::new().unwrap();
    for (name, contents) in NAMES.into_iter().zip(&prior) {
        store.create_durable(name, contents).unwrap();
    }
    assert_eq!(
        store.restart_inspection().unwrap(),
        Inspection::CommittedEnvelope {
            schema: CURRENT_SCHEMA
        }
    );
    assert_committed(store.commit(intent.each_ref().map(Vec::as_slice), |_| Ok(())));
    assert_eq!(
        store.restart_inspection().unwrap(),
        Inspection::CommittedEnvelope {
            schema: CURRENT_SCHEMA
        }
    );
    assert_only_primaries_and_lock(&store, true);
    let written = store.capture().unwrap().map(Option::unwrap);
    // The leading newline separates the record from libtest's test-name prefix.
    println!(
        "\nCONFIGURATION_OUTPUT={}",
        serde_json::to_string(&written).unwrap()
    );
}

#[test]
fn rejects_bad_intent_and_refuses_old_or_incomplete_preimages() {
    let store = TestStore::new().unwrap();
    let fixture = replay();
    let [profile, preferences, marker] = bytes(&fixture.prior);
    for (input, expected) in [
        ([profile, preferences, b"{}"], Reason::InvalidDocument),
        ([profile, b"{}", marker], Reason::HashMismatch),
    ] {
        let report = store.commit(input, |_| panic!("invalid intent reached mutation"));
        assert!(
            matches!(report.outcome, Outcome::InvalidIntent(Inspection::Preserve(reason)) if reason == expected),
            "{report:?}"
        );
    }
    let old_profile = fixture.prior[0].replace("\"schemaVersion\": 25", "\"schemaVersion\": 24");
    let old_preferences =
        fixture.prior[1].replace("\"schemaVersion\": 25", "\"schemaVersion\": 24");
    let old_marker = format!(
        r#"{{"schemaVersion":24,"profileSha256":"{:X}","preferencesSha256":"{:X}"}}"#,
        Sha256::digest(old_profile.as_bytes()),
        Sha256::digest(old_preferences.as_bytes()),
    );
    let old = [
        old_profile.as_bytes(),
        old_preferences.as_bytes(),
        old_marker.as_bytes(),
    ];
    let report = store.commit(old, |_| panic!("old intent reached mutation"));
    assert!(
        matches!(
            report.outcome,
            Outcome::InvalidIntent(Inspection::CommittedEnvelope { schema: 24 })
        ),
        "{report:?}"
    );
    assert_only_primaries_and_lock(&store, false);
    for (name, contents) in NAMES.into_iter().zip(old) {
        store.create_durable(name, contents).unwrap();
    }
    let report = store.commit(bytes(&fixture.committed), |_| {
        panic!("old preimage reached mutation")
    });
    assert!(
        matches!(
            report.outcome,
            Outcome::Refused(Inspection::CommittedEnvelope { schema: 24 })
        ),
        "{report:?}"
    );
    assert_primaries(&store, old.map(Some));

    fs::remove_file(store.path(PREFERENCES)).unwrap();
    fs::remove_file(store.path(COMMIT)).unwrap();
    fs::write(store.path(PROFILE), fixture.legacy.as_bytes()).unwrap();
    let report = store.commit(bytes(&fixture.committed), |_| {
        panic!("legacy preimage reached mutation")
    });
    assert!(
        matches!(
            report.outcome,
            Outcome::Refused(Inspection::LegacyCandidate)
        ),
        "{report:?}"
    );
    assert_primaries(&store, [Some(fixture.legacy.as_bytes()), None, None]);
}

#[test]
fn rolls_back_every_replaced_prefix_of_a_committed_aggregate() {
    let fixture = replay();
    for fail_after in 0..3 {
        let store = TestStore::new().unwrap();
        assert_committed(store.commit(bytes(&fixture.prior), |_| Ok(())));
        let mut restored = Vec::new();
        let report = store.commit(bytes(&fixture.committed), |boundary| match boundary {
            Boundary::AfterReplace(index) if index == fail_after => Err(injected()),
            Boundary::BeforeRestore(index) => {
                restored.push(index);
                Ok(())
            }
            _ => Ok(()),
        });
        assert_restored(report);
        assert_eq!(restored, (0..=fail_after).collect::<Vec<_>>());
        assert_primaries(&store, bytes(&fixture.prior).map(Some));
        assert_only_primaries_and_lock(&store, true);
    }
}

#[test]
fn rolls_back_to_exact_absence() {
    let store = TestStore::new().unwrap();
    let fixture = replay();
    let report = store.commit(bytes(&fixture.prior), |boundary| match boundary {
        Boundary::RecordFlushed => {
            let record = read_record(&store);
            for entry in record["targets"].as_array().unwrap() {
                assert_eq!(entry["priorExists"], false);
                assert!(entry["backupPath"].is_null());
                assert!(entry["restorePath"].is_null());
            }
            Ok(())
        }
        Boundary::AfterReplace(1) => Err(injected()),
        _ => Ok(()),
    });
    assert_restored(report);
    assert_primaries(&store, [None; 3]);
    assert_eq!(store.restart_inspection().unwrap(), Inspection::Empty);
    assert_only_primaries_and_lock(&store, false);
}

#[test]
fn interrupted_transaction_refuses_restart_even_with_consistent_primaries() {
    let fixture = replay();
    for interrupt_at in [
        Boundary::RecordFlushed,
        Boundary::AfterReplace(0),
        Boundary::AfterReplace(2),
    ] {
        let store = TestStore::new().unwrap();
        assert_committed(store.commit(bytes(&fixture.prior), |_| Ok(())));
        assert!(
            catch_unwind(AssertUnwindSafe(|| {
                store.commit(bytes(&fixture.committed), |boundary| {
                    assert_ne!(boundary, interrupt_at, "simulated abrupt interruption");
                    Ok(())
                })
            }))
            .is_err()
        );
        let before = store.capture().unwrap();
        let record = fs::read(store.path(RECOVERY_REQUIRED)).unwrap();
        assert_restart_refuses(&store, bytes(&fixture.committed));
        assert_eq!(before, store.capture().unwrap());
        assert_eq!(record, fs::read(store.path(RECOVERY_REQUIRED)).unwrap());
    }
}

#[test]
fn preexisting_sidecar_refuses_before_any_primary_is_opened() {
    let fixture = replay();
    for record in [Some(&b""[..]), Some(&b"{foreign record"[..]), None] {
        let store = TestStore::new().unwrap();
        fs::create_dir(store.path(PROFILE)).unwrap();
        if let Some(record) = record {
            fs::write(store.path(RECOVERY_REQUIRED), record).unwrap();
        } else {
            fs::create_dir(store.path(RECOVERY_REQUIRED)).unwrap();
        }
        assert_restart_refuses(&store, bytes(&fixture.committed));
    }
}

#[test]
fn competing_writer_and_late_record_are_never_clobbered() {
    let store = TestStore::new().unwrap();
    let fixture = replay();
    let lock = store.lock().unwrap();
    let busy = store.commit(bytes(&fixture.prior), |_| {
        panic!("busy writer reached mutation")
    });
    assert_eq!(assert_unchanged(busy).raw_os_error(), Some(32));
    drop(lock);

    let foreign = b"external record";
    let report = store.commit(bytes(&fixture.prior), |boundary| {
        if boundary == Boundary::BeforeRecord {
            fs::write(store.path(RECOVERY_REQUIRED), foreign)?;
        }
        Ok(())
    });
    assert_preserved(report, false);
    assert_eq!(fs::read(store.path(RECOVERY_REQUIRED)).unwrap(), foreign);
    assert_primaries(&store, [None; 3]);
    assert_restart_refuses(&store, bytes(&fixture.prior));
}

#[test]
fn stale_stage_is_preserved_and_known_artifacts_are_cleaned() {
    let store = TestStore::new().unwrap();
    let fixture = replay();
    let _lock = store.lock().unwrap();
    let transaction = Transaction::new(
        &store,
        transaction_id().unwrap(),
        [None; 3],
        bytes(&fixture.prior),
    );
    let stage = transaction.targets[1].stage.clone();
    fs::write(store.path(&stage), b"external stage").unwrap();
    let report = transaction.run(&mut |_| Ok(()));
    assert!(report.artifact_errors.is_empty(), "{report:?}");
    assert_eq!(
        assert_unchanged(report).kind(),
        io::ErrorKind::AlreadyExists
    );
    assert_primaries(&store, [None; 3]);
    assert!(!store.record_present().unwrap());
    assert_eq!(fs::read(store.path(&stage)).unwrap(), b"external stage");
    assert_eq!(fs::read_dir(&store.root).unwrap().count(), 2);
}

#[test]
fn external_changes_are_preserved_before_and_after_record_creation() {
    let fixture = replay();
    for (change_at, recovery_required) in [
        (Boundary::BeforeRecord, false),
        (Boundary::BeforeReplace(1), true),
    ] {
        let store = TestStore::new().unwrap();
        assert_committed(store.commit(bytes(&fixture.prior), |_| Ok(())));
        let report = store.commit(bytes(&fixture.committed), |boundary| {
            if boundary == change_at {
                fs::write(store.path(PREFERENCES), b"external preference")?;
            }
            Ok(())
        });
        assert_eq!(
            fs::read(store.path(PREFERENCES)).unwrap(),
            b"external preference"
        );
        if recovery_required {
            assert_preserved(report, true);
            assert_restart_refuses(&store, bytes(&fixture.committed));
        } else {
            assert_unchanged(report);
            assert!(!store.record_present().unwrap());
            assert_only_primaries_and_lock(&store, true);
        }
    }
}

#[test]
fn corrupted_backup_and_restore_failure_keep_recovery_evidence() {
    let fixture = replay();
    for corrupt_backup in [true, false] {
        let store = TestStore::new().unwrap();
        assert_committed(store.commit(bytes(&fixture.prior), |_| Ok(())));
        let report = store.commit(bytes(&fixture.committed), |boundary| match boundary {
            Boundary::AfterReplace(0) => {
                if corrupt_backup {
                    let record = read_record(&store);
                    let id = record["transactionId"].as_str().unwrap();
                    fs::write(
                        store.path(&format!("{PROFILE}.transaction-{id}.backup")),
                        b"tampered",
                    )?;
                }
                Err(injected())
            }
            Boundary::BeforeRestore(0) if !corrupt_backup => Err(injected()),
            _ => Ok(()),
        });
        assert_preserved(report, true);
        assert_restart_refuses(&store, bytes(&fixture.committed));
        store
            .expect(PROFILE, Some(fixture.committed[0].as_bytes()))
            .unwrap();
        let record: Value =
            serde_json::from_slice(&fs::read(store.path(RECOVERY_REQUIRED)).unwrap()).unwrap();
        let id = record["transactionId"].as_str().unwrap();
        let backup = store.path(&format!("{PROFILE}.transaction-{id}.backup"));
        assert!(backup.is_file());
    }
}

#[test]
fn clear_failure_preserves_sidecar_and_artifact_cleanup_failure_is_reported() {
    let fixture = replay();
    for fail_at in [Boundary::BeforeClear, Boundary::BeforeArtifactCleanup] {
        let store = TestStore::new().unwrap();
        assert_committed(store.commit(bytes(&fixture.prior), |_| Ok(())));
        let report = store.commit(bytes(&fixture.committed), |boundary| {
            if boundary == fail_at {
                Err(injected())
            } else {
                Ok(())
            }
        });
        assert_primaries(&store, bytes(&fixture.committed).map(Some));
        if fail_at == Boundary::BeforeClear {
            assert_preserved(report, false);
            assert_restart_refuses(&store, bytes(&fixture.committed));
        } else {
            assert!(matches!(report.outcome, Outcome::Committed), "{report:?}");
            assert_eq!(report.artifact_errors.len(), 1);
            assert!(!store.record_present().unwrap());
        }
        assert!(fs::read_dir(&store.root).unwrap().count() > 4);
    }
}

#[test]
fn hard_link_is_refused_without_mutating_its_other_name() {
    let store = TestStore::new().unwrap();
    let fixture = replay();
    let other = store.path("other.json");
    fs::write(&other, fixture.legacy.as_bytes()).unwrap();
    fs::hard_link(&other, store.path(PROFILE)).unwrap();
    let report = store.commit(bytes(&fixture.committed), |_| {
        panic!("linked file reached mutation")
    });
    assert_eq!(
        assert_unchanged(report).to_string(),
        "configuration entry has multiple links"
    );
    assert_eq!(fs::read(other).unwrap(), fixture.legacy.as_bytes());
    assert!(!store.record_present().unwrap());
}

#[test]
fn native_sharing_failure_after_first_replace_restores_prior() {
    let store = TestStore::new().unwrap();
    let fixture = replay();
    assert_committed(store.commit(bytes(&fixture.prior), |_| Ok(())));
    let reader = OpenOptions::new()
        .read(true)
        .share_mode(FILE_SHARE_READ)
        .open(store.path(PREFERENCES))
        .unwrap();
    let mut replaced = Vec::new();
    let report = store.commit(bytes(&fixture.committed), |boundary| {
        if let Boundary::AfterReplace(index) = boundary {
            replaced.push(index);
        }
        Ok(())
    });
    let Outcome::Restored(error) = report.outcome else {
        panic!("{report:?}");
    };
    assert_eq!(replaced, [0]);
    assert!(matches!(error.raw_os_error(), Some(5 | 32)));
    drop(reader);
    assert_primaries(&store, bytes(&fixture.prior).map(Some));
    assert_only_primaries_and_lock(&store, true);
}

#[test]
fn cleanup_preserves_a_same_byte_substitute_for_an_owned_backup() {
    let store = TestStore::new().unwrap();
    let fixture = replay();
    assert_committed(store.commit(bytes(&fixture.prior), |_| Ok(())));
    let mut backup = None;
    let report = store.commit(bytes(&fixture.committed), |boundary| {
        if boundary == Boundary::RecordFlushed {
            let record = read_record(&store);
            let id = record["transactionId"].as_str().unwrap();
            backup = Some(store.path(&format!("{PROFILE}.transaction-{id}.backup")));
        }
        if boundary == Boundary::BeforeArtifactCleanup {
            let foreign = store.path("substitute");
            fs::write(&foreign, fixture.prior[0].as_bytes())?;
            move_file(&foreign, backup.as_ref().unwrap(), true)?;
        }
        Ok(())
    });
    assert!(matches!(report.outcome, Outcome::Committed), "{report:?}");
    assert_eq!(report.artifact_errors.len(), 1);
    assert_eq!(
        fs::read(backup.unwrap()).unwrap(),
        fixture.prior[0].as_bytes()
    );
    assert!(!store.record_present().unwrap());
    assert_primaries(&store, bytes(&fixture.committed).map(Some));
}

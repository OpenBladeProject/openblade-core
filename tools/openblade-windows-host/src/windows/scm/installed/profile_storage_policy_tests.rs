//! The thermal policy change against the same in-memory store: managed
//! `SetThermalControlPolicyAsync` and
//! `AcknowledgeThermalPolicyUpgradeDisclosureAsync` stage one preference and
//! save the whole aggregate through the profile transaction.
use super::*;
use crate::service_status::thermal_convergence::{
    DISCLOSURE_ACKNOWLEDGED, DISCLOSURE_NOT_REQUIRED, DISCLOSURE_PENDING, MAINTAIN, STRICT,
};
use std::sync::atomic::AtomicBool;

const SET_STRICT: PolicyChange = PolicyChange::Set(STRICT);

/// The replay aggregate with its saved policy and disclosure.
fn with_policy(policy: i32, disclosure: i32) -> [Vec<u8>; 3] {
    let [profile, preferences, _] = Model::replay("prior");
    let mut document: Value = serde_json::from_slice(&preferences).unwrap();
    document["preferences"]["thermalControlPolicy"] = json!(policy);
    document["preferences"]["thermalPolicyUpgradeDisclosure"] = json!(disclosure);
    let preferences = serde_json::to_vec(&document).unwrap();
    let marker = preferences::commit_marker(
        profile_set::CURRENT_SCHEMA,
        &configuration::digest_hex(&profile),
        &configuration::digest_hex(&preferences),
    );
    [profile, preferences, marker]
}

fn saved(policy: i32, disclosure: i32) -> SavedPolicy {
    SavedPolicy::Admitted { policy, disclosure }
}

fn preferences_of(model: &Model) -> Value {
    serde_json::from_slice::<Value>(&model.documents()[1].clone().unwrap()).unwrap()["preferences"]
        .clone()
}

/// The aggregate the managed store writes for the replay profiles and
/// `staged`: the profile document is unchanged and the preferences change.
fn written(policy: i32, disclosure: i32) -> [Option<Vec<u8>>; 3] {
    let model = Model::seeded(with_policy(policy, disclosure));
    model.storage();
    let [profile, _, _] = Model::replay("prior");
    let staged = model.storage().thermal_policy().unwrap();
    assert_eq!(staged, saved(policy, disclosure));
    let (_, validator) = validator();
    let Committed { preferences, .. } = committed(&model, &validator).unwrap().unwrap();
    let preferences = preferences_document(profile_set::CURRENT_SCHEMA, &preferences);
    let marker = commit_marker(
        profile_set::CURRENT_SCHEMA,
        &sha256(&profile),
        &sha256(&preferences),
    );
    [Some(profile), Some(preferences), Some(marker)]
}

#[test]
fn each_change_stores_one_preference_in_the_whole_aggregate() {
    for (prior, change, stored) in [
        (
            (MAINTAIN, DISCLOSURE_NOT_REQUIRED),
            SET_STRICT,
            (STRICT, DISCLOSURE_NOT_REQUIRED),
        ),
        (
            (STRICT, DISCLOSURE_PENDING),
            PolicyChange::Set(MAINTAIN),
            (MAINTAIN, DISCLOSURE_PENDING),
        ),
        (
            (MAINTAIN, DISCLOSURE_PENDING),
            SET_STRICT,
            (STRICT, DISCLOSURE_PENDING),
        ),
        (
            (MAINTAIN, DISCLOSURE_PENDING),
            PolicyChange::Acknowledge,
            (MAINTAIN, DISCLOSURE_ACKNOWLEDGED),
        ),
        (
            (STRICT, DISCLOSURE_PENDING),
            PolicyChange::Acknowledge,
            (STRICT, DISCLOSURE_ACKNOWLEDGED),
        ),
    ] {
        let model = Model::seeded(with_policy(prior.0, prior.1));
        let storage = model.storage();
        let profile = model.documents()[0].clone();

        let PolicySave::Stored(preferences) = storage.save_thermal_policy(change).unwrap() else {
            panic!("{prior:?} {change:?} was not stored");
        };

        let case = format!("{prior:?} {change:?}");
        assert_eq!(
            (
                preferences.thermal_control_policy,
                preferences.thermal_policy_upgrade_disclosure
            ),
            stored,
            "{case}"
        );
        assert_eq!(
            storage.thermal_policy().unwrap(),
            saved(stored.0, stored.1),
            "{case}"
        );
        // A reopened store reads the change back.
        assert_eq!(
            model.storage().thermal_policy().unwrap(),
            saved(stored.0, stored.1),
            "{case}"
        );
        assert_eq!(
            model.documents()[0],
            profile,
            "{case}: the profiles are unchanged"
        );
        assert_eq!(model.documents(), written(stored.0, stored.1), "{case}");
        let document = preferences_of(&model);
        assert_eq!(document["thermalControlPolicy"], json!(stored.0), "{case}");
        assert_eq!(
            document["thermalPolicyUpgradeDisclosure"],
            json!(stored.1),
            "{case}"
        );
        assert_eq!(
            model.snapshot().len(),
            3,
            "{case}: no record or backup remains"
        );
        assert!(!storage.pending(), "{case}");
    }
}

#[test]
fn a_change_the_preferences_already_hold_writes_nothing() {
    for (prior, change) in [
        ((STRICT, DISCLOSURE_NOT_REQUIRED), SET_STRICT),
        (
            (MAINTAIN, DISCLOSURE_ACKNOWLEDGED),
            PolicyChange::Set(MAINTAIN),
        ),
        (
            (MAINTAIN, DISCLOSURE_NOT_REQUIRED),
            PolicyChange::Acknowledge,
        ),
        ((STRICT, DISCLOSURE_ACKNOWLEDGED), PolicyChange::Acknowledge),
    ] {
        let model = Model::seeded(with_policy(prior.0, prior.1));
        let storage = model.storage();
        let before = model.snapshot();

        let PolicySave::Unchanged(preferences) = storage.save_thermal_policy(change).unwrap()
        else {
            panic!("{prior:?} {change:?} was written");
        };

        assert_eq!(
            (
                preferences.thermal_control_policy,
                preferences.thermal_policy_upgrade_disclosure
            ),
            prior
        );
        assert_eq!(model.snapshot(), before);
        assert_eq!(model.writes.load(Ordering::Acquire), 0);
    }
}

#[test]
fn any_single_failed_write_leaves_the_prior_or_the_changed_aggregate() {
    let prior = with_policy(MAINTAIN, DISCLOSURE_NOT_REQUIRED).map(Some);
    let changed = written(STRICT, DISCLOSURE_NOT_REQUIRED);
    for fault in [Fault::Before, Fault::After] {
        for write in 0..11 {
            let model = Model::seeded(with_policy(MAINTAIN, DISCLOSURE_NOT_REQUIRED));
            model.fail(write, fault);
            let storage = model.storage();
            let outcome = storage.save_thermal_policy(SET_STRICT).unwrap();
            let case = format!("{fault:?} write {write}");
            match write {
                // A backup, the record or a document could not be written:
                // the exact prior aggregate stays or is restored.
                0..=6 => {
                    assert_eq!(outcome, PolicySave::Rejected(RESTORED.into()), "{case}");
                    assert_eq!(model.documents(), prior, "{case}");
                    assert_eq!(model.snapshot().len(), 3, "{case}");
                    assert!(!storage.pending(), "{case}");
                    assert_eq!(
                        storage.thermal_policy().unwrap(),
                        saved(MAINTAIN, DISCLOSURE_NOT_REQUIRED),
                        "{case}"
                    );
                }
                // The record could not be cleared: the store closes.
                7 => {
                    assert_eq!(
                        outcome,
                        PolicySave::Rejected(RECOVERY_REQUIRED.into()),
                        "{case}"
                    );
                    assert_eq!(model.documents(), changed, "{case}");
                    assert!(storage.pending(), "{case}");
                    assert_eq!(
                        storage.thermal_policy().unwrap(),
                        SavedPolicy::Quarantined,
                        "{case}"
                    );
                    let before = model.snapshot();
                    let writes = model.writes.load(Ordering::Acquire);
                    assert_eq!(
                        storage
                            .save_thermal_policy(PolicyChange::Acknowledge)
                            .unwrap(),
                        PolicySave::Quarantined,
                        "{case}"
                    );
                    assert_eq!(model.snapshot(), before, "{case}");
                    assert_eq!(model.writes.load(Ordering::Acquire), writes, "{case}");
                }
                // A backup could not be discarded: the change still stands.
                _ => {
                    assert!(matches!(outcome, PolicySave::Stored(_)), "{case}");
                    assert_eq!(model.documents(), changed, "{case}");
                    assert_eq!(
                        storage.thermal_policy().unwrap(),
                        saved(STRICT, DISCLOSURE_NOT_REQUIRED),
                        "{case}"
                    );
                }
            }
            let documents = model.documents();
            assert!(documents == prior || documents == changed, "{case}");
        }
    }
}

#[test]
fn a_write_whose_readback_differs_is_restored() {
    let prior = with_policy(MAINTAIN, DISCLOSURE_PENDING).map(Some);
    for (write, fault) in [
        (4, Fault::Wrong),
        (5, Fault::Wrong),
        (6, Fault::Wrong),
        (5, Fault::Lost),
        (6, Fault::Padded),
    ] {
        let model = Model::seeded(with_policy(MAINTAIN, DISCLOSURE_PENDING));
        model.fail(write, fault);
        let storage = model.storage();

        assert_eq!(
            storage
                .save_thermal_policy(PolicyChange::Acknowledge)
                .unwrap(),
            PolicySave::Rejected(RESTORED.into()),
            "{fault:?} write {write}"
        );

        assert_eq!(model.documents(), prior, "{fault:?} write {write}");
        assert!(!storage.pending(), "{fault:?} write {write}");
        assert_eq!(
            storage.thermal_policy().unwrap(),
            saved(MAINTAIN, DISCLOSURE_PENDING)
        );
    }
}

#[test]
fn a_failed_restore_closes_the_store_with_its_record() {
    let model = Model::seeded(with_policy(MAINTAIN, DISCLOSURE_NOT_REQUIRED));
    model.fail(6, Fault::Before);
    model.fail(7, Fault::Before);
    let storage = model.storage();

    assert_eq!(
        storage.save_thermal_policy(SET_STRICT).unwrap(),
        PolicySave::Rejected(RECOVERY_REQUIRED.into())
    );

    assert!(model.snapshot().contains_key(RECORD));
    assert!(storage.pending());
    assert_eq!(storage.thermal_policy().unwrap(), SavedPolicy::Quarantined);
    // A store opened afterwards finds the record too.
    assert!(
        Storage::open(Box::new(model.clone()), SID)
            .map_or(true, |reopened| reopened.thermal_policy().unwrap()
                == SavedPolicy::Quarantined)
    );
}

/// A thermal-journal fence (`quarantine_settlement`) leaves the configuration
/// admitted: the saved policy reads as saved, pending reconciliation, and a
/// change is stored. A configuration fence is the quarantine.
#[test]
fn only_a_configuration_fence_quarantines_the_saved_policy() {
    let model = Model::seeded(with_policy(MAINTAIN, DISCLOSURE_NOT_REQUIRED));
    let storage = model.storage();

    storage.quarantine_settlement().unwrap();

    assert!(storage.pending());
    assert_eq!(
        storage.thermal_policy().unwrap(),
        saved(MAINTAIN, DISCLOSURE_NOT_REQUIRED)
    );
    assert!(matches!(
        storage.save_thermal_policy(SET_STRICT).unwrap(),
        PolicySave::Stored(_)
    ));
    assert_eq!(
        storage.thermal_policy().unwrap(),
        saved(STRICT, DISCLOSURE_NOT_REQUIRED)
    );

    for fence in [
        |state: &mut State| state.configuration_failed = true,
        |state: &mut State| state.configuration_changed = true,
    ] {
        let model = Model::seeded(with_policy(MAINTAIN, DISCLOSURE_NOT_REQUIRED));
        let storage = model.storage();
        let before = model.snapshot();
        {
            let mut state = storage.state.lock().unwrap();
            state.quarantined = true;
            fence(&mut state);
        }
        assert_eq!(storage.thermal_policy().unwrap(), SavedPolicy::Quarantined);
        assert_eq!(
            storage.save_thermal_policy(SET_STRICT).unwrap(),
            PolicySave::Quarantined
        );
        assert_eq!(model.snapshot(), before);
    }
}

#[test]
fn a_preserved_aggregate_reports_strict_and_takes_no_change() {
    type Tamper = fn(&mut BTreeMap<String, Vec<u8>>);
    let tampers: [Tamper; 3] = [
        |files| files.get_mut(NAMES[0]).unwrap().push(b' '),
        |files| drop(files.insert(RECORD.into(), b"{}".to_vec())),
        |files| drop(files.remove(NAMES[2])),
    ];
    for tamper in tampers {
        let model = Model::seeded(with_policy(MAINTAIN, DISCLOSURE_PENDING));
        let storage = model.storage();
        tamper(&mut model.bytes.lock().unwrap());
        let before = model.snapshot();

        assert_eq!(storage.thermal_policy().unwrap(), SavedPolicy::Quarantined);
        for change in [
            SET_STRICT,
            PolicyChange::Set(MAINTAIN),
            PolicyChange::Acknowledge,
        ] {
            assert_eq!(
                storage.save_thermal_policy(change).unwrap(),
                PolicySave::Quarantined
            );
        }

        assert_eq!(model.snapshot(), before);
        assert_eq!(model.writes.load(Ordering::Acquire), 0);
    }
}

#[test]
fn an_absent_aggregate_is_the_safe_defaults_and_a_change_materialises_it() {
    let model = Model::default();
    let storage = model.storage();
    assert_eq!(
        storage.thermal_policy().unwrap(),
        saved(MAINTAIN, DISCLOSURE_NOT_REQUIRED)
    );
    assert_eq!(
        storage
            .save_thermal_policy(PolicyChange::Acknowledge)
            .unwrap(),
        PolicySave::Unchanged(Preferences::default())
    );
    assert!(model.snapshot().is_empty());

    assert!(matches!(
        storage.save_thermal_policy(SET_STRICT).unwrap(),
        PolicySave::Stored(_)
    ));

    assert_eq!(model.snapshot().len(), 3);
    assert_eq!(
        model.storage().thermal_policy().unwrap(),
        saved(STRICT, DISCLOSURE_NOT_REQUIRED)
    );
}

/// The model, whose reads fail once `unreadable` is set.
#[derive(Clone)]
struct Unreadable {
    model: Model,
    unreadable: Arc<AtomicBool>,
}

impl Files for Unreadable {
    fn read(&self, name: &str) -> io::Result<Option<Vec<u8>>> {
        if self.unreadable.load(Ordering::Acquire) {
            return Err(injected());
        }
        self.model.read(name)
    }
    fn replace(&self, name: &str, expected: Option<&[u8]>, bytes: &[u8]) -> io::Result<()> {
        self.model.replace(name, expected, bytes)
    }
    fn remove(&self, name: &str, expected: &[u8]) -> io::Result<()> {
        self.model.remove(name, expected)
    }
    fn discard_stage(&self, name: &str) -> io::Result<()> {
        self.model.discard_stage(name)
    }
}

#[test]
fn an_unreadable_store_fails_the_request_and_writes_nothing() {
    let model = Model::seeded(with_policy(MAINTAIN, DISCLOSURE_PENDING));
    let unreadable = Arc::new(AtomicBool::new(false));
    let storage = Storage::open(
        Box::new(Unreadable {
            model: model.clone(),
            unreadable: unreadable.clone(),
        }),
        SID,
    )
    .unwrap();
    unreadable.store(true, Ordering::Release);
    let before = model.snapshot();

    assert!(storage.thermal_policy().is_err());
    for change in [SET_STRICT, PolicyChange::Acknowledge] {
        assert!(storage.save_thermal_policy(change).is_err());
    }

    assert_eq!(model.snapshot(), before);
    assert_eq!(model.writes.load(Ordering::Acquire), 0);
    // Once it reads again, nothing was lost.
    unreadable.store(false, Ordering::Release);
    assert_eq!(
        storage.thermal_policy().unwrap(),
        saved(MAINTAIN, DISCLOSURE_PENDING)
    );
}

#[test]
fn an_aggregate_the_service_reader_would_refuse_is_not_changed() {
    // Managed's comparison keeps a dotless i apart from an I, so the
    // aggregate another writer stores after the open is valid; a coarser
    // reader would refuse it once rewritten.
    let model = Model::seeded(with_policy(MAINTAIN, DISCLOSURE_NOT_REQUIRED));
    let storage = Storage::open_with(Box::new(model.clone()), SID, coarser).unwrap();
    let [profile, _, _] = aggregate_with(json!([
        application(0, "C:\\\u{131}\\Game.exe"),
        application(1, r"C:\I\Game.exe")
    ]));
    let [_, preferences, _] = with_policy(MAINTAIN, DISCLOSURE_NOT_REQUIRED);
    let marker = preferences::commit_marker(
        profile_set::CURRENT_SCHEMA,
        &configuration::digest_hex(&profile),
        &configuration::digest_hex(&preferences),
    );
    for (name, bytes) in NAMES.into_iter().zip([profile, preferences, marker]) {
        model.bytes.lock().unwrap().insert(name.into(), bytes);
    }
    let before = model.snapshot();

    assert_eq!(
        storage.save_thermal_policy(SET_STRICT).unwrap(),
        PolicySave::Rejected(PREFERENCES_UNREADABLE.into())
    );

    assert_eq!(model.snapshot(), before);
    assert_eq!(model.writes.load(Ordering::Acquire), 0);
    assert!(!storage.pending());
    assert_eq!(
        storage.thermal_policy().unwrap(),
        saved(MAINTAIN, DISCLOSURE_NOT_REQUIRED)
    );
}

#[test]
fn an_aggregate_below_schema_16_migrates_to_maintain_with_the_disclosure_pending() {
    let mut checked = 0;
    let mut survived = 0;
    for case in migration::cases_for_policy() {
        let (name, schema, files) = case;
        let model = Model::default();
        *model.bytes.lock().unwrap() = files;
        let Ok(storage) = Storage::open(Box::new(model.clone()), SID) else {
            continue;
        };
        let SavedPolicy::Admitted { policy, disclosure } = storage.thermal_policy().unwrap() else {
            continue;
        };
        if schema < 16 {
            checked += 1;
            assert_eq!(
                (policy, disclosure),
                (MAINTAIN, DISCLOSURE_PENDING),
                "{name}"
            );
        } else {
            // Managed keeps a saved policy and disclosure from schema 16 on.
            let kept = match name.as_str() {
                "schema 16 Strict thermal policy survives migration" => {
                    Some((STRICT, DISCLOSURE_NOT_REQUIRED))
                }
                "schema 26 acknowledged thermal policy disclosure survives migration" => {
                    Some((MAINTAIN, DISCLOSURE_ACKNOWLEDGED))
                }
                _ => None,
            };
            if let Some(kept) = kept {
                survived += 1;
                assert_eq!((policy, disclosure), kept, "{name}");
            }
            let document = preferences_of(&model);
            assert_eq!(document["thermalControlPolicy"], json!(policy), "{name}");
            assert_eq!(
                document["thermalPolicyUpgradeDisclosure"],
                json!(disclosure),
                "{name}"
            );
        }
    }
    assert!(
        checked >= 10,
        "{checked} migrated aggregates below schema 16"
    );
    assert_eq!(survived, 2, "the managed rows that keep a saved policy");
}

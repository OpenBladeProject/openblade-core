//! The profile transaction against an in-memory store that can fail any one
//! of its writes. The managed replay fixture supplies the documents managed
//! itself wrote before and after the same save.
use super::*;
use openblade_domain::profile_set::{BatteryPolicy, Dialect, json, preferences, update};
use std::collections::BTreeMap;
use std::sync::atomic::{AtomicUsize, Ordering};

const REPLAY: &str = include_str!(
    "../../../../../../tests/OpenBlade.Service.Tests/WindowsReplay/v1/configuration-aggregate.json"
);
const SID: &str = "S-1-5-21-111-222-333-1001";
const RECORD: &str = configuration::RECOVERY_REQUIRED;

/// How an injected write fails.
#[derive(Clone, Copy, Debug, PartialEq)]
enum Fault {
    /// Refused before anything changed.
    Before,
    /// Applied, then reported as failed: the store cannot tell which.
    After,
}

#[derive(Clone, Default)]
struct Model {
    bytes: Arc<Mutex<BTreeMap<String, Vec<u8>>>>,
    writes: Arc<AtomicUsize>,
    /// Zero-based write to fail, and how; later entries fail later writes.
    faults: Arc<Mutex<Vec<(usize, Fault)>>>,
    log: Arc<Mutex<Vec<String>>>,
}

impl Model {
    fn replay(key: &str) -> [Vec<u8>; 3] {
        let fixture: Value = serde_json::from_str(REPLAY).unwrap();
        [0, 1, 2].map(|index| fixture[key][index].as_str().unwrap().as_bytes().to_vec())
    }

    fn seeded(documents: [Vec<u8>; 3]) -> Self {
        let model = Self::default();
        for (name, bytes) in NAMES.into_iter().zip(documents) {
            model.bytes.lock().unwrap().insert(name.into(), bytes);
        }
        model
    }

    fn committed() -> Self {
        Self::seeded(Self::replay("prior"))
    }

    fn storage(&self) -> Arc<Storage> {
        Storage::open(Box::new(self.clone()), SID).unwrap()
    }

    fn fail(&self, write: usize, fault: Fault) {
        self.faults.lock().unwrap().push((write, fault));
    }

    fn snapshot(&self) -> BTreeMap<String, Vec<u8>> {
        self.bytes.lock().unwrap().clone()
    }

    fn documents(&self) -> [Option<Vec<u8>>; 3] {
        NAMES.map(|name| self.bytes.lock().unwrap().get(name).cloned())
    }

    /// Counts a write and returns the fault scripted for it.
    fn write(&self, entry: String) -> Option<Fault> {
        let number = self.writes.fetch_add(1, Ordering::AcqRel);
        self.log.lock().unwrap().push(entry);
        self.faults
            .lock()
            .unwrap()
            .iter()
            .find_map(|(at, fault)| (*at == number).then_some(*fault))
    }
}

fn injected() -> io::Error {
    io::Error::other("injected profile store failure")
}

impl Files for Model {
    fn read(&self, name: &str) -> io::Result<Option<Vec<u8>>> {
        Ok(self.bytes.lock().unwrap().get(name).cloned())
    }
    fn replace(&self, name: &str, expected: Option<&[u8]>, bytes: &[u8]) -> io::Result<()> {
        let fault = self.write(format!("replace {name}"));
        if fault == Some(Fault::Before) {
            return Err(injected());
        }
        let mut files = self.bytes.lock().unwrap();
        if files.get(name).map(Vec::as_slice) != expected {
            return Err(denied());
        }
        files.insert(name.into(), bytes.to_vec());
        fault.map_or(Ok(()), |_| Err(injected()))
    }
    fn remove(&self, name: &str, expected: &[u8]) -> io::Result<()> {
        let fault = self.write(format!("remove {name}"));
        if fault == Some(Fault::Before) {
            return Err(injected());
        }
        let mut files = self.bytes.lock().unwrap();
        if files.get(name).map(Vec::as_slice) != Some(expected) {
            return Err(denied());
        }
        files.remove(name);
        fault.map_or(Ok(()), |_| Err(injected()))
    }
}

/// The saved set with a 75 percent charge limit: the save the managed replay
/// fixture recorded.
fn requested(model: &Model) -> ProfileSet {
    let document = json::parse(&model.documents()[0].clone().unwrap()).unwrap();
    let mut profiles =
        update::decode_profiles(document.member("profiles").unwrap(), Dialect::Document).unwrap();
    profiles.battery_policy = Some(BatteryPolicy {
        charge_limit_percent: 75,
    });
    profiles
}

fn transaction_id(model: &Model) -> String {
    let log = model.log.lock().unwrap();
    let backup = log.iter().find(|entry| entry.ends_with(".backup")).unwrap();
    backup
        .split(".transaction-")
        .nth(1)
        .unwrap()
        .trim_end_matches(".backup")
        .to_owned()
}

#[test]
fn a_stored_set_is_the_aggregate_managed_writes_for_it() {
    let model = Model::committed();
    let storage = model.storage();
    let request = requested(&model);

    assert_eq!(
        storage.save_profiles(&request).unwrap(),
        ProfileSave::Stored
    );

    // Managed wrote these three documents for the same save.
    assert_eq!(
        model.documents().map(Option::unwrap),
        Model::replay("committed")
    );
    assert_eq!(
        model.snapshot().len(),
        3,
        "no record, backup or staging file remains"
    );
    assert!(!storage.pending());
    // The preferences document is carried over byte for byte.
    assert_eq!(
        model.documents()[1],
        Some(Model::replay("prior")[1].clone())
    );
}

#[test]
fn the_transaction_follows_the_managed_order() {
    let model = Model::committed();
    assert_eq!(
        model.storage().save_profiles(&requested(&model)).unwrap(),
        ProfileSave::Stored
    );
    let id = transaction_id(&model);
    assert_eq!(id.len(), 32);
    assert!(
        id.bytes()
            .all(|b| b.is_ascii_digit() || (b'a'..=b'f').contains(&b))
    );
    let backup = |name: &str| format!("{name}.transaction-{id}.backup");
    let [profile, preferences, marker] = NAMES;
    assert_eq!(
        *model.log.lock().unwrap(),
        [
            format!("replace {}", backup(profile)),
            format!("replace {}", backup(preferences)),
            format!("replace {}", backup(marker)),
            format!("replace {RECORD}"),
            format!("replace {profile}"),
            format!("replace {preferences}"),
            format!("replace {marker}"),
            format!("remove {RECORD}"),
            format!("remove {}", backup(profile)),
            format!("remove {}", backup(preferences)),
            format!("remove {}", backup(marker)),
        ]
    );
}

#[test]
fn the_record_names_the_transaction_and_its_exact_backups() {
    let model = Model::committed();
    // Fail the first document after the record exists, and then its restore.
    model.fail(4, Fault::After);
    model.fail(5, Fault::Before);
    let prior = Model::replay("prior");
    let storage = model.storage();

    assert_eq!(
        storage.save_profiles(&requested(&model)).unwrap(),
        ProfileSave::Rejected(RECOVERY_REQUIRED.into())
    );

    let id = transaction_id(&model);
    let files = model.snapshot();
    let record: Value = serde_json::from_slice(&files[RECORD]).unwrap();
    assert_eq!(record["schemaVersion"], 1);
    assert_eq!(record["transactionId"], id);
    for (index, name) in NAMES.into_iter().enumerate() {
        let backup = format!("{name}.transaction-{id}.backup");
        assert_eq!(
            record["targets"][index],
            json!({"path": name, "priorExists": true, "backupPath": backup, "restorePath": null})
        );
        assert_eq!(
            files[&backup], prior[index],
            "the backup is the exact prior document"
        );
    }
    // The aggregate is preserved for review and nothing may change it.
    assert!(storage.pending());
    assert_eq!(
        storage.save_profiles(&requested(&model)).unwrap(),
        ProfileSave::Blocked(QUARANTINE_BLOCKED)
    );
    assert!(Storage::open(Box::new(model.clone()), SID).is_err());
}

#[test]
fn any_single_failed_write_leaves_a_whole_aggregate() {
    let prior = Model::replay("prior").map(Some);
    let committed = Model::replay("committed").map(Some);
    for fault in [Fault::Before, Fault::After] {
        for write in 0..11 {
            let model = Model::committed();
            model.fail(write, fault);
            let storage = model.storage();
            let outcome = storage.save_profiles(&requested(&model)).unwrap();
            let files = model.snapshot();
            let case = format!("{fault:?} write {write}");
            match write {
                // A backup or the record could not be written: nothing moved.
                0..=3 => {
                    assert_eq!(outcome, ProfileSave::Rejected(RESTORED.into()), "{case}");
                    assert_eq!(model.documents(), prior, "{case}");
                    assert!(!storage.pending(), "{case}");
                    assert_eq!(files.len(), 3, "{case}: no record or backup remains");
                }
                // A document replacement failed, or may have: the exact prior
                // aggregate is restored.
                4..=6 => {
                    assert_eq!(outcome, ProfileSave::Rejected(RESTORED.into()), "{case}");
                    assert_eq!(model.documents(), prior, "{case}");
                    assert_eq!(files.len(), 3, "{case}: record and backups are gone");
                    assert!(!storage.pending(), "{case}");
                }
                // The record could not be cleared: the new aggregate is whole
                // but stays preserved under its record.
                7 => {
                    assert_eq!(model.documents(), committed, "{case}");
                    assert_eq!(
                        outcome,
                        ProfileSave::Rejected(RECOVERY_REQUIRED.into()),
                        "{case}"
                    );
                    assert_eq!(files.contains_key(RECORD), fault == Fault::Before, "{case}");
                    assert!(storage.pending(), "{case}");
                }
                // A backup could not be discarded: the save still stands.
                _ => {
                    assert_eq!(outcome, ProfileSave::Stored, "{case}");
                    assert_eq!(model.documents(), committed, "{case}");
                    assert!(!files.contains_key(RECORD), "{case}");
                    assert!(!storage.pending(), "{case}");
                }
            }
            // Whatever happened, the three documents are one committed
            // aggregate: either the prior one or the new one.
            let documents = model.documents();
            assert!(documents == prior || documents == committed, "{case}");
        }
    }
}

#[test]
fn a_failed_restore_keeps_the_record_and_every_backup() {
    // The marker replacement fails and then the first restore write fails.
    let model = Model::committed();
    model.fail(6, Fault::Before);
    model.fail(7, Fault::Before);
    let storage = model.storage();

    assert_eq!(
        storage.save_profiles(&requested(&model)).unwrap(),
        ProfileSave::Rejected(RECOVERY_REQUIRED.into())
    );

    let files = model.snapshot();
    assert!(files.contains_key(RECORD));
    assert_eq!(
        files
            .keys()
            .filter(|name| name.ends_with(".backup"))
            .count(),
        3
    );
    // Torn: the new profile and preferences under the old marker.
    assert_eq!(
        model.documents()[0],
        Some(Model::replay("committed")[0].clone())
    );
    assert_eq!(
        model.documents()[2],
        Some(Model::replay("prior")[2].clone())
    );
    assert!(storage.pending());
    // Reads refuse the torn aggregate too.
    assert!(storage.profiles().is_err());
}

#[test]
fn a_rejected_set_changes_nothing() {
    let model = Model::committed();
    let storage = model.storage();
    let mut request = requested(&model);
    request.battery_policy = None;

    assert_eq!(
        storage.save_profiles(&request).unwrap(),
        ProfileSave::Rejected(
            "The requested intent was rejected: The battery policy is missing.".into()
        )
    );

    assert_eq!(
        model.documents().map(Option::unwrap),
        Model::replay("prior")
    );
    assert_eq!(model.writes.load(Ordering::Acquire), 0);
}

#[test]
fn a_preserved_aggregate_is_blocked_untouched() {
    type Tamper = fn(&mut BTreeMap<String, Vec<u8>>);
    let tampers: [Tamper; 4] = [
        // A document no longer matches the digest in the marker.
        |files| files.get_mut(NAMES[1]).unwrap().push(b' '),
        // Another writer recorded a transaction.
        |files| drop(files.insert(RECORD.into(), b"{}".to_vec())),
        // The marker is gone.
        |files| drop(files.remove(NAMES[2])),
        // A newer schema.
        |files| {
            let newer = String::from_utf8(files[NAMES[2]].clone())
                .unwrap()
                .replacen("27", "28", 1);
            files.insert(NAMES[2].into(), newer.into_bytes());
        },
    ];
    for tamper in tampers {
        // Opening already refuses a preserved aggregate; a store opened
        // before the aggregate changed must refuse to replace it as well.
        let model = Model::committed();
        let storage = model.storage();
        let request = requested(&model);
        tamper(&mut model.bytes.lock().unwrap());
        let before = model.snapshot();

        assert_eq!(
            storage.save_profiles(&request).unwrap(),
            ProfileSave::Blocked(QUARANTINE_BLOCKED)
        );

        assert_eq!(model.snapshot(), before);
        assert_eq!(model.writes.load(Ordering::Acquire), 0);
    }
}

#[test]
fn an_older_schema_is_migrated_by_the_save() {
    // Schema 26: the save rewrites the preferences document as well.
    let [profile, preferences, _] = Model::replay("prior");
    let older = |bytes: &[u8]| {
        String::from_utf8(bytes.to_vec())
            .unwrap()
            .replacen("\"schemaVersion\": 27", "\"schemaVersion\": 26", 1)
            .into_bytes()
    };
    let (profile, preferences) = (older(&profile), older(&preferences));
    let marker = preferences::commit_marker(
        26,
        &configuration::digest_hex(&profile),
        &configuration::digest_hex(&preferences),
    );
    let model = Model::seeded([profile, preferences, marker]);
    let storage = model.storage();

    assert_eq!(
        storage.save_profiles(&requested(&model)).unwrap(),
        ProfileSave::Stored
    );

    // The result is the aggregate managed writes at the current schema.
    assert_eq!(
        model.documents().map(Option::unwrap),
        Model::replay("committed")
    );
}

#[test]
fn an_absent_aggregate_is_materialised_from_the_safe_defaults() {
    let model = Model::default();
    let storage = model.storage();
    let mut request = ProfileSet::safe_defaults();
    request.battery_policy = Some(BatteryPolicy {
        charge_limit_percent: 75,
    });

    assert_eq!(
        storage.save_profiles(&request).unwrap(),
        ProfileSave::Stored
    );

    // The replay fixture's prior aggregate is managed's materialised default,
    // so the same save yields the same documents.
    assert_eq!(
        model.documents().map(Option::unwrap),
        Model::replay("committed")
    );
    assert_eq!(model.snapshot().len(), 3);
}

#[test]
fn a_repeated_attempt_reuses_its_own_backups() {
    // The first attempt cannot write the record and cannot discard one backup.
    let model = Model::committed();
    model.fail(3, Fault::Before);
    model.fail(5, Fault::Before);
    let storage = model.storage();
    assert_eq!(
        storage.save_profiles(&requested(&model)).unwrap(),
        ProfileSave::Rejected(RESTORED.into())
    );
    assert_eq!(
        model
            .snapshot()
            .keys()
            .filter(|name| name.ends_with(".backup"))
            .count(),
        1
    );

    assert_eq!(
        storage.save_profiles(&requested(&model)).unwrap(),
        ProfileSave::Stored
    );
    assert_eq!(model.snapshot().len(), 3);
}

//! The profile transaction against an in-memory store that can fail any one
//! of its writes and keeps staging files as the protected store does. The
//! managed replay fixture supplies the documents managed itself wrote before
//! and after the same save. `real` runs the same saves on files.
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
    /// Reported as written while other bytes landed.
    Wrong,
    /// Reported as written while nothing landed.
    Lost,
    /// Landed with a line break after it: the same JSON, other bytes.
    Padded,
    /// Refused with its staging file left behind, as the protected store
    /// leaves it when the rename fails.
    Stranded,
}

fn stage(name: &str) -> String {
    format!("{name}.pending")
}

#[derive(Clone, Default)]
struct Model {
    bytes: Arc<Mutex<BTreeMap<String, Vec<u8>>>>,
    writes: Arc<AtomicUsize>,
    /// Zero-based write to fail, and how; later entries fail later writes.
    faults: Arc<Mutex<Vec<(usize, Fault)>>>,
    log: Arc<Mutex<Vec<String>>>,
    /// A staging file another handle holds: it cannot be removed.
    held: Arc<Mutex<Option<String>>>,
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

    /// Leaves the staging file of `name` behind, as an interrupted replace does.
    fn strand(&self, name: &str) {
        let mut files = self.bytes.lock().unwrap();
        files.insert(stage(name), b"{\"schemaVersion\":".to_vec());
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
        // The stage is created new, so one left behind refuses the replace.
        if files.get(name).map(Vec::as_slice) != expected || files.contains_key(&stage(name)) {
            return Err(denied());
        }
        match fault {
            Some(Fault::Wrong) => drop(files.insert(name.into(), b"{".to_vec())),
            Some(Fault::Lost) => {}
            Some(Fault::Padded) => drop(files.insert(name.into(), [bytes, b"\r\n"].concat())),
            Some(Fault::Stranded) => {
                files.insert(stage(name), bytes.to_vec());
                return Err(injected());
            }
            _ => drop(files.insert(name.into(), bytes.to_vec())),
        }
        if fault == Some(Fault::After) {
            return Err(injected());
        }
        Ok(())
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
    fn discard_stage(&self, name: &str) -> io::Result<()> {
        let mut files = self.bytes.lock().unwrap();
        let held = self.held.lock().unwrap().as_deref() == Some(name);
        if held && files.contains_key(&stage(name)) {
            return Err(io::Error::from_raw_os_error(32));
        }
        files.remove(&stage(name));
        Ok(())
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
                    // With or without the record, the store stays closed.
                    assert_eq!(
                        storage.save_profiles(&requested(&model)).unwrap(),
                        ProfileSave::Blocked(QUARANTINE_BLOCKED),
                        "{case}"
                    );
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
fn a_write_that_lands_other_bytes_is_caught_by_the_readback() {
    let prior = Model::replay("prior").map(Some);
    for write in 4..=6 {
        let model = Model::committed();
        model.fail(write, Fault::Wrong);
        let storage = model.storage();

        assert_eq!(
            storage.save_profiles(&requested(&model)).unwrap(),
            ProfileSave::Rejected(RESTORED.into()),
            "write {write}"
        );

        assert_eq!(model.documents(), prior, "write {write}");
        assert_eq!(model.snapshot().len(), 3, "write {write}");
        assert!(!storage.pending(), "write {write}");
    }
}

#[test]
fn a_marker_that_is_not_the_intended_bytes_is_restored() {
    // The marker still commits the two documents, so the aggregate reads
    // back as the admitted set; only its bytes differ from what was meant.
    let model = Model::committed();
    model.fail(6, Fault::Padded);
    let storage = model.storage();

    assert_eq!(
        storage.save_profiles(&requested(&model)).unwrap(),
        ProfileSave::Rejected(RESTORED.into())
    );

    assert_eq!(
        model.documents().map(Option::unwrap),
        Model::replay("prior")
    );
    assert_eq!(model.snapshot().len(), 3);
    assert!(!storage.pending());
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
fn a_restore_that_does_not_land_keeps_the_record_and_every_backup() {
    // The profile lands though its write reports failure, and the write
    // that restores it reports success while nothing changes.
    let model = Model::committed();
    model.fail(4, Fault::After);
    model.fail(5, Fault::Lost);
    let storage = model.storage();

    assert_eq!(
        storage.save_profiles(&requested(&model)).unwrap(),
        ProfileSave::Rejected(RECOVERY_REQUIRED.into())
    );

    // Only reading the documents back shows the restore did not happen.
    let files = model.snapshot();
    assert!(files.contains_key(RECORD));
    assert_eq!(
        files
            .keys()
            .filter(|name| name.ends_with(".backup"))
            .count(),
        3
    );
    assert_eq!(
        model.documents()[0],
        Some(Model::replay("committed")[0].clone())
    );
    assert!(storage.pending());
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

#[test]
fn a_stage_an_abandoned_attempt_left_is_removed_before_the_save() {
    // The backups are named by the change, so learn their names first.
    let rehearsal = Model::committed();
    rehearsal
        .storage()
        .save_profiles(&requested(&rehearsal))
        .unwrap();
    let id = transaction_id(&rehearsal);
    let staged = NAMES
        .map(|name| format!("{name}.transaction-{id}.backup"))
        .into_iter()
        .chain([RECORD.to_owned()])
        .chain(NAMES.map(str::to_owned));
    for name in staged {
        let model = Model::committed();
        model.strand(&name);
        let storage = model.storage();

        assert_eq!(
            storage.save_profiles(&requested(&model)).unwrap(),
            ProfileSave::Stored,
            "{name}"
        );

        assert_eq!(
            model.documents().map(Option::unwrap),
            Model::replay("committed"),
            "{name}"
        );
        assert_eq!(model.snapshot().len(), 3, "{name}: the stage is gone");
    }
}

#[test]
fn a_stage_that_cannot_be_removed_refuses_the_save_and_says_so() {
    let model = Model::committed();
    model.strand(NAMES[0]);
    *model.held.lock().unwrap() = Some(NAMES[0].into());
    let storage = model.storage();
    let before = model.snapshot();

    assert_eq!(
        storage.save_profiles(&requested(&model)).unwrap(),
        ProfileSave::Rejected(STAGE_BLOCKED.into())
    );

    // Nothing was written and the store is not closed over it.
    assert_eq!(model.snapshot(), before);
    assert_eq!(model.writes.load(Ordering::Acquire), 0);
    assert!(!storage.pending());

    *model.held.lock().unwrap() = None;
    assert_eq!(
        storage.save_profiles(&requested(&model)).unwrap(),
        ProfileSave::Stored
    );
    assert_eq!(model.snapshot().len(), 3);
}

#[test]
fn a_write_that_fails_takes_its_stage_back() {
    // Every staged write in turn fails and leaves its staging file behind.
    for write in 0..=6 {
        let model = Model::committed();
        model.fail(write, Fault::Stranded);
        let storage = model.storage();

        assert_eq!(
            storage.save_profiles(&requested(&model)).unwrap(),
            ProfileSave::Rejected(RESTORED.into()),
            "write {write}"
        );

        assert_eq!(
            model.documents().map(Option::unwrap),
            Model::replay("prior"),
            "write {write}"
        );
        assert_eq!(
            model.snapshot().len(),
            3,
            "write {write}: {:?}",
            model.snapshot().keys()
        );
        assert!(!storage.pending(), "write {write}");
        // A restart reads the same aggregate, and the same save now stores.
        assert_eq!(
            Storage::open(Box::new(model.clone()), SID)
                .unwrap()
                .save_profiles(&requested(&model))
                .unwrap(),
            ProfileSave::Stored,
            "write {write}"
        );
        assert_eq!(model.snapshot().len(), 3, "write {write}");
    }
}

#[test]
fn a_stage_that_could_not_be_taken_back_is_removed_by_the_next_save() {
    // The profile replacement fails while another handle holds its stage.
    let model = Model::committed();
    model.fail(4, Fault::Stranded);
    *model.held.lock().unwrap() = Some(NAMES[0].into());
    let storage = model.storage();

    assert_eq!(
        storage.save_profiles(&requested(&model)).unwrap(),
        ProfileSave::Rejected(RESTORED.into())
    );

    let left: Vec<String> = model.snapshot().into_keys().collect();
    assert_eq!(left.len(), 4, "{left:?}");
    assert!(left.contains(&stage(NAMES[0])), "{left:?}");
    assert!(!storage.pending());
    // Still held: the next save says what is in its way and writes nothing.
    let writes = model.writes.load(Ordering::Acquire);
    assert_eq!(
        storage.save_profiles(&requested(&model)).unwrap(),
        ProfileSave::Rejected(STAGE_BLOCKED.into())
    );
    assert_eq!(model.writes.load(Ordering::Acquire), writes);

    *model.held.lock().unwrap() = None;
    assert_eq!(
        storage.save_profiles(&requested(&model)).unwrap(),
        ProfileSave::Stored
    );
    assert_eq!(model.snapshot().len(), 3);
}

#[test]
fn writes_that_never_land_are_caught_before_the_record_is_removed() {
    // The profile and the marker report success and change nothing, which
    // leaves the prior aggregate: whole and committed, but not the one meant.
    let model = Model::committed();
    model.fail(4, Fault::Lost);
    model.fail(6, Fault::Lost);
    let storage = model.storage();

    assert_eq!(
        storage.save_profiles(&requested(&model)).unwrap(),
        ProfileSave::Rejected(RESTORED.into())
    );

    assert_eq!(
        model.documents().map(Option::unwrap),
        Model::replay("prior")
    );
    // The verification ran while the record stood: it was removed after.
    let log = model.log.lock().unwrap().clone();
    assert_eq!(log[7], format!("remove {RECORD}"), "{log:?}");
    assert_eq!(model.snapshot().len(), 3);
    assert!(!storage.pending());
}

#[test]
fn a_failed_materialisation_deletes_the_documents_it_created() {
    // No aggregate and so no backups: the record, then the three documents.
    for (write, fault) in [(2, Fault::Before), (3, Fault::Before), (3, Fault::Wrong)] {
        let model = Model::default();
        model.fail(write, fault);
        let storage = model.storage();
        let mut request = ProfileSet::safe_defaults();
        request.battery_policy = Some(BatteryPolicy {
            charge_limit_percent: 75,
        });

        assert_eq!(
            storage.save_profiles(&request).unwrap(),
            ProfileSave::Rejected(RESTORED.into()),
            "{fault:?} write {write}"
        );

        assert!(
            model.snapshot().is_empty(),
            "{fault:?} write {write}: {:?}",
            model.snapshot().keys()
        );
        assert!(!storage.pending());
        assert_eq!(
            storage.save_profiles(&request).unwrap(),
            ProfileSave::Stored
        );
    }
}

/// The prior aggregate with `applications` as its application profiles.
fn aggregate_with(applications: Value) -> [Vec<u8>; 3] {
    let [profile, preferences, _] = Model::replay("prior");
    let mut document: Value = serde_json::from_slice(&profile).unwrap();
    document["profiles"]["applicationProfiles"] = applications;
    let profile = serde_json::to_vec(&document).unwrap();
    let marker = preferences::commit_marker(
        profile_set::CURRENT_SCHEMA,
        &configuration::digest_hex(&profile),
        &configuration::digest_hex(&preferences),
    );
    [profile, preferences, marker]
}

fn application(id: u8, path: &str) -> Value {
    json!({"id": format!("{id}f8fad5b-d9cb-469f-a165-70867728950e"), "name": "Game", "executablePath": path,
        "activationMode": 0, "enabled": true, "overrides": {"pluggedIn": {"performance": 2}}})
}

#[test]
fn the_reader_check_admits_what_the_service_reads_and_nothing_else() {
    let same = str::eq_ignore_ascii_case;
    assert!(reopens(&Model::replay("committed"), same));
    for stored in [
        r"C:\Games\Game.exe",
        r"\\?\C:\Games\..\Game.exe",
        r"\\?\C:\Games\.\Game.exe",
        r"\??\C:\Games\..\Game.exe",
        r"\\.\C:\Game.exe",
        r"\\server\share\Game.exe",
    ] {
        let documents = aggregate_with(json!([application(0, stored)]));
        assert!(reopens(&documents, same), "{stored}");
        // The store opens on exactly what the check admitted.
        assert!(Storage::open(Box::new(Model::seeded(documents)), SID).is_ok());
    }
    for refused in [
        r"C:\Games\..\Game.exe",
        r"\\.\C:\Games\.\Game.exe",
        r"Games\Game.exe",
        r"C:\Games\Game.txt",
    ] {
        let documents = aggregate_with(json!([application(0, refused)]));
        assert!(!reopens(&documents, same), "{refused}");
        assert!(Storage::open(Box::new(Model::seeded(documents)), SID).is_err());
    }
    // Two paths the store's comparison holds to be one.
    let twice = json!([
        application(0, r"C:\Game.exe"),
        application(1, r"c:\GAME.EXE")
    ]);
    assert!(!reopens(&aggregate_with(twice), same));
    // A marker that does not commit the documents.
    let mut torn = Model::replay("committed");
    torn[2] = Model::replay("prior")[2].clone();
    assert!(!reopens(&torn, same));
}

const VECTORS: &str = include_str!("../../../../tests/data/profile-set-vectors-v27.json");

#[test]
fn the_store_finds_duplicate_paths_as_managed_does_whatever_its_reader_compares() {
    // A final sigma and a capital sigma: one path to managed, two to the
    // reader this store was opened with. The frame reader refuses the pair
    // first; the store must not depend on that.
    let model = Model::committed();
    let storage = model.storage();
    let mut request = requested(&model);
    let document = aggregate_with(json!([
        application(0, "C:\\\u{3c2}\\Game.exe"),
        application(1, "C:\\\u{3a3}\\Game.exe")
    ]));
    assert!(reopens(&document, str::eq_ignore_ascii_case));
    let document = json::parse(&document[0]).unwrap();
    request.application_profiles =
        update::decode_profiles(document.member("profiles").unwrap(), Dialect::Document)
            .unwrap()
            .application_profiles;

    assert_eq!(
        storage.save_profiles(&request).unwrap(),
        ProfileSave::Rejected(
            "The requested intent was rejected: The application profile 2 executable path is not normalized or is duplicated."
                .into()
        )
    );

    assert_eq!(model.writes.load(Ordering::Acquire), 0);
}

#[test]
fn an_aggregate_with_a_case_pair_managed_keeps_apart_still_saves() {
    // U+0264 and U+A7CB are a case pair in Unicode 16. Managed's table does
    // not join them, so it stores both paths; a comparison that did join
    // them would preserve this aggregate and block every save.
    let documents = aggregate_with(json!([
        application(0, "C:\\\u{264}\\Game.exe"),
        application(1, "C:\\\u{a7cb}\\Game.exe")
    ]));
    let installed: fn(&str, &str) -> bool = crate::installed::readiness::thermal_native::same_path;
    for reader in [installed, str::eq_ignore_ascii_case] {
        let model = Model::seeded(documents.clone());
        let storage = Storage::open_with(Box::new(model.clone()), SID, reader).unwrap();
        let request = requested(&model);
        assert_eq!(request.application_profiles.len(), 2);

        assert_eq!(
            storage.save_profiles(&request).unwrap(),
            ProfileSave::Stored
        );

        assert!(!storage.pending());
        let reopened = Storage::open_with(Box::new(model.clone()), SID, reader).unwrap();
        let saved = requested(&model);
        assert_eq!(saved.application_profiles, request.application_profiles);
        assert_eq!(reopened.save_profiles(&saved).unwrap(), ProfileSave::Stored);
    }
}

/// A reader that holds a dotless i to be an i, as a different case table
/// could: coarser than managed's comparison, which keeps the two apart.
fn coarser(left: &str, right: &str) -> bool {
    let fold = |path: &str| path.replace('\u{131}', "i").to_ascii_lowercase();
    fold(left) == fold(right)
}

#[test]
fn a_set_the_service_reader_would_refuse_is_rejected_before_anything_is_written() {
    let model = Model::committed();
    let prior = model.snapshot();
    let storage = Storage::open_with(Box::new(model.clone()), SID, coarser).unwrap();
    let mut request = requested(&model);
    let document = aggregate_with(json!([
        application(0, "C:\\\u{131}\\Game.exe"),
        application(1, r"C:\I\Game.exe")
    ]));
    let document = json::parse(&document[0]).unwrap();
    let applications = document.member("profiles").unwrap();
    request.application_profiles = update::decode_profiles(applications, Dialect::Document)
        .unwrap()
        .application_profiles;

    // Managed's comparison keeps the two paths apart, so the set is valid.
    assert_eq!(
        storage.save_profiles(&request).unwrap(),
        ProfileSave::Rejected(UNREADABLE.into())
    );

    assert_eq!(model.snapshot(), prior);
    assert_eq!(model.writes.load(Ordering::Acquire), 0);
    assert!(!storage.pending());
    // The store still opens and still saves what its reader accepts.
    let reopened = Storage::open_with(Box::new(model.clone()), SID, coarser).unwrap();
    assert_eq!(
        reopened.save_profiles(&requested(&model)).unwrap(),
        ProfileSave::Stored
    );
    // A reader that compares as managed does stores the same set.
    let model = Model::committed();
    assert_eq!(
        model.storage().save_profiles(&request).unwrap(),
        ProfileSave::Stored
    );
    assert!(Storage::open(Box::new(model.clone()), SID).is_ok());
}

#[test]
fn every_managed_path_vector_is_answered_and_read_back_with_the_installed_comparison() {
    let reader = crate::installed::readiness::thermal_native::same_path;
    let vectors = json::parse(VECTORS.as_bytes()).unwrap();
    let Some(json::Json::Array(cases)) = vectors.member("cases") else {
        panic!("the vectors hold cases");
    };
    let (mut stored, mut closed) = (0, 0);
    for case in cases {
        let name = case.member("name").and_then(json::Json::text).unwrap();
        if !name.contains("application path") {
            continue;
        }
        let Some(json::Json::Array(edits)) = case.member("edit") else {
            panic!("{name} edits the set");
        };
        let [json::Json::Array(edit)] = edits.as_slice() else {
            panic!("{name} sets the application profiles only");
        };
        let model = Model::committed();
        let prior = model.snapshot();
        let mut set = profile_set::encode::profile_set(&requested(&model));
        let json::Json::Object(members) = &mut set else {
            unreachable!()
        };
        for (member, value) in members.iter_mut() {
            if member == "applicationProfiles" {
                *value = edit[1].clone();
            }
        }
        let frame = json::write(&json::Json::Object(vec![("profiles".into(), set)]), false);
        let expect = case.member("expect").unwrap();

        let bound = crate::service_status::ProfilesBody::bind(&frame);

        if expect.member("decode") != Some(&json::Json::Bool(true)) {
            closed += 1;
            assert!(bound.is_err(), "{name}: managed closes the connection");
            continue;
        }
        let bound = bound.unwrap_or_else(|_| panic!("{name}: managed binds this set"));
        let storage = Storage::open_with(Box::new(model.clone()), SID, reader).unwrap();
        let outcome = storage.save_profiles(bound.profiles().unwrap()).unwrap();

        let update = expect.member("update").unwrap();
        let message = update.member("message").and_then(json::Json::text).unwrap();
        let managed = match update.member("status").unwrap().integer(0, 3).unwrap() {
            0 => ProfileSave::Stored,
            _ => ProfileSave::Rejected(message.into()),
        };
        assert_eq!(outcome, managed, "{name}");
        if outcome == ProfileSave::Stored {
            stored += 1;
        } else {
            assert_eq!(model.snapshot(), prior, "{name}: nothing was written");
        }
        // Whatever was answered, the service still reads its configuration.
        assert!(!storage.pending(), "{name}");
        let reopened = Storage::open_with(Box::new(model.clone()), SID, reader);
        let reopened = reopened.unwrap_or_else(|error| panic!("{name}: {error}"));
        reopened
            .profiles()
            .unwrap_or_else(|error| panic!("{name}: {error}"));
    }
    assert!(
        stored >= 20 && closed >= 25,
        "{stored} stored, {closed} closed"
    );
}

#[path = "profile_storage_real_tests.rs"]
mod real;

#[path = "profile_migration_tests.rs"]
mod migration;

#[cfg(feature = "service-profile-triggers-v1")]
#[path = "profile_storage_policy_tests.rs"]
mod policy;

/// The Fan Stop settings and Battery Saver saves commit through the same
/// transaction as a profile save, so every failure leaves one whole
/// aggregate, says whether anything is owed, and a restart reads it.
#[cfg(feature = "service-fan-stop-v1")]
mod fan_stop {
    use super::*;
    use openblade_domain::profile_set::preferences::FanStopSettings;

    fn custom() -> FanStopSettings {
        FanStopSettings {
            end_mode: 1,
            seconds_above_limit: 9,
            ..FanStopSettings::default()
        }
    }

    type Save = fn(&Storage) -> PreferencesSave;

    /// Each save, and the same request to a restarted store once it landed.
    fn saves() -> [(&'static str, Save, Save); 2] {
        [
            (
                "settings",
                |storage| storage.save_fan_stop_settings(custom()).unwrap(),
                |storage| {
                    assert_eq!(storage.fan_stop_settings().unwrap(), custom());
                    storage.save_fan_stop_settings(custom()).unwrap()
                },
            ),
            (
                "battery saver",
                |storage| storage.save_battery_saver_fan_stop(true).unwrap(),
                |storage| storage.save_battery_saver_fan_stop(true).unwrap(),
            ),
        ]
    }

    fn saved_documents(save: Save) -> [Option<Vec<u8>>; 3] {
        let model = Model::committed();
        assert!(matches!(save(&model.storage()), PreferencesSave::Stored(_)));
        model.documents()
    }

    #[test]
    fn every_failed_write_leaves_a_whole_aggregate_that_a_restart_reads() {
        let prior = Model::replay("prior").map(Some);
        for (name, save, reread) in saves() {
            let saved = saved_documents(save);
            assert_ne!(saved, prior, "{name}");
            for fault in [Fault::Before, Fault::After, Fault::Stranded] {
                // Only the staged replacements strand a stage.
                let writes = if fault == Fault::Stranded {
                    0..7
                } else {
                    0..11
                };
                for write in writes {
                    let model = Model::committed();
                    model.fail(write, fault);
                    let storage = model.storage();
                    let outcome = save(&storage);
                    let case = format!("{name} {fault:?} write {write}");
                    match write {
                        // Nothing moved, or the exact prior aggregate is
                        // restored and every stage taken back.
                        0..=6 => {
                            assert_eq!(
                                outcome,
                                PreferencesSave::Rejected(RESTORED.into()),
                                "{case}"
                            );
                            assert_eq!(model.documents(), prior, "{case}");
                            assert_eq!(model.snapshot().len(), 3, "{case}");
                            assert!(!storage.pending(), "{case}");
                        }
                        // The record could not be cleared: the store closes.
                        7 => {
                            assert_eq!(
                                outcome,
                                PreferencesSave::Rejected(RECOVERY_REQUIRED.into()),
                                "{case}"
                            );
                            assert_eq!(model.documents(), saved, "{case}");
                            assert!(storage.pending(), "{case}");
                            assert_eq!(save(&storage), PreferencesSave::Quarantined, "{case}");
                            assert_eq!(
                                storage.fan_stop_settings().unwrap(),
                                FanStopSettings::default(),
                                "{case}: a preserved aggregate shows the managed defaults"
                            );
                        }
                        // A backup could not be discarded: the save stands.
                        _ => {
                            assert!(matches!(outcome, PreferencesSave::Stored(_)), "{case}");
                            assert_eq!(model.documents(), saved, "{case}");
                        }
                    }
                    let documents = model.documents();
                    assert!(documents == prior || documents == saved, "{case}");
                    // A restarted Service reads what is durable: the record
                    // keeps it closed; otherwise a landed save is unchanged
                    // and the prior aggregate takes the save again.
                    let restarted = Storage::open(Box::new(model.clone()), SID);
                    if model.snapshot().contains_key(RECORD) {
                        assert!(restarted.is_err(), "{case}");
                        continue;
                    }
                    let restarted = restarted.unwrap();
                    if documents == saved {
                        assert!(
                            matches!(reread(&restarted), PreferencesSave::Unchanged(_)),
                            "{case}"
                        );
                    } else {
                        assert!(
                            matches!(save(&restarted), PreferencesSave::Stored(_)),
                            "{case}"
                        );
                        assert_eq!(model.documents(), saved, "{case}");
                    }
                }
            }
        }
    }

    #[test]
    fn a_stage_that_cannot_be_removed_refuses_the_save_and_says_so() {
        for (name, save, _) in saves() {
            let model = Model::committed();
            model.strand(NAMES[1]);
            *model.held.lock().unwrap() = Some(NAMES[1].into());
            let storage = model.storage();
            let before = model.snapshot();

            assert_eq!(
                save(&storage),
                PreferencesSave::Rejected(STAGE_BLOCKED.into()),
                "{name}"
            );
            assert_eq!(model.snapshot(), before, "{name}");
            assert_eq!(model.writes.load(Ordering::Acquire), 0, "{name}");
            assert!(!storage.pending(), "{name}");

            *model.held.lock().unwrap() = None;
            assert!(
                matches!(save(&storage), PreferencesSave::Stored(_)),
                "{name}"
            );
            assert_eq!(model.snapshot().len(), 3, "{name}");
        }
    }

    #[test]
    fn a_preserved_aggregate_refuses_both_saves_untouched() {
        type Tamper = fn(&mut BTreeMap<String, Vec<u8>>);
        let tampers: [Tamper; 2] = [
            // Another writer recorded a transaction.
            |files| drop(files.insert(RECORD.into(), b"{}".to_vec())),
            // A document no longer matches the digest in the marker.
            |files| files.get_mut(NAMES[0]).unwrap().push(b' '),
        ];
        for (name, save, _) in saves() {
            for tamper in tampers {
                // A store opened after the change does not open at all; one
                // opened before it refuses to replace the aggregate.
                let model = Model::committed();
                let storage = model.storage();
                tamper(&mut model.bytes.lock().unwrap());
                assert!(
                    Storage::open(Box::new(model.clone()), SID).is_err(),
                    "{name}"
                );
                let before = model.snapshot();

                assert_eq!(save(&storage), PreferencesSave::Quarantined, "{name}");
                assert_eq!(model.snapshot(), before, "{name}");
                assert_eq!(model.writes.load(Ordering::Acquire), 0, "{name}");
                assert_eq!(
                    storage.fan_stop_settings().unwrap(),
                    FanStopSettings::default(),
                    "{name}"
                );
            }
        }
    }

    #[test]
    fn a_battery_saver_save_keeps_the_committed_preference_bytes() {
        // Managed `SaveProfilesAsync` writes the profiles and leaves the
        // preference document as it was; only the profile document and the
        // marker change. A trailing newline makes the committed bytes differ
        // from the canonical encoding of the same preferences, so a save that
        // re-encoded them would show.
        let [profile, mut preferences, _] = Model::replay("prior");
        preferences.push(b'\n');
        let marker = preferences::commit_marker(
            profile_set::CURRENT_SCHEMA,
            &configuration::digest_hex(&profile),
            &configuration::digest_hex(&preferences),
        );
        let model = Model::seeded([profile, preferences, marker]);
        let prior = model.documents();
        assert!(matches!(
            model.storage().save_battery_saver_fan_stop(true).unwrap(),
            PreferencesSave::Stored(_)
        ));
        let saved = model.documents();
        assert_ne!(saved[0], prior[0]);
        assert_eq!(saved[1], prior[1]);
        assert_ne!(saved[2], prior[2]);
    }

    #[test]
    fn an_unchanged_value_is_not_written() {
        let model = Model::committed();
        let storage = model.storage();
        let settings = storage.fan_stop_settings().unwrap();
        assert!(matches!(
            storage.save_fan_stop_settings(settings).unwrap(),
            PreferencesSave::Unchanged(_)
        ));
        assert!(matches!(
            storage.save_battery_saver_fan_stop(false).unwrap(),
            PreferencesSave::Unchanged(_)
        ));
        assert_eq!(model.writes.load(Ordering::Acquire), 0);
    }
}

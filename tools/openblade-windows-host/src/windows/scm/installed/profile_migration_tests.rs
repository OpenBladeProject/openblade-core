//! Migration when the store opens, as managed's load migrates: every managed
//! vector against the in-memory store, then a failure and a crash at each
//! durable step of the recorded transaction. `real` runs the same opens on
//! files.
use super::*;

const MIGRATIONS: &str = include_str!("../../../../tests/data/profile-migration-vectors.json");

/// One vector case: the files it stores, under their names.
pub(super) struct Case {
    pub(super) name: String,
    pub(super) files: BTreeMap<String, Vec<u8>>,
    expect: Value,
    /// A declared parity difference: managed's outcome is recorded, and
    /// this store refuses the open with every file untouched.
    native_difference: bool,
}

/// The bytes the managed recorder stores: the raw text, or the value written
/// compactly by the domain writer, which keeps its member order and number
/// text as `JsonNode.ToJsonString` does.
fn stored(case: &Value, document: &str) -> Option<Vec<u8>> {
    if let Some(text) = case.get(format!("{document}Text")) {
        return Some(text.as_str().unwrap().as_bytes().to_vec());
    }
    let value = case.get(document)?;
    let value = json::parse(&serde_json::to_vec(value).unwrap()).unwrap();
    Some(json::write(&value, false))
}

/// The rows the domain reader admits; this reader trusts its shape.
fn cases() -> Vec<Case> {
    let root: Value = serde_json::from_str(MIGRATIONS).unwrap();
    root["cases"]
        .as_array()
        .unwrap()
        .iter()
        .map(|case| {
            let mut files = BTreeMap::new();
            let profile = stored(case, "profile");
            let preferences = stored(case, "preferences");
            let marker = match (case.get("markerText"), case.get("schema")) {
                (Some(text), _) => Some(text.as_str().unwrap().as_bytes().to_vec()),
                (None, Some(schema)) => Some(commit_marker(
                    schema.as_i64().unwrap() as i32,
                    &sha256(profile.as_deref().unwrap_or_default()),
                    &sha256(preferences.as_deref().unwrap_or_default()),
                )),
                (None, None) => None,
            };
            for (name, bytes) in NAMES.into_iter().zip([profile, preferences, marker]) {
                if let Some(bytes) = bytes {
                    files.insert(name.to_owned(), bytes);
                }
            }
            Case {
                name: case["name"].as_str().unwrap().to_owned(),
                files,
                expect: case["expect"].clone(),
                native_difference: case.get("nativeDifference").is_some(),
            }
        })
        .collect()
}

pub(super) fn case(name: &str) -> Case {
    cases().into_iter().find(|case| case.name == name).unwrap()
}

fn model(files: &BTreeMap<String, Vec<u8>>) -> Model {
    let model = Model::default();
    *model.bytes.lock().unwrap() = files.clone();
    model
}

fn open(model: &Model) -> io::Result<Arc<Storage>> {
    Storage::open(Box::new(model.clone()), SID)
}

/// What the store holds once a clean open has migrated `files`.
pub(super) fn migrated(files: &BTreeMap<String, Vec<u8>>) -> BTreeMap<String, Vec<u8>> {
    let model = model(files);
    open(&model).unwrap();
    model.snapshot()
}

fn listing(files: &BTreeMap<String, Vec<u8>>) -> Vec<String> {
    files.keys().cloned().collect()
}

#[test]
fn every_managed_store_open_leaves_what_managed_leaves() {
    let (mut migrations, mut preserved, mut differences) = (0, 0, 0);
    for case in cases() {
        let name = &case.name;
        let model = model(&case.files);
        let opened = open(&model);
        let after = model.snapshot();
        let expect = &case.expect;
        if case.native_difference {
            differences += 1;
            assert!(opened.is_err(), "{name}: the store stays closed");
            assert_eq!(after, case.files, "{name}: the stored files are untouched");
            assert_eq!(model.writes.load(Ordering::Acquire), 0, "{name}");
            continue;
        }
        if expect["migrated"] == json!(true) {
            migrations += 1;
            assert!(opened.is_ok(), "{name}: {:?}", opened.err());
            let files: Vec<String> = serde_json::from_value(expect["files"].clone()).unwrap();
            assert_eq!(listing(&after), files, "{name}: only the documents remain");
            for (document, key) in
                NAMES
                    .into_iter()
                    .zip(["profileSha256", "preferencesSha256", "markerSha256"])
            {
                assert_eq!(
                    sha256(&after[document]),
                    expect[key].as_str().unwrap(),
                    "{name}: {document}"
                );
            }
            // A migrated aggregate is current: opening again changes nothing.
            let writes = model.writes.load(Ordering::Acquire);
            open(&model).unwrap();
            assert_eq!(model.snapshot(), after, "{name}");
            assert_eq!(model.writes.load(Ordering::Acquire), writes, "{name}");
            continue;
        }
        // Left alone, quarantined or a failed load: every byte stays.
        preserved += 1;
        assert_eq!(after, case.files, "{name}: the stored files are untouched");
        assert_eq!(model.writes.load(Ordering::Acquire), 0, "{name}");
        let schema = case
            .files
            .get(NAMES[2])
            .and_then(|marker| serde_json::from_slice::<Value>(marker).ok())
            .and_then(|marker| marker["schemaVersion"].as_i64());
        // This store reads schema 25 and newer itself; anything older that
        // managed preserves, and every legacy profile, keeps it closed.
        if expect["admitted"] != json!(true) && schema.is_none_or(|schema| schema < 25) {
            assert!(opened.is_err(), "{name}: the store stays closed");
        }
    }
    assert!(
        migrations >= 45 && preserved >= 50,
        "{migrations} {preserved}"
    );
    assert_eq!(differences, 5);
}

#[test]
fn a_future_or_corrupt_aggregate_is_refused_untouched() {
    for name in [
        "schema 28 is a future schema",
        "a future preference schema beside schema 24",
        "legacy with a future schema version",
        "schema 24 profile that is not JSON",
        "legacy that is not JSON",
        "schema 24 marker with another profile digest",
    ] {
        let case = case(name);
        let model = model(&case.files);
        assert!(open(&model).is_err(), "{name}");
        assert_eq!(model.snapshot(), case.files, "{name}");
        assert!(
            model.log.lock().unwrap().is_empty(),
            "{name}: nothing written"
        );
    }
}

/// The documents of a migrated aggregate.
fn documents(files: &BTreeMap<String, Vec<u8>>) -> [Option<Vec<u8>>; 3] {
    NAMES.map(|name| files.get(name).cloned())
}

/// The sources the crash and failure runs migrate: every prior document, and
/// the legacy profile alone.
fn sources() -> [Case; 2] {
    [
        case("schema 24 committed aggregate"),
        case("legacy profile"),
    ]
}

#[test]
fn the_migration_is_the_recorded_transaction_in_managed_order() {
    for source in sources() {
        let model = model(&source.files);
        open(&model).unwrap();
        let id = transaction_id(&model);
        let backup = |name: &str| format!("replace {name}.transaction-{id}.backup");
        let priors: Vec<&str> = NAMES
            .into_iter()
            .filter(|name| source.files.contains_key(*name))
            .collect();
        let mut expected: Vec<String> = priors.iter().map(|name| backup(name)).collect();
        expected.push(format!("replace {RECORD}"));
        expected.extend(NAMES.map(|name| format!("replace {name}")));
        expected.push(format!("remove {RECORD}"));
        expected.extend(
            priors
                .iter()
                .map(|name| backup(name).replacen("replace", "remove", 1)),
        );
        assert_eq!(*model.log.lock().unwrap(), expected, "{}", source.name);
    }
}

#[test]
fn any_single_failed_write_leaves_the_prior_or_the_migrated_aggregate() {
    for source in sources() {
        let intended = migrated(&source.files);
        let writes = migrated_writes(&source.files);
        for fault in [
            Fault::Before,
            Fault::After,
            Fault::Wrong,
            Fault::Lost,
            Fault::Padded,
            Fault::Stranded,
        ] {
            for write in 0..writes {
                let case = format!("{}: {fault:?} write {write}", source.name);
                let model = model(&source.files);
                model.fail(write, fault);
                let opened = open(&model);
                let files = model.snapshot();
                if files.contains_key(RECORD) {
                    // A restore that could not be confirmed keeps the record.
                    assert!(opened.is_err(), "{case}");
                    assert_preserved(&source.files, &intended, &files, &case);
                    continue;
                }
                // Without a record the documents are one whole aggregate,
                // and the next open finishes the migration.
                let whole = documents(&files);
                assert!(
                    whole == documents(&source.files) || whole == documents(&intended),
                    "{case}"
                );
                // A failed cleanup is reported even where the migration landed.
                if whole == documents(&source.files) {
                    assert!(opened.is_err(), "{case}");
                }
                model.faults.lock().unwrap().clear();
                open(&model).unwrap();
                assert_eq!(documents(&model.snapshot()), documents(&intended), "{case}");
            }
        }
    }
}

/// How many writes a clean migration of `files` makes.
fn migrated_writes(files: &BTreeMap<String, Vec<u8>>) -> usize {
    let model = model(files);
    open(&model).unwrap();
    model.writes.load(Ordering::Acquire)
}

/// A recorded transaction: the store stays closed, every backup is the exact
/// prior document, and each document is its prior or its migrated bytes.
fn assert_preserved(
    prior: &BTreeMap<String, Vec<u8>>,
    intended: &BTreeMap<String, Vec<u8>>,
    files: &BTreeMap<String, Vec<u8>>,
    case: &str,
) {
    for name in NAMES {
        let backup = files
            .iter()
            .find(|(file, _)| file.starts_with(name) && file.ends_with(".backup"))
            .map(|(_, bytes)| bytes);
        if let Some(prior) = prior.get(name) {
            // The original is never lost: in place, or in its backup.
            assert!(
                files.get(name) == Some(prior) || backup == Some(prior),
                "{case}: {name}"
            );
        }
        if let Some(backup) = backup {
            assert_eq!(Some(backup), prior.get(name), "{case}: {name} backup");
        }
        let document = files.get(name);
        assert!(
            document == prior.get(name) || document == intended.get(name),
            "{case}: {name} is the prior or the migrated document"
        );
    }
}

#[test]
fn a_crash_at_any_durable_step_reopens_or_preserves_the_original() {
    for source in sources() {
        let intended = migrated(&source.files);
        let writes = migrated_writes(&source.files);
        for fault in [Fault::Before, Fault::After, Fault::Stranded] {
            for write in 0..writes {
                let case = format!("{}: crash at write {write} ({fault:?})", source.name);
                let model = model(&source.files);
                // The process stops at this write: nothing after it lands,
                // neither the restore nor any cleanup.
                model.fail(write, fault);
                for later in write + 1..write + 32 {
                    model.fail(later, Fault::Before);
                }
                let _ = open(&model);
                let crashed = model.snapshot();
                model.faults.lock().unwrap().clear();

                let reopened = open(&model);
                let files = model.snapshot();
                if crashed.contains_key(RECORD) {
                    // Stage written, record written, committed or not, the
                    // record preserves the aggregate and the backups for
                    // review, exactly as managed leaves it.
                    assert!(reopened.is_err(), "{case}");
                    assert_eq!(files, crashed, "{case}: the restart changes nothing");
                    assert_preserved(&source.files, &intended, &files, &case);
                    continue;
                }
                // Before the record or after its removal: the restart
                // migrates, or finds the migration committed.
                assert!(reopened.is_ok(), "{case}: {:?}", reopened.err());
                assert_eq!(documents(&files), documents(&intended), "{case}");
                assert!(
                    !files.keys().any(|name| name.ends_with(".pending")),
                    "{case}: no stage remains"
                );
                for (name, bytes) in files.iter().filter(|(name, _)| name.ends_with(".backup")) {
                    // Only a crash in the final cleanup leaves a backup,
                    // and it is still the exact original.
                    let document = name.split(".transaction-").next().unwrap();
                    assert_eq!(Some(bytes), source.files.get(document), "{case}: {name}");
                }
                let writes = model.writes.load(Ordering::Acquire);
                open(&model).unwrap();
                assert_eq!(model.snapshot(), files, "{case}: reopening is idempotent");
                assert_eq!(model.writes.load(Ordering::Acquire), writes, "{case}");
            }
        }
    }
}

#[test]
fn a_crash_point_names_each_durable_step() {
    // The steps the crash run covers, by the write it stops at, for the
    // committed source: three backups, the record, the commit and cleanup.
    let source = case("schema 24 committed aggregate");
    let model = model(&source.files);
    open(&model).unwrap();
    let log = model.log.lock().unwrap().clone();
    assert_eq!(log.len(), 11);
    assert!(log[..3].iter().all(|entry| entry.ends_with(".backup")));
    assert_eq!(log[3], format!("replace {RECORD}"));
    // The marker replacement is the commit.
    assert_eq!(log[6], format!("replace {}", NAMES[2]));
    assert_eq!(log[7], format!("remove {RECORD}"));
    assert!(log[8..].iter().all(|entry| entry.starts_with("remove ")));
}

#[test]
fn a_stage_an_earlier_attempt_left_is_removed_before_the_migration() {
    for source in sources() {
        for name in [RECORD, NAMES[0], NAMES[1], NAMES[2]] {
            let mut files = source.files.clone();
            files.insert(stage(name), b"{\"schemaVersion\":".to_vec());
            let model = model(&files);

            open(&model).unwrap();

            assert_eq!(
                model.snapshot(),
                migrated(&source.files),
                "{}: {name}",
                source.name
            );
        }
    }
}

/// Managed `CheckRecoveryRequired` stops the load before it reads anything,
/// so a recorded transaction beside a migratable aggregate keeps every file,
/// a leftover stage included. The record's own exclusive creation would
/// refuse the migration later, after its stages were already discarded.
#[test]
fn a_recorded_transaction_is_never_migrated_or_cleaned() {
    for source in sources() {
        let mut files = source.files.clone();
        files.insert(RECORD.into(), b"{}".to_vec());
        files.insert(stage(NAMES[0]), b"{\"schemaVersion\":".to_vec());
        let model = model(&files);

        let error = open(&model).err().unwrap();

        assert_ne!(error.to_string(), MIGRATION_UNCHANGED, "{}", source.name);
        assert_eq!(model.snapshot(), files, "{}", source.name);
    }
}

#[test]
fn a_stage_that_cannot_be_removed_keeps_the_store_closed_and_unchanged() {
    let source = case("legacy profile");
    let mut files = source.files.clone();
    files.insert(stage(NAMES[1]), b"{".to_vec());
    let model = model(&files);
    *model.held.lock().unwrap() = Some(NAMES[1].into());

    let error = open(&model).err().unwrap();

    assert_eq!(error.to_string(), MIGRATION_UNCHANGED);
    assert_eq!(model.snapshot(), files);
    model.held.lock().unwrap().take();
    open(&model).unwrap();
    assert_eq!(model.snapshot(), migrated(&source.files));
}

#[test]
fn a_restore_that_cannot_be_confirmed_keeps_the_record_and_says_so() {
    let source = case("schema 24 committed aggregate");
    let model = model(&source.files);
    // The profile lands and reports failure; its restore is refused.
    model.fail(4, Fault::After);
    model.fail(5, Fault::Before);

    let error = open(&model).err().unwrap();

    assert_eq!(error.to_string(), MIGRATION_RECOVERY_REQUIRED);
    let files = model.snapshot();
    assert!(files.contains_key(RECORD));
    assert_preserved(&source.files, &migrated(&source.files), &files, "restore");
    model.faults.lock().unwrap().clear();
    assert!(open(&model).is_err());
    assert_eq!(model.snapshot(), files);
}

#[test]
fn a_migration_this_service_could_not_read_back_is_not_written() {
    // Two application profiles this coarser reader holds to be one path:
    // migrated, the store could not open on them, so they stay as they are.
    let source = case("schema 24 committed aggregate");
    let mut profile: Value = serde_json::from_slice(&source.files[NAMES[0]]).unwrap();
    profile["profiles"]["applicationProfiles"] = json!([
        application(0, "C:\\\u{131}\\Game.exe"),
        application(1, "C:\\i\\Game.exe")
    ]);
    let mut files = source.files.clone();
    let profile = serde_json::to_vec(&profile).unwrap();
    let marker = commit_marker(24, &sha256(&profile), &sha256(&files[NAMES[1]]));
    files.insert(NAMES[0].into(), profile);
    files.insert(NAMES[2].into(), marker);
    let ascii = model(&files);
    assert!(Storage::open_with(Box::new(ascii.clone()), SID, str::eq_ignore_ascii_case).is_ok());
    assert_ne!(ascii.snapshot(), files, "another comparison migrates them");

    let refused = model(&files);
    assert!(Storage::open_with(Box::new(refused.clone()), SID, coarser).is_err());

    assert_eq!(refused.snapshot(), files);
    assert!(refused.log.lock().unwrap().is_empty());
}

#[test]
fn an_absent_aggregate_is_still_materialised_by_the_first_save() {
    // Managed materialises it when it loads; this store leaves that to the
    // first save, which has always done it.
    let model = Model::default();
    open(&model).unwrap();
    assert!(model.snapshot().is_empty());
}

#[test]
fn a_migrated_legacy_profile_saves_like_any_aggregate() {
    let source = case("legacy profile");
    let model = model(&source.files);
    let storage = open(&model).unwrap();
    let mut request = requested(&model);
    request.battery_policy = Some(BatteryPolicy {
        charge_limit_percent: 70,
    });

    assert_eq!(
        storage.save_profiles(&request).unwrap(),
        ProfileSave::Stored
    );

    let document = json::parse(&model.documents()[0].clone().unwrap()).unwrap();
    let saved =
        update::decode_profiles(document.member("profiles").unwrap(), Dialect::Document).unwrap();
    assert_eq!(saved.battery_policy, request.battery_policy);
    assert_eq!(model.snapshot().len(), 3);
    // The migrated preferences are carried over byte for byte.
    assert_eq!(
        model.snapshot()[NAMES[1]],
        migrated(&source.files)[NAMES[1]]
    );
}

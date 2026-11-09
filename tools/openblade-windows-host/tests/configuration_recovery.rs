use openblade_windows_host::configuration::{
    COMMIT, CURRENT_SCHEMA, File, Inspection, PREFERENCES, PROFILE, RECOVERY_REQUIRED, Reason,
    RecoveryRecord, Snapshot, inspect,
};
use serde::Deserialize;
use serde_json::Value;
use sha2::{Digest, Sha256};
use std::collections::BTreeMap;

#[derive(Deserialize)]
struct Fixture {
    legacy: String,
    prior: [String; 3],
    committed: [String; 3],
    recovery: Value,
}

fn fixture() -> Fixture {
    serde_json::from_str(include_str!(
        "../../../tests/OpenBlade.Service.Tests/WindowsReplay/v1/configuration-aggregate.json"
    ))
    .unwrap()
}

// All reads use fixed names in a fake store. Artifact paths inside the managed
// record are deliberately never resolved, even during repeated restart.
#[derive(Clone, Debug, Default, Eq, PartialEq)]
struct FakeStorage(BTreeMap<&'static str, Vec<u8>>);

impl FakeStorage {
    fn observe(&self) -> Snapshot<'_> {
        let read = |name| self.0.get(name).map_or(File::Missing, |b| File::Bytes(b));
        Snapshot {
            recovery: if self.0.contains_key(RECOVERY_REQUIRED) {
                RecoveryRecord::Present
            } else {
                RecoveryRecord::Missing
            },
            profile: read(PROFILE),
            preferences: read(PREFERENCES),
            commit: read(COMMIT),
        }
    }

    fn put(&mut self, name: &'static str, bytes: impl AsRef<[u8]>) {
        self.0.insert(name, bytes.as_ref().to_vec());
    }

    fn replace(&mut self, targets: &[String; 3], count: usize) {
        for (name, document) in [PROFILE, PREFERENCES, COMMIT]
            .into_iter()
            .zip(targets)
            .take(count)
        {
            self.put(name, document);
        }
    }
}

const COMMITTED: Inspection = Inspection::CommittedEnvelope {
    schema: CURRENT_SCHEMA,
};

#[test]
fn managed_fixture_empty_legacy_and_verified_envelopes() {
    let f = fixture();
    let mut storage = FakeStorage::default();
    assert_eq!(inspect(storage.observe()), Inspection::Empty);
    storage.put(PROFILE, f.legacy);
    assert_eq!(inspect(storage.observe()), Inspection::LegacyCandidate);
    storage.replace(&f.prior, 1);
    assert_eq!(
        inspect(storage.observe()),
        Inspection::Preserve(Reason::IncompleteAggregate)
    );
    storage.replace(&f.prior, 2);
    assert_eq!(
        inspect(storage.observe()),
        Inspection::Preserve(Reason::IncompleteAggregate)
    );
    storage.replace(&f.prior, 3);
    assert_eq!(inspect(storage.observe()), COMMITTED);
    storage.replace(&f.committed, 3);
    assert_eq!(inspect(storage.observe()), COMMITTED);
}

#[test]
fn managed_sidecar_blocks_every_replacement_boundary_and_restart() {
    let f = fixture();
    for prior in [false, true] {
        for replaced in 0..=3 {
            let mut storage = FakeStorage::default();
            if prior {
                storage.replace(&f.prior, 3);
            }
            storage.put(RECOVERY_REQUIRED, serde_json::to_vec(&f.recovery).unwrap());
            storage.replace(&f.committed, replaced);
            let before = storage.clone();
            for _restart in 0..2 {
                assert_eq!(
                    inspect(storage.observe()),
                    Inspection::Preserve(Reason::RecoveryRequired)
                );
                assert_eq!(storage, before);
            }
        }
    }
}

#[test]
fn opaque_records_and_unknown_reads_fail_closed() {
    let f = fixture();
    let mut storage = FakeStorage::default();
    storage.replace(&f.committed, 3);
    for record in [
        b"".as_slice(),
        b"{broken",
        br#"{"schemaVersion":99,"targets":[{"path":"..\\newer.json"}]}"#,
    ] {
        storage.put(RECOVERY_REQUIRED, record);
        assert_eq!(
            inspect(storage.observe()),
            Inspection::Preserve(Reason::RecoveryRequired)
        );
    }
    let mut snapshot = storage.observe();
    snapshot.recovery = RecoveryRecord::Unavailable;
    assert_eq!(
        inspect(snapshot),
        Inspection::Preserve(Reason::StorageUnavailable)
    );
    for files in [
        [File::Unavailable, snapshot.preferences, snapshot.commit],
        [snapshot.profile, File::Unavailable, snapshot.commit],
        [snapshot.profile, snapshot.preferences, File::Unavailable],
    ] {
        let [profile, preferences, commit] = files;
        snapshot = Snapshot {
            recovery: RecoveryRecord::Missing,
            profile,
            preferences,
            commit,
        };
        assert_eq!(
            inspect(snapshot),
            Inspection::Preserve(Reason::StorageUnavailable)
        );
        snapshot.recovery = RecoveryRecord::Present;
        assert_eq!(
            inspect(snapshot),
            Inspection::Preserve(Reason::RecoveryRequired)
        );
    }
}

#[test]
fn stale_marker_and_external_edits_are_preserved_byte_for_byte() {
    let f = fixture();
    let mut storage = FakeStorage::default();
    storage.replace(&f.prior, 3);
    storage.replace(&f.committed, 2);
    assert_eq!(
        inspect(storage.observe()),
        Inspection::Preserve(Reason::HashMismatch)
    );

    storage.replace(&f.committed, 3);
    storage.0.get_mut(PREFERENCES).unwrap().push(b'\n');
    let before = storage.clone();
    assert_eq!(
        inspect(storage.observe()),
        Inspection::Preserve(Reason::HashMismatch)
    );
    assert_eq!(storage, before);

    storage.put(PROFILE, br#"{"schemaVersion":26,"schemaVersion":25}"#);
    assert_eq!(
        inspect(storage.observe()),
        Inspection::Preserve(Reason::FutureSchema)
    );
    storage.put(RECOVERY_REQUIRED, b"");
    assert_eq!(
        inspect(storage.observe()),
        Inspection::Preserve(Reason::RecoveryRequired)
    );
}

fn with_marker(profile: &str, preferences: &str, marker_schema: &str) -> FakeStorage {
    let mut storage = FakeStorage::default();
    storage.put(PROFILE, profile);
    storage.put(PREFERENCES, preferences);
    storage.put(
        COMMIT,
        format!(
            r#"{{"schemaVersion":{marker_schema},"profileSha256":"{:X}","preferencesSha256":"{:X}"}}"#,
            Sha256::digest(profile.as_bytes()),
            Sha256::digest(preferences.as_bytes())
        ),
    );
    storage
}

#[test]
fn envelope_schema_and_hash_rules_preserve_numeric_and_legacy_meanings() {
    let f = fixture();
    let [profile, prefs, _] = &f.committed;
    let old_profile = profile.replacen(r#""schemaVersion": 25"#, r#""schemaVersion": 24"#, 1);
    let old_prefs = prefs.replacen(r#""schemaVersion": 25"#, r#""schemaVersion": 24"#, 1);
    assert_eq!(
        inspect(with_marker(&old_profile, &old_prefs, "24").observe()),
        Inspection::CommittedEnvelope { schema: 24 }
    );
    assert_eq!(
        inspect(with_marker(&old_profile, prefs, "25").observe()),
        Inspection::Preserve(Reason::MixedSchema)
    );
    assert_eq!(
        inspect(with_marker(profile, &old_prefs, "25").observe()),
        Inspection::Preserve(Reason::InvalidDocument)
    );

    let quoted_prefs = prefs.replacen(r#""schemaVersion": 25"#, r#""schemaVersion": "25""#, 1);
    assert_eq!(
        inspect(with_marker(profile, &quoted_prefs, r#""25""#).observe()),
        COMMITTED
    );
    assert_eq!(
        inspect(with_marker(profile, prefs, "25.0").observe()),
        Inspection::Preserve(Reason::InvalidDocument)
    );
    assert_eq!(
        inspect(with_marker(profile, prefs, "0").observe()),
        Inspection::Preserve(Reason::InvalidDocument)
    );
    assert_eq!(
        inspect(with_marker(profile, prefs, "26").observe()),
        Inspection::Preserve(Reason::FutureSchema)
    );

    let mut storage = with_marker(profile, prefs, "25");
    let expected = String::from_utf8(storage.0[COMMIT].clone()).unwrap();
    let digest = format!("{:X}", Sha256::digest(profile.as_bytes()));
    for replacement in [digest.to_lowercase(), digest[..63].to_owned()] {
        storage.put(COMMIT, expected.replace(&digest, &replacement));
        assert_eq!(
            inspect(storage.observe()),
            Inspection::Preserve(Reason::HashMismatch)
        );
    }
    storage.put(COMMIT, expected.replace(&format!(r#""{digest}""#), "null"));
    assert_eq!(
        inspect(storage.observe()),
        Inspection::Preserve(Reason::HashMismatch)
    );
    storage.put(
        COMMIT,
        expected.replace("preferencesSha256", "foreignDigest"),
    );
    assert_eq!(
        inspect(storage.observe()),
        Inspection::Preserve(Reason::InvalidDocument)
    );
}

#[test]
fn malformed_or_duplicate_hashed_documents_remain_invalid() {
    let f = fixture();
    for broken in [
        r#"{"schemaVersion":25,"profiles":{"nested":[{"red":0,"r\u0065d":1}]}}"#,
        r#"{"schemaVersion":25,"profiles":null}"#,
        r#"{"schemaVersion":25,"profiles":{}}{}"#,
        r#"{"schemaVersion":25,"profiles":{"bad":"\uD800"}}"#,
        r#"{"schemaVersion":25,"profiles":{}"#,
        "",
    ] {
        assert_eq!(
            inspect(with_marker(broken, &f.committed[1], "25").observe()),
            Inspection::Preserve(Reason::InvalidDocument),
            "{broken}"
        );
    }
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct InspectionVector {
    id: String,
    profile: Option<String>,
    preferences: Option<String>,
    profile_schema: Option<String>,
    preferences_schema: Option<String>,
    marker_schema: Option<String>,
    #[serde(default)]
    profile_only: bool,
    managed: String,
    rust: Option<String>,
}

#[test]
fn inspection_matches_checked_managed_edge_cases() {
    let f = fixture();
    let vectors: Vec<InspectionVector> = serde_json::from_str(include_str!(
        "../../../tests/OpenBlade.Service.Tests/WindowsReplay/v1/configuration-inspection.json"
    ))
    .unwrap();
    for vector in vectors {
        let document = |raw: Option<String>, schema: Option<String>, original: &str| {
            raw.unwrap_or_else(|| match schema {
                Some(schema) => original.replacen(
                    r#""schemaVersion": 25"#,
                    &format!(r#""schemaVersion": {schema}"#),
                    1,
                ),
                None => original.to_owned(),
            })
        };
        let profile = document(vector.profile, vector.profile_schema, &f.committed[0]);
        let preferences = document(
            vector.preferences,
            vector.preferences_schema,
            &f.committed[1],
        );
        let storage = if vector.profile_only {
            let mut storage = FakeStorage::default();
            storage.put(PROFILE, profile);
            storage
        } else {
            with_marker(
                &profile,
                &preferences,
                vector.marker_schema.as_deref().unwrap_or("25"),
            )
        };
        let actual = match inspect(storage.observe()) {
            Inspection::CommittedEnvelope { .. } => "CommittedEnvelope".to_owned(),
            Inspection::Preserve(reason) => format!("{reason:?}"),
            other => panic!("{} returned {other:?}", vector.id),
        };
        assert_eq!(
            actual,
            vector.rust.as_ref().unwrap_or(&vector.managed).as_str(),
            "{}",
            vector.id
        );
    }
}

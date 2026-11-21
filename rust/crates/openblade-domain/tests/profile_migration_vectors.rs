//! The managed vectors for migrating a stored aggregate when the store opens.
//!
//! `ServiceControlRuntimeTests.NativeProfileMigrationVectors` records what
//! `BatteryConfigurationStore.LoadAsync` leaves on disk for each set of
//! stored files, and verifies the same file; the host's store-open tests
//! read it too. Here each case the payload reader sees is migrated with
//! `profile_set::migrate`: the schema-less legacy profile alone, or a
//! committed aggregate at the marker's schema. A case only the envelope
//! decides (a missing file, a marker of other bytes) is the host's.
//!
//! A case with `nativeDifference` is a declared parity difference: managed's
//! outcome stays its expectation, and this reader refuses it as invalid.
//!
//! The rewritten documents are compared by SHA-256, which `thermal-history`
//! brings; run with both features.
#![cfg(all(feature = "profile-set", feature = "thermal-history"))]

mod common;

use common::{member, validator};
use openblade_domain::profile_set::json::{self, Json};
use openblade_domain::profile_set::{Refusal, Stored, migrate};
use sha2::{Digest, Sha256};

const VECTORS: &str = include_str!(
    "../../../../tools/openblade-windows-host/tests/data/profile-migration-vectors.json"
);

const STORED: [&str; 6] = [
    "profile",
    "profileText",
    "preferences",
    "preferencesText",
    "schema",
    "markerText",
];

/// The file shape, checked before any row runs, as the managed reader
/// checks it: an unknown or misspelt member would otherwise be ignored and
/// its case silently weakened.
fn read_vectors(text: &str) -> Result<Json, String> {
    let root = json::parse(text.as_bytes()).map_err(|_| "malformed JSON".to_owned())?;
    shape(&root, &["comment", "cases"], &["comment", "cases"])?;
    let Json::Array(cases) = member(&root, "cases") else {
        return Err("the cases are an array".into());
    };
    let mut names = Vec::new();
    for case in cases {
        let allowed: Vec<&str> = ["name", "showDocument", "nativeDifference", "expect"]
            .into_iter()
            .chain(STORED)
            .collect();
        shape(case, &allowed, &["name"])?;
        match member(case, "name").text() {
            Some(name) if !name.is_empty() && !names.contains(&name) => names.push(name),
            _ => return Err("a case has a unique, non-empty name".into()),
        }
        for (document, raw) in [
            ("profile", "profileText"),
            ("preferences", "preferencesText"),
            ("schema", "markerText"),
        ] {
            if case.member(document).is_some() && case.member(raw).is_some() {
                return Err(format!("a case gives {document} or {raw}, not both"));
            }
            if case.member(raw).is_some_and(|text| text.text().is_none()) {
                return Err(format!("{raw} is a string"));
            }
        }
        if case
            .member("schema")
            .is_some_and(|schema| schema.integer(i32::MIN.into(), i32::MAX.into()).is_none())
        {
            return Err("a schema is an integer".into());
        }
        if case
            .member("showDocument")
            .is_some_and(|flag| flag.boolean().is_none())
        {
            return Err("showDocument is true or false".into());
        }
        if case
            .member("nativeDifference")
            .is_some_and(|reason| reason.text().is_none_or(str::is_empty))
        {
            return Err("nativeDifference is a non-empty string".into());
        }
        if !STORED.iter().any(|name| case.member(name).is_some()) {
            return Err("a case stores at least one file".into());
        }
    }
    Ok(root)
}

fn shape(value: &Json, allowed: &[&str], required: &[&str]) -> Result<(), String> {
    let members = value
        .members()
        .ok_or_else(|| format!("expected an object: {value:?}"))?;
    if let Some((name, _)) = members
        .iter()
        .find(|(name, _)| !allowed.contains(&name.as_str()))
    {
        return Err(format!("unexpected member {name:?}"));
    }
    match required.iter().find(|name| value.member(name).is_none()) {
        Some(name) => Err(format!("missing member {name:?}")),
        None => Ok(()),
    }
}

/// The bytes a case stores for `document`, as the managed recorder writes
/// them: the raw text, or the value written compactly.
fn document(case: &Json, document: &str) -> Option<Vec<u8>> {
    match (
        case.member(&format!("{document}Text")),
        case.member(document),
    ) {
        (Some(text), _) => Some(text.text().unwrap().as_bytes().to_vec()),
        (None, Some(value)) => Some(json::write(value, false)),
        (None, None) => None,
    }
}

fn sha256(bytes: &[u8]) -> String {
    format!("{:X}", Sha256::digest(bytes))
}

#[test]
fn every_managed_store_open_migrates_alike() {
    let root = read_vectors(VECTORS).unwrap();
    let Json::Array(cases) = member(&root, "cases") else {
        unreachable!()
    };
    let validator = validator();
    let mut failures = Vec::new();
    let (mut legacy, mut migrations, mut kept, mut refusals, mut envelope) = (0, 0, 0, 0, 0);
    let mut differences = 0;
    let mut schemas = std::collections::BTreeSet::new();
    for case in cases {
        let name = member(case, "name").text().unwrap();
        let expect = member(case, "expect");
        let profile = document(case, "profile");
        let preferences = document(case, "preferences");
        let schema = case
            .member("schema")
            .and_then(|schema| schema.integer(i32::MIN.into(), i32::MAX.into()))
            .map(|schema| schema as i32);
        let stored = match (&profile, &preferences, schema, case.member("markerText")) {
            (Some(profile), None, None, None) => {
                legacy += 1;
                Stored::Legacy { profile }
            }
            (Some(profile), Some(preferences), Some(schema), None) => Stored::Committed {
                schema,
                profile,
                preferences,
            },
            _ => {
                envelope += 1;
                continue;
            }
        };
        let observed = migrate(stored, &validator);
        let mut fail = |what: &str, native: String, managed: String| {
            failures.push(format!(
                "{name}: {what}\n  native:  {native}\n  managed: {managed}"
            ));
        };
        if case.member("nativeDifference").is_some() {
            differences += 1;
            if !matches!(observed, Err(Refusal::Invalid(_))) {
                fail(
                    "declared difference",
                    format!("{observed:?}"),
                    "Invalid".into(),
                );
            }
            continue;
        }
        if member(expect, "admitted") != &Json::Bool(true) {
            refusals += 1;
            let reason = member(expect, "reason").text().unwrap_or("throws");
            let agrees = match (&observed, reason) {
                (Err(Refusal::FutureSchema), "FutureSchema") => true,
                (Err(Refusal::IncompleteAggregate), "IncompleteAggregate") => true,
                // The marker's schema is the reader's; a document that
                // declares another is a mixed aggregate to managed.
                (Err(Refusal::Invalid(_)), "InvalidDocument" | "MixedSchema" | "throws") => true,
                _ => false,
            };
            if !agrees {
                fail("refusal", format!("{observed:?}"), reason.into());
            }
            continue;
        }
        let migration = match observed {
            Ok(migration) => migration,
            Err(refusal) => {
                fail("admitted", format!("{refusal:?}"), "admitted".into());
                continue;
            }
        };
        let migrated = member(expect, "migrated") == &Json::Bool(true);
        let Some(migration) = migration else {
            if migrated {
                fail("migrated", "left alone".into(), "migrated".into());
            }
            kept += 1;
            continue;
        };
        if !migrated {
            fail("migrated", "migrated".into(), "left alone".into());
            continue;
        }
        migrations += 1;
        schemas.extend(migration.schema);
        for (bytes, what) in
            migration
                .documents(sha256)
                .iter()
                .zip(["profile", "preferences", "marker"])
        {
            let text = String::from_utf8(bytes.clone()).unwrap();
            if let Some(shown) = member(expect, &format!("{what}Document")).text()
                && text != shown
            {
                fail(what, text.clone(), shown.into());
            }
            let expected = member(expect, &format!("{what}Sha256")).text().unwrap();
            if sha256(bytes) != expected {
                fail(
                    what,
                    format!("{} for {text}", sha256(bytes)),
                    expected.into(),
                );
            }
        }
        // Managed's migration leaves exactly the three documents.
        let Json::Array(files) = member(expect, "files") else {
            panic!("{name}: the files are recorded");
        };
        assert_eq!(files.len(), 3, "{name}");
    }
    assert!(
        failures.is_empty(),
        "{} mismatches:\n{}",
        failures.len(),
        failures.join("\n")
    );
    // Every schema managed migrates is migrated, the current one included
    // when its normalisation changes it, and the legacy profile.
    assert_eq!(schemas, (1..=27).collect());
    assert!(legacy >= 50 && migrations >= 45 && kept >= 1 && refusals >= 50 && envelope >= 4);
    assert_eq!(differences, 10);
}

#[test]
fn malformed_or_unknown_rows_are_rejected() {
    for text in [
        r#"{"comment":"","cases":[],"extra":1}"#,
        r#"{"comment":"","cases":{}}"#,
        r#"{"cases":[]}"#,
        r#"{"comment":"","cases":[{"profile":{}}]}"#,
        r#"{"comment":"","cases":[{"name":"","profile":{}}]}"#,
        r#"{"comment":"","cases":[{"name":"x"}]}"#,
        r#"{"comment":"","cases":[{"name":"x","profile":{},"profileText":"{}"}]}"#,
        r#"{"comment":"","cases":[{"name":"x","preferences":{},"preferencesText":"{}"}]}"#,
        r#"{"comment":"","cases":[{"name":"x","profile":{},"schema":1,"markerText":"{}"}]}"#,
        r#"{"comment":"","cases":[{"name":"x","profile":{},"schema":"1"}]}"#,
        r#"{"comment":"","cases":[{"name":"x","profile":{},"schema":1.5}]}"#,
        r#"{"comment":"","cases":[{"name":"x","profileText":{}}]}"#,
        r#"{"comment":"","cases":[{"name":"x","profile":{},"showDocument":1}]}"#,
        r#"{"comment":"","cases":[{"name":"x","profile":{},"expected":{}}]}"#,
        r#"{"comment":"","cases":[{"name":"x","profile":{},"nativeDifference":true}]}"#,
        r#"{"comment":"","cases":[{"name":"x","profile":{},"nativeDifference":""}]}"#,
        r#"{"comment":"","cases":[{"name":"x","profile":{}},{"name":"x","profile":{}}]}"#,
    ] {
        assert!(read_vectors(text).is_err(), "{text}");
    }
    let minimal = r#"{"comment":"","cases":[{"name":"x","profileText":"{}","preferences":{},"schema":27,"showDocument":false,"expect":{}},{"name":"y","markerText":"{}"}]}"#;
    assert!(read_vectors(minimal).is_ok());
}

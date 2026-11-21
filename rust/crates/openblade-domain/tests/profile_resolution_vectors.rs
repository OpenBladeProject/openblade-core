//! The managed vectors for native active application-profile resolution.
//!
//! `ServiceControlRuntimeTests.NativeProfileResolutionVectors` records each
//! case from `ProfileAutomationState` and each intent row from the real
//! `UpdateProfilesAsync`, and verifies the same file. A step marked
//! `nativeDifference` is one native refuses where managed accepts; the note
//! says why.
#![cfg(feature = "profile-set")]

mod common;

use common::{apply, member, validator};
use openblade_domain::application_context::{Intent, intent_changed};
use openblade_domain::profile_set::application::{
    self, ApplicationContext, Resolution, Unnormalized,
};
use openblade_domain::profile_set::json::{self, Json};
use openblade_domain::profile_set::{Dialect, PowerSource, ProfileSet, encode, normalize, update};

const VECTORS: &str = include_str!(
    "../../../../tools/openblade-windows-host/tests/data/profile-resolution-vectors.json"
);

/// The file shape, checked before any row runs, as the managed reader checks
/// it: an unknown or misspelt member would otherwise be ignored and its case
/// silently weakened.
fn read_vectors(text: &str) -> Result<Json, String> {
    let root = json::parse(text.as_bytes()).map_err(|_| "malformed JSON".to_owned())?;
    let all = ["comment", "base", "cases", "intent"];
    shape(&root, &all, &all)?;
    for case in rows(member(&root, "cases"))? {
        shape(
            case,
            &["name", "power", "profiles", "steps", "expect"],
            &["name", "power", "steps"],
        )?;
        power(member(case, "power"))?;
        edits(member(case, "profiles"))?;
        for step in rows(member(case, "steps"))? {
            shape(
                step,
                &["report", "update", "power", "nativeDifference"],
                &[],
            )?;
            let kinds: Vec<&str> = step
                .members()
                .unwrap_or_default()
                .iter()
                .map(|(name, _)| name.as_str())
                .filter(|name| *name != "nativeDifference")
                .collect();
            match kinds.as_slice() {
                ["report"] => reported_path(member(step, "report"))?,
                ["update"] => edits(member(step, "update"))?,
                ["power"] => power(member(step, "power")).map(drop)?,
                _ => return Err("a step is exactly one report, update or power change".into()),
            }
        }
    }
    for row in rows(member(&root, "intent"))? {
        shape(
            row,
            &["name", "profiles", "report", "update", "expect"],
            &["name", "report", "update"],
        )?;
        edits(member(row, "profiles"))?;
        reported_path(member(row, "report"))?;
        edits(member(row, "update"))?;
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

fn rows(value: &Json) -> Result<&[Json], String> {
    match value {
        Json::Array(items) => Ok(items),
        _ => Err("expected an array".into()),
    }
}

fn edits(value: &Json) -> Result<(), String> {
    match value {
        Json::Null | Json::Array(_) => Ok(()),
        _ => Err("profile edits are an array".into()),
    }
}

fn reported_path(value: &Json) -> Result<(), String> {
    match value {
        Json::Null | Json::String(_) => Ok(()),
        _ => Err("a reported path is a string or null".into()),
    }
}

fn power(value: &Json) -> Result<Option<PowerSource>, String> {
    match value.integer(0, 3) {
        Some(0) => Ok(Some(PowerSource::PluggedIn)),
        Some(1) => Ok(Some(PowerSource::Battery)),
        Some(2) => Ok(Some(PowerSource::UsbC)),
        Some(3) => Ok(None),
        _ => Err("a power source is 0 to 3".into()),
    }
}

fn name(row: &Json) -> &str {
    member(row, "name").text().unwrap()
}

/// A saved set: decoded from the document form, already normalised and
/// accepted by the production validator, as managed's reader asserts.
fn saved(base: &Json, edits: &Json, case: &str) -> ProfileSet {
    let mut profiles = update::decode_profiles(&apply(base, edits), Dialect::Document)
        .unwrap_or_else(|_| panic!("{case}: the saved set decodes"));
    let migrated = normalize::normalize_legacy_battery_profile(&mut profiles, None).unwrap();
    assert!(!migrated, "{case}: the saved set is already normalised");
    validator()
        .validate_stored_custom_intent(&profiles)
        .unwrap_or_else(|error| panic!("{case}: {error:?}"));
    profiles
}

fn project(resolution: &Resolution<'_>) -> [Json; 3] {
    [
        resolution
            .application_profile_id
            .map_or(Json::Null, Json::string),
        resolution
            .desired
            .as_deref()
            .map_or(Json::Null, encode::power_profile),
        encode::laptop_behavior(resolution.laptop_behavior),
    ]
}

fn base() -> Json {
    let root = read_vectors(VECTORS).unwrap();
    let base = member(&root, "base").clone();
    // The base is exactly what the native codec reads and writes back.
    let decoded = update::decode_profiles(&base, Dialect::Document).unwrap();
    assert_eq!(encode::profile_set(&decoded), base);
    base
}

#[test]
fn every_managed_case_selects_retains_and_resolves_alike() {
    let root = read_vectors(VECTORS).unwrap();
    let base = base();
    let (mut steps, mut differences) = (0, 0);
    for case in rows(member(&root, "cases")).unwrap() {
        let case_name = name(case);
        let mut profiles = saved(&base, member(case, "profiles"), case_name);
        let mut source = power(member(case, "power")).unwrap();
        let mut context = ApplicationContext::default();
        let expected = rows(member(case, "expect")).unwrap();
        let operations = rows(member(case, "steps")).unwrap();
        assert_eq!(operations.len(), expected.len(), "{case_name}");
        for (index, (step, expect)) in operations.iter().zip(expected).enumerate() {
            let at = format!("{case_name} step {index}");
            let prior = context.active().map(|active| active.id.to_owned());
            if let Some(report) = step.member("report") {
                let reported = application::report(&mut context, &profiles, report.text());
                if step.member("nativeDifference").is_some() {
                    assert_eq!(reported, Err(Unnormalized), "{at}");
                    assert_eq!(index + 1, operations.len(), "{at}: a refusal ends its case");
                    differences += 1;
                    break;
                }
                assert_eq!(
                    Json::Bool(reported.unwrap()),
                    *member(expect, "changed"),
                    "{at}"
                );
            } else if let Some(edits) = step.member("update") {
                profiles = saved(&base, edits, &at);
                let dropped = application::update(&mut context, &profiles);
                assert_eq!(
                    dropped,
                    prior.is_some() && member(expect, "activeId").is_null(),
                    "{at}"
                );
            } else {
                source = power(member(step, "power")).unwrap();
            }
            let resolution = application::resolve(&context, &profiles, source).unwrap();
            let [id, desired, behavior] = project(&resolution);
            assert_eq!(id, *member(expect, "activeId"), "{at}");
            assert_eq!(desired, *member(expect, "desired"), "{at}");
            assert_eq!(behavior, *member(expect, "laptopBehavior"), "{at}");
            steps += 1;
        }
    }
    // Guard against a file that silently lost its rows.
    assert_eq!((steps, differences), (44, 1));
}

#[test]
fn every_managed_save_changes_the_active_intent_alike() {
    let root = read_vectors(VECTORS).unwrap();
    let base = base();
    let mut branches = [false; 5];
    for row in rows(member(&root, "intent")).unwrap() {
        let row_name = name(row);
        let expect = member(row, "expect");
        let flag = |name| member(expect, name).boolean().unwrap();
        let mut profiles = saved(&base, member(row, "profiles"), row_name);
        let mut context = ApplicationContext::default();
        application::report(&mut context, &profiles, member(row, "report").text()).unwrap();
        let source = Some(PowerSource::PluggedIn);
        let before = application::resolve(&context, &profiles, source).unwrap();
        let (prior_id, prior_behavior) = (
            before.application_profile_id.map(str::to_owned),
            before.laptop_behavior,
        );
        profiles = saved(&base, member(row, "update"), row_name);
        application::update(&mut context, &profiles);
        let after = application::resolve(&context, &profiles, source).unwrap();

        // The selection and behaviour are this crate's; managed's full desired
        // state is the host's, so its comparison is taken as recorded.
        let id = |value: Option<&str>| value.map_or(Json::Null, Json::string);
        assert_eq!(
            id(prior_id.as_deref()),
            *member(expect, "priorId"),
            "{row_name}"
        );
        assert_eq!(
            id(after.application_profile_id),
            *member(expect, "id"),
            "{row_name}"
        );
        assert_eq!(
            after.laptop_behavior != prior_behavior,
            flag("laptopBehaviorChanged"),
            "{row_name}"
        );
        let prior = Intent {
            application_profile_id: prior_id.as_deref(),
            desired: false,
            laptop_behavior: prior_behavior,
        };
        let next = Intent {
            application_profile_id: after.application_profile_id,
            desired: flag("desiredChanged"),
            laptop_behavior: after.laptop_behavior,
        };
        let changed = intent_changed(&prior, &next);
        assert_eq!(changed, flag("intentChanged"), "{row_name}");

        branches[match () {
            () if prior.application_profile_id.is_none() => 0,
            () if next.application_profile_id != prior.application_profile_id => 1,
            () if next.desired => 2,
            () if next.laptop_behavior != prior.laptop_behavior => 3,
            () => 4,
        }] = true;
    }
    assert_eq!(
        branches, [true; 5],
        "every branch of the predicate is recorded"
    );
}

#[test]
fn malformed_or_unknown_rows_are_rejected() {
    for text in [
        r#"{"comment":"","base":{},"cases":[],"intent":[],"extra":1}"#,
        r#"{"comment":"","base":{},"cases":[]}"#,
        r#"{"comment":"","base":{},"cases":[{"name":"x","power":4,"steps":[]}],"intent":[]}"#,
        r#"{"comment":"","base":{},"cases":[{"name":"x","power":0,"steps":[],"expected":[]}],"intent":[]}"#,
        r#"{"comment":"","base":{},"cases":[{"name":"x","power":0,"steps":[{"report":null,"power":1}]}],"intent":[]}"#,
        r#"{"comment":"","base":{},"cases":[{"name":"x","power":0,"steps":[{"sleep":1}]}],"intent":[]}"#,
        r#"{"comment":"","base":{},"cases":[{"name":"x","power":0,"steps":[{"report":7}]}],"intent":[]}"#,
        r#"{"comment":"","base":{},"cases":[{"name":"x","power":0,"steps":[{"update":{}}]}],"intent":[]}"#,
        r#"{"comment":"","base":{},"cases":[{"name":"x","power":"0","steps":[]}],"intent":[]}"#,
        r#"{"comment":"","base":{},"cases":[],"intent":[{"name":"x","report":null}]}"#,
        r#"{"comment":"","base":{},"cases":[],"intent":[{"name":"x","report":null,"update":[],"power":0}]}"#,
    ] {
        assert!(read_vectors(text).is_err(), "{text}");
    }
    let minimal = r#"{"comment":"","base":{},"cases":[{"name":"x","power":3,"steps":[{"report":null},{"update":[]},{"power":0}]}],"intent":[{"name":"x","report":"C:\\a.exe","update":[]}]}"#;
    assert!(read_vectors(minimal).is_ok());
}

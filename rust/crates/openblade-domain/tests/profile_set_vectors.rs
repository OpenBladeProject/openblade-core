//! The managed vectors for the native `ProfileSet` contract.
//!
//! `ServiceControlRuntimeTests.NativeProfileVectors` records every
//! expectation from the managed IPC codec, the production RZ09-0581
//! validator and the real `UpdateProfilesAsync` path, and verifies the same
//! file. A case marked `nativeDifference` is one where native refuses a frame
//! managed accepts; the note says why.
//!
//! The stored documents are compared by SHA-256, which `thermal-history`
//! brings; run with both features.
#![cfg(all(feature = "profile-set", feature = "thermal-history"))]

use openblade_domain::profile_set::json::{self, Json};
use openblade_domain::profile_set::{
    CURRENT_SCHEMA, Capabilities, Dialect, ProfileSet, ValidationPolicy, Validator, encode,
    normalize, read_profile_document, update,
};
use sha2::{Digest, Sha256};

const VECTORS: &str = include_str!(
    "../../../../tools/openblade-windows-host/tests/data/profile-set-vectors-v27.json"
);
const CAPABILITIES: &str = include_str!(
    "../../../../tools/openblade-windows-host/tests/data/profile-capabilities-rz09-0581.json"
);

/// Ordinal case-insensitive equality, which the host takes from Windows.
fn same_path(left: &str, right: &str) -> bool {
    left.to_uppercase() == right.to_uppercase()
}

fn member<'a>(value: &'a Json, name: &str) -> &'a Json {
    value.member(name).unwrap_or(&Json::Null)
}

fn step<'a>(parent: &'a mut Json, key: &Json) -> &'a mut Json {
    match (parent, key) {
        (Json::Object(members), Json::String(name)) => {
            &mut members
                .iter_mut()
                .find(|(member, _)| member == name)
                .expect(name)
                .1
        }
        (Json::Array(items), Json::Number(index)) => &mut items[index.parse::<usize>().unwrap()],
        other => panic!("unreachable path step {other:?}"),
    }
}

/// Each edit is `[path, value]` to set a member or `[path]` to remove it.
fn apply(base: &Json, edits: &Json) -> Json {
    let mut result = base.clone();
    let Json::Array(edits) = edits else {
        return result;
    };
    for edit in edits {
        let (Json::Array(pair), Some(Json::Array(path))) = (edit, edit_path(edit)) else {
            panic!("malformed edit");
        };
        let Some((Json::String(name), parents)) = path.split_last() else {
            panic!("an edit names a member");
        };
        let mut parent = &mut result;
        for key in parents {
            parent = step(parent, key);
        }
        let Json::Object(members) = parent else {
            panic!("an edit targets an object");
        };
        let at = members.iter().position(|(member, _)| member == name);
        match (pair.get(1), at) {
            (Some(value), Some(at)) => members[at].1 = value.clone(),
            (Some(value), None) => members.push((name.clone(), value.clone())),
            (None, Some(at)) => {
                members.remove(at);
            }
            (None, None) => {}
        }
    }
    result
}

fn edit_path(edit: &Json) -> Option<&Json> {
    match edit {
        Json::Array(pair) => pair.first(),
        _ => None,
    }
}

fn text(value: &Json) -> Option<String> {
    value.text().map(str::to_owned)
}

fn validator() -> Validator {
    let capabilities = Capabilities::rz09_0581();
    Validator::new(
        ValidationPolicy::for_capabilities(&capabilities, &capabilities),
        same_path,
    )
}

fn frame(profiles: Json) -> Vec<u8> {
    json::write(
        &Json::Object(vec![
            ("operation".into(), Json::string("updateProfiles")),
            ("version".into(), Json::int(85)),
            (
                "requestId".into(),
                Json::string("aaaaaaaa-bbbb-cccc-dddd-000000000001"),
            ),
            ("profiles".into(), profiles),
        ]),
        false,
    )
}

#[test]
fn capabilities_are_the_managed_rz09_0581_composition() {
    let capabilities = Capabilities::rz09_0581();
    let sorted = |mut values: Vec<i32>| {
        values.sort_unstable();
        Json::Array(
            values
                .into_iter()
                .map(|value| Json::int(value.into()))
                .collect(),
        )
    };
    let range = |range: openblade_domain::profile_set::policy::FanSpeedRange| {
        Json::Object(vec![
            ("minimumRpm".into(), Json::int(range.minimum_rpm.into())),
            ("maximumRpm".into(), Json::int(range.maximum_rpm.into())),
            ("stepRpm".into(), Json::int(range.step_rpm.into())),
        ])
    };
    let mut combinations = capabilities
        .custom_performance
        .as_ref()
        .map(|custom| custom.allowed_combinations.clone())
        .unwrap_or_default();
    combinations.sort();
    let observed = Json::Object(vec![
        (
            "performancePresets".into(),
            sorted(
                capabilities
                    .performance_presets
                    .iter()
                    .map(|value| value.0)
                    .collect(),
            ),
        ),
        (
            "fanModes".into(),
            sorted(capabilities.fan_modes.iter().map(|value| value.0).collect()),
        ),
        (
            "lightingEffects".into(),
            sorted(
                capabilities
                    .lighting_effects
                    .iter()
                    .map(|value| value.0)
                    .collect(),
            ),
        ),
        (
            "fanSpeedRange".into(),
            capabilities.fan_speed_range.map_or(Json::Null, range),
        ),
        (
            "fanSpeedRangesByPerformancePreset".into(),
            Json::Array(
                capabilities
                    .fan_speed_ranges_by_performance_preset
                    .iter()
                    .map(|(preset, limits)| {
                        Json::Object(vec![
                            ("performance".into(), Json::int(preset.0.into())),
                            ("range".into(), range(*limits)),
                        ])
                    })
                    .collect(),
            ),
        ),
        (
            "customPerformance".into(),
            capabilities
                .custom_performance
                .as_ref()
                .map_or(Json::Null, |custom| {
                    Json::Object(vec![
                        (
                            "allowedCombinations".into(),
                            Json::Array(
                                combinations
                                    .iter()
                                    .map(|(cpu, gpu)| {
                                        Json::Array(vec![
                                            Json::int(cpu.0.into()),
                                            Json::int(gpu.0.into()),
                                        ])
                                    })
                                    .collect(),
                            ),
                        ),
                        (
                            "supportsMaximumFans".into(),
                            Json::Bool(custom.supports_maximum_fans),
                        ),
                    ])
                }),
        ),
        (
            "keyboardLightingLayoutId".into(),
            capabilities
                .keyboard_lighting_layout_id
                .map_or(Json::Null, Json::string),
        ),
        (
            "supportsBreathingColorSelection".into(),
            Json::Bool(capabilities.supports_breathing_color_selection),
        ),
        (
            "supportedBreathingColorModes".into(),
            capabilities
                .supported_breathing_color_modes
                .as_ref()
                .map_or(Json::Null, |modes| {
                    sorted(modes.iter().map(|mode| mode.0).collect())
                }),
        ),
        (
            "supportsWaveDirectionSelection".into(),
            Json::Bool(capabilities.supports_wave_direction_selection),
        ),
        (
            "supportsIndependentLightingControls".into(),
            Json::Bool(capabilities.supports_independent_lighting_controls),
        ),
        (
            "supportsVaporChamberLighting".into(),
            Json::Bool(capabilities.supports_vapor_chamber_lighting),
        ),
        (
            "supportsTouchpadStatusIndicator".into(),
            Json::Bool(capabilities.supports_touchpad_status_indicator),
        ),
        (
            "reportsPerformanceWithManualFans".into(),
            Json::Bool(capabilities.reports_performance_with_manual_fans),
        ),
        (
            "supportsManualFansOnLimitedPower".into(),
            Json::Bool(capabilities.supports_manual_fans_on_limited_power),
        ),
        (
            "supportsBatterySaverFanStop".into(),
            Json::Bool(capabilities.supports_battery_saver_fan_stop),
        ),
    ]);
    assert_eq!(json::parse(CAPABILITIES.as_bytes()).unwrap(), observed);
}

#[test]
fn every_managed_vector_decodes_validates_and_stores_alike() {
    let root = json::parse(VECTORS.as_bytes()).unwrap();
    let base = member(&root, "base");
    let Json::Array(cases) = member(&root, "cases") else {
        panic!("the vectors hold cases");
    };
    let validator = validator();
    let capabilities = Capabilities::rz09_0581();
    let mut failures = Vec::new();
    let (mut stored, mut rejected, mut refused, mut differences) = (0, 0, 0, 0);
    for case in cases {
        let name = text(member(case, "name")).unwrap();
        let expect = member(case, "expect");
        let update = member(expect, "update");
        let decoded = update::decode_request(&frame(apply(base, member(case, "edit"))), same_path);
        let mut check = |what: &str, observed: String, expected: String| {
            if observed != expected {
                failures.push(format!(
                    "{name}: {what}\n  native:  {observed}\n  managed: {expected}"
                ));
            }
        };

        // Managed has no reply for a frame it refuses or throws on.
        let native_refuses =
            case.member("nativeDifference").is_some() || update.member("throws").is_some();
        if member(expect, "decode") == &Json::Bool(false) || native_refuses {
            refused += 1;
            differences += usize::from(native_refuses);
            check("decode", format!("{}", decoded.is_ok()), "false".into());
            continue;
        }
        let Ok(profiles) = decoded else {
            check("decode", "false".into(), "true".into());
            continue;
        };

        let message = |validate: fn(&Validator, &ProfileSet) -> Result<(), normalize::Invalid>| {
            let mut normalized = profiles.clone();
            normalize::normalize_legacy_battery_profile(&mut normalized, None)
                .and_then(|_| validate(&validator, &normalized))
                .err()
                .map(|reason| reason.0)
        };
        check(
            "stored",
            format!("{:?}", message(Validator::validate_stored_custom_intent)),
            format!("{:?}", text(member(expect, "stored"))),
        );
        check(
            "full",
            format!("{:?}", message(Validator::validate)),
            format!("{:?}", text(member(expect, "full"))),
        );

        let mut saved =
            update::decode_profiles(&apply(base, member(case, "saved")), Dialect::Document)
                .unwrap();
        normalize::normalize_legacy_battery_profile(&mut saved, None).unwrap();
        let (status, reply, digest, document) = match update::admit(
            profiles.clone(),
            &saved,
            &validator,
            &validator,
            Some(&capabilities),
        ) {
            Ok(admitted) => {
                let document = encode::profile_document(CURRENT_SCHEMA, &admitted);
                // What is written reads back as the same set, unmigrated.
                let reread = read_profile_document(&document, true, &validator).unwrap();
                assert_eq!(reread.profiles, admitted, "{name}");
                assert!(!reread.migrated, "{name}");
                stored += 1;
                (
                    "0",
                    update::STORED_MESSAGE.to_owned(),
                    Some(format!("{:X}", Sha256::digest(&document))),
                    Some(String::from_utf8(document).unwrap()),
                )
            }
            Err(reason) => {
                rejected += 1;
                ("3", update::rejection_message(&reason), None, None)
            }
        };
        check(
            "update",
            format!("{status} {reply}"),
            format!(
                "{} {}",
                member(update, "status").integer(0, 3).unwrap(),
                text(member(update, "message")).unwrap()
            ),
        );
        if let Some(shown) = text(member(expect, "document")) {
            check("document", document.clone().unwrap_or_default(), shown);
        }
        // A digest alone cannot show what differs, so print the native bytes.
        let expected = text(member(expect, "documentSha256"));
        if digest != expected {
            check(
                "documentSha256",
                format!("{digest:?} for {}", document.unwrap_or_default()),
                format!("{expected:?}"),
            );
        }
    }
    assert!(
        failures.is_empty(),
        "{} mismatches:\n{}",
        failures.len(),
        failures.join("\n")
    );
    // The corpus keeps exercising each outcome.
    assert!(stored >= 60 && rejected >= 80 && refused >= 90 && differences >= 2);
}

const AGGREGATES: &str = include_str!(
    "../../../../tools/openblade-windows-host/tests/data/profile-aggregate-vectors.json"
);

fn digest(bytes: &[u8]) -> String {
    format!("{:X}", Sha256::digest(bytes))
}

/// `BatteryConfigurationStore.LoadAsync` over a committed aggregate at each
/// schema: what is admitted, what is rewritten, and the rewritten bytes.
#[test]
fn every_managed_aggregate_loads_and_migrates_alike() {
    use openblade_domain::profile_set::preferences::{
        commit_marker, preferences_document, read_aggregate,
    };
    let root = json::parse(AGGREGATES.as_bytes()).unwrap();
    let Json::Array(cases) = member(&root, "cases") else {
        panic!("the vectors hold cases");
    };
    let validator = validator();
    let mut failures = Vec::new();
    let (mut kept, mut migrations, mut refusals) = (0, 0, 0);
    for case in cases {
        let name = text(member(case, "name")).unwrap();
        let expect = member(case, "expect");
        let schema = member(case, "schema").integer(1, 27).unwrap() as i32;
        let observed = read_aggregate(
            schema,
            &json::write(member(case, "profile"), false),
            &json::write(member(case, "preferences"), false),
            &validator,
        );
        let mut check = |what: &str, observed: String, expected: String| {
            if observed != expected {
                failures.push(format!(
                    "{name}: {what}\n  native:  {observed}\n  managed: {expected}"
                ));
            }
        };
        // Managed quarantines the aggregate, or fails the load outright.
        if member(expect, "admitted") != &Json::Bool(true) {
            refusals += 1;
            if observed.is_ok() {
                check("admitted", "admitted".into(), "refused".into());
            }
            continue;
        }
        let aggregate = match observed {
            Ok(aggregate) => aggregate,
            Err(reason) => {
                check("admitted", reason.0, "admitted".into());
                continue;
            }
        };
        let migrated = member(expect, "migrated") == &Json::Bool(true);
        check(
            "migrated",
            aggregate.migrated.to_string(),
            migrated.to_string(),
        );
        if !migrated {
            kept += 1;
            continue;
        }
        migrations += 1;
        let profile = encode::profile_document(CURRENT_SCHEMA, &aggregate.profiles);
        let preferences = preferences_document(CURRENT_SCHEMA, &aggregate.preferences);
        let marker = commit_marker(CURRENT_SCHEMA, &digest(&profile), &digest(&preferences));
        for (what, shown, bytes) in [
            ("profile", "profileDocument", &profile),
            ("preferences", "preferencesDocument", &preferences),
            ("marker", "", &marker),
        ] {
            let document = String::from_utf8(bytes.clone()).unwrap();
            if let Some(shown) = text(member(expect, shown)) {
                check("document", document.clone(), shown);
            }
            let expected = text(member(expect, &format!("{what}Sha256"))).unwrap();
            if digest(bytes) != expected {
                check(what, format!("{} for {document}", digest(bytes)), expected);
            }
        }
    }
    assert!(
        failures.is_empty(),
        "{} mismatches:\n{}",
        failures.len(),
        failures.join("\n")
    );
    assert!(kept >= 5 && migrations >= 35 && refusals >= 35);
}

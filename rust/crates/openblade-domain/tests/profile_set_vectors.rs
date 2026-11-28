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

mod common;

use common::{apply, member, validator};
use openblade_domain::profile_set::json::{self, Json};
use openblade_domain::profile_set::{
    CURRENT_SCHEMA, Capabilities, Dialect, ProfileSet, Validator, encode, normalize,
    read_profile_document, update,
};
use sha2::{Digest, Sha256};

const VECTORS: &str = include_str!(
    "../../../../tools/openblade-windows-host/tests/data/profile-set-vectors-v27.json"
);
const CAPABILITIES: &str = include_str!(
    "../../../../tools/openblade-windows-host/tests/data/profile-capabilities-rz09-0581.json"
);
const ORDINAL_CASE: &str = include_str!(
    "../../../../tools/openblade-windows-host/tests/data/profile-ordinal-ignore-case.json"
);
/// Set to rewrite the compiled case table from the recorded vector. The
/// managed tests take the same variable to record the vectors.
const REGENERATE: &str = "OPENBLADE_REGENERATE_NATIVE_PROFILE_VECTORS";

use openblade_domain::profile_set::text::{
    fold_ordinal_case, same_ordinal_ignoring_case as same_path,
};

fn text(value: &Json) -> Option<String> {
    value.text().map(str::to_owned)
}

fn frame(profiles: Json) -> Vec<u8> {
    json::write(
        &Json::Object(vec![
            ("operation".into(), Json::string("updateProfiles")),
            ("version".into(), Json::int(91)),
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

/// The compiled table for `folds`, each character with the one it folds to:
/// runs of evenly spaced characters that fold by the same distance.
fn ordinal_case_table(folds: &[(u32, u32)]) -> String {
    let mut runs: Vec<(u32, u32, u32, u32)> = Vec::new();
    for &(from, to) in folds {
        if let Some((first, last, step, folded)) = runs.last_mut() {
            let gap = from - *last;
            let aligned = to.wrapping_sub(*folded) == from - *first;
            let spaced = if first == last {
                gap <= 2
            } else {
                gap == *step
            };
            if aligned && spaced {
                (*last, *step) = (from, gap);
                continue;
            }
        }
        runs.push((from, from, 1, to));
    }
    let mut table = String::from(
        "//! Managed `StringComparer.OrdinalIgnoreCase` as a table. Generated from\n\
         //! `tools/openblade-windows-host/tests/data/profile-ordinal-ignore-case.json`,\n\
         //! which a managed test records. Do not edit it: run the domain vector tests\n\
         //! with `OPENBLADE_REGENERATE_NATIVE_PROFILE_VECTORS` set to rewrite it.\n\n\
         /// `(first, last, step, folded)`: every `step`-th character from `first` to\n\
         /// `last` folds to the character as far from `folded` as it is from `first`.\n\
         /// Sorted, and no two entries overlap.\n\
         pub(super) const FOLDS: &[(u32, u32, u32, u32)] = &[\n",
    );
    for (first, last, step, folded) in runs {
        table.push_str(&format!(
            "    (0x{first:04X}, 0x{last:04X}, {step}, 0x{folded:04X}),\n"
        ));
    }
    table.push_str("];\n");
    table
}

#[test]
fn every_character_compares_as_managed_ordinal_ignore_case_records() {
    let vector = json::parse(ORDINAL_CASE.as_bytes()).expect("the vector parses");
    let Json::Array(classes) = member(&vector, "classes") else {
        panic!("the vector holds classes");
    };
    // Each character managed joins to another, with the least of its class.
    let mut folds: Vec<(u32, u32)> = Vec::new();
    for class in classes {
        let members: Vec<u32> = text(class)
            .expect("a class is text")
            .split(' ')
            .map(|scalar| u32::from_str_radix(scalar, 16).expect("a code point"))
            .collect();
        assert!(members.len() > 1 && members.is_sorted(), "{members:X?}");
        folds.extend(members[1..].iter().map(|member| (*member, members[0])));
    }
    folds.sort_unstable();
    assert!(folds.len() > 1400, "{} folds", folds.len());

    if std::env::var_os(REGENERATE).is_some() {
        let path = concat!(
            env!("CARGO_MANIFEST_DIR"),
            "/src/profile_set/ordinal_case.rs"
        );
        std::fs::write(path, ordinal_case_table(&folds)).expect("the table is written");
        return;
    }

    // Every character there is, not only those in the vector: the table must
    // join nothing managed keeps apart.
    let mut recorded = folds.iter().copied().peekable();
    for character in '\0'..=char::MAX {
        let scalar = character as u32;
        let expected = recorded
            .next_if(|(from, _)| *from == scalar)
            .map_or(scalar, |(_, to)| to);
        assert_eq!(
            fold_ordinal_case(character) as u32,
            expected,
            "U+{scalar:04X}"
        );
    }
    assert_eq!(recorded.next(), None);

    for (left, right, same) in [
        (r"C:\Games\Game.exe", r"c:\GAMES\game.EXE", true),
        (r"C:\Games\Game.exe", r"C:\Games\Game.ex", false),
        ("B\u{e4}r", "B\u{c4}R", true),
        // One character to one: no expansion, no ASCII letter for another.
        ("Stra\u{df}e", "STRASSE", false),
        ("\u{131}", "I", false),
        ("\u{17f}", "S", false),
        ("\u{212a}", "k", false),
        ("\u{fb01}", "FI", false),
        // Joined by managed, kept apart by the Windows comparison.
        ("\u{3c2}", "\u{3a3}", true),
        ("\u{b5}", "\u{39c}", true),
        ("\u{1c5}", "\u{1c6}", true),
        ("\u{1f80}", "\u{1f88}", true),
        ("\u{10428}", "\u{10400}", true),
        // Case pairs newer than managed's table: the Rust standard library
        // joins them and managed does not.
        ("\u{264}", "\u{a7cb}", false),
        ("\u{19b}", "\u{a7dc}", false),
        ("\u{1c8a}", "\u{1c89}", false),
        ("\u{16ebb}", "\u{16ea0}", false),
    ] {
        assert_eq!(same_path(left, right), same, "{left:?} {right:?}");
        assert_eq!(same_path(right, left), same, "{right:?} {left:?}");
    }
}

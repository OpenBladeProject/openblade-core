//! The managed vectors for native active application-profile resolution.
//!
//! `ServiceControlRuntimeTests.NativeProfileResolutionVectors` records each
//! case from `ProfileAutomationState` and each intent row from the real
//! `UpdateProfilesAsync`, and verifies the same file. A step marked
//! `nativeDifference` is one native refuses where managed accepts; the note
//! says why. A case may set the device capability and the confirmed firmware
//! state `Resolve` reads; an update step also records the save-path predicate
//! over `Resolve`.
#![cfg(feature = "profile-set")]

mod common;

use common::{apply, member, validator};
use openblade_domain::application_context::{Intent, intent_changed};
use openblade_domain::profile_set::application::{
    self, ApplicationContext, Confirmed, DesiredIntent, Power, Resolution, Unnormalized,
};
use openblade_domain::profile_set::json::{self, Json};
use openblade_domain::profile_set::{
    CustomPerformance, Dialect, FanMode, LaptopBehavior, LightingSettings, PerformancePreset,
    PowerSource, ProfileSet, encode, normalize, update,
};

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
            &[
                "name",
                "power",
                "projectUnclassified",
                "supportsManualFansOnLimitedPower",
                "confirmed",
                "profiles",
                "steps",
                "expect",
            ],
            &["name", "power", "steps"],
        )?;
        power_source(member(case, "power"))?;
        for flag in ["projectUnclassified", "supportsManualFansOnLimitedPower"] {
            if case
                .member(flag)
                .is_some_and(|value| value.boolean().is_none())
            {
                return Err(format!("{flag} is true or false"));
            }
        }
        if let Some(confirmed) = case.member("confirmed") {
            let all = ["performance", "fans", "lighting", "customPerformance"];
            shape(confirmed, &all, &all)?;
            for (name, max) in [("performance", 9), ("fans", 4)] {
                let value = member(confirmed, name);
                if !value.is_null() && value.integer(0, max).is_none() {
                    return Err(format!("a confirmed {name} is null or 0 to {max}"));
                }
            }
        }
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
                ["power"] => power_source(member(step, "power")).map(drop)?,
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

fn power_source(value: &Json) -> Result<Option<PowerSource>, String> {
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

/// A case's confirmed firmware state, owned: lighting and Custom settings
/// decode through the profile codec, as managed's reader deserialises them.
struct Observed {
    performance: Option<PerformancePreset>,
    fans: Option<FanMode>,
    lighting: Option<LightingSettings>,
    custom_performance: Option<CustomPerformance>,
}

impl Observed {
    fn read(base: &Json, value: Option<&Json>) -> Self {
        let Some(value) = value else {
            return Self {
                performance: None,
                fans: None,
                lighting: None,
                custom_performance: None,
            };
        };
        let number = |name| {
            let value = member(value, name);
            (!value.is_null()).then(|| i32::try_from(value.integer(0, 9).unwrap()).unwrap())
        };
        let edit = |name: &str, field: &str| {
            Json::Array(vec![
                Json::Array(vec![Json::string("pluggedIn"), Json::string(name)]),
                member(value, field).clone(),
            ])
        };
        let carrier = update::decode_profiles(
            &apply(
                base,
                &Json::Array(vec![
                    edit("lighting", "lighting"),
                    edit("customPerformance", "customPerformance"),
                ]),
            ),
            Dialect::Document,
        )
        .expect("the confirmed state decodes");
        Self {
            performance: number("performance").map(PerformancePreset),
            fans: number("fans").map(FanMode),
            lighting: carrier.plugged_in.lighting,
            custom_performance: carrier.plugged_in.custom_performance,
        }
    }

    fn confirmed(&self) -> Confirmed<'_> {
        Confirmed {
            performance: self.performance,
            fans: self.fans,
            lighting: self.lighting.as_ref(),
            custom_performance: self.custom_performance,
        }
    }
}

fn intent<'a>(resolution: &'a Resolution<'_>, supports: bool) -> DesiredIntent<'a> {
    application::desired_intent(resolution, supports).unwrap()
}

fn project(resolution: &Resolution<'_>, supports: bool) -> [Json; 4] {
    [
        resolution
            .application_profile_id
            .map_or(Json::Null, Json::string),
        encode::power_profile(&resolution.profile),
        encode::desired_intent(&intent(resolution, supports)),
        encode::laptop_behavior(resolution.laptop_behavior),
    ]
}

fn predicate<'a>(
    resolution: &'a Resolution<'_>,
    supports: bool,
) -> Intent<'a, DesiredIntent<'a>, LaptopBehavior> {
    Intent {
        application_profile_id: resolution.application_profile_id,
        desired: intent(resolution, supports),
        laptop_behavior: resolution.laptop_behavior,
    }
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
    let mut counts = [0; 6];
    for case in rows(member(&root, "cases")).unwrap() {
        let case_name = name(case);
        let mut profiles = saved(&base, member(case, "profiles"), case_name);
        let mut power = Power {
            state: power_source(member(case, "power")).unwrap(),
            project_unclassified: member(case, "projectUnclassified")
                .boolean()
                .unwrap_or(false),
        };
        let supports = member(case, "supportsManualFansOnLimitedPower")
            .boolean()
            .unwrap_or(true);
        let observed = Observed::read(&base, case.member("confirmed"));
        let confirmed = observed.confirmed();
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
                    counts[1] += 1;
                    break;
                }
                assert_eq!(
                    Json::Bool(reported.unwrap()),
                    *member(expect, "changed"),
                    "{at}"
                );
            } else if let Some(edits) = step.member("update") {
                let prior_context = context.clone();
                let prior_profiles = profiles;
                profiles = saved(&base, edits, &at);
                let dropped = application::update(&mut context, &profiles);
                assert_eq!(
                    dropped,
                    prior.is_some() && member(expect, "activeId").is_null(),
                    "{at}"
                );
                // `activeApplicationIntentChanged` over `Resolve`, as
                // `UpdateProfilesAsync` evaluates it with no override active.
                let before =
                    application::resolve(&prior_context, &prior_profiles, power, confirmed)
                        .unwrap();
                let after = application::resolve(&context, &profiles, power, confirmed).unwrap();
                let changed =
                    intent_changed(&predicate(&before, supports), &predicate(&after, supports));
                assert_eq!(
                    Json::Bool(changed),
                    *member(expect, "intentChanged"),
                    "{at}"
                );
                counts[3] += usize::from(changed);
            } else {
                // `ChangePowerSource` reports only whether the source moved.
                // Its other effects, expiring transient manual overrides and
                // cancelling a temporary full charge on battery, are the
                // host's and absent from every recorded case.
                let next = power_source(member(step, "power")).unwrap();
                assert_eq!(
                    Json::Bool(next != power.state),
                    *member(expect, "changed"),
                    "{at}"
                );
                counts[4] += usize::from(next == power.state);
                power.state = next;
            }
            let resolution = application::resolve(&context, &profiles, power, confirmed).unwrap();
            let [id, profile, desired, behavior] = project(&resolution, supports);
            assert_eq!(id, *member(expect, "activeId"), "{at}");
            assert_eq!(profile, *member(expect, "profile"), "{at}");
            assert_eq!(desired, *member(expect, "intent"), "{at}");
            assert_eq!(behavior, *member(expect, "laptopBehavior"), "{at}");
            counts[0] += 1;
            counts[2] += usize::from(resolution.desired_source().is_none());
            counts[5] += usize::from(!supports);
        }
    }
    // Guard against a file that silently lost its rows: steps, native
    // differences, steps on unknown external power, saves that change the
    // intent, power steps that repeat their source, steps without manual fans
    // on limited power.
    assert_eq!(counts, [138, 1, 33, 16, 3, 10]);
}

#[test]
fn every_managed_save_changes_the_active_intent_alike() {
    let root = read_vectors(VECTORS).unwrap();
    let base = base();
    let power = Power::retained(Some(PowerSource::PluggedIn));
    let mut branches = [false; 5];
    for row in rows(member(&root, "intent")).unwrap() {
        let row_name = name(row);
        let expect = member(row, "expect");
        let flag = |name| member(expect, name).boolean().unwrap();
        let prior_profiles = saved(&base, member(row, "profiles"), row_name);
        let mut context = ApplicationContext::default();
        application::report(&mut context, &prior_profiles, member(row, "report").text()).unwrap();
        let prior_context = context.clone();
        let profiles = saved(&base, member(row, "update"), row_name);
        application::update(&mut context, &profiles);
        // The runtime resolves before any confirmed member matters: every
        // row's profiles set lighting and no Custom preset without levels.
        let before =
            application::resolve(&prior_context, &prior_profiles, power, Confirmed::default())
                .unwrap();
        let after = application::resolve(&context, &profiles, power, Confirmed::default()).unwrap();
        // The recorded runtime admits manual fans on limited power.
        let prior = predicate(&before, true);
        let next = predicate(&after, true);
        let id = |value: Option<&str>| value.map_or(Json::Null, Json::string);
        assert_eq!(
            id(prior.application_profile_id),
            *member(expect, "priorId"),
            "{row_name}"
        );
        assert_eq!(
            id(next.application_profile_id),
            *member(expect, "id"),
            "{row_name}"
        );
        assert_eq!(
            next.desired != prior.desired,
            flag("desiredChanged"),
            "{row_name}"
        );
        assert_eq!(
            next.laptop_behavior != prior.laptop_behavior,
            flag("laptopBehaviorChanged"),
            "{row_name}"
        );
        assert_eq!(
            intent_changed(&prior, &next),
            flag("intentChanged"),
            "{row_name}"
        );

        // The first term that decides the predicate.
        branches[match () {
            () if prior.application_profile_id.is_none() => 0,
            () if next.application_profile_id != prior.application_profile_id => 1,
            () if next.desired != prior.desired => 2,
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
        r#"{"comment":"","base":{},"cases":[{"name":"x","power":0,"steps":[{"power":4}]}],"intent":[]}"#,
        r#"{"comment":"","base":{},"cases":[{"name":"x","power":0,"projectUnclassified":1,"steps":[]}],"intent":[]}"#,
        r#"{"comment":"","base":{},"cases":[{"name":"x","power":0,"supportsManualFansOnLimitedPower":null,"steps":[]}],"intent":[]}"#,
        r#"{"comment":"","base":{},"cases":[{"name":"x","power":3,"confirmed":{"performance":null,"fans":null,"lighting":null},"steps":[]}],"intent":[]}"#,
        r#"{"comment":"","base":{},"cases":[{"name":"x","power":3,"confirmed":{"performance":null,"fans":null,"lighting":null,"customPerformance":null,"battery":null},"steps":[]}],"intent":[]}"#,
        r#"{"comment":"","base":{},"cases":[{"name":"x","power":3,"confirmed":{"performance":"1","fans":null,"lighting":null,"customPerformance":null},"steps":[]}],"intent":[]}"#,
    ] {
        assert!(read_vectors(text).is_err(), "{text}");
    }
    let minimal = r#"{"comment":"","base":{},"cases":[{"name":"x","power":3,"projectUnclassified":false,"supportsManualFansOnLimitedPower":false,"confirmed":{"performance":1,"fans":null,"lighting":null,"customPerformance":null},"steps":[{"report":null},{"update":[]},{"power":0}]}],"intent":[{"name":"x","report":"C:\\a.exe","update":[]}]}"#;
    assert!(read_vectors(minimal).is_ok());
}

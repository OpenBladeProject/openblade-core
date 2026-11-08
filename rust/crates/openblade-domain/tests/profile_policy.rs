//! The single shared 14-case corpus is the literal migration contract.
use std::collections::HashSet;

use openblade_domain::{
    DisplayProjection, FanControlProjection, FanControlSource, PolicyError, ProfileOverride,
    ProfileProjection, apply, apply_display,
};
use serde::{Deserialize, Deserializer};

const JSON: &str = include_str!("../../../../tests/Fixtures/Parity/v1/profile-policy.json");

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Fixture {
    schema_version: u32,
    suite: String,
    source_revision: String,
    cases: Vec<Case>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Case {
    id: String,
    #[serde(deserialize_with = "required_nullable")]
    baseline: Option<Projection>,
    #[serde(rename = "override", deserialize_with = "required_nullable")]
    override_: Option<Override>,
    #[serde(deserialize_with = "required_nullable")]
    expected: Option<Projection>,
    #[serde(deserialize_with = "required_nullable")]
    expected_error: Option<String>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Projection {
    performance: i32,
    fans: i32,
    #[serde(deserialize_with = "required_nullable")]
    fan_control: Option<Fan>,
    #[serde(deserialize_with = "required_nullable")]
    display: Option<Display>,
    #[serde(deserialize_with = "required_nullable")]
    gaming_mode_enabled: Option<bool>,
}

#[derive(Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Override {
    performance: Option<i32>,
    fans: Option<i32>,
    fan_control: Option<Fan>,
    display: Option<DisplayOverride>,
    gaming_mode_enabled: Option<bool>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Fan {
    mode: i32,
    fixed_speed_rpm: i32,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Display {
    #[serde(deserialize_with = "required_nullable")]
    refresh_rate_key: Option<String>,
    #[serde(deserialize_with = "required_nullable")]
    hdr_enabled: Option<bool>,
    #[serde(deserialize_with = "required_nullable")]
    color_profile_key: Option<String>,
    #[serde(deserialize_with = "required_nullable")]
    brightness_percent: Option<u8>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct DisplayOverride {
    refresh_rate_key: Option<String>,
    hdr_enabled: Option<bool>,
    color_profile_key: Option<String>,
    brightness_percent: Option<u8>,
}

fn required_nullable<'de, D, T>(deserializer: D) -> Result<Option<T>, D::Error>
where
    D: Deserializer<'de>,
    T: Deserialize<'de>,
{
    Option::deserialize(deserializer)
}

impl Fan {
    fn projection(&self) -> FanControlProjection {
        FanControlProjection {
            mode: self.mode,
            fixed_speed_rpm: self.fixed_speed_rpm,
        }
    }
}

impl Display {
    fn projection(&self) -> DisplayProjection<'_> {
        DisplayProjection {
            refresh_rate_key: self.refresh_rate_key.as_deref(),
            hdr_enabled: self.hdr_enabled,
            color_profile_key: self.color_profile_key.as_deref(),
            brightness_percent: self.brightness_percent.map(i32::from),
        }
    }
}

impl DisplayOverride {
    fn projection(&self) -> DisplayProjection<'_> {
        DisplayProjection {
            refresh_rate_key: self.refresh_rate_key.as_deref(),
            hdr_enabled: self.hdr_enabled,
            color_profile_key: self.color_profile_key.as_deref(),
            brightness_percent: self.brightness_percent.map(i32::from),
        }
    }
}

impl Projection {
    fn projection(&self) -> ProfileProjection<'_> {
        ProfileProjection {
            performance: self.performance,
            fans: self.fans,
            fan_control: self.fan_control.as_ref().map(Fan::projection),
            display: self.display.as_ref().map(Display::projection),
            gaming_mode_enabled: self.gaming_mode_enabled,
        }
    }
}

impl Override {
    fn projection(&self) -> ProfileOverride<'_> {
        ProfileOverride {
            performance: self.performance,
            fans: self.fans,
            fan_control: self.fan_control.as_ref().map(Fan::projection),
            display: self.display.as_ref().map(DisplayOverride::projection),
            gaming_mode_enabled: self.gaming_mode_enabled,
        }
    }
}

fn parse(json: &str) -> Result<Fixture, String> {
    let fixture: Fixture = serde_json::from_str(json).map_err(|error| error.to_string())?;
    if fixture.schema_version != 1
        || fixture.suite != "application-profile-merge"
        || fixture.source_revision != "123936f3ea69881d21bd16a7a09529930c9a2c6f"
    {
        return Err("unsupported schema, suite or provenance".into());
    }
    let mut ids = HashSet::new();
    for case in &fixture.cases {
        if case.id.is_empty() || !ids.insert(case.id.as_str()) {
            return Err("missing or duplicate case ID".into());
        }
        match case.expected_error.as_deref() {
            None if case.baseline.is_some() && case.expected.is_some() => {}
            Some("baseline-null") if case.baseline.is_none() && case.expected.is_none() => {}
            _ => return Err("inconsistent expected outcome".into()),
        }
    }
    Ok(fixture)
}

#[test]
fn application_merge_matches_all_shared_cases() {
    let fixture = parse(JSON).expect("strict shared fixture");
    assert_eq!(fixture.cases.len(), 14);
    for case in fixture.cases {
        let baseline = case.baseline.as_ref().map(Projection::projection);
        let override_ = case.override_.as_ref().map(Override::projection);
        let actual = apply(baseline, override_);
        if case.expected_error.is_some() {
            assert_eq!(actual, Err(PolicyError::BaselineNull), "{}", case.id);
            continue;
        }
        let actual = actual.expect(&case.id);
        let expected = case.expected.as_ref().expect("expected").projection();
        assert_eq!(actual.profile, expected, "{}", case.id);
        let source = match case.id.as_str() {
            "legacy-maximum-fans"
            | "legacy-fixed-default-speed"
            | "legacy-automatic-replaces-fixed" => FanControlSource::LegacyDefault,
            "explicit-fan-control"
            | "conflicting-fan-fields-preserve-raw-merge"
            | "explicit-legacy-maximum-fixed-5400" => FanControlSource::Override,
            "legacy-maximum-fixed-5400-preserved" => FanControlSource::Baseline,
            _ => FanControlSource::None,
        };
        assert_eq!(actual.fan_control_source, source, "{}", case.id);
        if let Some(override_) = override_ {
            assert_eq!(
                Some(apply_display(
                    baseline.expect("baseline").display.unwrap_or_default(),
                    override_.display
                )),
                expected.display,
                "{}: direct display merge",
                case.id,
            );
        }
    }
}

#[test]
fn fixture_reader_rejects_non_contract_representations() {
    for (old, new) in [
        ("\"schemaVersion\": 1", "\"schemaVersion\": 2"),
        ("application-profile-merge", "other-suite"),
        ("123936f3ea69881d21bd16a7a09529930c9a2c6f", "unknown"),
        ("\"performance\": 1", "\"performance\": \"1\""),
        ("\"performance\": 1", "\"Performance\": 1"),
        (
            "\"performance\": 1",
            "\"performance\": 1, \"performance\": 2",
        ),
        (
            "\"hdrEnabled\": true",
            "\"hdrEnabled\": true, \"hdrEnabled\": false",
        ),
        ("\"fixedSpeedRpm\": 5400", "\"fixedSpeedRPM\": 5400"),
        ("\"baseline\": null,", ""),
        ("\"gamingModeEnabled\": true", "\"gamingModeEnabled\": 0"),
        ("\"brightnessPercent\": 70", "\"brightnessPercent\": -1"),
        ("\"brightnessPercent\": 70", "\"brightnessPercent\": 256"),
        ("null-baseline-rejected", "null-override"),
    ] {
        assert!(
            parse(&JSON.replacen(old, new, 1)).is_err(),
            "accepted {new}"
        );
    }
}

fn baseline() -> ProfileProjection<'static> {
    ProfileProjection {
        performance: 1,
        fans: 0,
        fan_control: None,
        display: None,
        gaming_mode_enabled: None,
    }
}

#[test]
fn only_legacy_fan_synthesis_validates_the_fan_enum() {
    for invalid in [i32::MIN, -1, 4, i32::MAX] {
        let raw = ProfileProjection {
            performance: invalid,
            fans: invalid,
            ..baseline()
        };
        assert_eq!(apply(Some(raw), None).unwrap().profile, raw);
        let inherited = apply(Some(raw), Some(ProfileOverride::default())).unwrap();
        assert_eq!(inherited.profile.performance, invalid);
        assert_eq!(inherited.profile.fans, invalid);
        let selected = ProfileOverride {
            performance: Some(invalid),
            fans: Some(invalid),
            ..ProfileOverride::default()
        };
        assert_eq!(
            apply(Some(baseline()), Some(selected)),
            Err(PolicyError::InvalidLegacyFanMode)
        );
        let control = FanControlProjection {
            mode: 1,
            fixed_speed_rpm: 2300,
        };
        let contradictory = apply(
            Some(baseline()),
            Some(ProfileOverride {
                fan_control: Some(control),
                ..selected
            }),
        )
        .unwrap();
        assert_eq!(contradictory.profile.performance, invalid);
        assert_eq!(contradictory.profile.fans, invalid);
        assert_eq!(contradictory.profile.fan_control, Some(control));
        assert_eq!(contradictory.fan_control_source, FanControlSource::Override);
    }
    assert_eq!(apply(None, None), Err(PolicyError::BaselineNull));
}

#[test]
fn display_values_remain_opaque_until_host_validation() {
    let long_key = "色😀".repeat(1025);
    let display = DisplayProjection {
        refresh_rate_key: Some(""),
        hdr_enabled: Some(false),
        color_profile_key: Some(&long_key),
        brightness_percent: Some(255),
    };
    assert_eq!(apply_display(display, None), display);
    let merged = apply_display(
        display,
        Some(DisplayProjection {
            refresh_rate_key: Some("\0\r\n"),
            brightness_percent: Some(0),
            ..DisplayProjection::default()
        }),
    );
    assert_eq!(merged.refresh_rate_key, Some("\0\r\n"));
    assert_eq!(merged.hdr_enabled, Some(false));
    assert_eq!(merged.color_profile_key, Some(long_key.as_str()));
    assert_eq!(merged.brightness_percent, Some(0));
}

#[test]
fn fan_source_preserves_host_curve_ownership_and_unmodified_scalars() {
    let control = FanControlProjection {
        mode: 2,
        fixed_speed_rpm: 2600,
    };
    let baseline = ProfileProjection {
        fans: 2,
        fan_control: Some(control),
        ..baseline()
    };
    let inherited = apply(Some(baseline), Some(ProfileOverride::default())).unwrap();
    assert_eq!(inherited.profile.fan_control, Some(control));
    assert_eq!(inherited.fan_control_source, FanControlSource::Baseline);
    let explicit = apply(
        Some(baseline),
        Some(ProfileOverride {
            fan_control: Some(control),
            ..ProfileOverride::default()
        }),
    )
    .unwrap();
    assert_eq!(explicit.fan_control_source, FanControlSource::Override);
    let reset = apply(
        Some(baseline),
        Some(ProfileOverride {
            fans: Some(2),
            ..ProfileOverride::default()
        }),
    )
    .unwrap();
    assert_eq!(
        reset.profile.fan_control,
        Some(FanControlProjection {
            mode: 2,
            fixed_speed_rpm: 3700
        })
    );
    assert_eq!(reset.fan_control_source, FanControlSource::LegacyDefault);
}

//! Bounded, allocation-free application-profile policy projection.
//!
//! This is a merge seam, not profile validation or hardware admission. Integer
//! enum payloads and display values retain the host's validation order.
#![no_std]

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct FanControlProjection {
    pub mode: i32,
    pub fixed_speed_rpm: i32,
}

#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct DisplayProjection<'a> {
    pub refresh_rate_key: Option<&'a str>,
    pub hdr_enabled: Option<bool>,
    pub color_profile_key: Option<&'a str>,
    pub brightness_percent: Option<i32>,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct ProfileProjection<'a> {
    pub performance: i32,
    pub fans: i32,
    pub fan_control: Option<FanControlProjection>,
    pub display: Option<DisplayProjection<'a>>,
    pub gaming_mode_enabled: Option<bool>,
}

#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct ProfileOverride<'a> {
    pub performance: Option<i32>,
    pub fans: Option<i32>,
    pub fan_control: Option<FanControlProjection>,
    pub display: Option<DisplayProjection<'a>>,
    pub gaming_mode_enabled: Option<bool>,
}

/// The host retains the selected complete fan object, including its arbitrary
/// manual curve. Only LegacyDefault synthesizes the host's default curve.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
#[repr(u32)]
pub enum FanControlSource {
    None = 0,
    Baseline = 1,
    Override = 2,
    LegacyDefault = 3,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct ProfileResolution<'a> {
    pub profile: ProfileProjection<'a>,
    pub fan_control_source: FanControlSource,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum PolicyError {
    BaselineNull,
    InvalidLegacyFanMode,
}

/// Mirrors ApplicationProfileResolution.Apply for the bounded projection.
/// Fields outside this projection must be retained/merged by the host.
pub fn apply<'a>(
    baseline: Option<ProfileProjection<'a>>,
    override_: Option<ProfileOverride<'a>>,
) -> Result<ProfileResolution<'a>, PolicyError> {
    let baseline = baseline.ok_or(PolicyError::BaselineNull)?;
    let baseline_source = if baseline.fan_control.is_some() {
        FanControlSource::Baseline
    } else {
        FanControlSource::None
    };
    let Some(value) = override_ else {
        return Ok(ProfileResolution {
            profile: baseline,
            fan_control_source: baseline_source,
        });
    };
    let fans = value
        .fans
        .or(value.fan_control.map(|control| control.mode))
        .unwrap_or(baseline.fans);
    let (fan_control, fan_control_source) = if let Some(control) = value.fan_control {
        (Some(control), FanControlSource::Override)
    } else if value.fans.is_some() {
        // FromLegacy constructs FanControlSettings and therefore rejects an
        // undefined fan mode here. Raw inherited enum values stay unvalidated.
        if !(0..=3).contains(&fans) {
            return Err(PolicyError::InvalidLegacyFanMode);
        }
        (
            Some(FanControlProjection {
                mode: fans,
                fixed_speed_rpm: if fans == 3 { 5400 } else { 3700 },
            }),
            FanControlSource::LegacyDefault,
        )
    } else {
        (baseline.fan_control, baseline_source)
    };
    Ok(ProfileResolution {
        profile: ProfileProjection {
            performance: value.performance.unwrap_or(baseline.performance),
            fans,
            fan_control,
            display: Some(apply_display(
                baseline.display.unwrap_or_default(),
                value.display,
            )),
            gaming_mode_enabled: value.gaming_mode_enabled.or(baseline.gaming_mode_enabled),
        },
        fan_control_source,
    })
}

/// Keys are opaque borrowed UTF-8, with no normalization or OS interpretation.
pub fn apply_display<'a>(
    baseline: DisplayProjection<'a>,
    override_: Option<DisplayProjection<'a>>,
) -> DisplayProjection<'a> {
    let Some(value) = override_ else {
        return baseline;
    };
    DisplayProjection {
        refresh_rate_key: value.refresh_rate_key.or(baseline.refresh_rate_key),
        hdr_enabled: value.hdr_enabled.or(baseline.hdr_enabled),
        color_profile_key: value.color_profile_key.or(baseline.color_profile_key),
        brightness_percent: value.brightness_percent.or(baseline.brightness_percent),
    }
}

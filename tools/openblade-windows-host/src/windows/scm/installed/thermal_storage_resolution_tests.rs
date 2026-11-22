//! The composition `Storage` layers over a pre-resolved domain profile layer:
//! the resolution alone supplies the profile's destination, GPU clock effect
//! and fan intent, and the persistent and temporary overrides outrank it.
//! The store here holds no aggregate at all, so any profile answer can only
//! come from the resolution passed in.

use super::{Files, Override, Storage, fan_settings, legacy_fan};
use crate::service_status::FanOverride;
use openblade_domain::{
    automation::UNCLASSIFIED_EXTERNAL,
    profile_set::{
        ApplicationActivationMode, ApplicationOverride, ApplicationOverrides, ApplicationProfile,
        FanControl, FanMode, GpuClockTuning, PerformancePreset, PowerSource, ProfileSet,
        application::{self, ApplicationContext, Confirmed, Power, Resolution},
    },
    thermal_mutation::{Destination, Preset},
};
use serde_json::json;
use std::{collections::BTreeMap, io, sync::Mutex};

const SID: &str = "S-1-5-21-111-222-333-1001";
const GAME: &str = r"C:\Games\Game.exe";

#[derive(Default)]
struct Memory(Mutex<BTreeMap<String, Vec<u8>>>);

impl Files for Memory {
    fn read(&self, name: &str) -> io::Result<Option<Vec<u8>>> {
        Ok(self.0.lock().unwrap().get(name).cloned())
    }
    fn replace(&self, name: &str, _expected: Option<&[u8]>, bytes: &[u8]) -> io::Result<()> {
        self.0.lock().unwrap().insert(name.into(), bytes.to_vec());
        Ok(())
    }
}

/// Balanced with Automatic fans on every source; the application overrides
/// the plugged-in profile with `game`.
fn profiles(game: ApplicationOverride) -> ProfileSet {
    let mut set = ProfileSet::safe_defaults();
    for source in PowerSource::ALL {
        let profile = set.for_source_mut(source);
        profile.performance = PerformancePreset::BALANCED;
        profile.fans = FanMode::AUTOMATIC;
        profile.fan_control = None;
        profile.gpu_clock_tuning = None;
    }
    set.application_profiles = vec![ApplicationProfile {
        id: "11111111-2222-3333-4444-555555555555".into(),
        name: "Game".into(),
        executable_path: GAME.into(),
        activation_mode: ApplicationActivationMode::FOREGROUND,
        overrides: ApplicationOverrides {
            plugged_in: Some(game),
            ..ApplicationOverrides::default()
        },
        enabled: true,
    }];
    set
}

/// Performance with a manual curve: every member the matchers read.
fn game() -> ApplicationOverride {
    ApplicationOverride {
        performance: Some(PerformancePreset::PERFORMANCE),
        fans: Some(FanMode::MANUAL_CURVE),
        ..ApplicationOverride::default()
    }
}

fn context(set: &ProfileSet, reported: Option<&str>) -> ApplicationContext {
    let mut context = ApplicationContext::default();
    application::report(&mut context, set, reported).unwrap();
    context
}

fn resolve<'a>(
    context: &'a ApplicationContext,
    set: &'a ProfileSet,
    source: Option<PowerSource>,
) -> Resolution<'a> {
    application::resolve(context, set, Power::retained(source), Confirmed::default()).unwrap()
}

fn storage() -> std::sync::Arc<Storage> {
    Storage::open(Box::new(Memory::default()), SID).unwrap()
}

fn wire(storage: &Storage, power: i32, profile: Option<&Resolution<'_>>) -> Option<(i32, i32)> {
    storage
        .desired(power, profile)
        .unwrap()
        .map(|(destination, source)| (destination.wire(), source))
}

#[test]
fn the_resolved_profile_decides_the_destination_gpu_check_and_fan_intent() {
    let set = profiles(game());
    let storage = storage();
    let active = context(&set, Some(GAME));
    let idle = context(&set, None);
    let with_game = resolve(&active, &set, Some(PowerSource::PluggedIn));
    let without = resolve(&idle, &set, Some(PowerSource::PluggedIn));

    // The application's preset is source 2, the power profile's source 1.
    assert_eq!(wire(&storage, 0, Some(&with_game)), Some((2, 2)));
    assert_eq!(wire(&storage, 0, Some(&without)), Some((1, 1)));
    assert!(storage.desired_manual_fans(0, Some(&with_game)).unwrap());
    assert!(!storage.desired_manual_fans(0, Some(&without)).unwrap());
    // An override that sets only the mode selects that mode's legacy default.
    assert_eq!(
        storage.desired_fan(0, Some(&with_game)).unwrap(),
        Some(legacy_fan(2))
    );
    assert_eq!(
        storage.desired_fan(0, Some(&without)).unwrap(),
        Some(legacy_fan(0))
    );
    assert!(!storage.gpu_incomplete(0, Some(&with_game)).unwrap());

    // Without a saved aggregate there is no profile layer.
    assert_eq!(wire(&storage, 0, None), None);
    assert_eq!(storage.desired_fan(0, None).unwrap(), None);
    assert!(!storage.desired_manual_fans(0, None).unwrap());
    assert!(!storage.gpu_incomplete(0, None).unwrap());

    // A resolution for another class selects nothing for this one.
    let battery = resolve(&active, &set, Some(PowerSource::Battery));
    assert_eq!(wire(&storage, 0, Some(&battery)), None);
    assert_eq!(storage.desired_fan(0, Some(&battery)).unwrap(), None);
    assert!(!storage.desired_manual_fans(0, Some(&battery)).unwrap());
    assert!(storage.gpu_incomplete(0, Some(&battery)).unwrap());

    // Unclassified external and unknown power apply no profile, even though
    // the domain resolves managed's observed fallback there.
    let unknown = resolve(&active, &set, None);
    for power in [UNCLASSIFIED_EXTERNAL, -1] {
        assert_eq!(wire(&storage, power, Some(&unknown)), None, "{power}");
        assert_eq!(storage.desired_fan(power, Some(&unknown)).unwrap(), None);
        assert!(!storage.desired_manual_fans(power, Some(&unknown)).unwrap());
    }
    // An unknown class with saved profiles is a possible GPU effect;
    // unclassified power has none without an override.
    assert!(
        !storage
            .gpu_incomplete(UNCLASSIFIED_EXTERNAL, Some(&unknown))
            .unwrap()
    );
    assert!(storage.gpu_incomplete(-1, Some(&unknown)).unwrap());

    // The application's GPU tuning, its fan control and its Custom levels
    // come from the resolved profile too.
    let curve = FanControl {
        fixed_speed_rpm: 4000,
        ..FanControl::from_legacy(FanMode::FIXED).unwrap()
    };
    let set = profiles(ApplicationOverride {
        performance: Some(PerformancePreset::CUSTOM),
        custom_performance: Some(openblade_domain::profile_set::CustomPerformance {
            cpu: openblade_domain::profile_set::CustomPerformanceLevel::LOW,
            gpu: openblade_domain::profile_set::CustomPerformanceLevel::MEDIUM,
            ..openblade_domain::profile_set::CustomPerformance::DEFAULT
        }),
        gpu_clock_tuning: Some(GpuClockTuning {
            enabled: true,
            offsets: None,
        }),
        fan_control: Some(curve.clone()),
        ..ApplicationOverride::default()
    });
    let active = context(&set, Some(GAME));
    let with_game = resolve(&active, &set, Some(PowerSource::PluggedIn));
    assert!(storage.gpu_incomplete(0, Some(&with_game)).unwrap());
    assert_eq!(
        storage.desired(0, Some(&with_game)).unwrap(),
        Some((Destination::custom(0, 1, false).unwrap(), 2))
    );
    assert_eq!(
        storage.desired_fan(0, Some(&with_game)).unwrap(),
        Some(fan_settings(&curve))
    );
    assert!(storage.desired_manual_fans(0, Some(&with_game)).unwrap());
    let idle = context(&set, None);
    assert!(
        !storage
            .gpu_incomplete(0, Some(&resolve(&idle, &set, Some(PowerSource::PluggedIn))))
            .unwrap()
    );
}

#[test]
fn persistent_then_temporary_overrides_outrank_the_resolved_profile() {
    let set = profiles(game());
    let storage = storage();
    let active = context(&set, Some(GAME));
    let profile = resolve(&active, &set, Some(PowerSource::PluggedIn));
    let silent = |lifetime| Override {
        value: Preset::Silent as i32,
        lifetime,
        custom_performance: None,
        gpu_clock_tuning: None,
    };

    storage.save(Some(silent(1)), 0).unwrap();
    assert_eq!(wire(&storage, 0, Some(&profile)), Some((0, 4)));
    storage.save(Some(silent(0)), 0).unwrap();
    assert_eq!(wire(&storage, 0, Some(&profile)), Some((0, 3)));
    storage.save(None, 0).unwrap();
    assert_eq!(wire(&storage, 0, Some(&profile)), Some((2, 2)));

    // An override's GPU tuning is an effect whatever the profile says.
    let mut tuned = silent(1);
    tuned.gpu_clock_tuning =
        Some(json!({"enabled":true,"offsets":{"coreMegahertz":10,"memoryMegahertz":20}}));
    storage.save(Some(tuned), 0).unwrap();
    assert!(storage.gpu_incomplete(0, Some(&profile)).unwrap());
    storage.save(None, 0).unwrap();
    assert!(!storage.gpu_incomplete(0, Some(&profile)).unwrap());

    // Saved Automatic fans outrank the application's manual curve, with
    // either lifetime.
    for lifetime in [1, 0] {
        storage
            .save_fan(Some(FanOverride { value: 0, lifetime }), None, 0)
            .unwrap();
        assert!(!storage.desired_manual_fans(0, Some(&profile)).unwrap());
        assert_eq!(
            storage.desired_fan(0, Some(&profile)).unwrap(),
            Some(legacy_fan(0)),
            "{lifetime}"
        );
    }
    storage.save_fan(None, None, 0).unwrap();
    assert!(storage.desired_manual_fans(0, Some(&profile)).unwrap());
    assert_eq!(
        storage.desired_fan(0, Some(&profile)).unwrap(),
        Some(legacy_fan(2))
    );
}

/// A resolved legacy fan control is the stored intent the override layers
/// and the fan journal hold for the same mode.
#[test]
fn resolved_legacy_fan_control_is_the_stored_legacy_default() {
    for mode in 0..=3 {
        let resolved = FanControl::from_legacy(FanMode(i32::from(mode))).unwrap();
        assert_eq!(fan_settings(&resolved), legacy_fan(mode), "{mode}");
    }
}

#[test]
fn an_application_that_sets_only_custom_levels_or_gpu_tuning_is_the_source() {
    // Managed `DesiredDeviceState.Performance` names the application when its
    // override sets the preset, the Custom levels or the GPU tuning.
    let custom = openblade_domain::profile_set::CustomPerformance {
        cpu: openblade_domain::profile_set::CustomPerformanceLevel::LOW,
        gpu: openblade_domain::profile_set::CustomPerformanceLevel::MEDIUM,
        ..openblade_domain::profile_set::CustomPerformance::DEFAULT
    };
    for game in [
        ApplicationOverride {
            custom_performance: Some(custom),
            ..ApplicationOverride::default()
        },
        ApplicationOverride {
            gpu_clock_tuning: Some(GpuClockTuning {
                enabled: false,
                offsets: None,
            }),
            ..ApplicationOverride::default()
        },
    ] {
        let mut set = profiles(game);
        set.plugged_in.performance = PerformancePreset::CUSTOM;
        let storage = storage();
        let active = context(&set, Some(GAME));
        let with_game = resolve(&active, &set, Some(PowerSource::PluggedIn));
        assert_eq!(
            storage
                .desired(0, Some(&with_game))
                .unwrap()
                .map(|(_, s)| s),
            Some(2)
        );
        let idle = context(&set, None);
        let without = resolve(&idle, &set, Some(PowerSource::PluggedIn));
        assert_eq!(
            storage.desired(0, Some(&without)).unwrap().map(|(_, s)| s),
            Some(1)
        );
    }
}

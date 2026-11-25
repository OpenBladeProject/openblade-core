//! Portable direct-transition policy and durable submission chronology.
//! Fresh Windows admission, persistence and native retirement are host obligations.
use crate::thermal_observation::{
    CustomPerformanceLevel, CustomPerformanceLevels, ObservedPerformance,
};
pub use openblade_types::InstalledThermalPreset as Preset;

/// The managed PerformancePreset wire value of Custom.
pub const CUSTOM: i32 = 5;
/// The 0D/02 Custom selector (fixture `performance-custom-channel-1/2`).
pub const CUSTOM_SELECTOR: u8 = 4;
/// The native row destination of a HyperBoost entry. Managed has no
/// PerformancePreset for HyperBoost (it is `HyperBoostActive` beside the
/// preset), so the value lies outside every managed preset (0-9) and is read
/// only by readers bound to the HyperBoost capability.
#[cfg(feature = "hyperboost")]
pub const HYPERBOOST: i32 = 100;
/// The 0D/02 HyperBoost selector (fixture `hyperboost-enable-channel-1/2`).
#[cfg(feature = "hyperboost")]
pub const HYPERBOOST_SELECTOR: u8 = 7;
/// One native destination on exact RZ09-0581, after managed
/// `ProductionBatteryPort`: a preset is its own 0D/02 pair
/// (`SetPerformanceAsync`); Custom is the selector-4 pair and then the CPU and
/// GPU 0D/07 setters (`SetCustomPerformanceAsync`). The levels are the
/// requested intent, never a firmware reading.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum Destination {
    Preset(Preset),
    Custom(CustomPerformanceLevels),
    /// Managed `SetHyperBoostAsync(true)` on exact RZ09-0581: the
    /// `hyperboost-enable` pair, confirmed by an independent read of
    /// HyperBoost with Automatic fans. It is never a saved intent.
    #[cfg(feature = "hyperboost")]
    HyperBoost,
}
impl Destination {
    /// Managed `CustomPerformanceSettings` on exact RZ09-0581, admitted by
    /// `ProductionBatteryPort`'s default `CustomPerformanceCapabilities`
    /// (`BuildCapabilities`): every CPU/GPU pair of Low, Medium and High but
    /// High/High, and no Custom maximum fans. Boost (3) has no setter
    /// (`CustomPerformanceAction`). Anything else is not a destination.
    pub fn custom(cpu: u64, gpu: u64, maximum_fans: bool) -> Option<Self> {
        let level = |n| match n {
            0 => Some(CustomPerformanceLevel::Low),
            1 => Some(CustomPerformanceLevel::Medium),
            2 => Some(CustomPerformanceLevel::High),
            _ => None,
        };
        let (cpu, gpu) = (level(cpu)?, level(gpu)?);
        (!maximum_fans
            && (cpu, gpu) != (CustomPerformanceLevel::High, CustomPerformanceLevel::High))
            .then_some(Self::Custom(CustomPerformanceLevels { cpu, gpu }))
    }
    /// The managed PerformancePreset wire value.
    pub fn wire(self) -> i32 {
        match self {
            Self::Preset(preset) => preset as i32,
            Self::Custom(_) => CUSTOM,
            #[cfg(feature = "hyperboost")]
            Self::HyperBoost => HYPERBOOST,
        }
    }
    /// The battery selectors 3 and 6 (managed `IsBatterySelectorCommand`).
    /// Custom's commands are ordinary.
    pub fn is_battery(self) -> bool {
        matches!(self, Self::Preset(preset) if preset.is_battery())
    }
    pub fn custom_levels(self) -> Option<CustomPerformanceLevels> {
        match self {
            Self::Custom(levels) => Some(levels),
            _ => None,
        }
    }
    /// The 0D/02 selector the destination's pair writes; Custom is 4.
    pub fn selector(self) -> u8 {
        match self {
            Self::Preset(preset) => preset.selector(),
            Self::Custom(_) => CUSTOM_SELECTOR,
            #[cfg(feature = "hyperboost")]
            Self::HyperBoost => HYPERBOOST_SELECTOR,
        }
    }
    /// The managed PerformancePreset name, as managed replies print it.
    pub fn name(self) -> &'static str {
        match self {
            Self::Preset(Preset::Silent) => "Silent",
            Self::Preset(Preset::Balanced) => "Balanced",
            Self::Preset(Preset::Performance) => "Performance",
            Self::Preset(Preset::BatterySaver) => "BatterySaver",
            Self::Preset(Preset::BatteryBalanced) => "BatteryBalanced",
            Self::Custom(_) => "Custom",
            #[cfg(feature = "hyperboost")]
            Self::HyperBoost => "HyperBoost",
        }
    }
}
impl From<Preset> for Destination {
    fn from(preset: Preset) -> Self {
        Self::Preset(preset)
    }
}

/// One performance request's recipe on exact RZ09-0581, after managed
/// `ProductionBatteryPort.SetPerformanceAsync` and `SetCustomPerformanceAsync`:
/// every destination is written directly from any confirmed non-HyperBoost
/// state, manual fans included (`CanApplyAutomaticPerformanceFrom` and the
/// Custom precondition, with `SupportsDirectManualFanTransitions`; this
/// getter reports every manual state as Maximum fans). No Balanced or default
/// intermediate is written.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum Plan {
    Unchanged,
    Direct {
        destination: Destination,
    },
    /// Managed leaves HyperBoost for a battery preset through the
    /// `hyperboost-restore-balanced` pair first (`CanExitHyperBoostDirectlyTo`
    /// is false: `HyperBoostBaseModeAdmissionSource` admits direct exits to
    /// Silent, Balanced, Performance and Custom only). That pair is not
    /// admitted natively, so nothing is written.
    #[cfg(not(feature = "hyperboost-exit"))]
    NoDirectPath,
    /// Managed leaves HyperBoost for a battery preset through the
    /// `hyperboost-restore-balanced` pair first (`CanExitHyperBoostDirectlyTo`
    /// is false: `HyperBoostBaseModeAdmissionSource` admits direct exits to
    /// Silent, Balanced, Performance and Custom only), then an independent
    /// read that must confirm Balanced with Automatic fans, then the battery
    /// pair (`ExecuteThermalSettersAsync` with `hyperBoostExitActions`).
    #[cfg(feature = "hyperboost-exit")]
    RestoreThenBattery {
        destination: Destination,
    },
}
impl Plan {
    pub fn from_observation(current: ObservedPerformance, destination: Destination) -> Self {
        if confirms(current, destination) {
            Self::Unchanged
        } else if current == ObservedPerformance::HyperBoostObserved && destination.is_battery() {
            #[cfg(not(feature = "hyperboost-exit"))]
            let plan = Self::NoDirectPath;
            #[cfg(feature = "hyperboost-exit")]
            let plan = Self::RestoreThenBattery { destination };
            plan
        } else {
            Self::Direct { destination }
        }
    }
}
/// The independent paired read that settles a recipe: the destination's own
/// selector with Automatic fans, and for Custom the requested levels read by
/// the 0D/87 getters (managed `state.CustomPerformance == value`). Unreadable
/// levels (manual High/High) confirm nothing.
pub fn confirms(current: ObservedPerformance, destination: Destination) -> bool {
    let preset = match destination {
        // Managed's entry readback: `HyperBoostActive` with Automatic fans
        // (`SetHyperBoostCoreAsync`'s final-state predicate), which the exact
        // getter decodes only as (7, 0).
        #[cfg(feature = "hyperboost")]
        Destination::HyperBoost => return current == ObservedPerformance::HyperBoostObserved,
        Destination::Custom(levels) => {
            return current
                == ObservedPerformance::Custom {
                    manual: false,
                    levels: Some(levels),
                };
        }
        Destination::Preset(preset) => preset,
    };
    matches!(
        (current, preset),
        (ObservedPerformance::SilentAutomatic, Preset::Silent)
            | (ObservedPerformance::BalancedAutomatic, Preset::Balanced)
            | (
                ObservedPerformance::PerformanceAutomatic,
                Preset::Performance
            )
            | (
                ObservedPerformance::BatterySaverAutomatic,
                Preset::BatterySaver
            )
            | (
                ObservedPerformance::BatteryBalancedAutomatic,
                Preset::BatteryBalanced
            )
    )
}
/// Managed `requireExternalPowerOnDirectHyperBoostExit`: a direct HyperBoost
/// exit to any destination but Balanced keeps the first reading's classified
/// external class for every setter. The Balanced exit has no class rule, and a
/// battery preset is never a direct exit (`directHyperBoostExit` is false for
/// it, so managed passes no class rule to its restore sequence either).
pub fn pins_class(current: ObservedPerformance, destination: Destination) -> bool {
    current == ObservedPerformance::HyperBoostObserved
        && destination != Destination::Preset(Preset::Balanced)
        && !destination.is_battery()
}
/// The intermediate independent read of the HyperBoost restore sequence:
/// managed requires `!HyperBoostActive`, Balanced and Automatic fans before
/// any battery setter (`ExecuteThermalSettersAsync`).
#[cfg(feature = "hyperboost-exit")]
pub fn restored(current: ObservedPerformance) -> bool {
    confirms(current, Destination::Preset(Preset::Balanced))
}
/// Whether a fresh setter-time power class admits this destination's pair,
/// after managed `AdmitOrdinaryThermalCommandAsync`. `windows_on_battery` is
/// the fresh Windows power kind: `Some(false)` AC, `Some(true)` battery.
/// - The ordinary commands (Silent 5, Balanced 0, Performance 2, and Custom's
///   selector 4 and 0D/07 setters) need an
///   external class with fresh Windows AC presence, unclassified (3) included.
///   Windows battery refuses them (`allowBattery: false`).
/// - The battery selectors (3 and 6, `allowBattery: true`) are admitted on
///   Windows battery, and on a classified external class (0 or 2) that agrees
///   with Windows AC. Unclassified power refuses them: managed admits an
///   unknown adapter only for ordinary commands. Managed skips the adapter
///   read on Windows battery; the host still takes one and needs it to read
///   battery (1), which is stricter.
pub fn admits_class(
    destination: Destination,
    class: i32,
    windows_on_battery: Option<bool>,
) -> bool {
    // Managed HyperBoost entry needs `PowerSourcePolicy.IsExternal` (PluggedIn
    // or UsbC, never ExternalUnknown) and, per command, an adapter that
    // `ValidateHyperBoostEligibilityAsync` admits: every classified external
    // reading (full 0 or limited 2) of the exact 07/8C table. Unclassified
    // power (3) and battery refuse it.
    #[cfg(feature = "hyperboost")]
    if destination == Destination::HyperBoost {
        return matches!(class, 0 | 2) && windows_on_battery == Some(false);
    }
    match class {
        0 | 2 => windows_on_battery == Some(false),
        crate::automation::UNCLASSIFIED_EXTERNAL => {
            !destination.is_battery() && windows_on_battery == Some(false)
        }
        1 => destination.is_battery() && windows_on_battery == Some(true),
        _ => false,
    }
}
/// Whether the Windows power kind sampled before a setter's 07/8C query can
/// still admit this destination's command, as managed
/// `AdmitOrdinaryThermalCommandAsync` captures it before querying the
/// adapter: Windows AC for every destination, Windows battery only for a
/// battery preset, and an unknown kind for none. It is true exactly when
/// some class admits the destination under that kind (`admits_class`).
pub fn admits_windows(destination: Destination, windows_on_battery: Option<bool>) -> bool {
    match windows_on_battery {
        Some(false) => true,
        Some(true) => destination.is_battery(),
        None => false,
    }
}
/// Intent precedes possible submission. Acknowledgement never settles the
/// transaction; only a new independent paired read can do so. Each setter
/// has its own intent and acknowledgement: a preset pair uses 1-4, and
/// Custom continues with 7-10 for its CPU and GPU setters. Leaving HyperBoost
/// for a battery preset first journals the restore pair as 80-83, then the
/// battery pair as 1-4.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
#[repr(u8)]
pub enum Progress {
    Prepared,
    FirstIntent,
    FirstAcknowledged,
    SecondIntent,
    SecondAcknowledged,
    Confirmed,
    /// Authenticated Prepared-only tail, after actual transport retirement.
    KnownNotSubmitted,
    ThirdIntent,
    ThirdAcknowledged,
    FourthIntent,
    FourthAcknowledged,
    #[cfg(feature = "hyperboost-exit")]
    RestoreFirstIntent = 80,
    #[cfg(feature = "hyperboost-exit")]
    RestoreFirstAcknowledged = 81,
    #[cfg(feature = "hyperboost-exit")]
    RestoreSecondIntent = 82,
    #[cfg(feature = "hyperboost-exit")]
    RestoreSecondAcknowledged = 83,
}
const PAIR: [Progress; 6] = {
    use Progress::*;
    [
        Prepared,
        FirstIntent,
        FirstAcknowledged,
        SecondIntent,
        SecondAcknowledged,
        Confirmed,
    ]
};
const CUSTOM_CHAIN: [Progress; 10] = {
    use Progress::*;
    [
        Prepared,
        FirstIntent,
        FirstAcknowledged,
        SecondIntent,
        SecondAcknowledged,
        ThirdIntent,
        ThirdAcknowledged,
        FourthIntent,
        FourthAcknowledged,
        Confirmed,
    ]
};
/// The HyperBoost-to-battery chain: the restore pair (80-83), then the
/// battery pair (1-4), then the final independent read.
#[cfg(feature = "hyperboost-exit")]
const RESTORE_CHAIN: [Progress; 10] = {
    use Progress::*;
    [
        Prepared,
        RestoreFirstIntent,
        RestoreFirstAcknowledged,
        RestoreSecondIntent,
        RestoreSecondAcknowledged,
        FirstIntent,
        FirstAcknowledged,
        SecondIntent,
        SecondAcknowledged,
        Confirmed,
    ]
};
/// Every legal submission chain of a row for wire destination `destination`:
/// its direct chain and, for a battery preset, the HyperBoost restore chain.
pub fn chains(destination: i32) -> impl Iterator<Item = &'static [Progress]> {
    #[cfg(feature = "hyperboost-exit")]
    let restore = Preset::from_wire(destination)
        .filter(|preset| preset.is_battery())
        .map(|_| &RESTORE_CHAIN[..]);
    #[cfg(not(feature = "hyperboost-exit"))]
    let restore: Option<&'static [Progress]> = None;
    Progress::chain(destination).into_iter().chain(restore)
}
impl Progress {
    /// The submission chain of a row for wire destination `destination`,
    /// Prepared to Confirmed, or None when the row has no native destination.
    pub fn chain(destination: i32) -> Option<&'static [Self]> {
        #[cfg(feature = "hyperboost")]
        if destination == HYPERBOOST {
            return Some(&PAIR);
        }
        if destination == CUSTOM {
            Some(&CUSTOM_CHAIN)
        } else {
            Preset::from_wire(destination).map(|_| &PAIR[..])
        }
    }
    /// The intent and acknowledgement of zero-based setter `index`.
    pub fn setter(destination: Destination, index: usize) -> Option<(Self, Self)> {
        let chain = Self::chain(destination.wire())?;
        let intent = *chain.get(1 + 2 * index)?;
        (intent != Self::Confirmed).then(|| (intent, chain[2 + 2 * index]))
    }
    pub fn blocks_restart(self) -> bool {
        !settled(self as u8)
    }
    /// The longest legal chain of a row for wire destination `destination`,
    /// which a Prepared row reserves before any setter.
    pub fn longest_chain(destination: i32) -> Option<&'static [Self]> {
        chains(destination).max_by_key(|chain| chain.len())
    }
    /// The intent and acknowledgement of zero-based restore setter `index`.
    #[cfg(feature = "hyperboost-exit")]
    pub fn restore_setter(index: usize) -> Option<(Self, Self)> {
        (index < 2).then(|| (RESTORE_CHAIN[1 + 2 * index], RESTORE_CHAIN[2 + 2 * index]))
    }
    /// Whether this step is a setter's intent, the row before possible
    /// submission.
    pub fn is_intent(self) -> bool {
        match self {
            Self::FirstIntent | Self::SecondIntent | Self::ThirdIntent | Self::FourthIntent => true,
            #[cfg(feature = "hyperboost-exit")]
            Self::RestoreFirstIntent | Self::RestoreSecondIntent => true,
            _ => false,
        }
    }
}
impl Destination {
    /// Setters before the independent read: the pair, and Custom's two levels.
    pub fn setters(self) -> usize {
        Progress::chain(self.wire()).map_or(0, |chain| (chain.len() - 2) / 2)
    }
}
pub fn settled(progress: u8) -> bool {
    matches!(progress, 5 | 6 | 30 | 50 | 51 | 62 | 63 | 72 | 73)
}
/// Whether a row of wire destination `destination` may follow `prior` with
/// `next` in the same request: the next step of one of its chains, or the
/// Prepared-only retirement (0 to 6).
pub fn progress_follows(destination: i32, prior: u8, next: u8) -> bool {
    let mut chains = chains(destination).peekable();
    chains.peek().is_some()
        && ((prior == 0 && next == 6)
            || chains.any(|chain| {
                chain
                    .windows(2)
                    .any(|step| step[0] as u8 == prior && step[1] as u8 == next)
            }))
}
/// Whether `progress` is a step of a row for wire destination `destination`.
pub fn valid_progress(destination: i32, progress: u8) -> bool {
    let mut chains = chains(destination).peekable();
    chains.peek().is_some()
        && (progress == Progress::KnownNotSubmitted as u8
            || chains.any(|chain| chain.iter().any(|step| *step as u8 == progress)))
}
/// Existing exact0581 performance-only policy; not a firmware prerequisite.
/// Managed `DeferredPerformanceIntentMessage` defers on every external source
/// (`PowerSourcePolicy.IsExternal`): full (0) and limited (2) adapters. On
/// unclassified external power (3) the managed coordinator also leaves manual
/// fans in place (its latent manual-fan intent writes nothing), so the
/// performance pair is deferred there too.
pub fn defer_manual_performance(
    current: ObservedPerformance,
    power: i32,
    desired_manual: bool,
) -> bool {
    matches!(power, 0 | 2 | crate::automation::UNCLASSIFIED_EXTERNAL)
        && desired_manual
        && current.projection().1 != 0
}

#[cfg(test)]
mod tests {
    extern crate std;
    use super::*;
    #[test]
    fn manual_performance_defers_on_every_external_class_only() {
        for power in 0..=4 {
            let external = matches!(power, 0 | 2 | 3);
            let manual = ObservedPerformance::PerformanceManual;
            assert_eq!(defer_manual_performance(manual, power, true), external);
            assert!(!defer_manual_performance(manual, power, false));
            let automatic = ObservedPerformance::PerformanceAutomatic;
            assert!(!defer_manual_performance(automatic, power, true));
        }
    }
    #[test]
    fn direct_presets_keep_custom_unknown_and_need_independent_pair() {
        let unknown = ObservedPerformance::Custom {
            manual: true,
            levels: None,
        };
        for destination in [Preset::Balanced, Preset::Performance].map(Destination::from) {
            assert_eq!(
                Plan::from_observation(unknown, destination),
                Plan::Direct { destination }
            );
            assert_eq!(unknown.custom_levels(), None);
            assert!(!confirms(unknown, destination));
        }
        assert_eq!(
            Plan::from_observation(
                ObservedPerformance::PerformanceAutomatic,
                Preset::Performance.into()
            ),
            Plan::Unchanged
        );
        assert!(Progress::SecondAcknowledged.blocks_restart());
        assert!(!progress_follows(1, 0, 2));
        assert!(progress_follows(1, 4, 5));
        assert!(!Progress::Confirmed.blocks_restart());
    }
    use ObservedPerformance as O;
    const PRESETS: [Preset; 5] = [
        Preset::Silent,
        Preset::Balanced,
        Preset::Performance,
        Preset::BatterySaver,
        Preset::BatteryBalanced,
    ];
    /// Every confirmed starting state the exact getter decodes.
    fn starting_states() -> [O; 13] {
        use crate::thermal_observation::{CustomPerformanceLevel as L, CustomPerformanceLevels};
        [
            O::SilentAutomatic,
            O::BalancedAutomatic,
            O::PerformanceAutomatic,
            O::BatterySaverAutomatic,
            O::BatteryBalancedAutomatic,
            O::SilentManual,
            O::PerformanceManual,
            O::BatterySaverManual,
            O::BatteryBalancedManual,
            O::FixedMaximum,
            O::HyperBoostObserved,
            O::Custom {
                manual: false,
                levels: Some(CustomPerformanceLevels {
                    cpu: L::Medium,
                    gpu: L::Low,
                }),
            },
            O::Custom {
                manual: true,
                levels: None,
            },
        ]
    }
    #[test]
    fn each_preset_is_one_direct_pair_from_every_confirmed_state() {
        for preset in PRESETS {
            let destination = Destination::from(preset);
            for current in starting_states() {
                let own = current.projection() == (Some(preset as i32), 0, Some(false));
                let expected = if own {
                    Plan::Unchanged
                } else if current == O::HyperBoostObserved && preset.is_battery() {
                    #[cfg(not(feature = "hyperboost-exit"))]
                    let plan = Plan::NoDirectPath;
                    #[cfg(feature = "hyperboost-exit")]
                    let plan = Plan::RestoreThenBattery { destination };
                    plan
                } else {
                    Plan::Direct { destination }
                };
                assert_eq!(
                    Plan::from_observation(current, destination),
                    expected,
                    "{current:?} -> {destination:?}"
                );
                // Only the destination's own selector with Automatic fans settles it.
                assert_eq!(confirms(current, destination), own);
                assert_eq!(
                    pins_class(current, destination),
                    current == O::HyperBoostObserved
                        && preset != Preset::Balanced
                        && !preset.is_battery()
                );
            }
        }
    }
    #[test]
    fn power_classes_follow_the_ordinary_and_battery_selector_rules() {
        let custom = Destination::custom(1, 0, false).unwrap();
        for destination in PRESETS.map(Destination::from).into_iter().chain([custom]) {
            let battery = destination.is_battery();
            for windows in [Some(false), Some(true), None] {
                let ac = windows == Some(false);
                assert_eq!(admits_class(destination, 0, windows), ac);
                assert_eq!(admits_class(destination, 2, windows), ac);
                assert_eq!(admits_class(destination, 3, windows), ac && !battery);
                assert_eq!(
                    admits_class(destination, 1, windows),
                    battery && windows == Some(true)
                );
                for class in [-1, 4] {
                    assert!(!admits_class(destination, class, windows));
                }
                assert_eq!(
                    admits_windows(destination, windows),
                    [0, 1, 2, 3]
                        .iter()
                        .any(|c| admits_class(destination, *c, windows))
                );
            }
        }
    }
    /// Managed HyperBoost entry needs `PowerSourcePolicy.IsExternal` and a
    /// classified external reading: full (0) or limited (2) under Windows AC.
    /// Battery, unclassified external power and an unknown Windows kind refuse.
    #[cfg(feature = "hyperboost")]
    #[test]
    fn hyperboost_needs_a_classified_external_adapter_under_windows_ac() {
        for windows in [Some(false), Some(true), None] {
            for class in [-1, 0, 1, 2, crate::automation::UNCLASSIFIED_EXTERNAL, 4] {
                assert_eq!(
                    admits_class(Destination::HyperBoost, class, windows),
                    matches!(class, 0 | 2) && windows == Some(false),
                    "{class} {windows:?}"
                );
            }
        }
    }
    /// The admitted Custom range is the managed RZ09-0581 capability vector,
    /// which the managed composition test pins against its factories.
    #[test]
    fn custom_destinations_are_the_rz09_0581_capability_range() {
        let vector: serde_json::Value = serde_json::from_str(include_str!(
            "../../../../tools/openblade-windows-host/tests/data/profile-capabilities-rz09-0581.json"
        ))
        .unwrap();
        let custom = &vector["customPerformance"];
        assert_eq!(custom["supportsMaximumFans"], false);
        let allowed: std::vec::Vec<(u64, u64)> = custom["allowedCombinations"]
            .as_array()
            .unwrap()
            .iter()
            .map(|pair| (pair[0].as_u64().unwrap(), pair[1].as_u64().unwrap()))
            .collect();
        for cpu in 0..=4 {
            for gpu in 0..=4 {
                let admitted = allowed.contains(&(cpu, gpu));
                let destination = Destination::custom(cpu, gpu, false);
                assert_eq!(destination.is_some(), admitted, "{cpu}/{gpu}");
                assert_eq!(Destination::custom(cpu, gpu, true), None);
                if let Some(destination) = destination {
                    let levels = destination.custom_levels().unwrap();
                    assert_eq!((levels.cpu as u64, levels.gpu as u64), (cpu, gpu));
                    assert_eq!((destination.wire(), destination.setters()), (CUSTOM, 4));
                    assert!(!destination.is_battery());
                }
            }
        }
        for preset in PRESETS {
            let destination = Destination::from(preset);
            assert_eq!(
                (destination.wire(), destination.setters()),
                (preset as i32, 2)
            );
            assert_eq!(destination.custom_levels(), None);
        }
    }
    #[test]
    fn custom_is_direct_and_settles_only_on_its_own_read_levels() {
        use crate::thermal_observation::CustomPerformanceLevel as L;
        let requested = Destination::custom(1, 0, false).unwrap();
        let levels = requested.custom_levels().unwrap();
        for current in starting_states() {
            let own = current
                == O::Custom {
                    manual: false,
                    levels: Some(levels),
                };
            // Managed admits a direct HyperBoost exit to Custom
            // (`CanExitHyperBoostDirectlyTo`), so no state has no path.
            let expected = if own {
                Plan::Unchanged
            } else {
                Plan::Direct {
                    destination: requested,
                }
            };
            assert_eq!(
                Plan::from_observation(current, requested),
                expected,
                "{current:?}"
            );
            assert_eq!(confirms(current, requested), own);
            assert_eq!(
                pins_class(current, requested),
                current == O::HyperBoostObserved
            );
        }
        // Other levels, manual fans and unreadable levels never confirm it.
        for current in [
            O::Custom {
                manual: false,
                levels: Some(CustomPerformanceLevels {
                    cpu: L::Medium,
                    gpu: L::Medium,
                }),
            },
            O::Custom {
                manual: true,
                levels: Some(levels),
            },
            O::Custom {
                manual: false,
                levels: None,
            },
        ] {
            assert!(!confirms(current, requested), "{current:?}");
        }
    }
    #[test]
    fn each_setter_has_its_own_intent_and_acknowledgement() {
        use Progress::*;
        let custom = Destination::custom(0, 2, false).unwrap();
        let pairs = [
            (FirstIntent, FirstAcknowledged),
            (SecondIntent, SecondAcknowledged),
            (ThirdIntent, ThirdAcknowledged),
            (FourthIntent, FourthAcknowledged),
        ];
        for (index, expected) in pairs.iter().enumerate() {
            assert_eq!(Progress::setter(custom, index), Some(*expected));
            assert_eq!(
                Progress::setter(Preset::Silent.into(), index),
                (index < 2).then_some(*expected)
            );
        }
        assert_eq!(Progress::setter(custom, 4), None);
        // The Custom chain continues past the pair; a pair row cannot.
        let chain = [0, 1, 2, 3, 4, 7, 8, 9, 10, 5];
        for prior in 0..=90u8 {
            for next in 0..=90u8 {
                let custom_step =
                    chain.windows(2).any(|step| step == [prior, next]) || (prior, next) == (0, 6);
                let pair_step = (prior < 5 && next == prior + 1) || (prior, next) == (0, 6);
                assert_eq!(
                    progress_follows(CUSTOM, prior, next),
                    custom_step,
                    "{prior}->{next}"
                );
                for preset in PRESETS {
                    let restore_step = cfg!(feature = "hyperboost-exit")
                        && preset.is_battery()
                        && RESTORE.windows(2).any(|step| step == [prior, next]);
                    assert_eq!(
                        progress_follows(preset as i32, prior, next),
                        pair_step || restore_step,
                        "{preset:?} {prior}->{next}"
                    );
                }
                for destination in [-1, 6, 9] {
                    assert!(!progress_follows(destination, prior, next));
                }
            }
            assert_eq!(valid_progress(CUSTOM, prior), prior <= 10);
            assert_eq!(valid_progress(1, prior), prior <= 6);
            for battery in [3, 4] {
                let restore = cfg!(feature = "hyperboost-exit") && (80..=83).contains(&prior);
                assert_eq!(valid_progress(battery, prior), prior <= 6 || restore);
            }
            assert!(!valid_progress(6, prior));
        }
        for progress in [7, 8, 9, 10] {
            assert!(!settled(progress));
        }
    }
    /// The HyperBoost restore chain: 0, the restore pair 80-83, the battery
    /// pair 1-4, then 5.
    const RESTORE: [u8; 10] = [0, 80, 81, 82, 83, 1, 2, 3, 4, 5];
    #[cfg(feature = "hyperboost-exit")]
    #[test]
    fn hyperboost_restore_chain_is_battery_only_and_unsettled() {
        use Progress::*;
        let pairs = [
            (RestoreFirstIntent, RestoreFirstAcknowledged),
            (RestoreSecondIntent, RestoreSecondAcknowledged),
        ];
        for (index, expected) in pairs.iter().enumerate() {
            assert_eq!(Progress::restore_setter(index), Some(*expected));
        }
        assert_eq!(Progress::restore_setter(2), None);
        assert_eq!(
            [
                RestoreFirstIntent,
                RestoreFirstAcknowledged,
                RestoreSecondIntent,
                RestoreSecondAcknowledged
            ]
            .map(|p| p as u8),
            [80, 81, 82, 83]
        );
        for progress in [80, 81, 82, 83] {
            assert!(!settled(progress));
        }
        for step in [
            RestoreFirstIntent,
            RestoreSecondIntent,
            FirstIntent,
            FourthIntent,
        ] {
            assert!(step.is_intent(), "{step:?}");
        }
        for step in [
            Prepared,
            RestoreFirstAcknowledged,
            RestoreSecondAcknowledged,
            Confirmed,
            KnownNotSubmitted,
        ] {
            assert!(!step.is_intent(), "{step:?}");
        }
        // A battery row reserves the restore chain; every other row its own.
        for preset in PRESETS {
            let longest = Progress::longest_chain(preset as i32).unwrap();
            let expected: &[u8] = if preset.is_battery() {
                &RESTORE
            } else {
                &[0, 1, 2, 3, 4, 5]
            };
            assert_eq!(
                longest
                    .iter()
                    .map(|p| *p as u8)
                    .collect::<std::vec::Vec<_>>(),
                expected
            );
        }
        assert_eq!(Progress::longest_chain(CUSTOM).unwrap().len(), 10);
        assert_eq!(Progress::longest_chain(6), None);
        // Entering the restore chain from Prepared, and leaving it into the
        // battery pair, are battery-only steps; 83 never confirms directly.
        for (prior, next) in [(0, 80), (83, 1)] {
            assert!(progress_follows(3, prior, next));
            assert!(progress_follows(4, prior, next));
            for destination in [0, 1, 2, CUSTOM] {
                assert!(!progress_follows(destination, prior, next));
            }
        }
        for (prior, next) in [(83, 5), (81, 1), (80, 82), (4, 80), (0, 81)] {
            assert!(!progress_follows(3, prior, next), "{prior}->{next}");
        }
    }
    #[cfg(feature = "hyperboost-exit")]
    #[test]
    fn only_balanced_with_automatic_fans_restores() {
        for current in starting_states() {
            assert_eq!(
                restored(current),
                current == O::BalancedAutomatic,
                "{current:?}"
            );
        }
    }
    #[cfg(not(feature = "hyperboost-exit"))]
    #[test]
    fn without_the_exit_feature_no_restore_step_is_legal() {
        for progress in 11..=u8::MAX {
            for destination in [0, 1, 2, 3, 4, CUSTOM] {
                assert!(!valid_progress(destination, progress));
                assert!(!progress_follows(destination, 0, progress));
                assert!(!progress_follows(destination, progress, 1));
            }
        }
        assert_eq!(Progress::longest_chain(3).unwrap().len(), 6);
        assert_eq!(RESTORE.len(), 10);
    }
    /// HyperBoost entry (managed `SetHyperBoostCoreAsync` on exact RZ09-0581)
    /// is one direct enable pair, journalled 0-5 under its own row
    /// destination, settled only by an independent read of HyperBoost with
    /// Automatic fans, and admitted only on classified external power with
    /// fresh Windows AC presence.
    #[cfg(feature = "hyperboost")]
    #[test]
    fn hyperboost_entry_is_one_direct_pair_on_classified_external_power() {
        let entry = Destination::HyperBoost;
        assert_eq!(entry.wire(), HYPERBOOST);
        assert_eq!(entry.selector(), HYPERBOOST_SELECTOR);
        assert!(!entry.is_battery());
        assert_eq!(entry.custom_levels(), None);
        assert_eq!(entry.setters(), 2);
        assert_eq!(entry.name(), "HyperBoost");
        // Outside every managed PerformancePreset, so no preset reader
        // admits the row by accident.
        assert!(!(0..=9).contains(&HYPERBOOST));
        assert_eq!(Preset::from_wire(HYPERBOOST), None);
        for current in starting_states() {
            let already = current == O::HyperBoostObserved;
            assert_eq!(confirms(current, entry), already, "{current:?}");
            assert_eq!(
                Plan::from_observation(current, entry),
                if already {
                    Plan::Unchanged
                } else {
                    Plan::Direct { destination: entry }
                },
                "{current:?}"
            );
            // Entry never pins a class: managed admits each command on any
            // eligible adapter reading.
            assert!(!pins_class(current, entry) || already, "{current:?}");
        }
        for class in -1..=4 {
            for windows in [Some(false), Some(true), None] {
                assert_eq!(
                    admits_class(entry, class, windows),
                    matches!(class, 0 | 2) && windows == Some(false),
                    "{class} {windows:?}"
                );
            }
        }
        assert!(admits_windows(entry, Some(false)));
        assert!(!admits_windows(entry, Some(true)));
        assert!(!admits_windows(entry, None));
        // The pair chain, and nothing else: no restore or Custom step.
        assert_eq!(Progress::chain(HYPERBOOST), Some(&PAIR[..]));
        assert_eq!(Progress::longest_chain(HYPERBOOST), Some(&PAIR[..]));
        for progress in 0..=u8::MAX {
            assert_eq!(
                valid_progress(HYPERBOOST, progress),
                progress <= 6,
                "{progress}"
            );
        }
        assert!(progress_follows(HYPERBOOST, 0, 1));
        assert!(progress_follows(HYPERBOOST, 4, 5));
        assert!(progress_follows(HYPERBOOST, 0, 6));
        assert!(!progress_follows(HYPERBOOST, 0, 80));
        assert!(!progress_follows(HYPERBOOST, 4, 7));
        assert_eq!(
            Progress::setter(entry, 0),
            Some((Progress::FirstIntent, Progress::FirstAcknowledged))
        );
        assert_eq!(
            Progress::setter(entry, 1),
            Some((Progress::SecondIntent, Progress::SecondAcknowledged))
        );
        assert_eq!(Progress::setter(entry, 2), None);
    }
    /// Without the HyperBoost feature the entry row destination is no
    /// destination at all.
    #[cfg(not(feature = "hyperboost"))]
    #[test]
    fn without_the_hyperboost_feature_no_entry_row_is_legal() {
        for progress in 0..=u8::MAX {
            assert!(!valid_progress(100, progress));
            assert!(!progress_follows(100, 0, progress));
        }
        assert_eq!(Progress::chain(100), None);
    }
}

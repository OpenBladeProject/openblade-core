//! Portable direct-transition policy and durable submission chronology.
//! Fresh Windows admission, persistence and native retirement are host obligations.
use crate::thermal_observation::ObservedPerformance;
pub use openblade_types::InstalledThermalPreset as Preset;

/// One preset request's recipe on exact RZ09-0581, after managed
/// `ProductionBatteryPort.SetPerformanceAsync`: every destination is its own
/// 0D/02 pair, written directly from any confirmed non-HyperBoost state,
/// manual fans included (`CanApplyAutomaticPerformanceFrom` with
/// `SupportsDirectManualFanTransitions`). No Balanced or default intermediate
/// is written.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum Plan {
    Unchanged,
    Direct {
        destination: Preset,
    },
    /// Managed leaves HyperBoost for a battery preset through the
    /// `hyperboost-restore-balanced` pair first (`CanExitHyperBoostDirectlyTo`
    /// is false: `HyperBoostBaseModeAdmissionSource` admits direct exits to
    /// Silent, Balanced, Performance and Custom only). That pair is not
    /// admitted natively, so nothing is written.
    NoDirectPath,
}
impl Plan {
    pub fn from_observation(current: ObservedPerformance, destination: Preset) -> Self {
        if confirms(current, destination) {
            Self::Unchanged
        } else if current == ObservedPerformance::HyperBoostObserved && destination.is_battery() {
            Self::NoDirectPath
        } else {
            Self::Direct { destination }
        }
    }
}
/// The independent paired read that settles a pair: the destination's own
/// selector with Automatic fans.
pub fn confirms(current: ObservedPerformance, destination: Preset) -> bool {
    matches!(
        (current, destination),
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
/// external class for every setter. The Balanced exit has no class rule.
pub fn pins_class(current: ObservedPerformance, destination: Preset) -> bool {
    current == ObservedPerformance::HyperBoostObserved && destination != Preset::Balanced
}
/// Whether a fresh setter-time power class admits this destination's pair,
/// after managed `AdmitOrdinaryThermalCommandAsync`. `windows_on_battery` is
/// the fresh Windows power kind: `Some(false)` AC, `Some(true)` battery.
/// - The ordinary selectors (Silent 5, Balanced 0, Performance 2) need an
///   external class with fresh Windows AC presence, unclassified (3) included.
///   Windows battery refuses them (`allowBattery: false`).
/// - The battery selectors (3 and 6, `allowBattery: true`) are admitted on
///   Windows battery, and on a classified external class (0 or 2) that agrees
///   with Windows AC. Unclassified power refuses them: managed admits an
///   unknown adapter only for ordinary commands. Managed skips the adapter
///   read on Windows battery; the host still takes one and needs it to read
///   battery (1), which is stricter.
pub fn admits_class(destination: Preset, class: i32, windows_on_battery: Option<bool>) -> bool {
    match class {
        0 | 2 => windows_on_battery == Some(false),
        crate::automation::UNCLASSIFIED_EXTERNAL => {
            !destination.is_battery() && windows_on_battery == Some(false)
        }
        1 => destination.is_battery() && windows_on_battery == Some(true),
        _ => false,
    }
}
/// The destinations a native fan return to Automatic applies in its own
/// pair (`fan_recipe::Plan::select_toward`). Managed applies every saved preset
/// there (`ApplyDirectAutomaticFanTransitionOutcomeAsync`); the native fan
/// recipe and its journal grammar (selectors 0 and 2) cover Balanced and
/// Performance only, so any other saved preset keeps the observed selector
/// and needs a new performance request.
pub fn applies_on_fan_handback(destination: Preset) -> bool {
    matches!(destination, Preset::Balanced | Preset::Performance)
}
/// Intent precedes possible submission. Acknowledgement never settles the
/// transaction; only a new independent paired read can do so.
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
}
impl Progress {
    pub fn next(self, next: Self) -> bool {
        progress_follows(self as u8, next as u8)
    }
    pub fn blocks_restart(self) -> bool {
        !settled(self as u8)
    }
}
pub fn settled(progress: u8) -> bool {
    matches!(progress, 5 | 6 | 30 | 50 | 51 | 62 | 63 | 72 | 73)
}
pub fn progress_follows(prior: u8, next: u8) -> bool {
    (prior < 5 && next == prior + 1) || (prior == 0 && next == 6)
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
        for destination in [Preset::Balanced, Preset::Performance] {
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
                Preset::Performance
            ),
            Plan::Unchanged
        );
        assert!(Progress::SecondAcknowledged.blocks_restart());
        assert!(!Progress::Prepared.next(Progress::FirstAcknowledged));
        assert!(Progress::SecondAcknowledged.next(Progress::Confirmed));
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
        for destination in PRESETS {
            for current in starting_states() {
                let own = current.projection() == (Some(destination as i32), 0, Some(false));
                let expected = if own {
                    Plan::Unchanged
                } else if current == O::HyperBoostObserved && destination.is_battery() {
                    Plan::NoDirectPath
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
                    current == O::HyperBoostObserved && destination != Preset::Balanced
                );
            }
        }
    }
    #[test]
    fn power_classes_follow_the_ordinary_and_battery_selector_rules() {
        for destination in PRESETS {
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
            }
            assert_eq!(
                applies_on_fan_handback(destination),
                matches!(destination, Preset::Balanced | Preset::Performance)
            );
        }
    }
}

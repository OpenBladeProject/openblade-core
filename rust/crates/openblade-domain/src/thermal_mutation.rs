//! Portable direct-transition policy and durable submission chronology.
//! Fresh Windows admission, persistence and native retirement are host obligations.
use crate::thermal_observation::ObservedPerformance;
pub use openblade_types::InstalledThermalPreset as Preset;

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum Plan {
    Unchanged,
    Direct { destination: Preset },
}
impl Plan {
    pub fn from_observation(current: ObservedPerformance, destination: Preset) -> Self {
        if confirms(current, destination) {
            Self::Unchanged
        } else {
            Self::Direct { destination }
        }
    }
}
pub fn confirms(current: ObservedPerformance, destination: Preset) -> bool {
    matches!(
        (current, destination),
        (ObservedPerformance::BalancedAutomatic, Preset::Balanced)
            | (
                ObservedPerformance::PerformanceAutomatic,
                Preset::Performance
            )
    )
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
}

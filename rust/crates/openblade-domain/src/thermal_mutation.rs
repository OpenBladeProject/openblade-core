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
}
impl Progress {
    pub fn next(self, next: Self) -> bool {
        next as u8 == self as u8 + 1
    }
    pub fn blocks_restart(self) -> bool {
        self != Self::Confirmed
    }
}

#[cfg(test)]
mod tests {
    use super::*;
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

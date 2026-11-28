//! Complete portable thermal observations; no I/O or write authority.
pub use openblade_types::CustomPerformanceLevel;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ObservedPerformance {
    BalancedAutomatic,
    PerformanceAutomatic,
    SilentAutomatic,
    SilentManual,
    PerformanceManual,
    BatterySaverAutomatic,
    BatteryBalancedAutomatic,
    BatterySaverManual,
    BatteryBalancedManual,
    FixedMaximum,
    HyperBoostObserved,
    Custom {
        manual: bool,
        levels: Option<CustomPerformanceLevels>,
    },
}

impl ObservedPerformance {
    pub fn projection(self) -> (Option<i32>, i32, Option<bool>) {
        match self {
            Self::BalancedAutomatic => (Some(1), 0, Some(false)),
            Self::PerformanceAutomatic => (Some(2), 0, Some(false)),
            Self::SilentAutomatic => (Some(0), 0, Some(false)),
            Self::SilentManual => (Some(0), 3, Some(false)),
            Self::PerformanceManual => (Some(2), 3, Some(false)),
            Self::BatterySaverAutomatic => (Some(3), 0, Some(false)),
            Self::BatteryBalancedAutomatic => (Some(4), 0, Some(false)),
            Self::BatterySaverManual => (Some(3), 3, Some(false)),
            Self::BatteryBalancedManual => (Some(4), 3, Some(false)),
            Self::FixedMaximum => (None, 3, Some(false)),
            Self::HyperBoostObserved => (None, 0, Some(true)),
            Self::Custom { manual, .. } => (Some(5), if manual { 3 } else { 0 }, Some(false)),
        }
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct CustomPerformanceLevels {
    pub cpu: CustomPerformanceLevel,
    pub gpu: CustomPerformanceLevel,
}
impl ObservedPerformance {
    /// Exact RZ09-0581 getter admission, independent of saved-schema/write policy.
    /// Manual High/High confirms selector/fans only, keeping Automatic handback possible.
    pub fn rz09_0581_custom(
        manual: bool,
        cpu: CustomPerformanceLevel,
        gpu: CustomPerformanceLevel,
    ) -> Option<Self> {
        let high_high = cpu == CustomPerformanceLevel::High && gpu == CustomPerformanceLevel::High;
        if high_high && !manual {
            return None;
        }
        Some(Self::Custom {
            manual,
            levels: (!high_high).then_some(CustomPerformanceLevels { cpu, gpu }),
        })
    }
    pub fn custom_levels(self) -> Option<CustomPerformanceLevels> {
        match self {
            Self::Custom { levels, .. } => levels,
            _ => None,
        }
    }
}

//! Pure exact-backend selection. A desired target is never firmware readback.
use crate::thermal_observation::ObservedPerformance as O;
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
#[cfg_attr(
    feature = "thermal-history",
    derive(serde::Serialize, serde::Deserialize)
)]
#[cfg_attr(feature = "thermal-history", serde(deny_unknown_fields))]
pub struct FanIntent {
    pub mode: u8,
    pub rpm: u16,
}
impl FanIntent {
    pub fn checked(mode: u8, rpm: u16) -> Option<Self> {
        if !matches!(mode, 0 | 1 | 3) || !(2000..=5400).contains(&rpm) || !rpm.is_multiple_of(100) {
            return None;
        }
        Some(Self {
            mode,
            rpm: if mode == 3 { 5400 } else { rpm },
        })
    }
    pub fn manual(self) -> bool {
        self.mode != 0
    }
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Plan {
    pub intent: FanIntent,
    pub selector: u8,
    pub entry: bool,
    pub intermediate: bool,
    pub source: i32,
    pub unchanged: bool,
}
impl Plan {
    pub fn select(baseline: O, intent: FanIntent, source: i32) -> Option<Self> {
        if FanIntent::checked(intent.mode, intent.rpm) != Some(intent) || !matches!(source, 0 | 2) {
            return None;
        }
        let (selector, manual) = match baseline {
            O::BalancedAutomatic => (0, false),
            O::PerformanceAutomatic => (2, false),
            O::PerformanceManual => (2, true),
            O::FixedMaximum => (0, true),
            _ => return None,
        };
        if selector == 2 && source != 0 {
            return None;
        }
        Some(Self {
            intent,
            selector,
            entry: intent.manual() && !manual,
            intermediate: intent.manual() && !manual && selector == 2,
            source,
            unchanged: !intent.manual() && !manual,
        })
    }
    pub fn setters(self) -> usize {
        if self.unchanged {
            0
        } else if self.entry {
            4
        } else {
            2
        }
    }
    pub fn pairs(self) -> usize {
        self.setters() + 1 + usize::from(self.intermediate)
    }
    pub fn confirms(self, o: O) -> bool {
        matches!(
            (self.selector, self.intent.manual(), o),
            (0, true, O::FixedMaximum)
                | (2, true, O::PerformanceManual)
                | (0, false, O::BalancedAutomatic)
                | (2, false, O::PerformanceAutomatic)
        )
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn checked_direct_recipes_keep_unknown_fields_unknown() {
        for rpm in [2000, 2100, 3700, 4000, 5400] {
            let v = FanIntent::checked(1, rpm).unwrap();
            let p = Plan::select(O::FixedMaximum, v, 0).unwrap();
            assert_eq!(p.setters(), 2);
            assert_eq!(p.pairs(), 3);
            assert!(!p.entry);
        }
        for rpm in [1999, 2001, 5500] {
            assert!(FanIntent::checked(1, rpm).is_none());
        }
        let maximum = FanIntent::checked(3, 3700).unwrap();
        assert_eq!(maximum.rpm, 5400);
        assert_ne!(maximum, FanIntent::checked(1, 5400).unwrap());
        let p = Plan::select(O::PerformanceAutomatic, maximum, 0).unwrap();
        assert_eq!(p.pairs(), 6);
        assert!(p.intermediate);
        assert!(!p.confirms(O::FixedMaximum));
        assert_eq!(
            Plan::select(O::BalancedAutomatic, maximum, 2)
                .unwrap()
                .pairs(),
            5
        );
        assert!(Plan::select(O::PerformanceAutomatic, maximum, 2).is_none());
        assert!(Plan::select(O::HyperBoostObserved, maximum, 0).is_none());
        assert!(
            Plan::select(
                O::BalancedAutomatic,
                FanIntent::checked(0, 3700).unwrap(),
                0
            )
            .unwrap()
            .unchanged
        );
    }
}

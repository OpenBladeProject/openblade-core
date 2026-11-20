//! Pure exact-backend selection. A desired target is never firmware readback.
use crate::thermal_mutation::{Destination, confirms};
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
/// Fans firmware reports as Automatic: firmware's own handback state, whatever
/// the selector, the power source and whether a native recipe exists for it.
/// Managed releases fan ownership on any confirmed Automatic fan mode
/// (`ExactFanControlOwnership.ObserveTopology`), which includes Custom with
/// Automatic fans and the HyperBoost pair (mode 7 with Automatic fans; see
/// `ServiceControlRuntime.ClassifyObservedFanTopology`). Mode 7 with manual
/// fans is not decoded, so it never reaches this predicate.
pub fn automatic_handback(o: O) -> bool {
    matches!(
        o,
        O::BalancedAutomatic
            | O::PerformanceAutomatic
            | O::SilentAutomatic
            | O::BatterySaverAutomatic
            | O::BatteryBalancedAutomatic
            | O::HyperBoostObserved
            | O::Custom { manual: false, .. }
    )
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Plan {
    pub intent: FanIntent,
    pub selector: u8,
    pub entry: bool,
    pub intermediate: bool,
    pub source: i32,
    pub unchanged: bool,
    /// The saved destination a return to Automatic writes, if any.
    pub destination: Option<Destination>,
}
impl Plan {
    pub fn select(baseline: O, intent: FanIntent, source: i32) -> Option<Self> {
        if FanIntent::checked(intent.mode, intent.rpm) != Some(intent)
            || !matches!(source, 0 | 2 | crate::automation::UNCLASSIFIED_EXTERNAL)
        {
            return None;
        }
        let (selector, manual) = match baseline {
            O::BalancedAutomatic => (0, false),
            O::PerformanceAutomatic => (2, false),
            O::PerformanceManual => (2, true),
            O::FixedMaximum => (0, true),
            _ => return None,
        };
        // Managed keeps the observed AC selector on both classified external
        // sources (full 0 and limited 2) and on unclassified external power
        // (3, `ProductionBatteryPort` UnclassifiedExternal) with the same
        // commands; the host admits 3 only with fresh Windows AC presence.
        Some(Self {
            intent,
            selector,
            entry: intent.manual() && !manual,
            intermediate: intent.manual() && !manual && selector == 2,
            source,
            unchanged: !intent.manual() && !manual,
            destination: None,
        })
    }
    /// A return to Automatic that also applies the saved destination. Managed
    /// (`ApplyDirectAutomaticFanTransitionOutcomeAsync`) leaves manual fans
    /// through that destination's own recipe: a preset's 0D/02 pair, which
    /// carries the Automatic fan mode, or Custom's selector-4 pair and its CPU
    /// and GPU 0D/07 setters. There is no fan pair first. Manual intents and
    /// `None` keep the observed selector. Unclassified external power admits no
    /// battery preset (`thermal_mutation::admits_class`).
    pub fn select_toward(
        baseline: O,
        intent: FanIntent,
        source: i32,
        performance: Option<Destination>,
    ) -> Option<Self> {
        let mut plan = Self::select(baseline, intent, source)?;
        if let Some(destination) = performance.filter(|_| !intent.manual()) {
            if destination.is_battery() && source == crate::automation::UNCLASSIFIED_EXTERNAL {
                return None;
            }
            plan.unchanged &= confirms(baseline, destination);
            plan.selector = destination.selector();
            plan.destination = Some(destination);
        }
        Some(plan)
    }
    /// Custom's two level setters follow its pair.
    pub fn setters(self) -> usize {
        if self.unchanged {
            0
        } else if self.entry || self.custom_levels().is_some() {
            4
        } else {
            2
        }
    }
    /// Setter pairs, power readings and final reads, Custom's 0D/87 pair included.
    pub fn pairs(self) -> usize {
        self.setters()
            + 1
            + usize::from(self.intermediate)
            + usize::from(self.custom_levels().is_some())
    }
    pub fn custom_levels(self) -> Option<crate::thermal_observation::CustomPerformanceLevels> {
        self.destination.and_then(Destination::custom_levels)
    }
    /// A carried destination settles only as the performance writer's own
    /// read does (`thermal_mutation::confirms`).
    pub fn confirms(self, o: O) -> bool {
        if let Some(destination) = self.destination {
            return confirms(o, destination);
        }
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
    fn automatic_handback_needs_automatic_fans_not_a_plan() {
        use crate::thermal_observation::{CustomPerformanceLevel as L, CustomPerformanceLevels};
        let auto = FanIntent::checked(0, 3700).unwrap();
        let levels = Some(CustomPerformanceLevels {
            cpu: L::Medium,
            gpu: L::High,
        });
        for o in [
            O::BalancedAutomatic,
            O::PerformanceAutomatic,
            O::SilentAutomatic,
            O::BatterySaverAutomatic,
            O::BatteryBalancedAutomatic,
            // Managed maps both to FanMode.Automatic (ProductionBatteryPort
            // MapThermalState), which ObserveTopology releases.
            O::HyperBoostObserved,
            O::Custom {
                manual: false,
                levels: None,
            },
            O::Custom {
                manual: false,
                levels,
            },
        ] {
            assert!(automatic_handback(o), "{o:?}");
            // Battery has no plan at all, yet the fans are already handed back.
            assert!(Plan::select(o, auto, 1).is_none());
        }
        for o in [
            O::SilentManual,
            O::PerformanceManual,
            O::BatterySaverManual,
            O::BatteryBalancedManual,
            O::FixedMaximum,
            O::Custom {
                manual: true,
                levels: None,
            },
            O::Custom {
                manual: true,
                levels,
            },
        ] {
            assert!(!automatic_handback(o), "{o:?}");
        }
    }
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
        let limited = Plan::select(O::PerformanceAutomatic, maximum, 2).unwrap();
        assert_eq!((limited.selector, limited.pairs()), (2, 6));
        assert!(Plan::select(O::PerformanceAutomatic, maximum, 1).is_none());
        let unclassified = Plan::select(O::PerformanceAutomatic, maximum, 3).unwrap();
        assert_eq!((unclassified.selector, unclassified.pairs()), (2, 6));
        for source in [-1, 4, i32::MAX] {
            assert!(Plan::select(O::PerformanceAutomatic, maximum, source).is_none());
        }
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
    #[test]
    fn automatic_return_takes_every_saved_destination_directly() {
        use crate::thermal_mutation::Preset;
        use crate::thermal_observation::{CustomPerformanceLevel as L, CustomPerformanceLevels};
        let auto = FanIntent::checked(0, 3700).unwrap();
        let custom = Destination::custom(2, 1, false).unwrap();
        let levels = CustomPerformanceLevels {
            cpu: L::High,
            gpu: L::Medium,
        };
        for source in [0, 2, 3] {
            for baseline in [O::FixedMaximum, O::PerformanceManual] {
                for (destination, selector, settled) in [
                    (Preset::Silent.into(), 5, O::SilentAutomatic),
                    (Preset::BatterySaver.into(), 3, O::BatterySaverAutomatic),
                    (
                        Preset::BatteryBalanced.into(),
                        6,
                        O::BatteryBalancedAutomatic,
                    ),
                    (
                        custom,
                        4,
                        O::Custom {
                            manual: false,
                            levels: Some(levels),
                        },
                    ),
                ] {
                    let p = Plan::select_toward(baseline, auto, source, Some(destination));
                    if source == 3 && destination.is_battery() {
                        assert!(p.is_none(), "{destination:?}");
                        continue;
                    }
                    let p = p.unwrap();
                    let setters = if destination == custom { 4 } else { 2 };
                    assert_eq!(
                        (p.selector, p.setters(), p.pairs()),
                        (
                            selector,
                            setters,
                            setters + 1 + usize::from(destination == custom)
                        )
                    );
                    assert!(!p.entry && !p.intermediate && !p.unchanged);
                    assert!(p.confirms(settled), "{destination:?}");
                    assert!(!p.confirms(baseline));
                    if destination == custom {
                        // Other levels, unread levels or manual fans confirm nothing.
                        for o in [
                            O::Custom {
                                manual: false,
                                levels: Some(CustomPerformanceLevels {
                                    cpu: L::High,
                                    gpu: L::High,
                                }),
                            },
                            O::Custom {
                                manual: false,
                                levels: None,
                            },
                            O::Custom {
                                manual: true,
                                levels: Some(levels),
                            },
                        ] {
                            assert!(!p.confirms(o), "{o:?}");
                        }
                    }
                }
            }
        }
        // Custom already confirmed with other levels is written in full.
        let p = Plan::select_toward(O::BalancedAutomatic, auto, 0, Some(custom)).unwrap();
        assert_eq!((p.setters(), p.unchanged), (4, false));
    }
    #[test]
    fn automatic_return_takes_the_saved_preset_in_one_pair() {
        use crate::thermal_mutation::Preset;
        let auto = FanIntent::checked(0, 3700).unwrap();
        // Unclassified external power (3) uses the same commands and selector.
        for source in [0, 2, 3] {
            // Manual fans under either selector leave through the saved preset.
            for baseline in [O::FixedMaximum, O::PerformanceManual] {
                for (preset, selector, settled) in [
                    (Preset::Balanced, 0, O::BalancedAutomatic),
                    (Preset::Performance, 2, O::PerformanceAutomatic),
                ] {
                    let p =
                        Plan::select_toward(baseline, auto, source, Some(preset.into())).unwrap();
                    assert_eq!((p.selector, p.setters(), p.pairs()), (selector, 2, 3));
                    assert!(!p.entry && !p.intermediate && !p.unchanged);
                    assert!(p.confirms(settled));
                    assert!(!p.confirms(baseline));
                }
            }
            // Automatic under another preset is a preset pair; the same preset is a no-op.
            let p = Plan::select_toward(
                O::BalancedAutomatic,
                auto,
                source,
                Some(Preset::Performance.into()),
            )
            .unwrap();
            assert_eq!((p.selector, p.setters(), p.unchanged), (2, 2, false));
            let p = Plan::select_toward(
                O::PerformanceAutomatic,
                auto,
                source,
                Some(Preset::Performance.into()),
            )
            .unwrap();
            assert!(p.unchanged);
            // Without a saved preset the observed selector is kept.
            let p = Plan::select_toward(O::PerformanceManual, auto, source, None).unwrap();
            assert_eq!(p, Plan::select(O::PerformanceManual, auto, source).unwrap());
            assert_eq!(p.selector, 2);
        }
        // A manual intent never takes a preset, and battery stays refused.
        let fixed = FanIntent::checked(1, 3700).unwrap();
        assert_eq!(
            Plan::select_toward(
                O::PerformanceAutomatic,
                fixed,
                0,
                Some(Preset::Balanced.into())
            ),
            Plan::select(O::PerformanceAutomatic, fixed, 0)
        );
        assert!(
            Plan::select_toward(O::FixedMaximum, auto, 1, Some(Preset::Balanced.into())).is_none()
        );
    }
}

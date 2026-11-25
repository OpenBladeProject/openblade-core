//! Managed `PerformanceShortcutSelection`: the next performance preset a
//! performance shortcut selects. The caller admits the physical supply, the
//! explicit action and the model's preset set; this neither classifies an
//! adapter nor reads saved intent.

/// Which managed cycle applies: `GetCycle` for a classified external source
/// (PluggedIn or UsbC) or Battery, and `GetOrdinaryCycle` for external power
/// whose adapter is unclassified.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum Cycle {
    External,
    Battery,
    Ordinary,
}

// Managed PerformancePreset wire values.
const SILENT: i32 = 0;
const BALANCED: i32 = 1;
const PERFORMANCE: i32 = 2;
const BATTERY_SAVER: i32 = 3;
const BATTERY_BALANCED: i32 = 4;
const CUSTOM: i32 = 5;
const TURBO: i32 = 6;
const MAXIMUM_POWER: i32 = 7;
const GAMING: i32 = 8;
const CREATOR: i32 = 9;

/// Managed `OrdinaryOrder`.
const ORDINARY: [i32; 8] = [
    BALANCED,
    SILENT,
    PERFORMANCE,
    TURBO,
    MAXIMUM_POWER,
    GAMING,
    CREATOR,
    CUSTOM,
];
/// Managed `ExternalOrder`: the ordinary order, then the battery presets.
const EXTERNAL: [i32; 10] = [
    BALANCED,
    SILENT,
    PERFORMANCE,
    TURBO,
    MAXIMUM_POWER,
    GAMING,
    CREATOR,
    CUSTOM,
    BATTERY_BALANCED,
    BATTERY_SAVER,
];
const BATTERY: [i32; 2] = [BATTERY_BALANCED, BATTERY_SAVER];

impl Cycle {
    fn order(self) -> &'static [i32] {
        match self {
            Self::External => &EXTERNAL,
            Self::Battery => &BATTERY,
            Self::Ordinary => &ORDINARY,
        }
    }

    /// Whether `preset` is in this cycle for the admitted presets. An empty
    /// set is managed's unverified capability: the classified cycles keep
    /// their established presets, without the four model-specific ones and,
    /// on external power, without the battery presets; the ordinary cycle
    /// has none.
    fn includes(self, admitted: &[i32], preset: i32) -> bool {
        if !admitted.is_empty() {
            return admitted.contains(&preset);
        }
        let established = !matches!(preset, TURBO | MAXIMUM_POWER | GAMING | CREATOR);
        match self {
            Self::External => established && !matches!(preset, BATTERY_SAVER | BATTERY_BALANCED),
            Self::Battery => established,
            Self::Ordinary => false,
        }
    }
}

/// Managed `TrySelectNext` and `TrySelectNextOrdinary`: the preset after
/// `current` in the cycle, the first one when `current` is not in it, and
/// none when the cycle has fewer than two presets.
pub fn next(cycle: Cycle, admitted: &[i32], current: i32) -> Option<i32> {
    let mut presets = cycle
        .order()
        .iter()
        .copied()
        .filter(|preset| cycle.includes(admitted, *preset));
    let count = presets.clone().count();
    if count < 2 {
        return None;
    }
    let position = presets.clone().position(|preset| preset == current);
    presets.nth(position.map_or(0, |index| (index + 1) % count))
}

#[cfg(test)]
mod tests {
    use super::*;

    const RZ09_0581: [i32; 6] = [
        SILENT,
        BALANCED,
        PERFORMANCE,
        BATTERY_SAVER,
        BATTERY_BALANCED,
        CUSTOM,
    ];

    #[test]
    fn the_rz09_0581_external_cycle_wraps_through_the_battery_presets() {
        let order = [
            BALANCED,
            SILENT,
            PERFORMANCE,
            CUSTOM,
            BATTERY_BALANCED,
            BATTERY_SAVER,
        ];
        for (index, current) in order.iter().enumerate() {
            assert_eq!(
                next(Cycle::External, &RZ09_0581, *current),
                Some(order[(index + 1) % order.len()])
            );
        }
        // A preset outside the cycle starts it from its first preset.
        assert_eq!(next(Cycle::External, &RZ09_0581, TURBO), Some(BALANCED));
        assert_eq!(next(Cycle::External, &RZ09_0581, -1), Some(BALANCED));
    }

    #[test]
    fn battery_and_ordinary_cycles_keep_only_their_presets() {
        assert_eq!(
            next(Cycle::Battery, &RZ09_0581, BATTERY_BALANCED),
            Some(BATTERY_SAVER)
        );
        assert_eq!(
            next(Cycle::Battery, &RZ09_0581, BATTERY_SAVER),
            Some(BATTERY_BALANCED)
        );
        assert_eq!(
            next(Cycle::Battery, &RZ09_0581, BALANCED),
            Some(BATTERY_BALANCED)
        );
        assert_eq!(next(Cycle::Ordinary, &RZ09_0581, PERFORMANCE), Some(CUSTOM));
        assert_eq!(next(Cycle::Ordinary, &RZ09_0581, CUSTOM), Some(BALANCED));
        assert_eq!(
            next(Cycle::Ordinary, &RZ09_0581, BATTERY_SAVER),
            Some(BALANCED)
        );
    }

    #[test]
    fn a_cycle_of_fewer_than_two_presets_selects_nothing() {
        assert_eq!(
            next(Cycle::Battery, &[BATTERY_SAVER, SILENT], BATTERY_SAVER),
            None
        );
        assert_eq!(next(Cycle::Ordinary, &[], BALANCED), None);
        assert_eq!(
            next(Cycle::Ordinary, &[CUSTOM, BATTERY_SAVER], CUSTOM),
            None
        );
    }

    #[test]
    fn an_unverified_capability_keeps_the_established_cycles() {
        assert_eq!(next(Cycle::External, &[], CUSTOM), Some(BALANCED));
        assert_eq!(next(Cycle::External, &[], PERFORMANCE), Some(CUSTOM));
        assert_eq!(
            next(Cycle::Battery, &[], BATTERY_SAVER),
            Some(BATTERY_BALANCED)
        );
    }
}

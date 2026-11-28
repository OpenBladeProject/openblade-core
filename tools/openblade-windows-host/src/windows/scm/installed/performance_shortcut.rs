//! Managed `CyclePerformanceShortcutAsync` on the native performance path.
//!
//! The shortcut becomes an ordinary performance request once its override is
//! staged: `RequestPermit::resolve` stages it from the fresh 07/8C class and
//! 0D/82 reading, before it runs the request's checks, saves it and writes
//! the destination through the reviewed recipe. Only the reply text is the
//! shortcut's own (`Source::reply`).
use super::super::thermal_storage::{Override, ProfileLayer, Storage, custom_destination};
use super::{Cause, Fresh, managed_power_source};
use crate::discovery::performance::ObservedPerformance;
use openblade_domain::{
    automation::UNCLASSIFIED_EXTERNAL,
    performance_shortcut::{self, Cycle},
    profile_set::{Capabilities, CustomPerformance, PerformancePreset},
    thermal_mutation::CUSTOM,
};
use serde_json::{Value, json};
use std::io;

/// Managed `PerformanceShortcutSource`.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(in super::super) enum Source {
    DedicatedButton,
    FunctionP,
}

impl Source {
    /// The wire value the request codec admits (`Enum.IsDefined`).
    pub(in super::super) fn from_wire(source: i32) -> Option<Self> {
        match source {
            0 => Some(Self::DedicatedButton),
            1 => Some(Self::FunctionP),
            _ => None,
        }
    }

    /// Managed's reply to a shortcut whose destination was applied, status
    /// 1, or stored for the fan return, status 0. Every other reply keeps
    /// the performance request's own text.
    pub(super) fn reply(self, status: u8, preset: i32) -> Option<String> {
        // Managed prints `next.ToString()`, which is the wire name of every
        // RZ09-0581 preset.
        let name = PerformancePreset(preset).name()?;
        let selected = match self {
            Self::DedicatedButton => format!("The performance button selected {name}."),
            Self::FunctionP => format!("Fn+P selected {name}."),
        };
        match status {
            1 => Some(selected),
            0 => Some(format!(
                "{selected} It will be applied when fans return to Automatic."
            )),
            _ => None,
        }
    }
}

/// Managed `PublishScopedUnavailable` for an unclassified adapter that does
/// not admit ordinary control (`FreshUnclassifiedExternalControlIsAdmitted`).
pub(super) const UNCLASSIFIED_BLOCKED: &str = "Hardware application was not confirmed. The external power adapter could not be classified; the performance shortcut is blocked.";
const STATE_UNAVAILABLE: &str =
    "The current thermal state is unavailable for ordinary performance cycling.";
const NO_ALTERNATE: &str =
    "No alternate performance preset is available for the current power source.";
/// The 07/8C battery class, managed `PowerSource.Battery`.
const BATTERY: i32 = 1;

/// What a shortcut's fresh reading selects.
pub(super) struct Reading<'a> {
    pub(super) storage: &'a Storage,
    pub(super) fresh: &'a dyn Fresh,
    /// The fresh 07/8C class.
    pub(super) power: i32,
    /// The saved profiles the request step read, with its active pair.
    pub(super) layer: &'a ProfileLayer,
    /// The fresh 0D/82 reading; none when no firmware was read.
    pub(super) baseline: Option<ObservedPerformance>,
    /// Confirmed drift has marked Performance as changed outside OpenBlade
    /// (managed `ExternalChanges`).
    pub(super) external_performance: bool,
}

/// The UntilPowerSourceChange override managed stages for the next preset,
/// or the refusal that stores nothing.
pub(super) fn stage(reading: &Reading<'_>) -> io::Result<Result<Override, Cause>> {
    let refused = |message| Ok(Err(Cause::Shortcut(message)));
    let unclassified = reading.power == UNCLASSIFIED_EXTERNAL;
    let cycle = if unclassified {
        Cycle::Ordinary
    } else if managed_power_source(reading.power, reading.fresh) == Some(1) {
        Cycle::Battery
    } else {
        Cycle::External
    };
    // Managed resolves the cycle's current preset and the profile's Custom
    // settings under the power source it cycles: Battery whenever Windows
    // reports battery. The thermal worker admits a performance request under
    // Windows battery only with a battery reading (`Power::thermal_source`),
    // so on every admitted path this is the adapter class, where the
    // override is saved and its destination selected.
    let source = if cycle == Cycle::Battery {
        BATTERY
    } else {
        reading.power
    };
    let observed = reading
        .baseline
        .and_then(|baseline| baseline.projection().0);
    let mut admitted: Vec<i32> = Capabilities::rz09_0581()
        .performance_presets
        .iter()
        .map(|preset| preset.0)
        .collect();
    let (current, ordinary_custom) = if unclassified {
        // Managed cycles from the confirmed preset there, and offers Custom
        // only with an admitted ordinary Custom intent.
        let Some(current) = observed else {
            return refused(STATE_UNAVAILABLE);
        };
        let custom = ordinary_custom(reading)?;
        if custom.is_none() {
            admitted.retain(|preset| *preset != CUSTOM);
        }
        (current, custom)
    } else {
        let current = match observed.filter(|_| reading.external_performance) {
            Some(observed) => observed,
            // A preset outside the cycle starts it from its first preset.
            None => reading
                .storage
                .selected_performance(source, reading.layer.at(source)?.as_ref())?
                .unwrap_or(-1),
        };
        (current, None)
    };
    let Some(next) = performance_shortcut::next(cycle, &admitted, current) else {
        return refused(NO_ALTERNATE);
    };
    let custom_performance = match (next == CUSTOM, unclassified) {
        (false, _) => None,
        (true, true) => ordinary_custom,
        (true, false) => Some(
            reading
                .storage
                .profile_custom_performance(source)?
                .unwrap_or_else(|| settings(CustomPerformance::DEFAULT)),
        ),
    };
    let staged = Override {
        value: next,
        lifetime: 0,
        custom_performance,
        // Managed stages the desired state's GPU tuning
        // (`current.DesiredState.GpuClockTuning`) and none on unclassified
        // power, where it is not admitted: what `ProfileAutomationState.Resolve`
        // selects at the reading's class, the overrides' and the profile's
        // tuning and a selected Custom's legacy offsets included
        // (`Storage::gpu_tuning`).
        gpu_clock_tuning: if unclassified {
            None
        } else {
            reading
                .storage
                .gpu_tuning(reading.power, reading.layer.at(reading.power)?.as_ref())?
        },
    };
    staged.validate()?;
    Ok(Ok(staged))
}

/// Managed `ResolveOrdinaryShortcutCustomIntent`: the saved override's
/// Custom settings without legacy offsets, then the confirmed levels the
/// model admits, then managed's default, kept only when the model admits it.
fn ordinary_custom(reading: &Reading<'_>) -> io::Result<Option<Value>> {
    let explicit = reading
        .storage
        .current_override(reading.power)?
        .and_then(|current| current.custom_performance)
        .map(|mut custom| {
            custom["gpuClockOffsets"] = Value::Null;
            custom
        });
    let confirmed = match reading.baseline {
        Some(ObservedPerformance::Custom {
            levels: Some(levels),
            ..
        }) => Some(json!({
            "cpu": levels.cpu as u8,
            "gpu": levels.gpu as u8,
            "maximumFans": false,
            "gpuClockOffsets": null,
        })),
        _ => None,
    }
    .filter(|custom| custom_destination(custom).is_some());
    let custom = explicit
        .or(confirmed)
        .unwrap_or_else(|| settings(CustomPerformance::DEFAULT));
    // The model admits no Custom maximum fans, so the managed Automatic-fan
    // condition for them never applies (`Destination::custom`).
    Ok(custom_destination(&custom).is_some().then_some(custom))
}

/// Managed `CustomPerformanceSettings` without legacy offsets.
fn settings(custom: CustomPerformance) -> Value {
    json!({
        "cpu": custom.cpu.0,
        "gpu": custom.gpu.0,
        "maximumFans": custom.maximum_fans,
        "gpuClockOffsets": null,
    })
}

#[cfg(test)]
mod tests {
    use super::Source;
    use openblade_domain::performance_shortcut::{self, Cycle};
    use serde_json::Value;

    /// Managed `PerformanceShortcutSelection`, recorded by
    /// `ServiceControlRuntimeTests.NativePerformanceShortcutVectors`.
    const VECTORS: &str = include_str!("../../../../tests/data/performance-shortcut-vectors.json");

    #[test]
    fn selection_replays_the_managed_vectors() {
        let vectors: Value = serde_json::from_str(VECTORS).unwrap();
        let rows = vectors["selection"].as_array().unwrap();
        assert_eq!(rows.len(), 12);
        for row in rows {
            let cycle = match row["cycle"].as_str().unwrap() {
                "pluggedIn" | "usbC" => Cycle::External,
                "battery" => Cycle::Battery,
                "ordinary" => Cycle::Ordinary,
                other => panic!("unrecorded cycle {other}"),
            };
            // An unverified capability is recorded as null presets.
            let admitted: Vec<i32> = row["presets"].as_array().map_or_else(Vec::new, |presets| {
                presets
                    .iter()
                    .map(|preset| preset.as_i64().unwrap() as i32)
                    .collect()
            });
            let next = row["next"].as_array().unwrap();
            assert_eq!(next.len(), 10, "{row}");
            for (current, expected) in next.iter().enumerate() {
                let expected = expected.as_i64().map(|preset| preset as i32);
                assert_eq!(
                    performance_shortcut::next(cycle, &admitted, current as i32),
                    expected,
                    "{row} from {current}"
                );
            }
        }
    }

    #[test]
    fn the_reply_names_the_selected_preset_only_when_it_applied_or_was_stored() {
        // Managed prints `next.ToString()`, and suffixes a Stored reply.
        assert_eq!(
            Source::FunctionP.reply(1, 4).as_deref(),
            Some("Fn+P selected BatteryBalanced.")
        );
        assert_eq!(
            Source::DedicatedButton.reply(0, 5).as_deref(),
            Some(
                "The performance button selected Custom. It will be applied when fans return to Automatic."
            )
        );
        for status in [2, 3] {
            assert_eq!(Source::FunctionP.reply(status, 0), None);
        }
        assert_eq!(Source::from_wire(0), Some(Source::DedicatedButton));
        assert_eq!(Source::from_wire(1), Some(Source::FunctionP));
        assert_eq!(Source::from_wire(2), None);
        assert_eq!(Source::from_wire(-1), None);
    }
}

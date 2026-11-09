//! Ordered, offline runtime composition for the development host.

use openblade_domain::{
    automation::{self, Provenance},
    recovery::{
        EntryChange, FunctionKeyMode, JournalRevision, LaptopGetter, LaptopJournal, LaptopReadback,
        LaptopRecoveryPlan, LaptopSettlement, LaptopValue, PendingEntry, plan_laptop_recovery,
    },
};
use serde_json::Value;
use std::{io, sync::Arc};
use tokio::sync::{mpsc, watch};

const IDENTITY: &str = "synthetic-device";
const REQUESTED: LaptopValue = LaptopValue::FunctionKeyPrimary(FunctionKeyMode::FunctionKeys);
const BATTERY_SOURCE: i32 = 1;
const POWER_PROFILE_PROVENANCE: i32 = 1;

#[derive(Clone, Copy)]
enum Observation {
    Recovery(Option<FunctionKeyMode>),
    Battery,
}

type TimedObservation = (u64, Option<Observation>);
const SCRIPT: [&[TimedObservation]; 3] = [
    &[(1, Some(Observation::Recovery(None)))],
    &[
        (3, None), // Cancel the pending observation and invalidate older results.
        (
            2,
            Some(Observation::Recovery(Some(FunctionKeyMode::FunctionKeys))),
        ),
        (4, Some(Observation::Battery)),
    ],
    &[(
        5,
        Some(Observation::Recovery(Some(FunctionKeyMode::MultimediaKeys))),
    )],
];

#[derive(Debug, PartialEq, Eq)]
enum Effect {
    Ignored,
    ProfileSelected,
    ReadUnavailable(LaptopGetter),
    JournalSettled(LaptopGetter, EntryChange),
}

// The actor owns one synthetic record. These revision bytes are opaque tokens
// for this test store. Protected persistence supplies real content hashes.
struct MemoryJournal {
    revision: JournalRevision,
    entry: Option<PendingEntry>,
}

impl MemoryJournal {
    fn load(&self) -> LaptopJournal<'_> {
        LaptopJournal::Valid {
            identity: IDENTITY,
            revision: self.revision,
            entries: self.entry.as_slice(),
        }
    }

    fn settle(
        &mut self,
        settlement: LaptopSettlement,
    ) -> io::Result<(FunctionKeyMode, EntryChange)> {
        if settlement.expected_revision != self.revision {
            return Err(io::Error::other("stale synthetic journal settlement"));
        }
        let [Some(change), None, None, None] = settlement.changes else {
            return Err(io::Error::other(
                "invalid synthetic journal settlement shape",
            ));
        };
        let Some(LaptopValue::FunctionKeyPrimary(observed)) = change.observed else {
            return Err(io::Error::other("invalid synthetic function-key readback"));
        };
        if self.entry
            != Some(PendingEntry {
                requested: change.requested,
                reconciliation_required: true,
            })
        {
            return Err(io::Error::other("synthetic journal intent changed"));
        }
        self.revision[0] = self.revision[0]
            .checked_add(1)
            .ok_or_else(|| io::Error::other("synthetic journal revision exhausted"))?;
        self.entry = match change.change {
            EntryChange::Remove => None,
            EntryChange::KeepTombstone => Some(PendingEntry {
                requested: change.requested,
                reconciliation_required: false,
            }),
        };
        Ok((observed, change.change))
    }
}

struct Replay {
    last_tick: u64,
    current: Value,
    journal: MemoryJournal,
}

impl Replay {
    fn new(mut current: Value) -> Self {
        current["writesBlocked"] = true.into();
        current["message"] = "Synthetic startup recovery pending.".into();
        Self {
            last_tick: 0,
            current,
            journal: MemoryJournal {
                revision: [1; 32],
                entry: Some(PendingEntry {
                    requested: REQUESTED,
                    reconciliation_required: true,
                }),
            },
        }
    }

    fn step(&mut self, tick: u64, observation: Option<Observation>) -> io::Result<Effect> {
        if tick <= self.last_tick {
            return Ok(Effect::Ignored);
        }
        self.last_tick = tick;
        let Some(observation) = observation else {
            return Ok(Effect::Ignored);
        };

        let revision = self.next_revision()?;
        let effect = match observation {
            Observation::Recovery(readback) => self.recover(readback)?,
            Observation::Battery => self.select_battery_profile()?,
        };
        if effect != Effect::Ignored {
            self.current["revision"] = revision.into();
        }
        Ok(effect)
    }

    fn next_revision(&self) -> io::Result<i64> {
        self.current["revision"]
            .as_i64()
            .and_then(|revision| revision.checked_add(1))
            .ok_or_else(|| io::Error::other("invalid replay status revision"))
    }

    #[cfg(windows)]
    fn project_charge_limit(&mut self, percent: u8) {
        self.current["profiles"]["batteryPolicy"]["chargeLimitPercent"] = percent.into();
        self.current["desiredState"]["battery"]["configuredPolicy"]["chargeLimitPercent"] =
            percent.into();
    }

    fn select_battery_profile(&mut self) -> io::Result<Effect> {
        let profiles = &self.current["profiles"];
        let plugged_in = profiles["pluggedIn"].as_object();
        let battery = profiles["battery"].as_object();
        let value = &profiles["battery"]["performance"];
        let eligible = value
            .as_i64()
            .and_then(|preset| i32::try_from(preset).ok())
            .is_some_and(|preset| automation::performance_source_eligible(preset, BATTERY_SOURCE));
        let performance_override = &self.current["performanceOverride"];
        let selection = automation::performance(
            performance_override["lifetime"] == 0,
            performance_override["lifetime"] == 1,
            !self.current["activeApplicationProfileId"].is_null(),
        );
        let source = match selection.provenance {
            Provenance::PowerProfile => POWER_PROFILE_PROVENANCE,
            _ => {
                return Err(io::Error::other(
                    "profile override outside the battery replay",
                ));
            }
        };
        // This scenario projects a performance-only change. Fail closed if a
        // fixture edit introduces other power-dependent settings or overrides.
        let same_other_settings = plugged_in
            .zip(battery)
            .is_some_and(|(plugged_in, battery)| {
                plugged_in.len() == battery.len()
                    && plugged_in.iter().all(|(key, value)| match key.as_str() {
                        "performance" | "customPerformance" => battery.contains_key(key),
                        _ => battery.get(key) == Some(value),
                    })
            });
        if !eligible
            || !same_other_settings
            || !performance_override.is_null()
            || !self.current["desiredState"]["customPerformance"].is_null()
            || self.current["fullChargeOverride"] != false
            || [
                "fanOverride",
                "fanControlOverride",
                "lightingOverride",
                "batteryPolicyOverride",
                "batteryProtectionOverride",
            ]
            .iter()
            .any(|key| !self.current[*key].is_null())
        {
            return Err(io::Error::other("unsupported synthetic battery profile"));
        }

        self.current["desiredState"]["performance"] =
            serde_json::json!({ "value": value, "source": source });
        self.current["activePowerSource"] = BATTERY_SOURCE.into();
        Ok(Effect::ProfileSelected)
    }

    fn recover(&mut self, function_key_primary: Option<FunctionKeyMode>) -> io::Result<Effect> {
        let plan = match plan_laptop_recovery(self.journal.load(), Some(IDENTITY), None) {
            LaptopRecoveryPlan::NoPendingEntries => return Ok(Effect::Ignored),
            LaptopRecoveryPlan::ReadOnly(plan) => plan,
            _ => return Err(io::Error::other("unexpected synthetic recovery plan")),
        };
        let getter = LaptopGetter::FunctionKeyPrimary;
        // The script supplies the result of precisely this getter.
        if !plan.getters().eq([getter]) {
            return Err(io::Error::other("unexpected synthetic recovery getter"));
        }
        let Some(settlement) = plan.finish(LaptopReadback {
            function_key_primary,
            ..Default::default()
        }) else {
            self.current["message"] = "Synthetic recovery readback unavailable.".into();
            return Ok(Effect::ReadUnavailable(getter));
        };
        let (observed, change) = self.journal.settle(settlement)?;
        self.current["functionKeyPrimary"] = match observed {
            FunctionKeyMode::FunctionKeys => 0.into(),
            FunctionKeyMode::MultimediaKeys => 1.into(),
        };
        // The managed runtime keeps the presentation block until full observation.
        self.current["message"] = "Synthetic recovery readback observed.".into();
        Ok(Effect::JournalSettled(getter, change))
    }
}

pub fn start(
    baseline: Value,
    mut advance: mpsc::Receiver<()>,
) -> (
    watch::Receiver<Arc<Value>>,
    impl Future<Output = io::Result<()>>,
) {
    let mut replay = Replay::new(baseline);
    let (publish, status) = watch::channel(Arc::new(replay.current.clone()));
    let drive = async move {
        for batch in SCRIPT {
            if advance.recv().await.is_none() {
                return Ok(());
            }
            for &(tick, observation) in batch {
                if replay.step(tick, observation)? != Effect::Ignored {
                    publish.send_replace(Arc::new(replay.current.clone()));
                }
            }
        }
        // Keep draining the bounded input and retain the last published snapshot.
        while advance.recv().await.is_some() {}
        Ok(())
    };
    (status, drive)
}

#[cfg(windows)]
pub fn start_durable(
    baseline: Value,
    percent: u8,
    update: Option<(mpsc::Receiver<()>, impl FnOnce() -> io::Result<u8>)>,
) -> io::Result<(
    watch::Receiver<Arc<Value>>,
    impl Future<Output = io::Result<()>>,
)> {
    let mut replay = Replay::new(baseline);
    replay.step(1, Some(Observation::Recovery(None)))?;
    replay.project_charge_limit(percent);
    let (publish, status) = watch::channel(Arc::new(replay.current.clone()));
    let drive = async move {
        if let Some((mut advance, commit)) = update
            && advance.recv().await.is_some()
        {
            let revision = replay.next_revision()?;
            // The storage callback returns the independently read committed value.
            // Any failed or unresolved transaction exits without publishing it.
            replay.project_charge_limit(commit()?);
            replay.current["revision"] = revision.into();
            publish.send_replace(Arc::new(replay.current.clone()));
            while advance.recv().await.is_some() {}
        }
        std::future::pending().await
    };
    Ok((status, drive))
}

#[cfg(test)]
mod tests;

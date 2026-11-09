//! Ordered, offline runtime composition for the development host.

use openblade_domain::{
    automation::{self, Provenance},
    recovery::{
        EntryChange, FunctionKeyMode, JournalRevision, LaptopGetter, LaptopJournal, LaptopReadback,
        LaptopRecoveryPlan, LaptopValue, PendingEntry, plan_laptop_recovery,
    },
};
use serde_json::Value;
use std::{io, sync::Arc};
use tokio::sync::{mpsc, watch};

const IDENTITY: &str = "synthetic-device";
const REVISION: JournalRevision = [1; 32];
const REQUESTED: LaptopValue = LaptopValue::FunctionKeyPrimary(FunctionKeyMode::FunctionKeys);

#[derive(Clone, Copy)]
enum Observation {
    Recovery(Option<FunctionKeyMode>),
    Battery,
}

const SCRIPT: [Observation; 3] = [
    Observation::Recovery(None),
    Observation::Battery,
    Observation::Recovery(Some(FunctionKeyMode::MultimediaKeys)),
];

#[derive(Debug, PartialEq, Eq)]
enum Effect {
    Ignored,
    ProfileSelected,
    ReadUnavailable(LaptopGetter),
    JournalSettled(LaptopGetter, EntryChange),
}

struct Replay {
    last_tick: u64,
    current: Value,
    // One validated, in-memory journal entry from the restart scenario.
    entry: Option<PendingEntry>,
}

impl Replay {
    fn new(mut current: Value) -> Self {
        current["writesBlocked"] = true.into();
        current["message"] = "Synthetic startup recovery pending.".into();
        Self {
            last_tick: 0,
            current,
            entry: Some(PendingEntry {
                requested: REQUESTED,
                reconciliation_required: true,
            }),
        }
    }

    fn step(&mut self, tick: u64, observation: Option<Observation>) -> io::Result<Effect> {
        if tick <= self.last_tick {
            return Ok(Effect::Ignored);
        }
        self.last_tick = tick;

        let effect = match observation {
            Some(Observation::Recovery(readback)) => self.recover(readback),
            Some(Observation::Battery) => self.select_battery_profile()?,
            None => Effect::Ignored,
        };
        if effect != Effect::Ignored {
            let revision = self.current["revision"]
                .as_i64()
                .and_then(|revision| revision.checked_add(1))
                .ok_or_else(|| io::Error::other("invalid replay status revision"))?;
            self.current["revision"] = revision.into();
        }
        Ok(effect)
    }

    fn select_battery_profile(&mut self) -> io::Result<Effect> {
        let value = &self.current["profiles"]["battery"]["performance"];
        let eligible = value
            .as_i64()
            .and_then(|preset| i32::try_from(preset).ok())
            .is_some_and(|preset| automation::performance_source_eligible(preset, 1));
        if !eligible
            || automation::performance(false, false, false).provenance != Provenance::PowerProfile
        {
            return Err(io::Error::other("invalid synthetic battery profile"));
        }
        self.current["desiredState"]["performance"]["value"] = value.clone();
        self.current["activePowerSource"] = 1.into();
        Ok(Effect::ProfileSelected)
    }

    fn recover(&mut self, function_key_primary: Option<FunctionKeyMode>) -> Effect {
        let journal = LaptopJournal::Valid {
            identity: IDENTITY,
            revision: REVISION,
            entries: self.entry.as_slice(),
        };
        let LaptopRecoveryPlan::ReadOnly(plan) =
            plan_laptop_recovery(journal, Some(IDENTITY), None)
        else {
            return Effect::Ignored;
        };
        let getter = LaptopGetter::FunctionKeyPrimary;
        // The transport result is supplied by the script for the one admitted getter.
        assert_eq!(plan.getters().next(), Some(getter));
        let Some(settlement) = plan.finish(LaptopReadback {
            function_key_primary,
            ..Default::default()
        }) else {
            self.current["message"] = "Synthetic recovery readback unavailable.".into();
            return Effect::ReadUnavailable(getter);
        };
        let change = settlement.changes[0].expect("settlement for one journal entry");
        let Some(LaptopValue::FunctionKeyPrimary(observed)) = change.observed else {
            unreachable!("readback for the admitted function-key getter");
        };
        // The actor has exclusive ownership of the fake store. Apply the
        // settlement only to the journal revision read in this turn.
        assert_eq!(settlement.expected_revision, REVISION);
        self.entry = match change.change {
            EntryChange::Remove => None,
            EntryChange::KeepTombstone => Some(PendingEntry {
                requested: change.requested,
                reconciliation_required: false,
            }),
        };
        self.current["functionKeyPrimary"] = match observed {
            FunctionKeyMode::FunctionKeys => 0.into(),
            FunctionKeyMode::MultimediaKeys => 1.into(),
        };
        // The managed runtime keeps the presentation block until full observation.
        self.current["message"] = "Synthetic recovery readback observed.".into();
        Effect::JournalSettled(getter, change.change)
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
        for (index, observation) in SCRIPT.into_iter().enumerate() {
            if advance.recv().await.is_none() {
                break;
            }
            if replay.step(index as u64 + 1, Some(observation))? != Effect::Ignored {
                publish.send_replace(Arc::new(replay.current.clone()));
            }
        }
        // Keep draining the bounded input and retain the last published snapshot.
        while advance.recv().await.is_some() {}
        Ok(())
    };
    (status, drive)
}

#[cfg(test)]
mod tests;

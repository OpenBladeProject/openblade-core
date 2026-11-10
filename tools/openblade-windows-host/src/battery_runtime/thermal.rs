use super::{Result, Scenario, ScenarioState, blocked_configuration};
use crate::{fan_runtime as fan, performance_runtime as performance};
use openblade_domain::recovery::ScopedStatus;
use serde_json::{Value, json};
use std::io;
use tokio::sync::mpsc;

// Both thermal recipes and battery policy share the scenario guard and one
// status publication. The scripted transport behind each recipe is fixture-only.
pub(super) struct Thermal {
    performance: performance::Controller,
    fans: fan::Controller,
}

impl Thermal {
    pub(super) fn new(status: &mut Value) -> io::Result<Self> {
        if status["activePowerSource"] != 0
            || status["profiles"]["pluggedIn"]["performance"] != 1
            || status["profiles"]["pluggedIn"]["fans"] != 0
            || !status["performanceOverride"].is_null()
            || !status["fanOverride"].is_null()
            || !status["fanControlOverride"].is_null()
            || !status["activeApplicationProfileId"].is_null()
        {
            return Err(io::Error::other("unexpected composed thermal fixture"));
        }
        status["capabilities"]["performancePresets"] = json!([1, 2]);
        status["capabilities"]["fanModes"] = json!([0, 1]);
        status["capabilities"]["fanSpeedRange"] =
            json!({"minimumRpm": 4000, "maximumRpm": 4000, "stepRpm": 100});
        status["capabilities"]["supportsDirectManualFanTransitions"] = true.into();
        status["confirmedState"]["hyperBoostActive"] = false.into();
        Ok(Self {
            performance: performance::Controller::standard(),
            fans: fan::Controller::temporary(),
        })
    }

    pub(super) fn project(&self, status: &mut Value) {
        self.performance.project(status);
        self.fans.project(status);
    }
}

fn blocked(message: &'static str) -> Result {
    Result {
        status: ScopedStatus::Blocked,
        message,
    }
}

pub(super) fn await_readback() -> Result {
    blocked("A device write awaits read-only reconciliation.")
}

impl ScenarioState {
    pub(super) fn has_pending_write(&self) -> bool {
        self.controller.pending.is_some()
            || self.thermal.as_ref().is_some_and(|thermal| {
                thermal.performance.pending_write() || thermal.fans.pending_write()
            })
    }

    fn thermal(&mut self) -> io::Result<&mut Thermal> {
        self.thermal
            .as_mut()
            .ok_or_else(|| io::Error::other("missing composed thermal fixture"))
    }

    fn thermal_block(&self) -> Option<Result> {
        if self.configuration_blocked {
            return Some(blocked_configuration());
        }
        self.has_pending_write().then(await_readback)
    }
}

impl Scenario {
    pub(crate) async fn apply_performance(
        &self,
        value: Option<performance::Override>,
    ) -> io::Result<Result> {
        self.update(|state| {
            if let Some(blocked) = state.thermal_block() {
                return Ok(blocked);
            }
            if value.as_ref().is_some_and(|value| !value.is_temporary()) {
                return Ok(blocked(
                    "Only temporary preset overrides are admitted in this fixture.",
                ));
            }
            let thermal = state.thermal()?;
            if !thermal.fans.automatic_confirmed() {
                return Ok(blocked(
                    "The scoped preset recipe requires a confirmed Automatic fan pair.",
                ));
            }
            let outcome = thermal.performance.request(value)?;
            Ok(Result {
                status: outcome.status,
                message: outcome.message,
            })
        })
        .await
    }

    pub(crate) async fn apply_fans(
        &self,
        value: Option<fan::Override>,
        settings: Option<fan::Settings>,
    ) -> io::Result<Result> {
        self.update(|state| {
            if let Some(blocked) = state.thermal_block() {
                return Ok(blocked);
            }
            let thermal = state.thermal()?;
            // The ordinary fan recipe was reviewed only in the Performance preset.
            if !thermal.performance.performance_confirmed() {
                return Ok(blocked(
                    "The scoped fan recipe requires confirmed Performance.",
                ));
            }
            let outcome = thermal.fans.request(value, settings)?;
            Ok(Result {
                status: outcome.status,
                message: outcome.message,
            })
        })
        .await
    }

    pub(crate) async fn drive_thermal(&self, mut advance: mpsc::Receiver<()>) -> io::Result<()> {
        for step in [
            ThermalStep::CancelPreset,
            ThermalStep::Fan(fan::Step::Handback),
            ThermalStep::Fan(fan::Step::ArmFinalTargetLoss),
            ThermalStep::Fan(fan::Step::ObserveManualPair),
            ThermalStep::Fan(fan::Step::Handback),
        ] {
            if advance.recv().await.is_none() {
                return Ok(());
            }
            self.update(|state| step.run(state)).await?;
        }
        while advance.recv().await.is_some() {}
        Ok(())
    }
}

enum ThermalStep {
    CancelPreset,
    Fan(fan::Step),
}

impl ThermalStep {
    fn run(self, state: &mut ScenarioState) -> io::Result<Result> {
        // The observation path stays available even while both kinds of
        // uncertainty block setters and owned handback.
        if !matches!(self, Self::Fan(fan::Step::ObserveManualPair))
            && let Some(blocked) = state.thermal_block()
        {
            return Ok(blocked);
        }
        let thermal = state.thermal()?;
        match self {
            Self::CancelPreset => {
                let outcome = performance::Step::Cancel.run(&mut thermal.performance);
                Ok(Result {
                    status: outcome.status,
                    message: outcome.message,
                })
            }
            Self::Fan(step) => {
                if !matches!(step, fan::Step::ObserveManualPair)
                    && !thermal.performance.performance_confirmed()
                {
                    return Ok(blocked(
                        "The scoped fan recipe requires confirmed Performance.",
                    ));
                }
                let outcome = step.run(&mut thermal.fans)?;
                Ok(Result {
                    status: outcome.status,
                    message: outcome.message,
                })
            }
        }
    }
}

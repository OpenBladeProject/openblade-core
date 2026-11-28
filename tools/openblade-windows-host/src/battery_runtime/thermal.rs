use super::{Result, Scenario, ScenarioState, blocked_configuration};
use crate::{fan_runtime as fan, performance_runtime as performance};
use openblade_domain::recovery::ScopedStatus;
use serde_json::{Value, json};
use std::io;
use tokio::sync::mpsc;

// Both thermal recipes and battery policy share the scenario guard and one
// status publication. The scripted transport behind each recipe is fixture-only.
pub(super) struct Thermal {
    pub(super) performance: performance::Controller,
    pub(super) fans: fan::Controller,
    power_profile: bool,
    pub(super) power_unresolved: bool,
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
            power_profile: false,
            power_unresolved: false,
        })
    }

    pub(super) fn for_power_profile(
        status: &mut Value,
        prior_performance: bool,
    ) -> io::Result<Self> {
        let mut thermal = Self::new(status)?;
        thermal.power_profile = true;
        // A prior confirmed standard preset provides a differing getter
        // baseline. The durable plugged-in destination remains Balanced.
        if prior_performance {
            thermal.performance.profile_replay_baseline();
        }
        Ok(thermal)
    }

    pub(super) fn pending_write(&self) -> bool {
        self.performance.pending_write() || self.fans.pending_write()
    }

    pub(super) fn project(&self, status: &mut Value) {
        if self.power_profile {
            self.performance.project_readback(status);
            if !status["performanceOverride"].is_null() {
                self.performance.project_desired(status);
            }
            if self.power_unresolved && status["activePowerSource"] == 1 {
                status["activePowerSource"] = 3.into();
            }
            if self.power_unresolved || status["activePowerSource"] != 0 {
                status["writesBlocked"] = true.into();
            }
        } else {
            self.performance.project(status);
        }
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
    pub(super) fn thermal(&mut self) -> io::Result<&mut Thermal> {
        self.thermal
            .as_mut()
            .ok_or_else(|| io::Error::other("missing composed thermal fixture"))
    }

    pub(super) fn unresolved_power(&self) -> Option<Result> {
        self.thermal
            .as_ref()
            .is_some_and(|thermal| thermal.power_unresolved)
            .then(|| blocked("Fresh external adapter classification is required."))
    }

    pub(super) fn direct_thermal_block(&self) -> Option<Result> {
        self.thermal_block()
            .or_else(|| self.unresolved_power())
            .or_else(|| {
                (self
                    .thermal
                    .as_ref()
                    .is_some_and(|thermal| thermal.power_profile)
                    && self.controller.power != super::Power::PluggedIn)
                    .then(|| blocked("The scoped thermal recipe requires full adapter power."))
            })
    }

    pub(super) fn thermal_block(&self) -> Option<Result> {
        if let Some(blocked) = self.session_block() {
            return Some(blocked);
        }
        if self.configuration_blocked {
            return Some(blocked_configuration());
        }
        (self.controller.pending.is_some()
            || self.thermal.as_ref().is_some_and(Thermal::pending_write))
        .then(await_readback)
    }
}

impl Scenario {
    pub(crate) async fn apply_performance(
        &self,
        value: Option<performance::Override>,
    ) -> io::Result<Result> {
        self.update(|state| {
            if let Some(blocked) = state.direct_thermal_block() {
                return Ok(blocked);
            }
            if value.as_ref().is_some_and(|value| !value.is_temporary()) {
                return Ok(blocked(
                    "Only temporary preset overrides are admitted in this fixture.",
                ));
            }
            let selected = state
                .application
                .as_ref()
                .map(|_| state.profile_performance().0);
            let thermal = state.thermal()?;
            if !thermal.fans.automatic_confirmed() {
                return Ok(blocked(
                    "The scoped preset recipe requires a confirmed Automatic fan pair.",
                ));
            }
            let outcome = if let Some(selected) = selected {
                if !super::power_profile::fresh_full_adapter(&mut thermal.fans)
                    || !thermal.fans.fresh_automatic()
                {
                    return Ok(blocked(
                        "The scoped preset recipe requires fresh full power and an Automatic fan pair.",
                    ));
                }
                thermal.performance.request_selected(value, selected, || {
                    super::power_profile::fresh_full_adapter(&mut thermal.fans)
                })?
            } else {
                thermal.performance.request(value)?
            };
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
            if let Some(blocked) = state.direct_thermal_block() {
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
            ThermalStep::ObservePreset,
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
        // Keep readback available after the scripted trajectory has finished.
        while advance.recv().await.is_some() {
            self.update(|state| {
                state.controller.observe();
                let thermal = state.thermal()?;
                if thermal.performance.pending_write() {
                    return ThermalStep::ObservePreset.run(state);
                }
                ThermalStep::Fan(fan::Step::ObserveManualPair).run(state)
            })
            .await?;
        }
        Ok(())
    }
}

enum ThermalStep {
    CancelPreset,
    ObservePreset,
    Fan(fan::Step),
}

impl ThermalStep {
    fn run(self, state: &mut ScenarioState) -> io::Result<Result> {
        // The observation path stays available even while both kinds of
        // uncertainty block setters and owned handback.
        if !matches!(
            self,
            Self::ObservePreset | Self::Fan(fan::Step::ObserveManualPair)
        ) && let Some(blocked) = state.thermal_block()
        {
            return Ok(blocked);
        }
        let thermal = state.thermal()?;
        match self {
            Self::CancelPreset | Self::ObservePreset => {
                let step = if matches!(self, Self::CancelPreset) {
                    performance::Step::Cancel
                } else {
                    performance::Step::Recover
                };
                let outcome = step.run(&mut thermal.performance);
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

use std::{
    io,
    sync::{Arc, Mutex},
};

use openblade_domain::recovery::ScopedStatus;
use serde::{Deserialize, Serialize};
use serde_json::{Value, json};
use tokio::sync::{mpsc, watch};

mod device;

use device::{Device, Fault, RecipeResult, Topology};

const PERFORMANCE: i32 = 2;
const AUTOMATIC: i32 = 0;
const FIXED: i32 = 1;
const PERSISTENT: i32 = 1;
const FANS: i32 = 1;

#[derive(Deserialize)]
#[serde(deny_unknown_fields, rename_all = "camelCase")]
pub(crate) struct Override {
    value: i32,
    lifetime: i32,
}

#[derive(Clone, Deserialize, Serialize)]
#[serde(deny_unknown_fields, rename_all = "camelCase")]
pub(crate) struct Settings {
    mode: i32,
    fixed_speed_rpm: i32,
    manual_curve: Curve,
}

#[derive(Clone, Deserialize, Serialize)]
#[serde(deny_unknown_fields, rename_all = "camelCase")]
struct Curve {
    cpu: Vec<Point>,
    gpu: Vec<Point>,
    use_higher_temperature_for_both: bool,
}

#[derive(Clone, Deserialize, Serialize)]
#[serde(deny_unknown_fields, rename_all = "camelCase")]
struct Point {
    temperature_celsius: i32,
    speed_rpm: i32,
}

impl Curve {
    fn valid(&self) -> bool {
        [&self.cpu, &self.gpu].into_iter().all(|points| {
            (2..=10).contains(&points.len())
                && points.iter().all(|point| {
                    (20..=100).contains(&point.temperature_celsius)
                        && (2000..=5400).contains(&point.speed_rpm)
                        && point.speed_rpm % 100 == 0
                })
                && points.windows(2).all(|pair| {
                    pair[0].temperature_celsius < pair[1].temperature_celsius
                        && pair[0].speed_rpm <= pair[1].speed_rpm
                })
        })
    }
}

struct Controller {
    device: Device,
    // The private replay's store is process-local. Handback never changes it.
    saved: Option<Settings>,
    confirmed: Option<Topology>,
    owned: bool,
    pending: bool,
    external: bool,
}

impl Controller {
    fn new(mut device: Device) -> Self {
        let confirmed = device.observe();
        Self {
            device,
            saved: None,
            confirmed,
            owned: false,
            pending: false,
            external: false,
        }
    }

    fn set_fixed(&mut self, settings: Settings) -> io::Result<Outcome> {
        if let Some(outcome) = self.write_block() {
            return Ok(outcome);
        }
        self.saved = Some(settings);
        self.transition(Topology::Manual)
    }

    fn handback(&mut self) -> io::Result<Outcome> {
        if let Some(outcome) = self.write_block() {
            return Ok(outcome);
        }
        if !self.owned || self.confirmed != Some(Topology::Manual) {
            return Ok(Outcome::new(
                ScopedStatus::Unchanged,
                "No confirmed OpenBlade-owned manual fans to hand back.",
            ));
        }
        self.transition(Topology::Automatic)
    }

    fn write_block(&mut self) -> Option<Outcome> {
        if !self.device.admits_writes() {
            // The fake cannot prove continuous exclusive authority across
            // this boundary. Only another confirmed write can rearm ownership.
            self.owned = false;
            return Some(Outcome::blocked("Fan write admission is unavailable."));
        }
        self.pending
            .then(|| Outcome::blocked("Fan writes await read-only reconciliation."))
    }

    fn transition(&mut self, target: Topology) -> io::Result<Outcome> {
        let Some(baseline) = self.device.observe() else {
            return Ok(Outcome::failed(
                "The Performance fan pair could not be read.",
            ));
        };
        self.record(baseline);
        if target == Topology::Automatic && baseline == Topology::Automatic {
            return Ok(Outcome::new(
                ScopedStatus::Unchanged,
                "Fans are already Automatic.",
            ));
        }
        let outcome = match self.device.apply(baseline, target) {
            RecipeResult::Unsubmitted => {
                Outcome::failed("The fan recipe was blocked before submission.")
            }
            #[cfg(test)]
            RecipeResult::Canceled => return Err(io::ErrorKind::Interrupted.into()),
            result => {
                let readback = self.device.observe();
                if let Some(observed) = readback {
                    self.record(observed);
                }
                if result == RecipeResult::Acknowledged && readback == Some(target) {
                    self.owned = target == Topology::Manual && self.device.admits_writes();
                    self.external = false;
                    Outcome::new(
                        ScopedStatus::Applied,
                        "Fan topology was confirmed by readback.",
                    )
                } else {
                    self.owned = false;
                    self.pending = result == RecipeResult::Uncertain || readback.is_none();
                    if result == RecipeResult::Acknowledged && readback.is_some() {
                        self.external = true;
                        Outcome::failed("Fan readback differed from the requested topology.")
                    } else if result == RecipeResult::Acknowledged && target == Topology::Manual {
                        Outcome::new(
                            ScopedStatus::Applied,
                            "Fan setter completed. Readback is unresolved.",
                        )
                    } else {
                        Outcome::failed("Fan submission or readback is unresolved.")
                    }
                }
            }
        };
        Ok(outcome)
    }

    fn record(&mut self, observed: Topology) {
        self.confirmed = Some(observed);
        if observed == Topology::Automatic {
            self.owned = false;
        }
    }

    fn observe(&mut self) {
        if !self.device.admits_writes() {
            self.owned = false;
        }
        if let Some(observed) = self.device.observe() {
            self.record(observed);
            self.pending = false;
        }
    }
}

pub(crate) struct Outcome {
    pub status: ScopedStatus,
    pub message: &'static str,
}

impl Outcome {
    fn new(status: ScopedStatus, message: &'static str) -> Self {
        Self { status, message }
    }

    fn blocked(message: &'static str) -> Self {
        Self::new(ScopedStatus::Blocked, message)
    }

    fn failed(message: &'static str) -> Self {
        Self::new(ScopedStatus::Failed, message)
    }
}

struct State {
    controller: Controller,
    status: Value,
}

pub(crate) struct Scenario {
    state: Mutex<State>,
    publish: watch::Sender<Arc<Value>>,
}

impl Scenario {
    pub(crate) fn new(mut status: Value) -> io::Result<Self> {
        if status["activePowerSource"] != 0
            || status["profiles"]["pluggedIn"]["fans"] != AUTOMATIC
            || status["desiredState"]["fans"] != json!({"value": AUTOMATIC, "source": 1})
            || !status["fanOverride"].is_null()
            || !status["fanControlOverride"].is_null()
            || !status["performanceOverride"].is_null()
            || !status["activeApplicationProfileId"].is_null()
        {
            return Err(io::Error::other("unexpected fan replay fixture"));
        }
        status["deviceVerified"] = true.into();
        status["potentialControllers"] = json!([]);
        status["profiles"]["pluggedIn"]["performance"] = PERFORMANCE.into();
        status["desiredState"]["performance"] = json!({"value": PERFORMANCE, "source": 1});
        status["confirmedState"]["performance"] = PERFORMANCE.into();
        status["confirmedState"]["hyperBoostActive"] = false.into();
        status["capabilities"]["fanModes"] = json!([AUTOMATIC, FIXED]);
        status["capabilities"]["performancePresets"] = json!([PERFORMANCE]);
        status["capabilities"]["fanSpeedRange"] =
            json!({"minimumRpm": 4000, "maximumRpm": 4000, "stepRpm": 100});
        status["capabilities"]["supportsDirectManualFanTransitions"] = true.into();
        let mut state = State {
            controller: Controller::new(Device::new()),
            status,
        };
        state.project("Synthetic RZ09-0581 fan replay ready.");
        let (publish, _) = watch::channel(Arc::new(state.status.clone()));
        Ok(Self {
            state: Mutex::new(state),
            publish,
        })
    }

    pub(crate) fn subscribe(&self) -> watch::Receiver<Arc<Value>> {
        self.publish.subscribe()
    }

    pub(crate) fn apply(
        &self,
        value: Option<Override>,
        settings: Option<Settings>,
    ) -> io::Result<Outcome> {
        self.update(|controller| match (value, settings) {
            (
                Some(Override {
                    value: FIXED,
                    lifetime: PERSISTENT,
                }),
                Some(settings),
            ) if settings.mode == FIXED
                && settings.fixed_speed_rpm == 4000
                && settings.manual_curve.valid() =>
            {
                controller.set_fixed(settings)
            }
            _ => Ok(Outcome::blocked(
                "Only the persistent 4,000 RPM Fixed request is admitted in this replay.",
            )),
        })
    }

    pub(crate) async fn drive(&self, mut advance: mpsc::Receiver<()>) -> io::Result<()> {
        for step in [
            Step::Handback,
            Step::ArmSplit,
            Step::ObserveSplit,
            Step::ObserveRestoredPair,
        ] {
            if advance.recv().await.is_none() {
                return Ok(());
            }
            self.update(|controller| step.run(controller))?;
        }
        while advance.recv().await.is_some() {}
        Ok(())
    }

    fn update(
        &self,
        run: impl FnOnce(&mut Controller) -> io::Result<Outcome>,
    ) -> io::Result<Outcome> {
        let mut state = self
            .state
            .lock()
            .map_err(|_| io::Error::other("fan scenario state poisoned"))?;
        let revision = state.status["revision"]
            .as_i64()
            .and_then(|revision| revision.checked_add(1))
            .ok_or_else(|| io::Error::other("invalid fan status revision"))?;
        let outcome = run(&mut state.controller)?;
        let previous = state.status.clone();
        state.project(outcome.message);
        if state.status != previous {
            state.status["revision"] = revision.into();
            self.publish.send_replace(Arc::new(state.status.clone()));
        }
        Ok(outcome)
    }
}

impl State {
    fn project(&mut self, message: &'static str) {
        let controller = &self.controller;
        let saved = controller.saved.as_ref();
        let (mode, source) = if saved.is_some() {
            (FIXED, 4)
        } else {
            (AUTOMATIC, 1)
        };
        self.status["desiredState"]["fans"] = json!({"value": mode, "source": source});
        self.status["desiredState"]["fanControl"] = json!(saved);
        self.status["fanOverride"] =
            json!(saved.map(|_| json!({"value": FIXED, "lifetime": PERSISTENT})));
        self.status["fanControlOverride"] = json!(saved);
        self.status["confirmedState"]["fans"] = json!(controller.confirmed.map(Topology::wire));
        self.status["writesBlocked"] =
            (controller.pending || !controller.device.admits_writes()).into();
        self.status["unconfirmedWrites"] = if controller.pending {
            json!([FANS])
        } else {
            json!([])
        };
        self.status["externalChanges"] = if controller.external {
            json!([FANS])
        } else {
            json!([])
        };
        self.status["message"] = message.into();
    }
}

enum Step {
    Handback,
    ArmSplit,
    ObserveSplit,
    ObserveRestoredPair,
}

impl Step {
    fn run(self, controller: &mut Controller) -> io::Result<Outcome> {
        let message = match self {
            Self::Handback => return controller.handback(),
            Self::ArmSplit => {
                controller.device.fault = Fault::LoseFirstModeResponse;
                "Synthetic fan response loss armed."
            }
            Self::ObserveSplit => {
                controller.device.fault = Fault::None;
                controller.observe();
                "Synthetic split fan pair observed without writes."
            }
            Self::ObserveRestoredPair => {
                controller.device.restore_scripted_pair(Topology::Automatic);
                controller.observe();
                "Synthetic restored fan pair observed without writes."
            }
        };
        Ok(Outcome::new(ScopedStatus::Unchanged, message))
    }
}

#[cfg(test)]
mod tests;

//! Application observation and selection share the composed runtime lock.
//! Only a fresh scripted step or a typed manual control can request an effect.

use super::{Power, Result, Scenario, ScenarioState, power_profile};
use crate::{
    application_runtime::{INACTIVE, Lease, Report, project_behavior, rules},
    performance_runtime::Step as PerformanceStep,
};
use openblade_domain::{automation::performance_source_eligible, recovery::ScopedStatus};
use serde_json::{Value, json};
use std::{future::pending, io};
use tokio::{
    sync::mpsc,
    time::{Instant, sleep_until},
};

const EXPIRED: &str = "The application profile lease expired; the current power profile is active.";

pub(super) struct Context {
    active: bool,
    lease: Lease,
}

impl Context {
    pub(super) fn new() -> Self {
        Self {
            active: true,
            lease: Lease::default(),
        }
    }
}

fn outcome(status: ScopedStatus, message: &'static str) -> Result {
    Result { status, message }
}

impl ScenarioState {
    pub(super) fn session_block(&self) -> Option<Result> {
        self.application
            .as_ref()
            .filter(|context| !context.active)
            .map(|_| outcome(ScopedStatus::Blocked, INACTIVE))
    }

    pub(super) fn expire_application(&mut self) {
        if let Some(context) = &mut self.application
            && let Some(at) = context.lease.deadline()
        {
            context.lease.expire(at, Instant::now());
        }
    }

    fn application_rule(&self) -> Option<&Value> {
        self.application
            .as_ref()?
            .lease
            .rule(rules(&self.current["profiles"]))
    }

    fn power_key(&self) -> &'static str {
        match self.controller.power {
            Power::PluggedIn => "pluggedIn",
            Power::Battery => "battery",
            Power::UsbC => "usbC",
        }
    }

    fn application_override(&self) -> &Value {
        self.application_rule()
            .map_or(&Value::Null, |rule| &rule["overrides"][self.power_key()])
    }

    pub(super) fn profile_performance(&self) -> (i32, i32) {
        let power = self.controller.power.index() as i32;
        let overlay = self.application_override()["performance"]
            .as_i64()
            .and_then(|preset| i32::try_from(preset).ok())
            .filter(|preset| performance_source_eligible(*preset, power));
        overlay.map_or((self.controller.profile().performance, 1), |preset| {
            (preset, 2)
        })
    }

    pub(super) fn project_application(&mut self) {
        let Some(context) = &self.application else {
            return;
        };
        let selected = self
            .thermal
            .as_ref()
            .and_then(|thermal| {
                thermal
                    .performance
                    .manual_selection(self.controller.power.index() as i32)
            })
            .unwrap_or_else(|| self.profile_performance());
        let overlay = self.application_override();
        let behavior = project_behavior(
            self.current["profiles"][self.power_key()]["laptopBehavior"].clone(),
            overlay,
        );
        self.current["activeApplicationProfileId"] = self
            .application_rule()
            .map_or(Value::Null, |rule| rule["id"].clone());
        self.current["effectiveLaptopBehavior"] = behavior;
        self.current["desiredState"]["performance"] =
            json!({"value": selected.0, "source": selected.1});
        if !context.active {
            self.current["controlAccess"] = 1.into();
            self.current["writesBlocked"] = true.into();
        } else {
            self.current["controlAccess"] = 0.into();
        }
    }

    fn reconcile_application(&mut self, lose_response: bool) -> io::Result<Result> {
        if let Some(blocked) = self.direct_thermal_block() {
            return Ok(blocked);
        }
        let value = self.profile_performance().0;
        let thermal = self.thermal()?;
        if thermal.performance.manual_selection(0).is_some() {
            return Ok(outcome(
                ScopedStatus::Unchanged,
                "Manual performance override remains selected.",
            ));
        }
        if !power_profile::fresh_full_adapter(&mut thermal.fans) || !thermal.fans.fresh_automatic()
        {
            return Ok(outcome(
                ScopedStatus::Blocked,
                "The scoped preset recipe requires fresh full power and an Automatic fan pair.",
            ));
        }
        if lose_response {
            PerformanceStep::LoseResponse.run(&mut thermal.performance);
        }
        let effect = thermal.performance.apply_power_profile(value, || {
            power_profile::fresh_full_adapter(&mut thermal.fans)
        })?;
        Ok(outcome(effect.status, effect.message))
    }
}

impl Scenario {
    pub(crate) fn accepts_application(&self) -> bool {
        self.application_changed.is_some()
    }

    pub(crate) async fn report_application(&self, report: &Report) -> io::Result<Result> {
        let mut state = self.state.lock().await;
        if let Some(blocked) = state.session_block() {
            return Ok(blocked);
        }
        let ScenarioState {
            application,
            current,
            ..
        } = &mut *state;
        let context = application.as_mut().expect("guarded application runtime");
        let effect = context
            .lease
            .report(rules(&current["profiles"]), report, Instant::now());
        let message = effect.message();
        if !effect.updates_status() {
            return Ok(outcome(ScopedStatus::Unchanged, message));
        }
        self.application_changed
            .as_ref()
            .expect("guarded application runtime")
            .notify_one();
        self.update_locked(&mut state, |_| {
            Ok(outcome(ScopedStatus::Unchanged, message))
        })
    }

    async fn watch_application_deadline(&self) -> io::Result<()> {
        let changed = self
            .application_changed
            .as_ref()
            .expect("guarded application runtime");
        loop {
            let deadline = self
                .state
                .lock()
                .await
                .application
                .as_ref()
                .and_then(|c| c.lease.deadline());
            tokio::select! {
                _ = changed.notified() => {}
                _ = async {
                    match deadline {
                        Some(at) => sleep_until(at).await,
                        None => pending().await,
                    }
                } => {
                    let mut state = self.state.lock().await;
                    if state.application.as_ref().and_then(|c| c.lease.deadline())
                        .is_some_and(|at| Some(at) == deadline && at <= Instant::now())
                    {
                        self.update_locked(&mut state, |_| Ok(outcome(ScopedStatus::Unchanged, EXPIRED)))?;
                    }
                }
            }
        }
    }

    pub(super) async fn drive_application_steps(
        &self,
        advance: &mut mpsc::Receiver<()>,
    ) -> io::Result<()> {
        tokio::select! {
            result = self.watch_application_deadline() => result,
            result = self.application_steps(advance) => result,
        }
    }

    async fn application_steps(&self, advance: &mut mpsc::Receiver<()>) -> io::Result<()> {
        enum Step {
            Reconcile { lose_response: bool },
            Battery,
            Session(bool),
            PlanFull,
            ApplyFull,
            Readback,
        }

        use Step::*;
        let steps = [
            Reconcile {
                lose_response: false,
            },
            Battery,
            Session(false),
            Session(true),
            PlanFull,
            ApplyFull,
            Reconcile {
                lose_response: true,
            },
            Reconcile {
                lose_response: false,
            },
            Readback,
        ];
        let mut lifecycle = power_profile::Lifecycle::default();
        for step in steps {
            if advance.recv().await.is_none() {
                return Ok(());
            }
            self.update(|state| match step {
                Reconcile { lose_response } => state.reconcile_application(lose_response),
                Battery => lifecycle.notify(state, 0, &[]),
                Session(active) => {
                    state
                        .application
                        .as_mut()
                        .expect("application script")
                        .active = active;
                    Ok(outcome(
                        ScopedStatus::Unchanged,
                        if active {
                            "Scripted session controls Windows again."
                        } else {
                            "Scripted session no longer controls Windows."
                        },
                    ))
                }
                PlanFull => lifecycle.notify(state, 1, power_profile::CONFIRMATIONS),
                ApplyFull => {
                    lifecycle.apply(state, lifecycle.planned_full, power_profile::FULL, false)
                }
                Readback => {
                    let read = PerformanceStep::Recover.run(&mut state.thermal()?.performance);
                    Ok(outcome(read.status, read.message))
                }
            })
            .await?;
        }
        while advance.recv().await.is_some() {}
        Ok(())
    }
}

//! Store-first ownership of a standard preset transition in the private replay.

use std::{
    io,
    sync::{Arc, Mutex},
};

use openblade_domain::{
    automation::{self, ValueOrigin},
    recovery::ScopedStatus,
};
use serde::Deserialize;
use serde_json::{Value, json};
use tokio::sync::{mpsc, watch};

pub(crate) mod device;

use device::{Device, Fault, Preset, RecipeResult};

const PERFORMANCE: i32 = 0;
const FULL_ADAPTER: i32 = 0;
const AUTOMATIC: i32 = 0;
const PENDING: &str =
    "Performance was not written because OpenBlade must confirm the current laptop state first.";

#[derive(Deserialize)]
#[serde(deny_unknown_fields, rename_all = "camelCase")]
pub(crate) struct Override {
    value: i32,
    lifetime: i32,
    custom_performance: Option<Value>,
    gpu_clock_tuning: Option<Value>,
}

#[derive(Clone, Copy)]
enum Intent {
    Clear,
    Temporary(Preset),
    Persistent(Preset),
}

#[cfg(windows)]
impl Override {
    pub(crate) fn is_temporary(&self) -> bool {
        self.lifetime == 0
    }
}

impl TryFrom<Option<Override>> for Intent {
    type Error = ();

    fn try_from(value: Option<Override>) -> Result<Self, ()> {
        let Some(value) = value else {
            return Ok(Self::Clear);
        };
        if value.custom_performance.is_some() || value.gpu_clock_tuning.is_some() {
            return Err(());
        }
        let preset = Preset::try_from(value.value)?;
        match value.lifetime {
            0 => Ok(Self::Temporary(preset)),
            1 => Ok(Self::Persistent(preset)),
            _ => Err(()),
        }
    }
}

// This test-owned store outlives a controller reconstruction. Scoped requests
// only replace the persistent override slot, never the saved power profile.
struct Store {
    profile: Preset,
    persistent: Option<Preset>,
    #[cfg(test)]
    fail_save: bool,
    #[cfg(test)]
    saves: usize,
}

impl Store {
    fn new(profile: Preset) -> Self {
        Self {
            profile,
            persistent: None,
            #[cfg(test)]
            fail_save: false,
            #[cfg(test)]
            saves: 0,
        }
    }

    fn save(&mut self, value: Option<Preset>) -> io::Result<()> {
        if self.persistent != value {
            #[cfg(test)]
            {
                if self.fail_save {
                    return Err(io::Error::other("synthetic override store failed"));
                }
                self.saves += 1;
            }
            self.persistent = value;
        }
        Ok(())
    }
}

pub(crate) struct Controller {
    store: Store,
    device: Device,
    temporary: Option<Preset>,
    confirmed: Option<Preset>,
    pending: Option<Preset>,
    external: bool,
}

impl Controller {
    pub(crate) fn standard() -> Self {
        Self::new(Store::new(Preset::Balanced), Device::new())
    }

    #[cfg(windows)]
    pub(crate) fn performance_confirmed(&self) -> bool {
        self.confirmed == Some(Preset::Performance) && !self.pending_write()
    }

    #[cfg(windows)]
    pub(crate) fn pending_write(&self) -> bool {
        self.pending.is_some()
    }

    fn new(store: Store, device: Device) -> Self {
        let mut controller = Self {
            store,
            device,
            temporary: None,
            confirmed: None,
            pending: None,
            external: false,
        };
        controller.observe();
        controller
    }

    fn desired(&self) -> (Preset, i32) {
        let selection = automation::performance(
            self.temporary.is_some(),
            self.store.persistent.is_some(),
            false,
        );
        match selection.origin {
            ValueOrigin::Temporary => (self.temporary.unwrap_or(self.store.profile), 3),
            ValueOrigin::Persistent => (self.store.persistent.unwrap_or(self.store.profile), 4),
            _ => (self.store.profile, 1),
        }
    }

    pub(crate) fn request(&mut self, value: Option<Override>) -> io::Result<Outcome> {
        match Intent::try_from(value) {
            Ok(intent) => self.apply(intent),
            Err(()) => Ok(Outcome::blocked(
                "Only standard Balanced and Performance overrides are admitted in this replay.",
            )),
        }
    }

    fn apply(&mut self, intent: Intent) -> io::Result<Outcome> {
        if self.pending.is_some() {
            return Ok(Outcome::blocked(PENDING));
        }
        if !self.device.admits_intent() {
            return Ok(Outcome::blocked(
                "The performance request lost session or power admission.",
            ));
        }
        let (temporary, persistent) = match intent {
            Intent::Temporary(value) => (Some(value), None),
            Intent::Persistent(value) => (None, Some(value)),
            Intent::Clear => (None, None),
        };
        let value = temporary.or(persistent).unwrap_or(self.store.profile);
        if !automation::performance_source_eligible(value as i32, FULL_ADAPTER) {
            return Ok(Outcome::blocked(
                "The preset is unavailable on this power source.",
            ));
        }
        if self.store.save(persistent).is_err() {
            return Ok(Outcome::failed(
                "Performance override intent could not be stored.",
            ));
        }
        self.temporary = temporary;

        let outcome = match self.device.apply(value) {
            RecipeResult::Unchanged => {
                self.confirm(value);
                Outcome::new(ScopedStatus::Unchanged, "Performance is already confirmed.")
            }
            RecipeResult::MatchedAtPort => {
                // The port skipped both setters. Observe independently without
                // attributing a change to this operation or creating a pending write.
                let observed = self.device.observe();
                if let Some(observed) = observed {
                    self.record_observation(observed);
                }
                if observed == Some(value) {
                    Outcome::new(ScopedStatus::Unchanged, "Performance is already confirmed.")
                } else {
                    Outcome::failed("Performance could not be confirmed without a write.")
                }
            }
            RecipeResult::Blocked => {
                Outcome::blocked("The laptop could not be admitted for this control operation.")
            }
            RecipeResult::BaselineUnavailable => {
                Outcome::failed("Performance could not be read before the write.")
            }
            RecipeResult::Canceled => return Err(io::ErrorKind::Interrupted.into()),
            RecipeResult::KnownNotApplied => {
                Outcome::failed("The thermal command was definitively not applied.")
            }
            RecipeResult::Acknowledged(baseline) => self.finish(value, baseline, false),
            RecipeResult::Indeterminate(baseline) => self.finish(value, baseline, true),
        };
        Ok(outcome)
    }

    fn finish(&mut self, target: Preset, baseline: Preset, indeterminate: bool) -> Outcome {
        // Managed reconciliation after possible submission uses a fresh getter.
        // A successful command echo alone never confirms the requested preset.
        let mut readback = self.device.observe();
        if readback.is_none() && !indeterminate {
            // The acknowledged path allows one additional read-only attempt
            // when the first confirmation read fails.
            readback = self.device.observe();
        }
        match readback {
            Some(observed) if observed == target => {
                self.confirm(observed);
                Outcome::new(
                    ScopedStatus::Applied,
                    "Performance applied and confirmed by readback.",
                )
            }
            Some(observed) => {
                let drift = self.confirmed.is_some_and(|previous| previous != observed);
                self.confirmed = Some(observed);
                self.external |= drift || !indeterminate || observed != baseline;
                Outcome::failed("Performance did not match the requested state after readback.")
            }
            None => {
                self.pending = Some(target);
                Outcome::failed("Performance write is unconfirmed.")
            }
        }
    }

    fn observe(&mut self) {
        let Some(observed) = self.device.observe() else {
            return;
        };
        self.record_observation(observed);
    }

    fn record_observation(&mut self, observed: Preset) {
        self.external = match self.pending.take() {
            Some(target) => observed != target,
            None => self.external || self.confirmed.is_some_and(|previous| previous != observed),
        };
        self.confirmed = Some(observed);
    }

    fn confirm(&mut self, observed: Preset) {
        self.confirmed = Some(observed);
        self.external = false;
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

    fn failed(message: &'static str) -> Self {
        Self::new(ScopedStatus::Failed, message)
    }

    fn blocked(message: &'static str) -> Self {
        Self::new(ScopedStatus::Blocked, message)
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
        if status["profiles"]["pluggedIn"]["performance"] != Preset::Balanced as i32
            || status["profiles"]["pluggedIn"]["fans"] != AUTOMATIC
            || status["activePowerSource"] != FULL_ADAPTER
            || !status["performanceOverride"].is_null()
            || !status["activeApplicationProfileId"].is_null()
            || !status["fanOverride"].is_null()
            || !status["fanControlOverride"].is_null()
            || status["desiredState"]["performance"]
                != json!({"value": Preset::Balanced as i32, "source": 1})
            || status["desiredState"]["fans"]["value"] != AUTOMATIC
        {
            return Err(io::Error::other("unexpected performance replay fixture"));
        }
        let controller = Controller::standard();
        status["deviceVerified"] = true.into();
        status["potentialControllers"] = json!([]);
        status["capabilities"]["performancePresets"] =
            json!([Preset::Balanced as i32, Preset::Performance as i32]);
        status["capabilities"]["fanModes"] = json!([AUTOMATIC]);
        status["confirmedState"]["fans"] = AUTOMATIC.into();
        status["confirmedState"]["hyperBoostActive"] = false.into();
        let mut state = State { controller, status };
        state.project("Synthetic RZ09-0581 performance replay ready.");
        let (publish, _) = watch::channel(Arc::new(state.status.clone()));
        Ok(Self {
            state: Mutex::new(state),
            publish,
        })
    }

    pub(crate) fn subscribe(&self) -> watch::Receiver<Arc<Value>> {
        self.publish.subscribe()
    }

    pub(crate) fn apply(&self, value: Option<Override>) -> io::Result<Outcome> {
        self.update(|controller| controller.request(value))
    }

    pub(crate) async fn drive(&self, mut advance: mpsc::Receiver<()>) -> io::Result<()> {
        for step in [Step::Cancel, Step::LoseResponse, Step::Recover] {
            if advance.recv().await.is_none() {
                return Ok(());
            }
            self.update(|controller| Ok(step.run(controller)))?;
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
            .map_err(|_| io::Error::other("performance scenario state poisoned"))?;
        let next = state.status["revision"]
            .as_i64()
            .and_then(|value| value.checked_add(1))
            .ok_or_else(|| io::Error::other("invalid performance status revision"))?;
        let outcome = run(&mut state.controller)?;
        let previous = state.status.clone();
        state.project(outcome.message);
        if state.status != previous {
            state.status["revision"] = next.into();
            self.publish.send_replace(Arc::new(state.status.clone()));
        }
        Ok(outcome)
    }
}

impl State {
    fn project(&mut self, message: &'static str) {
        self.status["writesBlocked"] = false.into();
        self.status["unconfirmedWrites"] = json!([]);
        self.status["externalChanges"] = json!([]);
        self.controller.project(&mut self.status);
        self.status["message"] = message.into();
    }
}

impl Controller {
    pub(crate) fn project(&self, status: &mut Value) {
        let controller = self;
        let (desired, source) = controller.desired();
        status["desiredState"]["performance"] = json!({"value": desired as i32, "source": source});
        status["confirmedState"]["performance"] =
            json!(controller.confirmed.map(|value| value as i32));
        status["performanceOverride"] = json!(
            controller
                .store
                .persistent
                .map(|value| (value, 1))
                .or_else(|| controller.temporary.map(|value| (value, 0)))
                .map(|(value, lifetime)| json!({
                    "value": value as i32,
                    "lifetime": lifetime,
                    "customPerformance": null,
                    "gpuClockTuning": null,
                }))
        );
        status["writesBlocked"] = (status["writesBlocked"] == true
            || controller.pending.is_some()
            || !controller.device.admits_writes())
        .into();
        if controller.pending.is_some() {
            status["unconfirmedWrites"]
                .as_array_mut()
                .unwrap()
                .push(json!(PERFORMANCE));
        }
        if controller.external {
            status["externalChanges"]
                .as_array_mut()
                .unwrap()
                .push(json!(PERFORMANCE));
        }
    }
}

#[derive(Clone, Copy)]
pub(crate) enum Step {
    Cancel,
    LoseResponse,
    Recover,
}

impl Step {
    pub(crate) fn run(self, controller: &mut Controller) -> Outcome {
        let message = match self {
            Self::Cancel => {
                controller.device.arm(Fault::CancelBeforeFirst);
                "Synthetic pre-submission cancellation armed."
            }
            Self::LoseResponse => {
                controller.device.arm(Fault::LoseSecondResponse);
                "Synthetic lost response armed."
            }
            Self::Recover => {
                controller.device.arm(Fault::None);
                controller.observe();
                "Synthetic read-only performance observation complete."
            }
        };
        Outcome::new(ScopedStatus::Unchanged, message)
    }
}

#[cfg(test)]
mod tests;

//! Battery-only controller and inert effects for the private foreground scenario.

use std::{
    io,
    sync::{Arc, Mutex},
};

use openblade_domain::{
    automation,
    recovery::{ScopedStatus, SubmissionCertainty},
};
use serde::Deserialize;
use serde_json::{Value, json};
use tokio::sync::{mpsc, watch};

use crate::battery_policy::{
    self as policy, Capabilities, Command, Confirmation, Confirmed, Desired, Intent, Limit, Plan,
    Submission,
};

// Exact RZ09-0581 (1532:02E0) production battery fixture, not an admission table
// for any other model. This host has no transport for these typed setters.
const LIMITS: &[i32] = &[50, 55, 60, 65, 70, 75, 80];
const CAPABILITIES: Capabilities<'static> = Capabilities {
    limits: LIMITS,
    protection_off: true,
    temporary: true,
};
const BATTERY_SUBSYSTEM: i32 = 2;

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum Power {
    PluggedIn,
    Battery,
    UsbC,
}

impl Power {
    fn index(self) -> usize {
        match self {
            Self::PluggedIn => 0,
            Self::Battery => 1,
            Self::UsbC => 2,
        }
    }
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Profile {
    battery_protection_enabled: Option<bool>,
    performance: i32,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields, rename_all = "camelCase")]
pub(crate) struct LimitValue {
    pub charge_limit_percent: i32,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct StoredProfiles {
    plugged_in: Profile,
    battery: Profile,
    usb_c: Profile,
    battery_policy: LimitValue,
}

trait ProfileSource {
    fn load(&self) -> io::Result<StoredProfiles>;
}

struct FixtureProfiles(Value);

impl ProfileSource for FixtureProfiles {
    fn load(&self) -> io::Result<StoredProfiles> {
        Ok(serde_json::from_value(self.0.clone())?)
    }
}

trait Device {
    fn admit(&mut self, power: Power) -> bool;
    fn read_policy(&mut self) -> Option<Confirmed>;
    // Each setter performs its own fresh submission check, after the baseline.
    fn submit(&mut self, power: Power, command: Command) -> Submission;
}

#[derive(Clone, Copy)]
struct Pending {
    target: Desired,
    explicit: bool,
}

struct Controller<D> {
    device: D,
    saved_limit: Limit,
    profiles: [Profile; 3],
    power: Power,
    policy_override: Option<Limit>,
    protection_override: Option<bool>,
    confirmed: Option<Confirmed>,
    pending: Option<Pending>,
    external: bool,
}

#[derive(Debug)]
pub(crate) struct Result {
    pub status: ScopedStatus,
    pub message: &'static str,
}

impl Controller<FakeDevice> {
    fn fixture(status: &Value) -> io::Result<Self> {
        let read = Confirmed {
            limit: Limit::new(80),
            temporary: Some(false),
        };
        Self::new(
            &FixtureProfiles(status["profiles"].clone()),
            FakeDevice::new(read),
        )
    }
}

impl<D: Device> Controller<D> {
    fn new(storage: &impl ProfileSource, device: D) -> io::Result<Self> {
        let stored = storage.load()?;
        let saved_limit = Limit::new(stored.battery_policy.charge_limit_percent)
            .ok_or_else(|| io::Error::other("invalid saved battery limit"))?;
        Ok(Self {
            device,
            saved_limit,
            profiles: [stored.plugged_in, stored.battery, stored.usb_c],
            power: Power::PluggedIn,
            policy_override: None,
            protection_override: None,
            confirmed: None,
            pending: None,
            external: false,
        })
    }

    fn profile(&self) -> &Profile {
        &self.profiles[self.power.index()]
    }

    fn saved_protection(&self) -> bool {
        self.profile().battery_protection_enabled.unwrap_or(true)
    }

    fn desired(&self) -> Desired {
        Desired {
            limit: self.policy_override.unwrap_or(self.saved_limit),
            protection: self.protection_override.unwrap_or(self.saved_protection()),
        }
    }

    fn commit(&mut self, target: Desired) {
        self.policy_override = (target.limit != self.saved_limit).then_some(target.limit);
        self.protection_override =
            (target.protection != self.saved_protection()).then_some(target.protection);
    }

    fn apply(&mut self, intent: Intent) -> io::Result<Result> {
        use ScopedStatus::{Applied, Blocked, Failed, Unchanged};
        if self.pending.is_some() {
            return Ok(Result {
                status: Blocked,
                message: "Battery was not written because OpenBlade must confirm the current laptop state first.",
            });
        }
        if !self.device.admit(self.power) {
            return Ok(Result {
                status: Blocked,
                message: "The laptop could not be admitted for this control operation.",
            });
        }
        let baseline = self.device.read_policy();
        let target = intent.stage(self.desired());
        let status = match policy::plan(intent, target, CAPABILITIES, baseline) {
            Plan::Blocked => {
                self.confirmed = baseline;
                Blocked
            }
            Plan::Unchanged => {
                self.confirmed = baseline;
                Unchanged
            }
            Plan::Write(command) => {
                let submission = self.device.submit(self.power, command);
                if submission == Submission::CanceledBeforeSet {
                    return Err(io::ErrorKind::Interrupted.into());
                }
                let readback = if submission == Submission::Acknowledged {
                    let read = self.device.read_policy();
                    self.confirmed = read;
                    policy::confirm(command, read, CAPABILITIES)
                } else {
                    if submission != Submission::Failed(SubmissionCertainty::Indeterminate) {
                        self.confirmed = baseline;
                    }
                    Confirmation::Unavailable
                };
                let outcome = policy::complete(submission, readback);
                if outcome.needs_confirmation {
                    self.pending = Some(Pending {
                        target,
                        explicit: intent != Intent::Profile,
                    });
                } else if readback == Confirmation::Mismatch {
                    self.external = true;
                }
                outcome.status
            }
        };
        if matches!(status, Applied | Unchanged) {
            if intent != Intent::Profile {
                self.commit(target);
            }
            self.external = false;
        }
        let message = match status {
            Applied => "Battery setting applied and confirmed by readback.",
            Unchanged => "Battery setting is already confirmed.",
            Blocked => "Battery value is not supported by the verified capability table.",
            Failed if self.pending.is_some() => "Battery write is indeterminate.",
            Failed => "Battery write was not confirmed.",
        };
        Ok(Result { status, message })
    }

    fn observe(&mut self) {
        if !self.device.admit(self.power) {
            return;
        }
        let Some(read) = self.device.read_policy() else {
            return;
        };
        self.confirmed = Some(read);
        if let Some(pending) = self.pending.take() {
            // Every writer in this slice records PolicyOnly. A missing override
            // getter stays unknown and cannot turn into a false observation.
            let target = pending.target.protection.then_some(pending.target.limit);
            let matches = read.limit == target && read.temporary != Some(true);
            if matches && pending.explicit {
                self.commit(pending.target);
            }
            self.external = !matches;
        }
    }

    fn change_power(&mut self, source: Power) -> io::Result<Result> {
        if self.pending.is_some() {
            // A new source is outside this slice until read-only recovery settles.
            return self.apply(Intent::Profile);
        }
        if self.power == source {
            return Ok(Result {
                status: ScopedStatus::Unchanged,
                message: "Battery power profile is already selected.",
            });
        }
        self.power = source;
        self.protection_override = None;
        self.apply(Intent::Profile)
    }
}

struct FakeDevice {
    observed: Confirmed,
    admitted: bool,
    next_submission: Option<Submission>,
    #[cfg(test)]
    effects: Vec<Effect>,
    #[cfg(test)]
    read_override: Option<Option<Confirmed>>,
}

#[cfg(test)]
#[derive(Debug, Eq, PartialEq)]
enum Effect {
    Admit,
    Read,
    Set(Command),
}

impl FakeDevice {
    fn new(observed: Confirmed) -> Self {
        Self {
            observed,
            admitted: true,
            next_submission: None,
            #[cfg(test)]
            effects: Vec::new(),
            #[cfg(test)]
            read_override: None,
        }
    }
}

impl Device for FakeDevice {
    fn admit(&mut self, _power: Power) -> bool {
        #[cfg(test)]
        self.effects.push(Effect::Admit);
        self.admitted
    }

    fn read_policy(&mut self) -> Option<Confirmed> {
        #[cfg(test)]
        self.effects.push(Effect::Read);
        #[cfg(test)]
        if let Some(read) = self.read_override.take() {
            return read;
        }
        Some(self.observed)
    }

    fn submit(&mut self, power: Power, command: Command) -> Submission {
        if !self.admit(power) {
            return Submission::Refused;
        }
        let submission = self
            .next_submission
            .take()
            .unwrap_or(Submission::Acknowledged);
        if matches!(
            submission,
            Submission::Acknowledged | Submission::Failed(SubmissionCertainty::Indeterminate)
        ) {
            #[cfg(test)]
            self.effects.push(Effect::Set(command));
            self.observed.limit = match command {
                Command::SetLimit(limit) => Some(limit),
                Command::ProtectionOff => None,
            };
        }
        submission
    }
}

pub(crate) struct Scenario {
    state: Mutex<ScenarioState>,
    publish: watch::Sender<Arc<Value>>,
}

struct ScenarioState {
    controller: Controller<FakeDevice>,
    current: Value,
}

impl Scenario {
    pub(crate) fn new(baseline: Value) -> io::Result<Self> {
        let mut controller = Controller::fixture(&baseline)?;
        controller.observe();
        let mut state = ScenarioState {
            controller,
            current: baseline,
        };
        state.current["deviceVerified"] = true.into();
        state.current["potentialControllers"] = json!([]);
        state.current["capabilities"]["chargeLimits"] = json!(LIMITS);
        state.current["capabilities"]["supportsBatteryProtectionOff"] =
            CAPABILITIES.protection_off.into();
        state.current["capabilities"]["supportsTemporaryFullCharge"] =
            CAPABILITIES.temporary.into();
        state.project("Synthetic battery scenario ready.");
        let (publish, _) = watch::channel(Arc::new(state.current.clone()));
        Ok(Self {
            state: Mutex::new(state),
            publish,
        })
    }

    pub(crate) fn subscribe(&self) -> watch::Receiver<Arc<Value>> {
        self.publish.subscribe()
    }

    pub(crate) async fn drive(&self, mut advance: mpsc::Receiver<()>) -> io::Result<()> {
        for step in [
            Step::Cancel,
            Step::LoseResponse,
            Step::Recover,
            Step::Battery,
            Step::UsbC,
        ] {
            if advance.recv().await.is_none() {
                break;
            }
            self.update(|controller| step.run(controller))?;
        }
        // Keep the foreground stdin consumer connected until host shutdown.
        while advance.recv().await.is_some() {}
        Ok(())
    }

    pub(crate) fn apply(&self, intent: Intent) -> io::Result<Result> {
        self.update(|controller| controller.apply(intent))
    }

    fn update(
        &self,
        run: impl FnOnce(&mut Controller<FakeDevice>) -> io::Result<Result>,
    ) -> io::Result<Result> {
        let mut state = self
            .state
            .lock()
            .map_err(|_| io::Error::other("battery scenario state poisoned"))?;
        let revision = state.current["revision"]
            .as_i64()
            .and_then(|value| value.checked_add(1))
            .ok_or_else(|| io::Error::other("invalid battery status revision"))?;
        let result = run(&mut state.controller)?;
        let previous = state.current.clone();
        state.project(result.message);
        if state.current != previous {
            state.current["revision"] = revision.into();
            self.publish.send_replace(Arc::new(state.current.clone()));
        }
        Ok(result)
    }
}

impl ScenarioState {
    fn project(&mut self, message: &'static str) {
        let controller = &self.controller;
        let desired = controller.desired();
        self.current["activePowerSource"] = json!(controller.power.index());
        let performance = controller.profile().performance;
        if automation::performance_source_eligible(performance, controller.power.index() as i32) {
            self.current["desiredState"]["performance"]["value"] = json!(performance);
        }
        self.current["desiredState"]["battery"] = json!({
            "configuredPolicy": {"chargeLimitPercent": desired.limit.percent()},
            "protectionEnabled": desired.protection,
            "temporaryFullCharge": false,
        });
        self.current["confirmedState"]["battery"] = json!(controller.confirmed.map(|read| json!({
            "protectionEnabled": read.limit.is_some(),
            "chargeLimitPercent": read.limit.map(Limit::percent),
            "temporaryFullCharge": read.temporary,
        })));
        self.current["batteryPolicyOverride"] =
            json!(controller.policy_override.map(|limit| json!({
                "chargeLimitPercent": limit.percent(),
            })));
        self.current["batteryProtectionOverride"] = json!(controller.protection_override);
        self.current["writesBlocked"] = json!(controller.pending.is_some());
        self.current["unconfirmedWrites"] = if controller.pending.is_some() {
            json!([BATTERY_SUBSYSTEM])
        } else {
            json!([])
        };
        self.current["externalChanges"] = if controller.external {
            json!([BATTERY_SUBSYSTEM])
        } else {
            json!([])
        };
        self.current["message"] = message.into();
    }
}

#[derive(Clone, Copy)]
enum Step {
    Cancel,
    LoseResponse,
    Recover,
    Battery,
    UsbC,
}

impl Step {
    fn run(self, controller: &mut Controller<FakeDevice>) -> io::Result<Result> {
        let message = match self {
            Self::Cancel => {
                controller.device.next_submission = Some(Submission::CanceledBeforeSet);
                "Synthetic pre-submission cancellation armed."
            }
            Self::LoseResponse => {
                controller.device.next_submission =
                    Some(Submission::Failed(SubmissionCertainty::Indeterminate));
                "Synthetic lost response armed."
            }
            Self::Recover => {
                controller.observe();
                "Synthetic read-only battery observation complete."
            }
            Self::Battery => return controller.change_power(Power::Battery),
            Self::UsbC => return controller.change_power(Power::UsbC),
        };
        Ok(Result {
            status: ScopedStatus::Unchanged,
            message,
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn setup() -> Controller<FakeDevice> {
        let baseline: Value = serde_json::from_str(crate::wire::CURRENT_FIXTURE).unwrap();
        Controller::fixture(&baseline["status"]).unwrap()
    }

    fn limit(percent: i32) -> Limit {
        Limit::new(percent).unwrap()
    }

    #[test]
    fn scenario_capabilities_follow_the_exact_model_evidence() {
        let entries: Vec<Value> = serde_json::from_str(include_str!(
            "../../../src/OpenBlade.Protocol/Fixtures/Rz09_0581_02e0_3_01_BatteryProduction.json"
        ))
        .unwrap();
        let mut limits = Vec::new();
        let mut protection_off = false;
        let mut temporary_enable = false;
        let mut temporary_cancel = false;
        for entry in entries {
            let action = entry["action"].as_str().unwrap();
            if !action.starts_with("battery-limit-")
                && !matches!(
                    action,
                    "battery-protection-off" | "charge-to-100-enable" | "charge-to-100-cancel"
                )
            {
                continue;
            }
            assert_eq!(entry["device"]["model"], "RZ09-0581");
            assert_eq!(entry["device"]["vendorId"], 0x1532);
            assert_eq!(entry["device"]["productId"], 0x02e0);
            for key in ["captured", "decoded", "applied", "readBack", "restored"] {
                assert_eq!(entry[key], true, "{action}: {key}");
            }
            match action {
                "battery-protection-off" => protection_off = true,
                "charge-to-100-enable" => temporary_enable = true,
                "charge-to-100-cancel" => temporary_cancel = true,
                limit => limits.push(
                    limit
                        .trim_start_matches("battery-limit-")
                        .parse::<i32>()
                        .unwrap(),
                ),
            }
        }
        limits.sort_unstable();
        assert_eq!(limits, CAPABILITIES.limits);
        assert_eq!(protection_off, CAPABILITIES.protection_off);
        assert_eq!(temporary_enable && temporary_cancel, CAPABILITIES.temporary);
    }

    #[test]
    fn standard_trajectory_matches_managed_effect_order_and_intent_boundaries() {
        use Effect::{Admit, Read, Set};
        use ScopedStatus::{Applied, Blocked, Failed, Unchanged};

        let mut battery = setup();
        battery.observe();
        battery.device.effects.clear();
        let cases = [
            (
                Intent::Limit(limit(75)),
                Applied,
                Some(Command::SetLimit(limit(75))),
            ),
            (Intent::Limit(limit(75)), Unchanged, None),
            (Intent::Limit(limit(76)), Blocked, None),
            (
                Intent::Protection(false),
                Applied,
                Some(Command::ProtectionOff),
            ),
            (Intent::Limit(limit(70)), Blocked, None),
            (
                Intent::Protection(true),
                Applied,
                Some(Command::SetLimit(limit(75))),
            ),
        ];
        for (intent, status, setter) in cases {
            assert_eq!(battery.apply(intent).unwrap().status, status);
            let effects = std::mem::take(&mut battery.device.effects);
            match setter {
                Some(command) => assert_eq!(effects, [Admit, Read, Admit, Set(command), Read]),
                None => assert_eq!(effects, [Admit, Read]),
            }
        }

        battery.device.next_submission =
            Some(Submission::Failed(SubmissionCertainty::Indeterminate));
        assert_eq!(
            battery.apply(Intent::Limit(limit(70))).unwrap().status,
            Failed
        );
        assert_eq!(
            std::mem::take(&mut battery.device.effects),
            [Admit, Read, Admit, Set(Command::SetLimit(limit(70)))],
        );
        assert_eq!(battery.confirmed.unwrap().limit, Some(limit(75)));
        assert_eq!(battery.desired().limit, limit(75));
        assert_eq!(
            battery.apply(Intent::Protection(false)).unwrap().status,
            Blocked
        );
        assert!(battery.device.effects.is_empty());

        battery.device.read_override = Some(None);
        battery.observe();
        assert!(battery.pending.is_some());
        battery.device.read_override = Some(Some(Confirmed {
            limit: Some(limit(70)),
            temporary: None,
        }));
        battery.observe();
        assert_eq!(battery.device.effects, [Admit, Read, Admit, Read]);
        assert!(battery.pending.is_none());
        assert_eq!(battery.desired().limit, limit(70));
        assert_eq!(battery.saved_limit, limit(80));
        assert_eq!(battery.confirmed.unwrap().temporary, None);
    }

    #[test]
    fn cancellation_known_refusal_and_unknown_preflight_do_not_submit_or_commit() {
        let mut battery = setup();
        battery.device.next_submission = Some(Submission::CanceledBeforeSet);
        assert_eq!(
            battery.apply(Intent::Protection(false)).unwrap_err().kind(),
            io::ErrorKind::Interrupted,
        );
        assert_eq!(
            std::mem::take(&mut battery.device.effects),
            [Effect::Admit, Effect::Read, Effect::Admit],
        );

        battery.device.next_submission =
            Some(Submission::Failed(SubmissionCertainty::KnownNotApplied));
        assert_eq!(
            battery.apply(Intent::Protection(false)).unwrap().status,
            ScopedStatus::Failed
        );
        assert_eq!(
            std::mem::take(&mut battery.device.effects),
            [Effect::Admit, Effect::Read, Effect::Admit],
        );

        for temporary in [None, Some(true)] {
            battery.device.observed.temporary = temporary;
            assert_eq!(
                battery.apply(Intent::Protection(false)).unwrap().status,
                ScopedStatus::Blocked
            );
            assert_eq!(
                std::mem::take(&mut battery.device.effects),
                [Effect::Admit, Effect::Read]
            );
        }
        battery.device.admitted = false;
        assert_eq!(
            battery.apply(Intent::Protection(false)).unwrap().status,
            ScopedStatus::Blocked
        );
        assert_eq!(battery.device.effects, [Effect::Admit]);
        assert!(battery.pending.is_none());
        assert!(battery.desired().protection);
    }

    #[test]
    fn recovery_mismatch_and_restart_do_not_replay_old_requests() {
        let mut battery = setup();
        battery.device.next_submission =
            Some(Submission::Failed(SubmissionCertainty::Indeterminate));
        battery.apply(Intent::Limit(limit(70))).unwrap();
        battery.device.observed.limit = Some(limit(65));
        battery.device.effects.clear();
        battery.observe();
        assert_eq!(battery.device.effects, [Effect::Admit, Effect::Read]);
        assert!(battery.pending.is_none());
        assert!(battery.external);
        assert_eq!(battery.desired().limit, limit(80));
        assert_eq!(battery.confirmed.unwrap().limit, Some(limit(65)));

        let mut restarted = setup();
        restarted.device.observed = battery.device.observed;
        restarted.observe();
        assert_eq!(restarted.device.effects, [Effect::Admit, Effect::Read]);
        assert_eq!(restarted.desired().limit, limit(80));
        assert_eq!(restarted.confirmed.unwrap().limit, Some(limit(65)));
    }

    #[test]
    fn power_change_reselects_protection_without_losing_the_global_limit_override() {
        let mut battery = setup();
        battery.apply(Intent::Limit(limit(75))).unwrap();
        battery.apply(Intent::Protection(false)).unwrap();
        battery.device.effects.clear();
        assert_eq!(
            battery.change_power(Power::Battery).unwrap().status,
            ScopedStatus::Applied
        );
        assert_eq!(
            battery.device.effects,
            [
                Effect::Admit,
                Effect::Read,
                Effect::Admit,
                Effect::Set(Command::SetLimit(limit(75))),
                Effect::Read
            ],
        );
        assert_eq!(battery.policy_override, Some(limit(75)));
        assert_eq!(battery.protection_override, None);
    }
}

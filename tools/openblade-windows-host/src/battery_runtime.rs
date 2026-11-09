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
    battery_policy: StoredLimit,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct StoredLimit {
    charge_limit_percent: i32,
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
    drift_baseline: Option<Confirmed>,
    pending: Option<Pending>,
    observed_power_while_pending: Option<Power>,
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
        let controller = Self::new(
            &FixtureProfiles(status["profiles"].clone()),
            FakeDevice::new(read),
        )?;
        validate_status_fixture(status, &controller.profiles)?;
        Ok(controller)
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
            drift_baseline: None,
            pending: None,
            observed_power_while_pending: None,
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
        let mut block_message =
            "Battery request does not meet the verified capability or state requirements.";
        let status = match policy::plan(intent, target, CAPABILITIES, baseline) {
            Plan::Blocked => {
                self.record_readback(baseline);
                block_message = match baseline.and_then(|read| read.temporary) {
                    None => "Battery policy or temporary full charge could not be confirmed.",
                    Some(true) => {
                        "Battery changes are blocked while temporary full charge is active."
                    }
                    Some(false) => block_message,
                };
                Blocked
            }
            Plan::Unchanged => {
                self.record_readback(baseline);
                Unchanged
            }
            Plan::Write(command) => {
                let submission = self.device.submit(self.power, command);
                if submission == Submission::CanceledBeforeSet {
                    return Err(io::ErrorKind::Interrupted.into());
                }
                if submission == Submission::Refused {
                    block_message = "Battery write admission closed before submission.";
                }
                let readback = if submission == Submission::Acknowledged {
                    let read = self.device.read_policy();
                    self.record_readback(read);
                    policy::confirm(command, read, CAPABILITIES)
                } else {
                    // As in the managed scoped runtime, an indeterminate SET
                    // returns no semantic readback. Preserve the last published
                    // state until an independent observation.
                    if submission != Submission::Failed(SubmissionCertainty::Indeterminate) {
                        self.record_readback(baseline);
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
            Blocked => block_message,
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
        self.record_readback(Some(read));
        if let Some(pending) = self.pending.take() {
            // A pending PolicyOnly confirmation can settle even if the
            // temporary flag is unavailable. Preserve that unknown in status.
            let matches = policy_matches(read, pending.target);
            if let Some(destination) = self.observed_power_while_pending.take() {
                // The observation belongs to a new source. Publish it without
                // committing the previous source's intent or issuing a setter.
                self.select_power(destination);
                self.external = !policy_matches(read, self.desired());
            } else {
                if matches && pending.explicit {
                    self.commit(pending.target);
                }
                self.external = !matches;
            }
        }
    }

    fn record_readback(&mut self, read: Option<Confirmed>) {
        self.confirmed = read;
        let Some(read) = read else {
            return;
        };
        let previous_temporary = self.drift_baseline.and_then(|old| old.temporary);
        if self
            .drift_baseline
            .is_some_and(|old| old.limit != read.limit)
            || previous_temporary
                .zip(read.temporary)
                .is_some_and(|(old, new)| old != new)
        {
            self.external = true;
        }
        self.drift_baseline = Some(Confirmed {
            temporary: read.temporary.or(previous_temporary),
            ..read
        });
    }

    fn select_power(&mut self, source: Power) {
        self.power = source;
        self.protection_override = None;
    }

    fn change_power(&mut self, source: Power) -> io::Result<Result> {
        if self.pending.is_some() {
            self.observed_power_while_pending = (source != self.power).then_some(source);
            return self.apply(Intent::Profile);
        }
        if self.power == source {
            return Ok(Result {
                status: ScopedStatus::Unchanged,
                message: "Battery power profile is already selected.",
            });
        }
        self.select_power(source);
        match self.apply(Intent::Profile) {
            Err(error) if error.kind() == io::ErrorKind::Interrupted => Ok(Result {
                status: ScopedStatus::Failed,
                message: "Battery profile setter canceled before submission.",
            }),
            result => result,
        }
    }
}

fn policy_matches(read: Confirmed, target: Desired) -> bool {
    read.limit == target.protection.then_some(target.limit) && read.temporary != Some(true)
}

fn validate_status_fixture(status: &Value, profiles: &[Profile; 3]) -> io::Result<()> {
    let first = status["profiles"]["pluggedIn"]
        .as_object()
        .ok_or_else(|| io::Error::other("missing battery parity profile fixture"))?;
    for (index, key) in ["pluggedIn", "battery", "usbC"].into_iter().enumerate() {
        let profile = status["profiles"][key]
            .as_object()
            .ok_or_else(|| io::Error::other("missing battery parity power profile"))?;
        let preset = profiles[index].performance;
        // Keep non-battery fixture fields common, except for the same
        // performance projection that the existing runtime replay handles.
        if !matches!(preset, 0..=4)
            || !automation::performance_source_eligible(preset, index as i32)
            || profile.len() != first.len()
            || !first.iter().all(|(field, value)| match field.as_str() {
                "performance" | "customPerformance" | "batteryProtectionEnabled" => {
                    profile.contains_key(field)
                }
                _ => profile.get(field) == Some(value),
            })
        {
            return Err(io::Error::other("unsupported battery parity power profile"));
        }
    }
    if status["desiredState"]["performance"]["source"] != 1
        || !status["desiredState"]["customPerformance"].is_null()
        || !status["activeApplicationProfileId"].is_null()
        || [
            "performanceOverride",
            "fanOverride",
            "fanControlOverride",
            "lightingOverride",
            "batteryPolicyOverride",
            "batteryProtectionOverride",
        ]
        .iter()
        .any(|key| !status[*key].is_null())
        || status["fullChargeOverride"] != false
    {
        return Err(io::Error::other(
            "unsupported battery parity status fixture",
        ));
    }
    Ok(())
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
        self.current["desiredState"]["performance"]["value"] =
            json!(controller.profile().performance);
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
                // A fault is valid only until the next script step. In
                // particular, no unused fault can reach a power transition.
                controller.device.next_submission = None;
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
    fn pending_write_observes_new_power_without_committing_old_source_intent() {
        let mut battery = setup();
        battery.observe();
        battery.device.next_submission =
            Some(Submission::Failed(SubmissionCertainty::Indeterminate));
        battery.apply(Intent::Limit(limit(70))).unwrap();
        battery.device.effects.clear();

        assert_eq!(
            battery.change_power(Power::Battery).unwrap().status,
            ScopedStatus::Blocked
        );
        assert!(battery.device.effects.is_empty());
        assert_eq!(battery.power, Power::PluggedIn);

        battery.observe();
        assert_eq!(battery.device.effects, [Effect::Admit, Effect::Read]);
        assert_eq!(battery.power, Power::Battery);
        assert!(battery.pending.is_none());
        assert!(battery.external);
        assert_eq!(battery.confirmed.unwrap().limit, Some(limit(70)));
        assert_eq!(battery.desired().limit, limit(80));
    }

    #[test]
    fn unused_script_faults_do_not_escape_into_power_changes() {
        let mut battery = setup();
        battery.apply(Intent::Protection(false)).unwrap();
        for step in [Step::Cancel, Step::LoseResponse, Step::Recover] {
            step.run(&mut battery).unwrap();
        }
        assert_eq!(
            Step::Battery.run(&mut battery).unwrap().status,
            ScopedStatus::Applied
        );
        assert_eq!(battery.confirmed.unwrap().limit, Some(limit(80)));
        assert!(battery.pending.is_none());
    }

    #[test]
    fn partial_passive_reads_keep_drift_evidence_until_a_matching_intent() {
        let mut battery = setup();
        battery.observe();
        battery.device.observed.limit = Some(limit(65));
        battery.device.observed.temporary = None;
        battery.observe();
        assert!(battery.external);
        assert_eq!(battery.confirmed.unwrap().temporary, None);
        battery.device.observed.limit = Some(limit(80));
        battery.device.observed.temporary = Some(false);
        battery.observe();
        assert!(battery.external);
        assert_eq!(
            battery.apply(Intent::Limit(limit(80))).unwrap().status,
            ScopedStatus::Unchanged
        );
        assert!(!battery.external);
    }

    #[test]
    fn explicit_enable_does_not_retarget_already_enabled_protection() {
        let mut battery = setup();
        battery.observe();
        battery.device.observed.limit = Some(limit(65));
        battery.device.effects.clear();
        assert_eq!(
            battery.apply(Intent::Protection(true)).unwrap().status,
            ScopedStatus::Blocked
        );
        assert_eq!(battery.device.effects, [Effect::Admit, Effect::Read]);
        assert_eq!(battery.confirmed.unwrap().limit, Some(limit(65)));
        assert_eq!(battery.desired().limit, limit(80));
    }

    #[test]
    fn lost_response_retains_last_published_state_when_the_baseline_has_drifted() {
        let mut battery = setup();
        battery.observe();
        battery.device.observed.limit = Some(limit(65));
        battery.device.next_submission =
            Some(Submission::Failed(SubmissionCertainty::Indeterminate));
        assert_eq!(
            battery.apply(Intent::Limit(limit(70))).unwrap().status,
            ScopedStatus::Failed
        );
        assert_eq!(battery.confirmed.unwrap().limit, Some(limit(80)));
        assert_eq!(battery.device.observed.limit, Some(limit(70)));
        battery.observe();
        assert_eq!(battery.confirmed.unwrap().limit, Some(limit(70)));
        assert_eq!(battery.desired().limit, limit(70));
    }

    #[test]
    fn status_fixture_rejects_unprojected_power_differences_and_accepts_additive_storage() {
        let baseline: Value = serde_json::from_str(crate::wire::CURRENT_FIXTURE).unwrap();
        let mut status = baseline["status"].clone();
        status["profiles"]["batteryPolicy"]["futureField"] = json!("safe additive field");
        assert!(Controller::fixture(&status).is_ok());
        status["profiles"]["battery"]["fans"] = json!(1);
        assert!(Controller::fixture(&status).is_err());
        status["profiles"]["battery"]["fans"] = json!(0);
        status["profiles"]["battery"]["performance"] = json!(1);
        assert!(Controller::fixture(&status).is_err());
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

//! Battery-only controller and inert effects for the private foreground scenario.

use std::{io, sync::Arc};

use openblade_domain::{
    automation,
    recovery::{ScopedStatus, SubmissionCertainty},
};
use openblade_protocol::{REPORT_LENGTH, RazerReport};
use serde::Deserialize;
use serde_json::{Value, json};
use tokio::sync::{Mutex, mpsc, watch};

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
enum Pending {
    Policy { target: Desired, explicit: bool },
    FullCharge(Command),
}

impl Pending {
    fn confirm(self, read: Confirmed) -> Confirmation {
        match self {
            Self::Policy { target, .. } => {
                // A limiter-only confirmation can settle from policy fields.
                if policy_matches(read, target) {
                    Confirmation::Match
                } else {
                    Confirmation::Mismatch
                }
            }
            Self::FullCharge(command) => policy::confirm(command, Some(read), CAPABILITIES),
        }
    }
}

struct Controller<D> {
    device: D,
    saved_limit: Limit,
    profiles: [Profile; 3],
    power: Power,
    policy_override: Option<Limit>,
    protection_override: Option<bool>,
    full_charge: bool,
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
            full_charge: false,
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
        if intent == Intent::FullCharge(true) {
            let rejection = match (self.power, self.desired().protection) {
                (Power::Battery, _) => {
                    Some("Temporary full charge can only be enabled while plugged in.")
                }
                (_, false) => {
                    Some("Temporary full charge requires battery protection to be enabled.")
                }
                _ => None,
            };
            if let Some(message) = rejection {
                return Ok(Result {
                    status: Failed,
                    message,
                });
            }
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
                    Some(true) if !matches!(intent, Intent::FullCharge(_)) => {
                        "Battery changes are blocked while temporary full charge is active."
                    }
                    Some(_) => block_message,
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
                    self.pending = Some(match command {
                        Command::FullCharge { .. } => Pending::FullCharge(command),
                        _ => Pending::Policy {
                            target,
                            explicit: intent != Intent::Profile,
                        },
                    });
                } else if readback == Confirmation::Mismatch {
                    self.external = true;
                }
                outcome.status
            }
        };
        if matches!(status, Applied | Unchanged) {
            match intent {
                Intent::FullCharge(enabled) => self.full_charge = enabled,
                Intent::Profile => {}
                _ => self.commit(target),
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
        if let Some(pending) = self.pending {
            let confirmation = pending.confirm(read);
            if confirmation == Confirmation::Unavailable {
                return;
            }
            self.pending = None;
            let matches = confirmation == Confirmation::Match;
            if let Some(destination) = self.observed_power_while_pending.take() {
                // Read-only reconciliation cannot commit a superseded intent.
                self.select_power(destination);
                self.external =
                    !policy_matches(read, self.desired()) || read.temporary == Some(true);
            } else {
                if matches {
                    match pending {
                        Pending::Policy {
                            target,
                            explicit: true,
                        } => self.commit(target),
                        Pending::FullCharge(Command::FullCharge { enabled, .. }) => {
                            self.full_charge = enabled;
                        }
                        _ => {}
                    }
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
        let mut previous_temporary = self.drift_baseline.and_then(|old| old.temporary);
        if self.full_charge && previous_temporary == Some(true) && read.temporary == Some(false) {
            self.full_charge = false;
            previous_temporary = Some(false);
        }
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

    #[cfg(windows)]
    fn store_limit(&mut self, limit: Limit) {
        self.saved_limit = limit;
        if self.policy_override == Some(limit) {
            self.policy_override = None;
        }
        if self.protection_override == Some(self.saved_protection()) {
            self.protection_override = None;
        }
    }

    fn record_power(&mut self, source: Power) {
        if self.pending.is_some() {
            self.observed_power_while_pending = (source != self.power).then_some(source);
        } else if self.power != source {
            self.select_power(source);
        }
    }

    fn select_power(&mut self, source: Power) {
        self.power = source;
        self.protection_override = None;
    }

    fn change_power(&mut self, source: Power) -> io::Result<Result> {
        if self.pending.is_some() {
            self.record_power(source);
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
    power_mode: PowerMode,
    report_reads: bool,
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
    fn query_power_mode(&mut self) -> Option<u8> {
        self.exchange_battery(POWER_MODE_GET, 0, false)
            .filter(|flags| flags & !POWER_MODE_DEFINED_BITS == 0)
    }

    fn query_charge_policy(&mut self) -> Option<Option<Limit>> {
        let value = self.exchange_battery(CHARGE_POLICY_GET, 0, false)?;
        if value == PROTECTION_OFF {
            return Some(None);
        }
        let limit = Limit::new(i32::from(value & 0x7f))?;
        (value & 0x80 != 0 && LIMITS.contains(&limit.percent())).then_some(Some(limit))
    }

    fn exchange_battery(&mut self, id: u8, value: u8, lose_response: bool) -> Option<u8> {
        let transaction = self.power_mode.transaction.checked_add(1)?;
        let request = battery_request(transaction, id, value)?;
        self.power_mode.transaction = transaction;
        let reply = self.reply_to_battery(&request.encode(), lose_response)?;
        Some(request.validate_response(&reply, Some(1)).ok()?.as_slice()[0])
    }

    fn reply_to_battery(
        &mut self,
        bytes: &[u8; REPORT_LENGTH],
        lose_response: bool,
    ) -> Option<[u8; REPORT_LENGTH]> {
        let reply_value = match (bytes[6], bytes[7], bytes[8]) {
            (BATTERY_CLASS, CHARGE_POLICY_GET, 0) => match self.observed.limit {
                Some(limit) => u8::try_from(limit.percent()).ok()? | 0x80,
                None => PROTECTION_OFF,
            },
            (BATTERY_CLASS, POWER_MODE_GET, 0) if !self.power_mode.unreadable => {
                let active = self.observed.temporary?;
                self.power_mode.other_settings | if active { FULL_CHARGE_BIT } else { 0 }
            }
            (BATTERY_CLASS, POWER_MODE_SET, flags @ (0 | FULL_CHARGE_BIT)) => {
                self.observed.temporary = Some(flags == FULL_CHARGE_BIT);
                flags
            }
            _ => return None,
        };
        if lose_response {
            return None;
        }
        let mut reply = battery_request(bytes[1], bytes[7], reply_value)?.encode();
        reply[0] = 2;
        Some(reply)
    }

    fn new(observed: Confirmed) -> Self {
        Self {
            observed,
            admitted: true,
            next_submission: None,
            power_mode: PowerMode::default(),
            report_reads: false,
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
        if !self.report_reads {
            return Some(self.observed);
        }
        let limit = self.query_charge_policy()?;
        Some(Confirmed {
            temporary: self
                .query_power_mode()
                .map(|flags| flags & FULL_CHARGE_BIT != 0),
            limit,
        })
    }

    fn submit(&mut self, power: Power, command: Command) -> Submission {
        if !self.admit(power) {
            return Submission::Refused;
        }
        if let Command::FullCharge { enabled, limit } = command
            && (!self
                .query_power_mode()
                .is_some_and(|flags| flags & !FULL_CHARGE_BIT == 0)
                || self.query_charge_policy() != Some(Some(limit))
                || enabled && power == Power::Battery
                || self.power_mode.transaction == u8::MAX)
        {
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
            match command {
                Command::SetLimit(limit) => self.observed.limit = Some(limit),
                Command::ProtectionOff => self.observed.limit = None,
                Command::FullCharge { enabled, .. } => {
                    let requested = if enabled { FULL_CHARGE_BIT } else { 0 };
                    let lose_response = std::mem::take(&mut self.power_mode.lose_set_reply)
                        || submission != Submission::Acknowledged;
                    let reply = self.exchange_battery(POWER_MODE_SET, requested, lose_response);
                    if reply != Some(requested) {
                        return Submission::Failed(SubmissionCertainty::Indeterminate);
                    }
                }
            }
        }
        submission
    }
}

// The exact RZ09-0581 managed oracle admits these three commands.
// The exchange above models the firmware in memory.
const BATTERY_CLASS: u8 = 0x07;
const CHARGE_POLICY_GET: u8 = 0x92;
const PROTECTION_OFF: u8 = 0x50;
const POWER_MODE_GET: u8 = 0x8f;
const POWER_MODE_SET: u8 = 0x0f;
const FULL_CHARGE_BIT: u8 = 0x04;
const POWER_MODE_DEFINED_BITS: u8 = 0x0f;

#[derive(Default)]
struct PowerMode {
    transaction: u8,
    other_settings: u8,
    unreadable: bool,
    lose_set_reply: bool,
}

fn battery_request(transaction: u8, id: u8, value: u8) -> Option<RazerReport> {
    RazerReport::create(transaction, BATTERY_CLASS, id, &[value]).ok()
}

#[cfg(windows)]
type ProfileWriter = openblade_windows_host::configuration_writer::fixture::BatteryProfileStore;

pub(crate) struct Scenario {
    #[cfg(windows)]
    editable: bool,
    full_charge: bool,
    #[cfg(windows)]
    lighting_off: bool,
    #[cfg(windows)]
    lighting_only: bool,
    #[cfg(windows)]
    lighting_operation: Mutex<()>,
    #[cfg(windows)]
    application_changed: Option<tokio::sync::Notify>,
    state: Arc<Mutex<ScenarioState>>,
    publish: watch::Sender<Arc<Value>>,
}

#[cfg(windows)]
mod application;
#[cfg(windows)]
pub(crate) mod lighting;
#[cfg(windows)]
mod power_profile;
#[cfg(windows)]
mod thermal;

#[cfg(all(windows, test))]
pub(crate) struct ProfileSaveBarrier {
    pub entered: tokio::sync::oneshot::Sender<()>,
    pub resume: tokio::sync::oneshot::Receiver<()>,
    pub saved: tokio::sync::oneshot::Sender<bool>,
}

#[cfg(windows)]
#[derive(Clone, Copy)]
pub(crate) enum ProfileReplay {
    Battery,
    Thermal,
    Power { lose_response: bool },
    FullCharge,
    Integrated { application: bool },
    LightingOff { owned: bool, uncertain: bool },
}

struct ScenarioState {
    #[cfg(windows)]
    application: Option<application::Context>,
    #[cfg(windows)]
    lighting: Option<lighting::Lighting>,
    #[cfg(windows)]
    thermal: Option<thermal::Thermal>,
    controller: Controller<FakeDevice>,
    current: Value,
    #[cfg(windows)]
    writer: Option<ProfileWriter>,
    #[cfg(all(windows, test))]
    profile_save_barrier: Option<ProfileSaveBarrier>,
    configuration_blocked: bool,
}

impl Scenario {
    pub(crate) fn new(baseline: Value) -> io::Result<Self> {
        Self::start(
            baseline,
            false,
            #[cfg(windows)]
            false,
            #[cfg(windows)]
            None,
            #[cfg(windows)]
            None,
            #[cfg(windows)]
            None,
            #[cfg(windows)]
            false,
        )
    }

    #[cfg(windows)]
    pub(crate) fn composed(
        mut baseline: Value,
        profiles: Value,
        writer: Option<ProfileWriter>,
        replay: ProfileReplay,
    ) -> io::Result<Self> {
        let application = matches!(replay, ProfileReplay::Integrated { application: true });
        if matches!(replay, ProfileReplay::Integrated { .. }) && writer.is_none() {
            return Err(io::Error::other(
                "The integrated fixture requires an admitted writer.",
            ));
        }
        baseline["profiles"] = profiles;
        let thermal = match replay {
            ProfileReplay::Battery
            | ProfileReplay::FullCharge
            | ProfileReplay::LightingOff { .. } => None,
            ProfileReplay::Thermal => Some(thermal::Thermal::new(&mut baseline)?),
            ProfileReplay::Power { .. } | ProfileReplay::Integrated { .. } => Some(
                thermal::Thermal::for_power_profile(&mut baseline, !application)?,
            ),
        };
        let lighting = match replay {
            ProfileReplay::LightingOff { owned, .. } => {
                let mut lighting = lighting::Lighting::new(&mut baseline)?;
                if owned {
                    lighting.start_renderer()?;
                }
                Some(lighting)
            }
            ProfileReplay::Integrated { .. } => {
                Some(lighting::Lighting::integrated(&mut baseline)?)
            }
            _ => None,
        };
        if application {
            crate::application_runtime::install_fixture_rule(&mut baseline["profiles"]);
        }
        Self::start(
            baseline,
            matches!(
                replay,
                ProfileReplay::FullCharge | ProfileReplay::Integrated { .. }
            ),
            matches!(replay, ProfileReplay::LightingOff { .. }),
            lighting,
            writer,
            thermal,
            application,
        )
    }

    fn start(
        baseline: Value,
        full_charge: bool,
        #[cfg(windows)] lighting_only: bool,
        #[cfg(windows)] lighting: Option<lighting::Lighting>,
        #[cfg(windows)] writer: Option<ProfileWriter>,
        #[cfg(windows)] thermal: Option<thermal::Thermal>,
        #[cfg(windows)] application: bool,
    ) -> io::Result<Self> {
        #[cfg(windows)]
        let editable = writer.is_some();
        let mut controller = Controller::fixture(&baseline)?;
        controller.device.report_reads = full_charge;
        controller.observe();
        #[cfg(windows)]
        let lighting_off = lighting.is_some();
        let mut state = ScenarioState {
            #[cfg(windows)]
            application: application.then(application::Context::new),
            #[cfg(windows)]
            lighting,
            #[cfg(windows)]
            thermal,
            controller,
            current: baseline,
            #[cfg(windows)]
            writer,
            #[cfg(all(windows, test))]
            profile_save_barrier: None,
            configuration_blocked: false,
        };
        state.current["deviceVerified"] = true.into();
        state.current["capabilities"]["chargeLimits"] = json!(LIMITS);
        state.current["capabilities"]["supportsBatteryProtectionOff"] =
            CAPABILITIES.protection_off.into();
        #[cfg(windows)]
        if lighting_only {
            state.current["capabilities"]["chargeLimits"] = json!([]);
            state.current["capabilities"]["supportsBatteryProtectionOff"] = false.into();
        }
        state.current["capabilities"]["supportsTemporaryFullCharge"] =
            (full_charge && CAPABILITIES.temporary).into();
        state.project("Synthetic battery scenario ready.");
        let (publish, _) = watch::channel(Arc::new(state.current.clone()));
        Ok(Self {
            #[cfg(windows)]
            editable,
            full_charge,
            #[cfg(windows)]
            lighting_off,
            #[cfg(windows)]
            lighting_only,
            #[cfg(windows)]
            lighting_operation: Mutex::new(()),
            #[cfg(windows)]
            application_changed: application.then(tokio::sync::Notify::new),
            state: Arc::new(Mutex::new(state)),
            publish,
        })
    }

    pub(crate) fn accepts_battery(&self, full_charge: bool) -> bool {
        #[cfg(windows)]
        if self.lighting_only {
            return false;
        }
        !full_charge || self.full_charge
    }

    #[cfg(windows)]
    pub(crate) fn accepts_lighting(&self) -> bool {
        self.lighting_off
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
            self.update(|state| {
                if state.configuration_blocked {
                    return step.observe_only(&mut state.controller);
                }
                step.run(&mut state.controller)
            })
            .await?;
        }
        // Keep the foreground stdin consumer connected until host shutdown.
        while advance.recv().await.is_some() {}
        Ok(())
    }

    #[cfg(windows)]
    pub(crate) async fn drive_full_charge(
        &self,
        mut advance: mpsc::Receiver<()>,
    ) -> io::Result<()> {
        type Step = (&'static str, fn(&mut Controller<FakeDevice>));
        let arm_loss: Step = ("Temporary full charge response loss armed.", |controller| {
            controller.device.power_mode.other_settings = 0;
            controller.device.power_mode.lose_set_reply = true;
        });
        let steps: [Step; 11] = [
            ("Temporary full charge read unavailable.", |controller| {
                controller.device.power_mode.unreadable = true;
            }),
            ("Firmware completed temporary full charge.", |controller| {
                controller.device.power_mode.unreadable = false;
                controller.device.observed.temporary = Some(false);
            }),
            ("Battery power observed.", |controller| {
                controller.record_power(Power::Battery);
            }),
            ("External power observed.", |controller| {
                controller.record_power(Power::PluggedIn);
            }),
            (
                "Another Power Mode Control setting is active.",
                |controller| {
                    controller.device.power_mode.other_settings = 0x08;
                },
            ),
            arm_loss,
            (
                "Read-only reconciliation has only the charge policy.",
                |controller| {
                    controller.device.power_mode.unreadable = true;
                },
            ),
            (
                "Read-only reconciliation has both battery readings.",
                |controller| {
                    controller.device.power_mode.unreadable = false;
                },
            ),
            (
                "Reconciled temporary full charge completed.",
                |controller| {
                    controller.device.observed.temporary = Some(false);
                },
            ),
            arm_loss,
            ("Unused full charge response loss expired.", |_| {}),
        ];
        for (message, inject) in steps {
            if advance.recv().await.is_none() {
                return Ok(());
            }
            self.update(|state| {
                state.controller.device.power_mode.lose_set_reply = false;
                inject(&mut state.controller);
                state.controller.observe();
                Ok(Result {
                    status: ScopedStatus::Unchanged,
                    message,
                })
            })
            .await?;
        }
        while advance.recv().await.is_some() {}
        Ok(())
    }

    pub(crate) async fn apply(&self, intent: Intent) -> io::Result<Result> {
        self.update(|state| {
            if state.configuration_blocked {
                return Ok(blocked_configuration());
            }
            #[cfg(windows)]
            if state
                .thermal
                .as_ref()
                .is_some_and(thermal::Thermal::pending_write)
            {
                return Ok(thermal::await_readback());
            }
            #[cfg(windows)]
            if let Some(blocked) = state.session_block().or_else(|| state.unresolved_power()) {
                return Ok(blocked);
            }
            state.controller.apply(intent)
        })
        .await
    }

    #[cfg(windows)]
    pub(crate) fn accepts_profiles(&self) -> bool {
        self.editable
    }

    #[cfg(windows)]
    pub(crate) async fn take_profile_writer(&self) -> Option<ProfileWriter> {
        // A canceled pipe task can leave a submitted blocking transaction holding
        // this guard. Take the writer after the outcome, even if that worker's
        // last reference to the scenario has yet to be dropped.
        self.state.lock().await.writer.take()
    }

    #[cfg(all(windows, test))]
    pub(crate) fn set_profile_save_barrier(&self, barrier: ProfileSaveBarrier) {
        self.state.try_lock().unwrap().profile_save_barrier = Some(barrier);
    }

    /// Only the saved battery policy can change. The application fixture rule
    /// remains in memory. Disk admission precedes publication.
    #[cfg(windows)]
    pub(crate) async fn save_profiles(
        self: Arc<Self>,
        profiles: Value,
        limit: Limit,
    ) -> io::Result<(i32, &'static str)> {
        let mut state = Arc::clone(&self.state).lock_owned().await;
        // After accepting the operation, hold the state until commit and readback finish.
        // Cancellation of the pipe handler leaves the blocking worker owning the guard.
        tokio::task::spawn_blocking(move || {
            let mut stored = false;
            let result = self.update_locked(&mut state, |state| {
                if state.configuration_blocked {
                    return Ok(blocked_configuration());
                }
                if let Some(blocked) = state.session_block() {
                    return Ok(blocked);
                }
                let mut expected = state.current["profiles"].clone();
                expected["batteryPolicy"]["chargeLimitPercent"] = limit.percent().into();
                if profiles != expected {
                    return Ok(Result {
                        status: ScopedStatus::Blocked,
                        message: "Only the saved battery policy can be edited in this fixture.",
                    });
                }
                #[cfg(test)]
                let saved = state.profile_save_barrier.take().map(|barrier| {
                    let _ = barrier.entered.send(());
                    let _ = barrier.resume.blocking_recv();
                    barrier.saved
                });
                let writer = state
                    .writer
                    .as_mut()
                    .ok_or_else(|| io::Error::other("missing battery profile writer"))?;
                let outcome = writer.save(limit.percent() as u8);
                #[cfg(test)]
                if let Some(saved) = saved {
                    let _ = saved.send(outcome.is_ok());
                }
                if let Err(error) = outcome {
                    let _ = std::io::Write::write_fmt(
                        &mut io::stderr().lock(),
                        format_args!("battery configuration save: {error}\n"),
                    );
                    state.configuration_blocked = writer.verify_prior().is_err();
                    return Ok(Result {
                        status: ScopedStatus::Failed,
                        message: if state.configuration_blocked {
                            blocked_configuration().message
                        } else {
                            "Battery profile save failed. The prior configuration is verified."
                        },
                    });
                }
                state.current["profiles"] = profiles;
                state.controller.store_limit(limit);
                stored = true;
                Ok(Result {
                    status: ScopedStatus::Unchanged,
                    message: "Profiles stored without automatic hardware application.",
                })
            })?;
            Ok((
                if stored {
                    0
                } else if result.status == ScopedStatus::Failed {
                    3
                } else {
                    2
                },
                result.message,
            ))
        })
        .await?
    }

    async fn update(
        &self,
        run: impl FnOnce(&mut ScenarioState) -> io::Result<Result>,
    ) -> io::Result<Result> {
        let mut state = self.state.lock().await;
        self.update_locked(&mut state, run)
    }

    fn update_locked(
        &self,
        state: &mut ScenarioState,
        run: impl FnOnce(&mut ScenarioState) -> io::Result<Result>,
    ) -> io::Result<Result> {
        let revision = state.current["revision"]
            .as_i64()
            .and_then(|value| value.checked_add(1))
            .ok_or_else(|| io::Error::other("invalid battery status revision"))?;
        let previous = state.current.clone();
        #[cfg(windows)]
        state.expire_application();
        let result = run(state)?;
        state.project(result.message);
        if state.current != previous {
            state.current["revision"] = revision.into();
            self.publish.send_replace(Arc::new(state.current.clone()));
        }
        Ok(result)
    }
}

fn blocked_configuration() -> Result {
    Result {
        status: ScopedStatus::Blocked,
        message: "Battery configuration could not be verified. Writes are blocked.",
    }
}

impl ScenarioState {
    fn project(&mut self, message: &'static str) {
        let controller = &self.controller;
        let desired = controller.desired();
        self.current["activePowerSource"] = json!(controller.power.index());
        self.current["potentialControllers"] = json!([]);
        self.current["desiredState"]["performance"] =
            json!({"value": controller.profile().performance, "source": 1});
        self.current["desiredState"]["battery"] = json!({
            "configuredPolicy": {"chargeLimitPercent": desired.limit.percent()},
            "protectionEnabled": desired.protection,
            "temporaryFullCharge": controller.full_charge,
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
        self.current["fullChargeOverride"] = json!(controller.full_charge);
        // This summary includes scoped fences. The typed handlers still check
        // their own admission, so renderer retirement cannot grant or revoke
        // an unrelated charge-policy operation.
        self.current["writesBlocked"] =
            json!(self.configuration_blocked || controller.pending.is_some());
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
        #[cfg(windows)]
        if let Some(thermal) = &self.thermal {
            thermal.project(&mut self.current);
        }
        #[cfg(windows)]
        if let Some(lighting) = &self.lighting {
            lighting.project(&mut self.current);
        }
        #[cfg(windows)]
        self.project_application();
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
    fn observe_only(self, controller: &mut Controller<FakeDevice>) -> io::Result<Result> {
        controller.device.next_submission = None;
        match self {
            Self::Recover => return self.run(controller),
            Self::Battery => controller.record_power(Power::Battery),
            Self::UsbC => controller.record_power(Power::UsbC),
            Self::Cancel | Self::LoseResponse => return Ok(blocked_configuration()),
        }
        Ok(Result {
            status: ScopedStatus::Unchanged,
            message: "Synthetic power source observed while configuration writes are blocked.",
        })
    }

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
    fn full_charge_reports_match_the_managed_exact_model_capture() {
        let fixture: Value = serde_json::from_str(include_str!(
            "../../../evidence/legacy-captures/decoded/rz09-0581-pid-02e0-bios-3.01-temporary-full-charge-validation-fixture.json"
        )).unwrap();
        assert_eq!(fixture["device"]["productId"], "02E0");
        assert_eq!(fixture["productionWired"], true);
        let exchanges = fixture["exchanges"].as_array().unwrap();
        let specs = [
            (CHARGE_POLICY_GET, 0, 0xd0),
            (POWER_MODE_GET, 0, 0),
            (POWER_MODE_SET, FULL_CHARGE_BIT, FULL_CHARGE_BIT),
            (CHARGE_POLICY_GET, 0, 0xd0),
            (POWER_MODE_GET, 0, FULL_CHARGE_BIT),
            (POWER_MODE_SET, 0, 0),
            (CHARGE_POLICY_GET, 0, 0xd0),
            (POWER_MODE_GET, 0, 0),
        ];
        assert_eq!(exchanges.len(), specs.len());
        let decode = |hex: &Value| {
            let hex = hex.as_str().unwrap();
            (0..hex.len())
                .step_by(2)
                .map(|index| u8::from_str_radix(&hex[index..index + 2], 16).unwrap())
                .collect::<Vec<_>>()
        };
        let mut device = setup().device;
        for (index, ((id, value, expected), row)) in specs.into_iter().zip(exchanges).enumerate() {
            let request = battery_request((index + 1) as u8, id, value).unwrap();
            assert_eq!(
                request.encode().as_slice(),
                decode(&row["requestEnvelopeHex"])
            );
            let mut reply = decode(&row["responseEnvelopeHex"]);
            assert_eq!(
                request
                    .validate_response(&reply, Some(1))
                    .unwrap()
                    .as_slice(),
                &[expected]
            );
            // The managed envelope validator permits the captured header padding.
            reply[3] = 0;
            reply[88] = openblade_protocol::calculate_checksum(&reply).unwrap();
            assert_eq!(
                device
                    .reply_to_battery(&request.encode(), false)
                    .unwrap()
                    .as_slice(),
                reply
            );
        }
    }

    #[test]
    fn full_charge_rejects_battery_power_before_scoped_admission_and_does_not_wrap_ids() {
        let mut controller = setup();
        controller.device.report_reads = true;
        controller.device.admitted = false;
        controller.power = Power::Battery;
        let enable = Intent::FullCharge(true);
        assert_eq!(
            controller.apply(enable).unwrap().status,
            ScopedStatus::Failed
        );
        assert!(controller.device.effects.is_empty());

        controller.power = Power::PluggedIn;
        controller.device.admitted = true;
        controller.device.power_mode.transaction = u8::MAX - 4;
        assert_eq!(
            controller.apply(enable).unwrap().status,
            ScopedStatus::Blocked
        );
        assert_eq!(controller.device.power_mode.transaction, u8::MAX);
        assert_eq!(controller.device.observed.temporary, Some(false));
        assert!(controller.pending.is_none());
        assert!(
            !controller
                .device
                .effects
                .iter()
                .any(|effect| matches!(effect, Effect::Set(_)))
        );
    }

    #[test]
    fn full_charge_reconciliation_requires_both_reads_and_never_replays() {
        let mut controller = setup();
        controller.device.report_reads = true;
        let enable = Intent::FullCharge(true);
        let cancel = Intent::FullCharge(false);
        let confirmed = |limit, temporary| Some(Confirmed { limit, temporary });
        let current = Some(limit(80));
        let command = Command::FullCharge {
            enabled: true,
            limit: limit(80),
        };

        for baseline in [
            None,
            confirmed(current, None),
            confirmed(None, Some(false)),
            confirmed(Some(limit(77)), Some(false)),
        ] {
            controller.device.read_override = Some(baseline);
            assert_eq!(
                controller.apply(enable).unwrap().status,
                ScopedStatus::Blocked
            );
        }
        assert!(
            !controller
                .device
                .effects
                .iter()
                .any(|effect| matches!(effect, Effect::Set(_)))
        );
        assert_eq!(
            policy::confirm(
                command,
                confirmed(Some(limit(75)), Some(true)),
                CAPABILITIES
            ),
            Confirmation::Mismatch
        );
        assert_eq!(
            policy::confirm(command, confirmed(None, Some(true)), CAPABILITIES),
            Confirmation::Mismatch
        );
        assert_eq!(
            policy::confirm(command, confirmed(current, None), CAPABILITIES),
            Confirmation::Unavailable
        );

        controller.device.power_mode.other_settings = 0x08;
        assert_eq!(
            controller.apply(enable).unwrap().status,
            ScopedStatus::Blocked
        );
        controller.device.power_mode.other_settings = 0;
        controller.device.next_submission =
            Some(Submission::Failed(SubmissionCertainty::Indeterminate));
        assert_eq!(
            controller.apply(enable).unwrap().status,
            ScopedStatus::Failed
        );
        controller.device.power_mode.unreadable = true;
        controller.observe();
        assert_eq!(controller.confirmed, confirmed(current, None));
        assert!(!controller.full_charge);
        assert!(controller.pending.is_some());
        assert_eq!(
            controller.apply(cancel).unwrap().status,
            ScopedStatus::Blocked
        );

        controller.device.power_mode.unreadable = false;
        controller.observe();
        assert!(controller.pending.is_none());
        assert!(controller.full_charge);
        controller.device.observed.temporary = Some(false);
        controller.observe();
        assert!(!controller.full_charge);
        assert!(!controller.external);
        assert_eq!(
            controller.apply(Intent::Profile).unwrap().status,
            ScopedStatus::Unchanged
        );
        assert_eq!(
            controller
                .device
                .effects
                .iter()
                .filter_map(|effect| match effect {
                    Effect::Set(command) => Some(*command),
                    _ => None,
                })
                .collect::<Vec<_>>(),
            [command]
        );
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

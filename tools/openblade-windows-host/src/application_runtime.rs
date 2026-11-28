//! Private Phase 5m replay. Report evidence only selects intent; this
//! scenario has no device adapter, profile writer, or process observer.

use std::{
    future::pending,
    io,
    path::Path,
    sync::{Arc, Mutex},
    time::Duration,
};

use openblade_domain::application_context::{ApplicationContext, Rule};
use openblade_domain::automation::{self, Provenance, ValueOrigin};
use serde::Deserialize;
use serde_json::{Value, json};
use tokio::{
    sync::{Notify, mpsc, watch},
    time::{Instant, sleep_until},
};

const LEASE: Duration = Duration::from_secs(15);
const PATH: &str = r"C:\Games\Game.exe";
const ID: &str = "e4c7ac58-003c-4f40-adb3-76f2d9e6ea9e";

pub(crate) const STALE: &str = "A stale application context was ignored.";

pub(crate) const INACTIVE: &str =
    "Session automation reports require the active local interactive Windows session.";

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub(crate) struct Report {
    #[serde(rename = "operation")]
    _operation: String,
    #[serde(rename = "version")]
    _version: i32,
    #[serde(rename = "requestId")]
    _request_id: String,
    instance_id: String,
    sequence: i64,
    // Match the managed required constructor parameter; only explicit null resets.
    #[serde(deserialize_with = "Deserialize::deserialize")]
    executable_path: Option<String>,
}

impl Report {
    pub(crate) fn decode(bytes: &[u8]) -> io::Result<Self> {
        let invalid = || io::Error::new(io::ErrorKind::InvalidData, "invalid application context");
        let mut report: Self = serde_json::from_slice(bytes).map_err(|_| invalid())?;
        if !crate::wire::is_guid(&report.instance_id)
            || report.sequence <= 0
            || report
                .executable_path
                .as_deref()
                .is_some_and(|path| !valid_path(path))
        {
            return Err(invalid());
        }
        report.instance_id.make_ascii_lowercase();
        Ok(report)
    }
}

fn valid_path(path: &str) -> bool {
    // The managed wire accepts only the canonical spelling, not an alias
    // that happens to normalize to the saved executable. Do not touch disk.
    if path.trim() != path
        || path.encode_utf16().count() > 32_767
        || path.chars().any(char::is_control)
        || !Path::new(path).is_absolute()
        || !path
            .as_bytes()
            .last_chunk::<4>()
            .is_some_and(|ext| ext.eq_ignore_ascii_case(b".exe"))
    {
        return false;
    }
    std::path::absolute(path).is_ok_and(|absolute| absolute.as_os_str() == path)
}

#[allow(unsafe_code)]
pub(crate) fn same_path(left: &str, right: &str) -> bool {
    use windows_sys::Win32::Globalization::CompareStringOrdinal;

    let left: Vec<u16> = left.encode_utf16().collect();
    let right: Vec<u16> = right.encode_utf16().collect();
    // SAFETY: both slices live for the call and the validated lengths fit i32.
    unsafe {
        CompareStringOrdinal(
            left.as_ptr(),
            left.len() as i32,
            right.as_ptr(),
            right.len() as i32,
            1,
        ) == 2 // CSTR_EQUAL
    }
}

#[derive(Clone, Copy)]
pub(crate) enum ReportEffect {
    Stale,
    Idle,
    Changed,
    Renewed,
}

impl ReportEffect {
    pub(crate) fn message(self) -> &'static str {
        match self {
            Self::Stale => STALE,
            Self::Idle => "No application profile is active.",
            Self::Changed => "The active application profile was recorded.",
            Self::Renewed => "The active application profile lease was renewed.",
        }
    }

    pub(crate) fn updates_status(self) -> bool {
        matches!(self, Self::Changed | Self::Renewed)
    }
}

#[derive(Default)]
pub(crate) struct Lease {
    last: Option<(String, i64)>,
    deadline: Option<Instant>,
    /// Managed's active pair, kept by the domain's shared selection rules.
    context: ApplicationContext<String>,
}

impl Lease {
    pub(crate) fn deadline(&self) -> Option<Instant> {
        self.deadline
    }

    pub(crate) fn report(
        &mut self,
        rules: &[Value],
        report: &Report,
        now: Instant,
    ) -> ReportEffect {
        if !self.accept(report) {
            return ReportEffect::Stale;
        }
        let changed = self.context.report(
            rules.iter().filter_map(rule),
            report.executable_path.as_deref(),
            same_path,
        );
        let active = self.context.active().is_some();
        if !changed && !active {
            return ReportEffect::Idle;
        }
        self.deadline = active.then(|| now + LEASE);
        if changed {
            ReportEffect::Changed
        } else {
            ReportEffect::Renewed
        }
    }

    pub(crate) fn expire(&mut self, expected: Instant, now: Instant) -> bool {
        if self.deadline != Some(expected) || now < expected {
            return false;
        }
        self.clear_selection();
        true
    }

    fn clear_selection(&mut self) {
        self.deadline = None;
        self.context.clear();
    }

    /// Managed `UpdateProfiles`: a changed rule set retires the selection
    /// unless an enabled rule with its id still holds the reported path.
    fn update(&mut self, rules: &[Value]) {
        if self
            .context
            .update(rules.iter().filter_map(rule), same_path)
        {
            self.deadline = None;
        }
    }

    /// The rule whose overrides apply, found as managed `GetApplicationOverride`
    /// finds it. Managed trusts the id because `UpdateProfiles` prunes the pair
    /// on every save; a caller here may change the rules without [`Self::update`],
    /// so the pair must still be retained, or no rule's overrides apply.
    pub(crate) fn rule<'a>(&self, rules: &'a [Value]) -> Option<&'a Value> {
        let active = self
            .context
            .retained(rules.iter().filter_map(rule), same_path)?;
        rules
            .iter()
            .find(|value| rule(value).is_some_and(|rule| rule.supplies(active.id)))
    }

    fn accept(&mut self, report: &Report) -> bool {
        // Only the current instance owns a sequence watermark, as in the
        // managed service. A different instance is allowed to start at one.
        if self.last.as_ref().is_some_and(|(instance, sequence)| {
            *instance == report.instance_id && report.sequence <= *sequence
        }) {
            return false;
        }
        self.last = Some((report.instance_id.clone(), report.sequence));
        true
    }
}

/// A fixture rule as the shared selection reads it; a malformed one selects nothing.
fn rule(value: &Value) -> Option<Rule<'_>> {
    Some(Rule {
        id: value["id"].as_str()?,
        executable_path: value["executablePath"].as_str()?,
        enabled: value["enabled"].as_bool() == Some(true),
    })
}

struct State {
    status: Value,
    lease: Lease,
    active_session: bool,
    battery: bool,
    manual: bool,
}

pub(crate) fn rules(profiles: &Value) -> &[Value] {
    profiles["applicationProfiles"]
        .as_array()
        .expect("fixture application rules")
}

pub(crate) fn install_fixture_rule(profiles: &mut Value) {
    // Use the full managed v91 profile shape so a battery-only update can round-trip
    // the immutable fixture rule without writing it to the profile aggregate.
    let battery = json!({
        "performance": 4, "customPerformance": null, "gpuClockTuning": null,
        "fans": null, "fanControl": null, "lighting": null, "display": null,
        "gamingModeEnabled": null, "gamingModeKeyPolicy": null, "isConfigured": true
    });
    let mut plugged_in = battery.clone();
    plugged_in["performance"] = 2.into();
    plugged_in["gamingModeEnabled"] = true.into();
    plugged_in["gamingModeKeyPolicy"] = json!({"blockWindowsKey": true, "blockAltTab": true});
    profiles["applicationProfiles"] = json!([{
        "id": ID, "name": "Scripted game", "executablePath": PATH,
        // The agent owns WhileRunningAfterActivation observation.
        "activationMode": 1, "enabled": true,
        "overrides": {"pluggedIn": plugged_in, "battery": battery, "usbC": null, "isConfigured": true}
    }]);
}

pub(crate) fn project_behavior(mut behavior: Value, overlay: &Value) -> Value {
    if behavior.is_null() {
        behavior = json!({
            "startupAnimationEnabled": null, "functionKeyPrimary": null,
            "gamingModeEnabled": null, "gamingModeKeyPolicy": null, "isConfigured": false
        });
    }
    for field in ["gamingModeEnabled", "gamingModeKeyPolicy"] {
        if let Some(value) = overlay.get(field).filter(|value| !value.is_null()) {
            behavior[field] = value.clone();
            behavior["isConfigured"] = true.into();
        }
    }
    behavior
}

impl State {
    fn new(mut status: Value) -> Self {
        install_fixture_rule(&mut status["profiles"]);
        let mut state = Self {
            status,
            lease: Lease::default(),
            active_session: true,
            battery: false,
            manual: false,
        };
        state.project("Synthetic application context ready.");
        state
    }

    fn report(&mut self, report: &Report, now: Instant) -> (i32, &'static str) {
        if !self.active_session {
            return (
                2,
                "Application context requires the active local Windows session.",
            );
        }
        let effect = self
            .lease
            .report(rules(&self.status["profiles"]), report, now);
        if effect.updates_status() {
            self.project(match effect {
                ReportEffect::Changed => "The active application profile changed.",
                _ => effect.message(),
            });
        }
        (0, effect.message())
    }

    fn expire(&mut self, expected: Instant, now: Instant) {
        if self.lease.expire(expected, now) {
            self.project(
                "The application profile lease expired; the current power profile is active.",
            );
        }
    }

    fn selected_rule(&self) -> Option<&Value> {
        self.lease.rule(rules(&self.status["profiles"]))
    }

    fn invalidate_changed_rule(&mut self) {
        self.lease.update(rules(&self.status["profiles"]));
    }

    fn project(&mut self, message: &'static str) {
        let (key, source) = if self.battery {
            ("battery", 1)
        } else {
            ("pluggedIn", 0)
        };
        let base = &self.status["profiles"][key];
        let app = self
            .selected_rule()
            .map_or(&Value::Null, |rule| &rule["overrides"][key]);
        let app_performance = app
            .get("performance")
            .filter(|value| !value.is_null())
            .cloned();
        let persistent = self.manual && automation::performance_source_eligible(1, source);
        let selection = automation::performance(false, persistent, app_performance.is_some());
        let performance = match selection.origin {
            ValueOrigin::Persistent => json!(1),
            _ => app_performance.unwrap_or_else(|| base["performance"].clone()),
        };
        let provenance = match selection.provenance {
            Provenance::PersistentOverride => 4,
            Provenance::ApplicationProfile => 2,
            _ => 1,
        };

        let behavior = project_behavior(base["laptopBehavior"].clone(), app);

        self.status["activePowerSource"] = source.into();
        self.status["controlAccess"] = i32::from(!self.active_session).into();
        self.status["activeApplicationProfileId"] = self
            .selected_rule()
            .map_or(Value::Null, |rule| rule["id"].clone());
        self.status["effectiveLaptopBehavior"] = behavior;
        self.status["performanceOverride"] = if self.manual {
            json!({"value": 1, "lifetime": 1, "customPerformance": null, "gpuClockTuning": null})
        } else {
            Value::Null
        };
        self.status["desiredState"]["performance"] =
            json!({"value": performance, "source": provenance});
        self.status["message"] = message.into();
    }

    fn step(&mut self, step: usize) {
        let message = match step {
            0 => {
                self.active_session = false;
                "Scripted session no longer controls Windows."
            }
            1 => {
                self.active_session = true;
                "Scripted session controls Windows again."
            }
            2 => {
                self.manual = true;
                "Scripted persistent override recorded."
            }
            3 => {
                self.battery = true;
                "Scripted battery power selected."
            }
            4 => {
                self.status["profiles"]["applicationProfiles"][0]["enabled"] = false.into();
                self.invalidate_changed_rule();
                "Scripted application rule disabled."
            }
            5 => {
                self.status["profiles"]["applicationProfiles"][0]["enabled"] = true.into();
                "Scripted application rule enabled."
            }
            _ => return,
        };
        self.project(message);
    }
}

pub(crate) struct Scenario {
    state: Mutex<State>,
    publication: watch::Sender<Arc<Value>>,
    deadline_changed: Notify,
}

impl Scenario {
    pub(crate) fn new(status: Value) -> Self {
        let state = State::new(status);
        let (publication, _) = watch::channel(Arc::new(state.status.clone()));
        Self {
            state: Mutex::new(state),
            publication,
            deadline_changed: Notify::new(),
        }
    }

    pub(crate) fn subscribe(&self) -> watch::Receiver<Arc<Value>> {
        self.publication.subscribe()
    }

    pub(crate) fn active(&self) -> bool {
        self.state.lock().expect("application state").active_session
    }

    pub(crate) fn report(&self, report: &Report) -> (i32, &'static str) {
        self.update(|state| state.report(report, Instant::now()))
    }

    fn update<T>(&self, operation: impl FnOnce(&mut State) -> T) -> T {
        let mut state = self.state.lock().expect("application state");
        let deadline = state.lease.deadline();
        let result = operation(&mut state);
        if state.lease.deadline() != deadline {
            self.deadline_changed.notify_one();
        }
        if *self.publication.borrow().as_ref() != state.status {
            let revision = state.status["revision"].as_i64().expect("fixture revision");
            state.status["revision"] = (revision + 1).into();
            self.publication
                .send_replace(Arc::new(state.status.clone()));
        }
        result
    }

    pub(crate) async fn drive(&self, mut advance: mpsc::Receiver<()>) -> io::Result<()> {
        let mut step = 0;
        loop {
            let deadline = self
                .state
                .lock()
                .expect("application state")
                .lease
                .deadline();
            tokio::select! {
                biased;
                () = async {
                    match deadline {
                        Some(at) => sleep_until(at).await,
                        None => pending().await,
                    }
                } => self.update(|state| state.expire(deadline.expect("elapsed deadline"), Instant::now())),
                () = self.deadline_changed.notified() => {},
                next = advance.recv() => {
                    if next.is_none() { return Ok(()); }
                    self.update(|state| state.step(step));
                    step = step.saturating_add(1);
                }
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn report(instance: &str, sequence: i64, path: Option<&str>) -> Report {
        Report {
            _operation: String::new(),
            _version: 91,
            _request_id: String::new(),
            instance_id: instance.into(),
            sequence,
            executable_path: path.map(str::to_owned),
        }
    }

    #[test]
    fn renewal_replaces_deadline_and_expiry_keeps_sequence_watermark() {
        let mut state = State::new(
            serde_json::from_str::<Value>(crate::wire::CURRENT_FIXTURE).unwrap()["status"].clone(),
        );
        let now = Instant::now();
        state.report(&report(ID, 1, Some(PATH)), now);
        let first = state.lease.deadline().unwrap();
        let renewed = now + Duration::from_secs(5);
        assert_eq!(
            state.report(&report(ID, 2, Some(PATH)), renewed).1,
            ReportEffect::Renewed.message()
        );
        assert_eq!(state.lease.deadline(), Some(renewed + LEASE));
        state.expire(first, first);
        assert!(state.selected_rule().is_some());
        let second = state.lease.deadline().unwrap();
        state.expire(second, second - Duration::from_millis(1));
        assert!(state.selected_rule().is_some());
        state.expire(second, second);
        assert!(state.selected_rule().is_none());
        assert_eq!(
            state.report(&report(ID, 2, Some(PATH)), second).1,
            "A stale application context was ignored."
        );
        state.report(&report(ID, 3, Some(PATH)), second);
        assert!(state.selected_rule().is_some());
    }

    #[test]
    fn a_profile_update_keeps_a_recased_rule_and_drops_a_disabled_one() {
        let mut state = State::new(
            serde_json::from_str::<Value>(crate::wire::CURRENT_FIXTURE).unwrap()["status"].clone(),
        );
        let now = Instant::now();
        state.report(&report(ID, 1, Some(PATH)), now);
        // Managed `UpdateProfiles` compares the reported path ignoring case,
        // so a recased rule keeps its selection and the next report renews it.
        state.status["profiles"]["applicationProfiles"][0]["executablePath"] =
            PATH.to_ascii_uppercase().into();
        state.invalidate_changed_rule();
        assert!(state.lease.deadline().is_some());
        assert_eq!(
            state.report(&report(ID, 2, Some(PATH)), now).1,
            ReportEffect::Renewed.message()
        );
        state.step(4);
        assert!(state.selected_rule().is_none());
        assert_eq!(state.lease.deadline(), None);
        // Re-enabling the rule does not select it; the next report does.
        state.step(5);
        assert!(state.selected_rule().is_none());
        assert_eq!(
            state.report(&report(ID, 3, Some(PATH)), now).1,
            ReportEffect::Changed.message()
        );
    }

    #[test]
    fn a_rule_changed_without_an_update_supplies_no_overrides() {
        let mut state = State::new(
            serde_json::from_str::<Value>(crate::wire::CURRENT_FIXTURE).unwrap()["status"].clone(),
        );
        state.report(&report(ID, 1, Some(PATH)), Instant::now());
        assert!(state.selected_rule().is_some());
        // A writer that skipped `Lease::update` cannot hand the active id's
        // overrides to a rule that no longer holds the reported path.
        let rule = &mut state.status["profiles"]["applicationProfiles"][0];
        rule["executablePath"] = r"C:\Games\Other.exe".into();
        assert!(state.selected_rule().is_none());
        let rule = &mut state.status["profiles"]["applicationProfiles"][0];
        rule["executablePath"] = PATH.into();
        rule["enabled"] = false.into();
        assert!(state.selected_rule().is_none());
    }

    #[test]
    fn authority_rechecked_after_initial_admission_without_consuming_sequence() {
        let status =
            serde_json::from_str::<Value>(crate::wire::CURRENT_FIXTURE).unwrap()["status"].clone();
        let scenario = Scenario::new(status);
        assert!(scenario.active());
        scenario.update(|state| state.step(0));
        let report = report(ID, 1, Some(PATH));
        assert_eq!(scenario.report(&report).0, 2);
        scenario.update(|state| state.step(1));
        assert_eq!(
            scenario.report(&report).1,
            "The active application profile was recorded."
        );
    }

    #[test]
    fn rejects_missing_reset_field_duplicate_members_and_bad_sequences() {
        let reset = json!({
            "operation": "reportApplicationContext", "version": 91, "requestId": ID,
            "instanceId": ID.to_ascii_uppercase(), "sequence": 1, "executablePath": null
        });
        let accepted = Report::decode(&serde_json::to_vec(&reset).unwrap()).unwrap();
        assert_eq!(accepted.instance_id, ID);
        assert!(accepted.executable_path.is_none());
        let duplicate =
            reset
                .to_string()
                .replacen(r#""sequence":1"#, r#""sequence":1,"sequence":2"#, 1);
        assert!(Report::decode(duplicate.as_bytes()).is_err());

        for (field, value) in [
            ("sequence", json!(0)),
            ("sequence", json!(-1)),
            ("sequence", json!(i64::MAX as u64 + 1)),
            ("instanceId", json!("00000000-0000-0000-0000-000000000000")),
            ("executablePath", json!(r"C:\Games\..\Game.exe")),
        ] {
            let mut malformed = reset.clone();
            malformed[field] = value;
            assert!(Report::decode(&serde_json::to_vec(&malformed).unwrap()).is_err());
        }
        let mut missing = reset;
        missing.as_object_mut().unwrap().remove("executablePath");
        assert!(Report::decode(&serde_json::to_vec(&missing).unwrap()).is_err());
    }

    #[test]
    fn requires_one_canonical_fully_qualified_executable() {
        for valid in [
            PATH,
            r"c:\games\GAME.EXE",
            r"\\host\share\é.exe",
            r"C:\Games\.exe",
        ] {
            assert!(valid_path(valid), "{valid:?}");
        }
        for invalid in [
            "Game.exe",
            r"C:Game.exe",
            r"\Games\Game.exe",
            r"C:/Games/Game.exe",
            r"C:\Games\..\Game.exe",
            r"C:\Games\.\Game.exe",
            r"C:\Games\\Game.exe",
            r"C:\Games\Game.exe\",
            r"C:\Games\Game.exe ",
            "C:\\Games\\Game\n.exe",
            r"C:\Games\Game.exe:stream",
            r"C:\Games\Game.txt",
        ] {
            assert!(!valid_path(invalid), "{invalid:?}");
        }
        assert!(same_path(r"C:\Games\É.exe", r"c:\games\é.exe"));
        assert!(!same_path(PATH, r"D:\Games\Game.exe"));
    }
}

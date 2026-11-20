//! Compatible saved intent and durable per-submission fence. Constructors take
//! explicit storage first; the injected path never resolves a product root.
use crate::configuration::{self, File as ConfigFile, Inspection, RecoveryRecord};
use openblade_domain::{
    automation::{UNCLASSIFIED_EXTERNAL, temporary_after, temporary_expired},
    thermal_mutation::{Preset, Progress, progress_follows, settled},
};
use serde_json::{Value, json};

use std::{
    io,
    sync::{Arc, Mutex},
};

#[path = "battery_storage.rs"]
mod battery;
pub(super) use battery::TemporaryRelease;

pub(super) const RELEASE_PAUSED: &str = "Temporary charge journal release is deferred.";
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(super) enum ReleasePause {
    Busy,
    ReadUnavailable,
    Reconciliation(&'static str),
}
impl ReleasePause {
    pub(super) fn message(self) -> String {
        let reason = match self {
            Self::Busy => {
                "New mutations are paused until active work settles; no hardware command will be replayed."
            }
            Self::ReadUnavailable => {
                "New mutations are paused while a read-only check is retried; no hardware command will be replayed."
            }
            Self::Reconciliation(reason) => reason,
        };
        format!("{RELEASE_PAUSED} {reason}")
    }
    pub(super) fn compose(self, message: &str) -> String {
        // An unresolved settlement is the authoritative public reason. Keep
        // it equal to the owned pending outcome so existing typed rendering
        // emits it once; generic release wording must not demote that cause.
        match self {
            Self::Reconciliation(reason) => reason.into(),
            _ => format!("{message} {}", self.message()).trim().to_owned(),
        }
    }
}
#[derive(Debug)]
pub(super) struct ReleaseDeferred {
    pub(super) cause: ReleasePause,
    source: Option<io::Error>,
}
impl ReleaseDeferred {
    pub(super) fn busy() -> Self {
        Self {
            cause: ReleasePause::Busy,
            source: None,
        }
    }
    pub(super) fn reconciliation(reason: &'static str) -> Self {
        Self {
            cause: ReleasePause::Reconciliation(reason),
            source: None,
        }
    }
    pub(super) fn unreadable(error: io::Error) -> Self {
        Self {
            cause: ReleasePause::ReadUnavailable,
            source: Some(error),
        }
    }
    pub(super) fn into_error(self) -> io::Error {
        io::Error::new(io::ErrorKind::WouldBlock, self)
    }
}
impl std::fmt::Display for ReleaseDeferred {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(&self.cause.message())
    }
}
impl std::error::Error for ReleaseDeferred {
    fn source(&self) -> Option<&(dyn std::error::Error + 'static)> {
        self.source.as_ref().map(|e| e as _)
    }
}
pub(super) fn is_release_deferred(error: &io::Error) -> bool {
    error.get_ref().is_some_and(|e| e.is::<ReleaseDeferred>())
}
// Created only at the raw open/read operations in the release-only adapter.
// Trust, custody, parsing, metadata and aggregate I/O errors are never tagged.
#[derive(Debug)]
struct ReleaseReadUnavailable(io::Error);
impl std::fmt::Display for ReleaseReadUnavailable {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        self.0.fmt(f)
    }
}
impl std::error::Error for ReleaseReadUnavailable {
    fn source(&self) -> Option<&(dyn std::error::Error + 'static)> {
        Some(&self.0)
    }
}
pub(super) fn release_open_error(error: io::Error) -> io::Error {
    if error.raw_os_error() == Some(windows_sys::Win32::Foundation::ERROR_SHARING_VIOLATION as i32)
    {
        io::Error::new(io::ErrorKind::WouldBlock, ReleaseReadUnavailable(error))
    } else {
        error
    }
}
pub(super) fn release_bytes_error(error: io::Error) -> io::Error {
    if error.raw_os_error() == Some(windows_sys::Win32::Foundation::ERROR_LOCK_VIOLATION as i32) {
        io::Error::new(io::ErrorKind::WouldBlock, ReleaseReadUnavailable(error))
    } else {
        error
    }
}
fn is_release_read_unavailable(error: &io::Error) -> bool {
    error
        .get_ref()
        .is_some_and(|e| e.is::<ReleaseReadUnavailable>())
}
const OVERRIDES: &str = "manual-overrides.json";
const JOURNAL: &str = "thermal-mutation.journal";
pub(super) trait Files: Send + Sync {
    fn read(&self, name: &str) -> io::Result<Option<Vec<u8>>>;
    fn read_release(&self, _name: &str) -> io::Result<Option<Vec<u8>>> {
        Err(denied())
    }
    fn reserve(&self, expected: Option<&[u8]>, needed: usize) -> io::Result<Option<Vec<u8>>> {
        if needed > crate::thermal_history::SEGMENT
            || expected
                .unwrap_or_default()
                .len()
                .checked_add(needed)
                .is_none_or(|n| n > crate::thermal_history::SEGMENT)
        {
            return Err(io::Error::new(
                io::ErrorKind::StorageFull,
                "Thermal progress capacity unavailable before Prepared.",
            ));
        }
        Ok(expected.map(<[u8]>::to_vec))
    }
    fn replace(&self, name: &str, expected: Option<&[u8]>, bytes: &[u8]) -> io::Result<()>;
    fn replace_cleanup(&self, name: &str, expected: Option<&[u8]>, bytes: &[u8]) -> io::Result<()> {
        self.replace(name, expected, bytes)
    }
    fn replace_retired(&self, name: &str, expected: Option<&[u8]>, bytes: &[u8]) -> io::Result<()> {
        self.replace(name, expected, bytes)
    }
}
fn denied() -> io::Error {
    io::Error::new(
        io::ErrorKind::PermissionDenied,
        "thermal configuration or submission journal is quarantined",
    )
}
fn decode(bytes: &[u8]) -> io::Result<Value> {
    configuration::validate_json_members(bytes).map_err(io::Error::other)?;
    serde_json::from_slice(bytes).map_err(io::Error::other)
}
// Managed PersistentOverrideStore.CurrentSchemaVersion. Schema 9 added keyboard
// effect control and schema 10 lighting zone controls; schema 8 is the oldest
// document whose fan details this bounded reader accepts without migration.
const OVERRIDE_SCHEMA: i64 = 10;
const OVERRIDE_KEYBOARD_EFFECT_SCHEMA: i64 = 9;
const OVERRIDE_FAN_DETAIL_SCHEMA: i64 = 8;
fn overrides(bytes: Option<&[u8]>) -> io::Result<Value> {
    let mut document = match bytes {
        Some(bytes) => decode(bytes)?,
        None => {
            json!({"schemaVersion":OVERRIDE_SCHEMA,"overrides":{"performance":null,"fans":null,"lighting":null,"customPerformance":null,"fanControl":null,"gpuClockTuning":null}})
        }
    };
    let root = document.as_object_mut().ok_or_else(denied)?;
    if root.len() != 2
        || !matches!(
            root.get("schemaVersion").and_then(Value::as_i64),
            Some(1..=OVERRIDE_SCHEMA)
        )
    {
        return Err(denied());
    }
    let schema = root
        .get("schemaVersion")
        .and_then(Value::as_i64)
        .ok_or_else(denied)?;
    // Managed rejects these before typed migration, so the document is invalid
    // rather than preserved for later migration.
    if configuration::legacy_lighting_intent(
        &Value::Object(root.clone()),
        schema < OVERRIDE_SCHEMA,
        schema < OVERRIDE_KEYBOARD_EFFECT_SCHEMA,
    ) {
        return Err(denied());
    }
    if schema < OVERRIDE_FAN_DETAIL_SCHEMA {
        return Err(io::Error::new(
            io::ErrorKind::Unsupported,
            "Legacy overrides require managed migration; original bytes are preserved.",
        ));
    }
    let value = root
        .get_mut("overrides")
        .and_then(Value::as_object_mut)
        .ok_or_else(denied)?;
    if value.keys().any(|key| {
        ![
            "performance",
            "fans",
            "lighting",
            "customPerformance",
            "fanControl",
            "gpuClockTuning",
        ]
        .contains(&key.as_str())
    }) {
        return Err(denied());
    }
    for key in ["performance", "fans", "lighting"] {
        if !value.contains_key(key) {
            return Err(denied());
        }
    }
    if !value["performance"].is_null() && !matches!(value["performance"].as_i64(), Some(0..=9)) {
        return Err(denied());
    }
    if !value["fans"].is_null() && !matches!(value["fans"].as_i64(), Some(0..=3)) {
        return Err(denied());
    }
    for key in [
        "lighting",
        "customPerformance",
        "fanControl",
        "gpuClockTuning",
    ] {
        if value
            .get(key)
            .is_some_and(|v| !v.is_null() && !v.is_object())
        {
            return Err(denied());
        }
    }
    if value["performance"].as_i64() != Some(5)
        && value.get("customPerformance").is_some_and(|v| !v.is_null())
    {
        return Err(denied());
    }
    // Preserve all unrelated fields. Legacy fan detail normalization is the
    // managed FromLegacy rule; never replace a supplied manual curve/target.
    if let Some(mode) = value["fans"].as_i64() {
        if value.get("fanControl").is_none_or(Value::is_null) {
            return Err(io::Error::new(
                io::ErrorKind::Unsupported,
                "Legacy fan details require managed normalization; original bytes are preserved.",
            ));
        } else if value["fanControl"]["mode"].as_i64() != Some(mode) {
            return Err(denied());
        }
    } else if value.get("fanControl").is_some_and(|v| !v.is_null()) {
        return Err(denied());
    }
    if let Some(v) = value.get("gpuClockTuning").filter(|v| !v.is_null()) {
        validate_gpu(v)?;
    }
    if let Some(v) = value.get("customPerformance").filter(|v| !v.is_null()) {
        members(v, &["cpu", "gpu", "maximumFans", "gpuClockOffsets"])?;
        if !matches!(v["cpu"].as_i64(), Some(0..=3))
            || !matches!(v["gpu"].as_i64(), Some(0..=2))
            || v.get("maximumFans").is_some_and(|v| !v.is_boolean())
        {
            return Err(denied());
        }
        if !v["gpuClockOffsets"].is_null() {
            offsets(&v["gpuClockOffsets"])?;
            if value.get("gpuClockTuning").is_some_and(|v| !v.is_null()) {
                return Err(denied());
            }
        }
    } else if value["performance"].as_i64() == Some(5) {
        return Err(denied());
    }
    if let Some(v) = value.get("fanControl").filter(|v| !v.is_null()) {
        fan_control(v)?;
    }
    // Schema 9 independent-intent preservation rewrites only non-null lighting.
    if !value["lighting"].is_null() {
        return Err(io::Error::new(
            io::ErrorKind::Unsupported,
            "Saved lighting override validation is outside this bounded composition; bytes are preserved.",
        ));
    }
    Ok(document)
}
fn members(v: &Value, allowed: &[&str]) -> io::Result<()> {
    if v.as_object()
        .is_none_or(|o| o.keys().any(|k| !allowed.contains(&k.as_str())))
    {
        Err(denied())
    } else {
        Ok(())
    }
}
fn offsets(v: &Value) -> io::Result<()> {
    members(v, &["coreMegahertz", "memoryMegahertz"])?;
    if ["coreMegahertz", "memoryMegahertz"]
        .iter()
        .any(|k| !matches!(v[k].as_i64(), Some(-10000..=10000)))
    {
        return Err(denied());
    }
    Ok(())
}
fn validate_gpu(v: &Value) -> io::Result<()> {
    crate::service_status::validate_gpu_tuning(v)
}
fn fan_control(v: &Value) -> io::Result<()> {
    members(v, &["mode", "fixedSpeedRpm", "manualCurve"])?;
    // Managed FanControlSettings.ValidateSpeed admits MaximumSupportedSpeedRpm
    // in stored intent for every model. Exact-model write admission remains
    // fan_history::validate_settings and the fan recipe, both at 5400.
    let speed = |v: &Value| matches!(v.as_i64(),Some(n) if (2000..=5500).contains(&n)&&n%100==0);
    if !matches!(v["mode"].as_i64(), Some(0..=3)) || !speed(&v["fixedSpeedRpm"]) {
        return Err(denied());
    }
    let curve = &v["manualCurve"];
    members(curve, &["cpu", "gpu", "useHigherTemperatureForBoth"])?;
    if !curve["useHigherTemperatureForBoth"].is_boolean() {
        return Err(denied());
    }
    for key in ["cpu", "gpu"] {
        let points = curve[key].as_array().ok_or_else(denied)?;
        if !(2..=10).contains(&points.len()) {
            return Err(denied());
        }
        let mut previous = None;
        for p in points {
            members(p, &["temperatureCelsius", "speedRpm"])?;
            let t = p["temperatureCelsius"].as_i64().ok_or_else(denied)?;
            if !(20..=100).contains(&t) || !speed(&p["speedRpm"]) {
                return Err(denied());
            }
            let rpm = p["speedRpm"].as_i64().unwrap();
            if previous.is_some_and(|(pt, ps)| t <= pt || rpm < ps) {
                return Err(denied());
            }
            previous = Some((t, rpm));
        }
    }
    Ok(())
}

pub(super) use crate::service_status::PerformanceOverride as Override;
use crate::thermal_history::Entry;
struct State {
    quarantined: bool,
    configuration_failed: bool,
    configuration_changed: bool,
    preparation_failed: bool,
    intent_append_failed: bool,
    bytes: Option<Vec<u8>>,
    document: Value,
    journal: Option<Vec<u8>>,
    last: Option<Entry>,
    temporary: Option<(Override, i32)>,
    temporary_fan: Option<(Value, i32)>,
    // Lifecycle epochs for `automation::temporary_after`: the latest fresh
    // power reading's, and each temporary's at its save.
    epoch: Option<u64>,
    temporary_epoch: Option<u64>,
    temporary_fan_epoch: Option<u64>,
    battery_limit: Option<u8>,
    battery_protection: Option<(bool, u8)>,
    battery_temporary: Option<bool>,
    temporary_capacity: usize,
    temporary_capacity_origin: Option<openblade_domain::temporary_charge_history::Origin>,
}
pub(super) struct Storage {
    same_path: fn(&str, &str) -> bool,
    user_sid: String,
    files: Box<dyn Files>,
    state: Mutex<State>,
    #[cfg(test)]
    battery_append_hook: Mutex<Option<AppendHook>>,
}
#[cfg(test)]
type AppendHook = Arc<dyn Fn(&str) + Send + Sync>;
fn pending_reason_locked(s: &State) -> &'static str {
    if s.last
        .as_ref()
        .is_some_and(|entry| entry.progress >= 1 && !settled(entry.progress))
    {
        "A thermal command may have been submitted. Read-only reconciliation is required and no mutation will be replayed."
    } else if s.intent_append_failed {
        "Thermal submission-intent journal publication could not be confirmed. Pending or published journal evidence is retained. Read-only reconciliation is required and no mutation will be replayed."
    } else if s.preparation_failed {
        "Thermal journal preparation could not be confirmed; storage reconciliation is required. The failed preparation reached no submission intent or thermal setter. Pending or published journal evidence is retained."
    } else if s.last.as_ref().is_some_and(|entry| entry.progress == 0) {
        "Intent stored and journal prepared; no mutation submission intent was reached. Explicit read-only reconciliation is required."
    } else if s.configuration_changed {
        "Saved configuration changed unexpectedly; storage reconciliation is required. No thermal submission is established by this change."
    } else if s.configuration_failed {
        "Saved configuration replacement failed; storage reconciliation is required. No thermal submission is established."
    } else {
        "An earlier thermal submission requires read-only reconciliation."
    }
}
impl Storage {
    #[cfg(test)]
    pub(super) fn open(files: Box<dyn Files>, user_sid: &str) -> io::Result<Arc<Self>> {
        Self::open_with(files, user_sid, str::eq_ignore_ascii_case)
    }
    pub(super) fn open_with(
        files: Box<dyn Files>,
        user_sid: &str,
        same_path: fn(&str, &str) -> bool,
    ) -> io::Result<Arc<Self>> {
        if !user_sid.starts_with("S-1-5-21-")
            || !user_sid
                .bytes()
                .all(|b| b.is_ascii_digit() || b"S-".contains(&b))
        {
            return Err(denied());
        }
        for name in [
            "manual-overrides.json.pending",
            "thermal-mutation.journal.pending",
            "battery-configuration.commit.json.recovery-required",
        ] {
            if files.read(name)?.is_some() {
                return Err(denied());
            }
        }
        aggregate(files.as_ref(), same_path)?;
        let bytes = files.read(OVERRIDES)?;
        let document = overrides(bytes.as_deref())?;
        let journal = files.read(JOURNAL)?;
        let last =
            crate::thermal_history::rows(journal.as_deref().unwrap_or_default(), |_, _| Ok(()))?.1;
        Ok(Arc::new(Self {
            #[cfg(test)]
            battery_append_hook: Mutex::new(None),
            same_path,
            user_sid: user_sid.into(),
            files,
            state: Mutex::new(State {
                quarantined: false,
                configuration_failed: false,
                configuration_changed: false,
                preparation_failed: false,
                intent_append_failed: false,
                bytes,
                document,
                journal,
                last,
                temporary: None,
                temporary_fan: None,
                epoch: None,
                temporary_epoch: None,
                temporary_fan_epoch: None,
                battery_limit: None,
                battery_protection: None,
                battery_temporary: None,
                temporary_capacity: 0,
                temporary_capacity_origin: None,
            }),
        }))
    }
    pub(super) fn outstanding_fan(&self) -> bool {
        self.state.lock().map_or(true, |s| {
            !crate::thermal_history::handback_complete(s.journal.as_deref().unwrap_or_default())
                .unwrap_or(false)
        })
    }
    pub(super) fn pending(&self) -> bool {
        self.state.lock().map_or(true, |s| {
            s.quarantined || s.last.as_ref().is_some_and(|e| !settled(e.progress))
        })
    }
    pub(super) fn save(&self, value: Option<Override>, power: i32) -> io::Result<()> {
        if let Some(v) = &value {
            v.validate()?;
        }
        aggregate(self.files.as_ref(), self.same_path)?;
        let mut s = self.state.lock().map_err(|_| denied())?;
        if s.quarantined || s.last.as_ref().is_some_and(|e| !settled(e.progress)) {
            return Err(denied());
        }
        let mut next = s.document.clone();
        let saved = value.as_ref().filter(|v| v.lifetime == 1);
        next["overrides"]["performance"] = saved.map_or(Value::Null, |v| json!(v.value));
        next["overrides"]["customPerformance"] = Value::Null;
        next["overrides"]["gpuClockTuning"] = saved
            .and_then(|v| v.gpu_clock_tuning.clone())
            .unwrap_or(Value::Null);
        next["schemaVersion"] = json!(OVERRIDE_SCHEMA);
        let bytes = serde_json::to_vec(&next)?;
        // Accepted intent is published only after atomic protected replacement
        // and independent byte readback. Unrelated overrides remain intact.
        if let Err(error) = self.files.replace(OVERRIDES, s.bytes.as_deref(), &bytes) {
            s.quarantined = true;
            s.configuration_failed = true;
            return Err(error);
        }
        s.bytes = Some(bytes);
        s.document = next;
        s.temporary = value.filter(|v| v.lifetime == 0).map(|v| (v, power));
        s.temporary_epoch = s.epoch;
        Ok(())
    }
    /// A fresh power class read at lifecycle `epoch`: keeps, refines or retires
    /// both temporaries (`automation::temporary_after`) before any read.
    pub(super) fn observe_power(&self, power: i32, epoch: u64) -> io::Result<()> {
        let mut s = self.state.lock().map_err(|_| denied())?;
        s.epoch = Some(epoch);
        let s = &mut *s;
        s.temporary = s.temporary.take().and_then(|(value, saved)| {
            temporary_after(saved, s.temporary_epoch, power, epoch).map(|next| (value, next))
        });
        s.temporary_fan = s.temporary_fan.take().and_then(|(value, saved)| {
            temporary_after(saved, s.temporary_fan_epoch, power, epoch).map(|next| (value, next))
        });
        Ok(())
    }
    pub(super) fn desired(
        &self,
        power: i32,
        application: Option<&str>,
    ) -> io::Result<Option<(Preset, i32)>> {
        let mut s = self.state.lock().map_err(|_| denied())?;
        if s.temporary
            .as_ref()
            .is_some_and(|(_, p)| temporary_expired(*p, power))
        {
            s.temporary = None;
        }
        let persistent = s.document["overrides"]["performance"]
            .as_i64()
            .map(|n| n as i32);
        let temporary = s.temporary.as_ref().map(|(v, _)| v.value);
        for (value, source) in [(persistent, 4), (temporary, 3)] {
            if let Some(value) = value
                && openblade_domain::automation::override_source_eligible(value, power)
            {
                return Ok(Preset::from_wire(value).map(|p| (p, source)));
            }
        }
        drop(s);
        let Some(doc) = aggregate(self.files.as_ref(), self.same_path)? else {
            return Ok(None);
        };
        let profiles = &doc["profiles"];
        let key = match power {
            0 => "pluggedIn",
            1 => "battery",
            2 => "usbC",
            _ => return Ok(None),
        };
        let app = application.and_then(|path| {
            profiles["applicationProfiles"]
                .as_array()?
                .iter()
                .find(|p| {
                    p["enabled"].as_bool() == Some(true)
                        && p["executablePath"]
                            .as_str()
                            .is_some_and(|s| (self.same_path)(s, path))
                })
        });
        let selected = app
            .and_then(|a| a["overrides"][key]["performance"].as_i64())
            .or_else(|| profiles[key]["performance"].as_i64());
        Ok(selected
            .filter(|n| openblade_domain::automation::performance_source_eligible(*n as i32, power))
            .and_then(|n| Preset::from_wire(n as i32))
            .map(|p| {
                (
                    p,
                    if app.is_some_and(|a| a["overrides"][key]["performance"].as_i64().is_some()) {
                        2
                    } else {
                        1
                    },
                )
            }))
    }
    pub(super) fn gpu_incomplete(&self, power: i32, application: Option<&str>) -> io::Result<bool> {
        let mut s = self.state.lock().map_err(|_| denied())?;
        // The same class expiry as `desired`, so the answer does not depend on
        // which of the two a caller reads first.
        if s.temporary
            .as_ref()
            .is_some_and(|(_, p)| temporary_expired(*p, power))
        {
            s.temporary = None;
        }
        if !s.document["overrides"]["gpuClockTuning"].is_null()
            || s.temporary
                .as_ref()
                .is_some_and(|(v, _)| v.gpu_clock_tuning.is_some())
        {
            return Ok(true);
        }
        drop(s);
        let Some(doc) = aggregate(self.files.as_ref(), self.same_path)? else {
            return Ok(false);
        };
        let profiles = &doc["profiles"];
        let key = match power {
            0 => "pluggedIn",
            1 => "battery",
            2 => "usbC",
            // Unclassified external power selects no profile: only the
            // explicit overrides checked above can carry a GPU effect.
            UNCLASSIFIED_EXTERNAL => return Ok(false),
            _ => return Ok(true),
        };
        let app = application.and_then(|path| {
            profiles["applicationProfiles"]
                .as_array()?
                .iter()
                .find(|p| {
                    p["enabled"].as_bool() == Some(true)
                        && p["executablePath"]
                            .as_str()
                            .is_some_and(|s| (self.same_path)(s, path))
                })
        });
        Ok(!profiles[key]["gpuClockTuning"].is_null()
            || app.is_some_and(|a| !a["overrides"][key]["gpuClockTuning"].is_null()))
    }
    pub(super) fn pending_reason(&self) -> io::Result<&'static str> {
        let s = self.state.lock().map_err(|_| {
            super::thermal_mutation::fatal_admission("Thermal storage state is poisoned.")
        })?;
        Ok(pending_reason_locked(&s))
    }

    pub(super) fn quarantine_settlement(&self) -> io::Result<()> {
        self.state
            .lock()
            .map_err(|_| {
                super::thermal_mutation::fatal_admission("Thermal storage state is poisoned.")
            })?
            .quarantined = true;
        Ok(())
    }
    pub(super) fn configuration_failed(&self) -> bool {
        self.state.lock().map_or(true, |s| s.configuration_failed)
    }
    pub(super) fn current_override(&self, power: i32) -> io::Result<Option<Override>> {
        let mut s = self.state.lock().map_err(|_| denied())?;
        if self.files.read(OVERRIDES)? != s.bytes {
            s.quarantined = true;
            s.configuration_changed = true;
            return Err(denied());
        }
        if s.temporary
            .as_ref()
            .is_some_and(|(_, p)| temporary_expired(*p, power))
        {
            s.temporary = None;
        }
        if let Some((v, _)) = &s.temporary {
            return Ok(Some(v.clone()));
        }
        let saved = &s.document["overrides"];
        Ok(saved["performance"].as_i64().map(|n| Override {
            value: n as i32,
            lifetime: 1,
            custom_performance: saved
                .get("customPerformance")
                .filter(|v| !v.is_null())
                .cloned(),
            gpu_clock_tuning: saved
                .get("gpuClockTuning")
                .filter(|v| !v.is_null())
                .cloned(),
        }))
    }
    pub(super) fn save_fan(
        &self,
        value: Option<crate::service_status::FanOverride>,
        settings: Option<Value>,
        power: i32,
    ) -> io::Result<()> {
        openblade_domain::fan_history::validate_requested(
            &json!({"value":value,"settings":settings}),
        )?;
        aggregate(self.files.as_ref(), self.same_path)?;
        let mut s = self.state.lock().map_err(|_| denied())?;
        if s.quarantined || s.last.as_ref().is_some_and(|e| !settled(e.progress)) {
            return Err(denied());
        }
        let resolved = value.as_ref().map(|v| {
            let mut s = settings.unwrap_or_else(|| legacy_fan(v.value));
            if v.value == 3 {
                s["fixedSpeedRpm"] = json!(5400);
            }
            s
        });
        let saved = value.as_ref().is_some_and(|v| v.lifetime == 1);
        let mut next = s.document.clone();
        next["overrides"]["fans"] = if saved {
            json!(value.as_ref().unwrap().value)
        } else {
            Value::Null
        };
        next["overrides"]["fanControl"] = if saved {
            resolved.clone().unwrap()
        } else {
            Value::Null
        };
        next["schemaVersion"] = json!(OVERRIDE_SCHEMA);
        let bytes = serde_json::to_vec(&next)?;
        if let Err(e) = self.files.replace(OVERRIDES, s.bytes.as_deref(), &bytes) {
            s.quarantined = true;
            s.configuration_failed = true;
            return Err(e);
        }
        s.bytes = Some(bytes);
        s.document = next;
        s.temporary_fan = if !saved {
            resolved.map(|v| (v, power))
        } else {
            None
        };
        s.temporary_fan_epoch = s.epoch;
        Ok(())
    }
    pub(super) fn current_fan_override(
        &self,
        power: i32,
    ) -> io::Result<Option<crate::service_status::FanOverride>> {
        let s = self.state.lock().map_err(|_| denied())?;
        if let Some((v, p)) = &s.temporary_fan
            && !temporary_expired(*p, power)
        {
            return Ok(Some(crate::service_status::FanOverride {
                value: v["mode"].as_u64().ok_or_else(denied)? as u8,
                lifetime: 0,
            }));
        }
        Ok(s.document["overrides"]["fans"].as_u64().map(|mode| {
            crate::service_status::FanOverride {
                value: mode as u8,
                lifetime: 1,
            }
        }))
    }
    // Integrity remains mandatory for release; mutable policy equality is not.
    pub(super) fn verify_fan_configuration(&self) -> io::Result<()> {
        aggregate(self.files.as_ref(), self.same_path)?;
        let mut s = self.state.lock().map_err(|_| denied())?;
        if self.files.read(OVERRIDES)? != s.bytes {
            s.quarantined = true;
            s.configuration_changed = true;
        }
        if s.quarantined {
            return Err(denied());
        }
        Ok(())
    }
    pub(super) fn desired_fan(
        &self,
        power: i32,
        application: Option<&str>,
    ) -> io::Result<Option<Value>> {
        let mut s = self.state.lock().map_err(|_| denied())?;
        if self.files.read(OVERRIDES)? != s.bytes {
            s.quarantined = true;
            s.configuration_changed = true;
            return Err(denied());
        }
        if s.temporary_fan
            .as_ref()
            .is_some_and(|(_, p)| temporary_expired(*p, power))
        {
            s.temporary_fan = None;
        }
        if let Some((v, _)) = &s.temporary_fan {
            return Ok(Some(v.clone()));
        }
        if !s.document["overrides"]["fanControl"].is_null() {
            // Stored intent may exceed the fan write bound; admit it here.
            let selected = s.document["overrides"]["fanControl"].clone();
            openblade_domain::fan_history::validate_settings(&selected)?;
            return Ok(Some(selected));
        }
        drop(s);
        let Some(doc) = aggregate(self.files.as_ref(), self.same_path)? else {
            return Ok(None);
        };
        let key = match power {
            0 => "pluggedIn",
            1 => "battery",
            2 => "usbC",
            _ => return Ok(None),
        };
        let profiles = &doc["profiles"];
        let app = application.and_then(|path| {
            profiles["applicationProfiles"]
                .as_array()?
                .iter()
                .find(|p| {
                    p["enabled"] == true
                        && p["executablePath"]
                            .as_str()
                            .is_some_and(|s| (self.same_path)(s, path))
                })
        });
        let base = &profiles[key];
        let over = app.map(|a| &a["overrides"][key]);
        let selected = over
            .filter(|o| !o["fanControl"].is_null())
            .map(|o| o["fanControl"].clone())
            .or_else(|| {
                over.and_then(|o| o["fans"].as_u64())
                    .map(|m| legacy_fan(m as u8))
            })
            .unwrap_or_else(|| {
                if base["fanControl"].is_null() {
                    legacy_fan(base["fans"].as_u64().unwrap_or(0) as u8)
                } else {
                    base["fanControl"].clone()
                }
            });
        openblade_domain::fan_history::validate_settings(&selected)?;
        Ok(Some(selected))
    }
    pub(super) fn record_fan(&self, mut entry: Entry, cleanup: bool) -> io::Result<()> {
        let mut s = self.state.lock().map_err(|_| denied())?;
        if s.quarantined {
            return Err(denied());
        }
        entry.user_sid = self.user_sid.clone();
        let record = entry.fan.as_ref().ok_or_else(denied)?;
        record.validate()?;
        if entry.progress == 6
            && (s.last.as_ref().is_none_or(|p| p.progress != 0)
                || self
                    .files
                    .read("thermal-mutation.journal.pending")?
                    .is_some()
                || self.files.read(JOURNAL)? != s.journal)
        {
            return Err(denied());
        }
        if entry.progress == 0 {
            if s.last.as_ref().is_some_and(|e| !settled(e.progress)) {
                return Err(denied());
            }
            // Full manual operation plus owned Stop release, including two-digit
            // progress values, must fit without rollover while ownership is live.
            let mut largest = entry.clone();
            largest.progress = 50;
            let row = serde_json::to_vec(&largest)?.len() + 1;
            let rows = if record.intent.manual() {
                8 + 2 * record.setters()
            } else {
                2 + 2 * record.setters()
            };
            if row > crate::thermal_history::ROW {
                return Err(denied());
            }
            let mut held = None;
            crate::thermal_history::rows(s.journal.as_deref().unwrap_or_default(), |e, _| {
                if e.fan.is_some() {
                    if e.progress == 30 {
                        held = Some(e.clone());
                    } else if matches!(e.progress, 50 | 51) {
                        held = None;
                    }
                }
                Ok(())
            })?;
            let retained = if let Some(mut old) = held {
                old.progress = 50;
                (serde_json::to_vec(&old)?.len() + 1)
                    .checked_mul(6)
                    .ok_or_else(denied)?
            } else {
                0
            };
            let needed = row
                .checked_mul(rows)
                .and_then(|n| n.checked_add(retained))
                .ok_or_else(denied)?;
            if s.temporary_capacity > 0
                && s.journal
                    .as_deref()
                    .unwrap_or_default()
                    .len()
                    .checked_add(needed)
                    .and_then(|n| n.checked_add(s.temporary_capacity))
                    .is_none_or(|n| n > crate::thermal_history::SEGMENT)
            {
                return Err(crate::thermal_history::capacity_refusal());
            }
            match self.files.reserve(
                s.journal.as_deref(),
                needed
                    .checked_add(s.temporary_capacity)
                    .ok_or_else(denied)?,
            ) {
                Ok(j) => s.journal = j,
                Err(e) if crate::thermal_history::known_capacity_refusal(&e) => return Err(e),
                Err(e) => {
                    s.quarantined = true;
                    s.preparation_failed = true;
                    return Err(e);
                }
            }
        }
        let mut bytes = s.journal.clone().unwrap_or_default();
        bytes.extend(serde_json::to_vec(&entry)?);
        bytes.push(b'\n');
        crate::thermal_history::rows(&bytes, |_, _| Ok(()))?;
        let result = if entry.progress == 6 {
            self.files
                .replace_retired(JOURNAL, s.journal.as_deref(), &bytes)
        } else if cleanup {
            self.files
                .replace_cleanup(JOURNAL, s.journal.as_deref(), &bytes)
        } else {
            self.files.replace(JOURNAL, s.journal.as_deref(), &bytes)
        };
        if let Err(e) = result {
            s.quarantined = true;
            s.preparation_failed = entry.progress == 0;
            s.intent_append_failed = matches!(entry.progress, 10 | 12 | 14 | 16 | 40 | 42);
            return Err(e);
        }
        s.journal = Some(bytes);
        s.last = Some(entry);
        Ok(())
    }
    pub(super) fn desired_manual_fans(
        &self,
        power: i32,
        application: Option<&str>,
    ) -> io::Result<bool> {
        let s = self.state.lock().map_err(|_| denied())?;
        if let Some((v, p)) = &s.temporary_fan
            && !temporary_expired(*p, power)
        {
            return Ok(v["mode"] != 0);
        }
        if let Some(mode) = s.document["overrides"]["fans"].as_i64() {
            return Ok(mode != 0);
        }
        drop(s);
        let Some(doc) = aggregate(self.files.as_ref(), self.same_path)? else {
            return Ok(false);
        };
        let key = match power {
            0 => "pluggedIn",
            1 => "battery",
            2 => "usbC",
            _ => return Ok(false),
        };
        let profiles = &doc["profiles"];
        let app = application.and_then(|path| {
            profiles["applicationProfiles"]
                .as_array()?
                .iter()
                .find(|p| {
                    p["enabled"].as_bool() == Some(true)
                        && p["executablePath"]
                            .as_str()
                            .is_some_and(|s| (self.same_path)(s, path))
                })
        });
        let mode = app
            .and_then(|a| {
                a["overrides"][key]["fans"]
                    .as_i64()
                    .or_else(|| a["overrides"][key]["fanControl"]["mode"].as_i64())
            })
            .or_else(|| profiles[key]["fans"].as_i64());
        Ok(mode.is_some_and(|m| m != 0))
    }
    pub(super) fn profiles(&self) -> io::Result<Option<Value>> {
        Ok(aggregate(self.files.as_ref(), self.same_path)?.map(|v| v["profiles"].clone()))
    }
    pub(super) fn progress(&self) -> Option<u8> {
        self.state.lock().ok()?.last.as_ref().map(|e| e.progress)
    }
    // Every caller, including the shared mutation tests, passes the journal fields individually.
    #[allow(clippy::too_many_arguments)]
    pub(super) fn record(
        &self,
        request: &str,
        owner: &str,
        destination: Preset,
        intent: &str,
        requested: &Option<Override>,
        caller_pid: u32,
        session: u32,
        progress: Progress,
    ) -> io::Result<()> {
        let mut s = self.state.lock().map_err(|_| denied())?;
        if s.quarantined {
            return Err(denied());
        }
        if progress == Progress::KnownNotSubmitted
            && (s.last.as_ref().is_none_or(|e| e.progress != 0)
                || self
                    .files
                    .read("thermal-mutation.journal.pending")?
                    .is_some()
                || self.files.read(JOURNAL)? != s.journal)
        {
            return Err(denied());
        }
        if let Some(prior) = &s.last {
            if prior.request == request {
                if prior.owner != owner
                    || prior.user_sid != self.user_sid
                    || prior.caller_pid != caller_pid
                    || prior.session != session
                    || prior.intent != intent
                    || prior.destination != destination as i32
                    || !progress_follows(prior.progress, progress as u8)
                {
                    return Err(denied());
                }
            } else if !settled(prior.progress) || progress != Progress::Prepared {
                return Err(denied());
            }
        } else if progress != Progress::Prepared {
            return Err(denied());
        }
        let entry = Entry {
            tfc: None,
            battery: None,
            fan: None,
            request: request.into(),
            owner: owner.into(),
            user_sid: self.user_sid.clone(),
            caller_pid,
            session,
            requested: requested.clone(),
            destination: destination as i32,
            intent: intent.into(),
            progress: progress as u8,
        };
        if progress == Progress::Prepared {
            // Reserve the exact request's largest legal branch (0..5). Row
            // progress is one digit, including the shorter 0->6 retirement path.
            let row = serde_json::to_vec(&entry)?
                .len()
                .checked_add(1)
                .ok_or_else(denied)?;
            if row > crate::thermal_history::ROW {
                return Err(denied());
            }
            let needed = row
                .checked_mul(6)
                .and_then(|n| n.checked_add(s.temporary_capacity))
                .ok_or_else(denied)?;
            // A live process-scoped TFC origin and its reserved cancellation
            // must stay together. Performance cannot spend or roll this quota.
            if s.temporary_capacity > 0
                && s.journal
                    .as_deref()
                    .unwrap_or_default()
                    .len()
                    .checked_add(needed)
                    .is_none_or(|n| n > crate::thermal_history::SEGMENT)
            {
                return Err(crate::thermal_history::capacity_refusal());
            }
            match self.files.reserve(s.journal.as_deref(), needed) {
                Ok(journal) => s.journal = journal,
                Err(error) if crate::thermal_history::known_capacity_refusal(&error) => {
                    return Err(error);
                }
                Err(error) => {
                    s.quarantined = true;
                    s.preparation_failed = true;
                    return Err(error);
                }
            }
        }
        let mut bytes = s.journal.clone().unwrap_or_default();
        bytes.extend(serde_json::to_vec(&entry)?);
        bytes.push(b'\n');
        if bytes.len() > 1024 * 1024 {
            return Err(io::Error::new(
                io::ErrorKind::StorageFull,
                "Thermal journal capacity exhausted; no further progress was recorded.",
            ));
        }
        if let Err(error) = self.files.replace(JOURNAL, s.journal.as_deref(), &bytes) {
            s.quarantined = true;
            // Only a new Prepared append establishes this local failure cause.
            // Staging or publication may already have left durable bytes.
            // Existing unresolved history cannot reach this append.
            s.preparation_failed = progress == Progress::Prepared;
            // An unsuccessful intent append may already be pending or published.
            // Cached Prepared is not proof that no intent evidence exists.
            s.intent_append_failed =
                matches!(progress, Progress::FirstIntent | Progress::SecondIntent);
            return Err(error);
        }
        s.journal = Some(bytes);
        s.last = Some(entry);
        Ok(())
    }
}

// This stage never migrates or writes the aggregate. Validate its durable envelope
// before touching overrides, and consume only explicitly represented performance
// fields. Missing configuration does not materialize or activate a default.
fn aggregate(files: &dyn Files, same_path: fn(&str, &str) -> bool) -> io::Result<Option<Value>> {
    if files.read(configuration::RECOVERY_REQUIRED)?.is_some() {
        return Err(denied());
    }
    let profile = files.read(configuration::PROFILE)?;
    let preferences = files.read(configuration::PREFERENCES)?;
    let commit = files.read(configuration::COMMIT)?;
    if profile.is_none() && preferences.is_none() && commit.is_none() {
        return Ok(None);
    }
    fn file(v: Option<&[u8]>) -> ConfigFile<'_> {
        v.map_or(ConfigFile::Missing, ConfigFile::Bytes)
    }
    // Schemas 25 and 26 are read as managed ProfileStore reads them: lighting
    // members newer than the document are rejected, and schema 26 also gets the
    // PreserveLegacyIndependentProfileIntent normalisation below. Schema 24 still
    // needs managed Fan Stop migration first.
    let Inspection::CommittedEnvelope {
        schema: schema @ 25..=configuration::CURRENT_SCHEMA,
    } = configuration::inspect(configuration::Snapshot {
        profile: file(profile.as_deref()),
        preferences: file(preferences.as_deref()),
        commit: file(commit.as_deref()),
        recovery: RecoveryRecord::Missing,
    })
    else {
        return Err(io::Error::new(
            io::ErrorKind::Unsupported,
            "Uncommitted or non-current profile aggregate is preserved; managed recovery or migration is required.",
        ));
    };
    let mut doc = decode(profile.as_deref().ok_or_else(denied)?)?;
    let prefs = decode(preferences.as_deref().ok_or_else(denied)?)?;
    if [&doc, &prefs]
        .into_iter()
        .any(|document| configuration::legacy_lighting_intent(document, schema < 27, schema < 26))
    {
        return Err(denied());
    }
    let profiles = doc["profiles"].as_object().ok_or_else(denied)?;
    for key in ["pluggedIn", "battery", "usbC"] {
        let p = profiles
            .get(key)
            .and_then(Value::as_object)
            .ok_or_else(denied)?;
        if !matches!(p.get("performance").and_then(Value::as_i64), Some(0..=9))
            || !matches!(p.get("fans").and_then(Value::as_i64), Some(0..=3))
        {
            return Err(denied());
        }
    }
    if let Some(apps) = profiles.get("applicationProfiles") {
        let apps = apps.as_array().ok_or_else(denied)?;
        if apps.len() > 128 {
            return Err(denied());
        }
        let mut paths: Vec<String> = vec![];
        let mut ids = std::collections::BTreeSet::new();
        for app in apps {
            let id = app["id"].as_str().ok_or_else(denied)?;
            let path = app["executablePath"].as_str().ok_or_else(denied)?;
            if !crate::service_frame::is_guid(id)
                || !ids.insert(id.to_ascii_lowercase())
                || paths.iter().any(|saved| same_path(saved, path))
                || !valid_application_path(path)
                || !app["enabled"].is_boolean()
                || !matches!(app["activationMode"].as_i64(), Some(0..=1))
                || !app["overrides"].is_object()
            {
                return Err(denied());
            }
            paths.push(path.to_owned());
            for key in ["pluggedIn", "battery", "usbC"] {
                let p = &app["overrides"][key];
                if !p.is_null()
                    && (!p.is_object()
                        || !p["performance"].is_null()
                            && !matches!(p["performance"].as_i64(), Some(0..=9)))
                {
                    return Err(denied());
                }
                // Entirely absent/null optional policy inherits. A present value
                // must satisfy the same typed fan contract as managed loading.
                if !p["fans"].is_null() && !matches!(p["fans"].as_i64(), Some(0..=3)) {
                    return Err(denied());
                }
                if !p["fanControl"].is_null() {
                    fan_control(&p["fanControl"])?;
                    let legacy_maximum = p["fans"].as_i64() == Some(3)
                        && p["fanControl"]["mode"].as_i64() == Some(1)
                        && p["fanControl"]["fixedSpeedRpm"].as_i64() == Some(5400);
                    if !p["fans"].is_null()
                        && p["fans"] != p["fanControl"]["mode"]
                        && !legacy_maximum
                    {
                        return Err(denied());
                    }
                }
            }
        }
    }
    crate::service_status::Snapshot::validate_thermal_profiles(&doc["profiles"])?;
    if schema == 26 {
        preserve_legacy_independent_intent(&mut doc["profiles"]);
    }
    Ok(Some(doc))
}

/// Managed `LightingSettingsFactory.PreserveLegacyIndependentProfileIntent`,
/// which `ProfileStore` applies only when it reads a schema-26 document: a
/// lighting setting that controls the keyboard effect and also carries
/// independent controls keeps them without the keyboard effect. `profiles` is
/// the document's `profiles` member; power profiles and application overrides
/// are both covered.
fn preserve_legacy_independent_intent(profiles: &mut Value) {
    const POWER: [&str; 3] = ["pluggedIn", "battery", "usbC"];
    fn preserve(profile: Option<&mut Value>) {
        let Some(Value::Object(lighting)) = profile.and_then(|p| p.get_mut("lighting")) else {
            return;
        };
        // ProfileStore binds these case-sensitively.
        if lighting.get("controlsKeyboardEffect") == Some(&Value::Bool(true))
            && lighting
                .get("independentControls")
                .is_some_and(|v| !v.is_null())
        {
            lighting.insert("controlsKeyboardEffect".into(), Value::Bool(false));
        }
    }
    for key in POWER {
        preserve(profiles.get_mut(key));
    }
    if let Some(Value::Array(apps)) = profiles.get_mut("applicationProfiles") {
        for app in apps {
            for key in POWER {
                preserve(app.get_mut("overrides").and_then(|o| o.get_mut(key)));
            }
        }
    }
}

pub(super) fn valid_application_path(p: &str) -> bool {
    p.encode_utf16().count() <= 32767
        && p.trim() == p
        && std::path::Path::new(p).is_absolute()
        && p.to_ascii_lowercase().ends_with(".exe")
        && !p.chars().any(char::is_control)
        && !p.split(['\\', '/']).any(|part| matches!(part, "." | ".."))
}

// Exact managed FromLegacy defaults; only intent, never firmware confirmation.
pub(super) fn legacy_fan(mode: u8) -> Value {
    let points = |temps: [u8; 6], speeds: [u16; 6]| {
        temps
            .into_iter()
            .zip(speeds)
            .map(|(t, r)| json!({"temperatureCelsius":t,"speedRpm":r}))
            .collect::<Vec<_>>()
    };
    json!({"mode":mode,"fixedSpeedRpm":if mode==3{5400}else{3700},"manualCurve":{"cpu":points([40,50,60,70,80,90],[2000,2100,2600,3400,4400,5400]),"gpu":points([35,45,55,65,75,85],[2000,2100,2700,3600,4500,5400]),"useHigherTemperatureForBoth":true}})
}
#[cfg(test)]
mod release_read_tests {
    use super::*;
    #[test]
    fn tfc_release_only_exact_read_operations_tag_retryable_errors() {
        for code in [2, 5, 32, 33, 995, 1117] {
            let open = release_open_error(io::Error::from_raw_os_error(code));
            let read = release_bytes_error(io::Error::from_raw_os_error(code));
            assert_eq!(is_release_read_unavailable(&open), code == 32);
            assert_eq!(is_release_read_unavailable(&read), code == 33);
            assert!(
                !is_release_read_unavailable(&io::Error::from_raw_os_error(code)),
                "aggregate errors are never tagged by number"
            );
        }
        let tagged = release_open_error(io::Error::from_raw_os_error(32));
        assert_eq!(
            tagged
                .get_ref()
                .unwrap()
                .downcast_ref::<ReleaseReadUnavailable>()
                .unwrap()
                .0
                .raw_os_error(),
            Some(32)
        );
        assert!(is_release_deferred(
            &ReleaseDeferred::unreadable(tagged).into_error()
        ));
        assert!(!is_release_deferred(&io::Error::other("unclassified")));
    }
}

//! Compatible saved intent and durable per-submission fence. Constructors take
//! explicit storage first; the injected path never resolves a product root.
use crate::configuration::{self, File as ConfigFile, Inspection, RecoveryRecord};
use openblade_domain::thermal_mutation::{Preset, Progress, progress_follows, settled};
use serde::{Deserialize, Serialize};
use serde_json::{Value, json};
use sha2::Digest;
use std::{
    io,
    sync::{Arc, Mutex},
};

const OVERRIDES: &str = "manual-overrides.json";
const JOURNAL: &str = "thermal-mutation.journal";
pub(super) trait Files: Send + Sync {
    fn read(&self, name: &str) -> io::Result<Option<Vec<u8>>>;
    fn replace(&self, name: &str, expected: Option<&[u8]>, bytes: &[u8]) -> io::Result<()>;
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
fn overrides(bytes: Option<&[u8]>) -> io::Result<Value> {
    let mut document = match bytes {
        Some(bytes) => decode(bytes)?,
        None => {
            json!({"schemaVersion":8,"overrides":{"performance":null,"fans":null,"lighting":null,"customPerformance":null,"fanControl":null,"gpuClockTuning":null}})
        }
    };
    let root = document.as_object_mut().ok_or_else(denied)?;
    if root.len() != 2
        || !matches!(
            root.get("schemaVersion").and_then(Value::as_i64),
            Some(1..=8)
        )
    {
        return Err(denied());
    }
    if root.get("schemaVersion").and_then(Value::as_i64) != Some(8) {
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
    members(v, &["enabled", "offsets"])?;
    if !v["enabled"].is_boolean() {
        return Err(denied());
    }
    offsets(&v["offsets"])
}
fn fan_control(v: &Value) -> io::Result<()> {
    members(v, &["mode", "fixedSpeedRpm", "manualCurve"])?;
    let speed = |v: &Value| matches!(v.as_i64(),Some(n) if (2000..=5400).contains(&n)&&n%100==0);
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
#[derive(Clone, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
struct Entry {
    request: String,
    owner: String,
    user_sid: String,
    caller_pid: u32,
    session: u32,
    requested: Option<Override>,
    destination: i32,
    intent: String,
    progress: u8,
}
struct State {
    quarantined: bool,
    configuration_failed: bool,
    bytes: Option<Vec<u8>>,
    document: Value,
    journal: Option<Vec<u8>>,
    last: Option<Entry>,
    temporary: Option<(Override, i32)>,
}
pub(super) struct Storage {
    same_path: fn(&str, &str) -> bool,
    user_sid: String,
    files: Box<dyn Files>,
    state: Mutex<State>,
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
        let mut last: Option<Entry> = None;
        if let Some(bytes) = &journal {
            if bytes.len() > 1024 * 1024 || !bytes.ends_with(b"\n") {
                return Err(denied());
            }
            for line in bytes.split(|b| *b == b'\n').filter(|line| !line.is_empty()) {
                let entry: Entry = serde_json::from_slice(line).map_err(|_| denied())?;
                if let Some(v) = &entry.requested {
                    v.validate()?;
                }
                if !entry.user_sid.starts_with("S-1-5-21-")
                    || !entry
                        .user_sid
                        .bytes()
                        .all(|b| b.is_ascii_digit() || b"S-".contains(&b))
                    || entry.caller_pid == 0
                    || entry.session == 0
                    || format!(
                        "{:x}",
                        sha2::Sha256::digest(serde_json::to_vec(&entry.requested)?)
                    ) != entry.intent
                {
                    return Err(denied());
                }
                if !crate::service_frame::is_guid(&entry.request)
                    || Preset::from_wire(entry.destination).is_none()
                    || !crate::native_owner_record::hex(&entry.owner, 64)
                    || !crate::native_owner_record::hex(&entry.intent, 64)
                    || entry.progress > 6
                {
                    return Err(denied());
                }
                if let Some(prior) = &last {
                    if entry.request == prior.request {
                        if entry.user_sid != prior.user_sid
                            || entry.caller_pid != prior.caller_pid
                            || entry.session != prior.session
                            || entry.owner != prior.owner
                            || entry.intent != prior.intent
                            || entry.destination != prior.destination
                            || !progress_follows(prior.progress, entry.progress)
                        {
                            return Err(denied());
                        }
                    } else if !settled(prior.progress) || entry.progress != 0 {
                        return Err(denied());
                    }
                } else if entry.progress != 0 {
                    return Err(denied());
                }
                last = Some(entry);
            }
        }
        Ok(Arc::new(Self {
            same_path,
            user_sid: user_sid.into(),
            files,
            state: Mutex::new(State {
                quarantined: false,
                configuration_failed: false,
                bytes,
                document,
                journal,
                last,
                temporary: None,
            }),
        }))
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
        next["schemaVersion"] = json!(8);
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
        Ok(())
    }
    pub(super) fn desired(
        &self,
        power: i32,
        application: Option<&str>,
    ) -> io::Result<Option<(Preset, i32)>> {
        let mut s = self.state.lock().map_err(|_| denied())?;
        if matches!(power, 0..=2) && s.temporary.as_ref().is_some_and(|(_, p)| *p != power) {
            s.temporary = None;
        }
        let persistent = s.document["overrides"]["performance"]
            .as_i64()
            .map(|n| n as i32);
        let temporary = s.temporary.as_ref().map(|(v, _)| v.value);
        for (value, source) in [(persistent, 4), (temporary, 3)] {
            if let Some(value) = value
                && openblade_domain::automation::performance_source_eligible(value, power)
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
        let s = self.state.lock().map_err(|_| denied())?;
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
    pub(super) fn configuration_failed(&self) -> bool {
        self.state.lock().map_or(true, |s| s.configuration_failed)
    }
    pub(super) fn current_override(&self, power: i32) -> io::Result<Option<Override>> {
        let mut s = self.state.lock().map_err(|_| denied())?;
        if self.files.read(OVERRIDES)? != s.bytes {
            s.quarantined = true;
            return Err(denied());
        }
        if matches!(power, 0..=2) && s.temporary.as_ref().is_some_and(|(_, p)| *p != power) {
            s.temporary = None;
        }
        if let Some((v, _)) = &s.temporary {
            return Ok(Some(v.clone()));
        }
        let saved = &s.document["overrides"];
        Ok(saved["performance"].as_i64().map(|n| Override {
            value: n as i32,
            lifetime: 1,
            custom_performance: None,
            gpu_clock_tuning: saved
                .get("gpuClockTuning")
                .filter(|v| !v.is_null())
                .cloned(),
        }))
    }
    pub(super) fn desired_manual_fans(
        &self,
        power: i32,
        application: Option<&str>,
    ) -> io::Result<bool> {
        let s = self.state.lock().map_err(|_| denied())?;
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
            .and_then(|a| a["overrides"][key]["fans"].as_i64())
            .or_else(|| profiles[key]["fans"].as_i64());
        Ok(mode.is_some_and(|m| m != 0))
    }
    pub(super) fn profiles(&self) -> io::Result<Option<Value>> {
        Ok(aggregate(self.files.as_ref(), self.same_path)?.map(|v| v["profiles"].clone()))
    }
    pub(super) fn progress(&self) -> Option<u8> {
        self.state.lock().ok()?.last.as_ref().map(|e| e.progress)
    }
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
    if !matches!(
        configuration::inspect(configuration::Snapshot {
            profile: file(profile.as_deref()),
            preferences: file(preferences.as_deref()),
            commit: file(commit.as_deref()),
            recovery: RecoveryRecord::Missing
        }),
        Inspection::CommittedEnvelope { schema: 25 }
    ) {
        return Err(io::Error::new(
            io::ErrorKind::Unsupported,
            "Uncommitted or non-current profile aggregate is preserved; managed recovery or migration is required.",
        ));
    }
    let doc = decode(profile.as_deref().ok_or_else(denied)?)?;
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
            }
        }
    }
    crate::service_status::Snapshot::validate_thermal_profiles(&doc["profiles"])?;
    Ok(Some(doc))
}

pub(super) fn valid_application_path(p: &str) -> bool {
    p.encode_utf16().count() <= 32767
        && p.trim() == p
        && std::path::Path::new(p).is_absolute()
        && p.to_ascii_lowercase().ends_with(".exe")
        && !p.chars().any(char::is_control)
        && !p.split(['\\', '/']).any(|part| matches!(part, "." | ".."))
}

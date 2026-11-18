//! Manual-operation subset of the managed display automation recovery state.
//!
//! The file is the managed `%LOCALAPPDATA%\OpenBlade\display-automation.json`
//! read by `DisplayAutomationCheckpointStore`. Loading follows its two typed
//! readers (recovery schema 4, then legacy schema 1); any other existing file
//! is invalid and pauses automation with every field uncertain. Writes encode
//! the complete schema-4 record with the same atomic temporary-file replace.
//! Begin/Complete/Resume mirror `DisplayAutomationCoordinator`. The native
//! Agent has no automation, so no transition is scheduled after a resume.
use super::observation::Snapshot;
use serde_json::{Map, Value, json};
use std::{
    fs::{self, File, OpenOptions},
    io::{self, Read, Write},
    path::PathBuf,
};

const SCHEMA: i64 = 1;
const RECOVERY_SCHEMA: i64 = 4;
pub(super) const MAXIMUM_BYTES: usize = 4096;

/// Durable storage boundary. Tests inject failures and restarts here.
pub(super) trait CheckpointFile: Send {
    /// `Ok(None)` only when the file does not exist.
    fn read(&mut self) -> io::Result<Option<Vec<u8>>>;
    /// Replace the file atomically after a flushed temporary write.
    fn replace(&mut self, bytes: &[u8]) -> io::Result<()>;
}

pub(super) struct WindowsCheckpointFile(Option<PathBuf>);

impl WindowsCheckpointFile {
    pub(super) fn current_user() -> Self {
        Self(
            super::super::user_environment::local_app_data()
                .ok()
                .map(|root| root.join("OpenBlade").join("display-automation.json")),
        )
    }

    fn path(&self) -> io::Result<&PathBuf> {
        self.0
            .as_ref()
            .ok_or_else(|| io::Error::other("local application data is unavailable"))
    }
}

impl CheckpointFile for WindowsCheckpointFile {
    fn read(&mut self) -> io::Result<Option<Vec<u8>>> {
        let file = match File::open(self.path()?) {
            Ok(file) => file,
            Err(error) if error.kind() == io::ErrorKind::NotFound => return Ok(None),
            Err(error) => return Err(error),
        };
        let mut bytes = Vec::new();
        file.take(MAXIMUM_BYTES as u64 + 1).read_to_end(&mut bytes)?;
        Ok(Some(bytes))
    }

    fn replace(&mut self, bytes: &[u8]) -> io::Result<()> {
        let path = self.path()?;
        if let Some(directory) = path.parent() {
            fs::create_dir_all(directory)?;
        }
        let mut temporary = path.clone().into_os_string();
        temporary.push(".tmp");
        let temporary = PathBuf::from(temporary);
        {
            let mut file = OpenOptions::new()
                .write(true)
                .create(true)
                .truncate(true)
                .open(&temporary)?;
            file.write_all(bytes)?;
            file.sync_all()?;
        }
        // Windows std rename replaces an existing destination (MoveFileExW).
        fs::rename(&temporary, path)
    }
}

/// Refresh rate, HDR, color profile and brightness, in managed field order.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub(super) struct Fields {
    pub(super) refresh: bool,
    pub(super) hdr: bool,
    pub(super) color: bool,
    pub(super) brightness: bool,
}

impl Fields {
    const ALL: Self = Self {
        refresh: true,
        hdr: true,
        color: true,
        brightness: true,
    };
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(super) enum Status {
    Succeeded = 0,
    Blocked = 1,
    Failed = 2,
    Indeterminate = 3,
}

pub(super) struct Recovery<F> {
    file: F,
    last: Option<u8>,
    application_profile_id: Option<String>,
    baseline: Option<Value>,
    owned: Fields,
    manual_hold: bool,
    paused: bool,
    uncertain: Fields,
    inherited_pause: bool,
    inherited: Fields,
    /// Native-only manual gate. Set while a brightness submission may be
    /// unresolved, including after restart; cleared only by an explicit read
    /// that independently observes brightness.
    brightness_unresolved: bool,
}

impl<F: CheckpointFile> Recovery<F> {
    pub(super) fn load(mut file: F) -> Self {
        let loaded = match file.read() {
            Ok(None) => Loaded::Absent,
            Ok(Some(bytes)) => parse(&bytes),
            Err(_) => Loaded::Invalid,
        };
        let mut state = Self {
            file,
            last: None,
            application_profile_id: None,
            baseline: None,
            owned: Fields::default(),
            manual_hold: false,
            paused: false,
            uncertain: Fields::default(),
            inherited_pause: false,
            inherited: Fields::default(),
            brightness_unresolved: false,
        };
        match loaded {
            Loaded::Absent => {}
            Loaded::Legacy(source) => state.last = Some(source),
            Loaded::Invalid => {
                state.paused = true;
                state.uncertain = Fields::ALL;
            }
            Loaded::Recovery(record) => {
                state.last = Some(record.last);
                state.manual_hold = record.manual_hold;
                state.paused = record.paused;
                state.uncertain = record.uncertain;
                // Managed only restores ownership alongside a baseline.
                if record.baseline.is_some() {
                    state.baseline = record.baseline;
                    state.owned = record.owned;
                    state.application_profile_id = record.application_profile_id;
                }
            }
        }
        state.brightness_unresolved = state.paused && state.uncertain.brightness;
        state
    }

    pub(super) fn brightness_unresolved(&self) -> bool {
        self.brightness_unresolved
    }

    /// `BeginManualOperation` for a brightness-only request. `source` is the
    /// current trusted power source, which managed observation would already
    /// have recorded. Returns false when the checkpoint could not be saved.
    pub(super) fn begin_brightness(&mut self, source: Option<u8>) -> bool {
        if let Some(source) = source {
            self.last = Some(source);
        }
        self.inherited_pause = self.paused;
        self.inherited = self.uncertain;
        self.manual_hold = true;
        self.paused = true;
        self.uncertain.brightness = true;
        self.baseline = None;
        self.owned = Fields::default();
        self.application_profile_id = None;
        if !self.persist() {
            self.inherited_pause = false;
            self.uncertain = self.inherited;
            self.inherited = Fields::default();
            self.latch();
            return false;
        }
        true
    }

    /// `CompleteManualOperation`.
    pub(super) fn complete(&mut self, status: Status, state: &Snapshot) {
        let affected = self.uncertain;
        let confirmed = status == Status::Succeeded && state.confirms(self.uncertain);
        if confirmed {
            self.paused = false;
            self.uncertain = Fields::default();
        } else if matches!(status, Status::Indeterminate | Status::Succeeded) {
            // Managed keeps the durable pause until every affected field reads
            // back. The native write gate tracks only a possible unconfirmed
            // submission: a Succeeded write already showed its level (or
            // submitted nothing), so unrelated inherited fields must not gate it.
            self.paused = true;
            self.brightness_unresolved |= status == Status::Indeterminate;
        } else {
            self.paused = self.inherited_pause;
            self.uncertain = if self.paused {
                self.inherited
            } else {
                Fields::default()
            };
        }
        self.inherited_pause = false;
        self.inherited = Fields::default();
        if self.last.is_some() && !self.persist() {
            self.uncertain = affected;
            self.latch();
            self.brightness_unresolved |= affected.brightness;
        }
    }

    /// `ResumeAfterSuccessfulExplicitRead`, plus the native brightness gate.
    pub(super) fn explicit_read(&mut self, state: &Snapshot) -> bool {
        if state.confirms(Fields {
            brightness: true,
            ..Fields::default()
        }) {
            self.brightness_unresolved = false;
        }
        if !self.paused || self.last.is_none() || !state.confirms(self.uncertain) {
            return false;
        }
        let reconciled = self.uncertain;
        self.paused = false;
        self.manual_hold = false;
        self.uncertain = Fields::default();
        if !self.persist() {
            self.uncertain = reconciled;
            self.latch();
            return false;
        }
        true
    }

    fn latch(&mut self) {
        self.paused = true;
        self.manual_hold = true;
    }

    fn persist(&mut self) -> bool {
        let Some(source) = self.last else {
            return false;
        };
        let record = json!({
            "SchemaVersion": RECOVERY_SCHEMA,
            "LastReconciledPowerSource": source,
            "ApplicationProfileId": self.application_profile_id,
            "Baseline": self.baseline,
            "OwnedRefreshRate": self.owned.refresh,
            "OwnedHdr": self.owned.hdr,
            "OwnedColorProfile": self.owned.color,
            "OwnedBrightness": self.owned.brightness,
            "ManualHold": self.manual_hold,
            "AutomaticWritesPaused": self.paused,
            "UncertainRefreshRate": self.uncertain.refresh,
            "UncertainHdr": self.uncertain.hdr,
            "UncertainColorProfile": self.uncertain.color,
            "UncertainBrightness": self.uncertain.brightness,
        });
        match serde_json::to_vec(&record) {
            Ok(bytes) if bytes.len() <= MAXIMUM_BYTES => self.file.replace(&bytes).is_ok(),
            _ => false,
        }
    }

    #[cfg(test)]
    pub(super) fn file(&mut self) -> &mut F {
        &mut self.file
    }

    #[cfg(test)]
    pub(super) fn flags(&self) -> (bool, bool, Fields) {
        (self.manual_hold, self.paused, self.uncertain)
    }
}

enum Loaded {
    Absent,
    Legacy(u8),
    Recovery(Record),
    Invalid,
}

struct Record {
    last: u8,
    application_profile_id: Option<String>,
    baseline: Option<Value>,
    owned: Fields,
    manual_hold: bool,
    paused: bool,
    uncertain: Fields,
}

fn parse(bytes: &[u8]) -> Loaded {
    if bytes.is_empty() || bytes.len() > MAXIMUM_BYTES {
        return Loaded::Invalid;
    }
    let Ok(Value::Object(object)) = serde_json::from_slice::<Value>(bytes) else {
        return Loaded::Invalid;
    };
    // System.Text.Json's default MaxDepth rejects deeper documents, including
    // nesting inside members neither reader binds.
    if 1 + object.values().map(depth).max().unwrap_or(0) > MAXIMUM_DEPTH {
        return Loaded::Invalid;
    }
    if let Some(record) = recovery(&object) {
        return Loaded::Recovery(record);
    }
    // The legacy reader binds only these two members of the same document; a
    // missing source defaults to PluggedIn (0), as in the typed managed record.
    match (integer(&object, "SchemaVersion"), integer(&object, "LastReconciledPowerSource")) {
        (Some(Some(SCHEMA)), Some(source)) => match source.unwrap_or(0) {
            source @ 0..=2 => Loaded::Legacy(source as u8),
            _ => Loaded::Invalid,
        },
        _ => Loaded::Invalid,
    }
}

const MAXIMUM_DEPTH: usize = 64;

/// Container nesting depth: a scalar is 0, `[]` and `{}` are 1.
fn depth(value: &Value) -> usize {
    match value {
        Value::Array(items) => 1 + items.iter().map(depth).max().unwrap_or(0),
        Value::Object(members) => 1 + members.values().map(depth).max().unwrap_or(0),
        _ => 0,
    }
}

/// Typed System.Text.Json default semantics: case-sensitive names, missing
/// members default, unknown members ignored, and a wrong type rejects the
/// whole document. `Some(None)` is a missing member.
fn recovery(object: &Map<String, Value>) -> Option<Record> {
    let schema = integer(object, "SchemaVersion")?.unwrap_or(0);
    let last = integer(object, "LastReconciledPowerSource")?.unwrap_or(0);
    let application_profile_id = match object.get("ApplicationProfileId") {
        None | Some(Value::Null) => None,
        Some(Value::String(id)) if guid(id) => Some(id.clone()),
        Some(_) => return None,
    };
    let baseline = match object.get("Baseline") {
        None | Some(Value::Null) => None,
        Some(value @ Value::Object(settings)) if valid_settings(settings) => Some(value.clone()),
        Some(_) => return None,
    };
    let flag = |name| boolean(object, name);
    let record = Record {
        last: 0,
        application_profile_id,
        baseline,
        owned: Fields {
            refresh: flag("OwnedRefreshRate")?,
            hdr: flag("OwnedHdr")?,
            color: flag("OwnedColorProfile")?,
            brightness: flag("OwnedBrightness")?,
        },
        manual_hold: flag("ManualHold")?,
        paused: flag("AutomaticWritesPaused")?,
        uncertain: Fields {
            refresh: flag("UncertainRefreshRate")?,
            hdr: flag("UncertainHdr")?,
            color: flag("UncertainColorProfile")?,
            brightness: flag("UncertainBrightness")?,
        },
    };
    (schema == RECOVERY_SCHEMA && (0..=2).contains(&last)).then_some(Record {
        last: last as u8,
        ..record
    })
}

/// A present member must be a JSON integer representable as Int32.
fn integer(object: &Map<String, Value>, name: &str) -> Option<Option<i64>> {
    match object.get(name) {
        None => Some(None),
        Some(Value::Number(number)) => number
            .as_i64()
            .filter(|value| i32::try_from(*value).is_ok())
            .map(Some),
        Some(_) => None,
    }
}

fn boolean(object: &Map<String, Value>, name: &str) -> Option<bool> {
    match object.get(name) {
        None => Some(false),
        Some(Value::Bool(value)) => Some(*value),
        Some(_) => None,
    }
}

fn guid(text: &str) -> bool {
    text.len() == 36
        && text.bytes().enumerate().all(|(i, b)| {
            if matches!(i, 8 | 13 | 18 | 23) {
                b == b'-'
            } else {
                b.is_ascii_hexdigit()
            }
        })
}

fn valid_settings(settings: &Map<String, Value>) -> bool {
    let text = |name| matches!(settings.get(name), None | Some(Value::Null | Value::String(_)));
    text("RefreshRateKey")
        && text("ColorProfileKey")
        && matches!(
            settings.get("HdrEnabled"),
            None | Some(Value::Null | Value::Bool(_))
        )
        && match settings.get("BrightnessPercent") {
            None | Some(Value::Null) => true,
            Some(Value::Number(number)) => number.as_u64().is_some_and(|value| value <= 255),
            Some(_) => false,
        }
}

#[cfg(test)]
#[path = "recovery_tests.rs"]
mod tests;

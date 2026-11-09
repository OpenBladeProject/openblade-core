//! Offline inspection of the managed battery configuration transaction envelope.
//! Observations must come from one coherent snapshot under the caller's storage gate.
//! No decision here authorizes initialization, migration, rollback, or activation.

use serde::Deserialize;
use serde::de::{MapAccess, Visitor};
use serde_json::value::RawValue;
use sha2::{Digest, Sha256};
use std::collections::HashSet;
use std::fmt;

pub const PROFILE: &str = "profiles.json";
pub const PREFERENCES: &str = "battery-preferences.json";
pub const COMMIT: &str = "battery-configuration.commit.json";
pub const RECOVERY_REQUIRED: &str = "battery-configuration.commit.json.recovery-required";
pub const CURRENT_SCHEMA: i32 = 25;

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum File<'a> {
    Missing,
    Bytes(&'a [u8]),
    Unavailable,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum RecoveryRecord {
    Missing,
    Present,
    Unavailable,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct Snapshot<'a> {
    pub recovery: RecoveryRecord,
    pub profile: File<'a>,
    pub preferences: File<'a>,
    pub commit: File<'a>,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum Inspection {
    Empty,
    /// A profile-only legacy envelope awaiting full managed payload validation.
    LegacyCandidate,
    /// The marker and document envelopes agree. Payload validation is still required.
    CommittedEnvelope {
        schema: i32,
    },
    Preserve(Reason),
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum Reason {
    RecoveryRequired,
    StorageUnavailable,
    FutureSchema,
    IncompleteAggregate,
    InvalidDocument,
    HashMismatch,
    MixedSchema,
}

pub fn inspect(snapshot: Snapshot<'_>) -> Inspection {
    inspect_snapshot(snapshot).unwrap_or_else(Inspection::Preserve)
}

fn inspect_snapshot(snapshot: Snapshot<'_>) -> Result<Inspection, Reason> {
    // Managed restart admission uses GetAttributes, not the sidecar contents.
    match snapshot.recovery {
        RecoveryRecord::Present => return Err(Reason::RecoveryRequired),
        RecoveryRecord::Unavailable => return Err(Reason::StorageUnavailable),
        RecoveryRecord::Missing => {}
    }

    let files = [snapshot.profile, snapshot.preferences, snapshot.commit];
    if files.contains(&File::Unavailable) {
        return Err(Reason::StorageUnavailable);
    }
    let documents = files.map(|file| match file {
        File::Bytes(bytes) => Some(Document::new(bytes)),
        _ => None,
    });
    if documents.iter().flatten().any(Document::has_future_schema) {
        return Err(Reason::FutureSchema);
    }

    match documents {
        [None, None, None] => Ok(Inspection::Empty),
        [Some(profile), None, None] => {
            if profile.has_schema() {
                return Err(Reason::IncompleteAggregate);
            }
            let root = profile.strict_object()?;
            if !root.has_exact_members(&["pluggedIn", "battery", "batteryPolicy"]) {
                return Err(Reason::InvalidDocument);
            }
            Ok(Inspection::LegacyCandidate)
        }
        [Some(profile), Some(preferences), Some(commit)] => {
            inspect_committed(&profile, &preferences, &commit)
        }
        _ => Err(Reason::IncompleteAggregate),
    }
}

fn inspect_committed(
    profile: &Document<'_>,
    preferences: &Document<'_>,
    commit: &Document<'_>,
) -> Result<Inspection, Reason> {
    let marker = commit.strict_object()?;
    if !marker.has_exact_members(&["schemaVersion", "profileSha256", "preferencesSha256"]) {
        return Err(Reason::InvalidDocument);
    }
    let schema = web_integer(marker.get("schemaVersion")).filter(|n| supported(*n));
    let schema = schema.ok_or(Reason::InvalidDocument)?;
    let profile_digest: Option<String> = decode(marker.get("profileSha256"))?;
    let preference_digest: Option<String> = decode(marker.get("preferencesSha256"))?;
    if !hash_matches(profile_digest.as_deref(), profile.bytes)
        || !hash_matches(preference_digest.as_deref(), preferences.bytes)
    {
        return Err(Reason::HashMismatch);
    }

    let prefs = preferences.strict_object()?;
    if !prefs.has_exact_members(&["schemaVersion", "preferences"])
        || web_integer(prefs.get("schemaVersion")) != Some(schema)
        || !is_object(prefs.get("preferences"))
    {
        // The managed preference reader reports this as invalid, including a
        // schema mismatch. MixedSchema is reserved for the profile mismatch.
        return Err(Reason::InvalidDocument);
    }
    let root = profile.strict_object()?;
    let version: i32 = decode(root.get("schemaVersion"))?;
    if !supported(version)
        || !root.has_exact_members(&["schemaVersion", "profiles"])
        || !is_object(root.get("profiles"))
    {
        return Err(Reason::InvalidDocument);
    }
    if version != schema {
        return Err(Reason::MixedSchema);
    }
    Ok(Inspection::CommittedEnvelope { schema })
}

fn supported(version: i32) -> bool {
    (1..=CURRENT_SCHEMA).contains(&version)
}

fn web_integer(value: Option<&RawValue>) -> Option<i32> {
    let raw = value?.get();
    if raw.starts_with('"') {
        let text: String = serde_json::from_str(raw).ok()?;
        text.parse().ok()
    } else {
        serde_json::from_str(raw).ok()
    }
}

fn hash_matches(supplied: Option<&str>, bytes: &[u8]) -> bool {
    // Hashes are public integrity metadata. Compare the exact uppercase spelling
    // over the original bytes, including whitespace and line endings.
    supplied.is_some_and(|expected| expected == format!("{:X}", Sha256::digest(bytes)))
}

fn is_object(value: Option<&RawValue>) -> bool {
    value.is_some_and(|raw| raw.get().starts_with('{'))
}

fn decode<'a, T: Deserialize<'a>>(value: Option<&'a RawValue>) -> Result<T, Reason> {
    serde_json::from_str(value.ok_or(Reason::InvalidDocument)?.get())
        .map_err(|_| Reason::InvalidDocument)
}

struct Document<'a> {
    bytes: &'a [u8],
    root: Option<Object<'a>>,
    unique: bool,
}

impl<'a> Document<'a> {
    fn new(bytes: &'a [u8]) -> Self {
        let (root, unique) = serde_json::from_slice::<Object<'_>>(bytes)
            .ok()
            .and_then(|root| root.audit(1).map(|unique| (Some(root), unique)))
            .unwrap_or_default();
        Self {
            bytes,
            root,
            unique,
        }
    }

    fn has_future_schema(&self) -> bool {
        // Check every occurrence before duplicate rejection, just like the
        // managed forward-schema scan. A quoted number is not evidence here.
        self.root.as_ref().is_some_and(|root| {
            root.0.iter().any(|(name, raw)| {
                name == "schemaVersion"
                    && serde_json::from_str::<i32>(raw.get()).is_ok_and(|n| n > CURRENT_SCHEMA)
            })
        })
    }

    fn has_schema(&self) -> bool {
        self.root
            .as_ref()
            .is_some_and(|root| root.get("schemaVersion").is_some())
    }

    fn strict_object(&self) -> Result<&Object<'a>, Reason> {
        self.root
            .as_ref()
            .filter(|_| self.unique)
            .ok_or(Reason::InvalidDocument)
    }
}

// Keep raw values so inspection does not round large legacy numbers, collapse
// duplicate members, or reserialize any document being hashed.
struct Object<'a>(Vec<(String, &'a RawValue)>);

impl<'a> Object<'a> {
    fn get(&self, key: &str) -> Option<&'a RawValue> {
        self.0
            .iter()
            .find_map(|(name, raw)| (name == key).then_some(*raw))
    }

    fn has_exact_members(&self, names: &[&str]) -> bool {
        self.0.len() == names.len() && names.iter().all(|name| self.get(name).is_some())
    }

    // A malformed or over-depth document cannot establish a schema. Duplicates
    // prevent admission but can still establish that the file is future data.
    fn audit(&self, depth: usize) -> Option<bool> {
        let mut seen = HashSet::new();
        let mut unique = true;
        for (name, raw) in &self.0 {
            unique &= seen.insert(name);
            unique &= audit_value(raw.get(), depth)?;
        }
        Some(unique)
    }
}

fn audit_value(raw: &str, parent_depth: usize) -> Option<bool> {
    if parent_depth >= 64 && (raw.starts_with('{') || raw.starts_with('[')) {
        return None;
    }
    match raw.as_bytes().first() {
        Some(b'{') => serde_json::from_str::<Object<'_>>(raw)
            .ok()?
            .audit(parent_depth + 1),
        Some(b'[') => {
            let mut unique = true;
            for item in serde_json::from_str::<Vec<&RawValue>>(raw).ok()? {
                unique &= audit_value(item.get(), parent_depth + 1)?;
            }
            Some(unique)
        }
        Some(b'"') => serde_json::from_str::<String>(raw).ok().map(|_| true),
        _ => Some(true),
    }
}

impl<'de> Deserialize<'de> for Object<'de> {
    fn deserialize<D: serde::Deserializer<'de>>(deserializer: D) -> Result<Self, D::Error> {
        struct ObjectVisitor;

        impl<'de> Visitor<'de> for ObjectVisitor {
            type Value = Object<'de>;

            fn expecting(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
                formatter.write_str("a configuration JSON object")
            }

            fn visit_map<M: MapAccess<'de>>(self, mut map: M) -> Result<Self::Value, M::Error> {
                let mut entries = Vec::new();
                while let Some(entry) = map.next_entry()? {
                    entries.push(entry);
                }
                Ok(Object(entries))
            }
        }

        deserializer.deserialize_map(ObjectVisitor)
    }
}

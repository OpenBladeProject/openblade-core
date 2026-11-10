//! Offline inspection of the managed battery configuration transaction envelope.
//! Observations must come from one coherent snapshot under the caller's storage gate.
//! No decision here authorizes initialization, migration, rollback, or activation.

use serde::Deserialize;
use serde::de::{self, MapAccess, SeqAccess, Visitor};
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
    let schema = marker
        .get("schemaVersion")
        .and_then(web_integer)
        .filter(|n| supported(*n));
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
        || prefs.get("schemaVersion").and_then(web_integer) != Some(schema)
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

pub(crate) fn web_integer(value: &RawValue) -> Option<i32> {
    let raw = value.get();
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
    supplied.is_some_and(|expected| expected == digest_hex(bytes))
}

pub(crate) fn digest_hex(bytes: &[u8]) -> String {
    format!("{:X}", Sha256::digest(bytes))
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
}

impl<'a> Document<'a> {
    fn new(bytes: &'a [u8]) -> Self {
        let root = std::str::from_utf8(bytes)
            .ok()
            .and_then(|text| serde_json::from_str(text).ok())
            .filter(|_| within_document_depth(bytes));
        Self { bytes, root }
    }

    fn has_future_schema(&self) -> bool {
        // Check every occurrence before duplicate rejection, just like the
        // managed forward-schema scan. A quoted number is not evidence here.
        self.root.as_ref().is_some_and(|root| {
            root.0.iter().any(|(name, raw)| {
                name.as_deref() == Some("schemaVersion")
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
        let root = self.root.as_ref().ok_or(Reason::InvalidDocument)?;
        serde_json::from_slice::<StrictJson>(self.bytes).map_err(|_| Reason::InvalidDocument)?;
        Ok(root)
    }
}

// serde_json validates the syntax of borrowed raw values without a depth
// ceiling. Count containers in that validated input to match JsonDocument.
fn within_document_depth(bytes: &[u8]) -> bool {
    let mut depth = 0;
    let mut quoted = false;
    let mut characters = bytes.iter().copied();
    while let Some(byte) = characters.next() {
        match byte {
            b'\\' if quoted => {
                characters.next();
            }
            b'"' => quoted = !quoted,
            b'{' | b'[' if !quoted => {
                depth += 1;
                if depth > 64 {
                    return false;
                }
            }
            b'}' | b']' if !quoted => depth -= 1,
            _ => {}
        }
    }
    true
}

// JsonDocument can scan property names even when another name or value has an
// unpaired UTF-16 escape. Keep such keys opaque during schema discovery. The
// strict pass still rejects them before returning a payload envelope.
struct DiscoveryKey(Option<String>);

impl<'de> Deserialize<'de> for DiscoveryKey {
    fn deserialize<D: serde::Deserializer<'de>>(deserializer: D) -> Result<Self, D::Error> {
        struct KeyVisitor;

        impl Visitor<'_> for KeyVisitor {
            type Value = DiscoveryKey;

            fn expecting(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
                formatter.write_str("a configuration JSON property name")
            }

            fn visit_bytes<E: de::Error>(self, value: &[u8]) -> Result<Self::Value, E> {
                Ok(DiscoveryKey(
                    std::str::from_utf8(value).ok().map(str::to_owned),
                ))
            }
        }

        deserializer.deserialize_bytes(KeyVisitor)
    }
}

struct Object<'a>(Vec<(Option<String>, &'a RawValue)>);

impl<'a> Object<'a> {
    fn get(&self, key: &str) -> Option<&'a RawValue> {
        self.0
            .iter()
            .find_map(|(name, raw)| (name.as_deref() == Some(key)).then_some(*raw))
    }

    fn has_exact_members(&self, names: &[&str]) -> bool {
        self.0.len() == names.len() && names.iter().all(|name| self.get(name).is_some())
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
                while let Some((DiscoveryKey(name), raw)) = map.next_entry()? {
                    entries.push((name, raw));
                }
                Ok(Object(entries))
            }
        }

        deserializer.deserialize_map(ObjectVisitor)
    }
}

// Validate nested members in one streaming pass. No payload numbers are
// interpreted here, including arbitrary-precision numbers.
struct StrictJson;

impl<'de> Deserialize<'de> for StrictJson {
    fn deserialize<D: serde::Deserializer<'de>>(deserializer: D) -> Result<Self, D::Error> {
        deserializer.deserialize_any(Self)
    }
}

impl<'de> Visitor<'de> for StrictJson {
    type Value = Self;

    fn expecting(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str("configuration JSON without duplicate properties")
    }

    fn visit_bool<E: de::Error>(self, _: bool) -> Result<Self, E> {
        Ok(self)
    }

    fn visit_i64<E: de::Error>(self, _: i64) -> Result<Self, E> {
        Ok(self)
    }

    fn visit_u64<E: de::Error>(self, _: u64) -> Result<Self, E> {
        Ok(self)
    }

    fn visit_f64<E: de::Error>(self, _: f64) -> Result<Self, E> {
        Ok(self)
    }

    fn visit_str<E: de::Error>(self, _: &str) -> Result<Self, E> {
        Ok(self)
    }

    fn visit_unit<E: de::Error>(self) -> Result<Self, E> {
        Ok(self)
    }

    fn visit_map<M: MapAccess<'de>>(self, mut map: M) -> Result<Self, M::Error> {
        let mut seen = HashSet::new();
        while let Some((key, _)) = map.next_entry::<String, StrictJson>()? {
            if !seen.insert(key) {
                return Err(de::Error::custom("duplicate configuration property"));
            }
        }
        Ok(self)
    }

    fn visit_seq<S: SeqAccess<'de>>(self, mut seq: S) -> Result<Self, S::Error> {
        while seq.next_element::<StrictJson>()?.is_some() {}
        Ok(self)
    }
}

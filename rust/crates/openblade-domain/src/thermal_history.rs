//! Portable, lossless thermal history grammar. No filesystem/default suppliers.
use crate::thermal_mutation::{CUSTOM, Preset, progress_follows, settled, valid_progress};
use serde::de::{self, MapAccess, SeqAccess, Visitor};
use serde::{Deserialize, Serialize};
use serde_json::Value;
use sha2::{Digest, Sha256};
use std::prelude::rust_2024::*;
use std::{collections::HashSet, fmt, format, io, vec};
fn invalid(message: &'static str) -> io::Error {
    io::Error::new(io::ErrorKind::InvalidData, message)
}
pub fn deny() -> io::Error {
    io::Error::new(
        io::ErrorKind::PermissionDenied,
        "Thermal history is unresolved or unsupported.",
    )
}
pub fn digest(b: &[u8]) -> String {
    format!("{:x}", Sha256::digest(b))
}
fn hex(s: &str, n: usize) -> bool {
    s.len() == n
        && s.bytes()
            .all(|b| b.is_ascii_digit() || (b'a'..=b'f').contains(&b))
}
pub(crate) fn is_guid(s: &str) -> bool {
    s.len() == 36
        && s.bytes().enumerate().all(|(i, b)| {
            if matches!(i, 8 | 13 | 18 | 23) {
                b == b'-'
            } else {
                b.is_ascii_hexdigit()
            }
        })
        && s != "00000000-0000-0000-0000-000000000000"
}
#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct PerformanceOverride {
    pub value: i32,
    pub lifetime: u8,
    pub custom_performance: Option<Value>,
    pub gpu_clock_tuning: Option<Value>,
}
impl PerformanceOverride {
    /// Managed `PerformanceOverride.IsWellFormed` over the native values: a
    /// preset destination carries no Custom settings, and Custom (5) carries
    /// well-formed ones. Model capability is policy, checked when selected.
    pub fn validate(&self) -> io::Result<()> {
        let custom = self.custom_performance.as_ref();
        let well_formed = if self.value == CUSTOM {
            custom.is_some_and(|v| validate_custom_performance(v).is_ok())
        } else {
            Preset::from_wire(self.value).is_some() && custom.is_none()
        };
        if !well_formed || self.lifetime > 1 {
            return Err(invalid("unsupported or invalid thermal override"));
        }
        if let Some(gpu) = &self.gpu_clock_tuning {
            validate_gpu_tuning(gpu)?;
            // Legacy Custom offsets and GPU tuning are exclusive.
            if custom.is_some_and(|v| v.get("gpuClockOffsets").is_some_and(|o| !o.is_null())) {
                return Err(invalid("unsupported or invalid thermal override"));
            }
        }
        Ok(())
    }
}

/// Managed `CustomPerformanceSettings.IsWellFormed`: a defined CPU level
/// (Low 0 to Boost 3), a GPU level of Low, Medium or High, the optional
/// maximum-fan flag and optional legacy GPU offsets. Numbers must be in their
/// canonical integer form, so the managed reader's intent digest agrees.
pub fn validate_custom_performance(v: &Value) -> io::Result<()> {
    let level = |key: &str, max: u64| {
        v[key]
            .as_u64()
            .is_some_and(|n| n <= max && canonical_integer(&v[key]))
    };
    let members = v.as_object().is_some_and(|o| {
        o.keys()
            .all(|k| ["cpu", "gpu", "maximumFans", "gpuClockOffsets"].contains(&k.as_str()))
    });
    if !members
        || !level("cpu", 3)
        || !level("gpu", 2)
        || v.get("maximumFans").is_some_and(|f| !f.is_boolean())
        || v.get("gpuClockOffsets")
            .is_some_and(|o| !o.is_null() && !valid_offsets(o, true))
    {
        return Err(invalid("unsupported or invalid thermal override"));
    }
    Ok(())
}

// An integer written as serde_json writes it back: no fraction, exponent or
// negative zero (arbitrary_precision keeps the original text).
fn canonical_integer(value: &Value) -> bool {
    let text = value.to_string();
    value
        .as_i64()
        .map(|n| n.to_string())
        .or_else(|| value.as_u64().map(|n| n.to_string()))
        == Some(text)
}

// Managed GpuClockOffsetSettings bounds; `canonical` also requires each number
// in its canonical integer form.
fn valid_offsets(value: &Value, canonical: bool) -> bool {
    value.as_object().is_some_and(|o| {
        o.len() == 2
            && o.keys()
                .all(|k| ["coreMegahertz", "memoryMegahertz"].contains(&k.as_str()))
    }) && ["coreMegahertz", "memoryMegahertz"].iter().all(|k| {
        value[k].as_i64().is_some_and(|n| {
            (-10000..=10000).contains(&n) && (!canonical || canonical_integer(&value[k]))
        })
    })
}

// Shared by saved intent and typed requests. Managed GpuClockTuningSettings
// emits EffectiveOffsets as a computed property (Profiles.cs); it grants no
// extra intent and must agree with Enabled/Offsets when it is present.
pub fn validate_gpu_tuning(gpu: &Value) -> io::Result<()> {
    let valid_offsets = |value: &Value| valid_offsets(value, false);
    if gpu.as_object().is_none_or(|o| {
        o.keys()
            .any(|k| !["enabled", "offsets", "effectiveOffsets"].contains(&k.as_str()))
    }) || !gpu["enabled"].is_boolean()
        || !valid_offsets(&gpu["offsets"])
    {
        return Err(invalid("unsupported or invalid thermal override"));
    }
    if let Some(effective) = gpu.get("effectiveOffsets") {
        let expected = if gpu["enabled"] == true {
            gpu["offsets"].clone()
        } else {
            serde_json::json!({"coreMegahertz":0,"memoryMegahertz":0})
        };
        if !valid_offsets(effective) || effective != &expected {
            return Err(invalid("inconsistent computed GPU offsets"));
        }
    }
    Ok(())
}

pub fn validate_json_members(bytes: &[u8]) -> serde_json::Result<()> {
    serde_json::from_slice::<StrictJson>(bytes).map(|_| ())
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

pub const SEGMENT: usize = 1024 * 1024;
pub const ROW: usize = 16384;
pub const CONTRACT: &str = "service-thermal-mutation-v1/rows-v1";
pub const ACTIVE: &str = "thermal-mutation.journal";
pub const STAGING: &str = "thermal-mutation.journal.rollover";
#[derive(Clone, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub struct Entry {
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub tfc: Option<crate::temporary_charge_history::TemporaryChargeRecord>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub battery: Option<crate::battery_history::BatteryRecord>,
    pub request: String,
    pub owner: String,
    pub user_sid: String,
    pub caller_pid: u32,
    pub session: u32,
    pub requested: Option<PerformanceOverride>,
    pub destination: i32,
    pub intent: String,
    pub progress: u8,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub fan: Option<crate::fan_history::FanRecord>,
}
pub fn battery_fan_binding(e: &Entry) -> io::Result<String> {
    let fan = e.fan.as_ref().ok_or_else(deny)?;
    Ok(digest(&lines(
        "OpenBlade.BatteryFanOwner.v1",
        &[
            e.request.clone(),
            e.owner.clone(),
            e.user_sid.clone(),
            e.caller_pid.to_string(),
            e.session.to_string(),
            fan.hash()?,
        ],
    )))
}
/// Validate complete rows without retaining the lifetime log. Callback joins each
/// original row to authenticated owner/producer custody; no current-owner relabel.
pub fn rows(
    bytes: &[u8],
    visit: impl FnMut(&Entry, &[u8]) -> io::Result<()>,
) -> io::Result<(u64, Option<Entry>)> {
    rows_from(bytes, None, visit).map(|(count, last, _)| (count, last))
}
/// The owners still live after a run of rows, as `rows` admits each row
/// against them.
#[derive(Default)]
pub struct Owners {
    /// The fan record that took manual fans (30) and has not handed them
    /// back (50 or 51).
    pub fan: Option<Entry>,
    /// The temporary-charge enable that settled (72 or 73) with the charge
    /// enabled, until another temporary-charge request settles.
    pub temporary: Option<Entry>,
}
/// `rows`, with the owners its rows leave live.
pub fn rows_with_owners(
    bytes: &[u8],
    visit: impl FnMut(&Entry, &[u8]) -> io::Result<()>,
) -> io::Result<(u64, Option<Entry>, Owners)> {
    rows_from(bytes, None, visit)
}
fn rows_from(
    bytes: &[u8],
    mut last: Option<Entry>,
    mut visit: impl FnMut(&Entry, &[u8]) -> io::Result<()>,
) -> io::Result<(u64, Option<Entry>, Owners)> {
    if bytes.len() > SEGMENT || !bytes.is_empty() && !bytes.ends_with(b"\n") {
        return Err(deny());
    }
    let mut owned = last.as_ref().filter(|e| e.progress == 30).cloned();
    let mut temporary_owner: Option<Entry> = None;
    // The automatic cancel request that replaced the live enable's reserved
    // IDs. No other automatic cancel of that enable may follow it, so one
    // enable sends at most its reserved set and one fresh set.
    let mut replaced_by: Option<String> = None;
    let mut count = 0u64;
    for line in bytes.split_inclusive(|b| *b == b'\n') {
        if line.len() < 2 || line.len() > ROW {
            return Err(deny());
        }
        validate_json_members(line).map_err(|_| deny())?;
        // Inspect optional-family shape before serde Option erases absent/null.
        // Legacy absence remains valid; a battery payload requires fan_owner,
        // with explicit null representing no owned fan.
        let shape: Value = serde_json::from_slice(line).map_err(|_| deny())?;
        if let Some(battery) = shape.get("battery")
            && (!battery.is_object() || battery.get("fan_owner").is_none())
        {
            return Err(deny());
        }
        if let Some(tfc) = shape.get("tfc")
            && (!tfc.is_object()
                || shape.get("fan").is_some()
                || shape.get("battery").is_some()
                || shape.get("requested") != Some(&Value::Null)
                || ["policy", "cancel_ids", "origin", "fan_owner"]
                    .iter()
                    .any(|k| tfc.get(k).is_none())
                // Absent means none; an explicit null has no meaning.
                || tfc.get("superseded_ids").is_some_and(Value::is_null))
        {
            return Err(deny());
        }
        let entry: Entry = serde_json::from_slice(line).map_err(|_| deny())?;
        if let Some(tfc) = &entry.tfc {
            crate::fan_history::validate_numbers(&shape["tfc"])?;
            tfc.validate()?;
        }
        if let Some(v) = &entry.requested {
            v.validate()?;
        }
        if let Some(battery) = &entry.battery {
            let raw: Value = serde_json::from_slice(line).map_err(|_| deny())?;
            crate::fan_history::validate_numbers(&raw["battery"])?;
            battery.validate()?;
        }
        if let Some(fan) = &entry.fan {
            let raw: Value = serde_json::from_slice(line).map_err(|_| deny())?;
            crate::fan_history::validate_raw(&raw["fan"])?;
            fan.validate()?;
        }
        if !is_guid(&entry.request)
            || !hex(&entry.owner, 64)
            || !hex(&entry.intent, 64)
            || !entry.user_sid.starts_with("S-1-5-21-")
            || !entry
                .user_sid
                .bytes()
                .all(|b| b.is_ascii_digit() || b"S-".contains(&b))
            || entry.caller_pid == 0
            || entry.session == 0
            || if let Some(t) = &entry.tfc {
                entry.battery.is_some()
                    || entry.fan.is_some()
                    || entry.requested.is_some()
                    || entry.destination != 0
                    || !t.valid_progress(entry.progress)
                    || t.hash()? != entry.intent
            } else if let Some(b) = &entry.battery {
                entry.fan.is_some()
                    || entry.requested.is_some()
                    || entry.destination != 0
                    || !b.valid_progress(entry.progress)
                    || b.hash()? != entry.intent
            } else if let Some(f) = &entry.fan {
                entry.requested.is_some()
                    || entry.destination != 0
                    || !f.valid_progress(entry.progress)
                    || f.hash()? != entry.intent
            } else {
                !valid_progress(entry.destination, entry.progress)
                    || digest(&serde_json::to_vec(&entry.requested)?) != entry.intent
            }
        {
            return Err(deny());
        }
        if owned.as_ref().is_some_and(|owner| {
            entry.owner != owner.owner
                || entry.user_sid != owner.user_sid
                || entry.session != owner.session
                || entry.fan.is_none()
                    && !((entry.battery.is_some() || entry.tfc.is_some())
                        && owner
                            .fan
                            .as_ref()
                            .is_some_and(|f| f.contract == crate::fan_history::CONTRACT))
        }) {
            return Err(deny());
        }
        if let Some(b) = &entry.battery {
            let expected = owned.as_ref().map(battery_fan_binding).transpose()?;
            if b.fan_owner != expected {
                return Err(deny());
            }
            if let Some(f) = owned.as_ref().and_then(|o| o.fan.as_ref())
                && f.preflight
                    .iter()
                    .chain(f.ids.iter())
                    .chain(f.cleanup.iter())
                    .flatten()
                    .any(|id| b.ids.contains(id))
            {
                return Err(deny());
            }
        }
        if let Some(t) = &entry.tfc {
            if let Some(origin) = &t.origin {
                let original = temporary_owner.as_ref().ok_or_else(deny)?;
                let enabled = original.tfc.as_ref().ok_or_else(deny)?;
                if origin.request != original.request
                    || origin.intent != original.intent
                    || entry.owner != original.owner
                    || entry.user_sid != original.user_sid
                    || entry.caller_pid != original.caller_pid
                    || entry.session != original.session
                    // Reserved IDs bind the cancel itself; fresh IDs name the
                    // reserved set they replace (`validate` keeps them apart).
                    || enabled.cancel_ids != Some(t.superseded_ids.unwrap_or(t.ids))
                    || replaced_by.as_ref().is_some_and(|r| *r != entry.request)
                {
                    return Err(deny());
                }
                if t.superseded_ids.is_some() {
                    replaced_by.get_or_insert_with(|| entry.request.clone());
                }
            }
            if t.fan_owner != owned.as_ref().map(battery_fan_binding).transpose()? {
                return Err(deny());
            }
            if let Some(f) = owned.as_ref().and_then(|o| o.fan.as_ref())
                && f.preflight
                    .iter()
                    .chain(f.ids.iter())
                    .chain(f.cleanup.iter())
                    .flatten()
                    .any(|id| {
                        t.ids.contains(id)
                            || t.cancel_ids.as_ref().is_some_and(|ids| ids.contains(id))
                    })
            {
                return Err(deny());
            }
        }
        let resumed_release = matches!(entry.progress, 31 | 51)
            && owned.as_ref().is_some_and(|owner| {
                let mut resumed = owner.clone();
                resumed.progress = entry.progress;
                serde_json::to_vec(&resumed).ok() == serde_json::to_vec(&entry).ok()
            });
        if let Some(p) = &last {
            if p.request == entry.request {
                if p.owner != entry.owner
                    || p.user_sid != entry.user_sid
                    || p.caller_pid != entry.caller_pid
                    || p.session != entry.session
                    || p.destination != entry.destination
                    || p.intent != entry.intent
                    || p.tfc != entry.tfc
                    || p.battery != entry.battery
                    || p.fan != entry.fan
                    || !entry.fan.as_ref().map_or_else(
                        || {
                            entry.battery.as_ref().map_or_else(
                                || {
                                    entry.tfc.as_ref().map_or_else(
                                        || {
                                            progress_follows(
                                                entry.destination,
                                                p.progress,
                                                entry.progress,
                                            )
                                        },
                                        |t| t.follows(p.progress, entry.progress),
                                    )
                                },
                                |b| b.follows(p.progress, entry.progress),
                            )
                        },
                        |f| f.follows(p.progress, entry.progress),
                    )
                {
                    return Err(deny());
                }
            } else if !settled(p.progress)
                || !resumed_release
                    && (entry.progress != 0
                        || p.progress == 30
                            && (entry.fan.is_none()
                                && !((entry.battery.is_some() || entry.tfc.is_some())
                                    && p.fan.as_ref().is_some_and(|f| {
                                        f.contract == crate::fan_history::CONTRACT
                                    }))
                                || entry.owner != p.owner
                                || entry.user_sid != p.user_sid
                                || entry.session != p.session))
            {
                return Err(deny());
            }
        } else if entry.progress != 0 {
            return Err(deny());
        }
        if entry.fan.is_some() {
            if entry.progress == 30 {
                owned = Some(entry.clone());
            } else if matches!(entry.progress, 50 | 51) {
                owned = None;
            }
        }
        if let Some(t) = &entry.tfc
            && matches!(entry.progress, 72 | 73)
        {
            temporary_owner = t.desired.temporary.then(|| entry.clone());
            replaced_by = None;
        }
        visit(&entry, line)?;
        count = count.checked_add(1).ok_or_else(deny)?;
        last = Some(entry);
    }
    let owners = Owners {
        fan: owned,
        temporary: temporary_owner,
    };
    Ok((count, last, owners))
}
fn lines(header: &str, fields: &[String]) -> Vec<u8> {
    format!("{}\n{}\n", header, fields.join("\n")).into_bytes()
}
fn parse<'a>(bytes: &'a [u8], header: &str, n: usize) -> io::Result<Vec<&'a str>> {
    if bytes.len() > 2048 || bytes.iter().any(|b| *b != 10 && !(32..=126).contains(b)) {
        return Err(deny());
    }
    let f: Vec<_> = std::str::from_utf8(bytes)
        .map_err(|_| deny())?
        .split('\n')
        .collect();
    if f.len() != n + 2 || f[0] != header || !f[n + 1].is_empty() {
        return Err(deny());
    }
    Ok(f[1..=n].to_vec())
}
fn number(v: &str) -> io::Result<u64> {
    let n: u64 = v.parse().map_err(|_| deny())?;
    if n.to_string() != v {
        return Err(deny());
    }
    Ok(n)
}
pub fn name(kind: &str, seq: u64) -> String {
    format!("{kind}-{seq:020}.bin")
}
#[derive(Clone)]
pub struct Checkpoint {
    pub sequence: u64,
    pub previous: String,
    pub segment: String,
    pub length: u64,
    pub records: u64,
}
impl Checkpoint {
    pub fn bytes(&self) -> Vec<u8> {
        lines(
            "OpenBlade.ThermalCheckpoint.v1",
            &[
                self.sequence.to_string(),
                self.previous.clone(),
                self.segment.clone(),
                self.length.to_string(),
                self.records.to_string(),
                CONTRACT.into(),
            ],
        )
    }
    pub fn parse(b: &[u8]) -> io::Result<Self> {
        let f = parse(b, "OpenBlade.ThermalCheckpoint.v1", 6)?;
        let v = Self {
            sequence: number(f[0])?,
            previous: f[1].into(),
            segment: f[2].into(),
            length: number(f[3])?,
            records: number(f[4])?,
        };
        if v.sequence == 0
            || !hex(&v.previous, 64)
            || !hex(&v.segment, 64)
            || v.length == 0
            || v.length > SEGMENT as u64
            || v.records == 0
            || v.records > v.length
            || f[5] != CONTRACT
            || v.bytes() != b
        {
            return Err(deny());
        }
        Ok(v)
    }
}
pub struct View {
    pub physical: Option<Vec<u8>>,
    pub tail: Vec<u8>,
    pub sequence: u64,
    pub head: String,
}
pub fn envelope(sequence: u64, head: &str, tail: &[u8]) -> Vec<u8> {
    let mut b = lines(
        "OpenBlade.ThermalActive.v1",
        &[sequence.to_string(), head.into(), CONTRACT.into()],
    );
    b.push(b'\n');
    b.extend(tail);
    b
}
pub fn active(physical: Option<Vec<u8>>) -> io::Result<View> {
    let b = physical.as_deref().unwrap_or_default();
    if physical.as_ref().is_some_and(Vec::is_empty) {
        return Err(deny());
    }
    if b.starts_with(b"OpenBlade.ThermalActive.") {
        let end = b.windows(2).position(|v| v == b"\n\n").ok_or_else(deny)?;
        let f = parse(&b[..end + 1], "OpenBlade.ThermalActive.v1", 3)?;
        let sequence = number(f[0])?;
        if sequence == 0 || !hex(f[1], 64) || f[2] != CONTRACT || b.len() - end - 2 > SEGMENT {
            return Err(deny());
        }
        let tail = b[end + 2..].to_vec();
        let head = f[1].to_owned();
        Ok(View {
            physical,
            tail,
            sequence,
            head,
        })
    } else {
        if b.len() > SEGMENT {
            return Err(deny());
        }
        let tail = b.to_vec();
        Ok(View {
            physical,
            tail,
            sequence: 0,
            head: "0".repeat(64),
        })
    }
}
/// Names are fixed or checked counters; implementations MUST use protected
/// relative handles under the SAME owner writer, create-new and durable readback.
pub trait Backend {
    fn read(&self, name: &str, max: usize) -> io::Result<Option<Vec<u8>>>;
    fn once(&self, name: &str, bytes: &[u8]) -> io::Result<()>;
    fn publish_active(&self, expected: Option<&[u8]>, bytes: &[u8]) -> io::Result<()>;
    fn namespace(&self, sequence: u64) -> io::Result<()>;
}
fn required(b: &impl Backend, n: &str, max: usize) -> io::Result<Vec<u8>> {
    b.read(n, max)?.ok_or_else(deny)
}
fn exact_once(b: &impl Backend, n: &str, v: &[u8]) -> io::Result<()> {
    match b.read(n, v.len())? {
        Some(old) if old == v => Ok(()),
        Some(_) => Err(deny()),
        None => b.once(n, v),
    }
}
/// A roll record is metadata intent, NEVER a firmware submission pending row.
/// Recovery only finishes its exact old settled tail; partial bytes remain fenced.
pub fn recover(
    b: &impl Backend,
    mut view: View,
    mut join: impl FnMut(&Entry, &[u8]) -> io::Result<()>,
) -> io::Result<View> {
    let next = view.sequence.checked_add(1).ok_or_else(deny)?;
    if let Some(intent) = b.read(&name("roll", next), 2048)? {
        let f = parse(&intent, "OpenBlade.ThermalRoll.v1", 4)?;
        let cp = Checkpoint::parse(&required(b, &name("checkpoint", next), 2048)?)?;
        let segment = required(b, &name("segment", next), SEGMENT)?;
        let (n, last) = rows(&segment, &mut join)?;
        let new = envelope(next, &digest(&cp.bytes()), &[]);
        let old = if view.sequence == 0 {
            segment.clone()
        } else {
            envelope(view.sequence, &view.head, &segment)
        };
        if cp.sequence != next
            || cp.previous != view.head
            || cp.segment != digest(&segment)
            || cp.length != segment.len() as u64
            || cp.records != n
            || !handback_complete(&segment)?
            || last.as_ref().is_none_or(|e| !settled(e.progress))
            || f != [
                digest(&old),
                digest(&new),
                digest(&cp.bytes()),
                CONTRACT.into(),
            ]
            .iter()
            .map(String::as_str)
            .collect::<Vec<_>>()
            || view.physical.as_deref() != Some(&old)
        {
            return Err(deny());
        }
        b.publish_active(Some(&old), &new)?;
        view = active(Some(new))?;
    }
    Ok(view)
}
/// Streaming full-history authentication: one segment + descriptor + last row.
/// No checkpoint replaces original evidence or original owner/producer joins.
pub fn inspect(
    b: &impl Backend,
    view: &View,
    join: impl FnMut(&Entry, &[u8]) -> io::Result<()>,
) -> io::Result<()> {
    inspect_prefix(b, view, join)?;
    b.namespace(view.sequence)
}
pub fn inspect_prefix(
    b: &impl Backend,
    view: &View,
    mut join: impl FnMut(&Entry, &[u8]) -> io::Result<()>,
) -> io::Result<()> {
    let mut previous = "0".repeat(64);
    let mut prior = None;
    for seq in 1..=view.sequence {
        let bytes = required(b, &name("checkpoint", seq), 2048)?;
        let cp = Checkpoint::parse(&bytes)?;
        let segment = required(b, &name("segment", seq), SEGMENT)?;
        let (n, last, _) = rows_from(&segment, prior.take(), &mut join)?;
        if cp.sequence != seq
            || cp.previous != previous
            || cp.segment != digest(&segment)
            || cp.length != segment.len() as u64
            || cp.records != n
            || !handback_complete(&segment)?
            || last.as_ref().is_none_or(|e| !settled(e.progress))
        {
            return Err(deny());
        }
        let intent = required(b, &name("roll", seq), 2048)?;
        let f = parse(&intent, "OpenBlade.ThermalRoll.v1", 4)?;
        let old = if seq == 1 {
            segment.clone()
        } else {
            envelope(seq - 1, &previous, &segment)
        };
        let new = envelope(seq, &digest(&bytes), &[]);
        if f != [digest(&old), digest(&new), digest(&bytes), CONTRACT.into()]
            .iter()
            .map(String::as_str)
            .collect::<Vec<_>>()
        {
            return Err(deny());
        }
        prior = last;
        previous = digest(&bytes);
    }
    if view.head != previous {
        return Err(deny());
    }
    rows_from(&view.tail, prior, &mut join)?;
    Ok(())
}
pub fn validate_prepared_retirement(expected: &[u8], bytes: &[u8], owner: &str) -> io::Result<()> {
    let (n, last) = rows(expected, |_, _| Ok(()))?;
    let mut prior = last.ok_or_else(deny)?;
    if prior.progress != 0 || prior.owner != owner || !bytes.starts_with(expected) {
        return Err(deny());
    }
    prior.progress = 6;
    let mut required = expected.to_vec();
    required.extend(serde_json::to_vec(&prior)?);
    required.push(10);
    let (m, _) = rows(bytes, |_, _| Ok(()))?;
    if n.checked_add(1) != Some(m) || bytes != required {
        return Err(deny());
    }
    Ok(())
}
pub fn handback_complete(bytes: &[u8]) -> io::Result<bool> {
    let (_, _, owners) = rows_with_owners(bytes, |_, _| Ok(()))?;
    Ok(owners.fan.is_none())
}
#[derive(Debug)]
struct PrepublicationCapacity;
impl fmt::Display for PrepublicationCapacity {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str("Thermal progress capacity refused before publication; retained cleanup reservation remains intact.")
    }
}
impl std::error::Error for PrepublicationCapacity {}
pub fn capacity_refusal() -> io::Error {
    io::Error::new(io::ErrorKind::StorageFull, PrepublicationCapacity)
}
pub fn known_capacity_refusal(e: &io::Error) -> bool {
    e.get_ref()
        .is_some_and(|e| e.is::<PrepublicationCapacity>())
}
pub fn reserve(
    b: &impl Backend,
    expected: Option<&[u8]>,
    needed: usize,
    mut join: impl FnMut(&Entry, &[u8]) -> io::Result<()>,
) -> io::Result<Vec<u8>> {
    if needed > SEGMENT {
        return Err(capacity_refusal());
    }
    let view = active(b.read(ACTIVE, SEGMENT + 2048)?)?;
    inspect(b, &view, &mut join)?;
    if view.tail.as_slice() != expected.unwrap_or_default() {
        return Err(deny());
    }
    if view.tail.len().checked_add(needed).ok_or_else(deny)? <= SEGMENT {
        return Ok(view.tail);
    }
    let (records, last) = rows(&view.tail, &mut join)?;
    if !handback_complete(&view.tail)? {
        return Err(capacity_refusal());
    }
    if last.as_ref().is_none_or(|e| !settled(e.progress)) || !handback_complete(&view.tail)? {
        return Err(deny());
    }
    let seq = view.sequence.checked_add(1).ok_or_else(deny)?;
    let cp = Checkpoint {
        sequence: seq,
        previous: view.head,
        segment: digest(&view.tail),
        length: view.tail.len() as u64,
        records,
    };
    let next = envelope(seq, &digest(&cp.bytes()), &[]);
    let intent = lines(
        "OpenBlade.ThermalRoll.v1",
        &[
            digest(view.physical.as_deref().ok_or_else(deny)?),
            digest(&next),
            digest(&cp.bytes()),
            CONTRACT.into(),
        ],
    );
    // Immutable evidence is published before the roll intent. A crash here is
    // classified as incomplete metadata and denies; no Prepared has been written.
    exact_once(b, &name("segment", seq), &view.tail)?;
    exact_once(b, &name("checkpoint", seq), &cp.bytes())?;
    exact_once(b, &name("roll", seq), &intent)?;
    if b.read(ACTIVE, SEGMENT + 2048)? != view.physical {
        return Err(deny());
    }
    b.publish_active(view.physical.as_deref(), &next)?;
    Ok(vec![])
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::{
        cell::{Cell, RefCell},
        collections::BTreeMap,
    };
    #[derive(Default)]
    struct Memory {
        files: RefCell<BTreeMap<String, Vec<u8>>>,
        cut: Cell<usize>,
        step: Cell<usize>,
    }
    impl Memory {
        fn point(&self) -> io::Result<()> {
            let n = self.step.get() + 1;
            self.step.set(n);
            if n == self.cut.get() {
                Err(deny())
            } else {
                Ok(())
            }
        }
    }
    impl Backend for Memory {
        fn read(&self, n: &str, max: usize) -> io::Result<Option<Vec<u8>>> {
            let b = self.files.borrow().get(n).cloned();
            if b.as_ref().is_some_and(|b| b.len() > max) {
                return Err(deny());
            }
            Ok(b)
        }
        fn once(&self, n: &str, b: &[u8]) -> io::Result<()> {
            self.point()?;
            if self.files.borrow().contains_key(n) {
                return Err(deny());
            }
            self.files.borrow_mut().insert(n.into(), b.to_vec());
            self.point()
        }
        fn publish_active(&self, e: Option<&[u8]>, b: &[u8]) -> io::Result<()> {
            if self.files.borrow().get(ACTIVE).map(Vec::as_slice) != e {
                return Err(deny());
            }
            self.point()?;
            self.files.borrow_mut().insert(STAGING.into(), b.to_vec());
            self.point()?;
            self.files.borrow_mut().insert(ACTIVE.into(), b.to_vec());
            self.files.borrow_mut().remove(STAGING);
            self.point()
        }
        fn namespace(&self, seq: u64) -> io::Result<()> {
            let files = self.files.borrow();
            if files.contains_key(STAGING) {
                return Err(deny());
            }
            let mut count = 0u64;
            for k in files.keys().filter(|k| k.as_str() != ACTIVE) {
                if !(1..=seq).any(|n| {
                    ["segment", "checkpoint", "roll"]
                        .iter()
                        .any(|kind| *k == name(kind, n))
                }) {
                    return Err(deny());
                }
                count += 1;
            }
            if count != seq * 3 {
                return Err(deny());
            }
            Ok(())
        }
    }
    fn vector() -> serde_json::Value {
        serde_json::from_str(include_str!(
            "../../../../tools/openblade-windows-host/tests/data/thermal-history-v1.json"
        ))
        .unwrap()
    }
    #[test]
    fn rows_report_the_owners_they_leave_live() {
        let fan: serde_json::Value = serde_json::from_str(include_str!(
            "../../../../tools/openblade-windows-host/tests/data/fan-history-v3.json"
        ))
        .unwrap();
        let tfc: serde_json::Value = serde_json::from_str(include_str!(
            "../../../../tools/openblade-windows-host/tests/data/temporary-charge-history-v1.json"
        ))
        .unwrap();
        let owners = |raw: &serde_json::Value| {
            rows_with_owners(raw.as_str().unwrap().as_bytes(), |_, _| Ok(()))
                .unwrap()
                .2
        };
        let case = |name: &str| {
            let cases = tfc["cases"].as_array().unwrap();
            owners(&cases.iter().find(|c| c["name"] == name).unwrap()["journal"])
        };
        let owned = owners(&fan["owned"]);
        assert_eq!(owned.fan.unwrap().progress, 30);
        assert!(owned.temporary.is_none());
        // A cleanup (50), an observed handback (51) and one after a retired
        // request (6) each end the ownership.
        for key in [
            "released",
            "observedAutomatic",
            "retiredThenObservedAutomatic",
        ] {
            assert!(owners(&fan[key]).fan.is_none(), "{key}");
        }
        let enabled = case("enable").temporary.unwrap();
        assert_eq!(
            (enabled.progress, enabled.tfc.unwrap().cancel_ids),
            (72, Some(core::array::from_fn(|i| 108 + i as u8)))
        );
        // A known-unsent cancel (6) leaves the enable live, and a settled
        // cancel ends it.
        let unsent = tfc["cases"]
            .as_array()
            .unwrap()
            .iter()
            .find(|c| c["name"] == "source-cancel-fresh-ids-after-known-unsent");
        let rows: Vec<&str> = unsent.unwrap()["journal"]
            .as_str()
            .unwrap()
            .split_inclusive('\n')
            .collect();
        let before_retry = rows[..6].concat();
        let live = rows_with_owners(before_retry.as_bytes(), |_, _| Ok(()))
            .unwrap()
            .2;
        assert_eq!(live.temporary.unwrap().progress, 72);
        for name in [
            "source-cancel-fresh-ids-after-known-unsent",
            "cancel-off-unknown-policy",
            "fresh-ids-bound-resets-at-next-enable",
        ] {
            assert!(case(name).temporary.is_none(), "{name}");
        }
    }
    /// The managed reader consumes the same journals.
    #[test]
    fn custom_rows_carry_their_settings_and_four_setters() {
        let v = vector();
        let custom = &v["customRows"];
        let requested: PerformanceOverride =
            serde_json::from_str(custom["requested"].as_str().unwrap()).unwrap();
        requested.validate().unwrap();
        assert_eq!(
            serde_json::to_string(&requested).unwrap(),
            custom["requested"].as_str().unwrap()
        );
        assert_eq!(
            digest(&serde_json::to_vec(&requested).unwrap()),
            custom["intent"].as_str().unwrap()
        );
        for (key, admitted) in [("admitted", true), ("refused", false)] {
            for case in custom[key].as_array().unwrap() {
                let journal = case["journal"].as_str().unwrap().as_bytes();
                assert_eq!(
                    rows(journal, |_, _| Ok(())).is_ok(),
                    admitted,
                    "{}",
                    case["name"]
                );
            }
        }
        // CPU Boost, GPU High and maximum fans are written settings.
        let boundary = &custom["boundary"];
        let settings = boundary["requested"].as_str().unwrap();
        let requested: PerformanceOverride = serde_json::from_str(settings).unwrap();
        requested.validate().unwrap();
        assert_eq!(serde_json::to_string(&requested).unwrap(), settings);
        assert_eq!(
            digest(&serde_json::to_vec(&requested).unwrap()),
            boundary["intent"].as_str().unwrap()
        );
        let complete = custom["admitted"][0]["journal"]
            .as_str()
            .unwrap()
            .replace(custom["requested"].as_str().unwrap(), settings)
            .replace(
                custom["intent"].as_str().unwrap(),
                boundary["intent"].as_str().unwrap(),
            );
        assert!(complete.contains("\"cpu\":3"));
        rows(complete.as_bytes(), |_, _| Ok(())).unwrap();
    }
    /// Leaving HyperBoost for a battery preset journals the restore pair as
    /// 80-83 first; a reader without the restore sequence refuses every such
    /// journal. The managed reader consumes the same journals.
    #[test]
    fn hyperboost_exit_rows_journal_the_restore_pair_first() {
        let v = vector();
        let rows_vector = &v["hyperBoostExitRows"];
        for (key, admitted) in [("admitted", true), ("refused", false)] {
            let cases = rows_vector[key].as_array().unwrap();
            assert!(!cases.is_empty());
            for case in cases {
                let journal = case["journal"].as_str().unwrap().as_bytes();
                assert_eq!(
                    rows(journal, |_, _| Ok(())).is_ok(),
                    admitted && cfg!(feature = "hyperboost-exit"),
                    "{}",
                    case["name"]
                );
            }
        }
    }
    #[test]
    fn canonical_shared_grammar_and_original_generation_commitment() {
        let v = vector();
        let segment = v["segment"].as_str().unwrap().as_bytes();
        // Every admitted RZ09-0581 pair destination (Silent, Balanced,
        // Performance, Battery Saver, Battery Balanced) is a valid row; Custom
        // without its settings and out-of-range values are denied with a
        // valid intent digest.
        for (key, admitted) in [("admitted", true), ("refused", false)] {
            for row in v["presetRows"][key].as_array().unwrap() {
                let text = std::str::from_utf8(segment)
                    .unwrap()
                    .replace(
                        "\"requested\":{\"value\":1,",
                        &format!("\"requested\":{{\"value\":{},", row["value"]),
                    )
                    .replace(
                        v["intent"].as_str().unwrap(),
                        row["intent"].as_str().unwrap(),
                    )
                    .replace(
                        "\"destination\":1",
                        &format!("\"destination\":{}", row["destination"]),
                    );
                assert_eq!(text.matches(row["intent"].as_str().unwrap()).count(), 2);
                assert_eq!(
                    rows(text.as_bytes(), |_, _| Ok(())).is_ok(),
                    admitted,
                    "{row}"
                );
            }
        }
        // A destination alone still needs its own valid value range.
        for destination in [6, -1] {
            let text = std::str::from_utf8(segment).unwrap().replace(
                "\"destination\":1",
                &format!("\"destination\":{destination}"),
            );
            assert!(rows(text.as_bytes(), |_, _| Ok(())).is_err());
        }
        let cp = Checkpoint::parse(v["checkpoint"].as_str().unwrap().as_bytes()).unwrap();
        assert_eq!(cp.segment, digest(segment));
        assert_eq!(cp.records, 2);
        let view = active(Some(v["active"].as_str().unwrap().as_bytes().to_vec())).unwrap();
        assert_eq!(view.sequence, 1);
        assert_eq!(view.head, digest(&cp.bytes()));
        let mut commitment = digest(&lines(
            "OpenBlade.ThermalGeneration.v1",
            &[
                "1".repeat(32),
                "2".repeat(32),
                format!("{}-service", "3".repeat(64)),
            ],
        ));
        rows(segment, |e, raw| {
            assert_eq!(e.intent, v["intent"].as_str().unwrap());
            commitment = digest(&lines(
                "OpenBlade.ThermalGenerationRow.v1",
                &[
                    commitment.clone(),
                    digest(raw),
                    digest(v["owner"].as_str().unwrap().as_bytes()),
                ],
            ));
            Ok(())
        })
        .unwrap();
        assert_eq!(commitment, v["generation"].as_str().unwrap());
        for bad in v["rejectedRows"].as_array().unwrap() {
            assert!(rows(bad.as_str().unwrap().as_bytes(), |_, _| Ok(())).is_err());
        }
        let b = Memory::default();
        for (n, k) in [
            ("segment", "segment"),
            ("checkpoint", "checkpoint"),
            ("roll", "roll"),
        ] {
            b.files
                .borrow_mut()
                .insert(name(n, 1), v[k].as_str().unwrap().as_bytes().to_vec());
        }
        b.files
            .borrow_mut()
            .insert(ACTIVE.into(), view.physical.clone().unwrap());
        inspect(&b, &view, |_, _| Ok(())).unwrap();
    }
    #[test]
    fn metadata_cuts_never_lose_original_bytes_or_create_firmware_settlement() {
        let v = vector();
        let original = v["segment"].as_str().unwrap().as_bytes().to_vec();
        for cut in 1..=9 {
            let b = Memory::default();
            b.files.borrow_mut().insert(ACTIVE.into(), original.clone());
            b.cut.set(cut);
            assert!(reserve(&b, Some(&original), SEGMENT, |_, _| Ok(())).is_err());
            b.cut.set(0);
            let view = active(b.read(ACTIVE, SEGMENT + 2048).unwrap()).unwrap();
            let recovered = recover(&b, view, |_, _| Ok(()));
            if cut >= 6 {
                let recovered = recovered.unwrap();
                inspect(&b, &recovered, |_, _| Ok(())).unwrap();
                assert_eq!(recovered.sequence, 1);
                assert!(recovered.tail.is_empty());
            }
            assert!(
                b.files.borrow().get(ACTIVE) == Some(&original)
                    || b.files.borrow().get(&name("segment", 1)) == Some(&original)
            );
            assert_eq!(
                rows(&original, |_, _| Ok(())).unwrap().1.unwrap().progress,
                6
            );
        }
    }
    #[test]
    fn unresolved_foreign_missing_and_oversized_capacity_remain_fenced() {
        let v = vector();
        let original = v["segment"].as_str().unwrap().as_bytes().to_vec();
        let b = Memory::default();
        b.files.borrow_mut().insert(ACTIVE.into(), original.clone());
        let before = b.files.borrow().clone();
        assert!(reserve(&b, Some(&original), SEGMENT + 1, |_, _| Ok(())).is_err());
        assert_eq!(*b.files.borrow(), before);
        assert!(reserve(&b, Some(&original), SEGMENT, |_, _| Err(deny())).is_err());
        assert_eq!(*b.files.borrow(), before);
        reserve(&b, Some(&original), SEGMENT, |_, _| Ok(())).unwrap();
        let mut crossed = active(b.read(ACTIVE, SEGMENT + 2048).unwrap()).unwrap();
        crossed.tail = original.clone();
        assert!(
            inspect(&b, &crossed, |_, _| Ok(())).is_err(),
            "same request cannot restart at a segment boundary"
        );
        for kind in ["segment", "checkpoint", "roll"] {
            let file = b.files.borrow_mut().remove(&name(kind, 1)).unwrap();
            let view = active(b.read(ACTIVE, SEGMENT + 2048).unwrap()).unwrap();
            assert!(inspect(&b, &view, |_, _| Ok(())).is_err());
            b.files.borrow_mut().insert(name(kind, 1), file);
        }
        b.files.borrow_mut().insert("foreign.bin".into(), vec![1]);
        let view = active(b.read(ACTIVE, SEGMENT + 2048).unwrap()).unwrap();
        assert!(inspect(&b, &view, |_, _| Ok(())).is_err());
        let prepared = original[..original.iter().position(|b| *b == 10).unwrap() + 1].to_vec();
        let b = Memory::default();
        b.files.borrow_mut().insert(ACTIVE.into(), prepared.clone());
        assert!(reserve(&b, Some(&prepared), SEGMENT, |_, _| Ok(())).is_err());
        assert_eq!(b.files.borrow().len(), 1);
    }
    #[test]
    fn battery_correction_complete_rehashed_vectors_agree_for_active_and_archived_history() {
        let v: serde_json::Value = serde_json::from_str(include_str!(
            "../../../../tools/openblade-windows-host/tests/data/battery-history-v1.json"
        ))
        .unwrap();
        for case in v["corrections"].as_array().unwrap() {
            let raw = case["journal"].as_str().unwrap().as_bytes();
            let valid = case["valid"].as_bool().unwrap();
            assert_eq!(digest(raw), case["sha256"].as_str().unwrap());
            for rolled in [false, true] {
                let raw = if rolled {
                    case["archivedJournal"].as_str().unwrap().as_bytes()
                } else {
                    raw
                };
                let b = Memory::default();
                let physical = if rolled {
                    let cp = Checkpoint {
                        sequence: 1,
                        previous: "0".repeat(64),
                        segment: digest(raw),
                        length: raw.len() as u64,
                        records: raw.iter().filter(|&&c| c == b'\n').count() as u64,
                    }
                    .bytes();
                    let empty = envelope(1, &digest(&cp), &[]);
                    let roll = lines(
                        "OpenBlade.ThermalRoll.v1",
                        &[digest(raw), digest(&empty), digest(&cp), CONTRACT.into()],
                    );
                    for (n, bytes) in [
                        ("segment", raw.to_vec()),
                        ("checkpoint", cp.clone()),
                        ("roll", roll),
                    ] {
                        b.files.borrow_mut().insert(name(n, 1), bytes);
                    }
                    empty
                } else {
                    raw.to_vec()
                };
                b.files.borrow_mut().insert(ACTIVE.into(), physical.clone());
                let before = b.files.borrow().clone();
                let view = active(Some(physical)).unwrap();
                let result = inspect(&b, &view, |e, _| {
                    assert_eq!(e.owner, "7".repeat(64));
                    Ok(())
                });
                assert_eq!(result.is_ok(), valid, "{} rolled={rolled}", case["name"]);
                assert_eq!(
                    *b.files.borrow(),
                    before,
                    "rejection must not rewrite history"
                );
            }
        }
    }
    #[test]
    fn battery_correction_completed_terminals_roll_and_recover_without_settling_pending_or_owned_rows()
     {
        let v: serde_json::Value = serde_json::from_str(include_str!(
            "../../../../tools/openblade-windows-host/tests/data/battery-history-v1.json"
        ))
        .unwrap();
        let confirmed = v["standalone"].as_str().unwrap().as_bytes().to_vec();
        let mut entry: Entry =
            serde_json::from_slice(confirmed.split(|b| *b == b'\n').next().unwrap()).unwrap();
        let battery = entry.battery.as_mut().unwrap();
        battery.baseline = battery.target;
        entry.intent = battery.hash().unwrap();
        let mut unchanged = Vec::new();
        for progress in [0, 63] {
            entry.progress = progress;
            unchanged.extend(serde_json::to_vec(&entry).unwrap());
            unchanged.push(b'\n');
        }
        for raw in [&confirmed, &unchanged] {
            assert!(handback_complete(raw).unwrap());
            for interrupted in [false, true] {
                let b = Memory::default();
                b.files.borrow_mut().insert(ACTIVE.into(), raw.to_vec());
                if interrupted {
                    b.cut.set(6);
                }
                let result = reserve(&b, Some(raw), SEGMENT, |_, _| Ok(()));
                if interrupted {
                    assert!(result.is_err());
                    assert_eq!(b.files.borrow().get(ACTIVE).unwrap(), raw);
                    b.cut.set(0);
                    let view =
                        recover(&b, active(Some(raw.to_vec())).unwrap(), |_, _| Ok(())).unwrap();
                    inspect(&b, &view, |_, _| Ok(())).unwrap();
                } else {
                    assert!(result.unwrap().is_empty());
                    inspect(
                        &b,
                        &active(b.read(ACTIVE, SEGMENT + 2048).unwrap()).unwrap(),
                        |_, _| Ok(()),
                    )
                    .unwrap();
                }
                assert_eq!(b.files.borrow().get(&name("segment", 1)).unwrap(), raw);
                let cp = Checkpoint::parse(b.files.borrow().get(&name("checkpoint", 1)).unwrap())
                    .unwrap();
                assert_eq!(cp.segment, digest(raw));
                assert_eq!(cp.length, raw.len() as u64);
                assert_eq!(
                    b.files.borrow().get(ACTIVE).unwrap(),
                    &envelope(1, &digest(&cp.bytes()), &[])
                );
            }
        }
        let owned = v["interleaved"]
            .as_str()
            .unwrap()
            .lines()
            .take_while(|line| !line.contains("\"battery\":"))
            .map(|line| format!("{line}\n"))
            .collect::<String>()
            .into_bytes();
        for raw in [
            confirmed
                .split_inclusive(|b| *b == b'\n')
                .take(2)
                .flatten()
                .copied()
                .collect::<Vec<_>>(),
            confirmed
                .split_inclusive(|b| *b == b'\n')
                .take(3)
                .flatten()
                .copied()
                .collect(),
            owned,
        ] {
            let b = Memory::default();
            b.files.borrow_mut().insert(ACTIVE.into(), raw.clone());
            assert!(reserve(&b, Some(&raw), SEGMENT, |_, _| Ok(())).is_err());
            assert_eq!(b.files.borrow().len(), 1);
            assert_eq!(b.files.borrow().get(ACTIVE).unwrap(), &raw);
            let cp = Checkpoint {
                sequence: 1,
                previous: "0".repeat(64),
                segment: digest(&raw),
                length: raw.len() as u64,
                records: raw.iter().filter(|&&c| c == b'\n').count() as u64,
            }
            .bytes();
            let empty = envelope(1, &digest(&cp), &[]);
            let intent = lines(
                "OpenBlade.ThermalRoll.v1",
                &[digest(&raw), digest(&empty), digest(&cp), CONTRACT.into()],
            );
            for (n, bytes) in [
                ("segment", raw.clone()),
                ("checkpoint", cp),
                ("roll", intent),
            ] {
                b.files.borrow_mut().insert(name(n, 1), bytes);
            }
            let before = b.files.borrow().clone();
            assert!(recover(&b, active(Some(raw)).unwrap(), |_, _| Ok(())).is_err());
            assert!(inspect(&b, &active(Some(empty)).unwrap(), |_, _| Ok(())).is_err());
            assert_eq!(*b.files.borrow(), before);
        }
    }
}

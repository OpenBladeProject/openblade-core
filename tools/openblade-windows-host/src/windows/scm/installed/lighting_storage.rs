//! The saved lighting override: managed `PersistentOverrideState.Lighting`
//! in `manual-overrides.json`, and `Automation`'s temporary one, which lapses
//! at a power-source change as the thermal temporaries do.
//!
//! The document is otherwise a `serde_json::Value`, which sorts members.
//! Lighting is kept typed and written by the domain writer, which keeps
//! managed's member order, `kind` first in `parameters` included.
use super::{OVERRIDE_SCHEMA, State, Storage, denied, settled};
use crate::service_status::{LIGHTING_PERSISTENT, LightingOverride};
use openblade_domain::{
    automation::temporary_expired,
    profile_set::{
        Json, LightingSettings, decode, encode, json,
        lighting::{is_well_formed, normalize_legacy_visibility},
    },
};
use serde_json::{Value, json};
use std::io;

/// Stands in for the lighting member while serde writes the document; it is
/// an ASCII string no other member of the validated document can hold.
const PLACEHOLDER: &str = "@@openblade-lighting-override@@";

/// The saved lighting in `bytes`, which `overrides` admitted as `document`,
/// as managed's store loads it without migrating: an object `IsWellFormed`
/// accepts that managed's visibility normalization leaves unchanged, at the
/// current schema. The member becomes null in `document`; the typed value is
/// the record. A document managed would migrate is preserved for managed.
pub(super) fn saved(
    bytes: Option<&[u8]>,
    document: &mut Value,
) -> io::Result<Option<LightingSettings>> {
    if document["overrides"]["lighting"].is_null() {
        return Ok(None);
    }
    let preserved = |reason: &str| io::Error::new(io::ErrorKind::Unsupported, reason.to_owned());
    if document["schemaVersion"].as_i64() != Some(OVERRIDE_SCHEMA) {
        return Err(preserved(
            "Saved lighting in an older override schema requires managed migration; bytes are preserved.",
        ));
    }
    let parsed = json::parse(bytes.ok_or_else(denied)?).map_err(|_| denied())?;
    let member = |json: &Json, name: &str| {
        json.members()
            .and_then(|members| members.iter().find(|(key, _)| key == name))
            .map(|(_, value)| value.clone())
    };
    let lighting = member(&parsed, "overrides")
        .and_then(|overrides| member(&overrides, "lighting"))
        .ok_or_else(denied)?;
    let settings = decode::lighting(&lighting).map_err(|_| denied())?;
    // Managed normalizes before it validates, and rewrites a changed value.
    let mut normalized = settings.clone();
    normalize_legacy_visibility(&mut normalized);
    if normalized != settings {
        return Err(preserved(
            "Saved lighting requires managed visibility migration; bytes are preserved.",
        ));
    }
    if !is_well_formed(&settings) {
        return Err(denied());
    }
    document["overrides"]["lighting"] = Value::Null;
    Ok(Some(settings))
}

/// The bytes of `document` with `lighting` as its lighting member, normalized
/// as managed `SaveAsync` normalizes every saved override
/// (`NormalizeLegacyLighting`).
pub(super) fn document_bytes(
    document: &Value,
    lighting: Option<&LightingSettings>,
) -> io::Result<Vec<u8>> {
    let Some(lighting) = lighting else {
        return Ok(serde_json::to_vec(document)?);
    };
    let mut next = document.clone();
    next["overrides"]["lighting"] = json!(PLACEHOLDER);
    let text = serde_json::to_string(&next)?;
    let quoted = format!("\"{PLACEHOLDER}\"");
    if text.matches(&quoted).count() != 1 {
        return Err(denied());
    }
    let mut lighting = lighting.clone();
    normalize_legacy_visibility(&mut lighting);
    let encoded = String::from_utf8(json::write(&encode::lighting(&lighting), false))
        .map_err(|_| denied())?;
    Ok(text.replacen(&quoted, &encoded, 1).into_bytes())
}

impl Storage {
    /// Managed `SetLightingOverrideCoreAsync`'s store: a persistent value is
    /// saved, and anything else clears the saved one; the bytes hold it
    /// normalized (`document_bytes`) and the store keeps it as requested, as
    /// managed's in-memory state does; a temporary value is
    /// held for the power class `power` it was requested at. The document is
    /// replaced only when its bytes change (`PersistOverridesIfChangedAsync`),
    /// and a failed replacement quarantines the store as every override
    /// save does.
    pub(in super::super) fn save_lighting(
        &self,
        value: Option<&LightingOverride>,
        power: i32,
    ) -> io::Result<()> {
        super::aggregate(self.files.as_ref(), self.same_path)?;
        let mut s = self.state.lock().map_err(|_| denied())?;
        // The override document is shared with the thermal journal's
        // intent, so a lighting save waits on it as every override save does.
        if s.quarantined || s.last.as_ref().is_some_and(|e| !settled(e.progress)) {
            return Err(denied());
        }
        let persisted = value
            .filter(|v| v.lifetime == LIGHTING_PERSISTENT)
            .map(|v| v.settings.clone());
        let mut next = s.document.clone();
        next["schemaVersion"] = json!(OVERRIDE_SCHEMA);
        let bytes = document_bytes(&next, persisted.as_ref())?;
        if s.bytes.as_deref() != Some(bytes.as_slice()) {
            if let Err(error) = self
                .files
                .replace(super::OVERRIDES, s.bytes.as_deref(), &bytes)
            {
                s.quarantined = true;
                s.configuration_failed = true;
                return Err(error);
            }
            s.bytes = Some(bytes);
            s.document = next;
        }
        s.lighting = persisted;
        s.temporary_lighting = value
            .filter(|v| v.lifetime != LIGHTING_PERSISTENT)
            .map(|v| (v.settings.clone(), power));
        s.temporary_lighting_generation = s.generation;
        Ok(())
    }

    /// The lighting override in effect at power class `power`: an unexpired
    /// temporary one, otherwise the saved one. A document changed outside
    /// this store quarantines it, as every override read does.
    pub(in super::super) fn current_lighting_override(
        &self,
        power: i32,
    ) -> io::Result<Option<LightingOverride>> {
        let mut s = self.state.lock().map_err(|_| denied())?;
        if self.files.read(super::OVERRIDES)? != s.bytes {
            s.quarantined = true;
            s.configuration_changed = true;
            return Err(denied());
        }
        if s.temporary_lighting
            .as_ref()
            .is_some_and(|(_, saved)| temporary_expired(*saved, power))
        {
            s.temporary_lighting = None;
        }
        if let Some((settings, _)) = &s.temporary_lighting {
            return Ok(Some(LightingOverride {
                settings: settings.clone(),
                lifetime: 0,
            }));
        }
        Ok(s.lighting.clone().map(|settings| LightingOverride {
            settings,
            lifetime: LIGHTING_PERSISTENT,
        }))
    }
}

/// Retires or refines the temporary lighting at a fresh power reading
/// (`Storage::observe_power`), as the thermal temporaries are.
pub(super) fn observe_power(s: &mut State, power: i32, generation: u64) {
    s.temporary_lighting = s.temporary_lighting.take().and_then(|(value, saved)| {
        openblade_domain::automation::temporary_after(
            saved,
            s.temporary_lighting_generation,
            power,
            generation,
        )
        .map(|next| (value, next))
    });
}

#[cfg(test)]
mod tests {
    use super::*;

    const OFF: &str = r#"{"effect":0,"color":{"red":0,"green":0,"blue":0},"brightness":0,"parameters":{"kind":"off"},"logoMode":null}"#;
    const STATIC: &str = r#"{"effect":1,"color":{"red":200,"green":10,"blue":5},"brightness":10,"parameters":{"kind":"static","color":{"red":200,"green":10,"blue":5}},"logoMode":null}"#;

    fn document(lighting: &str, schema: i64) -> String {
        format!(
            r#"{{"schemaVersion":{schema},"overrides":{{"performance":null,"fans":null,"lighting":{lighting},"customPerformance":null,"fanControl":null,"gpuClockTuning":null}}}}"#
        )
    }

    fn load(text: &str) -> io::Result<(Value, Option<LightingSettings>)> {
        let mut value = super::super::overrides(Some(text.as_bytes()))?;
        let lighting = saved(Some(text.as_bytes()), &mut value)?;
        Ok((value, lighting))
    }

    /// A saved document round-trips byte for byte, `kind` first, which a
    /// sorted `Value` would not keep.
    #[test]
    fn saved_lighting_round_trips_in_managed_member_order() {
        for lighting in [OFF, STATIC] {
            let text = document(lighting, OVERRIDE_SCHEMA);
            let (value, settings) = load(&text).unwrap();
            assert!(value["overrides"]["lighting"].is_null());
            let bytes = document_bytes(&value, settings.as_ref()).unwrap();
            let written = String::from_utf8(bytes).unwrap();
            assert!(written.contains(lighting), "{written}");
            assert_eq!(load(&written).unwrap().1, settings);
        }
        let (value, settings) = load(&document("null", OVERRIDE_SCHEMA)).unwrap();
        assert_eq!(settings, None);
        assert_eq!(
            document_bytes(&value, None).unwrap(),
            serde_json::to_vec(&value).unwrap()
        );
    }

    /// Managed `PersistentOverrideStore` rejects an ill-formed value and
    /// migrates an older or visibility-normalized one; native refuses the
    /// first and preserves the bytes of the others for managed.
    #[test]
    fn saved_lighting_managed_would_migrate_or_reject_is_refused() {
        let hidden = r#"{"effect":1,"color":{"red":200,"green":10,"blue":5},"brightness":0,"parameters":{"kind":"static","color":{"red":200,"green":10,"blue":5}},"logoMode":null}"#;
        let ill_formed = r#"{"effect":0,"color":{"red":0,"green":0,"blue":0},"brightness":9,"parameters":{"kind":"off"},"logoMode":null}"#;
        for (text, kind) in [
            (
                document(hidden, OVERRIDE_SCHEMA),
                io::ErrorKind::Unsupported,
            ),
            (document(OFF, 9), io::ErrorKind::Unsupported),
            (
                document(ill_formed, OVERRIDE_SCHEMA),
                io::ErrorKind::PermissionDenied,
            ),
        ] {
            assert_eq!(load(&text).unwrap_err().kind(), kind, "{text}");
        }
    }
}

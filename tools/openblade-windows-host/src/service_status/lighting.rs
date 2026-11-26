//! The `value` member of `setLightingOverride`: managed
//! `ManualOverride<LightingSettings>?`.
//!
//! serde only steps over the member. The typed override is bound from the
//! frame bytes by the domain reader, which keeps the member order that
//! managed's polymorphic `parameters` binding depends on and serde drops.
use serde::{Deserialize, Deserializer, Serialize, Serializer, de::IgnoredAny};

#[cfg(feature = "service-lighting-v1")]
use openblade_domain::profile_set::{
    Json, LightingSettings, decode, encode, json,
    lighting::{is_well_formed, normalize_legacy_off},
};

/// Managed `OverrideLifetime.Persistent`; `UntilPowerSourceChange` is 0.
#[cfg(feature = "service-lighting-v1")]
pub(crate) const PERSISTENT: i32 = 1;

/// A lighting override as managed's codec admits it (`IpcMessageCodec`:
/// a defined lifetime and well-formed settings), with managed
/// `SetLightingOverrideAsync`'s legacy Off normalization applied.
#[cfg(feature = "service-lighting-v1")]
#[derive(Clone, Debug, PartialEq)]
pub(crate) struct LightingOverride {
    pub(crate) settings: LightingSettings,
    pub(crate) lifetime: i32,
}

#[cfg(feature = "service-lighting-v1")]
impl LightingOverride {
    /// The managed wire record, `value` before `lifetime`.
    pub(crate) fn encode(&self) -> Json {
        Json::Object(vec![
            ("value".into(), encode::lighting(&self.settings)),
            ("lifetime".into(), Json::int(self.lifetime.into())),
        ])
    }
}

#[derive(Clone, Default)]
pub(crate) struct LightingBody(#[cfg(feature = "service-lighting-v1")] Option<LightingOverride>);

impl<'de> Deserialize<'de> for LightingBody {
    fn deserialize<D: Deserializer<'de>>(deserializer: D) -> Result<Self, D::Error> {
        IgnoredAny::deserialize(deserializer).map(|_| Self::default())
    }
}

impl Serialize for LightingBody {
    fn serialize<S: Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        #[cfg(feature = "service-lighting-v1")]
        if let Some(value) = &self.0 {
            let bytes = json::write(&value.encode(), false);
            return serde_json::from_slice::<serde_json::Value>(&bytes)
                .map_err(serde::ser::Error::custom)?
                .serialize(serializer);
        }
        serializer.serialize_unit()
    }
}

#[cfg(feature = "service-lighting-v1")]
impl LightingBody {
    /// Binds the request's `value` member from the frame bytes as managed's
    /// codec admits it: null, or exactly `value` and `lifetime`, with a
    /// defined lifetime and settings `IsWellFormed` accepts.
    pub(crate) fn bind(payload: &[u8]) -> std::io::Result<Self> {
        let refused = || crate::service_frame::invalid("invalid Service lighting override");
        let document = json::parse(payload).map_err(|_| refused())?;
        let member = |json: &Json, name: &str| {
            json.members()
                .and_then(|members| members.iter().find(|(key, _)| key == name))
                .map(|(_, value)| value.clone())
        };
        let value = member(&document, "value").ok_or_else(refused)?;
        if value == Json::Null {
            return Ok(Self(None));
        }
        let members = value.members().ok_or_else(refused)?;
        if members.len() != 2 {
            return Err(refused());
        }
        let lifetime = match member(&value, "lifetime") {
            Some(Json::Number(text)) if text == "0" || text == "1" => {
                text.parse().map_err(|_| refused())?
            }
            _ => return Err(refused()),
        };
        let settings = member(&value, "value").ok_or_else(refused)?;
        let mut settings = decode::lighting(&settings).map_err(|_| refused())?;
        if !is_well_formed(&settings) {
            return Err(refused());
        }
        normalize_legacy_off(&mut settings);
        Ok(Self(Some(LightingOverride { settings, lifetime })))
    }

    pub(crate) fn value(&self) -> Option<&LightingOverride> {
        self.0.as_ref()
    }
}

#[cfg(test)]
impl LightingBody {
    /// A cleared override: the body every lane can frame.
    pub(crate) fn cleared() -> Self {
        Self::default()
    }
}

#[cfg(all(test, feature = "service-lighting-v1"))]
mod tests {
    use super::*;

    fn bind(value: &str) -> std::io::Result<LightingBody> {
        LightingBody::bind(
            format!(
                r#"{{"operation":"setLightingOverride","version":91,"requestId":"0b7b2cba-63d1-4b8e-8d1f-8d5ad8b61c4a","value":{value}}}"#
            )
            .as_bytes(),
        )
    }

    const OFF: &str = r#"{"effect":0,"color":{"red":0,"green":0,"blue":0},"brightness":0,"parameters":{"kind":"off"},"logoMode":null}"#;

    /// Managed `IpcMessageCodec`: a null value, or a defined lifetime with
    /// well-formed settings.
    #[test]
    fn binds_only_what_the_managed_codec_admits() {
        assert_eq!(bind("null").unwrap().value(), None);
        for lifetime in [0, 1] {
            let body = bind(&format!(r#"{{"value":{OFF},"lifetime":{lifetime}}}"#)).unwrap();
            let value = body.value().unwrap();
            assert_eq!(value.lifetime, lifetime);
            assert_eq!(value.settings, LightingSettings::off());
        }
        for refused in [
            format!(r#"{{"value":{OFF},"lifetime":2}}"#),
            format!(r#"{{"value":{OFF},"lifetime":1.0}}"#),
            format!(r#"{{"value":{OFF}}}"#),
            format!(r#"{{"value":{OFF},"lifetime":1,"extra":1}}"#),
            // Off with a lit brightness is not well formed.
            r#"{"value":{"effect":0,"color":{"red":0,"green":0,"blue":0},"brightness":5,"parameters":{"kind":"off"}},"lifetime":1}"#.into(),
            r#""off""#.into(),
        ] {
            assert!(bind(&refused).is_err(), "{refused}");
        }
        // A lit effect binds; capability admission refuses it later.
        let lit = r#"{"effect":1,"color":{"red":200,"green":10,"blue":5},"brightness":10,"parameters":{"kind":"static","color":{"red":200,"green":10,"blue":5}},"logoMode":null}"#;
        bind(&format!(r#"{{"value":{lit},"lifetime":1}}"#)).unwrap();
    }

    /// Managed `NormalizeLegacyOff`: Off without parameters gains them.
    #[test]
    fn normalizes_legacy_off_as_managed() {
        let body = bind(
            r#"{"value":{"effect":0,"color":{"red":0,"green":0,"blue":0},"brightness":0},"lifetime":1}"#,
        )
        .unwrap();
        assert_eq!(body.value().unwrap().settings, LightingSettings::off());
    }

    /// The encoded record keeps `kind` first, as managed writes it.
    #[test]
    fn encodes_in_managed_member_order() {
        let body = bind(&format!(r#"{{"value":{OFF},"lifetime":0}}"#)).unwrap();
        let text = String::from_utf8(json::write(&body.value().unwrap().encode(), false)).unwrap();
        assert!(text.starts_with(r#"{"value":{"effect":0"#), "{text}");
        assert!(text.ends_with(r#""lifetime":0}"#), "{text}");
    }
}

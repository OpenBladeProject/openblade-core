//! Object-only JSON decoding for IPC input.
//!
//! serde's derived struct visitor also binds a JSON array by position, and an
//! internally tagged enum reads `["tag", ...]` as a tagged sequence;
//! `deny_unknown_fields` changes neither. The managed System.Text.Json codecs
//! read those types only from a JSON object. Decode every such payload with
//! [`from_slice`], and every nested member whose type is a derived struct with
//! [`object`], [`nullable_object`] or [`Object`]. Scalars, strings, maps and
//! `Value` members need no wrapper. Valid objects decode exactly as before:
//! the wrapper only declines the sequence form.

use serde::{
    Deserialize, Deserializer,
    de::{MapAccess, Visitor, value::MapAccessDeserializer},
};
use std::{fmt, marker::PhantomData};

/// A value read only from a JSON object.
pub struct Object<T>(pub T);

impl<'de, T: Deserialize<'de>> Deserialize<'de> for Object<T> {
    fn deserialize<D: Deserializer<'de>>(deserializer: D) -> Result<Self, D::Error> {
        struct Members<T>(PhantomData<T>);

        impl<'de, T: Deserialize<'de>> Visitor<'de> for Members<T> {
            type Value = T;

            fn expecting(&self, out: &mut fmt::Formatter<'_>) -> fmt::Result {
                out.write_str("a JSON object")
            }

            fn visit_map<M: MapAccess<'de>>(self, map: M) -> Result<T, M::Error> {
                T::deserialize(MapAccessDeserializer::new(map))
            }
        }

        deserializer
            .deserialize_map(Members(PhantomData))
            .map(Object)
    }
}

/// `serde_json::from_slice` for a payload whose root must be a JSON object.
pub fn from_slice<'a, T: Deserialize<'a>>(payload: &'a [u8]) -> serde_json::Result<T> {
    serde_json::from_slice(payload).map(|Object(value)| value)
}

/// `deserialize_with` for a member that must hold an object.
pub fn object<'de, D: Deserializer<'de>, T: Deserialize<'de>>(
    deserializer: D,
) -> Result<T, D::Error> {
    Object::deserialize(deserializer).map(|Object(value)| value)
}

/// `deserialize_with` for a member that holds null or an object. Like any
/// `deserialize_with` member it is required; add `default` to allow absence.
pub fn nullable_object<'de, D: Deserializer<'de>, T: Deserialize<'de>>(
    deserializer: D,
) -> Result<Option<T>, D::Error> {
    Option::<Object<T>>::deserialize(deserializer).map(|value| value.map(|Object(value)| value))
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::{Value, json};

    #[derive(Debug, Deserialize, PartialEq)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct Inner {
        limit: u8,
    }

    #[derive(Debug, Deserialize, PartialEq)]
    #[serde(tag = "operation", rename_all = "camelCase", deny_unknown_fields)]
    enum Tagged {
        Set {
            #[serde(deserialize_with = "object")]
            value: Inner,
            #[serde(default, deserialize_with = "nullable_object")]
            optional: Option<Inner>,
        },
    }

    fn set(limit: u8, optional: Option<u8>) -> Tagged {
        Tagged::Set {
            value: Inner { limit },
            optional: optional.map(|limit| Inner { limit }),
        }
    }

    // The unwrapped derives are the hazard this module exists for.
    #[test]
    fn serde_derives_alone_bind_arrays_by_position() {
        assert_eq!(
            serde_json::from_str::<Inner>("[80]").unwrap(),
            Inner { limit: 80 }
        );
        #[derive(Deserialize)]
        #[serde(tag = "operation", deny_unknown_fields)]
        enum Bare {
            #[serde(rename = "set")]
            Set { _value: Inner },
        }
        assert!(serde_json::from_str::<Bare>(r#"["set",[80]]"#).is_ok());
    }

    #[test]
    fn objects_decode_unchanged_at_every_position() {
        let frame = br#"{"operation":"set","value":{"limit":80},"optional":{"limit":70}}"#;
        assert_eq!(from_slice::<Tagged>(frame).unwrap(), set(80, Some(70)));
        let frame = br#"{"operation":"set","value":{"limit":80},"optional":null}"#;
        assert_eq!(from_slice::<Tagged>(frame).unwrap(), set(80, None));
        let frame = br#" {"operation":"set","value":{"limit":80}} "#;
        assert_eq!(from_slice::<Tagged>(frame).unwrap(), set(80, None));
        // Other deserializers (a buffered Value) take the same path.
        let value = json!({"operation":"set","value":{"limit":1},"optional":null});
        assert_eq!(
            serde_json::from_value::<Object<Tagged>>(value).unwrap().0,
            set(1, None)
        );
    }

    #[test]
    fn arrays_are_refused_at_the_root_and_every_wrapped_member() {
        for frame in [
            r#"["set",{"limit":80},null]"#,
            r#"["set",[80]]"#,
            r#"{"operation":"set","value":[80]}"#,
            r#"{"operation":"set","value":{"limit":80},"optional":[70]}"#,
            r#"[]"#,
            r#""set""#,
            r#"null"#,
            r#"80"#,
        ] {
            assert!(from_slice::<Tagged>(frame.as_bytes()).is_err(), "{frame}");
        }
        // A wrapped member refuses an array however it is buffered.
        let value: Value = serde_json::from_str(r#"{"operation":"set","value":[80]}"#).unwrap();
        assert!(serde_json::from_value::<Tagged>(value).is_err());
    }

    #[test]
    fn trailing_input_and_malformed_members_are_still_refused() {
        for frame in [
            r#"{"operation":"set","value":{"limit":80}} {}"#,
            r#"{"operation":"set","value":{"limit":80},"extra":1}"#,
            r#"{"operation":"set","value":{"limit":"80"}}"#,
            r#"{"operation":"set"}"#,
        ] {
            assert!(from_slice::<Tagged>(frame.as_bytes()).is_err(), "{frame}");
        }
    }
}

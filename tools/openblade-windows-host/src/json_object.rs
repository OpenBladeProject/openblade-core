//! Managed-compatible decoding of untrusted IPC JSON.
//!
//! Two serde behaviours accept input that the managed System.Text.Json codecs
//! refuse:
//!
//! - A derived struct also binds a JSON array by position, and an internally
//!   tagged enum reads `["tag", ...]` as a tagged sequence;
//!   `deny_unknown_fields` changes neither.
//! - With this crate's `arbitrary_precision` and `raw_value` features,
//!   serde_json reads an object whose member name is
//!   `$serde_json::private::Number` or `$serde_json::private::RawValue` as a
//!   number or as re-parsed JSON text. Only member names are reinterpreted; a
//!   string value with that prefix is ordinary text, for example a profile
//!   name or a Display key a user chose.
//!   A `Value`, `Number` or ignored member takes it as such, and the raw text
//!   hides its members from the duplicate-member check. serde also buffers a
//!   non-integer or out-of-range number in that private form inside internally
//!   tagged or flattened content, where a map visitor would take it as a map.
//!
//! Decode every payload with [`from_slice`], which refuses any member name
//! spelling a private token, and every nested member whose type is a derived struct
//! with [`object`], [`nullable_object`] or [`Object`], which refuse arrays and
//! private member names. Scalar, string, map and `Value` members need no
//! wrapper once the payload passed [`from_slice`]. Valid managed frames decode
//! exactly as before.

use serde::{
    Deserialize, Deserializer,
    de::{
        DeserializeSeed, Error, MapAccess, Visitor,
        value::{BorrowedStrDeserializer, MapAccessDeserializer, StringDeserializer},
    },
};
use std::{fmt, marker::PhantomData};

/// The prefix of serde_json's private member names for arbitrary-precision
/// numbers and raw values. Managed reads such a member as an ordinary one.
const PRIVATE_TOKEN: &[u8] = b"$serde_json::private::";

/// A value read only from a JSON object without serde_json private members.
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
                T::deserialize(MapAccessDeserializer::new(PublicMembers(map)))
            }
        }

        deserializer
            .deserialize_map(Members(PhantomData))
            .map(Object)
    }
}

/// Forwards a map, refusing serde_json's private member names. A buffered
/// `1.5` arrives as such a one-member map.
struct PublicMembers<M>(M);

impl<'de, M: MapAccess<'de>> MapAccess<'de> for PublicMembers<M> {
    type Error = M::Error;

    fn next_key_seed<K: DeserializeSeed<'de>>(
        &mut self,
        seed: K,
    ) -> Result<Option<K::Value>, M::Error> {
        let name = match self.0.next_key::<Name<'de>>()? {
            None => return Ok(None),
            Some(name) => name,
        };
        if name.text().as_bytes().starts_with(PRIVATE_TOKEN) {
            return Err(M::Error::custom("a private serde_json member"));
        }
        match name {
            Name::Borrowed(name) => seed.deserialize(BorrowedStrDeserializer::new(name)),
            Name::Owned(name) => seed.deserialize(StringDeserializer::new(name)),
        }
        .map(Some)
    }

    fn next_value_seed<V: DeserializeSeed<'de>>(&mut self, seed: V) -> Result<V::Value, M::Error> {
        self.0.next_value_seed(seed)
    }

    fn size_hint(&self) -> Option<usize> {
        self.0.size_hint()
    }
}

/// A member name, borrowed from the input when it has no escapes.
enum Name<'de> {
    Borrowed(&'de str),
    Owned(String),
}

impl Name<'_> {
    fn text(&self) -> &str {
        match self {
            Self::Borrowed(name) => name,
            Self::Owned(name) => name,
        }
    }
}

impl<'de> Deserialize<'de> for Name<'de> {
    fn deserialize<D: Deserializer<'de>>(deserializer: D) -> Result<Self, D::Error> {
        struct Text;

        impl<'de> Visitor<'de> for Text {
            type Value = Name<'de>;

            fn expecting(&self, out: &mut fmt::Formatter<'_>) -> fmt::Result {
                out.write_str("a member name")
            }

            fn visit_borrowed_str<E: Error>(self, name: &'de str) -> Result<Name<'de>, E> {
                Ok(Name::Borrowed(name))
            }

            fn visit_str<E: Error>(self, name: &str) -> Result<Name<'de>, E> {
                Ok(Name::Owned(name.to_owned()))
            }

            fn visit_string<E: Error>(self, name: String) -> Result<Name<'de>, E> {
                Ok(Name::Owned(name))
            }
        }

        deserializer.deserialize_str(Text)
    }
}

/// `serde_json::from_slice` for a payload whose root must be a JSON object
/// and in which no member name spells a serde_json private token.
pub fn from_slice<'a, T: Deserialize<'a>>(payload: &'a [u8]) -> serde_json::Result<T> {
    public_tokens(payload)?;
    serde_json::from_slice(payload).map(|Object(value)| value)
}

/// Refuses a payload in which any member name spells a serde_json private token,
/// for a decoder whose own visitor already reads only objects.
pub fn public_tokens(payload: &[u8]) -> serde_json::Result<()> {
    match scan(payload) {
        Some(_) => Ok(()),
        None => Err(serde_json::Error::custom("a private serde_json token")),
    }
}

/// Raw-token facts that a parsed value no longer shows.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Scan {
    /// Maximum container nesting (the root container is 1), ignoring
    /// brackets inside strings.
    pub depth: usize,
    /// An integer `-0` (not `-0.5` or an exponent sign), which serde_json
    /// reads as 0.
    pub negative_zero: bool,
}

/// One pass over the raw bytes; `None` when any decoded member name starts
/// with a serde_json private token (escapes included). A string is a member
/// name when the next non-whitespace byte is `:`; string values are never
/// refused. Malformed input is left to the parser.
pub fn scan(bytes: &[u8]) -> Option<Scan> {
    let mut found = Scan {
        depth: 0,
        negative_zero: false,
    };
    let mut current = 0usize;
    let mut index = 0;
    while let Some(&byte) = bytes.get(index) {
        index += 1;
        match byte {
            b'"' => {
                // Decode only as far as needed to compare an ASCII prefix;
                // a non-ASCII or malformed escape cannot spell the token.
                let mut matched = Some(0usize);
                while let Some(&byte) = bytes.get(index) {
                    index += 1;
                    let decoded = match byte {
                        b'"' => break,
                        b'\\' => {
                            let escape = bytes.get(index).copied();
                            index += 1;
                            match escape {
                                Some(b'u') => {
                                    let hex = bytes.get(index..index + 4);
                                    index += 4;
                                    hex.and_then(|hex| std::str::from_utf8(hex).ok())
                                        .and_then(|hex| u8::from_str_radix(hex, 16).ok())
                                        .filter(u8::is_ascii)
                                }
                                Some(b'b') => Some(0x08),
                                Some(b'f') => Some(0x0C),
                                Some(b'n') => Some(0x0A),
                                Some(b'r') => Some(0x0D),
                                Some(b't') => Some(0x09),
                                Some(other) => Some(other),
                                None => None,
                            }
                        }
                        other => Some(other),
                    };
                    matched = match matched {
                        Some(at) if at == PRIVATE_TOKEN.len() => Some(at),
                        Some(at) if decoded == Some(PRIVATE_TOKEN[at]) => Some(at + 1),
                        _ => None,
                    };
                }
                let name = bytes[index.min(bytes.len())..]
                    .iter()
                    .find(|byte| !matches!(byte, b' ' | 0x09 | 0x0A | 0x0D))
                    == Some(&b':');
                if name && matched == Some(PRIVATE_TOKEN.len()) {
                    return None;
                }
            }
            // A sign after an exponent marker (`1e-05`) is not a number start.
            b'-' if bytes.get(index) == Some(&b'0')
                && !matches!(bytes.get(index + 1), Some(b'.' | b'e' | b'E'))
                && !matches!(index.checked_sub(2).map(|at| bytes[at]), Some(b'e' | b'E')) =>
            {
                found.negative_zero = true;
            }
            b'[' | b'{' => {
                current += 1;
                found.depth = found.depth.max(current);
            }
            b']' | b'}' => current = current.saturating_sub(1),
            _ => {}
        }
    }
    Some(found)
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

    #[derive(Debug, Deserialize, PartialEq)]
    #[serde(rename_all = "camelCase")]
    struct Readings {
        rpm: Option<i32>,
    }

    #[derive(Debug, Deserialize, PartialEq)]
    #[serde(tag = "operation", rename_all = "camelCase", deny_unknown_fields)]
    enum Loose {
        Set {
            #[serde(default, deserialize_with = "nullable_object")]
            readings: Option<Readings>,
            settings: Option<Value>,
        },
    }

    // Without the guards, serde_json reads private tokens as typed values, and
    // a member of all-optional fields takes a buffered `1.5` as a map.
    #[test]
    fn serde_alone_reads_private_tokens_and_buffered_numbers() {
        let token =
            r#"{"operation":"set","settings":{"mode":{"$serde_json::private::Number":"1"}}}"#;
        let Loose::Set { settings, .. } = serde_json::from_str(token).unwrap();
        assert_eq!(settings.unwrap()["mode"], json!(1));
        let raw = r#"{"operation":"set","settings":{"$serde_json::private::RawValue":"{\"mode\":3,\"mode\":1}"}}"#;
        let Loose::Set { settings, .. } = serde_json::from_str(raw).unwrap();
        assert_eq!(settings.unwrap()["mode"], json!(1));
        #[derive(Deserialize)]
        #[serde(tag = "operation", rename_all = "camelCase")]
        enum Bare {
            Set { readings: Option<Readings> },
        }
        let Bare::Set { readings } =
            serde_json::from_str(r#"{"operation":"set","readings":1.5}"#).unwrap();
        assert_eq!(readings, Some(Readings { rpm: None }));
    }

    #[test]
    fn private_tokens_are_refused_as_member_names() {
        for frame in [
            r#"{"operation":"set","settings":{"mode":{"$serde_json::private::Number":"1"}}}"#,
            r#"{"operation":"set","settings":{"$serde_json::private::RawValue":"{\"mode\":1}"}}"#,
            r#"{"operation":"set","settings":{"$serde_json::private::":1}}"#,
            r#"{"operation":"set","settings":{"\u0024serde_json::private::Number":"1"}}"#,
            r#"{"operation":"set","settings":{"$serde_json::priv\u0061te::Number":"1"}}"#,
            r#"{"operation":"set","settings":{"$serde_json\u003a:private::Number":"1"}}"#,
            r#"{"operation":"set","settings":{"$serde_json::private::Number"  :"1"}}"#,
            r#"{"$serde_json::private::Number":"1"}"#,
        ] {
            assert!(scan(frame.as_bytes()).is_none(), "{frame}");
            assert!(from_slice::<Loose>(frame.as_bytes()).is_err(), "{frame}");
        }
        // Near misses decode unchanged.
        for frame in [
            r#"{"operation":"set","settings":{"$serde_json::private:":1}}"#,
            r#"{"operation":"set","settings":{"x$serde_json::private::Number":1}}"#,
            r#"{"operation":"set","settings":{"$serde_json::Private::Number":1}}"#,
            r#"{"operation":"set","settings":{"$serde_json::private\u00e9":1}}"#,
        ] {
            assert!(scan(frame.as_bytes()).is_some(), "{frame}");
            assert!(from_slice::<Loose>(frame.as_bytes()).is_ok(), "{frame}");
        }
    }

    // serde_json reinterprets only member names, so a string value with the
    // token prefix is ordinary text: a profile name or a Display key a user
    // chose. The same spellings as member names are refused.
    #[test]
    fn private_token_values_decode_and_names_are_refused() {
        let backslash = char::from(92);
        let whitespace = String::from_utf8(vec![13, 10, 9, 32]).unwrap();
        for text in [
            "$serde_json::private::Number",
            "$serde_json::private::RawValue",
            "$serde_json::private::",
            "$serde_json::private::game",
        ] {
            let escaped = text.replacen('$', &format!("{backslash}u0024"), 1);
            for spelled in [text.to_owned(), escaped] {
                for frame in [
                    format!(r#"{{"operation":"set","settings":{{"mode":"{spelled}"}}}}"#),
                    format!(r#"{{"operation":"set","settings":{{"a":"{spelled}" , "b":1}}}}"#),
                    format!(
                        r#"{{"operation":"set","settings":["{spelled}",{{"x":"{spelled}"}}]}}"#
                    ),
                    format!(r#"{{"operation":"set","settings":"{spelled}"}}"#),
                ] {
                    assert!(scan(frame.as_bytes()).is_some(), "{frame}");
                    let Loose::Set { settings, .. } = from_slice(frame.as_bytes()).unwrap();
                    assert!(settings.unwrap().to_string().contains(text), "{frame}");
                }
                for frame in [
                    format!(r#"{{"operation":"set","settings":{{"{spelled}":"1"}}}}"#),
                    format!(r#"{{"operation":"set","settings":{{"{spelled}"{whitespace}:"1"}}}}"#),
                    format!(r#"{{"operation":"set","settings":[{{"{spelled}":"1"}}]}}"#),
                    format!(r#"{{"{spelled}":"1","operation":"set"}}"#),
                ] {
                    assert!(scan(frame.as_bytes()).is_none(), "{frame}");
                    assert!(from_slice::<Loose>(frame.as_bytes()).is_err(), "{frame}");
                }
            }
        }
    }

    #[test]
    fn input_ending_inside_an_escape_does_not_panic() {
        // An escape advances past the end of the input, and the member-name
        // check then looks at the bytes after the string.
        for bytes in [
            vec![b'"', b'\\'],
            vec![b'"', b'\\', b'u'],
            vec![b'"', b'\\', b'u', b'0', b'0'],
        ] {
            let _ = scan(&bytes);
            assert!(
                from_slice::<serde_json::Value>(&bytes).is_err(),
                "{bytes:?}"
            );
        }
    }

    #[test]
    fn buffered_non_integer_numbers_are_not_objects() {
        for number in ["1.5", "1e2", "-0.0", "123456789012345678901234567890"] {
            let frame = format!(r#"{{"operation":"set","readings":{number}}}"#);
            assert!(from_slice::<Loose>(frame.as_bytes()).is_err(), "{frame}");
            // A Value member still reads the number as a number.
            let frame = format!(r#"{{"operation":"set","settings":{number}}}"#);
            let Loose::Set { settings, .. } = from_slice(frame.as_bytes()).unwrap();
            assert!(settings.unwrap().is_number(), "{frame}");
        }
        let frame = br#"{"operation":"set","readings":{"rpm":1},"settings":{"a":1.5}}"#;
        assert_eq!(
            from_slice::<Loose>(frame).unwrap(),
            Loose::Set {
                readings: Some(Readings { rpm: Some(1) }),
                settings: Some(json!({"a":1.5})),
            }
        );
        // Escaped member names still reach the struct visitor.
        let frame = br#"{"operation":"set","readings":{"\u0072pm":2}}"#;
        let Loose::Set { readings, .. } = from_slice(frame).unwrap();
        assert_eq!(readings, Some(Readings { rpm: Some(2) }));
    }

    #[test]
    fn scan_reports_depth_and_integer_negative_zero() {
        let scanned = |text: &str| scan(text.as_bytes()).unwrap();
        assert_eq!(scanned(r#"{"a":[{"b":"[{["}]}"#).depth, 3);
        assert_eq!(scanned("{}").depth, 1);
        // The deepest container, not the last one opened.
        assert_eq!(scanned("[[[]],[]]").depth, 3);
        for zero in ["-0", "[-0]", r#"{"a":-0}"#] {
            assert!(scanned(zero).negative_zero, "{zero}");
        }
        for other in [
            "-0.5", "-0e1", "-0E1", "1e-05", "1E-0", r#""-0""#, "-1", "0",
        ] {
            assert!(!scanned(other).negative_zero, "{other}");
        }
    }
}

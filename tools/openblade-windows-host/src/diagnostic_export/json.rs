//! The text managed `System.Text.Json` writes for the diagnostic archive:
//! `JavaScriptEncoder.Default` escaping and, when indented, two spaces and the
//! Windows `Environment.NewLine`. Pinned by the managed entry vectors.

use std::fmt::Write as _;

/// An ordered JSON value. Objects keep the member order managed serializes.
#[derive(Clone, Debug, PartialEq, Eq)]
pub(crate) enum Json {
    Null,
    Bool(bool),
    Int(i64),
    Str(String),
    Array(Vec<Json>),
    Object(Vec<(String, Json)>),
}

const NEW_LINE: &str = "\r\n";

impl Json {
    pub(crate) fn str(value: impl Into<String>) -> Self {
        Self::Str(value.into())
    }

    pub(crate) fn nullable(value: Option<impl Into<String>>) -> Self {
        value.map_or(Self::Null, Self::str)
    }

    pub(crate) fn object<'a>(members: impl IntoIterator<Item = (&'a str, Json)>) -> Self {
        Self::Object(
            members
                .into_iter()
                .map(|(name, value)| (name.to_owned(), value))
                .collect(),
        )
    }

    /// The managed `Utf8JsonWriter` text: indented, or compact.
    pub(crate) fn write(&self, indented: bool) -> String {
        let mut text = String::new();
        self.write_to(&mut text, indented.then_some(0));
        text
    }

    fn write_to(&self, text: &mut String, depth: Option<usize>) {
        match self {
            Self::Null => text.push_str("null"),
            Self::Bool(value) => text.push_str(if *value { "true" } else { "false" }),
            Self::Int(value) => {
                let _ = write!(text, "{value}");
            }
            Self::Str(value) => escape(text, value),
            Self::Array(items) => {
                text.push('[');
                for (index, item) in items.iter().enumerate() {
                    separate(text, index, depth);
                    item.write_to(text, depth.map(|depth| depth + 1));
                }
                close(text, items.is_empty(), depth, ']');
            }
            Self::Object(members) => {
                text.push('{');
                for (index, (name, value)) in members.iter().enumerate() {
                    separate(text, index, depth);
                    escape(text, name);
                    text.push_str(if depth.is_some() { ": " } else { ":" });
                    value.write_to(text, depth.map(|depth| depth + 1));
                }
                close(text, members.is_empty(), depth, '}');
            }
        }
    }

    /// Managed `WriteRedactedElement`: every string value, and every member
    /// name unless `names` is false, passed through `redact`.
    pub(crate) fn redacted(&self, redact: &impl Fn(&str) -> String, names: bool) -> Self {
        match self {
            Self::Str(value) => Self::Str(redact(value)),
            Self::Array(items) => Self::Array(
                items
                    .iter()
                    .map(|item| item.redacted(redact, names))
                    .collect(),
            ),
            Self::Object(members) => Self::Object(
                members
                    .iter()
                    .map(|(name, value)| {
                        (
                            if names { redact(name) } else { name.clone() },
                            value.redacted(redact, names),
                        )
                    })
                    .collect(),
            ),
            other => other.clone(),
        }
    }
}

fn separate(text: &mut String, index: usize, depth: Option<usize>) {
    if index > 0 {
        text.push(',');
    }
    if let Some(depth) = depth {
        text.push_str(NEW_LINE);
        indent(text, depth + 1);
    }
}

fn close(text: &mut String, empty: bool, depth: Option<usize>, bracket: char) {
    if let (Some(depth), false) = (depth, empty) {
        text.push_str(NEW_LINE);
        indent(text, depth);
    }
    text.push(bracket);
}

fn indent(text: &mut String, depth: usize) {
    for _ in 0..depth {
        text.push_str("  ");
    }
}

/// `JavaScriptEncoder.Default`: printable ASCII passes except the HTML and
/// JavaScript sensitive characters; everything else is an uppercase `\uXXXX`
/// per UTF-16 unit, apart from the short escapes `Utf8JsonWriter` keeps.
fn escape(text: &mut String, value: &str) {
    text.push('"');
    for character in value.chars() {
        match character {
            '\\' => text.push_str("\\\\"),
            '\n' => text.push_str("\\n"),
            '\r' => text.push_str("\\r"),
            '\t' => text.push_str("\\t"),
            '\u{8}' => text.push_str("\\b"),
            '\u{c}' => text.push_str("\\f"),
            '"' | '&' | '\'' | '+' | '<' | '>' | '`' => unicode(text, character),
            ' '..='~' => text.push(character),
            _ => unicode(text, character),
        }
    }
    text.push('"');
}

fn unicode(text: &mut String, character: char) {
    let mut units = [0; 2];
    for unit in character.encode_utf16(&mut units) {
        let _ = write!(text, "\\u{unit:04X}");
    }
}

/// Managed `DateTimeOffset` JSON for a UTC instant: round-trip text with the
/// fraction's trailing zeros removed.
pub(crate) fn timestamp(value: super::Timestamp) -> String {
    let (date, fraction) = value.civil();
    let mut text = date;
    if fraction != 0 {
        let digits = format!("{fraction:07}");
        text.push('.');
        text.push_str(digits.trim_end_matches('0'));
    }
    text.push_str("+00:00");
    text
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn escapes_as_the_default_encoder_does() {
        let value = Json::str("a\"&'+<>`\\/ \u{1}\t\u{7f}é😀\u{2028}");
        assert_eq!(
            value.write(false),
            r#""a\u0022\u0026\u0027\u002B\u003C\u003E\u0060\\/ \u0001\t\u007F\u00E9\uD83D\uDE00\u2028""#
        );
    }

    #[test]
    fn indents_with_two_spaces_and_crlf_and_keeps_empty_containers_inline() {
        let value = Json::object([
            ("a", Json::Array(vec![])),
            ("b", Json::Object(vec![])),
            ("c", Json::Array(vec![Json::Int(1), Json::Null])),
        ]);
        assert_eq!(
            value.write(true),
            "{\r\n  \"a\": [],\r\n  \"b\": {},\r\n  \"c\": [\r\n    1,\r\n    null\r\n  ]\r\n}"
        );
        assert_eq!(value.write(false), r#"{"a":[],"b":{},"c":[1,null]}"#);
    }
}

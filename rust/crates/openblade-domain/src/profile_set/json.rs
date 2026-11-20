//! A JSON tree that keeps what `System.Text.Json` observes and serde_json's
//! `Value` drops: member order, the number text and duplicate members.
//!
//! Reading is strict RFC 8259 with managed's 64-container depth ceiling.
//! Writing reproduces `Utf8JsonWriter` with the default `JavaScriptEncoder`:
//! the indented form uses two spaces and CRLF, exactly as `ProfileStore`
//! writes its documents on Windows.

use std::fmt::Write as _;
use std::string::{String, ToString};
use std::vec::Vec;

/// Managed `JsonSerializerOptions.MaxDepth` default.
const MAX_DEPTH: usize = 64;

#[derive(Clone, Debug, PartialEq)]
pub enum Json {
    Null,
    Bool(bool),
    /// The number exactly as written; interpretation belongs to the member.
    Number(String),
    String(String),
    Array(Vec<Json>),
    /// Members in document order. [`parse`] never yields a duplicate name.
    Object(Vec<(String, Json)>),
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct Malformed;

impl Json {
    pub fn int(value: i64) -> Self {
        Self::Number(value.to_string())
    }

    pub fn string(value: &str) -> Self {
        Self::String(value.into())
    }

    pub fn members(&self) -> Option<&[(String, Json)]> {
        match self {
            Self::Object(members) => Some(members),
            _ => None,
        }
    }

    pub fn member(&self, name: &str) -> Option<&Json> {
        self.members()?
            .iter()
            .find_map(|(key, value)| (key == name).then_some(value))
    }

    /// `Utf8JsonReader.TryGetInt32` and its narrower siblings: an integer
    /// literal with no fraction or exponent, inside `min..=max`.
    pub fn integer(&self, min: i64, max: i64) -> Option<i64> {
        let Self::Number(text) = self else {
            return None;
        };
        integer_text(text, min, max)
    }

    /// `JsonNumberHandling.AllowReadingFromString`, which `JsonSerializerDefaults.Web`
    /// selects: a number member also accepts its text in a string. Leading
    /// zeros are taken; `+5`, padding and an unsigned `-0` are refused.
    pub fn web_integer(&self, min: i64, max: i64) -> Option<i64> {
        match self {
            Self::String(text) => integer_text(text, min, max),
            _ => self.integer(min, max),
        }
    }

    /// A double as `Utf8JsonReader.GetDouble` reads it, overflow included.
    /// In a string managed also takes a leading `+`, a missing integer or
    /// fraction part, and the `NaN`, `Infinity` and `-Infinity` literals.
    pub fn web_double(&self) -> Option<f64> {
        match self {
            Self::Number(text) => text.parse().ok(),
            Self::String(text) => match text.as_str() {
                "NaN" => Some(f64::NAN),
                "Infinity" => Some(f64::INFINITY),
                "-Infinity" => Some(f64::NEG_INFINITY),
                text => is_decimal(text).then(|| text.parse().ok()).flatten(),
            },
            _ => None,
        }
    }

    pub fn boolean(&self) -> Option<bool> {
        match self {
            Self::Bool(value) => Some(*value),
            _ => None,
        }
    }

    pub fn text(&self) -> Option<&str> {
        match self {
            Self::String(value) => Some(value),
            _ => None,
        }
    }

    pub fn is_null(&self) -> bool {
        matches!(self, Self::Null)
    }
}

fn integer_text(text: &str, min: i64, max: i64) -> Option<i64> {
    let digits = text.strip_prefix('-').unwrap_or(text);
    if digits.is_empty() || !digits.bytes().all(|b| b.is_ascii_digit()) {
        return None;
    }
    // An unsigned member refuses a sign, including the `-0` literal.
    if min >= 0 && digits.len() != text.len() {
        return None;
    }
    text.parse::<i64>()
        .ok()
        .filter(|value| (min..=max).contains(value))
}

/// `[+-]` then digits with an optional fraction on either side of the point,
/// then an optional exponent. Rust's own parser would also take `inf`.
fn is_decimal(text: &str) -> bool {
    let unsigned = text.strip_prefix(['+', '-']).unwrap_or(text);
    let (mantissa, exponent) = match unsigned.split_once(['e', 'E']) {
        Some((mantissa, exponent)) => (mantissa, Some(exponent)),
        None => (unsigned, None),
    };
    let digits = |part: &str| part.bytes().all(|b| b.is_ascii_digit());
    let (whole, fraction) = mantissa.split_once('.').unwrap_or((mantissa, ""));
    digits(whole)
        && digits(fraction)
        && !(whole.is_empty() && fraction.is_empty())
        && exponent.is_none_or(|exponent| {
            let exponent = exponent.strip_prefix(['+', '-']).unwrap_or(exponent);
            !exponent.is_empty() && digits(exponent)
        })
}

/// One complete JSON value with nothing but whitespace around it.
pub fn parse(bytes: &[u8]) -> Result<Json, Malformed> {
    // Reject invalid UTF-8 once; the reader then slices on ASCII boundaries.
    core::str::from_utf8(bytes).map_err(|_| Malformed)?;
    let mut reader = Reader {
        bytes,
        at: 0,
        depth: 0,
    };
    reader.whitespace();
    let value = reader.value()?;
    reader.whitespace();
    if reader.at == bytes.len() {
        Ok(value)
    } else {
        Err(Malformed)
    }
}

struct Reader<'a> {
    bytes: &'a [u8],
    at: usize,
    depth: usize,
}

impl Reader<'_> {
    fn peek(&self) -> Option<u8> {
        self.bytes.get(self.at).copied()
    }

    fn whitespace(&mut self) {
        while matches!(self.peek(), Some(b' ' | b'\t' | b'\n' | b'\r')) {
            self.at += 1;
        }
    }

    fn literal(&mut self, text: &[u8], value: Json) -> Result<Json, Malformed> {
        if self.bytes[self.at..].starts_with(text) {
            self.at += text.len();
            Ok(value)
        } else {
            Err(Malformed)
        }
    }

    fn value(&mut self) -> Result<Json, Malformed> {
        match self.peek().ok_or(Malformed)? {
            b'n' => self.literal(b"null", Json::Null),
            b't' => self.literal(b"true", Json::Bool(true)),
            b'f' => self.literal(b"false", Json::Bool(false)),
            b'"' => self.string().map(Json::String),
            b'[' => self.container(|reader| reader.array()),
            b'{' => self.container(|reader| reader.object()),
            b'-' | b'0'..=b'9' => self.number().map(Json::Number),
            _ => Err(Malformed),
        }
    }

    fn container(
        &mut self,
        read: impl FnOnce(&mut Self) -> Result<Json, Malformed>,
    ) -> Result<Json, Malformed> {
        if self.depth == MAX_DEPTH {
            return Err(Malformed);
        }
        self.depth += 1;
        self.at += 1;
        let value = read(self)?;
        self.depth -= 1;
        Ok(value)
    }

    /// After a value: `true` for a separator, `false` at the closing byte.
    fn more(&mut self, close: u8) -> Result<bool, Malformed> {
        self.whitespace();
        let next = self.peek().ok_or(Malformed)?;
        self.at += 1;
        if next == b',' {
            self.whitespace();
            Ok(true)
        } else if next == close {
            Ok(false)
        } else {
            Err(Malformed)
        }
    }

    fn array(&mut self) -> Result<Json, Malformed> {
        let mut items = Vec::new();
        self.whitespace();
        if self.peek() == Some(b']') {
            self.at += 1;
            return Ok(Json::Array(items));
        }
        loop {
            items.push(self.value()?);
            if !self.more(b']')? {
                return Ok(Json::Array(items));
            }
        }
    }

    fn object(&mut self) -> Result<Json, Malformed> {
        let mut members: Vec<(String, Json)> = Vec::new();
        self.whitespace();
        if self.peek() == Some(b'}') {
            self.at += 1;
            return Ok(Json::Object(members));
        }
        loop {
            if self.peek() != Some(b'"') {
                return Err(Malformed);
            }
            let name = self.string()?;
            self.whitespace();
            if self.peek() != Some(b':') {
                return Err(Malformed);
            }
            self.at += 1;
            self.whitespace();
            let value = self.value()?;
            // Managed rejects a duplicate before it binds any member.
            if members.iter().any(|(seen, _)| *seen == name) {
                return Err(Malformed);
            }
            members.push((name, value));
            if !self.more(b'}')? {
                return Ok(Json::Object(members));
            }
        }
    }

    fn digits(&mut self) -> usize {
        let start = self.at;
        while matches!(self.peek(), Some(b'0'..=b'9')) {
            self.at += 1;
        }
        self.at - start
    }

    fn number(&mut self) -> Result<String, Malformed> {
        let start = self.at;
        if self.peek() == Some(b'-') {
            self.at += 1;
        }
        let leading_zero = self.peek() == Some(b'0');
        let integer = self.digits();
        if integer == 0 || leading_zero && integer > 1 {
            return Err(Malformed);
        }
        if self.peek() == Some(b'.') {
            self.at += 1;
            if self.digits() == 0 {
                return Err(Malformed);
            }
        }
        if matches!(self.peek(), Some(b'e' | b'E')) {
            self.at += 1;
            if matches!(self.peek(), Some(b'+' | b'-')) {
                self.at += 1;
            }
            if self.digits() == 0 {
                return Err(Malformed);
            }
        }
        // ASCII only, so the slice is valid UTF-8.
        core::str::from_utf8(&self.bytes[start..self.at])
            .map(Into::into)
            .map_err(|_| Malformed)
    }

    fn hex4(&mut self) -> Result<u32, Malformed> {
        let digits = self.bytes.get(self.at..self.at + 4).ok_or(Malformed)?;
        let text = core::str::from_utf8(digits).map_err(|_| Malformed)?;
        if !text.bytes().all(|b| b.is_ascii_hexdigit()) {
            return Err(Malformed);
        }
        self.at += 4;
        u32::from_str_radix(text, 16).map_err(|_| Malformed)
    }

    fn string(&mut self) -> Result<String, Malformed> {
        self.at += 1;
        let mut out = String::new();
        loop {
            let start = self.at;
            while !matches!(self.peek(), None | Some(b'"' | b'\\' | 0..=0x1F)) {
                self.at += 1;
            }
            out.push_str(core::str::from_utf8(&self.bytes[start..self.at]).map_err(|_| Malformed)?);
            match self.peek() {
                Some(b'"') => {
                    self.at += 1;
                    return Ok(out);
                }
                Some(b'\\') => {
                    self.at += 1;
                    let escape = self.peek().ok_or(Malformed)?;
                    self.at += 1;
                    out.push(match escape {
                        b'"' => '"',
                        b'\\' => '\\',
                        b'/' => '/',
                        b'b' => '\u{8}',
                        b'f' => '\u{C}',
                        b'n' => '\n',
                        b'r' => '\r',
                        b't' => '\t',
                        b'u' => self.escaped_scalar()?,
                        _ => return Err(Malformed),
                    });
                }
                // An unescaped control character or the end of input.
                _ => return Err(Malformed),
            }
        }
    }

    /// A `\u` escape, pairing surrogates; managed refuses an unpaired one.
    fn escaped_scalar(&mut self) -> Result<char, Malformed> {
        let unit = self.hex4()?;
        let scalar = if (0xD800..0xDC00).contains(&unit) {
            if self.bytes.get(self.at..self.at + 2) != Some(b"\\u") {
                return Err(Malformed);
            }
            self.at += 2;
            let low = self.hex4()?;
            if !(0xDC00..0xE000).contains(&low) {
                return Err(Malformed);
            }
            0x10000 + ((unit - 0xD800) << 10) + (low - 0xDC00)
        } else {
            unit
        };
        char::from_u32(scalar).ok_or(Malformed)
    }
}

/// `Utf8JsonWriter` output: compact, or `WriteIndented` with CRLF.
pub fn write(value: &Json, indented: bool) -> Vec<u8> {
    let mut out = String::new();
    write_value(&mut out, value, indented.then_some(0));
    out.into_bytes()
}

fn line(out: &mut String, indent: Option<usize>) {
    if let Some(depth) = indent {
        out.push_str("\r\n");
        for _ in 0..depth {
            out.push_str("  ");
        }
    }
}

fn write_value(out: &mut String, value: &Json, indent: Option<usize>) {
    let inner = indent.map(|depth| depth + 1);
    match value {
        Json::Null => out.push_str("null"),
        Json::Bool(true) => out.push_str("true"),
        Json::Bool(false) => out.push_str("false"),
        Json::Number(text) => out.push_str(text),
        Json::String(text) => write_string(out, text),
        Json::Array(items) => {
            out.push('[');
            for (index, item) in items.iter().enumerate() {
                if index > 0 {
                    out.push(',');
                }
                line(out, inner);
                write_value(out, item, inner);
            }
            if !items.is_empty() {
                line(out, indent);
            }
            out.push(']');
        }
        Json::Object(members) => {
            out.push('{');
            for (index, (name, member)) in members.iter().enumerate() {
                if index > 0 {
                    out.push(',');
                }
                line(out, inner);
                write_string(out, name);
                out.push(':');
                if indent.is_some() {
                    out.push(' ');
                }
                write_value(out, member, inner);
            }
            if !members.is_empty() {
                line(out, indent);
            }
            out.push('}');
        }
    }
}

/// The default `JavaScriptEncoder`: printable Basic Latin except the HTML-
/// sensitive characters is written as is, everything else as `\uXXXX` in
/// uppercase hex, by UTF-16 code unit.
fn write_string(out: &mut String, text: &str) {
    out.push('"');
    for character in text.chars() {
        match character {
            '\\' => out.push_str("\\\\"),
            '\u{8}' => out.push_str("\\b"),
            '\t' => out.push_str("\\t"),
            '\n' => out.push_str("\\n"),
            '\u{C}' => out.push_str("\\f"),
            '\r' => out.push_str("\\r"),
            ' '..='~' if !matches!(character, '"' | '&' | '\'' | '+' | '<' | '>' | '`') => {
                out.push(character)
            }
            _ => {
                let mut units = [0u16; 2];
                for unit in character.encode_utf16(&mut units) {
                    let _ = write!(out, "\\u{unit:04X}");
                }
            }
        }
    }
    out.push('"');
}

/// The shortest round-trip text `Utf8JsonWriter.WriteNumberValue(double)`
/// writes: fixed notation while the decimal point stays within seventeen
/// digits and four leading zeros, `E+NN` otherwise.
pub fn double(value: f64) -> Json {
    if value == 0.0 {
        return Json::Number(if value.is_sign_negative() { "-0" } else { "0" }.into());
    }
    // Rust and .NET both print the shortest digits that round-trip.
    let scientific = std::format!("{:e}", value.abs());
    let (mantissa, exponent) = scientific.split_once('e').unwrap_or((&scientific, "0"));
    let digits: String = mantissa.chars().filter(char::is_ascii_digit).collect();
    let exponent: i32 = exponent.parse().unwrap_or(0);
    // The value is 0.DIGITS x 10^scale.
    let scale = exponent + 1;
    let count = digits.len() as i32;
    let mut out = String::new();
    if value < 0.0 {
        out.push('-');
    }
    if scale > count.max(17) || scale < -3 {
        out.push_str(&digits[..1]);
        if count > 1 {
            out.push('.');
            out.push_str(&digits[1..]);
        }
        let _ = write!(
            out,
            "E{}{:02}",
            if exponent < 0 { '-' } else { '+' },
            exponent.abs()
        );
    } else if scale <= 0 {
        out.push_str("0.");
        for _ in scale..0 {
            out.push('0');
        }
        out.push_str(&digits);
    } else if scale >= count {
        out.push_str(&digits);
        for _ in count..scale {
            out.push('0');
        }
    } else {
        out.push_str(&digits[..scale as usize]);
        out.push('.');
        out.push_str(&digits[scale as usize..]);
    }
    Json::Number(out)
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::vec;

    fn text(value: f64) -> String {
        match double(value) {
            Json::Number(text) => text,
            _ => unreachable!(),
        }
    }

    #[test]
    fn doubles_match_the_managed_writer() {
        // Values and spellings observed from `JsonSerializer` on .NET 10.
        for (value, expected) in [
            (1.0, "1"),
            (1.25, "1.25"),
            (4.0, "4"),
            (0.1, "0.1"),
            (1e21, "1E+21"),
            (1e-7, "1E-07"),
            (123456789012345680000.0, "1.2345678901234568E+20"),
            (1.5e300, "1.5E+300"),
            (-0.0, "-0"),
            (0.30000000000000004, "0.30000000000000004"),
            (100.0, "100"),
            (1e15, "1000000000000000"),
            (1e16, "10000000000000000"),
            (12345678.9, "12345678.9"),
            (-12.0, "-12"),
            (-0.5, "-0.5"),
        ] {
            assert_eq!(text(value), expected);
        }
    }

    #[test]
    fn strings_use_the_default_javascript_encoder() {
        let value = Json::string("60Hz \u{e9}<>&'+\"\\/\u{7f}\u{2028}\u{1F600}\t`\u{1}");
        assert_eq!(
            String::from_utf8(write(&value, false)).unwrap(),
            r#""60Hz \u00E9\u003C\u003E\u0026\u0027\u002B\u0022\\/\u007F\u2028\uD83D\uDE00\t\u0060\u0001""#
        );
    }

    #[test]
    fn indented_output_is_crlf_with_two_spaces_and_closed_empties() {
        let value = parse(br#"{"a":1,"b":[1,{"c":[]}],"d":{}}"#).unwrap();
        assert_eq!(
            String::from_utf8(write(&value, true)).unwrap(),
            "{\r\n  \"a\": 1,\r\n  \"b\": [\r\n    1,\r\n    {\r\n      \"c\": []\r\n    }\r\n  ],\r\n  \"d\": {}\r\n}"
        );
        assert_eq!(write(&value, false), br#"{"a":1,"b":[1,{"c":[]}],"d":{}}"#);
    }

    #[test]
    fn parsing_is_strict_and_keeps_order_and_number_text() {
        let value = parse(b" {\"b\":1.0,\"a\":-0,\"s\":\"\\ud83d\\ude00\\n\"} ").unwrap();
        assert_eq!(
            value,
            Json::Object(vec![
                ("b".into(), Json::Number("1.0".into())),
                ("a".into(), Json::Number("-0".into())),
                ("s".into(), Json::string("\u{1F600}\n")),
            ])
        );
        for malformed in [
            &b""[..],
            b"{\"a\":1,\"a\":2}",
            b"[1,]",
            b"{\"a\":1,}",
            b"01",
            b"1.",
            b"+1",
            b"\"\\ud83d\"",
            b"\"\\udc00\"",
            b"\"a\nb\"",
            b"\"\\x\"",
            b"nul",
            b"1 2",
            b"\xEF\xBB\xBF1",
            b"\"\xFF\"",
            b"{a:1}",
            b"'a'",
        ] {
            assert_eq!(parse(malformed), Err(Malformed), "{malformed:?}");
        }
    }

    #[test]
    fn depth_follows_the_managed_ceiling() {
        let nested = |depth: usize| {
            let mut text = "[".repeat(depth);
            text.push_str(&"]".repeat(depth));
            text.into_bytes()
        };
        assert!(parse(&nested(64)).is_ok());
        assert_eq!(parse(&nested(65)), Err(Malformed));
    }

    #[test]
    fn members_read_as_the_managed_number_converters_do() {
        let number = |text: &str| Json::Number(text.into());
        assert_eq!(number("7").integer(0, 255), Some(7));
        assert_eq!(
            number("-0").integer(i32::MIN.into(), i32::MAX.into()),
            Some(0)
        );
        for refused in ["1.0", "1e0", "256", "-0", "-1"] {
            assert_eq!(number(refused).integer(0, 255), None, "{refused}");
        }
        assert_eq!(
            number("2147483648").integer(i32::MIN.into(), i32::MAX.into()),
            None
        );
        assert_eq!(Json::string("42").web_integer(0, 255), Some(42));
        assert_eq!(Json::string("42").integer(0, 255), None);
        assert_eq!(Json::string("007").web_integer(0, 255), Some(7));
        assert_eq!(Json::string("-0").web_integer(-1, 255), Some(0));
        for refused in ["+4", " 4", "", "4.0", "-0", "4 "] {
            assert_eq!(Json::string(refused).web_integer(0, 255), None, "{refused}");
        }
        assert_eq!(number("1.5").web_double(), Some(1.5));
        assert_eq!(number("1e999").web_double(), Some(f64::INFINITY));
        for (text, value) in [
            ("1e2", 100.0),
            ("+1", 1.0),
            (".5", 0.5),
            ("5.", 5.0),
            ("-12", -12.0),
            ("01.50", 1.5),
        ] {
            assert_eq!(Json::string(text).web_double(), Some(value), "{text}");
        }
        assert!(Json::string("NaN").web_double().is_some_and(f64::is_nan));
        assert_eq!(
            Json::string("-Infinity").web_double(),
            Some(f64::NEG_INFINITY)
        );
        for refused in [
            "nan",
            "-NaN",
            "+Infinity",
            "inf",
            "0x10",
            " 1",
            "",
            ".",
            "1e",
            "e5",
            "1e+",
            "--1",
        ] {
            assert_eq!(Json::string(refused).web_double(), None, "{refused:?}");
        }
        assert_eq!(Json::Bool(true).web_double(), None);
    }
}

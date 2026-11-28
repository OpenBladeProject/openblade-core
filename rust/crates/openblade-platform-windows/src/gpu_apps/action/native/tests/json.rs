//! Enough JSON and base64 to read the committed managed dispatcher fixture.
//! The platform crate takes no parsing dependency, even for tests.
use std::ops::Index;

#[derive(Debug, PartialEq)]
pub(super) enum Json {
    Null,
    Bool(bool),
    Number(String),
    String(String),
    Array(Vec<Json>),
    Object(Vec<(String, Json)>),
}

const NULL: Json = Json::Null;

/// A missing member reads as null, as the managed writer omits nothing.
impl Index<&str> for Json {
    type Output = Json;
    fn index(&self, key: &str) -> &Json {
        match self {
            Json::Object(members) => members
                .iter()
                .find(|(name, _)| name == key)
                .map_or(&NULL, |(_, value)| value),
            _ => panic!("not an object: {self:?}"),
        }
    }
}

impl Json {
    pub(super) fn as_str(&self) -> &str {
        self.as_optional_str().expect("a string")
    }

    pub(super) fn as_optional_str(&self) -> Option<&str> {
        match self {
            Json::String(text) => Some(text),
            Json::Null => None,
            _ => panic!("not a string: {self:?}"),
        }
    }

    pub(super) fn as_i64(&self) -> i64 {
        match self {
            Json::Number(text) => text.parse().unwrap(),
            _ => panic!("not an integer: {self:?}"),
        }
    }

    pub(super) fn as_i32(&self) -> i32 {
        i32::try_from(self.as_i64()).unwrap()
    }

    pub(super) fn items(&self) -> &[Json] {
        match self {
            Json::Array(items) => items,
            _ => panic!("not an array: {self:?}"),
        }
    }
}

pub(super) fn parse(text: &str) -> Json {
    let mut parser = Parser {
        bytes: text.as_bytes(),
        at: 0,
    };
    let value = parser.value();
    parser.space();
    assert_eq!(parser.at, text.len(), "trailing input");
    value
}

struct Parser<'a> {
    bytes: &'a [u8],
    at: usize,
}

impl Parser<'_> {
    fn space(&mut self) {
        while self.bytes.get(self.at).is_some_and(u8::is_ascii_whitespace) {
            self.at += 1;
        }
    }

    fn next(&mut self) -> u8 {
        let byte = self.bytes[self.at];
        self.at += 1;
        byte
    }

    fn literal(&mut self, literal: &str, value: Json) -> Json {
        assert!(self.bytes[self.at..].starts_with(literal.as_bytes()));
        self.at += literal.len();
        value
    }

    fn value(&mut self) -> Json {
        self.space();
        match self.bytes[self.at] {
            b'{' => {
                self.at += 1;
                let mut members = Vec::new();
                self.space();
                if self.bytes[self.at] == b'}' {
                    self.at += 1;
                    return Json::Object(members);
                }
                loop {
                    let Json::String(name) = self.value() else {
                        panic!("member name");
                    };
                    self.space();
                    assert_eq!(self.next(), b':');
                    members.push((name, self.value()));
                    self.space();
                    match self.next() {
                        b',' => {}
                        b'}' => return Json::Object(members),
                        other => panic!("unexpected {}", other as char),
                    }
                }
            }
            b'[' => {
                self.at += 1;
                let mut items = Vec::new();
                self.space();
                if self.bytes[self.at] == b']' {
                    self.at += 1;
                    return Json::Array(items);
                }
                loop {
                    items.push(self.value());
                    self.space();
                    match self.next() {
                        b',' => {}
                        b']' => return Json::Array(items),
                        other => panic!("unexpected {}", other as char),
                    }
                }
            }
            b'"' => self.string(),
            b't' => self.literal("true", Json::Bool(true)),
            b'f' => self.literal("false", Json::Bool(false)),
            b'n' => self.literal("null", Json::Null),
            _ => {
                let start = self.at;
                while self.bytes.get(self.at).is_some_and(|byte| {
                    matches!(byte, b'-' | b'+' | b'.' | b'e' | b'E' | b'0'..=b'9')
                }) {
                    self.at += 1;
                }
                assert!(self.at > start, "unexpected input at {start}");
                Json::Number(
                    std::str::from_utf8(&self.bytes[start..self.at])
                        .unwrap()
                        .into(),
                )
            }
        }
    }

    fn string(&mut self) -> Json {
        self.at += 1;
        let mut units = Vec::new();
        loop {
            let start = self.at;
            match self.next() {
                b'"' => break,
                b'\\' => {
                    let escaped = self.next();
                    if escaped == b'u' {
                        let hex = std::str::from_utf8(&self.bytes[self.at..self.at + 4]).unwrap();
                        units.push(u16::from_str_radix(hex, 16).unwrap());
                        self.at += 4;
                    } else {
                        units.push(u16::from(match escaped {
                            b'n' => b'\n',
                            b'r' => b'\r',
                            b't' => b'\t',
                            b'b' => 8,
                            b'f' => 12,
                            other => other,
                        }));
                    }
                }
                lead => {
                    let width = match lead {
                        0..0x80 => 1,
                        0xC0..0xE0 => 2,
                        0xE0..0xF0 => 3,
                        _ => 4,
                    };
                    let text = std::str::from_utf8(&self.bytes[start..start + width]).unwrap();
                    units.extend(text.encode_utf16());
                    self.at = start + width;
                }
            }
        }
        Json::String(String::from_utf16(&units).unwrap())
    }
}

pub(super) fn base64(text: &str) -> Vec<u8> {
    let value = |byte: u8| match byte {
        b'A'..=b'Z' => byte - b'A',
        b'a'..=b'z' => byte - b'a' + 26,
        b'0'..=b'9' => byte - b'0' + 52,
        b'+' => 62,
        b'/' => 63,
        _ => panic!("invalid base64"),
    };
    let bytes: Vec<u8> = text.bytes().filter(|byte| *byte != b'=').collect();
    let mut out = Vec::with_capacity(bytes.len() * 3 / 4);
    for chunk in bytes.chunks(4) {
        let mut acc = 0u32;
        for (index, byte) in chunk.iter().enumerate() {
            acc |= u32::from(value(*byte)) << (18 - 6 * index);
        }
        out.extend_from_slice(&acc.to_be_bytes()[1..chunk.len()]);
    }
    out
}

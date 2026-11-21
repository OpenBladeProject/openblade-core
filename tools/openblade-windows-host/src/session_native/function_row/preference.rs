//! The per-user function-row backend (managed `SpecialKeyBindingSource`
//! reading `UiPreferenceStore.LoadInputPreferencesAsync`). Only the
//! `functionRowBackend` member is read.
//!
//! Matching managed:
//! - A missing file is the `UiInputPreferences` default, WindowsHook, and so is
//!   a file without the member.
//! - The file is bounded at 1 MiB and may start with a UTF-8 BOM.
//! - Members match case-insensitively (`JsonSerializerDefaults.Web`).
//! - `KeyboardInputBackendJsonConverter`: a string is read by
//!   `Enum.TryParse(ignoreCase: true)` (see `enum_parse`), a number must be a
//!   32-bit integer, `null` and anything undefined is Unknown, and another JSON
//!   type fails the read. The managed parity vectors' `preference` rows pin
//!   each of these.
//! - A failed read keeps the last value read.
//!
//! Deliberate differences:
//! - Before the first successful read the backend is unknown, so no hook is
//!   installed. Managed starts at WindowsHook and keeps it through a failure.
//! - Other members are not validated. Managed fails the whole read when, for
//!   example, `snapTapEnabled` is not a boolean.
//! - Two members whose names differ only in case fail the read. Managed takes
//!   the last one. An exact repeat keeps the last one in both.
//! - The file is polled every second by its length and write time. Managed
//!   uses a directory watcher with the same one-second fallback.
use std::{
    fs,
    io::Read,
    path::PathBuf,
    time::{Duration, Instant, SystemTime},
};

use openblade_domain::function_row::Backend;
use openblade_platform_windows::agent_diagnostics::{Event, FunctionRowNotice};
use serde_json::Value;

/// Managed `UiPreferenceStore.MaximumFileSizeBytes`.
const MAXIMUM_BYTES: u64 = 1024 * 1024;
/// Managed `SpecialKeyBindingSource.FallbackRefreshInterval`.
const POLL: Duration = Duration::from_secs(1);
const MEMBER: &str = "functionRowBackend";

#[derive(Debug, Eq, PartialEq)]
pub(crate) struct Unreadable;

/// The backend a preference file selects. `None` is managed's Unknown.
pub(crate) fn parse(content: Option<&[u8]>) -> Result<Option<Backend>, Unreadable> {
    let Some(mut bytes) = content else {
        return Ok(Some(Backend::WindowsHook));
    };
    if bytes.len() as u64 > MAXIMUM_BYTES {
        return Err(Unreadable);
    }
    if let Some(rest) = bytes.strip_prefix(b"\xEF\xBB\xBF") {
        bytes = rest;
    }
    let root: Value = serde_json::from_slice(bytes).map_err(|_| Unreadable)?;
    let object = root.as_object().ok_or(Unreadable)?;
    let mut matching = object
        .iter()
        .filter(|(name, _)| name.eq_ignore_ascii_case(MEMBER));
    let Some((_, value)) = matching.next() else {
        return Ok(Some(Backend::WindowsHook));
    };
    if matching.next().is_some() {
        return Err(Unreadable);
    }
    match value {
        Value::Null => Ok(None),
        Value::Number(number) => Ok(number
            .as_i64()
            .and_then(|value| i32::try_from(value).ok())
            .and_then(normalize)),
        Value::String(text) => Ok(enum_parse(text).and_then(normalize)),
        _ => Err(Unreadable),
    }
}

/// Managed `KeyboardInputBackend.Normalize`.
fn normalize(value: i32) -> Option<Backend> {
    match value {
        1 => Some(Backend::WindowsHook),
        2 => Some(Backend::RazerDriver),
        _ => None,
    }
}

/// `Enum.TryParse(ignoreCase: true)` for `KeyboardInputBackend`:
/// - leading white space (`char.IsWhiteSpace`) is skipped;
/// - text that then starts with a digit or sign is one 32-bit integer with
///   optional trailing ASCII white space (`NumberStyles.AllowTrailingWhite`)
///   and then optional NULs, which .NET number parsing ignores at the end;
///   never comma-joined;
/// - anything else is comma-separated names, each trimmed and matched without
///   ASCII case, combined with OR; an empty or unknown name fails.
fn enum_parse(text: &str) -> Option<i32> {
    let text = text.trim_start();
    let first = text.chars().next()?;
    if first.is_ascii_digit() || first == '-' || first == '+' {
        // No name starts with a digit or sign, so a failed number fails.
        return text
            .trim_end_matches('\0')
            .trim_end_matches([' ', '\t', '\n', '\u{b}', '\u{c}', '\r'])
            .parse()
            .ok();
    }
    let mut value = 0;
    for part in text.split(',') {
        let part = part.trim();
        value |= if part.eq_ignore_ascii_case("Unknown") {
            0
        } else if part.eq_ignore_ascii_case("WindowsHook") {
            1
        } else if part.eq_ignore_ascii_case("RazerDriver") {
            2
        } else {
            return None;
        };
    }
    Some(value)
}

/// The supervisor's view of the preference.
pub(crate) trait Preference {
    fn poll(&mut self, now: Instant) -> Option<Backend>;
}

pub(crate) struct FilePreference {
    path: PathBuf,
    signature: Option<(u64, SystemTime)>,
    value: Option<Backend>,
    next: Option<Instant>,
    failing: bool,
    record: fn(Event),
}

impl FilePreference {
    pub(crate) fn new(path: PathBuf, record: fn(Event)) -> Self {
        Self {
            path,
            signature: None,
            value: None,
            next: None,
            failing: false,
            record,
        }
    }

    fn read(&mut self) -> Result<Option<Backend>, Unreadable> {
        let file = match fs::File::open(&self.path) {
            Ok(file) => file,
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
                self.signature = None;
                return parse(None);
            }
            Err(_) => return Err(Unreadable),
        };
        let metadata = file.metadata().map_err(|_| Unreadable)?;
        let signature = (metadata.len(), metadata.modified().map_err(|_| Unreadable)?);
        if self.signature == Some(signature) && !self.failing {
            return Ok(self.value);
        }
        if metadata.len() > MAXIMUM_BYTES {
            return Err(Unreadable);
        }
        let mut content = Vec::new();
        file.take(MAXIMUM_BYTES + 1)
            .read_to_end(&mut content)
            .map_err(|_| Unreadable)?;
        let value = parse(Some(&content))?;
        self.signature = Some(signature);
        Ok(value)
    }
}

impl Preference for FilePreference {
    fn poll(&mut self, now: Instant) -> Option<Backend> {
        if self.next.is_some_and(|next| now < next) {
            return self.value;
        }
        self.next = Some(now + POLL);
        match self.read() {
            Ok(value) => {
                self.value = value;
                self.failing = false;
            }
            Err(Unreadable) => {
                if !self.failing {
                    (self.record)(Event::FunctionRow(FunctionRowNotice::PreferencesUnreadable));
                }
                self.failing = true;
            }
        }
        self.value
    }
}

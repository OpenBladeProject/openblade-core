//! Managed `DiagnosticHistory`: a bounded, in-memory record of command
//! results and log lines. Managed keeps it only in process memory, with no
//! file and so no ACL; a restart starts empty. Native has no `ILogger`; its
//! producers record log lines directly (`record_log`), in managed's line
//! format.

// Only the profile store records a summary; other compositions export an
// empty history.
#![cfg_attr(not(any(test, feature = "service-profiles-v1")), allow(dead_code))]

use std::{collections::VecDeque, sync::Mutex};

use super::{
    Failure, Timestamp,
    redact::{redact_text, utf16_len},
};

const TRUNCATED_MARKER: &str = "...[truncated]";
/// Managed `AddLog` reads a carriage return as a line break.
const CARRIAGE_RETURN: char = '\r';
const LINE_FEED: char = '\n';
const LINE_FEED_TEXT: &str = "\n";

/// Managed `DiagnosticHistory` bounds, pinned by the contract file.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) struct Limits {
    pub(crate) entries: usize,
    pub(crate) characters: usize,
    pub(crate) line: usize,
}

impl Limits {
    /// `DefaultMaximumEntries`, `DefaultMaximumTotalCharacters` and
    /// `DefaultMaximumLineCharacters`.
    pub(crate) const DEFAULT: Self = Self {
        entries: 2048,
        characters: 4 * 1024 * 1024,
        line: 4096,
    };
}

/// Managed `DiagnosticCommandResult`.
#[derive(Clone, Debug, PartialEq, Eq)]
pub(crate) struct CommandResult {
    pub(crate) timestamp: Timestamp,
    pub(crate) subsystem: String,
    pub(crate) action: String,
    pub(crate) status: String,
    pub(crate) detail: Option<String>,
}

/// Managed `DiagnosticHistorySnapshot`.
#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub(crate) struct Snapshot {
    pub(crate) results: Vec<CommandResult>,
    pub(crate) log_lines: Vec<String>,
    pub(crate) evicted: u64,
    pub(crate) dropped: u64,
    pub(crate) truncated: u64,
    pub(crate) retained_entries: usize,
    pub(crate) retained_characters: usize,
}

enum Record {
    Result(CommandResult),
    Log(String),
}

struct Entry {
    record: Record,
    characters: usize,
}

/// What managed `AddLog` prefixes to each line: this process, this run of
/// it and the Service version.
struct Process {
    pid: u32,
    run: String,
    version: &'static str,
}

impl Process {
    /// Managed `Guid.NewGuid().ToString("N")` for the run: 32 lowercase hex
    /// digits from the process's random hash keys. It names a run, not a
    /// secret.
    fn current() -> Self {
        use std::hash::{BuildHasher, Hasher};
        let half = |salt: u64| {
            let mut hasher = std::collections::hash_map::RandomState::new().build_hasher();
            hasher.write_u64(salt);
            hasher.finish()
        };
        Self {
            pid: std::process::id(),
            run: format!("{:016x}{:016x}", half(0), half(1)),
            version: env!("CARGO_PKG_VERSION"),
        }
    }
}

#[derive(Default)]
struct State {
    entries: VecDeque<Entry>,
    characters: usize,
    evicted: u64,
    dropped: u64,
    truncated: u64,
}

type Clock = Box<dyn Fn() -> Timestamp + Send + Sync>;

pub(crate) struct History {
    limits: Limits,
    // Managed `DiagnosticText.Redact(value)`: the Service process's own
    // profile, local data, machine and user names.
    tokens: Vec<String>,
    clock: Clock,
    process: Process,
    state: Mutex<State>,
}

impl History {
    pub(crate) fn new(limits: Limits, tokens: Vec<String>, clock: Clock) -> Self {
        assert!(limits.entries > 0 && limits.characters > 0 && limits.line > 0);
        Self {
            limits,
            tokens,
            clock,
            process: Process::current(),
            state: Mutex::default(),
        }
    }

    /// This history with a fixed process identity, so a test can spell its
    /// log lines.
    #[cfg(test)]
    pub(crate) fn with_process(mut self, pid: u32, run: &str, version: &'static str) -> Self {
        self.process = Process {
            pid,
            run: run.to_owned(),
            version,
        };
        self
    }

    /// The Service's history: default bounds, the host's redaction tokens and
    /// the system clock. Test builds read no host identity.
    #[cfg(all(windows, feature = "service-thermal-mutation-v1"))]
    pub(crate) fn service() -> Self {
        #[cfg(not(test))]
        let tokens = super::host::default_tokens();
        #[cfg(test)]
        let tokens = Vec::new();
        Self::new(Limits::DEFAULT, tokens, Box::new(super::host::now))
    }

    /// A history nothing records into, as in a composition with no history
    /// producer: it redacts nothing, so it reads no host identity.
    #[cfg(windows)]
    pub(crate) fn unrecorded() -> Self {
        Self::new(Limits::DEFAULT, Vec::new(), Box::new(super::host::now))
    }

    /// Managed `RecordSummary(status, summary)`: a `Service`/`Reconcile`
    /// result. `status` is the managed `ControlOperationStatus` name.
    pub(crate) fn record_summary(&self, status: &str, summary: &str) {
        let timestamp = (self.clock)();
        let mut truncated = 0;
        let detail = self.clamp(summary, &mut truncated);
        self.add(
            CommandResult {
                timestamp,
                subsystem: "Service".into(),
                action: "Reconcile".into(),
                status: status.into(),
                detail: Some(detail),
            },
            truncated,
        );
    }

    /// Managed `AddCommandResult` and `Add`: every member clamped again,
    /// then retained or counted.
    fn add(&self, result: CommandResult, mut truncated: u64) {
        let result = CommandResult {
            subsystem: self.clamp(&result.subsystem, &mut truncated),
            action: self.clamp(&result.action, &mut truncated),
            status: self.clamp(&result.status, &mut truncated),
            detail: result
                .detail
                .map(|detail| self.clamp(&detail, &mut truncated)),
            ..result
        };
        let characters = utf16_len(&result.subsystem)
            + utf16_len(&result.action)
            + utf16_len(&result.status)
            + result.detail.as_deref().map_or(0, utf16_len);
        self.retain(Record::Result(result), characters, truncated);
    }

    /// Managed `AddLog(level, category, message)`: each logical line of the
    /// redacted message under managed's prefix, cut into pieces that fit
    /// the line bound with it. A piece keeps whole characters, as `clamp`
    /// does.
    #[cfg_attr(not(feature = "service-brightness-hid-usage-v1"), allow(dead_code))]
    pub(crate) fn record_log(&self, level: &str, category: &str, message: &str) {
        let (civil, fraction) = (self.clock)().civil();
        let mut truncated = 0;
        let prefix = format!(
            "{civil}.{fraction:07}+00:00 pid={} run={} version={} {level} {category}: ",
            self.process.pid, self.process.run, self.process.version
        );
        let limit = self.limits.line.saturating_sub(utf16_len(&prefix)).max(1);
        let normalized =
            redact_text(message, &self.tokens).replace(CARRIAGE_RETURN, LINE_FEED_TEXT);
        for logical in normalized.split(LINE_FEED).filter(|line| !line.is_empty()) {
            let mut piece = String::new();
            let mut units = 0;
            for character in logical.chars() {
                if units + character.len_utf16() > limit && !piece.is_empty() {
                    self.log_line(&prefix, &std::mem::take(&mut piece), &mut truncated);
                    units = 0;
                }
                units += character.len_utf16();
                piece.push(character);
            }
            if !piece.is_empty() {
                self.log_line(&prefix, &piece, &mut truncated);
            }
        }
        if truncated > 0 {
            self.count_truncated(truncated);
        }
    }

    fn log_line(&self, prefix: &str, content: &str, truncated: &mut u64) {
        let line = self.clamp(&format!("{prefix}{content}"), truncated);
        if !line.is_empty() {
            let characters = utf16_len(&line);
            self.retain(Record::Log(line), characters, std::mem::take(truncated));
        }
    }

    fn count_truncated(&self, truncated: u64) {
        self.state
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner)
            .truncated += truncated;
    }

    /// Managed `Add`: retained, evicting the oldest, or counted as dropped.
    fn retain(&self, record: Record, characters: usize, truncated: u64) {
        // A poisoned history refuses export (`snapshot`); its producers
        // keep recording into it.
        let mut state = self
            .state
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        state.truncated += truncated;
        if characters > self.limits.characters {
            state.dropped += 1;
            return;
        }
        while !state.entries.is_empty()
            && (state.entries.len() >= self.limits.entries
                || state.characters + characters > self.limits.characters)
        {
            let evicted = state.entries.pop_front().expect("a retained entry");
            state.characters -= evicted.characters;
            state.evicted += 1;
        }
        state.entries.push_back(Entry { record, characters });
        state.characters += characters;
    }

    /// Managed `Clamp`: redacted, on one line and within the line bound.
    /// A cut keeps whole characters, so it never splits a surrogate pair
    /// as a managed UTF-16 substring can.
    fn clamp(&self, value: &str, truncated: &mut u64) -> String {
        if value.is_empty() {
            return String::new();
        }
        let single = redact_text(value, &self.tokens).replace(['\r', '\n'], " ");
        let limit = self.limits.line;
        if utf16_len(&single) <= limit {
            return single;
        }
        *truncated += 1;
        if limit <= TRUNCATED_MARKER.len() {
            return TRUNCATED_MARKER[..limit].to_owned();
        }
        let mut kept = String::new();
        let mut units = 0;
        for character in single.chars() {
            units += character.len_utf16();
            if units > limit - TRUNCATED_MARKER.len() {
                break;
            }
            kept.push(character);
        }
        kept + TRUNCATED_MARKER
    }

    /// Managed `Snapshot`. A history poisoned by a panicking producer is
    /// unreadable, which managed reports as an I/O failure.
    pub(crate) fn snapshot(&self) -> Result<Snapshot, Failure> {
        let state = self
            .state
            .lock()
            .map_err(|_| Failure::new("Diagnostic history is unavailable."))?;
        Ok(Snapshot {
            results: state
                .entries
                .iter()
                .filter_map(|entry| match &entry.record {
                    Record::Result(result) => Some(result.clone()),
                    Record::Log(_) => None,
                })
                .collect(),
            log_lines: state
                .entries
                .iter()
                .filter_map(|entry| match &entry.record {
                    Record::Log(line) => Some(line.clone()),
                    Record::Result(_) => None,
                })
                .collect(),
            evicted: state.evicted,
            dropped: state.dropped,
            truncated: state.truncated,
            retained_entries: state.entries.len(),
            retained_characters: state.characters,
        })
    }

    #[cfg(test)]
    pub(crate) fn poison(&self) {
        let _ = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
            let _state = self.state.lock().unwrap();
            panic!("poisoned for the unreadable-history test");
        }));
    }
}

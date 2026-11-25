//! Native `exportDiagnostics`: the managed `DiagnosticArchiveProvider` and
//! `DiagnosticBundleExporter` archive over the caller-projected status this
//! Service already publishes, with the managed `DiagnosticHistory` retention.
//!
//! The archive is built from evidence the Service holds: the status snapshot
//! a GET would return, the bounded history and host metadata. No firmware,
//! device or storage read runs on the export path. Sections whose managed
//! source has no native producer carry exactly what managed writes when that
//! source is empty (`archive::Gap`).

use std::{
    fmt,
    sync::{
        Arc,
        atomic::{AtomicBool, Ordering},
    },
};

mod archive;
mod history;
#[cfg(windows)]
mod host;
mod json;
mod redact;
mod sections;
#[cfg(test)]
pub(crate) mod tests;
mod zip;

pub(crate) use archive::{Archive, Request, build};
pub(crate) use history::History;
#[cfg(test)]
pub(crate) use history::Limits;
#[cfg(windows)]
pub(crate) use host::WindowsHost;

/// Managed `OpenBladeIpc.MaximumDiagnosticArchiveBytes`.
pub(crate) const MAXIMUM_ARCHIVE_BYTES: usize = 16 * 1024 * 1024;
/// Managed `OpenBladeIpc.MaximumDiagnosticResponseBytes`: the frame bound
/// of the one response that carries an archive.
#[cfg(test)]
pub(crate) const MAXIMUM_RESPONSE_BYTES: usize = 24 * 1024 * 1024;

/// A UTC instant in .NET ticks: 100 ns intervals since 0001-01-01.
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord)]
pub(crate) struct Timestamp(u64);

const TICKS_PER_SECOND: u64 = 10_000_000;
const TICKS_PER_DAY: u64 = 86_400 * TICKS_PER_SECOND;
/// Days from 0001-01-01 to 1970-01-01.
const UNIX_EPOCH_DAYS: i64 = 719_162;
/// Ticks from 0001-01-01 to the FILETIME epoch, 1601-01-01.
#[cfg(windows)]
const FILETIME_EPOCH_TICKS: u64 = 504_911_232_000_000_000;

impl Timestamp {
    #[cfg(test)]
    pub(crate) const fn from_ticks(ticks: u64) -> Self {
        Self(ticks)
    }

    #[cfg(windows)]
    pub(crate) fn from_filetime(filetime: u64) -> Self {
        Self(filetime.saturating_add(FILETIME_EPOCH_TICKS))
    }

    /// The date and time of day as `yyyy-MM-ddTHH:mm:ss`, and the 100 ns
    /// fraction of its second.
    pub(crate) fn civil(self) -> (String, u64) {
        let ((year, month, day), (hour, minute, second)) = self.fields();
        (
            format!("{year:04}-{month:02}-{day:02}T{hour:02}:{minute:02}:{second:02}"),
            self.0 % TICKS_PER_SECOND,
        )
    }

    /// Managed `yyyyMMdd-HHmmss`.
    pub(crate) fn compact(self) -> String {
        let ((year, month, day), (hour, minute, second)) = self.fields();
        format!("{year:04}{month:02}{day:02}-{hour:02}{minute:02}{second:02}")
    }

    /// The MS-DOS (time, date) pair a ZIP header stores for this local time.
    pub(crate) fn dos(self) -> (u16, u16) {
        let ((year, month, day), (hour, minute, second)) = self.fields();
        let year = year.clamp(1980, 2107);
        let time = (hour << 11) | (minute << 5) | (second / 2);
        let date = ((year - 1980) << 9) | (month << 5) | day;
        (time as u16, date as u16)
    }

    fn fields(self) -> ((u32, u32, u32), (u32, u32, u32)) {
        let days = (self.0 / TICKS_PER_DAY) as i64 - UNIX_EPOCH_DAYS;
        let seconds = (self.0 % TICKS_PER_DAY) / TICKS_PER_SECOND;
        // Howard Hinnant's civil_from_days.
        let z = days + 719_468;
        let era = z.div_euclid(146_097);
        let doe = z.rem_euclid(146_097);
        let yoe = (doe - doe / 1460 + doe / 36_524 - doe / 146_096) / 365;
        let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
        let mp = (5 * doy + 2) / 153;
        let day = doy - (153 * mp + 2) / 5 + 1;
        let month = if mp < 10 { mp + 3 } else { mp - 9 };
        let year = yoe + era * 400 + i64::from(month <= 2);
        (
            (year as u32, month as u32, day as u32),
            (
                (seconds / 3600) as u32,
                (seconds / 60 % 60) as u32,
                (seconds % 60) as u32,
            ),
        )
    }

    /// Parses managed round-trip text with a `+00:00` offset.
    #[cfg(test)]
    pub(crate) fn parse(text: &str) -> Option<Self> {
        let text = text.strip_suffix("+00:00")?;
        let (date, time) = text.split_once('T')?;
        let mut date = date.split('-').map(str::parse::<i64>);
        let (year, month, day) = (date.next()?.ok()?, date.next()?.ok()?, date.next()?.ok()?);
        let (clock, fraction) = time.split_once('.').unwrap_or((time, ""));
        let mut clock = clock.split(':').map(str::parse::<u64>);
        let (hour, minute, second) = (
            clock.next()?.ok()?,
            clock.next()?.ok()?,
            clock.next()?.ok()?,
        );
        let fraction = if fraction.is_empty() {
            0
        } else {
            format!("{fraction:0<7}").parse::<u64>().ok()?
        };
        // days_from_civil.
        let year = if month <= 2 { year - 1 } else { year };
        let era = year.div_euclid(400);
        let yoe = year.rem_euclid(400);
        let mp = (month + 9) % 12;
        let doy = (153 * mp + 2) / 5 + day - 1;
        let doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
        let days = era * 146_097 + doe - 719_468 + UNIX_EPOCH_DAYS;
        Some(Self(
            u64::try_from(days).ok()? * TICKS_PER_DAY
                + (hour * 3600 + minute * 60 + second) * TICKS_PER_SECOND
                + fraction,
        ))
    }
}

/// A failed export: managed's `InvalidDataException`, `IOException` or
/// `UnauthorizedAccessException` message, replied as
/// `Diagnostic export failed: {message}`.
#[derive(Debug, PartialEq, Eq)]
pub(crate) struct Failure(pub(crate) String);

impl Failure {
    pub(crate) fn new(message: impl Into<String>) -> Self {
        Self(message.into())
    }

    pub(crate) fn reply(&self) -> String {
        format!("Diagnostic export failed: {}", self.0)
    }
}

impl fmt::Display for Failure {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str(&self.0)
    }
}

/// What the host supplies at capture: managed `DiagnosticHostMetadata`
/// without the firmware versions, which come from the status.
#[derive(Clone, Debug)]
pub(crate) struct HostMetadata {
    /// Managed `Environment.OSVersion.VersionString`.
    pub(crate) windows_version: String,
    /// Managed `WindowsDiagnosticMetadataSource.ReadSensitiveTokens`.
    pub(crate) tokens: Vec<String>,
    pub(crate) captured_at: Timestamp,
    /// The capture's local time, for the ZIP entry timestamps managed
    /// `ZipArchive.CreateEntry` stamps.
    pub(crate) local_time: Timestamp,
}

/// Managed `IDiagnosticMetadataSource`. `cancel` is the export's, as
/// managed passes its cancellation token.
pub(crate) trait Metadata: Send + Sync {
    fn capture(&self, cancel: &Cancel) -> Result<HostMetadata, Failure>;
}

/// The diagnostic sources one composition owns.
#[derive(Clone)]
pub(crate) struct Sources {
    pub(crate) history: Arc<History>,
    pub(crate) metadata: Arc<dyn Metadata>,
}

impl Sources {
    /// The Service's own sources: the host metadata and an empty history,
    /// for a composition with no history producer.
    #[cfg(windows)]
    pub(crate) fn service() -> Self {
        Self {
            history: Arc::new(History::unrecorded()),
            metadata: Arc::new(WindowsHost),
        }
    }
}

/// Set when the requester disconnects, the export's time bound passes or the
/// Service stops; the build stops at its next check.
#[derive(Clone, Default)]
pub(crate) struct Cancel(Arc<AtomicBool>);

impl Cancel {
    pub(crate) fn cancel(&self) {
        self.0.store(true, Ordering::Release);
    }

    pub(crate) fn check(&self) -> Result<(), Failure> {
        if self.0.load(Ordering::Acquire) {
            Err(Failure::new("The diagnostic export was canceled."))
        } else {
            Ok(())
        }
    }
}

/// Cancels on drop, so an aborted connection task stops its build.
pub(crate) struct CancelOnDrop(pub(crate) Cancel);

impl Drop for CancelOnDrop {
    fn drop(&mut self) {
        self.0.cancel();
    }
}

/// RFC 4648 base64, as managed `System.Text.Json` writes a `byte[]`.
pub(crate) fn base64(bytes: &[u8]) -> String {
    const ALPHABET: &[u8; 64] = b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    let mut text = String::with_capacity(bytes.len().div_ceil(3) * 4);
    for chunk in bytes.chunks(3) {
        let value = chunk.iter().enumerate().fold(0u32, |value, (index, byte)| {
            value | u32::from(*byte) << (16 - 8 * index)
        });
        for index in 0..4 {
            text.push(if index <= chunk.len() {
                char::from(ALPHABET[(value >> (18 - 6 * index) & 63) as usize])
            } else {
                '='
            });
        }
    }
    text
}

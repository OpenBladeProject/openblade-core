//! Best-effort current-user diagnostics for the selected native Session Agent.
//! Fixed events contain no peer identity, report data, paths or free-form text.
#![allow(unsafe_code)]
use crate::battery_report::{Failure, custody};
use std::{
    ffi::OsStr,
    fs::File,
    io::{self, Seek, SeekFrom, Write},
    os::windows::{
        fs::MetadataExt,
        io::{AsRawHandle, FromRawHandle, OwnedHandle},
    },
    path::Path,
    ptr::null_mut,
    sync::Mutex,
    time::{SystemTime, UNIX_EPOCH},
};
use windows_sys::{
    Wdk::Storage::FileSystem::{FILE_DIRECTORY_FILE, FILE_NON_DIRECTORY_FILE, FILE_OPEN_IF},
    Win32::{
        Security::{
            GetTokenInformation, TOKEN_ELEVATION, TOKEN_QUERY, TokenElevation, TokenSessionId,
        },
        Storage::FileSystem::{
            FILE_ATTRIBUTE_REPARSE_POINT, FILE_READ_ATTRIBUTES, FILE_READ_DATA, FILE_SHARE_READ,
            FILE_SHARE_WRITE, FILE_WRITE_DATA,
        },
        System::Threading::{GetCurrentProcess, OpenProcessToken},
    },
};

#[derive(Debug, Clone, Copy, Eq, PartialEq)]
pub enum Event {
    BatteryCleanup,
    BatteryRetirement(Failure),
    Origin(io::ErrorKind, Option<i32>),
    Listener(io::ErrorKind, Option<i32>),
    AgentFailure(io::ErrorKind, Option<i32>),
    /// Another Agent already owns a feature pipe or the stop event. Not a
    /// failure: the Agent exits 0 quietly, as managed does, leaving this line.
    PipeAlreadyOwned(Option<i32>),
    StopEndpointAlreadyOwned(Option<i32>),
    FunctionRow(FunctionRowNotice),
    /// Windows power-mode automation could not apply an association, as
    /// managed traces a failed `WindowsPowerModeAutomationResult`.
    WindowsPowerMode(PowerModeNotice),
}

/// The failed step of one Windows power-mode apply. Win32 codes only.
#[derive(Debug, Clone, Copy, Eq, PartialEq)]
pub enum PowerModeNotice {
    ReadFailed(u32),
    SetFailed(u32),
    ConfirmFailed(u32),
    /// Windows confirmed a different configured mode than the one set.
    NotRetained,
    /// The user-configured power mode functions could not be resolved.
    ApiUnavailable,
}

/// Fixed notices of the function-row role. None carries a key sequence, a
/// timing or a path.
#[derive(Debug, Clone, Copy, Eq, PartialEq)]
pub enum FunctionRowNotice {
    /// The policy admitted this action, but its effect is not admitted in
    /// this stage, so nothing was executed. Recorded once per action.
    EffectNotAdmitted(openblade_domain::function_row::Action),
    /// A Razer-driver ownership journal may still own the function row, so
    /// the Windows hook is not installed.
    DriverOwnershipJournalPresent,
    /// The driver ownership journals could not be observed.
    DriverOwnershipUnobservable,
    /// The per-user preferences could not be read; the last value read stays.
    PreferencesUnreadable,
    /// A hook run ended. An unclean retirement leaves ownership uncertain, so
    /// the role never installs again in this Agent's lifetime.
    HookRetired {
        clean: bool,
        error: Option<crate::function_row_hook::HookError>,
    },
}

// Both fixed names match the existing managed diagnostics export glob. Keep
// the preceding 1 MiB segment when the current file fills. With no timestamps
// used for ordering, a clock adjustment cannot reverse rotation.
const FILES: [&str; 2] = [
    "OpenBlade.SessionAgent.Native.log",
    "OpenBlade.SessionAgent.Native.previous.log",
];
const LIMIT: u64 = 1024 * 1024;

static LOGS: Mutex<Option<File>> = Mutex::new(None);

#[must_use]
pub struct Registration {
    _private: (),
}

/// Only the selected installed entry registers a sink. Keep the exact logs
/// directory handle for its lifetime; subsequent opens are relative to it.
pub fn register() -> Option<Registration> {
    if !ordinary_session() {
        return None;
    }
    let mut logs = LOGS.lock().ok()?;
    if logs.is_some() {
        return None;
    }
    *logs = Some(open_logs(&custody::local_data().ok()?).ok()?);
    Some(Registration { _private: () })
}

impl Drop for Registration {
    fn drop(&mut self) {
        if let Ok(mut logs) = LOGS.lock() {
            logs.take();
        }
    }
}

pub fn record(event: Event) {
    let stored = LOGS
        .lock()
        .ok()
        .and_then(|logs| logs.as_ref().map(|dir| store(dir, event, LIMIT)));
    fallback(event, stored, &mut io::stderr());
}

const MAX_FALLBACK_BYTES: usize = 512;

fn fallback(event: Event, stored: Option<io::Result<()>>, stderr: &mut impl Write) {
    if matches!(stored, Some(Ok(()))) {
        return;
    }
    // This fixed representation accepts only enum variants and numbers. The
    // persistence error itself may contain a path and is never formatted.
    let mut buffer = [0u8; MAX_FALLBACK_BYTES];
    let mut line = io::Cursor::new(&mut buffer[..]);
    if writeln!(line, "OpenBlade.SessionAgent.Native {event:?}").is_ok() {
        let count = line.position() as usize;
        let _ = stderr.write_all(&buffer[..count]);
    }
}
fn ordinary_session() -> bool {
    let mut raw = null_mut();
    // SAFETY: read only the current process token. Windows returns an owned handle.
    if unsafe { OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &mut raw) } == 0 {
        return false;
    }
    let token = unsafe { OwnedHandle::from_raw_handle(raw) };
    let mut elevation = TOKEN_ELEVATION::default();
    let mut session = 0u32;
    let mut size = 0;
    // SAFETY: exact output types and sizes for these token information classes.
    unsafe {
        GetTokenInformation(
            token.as_raw_handle(),
            TokenElevation,
            (&raw mut elevation).cast(),
            size_of_val(&elevation) as u32,
            &mut size,
        ) != 0
            && elevation.TokenIsElevated == 0
            && GetTokenInformation(
                token.as_raw_handle(),
                TokenSessionId,
                (&raw mut session).cast(),
                size_of_val(&session) as u32,
                &mut size,
            ) != 0
            && session != 0
    }
}

fn open_logs(root: &Path) -> io::Result<File> {
    let mut held = custody::pin_directories(root)?;
    for dir in ["OpenBlade", "Logs"] {
        let child = custody::relative(
            held.last().expect("local root pinned"),
            OsStr::new(dir),
            FILE_READ_DATA | FILE_READ_ATTRIBUTES,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            FILE_OPEN_IF,
            FILE_DIRECTORY_FILE,
            None,
        )?;
        let meta = child.metadata()?;
        if !meta.is_dir() || meta.file_attributes() & FILE_ATTRIBUTE_REPARSE_POINT != 0 {
            return Err(io::Error::other("invalid diagnostic directory"));
        }
        held.push(child);
    }
    Ok(held.pop().expect("logs directory pinned"))
}

fn store(parent: &File, event: Event, limit: u64) -> io::Result<()> {
    let sd = custody::private_descriptor()?;
    let open = |name: &str| -> io::Result<File> {
        let file = custody::relative(
            parent,
            OsStr::new(name),
            FILE_READ_ATTRIBUTES | FILE_READ_DATA | FILE_WRITE_DATA,
            FILE_SHARE_READ,
            FILE_OPEN_IF,
            FILE_NON_DIRECTORY_FILE,
            Some(&sd),
        )?;
        custody::plain_identity(&file)?;
        Ok(file)
    };

    // The primary file is always opened first, excluding concurrent writers
    // across processes. Only these fixed, verified single-link files can change.
    let mut current = open(FILES[0])?;
    let timestamp = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_millis();
    let line = format!("{timestamp} OpenBlade.SessionAgent.Native {event:?}\n");
    if line.len() as u64 > limit {
        return Err(io::Error::other("diagnostic record exceeds limit"));
    }
    let size = current.metadata()?.len();
    if size.saturating_add(line.len() as u64) > limit {
        let mut previous = open(FILES[1])?;
        previous.set_len(0)?;
        if size <= limit {
            let copied =
                std::io::copy(&mut std::io::Read::take(&mut current, size), &mut previous)?;
            if copied != size {
                return Err(io::Error::other("incomplete diagnostic rotation"));
            }
            previous.flush()?;
        }
        current.set_len(0)?;
    }
    current.seek(SeekFrom::End(0))?;
    current.write_all(line.as_bytes())?;
    current.flush()
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::fs;
    use windows_sys::{Win32::System::Com::CoCreateGuid, core::GUID};

    #[test]
    fn fallback_delivers_only_the_typed_event_when_persistence_is_unavailable() {
        let event = Event::BatteryRetirement(Failure::StopUnconfirmed);
        let mut bytes = Vec::new();
        fallback(event, Some(Ok(())), &mut bytes);
        assert!(bytes.is_empty());

        for outcome in [
            None,
            Some(Err(io::Error::other(r"C:\sensitive\report.xml"))),
        ] {
            bytes.clear();
            fallback(event, outcome, &mut bytes);
            assert_eq!(
                bytes,
                b"OpenBlade.SessionAgent.Native BatteryRetirement(StopUnconfirmed)\n"
            );
            assert!(bytes.len() <= MAX_FALLBACK_BYTES);
        }
        // A fixed-capacity writer with no space reports WriteZero. Failure of
        // the last sink is best-effort and cannot replace the product outcome.
        fallback(event, None, &mut &mut [][..]);
    }
    #[test]
    fn an_already_owned_note_is_a_fixed_non_failure_line_with_the_os_error() {
        for (event, expected) in [
            (
                Event::PipeAlreadyOwned(Some(5)),
                &b"OpenBlade.SessionAgent.Native PipeAlreadyOwned(Some(5))
"[..],
            ),
            (
                Event::StopEndpointAlreadyOwned(Some(183)),
                &b"OpenBlade.SessionAgent.Native StopEndpointAlreadyOwned(Some(183))
"[..],
            ),
        ] {
            let mut bytes = Vec::new();
            fallback(event, None, &mut bytes);
            assert_eq!(bytes, expected);
            // The terminal failure record is a different event.
            assert!(!matches!(event, Event::AgentFailure(..)));
        }
    }

    #[test]
    fn typed_native_events_are_exportable_bounded_and_reject_foreign_hardlinks() {
        let mut id = GUID::default();
        assert!(unsafe { CoCreateGuid(&mut id) } >= 0);
        let root = std::env::temp_dir().join(format!(
            "OpenBlade.AgentDiagnostics.{:08x}{:04x}{:04x}{:016x}",
            id.data1,
            id.data2,
            id.data3,
            u64::from_be_bytes(id.data4)
        ));
        fs::create_dir(&root).unwrap();
        let dir = root.join("OpenBlade").join("Logs");
        let current = dir.join(FILES[0]);
        let previous = dir.join(FILES[1]);
        let limit = 220;
        let parent = open_logs(&root).unwrap();
        for _ in 0..8 {
            store(&parent, Event::BatteryCleanup, limit).unwrap();
        }
        store(
            &parent,
            Event::AgentFailure(io::ErrorKind::PermissionDenied, Some(5)),
            limit,
        )
        .unwrap();
        for path in [&current, &previous] {
            assert!((1..=limit).contains(&fs::metadata(path).unwrap().len()));
        }
        assert!(
            fs::read_to_string(&current)
                .unwrap()
                .contains("AgentFailure(PermissionDenied, Some(5))")
        );
        assert!(
            fs::read_to_string(&previous)
                .unwrap()
                .contains("BatteryCleanup")
        );
        fs::remove_file(&current).unwrap();
        let other = root.join("untouched");
        fs::write(&other, b"original").unwrap();
        fs::hard_link(&other, &current).unwrap();
        assert!(store(&parent, Event::BatteryCleanup, limit).is_err());
        assert_eq!(fs::read(&other).unwrap(), b"original");
        fs::remove_file(&current).unwrap();
        fs::remove_file(&other).unwrap();
        fs::remove_file(&previous).unwrap();
        drop(parent);
        fs::remove_dir(&dir).unwrap();
        fs::remove_dir(dir.parent().unwrap()).unwrap();
        fs::remove_dir(&root).unwrap();
    }
}

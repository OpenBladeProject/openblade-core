//! Fixed managed-byte fixture for the foreground process harness.

use super::{
    Boundary, CURRENT_SCHEMA, FILE_SHARE_READ, Inspection, Outcome, TestStore, file_identity,
    handle_information, pin_directory_with_share, transaction_id,
};
use serde::Deserialize;
use std::{io, path::PathBuf};
use windows_sys::Win32::{
    Storage::FileSystem::GetDriveTypeW,
    System::{Com::CoTaskMemFree, WindowsProgramming::DRIVE_FIXED},
    UI::Shell::{FOLDERID_LocalAppData, SHGetKnownFolderPath},
};

const FIXTURE: &str = include_str!(
    "../../../../tests/OpenBlade.Service.Tests/WindowsReplay/v1/configuration-aggregate.json"
);

#[derive(Deserialize)]
struct Documents {
    prior: [String; 3],
    committed: [String; 3],
}

pub struct Fixture {
    id: String,
    store: TestStore,
    documents: Documents,
}

impl Fixture {
    /// This is the only constructor with write authority. An existing root
    /// (including a reparse point) makes the exclusive create fail.
    pub fn create() -> io::Result<Self> {
        let documents = serde_json::from_str(FIXTURE)?;
        let (temp, pins) = TestStore::pin_temp(&local_temp()?)?;
        // An empty Temp directory cannot be redirected while we create its
        // first child. Drop this narrow pin before any transaction replacements.
        let _temp_creation_pin = pin_directory_with_share(&temp, FILE_SHARE_READ)?;
        let id = transaction_id()?;
        let store = TestStore::create(root(temp, &id)?, pins, false)?;
        Ok(Self {
            id,
            store,
            documents,
        })
    }

    pub fn restart_key(&self) -> io::Result<(&str, String)> {
        Ok((&self.id, identity(&self.store)?))
    }

    pub fn seed(&self) -> io::Result<u8> {
        self.write(&self.documents.prior, false)
    }

    pub fn update(self, interrupt: bool) -> io::Result<u8> {
        self.write(&self.documents.committed, interrupt)
    }

    fn write(&self, intent: &[String; 3], interrupt: bool) -> io::Result<u8> {
        let report = self
            .store
            .commit(intent.each_ref().map(|doc| doc.as_bytes()), |at| {
                if interrupt && matches!(at, Boundary::AfterReplace(0) | Boundary::BeforeRestore(0))
                {
                    return Err(io::Error::other("injected developer interruption"));
                }
                Ok(())
            });
        match &report.outcome {
            Outcome::Committed if !interrupt && report.artifact_errors.is_empty() => {
                load(&self.store, &[intent])
            }
            Outcome::RecoveryRequired {
                cause,
                resolution: Some(resolution),
            } if interrupt => Err(io::Error::other(format!(
                "fixture interrupted: {cause}; {resolution}"
            ))),
            Outcome::InvalidIntent(inspection) | Outcome::Refused(inspection) => Err(
                io::Error::other(format!("fixture transaction refused: {inspection:?}")),
            ),
            Outcome::Unchanged(error) | Outcome::Restored(error) => Err(io::Error::other(format!(
                "fixture transaction left the prior configuration in place: {error}"
            ))),
            _ => Err(io::Error::other(format!(
                "fixture transaction failed: {report:?}"
            ))),
        }
    }
}

/// Reopening is read-only, even with a valid identity and known committed data.
/// No path, file names, or payload bytes are supplied by the caller.
pub fn restart(id: &str, expected: &str) -> io::Result<u8> {
    let (temp, mut pins) = TestStore::pin_temp(&local_temp()?)?;
    let root = root(temp, id)?;
    pins.push(pin_directory_with_share(&root, FILE_SHARE_READ)?);
    let store = TestStore {
        root,
        pins,
        cleanup_on_drop: false,
        held_lock: None,
    };
    if identity(&store)? != expected {
        return Err(io::Error::other("fixture root identity changed"));
    }
    let documents: Documents = serde_json::from_str(FIXTURE)?;
    load(&store, &[&documents.prior, &documents.committed])
}

fn load(store: &TestStore, allowed: &[&[String; 3]]) -> io::Result<u8> {
    let (inspection, bytes) = store.inspect_snapshot()?;
    if inspection
        != (Inspection::CommittedEnvelope {
            schema: CURRENT_SCHEMA,
        })
    {
        return Err(io::Error::other(format!(
            "fixture startup refused: {inspection:?}"
        )));
    }
    if !allowed.iter().any(|docs| {
        docs.iter()
            .zip(&bytes)
            .all(|(doc, bytes)| Some(doc.as_bytes()) == bytes.as_deref())
    }) {
        return Err(io::Error::other(
            "configuration outside the managed fixture",
        ));
    }
    let profile: serde_json::Value = serde_json::from_slice(
        bytes[0]
            .as_deref()
            .ok_or_else(|| io::Error::other("fixture profile missing"))?,
    )?;
    profile["profiles"]["batteryPolicy"]["chargeLimitPercent"]
        .as_u64()
        .and_then(|percent| u8::try_from(percent).ok())
        .ok_or_else(|| io::Error::other("fixture charge limit missing or invalid"))
}

fn root(temp: PathBuf, id: &str) -> io::Result<PathBuf> {
    if id.len() != 32
        || !id
            .bytes()
            .all(|byte| matches!(byte, b'0'..=b'9' | b'a'..=b'f'))
    {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "invalid fixture ID",
        ));
    }
    Ok(temp.join(format!("openblade-durable-host-{id}")))
}

fn identity(store: &TestStore) -> io::Result<String> {
    let directory = store
        .pins
        .last()
        .ok_or_else(|| io::Error::other("fixture root is unpinned"))?;
    let id = file_identity(&handle_information(directory)?);
    Ok(format!("{:08x}{:016x}", id.volume, id.index))
}

fn local_temp() -> io::Result<PathBuf> {
    use std::{
        ffi::OsString,
        os::windows::ffi::{OsStrExt, OsStringExt},
        ptr, slice,
    };

    // Resolve the Windows known folder, independently of TEMP, TMP, CWD and
    // command-line arguments. The shared store pins each plain path component.
    let mut path = ptr::null_mut();
    // SAFETY: The known folder ID and output pointer are valid; a null token
    // requests the current user's known folder.
    let result =
        unsafe { SHGetKnownFolderPath(&FOLDERID_LocalAppData, 0, ptr::null_mut(), &mut path) };
    let folder = if result >= 0 && !path.is_null() {
        // SAFETY: On success the API returns a NUL-terminated UTF-16 string.
        let wide = unsafe {
            let mut end = path;
            while *end != 0 {
                end = end.add(1);
            }
            slice::from_raw_parts(path, end.offset_from(path) as usize)
        };
        Ok(PathBuf::from(OsString::from_wide(wide)).join("Temp"))
    } else {
        Err(io::Error::other(
            "could not resolve the local fixture directory",
        ))
    };
    // SAFETY: The API allocates the output with the COM task allocator, and
    // the allocator accepts null.
    unsafe { CoTaskMemFree(path.cast()) };
    let folder = folder?;
    let drive = folder
        .ancestors()
        .filter(|path| path.has_root())
        .last()
        .ok_or_else(|| io::Error::other("fixture directory has no drive root"))?;
    let drive: Vec<u16> = drive.as_os_str().encode_wide().chain([0]).collect();
    // SAFETY: drive is a NUL-terminated UTF-16 drive-root path.
    if unsafe { GetDriveTypeW(drive.as_ptr()) } != DRIVE_FIXED {
        return Err(io::Error::other(
            "fixture directory requires a local fixed drive",
        ));
    }
    Ok(folder)
}

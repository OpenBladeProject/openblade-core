//! Windows fixture-only execution of the managed aggregate transaction.
#![allow(unsafe_code)]

use crate::configuration::{
    COMMIT, CURRENT_SCHEMA, File as ObservedFile, Inspection, PREFERENCES, PROFILE,
    RECOVERY_REQUIRED, RecoveryRecord, Snapshot, inspect,
};
use serde::Serialize;
use std::ffi::OsStr;
use std::fs::{self, File, OpenOptions};
use std::io::{self, Read, Write};
use std::os::windows::ffi::OsStrExt;
use std::os::windows::fs::{MetadataExt, OpenOptionsExt};
use std::os::windows::io::AsRawHandle;
use std::path::{Component, Path, PathBuf, Prefix};
use windows_sys::Win32::Storage::FileSystem::{
    BY_HANDLE_FILE_INFORMATION, DELETE, FILE_ATTRIBUTE_DIRECTORY, FILE_ATTRIBUTE_REPARSE_POINT,
    FILE_DISPOSITION_INFO, FILE_FLAG_BACKUP_SEMANTICS, FILE_FLAG_OPEN_REPARSE_POINT,
    FILE_FLAG_WRITE_THROUGH, FILE_GENERIC_READ, FILE_GENERIC_WRITE, FILE_SHARE_READ,
    FILE_SHARE_WRITE, FileDispositionInfo, GetFileInformationByHandle, MOVEFILE_REPLACE_EXISTING,
    MOVEFILE_WRITE_THROUGH, MoveFileExW, SetFileInformationByHandle,
};
use windows_sys::Win32::System::Com::CoCreateGuid;

pub mod fixture;
pub mod protected;

const NAMES: [&str; 3] = [PROFILE, PREFERENCES, COMMIT];
const LOCK: &str = "configuration.test-lock";
const MAX_DOCUMENT: u64 = 8 * 1024 * 1024;

#[derive(Debug, PartialEq, Eq)]
enum Boundary {
    BeforeStage(usize),
    BeforeRecord,
    RecordFlushed,
    BeforeReplace(usize),
    AfterReplace(usize),
    BeforeRestore(usize),
    BeforeClear,
    BeforeArtifactCleanup,
}

#[derive(Debug)]
enum Outcome {
    Committed,
    InvalidIntent(Inspection),
    Refused(Inspection),
    Unchanged(io::Error),
    Restored(io::Error),
    RecoveryRequired {
        cause: io::Error,
        resolution: Option<io::Error>,
    },
}

#[derive(Debug)]
struct Report {
    outcome: Outcome,
    artifact_errors: Vec<io::Error>,
}

impl Report {
    fn new(outcome: Outcome) -> Self {
        Self {
            outcome,
            artifact_errors: Vec::new(),
        }
    }
}

// Construction is private to the native tests and the fixed developer fixture.
struct TestStore {
    root: PathBuf,
    pins: Vec<File>,
    cleanup_on_drop: bool,
    held_lock: Option<File>,
}

impl TestStore {
    #[cfg(test)]
    fn new() -> io::Result<Self> {
        let (temp, pins) = Self::pin_temp(&std::env::temp_dir())?;
        let root = temp.join(format!("openblade-config-writer-{}", transaction_id()?));
        fs::create_dir(&root)?;
        Self::initialize(root, pins, None)
    }

    fn pin_temp(temp: &Path) -> io::Result<(PathBuf, Vec<File>)> {
        if !matches!(
            temp.components().next(),
            Some(Component::Prefix(prefix)) if matches!(prefix.kind(), Prefix::Disk(_))
        ) || !temp.is_absolute()
            || temp.components().any(|part| part == Component::ParentDir)
        {
            return Err(io::Error::other("expected a local absolute test temp path"));
        }

        // Deny delete sharing on each directory for the life of the root.
        // Check from the drive down so an unchecked ancestor is never followed.
        let mut pins = Vec::new();
        let directories: Vec<_> = temp.ancestors().filter(|path| path.has_root()).collect();
        for directory in directories.into_iter().rev() {
            pins.push(pin_directory(directory)?);
        }
        Ok((temp.components().collect(), pins))
    }

    fn initialize(
        root: PathBuf,
        mut pins: Vec<File>,
        creation_pin: Option<File>,
    ) -> io::Result<Self> {
        pins.push(pin_directory(&root)?);
        let mut store = Self {
            root,
            pins,
            cleanup_on_drop: true,
            held_lock: None,
        };
        let lock = OpenOptions::new()
            .write(true)
            .create_new(true)
            .share_mode(0)
            .custom_flags(FILE_FLAG_OPEN_REPARSE_POINT)
            .open(store.path(LOCK))?;
        // Keep the live fixture nonempty once MoveFileExW needs directory write sharing.
        if creation_pin.is_some() {
            store.held_lock = Some(lock);
        }
        drop(creation_pin);
        Ok(store)
    }

    fn path(&self, name: &str) -> PathBuf {
        debug_assert!(
            name.bytes()
                .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'.' | b'-'))
        );
        self.root.join(name)
    }

    fn lock(&self) -> io::Result<Option<File>> {
        if self.held_lock.is_some() {
            return Ok(None);
        }
        let lock = OpenOptions::new()
            .read(true)
            .share_mode(0)
            .custom_flags(FILE_FLAG_OPEN_REPARSE_POINT)
            .open(self.path(LOCK))?;
        check_regular_file(&lock)?;
        Ok(Some(lock))
    }

    #[cfg(test)]
    fn restart_inspection(&self) -> io::Result<Inspection> {
        self.inspect_snapshot().map(|(inspection, _)| inspection)
    }

    fn inspect_snapshot(&self) -> io::Result<(Inspection, [Option<Vec<u8>>; 3])> {
        let _lock = self.lock()?;
        if self.record_present()? {
            let inspection = inspect(Snapshot {
                recovery: RecoveryRecord::Present,
                profile: ObservedFile::Unavailable,
                preferences: ObservedFile::Unavailable,
                commit: ObservedFile::Unavailable,
            });
            return Ok((inspection, [None, None, None]));
        }
        let prior = self.capture()?;
        if self.record_present()? {
            return Err(io::Error::other(
                "recovery record appeared during inspection",
            ));
        }
        Ok((inspect_bytes(prior.each_ref().map(Option::as_deref)), prior))
    }

    fn record_present(&self) -> io::Result<bool> {
        match fs::symlink_metadata(self.path(RECOVERY_REQUIRED)) {
            Ok(_) => Ok(true),
            Err(error) if error.kind() == io::ErrorKind::NotFound => Ok(false),
            Err(error) => Err(error),
        }
    }

    fn read(&self, name: &str) -> io::Result<Option<Vec<u8>>> {
        let file = match OpenOptions::new()
            .read(true)
            .share_mode(FILE_SHARE_READ)
            .custom_flags(FILE_FLAG_OPEN_REPARSE_POINT)
            .open(self.path(name))
        {
            Ok(file) => file,
            Err(error) if error.kind() == io::ErrorKind::NotFound => return Ok(None),
            Err(error) => return Err(error),
        };
        read_file(&file).map(|(_, bytes)| Some(bytes))
    }

    fn capture(&self) -> io::Result<[Option<Vec<u8>>; 3]> {
        Ok([
            self.read(PROFILE)?,
            self.read(PREFERENCES)?,
            self.read(COMMIT)?,
        ])
    }

    fn expect(&self, name: &str, expected: Option<&[u8]>) -> io::Result<()> {
        if self.read(name)?.as_deref() != expected {
            return Err(io::Error::other(format!(
                "unexpected bytes or presence at {name}"
            )));
        }
        Ok(())
    }

    fn create_durable(&self, name: &str, bytes: &[u8]) -> io::Result<File> {
        let is_record = name == RECOVERY_REQUIRED;
        let access = FILE_GENERIC_WRITE | if is_record { DELETE } else { 0 };
        let mut file = OpenOptions::new()
            .write(true)
            .access_mode(access)
            .create_new(true)
            .share_mode(if is_record { FILE_SHARE_READ } else { 0 })
            .custom_flags(FILE_FLAG_WRITE_THROUGH | FILE_FLAG_OPEN_REPARSE_POINT)
            .open(self.path(name))?;
        file.write_all(bytes)
            .and_then(|()| file.sync_all())
            .map_err(|error| {
                io::Error::new(
                    error.kind(),
                    format!("could not write and flush {name}, file retained: {error}"),
                )
            })?;
        Ok(file)
    }

    fn create_artifact<'a>(&self, name: &str, bytes: &'a [u8]) -> io::Result<Artifact<'a>> {
        let file = self.create_durable(name, bytes)?;
        Ok(Artifact {
            name: name.to_owned(),
            bytes,
            identity: check_regular_file(&file)?,
        })
    }

    fn delete_exact(
        &self,
        name: &str,
        expected: &[u8],
        owned_artifact: Option<FileIdentity>,
    ) -> io::Result<()> {
        let file = match OpenOptions::new()
            .access_mode(FILE_GENERIC_READ | DELETE)
            .share_mode(FILE_SHARE_READ)
            .custom_flags(FILE_FLAG_OPEN_REPARSE_POINT)
            .open(self.path(name))
        {
            Ok(file) => file,
            Err(error) if owned_artifact.is_some() && error.kind() == io::ErrorKind::NotFound => {
                return Ok(());
            }
            Err(error) => return Err(error),
        };
        let (identity, bytes) = read_file(&file)?;
        if bytes != expected || owned_artifact.is_some_and(|owned| identity != owned) {
            return Err(io::Error::other(format!(
                "unexpected file or bytes at {name}"
            )));
        }
        mark_for_deletion(&file)
    }

    fn move_into_place(&self, source: &str, target: &str, replace: bool) -> io::Result<()> {
        move_file(&self.path(source), &self.path(target), replace)
    }

    fn commit(&self, intent: [&[u8]; 3], mut at: impl FnMut(Boundary) -> io::Result<()>) -> Report {
        let _lock = match self.lock() {
            Ok(lock) => lock,
            Err(error) => return Report::new(Outcome::Unchanged(error)),
        };
        match self.record_present() {
            Ok(true) => {
                return Report::new(Outcome::Refused(Inspection::Preserve(
                    crate::configuration::Reason::RecoveryRequired,
                )));
            }
            Ok(false) => {}
            Err(error) => return Report::new(Outcome::Unchanged(error)),
        }
        if intent.iter().any(|bytes| bytes.len() as u64 > MAX_DOCUMENT) {
            return Report::new(Outcome::Unchanged(io::Error::other(
                "configuration intent exceeds size limit",
            )));
        }
        let inspection = inspect_bytes(intent.map(Some));
        if inspection
            != (Inspection::CommittedEnvelope {
                schema: CURRENT_SCHEMA,
            })
        {
            return Report::new(Outcome::InvalidIntent(inspection));
        }

        let prior = match self.capture() {
            Ok(prior) => prior,
            Err(error) => return Report::new(Outcome::Unchanged(error)),
        };
        let prior = prior.each_ref().map(Option::as_deref);
        match inspect_bytes(prior) {
            Inspection::Empty
            | Inspection::CommittedEnvelope {
                schema: CURRENT_SCHEMA,
            } => {}
            other => return Report::new(Outcome::Refused(other)),
        }
        let id = match transaction_id() {
            Ok(id) => id,
            Err(error) => return Report::new(Outcome::Unchanged(error)),
        };
        Transaction::new(self, id, prior, intent).run(&mut at)
    }
}

impl Drop for TestStore {
    fn drop(&mut self) {
        if !self.cleanup_on_drop {
            return;
        }
        drop(self.held_lock.take());
        let cleanup = (|| {
            // Tear down fixture entries while the root is still pinned.
            // Directory sentinels are empty and are removed without recursion.
            for entry in fs::read_dir(&self.root)? {
                let entry = entry?;
                if entry.metadata()?.file_attributes() & FILE_ATTRIBUTE_DIRECTORY != 0 {
                    fs::remove_dir(entry.path())?;
                } else {
                    fs::remove_file(entry.path())?;
                }
            }
            self.pins.pop();
            fs::remove_dir(&self.root)
        })();
        if let Err(error) = cleanup {
            if std::thread::panicking() {
                eprintln!("could not remove test configuration fixture: {error}");
            } else {
                panic!("could not remove test configuration fixture: {error}");
            }
        }
    }
}

#[derive(Clone, Copy, PartialEq, Eq)]
struct FileIdentity {
    volume: u32,
    index: u64,
}

struct Artifact<'a> {
    name: String,
    bytes: &'a [u8],
    identity: FileIdentity,
}

struct Target<'a> {
    name: &'static str,
    prior: Option<&'a [u8]>,
    intended: &'a [u8],
    stage: String,
    backup: String,
    restore: String,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct Record<'a> {
    schema_version: u32,
    transaction_id: &'a str,
    targets: [RecordTarget; 3],
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct RecordTarget {
    path: PathBuf,
    prior_exists: bool,
    backup_path: Option<PathBuf>,
    restore_path: Option<PathBuf>,
}

struct Transaction<'a> {
    store: &'a TestStore,
    id: String,
    targets: [Target<'a>; 3],
    artifacts: Vec<Artifact<'a>>,
    replaced: [bool; 3],
}

impl<'a> Transaction<'a> {
    fn new(
        store: &'a TestStore,
        id: String,
        prior: [Option<&'a [u8]>; 3],
        intent: [&'a [u8]; 3],
    ) -> Self {
        let targets = std::array::from_fn(|index| {
            let name = NAMES[index];
            let prefix = format!("{name}.transaction-{id}");
            Target {
                name,
                prior: prior[index],
                intended: intent[index],
                stage: format!("{prefix}.stage"),
                backup: format!("{prefix}.backup"),
                restore: format!("{prefix}.restore"),
            }
        });
        Self {
            store,
            id,
            targets,
            artifacts: Vec::new(),
            replaced: [false; 3],
        }
    }

    fn stage(&mut self, at: &mut impl FnMut(Boundary) -> io::Result<()>) -> io::Result<()> {
        for (index, target) in self.targets.iter().enumerate() {
            at(Boundary::BeforeStage(index))?;
            self.artifacts
                .push(self.store.create_artifact(&target.stage, target.intended)?);
            if let Some(prior) = target.prior {
                self.artifacts
                    .push(self.store.create_artifact(&target.backup, prior)?);
            }
        }
        Ok(())
    }

    fn record(&self) -> io::Result<Vec<u8>> {
        let document = Record {
            schema_version: 1,
            transaction_id: &self.id,
            targets: self.targets.each_ref().map(|target| RecordTarget {
                path: self.store.path(target.name),
                prior_exists: target.prior.is_some(),
                backup_path: target.prior.map(|_| self.store.path(&target.backup)),
                restore_path: target.prior.map(|_| self.store.path(&target.restore)),
            }),
        };
        serde_json::to_vec(&document).map_err(io::Error::other)
    }

    fn run(mut self, at: &mut impl FnMut(Boundary) -> io::Result<()>) -> Report {
        let prepare = self
            .stage(at)
            .and_then(|()| at(Boundary::BeforeRecord))
            .and_then(|()| self.verify_primaries())
            .and_then(|()| self.record());
        let record = match prepare {
            Ok(record) => record,
            Err(error) => return self.cleanup(Outcome::Unchanged(error), at),
        };
        // The handle disallows deleting, swapping or writing the record while
        // replacements and rollback are in progress. No write precedes its flush.
        let record_handle = match self.store.create_durable(RECOVERY_REQUIRED, &record) {
            Ok(handle) => handle,
            Err(cause) => match self.store.record_present() {
                Ok(false) => return self.cleanup(Outcome::Unchanged(cause), at),
                Ok(true) => {
                    return Report::new(Outcome::RecoveryRequired {
                        cause,
                        resolution: None,
                    });
                }
                Err(error) => {
                    return Report::new(Outcome::RecoveryRequired {
                        cause,
                        resolution: Some(error),
                    });
                }
            },
        };
        let outcome = match self.replace(at) {
            Ok(()) => Outcome::Committed,
            Err(cause) => match self.restore(at) {
                Ok(()) => Outcome::Restored(cause),
                Err(error) => {
                    return Report::new(Outcome::RecoveryRequired {
                        cause,
                        resolution: Some(error),
                    });
                }
            },
        };
        let cleared = at(Boundary::BeforeClear)
            .and_then(|()| self.verify_primaries())
            .and_then(|()| mark_for_deletion(&record_handle));
        drop(record_handle);
        let cleared = cleared.and_then(|()| {
            if self.store.record_present()? {
                return Err(io::Error::other(
                    "recovery record is still present after clear",
                ));
            }
            Ok(())
        });
        if let Err(cause) = cleared {
            return Report::new(Outcome::RecoveryRequired {
                cause,
                resolution: match outcome {
                    Outcome::Restored(error) => Some(error),
                    _ => None,
                },
            });
        }
        self.cleanup(outcome, at)
    }

    fn verify_primaries(&self) -> io::Result<()> {
        for (index, target) in self.targets.iter().enumerate() {
            let expected = if self.replaced[index] {
                Some(target.intended)
            } else {
                target.prior
            };
            self.store.expect(target.name, expected)?;
        }
        Ok(())
    }

    fn replace(&mut self, at: &mut impl FnMut(Boundary) -> io::Result<()>) -> io::Result<()> {
        at(Boundary::RecordFlushed)?;
        for (index, target) in self.targets.iter().enumerate() {
            at(Boundary::BeforeReplace(index))?;
            self.verify_primaries()?;
            self.store.expect(&target.stage, Some(target.intended))?;
            self.store.expect(&target.backup, target.prior)?;
            self.store
                .move_into_place(&target.stage, target.name, target.prior.is_some())?;
            self.replaced[index] = true;
            self.store.expect(target.name, Some(target.intended))?;
            at(Boundary::AfterReplace(index))?;
        }
        self.verify_primaries()
    }

    fn restore(&mut self, at: &mut impl FnMut(Boundary) -> io::Result<()>) -> io::Result<()> {
        // An unexpected target could be an outside edit or an ambiguous rename.
        // Preserve the record and evidence in either case. Check all required
        // backups before restoring any primary.
        self.verify_primaries()?;
        for (target, replaced) in self.targets.iter().zip(self.replaced) {
            if replaced && let Some(prior) = target.prior {
                self.store.expect(&target.backup, Some(prior))?;
            }
        }
        for (index, target) in self.targets.iter().enumerate() {
            if !self.replaced[index] {
                continue;
            }
            at(Boundary::BeforeRestore(index))?;
            self.verify_primaries()?;
            if let Some(prior) = target.prior {
                self.store.expect(&target.backup, Some(prior))?;
                self.artifacts
                    .push(self.store.create_artifact(&target.restore, prior)?);
                self.store.expect(&target.restore, Some(prior))?;
                self.store
                    .move_into_place(&target.restore, target.name, true)?;
            } else {
                self.store
                    .delete_exact(target.name, target.intended, None)?;
            }
            self.replaced[index] = false;
            self.store.expect(target.name, target.prior)?;
        }
        self.verify_primaries()
    }

    fn cleanup(self, outcome: Outcome, at: &mut impl FnMut(Boundary) -> io::Result<()>) -> Report {
        let mut report = Report::new(outcome);
        if let Err(error) = at(Boundary::BeforeArtifactCleanup) {
            report.artifact_errors.push(error);
            return report;
        }
        for artifact in self.artifacts {
            let cleanup =
                self.store
                    .delete_exact(&artifact.name, artifact.bytes, Some(artifact.identity));
            if let Err(error) = cleanup {
                report.artifact_errors.push(error);
            }
        }
        report
    }
}

fn inspect_bytes(bytes: [Option<&[u8]>; 3]) -> Inspection {
    let [profile, preferences, commit] =
        bytes.map(|value| value.map_or(ObservedFile::Missing, ObservedFile::Bytes));
    inspect(Snapshot {
        recovery: RecoveryRecord::Missing,
        profile,
        preferences,
        commit,
    })
}

// Open a single known child relative to a retained directory. The fixture
// creates its private child; protected admission only opens existing entries.
fn open_relative(
    parent: &File,
    name: &OsStr,
    access: u32,
    share: u32,
    disposition: u32,
    options: u32,
) -> io::Result<File> {
    use std::{os::windows::io::FromRawHandle, ptr};
    use windows_sys::{
        Wdk::{
            Foundation::OBJECT_ATTRIBUTES,
            Storage::FileSystem::{
                FILE_DIRECTORY_FILE, FILE_SYNCHRONOUS_IO_NONALERT, NtCreateFile,
            },
        },
        Win32::{
            Foundation::{
                OBJ_CASE_INSENSITIVE, OBJ_DONT_REPARSE, RtlNtStatusToDosError, UNICODE_STRING,
            },
            System::IO::IO_STATUS_BLOCK,
        },
    };

    let mut wide: Vec<u16> = name.encode_wide().collect();
    let length = u16::try_from(wide.len() * size_of::<u16>()).map_err(io::Error::other)?;
    let mut name = UNICODE_STRING {
        Length: length,
        MaximumLength: length,
        Buffer: wide.as_mut_ptr(),
    };
    let attributes = OBJECT_ATTRIBUTES {
        Length: size_of::<OBJECT_ATTRIBUTES>() as u32,
        RootDirectory: parent.as_raw_handle(),
        ObjectName: &mut name,
        Attributes: OBJ_CASE_INSENSITIVE | OBJ_DONT_REPARSE,
        ..Default::default()
    };
    let mut handle = ptr::null_mut();
    let mut io_status = IO_STATUS_BLOCK::default();
    let file_attributes = if options & FILE_DIRECTORY_FILE != 0 {
        FILE_ATTRIBUTE_DIRECTORY
    } else {
        0
    };
    // SAFETY: The retained parent owns its handle, and all buffers stay live
    // for this synchronous call. The only callers supply a single child name.
    let status = unsafe {
        NtCreateFile(
            &mut handle,
            access,
            &attributes,
            &mut io_status,
            ptr::null(),
            file_attributes,
            share,
            disposition,
            options | FILE_SYNCHRONOUS_IO_NONALERT,
            ptr::null(),
            0,
        )
    };
    if status < 0 {
        // SAFETY: The returned NTSTATUS is the sole input.
        return Err(io::Error::from_raw_os_error(
            unsafe { RtlNtStatusToDosError(status) } as i32,
        ));
    }
    if handle.is_null() || handle == windows_sys::Win32::Foundation::INVALID_HANDLE_VALUE {
        return Err(io::Error::other("relative open returned no handle"));
    }
    // SAFETY: A successful synchronous open transferred ownership of the handle.
    Ok(unsafe { File::from_raw_handle(handle) })
}

fn pin_directory(path: &Path) -> io::Result<File> {
    pin_directory_with_share(path, FILE_SHARE_READ | FILE_SHARE_WRITE)
}

fn pin_directory_with_share(path: &Path, share: u32) -> io::Result<File> {
    let file = OpenOptions::new()
        .read(true)
        .share_mode(share)
        .custom_flags(FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT)
        .open(path)?;
    let metadata = file.metadata()?;
    if !metadata.is_dir() || metadata.file_attributes() & FILE_ATTRIBUTE_REPARSE_POINT != 0 {
        return Err(io::Error::other(
            "test root component is not a plain directory",
        ));
    }
    Ok(file)
}

fn read_file(file: &File) -> io::Result<(FileIdentity, Vec<u8>)> {
    let identity = check_regular_file(file)?;
    let mut bytes = Vec::new();
    file.take(MAX_DOCUMENT + 1).read_to_end(&mut bytes)?;
    if bytes.len() as u64 > MAX_DOCUMENT {
        return Err(io::Error::other(
            "configuration test file exceeds size limit",
        ));
    }
    Ok((identity, bytes))
}

fn mark_for_deletion(file: &File) -> io::Result<()> {
    let disposition = FILE_DISPOSITION_INFO { DeleteFile: true };
    // SAFETY: The file owns a live handle opened for DELETE and disposition
    // points to the structure required by FileDispositionInfo.
    if unsafe {
        SetFileInformationByHandle(
            file.as_raw_handle(),
            FileDispositionInfo,
            (&raw const disposition).cast(),
            size_of::<FILE_DISPOSITION_INFO>() as u32,
        )
    } == 0
    {
        return Err(io::Error::last_os_error());
    }
    Ok(())
}

fn check_regular_file(file: &File) -> io::Result<FileIdentity> {
    let metadata = file.metadata()?;
    if !metadata.is_file() || metadata.file_attributes() & FILE_ATTRIBUTE_REPARSE_POINT != 0 {
        return Err(io::Error::other("configuration entry is not a plain file"));
    }
    let info = handle_information(file)?;
    if info.nNumberOfLinks != 1 {
        return Err(io::Error::other("configuration entry has multiple links"));
    }
    Ok(file_identity(&info))
}

fn handle_information(file: &File) -> io::Result<BY_HANDLE_FILE_INFORMATION> {
    let mut info = BY_HANDLE_FILE_INFORMATION::default();
    // SAFETY: The file owns the live handle and info is writable for the call.
    if unsafe { GetFileInformationByHandle(file.as_raw_handle(), &mut info) } == 0 {
        return Err(io::Error::last_os_error());
    }
    Ok(info)
}

fn file_identity(info: &BY_HANDLE_FILE_INFORMATION) -> FileIdentity {
    FileIdentity {
        volume: info.dwVolumeSerialNumber,
        index: (u64::from(info.nFileIndexHigh) << 32) | u64::from(info.nFileIndexLow),
    }
}

fn move_file(source: &Path, target: &Path, replace: bool) -> io::Result<()> {
    fn wide(path: &Path) -> Vec<u16> {
        OsStr::new(r"\\?\")
            .encode_wide()
            .chain(path.as_os_str().encode_wide())
            .chain([0])
            .collect()
    }
    let source = wide(source);
    let target = wide(target);
    let flags = MOVEFILE_WRITE_THROUGH
        | if replace {
            MOVEFILE_REPLACE_EXISTING
        } else {
            0
        };
    // SAFETY: Both pointers are live, NUL-terminated local paths. Copy fallback
    // is disabled, and both names are direct children of the same pinned root.
    if unsafe { MoveFileExW(source.as_ptr(), target.as_ptr(), flags) } == 0 {
        return Err(io::Error::last_os_error());
    }
    Ok(())
}

fn transaction_id() -> io::Result<String> {
    let mut guid = windows_sys::core::GUID::default();
    // SAFETY: guid points to initialized, writable storage for a GUID.
    if unsafe { CoCreateGuid(&mut guid) } < 0 {
        return Err(io::Error::other(
            "could not create configuration transaction ID",
        ));
    }
    let tail = u64::from_be_bytes(guid.data4);
    Ok(format!(
        "{:08x}{:04x}{:04x}{tail:016x}",
        guid.data1, guid.data2, guid.data3
    ))
}

#[cfg(test)]
mod tests;

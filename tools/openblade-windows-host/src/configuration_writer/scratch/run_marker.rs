//! Service run markers for the enrolled, opt-in Windows scratch writer.
use super::{Lease, acquire};
use crate::configuration_writer::{
    check_regular_file, mark_for_deletion, open_relative, protected::rename_same_directory,
    read_file, transaction_id,
};
use std::{
    ffi::OsStr,
    fs::File,
    io::{self, Write},
    path::{Path, PathBuf},
};
use windows_sys::{
    Wdk::Storage::FileSystem::{
        FILE_CREATE, FILE_NON_DIRECTORY_FILE, FILE_OPEN, FILE_OPEN_REPARSE_POINT,
        FILE_WRITE_THROUGH,
    },
    Win32::{
        Foundation::ERROR_FILE_NOT_FOUND,
        Storage::FileSystem::{
            DELETE, FILE_GENERIC_READ, FILE_GENERIC_WRITE, FILE_READ_ATTRIBUTES, FILE_SHARE_READ,
            SYNCHRONIZE,
        },
    },
};

mod format;
use format::Clock;

const MARKER: &str = "service-running.json";
const TEMP: &str = "service-running.json.tmp";

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum StartupKind {
    Normal,
    UnexpectedExitRecovery,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum Boundary {
    BeforeStage,
    Staged,
    Published,
    BeforeClear,
}

/// Holds cleanup authority only after this instance publishes and confirms a marker.
/// Each operation reacquires the exclusive lease for the enrolled root and lock.
pub struct RunMarkerStore {
    path: PathBuf,
    root_id: String,
    lock_id: String,
    owned: Option<Vec<u8>>,
}

impl RunMarkerStore {
    pub fn new(path: &Path, root_id: &str, lock_id: &str) -> Self {
        Self {
            path: path.to_owned(),
            root_id: root_id.to_owned(),
            lock_id: lock_id.to_owned(),
            owned: None,
        }
    }

    pub fn begin_run(&mut self) -> io::Result<StartupKind> {
        self.begin(Clock::capture, None::<fn() -> io::Result<()>>, |_| Ok(()))
    }

    /// The verifier must independently confirm durable retirement of the exact
    /// outgoing backend generation. It runs under the writer lease before any write.
    pub fn begin_handoff(
        &mut self,
        require_retired_backend: impl FnOnce() -> io::Result<()>,
    ) -> io::Result<StartupKind> {
        self.begin(Clock::capture, Some(require_retired_backend), |_| Ok(()))
    }

    /// Clear the owned marker after a confirmed intentional stop and cleanup.
    pub fn complete_run(&mut self) -> io::Result<()> {
        self.complete(|_| Ok(()))
    }

    fn lease(&self) -> io::Result<Lease> {
        acquire(&self.path, &self.root_id, &self.lock_id)
    }

    fn begin(
        &mut self,
        clock: impl FnOnce() -> io::Result<Clock>,
        require_retired_backend: Option<impl FnOnce() -> io::Result<()>>,
        mut at: impl FnMut(Boundary) -> io::Result<()>,
    ) -> io::Result<StartupKind> {
        let lease = self.lease()?;
        if self.owned.is_some() {
            return Err(io::Error::other(
                "this store already owns a service run marker",
            ));
        }
        require_absent(&lease, TEMP)?;
        let clock = clock()?;
        let (kind, replace) = if let Some(require_retired_backend) = require_retired_backend {
            require_retired_backend()?;
            require_absent(&lease, MARKER)?;
            (StartupKind::Normal, false)
        } else if let Some(previous) = open_marker(&lease)? {
            let prior = read_file(&previous);
            (
                prior.map_or(StartupKind::UnexpectedExitRecovery, |(_, bytes)| {
                    format::classify(clock, &bytes)
                }),
                true,
            )
        } else {
            (StartupKind::Normal, false)
        };

        let id = transaction_id()?;
        let run_id = format!(
            "{}-{}-{}-{}-{}",
            &id[..8],
            &id[8..12],
            &id[12..16],
            &id[16..20],
            &id[20..]
        );
        let bytes = format::encode(clock, &run_id)?;

        at(Boundary::BeforeStage)?;
        let mut stage = open_relative(
            &lease._root,
            OsStr::new(TEMP),
            FILE_GENERIC_READ | FILE_GENERIC_WRITE | DELETE,
            0,
            FILE_CREATE,
            FILE_NON_DIRECTORY_FILE | FILE_OPEN_REPARSE_POINT | FILE_WRITE_THROUGH,
        )
        .map_err(|error| io::Error::other(format!("creating service run marker temp: {error}")))?;
        let identity = check_regular_file(&stage).map_err(|error| {
            io::Error::other(format!("verifying service run marker temp: {error}"))
        })?;
        // Keep the exclusive staging handle through the rename. Errors preserve
        // whichever name exists at the failing boundary for the next admission.
        stage
            .write_all(&bytes)
            .and_then(|()| stage.sync_all())
            .map_err(|error| {
                io::Error::other(format!("flushing service run marker temp: {error}"))
            })?;
        at(Boundary::Staged)?;
        rename_same_directory(&stage, MARKER, replace)
            .map_err(|error| io::Error::other(format!("publishing service run marker: {error}")))?;
        stage.sync_all().map_err(|error| {
            io::Error::other(format!("flushing published service run marker: {error}"))
        })?;
        drop(stage);
        at(Boundary::Published)?;

        let committed = open_marker(&lease)?
            .ok_or_else(|| io::Error::other("published service run marker is missing"))?;
        let (actual_identity, actual_bytes) = read_file(&committed)?;
        if actual_identity != identity || actual_bytes != bytes {
            return Err(io::Error::other("published service run marker changed"));
        }
        drop(committed);
        drop(lease);
        self.owned = Some(bytes);
        Ok(kind)
    }

    fn complete(&mut self, at: impl FnOnce(Boundary) -> io::Result<()>) -> io::Result<()> {
        let lease = self.lease()?;
        let owned = self
            .owned
            .as_deref()
            .ok_or_else(|| io::Error::other("this store has no confirmed service run marker"))?;
        require_absent(&lease, TEMP)?;
        // Exclude even readers that share DELETE. Otherwise NTFS can keep
        // the name delete-pending after we close our handle.
        let file = open_optional(
            &lease,
            MARKER,
            FILE_GENERIC_READ | DELETE,
            0,
            FILE_NON_DIRECTORY_FILE,
        )?
        .ok_or_else(|| io::Error::other("owned service run marker is missing"))?;
        if read_file(&file)?.1 != owned {
            return Err(io::Error::other(
                "service run marker belongs to a different run",
            ));
        }
        at(Boundary::BeforeClear)?;
        mark_for_deletion(&file)?;
        drop(file);
        require_absent(&lease, MARKER)?;
        drop(lease);
        self.owned = None;
        Ok(())
    }
}

fn open_marker(lease: &Lease) -> io::Result<Option<File>> {
    let file = open_optional(
        lease,
        MARKER,
        FILE_GENERIC_READ,
        FILE_SHARE_READ,
        FILE_NON_DIRECTORY_FILE,
    )?;
    if let Some(file) = &file {
        check_regular_file(file)?;
    }
    Ok(file)
}

fn require_absent(lease: &Lease, name: &str) -> io::Result<()> {
    let file = open_optional(
        lease,
        name,
        FILE_READ_ATTRIBUTES | SYNCHRONIZE,
        FILE_SHARE_READ,
        0,
    )?;
    if file.is_some() {
        return Err(io::Error::other("uncleared service run-marker evidence"));
    }
    Ok(())
}

fn open_optional(
    lease: &Lease,
    name: &str,
    access: u32,
    share: u32,
    options: u32,
) -> io::Result<Option<File>> {
    match open_relative(
        &lease._root,
        OsStr::new(name),
        access,
        share,
        FILE_OPEN,
        options | FILE_OPEN_REPARSE_POINT,
    ) {
        Ok(file) => Ok(Some(file)),
        Err(error) if error.raw_os_error() == Some(ERROR_FILE_NOT_FOUND as i32) => Ok(None),
        Err(error) => Err(error),
    }
}

#[cfg(test)]
mod tests;

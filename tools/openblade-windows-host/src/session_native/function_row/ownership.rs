//! Startup ownership guard (managed `WindowsFunctionRowOwnershipGuard.InspectStartup`
//! over `DriverOwnershipJournalReader.AllowsWindowsBackend(FunctionRow)`).
//!
//! The Razer-driver helper journals its function-row subscription in
//! `function-row-driver-ownership.json`, or in `snaptap-driver-ownership.json`
//! for a combined or legacy session, and deletes the journal after confirmed
//! cleanup. A journal that is still present may own the function row.
//!
//! Managed parses each journal and still admits the Windows hook when the
//! journal excludes FunctionRow or comes from a strictly earlier Windows boot.
//! This guard is stricter: any journal that exists, or whose existence cannot
//! be observed, refuses installation. It never reads, changes or deletes a
//! journal; only the driver supervisor reconciles them.
use std::{io, path::PathBuf};

const JOURNALS: [&str; 2] = [
    "function-row-driver-ownership.json",
    "snaptap-driver-ownership.json",
];

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum Journal {
    Clear,
    Present,
    Unobservable,
}

pub(crate) trait Guard {
    fn inspect(&mut self) -> Journal;
}

pub(crate) struct JournalGuard(PathBuf);

impl JournalGuard {
    pub(crate) fn new(directory: PathBuf) -> Self {
        Self(directory)
    }
}

impl Guard for JournalGuard {
    fn inspect(&mut self) -> Journal {
        for name in JOURNALS {
            match std::fs::symlink_metadata(self.0.join(name)) {
                // A missing directory is also no journal, as in managed.
                Err(error) if error.kind() == io::ErrorKind::NotFound => {}
                Err(_) => return Journal::Unobservable,
                Ok(_) => return Journal::Present,
            }
        }
        Journal::Clear
    }
}

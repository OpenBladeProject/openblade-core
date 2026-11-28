//! Read-only process evidence for the opt-in Windows controller adapter.
#![allow(unsafe_code)]

use std::{
    collections::HashSet,
    ffi::OsString,
    os::windows::{ffi::OsStringExt, io::AsRawHandle},
    path::{Path, PathBuf},
};

use windows_sys::Win32::{
    Foundation::{FILETIME, WAIT_TIMEOUT},
    System::{
        Diagnostics::ToolHelp::PROCESSENTRY32W,
        SystemInformation::GetSystemTimeAsFileTime,
        Threading::{GetProcessId, PROCESS_SYNCHRONIZE, WaitForSingleObject},
    },
};

use super::{ENGINE, HELPER, ParentSnapshot, Process, ProcessIdentity, Scan, eq};
use crate::{
    process_query::{
        filetime, image_path, open_process, open_process_token, process_times, token_session,
    },
    process_snapshot::visit_processes,
};

/// A pathname and identity queried on one live process handle. Image contents
/// and publisher remain unverified.
pub struct ProcessImageEvidence {
    pub identity: ProcessIdentity,
    pub path: PathBuf,
}

struct Row {
    pid: u32,
    name: String,
    image: Option<ProcessImageEvidence>,
}

pub struct ProcessInventory {
    started: u64,
    complete: bool,
    rows: Vec<Row>,
    parents: Vec<(u32, u32)>,
}

impl ProcessInventory {
    pub fn process_rows(&self) -> Vec<Process<'_>> {
        self.rows
            .iter()
            .map(|row| Process {
                name: &row.name,
                pid: Some(row.pid),
                identity: row.image.as_ref().map(|image| image.identity),
                verified_image: None,
            })
            .collect()
    }

    pub fn image_evidence(&self) -> impl Iterator<Item = &ProcessImageEvidence> {
        self.rows.iter().filter_map(|row| row.image.as_ref())
    }

    pub fn is_complete(&self) -> bool {
        self.complete
    }

    pub fn parent_snapshot(&self) -> ParentSnapshot<'_> {
        ParentSnapshot {
            started: self.started,
            links: if self.complete {
                Scan::Complete(&self.parents)
            } else {
                Scan::Incomplete(&self.parents)
            },
        }
    }
}

pub fn read() -> ProcessInventory {
    collect(now(), |visit| visit_processes(visit), query_process)
}

pub(super) fn now() -> u64 {
    let mut time = FILETIME::default();
    // SAFETY: The FILETIME is writable.
    unsafe { GetSystemTimeAsFileTime(&mut time) };
    filetime(time)
}

fn collect(
    started: u64,
    snapshot: impl FnOnce(&mut dyn FnMut(&PROCESSENTRY32W) -> bool) -> bool,
    mut query: impl FnMut(u32) -> Option<ProcessImageEvidence>,
) -> ProcessInventory {
    let mut inventory = ProcessInventory {
        started,
        complete: started != 0,
        rows: Vec::new(),
        parents: Vec::new(),
    };
    let mut pids = HashSet::new();
    let exhausted = snapshot(&mut |entry| {
        let pid = entry.th32ProcessID;
        if pid == 0 {
            return true;
        }
        if !pids.insert(pid) {
            inventory.complete = false;
        }
        if entry.th32ParentProcessID != 0 {
            inventory.parents.push((pid, entry.th32ParentProcessID));
        }
        if let Some(name) = process_name(&entry.szExeFile) {
            inventory.rows.push(Row {
                pid,
                name,
                image: None,
            });
        } else {
            inventory.complete = false;
        }
        true
    });
    inventory.complete &= exhausted;
    if !inventory.complete {
        return inventory;
    }

    for row in &mut inventory.rows {
        if !(eq(&row.name, ENGINE) || eq(&row.name, HELPER)) {
            continue;
        }
        row.image = query(row.pid).filter(|image| {
            image.identity.pid == row.pid
                && (1..started).contains(&image.identity.created)
                && matches_executable(&image.path, &row.name)
        });
        inventory.complete &= row.image.is_some();
    }
    inventory
}

fn query_process(pid: u32) -> Option<ProcessImageEvidence> {
    let process = open_process(pid, PROCESS_SYNCHRONIZE).ok()?;
    let (created, _) = process_times(&process)?;
    let path = image_path(&process)?;
    let token = open_process_token(process.as_raw_handle())?;
    let session = token_session(&token)?;
    // SAFETY: The process is still held, with synchronize access. A zero
    // timeout checks for exit without blocking, including exit code 259.
    let (pid, wait) = unsafe {
        (
            GetProcessId(process.as_raw_handle()),
            WaitForSingleObject(process.as_raw_handle(), 0),
        )
    };
    if pid == 0 || wait != WAIT_TIMEOUT || path.contains(&0) {
        return None;
    }
    Some(ProcessImageEvidence {
        identity: ProcessIdentity {
            pid,
            session,
            created,
        },
        path: OsString::from_wide(&path).into(),
    })
}

fn matches_executable(path: &Path, name: &str) -> bool {
    path.is_absolute()
        && path
            .file_name()
            .and_then(|file| file.to_str())
            .is_some_and(|file| {
                file.rsplit_once('.')
                    .is_some_and(|(stem, extension)| eq(stem, name) && eq(extension, "exe"))
            })
}

fn process_name(image: &[u16]) -> Option<String> {
    let end = image.iter().position(|&unit| unit == 0)?;
    let file = String::from_utf16(&image[..end]).ok()?;
    let name = match file.rsplit_once('.') {
        Some((stem, extension)) if eq(extension, "exe") => stem,
        _ => file.as_str(),
    };
    (!name.is_empty()).then(|| name.to_owned())
}

#[cfg(test)]
mod tests;

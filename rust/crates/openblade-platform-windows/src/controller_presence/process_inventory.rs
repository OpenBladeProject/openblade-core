//! Inactive compile-check adapter. It supplies names and raw parent links only.
#![allow(unsafe_code)]

use windows_sys::Win32::{
    Foundation::FILETIME, System::SystemInformation::GetSystemTimeAsFileTime,
};

use super::{ParentSnapshot, Process, Scan};
use crate::process_snapshot::visit_processes;

pub struct ProcessInventory {
    started: u64,
    complete: bool,
    names: Vec<(u32, String)>,
    parents: Vec<(u32, u32)>,
}

impl ProcessInventory {
    pub fn process_rows(&self) -> Vec<Process<'_>> {
        self.names
            .iter()
            .map(|(pid, name)| Process {
                name,
                pid: Some(*pid),
                identity: None,
                verified_image: None,
            })
            .collect()
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
    let mut time = FILETIME::default();
    // SAFETY: The FILETIME is writable and captures a lower bound on snapshot time.
    unsafe { GetSystemTimeAsFileTime(&mut time) };
    let mut inventory = ProcessInventory {
        started: (u64::from(time.dwHighDateTime) << 32) | u64::from(time.dwLowDateTime),
        complete: true,
        names: Vec::new(),
        parents: Vec::new(),
    };
    let exhausted = visit_processes(|entry| {
        if entry.th32ProcessID == 0 {
            return true;
        }
        if entry.th32ParentProcessID != 0 {
            inventory
                .parents
                .push((entry.th32ProcessID, entry.th32ParentProcessID));
        }
        if let Some(name) = process_name(&entry.szExeFile) {
            inventory.names.push((entry.th32ProcessID, name));
        } else {
            inventory.complete = false;
        }
        true
    });
    inventory.complete &= exhausted;
    inventory
}

fn process_name(image: &[u16]) -> Option<String> {
    let end = image.iter().position(|&unit| unit == 0)?;
    let file = String::from_utf16(&image[..end]).ok()?;
    let name = match file.rsplit_once('.') {
        Some((stem, extension)) if extension.eq_ignore_ascii_case("exe") => stem,
        _ => file.as_str(),
    };
    (!name.is_empty()).then(|| name.to_owned())
}

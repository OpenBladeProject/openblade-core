//! Bounded read-only SCM alias and status queries for the opt-in adapter.
#![allow(unsafe_code)]

use std::ptr::null;

use windows_sys::Win32::{
    Foundation::{ERROR_SERVICE_DOES_NOT_EXIST, GetLastError},
    System::Services::{
        CloseServiceHandle, GetServiceDisplayNameW, GetServiceKeyNameW, OpenSCManagerW,
        OpenServiceW, QueryServiceStatusEx, SC_HANDLE, SC_MANAGER_CONNECT, SC_STATUS_PROCESS_INFO,
        SERVICE_CONTINUE_PENDING, SERVICE_DRIVER, SERVICE_QUERY_STATUS, SERVICE_RUNNING,
        SERVICE_START_PENDING, SERVICE_STATUS_PROCESS, SERVICE_STOPPED, SERVICE_WIN32,
    },
};

use super::{
    CHROMA_SERVICES, SYNAPSE_SERVICES, Scan, Service, ServiceState, UPDATER, UPDATER_DISPLAY, eq,
};

struct ScHandle(SC_HANDLE);

impl Drop for ScHandle {
    fn drop(&mut self) {
        // SAFETY: Only a successfully opened, uniquely owned SCM handle is wrapped.
        unsafe { CloseServiceHandle(self.0) };
    }
}

#[derive(Clone, Copy)]
enum Alias {
    Key(&'static str),
    Display(&'static str),
}

#[derive(PartialEq, Eq)]
struct Names {
    key: String,
    display: String,
}

struct Row {
    names: Names,
    state: ServiceState,
}

pub struct ServiceInventory {
    complete: bool,
    rows: Vec<Row>,
}

impl ServiceInventory {
    pub fn service_rows(&self) -> Vec<Service<'_>> {
        self.rows
            .iter()
            .map(|row| Service {
                name: &row.names.key,
                display_name: &row.names.display,
                state: row.state,
            })
            .collect()
    }

    pub fn is_complete(&self) -> bool {
        self.complete
    }

    pub(super) fn scan<'a>(&self, rows: &'a [Service<'a>]) -> Scan<'a, Service<'a>> {
        if self.complete {
            Scan::Complete(rows)
        } else {
            Scan::Incomplete(rows)
        }
    }
}

pub fn read() -> ServiceInventory {
    // SAFETY: Null names select the local active database. Only connect access
    // is requested. Service handles separately request query-only status access.
    let raw = unsafe { OpenSCManagerW(null(), null(), SC_MANAGER_CONNECT) };
    if raw.is_null() {
        return ServiceInventory {
            complete: false,
            rows: Vec::new(),
        };
    }
    let scm = ScHandle(raw);
    collect(|alias| resolve(&scm, alias), |key| query(&scm, key))
}

fn collect<H>(
    mut resolve: impl FnMut(Alias) -> Result<Option<Names>, ()>,
    mut query: impl FnMut(&str) -> (ServiceState, H),
) -> ServiceInventory {
    // Enumerating SCM rows can silently omit services whose DACL denies status
    // access. Look up each exact key and display alias separately so that a
    // found alias with unreadable status can never pass for an absent alias.
    let public_aliases = SYNAPSE_SERVICES
        .iter()
        .chain(CHROMA_SERVICES)
        .copied()
        .chain([UPDATER_DISPLAY]);
    let aliases = public_aliases
        .clone()
        .chain([UPDATER])
        .map(Alias::Key)
        .chain(public_aliases.map(Alias::Display));
    let mut inventory = ServiceInventory {
        complete: true,
        rows: Vec::new(),
    };
    let first: Vec<_> = aliases.clone().map(&mut resolve).collect();
    let mut handles = Vec::new();
    for names in first
        .iter()
        .filter_map(|result| result.as_ref().ok()?.as_ref())
    {
        if let Some(previous) = inventory
            .rows
            .iter()
            .find(|row| eq(&row.names.key, &names.key))
        {
            inventory.complete &= eq(&previous.names.display, &names.display);
            continue;
        }
        let (state, handle) = query(&names.key);
        // Keep the service object alive through the second alias pass. SCM
        // defers deletion and same-key replacement while its handle is held.
        handles.push(handle);
        inventory.complete &= !matches!(state, ServiceState::Unknown);
        inventory.rows.push(Row {
            names: Names {
                key: names.key.clone(),
                display: names.display.clone(),
            },
            state,
        });
    }
    for (alias, before) in aliases.zip(first) {
        // A changed mapping makes the scan incomplete, while already observed
        // positive rows remain available to the policy.
        let after = resolve(alias);
        inventory.complete &= before.is_ok() && before == after;
        if matches!(before, Ok(None)) {
            inventory.complete &= !inventory.rows.iter().any(|row| match alias {
                Alias::Key(key) => eq(key, &row.names.key),
                Alias::Display(display) => eq(display, &row.names.display),
            });
        }
    }
    inventory
}

fn resolve(scm: &ScHandle, alias: Alias) -> Result<Option<Names>, ()> {
    let (key, display) = match alias {
        Alias::Key(alias) => {
            let Some(display) = lookup(scm, alias, true)? else {
                return Ok(None);
            };
            let key = lookup(scm, &display, false)?
                .filter(|key| eq(key, alias))
                .ok_or(())?;
            (key, display)
        }
        Alias::Display(alias) => {
            let Some(key) = lookup(scm, alias, false)? else {
                return Ok(None);
            };
            let display = lookup(scm, &key, true)?
                .filter(|display| eq(display, alias))
                .ok_or(())?;
            (key, display)
        }
    };
    if key.contains(['/', '\\']) {
        return Err(());
    }
    Ok(Some(Names { key, display }))
}

fn lookup(scm: &ScHandle, name: &str, by_key: bool) -> Result<Option<String>, ()> {
    const MAX_NAME_UNITS: usize = 257;
    let input: Vec<u16> = name.encode_utf16().chain([0]).collect();
    let mut output = [u16::MAX; MAX_NAME_UNITS];
    let mut units = output.len() as u32;
    // SAFETY: SCM is held. Input is NUL terminated and output is writable.
    let found = unsafe {
        if by_key {
            GetServiceDisplayNameW(scm.0, input.as_ptr(), output.as_mut_ptr(), &mut units)
        } else {
            GetServiceKeyNameW(scm.0, input.as_ptr(), output.as_mut_ptr(), &mut units)
        }
    };
    if found == 0 {
        // SAFETY: Read the error immediately after the failed name lookup.
        return if unsafe { GetLastError() } == ERROR_SERVICE_DOES_NOT_EXIST {
            Ok(None)
        } else {
            Err(())
        };
    }
    let end = output.iter().position(|&unit| unit == 0).ok_or(())?;
    if end == 0 {
        return Err(());
    }
    String::from_utf16(&output[..end]).map(Some).map_err(|_| ())
}

fn query(scm: &ScHandle, key: &str) -> (ServiceState, Option<ScHandle>) {
    let key: Vec<u16> = key.encode_utf16().chain([0]).collect();
    // SAFETY: SCM is held and key is NUL terminated. This handle is read-only.
    let raw = unsafe { OpenServiceW(scm.0, key.as_ptr(), SERVICE_QUERY_STATUS) };
    if raw.is_null() {
        // Even ERROR_SERVICE_DOES_NOT_EXIST here means the resolved key changed.
        return (ServiceState::Unknown, None);
    }
    let service = ScHandle(raw);
    let mut status = SERVICE_STATUS_PROCESS::default();
    let mut needed = 0;
    // SAFETY: The status buffer is writable and correctly sized for the requested level.
    let found = unsafe {
        QueryServiceStatusEx(
            service.0,
            SC_STATUS_PROCESS_INFO,
            (&mut status as *mut SERVICE_STATUS_PROCESS).cast(),
            size_of::<SERVICE_STATUS_PROCESS>() as u32,
            &mut needed,
        )
    };
    let state = if found == 0 {
        ServiceState::Unknown
    } else {
        map_status(status.dwServiceType, status.dwCurrentState)
    };
    (state, Some(service))
}

fn map_status(kind: u32, state: u32) -> ServiceState {
    match (kind & SERVICE_WIN32 != 0, kind & SERVICE_DRIVER != 0) {
        (false, true) => return ServiceState::Inactive,
        (true, false) => {}
        _ => return ServiceState::Unknown,
    }
    match state {
        SERVICE_RUNNING => ServiceState::Running,
        SERVICE_START_PENDING => ServiceState::StartPending,
        SERVICE_CONTINUE_PENDING => ServiceState::ContinuePending,
        SERVICE_STOPPED => ServiceState::Inactive,
        // Paused and stopping services have not confirmed a stopped state.
        _ => ServiceState::Unknown,
    }
}

#[cfg(test)]
mod tests;

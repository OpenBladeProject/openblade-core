//! Owned acquisition for the development host. The private pipe still uses replay.
#![allow(unsafe_code)]

use std::{
    fs::{self, File, OpenOptions},
    io::{self, Read},
    os::windows::{
        fs::{MetadataExt, OpenOptionsExt},
        io::AsRawHandle,
    },
    path::{Path, PathBuf},
    process::{Child, Command, Stdio},
    sync::atomic::{AtomicBool, Ordering},
    thread,
    time::{Duration, Instant},
};

use windows_sys::{
    Win32::{
        Storage::FileSystem::{
            DELETE, FILE_ATTRIBUTE_REPARSE_POINT, FILE_DISPOSITION_INFO,
            FILE_FLAG_BACKUP_SEMANTICS, FILE_FLAG_OPEN_REPARSE_POINT, FILE_READ_ATTRIBUTES,
            FILE_SHARE_READ, FILE_SHARE_WRITE, FileDispositionInfo, SetFileInformationByHandle,
        },
        System::Com::CoCreateGuid,
    },
    core::GUID,
};

use super::{
    report::{self, Report},
    xml::MAX_BYTES,
};

const REPORT_FILE: &str = "battery-report.xml";
const MAX_TIMEOUT: Duration = Duration::from_secs(15);
const POLL: Duration = Duration::from_millis(10);
const STOP_TIMEOUT: Duration = Duration::from_secs(2);

#[derive(Debug, Eq, PartialEq)]
pub(super) enum Failure {
    Scratch,
    Start,
    Wait,
    Cancelled,
    Timeout,
    StopUnconfirmed,
    ProcessExit,
    Output,
    InvalidReport,
}

/// Runs on a blocking owner thread. The caller supplies a trusted local scratch
/// root and a trusted leaf child (no descendants). Only the inert test child
/// uses this seam today. The caller signals cancellation and joins the owner.
pub(super) fn acquire(
    root: &Path,
    command: impl FnOnce(&Path) -> Command,
    cancelled: &AtomicBool,
    timeout: Duration,
) -> Result<Report, Failure> {
    let deadline = Instant::now() + timeout.min(MAX_TIMEOUT);
    check(cancelled, deadline)?;
    let mut attempt = Attempt::create(root).map_err(|_| Failure::Scratch)?;
    let output = attempt.path.join(REPORT_FILE);
    let mut command = command(&output);
    // Neither stream is used by the typed report. Discard both without buffering
    // or creating pipes that could block an otherwise successful child.
    command
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null());
    check(cancelled, deadline)?;
    let mut child = command.spawn().map_err(|_| Failure::Start)?;
    attempt.may_clean = false;
    let result = wait(&mut child, cancelled, deadline);
    if matches!(
        result,
        Err(Failure::Cancelled | Failure::Timeout | Failure::Wait)
    ) && !stop(&mut child)
    {
        return Err(Failure::StopUnconfirmed);
    }
    attempt.may_clean = true;
    check(cancelled, deadline)?;
    result?;
    let bytes = read_output(&output, cancelled, deadline);
    check(cancelled, deadline)?;
    let parsed = report::parse(&bytes?).ok_or(Failure::InvalidReport);
    drop(attempt);
    check(cancelled, deadline)?;
    parsed
}

fn check(cancelled: &AtomicBool, deadline: Instant) -> Result<(), Failure> {
    if cancelled.load(Ordering::Acquire) {
        Err(Failure::Cancelled)
    } else if Instant::now() >= deadline {
        Err(Failure::Timeout)
    } else {
        Ok(())
    }
}

fn wait(child: &mut Child, cancelled: &AtomicBool, deadline: Instant) -> Result<(), Failure> {
    loop {
        check(cancelled, deadline)?;
        if let Some(status) = child.try_wait().map_err(|_| Failure::Wait)? {
            return if status.success() {
                Ok(())
            } else {
                Err(Failure::ProcessExit)
            };
        }
        thread::sleep(POLL.min(deadline.saturating_duration_since(Instant::now())));
    }
}

fn stop(child: &mut Child) -> bool {
    let _ = child.kill();
    let deadline = Instant::now() + STOP_TIMEOUT;
    while Instant::now() < deadline {
        if matches!(child.try_wait(), Ok(Some(_))) {
            return true;
        }
        thread::sleep(POLL);
    }
    false
}

fn read_output(path: &Path, cancelled: &AtomicBool, deadline: Instant) -> Result<Vec<u8>, Failure> {
    let mut file = OpenOptions::new()
        .read(true)
        .share_mode(FILE_SHARE_READ)
        .custom_flags(FILE_FLAG_OPEN_REPARSE_POINT)
        .open(path)
        .map_err(|_| Failure::Output)?;
    let metadata = file.metadata().map_err(|_| Failure::Output)?;
    if !metadata.is_file()
        || metadata.file_attributes() & FILE_ATTRIBUTE_REPARSE_POINT != 0
        || !(1..=MAX_BYTES as u64).contains(&metadata.len())
    {
        return Err(Failure::Output);
    }
    let mut bytes = vec![0; metadata.len() as usize];
    for chunk in bytes.chunks_mut(16 * 1024) {
        check(cancelled, deadline)?;
        file.read_exact(chunk).map_err(|_| Failure::Output)?;
    }
    if file.read(&mut [0]).map_err(|_| Failure::Output)? != 0 {
        return Err(Failure::Output);
    }
    Ok(bytes)
}

struct Attempt {
    path: PathBuf,
    directory: File,
    may_clean: bool,
}

impl Attempt {
    fn create(root: &Path) -> io::Result<Self> {
        let mut id = GUID::default();
        // SAFETY: id is writable storage for a GUID.
        if unsafe { CoCreateGuid(&mut id) } < 0 {
            return Err(io::Error::other("could not generate battery attempt ID"));
        }
        let tail = u64::from_be_bytes(id.data4);
        let path = root.join(format!(
            "OpenBlade.BatteryReport.{:08x}{:04x}{:04x}{tail:016x}",
            id.data1, id.data2, id.data3
        ));
        fs::create_dir(&path)?;
        // Deny rename/deletion until cleanup and never open a substituted link.
        // If we cannot secure the handle, leave the path alone.
        let directory = OpenOptions::new()
            .access_mode(DELETE | FILE_READ_ATTRIBUTES)
            .share_mode(FILE_SHARE_READ | FILE_SHARE_WRITE)
            .custom_flags(FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT)
            .open(&path)?;
        let metadata = directory.metadata()?;
        if !metadata.is_dir() || metadata.file_attributes() & FILE_ATTRIBUTE_REPARSE_POINT != 0 {
            return Err(io::Error::other("invalid battery attempt directory"));
        }
        Ok(Self {
            path,
            directory,
            may_clean: true,
        })
    }
}

impl Drop for Attempt {
    fn drop(&mut self) {
        if !self.may_clean {
            return;
        }
        let _ = fs::remove_file(self.path.join(REPORT_FILE));
        let delete = FILE_DISPOSITION_INFO { DeleteFile: true };
        // SAFETY: The directory handle and info are live. This deletes only the
        // exact empty directory held open by the attempt, never its neighbors.
        unsafe {
            SetFileInformationByHandle(
                self.directory.as_raw_handle(),
                FileDispositionInfo,
                (&raw const delete).cast(),
                size_of::<FILE_DISPOSITION_INFO>() as u32,
            );
        }
    }
}

#[cfg(test)]
mod tests;

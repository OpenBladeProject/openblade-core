//! The protected adapter for the keyboard Driver-mode ownership record
//! (`openblade_domain::driver_mode_history`), `keyboard-driver-mode.bin` in
//! the same original owner custody as the thermal journal. Never resolves a
//! new root.
//!
//! Reads fail closed. A record that does not parse, names an owner this
//! history never made Active, or sits beside a stage left by an earlier
//! replace is refused, and nothing here rewrites or removes it: the stage is
//! kept as evidence, as a thermal `.pending` is. A file of the wrong identity
//! or protection is fatal admission evidence, as for every owner record.
//!
//! Only the generation that activated this custody replaces the record, as
//! its own Active owner, by compare-and-swap on the record it read, through
//! the same staged publication as the thermal journal (`replace_staged`). A
//! recovery-only process never activates, so it can read the record and seed
//! its IDs but never write it.
//!
//! A replace is admitted by the domain's edge table (`history::follows`)
//! and that compare-and-swap alone. The table admits any IDs on an edge into
//! an intent's prior, so which IDs a revert names is the ledger's to get
//! right (`driver_mode_ledger`), not this store's.
use super::*;
use openblade_domain::driver_mode_history::{self as history, Record as DriverModeRecord};
use windows_sys::Win32::Storage::FileSystem::FILE_SHARE_READ;

/// The record.
const RECORD: &str = "keyboard-driver-mode.bin";
/// Its stage while a replace is in progress.
const STAGE: &str = "keyboard-driver-mode.bin.pending";
/// The owner-directory names this family owns (`separately_validated`).
pub(super) const NAMES: [&str; 2] = [RECORD, STAGE];

impl Store {
    /// The record, `None` while there is none. It authenticates the owner
    /// custody first and writes nothing.
    pub(in crate::installed) fn driver_mode_record_read(
        &self,
    ) -> io::Result<Option<DriverModeRecord>> {
        self.verify_journal_custody()?;
        if self.driver_mode_stage_present()? {
            return Err(deny());
        }
        let Some(bytes) = self.driver_mode_file()? else {
            return Ok(None);
        };
        let record = DriverModeRecord::parse(&bytes)?;
        // Every record names the Active owner that wrote it.
        if !self.active_records.contains_key(record.owner()) {
            return Err(deny());
        }
        Ok(Some(record))
    }

    /// Replaces the record with `next` only while it still holds `expected`
    /// (`None`: no record), and only as this process's own Active owner. Any
    /// error after the stage is created leaves the record holding either
    /// `expected` or `next`, with the stage refusing later reads when it
    /// remains: the caller must treat both as possible.
    pub(in crate::installed) fn driver_mode_record_replace(
        &self,
        expected: Option<&DriverModeRecord>,
        next: &DriverModeRecord,
    ) -> io::Result<()> {
        if next.owner() != self.driver_mode_writer()?.nonce || !history::follows(expected, next) {
            return Err(deny());
        }
        if self.driver_mode_record_read()?.as_ref() != expected {
            return Err(deny());
        }
        let expected = expected.map(DriverModeRecord::bytes);
        self.replace_staged(
            Staged {
                stage: STAGE,
                target: RECORD,
                boundary: "driver-mode",
                protection: "Keyboard Driver-mode record staging protection is invalid.",
            },
            &next.bytes(),
            &|| Ok(self.driver_mode_file()? == expected),
            &|| Ok(self.driver_mode_record_read()?.as_ref() == Some(next)),
        )
    }

    /// The Active owner nonce a replace must name: this process's own, and
    /// only once it activated this custody.
    pub(in crate::installed) fn driver_mode_record_owner(&self) -> io::Result<String> {
        self.driver_mode_writer().map(|writer| writer.nonce.clone())
    }

    /// The owner record of the generation that may write: the last one,
    /// Active, made by this process.
    fn driver_mode_writer(&self) -> io::Result<&Record> {
        self.last
            .as_ref()
            .filter(|last| self.activated_here && last.phase == Phase::Active)
            .ok_or_else(deny)
    }

    /// Whether anything holds the stage's name. Its content is evidence only
    /// and is never read.
    fn driver_mode_stage_present(&self) -> io::Result<bool> {
        match paths::open_relative(
            &self.root,
            OsStr::new(STAGE),
            FILE_GENERIC_READ,
            FILE_SHARE_READ,
            FILE_OPEN,
            FILE_OPEN_REPARSE_POINT,
        ) {
            Err(error) if paths::missing_leaf(&error) => Ok(false),
            Err(error) => Err(error),
            Ok(_) => Ok(true),
        }
    }

    /// The record's bytes, `None` while there is none.
    fn driver_mode_file(&self) -> io::Result<Option<Vec<u8>>> {
        let mut file = match paths::open_relative(
            &self.root,
            OsStr::new(RECORD),
            FILE_GENERIC_READ,
            FILE_SHARE_READ,
            FILE_OPEN,
            FILE_NON_DIRECTORY_FILE | FILE_OPEN_REPARSE_POINT,
        ) {
            Ok(file) => file,
            Err(error) if paths::missing_leaf(&error) => return Ok(None),
            Err(error) => return Err(error),
        };
        paths::check_regular_file(&file)
            .and_then(|_| self.check(&file, Policy::Record))
            .map_err(|_| {
                crate::installed::readiness::thermal_mutation::fatal_admission(
                    "Protected keyboard Driver-mode record identity or protection changed.",
                )
            })?;
        let n = usize::try_from(file.metadata()?.len()).map_err(|_| deny())?;
        if n > history::MAX_BYTES {
            return Err(deny());
        }
        let mut bytes = vec![0; n];
        file.read_exact(&mut bytes)?;
        if file.metadata()?.len() != n as u64 {
            return Err(deny());
        }
        Ok(Some(bytes))
    }
}

#[cfg(test)]
#[path = "driver_mode_record_store_tests.rs"]
mod tests;

//! Managed `FirmwareVersions.McuVersion` on the installed read source
//! (`service-live-readings-v1`).
//!
//! Managed `ServiceControlRuntime.ReadFirmwareVersionsAsync` reads it with
//! `IBladeDevice.ReadMcuVersionAsync` inside each full verified observation,
//! after the confirmed state, while the record's MCU version is still
//! `Unavailable` and fewer than `MAXIMUM_ATTEMPTS` reads were made, and
//! publishes it with that observation. A failed read leaves `Unavailable`; a
//! version, once read, is never read again or cleared. Resume restores the
//! budget.
//!
//! Here each read is the exact RZ09-0581 00/87 query (`McuVersionRead`) on
//! the serialized read job, after the thermal read and every other query, so
//! it spends only its own share of that job and a failed query costs no other
//! reading. An attempt is a query whose SET went out (`McuQuerySends`,
//! counted on the worker): a job that sent none, such as one whose thermal
//! read failed, costs none, and a query whose observation was dropped still
//! counts, where managed returns the attempt of a canceled read. The budget
//! counts under one read admission, and a new admission, which a lifecycle
//! transition such as resume mints, restores it. Only a version confirmed by
//! a job whose reading the source accepted is kept.
use crate::{
    discovery::performance::{McuQuerySends, McuVersionRead, ReadAdmission},
    service_status::FirmwareVersions,
};
use std::cell::{Cell, RefCell};

/// Managed `MaximumMcuVersionReadAttempts`.
const MAXIMUM_ATTEMPTS: u64 = 3;

/// The MCU version and the budget of queries left to read it.
#[derive(Default)]
pub(super) struct Inventory {
    sends: McuQuerySends,
    /// The admission the budget counts under, and the queries sent before it.
    budget: Cell<Option<(ReadAdmission, u64)>>,
    version: RefCell<Option<String>>,
}

impl Inventory {
    /// The counter a job under `admission` passes to the worker to send the
    /// query, or None once the version is known or the budget is spent.
    pub(super) fn query(&self, admission: ReadAdmission) -> Option<McuQuerySends> {
        if self.version.borrow().is_some() {
            return None;
        }
        let sent = self.sends.count();
        let before = match self.budget.get() {
            Some((counted, before)) if counted == admission => before,
            _ => {
                self.budget.set(Some((admission, sent)));
                sent
            }
        };
        (sent - before < MAXIMUM_ATTEMPTS).then(|| self.sends.clone())
    }

    /// Keeps the version an accepted reading confirmed.
    pub(super) fn record(&self, read: McuVersionRead) {
        if let McuVersionRead::Confirmed(version) = read {
            *self.version.borrow_mut() = Some(version.to_string());
        }
    }

    /// `record` with the MCU version once it is known.
    pub(super) fn apply(&self, mut record: FirmwareVersions) -> FirmwareVersions {
        if let Some(version) = &*self.version.borrow() {
            record.mcu_version.clone_from(version);
        }
        record
    }

    #[cfg(test)]
    pub(super) fn sends_for_tests(&self) -> &McuQuerySends {
        &self.sends
    }
}

#[cfg(test)]
#[path = "mcu_version_tests.rs"]
mod tests;

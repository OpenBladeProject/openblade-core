//! Managed `FirmwareVersions.McuVersion` on the installed read source
//! (`service-live-readings-v1`).
//!
//! Managed `ServiceControlRuntime.ReadFirmwareVersionsAsync` reads it with
//! `IBladeDevice.ReadMcuVersionAsync` inside each full verified observation,
//! after the confirmed state, while the record's MCU version is still
//! `Unavailable` and fewer than `MAXIMUM_ATTEMPTS` reads were made, and
//! publishes it with that observation. A failed read leaves `Unavailable`; a
//! version, once read, is never read again or cleared. Only resume
//! (`BeginResumeValidationAsync`) and a confirmed device loss restore the
//! budget; a session change or power notice does not.
//!
//! Here each read is the exact RZ09-0581 00/87 query (`McuVersionRead`) on
//! the serialized read job, after the thermal read and every other query, so
//! it spends only its own share of that job and a failed query costs no other
//! reading. An attempt is a query whose SET went out (`McuQuerySends`,
//! counted on the worker): a job that sent none, such as one whose thermal
//! read failed, costs none, and a query whose observation was dropped still
//! counts, where managed returns the attempt of a canceled read. The budget
//! counts since the last resume, which the low-power gate's generation
//! identifies (`ReadCustody::resume_generation`). It does not count under the
//! read admission, whose lifecycle epoch advances at every session or power
//! notice. There is no device-loss reset: a device loss ends the native read
//! source's lease, and with it every later query, for the process. Only a
//! version confirmed by a job whose reading the source accepted is kept.
use crate::{
    discovery::performance::{McuQuerySends, McuVersionRead},
    service_status::FirmwareVersions,
};
use std::cell::{Cell, RefCell};

/// Managed `MaximumMcuVersionReadAttempts`.
const MAXIMUM_ATTEMPTS: u64 = 3;

/// The most queries one read source sends, whatever the resumes: four
/// budgets. Managed has no such cap. A query that gets no terminal reply
/// leaves its transaction ID quarantined for the process, so a firmware that
/// never answers 00/87 would otherwise take three more of the pool's 254 IDs
/// at every resume.
const MAXIMUM_SENDS: u64 = 4 * MAXIMUM_ATTEMPTS;

/// The MCU version and the budget of queries left to read it.
#[derive(Default)]
pub(super) struct Inventory {
    sends: McuQuerySends,
    /// The resume generation the budget counts under, and the queries sent
    /// before it.
    budget: Cell<Option<(u64, u64)>>,
    version: RefCell<Option<String>>,
}

impl Inventory {
    /// The counter a job passes to the worker to send the query, or None:
    /// once the version is known, once this source has sent `MAXIMUM_SENDS`,
    /// without a `resume` generation, or once the budget since that resume
    /// is spent. `resume` is read only while a query may still be sent.
    pub(super) fn query(&self, resume: impl FnOnce() -> Option<u64>) -> Option<McuQuerySends> {
        if self.version.borrow().is_some() {
            return None;
        }
        let sent = self.sends.count();
        if sent >= MAXIMUM_SENDS {
            return None;
        }
        let resume = resume()?;
        let before = match self.budget.get() {
            Some((counted, before)) if counted == resume => before,
            _ => {
                self.budget.set(Some((resume, sent)));
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

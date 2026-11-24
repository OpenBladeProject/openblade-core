//! The low-power preparation of a confirmed suspend
//! (`service-keyboard-driver-mode-v1`), managed
//! `ServiceControlRuntime.PrepareForLowPowerCoreAsync` as
//! `ServiceSessionPowerTransitionCoordinator` runs it: once per suspend
//! generation, within `MAXIMUM_AGE` of the notice, the keyboard's Normal
//! release under the Service's cleanup admission for that exact generation
//! (`Cleanup`, `WriteScope::LowPowerCleanup`), so a resume, a DisplayOn or
//! another suspend ends it at its next authorization. Saved intent is
//! unchanged; the next active reconciliation applies it. Fast Startup
//! hibernates the Service instead of stopping it, so its PBT_APMSUSPEND runs
//! this path, not the shutdown release.
//!
//! The fans are not touched. AGENTS.md rule 1 names only the keyboard's
//! Normal release at a low-power boundary, and managed restores fans there
//! only for a Battery Saver fan stop it owns (`OwnsBatterySaverFanStop`,
//! `ServiceControlRuntime.cs:13069-13087`, property at :21049). Native has
//! no Fan Stop yet (`openblade_domain::profile_set::policy`), so owned manual
//! fans stay as they are through the suspend and owned Stop hands them back.
//! TODO(fan-stop): when Fan Stop lands, restore an owned Battery Saver fan
//! stop here, after the keyboard, as managed does, under this generation's
//! cleanup admission. Managed's fan stop restore has no Windows-AC-only
//! pre-check; do not copy the one owned Stop's handback recipe needs.
use super::keyboard::{Boundary, Release};
use super::*;
use std::{cell::Cell, time::Duration};

/// Managed `SessionPowerTransition.MaximumAgeMilliseconds`: a transition
/// older than this from its notice's QPC timestamp is not prepared.
/// The SCM handler returns at once, as managed's does, so this bounds the
/// work's staleness and is not time Windows waits for.
pub(in super::super) const MAXIMUM_AGE: Duration = Duration::from_millis(1500);
/// The keyboard's largest share, its shutdown one.
const KEYBOARD_SHARE: Duration = keyboard::SHUTDOWN_BUDGET;

/// The suspend generation last prepared and how it ended.
#[derive(Default)]
pub(super) struct Prepared {
    generation: Cell<Option<u64>>,
    outcome: Cell<Option<Release>>,
}

impl Runtime {
    /// Prepares a new suspend generation once, on the origin tick or the
    /// suspend wake before the keyboard step. Nothing runs while the gate is
    /// clear, unreadable or exhausted, or for a generation already prepared.
    /// The outcome is a diagnostic; only fatal admission evidence is an
    /// error.
    pub(in super::super) async fn prepare_for_low_power(
        &self,
        mailbox: &MailboxSource,
    ) -> io::Result<()> {
        let Some(transition) = self.low_power.get().and_then(|read| read()) else {
            return Ok(());
        };
        let prepared = &self.low_power_prepared;
        if !transition.suspending() || prepared.generation.get() == Some(transition.generation) {
            return Ok(());
        }
        prepared.generation.set(Some(transition.generation));
        let age = self
            .notice_age
            .get()
            .map_or(Some(Duration::ZERO), |age| age(transition.timestamp));
        let Some(remaining) = age
            .and_then(|age| MAXIMUM_AGE.checked_sub(age))
            .filter(|remaining| !remaining.is_zero())
        else {
            prepared.outcome.set(Some(Release::Exhausted));
            return Ok(());
        };
        let keyboard = self
            .release_driver_mode_for(
                mailbox,
                Boundary::LowPower(transition.generation),
                remaining.min(KEYBOARD_SHARE),
            )
            .await?;
        prepared.outcome.set(Some(keyboard));
        Ok(())
    }
}

#[cfg(test)]
impl Runtime {
    /// The last prepared generation's outcome.
    pub(in super::super) fn low_power_outcome_for_tests(&self) -> Option<Release> {
        self.low_power_prepared.outcome.get()
    }
}

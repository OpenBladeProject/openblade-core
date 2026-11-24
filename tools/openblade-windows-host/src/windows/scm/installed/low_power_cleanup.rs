//! The low-power preparation of a confirmed suspend
//! (`service-keyboard-driver-mode-v1`), managed
//! `ServiceControlRuntime.PrepareForLowPowerCoreAsync` as
//! `ServiceSessionPowerTransitionCoordinator` runs it: once per suspend
//! generation, within `MAXIMUM_AGE` of the notice, the keyboard's Normal
//! release and then the owned fan Automatic handback. Both hold the Service's
//! cleanup admission for that exact generation (`Cleanup`,
//! `WriteScope::LowPowerCleanup`), so a resume, a DisplayOn or another
//! suspend ends them at their next authorization. A keyboard failure never
//! skips the fans. Saved intent is unchanged; the next active reconciliation
//! applies it. Fast Startup hibernates the Service instead of stopping it,
//! so its PBT_APMSUSPEND runs this path, not the shutdown release.
use super::fan::FanHandback;
use super::keyboard::{Boundary, Release};
use super::*;
use std::{cell::Cell, time::Duration};

/// Managed `SessionPowerTransition.MaximumAgeMilliseconds`: a transition
/// older than this from its notice's QPC timestamp is not prepared.
/// The SCM handler returns at once, as managed's does, so this bounds the
/// work's staleness and is not time Windows waits for.
pub(in super::super) const MAXIMUM_AGE: Duration = Duration::from_millis(1500);
/// What the keyboard leaves for the fan handback before the window ends: by
/// count, the cleanup recipe's nine exchanges, about 25 fresh admissions and
/// six journal writes take about 0.55-0.6 s, an estimate pending an attended
/// timing.
const FAN_RESERVE: Duration = Duration::from_millis(600);
/// The keyboard's largest share, its shutdown one.
const KEYBOARD_SHARE: Duration = keyboard::SHUTDOWN_BUDGET;
const _: () = assert!(KEYBOARD_SHARE.as_nanos() + FAN_RESERVE.as_nanos() == MAXIMUM_AGE.as_nanos());

/// The suspend generation last prepared and how it ended.
#[derive(Default)]
pub(super) struct Prepared {
    generation: Cell<Option<u64>>,
    outcome: Cell<Option<(Release, FanHandback)>>,
}

impl Runtime {
    /// Prepares a new suspend generation once, on the origin tick or the
    /// suspend wake before the keyboard step. Nothing runs while the gate is
    /// clear, unreadable or exhausted, or for a generation already prepared.
    /// The outcomes are diagnostics; only fatal admission evidence is an
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
            prepared
                .outcome
                .set(Some((Release::Exhausted, FanHandback::Exhausted)));
            return Ok(());
        };
        let deadline = Instant::now() + remaining;
        let generation = transition.generation;
        let keyboard = self
            .release_driver_mode_for(
                mailbox,
                Boundary::LowPower(generation),
                remaining.saturating_sub(FAN_RESERVE).min(KEYBOARD_SHARE),
            )
            .await?;
        let fans = self
            .hand_back_fans(mailbox, CleanupScope::LowPower { generation }, deadline)
            .await?;
        prepared.outcome.set(Some((keyboard, fans)));
        Ok(())
    }
}

#[cfg(test)]
impl Runtime {
    /// The last prepared generation's outcomes.
    pub(in super::super) fn low_power_outcome_for_tests(&self) -> Option<(Release, FanHandback)> {
        self.low_power_prepared.outcome.get()
    }
}

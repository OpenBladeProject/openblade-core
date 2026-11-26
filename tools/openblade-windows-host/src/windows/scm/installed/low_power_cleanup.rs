//! The low-power preparation of a confirmed suspend
//! (`service-keyboard-driver-mode-v1`), managed
//! `ServiceControlRuntime.PrepareForLowPowerCoreAsync` as
//! `ServiceSessionPowerTransitionCoordinator` runs it: once per suspend
//! generation, within `MAXIMUM_AGE` of the notice, the keyboard's Normal
//! release under the Service's cleanup admission for that exact generation
//! (`Cleanup`, `WriteScope::LowPowerCleanup`), so a resume, a DisplayOn or
//! another suspend ends it at its next authorization. With the lifecycle
//! triggers, owned HyperBoost then leaves in what is left of the window,
//! under the same generation's cleanup admission (`hyperboost::cleanup`).
//! A display-brightness usage still held is released before the keyboard
//! (`service-brightness-hid-usage-v1`), as managed releases it first.
//! Saved intent is unchanged; the next active reconciliation applies it, and
//! nothing is acquired or entered again before Windows reports the system
//! active. Fast Startup hibernates the Service instead of stopping it, so its
//! PBT_APMSUSPEND runs this path, not the shutdown release.
//!
//! Managed prepares in the other order, HyperBoost before the keyboard, each
//! under its own cancellation. Native has the one window, so the keyboard
//! goes first and keeps the whole share it had. An exit ahead of it could
//! spend that share, and one that stopped after a setter leaves an unsettled
//! row, at which the release refuses (`Release::Unsettled`). AGENTS.md rule 1
//! names the keyboard's Normal release at this boundary, so no HyperBoost
//! failure, refusal or timeout may skip it.
//!
//! The SCM callback that delivered the suspend is held until this
//! preparation has recorded an outcome for that generation, at most
//! `MAXIMUM_AGE` from the notice (`Lifecycle::arm_suspend_hold`), so Windows
//! does not suspend under the release. Every exit records it, through a drop
//! guard. Managed's SCM callback returns at once; its Session Agent relay
//! waits the same way for the Service (`SessionPowerTransitionRelay.cs:101-108`).
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
use crate::origin_lifetime::HoldArm;
use std::{
    cell::{Cell, OnceCell},
    time::Duration,
};

/// Managed `SessionPowerTransition.MaximumAgeMilliseconds`: a transition
/// older than this from its notice's QPC timestamp is not prepared. It is
/// also the longest the suspend's SCM callback is held for this
/// preparation, counted from the same stamp, as the managed Session Agent
/// relay bounds its wait.
pub(in super::super) const MAXIMUM_AGE: Duration = Duration::from_millis(1500);
/// The keyboard's largest share, its shutdown one.
const KEYBOARD_SHARE: Duration = keyboard::SHUTDOWN_BUDGET;

/// The suspend generation last prepared, how it ended, and the arm of the
/// suspend hold this preparation releases.
#[derive(Default)]
pub(super) struct Prepared {
    generation: Cell<Option<u64>>,
    outcome: Cell<Option<Release>>,
    // How that generation's owned HyperBoost exit ended.
    #[cfg(all(
        feature = "service-hyperboost-v1",
        feature = "service-profile-triggers-v1"
    ))]
    hyperboost: Cell<Option<hyperboost::cleanup::CleanupExit>>,
    // Unset holds nothing: the SCM callback returns at once.
    hold: OnceCell<HoldArm>,
}

impl Prepared {
    /// Releases the suspend hold of `arm`'s lifecycle as each generation is
    /// prepared. `None` holds nothing.
    #[cfg(not(test))]
    pub(super) fn holding(arm: Option<HoldArm>) -> Self {
        Self {
            hold: arm.map_or_else(OnceCell::new, OnceCell::from),
            ..Self::default()
        }
    }
}

/// Records `generation` in the suspend hold when the preparation exits,
/// however it exits: prepared, aged out, already prepared, refused, an
/// error or a dropped future.
struct Recorded<'a>(&'a Prepared, u64);
impl Drop for Recorded<'_> {
    fn drop(&mut self) {
        if let Some(hold) = self.0.hold.get() {
            hold.prepared(self.1);
        }
    }
}

impl Runtime {
    /// Prepares a new suspend generation once, on the origin tick or the
    /// suspend wake before the keyboard step. Nothing runs while the gate is
    /// clear, unreadable or exhausted, or for a generation already prepared.
    /// The outcome is a diagnostic; only fatal admission evidence is an
    /// error. Every exit after the gate is read releases the hold of the
    /// generation it read.
    pub(in super::super) async fn prepare_for_low_power(
        &self,
        mailbox: &MailboxSource,
    ) -> io::Result<()> {
        let Some(transition) = self.low_power.get().and_then(|read| read()) else {
            return Ok(());
        };
        let prepared = &self.low_power_prepared;
        let _recorded = Recorded(prepared, transition.generation);
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
        // The window's end, which bounds the HyperBoost exit after the
        // keyboard's share.
        #[cfg(all(
            feature = "service-hyperboost-v1",
            feature = "service-profile-triggers-v1"
        ))]
        let end = Instant::now() + remaining;
        // Managed releases a held display-brightness usage first, while the
        // boundary is still this generation's (`PrepareForLowPowerCoreAsync`).
        // It ends every brightness request's wait and needs no gate, so no
        // press keeps the gate from the keyboard, and it takes only what the
        // window holds beyond the keyboard's share, so the keyboard keeps the
        // whole of it; a release that has not answered by then stays queued
        // on the lane and runs while the keyboard is released. Its outcome is
        // a diagnostic, and nothing it does skips the keyboard.
        #[cfg(feature = "service-brightness-hid-usage-v1")]
        let remaining = {
            let start = Instant::now();
            let current = self.low_power.get().and_then(|read| read());
            if current.is_some_and(|current| {
                current.generation == transition.generation && current.suspending()
            }) {
                let share = remaining
                    .saturating_sub(KEYBOARD_SHARE)
                    .min(BRIGHTNESS_SHUTDOWN_BUDGET);
                let _ = self
                    .release_display_brightness(share, start + remaining)
                    .await;
            }
            remaining.saturating_sub(start.elapsed())
        };
        let keyboard = self
            .release_driver_mode_for(
                mailbox,
                Boundary::LowPower(transition.generation),
                remaining.min(KEYBOARD_SHARE),
            )
            .await?;
        prepared.outcome.set(Some(keyboard));
        // After the keyboard, whatever its outcome: only its fatal admission
        // evidence, which stops the Service, returns before this.
        #[cfg(all(
            feature = "service-hyperboost-v1",
            feature = "service-profile-triggers-v1"
        ))]
        {
            prepared.hyperboost.set(None);
            let scope = CleanupScope::LowPower {
                generation: transition.generation,
            };
            let exit = self.exit_owned_hyperboost_for(mailbox, scope, end).await?;
            prepared.hyperboost.set(Some(exit));
        }
        Ok(())
    }
}

#[cfg(test)]
impl Runtime {
    /// The last prepared generation's outcome.
    pub(in super::super) fn low_power_outcome_for_tests(&self) -> Option<Release> {
        self.low_power_prepared.outcome.get()
    }
    /// How the last prepared generation's owned HyperBoost exit ended.
    #[cfg(all(
        feature = "service-hyperboost-v1",
        feature = "service-profile-triggers-v1"
    ))]
    pub(in super::super) fn low_power_hyperboost_for_tests(
        &self,
    ) -> Option<hyperboost::cleanup::CleanupExit> {
        self.low_power_prepared.hyperboost.get()
    }
    /// Arms `lifecycle`'s suspend hold for this preparation, on this thread,
    /// as `Runtime::native` arms the origin's; it may be set once.
    pub(in super::super) fn hold_suspend_for_tests(
        &self,
        lifecycle: &crate::origin_lifetime::Lifecycle,
    ) {
        let arm = lifecycle.arm_suspend_hold(MAXIMUM_AGE).unwrap();
        assert!(self.low_power_prepared.hold.set(arm).is_ok());
    }
}

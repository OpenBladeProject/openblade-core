use openblade_platform_windows::display::{
    DisplayReadError, InternalDisplayRefresh, RefreshSnapshot,
};

use super::{
    Attempt, BLOCKED, FAILED, INDETERMINATE, SUCCEEDED, Value, display_state,
    journal::{Change, Panel, RefreshBaseline},
    panel::{Context, Outcome, ScriptedDisplay, wait_for_disconnect},
};

#[derive(Clone, Copy, PartialEq, Eq)]
pub(crate) enum RefreshFixture {
    Available,
    StaleTarget,
    StalePower,
    Inactive,
    ValidationError,
    StaleAfterValidation,
    SubmissionError,
    ReadbackMismatch,
    SiblingReadback,
    StalledAdmission,
    StalledSubmission,
}

impl ScriptedDisplay {
    pub(super) fn apply_refresh(
        &mut self,
        (power, key): (u8, &str),
        context: Option<Context>,
        reading: &Result<InternalDisplayRefresh, DisplayReadError>,
        state: &mut Value,
        attempt: Attempt<'_>,
    ) -> Outcome {
        let cancelled = attempt.cancelled;
        let Some(context) = context.filter(|context| context.power_source == power) else {
            return rejected(
                BLOCKED,
                "The expected power or active session is unavailable.",
            );
        };
        let Some(before) = single(reading) else {
            return rejected(FAILED, "The internal display is unavailable.");
        };
        let Some(identity) = Panel::observe(&before.display) else {
            return rejected(FAILED, "The exact internal display is unavailable.");
        };
        let Some(option) = before
            .refresh_options
            .iter()
            .find(|option| option.key == key)
        else {
            return rejected(FAILED, "That refresh-rate option is unavailable.");
        };

        if self.refresh_fixture == RefreshFixture::StalledAdmission {
            wait_for_disconnect("REFRESH ADMISSION", cancelled);
        }
        let Ok((fresh, packet)) = self.scenario.prepare_refresh(
            self.enabled,
            self.timing,
            self.refresh_fixture == RefreshFixture::StaleTarget,
            key,
        ) else {
            return rejected(BLOCKED, "The display topology could not be confirmed.");
        };
        let InternalDisplayRefresh::Single(fresh) = fresh else {
            return rejected(BLOCKED, "The internal display changed.");
        };
        if fresh.as_ref() != before {
            return rejected(BLOCKED, "The internal display changed.");
        }
        if cancelled() {
            return rejected(FAILED, "Refresh-rate change cancelled before submission.");
        }
        if self.context(true) != Some(context) {
            return rejected(BLOCKED, "The power or session context became stale.");
        }
        // The fresh classifier requires both CCD rates and the boost bit.
        if fresh.current_refresh_key.as_deref() == Some(key) {
            return (SUCCEEDED, None, false);
        }
        let Some(validated) = packet.and_then(|packet| {
            packet.validate(self.refresh_fixture == RefreshFixture::ValidationError)
        }) else {
            return rejected(
                FAILED,
                "The refresh-rate configuration could not be validated.",
            );
        };
        let last = self.read(self.refresh_fixture == RefreshFixture::StaleAfterValidation);
        if single(&last) != Some(before) {
            return rejected(BLOCKED, "The internal display changed after validation.");
        }
        if self.context(true) != Some(context) {
            return rejected(BLOCKED, "The power or session context became stale.");
        }
        if cancelled() {
            return rejected(FAILED, "Refresh-rate change cancelled before submission.");
        }

        let Some(before_value) = RefreshBaseline::observe(&fresh) else {
            return rejected(BLOCKED, "The refresh-rate baseline could not be confirmed.");
        };
        if !(attempt.record)(
            identity.clone(),
            Change::Refresh {
                before: before_value,
                requested: key.into(),
            },
        ) {
            return rejected(BLOCKED, super::JOURNAL_BLOCKED);
        }
        if cancelled() {
            return rejected(FAILED, "Refresh-rate change cancelled before submission.");
        }
        let applied = validated.submit();
        if self.refresh_fixture != RefreshFixture::ReadbackMismatch {
            self.timing = applied;
        }
        if self.refresh_fixture == RefreshFixture::StalledSubmission {
            wait_for_disconnect("REFRESH SUBMITTED", cancelled);
        }
        // Reconcile independently even when the native result could have been lost.
        let readback = self.read(self.refresh_fixture == RefreshFixture::SiblingReadback);
        let after = single(&readback).filter(|after| {
            Panel::observe(&after.display).as_ref() == Some(&identity)
                && (after.width, after.height) == (before.width, before.height)
                && after.display.supports_virtual_mode == before.display.supports_virtual_mode
        });
        let (observed, _, error) = display_state(&readback);
        if after.is_some() && error.is_none() {
            *state = observed;
        }
        if cancelled() {
            return pending("the request was cancelled after submission");
        }
        if self.refresh_fixture == RefreshFixture::SubmissionError {
            return pending("the native result was uncertain");
        }
        if self.context(true) != Some(context) {
            return pending("the power or session context became stale");
        }
        let Some(after) = after.filter(|_| error.is_none()) else {
            return pending("the exact target could not be read back");
        };
        if after.current_refresh_key.as_deref() != Some(key)
            || !after.refresh_options.contains(option)
        {
            return pending("Windows did not confirm the requested refresh-rate mode");
        }
        (SUCCEEDED, None, true)
    }
}

fn single(reading: &Result<InternalDisplayRefresh, DisplayReadError>) -> Option<&RefreshSnapshot> {
    match reading.as_ref().ok()? {
        InternalDisplayRefresh::Single(snapshot) => Some(snapshot),
        _ => None,
    }
}

fn rejected(status: i32, reason: &str) -> Outcome {
    (status, Some(reason.into()), false)
}

fn pending(reason: &str) -> Outcome {
    (
        INDETERMINATE,
        Some(format!(
            "Refresh-rate submission is indeterminate ({reason}); writes are paused."
        )),
        true,
    )
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::session_display::HdrFixture;
    use openblade_platform_windows::display::scripted::Scenario;

    #[test]
    fn fresh_no_op_requires_both_timings_and_the_boost_bit() {
        let mut panel = ScriptedDisplay::new(
            Scenario::Dynamic,
            HdrFixture::Available,
            RefreshFixture::Available,
        );
        fn apply(panel: &mut ScriptedDisplay, key: &str) -> Outcome {
            let reading = panel.read(false);
            let mut state = display_state(&reading).0;
            panel.apply_refresh(
                (0, key),
                panel.context(false),
                &reading,
                &mut state,
                Attempt {
                    cancelled: &|| false,
                    record: &|_, _| true,
                },
            )
        }
        // The desktop and physical rates already match this DRR option. The
        // boost bit is off, so the fixture must still submit once.
        let key = "dynamic-60-240";
        assert_eq!(apply(&mut panel, key), (SUCCEEDED, None, true));
        panel.refresh_fixture = RefreshFixture::ValidationError;
        assert_eq!(apply(&mut panel, key), (SUCCEEDED, None, false));
        panel.refresh_fixture = RefreshFixture::StaleTarget;
        assert_eq!(apply(&mut panel, key).0, BLOCKED);
    }
}

//! Scripted Windows lifecycle notifications for the private composed host.

use super::{Power, Result, Scenario, ScenarioState};
use crate::{fan_runtime, performance_runtime::Step as PerformanceStep};
use openblade_domain::recovery::ScopedStatus;
use std::io;
use tokio::sync::mpsc;

type Reply = Option<[u8; 2]>;
pub(super) const FULL: Reply = Some([0x11, 0x11]);
const LIMITED: Reply = Some([0x07, 0x11]);
const OTHER_FULL: Reply = Some([0x12, 0x11]);
const UNKNOWN: Reply = Some([0x10, 0x11]);
const MAX_ATTEMPTS: usize = 5;
pub(super) const CONFIRMATIONS: &[Reply] = &[UNKNOWN, FULL, None, UNKNOWN, FULL];

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum Adapter {
    Battery,
    UsbC(u16),
    Full280,
    Full330,
    Sufficient,
    Limited,
}

impl Adapter {
    fn source(self) -> Option<Power> {
        match self {
            Self::Battery => None,
            Self::UsbC(_) | Self::Limited => Some(Power::UsbC),
            Self::Full280 | Self::Full330 | Self::Sufficient => Some(Power::PluggedIn),
        }
    }
}

fn watts(level: u8) -> Option<u16> {
    // AdapterPowerProtocol.GetWatts uses vendor level codes, including 0x11
    // for 280W after the reserved 0x10 and 310W at 0x0f.
    const LEVELS: [u16; 16] = [
        0, 40, 45, 60, 65, 80, 90, 100, 120, 130, 150, 180, 200, 230, 250, 310,
    ];
    match level {
        0x11 => Some(280),
        0x12 => Some(330),
        0x13 => Some(400),
        _ => LEVELS.get(usize::from(level)).copied(),
    }
}

fn decode_adapter([actual, recommended]: [u8; 2]) -> Option<Adapter> {
    let actual = watts(actual)?;
    let recommended = watts(recommended).filter(|value| *value != 0)?;
    Some(match actual {
        0 => Adapter::Battery,
        45 | 60 | 65 | 80 | 90 | 100 if actual < recommended => Adapter::UsbC(actual),
        _ if actual < recommended => Adapter::Limited,
        280 => Adapter::Full280,
        330 => Adapter::Full330,
        _ => Adapter::Sufficient,
    })
}

fn resolve(ac_line: u8, mut read: impl FnMut() -> Reply) -> io::Result<Option<Power>> {
    match ac_line {
        0 => return Ok(Some(Power::Battery)),
        1 => {} // Windows only established external supply.
        _ => {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                "unknown AC line status",
            ));
        }
    }
    let mut previous = None;
    for _ in 0..MAX_ATTEMPTS {
        let Some(adapter) = read().and_then(decode_adapter) else {
            continue;
        };
        if let Some(known) = previous {
            // Compare decoded adapter identities before mapping to profiles.
            return Ok((known == adapter).then(|| adapter.source()).flatten());
        }
        previous = Some(adapter);
    }
    Ok(None)
}

#[derive(Default)]
pub(super) struct Lifecycle {
    epoch: u64,
    pub(super) planned_full: Option<u64>,
}

impl Lifecycle {
    pub(super) fn notify(
        &mut self,
        state: &mut ScenarioState,
        ac_line: u8,
        replies: &[Reply],
    ) -> io::Result<Result> {
        self.epoch = self
            .epoch
            .checked_add(1)
            .ok_or_else(|| io::Error::other("power observation epoch exhausted"))?;
        self.planned_full = None;
        let mut replies = replies.iter().copied();
        let thermal = state.thermal()?;
        thermal.power_unresolved = true;
        let source = resolve(ac_line, || {
            thermal.fans.read_scripted_adapter(replies.next().flatten())
        })?;
        if let Some(blocked) = state.thermal_block() {
            return Ok(blocked);
        }
        match source {
            None => Ok(result(
                ScopedStatus::Blocked,
                "External adapter classification unresolved.",
            )),
            Some(Power::PluggedIn) => {
                self.planned_full = Some(self.epoch);
                Ok(result(
                    ScopedStatus::Unchanged,
                    "Full-adapter profile planned.",
                ))
            }
            Some(source) => {
                // Selection is available here. The battery preset command and
                // limited-power thermal admission are outside this replay.
                select_power(state, source)?;
                state.thermal()?.power_unresolved = false;
                Ok(result(
                    ScopedStatus::Unchanged,
                    "Power profile selected without thermal writes.",
                ))
            }
        }
    }

    pub(super) fn apply(
        &mut self,
        state: &mut ScenarioState,
        plan: Option<u64>,
        fresh: Reply,
        lose_response: bool,
    ) -> io::Result<Result> {
        if plan != Some(self.epoch) || plan != self.planned_full {
            return Ok(result(
                ScopedStatus::Blocked,
                "Pending full-adapter authority was superseded.",
            ));
        }
        // Each notification grants at most one attempt. A failed admission
        // needs another observation, and a stale callback cannot spend it.
        self.planned_full = None;
        if let Some(blocked) = state.thermal_block() {
            return Ok(blocked);
        }
        if !fresh_full(&mut state.thermal()?.fans, fresh) {
            return Ok(result(
                ScopedStatus::Blocked,
                "Fresh adapter evidence does not confirm full power.",
            ));
        }
        if state.controller.power == Power::PluggedIn {
            state.thermal()?.power_unresolved = false;
            return Ok(result(
                ScopedStatus::Unchanged,
                "Full-adapter profile is already selected.",
            ));
        }
        let thermal = state.thermal()?;
        if !thermal.fans.automatic_confirmed() {
            // The fresh full-adapter evidence above admits cleanup of a pair
            // the scripted port already owns. A blocked or uncertain cleanup
            // cannot spend the planned preset write.
            let handback = thermal.fans.handback()?;
            if matches!(
                handback.status,
                ScopedStatus::Blocked | ScopedStatus::Failed
            ) {
                return Ok(result(handback.status, handback.message));
            }
        }
        if !thermal.fans.fresh_automatic() {
            return Ok(result(
                ScopedStatus::Blocked,
                "The scoped preset recipe requires a confirmed Automatic fan pair.",
            ));
        }
        select_power(state, Power::PluggedIn)?;
        let value = state.profile_performance().0;
        let thermal = state.thermal()?;
        thermal.power_unresolved = false;
        if lose_response {
            PerformanceStep::LoseResponse.run(&mut thermal.performance);
        }
        let outcome = thermal
            .performance
            .apply_power_profile(value, || fresh_full(&mut thermal.fans, fresh))?;
        Ok(result(outcome.status, outcome.message))
    }
}

fn select_power(state: &mut ScenarioState, source: Power) -> io::Result<()> {
    if state.controller.power != source {
        let thermal = state.thermal()?;
        thermal.performance.expire_power_override();
        thermal.fans.expire_power_override();
        if let Some(lighting) = &mut state.lighting {
            lighting.expire_power_override();
        }
        state.controller.record_power(source);
    }
    Ok(())
}

pub(super) fn fresh_full_adapter(fans: &mut fan_runtime::Controller) -> bool {
    fresh_full(fans, FULL)
}

fn fresh_full(fans: &mut fan_runtime::Controller, reply: Reply) -> bool {
    fans.read_scripted_adapter(reply)
        .and_then(decode_adapter)
        .and_then(Adapter::source)
        == Some(Power::PluggedIn)
}

fn result(status: ScopedStatus, message: &'static str) -> Result {
    Result { status, message }
}

enum Step {
    Notify(u8, &'static [Reply]),
    SaveFullPlan,
    ApplySuperseded,
    ApplyFull,
    Reconcile,
}

impl Scenario {
    pub(crate) async fn drive_power_profile(
        &self,
        mut advance: mpsc::Receiver<()>,
        lose_response: bool,
    ) -> io::Result<()> {
        self.drive_power_steps(&mut advance, lose_response).await?;
        // The process stays available for passive clients until stdin closes.
        while advance.recv().await.is_some() {}
        Ok(())
    }

    pub(super) async fn drive_power_steps(
        &self,
        advance: &mut mpsc::Receiver<()>,
        lose_response: bool,
    ) -> io::Result<()> {
        let mut lifecycle = Lifecycle::default();
        use Step::*;
        let steps = [
            Notify(1, &[FULL, OTHER_FULL]),
            Notify(0, &[]),
            SaveFullPlan,
            Notify(1, &[LIMITED, UNKNOWN, LIMITED]),
            Notify(1, CONFIRMATIONS),
            ApplySuperseded,
            ApplyFull,
        ];
        let recovery = [
            Notify(1, &[LIMITED, LIMITED]),
            Reconcile,
            Notify(1, &[LIMITED, LIMITED]),
        ];
        let mut superseded = None;
        for step in steps
            .into_iter()
            .chain(lose_response.then_some(recovery).into_iter().flatten())
        {
            if advance.recv().await.is_none() {
                return Ok(());
            }
            self.update(|state| match step {
                Notify(ac_line, replies) => lifecycle.notify(state, ac_line, replies),
                SaveFullPlan => {
                    let outcome = lifecycle.notify(state, 1, CONFIRMATIONS)?;
                    superseded = lifecycle.planned_full;
                    Ok(outcome)
                }
                ApplySuperseded => lifecycle.apply(state, superseded, FULL, false),
                ApplyFull => lifecycle.apply(state, lifecycle.planned_full, FULL, lose_response),
                Reconcile => {
                    let outcome = PerformanceStep::Recover.run(&mut state.thermal()?.performance);
                    Ok(result(outcome.status, outcome.message))
                }
            })
            .await?;
        }
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn matches_exact_adapter_identity_within_five_attempts() {
        for (replies, expected, count) in [
            (&[FULL, OTHER_FULL, FULL][..], None, 2),
            (
                &[UNKNOWN, FULL, None, UNKNOWN, FULL][..],
                Some(Power::PluggedIn),
                5,
            ),
            (&[LIMITED, None, LIMITED][..], Some(Power::UsbC), 3),
            (&[FULL, None, None, UNKNOWN, None, FULL][..], None, 5),
            (&[Some([0, 0x11]); 2][..], None, 2),
            (&[Some([0x13, 0x11]); 2][..], Some(Power::PluggedIn), 2),
        ] {
            let mut calls = 0;
            let observed = resolve(1, || {
                calls += 1;
                replies.get(calls - 1).copied().flatten()
            })
            .unwrap();
            assert_eq!((observed, calls), (expected, count));
        }
        assert_eq!(
            resolve(0, || panic!("Windows reports battery")).unwrap(),
            Some(Power::Battery)
        );
        assert!(resolve(255, || panic!("invalid Windows sample")).is_err());
    }
}

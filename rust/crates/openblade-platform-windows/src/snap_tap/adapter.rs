//! Synchronous message-loop adapter for the fixed A/D policy.

use super::{
    Admission, Context, Failure, HookEvent, Output, OwnershipAdmission, Reason, SnapTap, Status,
};
use std::panic::{AssertUnwindSafe, catch_unwind};

#[cfg(windows)]
#[allow(dead_code)] // The supervised product entry point remains on the managed helper.
mod native;

pub(super) type Route<'a> = dyn FnMut(HookEvent) -> bool + 'a;

trait Input: Output {
    fn admission(&mut self) -> Admission<'_>;
    fn stop_requested(&self) -> bool;
    fn keyboard_speed(&self) -> Option<u32>;
    fn released(&self, context: Context) -> (bool, bool);
}

trait Messages {
    type Hook;
    type Message;

    fn install(&mut self) -> Result<Self::Hook, u32>;
    fn start_timer(&mut self) -> Result<usize, u32>;
    fn receive(&mut self, route: &mut Route<'_>) -> Result<Option<Self::Message>, u32>;
    fn timer_id(message: &Self::Message) -> Option<usize>;
    fn dispatch(&mut self, message: Self::Message, route: &mut Route<'_>);
    fn unhook(&mut self, hook: Self::Hook) -> Result<(), u32>;
    fn kill_timer(&mut self, timer: usize) -> Result<(), u32>;
}

#[derive(Debug, PartialEq, Eq)]
enum LoopFailure {
    Install(u32),
    TimerSetup(u32),
    Receive(u32),
    Unwind,
}

#[derive(Debug, PartialEq, Eq)]
struct Retirement {
    policy_status: Status,
    policy_failure: Option<Failure>,
    loop_failure: Option<LoopFailure>,
    unhook_error: Option<u32>,
    timer_error: Option<u32>,
}

struct Session<I> {
    input: I,
    state: SnapTap,
}

impl<I: Input> Session<I> {
    fn route(&mut self, event: HookEvent) -> bool {
        // Keep unrelated traffic out of the policy and out of foreground/session
        // sampling. The timer and admitted A/D events still check admission.
        let Some(stroke) = event.stroke() else {
            return false;
        };
        if event.flags & 0x12 != 0 || !self.state.live() {
            return false;
        }
        match catch_unwind(AssertUnwindSafe(|| {
            let observation = self.input.observe();
            if observation.stop_requested {
                // GetMessage can deliver sent callbacks after cancellation.
                // A forwarded release ends ownership of that press. A later
                // forwarded down belongs to Windows, including at retirement.
                if let Some(winner) = self.state.winner() {
                    if !observation.active_console
                        || !self.state.context_matches(observation.context)
                    {
                        self.state.fail(Reason::HeldOutputUncertain, true);
                    } else if !stroke.down && stroke.key == winner {
                        self.state.winner = None;
                        self.state.repeat = None;
                    }
                }
                return false;
            }
            self.state
                .on_key(observation, Some(event), &mut self.input)
                .suppress
        })) {
            Ok(suppress) => suppress,
            Err(_) => {
                self.state.fail(Reason::HookCallbackFailed, false);
                false
            }
        }
    }

    fn drive<M: Messages>(&mut self, messages: &mut M, timer: usize) -> Result<(), LoopFailure> {
        while self.state.live() && !self.input.stop_requested() {
            let Some(message) = messages
                .receive(&mut |event| self.route(event))
                .map_err(LoopFailure::Receive)?
            else {
                break;
            };

            let observation = self.input.observe();
            if observation.stop_requested || !self.state.live() {
                break;
            }
            if self.state.refresh_due(observation.at) {
                self.state.refresh(observation.at, &self.input.admission());
            }
            if !self.state.live() {
                break;
            }

            let (global, mapped) = self.input.released(observation.context);
            self.state.reconcile(observation.context, global, mapped);
            if M::timer_id(&message) == Some(timer) {
                self.state.repeat(observation.at, false, &mut self.input);
            }
            // Windows sees the original retrieved message after policy processing.
            messages.dispatch(message, &mut |event| self.route(event));
        }
        Ok(())
    }

    fn stop(&mut self) {
        if !self.state.live() {
            return;
        }
        // No callback lease survives receive/dispatch. Resample admission before
        // the one possible owned release, even if the last snapshot is young.
        let mut observation = self.input.observe();
        if self.state.winner().is_some() {
            observation.active_console &= self.input.admission().rejection().is_none();
        }
        self.state.stop(observation, &mut self.input);
    }
}

fn run<I: Input, M: Messages>(
    input: I,
    mut messages: M,
    ownership: OwnershipAdmission,
) -> Result<Option<Retirement>, Reason> {
    let mut input = input;
    let observation = input.observe();
    let speed = input.keyboard_speed();
    let Some(state) = SnapTap::start(
        observation.at,
        &input.admission(),
        speed,
        observation.stop_requested,
        ownership,
    )?
    else {
        return Ok(None);
    };

    if input.stop_requested() {
        return Ok(None);
    }

    let mut session = Session { input, state };
    let (mut hook, mut timer) = (None, None);
    let mut loop_failure = catch_unwind(AssertUnwindSafe(|| {
        hook = Some(messages.install().map_err(LoopFailure::Install)?);
        let id = messages.start_timer().map_err(LoopFailure::TimerSetup)?;
        timer = Some(id);
        session.drive(&mut messages, id)
    }))
    .unwrap_or(Err(LoopFailure::Unwind))
    .err();

    if loop_failure.is_some() {
        session.state.fail(Reason::HookLoopFailed, false);
    } else if catch_unwind(AssertUnwindSafe(|| session.stop())).is_err() {
        session.state.fail(Reason::OutputAdmissionRejected, false);
        loop_failure.get_or_insert(LoopFailure::Unwind);
    }

    // Neither teardown failure can skip the other attempt. An Off policy
    // status alone says nothing about hook or timer retirement.
    let unhook_error = hook.and_then(|hook| messages.unhook(hook).err());
    let timer_error = timer.and_then(|timer| messages.kill_timer(timer).err());
    Ok(Some(Retirement {
        policy_status: session.state.status(),
        policy_failure: session.state.failure(),
        loop_failure,
        unhook_error,
        timer_error,
    }))
}

#[cfg(test)]
mod tests;

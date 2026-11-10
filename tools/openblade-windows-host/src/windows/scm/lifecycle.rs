use std::{
    io,
    sync::{Arc, Mutex, MutexGuard, Weak},
    time::{Duration, Instant},
};

use tokio::sync::{Notify, oneshot};
use windows_sys::Win32::{
    System::Services::{SERVICE_CONTROL_POWEREVENT, SERVICE_CONTROL_SESSIONCHANGE},
    UI::WindowsAndMessaging::{
        PBT_APMPOWERSTATUSCHANGE, PBT_APMRESUMEAUTOMATIC, PBT_APMRESUMESUSPEND, PBT_APMSUSPEND,
        PBT_POWERSETTINGCHANGE, WTS_CONSOLE_CONNECT, WTS_CONSOLE_DISCONNECT, WTS_REMOTE_CONNECT,
        WTS_REMOTE_DISCONNECT, WTS_SESSION_LOCK, WTS_SESSION_LOGOFF, WTS_SESSION_LOGON,
        WTS_SESSION_UNLOCK,
    },
};

const DUPLICATE_RESUME_WINDOW: Duration = Duration::from_secs(5);

#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub(super) enum Activity {
    #[default]
    Unknown,
    Suspended,
    Active,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(super) enum PowerSource {
    Battery,
    ExternalUnknown,
    Unknown,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(super) struct Observation {
    pub(super) power: PowerSource,
    pub(super) console_session: Option<u32>,
}

impl Observation {
    fn from_windows(ac_line_status: Option<u8>, console_session: u32) -> Self {
        Self {
            power: match ac_line_status {
                Some(0) => PowerSource::Battery,
                Some(1) => PowerSource::ExternalUnknown,
                _ => PowerSource::Unknown,
            },
            console_session: (console_session != u32::MAX).then_some(console_session),
        }
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(super) enum Work {
    Invalidate(Activity),
    // Current read-only evidence. Missing fields supersede earlier values.
    Refresh(Activity, Observation),
    Stop,
}

#[derive(Clone, Copy)]
enum PowerSetting {
    Source,
    Display(u32),
}

#[derive(Default)]
struct Pending {
    invalidate: bool,
    refresh: bool,
}

struct State {
    accepting: bool,
    activity: Activity,
    display_on: Option<bool>,
    last_resume: Option<Instant>,
    pending: Pending,
    #[cfg(test)]
    metadata_current: bool,
}

struct Shared {
    state: Mutex<State>,
    wake: Notify,
}

impl Shared {
    fn state(&self) -> MutexGuard<'_, State> {
        self.state.lock().unwrap_or_else(|error| error.into_inner())
    }
}

// The callback context can outlive the worker. It cannot retain the worker or
// put new work into a closed inbox, even if it upgraded just before STOP.
pub(super) struct Callbacks(Weak<Shared>);

impl Callbacks {
    #[cfg(test)]
    pub(super) fn fake_control(&self, code: u32, event: u32, now: Instant) {
        self.control(code, event, None, now);
    }

    #[cfg(test)]
    pub(super) fn metadata_current(&self) -> bool {
        self.0.upgrade().is_some_and(|shared| {
            let state = shared.state();
            state.accepting && state.metadata_current
        })
    }

    #[cfg(test)]
    pub(super) fn publish_metadata(&self, verified: bool, publish: impl FnOnce(bool)) -> bool {
        let Some(shared) = self.0.upgrade() else {
            return false;
        };
        let mut state = shared.state();
        // A callback can arrive inside acquisition after the worker's last
        // pending check. Commit under the same lock that invalidates evidence.
        let current = state.accepting && !state.pending.invalidate && !state.pending.refresh;
        state.metadata_current = current && verified;
        publish(state.metadata_current);
        current
    }

    fn control(&self, code: u32, event: u32, setting: Option<PowerSetting>, now: Instant) {
        let Some(shared) = self.0.upgrade() else {
            return;
        };
        let mut state = shared.state();
        if !state.accepting {
            return;
        }

        match (code, event, setting) {
            (SERVICE_CONTROL_POWEREVENT, PBT_APMSUSPEND, _) => {
                state.activity = Activity::Suspended;
                state.last_resume = None;
                state.pending.invalidate = true;
            }
            (SERVICE_CONTROL_POWEREVENT, PBT_APMRESUMESUSPEND | PBT_APMRESUMEAUTOMATIC, _) => {
                state.activity = Activity::Active;
                if state.last_resume.is_some_and(|last| {
                    now.checked_duration_since(last)
                        .is_some_and(|elapsed| elapsed <= DUPLICATE_RESUME_WINDOW)
                }) {
                    return;
                }
                state.last_resume = Some(now);
                state.pending.refresh = true;
            }
            (
                SERVICE_CONTROL_POWEREVENT,
                PBT_POWERSETTINGCHANGE,
                Some(PowerSetting::Display(level)),
            ) if level <= 2 => {
                let was_on = state.display_on.replace(level != 0);
                // Display-off alone is not sleep. An observed off -> on/dim
                // transition establishes activity and can recover a missed resume.
                if was_on != Some(false) || level == 0 || state.activity == Activity::Active {
                    return;
                }
                state.activity = Activity::Active;
                state.pending.refresh = true;
            }
            (SERVICE_CONTROL_POWEREVENT, PBT_APMPOWERSTATUSCHANGE, _)
            | (SERVICE_CONTROL_POWEREVENT, PBT_POWERSETTINGCHANGE, Some(PowerSetting::Source)) => {
                if state.activity == Activity::Suspended {
                    return;
                }
                state.pending.refresh = true;
            }
            (
                SERVICE_CONTROL_SESSIONCHANGE,
                WTS_CONSOLE_CONNECT
                | WTS_CONSOLE_DISCONNECT
                | WTS_REMOTE_CONNECT
                | WTS_REMOTE_DISCONNECT
                | WTS_SESSION_LOGON
                | WTS_SESSION_LOGOFF
                | WTS_SESSION_LOCK
                | WTS_SESSION_UNLOCK,
                _,
            ) => {
                // Never use the notification's session ID as console evidence.
                state.pending.invalidate = true;
                state.pending.refresh = true;
            }
            _ => return,
        }
        #[cfg(test)]
        {
            state.metadata_current = false;
        }
        drop(state);
        shared.wake.notify_one();
    }
}

pub(super) struct Worker(Arc<Shared>);

impl Worker {
    pub(super) fn new() -> (Self, Callbacks) {
        let shared = Arc::new(Shared {
            state: Mutex::new(State {
                accepting: true,
                activity: Activity::Unknown,
                display_on: None,
                last_resume: None,
                pending: Pending {
                    refresh: true,
                    ..Pending::default()
                },
                #[cfg(test)]
                metadata_current: false,
            }),
            wake: Notify::new(),
        });
        shared.wake.notify_one();
        let callbacks = Callbacks(Arc::downgrade(&shared));
        (Self(shared), callbacks)
    }

    pub(super) async fn run(
        self,
        mut stop: oneshot::Receiver<()>,
        mut observe: impl FnMut() -> Observation,
        mut apply: impl AsyncFnMut(Work) -> io::Result<()>,
    ) -> io::Result<()> {
        let result = self.refresh(&mut stop, &mut observe, &mut apply).await;
        self.0.state().accepting = false;
        // Await the same sink's retirement, including when refresh failed.
        apply(Work::Stop).await.and(result)
    }

    async fn refresh(
        &self,
        stop: &mut oneshot::Receiver<()>,
        observe: &mut impl FnMut() -> Observation,
        apply: &mut impl AsyncFnMut(Work) -> io::Result<()>,
    ) -> io::Result<()> {
        while let Some((activity, pending)) = self.next(stop).await {
            if pending.invalidate {
                apply(Work::Invalidate(activity)).await?;
            }
            if !pending.refresh || activity == Activity::Suspended || self.has_pending() {
                continue;
            }
            if stop.try_recv() != Err(oneshot::error::TryRecvError::Empty) {
                return Ok(());
            }
            let evidence = observe();
            // New evidence requested during this read takes precedence.
            if self.has_pending() {
                continue;
            }
            if stop.try_recv() != Err(oneshot::error::TryRecvError::Empty) {
                return Ok(());
            }
            apply(Work::Refresh(activity, evidence)).await?;
        }
        Ok(())
    }

    fn has_pending(&self) -> bool {
        let state = self.0.state();
        state.pending.invalidate || state.pending.refresh
    }

    async fn next(&self, stop: &mut oneshot::Receiver<()>) -> Option<(Activity, Pending)> {
        tokio::select! {
            biased;
            _ = stop => None,
            () = self.0.wake.notified() => {
                let mut state = self.0.state();
                Some((state.activity, std::mem::take(&mut state.pending)))
            }
        }
    }
}

impl Drop for Worker {
    fn drop(&mut self) {
        // A callback can hold a temporary strong reference when the worker drops.
        self.0.state().accepting = false;
    }
}

#[cfg(all(not(test), feature = "lifecycle-evidence-compile-check"))]
pub(super) mod native;

#[cfg(test)]
mod tests;

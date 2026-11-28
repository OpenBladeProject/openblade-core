//! The hook lifetime: when to install, when to retire, and the proof required
//! before installing again.
//!
//! The hook is installed when the per-user preference selects WindowsHook, no
//! Razer-driver journal may own the function row, the Agent's session is the
//! active console and a fresh status names RZ09-0581. A later unverified or
//! unknown status only makes the hook pass keys through, as managed keeps its
//! hook installed. A preference change or stop retires it: the admission first
//! drops the backend, the hook drains its held keys for at most 250 ms, unhooks,
//! and the worker resets. Another hook is installed only after the previous run
//! retired cleanly (`HookControl::retired_cleanly`); an unclean run leaves
//! ownership uncertain and this supervisor installs no other hook. Nothing
//! latches that across role runs; the in-process hook slot still refuses a
//! second hook while one holds it.
use std::{
    io,
    sync::Arc,
    thread::{self, JoinHandle},
    time::{Duration, Instant},
};

use openblade_domain::function_row::Backend;
use openblade_platform_windows::{
    agent_diagnostics::{Event, FunctionRowNotice},
    function_row_hook::{FunctionRowHook, HookControl, HookError, StartupAdmission},
};

use super::{
    admission::Admission,
    effects::StageEffects,
    ownership::{Guard, Journal},
    preference::Preference,
};

/// The wait before another install attempt after a refusal or a clean run.
pub(crate) const RETRY: Duration = Duration::from_secs(1);

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) struct Retirement {
    pub(crate) clean: bool,
    pub(crate) error: Option<HookError>,
}

pub(crate) trait Running {
    /// Stop admitting actions and retire (`HookControl::cancel`). Held releases
    /// and the worker's final reset still run.
    fn stop(&self);
    fn finished(&self) -> bool;
    fn join(self) -> Retirement;
}

pub(crate) trait Hooks {
    type Running: Running;
    /// Start one hook run. Its control is attached to `admission` before the
    /// hook reads its startup snapshot.
    fn start(
        &mut self,
        startup: StartupAdmission,
        admission: &Admission,
    ) -> io::Result<Self::Running>;
}

pub(crate) struct WindowsHooks {
    record: fn(Event),
}

impl WindowsHooks {
    pub(crate) fn new(record: fn(Event)) -> Self {
        Self { record }
    }
}

pub(crate) struct WindowsRunning {
    control: HookControl,
    hook: JoinHandle<Result<(), HookError>>,
    worker: JoinHandle<()>,
}

impl Hooks for WindowsHooks {
    type Running = WindowsRunning;

    fn start(
        &mut self,
        startup: StartupAdmission,
        admission: &Admission,
    ) -> io::Result<WindowsRunning> {
        let (hook, control, worker) = FunctionRowHook::with_executor();
        admission.attach(Box::new(control.clone()));
        let record = self.record;
        let worker = thread::Builder::new()
            .name("OpenBlade function-row actions".into())
            .spawn(move || {
                // The report's failures are already sticky in the control.
                let _ = worker.run(&mut StageEffects::new(record));
            })
            .inspect_err(|_| admission.detach())?;
        let hook = match thread::Builder::new()
            .name("OpenBlade function-row hook".into())
            .spawn(move || hook.run(startup))
        {
            Ok(hook) => hook,
            Err(error) => {
                // The unstarted hook was dropped with its sender, so the worker ends.
                admission.detach();
                let _ = worker.join();
                return Err(error);
            }
        };
        Ok(WindowsRunning {
            control,
            hook,
            worker,
        })
    }
}

impl Running for WindowsRunning {
    fn stop(&self) {
        self.control.cancel();
    }

    fn finished(&self) -> bool {
        self.hook.is_finished() && self.worker.is_finished()
    }

    fn join(self) -> Retirement {
        let hook = self.hook.join();
        let worker = self.worker.join();
        Retirement {
            clean: hook.is_ok() && worker.is_ok() && self.control.retired_cleanly(),
            error: hook.ok().and_then(Result::err),
        }
    }
}

enum Phase<R> {
    Idle,
    Running {
        hook: R,
        stopping: bool,
    },
    /// A run left ownership uncertain or failed natively: never install again.
    Blocked,
}

pub(crate) struct Supervisor<H: Hooks, P, G, L> {
    hooks: H,
    preference: P,
    guard: G,
    local_session: L,
    admission: Arc<Admission>,
    record: fn(Event),
    phase: Phase<H::Running>,
    retry_at: Option<Instant>,
    previous_retired: bool,
    journal_noted: Option<Journal>,
}

impl<H, P, G, L> Supervisor<H, P, G, L>
where
    H: Hooks,
    P: Preference,
    G: Guard,
    L: Fn() -> bool,
{
    pub(crate) fn new(
        hooks: H,
        preference: P,
        guard: G,
        local_session: L,
        admission: Arc<Admission>,
        record: fn(Event),
    ) -> Self {
        Self {
            hooks,
            preference,
            guard,
            local_session,
            admission,
            record,
            phase: Phase::Idle,
            retry_at: None,
            // No earlier run of this Agent holds the hook slot.
            previous_retired: true,
            journal_noted: None,
        }
    }

    /// One bounded step. Returns true once `stop` is set and no hook is left.
    pub(crate) fn step(&mut self, now: Instant, stop: bool) -> io::Result<bool> {
        let wanted = !stop && self.preference.poll(now) == Some(Backend::WindowsHook);
        match &mut self.phase {
            Phase::Running { hook, stopping } => {
                if !wanted && !*stopping {
                    // Pass-through before the drain, so no new press is admitted.
                    self.admission.select(None);
                    hook.stop();
                    *stopping = true;
                }
                if hook.finished() {
                    let Phase::Running { hook, .. } =
                        std::mem::replace(&mut self.phase, Phase::Idle)
                    else {
                        unreachable!("matched above");
                    };
                    self.retire(hook.join(), now);
                }
            }
            Phase::Idle
                if wanted
                    && self.retry_at.is_none_or(|retry| now >= retry)
                    && self.admission.admits_install(now) =>
            {
                self.install(now)?;
            }
            Phase::Idle | Phase::Blocked => {}
        }
        Ok(stop && !matches!(self.phase, Phase::Running { .. }))
    }

    fn install(&mut self, now: Instant) -> io::Result<()> {
        // Fresh evidence for every install, never a cached startup result.
        let journal = self.guard.inspect();
        if journal != Journal::Clear || !(self.local_session)() {
            if journal != Journal::Clear && self.journal_noted != Some(journal) {
                (self.record)(Event::FunctionRow(match journal {
                    Journal::Present => FunctionRowNotice::DriverOwnershipJournalPresent,
                    _ => FunctionRowNotice::DriverOwnershipUnobservable,
                }));
            }
            self.journal_noted = Some(journal);
            self.retry_at = Some(now + RETRY);
            return Ok(());
        }
        self.journal_noted = None;
        self.admission.select(Some(Backend::WindowsHook));
        let startup = StartupAdmission {
            requested_backend: Some(Backend::WindowsHook),
            local_session_confirmed: true,
            startup_recovery_clear: true,
            // The native Agent has no Razer-driver coordinator of its own; a
            // driver helper's ownership is the journal checked above.
            driver_can_transfer: true,
            previous_windows_hook_retired: self.previous_retired,
        };
        match self.hooks.start(startup, &self.admission) {
            Ok(hook) => {
                self.phase = Phase::Running {
                    hook,
                    stopping: false,
                };
                Ok(())
            }
            Err(error) => {
                self.admission.select(None);
                self.admission.detach();
                Err(error)
            }
        }
    }

    fn retire(&mut self, retirement: Retirement, now: Instant) {
        self.admission.detach();
        self.admission.select(None);
        (self.record)(Event::FunctionRow(FunctionRowNotice::HookRetired {
            clean: retirement.clean,
            error: retirement.error,
        }));
        self.previous_retired = retirement.clean;
        // A stop or a refused start may be retried; a native failure is not.
        let benign = matches!(
            retirement.error,
            None | Some(HookError::StartupRefused | HookError::Cancelled)
        );
        self.phase = if retirement.clean && benign {
            Phase::Idle
        } else {
            Phase::Blocked
        };
        self.retry_at = Some(now + RETRY);
    }
}

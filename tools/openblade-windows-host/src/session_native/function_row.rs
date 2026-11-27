//! Selected installed function-row role: the Windows-hook backend on exact
//! RZ09-0581, driven by the Service's confirmed keyboard Driver mode (managed
//! `WindowsReactiveKeyboardSource` with `FunctionRowKeyboardHookProcessor`).
//! AGENTS.md architecture rule 1: the Service alone owns Driver mode and the
//! Agent consumes its confirmed status only. Any unknown status passes every
//! key through.
//!
//! This stage issues no Service command. Layers:
//! - `admission` turns verified, fresh status into the hook's single expiring
//!   observation and invalidates it on reconnect, staleness or stop.
//! - `preference` reads the per-user backend choice (managed
//!   `SpecialKeyBindingSource.FunctionRowBackend`).
//! - `ownership` refuses installation while a Razer-driver journal may still
//!   own the function row (managed `WindowsFunctionRowOwnershipGuard`).
//! - `effects` maps every admitted action to a no-op with a diagnostic.
//! - `host` owns the hook and worker threads and their retirement.
//!
//! The renewal tick runs on the Agent's shared runtime, beside the status
//! subscription whose idle checks it stands in for. The supervisor runs on its
//! own thread because it reads files and joins threads.
use super::feature_lifetime::stopped;
use std::{
    io,
    sync::{
        Arc,
        atomic::{AtomicBool, Ordering::SeqCst},
        mpsc::{Receiver, SyncSender, sync_channel},
    },
    thread,
    time::{Duration, Instant},
};

use openblade_platform_windows::agent_diagnostics::record;
use tokio::{
    sync::{oneshot, watch},
    time::MissedTickBehavior,
};

mod admission;
mod effects;
mod host;
mod ownership;
mod preference;

#[cfg_attr(test, allow(unused_imports))]
pub(crate) use admission::{Admission, publish_status};
use host::{Hooks, Supervisor, WindowsHooks};
use ownership::{Guard, JournalGuard};
use preference::{FilePreference, Preference};

/// Managed `UiPreferenceStore.DefaultPath` and the driver journal directory.
const DIRECTORY: &str = "OpenBlade";
const PREFERENCES: &str = "preferences.json";

#[derive(Clone, Copy)]
struct Timing {
    /// The admission lease is renewed this often while the status subscription
    /// stays open; `admission::LEASE` bounds a missed renewal.
    renew: Duration,
    /// The supervisor's poll while it has nothing to wait for.
    tick: Duration,
    /// The longest stop waits for the hook and worker threads to retire: the
    /// hook's 250 ms drain, the unhook and the worker reset.
    retire: Duration,
}

impl Timing {
    const INSTALLED: Self = Self {
        renew: Duration::from_millis(500),
        tick: Duration::from_millis(100),
        retire: Duration::from_secs(2),
    };
}

/// The installed role. Its supervisor runs on a dedicated thread; the hook and
/// its action worker run on two more, joined before this returns.
pub(super) async fn run(
    origin: Arc<crate::loaded_origin::Origin>,
    stop: watch::Receiver<bool>,
    admission: Arc<Admission>,
) -> io::Result<()> {
    super::verify_origin(&origin)?;
    let directory = super::user_environment::local_app_data()?.join(DIRECTORY);
    let session = origin.session;
    let supervisor = Supervisor::new(
        WindowsHooks::new(record),
        FilePreference::new(directory.join(PREFERENCES), record),
        JournalGuard::new(directory),
        move || super::user_environment::active_console(session),
        Arc::clone(&admission),
        record,
    );
    serve(
        supervisor,
        admission,
        stop,
        || super::verify_origin(&origin),
        Timing::INSTALLED,
    )
    .await
}

/// Stop and wake for the supervisor thread.
struct Signals {
    stop: AtomicBool,
    wake: SyncSender<()>,
}

impl Signals {
    /// Pass-through first, then retire the hook: every key passes before the
    /// supervisor can see the stop and start the drain.
    fn retire(&self, admission: &Admission) {
        admission.invalidate();
        self.stop.store(true, SeqCst);
        let _ = self.wake.try_send(());
    }
}

/// Invalidates, then retires the supervisor, on every exit, including a
/// dropped future.
struct Retire(Arc<Signals>, Arc<Admission>);

impl Drop for Retire {
    fn drop(&mut self) {
        self.0.retire(&self.1);
    }
}

async fn serve<H, P, G, L>(
    supervisor: Supervisor<H, P, G, L>,
    admission: Arc<Admission>,
    mut stop: watch::Receiver<bool>,
    verify: impl Fn() -> io::Result<()>,
    timing: Timing,
) -> io::Result<()>
where
    H: Hooks + Send + 'static,
    H::Running: Send,
    P: Preference + Send + 'static,
    G: Guard + Send + 'static,
    L: Fn() -> bool + Send + 'static,
{
    let (wake, wakes) = sync_channel(1);
    let signals = Arc::new(Signals {
        stop: AtomicBool::new(false),
        wake,
    });
    let _retire = Retire(Arc::clone(&signals), Arc::clone(&admission));
    let (exit_tx, mut exit) = oneshot::channel();
    let thread_signals = Arc::clone(&signals);
    let thread = thread::Builder::new()
        .name("OpenBlade function-row supervisor".into())
        .spawn(move || {
            let result = supervise(supervisor, &thread_signals, &wakes, timing.tick);
            let _ = exit_tx.send(result);
        })?;

    let mut renew = tokio::time::interval(timing.renew);
    // Windows `Instant` keeps counting through sleep. A burst would replay every
    // tick missed during a suspend back to back on the Agent's shared runtime;
    // one renewal after resume re-verifies just as well.
    renew.set_missed_tick_behavior(MissedTickBehavior::Delay);
    let mut finished = None;
    let result = loop {
        tokio::select! {
            biased;
            () = stopped(&mut stop) => break Ok(()),
            owner = &mut exit => {
                // The supervisor ends only when asked; anything else is a failure.
                finished = Some(match owner {
                    Ok(Err(error)) => Err(error),
                    _ => Err(io::Error::other("function-row supervisor stopped unexpectedly")),
                });
                break Ok(());
            }
            _ = renew.tick() => {
                if let Err(error) = verify() {
                    break Err(error);
                }
                admission.renew(Instant::now());
            }
        }
    };
    signals.retire(&admission);
    let joined = match finished {
        Some(result) => result,
        None => match tokio::time::timeout(timing.retire, exit).await {
            Ok(Ok(result)) => result,
            Ok(Err(_)) => Err(io::Error::other("function-row supervisor panicked")),
            // The threads retire themselves when their OS calls return; the
            // Agent must not wait on them beyond the bound.
            Err(_) => {
                return result.and(Err(io::Error::new(
                    io::ErrorKind::TimedOut,
                    "function-row hook did not retire",
                )));
            }
        },
    };
    thread
        .join()
        .map_err(|_| io::Error::other("function-row supervisor panicked"))?;
    result.and(joined)
}

/// The supervisor loop. Each step is bounded; it waits for a wake or a tick.
fn supervise<H, P, G, L>(
    mut supervisor: Supervisor<H, P, G, L>,
    signals: &Signals,
    wakes: &Receiver<()>,
    tick: Duration,
) -> io::Result<()>
where
    H: Hooks,
    P: Preference,
    G: Guard,
    L: Fn() -> bool,
{
    loop {
        let stopping = signals.stop.load(SeqCst);
        if supervisor.step(Instant::now(), stopping)? {
            return Ok(());
        }
        let _ = wakes.recv_timeout(tick);
    }
}

#[cfg(test)]
mod tests;

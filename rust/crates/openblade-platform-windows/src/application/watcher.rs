//! Opt-in foreground lifecycle. The callback thread only records evidence.
//! The owner drives resolution, reporting time, and typed transport.
mod native;

#[cfg(any(test, feature = "foreground-watcher-replay"))]
pub mod replay;

use std::{
    io,
    num::NonZeroU32,
    sync::{
        Arc, Mutex,
        atomic::{AtomicBool, Ordering},
        mpsc::{Receiver, RecvTimeoutError, sync_channel},
    },
    thread::{self, JoinHandle},
    time::{Duration, Instant},
};

use openblade_application_reporter::{
    ApplicationProfile, Guid, ReportApplicationContextRequest, ReportOutcome,
};

use super::{Binding, Desktop, native::Windows, resolve};

type Result<T> = io::Result<T>;

#[derive(Clone, Copy)]
enum Notice {
    SampleCurrent,
    Window(usize),
}

#[derive(Clone, Copy)]
struct Latest {
    revision: u64,
    desktop_epoch: u64,
    notice: Notice,
    closed: bool,
}

/// Called after a notice or the worker's exit is recorded, outside the mailbox
/// lock. It must not block or call into the OS: it runs on the hook callback.
pub type Wake = Arc<dyn Fn() + Send + Sync>;

#[derive(Clone)]
struct Callbacks(Arc<Mutex<Latest>>, Arc<Mutex<Option<Wake>>>);

impl Callbacks {
    fn new() -> Self {
        Self(
            Arc::new(Mutex::new(Latest {
                revision: 1,
                desktop_epoch: 0,
                notice: Notice::SampleCurrent,
                closed: false,
            })),
            Arc::default(),
        )
    }

    fn wake(&self) {
        let wake = self
            .1
            .lock()
            .unwrap_or_else(|error| error.into_inner())
            .clone();
        if let Some(wake) = wake {
            wake();
        }
    }

    fn record(&self, notice: Notice) {
        let mut latest = self.0.lock().unwrap_or_else(|error| error.into_inner());
        latest.revision = latest.revision.wrapping_add(1);
        if matches!(notice, Notice::SampleCurrent) {
            latest.desktop_epoch = latest.desktop_epoch.wrapping_add(1);
        }
        latest.notice = notice;
        drop(latest);
        self.wake();
    }
}

struct WorkerExit(Callbacks);

impl Drop for WorkerExit {
    fn drop(&mut self) {
        self.0
            .0
            .lock()
            .unwrap_or_else(|error| error.into_inner())
            .closed = true;
        self.0.wake();
    }
}

trait Registration {
    // Return after a bounded wait or one message so cancellation is observable.
    fn pump(&mut self) -> Result<bool>;
}

struct Worker {
    callbacks: Callbacks,
    started: Receiver<io::Result<()>>,
    handle: JoinHandle<Result<()>>,
}

/// Native registration is explicit and never runs from workspace defaults.
/// A current-user owner must wait for readiness, poll regularly, and join
/// shutdown. The owner is also responsible for retiring outstanding sends.
pub type WindowsForegroundWatcher = ForegroundWatcher<Windows>;

#[allow(private_bounds)]
pub struct ForegroundWatcher<A: Desktop> {
    binding: Binding<A>,
    worker: Option<Worker>,
    ready: bool,
    consumed: u64,
    window: usize,
    desktop_epoch: u64,
    session: Option<NonZeroU32>,
    suspended: bool,
}

impl WindowsForegroundWatcher {
    pub fn start(version: i32, instance: Guid, cancellation: Arc<AtomicBool>) -> Result<Self> {
        Self::spawn(
            Windows,
            version,
            instance,
            cancellation,
            native::Hooks::register,
        )
    }
}

// Desktop is an internal seam, including for the optional scripted runner.
#[allow(private_bounds)]
impl<A: Desktop> ForegroundWatcher<A> {
    fn spawn<R: Registration>(
        api: A,
        version: i32,
        instance: Guid,
        cancellation: Arc<AtomicBool>,
        register: impl FnOnce(Callbacks, &AtomicBool) -> Result<R> + Send + 'static,
    ) -> Result<Self> {
        let callbacks = Callbacks::new();
        let exit = WorkerExit(callbacks.clone());
        let worker_callbacks = callbacks.clone();
        let (ready, started) = sync_channel(1);
        let stop = Arc::clone(&cancellation);
        let handle = thread::Builder::new()
            .name("OpenBlade foreground".into())
            .spawn(move || {
                if stop.load(Ordering::Acquire) {
                    return Ok(());
                }
                let mut registration = match register(worker_callbacks, &stop) {
                    Ok(value) => value,
                    Err(error) => {
                        let _ = ready.send(Err(error));
                        return Ok(());
                    }
                };
                let _exit = exit;
                if stop.load(Ordering::Acquire) || ready.send(Ok(())).is_err() {
                    return Ok(());
                }
                while !stop.load(Ordering::Acquire) && registration.pump()? {}
                Ok(())
            })?;
        Ok(Self {
            binding: Binding::new(api, version, instance, cancellation),
            worker: Some(Worker {
                callbacks,
                started,
                handle,
            }),
            ready: false,
            consumed: 0,
            window: 0,
            desktop_epoch: 0,
            session: None,
            suspended: false,
        })
    }

    /// A failed or timed-out startup requests cancellation immediately. This
    /// method never joins a thread still inside a native registration call.
    pub fn wait_ready(&mut self, timeout: Duration) -> Result<()> {
        let start = Instant::now();
        while self.binding.running() {
            if self.ready {
                return Ok(());
            }
            let remaining = timeout.saturating_sub(start.elapsed());
            let worker = self.worker.as_ref().expect("owned worker");
            match worker
                .started
                .recv_timeout(remaining.min(Duration::from_millis(10)))
            {
                Ok(Ok(())) if self.binding.running() => {
                    self.ready = true;
                    return Ok(());
                }
                Ok(Err(error)) => {
                    self.stop();
                    return Err(error);
                }
                Ok(Ok(())) => break,
                Err(RecvTimeoutError::Timeout) if start.elapsed() < timeout => continue,
                Err(error) => {
                    let kind = if !self.binding.running() {
                        io::ErrorKind::Interrupted
                    } else if error == RecvTimeoutError::Timeout {
                        io::ErrorKind::TimedOut
                    } else {
                        io::ErrorKind::BrokenPipe
                    };
                    self.stop();
                    return Err(io::Error::new(kind, "foreground worker did not start"));
                }
            }
        }
        self.stop();
        Err(io::Error::new(
            io::ErrorKind::Interrupted,
            "foreground startup cancelled",
        ))
    }

    /// Call `wake` after every foreground or desktop notice and when the
    /// registration thread ends, so the owner can wait for them instead of
    /// polling. It runs on the hook callback: it must not block or use the OS.
    pub fn set_notice(&mut self, wake: Wake) {
        if let Some(worker) = &self.worker {
            *worker
                .callbacks
                .1
                .lock()
                .unwrap_or_else(|error| error.into_inner()) = Some(wake);
        }
    }

    pub fn observe_profiles(&mut self, profiles: Vec<ApplicationProfile>) {
        if self.binding.running() {
            self.binding.policy.observe_profiles(profiles);
        }
    }

    pub fn complete(&mut self, sequence: i64, outcome: ReportOutcome) {
        if self.binding.running() {
            self.binding.policy.complete(sequence, outcome);
        }
    }

    pub fn disconnected(&mut self) {
        if self.binding.running() {
            self.binding.policy.disconnected();
        }
    }

    pub fn reconnected(&mut self) {
        if self.binding.running() {
            self.binding.policy.reconnected();
        }
    }

    /// Drive this from the current-user owner at a bounded cadence, including
    /// when there are no callbacks. While the session is locked or on another
    /// desktop the foreground is unknown, but retained processes stay and the
    /// lease keeps renewing, as managed does. Only a different session forgets
    /// every process.
    pub fn poll(
        &mut self,
        elapsed: Duration,
        request_id: Guid,
    ) -> Option<ReportApplicationContextRequest> {
        if !self.ready || !self.binding.running() {
            return None;
        }
        let callbacks = self.worker.as_ref()?.callbacks.clone();
        let read = || {
            callbacks
                .0
                .lock()
                .unwrap_or_else(|error| error.into_inner())
        };
        let snapshot = *read();
        if snapshot.closed {
            self.stop();
            return None;
        }

        // The active session is always this process's own. It is unavailable
        // while the console is locked, on the secure desktop, or on another
        // user, and none of those ends the user's processes. Managed keeps
        // retained processes and keeps renewing through them, so only the
        // foreground becomes unknown. A different session id is a different
        // user and forgets everything.
        let active = self.binding.api.active_local_session();
        let mut reset = snapshot.desktop_epoch != self.desktop_epoch || self.suspended;
        self.desktop_epoch = snapshot.desktop_epoch;
        if let Some(now) = active {
            if self.session.is_some_and(|before| before != now) {
                self.binding.policy.forget_process_context();
                reset = true;
            }
            self.session = Some(now);
        }
        self.suspended = active.is_none();
        if reset || active.is_none() {
            self.forget_foreground();
        }
        self.binding.prune_foreground();
        self.binding.policy.prune_exited_processes();

        let changed = active.is_some() && (snapshot.revision != self.consumed || reset);
        let window = if changed {
            match snapshot.notice {
                Notice::Window(window) if !reset => window,
                _ => self.binding.api.foreground(),
            }
        } else {
            self.window
        };
        let sample = if changed {
            let sample = resolve(&mut self.binding.api, window, &self.binding.cancellation);
            if self.binding.api.active_local_session() != active {
                // Lost while resolving: the next poll takes the inactive path.
                self.suspended = true;
                self.forget_foreground();
                return None;
            }
            sample
        } else {
            None
        };
        if active.is_some() && self.binding.api.foreground() != window {
            // A stale HWND cannot produce even an unknown observation. Also
            // suppress renewals while a newer window's callback is in transit.
            return None;
        }

        // Serialize admission with callback delivery, never holding this
        // lock while resolving a window or querying the desktop/session.
        let current = read();
        if current.closed {
            self.stop();
            return None;
        }
        if current.revision != snapshot.revision || !self.binding.running() {
            return None;
        }
        if changed {
            self.binding.accept_sample(sample);
            self.binding.prune_foreground();
            self.consumed = snapshot.revision;
            self.window = window;
        }
        self.binding.poll_policy(elapsed, request_id)
    }

    /// Foreground evidence is stale; retained processes stay.
    fn forget_foreground(&mut self) {
        self.binding.foreground = None;
        self.binding.policy.forget_foreground();
    }

    fn stop(&mut self) {
        self.binding.cancellation.store(true, Ordering::Release);
        self.binding.cancel();
    }

    /// Join the registration thread after retiring local process evidence.
    pub fn shutdown(mut self) -> Result<()> {
        self.stop();
        self.worker
            .take()
            .expect("owned worker")
            .handle
            .join()
            .map_err(|_| io::Error::other("foreground worker panicked"))?
    }
}

impl<A: Desktop> Drop for ForegroundWatcher<A> {
    fn drop(&mut self) {
        // Explicit shutdown is the joining path. Drop also requests cleanup
        // if startup is still inside the OS and cannot be joined promptly.
        self.binding.cancellation.store(true, Ordering::Release);
    }
}

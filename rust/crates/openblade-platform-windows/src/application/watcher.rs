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

#[derive(Clone)]
struct Callbacks(Arc<Mutex<Latest>>);

impl Callbacks {
    fn new() -> Self {
        Self(Arc::new(Mutex::new(Latest {
            revision: 1,
            desktop_epoch: 0,
            notice: Notice::SampleCurrent,
            closed: false,
        })))
    }

    fn record(&self, notice: Notice) {
        let mut latest = self.0.lock().unwrap_or_else(|error| error.into_inner());
        latest.revision = latest.revision.wrapping_add(1);
        if matches!(notice, Notice::SampleCurrent) {
            latest.desktop_epoch = latest.desktop_epoch.wrapping_add(1);
        }
        latest.notice = notice;
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
    /// when there are no callbacks. A disconnected session cannot renew or
    /// send a clear. The service lease can expire until authority is regained.
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

        let session = self.binding.api.active_local_session();
        let reset = session != self.session || snapshot.desktop_epoch != self.desktop_epoch;
        if reset {
            self.forget_authority(session, snapshot.desktop_epoch);
        }
        let session = session?;
        self.binding.prune_foreground();
        self.binding.policy.prune_exited_processes();

        let changed = snapshot.revision != self.consumed || reset;
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
            if self.binding.api.active_local_session() != Some(session) {
                self.forget_authority(None, snapshot.desktop_epoch);
                return None;
            }
            sample
        } else {
            None
        };
        if self.binding.api.foreground() != window {
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

    fn forget_authority(&mut self, session: Option<NonZeroU32>, desktop_epoch: u64) {
        self.session = session;
        self.desktop_epoch = desktop_epoch;
        self.binding.foreground = None;
        self.binding.policy.forget_process_context();
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

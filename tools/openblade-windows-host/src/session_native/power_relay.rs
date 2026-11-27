//! Selected session power relay (managed `SessionPowerTransitionRelay` and
//! `WindowsSessionPowerNotificationRegistration`). Windows calls back on its
//! own thread for each suspend and resume; the relay reports each direction
//! change to the Service as one `reportSessionPowerTransition` request: the
//! relay instance and its start, a sequence, the QPC timestamp and the kind.
//!
//! As in managed, only notification types 4 (suspend), 7 and 18 (resume)
//! count, a repeated direction is ignored, one report is in flight and only
//! the latest transition waits behind it. A newer transition cancels the
//! active report. Every transition has `BUDGET` from its timestamp: a suspend
//! callback waits at most that long for its report before Windows continues,
//! a resume callback returns at once, and no report is sent, or kept waiting
//! for its reply, past it. A report is never replayed.
//!
//! Unlike managed, the callback hands the report to the relay's own transport
//! thread instead of starting the pipe attempt itself, and each exchange first
//! proves the installed Service generation and pipe peer, as every native
//! Agent request does. The generation is established when the role starts, so
//! the first report does not spend its budget re-reading the package. The
//! managed callback also resets the special keys' function input; special
//! keys are not part of this role.
use super::feature_lifetime::stopped;
use std::{
    io,
    sync::{Arc, Condvar, Mutex, MutexGuard, PoisonError},
    time::Duration,
};

use openblade_platform_windows::agent_diagnostics::{
    Event, SessionPowerRelayNotice as Notice, record,
};
use tokio::{
    io::{AsyncRead, AsyncWrite},
    sync::{Notify, watch},
    time::{Instant, timeout},
};

use super::{
    scm_peer::WindowsInstalledServiceAuthority,
    service_client::{self, ReportChannel},
};
use crate::service_status::random_guid;

#[cfg(test)]
mod tests;
#[cfg(test)]
mod vectors;
mod windows;
mod wire;

/// Managed `SessionPowerTransition.MaximumAgeMilliseconds`.
const BUDGET: Duration = Duration::from_millis(1500);
/// `PBT_APMSUSPEND`.
const SUSPEND: u32 = 4;
/// `PBT_APMRESUMESUSPEND`.
const RESUME_SUSPEND: u32 = 7;
/// `PBT_APMRESUMEAUTOMATIC`.
const RESUME_AUTOMATIC: u32 = 18;

/// One direction change: its relay sequence, QPC timestamp and kind.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(super) struct Transition {
    pub(super) sequence: i64,
    pub(super) timestamp: i64,
    pub(super) suspending: bool,
}

/// The transition whose report the transport owns.
struct Active {
    transition: Transition,
    started: bool,
    canceled: bool,
}

impl Active {
    fn new(transition: Transition) -> Self {
        Self {
            transition,
            started: false,
            canceled: false,
        }
    }
}

#[derive(Default)]
struct State {
    last: Option<bool>,
    sequence: i64,
    active: Option<Active>,
    pending: Option<Transition>,
    disposed: bool,
}

impl State {
    fn active(&mut self, sequence: i64) -> Option<&mut Active> {
        self.active
            .as_mut()
            .filter(|active| active.transition.sequence == sequence)
    }

    /// Managed `Completion`: the report ended, was superseded while pending,
    /// expired, or the relay was disposed. An ended report leaves the active
    /// slot in the same step.
    fn complete(&mut self, sequence: i64) -> bool {
        self.disposed
            || (self.active(sequence).is_none()
                && self
                    .pending
                    .is_none_or(|pending| pending.sequence != sequence))
    }
}

/// What the transport does next.
#[derive(Debug, Eq, PartialEq)]
enum Next {
    Disposed,
    Idle,
    /// Canceled or out of budget before its send, as managed checks.
    Skip(Transition),
    Send(Transition),
}

/// The relay state shared by the Windows callback and the transport.
pub(super) struct Relay {
    started: i64,
    frequency: i64,
    clock: Box<dyn Fn() -> i64 + Send + Sync>,
    state: Mutex<State>,
    completed: Condvar,
    changed: Notify,
}

impl Relay {
    /// A relay whose instance started at QPC `started`, reading QPC from
    /// `clock` at `frequency` counts per second.
    pub(super) fn new(
        started: i64,
        frequency: i64,
        clock: Box<dyn Fn() -> i64 + Send + Sync>,
    ) -> io::Result<Self> {
        if started <= 0 || frequency <= 0 {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "invalid session power relay clock",
            ));
        }
        Ok(Self {
            started,
            frequency,
            clock,
            state: Mutex::default(),
            completed: Condvar::new(),
            changed: Notify::new(),
        })
    }

    fn lock(&self) -> MutexGuard<'_, State> {
        // Nothing panics while holding the lock; a poisoned state stays usable.
        self.state.lock().unwrap_or_else(PoisonError::into_inner)
    }

    fn changed(&self) {
        self.completed.notify_all();
        self.changed.notify_waiters();
    }

    /// The Windows callback (managed `OnPowerNotification`). A suspend waits
    /// for its report for the rest of its budget, then expires it. Returns
    /// the transition that expired while still pending, never sent.
    pub(super) fn on_notification(&self, kind: u32) -> Option<Transition> {
        let transition = self.notify(kind)?;
        (transition.suspending
            && !self.wait(transition, self.remaining(transition))
            && self.expire(transition))
        .then_some(transition)
    }

    /// Record one notification. A new transition replaces the pending one and
    /// cancels the active report, or becomes the active report itself.
    fn notify(&self, kind: u32) -> Option<Transition> {
        let suspending = match kind {
            SUSPEND => true,
            RESUME_SUSPEND | RESUME_AUTOMATIC => false,
            _ => return None,
        };
        let transition = {
            let mut state = self.lock();
            if state.disposed || state.last == Some(suspending) {
                return None;
            }
            state.last = Some(suspending);
            state.sequence = state.sequence.checked_add(1)?;
            let transition = Transition {
                sequence: state.sequence,
                timestamp: (self.clock)(),
                suspending,
            };
            state.pending = None;
            match &mut state.active {
                None => state.active = Some(Active::new(transition)),
                Some(active) => {
                    active.canceled = true;
                    state.pending = Some(transition);
                }
            }
            transition
        };
        self.changed();
        Some(transition)
    }

    /// Wait up to `remaining` for the transition's report to complete.
    fn wait(&self, transition: Transition, remaining: Duration) -> bool {
        let state = self.lock();
        let (mut state, _) = self
            .completed
            .wait_timeout_while(state, remaining, |state| {
                !state.complete(transition.sequence)
            })
            .unwrap_or_else(PoisonError::into_inner);
        state.complete(transition.sequence)
    }

    /// The budget ran out (managed `ExpireTransition`): drop the transition if
    /// it is still pending, or cancel its report. Returns whether it was
    /// pending.
    fn expire(&self, transition: Transition) -> bool {
        let expired = {
            let mut state = self.lock();
            if state.pending == Some(transition) {
                state.pending = None;
                true
            } else {
                if let Some(active) = state.active(transition.sequence) {
                    active.canceled = true;
                }
                false
            }
        };
        self.changed();
        expired
    }

    /// The rest of the transition's budget. A clock before the timestamp, or
    /// a timestamp before the instance started, leaves none.
    fn remaining(&self, transition: Transition) -> Duration {
        let now = (self.clock)();
        if now < transition.timestamp || transition.timestamp < self.started {
            return Duration::ZERO;
        }
        let elapsed = (i128::from(now) - i128::from(transition.timestamp)) * 1_000_000_000
            / i128::from(self.frequency);
        BUDGET.saturating_sub(Duration::from_nanos(
            u64::try_from(elapsed).unwrap_or(u64::MAX),
        ))
    }

    /// Start the active report, if it has not started.
    fn next(&self) -> Next {
        let mut state = self.lock();
        if state.disposed {
            return Next::Disposed;
        }
        let Some(active) = state.active.as_mut().filter(|active| !active.started) else {
            return Next::Idle;
        };
        active.started = true;
        let (transition, canceled) = (active.transition, active.canceled);
        drop(state);
        if canceled || self.remaining(transition).is_zero() {
            Next::Skip(transition)
        } else {
            Next::Send(transition)
        }
    }

    /// The active report ended; the pending transition becomes active.
    fn finish(&self, transition: Transition) {
        {
            let mut state = self.lock();
            if state.active(transition.sequence).is_some() {
                state.active = state.pending.take().map(Active::new);
            }
        }
        self.changed();
    }

    /// Whether the transition's report must not go on: canceled, disposed, or
    /// no longer the active report.
    fn canceled(&self, transition: Transition) -> bool {
        let mut state = self.lock();
        state.disposed
            || state
                .active(transition.sequence)
                .is_none_or(|active| active.canceled)
    }

    fn startable(&self) -> bool {
        let state = self.lock();
        state.disposed || state.active.as_ref().is_some_and(|active| !active.started)
    }

    async fn until(&self, ready: impl Fn() -> bool) {
        loop {
            let notified = self.changed.notified();
            tokio::pin!(notified);
            notified.as_mut().enable();
            if ready() {
                return;
            }
            notified.await;
        }
    }

    /// Managed `Dispose`: drop the pending transition, cancel the active
    /// report and release every waiting callback. Later notifications are
    /// ignored.
    pub(super) fn dispose(&self) {
        {
            let mut state = self.lock();
            state.disposed = true;
            state.pending = None;
            if let Some(active) = &mut state.active {
                active.canceled = true;
            }
        }
        self.changed();
    }
}

/// A report's admission to its write. The exchange checks it after the
/// Service generation and pipe peer have verified, immediately before the
/// first byte is written, because that verification can itself spend the
/// budget or outlast a newer transition.
#[derive(Clone, Copy)]
struct Gate<'a> {
    relay: &'a Relay,
    transition: Transition,
}

impl Gate<'_> {
    /// The end of the budget, which bounds the write and the reply, or an
    /// `Abandoned` error when the report was superseded, disposed or is out
    /// of budget.
    fn admit(self) -> io::Result<Instant> {
        let remaining = self.relay.remaining(self.transition);
        if self.relay.canceled(self.transition) || remaining.is_zero() {
            return Err(io::Error::new(io::ErrorKind::TimedOut, Abandoned));
        }
        Ok(Instant::now() + remaining)
    }
}

/// The gate refused the write; the report ends as abandoned, not failed.
#[derive(Debug)]
struct Abandoned;

impl std::fmt::Display for Abandoned {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        formatter.write_str("session power transition report abandoned before its write")
    }
}

impl std::error::Error for Abandoned {}

/// The report ran out of budget before its write or while it waited: a
/// refused gate, or an exchange past the deadline the gate set.
fn is_abandoned(error: &io::Error) -> bool {
    error.get_ref().is_some_and(|inner| inner.is::<Abandoned>())
        || service_client::exchange_deadline_passed(error)
}

/// What every report of one relay instance names.
#[derive(Clone, Copy)]
struct Instance {
    id: [u8; 16],
    started: i64,
}

/// One report over `channel`: the gate's transition encoded as a request, its
/// exchange gated on the report's budget, and the decoded reply.
async fn exchange_report<S: AsyncRead + AsyncWrite + Unpin>(
    channel: &mut ReportChannel<'_>,
    instance: Instance,
    gate: Gate<'_>,
    connect: impl AsyncFnOnce() -> io::Result<S>,
    peer: impl Fn(&S) -> io::Result<(u32, u32)>,
) -> io::Result<Option<u8>> {
    let request = wire::Request {
        request_id: random_guid()?,
        instance: instance.id,
        started: instance.started,
        transition: gate.transition,
    };
    let payload = request.encode()?;
    let reply = channel
        .exchange_on(&payload, || gate.admit(), connect, peer)
        .await?;
    wire::decode_reply(&reply, &request.request_id_text())
}

/// The installed role. Its transport runs on its own thread and runtime, as
/// the application reporter's does, because the Service generation it holds
/// blocks to acquire and verify.
pub(super) async fn run(
    origin: Arc<crate::loaded_origin::Origin>,
    stop: watch::Receiver<bool>,
) -> io::Result<()> {
    super::verify_origin(&origin)?;
    super::transport_thread::host("OpenBlade session power relay", move || {
        transport(origin, stop)
    })
    .await
}

async fn transport(
    origin: Arc<crate::loaded_origin::Origin>,
    mut stop: watch::Receiver<bool>,
) -> io::Result<()> {
    let authority = WindowsInstalledServiceAuthority::from_agent(Arc::clone(&origin))
        .map_err(super::origin_error)?;
    let mut channel = ReportChannel::new(Some(&authority));
    let id = random_guid()?;
    let relay = Arc::new(Relay::new(
        windows::counter()?,
        windows::frequency()?,
        Box::new(windows::now),
    )?);
    // Managed traces a failed registration and runs without the relay.
    let registration = match windows::Registration::register(Arc::clone(&relay)) {
        Ok(registration) => registration,
        Err(code) => {
            record(Event::SessionPowerRelay(Notice::RegistrationFailed(code)));
            stopped(&mut stop).await;
            return Ok(());
        }
    };
    // A Service that is not running yet is acquired again by the first report.
    let _ = channel.prime();
    let instance = Instance {
        id,
        started: relay.started,
    };
    let result = serve(
        &relay,
        async |gate| {
            exchange_report(
                &mut channel,
                instance,
                gate,
                service_client::connect_installed,
                service_client::installed_peer,
            )
            .await
        },
        &mut stop,
        || super::verify_origin(&origin),
        |notice| record(Event::SessionPowerRelay(notice)),
    )
    .await;
    // `serve` disposed the relay first, so no callback still waits on this
    // transport while Windows unregisters it (managed disposes the relay
    // before unregistering too).
    drop(registration);
    result
}

/// The transport: reports each active transition until stop. `verify` runs
/// before each send; its failure ends the role. `send` admits its write
/// through the gate it is given; a refusal, or an exchange past the deadline
/// the gate set, ends the report as abandoned. Stop drops a report in flight,
/// which closes its connection. Every report ends in the relay before its
/// notice is recorded, so a waiting suspend callback is released, and the
/// pending transition becomes active, without waiting for diagnostics. The
/// relay is disposed on every exit.
async fn serve<'r>(
    relay: &'r Relay,
    mut send: impl AsyncFnMut(Gate<'r>) -> io::Result<Option<u8>>,
    stop: &mut watch::Receiver<bool>,
    verify: impl Fn() -> io::Result<()>,
    mut report: impl FnMut(Notice),
) -> io::Result<()> {
    let abandoned = |transition: Transition| Notice::Abandoned {
        sequence: transition.sequence,
        suspending: transition.suspending,
    };
    let result = loop {
        let transition = match relay.next() {
            Next::Disposed => {
                stopped(stop).await;
                break Ok(());
            }
            Next::Idle => {
                tokio::select! {
                    biased;
                    () = stopped(stop) => break Ok(()),
                    () = relay.until(|| relay.startable()) => continue,
                }
            }
            Next::Skip(transition) => {
                relay.finish(transition);
                report(abandoned(transition));
                continue;
            }
            Next::Send(transition) => transition,
        };
        if let Err(error) = verify() {
            break Err(error);
        }
        // The check may itself have spent the budget; nothing runs past it.
        let remaining = relay.remaining(transition);
        if remaining.is_zero() {
            relay.finish(transition);
            report(abandoned(transition));
            continue;
        }
        let ended = tokio::select! {
            biased;
            () = stopped(stop) => None,
            () = relay.until(|| relay.canceled(transition)) => Some(abandoned(transition)),
            sent = timeout(remaining, send(Gate { relay, transition })) => Some(match sent {
                Err(_elapsed) => abandoned(transition),
                Ok(Err(error)) if is_abandoned(&error) => abandoned(transition),
                Ok(Ok(status)) => Notice::Answered {
                    sequence: transition.sequence,
                    suspending: transition.suspending,
                    status,
                },
                Ok(Err(error)) => Notice::Failed {
                    sequence: transition.sequence,
                    suspending: transition.suspending,
                    kind: error.kind(),
                },
            }),
        };
        let Some(notice) = ended else {
            relay.dispose();
            report(abandoned(transition));
            break Ok(());
        };
        relay.finish(transition);
        report(notice);
    };
    relay.dispose();
    result
}

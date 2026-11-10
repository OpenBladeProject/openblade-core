//! Private development supervisor. The adapter owns child creation and process exit observation.
//! No product worker or native sensor is launched by this module.

use std::{io, time::Duration};

use tokio::{
    io::{AsyncRead, AsyncReadExt, AsyncWrite, AsyncWriteExt},
    sync::watch,
    task::JoinHandle,
    time::{Instant, timeout_at},
};

pub mod protocol;
#[cfg(all(windows, feature = "cpu-worker-fixture"))]
pub mod windows;
use protocol::{REPLY_LENGTH, Reply, Sequence, decode_reply};

const READ_TIMEOUT: Duration = Duration::from_secs(1);
const STARTUP_TIMEOUT: Duration = Duration::from_secs(10);
const STOP_TIMEOUT: Duration = Duration::from_millis(250);

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum Exit {
    Pending,
    Confirmed,
    Unobservable,
}

/// A private byte stream with independent stop and exit observation. The adapter
/// must retain its child ownership until exit is independently confirmed. A
/// closed stream, dropped exit sender, or requested stop is not an exit proof.
pub struct PrivateChild<S> {
    pub io: S,
    pub stop: watch::Sender<Option<Instant>>,
    pub exit: watch::Receiver<Exit>,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum Failure {
    Unavailable,
    Timeout,
    MalformedReply,
    TerminalUnavailable,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct Diagnostics {
    pub last_failure: Option<Failure>,
    pub exit_unconfirmed: bool,
}

/// Evidence that this worker generation completed the Ready exchange.
#[derive(Debug)]
pub struct Readiness(WorkerGeneration);

impl Readiness {
    pub(crate) fn is_active(&self) -> bool {
        self.0.active()
    }
}

#[derive(Debug)]
struct WorkerGeneration {
    stop: watch::Receiver<Option<Instant>>,
    exit: watch::Receiver<Exit>,
}

impl WorkerGeneration {
    fn active(&self) -> bool {
        self.stop.has_changed().is_ok()
            && self.stop.borrow().is_none()
            && self.exit.has_changed().is_ok()
            && *self.exit.borrow() == Exit::Pending
    }
}

/// Evidence of a completed sample request. The supervisor does not cache samples.
#[derive(Debug)]
pub struct Sample {
    celsius: u8,
    #[cfg(windows)]
    generation: WorkerGeneration,
    requested_at: Instant,
    received_at: Instant,
}

impl Sample {
    #[cfg(windows)]
    pub(crate) fn for_entry(
        &self,
        ready: &Readiness,
        start: Instant,
        completed: Instant,
        at: Instant,
        max_age: Duration,
    ) -> Option<(u8, Instant)> {
        if !self.generation.exit.same_channel(&ready.0.exit)
            || !self.generation.stop.same_channel(&ready.0.stop)
            || !self.generation.active()
            || self.received_at > completed
        {
            return None;
        }
        self.celsius_for(start, at, max_age)
            .map(|celsius| (celsius, self.requested_at))
    }

    /// Times use the same monotonic clock. Age starts at the request, even when
    /// the response spent most of that age in flight.
    pub fn celsius_for(
        &self,
        window_start: Instant,
        now: Instant,
        max_age: Duration,
    ) -> Option<u8> {
        (window_start <= self.requested_at
            && self.received_at <= now
            && now - self.requested_at <= max_age)
            .then_some(self.celsius)
    }
}

type Startup<S> = JoinHandle<Result<S, Instant>>;

enum Phase<S> {
    Starting(Startup<S>),
    Ready(S),
    Stopping,
}

struct Lease<S> {
    phase: Phase<S>,
    stop: watch::Sender<Option<Instant>>,
    exit: watch::Receiver<Exit>,
}

impl<S> Lease<S> {
    fn generation(&self) -> WorkerGeneration {
        WorkerGeneration {
            stop: self.stop.subscribe(),
            exit: self.exit.clone(),
        }
    }

    fn exit(&self) -> Exit {
        let closed = self.exit.has_changed().is_err();
        let status = *self.exit.borrow();
        if status == Exit::Pending && closed {
            Exit::Unobservable
        } else {
            status
        }
    }

    fn stop(&mut self, at: Instant) {
        if !matches!(self.phase, Phase::Stopping) {
            if let Phase::Starting(task) = &self.phase {
                task.abort();
            }
            self.phase = Phase::Stopping;
            signal_stop(&self.stop, at);
        }
    }
}

/// Exclusive access serializes requests. Callers that share a supervisor must
/// include time spent acquiring that access in their own cancellation budget.
/// Dropping a pending sample future requests stop; dropping a warmup wait
/// leaves initialization owned by this supervisor.
pub struct Supervisor<S> {
    lease: Option<Lease<S>>,
    // Never reset across worker generations: readiness consumes a number too.
    sequence: i64,
    failures: u8,
    retry_at: Option<Instant>,
    last_failure: Option<Failure>,
    defer_cold_sampling: bool,
}

impl<S> Default for Supervisor<S> {
    fn default() -> Self {
        Self {
            lease: None,
            sequence: 0,
            failures: 0,
            retry_at: None,
            last_failure: None,
            defer_cold_sampling: false,
        }
    }
}

impl<S> Drop for Supervisor<S> {
    fn drop(&mut self) {
        if let Some(lease) = &mut self.lease {
            lease.stop(Instant::now());
        }
    }
}

impl<S> Supervisor<S>
where
    S: AsyncRead + AsyncWrite + Unpin + Send + 'static,
{
    /// Initialize without taking a reading. The factory only attaches a
    /// nonblocking private adapter; process launch is outside this contract.
    pub async fn prepare(&mut self, attach: impl FnOnce() -> io::Result<PrivateChild<S>>) -> bool {
        self.prepare_readiness(attach).await.is_some()
    }

    pub async fn prepare_readiness(
        &mut self,
        attach: impl FnOnce() -> io::Result<PrivateChild<S>>,
    ) -> Option<Readiness> {
        let deadline = Instant::now() + STARTUP_TIMEOUT;
        self.ready(attach, deadline, Failure::Unavailable).await?;
        let ready = Readiness(self.lease.as_ref()?.generation());
        ready.is_active().then_some(ready)
    }

    pub async fn sample(
        &mut self,
        attach: impl FnOnce() -> io::Result<PrivateChild<S>>,
    ) -> Option<Sample> {
        let started_at = Instant::now();
        let deadline = started_at + READ_TIMEOUT;
        let warming = self.ready(attach, deadline, Failure::Timeout).await?;
        let now = Instant::now();
        if now >= deadline {
            self.last_failure = Some(Failure::Timeout);
            return None;
        }
        if warming && (self.defer_cold_sampling || now - started_at > READ_TIMEOUT / 2) {
            self.last_failure = Some(Failure::Unavailable);
            return None;
        }

        self.sample_ready(warming, deadline).await
    }

    async fn sample_ready(&mut self, warming: bool, deadline: Instant) -> Option<Sample> {
        let Some(sequence) = self.next_sequence() else {
            self.fail(Failure::Unavailable, Instant::now());
            return None;
        };

        let mut active = ActiveSample {
            owner: self,
            pending: true,
        };
        let Phase::Ready(io) = &mut active.owner.lease.as_mut().unwrap().phase else {
            unreachable!("ready must have returned a stream");
        };
        let exchange = exchange_by(io, sequence, deadline).await;
        active.pending = false;
        let owner = &mut *active.owner;
        let Some(result) = exchange else {
            // The deadline elapsed before any write attempt. Leave the healthy
            // worker and the unused wire sequence in place.
            owner.last_failure = Some(Failure::Timeout);
            return None;
        };
        owner.sequence += 1;
        let failure = match result {
            Ok(TimedReply {
                reply: Reply::Value(value),
                requested_at,
                received_at,
            }) => {
                owner.failures = 0;
                owner.retry_at = None;
                owner.last_failure = None;
                let generation = owner.lease.as_ref().unwrap().generation();
                if !generation.active() {
                    owner.fail(Failure::Unavailable, received_at);
                    return None;
                }
                return Some(Sample {
                    celsius: value.rounded_celsius(),
                    #[cfg(windows)]
                    generation,
                    requested_at,
                    received_at,
                });
            }
            Ok(TimedReply {
                reply: Reply::Unavailable,
                received_at,
                ..
            }) => {
                owner.back_off(received_at);
                owner.last_failure = Some(Failure::Unavailable);
                return None;
            }
            Ok(TimedReply {
                reply: Reply::Ready,
                ..
            }) => Failure::MalformedReply,
            Ok(TimedReply {
                reply: Reply::TerminalUnavailable,
                ..
            }) => Failure::TerminalUnavailable,
            Err(failure) => failure,
        };
        if warming && failure == Failure::Timeout {
            // The managed reader retains this observation across generations.
            owner.defer_cold_sampling = true;
        }
        owner.fail(failure, Instant::now());
        None
    }

    pub fn diagnostics(&self) -> Diagnostics {
        let exit_unconfirmed = self.lease.as_ref().is_some_and(|lease| match lease.exit() {
            Exit::Confirmed => false,
            Exit::Unobservable => true,
            Exit::Pending => lease
                .stop
                .borrow()
                .is_some_and(|since| since.elapsed() >= STOP_TIMEOUT),
        });
        Diagnostics {
            last_failure: self.last_failure,
            exit_unconfirmed,
        }
    }

    async fn ready(
        &mut self,
        attach: impl FnOnce() -> io::Result<PrivateChild<S>>,
        deadline: Instant,
        wait_failure: Failure,
    ) -> Option<bool> {
        if let Some(lease) = &self.lease
            && matches!(lease.phase, Phase::Stopping)
        {
            if lease.exit() != Exit::Confirmed {
                return None;
            }
            self.lease = None;
        }
        if self.retry_at.is_some_and(|retry| Instant::now() < retry) {
            return None;
        }

        let mut warming = false;
        if self.lease.is_none() {
            let Some(sequence) = self.next_sequence() else {
                self.last_failure = Some(Failure::Unavailable);
                return None;
            };
            let started = Instant::now();
            let Ok(child) = attach() else {
                self.back_off(Instant::now());
                self.last_failure = Some(Failure::Unavailable);
                return None;
            };
            self.sequence += 1;
            self.lease = Some(start(child, sequence, started + STARTUP_TIMEOUT));
            warming = true;
        }
        let lease = self.lease.as_mut().unwrap();
        if let Phase::Starting(task) = &mut lease.phase {
            warming |= !task.is_finished();
            match timeout_at(deadline, task).await {
                Ok(Ok(Ok(io))) => lease.phase = Phase::Ready(io),
                Ok(Ok(Err(at))) => {
                    self.fail(Failure::Unavailable, at);
                    return None;
                }
                Ok(Err(_)) => {
                    self.fail(Failure::Unavailable, Instant::now());
                    return None;
                }
                Err(_) => {
                    self.last_failure = Some(wait_failure);
                    return None;
                }
            }
        }
        match lease.exit() {
            Exit::Pending => Some(warming),
            Exit::Confirmed => {
                // As in the managed reader, an observed idle exit is not a
                // sample fault. Retire it and let the next call attach.
                lease.stop(Instant::now());
                None
            }
            Exit::Unobservable => {
                self.fail(Failure::Unavailable, Instant::now());
                None
            }
        }
    }

    fn next_sequence(&self) -> Option<Sequence> {
        self.sequence.checked_add(1).and_then(Sequence::new)
    }

    fn back_off(&mut self, at: Instant) {
        self.failures = (self.failures + 1).min(16);
        self.retry_at = (self.failures >= 2)
            .then(|| at + Duration::from_secs((1_u64 << (self.failures - 2)).min(30)));
    }

    fn fail(&mut self, failure: Failure, at: Instant) {
        self.poison(at);
        self.last_failure = Some(failure);
    }

    fn poison(&mut self, at: Instant) {
        self.lease.as_mut().unwrap().stop(at);
        self.back_off(at);
    }
}

struct ActiveSample<'a, S: AsyncRead + AsyncWrite + Unpin + Send + 'static> {
    owner: &'a mut Supervisor<S>,
    pending: bool,
}

impl<S: AsyncRead + AsyncWrite + Unpin + Send + 'static> Drop for ActiveSample<'_, S> {
    fn drop(&mut self) {
        if self.pending {
            // The worker may have received the request. Discard its stream and
            // keep the exit fence even if the caller canceled before a reply.
            self.owner.sequence += 1;
            self.owner.poison(Instant::now());
        }
    }
}

fn start<S>(child: PrivateChild<S>, sequence: Sequence, deadline: Instant) -> Lease<S>
where
    S: AsyncRead + AsyncWrite + Unpin + Send + 'static,
{
    let PrivateChild { mut io, stop, exit } = child;
    let stop_on_failure = stop.clone();
    let watch_exit = exit.clone();
    let task = tokio::spawn(async move {
        let open = watch_exit.has_changed().is_ok() && *watch_exit.borrow() == Exit::Pending;
        if open
            && matches!(
                exchange_by(&mut io, sequence, deadline).await,
                Some(Ok(TimedReply {
                    reply: Reply::Ready,
                    ..
                }))
            )
        {
            Ok(io)
        } else {
            let at = Instant::now();
            signal_stop(&stop_on_failure, at);
            Err(at)
        }
    });
    Lease {
        phase: Phase::Starting(task),
        stop,
        exit,
    }
}

fn signal_stop(stop: &watch::Sender<Option<Instant>>, at: Instant) {
    stop.send_if_modified(|requested| {
        if requested.is_some() {
            return false;
        }
        *requested = Some(at);
        true
    });
}

struct TimedReply {
    reply: Reply,
    requested_at: Instant,
    received_at: Instant,
}

/// None means the deadline elapsed before any request could have been written.
/// An error inside Some retires the stream: submission or framing may be uncertain.
async fn exchange_by<S: AsyncRead + AsyncWrite + Unpin>(
    io: &mut S,
    sequence: Sequence,
    deadline: Instant,
) -> Option<Result<TimedReply, Failure>> {
    let frame = timeout_at(deadline, async {
        let requested_at = Instant::now();
        if requested_at >= deadline {
            return Ok(None);
        }
        io.write_all(&sequence.encode()).await?;
        io.flush().await?;
        let mut reply = [0; REPLY_LENGTH];
        io.read_exact(&mut reply).await?;
        Ok::<_, io::Error>(Some((reply, requested_at)))
    })
    .await;
    let received_at = Instant::now();
    let (frame, requested_at) = match frame {
        Err(_) => return Some(Err(Failure::Timeout)),
        Ok(Err(_)) => return Some(Err(Failure::Unavailable)),
        Ok(Ok(None)) => return None,
        Ok(Ok(Some(result))) => result,
    };
    // Tokio can poll a frame first when both the exchange and timer are ready.
    if received_at > deadline {
        return Some(Err(Failure::Timeout));
    }
    Some(
        decode_reply(&frame, sequence)
            .map(|reply| TimedReply {
                reply,
                requested_at,
                received_at,
            })
            .ok_or(Failure::MalformedReply),
    )
}
#[cfg(test)]
mod tests;

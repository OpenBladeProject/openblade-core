//! Read-only installed Agent Service client. The installed authority is unwired.
#![allow(dead_code)]

use std::{future::Future, io, num::NonZeroU32, os::windows::io::AsRawHandle, time::Duration};

use tokio::{
    io::{AsyncRead, AsyncWrite},
    net::windows::named_pipe::{ClientOptions, NamedPipeClient},
    time::{Instant, sleep, timeout_at},
};
use windows_sys::Win32::{
    Foundation::{ERROR_FILE_NOT_FOUND, ERROR_PIPE_BUSY},
    Storage::FileSystem::SECURITY_IDENTIFICATION,
};

use crate::{
    pipe_server_identity::server_identity,
    service_status::{
        ConfirmedPerformance, Snapshot, StatusRequest, exchange, production_path,
        read_status_event, request_id, subscribe,
    },
};

const STATUS_BUDGET: Duration = Duration::from_secs(2);
const INITIAL_RECONNECT_DELAY: Duration = Duration::from_millis(250);
const MAX_RECONNECT_DELAY: Duration = Duration::from_secs(5);

// Only an independent trusted proof source may classify absence/retirement.
// Native lookup failures and proof mismatches belong in Denied, never in a
// retryable variant merely because their OS error looks transient.
pub(crate) enum ServiceAuthorityError {
    Unavailable,
    Retired,
    Denied(io::Error),
}

impl From<io::Error> for ServiceAuthorityError {
    fn from(error: io::Error) -> Self {
        Self::Denied(error)
    }
}

impl ServiceAuthorityError {
    fn into_io(self) -> io::Error {
        match self {
            Self::Unavailable => {
                io::Error::new(io::ErrorKind::WouldBlock, "Service proof unavailable")
            }
            Self::Retired => io::Error::new(
                io::ErrorKind::ConnectionAborted,
                "Service generation retired",
            ),
            Self::Denied(error) => unverified(error),
        }
    }
}

// Retain one independently authorized LocalSystem Service generation in session
// zero (including a process-instance handle or equivalent trusted proof).
// Verification MUST check this original generation, even when a replacement
// has the same PID. Never resolve the current PID/path to rebind this object.
pub(crate) trait InstalledServiceGeneration {
    fn verify_live_process(&self) -> Result<NonZeroU32, ServiceAuthorityError>;
}

// Acquire a fresh independently authorized generation for each new stream.
// No production proof provider is wired here; pipe/PID/path are not authority.
pub(crate) trait InstalledServiceAuthority {
    fn acquire_generation(
        &self,
    ) -> Result<Box<dyn InstalledServiceGeneration + '_>, ServiceAuthorityError>;
}

// Implement only when EVERY acquired generation's ObserveState path
// independently reads performance or refuses. A v91 status peer alone
// cannot attest that its observation handler refreshed the hardware.
pub(crate) trait InstalledObservationAuthority: InstalledServiceAuthority {}

pub(crate) struct StatusObservation {
    pub(crate) snapshot: Snapshot,
    received_at: Instant,
}

impl StatusObservation {
    pub(crate) fn require_fresh(&self) -> io::Result<()> {
        if self.received_at.elapsed() >= STATUS_BUDGET {
            return Err(io::Error::new(
                io::ErrorKind::TimedOut,
                "stale Service status",
            ));
        }
        Ok(())
    }

    /// The instant at which this status stops being fresh, on the clock the
    /// function-row hook reads. A stale status is refused as `require_fresh` refuses it.
    #[cfg(feature = "agent-function-row-v1")]
    pub(crate) fn fresh_until(&self) -> io::Result<std::time::Instant> {
        self.require_fresh()?;
        Ok((self.received_at + STATUS_BUDGET).into_std())
    }
}

pub(crate) struct PerformanceObservation(StatusObservation);

impl PerformanceObservation {
    pub(crate) fn confirmed(&self) -> io::Result<Option<ConfirmedPerformance>> {
        self.0.require_fresh()?;
        Ok(self.0.snapshot.confirmed_performance())
    }
}

// Only a verified SubscribeStatus response can establish a new subscription.
pub(crate) struct SubscriptionInitial(StatusObservation);

impl SubscriptionInitial {
    pub(super) fn require_fresh(&self) -> io::Result<()> {
        self.0.require_fresh()
    }

    pub(crate) fn into_observation(self) -> StatusObservation {
        self.0
    }
}

#[cfg(any(
    feature = "agent-application-reporter-v1",
    feature = "agent-function-row-v1",
    feature = "agent-power-mode-automation-v1"
))]
impl SubscriptionInitial {
    pub(crate) fn snapshot(&self) -> &Snapshot {
        &self.0.snapshot
    }
}

#[cfg(feature = "agent-function-row-v1")]
impl SubscriptionInitial {
    pub(crate) fn fresh_until(&self) -> io::Result<std::time::Instant> {
        self.0.fresh_until()
    }
}

pub(crate) enum StatusUpdate {
    Opened(SubscriptionInitial),
    Event(StatusObservation),
    Reconnecting,
}

pub(super) async fn watch_status(
    authority: Option<&dyn InstalledServiceAuthority>,
    canceled: impl Future<Output = ()>,
    publish: impl FnMut(StatusUpdate) -> io::Result<()>,
) -> io::Result<()> {
    watch_monitored(
        authority,
        canceled,
        request_id,
        connect_installed,
        |pipe| server_identity(pipe.as_raw_handle()),
        |pipe| crate::pipe_server_identity::retain_monitor(pipe.as_raw_handle()).map(Some),
        publish,
    )
    .await
}

pub(crate) async fn watch_on<S: AsyncRead + AsyncWrite + Unpin>(
    authority: Option<&dyn InstalledServiceAuthority>,
    canceled: impl Future<Output = ()>,
    next_id: impl FnMut() -> io::Result<String>,
    connect: impl AsyncFnMut() -> io::Result<S>,
    peer: impl Fn(&S) -> io::Result<(u32, u32)>,
    publish: impl FnMut(StatusUpdate) -> io::Result<()>,
) -> io::Result<()> {
    watch_monitored(
        authority,
        canceled,
        next_id,
        connect,
        peer,
        |_| Ok(None),
        publish,
    )
    .await
}

type IdlePeer = Box<dyn Fn() -> io::Result<(u32, u32)>>;
async fn watch_monitored<S: AsyncRead + AsyncWrite + Unpin>(
    authority: Option<&dyn InstalledServiceAuthority>,
    canceled: impl Future<Output = ()>,
    mut next_id: impl FnMut() -> io::Result<String>,
    mut connect: impl AsyncFnMut() -> io::Result<S>,
    peer: impl Fn(&S) -> io::Result<(u32, u32)>,
    monitor: impl Fn(&S) -> io::Result<Option<IdlePeer>>,
    mut publish: impl FnMut(StatusUpdate) -> io::Result<()>,
) -> io::Result<()> {
    let watch = async {
        let authority = require_authority(authority)?;
        let mut delay = INITIAL_RECONNECT_DELAY;
        loop {
            let opened = open_on(authority, &mut next_id, &mut connect, &peer).await;
            let (mut stream, generation, pid, initial) = match opened {
                Ok(opened) => opened,
                Err(error) if retryable(&error) => {
                    publish(StatusUpdate::Reconnecting)?;
                    sleep(delay).await;
                    delay = (delay * 2).min(MAX_RECONNECT_DELAY);
                    continue;
                }
                Err(error) => return Err(error),
            };
            let idle_peer = monitor(&stream)?;
            let connected_at = Instant::now();
            publish(StatusUpdate::Opened(initial))?;
            loop {
                let event = {
                    let reading = read_status_event(&mut stream);
                    tokio::pin!(reading);
                    let mut check = tokio::time::interval(Duration::from_millis(500));
                    // Windows `Instant` keeps counting through sleep. A burst
                    // would run every check missed during a suspend back to
                    // back; one check after resume proves the generation and
                    // peer just as well.
                    check.set_missed_tick_behavior(tokio::time::MissedTickBehavior::Delay);

                    loop {
                        tokio::select! {
                            result = &mut reading => break result,
                            _ = check.tick() => {
                                let checked = (|| {
                                    let live = generation.verify_live_process().map_err(ServiceAuthorityError::into_io)?;
                                    if live != pid { return Err(super::denied("Service generation changed during idle")); }
                                    if let Some(peer) = &idle_peer {
                                        let actual = peer();
                                        generation.verify_live_process().map_err(ServiceAuthorityError::into_io)?;
                                        if actual.map_err(unverified)? != (pid.get(), 0) { return Err(super::denied("idle pipe peer changed")); }
                                    }
                                    Ok(())
                                })();
                                if let Err(error) = checked { break Err(error); }
                            }
                        }
                    }
                };
                let snapshot = match event {
                    Ok(snapshot) => snapshot,
                    Err(error) if retryable(&error) => break,
                    Err(error) => return Err(error),
                };
                match verify_peer(generation.as_ref(), pid, &stream, &peer) {
                    Ok(()) => (),
                    Err(error) if retryable(&error) => break,
                    Err(error) => return Err(error),
                }
                publish(StatusUpdate::Event(StatusObservation {
                    snapshot,
                    received_at: Instant::now(),
                }))?;
            }
            // A healthy idle subscription is stable too; no event/heartbeat is
            // needed. Successful opens alone never reset exponential backoff.
            if connected_at.elapsed() >= MAX_RECONNECT_DELAY {
                delay = INITIAL_RECONNECT_DELAY;
            }
            drop(idle_peer);
            drop(stream);
            drop(generation);
            publish(StatusUpdate::Reconnecting)?;
            sleep(delay).await;
            delay = (delay * 2).min(MAX_RECONNECT_DELAY);
        }
    };
    tokio::select! {
        biased;
        () = canceled => Err(io::ErrorKind::Interrupted.into()),
        result = watch => result,
    }
}

async fn open_on<'a, S: AsyncRead + AsyncWrite + Unpin>(
    authority: &'a dyn InstalledServiceAuthority,
    next_id: impl FnOnce() -> io::Result<String>,
    connect: impl AsyncFnOnce() -> io::Result<S>,
    peer: &impl Fn(&S) -> io::Result<(u32, u32)>,
) -> io::Result<(
    S,
    Box<dyn InstalledServiceGeneration + 'a>,
    NonZeroU32,
    SubscriptionInitial,
)> {
    let deadline = Instant::now() + STATUS_BUDGET;
    timeout_at(deadline, async {
        let generation = authority
            .acquire_generation()
            .map_err(ServiceAuthorityError::into_io)?;
        let pid = generation
            .verify_live_process()
            .map_err(ServiceAuthorityError::into_io)?;
        let id = next_id().map_err(|error| io::Error::new(io::ErrorKind::InvalidData, error))?;
        let mut stream = connect().await?;
        let snapshot = subscribe(&mut stream, id, |stream| {
            check_deadline(deadline)?;
            verify_peer(generation.as_ref(), pid, stream, peer)
        })
        .await?;
        let initial = SubscriptionInitial(observe_before_deadline(snapshot, deadline)?);
        Ok((stream, generation, pid, initial))
    })
    .await?
}

fn retryable(error: &io::Error) -> bool {
    !matches!(
        error.kind(),
        io::ErrorKind::InvalidData | io::ErrorKind::PermissionDenied | io::ErrorKind::Unsupported
    )
}

fn unverified(error: io::Error) -> io::Error {
    io::Error::new(io::ErrorKind::PermissionDenied, error)
}

pub(super) async fn get_status(
    authority: Option<&dyn InstalledServiceAuthority>,
    canceled: impl Future<Output = ()>,
) -> io::Result<StatusObservation> {
    // Fixed production endpoint. No scratch or pipe-supplied identity fallback.
    read_on(authority, canceled, request_id, connect_installed, |pipe| {
        server_identity(pipe.as_raw_handle())
    })
    .await
}

async fn read_on<S: AsyncRead + AsyncWrite + Unpin>(
    authority: Option<&dyn InstalledServiceAuthority>,
    canceled: impl Future<Output = ()>,
    next_id: impl FnOnce() -> io::Result<String>,
    connect: impl AsyncFnOnce() -> io::Result<S>,
    peer: impl Fn(&S) -> io::Result<(u32, u32)>,
) -> io::Result<StatusObservation> {
    request_on(
        authority,
        canceled,
        || StatusRequest::get(next_id()?),
        connect,
        peer,
    )
    .await
}

pub(super) async fn observe_state(
    authority: Option<&dyn InstalledObservationAuthority>,
    canceled: impl Future<Output = ()>,
) -> io::Result<PerformanceObservation> {
    observe_on(authority, canceled, request_id, connect_installed, |pipe| {
        server_identity(pipe.as_raw_handle())
    })
    .await
}

async fn observe_on<S: AsyncRead + AsyncWrite + Unpin>(
    authority: Option<&dyn InstalledObservationAuthority>,
    canceled: impl Future<Output = ()>,
    next_id: impl FnOnce() -> io::Result<String>,
    connect: impl AsyncFnOnce() -> io::Result<S>,
    peer: impl Fn(&S) -> io::Result<(u32, u32)>,
) -> io::Result<PerformanceObservation> {
    request_on(
        authority.map(|authority| authority as &dyn InstalledServiceAuthority),
        canceled,
        || StatusRequest::observe_performance(next_id()?),
        connect,
        peer,
    )
    .await
    .map(PerformanceObservation)
}

async fn request_on<S: AsyncRead + AsyncWrite + Unpin>(
    authority: Option<&dyn InstalledServiceAuthority>,
    canceled: impl Future<Output = ()>,
    request: impl FnOnce() -> io::Result<StatusRequest>,
    connect: impl AsyncFnOnce() -> io::Result<S>,
    peer: impl Fn(&S) -> io::Result<(u32, u32)>,
) -> io::Result<StatusObservation> {
    let deadline = Instant::now() + STATUS_BUDGET;
    let read = async {
        let authority = require_authority(authority)?;
        let generation = authority
            .acquire_generation()
            .map_err(ServiceAuthorityError::into_io)?;
        let pid = generation
            .verify_live_process()
            .map_err(ServiceAuthorityError::into_io)?;
        let request = request()?;
        let mut stream = connect().await?;
        let snapshot = exchange(&mut stream, &request, None, |stream| {
            check_deadline(deadline)?;
            verify_peer(generation.as_ref(), pid, stream, &peer)
        })
        .await?;
        observe_before_deadline(snapshot, deadline)
    };
    // The future owns the stream. Canceling drops it even during a partial
    // frame. A failed request is never replayed.
    tokio::select! {
        biased;
        () = canceled => Err(io::ErrorKind::Interrupted.into()),
        result = timeout_at(deadline, read) => result?,
    }
}

/// Managed `ServiceClient.ReportApplicationContextAsync`: one connection per
/// report, closed after its reply. The Agent additionally proves the installed
/// Service generation and pipe peer before any byte is sent and again before
/// the reply is accepted, because the request names the foreground executable.
///
/// The channel keeps one generation across reports, as the status subscription
/// keeps one for its stream. Acquiring a generation re-reads and hashes the
/// installed package, which is far too heavy for every five-second renewal. A
/// report verifies the held generation, as `verify_peer` does for any stream,
/// and acquires a new one only when it is retired or a verification fails. An
/// I/O failure keeps it, since it says nothing about the Service identity, and
/// so does dropping a report future, which closes its connection; a canceled
/// report is never replayed. Acquisition blocks its caller, so run the channel on a thread that
/// may block, not on the Agent's shared runtime.
///
/// The session power relay (managed `ReportSessionPowerTransitionAsync`)
/// shares the exchange: it sends its own encoded request and decodes the reply
/// itself, under a deadline its callback budget sets just before the write.
#[cfg(any(
    feature = "agent-application-reporter-v1",
    feature = "agent-session-power-relay-v1"
))]
pub(super) struct ReportChannel<'a> {
    authority: Option<&'a dyn InstalledServiceAuthority>,
    generation: Option<(Box<dyn InstalledServiceGeneration + 'a>, NonZeroU32)>,
}

/// Managed connects within two seconds and then waits for the reply without a
/// limit. A wedged Service would hold the one in-flight report, and with it
/// every renewal, forever, so the connect has its own two-second bound and
/// the reply `REPORT_REPLY_BUDGET`.
#[cfg(any(
    feature = "agent-application-reporter-v1",
    feature = "agent-session-power-relay-v1"
))]
const REPORT_CONNECT_BUDGET: Duration = Duration::from_secs(2);
/// A report may reconcile saved intent under the Service's
/// `RECONCILE_BUDGET`, and the Service answers a reconciliation that has run
/// even when it finished late, so the reply wait covers that budget and one
/// frame more. Abandoning the wait earlier would leave a submitted result
/// unacknowledged and the report pending.
#[cfg(feature = "agent-application-reporter-v1")]
const REPORT_REPLY_BUDGET: Duration = Duration::from_secs(
    crate::service_frame::RECONCILE_BUDGET.as_secs()
        + crate::service_frame::FRAME_TIMEOUT.as_secs(),
);

#[cfg(any(
    feature = "agent-application-reporter-v1",
    feature = "agent-session-power-relay-v1"
))]
impl<'a> ReportChannel<'a> {
    pub(super) fn new(authority: Option<&'a dyn InstalledServiceAuthority>) -> Self {
        Self {
            authority,
            generation: None,
        }
    }

    #[cfg(feature = "agent-application-reporter-v1")]
    pub(super) async fn report(
        &mut self,
        request: &openblade_application_reporter::ReportApplicationContextRequest,
    ) -> io::Result<openblade_application_reporter::ReportOutcome> {
        self.report_on(request, connect_installed, |pipe| {
            server_identity(pipe.as_raw_handle())
        })
        .await
    }

    /// Establish the held generation now, so the first exchange need not
    /// re-read and hash the installed package inside its own budget.
    #[cfg(feature = "agent-session-power-relay-v1")]
    pub(super) fn prime(&mut self) -> io::Result<()> {
        let (generation, pid) = self.admit()?;
        self.generation = Some((generation, pid));
        Ok(())
    }

    /// The held generation if it still verifies, else a freshly acquired one.
    fn admit(&mut self) -> io::Result<(Box<dyn InstalledServiceGeneration + 'a>, NonZeroU32)> {
        let authority = require_authority(self.authority)?;
        if let Some((generation, pid)) = self.generation.take()
            && generation.verify_live_process().ok() == Some(pid)
        {
            return Ok((generation, pid));
        }
        let generation = authority
            .acquire_generation()
            .map_err(ServiceAuthorityError::into_io)?;
        let pid = generation
            .verify_live_process()
            .map_err(ServiceAuthorityError::into_io)?;
        Ok((generation, pid))
    }

    #[cfg(feature = "agent-application-reporter-v1")]
    async fn report_on<S: AsyncRead + AsyncWrite + Unpin>(
        &mut self,
        request: &openblade_application_reporter::ReportApplicationContextRequest,
        connect: impl AsyncFnOnce() -> io::Result<S>,
        peer: impl Fn(&S) -> io::Result<(u32, u32)>,
    ) -> io::Result<openblade_application_reporter::ReportOutcome> {
        use openblade_windows_host::reporter_pipe::{Response, encode, guid_text};

        let payload = encode(request)?;
        let reply = self
            .exchange_on(
                &payload,
                || Ok(Instant::now() + REPORT_REPLY_BUDGET),
                connect,
                peer,
            )
            .await?;
        // Any refusal, including a typed Blocked or Failed result, leaves the
        // report pending exactly as managed: only Stored and Applied acknowledge.
        Response::decode(&reply, &guid_text(request.request_id))
            .and_then(|response| response.report_outcome())
            .map(|(outcome, _message)| outcome)
    }

    /// Send one already encoded request and return the reply payload, once the
    /// peer has verified again. `before_write` runs after that verification,
    /// immediately before the write: its refusal sends nothing, and its
    /// deadline bounds the write and the reply read together. Passing that
    /// deadline fails with an error `exchange_deadline_passed` recognizes.
    /// Only a failed verification, here or after the reply, drops the held
    /// generation; any other end, including a dropped exchange, keeps it.
    pub(super) async fn exchange_on<S: AsyncRead + AsyncWrite + Unpin>(
        &mut self,
        payload: &[u8],
        before_write: impl FnOnce() -> io::Result<Instant>,
        connect: impl AsyncFnOnce() -> io::Result<S>,
        peer: impl Fn(&S) -> io::Result<(u32, u32)>,
    ) -> io::Result<Vec<u8>> {
        use crate::service_frame::{read_payload, write_payload};

        let admitted = self.admit()?;
        let mut held = Held {
            slot: &mut self.generation,
            admitted: Some(admitted),
        };
        let mut stream = timeout_at(Instant::now() + REPORT_CONNECT_BUDGET, connect()).await??;
        held.verify(&stream, &peer)?;
        let deadline = before_write()?;
        let reply = timeout_at(deadline, async {
            write_payload(&mut stream, payload).await?;
            read_payload(&mut stream).await
        })
        .await
        .map_err(|_elapsed| io::Error::new(io::ErrorKind::TimedOut, DeadlinePassed))??;
        held.verify(&stream, &peer)?;
        Ok(reply)
    }
}

/// The generation one exchange admitted. It goes back to the channel when the
/// exchange ends, however it ends, including when its future is dropped,
/// unless a verification failed.
#[cfg(any(
    feature = "agent-application-reporter-v1",
    feature = "agent-session-power-relay-v1"
))]
struct Held<'h, 'a> {
    slot: &'h mut Option<(Box<dyn InstalledServiceGeneration + 'a>, NonZeroU32)>,
    admitted: Option<(Box<dyn InstalledServiceGeneration + 'a>, NonZeroU32)>,
}

#[cfg(any(
    feature = "agent-application-reporter-v1",
    feature = "agent-session-power-relay-v1"
))]
impl Held<'_, '_> {
    /// Verify the generation and pipe peer; a failure discards the generation.
    fn verify<S>(
        &mut self,
        stream: &S,
        peer: &impl Fn(&S) -> io::Result<(u32, u32)>,
    ) -> io::Result<()> {
        let Some((generation, pid)) = &self.admitted else {
            return Err(io::Error::other("report generation already discarded"));
        };
        let verified = verify_peer(generation.as_ref(), *pid, stream, peer);
        if verified.is_err() {
            self.admitted = None;
        }
        verified
    }
}

#[cfg(any(
    feature = "agent-application-reporter-v1",
    feature = "agent-session-power-relay-v1"
))]
impl Drop for Held<'_, '_> {
    fn drop(&mut self) {
        if let Some(admitted) = self.admitted.take() {
            *self.slot = Some(admitted);
        }
    }
}

/// The exchange passed the deadline its `before_write` returned.
#[derive(Debug)]
struct DeadlinePassed;

impl std::fmt::Display for DeadlinePassed {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        formatter.write_str("report exchange passed its deadline")
    }
}

impl std::error::Error for DeadlinePassed {}

/// Whether an exchange failed by passing its `before_write` deadline.
#[cfg(feature = "agent-session-power-relay-v1")]
pub(super) fn exchange_deadline_passed(error: &io::Error) -> bool {
    error
        .get_ref()
        .is_some_and(|inner| inner.is::<DeadlinePassed>())
}

/// The installed Service pipe's server identity, the peer port of every
/// production exchange.
#[cfg(feature = "agent-session-power-relay-v1")]
pub(super) fn installed_peer(pipe: &NamedPipeClient) -> io::Result<(u32, u32)> {
    server_identity(pipe.as_raw_handle())
}

fn require_authority(
    authority: Option<&dyn InstalledServiceAuthority>,
) -> io::Result<&dyn InstalledServiceAuthority> {
    authority.ok_or_else(|| {
        io::Error::new(
            io::ErrorKind::Unsupported,
            "installed Service authority is unavailable",
        )
    })
}

fn verify_peer<S>(
    generation: &dyn InstalledServiceGeneration,
    pid: NonZeroU32,
    stream: &S,
    peer: &impl Fn(&S) -> io::Result<(u32, u32)>,
) -> io::Result<()> {
    let verify_generation = || {
        if generation
            .verify_live_process()
            .map_err(ServiceAuthorityError::into_io)?
            != pid
        {
            return Err(super::denied("Service generation changed its identity"));
        }
        Ok(())
    };
    verify_generation()?;
    match peer(stream) {
        Ok(actual) if actual == (pid.get(), 0) => (),
        Ok(_) => return Err(super::denied("unverified installed Service pipe peer")),
        Err(error) => {
            // A kernel peer lookup can race a confirmed original-process exit.
            // Only independent generation proof may classify that as retirement.
            verify_generation()?;
            return Err(unverified(error));
        }
    }
    verify_generation()?;
    Ok(())
}

fn check_deadline(deadline: Instant) -> io::Result<()> {
    if Instant::now() >= deadline {
        return Err(io::ErrorKind::TimedOut.into());
    }
    Ok(())
}

fn observe_before_deadline(snapshot: Snapshot, deadline: Instant) -> io::Result<StatusObservation> {
    let received_at = Instant::now();
    if received_at >= deadline {
        return Err(io::ErrorKind::TimedOut.into());
    }
    Ok(StatusObservation {
        snapshot,
        received_at,
    })
}

pub(super) async fn connect_installed() -> io::Result<NamedPipeClient> {
    let path = production_path();
    loop {
        match ClientOptions::new()
            .security_qos_flags(SECURITY_IDENTIFICATION)
            .open(&path)
        {
            Ok(pipe) => return Ok(pipe),
            Err(error)
                if matches!(
                    error.raw_os_error().map(|code| code as u32),
                    Some(ERROR_FILE_NOT_FOUND | ERROR_PIPE_BUSY)
                ) =>
            {
                sleep(Duration::from_millis(25)).await
            }
            Err(error) => return Err(error),
        }
    }
}

#[cfg(test)]
pub(super) mod tests;

#[cfg(all(test, openblade_agent_endpoint))]
#[test]
fn diagnostic_origin_cause_survives_actual_denied_service_conversion() {
    use openblade_platform_windows::agent_diagnostics::Event;
    let cause = super::origin_error(io::Error::from_raw_os_error(5));
    let error = ServiceAuthorityError::from(cause).into_io();
    assert_eq!(error.kind(), io::ErrorKind::PermissionDenied);
    let mut events = Vec::new();
    super::diagnostics::terminal(&error, |event| events.push(event));
    assert_eq!(
        events,
        [Event::Origin(io::ErrorKind::PermissionDenied, Some(5))]
    );
}

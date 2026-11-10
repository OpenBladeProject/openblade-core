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

// The authority must independently verify and retain the installed LocalSystem
// Service process instance in session zero. Each call checks that the same
// process is still live and authorized, including across a Service restart.
pub(crate) trait InstalledServiceAuthority {
    fn verify_live_process(&self) -> io::Result<NonZeroU32>;
}

// Implement only for a pinned Service generation whose ObserveState path
// independently reads performance or refuses. A v79 status peer alone
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
    watch_on(
        authority,
        canceled,
        request_id,
        connect_installed,
        |pipe| server_identity(pipe.as_raw_handle()),
        publish,
    )
    .await
}

pub(crate) async fn watch_on<S: AsyncRead + AsyncWrite + Unpin>(
    authority: Option<&dyn InstalledServiceAuthority>,
    canceled: impl Future<Output = ()>,
    mut next_id: impl FnMut() -> io::Result<String>,
    mut connect: impl AsyncFnMut() -> io::Result<S>,
    peer: impl Fn(&S) -> io::Result<(u32, u32)>,
    mut publish: impl FnMut(StatusUpdate) -> io::Result<()>,
) -> io::Result<()> {
    let watch = async {
        let authority = require_authority(authority)?;
        let mut delay = INITIAL_RECONNECT_DELAY;
        loop {
            let opened = open_on(authority, &mut next_id, &mut connect, &peer).await;
            let (mut stream, pid, initial) = match opened {
                Ok(opened) => opened,
                Err(error) if retryable(&error) => {
                    publish(StatusUpdate::Reconnecting)?;
                    sleep(delay).await;
                    delay = (delay * 2).min(MAX_RECONNECT_DELAY);
                    continue;
                }
                Err(error) => return Err(error),
            };
            delay = INITIAL_RECONNECT_DELAY;
            publish(StatusUpdate::Opened(initial))?;
            loop {
                let snapshot = match read_status_event(&mut stream).await {
                    Ok(snapshot) => snapshot,
                    Err(error) if retryable(&error) => break,
                    Err(error) => return Err(error),
                };
                verify_peer(authority, pid, &stream, &peer).map_err(unverified)?;
                publish(StatusUpdate::Event(StatusObservation {
                    snapshot,
                    received_at: Instant::now(),
                }))?;
            }
            // Rotation/EOF releases the old stream before an immediate reopen.
            // A replacement initial snapshot resets consumers even if its
            // revision repeats or goes backwards.
            drop(stream);
            tokio::task::yield_now().await;
        }
    };
    tokio::select! {
        biased;
        () = canceled => Err(io::ErrorKind::Interrupted.into()),
        result = watch => result,
    }
}

async fn open_on<S: AsyncRead + AsyncWrite + Unpin>(
    authority: &dyn InstalledServiceAuthority,
    next_id: impl FnOnce() -> io::Result<String>,
    connect: impl AsyncFnOnce() -> io::Result<S>,
    peer: &impl Fn(&S) -> io::Result<(u32, u32)>,
) -> io::Result<(S, NonZeroU32, SubscriptionInitial)> {
    let deadline = Instant::now() + STATUS_BUDGET;
    timeout_at(deadline, async {
        let pid = authority.verify_live_process().map_err(unverified)?;
        let id = next_id().map_err(|error| io::Error::new(io::ErrorKind::InvalidData, error))?;
        let mut stream = connect().await?;
        let snapshot = subscribe(&mut stream, id, |stream| {
            check_deadline(deadline)?;
            verify_peer(authority, pid, stream, peer).map_err(unverified)
        })
        .await?;
        let initial = SubscriptionInitial(observe_before_deadline(snapshot, deadline)?);
        Ok((stream, pid, initial))
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
        let pid = authority.verify_live_process()?;
        let request = request()?;
        let mut stream = connect().await?;
        let snapshot = exchange(&mut stream, &request, None, |stream| {
            check_deadline(deadline)?;
            verify_peer(authority, pid, stream, &peer)
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
    authority: &dyn InstalledServiceAuthority,
    pid: NonZeroU32,
    stream: &S,
    peer: &impl Fn(&S) -> io::Result<(u32, u32)>,
) -> io::Result<()> {
    if peer(stream)? != (pid.get(), 0) || authority.verify_live_process()? != pid {
        return Err(super::denied("unverified installed Service pipe peer"));
    }
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

async fn connect_installed() -> io::Result<NamedPipeClient> {
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

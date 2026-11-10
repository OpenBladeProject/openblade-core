//! Compile-only installed Agent GetStatus adapter. The installed authority is unwired.
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
    service_status::{GetStatus, Snapshot, exchange, production_path, request_id},
};

const STATUS_BUDGET: Duration = Duration::from_secs(2);

// The authority must independently verify and retain the installed LocalSystem
// Service process instance in session zero. Each call checks that the same
// process is still live and authorized, including across a Service restart.
pub(super) trait InstalledServiceAuthority {
    fn verify_live_process(&self) -> io::Result<NonZeroU32>;
}

pub(super) struct StatusObservation {
    pub(super) snapshot: Snapshot,
    received_at: Instant,
}

impl StatusObservation {
    pub(super) fn require_fresh(&self) -> io::Result<()> {
        if self.received_at.elapsed() >= STATUS_BUDGET {
            return Err(io::Error::new(
                io::ErrorKind::TimedOut,
                "stale Service status",
            ));
        }
        Ok(())
    }
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
    let deadline = Instant::now() + STATUS_BUDGET;
    let read = async {
        let authority = authority.ok_or_else(|| {
            io::Error::new(
                io::ErrorKind::Unsupported,
                "installed Service authority is unavailable",
            )
        })?;
        let pid = authority.verify_live_process()?;
        let request = GetStatus::new(next_id()?)?;
        let mut stream = connect().await?;
        let snapshot = exchange(&mut stream, &request, None, |stream| {
            if Instant::now() >= deadline {
                return Err(io::ErrorKind::TimedOut.into());
            }
            if peer(stream)? != (pid.get(), 0) || authority.verify_live_process()? != pid {
                return Err(super::denied("unverified installed Service pipe peer"));
            }
            Ok(())
        })
        .await?;
        let received_at = Instant::now();
        if received_at >= deadline {
            return Err(io::ErrorKind::TimedOut.into());
        }
        Ok(StatusObservation {
            snapshot,
            received_at,
        })
    };
    // The future owns the stream. Canceling drops it even during a partial
    // frame. A failed request is never replayed.
    tokio::select! {
        biased;
        () = canceled => Err(io::ErrorKind::Interrupted.into()),
        result = timeout_at(deadline, read) => result?,
    }
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

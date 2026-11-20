//! Selected installed application-context reporter (managed
//! `ApplicationProfileReporter`). The Agent owns this role because it needs
//! the current user's foreground window and processes (AGENTS.md architecture
//! rule 2). Layers: the policy is `openblade-application-reporter`, foreground
//! observation is the platform `WindowsForegroundWatcher`, `owner` drives them
//! on a dedicated thread, and this module is the transport and lifetime.
//!
//! Like managed, each report uses its own short connection and a failed or
//! refused report is retried only by the policy's five-second renewal with a
//! fresh sequence and request id. Unlike managed, every exchange first proves
//! the installed Service generation and pipe peer, because the request names
//! the user's foreground executable.
use std::{io, sync::Arc, thread};

use openblade_application_reporter::{
    Guid, ReportApplicationContextRequest as Request, ReportOutcome,
};
use openblade_platform_windows::application::watcher::WindowsForegroundWatcher;
use openblade_windows_host::reporter_pipe;
use tokio::sync::{mpsc, oneshot, watch};

use self::owner::{Inbox, Link, Retire, Signals, Timing, Watcher};
use super::service_client::{self, InstalledServiceAuthority};
use crate::service_status::{PROTOCOL_VERSION, random_guid};

mod owner;
mod profiles;
pub(crate) use profiles::{ProfileFeed, Profiles};

// The reporter codec and the Agent's Service client speak one protocol.
const _: () = assert!(PROTOCOL_VERSION == reporter_pipe::VERSION);

pub(super) async fn run(
    origin: Arc<crate::loaded_origin::Origin>,
    stop: watch::Receiver<bool>,
    profiles: watch::Receiver<Profiles>,
    authority: &dyn InstalledServiceAuthority,
) -> io::Result<()> {
    super::verify_origin(&origin)?;
    serve(
        |instance, cancellation| {
            WindowsForegroundWatcher::start(PROTOCOL_VERSION, instance, cancellation)
        },
        profiles,
        stop,
        async |request| service_client::report_application_context(Some(authority), request).await,
        || super::verify_origin(&origin),
        Timing::INSTALLED,
    )
    .await
}

fn new_guid() -> io::Result<Guid> {
    random_guid().map(Guid)
}

async fn stopped(stop: &mut watch::Receiver<bool>) {
    loop {
        if *stop.borrow_and_update() {
            return;
        }
        if stop.changed().await.is_err() {
            return;
        }
    }
}

/// One reporter lifetime. The owner thread is joined before this returns, so
/// retained process handles and the foreground hooks are released before the
/// Agent drops its Origin and status custody. A report that is in flight when
/// the Agent stops is dropped, which closes its connection.
async fn serve<W: Watcher>(
    make: impl FnOnce(Guid, Arc<std::sync::atomic::AtomicBool>) -> io::Result<W> + Send + 'static,
    profiles: watch::Receiver<Profiles>,
    mut stop: watch::Receiver<bool>,
    mut report: impl AsyncFnMut(&Request) -> io::Result<ReportOutcome>,
    verify: impl Fn() -> io::Result<()>,
    timing: Timing,
) -> io::Result<()> {
    let startup = timing.ready;
    let instance = new_guid()?;
    let signals = Arc::new(Signals::new());
    let (requests_tx, mut requests) = mpsc::channel(1);
    let (inbox_tx, inbox) = std::sync::mpsc::sync_channel(1);
    let (ready_tx, ready) = oneshot::channel();
    let (exit_tx, mut exit) = oneshot::channel();
    // From here every exit, including a dropped future, retires the owner.
    let _retire = Retire(Arc::clone(&signals));
    let cancellation = signals.cancellation();
    let link = Link {
        signals: Arc::clone(&signals),
        requests: requests_tx,
        inbox,
        ready: ready_tx,
    };
    let thread = thread::Builder::new()
        .name("OpenBlade application reporter".into())
        .spawn(move || {
            let result = owner::run(
                move || make(instance, cancellation),
                profiles,
                new_guid,
                &timing,
                link,
            );
            let _ = exit_tx.send(result);
        })?;

    tokio::select! {
        biased;
        () = stopped(&mut stop) => {
            // Startup can still be inside a native registration call, which the
            // platform never joins from a wait. Bound this wait by the startup
            // budget so a stuck hook cannot hold the Agent's stop; the owner
            // keeps retiring itself after the Agent has moved on.
            return tokio::time::timeout(
                startup,
                join(&signals, &inbox_tx, None, exit, thread),
            )
            .await
            .unwrap_or_else(|_| {
                Err(io::Error::new(
                    io::ErrorKind::TimedOut,
                    "application reporter startup did not retire",
                ))
            });
        }
        started = ready => {
            // A startup failure detaches the owner, which retires itself; a
            // watcher stuck in the OS must not hold the other roles.
            started.map_err(|_| io::Error::other("application reporter owner exited"))??;
        }
    }

    let mut result = Ok(());
    let mut finished = None;
    loop {
        let request = tokio::select! {
            biased;
            () = stopped(&mut stop) => break,
            owner = &mut exit => {
                // The owner ended before it was asked to: its own error, or a
                // clean exit that nothing requested, is a failure.
                finished = Some(match owner {
                    Ok(Err(error)) => Err(error),
                    _ => Err(io::Error::other("application reporter stopped unexpectedly")),
                });
                break;
            }
            request = requests.recv() => match request {
                Some(request) => request,
                None => break,
            },
        };
        if let Err(error) = verify() {
            result = Err(error);
            break;
        }
        let outcome = tokio::select! {
            biased;
            () = stopped(&mut stop) => break,
            outcome = report(&request) => outcome.unwrap_or(ReportOutcome::TransportFailed),
        };
        if inbox_tx
            .try_send(Inbox::Completed(request.sequence, outcome))
            .is_err()
        {
            break;
        }
    }
    let joined = join(&signals, &inbox_tx, finished, exit, thread).await;
    result.and(joined)
}

/// Retire the owner and wait for it without blocking the runtime. `finished`
/// is the owner's result when it already ended on its own.
async fn join(
    signals: &Signals,
    inbox: &std::sync::mpsc::SyncSender<Inbox>,
    finished: Option<io::Result<()>>,
    exit: oneshot::Receiver<io::Result<()>>,
    thread: thread::JoinHandle<()>,
) -> io::Result<()> {
    signals.retire();
    let _ = inbox.try_send(Inbox::Wake);
    let result = match finished {
        Some(result) => result,
        None => exit
            .await
            .unwrap_or_else(|_| Err(io::Error::other("application reporter owner panicked"))),
    };
    thread
        .join()
        .map_err(|_| io::Error::other("application reporter owner panicked"))?;
    result
}

#[cfg(test)]
mod tests;

use std::{
    future::Future,
    io::{self, BufRead, Read, Write},
    sync::Arc,
};

use tokio::{
    net::windows::named_pipe::NamedPipeServer,
    runtime::Builder,
    sync::{Semaphore, mpsc, oneshot},
    task::JoinSet,
    time::{sleep, timeout},
};

use windows_sys::Win32::Foundation::{
    ERROR_BROKEN_PIPE, ERROR_NO_DATA, ERROR_PIPE_BUSY, ERROR_PIPE_NOT_CONNECTED,
};

use crate::runtime::DurableUpdate;
use crate::wire::{self, Session, Status};
use openblade_windows_host::{configuration_writer::fixture, valid_dev_id};

mod pipe;
mod scm;

const PREFIX: &str = r"\\.\pipe\OpenBlade.Rust.ReadOnly.Dev.";
const SCM_ID: &str = "scm";
const CLIENTS: usize = 8;
const SUBSCRIPTIONS: usize = 4;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum Phase {
    Starting,
    Running,
    Stopping,
    Stopped { failed: bool },
}

pub(super) fn entry() -> io::Result<()> {
    let args: Vec<_> = std::env::args_os().skip(1).collect();
    match args.as_slice() {
        [mode] if mode == "--scm" => scm::dispatch(),
        [mode, id]
            if mode == "foreground-durable"
                || mode == "foreground-durable-interrupt"
                || mode == "foreground-profile-bootstrap"
                || mode == "foreground-profile-migrate" =>
        {
            let id = foreground_id(id)?;
            let mut fixture = fixture::Fixture::create(mode == "foreground-profile-migrate")?;
            let (root, identity) = fixture.restart_key()?;
            let mut output = io::stdout().lock();
            writeln!(output, "ROOT {root} {identity}")?;
            output.flush()?;
            drop(output);
            fixture.hand_off_cleanup();
            let (percent, accepts_steps) = match mode.to_str() {
                Some("foreground-profile-bootstrap") => (fixture.bootstrap()?, false),
                Some("foreground-profile-migrate") => (fixture.migrate()?, false),
                _ => (fixture.seed()?, true),
            };
            let fixture = Arc::new(fixture);
            let interrupt = mode == "foreground-durable-interrupt";
            foreground(id, Session::Active, accepts_steps, |advance, shutdown| {
                let update = advance.map(|advance| {
                    let fixture = Arc::clone(&fixture);
                    DurableUpdate {
                        advance,
                        commit: Box::new(move || fixture.update(interrupt)),
                    }
                });
                let (status, drive) = Status::fixture()?.with_durable(percent, update, shutdown)?;
                Ok((status, Some(drive)))
            })
        }
        [mode, id, root, identity] if mode == "foreground-durable-restart" => {
            let id = foreground_id(id)?;
            let root = foreground_id(root)?;
            let identity = identity
                .to_str()
                .ok_or_else(|| io::Error::other("invalid root identity"))?;
            let percent = fixture::restart(root, identity)?;
            foreground(id, Session::Active, false, |_, shutdown| {
                let (status, drive) = Status::fixture()?.with_durable(percent, None, shutdown)?;
                Ok((status, Some(drive)))
            })
        }
        [mode, id]
            if mode == "foreground"
                || mode == "foreground-background"
                || mode == "foreground-replay" =>
        {
            let id = foreground_id(id)?;
            let session = if mode != "foreground-background" {
                Session::Active
            } else {
                Session::Background
            };
            foreground(id, session, mode == "foreground-replay", replay_status)
        }
        _ => Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "expected a development foreground mode with its hex ID(s), or --scm",
        )),
    }
}

fn foreground_id(id: &std::ffi::OsStr) -> io::Result<&str> {
    id.to_str().filter(|id| valid_dev_id(id)).ok_or_else(|| {
        io::Error::new(
            io::ErrorKind::InvalidInput,
            "expected a 32-digit lowercase hex ID",
        )
    })
}

fn foreground<F: Future<Output = io::Result<()>>>(
    id: &str,
    session: Session,
    accept_steps: bool,
    prepare: impl FnOnce(
        Option<mpsc::Receiver<()>>,
        oneshot::Receiver<()>,
    ) -> io::Result<(Status, Option<F>)>,
) -> io::Result<()> {
    let (mut advance, replay) = if accept_steps {
        let (sender, receiver) = mpsc::channel(1);
        (Some(sender), Some(receiver))
    } else {
        (None, None)
    };
    let (stop_tx, stop_rx) = oneshot::channel();
    let mut stop_tx = Some(stop_tx);
    let runtime = Builder::new_current_thread().enable_all().build()?;
    runtime.block_on(run_prepared(
        id,
        session,
        |shutdown| prepare(replay, shutdown),
        |phase| {
            if phase == Phase::Running {
                // This thread owns stdin. EOF stops replay; one byte stops fixed modes.
                std::thread::Builder::new()
                    .name("development-stop".into())
                    .spawn({
                        let stop = stop_tx.take().ok_or_else(|| {
                            io::Error::other("foreground stop signal already started")
                        })?;
                        let advance = advance.take();
                        move || {
                            let mut input = io::stdin().lock();
                            if let Some(advance) = advance {
                                let mut byte = [0];
                                while input.read_exact(&mut byte).is_ok() {
                                    if byte[0] == b'n' && advance.blocking_send(()).is_err() {
                                        break;
                                    }
                                }
                            } else {
                                let _ = input.fill_buf();
                            }
                            let _ = stop.send(());
                        }
                    })?;
                let mut output = io::stdout().lock();
                writeln!(output, "READY")?;
                output.flush()?;
            }
            Ok(())
        },
        async {
            let _ = stop_rx.await;
        },
    ))
}

async fn run(
    id: &str,
    session: Session,
    replay: Option<mpsc::Receiver<()>>,
    report: impl FnMut(Phase) -> io::Result<()>,
    stop: impl Future<Output = ()>,
) -> io::Result<()> {
    run_prepared(
        id,
        session,
        |shutdown| replay_status(replay, shutdown),
        report,
        stop,
    )
    .await
}

fn replay_status(
    replay: Option<mpsc::Receiver<()>>,
    shutdown: oneshot::Receiver<()>,
) -> io::Result<(Status, Option<impl Future<Output = io::Result<()>>>)> {
    let status = Status::fixture()?;
    Ok(if let Some(replay) = replay {
        let (status, drive) = status.with_replay(replay);
        let drive = async move {
            tokio::select! {
                _ = shutdown => Ok(()),
                result = drive => result,
            }
        };
        (status, Some(drive))
    } else {
        (status, None)
    })
}

async fn run_prepared<F: Future<Output = io::Result<()>>>(
    id: &str,
    session: Session,
    prepare: impl FnOnce(oneshot::Receiver<()>) -> io::Result<(Status, Option<F>)>,
    mut report: impl FnMut(Phase) -> io::Result<()>,
    stop: impl Future<Output = ()>,
) -> io::Result<()> {
    let result = async {
        report(Phase::Starting)?;
        let (shutdown, retiring) = oneshot::channel();
        let (status, drive) = prepare(retiring)?;
        let endpoint = pipe::Endpoint::new(format!("{PREFIX}{id}"))?;
        let claim = endpoint.claim()?;
        let listener = endpoint.listen()?;
        report(Phase::Running)?;

        let mut clients = JoinSet::new();
        let result = {
            let serve = accept(
                listener,
                &endpoint,
                session,
                Arc::new(status),
                &mut clients,
                stop,
            );
            let drive = async {
                match drive {
                    Some(drive) => drive.await,
                    None => Ok(()),
                }
            };
            tokio::pin!(serve, drive);
            tokio::select! {
                biased;
                result = &mut serve => {
                    let _ = shutdown.send(());
                    result.and(drive.await)
                }
                result = &mut drive => {
                    match result {
                        Ok(()) => serve.await,
                        Err(error) => Err(error),
                    }
                }
            }
        };
        let stopping = report(Phase::Stopping);
        clients.abort_all();
        while clients.join_next().await.is_some() {}
        drop(claim);
        result.and(stopping)
    }
    .await;
    let stopped = report(Phase::Stopped {
        failed: result.is_err(),
    });
    result.and(stopped)
}

async fn accept(
    mut listener: NamedPipeServer,
    endpoint: &pipe::Endpoint,
    session: Session,
    status: Arc<Status>,
    clients: &mut JoinSet<()>,
    stop: impl Future<Output = ()>,
) -> io::Result<()> {
    let subscriptions = Arc::new(Semaphore::new(SUBSCRIPTIONS));
    let identity = Arc::clone(&endpoint.identity);
    tokio::pin!(stop);
    loop {
        let connected = tokio::select! {
            biased;
            () = &mut stop => return Ok(()),
            joined = clients.join_next(), if !clients.is_empty() => {
                if let Some(Err(error)) = joined {
                    let _ = writeln!(io::stderr().lock(), "Development pipe task: {error}");
                }
                continue;
            }
            connected = listener.connect() => connected,
        };

        match connected {
            Ok(()) if clients.len() < CLIENTS => {
                let client = listener;
                let status = Arc::clone(&status);
                let identity = Arc::clone(&identity);
                let subscriptions = Arc::clone(&subscriptions);
                clients.spawn(async move {
                    let authorize =
                        |pipe: &NamedPipeServer| identity.authorize(pipe).map(|()| session);
                    let _ = wire::serve(client, authorize, &status, &subscriptions).await;
                });
            }
            Ok(()) => drop(listener), // Promptly close a connection above the handler limit.
            Err(error) if stale_connection(&error) => drop(listener),
            Err(error) => return Err(error),
        }

        // Retire the listener instead of reusing it. Tokio can already have
        // buffered bytes from its previous peer. The private claim holds the
        // name throughout the replacement.
        listener = tokio::select! {
            biased;
            () = &mut stop => return Ok(()),
            next = next_listener(endpoint) => next?,
        };
    }
}

fn stale_connection(error: &io::Error) -> bool {
    matches!(
        error.raw_os_error().map(|code| code as u32),
        Some(ERROR_BROKEN_PIPE | ERROR_NO_DATA | ERROR_PIPE_NOT_CONNECTED)
    )
}

async fn next_listener(endpoint: &pipe::Endpoint) -> io::Result<NamedPipeServer> {
    use std::time::Duration;

    timeout(Duration::from_secs(2), async {
        loop {
            match endpoint.listen() {
                Err(error) if error.raw_os_error() == Some(ERROR_PIPE_BUSY as i32) => {
                    // A retired overlapped operation can briefly retain its handle.
                    sleep(Duration::from_millis(20)).await;
                }
                result => return result,
            }
        }
    })
    .await?
}

#[cfg(test)]
mod tests {
    use std::{
        sync::atomic::{AtomicU32, Ordering},
        time::{Duration, SystemTime, UNIX_EPOCH},
    };

    use tokio::{
        io::{AsyncReadExt, AsyncWriteExt},
        net::windows::named_pipe::ClientOptions,
        time::timeout,
    };

    use super::*;

    fn test_id() -> String {
        let time = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap()
            .as_nanos();
        static NEXT: AtomicU32 = AtomicU32::new(0);
        let sequence = NEXT.fetch_add(1, Ordering::Relaxed);
        format!(
            "{:08x}{:016x}{sequence:08x}",
            std::process::id(),
            time as u64
        )
    }

    fn runtime() -> tokio::runtime::Runtime {
        Builder::new_current_thread().enable_all().build().unwrap()
    }

    #[test]
    fn foreground_names_are_confined_to_the_development_namespace() {
        assert!(valid_dev_id("00000000000000000000000000000000"));
        for invalid in [
            "scm",
            "OpenBlade.Service.v2",
            r"\\.\pipe\OpenBlade.Service.v2",
            "0000000000000000000000000000000A",
            "0000000000000000000000000000000\\",
        ] {
            assert!(!valid_dev_id(invalid));
        }
    }

    #[test]
    fn fake_scm_observes_readiness_and_shutdown_of_native_endpoint() {
        runtime().block_on(async {
            let id = test_id();
            let path = format!("{PREFIX}{id}");
            let (stop, stop_requested) = oneshot::channel();
            let (ready, wait_ready) = oneshot::channel();
            let mut ready = Some(ready);
            let mut phases = Vec::new();
            let host = run(
                &id,
                Session::Active,
                None,
                |phase| {
                    phases.push(phase);
                    match phase {
                        Phase::Starting | Phase::Stopped { .. } => {
                            assert_eq!(
                                ClientOptions::new().open(&path).unwrap_err().kind(),
                                io::ErrorKind::NotFound
                            );
                        }
                        Phase::Running => ready.take().unwrap().send(()).unwrap(),
                        Phase::Stopping => {}
                    }
                    Ok(())
                },
                async {
                    stop_requested.await.unwrap();
                },
            );

            let client = async {
                wait_ready.await.unwrap();
                let mut pipe = ClientOptions::new().open(&path).unwrap();
                // Leave an incomplete prefix on the wire when STOP arrives.
                pipe.write_all(&[10]).await.unwrap();
                tokio::task::yield_now().await;
                stop.send(()).unwrap();
                let mut byte = [0];
                let result = pipe.read(&mut byte).await;
                match result {
                    Ok(0) => {}
                    Err(error) if error.kind() == io::ErrorKind::BrokenPipe => {}
                    result => panic!("shutdown left a readable or unexpected pipe: {result:?}"),
                }
            };
            let (result, ()) =
                timeout(Duration::from_secs(5), async { tokio::join!(host, client) })
                    .await
                    .unwrap();
            result.unwrap();
            assert_eq!(
                phases,
                [
                    Phase::Starting,
                    Phase::Running,
                    Phase::Stopping,
                    Phase::Stopped { failed: false }
                ]
            );
        });
    }

    #[test]
    fn occupied_endpoint_fails_before_running_and_does_not_replace_owner() {
        runtime().block_on(async {
            let id = test_id();
            let path = format!("{PREFIX}{id}");
            let endpoint = pipe::Endpoint::new(path.clone()).unwrap();
            let _claim = endpoint.claim().unwrap();
            let _owner = endpoint.listen().unwrap();
            let mut phases = Vec::new();
            let result = run(
                &id,
                Session::Active,
                None,
                |phase| {
                    phases.push(phase);
                    Ok(())
                },
                std::future::pending(),
            )
            .await;
            assert_eq!(result.unwrap_err().kind(), io::ErrorKind::PermissionDenied);
            assert_eq!(phases, [Phase::Starting, Phase::Stopped { failed: true }]);
            assert!(ClientOptions::new().open(&path).is_ok());
        });
    }

    #[test]
    fn capacity_retires_overflow_without_waiting_for_partial_frame_deadlines() {
        runtime().block_on(async {
            let id = test_id();
            let path = format!("{PREFIX}{id}");
            let (stop, stop_requested) = oneshot::channel();
            let (ready, wait_ready) = oneshot::channel();
            let mut ready = Some(ready);
            let host = run(
                &id,
                Session::Active,
                None,
                |phase| {
                    if phase == Phase::Running {
                        ready.take().unwrap().send(()).unwrap();
                    }
                    Ok(())
                },
                async {
                    stop_requested.await.unwrap();
                },
            );
            let client = async {
                wait_ready.await.unwrap();
                let mut pending = Vec::new();
                for _ in 0..CLIENTS {
                    let mut client = open_pipe(&path).await;
                    client.write_all(&[10]).await.unwrap();
                    pending.push(client);
                }
                let mut overflow = open_pipe(&path).await;
                let mut byte = [0];
                let result = timeout(Duration::from_secs(1), overflow.read(&mut byte))
                    .await
                    .expect("overflow waited for an active handler to finish");
                assert_closed(result);

                // The owner must still be running and serviceable after the
                // eight stalled first frames time out and free their handlers.
                let result = pending[0].read(&mut byte).await;
                assert_closed(result);
                drop(pending);
                read_status(&path).await;
                stop.send(()).unwrap();
            };
            let (result, ()) =
                timeout(Duration::from_secs(8), async { tokio::join!(host, client) })
                    .await
                    .unwrap();
            result.unwrap();
        });
    }

    #[test]
    fn transient_instance_exhaustion_preserves_the_claim_and_retries() {
        runtime().block_on(async {
            let path = format!("{PREFIX}{}", test_id());
            let endpoint = pipe::Endpoint::new(path).unwrap();
            let _claim = endpoint.claim().unwrap();
            let mut instances: Vec<_> = (0..=CLIENTS).map(|_| endpoint.listen().unwrap()).collect();
            assert_eq!(
                endpoint.listen().unwrap_err().raw_os_error(),
                Some(ERROR_PIPE_BUSY as i32)
            );
            let release = async {
                sleep(Duration::from_millis(50)).await;
                drop(instances.pop());
            };
            let (replacement, ()) = tokio::join!(next_listener(&endpoint), release);
            drop(replacement.unwrap());
            drop(instances);
            assert_eq!(
                endpoint.claim().unwrap_err().kind(),
                io::ErrorKind::PermissionDenied
            );
        });
    }

    #[test]
    fn client_that_closes_before_accept_does_not_retire_endpoint() {
        runtime().block_on(async {
            let id = test_id();
            let path = format!("{PREFIX}{id}");
            let (stop, stop_requested) = oneshot::channel();
            let (ready, wait_ready) = oneshot::channel();
            let mut ready = Some(ready);
            let host = run(
                &id,
                Session::Active,
                None,
                |phase| {
                    if phase == Phase::Running {
                        // Open and close a client before the accept loop can poll.
                        drop(ClientOptions::new().open(&path).unwrap());
                        ready.take().unwrap().send(()).unwrap();
                    }
                    Ok(())
                },
                async {
                    stop_requested.await.unwrap();
                },
            );
            let client = async {
                wait_ready.await.unwrap();
                read_status(&path).await;
                stop.send(()).unwrap();
            };
            let (result, ()) =
                timeout(Duration::from_secs(5), async { tokio::join!(host, client) })
                    .await
                    .unwrap();
            result.unwrap();
        });
    }

    async fn open_pipe(path: &str) -> tokio::net::windows::named_pipe::NamedPipeClient {
        loop {
            match ClientOptions::new().open(path) {
                Err(error) if error.raw_os_error() == Some(ERROR_PIPE_BUSY as i32) => {
                    sleep(Duration::from_millis(5)).await;
                }
                result => return result.unwrap(),
            }
        }
    }

    async fn read_status(path: &str) {
        let mut pipe = open_pipe(path).await;
        let request = br#"{"operation":"getStatus","version":79,"requestId":"11111111-2222-4333-8444-555555555555"}"#;
        pipe.write_all(&(request.len() as i32).to_le_bytes())
            .await
            .unwrap();
        pipe.write_all(request).await.unwrap();
        assert!((1..=65_536).contains(&pipe.read_i32_le().await.unwrap()));
    }

    fn assert_closed(result: io::Result<usize>) {
        match result {
            Ok(0) => {}
            Err(error) if stale_connection(&error) => {}
            result => panic!("expected closed pipe, got {result:?}"),
        }
    }

    #[test]
    fn anonymous_qos_cannot_read_cached_status() {
        runtime().block_on(async {
            let id = test_id();
            let path = format!("{PREFIX}{id}");
            let (stop, stop_requested) = oneshot::channel();
            let (ready, wait_ready) = oneshot::channel();
            let mut ready = Some(ready);
            let host = run(&id, Session::Active, None, |phase| {
                if phase == Phase::Running {
                    ready.take().unwrap().send(()).unwrap();
                }
                Ok(())
            }, async { stop_requested.await.unwrap(); });
            let client = async {
                wait_ready.await.unwrap();
                let mut pipe = ClientOptions::new().security_qos_flags(0) // SecurityAnonymous
                    .open(&path).unwrap();
                let request = br#"{"operation":"getStatus","version":79,"requestId":"11111111-2222-4333-8444-555555555555"}"#;
                pipe.write_all(&(request.len() as i32).to_le_bytes()).await.unwrap();
                pipe.write_all(request).await.unwrap();
                let mut byte = [0];
                let result = pipe.read(&mut byte).await;
                match result {
                    Ok(0) => {}
                    Err(error) if error.kind() == io::ErrorKind::BrokenPipe => {}
                    result => panic!("anonymous peer received a response or unexpected error: {result:?}"),
                }
                stop.send(()).unwrap();
            };
            let (result, ()) = timeout(Duration::from_secs(5), async { tokio::join!(host, client) }).await.unwrap();
            result.unwrap();
        });
    }
}

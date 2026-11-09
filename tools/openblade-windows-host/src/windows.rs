use std::{
    future::Future,
    io::{self, BufRead, Write},
    sync::Arc,
};

use tokio::{
    net::windows::named_pipe::NamedPipeServer,
    runtime::Builder,
    sync::{Semaphore, oneshot},
    task::JoinSet,
};

use crate::wire::{self, Session, Status};

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
        [mode, id] if mode == "foreground" || mode == "foreground-background" => {
            let id = id.to_str().filter(|id| valid_id(id)).ok_or_else(|| {
                io::Error::new(
                    io::ErrorKind::InvalidInput,
                    "expected a 32-digit lowercase hex ID",
                )
            })?;
            let session = if mode == "foreground" {
                Session::Active
            } else {
                Session::Background
            };

            let (stop_tx, stop_rx) = oneshot::channel();
            let mut stop_tx = Some(stop_tx);
            let runtime = Builder::new_current_thread().enable_all().build()?;
            runtime.block_on(run(
                id,
                session,
                |phase| {
                    if phase == Phase::Running {
                        // One byte or EOF is the harness-owned stop signal.
                        std::thread::Builder::new()
                            .name("development-stop".into())
                            .spawn({
                                let stop = stop_tx.take().ok_or_else(|| {
                                    io::Error::other("foreground stop signal already started")
                                })?;
                                move || {
                                    let _ = io::stdin().lock().fill_buf();
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
        _ => Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "expected foreground <hex-id>, foreground-background <hex-id>, or --scm",
        )),
    }
}

fn valid_id(id: &str) -> bool {
    id.len() == 32
        && id
            .bytes()
            .all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte))
}

async fn run(
    id: &str,
    session: Session,
    mut report: impl FnMut(Phase) -> io::Result<()>,
    stop: impl Future<Output = ()>,
) -> io::Result<()> {
    let result = async {
        report(Phase::Starting)?;
        let status = Arc::new(Status::fixture()?);
        let endpoint = pipe::Endpoint::new(format!("{PREFIX}{id}"))?;
        let listener = endpoint.listen(true)?;
        report(Phase::Running)?;

        let mut clients = JoinSet::new();
        let result = accept(listener, &endpoint, session, status, &mut clients, stop).await;
        let stopping = report(Phase::Stopping);
        clients.abort_all();
        while clients.join_next().await.is_some() {}
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
        tokio::select! {
            biased;
            () = &mut stop => return Ok(()),
            joined = clients.join_next(), if !clients.is_empty() => {
                if let Some(Err(error)) = joined {
                    return Err(io::Error::other(error));
                }
            }
            connected = listener.connect(), if clients.len() < CLIENTS => {
                connected?;
                // Retain at least one server handle across every accepted connection.
                let next = endpoint.listen(false)?;
                let client = std::mem::replace(&mut listener, next);
                let status = Arc::clone(&status);
                let identity = Arc::clone(&identity);
                let subscriptions = Arc::clone(&subscriptions);
                clients.spawn(async move {
                    let authorize = |pipe: &NamedPipeServer| identity.authorize(pipe).map(|()| session);
                    // Invalid input, timeout and client disconnect only retire this connection.
                    let _ = wire::serve(client, authorize, &status, &subscriptions).await;
                });
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use std::time::{Duration, SystemTime, UNIX_EPOCH};

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
        format!("{:08x}{time:024x}", std::process::id())
    }

    fn runtime() -> tokio::runtime::Runtime {
        Builder::new_current_thread().enable_all().build().unwrap()
    }

    #[test]
    fn foreground_names_are_confined_to_the_development_namespace() {
        assert!(valid_id("00000000000000000000000000000000"));
        for invalid in [
            "scm",
            "OpenBlade.Service.v2",
            r"\\.\pipe\OpenBlade.Service.v2",
            "0000000000000000000000000000000A",
            "0000000000000000000000000000000\\",
        ] {
            assert!(!valid_id(invalid));
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
            let _owner = endpoint.listen(true).unwrap();
            let mut phases = Vec::new();
            let result = run(
                &id,
                Session::Active,
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
    fn anonymous_qos_cannot_read_cached_status() {
        runtime().block_on(async {
            let id = test_id();
            let path = format!("{PREFIX}{id}");
            let (stop, stop_requested) = oneshot::channel();
            let (ready, wait_ready) = oneshot::channel();
            let mut ready = Some(ready);
            let host = run(&id, Session::Active, |phase| {
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

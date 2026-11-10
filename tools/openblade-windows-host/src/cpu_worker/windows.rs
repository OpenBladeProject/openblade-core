//! Windows transport for the development protocol fixture.

use std::{
    future::Future,
    io,
    path::{Path, PathBuf},
    pin::Pin,
    sync::{Arc, Mutex},
    task::{Context, Poll, ready},
    time::Duration,
};

use tokio::{
    io::{AsyncRead, AsyncWrite, Join, ReadBuf},
    net::windows::named_pipe::NamedPipeServer,
    sync::{oneshot, watch},
    time::{Instant, sleep, timeout},
};

use super::{Diagnostics, Exit, PrivateChild, Readiness, STOP_TIMEOUT, Sample, Supervisor};

mod native;

const FIXTURE_NAME: &str = "openblade-cpu-worker-fixture.exe";
const EXIT_POLL_INTERVAL: Duration = Duration::from_millis(10);
type Pipes = Join<NamedPipeServer, NamedPipeServer>;
type PipeDelivery = oneshot::Receiver<Result<Pipes, Fault>>;

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum Stage {
    Image,
    Pipe,
    Stderr,
    CreateJob,
    ConfigureJob,
    HandleList,
    JobList,
    CreateProcessWithJob,
    RegisterPipe,
    LaunchTask,
    ObserveExit,
    Read,
    Write,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct Fault {
    pub stage: Stage,
    pub win32: Option<i32>,
    kind: io::ErrorKind,
}

impl Fault {
    fn new(stage: Stage, error: io::Error) -> Self {
        Self {
            stage,
            win32: error.raw_os_error(),
            kind: error.kind(),
        }
    }

    fn as_io(self) -> io::Error {
        self.win32
            .map_or_else(|| self.kind.into(), io::Error::from_raw_os_error)
    }
}

#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct NativeDiagnostics {
    pub transport: Option<Fault>,
    pub observation: Option<Fault>,
}

type FaultLog = Arc<Mutex<NativeDiagnostics>>;

/// Development-only entry point. The caller supplies its own fresh, trusted
/// fixture build directory. The image name is fixed and there is no argument
/// or environment override.
pub struct FixtureSupervisor {
    directory: PathBuf,
    supervisor: Supervisor<FixtureIo>,
    faults: FaultLog,
}

impl FixtureSupervisor {
    pub fn new(directory: &Path) -> io::Result<Self> {
        if !directory.is_absolute() {
            return Err(io::ErrorKind::InvalidInput.into());
        }
        Ok(Self {
            directory: directory.into(),
            supervisor: Supervisor::default(),
            faults: FaultLog::default(),
        })
    }

    pub async fn prepare(&mut self) -> bool {
        self.prepare_readiness().await.is_some()
    }

    pub async fn prepare_readiness(&mut self) -> Option<Readiness> {
        self.supervisor
            .prepare_readiness(|| attach(&self.directory, &self.faults))
            .await
    }

    pub async fn sample(&mut self) -> Option<Sample> {
        self.supervisor
            .sample(|| attach(&self.directory, &self.faults))
            .await
    }

    pub fn diagnostics(&self) -> (Diagnostics, NativeDiagnostics) {
        (self.supervisor.diagnostics(), *self.faults.lock().unwrap())
    }

    /// Request retirement and wait briefly for proof from the original process
    /// handle. A missed deadline leaves the lease fenced and the observer alive.
    pub async fn retire(&mut self) -> bool {
        let Some(lease) = &mut self.supervisor.lease else {
            return true;
        };
        lease.stop(Instant::now());
        timeout(
            STOP_TIMEOUT,
            lease.exit.wait_for(|state| *state == Exit::Confirmed),
        )
        .await
        .is_ok_and(|result| result.is_ok())
    }
}

fn attach(directory: &Path, faults: &FaultLog) -> io::Result<PrivateChild<FixtureIo>> {
    let (stop, stop_rx) = watch::channel(None);
    let (exit_tx, exit) = watch::channel(Exit::Pending);
    let (pipe_tx, pipe_rx) = oneshot::channel();
    *faults.lock().unwrap() = NativeDiagnostics::default();
    let log = faults.clone();
    let directory = directory.to_owned();
    tokio::spawn(run_generation(
        async move {
            match tokio::task::spawn_blocking(move || native::launch(&directory)).await {
                Ok(Ok((owner, pipes))) => Launch::Started(owner, pipes.register()),
                Ok(Err(fault)) => Launch::NoChild(fault),
                Err(_) => {
                    Launch::Uncertain(Fault::new(Stage::LaunchTask, io::ErrorKind::Other.into()))
                }
            }
        },
        stop_rx,
        exit_tx,
        pipe_tx,
        log,
    ));
    Ok(PrivateChild {
        io: FixtureIo {
            pipes: None,
            delivery: pipe_rx,
            faults: faults.clone(),
        },
        stop,
        exit,
    })
}

trait ProcessOwner {
    fn close_job(&mut self);
    fn exited(&self) -> Result<bool, Fault>;
}

enum Launch<P, S> {
    Started(P, Result<S, Fault>),
    NoChild(Fault),
    Uncertain(Fault),
}

async fn run_generation<P: ProcessOwner, S>(
    launch: impl Future<Output = Launch<P, S>>,
    mut stop: watch::Receiver<Option<Instant>>,
    exit: watch::Sender<Exit>,
    delivery: oneshot::Sender<Result<S, Fault>>,
    faults: FaultLog,
) {
    let (mut owner, pipes) = match launch.await {
        Launch::Started(owner, pipes) => (owner, pipes),
        other => {
            let (fault, status) = match other {
                Launch::NoChild(fault) => (fault, Exit::Confirmed),
                Launch::Uncertain(fault) => (fault, Exit::Unobservable),
                Launch::Started(..) => unreachable!(),
            };
            faults.lock().unwrap().transport = Some(fault);
            exit.send_replace(status);
            let _ = delivery.send(Err(fault));
            return;
        }
    };
    let mut stopping = match pipes {
        Err(fault) => {
            faults.lock().unwrap().transport = Some(fault);
            let _ = delivery.send(Err(fault));
            true
        }
        Ok(io) => stop_requested(&stop) || delivery.send(Ok(io)).is_err(),
    };
    if stopping {
        owner.close_job();
    }

    loop {
        match owner.exited() {
            Ok(true) => {
                // Finish closing the generation before enabling replacement.
                owner.close_job();
                drop(owner);
                exit.send_replace(Exit::Confirmed);
                return;
            }
            Ok(false) => {}
            Err(fault) => {
                faults.lock().unwrap().observation = Some(fault);
                exit.send_replace(Exit::Unobservable);
                if !stopping {
                    owner.close_job();
                    stopping = true;
                }
            }
        }
        if stopping {
            sleep(EXIT_POLL_INTERVAL).await;
        } else {
            tokio::select! {
                _ = sleep(EXIT_POLL_INTERVAL) => {}
                _ = stop.changed() => {}
            }
            if stop_requested(&stop) {
                owner.close_job();
                stopping = true;
            }
        }
    }
}

fn stop_requested(stop: &watch::Receiver<Option<Instant>>) -> bool {
    stop.has_changed().is_err() || stop.borrow().is_some()
}

struct FixtureIo {
    pipes: Option<Pipes>,
    delivery: PipeDelivery,
    faults: FaultLog,
}

impl FixtureIo {
    fn poll_pipes(&mut self, cx: &mut Context<'_>) -> Poll<io::Result<&mut Pipes>> {
        if self.pipes.is_none() {
            self.pipes = Some(match ready!(Pin::new(&mut self.delivery).poll(cx)) {
                Ok(Ok(pipes)) => pipes,
                Ok(Err(fault)) => return Poll::Ready(Err(fault.as_io())),
                Err(_) => return Poll::Ready(Err(io::ErrorKind::BrokenPipe.into())),
            });
        }
        Poll::Ready(Ok(self.pipes.as_mut().unwrap()))
    }

    fn record<T>(&self, stage: Stage, result: Poll<io::Result<T>>) -> Poll<io::Result<T>> {
        if let Poll::Ready(Err(error)) = &result {
            let mut faults = self.faults.lock().unwrap();
            faults.transport.get_or_insert_with(|| {
                Fault::new(
                    stage,
                    error
                        .raw_os_error()
                        .map_or_else(|| error.kind().into(), io::Error::from_raw_os_error),
                )
            });
        }
        result
    }
}

impl AsyncRead for FixtureIo {
    fn poll_read(
        mut self: Pin<&mut Self>,
        cx: &mut Context<'_>,
        buf: &mut ReadBuf<'_>,
    ) -> Poll<io::Result<()>> {
        let result = Pin::new(ready!(self.poll_pipes(cx))?).poll_read(cx, buf);
        self.record(Stage::Read, result)
    }
}

impl AsyncWrite for FixtureIo {
    fn poll_write(
        mut self: Pin<&mut Self>,
        cx: &mut Context<'_>,
        buf: &[u8],
    ) -> Poll<io::Result<usize>> {
        let result = Pin::new(ready!(self.poll_pipes(cx))?).poll_write(cx, buf);
        self.record(Stage::Write, result)
    }

    fn poll_flush(mut self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<io::Result<()>> {
        let result = Pin::new(ready!(self.poll_pipes(cx))?).poll_flush(cx);
        self.record(Stage::Write, result)
    }

    fn poll_shutdown(mut self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<io::Result<()>> {
        Pin::new(ready!(self.poll_pipes(cx))?).poll_shutdown(cx)
    }
}

#[cfg(test)]
mod tests;

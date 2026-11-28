use super::LIT;
use std::{
    io,
    sync::{
        Arc,
        atomic::{AtomicU8, Ordering},
    },
    time::Duration,
};
use tokio::{
    sync::{OwnedSemaphorePermit, mpsc, oneshot, watch},
    task::JoinHandle,
    time::timeout,
};

pub(super) const RETIRE_LIMIT: Duration = Duration::from_secs(60);

pub(super) enum Renderer {
    Owned(Producer),
    Retiring(watch::Receiver<bool>),
    Uncertain,
}

pub(super) struct Producer {
    admit_frame: mpsc::Sender<()>,
    cancel: oneshot::Sender<()>,
    worker: JoinHandle<bool>,
}

pub(super) struct Script {
    pub(super) started: oneshot::Receiver<()>,
    pub(super) complete_frame: oneshot::Sender<()>,
    pub(super) frame_done: oneshot::Receiver<()>,
    pub(super) release_port: oneshot::Sender<bool>,
}

impl Renderer {
    pub(super) fn start(
        permit: OwnedSemaphorePermit,
        brightness: Arc<AtomicU8>,
    ) -> io::Result<(Self, Script)> {
        let (admit_frame, mut frames) = mpsc::channel(1);
        let (cancel, canceled) = oneshot::channel();
        let (frame_started, started) = oneshot::channel();
        let (complete_frame, finish_frame) = oneshot::channel();
        let (frame_finished, frame_done) = oneshot::channel();
        let (release_port, port_released) = oneshot::channel();

        let worker = tokio::spawn(async move {
            // The worker owns the simulated port throughout frame submission,
            // cancellation and native release. Cancellation never aborts the
            // outstanding operation or releases the device lane early.
            let _port = permit;
            if frames.recv().await.is_none() {
                return false;
            }
            let _ = frame_started.send(());
            if finish_frame.await.is_err() {
                return false;
            }
            brightness.store(LIT, Ordering::Relaxed);
            let _ = frame_finished.send(());
            if canceled.await.is_err() {
                return false;
            }
            port_released.await.unwrap_or(false)
        });
        let renderer = Self::Owned(Producer {
            admit_frame,
            cancel,
            worker,
        });
        renderer.submit()?;
        Ok((
            renderer,
            Script {
                started,
                complete_frame,
                frame_done,
                release_port,
            },
        ))
    }

    pub(super) fn submit(&self) -> io::Result<()> {
        let Self::Owned(producer) = self else {
            return Err(io::Error::other("Renderer frame admission is closed."));
        };
        producer.admit_frame.try_send(()).map_err(io::Error::other)
    }

    pub(super) fn close(&mut self, done: watch::Receiver<bool>) -> io::Result<Producer> {
        match std::mem::replace(self, Self::Retiring(done)) {
            Self::Owned(producer) => Ok(producer),
            previous => {
                *self = previous;
                Err(io::Error::other("Renderer already closing."))
            }
        }
    }

    pub(super) fn closed(&self) -> bool {
        !matches!(self, Self::Owned(_))
    }
}

impl Producer {
    pub(super) async fn retire(self, limit: Duration) -> bool {
        let Self {
            admit_frame,
            cancel,
            worker,
        } = self;
        drop(admit_frame);
        let _ = cancel.send(());
        // A timeout latches uncertainty. The task still owns its permit until
        // it finishes; dropping the join handle does not abort that task.
        matches!(timeout(limit, worker).await, Ok(Ok(true)))
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use tokio::sync::Semaphore;

    #[tokio::test]
    async fn retirement_deadline_keeps_the_port_owned_until_the_worker_exits() {
        let lane = Arc::new(Semaphore::new(1));
        let brightness = Arc::new(AtomicU8::new(0));
        let permit = Arc::clone(&lane).try_acquire_owned().unwrap();
        let (mut renderer, script) = Renderer::start(permit, Arc::clone(&brightness)).unwrap();
        script.started.await.unwrap();

        let (_, done) = watch::channel(false);
        let worker = renderer.close(done).unwrap();
        assert!(renderer.submit().is_err());
        assert!(!worker.retire(Duration::ZERO).await);
        assert!(lane.try_acquire().is_err());
        assert_eq!(brightness.load(Ordering::Relaxed), 0);

        script.complete_frame.send(()).unwrap();
        script.frame_done.await.unwrap();
        assert_eq!(brightness.load(Ordering::Relaxed), LIT);
        assert!(lane.try_acquire().is_err());

        script.release_port.send(true).unwrap();
        assert!(
            timeout(Duration::from_secs(2), lane.acquire())
                .await
                .unwrap()
                .is_ok()
        );
        assert!(renderer.submit().is_err());
    }
}

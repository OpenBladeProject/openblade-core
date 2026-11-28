//! Feature lifetime shared by the installed monitor and injected supervision tests.
use std::{
    future::{Future, poll_fn},
    io,
    pin::Pin,
    task::Poll,
};
use tokio::sync::watch;

/// One selected endpoint, started with the shared feature stop signal.
pub(super) type Feature<'a> = Box<
    dyn FnOnce(watch::Receiver<bool>) -> Pin<Box<dyn Future<Output = io::Result<()>> + 'a>> + 'a,
>;

pub(super) fn feature<'a, F: Future<Output = io::Result<()>> + 'a>(
    start: impl FnOnce(watch::Receiver<bool>) -> F + 'a,
) -> Feature<'a> {
    Box::new(move |stop| Box::pin(start(stop)))
}

/// The features share one stop signal, and each must positively finish.
/// Stop, status/origin loss or the first finished feature signals all of them;
/// every other feature is then drained to completion, so one failed drain
/// cannot drop another feature's in-flight blocking work. The first result
/// is reported, then the first drain failure.
pub(super) async fn run_all<'a>(
    stop: impl Future<Output = io::Result<()>>,
    status: impl Future<Output = io::Result<()>>,
    features: Vec<Feature<'a>>,
) -> io::Result<()> {
    let (signal, receiver) = watch::channel(false);
    let mut running: Vec<_> = features
        .into_iter()
        .map(|start| Some(start(receiver.clone())))
        .collect();
    drop(receiver);
    tokio::pin!(stop, status);
    let result = poll_fn(|context| {
        if let Poll::Ready(result) = stop.as_mut().poll(context) {
            return Poll::Ready(result);
        }
        if let Poll::Ready(result) = status.as_mut().poll(context) {
            return Poll::Ready(result);
        }
        for slot in &mut running {
            if let Some(feature) = slot
                && let Poll::Ready(result) = feature.as_mut().poll(context)
            {
                *slot = None;
                return Poll::Ready(result);
            }
        }
        Poll::Pending
    })
    .await;
    let _ = signal.send(true);
    let mut drained = Ok(());
    poll_fn(|context| {
        let mut pending = false;
        for slot in &mut running {
            if let Some(feature) = slot {
                match feature.as_mut().poll(context) {
                    Poll::Ready(result) => {
                        *slot = None;
                        if drained.is_ok() {
                            drained = result;
                        }
                    }
                    Poll::Pending => pending = true,
                }
            }
        }
        if pending {
            Poll::Pending
        } else {
            Poll::Ready(())
        }
    })
    .await;
    result.and(drained)
}

/// Wait until `stop` is signaled or its sender is gone.
pub(super) async fn stopped(stop: &mut watch::Receiver<bool>) {
    loop {
        if *stop.borrow_and_update() {
            return;
        }
        if stop.changed().await.is_err() {
            return;
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::{cell::Cell, future::pending};
    #[tokio::test]
    async fn stop_and_status_failure_join_feature_before_owner_release() {
        for status_failure in [false, true] {
            let joined = Cell::new(false);
            let stop = async {
                if status_failure {
                    pending().await
                } else {
                    Ok(())
                }
            };
            let status = async {
                if status_failure {
                    Err(io::Error::other("status origin lost"))
                } else {
                    pending().await
                }
            };
            let result = run_all(
                stop,
                status,
                vec![feature(|mut canceled| {
                    let joined = &joined;
                    async move {
                        loop {
                            if *canceled.borrow_and_update() {
                                break;
                            }
                            canceled.changed().await.unwrap();
                        }
                        tokio::task::yield_now().await;
                        joined.set(true);
                        Ok(())
                    }
                })],
            )
            .await;
            assert!(joined.get());
            assert_eq!(result.is_err(), status_failure);
        }
    }
    #[tokio::test]
    async fn failed_feature_drain_cannot_be_reported_as_clean_stop() {
        let result = run_all(
            async { Ok(()) },
            pending(),
            vec![feature(|_stop| async {
                Err(io::Error::other("retirement cleanup failed"))
            })],
        )
        .await;
        assert!(result.is_err());
    }
    #[tokio::test]
    async fn every_active_feature_receives_stop_and_keeps_custody_until_all_reads_join() {
        use std::sync::{
            Arc, Condvar, Mutex,
            atomic::{AtomicBool, Ordering},
        };
        const COUNT: usize = 3;
        let flags = || std::array::from_fn::<_, COUNT, _>(|_| Arc::new(AtomicBool::new(false)));
        for failed_status in [false, true] {
            let started = flags();
            let canceled = flags();
            let done = flags();
            let gates: [_; COUNT] =
                std::array::from_fn(|_| Arc::new((Mutex::new(false), Condvar::new())));
            let owned = Cell::new(true);
            let trigger = Arc::new(AtomicBool::new(false));
            let monitor = |fail| {
                let trigger = trigger.clone();
                async move {
                    while !trigger.load(Ordering::Acquire) {
                        tokio::time::sleep(std::time::Duration::from_millis(1)).await;
                    }
                    if fail {
                        Err(io::Error::other("injected origin status loss"))
                    } else {
                        Ok(())
                    }
                }
            };
            let endpoint = |index: usize| {
                let started = started[index].clone();
                let canceled = canceled[index].clone();
                let done = done[index].clone();
                let gate = gates[index].clone();
                feature(move |mut stop: watch::Receiver<bool>| async move {
                    let work = tokio::task::spawn_blocking(move || {
                        started.store(true, Ordering::Release);
                        let (lock, signal) = &*gate;
                        let mut permit = lock.lock().unwrap();
                        while !*permit {
                            permit = signal.wait(permit).unwrap();
                        }
                        done.store(true, Ordering::Release);
                    });
                    while !*stop.borrow_and_update() {
                        stop.changed().await.unwrap();
                    }
                    canceled.store(true, Ordering::Release);
                    work.await.unwrap();
                    // Even a failed first drain must not abandon its siblings.
                    if index == 0 {
                        Err(io::Error::other("injected failed drain"))
                    } else {
                        Ok(())
                    }
                })
            };
            let (result, ()) = tokio::join!(
                async {
                    let features = (0..COUNT).map(endpoint).collect();
                    let result = if failed_status {
                        run_all(pending(), monitor(true), features).await
                    } else {
                        run_all(monitor(false), pending(), features).await
                    };
                    owned.set(false);
                    result
                },
                async {
                    tokio::time::timeout(std::time::Duration::from_secs(5), async {
                        while !started.iter().all(|s| s.load(Ordering::Acquire)) {
                            tokio::task::yield_now().await;
                        }
                        trigger.store(true, Ordering::Release);
                        while !canceled.iter().all(|s| s.load(Ordering::Acquire)) {
                            tokio::task::yield_now().await;
                        }
                        assert!(owned.get());
                        let release = |i: usize| {
                            let (lock, signal) = &*gates[i];
                            *lock.lock().unwrap() = true;
                            signal.notify_all();
                        };
                        for index in 0..COUNT {
                            release(index);
                            while !done[index].load(Ordering::Acquire) {
                                tokio::task::yield_now().await;
                            }
                            tokio::task::yield_now().await;
                            assert!(done[index + 1..].iter().all(|d| !d.load(Ordering::Acquire)));
                            if index + 1 < COUNT {
                                assert!(owned.get());
                            }
                        }
                    })
                    .await
                    .unwrap();
                },
            );
            assert!(result.is_err());
            assert!(done.iter().all(|d| d.load(Ordering::Acquire)));
            assert!(!owned.get());
        }
    }

    #[tokio::test]
    async fn one_feature_failure_signals_and_joins_the_others() {
        let joined = [Cell::new(false), Cell::new(false)];
        let waiter = |index: usize| {
            let joined = &joined[index];
            feature(move |mut stop: watch::Receiver<bool>| async move {
                while !*stop.borrow_and_update() {
                    stop.changed().await.unwrap();
                }
                joined.set(true);
                Ok(())
            })
        };
        let result = run_all(
            pending(),
            pending(),
            vec![
                waiter(0),
                feature(|_stop| async { Err(io::Error::other("middle failed")) }),
                waiter(1),
            ],
        )
        .await;
        assert!(result.is_err());
        assert!(joined.iter().all(Cell::get));
    }

    #[tokio::test]
    async fn no_selected_feature_ends_with_the_first_monitor() {
        let result = run_all(async { Ok(()) }, pending(), Vec::new()).await;
        assert!(result.is_ok());
        let result = run_all(
            pending(),
            async { Err(io::Error::other("lost")) },
            Vec::new(),
        )
        .await;
        assert!(result.is_err());
    }
}

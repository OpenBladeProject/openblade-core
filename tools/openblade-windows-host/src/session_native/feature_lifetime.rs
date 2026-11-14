//! Feature lifetime shared by the installed monitor and injected supervision tests.
use std::{future::Future, io};
use tokio::sync::watch;

pub(super) async fn run<Fut: Future<Output = io::Result<()>>>(
    stop: impl Future<Output = io::Result<()>>,
    status: impl Future<Output = io::Result<()>>,
    feature: impl FnOnce(watch::Receiver<bool>) -> Fut,
) -> io::Result<()> {
    let (stop_feature, feature_stop) = watch::channel(false);
    let feature = feature(feature_stop);
    tokio::pin!(stop, status, feature);
    let (result, finished) = tokio::select! {
        result = &mut stop => (result, false),
        result = &mut status => (result, false),
        result = &mut feature => (result, true),
    };
    let _ = stop_feature.send(true);
    // Stop, status/origin loss, and endpoint failure share one lifetime owner.
    // Never drop an in-flight provider because another monitor completed first.
    if !finished {
        feature.await?;
    }
    result
}

/// The pair shares a stop signal, but each future must positively finish. A
/// feature error also stops its sibling; join! is deliberately used on drain
/// so one failed drain cannot drop the other feature's in-flight blocking work.
#[cfg(any(
    test,
    all(
        feature = "agent-battery-report-v1",
        feature = "agent-display-query-v1"
    )
))]
pub(super) async fn run_pair<Left, Right>(
    stop: impl Future<Output = io::Result<()>>,
    status: impl Future<Output = io::Result<()>>,
    left: impl FnOnce(watch::Receiver<bool>) -> Left,
    right: impl FnOnce(watch::Receiver<bool>) -> Right,
) -> io::Result<()>
where
    Left: Future<Output = io::Result<()>>,
    Right: Future<Output = io::Result<()>>,
{
    let (signal, receiver) = watch::channel(false);
    let left = left(receiver.clone());
    let right = right(receiver);
    tokio::pin!(stop, status, left, right);
    let (result, finished) = tokio::select! {
        result = &mut stop => (result, 0),
        result = &mut status => (result, 0),
        result = &mut left => (result, 1),
        result = &mut right => (result, 2),
    };
    let _ = signal.send(true);
    let drain = match finished {
        1 => right.await,
        2 => left.await,
        _ => {
            let (left, right) = tokio::join!(left, right);
            left.and(right)
        }
    };
    result.and(drain)
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
            let result = run(stop, status, |mut canceled| {
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
            })
            .await;
            assert!(joined.get());
            assert_eq!(result.is_err(), status_failure);
        }
    }
    #[tokio::test]
    async fn failed_feature_drain_cannot_be_reported_as_clean_stop() {
        let result = run(async { Ok(()) }, pending(), |_stop| async {
            Err(io::Error::other("retirement cleanup failed"))
        })
        .await;
        assert!(result.is_err());
    }
    #[tokio::test]
    async fn both_active_features_receive_stop_and_keep_custody_until_both_reads_join() {
        use std::sync::{
            Arc, Condvar, Mutex,
            atomic::{AtomicBool, Ordering},
        };
        for failed_status in [false, true] {
            let started = [
                Arc::new(AtomicBool::new(false)),
                Arc::new(AtomicBool::new(false)),
            ];
            let canceled = [
                Arc::new(AtomicBool::new(false)),
                Arc::new(AtomicBool::new(false)),
            ];
            let done = [
                Arc::new(AtomicBool::new(false)),
                Arc::new(AtomicBool::new(false)),
            ];
            let gates = [
                Arc::new((Mutex::new(false), Condvar::new())),
                Arc::new((Mutex::new(false), Condvar::new())),
            ];
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
            let feature = |index: usize, mut stop: watch::Receiver<bool>| {
                let started = started[index].clone();
                let canceled = canceled[index].clone();
                let done = done[index].clone();
                let gate = gates[index].clone();
                async move {
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
                    // Even a failed first drain must not abandon its sibling.
                    if index == 0 {
                        Err(io::Error::other("injected failed drain"))
                    } else {
                        Ok(())
                    }
                }
            };
            let (result, ()) = tokio::join!(
                async {
                    let result = if failed_status {
                        run_pair(
                            pending(),
                            monitor(true),
                            |s| feature(0, s),
                            |s| feature(1, s),
                        )
                        .await
                    } else {
                        run_pair(
                            monitor(false),
                            pending(),
                            |s| feature(0, s),
                            |s| feature(1, s),
                        )
                        .await
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
                        release(0);
                        while !done[0].load(Ordering::Acquire) {
                            tokio::task::yield_now().await;
                        }
                        tokio::task::yield_now().await;
                        assert!(owned.get());
                        assert!(!done[1].load(Ordering::Acquire));
                        release(1);
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
    async fn one_feature_failure_signals_and_joins_the_other() {
        let joined = Cell::new(false);
        let result = run_pair(
            pending(),
            pending(),
            |_stop| async { Err(io::Error::other("left failed")) },
            |mut stop| {
                let joined = &joined;
                async move {
                    while !*stop.borrow_and_update() {
                        stop.changed().await.unwrap();
                    }
                    joined.set(true);
                    Ok(())
                }
            },
        )
        .await;
        assert!(result.is_err());
        assert!(joined.get());
    }
}

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
}

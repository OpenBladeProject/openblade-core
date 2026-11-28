use std::{cell::Cell, rc::Rc};

use tokio::{
    sync::{mpsc, oneshot},
    time::advance,
};

use super::*;

async fn get_near_boundary(after_poll: bool) {
    let subscriptions = Subscriptions::new(8);
    let observed_at = Rc::new(Cell::new(Instant::now()));
    let observations = Rc::new(Cell::new(1));
    let (attempted, mut attempts) = mpsc::unbounded_channel();
    let (stop, stopped) = oneshot::channel();
    let polling = subscriptions.refresh_while_subscribed(|| {
        let observed_at = Rc::clone(&observed_at);
        let observations = Rc::clone(&observations);
        let attempted = attempted.clone();
        async move {
            let mut due = observed_at.get() + REFRESH_INTERVAL;
            if Instant::now() >= due {
                observations.set(observations.get() + 1);
                observed_at.set(Instant::now());
                due = observed_at.get() + REFRESH_INTERVAL;
            }
            attempted.send(Instant::now()).unwrap();
            Ok(due)
        }
    });
    let driver = async {
        tokio::select! {
            result = polling => panic!("shared refresh stopped: {result:?}"),
            result = stopped => result.unwrap(),
        }
    };
    let clients = async {
        advance(REFRESH_INTERVAL * 3).await;
        assert!(attempts.try_recv().is_err(), "polling without subscribers");
        let start = Instant::now();
        observed_at.set(start); // shared initial/GetStatus observation
        let first = subscriptions.slots.try_acquire().unwrap();
        let second = subscriptions.slots.try_acquire().unwrap();
        subscriptions.wake.notify_one();
        assert_eq!(attempts.recv().await.unwrap(), start);
        assert_eq!(observations.get(), 1);

        let edge = Duration::from_millis(1);
        if after_poll {
            advance(REFRESH_INTERVAL).await;
            assert_eq!(attempts.recv().await.unwrap(), start + REFRESH_INTERVAL);
            assert_eq!(observations.get(), 2);
            advance(edge).await;
        } else {
            advance(REFRESH_INTERVAL - edge).await;
        }
        // GetStatus and subscription polling share the source's observation
        // clock. An unchanged observation updates freshness, not revisions.
        let got_status_at = Instant::now();
        observed_at.set(got_status_at);
        let reads_before_due = observations.get();
        if after_poll {
            advance(REFRESH_INTERVAL - edge).await;
        } else {
            advance(edge).await;
        }
        let skipped_at = attempts.recv().await.unwrap();
        assert_eq!(observations.get(), reads_before_due);
        let due = got_status_at + REFRESH_INTERVAL;
        assert!(skipped_at < due);
        advance(due - Instant::now()).await;
        assert_eq!(attempts.recv().await.unwrap(), due);
        assert_eq!(observations.get(), reads_before_due + 1);

        drop((first, second));
        advance(REFRESH_INTERVAL * 3).await;
        assert!(
            attempts.try_recv().is_err(),
            "polling continued without demand"
        );
        stop.send(()).unwrap();
    };
    tokio::join!(driver, clients);
}

#[tokio::test(start_paused = true)]
async fn get_status_just_before_poll_refreshes_at_observation_due_time() {
    get_near_boundary(false).await;
}

#[tokio::test(start_paused = true)]
async fn get_status_just_after_poll_refreshes_at_observation_due_time() {
    get_near_boundary(true).await;
}

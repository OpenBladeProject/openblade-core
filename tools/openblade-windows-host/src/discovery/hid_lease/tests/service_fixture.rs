use tokio::sync::{mpsc, oneshot};

use super::*;

pub(crate) async fn drive(
    mut steps: mpsc::Receiver<oneshot::Sender<()>>,
    mut shutdown: oneshot::Receiver<()>,
    draining: oneshot::Sender<()>,
    release: oneshot::Receiver<()>,
    mut publish: impl FnMut(bool),
) -> io::Result<()> {
    let source = fake();
    let mut owner = scripted_owner(&source);
    let cancelled = AtomicBool::new(false);
    let script = async {
        let done = next_step(&mut steps).await;
        let original = owner.acquire(&cancelled).unwrap();
        publish(true);
        done.send(()).unwrap();

        let done = next_step(&mut steps).await;
        source.retirements.borrow_mut().push_back(Poll::Pending);
        assert_eq!(owner.invalidate(&original), Some(Retirement::Pending));
        assert!(owner.revalidate(&original, &cancelled).is_err());
        assert!(owner.acquire(&cancelled).is_err());
        assert_eq!(source.opened.borrow().len(), 1);
        assert_eq!(*source.retired.borrow(), [1]);
        assert_eq!(source.drops.get(), 0);
        publish(false);
        done.send(()).unwrap();

        let done = next_step(&mut steps).await;
        assert_eq!(owner.poll_retirement(), Retirement::Complete);
        let replacement = owner.acquire(&cancelled).unwrap();
        assert_eq!(source.opened.borrow()[0], source.opened.borrow()[1]);
        assert_eq!(owner.invalidate(&original), None);
        assert!(owner.revalidate(&original, &cancelled).is_err());
        owner.revalidate(&replacement, &cancelled).unwrap();
        assert_eq!(*source.retired.borrow(), [1, 1]);
        assert_eq!(source.drops.get(), 1);
        publish(true);
        done.send(()).unwrap();

        let done = next_step(&mut steps).await;
        let rows = source.rows.take();
        source.retirements.borrow_mut().push_back(Poll::Pending);
        assert!(owner.revalidate(&replacement, &cancelled).is_err());
        assert_eq!(owner.retirement(), Retirement::Pending);
        source.rows.replace(rows);
        assert!(owner.acquire(&cancelled).is_err());
        assert_eq!(source.opened.borrow().len(), 2);
        assert_eq!(*source.retired.borrow(), [1, 1, 2]);
        assert_eq!(source.drops.get(), 1);
        publish(false);
        done.send(()).unwrap();
    };
    tokio::select! {
        biased;
        _ = &mut shutdown => {}
        () = script => { let _ = shutdown.await; }
    }

    // STOP uses the same owner and retains it throughout the pending drain.
    match owner.stop() {
        Retirement::Complete => Ok(()),
        Retirement::Uncertain => Err(io::Error::other("HID retirement is uncertain")),
        Retirement::Pending => {
            draining
                .send(())
                .map_err(|()| io::Error::other("HID drain observer disconnected"))?;
            release.await.map_err(io::Error::other)?;
            if owner.poll_retirement() != Retirement::Complete {
                return Err(io::Error::other("HID retirement did not settle"));
            }
            assert!(owner.acquire(&cancelled).is_err());
            assert_eq!(*source.retired.borrow(), [1, 1, 2, 2]);
            assert_eq!(source.drops.get(), 2);
            Ok(())
        }
    }
}

async fn next_step(steps: &mut mpsc::Receiver<oneshot::Sender<()>>) -> oneshot::Sender<()> {
    match steps.recv().await {
        Some(done) => done,
        None => std::future::pending().await,
    }
}

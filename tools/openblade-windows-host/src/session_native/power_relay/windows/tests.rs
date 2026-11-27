//! The registration's context lifetime against a fake `Notifications`. No
//! Windows function is called.
use std::sync::{
    Mutex, PoisonError,
    atomic::AtomicI64,
    mpsc::{Receiver, Sender, channel},
};

use super::*;
use crate::session_native::power_relay::SUSPEND;

/// Records the registered callback; answers with `registered` and
/// `unregistered`.
#[derive(Clone)]
struct Fake {
    callback: Arc<Mutex<Option<(Callback, usize)>>>,
    registered: Result<(), u32>,
    unregistered: u32,
}

impl Fake {
    fn new(registered: Result<(), u32>, unregistered: u32) -> Self {
        Self {
            callback: Arc::default(),
            registered,
            unregistered,
        }
    }

    /// Windows calling back on its own thread.
    fn call(&self, kind: u32) -> thread::JoinHandle<u32> {
        let (callback, context) = self
            .callback
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .expect("registered");
        // SAFETY: the callback with the context it was registered with.
        thread::spawn(move || unsafe { callback(context as *const c_void, kind, ptr::null()) })
    }
}

impl Notifications for Fake {
    fn register(&self, callback: Callback, context: *mut c_void) -> Result<*mut c_void, u32> {
        self.registered?;
        *self.callback.lock().unwrap_or_else(PoisonError::into_inner) =
            Some((callback, context as usize));
        Ok(ptr::dangling_mut())
    }

    fn unregister(&self, _handle: *mut c_void) -> u32 {
        self.unregistered
    }
}

/// A relay whose second QPC read, inside a suspend callback, says it has
/// entered and then blocks until released.
fn held_relay() -> (Arc<Relay>, Receiver<()>, Sender<()>) {
    let (entered_tx, entered) = channel();
    let (release, release_rx) = channel::<()>();
    let release_rx = Mutex::new(release_rx);
    let reads = AtomicI64::new(0);
    let relay = Relay::new(
        1,
        1000,
        Box::new(move || {
            if reads.fetch_add(1, SeqCst) == 1 {
                let _ = entered_tx.send(());
                let _ = release_rx
                    .lock()
                    .unwrap_or_else(PoisonError::into_inner)
                    .recv();
            }
            1
        }),
    )
    .unwrap();
    (Arc::new(relay), entered, release)
}

#[test]
fn a_callback_in_flight_keeps_the_context_until_it_leaves() {
    let (relay, entered, release) = held_relay();
    let fake = Fake::new(Ok(()), 0);
    let registration =
        Registration::with(fake.clone(), Arc::clone(&relay), Duration::from_secs(60)).unwrap();
    let windows = fake.call(SUSPEND);
    entered.recv().unwrap();
    // As the transport does: dispose, then unregister, while the callback is
    // still inside the context.
    relay.dispose();
    let releaser = thread::spawn(move || {
        thread::sleep(Duration::from_millis(100));
        release.send(()).unwrap();
    });
    let started = Instant::now();
    drop(registration);
    assert!(started.elapsed() >= Duration::from_millis(100));
    assert_eq!(windows.join().unwrap(), 0);
    releaser.join().unwrap();
    // The context, and its relay reference, were released.
    assert_eq!(Arc::strong_count(&relay), 1);
}

#[test]
fn a_callback_still_running_past_the_drain_leaves_the_context_alive() {
    let (relay, entered, release) = held_relay();
    let fake = Fake::new(Ok(()), 0);
    let registration =
        Registration::with(fake.clone(), Arc::clone(&relay), Duration::from_millis(20)).unwrap();
    let windows = fake.call(SUSPEND);
    entered.recv().unwrap();
    relay.dispose();
    drop(registration);
    assert_eq!(Arc::strong_count(&relay), 2);
    // The kept context stays valid for the callback that is still running.
    release.send(()).unwrap();
    assert_eq!(windows.join().unwrap(), 0);
    assert_eq!(Arc::strong_count(&relay), 2);
}

#[test]
fn an_idle_registration_releases_its_context_and_a_failed_one_keeps_it() {
    let relay = Arc::new(Relay::new(1, 1000, Box::new(|| 1)).unwrap());
    drop(Registration::with(Fake::new(Ok(()), 0), Arc::clone(&relay), Duration::ZERO).unwrap());
    assert_eq!(Arc::strong_count(&relay), 1);
    // A refused registration releases the context at once.
    assert_eq!(
        Registration::with(Fake::new(Err(5), 0), Arc::clone(&relay), DRAIN).err(),
        Some(5)
    );
    assert_eq!(Arc::strong_count(&relay), 1);
    // Windows may still call back after a failed unregistration.
    drop(Registration::with(Fake::new(Ok(()), 5), Arc::clone(&relay), DRAIN).unwrap());
    assert_eq!(Arc::strong_count(&relay), 2);
}

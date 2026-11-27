//! The registration's callback slot against a fake `Notifications`. No
//! Windows function is called.
use std::{
    sync::{
        atomic::{AtomicI64, Ordering::SeqCst},
        mpsc::{Receiver, Sender, channel},
    },
    thread,
};

use super::*;
use crate::session_native::power_relay::{RESUME_AUTOMATIC, SUSPEND};

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

fn idle_relay() -> Arc<Relay> {
    Arc::new(Relay::new(1, 1000, Box::new(|| 1)).unwrap())
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
fn a_callback_after_unregistration_finds_no_relay_and_does_nothing() {
    let relay = idle_relay();
    let fake = Fake::new(Ok(()), 0);
    let registration = Registration::with(fake.clone(), Arc::clone(&relay)).unwrap();
    drop(registration);
    assert_eq!(Arc::strong_count(&relay), 1);
    // A late callback reaches the kept slot, finds it empty, and the relay,
    // which was not disposed, records no transition.
    assert_eq!(fake.call(RESUME_AUTOMATIC).join().unwrap(), 0);
    assert_eq!(relay.lock().sequence, 0);
}

#[test]
fn a_callback_running_across_unregistration_keeps_its_own_relay() {
    let (relay, entered, release) = held_relay();
    let fake = Fake::new(Ok(()), 0);
    let registration = Registration::with(fake.clone(), Arc::clone(&relay)).unwrap();
    let windows = fake.call(SUSPEND);
    entered.recv().unwrap();
    // As the transport does: dispose, then unregister, while the callback is
    // still inside the relay. Drop does not wait for it.
    relay.dispose();
    drop(registration);
    assert_eq!(Arc::strong_count(&relay), 2, "the callback holds its clone");
    release.send(()).unwrap();
    assert_eq!(windows.join().unwrap(), 0);
    assert_eq!(Arc::strong_count(&relay), 1);
}

#[test]
fn a_refused_registration_releases_the_relay_and_a_failed_unregistration_keeps_it() {
    let relay = idle_relay();
    assert_eq!(
        Registration::with(Fake::new(Err(5), 0), Arc::clone(&relay)).err(),
        Some(5)
    );
    assert_eq!(Arc::strong_count(&relay), 1);
    // Windows may still call back after a failed unregistration, and the
    // callback still reaches the relay.
    let fake = Fake::new(Ok(()), 5);
    drop(Registration::with(fake.clone(), Arc::clone(&relay)).unwrap());
    assert_eq!(Arc::strong_count(&relay), 2);
    assert_eq!(fake.call(RESUME_AUTOMATIC).join().unwrap(), 0);
    assert_eq!(relay.lock().sequence, 1);
}

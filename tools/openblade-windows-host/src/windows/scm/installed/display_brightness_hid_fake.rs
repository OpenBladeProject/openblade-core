//! A scripted `Hid` for tests: managed's `IDisplayBrightnessHidTransport`
//! as its runtime tests script it. No Windows call is reached.

use std::{
    collections::VecDeque,
    sync::{Arc, Condvar, Mutex},
};

use super::{AVAILABILITY_UNKNOWN, Hid, Lane, WriteResult};

/// What the transport returns and what it was asked.
pub(crate) struct Script {
    /// One result per `set_usage`; Applied once they run out.
    pub(crate) writes: VecDeque<WriteResult>,
    /// Every usage `set_usage` was asked for, in order.
    pub(crate) usages: Vec<u16>,
    pub(crate) availability: u8,
    pub(crate) probes: usize,
    /// While set and false, `set_usage` waits after recording its usage
    /// until the test sets it true.
    pub(crate) hold: Option<Arc<(Mutex<bool>, Condvar)>>,
    /// The next `set_usage` panics after recording its usage.
    pub(crate) panic_next: bool,
}

impl Default for Script {
    fn default() -> Self {
        Self {
            writes: VecDeque::new(),
            usages: Vec::new(),
            availability: AVAILABILITY_UNKNOWN,
            probes: 0,
            hold: None,
            panic_next: false,
        }
    }
}

pub(crate) type Shared = Arc<Mutex<Script>>;

struct Scripted(Shared);

impl Hid for Scripted {
    fn availability(&mut self) -> u8 {
        let mut script = self.0.lock().unwrap();
        script.probes += 1;
        script.availability
    }

    fn set_usage(&mut self, usage: u16) -> WriteResult {
        let (hold, panics) = {
            let mut script = self.0.lock().unwrap();
            script.usages.push(usage);
            (script.hold.clone(), std::mem::take(&mut script.panic_next))
        };
        if let Some(hold) = hold {
            let (released, signal) = &*hold;
            let mut released = released.lock().unwrap();
            while !*released {
                released = signal.wait(released).unwrap();
            }
        }
        assert!(!panics, "scripted transport panic");
        self.0
            .lock()
            .unwrap()
            .writes
            .pop_front()
            .unwrap_or_else(WriteResult::applied)
    }
}

/// A lane over a new script.
pub(crate) fn lane() -> (Arc<Lane>, Shared) {
    let script = Shared::default();
    let shared = script.clone();
    let lane = Lane::spawn(move || Scripted(shared)).expect("the test lane starts");
    (Arc::new(lane), script)
}

/// A hold `set_usage` waits on, and its release.
pub(crate) fn hold(script: &Shared) -> impl Fn() {
    let hold = Arc::new((Mutex::new(false), Condvar::new()));
    script.lock().unwrap().hold = Some(hold.clone());
    move || {
        *hold.0.lock().unwrap() = true;
        hold.1.notify_all();
    }
}

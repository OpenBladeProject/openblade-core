//! A scripted `Hid` for tests: managed's `IDisplayBrightnessHidTransport`
//! as its runtime tests script it. No Windows call is reached.

use std::{
    collections::VecDeque,
    sync::{Arc, Condvar, Mutex},
};

use super::{AVAILABILITY_UNKNOWN, Hid, Lane, Log, Usage, WriteResult};

/// What the transport returns and what it was asked.
pub(crate) struct Script {
    /// One result per `set_usage`; Applied once they run out.
    pub(crate) writes: VecDeque<WriteResult>,
    /// Every usage `set_usage` was asked for, in order.
    pub(crate) usages: Vec<Usage>,
    pub(crate) availability: u8,
    pub(crate) probes: usize,
    /// While set and false, `set_usage` waits after recording its usage
    /// until the test sets it true.
    pub(crate) hold: Option<Arc<(Mutex<bool>, Condvar)>>,
    /// While set and false, `availability` waits after counting its probe.
    pub(crate) hold_probe: Option<Arc<(Mutex<bool>, Condvar)>>,
    /// The next `set_usage` panics after recording its usage.
    pub(crate) panic_next: bool,
    /// Every warning the lane recorded, as `category: message`.
    pub(crate) warnings: Vec<String>,
    /// Called with each usage as `set_usage` records it, outside the script's
    /// lock, so a test can see what else had happened by then.
    pub(crate) on_usage: Option<Arc<dyn Fn(Usage) + Send + Sync>>,
}

impl Default for Script {
    fn default() -> Self {
        Self {
            writes: VecDeque::new(),
            usages: Vec::new(),
            availability: AVAILABILITY_UNKNOWN,
            probes: 0,
            hold: None,
            hold_probe: None,
            panic_next: false,
            warnings: Vec::new(),
            on_usage: None,
        }
    }
}

pub(crate) type Shared = Arc<Mutex<Script>>;

struct Scripted(Shared);

fn wait(hold: Option<Arc<(Mutex<bool>, Condvar)>>) {
    if let Some(hold) = hold {
        let (released, signal) = &*hold;
        let mut released = released.lock().unwrap();
        while !*released {
            released = signal.wait(released).unwrap();
        }
    }
}

impl Hid for Scripted {
    fn availability(&mut self) -> u8 {
        let hold = {
            let mut script = self.0.lock().unwrap();
            script.probes += 1;
            script.hold_probe.clone()
        };
        wait(hold);
        self.0.lock().unwrap().availability
    }

    fn set_usage(&mut self, usage: Usage) -> WriteResult {
        let (hold, panics, observer) = {
            let mut script = self.0.lock().unwrap();
            script.usages.push(usage);
            (
                script.hold.clone(),
                std::mem::take(&mut script.panic_next),
                script.on_usage.clone(),
            )
        };
        if let Some(observer) = observer {
            observer(usage);
        }
        wait(hold);
        assert!(!panics, "scripted transport panic");
        self.0
            .lock()
            .unwrap()
            .writes
            .pop_front()
            .unwrap_or_else(WriteResult::applied)
    }
}

/// A lane over a new script, whose warnings the script records.
pub(crate) fn lane() -> (Arc<Lane>, Shared) {
    let script = Shared::default();
    let warnings = script.clone();
    let log = Log::new(Box::new(move |category, message| {
        warnings
            .lock()
            .unwrap()
            .warnings
            .push(format!("{category}: {message}"));
    }));
    let shared = script.clone();
    let lane =
        Lane::spawn(Arc::new(log), move || Scripted(shared.clone())).expect("the test lane starts");
    (Arc::new(lane), script)
}

fn gate(slot: &mut Option<Arc<(Mutex<bool>, Condvar)>>) -> impl Fn() + use<> {
    let hold = Arc::new((Mutex::new(false), Condvar::new()));
    *slot = Some(hold.clone());
    move || {
        *hold.0.lock().unwrap() = true;
        hold.1.notify_all();
    }
}

/// A hold `set_usage` waits on, and its release.
pub(crate) fn hold(script: &Shared) -> impl Fn() + use<> {
    gate(&mut script.lock().unwrap().hold)
}

/// A hold `availability` waits on, and its release.
pub(crate) fn hold_probe(script: &Shared) -> impl Fn() + use<> {
    gate(&mut script.lock().unwrap().hold_probe)
}

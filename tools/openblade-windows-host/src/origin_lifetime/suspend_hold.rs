//! The bounded hold of a new suspend generation's SCM control callback until
//! the low-power preparation has recorded an outcome for it.
//!
//! Windows suspends once the SCM control handler returns PBT_APMSUSPEND, so
//! a handler that returns at once races the keyboard's Normal release. The
//! hold waits, as the managed Session Agent's relay waits for the Service
//! (`SessionPowerTransitionRelay.cs:101-108`), at most until the notice's QPC
//! stamp plus the preparer's age limit. It changes no admission or fence and
//! sends nothing: it only delays the callback's return.
//!
//! The callback runs on the SCM dispatcher thread, the thread that called
//! `StartServiceCtrlDispatcherW`. The preparation runs on the current-thread
//! runtime `scm_host::registered_service` builds on the ServiceMain thread,
//! which the SCM starts separately. A wait on the preparer's own thread
//! could never be released, so it returns at once. No lock is held while
//! waiting except this hold's own, which `Condvar::wait_timeout` releases.
use std::{
    sync::{Arc, Condvar, Mutex},
    thread::{self, ThreadId},
    time::{Duration, Instant},
};

/// One lifecycle's hold. A preparer arms it (`arm`); each newly accepted
/// suspend generation may then be held (`accept`) until the preparer records
/// that generation or a later one (`HoldArm::prepared`).
#[derive(Default)]
pub(crate) struct SuspendHold {
    state: Mutex<State>,
    recorded: Condvar,
}
#[derive(Default)]
struct State {
    armed: Option<Armed>,
    // The highest generation the preparer has recorded an outcome for.
    prepared: Option<u64>,
}
struct Armed {
    // The longest a generation is held from its notice's QPC stamp.
    cap: Duration,
    // The thread the preparation runs on; it never waits.
    preparer: ThreadId,
}

impl SuspendHold {
    /// Arms the hold for the preparer running on this thread, which holds
    /// each new generation at most `cap` from its notice. `None` when a
    /// preparer already armed it or the hold is unreadable: one preparer per
    /// lifecycle, and an unarmed hold never waits.
    pub(crate) fn arm(self: &Arc<Self>, cap: Duration) -> Option<HoldArm> {
        let mut state = self.state.lock().ok()?;
        if state.armed.is_some() {
            return None;
        }
        state.armed = Some(Armed {
            cap,
            preparer: thread::current().id(),
        });
        Some(HoldArm(self.clone()))
    }

    /// The hold for a newly accepted suspend `generation` whose notice is
    /// `age` old, or `None` when there is nothing to wait for: the hold is
    /// unarmed or unreadable, the generation is already recorded, the age is
    /// unknown (a notice stamped in the future) or the cap is already spent.
    pub(crate) fn accept(
        self: &Arc<Self>,
        generation: u64,
        age: Option<Duration>,
    ) -> Option<PendingHold> {
        let state = self.state.lock().ok()?;
        let armed = state.armed.as_ref()?;
        if state
            .prepared
            .is_some_and(|prepared| prepared >= generation)
        {
            return None;
        }
        let left = armed.cap.checked_sub(age?).filter(|left| !left.is_zero())?;
        Some(PendingHold {
            hold: self.clone(),
            generation,
            deadline: Instant::now() + left,
        })
    }

    fn disarm(&self) {
        if let Ok(mut state) = self.state.lock() {
            state.armed = None;
        }
        self.recorded.notify_all();
    }
}

/// The preparer's arm. Dropping it disarms the hold and releases any wait.
pub(crate) struct HoldArm(Arc<SuspendHold>);
impl HoldArm {
    /// The preparer has recorded an outcome for `generation`: every hold of
    /// it or an earlier generation ends.
    #[cfg_attr(
        not(any(test, feature = "service-keyboard-driver-mode-v1")),
        allow(dead_code)
    )]
    pub(crate) fn prepared(&self, generation: u64) {
        let Ok(mut state) = self.0.state.lock() else {
            return;
        };
        if state.prepared.is_none_or(|prepared| prepared < generation) {
            state.prepared = Some(generation);
            self.0.recorded.notify_all();
        }
    }
}
impl Drop for HoldArm {
    fn drop(&mut self) {
        self.0.disarm();
    }
}

/// A held callback: one generation and the instant its cap ends.
pub(crate) struct PendingHold {
    hold: Arc<SuspendHold>,
    generation: u64,
    deadline: Instant,
}
impl PendingHold {
    /// Blocks until the preparer records this generation (true), or the
    /// deadline passes, the hold is disarmed or unreadable, or this is the
    /// preparer's own thread (false). The generation is checked again after
    /// every wake. It never fails or vetoes the control.
    pub(crate) fn wait(self) -> bool {
        let Ok(mut state) = self.hold.state.lock() else {
            return false;
        };
        loop {
            if state
                .prepared
                .is_some_and(|prepared| prepared >= self.generation)
            {
                return true;
            }
            let Some(armed) = &state.armed else {
                return false;
            };
            if armed.preparer == thread::current().id() {
                return false;
            }
            let Some(left) = self
                .deadline
                .checked_duration_since(Instant::now())
                .filter(|left| !left.is_zero())
            else {
                return false;
            };
            state = match self.hold.recorded.wait_timeout(state, left) {
                Ok((state, _)) => state,
                Err(_) => return false,
            };
        }
    }
    #[cfg(test)]
    pub(crate) fn generation(&self) -> u64 {
        self.generation
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::mpsc;

    const CAP: Duration = Duration::from_millis(1500);

    /// Arms on a scratch thread, so the test thread is not the preparer.
    fn armed(cap: Duration) -> (Arc<SuspendHold>, HoldArm) {
        let hold = Arc::new(SuspendHold::default());
        let arm = {
            let hold = hold.clone();
            thread::spawn(move || hold.arm(cap))
                .join()
                .unwrap()
                .unwrap()
        };
        (hold, arm)
    }

    #[test]
    fn a_hold_ends_when_its_generation_is_recorded() {
        let (hold, arm) = armed(CAP);
        let pending = hold.accept(3, Some(Duration::ZERO)).unwrap();
        let (started, waiting) = mpsc::channel();
        let waiter = thread::spawn(move || {
            started.send(()).unwrap();
            let at = Instant::now();
            (pending.wait(), at.elapsed())
        });
        waiting.recv().unwrap();
        thread::sleep(Duration::from_millis(50));
        // An earlier generation does not end it.
        arm.prepared(1);
        thread::sleep(Duration::from_millis(20));
        assert!(!waiter.is_finished());
        arm.prepared(3);
        let (prepared, waited) = waiter.join().unwrap();
        assert!(prepared);
        assert!(
            waited >= Duration::from_millis(50) && waited < CAP,
            "{waited:?}"
        );
        // A recorded generation is not held again, and a later record ends
        // an earlier, stale hold at once.
        arm.prepared(5);
        assert!(hold.accept(5, Some(Duration::ZERO)).is_none());
        let stale = hold.accept(7, Some(Duration::ZERO)).unwrap();
        arm.prepared(9);
        let at = Instant::now();
        assert!(stale.wait());
        assert!(at.elapsed() < Duration::from_millis(100));
    }

    #[test]
    fn a_hold_ends_at_its_cap_from_the_notice() {
        let cap = Duration::from_millis(200);
        let (hold, _arm) = armed(cap);
        // The cap counts from the notice's stamp, not from the wait: the
        // deadline is fixed at the accept, and the wait ends at it, however
        // late a loaded host wakes the waiter.
        for (generation, age) in [(1, Duration::ZERO), (3, cap - Duration::from_millis(150))] {
            let left = cap - age;
            let before = Instant::now();
            let pending = hold.accept(generation, Some(age)).unwrap();
            let after = Instant::now();
            let deadline = pending.deadline;
            assert!(
                deadline >= before + left && deadline <= after + left,
                "{age:?}"
            );
            assert!(!pending.wait());
            assert!(Instant::now() >= deadline, "{age:?}");
        }
        // A spent cap or an unknown age holds nothing.
        assert!(hold.accept(5, Some(cap)).is_none());
        assert!(hold.accept(5, Some(cap * 2)).is_none());
        assert!(hold.accept(5, None).is_none());
    }

    #[test]
    fn an_unarmed_or_disarmed_hold_never_waits() {
        let hold = Arc::new(SuspendHold::default());
        assert!(hold.accept(1, Some(Duration::ZERO)).is_none());
        let (hold, arm) = armed(CAP);
        // One preparer per lifecycle.
        assert!(hold.arm(CAP).is_none());
        let pending = hold.accept(1, Some(Duration::ZERO)).unwrap();
        let waiter = thread::spawn(move || pending.wait());
        thread::sleep(Duration::from_millis(50));
        drop(arm);
        assert!(!waiter.join().unwrap(), "disarming releases the wait");
        assert!(hold.accept(3, Some(Duration::ZERO)).is_none());
        // A new preparer may arm it again.
        assert!(hold.arm(CAP).is_some());
    }

    /// The preparation runs on the arming thread, so a wait there would
    /// block the very work it waits for: it returns at once, unrecorded.
    #[test]
    fn the_preparer_thread_never_waits() {
        let hold = Arc::new(SuspendHold::default());
        let _arm = hold.arm(CAP).unwrap();
        let at = Instant::now();
        assert!(!hold.accept(1, Some(Duration::ZERO)).unwrap().wait());
        assert!(at.elapsed() < Duration::from_millis(100));
        // Another thread does wait, until the cap. `accept` fixes the
        // deadline, so the wait is timed from before it, not from inside the
        // thread, whose spawn latency the deadline already counts.
        let at = Instant::now();
        let pending = hold
            .accept(1, Some(CAP - Duration::from_millis(100)))
            .unwrap();
        thread::spawn(move || pending.wait()).join().unwrap();
        let waited = at.elapsed();
        assert!(waited >= Duration::from_millis(100), "{waited:?}");
    }

    #[test]
    fn a_poisoned_hold_never_waits() {
        let (hold, _arm) = armed(CAP);
        let pending = hold.accept(1, Some(Duration::ZERO)).unwrap();
        let poison = hold.clone();
        let _ = thread::spawn(move || {
            let _state = poison.state.lock().unwrap();
            panic!("poison the hold");
        })
        .join();
        let at = Instant::now();
        assert!(!pending.wait());
        assert!(at.elapsed() < Duration::from_millis(100));
        assert!(hold.accept(3, Some(Duration::ZERO)).is_none());
    }
}

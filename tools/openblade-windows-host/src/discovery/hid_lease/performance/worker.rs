use std::{
    cell::RefCell,
    io,
    rc::Rc,
    sync::{
        Arc,
        atomic::{AtomicBool, Ordering},
    },
    thread::{Builder, JoinHandle},
};

use tokio::time::{Instant, sleep, timeout_at};

#[cfg(feature = "service-keyboard-driver-mode-read-v1")]
use super::DeviceModeRead;
use super::{
    Admission, Limit, MAX_OBSERVATION, ObservationProof, ObservedPerformance, POLL, blocked,
};
#[cfg(feature = "service-live-readings-v1")]
use super::{FanPairSends, FanRpmRead, McuQuerySends, McuVersionRead};

pub(crate) struct Reading<A = Admission> {
    pub(crate) admission: A,
    pub(crate) performance: ObservedPerformance,
    // Only the installed mailbox reader asks for it.
    #[cfg(feature = "service-keyboard-driver-mode-read-v1")]
    pub(crate) device_mode: DeviceModeRead,
    // Only the installed mailbox reader asks for it, for a fan-speed request.
    #[cfg(feature = "service-live-readings-v1")]
    pub(crate) fan_rpm: FanRpmRead,
    // Only the installed mailbox reader asks for it, while the firmware
    // record lacks the MCU version.
    #[cfg(feature = "service-live-readings-v1")]
    pub(crate) mcu_version: McuVersionRead,
}

impl<A> Reading<A> {
    /// A thermal reading that carries no query result.
    pub(crate) fn thermal(admission: A, performance: ObservedPerformance) -> Self {
        Self {
            admission,
            performance,
            #[cfg(feature = "service-keyboard-driver-mode-read-v1")]
            device_mode: DeviceModeRead::NotRequested,
            #[cfg(feature = "service-live-readings-v1")]
            fan_rpm: FanRpmRead::NotRequested,
            #[cfg(feature = "service-live-readings-v1")]
            mcu_version: McuVersionRead::NotRequested,
        }
    }
}

/// The read-only queries a job adds after its thermal observation. Each is
/// sent only after that observation completed, within its own budget, in
/// field order.
#[derive(Clone, Debug, Default)]
pub(crate) struct Queries {
    #[cfg(feature = "service-keyboard-driver-mode-read-v1")]
    pub(crate) device_mode: bool,
    /// The fan speed pair, counted where it is sent.
    #[cfg(feature = "service-live-readings-v1")]
    pub(crate) fan_rpm: Option<FanPairSends>,
    /// The MCU version query, counted where it is sent.
    #[cfg(feature = "service-live-readings-v1")]
    pub(crate) mcu_version: Option<McuQuerySends>,
}

pub(super) struct Job {
    deadline: Instant,
    cancelled: Arc<AtomicBool>,
    pub(super) session: Option<u32>,
    pub(super) queries: Queries,
}

impl Job {
    pub(super) fn limit(&self) -> Limit<'_> {
        Limit {
            deadline: self.deadline,
            cancelled: &self.cancelled,
        }
    }

    /// A job that asks for the thermal read alone.
    #[cfg(test)]
    pub(super) fn for_tests(deadline: Instant) -> Self {
        Self {
            deadline,
            cancelled: Arc::default(),
            session: None,
            queries: Queries::default(),
        }
    }
}

type Run<A> = dyn Fn(Job) -> io::Result<Option<Reading<A>>> + Send + Sync;

#[derive(Clone)]
pub(crate) struct Worker<A = Admission> {
    state: Rc<RefCell<State<A>>>,
    run: Arc<Run<A>>,
    occupied: Option<&'static AtomicBool>,
}

struct State<A> {
    active: Option<Active<A>>,
    stopped: bool,
    unconfirmed: bool,
}

impl<A> Default for State<A> {
    fn default() -> Self {
        Self {
            active: None,
            stopped: false,
            unconfirmed: false,
        }
    }
}

struct Active<A> {
    cancel: Arc<AtomicBool>,
    thread: JoinHandle<io::Result<Option<Reading<A>>>>,
}

struct Cancel(Arc<AtomicBool>);

impl Drop for Cancel {
    fn drop(&mut self) {
        self.0.store(true, Ordering::Release);
    }
}

impl<A> Drop for State<A> {
    fn drop(&mut self) {
        if let Some(active) = &self.active {
            active.cancel.store(true, Ordering::Release);
        }
    }
}

impl<A> State<A> {
    // Called only after thread completion. A failed cleanup or panic leaves
    // the owner fenced, including when the original requester has gone away.
    fn reap(&mut self) -> io::Result<Option<Reading<A>>> {
        let result = self
            .active
            .take()
            .expect("finished firmware worker")
            .thread
            .join()
            .map_err(|_| io::Error::other("firmware observation worker panicked"))
            .and_then(|result| result);
        self.unconfirmed = result.is_err();
        result
    }
}

impl<A: ObservationProof> Worker<A> {
    pub(super) fn new(
        run: impl Fn(Job) -> io::Result<Option<Reading<A>>> + Send + Sync + 'static,
        occupied: Option<&'static AtomicBool>,
    ) -> Self {
        Self {
            state: Rc::default(),
            run: Arc::new(run),
            occupied,
        }
    }

    pub(crate) async fn observe(
        &self,
        deadline: Instant,
        session: Option<u32>,
    ) -> io::Result<Option<Reading<A>>> {
        self.observe_with_budget(deadline, session, MAX_OBSERVATION)
            .await
    }
    pub(crate) async fn observe_with_budget(
        &self,
        deadline: Instant,
        session: Option<u32>,
        budget: std::time::Duration,
    ) -> io::Result<Option<Reading<A>>> {
        self.run_job(deadline, session, budget, Queries::default())
            .await
    }
    /// A thermal observation that also sends each asked query once.
    #[cfg(any(
        feature = "service-keyboard-driver-mode-read-v1",
        feature = "service-live-readings-v1"
    ))]
    pub(crate) async fn observe_with_queries(
        &self,
        deadline: Instant,
        queries: Queries,
    ) -> io::Result<Option<Reading<A>>> {
        self.run_job(deadline, None, MAX_OBSERVATION, queries).await
    }
    async fn run_job(
        &self,
        deadline: Instant,
        session: Option<u32>,
        budget: std::time::Duration,
        queries: Queries,
    ) -> io::Result<Option<Reading<A>>> {
        let deadline = deadline.min(Instant::now() + budget);
        let Some(_cancel) = self.start(deadline, session, queries)? else {
            return Ok(None);
        };
        if timeout_at(deadline, self.wait()).await.is_err() {
            return Ok(None);
        }
        let mut state = self.state.borrow_mut();
        if state.stopped {
            return Ok(None);
        }
        let result = state.reap()?;
        Ok(result.filter(|_| Instant::now() < deadline))
    }

    fn start(
        &self,
        deadline: Instant,
        session: Option<u32>,
        queries: Queries,
    ) -> io::Result<Option<Cancel>> {
        let mut state = self.state.borrow_mut();
        if state.stopped || state.unconfirmed || Instant::now() >= deadline {
            return Ok(None);
        }
        if let Some(active) = &state.active {
            if !active.cancel.load(Ordering::Acquire) || !active.thread.is_finished() {
                return Ok(None);
            }
            state.reap()?;
        }
        let occupied = self.occupied;
        if let Some(occupied) = occupied
            && occupied
                .compare_exchange(false, true, Ordering::AcqRel, Ordering::Acquire)
                .is_err()
        {
            return Ok(None);
        }

        let cancel = Arc::new(AtomicBool::new(false));
        let job = Job {
            deadline,
            cancelled: cancel.clone(),
            session,
            queries,
        };
        let run = self.run.clone();
        // The deadline is on the Service runtime's clock, which in production
        // is the real clock this thread reads. A test rig holds the Service's
        // clock while job threads run, so a test thread reads that clock.
        #[cfg(test)]
        let service_clock = tokio::runtime::Handle::try_current().ok();
        // Use an owned OS thread, outside the Service runtime's blocking pool.
        // A native call may stall past both the request and shutdown deadlines.
        let thread = Builder::new()
            .name("openblade-firmware-observer".into())
            .spawn(move || {
                let admitted = {
                    #[cfg(test)]
                    let _clock = service_clock.as_ref().map(tokio::runtime::Handle::enter);
                    job.limit().check().is_ok()
                };
                let result = if admitted { run(job) } else { Ok(None) };
                if result.is_ok()
                    && let Some(occupied) = occupied
                {
                    occupied.store(false, Ordering::Release);
                }
                result
            });
        match thread {
            Ok(thread) => {
                state.active = Some(Active {
                    cancel: cancel.clone(),
                    thread,
                });
                Ok(Some(Cancel(cancel)))
            }
            Err(error) => {
                if let Some(occupied) = occupied {
                    occupied.store(false, Ordering::Release);
                }
                Err(error)
            }
        }
    }

    async fn wait(&self) {
        while self
            .state
            .borrow()
            .active
            .as_ref()
            .is_some_and(|active| !active.thread.is_finished())
        {
            sleep(POLL).await;
        }
    }

    pub(crate) async fn stop(&self) -> io::Result<()> {
        {
            let mut state = self.state.borrow_mut();
            state.stopped = true;
            if let Some(active) = &state.active {
                active.cancel.store(true, Ordering::Release);
            }
        }
        self.wait().await;
        let mut state = self.state.borrow_mut();
        if state.active.is_some() {
            state.reap()?;
        }
        if state.unconfirmed {
            return Err(blocked("firmware worker retirement is unconfirmed"));
        }
        Ok(())
    }
}

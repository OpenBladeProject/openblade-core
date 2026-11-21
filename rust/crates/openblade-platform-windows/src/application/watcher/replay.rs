//! Scripted desktop and registration for the private v79 runner.
use std::{
    cell::{Cell, RefCell},
    collections::BTreeMap,
    io,
    num::NonZeroU32,
    rc::Rc,
    sync::{
        Arc,
        atomic::{AtomicBool, Ordering},
    },
    thread,
    time::Duration,
};

use openblade_application_reporter::{Guid, ProcessIdentity};

use super::{Callbacks, Desktop, ForegroundWatcher, Notice, Registration};
use crate::application::{Lifetime, Owner, process_identity};

pub type ScriptedWatcher = ForegroundWatcher<ScriptedDesktop>;

pub struct ScriptedDesktop(Rc<RefCell<State>>);

pub struct Script {
    state: Rc<RefCell<State>>,
    registration: Arc<AtomicBool>,
}

struct State {
    active: bool,
    session: u32,
    window: usize,
    processes: BTreeMap<usize, ProcessProbe>,
    callbacks: Option<Callbacks>,
    during_lookup: Option<usize>,
    deactivate_during_lookup: bool,
}

#[derive(Clone)]
pub struct ProcessProbe(Rc<ProcessState>);

struct ProcessState {
    identity: ProcessIdentity,
    path: String,
    alive: Cell<bool>,
    handles: Cell<usize>,
}

impl ProcessProbe {
    pub fn exit(&self) {
        self.0.alive.set(false);
    }

    pub fn handles(&self) -> usize {
        self.0.handles.get()
    }
}

#[doc(hidden)]
pub struct ScriptedProcess(ProcessProbe);

impl Lifetime for ScriptedProcess {
    fn identity(&self) -> Option<ProcessIdentity> {
        Some(self.0.0.identity)
    }

    fn image_path(&self) -> Option<String> {
        Some(self.0.0.path.clone())
    }

    fn alive(&self) -> bool {
        self.0.0.alive.get()
    }
}

impl Drop for ScriptedProcess {
    fn drop(&mut self) {
        self.0.0.handles.set(self.0.handles() - 1);
    }
}

impl Default for Script {
    fn default() -> Self {
        Self {
            state: Rc::new(RefCell::new(State {
                active: true,
                session: 1,
                window: 0,
                processes: BTreeMap::new(),
                callbacks: None,
                during_lookup: None,
                deactivate_during_lookup: false,
            })),
            registration: Arc::new(AtomicBool::new(false)),
        }
    }
}

impl Script {
    pub fn start(
        &mut self,
        version: i32,
        instance: Guid,
        stop: Arc<AtomicBool>,
    ) -> io::Result<ScriptedWatcher> {
        let registration = Arc::clone(&self.registration);
        let watcher = ForegroundWatcher::spawn(
            ScriptedDesktop(Rc::clone(&self.state)),
            version,
            instance,
            stop,
            move |_, _| Ok(ScriptedRegistration::new(registration)),
        )?;
        self.state.borrow_mut().callbacks =
            Some(watcher.worker.as_ref().unwrap().callbacks.clone());
        Ok(watcher)
    }

    pub fn process(&self, window: usize, pid: u32, generation: u64, path: &str) -> ProcessProbe {
        let probe = ProcessProbe(Rc::new(ProcessState {
            identity: process_identity(pid, generation).expect("scripted process identity"),
            path: path.to_owned(),
            alive: Cell::new(true),
            handles: Cell::new(0),
        }));
        self.state
            .borrow_mut()
            .processes
            .insert(window, probe.clone());
        probe
    }

    pub fn focus(&self, window: usize) {
        self.state.borrow_mut().window = window;
        self.announce(window);
    }

    pub fn announce(&self, window: usize) {
        self.record(Notice::Window(window));
    }

    pub fn desktop_switch(&self) {
        self.record(Notice::SampleCurrent);
    }

    fn record(&self, notice: Notice) {
        if let Some(callbacks) = &self.state.borrow().callbacks {
            callbacks.record(notice);
        }
    }

    /// The console locks while the next path lookup runs.
    pub fn deactivate_during_lookup(&self) {
        self.state.borrow_mut().deactivate_during_lookup = true;
    }

    pub fn supersede_during_lookup(&self, window: usize) {
        self.state.borrow_mut().during_lookup = Some(window);
    }

    pub fn active(&self, value: bool) {
        self.state.borrow_mut().active = value;
    }

    /// The active console session id. A different id is a different user.
    pub fn session(&self, id: u32) {
        self.state.borrow_mut().session = id;
    }

    pub fn registered(&self) -> bool {
        self.registration.load(Ordering::Acquire)
    }
}

impl Desktop for ScriptedDesktop {
    type Process = ScriptedProcess;

    fn active_local_session(&mut self) -> Option<NonZeroU32> {
        {
            let state = self.0.borrow();
            NonZeroU32::new(if state.active { state.session } else { 0 })
        }
    }

    fn foreground(&mut self) -> usize {
        self.0.borrow().window
    }

    fn owner(&mut self, window: usize) -> Option<Owner> {
        Some(Owner {
            thread: NonZeroU32::MIN,
            pid: self.0.borrow().processes.get(&window)?.0.identity.pid,
        })
    }

    fn process_session(&mut self, _: NonZeroU32) -> Option<NonZeroU32> {
        NonZeroU32::new(self.0.borrow().session)
    }

    fn open_query_and_synchronize(&mut self, pid: NonZeroU32) -> Option<Self::Process> {
        let state = self.0.borrow();
        let probe = state.processes.get(&state.window)?;
        if probe.0.identity.pid != pid {
            return None;
        }
        probe.0.handles.set(probe.handles() + 1);
        Some(ScriptedProcess(probe.clone()))
    }

    fn full_path(&mut self, input: &str) -> Option<String> {
        let mut state = self.0.borrow_mut();
        if std::mem::take(&mut state.deactivate_during_lookup) {
            state.active = false;
        }
        if let Some(window) = state.during_lookup.take() {
            state.window = window;
            if let Some(callbacks) = &state.callbacks {
                callbacks.record(Notice::Window(window));
            }
        }
        Some(input.to_owned())
    }

    fn paths_equal(first: &str, second: &str) -> bool {
        first.eq_ignore_ascii_case(second)
    }
}

struct ScriptedRegistration(Arc<AtomicBool>, thread::ThreadId);

impl ScriptedRegistration {
    fn new(registered: Arc<AtomicBool>) -> Self {
        registered.store(true, Ordering::Release);
        Self(registered, thread::current().id())
    }
}

impl Registration for ScriptedRegistration {
    fn pump(&mut self) -> io::Result<bool> {
        assert_eq!(self.1, thread::current().id());
        thread::park_timeout(Duration::from_millis(2));
        Ok(true)
    }
}

impl Drop for ScriptedRegistration {
    fn drop(&mut self) {
        assert_eq!(self.1, thread::current().id());
        self.0.store(false, Ordering::Release);
    }
}

#[cfg(test)]
mod tests;

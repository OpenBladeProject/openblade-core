use std::{
    cell::RefCell,
    sync::{
        Arc, Mutex,
        atomic::{AtomicBool, Ordering::SeqCst},
        mpsc::sync_channel,
    },
    time::{Duration, Instant},
};

use openblade_domain::function_row::{
    Action, Backend, Device, Lifecycle, Observation, Phase, PrimaryMode, Status, Stroke,
    WindowsHook,
};
use openblade_platform_windows::{
    agent_diagnostics::{Event, FunctionRowNotice},
    function_row_hook::{
        HookError, StartupAdmission,
        executor::{ActionEffects, Direction, DisplayBrightnessUsage, EffectOutcome, MediaEffect},
    },
};
use serde_json::{Value, json};
use tokio::sync::watch;

use super::{
    Retire, Signals, Timing,
    admission::{Admission, DriverMode, LEASE, Projection, Sink, publish_status},
    effects::StageEffects,
    host::{Hooks, RETRY, Retirement, Running, Supervisor},
    ownership::{Guard, Journal, JournalGuard},
    preference::{FilePreference, Preference, Unreadable, parse},
    serve,
};
use crate::{
    service_status::Snapshot,
    session_native::service_client::{StatusUpdate, SubscriptionInitial},
};

mod vectors;

const FRAMES: &[u8] = include_bytes!("../../../tests/fixtures/get-status-v91.frames");

/// The managed v91 fixture status with the given members replaced (`None`
/// removes one).
pub(super) fn status(members: &[(&str, Option<Value>)]) -> Value {
    let request = i32::from_le_bytes(FRAMES[..4].try_into().unwrap()) as usize;
    let mut frame: Value = serde_json::from_slice(&FRAMES[4 + request + 4..]).unwrap();
    let status = frame["status"].as_object_mut().unwrap();
    for (name, value) in members {
        match value {
            Some(value) => status.insert((*name).into(), value.clone()),
            None => status.remove(*name),
        };
    }
    frame["status"].take()
}

pub(super) fn snapshot(members: &[(&str, Option<Value>)]) -> Snapshot {
    serde_json::from_value(status(members)).unwrap()
}

fn verified(model: &str, driver: Value, primary: Value) -> Snapshot {
    snapshot(&[
        ("deviceVerified", Some(json!(true))),
        ("firmware", Some(firmware(model))),
        ("keyboardDriverModeActive", Some(driver)),
        ("functionKeyPrimary", Some(primary)),
    ])
}

fn firmware(model: &str) -> Value {
    json!({"biosVersion": "4.00", "ecVersion": "1", "mcuVersion": "1", "vbiosVersion": "1",
        "modelNumber": model, "vbiosGpuName": "Unavailable",
        "thunderboltFirmwareVersion": "Unavailable", "touchpadFirmwareVersion": "Unavailable",
        "thunderboltFirmwareAccess": null, "touchpadFirmwareAccess": null})
}

fn initial() -> SubscriptionInitial {
    crate::session_native::service_client::tests::fixture_initial()
        .unwrap()
        .1
}

fn event(snapshot: Snapshot) -> StatusUpdate {
    let mut observation = initial().into_observation();
    observation.snapshot = snapshot;
    StatusUpdate::Event(observation)
}

// ---- Status projection ----

#[test]
fn driver_mode_primary_and_device_are_parsed_strictly() {
    for (value, expected) in [
        (Some(json!(true)), DriverMode::Active),
        (Some(json!(false)), DriverMode::Inactive),
        (Some(Value::Null), DriverMode::Unknown),
        (None, DriverMode::Unknown),
        (Some(json!("true")), DriverMode::Unknown),
        (Some(json!(1)), DriverMode::Unknown),
        (Some(json!({})), DriverMode::Unknown),
    ] {
        let members = [
            ("deviceVerified", Some(json!(true))),
            ("keyboardDriverModeActive", value.clone()),
        ];
        assert_eq!(
            Projection::of(&snapshot(&members)).driver_mode,
            expected,
            "{value:?}"
        );
    }
    for (value, expected) in [
        (Some(json!(0)), Some(PrimaryMode::FunctionKeys)),
        (Some(json!(1)), Some(PrimaryMode::MultimediaKeys)),
        (Some(json!(2)), None),
        (Some(json!(-1)), None),
        (Some(json!(1.5)), None),
        (Some(json!("1")), None),
        (Some(json!("MultimediaKeys")), None),
        (Some(Value::Null), None),
        (None, None),
    ] {
        let members = [
            ("deviceVerified", Some(json!(true))),
            ("functionKeyPrimary", value.clone()),
        ];
        assert_eq!(
            Projection::of(&snapshot(&members)).primary,
            expected,
            "{value:?}"
        );
    }
    // Managed resets both while the laptop is unverified.
    let unverified = Projection::of(&snapshot(&[
        ("deviceVerified", Some(json!(false))),
        ("firmware", Some(firmware("RZ09-0581"))),
        ("keyboardDriverModeActive", Some(json!(true))),
        ("functionKeyPrimary", Some(json!(1))),
    ]));
    assert_eq!(
        unverified,
        Projection {
            device: None,
            driver_mode: DriverMode::Unknown,
            primary: None
        }
    );
    let device = |members: &[(&str, Option<Value>)]| Projection::of(&snapshot(members)).device;
    assert_eq!(
        device(&[
            ("deviceVerified", Some(json!(true))),
            ("firmware", Some(firmware("RZ09-0581")))
        ]),
        Some(Device::Rz09_0581)
    );
    for members in [
        // Unverified: managed classifies no input device.
        vec![
            ("deviceVerified", Some(json!(false))),
            ("firmware", Some(firmware("RZ09-0581"))),
        ],
        // Other models have their own function-row inputs and are refused.
        vec![
            ("deviceVerified", Some(json!(true))),
            ("firmware", Some(firmware("RZ09-0528"))),
        ],
        vec![
            ("deviceVerified", Some(json!(true))),
            ("firmware", Some(firmware("RZ09-0484"))),
        ],
        vec![
            ("deviceVerified", Some(json!(true))),
            ("firmware", Some(firmware("rz09-0581"))),
        ],
        vec![("deviceVerified", Some(json!(true))), ("firmware", None)],
        vec![
            ("deviceVerified", Some(json!(true))),
            ("firmware", Some(Value::Null)),
        ],
        vec![
            ("deviceVerified", Some(json!(true))),
            ("firmware", Some(json!({"modelNumber": 581}))),
        ],
    ] {
        assert_eq!(device(&members), None, "{members:?}");
    }
}

// ---- Admission ----

#[derive(Clone, Debug, PartialEq)]
enum Published {
    Observation(Observation, Instant),
    Invalidated,
}

#[derive(Clone, Default)]
struct Recorder(Arc<Mutex<Vec<Published>>>);

impl Recorder {
    fn take(&self) -> Vec<Published> {
        std::mem::take(&mut self.0.lock().unwrap())
    }
    fn last(&self) -> Option<Published> {
        self.0.lock().unwrap().last().cloned()
    }
}

impl Sink for Recorder {
    fn publish(&self, observation: Observation, valid_until: Instant) {
        self.0
            .lock()
            .unwrap()
            .push(Published::Observation(observation, valid_until));
    }
    fn invalidate(&self) {
        self.0.lock().unwrap().push(Published::Invalidated);
    }
}

fn attached() -> (Admission, Recorder) {
    let admission = Admission::default();
    let recorder = Recorder::default();
    admission.attach(Box::new(recorder.clone()));
    recorder.take();
    (admission, recorder)
}

const MEDIA_ROW: [u16; 12] = [
    0x3b, 0x3c, 0x3d, 0x3e, 0x3f, 0x40, 0x41, 0x42, 0x43, 0x44, 0x57, 0x58,
];

/// Every fresh press under this observation passes through.
fn passes_every_key(observation: Observation) -> bool {
    MEDIA_ROW.iter().all(|&scan_code| {
        let mut policy = WindowsHook::default();
        let decision = policy.handle(
            Stroke {
                scan_code,
                down: true,
            },
            false,
            observation,
        );
        !decision.suppress && decision.event.is_none()
    })
}

#[test]
fn unknown_inactive_or_unselected_admission_passes_every_key_through() {
    let now = Instant::now();
    for driver in [json!(null), json!(false), json!("true")] {
        let (admission, recorder) = attached();
        admission.select(Some(Backend::WindowsHook));
        admission.observe_fn(Some(false));
        admission.observe(
            Projection::of(&verified("RZ09-0581", driver.clone(), json!(1))),
            now + LEASE,
        );
        let Some(Published::Observation(observation, _)) = recorder.last() else {
            panic!("published");
        };
        assert!(passes_every_key(observation), "{driver}");
    }
    // Confirmed Driver mode without the selected backend, Fn or primary mode.
    let (admission, recorder) = attached();
    admission.observe_fn(Some(false));
    admission.observe(
        Projection::of(&verified("RZ09-0581", json!(true), json!(1))),
        now + LEASE,
    );
    let Some(Published::Observation(observation, _)) = recorder.last() else {
        panic!("published");
    };
    assert!(passes_every_key(observation));
    admission.select(Some(Backend::WindowsHook));
    admission.observe_fn(None);
    let Some(Published::Observation(observation, _)) = recorder.last() else {
        panic!("published");
    };
    assert!(passes_every_key(observation));
    admission.observe_fn(Some(false));
    admission.observe(
        Projection::of(&verified("RZ09-0581", json!(true), Value::Null)),
        now + LEASE,
    );
    let Some(Published::Observation(observation, _)) = recorder.last() else {
        panic!("published");
    };
    assert!(passes_every_key(observation));
}

#[test]
fn production_admission_never_admits_a_press_without_an_fn_source() {
    // No Fn observer is ported, so the no-op effects are unreachable.
    let now = Instant::now();
    for model in ["RZ09-0581", "RZ09-0528", "RZ09-0484"] {
        for driver in [json!(true), json!(false), json!(null)] {
            for primary in [json!(0), json!(1), json!(null)] {
                let (admission, recorder) = attached();
                admission.select(Some(Backend::WindowsHook));
                admission.observe(
                    Projection::of(&verified(model, driver.clone(), primary.clone())),
                    now + LEASE,
                );
                let Some(Published::Observation(observation, _)) = recorder.last() else {
                    panic!("published");
                };
                assert_eq!(observation.fn_pressed, None);
                assert!(passes_every_key(observation));
            }
        }
    }
}

#[test]
fn active_driver_mode_admits_only_the_rz09_0581_decisions() {
    let now = Instant::now();
    let (admission, recorder) = attached();
    admission.select(Some(Backend::WindowsHook));
    admission.observe_fn(Some(false));
    admission.observe(
        Projection::of(&verified("RZ09-0581", json!(true), json!(1))),
        now + LEASE,
    );
    let Some(Published::Observation(observation, until)) = recorder.last() else {
        panic!("published");
    };
    assert_eq!(until, now + LEASE);
    assert_eq!(
        observation,
        Observation {
            exact_device: Some(Device::Rz09_0581),
            selected_backend: Some(Backend::WindowsHook),
            status: Status::FirmwareDriverMode { active: true },
            fn_pressed: Some(false),
            primary_mode: Some(PrimaryMode::MultimediaKeys),
            lifecycle: Lifecycle::Active,
        }
    );
    let mut policy = WindowsHook::default();
    let down = policy.handle(
        Stroke {
            scan_code: 0x3c,
            down: true,
        },
        false,
        observation,
    );
    assert!(down.suppress);
    assert_eq!(down.event.unwrap().action, Action::VolumeDown);
    assert_eq!(down.event.unwrap().phase, Phase::Down);
    // Fn held in multimedia mode is the F-key layer.
    admission.observe_fn(Some(true));
    let Some(Published::Observation(observation, _)) = recorder.last() else {
        panic!("published");
    };
    assert!(passes_every_key(observation));

    // The same status from RZ09-0528 or RZ09-0484 names no admitted device.
    for model in ["RZ09-0528", "RZ09-0484"] {
        admission.observe_fn(Some(false));
        admission.observe(
            Projection::of(&verified(model, json!(true), json!(1))),
            now + LEASE,
        );
        let Some(Published::Observation(observation, _)) = recorder.last() else {
            panic!("published");
        };
        assert_eq!(observation.exact_device, None);
        assert!(passes_every_key(observation));
        assert!(!admission.admits_install(now));
    }
}

#[test]
fn reconnect_origin_loss_and_stale_status_invalidate() {
    let (admission, recorder) = attached();
    let current = event(verified("RZ09-0581", json!(true), json!(1)));
    publish_status(&admission, &current, || Ok(())).unwrap();
    assert!(matches!(recorder.last(), Some(Published::Observation(..))));
    assert!(admission.admits_install(Instant::now()));

    publish_status(&admission, &StatusUpdate::Reconnecting, || Ok(())).unwrap();
    assert_eq!(recorder.last(), Some(Published::Invalidated));
    assert!(!admission.admits_install(Instant::now()));
    // A renewal cannot revive what a reconnect made unknown.
    admission.renew(Instant::now());
    assert_eq!(recorder.last(), Some(Published::Invalidated));

    publish_status(&admission, &current, || Ok(())).unwrap();
    let error = publish_status(&admission, &current, || {
        Err(std::io::Error::new(
            std::io::ErrorKind::PermissionDenied,
            "origin lost",
        ))
    })
    .unwrap_err();
    assert_eq!(error.kind(), std::io::ErrorKind::PermissionDenied);
    assert_eq!(recorder.last(), Some(Published::Invalidated));

    // A new subscription's initial status is admitted again on its own.
    let mut opened = initial();
    publish_status(&admission, &StatusUpdate::Opened(opened), || Ok(())).unwrap();
    // The fixture status is unverified: published, but names no device.
    let Some(Published::Observation(observation, _)) = recorder.last() else {
        panic!("published");
    };
    assert_eq!(observation.exact_device, None);

    opened = initial();
    // Real time: the observation clock is not paused outside a runtime.
    std::thread::sleep(Duration::from_millis(2100));
    let error = publish_status(&admission, &StatusUpdate::Opened(opened), || Ok(())).unwrap_err();
    assert_eq!(error.kind(), std::io::ErrorKind::TimedOut);
    assert_eq!(recorder.last(), Some(Published::Invalidated));
}

#[test]
fn freshness_bounds_the_admission_and_renewal_extends_only_a_known_status() {
    let start = Instant::now();
    let (admission, recorder) = attached();
    let projection = Projection::of(&verified("RZ09-0581", json!(true), json!(1)));
    admission.observe(projection, start + Duration::from_millis(10));
    assert!(admission.admits_install(start));
    // Expired: the hook reads UNKNOWN after this instant, and no install starts.
    assert!(!admission.admits_install(start + Duration::from_millis(10)));
    admission.renew(start + Duration::from_millis(5));
    assert_eq!(
        recorder.last(),
        Some(Published::Observation(
            projection.observation(None, None),
            start + Duration::from_millis(5) + LEASE
        ))
    );
    // A renewal never shortens the lease.
    recorder.take();
    admission.renew(start);
    assert!(recorder.take().is_empty());
    // Absent status: nothing to renew, and a detached admission publishes nothing.
    admission.invalidate();
    admission.detach();
    recorder.take();
    admission.observe(projection, start + LEASE);
    assert!(recorder.take().is_empty());
}

// ---- Supervisor ----

thread_local! {
    static EVENTS: RefCell<Vec<Event>> = const { RefCell::new(Vec::new()) };
}

fn record(event: Event) {
    EVENTS.with(|events| events.borrow_mut().push(event));
}

fn events() -> Vec<Event> {
    EVENTS.with(|events| std::mem::take(&mut *events.borrow_mut()))
}

fn ignore(_: Event) {}

#[derive(Default)]
struct HookState {
    starts: Vec<StartupAdmission>,
    stops: usize,
    finished: bool,
    retirement: Option<Retirement>,
    start_error: bool,
    /// What the hook last received when it was asked to stop.
    at_stop: Vec<Option<Published>>,
}

#[derive(Clone, Default)]
struct FakeHooks(Arc<Mutex<HookState>>, Recorder);

struct FakeRunning(Arc<Mutex<HookState>>, Recorder);

impl Hooks for FakeHooks {
    type Running = FakeRunning;
    fn start(
        &mut self,
        startup: StartupAdmission,
        admission: &Admission,
    ) -> std::io::Result<FakeRunning> {
        let mut state = self.0.lock().unwrap();
        if state.start_error {
            return Err(std::io::Error::other("spawn failed"));
        }
        state.starts.push(startup);
        state.finished = false;
        admission.attach(Box::new(self.1.clone()));
        Ok(FakeRunning(Arc::clone(&self.0), self.1.clone()))
    }
}

impl Running for FakeRunning {
    fn stop(&self) {
        let mut state = self.0.lock().unwrap();
        state.stops += 1;
        state.at_stop.push(self.1.last());
    }
    fn finished(&self) -> bool {
        self.0.lock().unwrap().finished
    }
    fn join(self) -> Retirement {
        self.0
            .lock()
            .unwrap()
            .retirement
            .take()
            .unwrap_or(Retirement {
                clean: true,
                error: None,
            })
    }
}

#[derive(Clone)]
struct Scripted<T>(Arc<Mutex<T>>);

impl Preference for Scripted<Option<Backend>> {
    fn poll(&mut self, _: Instant) -> Option<Backend> {
        *self.0.lock().unwrap()
    }
}

impl Guard for Scripted<Journal> {
    fn inspect(&mut self) -> Journal {
        *self.0.lock().unwrap()
    }
}

struct Rig {
    hooks: FakeHooks,
    preference: Scripted<Option<Backend>>,
    journal: Scripted<Journal>,
    admission: Arc<Admission>,
}

type FakeSupervisor =
    Supervisor<FakeHooks, Scripted<Option<Backend>>, Scripted<Journal>, fn() -> bool>;

impl Rig {
    fn new() -> Self {
        Self {
            hooks: FakeHooks::default(),
            preference: Scripted(Arc::new(Mutex::new(Some(Backend::WindowsHook)))),
            journal: Scripted(Arc::new(Mutex::new(Journal::Clear))),
            admission: Arc::new(Admission::default()),
        }
    }
    fn supervisor(&self, local: fn() -> bool, record: fn(Event)) -> FakeSupervisor {
        Supervisor::new(
            self.hooks.clone(),
            self.preference.clone(),
            self.journal.clone(),
            local,
            Arc::clone(&self.admission),
            record,
        )
    }
    fn status(&self, model: &str, now: Instant) {
        self.admission.observe(
            Projection::of(&verified(model, json!(true), json!(1))),
            now + Duration::from_secs(60),
        );
    }
    fn starts(&self) -> usize {
        self.hooks.0.lock().unwrap().starts.len()
    }
    fn finish(&self, retirement: Retirement) {
        let mut state = self.hooks.0.lock().unwrap();
        state.finished = true;
        state.retirement = Some(retirement);
    }
}

fn local() -> bool {
    true
}

fn remote() -> bool {
    false
}

#[test]
fn installs_only_for_a_fresh_rz09_0581_status_with_the_hook_selected() {
    events();
    let now = Instant::now();
    let rig = Rig::new();
    let mut supervisor = rig.supervisor(local, record);
    // No status yet, then other models.
    supervisor.step(now, false).unwrap();
    for model in ["RZ09-0528", "RZ09-0484"] {
        rig.status(model, now);
        supervisor.step(now, false).unwrap();
    }
    assert_eq!(rig.starts(), 0);
    // The preference does not select the hook.
    rig.status("RZ09-0581", now);
    for backend in [None, Some(Backend::RazerDriver)] {
        *rig.preference.0.lock().unwrap() = backend;
        supervisor.step(now, false).unwrap();
    }
    assert_eq!(rig.starts(), 0);
    // Not the active console.
    *rig.preference.0.lock().unwrap() = Some(Backend::WindowsHook);
    let mut away = rig.supervisor(remote, record);
    away.step(now, false).unwrap();
    assert_eq!(rig.starts(), 0);

    supervisor.step(now, false).unwrap();
    let state = rig.hooks.0.lock().unwrap();
    assert_eq!(state.starts.len(), 1);
    let startup = state.starts[0];
    assert_eq!(startup.requested_backend, Some(Backend::WindowsHook));
    assert!(startup.local_session_confirmed && startup.startup_recovery_clear);
    assert!(startup.driver_can_transfer && startup.previous_windows_hook_retired);
    drop(state);
    // The hook's observation carries the selected backend.
    let Some(Published::Observation(observation, _)) = rig.hooks.1.last() else {
        panic!("published");
    };
    assert_eq!(observation.selected_backend, Some(Backend::WindowsHook));
    assert!(events().is_empty());
}

#[test]
fn a_driver_journal_refuses_installation_until_it_is_gone() {
    let now = Instant::now();
    let rig = Rig::new();
    rig.status("RZ09-0581", now);
    events();
    let mut supervisor = rig.supervisor(local, record);
    let mut at = now;
    for journal in [Journal::Present, Journal::Unobservable] {
        *rig.journal.0.lock().unwrap() = journal;
        for _ in 0..3 {
            supervisor.step(at, false).unwrap();
            at += RETRY;
        }
    }
    assert_eq!(rig.starts(), 0);
    // One notice per condition, not per attempt.
    assert_eq!(
        events(),
        [
            Event::FunctionRow(FunctionRowNotice::DriverOwnershipJournalPresent),
            Event::FunctionRow(FunctionRowNotice::DriverOwnershipUnobservable),
        ]
    );
    *rig.journal.0.lock().unwrap() = Journal::Clear;
    supervisor.step(at, false).unwrap();
    assert_eq!(rig.starts(), 1);
}

#[test]
fn a_backend_change_retires_the_hook_and_reinstalls_only_after_clean_release() {
    events();
    let now = Instant::now();
    let rig = Rig::new();
    rig.status("RZ09-0581", now);
    let mut supervisor = rig.supervisor(local, record);
    supervisor.step(now, false).unwrap();
    assert_eq!(rig.starts(), 1);

    *rig.preference.0.lock().unwrap() = Some(Backend::RazerDriver);
    supervisor.step(now, false).unwrap();
    supervisor.step(now, false).unwrap();
    assert_eq!(rig.hooks.0.lock().unwrap().stops, 1);
    // Pass-through before the drain: the hook already held no backend when it
    // was asked to stop.
    let [Some(Published::Observation(observation, _))] = rig.hooks.0.lock().unwrap().at_stop[..]
    else {
        panic!("published before the stop");
    };
    assert_eq!(observation.selected_backend, None);
    // Still running: the selection returning must not start a second hook.
    *rig.preference.0.lock().unwrap() = Some(Backend::WindowsHook);
    supervisor.step(now, false).unwrap();
    assert_eq!(rig.starts(), 1);

    rig.finish(Retirement {
        clean: true,
        error: None,
    });
    supervisor.step(now, false).unwrap();
    assert_eq!(
        events(),
        [Event::FunctionRow(FunctionRowNotice::HookRetired {
            clean: true,
            error: None
        })]
    );
    assert_eq!(rig.hooks.1.last(), Some(Published::Invalidated));
    // Reinstalled only after the retry delay, with the clean release proved.
    supervisor.step(now + RETRY / 2, false).unwrap();
    assert_eq!(rig.starts(), 1);
    supervisor.step(now + RETRY, false).unwrap();
    let state = rig.hooks.0.lock().unwrap();
    assert_eq!(state.starts.len(), 2);
    assert!(state.starts[1].previous_windows_hook_retired);
}

#[test]
fn an_unclean_or_failed_run_is_never_replaced() {
    for retirement in [
        Retirement {
            clean: false,
            error: None,
        },
        Retirement {
            clean: true,
            error: Some(HookError::Install(5)),
        },
        Retirement {
            clean: true,
            error: Some(HookError::AlreadyOwned),
        },
        Retirement {
            clean: false,
            error: Some(HookError::Unhook {
                code: 5,
                message_loop: None,
            }),
        },
    ] {
        let now = Instant::now();
        let rig = Rig::new();
        rig.status("RZ09-0581", now);
        let mut supervisor = rig.supervisor(local, ignore);
        supervisor.step(now, false).unwrap();
        rig.finish(retirement);
        for tick in 1..5 {
            supervisor.step(now + RETRY * tick, false).unwrap();
        }
        assert_eq!(rig.starts(), 1, "{retirement:?}");
        // Stopping needs nothing more.
        assert!(supervisor.step(now + RETRY * 6, true).unwrap());
    }
    // A refused start retired cleanly may be retried.
    let now = Instant::now();
    let rig = Rig::new();
    rig.status("RZ09-0581", now);
    let mut supervisor = rig.supervisor(local, ignore);
    supervisor.step(now, false).unwrap();
    rig.finish(Retirement {
        clean: true,
        error: Some(HookError::StartupRefused),
    });
    supervisor.step(now, false).unwrap();
    supervisor.step(now + RETRY, false).unwrap();
    assert_eq!(rig.starts(), 2);
}

#[test]
fn stop_retires_the_running_hook_before_reporting_done() {
    let now = Instant::now();
    let rig = Rig::new();
    rig.status("RZ09-0581", now);
    let mut supervisor = rig.supervisor(local, ignore);
    supervisor.step(now, false).unwrap();
    assert!(!supervisor.step(now, true).unwrap());
    assert!(!supervisor.step(now, true).unwrap());
    assert_eq!(rig.hooks.0.lock().unwrap().stops, 1);
    let [Some(Published::Observation(observation, _))] = rig.hooks.0.lock().unwrap().at_stop[..]
    else {
        panic!("published before the stop");
    };
    assert_eq!(observation.selected_backend, None);
    rig.finish(Retirement {
        clean: true,
        error: None,
    });
    assert!(supervisor.step(now, true).unwrap());
    // Nothing new starts while stopping.
    assert!(supervisor.step(now + RETRY * 3, true).unwrap());
    assert_eq!(rig.starts(), 1);

    // A start failure is the role's failure and leaves nothing attached.
    let rig = Rig::new();
    rig.status("RZ09-0581", now);
    rig.hooks.0.lock().unwrap().start_error = true;
    let mut supervisor = rig.supervisor(local, ignore);
    assert!(supervisor.step(now, false).is_err());
    assert!(rig.hooks.1.take().is_empty());
}

// ---- The async role ----

/// Reads the supervisor's stop flag whenever the hook is invalidated.
struct StopWitness(Arc<Signals>, Arc<Mutex<Vec<bool>>>);

impl Sink for StopWitness {
    fn publish(&self, _: Observation, _: Instant) {}
    fn invalidate(&self) {
        self.1.lock().unwrap().push(self.0.stop.load(SeqCst));
    }
}

#[test]
fn stop_passes_every_key_through_before_the_supervisor_can_see_it() {
    for dropped in [false, true] {
        let (wake, wakes) = sync_channel(1);
        let signals = Arc::new(Signals {
            stop: AtomicBool::new(false),
            wake,
        });
        let admission = Arc::new(Admission::default());
        let seen = Arc::new(Mutex::new(Vec::new()));
        admission.observe(
            Projection::of(&verified("RZ09-0581", json!(true), json!(1))),
            Instant::now() + Duration::from_secs(60),
        );
        admission.attach(Box::new(StopWitness(
            Arc::clone(&signals),
            Arc::clone(&seen),
        )));
        if dropped {
            // A dropped role future keeps the same order.
            drop(Retire(Arc::clone(&signals), Arc::clone(&admission)));
        } else {
            signals.retire(&admission);
        }
        assert_eq!(*seen.lock().unwrap(), [false], "dropped: {dropped}");
        assert!(signals.stop.load(SeqCst));
        assert!(wakes.try_recv().is_ok());
        assert!(!admission.admits_install(Instant::now()));
    }
}

const FAST: Timing = Timing {
    renew: Duration::from_millis(10),
    tick: Duration::from_millis(5),
    retire: Duration::from_millis(500),
};

async fn until(mut condition: impl FnMut() -> bool) {
    let deadline = Instant::now() + Duration::from_secs(5);
    while !condition() {
        assert!(Instant::now() < deadline, "condition not reached");
        tokio::time::sleep(Duration::from_millis(5)).await;
    }
}

#[tokio::test]
async fn the_role_renews_while_running_and_invalidates_then_retires_on_stop() {
    let rig = Rig::new();
    // Short enough that every renewal extends it.
    rig.admission.observe(
        Projection::of(&verified("RZ09-0581", json!(true), json!(1))),
        Instant::now() + Duration::from_millis(500),
    );
    let (stop_tx, stop) = watch::channel(false);
    let hooks = rig.hooks.clone();
    let role = tokio::spawn({
        let supervisor = rig.supervisor(local, ignore);
        let admission = Arc::clone(&rig.admission);
        async move { serve(supervisor, admission, stop, || Ok(()), FAST).await }
    });
    until(|| rig.starts() == 1).await;
    // Renewal republishes with a lease from now.
    hooks.1.take();
    until(|| !hooks.1.0.lock().unwrap().is_empty()).await;
    let Some(Published::Observation(_, lease)) = hooks.1.last() else {
        panic!("renewed");
    };
    assert!(lease > Instant::now());

    stop_tx.send(true).unwrap();
    until(|| hooks.0.lock().unwrap().stops == 1).await;
    // The admission is already unknown before the hook drains.
    assert!(!rig.admission.admits_install(Instant::now()));
    assert!(!role.is_finished());
    rig.finish(Retirement {
        clean: true,
        error: None,
    });
    role.await.unwrap().unwrap();
}

#[tokio::test]
async fn origin_loss_invalidates_and_a_stuck_hook_bounds_the_stop() {
    let rig = Rig::new();
    rig.status("RZ09-0581", Instant::now());
    let (_stop_tx, stop) = watch::channel(false);
    let lost = Arc::new(std::sync::atomic::AtomicBool::new(false));
    let role = tokio::spawn({
        let supervisor = rig.supervisor(local, ignore);
        let admission = Arc::clone(&rig.admission);
        let lost = Arc::clone(&lost);
        async move {
            serve(
                supervisor,
                admission,
                stop,
                move || {
                    if lost.load(std::sync::atomic::Ordering::SeqCst) {
                        Err(std::io::Error::other("origin lost"))
                    } else {
                        Ok(())
                    }
                },
                FAST,
            )
            .await
        }
    });
    until(|| rig.starts() == 1).await;
    lost.store(true, std::sync::atomic::Ordering::SeqCst);
    // The hook never finishes: the stop gives up after the retire bound.
    let error = role.await.unwrap().unwrap_err();
    assert_eq!(error.to_string(), "origin lost");
    assert!(!rig.admission.admits_install(Instant::now()));
    assert_eq!(rig.hooks.0.lock().unwrap().stops, 1);
    assert_eq!(rig.hooks.1.last(), Some(Published::Invalidated));
    // Let the abandoned supervisor thread end.
    rig.finish(Retirement {
        clean: true,
        error: None,
    });
}

#[tokio::test(start_paused = true)]
async fn a_suspend_is_followed_by_one_renewal_not_a_burst() {
    use std::sync::atomic::AtomicUsize;
    let rig = Rig::new();
    rig.status("RZ09-0581", Instant::now());
    let (_stop_tx, stop) = watch::channel(false);
    let verifies = Arc::new(AtomicUsize::new(0));
    let role = tokio::spawn({
        let supervisor = rig.supervisor(local, ignore);
        let admission = Arc::clone(&rig.admission);
        let verifies = Arc::clone(&verifies);
        async move {
            let verify = move || {
                verifies.fetch_add(1, SeqCst);
                Ok(())
            };
            serve(supervisor, admission, stop, verify, Timing::INSTALLED).await
        }
    });
    let settle = || async {
        for _ in 0..1_000 {
            tokio::task::yield_now().await;
        }
    };
    settle().await;
    assert_eq!(verifies.load(SeqCst), 1, "the first tick is immediate");
    // Windows `Instant` keeps counting through sleep, so the clock jumps.
    for suspended in [Duration::from_secs(600), Duration::from_secs(8 * 3600)] {
        let before = verifies.load(SeqCst);
        tokio::time::advance(suspended).await;
        settle().await;
        assert_eq!(verifies.load(SeqCst) - before, 1, "{suspended:?}");
    }
    // The next renewal is a full period after the resumed one.
    let before = verifies.load(SeqCst);
    tokio::time::advance(Timing::INSTALLED.renew - Duration::from_millis(1)).await;
    settle().await;
    assert_eq!(verifies.load(SeqCst), before);
    tokio::time::advance(Duration::from_millis(1)).await;
    settle().await;
    assert_eq!(verifies.load(SeqCst), before + 1);
    role.abort();
    let _ = role.await;
    rig.finish(Retirement {
        clean: true,
        error: None,
    });
}

// ---- Preference, ownership and effects ----

#[test]
fn the_preference_follows_the_managed_converter() {
    use Backend::{RazerDriver, WindowsHook};
    type Parsed = Result<Option<Backend>, Unreadable>;
    let cases: &[(&[u8], Parsed)] = &[
        (b"{}", Ok(Some(WindowsHook))),
        (
            br#"{"functionRowBackend":"WindowsHook"}"#,
            Ok(Some(WindowsHook)),
        ),
        (
            br#"{"functionRowBackend":"razerdriver"}"#,
            Ok(Some(RazerDriver)),
        ),
        (
            br#"{"FunctionRowBackend":"RazerDriver"}"#,
            Ok(Some(RazerDriver)),
        ),
        (
            br#"{"functionRowBackend":" WindowsHook "}"#,
            Ok(Some(WindowsHook)),
        ),
        (br#"{"functionRowBackend":"1"}"#, Ok(Some(WindowsHook))),
        (
            br#"{"functionRowBackend":"WindowsHook,Unknown"}"#,
            Ok(Some(WindowsHook)),
        ),
        (
            br#"{"functionRowBackend":"WindowsHook,RazerDriver"}"#,
            Ok(None),
        ),
        (br#"{"functionRowBackend":"Unknown"}"#, Ok(None)),
        (br#"{"functionRowBackend":"Future"}"#, Ok(None)),
        (br#"{"functionRowBackend":""}"#, Ok(None)),
        (br#"{"functionRowBackend":2}"#, Ok(Some(RazerDriver))),
        (br#"{"functionRowBackend":1}"#, Ok(Some(WindowsHook))),
        (br#"{"functionRowBackend":3}"#, Ok(None)),
        (br#"{"functionRowBackend":1.0}"#, Ok(None)),
        (br#"{"functionRowBackend":4294967297}"#, Ok(None)),
        (br#"{"functionRowBackend":null}"#, Ok(None)),
        (br#"{"functionRowBackend":true}"#, Err(Unreadable)),
        (br#"{"functionRowBackend":[]}"#, Err(Unreadable)),
        (
            br#"{"functionRowBackend":"WindowsHook","functionrowbackend":2}"#,
            Err(Unreadable),
        ),
        (
            b"\xEF\xBB\xBF{\"functionRowBackend\":2}",
            Ok(Some(RazerDriver)),
        ),
        (b"[]", Err(Unreadable)),
        (b"{", Err(Unreadable)),
        (b"", Err(Unreadable)),
    ];
    for (content, expected) in cases {
        assert_eq!(
            &parse(Some(content)),
            expected,
            "{}",
            String::from_utf8_lossy(content)
        );
    }
    assert_eq!(parse(None), Ok(Some(WindowsHook)));
    let oversized = vec![b' '; 1024 * 1024 + 1];
    assert_eq!(parse(Some(&oversized)), Err(Unreadable));
}

fn scratch(name: &str) -> std::path::PathBuf {
    let directory = std::env::temp_dir().join(format!(
        "openblade-function-row-{name}-{}",
        std::process::id()
    ));
    let _ = std::fs::remove_dir_all(&directory);
    std::fs::create_dir_all(&directory).unwrap();
    directory
}

#[test]
fn the_file_preference_keeps_the_last_value_through_a_failed_read() {
    events();
    let directory = scratch("preference");
    let path = directory.join("preferences.json");
    let mut preference = FilePreference::new(path.clone(), record);
    let start = Instant::now();
    // Missing file: the managed default.
    assert_eq!(preference.poll(start), Some(Backend::WindowsHook));
    std::fs::write(&path, br#"{"functionRowBackend":"RazerDriver"}"#).unwrap();
    // Polled once a second, not on every step.
    assert_eq!(preference.poll(start), Some(Backend::WindowsHook));
    let mut now = start + Duration::from_secs(1);
    assert_eq!(preference.poll(now), Some(Backend::RazerDriver));
    std::fs::write(&path, b"{ not json").unwrap();
    for _ in 0..2 {
        now += Duration::from_secs(1);
        assert_eq!(preference.poll(now), Some(Backend::RazerDriver));
    }
    assert_eq!(
        events(),
        [Event::FunctionRow(FunctionRowNotice::PreferencesUnreadable)]
    );
    std::fs::write(&path, br#"{"functionRowBackend":"WindowsHook"}"#).unwrap();
    now += Duration::from_secs(1);
    assert_eq!(preference.poll(now), Some(Backend::WindowsHook));

    // Unreadable before any successful read: nothing is selected.
    std::fs::write(&path, b"[]").unwrap();
    let mut fresh = FilePreference::new(path, ignore);
    assert_eq!(fresh.poll(start), None);
    let _ = std::fs::remove_dir_all(directory);
}

#[test]
fn any_driver_journal_refuses_the_windows_hook() {
    let directory = scratch("journal");
    let mut guard = JournalGuard::new(directory.clone());
    assert_eq!(guard.inspect(), Journal::Clear);
    for name in [
        "function-row-driver-ownership.json",
        "snaptap-driver-ownership.json",
    ] {
        let path = directory.join(name);
        // Its content is not interpreted: even an empty file may own the row.
        std::fs::write(&path, b"").unwrap();
        assert_eq!(guard.inspect(), Journal::Present, "{name}");
        std::fs::remove_file(path).unwrap();
    }
    assert_eq!(guard.inspect(), Journal::Clear);
    let mut missing = JournalGuard::new(directory.join("absent"));
    assert_eq!(missing.inspect(), Journal::Clear);
    let _ = std::fs::remove_dir_all(directory);
}

#[test]
fn stage_effects_execute_nothing_and_record_each_action_once() {
    events();
    let mut effects = StageEffects::new(record);
    for _ in 0..2 {
        assert_eq!(
            effects.media(MediaEffect::VolumeMute),
            EffectOutcome::Unsupported
        );
        assert_eq!(
            effects.step_keyboard_brightness_until_power_source_change(Direction::Increase),
            EffectOutcome::Unsupported
        );
        // Unsupported, never NotSubmitted, so the executor takes no WMI route.
        assert_eq!(
            effects.display_brightness_usage(DisplayBrightnessUsage::Held(Direction::Decrease)),
            EffectOutcome::Unsupported
        );
        assert_eq!(
            effects.display_brightness_usage(DisplayBrightnessUsage::Released),
            EffectOutcome::NoOp
        );
    }
    assert_eq!(
        effects.adjust_wmi_brightness(Direction::Decrease),
        EffectOutcome::Unsupported
    );
    effects.show_brightness_osd(50);
    assert_eq!(
        events(),
        [
            Action::VolumeMute,
            Action::KeyboardBrightnessUp,
            Action::DisplayBrightnessDown,
        ]
        .map(|action| Event::FunctionRow(FunctionRowNotice::EffectNotAdmitted(action)))
    );
}

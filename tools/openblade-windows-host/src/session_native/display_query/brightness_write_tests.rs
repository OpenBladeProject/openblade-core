//! Manual setBrightness through the real feature-owned worker, the shared
//! WMI policy and a scripted native boundary, with injected power, fence and
//! durable storage. No Windows supplier, endpoint or hardware is used.
use super::*;
use crate::session_native::display_query::observation::{BrightnessOwner, fixture_query_owned};
use openblade_platform_windows::{
    brightness::{
        Acknowledgement, CallBudget, MethodRow, ReadingRow, Submission, TopologyFailure,
        WindowsBrightnessWorker, WmiBrightnessNative,
    },
    display::scripted::{RefreshTiming, Scenario},
};
use std::sync::atomic::{AtomicUsize, Ordering};

const LEVELS: [u8; 5] = [0, 25, 50, 75, 100];

fn panel() -> InternalDisplayTopology {
    Scenario::Available
        .topology_with_hdr(false, RefreshTiming::default(), false)
        .unwrap()
}

fn instance() -> String {
    let InternalDisplayTopology::Single(panel) = panel() else {
        panic!("panel")
    };
    let path = panel
        .monitor_name
        .unwrap()
        .device_path
        .unwrap()
        .into_string()
        .unwrap();
    let prefix = path
        .strip_prefix(r"\\?\")
        .unwrap_or(&path)
        .split('#')
        .take(3)
        .collect::<Vec<_>>()
        .join(r"\");
    format!("{prefix}_0")
}

#[derive(Clone, Copy, PartialEq, Eq)]
enum Mode {
    /// Acknowledge and change the level.
    Apply,
    /// Acknowledge without changing the level (readback mismatch).
    Ignore,
    /// Fail after the submission boundary.
    Timeout,
    /// Lose the worker thread after the submission boundary.
    Panic,
}

struct Wmi {
    percent: Mutex<u8>,
    mode: Mutex<Mode>,
    /// Native setter invocations, whether or not the fence admitted them.
    setters: AtomicUsize,
    /// Submissions that passed the fence and the gate.
    submitted: AtomicUsize,
    native: AtomicUsize,
}

impl Wmi {
    fn new(percent: u8, mode: Mode) -> Arc<Self> {
        Arc::new(Self {
            percent: Mutex::new(percent),
            mode: Mutex::new(mode),
            setters: AtomicUsize::new(0),
            submitted: AtomicUsize::new(0),
            native: AtomicUsize::new(0),
        })
    }
}

struct Native(Arc<Wmi>);
impl WmiBrightnessNative for Native {
    type Method = ();
    fn topology(&mut self, _: &CallBudget<'_>) -> Result<InternalDisplayTopology, TopologyFailure> {
        self.0.native.fetch_add(1, Ordering::SeqCst);
        Ok(panel())
    }
    fn active_readings(
        &mut self,
        _: &CallBudget<'_>,
        _: usize,
    ) -> Result<Vec<ReadingRow>, NativeFailure> {
        self.0.native.fetch_add(1, Ordering::SeqCst);
        Ok(vec![ReadingRow {
            instance: Some(instance()),
            percent: Some(*self.0.percent.lock().unwrap()),
            levels: Some(LEVELS.into()),
            data_failure: None,
        }])
    }
    fn active_methods(
        &mut self,
        _: &CallBudget<'_>,
        _: usize,
    ) -> Result<Vec<MethodRow<()>>, NativeFailure> {
        self.0.native.fetch_add(1, Ordering::SeqCst);
        Ok(vec![MethodRow {
            instance: Some(instance()),
            handle: (),
        }])
    }
    fn set_brightness(
        &mut self,
        _: (),
        level: u8,
        submission: &mut Submission<'_>,
    ) -> Result<Acknowledgement, NativeFailure> {
        self.0.setters.fetch_add(1, Ordering::SeqCst);
        submission.begin()?;
        self.0.submitted.fetch_add(1, Ordering::SeqCst);
        match *self.0.mode.lock().unwrap() {
            Mode::Apply => {
                *self.0.percent.lock().unwrap() = level;
                Ok(Acknowledgement::Status(0))
            }
            Mode::Ignore => Ok(Acknowledgement::Status(0)),
            Mode::Timeout => Err(NativeFailure::DeadlineExceeded),
            Mode::Panic => panic!("injected WMI worker loss after submission"),
        }
    }
}

struct TestFence {
    changed: bool,
    admit: bool,
    checks: Arc<AtomicUsize>,
}
impl SubmissionFence for TestFence {
    fn topology(&self) -> Result<InternalDisplayTopology, DisplayReadError> {
        self.checks.fetch_add(1, Ordering::SeqCst);
        Ok(if self.changed {
            InternalDisplayTopology::NoInternalDisplay
        } else {
            panel()
        })
    }
    fn admit(&self) -> bool {
        self.admit
    }
}

struct Host {
    power: Option<PowerContext>,
    changed: bool,
    admit: bool,
    fence_checks: Arc<AtomicUsize>,
    queries: usize,
}
impl Host {
    fn admitted(source: u8) -> Self {
        Self {
            power: Some(PowerContext {
                source,
                generation: 7,
            }),
            changed: false,
            admit: true,
            fence_checks: Arc::default(),
            queries: 0,
        }
    }
}
impl WriteHost for Host {
    fn power(&self) -> Option<PowerContext> {
        self.power
    }
    fn fence(&self, admitted: PowerContext) -> Arc<dyn SubmissionFence> {
        assert_eq!(Some(admitted), self.power, "fence binds the captured context");
        Arc::new(TestFence {
            changed: self.changed,
            admit: self.admit,
            checks: self.fence_checks.clone(),
        })
    }
    fn target(&mut self) -> Result<BrightnessTarget, BrightnessError> {
        let InternalDisplayTopology::Single(display) = panel() else {
            panic!("panel")
        };
        BrightnessTarget::from_display(&display)
    }
    fn query(&mut self, brightness: &SharedBrightness, canceled: &AtomicBool) -> io::Result<Snapshot> {
        self.queries += 1;
        fixture_query_owned(brightness.clone(), canceled)
    }
}

/// Shared bytes survive a simulated Agent restart; writes can be refused.
#[derive(Clone)]
struct Memory {
    bytes: Arc<Mutex<Option<Vec<u8>>>>,
    /// Successful writes beyond this count fail (persistence fault).
    allow: Arc<AtomicUsize>,
    writes: Arc<AtomicUsize>,
}
impl Default for Memory {
    fn default() -> Self {
        Self {
            bytes: Arc::default(),
            allow: Arc::new(AtomicUsize::new(usize::MAX)),
            writes: Arc::default(),
        }
    }
}
impl CheckpointFile for Memory {
    fn read(&mut self) -> io::Result<Option<Vec<u8>>> {
        Ok(self.bytes.lock().unwrap().clone())
    }
    fn replace(&mut self, bytes: &[u8]) -> io::Result<()> {
        if self.writes.load(Ordering::SeqCst) >= self.allow.load(Ordering::SeqCst) {
            return Err(io::Error::other("injected checkpoint failure"));
        }
        self.writes.fetch_add(1, Ordering::SeqCst);
        *self.bytes.lock().unwrap() = Some(bytes.to_vec());
        Ok(())
    }
}
impl Memory {
    fn record(&self) -> serde_json::Value {
        serde_json::from_slice(self.bytes.lock().unwrap().as_deref().unwrap()).unwrap()
    }
    /// A prior managed legacy checkpoint supplies the last reconciled source.
    fn legacy(source: u8) -> Self {
        let file = Self::default();
        *file.bytes.lock().unwrap() =
            Some(format!(r#"{{"SchemaVersion":1,"LastReconciledPowerSource":{source}}}"#).into());
        file
    }
}

struct Rig {
    wmi: Arc<Wmi>,
    owner: SharedBrightness,
    file: Memory,
    recovery: Mutex<Recovery<Memory>>,
}
impl Rig {
    fn new(percent: u8, mode: Mode, file: Memory) -> Self {
        let wmi = Wmi::new(percent, mode);
        let native = wmi.clone();
        let worker = WindowsBrightnessWorker::start_scripted(move || Native(native)).unwrap();
        Self {
            wmi,
            owner: Arc::new(Mutex::new(BrightnessOwner::with_worker(worker))),
            recovery: Mutex::new(Recovery::load(file.clone())),
            file,
        }
    }
    fn set(&self, host: &mut Host, expected: u8, percent: u8) -> io::Result<WriteOutcome> {
        self.set_canceled(host, expected, percent, &AtomicBool::new(false))
    }
    fn set_canceled(
        &self,
        host: &mut Host,
        expected: u8,
        percent: u8,
        canceled: &AtomicBool,
    ) -> io::Result<WriteOutcome> {
        set_brightness(
            host,
            &self.owner,
            &self.recovery,
            SetBrightness {
                expected_power_source: expected,
                percent,
            },
            canceled,
        )
    }
    fn submitted(&self) -> usize {
        self.wmi.submitted.load(Ordering::SeqCst)
    }
    fn setters(&self) -> usize {
        self.wmi.setters.load(Ordering::SeqCst)
    }
}
impl Drop for Rig {
    fn drop(&mut self) {
        let _ = self
            .owner
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner)
            .shutdown();
    }
}

fn flag(record: &serde_json::Value, name: &str) -> bool {
    record[name].as_bool().unwrap()
}

#[test]
fn nearest_supported_level_is_submitted_once_confirmed_and_recorded() {
    let rig = Rig::new(25, Mode::Apply, Memory::default());
    let mut host = Host::admitted(1);
    let outcome = rig.set(&mut host, 1, 40).unwrap();
    assert_eq!(outcome.status, Status::Succeeded);
    assert_eq!(outcome.error, None);
    // 40 selects the nearest WMI level, 50; the reply carries a fresh query.
    assert_eq!(outcome.state.brightness(), Some(50));
    assert_eq!((rig.setters(), rig.submitted()), (1, 1));
    assert_eq!(host.fence_checks.load(Ordering::SeqCst), 1);
    assert_eq!(host.queries, 1);
    // Begin and complete both reached durable storage before returning.
    assert_eq!(rig.file.writes.load(Ordering::SeqCst), 2);
    let record = rig.file.record();
    assert_eq!(record["LastReconciledPowerSource"], 1);
    assert!(flag(&record, "ManualHold"));
    assert!(!flag(&record, "AutomaticWritesPaused"));
    assert!(!flag(&record, "UncertainBrightness"));

    // The current level never invokes the setter again.
    let unchanged = rig.set(&mut host, 1, 50).unwrap();
    assert_eq!(unchanged.status, Status::Succeeded);
    assert_eq!(rig.setters(), 1);
}

#[test]
fn denied_admission_and_preparation_failures_never_reach_the_setter() {
    // Unconfirmed power: blocked after a durable manual hold, never prepared.
    let rig = Rig::new(25, Mode::Apply, Memory::legacy(0));
    let mut host = Host::admitted(0);
    host.power = None;
    let outcome = rig.set(&mut host, 0, 75).unwrap();
    assert_eq!(
        (outcome.status, outcome.error),
        (Status::Blocked, Some(UNCONFIRMED_POWER))
    );
    assert_eq!(rig.wmi.native.load(Ordering::SeqCst), 3, "only the reply query read WMI");
    // Blocked completion restores the inherited (unpaused) state.
    let record = rig.file.record();
    assert!(!flag(&record, "AutomaticWritesPaused") && !flag(&record, "UncertainBrightness"));

    // Changed power source.
    let mut host = Host::admitted(1);
    let outcome = rig.set(&mut host, 2, 75).unwrap();
    assert_eq!(
        (outcome.status, outcome.error),
        (Status::Blocked, Some(CHANGED_POWER))
    );

    // Origin, session or power generation changed after preparation.
    let mut host = Host::admitted(1);
    host.admit = false;
    let outcome = rig.set(&mut host, 1, 75).unwrap();
    assert_eq!((outcome.status, outcome.error), (Status::Blocked, Some(STALE)));
    assert_eq!(host.fence_checks.load(Ordering::SeqCst), 1);

    // Target changed after preparation.
    let mut host = Host::admitted(1);
    host.changed = true;
    let outcome = rig.set(&mut host, 1, 75).unwrap();
    assert_eq!(
        (outcome.status, outcome.error),
        (Status::Blocked, Some(PANEL_CHANGED))
    );

    // Canceled before submission (client loss, deadline or Stop).
    let mut host = Host::admitted(1);
    let outcome = rig
        .set_canceled(&mut host, 1, 75, &AtomicBool::new(true))
        .unwrap();
    assert_eq!(
        (outcome.status, outcome.error),
        (Status::Blocked, Some(CANCELED))
    );

    // The native setter was entered twice (fence refusals) but never submitted.
    assert_eq!((rig.setters(), rig.submitted()), (2, 0));
    let record = rig.file.record();
    assert!(!flag(&record, "AutomaticWritesPaused") && !flag(&record, "UncertainBrightness"));
    // Nothing was paused: an admitted request still succeeds afterwards.
    let mut host = Host::admitted(1);
    assert_eq!(rig.set(&mut host, 1, 75).unwrap().status, Status::Succeeded);
}

#[test]
fn checkpoint_failure_before_submission_blocks_without_device_interaction() {
    let file = Memory::legacy(1);
    file.allow.store(0, Ordering::SeqCst);
    let rig = Rig::new(25, Mode::Apply, file);
    let mut host = Host::admitted(1);
    let outcome = rig.set(&mut host, 1, 75).unwrap();
    assert_eq!(
        (outcome.status, outcome.error),
        (Status::Blocked, Some(CHECKPOINT))
    );
    assert_eq!(rig.setters(), 0);
    assert_eq!(host.fence_checks.load(Ordering::SeqCst), 0);
    // No prior checkpoint and no confirmed source: nothing can be saved.
    let rig = Rig::new(25, Mode::Apply, Memory::default());
    let mut host = Host::admitted(1);
    host.power = None;
    assert_eq!(rig.set(&mut host, 1, 75).unwrap().error, Some(CHECKPOINT));
    assert_eq!(rig.setters(), 0);
}

#[test]
fn possible_submission_pauses_without_replay_across_restart_until_explicit_read() {
    for mode in [Mode::Timeout, Mode::Ignore] {
        let rig = Rig::new(25, mode, Memory::default());
        let mut host = Host::admitted(2);
        let outcome = rig.set(&mut host, 2, 75).unwrap();
        assert_eq!(
            (outcome.status, outcome.error),
            (Status::Indeterminate, Some(UNCONFIRMED))
        );
        assert_eq!(rig.submitted(), 1);
        let record = rig.file.record();
        assert!(flag(&record, "AutomaticWritesPaused") && flag(&record, "UncertainBrightness"));

        // Same process: refused before any device interaction, never replayed.
        let native = rig.wmi.native.load(Ordering::SeqCst);
        let blocked = rig.set(&mut host, 2, 75).unwrap();
        assert_eq!((blocked.status, blocked.error), (Status::Blocked, Some(PAUSED)));
        assert_eq!(rig.submitted(), 1);
        assert_eq!(rig.wmi.native.load(Ordering::SeqCst) - native, 3, "reply query only");

        // Restart: the durable checkpoint restores the gate.
        let restarted = Rig::new(25, Mode::Apply, rig.file.clone());
        assert!(restarted.recovery.lock().unwrap().brightness_unresolved());
        let blocked = restarted.set(&mut host, 2, 75).unwrap();
        assert_eq!(blocked.error, Some(PAUSED));
        assert_eq!(restarted.setters(), 0);

        // An explicit read that observes brightness reopens manual writes.
        let read = fixture_query_owned(restarted.owner.clone(), &AtomicBool::new(false)).unwrap();
        restarted.recovery.lock().unwrap().explicit_read(&read);
        assert!(!restarted.recovery.lock().unwrap().brightness_unresolved());
        let outcome = restarted.set(&mut host, 2, 75).unwrap();
        assert_eq!(outcome.status, Status::Succeeded);
        assert_eq!(restarted.submitted(), 1);
    }
}

#[test]
fn uncertain_completion_persistence_latches_the_pause() {
    let rig = Rig::new(25, Mode::Apply, Memory::default());
    let mut host = Host::admitted(0);
    // Begin persists; the completion write then fails after a confirmed change.
    rig.file.allow.store(1, Ordering::SeqCst);
    let outcome = rig.set(&mut host, 0, 100).unwrap();
    assert_eq!(outcome.status, Status::Succeeded);
    assert!(rig.recovery.lock().unwrap().brightness_unresolved());
    // The durable bytes still hold the begin checkpoint (paused, uncertain).
    let record = rig.file.record();
    assert!(flag(&record, "AutomaticWritesPaused") && flag(&record, "UncertainBrightness"));
    rig.file.allow.store(usize::MAX, Ordering::SeqCst);
    assert_eq!(rig.set(&mut host, 0, 0).unwrap().error, Some(PAUSED));
    assert_eq!(rig.submitted(), 1);
}

#[test]
fn lost_worker_after_submission_records_indeterminate_then_stops_the_feature() {
    let rig = Rig::new(25, Mode::Panic, Memory::default());
    let mut host = Host::admitted(1);
    let error = rig.set(&mut host, 1, 75).err().expect("worker loss is fatal");
    assert_eq!(error.to_string(), "brightness worker disconnected");
    assert_eq!(rig.submitted(), 1);
    // Certainty was recorded before the fatal return; no reply query ran.
    assert_eq!(host.queries, 0);
    let record = rig.file.record();
    assert!(flag(&record, "AutomaticWritesPaused") && flag(&record, "UncertainBrightness"));
    assert!(rig.recovery.lock().unwrap().brightness_unresolved());
}

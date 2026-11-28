//! Manual setBrightness through the real feature-owned worker, the shared
//! WMI policy and a scripted native boundary, with injected power, fence and
//! durable storage. No Windows supplier, endpoint or hardware is used.
use super::*;
use crate::session_native::display_query::observation::{BrightnessOwner, fixture_query_owned};
use crate::session_native::display_query::test_support::Memory;
use openblade_platform_windows::{
    brightness::{
        Acknowledgement, CallBudget, MethodRow, ReadingRow, Submission, TopologyFailure,
        WindowsBrightnessWorker, WmiBrightnessNative,
    },
    display::{
        DisplayReadError,
        scripted::{RefreshTiming, Scenario},
    },
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
    /// Workers started by the owner (replacement after an explicit read).
    starts: AtomicUsize,
    /// 1-based active-reading call that fails (0: none).
    fail_read_at: AtomicUsize,
    reads: AtomicUsize,
    /// Set after the submission boundary (cancellation racing a write).
    cancel_on_submit: Mutex<Option<Arc<AtomicBool>>>,
}

impl Wmi {
    fn new(percent: u8, mode: Mode) -> Arc<Self> {
        Arc::new(Self {
            percent: Mutex::new(percent),
            mode: Mutex::new(mode),
            setters: AtomicUsize::new(0),
            submitted: AtomicUsize::new(0),
            native: AtomicUsize::new(0),
            starts: AtomicUsize::new(0),
            fail_read_at: AtomicUsize::new(0),
            reads: AtomicUsize::new(0),
            cancel_on_submit: Mutex::new(None),
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
        let read = self.0.reads.fetch_add(1, Ordering::SeqCst) + 1;
        if read == self.0.fail_read_at.load(Ordering::SeqCst) {
            return Err(NativeFailure::Status(-1));
        }
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
        if let Some(cancel) = &*self.0.cancel_on_submit.lock().unwrap() {
            cancel.store(true, Ordering::SeqCst);
        }
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
        assert_eq!(
            Some(admitted),
            self.power,
            "fence binds the captured context"
        );
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
    fn query(
        &mut self,
        brightness: &SharedBrightness,
        canceled: &AtomicBool,
    ) -> io::Result<Snapshot> {
        self.queries += 1;
        fixture_query_owned(brightness.clone(), canceled)
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
        let starts = wmi.clone();
        let owner = BrightnessOwner::with_starter(move || {
            starts.starts.fetch_add(1, Ordering::SeqCst);
            let native = native.clone();
            WindowsBrightnessWorker::start_scripted(move || Native(native))
        });
        Self {
            wmi,
            owner: Arc::new(Mutex::new(owner)),
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
    assert_eq!(
        rig.wmi.native.load(Ordering::SeqCst),
        3,
        "only the reply query read WMI"
    );
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

    // Managed ExternalUnknown needs Service external-power evidence that a
    // native power context never holds, so it is the changed-source refusal.
    let mut host = Host::admitted(0);
    let setters = rig.setters();
    let outcome = rig.set(&mut host, 3, 75).unwrap();
    assert_eq!(
        (outcome.status, outcome.error),
        (Status::Blocked, Some(CHANGED_POWER))
    );
    assert_eq!(rig.setters(), setters);

    // Origin, session or power generation changed after preparation.
    let mut host = Host::admitted(1);
    host.admit = false;
    let outcome = rig.set(&mut host, 1, 75).unwrap();
    assert_eq!(
        (outcome.status, outcome.error),
        (Status::Blocked, Some(STALE))
    );
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
    // No prior checkpoint and no confirmed source: managed saves an
    // ExternalUnknown manual hold, then refuses the unconfirmed power.
    let rig = Rig::new(25, Mode::Apply, Memory::default());
    let mut host = Host::admitted(1);
    host.power = None;
    assert_eq!(
        rig.set(&mut host, 1, 75).unwrap().error,
        Some(UNCONFIRMED_POWER)
    );
    assert_eq!(rig.setters(), 0);
    assert_eq!(rig.file.record()["LastReconciledPowerSource"], 3);
}

#[test]
fn refusals_answer_with_the_reply_query_state() {
    let state = |rig: &Rig| {
        let read = fixture_query_owned(rig.owner.clone(), &AtomicBool::new(false)).unwrap();
        serde_json::to_value(read).unwrap()
    };

    // Checkpoint refusal: one reply query, and its state is the answer.
    let file = Memory::legacy(1);
    file.allow.store(0, Ordering::SeqCst);
    let rig = Rig::new(25, Mode::Apply, file);
    let mut host = Host::admitted(1);
    let refused = rig.set(&mut host, 1, 75).unwrap();
    assert_eq!(refused.error, Some(CHECKPOINT));
    assert_eq!(host.queries, 1);
    assert_eq!(refused.state.brightness(), Some(25));
    assert_eq!(serde_json::to_value(&refused.state).unwrap(), state(&rig));

    // Gate refusal after a possible submission: likewise.
    let rig = Rig::new(25, Mode::Timeout, Memory::default());
    let mut host = Host::admitted(2);
    assert_eq!(
        rig.set(&mut host, 2, 75).unwrap().status,
        Status::Indeterminate
    );
    let queries = host.queries;
    let refused = rig.set(&mut host, 2, 75).unwrap();
    assert_eq!(refused.error, Some(PAUSED));
    assert_eq!(host.queries, queries + 1);
    assert!(refused.state.brightness().is_some());
    assert_eq!(serde_json::to_value(&refused.state).unwrap(), state(&rig));
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
        assert_eq!(
            (blocked.status, blocked.error),
            (Status::Blocked, Some(PAUSED))
        );
        assert_eq!(rig.submitted(), 1);
        assert_eq!(
            rig.wmi.native.load(Ordering::SeqCst) - native,
            3,
            "reply query only"
        );

        // Restart: the durable checkpoint restores the gate.
        let restarted = Rig::new(25, Mode::Apply, rig.file.clone());
        assert!(restarted.recovery.lock().unwrap().unresolved().brightness);
        let blocked = restarted.set(&mut host, 2, 75).unwrap();
        assert_eq!(blocked.error, Some(PAUSED));
        assert_eq!(restarted.setters(), 0);

        // An explicit read that observes brightness reopens manual writes.
        let read = fixture_query_owned(restarted.owner.clone(), &AtomicBool::new(false)).unwrap();
        restarted.recovery.lock().unwrap().explicit_read(&read);
        assert!(!restarted.recovery.lock().unwrap().unresolved().brightness);
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
    assert!(rig.recovery.lock().unwrap().unresolved().brightness);
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
    let error = rig
        .set(&mut host, 1, 75)
        .err()
        .expect("worker loss is fatal");
    assert_eq!(error.to_string(), "brightness worker disconnected");
    assert_eq!(rig.submitted(), 1);
    // Certainty was recorded before the fatal return; no reply query ran.
    assert_eq!(host.queries, 0);
    let record = rig.file.record();
    assert!(flag(&record, "AutomaticWritesPaused") && flag(&record, "UncertainBrightness"));
    assert!(rig.recovery.lock().unwrap().unresolved().brightness);
}

#[test]
fn same_process_explicit_read_replaces_the_paused_worker_without_replay() {
    for mode in [Mode::Timeout, Mode::Ignore] {
        let rig = Rig::new(25, mode, Memory::default());
        let mut host = Host::admitted(2);
        assert_eq!(
            rig.set(&mut host, 2, 75).unwrap().status,
            Status::Indeterminate
        );
        *rig.wmi.mode.lock().unwrap() = Mode::Apply;
        let none = AtomicBool::new(false);

        // A read that misses brightness keeps the gate and the paused worker.
        rig.wmi
            .fail_read_at
            .store(rig.wmi.reads.load(Ordering::SeqCst) + 1, Ordering::SeqCst);
        let missed = fixture_query_owned(rig.owner.clone(), &none).unwrap();
        assert_eq!(missed.brightness(), None);
        explicit_read(&rig.owner, &rig.recovery, &missed).unwrap();
        assert_eq!(rig.set(&mut host, 2, 50).unwrap().error, Some(PAUSED));
        assert_eq!(rig.submitted(), 1);

        // An explicit read through the same worker joins and replaces it.
        let read = fixture_query_owned(rig.owner.clone(), &none).unwrap();
        assert_eq!(read.brightness(), Some(25));
        explicit_read(&rig.owner, &rig.recovery, &read).unwrap();
        assert!(!rig.recovery.lock().unwrap().unresolved().brightness);
        let next = rig.set(&mut host, 2, 50).unwrap();
        assert_eq!((next.status, next.error), (Status::Succeeded, None));
        assert_eq!(next.state.brightness(), Some(50));
        assert_eq!(
            rig.submitted(),
            2,
            "one new submission, never a replay of 75"
        );
        assert_eq!(
            rig.wmi.starts.load(Ordering::SeqCst),
            2,
            "the paused worker was replaced"
        );
    }
}

struct NoHdr(Host);
impl WriteHost for NoHdr {
    fn power(&self) -> Option<PowerContext> {
        self.0.power()
    }
    fn fence(&self, admitted: PowerContext) -> Arc<dyn SubmissionFence> {
        self.0.fence(admitted)
    }
    fn target(&mut self) -> Result<BrightnessTarget, BrightnessError> {
        self.0.target()
    }
    fn query(
        &mut self,
        brightness: &SharedBrightness,
        canceled: &AtomicBool,
    ) -> io::Result<Snapshot> {
        let mut value = serde_json::to_value(self.0.query(brightness, canceled)?).unwrap();
        value["hdrSupported"] = serde_json::Value::Bool(false);
        Ok(serde_json::from_value(value).unwrap())
    }
}

#[test]
fn inherited_unrelated_uncertainty_keeps_the_managed_pause_but_not_the_write_gate() {
    let file = Memory::default();
    *file.bytes.lock().unwrap() = Some(
        br#"{"SchemaVersion":4,"LastReconciledPowerSource":1,"ManualHold":true,"AutomaticWritesPaused":true,"UncertainHdr":true}"#
            .to_vec(),
    );
    let rig = Rig::new(25, Mode::Apply, file);
    let mut host = NoHdr(Host::admitted(1));
    let none = AtomicBool::new(false);
    for (percent, submitted) in [(75, 1), (50, 2)] {
        let request = SetBrightness {
            expected_power_source: 1,
            percent,
        };
        let outcome = set_brightness(&mut host, &rig.owner, &rig.recovery, request, &none).unwrap();
        assert_eq!((outcome.status, outcome.error), (Status::Succeeded, None));
        assert_eq!(rig.submitted(), submitted);
        assert!(!rig.recovery.lock().unwrap().unresolved().brightness);
        // Managed CompleteManualOperation still cannot confirm HDR.
        let record = rig.file.record();
        assert!(flag(&record, "AutomaticWritesPaused") && flag(&record, "UncertainHdr"));
    }
}

#[test]
fn unchanged_request_is_never_uncertain_even_if_the_reply_read_fails() {
    let rig = Rig::new(50, Mode::Apply, Memory::default());
    let mut host = Host::admitted(1);
    // Read 1 is the worker's own observation; read 2 is the reply query.
    rig.wmi.fail_read_at.store(2, Ordering::SeqCst);
    let outcome = rig.set(&mut host, 1, 40).unwrap();
    assert_eq!((outcome.status, outcome.error), (Status::Succeeded, None));
    assert_eq!(outcome.state.brightness(), None);
    assert_eq!(rig.setters(), 0);
    assert!(!rig.recovery.lock().unwrap().unresolved().brightness);
    assert_eq!(rig.set(&mut host, 1, 75).unwrap().status, Status::Succeeded);
}

#[test]
fn cancellation_after_submission_is_recorded_without_replay() {
    let rig = Rig::new(25, Mode::Apply, Memory::default());
    let mut host = Host::admitted(1);
    let canceled = Arc::new(AtomicBool::new(false));
    *rig.wmi.cancel_on_submit.lock().unwrap() = Some(canceled.clone());
    let outcome = rig.set_canceled(&mut host, 1, 75, &canceled).unwrap();
    // The submission completed; the interrupted reply query cannot show it, so
    // the record is uncertain (managed records cancellation the same way).
    assert_eq!(rig.submitted(), 1);
    assert_eq!(
        (outcome.status, outcome.error),
        (Status::Indeterminate, Some(UNCONFIRMED))
    );
    assert!(rig.recovery.lock().unwrap().unresolved().brightness);
    *rig.wmi.cancel_on_submit.lock().unwrap() = None;
    assert_eq!(rig.set(&mut host, 1, 75).unwrap().error, Some(PAUSED));
    assert_eq!(rig.submitted(), 1);
}

#[test]
fn explicit_read_without_an_unresolved_write_keeps_the_worker() {
    let rig = Rig::new(25, Mode::Apply, Memory::default());
    let mut host = Host::admitted(1);
    assert_eq!(rig.set(&mut host, 1, 75).unwrap().status, Status::Succeeded);
    let read = fixture_query_owned(rig.owner.clone(), &AtomicBool::new(false)).unwrap();
    assert_eq!(read.brightness(), Some(75));
    explicit_read(&rig.owner, &rig.recovery, &read).unwrap();
    assert_eq!(rig.set(&mut host, 1, 50).unwrap().status, Status::Succeeded);
    assert_eq!(
        rig.wmi.starts.load(Ordering::SeqCst),
        1,
        "no replacement without a gate"
    );
}

/// The power generation changes after the first `power()` read.
struct Drifting(Host, AtomicUsize);
impl WriteHost for Drifting {
    fn power(&self) -> Option<PowerContext> {
        let read = self.1.fetch_add(1, Ordering::SeqCst);
        self.0.power().map(|mut power| {
            if read > 0 {
                power.generation += 1;
            }
            power
        })
    }
    fn fence(&self, admitted: PowerContext) -> Arc<dyn SubmissionFence> {
        self.0.fence(admitted)
    }
    fn target(&mut self) -> Result<BrightnessTarget, BrightnessError> {
        self.0.target()
    }
    fn query(
        &mut self,
        brightness: &SharedBrightness,
        canceled: &AtomicBool,
    ) -> io::Result<Snapshot> {
        self.0.query(brightness, canceled)
    }
}

#[test]
fn power_change_after_a_confirmed_write_is_indeterminate_like_managed() {
    let rig = Rig::new(25, Mode::Apply, Memory::default());
    let mut host = Drifting(Host::admitted(1), AtomicUsize::new(0));
    let request = SetBrightness {
        expected_power_source: 1,
        percent: 75,
    };
    let none = AtomicBool::new(false);
    let outcome = set_brightness(&mut host, &rig.owner, &rig.recovery, request, &none).unwrap();
    assert_eq!(
        (outcome.status, outcome.error),
        (Status::Indeterminate, Some(STALE_AFTER))
    );
    assert_eq!(rig.submitted(), 1);
    assert!(rig.recovery.lock().unwrap().unresolved().brightness);
    let record = rig.file.record();
    assert!(flag(&record, "AutomaticWritesPaused") && flag(&record, "UncertainBrightness"));
}

#[test]
fn unchanged_request_skips_the_post_write_power_check_like_managed() {
    // Managed SetBrightnessAsync returns before the setter for a no-op, so
    // EnsureCurrent never runs and a later power change is not uncertainty.
    let rig = Rig::new(50, Mode::Apply, Memory::default());
    let mut host = Drifting(Host::admitted(1), AtomicUsize::new(0));
    let request = SetBrightness {
        expected_power_source: 1,
        percent: 50,
    };
    let none = AtomicBool::new(false);
    let outcome = set_brightness(&mut host, &rig.owner, &rig.recovery, request, &none).unwrap();
    assert_eq!((outcome.status, outcome.error), (Status::Succeeded, None));
    assert_eq!(rig.setters(), 0);
    assert!(!rig.recovery.lock().unwrap().unresolved().brightness);
}

/// The reply query fails with a non-cancellation error.
struct FailingQuery(Host);
impl WriteHost for FailingQuery {
    fn power(&self) -> Option<PowerContext> {
        self.0.power()
    }
    fn fence(&self, admitted: PowerContext) -> Arc<dyn SubmissionFence> {
        self.0.fence(admitted)
    }
    fn target(&mut self) -> Result<BrightnessTarget, BrightnessError> {
        self.0.target()
    }
    fn query(&mut self, _: &SharedBrightness, _: &AtomicBool) -> io::Result<Snapshot> {
        self.0.queries += 1;
        Err(io::Error::other("display query failed"))
    }
}

#[test]
fn fatal_reply_query_records_the_submitted_write_before_failing() {
    let rig = Rig::new(25, Mode::Apply, Memory::default());
    let mut host = FailingQuery(Host::admitted(1));
    let request = SetBrightness {
        expected_power_source: 1,
        percent: 75,
    };
    let none = AtomicBool::new(false);
    let error = set_brightness(&mut host, &rig.owner, &rig.recovery, request, &none)
        .err()
        .expect("a failed reply query is fatal");
    assert_eq!(error.to_string(), "display query failed");
    assert_eq!((rig.submitted(), host.0.queries), (1, 1));
    // The confirmed level cannot be shown, so the record is uncertain.
    assert!(rig.recovery.lock().unwrap().unresolved().brightness);
    let record = rig.file.record();
    assert!(flag(&record, "AutomaticWritesPaused") && flag(&record, "UncertainBrightness"));
    assert!(!flag(&record, "UncertainHdr"));
}

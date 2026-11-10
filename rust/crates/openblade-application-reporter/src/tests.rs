use super::*;
use std::cell::Cell;
use std::rc::Rc;

const FIRST: &str = r"C:\Games\first.exe";
const SECOND: &str = r"C:\Games\second.exe";
const FOREGROUND: &str = r"C:\Games\foreground.exe";
const INSTANCE: Guid = Guid([1; 16]);
const VERSION: i32 = 79;

fn identity(pid: u32, generation: u64) -> ProcessIdentity {
    ProcessIdentity {
        pid: NonZeroU32::new(pid).unwrap(),
        generation: NonZeroU64::new(generation).unwrap(),
    }
}

fn profile(id: u8, path: &str, mode: ActivationMode) -> ApplicationProfile {
    ApplicationProfile {
        id: Guid([id; 16]),
        executable_path: path.into(),
        mode,
        enabled: true,
    }
}

fn running(id: u8, path: &str) -> ApplicationProfile {
    profile(id, path, ActivationMode::WhileRunningAfterActivation)
}

struct Process {
    identity: ProcessIdentity,
    path: String,
    alive: Cell<bool>,
    opens: Cell<u32>,
    drops: Cell<u32>,
}

struct Handle(Rc<Process>);

impl ProcessHandle for Handle {
    fn identity(&self) -> ProcessIdentity {
        self.0.identity
    }

    fn executable_path(&self) -> &str {
        &self.0.path
    }

    fn is_alive(&self) -> bool {
        self.0.alive.get()
    }
}

impl Drop for Handle {
    fn drop(&mut self) {
        self.0.drops.set(self.0.drops.get() + 1);
    }
}

struct Script {
    reporter: ApplicationProfileReporter<Handle>,
    processes: BTreeMap<NonZeroU32, Rc<Process>>,
    requests: Vec<ReportApplicationContextRequest>,
    now: Duration,
}

impl Script {
    fn new(profiles: Vec<ApplicationProfile>) -> Self {
        // The managed comparison is ordinal-ignore-case. These scripted paths
        // are ASCII, where the fixture comparator has the same semantics.
        let mut reporter =
            ApplicationProfileReporter::new(VERSION, INSTANCE, str::eq_ignore_ascii_case);
        reporter.observe_profiles(profiles);
        Self {
            reporter,
            processes: BTreeMap::new(),
            requests: Vec::new(),
            now: Duration::ZERO,
        }
    }

    fn process(&mut self, pid: u32, generation: u64, path: &str) -> Rc<Process> {
        let process = Rc::new(Process {
            identity: identity(pid, generation),
            path: path.into(),
            alive: Cell::new(true),
            opens: Cell::new(0),
            drops: Cell::new(0),
        });
        self.processes
            .insert(process.identity.pid, Rc::clone(&process));
        process
    }

    fn foreground(&mut self, path: &str, process: Option<ProcessIdentity>) {
        self.reporter.observe_foreground(Foreground::Executable {
            path: path.into(),
            process,
        });
    }

    fn unknown(&mut self) {
        self.reporter.observe_foreground(Foreground::Unknown);
    }

    fn poll(&mut self) -> Option<i64> {
        let expected_sequence = i64::try_from(self.requests.len()).unwrap() + 1;
        let mut bytes = [0; 16];
        bytes[..8].copy_from_slice(&expected_sequence.to_be_bytes());
        let request_id = Guid(bytes);
        let request = self.reporter.poll(self.now, request_id, |identity| {
            // Deliberately open by PID in the fake so the reporter must
            // verify the returned lifetime itself.
            let process = Rc::clone(self.processes.get(&identity.pid)?);
            process.opens.set(process.opens.get() + 1);
            Some(Handle(process))
        })?;
        assert_eq!(request.sequence, expected_sequence);
        assert_eq!(request.version, VERSION);
        assert_eq!(request.instance_id, INSTANCE);
        assert_eq!(request.request_id, request_id);
        self.requests.push(request);
        Some(expected_sequence)
    }

    fn send(&mut self, path: Option<&str>, outcome: ReportOutcome) {
        let sequence = self.poll().expect("expected a report");
        assert_eq!(
            self.requests.last().unwrap().executable_path.as_deref(),
            path
        );
        self.reporter.complete(sequence, outcome);
    }

    fn expect(&mut self, path: Option<&str>) {
        self.send(path, ReportOutcome::Applied);
    }
}

#[test]
fn managed_foreground_priority_and_reactivation_keep_the_original_lifetime() {
    let mut script = Script::new(vec![
        running(2, SECOND),
        running(1, FIRST),
        profile(3, FOREGROUND, ActivationMode::Foreground),
    ]);
    let first = script.process(1, 10, FIRST);
    let second = script.process(2, 20, SECOND);
    script.foreground(r"c:\games\FIRST.EXE", Some(first.identity));
    script.expect(Some(FIRST));
    script.foreground(SECOND, Some(second.identity));
    script.expect(Some(SECOND));
    script.foreground(FIRST, Some(first.identity));
    script.expect(Some(FIRST));

    script.reporter.observe_profiles(vec![
        running(2, SECOND),
        running(1, FIRST),
        profile(3, FOREGROUND, ActivationMode::Foreground),
    ]);
    script.foreground(FIRST, Some(first.identity));
    assert_eq!(script.poll(), None);
    script.foreground(FOREGROUND, None);
    script.expect(Some(FOREGROUND));
    script.unknown();
    script.expect(Some(FIRST));
    assert_eq!(first.opens.get(), 1);
    assert_eq!(second.opens.get(), 1);

    script.reporter.observe_exit(first.identity);
    script.expect(Some(SECOND));
    assert_eq!(first.drops.get(), 1);
    script.reporter.observe_exit(second.identity);
    script.expect(None);
    assert_eq!(second.drops.get(), 1);
}

#[test]
fn pid_reuse_replaces_even_a_same_path_lifetime_and_old_exit_cannot_clear_it() {
    let mut script = Script::new(vec![running(1, FIRST)]);
    let first = script.process(42, 10, FIRST);
    script.foreground(FIRST, Some(first.identity));
    script.expect(Some(FIRST));

    let second = script.process(42, 20, FIRST);
    script.foreground(FIRST, Some(second.identity));
    script.reporter.observe_exit(first.identity);
    assert_eq!(script.poll(), None);
    assert_eq!(first.drops.get(), 1);
    assert_eq!(second.opens.get(), 1);
    assert_eq!(second.drops.get(), 0);

    script.unknown();
    assert_eq!(script.poll(), None);
    script.reporter.observe_exit(second.identity);
    script.expect(None);
    assert_eq!(second.drops.get(), 1);
}

#[test]
fn requires_full_path_and_independent_lifetime_evidence_for_retention() {
    let mut script = Script::new(vec![running(1, FIRST)]);
    let process = script.process(42, 20, r"D:\Games\first.exe");
    script.foreground(r"D:\Games\first.exe", Some(process.identity));
    assert_eq!(script.poll(), None);
    assert_eq!(process.opens.get(), 0);

    script.foreground(FIRST, Some(identity(42, 10)));
    assert_eq!(script.poll(), None);
    assert_eq!(process.drops.get(), 1);

    script.foreground(FIRST, Some(process.identity));
    script.expect(Some(FIRST));
    assert_eq!(process.drops.get(), 2);
    script.unknown();
    script.expect(None);

    script.foreground(FIRST, None);
    script.expect(Some(FIRST));
    script.unknown();
    script.expect(None);
    assert_eq!(process.opens.get(), 2);
}

#[test]
fn managed_dropped_exit_is_pruned_before_a_five_second_renewal() {
    let mut script = Script::new(vec![running(1, FIRST)]);
    let process = script.process(42, 10, FIRST);
    script.foreground(FIRST, Some(process.identity));
    script.expect(Some(FIRST));

    script.now = Duration::from_millis(4_999);
    assert_eq!(script.poll(), None);
    script.now = LEASE_INTERVAL;
    script.expect(Some(FIRST));

    process.alive.set(false);
    script.reporter.observe_exit(process.identity);
    for _ in 0..32 {
        script.unknown();
    }
    assert_eq!(script.reporter.observations.len(), 16);
    assert_eq!(script.poll(), None);
    script.now += LEASE_INTERVAL;
    script.expect(None);
    assert_eq!(process.drops.get(), 1);
    script.now += LEASE_INTERVAL;
    assert_eq!(script.poll(), None);
}

#[test]
fn managed_latest_status_survives_a_burst_during_an_in_flight_report() {
    let mut script = Script::new(vec![running(1, FIRST)]);
    let process = script.process(42, 10, FIRST);
    script.foreground(FIRST, Some(process.identity));
    let first = script.poll().unwrap();

    script.reporter.observe_profiles(vec![]);
    script
        .reporter
        .observe_profiles(vec![profile(1, FIRST, ActivationMode::Foreground)]);
    for _ in 0..32 {
        script.unknown();
    }
    assert_eq!(script.poll(), None);
    assert_eq!(process.drops.get(), 0);
    script.reporter.complete(first, ReportOutcome::Applied);
    script.expect(None);
    assert_eq!(process.drops.get(), 1);

    // The latest snapshot still permits a foreground-only match.
    script.foreground(FIRST, Some(process.identity));
    script.expect(Some(FIRST));
    assert_eq!(process.opens.get(), 1);
    script.reporter.observe_profiles(vec![]);
    script.expect(None);
}

#[test]
fn only_the_same_enabled_running_profile_preserves_background_activation() {
    for replacement in [
        vec![running(2, FIRST)],
        vec![running(1, SECOND)],
        vec![ApplicationProfile {
            enabled: false,
            ..running(1, FIRST)
        }],
        vec![],
    ] {
        let mut script = Script::new(vec![running(1, FIRST)]);
        let process = script.process(42, 10, FIRST);
        script.foreground(FIRST, Some(process.identity));
        script.expect(Some(FIRST));
        script.unknown();
        script
            .reporter
            .observe_profiles(vec![running(1, r"c:\games\FIRST.EXE")]);
        assert_eq!(script.poll(), None);
        assert_eq!(process.opens.get(), 1);
        script.reporter.observe_profiles(replacement);
        script.expect(None);
        assert_eq!(process.drops.get(), 1);
    }
}

#[test]
fn retries_unacknowledged_clear_only_on_a_due_tick_or_reconnect() {
    let mut script = Script::new(vec![profile(1, FIRST, ActivationMode::Foreground)]);
    script.foreground(FIRST, None);
    script.send(Some(FIRST), ReportOutcome::Rejected);
    assert_eq!(script.poll(), None);
    script.now = LEASE_INTERVAL;
    script.send(Some(FIRST), ReportOutcome::Stored);
    script.unknown();
    script.send(None, ReportOutcome::TransportFailed);
    assert_eq!(script.poll(), None);
    script.now += LEASE_INTERVAL;
    script.send(None, ReportOutcome::Rejected);
    script.reporter.disconnected();
    script.now += LEASE_INTERVAL;
    assert_eq!(script.poll(), None);
    script.reporter.reconnected();
    script.send(None, ReportOutcome::Stored);
    script.now += LEASE_INTERVAL;
    assert_eq!(script.poll(), None);
}

#[test]
fn reconnect_reconciles_latest_context_and_cancel_releases_resources() {
    let mut script = Script::new(vec![running(1, FIRST), running(2, SECOND)]);
    let first = script.process(1, 10, FIRST);
    let second = script.process(2, 20, SECOND);
    script.foreground(FIRST, Some(first.identity));
    let old = script.poll().unwrap();
    script.reporter.disconnected();
    first.alive.set(false);
    script.foreground(SECOND, Some(second.identity));
    assert_eq!(script.poll(), None);
    script.reporter.reconnected();
    let new = script.poll().unwrap();
    assert_eq!(
        script.requests.last().unwrap().executable_path.as_deref(),
        Some(SECOND)
    );
    assert_eq!(first.drops.get(), 1);
    script.reporter.complete(old, ReportOutcome::Stored);
    script.unknown();
    script.now = LEASE_INTERVAL;
    assert_eq!(script.poll(), None);
    script.reporter.cancel();
    assert_eq!(second.drops.get(), 1);
    script.reporter.complete(new, ReportOutcome::Applied);
    script.reporter.disconnected();
    script.reporter.reconnected();
    script.foreground(SECOND, Some(second.identity));
    script.now += LEASE_INTERVAL;
    assert_eq!(script.poll(), None);
    assert_eq!(second.opens.get(), 1);
}

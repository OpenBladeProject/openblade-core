//! Private Windows-only process fixture. Both processes use the test harness;
//! the worker runs the adapter with the same inert facade as the unit tests.

mod journal;

use super::*;
use journal::{Helper, Journal, Phase};
use std::io::{self, Read, Write};
use std::path::Path;
use std::process::{Child, Command, Stdio};
use std::time::{Duration, Instant};

const MODE: &str = "OPENBLADE_PRIVATE_SNAP_TAP_FIXTURE";
const GENERATION: &str = "OPENBLADE_PRIVATE_SNAP_TAP_GENERATION";
const WORKER: &str = "snap_tap::adapter::tests::supervisor::worker";
const RECEIPT: &str = "openblade-snap-tap-terminal";
const START: u8 = 1;
const WAIT: Duration = Duration::from_secs(10);

#[derive(Clone, Copy, Default, PartialEq, Eq)]
enum Selection {
    #[default]
    Managed,
    RustFixedAd,
}

struct Evidence {
    user: u64,
    helper_user: u64,
    session: u32,
    console_session: u32,
    model: Admission<'static>,
    driver_clear: bool,
    windows_owned: bool,
    cancelled: bool,
}

impl Evidence {
    fn scope(&self) -> Option<(u64, u32, &'static str)> {
        (!self.cancelled
            && self.user != 0
            && self.user == self.helper_user
            && self.session != 0
            && self.session == self.console_session
            && self.model.rejection().is_none()
            && self.driver_clear
            && self.windows_owned)
            .then_some((self.user, self.session, self.model.bios_product))
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum Terminal {
    Quiescent,
    RejectedBeforeStart,
    RecoveryRequired,
}

struct Worker {
    process: Child,
    helper: Helper,
    scope: (u64, u32, &'static str),
    start_attempted: bool,
    stop_requested: bool,
}

struct Supervisor {
    journal: Journal,
    worker: Option<Worker>,
    recovery: bool,
}

impl Supervisor {
    fn create(path: &Path) -> io::Result<Self> {
        Ok(Self::from_journal(Journal::create(path)?))
    }

    fn reopen(path: &Path) -> io::Result<Self> {
        Ok(Self::from_journal(Journal::reopen(path)?))
    }

    fn from_journal(journal: Journal) -> Self {
        Self {
            recovery: journal.unresolved(),
            journal,
            worker: None,
        }
    }

    fn start(
        &mut self,
        generation: u64,
        selection: Selection,
        mut observe: impl FnMut() -> Evidence,
        script: &str,
    ) -> io::Result<bool> {
        if !self.prepare(generation, selection, &mut observe, script)? {
            return Ok(false);
        }
        self.activate(observe())
    }

    fn prepare(
        &mut self,
        generation: u64,
        selection: Selection,
        observe: impl FnOnce() -> Evidence,
        script: &str,
    ) -> io::Result<bool> {
        if selection != Selection::RustFixedAd
            || self.worker.is_some()
            || self.recovery
            || !self.journal.can_reserve(generation)
        {
            return Ok(false);
        }
        let Some(scope) = observe().scope() else {
            return Ok(false);
        };

        let program = std::env::current_exe()?;
        self.persist(Helper { generation, pid: 0 }, Phase::Reserved)?;

        // Only the named test in this executable can be launched. The pipe is
        // private to this child and also acts as a start gate and lifetime lease.
        let process = Command::new(program)
            .args(["--exact", WORKER, "--ignored", "--nocapture"])
            .env(MODE, script)
            .env(GENERATION, generation.to_string())
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .spawn()
            .inspect_err(|_| self.recovery = true)?;
        let helper = Helper {
            generation,
            pid: process.id(),
        };
        self.worker = Some(Worker {
            process,
            helper,
            scope,
            start_attempted: false,
            stop_requested: false,
        });
        self.persist(helper, Phase::Gated)?;
        Ok(true)
    }

    fn activate(&mut self, evidence: Evidence) -> io::Result<bool> {
        let Some(worker) = &self.worker else {
            return Ok(false);
        };
        if self.recovery || worker.start_attempted || worker.stop_requested {
            return Ok(false);
        }
        if evidence.scope() != Some(worker.scope) {
            self.request_stop();
            return Ok(false);
        }
        self.persist(worker.helper, Phase::Armed)?;
        let worker = self.worker.as_mut().unwrap();
        worker.start_attempted = true;
        if let Err(error) = worker.process.stdin.as_mut().unwrap().write_all(&[START]) {
            // The write might have reached the child. Close the lease and
            // retain ownership and recovery even if it later claims cleanup.
            self.recovery = true;
            self.request_stop();
            return Err(error);
        }
        Ok(true)
    }

    fn persist(&mut self, helper: Helper, phase: Phase) -> io::Result<()> {
        self.journal.append(helper, phase).inspect_err(|_| {
            self.recovery = true;
            self.request_stop();
        })
    }

    fn request_stop(&mut self) {
        if let Some(worker) = &mut self.worker
            && worker.process.stdin.take().is_some()
        {
            worker.stop_requested = true;
        }
    }

    fn settle(
        &mut self,
        timeout: Duration,
        publish: impl FnOnce(&Self, Helper, Terminal) -> io::Result<()>,
    ) -> io::Result<Terminal> {
        let worker = self
            .worker
            .as_mut()
            .expect("settle requires an owned child");
        let helper = worker.helper;
        let deadline = Instant::now() + timeout;
        let exit = loop {
            match worker.process.try_wait() {
                Ok(Some(exit)) => break Some(exit),
                Ok(None) if Instant::now() < deadline => {
                    std::thread::sleep(Duration::from_millis(5));
                }
                _ => break None,
            }
        };
        let mut terminal = Terminal::RecoveryRequired;
        if let Some(exit) = exit
            && exit.success()
            && !self.recovery
        {
            // Read only after observing exit on our retained handle. This
            // trusted fixture has no descendants or other pipe writers.
            let mut output = String::new();
            if worker
                .process
                .stdout
                .as_mut()
                .unwrap()
                .take(4097)
                .read_to_string(&mut output)
                .is_ok()
                && output.len() <= 4096
                && output.ends_with('\n')
            {
                terminal = classify(
                    &output,
                    helper,
                    worker.start_attempted,
                    worker.stop_requested,
                );
            }
        }

        if terminal == Terminal::RecoveryRequired {
            self.recovery = true;
            self.request_stop();
        }
        // The publication callback sees the owned child and settled barrier.
        // Failed publication leaves ownership held and permanently blocks start.
        if let Err(error) = publish(self, helper, terminal) {
            self.recovery = true;
            return Err(error);
        }
        match terminal {
            Terminal::Quiescent => self.persist(helper, Phase::Retired)?,
            Terminal::RejectedBeforeStart => self.persist(helper, Phase::NoWrite)?,
            Terminal::RecoveryRequired => {}
        }
        if exit.is_some() {
            self.worker = None;
        }
        Ok(terminal)
    }
}

fn receipt(helper: Helper, result: &str) -> String {
    format!("{RECEIPT} {} {} {result}", helper.generation, helper.pid)
}

fn classify(output: &str, helper: Helper, start_attempted: bool, stop_requested: bool) -> Terminal {
    let mut receipts = output.lines().filter(|line| line.starts_with(RECEIPT));
    let Some(line) = receipts.next() else {
        return Terminal::RecoveryRequired;
    };
    if receipts.next().is_some() {
        return Terminal::RecoveryRequired;
    }
    if start_attempted && stop_requested && line == receipt(helper, "clean") {
        Terminal::Quiescent
    } else if !start_attempted && stop_requested && line == receipt(helper, "no-write") {
        Terminal::RejectedBeforeStart
    } else {
        Terminal::RecoveryRequired
    }
}

#[test]
#[ignore = "private child entry point invoked by the supervisor tests"]
fn worker() {
    let Ok(script) = std::env::var(MODE) else {
        return;
    };
    let helper = Helper {
        generation: std::env::var(GENERATION).unwrap().parse().unwrap(),
        pid: std::process::id(),
    };
    let mut gate = [0];
    let result = if std::io::stdin().read(&mut gate).unwrap() == 0 {
        "no-write"
    } else {
        assert_eq!(gate, [START]);
        match script.as_str() {
            "no-receipt" => return,
            "unexpected-clean" => "clean",
            "failed-exit" => {
                assert_eq!(run_adapter("clean"), "clean");
                println!("{}", receipt(helper, "clean"));
                panic!("inert child failed after writing its receipt");
            }
            "wrong-pid" => {
                println!("{}", receipt(Helper { pid: 0, ..helper }, "clean"));
                return;
            }
            "wrong-generation" => {
                println!(
                    "{}",
                    receipt(
                        Helper {
                            generation: 0,
                            ..helper
                        },
                        "clean"
                    )
                );
                return;
            }
            "clean" | "teardown-error" | "uncertain" => run_adapter(&script),
            _ => panic!("unknown fixture script"),
        }
    };
    println!("{}", receipt(helper, result));
}

fn run_adapter(script: &str) -> &'static str {
    let uncertain = script == "uncertain";
    let (result, events) = run_fake(
        State {
            retirement_errors: script == "teardown-error",
            results: uncertain
                .then_some(Submission::Uncertain)
                .into_iter()
                .collect(),
            ..State::default()
        },
        [
            ARM,
            key(0x1e, 0x100),
            key(0x20, 0x100),
            Step::WaitForSupervisorStop,
        ],
    );
    assert!(events.contains(&Event::Unhook));
    assert!(events.contains(&Event::KillTimer));
    if uncertain {
        assert_eq!(
            events
                .iter()
                .filter(|event| matches!(event, Event::Submit(_)))
                .count(),
            1,
        );
        assert!(
            result
                .as_ref()
                .unwrap()
                .as_ref()
                .unwrap()
                .policy_failure
                .unwrap()
                .output_uncertain
        );
    }
    match result {
        Ok(Some(Retirement {
            policy_status: Status::Off,
            policy_failure: None,
            loop_failure: None,
            unhook_error: None,
            timer_error: None,
        })) => "clean",
        _ => "uncertain",
    }
}

fn admitted() -> Evidence {
    Evidence {
        user: 10,
        helper_user: 10,
        session: 4,
        console_session: 4,
        model: Admission {
            bios_manufacturer: "Razer",
            bios_product: "Blade 16 - RZ09-0581",
            system_manufacturer: "Razer",
            system_product: "Blade 16 - RZ09-0581",
            active_console: true,
            controllers_complete: true,
            competing_controller: false,
        },
        driver_clear: true,
        windows_owned: true,
        cancelled: false,
    }
}

fn settle(supervisor: &mut Supervisor, expected: Terminal) {
    let helper = supervisor.worker.as_ref().unwrap().helper;
    let observed = supervisor
        .settle(WAIT, |owner, published_helper, terminal| {
            assert!(owner.worker.is_some());
            assert_eq!(published_helper, helper);
            assert_eq!(terminal, expected);
            assert_eq!(owner.recovery, terminal == Terminal::RecoveryRequired);
            Ok(())
        })
        .unwrap();
    assert_eq!(observed, expected);
    assert!(supervisor.worker.is_none());
}

#[test]
fn only_explicit_selection_and_complete_current_evidence_can_spawn() {
    let scratch = Scratch::new();
    let mut supervisor = Supervisor::create(&scratch.journal()).unwrap();
    assert!(
        !supervisor
            .start(
                1,
                Selection::default(),
                || panic!("do not observe"),
                "clean"
            )
            .unwrap()
    );
    let invalid: [fn(&mut Evidence); 10] = [
        |e| e.helper_user = 11,
        |e| e.console_session = 5,
        |e| e.model.active_console = false,
        |e| e.model.system_product = "Blade 16 - RZ09-0528",
        |e| e.model.bios_manufacturer = "Other",
        |e| e.model.controllers_complete = false,
        |e| e.model.competing_controller = true,
        |e| e.driver_clear = false,
        |e| e.windows_owned = false,
        |e| e.cancelled = true,
    ];
    for invalidate in invalid {
        assert!(
            !supervisor
                .start(
                    1,
                    Selection::RustFixedAd,
                    || {
                        let mut evidence = admitted();
                        invalidate(&mut evidence);
                        evidence
                    },
                    "clean"
                )
                .unwrap()
        );
        assert!(supervisor.worker.is_none());
    }
}

#[test]
fn stale_admission_closes_gate_and_proves_no_write() {
    for cancelled in [false, true] {
        let scratch = Scratch::new();
        let mut supervisor = Supervisor::create(&scratch.journal()).unwrap();
        let mut evidence = [false, true].into_iter();
        assert!(
            !supervisor
                .start(
                    1,
                    Selection::RustFixedAd,
                    || {
                        let mut current = admitted();
                        if evidence.next().unwrap() {
                            if cancelled {
                                current.cancelled = true;
                            } else {
                                current.model.bios_product = "Blade 16 - RZ09-0528";
                                current.model.system_product = current.model.bios_product;
                            }
                        }
                        current
                    },
                    "clean"
                )
                .unwrap()
        );
        settle(&mut supervisor, Terminal::RejectedBeforeStart);
        assert!(!supervisor.recovery);
    }
}

#[test]
fn cancellation_requires_retirement_and_exit_before_handoff() {
    let scratch = Scratch::new();
    let mut supervisor = Supervisor::create(&scratch.journal()).unwrap();
    for generation in 1..=2 {
        assert!(
            supervisor
                .start(generation, Selection::RustFixedAd, admitted, "clean")
                .unwrap()
        );
        assert!(
            !supervisor
                .start(1, Selection::RustFixedAd, || panic!("still owned"), "clean")
                .unwrap()
        );
        supervisor.request_stop();
        settle(&mut supervisor, Terminal::Quiescent);
    }
}

#[test]
fn exit_without_matching_cleanup_cannot_authorize_handoff() {
    for script in [
        "no-receipt",
        "wrong-pid",
        "wrong-generation",
        "failed-exit",
        "unexpected-clean",
        "uncertain",
        "teardown-error",
    ] {
        let scratch = Scratch::new();
        let mut supervisor = Supervisor::create(&scratch.journal()).unwrap();
        assert!(
            supervisor
                .start(1, Selection::RustFixedAd, admitted, script)
                .unwrap()
        );
        if script != "unexpected-clean" {
            supervisor.request_stop();
        }
        settle(&mut supervisor, Terminal::RecoveryRequired);
        assert!(
            !supervisor
                .start(1, Selection::RustFixedAd, || panic!("recovery"), "clean")
                .unwrap()
        );
        drop(supervisor);
        assert_blocked_after_reopen(&scratch);
    }
}

#[test]
fn timeout_keeps_child_owned_and_late_cleanup_cannot_reopen_handoff() {
    let scratch = Scratch::new();
    let mut supervisor = Supervisor::create(&scratch.journal()).unwrap();
    assert!(
        supervisor
            .start(1, Selection::RustFixedAd, admitted, "clean")
            .unwrap()
    );
    let terminal = supervisor
        .settle(Duration::ZERO, |owner, _, terminal| {
            assert!(owner.worker.is_some());
            assert_eq!(terminal, Terminal::RecoveryRequired);
            Ok(())
        })
        .unwrap();
    assert_eq!(terminal, Terminal::RecoveryRequired);
    assert!(supervisor.worker.is_some());
    settle(&mut supervisor, Terminal::RecoveryRequired);
    drop(supervisor);
    assert_blocked_after_reopen(&scratch);
}

#[test]
fn failed_status_publication_preserves_ownership_and_recovery() {
    let scratch = Scratch::new();
    let mut supervisor = Supervisor::create(&scratch.journal()).unwrap();
    assert!(
        supervisor
            .start(1, Selection::RustFixedAd, admitted, "clean")
            .unwrap()
    );
    supervisor.request_stop();
    let error = supervisor
        .settle(WAIT, |owner, _, terminal| {
            assert!(owner.worker.is_some());
            assert_eq!(terminal, Terminal::Quiescent);
            Err(io::Error::other("publication failed"))
        })
        .unwrap_err();
    assert_eq!(error.kind(), io::ErrorKind::Other);
    assert!(supervisor.worker.is_some());
    assert!(supervisor.recovery);
    settle(&mut supervisor, Terminal::RecoveryRequired);
    drop(supervisor);
    assert_blocked_after_reopen(&scratch);
}

#[test]
fn duplicate_terminal_receipt_is_ambiguous() {
    let helper = Helper {
        generation: 1,
        pid: 7,
    };
    let line = receipt(helper, "clean");
    let output = format!("{line}\n{line}\n");
    assert_eq!(
        classify(&output, helper, true, true),
        Terminal::RecoveryRequired
    );
}

struct Scratch(std::path::PathBuf);

impl Scratch {
    fn new() -> Self {
        use std::sync::atomic::{AtomicU64, Ordering};
        static NEXT: AtomicU64 = AtomicU64::new(0);
        loop {
            let id = NEXT.fetch_add(1, Ordering::Relaxed);
            let path = std::env::temp_dir().join(format!(
                "openblade-inert-snaptap-{}-{id}",
                std::process::id()
            ));
            match std::fs::create_dir(&path) {
                Ok(()) => return Self(path),
                Err(error) if error.kind() == io::ErrorKind::AlreadyExists => {}
                Err(error) => panic!("create scratch directory: {error}"),
            }
        }
    }

    fn journal(&self) -> std::path::PathBuf {
        self.0.join("ownership")
    }
}

impl Drop for Scratch {
    fn drop(&mut self) {
        let _ = std::fs::remove_file(self.journal());
        let _ = std::fs::remove_dir(&self.0);
    }
}

fn assert_blocked_after_reopen(scratch: &Scratch) {
    let mut restarted = Supervisor::reopen(&scratch.journal()).unwrap();
    assert!(restarted.recovery);
    for generation in [1, 2] {
        assert!(
            !restarted
                .start(
                    generation,
                    Selection::RustFixedAd,
                    || panic!("unresolved prior owner"),
                    "clean"
                )
                .unwrap()
        );
    }
}

#[test]
fn published_no_write_and_clean_retirement_survive_reopen_without_reentry() {
    let scratch = Scratch::new();
    for (generation, activate, expected) in [
        (1, false, Terminal::RejectedBeforeStart),
        (2, true, Terminal::Quiescent),
    ] {
        let mut supervisor = if generation == 1 {
            Supervisor::create(&scratch.journal()).unwrap()
        } else {
            Supervisor::reopen(&scratch.journal()).unwrap()
        };
        assert!(Supervisor::reopen(&scratch.journal()).is_err());
        assert!(
            supervisor
                .prepare(generation, Selection::RustFixedAd, admitted, "clean")
                .unwrap()
        );
        if activate {
            assert!(supervisor.activate(admitted()).unwrap());
        }
        supervisor.request_stop();
        assert!(!supervisor.activate(admitted()).unwrap());
        settle(&mut supervisor, expected);

        drop(supervisor);
        let mut restarted = Supervisor::reopen(&scratch.journal()).unwrap();
        assert!(!restarted.recovery);
        assert!(
            !restarted
                .start(
                    generation,
                    Selection::RustFixedAd,
                    || panic!("already used"),
                    "clean"
                )
                .unwrap()
        );
    }
}

#[test]
fn parent_loss_keeps_prior_generation_unresolved_even_after_child_exit() {
    for activated in [false, true] {
        let scratch = Scratch::new();
        let mut supervisor = Supervisor::create(&scratch.journal()).unwrap();
        assert!(
            supervisor
                .prepare(1, Selection::RustFixedAd, admitted, "clean")
                .unwrap()
        );
        if activated {
            assert!(supervisor.activate(admitted()).unwrap());
        }

        // Keep the inert process handle only as a test observer. Losing the
        // private lease models parent death without killing any process.
        let Worker {
            helper,
            mut process,
            ..
        } = supervisor.worker.take().unwrap();
        drop(supervisor);
        assert_blocked_after_reopen(&scratch);
        process.stdin.take();
        let deadline = Instant::now() + WAIT;
        let exit = loop {
            if let Some(exit) = process.try_wait().unwrap() {
                break exit;
            }
            assert!(Instant::now() < deadline, "inert child did not exit on EOF");
            std::thread::sleep(Duration::from_millis(5));
        };
        assert!(exit.success());
        let mut output = String::new();
        process
            .stdout
            .take()
            .unwrap()
            .read_to_string(&mut output)
            .unwrap();
        assert!(
            output
                .lines()
                .any(|line| line == receipt(helper, if activated { "clean" } else { "no-write" }))
        );
        assert_blocked_after_reopen(&scratch);
    }
}

#[test]
fn missing_torn_or_conflicting_history_cannot_reopen() {
    let scratch = Scratch::new();
    assert!(Supervisor::reopen(&scratch.journal()).is_err());
    let supervisor = Supervisor::create(&scratch.journal()).unwrap();
    drop(supervisor);
    let header = std::fs::read_to_string(scratch.journal()).unwrap();
    for suffix in [
        "",
        "1 0 reserve",
        "1 0 reserved\r\n",
        "1 0 reserved\n1 123 gated\n1 124 no-write+exit-ok+published\n",
        "1 0 reserved\n1 123 gated\n1 123 no-write+exit-ok+published\n1 0 reserved\n",
        "1 0 reserved\n1 123 gated\n1 123 armed\n1 123 no-write+exit-ok+published\n",
    ] {
        let contents = format!("{header}{suffix}");
        std::fs::write(scratch.journal(), &contents).unwrap();
        assert!(Supervisor::reopen(&scratch.journal()).is_err());
        assert!(Supervisor::create(&scratch.journal()).is_err());
        assert_eq!(
            std::fs::read_to_string(scratch.journal()).unwrap(),
            contents
        );
    }
}

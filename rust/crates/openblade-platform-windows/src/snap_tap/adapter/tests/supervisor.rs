//! Private Windows-only process fixture. Both processes use the test harness;
//! the worker runs the adapter with the same inert facade as the unit tests.

use super::*;
use std::io::{self, Read, Write};
use std::process::{Child, Command, Stdio};
use std::time::{Duration, Instant};

const MODE: &str = "OPENBLADE_PRIVATE_SNAP_TAP_FIXTURE";
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
    start_attempted: bool,
    stop_requested: bool,
}

#[derive(Default)]
struct Supervisor {
    worker: Option<Worker>,
    recovery: bool,
}

impl Supervisor {
    fn start(
        &mut self,
        selection: Selection,
        mut observe: impl FnMut() -> Evidence,
        script: &str,
    ) -> io::Result<bool> {
        if selection != Selection::RustFixedAd || self.worker.is_some() || self.recovery {
            return Ok(false);
        }
        let Some(scope) = observe().scope() else {
            return Ok(false);
        };

        // Only the named test in this executable can be launched. The pipe is
        // private to this child and also acts as a start gate and lifetime lease.
        let process = Command::new(std::env::current_exe()?)
            .args(["--exact", WORKER, "--ignored", "--nocapture"])
            .env(MODE, script)
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .spawn()?;
        let worker = self.worker.insert(Worker {
            process,
            start_attempted: false,
            stop_requested: false,
        });
        if observe().scope() != Some(scope) {
            worker.process.stdin.take();
            return Ok(false);
        }
        worker.start_attempted = true;
        if let Err(error) = worker.process.stdin.as_mut().unwrap().write_all(&[START]) {
            // The write might have reached the child. Close the lease and
            // retain ownership and recovery even if it later claims cleanup.
            worker.process.stdin.take();
            self.recovery = true;
            return Err(error);
        }
        Ok(true)
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
        publish: impl FnOnce(&Self, u32, Terminal) -> io::Result<()>,
    ) -> io::Result<Terminal> {
        let worker = self
            .worker
            .as_mut()
            .expect("settle requires an owned child");
        let pid = worker.process.id();
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
                terminal = classify(&output, pid, worker.start_attempted, worker.stop_requested);
            }
        }

        if terminal == Terminal::RecoveryRequired {
            self.recovery = true;
            self.request_stop();
        }
        // The publication callback sees the owned child and settled barrier.
        // Failed publication leaves ownership held and permanently blocks start.
        if let Err(error) = publish(self, pid, terminal) {
            self.recovery = true;
            return Err(error);
        }
        if exit.is_some() {
            self.worker = None;
        }
        Ok(terminal)
    }
}

fn classify(output: &str, pid: u32, start_attempted: bool, stop_requested: bool) -> Terminal {
    let mut receipts = output.lines().filter(|line| line.starts_with(RECEIPT));
    let Some(receipt) = receipts.next() else {
        return Terminal::RecoveryRequired;
    };
    if receipts.next().is_some() {
        return Terminal::RecoveryRequired;
    }
    if start_attempted && stop_requested && receipt == format!("{RECEIPT} {pid} clean") {
        Terminal::Quiescent
    } else if !start_attempted && receipt == format!("{RECEIPT} {pid} no-write") {
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
    let mut gate = [0];
    let result = if std::io::stdin().read(&mut gate).unwrap() == 0 {
        "no-write"
    } else {
        assert_eq!(gate, [START]);
        match script.as_str() {
            "no-receipt" => return,
            "unexpected-clean" => "clean",
            "wrong-pid" => {
                println!("{RECEIPT} 0 clean");
                return;
            }
            "clean" | "teardown-error" | "uncertain" => run_adapter(&script),
            _ => panic!("unknown fixture script"),
        }
    };
    println!("{RECEIPT} {} {result}", std::process::id());
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
    let pid = supervisor.worker.as_ref().unwrap().process.id();
    let observed = supervisor
        .settle(WAIT, |owner, published_pid, terminal| {
            assert!(owner.worker.is_some());
            assert_eq!(published_pid, pid);
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
    let mut supervisor = Supervisor::default();
    assert!(
        !supervisor
            .start(Selection::default(), || panic!("do not observe"), "clean")
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
        let mut supervisor = Supervisor::default();
        let mut evidence = [false, true].into_iter();
        assert!(
            !supervisor
                .start(
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
    let mut supervisor = Supervisor::default();
    for _ in 0..2 {
        assert!(
            supervisor
                .start(Selection::RustFixedAd, admitted, "clean")
                .unwrap()
        );
        assert!(
            !supervisor
                .start(Selection::RustFixedAd, || panic!("still owned"), "clean")
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
        "unexpected-clean",
        "uncertain",
        "teardown-error",
    ] {
        let mut supervisor = Supervisor::default();
        assert!(
            supervisor
                .start(Selection::RustFixedAd, admitted, script)
                .unwrap()
        );
        if script != "unexpected-clean" {
            supervisor.request_stop();
        }
        settle(&mut supervisor, Terminal::RecoveryRequired);
        assert!(
            !supervisor
                .start(Selection::RustFixedAd, || panic!("recovery"), "clean")
                .unwrap()
        );
    }
}

#[test]
fn timeout_keeps_child_owned_and_late_cleanup_cannot_reopen_handoff() {
    let mut supervisor = Supervisor::default();
    assert!(
        supervisor
            .start(Selection::RustFixedAd, admitted, "clean")
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
}

#[test]
fn failed_status_publication_preserves_ownership_and_recovery() {
    let mut supervisor = Supervisor::default();
    assert!(
        supervisor
            .start(Selection::RustFixedAd, admitted, "clean")
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
}

#[test]
fn duplicate_terminal_receipt_is_ambiguous() {
    let output = format!("{RECEIPT} 7 clean\n{RECEIPT} 7 clean\n");
    assert_eq!(classify(&output, 7, true, true), Terminal::RecoveryRequired);
}

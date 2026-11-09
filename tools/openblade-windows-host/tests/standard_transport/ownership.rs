//! Test-only cross-process handoff. These roles can open only fixture files.
//! The worker has no service, metadata discovery, or native HID entry point.

#[allow(unsafe_code)]
#[path = "ownership_windows.rs"]
mod native;

use crate::{
    adapter_power::{
        AdmissionFailure, CONTROL_COLLECTION, EXACT_PRODUCT, FixtureLease, HandleIdentity,
        OpenedHandle, QueryFailure,
        io::{Direction, FeatureIo, Frame, Limit, Transfer},
        query_once,
    },
    registry_identity::RegistryIdentity,
    replay::frame,
};
use native::{
    ChildProcess, FileIdentity, OwnerWatch, WorkerJob, attempt, child_endpoints, exclusive,
    identity,
};
use serde::{Deserialize, Serialize};
use std::{
    env,
    fs::{self, File},
    io::{self, Read, Write},
    path::{Path, PathBuf},
    sync::{
        atomic::{AtomicBool, Ordering},
        mpsc::{self, Receiver, SyncSender},
    },
    thread::{self, JoinHandle},
    time::{Duration, Instant},
};
use windows_sys::Win32::Foundation::ERROR_SHARING_VIOLATION;

const CONTROL: &str = "control.fixture";
const MATRIX: &str = "matrix.fixture";
const FENCE: &str = "owner.fixture";
const PAUSED: &str = "handoff.pending";
const BASELINE: [u8; 2] = [0x11, 0x11];
const WAIT: Duration = Duration::from_secs(10);

#[derive(Clone, Copy, Debug, Deserialize, Eq, PartialEq, Serialize)]
enum Case {
    Return,
    Cancel,
    Crash,
    LostReceipt,
    MalformedReceipt,
    StaleAttempt,
    StaleIdentity,
    Controller,
    ReconcileFailed,
    HoldUntilTerminated,
    ExecutionBlocked,
}

#[derive(Debug, Deserialize, Eq, PartialEq, Serialize)]
enum Outcome {
    Levels(u8, u8),
    Stale,
    Denied,
    Canceled,
    Failed,
}

#[derive(Debug, Deserialize, Eq, PartialEq, Serialize)]
enum Block {
    FenceUnavailable,
    RestartQuarantined,
    Protocol,
    LaneUnavailable,
    Quiesce,
    Launch,
    WorkerWaitFailed,
    WorkerExited,
    ReceiptLost,
    Refused(Outcome),
    Reconciliation,
}

impl From<io::Error> for Block {
    fn from(_: io::Error) -> Self {
        Self::Protocol
    }
}

#[derive(Debug, Deserialize, Eq, PartialEq, Serialize)]
enum Message {
    Active,
    Begin(Case),
    Ready([u8; 16]),
    Leased,
    Grant {
        attempt: [u8; 16],
        control: FileIdentity,
        case: Case,
    },
    Submitted,
    Continue,
    Cancel,
    Closed(Outcome),
    Returned,
    Blocked(Block),
}

fn require(condition: bool, detail: &'static str) -> io::Result<()> {
    if condition {
        Ok(())
    } else {
        Err(io::Error::other(detail))
    }
}

// A length byte bounds every read before deserialization. Pipes are inherited
// by the selected child; there is no listening endpoint or client selector.
fn send(mut to: impl Write, message: Message) -> io::Result<()> {
    let bytes = serde_json::to_vec(&message)?;
    let length = u8::try_from(bytes.len()).map_err(io::Error::other)?;
    to.write_all(&[length])?;
    to.write_all(&bytes)?;
    to.flush()
}

fn receive(mut from: impl Read) -> io::Result<Message> {
    let mut length = [0];
    from.read_exact(&mut length)?;
    let mut bytes = [0; 255];
    let body = &mut bytes[..usize::from(length[0])];
    from.read_exact(body).map_err(|error| {
        if error.kind() == io::ErrorKind::UnexpectedEof {
            io::Error::new(io::ErrorKind::InvalidData, "truncated message")
        } else {
            error
        }
    })?;
    Ok(serde_json::from_slice(body)?)
}

struct Incoming(Receiver<io::Result<Message>>);

impl Incoming {
    fn new(mut from: impl Read + Send + 'static) -> Self {
        let (sender, receiver) = mpsc::sync_channel(1);
        thread::spawn(move || {
            loop {
                let message = receive(&mut from);
                let terminal = message.is_err();
                if sender.send(message).is_err() || terminal {
                    break;
                }
            }
        });
        Self(receiver)
    }

    fn next(&self) -> io::Result<Message> {
        self.until(Instant::now() + WAIT)
    }

    fn until(&self, deadline: Instant) -> io::Result<Message> {
        self.0
            .recv_timeout(deadline.saturating_duration_since(Instant::now()))
            .map_err(io::Error::other)?
    }
}

struct Process {
    child: ChildProcess,
    input: File,
    output: Incoming,
}

impl Process {
    fn start(root: &Path, job: Option<&WorkerJob>) -> io::Result<Self> {
        let (child, input, reply) = ChildProcess::launch(root, job)?;
        Ok(Self {
            child,
            input,
            output: Incoming::new(reply),
        })
    }

    fn send(&mut self, message: Message) -> io::Result<()> {
        send(&mut self.input, message)
    }

    fn wait(&self) -> io::Result<bool> {
        self.child
            .wait(WAIT)?
            .ok_or_else(|| io::Error::new(io::ErrorKind::TimedOut, "child did not exit"))
    }
}

impl Drop for Process {
    fn drop(&mut self) {
        // Only the exact process created by the fixture can be terminated.
        if matches!(self.child.wait(Duration::ZERO), Ok(None)) && self.child.kill().is_ok() {
            let _ = self.wait();
        }
    }
}

struct Lane {
    file: File,
    release: SyncSender<()>,
    operation: JoinHandle<()>,
}

impl Lane {
    fn start(file: File) -> io::Result<Self> {
        let operation_handle = file.try_clone()?;
        let (release, finish) = mpsc::sync_channel(1);
        let operation = thread::spawn(move || {
            // The fixture operation owns a separate handle until completion.
            let _ = finish.recv();
            drop(operation_handle);
        });
        Ok(Self {
            file,
            release,
            operation,
        })
    }

    fn drain(self) -> io::Result<()> {
        self.release.send(()).map_err(io::Error::other)?;
        self.operation
            .join()
            .map_err(|_| io::Error::other("fixture drain failed"))?;
        drop(self.file);
        Ok(())
    }
}

struct ActiveOwner {
    control: Lane,
    matrix: Lane,
    fence: File,
}

struct PausedOwner {
    control: FileIdentity,
    matrix: FileIdentity,
    fence: File,
}

impl ActiveOwner {
    fn acquire() -> Result<Self, Block> {
        let fence = exclusive(FENCE, false).map_err(|_| Block::FenceUnavailable)?;
        if Path::new(PAUSED).try_exists()? {
            return Err(Block::RestartQuarantined);
        }
        let control = exclusive(CONTROL, false).map_err(|_| Block::LaneUnavailable)?;
        let matrix = exclusive(MATRIX, false).map_err(|_| Block::LaneUnavailable)?;
        Ok(Self::with_handles(fence, control, matrix)?)
    }

    fn with_handles(fence: File, control: File, matrix: File) -> io::Result<Self> {
        Ok(Self {
            control: Lane::start(control)?,
            matrix: Lane::start(matrix)?,
            fence,
        })
    }

    fn quiesce(self) -> io::Result<PausedOwner> {
        // Publish the restart block before releasing either lane. Any later
        // failure preserves it, even when the OS releases all process handles.
        exclusive(PAUSED, true)?.sync_all()?;
        let control = identity(&self.control.file)?;
        let matrix = identity(&self.matrix.file)?;
        self.control.drain()?;
        self.matrix.drain()?;
        // Check closure using new kernel opens before the grant.
        verify_lanes_closed()?;
        Ok(PausedOwner {
            control,
            matrix,
            fence: self.fence,
        })
    }
}

impl PausedOwner {
    fn resume(self) -> io::Result<ActiveOwner> {
        let mut control = exclusive(CONTROL, false)?;
        let matrix = exclusive(MATRIX, false)?;
        let mut readback = [0; 2];
        control.read_exact(&mut readback)?;
        require(
            identity(&control)? == self.control
                && identity(&matrix)? == self.matrix
                && readback == BASELINE,
            "independent fixture reconciliation failed",
        )?;
        let owner = ActiveOwner::with_handles(self.fence, control, matrix)?;
        fs::remove_file(PAUSED)?;
        Ok(owner)
    }
}

fn verify_lanes_closed() -> io::Result<()> {
    drop(exclusive(CONTROL, false)?);
    drop(exclusive(MATRIX, false)?);
    Ok(())
}

fn sharing_violation(path: impl AsRef<Path>) -> io::Result<bool> {
    match exclusive(path, false) {
        Ok(_) => Ok(false),
        Err(error) if error.raw_os_error() == Some(ERROR_SHARING_VIOLATION as i32) => Ok(true),
        Err(error) => Err(error),
    }
}

fn transfer(
    owner: ActiveOwner,
    outer: &Incoming,
    reply: &File,
    case: Case,
) -> Result<ActiveOwner, Block> {
    let paused = owner.quiesce().map_err(|_| Block::Quiesce)?;
    let job = WorkerJob::new().map_err(|_| Block::Launch)?;
    let mut worker = Process::start(&env::current_dir()?, Some(&job)).map_err(|_| Block::Launch)?;
    let Message::Ready(nonce) = worker.output.next()? else {
        return Err(Block::Protocol);
    };
    let mut granted = nonce;
    if case == Case::StaleAttempt {
        granted[0] ^= 1;
    }
    worker.send(Message::Grant {
        attempt: granted,
        control: if case == Case::StaleIdentity {
            paused.matrix
        } else {
            paused.control
        },
        case,
    })?;

    let mut terminal = worker.output.next();
    if matches!(terminal, Ok(Message::Leased)) {
        if case == Case::Return {
            // Exercise a rival while the lease is live, before starting the
            // real transport's three-second exchange timer.
            send(reply, Message::Leased)?;
            require(outer.next()? == Message::Continue, "expected query start")?;
        }
        worker.send(Message::Continue)?;
        terminal = worker.output.next();
    }
    let mut permitted_completion = false;
    if matches!(terminal, Ok(Message::Submitted)) {
        // The fixture SET is now in flight and the Rust child holds the only
        // control handle. Keep the matrix lane idle and the owner fence held.
        require(
            sharing_violation(CONTROL)?,
            "worker did not hold the control handle",
        )?;
        drop(exclusive(MATRIX, false)?);
        send(reply, Message::Submitted)?;
        let directive = outer.next()?;
        require(
            matches!(directive, Message::Continue | Message::Cancel),
            "invalid directive",
        )?;
        permitted_completion = directive == Message::Continue;
        worker.send(directive)?;
        terminal = worker.output.next();
    }
    if !worker.wait().map_err(|_| Block::WorkerWaitFailed)? {
        return Err(Block::WorkerExited);
    }
    // The process handle is signaled. Independently check both fixture handles
    // are available even for terminal refusals. Any abnormal result quarantines.
    verify_lanes_closed().map_err(|_| Block::LaneUnavailable)?;
    let outcome = match terminal {
        Ok(Message::Closed(outcome)) => outcome,
        Err(error) if error.kind() == io::ErrorKind::UnexpectedEof => {
            return Err(Block::ReceiptLost);
        }
        _ => return Err(Block::Protocol),
    };
    if outcome != Outcome::Levels(BASELINE[0], BASELINE[1]) {
        return Err(Block::Refused(outcome));
    }
    require(permitted_completion, "success outside the completion phase")?;
    require(
        matches!(worker.output.next(), Err(error) if error.kind() == io::ErrorKind::UnexpectedEof),
        "terminal receipt must end the worker stream",
    )?;
    if case == Case::ReconcileFailed {
        exclusive(CONTROL, false)?.write_all(&[0, 0])?;
    }
    paused.resume().map_err(|_| Block::Reconciliation)
}

struct FakeControl<'a> {
    file: File,
    expected: FileIdentity,
    case: Case,
    input: &'a Incoming,
    reply: &'a File,
    owner: &'a OwnerWatch,
    canceled: &'a AtomicBool,
}

impl OpenedHandle for FakeControl<'_> {
    fn identity(&self) -> Option<HandleIdentity> {
        (identity(&self.file).ok() == Some(self.expected)).then_some(CONTROL_COLLECTION)
    }
}

impl FeatureIo for FakeControl<'_> {
    fn transfer(&mut self, direction: Direction, buffer: &mut Frame, limit: Limit<'_>) -> Transfer {
        if let Some(stop) = limit.stop() {
            return Transfer::BeforeSubmission(stop);
        }
        if !self.owner.alive().unwrap_or(false) {
            return Transfer::PreparationFailed(995);
        }
        match direction {
            Direction::Set => {
                assert_eq!(*buffer, frame(0, 1, [0, 0]));
                send(self.reply, Message::Submitted).expect("report fake submission");
                if self.case == Case::HoldUntilTerminated {
                    // Deliberately ignore pipe EOF to exercise job teardown,
                    // independently of the cooperative protocol.
                    loop {
                        thread::park();
                    }
                }
                match self.input.until(limit.deadline) {
                    Ok(Message::Continue) if self.case == Case::Crash => std::process::exit(71),
                    Ok(Message::Continue) => {}
                    Ok(Message::Cancel) => self.canceled.store(true, Ordering::Release),
                    _ => return Transfer::Completed(Err(995)),
                }
                Transfer::Completed(Ok(0))
            }
            Direction::Get => {
                assert_eq!(*buffer, [0; 91]);
                *buffer = frame(2, 1, BASELINE);
                Transfer::Completed(Ok(91))
            }
        }
    }
}

// Power/session/controller observations are scripted independently of the
// process grant. Opening and identifying the fixture file are real Win32 I/O.
struct QueryLease<'a> {
    control: FileIdentity,
    case: Case,
    authority_checks: u8,
    input: &'a Incoming,
    reply: &'a File,
    owner: &'a OwnerWatch,
    canceled: &'a AtomicBool,
}

impl<'a> FixtureLease for QueryLease<'a> {
    type Handle = FakeControl<'a>;

    fn host(&mut self) -> (bool, RegistryIdentity<'_>) {
        let registry = (Some("Razer"), Some(EXACT_PRODUCT));
        (
            true,
            RegistryIdentity {
                bios: registry,
                system: registry,
            },
        )
    }

    fn check_authority(&mut self) -> Result<(), AdmissionFailure> {
        if !self.owner.alive().unwrap_or(false)
            || !Path::new(PAUSED).try_exists().unwrap_or(false)
            || !sharing_violation(FENCE).unwrap_or(false)
        {
            return Err(AdmissionFailure::OwnershipUnavailable);
        }
        self.authority_checks += 1;
        if self.case == Case::ExecutionBlocked && self.authority_checks == 2 {
            return Err(AdmissionFailure::ExecutionBlocked);
        }
        if self.case == Case::Controller && self.authority_checks == 2 {
            return Err(AdmissionFailure::ControllerConflict);
        }
        Ok(())
    }

    fn open_candidates(&mut self, _: Limit<'_>) -> Result<Vec<Self::Handle>, AdmissionFailure> {
        let file =
            exclusive(CONTROL, false).map_err(|_| AdmissionFailure::CandidatesUnavailable)?;
        Ok(vec![FakeControl {
            file,
            expected: self.control,
            case: self.case,
            input: self.input,
            reply: self.reply,
            owner: self.owner,
            canceled: self.canceled,
        }])
    }
}

fn worker(input: &Incoming, reply: &File, owner: &OwnerWatch) -> io::Result<()> {
    let expected = attempt()?;
    send(reply, Message::Ready(expected))?;
    let Message::Grant {
        attempt,
        control,
        case,
    } = input.next()?
    else {
        return Err(io::Error::other("expected a single grant"));
    };
    let canceled = AtomicBool::new(false);
    let outcome = if attempt == expected {
        send(reply, Message::Leased)?;
        require(input.next()? == Message::Continue, "expected query start")?;
        let result = query_once(
            QueryLease {
                control,
                case,
                authority_checks: 0,
                input,
                reply,
                owner,
                canceled: &canceled,
            },
            Limit {
                canceled: &canceled,
                deadline: Instant::now() + WAIT,
            },
        );
        match result {
            Ok(levels) => Outcome::Levels(levels.actual, levels.recommended),
            Err(QueryFailure::Admission(AdmissionFailure::HandleIdentityUnavailable)) => {
                Outcome::Stale
            }
            Err(QueryFailure::Admission(
                AdmissionFailure::ControllerConflict | AdmissionFailure::ExecutionBlocked,
            )) => Outcome::Denied,
            Err(QueryFailure::Exchange(_)) if canceled.load(Ordering::Acquire) => Outcome::Canceled,
            Err(_) => Outcome::Failed,
        }
    } else {
        Outcome::Stale
    };
    if case == Case::LostReceipt {
        return Ok(());
    }
    // query_once has consumed the lease and disposed of the fake handle.
    send(reply, Message::Closed(outcome))?;
    if case == Case::MalformedReceipt {
        // Leave a partial extra frame after a valid terminal receipt.
        let mut reply = reply;
        reply.write_all(&[1])?;
    }
    Ok(())
}

fn supervisor(input: &Incoming, reply: &File) -> io::Result<()> {
    let owner = match ActiveOwner::acquire() {
        Ok(owner) => owner,
        Err(reason) => return send(reply, Message::Blocked(reason)),
    };
    send(reply, Message::Active)?;
    let Message::Begin(case) = input.next()? else {
        return Err(io::Error::other("expected begin"));
    };
    match transfer(owner, input, reply, case) {
        Ok(_owner) => {
            send(reply, Message::Returned)?;
            require(
                input.next()? == Message::Continue,
                "expected fixture shutdown",
            )
        }
        Err(reason) => send(reply, Message::Blocked(reason)),
    }
}

#[test]
fn child_role() -> io::Result<()> {
    let Some((reply, owner)) = child_endpoints()? else {
        return Ok(());
    };
    let input = Incoming::new(io::stdin());
    match owner {
        Some(owner) => worker(&input, &reply, &owner),
        None => supervisor(&input, &reply),
    }
}

struct Fixture(PathBuf);

impl Fixture {
    fn new() -> io::Result<Self> {
        let name = u128::from_le_bytes(attempt()?);
        let root = env::temp_dir().join(format!("openblade-owner-{name:032x}"));
        fs::create_dir(&root)?;
        let fixture = Self(root);
        exclusive(fixture.0.join(CONTROL), true)?.write_all(&BASELINE)?;
        drop(exclusive(fixture.0.join(MATRIX), true)?);
        drop(exclusive(fixture.0.join(FENCE), true)?);
        Ok(fixture)
    }

    fn supervisor(&self) -> io::Result<Process> {
        Process::start(&self.0, None)
    }

    fn begin(&self, case: Case) -> io::Result<Process> {
        let mut owner = self.supervisor()?;
        assert_eq!(owner.output.next()?, Message::Active);
        assert!(sharing_violation(self.0.join(CONTROL))?);
        assert!(sharing_violation(self.0.join(MATRIX))?);
        owner.send(Message::Begin(case))?;
        Ok(owner)
    }

    fn assert_blocked(&self, reason: Block) -> io::Result<()> {
        let rival = self.supervisor()?;
        assert_eq!(rival.output.next()?, Message::Blocked(reason));
        assert!(rival.wait()?);
        Ok(())
    }

    fn assert_quarantined(&self) -> io::Result<()> {
        assert!(self.0.join(PAUSED).try_exists()?);
        assert!(!sharing_violation(self.0.join(CONTROL))?);
        assert!(!sharing_violation(self.0.join(MATRIX))?);
        self.assert_blocked(Block::RestartQuarantined)
    }
}

impl Drop for Fixture {
    fn drop(&mut self) {
        for entry in [CONTROL, MATRIX, FENCE, PAUSED] {
            let _ = fs::remove_file(self.0.join(entry));
        }
        let _ = fs::remove_dir(&self.0);
    }
}

#[test]
fn transfers_once_and_returns_after_closure_and_readback() -> io::Result<()> {
    let fixture = Fixture::new()?;
    let mut owner = fixture.begin(Case::Return)?;
    assert_eq!(owner.output.next()?, Message::Leased);
    fixture.assert_blocked(Block::FenceUnavailable)?;
    owner.send(Message::Continue)?;
    assert_eq!(owner.output.next()?, Message::Submitted);
    assert!(sharing_violation(fixture.0.join(CONTROL))?);
    assert!(!sharing_violation(fixture.0.join(MATRIX))?);
    assert!(sharing_violation(fixture.0.join(FENCE))?);
    owner.send(Message::Continue)?;
    assert_eq!(owner.output.next()?, Message::Returned);
    assert!(!fixture.0.join(PAUSED).try_exists()?);
    assert!(sharing_violation(fixture.0.join(CONTROL))?);
    assert!(sharing_violation(fixture.0.join(MATRIX))?);
    owner.send(Message::Continue)?;
    assert!(owner.wait()?);
    assert!(!sharing_violation(fixture.0.join(CONTROL))?);
    assert!(!sharing_violation(fixture.0.join(MATRIX))?);
    Ok(())
}

#[test]
fn stale_grants_and_changed_controller_fail_before_submission() -> io::Result<()> {
    for (case, outcome) in [
        (Case::StaleAttempt, Outcome::Stale),
        (Case::StaleIdentity, Outcome::Stale),
        (Case::Controller, Outcome::Denied),
        (Case::ExecutionBlocked, Outcome::Denied),
    ] {
        let fixture = Fixture::new()?;
        let owner = fixture.begin(case)?;
        assert_eq!(
            owner.output.next()?,
            Message::Blocked(Block::Refused(outcome))
        );
        assert!(owner.wait()?);
        fixture.assert_quarantined()?;
    }
    Ok(())
}

#[test]
fn canceled_crashed_lost_and_unreconciled_results_leave_the_fence_latched() -> io::Result<()> {
    for (case, directive, reason) in [
        (
            Case::Cancel,
            Message::Cancel,
            Block::Refused(Outcome::Canceled),
        ),
        (Case::Crash, Message::Continue, Block::WorkerExited),
        (Case::LostReceipt, Message::Continue, Block::ReceiptLost),
        (Case::MalformedReceipt, Message::Continue, Block::Protocol),
        (
            Case::ReconcileFailed,
            Message::Continue,
            Block::Reconciliation,
        ),
    ] {
        let fixture = Fixture::new()?;
        let mut owner = fixture.begin(case)?;
        assert_eq!(owner.output.next()?, Message::Submitted);
        owner.send(directive)?;
        assert_eq!(owner.output.next()?, Message::Blocked(reason));
        assert!(owner.wait()?);
        fixture.assert_quarantined()?;
    }
    Ok(())
}

#[test]
fn supervisor_death_closes_the_worker_job_but_never_clears_quarantine() -> io::Result<()> {
    let fixture = Fixture::new()?;
    let owner = fixture.begin(Case::HoldUntilTerminated)?;
    assert_eq!(owner.output.next()?, Message::Submitted);
    assert!(sharing_violation(fixture.0.join(CONTROL))?);
    owner.child.kill()?;
    assert!(!owner.wait()?);
    // Process/job termination need not be observed in the same order.
    // A replacement can acquire the old fence but must still refuse the domain.
    fixture.assert_blocked(Block::RestartQuarantined)?;
    let deadline = Instant::now() + WAIT;
    while sharing_violation(fixture.0.join(CONTROL))? {
        require(
            Instant::now() < deadline,
            "worker handle survived its supervisor",
        )?;
        thread::sleep(Duration::from_millis(5));
    }
    fixture.assert_quarantined()
}

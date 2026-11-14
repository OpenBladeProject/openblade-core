//! Current-user, read-only Windows battery report. Call from a blocking owner thread.
use std::{
    sync::{
        Mutex,
        atomic::{AtomicBool, Ordering},
    },
    thread,
    time::{Duration, Instant},
};

pub(crate) mod custody;
mod process;

const TIMEOUT: Duration = Duration::from_secs(15);
const RETIRE_TIMEOUT: Duration = Duration::from_secs(2);
const POLL: Duration = Duration::from_millis(20);
const MAX_BYTES: u64 = 4 * 1024 * 1024;

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum Failure {
    Scratch,
    Start,
    Wait,
    Cancelled,
    Timeout,
    StopUnconfirmed,
    ProcessExit,
    Output,
    Cleanup,
}

struct Quarantine {
    child: process::Child,
    attempt: custody::Attempt,
}

// The product pipe is serial. Enforce that contract here too, so even a second
// caller cannot start another collection while the previous job is uncertain.
static OWNER: Mutex<Option<Quarantine>> = Mutex::new(None);

/// Probe a previously uncertain job and its original process without starting
/// anything. A receipt requires both root exit and zero active job processes.
pub fn reconcile_retirement() -> Result<(), Failure> {
    let mut owner = OWNER.try_lock().map_err(|_| Failure::StopUnconfirmed)?;
    reconcile(&mut owner)
}

fn reconcile(owner: &mut Option<Quarantine>) -> Result<(), Failure> {
    if let Some(held) = owner {
        if !matches!(held.child.exited(), Ok(Some(_))) {
            return Err(Failure::StopUnconfirmed);
        }
        owner
            .take()
            .expect("retained acquisition")
            .attempt
            .clean()?;
    }
    Ok(())
}

pub fn acquire(cancelled: &AtomicBool) -> Result<Vec<u8>, Failure> {
    let mut owner = OWNER.try_lock().map_err(|_| Failure::StopUnconfirmed)?;
    reconcile(&mut owner)?;
    let deadline = Instant::now() + TIMEOUT;
    check(cancelled, deadline)?;
    let attempt = custody::Attempt::create()?;
    let launch = check(cancelled, deadline).and_then(|()| process::Child::launch(&attempt.output));
    let child = match launch {
        Ok(child) => child,
        Err(error) => {
            return finish_acquisition(
                Err(error),
                attempt.clean(),
                crate::agent_diagnostics::record,
            );
        }
    };
    let result = run(&child, cancelled, deadline, RETIRE_TIMEOUT);
    if result == Err(Failure::StopUnconfirmed) {
        *owner = Some(Quarantine { child, attempt });
        return Err(Failure::StopUnconfirmed);
    }
    let bytes = result.and_then(|()| attempt.read(cancelled, deadline));
    finish_acquisition(bytes, attempt.clean(), crate::agent_diagnostics::record).and_then(|bytes| {
        check(cancelled, deadline)?;
        Ok(bytes)
    })
}

// The job is confirmed retired, or creation never started it. Record cleanup
// failures independently, as the managed provider does in its finally block.
fn finish_acquisition(
    bytes: Result<Vec<u8>, Failure>,
    cleanup: Result<(), Failure>,
    record: impl FnOnce(crate::agent_diagnostics::Event),
) -> Result<Vec<u8>, Failure> {
    if cleanup.is_err() {
        record(crate::agent_diagnostics::Event::BatteryCleanup);
    }
    bytes
}

trait ChildControl {
    fn resume(&self) -> Result<(), Failure>;
    fn exited(&self) -> Result<Option<u32>, Failure>;
    fn terminate(&self);
}

fn run(
    child: &impl ChildControl,
    cancelled: &AtomicBool,
    deadline: Instant,
    grace: Duration,
) -> Result<(), Failure> {
    let result = check(cancelled, deadline)
        .and_then(|()| child.resume())
        .and_then(|()| {
            loop {
                check(cancelled, deadline)?;
                if let Some(code) = child.exited()? {
                    return if code == 0 {
                        Ok(())
                    } else {
                        Err(Failure::ProcessExit)
                    };
                }
                pause(deadline);
            }
        });
    if matches!(result, Ok(()) | Err(Failure::ProcessExit)) {
        return result;
    }
    child.terminate();
    let retire = Instant::now() + grace;
    loop {
        if matches!(child.exited(), Ok(Some(_))) {
            return result;
        }
        if Instant::now() >= retire {
            return Err(Failure::StopUnconfirmed);
        }
        pause(retire);
    }
}

fn pause(deadline: Instant) {
    thread::sleep(POLL.min(deadline.saturating_duration_since(Instant::now())));
}

fn check(cancelled: &AtomicBool, deadline: Instant) -> Result<(), Failure> {
    if cancelled.load(Ordering::Acquire) {
        Err(Failure::Cancelled)
    } else if Instant::now() >= deadline {
        Err(Failure::Timeout)
    } else {
        Ok(())
    }
}

#[cfg(test)]
mod tests;

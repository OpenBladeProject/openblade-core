use openblade_protocol::REPORT_LENGTH;
use std::{
    sync::atomic::{AtomicBool, Ordering},
    thread,
    time::{Duration, Instant},
};

pub(crate) const FRAME_LENGTH: usize = REPORT_LENGTH + 1;
pub(crate) type Frame = [u8; FRAME_LENGTH];

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) enum Direction {
    Set,
    Get,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) enum Stop {
    Caller,
    Deadline,
}

#[derive(Clone, Copy)]
pub(crate) struct Limit<'a> {
    pub(crate) canceled: &'a AtomicBool,
    pub(crate) deadline: Instant,
}

impl Limit<'_> {
    pub(crate) fn stop(self) -> Option<Stop> {
        if self.canceled.load(Ordering::Acquire) {
            Some(Stop::Caller)
        } else if Instant::now() >= self.deadline {
            Some(Stop::Deadline)
        } else {
            None
        }
    }
}

pub(crate) type Completion = Result<u32, u32>;
pub(crate) type CancelResult = Result<(), u32>;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) enum Transfer {
    BeforeSubmission(Stop),
    PreparationFailed(u32),
    Rejected(u32),
    Completed(Completion),
    CompletedAfterStop {
        reason: Stop,
        completion: Completion,
    },
    Canceled {
        reason: Stop,
        request: CancelResult,
        completion: Completion,
    },
}

pub(crate) trait FeatureIo {
    /// Returns only once the kernel no longer owns this request's storage.
    fn transfer(&mut self, direction: Direction, frame: &mut Frame, limit: Limit<'_>) -> Transfer;
}

pub(crate) trait Pending {
    fn poll(&mut self) -> Option<Completion>;
    fn cancel(&mut self) -> CancelResult;
}

/// A cancellation attempt never transfers ownership of operation storage back.
/// Keep the native completion even when it races with targeted cancellation.
pub(crate) fn finish_pending(operation: &mut impl Pending, limit: Limit<'_>) -> Transfer {
    let mut cancellation = None;
    loop {
        if let Some(completion) = operation.poll() {
            return match cancellation {
                Some((reason, request)) => Transfer::Canceled {
                    reason,
                    request,
                    completion,
                },
                None => Transfer::Completed(completion),
            };
        }

        if cancellation.is_none()
            && let Some(reason) = limit.stop()
        {
            cancellation = Some((reason, operation.cancel()));
        }
        thread::sleep(Duration::from_millis(1));
    }
}

use crate::io::{Direction, FRAME_LENGTH, FeatureIo, Frame, Limit, Stop, Transfer};
use openblade_protocol::{REPORT_LENGTH, RazerReport};
use openblade_types::ProtocolValidationFailure;
use std::{
    sync::{
        Mutex, MutexGuard, TryLockError,
        atomic::{AtomicBool, Ordering},
    },
    thread,
    time::{Duration, Instant},
};

#[derive(Debug, PartialEq, Eq)]
pub(crate) struct AdapterPowerLevels {
    pub(crate) actual: u8,
    pub(crate) recommended: u8,
}

#[derive(Debug, PartialEq, Eq)]
pub(crate) enum Failure {
    QueueFull,
    Queued(Stop),
    BeforeSubmission(Transfer),
    SynchronousSetRejection(u32),
    UncertainIo(Direction, Transfer),
    UncertainProtocol { count: u32, failure: ReplyFailure },
    ReconciliationRequired,
    Exhausted,
}

#[derive(Debug, PartialEq, Eq)]
pub(crate) enum ReplyFailure {
    Count,
    ReportId,
    Protocol(ProtocolValidationFailure),
}

/// One active exchange and at most one waiter. The active caller retains the
/// gate through terminal native completion, including cancellation drain.
pub(crate) struct Session<T> {
    state: Mutex<State<T>>,
    waiting: AtomicBool,
    settle_delay: Duration,
}

struct State<T> {
    io: Option<T>,
    next_transaction: Option<u8>,
}

impl<T: FeatureIo> Session<T> {
    pub(crate) fn new(io: T, settle_delay: Duration) -> Self {
        Self {
            state: Mutex::new(State {
                io: Some(io),
                next_transaction: Some(1),
            }),
            waiting: AtomicBool::new(false),
            settle_delay,
        }
    }

    pub(crate) fn query_adapter_power(
        &self,
        limit: Limit<'_>,
    ) -> Result<AdapterPowerLevels, Failure> {
        let mut state = self.claim(limit)?;
        if state.io.is_none() {
            return Err(Failure::ReconciliationRequired);
        }
        if let Some(reason) = limit.stop() {
            return Err(Failure::BeforeSubmission(Transfer::BeforeSubmission(
                reason,
            )));
        }
        let transaction = state.next_transaction.ok_or(Failure::Exhausted)?;
        state.next_transaction = transaction.checked_add(1);

        let result = query_adapter_power(
            state.io.as_mut().expect("claimed transport"),
            transaction,
            self.settle_delay,
            limit,
        );
        if matches!(
            result,
            Err(Failure::SynchronousSetRejection(_)
                | Failure::UncertainIo(..)
                | Failure::UncertainProtocol { .. })
        ) {
            // No caller can reuse this handle. A new session needs separate
            // admission and read-only reconciliation outside this offline unit.
            state.io = None;
        }
        result
    }

    pub(crate) fn waiter_present(&self) -> bool {
        self.waiting.load(Ordering::Acquire)
    }

    fn claim(&self, limit: Limit<'_>) -> Result<MutexGuard<'_, State<T>>, Failure> {
        // Serialize admission to the wait slot as well as to the native handle.
        if let Some(reason) = limit.stop() {
            return Err(Failure::Queued(reason));
        }
        if self.waiting.swap(true, Ordering::AcqRel) {
            return Err(Failure::QueueFull);
        }
        let _slot = WaitSlot(&self.waiting);
        loop {
            if let Some(reason) = limit.stop() {
                return Err(Failure::Queued(reason));
            }
            match self.state.try_lock() {
                Ok(state) => return Ok(state),
                Err(TryLockError::Poisoned(_)) => return Err(Failure::ReconciliationRequired),
                Err(TryLockError::WouldBlock) => thread::sleep(Duration::from_millis(1)),
            }
        }
    }
}

struct WaitSlot<'a>(&'a AtomicBool);

impl Drop for WaitSlot<'_> {
    fn drop(&mut self) {
        self.0.store(false, Ordering::Release);
    }
}

fn query_adapter_power(
    io: &mut impl FeatureIo,
    transaction: u8,
    settle_delay: Duration,
    limit: Limit<'_>,
) -> Result<AdapterPowerLevels, Failure> {
    // AdapterPowerCommandClient.QueryCommand, RZ09-0581 standard report ID 0.
    let request = RazerReport::create(transaction, 0x07, 0x8C, &[0, 0]).expect("fixed payload");
    let mut set: Frame = [0; FRAME_LENGTH];
    request
        .encode_into(&mut set[1..])
        .expect("fixed frame geometry");
    let set_result = io.transfer(Direction::Set, &mut set, limit);
    let set_count = match set_result {
        Transfer::Completed(Ok(count)) => count,
        Transfer::BeforeSubmission(_) | Transfer::PreparationFailed(_) => {
            return Err(Failure::BeforeSubmission(set_result));
        }
        Transfer::Rejected(error) => return Err(Failure::SynchronousSetRejection(error)),
        _ => return Err(Failure::UncertainIo(Direction::Set, set_result)),
    };

    let settled = Instant::now() + settle_delay;
    loop {
        if let Some(reason) = limit.stop() {
            return Err(Failure::UncertainIo(
                Direction::Set,
                Transfer::CompletedAfterStop {
                    reason,
                    completion: Ok(set_count),
                },
            ));
        }
        let remaining = settled.saturating_duration_since(Instant::now());
        if remaining.is_zero() {
            break;
        }
        thread::sleep(remaining.min(Duration::from_millis(1)));
    }
    let mut get: Frame = [0; FRAME_LENGTH];
    let get_result = io.transfer(Direction::Get, &mut get, limit);
    let Transfer::Completed(Ok(count)) = get_result else {
        return Err(Failure::UncertainIo(Direction::Get, get_result));
    };
    if count as usize != FRAME_LENGTH && !(count as usize == REPORT_LENGTH && get[0] == 0) {
        return Err(Failure::UncertainProtocol {
            count,
            failure: ReplyFailure::Count,
        });
    }
    if let Some(reason) = limit.stop() {
        return Err(Failure::UncertainIo(
            Direction::Get,
            Transfer::CompletedAfterStop {
                reason,
                completion: Ok(count),
            },
        ));
    }
    if get[0] != 0 {
        return Err(Failure::UncertainProtocol {
            count,
            failure: ReplyFailure::ReportId,
        });
    }
    let payload = request
        .validate_response(&get[1..], Some(2))
        .map_err(|error| Failure::UncertainProtocol {
            count,
            failure: ReplyFailure::Protocol(error),
        })?;
    Ok(AdapterPowerLevels {
        actual: payload.as_slice()[0],
        recommended: payload.as_slice()[1],
    })
}

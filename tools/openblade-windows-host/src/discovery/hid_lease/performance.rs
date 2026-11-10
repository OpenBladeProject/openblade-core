//! RZ09-0581 standard performance observation on a single admitted HID lease.
//! Only the Windows composition below can open a native feature handle.

use openblade_protocol::{REPORT_LENGTH, RazerReport};
use std::{
    io,
    sync::atomic::{AtomicBool, AtomicU8, Ordering},
    time::Duration,
};
use tokio::time::{Instant, sleep_until};

use super::{Descriptor, Selection, blocked, check_cancelled};

#[cfg(all(windows, feature = "firmware-performance-compile-check"))]
mod windows;

#[cfg(test)]
mod tests;

const MODEL: &str = "RZ09-0581";
const DEVICE: (u16, u16) = (0x1532, 0x02e0);
const USAGE: (u16, u16) = (1, 2);
const FRAME_LENGTH: usize = REPORT_LENGTH + 1;
const CLASS: u8 = 0x0d;
const GET_THERMAL: u8 = 0x82;
const SETTLE: Duration = Duration::from_millis(30);
const POLL: Duration = Duration::from_millis(5);
const MAX_OBSERVATION: Duration = Duration::from_secs(3);
type Frame = [u8; FRAME_LENGTH];

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum ObservedPerformance {
    BalancedAutomatic,
    PerformanceAutomatic,
}

// Only a live service owner can supply this evidence. An epoch must change
// across ownership, power, controller or lifecycle transitions, even if the
// visible state returns to the same value. Absence of any proof means None.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
struct Admission {
    epoch: u64,
    session: u32,
    full_adapter: bool,
    controller_clear: bool,
    awake: bool,
}
impl Admission {
    fn is_clear(self) -> bool {
        self.full_adapter && self.controller_clear && self.awake
    }
}

type Authorize<'a> = &'a mut dyn FnMut() -> Option<Admission>;

#[derive(Clone, Copy)]
struct Limit<'a> {
    deadline: Instant,
    cancelled: &'a AtomicBool,
}

impl Limit<'_> {
    fn check(self) -> io::Result<()> {
        check_cancelled(self.cancelled)?;
        if Instant::now() >= self.deadline {
            return Err(io::ErrorKind::TimedOut.into());
        }
        Ok(())
    }

    async fn wait(self, duration: Duration) -> io::Result<()> {
        sleep_until(self.deadline.min(Instant::now() + duration)).await;
        self.check()
    }
}

enum Transfer {
    SendGetter(Frame),
    ReceiveReply,
}

// One operation slot. A pending slot owns its storage until terminal native
// completion; cancel alone never retires it.
trait Port {
    fn reserve_pair(&mut self) -> Option<[u8; 2]>;
    fn verify(&mut self, limit: Limit<'_>) -> io::Result<()>;
    fn submit(&mut self, transfer: Transfer) -> io::Result<()>;
    fn poll(&mut self) -> Option<io::Result<(usize, Frame)>>;
    fn cancel(&mut self);
    fn poll_retirement(&mut self) -> io::Result<bool>;
}

fn reviewed_selection(selection: &Selection) -> bool {
    selection.model == MODEL && selection.device == DEVICE && selection.usage == Some(USAGE)
}

fn reviewed_descriptor(descriptor: Descriptor) -> bool {
    descriptor.device == DEVICE
        && descriptor.usage == USAGE
        && usize::from(descriptor.feature_bytes) == FRAME_LENGTH
}

fn verify_admission(authorize: Authorize<'_>, expected: Admission) -> io::Result<()> {
    if expected.is_clear() && authorize() == Some(expected) {
        Ok(())
    } else {
        Err(blocked("firmware observation lost live service admission"))
    }
}

struct Transactions(AtomicU8);

impl Transactions {
    const fn new() -> Self {
        Self(AtomicU8::new(0))
    }

    fn reserve_pair(&self) -> Option<[u8; 2]> {
        let last = self
            .0
            .try_update(Ordering::AcqRel, Ordering::Acquire, |last| {
                last.checked_add(2)
            })
            .ok()?;
        Some([last + 1, last + 2])
    }
}

struct Reader<P: Port> {
    port: P,
    admission: Admission,
    poisoned: bool,
}

impl<P: Port> Reader<P> {
    fn new(port: P, admission: Admission) -> Self {
        Self {
            port,
            admission,
            poisoned: false,
        }
    }

    async fn observe(
        &mut self,
        authorize: Authorize<'_>,
        limit: Limit<'_>,
    ) -> Option<ObservedPerformance> {
        if self.poisoned {
            return None;
        }
        let limit = Limit {
            deadline: limit.deadline.min(Instant::now() + MAX_OBSERVATION),
            ..limit
        };
        limit.check().ok()?;
        let admission = self.admission;
        let mut attempt = Attempt {
            reader: self,
            completed: false,
        };
        let result = attempt.read_pair(authorize, admission, limit).await;
        attempt.completed = result.is_some();
        result
    }

    // Once retirement begins no further traffic is permitted, even if a
    // canceled operation completes successfully after the observation deadline.
    async fn retire(&mut self, deadline: Instant) -> io::Result<()> {
        self.seal();
        loop {
            if self.port.poll_retirement()? {
                return Ok(());
            }
            if Instant::now() >= deadline {
                return Err(io::ErrorKind::TimedOut.into());
            }
            sleep_until(deadline.min(Instant::now() + POLL)).await;
        }
    }

    fn seal(&mut self) {
        if !self.poisoned {
            self.poisoned = true;
            self.port.cancel();
        }
    }
}

struct Attempt<'a, P: Port> {
    reader: &'a mut Reader<P>,
    completed: bool,
}

impl<P: Port> Attempt<'_, P> {
    async fn read_pair(
        &mut self,
        authorize: Authorize<'_>,
        admission: Admission,
        limit: Limit<'_>,
    ) -> Option<ObservedPerformance> {
        self.checkpoint(authorize, admission, limit).ok()?;
        let transactions = self.reader.port.reserve_pair()?;
        let mut selectors = [0; 2];
        for (index, transaction) in transactions.into_iter().enumerate() {
            let channel = index as u8 + 1;
            let request =
                RazerReport::create(transaction, CLASS, GET_THERMAL, &[1, channel, 0, 0]).ok()?;
            let mut frame = [0; FRAME_LENGTH];
            frame[1..].copy_from_slice(&request.encode());
            // HID SET_FEATURE transmits the 0x82 getter envelope. The paired
            // GET_FEATURE retrieves its reply. There is no 0x02 setter path.
            self.transfer(Transfer::SendGetter(frame), limit)
                .await
                .ok()?;
            limit.wait(SETTLE).await.ok()?;
            self.checkpoint(authorize, admission, limit).ok()?;
            let (length, frame) = self.transfer(Transfer::ReceiveReply, limit).await.ok()?;
            // The managed HIDClass path accepts 90 (OS omits the prefix
            // from its count) or 91 bytes in the fixed 91-byte buffer.
            if !matches!(length, REPORT_LENGTH | FRAME_LENGTH) || frame[0] != 0 {
                return None;
            }
            let reply = request.validate_response(&frame[1..], Some(4)).ok()?;
            let [1, returned_channel, mode, 0] = reply.as_slice() else {
                return None;
            };
            if *returned_channel != channel {
                return None;
            }
            selectors[index] = *mode;
            self.checkpoint(authorize, admission, limit).ok()?;
        }

        match selectors {
            [0, 0] => Some(ObservedPerformance::BalancedAutomatic),
            [2, 2] => Some(ObservedPerformance::PerformanceAutomatic),
            _ => None,
        }
    }

    fn checkpoint(
        &mut self,
        authorize: Authorize<'_>,
        admission: Admission,
        limit: Limit<'_>,
    ) -> io::Result<()> {
        limit.check()?;
        verify_admission(authorize, admission)?;
        self.reader.port.verify(limit)?;
        verify_admission(authorize, admission)?;
        limit.check()
    }

    async fn transfer(
        &mut self,
        transfer: Transfer,
        limit: Limit<'_>,
    ) -> io::Result<(usize, Frame)> {
        limit.check()?;
        self.reader.port.submit(transfer)?;
        loop {
            limit.check()?;
            if let Some(completion) = self.reader.port.poll() {
                limit.check()?;
                return completion;
            }
            limit.wait(POLL).await?;
        }
    }
}

impl<P: Port> Drop for Attempt<'_, P> {
    fn drop(&mut self) {
        if !self.completed {
            self.reader.seal();
        }
    }
}

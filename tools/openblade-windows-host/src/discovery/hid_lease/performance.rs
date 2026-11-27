//! RZ09-0581 performance observation on a single admitted HID lease.
//! Only the Windows composition below can open a native feature handle.

use openblade_protocol::{
    REPORT_LENGTH, RazerReport,
    custom_performance::{CustomPerformanceComponent, CustomPerformanceQuery},
};
use std::{io, sync::atomic::AtomicBool, time::Duration};
use tokio::time::{Instant, sleep_until};

use super::{Descriptor, Selection, blocked, check_cancelled};

mod transactions;
use transactions::Transactions;
#[cfg(test)]
mod transaction_tests;
#[cfg(all(windows, feature = "firmware-performance-compile-check"))]
mod windows;

#[cfg(test)]
mod tests;
#[cfg(test)]
pub(crate) mod worker_tests;

#[cfg(any(test, feature = "service-keyboard-driver-mode-read-v1"))]
mod device_mode;
#[cfg(any(test, feature = "service-keyboard-driver-mode-read-v1"))]
pub(crate) use device_mode::DeviceModeRead;
#[cfg(feature = "service-keyboard-driver-mode-v1")]
#[allow(unused_imports)] // Shared with the standalone discovery binary.
pub(crate) use device_mode::{DeviceModeJob, DeviceModeStep};
#[cfg(feature = "service-thermal-mutation-v1")]
mod battery;
#[cfg(feature = "service-lighting-v1")]
mod lighting;
#[cfg(feature = "service-lighting-v1")]
mod lighting_admission;
#[cfg(feature = "service-lighting-v1")]
mod lighting_effects;
#[cfg(feature = "service-lighting-v1")]
#[allow(unused_imports)] // Shared with the standalone discovery binary.
pub(crate) use lighting_effects::{
    Animation, ColorMode, Effect, Random, RefreshRate, SystemRandom,
};
#[cfg(feature = "service-lighting-v1")]
mod lighting_layout;
#[cfg(feature = "service-lighting-v1")]
#[allow(unused_imports)] // Shared with the standalone discovery binary.
pub(crate) use lighting::{
    LightingConfirmation, LightingJob, LightingOutcome, LightingReading, LightingTarget,
    MatrixState, RenderTarget, Renderer, Rgb,
};
#[cfg(feature = "service-lighting-v1")]
#[allow(unused_imports)] // Shared with the standalone discovery binary.
pub(crate) use lighting_admission::approved as lighting_approved;
#[cfg(any(test, feature = "service-live-readings-v1"))]
mod fan_rpm;
#[cfg(any(test, feature = "service-live-readings-v1"))]
pub(crate) use fan_rpm::{FanPairSends, FanRpmRead};
#[cfg(test)]
pub(crate) mod fan_rpm_tests;
#[cfg(any(test, feature = "service-live-readings-v1"))]
mod mcu_version;
#[cfg(any(test, feature = "service-live-readings-v1"))]
pub(crate) use mcu_version::{McuQuerySends, McuVersionRead};
#[cfg(test)]
mod mcu_version_tests;
#[cfg(any(
    feature = "service-keyboard-driver-mode-read-v1",
    feature = "service-live-readings-v1"
))]
#[allow(unused_imports)] // Shared with the standalone discovery binary.
pub(crate) use worker::Queries;
#[cfg(test)]
mod device_mode_guard_tests;
#[cfg(test)]
pub(crate) mod device_mode_tests;
#[cfg(feature = "service-thermal-mutation-v1")]
mod fan;
#[cfg(test)]
mod job_tests;
#[cfg(test)]
mod lighting_guard_tests;
#[cfg(feature = "service-thermal-mutation-v1")]
pub(crate) mod mutation;
#[cfg(feature = "service-thermal-mutation-v1")]
mod temporary_charge;
#[cfg(any(test, feature = "firmware-performance-compile-check"))]
mod worker;
#[cfg(all(windows, feature = "service-hardware-compile-check"))]
#[allow(unused_imports)] // Shared with the standalone discovery binary.
pub(crate) use windows::{native_read_worker, native_worker};
#[cfg(all(windows, feature = "service-hardware-compile-check"))]
#[allow(unused_imports)]
pub(crate) use worker::Worker;
#[cfg(all(windows, test, not(feature = "service-hardware-compile-check")))]
#[allow(unused_imports)] // Not every test binary names the worker directly.
pub(crate) use worker::Worker;

const MODEL: &str = "RZ09-0581";
const DEVICE: (u16, u16) = (0x1532, 0x02e0);
/// The exact identity, as (model, vendor ID, product ID), of every
/// observation this module's Readers produce.
#[cfg(feature = "service-mailbox-observation-v1")]
pub(crate) const OBSERVED_IDENTITY: (&str, u16, u16) = (MODEL, DEVICE.0, DEVICE.1);
const USAGE: (u16, u16) = (1, 2);
const FRAME_LENGTH: usize = REPORT_LENGTH + 1;
const CLASS: u8 = 0x0d;
const GET_THERMAL: u8 = 0x82;
const SETTLE: Duration = Duration::from_millis(30);
const POLL: Duration = Duration::from_millis(5);
const MAX_OBSERVATION: Duration = Duration::from_secs(3);
type Frame = [u8; FRAME_LENGTH];

pub(crate) use openblade_domain::thermal_observation::ObservedPerformance;

// Only a live service owner can supply this evidence. An epoch must change
// across ownership, power, controller or lifecycle transitions, even if the
// visible state returns to the same value. Absence of any proof means None.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) struct Admission {
    pub(crate) epoch: u64,
    pub(crate) session: u32,
    pub(crate) full_adapter: bool,
    pub(crate) controller_clear: bool,
    pub(crate) awake: bool,
}
impl Admission {
    pub(crate) fn is_clear(self) -> bool {
        self.session != 0 && self.full_adapter && self.controller_clear && self.awake
    }
}

// Read custody is independent of Full adapter power, UI session and mutation
// clearance. It is minted by the concrete retained-origin/read-lease chain.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) struct ReadAdmission {
    pub(crate) lifecycle: u64,
    pub(crate) generation: [u8; 32],
}

pub(crate) trait ObservationProof: Copy + Eq + Send + Sync + 'static {
    fn readable(self) -> bool;
    fn matches_session(self, session: Option<u32>) -> bool;
}
impl ObservationProof for Admission {
    fn readable(self) -> bool {
        self.is_clear()
    }
    fn matches_session(self, session: Option<u32>) -> bool {
        session.is_none_or(|s| s == self.session)
    }
}
impl ObservationProof for ReadAdmission {
    fn readable(self) -> bool {
        self.generation != [0; 32]
    }
    fn matches_session(self, _: Option<u32>) -> bool {
        true
    }
}

type Authorize<'a, A = Admission> = &'a mut dyn FnMut() -> Option<A>;

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
    #[cfg(feature = "service-thermal-mutation-v1")]
    SendMutation(Frame),
    ReceiveReply,
}

// One operation slot. A pending slot owns its storage until terminal native
// completion; cancel alone never retires it.
trait Port {
    /// The pool shared by every port on this device's reply mailbox.
    fn transactions(&self) -> &Transactions;
    fn verify(&mut self, limit: Limit<'_>) -> io::Result<()>;
    fn submit(&mut self, transfer: Transfer, limit: Limit<'_>) -> io::Result<()>;
    fn poll(&mut self) -> Option<io::Result<(usize, Frame)>>;
    fn cancel(&mut self);
    fn poll_retirement(&mut self) -> io::Result<bool>;

    /// Opens and verifies the exact matrix collection beside this port's
    /// control collection (`select_matrix`). Its lease shares this port's
    /// lifetime: `cancel` cancels its operation and `poll_retirement`
    /// retires it too.
    #[cfg(feature = "service-lighting-v1")]
    fn open_matrix(&mut self, _limit: Limit<'_>) -> io::Result<()> {
        Err(blocked("this port has no matrix collection"))
    }
    /// One 03/0B feature report, after revalidating the matrix identity. A
    /// report the driver may have received is in doubt, whether its call
    /// failed or it was accepted; an accepted one completes through
    /// `poll_matrix`.
    #[cfg(feature = "service-lighting-v1")]
    fn submit_matrix(
        &mut self,
        _report: &MatrixReport,
        _limit: Limit<'_>,
    ) -> Result<(), Unwritten> {
        Err(Unwritten::Unsent)
    }
    #[cfg(feature = "service-lighting-v1")]
    fn poll_matrix(&mut self) -> Option<io::Result<()>> {
        None
    }
}

/// One encoded 03/0B feature report.
#[cfg(feature = "service-lighting-v1")]
type MatrixReport = [u8; openblade_protocol::lighting::matrix::FEATURE_REPORT_LENGTH];

/// How one matrix write ended, when it did not complete.
#[cfg(feature = "service-lighting-v1")]
#[derive(Debug)]
enum Unwritten {
    /// Nothing reached the driver.
    Unsent,
    /// The report may have been submitted. Managed classifies every native
    /// matrix I/O failure as indeterminate (`ProductionLightingPort`'s
    /// writer), the driver's own refusal of the call included.
    InDoubt,
}

/// Managed `IsExactMatrixInterface`: the consumer collection whose feature
/// report carries the 375-byte matrix buffer.
#[cfg(feature = "service-lighting-v1")]
const MATRIX_ROLE: super::Role = super::Role {
    usage: (0x000c, 0x0001),
    feature_bytes: openblade_protocol::lighting::matrix::FEATURE_REPORT_LENGTH as u16,
};

/// Managed `ProductionLightingFactory`'s matrix selection: the one exact
/// collection with the matrix role among `source`'s candidates, of this
/// module's model and device.
#[cfg(feature = "service-lighting-v1")]
pub(super) fn select_matrix<S: super::LeaseSource>(
    source: &S,
    cancelled: &AtomicBool,
) -> io::Result<super::RoleIdentity> {
    let identity = super::RoleIdentity::select(source, MATRIX_ROLE, cancelled)?;
    if !reviewed_matrix(&identity) {
        return Err(blocked("unsupported keyboard matrix collection"));
    }
    Ok(identity)
}

/// The exact matrix identity: this module's model and device.
#[cfg(feature = "service-lighting-v1")]
pub(super) fn reviewed_matrix(identity: &super::RoleIdentity) -> bool {
    let descriptor = identity.descriptor();
    identity.model() == MODEL
        && descriptor.device == DEVICE
        && descriptor.usage == MATRIX_ROLE.usage
        && descriptor.feature_bytes == MATRIX_ROLE.feature_bytes
}

fn reviewed_selection(selection: &Selection) -> bool {
    selection.model == MODEL && selection.device == DEVICE && selection.usage == Some(USAGE)
}

fn reviewed_descriptor(descriptor: Descriptor) -> bool {
    descriptor.device == DEVICE
        && descriptor.usage == USAGE
        && usize::from(descriptor.feature_bytes) == FRAME_LENGTH
}

fn verify_admission<A: ObservationProof>(
    authorize: Authorize<'_, A>,
    expected: A,
) -> io::Result<()> {
    if expected.readable() && authorize() == Some(expected) {
        Ok(())
    } else {
        Err(blocked("firmware observation lost live service admission"))
    }
}

struct Reader<P: Port, A = Admission> {
    port: P,
    admission: A,
    poisoned: bool,
    // The two longest exchanges since `mutation::Pace::start`, for an
    // operation's measured pace.
    exchanges: Exchanges,
    // IDs this Reader reserved; those never sent are freed on retirement.
    held: Vec<u8>,
    // Pinned IDs that no record names yet; unpinned on retirement.
    provisional: Vec<u8>,
    // Pinned IDs of records that have settled (`mutation::Permit::settled_pins`);
    // unpinned on retirement.
    settled: Vec<u8>,
    // The request whose reply the next GET reads.
    request: Option<Frame>,
    // A reservation failed because fewer than two IDs were free.
    exhausted: bool,
}

/// The two longest exchanges a Reader has completed. Each runs from the
/// previous reply's completion (the first from `mutation::Pace::start`) to
/// its own reply's completion, so it includes the work before its send:
/// discovery, authorization and the custody and origin checks of each
/// checkpoint, as well as the send, the settle wait and the reply.
/// `mutation::Pace::prepared` moves `last` past the Prepared append, which
/// it charges separately.
#[derive(Default)]
struct Exchanges {
    last: Option<Instant>,
    slowest: Duration,
    second: Duration,
}
impl Exchanges {
    fn replied(&mut self, at: Instant) {
        let Some(last) = self.last.replace(at) else {
            return;
        };
        let exchange = at.saturating_duration_since(last);
        if exchange > self.slowest {
            self.second = self.slowest;
            self.slowest = exchange;
        } else if exchange > self.second {
            self.second = exchange;
        }
    }
}

impl<P: Port, A: ObservationProof> Reader<P, A> {
    fn new(port: P, admission: A) -> Self {
        Self {
            port,
            admission,
            poisoned: false,
            exchanges: Exchanges::default(),
            held: Vec::new(),
            provisional: Vec::new(),
            settled: Vec::new(),
            request: None,
            exhausted: false,
        }
    }

    /// Two IDs for this Reader's own exchanges.
    fn reserve_pair(&mut self) -> Option<[u8; 2]> {
        let pair = self.port.transactions().reserve_pair();
        match pair {
            Some(pair) => self.held.extend(pair),
            None => self.exhausted = true,
        }
        pair
    }

    /// Two IDs that a durable record may name. They stay pinned until this
    /// Reader retires, and until the record settles once `retain` is called:
    /// the record may carry them to a later Reader, and the journal requires
    /// a live fan owner's IDs to stay distinct from later battery IDs.
    #[cfg(feature = "service-thermal-mutation-v1")]
    fn reserve_retained_pair(&mut self) -> Option<[u8; 2]> {
        let pair = self.reserve_pair()?;
        for id in pair {
            self.port.transactions().pin(id);
        }
        self.provisional.extend(pair);
        Some(pair)
    }

    /// Hands every ID from `reserve_retained_pair` to `permit`'s record, which
    /// keeps them pinned until it settles (`mutation::Permit::retained_pins`).
    /// Call it once the record names them, before its first setter could be
    /// submitted; until then, retirement unpins them.
    #[cfg(feature = "service-thermal-mutation-v1")]
    fn retain(&mut self, permit: &dyn mutation::Permit) {
        permit.retained_pins(&self.provisional);
        self.provisional.clear();
    }

    /// Records the request before submission: from here on its ID is
    /// quarantined unless a correlated terminal reply is read.
    fn sending(&mut self, frame: &Frame) -> io::Result<()> {
        self.request = Some(*frame);
        if self.port.transactions().sent(frame[2]) {
            Ok(())
        } else {
            Err(blocked("transaction ID pool is unavailable"))
        }
    }

    fn replied(&mut self, length: usize, reply: &Frame) {
        if let Some((id, class, command)) = self
            .request
            .take()
            .and_then(|request| transactions::terminal_reply(&request, length, reply))
        {
            self.port.transactions().answered(id, class, command);
        }
    }

    async fn observe(
        &mut self,
        authorize: Authorize<'_, A>,
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

    /// One worker job on this freshly opened Reader, shared by the native
    /// worker and the fixture workers: the reads the job asks for (none when
    /// the port did not bind), retirement, then the fresh admission check on
    /// the job clock. A result past the deadline or after admission was lost
    /// is discarded.
    #[cfg(any(test, feature = "firmware-performance-compile-check"))]
    async fn job(
        &mut self,
        authorize: Authorize<'_, A>,
        job: &worker::Job,
        bound: bool,
    ) -> io::Result<Option<worker::Reading<A>>> {
        let limit = job.limit();
        let admission = self.admission;
        let reading = if !bound {
            None
        } else {
            #[cfg(feature = "service-keyboard-driver-mode-read-v1")]
            {
                self.observe_job(&mut *authorize, limit, job.queries.device_mode)
                    .await
                    .map(|(performance, device_mode)| worker::Reading {
                        device_mode,
                        ..worker::Reading::thermal(admission, performance)
                    })
            }
            #[cfg(not(feature = "service-keyboard-driver-mode-read-v1"))]
            {
                self.observe(&mut *authorize, limit)
                    .await
                    .map(|performance| worker::Reading::thermal(admission, performance))
            }
        };
        // The fan speed queries follow the thermal read and any device-mode
        // query, only when that read completed (`FanRpmRead`).
        #[cfg(feature = "service-live-readings-v1")]
        let reading = match (reading, &job.queries.fan_rpm) {
            (Some(reading), Some(sends)) => Some(worker::Reading {
                fan_rpm: self.read_fan_rpm(&mut *authorize, limit, sends).await,
                ..reading
            }),
            (reading, _) => reading,
        };
        // The MCU version query comes last, only when the thermal read
        // completed and no earlier query sealed the Reader
        // (`McuVersionRead`): a failed query then costs no other reading.
        #[cfg(feature = "service-live-readings-v1")]
        let reading = match (reading, &job.queries.mcu_version) {
            (Some(reading), Some(sends)) => Some(worker::Reading {
                mcu_version: self.read_mcu_version(&mut *authorize, limit, sends).await,
                ..reading
            }),
            (reading, _) => reading,
        };
        // Caller timeout revokes delivery, but the worker owns the native
        // resources until terminal I/O and callback drain are both confirmed.
        // Retirement therefore has no deadline of its own, and a reading whose
        // retirement ends past the job deadline is discarded below.
        self.retire(None).await?;
        Ok(reading.filter(|_| {
            limit.check().is_ok()
                && verify_admission(authorize, admission).is_ok()
                && limit.check().is_ok()
        }))
    }

    // Once retirement begins no further traffic is permitted, even if a
    // canceled operation completes successfully after the observation deadline.
    // Only then can no reserved ID still be sent, so the unsent are freed.
    // Held IDs go first: `release` skips the pinned ones, which `unpin` then
    // frees, so nothing this Reader frees is released twice. The order is
    // otherwise immaterial only because one Reader runs at a time (the
    // device's `OCCUPIED` worker guard): no other Reader can be issued an ID
    // between the two steps. A retirement that is never confirmed frees
    // nothing, and its provisional and settled pins stay out of the pool.
    async fn retire(&mut self, deadline: Option<Instant>) -> io::Result<()> {
        self.seal();
        loop {
            if self.port.poll_retirement()? {
                let transactions = self.port.transactions();
                for id in self.held.drain(..) {
                    transactions.release(id);
                }
                transactions.unpin(self.provisional.drain(..).chain(self.settled.drain(..)));
                return Ok(());
            }
            if deadline.is_some_and(|deadline| Instant::now() >= deadline) {
                return Err(io::ErrorKind::TimedOut.into());
            }
            let poll = Instant::now() + POLL;
            sleep_until(deadline.map_or(poll, |deadline| deadline.min(poll))).await;
        }
    }

    fn seal(&mut self) {
        if !self.poisoned {
            self.poisoned = true;
            self.port.cancel();
        }
    }
}

/// Whether a paired read also reads Custom levels, and with which getter IDs.
#[derive(Clone, Copy, PartialEq, Eq)]
enum LevelRead {
    Skip,
    Reserve,
    Reserved([u8; 2]),
}

struct Attempt<'a, P: Port, A: ObservationProof> {
    reader: &'a mut Reader<P, A>,
    completed: bool,
}

impl<P: Port, A: ObservationProof> Attempt<'_, P, A> {
    async fn read_pair(
        &mut self,
        authorize: Authorize<'_, A>,
        admission: A,
        limit: Limit<'_>,
    ) -> Option<ObservedPerformance> {
        self.checkpoint(authorize, admission, limit).ok()?;
        let transactions = self.reader.reserve_pair()?;
        self.read_reserved_pair(authorize, admission, limit, transactions, true)
            .await
    }

    async fn read_reserved_pair(
        &mut self,
        authorize: Authorize<'_, A>,
        admission: A,
        limit: Limit<'_>,
        transactions: [u8; 2],
        include_custom: bool,
    ) -> Option<ObservedPerformance> {
        let levels = if include_custom {
            LevelRead::Reserve
        } else {
            LevelRead::Skip
        };
        self.read_reserved(authorize, admission, limit, transactions, levels)
            .await
    }

    /// A paired read whose Custom level getters were reserved with the rest
    /// of an operation, so a Custom selector always reads its levels.
    async fn read_reserved_custom(
        &mut self,
        authorize: Authorize<'_, A>,
        admission: A,
        limit: Limit<'_>,
        transactions: [u8; 2],
        levels: [u8; 2],
    ) -> Option<ObservedPerformance> {
        self.read_reserved(
            authorize,
            admission,
            limit,
            transactions,
            LevelRead::Reserved(levels),
        )
        .await
    }

    async fn read_reserved(
        &mut self,
        authorize: Authorize<'_, A>,
        admission: A,
        limit: Limit<'_>,
        transactions: [u8; 2],
        levels: LevelRead,
    ) -> Option<ObservedPerformance> {
        self.checkpoint(authorize, admission, limit).ok()?;
        let mut selectors = [(0, 0); 2];
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
            let [1, returned_channel, mode, fan] = reply.as_slice() else {
                return None;
            };
            if *returned_channel != channel {
                return None;
            }
            selectors[index] = (*mode, *fan);
            self.checkpoint(authorize, admission, limit).ok()?;
        }

        if selectors[0] != selectors[1] {
            return None;
        }
        match selectors[0] {
            (0, 0) => Some(ObservedPerformance::BalancedAutomatic),
            (2, 0) => Some(ObservedPerformance::PerformanceAutomatic),
            (5, 0) => Some(ObservedPerformance::SilentAutomatic),
            (5, 1) => Some(ObservedPerformance::SilentManual),
            (2, 1) => Some(ObservedPerformance::PerformanceManual),
            (3, 0) => Some(ObservedPerformance::BatterySaverAutomatic),
            (6, 0) => Some(ObservedPerformance::BatteryBalancedAutomatic),
            (3, 1) => Some(ObservedPerformance::BatterySaverManual),
            (6, 1) => Some(ObservedPerformance::BatteryBalancedManual),
            (0, 1) => Some(ObservedPerformance::FixedMaximum),
            (7, 0) => Some(ObservedPerformance::HyperBoostObserved),
            (4, fan @ (0 | 1)) if levels == LevelRead::Skip => Some(ObservedPerformance::Custom {
                manual: fan == 1,
                levels: None,
            }),
            (4, fan @ (0 | 1)) => {
                // Reserve only after a complete Custom selector pair. A new handle
                // does not drain firmware replies, so it frees no ID either.
                let [cpu_id, gpu_id] = match levels {
                    LevelRead::Reserved(ids) => ids,
                    _ => self.reader.reserve_pair()?,
                };
                let cpu = self
                    .read_custom(
                        CustomPerformanceQuery::new(cpu_id, CustomPerformanceComponent::Cpu),
                        authorize,
                        admission,
                        limit,
                    )
                    .await?;
                let gpu = self
                    .read_custom(
                        CustomPerformanceQuery::new(gpu_id, CustomPerformanceComponent::Gpu),
                        authorize,
                        admission,
                        limit,
                    )
                    .await?;
                self.checkpoint(authorize, admission, limit).ok()?;
                ObservedPerformance::rz09_0581_custom(fan == 1, cpu, gpu)
            }
            _ => None,
        }
    }

    async fn read_custom(
        &mut self,
        query: CustomPerformanceQuery,
        authorize: Authorize<'_, A>,
        admission: A,
        limit: Limit<'_>,
    ) -> Option<openblade_protocol::custom_performance::CustomPerformanceLevel> {
        // Invocation is not submission. This Reader owns the enclosing attempt;
        // canceled/incomplete results seal it until actual native retirement.
        self.checkpoint(authorize, admission, limit).ok()?;
        let mut frame = [0; FRAME_LENGTH];
        frame[1..].copy_from_slice(&query.request().encode());
        self.transfer(Transfer::SendGetter(frame), limit)
            .await
            .ok()?;
        limit.wait(SETTLE).await.ok()?;
        self.checkpoint(authorize, admission, limit).ok()?;
        let (length, frame) = self.transfer(Transfer::ReceiveReply, limit).await.ok()?;
        if !matches!(length, REPORT_LENGTH | FRAME_LENGTH) || frame[0] != 0 {
            return None;
        }
        // Return CPU success directly: GPU invocation owns its admission check.
        query.decode(&frame[1..]).ok()
    }

    fn checkpoint(
        &mut self,
        authorize: Authorize<'_, A>,
        admission: A,
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
        let reply = matches!(transfer, Transfer::ReceiveReply);
        match &transfer {
            Transfer::SendGetter(frame) => self.reader.sending(frame)?,
            #[cfg(feature = "service-thermal-mutation-v1")]
            Transfer::SendMutation(frame) => self.reader.sending(frame)?,
            Transfer::ReceiveReply => {}
        }
        self.reader.port.submit(transfer, limit)?;
        loop {
            limit.check()?;
            if let Some(completion) = self.reader.port.poll() {
                limit.check()?;
                if reply && let Ok((length, frame)) = &completion {
                    self.reader.exchanges.replied(Instant::now());
                    self.reader.replied(*length, frame);
                }
                return completion;
            }
            limit.wait(POLL).await?;
        }
    }
}

impl<P: Port, A: ObservationProof> Drop for Attempt<'_, P, A> {
    fn drop(&mut self) {
        if !self.completed {
            self.reader.seal();
        }
    }
}

#[cfg(all(test, feature = "service-thermal-mutation-v1"))]
pub(crate) mod mutation_fixture;

//! The 00/84 keyboard device-mode query on the same serialized native Reader
//! custody as the thermal observation, and, only with
//! `service-keyboard-driver-mode-v1`, the one 00/04 write recipe a mutation
//! Reader runs for the keyboard Driver-mode owner (`apply_device_mode`). The
//! read job never sends a setter. The host's device-mode guard test fails
//! when another setter user or a new report construction appears.
use super::*;
#[cfg(feature = "service-keyboard-driver-mode-v1")]
use openblade_protocol::device_mode::DeviceModeSet;
use openblade_protocol::device_mode::{DeviceMode, DeviceModeQuery};
#[cfg(feature = "service-keyboard-driver-mode-v1")]
use std::sync::Mutex;

/// The query's own budget: three identity checkpoints, the settle and one
/// exchange. A stalled query ends here rather than at the job deadline.
const QUERY_BUDGET: Duration = Duration::from_secs(1);
/// Job time the query never takes, kept for retirement, the final admission
/// check and publication, so a timed-out query cannot cost the thermal read.
const QUERY_RESERVE: Duration = Duration::from_millis(500);

/// What one read job learned about the keyboard device mode.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub(crate) enum DeviceModeRead {
    /// Not queried: not asked, or too little job time remained to send it.
    #[default]
    NotRequested,
    /// Sent, but no valid correlated reply was confirmed within the query's
    /// budget. Unknown, never a guessed mode.
    Unconfirmed,
    Confirmed(DeviceMode),
}

impl<P: Port, A: ObservationProof> Reader<P, A> {
    /// One read job: the thermal observation, then, only when asked and only
    /// after that observation completed, one 00/84 query within its own
    /// budget, ending `QUERY_RESERVE` before the job deadline. A failed query
    /// seals this Reader like any incomplete exchange, and this Reader sends
    /// nothing more; the thermal result it followed is still returned.
    pub(super) async fn observe_job(
        &mut self,
        authorize: Authorize<'_, A>,
        limit: Limit<'_>,
        device_mode: bool,
    ) -> Option<(ObservedPerformance, DeviceModeRead)> {
        let performance = self.observe(&mut *authorize, limit).await?;
        if !device_mode {
            return Some((performance, DeviceModeRead::NotRequested));
        }
        let now = Instant::now();
        let Some(deadline) = limit
            .deadline
            .checked_sub(QUERY_RESERVE)
            .map(|deadline| deadline.min(now + QUERY_BUDGET))
            .filter(|deadline| *deadline > now)
        else {
            return Some((performance, DeviceModeRead::NotRequested));
        };
        let query = Limit { deadline, ..limit };
        let admission = self.admission;
        let mut attempt = Attempt {
            reader: self,
            completed: false,
        };
        let mode = attempt.read_device_mode(authorize, admission, query).await;
        attempt.completed = mode.is_some();
        Some((
            performance,
            mode.map_or(DeviceModeRead::Unconfirmed, DeviceModeRead::Confirmed),
        ))
    }
}

impl<P: Port, A: ObservationProof> Attempt<'_, P, A> {
    async fn read_device_mode(
        &mut self,
        authorize: Authorize<'_, A>,
        admission: A,
        limit: Limit<'_>,
    ) -> Option<DeviceMode> {
        self.checkpoint(authorize, admission, limit).ok()?;
        // IDs are reserved in pairs. The query sends the first; the second is
        // never sent, so no reply can be attributed to it, and retirement
        // frees it.
        let [transaction, _] = self.reader.reserve_pair()?;
        self.query_device_mode(authorize, admission, limit, transaction)
            .await
    }

    /// One 00/84 exchange with an already reserved `transaction`, after the
    /// caller's own checkpoint.
    async fn query_device_mode(
        &mut self,
        authorize: Authorize<'_, A>,
        admission: A,
        limit: Limit<'_>,
        transaction: u8,
    ) -> Option<DeviceMode> {
        let query = DeviceModeQuery::new(transaction);
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
        let mode = query.decode(&frame[1..]).ok()?;
        self.checkpoint(authorize, admission, limit).ok()?;
        Some(mode)
    }
}

/// How far one Driver-mode write job got (`DeviceModeJob::step`). The owner
/// reads the last step after the job, so a job that stops at any point,
/// cancellation included, leaves the step that says what may have reached
/// the firmware.
#[cfg(feature = "service-keyboard-driver-mode-v1")]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) enum DeviceModeStep {
    /// No setter was sent and nothing is in doubt: the job never ran on a
    /// bound, admitted device, sent nothing, or read its baseline and then
    /// found too little time for the claim's record, the setter, its
    /// acknowledgement and the readback (`Pace::covers`).
    Pending,
    /// The baseline 00/84 query may have been sent and no reply was
    /// confirmed. Only that query may have been sent.
    Unread,
    /// The baseline read this mode and the job wrote nothing.
    Observed(DeviceMode),
    /// The baseline admitted the setter, but the owner's claim could not be
    /// recorded durably, so the setter was not sent. Nothing is in doubt in
    /// firmware.
    Unrecorded,
    /// The baseline admitted the setter and the owner's claim was recorded,
    /// but the setter was not submitted (managed `KnownNotApplied`): its
    /// checkpoint refused, or the claim took so long that the setter, its
    /// acknowledgement and the readback no longer fit (`Pace::covers`).
    Claimed,
    /// The setter may have been submitted and its exact acknowledgement was
    /// not confirmed.
    Submitted,
    /// The exact acknowledgement was read; the independent 00/84 readback did
    /// not confirm the target.
    Acknowledged,
    /// The independent 00/84 readback confirmed the target.
    Confirmed,
}

/// The owner's claim (`DeviceModeJob::claim`): it records the job's four
/// reserved IDs, baseline, setter, readback and spare, durably, and an error
/// means the claim was not recorded and the setter must not be sent.
#[cfg(feature = "service-keyboard-driver-mode-v1")]
type Claim = Box<dyn Fn(&[u8; 4]) -> io::Result<()> + Send + Sync>;

/// One Driver-mode write job, carried by its permit
/// (`mutation::Permit::device_mode`): a fresh 00/84 read, then, only when that
/// read names `write_from`, the owner's claim, the 00/04 setter for `target`,
/// its exact acknowledgement and an independent 00/84 readback. Managed
/// `ProductionLightingPort.MaintainDriverModeAsync` (acquire) and
/// `TryRestoreOwnedDeviceModeCoreAsync` (release).
#[cfg(feature = "service-keyboard-driver-mode-v1")]
pub(crate) struct DeviceModeJob {
    target: DeviceMode,
    write_from: Option<DeviceMode>,
    claim: Claim,
    /// What the claim's durable writes are expected to take, charged in the
    /// pace before the claim.
    claim_allowance: Duration,
    step: Mutex<DeviceModeStep>,
}

#[cfg(feature = "service-keyboard-driver-mode-v1")]
impl DeviceModeJob {
    /// A job that writes `target` only from a baseline of `write_from`, and
    /// only after `claim` has recorded the claim; `None` reads the baseline
    /// and writes nothing. `claim` gets the job's reserved IDs and must make
    /// the claim durable before it returns `Ok`: the setter can be submitted
    /// as soon as it does. The claim is made only when `claim_allowance`, an
    /// estimate of the claim's own time, fits in the job's pace beside the
    /// exchanges after it, and the setter is sent only when those exchanges
    /// still fit once the claim's real time is spent.
    pub(crate) fn new(
        target: DeviceMode,
        write_from: Option<DeviceMode>,
        claim_allowance: Duration,
        claim: impl Fn(&[u8; 4]) -> io::Result<()> + Send + Sync + 'static,
    ) -> Self {
        Self {
            target,
            write_from,
            claim: Box::new(claim),
            claim_allowance,
            step: Mutex::new(DeviceModeStep::Pending),
        }
    }

    /// The last step the job reached. A poisoned record is the most
    /// conservative step: the setter may have been submitted.
    pub(crate) fn step(&self) -> DeviceModeStep {
        self.step
            .lock()
            .map_or(DeviceModeStep::Submitted, |step| *step)
    }

    fn record(&self, step: DeviceModeStep) {
        if let Ok(mut current) = self.step.lock() {
            *current = step;
        }
    }
}

/// The fixed time `Pace::covers` charges a Driver-mode setter beyond its own
/// and the readback's exchanges, after the baseline read: the baseline's
/// measured length again, and the readback's trailing checkpoint, which no
/// reply-to-reply exchange includes, at the baseline's own, from its reply to
/// `now`.
#[cfg(feature = "service-keyboard-driver-mode-v1")]
pub(super) fn setter_fixed(exchanges: &Exchanges, now: Instant) -> Duration {
    let trailing = exchanges
        .last
        .map_or(Duration::ZERO, |last| now.saturating_duration_since(last));
    exchanges.slowest.saturating_add(trailing)
}

#[cfg(feature = "service-keyboard-driver-mode-v1")]
impl<P: Port> Reader<P, ReadAdmission> {
    /// The Driver-mode write recipe (`DeviceModeJob`). Every ID it may send is
    /// reserved before the first send, so exhaustion strands no setter. The
    /// IDs are seeded, not pinned: the claim records all four durably before
    /// the setter can be sent, so a later process quarantines them before its
    /// first job (`transaction_seed`), while in this process each sent ID
    /// stays quarantined until a correlated reply is read, as every
    /// unjournaled exchange's does, and the unsent ones are freed at
    /// retirement. A new job always draws fresh IDs.
    pub(super) async fn apply_device_mode(
        &mut self,
        permit: &dyn mutation::Permit,
        job: &DeviceModeJob,
        limit: Limit<'_>,
    ) {
        let _ = self.run_device_mode(permit, job, limit).await;
    }

    async fn run_device_mode(
        &mut self,
        permit: &dyn mutation::Permit,
        job: &DeviceModeJob,
        limit: Limit<'_>,
    ) -> Option<()> {
        let mut authorize = || permit.authorize();
        let [baseline_id, set_id] = self.reserve_pair()?;
        let [readback_id, spare_id] = self.reserve_pair()?;
        let admission = self.admission;
        // The exchanges are timed from here, so the setter is sent only when
        // the job can still read its acknowledgement and the readback.
        let pace = mutation::Pace::start(self);
        let mut attempt = Attempt {
            reader: self,
            completed: false,
        };
        attempt.checkpoint(&mut authorize, admission, limit).ok()?;
        // From the call that may send it, the baseline query is in doubt.
        job.record(DeviceModeStep::Unread);
        let baseline = attempt
            .query_device_mode(&mut authorize, admission, limit, baseline_id)
            .await?;
        if job.write_from != Some(baseline) {
            job.record(DeviceModeStep::Observed(baseline));
            attempt.completed = true;
            return Some(());
        }
        // Two exchanges remain, the setter and the readback, each charged at the
        // baseline's measured length (its checkpoints and settle included); no
        // journal row is written. With one exchange measured, `covers` charges
        // the second remaining one at the second-longest, which is zero, so the
        // baseline is charged again as fixed time. Exchanges are timed reply to
        // reply, so the readback's trailing checkpoint is in none of them; it is
        // charged at the baseline's own, measured from its reply to now. The
        // claim's durable record write comes before the setter and is in no
        // exchange either, so its allowance is fixed time too. Too little time
        // left is known not applied, before any claim, and the baseline read
        // leaves nothing in doubt.
        let fixed = setter_fixed(&attempt.reader.exchanges, Instant::now());
        if !pace.covers(
            attempt.reader,
            2,
            0,
            fixed.saturating_add(job.claim_allowance),
            limit,
        ) {
            job.record(DeviceModeStep::Pending);
            return None;
        }
        // Managed claims ownership before the setter is submitted, so any
        // later failure leaves cleanup able to restore Normal. Here the claim
        // is durable, naming every ID this job may send, and a claim that
        // could not be recorded sends no setter.
        if (job.claim)(&[baseline_id, set_id, readback_id, spare_id]).is_err() {
            job.record(DeviceModeStep::Unrecorded);
            return None;
        }
        job.record(DeviceModeStep::Claimed);
        // The allowance was an estimate. With the claim's real time spent,
        // the setter is sent only if it, its acknowledgement and the readback
        // still fit; otherwise it is known not applied and the claim reverts.
        // `fixed` was measured before the claim, so the claim is charged once,
        // in the time that has passed.
        if !pace.covers(attempt.reader, 2, 0, fixed, limit) {
            return None;
        }
        let set = DeviceModeSet::new(set_id, job.target);
        let mut frame = [0; FRAME_LENGTH];
        frame[1..].copy_from_slice(&set.request().encode());
        attempt.checkpoint(&mut authorize, admission, limit).ok()?;
        // From the call that may submit it, the setter counts as possibly
        // submitted until its exact acknowledgement is read.
        job.record(DeviceModeStep::Submitted);
        attempt
            .transfer(Transfer::SendMutation(frame), limit)
            .await
            .ok()?;
        limit.wait(SETTLE).await.ok()?;
        attempt.checkpoint(&mut authorize, admission, limit).ok()?;
        let (length, reply) = attempt.transfer(Transfer::ReceiveReply, limit).await.ok()?;
        if !matches!(length, REPORT_LENGTH | FRAME_LENGTH) || reply[0] != 0 {
            return None;
        }
        // The reply must correlate (ID, class, command, status, checksum) and
        // echo the setter arguments byte for byte.
        set.acknowledge(&reply[1..]).ok()?;
        job.record(DeviceModeStep::Acknowledged);
        attempt.checkpoint(&mut authorize, admission, limit).ok()?;
        let readback = attempt
            .query_device_mode(&mut authorize, admission, limit, readback_id)
            .await?;
        if readback == job.target {
            job.record(DeviceModeStep::Confirmed);
            attempt.completed = true;
        }
        Some(())
    }
}

//! The animated renderer's stream for `lighting_mutation` (managed
//! `RunStreamAsync`): its frames on the stream task
//! (`Runtime::run_lighting_stream`), each under the origin tick's clean
//! controller reading (`Runtime::confirm_stream_clean`), on the Service's
//! matrix stream (`MatrixStream`). Every wait on that stream is bounded
//! here: a frame by its budget (`FRAME_BUDGET`), a request's open by its own
//! (`OPEN_BUDGET`) within the request's deadline, and Stop's settle by its
//! share of the drain (`STREAM_SETTLE_BUDGET`). A frame that outlasts its
//! budget is in doubt: the doubt latches, the renderer ends, its frames are
//! recalled (`MatrixStream::retire`) and the operation gate is released. An
//! open that outlasts its budget is recalled and cancelled the same way, and
//! the request refuses before any setter.
use super::super::{
    Fresh, Runtime, is_competing_controller, is_fatal_admission, verify_retained_failure,
};
use super::{Session, Unwritten};
use crate::discovery::performance::{Animation, MatrixStream, stream_report};
use std::{cell::Cell, io, rc::Rc, sync::Arc, time::Duration};
use tokio::time::{Instant, timeout_at};

/// Managed `RunStreamAsync`'s state for one animated renderer.
pub(super) struct Stream {
    pub(super) animation: Animation,
    /// When the effect started (managed `effectStartedTimestamp`): the new
    /// renderer's handshake.
    pub(super) started: Instant,
    /// The next frame's deadline, in ticks since `started`.
    pub(super) next: i64,
    /// The latest admission that confirmed a clean controller reading: the
    /// request that started the stream, then each origin tick's capture.
    pub(super) clean: Option<Clean>,
}

/// One clean reading: when it was taken, its admission and the Windows
/// power generation then.
pub(super) struct Clean {
    at: Instant,
    fresh: Arc<dyn Fresh>,
    generation: Option<u64>,
}

impl Clean {
    pub(super) fn new(fresh: Arc<dyn Fresh>, at: Instant) -> Self {
        let generation = fresh.power_generation();
        Self {
            at,
            fresh,
            generation,
        }
    }

    /// Whether a frame at `now` is admitted: this reading is no older than
    /// `CLEAN_READING_AGE`, its admission admits a frame
    /// (`Fresh::authorize_frame`), and the Windows power generation is the
    /// one it was taken at.
    fn admits(&self, now: Instant) -> bool {
        now.saturating_duration_since(self.at) <= CLEAN_READING_AGE
            && self.fresh.authorize_frame().is_some()
            && self.fresh.power_generation() == self.generation
    }
}

/// How old the latest clean controller reading may be when a stream frame
/// is written: two origin ticks. An older one writes nothing until the next
/// tick confirms a fresh one (`Runtime::stream_frame`).
const CLEAN_READING_AGE: Duration = Duration::from_secs(1);
/// The bound on one stream frame, from its render to its completion.
const FRAME_BUDGET: Duration = Duration::from_millis(500);
/// The bound on a request's open of the stream's collection, within the
/// request's deadline: a selection that does not finish in time is cancelled
/// and the request takes the unopened reply, leaving the rest of its budget to
/// store the intent.
const OPEN_BUDGET: Duration = Duration::from_secs(1);
/// Stop's share of the source drain for the stream's settle, out of the
/// time the display-brightness release, the keyboard release, the fan
/// handback and the HyperBoost exit are paced to (`readiness::SOURCE_DRAIN_TIMEOUT`).
/// It is at most one frame's budget, which already bounds a frame in flight;
/// a settle not confirmed within it latches the doubt and the drain goes on.
const STREAM_SETTLE_BUDGET: Duration = Duration::from_millis(300);
const _: () = assert!(STREAM_SETTLE_BUDGET.as_nanos() <= FRAME_BUDGET.as_nanos());

/// The ticks from `started` to `now`.
pub(super) fn ticks(started: Instant, now: Instant) -> i64 {
    i64::try_from(now.saturating_duration_since(started).as_nanos() / 100).unwrap_or(i64::MAX)
}

/// The instant `ticks` after `started`.
pub(super) fn at_ticks(started: Instant, ticks: i64) -> Instant {
    started + Duration::from_nanos(u64::try_from(ticks).unwrap_or(0).saturating_mul(100))
}

/// A stream frame's hold on the operation gate, until its deadline: the
/// origin tick waits for it rather than lose its clean reading
/// (`Runtime::maintain_lighting`).
struct Hold<'a>(&'a Cell<Option<Instant>>);

impl<'a> Hold<'a> {
    fn new(slot: &'a Cell<Option<Instant>>, deadline: Instant) -> Self {
        slot.set(Some(deadline));
        Self(slot)
    }
}

impl Drop for Hold<'_> {
    fn drop(&mut self) {
        self.0.set(None);
    }
}

impl Session {
    /// Whether the owned renderer is animated.
    pub(super) fn animated(&self) -> bool {
        self.owner
            .borrow()
            .as_ref()
            .is_some_and(|owner| owner.stream.is_some())
    }

    /// Recalls the stream's frames and closes its collection, when a
    /// request opened it.
    pub(super) fn close_stream(&self) {
        if self.stream_open.replace(false)
            && let Some(stream) = self.stream.get()
        {
            stream.retire();
        }
    }

    /// The bound on one frame.
    fn frame_budget(&self) -> Duration {
        #[cfg(test)]
        if let Some(budget) = self.frame_budget.get() {
            return budget;
        }
        FRAME_BUDGET
    }
}

/// What the stream loop does after one `stream_frame`.
#[derive(Debug, PartialEq, Eq)]
pub(in super::super::super) enum Next {
    /// The next frame is due at this instant.
    At(Instant),
    /// Nothing is due until the loop is woken: no animated renderer is
    /// owned, or no admitted clean reading, which the next origin tick that
    /// confirms one wakes.
    Wake,
}

impl Runtime {
    /// Opens the stream's collection, or confirms it is open, for an
    /// animated request within `OPEN_BUDGET` of its `deadline`: `false` when
    /// it is not, which refuses the request before any setter. An open that
    /// outlasts its bound is recalled, which cancels its selection. Opening
    /// writes nothing, and the collection stays open while an animated
    /// renderer is owned.
    pub(super) async fn open_stream(&self, port: &Rc<dyn MatrixStream>, deadline: Instant) -> bool {
        // A late open still closes with the session's next retire.
        self.lighting.stream_open.set(true);
        let bound = deadline.min(Instant::now() + OPEN_BUDGET);
        match timeout_at(bound, port.open()).await {
            Ok(opened) => opened.is_ok(),
            Err(_) => {
                self.lighting.close_stream();
                false
            }
        }
    }

    /// The origin tick's step for an animated renderer: one capture of the
    /// owning request's admission, whose full write admission, the
    /// competing-controller inventory included, makes the stream's clean
    /// reading at `now` (`CLEAN_READING_AGE`) and wakes the stream loop. The
    /// capture's own supplier pass is that admission
    /// (`Fresh::admitted_at_capture`). A competing controller retires the
    /// renderer without a write, as managed's controller refresh relinquishes
    /// lighting; any other refusal leaves no clean reading, so the stream
    /// writes nothing until a later tick confirms one. Only fatal admission
    /// evidence is an error.
    pub(super) fn confirm_stream_clean(
        &self,
        reader: &super::super::super::mailbox_read::ReadSource,
        sid: &str,
        session: u32,
        now: Instant,
    ) -> io::Result<()> {
        verify_retained_failure(&self.fatal)?;
        reader.custody().verify_integrity()?;
        let clean = match self.capture(sid, session, reader.custody()) {
            Ok((_, fresh)) => {
                fresh.verify_health()?;
                fresh.admitted_at_capture().then(|| Clean::new(fresh, now))
            }
            Err(error) if is_fatal_admission(&error) => return Err(error),
            Err(error) if is_competing_controller(&error) => {
                self.lighting.retire();
                return Ok(());
            }
            Err(_) => None,
        };
        let confirmed = clean.is_some();
        if let Some(stream) = self
            .lighting
            .owner
            .borrow_mut()
            .as_mut()
            .and_then(|owner| owner.stream.as_mut())
        {
            stream.clean = clean;
        }
        if confirmed {
            self.lighting.wake.notify_one();
        }
        Ok(())
    }

    /// One frame of the animated renderer at `now`, from the stream loop
    /// (`run_lighting_stream`), and what the loop does next. Managed
    /// `RunStreamAsync`: the frame is the animation at the time elapsed since
    /// the effect started, and the next deadline advances from the instant
    /// observed after the write (`Effect::advance`). Each frame, before
    /// anything is sent:
    ///
    /// - takes the operation gate without waiting: while a request or the
    ///   origin tick holds it, the frame is skipped and retried one interval
    ///   later, a visible pause. The origin tick waits for a gate a frame
    ///   holds instead, so a frame never costs the tick its clean reading;
    /// - ends the renderer without a write at a low-power boundary
    ///   (`crossed_low_power`);
    /// - needs a clean controller reading no older than `CLEAN_READING_AGE`
    ///   from the origin tick, and that reading's frame admission at the same
    ///   Windows power generation (`Clean::admits`). Otherwise it writes
    ///   nothing, and the loop waits for the tick that confirms a reading.
    ///
    /// The frame's wait is bounded by its budget. A frame refused before
    /// submission ends the renderer and the stream; one that may have been
    /// submitted without completing, or that outlasted its budget, also
    /// latches the doubt. Only fatal admission evidence is an error.
    pub(in super::super::super) async fn stream_frame(&self, now: Instant) -> io::Result<Next> {
        let started = Instant::now();
        let lighting = &self.lighting;
        let rate = lighting.rate.get();
        let interval = Duration::from_nanos(u64::try_from(rate.interval()).unwrap_or(0) * 100);
        let Ok(_serial) = self.gate.try_lock() else {
            return Ok(if lighting.animated() {
                Next::At(now + interval)
            } else {
                Next::Wake
            });
        };
        verify_retained_failure(&self.fatal)?;
        if self.crossed_low_power() {
            self.retire_renderer();
            return Ok(Next::Wake);
        }
        let Some(port) = lighting.stream.get().cloned() else {
            return Ok(Next::Wake);
        };
        // A defensive backstop: the latch already ended the renderer.
        if lighting.latched() {
            return Ok(Next::Wake);
        }
        let observed = |stream: &Stream| ticks(stream.started, now + started.elapsed());
        // Render under the borrow; the frame is written after it ends.
        let report = {
            let mut guard = lighting.owner.borrow_mut();
            let Some(owner) = guard.as_mut() else {
                return Ok(Next::Wake);
            };
            let (layout, effect) = (owner.renderer.layout, owner.renderer.effect);
            let Some(stream) = owner.stream.as_mut() else {
                return Ok(Next::Wake);
            };
            let due = at_ticks(stream.started, stream.next);
            if now < due {
                return Ok(Next::At(due));
            }
            if !stream.clean.as_ref().is_some_and(|clean| clean.admits(now)) {
                stream.next = effect.advance(stream.next, observed(stream), rate);
                return Ok(Next::Wake);
            }
            let elapsed = ticks(stream.started, now);
            match stream_report(&mut stream.animation, layout, elapsed) {
                Some(report) => report,
                // The effect drew nothing (a failed random source): nothing
                // was sent, and nothing more will be drawn.
                None => {
                    drop(guard);
                    lighting.retire();
                    return Ok(Next::Wake);
                }
            }
        };
        let deadline = Instant::now() + lighting.frame_budget();
        let _hold = Hold::new(&lighting.stream_hold, deadline);
        let claim = lighting.claim();
        let written = timeout_at(deadline, port.frame(report, deadline))
            .await
            .unwrap_or(Err(Unwritten::InDoubt));
        verify_retained_failure(&self.fatal)?;
        claim.settle(matches!(written, Err(Unwritten::InDoubt)));
        if written.is_err() {
            // A frame refused before submission ends the stream as one that
            // may have been submitted does (which `settle` latched): the
            // next request draws anew.
            lighting.retire();
            return Ok(Next::Wake);
        }
        let mut owner = lighting.owner.borrow_mut();
        let Some((effect, stream)) = owner.as_mut().and_then(|owner| {
            let effect = owner.renderer.effect;
            owner.stream.as_mut().map(|stream| (effect, stream))
        }) else {
            return Ok(Next::Wake);
        };
        stream.next = effect.advance(stream.next, observed(stream), rate);
        Ok(Next::At(at_ticks(stream.started, stream.next)))
    }

    /// The stream loop: one frame at each deadline `stream_frame` returns,
    /// and otherwise waits for a new animated renderer, a cadence or a clean
    /// reading. It ends only with fatal admission evidence.
    pub(in super::super::super) async fn run_lighting_stream(&self) -> io::Result<()> {
        loop {
            match self.stream_frame(Instant::now()).await? {
                Next::At(at) => {
                    tokio::select! {
                        () = tokio::time::sleep_until(at) => {}
                        () = self.lighting.wake.notified() => {}
                    }
                }
                Next::Wake => self.lighting.wake.notified().await,
            }
        }
    }

    /// Stop's end of the stream, after the renderer ended
    /// (`retire_renderer`, whose retire already recalled every frame not yet
    /// submitted): the drain waits, for its `STREAM_SETTLE_BUDGET` share
    /// before `deadline`, for the stream to report that no report is
    /// outstanding, so no frame is issued after the keyboard release and the
    /// fan handback. A stream that cannot confirm it in time latches the
    /// doubt, and the drain goes on: only the report already submitted may
    /// still complete.
    pub(in super::super::super) async fn settle_lighting_stream(&self, deadline: Instant) {
        let Some(port) = self.lighting.stream.get().cloned() else {
            return;
        };
        // A no-op after `retire_renderer`, unless a request opened it since.
        self.lighting.close_stream();
        let share = deadline.min(Instant::now() + STREAM_SETTLE_BUDGET);
        if !matches!(timeout_at(share, port.settle(share)).await, Ok(true)) {
            self.lighting.latch();
        }
    }

    /// Composes the animated matrix stream (`Session::stream`); it may be
    /// set once.
    pub(in super::super::super) fn compose_matrix_stream(&self, stream: Rc<dyn MatrixStream>) {
        assert!(self.lighting.stream.set(stream).is_ok());
    }
}

#[cfg(test)]
impl Runtime {
    /// The animated stream's next deadline, the effect it draws and when the
    /// effect started.
    pub(in super::super::super) fn stream_due_for_tests(
        &self,
    ) -> Option<(Instant, crate::discovery::performance::Effect, Instant)> {
        let owner = self.lighting.owner.borrow();
        let owner = owner.as_ref()?;
        let stream = owner.stream.as_ref()?;
        Some((
            at_ticks(stream.started, stream.next),
            owner.renderer.effect,
            stream.started,
        ))
    }

    /// When the stream's latest clean reading was taken.
    pub(in super::super::super) fn stream_clean_for_tests(&self) -> Option<Instant> {
        let owner = self.lighting.owner.borrow();
        let stream = owner.as_ref()?.stream.as_ref()?;
        stream.clean.as_ref().map(|clean| clean.at)
    }

    /// Bounds every later frame by `budget` instead of `FRAME_BUDGET`.
    pub(in super::super::super) fn set_frame_budget_for_tests(&self, budget: Duration) {
        self.lighting.frame_budget.set(Some(budget));
    }

    /// Marks the operation gate as held by a stream frame due by `deadline`
    /// (`Hold`), as a frame in flight does.
    pub(in super::super::super) fn set_stream_hold_for_tests(&self, deadline: Option<Instant>) {
        self.lighting.stream_hold.set(deadline);
    }
}

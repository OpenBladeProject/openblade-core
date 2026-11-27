//! The renderer recipe, managed `ProductionLightingPort.SetLightingAsync`
//! for a lit effect, the Static keepalive frame (`StartStreamCore`'s idle
//! frame) and the animated stream's frame encoding (`stream_report`):
//!
//! 1. the baseline brightness, the logo when one is requested, and the 00/86
//!    layout byte once per session;
//! 2. a request for the owned renderer's effect and color keeps it and writes only the
//!    brightness and logo that differ (managed `CanRetainRenderer`);
//! 3. otherwise the brightness first, unless the keyboard wakes from dark;
//!    then the 00/84 read and the 03/0A:0500 handshake, the first frame, the
//!    brightness when waking from dark, and the logo.
//!
//! Every exchange and frame follows a fresh admission checkpoint, and the
//! matrix collection is revalidated before each frame. A setter, handshake
//! or frame that may have been submitted without confirmation stops the job
//! in doubt, with nothing replayed (stricter than managed, which attempts
//! its rollback). After a known failure (a readback of another value, a
//! device mode outside Normal and Driver, or a frame refused before it was
//! submitted) the job restores the confirmed brightness, the previous
//! renderer's frame and the logo once, as managed's rollback does; a restore
//! that is itself in doubt stops the job (managed `RollbackIndeterminate`).
use super::*;
use openblade_protocol::{
    device_mode::DeviceModeQuery,
    lighting::matrix::{
        LayoutQuery, MatrixFrame, RegionalProjection, TransitionSet, encode_feature_report,
    },
};
use std::sync::atomic::{AtomicU32, Ordering};

/// Every ID a render job may send: the baseline brightness (1) and logo (2),
/// the layout (1), the brightness setter and readback (2), the handshake
/// (2), the waking brightness (2), the logo setters and readback (4), and
/// their restores: brightness (2), handshake (2) and logo (4).
pub(super) const IDS: usize = 22;

const LAYOUT_UNREAD: &str = "Keyboard layout could not be read.";
/// Managed `ExecuteTransitionHandshakeCoreAsync`'s refusal of a device mode
/// outside Normal and Driver.
const DEVICE_MODE_UNSUPPORTED: &str = "The device-mode query 00/84 returned an unsupported mode.";
const HANDSHAKE_IN_DOUBT: &str =
    "The lighting transition handshake may have been submitted and was not confirmed.";
const FRAME_UNSENT: &str = "The keyboard matrix frame could not be sent.";
const FRAME_IN_DOUBT: &str =
    "A keyboard matrix frame may have been submitted and was not confirmed.";

/// Managed matrix transaction IDs, which cycle through 1 to 255 apart from
/// the control mailbox's pool.
fn matrix_transaction() -> u8 {
    static NEXT: AtomicU32 = AtomicU32::new(0);
    (NEXT.fetch_add(1, Ordering::Relaxed) % 255) as u8 + 1
}

/// The encoded first or keepalive frame of a Static `renderer`: the uniform
/// canvas, projected onto its layout's physical map. `None` when the
/// reviewed source maps did not load, which also refuses admission, or for
/// an animated renderer, whose frames only its animation draws.
fn frame(renderer: Renderer) -> Option<MatrixReport> {
    if renderer.effect.animated() {
        return None;
    }
    encode(&MatrixFrame::uniform(renderer.color), renderer.layout)
}

fn encode(canvas: &MatrixFrame, layout: u8) -> Option<MatrixReport> {
    let maps = super::super::lighting_layout::source_maps()?;
    let projection = RegionalProjection::new(&maps.layout(), layout);
    Some(encode_feature_report(
        matrix_transaction(),
        &projection.project(canvas),
    ))
}

/// One encoded frame of `animation` at `elapsed` ticks since its effect
/// started, projected onto `layout`'s physical map: managed
/// `RunStreamAsync`'s `RenderFrame` and `WriteMatrixCoreAsync`. `None` when
/// the effect drew nothing (a failed random source) or the source maps did
/// not load.
pub(crate) fn stream_report(
    animation: &mut Animation,
    layout: u8,
    elapsed: i64,
) -> Option<MatrixReport> {
    encode(&animation.render(elapsed)?, layout)
}

/// The first frame of a render job's `renderer`: its animation's at elapsed
/// zero, or the Static canvas.
fn first_frame(job: &LightingJob, renderer: Renderer) -> Option<MatrixReport> {
    if !renderer.effect.animated() {
        return frame(renderer);
    }
    let mut animation = job.animation.lock().ok()?;
    stream_report(animation.as_mut()?, renderer.layout, 0)
}

impl<P: Port> Reader<P, ReadAdmission> {
    /// One keepalive frame of the owned `renderer`. A frame refused before
    /// submission keeps the renderer; one that may have been submitted
    /// without completing loses it.
    pub(super) async fn keepalive(
        &mut self,
        authorize: Authorize<'_, ReadAdmission>,
        job: &LightingJob,
        renderer: Renderer,
        limit: Limit<'_>,
    ) -> Option<()> {
        let admission = self.admission;
        job.record(LightingOutcome::Refused);
        let report = frame(renderer)?;
        self.port.open_matrix(limit).ok()?;
        let mut attempt = Attempt {
            reader: self,
            completed: false,
        };
        match attempt
            .frame(authorize, admission, limit, &report, job)
            .await
        {
            Ok(()) => {
                job.set_matrix(MatrixState::Owned(renderer));
                job.record(LightingOutcome::Rendered);
                attempt.completed = true;
            }
            Err(Unwritten::Unsent) => {
                job.record(LightingOutcome::Refused);
                attempt.completed = true;
            }
            Err(Unwritten::InDoubt) => {}
        }
        Some(())
    }

    /// The Static recipe (module docs), after the intent was stored.
    pub(super) async fn render(
        &mut self,
        authorize: Authorize<'_, ReadAdmission>,
        run: &mut Run<'_>,
        target: RenderTarget,
        limit: Limit<'_>,
    ) -> Option<()> {
        let job = run.job;
        let admission = self.admission;
        // Managed opens both collections before it reads anything.
        if self.port.open_matrix(limit).is_err() {
            job.record(LightingOutcome::Unavailable);
            return Some(());
        }
        let mut attempt = Attempt {
            reader: self,
            completed: false,
        };
        job.record(LightingOutcome::Unread(BRIGHTNESS_UNREAD));
        let raw = attempt
            .brightness(authorize, admission, limit, run, false)
            .await?;
        let mut baseline = LightingReading {
            brightness: Some(raw),
            logo: None,
        };
        if target.logo.is_some() {
            job.record(LightingOutcome::Unread(LOGO_UNREAD));
            let Some(logo) = attempt.logo(authorize, admission, limit, run).await? else {
                job.record(LightingOutcome::Malformed);
                attempt.completed = true;
                return Some(());
            };
            baseline.logo = Some(logo);
        }
        let layout = match target.layout {
            Some(layout) => layout,
            None => {
                job.record(LightingOutcome::Unread(LAYOUT_UNREAD));
                let layout = attempt.layout(authorize, admission, limit, run).await?;
                job.set_layout(layout);
                layout
            }
        };
        job.observe(Some(baseline), None);
        let renderer = Renderer {
            effect: target.effect,
            color: target.color,
            layout,
        };
        let waking = raw == 0 && target.brightness != 0;
        let retained = !waking && target.previous == Some(renderer);
        let writes_logo = target.logo.is_some_and(|logo| baseline.logo != Some(logo));
        let early_brightness = !waking && raw != target.brightness;
        // The new renderer's first frame is drawn before anything is sent: an
        // animation whose random source fails refuses the job here.
        let first = if retained {
            None
        } else {
            let Some(report) = first_frame(job, renderer) else {
                job.record(LightingOutcome::Unavailable);
                attempt.completed = true;
                return Some(());
            };
            Some(report)
        };
        // Every exchange forward, the frame charged as one, and every
        // restore, each at the measured pace.
        let logo = |mode| if writes_logo { logo_exchanges(mode) } else { 0 };
        let brightness = u32::from(early_brightness || waking) * 2;
        let (forward, restore) = if retained {
            (
                brightness + logo(target.logo),
                brightness + logo(baseline.logo),
            )
        } else {
            let matrix = 3 * u32::from(target.previous.is_some());
            (
                brightness + 3 + logo(target.logo),
                brightness + matrix + logo(baseline.logo),
            )
        };
        if !paced(
            &attempt.reader.exchanges,
            forward + restore,
            Duration::ZERO,
            limit,
        ) {
            job.record(LightingOutcome::Unpaced);
            attempt.completed = true;
            return Some(());
        }
        job.record(LightingOutcome::Refused);
        let mut steps = Steps::default();
        let failure = 'forward: {
            if early_brightness {
                let in_doubt = LightingOutcome::InDoubt(BRIGHTNESS_IN_DOUBT);
                steps.brightness = true;
                if !attempt
                    .set_brightness(
                        authorize,
                        admission,
                        limit,
                        run,
                        target.brightness,
                        in_doubt,
                    )
                    .await?
                {
                    break 'forward Some(BRIGHTNESS_MISMATCH);
                }
            }
            if let Some(report) = first {
                job.record(Self::interrupted(steps));
                match attempt.handshake(authorize, admission, limit, run).await? {
                    Some(()) => steps.handshake = true,
                    None => break 'forward Some(DEVICE_MODE_UNSUPPORTED),
                }
                job.record(LightingOutcome::Interrupted);
                match attempt
                    .frame(authorize, admission, limit, &report, job)
                    .await
                {
                    Ok(()) => {
                        job.set_matrix(MatrixState::Owned(renderer));
                        job.record(LightingOutcome::Interrupted);
                    }
                    Err(Unwritten::Unsent) => break 'forward Some(FRAME_UNSENT),
                    Err(Unwritten::InDoubt) => return None,
                }
                if waking {
                    let in_doubt = LightingOutcome::InDoubt(BRIGHTNESS_IN_DOUBT);
                    steps.brightness = true;
                    if !attempt
                        .set_brightness(
                            authorize,
                            admission,
                            limit,
                            run,
                            target.brightness,
                            in_doubt,
                        )
                        .await?
                    {
                        break 'forward Some(BRIGHTNESS_MISMATCH);
                    }
                }
            }
            if writes_logo && let Some(mode) = target.logo {
                job.record(Self::interrupted(steps));
                steps.logo = true;
                let in_doubt = LightingOutcome::InDoubt(LOGO_IN_DOUBT);
                if !attempt
                    .set_logo(authorize, admission, limit, run, mode, in_doubt)
                    .await?
                {
                    break 'forward Some(LOGO_MISMATCH);
                }
            }
            None
        };
        // A retained renderer keeps its cadence: the matrix is `Kept`.
        let Some(failure) = failure else {
            job.record(LightingOutcome::Rendered);
            attempt.completed = true;
            return Some(());
        };
        let restored = attempt
            .restore(
                authorize, admission, limit, run, target, raw, baseline, steps, failure,
            )
            .await?;
        job.record(LightingOutcome::Mismatched {
            failure,
            restored: Some(restored),
        });
        attempt.completed = true;
        Some(())
    }

    /// What a refusal before the next write leaves: nothing yet, or the
    /// confirmed writes so far.
    fn interrupted(steps: Steps) -> LightingOutcome {
        if steps.brightness || steps.handshake {
            LightingOutcome::Interrupted
        } else {
            LightingOutcome::Refused
        }
    }
}

/// Which writes a render job confirmed before a known failure.
#[derive(Clone, Copy, Default)]
struct Steps {
    brightness: bool,
    handshake: bool,
    logo: bool,
}

impl<P: Port> Attempt<'_, P, ReadAdmission> {
    /// Managed `KeyboardLayoutProtocol`: the 00/86 query's layout byte.
    async fn layout(
        &mut self,
        authorize: Authorize<'_, ReadAdmission>,
        admission: ReadAdmission,
        limit: Limit<'_>,
        run: &mut Run<'_>,
    ) -> Option<u8> {
        let query = LayoutQuery::new(run.take());
        let report = self
            .exchange(authorize, admission, limit, query.request(), None)
            .await?;
        query.decode(&report).ok()
    }

    /// Managed `ExecuteTransitionHandshakeCoreAsync`: the 00/84 read, which
    /// must report Normal or Driver mode, then the 03/0A:0500 setter and its
    /// exact echo. `Some(None)` when the correlated mode is outside both,
    /// before the setter; `None` when an exchange was not confirmed, in
    /// doubt once the setter may have been sent.
    async fn handshake(
        &mut self,
        authorize: Authorize<'_, ReadAdmission>,
        admission: ReadAdmission,
        limit: Limit<'_>,
        run: &mut Run<'_>,
    ) -> Option<Option<()>> {
        let query = DeviceModeQuery::new(run.take());
        let report = self
            .exchange(authorize, admission, limit, query.request(), None)
            .await?;
        if query.decode(&report).is_err() {
            // The reply correlated, so its envelope was valid: a mode
            // outside the contract, or a malformed payload.
            return Some(None);
        }
        let set = TransitionSet::new(run.take());
        let in_doubt = LightingOutcome::InDoubt(HANDSHAKE_IN_DOUBT);
        let report = self
            .exchange(
                authorize,
                admission,
                limit,
                set.request(),
                Some((run.job, Member::Matrix, in_doubt)),
            )
            .await?;
        set.acknowledge(&report).ok()?;
        Some(Some(()))
    }

    /// One matrix report after its own checkpoint. The job is in doubt from
    /// the submission, a call the driver refused included; a report refused
    /// before it is `Unsent`.
    async fn frame(
        &mut self,
        authorize: Authorize<'_, ReadAdmission>,
        admission: ReadAdmission,
        limit: Limit<'_>,
        report: &MatrixReport,
        job: &LightingJob,
    ) -> Result<(), Unwritten> {
        self.checkpoint(authorize, admission, limit)
            .map_err(|_| Unwritten::Unsent)?;
        let before = job.outcome();
        job.sending(Member::Matrix, LightingOutcome::InDoubt(FRAME_IN_DOUBT));
        let submitted = self.reader.port.submit_matrix(report, limit);
        if let Err(Unwritten::Unsent) = submitted {
            job.unsend(before);
            return Err(Unwritten::Unsent);
        }
        // Whatever the previous renderer drew may be replaced from here.
        job.set_matrix(MatrixState::Lost);
        if submitted.is_err() {
            return Err(Unwritten::InDoubt);
        }
        loop {
            if limit.check().is_err() {
                return Err(Unwritten::InDoubt);
            }
            if let Some(completion) = self.reader.port.poll_matrix() {
                return completion.map_err(|_| Unwritten::InDoubt);
            }
            if limit.wait(POLL).await.is_err() {
                return Err(Unwritten::InDoubt);
            }
        }
    }

    /// Managed `SetLightingAsync`'s rollback after a known failure: the
    /// confirmed brightness, the previous renderer's handshake and frame when
    /// this job's handshake may have replaced it, and the logo, each once.
    /// `Some(false)` when a restore readback confirmed another value or a
    /// restore was refused before it was sent; `None` stops the job in doubt.
    #[allow(clippy::too_many_arguments)]
    async fn restore(
        &mut self,
        authorize: Authorize<'_, ReadAdmission>,
        admission: ReadAdmission,
        limit: Limit<'_>,
        run: &mut Run<'_>,
        target: RenderTarget,
        raw: u8,
        baseline: LightingReading,
        steps: Steps,
        failure: &'static str,
    ) -> Option<bool> {
        let job = run.job;
        let unrestored = LightingOutcome::Mismatched {
            failure,
            restored: Some(false),
        };
        let in_doubt = LightingOutcome::Mismatched {
            failure,
            restored: None,
        };
        let mut restored = true;
        if steps.brightness {
            job.record(unrestored);
            let sent = job.setters_sent();
            let result = self
                .set_brightness(authorize, admission, limit, run, raw, in_doubt)
                .await;
            restored &= restore_confirmed(result, job.sent_since(sent))?;
        }
        if steps.handshake {
            match target.previous {
                // An animated renderer's phase cannot be drawn again here:
                // the restore leaves no owned renderer (stricter than
                // managed, which restarts the previous effect).
                Some(previous) if previous.effect.animated() => {
                    job.set_matrix(MatrixState::Lost);
                    restored = false;
                }
                Some(previous) => {
                    let report = frame(previous)?;
                    job.record(unrestored);
                    let sent = job.setters_sent();
                    match self.handshake(authorize, admission, limit, run).await {
                        Some(Some(())) => {
                            match self.frame(authorize, admission, limit, &report, job).await {
                                Ok(()) => job.set_matrix(MatrixState::Owned(previous)),
                                Err(Unwritten::Unsent) => restored = false,
                                Err(Unwritten::InDoubt) => {
                                    job.record(in_doubt);
                                    return None;
                                }
                            }
                        }
                        Some(None) => restored = false,
                        None if !job.sent_since(sent) => restored = false,
                        None => {
                            job.record(in_doubt);
                            return None;
                        }
                    }
                }
                // No renderer drew the matrix before this job.
                None => job.set_matrix(MatrixState::Lost),
            }
        }
        if steps.logo
            && let Some(mode) = baseline.logo
        {
            job.record(unrestored);
            let sent = job.setters_sent();
            let result = self
                .set_logo(authorize, admission, limit, run, mode, in_doubt)
                .await;
            restored &= restore_confirmed(result, job.sent_since(sent))?;
        }
        Some(restored)
    }
}

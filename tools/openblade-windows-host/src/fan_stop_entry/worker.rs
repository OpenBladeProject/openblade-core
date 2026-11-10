//! Worker-backed inputs for the manual Stop fixture. No acquisition or dispatch
//! is performed by the replay.

use std::{future::Future, ptr};
use tokio::time::Instant;

use super::*;
use crate::cpu_worker::{Readiness, Sample};

/// One scripted activation. Its monotonic origin is also the origin for the
/// scripted context, safety and GPU spans supplied to the replay.
pub struct WorkerAttempt {
    run: u64,
    origin: Instant,
    ready: Option<(Readiness, Instant)>,
}

impl WorkerAttempt {
    pub fn new(run: u64) -> Self {
        Self {
            run,
            origin: Instant::now(),
            ready: None,
        }
    }

    pub fn elapsed(&self) -> Duration {
        self.origin.elapsed()
    }

    /// Ready establishes a generation but contributes no temperature.
    pub async fn prepare(&mut self, readiness: impl Future<Output = Option<Readiness>>) {
        self.ready = None;
        self.ready = readiness.await.map(|ready| (ready, Instant::now()));
    }

    /// Bind the request to the attempt and to the volatile observation that
    /// preceded it. An old completion cannot be registered as a new request.
    pub async fn sample(
        &self,
        volatile: VolatileEvidence,
        request: impl Future<Output = Option<Sample>>,
    ) -> Option<AttemptSample<'_>> {
        let (ready, _) = self.ready.as_ref()?;
        let started = Instant::now();
        if !ready.is_active() || !volatile.capture.fresh(self.run, self.elapsed(), MAX_AGE) {
            return None;
        }
        let sample = request.await?;
        let now = Instant::now();
        sample.for_entry(ready, started, now, now, MAX_AGE)?;
        Some(AttemptSample {
            attempt: self,
            volatile: volatile.capture,
            sample,
        })
    }

    fn instant(&self, offset: Duration) -> Option<Instant> {
        self.origin.checked_add(offset)
    }
}

pub struct AttemptSample<'a> {
    attempt: &'a WorkerAttempt,
    volatile: Span,
    sample: Sample,
}

#[derive(Clone, Copy, Debug)]
pub struct GpuRead {
    pub capture: Span,
    pub state: GpuState,
}

#[derive(Clone, Copy, Debug)]
pub enum GpuState {
    Unknown,
    Active {
        celsius: Option<i32>,
    },
    Inactive {
        celsius: Option<i32>,
    },
    PoweredDown {
        inactive: bool,
        celsius: Option<i32>,
    },
}

#[derive(Clone, Copy)]
pub struct WorkerThermal<'a> {
    pub cpu: Option<&'a AttemptSample<'a>>,
    pub gpu: Option<GpuRead>,
    pub canceled: bool,
}

/// Use the same ordered context and thermal gates as the scripted replay,
/// with readiness and CPU values coming only from the typed worker.
pub fn replay_worker(
    entry: &ManualEntry<'_, WorkerThermal<'_>, &WorkerAttempt>,
) -> Result<(), Refusal> {
    let attempt = entry.cpu_reader_ready;
    let prepared = attempt.run == entry.binding.run
        && attempt.ready.as_ref().is_some_and(|(_, ready_at)| {
            attempt
                .instant(entry.initial_check_at)
                .is_some_and(|at| *ready_at <= at)
        });
    replay_core(entry, prepared, |observation, at, policy| {
        attempt.assess(observation, at, policy)
    })
}

impl WorkerAttempt {
    fn assess(
        &self,
        observation: SafetyObservation<WorkerThermal<'_>>,
        at: Duration,
        policy: ThermalPolicy,
    ) -> Result<(), SafetyRefusal> {
        let unavailable = SafetyRefusal::NotObserved(ThermalNotObserved::Unavailable);
        let SafetyObservation {
            capture,
            volatile,
            thermal,
        } = observation;
        let (ready, ready_at) = self.ready.as_ref().ok_or(unavailable)?;
        let cpu = thermal
            .cpu
            .filter(|cpu| ptr::eq(cpu.attempt, self) && cpu.volatile == volatile.capture)
            .ok_or(unavailable)?;
        let start = self
            .instant(volatile.capture.completed)
            .ok_or(unavailable)?
            .max(*ready_at);
        let completed = self.instant(capture.completed).ok_or(unavailable)?;
        let at = self.instant(at).ok_or(unavailable)?;
        let (celsius, requested_at) = cpu
            .sample
            .for_entry(ready, start, completed, at, MAX_AGE)
            .ok_or(unavailable)?;

        let gpu = thermal.gpu.filter(|gpu| {
            gpu.capture.started >= volatile.capture.completed
                && gpu.capture.fresh(self.run, capture.completed, MAX_AGE)
        });
        let (gpu, gpu_inactive, gpu_powered_down) = match gpu.map(|read| read.state) {
            Some(GpuState::Active { celsius }) => (celsius, false, false),
            Some(GpuState::Inactive { celsius }) => (celsius, true, false),
            Some(GpuState::PoweredDown { inactive, celsius }) => (celsius, inactive, true),
            Some(GpuState::Unknown) | None => (None, false, false),
        };
        assess_thermal(
            policy,
            Readings {
                cpu: Some(celsius.into()),
                gpu,
                gpu_inactive,
                gpu_powered_down,
            },
            completed.checked_duration_since(requested_at),
            thermal.canceled,
        )
        .map_err(SafetyRefusal::Thermal)
    }
}

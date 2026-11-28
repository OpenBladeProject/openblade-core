//! Verified Service status to the hook's single expiring observation.
//!
//! Managed `ServiceReactiveLightingLoop.ObserveStatus` keeps Driver-mode
//! knowledge as Unknown, Inactive or Active, and only Active lets the hook
//! intercept a new press. Here every verified, fresh status replaces the
//! projection, and a reconnect, a stale or unverifiable status and stop make it
//! unknown at once. The hook reads one packed atomic and never takes this lock.
use std::{
    io,
    sync::{Mutex, MutexGuard},
    time::{Duration, Instant},
};

use openblade_domain::function_row::{
    Backend, Device, Lifecycle, Observation, PrimaryMode, Status,
};
use openblade_platform_windows::function_row_hook::HookControl;

use crate::{service_status::Snapshot, session_native::service_client::StatusUpdate};

/// The one model this role is reviewed for. RZ09-0528 and RZ09-0484 have their
/// own function-row inputs (Function-P, host-managed Fn) and are refused.
pub(crate) const ADMITTED_MODEL: &str = "RZ09-0581";

/// How long one renewal keeps the projection admitted. The renewal leases the
/// open subscription, not the status's age: the Service sends no heartbeat.
/// The idle subscription checks its Service generation and pipe peer every
/// 500 ms; a failed read reconnects and a changed identity ends the watcher,
/// and either invalidates the admission. A renewal that stops (a stalled
/// runtime) lets the admission expire one lease after the last renewal, or
/// up to two seconds after a status arrived (its `fresh_until`), whichever
/// is later.
pub(crate) const LEASE: Duration = Duration::from_secs(1);

/// Managed `KeyboardDriverModeKnowledge`.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum DriverMode {
    Unknown,
    Inactive,
    Active,
}

/// What one verified status says about the function row.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) struct Projection {
    pub(crate) device: Option<Device>,
    pub(crate) driver_mode: DriverMode,
    pub(crate) primary: Option<PrimaryMode>,
}

impl Projection {
    pub(crate) fn of(snapshot: &Snapshot) -> Self {
        let device =
            (snapshot.verified_model_number() == Some(ADMITTED_MODEL)).then_some(Device::Rz09_0581);
        Self {
            device,
            driver_mode: match snapshot.keyboard_driver_mode_active() {
                Some(true) => DriverMode::Active,
                Some(false) => DriverMode::Inactive,
                None => DriverMode::Unknown,
            },
            // Managed falls back to the active profile's laptop behavior when
            // the Service omits FunctionKeyPrimary. That fallback is not ported:
            // an absent value stays unknown, which passes keys through.
            primary: snapshot.function_key_primary(),
        }
    }

    pub(crate) fn observation(
        self,
        backend: Option<Backend>,
        fn_pressed: Option<bool>,
    ) -> Observation {
        Observation {
            exact_device: self.device,
            selected_backend: backend,
            status: match self.driver_mode {
                DriverMode::Active => Status::FirmwareDriverMode { active: true },
                DriverMode::Inactive => Status::FirmwareDriverMode { active: false },
                DriverMode::Unknown => Status::Unknown,
            },
            fn_pressed,
            primary_mode: self.primary,
            lifecycle: Lifecycle::Active,
        }
    }
}

/// Where the admission goes: the running hook's control in production.
pub(crate) trait Sink: Send {
    fn publish(&self, observation: Observation, valid_until: Instant);
    fn invalidate(&self);
}

impl Sink for HookControl {
    fn publish(&self, observation: Observation, valid_until: Instant) {
        HookControl::publish(self, observation, valid_until);
    }

    fn invalidate(&self) {
        HookControl::invalidate(self);
    }
}

#[derive(Default)]
struct State {
    status: Option<Projection>,
    valid_until: Option<Instant>,
    /// The effective backend after the preference and ownership guard.
    backend: Option<Backend>,
    /// Managed reads Fn from the Razer HID report through Raw Input
    /// (`FunctionKeyObservation`). That observer is not ported, so Fn stays
    /// unknown and the policy admits no press in this stage.
    fn_pressed: Option<bool>,
    sink: Option<Box<dyn Sink>>,
}

impl State {
    fn push(&self) {
        let Some(sink) = &self.sink else {
            return;
        };
        match (self.status, self.valid_until) {
            (Some(status), Some(until)) => {
                sink.publish(status.observation(self.backend, self.fn_pressed), until);
            }
            _ => sink.invalidate(),
        }
    }
}

/// Shared by the status watcher, the renewal tick and the supervisor.
#[derive(Default)]
pub(crate) struct Admission(Mutex<State>);

impl Admission {
    fn state(&self) -> MutexGuard<'_, State> {
        self.0.lock().unwrap_or_else(|error| error.into_inner())
    }

    /// A verified status, admitted until it stops being fresh.
    pub(crate) fn observe(&self, projection: Projection, fresh_until: Instant) {
        let mut state = self.state();
        state.status = Some(projection);
        state.valid_until = Some(fresh_until);
        state.push();
    }

    /// Nothing is known: every key passes through.
    pub(crate) fn invalidate(&self) {
        let mut state = self.state();
        state.status = None;
        state.valid_until = None;
        state.push();
    }

    /// The subscription is still open: extend the lease from `now`.
    pub(crate) fn renew(&self, now: Instant) {
        let mut state = self.state();
        if state.status.is_none() {
            return;
        }
        let lease = now + LEASE;
        if state.valid_until.is_none_or(|until| until < lease) {
            state.valid_until = Some(lease);
            state.push();
        }
    }

    pub(crate) fn select(&self, backend: Option<Backend>) {
        let mut state = self.state();
        state.backend = backend;
        state.push();
    }

    /// Hand the admission to a new hook before it reads its startup snapshot.
    pub(crate) fn attach(&self, sink: Box<dyn Sink>) {
        let mut state = self.state();
        state.sink = Some(sink);
        state.push();
    }

    pub(crate) fn detach(&self) {
        let mut state = self.state();
        if let Some(sink) = state.sink.take() {
            sink.invalidate();
        }
    }

    /// A hook may start: the latest status names the admitted laptop and is
    /// still admitted. Driver mode is not required; the hook passes keys
    /// through until it is confirmed.
    pub(crate) fn admits_install(&self, now: Instant) -> bool {
        let state = self.state();
        state
            .status
            .is_some_and(|status| status.device == Some(Device::Rz09_0581))
            && state.valid_until.is_some_and(|until| now < until)
    }

    #[cfg(test)]
    pub(crate) fn observe_fn(&self, pressed: Option<bool>) {
        let mut state = self.state();
        state.fn_pressed = pressed;
        state.push();
    }
}

/// Hand a status update to the admission. A failed origin check or a stale
/// status invalidates it before the error ends the watcher, as does a
/// reconnect: a new Service generation must prove itself again.
pub(crate) fn publish_status(
    admission: &Admission,
    update: &StatusUpdate,
    verify: impl Fn() -> io::Result<()>,
) -> io::Result<()> {
    let result = (|| match update {
        StatusUpdate::Opened(initial) => {
            verify()?;
            let until = initial.fresh_until()?;
            admission.observe(Projection::of(initial.snapshot()), until);
            Ok(())
        }
        StatusUpdate::Event(observation) => {
            verify()?;
            let until = observation.fresh_until()?;
            admission.observe(Projection::of(&observation.snapshot), until);
            Ok(())
        }
        StatusUpdate::Reconnecting => {
            admission.invalidate();
            Ok(())
        }
    })();
    if result.is_err() {
        admission.invalidate();
    }
    result
}

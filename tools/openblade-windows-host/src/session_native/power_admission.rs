//! Confirmed power context retained from authenticated Service status.
//!
//! Mirrors the managed `DisplayPowerContextState` generation rules used for
//! display admission: a changed confirmed source, ExternalUnknown, a lost
//! subscription and every newly opened subscription start a new generation.
//! An ExternalUnknown context also carries managed `ManualExternalPowerPresent`
//! (fresh Service external-power evidence on a verified, Active device); a
//! change in that flag starts a new generation too. Retirement is permanent.
//! Nothing here reads power from another source.
use std::sync::{Arc, Mutex};

/// Managed `PowerSource.ExternalUnknown`.
pub(crate) const EXTERNAL_UNKNOWN: u8 = 3;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) struct PowerContext {
    pub(crate) source: u8,
    pub(crate) generation: u64,
}

impl PowerContext {
    /// A manual-external context, which only the manual setters admit.
    pub(crate) fn manual_external(self) -> bool {
        self.source == EXTERNAL_UNKNOWN
    }
}

#[derive(Default)]
struct State {
    source: Option<u8>,
    /// Managed `ManualExternalPowerPresent`; set only while `source` is None.
    manual_external: bool,
    generation: u64,
    retired: bool,
}

impl State {
    /// Managed `MarkUnavailableCore(manual)`: a new generation when the context
    /// was confirmed or its manual-external flag changes.
    fn unconfirmed(&mut self, manual: bool) {
        if self.source.take().is_some() || self.manual_external != manual {
            self.generation += 1;
        }
        self.manual_external = manual;
    }
}

#[derive(Clone, Default)]
pub(crate) struct PowerAdmission(Arc<Mutex<State>>);

fn confirmed(active: i32) -> Option<u8> {
    // Managed PowerSource: PluggedIn=0, Battery=1, UsbC=2, ExternalUnknown=3.
    matches!(active, 0..=2).then_some(active as u8)
}

impl PowerAdmission {
    fn update(&self, change: impl FnOnce(&mut State)) {
        // A poisoned lock cannot be trusted for admission: retire instead.
        match self.0.lock() {
            Ok(mut state) if !state.retired => change(&mut state),
            Ok(_) => {}
            Err(poisoned) => {
                let mut state = poisoned.into_inner();
                state.retired = true;
                state.source = None;
                state.manual_external = false;
            }
        }
    }

    /// A verified fresh SubscribeStatus response. Reconnection is never a
    /// continuation of an earlier admission, even with the same source.
    /// `manual` is the response's manual-external evidence; it applies only to
    /// an unconfirmed source.
    pub(crate) fn opened(&self, active: i32, manual: bool) {
        self.update(|state| {
            state.generation += 1;
            state.source = confirmed(active);
            state.manual_external = state.source.is_none() && manual;
        });
    }

    /// A verified fresh event on the current subscription.
    pub(crate) fn observed(&self, active: i32, manual: bool) {
        self.update(|state| match confirmed(active) {
            None => state.unconfirmed(manual),
            source if state.source != source => {
                state.generation += 1;
                state.source = source;
                state.manual_external = false;
            }
            _ => {}
        });
    }

    /// The subscription is reconnecting or failed its freshness check
    /// (managed `MarkUnavailable()`, which clears manual-external evidence).
    pub(crate) fn unavailable(&self) {
        self.update(|state| state.unconfirmed(false));
    }

    /// The status watcher or Agent stop ended this lifetime.
    pub(crate) fn retire(&self) {
        let mut state = self
            .0
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        state.retired = true;
        state.source = None;
        state.manual_external = false;
        state.generation += 1;
    }

    /// Managed `DisplayPowerContextState.Update(source, NextGeneration())` from
    /// display automation: a new generation for the current source, so every
    /// operation admitted on the older one becomes stale. Returns the new
    /// confirmed context, if any.
    #[cfg(feature = "agent-display-automation-v1")]
    pub(crate) fn advance(&self) -> Option<PowerContext> {
        self.update(|state| state.generation += 1);
        self.capture()
    }

    /// The confirmed context only: automation and profile apply.
    #[cfg(any(test, feature = "agent-display-profile-apply-v1"))]
    pub(crate) fn capture(&self) -> Option<PowerContext> {
        let state = self.0.lock().ok()?;
        if state.retired {
            return None;
        }
        Some(PowerContext {
            source: state.source?,
            generation: state.generation,
        })
    }

    /// The context a manual setter may admit: the confirmed one, or the
    /// ExternalUnknown context with manual-external evidence.
    pub(crate) fn capture_manual(&self) -> Option<PowerContext> {
        let state = self.0.lock().ok()?;
        if state.retired {
            return None;
        }
        let source = match state.source {
            Some(source) => source,
            None if state.manual_external => EXTERNAL_UNKNOWN,
            None => return None,
        };
        Some(PowerContext {
            source,
            generation: state.generation,
        })
    }

    /// Managed `EnsureCurrent`: the same source and generation, either
    /// confirmed or still manual-external. A confirmed context never matches
    /// a manual-external one, because a change between them starts a new
    /// generation.
    pub(crate) fn current(&self, expected: PowerContext) -> bool {
        self.capture_manual() == Some(expected)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn generations_follow_managed_display_context_rules() {
        let power = PowerAdmission::default();
        assert_eq!(power.capture(), None);
        power.opened(1, false);
        let battery = power.capture().unwrap();
        assert_eq!(battery.source, 1);
        // Same-source events on one subscription keep admission.
        power.observed(1, false);
        assert!(power.current(battery));
        // A changed source, then a return, never revives the old generation.
        power.observed(0, false);
        let plugged = power.capture().unwrap();
        assert_ne!(plugged.generation, battery.generation);
        power.observed(1, false);
        assert!(!power.current(battery));
        let battery = power.capture().unwrap();
        // ExternalUnknown and reconnecting both clear the confirmed source.
        power.observed(3, false);
        assert_eq!(power.capture(), None);
        assert_eq!(power.capture_manual(), None);
        power.observed(1, false);
        assert!(!power.current(battery));
        let battery = power.capture().unwrap();
        power.unavailable();
        assert_eq!(power.capture(), None);
        // A new subscription with the same source is a new generation.
        power.opened(1, false);
        assert!(!power.current(battery));
        let reopened = power.capture().unwrap();
        power.opened(1, false);
        assert!(!power.current(reopened));
        // Undefined/unknown initial status is unconfirmed.
        power.opened(3, false);
        assert_eq!(power.capture(), None);
        power.opened(7, false);
        assert_eq!(power.capture(), None);
        power.opened(2, false);
        let usb = power.capture().unwrap();
        assert_eq!(usb.source, 2);
        power.retire();
        assert_eq!(power.capture(), None);
        power.opened(2, false);
        power.observed(2, false);
        assert_eq!(power.capture(), None, "retirement is permanent");
    }

    #[test]
    fn manual_external_follows_managed_mark_unavailable_core() {
        let power = PowerAdmission::default();
        // Manual evidence on a confirmed source is ignored.
        power.opened(1, true);
        let battery = power.capture_manual().unwrap();
        assert_eq!(battery.source, 1);
        assert_eq!(power.capture(), Some(battery));
        // Confirmed to manual-external: a new generation, admitted only manually.
        power.observed(3, true);
        assert_eq!(power.capture(), None);
        let manual = power.capture_manual().unwrap();
        assert!(manual.manual_external());
        assert_ne!(manual.generation, battery.generation);
        assert!(!power.current(battery));
        // Repeated identical evidence keeps the generation.
        power.observed(3, true);
        assert!(power.current(manual));
        // Losing the evidence starts a new, unadmitted generation.
        power.observed(3, false);
        assert_eq!(power.capture_manual(), None);
        assert!(!power.current(manual));
        // Regaining it is another generation, never the old one.
        power.observed(3, true);
        let regained = power.capture_manual().unwrap();
        assert_ne!(regained.generation, manual.generation);
        // A confirmed source supersedes it and clears the flag.
        power.observed(0, true);
        assert_eq!(power.capture_manual().unwrap().source, 0);
        assert!(!power.current(regained));
        power.observed(3, true);
        let manual = power.capture_manual().unwrap();
        // Reconnecting clears the evidence (managed MarkUnavailable()).
        power.unavailable();
        assert_eq!(power.capture_manual(), None);
        assert!(!power.current(manual));
        // A freshly opened subscription carries its own evidence.
        power.opened(3, true);
        let opened = power.capture_manual().unwrap();
        assert_ne!(opened.generation, manual.generation);
        power.retire();
        assert_eq!(power.capture_manual(), None);
        power.opened(3, true);
        assert_eq!(power.capture_manual(), None, "retirement is permanent");
    }
}

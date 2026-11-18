//! Confirmed power context retained from authenticated Service status.
//!
//! Mirrors the managed `DisplayPowerContextState` generation rules used for
//! display admission: a changed confirmed source, ExternalUnknown, a lost
//! subscription and every newly opened subscription start a new generation.
//! Retirement is permanent. Nothing here reads power from another source.
use std::sync::{Arc, Mutex};

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) struct PowerContext {
    pub(crate) source: u8,
    pub(crate) generation: u64,
}

#[derive(Default)]
struct State {
    source: Option<u8>,
    generation: u64,
    retired: bool,
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
            }
        }
    }

    /// A verified fresh SubscribeStatus response. Reconnection is never a
    /// continuation of an earlier admission, even with the same source.
    pub(crate) fn opened(&self, active: i32) {
        self.update(|state| {
            state.generation += 1;
            state.source = confirmed(active);
        });
    }

    /// A verified fresh event on the current subscription.
    pub(crate) fn observed(&self, active: i32) {
        self.update(|state| {
            let source = confirmed(active);
            if source.is_none() || state.source != source {
                if state.source.is_some() || source.is_some() {
                    state.generation += 1;
                }
                state.source = source;
            }
        });
    }

    /// The subscription is reconnecting or failed its freshness check.
    pub(crate) fn unavailable(&self) {
        self.update(|state| {
            if state.source.take().is_some() {
                state.generation += 1;
            }
        });
    }

    /// The status watcher or Agent stop ended this lifetime.
    pub(crate) fn retire(&self) {
        let mut state = self
            .0
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        state.retired = true;
        state.source = None;
        state.generation += 1;
    }

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

    pub(crate) fn current(&self, expected: PowerContext) -> bool {
        self.capture() == Some(expected)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn generations_follow_managed_display_context_rules() {
        let power = PowerAdmission::default();
        assert_eq!(power.capture(), None);
        power.opened(1);
        let battery = power.capture().unwrap();
        assert_eq!(battery.source, 1);
        // Same-source events on one subscription keep admission.
        power.observed(1);
        assert!(power.current(battery));
        // A changed source, then a return, never revives the old generation.
        power.observed(0);
        let plugged = power.capture().unwrap();
        assert_ne!(plugged.generation, battery.generation);
        power.observed(1);
        assert!(!power.current(battery));
        let battery = power.capture().unwrap();
        // ExternalUnknown and reconnecting both clear the confirmed source.
        power.observed(3);
        assert_eq!(power.capture(), None);
        power.observed(1);
        assert!(!power.current(battery));
        let battery = power.capture().unwrap();
        power.unavailable();
        assert_eq!(power.capture(), None);
        // A new subscription with the same source is a new generation.
        power.opened(1);
        assert!(!power.current(battery));
        let reopened = power.capture().unwrap();
        power.opened(1);
        assert!(!power.current(reopened));
        // Undefined/unknown initial status is unconfirmed.
        power.opened(3);
        assert_eq!(power.capture(), None);
        power.opened(7);
        assert_eq!(power.capture(), None);
        power.opened(2);
        let usb = power.capture().unwrap();
        assert_eq!(usb.source, 2);
        power.retire();
        assert_eq!(power.capture(), None);
        power.opened(2);
        power.observed(2);
        assert_eq!(power.capture(), None, "retirement is permanent");
    }
}

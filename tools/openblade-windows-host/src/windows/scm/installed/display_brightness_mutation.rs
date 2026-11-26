//! Installed `setDisplayBrightnessHidUsage` on exact RZ09-0581: managed
//! `ServiceControlRuntime.SetDisplayBrightnessHidUsageAsync`
//! (src/OpenBlade.Service/ServiceControlRuntime.cs:3409-3510) under the
//! thermal operation gate, with managed's Stop release (13384-13387).
//!
//! Admission is the request runtime's own: the caller must control the
//! active interactive session, retained fatal evidence refuses, and a press
//! takes a fresh Write capture (exact device, session, power and competing-
//! controller admission at the current lifecycle epoch) at managed's device
//! check and asks it again just before the IOCTL. The package and device
//! binding is the transport's, before every write. A release needs no device
//! admission, as managed's does not.
//!
//! Managed persists nothing for this input, and neither does native: the
//! held usage lives in memory, and a restarted Service neutralizes before
//! its first press. Deviations, each fail-safe: there is no maintenance-tick
//! neutralization and no low-power release (a usage held across a suspend is
//! released by the next request or by Stop), and the published availability
//! is the lane's last resolution rather than one per status.

use super::*;

#[path = "display_brightness_hid.rs"]
pub(in super::super) mod hid;
#[cfg(not(test))]
#[path = "display_brightness_hid_windows.rs"]
mod windows;

use hid::{Held, Lane};

/// What Stop gives the release, out of the drain's deadline. One cached
/// IOCTL with its validation; a held usage is rare at Stop.
pub(in super::super) const SHUTDOWN_BUDGET: std::time::Duration =
    std::time::Duration::from_millis(300);

/// The one model whose stack the lane resolves.
pub(in super::super) const MODEL: &str = "RZ09-0581";

/// The lane and managed's held-usage fields.
#[derive(Default)]
pub(in super::super) struct Brightness {
    lane: std::cell::OnceCell<Arc<Lane>>,
    held: RefCell<Held>,
}

impl Brightness {
    #[cfg(not(test))]
    pub(in super::super) fn production() -> Self {
        let brightness = Self::default();
        if let Some(lane) = production_lane() {
            let _ = brightness.lane.set(lane);
        }
        brightness
    }
}

/// The process's one lane over the Windows transport, shared by the request
/// runtime and the status observer. A thread that cannot start leaves every
/// request refused as unavailable and the availability Unknown.
#[cfg(not(test))]
pub(in super::super) fn production_lane() -> Option<Arc<Lane>> {
    static LANE: OnceLock<Option<Arc<Lane>>> = OnceLock::new();
    LANE.get_or_init(|| {
        Lane::spawn(|| hid::Transport::new(windows::Windows))
            .ok()
            .map(Arc::new)
    })
    .clone()
}

/// Managed `displayBrightnessHidAvailability` for the host the status
/// observer identified: the lane's last resolution on the model whose stack
/// it resolves, Unavailable on any other identified host (native admits no
/// other stack), and Unknown while the host is unidentified.
pub(in super::super) fn availability(model: Option<Option<&str>>, lane: Option<&Lane>) -> u8 {
    match model {
        Some(Some(model)) if model == MODEL => {
            lane.map_or(hid::AVAILABILITY_UNKNOWN, Lane::availability)
        }
        Some(_) => hid::AVAILABILITY_UNAVAILABLE,
        None => hid::AVAILABILITY_UNKNOWN,
    }
}

fn controls_session(caller: &Caller) -> bool {
    caller.logon == Logon::Interactive && caller.control_session == Some(caller.session)
}

impl Runtime {
    /// Composes the lane (`Runtime::brightness`); it may be set once.
    #[cfg(test)]
    pub(in super::super) fn compose_brightness(&self, lane: Arc<Lane>) {
        assert!(self.brightness.lane.set(lane).is_ok());
    }

    /// The lane, for the status observer.
    pub(in super::super) fn brightness_lane(&self) -> Option<Arc<Lane>> {
        self.brightness.lane.get().cloned()
    }

    /// Managed `SetDisplayBrightnessHidUsageAsync`; `usage` is defined
    /// (`StatusRequest::validate`).
    pub(in super::super) async fn set_display_brightness_hid_usage(
        &self,
        mailbox: MailboxSource,
        caller: Caller,
        sid: String,
        usage: u16,
        deadline: Instant,
    ) -> io::Result<Outcome> {
        #[cfg(test)]
        self.entered.set(self.entered.get() + 1);
        if !controls_session(&caller) {
            return Ok(Outcome::blocked(
                "The active interactive Windows user is required.",
            ));
        }
        // One mutation at a time, as managed's operation gate allows.
        let _serial = self.gate.lock().await;
        verify_retained_failure(&self.fatal)?;
        let lane = self.brightness.lane.get().map(Arc::as_ref);
        // A fatal admission is retained, as every other mutation retains it,
        // so later requests refuse before reaching the lane.
        let admit = || -> io::Result<Option<Box<dyn Fn() -> bool>>> {
            let MailboxSource::Reader(reader) = &mailbox else {
                return Ok(None);
            };
            reader
                .custody()
                .verify_integrity()
                .inspect_err(|error| remember_failure(&self.fatal, error))?;
            match (self.factory)(&sid, caller.session, reader.custody(), Capture::Write) {
                Ok((_, fresh)) if fresh.authorize().is_some() => {
                    Ok(Some(Box::new(move || fresh.authorize().is_some())))
                }
                Ok(_) => Ok(None),
                Err(error) if is_fatal_admission(&error) => {
                    remember_failure(&self.fatal, &error);
                    Err(error)
                }
                Err(_) => Ok(None),
            }
        };
        let outcome = hid::set_usage(&self.brightness.held, lane, usage, deadline, admit).await?;
        verify_retained_failure(&self.fatal)?;
        Ok(outcome)
    }

    /// Managed's shutdown release (`PrepareForShutdownAsync`): a held usage
    /// is released once, within `SHUTDOWN_BUDGET`, after any request still
    /// holding the gate. The reply is a diagnostic: nothing it does fails or
    /// delays the rest of Stop past its budget.
    pub(in super::super) async fn release_display_brightness(
        &self,
        drain: Instant,
    ) -> Option<Outcome> {
        let deadline = (Instant::now() + SHUTDOWN_BUDGET).min(drain);
        let _serial = tokio::time::timeout_at(deadline, self.gate.lock())
            .await
            .ok()?;
        if !self.brightness.held.borrow().release_required {
            return None;
        }
        let lane = self.brightness.lane.get().map(Arc::as_ref);
        Some(hid::release(&self.brightness.held, lane, deadline).await)
    }
}

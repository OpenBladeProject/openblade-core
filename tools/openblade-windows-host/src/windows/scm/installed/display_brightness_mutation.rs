//! Installed `setDisplayBrightnessHidUsage` on exact RZ09-0581: managed
//! `ServiceControlRuntime.SetDisplayBrightnessHidUsageAsync`
//! (src/OpenBlade.Service/ServiceControlRuntime.cs:3409-3510) under the
//! thermal operation gate, with managed's releases at Stop (13384-13387), at
//! a confirmed suspend (13790-13793) and on the Driver-mode maintenance step
//! (13032-13043).
//!
//! Admission is the request runtime's own: the caller must control the
//! active interactive session, retained fatal evidence refuses, and a press
//! takes a fresh device capture (exact device, session, power and
//! competing-controller admission at the current lifecycle epoch) at
//! managed's device check and asks it again just before the IOCTL. That
//! capture needs no profile store, as managed's device check needs none. The
//! package and device binding is the transport's, before every write. A
//! release needs no device admission, as managed's does not.
//!
//! Managed persists nothing for this input, and neither does native: the
//! held usage lives in memory, and a restarted Service neutralizes before
//! its first press, or on the first maintenance step that finds the device
//! verified. The suspend release and the maintenance step are the keyboard
//! Driver-mode owner's (`service-keyboard-driver-mode-v1`); without it a
//! usage held across a suspend is released by the next request or by Stop.
//! The published availability is the lane's last resolution rather than one
//! per status: a resolution is a Windows call the status observer must not
//! wait on, so a changed availability reaches the status published after it
//! resolves.

use super::*;

#[path = "display_brightness_hid.rs"]
pub(in super::super) mod hid;
#[cfg(not(test))]
#[path = "display_brightness_hid_windows.rs"]
mod windows;

use hid::{Held, Lane, Usage};

/// What a cleanup boundary gives the release: Stop out of the drain's
/// deadline (`readiness::SOURCE_DRAIN_TIMEOUT`), a confirmed suspend out of
/// its window, before the keyboard's share. One cached IOCTL with its
/// validation; a usage held then is rare.
pub(in super::super) const SHUTDOWN_BUDGET: std::time::Duration =
    std::time::Duration::from_millis(300);

/// The one model whose stack the lane resolves.
pub(in super::super) const MODEL: &str = "RZ09-0581";

/// Native only: why a request still waiting when a cleanup boundary started
/// stopped waiting. A press it may have submitted stays held, and the
/// boundary's release runs after it on the lane.
pub(in super::super) const INTERRUPTED: &str = "the laptop entered low power or is stopping";

/// Managed's maintenance summary for a release that failed.
#[cfg(feature = "service-keyboard-driver-mode-v1")]
const NEUTRALIZATION_FAILED: &str = "Stale native display-brightness input could not be released";

/// A fresh device capture for one press: managed's device check, without a
/// profile store (`Runtime::admit_display_brightness`).
pub(in super::super) type Guard =
    dyn Fn(&str, u32, Arc<dyn ReadCustody>) -> io::Result<Arc<dyn Fresh>>;

/// The lane, managed's held-usage fields and the press admission.
#[derive(Default)]
pub(in super::super) struct Brightness {
    lane: std::cell::OnceCell<Arc<Lane>>,
    held: RefCell<Held>,
    guard: std::cell::OnceCell<Box<Guard>>,
    // Ends the wait of every request when a cleanup boundary starts.
    boundary: tokio::sync::Notify,
    // When the maintenance step may next try a pending neutralization.
    #[cfg(feature = "service-keyboard-driver-mode-v1")]
    neutralize_due: std::cell::Cell<Option<Instant>>,
}

impl Brightness {
    /// The production lane, which records into `log`, and the native device
    /// capture.
    #[cfg(not(test))]
    pub(in super::super) fn production(log: Arc<hid::Log>, guard: Box<Guard>) -> Self {
        let brightness = Self::default();
        if let Some(lane) = production_lane(log) {
            let _ = brightness.lane.set(lane);
        }
        let _ = brightness.guard.set(guard);
        brightness
    }
}

/// The process's one lane over the Windows transport, shared by the request
/// runtime and the status observer. A thread that cannot start leaves every
/// request refused as unavailable and the availability Unknown.
#[cfg(not(test))]
fn production_lane(log: Arc<hid::Log>) -> Option<Arc<Lane>> {
    static LANE: OnceLock<Option<Arc<Lane>>> = OnceLock::new();
    LANE.get_or_init(|| {
        let device = log.clone();
        Lane::spawn(log, move || {
            hid::Transport::new(windows::Windows::new(device.clone()))
        })
        .ok()
        .map(Arc::new)
    })
    .clone()
}

/// What the lane records, into the Service's diagnostic history when the
/// export is composed.
#[cfg(not(test))]
pub(in super::super) fn production_log(
    #[cfg(feature = "service-diagnostics-export-v1")] history: &Arc<
        crate::diagnostic_export::History,
    >,
) -> Arc<hid::Log> {
    #[cfg(feature = "service-diagnostics-export-v1")]
    {
        let history = Arc::clone(history);
        Arc::new(hid::Log::new(Box::new(move |category, message| {
            history.record_log("Warning", category, message);
        })))
    }
    #[cfg(not(feature = "service-diagnostics-export-v1"))]
    Arc::default()
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

    /// Composes the press admission; it may be set once. Without it a test
    /// runtime captures through its factory.
    #[cfg(test)]
    pub(in super::super) fn compose_brightness_guard(&self, guard: Box<Guard>) {
        assert!(self.brightness.guard.set(guard).is_ok());
    }

    /// The lane, for the status observer.
    pub(in super::super) fn brightness_lane(&self) -> Option<Arc<Lane>> {
        self.brightness.lane.get().cloned()
    }

    /// A fresh device capture for a press (managed's device check:
    /// `DeviceVerified`, writes not blocked, the verified device and a fresh
    /// power context). It opens no profile store and selects no profile
    /// root, so it admits any interactive user the session admission does.
    fn admit_display_brightness(
        &self,
        sid: &str,
        session: u32,
        custody: Arc<dyn ReadCustody>,
    ) -> io::Result<Arc<dyn Fresh>> {
        match self.brightness.guard.get() {
            Some(guard) => guard(sid, session, custody),
            None => (self.factory)(sid, session, custody, Capture::Write).map(|(_, fresh)| fresh),
        }
    }

    /// Managed `SetDisplayBrightnessHidUsageAsync`.
    pub(in super::super) async fn set_display_brightness_hid_usage(
        &self,
        mailbox: MailboxSource,
        caller: Caller,
        sid: String,
        usage: Usage,
        deadline: Instant,
    ) -> io::Result<Outcome> {
        #[cfg(test)]
        self.entered.set(self.entered.get() + 1);
        if !controls_session(&caller) {
            return Ok(Outcome::blocked(
                "The active interactive Windows user is required.",
            ));
        }
        // A cleanup boundary ends the wait, at the gate or on the lane, so
        // the gate is free for its keyboard release. Dropping the request
        // withdraws a press that has not started and keeps one that has
        // held (`hid::set_usage`); the boundary's release follows it.
        let boundary = self.brightness.boundary.notified();
        tokio::pin!(boundary);
        boundary.as_mut().enable();
        tokio::select! {
            biased;
            () = &mut boundary => Ok(hid::reply(&hid::WriteResult::failed(Some(
                INTERRUPTED.to_owned(),
            )))),
            outcome = self.set_display_brightness_gated(mailbox, caller, sid, usage, deadline) => {
                outcome
            }
        }
    }

    /// The request after its session check, under the gate.
    async fn set_display_brightness_gated(
        &self,
        mailbox: MailboxSource,
        caller: Caller,
        sid: String,
        usage: Usage,
        deadline: Instant,
    ) -> io::Result<Outcome> {
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
            match self.admit_display_brightness(&sid, caller.session, reader.custody()) {
                Ok(fresh) if fresh.authorize().is_some() => {
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

    /// Managed's cleanup release (`PrepareForShutdownAsync` and
    /// `PrepareForLowPowerCoreAsync`), within `share` and by `end`. It
    /// first ends the wait of every request, so none keeps the gate from
    /// the keyboard's release after it, and then queues the release a usage
    /// still held owes without the gate: managed's operation gate would
    /// keep it behind a press still running, and the lane already does.
    /// Queued behind that press, it runs after it, and one that has not
    /// answered by the share's end stays owed and runs then
    /// (`hid::release_owed`). The outcome is a diagnostic: nothing it does
    /// fails or delays the boundary past its share.
    pub(in super::super) async fn release_display_brightness(
        &self,
        share: std::time::Duration,
        end: Instant,
    ) -> Option<Outcome> {
        self.brightness.boundary.notify_waiters();
        let until = (Instant::now() + share.min(SHUTDOWN_BUDGET)).min(end);
        let lane = self.brightness.lane.get().map(Arc::as_ref);
        hid::release_owed(&self.brightness.held, lane, until, |held| {
            held.release_required
        })
        .await
    }

    /// Managed `MaintainDriverModeAsync`'s neutralization, under the gate
    /// the maintenance step holds and after its verified-device check: a
    /// Service that has not yet released since it started releases now, and
    /// a failure is recorded as managed records it. Nothing is pressed. A
    /// pending one is tried once every `keyboard::INTERVAL`, whatever the
    /// keyboard's own record says, so a step that returns before its own
    /// cadence does not retry it on every tick.
    #[cfg(feature = "service-keyboard-driver-mode-v1")]
    pub(in super::super) async fn neutralize_display_brightness(&self) {
        let brightness = &self.brightness;
        let now = Instant::now();
        if !brightness.held.borrow().neutralization_pending
            || brightness.neutralize_due.get().is_some_and(|due| now < due)
        {
            return;
        }
        brightness
            .neutralize_due
            .set(Some(now + super::keyboard::INTERVAL));
        let lane = brightness.lane.get().map(Arc::as_ref);
        let until = now + hid::CALL_BUDGET;
        let outcome = hid::release_owed(&brightness.held, lane, until, |held| {
            held.neutralization_pending
        })
        .await;
        #[cfg(feature = "service-diagnostics-export-v1")]
        if let Some(outcome) = outcome.filter(|outcome| outcome.status == 3) {
            self.diagnostic_history.record_summary(
                "Failed",
                &format!("{NEUTRALIZATION_FAILED}: {}", outcome.message),
            );
        }
        #[cfg(not(feature = "service-diagnostics-export-v1"))]
        let _ = (outcome, NEUTRALIZATION_FAILED);
    }

    /// Moves the next neutralization `by` earlier, or makes it due now.
    #[cfg(all(test, feature = "service-keyboard-driver-mode-v1"))]
    pub(in super::super) fn neutralize_age_for_tests(&self, by: Option<std::time::Duration>) {
        let due = &self.brightness.neutralize_due;
        due.set(by.and_then(|by| due.get().map(|due| due - by)));
    }
}

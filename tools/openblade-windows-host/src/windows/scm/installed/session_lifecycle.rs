//! The two lifecycle operations other processes ask the Service for: the
//! Tray's complete shutdown (`shutdownOpenBlade`) and the Session Agent's
//! relay of a suspend or resume (`reportSessionPowerTransition`).
//!
//! Neither has a lifecycle of its own. A shutdown asks for the Service's own
//! STOP (`scm_host::deliver_stop`), so the existing retirement and owned
//! cleanup run, and nothing else does. A relayed report is answered and never
//! applied: SCM's notices alone drive the low-power gate, so a report can
//! neither open nor end a boundary. It is compared with the gate read-only.
//! A suspend is held until its cleanup has run only where the keyboard
//! Driver-mode owner arms that hold (`service-keyboard-driver-mode-v1`,
//! `Lifecycle::arm_suspend_hold`); the relay adds none.

use std::{io, rc::Rc, sync::Arc};

use tokio::sync::Mutex;

use super::windows::{Caller, Stream};
use super::{ExchangeError, StatusSource, refresh_caller};
use crate::service_frame::{encode, write_payload};
use crate::service_status::{StatusRequest, StatusResponse};

#[cfg(feature = "service-session-power-relay-v1")]
pub(super) use relay::{Relay, serve_report};
#[cfg(feature = "service-lifecycle-shutdown-v1")]
pub(super) use shutdown::{Quiesced, ShutdownSignal, serve_shutdown};

/// What a composition supplies to serve the lifecycle operations. A port it
/// leaves out answers its operation as managed does without its service.
#[derive(Default)]
pub(super) struct Ports {
    #[cfg(feature = "service-lifecycle-shutdown-v1")]
    pub(super) shutdown: Option<Rc<ShutdownSignal>>,
    #[cfg(feature = "service-session-power-relay-v1")]
    pub(super) relay: Option<Rc<Relay>>,
}

impl Ports {
    /// The installed Service's ports over its loaded origin.
    pub(super) fn native(origin: &Arc<crate::loaded_origin::Origin>) -> Self {
        let _ = origin;
        Self {
            #[cfg(feature = "service-lifecycle-shutdown-v1")]
            shutdown: Some(Rc::new(ShutdownSignal::native())),
            #[cfg(feature = "service-session-power-relay-v1")]
            relay: Some(Rc::new(Relay::native(origin))),
        }
    }
}

/// Managed `ControlOperationResult` as the mutation replies carry it.
fn outcome(status: u8, message: &str) -> io::Result<serde_json::Value> {
    serde_json::to_value(super::thermal_mutation::Outcome {
        status,
        message: message.to_owned(),
    })
    .map_err(io::Error::other)
}

async fn reply(stream: &mut dyn Stream, response: &StatusResponse) -> Result<(), ExchangeError> {
    let payload = encode(response).map_err(ExchangeError::Runtime)?;
    write_payload(stream, &payload)
        .await
        .map_err(ExchangeError::Client)
}

#[cfg(feature = "service-lifecycle-shutdown-v1")]
pub(super) mod shutdown {
    use std::{cell::Cell, cell::RefCell, future::Future, pin::Pin, time::Duration};

    use tokio::{io::AsyncReadExt, task::JoinHandle};

    use super::*;

    /// Managed `HostServiceShutdownSignal.ResponseGracePeriod`: the reply is
    /// written before the host stops.
    pub(crate) const SHUTDOWN_DELAY: Duration = Duration::from_millis(500);
    pub(crate) const SHUTTING_DOWN: &str = "OpenBlade is shutting down.";
    pub(crate) const ADMINISTRATOR: &str = "This operation requires an administrator.";

    /// Resolves once the operations already admitted before it have ended.
    pub(crate) type Quiesced = Pin<Box<dyn Future<Output = ()> + 'static>>;

    /// Managed `HostServiceShutdownSignal`: the first request stops the
    /// Service once `delay` has passed; every later one changes nothing.
    pub(crate) struct ShutdownSignal {
        requested: Cell<bool>,
        delay: Duration,
        stop: Arc<dyn Fn() + Send + Sync>,
        /// The delayed stop, kept so a test can wait for it (`settled`).
        task: RefCell<Option<JoinHandle<()>>>,
    }

    impl ShutdownSignal {
        pub(crate) fn new(delay: Duration, stop: Arc<dyn Fn() + Send + Sync>) -> Self {
            Self {
                requested: Cell::new(false),
                delay,
                stop,
                task: RefCell::new(None),
            }
        }

        /// The Service's own STOP after managed's delay.
        pub(crate) fn native() -> Self {
            Self::new(SHUTDOWN_DELAY, Arc::new(crate::scm_host::deliver_stop))
        }

        /// `RequestShutdownAfterResponse`. The delay runs on its own task,
        /// so a client that disconnects after its reply cannot cancel it.
        pub(crate) fn request_after_response(&self) {
            if self.requested.replace(true) {
                return;
            }
            let (delay, stop) = (self.delay, Arc::clone(&self.stop));
            *self.task.borrow_mut() = Some(tokio::spawn(async move {
                tokio::time::sleep(delay).await;
                stop();
            }));
        }

        /// Waits for a requested stop to have run; returns at once when none
        /// was requested.
        #[cfg(test)]
        pub(crate) async fn settled(&self) {
            let task = self.task.borrow_mut().take();
            if let Some(task) = task {
                task.await.expect("the delayed stop panicked");
            }
        }
    }

    /// Managed admits a shutdown from LocalSystem or an administrator,
    /// whatever its logon kind or session, after the user-request gate: it
    /// waits for the operation in flight, and a client that leaves while it
    /// waits is cancelled without stopping anything. Only this operation
    /// admits a caller whose logon is neither interactive nor SYSTEM, as an
    /// elevated service account's is; the status endpoint's own checks, the
    /// local-only pipe included, still apply. Caller evidence that cannot be
    /// read closes the connection; a token whose membership cannot be read
    /// is answered as no administrator's.
    pub(crate) async fn serve_shutdown(
        stream: &mut dyn Stream,
        request: &StatusRequest,
        original: Caller,
        source: &Mutex<Box<dyn StatusSource>>,
    ) -> Result<(), ExchangeError> {
        use ExchangeError::{Client, Runtime};
        let cancelled = || Err(Client(io::ErrorKind::BrokenPipe.into()));
        refresh_caller(stream, original)?;
        let port = source.lock().await.shutdown();
        let response = match port {
            None => StatusResponse::failure(request, crate::service_status::UNSUPPORTED, None),
            Some((signal, quiesced)) => {
                // EOF or another request cancels this single-request connection.
                let mut probe = [0];
                tokio::select! {
                    biased;
                    disconnected = stream.read(&mut probe) => {
                        disconnected.map_err(Client)?;
                        return cancelled();
                    }
                    () = quiesced => {}
                }
                // Managed observes the cancellation again once it holds the
                // gate. A read may still be waiting out its retry, so the
                // pipe's state is read now.
                if stream.peer_closed() {
                    return cancelled();
                }
                refresh_caller(stream, original)?;
                if stream.system_or_administrator_after_read() {
                    source.lock().await.verify_origin().map_err(Runtime)?;
                    signal.request_after_response();
                    StatusResponse::mutation(request, outcome(0, SHUTTING_DOWN).map_err(Runtime)?)
                } else {
                    StatusResponse::failure(request, ADMINISTRATOR, None)
                }
            }
        };
        reply(stream, &response).await
    }
}

#[cfg(feature = "service-session-power-relay-v1")]
pub(super) mod relay {
    use std::{cell::Cell, cell::RefCell, time::Duration};

    use openblade_domain::low_power_gate::View;

    use super::super::windows::Logon;
    use super::*;
    use crate::origin_lifetime::Lifecycle;

    /// Managed `ServiceRequestAccessPolicy` for every session automation report.
    pub(crate) const INACTIVE: &str =
        "Session automation reports require the active local interactive Windows session.";
    pub(crate) const UNAVAILABLE: &str = "Session power notification handling is unavailable.";
    pub(crate) const UNAUTHORIZED: &str =
        "The session power notification is unauthorized, invalid, or expired.";
    pub(crate) const STALE: &str =
        "The session power notification belongs to a stale agent or sequence.";
    pub(crate) const NEWER: &str = "A newer Windows power transition has already been accepted.";
    /// Managed's reply when an accepted report's lifecycle work has not
    /// completed by its deadline. Native always gives it at once: the relay
    /// queues no work.
    pub(crate) const ACCEPTED: &str = "The notification was accepted; lifecycle completion was not confirmed before the deadline.";
    /// `SessionPowerTransition.MaximumAgeMilliseconds`.
    const MAXIMUM_AGE: Duration = Duration::from_millis(1500);

    /// One relayed report, its instance spelled as the codec read it.
    pub(crate) struct Report<'a> {
        pub(crate) sid: &'a str,
        pub(crate) session: u32,
        pub(crate) instance: &'a str,
        pub(crate) started: i64,
        pub(crate) sequence: i64,
        pub(crate) timestamp: i64,
        pub(crate) suspending: bool,
    }

    /// Managed `AgentInstance`: the Session Agent run that last reported.
    #[derive(Clone, PartialEq, Eq)]
    struct Agent {
        sid: String,
        session: u32,
        instance: String,
        started: i64,
    }

    /// The relay's own record of the Session Agent instance and sequence it
    /// last accepted (managed `agentInstance` and `lastSequence`), and of
    /// that report's stamp. The gate it compares with is read through
    /// `view`, which takes no lock the SCM callback takes
    /// (`Lifecycle::gate_view`); `age` reads a stamp on the clock every
    /// notice is stamped with.
    pub(crate) struct Relay {
        accepted: RefCell<Option<(Agent, i64)>>,
        stamp: Cell<Option<View>>,
        view: Box<dyn Fn() -> Option<View>>,
        age: Box<dyn Fn(i64) -> Option<Duration>>,
    }

    impl Relay {
        pub(crate) fn new(
            view: impl Fn() -> Option<View> + 'static,
            age: impl Fn(i64) -> Option<Duration> + 'static,
        ) -> Self {
            Self {
                accepted: RefCell::default(),
                stamp: Cell::default(),
                view: Box::new(view),
                age: Box::new(age),
            }
        }

        /// The relay over the loaded origin's lifecycle.
        pub(crate) fn native(origin: &Arc<crate::loaded_origin::Origin>) -> Self {
            Self::over(Arc::clone(origin), |origin| &origin.lifecycle)
        }

        /// The relay over the lifecycle `owner` holds.
        pub(crate) fn over<T: 'static>(owner: Arc<T>, lifecycle: fn(&T) -> &Lifecycle) -> Self {
            let view = Arc::clone(&owner);
            Self::new(
                move || lifecycle(&view).gate_view(),
                move |timestamp| lifecycle(&owner).age(timestamp),
            )
        }

        /// Managed `ReportAsync`'s checks, in its order, for a caller
        /// already admitted as the active local interactive session, aged
        /// as of its receipt, `waited` ago. `Ok` is the outcome's status and
        /// message; `Err` is a failure reply's text, given only for a
        /// lifecycle that reads no gate.
        pub(crate) fn report(
            &self,
            report: &Report<'_>,
            waited: Duration,
        ) -> Result<(u8, &'static str), &'static str> {
            // The codec already refused a nil instance, a start or sequence
            // below one and a stamp before the start; the age is its own
            // check. A stamp after the receipt has no age.
            let age = (self.age)(report.timestamp).and_then(|age| age.checked_sub(waited));
            if !age.is_some_and(|age| age < MAXIMUM_AGE) {
                return Ok((2, UNAUTHORIZED));
            }
            let incoming = Agent {
                sid: report.sid.to_owned(),
                session: report.session,
                instance: report.instance.to_ascii_lowercase(),
                started: report.started,
            };
            if let Some((previous, sequence)) = &*self.accepted.borrow()
                && previous.sid == incoming.sid
                && previous.session == incoming.session
                && (incoming.started < previous.started
                    || (incoming.started == previous.started
                        && incoming.instance != previous.instance)
                    || (incoming.instance == previous.instance
                        && (incoming.started != previous.started || report.sequence <= *sequence)))
            {
                return Ok((2, STALE));
            }
            let Some(gate) = (self.view)() else {
                return Err(UNAVAILABLE);
            };
            if newest(gate, self.stamp.get()).refuses(report.suspending, report.timestamp) {
                return Ok((2, NEWER));
            }
            *self.accepted.borrow_mut() = Some((incoming, report.sequence));
            self.stamp.set(Some(View {
                latest: report.timestamp,
                active: report.suspending,
            }));
            Ok((0, ACCEPTED))
        }
    }

    /// What managed's one gate would hold after both SCM's notices and the
    /// relay's accepted reports, from native's gate, which never sees a
    /// report, and the relay's last accepted stamp: the newer of the two.
    /// At an equal stamp it is clear if either is, in either arrival order:
    /// a clear that arrives second is accepted, and a suspend that arrives
    /// second at a clear's stamp is refused.
    fn newest(gate: View, own: Option<View>) -> View {
        match own {
            Some(own) if own.latest > gate.latest => own,
            Some(own) if own.latest == gate.latest => View {
                latest: gate.latest,
                active: own.active && gate.active,
            },
            _ => gate,
        }
    }

    /// Managed admits a report from the active local interactive session
    /// only, then hands it to its coordinator before any gate. Another
    /// caller of the status endpoint gets the failure reply; a caller it
    /// refuses still closes. The report is aged as of `received`, before
    /// any wait for the source, as managed judges it on arrival.
    pub(crate) async fn serve_report(
        stream: &mut dyn Stream,
        request: &StatusRequest,
        original: Caller,
        source: &Mutex<Box<dyn StatusSource>>,
        received: tokio::time::Instant,
    ) -> Result<(), ExchangeError> {
        use ExchangeError::{Client, Runtime};
        let StatusRequest::ReportSessionPowerTransition {
            instance_id,
            instance_started_qpc,
            sequence,
            timestamp_qpc,
            suspending,
            ..
        } = request
        else {
            return Err(Runtime(io::Error::other("not a session power report")));
        };
        let caller = refresh_caller(stream, original)?;
        let access = caller.access().map_err(Client)?;
        let response = if caller.logon != Logon::Interactive || access != 0 {
            StatusResponse::failure(request, INACTIVE, None)
        } else {
            let relay = source.lock().await.session_power_relay();
            match relay {
                None => StatusResponse::failure(request, UNAVAILABLE, None),
                Some(relay) => {
                    let sid = stream.user_sid_after_read().map_err(Client)?;
                    let answer = relay.report(
                        &Report {
                            sid: &sid,
                            session: caller.session,
                            instance: instance_id,
                            started: *instance_started_qpc,
                            sequence: *sequence,
                            timestamp: *timestamp_qpc,
                            suspending: *suspending,
                        },
                        received.elapsed(),
                    );
                    source.lock().await.verify_origin().map_err(Runtime)?;
                    match answer {
                        Ok((status, message)) => StatusResponse::mutation(
                            request,
                            outcome(status, message).map_err(Runtime)?,
                        ),
                        Err(error) => StatusResponse::failure(request, error, None),
                    }
                }
            }
        };
        reply(stream, &response).await
    }
}

//! The two lifecycle operations other processes ask the Service for: the
//! Tray's complete shutdown (`shutdownOpenBlade`) and the Session Agent's
//! relay of a suspend or resume (`reportSessionPowerTransition`).
//!
//! Neither has a lifecycle of its own. A shutdown asks for the Service's own
//! STOP (`scm_host::deliver_stop`), so the existing retirement and owned
//! cleanup run, and nothing else does. A relayed report is answered and never
//! applied: SCM's notices alone drive the low-power gate and hold a suspend
//! until its cleanup has run (`Lifecycle::notice`), so a report can neither
//! open nor end a boundary. It is compared with the gate read-only.

#[cfg(any(feature = "service-lifecycle-shutdown-v1", not(test)))]
use std::sync::Arc;
use std::{cell::Cell, io, rc::Rc, time::Duration};
#[cfg(feature = "service-lifecycle-shutdown-v1")]
use std::{future::Future, pin::Pin};

#[cfg(feature = "service-session-power-relay-v1")]
use openblade_domain::low_power_gate::View;
#[cfg(feature = "service-lifecycle-shutdown-v1")]
use tokio::io::AsyncReadExt;
use tokio::sync::Mutex;

use super::windows::{Caller, Logon, Stream};
use super::{ExchangeError, StatusSource, refresh_caller};
use crate::service_frame::{encode, write_payload};
use crate::service_status::{StatusRequest, StatusResponse};

/// What a composition supplies to serve the lifecycle operations. A port it
/// leaves out answers its operation as unsupported.
#[derive(Default)]
pub(super) struct Ports {
    #[cfg(feature = "service-lifecycle-shutdown-v1")]
    pub(super) shutdown: Option<Rc<ShutdownSignal>>,
    #[cfg(feature = "service-session-power-relay-v1")]
    pub(super) relay: Option<Rc<Relay>>,
}

impl Ports {
    /// The installed Service's ports over its loaded origin.
    #[cfg(not(test))]
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

/// Resolves once the operations already admitted before it have ended.
#[cfg(feature = "service-lifecycle-shutdown-v1")]
pub(super) type Quiesced = Pin<Box<dyn Future<Output = ()> + 'static>>;

/// A composition that lists the operation but has no port for it answers as
/// managed answers an operation it does not dispatch.
#[cfg(feature = "service-lifecycle-shutdown-v1")]
fn unsupported(request: &StatusRequest) -> StatusResponse {
    StatusResponse::failure(request, crate::service_status::UNSUPPORTED, None)
}

// Shutdown ------------------------------------------------------------------

/// Managed `HostServiceShutdownSignal.ResponseGracePeriod`: the reply is
/// written before the host stops.
#[cfg(feature = "service-lifecycle-shutdown-v1")]
pub(super) const SHUTDOWN_DELAY: Duration = Duration::from_millis(500);
#[cfg(feature = "service-lifecycle-shutdown-v1")]
pub(super) const SHUTTING_DOWN: &str = "OpenBlade is shutting down.";
#[cfg(feature = "service-lifecycle-shutdown-v1")]
pub(super) const ADMINISTRATOR: &str = "This operation requires an administrator.";

/// Managed `HostServiceShutdownSignal`: the first request stops the Service
/// once `delay` has passed; every later one changes nothing.
#[cfg(feature = "service-lifecycle-shutdown-v1")]
pub(super) struct ShutdownSignal {
    requested: Cell<bool>,
    delay: Duration,
    stop: Arc<dyn Fn() + Send + Sync>,
}

#[cfg(feature = "service-lifecycle-shutdown-v1")]
impl ShutdownSignal {
    pub(super) fn new(delay: Duration, stop: Arc<dyn Fn() + Send + Sync>) -> Self {
        Self {
            requested: Cell::new(false),
            delay,
            stop,
        }
    }

    /// The Service's own STOP after managed's delay.
    #[cfg(not(test))]
    fn native() -> Self {
        Self::new(SHUTDOWN_DELAY, Arc::new(crate::scm_host::deliver_stop))
    }

    /// `RequestShutdownAfterResponse`. The delay runs on its own task, so a
    /// client that disconnects after its reply cannot cancel the stop.
    pub(super) fn request_after_response(&self) {
        if self.requested.replace(true) {
            return;
        }
        let (delay, stop) = (self.delay, Arc::clone(&self.stop));
        drop(tokio::spawn(async move {
            tokio::time::sleep(delay).await;
            stop();
        }));
    }
}

/// Managed admits a shutdown from LocalSystem or an administrator, whatever
/// its logon kind or session, after the user-request gate: it waits for the
/// operation in flight, and a client that leaves while it waits is
/// cancelled without stopping anything. Only this operation admits a caller
/// whose logon is neither interactive nor SYSTEM, as an elevated service
/// account's is; the status endpoint's own checks, the local-only pipe
/// included, still apply.
#[cfg(feature = "service-lifecycle-shutdown-v1")]
pub(super) async fn serve_shutdown(
    stream: &mut dyn Stream,
    request: &StatusRequest,
    original: Caller,
    source: &Mutex<Box<dyn StatusSource>>,
) -> Result<(), ExchangeError> {
    use ExchangeError::{Client, Runtime};
    refresh_caller(stream, original)?;
    let port = source.lock().await.shutdown();
    let response = match port {
        None => unsupported(request),
        Some((signal, quiesced)) => {
            // EOF or another request cancels this single-request connection.
            let mut probe = [0];
            tokio::select! {
                biased;
                disconnected = stream.read(&mut probe) => {
                    disconnected.map_err(Client)?;
                    return Err(Client(io::ErrorKind::BrokenPipe.into()));
                }
                () = quiesced => {}
            }
            let caller = refresh_caller(stream, original)?;
            if caller.logon == Logon::System || stream.administrator_after_read() {
                source.lock().await.verify_origin().map_err(Runtime)?;
                signal.request_after_response();
                StatusResponse::mutation(request, outcome(0, SHUTTING_DOWN).map_err(Runtime)?)
            } else {
                StatusResponse::failure(request, ADMINISTRATOR, None)
            }
        }
    };
    write_payload(stream, &encode(&response).map_err(Runtime)?)
        .await
        .map_err(Client)
}

// Session power relay -------------------------------------------------------

/// Managed `ServiceRequestAccessPolicy` for every session automation report.
#[cfg(feature = "service-session-power-relay-v1")]
pub(super) const INACTIVE: &str =
    "Session automation reports require the active local interactive Windows session.";
#[cfg(feature = "service-session-power-relay-v1")]
pub(super) const UNAVAILABLE: &str = "Session power notification handling is unavailable.";
#[cfg(feature = "service-session-power-relay-v1")]
pub(super) const UNAUTHORIZED: &str =
    "The session power notification is unauthorized, invalid, or expired.";
#[cfg(feature = "service-session-power-relay-v1")]
pub(super) const STALE: &str =
    "The session power notification belongs to a stale agent or sequence.";
#[cfg(feature = "service-session-power-relay-v1")]
pub(super) const NEWER: &str = "A newer Windows power transition has already been accepted.";
/// Managed's reply when an accepted report's lifecycle work has not
/// completed by its deadline. Native always gives it at once: the relay
/// queues no work, and SCM's hold covers the suspend.
#[cfg(feature = "service-session-power-relay-v1")]
pub(super) const ACCEPTED: &str =
    "The notification was accepted; lifecycle completion was not confirmed before the deadline.";
/// `SessionPowerTransition.MaximumAgeMilliseconds`.
#[cfg(feature = "service-session-power-relay-v1")]
const MAXIMUM_AGE: Duration = Duration::from_millis(1500);

/// One relayed report, its instance spelled as the codec read it.
#[cfg(feature = "service-session-power-relay-v1")]
pub(super) struct Report<'a> {
    pub(super) sid: &'a str,
    pub(super) session: u32,
    pub(super) instance: &'a str,
    pub(super) started: i64,
    pub(super) sequence: i64,
    pub(super) timestamp: i64,
    pub(super) suspending: bool,
}

/// Managed `AgentInstance`: the Session Agent run that last reported.
#[cfg(feature = "service-session-power-relay-v1")]
#[derive(Clone, PartialEq, Eq)]
struct Agent {
    sid: String,
    session: u32,
    instance: String,
    started: i64,
}

/// The relay's own record of the Session Agent instance and sequence it
/// last accepted (managed `agentInstance` and `lastSequence`), and of that
/// report's stamp. The gate it compares with is read through `view`, which
/// takes no lock the SCM callback takes (`Lifecycle::gate_view`); `age`
/// reads a stamp on the clock every notice is stamped with.
#[cfg(feature = "service-session-power-relay-v1")]
pub(super) struct Relay {
    accepted: std::cell::RefCell<Option<(Agent, i64)>>,
    stamp: Cell<Option<View>>,
    view: Box<dyn Fn() -> Option<View>>,
    age: Box<dyn Fn(i64) -> Option<Duration>>,
}

#[cfg(feature = "service-session-power-relay-v1")]
impl Relay {
    pub(super) fn new(
        view: impl Fn() -> Option<View> + 'static,
        age: impl Fn(i64) -> Option<Duration> + 'static,
    ) -> Self {
        Self {
            accepted: Default::default(),
            stamp: Cell::default(),
            view: Box::new(view),
            age: Box::new(age),
        }
    }

    /// The relay over the loaded origin's lifecycle.
    #[cfg(not(test))]
    fn native(origin: &Arc<crate::loaded_origin::Origin>) -> Self {
        let (view, age) = (Arc::clone(origin), Arc::clone(origin));
        Self::new(
            move || view.lifecycle.gate_view(),
            move |timestamp| age.lifecycle.age(timestamp),
        )
    }

    /// Managed `ReportAsync`'s checks, in its order, for a caller already
    /// admitted as the active local interactive session. `Ok` is the
    /// outcome's status and message; `Err` is a failure reply's text, given
    /// only for a lifecycle whose epoch is exhausted, which reads no gate.
    pub(super) fn report(&self, report: &Report<'_>) -> Result<(u8, &'static str), &'static str> {
        // Managed's codec already refused a nil instance, a start or sequence
        // below one and a stamp before the start; the age is its own check.
        if !(self.age)(report.timestamp).is_some_and(|age| age < MAXIMUM_AGE) {
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
                || (incoming.started == previous.started && incoming.instance != previous.instance)
                || (incoming.instance == previous.instance
                    && (incoming.started != previous.started || report.sequence <= *sequence)))
        {
            return Ok((2, STALE));
        }
        let Some(gate) = (self.view)() else {
            return Err(UNAVAILABLE);
        };
        // Managed's gate also holds the stamp of the last report it accepted.
        // Native's gate never sees one, so the newer of the two is compared,
        // which is what managed's gate would hold.
        let latest = match self.stamp.get() {
            Some(own) if own.latest > gate.latest => own,
            _ => gate,
        };
        if latest.refuses(report.suspending, report.timestamp) {
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

/// Managed admits a report from the active local interactive session only,
/// then hands it to its coordinator. Another caller of the status endpoint
/// gets the failure reply; a caller it refuses still closes.
#[cfg(feature = "service-session-power-relay-v1")]
pub(super) async fn serve_report(
    stream: &mut dyn Stream,
    request: &StatusRequest,
    original: Caller,
    source: &Mutex<Box<dyn StatusSource>>,
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
                let reply = relay.report(&Report {
                    sid: &sid,
                    session: caller.session,
                    instance: instance_id,
                    started: *instance_started_qpc,
                    sequence: *sequence,
                    timestamp: *timestamp_qpc,
                    suspending: *suspending,
                });
                source.lock().await.verify_origin().map_err(Runtime)?;
                match reply {
                    Ok((status, message)) => StatusResponse::mutation(
                        request,
                        outcome(status, message).map_err(Runtime)?,
                    ),
                    Err(error) => StatusResponse::failure(request, error, None),
                }
            }
        }
    };
    write_payload(stream, &encode(&response).map_err(Runtime)?)
        .await
        .map_err(Client)
}

/// Managed `ControlOperationResult` as the mutation replies carry it.
fn outcome(status: u8, message: &str) -> io::Result<serde_json::Value> {
    serde_json::to_value(super::thermal_mutation::Outcome {
        status,
        message: message.to_owned(),
    })
    .map_err(io::Error::other)
}

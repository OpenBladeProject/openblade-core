//! Installed v91 status. Ports must own their resources and acknowledge retirement.

use std::{future::Future, io, pin::Pin, rc::Rc, sync::Arc, time::Duration};

#[cfg(test)]
use crate::service_frame::write_frame;
#[cfg(test)]
use tokio::io::{AsyncRead, AsyncWrite};
use tokio::{
    io::AsyncReadExt,
    sync::{Mutex, oneshot, watch},
    task::{JoinSet, LocalSet},
    time::{Instant, MissedTickBehavior, timeout, timeout_at},
};

use super::{Readiness, denied};
use crate::{
    scm_host::startup_stopped,
    service_frame::{FRAME_TIMEOUT, encode, read_payload, write_payload},
    service_status::{
        PIPE_NAME, PROTOCOL_VERSION, Snapshot, StatusRequest, StatusResponse, UnservedRequest,
        exchange, production_path, request_id as probe_id,
    },
};

#[path = "readiness_windows.rs"]
mod windows;
use windows::{Caller, ControlSession, Logon, MAX_CONNECTIONS, Pending, Stream};

#[cfg(any(test, feature = "service-hardware-compile-check"))]
// The native constructors are compile-checked only; no admitted runtime composes them yet.
#[allow(dead_code)]
#[path = "firmware_source.rs"]
mod firmware_source;
#[cfg(any(test, feature = "service-hardware-compile-check"))]
pub(super) use firmware_source::FirmwareSource;
#[cfg(feature = "service-mailbox-observation-v1")]
#[cfg_attr(test, allow(dead_code))] // Test builds exclude the native acquisition path.
#[path = "mailbox_read.rs"]
mod mailbox_read;
#[cfg(any(test, feature = "service-hardware-compile-check"))]
// Native acquisition is compile-checked only; no admitted runtime acquires an owner yet.
#[allow(dead_code)]
#[path = "ownership.rs"]
pub(super) mod ownership;

// NVIDIA's NVML: one System32-only loader for the GPU temperature reader and
// the clock-offset backend, and that backend, which only
// `service-gpu-clock-tuning-v1` composes (`thermal_gpu`).
#[cfg(feature = "service-thermal-mutation-v1")]
#[path = "gpu_clock_offsets.rs"]
mod gpu_clock_offsets;
#[cfg(any(
    feature = "service-live-readings-v1",
    feature = "service-thermal-mutation-v1"
))]
#[path = "nvml_library.rs"]
mod nvml_library;
// The claimed, budgeted call thread the GPU worker and the display-brightness
// lane share.
#[cfg(feature = "service-thermal-mutation-v1")]
#[path = "withdrawal_lane.rs"]
mod withdrawal_lane;

#[cfg(feature = "service-diagnostics-export-v1")]
#[path = "readiness_export.rs"]
mod export;
#[cfg(feature = "service-diagnostics-export-v1")]
use export::serve_export;
#[path = "status_source.rs"]
mod status_source;
#[cfg(feature = "service-profile-triggers-v1")]
#[path = "thermal_drift.rs"]
mod thermal_drift;
#[cfg(feature = "service-gpu-clock-tuning-v1")]
#[path = "thermal_gpu.rs"]
mod thermal_gpu;
#[cfg(feature = "service-thermal-mutation-v1")]
#[path = "thermal_mutation.rs"]
mod thermal_mutation;
#[cfg(feature = "service-thermal-mutation-v1")]
#[path = "thermal_native.rs"]
mod thermal_native;
#[cfg(feature = "service-thermal-mutation-v1")]
#[path = "thermal_storage.rs"]
mod thermal_storage;
#[cfg(feature = "service-thermal-mutation-v1")]
#[path = "transaction_seed.rs"]
mod transaction_seed;

const PROBE_TIMEOUT: Duration = Duration::from_secs(4);
const DRAIN_TIMEOUT: Duration = Duration::from_secs(2);
/// The source drain's bound. The keyboard Driver-mode release takes its own
/// share before the fan handback (`thermal_mutation::KEYBOARD_SHUTDOWN_BUDGET`,
/// 0.9 s) and the fans keep the 2 s they had. With the listener's 2 s that is
/// 4.9 s, inside the 5 s Stop wait hint (`scm_host::status_for`) and the
/// system-shutdown window, WaitToKillServiceTimeout, about 5 s: native
/// accepts SERVICE_CONTROL_SHUTDOWN, not PRESHUTDOWN. A display-brightness
/// usage still held at Stop is released first, within its own share
/// (`thermal_mutation::BRIGHTNESS_SHUTDOWN_BUDGET`, 0.3 s), out of this bound:
/// only then do the fans start with less than their 2 s, at least 1.7 s.
#[cfg(feature = "service-keyboard-driver-mode-v1")]
const SOURCE_DRAIN_TIMEOUT: Duration =
    DRAIN_TIMEOUT.saturating_add(thermal_mutation::KEYBOARD_SHUTDOWN_BUDGET);
#[cfg(not(feature = "service-keyboard-driver-mode-v1"))]
const SOURCE_DRAIN_TIMEOUT: Duration = DRAIN_TIMEOUT;
const MAX_SUBSCRIPTIONS: usize = 8;

#[path = "subscription.rs"]
mod subscription;
use subscription::{Publication, Subscriptions, serve_subscription};

fn windows_control_session() -> ControlSession {
    Rc::new(|| Ok(crate::console_session::observe()))
}

struct Listener {
    pipe_name: &'static str,
    // Must come from an exclusive first-instance create, held continuously.
    first_instance: bool,
    // Protected managed DACL: Network deny, SYSTEM/admin full, Users read/write.
    managed_acl: bool,
    local_only: bool,
}

impl Listener {
    fn verify(self) -> io::Result<()> {
        if self.pipe_name != PIPE_NAME
            || !self.first_instance
            || !self.managed_acl
            || !self.local_only
        {
            return Err(denied("unverified installed status listener"));
        }
        Ok(())
    }
}

impl Caller {
    fn access(self) -> io::Result<i32> {
        match self.logon {
            Logon::System => Ok(0),
            Logon::Interactive if self.control_session == Some(self.session) => Ok(0),
            Logon::Interactive | Logon::RemoteInteractive => Ok(1),
            Logon::Other => Err(denied(
                "installed status requires SYSTEM or an interactive caller",
            )),
        }
    }
}

// Concrete implementations must bind all evidence to the live connection, not
// to a name lookup. Dropping pending I/O must leave no detached work; dropping
// an accepted stream must close it. Retire confirms listener and I/O closure.
trait Endpoint {
    fn listener(&self) -> io::Result<Listener>;
    fn accept(&self) -> Pending<'_, Box<dyn Stream>>;
    fn connect_probe(&self) -> Pending<'_, Box<dyn Stream>>;
    fn retire(&mut self) -> Pending<'_, ()>;
}

impl Endpoint for windows::NativeEndpoint {
    fn listener(&self) -> io::Result<Listener> {
        self.verify_managed_listener(&production_path())?;
        Ok(Listener {
            pipe_name: PIPE_NAME,
            first_instance: true,
            managed_acl: true,
            local_only: true,
        })
    }

    fn accept(&self) -> Pending<'_, Box<dyn Stream>> {
        windows::NativeEndpoint::accept(self)
    }

    fn connect_probe(&self) -> Pending<'_, Box<dyn Stream>> {
        windows::NativeEndpoint::connect_probe(self)
    }

    fn retire(&mut self) -> Pending<'_, ()> {
        windows::NativeEndpoint::retire(self)
    }
}

// Owns an already admitted runtime source. Requests that overlap an
// observation can share it, using the projection for the resolved caller.
type Observation = Pin<Box<dyn Future<Output = io::Result<Option<Snapshot>>> + 'static>>;

#[cfg(feature = "service-thermal-mutation-v1")]
type Mutation = Pin<
    Box<dyn Future<Output = io::Result<(thermal_mutation::Outcome, Option<Snapshot>)>> + 'static>,
>;
/// One lifecycle reconciliation, keyboard Driver-mode step or lighting
/// keepalive, awaited without the source lock.
#[cfg(any(
    feature = "service-profile-triggers-v1",
    feature = "service-keyboard-driver-mode-v1",
    feature = "service-lighting-v1"
))]
type Reconciling = Pin<Box<dyn Future<Output = io::Result<()>> + 'static>>;
trait StatusSource {
    fn process_power_source(&mut self) -> Pending<'_, ()> {
        Box::pin(async { Ok(()) })
    }
    // The origin tick's reconciliation of what a lifecycle transition left
    // owed (`Runtime::reconcile_lifecycle`). It is taken under the source
    // lock and awaited after it is released, as a mutation is, so a
    // reconciliation never holds status or subscriptions behind its steps.
    #[cfg(feature = "service-profile-triggers-v1")]
    fn reconcile_lifecycle(&mut self) -> Option<Reconciling> {
        None
    }
    // The origin tick's lighting keepalive at `now`, after the power check
    // and before the keyboard Driver-mode step (`Runtime::maintain_lighting`),
    // taken under the source lock and awaited after it is released.
    #[cfg(feature = "service-lighting-v1")]
    fn maintain_lighting(&mut self, _now: Instant) -> Option<Reconciling> {
        None
    }
    // The animated lighting stream loop (`Runtime::run_lighting_stream`),
    // taken once when connections are served; it ends only with fatal
    // admission evidence.
    #[cfg(feature = "service-lighting-v1")]
    fn lighting_stream(&mut self) -> Option<Reconciling> {
        None
    }
    // The origin tick's 15 s keyboard Driver-mode step after the power
    // check (`Runtime::maintain_driver_mode`), taken under the source lock and
    // awaited after it is released, as a reconciliation is.
    #[cfg(feature = "service-keyboard-driver-mode-v1")]
    fn maintain_driver_mode(&mut self) -> Option<Reconciling> {
        None
    }
    // Notified once for each new suspend generation
    // (`Lifecycle::suspended`), so the origin tick prepares the boundary at
    // once instead of at its next interval.
    #[cfg(feature = "service-keyboard-driver-mode-v1")]
    fn low_power_wake(&self) -> Option<Arc<tokio::sync::Notify>> {
        None
    }
    #[cfg(feature = "service-thermal-mutation-v1")]
    fn mutate(&mut self, _: Caller, _: String, _: StatusRequest, _: Instant) -> Mutation {
        Box::pin(async {
            Ok((
                thermal_mutation::Outcome::blocked("This composition has no mutation authority."),
                None,
            ))
        })
    }

    fn snapshot(&mut self, caller: Caller, requested: Instant) -> Pending<'_, Snapshot>;

    // Only an admitted, independent read started for this call can supply
    // performance. This port must not reconcile, mutate or reuse a publication.
    // Dropping the read must cancel it. The metadata observer has no firmware
    // read authority.
    fn observe_performance(&mut self, _: Caller, _: Instant) -> Observation {
        Box::pin(async { Ok(None) })
    }

    // The admitted runtime must seed this feed and publish monotonically,
    // with the managed inactive-session projection for every revision.
    fn publications(&self) -> Option<watch::Receiver<Arc<Publication>>> {
        None
    }
    // Return the next due time from the same owner that updates the cache.
    // A skipped refresh must not add another complete polling interval.
    fn refresh_publications(&mut self) -> Pending<'_, Instant> {
        Box::pin(async { Ok(Instant::now() + subscription::REFRESH_INTERVAL) })
    }
    fn verify_origin(&self) -> io::Result<()> {
        Ok(())
    }
    fn confirm_drained(&self) -> io::Result<()> {
        Ok(())
    }
    fn publication_guard(&self) -> Rc<dyn Fn() -> io::Result<()>> {
        Rc::new(|| Ok(()))
    }
    // The history and host metadata an export reads. A composition with a
    // history producer supplies its own history; any other has none.
    #[cfg(feature = "service-diagnostics-export-v1")]
    fn diagnostics(&self) -> crate::diagnostic_export::Sources {
        crate::diagnostic_export::Sources::service()
    }
    fn drain(&mut self) -> Pending<'_, ()>;
    /// `drain` under `deadline`, the bound `Ports::retire` puts on it, so
    /// work that must end inside the drain can pace itself to the time left.
    fn drain_by(&mut self, _deadline: Instant) -> Pending<'_, ()> {
        self.drain()
    }
}

struct Ports {
    endpoint: Box<dyn Endpoint>,
    source: Rc<Mutex<Box<dyn StatusSource>>>,
}

#[cfg(not(test))]
pub(super) async fn prepare_installed_endpoint(
    launch: &super::LaunchIdentity,
    admission: super::native::AdmittedRuntime,
    shutdown: oneshot::Receiver<()>,
    failed: oneshot::Sender<()>,
) -> io::Result<(Readiness, impl Future<Output = io::Result<()>> + use<>)> {
    let origin = admission.verify_launch(launch)?;
    let source = status_source::native_status_source(origin)?;
    let endpoint = windows::NativeEndpoint::open_managed(
        &production_path(),
        launch.pid,
        windows_control_session(),
    )?;
    prepare(
        launch.pid,
        shutdown,
        failed,
        Ok(Ports {
            endpoint: Box::new(endpoint),
            source: Rc::new(Mutex::new(source)),
        }),
    )
    .await
}

async fn prepare(
    pid: u32,
    mut shutdown: oneshot::Receiver<()>,
    failed: oneshot::Sender<()>,
    ports: io::Result<Ports>,
) -> io::Result<(Readiness, impl Future<Output = io::Result<()>> + use<>)> {
    let mut ports = ports?;
    let result = tokio::select! {
        biased;
        result = timeout(PROBE_TIMEOUT, ports.probe(pid)) => result.map_err(io::Error::from).and_then(|r| r),
        signal = &mut shutdown => signal.map_err(|_| lost_shutdown()).and_then(|()| Err(startup_stopped())),
    };
    match result {
        Ok(()) => Ok((
            Readiness {
                pipe_name: PIPE_NAME,
                server_pid: pid,
                protocol_version: PROTOCOL_VERSION,
                correlated_status: true,
            },
            ports.drive(shutdown, failed),
        )),
        Err(error) => {
            // In particular, an unconfirmed drain overrides startup cancellation.
            ports.retire().await?;
            Err(error)
        }
    }
}

impl Ports {
    async fn probe(&mut self, pid: u32) -> io::Result<()> {
        self.endpoint.listener()?.verify()?;
        let request = StatusRequest::get(probe_id()?)?;
        let endpoint = &*self.endpoint;
        let served = async {
            let mut source = self.source.lock().await;
            serve(&mut **source, endpoint.accept(), Some((pid, &request))).await
        };
        let probe = async {
            let mut client = endpoint.connect_probe().await?;
            exchange(&mut *client, &request, Some(0), |stream| {
                verify_peer(stream, pid)
            })
            .await
            .map(drop)
        };
        // Keep the accepted server handle open until the client validates the
        // response and rechecks the server PID on the original connection.
        let (_server, ()) =
            tokio::try_join!(async { served.await.map_err(io::Error::from) }, probe)?;
        endpoint.listener()?.verify()
    }

    async fn drive(
        mut self,
        mut shutdown: oneshot::Receiver<()>,
        failed: oneshot::Sender<()>,
    ) -> io::Result<()> {
        let (mut result, signal_consumed) = LocalSet::new()
            .run_until(self.serve_connections(&mut shutdown))
            .await;
        if !signal_consumed {
            // Notify SCM before a bounded drain so the service is no longer
            // reported as running after the listener or source failed.
            let _ = failed.send(());
            let signal = shutdown.await.map_err(|_| lost_shutdown());
            // Only a typed STOP revocation followed by the real shutdown signal
            // is expected. A lost channel or any earlier trust failure stays fatal.
            result = if result
                .as_ref()
                .is_err_and(crate::loaded_origin::lifetime::expected_stop)
            {
                signal
            } else {
                result.and(signal)
            };
        }
        result.and(self.retire().await)
    }

    async fn serve_connections(
        &self,
        shutdown: &mut oneshot::Receiver<()>,
    ) -> (io::Result<()>, bool) {
        let publications = self.source.lock().await.publications();
        let subscriptions = Rc::new(Subscriptions::new(MAX_SUBSCRIPTIONS));
        let refresh = subscriptions.refresh_while_subscribed(|| async {
            let mut source = self.source.lock().await;
            if subscriptions.has_subscribers() {
                source.refresh_publications().await
            } else {
                Ok(Instant::now())
            }
        });
        tokio::pin!(refresh);
        let mut connections = JoinSet::new();
        let mut accept = self.endpoint.accept();
        #[cfg(feature = "service-lighting-v1")]
        let stream = self.source.lock().await.lighting_stream();
        let lighting_stream = async move {
            #[cfg(feature = "service-lighting-v1")]
            if let Some(stream) = stream {
                return stream.await;
            }
            std::future::pending::<io::Result<()>>().await
        };
        tokio::pin!(lighting_stream);
        // Keep lock acquisition in a polled future. Awaiting it inside the
        // selected tick branch would stop polling a refresh that owns the lock.
        let origin_check = async {
            let mut interval = tokio::time::interval(Duration::from_millis(500));
            // Windows Instant counts time asleep, so the default Burst would
            // replay every missed origin and power check back to back after a
            // resume. Delay runs one check, then resumes the cadence.
            interval.set_missed_tick_behavior(MissedTickBehavior::Delay);
            // A new suspend generation runs the tick at once: its low-power
            // preparation has `low_power_cleanup::MAXIMUM_AGE` from
            // the notice, and the interval alone could spend a third of it.
            #[cfg(feature = "service-keyboard-driver-mode-v1")]
            let wake = self.source.lock().await.low_power_wake();
            loop {
                #[cfg(feature = "service-keyboard-driver-mode-v1")]
                if let Some(wake) = &wake {
                    tokio::select! {
                        _ = interval.tick() => {}
                        () = wake.notified() => {}
                    }
                } else {
                    interval.tick().await;
                }
                #[cfg(not(feature = "service-keyboard-driver-mode-v1"))]
                interval.tick().await;
                let mut source = self.source.lock().await;
                source.verify_origin()?;
                source.process_power_source().await?;
                // The renderer's keepalive goes before the keyboard step, so a
                // tick that finds the operation gate free at a low-power
                // boundary ends the renderer before Normal mode is restored.
                // While a request holds the gate the tick sends nothing, and
                // the renderer ends at the first tick or request after it
                // (`Runtime::crossed_low_power`).
                #[cfg(feature = "service-lighting-v1")]
                {
                    let maintain = source.maintain_lighting(Instant::now());
                    drop(source);
                    if let Some(maintain) = maintain {
                        maintain.await?;
                    }
                }
                // Managed step order: power-source classification, then the
                // keyboard Driver-mode step. Its first eligible tick is the
                // managed startup call; native runs no startup reconciliation
                // for it to precede. In low power it prepares a new suspend
                // generation once and acquires nothing.
                #[cfg(feature = "service-keyboard-driver-mode-v1")]
                {
                    #[cfg(feature = "service-lighting-v1")]
                    let mut source = self.source.lock().await;
                    let maintain = source.maintain_driver_mode();
                    drop(source);
                    if let Some(maintain) = maintain {
                        maintain.await?;
                    }
                }
                // A resume, a gate-clearing DisplayOn, a power-kind change or
                // an expired lease reconciles what the reporting pair owes.
                // It is bounded by its own steps and serialized with requests
                // by the mutation gate; the tick waits for it, so at most one
                // runs.
                #[cfg(feature = "service-profile-triggers-v1")]
                {
                    #[cfg(any(
                        feature = "service-keyboard-driver-mode-v1",
                        feature = "service-lighting-v1"
                    ))]
                    let mut source = self.source.lock().await;
                    let reconcile = source.reconcile_lifecycle();
                    drop(source);
                    if let Some(reconcile) = reconcile {
                        reconcile.await?;
                    }
                }
            }
        };
        tokio::pin!(origin_check);
        let outcome = loop {
            tokio::select! {
                biased;
                result = &mut origin_check => break (result, false),
                result = &mut lighting_stream => break (result, false),
                signal = &mut *shutdown => break (signal.map_err(|_| lost_shutdown()), true),
                result = &mut refresh, if publications.is_some() => break (result, false),
                joined = connections.join_next(), if !connections.is_empty() => match joined {
                    Some(Ok(Err(ExchangeError::Runtime(error)))) => break (Err(error), false),
                    Some(Err(error)) => break (Err(io::Error::other(error)), false),
                    _ => {}
                },
                accepted = &mut accept, if connections.len() < MAX_CONNECTIONS => {
                    let stream = match accepted {
                        Ok(stream) => stream,
                        Err(error) => break (Err(error), false),
                    };
                    let source = Rc::clone(&self.source);
                    let publications = publications.clone();
                    let subscriptions = Rc::clone(&subscriptions);
                    connections.spawn_local(async move {
                        serve_connection(stream, source, publications, subscriptions).await
                    });
                    accept = self.endpoint.accept();
                }
            }
        };
        drop(accept);
        connections.shutdown().await;
        outcome
    }

    async fn retire(&mut self) -> io::Result<()> {
        // Stop ingress before draining the source. Attempt both even when
        // listener ownership or retirement cannot be confirmed.
        let owned = self.endpoint.listener().and_then(Listener::verify);
        let listener = timeout(DRAIN_TIMEOUT, self.endpoint.retire()).await;
        let deadline = Instant::now() + SOURCE_DRAIN_TIMEOUT;
        let runtime = timeout_at(deadline, async {
            self.source.lock().await.drain_by(deadline).await
        })
        .await;
        owned.and(listener?).and(runtime?)?;
        self.source.lock().await.confirm_drained()
    }
}

fn lost_shutdown() -> io::Error {
    io::Error::new(
        io::ErrorKind::BrokenPipe,
        "installed shutdown signal was lost",
    )
}

fn verify_peer(stream: &dyn Stream, pid: u32) -> io::Result<()> {
    if stream.server_pid()? != pid {
        return Err(denied("status probe reached a different server process"));
    }
    Ok(())
}

#[derive(Debug)]
enum ExchangeError {
    Client(io::Error),
    Runtime(io::Error),
}

impl From<ExchangeError> for io::Error {
    fn from(value: ExchangeError) -> Self {
        match value {
            ExchangeError::Client(error) | ExchangeError::Runtime(error) => error,
        }
    }
}

async fn serve(
    source: &mut dyn StatusSource,
    accept: impl Future<Output = io::Result<Box<dyn Stream>>>,
    probe: Option<(u32, &StatusRequest)>,
) -> Result<Box<dyn Stream>, ExchangeError> {
    use ExchangeError::{Client, Runtime};
    let mut stream = accept.await.map_err(Runtime)?;
    let (payload, caller) = read_frame(&mut *stream).await?;
    let request = StatusRequest::decode_get(&payload).map_err(Client)?;
    if let Some((pid, expected)) = probe
        && (caller.pid != Some(pid)
            || caller.logon != Logon::System
            || caller.session != 0
            || !request.matches(expected))
    {
        return Err(Client(denied(
            "unverified installed self-probe caller or request",
        )));
    }
    serve_get(source, &mut *stream, &request, caller).await?;
    Ok(stream)
}

async fn serve_connection(
    mut stream: Box<dyn Stream>,
    source: Rc<Mutex<Box<dyn StatusSource>>>,
    publications: Option<watch::Receiver<Arc<Publication>>>,
    subscriptions: Rc<Subscriptions>,
) -> Result<(), ExchangeError> {
    let (request, caller) = match read_request(&mut *stream).await? {
        (Inbound::Served(request), caller) => (request, caller),
        (Inbound::Unserved(request), caller) => {
            let guard = source.lock().await.publication_guard();
            return serve_unserved(&mut *stream, &request, caller, &*guard).await;
        }
    };
    // Receipt precedes waiting for an in-flight observer's source mutex. Its
    // completion can satisfy this GET only when it is newer than this instant.
    let requested = Instant::now();
    let guard = source.lock().await.publication_guard();
    guard().map_err(ExchangeError::Runtime)?;
    if request.is_subscription() {
        serve_subscription(
            &mut *stream,
            &request,
            caller,
            publications,
            &subscriptions,
            &*guard,
            async { source.lock().await.refresh_publications().await.map(drop) },
        )
        .await
    } else if cfg!(feature = "service-thermal-mutation-v1")
        && matches!(
            request,
            StatusRequest::SetBatteryPolicy { .. }
                | StatusRequest::SetBatteryProtection { .. }
                | StatusRequest::SetFullChargeOverride { .. }
                | StatusRequest::SetFanOverride { .. }
                | StatusRequest::SetPerformanceOverride { .. }
                | StatusRequest::CyclePerformanceShortcut { .. }
                | StatusRequest::SetPerformanceShortcutPreview { .. }
                | StatusRequest::SetHyperBoost { .. }
                | StatusRequest::RecoverHyperBoostRestore { .. }
                | StatusRequest::SetDisplayBrightnessHidUsage { .. }
                | StatusRequest::ReportApplicationContext { .. }
                | StatusRequest::UpdateProfiles { .. }
                | StatusRequest::SetThermalControlPolicy { .. }
                | StatusRequest::AcknowledgeThermalPolicyUpgradeDisclosure { .. }
                | StatusRequest::ReapplyChangedSettings { .. }
                | StatusRequest::SetFanStopMode { .. }
                | StatusRequest::SetFanStopSettings { .. }
                | StatusRequest::ResumeAutomaticFanStop { .. }
                | StatusRequest::SetBatterySaverFanStop { .. }
                | StatusRequest::SetLightingOverride { .. }
                | StatusRequest::StepKeyboardBrightness { .. }
                | StatusRequest::SetLightingRefreshRate { .. }
        )
    {
        #[cfg(feature = "service-thermal-mutation-v1")]
        serve_mutation(&mut *stream, &request, caller, &source).await?;
        guard().map_err(ExchangeError::Runtime)
    } else if request.is_observation() {
        serve_observation(&mut *stream, &request, caller, &source).await?;
        guard().map_err(ExchangeError::Runtime)
    } else if request.is_export() {
        #[cfg(feature = "service-diagnostics-export-v1")]
        serve_export(
            &mut *stream,
            &request,
            caller,
            &source,
            subscriptions.exports(),
        )
        .await?;
        guard().map_err(ExchangeError::Runtime)
    } else {
        caller.access().map_err(ExchangeError::Client)?;
        let payload = {
            let mut source = source.lock().await;
            let caller = resolve_caller(&*stream, caller)?;
            status_payload(&mut **source, &request, caller, requested, Some(&*stream)).await?
        };
        guard().map_err(ExchangeError::Runtime)?;
        crate::service_frame::write_payload(&mut *stream, &payload)
            .await
            .map_err(ExchangeError::Client)?;
        guard().map_err(ExchangeError::Runtime)
    }
}

async fn serve_observation(
    stream: &mut dyn Stream,
    request: &StatusRequest,
    caller: Caller,
    source: &Mutex<Box<dyn StatusSource>>,
) -> Result<(), ExchangeError> {
    use ExchangeError::{Client, Runtime};
    caller.access().map_err(Client)?;
    let deadline = Instant::now()
        + if request.battery_request().is_some() {
            Duration::from_secs(6)
        } else {
            FRAME_TIMEOUT
        };
    let response = if refresh_caller(stream, caller)?.access().map_err(Client)? != 0 {
        StatusResponse::failure(
            request,
            "Another Windows session currently controls OpenBlade hardware.",
            None,
        )
    } else if request.requests_live_readings() && !cfg!(feature = "service-live-readings-v1") {
        StatusResponse::failure(request, "Live readings are unavailable.", None)
    } else {
        let observation = async {
            let observed = {
                let mut source = source.lock().await;
                let caller = refresh_caller(stream, caller)?;
                if caller.access().map_err(Client)? != 0 || Instant::now() >= deadline {
                    return Ok(None);
                }
                request.scope_live_readings(|| source.observe_performance(caller, deadline))
            };
            // EOF or another request cancels this single-request connection.
            let mut probe = [0];
            tokio::select! {
                biased;
                disconnected = stream.read(&mut probe) => {
                    disconnected.map_err(Client)?;
                    Err(Client(io::ErrorKind::BrokenPipe.into()))
                }
                observed = observed => match observed {
                    #[cfg(feature = "service-thermal-mutation-v1")]
                    Err(error) if thermal_mutation::is_fatal_admission(&error) => Err(Runtime(error)),
                    other => Ok(other.ok().flatten()),
                },
            }
        };
        let status = timeout_at(deadline, observation)
            .await
            .unwrap_or(Ok(None))?;
        let access = refresh_caller(stream, caller)?.access().map_err(Client)?;
        if let Some(status) = status.filter(|status| {
            Instant::now() < deadline
                && access == 0
                && (status.has_fresh_mailbox_read() || status.confirmed_performance().is_some())
        }) {
            source.lock().await.verify_origin().map_err(Runtime)?;
            status.verify(access).map_err(Runtime)?;
            #[cfg(feature = "service-thermal-mutation-v1")]
            let status = project_private_status(stream, caller, &status)?;
            StatusResponse::success(request, status)
        } else {
            StatusResponse::failure(
                request,
                "Fresh performance observation is unavailable.",
                None,
            )
        }
    };
    write_payload(stream, &encode(&response).map_err(Runtime)?)
        .await
        .map_err(Client)
}

enum Inbound {
    Served(StatusRequest),
    Unserved(UnservedRequest),
}

async fn read_frame(stream: &mut dyn Stream) -> Result<(Vec<u8>, Caller), ExchangeError> {
    use ExchangeError::Client;
    let payload = timeout(FRAME_TIMEOUT, read_payload(&mut *stream))
        .await
        .map_err(|e| Client(e.into()))?
        .map_err(Client)?;
    // Windows impersonation uses the token from the last read on this handle.
    let caller = stream.caller_after_read().map_err(Client)?;
    Ok((payload, caller))
}

// A served operation keeps its typed decoder and its error. Only a request
// that names another managed operation reaches the unsupported reply.
async fn read_request(stream: &mut dyn Stream) -> Result<(Inbound, Caller), ExchangeError> {
    let (payload, caller) = read_frame(stream).await?;
    let request = match StatusRequest::decode(&payload) {
        Ok(request) => Inbound::Served(request),
        Err(error) => UnservedRequest::decode(&payload)
            .map(Inbound::Unserved)
            .map_err(|_| ExchangeError::Client(error))?,
    };
    Ok((request, caller))
}

// Managed's unsupported-operation reply. It reads no source state, takes no
// device or storage authority and needs no session control, so any admitted
// caller receives it; a caller the status endpoint refuses still closes.
async fn serve_unserved(
    stream: &mut dyn Stream,
    request: &UnservedRequest,
    caller: Caller,
    guard: &dyn Fn() -> io::Result<()>,
) -> Result<(), ExchangeError> {
    use ExchangeError::{Client, Runtime};
    caller.access().map_err(Client)?;
    let payload = encode(&StatusResponse::unsupported(request)).map_err(Runtime)?;
    guard().map_err(Runtime)?;
    write_payload(stream, &payload).await.map_err(Client)?;
    guard().map_err(Runtime)
}

fn resolve_caller(stream: &dyn Stream, mut caller: Caller) -> Result<Caller, ExchangeError> {
    if caller.logon == Logon::Interactive {
        // Failure of the admitted session authority is a runtime failure,
        // not an inactive session or a bad client.
        caller.control_session = stream.control_session().map_err(ExchangeError::Runtime)?;
    }
    Ok(caller)
}

fn refresh_caller(stream: &dyn Stream, original: Caller) -> Result<Caller, ExchangeError> {
    let peer = stream.caller_after_read().map_err(ExchangeError::Client)?;
    if original.pid.is_none()
        || peer.pid != original.pid
        || peer.session != original.session
        || peer.logon != original.logon
    {
        return Err(ExchangeError::Client(denied(
            "installed status peer changed",
        )));
    }
    resolve_caller(stream, peer)
}

async fn serve_get(
    source: &mut dyn StatusSource,
    stream: &mut dyn Stream,
    request: &StatusRequest,
    caller: Caller,
) -> Result<(), ExchangeError> {
    source.verify_origin().map_err(ExchangeError::Runtime)?;
    let caller = resolve_caller(stream, caller)?;
    let payload = status_payload(source, request, caller, Instant::now(), Some(stream)).await?;
    source.verify_origin().map_err(ExchangeError::Runtime)?;
    crate::service_frame::write_payload(stream, &payload)
        .await
        .map_err(ExchangeError::Client)?;
    source.verify_origin().map_err(ExchangeError::Runtime)
}

async fn status_payload(
    source: &mut dyn StatusSource,
    request: &StatusRequest,
    caller: Caller,
    requested: Instant,
    stream: Option<&dyn Stream>,
) -> Result<Vec<u8>, ExchangeError> {
    use ExchangeError::{Client, Runtime};
    let access = caller.access().map_err(Client)?;
    let status = timeout(FRAME_TIMEOUT, source.snapshot(caller, requested))
        .await
        .map_err(|e| Runtime(e.into()))?
        .map_err(Runtime)?;
    status.verify(access).map_err(Runtime)?;
    #[cfg(feature = "service-thermal-mutation-v1")]
    let status = match stream {
        Some(stream) => project_private_status(stream, caller, &status)?,
        None => status.project_private(None),
    };
    #[cfg(not(feature = "service-thermal-mutation-v1"))]
    let _ = stream;
    let response = StatusResponse::success(request, status);
    // Encoding errors (including oversized source data) are runtime failures,
    // while an individual client disconnect or write timeout is isolated.
    crate::service_frame::encode(&response).map_err(Runtime)
}

#[cfg(test)]
#[path = "readiness_tests.rs"]
mod tests;

// Offline compile probe: metadata must fail to resolve BOTH real constructors.
// Hardware compile-only composition must type-check it; neither probe is run.
#[cfg(openblade_metadata_acquisition_probe)]
fn acquisition_exclusion_probe(package: crate::candidate_receipt::CommittedServiceImage) {
    let _ = FirmwareSource::native(1, None, |_| None);
    let _ = FirmwareSource::native_with_package(1, None, package, |_| None);
    let _ = crate::discovery::performance::native_worker(|| None);
}

/// Exchanges (a send and its reply) in the longest thermal request: up to 4
/// for the status snapshot, then 17 for Custom from a Custom baseline (the
/// observation with its levels 4, the 07/8C read 1, four setters with their
/// power reads 8, the final pair with its levels 4). The longest fan request
/// has 17 as well: a return to Automatic with Custom, or a manual entry with
/// its intermediate pair.
#[cfg(feature = "service-thermal-mutation-v1")]
const RECIPE_EXCHANGES: u32 = 21;
/// The fan recipe's fixed spacing: 210 ms after an entry state pair and 65 ms
/// between target commands.
#[cfg(feature = "service-thermal-mutation-v1")]
const RECIPE_WAITS: Duration = Duration::from_millis(275);
/// The time `FRAME_TIMEOUT` leaves each exchange of the request it was sized
/// for: a preset pair from a Custom baseline, 4 snapshot and 11 recipe
/// exchanges.
#[cfg(feature = "service-thermal-mutation-v1")]
const FRAME_EXCHANGE: Duration = Duration::from_nanos(FRAME_TIMEOUT.as_nanos() as u64 / 15);
/// A performance or fan request's budget: the 6 s of a battery request. It
/// gives the longest recipe twice the time per exchange `FRAME_TIMEOUT` gave a
/// preset request, after its fixed spacing. Each operation still refuses
/// before its first setter's intent row when its measured pace cannot finish
/// before the deadline (`Pace`): the remaining exchanges at the measured
/// reply-to-reply intervals, which include each checkpoint's authorizations,
/// custody check and origin verification, the remaining journal rows at the
/// timed Prepared append, and the fixed spacing. Retirement after the closing
/// row is not charged.
#[cfg(feature = "service-thermal-mutation-v1")]
const RECIPE_BUDGET: Duration = Duration::from_secs(6);
#[cfg(feature = "service-thermal-mutation-v1")]
const _: () = assert!(
    RECIPE_BUDGET.as_nanos()
        >= RECIPE_WAITS.as_nanos() + 2 * FRAME_EXCHANGE.as_nanos() * RECIPE_EXCHANGES as u128
);
/// Exchanges in the longest reconciliation of saved intent: the request's 21,
/// then a fan step of up to 17 under the same deadline. A preset and a
/// manual fan entry each fit the longest recipe, so both steps together are
/// 38 exchanges and the fixed spacing of one fan recipe.
#[cfg(feature = "service-profile-triggers-v1")]
const RECONCILE_EXCHANGES: u32 = RECIPE_EXCHANGES + 17;
/// The 11 s reconciliation budget (`RECONCILE_BUDGET`) gives those 38
/// exchanges the time per exchange a performance request has: 2 x 133.3 ms
/// x 38 + 275 ms is about 10.41 s. Each step still refuses before its first
/// setter when its measured pace cannot finish before the shared deadline.
#[cfg(feature = "service-profile-triggers-v1")]
const _: () = assert!(
    crate::service_frame::RECONCILE_BUDGET.as_nanos()
        >= RECIPE_WAITS.as_nanos() + 2 * FRAME_EXCHANGE.as_nanos() * RECONCILE_EXCHANGES as u128
);

/// The deadline a mutation request runs under, from its arrival.
#[cfg(feature = "service-thermal-mutation-v1")]
fn mutation_budget(request: &StatusRequest) -> Duration {
    #[cfg(feature = "service-profile-triggers-v1")]
    if reconciles(request) {
        return crate::service_frame::RECONCILE_BUDGET;
    }
    // A policy request writes no hardware, but it waits for the operation
    // gate, which a thermal request holds for up to its own recipe budget.
    #[cfg(feature = "service-profile-triggers-v1")]
    if matches!(
        request,
        StatusRequest::SetThermalControlPolicy { .. }
            | StatusRequest::AcknowledgeThermalPolicyUpgradeDisclosure { .. }
    ) {
        return RECIPE_BUDGET;
    }
    // So does a Fan Stop request, which writes no hardware either.
    #[cfg(feature = "service-fan-stop-v1")]
    if matches!(
        request,
        StatusRequest::SetFanStopMode { .. }
            | StatusRequest::SetFanStopSettings { .. }
            | StatusRequest::ResumeAutomaticFanStop { .. }
            | StatusRequest::SetBatterySaverFanStop { .. }
    ) {
        return RECIPE_BUDGET;
    }
    // A display-brightness usage writes no thermal state, but it waits for
    // the same gate, and its lane bounds each submission within this budget
    // (`display_brightness_hid::submit`).
    #[cfg(feature = "service-brightness-hid-usage-v1")]
    if matches!(request, StatusRequest::SetDisplayBrightnessHidUsage { .. }) {
        return RECIPE_BUDGET;
    }
    // And the HyperBoost recovery request, which reads managed's journal
    // only.
    #[cfg(feature = "service-hyperboost-v1")]
    if matches!(request, StatusRequest::RecoverHyperBoostRestore { .. }) {
        return RECIPE_BUDGET;
    }
    if request.battery_request().is_some()
        || matches!(
            request,
            StatusRequest::SetPerformanceOverride { .. }
                | StatusRequest::SetFanOverride { .. }
                | StatusRequest::CyclePerformanceShortcut { .. }
                | StatusRequest::SetHyperBoost { .. }
                | StatusRequest::SetLightingOverride { .. }
                | StatusRequest::StepKeyboardBrightness { .. }
        )
    {
        RECIPE_BUDGET
    } else {
        FRAME_TIMEOUT
    }
}

/// A request that may reconcile saved intent after it has committed
/// (`service-profile-triggers-v1`). Its runtime bounds itself instead of the
/// outer wait: it refuses before committing once the deadline has passed,
/// after committing each step refuses at its measured pace, and a step still
/// waiting on a transfer `RECONCILE_MARGIN` past the deadline is dropped with
/// the drop safety of an explicit request (`Runtime::reconcile_locked`). A
/// late result is still answered rather than dropped with the connection.
#[cfg(feature = "service-thermal-mutation-v1")]
fn reconciles(request: &StatusRequest) -> bool {
    cfg!(feature = "service-profile-triggers-v1")
        && matches!(
            request,
            StatusRequest::ReportApplicationContext { .. }
                | StatusRequest::UpdateProfiles { .. }
                | StatusRequest::ReapplyChangedSettings { .. }
        )
}

#[cfg(feature = "service-thermal-mutation-v1")]
async fn serve_mutation(
    stream: &mut dyn Stream,
    request: &StatusRequest,
    original: Caller,
    source: &Mutex<Box<dyn StatusSource>>,
) -> Result<(), ExchangeError> {
    use ExchangeError::{Client, Runtime};
    let caller = refresh_caller(stream, original)?;
    // A caller the endpoint admits no access to is the client's fault and
    // refuses here, before the snapshot would raise the same refusal as a
    // runtime failure that stops the Service.
    caller.access().map_err(Client)?;
    let sid = stream.user_sid_after_read().map_err(Client)?;
    let deadline = Instant::now() + mutation_budget(request);
    let future = {
        let mut source = source.lock().await;
        source.verify_origin().map_err(Runtime)?;
        // A display-brightness request observes nothing first: managed answers
        // a repeat of the held usage and a release before its device check,
        // and a press takes its own fresh admission before it writes
        // (`Runtime::set_display_brightness_hid_usage`). The Session Agent
        // repeats a held key at about 30 Hz, so an observation per repeat
        // would queue the key-up behind them.
        if !matches!(request, StatusRequest::SetDisplayBrightnessHidUsage { .. }) {
            timeout_at(deadline, source.snapshot(caller, Instant::now()))
                .await
                .map_err(|e| Runtime(e.into()))?
                .map_err(Runtime)?;
        }
        source.mutate(caller, sid.clone(), request.clone(), deadline)
    };
    // A request that may reconcile after committing is never canceled at its
    // deadline: its runtime bounds itself (`reconciles`).
    let bounded = async {
        if reconciles(request) {
            Ok(future.await)
        } else {
            timeout_at(deadline, future).await
        }
    };
    let mut probe = [0];
    let (outcome, status) = tokio::select! {
        biased;
        disconnected=stream.read(&mut probe)=>{disconnected.map_err(Client)?;return Err(Client(io::ErrorKind::BrokenPipe.into()));}
        result=bounded=>result.map_err(|e|Client(e.into()))?.map_err(Runtime)?,
    };
    // The deadline bounds the wait above and each runtime's own admission.
    // A mutation that has returned is answered even when it finished late:
    // it may have stored or submitted, and the caller must learn the outcome
    // rather than a closed connection.
    let current = refresh_caller(stream, original)?;
    if current.access().map_err(Client)? != 0
        || stream.user_sid_after_read().map_err(Client)? != sid
    {
        return Err(Client(io::ErrorKind::PermissionDenied.into()));
    }
    source.lock().await.verify_origin().map_err(Runtime)?;
    if let Some(status) = &status {
        status.verify(0).map_err(Runtime)?;
    }
    let response = StatusResponse::mutation(
        request,
        serde_json::to_value(outcome).map_err(|e| Runtime(e.into()))?,
    );
    write_payload(stream, &encode(&response).map_err(Runtime)?)
        .await
        .map_err(Client)?;
    source.lock().await.verify_origin().map_err(Runtime)
}

#[cfg(feature = "service-thermal-mutation-v1")]
fn project_private_status(
    stream: &dyn Stream,
    caller: Caller,
    status: &Snapshot,
) -> Result<Snapshot, ExchangeError> {
    let current = refresh_caller(stream, caller)?;
    let sid = if current.logon == Logon::Interactive
        && current.access().map_err(ExchangeError::Client)? == 0
    {
        Some(
            stream
                .user_sid_after_read()
                .map_err(ExchangeError::Client)?,
        )
    } else {
        None
    };
    Ok(status.project_private(sid.as_deref()))
}

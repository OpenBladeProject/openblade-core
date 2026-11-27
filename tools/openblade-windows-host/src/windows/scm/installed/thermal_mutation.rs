//! One installed request composition. No fake or scenario transport is imported.
#[cfg(feature = "service-profile-triggers-v1")]
use super::thermal_drift;
#[cfg(feature = "service-gpu-clock-tuning-v1")]
use super::thermal_gpu;
#[cfg(feature = "service-fan-mutation-v2")]
use super::thermal_native::CleanupGuard;
#[cfg(feature = "service-profile-triggers-v1")]
use super::thermal_native::StorageGuard;
#[cfg(not(test))]
use super::thermal_native::{CustodialFiles, ProtectedFiles};
use super::{
    Caller, Logon,
    mailbox_read::{CleanupScope, MailboxSource, ReadCustody},
    thermal_native::{SettleGuard, WindowsGuard},
    thermal_storage::{Foreground, Override, Storage},
};
#[cfg(not(test))]
use crate::loaded_origin::Origin;
use crate::{
    discovery::performance::{
        ObservedPerformance, ReadAdmission,
        mutation::{Permit, Refusal, SettledPins, Settlement},
    },
    service_status::{ReadConfirmation, Snapshot},
};
use openblade_domain::{
    automation::UNCLASSIFIED_EXTERNAL,
    profile_set::application::Resolution,
    thermal_mutation::{CUSTOM, Destination, Preset, Progress, confirms},
};
use sha2::{Digest, Sha256};
use std::{
    cell::RefCell,
    io,
    rc::Rc,
    sync::{
        Arc, Mutex, OnceLock,
        atomic::{AtomicBool, Ordering},
    },
};
use tokio::time::Instant;

#[derive(Debug)]
struct FatalAdmission(&'static str);
impl std::fmt::Display for FatalAdmission {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(self.0)
    }
}
impl std::error::Error for FatalAdmission {}
pub(super) fn fatal_admission(message: &'static str) -> io::Error {
    io::Error::other(FatalAdmission(message))
}

pub(super) fn is_fatal_admission(error: &io::Error) -> bool {
    error
        .get_ref()
        .is_some_and(|cause| cause.is::<FatalAdmission>())
}

/// A write capture refused only because fresh competing-controller absence
/// was not confirmed: a controller is present, or detection failed or ran
/// too long (`ReadOnlyAdmission::confirms_fresh_absence`). It is
/// `PermissionDenied` with the ordinary refusal message, so a caller that
/// does not ask for it treats it as any other refusal.
#[derive(Debug)]
struct CompetingController;
impl std::fmt::Display for CompetingController {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str("fresh installed mutation admission is unavailable")
    }
}
impl std::error::Error for CompetingController {}
// Built by the native guard, and by the trigger rig in tests.
#[cfg_attr(test, allow(dead_code))]
pub(super) fn competing_controller() -> io::Error {
    io::Error::new(io::ErrorKind::PermissionDenied, CompetingController)
}
#[cfg(any(
    feature = "service-profile-triggers-v1",
    feature = "service-lighting-v1"
))]
pub(super) fn is_competing_controller(error: &io::Error) -> bool {
    error
        .get_ref()
        .is_some_and(|cause| cause.is::<CompetingController>())
}

pub(super) fn verify_retained_failure(fatal: &Mutex<Option<&'static str>>) -> io::Result<()> {
    match *fatal
        .lock()
        .map_err(|_| fatal_admission("Thermal fatal evidence lock poisoned."))?
    {
        Some(message) => Err(fatal_admission(message)),
        None => Ok(()),
    }
}

pub(super) fn remember_failure(fatal: &Mutex<Option<&'static str>>, error: &io::Error) {
    if let Some(cause) = error
        .get_ref()
        .and_then(|cause| cause.downcast_ref::<FatalAdmission>())
        && let Ok(mut retained) = fatal.lock()
    {
        retained.get_or_insert(cause.0);
    }
}

#[path = "battery_mutation.rs"]
mod battery;
#[cfg(feature = "service-temporary-charge-v1")]
pub(super) use battery::BatteryUpdate;
#[cfg(feature = "service-thermal-convergence-v1")]
pub(super) use triggers::convergence_loop::OBSERVE_BUDGET as CONVERGENCE_OBSERVE_BUDGET;
#[cfg(feature = "service-brightness-hid-usage-v1")]
#[path = "display_brightness_mutation.rs"]
mod brightness;
#[path = "fan_mutation.rs"]
mod fan;
#[cfg(feature = "service-fan-stop-v1")]
#[path = "fan_stop_mutation.rs"]
mod fan_stop;
#[cfg(feature = "service-brightness-hid-usage-v1")]
pub(super) use brightness::{
    SHUTDOWN_BUDGET as BRIGHTNESS_SHUTDOWN_BUDGET, availability as brightness_availability,
    hid as brightness_hid,
};
#[cfg(feature = "service-performance-shortcut-v1")]
#[path = "performance_shortcut.rs"]
mod shortcut;
pub(super) use fan::FanRequest;
#[cfg(feature = "service-fan-stop-v1")]
pub(super) use fan_stop::FanStopRequest;
#[cfg(feature = "service-performance-shortcut-v1")]
pub(super) use shortcut::Source as ShortcutSource;
#[cfg(feature = "service-profile-triggers-v1")]
#[path = "thermal_convergence.rs"]
mod convergence;
#[cfg(feature = "service-hyperboost-v1")]
#[path = "hyperboost_mutation.rs"]
mod hyperboost;
#[cfg(any(
    feature = "service-thermal-convergence-v1",
    feature = "service-hyperboost-v1"
))]
#[path = "hyperboost_journal.rs"]
mod hyperboost_journal;
#[cfg(feature = "service-keyboard-driver-mode-v1")]
#[path = "driver_mode_owner.rs"]
mod keyboard;
#[cfg(feature = "service-lighting-v1")]
#[path = "lighting_mutation.rs"]
mod lighting;
#[cfg(feature = "service-lighting-v1")]
pub(super) use lighting::{LightingRequest, logo_wire};
#[cfg(all(feature = "service-lighting-v1", feature = "service-profiles-v1"))]
pub(super) use lighting::{StepRequest, step_lifetime};
#[cfg(feature = "service-keyboard-driver-mode-v1")]
#[path = "low_power_cleanup.rs"]
mod low_power;
#[cfg(feature = "service-profile-triggers-v1")]
#[path = "thermal_policy_mutation.rs"]
mod policy;
#[cfg(feature = "service-profiles-v1")]
#[path = "profile_mutation.rs"]
mod profiles;
#[cfg(feature = "service-profile-triggers-v1")]
#[path = "request_triggers.rs"]
mod triggers;
#[cfg(all(test, feature = "service-hyperboost-v1"))]
pub(super) use hyperboost::Pad;
#[cfg(all(
    test,
    feature = "service-hyperboost-v1",
    feature = "service-profile-triggers-v1",
    feature = "service-keyboard-driver-mode-v1"
))]
pub(super) use hyperboost::cleanup::CleanupExit as HyperBoostCleanupExit;
#[cfg(feature = "service-hyperboost-v1")]
pub(super) use hyperboost::{MODEL as HYPERBOOST_MODEL, Probes};
#[cfg(feature = "service-keyboard-driver-mode-v1")]
pub(super) use keyboard::SHUTDOWN_BUDGET as KEYBOARD_SHUTDOWN_BUDGET;
#[cfg(all(test, feature = "service-keyboard-driver-mode-v1"))]
pub(super) use keyboard::{Boundary, INTERVAL, Release};
#[cfg(all(test, feature = "service-keyboard-driver-mode-v1"))]
pub(super) use low_power::MAXIMUM_AGE as LOW_POWER_MAXIMUM_AGE;
#[cfg(all(test, feature = "service-thermal-convergence-v1"))]
pub(super) use triggers::reapply::{
    FANS_BRIDGE, IN_FLIGHT, NOT_RETIRED, PENDING, PERFORMANCE_BRIDGE, UNCLASSIFIED,
};
#[cfg(all(test, feature = "service-profile-triggers-v1"))]
pub(super) use triggers::{
    APPLICATION_SESSION_REQUIRED, LOW_POWER_DEFERRED, LOW_POWER_PERFORMANCE, LOW_POWER_STEP,
    fans_first, is_step_request, operation_status,
};
pub(super) trait Fresh: Send + Sync {
    /// The fresh Windows power kind: `Some(false)` is AC (ACLineStatus
    /// online), `Some(true)` battery and `None` unknown. It is never wattage
    /// evidence.
    fn on_battery(&self) -> Option<bool> {
        None
    }
    /// The Windows power generation after a fresh sample of the power kind:
    /// it advances only when the ACLineStatus kind changes (managed
    /// `windowsPowerGeneration`), never on a lock, an unlock or a notice
    /// that keeps the kind.
    fn power_generation(&self) -> Option<u64> {
        None
    }
    fn authentication(&self) -> Option<(u32, i32)> {
        None
    }
    fn authorize_owned_stop(&self) -> Option<ReadAdmission> {
        None
    }
    fn verify_health(&self) -> io::Result<()> {
        Ok(())
    }
    fn authorize(&self) -> Option<ReadAdmission>;
    /// One animated stream frame's admission (`Runtime::stream_frame`):
    /// every check of `authorize` but the competing-controller inventory,
    /// whose fresh reading the origin tick confirms instead. `None`, the
    /// default, admits no frame.
    #[cfg(feature = "service-lighting-v1")]
    fn authorize_frame(&self) -> Option<ReadAdmission> {
        None
    }
    /// Whether storing request intent is admitted now: every check of
    /// `authorize` except the lifecycle epoch, so the low-power gate does not
    /// refuse it (`WindowsGuard::authorize_storage`). It admits no read or
    /// write; only `authorize` does.
    #[cfg(feature = "service-profile-triggers-v1")]
    fn authorize_storage(&self) -> bool {
        false
    }
    /// Settle-only Stop admission for this owner's exact identity at the
    /// current lifecycle epoch (`WindowsGuard::capture_settle`).
    fn capture_settle(
        &self,
        _sid: &str,
        _session: u32,
        _authentication: Option<(u32, i32)>,
    ) -> io::Result<Arc<dyn Settle>> {
        Err(io::ErrorKind::PermissionDenied.into())
    }
}
/// A settle capture (`Fresh::capture_settle`): owned-Stop reads only. It is
/// not a `Fresh`, so it cannot authorize a request or a setter: every request
/// permit, the keyboard Driver-mode owner's included, holds an
/// `Arc<dyn Fresh>`, and only the fan handback's settle permit, which appends
/// nothing but the owner's 51 row, holds this.
pub(super) trait Settle: Send + Sync {
    fn authorize_owned_stop(&self) -> Option<ReadAdmission>;
    fn verify_health(&self) -> io::Result<()>;
}
impl Settle for SettleGuard {
    fn authorize_owned_stop(&self) -> Option<ReadAdmission> {
        self.authorize_owned_stop()
    }
    fn verify_health(&self) -> io::Result<()> {
        self.verify_health()
    }
}
/// The Service's own owned-cleanup admission (`CleanupGuard::capture`). It
/// needs no signed-in console user: Windows logs every user off before a
/// system shutdown reaches services, and a suspend may come from the lock
/// screen. Managed releases with nobody signed in too
/// (`ServiceControlRuntime.ReleaseDriverModeCoreAsync` and its
/// `FreshLightingCleanupAdmission`). Every authorization is fresh: origin
/// trust for its scope, the lifecycle epoch it pinned under the gate's
/// admission of that scope, a known AC line status, fresh competing-
/// controller absence, and the exact original mailbox custody and generation.
///
/// It is not a `Fresh` or a `Settle`, so no request, reconciliation, acquire
/// or settle permit can hold one. Exactly three permits do, and each fixes
/// what it may write: with the keyboard owner, its release (`keyboard::DriverModePermit::release`,
/// Normal after a fresh read of Driver), the owned fan handback
/// (`fan::FanHandbackPermit`, the owner's own Automatic cleanup stages after a
/// fresh read of its manual state) and, with owned HyperBoost and the
/// lifecycle triggers, its exit (`hyperboost::cleanup`, managed's Balanced
/// restore after a fresh read of HyperBoost the journal says native owns).
/// None resolves or applies saved intent.
#[cfg(feature = "service-fan-mutation-v2")]
pub(super) trait Cleanup: Send + Sync {
    fn scope(&self) -> CleanupScope;
    fn authorize(&self) -> Option<ReadAdmission>;
    /// The fresh Windows power kind, as `Fresh::on_battery` reads it.
    fn on_battery(&self) -> Option<bool>;
    fn verify_health(&self) -> io::Result<()>;
}
#[cfg(feature = "service-fan-mutation-v2")]
impl Cleanup for CleanupGuard {
    fn scope(&self) -> CleanupScope {
        self.scope()
    }
    fn authorize(&self) -> Option<ReadAdmission> {
        self.authorize()
    }
    fn on_battery(&self) -> Option<bool> {
        self.on_battery()
    }
    fn verify_health(&self) -> io::Result<()> {
        self.verify_health()
    }
}
/// The age of a gate transition's QPC timestamp (`Lifecycle::age`).
#[cfg(feature = "service-keyboard-driver-mode-v1")]
type NoticeAge = Box<dyn Fn(i64) -> Option<std::time::Duration>>;
/// Captures the Service's cleanup admission over a mailbox custody
/// (`CleanupGuard::capture`).
#[cfg(feature = "service-fan-mutation-v2")]
type CleanupFactory =
    Box<dyn Fn(Arc<dyn ReadCustody>, CleanupScope) -> io::Result<Arc<dyn Cleanup>>>;
impl Fresh for WindowsGuard {
    #[cfg(feature = "service-lighting-v1")]
    fn authorize_frame(&self) -> Option<ReadAdmission> {
        self.authorize_frame()
    }
    fn capture_settle(
        &self,
        sid: &str,
        session: u32,
        authentication: Option<(u32, i32)>,
    ) -> io::Result<Arc<dyn Settle>> {
        Ok(self.capture_settle(sid, session, authentication)?)
    }
    fn on_battery(&self) -> Option<bool> {
        self.on_battery()
    }
    fn power_generation(&self) -> Option<u64> {
        self.power_generation()
    }
    fn authentication(&self) -> Option<(u32, i32)> {
        Some(self.authentication())
    }
    fn authorize_owned_stop(&self) -> Option<ReadAdmission> {
        self.authorize_owned_stop()
    }
    fn verify_health(&self) -> io::Result<()> {
        self.verify_health()
    }
    fn authorize(&self) -> Option<ReadAdmission> {
        self.authorize()
    }
    #[cfg(feature = "service-profile-triggers-v1")]
    fn authorize_storage(&self) -> bool {
        self.authorize_storage()
    }
}
#[cfg(feature = "service-profile-triggers-v1")]
impl Fresh for StorageGuard {
    fn on_battery(&self) -> Option<bool> {
        self.on_battery()
    }
    fn power_generation(&self) -> Option<u64> {
        self.power_generation()
    }
    fn authentication(&self) -> Option<(u32, i32)> {
        Some(self.authentication())
    }
    fn verify_health(&self) -> io::Result<()> {
        self.verify_health()
    }
    // Never a read or write: a storage capture pins no lifecycle epoch.
    fn authorize(&self) -> Option<ReadAdmission> {
        None
    }
    fn authorize_storage(&self) -> bool {
        self.authorize_storage()
    }
}
/// What one request capture admits (`Runtime::capture_scoped`).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(super) enum Capture {
    /// Reads and writes at the lifecycle epoch it pins
    /// (`WindowsGuard::capture`).
    Write,
    /// Storing request intent only, in any lifecycle state
    /// (`WindowsGuard::capture_storage`): its `Fresh` never authorizes a
    /// read or write.
    #[cfg(feature = "service-profile-triggers-v1")]
    Storage,
}
type Factory =
    dyn Fn(&str, u32, Arc<dyn ReadCustody>, Capture) -> io::Result<(Arc<Storage>, Arc<dyn Fresh>)>;
type ReleaseNotice = (
    Arc<battery::BatteryPermit>,
    Option<super::thermal_storage::ReleasePause>,
);
#[cfg(test)]
type ReleaseHook = Rc<dyn Fn()>;
/// Reads the lifecycle's low-power gate transition (`Lifecycle::low_power`).
#[cfg(any(
    feature = "service-profile-triggers-v1",
    feature = "service-keyboard-driver-mode-v1"
))]
type LowPowerProbe = Box<dyn Fn() -> Option<openblade_domain::low_power_gate::Transition>>;
/// Reads the lifecycle's Windows power generation without sampling it
/// (`Lifecycle::observe_power_kind(None)`); every SCM power notice samples.
#[cfg(feature = "service-profile-triggers-v1")]
type PowerProbe = Box<dyn Fn() -> Option<u64>>;
/// Reads the console session and its primary user's SID
/// (`thermal_native::console_user`); `None` while none is known.
#[cfg(any(
    feature = "service-profile-triggers-v1",
    feature = "service-keyboard-driver-mode-v1"
))]
type ConsoleProbe = Box<dyn Fn() -> Option<(String, u32)>>;
/// How long a foreground report holds without renewal (managed
/// `ApplicationContextLeaseDuration`).
pub(super) const APPLICATION_LEASE: std::time::Duration = std::time::Duration::from_secs(15);
#[derive(Clone)]
pub(super) struct Runtime {
    factory: Rc<Factory>,
    application: Rc<RefCell<Option<Application>>>,
    // What each `(sid, session)` pair owes a reconciliation of its saved
    // intent (`triggers::Debt`); a pair without an entry owes its startup one.
    #[cfg(feature = "service-profile-triggers-v1")]
    debts: Rc<RefCell<std::collections::HashMap<(String, u32), triggers::Debt>>>,
    // Each user's startup reconciliation, keyed on the SID as managed keys
    // activation (`triggers::Startup`). A user without an entry is starting
    // and has compared nothing; until it ends, the user's reconciliation
    // applies over confirmed drift (`thermal_drift::Mode`).
    #[cfg(feature = "service-profile-triggers-v1")]
    startup: Rc<RefCell<std::collections::HashMap<String, triggers::Startup>>>,
    // The lifecycle's low-power gate transition, `None` while the lifecycle
    // is unreadable or exhausted. A reconciliation reads it to stop before a
    // step and to classify a step's refusal (`triggers::Gate`); every write
    // is still refused by its own admission (`WindowsGuard::authorize_for`).
    // The native composition sets it; only test composition leaves it unset,
    // which reads clear.
    #[cfg(any(
        feature = "service-profile-triggers-v1",
        feature = "service-keyboard-driver-mode-v1"
    ))]
    low_power: Rc<std::cell::OnceCell<LowPowerProbe>>,
    // The Windows power generation, for the lifecycle tick's power-change
    // cause (`triggers::lifecycle`). Unset reads unknown: no power cause.
    #[cfg(feature = "service-profile-triggers-v1")]
    power_generation: Rc<std::cell::OnceCell<PowerProbe>>,
    // What the lifecycle tick last saw, the console user it last activated
    // and the cause it still owes an attempt, `None` before its first tick
    // (`triggers::lifecycle`).
    #[cfg(feature = "service-profile-triggers-v1")]
    lifecycle: Rc<RefCell<Option<triggers::lifecycle::Tick>>>,
    // The console session's interactive user, for the lifecycle tick's pair
    // when no report of that session was admitted and for its activation.
    // Unset reads the reporter and activates no one.
    #[cfg(any(
        feature = "service-profile-triggers-v1",
        feature = "service-keyboard-driver-mode-v1"
    ))]
    console: Rc<std::cell::OnceCell<ConsoleProbe>>,
    // The lifecycle's ordinary write epoch (`Lifecycle::active`), so a step
    // refused by a fence that left the gate generation in place keeps its
    // debt (`reconcile_locked`). Unset reads unknown: no such retry.
    #[cfg(feature = "service-profile-triggers-v1")]
    admission_epoch: Rc<std::cell::OnceCell<PowerProbe>>,
    // The Service's owned-cleanup admission (`Cleanup`), for the fan
    // handback at Stop and shutdown, with the keyboard owner its release
    // there and at low power, and the owned HyperBoost exit at both. Unset
    // admits no cleanup.
    #[cfg(feature = "service-fan-mutation-v2")]
    cleanup: Rc<std::cell::OnceCell<CleanupFactory>>,
    // How long ago a gate transition's QPC timestamp was (`Lifecycle::age`).
    // Unset reads a fresh notice.
    #[cfg(feature = "service-keyboard-driver-mode-v1")]
    notice_age: Rc<std::cell::OnceCell<NoticeAge>>,
    // The low-power preparation's last suspend generation (`low_power`).
    #[cfg(feature = "service-keyboard-driver-mode-v1")]
    low_power_prepared: Rc<low_power::Prepared>,
    // Confirmed thermal drift, shared with the reader that observes it
    // (`thermal_drift`).
    #[cfg(feature = "service-profile-triggers-v1")]
    drift: Arc<Mutex<thermal_drift::Tracker>>,
    // The convergence loop (`triggers::convergence_loop`): set by the native
    // composition, and by tests that run it. Unset runs no loop, and the
    // status publishes what it did before the loop existed.
    #[cfg(feature = "service-thermal-convergence-v1")]
    convergence_loop: Rc<std::cell::OnceCell<triggers::convergence_loop::Loop>>,
    // Owned HyperBoost's read-only probes (`hyperboost::Probes`): set by
    // the native composition and by tests. Unset refuses every HyperBoost
    // request before any read or write.
    #[cfg(feature = "service-hyperboost-v1")]
    hyperboost: Rc<std::cell::OnceCell<hyperboost::Probes>>,
    // GPU clock tuning on the explicit performance path (`thermal_gpu`).
    // The native composition sets it when its worker starts; unset keeps
    // every GPU clock effect refused as incomplete (`Cause::Gpu`). Every
    // thermal writer reads its fence (`Runtime::gpu_fence`).
    #[cfg(feature = "service-gpu-clock-tuning-v1")]
    gpu: Rc<std::cell::OnceCell<Arc<thermal_gpu::Lane>>>,
    // Managed's automatic Fan Stop eligibility (`fan_stop::Automation`).
    #[cfg(feature = "service-fan-stop-v1")]
    fan_stop: Rc<RefCell<fan_stop::Automation>>,
    // The lighting doubt latch, the session layout byte and the owned Static
    // renderer (`lighting::Session`).
    #[cfg(feature = "service-lighting-v1")]
    lighting: Rc<lighting::Session>,
    // Display-brightness HID input and its held usage (`brightness`).
    #[cfg(feature = "service-brightness-hid-usage-v1")]
    brightness: Rc<brightness::Brightness>,
    fan_owner: Rc<RefCell<Option<Arc<fan::FanPermit>>>>,
    temporary_owner: Rc<RefCell<Option<Arc<battery::BatteryPermit>>>>,
    temporary_pending: Rc<RefCell<Option<Arc<battery::BatteryPermit>>>>,
    release_deferred: Rc<std::cell::Cell<Option<super::thermal_storage::ReleasePause>>>,
    release_notice: Rc<RefCell<Option<ReleaseNotice>>>,
    #[cfg(test)]
    release_hook: Rc<RefCell<Option<ReleaseHook>>>,
    #[cfg(feature = "service-temporary-charge-v1")]
    temporary_event: Rc<std::cell::Cell<Option<u64>>>,
    fan_pending: Rc<RefCell<Option<Arc<fan::FanPermit>>>>,
    // Pins of settled fan and temporary-charge records, which the next
    // mutation Reader to retire unpins (`mutation::SettledPins`).
    settled_pins: Arc<SettledPins>,
    // The store of the latest capture of any caller (`capture_scoped`), for
    // the Service's own cleanups, which have no caller to capture one: the
    // keyboard release's journal check and the owned HyperBoost exit's
    // ownership and rows. Every write in the process is captured first, the
    // captures are serialized by the gate, and every store recovers the last
    // row of the one custody journal when it opens, so this store has seen
    // every row, whichever user wrote it.
    #[cfg(any(
        feature = "service-keyboard-driver-mode-v1",
        all(
            feature = "service-hyperboost-v1",
            feature = "service-profile-triggers-v1"
        )
    ))]
    captured: Rc<RefCell<Option<Arc<Storage>>>>,
    // Keyboard Driver-mode ownership, held in memory (`keyboard`).
    #[cfg(feature = "service-keyboard-driver-mode-v1")]
    keyboard: Rc<keyboard::Keyboard>,
    gate: Rc<tokio::sync::Mutex<()>>,
    // Managed `DiagnosticHistory`: what an export reports of this runtime.
    #[cfg(feature = "service-diagnostics-export-v1")]
    diagnostic_history: Arc<crate::diagnostic_export::History>,
    // Public battery history is bounded independently of request/user intent.
    // Only independent reads update its timestamp; sibling publications cannot.
    battery_observation: Arc<Mutex<battery::BatteryObservation>>,
    battery_source: Arc<std::sync::atomic::AtomicU8>,
    battery_warning: Arc<Mutex<Option<String>>>,
    fatal: Arc<Mutex<Option<&'static str>>>,
    #[cfg(test)]
    pub(super) entered: Rc<std::cell::Cell<u64>>,
}
/// The latest admitted foreground report and its lease: the reported path
/// with the active pair (`Foreground`), from which every read of that
/// session's saved policy resolves its profile layer until `expires`.
struct Application {
    sid: String,
    session: u32,
    pid: u32,
    instance: String,
    sequence: i64,
    foreground: Foreground,
    expires: Instant,
}
/// One performance step under the held gate (`Runtime::apply_locked`).
struct ThermalStep {
    caller: Caller,
    sid: String,
    request: String,
    value: Option<Override>,
    // `Some` for a reconciliation of saved intent: the classes it applies at.
    reconcile: Option<Classes>,
    // A reconciliation's drift decision, shared by its steps.
    #[cfg(feature = "service-profile-triggers-v1")]
    transition: Option<Arc<thermal_drift::Transition>>,
    // `Some` for a performance shortcut, whose override `resolve` stages
    // from the fresh reading; `value` is then None.
    #[cfg(feature = "service-performance-shortcut-v1")]
    shortcut: Option<ShortcutSource>,
    // `Some` for an owned HyperBoost request (`hyperboost`), which stores
    // nothing; `value` is then None.
    #[cfg(feature = "service-hyperboost-v1")]
    hyperboost: Option<hyperboost::Step>,
    deadline: Instant,
    status: Snapshot,
}
/// The classified classes a reconciliation applies at. A step whose own
/// adapter reading finds a classified class outside them writes nothing and
/// reports it (`Stepped::unchanged`): only that reading tells the two
/// external classes apart.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(super) struct Classes(u8);
impl Classes {
    pub(super) fn contains(self, class: i32) -> bool {
        (0..=2).contains(&class) && self.0 & (1 << class) != 0
    }
}
/// One step's reply and status projection, and what a reconciliation
/// (`triggers`) reads of it: whether the step found its class outside
/// `Classes` and so wrote nothing, whether its refusal may clear by itself
/// before any submission, and the class its reading found.
pub(super) struct Stepped {
    pub(super) outcome: Outcome,
    pub(super) snapshot: Option<Snapshot>,
    // Only a reconciliation (`triggers`) reads these.
    #[cfg_attr(not(feature = "service-profile-triggers-v1"), allow(dead_code))]
    pub(super) unchanged: bool,
    // Refused before any setter for a cause that may clear by itself: the
    // measured pace (`Refusal::Deadline`), an adapter reading that is not
    // classified yet, or a store or profile read that failed. Whether the step
    // submitted anything is the journal's to say (`Runtime::reconcile_locked`).
    #[cfg_attr(not(feature = "service-profile-triggers-v1"), allow(dead_code))]
    pub(super) transient: bool,
    // The classified class the step's reading found inside its classes.
    #[cfg_attr(not(feature = "service-profile-triggers-v1"), allow(dead_code))]
    pub(super) class: Option<i32>,
    // A reconciliation's drift decision excluded both thermal subsystems
    // (`thermal_drift`): nothing was written, and the other step does not run.
    #[cfg_attr(not(feature = "service-profile-triggers-v1"), allow(dead_code))]
    pub(super) excluded: bool,
}
impl Stepped {
    fn new(outcome: Outcome, snapshot: Option<Snapshot>) -> Self {
        Self {
            outcome,
            snapshot,
            unchanged: false,
            transient: false,
            class: None,
            excluded: false,
        }
    }
    /// A refusal before any firmware access that may clear by itself.
    fn transient(outcome: Outcome) -> Self {
        Self {
            transient: true,
            ..Self::new(outcome, None)
        }
    }
    /// A reconciliation step whose own reading found a classified class its
    /// trigger did not change: nothing was accepted or written.
    fn unchanged() -> Self {
        Self {
            unchanged: true,
            ..Self::new(Outcome::blocked(UNCHANGED_CLASS), None)
        }
    }
    fn reply(self) -> (Outcome, Option<Snapshot>) {
        (self.outcome, self.snapshot)
    }
}
/// Managed `ExecuteScopedMutationAsync` reply for a cleared explicit override
/// on unclassified external power, which selects no power profile.
pub(super) const UNCLASSIFIED_CLEARED: &str =
    "Override cleared. Power-profile application waits for adapter classification.";
/// A reconciliation step whose own adapter reading found a classified class
/// the trigger did not change (`Classes`): nothing was accepted or written.
pub(super) const UNCHANGED_CLASS: &str =
    "Saved intent did not change at the current power source; nothing was written.";
/// A reconciliation step whose status projection failed after its hardware
/// result, without fencing the store (`after_write_unreadable`).
const AFTER_WRITE_UNREADABLE: &str = "Saved configuration could not be read after the hardware result, so no applied state is claimed.";
/// The reply of a reconciliation step whose status projection failed after
/// its hardware result: the store's own reconciliation reason when the read
/// fenced it, and otherwise the unreadable configuration. The hardware result
/// itself is not claimed.
pub(super) fn after_write_unreadable(storage: &Storage) -> io::Result<Outcome> {
    Ok(if storage.pending() {
        Outcome::blocked(storage.pending_reason()?)
    } else {
        Outcome::blocked(AFTER_WRITE_UNREADABLE)
    })
}
/// The request's remaining time cannot cover the recipe at the device pace
/// measured so far (`Refusal::Deadline`): the Prepared row is settled as
/// known not submitted and no setter was submitted.
pub(super) const DEADLINE: &str = "The remaining request time cannot cover this recipe at the measured device pace; no thermal setter was submitted.";
/// Managed `ScopedPerformanceWriteBlockReason` while a thermal confirmation
/// is pending (`ServiceControlRuntime.cs:15788-15793`): an explicit
/// performance request.
pub(super) const PENDING_PERFORMANCE: &str = "A thermal write requires read-only reconciliation before the performance command can be submitted.";
/// Managed `ScopedThermalWriteBlockReason` while a thermal confirmation is
/// pending (`ServiceControlRuntime.cs:15832-15838`): every other thermal
/// writer.
pub(super) const PENDING_OTHER: &str =
    "A thermal write requires read-only reconciliation before another command can be submitted.";
/// A performance step whose fresh admission refused before anything was
/// accepted, read or submitted.
const CURRENT_ADMISSION_UNAVAILABLE: &str =
    "Current thermal admission is unavailable; no intent or command was submitted.";
/// Managed `ValidateUnclassifiedPerformanceOverride`, as `RecordRejectedIntent`
/// reports it (Failed).
const UNCLASSIFIED_REJECTED: &str = "The requested intent was rejected: This request needs classified power or includes native overclocking; ordinary external presence does not admit it.";
/// Managed `ValidateUnclassifiedPerformanceOverride` for Custom settings the
/// model capability does not admit, as `RecordRejectedIntent` reports it.
const UNCLASSIFIED_CUSTOM_REJECTED: &str = "The requested intent was rejected: The complete Custom CPU/GPU and maximum-fan intent is not admitted for this model.";
/// Managed `PublishScopedUnavailable` (Blocked) when an unclassified adapter
/// reading lacks fresh Windows AC presence or the confirmed state is HyperBoost
/// (`CanAdmitUnclassifiedExternalControl`); no intent is stored.
pub(super) const UNCLASSIFIED_BLOCKED: &str = "Hardware application was not confirmed. The external power adapter could not be classified; overrides and control writes are blocked.";
/// The class a status projection reads saved intent at: the base only when
/// fresh Windows power agrees with it (battery, or AC presence for the base's
/// external class), and unknown (-1) otherwise.
pub(super) fn status_query_power(base: i32, fresh: &dyn Fresh) -> i32 {
    match (base, fresh.on_battery()) {
        (1, Some(true)) => 1,
        (base, Some(false)) if base != 1 => base,
        _ => -1,
    }
}
/// The class `thermalConvergence` is derived at from a status projection's
/// `query` (`status_query_power`). Status reports any external power as the
/// coarse class 3, and the projection's own saved intent stays there: coarse
/// metadata claims no resolved application policy. Under fresh AC presence
/// the class this user's last step classified from its own adapter reading
/// (`Storage::observed_power`) stands in for it while the Windows power
/// generation is unchanged, so the member classifies on PluggedIn or USB-C
/// as that step did. No firmware is read here; managed reads the adapter
/// periodically instead (a later slice).
#[cfg(feature = "service-profile-triggers-v1")]
pub(super) fn convergence_power(
    query: i32,
    fresh: &dyn Fresh,
    storage: &Storage,
) -> io::Result<i32> {
    if matches!(query, 1 | -1) || fresh.on_battery() != Some(false) {
        return Ok(query);
    }
    Ok(match fresh.power_generation() {
        Some(generation) => storage
            .observed_power(generation)?
            .filter(|class| matches!(class, 0 | 2))
            .unwrap_or(query),
        None => query,
    })
}
#[derive(Clone, serde::Serialize)]
#[cfg_attr(test, derive(Debug, PartialEq, Eq))]
pub(super) struct Outcome {
    pub(super) status: u8,
    pub(super) message: String,
}
impl Outcome {
    pub(super) fn blocked(message: impl Into<String>) -> Self {
        Self {
            status: 2,
            message: message.into(),
        }
    }
}
impl Runtime {
    /// The history this runtime records into, for the export port.
    #[cfg(feature = "service-diagnostics-export-v1")]
    pub(super) fn diagnostic_history(&self) -> Arc<crate::diagnostic_export::History> {
        Arc::clone(&self.diagnostic_history)
    }

    /// Managed `UpdateProfilesAsync` records each stored save.
    #[cfg(feature = "service-profiles-v1")]
    pub(super) fn record_stored_profiles(&self) {
        #[cfg(feature = "service-diagnostics-export-v1")]
        self.diagnostic_history.record_summary(
            "Stored",
            openblade_domain::profile_set::update::STORED_MESSAGE,
        );
    }
}
impl Runtime {
    #[cfg(test)]
    pub(super) fn injected(
        mailbox: &MailboxSource,
        factory: impl Fn(
            &str,
            u32,
            Arc<dyn ReadCustody>,
            Capture,
        ) -> io::Result<(Arc<Storage>, Arc<dyn Fresh>)>
        + 'static,
    ) -> Self {
        Self {
            factory: Rc::new(factory),
            #[cfg(feature = "service-gpu-clock-tuning-v1")]
            gpu: Rc::default(),
            #[cfg(feature = "service-fan-stop-v1")]
            fan_stop: Rc::default(),
            #[cfg(feature = "service-lighting-v1")]
            lighting: Rc::default(),
            #[cfg(feature = "service-brightness-hid-usage-v1")]
            brightness: Rc::default(),
            application: Rc::default(),
            #[cfg(feature = "service-profile-triggers-v1")]
            debts: Rc::default(),
            #[cfg(feature = "service-profile-triggers-v1")]
            startup: Rc::default(),
            #[cfg(any(
                feature = "service-profile-triggers-v1",
                feature = "service-keyboard-driver-mode-v1"
            ))]
            low_power: Rc::default(),
            #[cfg(feature = "service-profile-triggers-v1")]
            power_generation: Rc::default(),
            #[cfg(feature = "service-profile-triggers-v1")]
            lifecycle: Rc::default(),
            #[cfg(any(
                feature = "service-profile-triggers-v1",
                feature = "service-keyboard-driver-mode-v1"
            ))]
            console: Rc::default(),
            #[cfg(feature = "service-profile-triggers-v1")]
            admission_epoch: Rc::default(),
            #[cfg(feature = "service-fan-mutation-v2")]
            cleanup: Rc::default(),
            #[cfg(feature = "service-keyboard-driver-mode-v1")]
            notice_age: Rc::default(),
            #[cfg(feature = "service-keyboard-driver-mode-v1")]
            low_power_prepared: Rc::default(),
            #[cfg(feature = "service-profile-triggers-v1")]
            drift: mailbox.drift(),
            #[cfg(feature = "service-thermal-convergence-v1")]
            convergence_loop: Rc::default(),
            #[cfg(feature = "service-hyperboost-v1")]
            hyperboost: Rc::default(),
            fan_owner: Rc::default(),
            temporary_owner: Rc::default(),
            temporary_pending: Rc::default(),
            release_deferred: Rc::default(),
            release_notice: Rc::default(),
            #[cfg(test)]
            release_hook: Rc::default(),
            #[cfg(feature = "service-temporary-charge-v1")]
            temporary_event: Rc::default(),
            fan_pending: Rc::default(),
            settled_pins: Arc::default(),
            #[cfg(any(
                feature = "service-keyboard-driver-mode-v1",
                all(
                    feature = "service-hyperboost-v1",
                    feature = "service-profile-triggers-v1"
                )
            ))]
            captured: Rc::default(),
            #[cfg(feature = "service-keyboard-driver-mode-v1")]
            keyboard: Rc::default(),
            gate: Rc::default(),
            #[cfg(feature = "service-diagnostics-export-v1")]
            diagnostic_history: Arc::new(crate::diagnostic_export::History::service()),
            battery_observation: Arc::default(),
            battery_source: Arc::new(std::sync::atomic::AtomicU8::new(3)),
            battery_warning: Arc::default(),
            fatal: mailbox.fatal_evidence(),
            #[cfg(test)]
            entered: Rc::default(),
        }
    }
    /// Reads the low-power gate from `probe` (`Runtime::low_power`). Test
    /// composition wires its rig's lifecycle here; it may be set once.
    #[cfg(all(
        test,
        any(
            feature = "service-profile-triggers-v1",
            feature = "service-keyboard-driver-mode-v1"
        )
    ))]
    pub(super) fn read_low_power(
        &self,
        probe: impl Fn() -> Option<openblade_domain::low_power_gate::Transition> + 'static,
    ) {
        assert!(self.low_power.set(Box::new(probe)).is_ok());
    }
    /// Composes GPU clock tuning over `lane` (`Runtime::gpu`); it may be set
    /// once.
    #[cfg(all(test, feature = "service-gpu-clock-tuning-v1"))]
    pub(super) fn compose_gpu(&self, lane: Arc<thermal_gpu::Lane>) {
        assert!(self.gpu.set(lane).is_ok());
    }
    /// Reads the Windows power generation from `probe`
    /// (`Runtime::power_generation`); it may be set once.
    #[cfg(all(test, feature = "service-profile-triggers-v1"))]
    pub(super) fn read_power_generation(&self, probe: impl Fn() -> Option<u64> + 'static) {
        assert!(self.power_generation.set(Box::new(probe)).is_ok());
    }
    /// Reads the lifecycle's ordinary write epoch from `probe`
    /// (`Runtime::admission_epoch`); it may be set once.
    #[cfg(all(test, feature = "service-profile-triggers-v1"))]
    pub(super) fn read_admission_epoch(&self, probe: impl Fn() -> Option<u64> + 'static) {
        assert!(self.admission_epoch.set(Box::new(probe)).is_ok());
    }
    /// Captures the Service's cleanup admission with `factory`
    /// (`Runtime::cleanup`); it may be set once.
    #[cfg(all(test, feature = "service-fan-mutation-v2"))]
    pub(super) fn read_cleanup(
        &self,
        factory: impl Fn(Arc<dyn ReadCustody>, CleanupScope) -> io::Result<Arc<dyn Cleanup>> + 'static,
    ) {
        assert!(self.cleanup.set(Box::new(factory)).is_ok());
    }
    /// Reads a gate transition's age from `probe` (`Runtime::notice_age`);
    /// it may be set once.
    #[cfg(all(test, feature = "service-keyboard-driver-mode-v1"))]
    pub(super) fn read_notice_age(
        &self,
        probe: impl Fn(i64) -> Option<std::time::Duration> + 'static,
    ) {
        assert!(self.notice_age.set(Box::new(probe)).is_ok());
    }
    /// Reads the console session's user from `probe` (`Runtime::console`);
    /// it may be set once.
    #[cfg(all(
        test,
        any(
            feature = "service-profile-triggers-v1",
            feature = "service-keyboard-driver-mode-v1"
        )
    ))]
    pub(super) fn read_console(&self, probe: impl Fn() -> Option<(String, u32)> + 'static) {
        assert!(self.console.set(Box::new(probe)).is_ok());
    }
    /// Reads owned HyperBoost's pad and journal from `probes`
    /// (`Runtime::hyperboost`); they may be set once.
    #[cfg(all(test, feature = "service-hyperboost-v1"))]
    pub(super) fn read_hyperboost(&self, probes: hyperboost::Probes) {
        assert!(self.hyperboost.set(probes).is_ok());
    }
    #[cfg(not(test))]
    pub(super) fn native(origin: Arc<Origin>, mailbox: &MailboxSource) -> Self {
        let runtime = Self::native_parts(origin, mailbox);
        #[cfg(all(windows, feature = "service-lighting-v1"))]
        runtime.compose_matrix_stream(crate::discovery::performance::native_matrix_stream());
        runtime
    }
    #[cfg(not(test))]
    fn native_parts(origin: Arc<Origin>, mailbox: &MailboxSource) -> Self {
        let stores = RefCell::new(None::<(String, Arc<Storage>)>);
        #[cfg(any(
            feature = "service-profile-triggers-v1",
            feature = "service-keyboard-driver-mode-v1"
        ))]
        let low_power: LowPowerProbe = {
            let origin = origin.clone();
            Box::new(move || origin.lifecycle.low_power())
        };
        #[cfg(feature = "service-profile-triggers-v1")]
        let power_generation: PowerProbe = {
            // The tick reads the generation without sampling, and only a
            // sample that differs from a known kind advances it. Sample the
            // kind once now, so the first power change after start is a
            // change and not the baseline, whether or not a capture or a
            // power notice sampled it first.
            origin
                .lifecycle
                .observe_power_kind(crate::origin_lifetime::sample_power_kind());
            let origin = origin.clone();
            Box::new(move || origin.lifecycle.observe_power_kind(None))
        };
        #[cfg(feature = "service-profile-triggers-v1")]
        let admission_epoch: PowerProbe = {
            let origin = origin.clone();
            Box::new(move || origin.lifecycle.active())
        };
        #[cfg(feature = "service-fan-mutation-v2")]
        let cleanup: CleanupFactory = {
            let origin = origin.clone();
            Box::new(move |custody, scope| {
                Ok(CleanupGuard::capture(origin.clone(), custody, scope)? as Arc<dyn Cleanup>)
            })
        };
        #[cfg(feature = "service-keyboard-driver-mode-v1")]
        let notice_age: NoticeAge = {
            let origin = origin.clone();
            Box::new(move |timestamp| origin.lifecycle.age(timestamp))
        };
        // The preparation runs on this thread's runtime, which the hold never
        // waits on (`origin_lifetime::suspend_hold`).
        #[cfg(feature = "service-keyboard-driver-mode-v1")]
        let low_power_prepared =
            low_power::Prepared::holding(origin.lifecycle.arm_suspend_hold(low_power::MAXIMUM_AGE));
        // A worker that cannot start leaves every GPU clock effect refused as
        // incomplete, as without the feature.
        #[cfg(feature = "service-gpu-clock-tuning-v1")]
        let gpu = {
            let gpu = std::cell::OnceCell::new();
            if let Ok(worker) = super::gpu_clock_offsets::production() {
                let _ = gpu.set(Arc::new(thermal_gpu::Lane::new(worker)));
            }
            Rc::new(gpu)
        };
        #[cfg(feature = "service-diagnostics-export-v1")]
        let diagnostic_history = Arc::new(crate::diagnostic_export::History::service());
        // A press is admitted by the device capture alone: it selects no
        // profile root, so it needs no store (`brightness::Guard`).
        #[cfg(feature = "service-brightness-hid-usage-v1")]
        let brightness = {
            let origin = origin.clone();
            let guard: Box<brightness::Guard> = Box::new(move |sid, session, custody| {
                Ok(
                    WindowsGuard::capture(origin.clone(), custody, sid.to_owned(), session)?
                        as Arc<dyn Fresh>,
                )
            });
            Rc::new(brightness::Brightness::production(
                brightness::production_log(
                    #[cfg(feature = "service-diagnostics-export-v1")]
                    &diagnostic_history,
                ),
                guard,
            ))
        };
        Self {
            factory: Rc::new(move |sid, session, custody, capture| {
                let guard: Arc<dyn Fresh> = match capture {
                    Capture::Write => WindowsGuard::capture(
                        origin.clone(),
                        custody.clone(),
                        sid.to_owned(),
                        session,
                    )?,
                    #[cfg(feature = "service-profile-triggers-v1")]
                    Capture::Storage => WindowsGuard::capture_storage(
                        origin.clone(),
                        custody.clone(),
                        sid.to_owned(),
                        session,
                    )?,
                };
                let mut stores = stores.borrow_mut();
                if stores.as_ref().is_none_or(|(current, _)| current != sid) {
                    // Trusted peer/session inputs are selected before resolving any
                    // product root. Test composition supplies its own factory.
                    if !sid.starts_with("S-1-5-21-")
                        || !sid
                            .bytes()
                            .all(|b| b.is_ascii_digit() || b"S-".contains(&b))
                    {
                        return Err(io::ErrorKind::PermissionDenied.into());
                    }
                    stores.take();
                    let root = crate::service_install_identity::known_folder(
                        &windows_sys::Win32::UI::Shell::FOLDERID_ProgramData,
                    )?
                    .join("OpenBlade")
                    .join("users")
                    .join(sid);
                    *stores = Some((
                        sid.to_owned(),
                        Storage::open_with(
                            Box::new(CustodialFiles {
                                user: ProtectedFiles::open_at(&root)?,
                                custody,
                            }),
                            sid,
                            super::thermal_native::same_path,
                        )?,
                    ));
                }
                Ok((stores.as_ref().unwrap().1.clone(), guard))
            }),
            #[cfg(feature = "service-gpu-clock-tuning-v1")]
            gpu,
            #[cfg(feature = "service-fan-stop-v1")]
            fan_stop: Rc::default(),
            #[cfg(feature = "service-lighting-v1")]
            lighting: Rc::default(),
            #[cfg(feature = "service-brightness-hid-usage-v1")]
            brightness,
            application: Rc::default(),
            #[cfg(feature = "service-profile-triggers-v1")]
            debts: Rc::default(),
            #[cfg(feature = "service-profile-triggers-v1")]
            startup: Rc::default(),
            #[cfg(any(
                feature = "service-profile-triggers-v1",
                feature = "service-keyboard-driver-mode-v1"
            ))]
            low_power: Rc::new(low_power.into()),
            #[cfg(feature = "service-profile-triggers-v1")]
            power_generation: Rc::new(power_generation.into()),
            #[cfg(feature = "service-profile-triggers-v1")]
            lifecycle: Rc::default(),
            #[cfg(any(
                feature = "service-profile-triggers-v1",
                feature = "service-keyboard-driver-mode-v1"
            ))]
            console: Rc::new(std::cell::OnceCell::from(
                Box::new(super::thermal_native::console_user) as ConsoleProbe,
            )),
            #[cfg(feature = "service-profile-triggers-v1")]
            admission_epoch: Rc::new(admission_epoch.into()),
            #[cfg(feature = "service-fan-mutation-v2")]
            cleanup: Rc::new(cleanup.into()),
            #[cfg(feature = "service-keyboard-driver-mode-v1")]
            notice_age: Rc::new(notice_age.into()),
            #[cfg(feature = "service-keyboard-driver-mode-v1")]
            low_power_prepared: Rc::new(low_power_prepared),
            #[cfg(feature = "service-profile-triggers-v1")]
            drift: mailbox.drift(),
            #[cfg(feature = "service-thermal-convergence-v1")]
            convergence_loop: Rc::new(std::cell::OnceCell::from(
                triggers::convergence_loop::Loop::new(Arc::new(hyperboost_journal::read)),
            )),
            // Owned HyperBoost leaves at the battery boundary only through
            // the lifecycle triggers (`hyperboost::Exit`).
            // Without them nothing would end an owned session after an
            // unplug, so no probe is composed and every HyperBoost request
            // replies `hyperboost::UNAVAILABLE` before any read or write.
            #[cfg(all(
                feature = "service-hyperboost-v1",
                feature = "service-profile-triggers-v1"
            ))]
            hyperboost: Rc::new(std::cell::OnceCell::from(hyperboost::Probes::native())),
            #[cfg(all(
                feature = "service-hyperboost-v1",
                not(feature = "service-profile-triggers-v1")
            ))]
            hyperboost: Rc::default(),
            fan_owner: Rc::default(),
            temporary_owner: Rc::default(),
            temporary_pending: Rc::default(),
            release_deferred: Rc::default(),
            release_notice: Rc::default(),
            #[cfg(test)]
            release_hook: Rc::default(),
            #[cfg(feature = "service-temporary-charge-v1")]
            temporary_event: Rc::default(),
            fan_pending: Rc::default(),
            settled_pins: Arc::default(),
            #[cfg(any(
                feature = "service-keyboard-driver-mode-v1",
                all(
                    feature = "service-hyperboost-v1",
                    feature = "service-profile-triggers-v1"
                )
            ))]
            captured: Rc::default(),
            #[cfg(feature = "service-keyboard-driver-mode-v1")]
            keyboard: Rc::default(),
            gate: Rc::default(),
            #[cfg(feature = "service-diagnostics-export-v1")]
            diagnostic_history,
            battery_observation: Arc::default(),
            battery_source: Arc::new(std::sync::atomic::AtomicU8::new(3)),
            battery_warning: Arc::default(),
            fatal: mailbox.fatal_evidence(),
            #[cfg(test)]
            entered: Rc::default(),
        }
    }
    #[cfg(not(feature = "service-profile-triggers-v1"))]
    pub(super) fn application(
        &self,
        caller: Caller,
        sid: String,
        instance: String,
        sequence: i64,
        path: Option<String>,
    ) -> Outcome {
        if caller.logon != Logon::Interactive
            || caller.control_session != Some(caller.session)
            || caller.pid.is_none()
            || sequence <= 0
            || !crate::service_frame::is_guid(&instance)
            || path
                .as_ref()
                .is_some_and(|p| !super::thermal_storage::valid_application_path(p))
        {
            return Outcome::blocked("Application context was rejected.");
        }
        let mut current = self.application.borrow_mut();
        if current.as_ref().is_some_and(|old| {
            old.sid == sid
                && old.session == caller.session
                && old.instance.eq_ignore_ascii_case(&instance)
                && (old.pid != caller.pid.unwrap() || sequence <= old.sequence)
        }) {
            return Outcome::blocked(
                "Application context is stale or belongs to another reporter.",
            );
        }
        *current = Some(Application {
            sid,
            session: caller.session,
            pid: caller.pid.unwrap(),
            instance,
            sequence,
            foreground: Foreground::reported(path),
            expires: Instant::now() + APPLICATION_LEASE,
        });
        Outcome {
            status: 0,
            message: "Application context stored; automatic native reconciliation is not enabled."
                .into(),
        }
    }
    /// The foreground application `sid` reported in `session` while its
    /// lease holds, and none otherwise.
    fn foreground(&self, sid: &str, session: u32) -> Foreground {
        self.application
            .borrow()
            .as_ref()
            .filter(|a| a.sid == sid && a.session == session && Instant::now() < a.expires)
            .map(|a| a.foreground.clone())
            .unwrap_or_default()
    }
    /// The private thermal projection of one user: saved profiles, saved
    /// intent and its provenance, read from that user's store at each status.
    fn thermal_policy(
        &self,
        policy_sid: String,
        session: u32,
        custody: Arc<dyn ReadCustody>,
    ) -> Rc<dyn Fn(Snapshot) -> io::Result<Snapshot>> {
        let runtime = self.clone();
        Rc::new(move |base| {
            verify_retained_failure(&runtime.fatal)?;
            let base = base.without_private_policy();
            let (store, fresh) = match runtime.capture(&policy_sid, session, custody.clone()) {
                Ok(value) => value,
                Err(error) if is_fatal_admission(&error) => return Err(error),
                Err(_) => {
                    return Ok(base.private_policy_unavailable(
                        crate::service_status::PrivatePolicyFamily::Thermal,
                    ));
                }
            };
            fresh.verify_health()?;
            let admitted = fresh.authorize().is_some();
            fresh.verify_health()?;
            if !admitted {
                return Ok(base);
            }
            let power = base.thermal_power_source();
            // The status base reports 3 for any power that is not battery,
            // Unknown included, and may be older than this read. A failed,
            // unknown or disagreeing Windows read is not a power boundary
            // (managed `CaptureControlPowerSource`), so unless fresh Windows
            // power agrees with the base, saved intent is read at an unknown
            // class (-1), which retires no temporary.
            let query = status_query_power(power, fresh.as_ref());
            let value = store.current_override(query)?;
            let layer = store.profile_layer(&runtime.foreground(&policy_sid, session))?;
            let profile = layer.at(query)?;
            let desired = store.desired(query, profile.as_ref())?;
            #[cfg(feature = "service-profile-triggers-v1")]
            let convergence = {
                let class = convergence_power(query, fresh.as_ref(), &store)?;
                runtime.thermal_convergence(
                    &base,
                    &store,
                    &policy_sid,
                    session,
                    class,
                    layer.at(class)?.as_ref(),
                )?
            };
            let projected = base
                .thermal_profiles(store.profiles()?)?
                .thermal_intent(
                    value.as_ref(),
                    desired.map(|(p, _)| p),
                    "Saved performance policy; current hardware is reported independently.",
                    None,
                )
                .thermal_provenance(desired.map(|(_, s)| s))
                .fan_intent(
                    store.current_fan_override(query)?.as_ref(),
                    store.desired_fan(query, profile.as_ref())?,
                    power,
                    "Saved thermal policy; firmware mode is reported independently.",
                )
                .bind_private_user(&policy_sid);
            #[cfg(feature = "service-profile-triggers-v1")]
            let projected = projected.with_thermal_convergence(convergence);
            runtime.project_saved_members(projected, &store, &policy_sid, session, query)
        })
    }
    /// The private members every retained policy projection carries beside
    /// its thermal ones, the fan request's included (`fan::Runtime::apply_fan`):
    /// managed's Fan Stop automation and the lighting override at `query`.
    /// The caller has compared the override document with `store`'s bytes
    /// (`Storage::current_override`), so the lighting member is the typed
    /// value the store holds.
    pub(super) fn project_saved_members(
        &self,
        projected: Snapshot,
        store: &Storage,
        sid: &str,
        session: u32,
        query: i32,
    ) -> io::Result<Snapshot> {
        #[cfg(feature = "service-fan-stop-v1")]
        let projected = self.project_fan_stop(projected, store, sid, session)?;
        #[cfg(feature = "service-lighting-v1")]
        let projected =
            projected.lighting_intent(store.verified_lighting_override(query)?.as_ref());
        let _ = (store, sid, session, query);
        Ok(projected)
    }
    // Every caller, including the shared mutation tests, passes the admitted request fields individually.
    #[allow(clippy::too_many_arguments)]
    pub(super) async fn apply(
        &self,
        mailbox: MailboxSource,
        caller: Caller,
        sid: String,
        request: String,
        value: Option<Override>,
        deadline: Instant,
        status: Snapshot,
    ) -> io::Result<(Outcome, Option<Snapshot>)> {
        #[cfg(test)]
        self.entered.set(self.entered.get() + 1);
        let _serial = self.gate.lock().await;
        self.apply_locked(
            mailbox,
            ThermalStep {
                caller,
                sid,
                request,
                value,
                reconcile: None,
                #[cfg(feature = "service-profile-triggers-v1")]
                transition: None,
                #[cfg(feature = "service-performance-shortcut-v1")]
                shortcut: None,
                #[cfg(feature = "service-hyperboost-v1")]
                hyperboost: None,
                deadline,
                status,
            },
        )
        .await
        .map(Stepped::reply)
    }
    /// Managed `CyclePerformanceShortcutAsync`: one explicit performance
    /// request whose override `resolve` stages from the fresh reading
    /// (`shortcut::stage`), under the same gate, admission, journal,
    /// readback and stop-on-ambiguity as `apply`.
    #[cfg(feature = "service-performance-shortcut-v1")]
    #[allow(clippy::too_many_arguments)]
    pub(super) async fn cycle_shortcut(
        &self,
        mailbox: MailboxSource,
        caller: Caller,
        sid: String,
        request: String,
        source: ShortcutSource,
        deadline: Instant,
        status: Snapshot,
    ) -> io::Result<(Outcome, Option<Snapshot>)> {
        #[cfg(test)]
        self.entered.set(self.entered.get() + 1);
        let _serial = self.gate.lock().await;
        self.apply_locked(
            mailbox,
            ThermalStep {
                caller,
                sid,
                request,
                value: None,
                reconcile: None,
                #[cfg(feature = "service-profile-triggers-v1")]
                transition: None,
                shortcut: Some(source),
                #[cfg(feature = "service-hyperboost-v1")]
                hyperboost: None,
                deadline,
                status,
            },
        )
        .await
        .map(Stepped::reply)
    }
    /// One performance step under the held gate: an explicit request, or a
    /// reconciliation of saved intent (`ThermalStep::reconcile`), which
    /// stores nothing and keeps every per-write admission, re-check,
    /// readback and stop-on-ambiguity path of the request.
    async fn apply_locked(&self, mailbox: MailboxSource, step: ThermalStep) -> io::Result<Stepped> {
        let ThermalStep {
            caller,
            sid,
            request,
            value,
            reconcile,
            #[cfg(feature = "service-profile-triggers-v1")]
            transition,
            #[cfg(feature = "service-performance-shortcut-v1")]
            shortcut,
            #[cfg(feature = "service-hyperboost-v1")]
            hyperboost,
            deadline,
            status,
        } = step;
        #[cfg(feature = "service-performance-shortcut-v1")]
        let staged = match shortcut {
            Some(source) => Staged::Shortcut(source),
            None => Staged::Value(value),
        };
        #[cfg(not(feature = "service-performance-shortcut-v1"))]
        let staged = Staged::Value(value);
        verify_retained_failure(&self.fatal)?;
        // Metadata may retain a previous user's projection. New accepted intent
        // starts from the public view even when this user has no profile file.
        let status = status
            .without_private_policy()
            .without_thermal_observation();
        if caller.logon != Logon::Interactive || caller.control_session != Some(caller.session) {
            return Ok(Stepped::new(
                Outcome::blocked("The active interactive Windows user is required."),
                None,
            ));
        }
        let MailboxSource::Reader(reader) = mailbox else {
            return Ok(Stepped::new(
                Outcome::blocked("Authenticated thermal custody is unavailable."),
                None,
            ));
        };
        reader.custody().verify_integrity()?;
        // A reconciliation and a HyperBoost request store no intent.
        #[cfg(feature = "service-hyperboost-v1")]
        let stores_nothing = reconcile.is_some() || hyperboost.is_some();
        #[cfg(not(feature = "service-hyperboost-v1"))]
        let stores_nothing = reconcile.is_some();
        // A reconciliation step writes, so it never takes a storage capture.
        let captured = if stores_nothing {
            self.capture(&sid, caller.session, reader.custody())
        } else {
            self.capture_intent(&sid, caller.session, reader.custody())
        };
        let (storage, fresh) = match captured {
            Ok(v) => v,
            Err(e) if is_fatal_admission(&e) => {
                return Err(e);
            }
            // Nothing was read from firmware or submitted.
            Err(e) => {
                return Ok(Stepped::transient(Outcome::blocked(format!(
                    "Saved thermal configuration or fresh admission is unavailable: {e}"
                ))));
            }
        };
        fresh.verify_health()?;
        if storage.pending() {
            return Ok(Stepped::new(
                Outcome::blocked(storage.pending_reason()?),
                None,
            ));
        }
        let proof = fresh.authorize();
        fresh.verify_health()?;
        let Some(proof) = proof else {
            // An explicit request while the low-power gate is set stores its
            // intent without any read or write, as managed stores it before
            // its write admission refuses (`store_performance_deferred`).
            #[cfg(feature = "service-profile-triggers-v1")]
            if !stores_nothing && self.in_low_power() {
                reader.retain_policy(self.thermal_policy(
                    sid.clone(),
                    caller.session,
                    reader.custody(),
                ));
                return self
                    .store_performance_deferred(
                        triggers::DeferredStore {
                            storage: &storage,
                            fresh: fresh.as_ref(),
                            application: &self.foreground(&sid, caller.session),
                            pair: (&sid, caller.session),
                        },
                        staged,
                    )
                    .map(|outcome| Stepped::new(outcome, None));
            }
            return Ok(Stepped::new(
                Outcome::blocked(CURRENT_ADMISSION_UNAVAILABLE),
                None,
            ));
        };
        // The gate once an explicit request holds its own write admission, for
        // a request the gate overtakes later (`overtaken_by_gate`). A
        // reconciliation reads its own before each step.
        #[cfg(feature = "service-profile-triggers-v1")]
        let mark = (!stores_nothing).then(|| self.gate_mark());
        let application = self.foreground(&sid, caller.session);
        #[cfg(feature = "service-profile-triggers-v1")]
        if let Some(classes) = reconcile
            && let Some(refused) =
                refused_by_saved_intent(&storage, &application, classes, fresh.as_ref())?
        {
            return Ok(refused);
        }
        reader.retain_policy(self.thermal_policy(sid.clone(), caller.session, reader.custody()));
        #[cfg(feature = "service-performance-shortcut-v1")]
        let (requested, shortcut) = match staged {
            Staged::Value(value) => (OnceLock::from(Requested::new(value)?), None),
            Staged::Shortcut(source) => (OnceLock::new(), Some(source)),
        };
        #[cfg(not(feature = "service-performance-shortcut-v1"))]
        let Staged::Value(value) = staged;
        #[cfg(not(feature = "service-performance-shortcut-v1"))]
        let requested = OnceLock::from(Requested::new(value)?);
        let permit = Arc::new(RequestPermit {
            storage,
            fresh,
            request,
            caller_pid: caller.pid.ok_or(io::ErrorKind::PermissionDenied)?,
            session: caller.session,
            owner: format!("{:x}", Sha256::digest(proof.generation)),
            requested,
            #[cfg(feature = "service-performance-shortcut-v1")]
            shortcut,
            reconcile,
            #[cfg(feature = "service-profile-triggers-v1")]
            transition,
            #[cfg(feature = "service-profile-triggers-v1")]
            drift: self.drift.clone(),
            #[cfg(feature = "service-profile-triggers-v1")]
            baseline: Mutex::new(None),
            application,
            accepted: AtomicBool::new(false),
            destination: Mutex::new(None),
            power: Mutex::new(None),
            selected_source: Mutex::new(None),
            cause: Mutex::new(Cause::CurrentAdmission),
            fatal: self.fatal.clone(),
            #[cfg(feature = "service-gpu-clock-tuning-v1")]
            gpu: self.gpu.get().filter(|_| !stores_nothing).cloned(),
            #[cfg(feature = "service-gpu-clock-tuning-v1")]
            // Read only for a composed lane, so a build without one reads the
            // gate exactly as it did.
            gpu_boundary: Mutex::new(if self.gpu.get().is_some() && !stores_nothing {
                self.gpu_boundary()
            } else {
                thermal_gpu::Boundary::default()
            }),
            #[cfg(feature = "service-gpu-clock-tuning-v1")]
            gpu_tuning: Mutex::new(None),
            #[cfg(feature = "service-gpu-clock-tuning-v1")]
            gpu_half: Mutex::new(None),
            #[cfg(feature = "service-gpu-clock-tuning-v1")]
            deadline,
            gpu_fence: self.gpu_fence(),
            #[cfg(feature = "service-hyperboost-v1")]
            hyperboost,
        });
        let result = reader.mutate(permit.clone(), deadline).await;
        permit.verify_failure()?;
        let result = match result {
            Err(error) if is_fatal_admission(&error) => return Err(error),
            result => result,
        };
        // A readback of this step is OpenBlade's own (managed
        // `ConfirmIntentional`), whatever admission the reply keeps below.
        // The Reader returns one only for a destination it confirms: one it
        // wrote, which `record(Confirmed)` already recorded, or one firmware
        // already reported.
        #[cfg(feature = "service-profile-triggers-v1")]
        if let (Ok(Some(readback)), Some(destination)) = (
            &result,
            *permit
                .destination
                .lock()
                .map_err(|_| io::ErrorKind::Other)?,
        ) {
            let mut drift = self.drift.lock().map_err(|_| io::ErrorKind::Other)?;
            drift.observe(*readback);
            drift.own_performance(destination);
        }
        permit.fresh.verify_health()?;
        let final_proof = if Instant::now() < deadline {
            permit.fresh.authorize()
        } else {
            None
        };
        permit.fresh.verify_health()?;
        if final_proof != Some(proof) {
            // Stored, then refused at its own write admission because the gate
            // set: the deferral it would have taken had the gate set first.
            #[cfg(feature = "service-profile-triggers-v1")]
            if let Some(mark) = mark
                && permit.accepted.load(Ordering::Acquire)
                && self.overtaken_by_gate(mark, &permit.storage, &permit.request)
            {
                self.owe_stored_intent((&sid, caller.session));
                return Ok(Stepped::new(
                    deferred_performance(permit.value().is_none()),
                    None,
                ));
            }
            return Ok(Stepped::new(
                Outcome::blocked(
                    "Thermal result lost caller or owner admission; submission evidence is retained.",
                ),
                None,
            ));
        }
        let destination = *permit
            .destination
            .lock()
            .map_err(|_| io::ErrorKind::Other)?;
        if let Err(error) = &result {
            permit.set_cause(Cause::Transport(error.to_string()));
        }
        let observed = result
            .ok()
            .flatten()
            .filter(|value| destination.is_some_and(|destination| confirms(*value, destination)));
        // The GPU half's second step, once the firmware destination is
        // confirmed by its independent readback (`thermal_gpu::Lane::complete`).
        // A failure is managed's reply, never retried.
        #[cfg(feature = "service-gpu-clock-tuning-v1")]
        let gpu = complete_gpu(&permit, observed.is_some(), proof).await?;
        #[cfg(feature = "service-gpu-clock-tuning-v1")]
        let gpu_applied = gpu.as_ref().is_none_or(|c| c.applied);
        #[cfg(feature = "service-gpu-clock-tuning-v1")]
        if let Some(failed) = gpu.as_ref().filter(|c| !c.applied) {
            permit.set_cause(Cause::GpuRefused {
                message: failed.message.clone(),
                failed: failed.failed,
            });
        }
        #[cfg(not(feature = "service-gpu-clock-tuning-v1"))]
        let gpu_applied = true;
        let accepted = permit.accepted.load(Ordering::Acquire);
        let requested_applied = permit
            .value()
            .is_none_or(|v| destination.is_some_and(|d| v.value == d.wire()));
        let outcome = if let Some(outcome) =
            permit.hyperboost_reply(observed.is_some(), destination)?
        {
            outcome
        } else if observed.is_some() && requested_applied && gpu_applied {
            #[cfg(feature = "service-gpu-clock-tuning-v1")]
            let message = if permit.gpu_composed() {
                thermal_gpu::confirmed(gpu.as_ref())
            } else {
                CONFIRMED
            };
            #[cfg(not(feature = "service-gpu-clock-tuning-v1"))]
            let message = CONFIRMED;
            Outcome {
                status: 1,
                message: message.into(),
            }
        } else if permit.storage.configuration_failed() {
            Outcome::blocked(
                "Saved configuration replacement failed. Intent was not accepted; no thermal command was submitted. Storage reconciliation is required.",
            )
        } else if permit.storage.pending() {
            Outcome::blocked(permit.storage.pending_reason()?)
        } else if accepted {
            let cause = permit.cause.lock().map_err(|_| io::ErrorKind::Other)?;
            if matches!(
                *cause,
                Cause::ManualDeferred | Cause::AwaitingClassification
            ) {
                // Managed's reply for this external-power manual-fan deferral
                // (ServiceControlRuntime.DeferredPerformanceIntentMessage), or for a
                // clear while the adapter is unclassified. As in
                // managed, a fan request that returns the fans to Automatic
                // applies the saved preset in the same pair (FanPermit).
                Outcome {
                    status: 0,
                    message: cause.message(),
                }
            } else {
                // Saved but unapplied: Blocked, prefixed as ExecuteScopedMutationAsync
                // prefixes the hardware result after committing managed intent.
                let committed = performance_committed(permit.value().is_none());
                // The policy can select and confirm another destination while the
                // requested value itself stays saved but unapplied.
                let reason = if observed.is_some() && !requested_applied && gpu_applied {
                    "The current power and profile policy selects another performance destination, which is confirmed; the requested value is saved but not applied.".into()
                } else {
                    cause.message()
                };
                // A reconciliation committed nothing, so only its hardware
                // result is reported (managed `ApplyFullTransitionAsync`).
                if reconcile.is_some() {
                    Outcome::blocked(reason)
                } else if gpu_failed(&cause) {
                    // Managed reports an indeterminate or rejected GPU clock
                    // submission as Failed, after the same committed prefix
                    // (`ServiceControlRuntime.cs:14989-14991`).
                    Outcome {
                        status: 3,
                        message: format!("{committed} Hardware result: {reason}"),
                    }
                } else {
                    Outcome::blocked(format!("{committed} Hardware result: {reason}"))
                }
            }
        } else if let cause @ (Cause::UnclassifiedRejected
        | Cause::UnclassifiedCustomRejected
        | Cause::Rejected(_)) = &*permit.cause.lock().map_err(|_| io::ErrorKind::Other)?
        {
            Outcome {
                status: 3,
                message: cause.message(),
            }
        } else if matches!(
            *permit.cause.lock().map_err(|_| io::ErrorKind::Other)?,
            Cause::UnclassifiedBlocked
        ) {
            Outcome::blocked(UNCLASSIFIED_BLOCKED)
        } else if let Some(message) = shortcut_refusal(&permit)? {
            Outcome::blocked(message)
        } else if matches!(
            *permit.cause.lock().map_err(|_| io::ErrorKind::Other)?,
            Cause::Unchanged
        ) {
            // Nothing was accepted or written; the reconciliation replies.
            return Ok(Stepped::unchanged());
        } else {
            Outcome::blocked(
                "Performance intent was not accepted; admission or protected persistence failed.",
            )
        };
        // Managed reports an applied or stored shortcut by what it selected.
        #[cfg(feature = "service-performance-shortcut-v1")]
        let outcome = match (permit.shortcut, permit.value()) {
            (Some(source), Some(value)) => match source.reply(outcome.status, value.value) {
                Some(message) => Outcome {
                    status: outcome.status,
                    message,
                },
                None => outcome,
            },
            _ => outcome,
        };
        let power = *permit.power.lock().map_err(|_| io::ErrorKind::Other)?;
        // A pace refusal, an adapter not classified yet, or a job that ran out
        // of time or admission at a reading or checkpoint without a refusal of
        // its own. A job that stopped after its intent row may have submitted:
        // the journal says so, and the reconciliation then retries nothing.
        let transient = observed.is_none()
            && matches!(
                *permit.cause.lock().map_err(|_| io::ErrorKind::Other)?,
                Cause::Deadline | Cause::UnclassifiedBlocked | Cause::CurrentAdmission
            );
        #[cfg(feature = "service-profile-triggers-v1")]
        let excluded = matches!(
            *permit.cause.lock().map_err(|_| io::ErrorKind::Other)?,
            Cause::Excluded(_)
        );
        #[cfg(not(feature = "service-profile-triggers-v1"))]
        let excluded = false;
        let selected_source = *permit
            .selected_source
            .lock()
            .map_err(|_| io::ErrorKind::Other)?;
        // The destination the status projects as desired: a HyperBoost
        // entry changes no saved intent.
        let projected = permit.projected_destination(destination)?;
        let project = |status: Snapshot| -> io::Result<Option<Snapshot>> {
            // A reconciliation reports the override layer it applied over.
            let intent_value = match power.filter(|_| stores_nothing) {
                Some(power) => permit.storage.current_override(power)?,
                None => permit.value().cloned(),
            };
            let status = if let Some(observed) = observed {
                let fresh = permit.fresh.clone();
                status.with_mailbox_read(
                    observed,
                    ReadConfirmation::new(deadline, move || fresh.authorize() == Some(proof)),
                )
            } else {
                status
            };
            let status = if accepted {
                status.thermal_profiles(permit.storage.profiles()?)?
            } else {
                status
            };
            Ok(accepted.then(|| {
                status
                    .thermal_intent(intent_value.as_ref(), projected, &outcome.message, power)
                    .bind_private_user(&sid)
                    .thermal_provenance(selected_source)
                    .thermal_write_admission(outcome.status == 1)
            }))
        };
        match project(status) {
            Ok(snapshot) => Ok(Stepped {
                transient,
                class: power,
                excluded,
                ..Stepped::new(outcome, snapshot)
            }),
            Err(error) if reconcile.is_none() || is_fatal_admission(&error) => Err(error),
            // A reconciliation may already have written: its reply must reach
            // the trigger's caller, so a store that changed or cannot be read
            // after the hardware result fences writes and is reported, never
            // ending the Service.
            Err(_) => Ok(Stepped::new(after_write_unreadable(&permit.storage)?, None)),
        }
    }
}
/// A reconciliation's performance step refused from saved intent alone, before
/// any firmware read or transaction pair (`Runtime::reconcile_locked`): at every
/// class its reading could find, the saved policy carries a GPU clock effect,
/// which has no native writer, or selects no destination. The step would read
/// the adapter only to reach the same refusal at whichever class it found.
/// `classes` must cover every class fresh Windows power admits, or the reading
/// could find one the trigger did not change, which writes nothing either but
/// replies so. A store or profile read that fails is a transient refusal; a
/// changed override document fences the store, as the step's own check would.
#[cfg(feature = "service-profile-triggers-v1")]
fn refused_by_saved_intent(
    storage: &Storage,
    application: &Foreground,
    classes: Classes,
    fresh: &dyn Fresh,
) -> io::Result<Option<Stepped>> {
    if classes != Classes::admitted(fresh) {
        return Ok(None);
    }
    let refusal = (|| {
        storage.verify_overrides()?;
        let layer = storage.profile_layer(application)?;
        let mut causes = Vec::new();
        for class in classes.iter() {
            // Each class is read in its own scope: a read at one class
            // expires a temporary kept for another (`Storage::unexpired`).
            let cause = storage.unexpired(|| {
                let profile = layer.at(class)?;
                Ok(if storage.gpu_incomplete(class, profile.as_ref())? {
                    Some(Cause::Gpu)
                } else if storage.desired(class, profile.as_ref())?.is_none() {
                    Some(Cause::NoEligiblePolicy)
                } else {
                    None
                })
            })?;
            let Some(cause) = cause else {
                return Ok(None);
            };
            let message = cause.message();
            if !causes.contains(&message) {
                causes.push(message);
            }
        }
        Ok(Some(causes.join(" ")))
    })();
    match refusal {
        Ok(refused) => Ok(refused.map(|message| Stepped::new(Outcome::blocked(message), None))),
        Err(error) if is_fatal_admission(&error) => Err(error),
        Err(_) if storage.pending() => Ok(Some(Stepped::new(
            Outcome::blocked(storage.pending_reason()?),
            None,
        ))),
        Err(error) => Ok(Some(Stepped::transient(Outcome::blocked(format!(
            "Saved thermal configuration or fresh admission is unavailable: {error}"
        ))))),
    }
}
/// The saved policy's destination for one fresh class, and why it may not be
/// written now. It stores nothing and submits nothing, so a reconciliation of
/// already-saved intent (`Runtime::reconcile_locked`) uses it unchanged.
struct Selection {
    desired: Option<(Destination, i32)>,
    blocked: Option<Cause>,
}
/// `profile` is the domain resolution at `power` (`ProfileLayer::at`): the
/// overrides `Storage` holds are composed over it, and it alone supplies the
/// profile layer of the destination, the GPU clock check and the fan intent.
/// With `gpu` the step writes GPU clock tuning itself (`thermal_gpu`), so a
/// GPU clock effect blocks nothing here.
fn select(
    storage: &Storage,
    power: i32,
    profile: Option<&Resolution<'_>>,
    baseline: ObservedPerformance,
    gpu: bool,
) -> io::Result<Selection> {
    let blocked = |cause| Selection {
        desired: None,
        blocked: Some(cause),
    };
    if !gpu && storage.gpu_incomplete(power, profile)? {
        return Ok(blocked(Cause::Gpu));
    }
    let Some((destination, source)) = storage.desired(power, profile)? else {
        return Ok(blocked(Cause::NoEligiblePolicy));
    };
    let desired = Some((destination, source));
    let blocked = if openblade_domain::thermal_mutation::defer_manual_performance(
        baseline,
        power,
        storage.desired_manual_fans(power, profile)?,
    ) {
        // Stored promises that the fan request returning the fans applies the
        // intent, as it does for every native destination.
        Some(Cause::ManualDeferred)
    } else if storage.outstanding_fan() {
        // An owned fan row without its handback is not a manual-fan deferral:
        // the saved fan intent may already be Automatic or cleared.
        Some(Cause::FanHandbackPending)
    } else {
        None
    };
    Ok(Selection { desired, blocked })
}
struct RequestPermit {
    caller_pid: u32,
    session: u32,
    storage: Arc<Storage>,
    fresh: Arc<dyn Fresh>,
    request: String,
    owner: String,
    // The override this request stores and journals. A shortcut stages
    // its own in `resolve` from the fresh reading (`shortcut::stage`);
    // every other request carries it from the start.
    requested: OnceLock<Requested>,
    #[cfg(feature = "service-performance-shortcut-v1")]
    shortcut: Option<ShortcutSource>,
    // `Some` applies saved intent at those classes without storing any
    // (`Runtime::reconcile_locked`); `value` is then None.
    reconcile: Option<Classes>,
    // A reconciliation's drift decision (`thermal_drift`).
    #[cfg(feature = "service-profile-triggers-v1")]
    transition: Option<Arc<thermal_drift::Transition>>,
    // Confirmed thermal drift: this step's confirmed write is OpenBlade's
    // own, and a possibly submitted one is settled by the next reading
    // (`record`).
    #[cfg(feature = "service-profile-triggers-v1")]
    drift: Arc<Mutex<thermal_drift::Tracker>>,
    // The fresh reading the destination is written from, which says
    // whether its pair also returns manual fans to Automatic (`record`).
    #[cfg(feature = "service-profile-triggers-v1")]
    baseline: Mutex<Option<ObservedPerformance>>,
    application: Foreground,
    accepted: AtomicBool,
    destination: Mutex<Option<Destination>>,
    power: Mutex<Option<i32>>,
    selected_source: Mutex<Option<i32>>,
    cause: Mutex<Cause>,
    // Reader refusal is Option-valued; retain typed trust loss across that boundary.
    fatal: Arc<Mutex<Option<&'static str>>>,
    // GPU clock tuning (`thermal_gpu`) for an explicit request; a
    // reconciliation keeps every GPU clock effect refused as incomplete.
    #[cfg(feature = "service-gpu-clock-tuning-v1")]
    gpu: Option<Arc<thermal_gpu::Lane>>,
    // The lifecycle this step was admitted under.
    #[cfg(feature = "service-gpu-clock-tuning-v1")]
    gpu_boundary: Mutex<thermal_gpu::Boundary>,
    // The tuning `resolve` selected (None: none) and the half it left.
    #[cfg(feature = "service-gpu-clock-tuning-v1")]
    gpu_tuning: Mutex<Option<Option<thermal_gpu::Tuning>>>,
    #[cfg(feature = "service-gpu-clock-tuning-v1")]
    gpu_half: Mutex<Option<thermal_gpu::Half>>,
    // The request's deadline, which bounds both GPU halves.
    #[cfg(feature = "service-gpu-clock-tuning-v1")]
    deadline: Instant,
    gpu_fence: GpuFence,
    // An owned HyperBoost request (`hyperboost::Step`), which stores nothing.
    #[cfg(feature = "service-hyperboost-v1")]
    hyperboost: Option<hyperboost::Step>,
}
/// The fence every native thermal writer reads before its rows
/// (`Permit::thermal_fenced`): with `service-gpu-clock-tuning-v1` composed,
/// the GPU clock lane's outstanding or uncertain submission
/// (`thermal_gpu::Lane::fenced`), for which managed's pending Performance
/// confirmation blocks every thermal write. None fences nothing.
pub(super) type GpuFence = Option<Arc<dyn Fn(std::time::Duration) -> bool + Send + Sync>>;
impl Runtime {
    /// The fence for a writer or trigger admitted now: it carries the
    /// lifecycle the writer was admitted under (`Runtime::gpu_boundary`), so
    /// its resolution read crosses a power change as a GPU step would. A
    /// permit that outlives that lifecycle builds its own: owned Stop's
    /// settle-only and handback permits take one at Stop, after the power or
    /// session change that moved the owner's epoch.
    pub(super) fn gpu_fence(&self) -> GpuFence {
        #[cfg(feature = "service-gpu-clock-tuning-v1")]
        if let Some(lane) = self.gpu.get().cloned() {
            let boundary = self.gpu_boundary();
            return Some(Arc::new(move |budget| lane.fenced(boundary, budget)));
        }
        None
    }
}
/// A request's override (`None` clears it) and the digest its journal
/// rows carry.
struct Requested {
    value: Option<Override>,
    intent: String,
}
impl Requested {
    fn new(value: Option<Override>) -> io::Result<Self> {
        let intent = format!("{:x}", Sha256::digest(serde_json::to_vec(&value)?));
        Ok(Self { value, intent })
    }
}
/// Whether `cause` is a GPU clock failure managed reports as Failed.
fn gpu_failed(cause: &Cause) -> bool {
    #[cfg(feature = "service-gpu-clock-tuning-v1")]
    if let Cause::GpuRefused { failed, .. } = cause {
        return *failed;
    }
    let _ = cause;
    false
}
/// The reply of a confirmed step without GPU clock tuning composed.
const CONFIRMED: &str = "Bounded thermal preset confirmed by independent paired readback. GPU clock handling and broad device capabilities remain unavailable; other native controls are incomplete.";
/// The GPU half's second step (`thermal_gpu::Lane::complete`) once the
/// firmware readback `confirmed` the destination, under the step's own
/// write admission `proof`, read afresh. None when the step has no second
/// half. The NVML calls block a pool thread, not the Service runtime.
#[cfg(feature = "service-gpu-clock-tuning-v1")]
async fn complete_gpu(
    permit: &RequestPermit,
    confirmed: bool,
    proof: ReadAdmission,
) -> io::Result<Option<thermal_gpu::Completed>> {
    let half = *permit.gpu_half.lock().map_err(|_| io::ErrorKind::Other)?;
    let (Some(lane), Some(half), true) = (permit.gpu.clone(), half, confirmed) else {
        return Ok(None);
    };
    permit.fresh.verify_health()?;
    let admitted = permit.fresh.authorize() == Some(proof);
    permit.fresh.verify_health()?;
    if !admitted {
        return Ok(thermal_gpu::unadmitted(half));
    }
    let boundary = *permit
        .gpu_boundary
        .lock()
        .map_err(|_| io::ErrorKind::Other)?;
    let budget = permit.gpu_budget();
    Ok(
        tokio::task::spawn_blocking(move || lane.complete(boundary, half, budget))
            .await
            .unwrap_or_else(|_| thermal_gpu::lost(half)),
    )
}
/// What a performance step stores: an explicit override (`None` clears it),
/// or the one a shortcut stages from its fresh reading.
enum Staged {
    Value(Option<Override>),
    #[cfg(feature = "service-performance-shortcut-v1")]
    Shortcut(ShortcutSource),
}
#[cfg(feature = "service-profile-triggers-v1")]
impl Staged {
    /// The refusal at an unclassified adapter that admits no ordinary
    /// control: a shortcut reports managed's own.
    fn unclassified_blocked(&self) -> &'static str {
        match self {
            Self::Value(_) => UNCLASSIFIED_BLOCKED,
            #[cfg(feature = "service-performance-shortcut-v1")]
            Self::Shortcut(_) => shortcut::UNCLASSIFIED_BLOCKED,
        }
    }
}
#[derive(Debug)]
enum Cause {
    CurrentAdmission,
    Deadline,
    AwaitingClassification,
    FanHandbackPending,
    Gpu,
    /// GPU clock tuning refused the step before any journal row or setter
    /// (`thermal_gpu::Lane::prepare`), or its second half failed after the
    /// firmware destination was confirmed (`thermal_gpu::Lane::complete`):
    /// managed's reply, which is never retried.
    #[cfg(feature = "service-gpu-clock-tuning-v1")]
    GpuRefused {
        message: String,
        failed: bool,
    },
    /// The shared thermal fence refused the step before any journal row or
    /// setter (`Refusal::ThermalPending`): a GPU clock submission is
    /// outstanding or uncertain. Managed's scoped block reason.
    ThermalPending(&'static str),
    ManualDeferred,
    /// Leaving HyperBoost for a battery preset without the restore sequence.
    #[cfg(not(feature = "service-thermal-hyperboost-exit-v1"))]
    NoDirectPath,
    NoEligiblePolicy,
    TransactionCapacity,
    PowerChanged,
    Storage(String),
    Transport(String),
    UnclassifiedBlocked,
    UnclassifiedRejected,
    UnclassifiedCustomRejected,
    /// A reconciliation step found a class its trigger did not change.
    Unchanged,
    /// Managed `ValidateThermalOverrideCombination` on classified power, as
    /// `RecordRejectedIntent` reports it (Failed); nothing is stored.
    Rejected(String),
    /// A reconciliation excluded both thermal subsystems for confirmed
    /// drift or an observed HyperBoost (`thermal_drift`).
    #[cfg(feature = "service-profile-triggers-v1")]
    Excluded(thermal_drift::Exclusion),
    /// A performance shortcut that staged nothing (`shortcut::stage`);
    /// nothing is stored or written.
    #[cfg(feature = "service-performance-shortcut-v1")]
    Shortcut(&'static str),
    /// A HyperBoost step refused, or already in its state, with managed's
    /// reply and status (`hyperboost`); nothing is stored or written.
    #[cfg(feature = "service-hyperboost-v1")]
    HyperBoost {
        message: &'static str,
        status: u8,
    },
}
impl Cause {
    fn message(&self) -> String {
        match self {
            Self::CurrentAdmission => "Current admission or independent readback did not complete; no successful application is reported.".into(),
            Self::Deadline => DEADLINE.into(),
            Self::AwaitingClassification => UNCLASSIFIED_CLEARED.into(),
            Self::FanHandbackPending => "OpenBlade still owns a fan change that has not been handed back to firmware Automatic; no thermal setter was submitted.".into(),
            Self::Gpu => "GPU clock handling is incomplete; no thermal setter was submitted.".into(),
            #[cfg(feature = "service-gpu-clock-tuning-v1")]
            Self::GpuRefused { message, .. } => message.clone(),
            Self::ThermalPending(message) => (*message).into(),
            // Without the fan composition no native request returns the fans,
            // so that build keeps the explicit resubmission instruction.
            Self::ManualDeferred if cfg!(feature = "service-fan-mutation-v2") => "Performance intent was stored and will be applied when fans return to Automatic.".into(),
            Self::ManualDeferred => "Performance intent stored. Performance is saved, not applied. Manual fan intent is preserved. Submit a new performance request after selecting Automatic fans.".into(),
            #[cfg(not(feature = "service-thermal-hyperboost-exit-v1"))]
            Self::NoDirectPath => "Leaving HyperBoost for a battery preset needs the HyperBoost restore sequence, which this build does not submit; no thermal setter was submitted.".into(),
            Self::NoEligiblePolicy => "No supported performance destination is selected by the current power and profile policy.".into(),
            Self::TransactionCapacity => "Thermal transaction ID capacity is exhausted; no setter was submitted.".into(),
            Self::PowerChanged => "The independently observed power source changed or no longer admits this destination.".into(),
            Self::Storage(e) => format!("Protected storage could not record the next step: {e}"),
            Self::Transport(e) => format!("Thermal transport or ownership did not complete: {e}"),
            Self::UnclassifiedBlocked => UNCLASSIFIED_BLOCKED.into(),
            Self::UnclassifiedRejected => UNCLASSIFIED_REJECTED.into(),
            Self::UnclassifiedCustomRejected => UNCLASSIFIED_CUSTOM_REJECTED.into(),
            Self::Unchanged => UNCHANGED_CLASS.into(),
            Self::Rejected(message) => format!("The requested intent was rejected: {message}"),
            #[cfg(feature = "service-profile-triggers-v1")]
            Self::Excluded(exclusion) => exclusion.message("Performance"),
            #[cfg(feature = "service-performance-shortcut-v1")]
            Self::Shortcut(message) => (*message).into(),
            #[cfg(feature = "service-hyperboost-v1")]
            Self::HyperBoost { message, .. } => (*message).into(),
        }
    }
}
impl RequestPermit {
    /// A HyperBoost step's reply (`hyperboost`); None for any other step.
    fn hyperboost_reply(
        &self,
        confirmed: bool,
        destination: Option<Destination>,
    ) -> io::Result<Option<Outcome>> {
        #[cfg(feature = "service-hyperboost-v1")]
        if let Some(step) = &self.hyperboost {
            return self
                .hyperboost_outcome(step, confirmed, destination)
                .map(Some);
        }
        let _ = (confirmed, destination);
        Ok(None)
    }
    /// The destination a step's status projects as desired: the step's own,
    /// but for a HyperBoost entry the saved one, which it does not change.
    /// A restore-only exit projects its Balanced destination: publication
    /// projects the desired state again from the saved policy
    /// (`ReadSource::current_policy`), and only a Reader writes.
    fn projected_destination(
        &self,
        destination: Option<Destination>,
    ) -> io::Result<Option<Destination>> {
        #[cfg(feature = "service-hyperboost-v1")]
        if let Some(step) = self.hyperboost.as_ref().filter(|step| step.enable) {
            return step.saved();
        }
        Ok(destination)
    }
    /// The override this request stores; none for a clear, or for a
    /// shortcut that has not staged one.
    fn value(&self) -> Option<&Override> {
        self.requested.get().and_then(|r| r.value.as_ref())
    }
    /// Whether this step writes GPU clock tuning itself (`thermal_gpu`).
    #[cfg(feature = "service-gpu-clock-tuning-v1")]
    fn gpu_composed(&self) -> bool {
        self.gpu.is_some()
    }
    #[cfg(not(feature = "service-gpu-clock-tuning-v1"))]
    fn gpu_composed(&self) -> bool {
        false
    }
    /// The tuning class `power` selects, `Err` for tuning managed
    /// `GpuClockTuningSettings.IsWellFormed` refuses.
    #[cfg(feature = "service-gpu-clock-tuning-v1")]
    fn gpu_tuning_at(
        &self,
        power: i32,
        profile: Option<&Resolution<'_>>,
    ) -> io::Result<Result<Option<thermal_gpu::Tuning>, ()>> {
        Ok(match self.storage.gpu_tuning(power, profile)? {
            None => Ok(None),
            Some(value) => thermal_gpu::Tuning::parse(&value).map(Some).ok_or(()),
        })
    }
    /// Whether a fresh external class `power` selects the tuning the first
    /// half was decided for (`PowerFence::admits`).
    #[cfg(feature = "service-gpu-clock-tuning-v1")]
    fn gpu_tuning_holds(&self, power: i32, profile: Option<&Resolution<'_>>) -> io::Result<bool> {
        let decided = *self.gpu_tuning.lock().map_err(|_| io::ErrorKind::Other)?;
        Ok(decided.is_some() && self.gpu_tuning_at(power, profile)?.ok() == decided)
    }
    #[cfg(not(feature = "service-gpu-clock-tuning-v1"))]
    fn gpu_tuning_holds(&self, _: i32, _: Option<&Resolution<'_>>) -> io::Result<bool> {
        Ok(false)
    }
    /// What remains of the request for one GPU call, less the margin its
    /// reply needs (`thermal_gpu::REPLY_MARGIN`): no GPU call outlives the
    /// request.
    #[cfg(feature = "service-gpu-clock-tuning-v1")]
    fn gpu_budget(&self) -> std::time::Duration {
        self.deadline
            .saturating_duration_since(Instant::now())
            .saturating_sub(thermal_gpu::REPLY_MARGIN)
    }
    /// The GPU half's first step for the destination `desired` selected at
    /// class `power` from the fresh `baseline` (`thermal_gpu::Lane::prepare`),
    /// or the cause that refuses the step with nothing written. A step the
    /// Reader refuses as `Plan::NoDirectPath` takes none. With the HyperBoost
    /// exit, the restore sequence's battery destination takes the half a
    /// direct step to it would, after the battery pair's readback.
    #[cfg(feature = "service-gpu-clock-tuning-v1")]
    fn prepare_gpu(
        &self,
        power: i32,
        profile: Option<&Resolution<'_>>,
        baseline: ObservedPerformance,
        desired: Option<Destination>,
    ) -> io::Result<Option<Cause>> {
        use openblade_domain::thermal_mutation::Plan;
        let (Some(lane), Some(destination)) = (&self.gpu, desired) else {
            return Ok(None);
        };
        let plan = Plan::from_observation(baseline, destination);
        #[cfg(not(feature = "service-thermal-hyperboost-exit-v1"))]
        if plan == Plan::NoDirectPath {
            return Ok(None);
        }
        let Ok(tuning) = self.gpu_tuning_at(power, profile)? else {
            return Ok(Some(Cause::GpuRefused {
                message: thermal_gpu::UNSUPPORTED.into(),
                failed: false,
            }));
        };
        *self.gpu_tuning.lock().map_err(|_| io::ErrorKind::Other)? = Some(tuning);
        // The step's resolved power class joins its boundary, so both
        // halves rearm the management on the same classified source change.
        let boundary = {
            let mut boundary = self.gpu_boundary.lock().map_err(|_| io::ErrorKind::Other)?;
            boundary.source = Some(power);
            *boundary
        };
        Ok(
            match lane.prepare(boundary, tuning, plan == Plan::Unchanged, self.gpu_budget()) {
                thermal_gpu::Prepared::Proceed(half) => {
                    *self.gpu_half.lock().map_err(|_| io::ErrorKind::Other)? = Some(half);
                    None
                }
                thermal_gpu::Prepared::Pending => Some(Cause::GpuRefused {
                    message: thermal_gpu::PENDING.into(),
                    failed: false,
                }),
                thermal_gpu::Prepared::Refused { message, failed } => {
                    Some(Cause::GpuRefused { message, failed })
                }
            },
        )
    }
    /// An unclassified adapter that admits no ordinary control: a shortcut
    /// reports managed's own refusal for it.
    fn unclassified_blocked(&self) -> Cause {
        #[cfg(feature = "service-performance-shortcut-v1")]
        if self.shortcut.is_some() {
            return Cause::Shortcut(shortcut::UNCLASSIFIED_BLOCKED);
        }
        Cause::UnclassifiedBlocked
    }
    /// The shortcut's override for the fresh class `power` and reading
    /// `baseline` over the profiles `layer` the request step read, or the
    /// refusal that stores nothing.
    #[cfg(feature = "service-performance-shortcut-v1")]
    fn stage_shortcut(
        &self,
        power: i32,
        baseline: ObservedPerformance,
        layer: &super::thermal_storage::ProfileLayer,
    ) -> io::Result<Result<Override, Cause>> {
        // Managed `ExternalChanges` as confirmed drift publishes it.
        #[cfg(feature = "service-profile-triggers-v1")]
        let external_performance = self
            .drift
            .lock()
            .map_err(|_| io::ErrorKind::Other)?
            .external()
            .contains(&thermal_drift::PERFORMANCE);
        #[cfg(not(feature = "service-profile-triggers-v1"))]
        let external_performance = false;
        shortcut::stage(&shortcut::Reading {
            storage: &self.storage,
            fresh: self.fresh.as_ref(),
            power,
            layer,
            baseline: Some(baseline),
            external_performance,
        })
    }
    fn remember_failure(&self, error: &io::Error) {
        remember_failure(&self.fatal, error);
    }
    fn verify_failure(&self) -> io::Result<()> {
        verify_retained_failure(&self.fatal)
    }
    fn set_cause(&self, cause: Cause) {
        if let Ok(mut current) = self.cause.lock() {
            *current = cause;
        }
    }
    fn observe_power(&self, power: i32) -> io::Result<()> {
        let generation = self
            .fresh
            .power_generation()
            .ok_or(io::ErrorKind::PermissionDenied)?;
        self.storage.observe_power(power, generation)
    }
}
/// Whether a battery reading of the adapter contradicts Windows. Managed
/// `ResolveCurrentPowerSourceAsync` returns Battery only when Windows itself
/// reports battery; with Windows AC or unknown it samples the adapter and maps
/// a Battery reply to ExternalUnknown, which `ExecuteScopedMutationAsync`
/// blocks (`PublishScopedUnavailable`, `UNCLASSIFIED_BLOCKED`) before any
/// intent is stored. The battery classification never became current there,
/// so the caller must also not expire temporaries by observing it.
pub(super) fn battery_reading_contradicts_windows(power: i32, fresh: &dyn Fresh) -> bool {
    power == 1 && fresh.on_battery() != Some(true)
}
/// Managed's `Automation.PowerSource` for classified reading `power`, as
/// `ResolveCurrentPowerSourceAsync` resolves it: Battery whenever Windows
/// reports battery, without sampling the adapter, and otherwise the adapter's
/// external class. A battery reading under Windows AC never gets here
/// (`battery_reading_contradicts_windows`). Without a Windows reading native
/// stores nothing, so there is no source to check against.
pub(super) fn managed_power_source(power: i32, fresh: &dyn Fresh) -> Option<i32> {
    match (power, fresh.on_battery()) {
        (_, Some(true)) => Some(1),
        (0 | 2, Some(false)) => Some(power),
        _ => None,
    }
}
#[cfg(feature = "service-profile-triggers-v1")]
impl Runtime {
    /// Managed `ExecuteScopedMutationAsync` for an explicit performance
    /// request while the low-power gate is set: the override is validated
    /// and stored as `RequestPermit::resolve` stores it at a battery reading,
    /// and nothing is read or submitted. The reply is managed's committed
    /// message with its per-write refusal (`deferred_performance`), and the
    /// pair owes a reconciliation for the first report after the gate clears
    /// (`Runtime::owe_stored_intent`). Only Windows battery classifies power
    /// without an adapter reading (`triggers::deferred_store_refusal`).
    fn store_performance_deferred(
        &self,
        store: triggers::DeferredStore<'_>,
        value: Staged,
    ) -> io::Result<Outcome> {
        use triggers::DeferredRefusal;
        match triggers::deferred_store_refusal(store.fresh) {
            Some(DeferredRefusal::Admission) => {
                return Ok(Outcome::blocked(CURRENT_ADMISSION_UNAVAILABLE));
            }
            Some(DeferredRefusal::Unclassified) => {
                return Ok(Outcome::blocked(value.unclassified_blocked()));
            }
            None => {}
        }
        let storage = store.storage;
        match self.store_performance_now(store, value) {
            Err(error) if is_fatal_admission(&error) => Err(error),
            Err(_) if storage.configuration_failed() => Ok(Outcome::blocked(
                "Saved configuration replacement failed. Intent was not accepted; no thermal command was submitted. Storage reconciliation is required.",
            )),
            Err(_) => Ok(Outcome::blocked(
                "Performance intent was not accepted; admission or protected persistence failed.",
            )),
            stored => stored,
        }
    }
    /// Every check and read of the store first, then the save and the debt
    /// together: an error before the save stores nothing.
    fn store_performance_now(
        &self,
        store: triggers::DeferredStore<'_>,
        staged: Staged,
    ) -> io::Result<Outcome> {
        use triggers::BATTERY;
        let triggers::DeferredStore {
            storage,
            fresh,
            application,
            pair,
        } = store;
        let generation = fresh
            .power_generation()
            .ok_or(io::ErrorKind::PermissionDenied)?;
        storage.observe_power(BATTERY, generation)?;
        // A saved document the domain decoder refuses stores nothing.
        let layer = storage.profile_layer(application)?;
        layer.at(BATTERY)?;
        // Only an explicit value without the shortcut feature.
        #[cfg_attr(
            not(feature = "service-performance-shortcut-v1"),
            allow(clippy::infallible_destructuring_match)
        )]
        let value = match staged {
            Staged::Value(value) => value,
            // Nothing is read from firmware while the gate is set, so the
            // shortcut cycles from the saved intent at Windows battery; the
            // runtime keeps no confirmed reading for managed's cached
            // `ExternalChanges` and `ConfirmedState` to select from.
            #[cfg(feature = "service-performance-shortcut-v1")]
            Staged::Shortcut(_) => {
                match shortcut::stage(&shortcut::Reading {
                    storage,
                    fresh,
                    power: BATTERY,
                    layer: &layer,
                    baseline: None,
                    external_performance: false,
                })? {
                    Ok(value) => Some(value),
                    Err(cause) => return Ok(Outcome::blocked(cause.message())),
                }
            }
        };
        if let Some(message) = combination_rejected(storage, fresh, value.as_ref(), BATTERY)? {
            return Ok(Outcome {
                status: 3,
                message: Cause::Rejected(message).message(),
            });
        }
        let cleared = value.is_none();
        storage.save(value, BATTERY)?;
        self.owe_stored_intent(pair);
        Ok(deferred_performance(cleared))
    }
}
/// Managed's committed message for an explicit performance request.
fn performance_committed(cleared: bool) -> &'static str {
    if cleared {
        "Performance override was cleared."
    } else {
        "Performance override intent was stored."
    }
}
/// The reply of an explicit performance request whose intent was stored
/// while the low-power gate was set: the committed message with managed's
/// per-write refusal (`LOW_POWER_PERFORMANCE`).
#[cfg(feature = "service-profile-triggers-v1")]
fn deferred_performance(cleared: bool) -> Outcome {
    Outcome::blocked(format!(
        "{} Hardware result: {}",
        performance_committed(cleared),
        triggers::LOW_POWER_PERFORMANCE
    ))
}
/// The refusal of a shortcut that staged nothing (`Cause::Shortcut`).
#[cfg(feature = "service-performance-shortcut-v1")]
fn shortcut_refusal(permit: &RequestPermit) -> io::Result<Option<String>> {
    Ok(
        match &*permit.cause.lock().map_err(|_| io::ErrorKind::Other)? {
            cause @ Cause::Shortcut(_) => Some(cause.message()),
            _ => None,
        },
    )
}
#[cfg(not(feature = "service-performance-shortcut-v1"))]
fn shortcut_refusal(_: &RequestPermit) -> io::Result<Option<String>> {
    Ok(None)
}
/// Managed `ValidateUnclassifiedPerformanceOverride`: on unclassified power
/// it rejects native overclocking, legacy Custom offsets included, and the
/// battery presets before any intent is stored, since ordinary external
/// presence admits none of them. It then rejects Custom settings the model
/// capability does not admit.
fn unclassified_rejected(v: &Override) -> Option<Cause> {
    let custom = v.custom_performance.as_ref();
    if v.gpu_clock_tuning.is_some()
        || Preset::from_wire(v.value).is_some_and(Preset::is_battery)
        || custom.is_some_and(|c| c.get("gpuClockOffsets").is_some_and(|o| !o.is_null()))
    {
        Some(Cause::UnclassifiedRejected)
    } else if v.value == CUSTOM
        && custom
            .and_then(super::thermal_storage::custom_destination)
            .is_none()
    {
        Some(Cause::UnclassifiedCustomRejected)
    } else {
        None
    }
}
/// Managed `ValidateThermalOverrideCombination` on classified power, run
/// before the performance override `value` is stored: the requested preset
/// (or the active power profile's, for a clear) with the saved fan override
/// (or the profile's fans) must pass `ProfileSetValidator
/// .ValidatePerformanceFanCombination` for that source, managed's power
/// source (`managed_power_source`): Battery under Windows battery, whatever
/// the adapter reads.
fn combination_rejected(
    storage: &Storage,
    fresh: &dyn Fresh,
    value: Option<&Override>,
    power: i32,
) -> io::Result<Option<String>> {
    let Some(power) = managed_power_source(power, fresh) else {
        return Ok(None);
    };
    let Some((profile_performance, profile_fans)) = storage.profile_thermal(power)? else {
        return Ok(None);
    };
    let performance = value.map_or(profile_performance, |v| v.value);
    let fans = storage
        .current_fan_override(power)?
        .map_or(profile_fans, |f| i32::from(f.value));
    Ok(thermal_combination(performance, fans, power).err())
}
/// Managed `ProfileSetValidator.ValidatePerformanceFanCombination` with the
/// exact RZ09-0581 policy that profile saves use
/// (`CreateForDeviceCapabilities`). `power` is a classified source.
fn thermal_combination(performance: i32, fans: i32, power: i32) -> Result<(), String> {
    use openblade_domain::profile_set::{
        Capabilities, FanMode, PerformancePreset, PowerSource, ValidationPolicy, Validator,
    };
    let source = match power {
        0 => PowerSource::PluggedIn,
        1 => PowerSource::Battery,
        _ => PowerSource::UsbC,
    };
    let capabilities = Capabilities::rz09_0581();
    let policy = ValidationPolicy::for_capabilities(&capabilities, &capabilities);
    Validator::validate_performance_fan_combination(
        &policy,
        PerformancePreset(performance),
        FanMode(fans),
        source,
    )
    .map_err(|invalid| invalid.0)
}
/// Managed `SetFanOverrideCoreAsync` on classified power `power`, before the
/// override is stored: `FanControlPowerPolicy.IsAvailable` for a requested
/// mode, then `ValidateThermalOverrideCombination` with the saved performance
/// override (or the profile's preset) and the requested mode (or the
/// profile's fans, for a clear).
pub(super) fn fan_override_rejected(
    requested: Option<i32>,
    performance: i32,
    profile_fans: i32,
    power: i32,
) -> Option<String> {
    use openblade_domain::profile_set::{Capabilities, FanMode, PowerSource};
    let source = match power {
        0 => PowerSource::PluggedIn,
        1 => PowerSource::Battery,
        _ => PowerSource::UsbC,
    };
    let supports_limited = Capabilities::rz09_0581().supports_manual_fans_on_limited_power;
    if let Some(mode) = requested
        && !FanMode(mode).available_on(source, supports_limited)
    {
        return Some("The selected fan mode is unavailable on this model and power source.".into());
    }
    thermal_combination(performance, requested.unwrap_or(profile_fans), power).err()
}
impl Permit for RequestPermit {
    #[cfg_attr(not(feature = "service-hyperboost-v1"), allow(unused_variables))]
    fn admits_setter(&self, cancelled: &AtomicBool) -> bool {
        #[cfg(feature = "service-hyperboost-v1")]
        if let Some(step) = &self.hyperboost {
            return self.hyperboost_admits_setter(step, cancelled);
        }
        true
    }
    fn failed(&self, error: &io::Error) {
        self.remember_failure(error);
    }
    fn authorize(&self) -> Option<ReadAdmission> {
        self.verify_failure().ok()?;
        // A convergence attempt's scope closes at Stop or at a refusal
        // (`thermal_drift::Scope::open`).
        #[cfg(feature = "service-profile-triggers-v1")]
        if self.transition.as_ref().is_some_and(|t| !t.open()) {
            return None;
        }
        self.fresh.authorize()
    }
    fn windows_on_battery(&self) -> Option<bool> {
        self.fresh.on_battery()
    }
    fn thermal_fenced(&self, budget: std::time::Duration) -> bool {
        self.gpu_fence.as_ref().is_some_and(|fenced| fenced(budget))
    }
    #[cfg(feature = "service-hyperboost-v1")]
    fn restores_only(&self) -> bool {
        self.hyperboost
            .as_ref()
            .is_some_and(hyperboost::Step::restores_only)
    }
    // A paired NVML submission follows the firmware setters inside the
    // request (`thermal_gpu::Lane::complete`).
    #[cfg(feature = "service-gpu-clock-tuning-v1")]
    fn reserve(&self) -> std::time::Duration {
        match *self
            .gpu_half
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner)
        {
            Some(thermal_gpu::Half::Paired(_) | thermal_gpu::Half::PairedReuse) => {
                thermal_gpu::COMPLETE_RESERVE
            }
            _ => std::time::Duration::ZERO,
        }
    }
    fn destination_holds(&self, power: i32) -> bool {
        (|| -> io::Result<bool> {
            let Some(destination) = *self.destination.lock().map_err(|_| io::ErrorKind::Other)?
            else {
                return Ok(false);
            };
            self.observe_power(power)?;
            // Managed's unowned exit resolves nothing from the saved intent, so
            // a class its restore rule admits keeps it (`Permit::restores_only`).
            #[cfg(feature = "service-hyperboost-v1")]
            if self.restores_only() {
                return Ok(true);
            }
            // Resolved again from the saved profiles as they are now.
            let layer = self.storage.profile_layer(&self.application)?;
            let profile = layer.at(power)?;
            // With GPU clock tuning the class must also select the tuning the
            // first half was decided for; without it, no GPU clock effect.
            let gpu_holds = if self.gpu_composed() {
                self.gpu_tuning_holds(power, profile.as_ref())?
            } else {
                !self.storage.gpu_incomplete(power, profile.as_ref())?
            };
            Ok(gpu_holds
                && self
                    .storage
                    .desired(power, profile.as_ref())?
                    .map(|(p, _)| p)
                    == Some(destination))
        })()
        .inspect_err(|error| self.remember_failure(error))
        .unwrap_or(false)
    }
    #[cfg_attr(not(feature = "service-hyperboost-v1"), allow(unused_variables))]
    fn resolve(
        &self,
        power: i32,
        baseline: ObservedPerformance,
        cancelled: &AtomicBool,
    ) -> io::Result<Option<Destination>> {
        self.verify_failure()?;
        (|| {
            if self.accepted.load(Ordering::Acquire) {
                return Err(io::ErrorKind::PermissionDenied.into());
            }
            let unclassified = power == UNCLASSIFIED_EXTERNAL;
            // Managed `CanAdmitUnclassifiedExternalControl` needs confirmed
            // HyperBoost to be inactive, and the HyperBoost exit needs classified
            // power, so nothing is stored or written. Managed applies saved
            // intent only on classified power, so a reconciliation that finds
            // the adapter unclassified writes nothing either.
            if unclassified
                && (self.reconcile.is_some() || baseline == ObservedPerformance::HyperBoostObserved)
            {
                self.set_cause(self.unclassified_blocked());
                return Ok(None);
            }
            if let Some(cause) = self
                .value()
                .filter(|_| unclassified)
                .and_then(unclassified_rejected)
            {
                self.set_cause(cause);
                return Ok(None);
            }
            if battery_reading_contradicts_windows(power, self.fresh.as_ref()) {
                self.set_cause(self.unclassified_blocked());
                return Ok(None);
            }
            self.observe_power(power)?;
            // A reconciliation applies only at the classes its trigger changed;
            // only this reading tells the external classes apart.
            if self
                .reconcile
                .is_some_and(|classes| !classes.contains(power))
            {
                self.set_cause(Cause::Unchanged);
                return Ok(None);
            }
            // An owned HyperBoost request resolves its own destination and
            // stores nothing (`hyperboost`).
            #[cfg(feature = "service-hyperboost-v1")]
            if let Some(step) = &self.hyperboost {
                return self.resolve_hyperboost(step, power, baseline, cancelled);
            }
            // The profile layer is resolved before the override is stored, as
            // the fan path does: a saved document the domain decoder refuses
            // stores nothing and the request is not accepted. Staging and
            // saving the override change no profile, so this one read serves
            // the shortcut and the destination.
            let layer = self.storage.profile_layer(&self.application)?;
            // A shortcut stages its override from this reading, after the
            // class it was read at expired any temporary it retires, and the
            // override then takes every check of an explicit request (managed
            // `CyclePerformanceShortcutAsync`).
            #[cfg(feature = "service-performance-shortcut-v1")]
            if self.shortcut.is_some() && self.requested.get().is_none() {
                let staged = self.stage_shortcut(power, baseline, &layer)?;
                // Managed validates the staged override as it would an
                // explicit one (`ValidateUnclassifiedPerformanceOverride`).
                match staged.map(|value| {
                    let rejected = unclassified
                        .then(|| unclassified_rejected(&value))
                        .flatten();
                    (value, rejected)
                }) {
                    Ok((_, Some(cause))) | Err(cause) => {
                        self.set_cause(cause);
                        return Ok(None);
                    }
                    Ok((value, None)) => {
                        self.requested
                            .set(Requested::new(Some(value))?)
                            .map_err(|_| io::ErrorKind::PermissionDenied)?;
                    }
                }
            }
            // A reconciliation applies intent already saved: managed
            // `ApplyFullTransitionAsync` validates no override and stores nothing.
            if self.reconcile.is_none()
                && let Some(message) =
                    combination_rejected(&self.storage, self.fresh.as_ref(), self.value(), power)?
            {
                self.set_cause(Cause::Rejected(message));
                return Ok(None);
            }
            let profile = layer.at(power)?;
            if self.reconcile.is_some() {
                // Nothing is stored, so the override document the saved intent
                // resolves over is checked as `save`'s replacement would check
                // it: a document changed outside the Service fences writes
                // before any submission.
                self.storage
                    .verify_overrides()
                    .inspect_err(|e| self.set_cause(Cause::Storage(e.to_string())))?;
            } else {
                self.storage
                    .save(self.value().cloned(), power)
                    .inspect_err(|e| self.set_cause(Cause::Storage(e.to_string())))?;
            }
            self.accepted.store(true, Ordering::Release);
            *self.power.lock().map_err(|_| io::ErrorKind::Other)? = Some(power);
            // A clear selects no profile there: nothing is written until the
            // adapter is classified (managed `ExecuteScopedMutationAsync`).
            if unclassified && self.value().is_none() {
                self.set_cause(Cause::AwaitingClassification);
                return Ok(None);
            }
            let selection = select(
                &self.storage,
                power,
                profile.as_ref(),
                baseline,
                self.gpu_composed(),
            )?;
            *self
                .selected_source
                .lock()
                .map_err(|_| io::ErrorKind::Other)? = selection.desired.map(|(_, s)| s);
            let desired = selection.desired.map(|(p, _)| p);
            *self.destination.lock().map_err(|_| io::ErrorKind::Other)? = desired;
            #[cfg(feature = "service-profile-triggers-v1")]
            {
                *self.baseline.lock().map_err(|_| io::ErrorKind::Other)? = Some(baseline);
            }
            // Before any cause that refuses the write: managed resolves its
            // write exclusions before it tries a subsystem.
            #[cfg(feature = "service-profile-triggers-v1")]
            if let Some(transition) = &self.transition
                && let Some(exclusion) = transition.decide(
                    baseline,
                    drift_intent(&self.storage, power, profile.as_ref())?,
                )?
            {
                self.set_cause(Cause::Excluded(exclusion));
                return Ok(None);
            }
            if let Some(cause) = selection.blocked {
                self.set_cause(cause);
                return Ok(None);
            }
            // The GPU half's first step, last: after the override was stored
            // and every firmware refusal decided, before any journal row or
            // setter, so a refusal here writes nothing (managed
            // `beforeInitialStateRead` and `PreflightGpuClockOffsets`).
            #[cfg(feature = "service-gpu-clock-tuning-v1")]
            if let Some(cause) = self.prepare_gpu(power, profile.as_ref(), baseline, desired)? {
                self.set_cause(cause);
                return Ok(None);
            }
            Ok(desired)
        })()
        .inspect_err(|error| self.remember_failure(error))
    }
    fn refused(&self, cause: Refusal) {
        self.set_cause(match cause {
            Refusal::TransactionCapacity => Cause::TransactionCapacity,
            Refusal::CurrentAdmission => Cause::CurrentAdmission,
            Refusal::PowerChanged => Cause::PowerChanged,
            Refusal::UnclassifiedBlocked => Cause::UnclassifiedBlocked,
            #[cfg(not(feature = "service-thermal-hyperboost-exit-v1"))]
            Refusal::NoDirectPath => Cause::NoDirectPath,
            Refusal::Deadline => Cause::Deadline,
            Refusal::ThermalPending if self.reconcile.is_some() => {
                Cause::ThermalPending(PENDING_OTHER)
            }
            Refusal::ThermalPending => Cause::ThermalPending(PENDING_PERFORMANCE),
        });
    }
    fn retired(&self) -> io::Result<Settlement> {
        self.verify_failure()?;
        // Transport retirement is already positive. Settlement is attempted once;
        // an uncertain append keeps the original evidence and global write fence.
        self.fresh.verify_health()?;
        let result = if self.accepted.load(Ordering::Acquire) && self.storage.progress() == Some(0)
        {
            self.record_progress(Progress::KnownNotSubmitted)
        } else {
            Ok(())
        };
        self.fresh.verify_health()?;
        match result {
            Ok(()) => Ok(Settlement::Complete),
            Err(error) if is_fatal_admission(&error) => Err(error),
            Err(error) => {
                self.storage.quarantine_settlement()?;
                Ok(Settlement::Unresolved(error))
            }
        }
    }
    fn record(&self, progress: Progress) -> io::Result<()> {
        self.verify_failure()?;
        self.authorize().ok_or(io::ErrorKind::PermissionDenied)?;
        // A convergence attempt admits each setter's intent afresh, before
        // its row (`thermal_drift::Scope::admits`).
        #[cfg(feature = "service-profile-triggers-v1")]
        if progress.is_intent() && self.transition.as_ref().is_some_and(|t| !t.admits(false)) {
            self.set_cause(Cause::CurrentAdmission);
            return Err(io::ErrorKind::PermissionDenied.into());
        }
        self.record_progress(progress)
            .inspect_err(|e| self.set_cause(Cause::Storage(e.to_string())))?;
        // A journaled intent precedes its setter. A step dropped at its bound
        // or deadline, or failed after it, has no readback of its own, so the
        // next reading decides whether the write landed (`submitted`); the
        // row it leaves unsettled still fences writes.
        #[cfg(feature = "service-profile-triggers-v1")]
        if progress.is_intent()
            && let Some(destination) = *self.destination.lock().map_err(|_| io::ErrorKind::Other)?
        {
            let from = *self.baseline.lock().map_err(|_| io::ErrorKind::Other)?;
            // The HyperBoost restore pair writes Balanced, so a stop after
            // one of its setters leaves Balanced as OpenBlade's own write.
            #[cfg(feature = "service-thermal-hyperboost-exit-v1")]
            let destination = match progress {
                Progress::RestoreFirstIntent | Progress::RestoreSecondIntent => {
                    Destination::Preset(Preset::Balanced)
                }
                _ => destination,
            };
            self.drift
                .lock()
                .map_err(|_| io::ErrorKind::Other)?
                .submitted_from(destination, from);
        }
        Ok(())
    }
}
/// The thermal intent a reconciliation's drift decision compares at class
/// `power` (`thermal_drift::Desired`): the destination `select` resolves,
/// none for an incomplete GPU clock policy, and the saved fan mode toward
/// it. Both steps compare the same intent, whichever reads first.
#[cfg(feature = "service-profile-triggers-v1")]
pub(super) fn drift_intent(
    storage: &Storage,
    power: i32,
    profile: Option<&Resolution<'_>>,
) -> io::Result<thermal_drift::Desired> {
    let performance = if storage.gpu_incomplete(power, profile)? {
        None
    } else {
        storage
            .desired(power, profile)?
            .map(|(destination, _)| destination)
    };
    Ok(thermal_drift::Desired {
        performance,
        fans: thermal_drift::Fans::of(
            storage.desired_fan_mode(power, profile)?,
            power,
            performance,
        ),
    })
}
impl RequestPermit {
    fn record_progress(&self, progress: Progress) -> io::Result<()> {
        let destination = self
            .destination
            .lock()
            .map_err(|_| io::ErrorKind::Other)?
            .ok_or(io::ErrorKind::PermissionDenied)?;
        let requested = self
            .requested
            .get()
            .ok_or(io::ErrorKind::PermissionDenied)?;
        self.storage
            .record(
                &self.request,
                &self.owner,
                destination,
                &requested.intent,
                &requested.value,
                self.caller_pid,
                self.session,
                progress,
            )
            .inspect_err(|error| self.remember_failure(error))
    }
}

#[cfg(all(test, feature = "service-battery-policy-v1"))]
impl Runtime {
    pub(super) fn battery_clock_for_tests(&self) -> Rc<dyn Fn() -> Option<Instant>> {
        let history = self.battery_observation.clone();
        Rc::new(move || history.lock().unwrap().policy.map(|(_, at)| at))
    }
}

#[cfg(all(test, feature = "service-fan-mutation-v2"))]
impl Runtime {
    /// The settled pins waiting for a Reader to unpin them.
    pub(super) fn queued_pins_for_tests(&self) -> Vec<u8> {
        self.settled_pins.peek()
    }
}

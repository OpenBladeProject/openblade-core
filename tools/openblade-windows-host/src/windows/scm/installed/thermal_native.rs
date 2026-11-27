//! Real Windows suppliers, compile-only during offline verification.
#![allow(unsafe_code)]
#[cfg(feature = "service-fan-mutation-v2")]
use super::mailbox_read::CleanupScope;
use super::{mailbox_read::ReadCustody, thermal_storage::Files};
use crate::discovery::performance::ReadAdmission;
use crate::{
    loaded_origin::Origin,
    windows_protected_path::{self as paths, Policy},
};
use openblade_domain::low_power_gate::WriteScope;
use std::{
    ffi::OsStr,
    fs::File,
    io::{self, Read, Seek, Write},
    os::windows::io::{FromRawHandle, OwnedHandle},
    path::Path,
    sync::{Arc, Mutex},
};
use windows_sys::{
    Wdk::Storage::FileSystem::{
        FILE_CREATE, FILE_NON_DIRECTORY_FILE, FILE_OPEN, FILE_OPEN_IF, FILE_OPEN_REPARSE_POINT,
    },
    Win32::{
        Storage::FileSystem::{DELETE, FILE_GENERIC_READ, FILE_GENERIC_WRITE, FILE_SHARE_READ},
        System::{
            Power::{GetSystemPowerStatus, SYSTEM_POWER_STATUS},
            RemoteDesktop::WTSQueryUserToken,
        },
    },
};
fn deny() -> io::Error {
    io::Error::new(
        io::ErrorKind::PermissionDenied,
        "fresh installed mutation admission is unavailable",
    )
}

/// The exact primary SID, session and logon one request capture starts from,
/// with every Windows supplier check of its admissions (`Identity::admits`).
/// It pins no lifecycle epoch, so it never authorizes a read or write: only a
/// `WindowsGuard`, which always pins one, does.
#[cfg_attr(test, allow(dead_code))] // Test composition injects its own Fresh; only the native factory builds the guard.
struct Identity {
    origin: Arc<Origin>,
    custody: Arc<dyn ReadCustody>,
    sid: String,
    session: u32,
    authentication: (u32, i32),
    controllers:
        Mutex<openblade_platform_windows::controller_presence::admission::ReadOnlyAdmission>,
}
#[cfg_attr(test, allow(dead_code))] // Test composition injects its own Fresh; only the native factory builds the guard.
impl Identity {
    /// The exact primary SID and logon of `session`. Every request capture
    /// starts here.
    fn new(
        origin: Arc<Origin>,
        custody: Arc<dyn ReadCustody>,
        sid: String,
        session: u32,
    ) -> io::Result<Self> {
        origin.lifecycle.verify_health().map_err(|_| {
            super::thermal_mutation::fatal_admission("lifecycle state lock poisoned")
        })?;
        origin.mutation_artifact()?;
        let identity = session_identity(session)?;
        if identity.sid != sid || identity.session != session || !identity.primary {
            return Err(deny());
        }
        origin.lifecycle.verify_health().map_err(|_| {
            super::thermal_mutation::fatal_admission("lifecycle state lock poisoned")
        })?;
        Ok(Self {
            origin,
            custody,
            sid,
            session,
            authentication: (
                identity.authentication.LowPart,
                identity.authentication.HighPart,
            ),
            controllers: Mutex::new(
                openblade_platform_windows::controller_presence::admission::ReadOnlyAdmission::native(),
            ),
        })
    }
    /// The same identity with a fresh competing-controller admission.
    fn renewed(&self) -> Self {
        Self {
            origin: self.origin.clone(),
            custody: self.custody.clone(),
            sid: self.sid.clone(),
            session: self.session,
            authentication: self.authentication,
            controllers: Mutex::new(
                openblade_platform_windows::controller_presence::admission::ReadOnlyAdmission::native(),
            ),
        }
    }
    /// Samples the Windows power kind into the loaded origin's lifecycle
    /// and returns its power generation.
    fn power_generation(&self) -> Option<u64> {
        self.origin
            .lifecycle
            .observe_power_kind(crate::origin_lifetime::sample_power_kind())
    }
    fn on_battery(&self) -> Option<bool> {
        windows_on_battery()
    }
    fn verify_health(&self) -> io::Result<()> {
        self.custody.verify_integrity()?;
        self.origin.lifecycle.verify_health().map_err(|_| {
            super::thermal_mutation::fatal_admission("lifecycle state lock poisoned")
        })?;
        self.controllers.lock().map(|_| ()).map_err(|_| {
            super::thermal_mutation::fatal_admission("controller admission lock poisoned")
        })
    }
    /// Whether storing request intent is admitted now: every check of
    /// `WindowsGuard::authorize` (origin and package trust, the exact mailbox
    /// custody and generation, the same console session unlocked, the same
    /// primary SID and logon LUID, a known AC line status and fresh
    /// competing-controller absence) except the lifecycle epoch. It admits no
    /// read or write. It names no device: the HID identity belongs to the
    /// mailbox custody, admitted when the Service opened it, and only a
    /// write's own `authorize` is checked against that custody's reader.
    #[cfg(feature = "service-profile-triggers-v1")]
    fn authorize_storage(&self) -> bool {
        self.admits(false, || true).is_ok() && self.custody.authorize_storage()
    }
    /// The Windows supplier checks of one admission. `epoch` is its
    /// lifecycle check, run before and after the suppliers: a write compares
    /// its pinned epoch, and storage admits any lifecycle state.
    fn admits(&self, stop: bool, epoch: impl Fn() -> bool) -> Result<(), Denial> {
        let verify = || {
            if stop {
                self.origin.verify_owned_stop()
            } else {
                self.origin.verify()
            }
        };
        verify().map_err(|_| Denial::Admission)?;
        if !epoch() {
            return Err(Denial::Admission);
        }
        if !if stop {
            self.origin
                .owned_stop_committed()
                .map_err(|_| Denial::Admission)?
        } else {
            self.origin.committed().map_err(|_| Denial::Admission)?
        } || crate::console_session::observe() != Some(self.session)
        {
            return Err(Denial::Admission);
        }
        if !session_unlocked(self.session) {
            return Err(Denial::Admission);
        }
        let identity = session_identity(self.session).map_err(|_| Denial::Admission)?;
        if !identity.primary
            || identity.session != self.session
            || identity.sid != self.sid
            || (
                identity.authentication.LowPart,
                identity.authentication.HighPart,
            ) != self.authentication
        {
            return Err(Denial::Admission);
        }
        power_and_controllers(&self.controllers)?;
        // Mutable callback fencing cannot revive an earlier operation. A new
        // request captures a fresh epoch and rechecks every Windows supplier.
        verify().map_err(|_| Denial::Admission)?;
        if epoch() {
            Ok(())
        } else {
            Err(Denial::Admission)
        }
    }
}
/// The fresh Windows power kind: `Some(true)` battery, `Some(false)` AC and
/// `None` unknown, with the same documented ACLineStatus mapping as lifecycle
/// Observation.
#[cfg_attr(test, allow(dead_code))] // Test composition injects its own Fresh; only the native factory builds the guard.
fn windows_on_battery() -> Option<bool> {
    let mut status = SYSTEM_POWER_STATUS::default();
    // SAFETY: writable native output.
    if unsafe { GetSystemPowerStatus(&mut status) } == 0 {
        return None;
    }
    match status.ACLineStatus {
        0 => Some(true),
        1 => Some(false),
        _ => None,
    }
}
/// The power and controller checks every admission ends with: a known AC
/// line status, then fresh competing-controller absence. Every check before
/// them is the session's, the origin's or the lifecycle's; only the last one
/// says another controller may own the device. Detection that failed or ran
/// too long confirms no absence and is refused the same way, as managed
/// treats a detection warning as a controller.
#[cfg_attr(test, allow(dead_code))] // Test composition injects its own Fresh; only the native factory builds the guard.
fn power_and_controllers(
    controllers: &Mutex<
        openblade_platform_windows::controller_presence::admission::ReadOnlyAdmission,
    >,
) -> Result<(), Denial> {
    power_known()?;
    if !controllers
        .lock()
        .map_err(|_| Denial::Admission)?
        .confirms_fresh_absence()
    {
        return Err(Denial::CompetingController);
    }
    Ok(())
}
/// A known AC line status. This excludes an unknown coarse source only; typed
/// 07/8C independently decides sufficient versus limited adapter.
#[cfg_attr(test, allow(dead_code))] // Test composition injects its own Fresh; only the native factory builds the guard.
fn power_known() -> Result<(), Denial> {
    let mut status = SYSTEM_POWER_STATUS::default();
    // SAFETY: writable native output.
    if unsafe { GetSystemPowerStatus(&mut status) } == 0 || status.ACLineStatus > 1 {
        return Err(Denial::Admission);
    }
    Ok(())
}
/// Why `Identity::admits` refused.
#[cfg_attr(test, allow(dead_code))] // Test composition injects its own Fresh; only the native factory builds the guard.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(super) enum Denial {
    /// Any check but the competing-controller one: origin trust, the
    /// lifecycle epoch, the console session, its lock state, the primary
    /// identity or the AC line status.
    Admission,
    /// Fresh competing-controller absence was not confirmed.
    CompetingController,
}
#[cfg_attr(test, allow(dead_code))] // Test composition injects its own Fresh; only the native factory builds the guard.
impl Denial {
    /// The capture's error: a typed one for a competing controller, which
    /// the lifecycle tick asks for (`is_competing_controller`).
    pub(super) fn error(self) -> io::Error {
        match self {
            Self::Admission => deny(),
            Self::CompetingController => super::thermal_mutation::competing_controller(),
        }
    }
}
/// A request's read and write admission: its identity at the ordinary
/// lifecycle epoch it pins. Every `authorize` compares that epoch again, so
/// the low-power gate, which admits no ordinary epoch, refuses it. A guard
/// without an epoch cannot be built; storage takes a `StorageGuard`.
#[cfg_attr(test, allow(dead_code))] // Test composition injects its own Fresh; only the native factory builds the guard.
pub(super) struct WindowsGuard {
    identity: Identity,
    lifecycle: u64,
}
#[cfg_attr(test, allow(dead_code))] // Test composition injects its own Fresh; only the native factory builds the guard.
impl WindowsGuard {
    pub(super) fn power_generation(&self) -> Option<u64> {
        self.identity.power_generation()
    }
    pub(super) fn on_battery(&self) -> Option<bool> {
        self.identity.on_battery()
    }
    pub(super) fn capture(
        origin: Arc<Origin>,
        custody: Arc<dyn ReadCustody>,
        sid: String,
        session: u32,
    ) -> io::Result<Arc<Self>> {
        let identity = Identity::new(origin, custody, sid, session)?;
        let lifecycle = identity.origin.lifecycle.active().ok_or_else(deny)?;
        let guard = Arc::new(Self {
            identity,
            lifecycle,
        });
        let proof = guard.admit_for(false);
        guard.verify_health()?;
        proof.map_err(Denial::error)?;
        Ok(guard)
    }
    /// A capture that admits storing request intent and nothing else: every
    /// check of `capture` but the lifecycle epoch
    /// (`Identity::authorize_storage`), so it succeeds while the low-power
    /// gate is set or the lifecycle is exhausted. Its guard holds no epoch
    /// and authorizes no read or write: a write needs its own `capture` with
    /// the lifecycle active, and each write's `authorize` compares that
    /// epoch again.
    #[cfg(feature = "service-profile-triggers-v1")]
    pub(super) fn capture_storage(
        origin: Arc<Origin>,
        custody: Arc<dyn ReadCustody>,
        sid: String,
        session: u32,
    ) -> io::Result<Arc<StorageGuard>> {
        let identity = Identity::new(origin, custody, sid, session)?;
        let admitted = identity.authorize_storage();
        identity.verify_health()?;
        if !admitted {
            return Err(deny());
        }
        Ok(Arc::new(StorageGuard(identity)))
    }
    pub(super) fn authentication(&self) -> (u32, i32) {
        self.identity.authentication
    }
    pub(super) fn verify_health(&self) -> io::Result<()> {
        self.identity.verify_health()
    }
    pub(super) fn authorize(&self) -> Option<ReadAdmission> {
        self.authorize_for(false)
    }
    pub(super) fn authorize_owned_stop(&self) -> Option<ReadAdmission> {
        self.authorize_for(true)
    }
    #[cfg(feature = "service-profile-triggers-v1")]
    pub(super) fn authorize_storage(&self) -> bool {
        self.identity.authorize_storage()
    }
    /// A fresh Stop-scoped capture of this owner's exact identity at the
    /// current lifecycle epoch, for the settle-only fan close. Every
    /// SERVICE_CONTROL_POWEREVENT and SESSIONCHANGE advances the epoch, so the
    /// owner's own capture cannot authorize Stop after an unplug, a lock or a
    /// resume. This one pins the new epoch and keeps every other check of
    /// `authorize_for(true)`: owned-stop origin and package trust, the exact
    /// mailbox custody and generation, the same console session unlocked, the
    /// same primary SID and logon LUID, a known AC line status and fresh
    /// competing-controller absence. The low-power gate admits no ordinary epoch,
    /// so nothing is captured while it is set.
    pub(super) fn capture_settle(
        &self,
        sid: &str,
        session: u32,
        authentication: Option<(u32, i32)>,
    ) -> io::Result<Arc<SettleGuard>> {
        let identity = &self.identity;
        if sid != identity.sid
            || session != identity.session
            || authentication != Some(identity.authentication)
        {
            return Err(deny());
        }
        self.verify_health()?;
        identity.origin.verify_owned_stop()?;
        let lifecycle = identity.origin.lifecycle.active().ok_or_else(deny)?;
        let guard = SettleGuard(Self {
            identity: identity.renewed(),
            lifecycle,
        });
        let proof = guard.authorize_owned_stop();
        guard.verify_health()?;
        proof.ok_or_else(deny)?;
        Ok(Arc::new(guard))
    }
    fn authorize_for(&self, stop: bool) -> Option<ReadAdmission> {
        self.admit_for(stop).ok()
    }
    /// One animated stream frame (`Runtime::stream_frame`): the checks a
    /// frame repeats between two origin ticks. The pinned lifecycle epoch,
    /// before and after the others, which every suspend, resume, session
    /// change and power notice advances; the same console session, unlocked
    /// (WTS); a known AC line status (`GetSystemPowerStatus`); and the
    /// custody's frame admission (`ReadCustody::authorize_frame`): its state,
    /// its retained failure and the loaded origin's lifetime, which a
    /// revocation or a lost trust ends at once.
    ///
    /// Deliberately not per frame, but from the origin tick's clean reading,
    /// a full `capture` no older than 1 s: the loaded-origin process, package
    /// (ACL and `current.bin`) and SCM verification, the original mapping
    /// (`managed-handoff.bin`), the journal custody, the primary SID and
    /// logon, and the competing-controller inventory. A per-frame pass would
    /// run those file, SCM and inventory queries at frame rate; a change to
    /// any of them is seen at the next tick.
    #[cfg(feature = "service-lighting-v1")]
    pub(super) fn authorize_frame(&self) -> Option<ReadAdmission> {
        let identity = &self.identity;
        let epoch =
            || identity.origin.lifecycle.epoch_for(WriteScope::Ordinary) == Some(self.lifecycle);
        let admitted = epoch()
            && crate::console_session::observe() == Some(identity.session)
            && session_unlocked(identity.session)
            && power_known().is_ok()
            && epoch();
        if !admitted {
            return None;
        }
        identity.custody.authorize_frame()
    }
    /// `authorize_for` with the reason for a refusal.
    fn admit_for(&self, stop: bool) -> Result<ReadAdmission, Denial> {
        // The low-power gate refuses every request and owned-Stop write here.
        // Only the Service's low-power cleanup (`CleanupGuard`) may pin
        // `WriteScope::LowPowerCleanup`.
        let identity = &self.identity;
        identity.admits(stop, || {
            identity.origin.lifecycle.epoch_for(WriteScope::Ordinary) == Some(self.lifecycle)
        })?;
        if stop {
            identity.custody.authorize_owned_stop()
        } else {
            identity.custody.authorize()
        }
        .ok_or(Denial::Admission)
    }
}
/// Stop-only admission captured by `WindowsGuard::capture_settle`. It exposes
/// only the owned-stop proof, never request admission. Test composition
/// injects its own `Fresh`, so only the native factory constructs it.
#[cfg_attr(test, allow(dead_code))] // Test composition injects its own Fresh; only the native factory builds the guard.
pub(super) struct SettleGuard(WindowsGuard);
#[cfg_attr(test, allow(dead_code))] // Test composition injects its own Fresh; only the native factory builds the guard.
impl SettleGuard {
    pub(super) fn verify_health(&self) -> io::Result<()> {
        self.0.verify_health()
    }
    pub(super) fn authorize_owned_stop(&self) -> Option<ReadAdmission> {
        self.0.authorize_for(true)
    }
}
/// The Service's owned-cleanup admission (`thermal_mutation::Cleanup`): the
/// keyboard Normal release, the owned fan Automatic handback and the owned
/// HyperBoost exit at Stop, at shutdown and at a confirmed low-power
/// boundary (the fans at Stop and shutdown only). It names no console user,
/// SID or logon, because the cleanup is the Service's own and must run with
/// nobody signed in or the session locked (managed
/// `ReleaseDriverModeCoreAsync` and `FreshLightingCleanupAdmission`). Every
/// other check of the console admission stays and runs fresh on each
/// `authorize`: origin and package trust for the scope (owned-stop trust for
/// Stop and shutdown, ordinary trust for low power), the lifecycle epoch the
/// scope pinned, a known AC line status, fresh competing-controller absence,
/// and the exact original mailbox custody and generation through
/// `ReadCustody::authorize_cleanup`, which names the admitted device.
#[cfg(feature = "service-fan-mutation-v2")]
#[cfg_attr(test, allow(dead_code))] // Test composition injects its own Cleanup; only the native factory builds the guard.
pub(super) struct CleanupGuard {
    origin: Arc<Origin>,
    custody: Arc<dyn ReadCustody>,
    scope: CleanupScope,
    lifecycle: u64,
    controllers:
        Mutex<openblade_platform_windows::controller_presence::admission::ReadOnlyAdmission>,
}
#[cfg(feature = "service-fan-mutation-v2")]
#[cfg_attr(test, allow(dead_code))] // Test composition injects its own Cleanup; only the native factory builds the guard.
impl CleanupGuard {
    /// Pins the lifecycle fence `scope` is admitted at (`Lifecycle::fence_for`):
    /// the ordinary epoch for Stop and shutdown, and the exact suspend
    /// generation for low power. The gate admits neither while the other
    /// holds, so a Stop capture refuses while suspending and a low-power
    /// capture refuses once resumed.
    pub(super) fn capture(
        origin: Arc<Origin>,
        custody: Arc<dyn ReadCustody>,
        scope: CleanupScope,
    ) -> io::Result<Arc<Self>> {
        origin.lifecycle.verify_health().map_err(|_| {
            super::thermal_mutation::fatal_admission("lifecycle state lock poisoned")
        })?;
        origin.mutation_artifact()?;
        let lifecycle = origin
            .lifecycle
            .fence_for(scope.write_scope())
            .ok_or_else(deny)?;
        let guard = Arc::new(Self {
            origin,
            custody,
            scope,
            lifecycle,
            controllers: Mutex::new(
                openblade_platform_windows::controller_presence::admission::ReadOnlyAdmission::native(),
            ),
        });
        let proof = guard.admit();
        guard.verify_health()?;
        proof.map_err(Denial::error)?;
        Ok(guard)
    }
    pub(super) fn scope(&self) -> CleanupScope {
        self.scope
    }
    pub(super) fn on_battery(&self) -> Option<bool> {
        windows_on_battery()
    }
    pub(super) fn verify_health(&self) -> io::Result<()> {
        self.custody.verify_integrity()?;
        self.origin.lifecycle.verify_health().map_err(|_| {
            super::thermal_mutation::fatal_admission("lifecycle state lock poisoned")
        })?;
        self.controllers.lock().map(|_| ()).map_err(|_| {
            super::thermal_mutation::fatal_admission("controller admission lock poisoned")
        })
    }
    pub(super) fn authorize(&self) -> Option<ReadAdmission> {
        self.admit().ok()
    }
    /// `authorize` with the reason for a refusal.
    fn admit(&self) -> Result<ReadAdmission, Denial> {
        let stop = self.scope == CleanupScope::OwnedStop;
        admit_cleanup(
            || {
                if stop {
                    self.origin.verify_owned_stop().is_ok()
                } else {
                    self.origin.verify().is_ok()
                }
            },
            || {
                if stop {
                    self.origin.owned_stop_committed().ok()
                } else {
                    self.origin.committed().ok()
                }
            },
            || self.origin.lifecycle.fence_for(self.scope.write_scope()) == Some(self.lifecycle),
            || power_and_controllers(&self.controllers),
            || self.custody.authorize_cleanup(self.scope),
        )
    }
}
/// The Service's cleanup admission over its suppliers, in order
/// (`CleanupGuard::admit`): origin trust, the scope's pinned lifecycle fence
/// and origin commitment (`None` when unreadable), a known AC line status and
/// fresh competing-controller absence, origin trust and the fence again, and
/// last the exact original custody's admission of the scope, which names the
/// admitted device. Each refusal stops the checks after it. Only a competing
/// controller is a typed refusal. The test model (`CleanupFresh`) runs the
/// same function over its own suppliers.
#[cfg(feature = "service-fan-mutation-v2")]
pub(super) fn admit_cleanup(
    verify: impl Fn() -> bool,
    committed: impl FnOnce() -> Option<bool>,
    fence: impl Fn() -> bool,
    power_and_controllers: impl FnOnce() -> Result<(), Denial>,
    custody: impl FnOnce() -> Option<ReadAdmission>,
) -> Result<ReadAdmission, Denial> {
    if !verify() || !fence() || committed() != Some(true) {
        return Err(Denial::Admission);
    }
    power_and_controllers()?;
    if !verify() || !fence() {
        return Err(Denial::Admission);
    }
    custody().ok_or(Denial::Admission)
}
/// Storage-only admission captured by `WindowsGuard::capture_storage`: an
/// identity with no lifecycle epoch. It exposes the storage admission and the
/// Windows power reads, and it cannot hold a read or write proof. Test
/// composition injects its own `Fresh`, so only the native factory
/// constructs it.
#[cfg(feature = "service-profile-triggers-v1")]
#[cfg_attr(test, allow(dead_code))] // Test composition injects its own Fresh; only the native factory builds the guard.
pub(super) struct StorageGuard(Identity);
#[cfg(feature = "service-profile-triggers-v1")]
#[cfg_attr(test, allow(dead_code))] // Test composition injects its own Fresh; only the native factory builds the guard.
impl StorageGuard {
    pub(super) fn authentication(&self) -> (u32, i32) {
        self.0.authentication
    }
    pub(super) fn verify_health(&self) -> io::Result<()> {
        self.0.verify_health()
    }
    pub(super) fn on_battery(&self) -> Option<bool> {
        self.0.on_battery()
    }
    pub(super) fn power_generation(&self) -> Option<u64> {
        self.0.power_generation()
    }
    pub(super) fn authorize_storage(&self) -> bool {
        self.0.authorize_storage()
    }
}
// Exact current-session read; source/compile-only during offline validation.
#[cfg_attr(test, allow(dead_code))] // Only the native session and path checks call this; tests inject them.
fn session_unlocked(session: u32) -> bool {
    use windows_sys::Win32::System::RemoteDesktop::{
        WTS_SESSIONSTATE_UNLOCK, WTSActive, WTSFreeMemory, WTSINFOEXW, WTSQuerySessionInformationW,
        WTSSessionInfoEx,
    };
    let mut buffer = std::ptr::null_mut();
    let mut bytes = 0;
    // SAFETY: writable returned buffer/length. Windows owns the allocation.
    if unsafe {
        WTSQuerySessionInformationW(
            std::ptr::null_mut(),
            session,
            WTSSessionInfoEx,
            &mut buffer,
            &mut bytes,
        )
    } == 0
    {
        return false;
    }
    let valid = if !buffer.is_null() && bytes as usize >= std::mem::size_of::<WTSINFOEXW>() {
        // SAFETY: checked complete structure, then its documented level-one union.
        let info = unsafe { buffer.cast::<WTSINFOEXW>().read_unaligned() };
        if info.Level == 1 {
            let level = unsafe { info.Data.WTSInfoExLevel1 };
            level.SessionId == session
                && level.SessionState == WTSActive
                && level.SessionFlags == WTS_SESSIONSTATE_UNLOCK as i32
        } else {
            false
        }
    } else {
        false
    };
    // SAFETY: release only the returned Windows allocation, on every success path.
    if !buffer.is_null() {
        unsafe { WTSFreeMemory(buffer.cast()) };
    }
    valid
}
/// The console session and its primary user's SID, as `Identity::admits`
/// reads them, for the lifecycle tick's pair when no report of that session
/// was admitted and for its activation. The token's logon is classified as a
/// request's caller is (`windows::token_context`), and only an interactive
/// one is named: a token carrying the remote-logon group is refused, as a
/// remote-interactive request is. It names a candidate only: the capture
/// that follows proves the same console session unlocked, the same primary
/// SID and logon LUID, and every other admission, before any read or write.
#[cfg(any(
    feature = "service-profile-triggers-v1",
    feature = "service-keyboard-driver-mode-v1"
))]
#[cfg_attr(test, allow(dead_code))] // Only the native composition reads the console; tests inject it.
pub(super) fn console_user() -> Option<(String, u32)> {
    let session = crate::console_session::observe()?;
    let token = session_token(session).ok()?;
    let identity = crate::windows_token::Identity::read(&token).ok()?;
    let (logon, logon_session) = super::windows::token_context(&token).ok()?;
    (identity.primary
        && identity.session == session
        && logon_session == session
        && logon == super::Logon::Interactive)
        .then_some((identity.sid, session))
}
#[cfg_attr(test, allow(dead_code))] // Only the native session and path checks call this; tests inject them.
fn session_identity(session: u32) -> io::Result<crate::windows_token::Identity> {
    crate::windows_token::Identity::read(&session_token(session)?)
}
/// The primary token of `session`'s user.
#[cfg_attr(test, allow(dead_code))] // Only the native session and path checks call this; tests inject them.
fn session_token(session: u32) -> io::Result<OwnedHandle> {
    if session == 0 {
        return Err(deny());
    }
    let mut token = std::ptr::null_mut();
    // SAFETY: LocalSystem query of the independently authenticated session;
    // successful output is owned until all identity fields have been copied.
    if unsafe { WTSQueryUserToken(session, &mut token) } == 0 {
        return Err(io::Error::last_os_error());
    }
    Ok(unsafe { OwnedHandle::from_raw_handle(token) })
}

// Existing per-user directories can be SYSTEM-owned and inherit the protected
// machine ACL. They must not grant untrusted mutation or child replacement.
const USER_ROOT: Policy = Policy::Directory { extra_rights: 0 };

pub(super) struct ProtectedFiles {
    root: File,
    _ancestors: Vec<File>,
    _writer: File,
    #[cfg(test)]
    verifier: Option<crate::installed::FileVerifier>,
}
impl ProtectedFiles {
    /// No default root, environment resolution or token query occurs here.
    #[cfg_attr(test, allow(dead_code))] // Only the native factory acquires the protected root, which tests replace.
    pub(super) fn open_at(path: &Path) -> io::Result<Self> {
        let (root, ancestors, _) = paths::pin_root(path)?;
        paths::require_ntfs(&root)?;
        paths::verify_security(&root, USER_ROOT)?;
        for (i, parent) in ancestors.iter().enumerate() {
            paths::verify_security(
                parent,
                if i + 3 == ancestors.len() {
                    Policy::PROGRAM_DATA
                } else {
                    Policy::ANCESTOR
                },
            )?;
        }
        let writer = paths::open_relative(
            &root,
            OsStr::new("configuration.writer-lock"),
            FILE_GENERIC_READ | FILE_GENERIC_WRITE,
            0,
            FILE_OPEN_IF,
            FILE_NON_DIRECTORY_FILE | FILE_OPEN_REPARSE_POINT,
        )?;
        paths::check_regular_file(&writer)?;
        paths::verify_security(&writer, Policy::Record)?;
        if writer.metadata()?.len() != 0 {
            return Err(deny());
        }
        Ok(Self {
            root,
            _ancestors: ancestors,
            _writer: writer,
            #[cfg(test)]
            verifier: None,
        })
    }
    fn check(&self, file: &File, policy: Policy) -> io::Result<()> {
        #[cfg(test)]
        if let Some(verify) = &self.verifier {
            return verify(file);
        }
        paths::verify_security(file, policy)
    }
    #[cfg(test)]
    pub(super) fn injected(root: File, verify: crate::installed::FileVerifier) -> io::Result<Self> {
        verify(&root)?;
        let writer = paths::open_relative(
            &root,
            OsStr::new("configuration.writer-lock"),
            FILE_GENERIC_READ | FILE_GENERIC_WRITE,
            0,
            FILE_OPEN_IF,
            FILE_NON_DIRECTORY_FILE | FILE_OPEN_REPARSE_POINT,
        )?;
        paths::check_regular_file(&writer)?;
        verify(&writer)?;
        if writer.metadata()?.len() != 0 {
            return Err(deny());
        }
        Ok(Self {
            root,
            _ancestors: vec![],
            _writer: writer,
            verifier: Some(verify),
        })
    }
    fn file(&self, name: &str, create: bool) -> io::Result<File> {
        if name.is_empty()
            || !name
                .bytes()
                .all(|b| b.is_ascii_alphanumeric() || b".-".contains(&b))
        {
            return Err(deny());
        }
        self.check(&self.root, USER_ROOT)?;
        let file = paths::open_relative(
            &self.root,
            OsStr::new(name),
            FILE_GENERIC_READ
                | if create {
                    FILE_GENERIC_WRITE | DELETE
                } else {
                    0
                },
            if create { 0 } else { FILE_SHARE_READ },
            if create { FILE_CREATE } else { FILE_OPEN },
            FILE_NON_DIRECTORY_FILE | FILE_OPEN_REPARSE_POINT,
        )?;
        paths::check_regular_file(&file)?;
        self.check(&file, Policy::Record)?;
        Ok(file)
    }
}
impl Files for ProtectedFiles {
    fn read(&self, name: &str) -> io::Result<Option<Vec<u8>>> {
        let mut f = match self.file(name, false) {
            Ok(f) => f,
            Err(e) if paths::missing_leaf(&e) => return Ok(None),
            Err(e) => return Err(e),
        };
        if f.metadata()?.len() > 8 * 1024 * 1024 {
            return Err(deny());
        }
        let mut bytes = Vec::new();
        f.read_to_end(&mut bytes)?;
        Ok(Some(bytes))
    }
    fn replace(&self, name: &str, expected: Option<&[u8]>, bytes: &[u8]) -> io::Result<()> {
        if self.read(name)?.as_deref() != expected {
            return Err(deny());
        }
        let mut staged = self.file(&format!("{name}.pending"), true)?;
        staged.write_all(bytes)?;
        staged.sync_all()?;
        staged.rewind()?;
        let mut check = Vec::new();
        staged.read_to_end(&mut check)?;
        if check != bytes {
            return Err(deny());
        }
        if self.read(name)?.as_deref() != expected {
            return Err(deny());
        }
        paths::rename_same_directory(&staged, name, true)?;
        staged.sync_all()?;
        drop(staged);
        if self.read(name)?.as_deref() != Some(bytes) {
            return Err(deny());
        }
        Ok(())
    }
    #[cfg(feature = "service-profiles-v1")]
    fn remove(&self, name: &str, expected: &[u8]) -> io::Result<()> {
        self.delete(name, Some(expected))
    }
    #[cfg(feature = "service-profiles-v1")]
    fn discard_stage(&self, name: &str) -> io::Result<()> {
        match self.delete(&format!("{name}.pending"), None) {
            Err(error) if paths::missing_leaf(&error) => Ok(()),
            result => result,
        }
    }
}
#[cfg(feature = "service-profiles-v1")]
impl ProtectedFiles {
    /// Deletes `name`, which must exist, through one exclusive handle: only
    /// while it holds exactly `expected`, or whatever it holds when there is
    /// nothing to match.
    fn delete(&self, name: &str, expected: Option<&[u8]>) -> io::Result<()> {
        if name.is_empty()
            || !name
                .bytes()
                .all(|b| b.is_ascii_alphanumeric() || b".-".contains(&b))
        {
            return Err(deny());
        }
        self.check(&self.root, USER_ROOT)?;
        // Exclusive, so the bytes matched are the bytes deleted.
        let file = paths::open_relative(
            &self.root,
            OsStr::new(name),
            FILE_GENERIC_READ | DELETE,
            0,
            FILE_OPEN,
            FILE_NON_DIRECTORY_FILE | FILE_OPEN_REPARSE_POINT,
        )?;
        paths::check_regular_file(&file)?;
        self.check(&file, Policy::Record)?;
        if let Some(expected) = expected {
            // A file of another length is not these bytes, so no more than
            // their length is ever read. A stage is deleted unread.
            if file.metadata()?.len() != expected.len() as u64 {
                return Err(deny());
            }
            let mut bytes = Vec::with_capacity(expected.len());
            (&file)
                .take(expected.len() as u64)
                .read_to_end(&mut bytes)?;
            if bytes != expected {
                return Err(deny());
            }
        }
        paths::mark_for_deletion(&file)?;
        drop(file);
        if self.read(name)?.is_some() {
            return Err(deny());
        }
        Ok(())
    }
}

// Configuration remains per-user; submission ambiguity is machine-global under
// the SAME durable owner Store. Switching SID cannot bypass another user's fence.
pub(super) struct CustodialFiles {
    pub(super) user: ProtectedFiles,
    pub(super) custody: Arc<dyn ReadCustody>,
}
impl Files for CustodialFiles {
    fn read_release(&self, name: &str) -> io::Result<Option<Vec<u8>>> {
        match name {
            "thermal-mutation.journal" => self.custody.journal_read_release(false),
            "thermal-mutation.journal.pending" => self.custody.journal_read_release(true),
            _ => Err(deny()),
        }
    }
    fn replace_retired(&self, name: &str, expected: Option<&[u8]>, bytes: &[u8]) -> io::Result<()> {
        if name != "thermal-mutation.journal" {
            return Err(deny());
        }
        self.custody.journal_retire_prepared(expected, bytes)
    }
    fn replace_cleanup(
        &self,
        name: &str,
        scope: super::mailbox_read::CleanupScope,
        expected: Option<&[u8]>,
        bytes: &[u8],
    ) -> io::Result<()> {
        if name != "thermal-mutation.journal" {
            return Err(deny());
        }
        self.custody.journal_replace_cleanup(scope, expected, bytes)
    }
    fn reserve(&self, expected: Option<&[u8]>, needed: usize) -> io::Result<Option<Vec<u8>>> {
        self.custody.journal_reserve(expected, needed)
    }
    fn read(&self, name: &str) -> io::Result<Option<Vec<u8>>> {
        match name {
            "thermal-mutation.journal" => self.custody.journal_read(false),
            "thermal-mutation.journal.pending" => self.custody.journal_read(true),
            _ => self.user.read(name),
        }
    }
    fn replace(&self, name: &str, expected: Option<&[u8]>, bytes: &[u8]) -> io::Result<()> {
        if name == "thermal-mutation.journal" {
            self.custody.journal_replace(expected, bytes)
        } else {
            self.user.replace(name, expected, bytes)
        }
    }
    #[cfg(feature = "service-profiles-v1")]
    fn remove(&self, name: &str, expected: &[u8]) -> io::Result<()> {
        // The journal is custody's; it is never removed through this store.
        if name.starts_with("thermal-mutation.journal") {
            return Err(deny());
        }
        self.user.remove(name, expected)
    }
    #[cfg(feature = "service-profiles-v1")]
    fn discard_stage(&self, name: &str) -> io::Result<()> {
        // A pending journal or override document is evidence the handback to
        // managed preserves, never a leftover to clean up.
        if name.starts_with("thermal-mutation.journal") || name == "manual-overrides.json" {
            return Err(deny());
        }
        self.user.discard_stage(name)
    }
}

#[cfg_attr(test, allow(dead_code))] // Only the native session and path checks call this; tests inject them.
pub(super) fn same_path(left: &str, right: &str) -> bool {
    let left: Vec<u16> = left.encode_utf16().collect();
    let right: Vec<u16> = right.encode_utf16().collect();
    // SAFETY: bounded immutable Unicode buffers. This is the managed ordinal
    // case-insensitive contract, not path normalization or a filesystem lookup.
    unsafe {
        windows_sys::Win32::Globalization::CompareStringOrdinal(
            left.as_ptr(),
            left.len() as i32,
            right.as_ptr(),
            right.len() as i32,
            1,
        ) == 2
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    fn descriptor(sddl: &str) -> Vec<u8> {
        let input: Vec<u16> = sddl.encode_utf16().chain([0]).collect();
        let mut output = std::ptr::null_mut();
        let mut length = 0;
        // SAFETY: Pure conversion of the literal test descriptor into owned memory.
        assert_ne!(
            unsafe {
                windows_sys::Win32::Security::Authorization::ConvertStringSecurityDescriptorToSecurityDescriptorW(input.as_ptr(),1,&mut output,&mut length)
            },
            0
        );
        let descriptor = paths::LocalDescriptor(output);
        unsafe { std::slice::from_raw_parts(descriptor.0.cast::<u8>(), length as usize) }.to_vec()
    }
    #[test]
    fn existing_managed_user_root_allows_system_inheritance_without_untrusted_mutation() {
        for sddl in [
            "O:SYD:AI(A;OICIID;FA;;;SY)(A;OICIID;FA;;;BA)(A;OICIID;FR;;;BU)",
            "O:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)",
        ] {
            assert_eq!(
                paths::accepts_descriptor(&descriptor(sddl), USER_ROOT),
                Some(true)
            );
        }
        for sddl in [
            "O:BUD:P(A;OICI;FA;;;BU)",
            "O:SYD:AI(A;OICIID;FA;;;SY)(A;OICIID;FW;;;BU)",
            "O:SYD:P(A;OICI;FA;;;SY)(A;;0x4;;;BU)",
        ] {
            assert_ne!(
                paths::accepts_descriptor(&descriptor(sddl), USER_ROOT),
                Some(true)
            );
        }
    }
}

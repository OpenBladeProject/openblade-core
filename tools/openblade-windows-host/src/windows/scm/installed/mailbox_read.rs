//! Concrete read-only transfer. No firmware owner history or mutation authority.
use super::{Observation, Snapshot, subscription::Publication};
use crate::{
    discovery::performance::{ReadAdmission, Worker, native_read_worker},
    loaded_origin::Origin,
    native_package::{create_attempt_record, digest, encode, encode64, lines},
    windows_protected_path::{self as paths, Policy},
};
#[cfg(feature = "service-keyboard-driver-mode-v1")]
use openblade_domain::driver_mode_history::Record as DriverModeRecord;
use sha2::{Digest, Sha256};
use std::{cell::RefCell, rc::Rc};
use std::{
    ffi::OsStr,
    fs::File,
    io::{self, Read},
    sync::{
        Arc, Mutex,
        atomic::{AtomicU8, Ordering},
    },
};
use tokio::sync::watch;
use tokio::time::Instant;
use windows_sys::{
    Wdk::Storage::FileSystem::{FILE_NON_DIRECTORY_FILE, FILE_OPEN, FILE_OPEN_REPARSE_POINT},
    Win32::Storage::FileSystem::{FILE_GENERIC_READ, FILE_SHARE_READ},
};

#[cfg(feature = "service-keyboard-driver-mode-read-v1")]
#[path = "driver_mode.rs"]
mod driver_mode;
#[cfg(feature = "service-keyboard-driver-mode-v1")]
pub(super) use driver_mode::INTERVAL as DRIVER_MODE_INTERVAL;
#[cfg(all(test, not(feature = "service-keyboard-driver-mode-read-v1")))]
#[path = "driver_mode_absent_tests.rs"]
mod driver_mode_absent_tests;
#[cfg(test)]
#[path = "driver_mode_rig.rs"]
mod driver_mode_rig;
#[cfg(feature = "service-live-readings-v1")]
#[path = "firmware_versions.rs"]
mod firmware_versions;
#[cfg(feature = "service-live-readings-v1")]
#[path = "gpu_temperature.rs"]
mod gpu_temperature;
#[cfg(feature = "service-live-readings-v1")]
#[path = "live_readings.rs"]
mod live_readings;

struct Resources {
    root: File,
    _ancestors: Vec<File>,
    _exclusion: File,
    _owner: super::ownership::durable::Store,
    _records: Vec<File>,
    _build: File,
}

struct ReadLease {
    origin: Arc<Origin>,
    resources: Mutex<Option<Resources>>,
    proof: ReadAdmission,
    retirement_name: String,
    recovery_only: bool,
    // 0 acquired, 1 revoked/quarantined, 2 successfully retired.
    state: AtomicU8,
}

#[derive(Debug)]
pub(super) enum ActivationFailure {
    KnownUnpublished(io::Error),
    Uncertain(io::Error),
}
impl ActivationFailure {
    fn error(self) -> io::Error {
        match self {
            Self::KnownUnpublished(e) | Self::Uncertain(e) => e,
        }
    }
}

/// Which owned cleanup an admission serves. Each admits only the cleanup
/// that holds it (`thermal_mutation::Cleanup`), never a request.
#[cfg(feature = "service-thermal-mutation-v1")]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(super) enum CleanupScope {
    /// The coordinated Stop: owned-Stop origin trust at the ordinary
    /// lifecycle epoch (`ReadCustody::authorize_owned_stop`).
    OwnedStop,
    /// The low-power preparation for one suspend: live origin trust at the
    /// epoch the gate admits for that generation
    /// (`WriteScope::LowPowerCleanup`), which ends at the next resume,
    /// DisplayOn or suspend.
    #[cfg(feature = "service-keyboard-driver-mode-v1")]
    LowPower { generation: u64 },
}

#[cfg(feature = "service-fan-mutation-v2")]
impl CleanupScope {
    /// The lifecycle write scope this cleanup pins its epoch under.
    pub(super) fn write_scope(self) -> openblade_domain::low_power_gate::WriteScope {
        use openblade_domain::low_power_gate::WriteScope;
        match self {
            Self::OwnedStop => WriteScope::Ordinary,
            #[cfg(feature = "service-keyboard-driver-mode-v1")]
            Self::LowPower { generation } => WriteScope::LowPowerCleanup { generation },
        }
    }
}

pub(super) trait ReadCustody: Send + Sync {
    fn authorize(&self) -> Option<ReadAdmission>;
    fn authorize_owned_stop(&self) -> Option<ReadAdmission> {
        None
    }
    /// The owned cleanup admission of `scope`: the owned-Stop one, and the
    /// low-power one only where a custody admits it itself.
    #[cfg(feature = "service-thermal-mutation-v1")]
    fn authorize_cleanup(&self, scope: CleanupScope) -> Option<ReadAdmission> {
        match scope {
            CleanupScope::OwnedStop => self.authorize_owned_stop(),
            #[cfg(feature = "service-keyboard-driver-mode-v1")]
            CleanupScope::LowPower { .. } => None,
        }
    }
    /// The low-power gate's generation (`Lifecycle::resume_generation`). It
    /// advances only at a suspend and a resume, whereas the lifecycle epoch
    /// in `ReadAdmission` advances at every session or power notice, so two
    /// admitted reads see different values only across a resume. None when
    /// the gate is unreadable, or for a custody without one.
    #[cfg(feature = "service-live-readings-v1")]
    fn resume_generation(&self) -> Option<u64> {
        None
    }
    /// Whether this custody admits everything `authorize` checks but the
    /// lifecycle epoch. It admits storing request intent only
    /// (`WindowsGuard::capture_storage`), never a read or a write, so the
    /// low-power gate does not refuse it.
    #[cfg(feature = "service-profile-triggers-v1")]
    fn authorize_storage(&self) -> bool {
        false
    }
    #[cfg(feature = "service-thermal-mutation-v1")]
    fn journal_replace_cleanup(
        &self,
        _scope: CleanupScope,
        _expected: Option<&[u8]>,
        _bytes: &[u8],
    ) -> io::Result<()> {
        Err(deny())
    }
    fn journal_retire_prepared(&self, _expected: Option<&[u8]>, _bytes: &[u8]) -> io::Result<()> {
        Err(deny())
    }
    fn revoke(&self);
    fn retire(&self) -> io::Result<()>;
    fn confirm_drained(&self) -> io::Result<()>;
    #[cfg(feature = "service-thermal-mutation-v1")]
    fn verify_integrity(&self) -> io::Result<()> {
        Ok(())
    }
    #[cfg(feature = "service-thermal-mutation-v1")]
    fn activate_mutation(&self) -> Result<(), ActivationFailure> {
        Err(ActivationFailure::KnownUnpublished(deny()))
    }
    /// Whether this custody only recovers an earlier owner's record: it
    /// refuses mutation activation for its whole life (`ReadLease`).
    #[cfg(feature = "service-keyboard-driver-mode-v1")]
    fn recovery_only(&self) -> bool {
        false
    }
    #[cfg(feature = "service-thermal-mutation-v1")]
    fn journal_read(&self, _: bool) -> io::Result<Option<Vec<u8>>> {
        Err(deny())
    }
    #[cfg(feature = "service-thermal-mutation-v1")]
    fn journal_read_release(&self, _: bool) -> io::Result<Option<Vec<u8>>> {
        Err(deny())
    }
    #[cfg(feature = "service-thermal-mutation-v1")]
    fn journal_reserve(&self, _: Option<&[u8]>, _: usize) -> io::Result<Option<Vec<u8>>> {
        Err(deny())
    }
    #[cfg(feature = "service-thermal-mutation-v1")]
    fn journal_replace(&self, _: Option<&[u8]>, _: &[u8]) -> io::Result<()> {
        Err(deny())
    }
    /// The keyboard Driver-mode ownership record in the original owner
    /// custody, `None` while there is none (`Store::driver_mode_record_read`).
    /// Reading it writes nothing, so a recovery-only custody reads it too.
    #[cfg(feature = "service-keyboard-driver-mode-v1")]
    fn driver_mode_record_read(&self) -> io::Result<Option<DriverModeRecord>> {
        Err(deny())
    }
    /// The Active owner nonce a replace of that record names
    /// (`Store::driver_mode_record_owner`): only a custody this process
    /// activated has one, so a recovery-only custody refuses.
    #[cfg(feature = "service-keyboard-driver-mode-v1")]
    fn driver_mode_record_owner(&self) -> io::Result<String> {
        Err(deny())
    }
    /// Replaces that record with `next` while it holds `expected`, under the
    /// ordinary admission (`authorize`): the acquire and its read-only
    /// resolution. A recovery-only custody refuses.
    #[cfg(feature = "service-keyboard-driver-mode-v1")]
    fn driver_mode_record_replace(
        &self,
        _expected: Option<&DriverModeRecord>,
        _next: &DriverModeRecord,
    ) -> io::Result<()> {
        Err(deny())
    }
    /// `driver_mode_record_replace` under the owned cleanup admission of
    /// `scope` (`authorize_cleanup`): the releases at Stop and at a suspend.
    #[cfg(feature = "service-keyboard-driver-mode-v1")]
    fn driver_mode_record_replace_cleanup(
        &self,
        _scope: CleanupScope,
        _expected: Option<&DriverModeRecord>,
        _next: &DriverModeRecord,
    ) -> io::Result<()> {
        Err(deny())
    }
}

// Every reader factory and repeated native checkpoint receives this same
// original custody. Integrity failures survive the Option-valued read API;
// configuration policy is deliberately not part of this admission.
#[cfg(feature = "service-thermal-mutation-v1")]
struct GuardedReadCustody {
    original: Arc<dyn ReadCustody>,
    fatal: Arc<Mutex<Option<&'static str>>>,
}
#[cfg(feature = "service-thermal-mutation-v1")]
impl ReadCustody for GuardedReadCustody {
    fn verify_integrity(&self) -> io::Result<()> {
        super::thermal_mutation::verify_retained_failure(&self.fatal)?;
        let result = self.original.verify_integrity();
        if let Err(error) = &result {
            super::thermal_mutation::remember_failure(&self.fatal, error);
        }
        super::thermal_mutation::verify_retained_failure(&self.fatal)?;
        result
    }
    fn authorize(&self) -> Option<ReadAdmission> {
        self.verify_integrity().ok()?;
        self.original.authorize()
    }
    // A gate read admits nothing; the job it sizes authorizes itself.
    #[cfg(feature = "service-live-readings-v1")]
    fn resume_generation(&self) -> Option<u64> {
        self.original.resume_generation()
    }
    #[cfg(feature = "service-profile-triggers-v1")]
    fn authorize_storage(&self) -> bool {
        self.verify_integrity().is_ok() && self.original.authorize_storage()
    }
    fn authorize_owned_stop(&self) -> Option<ReadAdmission> {
        self.verify_integrity().ok()?;
        self.original.authorize_owned_stop()
    }
    fn authorize_cleanup(&self, scope: CleanupScope) -> Option<ReadAdmission> {
        self.verify_integrity().ok()?;
        self.original.authorize_cleanup(scope)
    }
    fn journal_replace_cleanup(
        &self,
        scope: CleanupScope,
        expected: Option<&[u8]>,
        bytes: &[u8],
    ) -> io::Result<()> {
        self.verify_integrity()?;
        self.original
            .journal_replace_cleanup(scope, expected, bytes)
    }
    fn journal_retire_prepared(&self, expected: Option<&[u8]>, bytes: &[u8]) -> io::Result<()> {
        self.verify_integrity()?;
        self.original.journal_retire_prepared(expected, bytes)
    }
    fn revoke(&self) {
        self.original.revoke();
    }
    fn retire(&self) -> io::Result<()> {
        self.original.retire()
    }
    fn confirm_drained(&self) -> io::Result<()> {
        self.original.confirm_drained()
    }
    fn activate_mutation(&self) -> Result<(), ActivationFailure> {
        self.verify_integrity()
            .map_err(ActivationFailure::KnownUnpublished)?;
        self.original.activate_mutation()
    }
    #[cfg(feature = "service-keyboard-driver-mode-v1")]
    fn recovery_only(&self) -> bool {
        self.original.recovery_only()
    }
    fn journal_read(&self, pending: bool) -> io::Result<Option<Vec<u8>>> {
        self.verify_integrity()?;
        self.original.journal_read(pending)
    }
    fn journal_read_release(&self, pending: bool) -> io::Result<Option<Vec<u8>>> {
        self.verify_integrity()?;
        self.original.journal_read_release(pending)
    }
    fn journal_reserve(
        &self,
        expected: Option<&[u8]>,
        needed: usize,
    ) -> io::Result<Option<Vec<u8>>> {
        self.verify_integrity()?;
        self.original.journal_reserve(expected, needed)
    }
    fn journal_replace(&self, expected: Option<&[u8]>, bytes: &[u8]) -> io::Result<()> {
        self.verify_integrity()?;
        self.original.journal_replace(expected, bytes)
    }
    #[cfg(feature = "service-keyboard-driver-mode-v1")]
    fn driver_mode_record_read(&self) -> io::Result<Option<DriverModeRecord>> {
        self.verify_integrity()?;
        self.original.driver_mode_record_read()
    }
    #[cfg(feature = "service-keyboard-driver-mode-v1")]
    fn driver_mode_record_owner(&self) -> io::Result<String> {
        self.verify_integrity()?;
        self.original.driver_mode_record_owner()
    }
    #[cfg(feature = "service-keyboard-driver-mode-v1")]
    fn driver_mode_record_replace(
        &self,
        expected: Option<&DriverModeRecord>,
        next: &DriverModeRecord,
    ) -> io::Result<()> {
        self.verify_integrity()?;
        self.original.driver_mode_record_replace(expected, next)
    }
    #[cfg(feature = "service-keyboard-driver-mode-v1")]
    fn driver_mode_record_replace_cleanup(
        &self,
        scope: CleanupScope,
        expected: Option<&DriverModeRecord>,
        next: &DriverModeRecord,
    ) -> io::Result<()> {
        self.verify_integrity()?;
        self.original
            .driver_mode_record_replace_cleanup(scope, expected, next)
    }
}

// Same canonical cross-language contract used before concrete read acquisition.
fn validate_transfer(
    mapping: &[u8],
    actual_mapping: &[u8],
    request: &[u8],
    receipt: &[u8],
    run: &[u8],
) -> io::Result<()> {
    let version_two = mapping.starts_with(b"OpenBlade.ManagedMailboxEpoch.v2\n");
    let f = lines(
        mapping,
        if version_two {
            "OpenBlade.ManagedMailboxEpoch.v2"
        } else {
            "OpenBlade.ManagedMailboxEpoch.v1"
        },
        if version_two { 8 } else { 6 },
    )?;
    let r = lines(run, "OpenBlade.ManagedMailboxRun.v1", 5)?;
    let hex = |s: &str, n| {
        s.len() == n
            && s.bytes()
                .all(|b| b.is_ascii_digit() || (b'a'..=b'f').contains(&b))
    };
    let process: Vec<_> = r[1].split(':').collect();
    let numeric = process.len() == 2
        && process[0]
            .parse::<u32>()
            .ok()
            .is_some_and(|p| p != 0 && p.to_string() == process[0])
        && process[1]
            .parse::<u64>()
            .ok()
            .is_some_and(|t| t != 0 && t <= i64::MAX as u64 && t.to_string() == process[1]);
    if !hex(r[0], 32)
        || r[0].bytes().all(|b| b == b'0')
        || !hex(r[2], 64)
        || !hex(r[3], 64)
        || !hex(r[4], 32)
        || !numeric
        || version_two && (!hex(f[6], 32) || f[6] != r[0] || !hex(f[7], 64))
        || !hex(f[0], 64)
        || !hex(f[1], 64)
        || !hex(f[2], 32)
        || !hex(f[3], 32)
        || !hex(f[4], 32)
        || !hex(f[5], 64)
    {
        return Err(deny());
    }
    if actual_mapping != mapping
        || digest(receipt) != f[0]
        || digest(request) != f[1]
        || request
            != encode(
                "OpenBlade.ManagedMailboxRequest.v1",
                &[&digest(run), f[3], f[4], f[5]],
            )
        || receipt
            != encode(
                "OpenBlade.ManagedMailboxQuiescent.v1",
                &[
                    &digest(request),
                    &encode64(run),
                    "transport-quiescent-no-mutation",
                ],
            )
    {
        return Err(deny());
    }
    Ok(())
}
fn validate_selected_transfer(
    mapping: &[u8],
    actual: &[u8],
    request: &[u8],
    receipt: &[u8],
    run: &[u8],
    selection: &[u8],
    exited: &[u8],
) -> io::Result<()> {
    validate_transfer(mapping, actual, request, receipt, run)?;
    let m = lines(mapping, "OpenBlade.ManagedMailboxEpoch.v2", 8)?;
    let s = lines(selection, "OpenBlade.ManagedMailboxTransfer.v2", 4)?;
    let r = lines(run, "OpenBlade.ManagedMailboxRun.v1", 5)?;
    if s[0]
        .parse::<u64>()
        .ok()
        .is_none_or(|n| n == 0 || n > i64::MAX as u64 || n.to_string() != s[0])
        || s[1].len() != 64
        || !s[1]
            .bytes()
            .all(|b| b.is_ascii_digit() || (b'a'..=b'f').contains(&b))
        || digest(selection) != m[7]
        || s[2] != encode64(run)
        || s[3] != encode64(request)
        || exited
            != encode(
                "OpenBlade.ManagedMailboxOriginalExited.v2",
                &[&digest(selection), r[1], &digest(receipt)],
            )
    {
        return Err(deny());
    }
    Ok(())
}
fn deny() -> io::Error {
    io::Error::new(
        io::ErrorKind::PermissionDenied,
        "original managed mailbox handoff/read custody is unavailable",
    )
}
pub(super) fn authorize_original(
    state: &AtomicU8,
    committed: io::Result<bool>,
    mapping: impl FnOnce() -> io::Result<()>,
    proof: ReadAdmission,
) -> Option<ReadAdmission> {
    if state.load(Ordering::Acquire) != 0 {
        return None;
    }
    match committed {
        Ok(false) => None,
        Ok(true) => match mapping() {
            Ok(()) => Some(proof),
            Err(e) if crate::origin_lifetime::expected_stop(&e) => None,
            Err(_) => {
                state.store(1, Ordering::Release);
                None
            }
        },
        Err(e) if crate::origin_lifetime::expected_stop(&e) => None,
        _ => {
            state.store(1, Ordering::Release);
            None
        }
    }
}
/// `ReadLease`'s low-power cleanup admission: `authorize_original` at the
/// suspend generation's lifecycle fence (`Lifecycle::fence_for`) instead of
/// the ordinary epoch, with the same live origin trust, original custody and
/// generation. No fence refuses before origin trust is read, so a resumed
/// generation never latches the custody unavailable.
#[cfg(feature = "service-keyboard-driver-mode-v1")]
fn low_power_admission(
    state: &AtomicU8,
    committed: io::Result<bool>,
    mapping: impl FnOnce() -> io::Result<()>,
    fence: Option<u64>,
    proof: ReadAdmission,
) -> Option<ReadAdmission> {
    if state.load(Ordering::Acquire) != 0 {
        return None;
    }
    let lifecycle = fence?;
    authorize_original(
        state,
        committed,
        mapping,
        ReadAdmission { lifecycle, ..proof },
    )
}
fn record(root: &File, name: &str, held: &mut Vec<File>) -> io::Result<Vec<u8>> {
    let mut file = paths::open_relative(
        root,
        OsStr::new(name),
        FILE_GENERIC_READ,
        FILE_SHARE_READ,
        FILE_OPEN,
        FILE_NON_DIRECTORY_FILE | FILE_OPEN_REPARSE_POINT,
    )?;
    paths::check_regular_file(&file)?;
    paths::verify_security(&file, Policy::Record)?;
    if file.metadata()?.len() > 16384 {
        return Err(deny());
    }
    let mut bytes = Vec::new();
    file.read_to_end(&mut bytes)?;
    held.push(file);
    Ok(bytes)
}
impl ReadLease {
    fn acquire(origin: Arc<Origin>) -> io::Result<Arc<Self>> {
        origin.verify()?;
        let mapping = origin.mailbox_mapping()?;
        let path = crate::service_install_identity::known_folder(
            &windows_sys::Win32::UI::Shell::FOLDERID_ProgramData,
        )?
        .join("OpenBlade")
        .join("ManagedHandoff");
        let (root, ancestors, _) = paths::pin_root(&path)?;
        paths::require_ntfs(&root)?;
        paths::verify_security(&root, Policy::ROOT)?;
        for (i, parent) in ancestors.iter().enumerate() {
            paths::verify_security(
                parent,
                if i + 1 == ancestors.len() {
                    Policy::ROOT
                } else if i + 2 == ancestors.len() {
                    Policy::PROGRAM_DATA
                } else {
                    Policy::ANCESTOR
                },
            )?;
        }
        let exclusion = paths::open_relative(
            &root,
            OsStr::new("exclusion.lock"),
            FILE_GENERIC_READ,
            0,
            FILE_OPEN,
            FILE_NON_DIRECTORY_FILE | FILE_OPEN_REPARSE_POINT,
        )?;
        paths::check_regular_file(&exclusion)?;
        paths::verify_security(&exclusion, Policy::PrivateCustody)?;
        let owner = super::ownership::durable::acquire_empty_read_history()?;
        let mut held = Vec::new();
        let creation = encode(
            "OpenBlade.ManagedMailboxBootstrap.v2",
            &["protected-installer-anchor", "ManagedHandoff"],
        );
        let anchor = ancestors.last().ok_or_else(deny)?;
        if record(anchor, "managed-handoff-bootstrap-v2.bin", &mut held)? != creation
            || record(anchor, "managed-handoff-complete-v2.bin", &mut held)?
                != encode(
                    "OpenBlade.ManagedMailboxBootstrapComplete.v2",
                    &[&digest(&creation)],
                )
            || record(&root, "handoff-root.bin", &mut held)?
                != encode("OpenBlade.ManagedMailboxRoot.v2", &[&digest(&creation)])
        {
            return Err(deny());
        }
        let m = lines(&mapping, "OpenBlade.ManagedMailboxEpoch.v2", 8)?;
        let name = |part: &str| format!("handoff-{}-{}.bin", m[4], part);
        let actual_mapping = record(&root, &name("mapping"), &mut held)?;
        let request = record(&root, &name("request"), &mut held)?;
        let receipt = record(&root, &name("receipt"), &mut held)?;
        let run = record(&root, &name("run"), &mut held)?;
        let selection = record(&root, &name("selection"), &mut held)?;
        let exited = record(&root, &name("original-exit"), &mut held)?;
        validate_selected_transfer(
            &mapping,
            &actual_mapping,
            &request,
            &receipt,
            &run,
            &selection,
            &exited,
        )?;
        // A terminal disposition permanently closes this original transfer.
        let terminal = paths::open_relative(
            &root,
            OsStr::new(&name("terminal")),
            FILE_GENERIC_READ,
            FILE_SHARE_READ,
            FILE_OPEN,
            FILE_NON_DIRECTORY_FILE | FILE_OPEN_REPARSE_POINT,
        );
        match terminal {
            Err(error) if matches!(error.raw_os_error(), Some(2 | 3)) => (),
            _ => return Err(deny()),
        }
        let r = lines(&run, "OpenBlade.ManagedMailboxRun.v1", 5)?;
        if record(&root, &format!("run-{}.bin", r[0]), &mut held)? != run {
            return Err(deny());
        }
        validate_transfer(&mapping, &actual_mapping, &request, &receipt, &run)?;
        let binding = record(&root, &format!("run-binding-{}.bin", r[0]), &mut held)?;
        let b = lines(&binding, "OpenBlade.ManagedMailboxBuildBinding.v1", 4)?;
        if b[0] != digest(&run)
            || b[1] != origin.installation
            || b[2].len() != 64
            || b[3].len() != 64
            || !b[2..].iter().all(|v| {
                v.bytes()
                    .all(|c| c.is_ascii_digit() || (b'a'..=b'f').contains(&c))
            })
        {
            return Err(deny());
        }
        let build = paths::open_directory(&root, OsStr::new(&format!("build-{}", b[2])))?;
        paths::verify_security(&build, Policy::Directory { extra_rights: 0 })?;
        let manifest = record(&build, "release.json", &mut held)?;
        let signature = record(&build, "release.sig", &mut held)?;
        let marker = record(&build, "verified.bin", &mut held)?;
        if marker
            != encode(
                "OpenBlade.ManagedMailboxPackage.v1",
                &[b[1], b[2], &digest(&manifest), &digest(&signature), b[3]],
            )
        {
            return Err(deny());
        }
        // Setup independently authenticated this original signed installer before
        // minting the one-use mapping. Retain its exact bytes through native drain.
        let mut original = paths::open_relative(
            &build,
            OsStr::new("package.exe"),
            FILE_GENERIC_READ,
            FILE_SHARE_READ,
            FILE_OPEN,
            FILE_NON_DIRECTORY_FILE | FILE_OPEN_REPARSE_POINT,
        )?;
        paths::check_regular_file(&original)?;
        paths::verify_security(&original, Policy::Record)?;
        if original.metadata()?.len() > 1024 * 1024 * 1024 {
            return Err(deny());
        }
        let mut hash = Sha256::new();
        let mut buffer = [0; 65536];
        loop {
            let n = original.read(&mut buffer)?;
            if n == 0 {
                break;
            }
            hash.update(&buffer[..n]);
        }
        if format!("{:x}", hash.finalize()) != b[2] {
            return Err(deny());
        }
        held.push(original);
        // Create-once per original incoming epoch. An exited/crashed native
        // process cannot adopt the same transfer or manufacture a new read run.
        let acquisition = encode(
            "OpenBlade.NativeMailboxRead.v1",
            &[
                &digest(&mapping),
                &origin.epoch,
                &origin.pid.to_string(),
                &origin.created.to_string(),
            ],
        );
        let acquisition_name = format!("read-acquired-{}.bin", origin.epoch);
        let mut recovery_only = false;
        let mut retirement_name = format!("read-retired-{}.bin", origin.epoch);
        #[cfg(feature = "service-thermal-mutation-v1")]
        match record(&root, &acquisition_name, &mut held) {
            Ok(prior) => {
                let fields = lines(&prior, "OpenBlade.NativeMailboxRead.v1", 4)?;
                if fields[0] != digest(&mapping)
                    || fields[1] != origin.epoch
                    || fields[2]
                        .parse::<u32>()
                        .ok()
                        .filter(|p| *p != 0)
                        .is_none_or(|p| p.to_string() != fields[2])
                    || fields[3]
                        .parse::<u64>()
                        .ok()
                        .filter(|p| *p != 0)
                        .is_none_or(|p| p.to_string() != fields[3])
                    || (fields[2] == origin.pid.to_string()
                        && fields[3] == origin.created.to_string())
                {
                    return Err(deny());
                }
                // Actual exclusion and writer handles are already held. Reopening
                // read-only does not retire the prior owner or reuse its budget.
                // This process can never activate a writer from this acquisition.
                recovery_only = true;
                let suffix = format!("{}-{}-{}", origin.epoch, origin.pid, origin.created);
                retirement_name = format!("read-recovery-retired-{suffix}.bin");
                create_attempt_record(
                    &root,
                    &format!("read-recovery-acquired-{suffix}.bin"),
                    &acquisition,
                )?;
            }
            Err(error) if matches!(error.raw_os_error(), Some(2 | 3)) => {
                create_attempt_record(&root, &acquisition_name, &acquisition)?;
            }
            Err(error) => return Err(error),
        }
        #[cfg(not(feature = "service-thermal-mutation-v1"))]
        create_attempt_record(&root, &acquisition_name, &acquisition)?;
        let proof = ReadAdmission {
            lifecycle: 1,
            generation: Sha256::digest(&acquisition).into(),
        };
        origin.verify()?;
        Ok(Arc::new(Self {
            origin,
            resources: Mutex::new(Some(Resources {
                root,
                _ancestors: ancestors,
                _exclusion: exclusion,
                _owner: owner,
                _records: held,
                _build: build,
            })),
            proof,
            retirement_name,
            recovery_only,
            state: AtomicU8::new(0),
        }))
    }
    fn authorize(&self) -> Option<ReadAdmission> {
        if self.state.load(Ordering::Acquire) != 0 {
            return None;
        }
        // Provisional health denies temporarily; actual trust loss is sticky.
        authorize_original(
            &self.state,
            self.origin.committed(),
            || self.origin.mailbox_mapping().map(drop),
            ReadAdmission {
                lifecycle: self.origin.lifecycle.active()?,
                ..self.proof
            },
        )
    }
    // `authorize` without the lifecycle epoch.
    #[cfg(feature = "service-profile-triggers-v1")]
    fn authorize_storage(&self) -> bool {
        authorize_original(
            &self.state,
            self.origin.committed(),
            || self.origin.mailbox_mapping().map(drop),
            self.proof,
        )
        .is_some()
    }
    fn revoke(&self) {
        let _ = self
            .state
            .compare_exchange(0, 1, Ordering::AcqRel, Ordering::Acquire);
    }
    fn retire(&self) -> io::Result<()> {
        if self.state.load(Ordering::Acquire) == 2 {
            return Ok(());
        }
        self.revoke();
        let mut resources = self.resources.lock().map_err(|_| deny())?;
        let held = resources.as_mut().ok_or_else(deny)?;
        #[cfg(feature = "service-thermal-mutation-v1")]
        held._owner.retire_active()?;
        create_attempt_record(
            &held.root,
            &self.retirement_name,
            &encode(
                "OpenBlade.NativeMailboxReadRetired.v1",
                &[
                    &self.origin.epoch,
                    &self.origin.pid.to_string(),
                    &self.origin.created.to_string(),
                ],
            ),
        )?;
        self.state.store(2, Ordering::Release);
        resources.take();
        Ok(())
    }
}
impl ReadCustody for ReadLease {
    #[cfg(feature = "service-thermal-mutation-v1")]
    fn journal_retire_prepared(&self, expected: Option<&[u8]>, bytes: &[u8]) -> io::Result<()> {
        self.verify_integrity()?;
        crate::thermal_history::validate_prepared_retirement(
            expected.ok_or_else(deny)?,
            bytes,
            &digest(&self.proof.generation),
        )?;
        let held = self.resources.lock().map_err(|_| deny())?;
        let owner = &held.as_ref().ok_or_else(deny)?._owner;
        if owner.journal_read(true)?.is_some() {
            return Err(deny());
        }
        owner.journal_replace(expected, bytes)
    }
    fn authorize_owned_stop(&self) -> Option<ReadAdmission> {
        if self.state.load(Ordering::Acquire) != 0 {
            return None;
        }
        self.origin.verify_owned_stop().ok()?;
        authorize_original(
            &self.state,
            self.origin.owned_stop_committed(),
            || self.origin.owned_stop_mapping().map(drop),
            ReadAdmission {
                lifecycle: self.origin.lifecycle.active()?,
                ..self.proof
            },
        )
    }
    /// The low-power scope is `authorize` at that suspend generation's own
    /// fence (`Lifecycle::fence_for`, through `low_power_admission`) instead
    /// of the ordinary epoch, so a lock or unlock during the suspend leaves
    /// it admitted: the same live origin trust, original custody and
    /// generation.
    #[cfg(feature = "service-keyboard-driver-mode-v1")]
    fn authorize_cleanup(&self, scope: CleanupScope) -> Option<ReadAdmission> {
        let CleanupScope::LowPower { generation } = scope else {
            return self.authorize_owned_stop();
        };
        let write = openblade_domain::low_power_gate::WriteScope::LowPowerCleanup { generation };
        low_power_admission(
            &self.state,
            self.origin.committed(),
            || self.origin.mailbox_mapping().map(drop),
            self.origin.lifecycle.fence_for(write),
            self.proof,
        )
    }
    #[cfg(feature = "service-live-readings-v1")]
    fn resume_generation(&self) -> Option<u64> {
        self.origin.lifecycle.resume_generation()
    }
    #[cfg(feature = "service-thermal-mutation-v1")]
    fn journal_replace_cleanup(
        &self,
        scope: CleanupScope,
        expected: Option<&[u8]>,
        bytes: &[u8],
    ) -> io::Result<()> {
        self.authorize_cleanup(scope).ok_or_else(deny)?;
        self.resources
            .lock()
            .map_err(|_| deny())?
            .as_ref()
            .ok_or_else(deny)?
            ._owner
            .journal_replace(expected, bytes)
    }
    #[cfg(feature = "service-thermal-mutation-v1")]
    fn verify_integrity(&self) -> io::Result<()> {
        let result = self
            .resources
            .lock()
            .map_err(|_| {
                super::thermal_mutation::fatal_admission("Thermal custody lock poisoned.")
            })?
            .as_ref()
            .ok_or_else(|| {
                super::thermal_mutation::fatal_admission(
                    "Original thermal custody is no longer held.",
                )
            })?
            ._owner
            .verify_journal_custody();
        if result.is_err() {
            self.revoke();
        }
        result
    }
    #[cfg(feature = "service-thermal-mutation-v1")]
    fn journal_read(&self, pending: bool) -> io::Result<Option<Vec<u8>>> {
        self.resources
            .lock()
            .map_err(|_| deny())?
            .as_ref()
            .ok_or_else(deny)?
            ._owner
            .journal_read(pending)
    }
    #[cfg(feature = "service-thermal-mutation-v1")]
    fn journal_read_release(&self, pending: bool) -> io::Result<Option<Vec<u8>>> {
        self.resources
            .lock()
            .map_err(|_| deny())?
            .as_ref()
            .ok_or_else(deny)?
            ._owner
            .journal_read_release(pending)
    }
    #[cfg(feature = "service-thermal-mutation-v1")]
    fn journal_reserve(
        &self,
        expected: Option<&[u8]>,
        needed: usize,
    ) -> io::Result<Option<Vec<u8>>> {
        self.authorize().ok_or_else(deny)?;
        self.resources
            .lock()
            .map_err(|_| deny())?
            .as_ref()
            .ok_or_else(deny)?
            ._owner
            .journal_reserve(expected, needed)
    }
    #[cfg(feature = "service-thermal-mutation-v1")]
    fn journal_replace(&self, expected: Option<&[u8]>, bytes: &[u8]) -> io::Result<()> {
        self.authorize().ok_or_else(deny)?;
        self.resources
            .lock()
            .map_err(|_| deny())?
            .as_ref()
            .ok_or_else(deny)?
            ._owner
            .journal_replace(expected, bytes)
    }
    #[cfg(feature = "service-keyboard-driver-mode-v1")]
    fn recovery_only(&self) -> bool {
        self.recovery_only
    }
    #[cfg(feature = "service-keyboard-driver-mode-v1")]
    fn driver_mode_record_read(&self) -> io::Result<Option<DriverModeRecord>> {
        self.resources
            .lock()
            .map_err(|_| deny())?
            .as_ref()
            .ok_or_else(deny)?
            ._owner
            .driver_mode_record_read()
    }
    // The owner Store refuses a custody this process never activated as
    // well; the recovery-only check names the reason here.
    #[cfg(feature = "service-keyboard-driver-mode-v1")]
    fn driver_mode_record_owner(&self) -> io::Result<String> {
        if self.recovery_only {
            return Err(deny());
        }
        self.resources
            .lock()
            .map_err(|_| deny())?
            .as_ref()
            .ok_or_else(deny)?
            ._owner
            .driver_mode_record_owner()
    }
    #[cfg(feature = "service-keyboard-driver-mode-v1")]
    fn driver_mode_record_replace(
        &self,
        expected: Option<&DriverModeRecord>,
        next: &DriverModeRecord,
    ) -> io::Result<()> {
        if self.recovery_only {
            return Err(deny());
        }
        self.authorize().ok_or_else(deny)?;
        self.resources
            .lock()
            .map_err(|_| deny())?
            .as_ref()
            .ok_or_else(deny)?
            ._owner
            .driver_mode_record_replace(expected, next)
    }
    #[cfg(feature = "service-keyboard-driver-mode-v1")]
    fn driver_mode_record_replace_cleanup(
        &self,
        scope: CleanupScope,
        expected: Option<&DriverModeRecord>,
        next: &DriverModeRecord,
    ) -> io::Result<()> {
        if self.recovery_only {
            return Err(deny());
        }
        self.authorize_cleanup(scope).ok_or_else(deny)?;
        self.resources
            .lock()
            .map_err(|_| deny())?
            .as_ref()
            .ok_or_else(deny)?
            ._owner
            .driver_mode_record_replace(expected, next)
    }
    #[cfg(feature = "service-thermal-mutation-v1")]
    fn activate_mutation(&self) -> Result<(), ActivationFailure> {
        use ActivationFailure::{KnownUnpublished, Uncertain};
        if self.recovery_only {
            return Err(KnownUnpublished(deny()));
        }
        self.authorize().ok_or_else(|| KnownUnpublished(deny()))?;
        let mut held = self.resources.lock().map_err(|_| Uncertain(deny()))?;
        let resources = held.as_mut().ok_or_else(|| Uncertain(deny()))?;
        let nonce = digest(&self.proof.generation);
        resources
            ._owner
            .activate_first(&self.origin, nonce)
            .map_err(|e| {
                if resources._owner.activation_started() {
                    Uncertain(e)
                } else {
                    KnownUnpublished(e)
                }
            })
    }
    fn authorize(&self) -> Option<ReadAdmission> {
        ReadLease::authorize(self)
    }
    #[cfg(feature = "service-profile-triggers-v1")]
    fn authorize_storage(&self) -> bool {
        ReadLease::authorize_storage(self)
    }
    fn revoke(&self) {
        ReadLease::revoke(self);
    }
    fn retire(&self) -> io::Result<()> {
        ReadLease::retire(self)
    }
    fn confirm_drained(&self) -> io::Result<()> {
        if self.state.load(Ordering::Acquire) != 2 {
            return Err(deny());
        }
        self.origin.confirm_transport_drain()
    }
}
impl Drop for ReadLease {
    fn drop(&mut self) {
        if self.state.load(Ordering::Acquire) != 2 {
            // A worker fault, cancellation or dropped source preserves custody.
            std::mem::forget(
                self.resources
                    .get_mut()
                    .unwrap_or_else(|e| e.into_inner())
                    .take(),
            );
            std::mem::forget(self.origin.clone());
        }
    }
}

#[derive(Clone)]
pub(super) enum MailboxSource {
    Reader(ReadSource),
    NoReader {
        origin: Arc<dyn super::status_source::StatusOrigin>,
        retired: Arc<std::sync::atomic::AtomicBool>,
    },
}
impl MailboxSource {
    /// Whether the last thermal observation confirmed manual fans
    /// (`ReadSource::confirmed_manual_fans`); without a reader, none did.
    #[cfg(feature = "service-profile-triggers-v1")]
    pub(super) fn confirmed_manual_fans(&self) -> bool {
        matches!(self, Self::Reader(reader) if reader.confirmed_manual_fans())
    }
    /// The reader's confirmed thermal drift (`thermal_drift`); without a
    /// reader, a tracker nothing observes.
    #[cfg(feature = "service-profile-triggers-v1")]
    pub(super) fn drift(&self) -> Arc<Mutex<super::thermal_drift::Tracker>> {
        match self {
            Self::Reader(reader) => reader.drift.clone(),
            Self::NoReader { .. } => Arc::default(),
        }
    }
    #[cfg(feature = "service-thermal-mutation-v1")]
    pub(super) fn fatal_evidence(&self) -> Arc<Mutex<Option<&'static str>>> {
        match self {
            Self::Reader(reader) => reader.fatal.clone(),
            Self::NoReader { .. } => Arc::default(),
        }
    }
    pub(super) fn acquire(origin: Arc<Origin>) -> io::Result<Self> {
        let actual = origin.clone();
        Self::compose(origin, || ReadSource::acquire(actual))
    }
    pub(super) fn compose(
        origin: Arc<dyn super::status_source::StatusOrigin>,
        acquire_before_worker: impl FnOnce() -> io::Result<ReadSource>,
    ) -> io::Result<Self> {
        origin.verify()?;
        let result = acquire_before_worker();
        // Invalid startup custody or genuine origin loss remains fatal. Every
        // error before ReadSource construction proves only this attempt had no
        // firmware worker; it says nothing about earlier acquisition markers.
        origin.verify()?;
        Ok(match result {
            Ok(reader) => Self::Reader(reader),
            Err(_) => Self::NoReader {
                origin,
                retired: Arc::new(std::sync::atomic::AtomicBool::new(false)),
            },
        })
    }
    pub(super) fn decorate(
        &self,
        publisher: &watch::Sender<Arc<Publication>>,
        metadata: Arc<Publication>,
    ) -> io::Result<()> {
        match self {
            Self::Reader(reader) => reader.decorate(publisher, Some(metadata)),
            Self::NoReader { .. } => {
                let current = publisher.borrow().clone();
                let reason =
                    " Thermal reader unavailable before native I/O; diagnostic status only.";
                let mut next = Publication {
                    active: metadata
                        .active
                        .unavailable_mailbox(current.active.revision(), reason),
                    inactive: metadata
                        .inactive
                        .unavailable_mailbox(current.active.revision(), reason),
                };
                if serde_json::to_vec(&next.active)? != serde_json::to_vec(&current.active)?
                    || serde_json::to_vec(&next.inactive)? != serde_json::to_vec(&current.inactive)?
                {
                    let revision = current.active.revision().checked_add(1).ok_or_else(deny)?;
                    next.active.set_observation_revision(revision);
                    next.inactive.set_observation_revision(revision);
                    publisher.send_replace(Arc::new(next));
                }
                Ok(())
            }
        }
    }
    pub(super) fn next_refresh(&self) -> Option<Instant> {
        match self {
            Self::Reader(reader) => reader.next_refresh(),
            Self::NoReader { .. } => None,
        }
    }
    pub(super) fn observe(
        &self,
        snapshot: Snapshot,
        deadline: Instant,
        publisher: watch::Sender<Arc<Publication>>,
    ) -> Observation {
        match self {
            Self::Reader(reader) => reader.observe(snapshot, deadline, publisher),
            Self::NoReader { .. } => Box::pin(async { Ok(None) }),
        }
    }
    pub(super) async fn stop(&self) -> io::Result<()> {
        match self {
            Self::Reader(reader) => reader.stop().await,
            Self::NoReader { retired, .. } => {
                retired.store(true, Ordering::Release);
                Ok(())
            }
        }
    }
    pub(super) fn confirm_drained(&self) -> io::Result<()> {
        match self {
            Self::Reader(reader) => reader.confirm_drained(),
            Self::NoReader { origin, retired } if retired.load(Ordering::Acquire) => {
                origin.confirm_transport_drain()
            }
            _ => Err(deny()),
        }
    }
}

#[cfg(feature = "service-thermal-mutation-v1")]
#[derive(Debug)]
struct UnresolvedSettlement(io::Error);
#[cfg(feature = "service-thermal-mutation-v1")]
impl std::fmt::Display for UnresolvedSettlement {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        self.0.fmt(f)
    }
}
#[cfg(feature = "service-thermal-mutation-v1")]
impl std::error::Error for UnresolvedSettlement {}

// Last getter completion owns this monotonic age; status refresh never renews it.
pub(super) const RETENTION_WINDOW: std::time::Duration = std::time::Duration::from_secs(5);

#[cfg(feature = "service-thermal-mutation-v1")]
#[derive(Clone, Copy, Default, Eq, PartialEq)]
enum MutationPhase {
    #[default]
    Idle,
    ReaderDrain,
    Active,
    Fenced,
}

#[cfg(feature = "service-thermal-mutation-v1")]
type PermitRef = Arc<dyn crate::discovery::performance::mutation::Permit>;
#[cfg(feature = "service-thermal-mutation-v1")]
type IntentFn = Rc<dyn Fn(Snapshot) -> io::Result<Snapshot>>;
#[cfg(feature = "service-thermal-mutation-v1")]
type ExpiryFn = Rc<dyn Fn() -> Option<Instant>>;
#[cfg(feature = "service-thermal-mutation-v1")]
type MutationLane = (Worker<ReadAdmission>, PermitRef);

#[derive(Clone)]
pub(super) struct ReadSource {
    // One first-cause record spans reader admission and canceled mutation completion.
    #[cfg(feature = "service-thermal-mutation-v1")]
    fatal: Arc<Mutex<Option<&'static str>>>,
    #[cfg(feature = "service-thermal-mutation-v1")]
    factories: Option<Rc<MutationFactories>>,
    lease: Arc<dyn ReadCustody>,
    worker: Rc<RefCell<Worker<ReadAdmission>>>,
    #[cfg(feature = "service-thermal-mutation-v1")]
    mutation: Rc<RefCell<Option<MutationLane>>>,
    #[cfg(feature = "service-thermal-mutation-v1")]
    activated: Rc<std::cell::Cell<bool>>,
    #[cfg(feature = "service-thermal-mutation-v1")]
    phase: Rc<std::cell::Cell<MutationPhase>>,
    // Retain original request/owner proof after uncertain settlement; never retry.
    #[cfg(feature = "service-thermal-mutation-v1")]
    unsettled: Rc<RefCell<Option<PermitRef>>>,
    #[cfg(feature = "service-thermal-mutation-v1")]
    operation: Rc<tokio::sync::Mutex<()>>,
    #[cfg(all(test, feature = "service-thermal-mutation-v1"))]
    registration_pause: Rc<std::cell::Cell<u8>>,
    #[cfg(feature = "service-thermal-mutation-v1")]
    intent: Rc<RefCell<Option<IntentFn>>>,
    #[cfg(feature = "service-thermal-mutation-v1")]
    battery_intent: Rc<RefCell<Option<IntentFn>>>,
    #[cfg(feature = "service-thermal-mutation-v1")]
    battery_expiry: Rc<RefCell<Option<ExpiryFn>>>,
    // Managed `PerformanceShortcutPreview`, published with every active
    // status (`current_policy`).
    #[cfg(feature = "service-performance-shortcut-v1")]
    shortcut_preview: Rc<RefCell<Option<crate::service_status::ShortcutPreview>>>,
    last: Rc<RefCell<Option<(Snapshot, Instant)>>>,
    /// When the job behind `last` started. Observations finish out of job
    /// order while one waits for its GPU sample, so a reading from an
    /// earlier job is never stored over it.
    newest_job: Rc<std::cell::Cell<Option<Instant>>>,
    // Confirmed thermal drift (`thermal_drift`), shared with the runtime's
    // permits, which decide on the worker thread.
    #[cfg(feature = "service-profile-triggers-v1")]
    drift: Arc<Mutex<super::thermal_drift::Tracker>>,
    #[cfg(feature = "service-keyboard-driver-mode-read-v1")]
    driver_mode: Rc<driver_mode::Cadence>,
    #[cfg(feature = "service-live-readings-v1")]
    live: Rc<live_readings::Host>,
    #[cfg(feature = "service-live-readings-v1")]
    firmware: Rc<firmware_versions::Host>,
    #[cfg(feature = "service-live-readings-v1")]
    gpu: Rc<gpu_temperature::Host>,
    metadata: Rc<RefCell<Option<Arc<Publication>>>>,
}
impl ReadSource {
    #[cfg(all(test, feature = "service-thermal-mutation-v1"))]
    pub(super) fn registration_pause_for_tests(&self) -> Rc<std::cell::Cell<u8>> {
        self.registration_pause.clone()
    }
    pub(super) fn acquire(origin: Arc<Origin>) -> io::Result<Self> {
        let lease = ReadLease::acquire(origin)?;
        let source = Self::compose(lease, |custody| {
            native_read_worker(move || custody.authorize())
        });
        #[cfg(feature = "service-thermal-mutation-v1")]
        let source = source.with_mutation(MutationFactories::native());
        // Only the acquired production source reads Windows battery state,
        // firmware versions and the GPU temperature.
        #[cfg(feature = "service-live-readings-v1")]
        let source = Self {
            live: Rc::new(live_readings::Host::new(Rc::new(live_readings::Windows))),
            firmware: Rc::new(firmware_versions::Host::new(Arc::new(
                firmware_versions::Windows,
            ))),
            gpu: Rc::new(gpu_temperature::Host::new(
                gpu_temperature::windows_sampler(),
            )),
            ..source
        };
        Ok(source)
    }
    #[cfg(all(test, feature = "service-live-readings-v1"))]
    fn with_firmware_capture_for_tests(self, capture: Arc<dyn firmware_versions::Capture>) -> Self {
        Self {
            firmware: Rc::new(firmware_versions::Host::new(capture)),
            ..self
        }
    }
    #[cfg(all(test, feature = "service-live-readings-v1"))]
    fn with_gpu_for_tests(self, factory: gpu_temperature::Factory) -> Self {
        Self {
            gpu: Rc::new(gpu_temperature::Host::new(factory)),
            ..self
        }
    }
    #[cfg(all(test, feature = "service-live-readings-v1"))]
    fn with_live_capture_for_tests(self, capture: Rc<dyn live_readings::Capture>) -> Self {
        Self {
            live: Rc::new(live_readings::Host::new(capture)),
            ..self
        }
    }
    pub(super) fn compose(
        lease: Arc<dyn ReadCustody>,
        factory: impl FnOnce(Arc<dyn ReadCustody>) -> Worker<ReadAdmission>,
    ) -> Self {
        #[cfg(feature = "service-thermal-mutation-v1")]
        let fatal = Arc::default();
        #[cfg(feature = "service-thermal-mutation-v1")]
        let lease: Arc<dyn ReadCustody> = Arc::new(GuardedReadCustody {
            original: lease,
            fatal: Arc::clone(&fatal),
        });
        let worker = factory(lease.clone());
        Self {
            #[cfg(feature = "service-thermal-mutation-v1")]
            fatal,
            lease,
            #[cfg(feature = "service-thermal-mutation-v1")]
            factories: None,
            worker: Rc::new(RefCell::new(worker)),
            #[cfg(feature = "service-thermal-mutation-v1")]
            mutation: Rc::default(),
            #[cfg(feature = "service-thermal-mutation-v1")]
            activated: Rc::default(),
            #[cfg(feature = "service-thermal-mutation-v1")]
            phase: Rc::default(),
            #[cfg(feature = "service-thermal-mutation-v1")]
            unsettled: Rc::default(),
            #[cfg(feature = "service-thermal-mutation-v1")]
            operation: Rc::default(),
            #[cfg(all(test, feature = "service-thermal-mutation-v1"))]
            registration_pause: Rc::default(),
            #[cfg(feature = "service-thermal-mutation-v1")]
            intent: Rc::default(),
            #[cfg(feature = "service-thermal-mutation-v1")]
            battery_intent: Rc::default(),
            #[cfg(feature = "service-thermal-mutation-v1")]
            battery_expiry: Rc::default(),
            #[cfg(feature = "service-performance-shortcut-v1")]
            shortcut_preview: Rc::default(),
            last: Rc::default(),
            newest_job: Rc::default(),
            #[cfg(feature = "service-profile-triggers-v1")]
            drift: Arc::default(),
            #[cfg(feature = "service-keyboard-driver-mode-read-v1")]
            driver_mode: Rc::default(),
            #[cfg(feature = "service-live-readings-v1")]
            live: Rc::new(live_readings::Host::new(Rc::new(
                live_readings::Unavailable,
            ))),
            #[cfg(feature = "service-live-readings-v1")]
            firmware: Rc::new(firmware_versions::Host::none()),
            #[cfg(feature = "service-live-readings-v1")]
            gpu: Rc::new(gpu_temperature::Host::none()),
            metadata: Rc::default(),
        }
    }
    /// Joins what a lighting request left the confirmed lighting members
    /// (`Snapshot::with_lighting_reading`) to the retained thermal read, so
    /// each later projection of that read (`decorate`) carries them until the
    /// next thermal read replaces it. That read does not read lighting (slice
    /// L2), so it confirms no lighting.
    #[cfg(feature = "service-lighting-v1")]
    pub(super) fn record_lighting(
        &self,
        brightness: Option<Option<u8>>,
        logo: Option<Option<i32>>,
    ) {
        if let Some((thermal, _)) = self.last.borrow_mut().as_mut() {
            *thermal = thermal.clone().with_lighting_reading(brightness, logo);
        }
    }
    pub(super) fn decorate(
        &self,
        publisher: &watch::Sender<Arc<Publication>>,
        metadata: Option<Arc<Publication>>,
    ) -> io::Result<()> {
        self.decorate_at(publisher, metadata, Instant::now())
    }
    fn decorate_at(
        &self,
        publisher: &watch::Sender<Arc<Publication>>,
        metadata: Option<Arc<Publication>>,
        now: Instant,
    ) -> io::Result<()> {
        if let Some(metadata) = metadata {
            *self.metadata.borrow_mut() = Some(metadata);
        }
        let current = publisher.borrow().clone();
        let metadata = self
            .metadata
            .borrow()
            .clone()
            .unwrap_or_else(|| current.clone());
        // Authorization loss is sticky in the custody object, independent of
        // metadata origin. Never turn its failure into a metadata endpoint error.
        let authorized = self.lease.authorize().is_some();
        #[cfg(feature = "service-thermal-mutation-v1")]
        self.lease.verify_integrity()?;
        let last = self.last.borrow();
        let thermal = last.as_ref().filter(|(_, completed)| {
            now.checked_duration_since(*completed).unwrap_or_default() < RETENTION_WINDOW
        });
        let project = |base: &Snapshot| match thermal {
            Some((thermal, _)) => {
                let retained = base.retained_mailbox(thermal, current.active.revision());
                if authorized {
                    retained
                } else {
                    retained.read_revoked()
                }
            }
            None => base.unavailable_mailbox(
                current.active.revision(),
                if last.is_some() {
                    " Last thermal observation expired; current settings are unavailable."
                } else if !authorized {
                    " Thermal reader is revoked or unavailable; no fresh read is confirmed."
                } else {
                    " No fresh thermal read is confirmed."
                },
            ),
        };
        #[cfg(feature = "service-thermal-mutation-v1")]
        let project = |base: &Snapshot| self.current_policy(project(base));
        #[cfg(not(feature = "service-thermal-mutation-v1"))]
        let project = |base: &Snapshot| Ok::<_, io::Error>(project(base));
        let mut next = Publication {
            active: project(&metadata.active)?,
            inactive: project(&metadata.inactive)?,
        };
        // Managed battery health is device-independent status: the first
        // status captures it, and a power-source change retries it while null.
        #[cfg(feature = "service-live-readings-v1")]
        {
            self.live
                .power_source(metadata.active.active_power_source());
            let health = self.live.battery_health();
            next.active = next.active.with_battery_health(health);
            next.inactive = next.inactive.with_battery_health(health);
            // Managed captures firmware versions once, at start. The worker
            // reads them off this path, so a status before it finishes has none.
            let firmware = self.firmware.poll(now);
            next.active = next.active.with_firmware_versions(firmware.clone());
            next.inactive = next.inactive.with_firmware_versions(firmware);
        }
        if serde_json::to_vec(&next.active)? == serde_json::to_vec(&current.active)?
            && serde_json::to_vec(&next.inactive)? == serde_json::to_vec(&current.inactive)?
        {
            return Ok(());
        }
        let revision = current.active.revision().checked_add(1).ok_or_else(deny)?;
        next.active.set_observation_revision(revision);
        next.inactive.set_observation_revision(revision);
        publisher.send_replace(Arc::new(next));
        Ok(())
    }
    #[cfg(all(test, feature = "service-thermal-mutation-v1"))]
    pub(super) fn idle_for_tests(&self) -> bool {
        self.phase.get() == MutationPhase::Idle
    }
    /// Whether the last thermal observation confirmed manual fans, the
    /// confirmed fans managed `DeviceSubsystemOrder.ForTransition` orders by.
    /// Managed retains its confirmed state between observations, so an
    /// observation past the retention window still counts. It chooses a
    /// reconciliation's order only: each step reads firmware again.
    #[cfg(feature = "service-profile-triggers-v1")]
    pub(super) fn confirmed_manual_fans(&self) -> bool {
        self.last
            .borrow()
            .as_ref()
            .is_some_and(|(thermal, _)| thermal.confirmed_manual_fans())
    }
    #[cfg(test)]
    pub(super) fn last_completed_for_tests(&self) -> Option<Instant> {
        self.last.borrow().as_ref().map(|(_, at)| *at)
    }
    #[cfg(test)]
    pub(super) fn expire_for_tests(&self) {
        if let Some((_, at)) = self.last.borrow_mut().as_mut() {
            *at = Instant::now() - RETENTION_WINDOW;
        }
    }
    pub(super) fn next_refresh(&self) -> Option<Instant> {
        let thermal = self
            .last
            .borrow()
            .as_ref()
            .map(|(_, at)| *at + RETENTION_WINDOW)
            .filter(|due| *due > Instant::now());
        #[cfg(feature = "service-thermal-mutation-v1")]
        let battery = self
            .battery_expiry
            .borrow()
            .as_ref()
            .and_then(|expiry| expiry());
        #[cfg(not(feature = "service-thermal-mutation-v1"))]
        let battery: Option<Instant> = None;
        thermal.into_iter().chain(battery).min()
    }
    #[cfg(not(feature = "service-keyboard-driver-mode-read-v1"))]
    pub(super) fn observe(
        &self,
        snapshot: Snapshot,
        deadline: Instant,
        publisher: watch::Sender<Arc<Publication>>,
    ) -> Observation {
        self.observe_at(snapshot, deadline, publisher)
    }
    #[cfg(feature = "service-keyboard-driver-mode-read-v1")]
    pub(super) fn observe(
        &self,
        snapshot: Snapshot,
        deadline: Instant,
        publisher: watch::Sender<Arc<Publication>>,
    ) -> Observation {
        self.observe_at(snapshot, deadline, publisher, Instant::now())
    }
    /// One read job. With the Driver-mode read, `job` is the instant the job
    /// starts: it decides the 00/84 query, dates its result and selects the
    /// published value.
    pub(super) fn observe_at(
        &self,
        _: Snapshot,
        deadline: Instant,
        publisher: watch::Sender<Arc<Publication>>,
        #[cfg(feature = "service-keyboard-driver-mode-read-v1")] job: Instant,
    ) -> Observation {
        let source = self.clone();
        if self.metadata.borrow().is_none() {
            *self.metadata.borrow_mut() = Some(publisher.borrow().clone());
        }
        // The ObserveState being served sets this while the observation is
        // created (`StatusRequest::scope_live_readings`); any other read asks
        // for none.
        #[cfg(feature = "service-live-readings-v1")]
        let live = live_readings::requested();
        Box::pin(async move {
            if source.lease.authorize().is_none() {
                source.decorate(&publisher, None)?;
                return Ok(None);
            }
            #[cfg(feature = "service-thermal-mutation-v1")]
            // Observation preserves the worker's no-queue occupancy contract.
            // Waiting here can deadlock a caller holding an earlier observation
            // while asking whether another read can enter. Mutation still owns
            // serialization and actual transport retirement.
            let Ok(_serial) = source.operation.try_lock() else {
                return Ok(None);
            };
            #[cfg(feature = "service-thermal-mutation-v1")]
            source.drain_mutation(true).await?;
            let worker = source.worker.borrow().clone();
            // Managed captures battery power before its device read.
            #[cfg(feature = "service-live-readings-v1")]
            let battery_power = (live & crate::service_status::live_sensors::BATTERY_POWER != 0)
                .then(|| source.live.battery_power_flow())
                .flatten();
            // Managed reads GPU temperature after its device read. Its worker
            // samples it beside the job here, so the 150 ms activity window
            // and an NVML start lengthen neither the job nor the reply, and
            // a sample whose reading is not accepted is dropped unpublished.
            #[cfg(feature = "service-live-readings-v1")]
            let gpu = (live & crate::service_status::live_sensors::GPU_TEMPERATURE != 0)
                .then(|| source.gpu.request(deadline))
                .flatten();
            // The 0D/88 pair rides this serialized job only for an
            // ObserveState that asked for fan speeds, after the thermal read,
            // and not while an unconfirmed pair's back-off lasts.
            #[cfg(feature = "service-live-readings-v1")]
            let fan_speeds = live & crate::service_status::live_sensors::FAN_SPEEDS != 0;
            #[cfg(feature = "service-live-readings-v1")]
            let fan_query = fan_speeds
                .then(|| source.live.fan_query(Instant::now()))
                .flatten();
            #[cfg(feature = "service-live-readings-v1")]
            let send_fans = fan_query.is_some();
            // The 00/87 MCU version query rides this job, after the thermal
            // read and every other query, while the firmware record lacks it
            // and its budget since the last resume lasts. The gate is read
            // only while the query may still be sent.
            #[cfg(feature = "service-live-readings-v1")]
            let mcu_query = source
                .firmware
                .mcu_query(|| source.lease.resume_generation());
            // The worker runs one job at a time, so this orders the readings.
            let started = Instant::now();
            #[cfg(not(any(
                feature = "service-keyboard-driver-mode-read-v1",
                feature = "service-live-readings-v1"
            )))]
            let result = worker.observe(deadline, None).await;
            // The 00/84 query rides this serialized job at the managed
            // observation cadence; it adds no HID traffic between those reads.
            // The job instant decides the query, records its result and
            // selects the published value.
            #[cfg(feature = "service-keyboard-driver-mode-read-v1")]
            let asked = source
                .lease
                .authorize()
                .filter(|admission| source.driver_mode.due(*admission, job));
            #[cfg(any(
                feature = "service-keyboard-driver-mode-read-v1",
                feature = "service-live-readings-v1"
            ))]
            let result = worker
                .observe_with_queries(
                    deadline,
                    crate::discovery::performance::Queries {
                        #[cfg(feature = "service-keyboard-driver-mode-read-v1")]
                        device_mode: asked.is_some(),
                        #[cfg(feature = "service-live-readings-v1")]
                        fan_rpm: fan_query,
                        #[cfg(feature = "service-live-readings-v1")]
                        mcu_version: mcu_query,
                    },
                )
                .await;
            #[cfg(feature = "service-keyboard-driver-mode-read-v1")]
            if let Some(asked) = asked {
                // A query that was asked for and brought back no result, or a
                // job that brought back nothing, is unknown until the next
                // interval: it is never retried in between. One skipped for
                // lack of job time sent nothing and stays due. The worker's
                // `Ok(None)` also covers a job it refused to start (stopped,
                // unconfirmed, busy or out of time), which sent nothing; that
                // too publishes null over a cached mode until the next
                // interval. It fails closed, so the two are not told apart.
                let read = match &result {
                    Ok(Some(reading)) if reading.admission == asked => reading.device_mode,
                    _ => crate::discovery::performance::DeviceModeRead::Unconfirmed,
                };
                source.driver_mode.record(asked, read, job);
            }
            // A job allowed to send the pair settles it before any admission
            // check, so a sent, unconfirmed pair backs off even when this job
            // publishes nothing. Whether a SET went out is the worker's count,
            // not this result: a job that brought back no reading backs off
            // only if it sent one. A dropped observation never gets here; the
            // next `fan_query` accounts for its pair.
            #[cfg(feature = "service-live-readings-v1")]
            if send_fans {
                let read = match &result {
                    Ok(Some(reading)) => reading.fan_rpm,
                    _ => crate::discovery::performance::FanRpmRead::NotRequested,
                };
                source.live.settle(read, Instant::now());
            }
            #[cfg(feature = "service-live-readings-v1")]
            if fan_speeds && !matches!(result, Ok(Some(_))) {
                source.live.missed(None, Instant::now());
            }
            #[cfg(feature = "service-thermal-mutation-v1")]
            source.lease.verify_integrity()?;
            let reading = match result {
                Ok(reading) => reading,
                Err(_) => {
                    source.lease.revoke();
                    source.decorate(&publisher, None)?;
                    return Ok(None);
                }
            };
            let Some(reading) = reading else {
                return Ok(None);
            };
            // The sample is waited for at most `gpu_temperature::AFTER_READING`
            // and never past `RESERVE` before the deadline, ahead of the
            // admission and deadline checks below.
            #[cfg(feature = "service-live-readings-v1")]
            let gpu = match gpu {
                Some(pending) => pending.temperature().await,
                None => None,
            };
            let admission = source.lease.authorize();
            #[cfg(feature = "service-thermal-mutation-v1")]
            source.lease.verify_integrity()?;
            if admission != Some(reading.admission) || Instant::now() >= deadline {
                return Ok(None);
            }
            // A later job finished first while this one waited for its GPU
            // sample: this reading is older than the one already published.
            if source.superseded(started) {
                return Ok(None);
            }
            #[cfg(feature = "service-live-readings-v1")]
            let fans = fan_speeds
                .then(|| {
                    source
                        .live
                        .fans(reading.admission, reading.fan_rpm, Instant::now())
                })
                .flatten();
            let lease = source.lease.clone();
            let proof = reading.admission;
            // Metadata may refresh while the native worker runs outside the
            // source mutex. Never assign its newer revision to an old snapshot.
            let current = publisher.borrow().active.clone();
            // A fresh exact-device reading is drift evidence, and may settle
            // a resume hold (managed periodic `ObserveAutomaticState`, then
            // `ReconcileResumePerformanceDrift`).
            #[cfg(feature = "service-profile-triggers-v1")]
            if let Ok(mut drift) = source.drift.lock() {
                drift.observe_published(reading.performance);
                drift.settle(reading.performance, Instant::now());
            }
            // The status's observation record is exact-model data and the
            // reading carries no model; its Reader is the one producer.
            debug_assert_eq!(
                crate::discovery::performance::OBSERVED_IDENTITY,
                crate::service_status::OBSERVED_CAPABILITIES_MODEL
            );
            let mut observed = current.with_mailbox_read(
                reading.performance,
                crate::service_status::ReadConfirmation::new(deadline, move || {
                    lease.authorize() == Some(proof)
                }),
            );
            #[cfg(feature = "service-keyboard-driver-mode-read-v1")]
            {
                observed =
                    observed.with_keyboard_driver_mode(source.driver_mode.active(proof, job));
            }
            #[cfg(feature = "service-live-readings-v1")]
            {
                source.live.verified();
                // Only an accepted reading's version is kept; the next
                // publication carries it.
                source.firmware.mcu_read(reading.mcu_version);
                observed = observed
                    .with_live_readings(live, battery_power, fans, gpu)
                    .with_battery_health(source.live.battery_health());
            }
            source.retain(&observed, started);
            source.decorate(&publisher, None)?;
            // The reply at revision N carries what publication N carries.
            #[cfg(feature = "service-live-readings-v1")]
            {
                observed =
                    observed.with_firmware_versions(publisher.borrow().active.firmware_versions());
            }
            #[cfg(feature = "service-thermal-mutation-v1")]
            {
                observed = source.current_policy(observed)?;
            }
            observed.set_observation_revision(publisher.borrow().active.revision());
            if !observed.has_fresh_mailbox_read() {
                return Ok(None);
            }
            Ok(Some(observed))
        })
    }
    pub(super) async fn stop(&self) -> io::Result<()> {
        self.lease.revoke();
        // A firmware capture still running ends after the read it is in, and
        // the GPU worker makes no call after the one it is in and then ends
        // its NVML session; neither is waited for.
        #[cfg(feature = "service-live-readings-v1")]
        {
            self.firmware.stop();
            self.gpu.stop();
        }
        #[cfg(feature = "service-thermal-mutation-v1")]
        let settlement = self.drain_mutation(false).await;
        #[cfg(feature = "service-thermal-mutation-v1")]
        if settlement.as_ref().is_err_and(|error| {
            !error
                .get_ref()
                .is_some_and(|cause| cause.is::<UnresolvedSettlement>())
        }) {
            return settlement;
        }
        // Positive callback retirement remains independently publishable when
        // terminal bookkeeping is unresolved. Never retry the retained permit.
        let worker = self.worker.borrow().clone();
        worker.stop().await?;
        self.lease.retire()?;
        #[cfg(feature = "service-thermal-mutation-v1")]
        return settlement;
        #[cfg(not(feature = "service-thermal-mutation-v1"))]
        Ok(())
    }
    pub(super) fn confirm_drained(&self) -> io::Result<()> {
        self.lease.confirm_drained()
    }
    /// Stores the reading of the job started at `job` as the last one.
    fn retain(&self, snapshot: &Snapshot, job: Instant) {
        self.newest_job.set(Some(job));
        *self.last.borrow_mut() = Some((snapshot.clone(), Instant::now()));
    }
    /// Whether a job started after `job` already stored its reading.
    fn superseded(&self, job: Instant) -> bool {
        self.newest_job.get().is_some_and(|newest| newest > job)
    }
}

#[cfg(feature = "service-thermal-mutation-v1")]
pub(super) struct MutationFactories {
    pub(super) read: Box<dyn Fn(Arc<dyn ReadCustody>) -> Worker<ReadAdmission>>,
    pub(super) write: Box<
        dyn Fn(Arc<dyn crate::discovery::performance::mutation::Permit>) -> Worker<ReadAdmission>,
    >,
    /// Quarantines IDs in the transaction pool the workers above share.
    pub(super) quarantine: Box<super::transaction_seed::Quarantine<'static>>,
}

#[cfg(feature = "service-thermal-mutation-v1")]
impl MutationFactories {
    /// The native workers and the one process pool they share.
    fn native() -> Self {
        Self {
            read: Box::new(|custody| native_read_worker(move || custody.authorize())),
            write: Box::new(crate::discovery::performance::mutation::native_mutation_worker),
            quarantine: Box::new(crate::discovery::performance::mutation::quarantine_native),
        }
    }
}

#[cfg(feature = "service-thermal-mutation-v1")]
impl ReadSource {
    /// Adds the mutation lanes. Before any worker job runs, it quarantines
    /// the IDs whose replies, by the journal, an earlier process may have
    /// left in the mailbox (`transaction_seed`). It reads the journal as
    /// release does, authenticating it without recovery, and the keyboard
    /// Driver-mode record, which a recovery-only custody reads too, so
    /// composing a source writes nothing.
    fn with_mutation(mut self, factories: MutationFactories) -> Self {
        #[cfg(feature = "service-keyboard-driver-mode-v1")]
        let driver_mode = || {
            let record = self.lease.driver_mode_record_read()?;
            Ok(record.map(|record| record.ids()))
        };
        #[cfg(not(feature = "service-keyboard-driver-mode-v1"))]
        let driver_mode = || Ok(None);
        super::transaction_seed::quarantine_unanswered(
            &|| self.lease.journal_read_release(false),
            &driver_mode,
            &self.fatal,
            &*factories.quarantine,
        );
        self.factories = Some(Rc::new(factories));
        self
    }
}

/// The keyboard Driver-mode owner's confirmed result, recorded as the read
/// job's own 00/84 result is (`driver_mode::Cadence`), so a read job under
/// the same admission publishes it for the interval instead of querying again.
#[cfg(feature = "service-keyboard-driver-mode-v1")]
impl ReadSource {
    pub(super) fn record_driver_mode(
        &self,
        admission: ReadAdmission,
        read: crate::discovery::performance::DeviceModeRead,
        at: Instant,
    ) {
        self.driver_mode.record(admission, read, at);
    }
    /// The mode a read job confirmed under the current read admission within
    /// the interval, `None` when unknown or stale (`driver_mode::Cadence`).
    pub(super) fn driver_mode_confirmed(&self) -> Option<bool> {
        let admission = self.lease.authorize()?;
        self.driver_mode.active(admission, Instant::now())
    }
    /// `driver_mode_confirmed` for a job that started after `since`.
    pub(super) fn driver_mode_confirmed_since(&self, since: Instant) -> Option<bool> {
        let admission = self.lease.authorize()?;
        self.driver_mode
            .active_since(admission, Instant::now(), since)
    }
    /// Whether this source has activated its write lease. Until it has, the
    /// owner custody writes no Driver-mode record (`Store::driver_mode_record_owner`).
    pub(super) fn mutation_activated(&self) -> bool {
        self.activated.get()
    }
    /// Whether this source can never activate a mutation: a recovery-only
    /// lease, whose every write would only stop and rebuild the reader.
    pub(super) fn recovery_only(&self) -> bool {
        !self.activated.get() && self.lease.recovery_only()
    }
    #[cfg(test)]
    pub(super) fn driver_mode_recorded_for_tests(&self) -> bool {
        self.driver_mode.recorded()
    }
    #[cfg(test)]
    pub(super) fn driver_mode_age_for_tests(&self, by: std::time::Duration) {
        self.driver_mode.age(by);
    }
    /// Whether the retained read is a fresh exact-device observation, as the
    /// published status projects it (`decorate_at`): managed
    /// `TryGetVerifiedDevice` for the Driver-mode owner.
    pub(super) fn driver_mode_verified(&self) -> bool {
        self.last
            .borrow()
            .as_ref()
            .is_some_and(|(snapshot, completed)| {
                Instant::now()
                    .checked_duration_since(*completed)
                    .unwrap_or_default()
                    < RETENTION_WINDOW
                    && snapshot.device_verified()
            })
    }
}

/// Whether a write may change the fans, so a retained fan speed pair must
/// not outlive it. A 00/04 keyboard write cannot, and neither can a keyboard
/// lighting job, whose only setters are 03/03, 03/00 and 03/02.
#[cfg(all(
    feature = "service-live-readings-v1",
    feature = "service-thermal-mutation-v1"
))]
fn changes_fans(permit: &dyn crate::discovery::performance::mutation::Permit) -> bool {
    #[cfg(feature = "service-keyboard-driver-mode-v1")]
    if permit.device_mode().is_some() {
        return false;
    }
    #[cfg(feature = "service-lighting-v1")]
    if permit.lighting().is_some() {
        return false;
    }
    let _ = permit;
    true
}

#[cfg(feature = "service-thermal-mutation-v1")]
pub(super) fn project_private_policy(
    value: Snapshot,
    family: crate::service_status::PrivatePolicyFamily,
    policy: impl FnOnce(Snapshot) -> io::Result<Snapshot>,
) -> io::Result<Snapshot> {
    let value = value.private_policy_reconciling(family);
    match policy(value.clone()) {
        Ok(value) => Ok(value),
        Err(error) if super::thermal_mutation::is_fatal_admission(&error) => Err(error),
        Err(_) => Ok(value.private_policy_unavailable(family)),
    }
}

#[cfg(all(test, feature = "service-thermal-mutation-v1"))]
impl ReadSource {
    pub(super) fn injected_mutation(
        lease: Arc<dyn ReadCustody>,
        factories: MutationFactories,
    ) -> Self {
        Self::compose(lease, |custody| (factories.read)(custody)).with_mutation(factories)
    }
}

#[cfg(all(test, feature = "service-fan-mutation-v2"))]
#[test]
fn fan_v2_expected_stop_denies_ordinary_read_without_revoking_original_custody() {
    let live = crate::origin_lifetime::Lifetime::default();
    live.stop();
    let state = AtomicU8::new(0);
    let p = ReadAdmission {
        lifecycle: 1,
        generation: [7; 32],
    };
    assert!(authorize_original(&state, live.verify().map(|_| true), || Ok(()), p).is_none());
    assert_eq!(state.load(Ordering::Acquire), 0);
    assert!(authorize_original(&state, Ok(true), || live.verify(), p).is_none());
    assert_eq!(state.load(Ordering::Acquire), 0);
    live.lose_trust();
    assert!(authorize_original(&state, live.verify().map(|_| true), || Ok(()), p).is_none());
    assert_eq!(state.load(Ordering::Acquire), 1);
}

#[cfg(feature = "service-thermal-mutation-v1")]
impl ReadSource {
    pub(super) fn retain_battery_expiry(&self, expiry: Rc<dyn Fn() -> Option<Instant>>) {
        // A single replaceable callback owns only the fixed public history.
        // Due/expired entries are excluded by the history to avoid a spin loop.
        *self.battery_expiry.borrow_mut() = Some(expiry);
    }
    pub(super) fn retain_battery_policy(
        &self,
        policy: Rc<dyn Fn(Snapshot) -> io::Result<Snapshot>>,
    ) {
        *self.battery_intent.borrow_mut() = Some(policy);
    }
    pub(super) fn retain_policy(&self, policy: Rc<dyn Fn(Snapshot) -> io::Result<Snapshot>>) {
        *self.intent.borrow_mut() = Some(policy);
    }
    /// Publishes the confirmed drift as managed `ExternalChanges`. Every
    /// published status is projected here or through `current_policy`.
    #[cfg(feature = "service-profile-triggers-v1")]
    pub(super) fn with_drift(&self, value: Snapshot) -> io::Result<Snapshot> {
        let external = self
            .drift
            .lock()
            .map_err(|_| io::ErrorKind::Other)?
            .external();
        Ok(value.with_external_changes(external))
    }
    pub(super) fn retain_mutation(&self, snapshot: &Snapshot) {
        if snapshot.has_fresh_mailbox_read() {
            self.retain(snapshot, Instant::now());
        }
    }
    pub(super) fn custody(&self) -> Arc<dyn ReadCustody> {
        self.lease.clone()
    }
    pub(super) fn current_policy(&self, value: Snapshot) -> io::Result<Snapshot> {
        self.lease.verify_integrity()?;
        let value = project_private_policy(
            value,
            crate::service_status::PrivatePolicyFamily::Thermal,
            |base| {
                self.intent
                    .borrow()
                    .as_ref()
                    .map_or(Ok(base.clone()), |policy| policy(base))
            },
        )?;
        let value = project_private_policy(
            value,
            crate::service_status::PrivatePolicyFamily::Battery,
            |base| {
                self.battery_intent
                    .borrow()
                    .as_ref()
                    .map_or(Ok(base.clone()), |policy| policy(base))
            },
        )?;
        #[cfg(feature = "service-profile-triggers-v1")]
        let value = self.with_drift(value)?;
        #[cfg(feature = "service-performance-shortcut-v1")]
        let value = value.with_shortcut_preview(self.shortcut_preview.borrow().as_ref());
        Ok(value)
    }
    /// Managed `SetPerformanceShortcutPreviewAsync` over the held preview:
    /// a clear removes only the session's own generation, and a preview
    /// replaces any but a newer one of the same session. Returns managed's
    /// reply and whether the preview changed. It takes no operation gate
    /// and reads or writes no firmware.
    #[cfg(feature = "service-performance-shortcut-v1")]
    pub(super) fn set_shortcut_preview(
        &self,
        session_id: &str,
        generation: i64,
        preset: Option<i32>,
    ) -> (&'static str, bool) {
        const NEWER: &str = "A newer performance shortcut preview is already active.";
        // Managed compares GUID values, so the spelling's case is not part
        // of the session.
        let session_id = session_id.to_ascii_lowercase();
        let mut held = self.shortcut_preview.borrow_mut();
        let Some(preset) = preset else {
            if held
                .as_ref()
                .is_some_and(|held| held.session_id == session_id && held.generation == generation)
            {
                *held = None;
                return ("Performance shortcut preview was cleared.", true);
            }
            return (NEWER, false);
        };
        if held
            .as_ref()
            .is_some_and(|held| held.session_id == session_id && held.generation > generation)
        {
            return (NEWER, false);
        }
        let next = crate::service_status::ShortcutPreview {
            session_id,
            generation,
            preset,
        };
        let changed = held.as_ref() != Some(&next);
        *held = Some(next);
        ("Performance shortcut preview was published.", changed)
    }
    async fn drain_mutation(&self, reconstruct: bool) -> io::Result<()> {
        let active = self.mutation.borrow().clone();
        let mut settlement_error = None;
        if let Some((worker, permit)) = active {
            // Only positive callback/worker retirement clears active I/O.
            if let Err(error) = worker.stop().await {
                permit.failed(&error);
                return Err(error);
            }
            self.mutation.borrow_mut().take();
            self.phase.set(MutationPhase::ReaderDrain);
            let settlement = self.lease.verify_integrity().and_then(|_| permit.retired());
            match settlement {
                Ok(crate::discovery::performance::mutation::Settlement::Complete) => {}
                Ok(crate::discovery::performance::mutation::Settlement::Unresolved(error)) => {
                    *self.unsettled.borrow_mut() = Some(permit);
                    settlement_error = Some(error);
                }
                Err(error) => {
                    *self.unsettled.borrow_mut() = Some(permit);
                    self.phase.set(MutationPhase::Fenced);
                    return Err(error);
                }
            }
        }
        if reconstruct && self.phase.get() == MutationPhase::Fenced {
            return Err(deny());
        }
        if self.phase.get() == MutationPhase::ReaderDrain {
            let reader = self.worker.borrow().clone();
            reader.stop().await?;
            if reconstruct {
                if let Err(error) = self.lease.verify_integrity() {
                    self.phase.set(MutationPhase::Fenced);
                    return Err(error);
                }
                self.lease.authorize().ok_or_else(deny)?;
                let factory = self.factories.as_ref().ok_or_else(deny)?;
                *self.worker.borrow_mut() = (factory.read)(self.lease.clone());
                self.phase.set(MutationPhase::Idle);
            }
        }
        settlement_error.map_or(Ok(()), |error| {
            Err(io::Error::other(UnresolvedSettlement(error)))
        })
    }
    /// Stops the reader so a mutation or its caller can own the HID exchange.
    /// Every stop other than the terminal `ReadSource::stop` goes through here.
    ///
    /// The retained `ReaderDrain` phase is the single restart point: the next
    /// observation or mutation reconstructs the reader in `drain_mutation`
    /// after the stop is confirmed and integrity and read custody are freshly
    /// admitted. A caller that then refuses or fails before submitting anything
    /// therefore never leaves reads stopped. The phase is set before the await,
    /// so a canceled stop is still resumed by the next read. A fenced source
    /// stays fenced: its activation may have been published.
    async fn stop_reader(&self) -> io::Result<()> {
        if self.phase.get() != MutationPhase::Fenced {
            self.phase.set(MutationPhase::ReaderDrain);
        }
        let reader = self.worker.borrow().clone();
        reader.stop().await
    }
    pub(super) async fn mutate_cleanup(
        &self,
        permit: Arc<dyn crate::discovery::performance::mutation::Permit>,
        deadline: Instant,
    ) -> io::Result<Option<crate::discovery::performance::ObservedPerformance>> {
        self.mutate_cleanup_in(permit, CleanupScope::OwnedStop, deadline)
            .await
    }
    /// One owned cleanup job under `scope`: the permit's admission must be
    /// the custody's cleanup admission of that scope, so a low-power job
    /// runs only while its suspend generation is current.
    pub(super) async fn mutate_cleanup_in(
        &self,
        permit: Arc<dyn crate::discovery::performance::mutation::Permit>,
        scope: CleanupScope,
        deadline: Instant,
    ) -> io::Result<Option<crate::discovery::performance::ObservedPerformance>> {
        let _serial = self.operation.lock().await;
        // No retained fan pair outlives a write that may change the fans; a
        // 00/04 keyboard write cannot (`changes_fans`).
        #[cfg(feature = "service-live-readings-v1")]
        if changes_fans(&*permit) {
            self.live.mutating();
        }
        self.lease.verify_integrity()?;
        self.drain_mutation(false).await?;
        self.stop_reader().await?;
        if !self.activated.get()
            || !permit.cleanup()
            || permit.authorize().is_none()
            || permit.authorize() != self.lease.authorize_cleanup(scope)
        {
            return Err(deny());
        }
        let factory = self.factories.as_ref().ok_or_else(deny)?;
        permit.mutation_registered();
        let worker = (factory.write)(permit.clone());
        *self.mutation.borrow_mut() = Some((worker.clone(), permit.clone()));
        self.phase.set(MutationPhase::Active);
        let result = if permit.is_battery() {
            worker
                .observe_with_budget(deadline, None, std::time::Duration::from_secs(6))
                .await
        } else {
            worker.observe(deadline, None).await
        };
        if let Err(error) = &result {
            permit.failed(error);
        }
        let drained = self.drain_mutation(false).await;
        // As in `mutate`: the unconfirmed stop that follows a failed job must
        // not erase the job's own fatal admission evidence.
        match result {
            Err(error) if super::thermal_mutation::is_fatal_admission(&error) => Err(error),
            result => {
                drained?;
                result.map(|r| r.map(|r| r.performance))
            }
        }
    }
    /// Joins active mutation work and stops the reader. The next read restarts
    /// it (`stop_reader`), so the caller may return without a mutation.
    pub(super) async fn drain_active(&self) -> io::Result<()> {
        let _serial = self.operation.lock().await;
        self.drain_mutation(false).await?;
        self.stop_reader().await
    }
    pub(super) async fn mutate(
        &self,
        permit: Arc<dyn crate::discovery::performance::mutation::Permit>,
        deadline: Instant,
    ) -> io::Result<Option<crate::discovery::performance::ObservedPerformance>> {
        let factory = self.factories.as_ref().ok_or_else(deny)?;
        let _serial = self.operation.lock().await;
        // No retained fan pair outlives a write that may change the fans; a
        // 00/04 keyboard write cannot (`changes_fans`).
        #[cfg(feature = "service-live-readings-v1")]
        if changes_fans(&*permit) {
            self.live.mutating();
        }
        #[cfg(test)]
        if self.registration_pause.get() != 0 {
            self.registration_pause.set(2);
            while self.registration_pause.get() != 0 {
                tokio::time::sleep(std::time::Duration::from_millis(1)).await;
            }
        }
        self.lease.verify_integrity()?;
        self.drain_mutation(true).await?;
        // `stop_reader` retains its phase before the await. Cancellation or
        // suspend cannot lose the proof that no owner publication was attempted.
        self.stop_reader().await?;
        if permit.authorize() != self.lease.authorize() || permit.authorize().is_none() {
            return Err(deny());
        }
        if !self.activated.get() {
            self.phase.set(MutationPhase::Fenced);
            if let Err(failure) = self.lease.activate_mutation() {
                if matches!(&failure, ActivationFailure::KnownUnpublished(_)) {
                    self.phase.set(MutationPhase::ReaderDrain);
                }
                return Err(failure.error());
            }
            self.activated.set(true);
        }
        permit.mutation_registered();
        let worker = (factory.write)(permit.clone());
        *self.mutation.borrow_mut() = Some((worker.clone(), permit.clone()));
        self.phase.set(MutationPhase::Active);
        // Every job here, temporary charge included, runs under the recipe
        // budget, so its own deadline is the bound: 6 s for battery, temporary
        // charge, performance and fan requests (`readiness::mutation_budget`)
        // and 4 s for the automatic temporary-charge release. Owned-Stop
        // cleanup runs through `mutate_cleanup` with its own deadline. A keyboard
        // Driver-mode job has its own shorter budget, counted from its start.
        #[cfg(feature = "service-keyboard-driver-mode-v1")]
        let budget = permit.budget().unwrap_or(super::RECIPE_BUDGET);
        #[cfg(not(feature = "service-keyboard-driver-mode-v1"))]
        let budget = super::RECIPE_BUDGET;
        let result = worker.observe_with_budget(deadline, None, budget).await;
        if let Err(error) = &result {
            permit.failed(error);
        }
        let drained = self.drain_mutation(true).await;
        // A generic unconfirmed-stop error must not erase the original typed
        // trust failure. Drain still runs and retains its worker/ownership fence.
        match result {
            Err(error) if super::thermal_mutation::is_fatal_admission(&error) => Err(error),
            result => {
                drained?;
                result.map(|reading| reading.map(|reading| reading.performance))
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::discovery::performance::worker_tests::{
        BlockAt, Held, blocking_worker_with_authority,
    };
    use std::{
        sync::atomic::{AtomicBool, AtomicUsize},
        time::Duration,
    };
    use tokio::time::timeout;

    /// The acquired source seeds the pool its native workers reserve from.
    #[cfg(feature = "service-thermal-mutation-v1")]
    #[test]
    fn native_factories_quarantine_the_native_workers_pool() {
        assert!(
            crate::discovery::performance::mutation::quarantines_native_pool(
                &*MutationFactories::native().quarantine
            )
        );
    }

    fn fixture() -> serde_json::Value {
        serde_json::from_str(include_str!(
            "../../../../../../tests/Fixtures/Parity/managed-mailbox-handoff-v1.json"
        ))
        .unwrap()
    }
    struct InjectedCustody {
        proof: ReadAdmission,
        live: AtomicBool,
        retired: AtomicBool,
        releases: AtomicUsize,
    }
    impl ReadCustody for InjectedCustody {
        fn authorize(&self) -> Option<ReadAdmission> {
            self.live.load(Ordering::Acquire).then_some(self.proof)
        }
        fn revoke(&self) {
            self.live.store(false, Ordering::Release);
        }
        fn retire(&self) -> io::Result<()> {
            self.retired.store(true, Ordering::Release);
            self.releases.fetch_add(1, Ordering::AcqRel);
            Ok(())
        }
        fn confirm_drained(&self) -> io::Result<()> {
            if self.retired.load(Ordering::Acquire) {
                Ok(())
            } else {
                Err(deny())
            }
        }
    }
    fn compose(at: BlockAt) -> (ReadSource, Arc<InjectedCustody>, Held) {
        let fixture = fixture();
        let bytes = |name: &str| fixture[name].as_str().unwrap().as_bytes();
        validate_transfer(
            bytes("mapping"),
            bytes("mapping"),
            bytes("request"),
            bytes("receipt"),
            bytes("run"),
        )
        .unwrap();
        let lease = Arc::new(InjectedCustody {
            proof: ReadAdmission {
                lifecycle: 1,
                generation: Sha256::digest(bytes("mapping")).into(),
            },
            live: AtomicBool::new(true),
            retired: AtomicBool::new(false),
            releases: AtomicUsize::new(0),
        });
        let mut held = None;
        let source = ReadSource::compose(lease.clone(), |custody| {
            let (worker, control) =
                blocking_worker_with_authority(at, None, move || custody.authorize());
            held = Some(control);
            worker
        });
        (source, lease, held.unwrap())
    }
    fn status(access: i32) -> Snapshot {
        Snapshot::observed_read_only(
            0,
            access,
            true,
            false,
            vec!["Razer Synapse".into()],
            Some("controller state is advisory for this read"),
            "Awaiting explicit thermal observation.".into(),
        )
    }
    fn publisher() -> watch::Sender<Arc<Publication>> {
        watch::channel(Arc::new(Publication {
            active: status(0),
            inactive: status(1),
        }))
        .0
    }

    #[tokio::test]
    async fn actual_composed_read_factory_observes_battery_without_full_power_or_mutation_authority()
     {
        let (source, custody, held) = compose(BlockAt::Verify);
        held.release.send(()).unwrap();
        let publisher = publisher();
        let observed = source
            .observe(
                status(0),
                Instant::now() + Duration::from_secs(2),
                publisher.clone(),
            )
            .await
            .unwrap()
            .unwrap();
        assert!(observed.has_fresh_mailbox_read());
        assert!(observed.confirmed_performance().is_none()); // Preserve Agent's stronger meaning.
        let json = serde_json::to_value(&observed).unwrap();
        assert_eq!(json["confirmedState"]["performance"], 2);
        assert_eq!(json["confirmedState"]["fans"], 0);
        assert_eq!(json["activePowerSource"], 1);
        assert_eq!(json["writesBlocked"], true);
        assert!(json["capabilities"].is_null());
        assert!(json["liveReadings"].is_null());
        assert_eq!(held.submitted.load(Ordering::Acquire), 4); // Two getter envelopes and replies, one port.
        let retained = publisher.borrow().active.clone();
        assert!(!retained.has_fresh_mailbox_read());
        assert_eq!(
            serde_json::to_value(retained).unwrap()["confirmedState"],
            json["confirmedState"]
        );
        // Serialization cannot carry the in-process fresh-read witness.
        let decoded: Snapshot = serde_json::from_value(json).unwrap();
        assert!(!decoded.has_fresh_mailbox_read());
        source.stop().await.unwrap();
        source.confirm_drained().unwrap();
        assert_eq!(custody.releases.load(Ordering::Acquire), 1);
    }

    #[tokio::test]
    async fn canceled_caller_and_worker_exit_cannot_release_custody_before_native_retirement() {
        let (source, custody, held) = compose(BlockAt::Retire);
        let publisher = publisher();
        {
            let observed = source.observe(
                status(0),
                Instant::now() + Duration::from_secs(2),
                publisher.clone(),
            );
            tokio::pin!(observed);
            tokio::select! {
                reached = held.entered => reached.unwrap(),
                _ = &mut observed => panic!("caller completed before the held native retirement"),
            }
        }
        assert!(
            timeout(Duration::from_millis(20), source.stop())
                .await
                .is_err()
        );
        assert_eq!(custody.releases.load(Ordering::Acquire), 0);
        assert!(source.confirm_drained().is_err());
        held.release.send(()).unwrap();
        timeout(Duration::from_secs(2), source.stop())
            .await
            .unwrap()
            .unwrap();
        assert_eq!(custody.releases.load(Ordering::Acquire), 1);
        assert!(!publisher.borrow().active.has_fresh_mailbox_read());
        assert!(
            serde_json::to_value(publisher.borrow().active.clone()).unwrap()["confirmedState"]
                .is_null()
        );
    }

    #[tokio::test]
    async fn owner_loss_before_publication_discards_late_success_without_replay() {
        let (source, custody, held) = compose(BlockAt::Retire);
        let publisher = publisher();
        let observed = source.observe(
            status(0),
            Instant::now() + Duration::from_secs(2),
            publisher.clone(),
        );
        tokio::pin!(observed);
        tokio::select! {
            reached = held.entered => reached.unwrap(),
            _ = &mut observed => panic!("read escaped retirement barrier"),
        }
        custody.revoke();
        held.release.send(()).unwrap();
        assert!(observed.await.unwrap().is_none());
        assert!(
            source
                .observe(
                    status(0),
                    Instant::now() + Duration::from_secs(2),
                    publisher.clone()
                )
                .await
                .unwrap()
                .is_none()
        );
        assert_eq!(held.submitted.load(Ordering::Acquire), 4);
        assert!(
            serde_json::to_value(publisher.borrow().active.clone()).unwrap()["confirmedState"]
                .is_null()
        );
        source.stop().await.unwrap();
    }

    #[test]
    fn handoff_receipt_request_original_build_and_epoch_mapping_are_byte_exact() {
        let f = fixture();
        let mapping = f["mapping"].as_str().unwrap().as_bytes();
        let request = f["request"].as_str().unwrap().as_bytes();
        let receipt = f["receipt"].as_str().unwrap().as_bytes();
        let run = f["run"].as_str().unwrap().as_bytes();
        validate_transfer(mapping, mapping, request, receipt, run).unwrap();
        for selected in 0..5 {
            let mut values: Vec<Vec<u8>> = [mapping, mapping, request, receipt, run]
                .iter()
                .map(|v| v.to_vec())
                .collect();
            let index = values[selected].len() / 2;
            values[selected][index] ^= 1;
            assert!(
                validate_transfer(&values[0], &values[1], &values[2], &values[3], &values[4])
                    .is_err()
            );
        }
    }

    #[test]
    fn provisional_startup_denies_temporarily_but_actual_origin_loss_is_sticky() {
        let state = AtomicU8::new(0);
        let proof = ReadAdmission {
            lifecycle: 1,
            generation: [1; 32],
        };
        assert!(
            authorize_original(
                &state,
                Ok(false),
                || panic!("no mapping/read during provisional health"),
                proof
            )
            .is_none()
        );
        assert_eq!(state.load(Ordering::Acquire), 0);
        assert_eq!(
            authorize_original(&state, Ok(true), || Ok(()), proof),
            Some(proof)
        );
        assert!(authorize_original(&state, Err(deny()), || Ok(()), proof).is_none());
        assert!(authorize_original(&state, Ok(true), || Ok(()), proof).is_none());
    }

    /// The low-power admission carries the generation's fence, not the
    /// proof's epoch; without a fence it refuses before origin trust, so a
    /// lost origin is not latched; with one it is `authorize_original`.
    #[cfg(feature = "service-keyboard-driver-mode-v1")]
    #[test]
    fn the_low_power_admission_carries_the_generation_fence() {
        let state = AtomicU8::new(0);
        let proof = ReadAdmission {
            lifecycle: 1,
            generation: [1; 32],
        };
        let fenced = ReadAdmission {
            lifecycle: 9,
            ..proof
        };
        assert_eq!(
            low_power_admission(&state, Ok(true), || Ok(()), Some(9), proof),
            Some(fenced)
        );
        assert!(
            low_power_admission(
                &state,
                Err(deny()),
                || panic!("no mapping without a fence"),
                None,
                proof
            )
            .is_none()
        );
        assert_eq!(state.load(Ordering::Acquire), 0);
        assert!(low_power_admission(&state, Ok(false), || Ok(()), Some(9), proof).is_none());
        assert_eq!(state.load(Ordering::Acquire), 0);
        assert!(low_power_admission(&state, Err(deny()), || Ok(()), Some(9), proof).is_none());
        assert_eq!(state.load(Ordering::Acquire), 1);
        assert!(low_power_admission(&state, Ok(true), || Ok(()), Some(9), proof).is_none());
    }

    #[tokio::test]
    async fn native_completion_merges_current_metadata_before_assigning_its_publication_revision() {
        let (source, _, held) = compose(BlockAt::Retire);
        let publisher = publisher();
        let observed = source.observe(
            status(0),
            Instant::now() + Duration::from_secs(2),
            publisher.clone(),
        );
        tokio::pin!(observed);
        tokio::select! {
            reached = held.entered => reached.unwrap(),
            _ = &mut observed => panic!("read escaped retirement barrier"),
        }
        let metadata = |access| {
            Snapshot::observed_read_only(
                7,
                access,
                false,
                false,
                vec!["new-controller".into()],
                None,
                "New metadata".into(),
            )
        };
        publisher.send_replace(Arc::new(Publication {
            active: metadata(0),
            inactive: metadata(1),
        }));
        let clean_metadata = publisher.borrow().clone();
        source.decorate(&publisher, Some(clean_metadata)).unwrap();
        held.release.send(()).unwrap();
        let fresh = observed.await.unwrap().unwrap();
        let json = serde_json::to_value(&fresh).unwrap();
        let retained = serde_json::to_value(publisher.borrow().active.clone()).unwrap();
        assert_eq!(json["activePowerSource"], 3);
        assert_eq!(
            json["potentialControllers"],
            serde_json::json!(["new-controller"])
        );
        assert_eq!(json["revision"], retained["revision"]);
        assert_eq!(json["activePowerSource"], retained["activePowerSource"]);
        source.stop().await.unwrap();
    }
    #[test]
    fn canonical_v2_selected_transfer_and_terminal_records_match_csharp_and_reject_substitution() {
        let f: serde_json::Value = serde_json::from_str(include_str!(
            "../../../../../../tests/Fixtures/Parity/managed-mailbox-handoff-v2.json"
        ))
        .unwrap();
        let b = |key: &str| f[key].as_str().unwrap().as_bytes();
        let values = [
            b("mapping"),
            b("mapping"),
            b("request"),
            b("receipt"),
            b("run"),
            b("selection"),
            b("originalExit"),
        ];
        validate_selected_transfer(
            values[0], values[1], values[2], values[3], values[4], values[5], values[6],
        )
        .unwrap();
        for selected in 0..values.len() {
            let mut changed = values.map(|v| v.to_vec());
            let index = changed[selected].len() / 2;
            changed[selected][index] ^= 1;
            assert!(
                validate_selected_transfer(
                    &changed[0],
                    &changed[1],
                    &changed[2],
                    &changed[3],
                    &changed[4],
                    &changed[5],
                    &changed[6]
                )
                .is_err()
            );
        }
        for bad in f["rejectedTransfers"].as_array().unwrap() {
            let b = |key: &str| bad[key].as_str().unwrap().as_bytes();
            assert!(
                validate_selected_transfer(
                    b("mapping"),
                    b("mapping"),
                    b("request"),
                    b("receipt"),
                    b("run"),
                    b("selection"),
                    b("originalExit")
                )
                .is_err()
            );
        }
        let terminal = lines(b("terminal"), "OpenBlade.ManagedMailboxTerminal.v2", 6).unwrap();
        assert_eq!(terminal[0], digest(b("selection")));
        assert_eq!(terminal[1], digest(b("receipt")));
        assert_eq!(terminal[2], digest(b("originalExit")));
        assert_eq!(terminal[3], "consumed-and-retired");
        assert_eq!(terminal[4], digest(b("mapping")));
        assert_eq!(terminal[5], digest(b("nativeProof")));
    }

    #[tokio::test]
    async fn worker_error_revokes_read_projection_without_retiring_or_releasing_custody() {
        let custody = Arc::new(InjectedCustody {
            proof: ReadAdmission {
                lifecycle: 1,
                generation: [7; 32],
            },
            live: true.into(),
            retired: false.into(),
            releases: 0.into(),
        });
        let source = ReadSource::compose(custody.clone(), |_| {
            crate::discovery::performance::worker_tests::failing_worker()
        });
        let publisher = publisher();
        assert!(
            source
                .observe(
                    status(0),
                    Instant::now() + Duration::from_secs(2),
                    publisher.clone()
                )
                .await
                .unwrap()
                .is_none()
        );
        assert!(custody.authorize().is_none());
        source.decorate(&publisher, None).unwrap();
        let value = serde_json::to_value(&publisher.borrow().active).unwrap();
        assert!(value["message"].as_str().unwrap().contains("revoked"));
        assert!(value["confirmedState"].is_null());
        assert!(source.stop().await.is_err());
        assert!(source.confirm_drained().is_err());
        assert_eq!(custody.releases.load(Ordering::Acquire), 0);
    }
    #[tokio::test]
    async fn installed_custom_reads_outlast_the_old_budget_then_expire_without_timestamp_renewal() {
        use crate::discovery::performance::worker_tests::{CustomIo, custom_io_worker};
        let io = CustomIo::new();
        let custody = Arc::new(InjectedCustody {
            proof: ReadAdmission {
                lifecycle: 1,
                generation: [7; 32],
            },
            live: AtomicBool::new(true),
            retired: AtomicBool::new(false),
            releases: AtomicUsize::new(0),
        });
        let source = ReadSource::compose(custody.clone(), |lease| {
            custom_io_worker(io.clone(), move || lease.authorize())
        });
        let publisher = publisher();
        // Four IDs a read: the unreclaimed pool refused the 64th read's levels.
        for _ in 0..70 {
            let observed = source
                .observe(
                    status(0),
                    Instant::now() + Duration::from_secs(2),
                    publisher.clone(),
                )
                .await
                .unwrap()
                .unwrap();
            let json = serde_json::to_value(observed).unwrap();
            assert_eq!(
                json["confirmedState"]["customPerformance"],
                serde_json::json!({"cpu":1,"gpu":2,"maximumFans":false})
            );
            assert_eq!(json["confirmedState"]["performance"], 5);
        }
        assert_eq!(io.getters.load(Ordering::Acquire), 280);
        let completed = source.last.borrow().as_ref().unwrap().1;
        // Unanswered exchanges can still quarantine every ID; then no read
        // sends anything or renews the result.
        io.quarantine_free_ids();
        for _ in 0..2 {
            assert!(
                source
                    .observe(
                        status(0),
                        Instant::now() + Duration::from_secs(2),
                        publisher.clone()
                    )
                    .await
                    .unwrap()
                    .is_none()
            );
            assert_eq!(io.getters.load(Ordering::Acquire), 280);
            assert_eq!(source.last.borrow().as_ref().unwrap().1, completed);
        }
        source
            .decorate_at(
                &publisher,
                None,
                completed + RETENTION_WINDOW - Duration::from_millis(1),
            )
            .unwrap();
        assert!(!publisher.borrow().active.metadata_only());
        source
            .decorate_at(&publisher, None, completed + RETENTION_WINDOW)
            .unwrap();
        assert!(publisher.borrow().active.metadata_only());
        assert_eq!(io.retirements.load(Ordering::Acquire), 72);
        source.stop().await.unwrap();
        source.confirm_drained().unwrap();
        assert_eq!(custody.releases.load(Ordering::Acquire), 1);
    }

    #[tokio::test]
    async fn installed_custom_manual_high_high_replaces_old_levels_automatic_rejects() {
        use crate::discovery::performance::worker_tests::{CustomIo, custom_io_worker};
        let io = CustomIo::new();
        let custody = Arc::new(InjectedCustody {
            proof: ReadAdmission {
                lifecycle: 1,
                generation: [7; 32],
            },
            live: AtomicBool::new(true),
            retired: AtomicBool::new(false),
            releases: AtomicUsize::new(0),
        });
        let source = ReadSource::compose(custody.clone(), |lease| {
            custom_io_worker(io.clone(), move || lease.authorize())
        });
        let publisher = publisher();
        let deadline = || Instant::now() + Duration::from_secs(2);
        assert!(
            source
                .observe(status(0), deadline(), publisher.clone())
                .await
                .unwrap()
                .is_some()
        );
        io.cpu.store(2, Ordering::Release);
        io.fan.store(1, Ordering::Release);
        let observed = source
            .observe(status(0), deadline(), publisher.clone())
            .await
            .unwrap()
            .unwrap();
        let json = serde_json::to_value(observed).unwrap();
        assert_eq!(json["confirmedState"]["performance"], 5);
        assert_eq!(json["confirmedState"]["fans"], 3);
        assert!(json["confirmedState"]["customPerformance"].is_null());
        assert!(serde_json::to_value(publisher.borrow().active.clone()).unwrap()["confirmedState"]["customPerformance"].is_null());
        let completed = source.last.borrow().as_ref().unwrap().1;
        io.fan.store(0, Ordering::Release);
        assert!(
            source
                .observe(status(0), deadline(), publisher.clone())
                .await
                .unwrap()
                .is_none()
        );
        assert_eq!(source.last.borrow().as_ref().unwrap().1, completed);
        assert!(serde_json::to_value(publisher.borrow().active.clone()).unwrap()["confirmedState"]["customPerformance"].is_null());
        source
            .decorate_at(&publisher, None, completed + RETENTION_WINDOW)
            .unwrap();
        assert!(publisher.borrow().active.metadata_only());
        source.stop().await.unwrap();
    }
    #[tokio::test]
    async fn installed_custom_gpu_possible_submission_keeps_custody_until_native_retirement() {
        use crate::discovery::performance::worker_tests::blocking_custom_worker_with_authority;
        let custody = Arc::new(InjectedCustody {
            proof: ReadAdmission {
                lifecycle: 1,
                generation: [7; 32],
            },
            live: AtomicBool::new(true),
            retired: AtomicBool::new(false),
            releases: AtomicUsize::new(0),
        });
        let mut held = None;
        let source = ReadSource::compose(custody.clone(), |lease| {
            let (worker, control) =
                blocking_custom_worker_with_authority(BlockAt::GpuSubmit, move || {
                    lease.authorize()
                });
            held = Some(control);
            worker
        });
        let held = held.unwrap();
        let publisher = publisher();
        {
            let observed = source.observe(
                status(0),
                Instant::now() + Duration::from_secs(2),
                publisher.clone(),
            );
            tokio::pin!(observed);
            tokio::select! {entered=held.entered=>entered.unwrap(),_=&mut observed=>panic!("escaped pending GPU submit")}
            assert_eq!(held.submitted.load(Ordering::Acquire), 7); // CPU succeeded; GPU SET may have submitted.
            // A second queued whole-observation cannot claim the occupied Reader.
            assert!(
                timeout(
                    Duration::from_secs(2),
                    source.observe(
                        status(0),
                        Instant::now() + Duration::from_secs(2),
                        publisher.clone()
                    )
                )
                .await
                .expect("overlapping observation must refuse without waiting for native retirement")
                .unwrap()
                .is_none()
            );
            assert_eq!(held.submitted.load(Ordering::Acquire), 7);
        }
        assert!(
            timeout(Duration::from_millis(20), source.stop())
                .await
                .is_err()
        );
        assert_eq!(custody.releases.load(Ordering::Acquire), 0);
        assert!(source.confirm_drained().is_err());
        assert!(publisher.borrow().active.metadata_only());
        assert!(source.last.borrow().is_none());
        held.release.send(()).unwrap();
        timeout(Duration::from_secs(2), source.stop())
            .await
            .unwrap()
            .unwrap();
        assert!(held.cancelled.load(Ordering::Acquire));
        assert_eq!(held.submitted.load(Ordering::Acquire), 7);
        assert_eq!(custody.releases.load(Ordering::Acquire), 1);
        source.confirm_drained().unwrap();
        assert!(publisher.borrow().active.metadata_only());
    }
}

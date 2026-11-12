//! Concrete read-only transfer. No firmware owner history or mutation authority.
use super::{Observation, Snapshot, subscription::Publication};
use crate::{
    discovery::performance::{ReadAdmission, Worker, native_read_worker},
    loaded_origin::Origin,
    native_package::{create_attempt_record, digest, encode, encode64, lines},
    windows_protected_path::{self as paths, Policy},
};
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

pub(super) trait ReadCustody: Send + Sync {
    fn authorize(&self) -> Option<ReadAdmission>;
    fn revoke(&self);
    fn retire(&self) -> io::Result<()>;
    fn confirm_drained(&self) -> io::Result<()>;
    #[cfg(feature = "service-thermal-mutation-v1")]
    fn activate_mutation(&self) -> Result<(), ActivationFailure> {
        Err(ActivationFailure::KnownUnpublished(deny()))
    }
    #[cfg(feature = "service-thermal-mutation-v1")]
    fn journal_read(&self, _: bool) -> io::Result<Option<Vec<u8>>> {
        Err(deny())
    }
    #[cfg(feature = "service-thermal-mutation-v1")]
    fn journal_replace(&self, _: Option<&[u8]>, _: &[u8]) -> io::Result<()> {
        Err(deny())
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
fn authorize_original(
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
        Ok(true) if mapping().is_ok() => Some(proof),
        _ => {
            state.store(1, Ordering::Release);
            None
        }
    }
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

// Last getter completion owns this monotonic age; status refresh never renews it.
const RETENTION_WINDOW: std::time::Duration = std::time::Duration::from_secs(5);

#[derive(Clone)]
pub(super) struct ReadSource {
    #[cfg(feature = "service-thermal-mutation-v1")]
    factories: Option<Rc<MutationFactories>>,
    lease: Arc<dyn ReadCustody>,
    worker: Rc<RefCell<Worker<ReadAdmission>>>,
    #[cfg(feature = "service-thermal-mutation-v1")]
    mutation: Rc<
        RefCell<
            Option<(
                Worker<ReadAdmission>,
                Arc<dyn crate::discovery::performance::mutation::Permit>,
            )>,
        >,
    >,
    #[cfg(feature = "service-thermal-mutation-v1")]
    activated: Rc<std::cell::Cell<bool>>,
    #[cfg(feature = "service-thermal-mutation-v1")]
    operation: Rc<tokio::sync::Mutex<()>>,
    #[cfg(feature = "service-thermal-mutation-v1")]
    intent: Rc<RefCell<Option<Rc<dyn Fn(Snapshot) -> io::Result<Snapshot>>>>>,
    last: Rc<RefCell<Option<(Snapshot, Instant)>>>,
    metadata: Rc<RefCell<Option<Arc<Publication>>>>,
}
impl ReadSource {
    pub(super) fn acquire(origin: Arc<Origin>) -> io::Result<Self> {
        let lease = ReadLease::acquire(origin)?;
        let source = Self::compose(lease, |custody| {
            native_read_worker(move || custody.authorize())
        });
        #[cfg(feature = "service-thermal-mutation-v1")]
        let source = {
            let mut source = source;
            source.factories = Some(Rc::new(MutationFactories {
                read: Box::new(|custody| native_read_worker(move || custody.authorize())),
                write: Box::new(crate::discovery::performance::mutation::native_mutation_worker),
            }));
            source
        };
        Ok(source)
    }
    pub(super) fn compose(
        lease: Arc<dyn ReadCustody>,
        factory: impl FnOnce(Arc<dyn ReadCustody>) -> Worker<ReadAdmission>,
    ) -> Self {
        let worker = factory(lease.clone());
        Self {
            lease,
            #[cfg(feature = "service-thermal-mutation-v1")]
            factories: None,
            worker: Rc::new(RefCell::new(worker)),
            #[cfg(feature = "service-thermal-mutation-v1")]
            mutation: Rc::default(),
            #[cfg(feature = "service-thermal-mutation-v1")]
            activated: Rc::default(),
            #[cfg(feature = "service-thermal-mutation-v1")]
            operation: Rc::default(),
            #[cfg(feature = "service-thermal-mutation-v1")]
            intent: Rc::default(),
            last: Rc::default(),
            metadata: Rc::default(),
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
        let project = |base: &Snapshot| {
            let value = project(base);
            self.intent
                .borrow()
                .as_ref()
                .map_or(Ok(value.clone()), |intent| intent(value.clone()))
                .unwrap_or_else(|_| value.redact_private().read_revoked())
        };
        let mut next = Publication {
            active: project(&metadata.active),
            inactive: project(&metadata.inactive),
        };
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
        self.last
            .borrow()
            .as_ref()
            .map(|(_, at)| *at + RETENTION_WINDOW)
            .filter(|due| *due > Instant::now())
    }
    pub(super) fn observe(
        &self,
        _: Snapshot,
        deadline: Instant,
        publisher: watch::Sender<Arc<Publication>>,
    ) -> Observation {
        let source = self.clone();
        if self.metadata.borrow().is_none() {
            *self.metadata.borrow_mut() = Some(publisher.borrow().clone());
        }
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
            source.drain_mutation().await?;
            let worker = source.worker.borrow().clone();
            let reading = match worker.observe(deadline, None).await {
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
            if source.lease.authorize() != Some(reading.admission) || Instant::now() >= deadline {
                return Ok(None);
            }
            let lease = source.lease.clone();
            let proof = reading.admission;
            // Metadata may refresh while the native worker runs outside the
            // source mutex. Never assign its newer revision to an old snapshot.
            let current = publisher.borrow().active.clone();
            let mut observed = current.with_mailbox_read(
                reading.performance,
                crate::service_status::ReadConfirmation::new(deadline, move || {
                    lease.authorize() == Some(proof)
                }),
            );
            *source.last.borrow_mut() = Some((observed.clone(), Instant::now()));
            source.decorate(&publisher, None)?;
            #[cfg(feature = "service-thermal-mutation-v1")]
            if let Some(policy) = source.intent.borrow().as_ref() {
                observed = policy(observed.clone())
                    .unwrap_or_else(|_| observed.redact_private().read_revoked());
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
        #[cfg(feature = "service-thermal-mutation-v1")]
        self.drain_mutation().await?;
        let worker = self.worker.borrow().clone();
        worker.stop().await?;
        self.lease.retire()
    }
    pub(super) fn confirm_drained(&self) -> io::Result<()> {
        self.lease.confirm_drained()
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
    async fn installed_custom_budget_keeps_63_results_then_expires_without_timestamp_renewal() {
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
        for _ in 0..63 {
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
        assert_eq!(io.getters.load(Ordering::Acquire), 252);
        let completed = source.last.borrow().as_ref().unwrap().1;
        // Pair 127 can read the Custom selector, but cannot admit CPU/GPU IDs.
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
        assert_eq!(io.getters.load(Ordering::Acquire), 254);
        assert_eq!(source.last.borrow().as_ref().unwrap().1, completed);
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
        assert_eq!(io.getters.load(Ordering::Acquire), 254);
        assert_eq!(source.last.borrow().as_ref().unwrap().1, completed);
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
        assert_eq!(io.retirements.load(Ordering::Acquire), 65);
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

#[cfg(feature = "service-thermal-mutation-v1")]
impl ReadSource {
    pub(super) fn retain_policy(&self, policy: Rc<dyn Fn(Snapshot) -> io::Result<Snapshot>>) {
        *self.intent.borrow_mut() = Some(policy);
    }
    pub(super) fn retain_mutation(&self, snapshot: &Snapshot) {
        if snapshot.has_fresh_mailbox_read() {
            *self.last.borrow_mut() = Some((snapshot.clone(), Instant::now()));
        }
    }
    pub(super) fn custody(&self) -> Arc<dyn ReadCustody> {
        self.lease.clone()
    }
    async fn drain_mutation(&self) -> io::Result<()> {
        let active = self.mutation.borrow().clone();
        if let Some((worker, permit)) = active {
            worker.stop().await?;
            permit.retired()?;
            self.mutation.borrow_mut().take();
            let custody = self.lease.clone();
            let factory = self.factories.as_ref().ok_or_else(deny)?;
            *self.worker.borrow_mut() = (factory.read)(custody);
        }
        Ok(())
    }
    pub(super) async fn mutate(
        &self,
        permit: Arc<dyn crate::discovery::performance::mutation::Permit>,
        deadline: Instant,
    ) -> io::Result<Option<crate::discovery::performance::ObservedPerformance>> {
        let factory = self.factories.as_ref().ok_or_else(deny)?;
        let _serial = self.operation.lock().await;
        self.drain_mutation().await?;
        let reader = self.worker.borrow().clone();
        reader.stop().await?; // Includes actual native I/O and callback retirement.
        if permit.authorize() != self.lease.authorize() || permit.authorize().is_none() {
            // The reader is genuinely drained and no owner publication was attempted.
            if self.lease.authorize().is_some() {
                *self.worker.borrow_mut() = (factory.read)(self.lease.clone());
            }
            return Err(deny());
        }
        if !self.activated.get() {
            if let Err(failure) = self.lease.activate_mutation() {
                if matches!(&failure, ActivationFailure::KnownUnpublished(_))
                    && self.lease.authorize().is_some()
                {
                    *self.worker.borrow_mut() = (factory.read)(self.lease.clone());
                }
                return Err(failure.error());
            } // SAME held Store; Reserved then Active flush/readback.
            self.activated.set(true);
        }
        let worker = (factory.write)(permit.clone());
        *self.mutation.borrow_mut() = Some((worker.clone(), permit));
        let result = worker.observe(deadline, None).await;
        // Dropping this future retains the worker in self.mutation. A later
        // request/shutdown drains exactly that worker before any replacement.
        self.drain_mutation().await?;
        result.map(|reading| reading.map(|reading| reading.performance))
    }
}

#[cfg(feature = "service-thermal-mutation-v1")]
pub(super) struct MutationFactories {
    pub(super) read: Box<dyn Fn(Arc<dyn ReadCustody>) -> Worker<ReadAdmission>>,
    pub(super) write: Box<
        dyn Fn(Arc<dyn crate::discovery::performance::mutation::Permit>) -> Worker<ReadAdmission>,
    >,
}
#[cfg(all(test, feature = "service-thermal-mutation-v1"))]
impl ReadSource {
    pub(super) fn injected_mutation(
        lease: Arc<dyn ReadCustody>,
        factories: MutationFactories,
    ) -> Self {
        let worker = (factories.read)(lease.clone());
        let mut source = Self::compose(lease, |_| worker);
        source.factories = Some(Rc::new(factories));
        source
    }
}

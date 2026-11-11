//! Durable ownership history outside replaceable epochs. Process exit, reboot,
//! lock abandonment and SCM STOPPED never create a Retired record.
#![allow(dead_code)]
use super::{Authority, AuthorityEpoch, OwnerLease, Ownership};
use crate::discovery::performance::Admission;
use crate::native_owner_record::{Phase, Record, deny, digest, hex, require_transition};
use crate::{
    loaded_origin::Origin,
    windows_protected_path::{self as paths, Policy},
};
use std::{
    ffi::OsStr,
    fs::File,
    io::{self, Read, Seek, Write},
    path::PathBuf,
    sync::{
        Arc, Mutex,
        atomic::{AtomicBool, Ordering},
    },
};
use windows_sys::{
    Wdk::Storage::FileSystem::{
        FILE_CREATE, FILE_NON_DIRECTORY_FILE, FILE_OPEN, FILE_OPEN_REPARSE_POINT,
    },
    Win32::Storage::FileSystem::{FILE_GENERIC_READ, FILE_GENERIC_WRITE},
};

struct Store {
    root: File,
    _ancestors: Vec<File>,
    lock: File,
    path: PathBuf,
    last: Option<Record>,
}
impl Store {
    fn open() -> io::Result<Self> {
        let path = crate::service_install_identity::known_folder(
            &windows_sys::Win32::UI::Shell::FOLDERID_ProgramData,
        )?
        .join("OpenBlade.NativeEpochs")
        .join("owner");
        let (root, ancestors, _) = paths::pin_root(&path)?;
        paths::require_ntfs(&root)?;
        for (i, a) in ancestors.iter().enumerate() {
            if i + 1 == ancestors.len() {
                paths::verify_descriptor(a, paths::accepts_public_native_root)?;
            } else {
                paths::verify_security(
                    a,
                    if i + 2 == ancestors.len() {
                        Policy::PROGRAM_DATA
                    } else {
                        Policy::ANCESTOR
                    },
                )?;
            }
        }
        paths::verify_security(&root, Policy::ROOT)?;
        let lock = open(&root, "writer.lock", false)?;
        let mut result = Self {
            root,
            _ancestors: ancestors,
            lock,
            path,
            last: None,
        };
        let mut entries = std::fs::read_dir(&result.path)?
            .map(|e| e.map(|e| e.file_name()))
            .collect::<io::Result<Vec<_>>>()?;
        if entries.len() > 4096 {
            return Err(deny());
        }
        entries.sort();
        let mut previous: Option<Record> = None;
        for entry in entries {
            let name = entry.to_str().ok_or_else(deny)?;
            if name == "writer.lock" {
                continue;
            }
            let mut held = open(&result.root, name, false)?;
            let bytes = bounded(&mut held)?;
            let record = Record::parse(&bytes)?;
            if name != format!("owner-{:020}.bin", record.sequence) {
                return Err(deny());
            }
            if let Some(prior) = &previous {
                if record.sequence != prior.sequence.checked_add(1).ok_or_else(deny)?
                    || record.predecessor != digest(&prior.bytes())
                {
                    return Err(deny());
                }
                require_transition(prior, &record)?;
            } else if record.sequence != 1
                || record.predecessor != "0".repeat(64)
                || record.phase != Phase::Reserved
            {
                return Err(deny());
            }
            previous = Some(record);
        }
        result.last = previous;
        Ok(result)
    }
    fn reserve(&mut self, origin: &Origin, artifact: String, nonce: String) -> io::Result<()> {
        origin.verify()?;
        if !origin.committed()? || !hex(&nonce, 64) {
            return Err(deny());
        }
        let prior = self.last.as_ref().ok_or_else(deny)?;
        // The first native generation has no predecessor certificate: actual
        // coordinated managed handoff is still required and cannot be guessed.
        if prior.phase != Phase::Retired {
            return Err(deny());
        }
        let next = Record {
            phase: Phase::Reserved,
            sequence: prior.sequence.checked_add(1).ok_or_else(deny)?,
            predecessor: digest(&prior.bytes()),
            installation: origin.installation.clone(),
            epoch: origin.epoch.clone(),
            artifact,
            process: format!("{}:{}", origin.pid, origin.created),
            nonce,
        };
        self.publish(next)
    }
    fn advance(&mut self, phase: Phase) -> io::Result<()> {
        let prior = self.last.as_ref().ok_or_else(deny)?;
        let next = Record {
            phase,
            sequence: prior.sequence.checked_add(1).ok_or_else(deny)?,
            predecessor: digest(&prior.bytes()),
            ..prior.clone()
        };
        self.publish(next)
    }
    fn publish(&mut self, next: Record) -> io::Result<()> {
        paths::verify_security(&self.root, Policy::ROOT)?;
        if let Some(prior) = &self.last {
            require_transition(prior, &next)?;
        }
        let name = format!("owner-{:020}.bin", next.sequence);
        let mut file = open(&self.root, &name, true)?;
        let bytes = next.bytes();
        // A partial record is intentionally retained as an unresolved fence.
        file.write_all(&bytes)?;
        file.sync_all()?;
        file.rewind()?;
        if bounded(&mut file)? != bytes {
            return Err(deny());
        }
        self.last = Some(next);
        Ok(())
    }
}

// Observation retains the real global owner serialization boundary. Empty
// history is admitted only with the separate authenticated managed handoff;
// it is never converted into a firmware Retired record or write reservation.
pub(in crate::installed) fn acquire_empty_read_history() -> io::Result<impl Send + Sync> {
    let store = Store::open()?;
    if store.last.is_some() {
        return Err(deny());
    }
    Ok(store)
}
fn open(root: &File, name: &str, create: bool) -> io::Result<File> {
    let file = paths::open_relative(
        root,
        OsStr::new(name),
        FILE_GENERIC_READ | FILE_GENERIC_WRITE,
        0,
        if create { FILE_CREATE } else { FILE_OPEN },
        FILE_NON_DIRECTORY_FILE | FILE_OPEN_REPARSE_POINT,
    )?;
    paths::check_regular_file(&file)?;
    paths::verify_security(&file, Policy::Record)?;
    Ok(file)
}
fn bounded(file: &mut File) -> io::Result<Vec<u8>> {
    let n = file.metadata()?.len();
    if n == 0 || n > 4096 {
        return Err(deny());
    }
    let mut value = vec![0; n as usize];
    file.read_exact(&mut value)?;
    Ok(value)
}
pub(in crate::installed) struct NativeAuthority {
    origin: Arc<Origin>,
    epoch: AuthorityEpoch,
    revoked: AtomicBool,
    store: Mutex<Option<Store>>,
}
impl NativeAuthority {
    pub(in crate::installed) fn from_origin(origin: Arc<Origin>) -> io::Result<Self> {
        origin.verify()?;
        Ok(Self {
            epoch: AuthorityEpoch {
                instance: Arc::new(()),
                launch_pid: origin.pid,
            },
            origin,
            revoked: AtomicBool::new(false),
            store: Mutex::new(Store::open().ok()),
        })
    }
    pub(in crate::installed) fn admission_denial(&self) -> &'static str {
        "full-adapter/activity/managed-drain suppliers unavailable"
    }
}
impl Authority for NativeAuthority {
    fn loaded_generation(&self) -> io::Result<AuthorityEpoch> {
        self.origin.verify()?;
        if self.revoked.load(Ordering::Acquire) || !self.origin.committed()? {
            return Err(deny());
        }
        Ok(self.epoch.clone())
    }
    fn acquire(&self, epoch: &AuthorityEpoch) -> io::Result<Arc<dyn OwnerLease>> {
        self.loaded_generation()?;
        if *epoch != self.epoch {
            return Err(deny());
        }
        // Concrete durable custody checks precede any future reservation. Missing
        // legacy handoff never becomes a fabricated first Retired record.
        let held = self.store.lock().map_err(|_| deny())?;
        let store = held.as_ref().ok_or_else(deny)?;
        if store
            .last
            .as_ref()
            .is_none_or(|r| r.phase != Phase::Retired)
        {
            return Err(deny());
        }
        // There is no reachable worker construction while the required actual
        // adapter/activity/handoff sources are missing. Never fill Admission.
        self.revoked.store(true, Ordering::Release);
        Err(deny())
    }
}
// Retain concrete storage and origin through an active worker's confirmed drain.
// Construction is private and currently unreachable because admission denies.
struct DurableLease {
    origin: Arc<Origin>,
    epoch: AuthorityEpoch,
    store: Mutex<Option<Store>>,
    revoked: AtomicBool,
    admission: Admission,
}
impl DurableLease {
    fn activate(
        origin: Arc<Origin>,
        epoch: AuthorityEpoch,
        mut store: Store,
        admission: Admission,
    ) -> io::Result<Arc<Self>> {
        origin.verify()?;
        if !origin.committed()?
            || !admission.is_clear()
            || epoch.launch_pid != origin.pid
            || store
                .last
                .as_ref()
                .is_none_or(|r| r.phase != Phase::Reserved || r.epoch != origin.epoch)
        {
            return Err(deny());
        }
        store.advance(Phase::Active)?; // flush+readback before any worker can be reached
        Ok(Arc::new(Self {
            origin,
            epoch,
            store: Mutex::new(Some(store)),
            revoked: AtomicBool::new(false),
            admission,
        }))
    }
}
impl OwnerLease for DurableLease {
    fn observe(&self) -> Ownership {
        // Future suppliers must replace this denial with independently maintained
        // fresh evidence; neither ACLineStatus nor console ID can supply it.
        self.revoked.store(true, Ordering::Release);
        Ownership::Lost
    }
    fn release(&self) -> io::Result<Ownership> {
        // Binding calls this only after the actual firmware worker/callback drain.
        // Do not require active selection: guarded cleanup survives revocation.
        self.revoked.store(true, Ordering::Release);
        let mut held = self.store.lock().map_err(|_| deny())?;
        let store = held.as_mut().ok_or_else(deny)?;
        let prior = store.last.as_ref().ok_or_else(deny)?;
        if prior.epoch != self.origin.epoch
            || prior.process != format!("{}:{}", self.origin.pid, self.origin.created)
        {
            return Err(deny());
        }
        match prior.phase {
            Phase::Active => {
                store.advance(Phase::Retiring)?;
                store.advance(Phase::Retired)?;
            }
            Phase::Retiring => {
                store.advance(Phase::Retired)?;
            }
            Phase::Retired => (),
            Phase::Reserved => return Err(deny()), // never-started reservation retirement stays private
        }
        Ok(Ownership::Retired(self.epoch.clone()))
    }
}
impl Drop for DurableLease {
    fn drop(&mut self) {
        // Unconfirmed retirement quarantines lock, immutable origin and package.
        std::mem::forget(
            self.store
                .get_mut()
                .unwrap_or_else(|e| e.into_inner())
                .take(),
        );
        std::mem::forget(self.origin.clone());
    }
}

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

#[cfg(feature = "service-thermal-mutation-v1")]
#[path = "thermal_history_store.rs"]
mod thermal_history_store;

pub(in crate::installed) struct Store {
    root: File,
    _ancestors: Vec<File>,
    lock: File,
    path: PathBuf,
    last: Option<Record>,
    activated_here: bool,
    activation_started: bool,
    #[cfg(feature = "service-thermal-mutation-v1")]
    active_records: std::collections::BTreeMap<String, Record>,
    #[cfg(feature = "service-thermal-mutation-v1")]
    battery_producers:
        Mutex<std::collections::BTreeMap<String, crate::native_package::HistoricalBatteryProducer>>,
    #[cfg(all(test, feature = "service-thermal-mutation-v1"))]
    pub(in crate::installed) battery_producer_fixture:
        Option<Arc<dyn Fn(&Record, bool) -> io::Result<()> + Send + Sync>>,
    #[cfg(test)]
    verifier: Option<Arc<dyn Fn(&File) -> io::Result<()> + Send + Sync>>,
    #[cfg(test)]
    pub(in crate::installed) history_boundary:
        Option<Arc<dyn Fn(&str) -> io::Result<()> + Send + Sync>>,
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
            activated_here: false,
            activation_started: false,
            #[cfg(feature = "service-thermal-mutation-v1")]
            active_records: Default::default(),
            #[cfg(feature = "service-thermal-mutation-v1")]
            battery_producers: Default::default(),
            #[cfg(all(test, feature = "service-thermal-mutation-v1"))]
            battery_producer_fixture: None,
            #[cfg(test)]
            verifier: None,
            #[cfg(test)]
            history_boundary: None,
        };
        result.load_history()?;
        Ok(result)
    }
    fn load_history(&mut self) -> io::Result<()> {
        let mut entries = Vec::new();
        for entry in std::fs::read_dir(&self.path)? {
            let name = entry?.file_name();
            #[cfg(feature = "service-thermal-mutation-v1")]
            if matches!(
                name.to_str(),
                Some(
                    "thermal-mutation.journal"
                        | "thermal-mutation.journal.pending"
                        | "thermal-mutation.journal.rollover"
                        | "thermal-history"
                )
            ) {
                continue;
            }
            // Preserve the old 4096-entry owner budget including writer.lock;
            // thermal namespace growth never consumes/refills owner capacity.
            if entries.len() == 4096 {
                return Err(deny());
            }
            entries.push(name);
        }
        entries.sort();
        let mut previous: Option<Record> = None;
        for entry in entries {
            let name = entry.to_str().ok_or_else(deny)?;
            if name == "writer.lock" {
                continue;
            }
            // Older compositions must retain their complete-set refusal. Only
            // this composition owns and validates the separate submission log.
            #[cfg(feature = "service-thermal-mutation-v1")]
            if matches!(
                name,
                "thermal-mutation.journal"
                    | "thermal-mutation.journal.pending"
                    | "thermal-mutation.journal.rollover"
                    | "thermal-history"
            ) {
                continue;
            }
            let mut held = self.open_record(name, false)?;
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
            #[cfg(feature = "service-thermal-mutation-v1")]
            if record.phase == Phase::Active
                && self
                    .active_records
                    .insert(record.nonce.clone(), record.clone())
                    .is_some()
            {
                return Err(deny()); // A nonce identifies exactly one authenticated Active owner.
            }
            previous = Some(record);
        }
        self.last = previous;
        Ok(())
    }
    #[cfg(all(test, feature = "service-thermal-mutation-v1"))]
    pub(in crate::installed) fn injected(
        root: File,
        path: PathBuf,
        verify: Arc<dyn Fn(&File) -> io::Result<()> + Send + Sync>,
    ) -> io::Result<Self> {
        verify(&root)?;
        let lock = paths::open_relative(
            &root,
            OsStr::new("writer.lock"),
            FILE_GENERIC_READ | FILE_GENERIC_WRITE,
            0,
            FILE_OPEN,
            FILE_NON_DIRECTORY_FILE | FILE_OPEN_REPARSE_POINT,
        )?;
        paths::check_regular_file(&lock)?;
        verify(&lock)?;
        let mut store = Self {
            root,
            _ancestors: vec![],
            lock,
            path,
            last: None,
            activated_here: false,
            activation_started: false,
            #[cfg(feature = "service-thermal-mutation-v1")]
            active_records: Default::default(),
            #[cfg(feature = "service-thermal-mutation-v1")]
            battery_producers: Default::default(),
            #[cfg(all(test, feature = "service-thermal-mutation-v1"))]
            battery_producer_fixture: None,
            verifier: Some(verify),
            history_boundary: None,
        };
        store.load_history()?;
        Ok(store)
    }
    #[cfg(all(test, feature = "service-thermal-mutation-v1"))]
    pub(in crate::installed) fn activate_injected(&mut self, record: Record) -> io::Result<()> {
        self.activate_bound(record)
    }
    #[cfg(feature = "service-thermal-mutation-v1")]
    pub(in crate::installed) fn activation_started(&self) -> bool {
        self.activation_started
    }
    // Only authenticated ReadLease custody calls this after actual reader drain.
    #[cfg(feature = "service-thermal-mutation-v1")]
    pub(in crate::installed) fn activate_first(
        &mut self,
        origin: &Origin,
        nonce: String,
    ) -> io::Result<()> {
        origin.verify()?;
        let artifact = origin.mutation_artifact()?;
        if !origin.committed()? {
            return Err(deny());
        }
        self.activate_bound(Record {
            phase: Phase::Reserved,
            sequence: 1,
            predecessor: "0".repeat(64),
            installation: origin.installation.clone(),
            epoch: origin.epoch.clone(),
            artifact,
            process: format!("{}:{}", origin.pid, origin.created),
            nonce,
        })
    }
    #[cfg(feature = "service-thermal-mutation-v1")]
    fn activate_bound(&mut self, mut reservation: Record) -> io::Result<()> {
        if self.activated_here
            || reservation.phase != Phase::Reserved
            || !hex(&reservation.nonce, 64)
        {
            return Err(deny());
        }
        if let Some(prior) = &self.last {
            if prior.phase != Phase::Retired {
                return Err(deny());
            }
            reservation.sequence = prior.sequence.checked_add(1).ok_or_else(deny)?;
            reservation.predecessor = digest(&prior.bytes());
        }
        Record::parse(&reservation.bytes())?;
        self.activation_started = true;
        self.publish(reservation)?;
        self.advance(Phase::Active)?;
        self.activated_here = true;
        Ok(())
    }
    #[cfg(feature = "service-thermal-mutation-v1")]
    pub(in crate::installed) fn retire_active(&mut self) -> io::Result<()> {
        if self.activated_here && self.last.as_ref().is_some_and(|r| r.phase == Phase::Active) {
            self.advance(Phase::Retiring)?;
            self.advance(Phase::Retired)?;
        }
        Ok(())
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
    #[cfg(feature = "service-thermal-mutation-v1")]
    pub(in crate::installed) fn verify_journal_custody(&self) -> io::Result<()> {
        let check = || -> io::Result<()> {
            self.check(&self.root, Policy::ROOT)?;
            paths::check_regular_file(&self.lock)?;
            self.check(&self.lock, Policy::Record)?;
            if let Some(last) = &self.last {
                let mut file =
                    self.open_record(&format!("owner-{:020}.bin", last.sequence), false)?;
                if bounded(&mut file)? != last.bytes() {
                    return Err(deny());
                }
            }
            Ok(())
        };
        check().map_err(|_| {
            crate::installed::readiness::thermal_mutation::fatal_admission(
                "Original protected thermal owner custody changed or cannot be authenticated.",
            )
        })
    }
    #[cfg(feature = "service-thermal-mutation-v1")]
    pub(in crate::installed) fn journal_read(&self, pending: bool) -> io::Result<Option<Vec<u8>>> {
        if !pending {
            let view = self.journal_view()?;
            return Ok(view.physical.map(|_| view.tail));
        }
        use crate::thermal_history::Backend;
        thermal_history_store::History(self).read(
            "thermal-mutation.journal.pending",
            crate::thermal_history::SEGMENT + 2048,
        )
    }
    #[cfg(feature = "service-thermal-mutation-v1")]
    pub(in crate::installed) fn journal_replace(
        &self,
        expected: Option<&[u8]>,
        bytes: &[u8],
    ) -> io::Result<()> {
        self.verify_journal_custody()?;
        self.validate_journal_owners(bytes).map_err(|_| {
            crate::installed::readiness::thermal_mutation::fatal_admission(
                "Thermal settlement original owner binding is invalid.",
            )
        })?;
        let view = self.journal_view()?;
        if bytes.len() > 1024 * 1024 || view.tail.as_slice() != expected.unwrap_or_default() {
            return Err(deny());
        }
        let proposed = crate::thermal_history::View {
            physical: None,
            tail: bytes.to_vec(),
            sequence: view.sequence,
            head: view.head.clone(),
        };
        crate::thermal_history::inspect_prefix(
            &thermal_history_store::History(self),
            &proposed,
            |e, r| self.join_row(e, r),
        )?;
        let physical = if view.sequence == 0 {
            bytes.to_vec()
        } else {
            crate::thermal_history::envelope(view.sequence, &view.head, bytes)
        };
        if self.journal_read(false)?.as_deref() != expected {
            return Err(deny());
        }
        let mut file = paths::open_relative(
            &self.root,
            OsStr::new("thermal-mutation.journal.pending"),
            FILE_GENERIC_READ
                | FILE_GENERIC_WRITE
                | windows_sys::Win32::Storage::FileSystem::DELETE,
            0,
            FILE_CREATE,
            FILE_NON_DIRECTORY_FILE | FILE_OPEN_REPARSE_POINT,
        )?;
        paths::check_regular_file(&file)
            .and_then(|_| self.check(&file, Policy::Record))
            .map_err(|_| {
                crate::installed::readiness::thermal_mutation::fatal_admission(
                    "Thermal settlement staging protection is invalid.",
                )
            })?;
        file.write_all(&physical)?;
        file.sync_all()?;
        file.rewind()?;
        let mut check = vec![];
        file.read_to_end(&mut check)?;
        if check != physical || self.journal_read(false)?.as_deref() != expected {
            return Err(deny());
        }
        paths::rename_same_directory(&file, "thermal-mutation.journal", true)?;
        file.sync_all()?;
        drop(file);
        if self.journal_read(false)?.as_deref() != Some(bytes) {
            return Err(deny());
        }
        Ok(())
    }
    #[cfg(feature = "service-thermal-mutation-v1")]
    fn validate_journal_owners(&self, bytes: &[u8]) -> io::Result<()> {
        if bytes.is_empty() || !bytes.ends_with(b"\n") {
            return Err(deny());
        }
        #[derive(serde::Deserialize)]
        struct OwnerBinding {
            owner: String,
        }
        for line in bytes[..bytes.len() - 1].split(|b| *b == b'\n') {
            let entry: OwnerBinding = serde_json::from_slice(line).map_err(|_| deny())?;
            if !self.active_records.contains_key(&entry.owner) {
                return Err(deny());
            }
        }
        Ok(())
    }
    fn check(&self, file: &File, policy: Policy) -> io::Result<()> {
        #[cfg(test)]
        if let Some(verify) = &self.verifier {
            return verify(file);
        }
        paths::verify_security(file, policy)
    }
    fn open_record(&self, name: &str, create: bool) -> io::Result<File> {
        let file = paths::open_relative(
            &self.root,
            OsStr::new(name),
            FILE_GENERIC_READ | FILE_GENERIC_WRITE,
            0,
            if create { FILE_CREATE } else { FILE_OPEN },
            FILE_NON_DIRECTORY_FILE | FILE_OPEN_REPARSE_POINT,
        )?;
        let protection =
            paths::check_regular_file(&file).and_then(|_| self.check(&file, Policy::Record));
        #[cfg(feature = "service-thermal-mutation-v1")]
        let protection = protection.map_err(|_| {
            crate::installed::readiness::thermal_mutation::fatal_admission(
                "Protected owner record identity or protection changed.",
            )
        });
        protection?;
        Ok(file)
    }
    fn publish(&mut self, next: Record) -> io::Result<()> {
        #[cfg(feature = "service-thermal-mutation-v1")]
        if next.phase == Phase::Active && self.active_records.contains_key(&next.nonce) {
            return Err(deny());
        }
        self.check(&self.root, Policy::ROOT)?;
        if let Some(prior) = &self.last {
            require_transition(prior, &next)?;
        }
        let name = format!("owner-{:020}.bin", next.sequence);
        let mut file = self.open_record(&name, true)?;
        let bytes = next.bytes();
        // A partial record is intentionally retained as an unresolved fence.
        file.write_all(&bytes)?;
        file.sync_all()?;
        file.rewind()?;
        if bounded(&mut file)? != bytes {
            return Err(deny());
        }
        #[cfg(feature = "service-thermal-mutation-v1")]
        if next.phase == Phase::Active
            && self
                .active_records
                .insert(next.nonce.clone(), next.clone())
                .is_some()
        {
            return Err(deny());
        }
        self.last = Some(next);
        Ok(())
    }
}

// Observation retains the real global owner serialization boundary. Empty
// history is admitted only with the separate authenticated managed handoff;
// the mutation composition also admits valid history for read-only recovery.
// An unresolved predecessor cannot reserve or be retired by this new process;
// it is never converted into a firmware Retired record or write reservation.
pub(in crate::installed) fn acquire_empty_read_history() -> io::Result<Store> {
    let store = Store::open()?;
    #[cfg(not(feature = "service-thermal-mutation-v1"))]
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

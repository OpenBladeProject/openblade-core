//! The keyboard Driver-mode record in the rig's files, as the owner custody
//! keeps it (`Store::driver_mode_record_read/replace` behind `ReadLease`):
//! strict parsing, a leftover stage refusing reads, and a compare-and-swap
//! replace along the domain's edges, only as this process's own Active
//! owner, under the admission the caller names. It also counts and can fail
//! replaces, and can make each take real time, as slow storage does.
use super::*;
use openblade_domain::driver_mode_history::{self as history, Record};
use std::sync::atomic::AtomicU8;

pub(super) const RECORD: &str = "keyboard-driver-mode.bin";
pub(super) const STAGE: &str = "keyboard-driver-mode.bin.pending";

/// What an injected replace failure leaves behind.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
#[repr(u8)]
pub(super) enum Fault {
    /// Refused before anything was written.
    Refused = 1,
    /// The new record was written but the replace still failed, as a crash
    /// after the rename or a failed verification does.
    Written = 2,
    /// Fatal admission evidence, before anything was written.
    Fatal = 3,
}

#[derive(Default)]
pub(super) struct RecordModel {
    files: StdMutex<Option<MemoryFiles>>,
    /// The Active owner nonce this rig's process writes as.
    owner: StdMutex<String>,
    /// The replace attempt (1-based) that fails with `fault`; 0 never.
    fail_at: AtomicUsize,
    fault: AtomicU8,
    attempts: AtomicUsize,
    /// Every replace attempt: the cleanup scope it held (`None`: the
    /// ordinary admission) and the record it would write.
    writes: StdMutex<Vec<(Option<CleanupScope>, Vec<u8>)>>,
    /// The replace attempt (1-based) that takes this much real time.
    delay: StdMutex<Option<(usize, std::time::Duration)>>,
    /// Runs once after the next record is written, as the caller's next
    /// step would observe the world changed.
    after_write: StdMutex<Option<Box<dyn FnOnce() + Send>>>,
}

impl RecordModel {
    /// The model over `files`, writing as owner `nonce` (one hex digit,
    /// repeated).
    pub(super) fn bind(&self, files: &MemoryFiles, nonce: char) {
        *self.files.lock().unwrap() = Some(files.clone());
        *self.owner.lock().unwrap() = nonce.to_string().repeat(64);
    }

    /// Fails the `nth` replace attempt from now with `fault`.
    pub(super) fn fail(&self, nth: usize, fault: Fault) {
        self.fault.store(fault as u8, Ordering::Release);
        self.fail_at.store(
            self.attempts.load(Ordering::Acquire) + nth,
            Ordering::Release,
        );
    }

    /// Makes the `nth` replace attempt from now take `delay` of real time.
    pub(super) fn delay_at(&self, nth: usize, delay: std::time::Duration) {
        *self.delay.lock().unwrap() = Some((self.attempts.load(Ordering::Acquire) + nth, delay));
    }

    /// Runs `then` once, right after the next record is written.
    pub(super) fn after_write(&self, then: impl FnOnce() + Send + 'static) {
        *self.after_write.lock().unwrap() = Some(Box::new(then));
    }

    /// Every replace attempt so far.
    pub(super) fn writes(&self) -> Vec<(Option<CleanupScope>, Vec<u8>)> {
        self.writes.lock().unwrap().clone()
    }

    fn files(&self) -> io::Result<MemoryFiles> {
        self.files
            .lock()
            .unwrap()
            .clone()
            .ok_or_else(|| io::ErrorKind::PermissionDenied.into())
    }

    pub(super) fn read(&self) -> io::Result<Option<Record>> {
        let files = self.files()?;
        if files.read(STAGE)?.is_some() {
            return Err(io::ErrorKind::PermissionDenied.into());
        }
        files
            .read(RECORD)?
            .map(|bytes| Record::parse(&bytes))
            .transpose()
    }

    /// The writer nonce, refused where the Store has none.
    pub(super) fn owner(&self, writable: bool) -> io::Result<String> {
        if !writable {
            return Err(io::ErrorKind::PermissionDenied.into());
        }
        Ok(self.owner.lock().unwrap().clone())
    }

    /// `Store::driver_mode_record_replace` behind `ReadLease`: refused
    /// unless `writable` (activated here, not recovery-only) and `admitted`
    /// (the caller's custody admission), then as the Store refuses.
    pub(super) fn replace(
        &self,
        writable: bool,
        admitted: bool,
        scope: Option<CleanupScope>,
        expected: Option<&Record>,
        next: &Record,
    ) -> io::Result<()> {
        let attempt = self.attempts.fetch_add(1, Ordering::AcqRel) + 1;
        self.writes.lock().unwrap().push((scope, next.bytes()));
        if let Some((_, delay)) = self.delay.lock().unwrap().take_if(|(at, _)| *at == attempt) {
            std::thread::sleep(delay);
        }
        let fault = (self.fail_at.load(Ordering::Acquire) == attempt)
            .then(|| self.fault.load(Ordering::Acquire));
        if fault == Some(Fault::Fatal as u8) {
            return Err(
                crate::installed::readiness::thermal_mutation::fatal_admission(
                    "injected Driver-mode record custody lost",
                ),
            );
        }
        if fault == Some(Fault::Refused as u8)
            || !writable
            || !admitted
            || next.owner() != self.owner(writable)?
            || !history::follows(expected, next)
            || self.read()?.as_ref() != expected
        {
            return Err(io::ErrorKind::PermissionDenied.into());
        }
        self.files()?
            .bytes
            .lock()
            .unwrap()
            .insert(RECORD.into(), next.bytes());
        if let Some(then) = self.after_write.lock().unwrap().take() {
            then();
        }
        if fault == Some(Fault::Written as u8) {
            return Err(io::ErrorKind::StorageFull.into());
        }
        Ok(())
    }
}

//! Test doubles shared by the display write tests: an in-memory managed
//! checkpoint and, for the CCD writes, the brightness owner and recovery
//! state they run against. No Windows file, WMI or CCD call is used.
use super::recovery::CheckpointFile;
use std::{
    io,
    sync::{
        Arc, Mutex,
        atomic::{AtomicUsize, Ordering},
    },
};

/// Shared bytes survive a simulated Agent restart; writes can be refused.
#[derive(Clone)]
pub(super) struct Memory {
    pub(super) bytes: Arc<Mutex<Option<Vec<u8>>>>,
    /// Successful writes beyond this count fail (persistence fault).
    pub(super) allow: Arc<AtomicUsize>,
    pub(super) writes: Arc<AtomicUsize>,
}

impl Default for Memory {
    fn default() -> Self {
        Self {
            bytes: Arc::default(),
            allow: Arc::new(AtomicUsize::new(usize::MAX)),
            writes: Arc::default(),
        }
    }
}

impl CheckpointFile for Memory {
    fn read(&mut self) -> io::Result<Option<Vec<u8>>> {
        Ok(self.bytes.lock().unwrap().clone())
    }

    fn replace(&mut self, bytes: &[u8]) -> io::Result<()> {
        if self.writes.load(Ordering::SeqCst) >= self.allow.load(Ordering::SeqCst) {
            return Err(io::Error::other("injected checkpoint failure"));
        }
        self.writes.fetch_add(1, Ordering::SeqCst);
        *self.bytes.lock().unwrap() = Some(bytes.to_vec());
        Ok(())
    }
}

impl Memory {
    pub(super) fn with(bytes: &str) -> Self {
        let file = Self::default();
        *file.bytes.lock().unwrap() = Some(bytes.into());
        file
    }

    pub(super) fn record(&self) -> serde_json::Value {
        serde_json::from_slice(self.bytes.lock().unwrap().as_deref().unwrap()).unwrap()
    }

    /// A prior managed legacy checkpoint supplies the last reconciled source.
    pub(super) fn legacy(source: u8) -> Self {
        Self::with(&format!(
            r#"{{"SchemaVersion":1,"LastReconciledPowerSource":{source}}}"#
        ))
    }
}

#[cfg(openblade_display_ccd_write)]
pub(super) use ccd::Rig;

#[cfg(openblade_display_ccd_write)]
mod ccd {
    use super::Memory;
    use crate::session_native::display_query::{
        observation::{BrightnessOwner, SharedBrightness, Snapshot},
        recovery::{Fields, Recovery},
        write::explicit_read,
    };
    use std::sync::{Arc, Mutex};

    /// The durable recovery state and an inert brightness owner for one CCD
    /// write test; a new `Rig` over the same `Memory` is an Agent restart.
    pub(in super::super) struct Rig {
        pub(in super::super) owner: SharedBrightness,
        pub(in super::super) file: Memory,
        pub(in super::super) recovery: Mutex<Recovery<Memory>>,
    }

    impl Rig {
        pub(in super::super) fn new(file: Memory) -> Self {
            Self {
                owner: Arc::new(Mutex::new(BrightnessOwner::inert())),
                recovery: Mutex::new(Recovery::load(file.clone())),
                file,
            }
        }

        pub(in super::super) fn unresolved(&self) -> Fields {
            self.recovery.lock().unwrap().unresolved()
        }

        /// A completed explicit query through the production resume path.
        pub(in super::super) fn explicit_read(&self, state: &Snapshot) {
            explicit_read(&self.owner, &self.recovery, state).unwrap();
        }
    }
}

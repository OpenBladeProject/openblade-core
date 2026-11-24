//! An owner directory on real files in a private directory under the
//! temporary directory. Only the security verifier is injected; every open,
//! rename, sharing and link rule is the system's.
use super::*;
use std::os::windows::fs::OpenOptionsExt;
use windows_sys::Win32::Storage::FileSystem::FILE_FLAG_BACKUP_SEMANTICS;

pub(super) struct OwnerRoot(PathBuf);

impl OwnerRoot {
    pub(super) fn new(tag: &str) -> Self {
        let name = format!("openblade-owner-store-{tag}-{}", std::process::id());
        let root = Self(std::env::temp_dir().join(name));
        let _ = std::fs::remove_dir_all(&root.0);
        std::fs::create_dir_all(&root.0).unwrap();
        std::fs::write(root.path("writer.lock"), b"").unwrap();
        root
    }

    pub(super) fn path(&self, name: &str) -> PathBuf {
        self.0.join(name)
    }

    /// The Store over this directory, as a process that has not activated.
    pub(super) fn store(&self) -> io::Result<Store> {
        self.store_verified(Arc::new(|_| Ok(())))
    }

    pub(super) fn store_verified(
        &self,
        verify: crate::installed::FileVerifier,
    ) -> io::Result<Store> {
        let root = std::fs::OpenOptions::new()
            .read(true)
            .custom_flags(FILE_FLAG_BACKUP_SEMANTICS)
            .open(&self.0)?;
        Store::injected(root, self.0.clone(), verify)
    }

    /// The Store of a process that activated as owner `nonce`.
    pub(super) fn active(&self, nonce: char) -> Store {
        let mut store = self.store().unwrap();
        store.activate_injected(reservation(nonce)).unwrap();
        store
    }
}

impl Drop for OwnerRoot {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}

/// A first or later reservation whose epoch and nonce repeat `nonce`.
pub(super) fn reservation(nonce: char) -> Record {
    Record {
        phase: Phase::Reserved,
        sequence: 1,
        predecessor: "0".repeat(64),
        installation: "1".repeat(32),
        epoch: nonce.to_string().repeat(32),
        artifact: format!("{}-service", "3".repeat(64)),
        process: "17:1".into(),
        nonce: nonce.to_string().repeat(64),
    }
}

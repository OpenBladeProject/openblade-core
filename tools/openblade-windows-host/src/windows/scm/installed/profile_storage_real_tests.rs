//! The profile transaction on real files through the protected store, in a
//! private directory under the temporary directory. The security verifier is
//! injected; every open, rename, deletion and sharing rule is the system's.
use super::migration::{case, migrated};
use super::*;
use crate::installed::readiness::thermal_native::ProtectedFiles;
use std::os::windows::fs::OpenOptionsExt;
use std::path::{Path, PathBuf};
use std::sync::atomic::AtomicBool;
use windows_sys::Win32::Storage::FileSystem::{FILE_FLAG_BACKUP_SEMANTICS, FILE_SHARE_READ};

const WRITER_LOCK: &str = "configuration.writer-lock";

/// A directory holding the managed replay's prior aggregate.
struct Root(PathBuf);

impl Root {
    fn new(tag: &str) -> Self {
        let name = format!("openblade-profile-store-{tag}-{}", std::process::id());
        let root = Self(std::env::temp_dir().join(name));
        let _ = std::fs::remove_dir_all(&root.0);
        std::fs::create_dir_all(&root.0).unwrap();
        for (name, bytes) in NAMES.into_iter().zip(Model::replay("prior")) {
            std::fs::write(root.0.join(name), bytes).unwrap();
        }
        root
    }

    /// A directory holding exactly `files`.
    fn holding(tag: &str, files: &BTreeMap<String, Vec<u8>>) -> Self {
        let root = Self::new(tag);
        for name in NAMES {
            std::fs::remove_file(root.path(name)).unwrap();
        }
        for (name, bytes) in files {
            std::fs::write(root.path(name), bytes).unwrap();
        }
        root
    }

    /// Every file but the store's own lock, with its bytes.
    fn contents(&self) -> BTreeMap<String, Vec<u8>> {
        self.listing()
            .into_iter()
            .map(|name| {
                let bytes = std::fs::read(self.path(&name)).unwrap();
                (name, bytes)
            })
            .collect()
    }

    fn files(&self) -> ProtectedFiles {
        let root = std::fs::OpenOptions::new()
            .read(true)
            .custom_flags(FILE_FLAG_BACKUP_SEMANTICS)
            .open(&self.0)
            .unwrap();
        ProtectedFiles::injected(root, Arc::new(|_| Ok(()))).unwrap()
    }

    fn storage(&self) -> Arc<Storage> {
        Storage::open(Box::new(self.files()), SID).unwrap()
    }

    fn path(&self, name: &str) -> PathBuf {
        self.0.join(name)
    }

    /// Every file but the store's own lock.
    fn listing(&self) -> Vec<String> {
        let mut names: Vec<String> = std::fs::read_dir(&self.0)
            .unwrap()
            .map(|entry| entry.unwrap().file_name().into_string().unwrap())
            .filter(|name| name != WRITER_LOCK)
            .collect();
        names.sort();
        names
    }

    fn documents(&self) -> [Vec<u8>; 3] {
        NAMES.map(|name| std::fs::read(self.path(name)).unwrap())
    }

    /// Only the three documents remain: no stage, record or backup.
    fn assert_only_documents(&self) {
        let mut expected = NAMES.map(str::to_owned).to_vec();
        expected.sort();
        assert_eq!(self.listing(), expected);
    }
}

impl Drop for Root {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}

/// Opens `path` so that nothing may rename over it or delete it meanwhile.
fn hold(path: &Path) -> std::fs::File {
    std::fs::OpenOptions::new()
        .read(true)
        .share_mode(FILE_SHARE_READ)
        .open(path)
        .unwrap()
}

#[test]
fn a_save_writes_the_aggregate_managed_writes_and_leaves_nothing_else() {
    let root = Root::new("save");
    let storage = root.storage();

    assert_eq!(
        storage
            .save_profiles(&requested(&Model::committed()))
            .unwrap(),
        ProfileSave::Stored
    );

    assert_eq!(root.documents(), Model::replay("committed"));
    root.assert_only_documents();
    drop(storage);
    assert!(!root.storage().pending());
}

#[test]
fn a_refused_rename_leaves_no_stage_and_the_next_save_stores() {
    for held in NAMES {
        let root = Root::new("rename");
        let storage = root.storage();
        let request = requested(&Model::committed());
        // Another reader that does not share deletion: the rename over the
        // document fails for as long as it is open.
        let reader = hold(&root.path(held));

        let first = storage.save_profiles(&request).unwrap();

        drop(reader);
        assert_eq!(first, ProfileSave::Rejected(RESTORED.into()), "{held}");
        assert_eq!(root.documents(), Model::replay("prior"), "{held}");
        root.assert_only_documents();
        assert!(!storage.pending(), "{held}");

        assert_eq!(
            storage.save_profiles(&request).unwrap(),
            ProfileSave::Stored,
            "{held}"
        );
        assert_eq!(root.documents(), Model::replay("committed"), "{held}");
        root.assert_only_documents();
        // A restart opens the store and saves again.
        drop(storage);
        assert_eq!(
            root.storage().save_profiles(&request).unwrap(),
            ProfileSave::Stored,
            "{held}"
        );
    }
}

#[test]
fn a_stage_a_crash_left_is_removed_by_the_next_save() {
    // A crash between creating a stage and renaming it: of the record, which
    // managed would never have read, and of a document.
    for name in [RECORD, NAMES[0], NAMES[2]] {
        let root = Root::new("crash");
        std::fs::write(root.path(&stage(name)), b"{\"schemaVersion\":1").unwrap();
        let storage = root.storage();

        assert_eq!(
            storage
                .save_profiles(&requested(&Model::committed()))
                .unwrap(),
            ProfileSave::Stored,
            "{name}"
        );

        assert_eq!(root.documents(), Model::replay("committed"), "{name}");
        root.assert_only_documents();
        assert!(!storage.pending(), "{name}");
    }
}

#[test]
fn a_stage_another_handle_holds_refuses_the_save_until_it_is_released() {
    let root = Root::new("held-stage");
    let stale = root.path(&stage(NAMES[1]));
    std::fs::write(&stale, b"{").unwrap();
    let holder = hold(&stale);
    let storage = root.storage();
    let request = requested(&Model::committed());

    assert_eq!(
        storage.save_profiles(&request).unwrap(),
        ProfileSave::Rejected(STAGE_BLOCKED.into())
    );

    // Nothing was written: no backup, no record, the prior documents.
    assert_eq!(root.documents(), Model::replay("prior"));
    assert_eq!(root.listing().len(), 4);
    assert!(!storage.pending());

    drop(holder);
    assert_eq!(
        storage.save_profiles(&request).unwrap(),
        ProfileSave::Stored
    );
    root.assert_only_documents();
}

#[test]
fn a_recorded_transaction_is_preserved_whatever_stages_lie_beside_it() {
    // The record is the evidence; a stage next to it is not cleaned up
    // around it and the store does not open.
    let root = Root::new("record");
    std::fs::write(root.path(RECORD), b"{\"schemaVersion\":1}").unwrap();
    std::fs::write(root.path(&stage(NAMES[0])), b"{").unwrap();
    let before = root.listing();

    assert!(Storage::open(Box::new(root.files()), SID).is_err());

    assert_eq!(root.listing(), before);
}

#[test]
fn remove_deletes_only_the_exact_bytes_and_a_stage_only_by_its_name() {
    let root = Root::new("remove");
    let files = root.files();
    let backup = "profiles.json.transaction-0123.backup";
    std::fs::write(root.path(backup), b"exact").unwrap();

    // Other bytes, a prefix and a longer file are all refused and kept.
    for other in [&b"other"[..], b"exac", b"exactly"] {
        assert!(Files::remove(&files, backup, other).is_err());
        assert_eq!(std::fs::read(root.path(backup)).unwrap(), b"exact");
    }
    Files::remove(&files, backup, b"exact").unwrap();
    assert!(!root.path(backup).exists());
    // Nothing to remove is a refusal: the caller named bytes that are not there.
    assert!(Files::remove(&files, backup, b"exact").is_err());

    // No stage is success; a stage is removed whatever it holds, and the
    // document it would have replaced is never touched.
    Files::discard_stage(&files, NAMES[0]).unwrap();
    std::fs::write(root.path(&stage(NAMES[0])), b"half a docu").unwrap();
    Files::discard_stage(&files, NAMES[0]).unwrap();
    assert!(!root.path(&stage(NAMES[0])).exists());
    // It is deleted unread, so one larger than anything the store reads goes too.
    std::fs::write(root.path(&stage(NAMES[0])), vec![b' '; 9 * 1024 * 1024]).unwrap();
    Files::discard_stage(&files, NAMES[0]).unwrap();
    assert!(!root.path(&stage(NAMES[0])).exists());
    assert_eq!(root.documents(), Model::replay("prior"));
    // Names outside the store's alphabet never reach the file system.
    assert!(Files::discard_stage(&files, r"..\profiles.json").is_err());
    assert!(Files::remove(&files, r"..\profiles.json", b"").is_err());
}

#[test]
fn the_store_leaves_a_failed_stage_and_only_its_owner_discards_it() {
    let root = Root::new("replace");
    let files = root.files();
    let prior = Model::replay("prior");
    let reader = hold(&root.path(NAMES[0]));

    assert!(Files::replace(&files, NAMES[0], Some(&prior[0]), b"{}").is_err());

    drop(reader);
    // The replace keeps its stage where the rename failed: for the override
    // document that is evidence. It refuses every later replacement.
    assert_eq!(root.documents(), prior);
    assert_eq!(std::fs::read(root.path(&stage(NAMES[0]))).unwrap(), b"{}");
    assert!(Files::replace(&files, NAMES[0], Some(&prior[0]), b"{}").is_err());

    Files::discard_stage(&files, NAMES[0]).unwrap();
    root.assert_only_documents();
    Files::replace(&files, NAMES[0], Some(&prior[0]), b"{}").unwrap();
    assert_eq!(std::fs::read(root.path(NAMES[0])).unwrap(), b"{}");
    root.assert_only_documents();
}

struct NoCustody;

impl crate::installed::readiness::mailbox_read::ReadCustody for NoCustody {
    fn authorize(&self) -> Option<crate::discovery::performance::ReadAdmission> {
        None
    }
    fn revoke(&self) {}
    fn retire(&self) -> io::Result<()> {
        Ok(())
    }
    fn confirm_drained(&self) -> io::Result<()> {
        Ok(())
    }
}

#[test]
fn the_installed_store_never_discards_a_pending_journal_or_override_document() {
    use crate::installed::readiness::thermal_native::CustodialFiles;
    let root = Root::new("custody");
    let files = CustodialFiles {
        user: root.files(),
        custody: Arc::new(NoCustody),
    };
    // A pending journal is submission evidence. Even one that strayed into
    // the user's directory is not this store's to clean up.
    for journal in [
        "thermal-mutation.journal",
        "thermal-mutation.journal.pending",
    ] {
        std::fs::write(root.path(&stage(journal)), b"evidence").unwrap();
        assert!(Files::discard_stage(&files, journal).is_err());
        assert!(Files::remove(&files, &stage(journal), b"evidence").is_err());
        assert_eq!(
            std::fs::read(root.path(&stage(journal))).unwrap(),
            b"evidence"
        );
    }
    let overrides = "manual-overrides.json";
    std::fs::write(root.path(&stage(overrides)), b"evidence").unwrap();
    assert!(Files::discard_stage(&files, overrides).is_err());
    assert!(root.path(&stage(overrides)).exists());
    // A document's stage goes through to the user's files.
    std::fs::write(root.path(&stage(NAMES[2])), b"{").unwrap();
    Files::discard_stage(&files, NAMES[2]).unwrap();
    assert!(!root.path(&stage(NAMES[2])).exists());
}

#[test]
fn an_older_aggregate_is_migrated_on_open_and_nothing_else_remains() {
    for name in [
        "legacy profile",
        "schema 1 committed aggregate",
        "schema 24 committed aggregate",
    ] {
        let source = case(name);
        let root = Root::holding("migrate", &source.files);

        let storage = root.storage();

        assert_eq!(root.contents(), migrated(&source.files), "{name}");
        root.assert_only_documents();
        assert!(!storage.pending(), "{name}");
        // A restart opens the migrated aggregate and changes nothing.
        drop(storage);
        let migrated = root.contents();
        drop(root.storage());
        assert_eq!(root.contents(), migrated, "{name}");
    }
}

/// Another writer that replaces the marker once, right after the store's
/// own replace has read it back: the only way a real store reads back other
/// bytes, since `ProtectedFiles::replace` verifies each file it writes.
struct SecondWriter {
    files: ProtectedFiles,
    marker: PathBuf,
    armed: AtomicBool,
}

impl Files for SecondWriter {
    fn read(&self, name: &str) -> io::Result<Option<Vec<u8>>> {
        self.files.read(name)
    }
    fn replace(&self, name: &str, expected: Option<&[u8]>, bytes: &[u8]) -> io::Result<()> {
        self.files.replace(name, expected, bytes)?;
        if name == NAMES[2] && self.armed.swap(false, Ordering::AcqRel) {
            std::fs::write(&self.marker, [bytes, b"\r\n"].concat()).unwrap();
        }
        Ok(())
    }
    fn remove(&self, name: &str, expected: &[u8]) -> io::Result<()> {
        self.files.remove(name, expected)
    }
    fn discard_stage(&self, name: &str) -> io::Result<()> {
        self.files.discard_stage(name)
    }
}

/// On real files a migration that reads back as other bytes is restored
/// exactly and then held: later opens are refused without touching a file.
#[test]
fn a_migration_another_writer_changed_is_restored_and_held_on_real_files() {
    forget_mismatches();
    let source = case("schema 24 committed aggregate");
    let root = Root::holding("held", &source.files);
    let writer = SecondWriter {
        files: root.files(),
        marker: root.path(NAMES[2]),
        armed: AtomicBool::new(true),
    };

    let error = Storage::open(Box::new(writer), SID).err().unwrap();

    assert_eq!(error.to_string(), QUARANTINE_BLOCKED);
    assert_eq!(root.contents(), source.files, "restored exactly");
    let stamps = |root: &Root| -> Vec<_> {
        root.listing()
            .into_iter()
            .map(|name| {
                (
                    std::fs::metadata(root.path(&name))
                        .unwrap()
                        .modified()
                        .unwrap(),
                    name,
                )
            })
            .collect()
    };
    let before = stamps(&root);
    for _ in 0..3 {
        let error = Storage::open(Box::new(root.files()), SID).err().unwrap();
        assert_eq!(error.to_string(), QUARANTINE_BLOCKED);
    }
    assert_eq!(stamps(&root), before, "nothing written again");
    forget_mismatches();
    drop(root.storage());
    assert_eq!(root.contents(), migrated(&source.files));
}

#[test]
fn a_refused_rename_at_open_keeps_the_original_until_the_next_open() {
    let source = case("schema 24 committed aggregate");
    for held in NAMES {
        let root = Root::holding("migrate-rename", &source.files);
        // Another reader that does not share deletion: the rename over the
        // document fails for as long as it is open.
        let reader = hold(&root.path(held));

        let opened = Storage::open(Box::new(root.files()), SID);

        drop(reader);
        assert_eq!(
            opened.err().map(|error| error.to_string()).as_deref(),
            Some(MIGRATION_UNCHANGED),
            "{held}"
        );
        // The original bytes, and no stage, record or backup.
        assert_eq!(root.contents(), source.files, "{held}");
        drop(root.storage());
        assert_eq!(root.contents(), migrated(&source.files), "{held}");
    }
}

#[test]
fn a_future_or_corrupt_aggregate_on_real_files_is_left_untouched() {
    for name in [
        "schema 28 is a future schema",
        "legacy with a future schema version",
        "schema 24 profile that is not JSON",
    ] {
        let source = case(name);
        let root = Root::holding("migrate-refused", &source.files);

        assert!(
            Storage::open(Box::new(root.files()), SID).is_err(),
            "{name}"
        );

        assert_eq!(root.contents(), source.files, "{name}");
    }
}

#[test]
fn a_stage_a_crash_left_beside_a_legacy_profile_is_removed_by_the_open() {
    let source = case("legacy profile");
    for name in [RECORD, NAMES[0], NAMES[1]] {
        let root = Root::holding("migrate-crash", &source.files);
        std::fs::write(root.path(&stage(name)), b"{\"pluggedIn\":").unwrap();

        drop(root.storage());

        assert_eq!(root.contents(), migrated(&source.files), "{name}");
    }
}

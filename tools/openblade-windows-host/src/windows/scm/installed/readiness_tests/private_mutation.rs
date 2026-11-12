//! Real files only in the prelisted own-user fixture root. No default root,
//! token, process admission, service, pipe or hardware supplier is called.
#![allow(unsafe_code)]
use super::*;
use crate::installed::readiness::{
    ownership::durable::Store,
    thermal_native::{CustodialFiles, ProtectedFiles},
    thermal_storage::{Files, Override, Storage},
};
use crate::{
    native_owner_record::{Phase, Record},
    windows_protected_path as paths,
};
use std::{fs::File, io::Write, path::PathBuf, sync::Arc};
use windows_sys::Win32::{
    Security::Authorization::ConvertStringSidToSidW, Security::GetLengthSid,
    Storage::FileSystem::FILE_ALL_ACCESS,
};
struct PrivateRoot {
    path: PathBuf,
    root: File,
    _ancestors: Vec<File>,
    verify: Arc<dyn Fn(&File) -> io::Result<()> + Send + Sync>,
    sid: String,
}
impl PrivateRoot {
    fn open(leaf: &str) -> Self {
        // The runner holds and hashes both these explicit inputs before launch.
        let base = PathBuf::from(
            std::env::var_os("OPENBLADE_THERMAL_FIXTURE_ROOT")
                .expect("prelisted private fixture root"),
        );
        assert!(base.is_absolute());
        assert!(matches!(
            leaf,
            "protected-storage" | "owner-history" | "owner-journal"
        ));
        let sid = std::env::var("OPENBLADE_THERMAL_FIXTURE_SID").expect("prelisted own-user SID");
        assert!(sid.starts_with("S-1-5-21-"));
        let wide: Vec<u16> = sid.encode_utf16().chain([0]).collect();
        let mut native = std::ptr::null_mut();
        // SAFETY: Parses only the explicitly supplied SID string; no token lookup.
        assert_ne!(
            unsafe { ConvertStringSidToSidW(wide.as_ptr(), &mut native) },
            0
        );
        let allocation = paths::LocalDescriptor(native);
        let len = unsafe { GetLengthSid(native) } as usize;
        let owner = unsafe { std::slice::from_raw_parts(native.cast::<u8>(), len) }.to_vec();
        drop(allocation);
        let path = base.join(leaf);
        let (root, ancestors, _) = paths::pin_root(&path).unwrap();
        paths::require_ntfs(&root).unwrap();
        let verify: Arc<dyn Fn(&File) -> io::Result<()> + Send + Sync> = Arc::new(move |file| {
            paths::verify_descriptor(file, |bytes| private_acl(bytes, &owner))
        });
        verify(&root).unwrap();
        Self {
            path,
            root,
            _ancestors: ancestors,
            verify,
            sid,
        }
    }
    fn files(&self) -> io::Result<ProtectedFiles> {
        ProtectedFiles::injected(self.root.try_clone()?, self.verify.clone())
    }
    fn owner(&self) -> io::Result<Store> {
        Store::injected(
            self.root.try_clone()?,
            self.path.clone(),
            self.verify.clone(),
        )
    }
}
fn private_acl(bytes: &[u8], owner: &[u8]) -> Option<bool> {
    if paths::sid(paths::section(bytes, 4)?)? != owner {
        return Some(false);
    }
    let control = paths::word(bytes, 2)?;
    if control & 0x8004 != 0x8004 {
        return Some(false);
    }
    let acl = paths::section(bytes, 16)?;
    let mut ace = acl.get(8..usize::from(paths::word(acl, 2)?))?;
    let mut own = false;
    for _ in 0..paths::word(acl, 4)? {
        let n = usize::from(paths::word(ace, 2)?);
        let item = ace.get(..n)?;
        let trustee = paths::sid(item.get(8..)?)?;
        if item[0] != 0
            || item[1] & !0x13 != 0
            || paths::dword(item, 4)? != FILE_ALL_ACCESS
            || !(trustee == owner || trustee == paths::SYSTEM || trustee == paths::ADMINISTRATORS)
        {
            return Some(false);
        }
        own |= trustee == owner;
        ace = &ace[n..];
    }
    Some(own && ace.is_empty())
}
struct JournalCustody(std::sync::Mutex<Store>);
impl crate::installed::readiness::mailbox_read::ReadCustody for JournalCustody {
    fn authorize(&self) -> Option<crate::discovery::performance::ReadAdmission> {
        Some(crate::discovery::performance::ReadAdmission {
            generation: [7; 32],
        })
    }
    fn revoke(&self) {}
    fn retire(&self) -> io::Result<()> {
        Ok(())
    }
    fn confirm_drained(&self) -> io::Result<()> {
        Ok(())
    }
    fn journal_read(&self, pending: bool) -> io::Result<Option<Vec<u8>>> {
        self.0.lock().unwrap().journal_read(pending)
    }
    fn journal_replace(&self, expected: Option<&[u8]>, bytes: &[u8]) -> io::Result<()> {
        self.0.lock().unwrap().journal_replace(expected, bytes)
    }
}
fn reservation(epoch: char) -> Record {
    Record {
        phase: Phase::Reserved,
        sequence: 1,
        predecessor: "0".repeat(64),
        installation: "1".repeat(32),
        epoch: epoch.to_string().repeat(32),
        artifact: format!("{}-service", "3".repeat(64)),
        process: "17:1".into(),
        nonce: "4".repeat(64),
    }
}
#[test]
fn private_owner_store_first_reservation_clean_retirement_and_restart_fence() {
    let fixture = PrivateRoot::open("owner-history");
    let mut store = fixture.owner().unwrap();
    assert!(fixture.owner().is_err());
    store.activate_injected(reservation('2')).unwrap();
    let first = std::fs::read(fixture.path.join("owner-00000000000000000001.bin")).unwrap();
    let second = std::fs::read(fixture.path.join("owner-00000000000000000002.bin")).unwrap();
    assert_eq!(Record::parse(&first).unwrap().phase, Phase::Reserved);
    assert_eq!(Record::parse(&second).unwrap().phase, Phase::Active);
    store.retire_active().unwrap();
    drop(store);
    let mut store = fixture.owner().unwrap();
    store.activate_injected(reservation('5')).unwrap();
    drop(store);
    let before = std::fs::read(fixture.path.join("owner-00000000000000000006.bin")).unwrap();
    assert_eq!(Record::parse(&before).unwrap().phase, Phase::Active);
    let mut restarted = fixture.owner().unwrap();
    assert!(restarted.activate_injected(reservation('6')).is_err());
    restarted.retire_active().unwrap();
    drop(restarted);
    assert_eq!(
        std::fs::read(fixture.path.join("owner-00000000000000000006.bin")).unwrap(),
        before
    );
    assert!(!fixture.path.join("owner-00000000000000000007.bin").exists());
}
#[test]
fn private_protected_override_and_submission_journal_survive_restart_and_share_conflict() {
    use openblade_domain::thermal_mutation::{Preset, Progress};
    use sha2::{Digest, Sha256};
    let fixture = PrivateRoot::open("protected-storage");
    let files = fixture.files().unwrap();
    assert!(fixture.files().is_err());
    let bytes=serde_json::to_vec(&json!({"schemaVersion":8,"overrides":{"performance":null,"fans":null,"lighting":null,"customPerformance":null,"fanControl":null,"gpuClockTuning":null}})).unwrap();
    files
        .replace("manual-overrides.json", None, &bytes)
        .unwrap();
    let machine = PrivateRoot::open("owner-journal");
    let custody = Arc::new(JournalCustody(std::sync::Mutex::new(
        machine.owner().unwrap(),
    )));
    let storage = Storage::open(
        Box::new(CustodialFiles {
            user: files,
            custody: custody.clone(),
        }),
        &fixture.sid,
    )
    .unwrap();
    let requested = Some(Override {
        value: 1,
        lifetime: 1,
        custom_performance: None,
        gpu_clock_tuning: None,
    });
    storage.save(requested.clone(), 0).unwrap();
    let intent = format!(
        "{:x}",
        Sha256::digest(serde_json::to_vec(&requested).unwrap())
    );
    let request = "aaaaaaaa-bbbb-cccc-dddd-000000000001";
    storage
        .record(
            request,
            &"7".repeat(64),
            Preset::Balanced,
            &intent,
            &requested,
            42,
            7,
            Progress::Prepared,
        )
        .unwrap();
    storage
        .record(
            request,
            &"7".repeat(64),
            Preset::Balanced,
            &intent,
            &requested,
            42,
            7,
            Progress::FirstIntent,
        )
        .unwrap();
    drop(storage);
    drop(custody);
    let custody = Arc::new(JournalCustody(std::sync::Mutex::new(
        machine.owner().unwrap(),
    )));
    let reopened = Storage::open(
        Box::new(CustodialFiles {
            user: fixture.files().unwrap(),
            custody: custody.clone(),
        }),
        "S-1-5-21-111-222-333-1002",
    )
    .unwrap();
    assert!(reopened.pending());
    assert_eq!(reopened.progress(), Some(1));
    assert!(reopened.save(None, 0).is_err());
    drop(reopened);
    let original = std::fs::read(fixture.path.join("manual-overrides.json")).unwrap();
    let mut interrupted = std::fs::OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(machine.path.join("thermal-mutation.journal.pending"))
        .unwrap();
    interrupted.write_all(b"interrupted").unwrap();
    interrupted.sync_all().unwrap();
    drop(interrupted);
    assert!(
        Storage::open(
            Box::new(CustodialFiles {
                user: fixture.files().unwrap(),
                custody
            }),
            &fixture.sid
        )
        .is_err()
    );
    assert_eq!(
        std::fs::read(fixture.path.join("manual-overrides.json")).unwrap(),
        original
    );
    assert_eq!(
        std::fs::read(machine.path.join("thermal-mutation.journal.pending")).unwrap(),
        b"interrupted"
    );
}

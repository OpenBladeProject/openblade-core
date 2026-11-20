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

#[cfg(feature = "service-battery-policy-v1")]
type RecordCheck = Arc<dyn Fn(&Record, bool) -> io::Result<()> + Send + Sync>;
use windows_sys::Win32::{
    Security::Authorization::ConvertStringSidToSidW, Security::GetLengthSid,
    Storage::FileSystem::FILE_ALL_ACCESS,
};
// No runner prepares these roots (build/NativeWindowsFixtureManifest.json lists no
// case here), so every test is ignored rather than passing without running. To run
// them, set OPENBLADE_THERMAL_FIXTURE_ROOT and OPENBLADE_THERMAL_FIXTURE_SID, create
// each leaf PrivateRoot::open names with an own-user-only ACL on NTFS and an empty
// writer.lock in it (plus a thermal-history directory where a case seeds segments
// directly), then pass --ignored.
struct PrivateRoot {
    path: PathBuf,
    root: File,
    _ancestors: Vec<File>,
    verify: crate::installed::FileVerifier,
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
        assert!(
            (leaf.starts_with("history-cut-")
                && leaf.len() == 14
                && leaf[12..]
                    .parse::<usize>()
                    .is_ok_and(|n| n < 18 && leaf == format!("history-cut-{n:02}")))
                || matches!(
                    leaf,
                    "protected-storage"
                        | "battery-corrections"
                        | "owner-history"
                        | "owner-journal"
                        | "owner-journal-foreign"
                )
        );
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
        let verify: crate::installed::FileVerifier = Arc::new(move |file| {
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
            lifecycle: 1,
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
    fn journal_read_release(&self, pending: bool) -> io::Result<Option<Vec<u8>>> {
        self.0.lock().unwrap().journal_read_release(pending)
    }
    fn journal_reserve(
        &self,
        expected: Option<&[u8]>,
        needed: usize,
    ) -> io::Result<Option<Vec<u8>>> {
        self.0.lock().unwrap().journal_reserve(expected, needed)
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
        nonce: epoch.to_string().repeat(64),
    }
}
#[test]
#[ignore = "needs prepared private fixture roots; see the note above PrivateRoot"]
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
#[ignore = "needs prepared private fixture roots; see the note above PrivateRoot"]
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
    let mut first_owner = machine.owner().unwrap();
    first_owner.activate_injected(reservation('7')).unwrap();
    let custody = Arc::new(JournalCustody(std::sync::Mutex::new(first_owner)));
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
    for progress in [
        Progress::Prepared,
        Progress::FirstIntent,
        Progress::FirstAcknowledged,
        Progress::SecondIntent,
        Progress::SecondAcknowledged,
        Progress::Confirmed,
    ] {
        storage
            .record(
                request,
                &"7".repeat(64),
                Preset::Balanced.into(),
                &intent,
                &requested,
                42,
                7,
                progress,
            )
            .unwrap();
    }
    drop(storage);
    custody.0.lock().unwrap().retire_active().unwrap();
    drop(custody);
    let mut next_owner = machine.owner().unwrap();
    next_owner.activate_injected(reservation('8')).unwrap();
    let custody = Arc::new(JournalCustody(std::sync::Mutex::new(next_owner)));
    let storage = Storage::open(
        Box::new(CustodialFiles {
            user: fixture.files().unwrap(),
            custody: custody.clone(),
        }),
        &fixture.sid,
    )
    .unwrap();
    assert!(
        !storage.pending(),
        "confirmed prior authenticated owner remains valid after new activation"
    );
    for progress in [Progress::Prepared, Progress::FirstIntent] {
        storage
            .record(
                "aaaaaaaa-bbbb-cccc-dddd-000000000002",
                &"8".repeat(64),
                Preset::Balanced.into(),
                &intent,
                &requested,
                42,
                7,
                progress,
            )
            .unwrap();
    }
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

#[test]
#[ignore = "needs prepared private fixture roots; see the note above PrivateRoot"]
fn private_foreign_confirmed_journal_owner_is_refused_without_rewriting_history() {
    use sha2::{Digest, Sha256};
    let fixture = PrivateRoot::open("owner-journal-foreign");
    let mut owner = fixture.owner().unwrap();
    owner.activate_injected(reservation('7')).unwrap();
    let requested = Some(Override {
        value: 1,
        lifetime: 1,
        custom_performance: None,
        gpu_clock_tuning: None,
    });
    let intent = format!(
        "{:x}",
        Sha256::digest(serde_json::to_vec(&requested).unwrap())
    );
    let mut bytes = vec![];
    for progress in 0..=5 {
        let entry = json!({"request":"aaaaaaaa-bbbb-cccc-dddd-000000000001","owner":"f".repeat(64),"user_sid":fixture.sid,"caller_pid":42,"session":7,"requested":requested,"destination":1,"intent":intent,"progress":progress});
        bytes.extend(serde_json::to_vec(&entry).unwrap());
        bytes.push(b'\n');
    }
    assert!(owner.journal_replace(None, &bytes).is_err());
    assert!(!fixture.path.join("thermal-mutation.journal").exists());
    owner.retire_active().unwrap();
    drop(owner);
    // Explicit private fixture input models an accidentally substituted,
    // otherwise complete journal. Never product discovery or owner repair.
    let path = fixture.path.join("thermal-mutation.journal");
    let mut file = std::fs::OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(&path)
        .unwrap();
    file.write_all(&bytes).unwrap();
    file.sync_all().unwrap();
    drop(file);
    let owner = fixture.owner().unwrap();
    assert!(owner.journal_read(false).is_err());
    assert!(owner.journal_replace(Some(&bytes), &bytes).is_err());
    drop(owner);
    assert_eq!(std::fs::read(path).unwrap(), bytes);
    assert_eq!(
        Record::parse(&std::fs::read(fixture.path.join("owner-00000000000000000004.bin")).unwrap())
            .unwrap()
            .phase,
        Phase::Retired
    );
}

#[test]
#[ignore = "needs prepared private fixture roots; see the note above PrivateRoot"]
fn private_prepared_settlement_authenticates_owner_prefix_and_reopens_terminal_history() {
    use openblade_domain::thermal_mutation::{Preset, Progress};
    use sha2::{Digest, Sha256};
    // Actual worker retirement is covered by the injected outer composition.
    // This fixture covers the protected owner-store/atomic-file parser boundary.
    let user = PrivateRoot::open("protected-storage");
    let machine = PrivateRoot::open("owner-journal");
    let mut owner = machine.owner().unwrap();
    owner.activate_injected(reservation('7')).unwrap();
    let custody = Arc::new(JournalCustody(std::sync::Mutex::new(owner)));
    let storage = Storage::open(
        Box::new(CustodialFiles {
            user: user.files().unwrap(),
            custody: custody.clone(),
        }),
        &user.sid,
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
    let request = "aaaaaaaa-bbbb-cccc-dddd-000000000003";
    storage
        .record(
            request,
            &"7".repeat(64),
            Preset::Balanced.into(),
            &intent,
            &requested,
            42,
            7,
            Progress::Prepared,
        )
        .unwrap();
    let prepared = custody
        .0
        .lock()
        .unwrap()
        .journal_read(false)
        .unwrap()
        .unwrap();
    assert!(storage.pending());
    storage
        .record(
            request,
            &"7".repeat(64),
            Preset::Balanced.into(),
            &intent,
            &requested,
            42,
            7,
            Progress::KnownNotSubmitted,
        )
        .unwrap();
    let settled = custody
        .0
        .lock()
        .unwrap()
        .journal_read(false)
        .unwrap()
        .unwrap();
    assert!(settled.starts_with(&prepared));
    assert!(!storage.pending());
    assert_eq!(storage.progress(), Some(6));
    let mut rows: Vec<Value> = settled
        .split(|b| *b == b'\n')
        .filter(|v| !v.is_empty())
        .map(|v| serde_json::from_slice(v).unwrap())
        .collect();
    rows[1]["owner"] = json!("f".repeat(64));
    let mut foreign = Vec::new();
    for row in rows {
        foreign.extend(serde_json::to_vec(&row).unwrap());
        foreign.push(b'\n');
    }
    assert!(
        custody
            .0
            .lock()
            .unwrap()
            .journal_replace(Some(&settled), &foreign)
            .is_err()
    );
    assert_eq!(
        std::fs::read(machine.path.join("thermal-mutation.journal")).unwrap(),
        settled
    );
    drop(storage);
    custody.0.lock().unwrap().retire_active().unwrap();
    drop(custody);
    let mut owner = machine.owner().unwrap();
    assert_eq!(owner.journal_read(false).unwrap().unwrap(), settled);
    owner.activate_injected(reservation('8')).unwrap();
    let custody = Arc::new(JournalCustody(std::sync::Mutex::new(owner)));
    let storage = Storage::open(
        Box::new(CustodialFiles {
            user: user.files().unwrap(),
            custody: custody.clone(),
        }),
        &user.sid,
    )
    .unwrap();
    assert!(!storage.pending());
    assert_eq!(storage.progress(), Some(6));
    storage.save(None, 0).unwrap();
    assert_eq!(
        custody
            .0
            .lock()
            .unwrap()
            .journal_read(false)
            .unwrap()
            .unwrap(),
        settled
    );
    drop(storage);
    custody.0.lock().unwrap().retire_active().unwrap();
}

#[test]
#[ignore = "needs prepared private fixture roots; see the note above PrivateRoot"]
fn private_pending_terminal_file_preserves_intact_original_owner_custody() {
    use openblade_domain::thermal_mutation::{Preset, Progress};
    use sha2::{Digest, Sha256};
    let user = PrivateRoot::open("protected-storage");
    let machine = PrivateRoot::open("owner-journal");
    let mut owner = machine.owner().unwrap();
    owner.activate_injected(reservation('7')).unwrap();
    let custody = Arc::new(JournalCustody(std::sync::Mutex::new(owner)));
    let storage = Storage::open(
        Box::new(CustodialFiles {
            user: user.files().unwrap(),
            custody: custody.clone(),
        }),
        &user.sid,
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
    let request = "aaaaaaaa-bbbb-cccc-dddd-000000000003";
    storage
        .record(
            request,
            &"7".repeat(64),
            Preset::Balanced.into(),
            &intent,
            &requested,
            42,
            7,
            Progress::Prepared,
        )
        .unwrap();
    let prepared = custody
        .0
        .lock()
        .unwrap()
        .journal_read(false)
        .unwrap()
        .unwrap();
    // A protected, fully formed pending file can survive an uncertain previous
    // append. Its presence is not evidence that the retained owner was replaced.
    let mut terminal: Value =
        serde_json::from_slice(prepared.strip_suffix(b"\n").unwrap()).unwrap();
    terminal["progress"] = json!(6);
    let mut pending = prepared.clone();
    pending.extend(serde_json::to_vec(&terminal).unwrap());
    pending.push(b'\n');
    std::fs::write(
        machine.path.join("thermal-mutation.journal.pending"),
        &pending,
    )
    .unwrap();
    let error = storage
        .record(
            request,
            &"7".repeat(64),
            Preset::Balanced.into(),
            &intent,
            &requested,
            42,
            7,
            Progress::KnownNotSubmitted,
        )
        .unwrap_err();
    assert!(!crate::installed::readiness::thermal_mutation::is_fatal_admission(&error));
    assert!(storage.pending());
    let owner = custody.0.lock().unwrap();
    owner.verify_journal_custody().unwrap();
    assert_eq!(owner.journal_read(false).unwrap().unwrap(), prepared);
    assert_eq!(owner.journal_read(true).unwrap().unwrap(), pending);
    drop(owner);
    drop(storage);
    assert!(
        Storage::open(
            Box::new(CustodialFiles {
                user: user.files().unwrap(),
                custody: custody.clone()
            }),
            &user.sid
        )
        .is_err()
    );
    assert_eq!(
        std::fs::read(machine.path.join("thermal-mutation.journal")).unwrap(),
        prepared
    );
    assert_eq!(
        std::fs::read(machine.path.join("thermal-mutation.journal.pending")).unwrap(),
        pending
    );
    custody.0.lock().unwrap().retire_active().unwrap();
}

#[test]
#[ignore = "needs prepared private fixture roots; see the note above PrivateRoot"]
fn history_growth_real_storage_reserves_before_prepared_across_original_owners() {
    use crate::thermal_history::{self as history, Entry};
    use openblade_domain::thermal_mutation::{Preset, Progress};
    let user = PrivateRoot::open("protected-storage");
    let machine = PrivateRoot::open("owner-journal");
    let mut owner = machine.owner().unwrap();
    owner.activate_injected(reservation('7')).unwrap();
    let requested = None;
    let intent = crate::native_owner_record::digest(b"null");
    let mut seed = vec![];
    let mut n = 0;
    loop {
        let mut transaction = vec![];
        for progress in [0, 6] {
            let e = Entry {
                tfc: None,
                battery: None,
                fan: None,
                request: format!("aaaaaaaa-bbbb-cccc-dddd-{n:012}"),
                owner: "7".repeat(64),
                user_sid: user.sid.clone(),
                caller_pid: 42,
                session: 7,
                requested: None,
                destination: 1,
                intent: intent.clone(),
                progress,
            };
            transaction.extend(serde_json::to_vec(&e).unwrap());
            transaction.push(b'\n');
        }
        if seed.len() + transaction.len() > history::SEGMENT {
            break;
        }
        seed.extend(transaction);
        n += 1;
    }
    owner.journal_replace(None, &seed).unwrap();
    owner.retire_active().unwrap();
    drop(owner);
    let mut owner = machine.owner().unwrap();
    owner.activate_injected(reservation('8')).unwrap();
    let custody = Arc::new(JournalCustody(std::sync::Mutex::new(owner)));
    let storage = Storage::open(
        Box::new(CustodialFiles {
            user: user.files().unwrap(),
            custody: custody.clone(),
        }),
        &user.sid,
    )
    .unwrap();
    let request = "bbbbbbbb-cccc-dddd-eeee-000000000001";
    storage
        .record(
            request,
            &"8".repeat(64),
            Preset::Balanced.into(),
            &intent,
            &requested,
            42,
            7,
            Progress::Prepared,
        )
        .unwrap();
    assert_eq!(
        std::fs::read(
            machine
                .path
                .join("thermal-history/segment-00000000000000000001.bin")
        )
        .unwrap(),
        seed
    );
    assert!(
        custody
            .0
            .lock()
            .unwrap()
            .journal_read(false)
            .unwrap()
            .unwrap()
            .len()
            < history::ROW
    );
    // Same actual storage chronology, typed terminal guarantee remains distinct.
    for p in [
        Progress::FirstIntent,
        Progress::FirstAcknowledged,
        Progress::SecondIntent,
        Progress::SecondAcknowledged,
        Progress::Confirmed,
    ] {
        storage
            .record(
                request,
                &"8".repeat(64),
                Preset::Balanced.into(),
                &intent,
                &requested,
                42,
                7,
                p,
            )
            .unwrap();
    }
    drop(storage);
    let mut owner = custody.0.lock().unwrap();
    owner.retire_active().unwrap();
    drop(owner);
    drop(custody);
    let mut owner = machine.owner().unwrap();
    owner.activate_injected(reservation('9')).unwrap();
    let custody = Arc::new(JournalCustody(std::sync::Mutex::new(owner)));
    let storage = Storage::open(
        Box::new(CustodialFiles {
            user: user.files().unwrap(),
            custody: custody.clone(),
        }),
        &user.sid,
    )
    .unwrap();
    assert!(!storage.pending());
    let request = "cccccccc-dddd-eeee-ffff-000000000001";
    storage
        .record(
            request,
            &"9".repeat(64),
            Preset::Performance.into(),
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
            &"9".repeat(64),
            Preset::Performance.into(),
            &intent,
            &requested,
            42,
            7,
            Progress::KnownNotSubmitted,
        )
        .unwrap();
    let tail = custody
        .0
        .lock()
        .unwrap()
        .journal_read(false)
        .unwrap()
        .unwrap();
    assert!(seed.len() + tail.len() > history::SEGMENT);
    assert_eq!(
        history::rows(&tail, |_, _| Ok(()))
            .unwrap()
            .1
            .unwrap()
            .progress,
        6
    );
}

#[test]
#[ignore = "needs prepared private fixture roots; see the note above PrivateRoot"]
fn history_protected_publication_cuts_recover_only_matched_metadata_and_preserve_every_byte() {
    let vector: Value = serde_json::from_str(include_str!(
        "../../../../../tests/data/thermal-history-v1.json"
    ))
    .unwrap();
    let original = vector["segment"].as_str().unwrap().as_bytes().to_vec();
    let boundaries = [
        "segment:created",
        "segment:written",
        "segment:flushed",
        "segment:readback",
        "checkpoint:created",
        "checkpoint:written",
        "checkpoint:flushed",
        "checkpoint:readback",
        "roll:created",
        "roll:written",
        "roll:flushed",
        "roll:readback",
        "active:created",
        "active:written",
        "active:flushed",
        "active:readback",
        "active:renamed",
        "active:verified",
    ];
    for (index, boundary) in boundaries.into_iter().enumerate() {
        let machine = PrivateRoot::open(&format!("history-cut-{index:02}"));
        let mut owner = machine.owner().unwrap();
        owner.activate_injected(reservation('4')).unwrap();
        owner.journal_replace(None, &original).unwrap();
        let triggered = Arc::new(std::sync::atomic::AtomicBool::new(false));
        let seen = triggered.clone();
        owner.history_boundary = Some(Arc::new(move |point| {
            if point == boundary {
                seen.store(true, std::sync::atomic::Ordering::Release);
                Err(io::ErrorKind::StorageFull.into())
            } else {
                Ok(())
            }
        }));
        assert!(
            owner
                .journal_reserve(Some(&original), crate::thermal_history::SEGMENT)
                .is_err()
        );
        assert!(triggered.load(std::sync::atomic::Ordering::Acquire));
        drop(owner);
        let owner = machine.owner().unwrap();
        let recovered = owner.journal_read(false);
        let recoverable = matches!(index,9..=11|13..=17);
        assert_eq!(recovered.is_ok(), recoverable, "{boundary}");
        if recoverable {
            assert!(recovered.unwrap().unwrap().is_empty());
        }
        let active = std::fs::read(machine.path.join("thermal-mutation.journal")).unwrap();
        let archived = std::fs::read(
            machine
                .path
                .join("thermal-history/segment-00000000000000000001.bin"),
        )
        .ok();
        assert!(active == original || archived.as_ref() == Some(&original));
        assert!(
            !machine
                .path
                .join("thermal-mutation.journal.pending")
                .exists()
        );
        println!(
            "THERMAL_METADATA_CUT {boundary} recovered={recoverable} original_sha256={}",
            crate::native_owner_record::digest(&original)
        );
    }
}

#[test]
#[ignore = "needs prepared private fixture roots; see the note above PrivateRoot"]
fn history_orphan_rollover_staging_without_directory_is_never_empty_native_history() {
    let machine = PrivateRoot::open("owner-journal-foreign");
    let mut owner = machine.owner().unwrap();
    owner.activate_injected(reservation('4')).unwrap();
    let path = machine.path.join("thermal-mutation.journal.rollover");
    let evidence = b"partial metadata, never firmware intent";
    std::fs::write(&path, evidence).unwrap();
    assert!(!machine.path.join("thermal-history").exists());
    assert!(owner.journal_read(false).is_err());
    assert!(owner.journal_reserve(None, 1000).is_err());
    drop(owner);
    let owner = machine.owner().unwrap();
    assert!(owner.journal_read(false).is_err());
    assert_eq!(std::fs::read(path).unwrap(), evidence);
    assert!(!machine.path.join("thermal-mutation.journal").exists());
}

#[cfg(feature = "service-fan-mutation-v2")]
#[test]
#[ignore = "needs prepared private fixture roots; see the note above PrivateRoot"]
fn fan_v2_real_protected_history_retains_owned_fence_across_reload_and_rollover() {
    verify_fan_protected_history(
        serde_json::from_str(include_str!(
            "../../../../../tests/data/fan-history-v2.json"
        ))
        .unwrap(),
        "released",
    );
}
#[cfg(feature = "service-fan-mutation-v2")]
#[test]
#[ignore = "needs prepared private fixture roots; see the note above PrivateRoot"]
fn fan_v3_observed_auto_protected_history_reloads_and_rolls_losslessly() {
    verify_fan_protected_history(
        serde_json::from_str(include_str!(
            "../../../../../tests/data/fan-history-v3.json"
        ))
        .unwrap(),
        "observedAutomatic",
    );
}
#[cfg(feature = "service-fan-mutation-v2")]
fn verify_fan_protected_history(vector: Value, terminal: &str) {
    let machine = PrivateRoot::open("owner-journal");
    let mut owner = machine.owner().unwrap();
    owner.activate_injected(reservation('7')).unwrap();
    let owned = vector["owned"].as_str().unwrap().as_bytes();
    let released = vector[terminal].as_str().unwrap().as_bytes();
    owner.journal_replace(None, owned).unwrap();
    assert_eq!(owner.journal_read(false).unwrap().unwrap(), owned);
    let refusal = owner
        .journal_reserve(Some(owned), crate::thermal_history::SEGMENT)
        .unwrap_err();
    assert!(crate::thermal_history::known_capacity_refusal(&refusal));
    assert_eq!(owner.journal_read(false).unwrap().unwrap(), owned);
    owner.retire_active().unwrap();
    drop(owner);
    let owner = machine.owner().unwrap();
    assert_eq!(owner.journal_read(false).unwrap().unwrap(), owned);
    drop(owner);
    // Completion uses the same retained authenticated original producer; a new
    // generation cannot adopt fan ownership. This fixture appends only evidence.
    let owner = machine.owner().unwrap();
    owner.journal_replace(Some(owned), released).unwrap();
    let tail = owner
        .journal_reserve(Some(released), crate::thermal_history::SEGMENT)
        .unwrap()
        .unwrap();
    assert!(tail.is_empty());
    let segment = std::fs::read(
        machine
            .path
            .join("thermal-history/segment-00000000000000000001.bin"),
    )
    .unwrap();
    assert_eq!(segment, released);
}

#[cfg(feature = "service-battery-policy-v1")]
#[test]
#[ignore = "needs prepared private fixture roots; see the note above PrivateRoot"]
fn battery_v1_protected_history_requires_original_producer_and_retains_interleaved_release() {
    let vector: Value = serde_json::from_str(include_str!(
        "../../../../../tests/data/battery-history-v1.json"
    ))
    .unwrap();
    let machine = PrivateRoot::open("owner-journal");
    let mut owner = machine.owner().unwrap();
    owner.activate_injected(reservation('7')).unwrap();
    let admitted: RecordCheck = Arc::new(|original, temporary| {
        if !temporary
            && original.nonce == "7".repeat(64)
            && original.epoch == "7".repeat(32)
            && original.installation == "1".repeat(32)
            && original.artifact == format!("{}-service", "3".repeat(64))
        {
            Ok(())
        } else {
            Err(io::ErrorKind::PermissionDenied.into())
        }
    });
    let mixed = vector["interleaved"].as_str().unwrap().as_bytes();
    let released = vector["released"].as_str().unwrap().as_bytes();
    // Injected Store denies missing supplier before any real path/package resolver.
    assert!(owner.journal_replace(None, mixed).is_err());
    assert!(!machine.path.join("thermal-mutation.journal").exists());
    owner.battery_producer_fixture = Some(admitted.clone());
    owner.journal_replace(None, mixed).unwrap();
    assert!(crate::thermal_history::known_capacity_refusal(
        &owner
            .journal_reserve(Some(mixed), crate::thermal_history::SEGMENT)
            .unwrap_err()
    ));
    owner.retire_active().unwrap();
    drop(owner);
    let mut owner = machine.owner().unwrap();
    owner.battery_producer_fixture =
        Some(Arc::new(|_, _| Err(io::ErrorKind::PermissionDenied.into())));
    assert!(owner.journal_read(false).is_err());
    assert_eq!(
        std::fs::read(machine.path.join("thermal-mutation.journal")).unwrap(),
        mixed
    );
    owner.battery_producer_fixture = Some(admitted);
    assert_eq!(owner.journal_read(false).unwrap().unwrap(), mixed);
    owner.journal_replace(Some(mixed), released).unwrap();
    let tail = owner
        .journal_reserve(Some(released), crate::thermal_history::SEGMENT)
        .unwrap()
        .unwrap();
    assert!(tail.is_empty());
    assert_eq!(
        std::fs::read(
            machine
                .path
                .join("thermal-history/segment-00000000000000000001.bin")
        )
        .unwrap(),
        released
    );
}

#[cfg(feature = "service-battery-policy-v1")]
#[test]
#[ignore = "needs prepared private fixture roots; see the note above PrivateRoot"]
fn battery_correction_protected_complete_rehashed_active_and_archived_inputs() {
    let vector: Value = serde_json::from_str(include_str!(
        "../../../../../tests/data/battery-history-v1.json"
    ))
    .unwrap();
    let machine = PrivateRoot::open("battery-corrections");
    let mut owner = machine.owner().unwrap();
    owner.activate_injected(reservation('7')).unwrap();
    owner.retire_active().unwrap();
    owner.battery_producer_fixture = Some(Arc::new(|original, temporary| {
        if !temporary
            && original.nonce == "7".repeat(64)
            && original.epoch == "7".repeat(32)
            && original.installation == "1".repeat(32)
            && original.artifact == format!("{}-service", "3".repeat(64))
        {
            Ok(())
        } else {
            Err(io::ErrorKind::PermissionDenied.into())
        }
    }));
    for case in vector["corrections"].as_array().unwrap() {
        for rolled in [false, true] {
            let raw = case[if rolled { "archivedJournal" } else { "journal" }]
                .as_str()
                .unwrap()
                .as_bytes();
            let journal = machine.path.join("thermal-mutation.journal");
            let physical = if rolled {
                let cp = crate::thermal_history::Checkpoint {
                    sequence: 1,
                    previous: "0".repeat(64),
                    segment: crate::thermal_history::digest(raw),
                    length: raw.len() as u64,
                    records: raw.iter().filter(|&&c| c == b'\n').count() as u64,
                }
                .bytes();
                let empty =
                    crate::thermal_history::envelope(1, &crate::thermal_history::digest(&cp), &[]);
                let roll = format!(
                    "OpenBlade.ThermalRoll.v1\n{}\n{}\n{}\n{}\n",
                    crate::thermal_history::digest(raw),
                    crate::thermal_history::digest(&empty),
                    crate::thermal_history::digest(&cp),
                    crate::thermal_history::CONTRACT
                )
                .into_bytes();
                for (name, bytes) in [
                    ("segment", raw.to_vec()),
                    ("checkpoint", cp),
                    ("roll", roll),
                ] {
                    std::fs::write(
                        machine
                            .path
                            .join(format!("thermal-history/{name}-00000000000000000001.bin")),
                        bytes,
                    )
                    .unwrap();
                }
                empty
            } else {
                raw.to_vec()
            };
            std::fs::write(&journal, &physical).unwrap();
            let before = std::fs::read(&journal).unwrap();
            let result = owner.journal_read_release(false);
            assert_eq!(
                result.is_ok(),
                case["valid"].as_bool().unwrap(),
                "{} rolled={rolled}",
                case["name"]
            );
            assert_eq!(std::fs::read(&journal).unwrap(), before);
            if rolled {
                assert_eq!(
                    std::fs::read(
                        machine
                            .path
                            .join("thermal-history/segment-00000000000000000001.bin")
                    )
                    .unwrap(),
                    raw
                );
                for name in ["segment", "checkpoint", "roll"] {
                    std::fs::remove_file(
                        machine
                            .path
                            .join(format!("thermal-history/{name}-00000000000000000001.bin")),
                    )
                    .unwrap();
                }
            }
        }
    }
}

#[cfg(feature = "service-temporary-charge-v1")]
#[test]
#[ignore = "needs prepared private fixture roots; see the note above PrivateRoot"]
fn tfc_v1_protected_original_producer_is_checked_for_each_required_family() {
    let vector: Value = serde_json::from_str(include_str!(
        "../../../../../tests/data/temporary-charge-history-v1.json"
    ))
    .unwrap();
    let raw = vector["cases"]
        .as_array()
        .unwrap()
        .iter()
        .find(|c| c["name"] == "enable")
        .unwrap()["journal"]
        .as_str()
        .unwrap()
        .as_bytes();
    let machine = PrivateRoot::open("owner-journal");
    let mut owner = machine.owner().unwrap();
    owner.activate_injected(reservation('7')).unwrap();
    let basic: RecordCheck = Arc::new(|original, temporary| {
        if !temporary && original.nonce == "7".repeat(64) {
            Ok(())
        } else {
            Err(io::ErrorKind::PermissionDenied.into())
        }
    });
    owner.battery_producer_fixture = Some(basic.clone());
    assert!(owner.journal_replace(None, raw).is_err());
    assert!(!machine.path.join("thermal-mutation.journal").exists());
    owner.battery_producer_fixture = Some(Arc::new(|original, _| {
        if original.nonce == "7".repeat(64)
            && original.epoch == "7".repeat(32)
            && original.installation == "1".repeat(32)
            && original.artifact == format!("{}-service", "3".repeat(64))
        {
            Ok(())
        } else {
            Err(io::ErrorKind::PermissionDenied.into())
        }
    }));
    owner.journal_replace(None, raw).unwrap();
    assert_eq!(owner.journal_read_release(false).unwrap().unwrap(), raw);
    // A release read cannot repair/publish metadata. Staging and malformed
    // evidence remain byte-identical while the release-only path fails closed.
    let staging = machine.path.join(crate::thermal_history::STAGING);
    std::fs::write(&staging, b"retained staging evidence").unwrap();
    assert!(owner.journal_read_release(false).is_err());
    assert_eq!(
        std::fs::read(&staging).unwrap(),
        b"retained staging evidence"
    );
    assert_eq!(
        std::fs::read(machine.path.join(crate::thermal_history::ACTIVE)).unwrap(),
        raw
    );
    std::fs::remove_file(&staging).unwrap();
    owner.battery_producer_fixture = Some(basic);
    assert!(owner.journal_read_release(false).is_err());
    assert_eq!(
        std::fs::read(machine.path.join("thermal-mutation.journal")).unwrap(),
        raw
    );
}

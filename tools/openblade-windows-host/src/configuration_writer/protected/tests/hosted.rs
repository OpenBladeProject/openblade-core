use super::*;
use crate::configuration_writer::{
    FileIdentity, LOCK, Outcome, TestStore, check_regular_file, file_identity,
    fixture::managed_documents, mark_for_deletion, open_relative_inner, transaction_id,
};
use windows_sys::{
    Wdk::Storage::FileSystem::FILE_CREATE,
    Win32::Storage::FileSystem::{DELETE, FILE_SHARE_DELETE, GetVolumeInformationByHandleW},
};

#[test]
#[ignore = "requires the disposable hosted Windows administrator runner"]
fn hosted_ntfs_admits_managed_configuration() -> io::Result<()> {
    if std::env::var("OPENBLADE_HOSTED_NTFS_ADMIN").as_deref() != Ok("1") {
        return Err(io::Error::other("hosted administrator preflight missing"));
    }
    let drive = PathBuf::from(format!(
        "{}\\",
        std::env::var("SystemDrive").map_err(io::Error::other)?
    ));
    let root = drive.join(format!("openblade-ntfs-{}", transaction_id()?));
    let (drive, names) = split_path(&root)?;
    let [leaf] = names.as_slice() else {
        return Err(denied("fixture must be a direct child of the system drive"));
    };

    // Check and retain the only ancestor before creating anything.
    let (drive_pin, _) = pin_drive(&drive)?;
    verify_security(&drive_pin, Policy::Ancestor)?;
    let mut filesystem = [0u16; 16];
    // SAFETY: the handle is live and filesystem is a writable UTF-16 buffer.
    if unsafe {
        GetVolumeInformationByHandleW(
            drive_pin.as_raw_handle(),
            ptr::null_mut(),
            0,
            ptr::null_mut(),
            ptr::null_mut(),
            ptr::null_mut(),
            filesystem.as_mut_ptr(),
            filesystem.len() as u32,
        )
    } == 0
    {
        return Err(io::Error::last_os_error());
    }
    if !filesystem
        .into_iter()
        .take(5)
        .eq("NTFS".encode_utf16().chain([0]))
    {
        return Err(io::Error::other("fixture drive must be NTFS"));
    }

    let setup_sddl = std::env::var("OPENBLADE_HOSTED_SETUP_SDDL").map_err(io::Error::other)?;
    let setup = local_descriptor(&setup_sddl).map_err(|error| {
        io::Error::new(
            error.kind(),
            format!("could not convert the Setup descriptor: {error}"),
        )
    })?;
    if accepts_descriptor(setup.bytes(), Policy::Root) != Some(true) {
        return Err(denied("Setup descriptor does not match Rust admission"));
    }
    let created = open_relative_inner(
        &drive_pin,
        leaf,
        FILE_GENERIC_READ,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
        FILE_CREATE,
        FILE_DIRECTORY_FILE | FILE_OPEN_REPARSE_POINT,
        Some(&setup.allocation),
    )?;

    let result = std::panic::catch_unwind(|| {
        let identity = file_identity(&directory_info(&created)?);
        exercise_admission(&root, identity)
    });
    let cleanup = cleanup_fixture(&root, created);
    drop(drive_pin);
    match result {
        Ok(Ok(())) => cleanup,
        Ok(Err(error)) => {
            if let Err(cleanup) = cleanup {
                eprintln!("fixture cleanup also failed: {cleanup}");
            }
            Err(error)
        }
        Err(panic) => {
            if let Err(cleanup) = cleanup {
                eprintln!("fixture cleanup also failed: {cleanup}");
            }
            std::panic::resume_unwind(panic)
        }
    }
}

fn exercise_admission(root: &Path, created: FileIdentity) -> io::Result<()> {
    let storage = ProtectedStorage::open(root)?;
    assert!(storage.identity == created);
    assert_eq!(storage.inspect()?, Inspection::Empty);
    // ProtectedStorage retains the actual drive and root handles throughout the
    // existing writer call. The outer fixture owns the only teardown.
    let store = TestStore {
        root: root.to_owned(),
        pins: Vec::new(),
        cleanup_on_drop: false,
        held_lock: None,
    };
    drop(store.create_durable(LOCK, b"")?);
    let documents = managed_documents()?;
    let prior = documents
        .prior
        .each_ref()
        .map(|document| document.as_bytes());
    let next = documents
        .committed
        .each_ref()
        .map(|document| document.as_bytes());
    assert_ne!(prior[0], next[0]);
    assert_eq!(prior[1], next[1]);
    assert_ne!(prior[2], next[2]); // Companion hash envelope.
    for (name, bytes) in NAMES.into_iter().zip(prior) {
        drop(store.create_durable(name, bytes)?);
    }
    assert_snapshot(&storage, prior)?;
    let prior_profile = check_regular_file(&storage.open_record(PROFILE)?.unwrap())?;
    // Request a delete-capable handle without deleting or renaming anything.
    let delete_open = OpenOptions::new()
        .access_mode(DELETE)
        .share_mode(FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE)
        .custom_flags(FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT)
        .open(root);
    assert_eq!(
        delete_open.unwrap_err().raw_os_error(),
        Some(windows_sys::Win32::Foundation::ERROR_SHARING_VIOLATION as i32)
    );
    let report = store.commit(next, |_| Ok(()));
    assert!(
        matches!(report.outcome, Outcome::Committed) && report.artifact_errors.is_empty(),
        "{report:?}"
    );

    // A fresh open must identify the very same root while the first pin lives.
    let reopened = ProtectedStorage::open(root)?;
    assert!(reopened.identity == storage.identity);
    assert_snapshot(&storage, next)?;
    assert_snapshot(&reopened, next)?;

    let primary = root.join(PROFILE);
    let linked = root.join("profile.test-link");
    let identity = check_regular_file(&reopened.open_record(PROFILE)?.unwrap())?;
    assert!(identity != prior_profile);
    fs::hard_link(&primary, &linked)?;
    let error = reopened
        .inspect()
        .expect_err("multiply linked record admitted");
    assert_eq!(error.to_string(), "configuration entry has multiple links");
    for path in [&primary, &linked] {
        let file = File::open(path)?;
        let info = handle_information(&file)?;
        assert_eq!(info.nNumberOfLinks, 2);
        assert!(file_identity(&info) == identity);
        verify_security(&file, Policy::Record)?;
        assert_eq!(fs::read(path)?, next[0]);
    }
    for (name, bytes) in [(PREFERENCES, next[1]), (COMMIT, next[2])] {
        let record = reopened.open_record(name)?.unwrap();
        assert_eq!(read_file(&record)?.1, bytes);
    }
    assert!(!store.record_present()?);
    fs::remove_file(linked)?;
    assert_snapshot(&reopened, next)?;
    println!(
        "NTFS Administrator admission, managed-byte replacement, and untouched rejected record verified"
    );
    Ok(())
}

fn assert_snapshot(storage: &ProtectedStorage, expected: [&[u8]; 3]) -> io::Result<()> {
    assert_eq!(
        storage.inspect()?,
        Inspection::CommittedEnvelope {
            schema: crate::configuration::CURRENT_SCHEMA,
        }
    );
    for (name, bytes) in NAMES.into_iter().zip(expected) {
        let record = storage.open_record(name)?.unwrap();
        assert_eq!(read_file(&record)?.1, bytes);
    }
    Ok(())
}

fn cleanup_fixture(root: &Path, created: File) -> io::Result<()> {
    // No recursion, no retries, and no traversal into unexpected entries.
    use std::os::windows::fs::MetadataExt;
    // Retain the checked directory itself through the deletion request.
    let pin = OpenOptions::new()
        .access_mode(FILE_GENERIC_READ | DELETE)
        .share_mode(FILE_SHARE_READ | FILE_SHARE_WRITE)
        .custom_flags(FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT)
        .open(root)?;
    if file_identity(&directory_info(&created)?) != file_identity(&directory_info(&pin)?) {
        return Err(denied("fixture identity changed before cleanup"));
    }
    let mut files = Vec::new();
    for (index, entry) in fs::read_dir(root)?.enumerate() {
        let entry = entry?;
        let kind = entry.file_type()?;
        if index >= 16
            || !kind.is_file()
            || entry.metadata()?.file_attributes() & FILE_ATTRIBUTE_REPARSE_POINT != 0
            || !fixture_entry(&entry.file_name())
        {
            return Err(io::Error::other("fixture cleanup found unexpected entries"));
        }
        files.push(entry.path());
    }
    for file in files {
        fs::remove_file(file)?;
    }
    mark_for_deletion(&pin)?;
    drop(created);
    drop(pin);
    match fs::symlink_metadata(root) {
        Err(error) if error.kind() == io::ErrorKind::NotFound => {
            println!("NTFS fixture removed");
            Ok(())
        }
        other => Err(io::Error::other(format!(
            "fixture removal could not be verified: {other:?}"
        ))),
    }
}

fn fixture_entry(name: &OsStr) -> bool {
    let Some(name) = name.to_str() else {
        return false;
    };
    NAMES.contains(&name)
        || matches!(name, LOCK | RECOVERY_REQUIRED | "profile.test-link")
        || name
            .split_once(".transaction-")
            .and_then(|(base, rest)| rest.split_once('.').map(|(id, kind)| (base, id, kind)))
            .is_some_and(|(base, id, kind)| {
                NAMES.contains(&base)
                    && crate::valid_dev_id(id)
                    && matches!(kind, "stage" | "backup" | "restore")
            })
}

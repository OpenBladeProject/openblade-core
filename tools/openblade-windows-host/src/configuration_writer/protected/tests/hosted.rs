use super::*;
use crate::configuration_writer::{
    LOCK, Outcome, TestStore, check_regular_file, pin_directory, transaction_id,
};
use windows_sys::Win32::{
    Security::SECURITY_ATTRIBUTES,
    Storage::FileSystem::{CreateDirectoryW, FILE_SHARE_DELETE, GetVolumeInformationByHandleW},
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
    let (exact_drive, _) = split_path(&root)?;
    if exact_drive != drive {
        return Err(denied("fixture must be a direct child of the system drive"));
    }

    // The only ancestor is opened and checked before any fixture is created.
    let wide_drive: Vec<u16> = drive.as_os_str().encode_wide().chain([0]).collect();
    // SAFETY: wide_drive is a NUL-terminated drive root.
    if unsafe { GetDriveTypeW(wide_drive.as_ptr()) } != DRIVE_FIXED {
        return Err(denied("fixture drive must be fixed"));
    }
    let drive_pin = pin_directory(&drive)?;
    check_drive(&drive_pin, &drive)?;
    directory_info(&drive_pin)?;
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
    if !filesystem.starts_with(&[78, 84, 70, 83, 0]) {
        return Err(io::Error::other("fixture drive must be NTFS"));
    }

    let setup =
        descriptor(&std::env::var("OPENBLADE_HOSTED_SETUP_SDDL").map_err(io::Error::other)?);
    if accepts_descriptor(&setup, Policy::Root) != Some(true) {
        return Err(denied("Setup descriptor does not match Rust admission"));
    }
    let mut setup = setup;
    let security = SECURITY_ATTRIBUTES {
        nLength: size_of::<SECURITY_ATTRIBUTES>() as u32,
        lpSecurityDescriptor: setup.as_mut_ptr().cast(),
        bInheritHandle: 0,
    };
    let path: Vec<u16> = root.as_os_str().encode_wide().chain([0]).collect();
    // SAFETY: path is NUL-terminated and security points to the live Setup
    // descriptor. CreateDirectoryW fails if the generated leaf already exists.
    if unsafe { CreateDirectoryW(path.as_ptr(), &security) } == 0 {
        return Err(io::Error::last_os_error());
    }

    let result = std::panic::catch_unwind(|| exercise_admission(&root));
    let cleanup = cleanup_fixture(&root);
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

fn exercise_admission(root: &Path) -> io::Result<()> {
    let storage = ProtectedStorage::open(root)?;
    assert_eq!(storage.inspect()?, Inspection::Empty);
    let store = TestStore {
        root: root.to_owned(),
        pins: Vec::new(),
        cleanup_on_drop: false,
        held_lock: None,
    };
    drop(store.create_durable(LOCK, b"")?);
    let fixture: serde_json::Value = serde_json::from_str(include_str!(
        "../../../../../../tests/OpenBlade.Service.Tests/WindowsReplay/v1/configuration-aggregate.json"
    ))?;
    let documents = |key| -> [&[u8]; 3] {
        std::array::from_fn(|index| fixture[key][index].as_str().unwrap().as_bytes())
    };
    let prior = documents("prior");
    let next = documents("committed");
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
        .access_mode(windows_sys::Win32::Storage::FileSystem::DELETE)
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
        assert!(super::super::super::file_identity(&info) == identity);
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

fn cleanup_fixture(root: &Path) -> io::Result<()> {
    // No recursion, no retries, and no traversal into unexpected entries.
    use std::os::windows::fs::MetadataExt;
    let pin = pin_directory(root)?;
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
        fs::remove_file(entry.path())?;
    }
    drop(pin);
    fs::remove_dir(root)?;
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

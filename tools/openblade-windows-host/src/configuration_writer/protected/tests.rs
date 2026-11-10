use super::*;
use crate::configuration::{COMMIT, PREFERENCES, PROFILE, Reason};
use std::fs;
use windows_sys::Win32::Security::Authorization::ConvertStringSecurityDescriptorToSecurityDescriptorW;

const ROOT: &str = "O:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)";

struct TestDescriptor {
    allocation: LocalDescriptor,
    size: usize,
}

impl TestDescriptor {
    fn bytes(&self) -> &[u8] {
        // SAFETY: Windows supplied the size and the allocation is owned by self.
        unsafe { slice::from_raw_parts(self.allocation.0.cast(), self.size) }
    }
}

fn local_descriptor(sddl: &str) -> io::Result<TestDescriptor> {
    let wide: Vec<u16> = sddl.encode_utf16().chain([0]).collect();
    let mut descriptor = ptr::null_mut();
    let mut size = 0;
    // SAFETY: wide is NUL-terminated and both out parameters are writable.
    if unsafe {
        ConvertStringSecurityDescriptorToSecurityDescriptorW(
            wide.as_ptr(),
            1,
            &mut descriptor,
            &mut size,
        )
    } == 0
    {
        return Err(io::Error::last_os_error());
    }
    if descriptor.is_null() {
        return Err(io::Error::other("Windows returned no security descriptor"));
    }
    Ok(TestDescriptor {
        allocation: LocalDescriptor(descriptor),
        size: size as usize,
    })
}

fn descriptor(sddl: &str) -> Vec<u8> {
    local_descriptor(sddl)
        .expect("invalid test SDDL")
        .bytes()
        .to_vec()
}

#[test]
fn root_requires_the_applied_setup_acl() {
    assert_eq!(
        accepts_descriptor(&descriptor(ROOT), Policy::Root),
        Some(true)
    );
    for invalid in [
        "O:SYD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)", // eligible before setup, wrong applied owner
        "O:BUD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)",
        "O:BAD:(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)",
        "O:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;SY)",
        "O:BAD:P(A;OICI;FA;;;SY)(A;OICI;FR;;;BA)",
        "O:BAD:P(A;OICI;FA;;;SY)(A;OI;FA;;;BA)",
        "O:BAD:P(A;OICI;FA;;;SY)(A;OICIID;FA;;;BA)",
        "O:BAD:P(A;OICI;FA;;;SY)(D;OICI;FA;;;BA)",
        "O:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;;FR;;;BU)",
        "O:BAD:P",
        "O:BA",
    ] {
        assert_ne!(
            accepts_descriptor(&descriptor(invalid), Policy::Root),
            Some(true),
            "{invalid}"
        );
    }
}

#[test]
fn records_allow_safe_inheritance_but_reject_untrusted_mutation() {
    for trusted in [
        "O:SYD:AI(A;ID;FA;;;SY)(A;ID;FA;;;BA)",
        "O:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FRFX;;;BU)",
        "O:BAD:(D;;FW;;;BU)(A;;GRGX;;;BU)(A;;FA;;;SY)",
    ] {
        assert_eq!(
            accepts_descriptor(&descriptor(trusted), Policy::Record),
            Some(true),
            "{trusted}"
        );
    }
    for invalid in [
        "O:BUD:P(A;;FA;;;SY)(A;;FA;;;BA)",
        "O:SYD:(A;ID;FA;;;SY)(A;ID;FW;;;BU)",
        "O:BAD:(A;;GA;;;WD)",
        "O:BAD:(A;;GW;;;WD)",
        "O:BAD:(A;;WD;;;BU)",
        "O:BAD:(A;;WO;;;BU)",
        "O:BAD:(A;;SD;;;BU)",
        "O:BAD:(A;;0x40;;;BU)",       // delete child
        "O:BAD:(A;;0x100;;;BU)",      // write attributes
        "O:BAD:(A;;0x10;;;BU)",       // write extended attributes
        "O:BAD:(A;;0x02000000;;;BU)", // unmapped/unknown grant
        "O:BAD:(A;IO;FR;;;BU)",
        "O:BAD:(OA;;FA;00112233-4455-6677-8899-aabbccddeeff;;BU)",
        "O:BAD:NO_ACCESS_CONTROL",
    ] {
        assert_ne!(
            accepts_descriptor(&descriptor(invalid), Policy::Record),
            Some(true),
            "{invalid}"
        );
    }
}

#[test]
fn ancestors_allow_child_creation_without_allowing_redirection() {
    let trusted_installer = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
    let sddl =
        format!("O:{trusted_installer}D:(A;OICI;FA;;;SY)(A;CIIO;GA;;;CO)(A;;0x4;;;BU)(A;;FR;;;BU)");
    let bytes = descriptor(&sddl);
    assert_eq!(accepts_descriptor(&bytes, Policy::Ancestor), Some(true));
    assert_ne!(accepts_descriptor(&bytes, Policy::Root), Some(true));
    assert_ne!(accepts_descriptor(&bytes, Policy::Record), Some(true));
    for invalid in [
        "O:BUD:(A;;FR;;;BU)",
        "O:SYD:(A;;0x40;;;BU)",
        "O:SYD:(A;CI;SD;;;BU)",
        "O:SYD:(A;ID;WD;;;BU)",
        "O:SYD:(A;;0x100;;;BU)",
        "O:SYD:(A;;GW;;;BU)",
    ] {
        assert_ne!(
            accepts_descriptor(&descriptor(invalid), Policy::Ancestor),
            Some(true),
            "{invalid}"
        );
    }
}

#[test]
fn malformed_descriptors_fail_closed() {
    let valid = descriptor(ROOT);
    for end in 0..valid.len() {
        assert_ne!(
            accepts_descriptor(&valid[..end], Policy::Root),
            Some(true),
            "length {end}"
        );
    }
    let acl = dword(&valid, 16).unwrap() as usize;
    for (offset, bad) in [(0, 0), (4, 0xff), (16, 0), (acl + 4, 0xff), (acl + 10, 0)] {
        let mut malformed = valid.clone();
        malformed[offset] = bad;
        assert_ne!(
            accepts_descriptor(&malformed, Policy::Root),
            Some(true),
            "offset {offset}"
        );
    }
}

#[test]
fn exact_local_path_and_absence_are_required() {
    assert!(split_path(Path::new(r"C:\private\OpenBlade")).is_ok());
    for path in [
        r"C:\",
        r"C:OpenBlade",
        r"\OpenBlade",
        r"\\host\share\OpenBlade",
        r"\\?\C:\private\OpenBlade",
        r"C:\private\..\OpenBlade",
        r"C:\private\.\OpenBlade",
        r"C:/private/OpenBlade",
        r"C:\private\OpenBlade:ads",
        r"C:\private\OpenBlade.",
        r"C:\private\OpenBlade ",
        r"C:\NUL.txt\OpenBlade",
        r"C:\private\CoM¹",
        r"C:\private\CON .txt",
    ] {
        assert!(split_path(Path::new(path)).is_err(), "{path}");
    }
    assert!(missing_leaf(&io::Error::from_raw_os_error(2)));
    for code in [3, 5, 32, 303, 4390, 1920] {
        assert!(!missing_leaf(&io::Error::from_raw_os_error(code)), "{code}");
    }
}

#[test]
fn recovery_presence_short_circuits_and_late_presence_fails() {
    let blocked = inspect_records(
        |name| {
            assert_eq!(name, RECOVERY_REQUIRED);
            Ok(Some(()))
        },
        |_| panic!("recovery contents must not be read"),
    )
    .unwrap();
    assert_eq!(blocked, Inspection::Preserve(Reason::RecoveryRequired));

    let mut script = [
        RECOVERY_REQUIRED,
        PROFILE,
        PREFERENCES,
        COMMIT,
        RECOVERY_REQUIRED,
    ]
    .into_iter();
    let mut probes = 0;
    let late = inspect_records(
        |name| {
            assert_eq!(Some(name), script.next());
            probes += 1;
            Ok((probes == 5).then_some(()))
        },
        |_| panic!("no aggregate file is present"),
    );
    assert!(late.is_err());
    assert_eq!(script.next(), None);
}

#[test]
fn bad_open_prevents_every_read_and_failed_read_never_publishes() {
    for blocked in [RECOVERY_REQUIRED, PROFILE, PREFERENCES, COMMIT] {
        let result = inspect_records(
            |name| {
                if name == blocked {
                    Err(io::Error::from_raw_os_error(5))
                } else {
                    Ok((name != RECOVERY_REQUIRED).then_some(()))
                }
            },
            |_| panic!("no file may be read until all opens succeed"),
        );
        assert_eq!(result.unwrap_err().raw_os_error(), Some(5));
    }
    let result = inspect_records(
        |name| Ok((name == PROFILE).then_some(())),
        |_| Err(io::Error::other("scripted read failure")),
    );
    assert!(result.is_err());
    assert_eq!(
        inspect_records(|_| Ok(None::<()>), |_| unreachable!()).unwrap(),
        Inspection::Empty
    );
}

#[test]
fn live_private_fixtures_refuse_unprotected_roots_and_records() {
    let fixture = super::super::TestStore::new().unwrap();
    let root = &fixture.root;
    let expected = "untrusted configuration security descriptor";
    assert_eq!(
        ProtectedStorage::open(root).err().unwrap().to_string(),
        expected
    );
    let missing = root.join("absent");
    assert!(ProtectedStorage::open(&missing).is_err());
    assert!(!missing.exists());

    // Exercise the record open in isolation without granting the fixture root
    // production admission or applying any security descriptor to disk.
    let parent = super::super::pin_directory(root.parent().unwrap()).unwrap();
    let handle = open_directory(&parent, root.file_name().unwrap()).unwrap();
    let candidate = ProtectedStorage {
        identity: super::super::file_identity(&directory_info(&handle).unwrap()),
        root: handle,
        ancestors: Vec::new(),
    };
    assert!(candidate.open_record(PROFILE).unwrap().is_none());
    fs::write(root.join(PROFILE), b"private fixture").unwrap();
    assert_eq!(
        candidate.open_record(PROFILE).unwrap_err().to_string(),
        expected
    );
    fs::hard_link(root.join(PROFILE), root.join(PREFERENCES)).unwrap();
    assert!(
        candidate
            .open_record(PREFERENCES)
            .unwrap_err()
            .to_string()
            .contains("multiple links")
    );
    fs::remove_file(root.join(PREFERENCES)).unwrap();
    fs::create_dir(root.join(RECOVERY_REQUIRED)).unwrap();
    assert!(
        candidate
            .open_record(RECOVERY_REQUIRED)
            .unwrap_err()
            .to_string()
            .contains("plain file")
    );
    // The exact admission open must permit the existing writer's rename while
    // the directory remains pinned. Only the fixture below is modified.
    let stage = root.join("private.stage");
    fs::write(&stage, b"replacement fixture").unwrap();
    super::super::move_file(&stage, &root.join(PROFILE), true).unwrap();
    assert_eq!(
        fs::read(root.join(PROFILE)).unwrap(),
        b"replacement fixture"
    );
    drop(candidate);
    fs::remove_dir(root.join(RECOVERY_REQUIRED)).unwrap();
    assert_eq!(
        fs::read(root.join(PROFILE)).unwrap(),
        b"replacement fixture"
    );
}

mod hosted;

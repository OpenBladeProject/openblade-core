//! One local per-user directory and one precreated file. Relative atomic
//! creation returns their original handles, so acquisition never adopts a
//! preexisting object or follows a substituted final component.
#![allow(unsafe_code)]
use super::{Failure, MAX_BYTES, check};
use std::{
    ffi::{OsStr, OsString, c_void},
    fs::File,
    io::{self, Read},
    os::windows::{
        ffi::{OsStrExt, OsStringExt},
        fs::{MetadataExt, OpenOptionsExt},
        io::{AsRawHandle, FromRawHandle},
    },
    path::{Component, Path, PathBuf, Prefix},
    ptr::{null, null_mut},
    slice,
    sync::atomic::AtomicBool,
    time::Instant,
};
use windows_sys::{
    Wdk::{
        Foundation::OBJECT_ATTRIBUTES,
        Storage::FileSystem::{
            FILE_CREATE, FILE_DIRECTORY_FILE, FILE_NON_DIRECTORY_FILE, FILE_OPEN,
            FILE_OPEN_REPARSE_POINT, FILE_SYNCHRONOUS_IO_NONALERT, NtCreateFile,
        },
    },
    Win32::{
        Foundation::{
            GENERIC_READ, INVALID_HANDLE_VALUE, LocalFree, OBJ_CASE_INSENSITIVE, OBJ_DONT_REPARSE,
            RtlNtStatusToDosError, UNICODE_STRING,
        },
        Security::{
            Authorization::{
                ConvertSidToStringSidW, ConvertStringSecurityDescriptorToSecurityDescriptorW,
                SDDL_REVISION_1,
            },
            GetTokenInformation, SECURITY_MAX_SID_SIZE, TOKEN_QUERY, TOKEN_USER, TokenUser,
        },
        Storage::FileSystem::{
            BY_HANDLE_FILE_INFORMATION, DELETE, FILE_ATTRIBUTE_DIRECTORY,
            FILE_ATTRIBUTE_REPARSE_POINT, FILE_DISPOSITION_INFO, FILE_FLAG_BACKUP_SEMANTICS,
            FILE_FLAG_OPEN_REPARSE_POINT, FILE_READ_ATTRIBUTES, FILE_READ_DATA, FILE_SHARE_DELETE,
            FILE_SHARE_READ, FILE_SHARE_WRITE, FileDispositionInfo, GetDriveTypeW,
            GetFileInformationByHandle, GetFinalPathNameByHandleW, GetVolumeInformationByHandleW,
            ReOpenFile, SYNCHRONIZE, SetFileInformationByHandle,
        },
        System::{
            Com::{CoCreateGuid, CoTaskMemFree},
            IO::IO_STATUS_BLOCK,
            Threading::{GetCurrentProcess, OpenProcessToken},
        },
        UI::Shell::{FOLDERID_LocalAppData, KF_FLAG_DONT_VERIFY, SHGetKnownFolderPath},
    },
    core::GUID,
};

const REPORT: &str = "battery-report.xml";
const ALL_SHARES: u32 = FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE;

pub(super) struct Attempt {
    pub(super) output: PathBuf,
    file: File,
    directory: File,
    _ancestors: Vec<File>,
}

impl Attempt {
    pub(super) fn create() -> Result<Self, Failure> {
        let root = local_data().map_err(|_| Failure::Scratch)?;
        Self::in_directory(&root)
    }

    fn in_directory(root: &Path) -> Result<Self, Failure> {
        let ancestors = pin_directories(root).map_err(|_| Failure::Scratch)?;
        let parent = ancestors.last().ok_or(Failure::Scratch)?;
        let sd = private_descriptor().map_err(|_| Failure::Scratch)?;
        let mut id = GUID::default();
        // SAFETY: valid GUID output.
        if unsafe { CoCreateGuid(&mut id) } < 0 {
            return Err(Failure::Scratch);
        }
        let name = format!(
            "OpenBlade.BatteryReport.{:08x}{:04x}{:04x}{:016x}",
            id.data1,
            id.data2,
            id.data3,
            u64::from_be_bytes(id.data4)
        );
        let directory = relative(
            parent,
            OsStr::new(&name),
            FILE_READ_ATTRIBUTES | DELETE,
            FILE_SHARE_READ,
            FILE_CREATE,
            FILE_DIRECTORY_FILE,
            Some(&sd),
        )
        .map_err(|_| Failure::Scratch)?;
        let output = root.join(name).join(REPORT);
        // A data-read handle participates in Windows share checking. Permit
        // writing, deny delete sharing, and retain the actual output object.
        let file = match relative(
            &directory,
            OsStr::new(REPORT),
            FILE_READ_DATA | FILE_READ_ATTRIBUTES,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            FILE_CREATE,
            FILE_NON_DIRECTORY_FILE,
            Some(&sd),
        ) {
            Ok(file) => file,
            Err(_) => {
                dispose(&directory).map_err(|_| Failure::Cleanup)?;
                return Err(Failure::Scratch);
            }
        };
        Ok(Self {
            output,
            file,
            directory,
            _ancestors: ancestors,
        })
    }

    pub(super) fn read(
        &self,
        cancelled: &AtomicBool,
        deadline: Instant,
    ) -> Result<Vec<u8>, Failure> {
        check(cancelled, deadline)?;
        // Read the originally created object, with new writers excluded.
        let mut reader =
            reopen(&self.file, GENERIC_READ, FILE_SHARE_READ).map_err(|_| Failure::Output)?;
        plain_identity(&reader).map_err(|_| Failure::Output)?;
        let n = reader.metadata().map_err(|_| Failure::Output)?.len();
        if !(1..=MAX_BYTES).contains(&n) {
            return Err(Failure::Output);
        }
        let mut bytes = vec![0; n as usize];
        for chunk in bytes.chunks_mut(16 * 1024) {
            check(cancelled, deadline)?;
            reader.read_exact(chunk).map_err(|_| Failure::Output)?;
        }
        if reader.read(&mut [0]).map_err(|_| Failure::Output)? != 0 {
            return Err(Failure::Output);
        }
        Ok(bytes)
    }

    pub(super) fn clean(self) -> Result<(), Failure> {
        // Keep the original file object alive (prevents file-ID reuse) as its
        // name pin is released to obtain DELETE access. Only the original
        // single-link object may be unlinked from the original directory.
        let keep =
            reopen(&self.file, FILE_READ_ATTRIBUTES, ALL_SHARES).map_err(|_| Failure::Cleanup)?;
        let identity = plain_identity(&keep).map_err(|_| Failure::Cleanup)?;
        drop(self.file);
        let delete = relative(
            &self.directory,
            OsStr::new(REPORT),
            DELETE | FILE_READ_ATTRIBUTES,
            FILE_SHARE_READ,
            FILE_OPEN,
            FILE_NON_DIRECTORY_FILE,
            None,
        )
        .map_err(|_| Failure::Cleanup)?;
        if plain_identity(&delete).map_err(|_| Failure::Cleanup)? != identity {
            return Err(Failure::Cleanup);
        }
        dispose(&delete).map_err(|_| Failure::Cleanup)?;
        drop(delete);
        drop(keep);
        dispose(&self.directory).map_err(|_| Failure::Cleanup)
    }
}

pub(crate) fn plain_identity(file: &File) -> io::Result<(u32, u32, u32)> {
    let mut info = BY_HANDLE_FILE_INFORMATION::default();
    // SAFETY: live handle, correctly sized typed output.
    if unsafe { GetFileInformationByHandle(file.as_raw_handle(), &mut info) } == 0 {
        return Err(io::Error::last_os_error());
    }
    if info.dwFileAttributes & (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT) != 0
        || info.nNumberOfLinks != 1
    {
        return Err(io::Error::other("expected plain single-link report"));
    }
    Ok((
        info.dwVolumeSerialNumber,
        info.nFileIndexHigh,
        info.nFileIndexLow,
    ))
}

fn dispose(file: &File) -> io::Result<()> {
    let info = FILE_DISPOSITION_INFO { DeleteFile: true };
    // SAFETY: disposition applies only to this retained handle.
    if unsafe {
        SetFileInformationByHandle(
            file.as_raw_handle(),
            FileDispositionInfo,
            (&raw const info).cast(),
            size_of_val(&info) as u32,
        )
    } == 0
    {
        Err(io::Error::last_os_error())
    } else {
        Ok(())
    }
}

fn reopen(file: &File, access: u32, share: u32) -> io::Result<File> {
    // SAFETY: independent owned handle to the same file object on success.
    let raw = unsafe {
        ReOpenFile(
            file.as_raw_handle(),
            access,
            share,
            FILE_FLAG_OPEN_REPARSE_POINT,
        )
    };
    if raw == INVALID_HANDLE_VALUE {
        return Err(io::Error::last_os_error());
    }
    Ok(unsafe { File::from_raw_handle(raw) })
}

pub(crate) fn relative(
    parent: &File,
    name: &OsStr,
    access: u32,
    share: u32,
    disposition: u32,
    kind: u32,
    security: Option<&Allocation>,
) -> io::Result<File> {
    let mut units: Vec<_> = name.encode_wide().collect();
    let length = u16::try_from(units.len() * 2).map_err(io::Error::other)?;
    let mut name = UNICODE_STRING {
        Length: length,
        MaximumLength: length,
        Buffer: units.as_mut_ptr(),
    };
    let attributes = OBJECT_ATTRIBUTES {
        Length: size_of::<OBJECT_ATTRIBUTES>() as u32,
        RootDirectory: parent.as_raw_handle(),
        ObjectName: &mut name,
        Attributes: OBJ_CASE_INSENSITIVE | OBJ_DONT_REPARSE,
        SecurityDescriptor: security.map_or(null(), |sd| sd.0.cast()),
        ..Default::default()
    };
    let mut raw = null_mut();
    let mut result = IO_STATUS_BLOCK::default();
    // SAFETY: retained parent, single component, live length-delimited name,
    // optional owned security descriptor and correctly sized synchronous outputs.
    let status = unsafe {
        NtCreateFile(
            &mut raw,
            access | SYNCHRONIZE,
            &attributes,
            &mut result,
            null(),
            if kind == FILE_DIRECTORY_FILE {
                FILE_ATTRIBUTE_DIRECTORY
            } else {
                0
            },
            share,
            disposition,
            kind | FILE_OPEN_REPARSE_POINT | FILE_SYNCHRONOUS_IO_NONALERT,
            null(),
            0,
        )
    };
    if status < 0 {
        return Err(io::Error::from_raw_os_error(
            unsafe { RtlNtStatusToDosError(status) } as i32,
        ));
    }
    Ok(unsafe { File::from_raw_handle(raw) })
}

pub(crate) fn pin_directories(path: &Path) -> io::Result<Vec<File>> {
    let mut components = path.components();
    let Some(Component::Prefix(prefix)) = components.next() else {
        return Err(invalid());
    };
    if !matches!(prefix.kind(), Prefix::Disk(_)) || components.next() != Some(Component::RootDir) {
        return Err(invalid());
    }
    let mut exact: PathBuf = [Component::Prefix(prefix), Component::RootDir]
        .into_iter()
        .collect();
    let drive: Vec<_> = exact.as_os_str().encode_wide().chain([0]).collect();
    // DRIVE_FIXED = 3. No remote volumes or environment-supplied scratch roots.
    if unsafe { GetDriveTypeW(drive.as_ptr()) } != 3 {
        return Err(invalid());
    }
    let root = File::options()
        .access_mode(FILE_READ_DATA | FILE_READ_ATTRIBUTES)
        .share_mode(FILE_SHARE_READ | FILE_SHARE_WRITE)
        .custom_flags(FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT)
        .open(&exact)?;
    let mut actual = [0u16; 16];
    // SAFETY: live root and valid UTF-16 output buffer. Refuse SUBST roots.
    let n = unsafe {
        GetFinalPathNameByHandleW(
            root.as_raw_handle(),
            actual.as_mut_ptr(),
            actual.len() as u32,
            0,
        )
    } as usize;
    if !actual
        .get(..n)
        .and_then(|a| String::from_utf16(a).ok())
        .is_some_and(|a| a.eq_ignore_ascii_case(&format!(r"\\?\{}", exact.display())))
    {
        return Err(invalid());
    }
    let mut flags = 0;
    // SAFETY: only the volume capability flags are requested.
    if unsafe {
        GetVolumeInformationByHandleW(
            root.as_raw_handle(),
            null_mut(),
            0,
            null_mut(),
            null_mut(),
            &mut flags,
            null_mut(),
            0,
        )
    } == 0
        || flags & 8 == 0
    {
        return Err(invalid()); // FILE_PERSISTENT_ACLS is required for the attempt DACL.
    }
    let mut held = vec![root];
    for component in components {
        let Component::Normal(name) = component else {
            return Err(invalid());
        };
        let text = name.to_str().ok_or_else(invalid)?;
        if text.ends_with([' ', '.'])
            || text.chars().any(|ch| ch < ' ' || r#"<>:"|?*"#.contains(ch))
        {
            return Err(invalid());
        }
        exact.push(name);
        // These shared directories can have legitimate ADD_FILE writers. Hold
        // a share-participating data handle and exclude deletion/rename.
        let dir = relative(
            held.last().expect("retained parent"),
            name,
            FILE_READ_DATA | FILE_READ_ATTRIBUTES,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            FILE_OPEN,
            FILE_DIRECTORY_FILE,
            None,
        )?;
        let info = dir.metadata()?;
        if !info.is_dir() || info.file_attributes() & FILE_ATTRIBUTE_REPARSE_POINT != 0 {
            return Err(invalid());
        }
        held.push(dir);
    }
    if exact.as_os_str() != path.as_os_str() {
        return Err(invalid());
    }
    Ok(held)
}

fn invalid() -> io::Error {
    io::Error::other("invalid local battery report path")
}

pub(crate) fn local_data() -> io::Result<PathBuf> {
    let mut path = null_mut();
    // SAFETY: the known-folder API returns a task-allocated, NUL-terminated path.
    let result = unsafe {
        SHGetKnownFolderPath(
            &FOLDERID_LocalAppData,
            KF_FLAG_DONT_VERIFY as u32,
            null_mut(),
            &mut path,
        )
    };
    let folder = if result >= 0 && !path.is_null() {
        Ok(PathBuf::from(os_string(path)))
    } else {
        Err(invalid())
    };
    unsafe { CoTaskMemFree(path.cast()) };
    folder
}

fn os_string(text: *const u16) -> OsString {
    // Only Windows-allocated, NUL-terminated known-folder or SID strings reach here.
    unsafe {
        let n = (0..)
            .find(|&i| *text.add(i) == 0)
            .expect("Windows string terminator");
        OsString::from_wide(slice::from_raw_parts(text, n))
    }
}

pub(crate) struct Allocation(*mut c_void);
impl Drop for Allocation {
    fn drop(&mut self) {
        unsafe {
            LocalFree(self.0);
        }
    }
}

pub(crate) fn private_descriptor() -> io::Result<Allocation> {
    let mut raw = null_mut();
    // SAFETY: open caller's process token for a typed user query only.
    if unsafe { OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &mut raw) } == 0 {
        return Err(io::Error::last_os_error());
    }
    let token = unsafe { std::os::windows::io::OwnedHandle::from_raw_handle(raw) };
    let mut buffer = [0usize;
        (size_of::<TOKEN_USER>() + SECURITY_MAX_SID_SIZE as usize).div_ceil(size_of::<usize>())];
    let mut length = 0;
    let mut string = null_mut();
    // SAFETY: TokenUser points inside the live aligned allocation. Windows
    // returns an owned, terminated SID string.
    unsafe {
        if GetTokenInformation(
            token.as_raw_handle(),
            TokenUser,
            buffer.as_mut_ptr().cast(),
            size_of_val(&buffer) as u32,
            &mut length,
        ) == 0
            || ConvertSidToStringSidW(
                (*buffer.as_ptr().cast::<TOKEN_USER>()).User.Sid,
                &mut string,
            ) == 0
        {
            return Err(io::Error::last_os_error());
        }
    }
    let _sid_alloc = Allocation(string.cast());
    let sid = os_string(string);
    let sid = sid.to_str().ok_or_else(invalid)?;
    let sddl: Vec<_> = format!("O:{sid}D:P(A;;FA;;;{sid})")
        .encode_utf16()
        .chain([0])
        .collect();
    let mut sd = null_mut();
    // SAFETY: Windows allocates the descriptor from the terminated SDDL.
    if unsafe {
        ConvertStringSecurityDescriptorToSecurityDescriptorW(
            sddl.as_ptr(),
            SDDL_REVISION_1,
            &mut sd,
            null_mut(),
        )
    } == 0
    {
        return Err(io::Error::last_os_error());
    }
    Ok(Allocation(sd))
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::{fs, time::Duration};
    fn test_root() -> PathBuf {
        std::env::temp_dir().components().collect()
    }

    #[test]
    fn shared_ancestor_writer_coexists_in_either_order_and_pin_prevents_replacement() {
        use windows_sys::Win32::{
            Foundation::ERROR_SHARING_VIOLATION, Storage::FileSystem::FILE_ADD_FILE,
        };

        // Generate a single fresh test directory with collision refusal. Its
        // ordinary sibling name is used to test rename with/without the pin.
        let mut id = GUID::default();
        assert!(unsafe { CoCreateGuid(&mut id) } >= 0);
        let name = format!(
            "OpenBlade.BatteryTest.{:08x}{:04x}{:04x}{:016x}",
            id.data1,
            id.data2,
            id.data3,
            u64::from_be_bytes(id.data4)
        );
        let shared = test_root().join(name);
        let moved = test_root().join(format!(
            "{}.moved",
            shared.file_name().unwrap().to_string_lossy()
        ));
        fs::create_dir(&shared).unwrap();
        let open_writer = || {
            File::options()
                .access_mode(FILE_ADD_FILE)
                .share_mode(ALL_SHARES)
                .custom_flags(FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT)
                .open(&shared)
                .unwrap()
        };
        for writer_first in [true, false] {
            let before = writer_first.then(open_writer);
            let pins = pin_directories(&shared).unwrap();
            let after = (!writer_first).then(open_writer);
            let child = shared.join("child");
            fs::write(&child, b"x").unwrap();
            fs::remove_file(child).unwrap();
            drop((before, after));
            for refused in [fs::rename(&shared, &moved), fs::remove_dir(&shared)] {
                assert_eq!(
                    refused.unwrap_err().raw_os_error(),
                    Some(ERROR_SHARING_VIOLATION as i32)
                );
            }
            drop(pins);
        }
        fs::rename(&shared, &moved).unwrap();
        fs::remove_dir(&moved).unwrap();
    }

    #[test]
    fn precreated_report_is_pinned_bounded_and_cleaned_by_identity() {
        let root = test_root();
        let attempt = Attempt::in_directory(&root).unwrap();
        let dir = attempt.output.parent().unwrap().to_owned();
        let cancelled = AtomicBool::new(false);
        let deadline = Instant::now() + Duration::from_secs(10);
        assert_eq!(attempt.read(&cancelled, deadline), Err(Failure::Output));
        fs::write(&attempt.output, b"<BatteryReport/>").unwrap();
        assert!(fs::remove_file(&attempt.output).is_err());
        assert_eq!(
            attempt.read(&cancelled, deadline).unwrap(),
            b"<BatteryReport/>"
        );
        File::options()
            .write(true)
            .open(&attempt.output)
            .unwrap()
            .set_len(MAX_BYTES + 1)
            .unwrap();
        assert_eq!(attempt.read(&cancelled, deadline), Err(Failure::Output));
        attempt.clean().unwrap();
        assert!(!dir.exists());
    }
    #[test]
    fn cleanup_preserves_unowned_neighbor_inside_attempt() {
        let attempt = Attempt::in_directory(&test_root()).unwrap();
        let dir = attempt.output.parent().unwrap().to_owned();
        let other = dir.join("other");
        fs::write(&other, b"untouched").unwrap();
        assert_eq!(attempt.clean(), Err(Failure::Cleanup));
        assert_eq!(fs::read(&other).unwrap(), b"untouched");
        fs::remove_file(other).unwrap();
        fs::remove_dir(dir).unwrap();
    }
}

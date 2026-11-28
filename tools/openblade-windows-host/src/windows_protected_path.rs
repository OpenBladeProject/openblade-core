//! Shared retained-path and descriptor checks for local Windows storage.
#![allow(unsafe_code)]

use std::{
    ffi::OsStr,
    fs::{File, OpenOptions},
    io,
    os::windows::{
        ffi::OsStrExt,
        fs::{MetadataExt, OpenOptionsExt},
        io::AsRawHandle,
    },
    path::{Component, Path, PathBuf, Prefix},
    ptr, slice,
};
use windows_sys::{
    Wdk::Storage::FileSystem::{FILE_DIRECTORY_FILE, FILE_OPEN, FILE_OPEN_REPARSE_POINT},
    Win32::{
        Foundation::{ERROR_FILE_NOT_FOUND, GENERIC_EXECUTE, GENERIC_READ, HLOCAL, LocalFree},
        Security::{
            Authorization::{GetSecurityInfo, SE_FILE_OBJECT},
            DACL_SECURITY_INFORMATION, GetSecurityDescriptorLength, OWNER_SECURITY_INFORMATION,
            SE_DACL_PRESENT, SE_DACL_PROTECTED, SE_SELF_RELATIVE,
        },
        Storage::FileSystem::{
            BY_HANDLE_FILE_INFORMATION, FILE_ADD_SUBDIRECTORY, FILE_ALL_ACCESS,
            FILE_ATTRIBUTE_DIRECTORY, FILE_ATTRIBUTE_REPARSE_POINT, FILE_FLAG_BACKUP_SEMANTICS,
            FILE_FLAG_OPEN_REPARSE_POINT, FILE_GENERIC_EXECUTE, FILE_GENERIC_READ, FILE_SHARE_READ,
            FILE_SHARE_WRITE, FILE_TYPE_DISK, GetDriveTypeW, GetFileInformationByHandle,
            GetFileType, GetFinalPathNameByHandleW,
        },
        System::WindowsProgramming::DRIVE_FIXED,
    },
};

pub(crate) fn open_relative(
    parent: &File,
    name: &OsStr,
    access: u32,
    share: u32,
    disposition: u32,
    options: u32,
) -> io::Result<File> {
    open_relative_with_security(parent, name, access, share, disposition, options, None)
}

pub(crate) fn open_relative_with_security(
    parent: &File,
    name: &OsStr,
    access: u32,
    share: u32,
    disposition: u32,
    options: u32,
    security: Option<&LocalDescriptor>,
) -> io::Result<File> {
    use std::{os::windows::io::FromRawHandle, ptr};
    use windows_sys::{
        Wdk::{
            Foundation::OBJECT_ATTRIBUTES,
            Storage::FileSystem::{
                FILE_DIRECTORY_FILE, FILE_SYNCHRONOUS_IO_NONALERT, NtCreateFile,
            },
        },
        Win32::{
            Foundation::{
                OBJ_CASE_INSENSITIVE, OBJ_DONT_REPARSE, RtlNtStatusToDosError, UNICODE_STRING,
            },
            System::IO::IO_STATUS_BLOCK,
        },
    };

    let mut wide: Vec<u16> = name.encode_wide().collect();
    let length = u16::try_from(wide.len() * size_of::<u16>()).map_err(io::Error::other)?;
    let mut name = UNICODE_STRING {
        Length: length,
        MaximumLength: length,
        Buffer: wide.as_mut_ptr(),
    };
    let attributes = OBJECT_ATTRIBUTES {
        Length: size_of::<OBJECT_ATTRIBUTES>() as u32,
        RootDirectory: parent.as_raw_handle(),
        ObjectName: &mut name,
        Attributes: OBJ_CASE_INSENSITIVE | OBJ_DONT_REPARSE,
        SecurityDescriptor: security.map_or(ptr::null(), |descriptor| descriptor.0.cast()),
        ..Default::default()
    };
    let mut handle = ptr::null_mut();
    let mut io_status = IO_STATUS_BLOCK::default();
    let file_attributes = if options & FILE_DIRECTORY_FILE != 0 {
        FILE_ATTRIBUTE_DIRECTORY
    } else {
        0
    };
    // SAFETY: The retained parent owns its handle, and all buffers stay live
    // for this synchronous call. The only callers supply a single child name.
    let status = unsafe {
        NtCreateFile(
            &mut handle,
            access,
            &attributes,
            &mut io_status,
            ptr::null(),
            file_attributes,
            share,
            disposition,
            options | FILE_SYNCHRONOUS_IO_NONALERT,
            ptr::null(),
            0,
        )
    };
    if status < 0 {
        // SAFETY: The returned NTSTATUS is the sole input.
        return Err(io::Error::from_raw_os_error(
            unsafe { RtlNtStatusToDosError(status) } as i32,
        ));
    }
    if handle.is_null() || handle == windows_sys::Win32::Foundation::INVALID_HANDLE_VALUE {
        return Err(io::Error::other("relative open returned no handle"));
    }
    // SAFETY: A successful synchronous open transferred ownership of the handle.
    Ok(unsafe { File::from_raw_handle(handle) })
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) struct FileIdentity {
    pub(crate) volume: u32,
    pub(crate) index: u64,
}

pub(crate) fn check_regular_file(file: &File) -> io::Result<FileIdentity> {
    let metadata = file.metadata()?;
    if !metadata.is_file() || metadata.file_attributes() & FILE_ATTRIBUTE_REPARSE_POINT != 0 {
        return Err(io::Error::other("configuration entry is not a plain file"));
    }
    let info = handle_information(file)?;
    if info.nNumberOfLinks != 1 {
        return Err(io::Error::other("configuration entry has multiple links"));
    }
    Ok(file_identity(&info))
}

pub(crate) fn handle_information(file: &File) -> io::Result<BY_HANDLE_FILE_INFORMATION> {
    let mut info = BY_HANDLE_FILE_INFORMATION::default();
    // SAFETY: The file owns the live handle and info is writable for the call.
    if unsafe { GetFileInformationByHandle(file.as_raw_handle(), &mut info) } == 0 {
        return Err(io::Error::last_os_error());
    }
    Ok(info)
}

pub(crate) fn file_identity(info: &BY_HANDLE_FILE_INFORMATION) -> FileIdentity {
    FileIdentity {
        volume: info.dwVolumeSerialNumber,
        index: (u64::from(info.nFileIndexHigh) << 32) | u64::from(info.nFileIndexLow),
    }
}

pub(crate) fn pin_root(root: &Path) -> io::Result<(File, Vec<File>, BY_HANDLE_FILE_INFORMATION)> {
    let (drive, names) = split_path(root)?;
    let wide: Vec<u16> = drive.as_os_str().encode_wide().chain([0]).collect();
    // SAFETY: wide is a NUL-terminated local drive root.
    if unsafe { GetDriveTypeW(wide.as_ptr()) } != DRIVE_FIXED {
        return Err(denied("configuration drive is not fixed"));
    }
    let drive_handle = OpenOptions::new()
        .read(true)
        .share_mode(FILE_SHARE_READ | FILE_SHARE_WRITE)
        .custom_flags(FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT)
        .open(&drive)?;
    check_drive(&drive_handle, &drive)?;
    let volume = directory_info(&drive_handle)?.dwVolumeSerialNumber;
    let mut ancestors = vec![drive_handle];
    let mut names = names.into_iter().peekable();
    while let Some(name) = names.next() {
        let parent = ancestors.last().expect("the drive is pinned");
        let is_leaf = names.peek().is_none();
        let file = open_directory(parent, name)?;
        let info = directory_info(&file)?;
        if info.dwVolumeSerialNumber != volume {
            return Err(denied("configuration directory changed volumes"));
        }
        if is_leaf {
            return Ok((file, ancestors, info));
        }
        ancestors.push(file);
    }
    unreachable!("split_path requires a directory below the drive")
}

pub(crate) fn require_ntfs(volume: &File) -> io::Result<()> {
    use windows_sys::Win32::Storage::FileSystem::GetVolumeInformationByHandleW;

    let mut filesystem = [0u16; 16];
    // SAFETY: The retained drive handle is live and the output buffer is writable.
    if unsafe {
        GetVolumeInformationByHandleW(
            volume.as_raw_handle(),
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
        return Err(denied("protected writer requires NTFS"));
    }
    Ok(())
}

pub(crate) fn missing_leaf(error: &io::Error) -> bool {
    // The open is relative to an already pinned directory. PATH_NOT_FOUND,
    // access failures, reparse errors and sharing violations remain errors.
    error.raw_os_error() == Some(ERROR_FILE_NOT_FOUND as i32)
}

pub(crate) fn split_path(path: &Path) -> io::Result<(PathBuf, Vec<&OsStr>)> {
    let mut components = path.components();
    let Some(Component::Prefix(prefix)) = components.next() else {
        return Err(denied("configuration root is not a local absolute path"));
    };
    if !matches!(prefix.kind(), Prefix::Disk(_)) || components.next() != Some(Component::RootDir) {
        return Err(denied("configuration root is not a local absolute path"));
    }
    let drive: PathBuf = [Component::Prefix(prefix), Component::RootDir]
        .into_iter()
        .collect();
    let mut names = Vec::new();
    let mut exact = drive.clone();
    for component in components {
        let Component::Normal(name) = component else {
            return Err(denied("unexpected configuration path component"));
        };
        let name_text = name
            .to_str()
            .ok_or_else(|| denied("invalid configuration path text"))?;
        if name_text
            .chars()
            .any(|ch| ch < ' ' || r#"<>:"|?*"#.contains(ch))
            || name_text.ends_with([' ', '.'])
            || reserved_device(name_text)
        {
            return Err(denied("ambiguous configuration path component"));
        }
        names.push(name);
        exact.push(name);
    }
    if names.is_empty() || exact.as_os_str() != path.as_os_str() {
        return Err(denied(
            "configuration root must be an exact path below a drive",
        ));
    }
    Ok((drive, names))
}

pub(crate) fn reserved_device(name: &str) -> bool {
    let base = name
        .split('.')
        .next()
        .unwrap_or_default()
        .trim_end_matches(' ');
    let base = base.to_ascii_uppercase();
    matches!(
        base.as_str(),
        "CON" | "PRN" | "AUX" | "NUL" | "CONIN$" | "CONOUT$"
    ) || base
        .strip_prefix("COM")
        .or_else(|| base.strip_prefix("LPT"))
        .is_some_and(|suffix| {
            matches!(
                suffix,
                "1" | "2" | "3" | "4" | "5" | "6" | "7" | "8" | "9" | "¹" | "²" | "³"
            )
        })
}

fn check_drive(file: &File, drive: &Path) -> io::Result<()> {
    let mut actual = [0u16; 16];
    // SAFETY: The file owns a live handle and actual is writable UTF-16 storage.
    let len = unsafe {
        GetFinalPathNameByHandleW(
            file.as_raw_handle(),
            actual.as_mut_ptr(),
            actual.len() as u32,
            0,
        )
    } as usize;
    if len == 0 {
        return Err(io::Error::last_os_error());
    }
    let expected: Vec<_> = OsStr::new(r"\\?\")
        .encode_wide()
        .chain(drive.as_os_str().encode_wide())
        .collect();
    let matches = actual.get(..len).is_some_and(|actual| {
        actual.len() == expected.len()
            && actual.iter().zip(expected).all(|(&left, right)| {
                left == right
                    || left < 128
                        && right < 128
                        && (left as u8).eq_ignore_ascii_case(&(right as u8))
            })
    });
    if !matches {
        return Err(denied("configuration drive is redirected"));
    }
    Ok(())
}

pub(crate) fn open_directory(parent: &File, name: &OsStr) -> io::Result<File> {
    // Allow same-directory replacement after admission. Withholding delete
    // sharing pins this directory without interfering with child renames.
    open_relative(
        parent,
        name,
        FILE_GENERIC_READ,
        FILE_SHARE_READ | FILE_SHARE_WRITE,
        FILE_OPEN,
        FILE_DIRECTORY_FILE | FILE_OPEN_REPARSE_POINT,
    )
}

pub(crate) fn directory_info(file: &File) -> io::Result<BY_HANDLE_FILE_INFORMATION> {
    let info = handle_information(file)?;
    // SAFETY: file owns a live handle.
    if unsafe { GetFileType(file.as_raw_handle()) } != FILE_TYPE_DISK
        || info.dwFileAttributes & (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT)
            != FILE_ATTRIBUTE_DIRECTORY
    {
        return Err(denied(
            "configuration path component is not a plain disk directory",
        ));
    }
    Ok(info)
}

#[derive(Clone, Copy)]
pub(crate) enum Policy {
    Directory { extra_rights: u32 },
    Setup { ace_flags: u8 },
    Record,
    PrivateCustody,
}

impl Policy {
    pub(crate) const ANCESTOR: Self = Self::Directory {
        extra_rights: FILE_ADD_SUBDIRECTORY,
    };
    pub(crate) const PROGRAM_DATA: Self = Self::Directory {
        // Fixed ProgramData only: child creation and ordinary metadata.
        // No delete-child, replacement, or security mutation rights.
        extra_rights: FILE_ADD_SUBDIRECTORY | 2 | 0x10 | 0x100,
    };
    pub(crate) const ROOT: Self = Self::Setup {
        ace_flags: OBJECT_AND_CONTAINER_INHERIT,
    };
    #[cfg(feature = "vm-scm-fixture")]
    pub(crate) const VM_FILE: Self = Self::Setup { ace_flags: 0 };
}

pub(crate) fn denied(message: &'static str) -> io::Error {
    io::Error::new(io::ErrorKind::PermissionDenied, message)
}

pub(crate) struct LocalDescriptor(pub(crate) HLOCAL);

impl Drop for LocalDescriptor {
    fn drop(&mut self) {
        // SAFETY: Windows allocated the non-null descriptor for the caller.
        unsafe { LocalFree(self.0) };
    }
}

pub(crate) fn verify_security(file: &File, policy: Policy) -> io::Result<()> {
    verify_descriptor(file, |bytes| accepts_descriptor(bytes, policy))
}

pub(crate) fn verify_descriptor(
    file: &File,
    accepts: impl FnOnce(&[u8]) -> Option<bool>,
) -> io::Result<()> {
    let descriptor = retain_descriptor(file)?;
    // SAFETY: The live self-relative descriptor and its allocation come from Windows.
    let size = unsafe { GetSecurityDescriptorLength(descriptor.0) } as usize;
    const MAX_DESCRIPTOR: usize = 20 + u16::MAX as usize + 2 * (8 + 15 * 4);
    if !(20..=MAX_DESCRIPTOR).contains(&size) {
        return Err(denied("invalid configuration security descriptor size"));
    }
    // SAFETY: Windows reports the size of this descriptor. The allocation
    // remains owned by descriptor for the duration of validation.
    let bytes = unsafe { slice::from_raw_parts(descriptor.0.cast(), size) };
    if !accepts(bytes).unwrap_or(false) {
        return Err(denied("untrusted configuration security descriptor"));
    }
    Ok(())
}

pub(crate) const SYSTEM: &[u8] = &[1, 1, 0, 0, 0, 0, 0, 5, 18, 0, 0, 0];
pub(crate) const ADMINISTRATORS: &[u8] = &[1, 2, 0, 0, 0, 0, 0, 5, 32, 0, 0, 0, 32, 2, 0, 0];
// Windows Modules Installer is trusted on ancestors only, using the same SID as the
// managed guardian path policy. It is not part of setup's machine-data ACL.
pub(crate) const TRUSTED_INSTALLER: &[u8] = &[
    1, 6, 0, 0, 0, 0, 0, 5, 80, 0, 0, 0, 181, 137, 251, 56, 25, 132, 194, 203, 92, 108, 35, 109,
    87, 0, 119, 110, 192, 2, 100, 135,
];
const OBJECT_AND_CONTAINER_INHERIT: u8 = 0x03;
const INHERIT_ONLY_ACE: u8 = 0x08;
const DIRECTORY_ACE_FLAGS: u8 = 0x1f;
const INHERITED_ACE: u8 = 0x10;
const SAFE_RECORD_RIGHTS: u32 =
    FILE_GENERIC_READ | FILE_GENERIC_EXECUTE | GENERIC_READ | GENERIC_EXECUTE;

// Parse only the self-relative owner and DACL from GetSecurityInfo. Reject
// ambiguous ACE formats rather than treating unknown grants as harmless.
pub(crate) fn accepts_descriptor(bytes: &[u8], policy: Policy) -> Option<bool> {
    let control = word(bytes, 2)?;
    if *bytes.first()? != 1
        || control & (SE_SELF_RELATIVE | SE_DACL_PRESENT) != SE_SELF_RELATIVE | SE_DACL_PRESENT
    {
        return Some(false);
    }
    let owner = sid(section(bytes, 4)?)?;
    let setup = matches!(policy, Policy::Setup { .. } | Policy::PrivateCustody);
    if match policy {
        Policy::Record => owner != ADMINISTRATORS && owner != SYSTEM,
        Policy::PrivateCustody => {
            !matches!(owner, ADMINISTRATORS | SYSTEM) || control & SE_DACL_PROTECTED == 0
        }
        Policy::Directory { .. } => !matches!(owner, ADMINISTRATORS | SYSTEM | TRUSTED_INSTALLER),
        Policy::Setup { .. } => owner != ADMINISTRATORS || control & SE_DACL_PROTECTED == 0,
    } {
        return Some(false);
    }
    let acl = section(bytes, 16)?;
    if !matches!(acl.first()?, 2 | 4) {
        return Some(false);
    }
    let count = word(acl, 4)?;
    let mut aces = acl.get(8..usize::from(word(acl, 2)?))?;
    if count == 0 || setup && count != 2 {
        return Some(false);
    }
    let mut setup_principals = 0;
    for _ in 0..count {
        let length = usize::from(word(aces, 2)?);
        let ace = aces.get(..length)?;
        let (kind, flags) = (*ace.first()?, *ace.get(1)?);
        let mask = dword(ace, 4)?;
        let trustee = sid(ace.get(8..)?)?;
        if trustee.len() + 8 != length {
            return Some(false);
        }
        let principal = match trustee {
            SYSTEM => 1,
            ADMINISTRATORS => 2,
            _ => 0,
        };
        match policy {
            // An ancestor may allow creation of a subdirectory. Direct grants
            // that can replace an existing child, redirect traversal or change
            // security are refused. Inherit-only grants are checked where
            // they apply, on the next directory or record's own descriptor.
            Policy::Directory { extra_rights } => {
                if !matches!(kind, 0 | 1)
                    || flags & !DIRECTORY_ACE_FLAGS != 0
                    || kind == 0
                        && flags & INHERIT_ONLY_ACE == 0
                        && principal == 0
                        && trustee != TRUSTED_INSTALLER
                        && mask & !(SAFE_RECORD_RIGHTS | extra_rights) != 0
                {
                    return Some(false);
                }
            }
            // Legacy files may have explicit or inherited grants. Denies and
            // read/execute grants convey no write authority.
            Policy::Record => {
                if !matches!(kind, 0 | 1)
                    || flags & !INHERITED_ACE != 0
                    || kind == 0 && principal == 0 && mask & !SAFE_RECORD_RIGHTS != 0
                {
                    return Some(false);
                }
            }
            // Setup grants FullControl to exactly SYSTEM and Administrators.
            Policy::Setup { ace_flags } => {
                if kind != 0 || flags != ace_flags || mask != FILE_ALL_ACCESS || principal == 0 {
                    return Some(false);
                }
                setup_principals |= principal;
            }
            Policy::PrivateCustody => {
                if kind != 0 || flags != 0 || mask != FILE_ALL_ACCESS || principal == 0 {
                    return Some(false);
                }
                setup_principals |= principal;
            }
        }
        aces = &aces[length..];
    }
    Some(!setup || setup_principals == 3)
}

// Fixed public native root only. Never use this policy for broker/private data.
pub(crate) fn accepts_public_native_root(bytes: &[u8]) -> Option<bool> {
    if !accepts_descriptor(bytes, Policy::Directory { extra_rights: 0 })?
        || word(bytes, 2)? & SE_DACL_PROTECTED == 0
        || sid(section(bytes, 4)?)? != ADMINISTRATORS
    {
        return Some(false);
    }
    let acl = section(bytes, 16)?;
    if word(acl, 4)? != 3 {
        return Some(false);
    }
    let mut aces = acl.get(8..usize::from(word(acl, 2)?))?;
    let mut seen = 0;
    for _ in 0..3 {
        let n = word(aces, 2)? as usize;
        let ace = aces.get(..n)?;
        if ace[0] != 0 || ace[1] != 3 {
            return Some(false);
        }
        let trustee = sid(ace.get(8..)?)?;
        let mask = dword(ace, 4)?;
        let bit = if trustee == ADMINISTRATORS && mask == FILE_ALL_ACCESS {
            1
        } else if trustee == SYSTEM && mask == FILE_ALL_ACCESS {
            2
        } else if trustee == [1, 2, 0, 0, 0, 0, 0, 5, 32, 0, 0, 0, 33, 2, 0, 0]
            && mask == FILE_GENERIC_READ | FILE_GENERIC_EXECUTE
        {
            4
        } else {
            return Some(false);
        };
        if seen & bit != 0 {
            return Some(false);
        }
        seen |= bit;
        aces = &aces[n..];
    }
    Some(seen == 7)
}

pub(crate) fn section(bytes: &[u8], field: usize) -> Option<&[u8]> {
    let offset = dword(bytes, field)? as usize;
    if offset < 20 || !offset.is_multiple_of(4) {
        return None;
    }
    bytes.get(offset..)
}

pub(crate) fn sid(bytes: &[u8]) -> Option<&[u8]> {
    let count = usize::from(*bytes.get(1)?);
    if bytes[0] != 1 || count > 15 {
        return None;
    }
    bytes.get(..8 + 4 * count)
}

pub(crate) fn word(bytes: &[u8], offset: usize) -> Option<u16> {
    Some(u16::from_le_bytes(
        bytes.get(offset..offset + 2)?.try_into().ok()?,
    ))
}

pub(crate) fn dword(bytes: &[u8], offset: usize) -> Option<u32> {
    Some(u32::from_le_bytes(
        bytes.get(offset..offset + 4)?.try_into().ok()?,
    ))
}

pub(crate) fn rename_same_directory(file: &File, target: &str, replace: bool) -> io::Result<()> {
    use windows_sys::{
        Wdk::Storage::FileSystem::{
            FILE_RENAME_INFORMATION, FileRenameInformation, NtSetInformationFile,
        },
        Win32::{
            Foundation::{HANDLE, RtlNtStatusToDosError},
            System::IO::IO_STATUS_BLOCK,
        },
    };

    #[repr(C)]
    struct Rename {
        replace: u32,
        root: HANDLE,
        byte_len: u32,
        name: [u16; 128],
    }
    // The handle was opened relative to the pinned root. A simple name
    // with no RootDirectory renames within that same directory.
    let mut info = Rename {
        replace: u32::from(replace),
        root: ptr::null_mut(),
        byte_len: 0,
        name: [0; 128],
    };
    if target.is_empty()
        || !target
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'.' | b'-'))
    {
        return Err(denied("configuration rename target must be a simple name"));
    }
    let name: Vec<u16> = target.encode_utf16().collect();
    if name.len() > info.name.len() {
        return Err(denied("configuration rename target is too long"));
    }
    info.name[..name.len()].copy_from_slice(&name);
    info.byte_len = (name.len() * size_of::<u16>()) as u32;
    debug_assert_eq!(
        std::mem::offset_of!(Rename, name),
        std::mem::offset_of!(FILE_RENAME_INFORMATION, FileName)
    );
    let mut status_block = IO_STATUS_BLOCK::default();
    // SAFETY: The source and buffers stay live throughout the synchronous call.
    let status = unsafe {
        NtSetInformationFile(
            file.as_raw_handle(),
            &mut status_block,
            (&raw const info).cast(),
            size_of::<Rename>() as u32,
            FileRenameInformation,
        )
    };
    if status < 0 {
        // SAFETY: This only translates the returned status code.
        return Err(io::Error::from_raw_os_error(
            unsafe { RtlNtStatusToDosError(status) } as i32,
        ));
    }
    Ok(())
}

/// Deletes the file when its last handle closes. The handle must have been
/// opened with DELETE access.
pub(crate) fn mark_for_deletion(file: &File) -> io::Result<()> {
    use windows_sys::Win32::Storage::FileSystem::{
        FILE_DISPOSITION_INFO, FileDispositionInfo, SetFileInformationByHandle,
    };
    let disposition = FILE_DISPOSITION_INFO { DeleteFile: true };
    // SAFETY: The file owns a live handle opened for DELETE and disposition
    // points to the structure required by FileDispositionInfo.
    if unsafe {
        SetFileInformationByHandle(
            file.as_raw_handle(),
            FileDispositionInfo,
            (&raw const disposition).cast(),
            size_of::<FILE_DISPOSITION_INFO>() as u32,
        )
    } == 0
    {
        return Err(io::Error::last_os_error());
    }
    Ok(())
}

/// Retain an existing protected descriptor for a child with the same policy.
/// The caller verifies both parent and new child; this never grants wider ACLs.
pub(crate) fn retain_descriptor(file: &File) -> io::Result<LocalDescriptor> {
    let mut descriptor = ptr::null_mut();
    // SAFETY: file owns the handle opened with READ_CONTROL, and descriptor is
    // writable. Only the owner and DACL are requested.
    let status = unsafe {
        GetSecurityInfo(
            file.as_raw_handle(),
            SE_FILE_OBJECT,
            OWNER_SECURITY_INFORMATION | DACL_SECURITY_INFORMATION,
            ptr::null_mut(),
            ptr::null_mut(),
            ptr::null_mut(),
            ptr::null_mut(),
            &mut descriptor,
        )
    };
    if status != 0 {
        return Err(io::Error::from_raw_os_error(status as i32));
    }
    if descriptor.is_null() {
        return Err(denied("missing configuration security descriptor"));
    }
    let descriptor = LocalDescriptor(descriptor);
    Ok(descriptor)
}

#[cfg(test)]
mod native_root_path_tests {
    use super::*;
    #[test]
    fn shared_reserved_device_guard_rejects_superscript_and_numeric_aliases() {
        for name in [
            "COM1",
            "LPT9.txt",
            "COM\u{b9}",
            "LPT\u{b2}",
            "COM\u{b3}.bin",
            "LPT\u{b9} .dat",
        ] {
            assert!(reserved_device(name), "alias accepted: {name}");
        }
        for name in ["COM10", "COMx", "LPT0", "ordinary.bin"] {
            assert!(!reserved_device(name));
        }
    }
}

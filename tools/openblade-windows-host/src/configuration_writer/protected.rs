//! Retained-handle admission and developer transaction I/O for the Windows
//! battery configuration aggregate. Existing records are checked independently
//! and are never re-owned. The exclusive writer lease applies to cooperating
//! test writers. The installed managed service does not participate.

use super::{
    FileIdentity, NAMES, check_regular_file, handle_information, inspect_bytes, open_relative,
    open_relative_with_security, read_file,
};
use crate::configuration::{
    File as ObservedFile, Inspection, RECOVERY_REQUIRED, RecoveryRecord, Snapshot, inspect,
};
use std::{
    ffi::OsStr,
    fs::{File, OpenOptions},
    io::{self, Write},
    os::windows::{ffi::OsStrExt, fs::OpenOptionsExt, io::AsRawHandle},
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
            FILE_SHARE_WRITE, FILE_TYPE_DISK, GetDriveTypeW, GetFileType,
            GetFinalPathNameByHandleW,
        },
        System::WindowsProgramming::DRIVE_FIXED,
    },
};

/// Retains the actual admitted directory and its ancestors. Opening and
/// inspecting never creates, repairs, migrates, or deletes any entry.
pub struct ProtectedStorage {
    root: File,
    ancestors: Vec<File>,
    identity: super::FileIdentity,
}

impl ProtectedStorage {
    /// Opens an existing local root. The caller supplies its exact location
    /// from trusted setup configuration. No installed path is resolved here.
    pub fn open(root: &Path) -> io::Result<Self> {
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
                let storage = Self {
                    root: file,
                    ancestors,
                    identity: super::file_identity(&info),
                };
                storage.check_root()?;
                return Ok(storage);
            }
            ancestors.push(file);
        }
        unreachable!("split_path requires a directory below the drive")
    }

    /// Reads the existing aggregate envelope. A trusted recovery-required
    /// sidecar fences startup by presence alone. Transaction artifacts remain
    /// opaque and are left for the existing recovery process.
    pub fn inspect(&self) -> io::Result<Inspection> {
        self.check_root()?;
        let result = inspect_records(
            |name| self.open_record(name),
            |file| read_file(file).map(|(_, bytes)| bytes),
        )?;
        self.check_root()?;
        Ok(result)
    }

    pub(super) fn check_root(&self) -> io::Result<()> {
        verify_security(&self.root, Policy::Root)?;
        for ancestor in &self.ancestors {
            let info = directory_info(ancestor)?;
            if info.dwVolumeSerialNumber != self.identity.volume {
                return Err(denied("configuration ancestor changed volumes"));
            }
            verify_security(ancestor, Policy::Ancestor)?;
        }
        if super::file_identity(&directory_info(&self.root)?) != self.identity {
            return Err(denied("configuration root identity changed"));
        }
        Ok(())
    }

    pub(super) fn open_record(&self, name: &str) -> io::Result<Option<File>> {
        // The recovery fence is presence-only and needs no data-read access.
        let access = if name == RECOVERY_REQUIRED {
            use windows_sys::Win32::Storage::FileSystem::{
                FILE_READ_ATTRIBUTES, READ_CONTROL, SYNCHRONIZE,
            };
            FILE_READ_ATTRIBUTES | READ_CONTROL | SYNCHRONIZE
        } else {
            FILE_GENERIC_READ
        };
        self.open_existing(name, access)
    }

    pub(super) fn open_existing(&self, name: &str, access: u32) -> io::Result<Option<File>> {
        let file = match open_relative(
            &self.root,
            OsStr::new(name),
            access,
            FILE_SHARE_READ,
            FILE_OPEN,
            FILE_OPEN_REPARSE_POINT,
        ) {
            Ok(file) => file,
            Err(error) if missing_leaf(&error) => return Ok(None),
            Err(error) => return Err(error),
        };
        self.verify_record(&file)?;
        Ok(Some(file))
    }

    fn verify_record(&self, file: &File) -> io::Result<FileIdentity> {
        let identity = check_regular_file(file)?;
        // SAFETY: file owns a live handle.
        if unsafe { GetFileType(file.as_raw_handle()) } != FILE_TYPE_DISK
            || identity.volume != self.identity.volume
        {
            return Err(denied("configuration record is not on the pinned disk"));
        }
        verify_security(file, Policy::Record)?;
        Ok(identity)
    }

    pub(super) fn acquire_writer(&self) -> io::Result<File> {
        use windows_sys::Wdk::Storage::FileSystem::FILE_OPEN_IF;
        use windows_sys::Win32::Storage::FileSystem::FILE_GENERIC_WRITE;

        self.require_ntfs()?;
        self.create_or_open(
            "configuration.writer-lock",
            FILE_GENERIC_READ | FILE_GENERIC_WRITE,
            0,
            FILE_OPEN_IF,
        )
        .and_then(|file| {
            if file.metadata()?.len() != 0 {
                return Err(denied("configuration writer lease is not empty"));
            }
            Ok(file)
        })
        .map_err(|error| {
            io::Error::new(
                error.kind(),
                format!("protected configuration writer lease: {error}"),
            )
        })
    }

    fn require_ntfs(&self) -> io::Result<()> {
        use windows_sys::Win32::Storage::FileSystem::GetVolumeInformationByHandleW;

        let volume = self
            .ancestors
            .first()
            .expect("drive handle retained during admission");
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

    pub(super) fn create_durable(&self, name: &str, bytes: &[u8]) -> io::Result<File> {
        use windows_sys::{
            Wdk::Storage::FileSystem::FILE_CREATE,
            Win32::Storage::FileSystem::{DELETE, FILE_GENERIC_WRITE},
        };

        let is_record = name == RECOVERY_REQUIRED;
        let mut file = self.create_or_open(
            name,
            FILE_GENERIC_WRITE | if is_record { DELETE } else { 0 },
            if is_record { FILE_SHARE_READ } else { 0 },
            FILE_CREATE,
        )?;
        file.write_all(bytes)
            .and_then(|()| file.sync_all())
            .map_err(|error| {
                io::Error::new(
                    error.kind(),
                    format!("could not write and flush {name}, file retained: {error}"),
                )
            })?;
        Ok(file)
    }

    fn create_or_open(
        &self,
        name: &str,
        access: u32,
        share: u32,
        disposition: u32,
    ) -> io::Result<File> {
        use windows_sys::Wdk::Storage::FileSystem::{FILE_NON_DIRECTORY_FILE, FILE_WRITE_THROUGH};
        use windows_sys::Win32::Security::Authorization::{
            ConvertStringSecurityDescriptorToSecurityDescriptorW, SDDL_REVISION_1,
        };
        use windows_sys::Win32::Storage::FileSystem::READ_CONTROL;

        self.check_root()?;
        let sddl: Vec<u16> = "O:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)"
            .encode_utf16()
            .chain([0])
            .collect();
        let mut descriptor = ptr::null_mut();
        // SAFETY: The string is terminated and Windows writes a LocalAlloc descriptor.
        if unsafe {
            ConvertStringSecurityDescriptorToSecurityDescriptorW(
                sddl.as_ptr(),
                SDDL_REVISION_1,
                &mut descriptor,
                ptr::null_mut(),
            )
        } == 0
        {
            return Err(io::Error::last_os_error());
        }
        if descriptor.is_null() {
            return Err(denied("missing protected creation descriptor"));
        }
        let descriptor = LocalDescriptor(descriptor);
        // The descriptor applies only on creation. Existing lease entries
        // are verified on their opened handles and never repaired.
        let file = open_relative_with_security(
            &self.root,
            OsStr::new(name),
            access | READ_CONTROL,
            share,
            disposition,
            FILE_NON_DIRECTORY_FILE | FILE_OPEN_REPARSE_POINT | FILE_WRITE_THROUGH,
            Some(&descriptor),
        )?;
        self.verify_record(&file)?;
        Ok(file)
    }

    pub(super) fn rename(
        &self,
        source: &str,
        target: &str,
        replace: bool,
        identity: FileIdentity,
        bytes: &[u8],
    ) -> io::Result<()> {
        use windows_sys::Win32::Storage::FileSystem::{DELETE, FILE_GENERIC_WRITE};

        self.check_root()?;
        let file = self
            .open_existing(source, FILE_GENERIC_READ | FILE_GENERIC_WRITE | DELETE)?
            .ok_or_else(|| io::Error::new(io::ErrorKind::NotFound, "staged record missing"))?;
        let (actual_id, actual_bytes) = read_file(&file)?;
        if actual_id != identity || actual_bytes != bytes {
            return Err(denied("staged record changed before rename"));
        }

        rename_same_directory(&file, target, replace)?;
        // A failed flush after submission leaves identity verification to
        // preserve the recovery marker instead of retrying the rename.
        file.sync_all()
    }
}

fn rename_same_directory(file: &File, target: &str, replace: bool) -> io::Result<()> {
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

fn missing_leaf(error: &io::Error) -> bool {
    // The open is relative to an already pinned directory. PATH_NOT_FOUND,
    // access failures, reparse errors and sharing violations remain errors.
    error.raw_os_error() == Some(ERROR_FILE_NOT_FOUND as i32)
}

fn inspect_records<T>(
    mut open: impl FnMut(&str) -> io::Result<Option<T>>,
    mut read: impl FnMut(&T) -> io::Result<Vec<u8>>,
) -> io::Result<Inspection> {
    if open(RECOVERY_REQUIRED)?.is_some() {
        return Ok(inspect(Snapshot {
            recovery: RecoveryRecord::Present,
            profile: ObservedFile::Unavailable,
            preferences: ObservedFile::Unavailable,
            commit: ObservedFile::Unavailable,
        }));
    }
    // Keep every admitted handle open through the final sidecar check. The
    // writer must still be excluded by the eventual host's ownership gate.
    let files = [open(NAMES[0])?, open(NAMES[1])?, open(NAMES[2])?];
    let mut bytes = [None, None, None];
    for (file, bytes) in files.iter().zip(&mut bytes) {
        *bytes = file.as_ref().map(&mut read).transpose()?;
    }
    if open(RECOVERY_REQUIRED)?.is_some() {
        return Err(denied("recovery record appeared during admission"));
    }
    Ok(inspect_bytes(bytes.each_ref().map(Option::as_deref)))
}

fn split_path(path: &Path) -> io::Result<(PathBuf, Vec<&OsStr>)> {
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

fn reserved_device(name: &str) -> bool {
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

fn open_directory(parent: &File, name: &OsStr) -> io::Result<File> {
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

fn directory_info(file: &File) -> io::Result<BY_HANDLE_FILE_INFORMATION> {
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
enum Policy {
    Ancestor,
    Root,
    Record,
}

fn denied(message: &'static str) -> io::Error {
    io::Error::new(io::ErrorKind::PermissionDenied, message)
}

pub(super) struct LocalDescriptor(pub(super) HLOCAL);

impl Drop for LocalDescriptor {
    fn drop(&mut self) {
        // SAFETY: Windows allocated the non-null descriptor for the caller.
        unsafe { LocalFree(self.0) };
    }
}

fn verify_security(file: &File, policy: Policy) -> io::Result<()> {
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
    // SAFETY: The live self-relative descriptor and its allocation come from Windows.
    let size = unsafe { GetSecurityDescriptorLength(descriptor.0) } as usize;
    const MAX_DESCRIPTOR: usize = 20 + u16::MAX as usize + 2 * (8 + 15 * 4);
    if !(20..=MAX_DESCRIPTOR).contains(&size) {
        return Err(denied("invalid configuration security descriptor size"));
    }
    // SAFETY: Windows reports the size of this descriptor. The allocation
    // remains owned by descriptor for the duration of validation.
    let bytes = unsafe { slice::from_raw_parts(descriptor.0.cast(), size) };
    if !accepts_descriptor(bytes, policy).unwrap_or(false) {
        return Err(denied("untrusted configuration security descriptor"));
    }
    Ok(())
}

const SYSTEM: &[u8] = &[1, 1, 0, 0, 0, 0, 0, 5, 18, 0, 0, 0];
const ADMINISTRATORS: &[u8] = &[1, 2, 0, 0, 0, 0, 0, 5, 32, 0, 0, 0, 32, 2, 0, 0];
// Windows Modules Installer is trusted on ancestors only, using the same SID as the
// managed guardian path policy. It is not part of setup's machine-data ACL.
const TRUSTED_INSTALLER: &[u8] = &[
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
fn accepts_descriptor(bytes: &[u8], policy: Policy) -> Option<bool> {
    let control = word(bytes, 2)?;
    if *bytes.first()? != 1
        || control & (SE_SELF_RELATIVE | SE_DACL_PRESENT) != SE_SELF_RELATIVE | SE_DACL_PRESENT
    {
        return Some(false);
    }
    let owner = sid(section(bytes, 4)?)?;
    if match policy {
        Policy::Root => owner != ADMINISTRATORS || control & SE_DACL_PROTECTED == 0,
        Policy::Record => owner != ADMINISTRATORS && owner != SYSTEM,
        Policy::Ancestor => !matches!(owner, ADMINISTRATORS | SYSTEM | TRUSTED_INSTALLER),
    } {
        return Some(false);
    }
    let acl = section(bytes, 16)?;
    if !matches!(acl.first()?, 2 | 4) {
        return Some(false);
    }
    let count = word(acl, 4)?;
    let mut aces = acl.get(8..usize::from(word(acl, 2)?))?;
    if count == 0 || matches!(policy, Policy::Root) && count != 2 {
        return Some(false);
    }
    let mut root_principals = 0;
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
            // Same two explicit propagating FullControl grants setup verifies.
            Policy::Root => {
                if kind != 0
                    || flags != OBJECT_AND_CONTAINER_INHERIT
                    || mask != FILE_ALL_ACCESS
                    || principal == 0
                {
                    return Some(false);
                }
                root_principals |= principal;
            }
            // An ancestor may allow creation of a subdirectory. Direct grants
            // that can replace an existing child, redirect traversal or change
            // security are refused. Inherit-only grants are checked where
            // they apply, on the next directory or record's own descriptor.
            Policy::Ancestor => {
                if !matches!(kind, 0 | 1)
                    || flags & !DIRECTORY_ACE_FLAGS != 0
                    || kind == 0
                        && flags & INHERIT_ONLY_ACE == 0
                        && principal == 0
                        && trustee != TRUSTED_INSTALLER
                        && mask & !(SAFE_RECORD_RIGHTS | FILE_ADD_SUBDIRECTORY) != 0
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
        }
        aces = &aces[length..];
    }
    Some(!matches!(policy, Policy::Root) || root_principals == 3)
}

fn section(bytes: &[u8], field: usize) -> Option<&[u8]> {
    let offset = dword(bytes, field)? as usize;
    if offset < 20 || !offset.is_multiple_of(4) {
        return None;
    }
    bytes.get(offset..)
}

fn sid(bytes: &[u8]) -> Option<&[u8]> {
    let count = usize::from(*bytes.get(1)?);
    if bytes[0] != 1 || count > 15 {
        return None;
    }
    bytes.get(..8 + 4 * count)
}

fn word(bytes: &[u8], offset: usize) -> Option<u16> {
    Some(u16::from_le_bytes(
        bytes.get(offset..offset + 2)?.try_into().ok()?,
    ))
}

fn dword(bytes: &[u8], offset: usize) -> Option<u32> {
    Some(u32::from_le_bytes(
        bytes.get(offset..offset + 4)?.try_into().ok()?,
    ))
}

#[cfg(test)]
mod tests;

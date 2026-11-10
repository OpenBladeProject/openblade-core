//! Read-only admission for the machine-wide battery configuration aggregate.
//! The future host must supply the authoritative local root and exclude other
//! privileged writers before treating the result as a startup snapshot.
//! Setup #912 protects only the root during service installation. Existing
//! records are checked independently and are never re-owned here. The positive
//! on-disk ACL path still needs validation under the intended Windows identity
//! with setup-equivalent permissions on an isolated NTFS runner. That exercise
//! must include the effective ancestor ACLs on the intended Windows layout.

use super::{
    NAMES, check_regular_file, handle_information, inspect_bytes, open_relative, read_file,
};
use crate::configuration::{
    File as ObservedFile, Inspection, RECOVERY_REQUIRED, RecoveryRecord, Snapshot, inspect,
};
use std::{
    ffi::OsStr,
    fs::{File, OpenOptions},
    io,
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
        let (drive_handle, volume) = pin_drive(&drive)?;
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

    fn check_root(&self) -> io::Result<()> {
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

    fn open_record(&self, name: &str) -> io::Result<Option<File>> {
        // The recovery fence is presence-only and needs no data-read access.
        let access = if name == RECOVERY_REQUIRED {
            use windows_sys::Win32::Storage::FileSystem::{
                FILE_READ_ATTRIBUTES, READ_CONTROL, SYNCHRONIZE,
            };
            FILE_READ_ATTRIBUTES | READ_CONTROL | SYNCHRONIZE
        } else {
            FILE_GENERIC_READ
        };
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
        // SAFETY: file owns a live handle.
        if unsafe { GetFileType(file.as_raw_handle()) } != FILE_TYPE_DISK
            || check_regular_file(&file)?.volume != self.identity.volume
        {
            return Err(denied("configuration record is not on the pinned disk"));
        }
        verify_security(&file, Policy::Record)?;
        Ok(Some(file))
    }
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

fn pin_drive(drive: &Path) -> io::Result<(File, u32)> {
    let wide: Vec<u16> = drive.as_os_str().encode_wide().chain([0]).collect();
    // SAFETY: wide is a NUL-terminated local drive root.
    if unsafe { GetDriveTypeW(wide.as_ptr()) } != DRIVE_FIXED {
        return Err(denied("configuration drive is not fixed"));
    }
    let handle = OpenOptions::new()
        .read(true)
        .share_mode(FILE_SHARE_READ | FILE_SHARE_WRITE)
        .custom_flags(FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT)
        .open(drive)?;
    check_drive(&handle, drive)?;
    let volume = directory_info(&handle)?.dwVolumeSerialNumber;
    Ok((handle, volume))
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

pub(super) struct LocalDescriptor(HLOCAL);

#[cfg(test)]
impl LocalDescriptor {
    pub(super) fn as_ptr(&self) -> *const windows_sys::Win32::Security::SECURITY_DESCRIPTOR {
        self.0.cast_const().cast()
    }
}

impl Drop for LocalDescriptor {
    fn drop(&mut self) {
        // SAFETY: Windows allocated this non-null descriptor for release by LocalFree.
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

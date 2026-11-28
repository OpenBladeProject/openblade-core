//! Retained-handle admission and developer transaction I/O for the Windows
//! battery configuration aggregate. Existing records are checked independently
//! and are never re-owned. The exclusive writer lease applies to cooperating
//! test writers. The installed managed service does not participate.

use super::{
    FileIdentity, NAMES, check_regular_file, inspect_bytes, open_relative,
    open_relative_with_security, read_file,
};
use crate::configuration::{
    File as ObservedFile, Inspection, RECOVERY_REQUIRED, RecoveryRecord, Snapshot, inspect,
};
#[cfg(feature = "vm-scm-fixture")]
use crate::windows_protected_path::reserved_device;
use crate::windows_protected_path::{
    LocalDescriptor, Policy, denied, directory_info, missing_leaf, verify_security,
};
#[cfg(test)]
use crate::windows_protected_path::{accepts_descriptor, split_path};
pub(super) use crate::windows_protected_path::{pin_root, require_ntfs};
#[cfg(test)]
use std::slice;
use std::{
    ffi::OsStr,
    fs::File,
    io::{self, Write},
    os::windows::io::AsRawHandle,
    path::Path,
    ptr,
};
use windows_sys::{
    Wdk::Storage::FileSystem::{FILE_OPEN, FILE_OPEN_REPARSE_POINT},
    Win32::Storage::FileSystem::{FILE_GENERIC_READ, FILE_SHARE_READ, FILE_TYPE_DISK, GetFileType},
};

/// Retains the actual admitted directory and its ancestors. Opening and
/// inspecting never creates, repairs, migrates, or deletes any entry.
pub struct ProtectedStorage {
    root: File,
    ancestors: Vec<File>,
    identity: super::FileIdentity,
    root_policy: Policy,
}

impl ProtectedStorage {
    /// Opens an existing local root. The caller supplies its exact location
    /// from trusted setup configuration. No installed path is resolved here.
    pub fn open(root: &Path) -> io::Result<Self> {
        Self::open_with_root_policy(root, Policy::ROOT)
    }

    fn open_with_root_policy(root: &Path, root_policy: Policy) -> io::Result<Self> {
        let (root, ancestors, info) = pin_root(root)?;
        let storage = Self {
            root,
            ancestors,
            identity: super::file_identity(&info),
            root_policy,
        };
        storage.check_root()?;
        Ok(storage)
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
        verify_security(&self.root, self.root_policy)?;
        for ancestor in &self.ancestors {
            let info = directory_info(ancestor)?;
            if info.dwVolumeSerialNumber != self.identity.volume {
                return Err(denied("configuration ancestor changed volumes"));
            }
            verify_security(ancestor, Policy::ANCESTOR)?;
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
        self.open_with_policy(name, access, Policy::Record)
    }

    #[cfg(feature = "vm-scm-fixture")]
    pub fn open_vm_root(root: &Path) -> io::Result<Self> {
        let storage = Self::open(root)?;
        require_ntfs(&storage.ancestors[0])?;
        Ok(storage)
    }

    #[cfg(feature = "vm-scm-fixture")]
    pub fn open_vm_file(&self, name: &str) -> io::Result<Option<File>> {
        if name.is_empty()
            || name.ends_with('.')
            || !name
                .bytes()
                .all(|byte| byte.is_ascii_alphanumeric() || b"._-".contains(&byte))
            || reserved_device(name)
        {
            return Err(denied("expected one plain VM fixture filename"));
        }
        self.check_root()?;
        self.open_with_policy(name, FILE_GENERIC_READ, Policy::VM_FILE)
    }

    fn open_with_policy(
        &self,
        name: &str,
        access: u32,
        policy: Policy,
    ) -> io::Result<Option<File>> {
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
        self.verify_file(&file, policy)?;
        Ok(Some(file))
    }

    fn verify_file(&self, file: &File, policy: Policy) -> io::Result<FileIdentity> {
        let identity = check_regular_file(file)?;
        // SAFETY: file owns a live handle.
        if unsafe { GetFileType(file.as_raw_handle()) } != FILE_TYPE_DISK
            || identity.volume != self.identity.volume
        {
            return Err(denied("configuration record is not on the pinned disk"));
        }
        verify_security(file, policy)?;
        Ok(identity)
    }

    pub(super) fn acquire_writer(&self) -> io::Result<File> {
        use windows_sys::Wdk::Storage::FileSystem::FILE_OPEN_IF;
        use windows_sys::Win32::Storage::FileSystem::FILE_GENERIC_WRITE;

        require_ntfs(&self.ancestors[0])?;
        self.create_or_open(
            super::WRITER_LOCK,
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
        self.verify_file(&file, Policy::Record)?;
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

pub(super) use crate::windows_protected_path::rename_same_directory;

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

#[cfg(test)]
mod tests;

#[cfg(all(test, feature = "vm-scm-fixture"))]
#[test]
fn vm_file_requires_the_setup_file_descriptor() {
    use windows_sys::Win32::Security::Authorization::{
        ConvertStringSecurityDescriptorToSecurityDescriptorW, SDDL_REVISION_1,
    };

    for (sddl, expected) in [
        ("O:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)", true),
        ("O:SYD:P(A;;FA;;;SY)(A;;FA;;;BA)", false),
        ("O:BAD:(A;;FA;;;SY)(A;;FA;;;BA)", false),
        ("O:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)", false),
        ("O:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FR;;;WD)", false),
    ] {
        let sddl: Vec<_> = sddl.encode_utf16().chain([0]).collect();
        let mut descriptor = ptr::null_mut();
        let mut length = 0;
        // SAFETY: sddl is NUL-terminated and both outputs are writable.
        let result = unsafe {
            ConvertStringSecurityDescriptorToSecurityDescriptorW(
                sddl.as_ptr(),
                SDDL_REVISION_1,
                &mut descriptor,
                &mut length,
            )
        };
        assert_ne!(result, 0, "{}", io::Error::last_os_error());
        let descriptor = LocalDescriptor(descriptor);
        // SAFETY: Windows allocated length bytes and descriptor owns them.
        let bytes = unsafe { slice::from_raw_parts(descriptor.0.cast(), length as usize) };
        assert_eq!(accepts_descriptor(bytes, Policy::VM_FILE), Some(expected));
    }
}

use super::{FileIdentity, check_regular_file, file_identity, open_relative};
use std::{ffi::OsStr, fs::File, io, os::windows::io::AsRawHandle, path::Path};
use windows_sys::{
    Wdk::Storage::FileSystem::{FILE_NON_DIRECTORY_FILE, FILE_OPEN, FILE_OPEN_REPARSE_POINT},
    Win32::Storage::FileSystem::{
        FILE_GENERIC_READ, FILE_GENERIC_WRITE, FILE_TYPE_DISK, GetFileType,
    },
};

pub mod run_marker;

pub struct Lease {
    // Close the exclusive file before releasing the retained directories.
    _file: File,
    _root: File,
    _ancestors: Vec<File>,
}

pub fn acquire(path: &Path, root: &str, lock: &str) -> io::Result<Lease> {
    let root_id = parse_identity(root)?;
    let lock_id = parse_identity(lock)?;
    let leaf = path
        .file_name()
        .and_then(OsStr::to_str)
        .and_then(|name| name.strip_prefix("openblade-writer-fence-"));
    if !leaf.is_some_and(|id| id.len() == 32 && id.bytes().all(is_lower_hex)) {
        return Err(io::Error::other(
            "expected a named scratch writer-fence directory",
        ));
    }
    let (root, ancestors, info) = super::protected::pin_root(path)?;
    super::protected::require_ntfs(&ancestors[0])?;
    if file_identity(&info) != root_id || lock_id.volume != root_id.volume {
        return Err(io::Error::other("scratch root identity mismatch"));
    }

    // The protected writer uses the same exclusive open. In the scratch
    // rehearsal a missing or replaced lease cannot recreate authority.
    let file = open_relative(
        &root,
        OsStr::new(super::WRITER_LOCK),
        FILE_GENERIC_READ | FILE_GENERIC_WRITE,
        0,
        FILE_OPEN,
        FILE_NON_DIRECTORY_FILE | FILE_OPEN_REPARSE_POINT,
    )?;
    // SAFETY: The file owns a live Windows handle.
    if unsafe { GetFileType(file.as_raw_handle()) } != FILE_TYPE_DISK
        || check_regular_file(&file)? != lock_id
        || file.metadata()?.len() != 0
    {
        return Err(io::Error::other(
            "scratch writer lease identity or shape mismatch",
        ));
    }

    Ok(Lease {
        _file: file,
        _root: root,
        _ancestors: ancestors,
    })
}

fn is_lower_hex(byte: u8) -> bool {
    byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte)
}

fn parse_identity(value: &str) -> io::Result<FileIdentity> {
    let invalid = || io::Error::other("malformed scratch identity");
    let (volume, index) = value.split_once(':').ok_or_else(invalid)?;
    if volume.len() != 8
        || index.len() != 16
        || !volume.bytes().chain(index.bytes()).all(is_lower_hex)
    {
        return Err(invalid());
    }
    let volume = u32::from_str_radix(volume, 16).map_err(|_| invalid())?;
    let index = u64::from_str_radix(index, 16).map_err(|_| invalid())?;
    if index == 0 {
        return Err(invalid());
    }
    Ok(FileIdentity { volume, index })
}

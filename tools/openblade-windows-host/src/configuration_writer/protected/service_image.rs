//! Read-only evidence for the fixed installed service image on disk.

use std::{
    ffi::OsString,
    fs::File,
    io::{self, Read},
    os::windows::ffi::OsStringExt,
    path::PathBuf,
    ptr::null_mut,
    slice,
};

use sha2::{Digest, Sha256};
use windows_sys::Win32::{
    Storage::FileSystem::FILE_GENERIC_READ,
    System::Com::CoTaskMemFree,
    UI::Shell::{FOLDERID_ProgramFiles, KF_FLAG_DONT_VERIFY, SHGetKnownFolderPath},
};

use super::{Policy, ProtectedStorage, denied, require_ntfs};
use crate::configuration_writer::{FileIdentity, file_identity, handle_information};

pub const INSTALL_DIRECTORY: &str = "OpenBlade";
pub const IMAGE_NAME: &str = "OpenBlade.Service.exe";
const MAX_IMAGE_BYTES: u64 = 64 * 1024 * 1024;

/// Supplies a digest already authenticated for the exact native candidate.
/// Implement this at the build trust boundary. A path, fixture receipt or
/// digest calculated from the installed file cannot supply this evidence.
pub trait AuthenticatedServiceBuild {
    fn expected_service_sha256(&self) -> io::Result<Option<[u8; 32]>>;
}

/// Keeps the verified image and its directory chain open without delete
/// sharing. The image also denies write sharing. This describes the opened
/// file only. A separate observation must bind a running process to it.
pub struct ProtectedServiceImage {
    _image: File,
    _storage: ProtectedStorage,
    identity: FileIdentity,
}

impl ProtectedServiceImage {
    pub fn read(build: &impl AuthenticatedServiceBuild) -> io::Result<Self> {
        with_expected_digest(build, |expected| {
            let root = program_files()?.join(INSTALL_DIRECTORY);
            let storage =
                ProtectedStorage::open_with_root_policy(&root, Policy::INSTALL_DIRECTORY)?;
            require_ntfs(&storage.ancestors[0])?;
            let mut image = storage
                .open_with_policy(IMAGE_NAME, FILE_GENERIC_READ, Policy::Record)?
                .ok_or_else(|| {
                    io::Error::new(io::ErrorKind::NotFound, "installed image is missing")
                })?;
            let before = snapshot(&image)?;
            verify_digest(&mut image, before.size, expected)?;
            if storage.verify_file(&image, Policy::Record)? != before.identity
                || snapshot(&image)? != before
            {
                return Err(denied("installed image changed during verification"));
            }
            storage.check_root()?;
            Ok(Self {
                _image: image,
                _storage: storage,
                identity: before.identity,
            })
        })
    }

    /// Volume serial number and NTFS file index, meaningful with the handle retained.
    pub fn file_identity(&self) -> (u32, u64) {
        (self.identity.volume, self.identity.index)
    }
}

fn with_expected_digest<T>(
    build: &impl AuthenticatedServiceBuild,
    inspect: impl FnOnce([u8; 32]) -> io::Result<T>,
) -> io::Result<T> {
    let expected = build
        .expected_service_sha256()?
        .ok_or_else(|| denied("authenticated installed service build evidence is unavailable"))?;
    inspect(expected)
}

#[derive(PartialEq, Eq)]
struct ImageSnapshot {
    identity: FileIdentity,
    size: u64,
    last_write: u64,
}

fn snapshot(file: &File) -> io::Result<ImageSnapshot> {
    let info = handle_information(file)?;
    let identity = file_identity(&info);
    if identity.index == 0 {
        return Err(denied("installed image has no usable file identity"));
    }
    Ok(ImageSnapshot {
        identity,
        size: (u64::from(info.nFileSizeHigh) << 32) | u64::from(info.nFileSizeLow),
        last_write: (u64::from(info.ftLastWriteTime.dwHighDateTime) << 32)
            | u64::from(info.ftLastWriteTime.dwLowDateTime),
    })
}

fn verify_digest(file: impl Read, size: u64, expected: [u8; 32]) -> io::Result<()> {
    if !(1..=MAX_IMAGE_BYTES).contains(&size) {
        return Err(denied(
            "installed image size is outside the verification bound",
        ));
    }
    let mut hash = Sha256::new();
    let read = io::copy(&mut file.take(size + 1), &mut hash)?;
    if read != size || <[u8; 32]>::from(hash.finalize()) != expected {
        return Err(denied(
            "installed image does not match authenticated build evidence",
        ));
    }
    Ok(())
}

/// Resolves the fixed known folder without consulting environment overrides.
pub fn program_files() -> io::Result<PathBuf> {
    let mut path = null_mut();
    // SAFETY: Windows supplies a task-allocated NUL-terminated path on success.
    let result = unsafe {
        SHGetKnownFolderPath(
            &FOLDERID_ProgramFiles,
            KF_FLAG_DONT_VERIFY as u32,
            null_mut(),
            &mut path,
        )
    };
    let folder = if result >= 0 && !path.is_null() {
        let mut len = 0;
        // SAFETY: The successful query returned a live NUL-terminated string.
        while unsafe { *path.add(len) } != 0 {
            len += 1;
        }
        // SAFETY: len excludes the terminator and the allocation remains live.
        Ok(PathBuf::from(OsString::from_wide(unsafe {
            slice::from_raw_parts(path, len)
        })))
    } else {
        Err(denied("Program Files known folder is unavailable"))
    };
    // SAFETY: The returned allocation, if present, belongs to this caller.
    unsafe { CoTaskMemFree(path.cast()) };
    folder
}

#[cfg(test)]
mod tests {
    use super::*;

    struct FakeBuild(Option<[u8; 32]>);

    impl AuthenticatedServiceBuild for FakeBuild {
        fn expected_service_sha256(&self) -> io::Result<Option<[u8; 32]>> {
            Ok(self.0)
        }
    }

    // Known SHA-256 for the bytes b"abc".
    const EXPECTED: [u8; 32] = [
        0xba, 0x78, 0x16, 0xbf, 0x8f, 0x01, 0xcf, 0xea, 0x41, 0x41, 0x40, 0xde, 0x5d, 0xae, 0x22,
        0x23, 0xb0, 0x03, 0x61, 0xa3, 0x96, 0x17, 0x7a, 0x9c, 0xb4, 0x10, 0xff, 0x61, 0xf2, 0x00,
        0x15, 0xad,
    ];

    #[test]
    fn missing_build_evidence_refuses_before_inspection() {
        let error = with_expected_digest::<()>(&FakeBuild(None), |_| {
            panic!("must not open anything without authenticated evidence")
        })
        .unwrap_err();
        assert_eq!(error.kind(), io::ErrorKind::PermissionDenied);
    }

    #[test]
    fn verifies_the_whole_bounded_stream_against_injected_build_evidence() {
        let build = FakeBuild(Some(EXPECTED));
        with_expected_digest(&build, |expected| verify_digest(&b"abc"[..], 3, expected)).unwrap();
        for (bytes, size) in [
            (&b"abd"[..], 3),
            (&b"abc"[..], 4),
            (&b"abcd"[..], 3),
            (&b""[..], 0),
            (&b"abc"[..], MAX_IMAGE_BYTES + 1),
        ] {
            let error =
                with_expected_digest(&build, |expected| verify_digest(bytes, size, expected))
                    .unwrap_err();
            assert_eq!(error.kind(), io::ErrorKind::PermissionDenied);
        }
    }
}

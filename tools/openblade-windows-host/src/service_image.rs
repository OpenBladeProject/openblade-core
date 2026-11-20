//! Verification of the retained Service file on disk.

#[cfg(feature = "scratch-candidate-receipt")]
use std::fs::File;
use std::io::{self, Read};

use sha2::{Digest, Sha256};

use crate::windows_protected_path::denied;
#[cfg(feature = "scratch-candidate-receipt")]
use crate::windows_protected_path::{FileIdentity, file_identity, handle_information};

pub(super) const MAX_IMAGE_BYTES: u64 = 64 * 1024 * 1024;

#[cfg(feature = "scratch-candidate-receipt")]
pub(super) fn verify(image: &mut File, expected_size: u64, expected: [u8; 32]) -> io::Result<()> {
    let before = snapshot(image)?;
    if before.size != expected_size {
        return Err(denied(
            "installed Service length differs from the committed receipt",
        ));
    }
    verify_digest(&mut *image, before.size, expected)?;
    if snapshot(image)? != before {
        return Err(denied("installed Service changed during verification"));
    }
    Ok(())
}

#[cfg(feature = "scratch-candidate-receipt")]
#[derive(PartialEq, Eq)]
pub(super) struct ImageSnapshot {
    identity: FileIdentity,
    pub(super) size: u64,
    last_write: u64,
}

#[cfg(feature = "scratch-candidate-receipt")]
pub(super) fn snapshot(file: &File) -> io::Result<ImageSnapshot> {
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

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn verifies_the_whole_bounded_stream() {
        let expected: [u8; 32] = Sha256::digest(b"abc").into();
        verify_digest(&b"abc"[..], 3, expected).unwrap();
        for (bytes, size) in [
            (&b"abd"[..], 3),
            (&b"abc"[..], 4),
            (&b"abcd"[..], 3),
            (&b""[..], 0),
            (&b"abc"[..], MAX_IMAGE_BYTES + 1),
        ] {
            assert_eq!(
                verify_digest(bytes, size, expected).unwrap_err().kind(),
                io::ErrorKind::PermissionDenied
            );
        }
    }
}

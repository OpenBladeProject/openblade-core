//! Scratch-only retained NativeAOT artifact; OS signing policy admits external modules.
#![allow(unsafe_code)]
use crate::{
    scratch_receipt_policy,
    service_image::{self, ImageSnapshot},
    windows_protected_path::{
        FileIdentity, check_regular_file, denied, denied as invalid, directory_info, dword,
        file_identity, open_relative, verify_descriptor,
    },
};
use sha2::{Digest, Sha256};
use std::{
    ffi::OsStr,
    fs::File,
    io::{self, Read, Seek},
    os::windows::io::AsRawHandle,
};
use windows_sys::{
    Wdk::Storage::FileSystem::{
        FILE_DIRECTORY_FILE, FILE_NON_DIRECTORY_FILE, FILE_OPEN, FILE_OPEN_REPARSE_POINT,
    },
    Win32::{
        Foundation::{ERROR_NO_MORE_FILES, HANDLE},
        Storage::FileSystem::{
            FILE_ATTRIBUTE_DIRECTORY, FILE_ATTRIBUTE_REPARSE_POINT, FILE_GENERIC_READ,
            FILE_ID_BOTH_DIR_INFO, FILE_SHARE_READ, FILE_SHARE_WRITE, FileIdBothDirectoryInfo,
            FileIdBothDirectoryRestartInfo, GetFileInformationByHandleEx,
        },
        System::Threading::{
            GetProcessMitigationPolicy, ProcessExtensionPointDisablePolicy, ProcessImageLoadPolicy,
            ProcessSignaturePolicy,
        },
    },
};
const IMAGE: &str = "OpenBlade.CandidateReceipt.Scratch.exe";
const READINESS: &str = "openblade-scratch-candidate-receipt.exe";
const PREFIX: &str = "OpenBlade.ScratchNativeReceiver.v2";

pub(crate) struct ScratchReceiverClosure {
    owner: Vec<u8>,
    volume: u32,
    directory: File,
    directory_identity: FileIdentity,
    file: File,
    identity: FileIdentity,
    snapshot: ImageSnapshot,
    digest: [u8; 32],
}
impl ScratchReceiverClosure {
    pub(crate) fn retain(
        scratch: &File,
        owner: &[u8],
        expected_digest: [u8; 32],
    ) -> io::Result<Self> {
        let volume = directory_info(scratch)?.dwVolumeSerialNumber;
        let directory = open_relative(
            scratch,
            OsStr::new("receiver"),
            FILE_GENERIC_READ,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            FILE_OPEN,
            FILE_DIRECTORY_FILE | FILE_OPEN_REPARSE_POINT,
        )?;
        let directory_identity = file_identity(&directory_info(&directory)?);
        let file = open_relative(
            &directory,
            OsStr::new(IMAGE),
            FILE_GENERIC_READ,
            FILE_SHARE_READ,
            FILE_OPEN,
            FILE_NON_DIRECTORY_FILE | FILE_OPEN_REPARSE_POINT,
        )?;
        let identity = check_regular_file(&file)?;
        let snapshot = service_image::snapshot(&file)?;
        let mut retained = Self {
            owner: owner.to_vec(),
            volume,
            directory,
            directory_identity,
            file,
            identity,
            snapshot,
            digest: expected_digest,
        };
        retained.require_unchanged()?;
        Ok(retained)
    }
    pub(crate) fn require_unchanged(&mut self) -> io::Result<[u8; 32]> {
        self.require_members()?;
        require_identity(check_regular_file(&self.file)?, self.identity, self.volume)?;
        verify_descriptor(&self.file, |bytes| {
            scratch_receipt_policy::accepts(bytes, &self.owner, false, false)
        })?;
        if !(256..=service_image::MAX_IMAGE_BYTES).contains(&self.snapshot.size)
            || service_image::snapshot(&self.file)? != self.snapshot
        {
            return Err(denied(
                "native receiver member changed or exceeds its size bound",
            ));
        }
        self.file.rewind()?;
        let mut bytes = Vec::new();
        (&mut self.file)
            .take(self.snapshot.size + 1)
            .read_to_end(&mut bytes)?;
        if bytes.len() as u64 != self.snapshot.size
            || service_image::snapshot(&self.file)? != self.snapshot
            || !accept_native_image(&bytes)
        {
            return Err(denied(
                "scratch receiver requires its native x64 AOT executable",
            ));
        }
        let actual = encode_digest(&bytes);
        self.require_members()?;
        require_digest(actual, self.digest)?;
        Ok(actual)
    }
    fn require_members(&self) -> io::Result<()> {
        require_identity(
            file_identity(&directory_info(&self.directory)?),
            self.directory_identity,
            self.volume,
        )?;
        verify_descriptor(&self.directory, |bytes| {
            scratch_receipt_policy::accepts(bytes, &self.owner, true, false)
        })?;
        let mut image = false;
        for (name, attrs) in enumerate(&self.directory, 2)? {
            if attrs & (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT) != 0 {
                return Err(denied("unexpected native receiver directory"));
            }
            match name.as_str() {
                IMAGE if !image => image = true,
                READINESS => (),
                _ => return Err(denied("obsolete or unexpected native receiver member")),
            }
        }
        if !image {
            return Err(denied("missing native receiver"));
        }
        Ok(())
    }
}
fn encode_digest(bytes: &[u8]) -> [u8; 32] {
    let mut encoded = Vec::new();
    write_string(&mut encoded, PREFIX);
    write_string(&mut encoded, IMAGE);
    encoded.extend_from_slice(&(bytes.len() as u64).to_le_bytes());
    encoded.extend_from_slice(&Sha256::digest(bytes));
    Sha256::digest(encoded).into()
}
fn write_string(encoded: &mut Vec<u8>, value: &str) {
    let mut length = value.len();
    while length >= 128 {
        encoded.push((length as u8 & 0x7f) | 0x80);
        length >>= 7;
    }
    encoded.push(length as u8);
    encoded.extend_from_slice(value.as_bytes());
}
fn require_digest(actual: [u8; 32], expected: [u8; 32]) -> io::Result<()> {
    if actual != expected {
        return Err(denied(
            "native receiver differs from authenticated launch evidence",
        ));
    }
    Ok(())
}
fn require_identity(actual: FileIdentity, expected: FileIdentity, volume: u32) -> io::Result<()> {
    if actual != expected || actual.index == 0 || actual.volume != volume {
        return Err(denied("native receiver identity or volume changed"));
    }
    Ok(())
}
pub(crate) fn accept_policies(signature: u32, extensions: u32, image_load: u32) -> bool {
    signature == 1 && extensions == 1 && image_load == 4
}
pub(crate) fn require_process_policies(process: HANDLE) -> io::Result<()> {
    let mut signature = 0u32;
    let mut extensions = 0u32;
    let mut image_load = 0u32;
    // SAFETY: caller retains the authenticated original process handle; outputs are u32 policy structures.
    unsafe {
        if GetProcessMitigationPolicy(
            process,
            ProcessSignaturePolicy,
            (&mut signature as *mut u32).cast(),
            4,
        ) == 0
            || GetProcessMitigationPolicy(
                process,
                ProcessExtensionPointDisablePolicy,
                (&mut extensions as *mut u32).cast(),
                4,
            ) == 0
            || GetProcessMitigationPolicy(
                process,
                ProcessImageLoadPolicy,
                (&mut image_load as *mut u32).cast(),
                4,
            ) == 0
        {
            return Err(io::Error::last_os_error());
        }
    }
    if !accept_policies(signature, extensions, image_load) {
        return Err(denied(
            "native scratch receiver lacks its required creation policies",
        ));
    }
    Ok(())
}
// Format discrimination only; same bounded decoder as C#, never a signature claim.
pub(crate) fn accept_native_image(bytes: &[u8]) -> bool {
    native_image(bytes) == Some(true)
}
fn native_image(b: &[u8]) -> Option<bool> {
    let u16at = |i: usize| {
        Some(u16::from_le_bytes(
            b.get(i..i.checked_add(2)?)?.try_into().ok()?,
        ))
    };
    let u32at = |i: usize| {
        Some(u32::from_le_bytes(
            b.get(i..i.checked_add(4)?)?.try_into().ok()?,
        ))
    };
    if u16at(0)? != 0x5a4d {
        return Some(false);
    }
    let pe = u32at(60)? as usize;
    let o = pe.checked_add(24)?;
    let sections = u16at(pe.checked_add(6)?)? as usize;
    if u32at(pe)? != 0x4550
        || u16at(pe + 4)? != 0x8664
        || !(1..=96).contains(&sections)
        || u16at(pe + 22)? & 0x2002 != 2
        || u16at(pe + 20)? < 240
        || u16at(o)? != 0x20b
        || u32at(o + 16)? == 0
        || u32at(o + 108)? < 16
        || u32at(o + 112)? == 0
        || u32at(o + 116)? == 0
        || u32at(o + 216)? != 0
        || u32at(o + 220)? != 0
        || u32at(o + 224)? != 0
        || u32at(o + 228)? != 0
    {
        return Some(false);
    }
    let offset = |rva: u32, size: usize| -> Option<usize> {
        for i in 0..sections {
            let s = o
                .checked_add(u16at(pe + 20)? as usize)?
                .checked_add(40 * i)?;
            let start = u32at(s + 12)?;
            let raw = u32at(s + 16)?;
            if rva >= start && u64::from(rva) - u64::from(start) + size as u64 <= u64::from(raw) {
                let at =
                    usize::try_from(u64::from(u32at(s + 20)?) + u64::from(rva) - u64::from(start))
                        .ok()?;
                b.get(at..at.checked_add(size)?)?;
                return Some(at);
            }
        }
        None
    };
    let exports = offset(u32at(o + 112)?, 40)?;
    if u32at(exports + 24)? != 1 {
        return Some(false);
    }
    let names = offset(u32at(exports + 32)?, 4)?;
    let name = offset(u32at(names)?, 25)?;
    Some(b.get(name..name + 25)? == b"DotNetRuntimeDebugHeader\0")
}
fn enumerate(directory: &File, maximum_members: usize) -> io::Result<Vec<(String, u32)>> {
    let mut result = Vec::new();
    // u64 storage provides the alignment required by FILE_ID_BOTH_DIR_INFO.
    let mut buffer = vec![0u64; 8192];
    let mut class = FileIdBothDirectoryRestartInfo;
    loop {
        // SAFETY: directory is a live retained directory handle; the aligned
        // allocation is writable and its exact byte capacity is supplied.
        let ok = unsafe {
            GetFileInformationByHandleEx(
                directory.as_raw_handle(),
                class,
                buffer.as_mut_ptr().cast(),
                (buffer.len() * 8) as u32,
            )
        };
        if ok == 0 {
            let error = io::Error::last_os_error();
            if error.raw_os_error() == Some(ERROR_NO_MORE_FILES as i32) {
                break;
            }
            return Err(error);
        }
        class = FileIdBothDirectoryInfo;
        // SAFETY: buffer owns this many initialized bytes throughout parsing.
        let bytes =
            unsafe { std::slice::from_raw_parts(buffer.as_ptr().cast::<u8>(), buffer.len() * 8) };
        let mut offset = 0;
        loop {
            let entry = bytes
                .get(offset..)
                .ok_or_else(|| invalid("invalid receiver directory enumeration"))?;
            let next =
                dword(entry, 0).ok_or_else(|| invalid("short receiver directory entry"))? as usize;
            let length = dword(
                entry,
                std::mem::offset_of!(FILE_ID_BOTH_DIR_INFO, FileNameLength),
            )
            .ok_or_else(|| invalid("short receiver directory entry"))?
                as usize;
            let start = std::mem::offset_of!(FILE_ID_BOTH_DIR_INFO, FileName);
            if length == 0 || length % 2 != 0 || length > 510 {
                return Err(invalid("invalid receiver member name length"));
            }
            let name_bytes = entry
                .get(start..start + length)
                .ok_or_else(|| invalid("short receiver member name"))?;
            let utf16: Vec<_> = name_bytes
                .chunks_exact(2)
                .map(|v| u16::from_le_bytes([v[0], v[1]]))
                .collect();
            let name =
                String::from_utf16(&utf16).map_err(|_| invalid("invalid receiver member name"))?;
            let attributes = dword(
                entry,
                std::mem::offset_of!(FILE_ID_BOTH_DIR_INFO, FileAttributes),
            )
            .ok_or_else(|| invalid("short receiver directory entry"))?;
            if name != "." && name != ".." {
                result.push((name, attributes));
                // Bound allocation to the exact expected direct children, plus
                // the sole readiness exception at the receiver root.
                if result.len() > maximum_members {
                    return Err(invalid("receiver namespace exceeds its member bound"));
                }
            }
            if next == 0 {
                break;
            }
            if next < start + length || !next.is_multiple_of(8) {
                return Err(invalid("invalid receiver directory entry offset"));
            }
            offset = offset
                .checked_add(next)
                .ok_or_else(|| invalid("receiver enumeration overflow"))?;
        }
    }
    Ok(result)
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn native_byte_change_reaches_the_authenticated_digest_guard() {
        let v: serde_json::Value = serde_json::from_str(include_str!(
            "../tests/data/scratch-native-receiver-v2.json"
        ))
        .unwrap();
        let mut image: Vec<u8> = v["imageHex"]
            .as_str()
            .unwrap()
            .as_bytes()
            .chunks_exact(2)
            .map(|x| u8::from_str_radix(std::str::from_utf8(x).unwrap(), 16).unwrap())
            .collect();
        let original = encode_digest(&image);
        require_digest(original, original).unwrap();
        image[440] ^= 1;
        assert!(accept_native_image(&image));
        assert_eq!(
            require_digest(encode_digest(&image), original)
                .unwrap_err()
                .to_string(),
            "native receiver differs from authenticated launch evidence"
        );
    }
    #[test]
    fn retained_identity_index_and_volume_guards_are_independent() {
        let expected = FileIdentity {
            volume: 7,
            index: 42,
        };
        require_identity(expected, expected, 7).unwrap();
        for (actual, volume) in [
            (
                FileIdentity {
                    volume: 7,
                    index: 43,
                },
                7,
            ),
            (
                FileIdentity {
                    volume: 8,
                    index: 42,
                },
                7,
            ),
            (
                FileIdentity {
                    volume: 7,
                    index: 0,
                },
                7,
            ),
            (expected, 8),
        ] {
            assert_eq!(
                require_identity(actual, expected, volume)
                    .unwrap_err()
                    .to_string(),
                "native receiver identity or volume changed"
            );
        }
        let zero = FileIdentity {
            volume: 7,
            index: 0,
        };
        assert!(require_identity(zero, zero, 7).is_err());
    }
    #[test]
    fn shared_native_vector_and_policy_refusals() {
        let v: serde_json::Value = serde_json::from_str(include_str!(
            "../tests/data/scratch-native-receiver-v2.json"
        ))
        .unwrap();
        let b: Vec<u8> = v["imageHex"]
            .as_str()
            .unwrap()
            .as_bytes()
            .chunks_exact(2)
            .map(|x| u8::from_str_radix(std::str::from_utf8(x).unwrap(), 16).unwrap())
            .collect();
        assert!(accept_native_image(&b));
        let digest: String = encode_digest(&b)
            .iter()
            .map(|b| format!("{b:02x}"))
            .collect();
        assert_eq!(digest, v["canonicalSha256"].as_str().unwrap());
        for size in 0..b.len() {
            assert!(!accept_native_image(&b[..size]));
        }
        for offset in [0, 60, 132, 150, 152, 264, 368, 496] {
            let mut changed = b.clone();
            changed[offset] ^= 0xff;
            assert!(!accept_native_image(&changed), "offset {offset}");
        }
        assert!(accept_policies(1, 1, 4));
        for bits in [0, 2, 3, 5, 0xffffffff] {
            assert!(!accept_policies(bits, 1, 4));
            assert!(!accept_policies(1, bits, 4));
            assert!(!accept_policies(1, 1, bits));
        }
    }
}

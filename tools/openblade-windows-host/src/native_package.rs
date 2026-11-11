//! Independently reconstructible package/installation commitment. This module
//! grants no loaded-image, process, native-drain, or HID ownership authority.
#![allow(unsafe_code, dead_code)]

use crate::windows_protected_path::{self as paths, Policy};
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use std::{
    ffi::OsStr,
    fs::File,
    io::{self, Read, Seek},
    path::PathBuf,
};
use windows_sys::{
    Wdk::Storage::FileSystem::{FILE_NON_DIRECTORY_FILE, FILE_OPEN, FILE_OPEN_REPARSE_POINT},
    Win32::{
        Security::Cryptography::*,
        Storage::FileSystem::{FILE_GENERIC_READ, FILE_SHARE_DELETE, FILE_SHARE_READ},
        UI::Shell::FOLDERID_ProgramData,
    },
};

const ZERO: &str = "0000000000000000000000000000000000000000000000000000000000000000";
const DOMAIN: &[u8] = b"OpenBlade-Local-Dev-Deployment-Manifest-v1\0";
const ROLES: &[u8] = b"OpenBlade.WindowsNativeRoles\0v1\0win-x64\0";

fn deny() -> io::Error {
    paths::denied("native package/epoch evidence is unavailable, changed, or unadmitted")
}
fn hex(bytes: &[u8]) -> String {
    bytes.iter().map(|b| format!("{b:02x}")).collect()
}
fn digest(bytes: &[u8]) -> String {
    hex(&Sha256::digest(bytes))
}
fn is_hex(value: &str, length: usize) -> bool {
    value.len() == length
        && value
            .bytes()
            .all(|b| b.is_ascii_digit() || (b'a'..=b'f').contains(&b))
}
fn lines<'a>(bytes: &'a [u8], header: &str, count: usize) -> io::Result<Vec<&'a str>> {
    if bytes.is_empty()
        || bytes.len() > 16384
        || bytes
            .iter()
            .any(|&b| b != 10 && !(0x20..=0x7e).contains(&b))
    {
        return Err(deny());
    }
    let value = std::str::from_utf8(bytes).map_err(|_| deny())?;
    let parts: Vec<_> = value.split('\n').collect();
    if parts.len() != count + 2 || parts[0] != header || parts.last() != Some(&"") {
        return Err(deny());
    }
    Ok(parts[1..parts.len() - 1].to_vec())
}
fn encode(header: &str, fields: &[&str]) -> Vec<u8> {
    format!("{header}\n{}\n", fields.join("\n")).into_bytes()
}

#[derive(Clone, Debug, PartialEq, Eq)]
struct Epoch {
    phase: String,
    id: String,
    predecessor: String,
    ordinal: u64,
    installation: String,
    package: String,
    request: String,
    service: String,
    agent: String,
    readiness: String,
    owner: String,
}
impl Epoch {
    fn parse(bytes: &[u8]) -> io::Result<Self> {
        let f = lines(bytes, "OpenBlade.NativeEpoch.v1", 11)?;
        let ordinal = f[3].parse::<u64>().map_err(|_| deny())?;
        if !matches!(f[0], "Provisional" | "Committed" | "Unresolved" | "Retired")
            || !is_hex(f[1], 32)
            || f[1].bytes().all(|b| b == b'0')
            || !is_hex(f[2], 64)
            || ordinal == 0
            || ordinal > i64::MAX as u64
            || ordinal.to_string() != f[3]
            || !is_hex(f[4], 32)
            || !is_hex(f[5], 64)
            || !is_hex(f[6], 32)
            || !artifact_id(f[7], "service")
            || !artifact_id(f[8], "agent")
            || !is_hex(f[9], 64)
            || !is_hex(f[10], 64)
            || (f[0] == "Committed") == (f[9] == ZERO)
        {
            return Err(deny());
        }
        Ok(Self {
            phase: f[0].into(),
            id: f[1].into(),
            predecessor: f[2].into(),
            ordinal,
            installation: f[4].into(),
            package: f[5].into(),
            request: f[6].into(),
            service: f[7].into(),
            agent: f[8].into(),
            readiness: f[9].into(),
            owner: f[10].into(),
        })
    }
    fn same_origin(&self, other: &Self) -> bool {
        self.id == other.id
            && self.predecessor == other.predecessor
            && self.ordinal == other.ordinal
            && self.installation == other.installation
            && self.package == other.package
            && self.request == other.request
            && self.service == other.service
            && self.agent == other.agent
            && self.owner == other.owner
    }
}
fn artifact_id(id: &str, role: &str) -> bool {
    id.len() == 65 + role.len()
        && id.ends_with(&format!("-{role}"))
        && is_hex(&id[..64], 64)
        && &id[..64] != ZERO
}

#[derive(Deserialize, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Release {
    schema_version: u32,
    signature_algorithm: String,
    signing_key_curve: String,
    signing_key_id: String,
    installation_id: String,
    sequence: u64,
    channel: String,
    product: String,
    version: String,
    source_revision: String,
    architecture: String,
    minimum_windows_build: u32,
    installer_file_name: String,
    bundle_id: String,
    provider_key: String,
    upgrade_code: String,
    installer_size: u64,
    installer_sha256: String,
}
#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Enrollment {
    schema_version: u32,
    installation_id: String,
    enrolled_account_sid: String,
    deployment_policy_id: String,
    broker_version: String,
    development_signing_key_id: String,
    development_signing_subject_public_key_info_base64: String,
    enrolled_at: String,
    #[serde(default)]
    cpu_voltage_validation_enabled: bool,
}

fn decode64(value: &str) -> io::Result<Vec<u8>> {
    if value.is_empty() || !value.len().is_multiple_of(4) || value.len() > 4096 {
        return Err(deny());
    }
    let alphabet = b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    let mut out = Vec::new();
    for (i, group) in value.as_bytes().chunks_exact(4).enumerate() {
        let mut v = [0u32; 4];
        let mut pad = 0;
        for j in 0..4 {
            if group[j] == b'=' {
                if j < 2 {
                    return Err(deny());
                }
                pad += 1;
            } else {
                if pad != 0 {
                    return Err(deny());
                }
                v[j] = alphabet
                    .iter()
                    .position(|&c| c == group[j])
                    .ok_or_else(deny)? as u32;
            }
        }
        if pad > 2
            || pad > 0 && i + 1 != value.len() / 4
            || pad == 1 && v[2] & 3 != 0
            || pad == 2 && v[1] & 15 != 0
        {
            return Err(deny());
        }
        let n = v[0] << 18 | v[1] << 12 | v[2] << 6 | v[3];
        out.push((n >> 16) as u8);
        if pad < 2 {
            out.push((n >> 8) as u8);
        }
        if pad < 1 {
            out.push(n as u8);
        }
    }
    Ok(out)
}

// CNG verification only: no certificate store, trust provisioning, network, or
// key generation. The key is acquired from fixed protected owner enrollment.
fn verify_p256(spki: &[u8], signature: &[u8], hash: &[u8; 32]) -> io::Result<()> {
    const PREFIX: &[u8] = &[
        0x30, 0x59, 0x30, 0x13, 0x06, 0x07, 0x2a, 0x86, 0x48, 0xce, 0x3d, 0x02, 0x01, 0x06, 0x08,
        0x2a, 0x86, 0x48, 0xce, 0x3d, 0x03, 0x01, 0x07, 0x03, 0x42, 0x00, 0x04,
    ];
    if spki.len() != 91 || !spki.starts_with(PREFIX) || signature.len() != 64 {
        return Err(deny());
    }
    let mut algorithm = std::ptr::null_mut();
    // SAFETY: fixed CNG algorithm, valid output and static UTF-16 identifier.
    if unsafe {
        BCryptOpenAlgorithmProvider(
            &mut algorithm,
            BCRYPT_ECDSA_P256_ALGORITHM,
            std::ptr::null(),
            0,
        )
    } < 0
    {
        return Err(deny());
    }
    struct Algorithm(BCRYPT_ALG_HANDLE);
    impl Drop for Algorithm {
        fn drop(&mut self) {
            unsafe {
                BCryptCloseAlgorithmProvider(self.0, 0);
            }
        }
    }
    let algorithm = Algorithm(algorithm);
    let mut blob = Vec::from(BCRYPT_ECDSA_PUBLIC_P256_MAGIC.to_le_bytes());
    blob.extend_from_slice(&32u32.to_le_bytes());
    blob.extend_from_slice(&spki[PREFIX.len()..]);
    let mut key = std::ptr::null_mut();
    // SAFETY: bounded ECCPUBLICBLOB contains exactly the P-256 X/Y coordinates.
    if unsafe {
        BCryptImportKeyPair(
            algorithm.0,
            std::ptr::null_mut(),
            BCRYPT_ECCPUBLIC_BLOB,
            &mut key,
            blob.as_ptr(),
            blob.len() as u32,
            0,
        )
    } < 0
    {
        return Err(deny());
    }
    struct Key(BCRYPT_KEY_HANDLE);
    impl Drop for Key {
        fn drop(&mut self) {
            unsafe {
                BCryptDestroyKey(self.0);
            }
        }
    }
    let key = Key(key);
    // SAFETY: fixed hash and P1363 signature sizes; retained algorithm/key lifetime.
    if unsafe {
        BCryptVerifySignature(
            key.0,
            std::ptr::null(),
            hash.as_ptr(),
            32,
            signature.as_ptr(),
            64,
            0,
        )
    } < 0
    {
        return Err(deny());
    }
    Ok(())
}

fn authenticate(enrollment: &[u8], manifest: &[u8], signature: &[u8]) -> io::Result<Release> {
    if enrollment.len() > 2048 || manifest.len() > 4096 {
        return Err(deny());
    }
    let e: Enrollment = serde_json::from_slice(enrollment).map_err(|_| deny())?;
    if e.schema_version != 1
        || e.enrolled_account_sid.is_empty()
        || e.deployment_policy_id.is_empty()
        || e.broker_version.is_empty()
        || e.enrolled_at.is_empty()
    {
        return Err(deny());
    }
    authenticate_key(
        &e.installation_id,
        &e.development_signing_key_id,
        &e.development_signing_subject_public_key_info_base64,
        manifest,
        signature,
    )
}
fn authenticate_key(
    installation: &str,
    key: &str,
    spki64: &str,
    manifest: &[u8],
    signature: &[u8],
) -> io::Result<Release> {
    if manifest.len() > 4096 {
        return Err(deny());
    }
    let r: Release = serde_json::from_slice(manifest).map_err(|_| deny())?;
    if installation != r.installation_id
        || r.schema_version != 1
        || r.channel != "LocalDev"
        || r.product != "OpenBlade"
        || r.signature_algorithm != "ECDSA-P256-SHA256-P1363"
        || r.signing_key_curve != "P-256"
        || r.architecture != "x64"
        || r.minimum_windows_build != 26100
        || r.installer_file_name != "OpenBlade-Setup.exe"
        || r.bundle_id != "OSSBlade.OpenBlade"
        || r.provider_key != "OSSBlade.OpenBlade"
        || r.upgrade_code != "FF859CA9-D69F-4995-A2AD-69E540E45724"
        || r.sequence == 0
        || r.sequence > i64::MAX as u64
        || r.installer_size == 0
        || r.installer_size > 1024 * 1024 * 1024
        || !is_hex(&r.installer_sha256, 64)
        || !is_hex(&r.source_revision, 40)
        || serde_json::to_vec(&r).map_err(|_| deny())? != manifest
    {
        return Err(deny());
    }
    let version: Vec<_> = r.version.split('.').collect();
    if version.len() != 4
        || version[0] != "65535"
        || version
            .iter()
            .any(|s| s.parse::<u16>().is_err() || s.parse::<u16>().unwrap().to_string() != *s)
    {
        return Err(deny());
    }
    let spki = decode64(spki64)?;
    if key != digest(&spki) || r.signing_key_id != key {
        return Err(deny());
    }
    let mut hash = Sha256::new();
    hash.update(DOMAIN);
    hash.update(manifest);
    verify_p256(&spki, signature, &hash.finalize().into())?;
    Ok(r)
}

fn authenticate_bound_projection(
    projection: &[u8],
    expected: &str,
    manifest: &[u8],
    signature: &[u8],
) -> io::Result<Release> {
    if digest(projection) != expected {
        return Err(deny());
    }
    authenticate_projection(projection, manifest, signature)
}
fn authenticate_projection(
    projection: &[u8],
    manifest: &[u8],
    signature: &[u8],
) -> io::Result<Release> {
    let f = lines(projection, "OpenBlade.NativeVerification.v1", 6)?;
    if f[0] != "LocalDev" || f[2].is_empty() || !is_hex(f[3], 64) || !is_hex(f[5], 64) {
        return Err(deny());
    }
    // Custody is checked by the fixed protected generation reader before this
    // crypto function. An adjacent key alone is never a trust root.
    authenticate_key(f[1], f[3], f[4], manifest, signature)
}

// Exact .obid payload in a fully verified native PE. Geometry is checked again;
// this never substitutes for inspect_artifact's full digest/role/target mapping.
fn compiled_record(image: &[u8]) -> io::Result<&[u8]> {
    let u16at = |o: usize| -> io::Result<usize> {
        Ok(u16::from_le_bytes(
            image
                .get(o..o + 2)
                .ok_or_else(deny)?
                .try_into()
                .map_err(|_| deny())?,
        ) as usize)
    };
    let u32at = |o: usize| -> io::Result<usize> {
        Ok(u32::from_le_bytes(
            image
                .get(o..o + 4)
                .ok_or_else(deny)?
                .try_into()
                .map_err(|_| deny())?,
        ) as usize)
    };
    let pe = u32at(60)?;
    let sections = pe.checked_add(24 + u16at(pe + 20)?).ok_or_else(deny)?;
    let mut result = None;
    for n in 0..u16at(pe + 6)? {
        let o = sections + 40 * n;
        if image.get(o..o + 8) == Some(b".obid\0\0\0") {
            if result.is_some() {
                return Err(deny());
            }
            let size = u32at(o + 8)?;
            let raw = u32at(o + 20)?;
            if size == 0 || size > 1024 {
                return Err(deny());
            }
            result = image.get(raw..raw.checked_add(size).ok_or_else(deny)?);
        }
    }
    result.ok_or_else(deny)
}

fn inspect_artifact(
    record: &[u8],
    image: &[u8],
    role: &str,
    release: &Release,
) -> io::Result<String> {
    let f = lines(record, "OpenBlade.NativeArtifactRecord.v1", 10)?;
    if !artifact_id(f[0], role)
        || f[1] != role
        || f[2] != "x86_64-pc-windows-msvc"
        || f[3] != release.source_revision
        || f[7] != image.len().to_string()
        || f[8] != digest(image)
        || !f[5].starts_with("rustc ")
        || !matches!(f[6], "debug" | "release")
    {
        return Err(deny());
    }
    let features: Vec<_> = f[4].split(',').collect();
    let mut ordered = features.clone();
    ordered.sort_unstable();
    ordered.dedup();
    if features != ordered
        || !features.contains(&if role == "service" {
            "installed-service-candidate"
        } else {
            "native-agent-entry-compile-check"
        })
    {
        return Err(deny());
    }
    let u16_at = |n: usize| -> io::Result<u16> {
        Ok(u16::from_le_bytes(
            image.get(n..n + 2).ok_or_else(deny)?.try_into().unwrap(),
        ))
    };
    let u32_at = |n: usize| -> io::Result<u32> {
        Ok(u32::from_le_bytes(
            image.get(n..n + 4).ok_or_else(deny)?.try_into().unwrap(),
        ))
    };
    if image.len() < 512 || image.len() > 64 * 1024 * 1024 || image.get(..2) != Some(b"MZ") {
        return Err(deny());
    }
    let pe = u32_at(60)? as usize;
    if pe > 1024 * 1024
        || image.get(pe..pe + 4) != Some(b"PE\0\0")
        || u16_at(pe + 4)? != 0x8664
        || u16_at(pe + 22)? & 0x2002 != 2
        || u16_at(pe + 20)? != 240
        || u16_at(pe + 24)? != 0x20b
        || u32_at(pe + 24 + 108)? < 16
        || u32_at(pe + 24 + 224)? != 0
        || u32_at(pe + 24 + 228)? != 0
        || u32_at(pe + 24 + 216)? != 0
        || u32_at(pe + 24 + 220)? != 0
    {
        return Err(deny());
    }
    let count = u16_at(pe + 6)? as usize;
    if !(1..=96).contains(&count) {
        return Err(deny());
    }
    let mut marker = None;
    let mut sections = Vec::new();
    for i in 0..count {
        let start = pe + 24 + 240 + 40 * i;
        let name = image.get(start..start + 8).ok_or_else(deny)?;
        let size = u32_at(start + 8)? as usize;
        let rva = u32_at(start + 12)? as usize;
        let raw_size = u32_at(start + 16)? as usize;
        let raw = u32_at(start + 20)? as usize;
        if raw
            .checked_add(raw_size)
            .is_none_or(|end| end > image.len())
        {
            return Err(deny());
        }
        sections.push((rva, size, raw, raw_size));
        if name == b".obid\0\0\0" {
            if marker.is_some() || size == 0 || size > 1024 || raw_size < size {
                return Err(deny());
            }
            marker = Some(image.get(raw..raw + size).ok_or_else(deny)?);
        }
    }
    if marker != Some(encode("OpenBlade.NativeArtifact.v1", &f[..7]).as_slice()) {
        return Err(deny());
    }
    let section_bytes = |rva: usize| -> io::Result<&[u8]> {
        let matches: Vec<_> = sections
            .iter()
            .filter(|(base, size, _, _)| rva >= *base && rva - base < *size)
            .collect();
        if matches.len() != 1 {
            return Err(deny());
        }
        let (base, size, raw, raw_size) = *matches[0];
        let offset = rva - base;
        if offset >= raw_size {
            return Err(deny());
        }
        image
            .get(raw + offset..raw + size.min(raw_size))
            .ok_or_else(deny)
    };
    let import_rva = u32_at(pe + 24 + 120)? as usize;
    let import_size = u32_at(pe + 24 + 124)? as usize;
    if !(20..=65536).contains(&import_size) {
        return Err(deny());
    }
    let table = section_bytes(import_rva)?
        .get(..import_size)
        .ok_or_else(deny)?;
    let mut imports = std::collections::BTreeSet::new();
    let mut terminated = false;
    for entry in table.chunks_exact(20) {
        if entry.iter().all(|&b| b == 0) {
            terminated = true;
            break;
        }
        let name_rva = u32::from_le_bytes(entry[12..16].try_into().unwrap()) as usize;
        let name = section_bytes(name_rva)?;
        let end = name.iter().position(|&b| b == 0).ok_or_else(deny)?;
        if !(1..=120).contains(&end) || name[..end].iter().any(|b| !(0x21..=0x7e).contains(b)) {
            return Err(deny());
        }
        let name = std::str::from_utf8(&name[..end])
            .map_err(|_| deny())?
            .to_ascii_lowercase();
        if !system_import(&name) {
            return Err(deny());
        }
        imports.insert(name);
    }
    if !terminated
        || imports.into_iter().collect::<Vec<_>>().join(",") != f[9]
        || f[5].len() > 160
        || features
            .iter()
            .any(|s| s.bytes().any(|b| !b.is_ascii_alphanumeric() && b != b'-'))
    {
        return Err(deny());
    }
    Ok(f[0].into())
}

fn system_import(name: &str) -> bool {
    matches!(
        name,
        "combase.dll"
            | "bcryptprimitives.dll"
            | "vcruntime140.dll"
            | "kernel32.dll"
            | "ntdll.dll"
            | "advapi32.dll"
            | "bcrypt.dll"
            | "crypt32.dll"
            | "user32.dll"
            | "userenv.dll"
            | "shell32.dll"
            | "ole32.dll"
            | "oleaut32.dll"
            | "ws2_32.dll"
            | "iphlpapi.dll"
            | "setupapi.dll"
            | "cfgmgr32.dll"
            | "powrprof.dll"
            | "wtsapi32.dll"
            | "secur32.dll"
            | "version.dll"
            | "wintrust.dll"
            | "psapi.dll"
            | "gdi32.dll"
            | "dwmapi.dll"
            | "comctl32.dll"
            | "comdlg32.dll"
            | "shlwapi.dll"
            | "hid.dll"
            | "winmm.dll"
            | "dbghelp.dll"
            | "msvcrt.dll"
    ) || name.starts_with("api-ms-win-")
        && name.ends_with(".dll")
        && name
            .bytes()
            .all(|b| b.is_ascii_alphanumeric() || b == b'-' || b == b'.')
}

pub(crate) struct InstalledPackage {
    root: File,
    _ancestors: Vec<File>,
    directories: Vec<File>,
    held: Vec<File>,
    selection: Vec<u8>,
    root_path: PathBuf,
    pub(crate) service_path: PathBuf,
    pub(crate) agent_path: PathBuf,
    pub(crate) commitment: [u8; 32],
    denied: bool,
    epoch: Epoch,
    startup_owner: Option<crate::retained_process::RetainedProcessInstance>,
    service_identity: Vec<u8>,
    agent_identity: Vec<u8>,
}
impl InstalledPackage {
    pub(crate) fn record_metadata_drain(&self, pid: u32, created: u64) -> io::Result<()> {
        use std::io::Write;
        let name = format!("status-drain-{pid}-{created}.bin");
        let mut file = paths::open_relative(
            &self.directories[0],
            OsStr::new(&name),
            windows_sys::Win32::Storage::FileSystem::FILE_GENERIC_READ
                | windows_sys::Win32::Storage::FileSystem::FILE_GENERIC_WRITE,
            0,
            windows_sys::Wdk::Storage::FileSystem::FILE_CREATE,
            FILE_NON_DIRECTORY_FILE | FILE_OPEN_REPARSE_POINT,
        )?;
        paths::check_regular_file(&file)?;
        paths::verify_security(&file, Policy::Record)?;
        let bytes = encode(
            "OpenBlade.NativeStatusDrain.v1",
            &[&self.epoch.id, &pid.to_string(), &created.to_string()],
        );
        file.write_all(&bytes)?;
        file.sync_all()?;
        if read_bytes(&mut file, 4096)? != bytes {
            return Err(deny());
        }
        Ok(())
    }
    pub(crate) fn identity(&self, role: &str) -> &[u8] {
        if role == "service" {
            &self.service_identity
        } else {
            &self.agent_identity
        }
    }
    pub(crate) fn epoch_id(&self) -> &str {
        &self.epoch.id
    }
    pub(crate) fn installation(&self) -> &str {
        &self.epoch.installation
    }
    pub(crate) fn is_committed(&self) -> bool {
        self.epoch.phase == "Committed"
    }

    pub(crate) fn require_current(&mut self) -> io::Result<()> {
        if self.denied {
            return Err(deny());
        }
        let result = (|| {
            for (i, d) in self._ancestors.iter().enumerate() {
                paths::verify_security(
                    d,
                    if i + 1 == self._ancestors.len() {
                        Policy::PROGRAM_DATA
                    } else {
                        Policy::ANCESTOR
                    },
                )?;
            }
            paths::verify_descriptor(&self.root, paths::accepts_public_native_root)?;
            for d in &self.directories {
                paths::verify_security(d, Policy::Directory { extra_rights: 0 })?;
            }
            for file in &self.held {
                paths::check_regular_file(file)?;
                paths::verify_security(file, Policy::Record)?;
            }
            // The only permitted in-place phase change is this exact live
            // startup attempt becoming committed after bound readiness.
            if self.epoch.phase == "Provisional" {
                let mut current = open_file(&self.root, "current.bin", true)?;
                let bytes = read_bytes(&mut current, 16384)?;
                if bytes != self.selection {
                    let next = Epoch::parse(&bytes)?;
                    if next.phase != "Committed" || !self.epoch.same_origin(&next) {
                        return Err(deny());
                    }
                    frontier_matches(&self.root, &self.root_path, &bytes)?;
                    let phase =
                        retain_bytes(&self.directories[0], "Committed.bin", 16384, &mut self.held)?;
                    if phase != bytes {
                        return Err(deny());
                    }
                    self.epoch = next;
                    self.selection = bytes;
                    self.startup_owner = None;
                }
            }
            frontier_matches(&self.root, &self.root_path, &self.selection)?;
            app_names(&self.service_path.parent().ok_or_else(deny)?.to_path_buf())?;
            let mut current = open_file(&self.root, "current.bin", true)?;
            let bytes = read_bytes(&mut current, 16384)?;
            if bytes != self.selection {
                return Err(deny());
            }
            if let Some(owner) = &mut self.startup_owner {
                owner
                    .check_live()
                    .map_err(crate::retained_process::instance_io)?;
            }
            Ok(())
        })();
        if result.is_err() {
            self.denied = true;
        }
        result
    }
}
fn open_file(parent: &File, name: &str, mutable: bool) -> io::Result<File> {
    let file = paths::open_relative(
        parent,
        OsStr::new(name),
        FILE_GENERIC_READ,
        FILE_SHARE_READ | if mutable { FILE_SHARE_DELETE } else { 0 },
        FILE_OPEN,
        FILE_NON_DIRECTORY_FILE | FILE_OPEN_REPARSE_POINT,
    )?;
    paths::check_regular_file(&file)?;
    paths::verify_security(&file, Policy::Record)?;
    Ok(file)
}
fn read_bytes(file: &mut File, max: u64) -> io::Result<Vec<u8>> {
    file.rewind()?;
    let size = file.metadata()?.len();
    if size == 0 || size > max {
        return Err(deny());
    }
    let mut bytes = Vec::with_capacity(size as usize);
    file.take(size + 1).read_to_end(&mut bytes)?;
    if bytes.len() as u64 != size || file.metadata()?.len() != size {
        return Err(deny());
    }
    Ok(bytes)
}
fn retain_bytes(parent: &File, name: &str, max: u64, held: &mut Vec<File>) -> io::Result<Vec<u8>> {
    let mut file = open_file(parent, name, false)?;
    let bytes = read_bytes(&mut file, max)?;
    held.push(file);
    Ok(bytes)
}
fn app_names(app_path: &std::path::Path) -> io::Result<()> {
    let mut names = std::fs::read_dir(app_path)?
        .map(|e| e.map(|e| e.file_name()))
        .collect::<io::Result<Vec<_>>>()?;
    names.sort();
    if names
        != ["OpenBlade.Service.exe", "OpenBlade.SessionAgent.exe"].map(std::ffi::OsString::from)
    {
        return Err(deny());
    }
    Ok(())
}
fn frontier_matches(root: &File, path: &std::path::Path, selection: &[u8]) -> io::Result<()> {
    let mut names = Vec::new();
    for entry in std::fs::read_dir(path)? {
        let name = entry?.file_name();
        let name = name.to_str().ok_or_else(deny)?;
        if name.starts_with("selection-") {
            if names.len() >= 4096 {
                return Err(deny());
            }
            names.push(name.to_owned());
        }
    }
    names.sort();
    let name = names.last().ok_or_else(deny)?;
    let mut held = open_file(root, name, false)?;
    let bytes = read_bytes(&mut held, 16384)?;
    let record = Epoch::parse(&bytes)?;
    let rank = match record.phase.as_str() {
        "Provisional" => 0,
        "Committed" => 1,
        "Unresolved" => 2,
        "Retired" => 3,
        _ => return Err(deny()),
    };
    if name != &format!("selection-{:020}-{rank}.bin", record.ordinal) || bytes != selection {
        return Err(deny());
    }
    Ok(())
}
fn pin(path: &std::path::Path) -> io::Result<(File, Vec<File>)> {
    let (root, ancestors, _) = paths::pin_root(path)?;
    paths::require_ntfs(&root)?;
    for (i, d) in ancestors.iter().enumerate() {
        paths::verify_security(
            d,
            if i + 1 == ancestors.len() {
                Policy::PROGRAM_DATA
            } else {
                Policy::ANCESTOR
            },
        )?;
    }
    paths::verify_descriptor(&root, paths::accepts_public_native_root)?;
    Ok((root, ancestors))
}

pub(crate) fn read_installed_package() -> io::Result<InstalledPackage> {
    read_package(false)
}
pub(crate) fn read_startup_package() -> io::Result<InstalledPackage> {
    read_package(true)
}
fn read_package(allow_provisional: bool) -> io::Result<InstalledPackage> {
    let data = crate::service_install_identity::known_folder(&FOLDERID_ProgramData)?;
    let root_path = data.join("OpenBlade.NativeEpochs");
    let (root, ancestors) = pin(&root_path)?;
    let mut root_record = open_file(&root, "root.bin", false)?;
    if read_bytes(&mut root_record, 256)? != b"OpenBlade.NativeRoot.v1\nlegacy-store-absent\n" {
        return Err(deny());
    }
    let mut selected = open_file(&root, "current.bin", true)?;
    let selection = read_bytes(&mut selected, 16384)?;
    let epoch = Epoch::parse(&selection)?;
    // Provisional proof is held only by the live Setup transaction; it is never
    // reconstructed from disk, a PID, a lock collision or reboot state.
    if epoch.phase != "Committed" && !(allow_provisional && epoch.phase == "Provisional") {
        return Err(deny());
    }
    frontier_matches(&root, &root_path, &selection)?;
    let generation = paths::open_directory(&root, OsStr::new(&epoch.id))?;
    paths::verify_security(&generation, Policy::Directory { extra_rights: 0 })?;
    let mut held = vec![selected];
    let phase = retain_bytes(
        &generation,
        &format!("{}.bin", epoch.phase),
        16384,
        &mut held,
    )?;
    if phase != selection {
        return Err(deny());
    }
    let origin = Epoch::parse(&retain_bytes(&generation, "origin.bin", 16384, &mut held)?)?;
    if origin.phase != "Provisional" || !epoch.same_origin(&origin) {
        return Err(deny());
    }
    let startup_owner = if epoch.phase == "Provisional" {
        let bytes = retain_bytes(&generation, "startup-owner.bin", 4096, &mut held)?;
        if digest(&bytes) != epoch.owner {
            return Err(deny());
        }
        let f = lines(&bytes, "OpenBlade.NativeStartupOwner.v1", 6)?;
        if f[0] != epoch.id || f[1] != epoch.request {
            return Err(deny());
        }
        Some(
            crate::retained_process::RetainedProcessInstance::capture_expected(
                f[2].parse().map_err(|_| deny())?,
                f[3].parse().map_err(|_| deny())?,
                std::path::Path::new(f[5]),
                f[4].parse().map_err(|_| deny())?,
            )?,
        )
    } else {
        None
    };
    let package = retain_bytes(&generation, "package.bin", 16384, &mut held)?;
    if digest(&package) != epoch.package {
        return Err(deny());
    }
    let f = lines(&package, "OpenBlade.NativePackage.v2", 12)?;
    let manifest = retain_bytes(&generation, "release.json", 4096, &mut held)?;
    let signature = retain_bytes(&generation, "release.sig", 64, &mut held)?;
    let projection = retain_bytes(&generation, "verification.bin", 2048, &mut held)?;
    let release = authenticate_bound_projection(&projection, f[11], &manifest, &signature)?;
    if f[0] != "LocalDev"
        || f[1] != epoch.installation
        || f[1] != release.installation_id.replace('-', "")
        || f[2] != release.installer_sha256
        || f[3] != release.installer_size.to_string()
        || f[4] != digest(&manifest)
        || f[5] != digest(&signature)
        || f[6] != release.signing_key_id
        || f[10] != "two-executables-static-crt-system-imports-v2"
    {
        return Err(deny());
    }
    let inventory = retain_bytes(&generation, "roles.bin", 4096, &mut held)?;
    if digest(&inventory) != f[7]
        || inventory.len() != ROLES.len() + 28 + 64
        || !inventory.starts_with(ROLES)
        || hex(&inventory[ROLES.len()..ROLES.len() + 20]) != release.source_revision
    {
        return Err(deny());
    }
    let version: Vec<_> = release
        .version
        .split('.')
        .map(|s| s.parse::<u16>().map_err(|_| deny()))
        .collect::<io::Result<_>>()?;
    for (i, v) in version.iter().enumerate() {
        if inventory[ROLES.len() + 20 + i * 2..ROLES.len() + 22 + i * 2] != v.to_le_bytes() {
            return Err(deny());
        }
    }
    let mut original = open_file(&generation, "package.exe", false)?;
    if original.metadata()?.len() != release.installer_size {
        return Err(deny());
    }
    let mut hash = Sha256::new();
    let mut buffer = [0u8; 81920];
    let mut length = 0u64;
    loop {
        let n = original.read(&mut buffer)?;
        if n == 0 {
            break;
        }
        length = length.checked_add(n as u64).ok_or_else(deny)?;
        if length > release.installer_size {
            return Err(deny());
        }
        hash.update(&buffer[..n]);
    }
    if length != release.installer_size || hex(&hash.finalize()) != release.installer_sha256 {
        return Err(deny());
    }
    held.push(original);
    let app = paths::open_directory(&generation, OsStr::new("app"))?;
    paths::verify_security(&app, Policy::Directory { extra_rights: 0 })?;
    // Full app-local closure is deliberately bounded by the authenticated recipe.
    // Protected projection custody is part of the privileged installer TCB. The
    // original signed package remains independently verifiable, not a live HMAC.
    let app_path = root_path.join(&epoch.id).join("app");
    app_names(&app_path)?;
    let service = retain_bytes(&app, "OpenBlade.Service.exe", 64 * 1024 * 1024, &mut held)?;
    let agent = retain_bytes(
        &app,
        "OpenBlade.SessionAgent.exe",
        64 * 1024 * 1024,
        &mut held,
    )?;
    if inspect_artifact(&decode64(f[8])?, &service, "service", &release)? != epoch.service
        || inspect_artifact(&decode64(f[9])?, &agent, "agent", &release)? != epoch.agent
        || inventory[inventory.len() - 64..inventory.len() - 32] != Sha256::digest(&service)[..]
        || inventory[inventory.len() - 32..] != Sha256::digest(&agent)[..]
    {
        return Err(deny());
    }
    held.push(root_record);
    let mut result = InstalledPackage {
        root,
        _ancestors: ancestors,
        directories: vec![generation, app],
        held,
        selection: selection.clone(),
        root_path,
        service_path: app_path.join("OpenBlade.Service.exe"),
        agent_path: app_path.join("OpenBlade.SessionAgent.exe"),
        commitment: Sha256::digest(&selection).into(),
        denied: false,
        service_identity: compiled_record(&service)?.to_vec(),
        agent_identity: compiled_record(&agent)?.to_vec(),
        epoch,
        startup_owner,
    };
    result.require_current()?;
    Ok(result)
}

#[cfg(test)]
#[path = "native_package/tests.rs"]
mod tests;

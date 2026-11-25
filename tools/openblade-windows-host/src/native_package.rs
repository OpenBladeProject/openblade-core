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
#[path = "native_package/identity_bound.rs"]
mod identity_bound;
use identity_bound::MAX_IDENTITY_BYTES;
/// Largest base64 artifact record a package carries: up to 8193 bytes of
/// record, which the managed encoder also refuses to exceed. An identity at
/// `MAX_IDENTITY_BYTES` plus its size, digest and imports fits. The package's
/// 16384-byte record bound limits the pair. The managed
/// `NativeArtifact.MaximumRecordBase64` states the same value.
const MAX_ARTIFACT_RECORD_BASE64: usize = 10924;

fn deny() -> io::Error {
    paths::denied("native package/epoch evidence is unavailable, changed, or unadmitted")
}
fn hex(bytes: &[u8]) -> String {
    bytes.iter().map(|b| format!("{b:02x}")).collect()
}
pub(crate) fn digest(bytes: &[u8]) -> String {
    hex(&Sha256::digest(bytes))
}
fn is_hex(value: &str, length: usize) -> bool {
    value.len() == length
        && value
            .bytes()
            .all(|b| b.is_ascii_digit() || (b'a'..=b'f').contains(&b))
}
pub(crate) fn lines<'a>(bytes: &'a [u8], header: &str, count: usize) -> io::Result<Vec<&'a str>> {
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
pub(crate) fn encode64(bytes: &[u8]) -> String {
    const A: &[u8] = b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    let mut out = String::new();
    for group in bytes.chunks(3) {
        let n = (group[0] as u32) << 16
            | (group.get(1).copied().unwrap_or(0) as u32) << 8
            | group.get(2).copied().unwrap_or(0) as u32;
        out.push(A[(n >> 18) as usize] as char);
        out.push(A[((n >> 12) & 63) as usize] as char);
        out.push(if group.len() > 1 {
            A[((n >> 6) & 63) as usize] as char
        } else {
            '='
        });
        out.push(if group.len() > 2 {
            A[(n & 63) as usize] as char
        } else {
            '='
        });
    }
    out
}
pub(crate) fn create_attempt_record(parent: &File, name: &str, bytes: &[u8]) -> io::Result<()> {
    use std::io::Write;
    // CREATE-only: an incomplete attempt survives; never replay or overwrite it.
    let mut file = paths::open_relative(
        parent,
        OsStr::new(name),
        windows_sys::Win32::Storage::FileSystem::FILE_GENERIC_READ
            | windows_sys::Win32::Storage::FileSystem::FILE_GENERIC_WRITE,
        0,
        windows_sys::Wdk::Storage::FileSystem::FILE_CREATE,
        FILE_NON_DIRECTORY_FILE | FILE_OPEN_REPARSE_POINT,
    )?;
    paths::check_regular_file(&file)?;
    paths::verify_security(&file, Policy::Record)?;
    file.write_all(bytes)?;
    file.sync_all()?;
    if read_bytes(&mut file, 16384)? != bytes {
        return Err(deny());
    }
    Ok(())
}

// One custody record plus 4096 launch/drain pairs is the shared C#/Rust limit.
// Admission reserves the final drain slot; drain publication never checks it.
const MAXIMUM_ATTEMPTS: usize = 4096;
const MAXIMUM_ATTEMPT_ENTRIES: usize = 1 + 2 * MAXIMUM_ATTEMPTS;
#[derive(Debug, PartialEq)]
enum AttemptRefusal {
    BudgetExhausted,
    HistoryOverLimit,
    MalformedHistory,
}
impl std::fmt::Display for AttemptRefusal {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "native attempt history: {self:?}")
    }
}
impl std::error::Error for AttemptRefusal {}
fn attempt_refusal(reason: AttemptRefusal) -> io::Error {
    io::Error::new(io::ErrorKind::InvalidData, reason)
}
fn require_launch_budget(
    names: Vec<String>,
    mut read: impl FnMut(&str) -> io::Result<Vec<u8>>,
    epoch: &Epoch,
    origin: &[u8],
    image: &std::path::Path,
    read_only_mailbox: bool,
) -> io::Result<()> {
    use std::collections::BTreeMap;
    if names.len() > MAXIMUM_ATTEMPT_ENTRIES {
        return Err(attempt_refusal(AttemptRefusal::HistoryOverLimit));
    }
    let mut launches = BTreeMap::new();
    let mut drains = BTreeMap::new();
    let mut custody = false;
    let malformed = || attempt_refusal(AttemptRefusal::MalformedHistory);
    for name in names {
        if name == "custody.lock" {
            if custody {
                return Err(malformed());
            }
            custody = true;
            continue;
        }
        let (target, key) = if let Some(key) = name.strip_prefix("launch-") {
            (&mut launches, key.to_owned())
        } else if let Some(key) = name.strip_prefix("drain-") {
            (&mut drains, key.to_owned())
        } else {
            return Err(malformed());
        };
        if !key.ends_with(".bin") || target.insert(key, read(&name)?).is_some() {
            return Err(malformed());
        }
    }
    if !custody {
        return Err(malformed());
    }
    if launches.len() > MAXIMUM_ATTEMPTS || drains.len() > MAXIMUM_ATTEMPTS {
        return Err(attempt_refusal(AttemptRefusal::HistoryOverLimit));
    }
    for (name, bytes) in &launches {
        let f = lines(bytes, "OpenBlade.NativeServiceLaunch.v1", 10).map_err(|_| malformed())?;
        let owner = StartupOwner::parse(&encode(
            "OpenBlade.NativeStartupOwner.v2",
            &[f[0], f[2], f[5], f[6], f[7], f[9]],
        ))
        .map_err(|_| malformed())?;
        if f[0] != epoch.id
            || f[1] != digest(origin)
            || f[2] != epoch.request
            || f[3] != epoch.package
            || f[4] != epoch.service
            || f[7] != "0"
            || f[8] != "LocalSystem-primary-session0"
            || owner.image != image
            || name != &format!("{}-{}.bin", owner.pid, owner.created)
        {
            return Err(malformed());
        }
        if let Some(drain) = drains.get(name)
            && *drain
                != encode(
                    if read_only_mailbox {
                        "OpenBlade.NativeTransportDrain.v1"
                    } else {
                        "OpenBlade.NativeStatusDrain.v2"
                    },
                    &[&epoch.id, &digest(bytes), f[5], f[6]],
                )
        {
            return Err(malformed());
        }
    }
    if drains.keys().any(|key| !launches.contains_key(key)) {
        return Err(malformed());
    }
    // An unmatched older launch is retained. This is not a drain certificate;
    // recovery still refuses it, including after a later original's clean stop.
    if launches.len() == MAXIMUM_ATTEMPTS {
        return Err(attempt_refusal(AttemptRefusal::BudgetExhausted));
    }
    Ok(())
}

fn publish_metadata_drain(
    epoch: &str,
    launch: &str,
    pid: u32,
    created: u64,
    publish: impl FnOnce(&str, &[u8]) -> io::Result<()>,
) -> io::Result<()> {
    let bytes = encode(
        "OpenBlade.NativeStatusDrain.v2",
        &[epoch, launch, &pid.to_string(), &created.to_string()],
    );
    publish(&format!("drain-{pid}-{created}.bin"), &bytes)
}

pub(crate) fn encode(header: &str, fields: &[&str]) -> Vec<u8> {
    format!("{header}\n{}\n", fields.join("\n")).into_bytes()
}

struct StartupOwner {
    epoch: String,
    request: String,
    pid: u32,
    created: u64,
    session: u32,
    image: PathBuf,
}
impl StartupOwner {
    fn parse(bytes: &[u8]) -> io::Result<Self> {
        use std::os::windows::ffi::OsStringExt;
        // v1's final field remains literal ASCII; never reinterpret it as base64.
        let legacy = bytes.starts_with(b"OpenBlade.NativeStartupOwner.v1\n");
        let f = lines(
            bytes,
            if legacy {
                "OpenBlade.NativeStartupOwner.v1"
            } else {
                "OpenBlade.NativeStartupOwner.v2"
            },
            6,
        )?;
        let units: Vec<u16> = if legacy {
            f[5].encode_utf16().collect()
        } else {
            let raw = decode64_bounded(f[5], 10924)?;
            if !raw.len().is_multiple_of(2) {
                return Err(deny());
            }
            raw.as_chunks::<2>()
                .0
                .iter()
                .map(|v| u16::from_le_bytes(*v))
                .collect()
        };
        if units.is_empty() || units.len() > 4096 || units.contains(&0) {
            return Err(deny());
        }
        let pid: u32 = f[2].parse().map_err(|_| deny())?;
        let created: u64 = f[3].parse().map_err(|_| deny())?;
        let session: u32 = f[4].parse().map_err(|_| deny())?;
        if !is_hex(f[0], 32)
            || !is_hex(f[1], 32)
            || pid == 0
            || pid.to_string() != f[2]
            || created == 0
            || created > i64::MAX as u64
            || created.to_string() != f[3]
            || session.to_string() != f[4]
        {
            return Err(deny());
        }
        Ok(Self {
            epoch: f[0].into(),
            request: f[1].into(),
            pid,
            created,
            session,
            image: std::ffi::OsString::from_wide(&units).into(),
        })
    }
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
    decode64_bounded(value, 4096)
}
/// A package record's fields, with the Service and Agent artifact records it
/// carries decoded. Both production readers parse through this after checking
/// the bytes against the epoch's package digest.
struct PackageRecord<'a> {
    fields: Vec<&'a str>,
    service: Vec<u8>,
    agent: Vec<u8>,
}
fn package_record(bytes: &[u8]) -> io::Result<PackageRecord<'_>> {
    let fields = lines(bytes, "OpenBlade.NativePackage.v2", 12)?;
    Ok(PackageRecord {
        service: decode64_bounded(fields[8], MAX_ARTIFACT_RECORD_BASE64)?,
        agent: decode64_bounded(fields[9], MAX_ARTIFACT_RECORD_BASE64)?,
        fields,
    })
}
fn decode64_bounded(value: &str, maximum: usize) -> io::Result<Vec<u8>> {
    if value.is_empty() || !value.len().is_multiple_of(4) || value.len() > maximum {
        return Err(deny());
    }
    let alphabet = b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    let mut out = Vec::new();
    for (i, group) in value.as_bytes().as_chunks::<4>().0.iter().enumerate() {
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
            if size == 0 || size > MAX_IDENTITY_BYTES {
                return Err(deny());
            }
            result = image.get(raw..raw.checked_add(size).ok_or_else(deny)?);
        }
    }
    result.ok_or_else(deny)
}

// This contract is authenticated transitively by signed package -> exact PE ->
// .obid. Neither an incoming image nor a policy label grants old-epoch rights.
fn capabilities(schema: u8, role: &str, features: &str, value: &str) -> io::Result<()> {
    let features: Vec<_> = features.split(',').collect();
    let has = |name| features.contains(&name);
    let metadata = has("service-metadata-only-v1");
    let hardware = has("service-hardware-compile-check");
    let firmware = has("firmware-performance-compile-check");
    let observation = has("service-mailbox-observation-v1");
    let mutation = has("service-thermal-mutation-v1");
    let fan = has("service-fan-mutation-v2");
    let battery = has("service-battery-policy-v1");
    let temporary = has("service-temporary-charge-v1");
    let agent_battery = has("agent-battery-report-v1");
    let agent_display = has("agent-display-query-v1");
    let agent_write = has("agent-display-brightness-write-v1");
    let agent_hdr = has("agent-display-hdr-write-v1");
    let agent_refresh = has("agent-display-refresh-write-v1");
    let agent_color = has("agent-display-color-write-v1");
    let agent_profile = has("agent-display-profile-apply-v1");
    let agent_gpu = has("agent-gpu-v1");
    // Keyboard Driver-mode ownership writes 00/04, which no admitted
    // composition sends, and its low-power release and D1 fan handback are a
    // later slice. Refuse its feature and its capability marker by name,
    // before the read and trigger features built with it, until a stage admits
    // it (docs/windows-rust-development-service.md).
    const DRIVER_OWNER: &str = "service-keyboard-driver-mode-v1";
    if has(DRIVER_OWNER)
        || value
            .split(',')
            .any(|capability| capability == DRIVER_OWNER)
    {
        return Err(deny());
    }
    // GPU clock tuning writes NVIDIA driver state through an NVML module
    // loaded from System32. Refuse its feature and its capability marker by
    // name, before the triggers it extends, until a WinVerifyTrust and
    // catalog gate checks the loaded module path and a packaging stage admits
    // it (docs/windows-native-thermal-mutation.md).
    const GPU_CLOCK_TUNING: &str = "service-gpu-clock-tuning-v1";
    if has(GPU_CLOCK_TUNING)
        || value
            .split(',')
            .any(|capability| capability == GPU_CLOCK_TUNING)
    {
        return Err(deny());
    }
    // Maintain-desired-state convergence writes without a request. Refuse its
    // feature and its capability marker by name, before the triggers it
    // extends, until a packaging stage admits it.
    const THERMAL_CONVERGENCE: &str = "service-thermal-convergence-v1";
    if has(THERMAL_CONVERGENCE)
        || value
            .split(',')
            .any(|capability| capability == THERMAL_CONVERGENCE)
    {
        return Err(deny());
    }
    // Automatic reconciliation of saved intent is not admitted for packaging:
    // refuse its feature and its capability marker by name, whatever else the
    // record claims, until a packaging stage admits it (with the low-power
    // gate and transaction-pool reclamation it still lacks).
    const PROFILE_TRIGGERS: &str = "service-profile-triggers-v1";
    if has(PROFILE_TRIGGERS)
        || value
            .split(',')
            .any(|capability| capability == PROFILE_TRIGGERS)
    {
        return Err(deny());
    }
    // Fan Stop is served with its guardian unavailable: it stores settings
    // and refuses Stop. No packaging stage has admitted it, so refuse its
    // feature and its capability marker by name, before the profiles it
    // extends (docs/windows-rust-development-service.md).
    const FAN_STOP: &str = "service-fan-stop-v1";
    if has(FAN_STOP) || value.split(',').any(|capability| capability == FAN_STOP) {
        return Err(deny());
    }
    // No packaging stage has admitted a Service that serves updateProfiles.
    // Refuse the feature and the capability by name, whatever else the
    // record claims, rather than rely on the expected string below.
    const PROFILES: &str = "service-profiles-v1";
    if has(PROFILES) || value.split(',').any(|capability| capability == PROFILES) {
        return Err(deny());
    }
    // The read-only keyboard Driver mode sends a command no admitted
    // composition sends. The transaction pool that blocked it is reclaimed
    // now, but admitting it is a separate packaging decision: refuse its
    // feature and its capability marker by name until a stage admits it
    // (docs/windows-rust-development-service.md).
    const DRIVER_MODE: &str = "service-keyboard-driver-mode-read-v1";
    if has(DRIVER_MODE) || value.split(',').any(|capability| capability == DRIVER_MODE) {
        return Err(deny());
    }
    // The live-reading read sends the 0D/88 fan speed queries, which no
    // admitted composition sends, and adds Windows battery reads. Refuse its
    // feature and marker by name until a stage admits it
    // (docs/windows-rust-development-service.md).
    const LIVE_READINGS: &str = "service-live-readings-v1";
    if has(LIVE_READINGS)
        || value
            .split(',')
            .any(|capability| capability == LIVE_READINGS)
    {
        return Err(deny());
    }
    // The diagnostic export answers with an archive of the caller-projected
    // status, no admitted composition serves it, and admitting it is a
    // separate packaging decision. Refuse its feature and marker by name
    // (docs/windows-rust-development-service.md).
    const DIAGNOSTICS_EXPORT: &str = "service-diagnostics-export-v1";
    if has(DIAGNOSTICS_EXPORT)
        || value
            .split(',')
            .any(|capability| capability == DIAGNOSTICS_EXPORT)
    {
        return Err(deny());
    }
    // The performance shortcut writes a thermal preset no packaged request
    // names, and its preview publishes status without the operation gate.
    // Admitting them is a separate packaging decision. Refuse the feature
    // and its marker by name (docs/windows-rust-development-service.md).
    const PERFORMANCE_SHORTCUT: &str = "service-performance-shortcut-v1";
    if has(PERFORMANCE_SHORTCUT)
        || value
            .split(',')
            .any(|capability| capability == PERFORMANCE_SHORTCUT)
    {
        return Err(deny());
    }
    // The HyperBoost exit to a battery preset writes managed's restore pair
    // in HyperBoost, which has no capture evidence on battery. Admitting it
    // is a separate packaging decision. Refuse the feature and its marker by
    // name (docs/windows-rust-development-service.md).
    const HYPERBOOST_EXIT: &str = "service-thermal-hyperboost-exit-v1";
    if has(HYPERBOOST_EXIT)
        || value
            .split(',')
            .any(|capability| capability == HYPERBOOST_EXIT)
    {
        return Err(deny());
    }
    // The application-context reporter is not admitted for packaging either:
    // refuse its feature and its capability marker by name, whatever else the
    // record claims, until a packaging stage admits both.
    // That stage must also pair the reporter with a Service built with
    // service-thermal-mutation-v1, which alone serves reportApplicationContext;
    // see docs/windows-native-agent-application-reporter.md.
    const REPORTER: &str = "agent-application-reporter-v1";
    const REPORTER_MARKER: &str = "session-application-reporter-v1";
    if has(REPORTER)
        || value
            .split(',')
            .any(|capability| capability == REPORTER || capability == REPORTER_MARKER)
    {
        return Err(deny());
    }
    // The function-row hook is not admitted for packaging: refuse its feature
    // and its capability marker by name until a stage admits the role; see
    // docs/windows-native-agent-function-row.md.
    const FUNCTION_ROW: &str = "agent-function-row-v1";
    const FUNCTION_ROW_MARKER: &str = "session-function-row-v1";
    if has(FUNCTION_ROW)
        || value
            .split(',')
            .any(|capability| capability == FUNCTION_ROW || capability == FUNCTION_ROW_MARKER)
    {
        return Err(deny());
    }
    // Display automation writes without a request, so it is not admitted for
    // packaging either: refuse its feature and its capability marker by name
    // until a stage admits it; see docs/windows-native-agent-display-automation.md.
    const AUTOMATION: &str = "agent-display-automation-v1";
    const AUTOMATION_MARKER: &str = "session-display-automation-v1";
    if has(AUTOMATION)
        || value
            .split(',')
            .any(|capability| capability == AUTOMATION || capability == AUTOMATION_MARKER)
    {
        return Err(deny());
    }
    if (agent_write || agent_hdr || agent_refresh || agent_color) && !agent_display {
        return Err(deny());
    }
    // The profile composes every reviewed display write.
    if agent_profile && !(agent_write && agent_hdr && agent_refresh && agent_color) {
        return Err(deny());
    }
    if (agent_battery
        || agent_display
        || agent_write
        || agent_hdr
        || agent_refresh
        || agent_color
        || agent_profile
        || agent_gpu)
        && !has("native-agent-entry-compile-check")
    {
        return Err(deny());
    }
    if temporary && !battery {
        return Err(deny());
    }
    if battery && !fan {
        return Err(deny());
    }
    if fan && !mutation {
        return Err(deny());
    }
    if mutation && (!observation || !has("controller-admission-compile-check")) {
        return Err(deny());
    }
    if observation && (!hardware || metadata) {
        return Err(deny());
    }
    if schema == 1 {
        return if metadata
            || hardware
            || agent_battery
            || agent_display
            || agent_write
            || agent_hdr
            || agent_refresh
            || agent_color
            || agent_profile
            || agent_gpu
        {
            Err(deny())
        } else {
            Ok(())
        };
    }
    if metadata
        && (hardware
            || firmware
            || has("hid-identity-compile-check")
            || features
                .iter()
                .any(|f| f.contains("fixture") || f.starts_with("scratch-")))
    {
        return Err(deny());
    }
    if !matches!(role, "service" | "agent")
        || hardware != firmware
        || ((metadata || hardware) && !has("installed-service-candidate"))
    {
        return Err(deny());
    }
    // Each selected Agent contract in fixed order; the write features already
    // require the display query above, and the profile every write.
    let agent = [
        (agent_battery, "agent-battery-report-v1"),
        (agent_display, "session-display-query-partial-v1"),
        (agent_write, "session-display-brightness-write-partial-v1"),
        (agent_hdr, "session-display-hdr-write-partial-v1"),
        (agent_refresh, "session-display-refresh-write-partial-v1"),
        (agent_color, "session-display-color-write-partial-v1"),
        (agent_profile, "session-display-profile-apply-partial-v1"),
        (agent_gpu, "session-gpu-apps-v1"),
    ]
    .into_iter()
    .filter_map(|(selected, marker)| selected.then_some(marker))
    .collect::<Vec<_>>()
    .join(",");
    let expected = if role == "service" {
        if metadata == hardware || !has("startup-custody-v1") || hardware != firmware {
            return Err(deny());
        }
        if metadata {
            "private-service-custody-v1,service-metadata-only-v1"
        } else if temporary {
            "private-service-custody-v1,service-mailbox-observation-v1,service-thermal-mutation-v1,service-fan-mutation-v2,service-battery-policy-v1,service-temporary-charge-v1"
        } else if battery {
            "private-service-custody-v1,service-mailbox-observation-v1,service-thermal-mutation-v1,service-fan-mutation-v2,service-battery-policy-v1"
        } else if fan {
            "private-service-custody-v1,service-mailbox-observation-v1,service-thermal-mutation-v1,service-fan-mutation-v2"
        } else if mutation {
            "private-service-custody-v1,service-mailbox-observation-v1,service-thermal-mutation-v1"
        } else if observation {
            "private-service-custody-v1,service-mailbox-observation-v1"
        } else {
            "private-service-custody-v1"
        }
    } else if agent.is_empty() {
        "none"
    } else {
        &agent
    };
    if value != expected {
        return Err(deny());
    }
    Ok(())
}

// Called after SAME custody acquisition and fresh current/frontier admission,
// before launch publication. A matching stale marker never revives a retired epoch.
fn require_submission(epoch: &Epoch, origin: &[u8], submission: &[u8]) -> io::Result<()> {
    if !matches!(epoch.phase.as_str(), "Provisional" | "Committed")
        || submission
            != encode(
                "OpenBlade.NativeStartupSubmission.v1",
                &[
                    &epoch.id,
                    &epoch.request,
                    &digest(origin),
                    &epoch.package,
                    &epoch.service,
                ],
            )
    {
        return Err(deny());
    }
    Ok(())
}

/// The startup reader's policy, for the compiled-identity tests of other
/// modules.
#[cfg(all(
    test,
    any(
        feature = "service-keyboard-driver-mode-read-v1",
        feature = "service-live-readings-v1",
        feature = "service-diagnostics-export-v1"
    )
))]
pub(crate) fn startup_policy_for_tests(identity: &[u8]) -> io::Result<&'static str> {
    startup_policy(identity)
}

fn startup_policy(identity: &[u8]) -> io::Result<&'static str> {
    if identity.starts_with(b"OpenBlade.NativeArtifact.v2\n") {
        let f = lines(identity, "OpenBlade.NativeArtifact.v2", 8)?;
        capabilities(2, f[1], f[4], f[7])?;
        Ok(
            if f[7] == "private-service-custody-v1,service-metadata-only-v1" {
                "service-metadata-only-v1"
            } else if f[7]
                == "private-service-custody-v1,service-mailbox-observation-v1,service-thermal-mutation-v1,service-fan-mutation-v2,service-battery-policy-v1,service-temporary-charge-v1"
            {
                "service-temporary-charge-v1"
            } else if f[7]
                == "private-service-custody-v1,service-mailbox-observation-v1,service-thermal-mutation-v1,service-fan-mutation-v2,service-battery-policy-v1"
            {
                "service-battery-policy-v1"
            } else if f[7]
                == "private-service-custody-v1,service-mailbox-observation-v1,service-thermal-mutation-v1,service-fan-mutation-v2"
            {
                "service-fan-mutation-v2"
            } else if f[7]
                == "private-service-custody-v1,service-mailbox-observation-v1,service-thermal-mutation-v1"
            {
                "service-thermal-mutation-v1"
            } else if f[7] == "private-service-custody-v1,service-mailbox-observation-v1" {
                "service-mailbox-observation-v1"
            } else {
                "service-hardware-compile-check"
            },
        )
    } else {
        lines(identity, "OpenBlade.NativeArtifact.v1", 7)?;
        Ok("metadata-only-no-firmware")
    }
}

fn inspect_artifact(
    record: &[u8],
    image: &[u8],
    role: &str,
    release: &Release,
) -> io::Result<String> {
    let schema = if record.starts_with(b"OpenBlade.NativeArtifactRecord.v2\n") {
        2
    } else {
        1
    };
    let f = lines(
        record,
        if schema == 2 {
            "OpenBlade.NativeArtifactRecord.v2"
        } else {
            "OpenBlade.NativeArtifactRecord.v1"
        },
        if schema == 2 { 11 } else { 10 },
    )?;
    capabilities(schema, role, f[4], if schema == 2 { f[10] } else { "none" })?;
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
            if marker.is_some() || size == 0 || size > MAX_IDENTITY_BYTES || raw_size < size {
                return Err(deny());
            }
            marker = Some(image.get(raw..raw + size).ok_or_else(deny)?);
        }
    }
    let marker_fields: Vec<_> = f[..7]
        .iter()
        .copied()
        .chain((schema == 2).then_some(f.get(10).copied().unwrap_or("none")))
        .collect();
    if marker
        != Some(
            encode(
                if schema == 2 {
                    "OpenBlade.NativeArtifact.v2"
                } else {
                    "OpenBlade.NativeArtifact.v1"
                },
                &marker_fields,
            )
            .as_slice(),
        )
    {
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
    for entry in table.as_chunks::<20>().0 {
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

/// The reviewed inbox Windows DLLs a native executable may import by name,
/// besides the `api-ms-win-` API sets. The managed
/// `WindowsNativePackage.IsSystemImport` admits the same names.
const SYSTEM_IMPORTS: &[&str] = &[
    "combase.dll",
    "bcryptprimitives.dll",
    "kernel32.dll",
    "ntdll.dll",
    "advapi32.dll",
    "bcrypt.dll",
    "crypt32.dll",
    "user32.dll",
    "userenv.dll",
    "shell32.dll",
    "ole32.dll",
    "oleaut32.dll",
    "ws2_32.dll",
    "iphlpapi.dll",
    "setupapi.dll",
    "cfgmgr32.dll",
    "powrprof.dll",
    "wtsapi32.dll",
    "secur32.dll",
    "version.dll",
    "wintrust.dll",
    "psapi.dll",
    "gdi32.dll",
    "dwmapi.dll",
    "comctl32.dll",
    "comdlg32.dll",
    "shlwapi.dll",
    "hid.dll",
    "winmm.dll",
    "dbghelp.dll",
    "msvcrt.dll",
    // Inbox Windows Color System: the Agent display query reads the
    // active profile and setColorProfile writes it.
    "mscms.dll",
    // Inbox Windows Web Services XML reader over the Agent battery
    // report's bounded in-memory buffer; no channel or network API.
    "webservices.dll",
];

fn system_import(name: &str) -> bool {
    SYSTEM_IMPORTS.contains(&name)
        || name.starts_with("api-ms-win-")
            && name.ends_with(".dll")
            && name
                .bytes()
                .all(|b| b.is_ascii_alphanumeric() || b == b'-' || b == b'.')
}

const PROMOTION_BUDGET: std::time::Duration = std::time::Duration::from_secs(4);
#[derive(Default)]
struct Promotion {
    pending: Option<(std::time::Instant, Vec<u8>)>,
}
impl Promotion {
    fn check(
        &mut self,
        epoch: &Epoch,
        selected: &[u8],
        current: &[u8],
        frontier: &[u8],
        phase: Option<&[u8]>,
        now: std::time::Instant,
    ) -> io::Result<Option<Epoch>> {
        if selected == current && selected == frontier && self.pending.is_none() {
            return Ok(None);
        }
        let next = Epoch::parse(frontier)?;
        if epoch.phase != "Provisional"
            || next.phase != "Committed"
            || !epoch.same_origin(&next)
            || phase != Some(frontier)
            || !(current == selected || current == frontier)
        {
            return Err(deny());
        }
        if let Some((deadline, expected)) = &self.pending
            && (now >= *deadline || expected != frontier)
        {
            return Err(deny());
        }
        if current == frontier {
            self.pending = None;
            return Ok(Some(next));
        }
        if self.pending.is_none() {
            self.pending = Some((now + PROMOTION_BUDGET, frontier.to_vec()));
        }
        Ok(None)
    }
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
    promotion: Promotion,
    epoch: Epoch,
    startup_owner: Option<crate::retained_process::RetainedProcessInstance>,
    service_identity: Vec<u8>,
    agent_identity: Vec<u8>,
    committed_phase_retained: bool,
    service_custody: Option<File>,
    service_attempt: Option<(File, String)>,
}
impl InstalledPackage {
    // This fixed metadata-only entry contract is committed by the signed service
    // feature identity. Custody lasts until Origin/process teardown, including
    // drain publication after revocation. It is never firmware Authority.
    pub(crate) fn begin_service_attempt(
        &mut self,
        pid: u32,
        created: u64,
        session: u32,
        image: &std::path::Path,
    ) -> io::Result<()> {
        use std::os::windows::ffi::OsStrExt;
        if self.service_custody.is_some()
            || pid == 0
            || created == 0
            || session != 0
            || image != self.service_path
        {
            return Err(deny());
        }
        let origin = retain_bytes(&self.directories[0], "origin.bin", 16384, &mut self.held)?;
        let policy = retain_bytes(
            &self.directories[0],
            "startup-policy.bin",
            2048,
            &mut self.held,
        )?;
        if policy
            != encode(
                "OpenBlade.NativeStartupPolicy.v1",
                &[
                    &self.epoch.id,
                    &digest(&origin),
                    &self.epoch.service,
                    startup_policy(&self.service_identity)?,
                ],
            )
        {
            return Err(deny());
        }
        let attempts = paths::open_directory(&self.directories[0], OsStr::new("attempts"))?;
        paths::verify_security(&attempts, Policy::Directory { extra_rights: 0 })?;
        let mut custody = paths::open_relative(
            &attempts,
            OsStr::new("custody.lock"),
            windows_sys::Win32::Storage::FileSystem::FILE_GENERIC_READ,
            0,
            FILE_OPEN,
            FILE_NON_DIRECTORY_FILE | FILE_OPEN_REPARSE_POINT,
        )?;
        paths::check_regular_file(&custody)?;
        paths::verify_security(&custody, Policy::PrivateCustody)?;
        if read_bytes(&mut custody, 256)?
            != encode("OpenBlade.NativeServiceCustody.v1", &[&self.epoch.id])
        {
            return Err(deny());
        }
        self.service_custody = Some(custody);
        // Pending starts must revalidate after acquiring custody, so Setup can
        // retire while holding the same lock without an old startup entering.
        self.require_current()?;
        let submission =
            retain_bytes(&self.directories[0], "submission.bin", 2048, &mut self.held)?;
        require_submission(&self.epoch, &origin, &submission)?;
        let path: Vec<u8> = image
            .as_os_str()
            .encode_wide()
            .flat_map(u16::to_le_bytes)
            .collect();
        if path.is_empty() || path.len() > 8192 {
            return Err(deny());
        }
        let bytes = encode(
            "OpenBlade.NativeServiceLaunch.v1",
            &[
                &self.epoch.id,
                &digest(&origin),
                &self.epoch.request,
                &self.epoch.package,
                &self.epoch.service,
                &pid.to_string(),
                &created.to_string(),
                &session.to_string(),
                "LocalSystem-primary-session0",
                &encode64(&path),
            ],
        );
        // The retained directory ancestry prevents path replacement during this
        // bounded enumeration; individual records are opened relative to it.
        let attempt_path = self
            .service_path
            .parent()
            .and_then(std::path::Path::parent)
            .ok_or_else(deny)?
            .join("attempts");
        let names = std::fs::read_dir(attempt_path)?
            .take(MAXIMUM_ATTEMPT_ENTRIES + 1)
            .map(|entry| {
                entry.and_then(|entry| entry.file_name().into_string().map_err(|_| deny()))
            })
            .collect::<io::Result<Vec<_>>>()?;
        require_launch_budget(
            names,
            |name| {
                let mut record = paths::open_relative(
                    &attempts,
                    OsStr::new(name),
                    FILE_GENERIC_READ,
                    FILE_SHARE_READ,
                    FILE_OPEN,
                    FILE_NON_DIRECTORY_FILE | FILE_OPEN_REPARSE_POINT,
                )?;
                paths::check_regular_file(&record)?;
                paths::verify_security(&record, Policy::Record)?;
                read_bytes(&mut record, 16384)
            },
            &self.epoch,
            &origin,
            image,
            matches!(
                startup_policy(&self.service_identity)?,
                "service-mailbox-observation-v1"
                    | "service-thermal-mutation-v1"
                    | "service-fan-mutation-v2"
                    | "service-battery-policy-v1"
                    | "service-temporary-charge-v1"
            ),
        )?;
        create_attempt_record(&attempts, &format!("launch-{pid}-{created}.bin"), &bytes)?;
        self.service_attempt = Some((attempts, digest(&bytes)));
        Ok(())
    }
    pub(crate) fn record_metadata_drain(&self, pid: u32, created: u64) -> io::Result<()> {
        if matches!(
            startup_policy(&self.service_identity)?,
            "service-mailbox-observation-v1"
                | "service-thermal-mutation-v1"
                | "service-fan-mutation-v2"
                | "service-battery-policy-v1"
                | "service-temporary-charge-v1"
        ) {
            return Err(deny());
        }
        let (attempts, launch) = self.service_attempt.as_ref().ok_or_else(deny)?;
        if self.service_custody.is_none() {
            return Err(deny());
        }
        publish_metadata_drain(&self.epoch.id, launch, pid, created, |name, bytes| {
            create_attempt_record(attempts, name, bytes)
        })
    }
    pub(crate) fn record_transport_drain(&self, pid: u32, created: u64) -> io::Result<()> {
        let (attempts, launch) = self.service_attempt.as_ref().ok_or_else(deny)?;
        if self.service_custody.is_none()
            || !matches!(
                startup_policy(&self.service_identity)?,
                "service-mailbox-observation-v1"
                    | "service-thermal-mutation-v1"
                    | "service-fan-mutation-v2"
                    | "service-battery-policy-v1"
                    | "service-temporary-charge-v1"
            )
        {
            return Err(deny());
        }
        create_attempt_record(
            attempts,
            &format!("drain-{pid}-{created}.bin"),
            &encode(
                "OpenBlade.NativeTransportDrain.v1",
                &[
                    &self.epoch.id,
                    launch,
                    &pid.to_string(),
                    &created.to_string(),
                ],
            ),
        )
    }
    pub(crate) fn mailbox_mapping(&self) -> io::Result<Vec<u8>> {
        if !matches!(
            startup_policy(&self.service_identity)?,
            "service-mailbox-observation-v1"
                | "service-thermal-mutation-v1"
                | "service-fan-mutation-v2"
                | "service-battery-policy-v1"
                | "service-temporary-charge-v1"
        ) {
            return Err(deny());
        }
        let expected_prefix = [
            &self.epoch.id,
            &self.epoch.installation,
            &self.epoch.request,
            &self.epoch.package,
        ];
        let mut mapping = crate::windows_protected_path::open_relative(
            &self.directories[0],
            std::ffi::OsStr::new("managed-handoff.bin"),
            FILE_GENERIC_READ,
            FILE_SHARE_READ,
            FILE_OPEN,
            FILE_NON_DIRECTORY_FILE | FILE_OPEN_REPARSE_POINT,
        )?;
        crate::windows_protected_path::check_regular_file(&mapping)?;
        crate::windows_protected_path::verify_security(&mapping, Policy::Record)?;
        let bytes = read_bytes(&mut mapping, 4096)?;
        let f = lines(&bytes, "OpenBlade.ManagedMailboxEpoch.v2", 8)?;
        if f[2..6] != expected_prefix || !is_hex(f[6], 32) || !is_hex(f[7], 64) {
            return Err(deny());
        }
        Ok(bytes)
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
            require_no_retirement(&self.directories[0])?;
            // Setup commits phase -> frontier -> current. Treat only this exact
            // authenticated live-attempt interval as typed pending promotion.
            // Unrelated/unknown publication remains terminal; no startup is replayed.
            let mut current_file = open_file(&self.root, "current.bin", true)?;
            let current = read_bytes(&mut current_file, 16384)?;
            let frontier = read_frontier(&self.root, &self.root_path)?;
            let phase = if current != self.selection || frontier != self.selection {
                let mut phase = open_file(&self.directories[0], "Committed.bin", false)?;
                let bytes = read_bytes(&mut phase, 16384)?;
                if !self.committed_phase_retained {
                    self.held.push(phase);
                    self.committed_phase_retained = true;
                }
                Some(bytes)
            } else {
                None
            };
            if let Some(next) = self.promotion.check(
                &self.epoch,
                &self.selection,
                &current,
                &frontier,
                phase.as_deref(),
                std::time::Instant::now(),
            )? {
                self.epoch = next;
                self.selection = frontier;
                self.commitment = Sha256::digest(&self.selection).into();
                self.startup_owner = None;
            }
            app_names(self.service_path.parent().ok_or_else(deny)?)?;
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
fn require_no_retirement(parent: &File) -> io::Result<()> {
    match open_file(parent, "Retired.bin", false) {
        Err(error) if paths::missing_leaf(&error) => Ok(()),
        _ => Err(deny()),
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
fn read_frontier(root: &File, path: &std::path::Path) -> io::Result<Vec<u8>> {
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
    if name != &format!("selection-{:020}-{rank}.bin", record.ordinal) {
        return Err(deny());
    }
    Ok(bytes)
}
fn frontier_matches(root: &File, path: &std::path::Path, selection: &[u8]) -> io::Result<()> {
    if read_frontier(root, path)? != selection {
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
    require_no_retirement(&generation)?;
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
        let bytes = retain_bytes(&generation, "startup-owner.bin", 16384, &mut held)?;
        if digest(&bytes) != epoch.owner {
            return Err(deny());
        }
        let owner = StartupOwner::parse(&bytes)?;
        if owner.epoch != epoch.id || owner.request != epoch.request {
            return Err(deny());
        }
        Some(
            crate::retained_process::RetainedProcessInstance::capture_expected(
                owner.pid,
                owner.created,
                &owner.image,
                owner.session,
            )?,
        )
    } else {
        None
    };
    let package = retain_bytes(&generation, "package.bin", 16384, &mut held)?;
    if digest(&package) != epoch.package {
        return Err(deny());
    }
    let PackageRecord {
        fields: f,
        service: service_record,
        agent: agent_record,
    } = package_record(&package)?;
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
    if inspect_artifact(&service_record, &service, "service", &release)? != epoch.service
        || inspect_artifact(&agent_record, &agent, "agent", &release)? != epoch.agent
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
        promotion: Promotion::default(),
        service_identity: compiled_record(&service)?.to_vec(),
        agent_identity: compiled_record(&agent)?.to_vec(),
        committed_phase_retained: epoch.phase == "Committed",
        epoch,
        startup_owner,
        service_custody: None,
        service_attempt: None,
    };
    result.require_current()?;
    Ok(result)
}

#[cfg(test)]
#[path = "native_package/tests.rs"]
mod tests;

/// Original generation proof for the new battery/coexistence family. It does
/// not consult current selection or launch a retired image. All authenticated
/// inputs remain pinned against replacement for the lifetime of the owner Store.
#[cfg(feature = "service-thermal-mutation-v1")]
pub(crate) struct HistoricalBatteryProducer {
    installation: String,
    epoch: String,
    artifact: String,
    temporary: bool,
    _held: Vec<File>,
}
#[cfg(feature = "service-thermal-mutation-v1")]
impl HistoricalBatteryProducer {
    pub(crate) fn verify_temporary(&self) -> io::Result<()> {
        if self.temporary { Ok(()) } else { Err(deny()) }
    }
    pub(crate) fn verify(&self, installation: &str, epoch: &str, artifact: &str) -> io::Result<()> {
        if installation != self.installation || epoch != self.epoch || artifact != self.artifact {
            return Err(deny());
        }
        Ok(())
    }
}
#[cfg(feature = "service-thermal-mutation-v1")]
pub(crate) fn historical_battery_producer(
    root_path: &std::path::Path,
    installation: &str,
    epoch: &str,
    artifact: &str,
) -> io::Result<HistoricalBatteryProducer> {
    // The service supplies these fields from its immutable authenticated Active
    // record. Keep this shared package verifier independent of service ownership
    // types; the agent gains no owner-store or mutation authority.
    if !is_hex(installation, 32) || !is_hex(epoch, 32) || !artifact_id(artifact, "service") {
        return Err(deny());
    }
    let (root, mut ancestors) = pin(root_path)?;
    let mut held = Vec::new();
    if retain_bytes(&root, "root.bin", 256, &mut held)?
        != b"OpenBlade.NativeRoot.v1\nlegacy-store-absent\n"
    {
        return Err(deny());
    }
    let generation = paths::open_directory(&root, OsStr::new(epoch))?;
    paths::verify_security(&generation, Policy::Directory { extra_rights: 0 })?;
    let origin = Epoch::parse(&retain_bytes(&generation, "origin.bin", 16384, &mut held)?)?;
    if origin.phase != "Provisional"
        || origin.id != epoch
        || origin.installation != installation
        || origin.service != artifact
    {
        return Err(deny());
    }
    let package = retain_bytes(&generation, "package.bin", 16384, &mut held)?;
    if digest(&package) != origin.package {
        return Err(deny());
    }
    let PackageRecord {
        fields: f,
        service: service_record,
        ..
    } = package_record(&package)?;
    let manifest = retain_bytes(&generation, "release.json", 4096, &mut held)?;
    let signature = retain_bytes(&generation, "release.sig", 64, &mut held)?;
    let projection = retain_bytes(&generation, "verification.bin", 2048, &mut held)?;
    let release = authenticate_bound_projection(&projection, f[11], &manifest, &signature)?;
    if f[0] != "LocalDev"
        || f[1] != origin.installation
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
    let service = retain_bytes(&app, "OpenBlade.Service.exe", 64 * 1024 * 1024, &mut held)?;
    let policy = startup_policy(compiled_record(&service)?)?;
    if inspect_artifact(&service_record, &service, "service", &release)? != artifact
        || !matches!(
            policy,
            "service-battery-policy-v1" | "service-temporary-charge-v1"
        )
    {
        return Err(deny());
    }
    held.push(app);
    held.push(generation);
    held.push(root);
    held.append(&mut ancestors);
    Ok(HistoricalBatteryProducer {
        installation: installation.to_owned(),
        epoch: epoch.to_owned(),
        artifact: artifact.to_owned(),
        temporary: policy == "service-temporary-charge-v1",
        _held: held,
    })
}

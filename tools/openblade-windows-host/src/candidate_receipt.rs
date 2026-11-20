//! Bind package records to retained candidate files, with separate startup and commit admission.

use std::io;
#[cfg(feature = "scratch-candidate-receipt")]
use std::{ffi::OsStr, fs::File, io::Read, os::windows::io::AsRawHandle, path::Path};

#[cfg(feature = "scratch-candidate-receipt")]
use serde::{Deserialize, Serialize};
#[cfg(feature = "scratch-candidate-receipt")]
use sha2::{Digest, Sha256};
#[cfg(feature = "scratch-candidate-receipt")]
use windows_sys::{
    Wdk::Storage::FileSystem::{FILE_NON_DIRECTORY_FILE, FILE_OPEN, FILE_OPEN_REPARSE_POINT},
    Win32::Storage::FileSystem::{FILE_GENERIC_READ, FILE_SHARE_READ, FILE_TYPE_DISK, GetFileType},
};

#[cfg(feature = "scratch-candidate-receipt")]
use crate::{
    service_image::{self, MAX_IMAGE_BYTES, snapshot},
    service_install_identity::IMAGE_NAME,
    windows_protected_path::{
        check_regular_file, denied, directory_info, file_identity, open_relative, pin_root,
        require_ntfs,
    },
};

// The receipt fixtures compile this file standalone; the Service entry also loads it at the crate root.
#[allow(clippy::duplicate_mod)]
#[path = "native_package.rs"]
pub(crate) mod native_package;

#[cfg(feature = "scratch-candidate-receipt")]
const COMMITTED_NAME: &str = "candidate-package.committed.json";
#[cfg(feature = "scratch-candidate-receipt")]
const MAX_RECEIPT_BYTES: u64 = 4096;
#[cfg(feature = "scratch-candidate-receipt")]
const EMPTY_GUID: &str = "00000000-0000-0000-0000-000000000000";
#[cfg(feature = "scratch-candidate-receipt")]
const AGENT_NAME: &str = "OpenBlade.SessionAgent.exe";
#[cfg(feature = "scratch-candidate-receipt")]
pub(super) const SCRATCH_LAUNCH_NAME: &str = "candidate-setup-launch.v3";

#[cfg(feature = "scratch-candidate-receipt")]
#[derive(Clone, Copy)]
enum Phase {
    Committed,
    #[cfg(feature = "scratch-candidate-receipt")]
    Startup,
}

#[cfg(feature = "scratch-candidate-receipt")]
impl Phase {
    fn record(self) -> (&'static str, &'static str) {
        match self {
            Self::Committed => (COMMITTED_NAME, "SetupPayloadCommitted"),
            #[cfg(feature = "scratch-candidate-receipt")]
            Self::Startup => ("candidate-package.startup.json", "SetupPayloadStarting"),
        }
    }
}

/// Evidence about files on disk. The process must separately prove that it
/// loaded this generation before it can open the production endpoint.
pub(crate) struct CommittedServiceImage {
    #[cfg(feature = "scratch-candidate-receipt")]
    _files: Option<RetainedRecord>,
    package: Option<native_package::InstalledPackage>,
}

impl CommittedServiceImage {
    pub(crate) fn require_current(&mut self) -> io::Result<()> {
        if let Some(package) = &mut self.package {
            package.require_current()?;
        }
        Ok(())
    }
}

#[cfg(feature = "scratch-candidate-receipt")]
struct RetainedRecord {
    _image: File,
    _receipt: File,
    _install_directory: File,
    _ancestors: Vec<File>,
}

#[cfg(feature = "scratch-candidate-receipt")]
pub(super) struct ActiveBinding<'a> {
    pub(super) digest: [u8; 32],
    pub(super) setup_identity: [u8; 12],
    pub(super) deployment_request_id: &'a str,
    pub(super) launch_secret_digest: [u8; 32],
}

#[cfg(feature = "scratch-candidate-receipt")]
fn verify_scratch_launch(
    evidence: &[u8; 120],
    active: &[u8],
    issuer: &ActiveBinding<'_>,
) -> io::Result<[u8; 32]> {
    // v2 launch file: broker/receiver generations, active/closure/secret digests.
    let active_digest: [u8; 32] = Sha256::digest(active).into();
    if evidence[12..24] != issuer.setup_identity
        || evidence[24..56] != active_digest
        || evidence[88..120] != issuer.launch_secret_digest
    {
        return Err(denied(
            "provisional Setup issuer differs from the broker launch evidence",
        ));
    }
    Ok(evidence[56..88]
        .try_into()
        .expect("fixed v2 closure digest"))
}

// The caller supplies the kernel-pinned identity; tests exercise comparison only.
#[cfg(feature = "scratch-candidate-receipt")]
pub(super) fn verify_setup_generation(commitment: &str, identity: [u8; 12]) -> io::Result<()> {
    let identity_hex: String = identity.iter().map(|byte| format!("{byte:02x}")).collect();
    if commitment.len() != 152 || !lower_hex(commitment, 152) || commitment[128..] != identity_hex {
        return Err(denied("provisional Setup process generation differs"));
    }
    Ok(())
}

// In-memory stream seam for size/stability failures; production supplies a same-handle snapshot.
#[cfg(feature = "scratch-candidate-receipt")]
fn read_launch_evidence(
    mut reader: impl Read,
    size: u64,
    unchanged: impl FnOnce() -> io::Result<bool>,
) -> io::Result<[u8; 120]> {
    let mut evidence = [0; 120];
    if size != evidence.len() as u64 {
        return Err(denied("invalid scratch launch evidence length"));
    }
    reader.read_exact(&mut evidence)?;
    if !unchanged()? {
        return Err(denied(
            "scratch launch evidence changed during verification",
        ));
    }
    Ok(evidence)
}

#[cfg(all(test, feature = "scratch-candidate-receipt"))]
mod scratch_launch_tests {
    use super::*;

    // A test-only byte authority for guard coverage, never a live peer claim.
    fn authenticated_test_evidence() -> (Vec<u8>, ActiveBinding<'static>, [u8; 120]) {
        let active = b"canonical active deployment test evidence".to_vec();
        let issuer = ActiveBinding {
            digest: [0; 32],
            setup_identity: [1; 12],
            deployment_request_id: "310b8316-4a6d-412d-82cb-c4c1b34a725d",
            launch_secret_digest: Sha256::digest([2; 32]).into(),
        };
        let mut evidence = [0; 120];
        evidence[12..24].copy_from_slice(&issuer.setup_identity);
        evidence[24..56].copy_from_slice(&Sha256::digest(&active));
        evidence[56..88].fill(3);
        evidence[88..].copy_from_slice(&issuer.launch_secret_digest);
        assert_eq!(
            verify_scratch_launch(&evidence, &active, &issuer).unwrap(),
            [3; 32]
        );
        (active, issuer, evidence)
    }

    fn reject_launch_byte(offset: usize) {
        let (active, issuer, mut evidence) = authenticated_test_evidence();
        evidence[offset] ^= 1;
        assert_eq!(
            verify_scratch_launch(&evidence, &active, &issuer)
                .unwrap_err()
                .to_string(),
            "provisional Setup issuer differs from the broker launch evidence"
        );
    }

    #[test]
    fn authenticated_launch_rejects_receiver_generation() {
        reject_launch_byte(16);
    }
    #[test]
    fn authenticated_launch_rejects_original_active_binding() {
        reject_launch_byte(24);
    }
    #[test]
    fn authenticated_launch_rejects_inherited_secret() {
        reject_launch_byte(88);
    }

    #[test]
    fn authenticated_startup_rejects_different_kernel_pinned_generation_comparison() {
        let (_, issuer, _) = authenticated_test_evidence();
        let mut commitment = "00".repeat(64);
        commitment.extend(
            issuer
                .setup_identity
                .iter()
                .map(|byte| format!("{byte:02x}")),
        );
        verify_setup_generation(&commitment, issuer.setup_identity).unwrap();
        let mut other = issuer.setup_identity;
        other[4] ^= 1;
        assert_eq!(
            verify_setup_generation(&commitment, other)
                .unwrap_err()
                .to_string(),
            "provisional Setup process generation differs"
        );
    }

    #[test]
    fn authenticated_launch_rejects_exact_length_before_reading() {
        let (_, _, evidence) = authenticated_test_evidence();
        for size in [0, 119, 121] {
            assert_eq!(
                read_launch_evidence(evidence.as_slice(), size, || panic!(
                    "length must refuse first"
                ))
                .unwrap_err()
                .to_string(),
                "invalid scratch launch evidence length"
            );
        }
    }

    #[test]
    fn authenticated_launch_rejects_same_handle_change_after_reading() {
        let (_, _, evidence) = authenticated_test_evidence();
        assert_eq!(
            read_launch_evidence(evidence.as_slice(), 120, || Ok(true)).unwrap(),
            evidence
        );
        assert_eq!(
            read_launch_evidence(evidence.as_slice(), 120, || Ok(false))
                .unwrap_err()
                .to_string(),
            "scratch launch evidence changed during verification"
        );
        assert_eq!(
            read_launch_evidence(&evidence[..119], 120, || Ok(true))
                .unwrap_err()
                .kind(),
            io::ErrorKind::UnexpectedEof
        );
    }
}

#[cfg(feature = "installed-service-candidate")]
// Reserved constructor for the compile-only native composition; no production caller yet.
#[allow(dead_code)]
pub(crate) fn read_installed() -> io::Result<CommittedServiceImage> {
    let package = native_package::read_installed_package()?;
    Ok(CommittedServiceImage {
        #[cfg(feature = "scratch-candidate-receipt")]
        _files: None,
        package: Some(package),
    })
}

#[cfg(feature = "scratch-candidate-receipt")]
pub(super) fn read(
    install: &Path,
    scope: &str,
    check_directory: impl Fn(&File, usize, bool, usize) -> io::Result<()>,
    check_file: impl Fn(&File) -> io::Result<()>,
    expected_receipt: impl FnOnce() -> Option<[u8; 32]>,
) -> io::Result<CommittedServiceImage> {
    let (files, _) = read_record(
        install,
        scope,
        Phase::Committed,
        check_directory,
        check_file,
        expected_receipt,
        #[cfg(feature = "scratch-candidate-receipt")]
        None,
    )?;
    Ok(CommittedServiceImage {
        _files: Some(files),
        package: None,
    })
}

#[cfg(feature = "scratch-candidate-receipt")]
pub(super) struct ProvisionalStartup {
    _files: RetainedRecord,
    _evidence: StartupEvidence,
}

#[cfg(feature = "scratch-candidate-receipt")]
struct StartupEvidence {
    binding: File,
    agent: File,
    _launch: File,
    _request: File,
    _staging: File,
    closure: crate::scratch_receiver_closure::ScratchReceiverClosure,
}

#[cfg(feature = "scratch-candidate-receipt")]
impl ProvisionalStartup {
    pub(super) fn require_unchanged(&mut self) -> io::Result<()> {
        self._evidence.closure.require_unchanged().map(|_| ())
    }
}

#[cfg(feature = "scratch-candidate-receipt")]
pub(super) fn read_provisional(
    install: &Path,
    scope: &str,
    check_directory: impl Fn(&File, usize, bool, usize) -> io::Result<()>,
    check_file: impl Fn(&File) -> io::Result<()>,
    expected: [u8; 32],
    active_binding: ActiveBinding<'_>,
) -> io::Result<ProvisionalStartup> {
    let (files, startup) = read_record(
        install,
        scope,
        Phase::Startup,
        check_directory,
        check_file,
        || Some(expected),
        Some(active_binding),
    )?;
    Ok(ProvisionalStartup {
        _files: files,
        _evidence: startup.expect("startup retains the complete launch evidence and closure"),
    })
}

#[cfg(feature = "scratch-candidate-receipt")]
type OptionalStartup = Option<StartupEvidence>;

#[cfg(feature = "scratch-candidate-receipt")]
fn read_record(
    install: &Path,
    scope: &str,
    phase: Phase,
    check_directory: impl Fn(&File, usize, bool, usize) -> io::Result<()>,
    check_file: impl Fn(&File) -> io::Result<()>,
    expected_receipt: impl FnOnce() -> Option<[u8; 32]>,
    #[cfg(feature = "scratch-candidate-receipt")] active_binding: Option<ActiveBinding<'_>>,
) -> io::Result<(RetainedRecord, OptionalStartup)> {
    let (root, ancestors, info) = pin_root(install)?;
    require_ntfs(&ancestors[0])?;
    let check_directories = || {
        for (index, directory) in ancestors.iter().enumerate() {
            if directory_info(directory)?.dwVolumeSerialNumber != info.dwVolumeSerialNumber {
                return Err(denied("candidate directory ancestry changed volumes"));
            }
            check_directory(directory, index, false, ancestors.len())?;
        }
        if file_identity(&directory_info(&root)?) != file_identity(&info) {
            return Err(denied("candidate install directory identity changed"));
        }
        check_directory(&root, ancestors.len(), true, ancestors.len())
    };
    check_directories()?;
    let open = |directory: &File, name| {
        open_relative(
            directory,
            OsStr::new(name),
            FILE_GENERIC_READ,
            FILE_SHARE_READ,
            FILE_OPEN,
            FILE_NON_DIRECTORY_FILE | FILE_OPEN_REPARSE_POINT,
        )
    };
    let verify_file = |file: &File| {
        let identity = check_regular_file(file)?;
        // SAFETY: file owns a live handle.
        if identity.index == 0
            || identity.volume != info.dwVolumeSerialNumber
            || unsafe_disk_type(file) != FILE_TYPE_DISK
        {
            return Err(denied(
                "candidate evidence is outside the retained NTFS tree",
            ));
        }
        check_file(file)
    };

    let (name, expected_phase) = phase.record();
    let mut receipt = open(&root, name)?;
    verify_file(&receipt)?;
    let before = snapshot(&receipt)?;
    if !(1..=MAX_RECEIPT_BYTES).contains(&before.size) {
        return Err(invalid_receipt());
    }
    let mut bytes = Vec::with_capacity(before.size as usize);
    (&mut receipt)
        .take(before.size + 1)
        .read_to_end(&mut bytes)?;
    if bytes.len() as u64 != before.size || snapshot(&receipt)? != before {
        return Err(denied("candidate receipt changed during verification"));
    }
    let record = parse(&bytes, scope, expected_phase)?;
    let expected = expected_receipt()
        .ok_or_else(|| denied("authenticated committed release identity is unavailable"))?;
    if <[u8; 32]>::from(Sha256::digest(&bytes)) != expected {
        return Err(denied(
            "candidate receipt differs from the authenticated release binding",
        ));
    }

    #[cfg(feature = "scratch-candidate-receipt")]
    let startup = if let Some(expected_binding) = active_binding {
        if record.request_id != expected_binding.deployment_request_id {
            return Err(denied(
                "provisional receipt has a different deployment request",
            ));
        }
        // Scratch layout is <scratch>/program-files/OpenBlade. The retained
        // scratch ancestor already passed the same owner and DACL checks.
        let scratch = ancestors
            .iter()
            .rev()
            .nth(1)
            .ok_or_else(|| denied("missing candidate scratch ancestor"))?;
        let mut binding = open(scratch, "unresolved-deployment.json")?;
        verify_file(&binding)?;
        let before = snapshot(&binding)?;
        // BrokerInFlightDeploymentCodec.MaximumBytes.
        if !(1..=2048).contains(&before.size) {
            return Err(denied("invalid active candidate binding size"));
        }
        let mut active = Vec::with_capacity(before.size as usize);
        (&mut binding)
            .take(before.size + 1)
            .read_to_end(&mut active)?;
        let mut hash = Sha256::new();
        hash.update(&active);
        hash.update(expected_binding.setup_identity);
        hash.update(expected);
        if active.len() as u64 != before.size
            || snapshot(&binding)? != before
            || <[u8; 32]>::from(hash.finalize()) != expected_binding.digest
        {
            return Err(denied(
                "provisional receipt has a different active generation",
            ));
        }
        // Verify the original broker launch evidence using the issuer identity
        // obtained from the live pipe peer and the inherited launch secret.
        let open_directory = |parent: &File, name: &str| {
            use windows_sys::Wdk::Storage::FileSystem::FILE_DIRECTORY_FILE;
            let directory = open_relative(
                parent,
                OsStr::new(name),
                FILE_GENERIC_READ,
                FILE_SHARE_READ,
                FILE_OPEN,
                FILE_DIRECTORY_FILE | FILE_OPEN_REPARSE_POINT,
            )?;
            if directory_info(&directory)?.dwVolumeSerialNumber != info.dwVolumeSerialNumber {
                return Err(denied("scratch launch directory changed volumes"));
            }
            check_directory(&directory, ancestors.len(), true, ancestors.len())?;
            Ok(directory)
        };
        let staging = open_directory(scratch, "staging")?;
        let request = open_directory(&staging, &record.request_id.replace('-', ""))?;
        let launch = open(&request, SCRATCH_LAUNCH_NAME)?;
        verify_file(&launch)?;
        let launch_snapshot = snapshot(&launch)?;
        let mut reader = &launch;
        let evidence = read_launch_evidence(&mut reader, launch_snapshot.size, || {
            Ok(snapshot(&launch)? == launch_snapshot)
        })?;
        let receiver_digest = verify_scratch_launch(&evidence, &active, &expected_binding)?;
        let owner = crate::scratch_receipt_policy::retained_owner(scratch)?;
        let closure = crate::scratch_receiver_closure::ScratchReceiverClosure::retain(
            scratch,
            &owner,
            receiver_digest,
        )
        .map_err(|error| {
            io::Error::new(
                error.kind(),
                format!("Setup issuer image verification failed: {error}"),
            )
        })?;
        let mut agent = open(&root, AGENT_NAME)?;
        verify_file(&agent)?;
        service_image::verify(
            &mut agent,
            record.session_agent.length,
            digest(&record.session_agent.sha256)?,
        )
        .map_err(|error| {
            io::Error::new(
                error.kind(),
                format!("Session Agent verification failed: {error}"),
            )
        })?;
        Some(StartupEvidence {
            binding,
            agent,
            _launch: launch,
            _request: request,
            _staging: staging,
            closure,
        })
    } else {
        None
    };

    let mut image = open(&root, IMAGE_NAME)?;
    verify_file(&image)?;
    service_image::verify(
        &mut image,
        record.service.length,
        digest(&record.service.sha256)?,
    )?;
    verify_file(&image)?;
    verify_file(&receipt)?;
    #[cfg(feature = "scratch-candidate-receipt")]
    if let Some(evidence) = &startup {
        verify_file(&evidence.binding)?;
        verify_file(&evidence.agent)?;
    }
    #[cfg(feature = "scratch-candidate-receipt")]
    let mut startup = startup;
    #[cfg(feature = "scratch-candidate-receipt")]
    if let Some(evidence) = &mut startup {
        evidence.closure.require_unchanged()?;
    }
    check_directories()?;
    if snapshot(&receipt)? != before {
        return Err(denied("candidate receipt changed during verification"));
    }
    Ok((
        RetainedRecord {
            _image: image,
            _receipt: receipt,
            _install_directory: root,
            _ancestors: ancestors,
        },
        startup,
    ))
}

#[cfg(feature = "scratch-candidate-receipt")]
#[allow(unsafe_code)]
fn unsafe_disk_type(file: &File) -> u32 {
    // SAFETY: The borrowed file owns a live handle for the call.
    unsafe { GetFileType(file.as_raw_handle()) }
}

#[cfg(feature = "scratch-candidate-receipt")]
#[derive(Deserialize, Serialize)]
#[serde(deny_unknown_fields, rename_all = "camelCase")]
struct Receipt {
    schema: u8,
    phase: String,
    scratch_id: String,
    installation_id: String,
    request_id: String,
    issued_at: String,
    package_sha256: String,
    package_length: u64,
    version: String,
    source_revision: String,
    manifest_sha256: String,
    enrollment_sha256: String,
    broker_binding_sha256: String,
    signing_key_id: String,
    sequence: i64,
    service: RoleImage,
    session_agent: RoleImage,
}

#[cfg(feature = "scratch-candidate-receipt")]
#[derive(Deserialize, Serialize)]
#[serde(deny_unknown_fields, rename_all = "PascalCase")]
struct RoleImage {
    role: u8,
    payload_id: String,
    payload_path: String,
    cabinet_member: String,
    installed_name: String,
    length: u64,
    sha256: String,
}

#[cfg(feature = "scratch-candidate-receipt")]
impl RoleImage {
    fn matches(&self, role: u8, name: &str) -> bool {
        self.role == role
            && self.installed_name == name
            && self.payload_path.strip_prefix("payload\\") == Some(name)
            && canonical_id(&self.payload_id)
            && canonical_id(&self.cabinet_member)
            && (1..=MAX_IMAGE_BYTES).contains(&self.length)
            && lower_hex(&self.sha256, 64)
    }
}

#[cfg(feature = "scratch-candidate-receipt")]
fn parse(bytes: &[u8], scope: &str, expected_phase: &str) -> io::Result<Receipt> {
    let record: Receipt = serde_json::from_slice(bytes).map_err(|_| invalid_receipt())?;
    // Compare the canonical serializer order and spellings, including integer
    // syntax, escaping and whitespace. Serde also rejects duplicate fields.
    if record.schema != 2
        || record.phase != expected_phase
        || record.scratch_id != scope
        || !guid(scope)
        || !nonempty_guid(&record.installation_id)
        || !nonempty_guid(&record.request_id)
        || !utc_timestamp(&record.issued_at)
        || !lower_hex(&record.package_sha256, 64)
        || !(1..=1024 * 1024 * 1024).contains(&record.package_length)
        || !version(&record.version)
        || !lower_hex(&record.source_revision, 40)
        || !lower_hex(&record.manifest_sha256, 64)
        || !lower_hex(&record.enrollment_sha256, 64)
        || !lower_hex(&record.broker_binding_sha256, 64)
        || !lower_hex(&record.signing_key_id, 64)
        || record.sequence <= 0
        || !record.service.matches(0, IMAGE_NAME)
        || !record.session_agent.matches(1, AGENT_NAME)
        || record
            .service
            .payload_id
            .eq_ignore_ascii_case(&record.session_agent.payload_id)
        || record.service.cabinet_member == record.session_agent.cabinet_member
        || serde_json::to_vec(&record).map_err(io::Error::other)? != bytes
    {
        return Err(invalid_receipt());
    }
    Ok(record)
}

#[cfg(feature = "scratch-candidate-receipt")]
fn invalid_receipt() -> io::Error {
    io::Error::new(
        io::ErrorKind::InvalidData,
        "invalid canonical candidate receipt",
    )
}

#[cfg(feature = "scratch-candidate-receipt")]
fn canonical_id(value: &str) -> bool {
    (1..=128).contains(&value.len())
        && value
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || b"_.".contains(&byte))
}

#[cfg(feature = "scratch-candidate-receipt")]
pub(super) fn lower_hex(value: &str, len: usize) -> bool {
    value.len() == len
        && value
            .bytes()
            .all(|byte| matches!(byte, b'0'..=b'9' | b'a'..=b'f'))
}

#[cfg(feature = "scratch-candidate-receipt")]
fn guid(value: &str) -> bool {
    value.len() == 36
        && value.split('-').map(str::len).eq([8, 4, 4, 4, 12])
        && value.split('-').all(|part| lower_hex(part, part.len()))
}

#[cfg(feature = "scratch-candidate-receipt")]
fn nonempty_guid(value: &str) -> bool {
    value != EMPTY_GUID && guid(value)
}

#[cfg(feature = "scratch-candidate-receipt")]
fn version(value: &str) -> bool {
    let parts = value.split('.');
    parts.clone().count() == 4
        && parts.into_iter().all(|part| {
            part.parse::<i32>()
                .is_ok_and(|n| n >= 0 && n.to_string() == part)
        })
}

#[cfg(feature = "scratch-candidate-receipt")]
fn utc_timestamp(value: &str) -> bool {
    // System.Text.Json emits UTC DateTimeOffset with +00:00 and up to seven
    // fractional digits, dropping trailing zeroes.
    let Some(value) = value.strip_suffix("+00:00") else {
        return false;
    };
    let (whole, fraction) = value.split_once('.').unwrap_or((value, ""));
    if (value.len() != whole.len()
        && (!(1..=7).contains(&fraction.len())
            || fraction.ends_with('0')
            || !fraction.bytes().all(|byte| byte.is_ascii_digit())))
        || whole.len() != 19
        || !whole.bytes().enumerate().all(|(i, byte)| match i {
            4 | 7 => byte == b'-',
            10 => byte == b'T',
            13 | 16 => byte == b':',
            _ => byte.is_ascii_digit(),
        })
    {
        return false;
    }
    let number = |start, end| whole[start..end].parse::<u32>().expect("validated digits");
    let year = number(0, 4);
    let days = match number(5, 7) {
        1 | 3 | 5 | 7 | 8 | 10 | 12 => 31,
        4 | 6 | 9 | 11 => 30,
        2 if year.is_multiple_of(4) && (!year.is_multiple_of(100) || year.is_multiple_of(400)) => {
            29
        }
        2 => 28,
        _ => 0,
    };
    year > 0
        && (1..=days).contains(&number(8, 10))
        && number(11, 13) < 24
        && number(14, 16) < 60
        && number(17, 19) < 60
}

#[cfg(feature = "scratch-candidate-receipt")]
pub(super) fn digest(value: &str) -> io::Result<[u8; 32]> {
    if !lower_hex(value, 64) {
        return Err(invalid_receipt());
    }
    let mut bytes = [0; 32];
    for (index, byte) in bytes.iter_mut().enumerate() {
        *byte = u8::from_str_radix(&value[2 * index..2 * index + 2], 16)
            .map_err(|_| invalid_receipt())?;
    }
    Ok(bytes)
}

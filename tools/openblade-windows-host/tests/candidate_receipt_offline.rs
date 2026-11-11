//! Data-only NTFS receipt verification. These tests never launch a receiver or use IPC.
#![cfg(all(test, windows, feature = "scratch-candidate-receipt"))]
#![allow(dead_code)]

#[path = "../src/candidate_receipt.rs"]
mod candidate_receipt;
#[path = "../src/scratch_receipt_policy.rs"]
mod scratch_receipt_policy;
#[path = "../src/scratch_receiver_closure.rs"]
mod scratch_receiver_closure;
#[path = "../src/service_image.rs"]
mod service_image;
#[path = "../src/service_install_identity.rs"]
mod service_install_identity;
#[path = "../src/windows_protected_path.rs"]
mod windows_protected_path;
#[path = "../src/windows_token.rs"]
mod windows_token;

use sha2::{Digest, Sha256};
use std::{
    fs::{self, OpenOptions},
    io,
    os::windows::{ffi::OsStrExt, fs::OpenOptionsExt},
    path::{Path, PathBuf},
    sync::atomic::{AtomicU64, Ordering},
};
use windows_protected_path::verify_descriptor;

const SCOPE: &str = "300b8316-4a6d-412d-82cb-c4c1b34a725d";
const REQUEST: &str = "310b8316-4a6d-412d-82cb-c4c1b34a725d";
const OTHER_REQUEST: &str = "320b8316-4a6d-412d-82cb-c4c1b34a725d";
const SETUP: [u8; 12] = [7; 12];
const SECRET: [u8; 32] = [8; 32];
const SERVICE: &[u8] = b"data-only service image";
const AGENT: &[u8] = b"data-only session agent image";
const ACTIVE: &[u8] = b"data-only active deployment generation";
const RECEIPT_NAME: &str = "candidate-package.startup.json";
static NEXT: AtomicU64 = AtomicU64::new(0);

fn hash(bytes: &[u8]) -> [u8; 32] {
    Sha256::digest(bytes).into()
}
fn hex(bytes: &[u8]) -> String {
    bytes.iter().map(|b| format!("{b:02x}")).collect()
}

// Set one owner-only inheritable ACE before creating any descendants. The same
// policy used by the scratch receiver independently checks every retained handle.
#[allow(unsafe_code)]
fn protect_root(path: &Path, owner: &[u8]) -> io::Result<()> {
    use windows_sys::Win32::Security::{
        DACL_SECURITY_INFORMATION, OWNER_SECURITY_INFORMATION, PROTECTED_DACL_SECURITY_INFORMATION,
        SetFileSecurityW,
    };
    use windows_sys::Win32::Storage::FileSystem::FILE_ALL_ACCESS;
    let acl_offset = 20 + owner.len();
    let ace_size = 8 + owner.len();
    let mut descriptor = vec![0u8; acl_offset + 8 + ace_size];
    descriptor[0] = 1;
    descriptor[2..4].copy_from_slice(&0x9004u16.to_le_bytes());
    descriptor[4..8].copy_from_slice(&20u32.to_le_bytes());
    descriptor[16..20].copy_from_slice(&(acl_offset as u32).to_le_bytes());
    descriptor[20..acl_offset].copy_from_slice(owner);
    let acl = &mut descriptor[acl_offset..];
    acl[0] = 2;
    acl[2..4].copy_from_slice(&((8 + ace_size) as u16).to_le_bytes());
    acl[4..6].copy_from_slice(&1u16.to_le_bytes());
    acl[9] = 3;
    acl[10..12].copy_from_slice(&(ace_size as u16).to_le_bytes());
    acl[12..16].copy_from_slice(&FILE_ALL_ACCESS.to_le_bytes());
    acl[16..].copy_from_slice(owner);
    let path: Vec<u16> = path.as_os_str().encode_wide().chain(Some(0)).collect();
    // SAFETY: path is terminated and descriptor is a complete self-relative SD.
    if unsafe {
        SetFileSecurityW(
            path.as_ptr(),
            OWNER_SECURITY_INFORMATION
                | DACL_SECURITY_INFORMATION
                | PROTECTED_DACL_SECURITY_INFORMATION,
            descriptor.as_mut_ptr().cast(),
        )
    } == 0
    {
        return Err(io::Error::last_os_error());
    }
    Ok(())
}

struct Tree {
    root: PathBuf,
    install: PathBuf,
    owner: Vec<u8>,
    receipt: Vec<u8>,
    launch: Vec<u8>,
    expected: [u8; 32],
    active_digest: [u8; 32],
}

impl Tree {
    fn new() -> Self {
        let owner = scratch_receipt_policy::owner().expect("standard-token offline test");
        let nonce = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap()
            .as_nanos();
        let root = service_install_identity::known_folder(
            &windows_sys::Win32::UI::Shell::FOLDERID_LocalAppData,
        )
        .unwrap()
        .join("Temp")
        .join(format!(
            "OpenBlade.ReceiptOffline-{nonce}-{}",
            NEXT.fetch_add(1, Ordering::Relaxed)
        ));
        fs::create_dir(&root).unwrap();
        protect_root(&root, &owner).unwrap();
        let install = root.join("program-files").join("OpenBlade");
        fs::create_dir_all(&install).unwrap();
        fs::create_dir_all(root.join("staging").join(REQUEST.replace('-', ""))).unwrap();
        fs::create_dir(root.join("receiver")).unwrap();
        let receipt = canonical_receipt();
        let expected = hash(&receipt);
        let mut binding = Sha256::new();
        binding.update(ACTIVE);
        binding.update(SETUP);
        binding.update(expected);
        let active_digest = binding.finalize().into();
        fs::write(install.join(RECEIPT_NAME), &receipt).unwrap();
        fs::write(install.join("OpenBlade.Service.exe"), SERVICE).unwrap();
        fs::write(install.join("OpenBlade.SessionAgent.exe"), AGENT).unwrap();
        fs::write(root.join("unresolved-deployment.json"), ACTIVE).unwrap();
        let closure_digest = install_receiver_closure(&root);
        let mut launch = vec![0u8; 120];
        launch[..12].fill(6);
        launch[12..24].copy_from_slice(&SETUP);
        launch[24..56].copy_from_slice(&hash(ACTIVE));
        launch[56..88].copy_from_slice(&closure_digest);
        launch[88..].copy_from_slice(&SECRET);
        let tree = Self {
            root,
            install,
            owner,
            receipt,
            launch,
            expected,
            active_digest,
        };
        tree.write_launch();
        tree
    }
    fn launch_path(&self) -> PathBuf {
        self.root
            .join("staging")
            .join(REQUEST.replace('-', ""))
            .join(candidate_receipt::SCRATCH_LAUNCH_NAME)
    }
    fn write_launch(&self) {
        fs::write(self.launch_path(), &self.launch).unwrap();
    }
    fn read(
        &self,
        request: &str,
        digest: [u8; 32],
    ) -> io::Result<candidate_receipt::ProvisionalStartup> {
        candidate_receipt::read_provisional(
            &self.install,
            SCOPE,
            |file, index, _, count| {
                let scratch_index = count - 2;
                if index >= scratch_index {
                    verify_descriptor(file, |bytes| {
                        scratch_receipt_policy::accepts(
                            bytes,
                            &self.owner,
                            true,
                            index == scratch_index,
                        )
                    })?;
                }
                Ok(())
            },
            |file| {
                verify_descriptor(file, |bytes| {
                    scratch_receipt_policy::accepts(bytes, &self.owner, false, false)
                })
            },
            self.expected,
            candidate_receipt::ActiveBinding {
                digest,
                setup_identity: SETUP,
                deployment_request_id: request,
                launch_secret_digest: SECRET,
            },
        )
    }
    fn error(&self) -> io::Error {
        self.read(REQUEST, self.active_digest)
            .err()
            .expect("must reject target mutation")
    }
    fn rebind_receipt(&mut self, receipt: Vec<u8>) {
        self.receipt = receipt;
        self.expected = hash(&self.receipt);
        let mut binding = Sha256::new();
        binding.update(ACTIVE);
        binding.update(SETUP);
        binding.update(self.expected);
        self.active_digest = binding.finalize().into();
        fs::write(self.install.join(RECEIPT_NAME), &self.receipt).unwrap();
    }
}
impl Drop for Tree {
    fn drop(&mut self) {
        fs::remove_dir_all(&self.root).expect("remove private offline receipt tree");
    }
}

fn canonical_receipt() -> Vec<u8> {
    format!(concat!("{{\"schema\":2,\"phase\":\"SetupPayloadStarting\",\"scratchId\":\"{}\",\"installationId\":\"{}\",\"requestId\":\"{}\",\"issuedAt\":\"2026-11-10T12:00:00+00:00\",\"packageSha256\":\"{}\",\"packageLength\":1,\"version\":\"0.0.0.0\",\"sourceRevision\":\"{}\",\"manifestSha256\":\"{}\",\"enrollmentSha256\":\"{}\",\"brokerBindingSha256\":\"{}\",\"signingKeyId\":\"{}\",\"sequence\":1,",
    "\"service\":{{\"Role\":0,\"PayloadId\":\"ServicePayload\",\"PayloadPath\":\"payload\\\\OpenBlade.Service.exe\",\"CabinetMember\":\"ServiceMember\",\"InstalledName\":\"OpenBlade.Service.exe\",\"Length\":{},\"Sha256\":\"{}\"}},",
    "\"sessionAgent\":{{\"Role\":1,\"PayloadId\":\"AgentPayload\",\"PayloadPath\":\"payload\\\\OpenBlade.SessionAgent.exe\",\"CabinetMember\":\"AgentMember\",\"InstalledName\":\"OpenBlade.SessionAgent.exe\",\"Length\":{},\"Sha256\":\"{}\"}}}}"),
    SCOPE, OTHER_REQUEST, REQUEST, "11".repeat(32), "22".repeat(20), "33".repeat(32), "44".repeat(32), "55".repeat(32), "66".repeat(32), SERVICE.len(), hex(&hash(SERVICE)), AGENT.len(), hex(&hash(AGENT))).into_bytes()
}

#[test]
fn valid_authenticated_provisional_receipt_retains_its_disk_evidence() {
    let tree = Tree::new();
    let mut retained = tree
        .read(REQUEST, tree.active_digest)
        .expect("valid prior guards and launch evidence");
    retained
        .require_unchanged()
        .expect("native artifact remains unchanged");
    // Retention must prevent a subsequent writer from changing a checked image.
    assert!(
        OpenOptions::new()
            .write(true)
            .open(tree.install.join("OpenBlade.Service.exe"))
            .is_err()
    );
    drop(retained);
}

#[test]
fn deployment_request_and_active_generation_reach_their_specific_guards() {
    let tree = Tree::new();
    let error = tree.read(OTHER_REQUEST, tree.active_digest).err().unwrap();
    assert_eq!(
        error.to_string(),
        "provisional receipt has a different deployment request"
    );
    let mut changed = tree.active_digest;
    changed[0] ^= 1;
    let error = tree.read(REQUEST, changed).err().unwrap();
    assert_eq!(
        error.to_string(),
        "provisional receipt has a different active generation"
    );
}

#[test]
fn launch_generation_binding_and_secret_mutations_reach_launch_authentication() {
    for offset in [12, 24, 88] {
        let mut tree = Tree::new();
        tree.launch[offset] ^= 1;
        tree.write_launch();
        assert_eq!(
            tree.error().to_string(),
            "provisional Setup issuer differs from the broker launch evidence",
            "offset {offset}"
        );
    }
}

#[test]
fn launch_evidence_requires_exact_v3_length() {
    for length in [119, 121] {
        let mut tree = Tree::new();
        tree.launch.resize(length, 0);
        tree.write_launch();
        assert_eq!(
            tree.error().to_string(),
            "invalid scratch launch evidence length"
        );
    }
}

#[test]
fn exclusively_locked_receipt_fails_at_the_actual_ntfs_open() {
    let tree = Tree::new();
    let _locked = OpenOptions::new()
        .read(true)
        .share_mode(0)
        .open(tree.install.join(RECEIPT_NAME))
        .unwrap();
    let error = tree.error();
    assert_eq!(
        error.raw_os_error(),
        Some(32),
        "must be sharing violation, not an earlier parser rejection"
    );
}

#[test]
fn malformed_receipt_and_role_are_rejected_after_valid_path_admission() {
    for mutation in ["trailing", "role"] {
        let mut tree = Tree::new();
        let mut receipt = String::from_utf8(tree.receipt.clone()).unwrap();
        if mutation == "trailing" {
            receipt.push(' ');
        } else {
            receipt = receipt.replacen("\"Role\":0", "\"Role\":1", 1);
        }
        tree.rebind_receipt(receipt.into_bytes());
        assert_eq!(
            tree.error().to_string(),
            "invalid canonical candidate receipt"
        );
    }
}

#[test]
fn changed_service_and_agent_reach_independent_image_digest_verification() {
    for (name, expected_error) in [
        (
            "OpenBlade.Service.exe",
            "installed image does not match authenticated build evidence",
        ),
        (
            "OpenBlade.SessionAgent.exe",
            "Session Agent verification failed: installed image does not match authenticated build evidence",
        ),
    ] {
        let tree = Tree::new();
        let path = tree.install.join(name);
        let mut bytes = fs::read(&path).unwrap();
        bytes[0] ^= 1;
        fs::write(path, bytes).unwrap();
        assert_eq!(tree.error().to_string(), expected_error);
    }
}

fn install_receiver_closure(root: &Path) -> [u8; 32] {
    let v: serde_json::Value =
        serde_json::from_str(include_str!("data/scratch-native-receiver-v2.json")).unwrap();
    let image: Vec<u8> = v["imageHex"]
        .as_str()
        .unwrap()
        .as_bytes()
        .chunks_exact(2)
        .map(|x| u8::from_str_radix(std::str::from_utf8(x).unwrap(), 16).unwrap())
        .collect();
    fs::write(
        root.join("receiver/OpenBlade.CandidateReceipt.Scratch.exe"),
        image,
    )
    .unwrap();
    candidate_receipt::digest(v["canonicalSha256"].as_str().unwrap()).unwrap()
}
#[test]
fn native_launch_filename_matches_real_csharp_vector() {
    let v: serde_json::Value =
        serde_json::from_str(include_str!("data/scratch-native-receiver-v2.json")).unwrap();
    assert_eq!(
        candidate_receipt::SCRATCH_LAUNCH_NAME,
        v["launchEvidenceName"].as_str().unwrap()
    );
}
#[test]
fn obsolete_launch_evidence_names_cannot_substitute_for_native_v3() {
    for obsolete in ["candidate-setup-launch.v1", "candidate-setup-launch.v2"] {
        let tree = Tree::new();
        let current = tree.launch_path();
        fs::rename(&current, current.with_file_name(obsolete)).unwrap();
        assert_eq!(tree.error().kind(), io::ErrorKind::NotFound);
    }
}
#[test]
fn changed_native_image_reaches_specific_authenticated_digest_refusal() {
    let tree = Tree::new();
    let path = tree
        .root
        .join("receiver/OpenBlade.CandidateReceipt.Scratch.exe");
    let mut image = fs::read(&path).unwrap();
    image[440] ^= 1;
    assert!(scratch_receiver_closure::accept_native_image(&image));
    fs::write(path, image).unwrap();
    assert_eq!(
        tree.error().to_string(),
        "native receiver differs from authenticated launch evidence"
    );
}
#[test]
fn retained_native_image_blocks_writes_and_releases_without_acl_changes() {
    let tree = Tree::new();
    let mut retained = tree.read(REQUEST, tree.active_digest).unwrap();
    let image = tree
        .root
        .join("receiver/OpenBlade.CandidateReceipt.Scratch.exe");
    assert!(OpenOptions::new().write(true).open(&image).is_err());
    assert!(fs::remove_file(&image).is_err());
    retained.require_unchanged().unwrap();
    drop(retained);
    OpenOptions::new().write(true).open(&image).unwrap();
}
#[test]
fn additions_are_refused_but_never_acl_sealed() {
    let tree = Tree::new();
    let mut retained = tree.read(REQUEST, tree.active_digest).unwrap();
    fs::write(tree.root.join("receiver/unexpected.dll"), b"data only").unwrap();
    assert!(retained.require_unchanged().is_err());
}
#[test]
fn obsolete_fdd_input_and_missing_native_image_refuse() {
    let tree = Tree::new();
    fs::write(
        tree.root
            .join("receiver/OpenBlade.CandidateReceipt.Scratch.deps.json"),
        b"{}",
    )
    .unwrap();
    assert!(tree.error().to_string().contains("obsolete or unexpected"));
    fs::remove_file(
        tree.root
            .join("receiver/OpenBlade.CandidateReceipt.Scratch.exe"),
    )
    .unwrap();
    assert_eq!(tree.error().kind(), io::ErrorKind::NotFound);
}
#[test]
fn production_read_refuses_even_a_valid_committed_record_without_authority() {
    let tree = Tree::new();
    let committed = String::from_utf8(tree.receipt.clone())
        .unwrap()
        .replace("SetupPayloadStarting", "SetupPayloadCommitted");
    fs::write(
        tree.install.join("candidate-package.committed.json"),
        committed,
    )
    .unwrap();
    let result = candidate_receipt::read(
        &tree.install,
        SCOPE,
        |file, index, _, count| {
            let scratch_index = count - 2;
            if index >= scratch_index {
                verify_descriptor(file, |bytes| {
                    scratch_receipt_policy::accepts(
                        bytes,
                        &tree.owner,
                        true,
                        index == scratch_index,
                    )
                })?;
            }
            Ok(())
        },
        |file| {
            verify_descriptor(file, |bytes| {
                scratch_receipt_policy::accepts(bytes, &tree.owner, false, false)
            })
        },
        || None,
    );
    let error = result
        .err()
        .expect("production authority is deliberately unavailable");
    assert_eq!(
        error.to_string(),
        "authenticated committed release identity is unavailable"
    );
}

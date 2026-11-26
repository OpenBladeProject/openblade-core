use super::*;

#[test]
fn read_composition_requires_its_own_capability_and_cannot_claim_metadata_only() {
    let features = "firmware-performance-compile-check,hid-identity-compile-check,installed-service-candidate,service-hardware-compile-check,service-mailbox-observation-v1,startup-custody-v1";
    let read = "private-service-custody-v1,service-mailbox-observation-v1";
    assert!(capabilities(2, "service", features, read).is_ok());
    assert!(capabilities(2, "agent", features, "none").is_ok());
    for capability in [
        "none",
        "private-service-custody-v1",
        "private-service-custody-v1,service-metadata-only-v1",
    ] {
        assert!(capabilities(2, "service", features, capability).is_err());
    }
    assert!(
        capabilities(
            2,
            "service",
            &format!("{features},service-metadata-only-v1"),
            read
        )
        .is_err()
    );
    assert!(
        capabilities(
            2,
            "service",
            &features.replace("service-hardware-compile-check,", ""),
            read
        )
        .is_err()
    );
    assert!(capabilities(1, "service", features, read).is_err());
    let record = encode(
        "OpenBlade.NativeArtifact.v2",
        &[
            "artifact",
            "service",
            "x86_64-pc-windows-msvc",
            "source",
            features,
            "compiler",
            "release",
            read,
        ],
    );
    assert_eq!(
        startup_policy(&record).unwrap(),
        "service-mailbox-observation-v1"
    );
}

fn vector() -> serde_json::Value {
    serde_json::from_str(include_str!("../../tests/data/native-package-v1.json")).unwrap()
}
fn bytes(v: &serde_json::Value, name: &str) -> Vec<u8> {
    decode64(v[name].as_str().unwrap()).unwrap()
}

#[test]
fn canonical_epoch_matches_csharp_bytes_and_digest() {
    let v = vector();
    let b = bytes(&v, "epoch");
    let e = Epoch::parse(&b).unwrap();
    assert_eq!(e.phase, "Committed");
    assert_eq!(e.ordinal, 1);
    assert_eq!(digest(&b), v["epochSha256"].as_str().unwrap());
    let f = lines(&b, "OpenBlade.NativeEpoch.v1", 11).unwrap();
    assert_eq!(b, encode("OpenBlade.NativeEpoch.v1", &f));
}
#[test]
fn canonical_epoch_rejects_trailing_bytes_unknown_phase_and_wrong_field_count() {
    let v = vector();
    let b = bytes(&v, "epoch");
    let mut extra = b.clone();
    extra.push(10);
    assert!(Epoch::parse(&extra).is_err());
    for change in ["Pending", "Provisional", "Unresolved", "Retired"] {
        assert!(
            Epoch::parse(
                &String::from_utf8(b.clone())
                    .unwrap()
                    .replace("Committed", change)
                    .into_bytes()
            )
            .is_err()
        );
    }
}
#[test]
fn canonical_epoch_rejects_reused_empty_ids_or_noncanonical_ordinal() {
    let v = vector();
    let b = String::from_utf8(bytes(&v, "epoch")).unwrap();
    for text in [
        b.replace(&"2".repeat(32), &"0".repeat(32)),
        b.replace("\n1\n", "\n01\n"),
        b.replace("\n1\n", "\n0\n"),
    ] {
        assert!(Epoch::parse(text.as_bytes()).is_err());
    }
}
#[test]
fn independent_localdev_p256_signature_authenticates_test_bytes() {
    let v = vector();
    let release = authenticate(
        &bytes(&v, "enrollment"),
        &bytes(&v, "manifest"),
        &bytes(&v, "signature"),
    )
    .unwrap();
    assert_eq!(release.channel, "LocalDev");
    assert_eq!(release.source_revision, "a".repeat(40));
}
#[test]
fn signature_rejects_wrong_domain_and_mutation() {
    let v = vector();
    let e = bytes(&v, "enrollment");
    let m = bytes(&v, "manifest");
    assert!(authenticate(&e, &m, &bytes(&v, "wrongDomain")).is_err());
    let mut signature = bytes(&v, "signature");
    signature[0] ^= 1;
    assert!(authenticate(&e, &m, &signature).is_err());
    signature.pop();
    assert!(authenticate(&e, &m, &signature).is_err());
}
#[test]
fn trust_rejects_unknown_key_missing_enrollment_and_stable_relabel() {
    let v = vector();
    let e = bytes(&v, "enrollment");
    let m = bytes(&v, "manifest");
    let s = bytes(&v, "signature");
    assert!(authenticate(b"{}", &m, &s).is_err());
    let mut enrollment: serde_json::Value = serde_json::from_slice(&e).unwrap();
    enrollment["developmentSigningKeyId"] = "a".repeat(64).into();
    assert!(authenticate(&serde_json::to_vec(&enrollment).unwrap(), &m, &s).is_err());
    let relabel = String::from_utf8(m).unwrap().replace("LocalDev", "Stable");
    assert!(authenticate(&e, relabel.as_bytes(), &s).is_err());
}
#[test]
fn signature_rejects_noncanonical_manifest_duplicate_keys_and_wrong_installation() {
    let v = vector();
    let e = bytes(&v, "enrollment");
    let m = bytes(&v, "manifest");
    let s = bytes(&v, "signature");
    let mut spaced = m.clone();
    spaced.push(b' ');
    assert!(authenticate(&e, &spaced, &s).is_err());
    let duplicate = String::from_utf8(m)
        .unwrap()
        .replacen('{', "{\"schemaVersion\":1,", 1);
    assert!(authenticate(&e, duplicate.as_bytes(), &s).is_err());
    let other = String::from_utf8(e).unwrap().replace(
        "11111111-1111-1111-1111-111111111111",
        "22222222-2222-2222-2222-222222222222",
    );
    assert!(authenticate(other.as_bytes(), &bytes(&v, "manifest"), &s).is_err());
}
#[test]
fn native_artifact_csharp_record_matches_actual_pe_metadata_and_role() {
    let v = vector();
    let r = authenticate(
        &bytes(&v, "enrollment"),
        &bytes(&v, "manifest"),
        &bytes(&v, "signature"),
    )
    .unwrap();
    assert_eq!(
        inspect_artifact(
            &bytes(&v, "serviceRecord"),
            &bytes(&v, "serviceImage"),
            "service",
            &r
        )
        .unwrap(),
        format!("{}-service", "b".repeat(64))
    );
    assert!(
        inspect_artifact(
            &bytes(&v, "serviceRecord"),
            &bytes(&v, "serviceImage"),
            "agent",
            &r
        )
        .is_err()
    );
    inspect_artifact(
        &bytes(&v, "agentRecord"),
        &bytes(&v, "agentImage"),
        "agent",
        &r,
    )
    .unwrap();
}
#[test]
fn native_artifact_rejects_image_metadata_target_feature_or_build_mutation() {
    let v = vector();
    let r = authenticate(
        &bytes(&v, "enrollment"),
        &bytes(&v, "manifest"),
        &bytes(&v, "signature"),
    )
    .unwrap();
    let a = bytes(&v, "serviceRecord");
    let image = bytes(&v, "serviceImage");
    let mut changed = image.clone();
    changed[0x400] ^= 1;
    assert!(inspect_artifact(&a, &changed, "service", &r).is_err());
    for pair in [
        ("x86_64-pc-windows-msvc", "aarch64-pc-windows-msvc"),
        ("installed-service-candidate", "not-installed"),
        ("rustc 1.99.1", "other 1.99.1"),
    ] {
        let bad = String::from_utf8(a.clone())
            .unwrap()
            .replace(pair.0, pair.1);
        assert!(inspect_artifact(bad.as_bytes(), &image, "service", &r).is_err());
    }
}
#[test]
fn canonical_package_binds_both_roles_closure_and_distinct_artifacts() {
    let v = vector();
    let package = bytes(&v, "package");
    let f = lines(&package, "OpenBlade.NativePackage.v1", 11).unwrap();
    let e = Epoch::parse(&bytes(&v, "epoch")).unwrap();
    assert_eq!(digest(&package), e.package);
    assert_eq!(decode64(f[8]).unwrap(), bytes(&v, "serviceRecord"));
    assert_eq!(decode64(f[9]).unwrap(), bytes(&v, "agentRecord"));
    assert_eq!(f[10], "two-executables-system-runtime-imports-v1");
    let mut switched = f.clone();
    switched.swap(8, 9);
    assert_ne!(
        digest(&encode("OpenBlade.NativePackage.v1", &switched)),
        e.package
    );
}
#[test]
fn base64_rejects_noncanonical_padding_and_trailing_data() {
    for b in [
        "", "A===", "AA=A", "AAAA=", "AB==", "AAB=", "AA==AAAA", "AA\n=",
    ] {
        assert!(decode64(b).is_err(), "{b}");
    }
    assert_eq!(decode64("AA==").unwrap(), [0]);
    assert_eq!(decode64("AAA=").unwrap(), [0, 0]);
}
#[test]
fn fresh_rollback_epoch_cannot_rebind_old_origin() {
    let v = vector();
    let e = Epoch::parse(&bytes(&v, "epoch")).unwrap();
    let mut rollback = e.clone();
    rollback.id = "9".repeat(32);
    rollback.ordinal += 1;
    rollback.predecessor = digest(&bytes(&v, "epoch"));
    assert!(!e.same_origin(&rollback));
    assert_eq!(e.package, rollback.package);
}

#[test]
fn public_projection_matches_csharp_canonical_bytes_and_authenticates_original_signature() {
    let p: serde_json::Value =
        serde_json::from_str(include_str!("../../tests/data/native-verification-v1.json")).unwrap();
    let b = bytes(&p, "projection");
    let v = vector();
    let fields = lines(&b, "OpenBlade.NativeVerification.v1", 6).unwrap();
    assert_eq!(b, encode("OpenBlade.NativeVerification.v1", &fields));
    assert_eq!(digest(&b), p["sha256"].as_str().unwrap());
    assert!(
        authenticate_bound_projection(
            &b,
            p["sha256"].as_str().unwrap(),
            &bytes(&v, "manifest"),
            &bytes(&v, "signature")
        )
        .is_ok()
    );
    let text = String::from_utf8(b.clone()).unwrap();
    for bad in [
        text.replace("LocalDev", "Stable"),
        text.replace(fields[1], "22222222-2222-2222-2222-222222222222"),
        text.replace(fields[2], "wrong-policy"),
        text.replace(fields[3], &"a".repeat(64)),
        text.replace(fields[5], &"a".repeat(64)),
        format!("{text}\n"),
    ] {
        assert!(
            authenticate_bound_projection(
                bad.as_bytes(),
                p["sha256"].as_str().unwrap(),
                &bytes(&v, "manifest"),
                &bytes(&v, "signature")
            )
            .is_err()
        );
    }
    assert!(
        authenticate_projection(&b, &bytes(&v, "manifest"), &bytes(&v, "wrongDomain")).is_err()
    );
}
#[test]
fn actual_pe_obid_reader_does_not_accept_a_marker_elsewhere_or_wrong_geometry() {
    let v = vector();
    let mut image = bytes(&v, "serviceImage");
    let record = compiled_record(&image).unwrap().to_vec();
    assert!(
        record.starts_with(b"OpenBlade.NativeArtifact.v1\n")
            || record.starts_with(b"OpenBlade.NativeArtifact.v2\n")
    );
    image[0] ^= 1; // full PE validation remains inspect_artifact's job
    let r = authenticate(
        &bytes(&v, "enrollment"),
        &bytes(&v, "manifest"),
        &bytes(&v, "signature"),
    )
    .unwrap();
    assert!(inspect_artifact(&bytes(&v, "serviceRecord"), &image, "service", &r).is_err());
}

#[test]
fn real_commit_frontier_before_current_is_bounded_same_attempt_promotion() {
    let v = vector();
    let committed = bytes(&v, "epoch");
    let text = String::from_utf8(committed.clone()).unwrap();
    let original = Epoch::parse(&committed).unwrap();
    let provisional = text
        .replace("Committed", "Provisional")
        .replace(
            &format!("\n{}\n", original.readiness),
            &format!("\n{ZERO}\n"),
        )
        .into_bytes();
    let epoch = Epoch::parse(&provisional).unwrap();
    let now = std::time::Instant::now();
    let mut progress = Promotion::default();
    assert!(
        progress
            .check(&epoch, &provisional, &provisional, &provisional, None, now)
            .unwrap()
            .is_none()
    );
    assert!(
        progress
            .check(
                &epoch,
                &provisional,
                &provisional,
                &committed,
                Some(&committed),
                now
            )
            .unwrap()
            .is_none()
    );
    assert!(
        progress
            .check(&epoch, &provisional, &provisional, &provisional, None, now)
            .is_err()
    ); // cannot roll frontier back
    let promoted = progress
        .check(
            &epoch,
            &provisional,
            &committed,
            &committed,
            Some(&committed),
            now,
        )
        .unwrap()
        .unwrap();
    assert_eq!(promoted, original);
    assert!(
        progress
            .check(&promoted, &committed, &committed, &committed, None, now)
            .unwrap()
            .is_none()
    );
    let mut expired = Promotion::default();
    expired
        .check(
            &epoch,
            &provisional,
            &provisional,
            &committed,
            Some(&committed),
            now,
        )
        .unwrap();
    assert!(
        expired
            .check(
                &epoch,
                &provisional,
                &provisional,
                &committed,
                Some(&committed),
                now + PROMOTION_BUDGET
            )
            .is_err()
    );
    let other = text.replace(&original.id, &"8".repeat(32)).into_bytes();
    assert!(
        Promotion::default()
            .check(
                &epoch,
                &provisional,
                &provisional,
                &other,
                Some(&other),
                now
            )
            .is_err()
    );
    assert!(
        Promotion::default()
            .check(&epoch, &provisional, &provisional, &committed, None, now)
            .is_err()
    );
}

#[test]
fn shared_origin_correction_descriptors_and_owner_paths() {
    use std::os::windows::ffi::OsStrExt;
    let v: serde_json::Value = serde_json::from_slice(include_bytes!(
        "../../tests/data/native-origin-corrections-v1.json"
    ))
    .unwrap();
    for case in v["descriptors"].as_array().unwrap() {
        let descriptor = decode64(case["descriptor"].as_str().unwrap()).unwrap();
        assert_eq!(
            paths::accepts_descriptor(&descriptor, Policy::PROGRAM_DATA),
            Some(case["programData"].as_bool().unwrap())
        );
        assert_eq!(
            paths::accepts_descriptor(&descriptor, Policy::ANCESTOR),
            Some(false)
        );
        assert_eq!(
            paths::accepts_descriptor(&descriptor, Policy::Directory { extra_rights: 0 }),
            Some(false)
        );
        assert_eq!(
            paths::accepts_descriptor(&descriptor, Policy::ROOT),
            Some(false)
        );
        assert_eq!(
            paths::accepts_descriptor(&descriptor, Policy::Record),
            Some(false)
        );
        assert_eq!(paths::accepts_public_native_root(&descriptor), Some(false));
    }
    for case in v["owners"].as_array().unwrap() {
        let bytes = decode64_bounded(case["record"].as_str().unwrap(), 32768).unwrap();
        let owner = StartupOwner::parse(&bytes).unwrap();
        let expected: Vec<u16> = case["units"]
            .as_array()
            .unwrap()
            .iter()
            .map(|u| u.as_u64().unwrap() as u16)
            .collect();
        assert_eq!(
            owner.image.as_os_str().encode_wide().collect::<Vec<_>>(),
            expected
        );
        assert_eq!((owner.pid, owner.created, owner.session), (17, 1, 0));
    }
    for case in v["malformedOwners"].as_array().unwrap() {
        let bytes = decode64_bounded(case.as_str().unwrap(), 32768).unwrap();
        assert!(StartupOwner::parse(&bytes).is_err());
    }
    let legacy = encode(
        "OpenBlade.NativeStartupOwner.v1",
        &[
            &"a".repeat(32),
            &"b".repeat(32),
            "17",
            "1",
            "0",
            r"C:\ASCII\Setup.exe",
        ],
    );
    assert_eq!(
        StartupOwner::parse(&legacy).unwrap().image,
        PathBuf::from(r"C:\ASCII\Setup.exe")
    );
}
#[test]
fn owner_path_rejects_noncanonical_odd_nul_and_excessive_encoding() {
    for value in [
        "",
        "QQ==",
        "QQB=",
        "QQA= ",
        "AA A=",
        "AAAA",
        "!!!!",
        &"AAAA".repeat(2731),
    ] {
        let record = encode(
            "OpenBlade.NativeStartupOwner.v2",
            &[&"a".repeat(32), &"b".repeat(32), "17", "1", "0", value],
        );
        assert!(StartupOwner::parse(&record).is_err(), "accepted {value:?}");
    }
}

#[test]
fn private_custody_descriptor_parity_preserves_public_record_access() {
    let v: serde_json::Value = serde_json::from_slice(include_bytes!(
        "../../tests/data/native-recovery-lifetime-v1.json"
    ))
    .unwrap();
    assert_eq!(
        v["maximumAttempts"].as_u64().unwrap() as usize,
        MAXIMUM_ATTEMPTS
    );
    for case in v["descriptors"].as_array().unwrap() {
        let bytes = decode64(case["bytes"].as_str().unwrap()).unwrap();
        assert_eq!(
            paths::accepts_descriptor(&bytes, Policy::PrivateCustody),
            Some(case["privateCustody"].as_bool().unwrap()),
            "{}",
            case["name"]
        );
        // The stricter custody policy does not change ordinary public evidence.
        assert_eq!(
            paths::accepts_descriptor(&bytes, Policy::Record),
            Some(case["name"] != "inheriting")
        );
    }
}

#[test]
fn shared_attempt_budget_reserves_last_drain_and_preserves_exhausted_or_malformed_history() {
    use std::{collections::BTreeMap, os::windows::ffi::OsStrExt};
    let epoch = Epoch::parse(&bytes(&vector(), "epoch")).unwrap();
    let origin = b"private injected origin";
    let image = PathBuf::from(r"C:\Fixture\app\OpenBlade.Service.exe");
    let path: Vec<u8> = image
        .as_os_str()
        .encode_wide()
        .flat_map(u16::to_le_bytes)
        .collect();
    let mut records = BTreeMap::new();
    records.insert(
        "custody.lock".to_owned(),
        encode("OpenBlade.NativeServiceCustody.v1", &[&epoch.id]),
    );
    let add_launch = |records: &mut BTreeMap<String, Vec<u8>>, pid: usize| {
        let p = pid.to_string();
        let launch = encode(
            "OpenBlade.NativeServiceLaunch.v1",
            &[
                &epoch.id,
                &digest(origin),
                &epoch.request,
                &epoch.package,
                &epoch.service,
                &p,
                &p,
                "0",
                "LocalSystem-primary-session0",
                &encode64(&path),
            ],
        );
        records.insert(format!("launch-{p}-{p}.bin"), launch.clone());
        launch
    };
    let drain = |records: &mut BTreeMap<String, Vec<u8>>, launch: &[u8], pid: usize| {
        publish_metadata_drain(
            &epoch.id,
            &digest(launch),
            pid as u32,
            pid as u64,
            |name, bytes| {
                assert!(records.insert(name.to_owned(), bytes.to_vec()).is_none());
                Ok(())
            },
        )
        .unwrap();
    };
    let check = |records: &BTreeMap<String, Vec<u8>>| {
        require_launch_budget(
            records.keys().cloned().collect(),
            |name| Ok(records[name].clone()),
            &epoch,
            origin,
            &image,
            false,
        )
    };
    for pid in 1..MAXIMUM_ATTEMPTS {
        let launch = add_launch(&mut records, pid);
        drain(&mut records, &launch, pid);
    }
    check(&records).unwrap();
    let last = add_launch(&mut records, MAXIMUM_ATTEMPTS);
    let error = check(&records).unwrap_err();
    assert_eq!(
        error.get_ref().unwrap().downcast_ref::<AttemptRefusal>(),
        Some(&AttemptRefusal::BudgetExhausted)
    );
    // Actual drain publication seam has no launch-budget check.
    drain(&mut records, &last, MAXIMUM_ATTEMPTS);
    assert_eq!(records.len(), MAXIMUM_ATTEMPT_ENTRIES);
    let before = records.clone();
    assert_eq!(
        check(&records)
            .unwrap_err()
            .get_ref()
            .unwrap()
            .downcast_ref::<AttemptRefusal>(),
        Some(&AttemptRefusal::BudgetExhausted)
    );
    assert_eq!(records, before);
    records.insert(
        "launch-4097-4097.bin".into(),
        b"preserved over-limit data".to_vec(),
    );
    let oversized = records.clone();
    assert_eq!(
        check(&records)
            .unwrap_err()
            .get_ref()
            .unwrap()
            .downcast_ref::<AttemptRefusal>(),
        Some(&AttemptRefusal::HistoryOverLimit)
    );
    assert_eq!(records, oversized);
    let mut malformed = before;
    malformed.insert("launch-1-1.bin".into(), b"malformed".to_vec());
    assert_eq!(
        check(&malformed)
            .unwrap_err()
            .get_ref()
            .unwrap()
            .downcast_ref::<AttemptRefusal>(),
        Some(&AttemptRefusal::MalformedHistory)
    );
}

#[test]
fn shared_v2_capabilities_preserve_combined_agent_features_and_reject_forgery() {
    let data: serde_json::Value =
        serde_json::from_str(include_str!("../../tests/data/native-capabilities-v2.json")).unwrap();
    let v = vector();
    let release = authenticate(
        &bytes(&v, "enrollment"),
        &bytes(&v, "manifest"),
        &bytes(&v, "signature"),
    )
    .unwrap();
    for case in data["cases"].as_array().unwrap() {
        let image = bytes(case, "image");
        let record = bytes(case, "record");
        let role = case["role"].as_str().unwrap();
        let result = inspect_artifact(&record, &image, role, &release);
        assert_eq!(
            result.is_ok(),
            case["accepted"].as_bool().unwrap(),
            "{}",
            case["name"]
        );
        if result.is_ok() {
            let f = lines(&record, "OpenBlade.NativeArtifactRecord.v2", 11).unwrap();
            assert_eq!(record, encode("OpenBlade.NativeArtifactRecord.v2", &f));
            if role == "agent" {
                if case["name"] == "temporary-combined-agent" {
                    assert!(f[4].contains("service-temporary-charge-v1"));
                } else {
                    assert!(f[4].contains("service-metadata-only-v1"));
                }
                assert_eq!(f[10], "none");
            }
            let mut tampered = f.clone();
            tampered[10] = "unknown";
            assert!(
                inspect_artifact(
                    &encode("OpenBlade.NativeArtifactRecord.v2", &tampered),
                    &image,
                    role,
                    &release
                )
                .is_err()
            );
        }
    }
}

#[test]
fn a_profile_serving_service_is_not_admitted_as_any_composition() {
    // The features and the capability build.rs gives a build with
    // service-profiles-v1. Packaging has not admitted that capability, so the
    // reader must refuse the image rather than take it for the
    // temporary-charge composition it extends.
    let features = "controller-admission-compile-check,firmware-performance-compile-check,hid-identity-compile-check,installed-service-candidate,lifecycle-evidence-compile-check,service-battery-policy-v1,service-fan-mutation-v2,service-hardware-compile-check,service-mailbox-observation-v1,service-profiles-v1,service-temporary-charge-v1,service-thermal-mutation-v1,startup-custody-v1";
    // Refused by name: a record that keeps the feature but claims only the
    // temporary-charge capabilities, in either role or schema, is no better.
    for role in ["service", "agent"] {
        for claimed in [
            "private-service-custody-v1,service-mailbox-observation-v1,service-thermal-mutation-v1,service-fan-mutation-v2,service-battery-policy-v1,service-temporary-charge-v1",
            "none",
        ] {
            assert!(capabilities(2, role, features, claimed).is_err());
            assert!(capabilities(1, role, features, claimed).is_err());
        }
    }
    let temporary = "private-service-custody-v1,service-mailbox-observation-v1,service-thermal-mutation-v1,service-fan-mutation-v2,service-battery-policy-v1,service-temporary-charge-v1";
    let profiles = format!("{temporary},service-profiles-v1");
    assert!(capabilities(2, "service", features, &profiles).is_err());
    assert!(capabilities(1, "service", features, &profiles).is_err());
    // The refusal is the profile capability: without it this is the admitted
    // temporary-charge composition, which cannot claim it either.
    let extended = features.replace("service-profiles-v1,", "");
    assert!(capabilities(2, "service", &extended, temporary).is_ok());
    assert!(capabilities(2, "service", &extended, &profiles).is_err());
}

#[test]
fn a_keyboard_driver_mode_reader_is_not_admitted_by_feature_or_marker() {
    // The features and the marker build.rs gives each admitted Service
    // composition built with service-keyboard-driver-mode-read-v1. Packaging
    // has not admitted the 00/84 read, so the reader refuses the image by name
    // rather than take it for the composition it extends.
    const MARKER: &str = "service-keyboard-driver-mode-read-v1";
    let observation = "firmware-performance-compile-check,hid-identity-compile-check,installed-service-candidate,lifecycle-evidence-compile-check,service-hardware-compile-check,service-mailbox-observation-v1,startup-custody-v1";
    let mutation = "controller-admission-compile-check,firmware-performance-compile-check,hid-identity-compile-check,installed-service-candidate,lifecycle-evidence-compile-check,service-hardware-compile-check,service-mailbox-observation-v1,service-thermal-mutation-v1,startup-custody-v1";
    let temporary = "controller-admission-compile-check,firmware-performance-compile-check,hid-identity-compile-check,installed-service-candidate,lifecycle-evidence-compile-check,service-battery-policy-v1,service-fan-mutation-v2,service-hardware-compile-check,service-mailbox-observation-v1,service-temporary-charge-v1,service-thermal-mutation-v1,startup-custody-v1";
    for (features, admitted) in [
        (
            observation,
            "private-service-custody-v1,service-mailbox-observation-v1",
        ),
        (
            mutation,
            "private-service-custody-v1,service-mailbox-observation-v1,service-thermal-mutation-v1",
        ),
        (
            temporary,
            "private-service-custody-v1,service-mailbox-observation-v1,service-thermal-mutation-v1,service-fan-mutation-v2,service-battery-policy-v1,service-temporary-charge-v1",
        ),
    ] {
        // Without the feature this is an admitted composition.
        assert!(capabilities(2, "service", features, admitted).is_ok());
        let built = features.replace(
            "service-mailbox-observation-v1,",
            "service-keyboard-driver-mode-read-v1,service-mailbox-observation-v1,",
        );
        let marked = format!("{admitted},{MARKER}");
        // The build's own record, and one that keeps the feature but claims
        // only the composition it extends, in either role or schema.
        for claimed in [marked.as_str(), admitted, "none"] {
            for role in ["service", "agent"] {
                assert!(capabilities(2, role, &built, claimed).is_err());
                assert!(capabilities(1, role, &built, claimed).is_err());
            }
        }
        // A forged marker without the feature is no better.
        assert!(capabilities(2, "service", features, &marked).is_err());
    }
}

#[test]
fn a_keyboard_driver_mode_owner_is_not_admitted_by_feature_or_marker() {
    // Driver-mode ownership writes 00/04. The reader refuses it by its own
    // name, even in a record that drops the read feature it implies and
    // claims only the admitted composition it would extend.
    const OWNER: &str = "service-keyboard-driver-mode-v1";
    let temporary = "controller-admission-compile-check,firmware-performance-compile-check,hid-identity-compile-check,installed-service-candidate,lifecycle-evidence-compile-check,service-battery-policy-v1,service-fan-mutation-v2,service-hardware-compile-check,service-mailbox-observation-v1,service-temporary-charge-v1,service-thermal-mutation-v1,startup-custody-v1";
    let admitted = "private-service-custody-v1,service-mailbox-observation-v1,service-thermal-mutation-v1,service-fan-mutation-v2,service-battery-policy-v1,service-temporary-charge-v1";
    assert!(capabilities(2, "service", temporary, admitted).is_ok());
    let forged = temporary.replace(
        "service-mailbox-observation-v1,",
        &format!("{OWNER},service-mailbox-observation-v1,"),
    );
    let built = temporary.replace(
        "service-mailbox-observation-v1,",
        &format!("{OWNER},service-keyboard-driver-mode-read-v1,service-mailbox-observation-v1,"),
    );
    let marked = format!("{admitted},service-keyboard-driver-mode-read-v1,{OWNER}");
    for features in [forged.as_str(), built.as_str()] {
        for claimed in [marked.as_str(), admitted, "none"] {
            for role in ["service", "agent"] {
                assert!(capabilities(2, role, features, claimed).is_err());
                assert!(capabilities(1, role, features, claimed).is_err());
            }
        }
    }
    // A forged marker without the feature is no better.
    assert!(capabilities(2, "service", temporary, &format!("{admitted},{OWNER}")).is_err());
}

#[test]
fn a_live_reading_service_is_not_admitted_by_feature_or_marker() {
    // The features and the marker build.rs gives each admitted Service
    // composition built with service-live-readings-v1. Packaging has not
    // admitted the 0D/88 read, so the reader refuses the image by name rather
    // than take it for the composition it extends.
    const MARKER: &str = "service-live-readings-v1";
    let observation = "firmware-performance-compile-check,hid-identity-compile-check,installed-service-candidate,lifecycle-evidence-compile-check,service-hardware-compile-check,service-mailbox-observation-v1,startup-custody-v1";
    let temporary = "controller-admission-compile-check,firmware-performance-compile-check,hid-identity-compile-check,installed-service-candidate,lifecycle-evidence-compile-check,service-battery-policy-v1,service-fan-mutation-v2,service-hardware-compile-check,service-mailbox-observation-v1,service-temporary-charge-v1,service-thermal-mutation-v1,startup-custody-v1";
    for (features, admitted) in [
        (
            observation,
            "private-service-custody-v1,service-mailbox-observation-v1",
        ),
        (
            temporary,
            "private-service-custody-v1,service-mailbox-observation-v1,service-thermal-mutation-v1,service-fan-mutation-v2,service-battery-policy-v1,service-temporary-charge-v1",
        ),
    ] {
        // Without the feature this is an admitted composition.
        assert!(capabilities(2, "service", features, admitted).is_ok());
        let built = features.replace(
            "service-mailbox-observation-v1,",
            "service-live-readings-v1,service-mailbox-observation-v1,",
        );
        let marked = format!("{admitted},{MARKER}");
        // The build's own record, and one that keeps the feature but claims
        // only the composition it extends, in either role or schema.
        for claimed in [marked.as_str(), admitted, "none"] {
            for role in ["service", "agent"] {
                assert!(capabilities(2, role, &built, claimed).is_err());
                assert!(capabilities(1, role, &built, claimed).is_err());
            }
        }
        // A forged marker without the feature is no better.
        assert!(capabilities(2, "service", features, &marked).is_err());
    }
}

#[test]
fn a_diagnostic_export_service_is_not_admitted_by_feature_or_marker() {
    // The features and the marker build.rs gives each admitted Service
    // composition built with service-diagnostics-export-v1. Packaging has not
    // admitted exportDiagnostics, so the reader refuses the image by name
    // rather than take it for the composition it extends.
    const MARKER: &str = "service-diagnostics-export-v1";
    let observation = "firmware-performance-compile-check,hid-identity-compile-check,installed-service-candidate,lifecycle-evidence-compile-check,service-hardware-compile-check,service-mailbox-observation-v1,startup-custody-v1";
    let temporary = "controller-admission-compile-check,firmware-performance-compile-check,hid-identity-compile-check,installed-service-candidate,lifecycle-evidence-compile-check,service-battery-policy-v1,service-fan-mutation-v2,service-hardware-compile-check,service-mailbox-observation-v1,service-temporary-charge-v1,service-thermal-mutation-v1,startup-custody-v1";
    for (features, admitted) in [
        (
            observation,
            "private-service-custody-v1,service-mailbox-observation-v1",
        ),
        (
            temporary,
            "private-service-custody-v1,service-mailbox-observation-v1,service-thermal-mutation-v1,service-fan-mutation-v2,service-battery-policy-v1,service-temporary-charge-v1",
        ),
    ] {
        // Without the feature this is an admitted composition.
        assert!(capabilities(2, "service", features, admitted).is_ok());
        let mut built: Vec<&str> = features.split(',').chain([MARKER]).collect();
        built.sort_unstable();
        let built = built.join(",");
        let marked = format!("{admitted},{MARKER}");
        // The build's own record, and one that keeps the feature but claims
        // only the composition it extends, in either role or schema.
        for claimed in [marked.as_str(), admitted, "none"] {
            for role in ["service", "agent"] {
                assert!(capabilities(2, role, &built, claimed).is_err());
                assert!(capabilities(1, role, &built, claimed).is_err());
            }
        }
        // A forged marker without the feature is no better.
        assert!(capabilities(2, "service", features, &marked).is_err());
    }
}

#[test]
fn a_performance_shortcut_service_is_not_admitted_by_feature_or_marker() {
    // The features and the marker build.rs gives the admitted temporary-charge
    // composition built with service-performance-shortcut-v1. Packaging has
    // not admitted the shortcut or its preview, so the reader refuses the
    // image by name rather than take it for the composition it extends.
    const MARKER: &str = "service-performance-shortcut-v1";
    let features = "controller-admission-compile-check,firmware-performance-compile-check,hid-identity-compile-check,installed-service-candidate,lifecycle-evidence-compile-check,service-battery-policy-v1,service-fan-mutation-v2,service-hardware-compile-check,service-mailbox-observation-v1,service-temporary-charge-v1,service-thermal-mutation-v1,startup-custody-v1";
    let admitted = "private-service-custody-v1,service-mailbox-observation-v1,service-thermal-mutation-v1,service-fan-mutation-v2,service-battery-policy-v1,service-temporary-charge-v1";
    // Without the feature this is an admitted composition.
    assert!(capabilities(2, "service", features, admitted).is_ok());
    let mut built: Vec<&str> = features.split(',').chain([MARKER]).collect();
    built.sort_unstable();
    let built = built.join(",");
    let marked = format!("{admitted},{MARKER}");
    // The build's own record, and one that keeps the feature but claims only
    // the composition it extends, in either role or schema.
    for claimed in [marked.as_str(), admitted, "none"] {
        for role in ["service", "agent"] {
            assert!(capabilities(2, role, &built, claimed).is_err());
            assert!(capabilities(1, role, &built, claimed).is_err());
        }
    }
    // A forged marker without the feature is no better.
    assert!(capabilities(2, "service", features, &marked).is_err());
}

#[test]
fn a_hyperboost_exit_service_is_not_admitted_by_feature_or_marker() {
    // The features and the marker build.rs gives the admitted temporary-charge
    // composition built with service-thermal-hyperboost-exit-v1. Packaging has
    // not admitted the restore pair in HyperBoost, so the reader refuses the
    // image by name rather than take it for the composition it extends.
    const MARKER: &str = "service-thermal-hyperboost-exit-v1";
    let features = "controller-admission-compile-check,firmware-performance-compile-check,hid-identity-compile-check,installed-service-candidate,lifecycle-evidence-compile-check,service-battery-policy-v1,service-fan-mutation-v2,service-hardware-compile-check,service-mailbox-observation-v1,service-temporary-charge-v1,service-thermal-mutation-v1,startup-custody-v1";
    let admitted = "private-service-custody-v1,service-mailbox-observation-v1,service-thermal-mutation-v1,service-fan-mutation-v2,service-battery-policy-v1,service-temporary-charge-v1";
    assert!(capabilities(2, "service", features, admitted).is_ok());
    let mut built: Vec<&str> = features.split(',').chain([MARKER]).collect();
    built.sort_unstable();
    let built = built.join(",");
    let marked = format!("{admitted},{MARKER}");
    for claimed in [marked.as_str(), admitted, "none"] {
        for role in ["service", "agent"] {
            assert!(capabilities(2, role, &built, claimed).is_err());
            assert!(capabilities(1, role, &built, claimed).is_err());
        }
    }
    // A forged marker without the feature is no better.
    assert!(capabilities(2, "service", features, &marked).is_err());
}

#[test]
fn an_owned_hyperboost_service_is_not_admitted_by_feature_or_marker() {
    // The features and markers build.rs gives the admitted temporary-charge
    // composition built with service-hyperboost-v1, which implies the exit.
    // Packaging has not admitted the enable pair, so the reader refuses the
    // image by name, and refuses either marker alone.
    const EXIT: &str = "service-thermal-hyperboost-exit-v1";
    const MARKER: &str = "service-hyperboost-v1";
    let features = "controller-admission-compile-check,firmware-performance-compile-check,hid-identity-compile-check,installed-service-candidate,lifecycle-evidence-compile-check,service-battery-policy-v1,service-fan-mutation-v2,service-hardware-compile-check,service-mailbox-observation-v1,service-temporary-charge-v1,service-thermal-mutation-v1,startup-custody-v1";
    let admitted = "private-service-custody-v1,service-mailbox-observation-v1,service-thermal-mutation-v1,service-fan-mutation-v2,service-battery-policy-v1,service-temporary-charge-v1";
    assert!(capabilities(2, "service", features, admitted).is_ok());
    let mut built: Vec<&str> = features.split(',').chain([MARKER, EXIT]).collect();
    built.sort_unstable();
    let built = built.join(",");
    let marked = format!("{admitted},{EXIT},{MARKER}");
    for claimed in [marked.as_str(), admitted, "none"] {
        for role in ["service", "agent"] {
            assert!(capabilities(2, role, &built, claimed).is_err());
            assert!(capabilities(1, role, &built, claimed).is_err());
        }
    }
    // The owned feature alone, or a forged marker without it, is no better.
    let mut owned: Vec<&str> = features.split(',').chain([MARKER]).collect();
    owned.sort_unstable();
    assert!(capabilities(2, "service", &owned.join(","), admitted).is_err());
    assert!(capabilities(2, "service", features, &format!("{admitted},{MARKER}")).is_err());
}

#[test]
fn a_lighting_service_is_not_admitted_by_feature_or_marker() {
    // The features and marker build.rs gives the admitted temporary-charge
    // composition built with service-lighting-v1. Packaging has not admitted
    // the lighting setters, so the reader refuses the image by name, and
    // refuses the feature or a forged marker alone.
    const MARKER: &str = "service-lighting-v1";
    let features = "controller-admission-compile-check,firmware-performance-compile-check,hid-identity-compile-check,installed-service-candidate,lifecycle-evidence-compile-check,service-battery-policy-v1,service-fan-mutation-v2,service-hardware-compile-check,service-mailbox-observation-v1,service-temporary-charge-v1,service-thermal-mutation-v1,startup-custody-v1";
    let admitted = "private-service-custody-v1,service-mailbox-observation-v1,service-thermal-mutation-v1,service-fan-mutation-v2,service-battery-policy-v1,service-temporary-charge-v1";
    assert!(capabilities(2, "service", features, admitted).is_ok());
    let mut built: Vec<&str> = features.split(',').chain([MARKER]).collect();
    built.sort_unstable();
    let built = built.join(",");
    let marked = format!("{admitted},{MARKER}");
    for claimed in [marked.as_str(), admitted, "none"] {
        for role in ["service", "agent"] {
            assert!(capabilities(2, role, &built, claimed).is_err());
            assert!(capabilities(1, role, &built, claimed).is_err());
        }
    }
    assert!(capabilities(2, "service", features, &marked).is_err());
}

#[test]
fn a_display_brightness_input_service_is_not_admitted_by_feature_or_marker() {
    // The features and marker build.rs gives the admitted temporary-charge
    // composition built with service-brightness-hid-usage-v1. Its IOCTLs have
    // no readback and no capture evidence, so the reader refuses the image by
    // name, and refuses the marker alone.
    const MARKER: &str = "service-brightness-hid-usage-v1";
    let features = "controller-admission-compile-check,firmware-performance-compile-check,hid-identity-compile-check,installed-service-candidate,lifecycle-evidence-compile-check,service-battery-policy-v1,service-fan-mutation-v2,service-hardware-compile-check,service-mailbox-observation-v1,service-temporary-charge-v1,service-thermal-mutation-v1,startup-custody-v1";
    let admitted = "private-service-custody-v1,service-mailbox-observation-v1,service-thermal-mutation-v1,service-fan-mutation-v2,service-battery-policy-v1,service-temporary-charge-v1";
    assert!(capabilities(2, "service", features, admitted).is_ok());
    let mut built: Vec<&str> = features
        .split(',')
        .chain(["driver-api-trust", MARKER])
        .collect();
    built.sort_unstable();
    let built = built.join(",");
    let marked = format!("{admitted},{MARKER}");
    for claimed in [marked.as_str(), admitted, "none"] {
        for role in ["service", "agent"] {
            assert!(capabilities(2, role, &built, claimed).is_err());
            assert!(capabilities(1, role, &built, claimed).is_err());
        }
    }
    // A forged marker without the feature is no better.
    assert!(capabilities(2, "service", features, &marked).is_err());
}

#[test]
fn a_profile_trigger_service_is_not_admitted_by_feature_or_marker() {
    // The features and the capability build.rs gives a build with
    // service-profile-triggers-v1. Packaging has not admitted automatic
    // reconciliation, so the reader refuses it by name in either role or
    // schema, whatever else the record claims.
    let profiles = "controller-admission-compile-check,firmware-performance-compile-check,hid-identity-compile-check,installed-service-candidate,lifecycle-evidence-compile-check,service-battery-policy-v1,service-fan-mutation-v2,service-hardware-compile-check,service-mailbox-observation-v1,service-profiles-v1,service-temporary-charge-v1,service-thermal-mutation-v1,startup-custody-v1";
    let features = profiles.replace(
        "service-profiles-v1,",
        "service-profile-triggers-v1,service-profiles-v1,",
    );
    let temporary = "private-service-custody-v1,service-mailbox-observation-v1,service-thermal-mutation-v1,service-fan-mutation-v2,service-battery-policy-v1,service-temporary-charge-v1";
    let marked = format!("{temporary},service-profiles-v1,service-profile-triggers-v1");
    for role in ["service", "agent"] {
        for claimed in [marked.as_str(), temporary, "none"] {
            assert!(capabilities(2, role, &features, claimed).is_err());
            assert!(capabilities(1, role, &features, claimed).is_err());
        }
    }
    // Neither the feature nor the marker alone is admitted beside the
    // otherwise admitted temporary-charge composition.
    let extended = profiles.replace("service-profiles-v1,", "");
    assert!(capabilities(2, "service", &extended, temporary).is_ok());
    let feature_only = extended.replace(
        "service-temporary-charge-v1,",
        "service-profile-triggers-v1,service-temporary-charge-v1,",
    );
    assert!(capabilities(2, "service", &feature_only, temporary).is_err());
    let marker_only = format!("{temporary},service-profile-triggers-v1");
    assert!(capabilities(2, "service", &extended, &marker_only).is_err());
    assert!(capabilities(1, "service", &extended, &marker_only).is_err());
}

#[test]
fn an_application_reporter_agent_is_not_admitted_by_feature_or_marker() {
    // The feature and the marker build.rs gives a build with
    // agent-application-reporter-v1. Packaging has not admitted either, so the
    // reader refuses the image by name whatever else the record claims.
    let features = "agent-application-reporter-v1,native-agent-entry-compile-check";
    for claimed in [
        "session-application-reporter-v1",
        "agent-application-reporter-v1",
        "none",
        "agent-battery-report-v1,session-application-reporter-v1",
    ] {
        assert!(capabilities(2, "agent", features, claimed).is_err());
        assert!(capabilities(1, "agent", features, claimed).is_err());
        assert!(capabilities(2, "service", features, claimed).is_err());
    }
    // A forged marker without the feature is no better.
    assert!(
        capabilities(
            2,
            "agent",
            "native-agent-entry-compile-check",
            "session-application-reporter-v1"
        )
        .is_err()
    );
    // The reporter beside an admitted contract is refused; the contract alone is not.
    let combined =
        "agent-application-reporter-v1,agent-battery-report-v1,native-agent-entry-compile-check";
    assert!(capabilities(2, "agent", combined, "agent-battery-report-v1").is_err());
    assert!(
        capabilities(
            2,
            "agent",
            "agent-battery-report-v1,native-agent-entry-compile-check",
            "agent-battery-report-v1"
        )
        .is_ok()
    );
}

#[test]
fn a_function_row_agent_is_not_admitted_by_feature_or_marker() {
    // The feature and the marker build.rs gives a build with
    // agent-function-row-v1. Packaging has not admitted either, so the reader
    // refuses the image by name whatever else the record claims.
    let features = "agent-function-row-v1,native-agent-entry-compile-check";
    for claimed in [
        "session-function-row-v1",
        "agent-function-row-v1",
        "none",
        "agent-battery-report-v1,session-function-row-v1",
    ] {
        assert!(capabilities(2, "agent", features, claimed).is_err());
        assert!(capabilities(1, "agent", features, claimed).is_err());
        assert!(capabilities(2, "service", features, claimed).is_err());
    }
    // A forged marker without the feature is no better.
    assert!(
        capabilities(
            2,
            "agent",
            "native-agent-entry-compile-check",
            "session-function-row-v1"
        )
        .is_err()
    );
    // Beside an admitted contract, and beside every Agent contract with the
    // reporter, the hook is still refused; the contract alone is not.
    let combined = "agent-battery-report-v1,agent-function-row-v1,native-agent-entry-compile-check";
    assert!(capabilities(2, "agent", combined, "agent-battery-report-v1").is_err());
    assert!(
        capabilities(
            2,
            "agent",
            combined,
            "agent-battery-report-v1,session-function-row-v1"
        )
        .is_err()
    );
    assert!(
        capabilities(
            2,
            "agent",
            "agent-battery-report-v1,native-agent-entry-compile-check",
            "agent-battery-report-v1"
        )
        .is_ok()
    );
}

#[test]
fn a_display_automation_agent_is_not_admitted_by_feature_or_marker() {
    // The feature and the marker build.rs gives a build with
    // agent-display-automation-v1. It writes without a request, so packaging
    // refuses the image by name whatever else the record claims.
    let features = "agent-display-automation-v1,native-agent-entry-compile-check";
    for claimed in [
        "session-display-automation-v1",
        "agent-display-automation-v1",
        "none",
        "agent-battery-report-v1,session-display-automation-v1",
    ] {
        assert!(capabilities(2, "agent", features, claimed).is_err());
        assert!(capabilities(1, "agent", features, claimed).is_err());
        assert!(capabilities(2, "service", features, claimed).is_err());
    }
    // A forged marker without the feature is no better.
    assert!(
        capabilities(
            2,
            "agent",
            "native-agent-entry-compile-check",
            "session-display-automation-v1"
        )
        .is_err()
    );
    // Beside an admitted contract the automation is still refused.
    let combined =
        "agent-battery-report-v1,agent-display-automation-v1,native-agent-entry-compile-check";
    assert!(capabilities(2, "agent", combined, "agent-battery-report-v1").is_err());
}

#[test]
fn legacy_artifact_record_and_compiled_identity_reencode_byte_exact() {
    let v = vector();
    for role in ["service", "agent"] {
        let record = bytes(&v, &format!("{role}Record"));
        let f = lines(&record, "OpenBlade.NativeArtifactRecord.v1", 10).unwrap();
        assert_eq!(record, encode("OpenBlade.NativeArtifactRecord.v1", &f));
        let image = bytes(&v, &format!("{role}Image"));
        assert_eq!(
            compiled_record(&image).unwrap(),
            encode("OpenBlade.NativeArtifact.v1", &f[..7])
        );
    }
}

#[test]
fn late_queued_start_cannot_reuse_submission_after_retirement_or_unresolved_selection() {
    let v = vector();
    let selected = bytes(&v, "epoch");
    let epoch = Epoch::parse(&selected).unwrap();
    let origin = b"authenticated original bytes supplied after custody";
    let marker = encode(
        "OpenBlade.NativeStartupSubmission.v1",
        &[
            &epoch.id,
            &epoch.request,
            &digest(origin),
            &epoch.package,
            &epoch.service,
        ],
    );
    require_submission(&epoch, origin, &marker).unwrap();
    assert!(require_submission(&epoch, origin, b"").is_err());
    let mut torn = marker.clone();
    torn[0] ^= 1;
    assert!(require_submission(&epoch, origin, &torn).is_err());
    for phase in ["Retired", "Unresolved"] {
        let next = String::from_utf8(selected.clone())
            .unwrap()
            .replace("Committed", phase)
            .replace(&epoch.readiness, ZERO)
            .into_bytes();
        let retired = Epoch::parse(&next).unwrap();
        assert!(require_submission(&retired, origin, &marker).is_err());
        assert!(
            Promotion::default()
                .check(
                    &epoch,
                    &selected,
                    &next,
                    &next,
                    None,
                    std::time::Instant::now()
                )
                .is_err()
        );
    }
}

#[test]
fn observation_restart_requires_transport_drain_and_preserves_unmatched_prior_attempts() {
    use std::{collections::BTreeMap, os::windows::ffi::OsStrExt};
    let epoch = Epoch::parse(&bytes(&vector(), "epoch")).unwrap();
    let origin = b"injected original origin";
    let image = PathBuf::from(r"C:\Fixture\app\OpenBlade.Service.exe");
    let path: Vec<_> = image
        .as_os_str()
        .encode_wide()
        .flat_map(u16::to_le_bytes)
        .collect();
    let launch = encode(
        "OpenBlade.NativeServiceLaunch.v1",
        &[
            &epoch.id,
            &digest(origin),
            &epoch.request,
            &epoch.package,
            &epoch.service,
            "17",
            "100",
            "0",
            "LocalSystem-primary-session0",
            &encode64(&path),
        ],
    );
    let mut records = BTreeMap::from([
        (
            "custody.lock".to_owned(),
            encode("OpenBlade.NativeServiceCustody.v1", &[&epoch.id]),
        ),
        ("launch-17-100.bin".to_owned(), launch.clone()),
    ]);
    let check = |records: &BTreeMap<String, Vec<u8>>, read| {
        require_launch_budget(
            records.keys().cloned().collect(),
            |name| Ok(records[name].clone()),
            &epoch,
            origin,
            &image,
            read,
        )
    };
    let before = records.clone();
    check(&records, true).unwrap(); // Diagnostic restart does not claim older drain.
    assert_eq!(records, before);
    records.insert(
        "drain-17-100.bin".into(),
        encode(
            "OpenBlade.NativeStatusDrain.v2",
            &[&epoch.id, &digest(&launch), "17", "100"],
        ),
    );
    assert!(check(&records, true).is_err());
    records.insert(
        "drain-17-100.bin".into(),
        encode(
            "OpenBlade.NativeTransportDrain.v1",
            &[&epoch.id, &digest(&launch), "17", "100"],
        ),
    );
    check(&records, true).unwrap();
    assert!(check(&records, false).is_err());
}

#[test]
fn selected_agent_battery_capability_is_exact_and_independent_of_service_features() {
    let selected = "agent-battery-report-v1,native-agent-entry-compile-check";
    assert!(capabilities(2, "agent", selected, "agent-battery-report-v1").is_ok());
    assert!(capabilities(2, "agent", selected, "none").is_err());
    assert!(capabilities(1, "agent", selected, "none").is_err());
    assert!(
        capabilities(
            2,
            "agent",
            "agent-battery-report-v1",
            "agent-battery-report-v1"
        )
        .is_err()
    );
    assert!(
        capabilities(
            2,
            "agent",
            "native-agent-entry-compile-check",
            "agent-battery-report-v1"
        )
        .is_err()
    );
    let combined = "agent-battery-report-v1,installed-service-candidate,native-agent-entry-compile-check,service-metadata-only-v1,startup-custody-v1";
    assert!(capabilities(2, "agent", combined, "agent-battery-report-v1").is_ok());
    assert!(
        capabilities(
            2,
            "service",
            combined,
            "private-service-custody-v1,service-metadata-only-v1"
        )
        .is_ok()
    );
    assert!(capabilities(2, "service", combined, "agent-battery-report-v1").is_err());
}

#[test]
fn selected_partial_display_query_capability_is_exact_and_composes_with_battery() {
    let selected = "agent-display-query-v1,native-agent-entry-compile-check";
    let display = "session-display-query-partial-v1";
    assert!(capabilities(2, "agent", selected, display).is_ok());
    assert!(capabilities(1, "agent", selected, display).is_err());
    assert!(capabilities(2, "agent", "agent-display-query-v1", display).is_err());
    assert!(capabilities(2, "agent", "native-agent-entry-compile-check", display).is_err());
    for forged in [
        "none",
        "agent-display-query-v1",
        "session-display-v1",
        "session-display-query-partial-v1,session-display-query-partial-v1",
    ] {
        assert!(capabilities(2, "agent", selected, forged).is_err());
    }
    let combined = "agent-battery-report-v1,agent-display-query-v1,installed-service-candidate,native-agent-entry-compile-check,service-metadata-only-v1,startup-custody-v1";
    let both = "agent-battery-report-v1,session-display-query-partial-v1";
    assert!(capabilities(2, "agent", combined, both).is_ok());
    for forged in [
        display,
        "agent-battery-report-v1",
        "session-display-query-partial-v1,agent-battery-report-v1",
        "agent-battery-report-v1,session-display-query-partial-v1,session-display-query-partial-v1",
    ] {
        assert!(capabilities(2, "agent", combined, forged).is_err());
    }
    assert!(
        capabilities(
            2,
            "service",
            combined,
            "private-service-custody-v1,service-metadata-only-v1"
        )
        .is_ok()
    );
    assert!(capabilities(2, "service", combined, both).is_err());
}

#[test]
fn selected_partial_display_brightness_write_capability_is_exact_and_read_only_builds_cannot_claim_it()
 {
    let write = "session-display-query-partial-v1,session-display-brightness-write-partial-v1";
    let battery_write = "agent-battery-report-v1,session-display-query-partial-v1,session-display-brightness-write-partial-v1";
    let selected =
        "agent-display-brightness-write-v1,agent-display-query-v1,native-agent-entry-compile-check";
    assert!(capabilities(2, "agent", selected, write).is_ok());
    assert!(capabilities(1, "agent", selected, write).is_err());
    assert!(
        capabilities(
            2,
            "agent",
            "agent-display-brightness-write-v1,native-agent-entry-compile-check",
            write
        )
        .is_err()
    );
    assert!(
        capabilities(
            2,
            "agent",
            "agent-display-brightness-write-v1,agent-display-query-v1",
            write
        )
        .is_err()
    );
    for forged in [
        "none",
        "session-display-query-partial-v1",
        "session-display-brightness-write-partial-v1",
        "session-display-brightness-write-partial-v1,session-display-query-partial-v1",
        "agent-battery-report-v1,session-display-query-partial-v1",
        battery_write,
        "session-display-query-partial-v1,session-display-brightness-write-partial-v1,session-display-brightness-write-partial-v1",
    ] {
        assert!(capabilities(2, "agent", selected, forged).is_err());
    }
    let query_only = "agent-display-query-v1,native-agent-entry-compile-check";
    assert!(capabilities(2, "agent", query_only, write).is_err());
    assert!(capabilities(2, "agent", query_only, battery_write).is_err());
    let combined = "agent-battery-report-v1,agent-display-brightness-write-v1,agent-display-query-v1,native-agent-entry-compile-check";
    assert!(capabilities(2, "agent", combined, battery_write).is_ok());
    for forged in [
        write,
        "agent-battery-report-v1,session-display-query-partial-v1",
        "session-display-query-partial-v1,session-display-brightness-write-partial-v1,agent-battery-report-v1",
        "agent-battery-report-v1,session-display-brightness-write-partial-v1,session-display-query-partial-v1",
    ] {
        assert!(capabilities(2, "agent", combined, forged).is_err());
    }
    assert!(capabilities(2, "service", selected, write).is_err());
}

#[test]
fn selected_partial_display_hdr_write_capability_is_exact_and_other_builds_cannot_claim_it() {
    const QUERY: &str = "session-display-query-partial-v1";
    const BRIGHTNESS: &str = "session-display-brightness-write-partial-v1";
    const HDR: &str = "session-display-hdr-write-partial-v1";
    const BATTERY: &str = "agent-battery-report-v1";
    let entry = "native-agent-entry-compile-check";
    for battery in [false, true] {
        for brightness in [false, true] {
            let mut features = vec![
                "agent-display-hdr-write-v1",
                "agent-display-query-v1",
                entry,
            ];
            let mut markers = vec![QUERY, HDR];
            if brightness {
                features.push("agent-display-brightness-write-v1");
                markers.insert(1, BRIGHTNESS);
            }
            if battery {
                features.push("agent-battery-report-v1");
                markers.insert(0, BATTERY);
            }
            features.sort();
            let selected = features.join(",");
            let expected = markers.join(",");
            assert!(capabilities(2, "agent", &selected, &expected).is_ok());
            assert!(capabilities(1, "agent", &selected, &expected).is_err());
            assert!(capabilities(2, "service", &selected, &expected).is_err());
            // The write needs both the display query and the native entry.
            for missing in ["agent-display-query-v1", entry] {
                let partial: Vec<_> = features.iter().copied().filter(|f| *f != missing).collect();
                assert!(capabilities(2, "agent", &partial.join(","), &expected).is_err());
            }
            let without_hdr: Vec<_> = markers.iter().copied().filter(|m| *m != HDR).collect();
            let reordered = {
                let mut reordered = markers.clone();
                let hdr = reordered.pop().unwrap();
                reordered.insert(reordered.len() - 1, hdr);
                reordered.join(",")
            };
            for forged in [
                "none".to_owned(),
                without_hdr.join(","),
                HDR.to_owned(),
                format!("{expected},{HDR}"),
                reordered,
            ] {
                assert!(
                    capabilities(2, "agent", &selected, &forged).is_err(),
                    "{forged}"
                );
            }
        }
    }
    // Query-only and brightness-only builds cannot claim the HDR marker.
    for (features, markers) in [
        (
            "agent-display-query-v1,native-agent-entry-compile-check",
            vec![QUERY],
        ),
        (
            "agent-display-brightness-write-v1,agent-display-query-v1,native-agent-entry-compile-check",
            vec![QUERY, BRIGHTNESS],
        ),
        (
            "agent-battery-report-v1,agent-display-brightness-write-v1,agent-display-query-v1,native-agent-entry-compile-check",
            vec![BATTERY, QUERY, BRIGHTNESS],
        ),
    ] {
        assert!(capabilities(2, "agent", features, &markers.join(",")).is_ok());
        let mut claimed = markers.clone();
        claimed.push(HDR);
        assert!(capabilities(2, "agent", features, &claimed.join(",")).is_err());
    }
}

#[test]
fn selected_partial_display_refresh_write_capability_is_exact_and_other_builds_cannot_claim_it() {
    const QUERY: &str = "session-display-query-partial-v1";
    const BRIGHTNESS: &str = "session-display-brightness-write-partial-v1";
    const HDR: &str = "session-display-hdr-write-partial-v1";
    const REFRESH: &str = "session-display-refresh-write-partial-v1";
    const BATTERY: &str = "agent-battery-report-v1";
    let entry = "native-agent-entry-compile-check";
    for selection in 0..8 {
        let (battery, brightness, hdr) =
            (selection & 1 != 0, selection & 2 != 0, selection & 4 != 0);
        let mut features = vec![
            "agent-display-query-v1",
            "agent-display-refresh-write-v1",
            entry,
        ];
        let mut markers = vec![QUERY];
        if battery {
            features.push("agent-battery-report-v1");
            markers.insert(0, BATTERY);
        }
        if brightness {
            features.push("agent-display-brightness-write-v1");
            markers.push(BRIGHTNESS);
        }
        if hdr {
            features.push("agent-display-hdr-write-v1");
            markers.push(HDR);
        }
        markers.push(REFRESH);
        features.sort();
        let selected = features.join(",");
        let expected = markers.join(",");
        assert!(
            capabilities(2, "agent", &selected, &expected).is_ok(),
            "{expected}"
        );
        assert!(capabilities(1, "agent", &selected, &expected).is_err());
        assert!(capabilities(2, "service", &selected, &expected).is_err());
        // The write needs both the display query and the native entry.
        for missing in ["agent-display-query-v1", entry] {
            let partial: Vec<_> = features.iter().copied().filter(|f| *f != missing).collect();
            assert!(capabilities(2, "agent", &partial.join(","), &expected).is_err());
        }
        let without_refresh: Vec<_> = markers.iter().copied().filter(|m| *m != REFRESH).collect();
        let reordered = {
            let mut reordered = markers.clone();
            let refresh = reordered.pop().unwrap();
            reordered.insert(reordered.len() - 1, refresh);
            reordered.join(",")
        };
        for forged in [
            "none".to_owned(),
            without_refresh.join(","),
            REFRESH.to_owned(),
            format!("{expected},{REFRESH}"),
            reordered,
        ] {
            assert!(
                capabilities(2, "agent", &selected, &forged).is_err(),
                "{forged}"
            );
        }
    }
    // Query-only, brightness-only and HDR-only builds cannot claim the
    // refresh marker.
    for (features, markers) in [
        (
            "agent-display-query-v1,native-agent-entry-compile-check",
            vec![QUERY],
        ),
        (
            "agent-display-brightness-write-v1,agent-display-query-v1,native-agent-entry-compile-check",
            vec![QUERY, BRIGHTNESS],
        ),
        (
            "agent-display-hdr-write-v1,agent-display-query-v1,native-agent-entry-compile-check",
            vec![QUERY, HDR],
        ),
        (
            "agent-battery-report-v1,agent-display-brightness-write-v1,agent-display-hdr-write-v1,agent-display-query-v1,native-agent-entry-compile-check",
            vec![BATTERY, QUERY, BRIGHTNESS, HDR],
        ),
    ] {
        assert!(capabilities(2, "agent", features, &markers.join(",")).is_ok());
        let mut claimed = markers.clone();
        claimed.push(REFRESH);
        assert!(capabilities(2, "agent", features, &claimed.join(",")).is_err());
    }
    // The refresh write requires the display query: a build without it
    // cannot claim the refresh marker alone under either schema.
    for battery in [false, true] {
        let mut features = vec!["agent-display-refresh-write-v1", entry];
        let mut markers = vec![REFRESH];
        if battery {
            features.insert(0, "agent-battery-report-v1");
            markers.insert(0, BATTERY);
        }
        for schema in [1, 2] {
            assert!(
                capabilities(schema, "agent", &features.join(","), &markers.join(",")).is_err(),
                "{schema} {battery}"
            );
        }
    }
}

#[test]
fn selected_partial_display_color_write_capability_is_exact_and_other_builds_cannot_claim_it() {
    const QUERY: &str = "session-display-query-partial-v1";
    const BRIGHTNESS: &str = "session-display-brightness-write-partial-v1";
    const HDR: &str = "session-display-hdr-write-partial-v1";
    const REFRESH: &str = "session-display-refresh-write-partial-v1";
    const COLOR: &str = "session-display-color-write-partial-v1";
    const BATTERY: &str = "agent-battery-report-v1";
    let entry = "native-agent-entry-compile-check";
    for selection in 0..16 {
        let mut features = vec![
            "agent-display-query-v1",
            "agent-display-color-write-v1",
            entry,
        ];
        let mut markers = vec![QUERY];
        if selection & 1 != 0 {
            features.push("agent-battery-report-v1");
            markers.insert(0, BATTERY);
        }
        for (bit, feature, marker) in [
            (2, "agent-display-brightness-write-v1", BRIGHTNESS),
            (4, "agent-display-hdr-write-v1", HDR),
            (8, "agent-display-refresh-write-v1", REFRESH),
        ] {
            if selection & bit != 0 {
                features.push(feature);
                markers.push(marker);
            }
        }
        markers.push(COLOR);
        features.sort();
        let selected = features.join(",");
        let expected = markers.join(",");
        assert!(
            capabilities(2, "agent", &selected, &expected).is_ok(),
            "{expected}"
        );
        assert!(capabilities(1, "agent", &selected, &expected).is_err());
        assert!(capabilities(2, "service", &selected, &expected).is_err());
        // The write needs both the display query and the native entry.
        for missing in ["agent-display-query-v1", entry] {
            let partial: Vec<_> = features.iter().copied().filter(|f| *f != missing).collect();
            assert!(capabilities(2, "agent", &partial.join(","), &expected).is_err());
        }
        let without_color: Vec<_> = markers.iter().copied().filter(|m| *m != COLOR).collect();
        let reordered = {
            let mut reordered = markers.clone();
            let color = reordered.pop().unwrap();
            reordered.insert(reordered.len() - 1, color);
            reordered.join(",")
        };
        for forged in [
            "none".to_owned(),
            without_color.join(","),
            COLOR.to_owned(),
            format!("{expected},{COLOR}"),
            reordered,
        ] {
            assert!(
                capabilities(2, "agent", &selected, &forged).is_err(),
                "{forged}"
            );
        }
    }
    // Builds without the color write cannot claim its marker.
    for (features, markers) in [
        (
            "agent-display-query-v1,native-agent-entry-compile-check",
            vec![QUERY],
        ),
        (
            "agent-display-brightness-write-v1,agent-display-query-v1,native-agent-entry-compile-check",
            vec![QUERY, BRIGHTNESS],
        ),
        (
            "agent-display-hdr-write-v1,agent-display-query-v1,native-agent-entry-compile-check",
            vec![QUERY, HDR],
        ),
        (
            "agent-display-query-v1,agent-display-refresh-write-v1,native-agent-entry-compile-check",
            vec![QUERY, REFRESH],
        ),
        (
            "agent-battery-report-v1,agent-display-brightness-write-v1,agent-display-hdr-write-v1,agent-display-query-v1,agent-display-refresh-write-v1,native-agent-entry-compile-check",
            vec![BATTERY, QUERY, BRIGHTNESS, HDR, REFRESH],
        ),
    ] {
        assert!(capabilities(2, "agent", features, &markers.join(",")).is_ok());
        let mut claimed = markers.clone();
        claimed.push(COLOR);
        assert!(capabilities(2, "agent", features, &claimed.join(",")).is_err());
    }
    // The color write requires the display query: a build without it
    // cannot claim the color marker alone under either schema.
    for battery in [false, true] {
        let mut features = vec!["agent-display-color-write-v1", entry];
        let mut markers = vec![COLOR];
        if battery {
            features.insert(0, "agent-battery-report-v1");
            markers.insert(0, BATTERY);
        }
        for schema in [1, 2] {
            assert!(
                capabilities(schema, "agent", &features.join(","), &markers.join(",")).is_err(),
                "{schema} {battery}"
            );
        }
        // Nor with the query marker it does not have.
        let mut claimed = markers.clone();
        claimed.insert(claimed.len() - 1, QUERY);
        assert!(capabilities(2, "agent", &features.join(","), &claimed.join(",")).is_err());
    }
}

#[test]
fn selected_partial_display_profile_capability_requires_every_write_and_is_exact() {
    const QUERY: &str = "session-display-query-partial-v1";
    const BRIGHTNESS: &str = "session-display-brightness-write-partial-v1";
    const HDR: &str = "session-display-hdr-write-partial-v1";
    const REFRESH: &str = "session-display-refresh-write-partial-v1";
    const COLOR: &str = "session-display-color-write-partial-v1";
    const PROFILE: &str = "session-display-profile-apply-partial-v1";
    const BATTERY: &str = "agent-battery-report-v1";
    let entry = "native-agent-entry-compile-check";
    let writes = [
        "agent-display-brightness-write-v1",
        "agent-display-hdr-write-v1",
        "agent-display-refresh-write-v1",
        "agent-display-color-write-v1",
    ];
    for battery in [false, true] {
        let mut features = vec![
            "agent-display-query-v1",
            "agent-display-profile-apply-v1",
            entry,
        ];
        features.extend(writes);
        let mut markers = vec![QUERY, BRIGHTNESS, HDR, REFRESH, COLOR, PROFILE];
        if battery {
            features.push("agent-battery-report-v1");
            markers.insert(0, BATTERY);
        }
        features.sort();
        let selected = features.join(",");
        let expected = markers.join(",");
        assert!(
            capabilities(2, "agent", &selected, &expected).is_ok(),
            "{expected}"
        );
        assert!(capabilities(1, "agent", &selected, &expected).is_err());
        assert!(capabilities(2, "service", &selected, &expected).is_err());
        // The profile needs the query, every write it composes and the entry.
        for missing in writes
            .iter()
            .copied()
            .chain(["agent-display-query-v1", entry])
        {
            let partial: Vec<_> = features.iter().copied().filter(|f| *f != missing).collect();
            let claimed: Vec<_> = markers
                .iter()
                .copied()
                .filter(|marker| {
                    !(missing.contains("brightness") && *marker == BRIGHTNESS
                        || missing.contains("hdr") && *marker == HDR
                        || missing.contains("refresh") && *marker == REFRESH
                        || missing.contains("color") && *marker == COLOR)
                })
                .collect();
            for schema in [1, 2] {
                assert!(
                    capabilities(schema, "agent", &partial.join(","), &claimed.join(",")).is_err(),
                    "{missing}"
                );
            }
        }
        let without_profile: Vec<_> = markers.iter().copied().filter(|m| *m != PROFILE).collect();
        let reordered = {
            let mut reordered = markers.clone();
            let profile = reordered.pop().unwrap();
            reordered.insert(reordered.len() - 1, profile);
            reordered.join(",")
        };
        for forged in [
            "none".to_owned(),
            without_profile.join(","),
            PROFILE.to_owned(),
            format!("{expected},{PROFILE}"),
            reordered,
        ] {
            assert!(
                capabilities(2, "agent", &selected, &forged).is_err(),
                "{forged}"
            );
        }
    }
    // A build with every write but no profile cannot claim its marker.
    let mut features = vec!["agent-display-query-v1", entry];
    features.extend(writes);
    features.sort();
    let markers = [QUERY, BRIGHTNESS, HDR, REFRESH, COLOR];
    assert!(capabilities(2, "agent", &features.join(","), &markers.join(",")).is_ok());
    let claimed = format!("{},{PROFILE}", markers.join(","));
    assert!(capabilities(2, "agent", &features.join(","), &claimed).is_err());
}

/// The import descriptors of a real bound Agent (static CRT, every Agent
/// feature), read from the image without executing it; the
/// managed `WindowsNativeEpochTests.RealAgentImports` pins the same set.
const REAL_AGENT_IMPORTS: [&str; 15] = [
    "kernel32.dll",
    "wtsapi32.dll",
    "shell32.dll",
    "combase.dll",
    "webservices.dll",
    "bcrypt.dll",
    "advapi32.dll",
    "ntdll.dll",
    "ole32.dll",
    "api-ms-win-core-synch-l1-2-0.dll",
    "user32.dll",
    "oleaut32.dll",
    "mscms.dll",
    "bcryptprimitives.dll",
    "KERNEL32.dll",
];

/// The managed package source, whose `IsSystemImport` must admit exactly the
/// names of [`SYSTEM_IMPORTS`] and then only the API-set rule; the Rust
/// workflow also runs on its changes.
const MANAGED_PACKAGE: &str =
    include_str!("../../../../src/OpenBlade.Deployment/WindowsNativePackage.cs");

#[test]
fn the_import_allowlist_matches_the_managed_allowlist() {
    let (_, rule) = MANAGED_PACKAGE
        .split_once("bool IsSystemImport(string name) =>")
        .expect("the managed allowlist");
    let (rule, _) = rule.split_once(';').expect("one expression");
    let (names, api_sets) = rule
        .split_once("|| (name.StartsWith(")
        .expect("the managed API-set rule follows the names");
    // Nothing but the API-set rule follows the names; both sides check that
    // rule against the shared native-api-set-imports-v1.json table.
    assert_eq!(
        api_sets.split_whitespace().collect::<Vec<_>>().join(" "),
        "\"api-ms-win-\", StringComparison.Ordinal) && name.EndsWith(\".dll\", \
         StringComparison.Ordinal) && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' \
         or '.'))"
    );
    let mut managed: Vec<_> = names.split('"').skip(1).step_by(2).collect();
    let mut native = SYSTEM_IMPORTS.to_vec();
    managed.sort_unstable();
    native.sort_unstable();
    assert_eq!(managed, native);
}

#[test]
fn api_set_imports_follow_the_shared_table() {
    let data: serde_json::Value = serde_json::from_str(include_str!(
        "../../tests/data/native-api-set-imports-v1.json"
    ))
    .unwrap();
    for case in data["cases"].as_array().unwrap() {
        let name = case["name"].as_str().unwrap();
        assert_eq!(
            system_import(name),
            case["admitted"].as_bool().unwrap(),
            "{name}"
        );
    }
}

#[test]
fn real_agent_imports_are_reviewed_system_contracts_and_others_are_not() {
    for name in REAL_AGENT_IMPORTS {
        assert!(system_import(&name.to_ascii_lowercase()), "{name}");
    }
    for name in [
        "winhttp.dll",
        "wininet.dll",
        "mscms.dll.evil",
        "webservices",
    ] {
        assert!(!system_import(name), "{name}");
    }
}

/// The vector's Agent image with its import section grown to 0x400 bytes:
/// one descriptor per name, the terminator, then the names, like the managed
/// `WindowsNativeEpochTests.Image(imports:)`.
fn with_imports(mut image: Vec<u8>, imports: &[&str]) -> Vec<u8> {
    const IDATA: usize = 0x800;
    let put = |image: &mut Vec<u8>, at: usize, value: usize| {
        image[at..at + 4].copy_from_slice(&u32::try_from(value).unwrap().to_le_bytes());
    };
    let pe = u32::from_le_bytes(image[60..64].try_into().unwrap()) as usize;
    let section = pe + 24 + 240 + 40;
    assert_eq!(&image[section..section + 8], b".idata\0\0");
    image.truncate(IDATA);
    image.resize(IDATA + 0x400, 0);
    put(&mut image, section + 8, 0x400);
    put(&mut image, section + 16, 0x400);
    put(&mut image, pe + 24 + 124, 20 * (imports.len() + 1));
    let mut name = IDATA + 20 * (imports.len() + 1);
    for (index, import) in imports.iter().enumerate() {
        put(&mut image, IDATA + 20 * index + 12, 0x2000 + name - IDATA);
        image[name..name + import.len()].copy_from_slice(import.as_bytes());
        name += import.len() + 1;
    }
    image
}

/// The vector's Agent record bound to `image` and naming `imports`.
fn agent_record(v: &serde_json::Value, image: &[u8], imports: &str) -> Vec<u8> {
    let record = bytes(v, "agentRecord");
    let mut f = lines(&record, "OpenBlade.NativeArtifactRecord.v1", 10).unwrap();
    let (size, sha) = (image.len().to_string(), digest(image));
    (f[7], f[8], f[9]) = (&size, &sha, imports);
    encode("OpenBlade.NativeArtifactRecord.v1", &f)
}

#[test]
fn the_real_agent_import_descriptors_pass_artifact_inspection() {
    let v = vector();
    let r = authenticate(
        &bytes(&v, "enrollment"),
        &bytes(&v, "manifest"),
        &bytes(&v, "signature"),
    )
    .unwrap();
    let image = with_imports(bytes(&v, "agentImage"), &REAL_AGENT_IMPORTS);
    // The record names the canonical set: lowercase, sorted, deduplicated.
    let canonical = "advapi32.dll,api-ms-win-core-synch-l1-2-0.dll,bcrypt.dll,\
                     bcryptprimitives.dll,combase.dll,kernel32.dll,mscms.dll,ntdll.dll,\
                     ole32.dll,oleaut32.dll,shell32.dll,user32.dll,webservices.dll,wtsapi32.dll";
    inspect_artifact(&agent_record(&v, &image, canonical), &image, "agent", &r).unwrap();
    // The same descriptors without the all-zero terminator in the table.
    let mut unterminated = image.clone();
    let size = u32::from_le_bytes(image[60..64].try_into().unwrap()) as usize + 24 + 124;
    let descriptors = u32::try_from(20 * REAL_AGENT_IMPORTS.len()).unwrap();
    unterminated[size..size + 4].copy_from_slice(&descriptors.to_le_bytes());
    let record = agent_record(&v, &unterminated, canonical);
    assert!(inspect_artifact(&record, &unterminated, "agent", &r).is_err());
    let (fewer, _) = canonical.rsplit_once(',').unwrap();
    assert!(inspect_artifact(&agent_record(&v, &image, fewer), &image, "agent", &r).is_err());

    // One unreviewed descriptor among them refuses the whole image.
    let mut unreviewed = REAL_AGENT_IMPORTS.to_vec();
    unreviewed.insert(7, "winhttp.dll");
    let image = with_imports(bytes(&v, "agentImage"), &unreviewed);
    // The record is canonical, so only the allowlist can refuse it.
    let named = canonical.replace("webservices.dll,", "webservices.dll,winhttp.dll,");
    assert!(inspect_artifact(&agent_record(&v, &image, &named), &image, "agent", &r).is_err());
}

#[test]
fn the_identity_bounds_match_the_managed_and_build_script_bounds() {
    for constant in [
        format!("internal const int MaximumIdentityBytes = {MAX_IDENTITY_BYTES};"),
        format!("internal const int MaximumRecordBase64 = {MAX_ARTIFACT_RECORD_BASE64};"),
    ] {
        assert_eq!(MANAGED_PACKAGE.matches(&constant).count(), 1, "{constant}");
    }
    // build.rs mounts the same module once and names the bound only in its
    // assert, path-qualified. This is a text check: it catches a second module,
    // import or binding named identity_bound written in build.rs (including
    // another #[path] mount), another use of the bound, and a changed
    // condition or message. It cannot see a name brought in by a macro or an
    // include!, or a cfg-dependent definition inside identity_bound.rs.
    let build = include_str!("../../build.rs");
    let module = "#[path = \"src/native_package/identity_bound.rs\"]\nmod identity_bound;\n";
    assert_eq!(build.matches(module).count(), 1);
    assert_eq!(build.matches("#[path").count(), 1);
    assert_eq!(build.matches("identity_bound").count(), 4);
    assert_eq!(build.matches("MAX_IDENTITY_BYTES").count(), 2);
    assert_eq!(
        build.matches("identity_bound::MAX_IDENTITY_BYTES").count(),
        2
    );
    for text in [
        "record.len() <= identity_bound::MAX_IDENTITY_BYTES && record.is_ascii(),",
        "at most {} bytes, not {}\",\n            identity_bound::MAX_IDENTITY_BYTES,\n            \
         record.len()\n",
    ] {
        assert_eq!(build.matches(text).count(), 1, "{text}");
    }
}

const IDENTITY_BOUND_DATA: &str = include_str!("../../tests/data/native-identity-bound-v1.json");

/// native-identity-bound-v1.json: identities padded to 1025 bytes, the shared
/// bound, and one byte over it. The managed `WindowsNativeEpochTests` checks
/// `NativeArtifact.Inspect` against the same images and records.
fn identity_bound_cases() -> serde_json::Value {
    let data: serde_json::Value = serde_json::from_str(IDENTITY_BOUND_DATA).unwrap();
    assert_eq!(data["bound"], MAX_IDENTITY_BYTES);
    data
}

/// The worst shippable composition's Cargo features: the closure of
/// service-temporary-charge-v1 and every Agent feature, sorted.
const WORST_COMPOSITION: [&str; 19] = [
    "agent-battery-report-v1",
    "agent-display-brightness-write-v1",
    "agent-display-color-write-v1",
    "agent-display-hdr-write-v1",
    "agent-display-query-v1",
    "agent-display-refresh-write-v1",
    "controller-admission-compile-check",
    "firmware-performance-compile-check",
    "hid-identity-compile-check",
    "installed-service-candidate",
    "lifecycle-evidence-compile-check",
    "native-agent-entry-compile-check",
    "service-battery-policy-v1",
    "service-fan-mutation-v2",
    "service-hardware-compile-check",
    "service-mailbox-observation-v1",
    "service-temporary-charge-v1",
    "service-thermal-mutation-v1",
    "startup-custody-v1",
];

/// A release identity for `role` built from [`WORST_COMPOSITION`], padded to
/// exactly `length` bytes with reserved feature names.
fn bound_identity(role: &str, length: usize) -> String {
    let capabilities = if role == "agent" {
        "agent-battery-report-v1,session-display-query-partial-v1,\
         session-display-brightness-write-partial-v1,session-display-hdr-write-partial-v1,\
         session-display-refresh-write-partial-v1,session-display-color-write-partial-v1"
    } else {
        "private-service-custody-v1,service-mailbox-observation-v1,service-thermal-mutation-v1,\
         service-fan-mutation-v2,service-battery-policy-v1,service-temporary-charge-v1"
    };
    let identity = |features: &[String]| {
        let mut sorted = features.to_vec();
        sorted.sort_unstable();
        format!(
            "OpenBlade.NativeArtifact.v2\n{}-{role}\n{role}\nx86_64-pc-windows-msvc\n{}\n{}\n\
             rustc 1.99.1 (58cf8f9c3 2026-10-13)\nrelease\n{capabilities}\n",
            "c".repeat(64),
            "a".repeat(40),
            sorted.join(",")
        )
    };
    let mut features: Vec<String> = WORST_COMPOSITION.map(String::from).to_vec();
    // Whole reserved capability names while room remains for the fill name.
    for n in 0.. {
        let next = [
            features.clone(),
            vec![format!("agent-reserved-capability-{n:03}-v1")],
        ]
        .concat();
        if identity(&next).len() + 32 > length {
            break;
        }
        features = next;
    }
    let fill = "agent-reserved-fill-";
    let base = identity(&[features.clone(), vec![fill.into()]].concat()).len();
    features.push(format!("{fill}{}", "x".repeat(length - base)));
    let text = identity(&features);
    assert_eq!(text.len(), length);
    text
}

/// Inert PE32+ headers, no instructions: `.obid` at RVA 0x1000 with raw room
/// for 4608 bytes, then `.idata` at RVA 0x3000 importing KERNEL32.dll, so no
/// tested identity overlaps another section.
fn bound_image(identity: &[u8]) -> Vec<u8> {
    let mut image = vec![0u8; 0x1800];
    let mut put = |at: usize, bytes: &[u8]| image[at..at + bytes.len()].copy_from_slice(bytes);
    put(0, b"MZ");
    put(60, &0x80u32.to_le_bytes());
    put(0x80, b"PE\0\0");
    for (at, value) in [
        (0x84, 0x8664u16),
        (0x86, 2),
        (0x94, 240),
        (0x96, 2),
        (0x98, 0x20b),
    ] {
        put(at, &value.to_le_bytes());
    }
    let header = [(32, 0x1000u32), (36, 0x200), (56, 0x4000), (60, 0x400)];
    for (at, value) in header
        .into_iter()
        .chain([(108, 16), (120, 0x3000), (124, 40)])
    {
        put(0x98 + at, &value.to_le_bytes());
    }
    let sections = 0x98 + 240;
    put(sections, b".obid");
    put(sections + 40, b".idata");
    let length = u32::try_from(identity.len()).unwrap();
    for (at, value) in [
        (8, length),
        (12, 0x1000),
        (16, 0x1200),
        (20, 0x400),
        (36, 0x4000_0040),
        (48, 0x200),
        (52, 0x3000),
        (56, 0x200),
        (60, 0x1600),
        (76, 0x4000_0040),
    ] {
        put(sections + at, &value.to_le_bytes());
    }
    put(0x400, identity);
    put(0x1600 + 12, &0x3040u32.to_le_bytes());
    put(0x1640, b"KERNEL32.dll\0");
    image
}

/// The exact text of native-identity-bound-v1.json.
fn identity_bound_data() -> String {
    let cases = [
        ("agent-above-former-1024-bound", "agent", 1025, true),
        ("agent-at-bound", "agent", MAX_IDENTITY_BYTES, true),
        ("service-at-bound", "service", MAX_IDENTITY_BYTES, true),
        ("agent-over-bound", "agent", MAX_IDENTITY_BYTES + 1, false),
    ];
    let rows: Vec<_> = cases
        .into_iter()
        .map(|(name, role, length, accepted)| {
            let identity = bound_identity(role, length);
            let image = bound_image(identity.as_bytes());
            let f: Vec<_> = identity.split('\n').collect();
            let (size, sha) = (image.len().to_string(), digest(&image));
            let record = encode(
                "OpenBlade.NativeArtifactRecord.v2",
                &[
                    f[1],
                    f[2],
                    f[3],
                    f[4],
                    f[5],
                    f[6],
                    f[7],
                    &size,
                    &sha,
                    "kernel32.dll",
                    f[8],
                ],
            );
            format!(
                "    {{\n      \"name\": \"{name}\",\n      \"role\": \"{role}\",\n      \
                 \"identityBytes\": {length},\n      \"accepted\": {accepted},\n      \
                 \"image\": \"{}\",\n      \"record\": \"{}\"\n    }}",
                encode64(&image),
                encode64(&record)
            )
        })
        .collect();
    format!(
        "{{\n  \"note\": \"Inert PE32+ headers with no executable instructions. Each .obid \
         identity is the worst shippable composition (service-temporary-charge-v1 and every \
         Agent feature) padded with reserved feature names to an exact length around the \
         shared {MAX_IDENTITY_BYTES}-byte bound. Generated by native_package::tests::\
         regenerate_identity_bound_data.\",\n  \"bound\": {MAX_IDENTITY_BYTES},\n  \
         \"cases\": [\n{}\n  ]\n}}\n",
        rows.join(",\n")
    )
}

/// Rewrites the data after a bound change:
/// `cargo test --manifest-path tools/openblade-windows-host/Cargo.toml --lib
/// regenerate_identity_bound_data -- --ignored`.
#[test]
#[ignore = "writes tests/data/native-identity-bound-v1.json"]
fn regenerate_identity_bound_data() {
    let path = concat!(
        env!("CARGO_MANIFEST_DIR"),
        "/tests/data/native-identity-bound-v1.json"
    );
    std::fs::write(path, identity_bound_data()).unwrap();
}

#[test]
fn identity_bound_data_is_the_generator_output() {
    assert!(
        IDENTITY_BOUND_DATA == identity_bound_data(),
        "regenerate native-identity-bound-v1.json with regenerate_identity_bound_data"
    );
}

/// A test value of any length; `decode64` keeps its 4096-character bound.
fn large(case: &serde_json::Value, name: &str) -> Vec<u8> {
    let value = case[name].as_str().unwrap();
    decode64_bounded(value, value.len()).unwrap()
}

fn vector_release() -> Release {
    let v = vector();
    authenticate(
        &bytes(&v, "enrollment"),
        &bytes(&v, "manifest"),
        &bytes(&v, "signature"),
    )
    .unwrap()
}

#[test]
fn compiled_identity_is_admitted_up_to_the_shared_bound_and_no_further() {
    let data = identity_bound_cases();
    let release = vector_release();
    let cases = data["cases"].as_array().unwrap();
    let sizes: Vec<_> = cases
        .iter()
        .map(|case| case["identityBytes"].as_u64().unwrap() as usize)
        .collect();
    assert_eq!(
        sizes,
        [
            1025,
            MAX_IDENTITY_BYTES,
            MAX_IDENTITY_BYTES,
            MAX_IDENTITY_BYTES + 1
        ]
    );
    for (case, size) in cases.iter().zip(sizes) {
        let image = large(case, "image");
        let record = large(case, "record");
        let accepted = case["accepted"].as_bool().unwrap();
        assert_eq!(
            compiled_record(&image).map(<[u8]>::len).ok(),
            accepted.then_some(size),
            "{}",
            case["name"]
        );
        assert_eq!(
            inspect_artifact(&record, &image, case["role"].as_str().unwrap(), &release).is_ok(),
            accepted,
            "{}",
            case["name"]
        );
    }
}

/// A package record whose artifact fields are `service` and `agent`.
fn package_with(service: &str, agent: &str) -> Vec<u8> {
    let (installation, digest) = ("1".repeat(32), "a".repeat(64));
    encode(
        "OpenBlade.NativePackage.v2",
        &[
            "LocalDev",
            &installation,
            &digest,
            "4294967296",
            &digest,
            &digest,
            &digest,
            &digest,
            service,
            agent,
            "two-executables-static-crt-system-imports-v2",
            &digest,
        ],
    )
}

#[test]
fn package_carries_both_roles_at_the_identity_bound() {
    let data = identity_bound_cases();
    let release = vector_release();
    let case = |name: &str| {
        data["cases"]
            .as_array()
            .unwrap()
            .iter()
            .find(|case| case["name"] == name)
            .unwrap()
    };
    let (service, agent) = (case("service-at-bound"), case("agent-at-bound"));
    let package = package_with(
        &encode64(&large(service, "record")),
        &encode64(&large(agent, "record")),
    );
    let parsed = package_record(&package).unwrap();
    assert_eq!(parsed.fields.len(), 12);
    for (record, case) in [(&parsed.service, service), (&parsed.agent, agent)] {
        // Each record is beyond decode64's 3072-byte reach.
        assert!(record.len() > 3072);
        assert_eq!(*record, large(case, "record"));
        let role = case["role"].as_str().unwrap();
        inspect_artifact(record, &large(case, "image"), role, &release).unwrap();
    }
    // Only package_record widens its two fields: decode64 keeps 4096 characters.
    for field in [parsed.fields[8], parsed.fields[9]] {
        assert!(decode64(field).is_err());
    }
    assert_eq!(decode64(&"A".repeat(4096)).unwrap().len(), 3072);
    assert!(decode64(&"A".repeat(4100)).is_err());
}

#[test]
fn package_artifact_fields_are_bounded_at_the_shared_base64_bound() {
    let at = "A".repeat(MAX_ARTIFACT_RECORD_BASE64);
    let over = "A".repeat(MAX_ARTIFACT_RECORD_BASE64 + 4);
    let package = package_with(&at, "AAAA");
    let parsed = package_record(&package).unwrap();
    // The managed NativeArtifact.MaximumRecordBytes: the encoder's limit.
    assert_eq!(parsed.service.len(), 8193);
    assert_eq!(
        package_record(&package_with("AAAA", &at))
            .unwrap()
            .agent
            .len(),
        8193
    );
    assert!(package_record(&package_with(&over, "AAAA")).is_err());
    assert!(package_record(&package_with("AAAA", &over)).is_err());
}

#[test]
fn selected_session_gpu_capability_is_exact_and_composes_after_display() {
    const GPU: &str = "session-gpu-apps-v1";
    let selected = "agent-gpu-v1,native-agent-entry-compile-check";
    assert!(capabilities(2, "agent", selected, GPU).is_ok());
    assert!(capabilities(1, "agent", selected, GPU).is_err());
    assert!(capabilities(2, "agent", "agent-gpu-v1", GPU).is_err());
    assert!(capabilities(2, "service", selected, GPU).is_err());
    for forged in [
        "none",
        "agent-gpu-v1",
        "session-gpu-v2",
        "session-gpu-apps-v1,session-gpu-apps-v1",
    ] {
        assert!(
            capabilities(2, "agent", selected, forged).is_err(),
            "{forged}"
        );
    }
    // A build without the GPU feature cannot claim its marker.
    let battery = "agent-battery-report-v1,native-agent-entry-compile-check";
    assert!(capabilities(2, "agent", battery, GPU).is_err());
    assert!(
        capabilities(
            2,
            "agent",
            battery,
            "agent-battery-report-v1,session-gpu-apps-v1"
        )
        .is_err()
    );

    let combined = "agent-battery-report-v1,agent-display-query-v1,agent-gpu-v1,native-agent-entry-compile-check";
    let expected = "agent-battery-report-v1,session-display-query-partial-v1,session-gpu-apps-v1";
    assert!(capabilities(2, "agent", combined, expected).is_ok());
    for forged in [
        GPU,
        "agent-battery-report-v1,session-display-query-partial-v1",
        "session-gpu-apps-v1,agent-battery-report-v1,session-display-query-partial-v1",
        "agent-battery-report-v1,session-gpu-apps-v1,session-display-query-partial-v1",
    ] {
        assert!(
            capabilities(2, "agent", combined, forged).is_err(),
            "{forged}"
        );
    }
}

#[test]
fn a_thermal_convergence_service_is_not_admitted_by_feature_or_marker() {
    // The features and the capability build.rs gives a build with
    // service-thermal-convergence-v1. Packaging has not admitted convergence
    // writes, so the reader refuses them by name in either role or schema,
    // whatever else the record claims, and before the triggers they extend.
    let triggers = "controller-admission-compile-check,firmware-performance-compile-check,hid-identity-compile-check,installed-service-candidate,lifecycle-evidence-compile-check,service-battery-policy-v1,service-fan-mutation-v2,service-hardware-compile-check,service-mailbox-observation-v1,service-profile-triggers-v1,service-profiles-v1,service-temporary-charge-v1,service-thermal-mutation-v1,startup-custody-v1";
    let features = triggers.replace(
        "service-thermal-mutation-v1,",
        "service-thermal-convergence-v1,service-thermal-mutation-v1,",
    );
    let temporary = "private-service-custody-v1,service-mailbox-observation-v1,service-thermal-mutation-v1,service-fan-mutation-v2,service-battery-policy-v1,service-temporary-charge-v1";
    let marked = format!(
        "{temporary},service-profiles-v1,service-profile-triggers-v1,service-thermal-convergence-v1"
    );
    for role in ["service", "agent"] {
        for claimed in [marked.as_str(), temporary, "none"] {
            assert!(capabilities(2, role, &features, claimed).is_err());
            assert!(capabilities(1, role, &features, claimed).is_err());
        }
    }
    // Neither the feature nor the marker alone is admitted beside the
    // otherwise admitted temporary-charge composition.
    let extended = triggers.replace("service-profile-triggers-v1,service-profiles-v1,", "");
    assert!(capabilities(2, "service", &extended, temporary).is_ok());
    let feature_only = extended.replace(
        "service-thermal-mutation-v1,",
        "service-thermal-convergence-v1,service-thermal-mutation-v1,",
    );
    assert!(capabilities(2, "service", &feature_only, temporary).is_err());
    let marker_only = format!("{temporary},service-thermal-convergence-v1");
    assert!(capabilities(2, "service", &extended, &marker_only).is_err());
    assert!(capabilities(1, "service", &extended, &marker_only).is_err());
}

#[test]
fn a_gpu_clock_tuning_service_is_not_admitted_by_feature_or_marker() {
    // The features and the capability build.rs gives a build with
    // service-gpu-clock-tuning-v1 beside the triggers it implies. Packaging
    // has not admitted NVML writes, so the reader refuses them by name in
    // either role or schema, whatever else the record claims, and before the
    // triggers they extend.
    const MARKER: &str = "service-gpu-clock-tuning-v1";
    let triggers = "controller-admission-compile-check,firmware-performance-compile-check,hid-identity-compile-check,installed-service-candidate,lifecycle-evidence-compile-check,service-battery-policy-v1,service-fan-mutation-v2,service-hardware-compile-check,service-mailbox-observation-v1,service-profile-triggers-v1,service-profiles-v1,service-temporary-charge-v1,service-thermal-mutation-v1,startup-custody-v1";
    let features = triggers.replace(
        "service-fan-mutation-v2,",
        "service-fan-mutation-v2,service-gpu-clock-tuning-v1,",
    );
    let temporary = "private-service-custody-v1,service-mailbox-observation-v1,service-thermal-mutation-v1,service-fan-mutation-v2,service-battery-policy-v1,service-temporary-charge-v1";
    let marked = format!("{temporary},service-profiles-v1,service-profile-triggers-v1,{MARKER}");
    for role in ["service", "agent"] {
        for claimed in [marked.as_str(), temporary, "none"] {
            assert!(capabilities(2, role, &features, claimed).is_err());
            assert!(capabilities(1, role, &features, claimed).is_err());
        }
    }
    // Neither the feature nor the marker alone is admitted beside the
    // otherwise admitted temporary-charge composition.
    let admitted = triggers.replace("service-profile-triggers-v1,service-profiles-v1,", "");
    assert!(capabilities(2, "service", &admitted, temporary).is_ok());
    let feature_only = admitted.replace(
        "service-fan-mutation-v2,",
        "service-fan-mutation-v2,service-gpu-clock-tuning-v1,",
    );
    assert!(capabilities(2, "service", &feature_only, temporary).is_err());
    let marker_only = format!("{temporary},{MARKER}");
    assert!(capabilities(2, "service", &admitted, &marker_only).is_err());
    assert!(capabilities(1, "service", &admitted, &marker_only).is_err());
}

#[test]
fn a_fan_stop_service_is_not_admitted_by_feature_or_marker() {
    // The features and the capability build.rs gives a build with
    // service-fan-stop-v1 beside updateProfiles. Packaging has not admitted
    // Fan Stop, so the reader refuses it by name in either role or schema,
    // whatever else the record claims.
    const MARKER: &str = "service-fan-stop-v1";
    let profiles = "controller-admission-compile-check,firmware-performance-compile-check,hid-identity-compile-check,installed-service-candidate,lifecycle-evidence-compile-check,service-battery-policy-v1,service-fan-mutation-v2,service-hardware-compile-check,service-mailbox-observation-v1,service-profiles-v1,service-temporary-charge-v1,service-thermal-mutation-v1,startup-custody-v1";
    let features = profiles.replace(
        "service-fan-mutation-v2,",
        "service-fan-mutation-v2,service-fan-stop-v1,",
    );
    let temporary = "private-service-custody-v1,service-mailbox-observation-v1,service-thermal-mutation-v1,service-fan-mutation-v2,service-battery-policy-v1,service-temporary-charge-v1";
    let marked = format!("{temporary},service-profiles-v1,{MARKER}");
    for role in ["service", "agent"] {
        for claimed in [marked.as_str(), temporary, "none"] {
            assert!(capabilities(2, role, &features, claimed).is_err());
            assert!(capabilities(1, role, &features, claimed).is_err());
        }
    }
    // Neither the feature nor the marker alone is admitted beside the
    // otherwise admitted temporary-charge composition.
    let admitted = profiles.replace("service-profiles-v1,", "");
    assert!(capabilities(2, "service", &admitted, temporary).is_ok());
    let feature_only = admitted.replace(
        "service-fan-mutation-v2,",
        "service-fan-mutation-v2,service-fan-stop-v1,",
    );
    assert!(capabilities(2, "service", &feature_only, temporary).is_err());
    let marker_only = format!("{temporary},{MARKER}");
    assert!(capabilities(2, "service", &admitted, &marker_only).is_err());
    assert!(capabilities(1, "service", &admitted, &marker_only).is_err());
}

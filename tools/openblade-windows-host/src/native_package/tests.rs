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
fn selected_partial_display_brightness_write_capability_is_exact_and_read_only_builds_cannot_claim_it() {
    let write = "session-display-query-partial-v1,session-display-brightness-write-partial-v1";
    let battery_write = "agent-battery-report-v1,session-display-query-partial-v1,session-display-brightness-write-partial-v1";
    let selected = "agent-display-brightness-write-v1,agent-display-query-v1,native-agent-entry-compile-check";
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
        let (battery, brightness, hdr) = (selection & 1 != 0, selection & 2 != 0, selection & 4 != 0);
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
        assert!(capabilities(2, "agent", &selected, &expected).is_ok(), "{expected}");
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
}

use super::*;

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
    assert!(record.starts_with(b"OpenBlade.NativeArtifact.v1\n"));
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

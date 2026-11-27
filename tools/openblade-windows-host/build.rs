use std::{env, fs, path::PathBuf, process::Command};

#[path = "src/native_package/identity_bound.rs"]
mod identity_bound;

fn main() {
    println!("cargo::rustc-check-cfg=cfg(openblade_metadata_acquisition_probe)");
    println!("cargo::rustc-check-cfg=cfg(openblade_display_write)");
    println!("cargo::rustc-check-cfg=cfg(openblade_display_manual_write)");
    println!("cargo::rustc-check-cfg=cfg(openblade_agent_endpoint)");
    for name in ["OPENBLADE_NATIVE_BUILD_NONCE", "OPENBLADE_NATIVE_SOURCE"] {
        println!("cargo:rerun-if-env-changed={name}");
    }
    let nonce = env::var("OPENBLADE_NATIVE_BUILD_NONCE").unwrap_or_default();
    let source = env::var("OPENBLADE_NATIVE_SOURCE").unwrap_or_default();
    let hex = |s: &str, n| {
        s.len() == n
            && s.bytes()
                .all(|c| c.is_ascii_digit() || (b'a'..=b'f').contains(&c))
    };
    if (!nonce.is_empty() || !source.is_empty())
        && (!hex(&nonce, 64) || nonce.bytes().all(|b| b == b'0') || !hex(&source, 40))
    {
        panic!("native binding requires a fresh 32-byte lowercase nonce and exact source revision");
    }
    if !nonce.is_empty()
        && env::var("TARGET").as_deref() == Ok("x86_64-pc-windows-msvc")
        && !env::var("CARGO_CFG_TARGET_FEATURE")
            .unwrap_or_default()
            .split(',')
            .any(|v| v == "crt-static")
    {
        panic!("bound native generation recipe requires static CRT");
    }
    let mut features: Vec<_> = env::vars()
        .filter_map(|(k, _)| {
            k.strip_prefix("CARGO_FEATURE_")
                .map(|s| s.to_ascii_lowercase().replace('_', "-"))
        })
        .collect();
    features.sort();
    let has = |name: &str| features.iter().any(|f| f == name);
    let metadata = has("service-metadata-only-v1");
    let hardware = has("service-hardware-compile-check");
    let observation = has("service-mailbox-observation-v1");
    assert!(
        !(metadata
            && (hardware
                || has("firmware-performance-compile-check")
                || has("hid-identity-compile-check")
                || features
                    .iter()
                    .any(|f| f.contains("fixture") || f.starts_with("scratch-")))),
        "metadata composition excludes real firmware acquisition and fixtures"
    );
    if has("installed-service-candidate") {
        assert!(
            metadata != hardware,
            "Service requires one explicit composition"
        );
    }
    let compiler = Command::new(env::var_os("RUSTC").expect("rustc"))
        .arg("--version")
        .output()
        .expect("compiler provenance");
    assert!(compiler.status.success());
    let compiler = String::from_utf8(compiler.stdout).expect("compiler UTF-8");
    let target = env::var("TARGET").expect("target");
    let profile = env::var("PROFILE").expect("profile");
    // Selected Agent contracts in fixed order. Every partial display write
    // implies the display query feature, and the profile apply implies every
    // display write it composes.
    let agent = [
        ("agent-battery-report-v1", "agent-battery-report-v1"),
        (
            "agent-application-reporter-v1",
            "session-application-reporter-v1",
        ),
        ("agent-display-query-v1", "session-display-query-partial-v1"),
        (
            "agent-display-brightness-write-v1",
            "session-display-brightness-write-partial-v1",
        ),
        (
            "agent-display-hdr-write-v1",
            "session-display-hdr-write-partial-v1",
        ),
        (
            "agent-display-refresh-write-v1",
            "session-display-refresh-write-partial-v1",
        ),
        (
            "agent-display-color-write-v1",
            "session-display-color-write-partial-v1",
        ),
        (
            "agent-display-profile-apply-v1",
            "session-display-profile-apply-partial-v1",
        ),
        (
            "agent-display-automation-v1",
            "session-display-automation-v1",
        ),
        ("agent-gpu-v1", "session-gpu-apps-v1"),
        ("agent-function-row-v1", "session-function-row-v1"),
        (
            "agent-power-mode-automation-v1",
            "session-power-mode-automation-v1",
        ),
    ]
    .into_iter()
    .filter_map(|(feature, marker)| has(feature).then_some(marker))
    .collect::<Vec<_>>()
    .join(",");
    // Any selected installed Agent endpoint: shared lifetime and diagnostics.
    if has("agent-battery-report-v1")
        || has("agent-application-reporter-v1")
        || has("agent-display-query-v1")
        || has("agent-gpu-v1")
        || has("agent-function-row-v1")
        || has("agent-power-mode-automation-v1")
    {
        println!("cargo::rustc-cfg=openblade_agent_endpoint");
    }
    // Shared write infrastructure (power admission, recovery, fence) for any
    // selected partial display write.
    if has("agent-display-brightness-write-v1")
        || has("agent-display-hdr-write-v1")
        || has("agent-display-refresh-write-v1")
        || has("agent-display-color-write-v1")
    {
        println!("cargo::rustc-cfg=openblade_display_write");
    }
    // The manual-operation frame shared by the HDR, refresh-rate and color-profile
    // writes.
    if has("agent-display-hdr-write-v1")
        || has("agent-display-refresh-write-v1")
        || has("agent-display-color-write-v1")
    {
        println!("cargo::rustc-cfg=openblade_display_manual_write");
    }
    for (role, name) in [
        ("service", "service_identity.rs"),
        ("agent", "agent_identity.rs"),
    ] {
        // A fresh publisher-supplied nonce is mandatory for each rebuilt output.
        // Role separates the two artifacts emitted by one build invocation.
        let id = if nonce.is_empty() {
            "unbound".into()
        } else {
            format!("{nonce}-{role}")
        };
        let capabilities = if role == "service" && has("installed-service-candidate") {
            if metadata {
                "private-service-custody-v1,service-metadata-only-v1"
            } else if has("service-thermal-convergence-v1") {
                // Thermal convergence carries its own marker beside the
                // triggers it extends, refused by name in both readers.
                "private-service-custody-v1,service-mailbox-observation-v1,service-thermal-mutation-v1,service-fan-mutation-v2,service-battery-policy-v1,service-temporary-charge-v1,service-profiles-v1,service-profile-triggers-v1,service-thermal-convergence-v1"
            } else if has("service-profile-triggers-v1") {
                // Automatic reconciliation carries its own marker, so a
                // package reader refuses it by name whatever else it admits.
                "private-service-custody-v1,service-mailbox-observation-v1,service-thermal-mutation-v1,service-fan-mutation-v2,service-battery-policy-v1,service-temporary-charge-v1,service-profiles-v1,service-profile-triggers-v1"
            } else if has("service-profiles-v1") {
                // No package reader admits this capability yet, so an image
                // that serves updateProfiles cannot be packaged as the
                // temporary-charge composition it extends.
                "private-service-custody-v1,service-mailbox-observation-v1,service-thermal-mutation-v1,service-fan-mutation-v2,service-battery-policy-v1,service-temporary-charge-v1,service-profiles-v1"
            } else if has("service-temporary-charge-v1") {
                "private-service-custody-v1,service-mailbox-observation-v1,service-thermal-mutation-v1,service-fan-mutation-v2,service-battery-policy-v1,service-temporary-charge-v1"
            } else if has("service-battery-policy-v1") {
                "private-service-custody-v1,service-mailbox-observation-v1,service-thermal-mutation-v1,service-fan-mutation-v2,service-battery-policy-v1"
            } else if has("service-fan-mutation-v2") {
                "private-service-custody-v1,service-mailbox-observation-v1,service-thermal-mutation-v1,service-fan-mutation-v2"
            } else if has("service-thermal-mutation-v1") {
                "private-service-custody-v1,service-mailbox-observation-v1,service-thermal-mutation-v1"
            } else if observation {
                "private-service-custody-v1,service-mailbox-observation-v1"
            } else {
                "private-service-custody-v1"
            }
        } else if role == "agent" && !agent.is_empty() {
            &agent
        } else {
            "none"
        };
        // No package reader admits the read-only keyboard Driver mode, so its
        // marker follows whatever composition the Service otherwise claims.
        let capabilities = if role == "service" && has("service-keyboard-driver-mode-read-v1") {
            format!("{capabilities},service-keyboard-driver-mode-read-v1")
        } else {
            capabilities.to_owned()
        };
        // Nor Driver-mode ownership, which adds the 00/04 setter.
        let capabilities = if role == "service" && has("service-keyboard-driver-mode-v1") {
            format!("{capabilities},service-keyboard-driver-mode-v1")
        } else {
            capabilities
        };
        // Nor does any reader admit the live-reading read, whose marker follows
        // the same rule.
        let capabilities = if role == "service" && has("service-live-readings-v1") {
            format!("{capabilities},service-live-readings-v1")
        } else {
            capabilities
        };
        // Nor the diagnostic export, under the same rule.
        let capabilities = if role == "service" && has("service-diagnostics-export-v1") {
            format!("{capabilities},service-diagnostics-export-v1")
        } else {
            capabilities
        };
        // Nor Fan Stop served with its guardian unavailable.
        let capabilities = if role == "service" && has("service-fan-stop-v1") {
            format!("{capabilities},service-fan-stop-v1")
        } else {
            capabilities
        };
        // Nor GPU clock tuning, which writes NVIDIA driver state through NVML.
        let capabilities = if role == "service" && has("service-gpu-clock-tuning-v1") {
            format!("{capabilities},service-gpu-clock-tuning-v1")
        } else {
            capabilities
        };
        // Nor the performance shortcut and its preview, under the same rule.
        let capabilities = if role == "service" && has("service-performance-shortcut-v1") {
            format!("{capabilities},service-performance-shortcut-v1")
        } else {
            capabilities
        };
        // Nor the HyperBoost exit to a battery preset, under the same rule.
        let capabilities = if role == "service" && has("service-thermal-hyperboost-exit-v1") {
            format!("{capabilities},service-thermal-hyperboost-exit-v1")
        } else {
            capabilities
        };
        // Nor owned HyperBoost, under the same rule.
        let capabilities = if role == "service" && has("service-hyperboost-v1") {
            format!("{capabilities},service-hyperboost-v1")
        } else {
            capabilities
        };
        // Nor display-brightness input, which submits RzCommon input IOCTLs.
        let capabilities = if role == "service" && has("service-brightness-hid-usage-v1") {
            format!("{capabilities},service-brightness-hid-usage-v1")
        } else {
            capabilities
        };
        // Nor keyboard lighting, under the same rule.
        let capabilities = if role == "service" && has("service-lighting-v1") {
            format!("{capabilities},service-lighting-v1")
        } else {
            capabilities
        };
        let record = format!(
            "OpenBlade.NativeArtifact.v2\n{id}\n{role}\n{target}\n{source}\n{}\n{}\n{profile}\n{capabilities}\n",
            features.join(","),
            compiler.trim()
        );
        assert!(
            record.len() <= identity_bound::MAX_IDENTITY_BYTES && record.is_ascii(),
            "the {role} build identity must be ASCII and at most {} bytes, not {}",
            identity_bound::MAX_IDENTITY_BYTES,
            record.len()
        );
        let bytes = format!("{:?}", record.as_bytes());
        let generated = format!(
            "#[allow(unsafe_code)]\n#[used]\n#[unsafe(link_section = \".obid\")]\nstatic NATIVE_ARTIFACT_IDENTITY: [u8; {}] = {bytes};\n",
            record.len()
        );
        fs::write(
            PathBuf::from(env::var_os("OUT_DIR").unwrap()).join(name),
            generated,
        )
        .unwrap();
    }
}

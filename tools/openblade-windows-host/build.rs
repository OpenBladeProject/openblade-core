use std::{env, fs, path::PathBuf, process::Command};

fn main() {
    println!("cargo::rustc-check-cfg=cfg(openblade_metadata_acquisition_probe)");
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
            } else {
                "private-service-custody-v1"
            }
        } else {
            "none"
        };
        let record = format!(
            "OpenBlade.NativeArtifact.v2\n{id}\n{role}\n{target}\n{source}\n{}\n{}\n{profile}\n{capabilities}\n",
            features.join(","),
            compiler.trim()
        );
        assert!(record.len() <= 1024 && record.is_ascii());
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

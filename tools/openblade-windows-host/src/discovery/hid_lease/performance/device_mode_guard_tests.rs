//! The reviewed 00/04 keyboard device-mode setters exist only behind the
//! protocol crate's `device-mode-setters` feature, which only the host's
//! `service-keyboard-driver-mode-v1` composition enables, and in its tests.
//! `RazerReport::create` and `encode_report` are public encoders, so no type
//! stops other code building 00/04. This guard is a textual allowlist: it
//! fails when non-test source in the native Service's crates names the setter
//! outside the codec and the one Driver-mode write recipe, builds a
//! Driver-mode job outside its one owner, renames an encoder on import, or
//! adds a call to either encoder, so each new one is reviewed against the
//! admitted commands. It does not see an encoder reached through
//! a glob import or a macro.
use std::{
    collections::BTreeMap,
    fs,
    path::{Path, PathBuf},
};

/// Every non-test file of the host and the Rust crates it links that names
/// `RazerReport::create(` or `encode_report(`, with its count. `openblade-ffi`
/// is managed's encoder and is not linked into the native Service.
const CONSTRUCTIONS: &[(&str, usize)] = &[
    ("rust/crates/openblade-protocol/src/battery.rs", 4),
    (
        "rust/crates/openblade-protocol/src/custom_performance.rs",
        2,
    ),
    // The 00/84 query and the feature-gated 00/04 setter.
    ("rust/crates/openblade-protocol/src/device_mode.rs", 2),
    ("rust/crates/openblade-protocol/src/fan.rs", 1),
    // The 0D/88 fan speed query alone.
    ("rust/crates/openblade-protocol/src/fan_rpm.rs", 1),
    // The reviewed 03/83, 03/80 and 03/82 queries and, behind
    // `lighting-setters`, the 03/03, 03/00 and 03/02 setters, through one
    // fixed-length builder.
    ("rust/crates/openblade-protocol/src/lighting.rs", 1),
    // The 00/87 MCU version query alone.
    ("rust/crates/openblade-protocol/src/mcu_version.rs", 1),
    // The definition of `encode_report`.
    ("rust/crates/openblade-protocol/src/lib.rs", 1),
    ("rust/crates/openblade-protocol/src/thermal_preset.rs", 1),
    ("tools/openblade-windows-host/src/battery_runtime.rs", 1),
    (
        "tools/openblade-windows-host/src/battery_runtime/lighting.rs",
        1,
    ),
    (
        "tools/openblade-windows-host/src/discovery/hid_lease/performance.rs",
        1,
    ),
    (
        "tools/openblade-windows-host/src/discovery/hid_lease/performance/mutation.rs",
        1,
    ),
    // The scripted reply encoder of the test-only mutation fixture.
    (
        "tools/openblade-windows-host/src/discovery/hid_lease/performance/mutation_fixture.rs",
        1,
    ),
    // Rebuilds an already submitted request to correlate its reply; the
    // result is never encoded or sent.
    (
        "tools/openblade-windows-host/src/discovery/hid_lease/performance/transactions.rs",
        1,
    ),
    ("tools/openblade-windows-host/src/fan_runtime/device.rs", 2),
    (
        "tools/openblade-windows-host/src/performance_runtime/device.rs",
        2,
    ),
];

/// The only non-test files that may name the 00/04 setter: its codec and the
/// Driver-mode write recipe (`Reader::apply_device_mode`).
const SETTER_USERS: &[&str] = &[
    "rust/crates/openblade-protocol/src/device_mode.rs",
    "tools/openblade-windows-host/src/discovery/hid_lease/performance/device_mode.rs",
];

/// The only non-test file that may build a Driver-mode job, and so ask the
/// recipe for a 00/04 setter: the Service's owner (`Runtime::maintain_driver_mode`
/// and the shutdown release). The recipe's module defines it.
const JOB_USERS: &[&str] =
    &["tools/openblade-windows-host/src/windows/scm/installed/driver_mode_owner.rs"];

fn root() -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR")).join("../..")
}

/// Test modules by this repository's naming: `tests.rs`, `*_tests.rs` and
/// anything under a `tests` or `*_tests` directory. A fixture name alone is
/// not a test module; production fixtures exist.
fn is_test(path: &Path) -> bool {
    let name = path.file_name().unwrap().to_str().unwrap();
    name == "tests.rs"
        || name.ends_with("_tests.rs")
        || path.components().any(|part| {
            part.as_os_str()
                .to_str()
                .is_some_and(|part| part == "tests" || part.ends_with("_tests"))
        })
}

fn sources(dir: &Path, found: &mut Vec<PathBuf>) {
    for entry in fs::read_dir(dir).unwrap() {
        let path = entry.unwrap().path();
        if path.is_dir() {
            sources(&path, found);
        } else if path.extension().is_some_and(|ext| ext == "rs") && !is_test(&path) {
            found.push(path);
        }
    }
}

#[test]
fn no_non_test_source_names_the_setter_or_adds_an_encoder_call() {
    let root = root();
    let mut files = Vec::new();
    sources(&root.join("tools/openblade-windows-host/src"), &mut files);
    for entry in fs::read_dir(root.join("rust/crates")).unwrap() {
        let krate = entry.unwrap().path();
        if krate.file_name().unwrap() != "openblade-ffi" && krate.join("src").is_dir() {
            sources(&krate.join("src"), &mut files);
        }
    }
    let mut constructions = BTreeMap::new();
    for file in &files {
        let text = fs::read_to_string(file).unwrap();
        let name = file
            .strip_prefix(&root)
            .unwrap()
            .to_str()
            .unwrap()
            .replace('\\', "/");
        assert!(
            !text.contains("DeviceModeSet") || SETTER_USERS.contains(&name.as_str()),
            "{name} names the setter"
        );
        assert!(
            !text.contains("DeviceModeJob::new") || JOB_USERS.contains(&name.as_str()),
            "{name} builds a Driver-mode job"
        );
        for encoder in ["RazerReport as ", "encode_report as "] {
            assert!(!text.contains(encoder), "{name} renames an encoder");
        }
        let count =
            text.matches("RazerReport::create(").count() + text.matches("encode_report(").count();
        if count > 0 {
            constructions.insert(name, count);
        }
    }
    let expected = CONSTRUCTIONS
        .iter()
        .map(|&(name, count)| (name.to_owned(), count))
        .collect::<BTreeMap<_, _>>();
    assert_eq!(constructions, expected);
}

/// The protocol module builds the query from fixed constants. The setter
/// names its one ID, and both its type and its methods exist only with the
/// `device-mode-setters` feature or in the module's tests.
#[test]
fn the_device_mode_setter_is_gated_behind_its_feature() {
    let codec =
        fs::read_to_string(root().join("rust/crates/openblade-protocol/src/device_mode.rs"))
            .unwrap();
    assert!(codec.contains("const QUERY: u8 = 0x84;"));
    assert!(codec.contains("RazerReport::create(transaction, CLASS, QUERY, &QUERY_ARGUMENTS)"));
    assert_eq!(codec.matches("0x04").count(), 1);
    assert!(codec.contains("    const ID: u8 = 0x04;"));
    let gate = "#[cfg(any(test, feature = \"device-mode-setters\"))]\n";
    assert_eq!(codec.matches(gate).count(), 2);
    assert!(codec.contains(&format!("{gate}pub struct DeviceModeSet {{")));
    assert!(codec.contains(&format!("{gate}impl DeviceModeSet {{")));
    assert!(codec.contains("RazerReport::create(transaction, CLASS, Self::ID, &arguments)"));
    let tests = codec.split("#[cfg(test)]").skip(1).collect::<Vec<_>>();
    assert_eq!(tests, ["\nmod tests;\n"]);
}

/// Only the host's Driver-mode ownership feature enables the setters, and
/// only the Driver-mode write recipe imports them, under that feature.
#[test]
fn only_the_driver_mode_owner_enables_the_setters() {
    let root = root();
    let protocol =
        fs::read_to_string(root.join("rust/crates/openblade-protocol/Cargo.toml")).unwrap();
    assert!(protocol.contains("\ndevice-mode-setters = []\n"));
    let host = fs::read_to_string(root.join("tools/openblade-windows-host/Cargo.toml")).unwrap();
    let enabling = host
        .lines()
        .filter(|line| line.contains("device-mode-setters"))
        .collect::<Vec<_>>();
    assert_eq!(enabling.len(), 1, "{enabling:?}");
    assert!(enabling[0].starts_with("service-keyboard-driver-mode-v1 = ["));
    let recipe =
        fs::read_to_string(root.join(
            "tools/openblade-windows-host/src/discovery/hid_lease/performance/device_mode.rs",
        ))
        .unwrap();
    assert!(recipe.contains(
        "#[cfg(feature = \"service-keyboard-driver-mode-v1\")]\nuse openblade_protocol::device_mode::DeviceModeSet;\n"
    ));
    assert_eq!(recipe.matches("DeviceModeSet").count(), 2);
}

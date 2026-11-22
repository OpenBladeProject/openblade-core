//! The reviewed 00/04 keyboard device-mode setters exist only in the protocol
//! crate's tests. `RazerReport::create` is a public encoder, so no type can
//! stop other code building 00/04; this guard instead fails when non-test
//! source in the native Service's crates names the setter or adds a report
//! construction, so each new one is reviewed against the admitted commands.
use std::{
    collections::BTreeMap,
    fs,
    path::{Path, PathBuf},
};

/// Every non-test file of the host and the Rust crates it links that calls
/// `RazerReport::create`, with its call count. `openblade-ffi` is managed's
/// encoder and is not linked into the native Service.
const CONSTRUCTIONS: &[(&str, usize)] = &[
    ("rust/crates/openblade-protocol/src/battery.rs", 4),
    (
        "rust/crates/openblade-protocol/src/custom_performance.rs",
        2,
    ),
    // The 00/84 query alone.
    ("rust/crates/openblade-protocol/src/device_mode.rs", 1),
    ("rust/crates/openblade-protocol/src/fan.rs", 1),
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
    ("tools/openblade-windows-host/src/fan_runtime/device.rs", 2),
    (
        "tools/openblade-windows-host/src/performance_runtime/device.rs",
        2,
    ),
];

fn root() -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR")).join("../..")
}

fn is_test(path: &Path) -> bool {
    let name = path.file_name().unwrap().to_str().unwrap();
    name == "tests.rs"
        || name.ends_with("_tests.rs")
        || name.contains("fixture")
        || path.components().any(|part| {
            part.as_os_str().to_str().is_some_and(|part| {
                matches!(part, "tests" | "fixtures") || part.ends_with("_tests")
            })
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
fn no_non_test_source_names_the_setter_or_adds_a_report_construction() {
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
        assert!(!text.contains("DeviceModeSet"), "{name} names the setter");
        let count = text.matches("RazerReport::create(").count();
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

/// The protocol module builds the query from fixed constants and names no
/// setter ID; the setter and its acknowledgement live in its tests.
#[test]
fn the_device_mode_codec_builds_only_the_query() {
    let codec =
        fs::read_to_string(root().join("rust/crates/openblade-protocol/src/device_mode.rs"))
            .unwrap();
    assert!(!codec.contains("0x04"));
    assert!(codec.contains("const QUERY: u8 = 0x84;"));
    assert!(codec.contains("RazerReport::create(transaction, CLASS, QUERY, &QUERY_ARGUMENTS)"));
    let tests = codec.split("#[cfg(test)]").skip(1).collect::<Vec<_>>();
    assert_eq!(tests, ["\nmod tests;\n"]);
}

//! The reviewed 03/03, 03/00 and 03/02 keyboard lighting setters, the 03/0B
//! matrix frame encoder and the 03/0A handshake setter exist only behind the
//! protocol crate's `lighting-setters` feature, which only the host's
//! `service-lighting-v1` composition enables. As the Driver-mode guard
//! (`device_mode_guard_tests`) does for 00/04, this textual allowlist fails
//! when non-test source names a setter outside its codec and the one lighting
//! recipe, builds a lighting job outside its one owner, or changes the
//! commands the codec builds, so each change is reviewed against the commands
//! admitted for exact RZ09-0581. It does not see a name reached through a
//! glob import or a macro.
use std::{
    fs,
    path::{Path, PathBuf},
};

/// The only non-test files that may name a lighting setter: its codec, the
/// lighting recipe (`Reader::apply_lighting`), and the semantic admission,
/// which encodes each setter only to compare its bytes with the reviewed
/// manifest and never sends one (`lighting_admission`).
const SETTER_USERS: &[&str] = &[
    "rust/crates/openblade-protocol/src/lighting.rs",
    "tools/openblade-windows-host/src/discovery/hid_lease/performance/lighting.rs",
    "tools/openblade-windows-host/src/discovery/hid_lease/performance/lighting_admission.rs",
];

/// The only non-test files that may name the matrix frame encoder or the
/// 03/0A handshake setter: their codec and the semantic admission, which
/// encodes the known answer and the projection fingerprint and builds the
/// handshake only to compare it with the oracle.
const MATRIX_SETTER_USERS: &[&str] = &[
    "rust/crates/openblade-protocol/src/lighting/matrix.rs",
    "tools/openblade-windows-host/src/discovery/hid_lease/performance/lighting_admission.rs",
];

/// The matrix codec's setters, named whole.
const MATRIX_SETTERS: [&str; 2] = ["TransitionSet", "encode_feature_report"];

/// The only non-test file that may build a lighting job, and so ask the
/// recipe for a setter: the Service's `setLightingOverride` composition. The
/// recipe's module defines the constructors.
const JOB_USERS: &[&str] =
    &["tools/openblade-windows-host/src/windows/scm/installed/lighting_mutation.rs"];

/// Every lighting job constructor that can reach a setter or a store.
const JOB_CONSTRUCTORS: [&str; 3] = [
    "LightingJob::write(",
    "LightingJob::store(",
    "LightingJob::resolution(",
];

fn root() -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR")).join("../..")
}

/// Test modules by this repository's naming, as the Driver-mode guard reads
/// them.
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

/// Whether `text` names `ident` as a whole identifier (`LightingSettings`
/// is another type).
fn names(text: &str, ident: &str) -> bool {
    text.match_indices(ident).any(|(at, _)| {
        !text[at + ident.len()..].starts_with(|c: char| c.is_alphanumeric() || c == '_')
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
fn no_non_test_source_names_a_lighting_setter_or_builds_a_job_elsewhere() {
    let root = root();
    let mut files = Vec::new();
    sources(&root.join("tools/openblade-windows-host/src"), &mut files);
    for entry in fs::read_dir(root.join("rust/crates")).unwrap() {
        let krate = entry.unwrap().path();
        if krate.file_name().unwrap() != "openblade-ffi" && krate.join("src").is_dir() {
            sources(&krate.join("src"), &mut files);
        }
    }
    let mut setter_users = Vec::new();
    let mut matrix_users = Vec::new();
    let mut job_users = Vec::new();
    for file in &files {
        let text = fs::read_to_string(file).unwrap();
        let name = file
            .strip_prefix(&root)
            .unwrap()
            .to_str()
            .unwrap()
            .replace('\\', "/");
        assert!(
            !text.contains("LightingSet as "),
            "{name} renames the setter"
        );
        assert!(!text.contains("LightingJob as "), "{name} renames the job");
        if names(&text, "LightingSet") {
            setter_users.push(name.clone());
        }
        for setter in MATRIX_SETTERS {
            assert!(
                !text.contains(&format!("{setter} as ")),
                "{name} renames {setter}"
            );
        }
        if MATRIX_SETTERS.iter().any(|setter| names(&text, setter)) {
            matrix_users.push(name.clone());
        }
        if JOB_CONSTRUCTORS.iter().any(|built| text.contains(built)) {
            job_users.push(name);
        }
    }
    setter_users.sort();
    assert_eq!(setter_users, SETTER_USERS);
    matrix_users.sort();
    assert_eq!(matrix_users, MATRIX_SETTER_USERS);
    assert_eq!(job_users, JOB_USERS);
}

/// The codec builds every report through one fixed-length builder, from the
/// reviewed class, queries and setter IDs, and the setters and their type
/// exist only with the `lighting-setters` feature or in the module's tests.
#[test]
fn the_lighting_codec_builds_only_the_reviewed_commands_behind_its_feature() {
    let codec =
        fs::read_to_string(root().join("rust/crates/openblade-protocol/src/lighting.rs")).unwrap();
    for constant in [
        "const CLASS: u8 = 0x03;",
        "const BRIGHTNESS_QUERY: u8 = 0x83;",
        "const LOGO_STATE_QUERY: u8 = 0x80;",
        "const LOGO_EFFECT_QUERY: u8 = 0x82;",
        "    const BRIGHTNESS: u8 = 0x03;",
        "    const LOGO_STATE: u8 = 0x00;",
        "    const LOGO_EFFECT: u8 = 0x02;",
    ] {
        assert_eq!(codec.matches(constant).count(), 1, "{constant}");
    }
    // One builder, from the class, and every caller of it: the three queries
    // and the setter constructor.
    assert_eq!(codec.matches("RazerReport::create(").count(), 1);
    assert!(codec.contains("    RazerReport::create(transaction, CLASS, id, arguments)\n"));
    let ids = |call: &str| {
        codec
            .match_indices(call)
            .map(|(at, found)| {
                let id = &codec[at + found.len()..];
                id[..id.find(',').unwrap()].to_owned()
            })
            .collect::<Vec<_>>()
    };
    assert_eq!(
        ids("report(transaction, "),
        [
            "BRIGHTNESS_QUERY",
            "LOGO_STATE_QUERY",
            "LOGO_EFFECT_QUERY",
            "id"
        ]
    );
    assert_eq!(
        ids("Self::new(transaction, Self::"),
        ["BRIGHTNESS", "LOGO_STATE", "LOGO_EFFECT"]
    );
    let gate = "#[cfg(any(test, feature = \"lighting-setters\"))]\n";
    assert_eq!(codec.matches(gate).count(), 2);
    assert!(codec.contains(&format!("{gate}pub struct LightingSet {{")));
    assert!(codec.contains(&format!("{gate}impl LightingSet {{")));
    let tests = codec.split("#[cfg(test)]").skip(1).collect::<Vec<_>>();
    assert_eq!(tests, ["\nmod tests;\n"]);
}

/// The matrix codec builds the 00/86 query and the 03/0A:0500 setter from
/// the reviewed IDs and encodes only the 03/0B frame, and the frame encoder
/// and the setter exist only with `lighting-setters` or in its tests.
#[test]
fn the_matrix_codec_builds_only_the_reviewed_commands_behind_its_feature() {
    let codec =
        fs::read_to_string(root().join("rust/crates/openblade-protocol/src/lighting/matrix.rs"))
            .unwrap();
    for constant in [
        "pub const MATRIX_CLASS: u8 = 0x03;",
        "pub const MATRIX_COMMAND: u8 = 0x0B;",
        "    const CLASS: u8 = 0x00;
    const ID: u8 = 0x86;",
        "    const CLASS: u8 = 0x03;
    const ID: u8 = 0x0a;
    const ARGUMENTS: [u8; 2] = [0x05, 0x00];",
    ] {
        assert_eq!(codec.matches(constant).count(), 1, "{constant}");
    }
    assert_eq!(codec.matches("RazerReport::create(").count(), 2);
    assert!(codec.contains("RazerReport::create(transaction, Self::CLASS, Self::ID, &[0; 3])"));
    assert!(
        codec.contains("RazerReport::create(transaction, Self::CLASS, Self::ID, &Self::ARGUMENTS)")
    );
    let gate = "#[cfg(any(test, feature = \"lighting-setters\"))]
";
    assert_eq!(codec.matches(gate).count(), 3);
    assert!(codec.contains(&format!("{gate}pub fn encode_feature_report(")));
    assert!(codec.contains(&format!("{gate}pub struct TransitionSet {{")));
    assert!(codec.contains(&format!("{gate}impl TransitionSet {{")));
    let tests = codec.split("#[cfg(test)]").skip(1).collect::<Vec<_>>();
    assert_eq!(
        tests,
        ["
mod tests;
"]
    );
}

/// Only the host's lighting composition enables the setters.
#[test]
fn only_the_lighting_composition_enables_the_setters() {
    let root = root();
    let protocol =
        fs::read_to_string(root.join("rust/crates/openblade-protocol/Cargo.toml")).unwrap();
    assert!(protocol.contains("\nlighting-setters = []\n"));
    let host = fs::read_to_string(root.join("tools/openblade-windows-host/Cargo.toml")).unwrap();
    let enabling = host
        .lines()
        .filter(|line| line.contains("lighting-setters"))
        .collect::<Vec<_>>();
    assert_eq!(enabling.len(), 1, "{enabling:?}");
    assert!(enabling[0].starts_with("service-lighting-v1 = ["));
}

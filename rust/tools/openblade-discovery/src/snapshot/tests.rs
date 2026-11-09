use super::*;
use serde_json::{Value, json};
use std::{fs, path::PathBuf};

fn assert_fixture(snapshot: Snapshot, json: &str) {
    assert_eq!(
        serde_json::to_value(snapshot).unwrap(),
        serde_json::from_str::<Value>(json).unwrap()
    );
}

#[test]
fn macos_platform_identity_cannot_resolve_hid_to_exact_model() {
    let hid = Hid::from_entries([Entry::Irrelevant, Entry::Candidate(0x0255)]);
    assert_fixture(
        Snapshot::new(Platform::Macos, HostMetadata::Unverified, hid),
        include_str!("../../fixtures/macos-unverified.json"),
    );
}

#[test]
fn scan_distinguishes_repeated_ids_and_incomplete_enumeration() {
    let repeated = Hid::from_entries([Entry::Candidate(0x0255), Entry::Candidate(0x0255)]);
    assert_eq!(
        serde_json::to_value(repeated).unwrap()["state"],
        "ambiguous"
    );
    let mut consumed = 0;
    let entries = std::iter::from_fn(|| {
        consumed += 1;
        Some(Entry::Candidate(0x0255))
    });
    let capped = serde_json::to_value(Hid::from_entries(entries)).unwrap();
    assert_eq!(consumed, MAX_ENTRIES + 1);
    assert_eq!(capped["state"], "incomplete");
    assert_eq!(capped["candidates"][0]["entries"], MAX_ENTRIES);
    let exact = Hid::from_entries(std::iter::repeat_with(|| Entry::Irrelevant).take(MAX_ENTRIES));
    assert_eq!(serde_json::to_value(exact).unwrap()["state"], "none");
}

struct Sysfs(PathBuf);

impl Sysfs {
    fn new(case: &str) -> Self {
        let path =
            std::env::temp_dir().join(format!("openblade-discovery-{}-{case}", std::process::id()));
        fs::create_dir(&path).unwrap();
        Self(path)
    }

    fn write(&self, path: &str, contents: impl AsRef<[u8]>) {
        let path = self.0.join(path);
        fs::create_dir_all(path.parent().unwrap()).unwrap();
        fs::write(path, contents).unwrap();
    }

    fn snapshot(&self) -> Snapshot {
        crate::linux::discover(&self.0)
    }
}

impl Drop for Sysfs {
    fn drop(&mut self) {
        let _ = fs::remove_dir_all(&self.0);
    }
}

#[test]
fn linux_projects_sorted_candidates_and_omits_other_buses_and_vendors() {
    let sys = Sysfs::new("ambiguous");
    sys.write("class/dmi/id/sys_vendor", b"Razer\n");
    sys.write("class/dmi/id/product_name", b"Blade 15 Base (synthetic)\n");
    for (name, alias) in [
        ("z", "hid:b0003g0001v00001532p00000255\n"),
        ("x", "hid:b0003g0001v00001532p000002E0\n"),
        ("y", "hid:b0003g0001v00001532p00000255\n"),
        ("other-bus", "hid:b0005g0001v00001532p00000255\n"),
        ("other-vendor", "hid:b0003g0001v0000046Dp00000255\n"),
    ] {
        sys.write(&format!("bus/hid/devices/{name}/modalias"), alias);
    }
    assert_fixture(
        sys.snapshot(),
        include_str!("../../fixtures/linux-ambiguous.json"),
    );
}

#[test]
fn linux_distinguishes_no_candidates_from_missing_sysfs() {
    let sys = Sysfs::new("absent");
    let empty = serde_json::to_value(sys.snapshot()).unwrap();
    assert_eq!(empty["hostMetadata"], "unavailable");
    assert_eq!(
        empty["hid"],
        json!({"state": "unavailable", "candidates": []})
    );

    fs::create_dir_all(sys.0.join("bus/hid/devices")).unwrap();
    sys.write("class/dmi/id/sys_vendor", b"Synthetic OEM\n");
    // A Blade-like model string alone must not change the host classification.
    sys.write("class/dmi/id/product_name", b"Razer Blade 15 Base\n");
    assert_fixture(
        sys.snapshot(),
        include_str!("../../fixtures/linux-no-candidates.json"),
    );
}

#[test]
fn linux_does_not_promote_malformed_or_partial_metadata() {
    let sys = Sysfs::new("malformed");
    sys.write("class/dmi/id/sys_vendor", [b'X'; MAX_HOST_TEXT + 2]);
    sys.write("class/dmi/id/product_name", b"Razer Blade 15 Base\n");
    sys.write(
        "bus/hid/devices/valid/modalias",
        b"hid:b0003g0001v00001532p00000255\n",
    );
    sys.write(
        "bus/hid/devices/invalid-width/modalias",
        b"hid:b0003g0001v00001532p00010255\n",
    );
    sys.write(
        "bus/hid/devices/invalid-hex/modalias",
        b"hid:b0003g0001v00001532p+0000255\n",
    );
    sys.write(
        "bus/hid/devices/truncated/modalias",
        b"hid:b0003g0001v00001532p00000255",
    );
    sys.write(
        "bus/hid/devices/over-limit/modalias",
        b"hid:b0003g0001v00001532p00000255\nextra",
    );
    fs::create_dir(sys.0.join("bus/hid/devices/missing-attribute")).unwrap();
    assert_fixture(
        sys.snapshot(),
        include_str!("../../fixtures/linux-incomplete.json"),
    );

    sys.write("class/dmi/id/sys_vendor", b"Razer Inc.\n");
    sys.write("class/dmi/id/product_name", b"Blade 15 Base\0private\n");
    assert_eq!(
        serde_json::to_value(sys.snapshot()).unwrap()["hostMetadata"],
        "unverified"
    );
}

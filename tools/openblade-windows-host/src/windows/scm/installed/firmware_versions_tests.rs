//! The firmware-version capture over injected Windows reads, its schedule on
//! a real worker thread, and its publication through the real ReadSource with
//! a fake fan controller behind the Port. No test calls a Windows API.
use super::super::driver_mode_rig::{Custody, status};
use super::super::*;
use super::{
    BIOS_KEY, Capture, DEADLINE, HidInterface, HidPath, Host, Part, Router, UNAVAILABLE, Value,
    VideoInstance, capture, ec_version, format_text, is_touchpad_interface, is_version,
    parse_int32, unavailable,
};
use crate::discovery::performance::fan_rpm_tests::{FanIo, fan_io_worker};
use crate::{discovery::catalog_model, service_status::FirmwareVersions};
use serde_json::{Value as Json, json};
use std::{
    collections::HashMap,
    io,
    sync::{
        Mutex,
        atomic::{AtomicUsize, Ordering},
        mpsc::{Receiver, Sender, channel},
    },
    time::Duration,
};

const ALL: u8 = 15;
const ROUTER: &str = r"USB4\ROOT_DEVICE_ROUTER&VID_8086&PID_5781";
const ROUTER_ID: &str = r"USB4\ROOT_DEVICE_ROUTER&VID_8086&PID_5781&REV_0084";
const PARENT: &str = r"PCI\VEN_8086&DEV_5781&SUBSYS_30101A58&REV_84&USB4_MS_CM";

fn text(value: &str) -> Value {
    Value::Text(value.into())
}

/// A held read: the worker reports it entered, then waits for the release.
struct Gate {
    entered: Mutex<Sender<()>>,
    release: Mutex<Receiver<()>>,
}

/// The test's side of a `Gate`.
struct Hold {
    entered: Receiver<()>,
    release: Sender<()>,
}

#[derive(Clone, Copy, PartialEq)]
enum Stage {
    Video,
    Router,
    Touchpad,
}

/// Windows as a test describes it. A source left None fails its read, and
/// every read is counted.
#[derive(Default)]
struct Fake {
    registry: HashMap<&'static str, Value>,
    registry_fails: bool,
    video: Option<Vec<Vec<VideoInstance>>>,
    routers: Option<Vec<Router>>,
    /// The touchpad's collections: each gets the interface path and hardware
    /// IDs of a USB touchpad, unless `raw_hid` is set.
    interfaces: Option<Vec<HidInterface>>,
    /// Every HID interface as Windows lists it, with what opening it reports.
    raw_hid: Option<Vec<(HidPath, Option<HidInterface>)>>,
    /// The paths whose attributes were read, which is to say opened.
    opened: Mutex<Vec<String>>,
    gate: Option<(Stage, Gate)>,
    registry_reads: AtomicUsize,
    video_reads: AtomicUsize,
    router_reads: AtomicUsize,
    interface_reads: AtomicUsize,
}

impl Fake {
    /// A healthy RZ09-0581.
    fn healthy() -> Self {
        let mut fake = Self::default();
        fake.registry.extend([
            ("SystemSKU", text("RZ09-0581EU")),
            ("BIOSVersion", text(" 3.01 ")),
            ("ECFirmwareMajorRelease", Value::Number(1)),
            ("ECFirmwareMinorRelease", Value::Number(4)),
        ]);
        // Two instances of one adapter that differ only in case and padding.
        fake.video = Some(vec![vec![
            VideoInstance {
                adapter: text("NVIDIA GeForce RTX 5090 Laptop GPU"),
                bios: text("Version 98.03.5c.40.3d"),
            },
            VideoInstance {
                adapter: text(" nvidia geforce rtx 5090 laptop gpu "),
                bios: text("version 98.03.5C.40.3D"),
            },
        ]]);
        fake.routers = Some(vec![router("47.0")]);
        fake.interfaces = Some(vec![touchpad(0x0123)]);
        fake
    }

    fn held(mut self, stage: Stage) -> (Self, Hold) {
        let (entered, entered_receiver) = channel();
        let (release_sender, release) = channel();
        self.gate = Some((
            stage,
            Gate {
                entered: Mutex::new(entered),
                release: Mutex::new(release),
            },
        ));
        (
            self,
            Hold {
                entered: entered_receiver,
                release: release_sender,
            },
        )
    }

    fn pass(&self, stage: Stage) {
        if let Some((held, gate)) = &self.gate
            && *held == stage
        {
            let _ = gate.entered.lock().unwrap().send(());
            // A dropped Hold also releases.
            let _ = gate.release.lock().unwrap().recv();
        }
    }

    fn reads(&self) -> [usize; 4] {
        [
            &self.registry_reads,
            &self.video_reads,
            &self.router_reads,
            &self.interface_reads,
        ]
        .map(|count| count.load(Ordering::SeqCst))
    }
}

/// A source that says when the worker that held it has ended: the host gives
/// its handle to the worker, so the last drop is the worker's.
struct Probe {
    fake: Arc<Fake>,
    ended: Mutex<Sender<()>>,
}

impl Probe {
    fn new(fake: Fake) -> (Arc<Fake>, Arc<Self>, Receiver<()>) {
        let (ended, signal) = channel();
        let fake = Arc::new(fake);
        let probe = Arc::new(Self {
            fake: fake.clone(),
            ended: Mutex::new(ended),
        });
        (fake, probe, signal)
    }
}

impl Drop for Probe {
    fn drop(&mut self) {
        let _ = self.ended.lock().unwrap().send(());
    }
}

impl Capture for Probe {
    fn registry(&self, key: &str, name: &str) -> io::Result<Value> {
        self.fake.registry(key, name)
    }
    fn video_adapters(&self) -> io::Result<Vec<Vec<VideoInstance>>> {
        self.fake.video_adapters()
    }
    fn thunderbolt_routers(&self) -> io::Result<Vec<Router>> {
        self.fake.thunderbolt_routers()
    }
    fn hid_paths(&self) -> io::Result<Vec<HidPath>> {
        self.fake.hid_paths()
    }
    fn hid_attributes(&self, path: &str) -> Option<HidInterface> {
        self.fake.hid_attributes(path)
    }
}

fn failure() -> io::Error {
    io::ErrorKind::PermissionDenied.into()
}

impl Capture for Fake {
    fn registry(&self, key: &str, name: &str) -> io::Result<Value> {
        assert_eq!(key, BIOS_KEY);
        self.registry_reads.fetch_add(1, Ordering::SeqCst);
        if self.registry_fails {
            return Err(failure());
        }
        Ok(self.registry.get(name).cloned().unwrap_or(Value::Missing))
    }
    fn video_adapters(&self) -> io::Result<Vec<Vec<VideoInstance>>> {
        self.video_reads.fetch_add(1, Ordering::SeqCst);
        self.pass(Stage::Video);
        self.video.clone().ok_or_else(failure)
    }
    fn thunderbolt_routers(&self) -> io::Result<Vec<Router>> {
        self.router_reads.fetch_add(1, Ordering::SeqCst);
        self.pass(Stage::Router);
        self.routers.clone().ok_or_else(failure)
    }
    fn hid_paths(&self) -> io::Result<Vec<HidPath>> {
        self.interface_reads.fetch_add(1, Ordering::SeqCst);
        self.pass(Stage::Touchpad);
        if let Some(raw) = &self.raw_hid {
            return Ok(raw.iter().map(|(path, _)| path.clone()).collect());
        }
        let interfaces = self.interfaces.as_ref().ok_or_else(failure)?;
        Ok(interfaces
            .iter()
            .enumerate()
            .map(|(index, interface)| HidPath {
                path: format!(r"\\?\hid#vid_093a&pid_3001&col{index:02}"),
                hardware_ids: vec![format!(
                    r"HID\VID_{:04X}&UP:{:04X}_U:{:04X}",
                    interface.vendor_id, interface.usage_page, interface.usage
                )],
            })
            .collect())
    }
    fn hid_attributes(&self, path: &str) -> Option<HidInterface> {
        self.opened.lock().unwrap().push(path.to_owned());
        if let Some(raw) = &self.raw_hid {
            return raw
                .iter()
                .find(|(listed, _)| listed.path == path)
                .and_then(|(_, attributes)| *attributes);
        }
        let index: usize = path.rsplit("col").next()?.parse().ok()?;
        self.interfaces.as_ref()?.get(index).copied()
    }
}

fn router(firmware: &str) -> Router {
    Router {
        instance_id: format!(r"{ROUTER}\6&2a4b1c&0&0"),
        firmware_version: Some(firmware.into()),
        hardware_ids: vec![ROUTER_ID.into(), ROUTER.into()],
        service: Some("Usb4DeviceRouter".into()),
        parent: Some(format!(r"{PARENT}\3&11583659&0&D0")),
    }
}

fn touchpad(version: u16) -> HidInterface {
    HidInterface {
        vendor_id: 0x093a,
        product_id: 0x3001,
        usage_page: 0x0d,
        usage: 0x05,
        version,
    }
}

/// The record a capture reports, read synchronously.
fn captured(fake: &Fake) -> FirmwareVersions {
    let mut record = unavailable();
    capture(fake, |part| {
        part.apply(&mut record);
        true
    });
    record
}

fn healthy_record() -> FirmwareVersions {
    FirmwareVersions {
        bios_version: "3.01".into(),
        ec_version: "1.04".into(),
        mcu_version: UNAVAILABLE.into(),
        vbios_version: "98.03.5c.40.3d".into(),
        model_number: "RZ09-0581".into(),
        vbios_gpu_name: "NVIDIA GeForce RTX 5090 Laptop GPU".into(),
        thunderbolt_firmware_version: "47.0".into(),
        touchpad_firmware_version: "1.23".into(),
        thunderbolt_firmware_access: Some(1),
        touchpad_firmware_access: Some(1),
    }
}

/// Polls until the capture is done, with a clock that never reaches the deadline.
fn settle(host: &Host) -> FirmwareVersions {
    let now = Instant::now();
    for _ in 0..5_000 {
        if let Some(record) = host.poll(now) {
            return record;
        }
        std::thread::sleep(Duration::from_millis(1));
    }
    panic!("the capture did not finish");
}

// --- Capture ---

/// The managed record of a healthy RZ09-0581: every part read, the model
/// canonicalized to the catalog's, trimmed text, the EC minor with two digits,
/// the adapter's duplicate instance counted once, the VBIOS without its
/// "Version" prefix, and McuVersion left for the control interface.
#[test]
fn a_healthy_rz09_0581_is_the_managed_record() {
    let fake = Fake::healthy();
    assert_eq!(captured(&fake), healthy_record());
    assert_eq!(fake.reads(), [4, 1, 1, 1]);
}

/// McuVersion is managed's firmware query over the control interface, which
/// the read job sends (`firmware_versions_mcu_tests`), never this capture:
/// whatever Windows reports, the capture leaves it Unavailable.
#[test]
fn the_capture_never_reads_the_mcu_version() {
    let mut fake = Fake::healthy();
    for name in ["MCUVersion", "McuVersion", "MCU"] {
        fake.registry.insert(name, text("1.2.3.4"));
    }
    assert_eq!(captured(&fake).mcu_version, UNAVAILABLE);
    assert_eq!(fake.reads()[0], 4, "only the four managed registry values");
}

/// Each source that fails or is missing makes only its own parts
/// `Unavailable`.
#[test]
fn a_failing_source_is_unavailable_for_its_parts_only() {
    let healthy = healthy_record();
    let unavailable_text = |record: &mut FirmwareVersions, parts: &[&str]| {
        for part in parts {
            let field = match *part {
                "bios" => &mut record.bios_version,
                "ec" => &mut record.ec_version,
                "vbios" => &mut record.vbios_version,
                "gpu" => &mut record.vbios_gpu_name,
                "model" => &mut record.model_number,
                "thunderbolt" => &mut record.thunderbolt_firmware_version,
                "touchpad" => &mut record.touchpad_firmware_version,
                other => unreachable!("{other}"),
            };
            *field = UNAVAILABLE.into();
        }
    };

    let mut fake = Fake::healthy();
    fake.video = None;
    let mut expect = healthy.clone();
    unavailable_text(&mut expect, &["vbios", "gpu"]);
    assert_eq!(captured(&fake), expect, "video registry");

    let mut fake = Fake::healthy();
    fake.routers = None;
    let mut expect = healthy.clone();
    unavailable_text(&mut expect, &["thunderbolt"]);
    assert_eq!(captured(&fake), expect, "USB4 enumeration");

    let mut fake = Fake::healthy();
    fake.interfaces = None;
    let mut expect = healthy.clone();
    unavailable_text(&mut expect, &["touchpad"]);
    assert_eq!(captured(&fake), expect, "HID enumeration");

    // With no BIOS key the model is unknown, so the device reads and their
    // access are skipped, as managed skips them for any other model.
    let mut fake = Fake::healthy();
    fake.registry_fails = true;
    let mut expect = healthy.clone();
    unavailable_text(
        &mut expect,
        &["bios", "ec", "model", "thunderbolt", "touchpad"],
    );
    expect.thunderbolt_firmware_access = None;
    expect.touchpad_firmware_access = None;
    assert_eq!(captured(&fake), expect, "registry");
    assert_eq!(fake.reads()[2..], [0, 0]);

    // One registry value does not affect another.
    for (name, parts) in [
        ("BIOSVersion", &["bios"][..]),
        ("ECFirmwareMajorRelease", &["ec"]),
        ("ECFirmwareMinorRelease", &["ec"]),
    ] {
        let mut fake = Fake::healthy();
        fake.registry.remove(name);
        let mut expect = healthy.clone();
        unavailable_text(&mut expect, parts);
        assert_eq!(captured(&fake), expect, "{name}");
    }
}

/// Every registry value that is not the managed type is `Unavailable`, and a
/// numeric EC value is an int that is a byte.
#[test]
fn a_registry_value_of_the_wrong_type_is_unavailable() {
    for wrong in [
        Value::Other,
        Value::Number(3),
        Value::Missing,
        text("  "),
        Value::Multi(vec![]),
        Value::Multi(vec![" ".into()]),
    ] {
        assert_eq!(format_text(&wrong), UNAVAILABLE, "{wrong:?}");
    }
    assert_eq!(
        format_text(&Value::Multi(vec!["A ".into(), " ".into(), "B".into()])),
        "A  B"
    );
    assert_eq!(format_text(&text(" A B ")), "A B");

    for (major, minor, expect) in [
        (Value::Number(1), Value::Number(4), "1.04"),
        (Value::Number(255), Value::Number(255), "255.255"),
        (Value::Number(0), Value::Number(0), "0.00"),
        (text(" 12 "), Value::Number(7), "12.07"),
        (Value::Number(256), Value::Number(1), UNAVAILABLE),
        (Value::Number(-1), Value::Number(1), UNAVAILABLE),
        (Value::Number(1 << 32), Value::Number(1), UNAVAILABLE),
        (Value::Number(1), Value::Number(256), UNAVAILABLE),
        (text("x"), Value::Number(1), UNAVAILABLE),
        (text("1.5"), Value::Number(1), UNAVAILABLE),
        (Value::Other, Value::Number(1), UNAVAILABLE),
        (
            Value::Multi(vec!["1".into()]),
            Value::Number(1),
            UNAVAILABLE,
        ),
        (Value::Missing, Value::Number(1), UNAVAILABLE),
        (Value::Number(1), Value::Missing, UNAVAILABLE),
    ] {
        assert_eq!(ec_version(&major, &minor), expect, "{major:?} {minor:?}");
    }

    // Through a capture, a wrong-typed value affects its own part only.
    let mut fake = Fake::healthy();
    fake.registry.insert("BIOSVersion", Value::Other);
    fake.registry.insert("ECFirmwareMinorRelease", Value::Other);
    let record = captured(&fake);
    assert_eq!(record.bios_version, UNAVAILABLE);
    assert_eq!(record.ec_version, UNAVAILABLE);
    assert_eq!(record.model_number, "RZ09-0581");
    assert_eq!(record.vbios_version, "98.03.5c.40.3d");
    assert_eq!(record.touchpad_firmware_version, "1.23");
}

/// The video instance needs both values to be text, and exactly one NVIDIA
/// adapter must report a non-blank version.
#[test]
fn the_vbios_needs_one_nvidia_adapter_with_text_values() {
    let instance = |adapter: Value, bios: Value| VideoInstance { adapter, bios };
    let nvidia = || text("NVIDIA GeForce RTX 5090 Laptop GPU");
    for (adapters, expect) in [
        (
            vec![vec![instance(nvidia(), text("Version 1.2"))]],
            Some("1.2"),
        ),
        // Another adapter does not matter.
        (
            vec![
                vec![instance(
                    text("Intel(R) Arc(TM) Graphics"),
                    text("Version 9"),
                )],
                vec![instance(nvidia(), text("98.03"))],
            ],
            Some("98.03"),
        ),
        // The wrong type of either value skips the instance.
        (vec![vec![instance(nvidia(), Value::Other)]], None),
        (vec![vec![instance(Value::Other, text("1.2"))]], None),
        (
            vec![vec![instance(nvidia(), Value::Multi(vec!["1.2".into()]))]],
            None,
        ),
        (vec![vec![instance(nvidia(), Value::Missing)]], None),
        // A blank version, or one that is only the prefix, has none.
        (vec![vec![instance(nvidia(), text("  "))]], None),
        (vec![vec![instance(nvidia(), text("Version "))]], None),
        // Two NVIDIA adapters, or none, are ambiguous.
        (
            vec![
                vec![instance(nvidia(), text("1.2"))],
                vec![instance(nvidia(), text("3.4"))],
            ],
            None,
        ),
        (vec![vec![instance(text("Intel"), text("1.2"))]], None),
        (vec![], None),
        // Distinct versions of one adapter are two candidates; equal ones are one.
        (
            vec![vec![
                instance(nvidia(), text("1.2")),
                instance(nvidia(), text("3.4")),
            ]],
            None,
        ),
        (
            vec![vec![
                instance(nvidia(), text("1.2")),
                instance(nvidia(), text(" 1.2 ")),
            ]],
            Some("1.2"),
        ),
        // The same strings under two adapter keys are two candidates.
        (
            vec![
                vec![instance(nvidia(), text("1.2"))],
                vec![instance(nvidia(), text("1.2"))],
            ],
            None,
        ),
    ] {
        let mut fake = Fake::healthy();
        fake.video = Some(adapters.clone());
        assert_eq!(
            captured(&fake).vbios_version,
            expect.unwrap_or(UNAVAILABLE),
            "{adapters:?}"
        );
    }
}

/// Thunderbolt needs exactly one router with the exact instance, hardware ID,
/// service and parent, and a firmware version that parses as one.
#[test]
fn thunderbolt_needs_one_exact_router_and_a_version() {
    let with = |change: &dyn Fn(&mut Router)| {
        let mut router = router("47.0");
        change(&mut router);
        router
    };
    let cases: Vec<(Vec<Router>, Option<&str>)> = vec![
        (vec![router("47.0")], Some("47.0")),
        (vec![router(" 47.0.1 ")], Some("47.0.1")),
        (vec![router("1.2.3.4")], Some("1.2.3.4")),
        (vec![router("47")], None),
        (vec![router("1.2.3.4.5")], None),
        (vec![router("v47.0")], None),
        (vec![router("-1.0")], None),
        (vec![router("")], None),
        (vec![], None),
        (vec![router("47.0"), router("48.0")], None),
        // A router that managed's enumeration omits is not counted.
        (
            vec![router("47.0"), with(&|r| r.firmware_version = None)],
            Some("47.0"),
        ),
        (
            vec![
                router("47.0"),
                with(&|r| r.firmware_version = Some(" ".into())),
            ],
            Some("47.0"),
        ),
        (
            vec![router("47.0"), with(&|r| r.hardware_ids.clear())],
            Some("47.0"),
        ),
        (
            vec![router("47.0"), with(&|r| r.service = None)],
            Some("47.0"),
        ),
        (
            vec![router("47.0"), with(&|r| r.parent = None)],
            Some("47.0"),
        ),
        // A present router that is not the exact one does not count either.
        (
            vec![
                router("47.0"),
                with(&|r| r.service = Some("OtherService".into())),
            ],
            Some("47.0"),
        ),
        (vec![with(&|r| r.hardware_ids = vec![ROUTER.into()])], None),
        (
            vec![with(&|r| {
                r.parent = Some(r"PCI\VEN_8086&DEV_0000\1".into())
            })],
            None,
        ),
        (
            vec![with(&|r| {
                r.instance_id = r"USB4\ROOT_DEVICE_ROUTER&VID_8086&PID_0000\1".into()
            })],
            None,
        ),
        (
            vec![with(&|r| r.service = Some("usb4devicerouter".into()))],
            Some("47.0"),
        ),
    ];
    for (routers, expect) in cases {
        let mut fake = Fake::healthy();
        fake.routers = Some(routers.clone());
        assert_eq!(
            captured(&fake).thunderbolt_firmware_version,
            expect.unwrap_or(UNAVAILABLE),
            "{routers:?}"
        );
    }
}

/// The touchpad needs exactly one collection of its device and usage whose
/// version number is a non-zero BCD pair; any other format is `Unavailable`.
#[test]
fn the_touchpad_needs_one_collection_and_a_bcd_version() {
    let usage = |usage_page, usage| HidInterface {
        usage_page,
        usage,
        ..touchpad(0x0123)
    };
    let cases: Vec<(Vec<HidInterface>, Option<&str>)> = vec![
        (vec![touchpad(0x0123)], Some("1.23")),
        (vec![touchpad(0x9999)], Some("99.99")),
        (vec![touchpad(0x0001)], Some("0.01")),
        (vec![touchpad(0x1000)], Some("10.00")),
        (vec![touchpad(0)], None),
        // A nibble above 9 is not BCD, in either byte.
        (vec![touchpad(0x0A23)], None),
        (vec![touchpad(0x012B)], None),
        (vec![touchpad(0xFFFF)], None),
        (vec![], None),
        (vec![touchpad(0x0123), touchpad(0x0124)], None),
        // Other collections of the device or other usages are not it.
        (vec![touchpad(0x0123), usage(0x01, 0x02)], Some("1.23")),
        (vec![usage(0x0d, 0x04)], None),
        (vec![usage(0x01, 0x05)], None),
        (
            vec![
                touchpad(0x0123),
                HidInterface {
                    vendor_id: 0x1532,
                    ..touchpad(0x0456)
                },
                HidInterface {
                    product_id: 0x3002,
                    ..touchpad(0x0456)
                },
            ],
            Some("1.23"),
        ),
    ];
    for (interfaces, expect) in cases {
        let mut fake = Fake::healthy();
        fake.interfaces = Some(interfaces.clone());
        assert_eq!(
            captured(&fake).touchpad_firmware_version,
            expect.unwrap_or(UNAVAILABLE),
            "{interfaces:?}"
        );
    }
}

/// The two device reads and their access belong to the exact RZ09-0581. Any
/// other model, a model the catalog does not know, or no model, skips them.
#[test]
fn only_the_exact_target_model_reads_the_devices() {
    for (sku, model, target) in [
        ("RZ09-0581", "RZ09-0581", true),
        ("rz09-0581-abc", "RZ09-0581", true),
        ("  RZ09-0581  ", "RZ09-0581", true),
        ("RZ09-0528-EN", "RZ09-0528", false),
        ("RZ09-0482", "RZ09-0482", false),
        ("RZ09-0000", "RZ09-0000", false),
        ("RZ09-058", "RZ09-058", false),
        ("Z09-0581", "Z09-0581", false),
    ] {
        let mut fake = Fake::healthy();
        fake.registry.insert("SystemSKU", text(sku));
        let record = captured(&fake);
        assert_eq!(record.model_number, model, "{sku}");
        assert_eq!(fake.reads()[2..], [usize::from(target); 2], "{sku}");
        let (version, access) = (target.then_some("1.23"), target.then_some(1));
        assert_eq!(
            record.touchpad_firmware_version,
            version.unwrap_or(UNAVAILABLE),
            "{sku}"
        );
        assert_eq!(record.touchpad_firmware_access, access, "{sku}");
        assert_eq!(record.thunderbolt_firmware_access, access, "{sku}");
    }
    // The catalog is the discovery inventory, with the target first.
    assert_eq!(catalog_model("RZ09-0581x").unwrap(), Some("RZ09-0581"));
    assert_eq!(catalog_model("RZ09-0528").unwrap(), Some("RZ09-0528"));
    assert_eq!(catalog_model("RZ09-9999").unwrap(), None);
}

/// The capture stops reading once the host stops listening.
#[test]
fn the_capture_stops_when_nobody_listens() {
    let fake = Fake::healthy();
    let mut parts = Vec::new();
    capture(&fake, |part| {
        parts.push(part);
        false
    });
    assert_eq!(fake.reads()[2..], [0, 0]);
    assert!(matches!(parts[..], [Part::Registry { .. }]));
}

/// Each report is a stop point: a reporter that takes the registry part and
/// refuses the Thunderbolt part stops before the touchpad read.
#[test]
fn the_capture_stops_when_a_later_part_is_refused() {
    let fake = Fake::healthy();
    let mut reported = 0;
    capture(&fake, |_| {
        reported += 1;
        reported < 2
    });
    assert_eq!(reported, 2);
    assert_eq!(fake.reads(), [4, 1, 1, 0]);
}

const I2C_TOUCHPAD: &str =
    r"\\?\HID#PIXA3854&Col02#5&3b4bd4b4&0&0001#{4d1e55b2-f16f-11cf-88cb-001111000030}";
const I2C_MOUSE: &str =
    r"\\?\HID#PIXA3854&Col01#5&3b4bd4b4&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
const USB_TOUCHPAD: &str =
    r"\\?\HID#VID_093A&PID_3001&Col02#7&1c2b3a4d&0&0001#{4d1e55b2-f16f-11cf-88cb-001111000030}";
const CONTROL: &str =
    r"\\?\HID#VID_1532&PID_02E0&MI_02&Col01#8&2f4e1a&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";

fn hid(path: &str, ids: &[&str]) -> HidPath {
    HidPath {
        path: path.into(),
        hardware_ids: ids.iter().map(|id| (*id).to_owned()).collect(),
    }
}

/// The hardware IDs hidclass reads on the RZ09-0581 for the touchpad's
/// top-level collection. The touchpad is HID over I2C (ACPI PIXA3854), so the
/// interface path does not name 093A:3001; only the vendor and usage ID does.
fn i2c_touchpad() -> HidPath {
    hid(
        I2C_TOUCHPAD,
        &[
            r"HID\VEN_PIXA&DEV_3854&Col02",
            r"HID\PIXA3854&Col02",
            r"HID\*PIXA3854&Col02",
            r"HID\VID_093A&UP:000D_U:0005",
            "HID_DEVICE_UP:000D_U:0005",
            "HID_DEVICE",
        ],
    )
}

/// The same device's mouse collection: its vendor and another usage.
fn i2c_mouse() -> HidPath {
    hid(
        I2C_MOUSE,
        &[
            r"HID\VEN_PIXA&DEV_3854&Col01",
            r"HID\PIXA3854&Col01",
            r"HID\*PIXA3854&Col01",
            r"HID\VID_093A&UP:0001_U:0002",
            "HID_DEVICE_UP:0001_U:0002",
            "HID_DEVICE",
        ],
    )
}

fn usb_touchpad() -> HidPath {
    hid(
        USB_TOUCHPAD,
        &[
            r"HID\VID_093A&PID_3001&REV_0123&Col02",
            r"HID\VID_093A&PID_3001&Col02",
            r"HID\VID_093A&UP:000D_U:0005",
            "HID_DEVICE_UP:000D_U:0005",
            "HID_DEVICE",
        ],
    )
}

/// One of the laptop's own control collections, which must never be opened.
fn control() -> HidPath {
    hid(
        CONTROL,
        &[
            r"HID\VID_1532&PID_02E0&REV_0200&MI_02&Col01",
            r"HID\VID_1532&PID_02E0&MI_02&Col01",
            r"HID\VID_1532&UP:FF00_U:0001",
            "HID_DEVICE_UP:FF00_U:0001",
            "HID_DEVICE",
        ],
    )
}

/// The selection reads only the devnode hardware IDs: the touchpad's vendor
/// and top-level usage, whatever the bus and whatever the path says.
#[test]
fn the_touchpad_is_selected_by_its_vendor_and_usage_hardware_id() {
    assert!(is_touchpad_interface(&i2c_touchpad().hardware_ids));
    assert!(is_touchpad_interface(&usb_touchpad().hardware_ids));
    assert!(!is_touchpad_interface(&i2c_mouse().hardware_ids));
    assert!(!is_touchpad_interface(&control().hardware_ids));
    assert!(!is_touchpad_interface(&[]));
    let lowercase: Vec<String> = i2c_touchpad()
        .hardware_ids
        .iter()
        .map(|id| id.to_lowercase())
        .collect();
    assert!(is_touchpad_interface(&lowercase));
    // A path that names the device is not enough, and the ID is exact.
    for id in [
        r"HID\VID_093A&PID_3001",
        r"HID\VID_093A&UP:000D_U:0004",
        r"HID\VID_093A&UP:0001_U:0005",
        r"HID\VID_093B&UP:000D_U:0005",
        r"HID\VID_093A&UP:000D_U:0005&MI_00",
        "HID_DEVICE_UP:000D_U:0005",
    ] {
        assert!(!is_touchpad_interface(&[id.to_owned()]), "{id}");
    }
}

fn touchpad_row(
    path: &str,
    ids: HidPath,
    attributes: Option<HidInterface>,
) -> (HidPath, Option<HidInterface>) {
    assert_eq!(path, ids.path);
    (ids, attributes)
}

fn with_hid(rows: Vec<(HidPath, Option<HidInterface>)>) -> Fake {
    let mut fake = Fake::healthy();
    fake.raw_hid = Some(rows);
    fake
}

/// On the RZ09-0581 the touchpad is read through its I2C interface, and
/// nothing else is opened: not its mouse collection, not the laptop's control
/// collections, which managed would also open.
#[test]
fn only_the_touchpad_interface_is_opened() {
    let mouse = HidInterface {
        usage_page: 0x01,
        usage: 0x02,
        ..touchpad(0x0123)
    };
    let fake = with_hid(vec![
        (
            control(),
            Some(HidInterface {
                vendor_id: 0x1532,
                product_id: 0x02e0,
                usage_page: 0xff00,
                usage: 1,
                version: 0x0200,
            }),
        ),
        touchpad_row(I2C_MOUSE, i2c_mouse(), Some(mouse)),
        touchpad_row(I2C_TOUCHPAD, i2c_touchpad(), Some(touchpad(0x0123))),
    ]);
    assert_eq!(captured(&fake).touchpad_firmware_version, "1.23");
    assert_eq!(*fake.opened.lock().unwrap(), [I2C_TOUCHPAD]);

    // A USB touchpad is selected the same way.
    let fake = with_hid(vec![
        (control(), None),
        touchpad_row(USB_TOUCHPAD, usb_touchpad(), Some(touchpad(0x0456))),
    ]);
    assert_eq!(captured(&fake).touchpad_firmware_version, "4.56");
    assert_eq!(*fake.opened.lock().unwrap(), [USB_TOUCHPAD]);

    // No interface names it: none is opened and the version is unavailable.
    let fake = with_hid(vec![
        (control(), None),
        touchpad_row(I2C_MOUSE, i2c_mouse(), None),
    ]);
    assert_eq!(captured(&fake).touchpad_firmware_version, UNAVAILABLE);
    assert!(fake.opened.lock().unwrap().is_empty());

    // A selected interface that cannot be opened or described is skipped.
    let fake = with_hid(vec![touchpad_row(I2C_TOUCHPAD, i2c_touchpad(), None)]);
    assert_eq!(captured(&fake).touchpad_firmware_version, UNAVAILABLE);
    assert_eq!(*fake.opened.lock().unwrap(), [I2C_TOUCHPAD]);

    // Two selected collections are ambiguous, as for managed.
    let fake = with_hid(vec![
        touchpad_row(I2C_TOUCHPAD, i2c_touchpad(), Some(touchpad(0x0123))),
        touchpad_row(USB_TOUCHPAD, usb_touchpad(), Some(touchpad(0x0123))),
    ]);
    assert_eq!(captured(&fake).touchpad_firmware_version, UNAVAILABLE);
}

/// .NET's `Version.TryParse` and `int.Parse`, over the cases that differ
/// between a hand-written parser and the framework. The vectors file pins
/// the same cases against managed.
#[test]
fn the_managed_number_formats_are_parsed_exactly() {
    for (text, expect) in [
        ("1.2", true),
        ("1.2.3", true),
        ("1.2.3.4", true),
        ("0.0", true),
        ("1", false),
        ("", false),
        ("1.", false),
        (".1", false),
        ("1..2", false),
        ("1.2.3.4.5", false),
        ("1.-2", false),
        ("1.2a", false),
        ("1.2 3", false),
        ("1.2147483647", true),
        ("1.2147483648", false),
    ] {
        assert_eq!(is_version(text), expect, "{text:?}");
    }
    for (text, expect) in [
        ("5", Some(5)),
        (" 5 ", Some(5)),
        ("+5", Some(5)),
        ("-5", Some(-5)),
        ("-0", Some(0)),
        ("", None),
        ("+", None),
        ("5.0", None),
        ("0x10", None),
        ("2147483647", Some(i32::MAX)),
        ("2147483648", None),
        ("-2147483648", Some(i32::MIN)),
        ("-2147483649", None),
        ("٣", None),
        // A long digit string is out of range, however long it is.
        ("99999999999999999999999999", None),
        ("-99999999999999999999999999", None),
        ("0000000000000000000000000000000000005", Some(5)),
        // .NET accepts terminators after the number and its trailing white space.
        ("5\0", Some(5)),
        ("5 \0\0", Some(5)),
        ("5\0 ", None),
        ("5\0x", None),
        ("\u{a0}5", None),
    ] {
        assert_eq!(parse_int32(text), expect, "{text:?}");
    }
}

// --- Schedule ---

/// The capture runs once, on its own worker: later polls return the same
/// record and read nothing again, whatever Windows now reports.
#[test]
fn the_record_is_captured_once_and_never_refreshed() {
    let fake = Arc::new(Fake::healthy());
    let host = Host::new(fake.clone());
    assert_eq!(
        fake.reads(),
        [0; 4],
        "nothing is read before the first poll"
    );
    assert_eq!(settle(&host), healthy_record());
    let reads = fake.reads();
    assert_eq!(reads, [4, 1, 1, 1]);
    for _ in 0..3 {
        assert_eq!(host.poll(Instant::now()), Some(healthy_record()));
    }
    // A poll long after the deadline changes nothing either.
    assert_eq!(
        host.poll(Instant::now() + DEADLINE * 10),
        Some(healthy_record())
    );
    assert_eq!(fake.reads(), reads);
}

/// An unreadable first capture is the record too; it is not retried, as
/// managed's startup capture is not.
#[test]
fn an_unavailable_capture_is_published_and_not_retried() {
    let mut fake = Fake::healthy();
    fake.registry_fails = true;
    fake.video = None;
    let fake = Arc::new(fake);
    let host = Host::new(fake.clone());
    assert_eq!(settle(&host), unavailable());
    assert_eq!(settle(&host), unavailable());
    assert_eq!(fake.reads(), [4, 1, 0, 0]);
}

/// A host without a source captures and publishes nothing.
#[test]
fn a_host_without_a_source_publishes_nothing() {
    let host = Host::none();
    for _ in 0..3 {
        assert_eq!(host.poll(Instant::now()), None);
    }
}

/// A source that hangs leaves only its own parts unavailable once the
/// deadline passes. Before it, nothing is published; the poll returns at once
/// and never waits for the worker.
#[test]
fn a_hung_source_costs_only_its_own_part_after_the_deadline() {
    let (fake, hold) = Fake::healthy().held(Stage::Touchpad);
    let (fake, probe, ended) = Probe::new(fake);
    let host = Host::new(probe);
    let start = Instant::now();
    assert_eq!(host.poll(start), None);
    hold.entered.recv_timeout(Duration::from_secs(5)).unwrap();
    // The registry and Thunderbolt parts were reported before the hang.
    assert_eq!(host.poll(start + DEADLINE - Duration::from_millis(1)), None);
    let mut expect = healthy_record();
    expect.touchpad_firmware_version = UNAVAILABLE.into();
    assert_eq!(host.poll(start + DEADLINE), Some(expect.clone()));
    // The hung read returns and the worker ends, having read the touchpad
    // once and nothing else. What it reports now is not published.
    hold.release.send(()).unwrap();
    ended.recv_timeout(Duration::from_secs(5)).unwrap();
    assert_eq!(fake.reads(), [4, 1, 1, 1]);
    assert_eq!(host.poll(start + DEADLINE * 2), Some(expect));
}

/// A hang before the first part leaves every part unavailable, and the model
/// is unknown, so no access is claimed.
#[test]
fn a_hang_before_any_part_publishes_the_unavailable_record() {
    let (fake, hold) = Fake::healthy().held(Stage::Video);
    let host = Host::new(Arc::new(fake));
    let start = Instant::now();
    assert_eq!(host.poll(start), None);
    hold.entered.recv_timeout(Duration::from_secs(5)).unwrap();
    assert_eq!(host.poll(start + DEADLINE), Some(unavailable()));
    hold.release.send(()).unwrap();
}

// --- Publication ---

struct Rig {
    io: Arc<FanIo>,
    custody: Arc<Custody>,
    /// While set, the admission the worker sees in place of the custody's:
    /// a device change after the worker's last check and before the source
    /// accepts its reading.
    worker_admission: Arc<Mutex<Option<ReadAdmission>>>,
    source: ReadSource,
    publisher: watch::Sender<Arc<Publication>>,
}

impl Rig {
    /// A source with the firmware capture `fake`, or none.
    fn new(fake: Option<Arc<dyn Capture>>) -> Self {
        let custody = Arc::new(Custody::new());
        let worker_admission = Arc::new(Mutex::new(None));
        let io = FanIo::new([0x20, 0x1e]);
        let device = io.clone();
        let pinned = worker_admission.clone();
        let source = ReadSource::compose(custody.clone(), move |lease| {
            fan_io_worker(device, move || {
                let pinned = *pinned.lock().unwrap();
                pinned.or_else(|| lease.authorize())
            })
        });
        let source = match fake {
            Some(fake) => source.with_firmware_capture_for_tests(fake),
            None => source,
        };
        let publisher = watch::channel(Arc::new(Publication {
            active: status(0),
            inactive: status(1),
        }))
        .0;
        Self {
            io,
            custody,
            worker_admission,
            source,
            publisher,
        }
    }

    fn request(mask: u8) -> crate::service_status::StatusRequest {
        serde_json::from_value(json!({
            "operation": "observeState",
            "version": 91,
            "requestId": "11111111-2222-4333-8444-555555555555",
            "includeLiveReadings": mask != 0,
            "requestedLiveReadings": mask,
            "effectiveLiveReadings": mask,
        }))
        .unwrap()
    }

    async fn snapshot(&self, mask: u8) -> Option<Snapshot> {
        let deadline = Instant::now() + Duration::from_secs(2);
        let publisher = self.publisher.clone();
        Self::request(mask)
            .scope_live_readings(|| self.source.observe(status(0), deadline, publisher))
            .await
            .unwrap()
    }

    async fn observe(&self, mask: u8) -> Json {
        serde_json::to_value(self.snapshot(mask).await.unwrap()).unwrap()
    }

    fn published(&self) -> [Json; 2] {
        let publication = self.publisher.borrow().clone();
        [&publication.active, &publication.inactive].map(|s| serde_json::to_value(s).unwrap())
    }

    /// A metadata publication at `now`, as the metadata observer sends one.
    fn metadata_at(&self, now: Instant) {
        let status = |access| {
            Snapshot::observed_read_only(0, access, false, false, vec![], None, "Awaiting.".into())
        };
        self.source
            .decorate_at(
                &self.publisher,
                Some(Arc::new(Publication {
                    active: status(0),
                    inactive: status(1),
                })),
                now,
            )
            .unwrap();
    }

    fn metadata(&self) {
        self.metadata_at(Instant::now());
    }

    /// Publishes metadata until the capture is in them.
    fn await_firmware(&self) -> [Json; 2] {
        for _ in 0..5_000 {
            self.metadata();
            let published = self.published();
            if !published[0]["firmware"].is_null() {
                return published;
            }
            std::thread::sleep(Duration::from_millis(1));
        }
        panic!("the firmware record was not published");
    }
}

fn record_json() -> Json {
    json!({
        "biosVersion": "3.01", "ecVersion": "1.04", "mcuVersion": "Unavailable",
        "vbiosVersion": "98.03.5c.40.3d", "modelNumber": "RZ09-0581",
        "vbiosGpuName": "NVIDIA GeForce RTX 5090 Laptop GPU",
        "thunderboltFirmwareVersion": "47.0", "touchpadFirmwareVersion": "1.23",
        "thunderboltFirmwareAccess": 1, "touchpadFirmwareAccess": 1,
    })
}

/// The record is device-independent status: both projections of the first
/// publication that follows the capture carry it, every ObserveState reply
/// carries it whatever sensors it asked for, and a later capture never
/// changes it.
#[tokio::test]
async fn status_and_every_observe_reply_carry_the_captured_record() {
    let fake = Arc::new(Fake::healthy());
    let rig = Rig::new(Some(fake.clone()));
    for published in rig.await_firmware() {
        assert_eq!(published["firmware"], record_json());
    }
    for mask in [0, 1, 2, ALL] {
        let observed = rig.observe(mask).await;
        assert_eq!(observed["firmware"], record_json(), "mask {mask}");
    }
    for published in rig.published() {
        assert_eq!(published["firmware"], record_json());
    }
    assert_eq!(fake.reads(), [4, 1, 1, 1]);
}

/// An observation that follows a finished capture but no metadata publication
/// carries the record itself, rather than waiting for the next publication.
#[tokio::test]
async fn an_observation_carries_a_capture_that_no_status_has_published() {
    let rig = Rig::new(Some(Arc::new(Fake::healthy())));
    assert_eq!(settle(&rig.source.firmware), healthy_record());
    assert!(
        rig.published()
            .iter()
            .all(|published| published.get("firmware").is_none())
    );
    assert_eq!(rig.observe(0).await["firmware"], record_json());
}

/// The capture waits as long as a slow enumeration needs and no longer.
#[test]
fn the_deadline_is_ten_seconds() {
    assert_eq!(DEADLINE, Duration::from_secs(10));
}

/// A status without a source has no member, as before this capture existed.
#[tokio::test]
async fn a_source_without_a_capture_publishes_no_member() {
    let rig = Rig::new(None);
    rig.metadata();
    for published in rig.published() {
        assert!(published.get("firmware").is_none());
    }
    assert!(rig.observe(ALL).await.get("firmware").is_none());
}

/// A capture that has not finished never holds the read job: the observation
/// completes and sends its fan queries while the capture is blocked, with no
/// member in its reply.
#[tokio::test]
async fn a_blocked_capture_does_not_hold_the_read_job() {
    let (fake, hold) = Fake::healthy().held(Stage::Video);
    let rig = Rig::new(Some(Arc::new(fake)));
    rig.metadata();
    hold.entered.recv_timeout(Duration::from_secs(5)).unwrap();
    let observed = rig.observe(ALL).await;
    assert!(observed.get("firmware").is_none());
    assert_eq!(observed["liveReadings"]["fan1Rpm"], 3200);
    assert_eq!(rig.io.queries().len(), 2);
    for published in rig.published() {
        assert!(published.get("firmware").is_none());
    }
    hold.release.send(()).unwrap();
    for published in rig.await_firmware() {
        assert_eq!(published["firmware"], record_json());
    }
}

/// A host that stops while its worker is in a read does not wait for it: the
/// worker finishes that read, finds nobody listening and reads nothing more.
/// What it had reported is the record.
#[test]
fn a_stopped_host_ends_its_worker_after_the_current_read() {
    let (fake, hold) = Fake::healthy().held(Stage::Router);
    let (fake, probe, ended) = Probe::new(fake);
    let host = Host::new(probe);
    let now = Instant::now();
    assert_eq!(host.poll(now), None);
    hold.entered.recv_timeout(Duration::from_secs(5)).unwrap();
    host.stop();
    hold.release.send(()).unwrap();
    ended.recv_timeout(Duration::from_secs(5)).unwrap();
    assert_eq!(fake.reads(), [4, 1, 1, 0]);
    let mut expect = healthy_record();
    expect.thunderbolt_firmware_version = UNAVAILABLE.into();
    expect.touchpad_firmware_version = UNAVAILABLE.into();
    assert_eq!(host.poll(now), Some(expect));
}

/// A host stopped before its first poll never starts the capture.
#[test]
fn a_host_stopped_before_it_starts_never_reads() {
    let (fake, probe, ended) = Probe::new(Fake::healthy());
    let host = Host::new(probe);
    host.stop();
    assert_eq!(host.poll(Instant::now()), None);
    // Stopping released the source unused; a started worker would have read.
    ended.recv_timeout(Duration::from_secs(5)).unwrap();
    assert_eq!(fake.reads(), [0; 4]);
}

/// Stopping the source completes while a capture is held in a read, and the
/// capture then ends without reading a device.
#[tokio::test]
async fn stopping_the_source_does_not_wait_for_a_held_capture() {
    let (fake, hold) = Fake::healthy().held(Stage::Video);
    let (fake, probe, ended) = Probe::new(fake);
    let rig = Rig::new(Some(probe));
    rig.metadata();
    hold.entered.recv_timeout(Duration::from_secs(5)).unwrap();
    tokio::time::timeout(Duration::from_secs(5), rig.source.stop())
        .await
        .expect("stop completes while the capture is held")
        .unwrap();
    hold.release.send(()).unwrap();
    ended.recv_timeout(Duration::from_secs(5)).unwrap();
    assert_eq!(fake.reads(), [4, 1, 0, 0]);
}

const FRAMES: &str = "firmware-version-native-frames.json";
const REGENERATE: &str = "OPENBLADE_REGENERATE_NATIVE_FIRMWARE_FRAMES";

/// Records production snapshots as v91 status events for the managed codec
/// test (`NativeFirmwareVersionFrames_DecodeThroughTheManagedCodec`): an
/// ObserveState reply and the subscription feed with the complete record,
/// both again after the read job read the MCU version, and a feed whose
/// touchpad read hung. A build with the Driver-mode read adds its
/// own member, so only this composition records.
#[cfg(not(feature = "service-keyboard-driver-mode-read-v1"))]
#[tokio::test]
async fn native_frames_match_the_recorded_managed_codec_input() {
    let mut frames = Vec::new();
    let mut record = |name: &str, snapshot: &Snapshot, expect: Json| {
        let frame = crate::service_status::encode_status_event(snapshot).unwrap();
        frames.push(json!({
            "name": name,
            "frame": String::from_utf8(frame).unwrap(),
            "expect": expect,
        }));
    };

    let rig = Rig::new(Some(Arc::new(Fake::healthy())));
    rig.await_firmware();
    record(
        "observe reply",
        &rig.snapshot(ALL).await.unwrap(),
        record_json(),
    );
    record(
        "subscription",
        &rig.publisher.borrow().clone().active,
        record_json(),
    );

    let rig = Rig::new(Some(Arc::new(Fake::healthy())));
    *rig.io.mcu.lock().unwrap() = Some((2, vec![1, 5, 0, 0]));
    rig.await_firmware();
    let mut read = record_json();
    read["mcuVersion"] = json!("1.5.0.0");
    record(
        "observe reply that read the MCU version",
        &rig.snapshot(ALL).await.unwrap(),
        read.clone(),
    );
    record(
        "subscription after the MCU version was read",
        &rig.publisher.borrow().clone().active,
        read,
    );

    let (fake, hold) = Fake::healthy().held(Stage::Touchpad);
    let rig = Rig::new(Some(Arc::new(fake)));
    let start = Instant::now();
    rig.metadata_at(start);
    hold.entered.recv_timeout(Duration::from_secs(5)).unwrap();
    rig.metadata_at(start + DEADLINE);
    let mut partial = record_json();
    partial["touchpadFirmwareVersion"] = json!("Unavailable");
    record(
        "subscription with a hung touchpad",
        &rig.publisher.borrow().clone().active,
        partial,
    );
    hold.release.send(()).unwrap();

    let mut text = String::from("{\"frames\":[\n");
    text.push_str(
        &frames
            .iter()
            .map(|frame| serde_json::to_string(frame).unwrap())
            .collect::<Vec<_>>()
            .join(",\n"),
    );
    text.push_str("\n]}\n");
    let path = std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("tests/data")
        .join(FRAMES);
    if std::env::var_os(REGENERATE).is_some() {
        std::fs::write(&path, &text).unwrap();
    }
    assert_eq!(std::fs::read_to_string(&path).unwrap(), text);
}

// --- Managed vectors ---

fn vectors() -> Json {
    serde_json::from_str(include_str!(
        "../../../../tests/data/firmware-version-vectors.json"
    ))
    .unwrap()
}

fn value_of(row: &Json) -> Value {
    match row["type"].as_str().unwrap() {
        "text" => Value::Text(row["value"].as_str().unwrap().into()),
        "multi" => Value::Multi(
            row["value"]
                .as_array()
                .unwrap()
                .iter()
                .map(|item| item.as_str().unwrap().into())
                .collect(),
        ),
        "int" | "long" => Value::Number(row["value"].as_i64().unwrap()),
        "binary" => Value::Other,
        other => unreachable!("{other}"),
    }
}

fn optional(row: &Json) -> Option<String> {
    row.as_str().map(str::to_owned)
}

/// A fake Windows from one vector row.
fn fake_of(row: &Json) -> Fake {
    let mut fake = Fake::default();
    let names = [
        "SystemSKU",
        "BIOSVersion",
        "ECFirmwareMajorRelease",
        "ECFirmwareMinorRelease",
    ];
    for name in names {
        if let Some(value) = row["registry"].get(name) {
            fake.registry.insert(name, value_of(value));
        }
    }
    fake.video = Some(
        row["video"]
            .as_array()
            .unwrap()
            .iter()
            .map(|adapter| {
                adapter
                    .as_array()
                    .unwrap()
                    .iter()
                    .map(|instance| VideoInstance {
                        adapter: value_of(&instance["adapter"]),
                        bios: value_of(&instance["bios"]),
                    })
                    .collect()
            })
            .collect(),
    );
    fake.routers = Some(
        row["routers"]
            .as_array()
            .unwrap()
            .iter()
            .map(|router| Router {
                instance_id: router["instanceId"].as_str().unwrap().into(),
                firmware_version: optional(&router["firmware"]),
                hardware_ids: router["hardwareIds"]
                    .as_array()
                    .unwrap()
                    .iter()
                    .map(|id| id.as_str().unwrap().into())
                    .collect(),
                service: optional(&router["service"]),
                parent: optional(&router["parent"]),
            })
            .collect(),
    );
    fake.interfaces = Some(
        row["interfaces"]
            .as_array()
            .unwrap()
            .iter()
            .map(|interface| {
                let number = |member| interface[member].as_u64().unwrap() as u16;
                HidInterface {
                    vendor_id: number("vendorId"),
                    product_id: number("productId"),
                    usage_page: number("usagePage"),
                    usage: number("usage"),
                    version: number("version"),
                }
            })
            .collect(),
    );
    fake
}

/// The record `WindowsFirmwareVersionSource.Capture()` produced for each
/// scenario, written through the managed v91 status codec, is the record the
/// native capture produces over the same Windows reads.
#[test]
fn the_capture_matches_the_managed_vectors() {
    let vectors = vectors();
    let rows = vectors["records"].as_array().unwrap();
    assert!(rows.len() >= 20);
    for row in rows {
        let fake = fake_of(row);
        let record = serde_json::to_value(captured(&fake)).unwrap();
        assert_eq!(record, row["expect"], "{}", row["name"]);
    }
    for row in vectors["versions"].as_array().unwrap() {
        let text = row["text"].as_str().unwrap();
        assert_eq!(
            is_version(text),
            row["expect"].as_bool().unwrap(),
            "{text:?}"
        );
    }
    for row in vectors["integers"].as_array().unwrap() {
        let text = row["text"].as_str().unwrap();
        assert_eq!(
            parse_int32(text).map(i64::from),
            row["expect"].as_i64(),
            "{text:?}"
        );
    }
}

#[path = "firmware_versions_mcu_tests.rs"]
mod mcu;

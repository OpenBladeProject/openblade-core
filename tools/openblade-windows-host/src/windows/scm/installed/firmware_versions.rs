//! Managed firmware versions on the installed read source
//! (`service-live-readings-v1`).
//!
//! Managed `WindowsFirmwareVersionSource` captures one `FirmwareVersions`
//! record when the runtime starts and never refreshes it, so this module
//! captures it once for the read source the Service acquires at start. The
//! capture runs on its own worker thread, never on the read job, so it cannot
//! spend the job's HID budget.
//! It takes no hardware lease and sends no laptop command; every source is a
//! Windows metadata read:
//!
//! - BIOS, EC and model: the `HARDWARE\DESCRIPTION\System\BIOS` registry key.
//! - VBIOS and GPU name: the video adapter registry keys.
//! - Thunderbolt: the USB4 root router's firmware version device property.
//! - Touchpad: the version number of its HID attributes (`HidD_GetAttributes`
//!   on a handle opened with no access), not a report to the device. The
//!   touchpad is HID over I2C on the RZ09-0581, so its interface path does not
//!   name its vendor; it is found by the devnode hardware ID that hidclass
//!   builds for every top-level collection, `HID\VID_093A&UP:000D_U:0005`, and
//!   only interfaces with that ID are opened.
//!
//! McuVersion is not a Windows read. Managed reads it with a firmware query
//! over the laptop's control interface (`IBladeDevice.ReadMcuVersionAsync`),
//! so it rides the Service's own read job (`mcu_version`), and this record
//! carries it once a job has confirmed it. Until then it is `Unavailable`.
//!
//! A part that cannot be read is the managed text `Unavailable` and does not
//! affect another part. The record is published once, when the worker is done
//! or `DEADLINE` has passed; a source that hangs at that point leaves its own
//! parts `Unavailable`, and its worker keeps only the handles it owns until
//! the call returns.
use crate::{
    discovery::{
        catalog_model,
        performance::{McuQuerySends, McuVersionRead},
    },
    service_status::FirmwareVersions,
};
use std::{
    cell::RefCell,
    io,
    sync::{
        Arc,
        mpsc::{Receiver, Sender, TryRecvError, channel},
    },
    thread,
    time::Duration,
};
use tokio::time::Instant;

#[cfg(windows)]
#[path = "firmware_versions_windows.rs"]
mod windows;
#[cfg(windows)]
pub(super) use windows::Windows;

#[path = "mcu_version.rs"]
mod mcu_version;

pub(super) const UNAVAILABLE: &str = "Unavailable";
/// Managed `DeviceFeatureAccess.ReadOnly`.
const READ_ONLY: u8 = 1;
/// The longest the worker may take before the record is published without
/// the parts it has not yet reported.
pub(super) const DEADLINE: Duration = Duration::from_secs(10);

const BIOS_KEY: &str = r"HARDWARE\DESCRIPTION\System\BIOS";
const TARGET_MODEL: &str = "RZ09-0581";

// Managed `WindowsDeviceFirmwareVersionReader` constants.
const THUNDERBOLT_ROUTER: &str = r"USB4\ROOT_DEVICE_ROUTER&VID_8086&PID_5781";
const THUNDERBOLT_HARDWARE_ID: &str = r"USB4\ROOT_DEVICE_ROUTER&VID_8086&PID_5781&REV_0084";
const THUNDERBOLT_PARENT: &str = r"PCI\VEN_8086&DEV_5781&SUBSYS_30101A58&REV_84&USB4_MS_CM";
const THUNDERBOLT_SERVICE: &str = "Usb4DeviceRouter";
const TOUCHPAD: (u16, u16) = (0x093A, 0x3001);
const TOUCHPAD_USAGE: (u16, u16) = (0x000D, 0x0005);
/// The vendor and top-level usage hardware ID of the touchpad's collection.
const TOUCHPAD_ID: &str = r"HID\VID_093A&UP:000D_U:0005";

/// What `RegistryKey.GetValue` returns, by the types the managed code tells
/// apart.
#[derive(Clone, Debug, PartialEq, Eq)]
pub(super) enum Value {
    Missing,
    /// REG_SZ and REG_EXPAND_SZ.
    Text(String),
    /// REG_MULTI_SZ.
    Multi(Vec<String>),
    /// REG_DWORD (as the signed int .NET returns) and REG_QWORD.
    Number(i64),
    /// Any other type, such as REG_BINARY.
    Other,
}

/// One video adapter registry instance's two firmware values.
#[derive(Clone, Debug, PartialEq, Eq)]
pub(super) struct VideoInstance {
    pub(super) adapter: Value,
    pub(super) bios: Value,
}

/// One present USB4 device whose instance ID is the root router's, with the
/// properties managed reads. A property that is absent or of another type is
/// None, or empty for the hardware IDs.
#[derive(Clone, Debug, PartialEq, Eq)]
pub(super) struct Router {
    pub(super) instance_id: String,
    pub(super) firmware_version: Option<String>,
    pub(super) hardware_ids: Vec<String>,
    pub(super) service: Option<String>,
    pub(super) parent: Option<String>,
}

/// One HID collection's attributes and top-level usage.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(super) struct HidInterface {
    pub(super) vendor_id: u16,
    pub(super) product_id: u16,
    pub(super) usage_page: u16,
    pub(super) usage: u16,
    pub(super) version: u16,
}

/// One present HID interface: its opaque path and devnode hardware IDs.
#[derive(Clone, Debug, PartialEq, Eq)]
pub(super) struct HidPath {
    pub(super) path: String,
    pub(super) hardware_ids: Vec<String>,
}

/// The Windows metadata reads, injectable so tests never call Windows. Each
/// read is independent and returns what the system holds, unselected.
pub(super) trait Capture: Send + Sync {
    /// A value under `HKEY_LOCAL_MACHINE`, 64-bit view.
    fn registry(&self, key: &str, name: &str) -> io::Result<Value>;
    /// The video adapters' registry instances, one list per adapter key.
    fn video_adapters(&self) -> io::Result<Vec<Vec<VideoInstance>>>;
    fn thunderbolt_routers(&self) -> io::Result<Vec<Router>>;
    /// Every present HID interface, read without opening any.
    fn hid_paths(&self) -> io::Result<Vec<HidPath>>;
    /// The attributes and top-level usage of one HID interface, opened with no
    /// access. None skips an interface that cannot be opened or described.
    fn hid_attributes(&self, path: &str) -> Option<HidInterface>;
}

/// The record with every part unavailable, managed `FirmwareVersions.Unavailable()`.
pub(super) fn unavailable() -> FirmwareVersions {
    let text = || UNAVAILABLE.to_owned();
    FirmwareVersions {
        bios_version: text(),
        ec_version: text(),
        mcu_version: text(),
        vbios_version: text(),
        model_number: text(),
        vbios_gpu_name: text(),
        thunderbolt_firmware_version: text(),
        touchpad_firmware_version: text(),
        thunderbolt_firmware_access: None,
        touchpad_firmware_access: None,
    }
}

// --- Managed parsing and selection ---

/// .NET `int.Parse(text, NumberStyles.Integer, InvariantCulture)`: optional
/// leading white space, an optional sign, ASCII digits that fit an int, then
/// optional white space and terminators.
fn parse_int32(text: &str) -> Option<i32> {
    let is_white = |c: char| matches!(c, '\t'..='\r' | ' ');
    let text = text.trim_start_matches(is_white);
    let (negative, rest) = match text.as_bytes().first()? {
        b'-' => (true, &text[1..]),
        b'+' => (false, &text[1..]),
        _ => (false, text),
    };
    let (digits, tail) = rest.split_at(rest.bytes().take_while(u8::is_ascii_digit).count());
    if digits.is_empty() || !tail.trim_start_matches(is_white).chars().all(|c| c == '\0') {
        return None;
    }
    let magnitude = digits.bytes().try_fold(0i64, |sum, digit| {
        let sum = sum * 10 + i64::from(digit - b'0');
        (sum <= 1 << 31).then_some(sum)
    })?;
    i32::try_from(if negative { -magnitude } else { magnitude }).ok()
}

/// .NET `Version.TryParse`: two to four components, each a non-negative int.
fn is_version(text: &str) -> bool {
    let components = text.split('.').count();
    (2..=4).contains(&components)
        && text
            .split('.')
            .all(|component| parse_int32(component).is_some_and(|value| value >= 0))
}

fn is_blank(text: &str) -> bool {
    text.trim().is_empty()
}

/// Managed `FormatRegistryString`.
fn format_text(value: &Value) -> String {
    match value {
        Value::Text(text) if !is_blank(text) => text.trim().to_owned(),
        Value::Multi(values) if values.iter().any(|item| !is_blank(item)) => values
            .iter()
            .filter(|item| !is_blank(item))
            .map(String::as_str)
            .collect::<Vec<_>>()
            .join(" "),
        _ => UNAVAILABLE.into(),
    }
}

/// Managed `TryConvertByte`: `Convert.ToInt32` of a number or numeric text
/// that is a byte.
fn byte_value(value: &Value) -> Option<u8> {
    let number = match value {
        Value::Number(number) => i32::try_from(*number).ok()?,
        Value::Text(text) => parse_int32(text)?,
        _ => return None,
    };
    u8::try_from(number).ok()
}

/// Managed `ReadEcVersion`: `major.minor` with a two-digit minor.
fn ec_version(major: &Value, minor: &Value) -> String {
    match (byte_value(major), byte_value(minor)) {
        (Some(major), Some(minor)) => format!("{major}.{minor:02}"),
        _ => UNAVAILABLE.into(),
    }
}

/// A video adapter's firmware as managed reads it from the registry.
#[derive(Clone, Debug, PartialEq, Eq)]
pub(super) struct VideoCandidate {
    adapter: String,
    bios: String,
}

/// Managed `ReadVideoFirmwareCandidates` and `CollapseRegistryAdapterInstances`
/// over each adapter key's instances: an instance needs both values to be
/// text, and instances of one adapter with the same trimmed adapter and BIOS
/// strings (ignoring case) count once.
fn video_candidates(adapters: &[Vec<VideoInstance>]) -> Vec<VideoCandidate> {
    let mut candidates = Vec::new();
    for instances in adapters {
        let mut seen = Vec::new();
        for instance in instances {
            let (Value::Text(adapter), Value::Text(bios)) = (&instance.adapter, &instance.bios)
            else {
                continue;
            };
            let key = (adapter.trim().to_uppercase(), bios.trim().to_uppercase());
            if !seen.contains(&key) {
                seen.push(key);
                candidates.push(VideoCandidate {
                    adapter: adapter.clone(),
                    bios: bios.clone(),
                });
            }
        }
    }
    candidates
}

/// Managed `ReadVbiosFirmware`: the one NVIDIA adapter's (name, VBIOS
/// version), with a leading "Version" removed, or None.
fn vbios(candidates: &[VideoCandidate]) -> Option<(String, String)> {
    let mut nvidia = candidates
        .iter()
        .filter(|candidate| candidate.adapter.to_ascii_uppercase().contains("NVIDIA"));
    let candidate = nvidia.next().filter(|_| nvidia.next().is_none())?;
    let value = candidate.bios.trim();
    let version = match value.get(.."Version".len()) {
        Some(prefix) if prefix.eq_ignore_ascii_case("Version") => value["Version".len()..].trim(),
        _ => value,
    };
    (!version.is_empty()).then(|| (candidate.adapter.trim().to_owned(), version.to_owned()))
}

/// Managed `SelectThunderboltFirmwareVersion`: the firmware version of the
/// one USB4 root router with the exact instance, hardware ID, service and
/// parent, when it is a version.
fn thunderbolt(routers: &[Router]) -> Option<String> {
    let starts = |text: &str, prefix: &str| {
        text.get(..prefix.len())
            .is_some_and(|start| start.eq_ignore_ascii_case(prefix))
    };
    let mut exact = routers.iter().filter(|router| {
        // Managed's enumeration lists only a router that has all four
        // properties. The exact hardware ID, service and parent below imply
        // three of them, so only a firmware version needs its own check: a
        // router without one is not counted.
        router
            .firmware_version
            .as_deref()
            .is_some_and(|version| !is_blank(version))
            && starts(&router.instance_id, &format!("{THUNDERBOLT_ROUTER}\\"))
            && router
                .hardware_ids
                .iter()
                .any(|id| id.eq_ignore_ascii_case(THUNDERBOLT_HARDWARE_ID))
            && router
                .service
                .as_deref()
                .is_some_and(|service| service.eq_ignore_ascii_case(THUNDERBOLT_SERVICE))
            && router
                .parent
                .as_deref()
                .is_some_and(|parent| starts(parent, &format!("{THUNDERBOLT_PARENT}\\")))
    });
    let router = exact.next().filter(|_| exact.next().is_none())?;
    let version = router.firmware_version.as_deref()?.trim();
    is_version(version).then(|| version.to_owned())
}

/// Whether a HID interface's devnode hardware IDs name the touchpad's
/// top-level collection. Managed opens every HID interface and then keeps the
/// touchpad's by its attributes; opening only the ones named here leaves the
/// laptop's own control collections alone.
fn is_touchpad_interface(hardware_ids: &[String]) -> bool {
    hardware_ids
        .iter()
        .any(|id| id.eq_ignore_ascii_case(TOUCHPAD_ID))
}

/// The attributes of each interface that names the touchpad's collection.
fn touchpad_attributes(capture: &dyn Capture) -> io::Result<Vec<HidInterface>> {
    Ok(capture
        .hid_paths()?
        .iter()
        .filter(|interface| is_touchpad_interface(&interface.hardware_ids))
        .filter_map(|interface| capture.hid_attributes(&interface.path))
        .collect())
}

/// Managed `SelectTouchpadFirmwareVersion`: the BCD version number of the one
/// touchpad collection, as `major.minor`.
fn touchpad(interfaces: &[HidInterface]) -> Option<String> {
    let mut exact = interfaces.iter().filter(|interface| {
        (interface.vendor_id, interface.product_id) == TOUCHPAD
            && (interface.usage_page, interface.usage) == TOUCHPAD_USAGE
    });
    let interface = exact.next().filter(|_| exact.next().is_none())?;
    if interface.version == 0 {
        return None;
    }
    let decode = |byte: u8| {
        let (high, low) = (byte >> 4, byte & 0x0f);
        (high <= 9 && low <= 9).then_some(high * 10 + low)
    };
    let major = decode((interface.version >> 8) as u8)?;
    let minor = decode(interface.version as u8)?;
    Some(format!("{major}.{minor:02}"))
}

// --- Capture ---

/// What the worker reports as it goes.
#[derive(Debug, PartialEq, Eq)]
pub(super) enum Part {
    /// The registry reads and the model they decide.
    Registry {
        bios: String,
        ec: String,
        model: String,
        vbios: Option<(String, String)>,
        target: bool,
    },
    Thunderbolt(Option<String>),
    Touchpad(Option<String>),
}

impl Part {
    fn apply(self, record: &mut FirmwareVersions) {
        let text = |value: Option<String>| value.unwrap_or_else(|| UNAVAILABLE.into());
        match self {
            Self::Registry {
                bios,
                ec,
                model,
                vbios,
                target,
            } => {
                let (gpu, version) = vbios.unzip();
                record.bios_version = bios;
                record.ec_version = ec;
                record.model_number = model;
                record.vbios_gpu_name = text(gpu);
                record.vbios_version = text(version);
                let access = target.then_some(READ_ONLY);
                record.thunderbolt_firmware_access = access;
                record.touchpad_firmware_access = access;
            }
            Self::Thunderbolt(version) => record.thunderbolt_firmware_version = text(version),
            Self::Touchpad(version) => record.touchpad_firmware_version = text(version),
        }
    }
}

/// Managed `Capture()`, one part at a time: `report` takes each part as it is
/// read and returns whether to go on. The two device reads are for the exact
/// RZ09-0581 only, as managed makes them.
pub(super) fn capture(capture: &dyn Capture, mut report: impl FnMut(Part) -> bool) {
    let registry = |name| capture.registry(BIOS_KEY, name).unwrap_or(Value::Missing);
    let vbios = capture
        .video_adapters()
        .ok()
        .and_then(|adapters| vbios(&video_candidates(&adapters)));
    // The catalog is compiled in and validated by the discovery tests, so a
    // lookup error cannot occur in an image; it would leave the model as read
    // and skip the device reads, which is the safe side.
    let sku = format_text(&registry("SystemSKU"));
    let catalog = if sku == UNAVAILABLE {
        None
    } else {
        catalog_model(&sku).ok().flatten()
    };
    let model = catalog.map_or(sku, str::to_owned);
    let target = catalog == Some(TARGET_MODEL);
    let go_on = report(Part::Registry {
        bios: format_text(&registry("BIOSVersion")),
        ec: ec_version(
            &registry("ECFirmwareMajorRelease"),
            &registry("ECFirmwareMinorRelease"),
        ),
        model,
        vbios,
        target,
    });
    if !go_on || !target {
        return;
    }
    let routers = capture.thunderbolt_routers().ok();
    if !report(Part::Thunderbolt(routers.as_deref().and_then(thunderbolt))) {
        return;
    }
    let interfaces = touchpad_attributes(capture).ok();
    report(Part::Touchpad(interfaces.as_deref().and_then(touchpad)));
}

// --- Schedule ---

enum State {
    /// No Windows source: nothing is captured and nothing is published.
    Absent,
    /// The source, handed to the worker by the first poll.
    Idle(Arc<dyn Capture>),
    Running {
        parts: Receiver<Part>,
        started: Instant,
        record: FirmwareVersions,
    },
    Done(FirmwareVersions),
}

/// Folds the parts the worker has reported into `record`, and returns
/// whether it has ended.
fn collect(parts: &Receiver<Part>, record: &mut FirmwareVersions) -> bool {
    loop {
        match parts.try_recv() {
            Ok(part) => part.apply(record),
            Err(TryRecvError::Empty) => return false,
            Err(TryRecvError::Disconnected) => return true,
        }
    }
}

/// The installed read source's firmware versions: one capture for the source,
/// which the Service acquires once at start, begun by the first poll, and the
/// MCU version its read job reads.
pub(super) struct Host {
    state: RefCell<State>,
    mcu: mcu_version::Inventory,
}

impl Host {
    /// No Windows source: nothing is captured and nothing is published.
    pub(super) fn none() -> Self {
        Self {
            state: RefCell::new(State::Absent),
            mcu: mcu_version::Inventory::default(),
        }
    }

    pub(super) fn new(capture: Arc<dyn Capture>) -> Self {
        Self {
            state: RefCell::new(State::Idle(capture)),
            mcu: mcu_version::Inventory::default(),
        }
    }

    /// The captured record, or None while the capture runs, and always
    /// without a source. The first poll starts the capture. It returns at
    /// once: it only collects what the worker has reported.
    pub(super) fn poll(&self, now: Instant) -> Option<FirmwareVersions> {
        let mut state = self.state.borrow_mut();
        if let State::Idle(_) = &*state
            && let State::Idle(capture) = std::mem::replace(&mut *state, State::Absent)
        {
            *state = Self::start(capture, now);
        }
        if let State::Running {
            parts,
            started,
            record,
        } = &mut *state
        {
            let ended = collect(parts, record);
            if ended || now.saturating_duration_since(*started) >= DEADLINE {
                *state = State::Done(record.clone());
            }
        }
        match &*state {
            State::Done(record) => Some(self.mcu.apply(record.clone())),
            _ => None,
        }
    }

    /// The counter a read job passes to the worker to send the MCU version
    /// query, or None: without a source (managed reads no MCU version without
    /// a record), once the version is known, or once the budget since the
    /// `resume` generation is spent (`mcu_version::Inventory::query`).
    pub(super) fn mcu_query(&self, resume: impl FnOnce() -> Option<u64>) -> Option<McuQuerySends> {
        if matches!(*self.state.borrow(), State::Absent) {
            return None;
        }
        self.mcu.query(resume)
    }

    /// What an accepted reading learned about the MCU version.
    pub(super) fn mcu_read(&self, read: McuVersionRead) {
        self.mcu.record(read);
    }

    /// The queries this source has sent.
    #[cfg(test)]
    pub(super) fn mcu_sends_for_tests(&self) -> u64 {
        self.mcu.sends_for_tests().count()
    }

    /// Stops listening. A capture not yet started never starts, and a worker
    /// still running ends after the read it is in, since its next report
    /// fails. What it had reported is the record.
    /// It then publishes that partial record, so Stop relies on the drain
    /// dropping the publisher before it calls this: nothing may decorate a
    /// status after it.
    pub(super) fn stop(&self) {
        let mut state = self.state.borrow_mut();
        match &mut *state {
            State::Idle(_) => *state = State::Absent,
            State::Running { parts, record, .. } => {
                collect(parts, record);
                *state = State::Done(record.clone());
            }
            State::Absent | State::Done(_) => {}
        }
    }

    fn start(capture: Arc<dyn Capture>, now: Instant) -> State {
        let (send, parts): (Sender<Part>, Receiver<Part>) = channel();
        let spawned = thread::Builder::new()
            .name("firmware-versions".into())
            .spawn(move || {
                // A send fails once the host stopped listening: stop reading.
                self::capture(&*capture, |part| send.send(part).is_ok());
            });
        match spawned {
            Ok(_) => State::Running {
                parts,
                started: now,
                record: unavailable(),
            },
            Err(_) => State::Done(unavailable()),
        }
    }
}

#[cfg(test)]
#[path = "firmware_versions_tests.rs"]
mod tests;

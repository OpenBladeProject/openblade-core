use serde::{Deserialize, Serialize};
use std::{
    collections::{HashMap, HashSet},
    io,
    sync::atomic::{AtomicBool, Ordering},
};

#[cfg(any(test, feature = "hid-identity-compile-check"))]
mod hid_lease;
#[cfg(all(windows, any(test, feature = "service-hardware-compile-check")))]
#[allow(unused_imports)] // Shared with the standalone discovery binary.
pub(crate) use hid_lease::performance;
#[cfg(all(windows, test))]
#[allow(unused_imports)] // Consumed by the service host's test harness.
pub(crate) use hid_lease::tests::service_fixture;
mod pnp;
#[cfg(test)]
mod probe_inventory;
#[cfg(test)]
mod tests;
#[cfg(all(
    windows,
    any(not(test), feature = "firmware-performance-compile-check")
))]
pub(crate) mod windows;

const MAX_INTERFACES: usize = 4096;
const MAX_REGISTRY_TEXT: usize = 256;
const INVENTORY: &str = include_str!(
    "../../../../tests/OpenBlade.Protocol.Tests/Fixtures/windows-rust-model-evidence-inventory.json"
);

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Inventory<'a> {
    schema_version: u8,
    #[serde(borrow)]
    models: Vec<Model<'a>>,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Model<'a> {
    model_number: &'a str,
    windows_registry_product_name: &'a str,
    usb_vendor_id: &'a str,
    usb_product_id: &'a str,
    support_basis: &'a str,
    windows_control_usage: Option<(u16, u16)>,
}

impl Inventory<'_> {
    fn load() -> io::Result<Self> {
        let inventory: Self = serde_json::from_str(INVENTORY)?;
        let mut names = HashSet::new();
        let mut numbers = HashSet::new();
        let valid = inventory.schema_version == 2
            && !inventory.models.is_empty()
            && inventory.models.iter().all(|model| {
                names.insert(model.windows_registry_product_name)
                    && numbers.insert(model.model_number)
                    && model.device_id().is_some()
                    && match model.support_basis {
                        "PhysicallyValidated" => model.windows_control_usage.is_some(),
                        "SourceReviewed" => model.windows_control_usage.is_none(),
                        _ => false,
                    }
            });
        if !valid {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                "invalid Windows model projection",
            ));
        }
        Ok(inventory)
    }
}

impl Model<'_> {
    fn device_id(&self) -> Option<(u16, u16)> {
        Some((
            pnp::hex(self.usb_vendor_id, 4)?,
            pnp::hex(self.usb_product_id, 4)?,
        ))
    }
}

/// Managed `RazerBladeModelCatalog.Find`: the catalog model number that the
/// trimmed `text` starts with, ignoring ASCII case. The catalog is the model
/// inventory; an invalid inventory is an error rather than an unknown model.
#[allow(dead_code)] // The standalone discovery binary does not look a model up by text.
pub(crate) fn catalog_model(text: &str) -> io::Result<Option<&'static str>> {
    let text = text.trim();
    Ok(Inventory::load()?
        .models
        .into_iter()
        .map(|model| model.model_number)
        .find(|number| {
            text.get(..number.len())
                .is_some_and(|prefix| prefix.eq_ignore_ascii_case(number))
        }))
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase", tag = "state", content = "value")]
enum RegistryText {
    Present(String),
    Missing,
    Malformed,
    Unavailable,
}

impl RegistryText {
    fn value(&self) -> Option<&str> {
        match self {
            Self::Present(value) => Some(value),
            _ => None,
        }
    }

    fn state(&self) -> TextState {
        match self {
            Self::Present(_) => TextState::Present,
            Self::Missing => TextState::Missing,
            Self::Malformed => TextState::Malformed,
            Self::Unavailable => TextState::Unavailable,
        }
    }

    fn from_utf16(units: &[u16]) -> Self {
        let Some(text) = units.strip_suffix(&[0]) else {
            return Self::Malformed;
        };
        if text.len() > MAX_REGISTRY_TEXT {
            return Self::Malformed;
        }
        match String::from_utf16(text) {
            Ok(text) if text.chars().any(char::is_control) => Self::Malformed,
            Ok(text) if text.trim().is_empty() => Self::Missing,
            Ok(text) => Self::Present(text),
            Err(_) => Self::Malformed,
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
enum TextState {
    Present,
    Missing,
    Malformed,
    Unavailable,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
enum Platform {
    Supported,
    Unsupported,
    Unavailable,
}

#[derive(Clone)]
struct RegistrySource {
    manufacturer: RegistryText,
    product_name: RegistryText,
}

#[derive(Clone)]
struct HostObservation {
    platform: Platform,
    bios: RegistrySource,
    system_information: RegistrySource,
    sku: RegistryText,
    bios_version: RegistryText,
}

#[derive(Debug, PartialEq, Eq, Serialize)]
#[serde(
    rename_all = "camelCase",
    rename_all_fields = "camelCase",
    tag = "state"
)]
enum HostMatch<'a> {
    Incomplete {
        bios_manufacturer: TextState,
        bios_product_name: TextState,
        system_manufacturer: TextState,
        system_product_name: TextState,
    },
    Contradictory,
    Unknown,
    Matched {
        model_number: &'a str,
        support_basis: &'a str,
        sku: &'a RegistryText,
        bios_version: &'a RegistryText,
    },
}

impl HostObservation {
    fn resolve<'a>(
        &'a self,
        inventory: &'a Inventory<'a>,
    ) -> (HostMatch<'a>, Option<&'a Model<'a>>) {
        let (
            Some(bios_manufacturer),
            Some(bios_product),
            Some(system_manufacturer),
            Some(system_product),
        ) = (
            self.bios.manufacturer.value(),
            self.bios.product_name.value(),
            self.system_information.manufacturer.value(),
            self.system_information.product_name.value(),
        )
        else {
            return (
                HostMatch::Incomplete {
                    bios_manufacturer: self.bios.manufacturer.state(),
                    bios_product_name: self.bios.product_name.state(),
                    system_manufacturer: self.system_information.manufacturer.state(),
                    system_product_name: self.system_information.product_name.state(),
                },
                None,
            );
        };
        if bios_manufacturer != system_manufacturer || bios_product != system_product {
            return (HostMatch::Contradictory, None);
        }
        let model = inventory.models.iter().find(|model| {
            bios_manufacturer == "Razer" && bios_product == model.windows_registry_product_name
        });
        match model {
            Some(model) => (
                HostMatch::Matched {
                    model_number: model.model_number,
                    support_basis: model.support_basis,
                    sku: &self.sku,
                    bios_version: &self.bios_version,
                },
                (self.platform == Platform::Supported).then_some(model),
            ),
            None => (HostMatch::Unknown, None),
        }
    }
}

#[derive(Clone)]
struct Interface {
    // Windows supplies this opaque interface name. It is never parsed; only the
    // firmware touchpad read receives it, to open the selected interface.
    path: String,
    hardware_ids: Option<Vec<String>>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
enum ScanState {
    Complete,
    Incomplete,
    Unavailable,
}

#[derive(Debug, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
enum CandidateState {
    NotEvaluated,
    None,
    SingleUnresolved,
    Ambiguous,
    Incomplete,
}

#[derive(Debug, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
struct CandidateCounts {
    matching_hints: usize,
    missing_usage_hint: usize,
    wrong_usage_hint: usize,
}

#[derive(Debug, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
struct HidEvidence {
    inventory: ScanState,
    interface_count: usize,
    unreadable_interfaces: usize,
    duplicate_interfaces: usize,
    candidates: CandidateState,
    counts: Option<CandidateCounts>,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct Snapshot<'a> {
    schema_version: u8,
    platform: Platform,
    registry_match: HostMatch<'a>,
    hid: HidEvidence,
}

trait DiscoverySource {
    fn read_host(&self, cancelled: &AtomicBool) -> io::Result<HostObservation>;

    // The source emits one row for each present interface, including failed row reads.
    fn scan_hid(
        &self,
        cancelled: &AtomicBool,
        visit: impl FnMut(Option<Interface>) -> io::Result<()>,
    ) -> io::Result<ScanState>;
}

fn check_cancelled(cancelled: &AtomicBool) -> io::Result<()> {
    if cancelled.load(Ordering::Relaxed) {
        Err(io::Error::new(
            io::ErrorKind::Interrupted,
            "discovery cancelled",
        ))
    } else {
        Ok(())
    }
}

fn discover(source: &impl DiscoverySource, cancelled: &AtomicBool) -> io::Result<String> {
    check_cancelled(cancelled)?;
    let inventory = Inventory::load()?;
    let host = source.read_host(cancelled)?;
    check_cancelled(cancelled)?;
    let (registry_match, model) = host.resolve(&inventory);
    let (hid, _) = discover_hid(source, model, cancelled)?;
    let snapshot = serde_json::to_string(&Snapshot {
        schema_version: 1,
        platform: host.platform,
        registry_match,
        hid,
    })?;
    check_cancelled(cancelled)?;
    Ok(snapshot)
}

/// Whether any present HID interface's devnode hardware IDs contain `token`,
/// compared ASCII case-insensitively, for a presence check that opens no
/// interface. Unlike `windows::hid_interfaces`, a row or ID list that cannot
/// be read is an error, as is an incomplete scan: absence is reported only
/// from a complete scan in which every interface's IDs were read.
#[cfg(any(test, feature = "service-hyperboost-v1"))]
fn hardware_id_present(
    source: &impl DiscoverySource,
    cancelled: &AtomicBool,
    token: &str,
) -> io::Result<bool> {
    let token = token.to_ascii_uppercase();
    let mut present = false;
    let state = source.scan_hid(cancelled, |interface| {
        let ids = interface
            .and_then(|interface| interface.hardware_ids)
            .ok_or_else(|| io::Error::other("HID interface hardware IDs are unreadable"))?;
        present |= ids
            .iter()
            .any(|id| id.to_ascii_uppercase().contains(&token));
        Ok(())
    })?;
    if state == ScanState::Complete {
        Ok(present)
    } else {
        Err(io::Error::other("HID enumeration incomplete"))
    }
}

#[cfg(all(windows, not(test), feature = "installed-service-candidate"))]
#[allow(unused_imports)] // The standalone discovery binary also compiles this module.
pub(crate) use status_metadata::{StatusHid, StatusMetadata, observe_status_metadata};

#[cfg(all(windows, not(test), feature = "installed-service-candidate"))]
#[allow(dead_code)]
mod status_metadata {
    use super::*;

    pub(crate) enum StatusMetadata {
        Unsupported,
        Unavailable,
        MatchedHost { model: String, hid: StatusHid },
    }

    pub(crate) enum StatusHid {
        NoCandidate,
        Candidate,
        Ambiguous,
        Unavailable,
    }

    pub(crate) fn observe_status_metadata(cancel: &AtomicBool) -> io::Result<StatusMetadata> {
        let inventory = Inventory::load()?;
        let host = windows::WindowsMetadata.read_host(cancel)?;
        let (host_match, model) = host.resolve(&inventory);
        let Some(model) = model else {
            return Ok(match (host.platform, host_match) {
                (Platform::Supported, HostMatch::Unknown) => StatusMetadata::Unsupported,
                _ => StatusMetadata::Unavailable,
            });
        };
        // A SetupAPI/PnP hint does not open a device or verify a lease.
        let hid = match discover_hid(&windows::WindowsMetadata, Some(model), cancel) {
            Ok((evidence, _)) => match evidence.candidates {
                CandidateState::None => StatusHid::NoCandidate,
                CandidateState::SingleUnresolved => StatusHid::Candidate,
                CandidateState::Ambiguous => StatusHid::Ambiguous,
                CandidateState::NotEvaluated | CandidateState::Incomplete => StatusHid::Unavailable,
            },
            Err(error) if error.kind() == io::ErrorKind::Interrupted => return Err(error),
            Err(_) => StatusHid::Unavailable,
        };
        Ok(StatusMetadata::MatchedHost {
            model: model.model_number.to_owned(),
            hid,
        })
    }
}

#[cfg_attr(
    not(any(test, feature = "hid-identity-compile-check")),
    allow(dead_code)
)]
struct CandidateHint {
    path: String,
    facts: pnp::Facts,
}

/// One bounded HID scan: every interface by its case-folded path, with its
/// PnP facts, or none when unreadable or contradicted by a duplicate.
struct Scan {
    inventory: ScanState,
    interface_count: usize,
    unreadable_interfaces: usize,
    duplicate_interfaces: usize,
    interfaces: HashMap<String, (String, Option<pnp::Facts>)>,
}

fn scan(source: &impl DiscoverySource, cancelled: &AtomicBool) -> io::Result<Scan> {
    let mut interfaces = HashMap::new();
    let mut interface_count = 0;
    let mut unreadable_interfaces = 0;
    let mut duplicate_interfaces = 0;
    let scan = source.scan_hid(cancelled, |interface| {
        check_cancelled(cancelled)?;
        if interface_count == MAX_INTERFACES {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                "HID enumeration limit exceeded",
            ));
        }
        interface_count += 1;
        let Some(interface) = interface else {
            unreadable_interfaces += 1;
            return Ok(());
        };
        let facts = interface.hardware_ids.as_deref().and_then(pnp::facts);
        if facts.is_none() {
            unreadable_interfaces += 1;
        }
        match interfaces.entry(interface.path.to_ascii_uppercase()) {
            std::collections::hash_map::Entry::Vacant(entry) => {
                entry.insert((interface.path, facts));
            }
            std::collections::hash_map::Entry::Occupied(mut entry) => {
                duplicate_interfaces += 1;
                if entry.get().1 != facts {
                    entry.get_mut().1 = None;
                }
            }
        }
        Ok(())
    })?;
    check_cancelled(cancelled)?;

    let inventory = if scan == ScanState::Complete
        && (unreadable_interfaces != 0 || duplicate_interfaces != 0)
    {
        ScanState::Incomplete
    } else {
        scan
    };
    Ok(Scan {
        inventory,
        interface_count,
        unreadable_interfaces,
        duplicate_interfaces,
        interfaces,
    })
}

fn discover_hid(
    source: &impl DiscoverySource,
    model: Option<&Model<'_>>,
    cancelled: &AtomicBool,
) -> io::Result<(HidEvidence, Option<CandidateHint>)> {
    let expected_id = model.and_then(Model::device_id);
    let Scan {
        inventory,
        interface_count,
        unreadable_interfaces,
        duplicate_interfaces,
        interfaces,
    } = scan(source, cancelled)?;
    let mut hint = None;
    let counts = model.zip(expected_id).map(|(model, expected_id)| {
        let mut counts = CandidateCounts {
            matching_hints: 0,
            missing_usage_hint: 0,
            wrong_usage_hint: 0,
        };
        for (path, facts) in interfaces.into_values() {
            let Some(facts) = facts.filter(|facts| facts.device() == Some(expected_id)) else {
                continue;
            };
            match (model.windows_control_usage, facts.usage) {
                (Some(_), None) => counts.missing_usage_hint += 1,
                (Some(expected), Some(usage)) if usage != expected => counts.wrong_usage_hint += 1,
                _ => {
                    counts.matching_hints += 1;
                    hint = Some(CandidateHint { path, facts });
                }
            }
        }
        counts
    });
    let candidates = match &counts {
        None => CandidateState::NotEvaluated,
        Some(counts) if counts.matching_hints > 1 => CandidateState::Ambiguous,
        Some(counts) if inventory != ScanState::Complete || counts.missing_usage_hint > 0 => {
            CandidateState::Incomplete
        }
        Some(counts) if counts.matching_hints == 1 => CandidateState::SingleUnresolved,
        Some(_) => CandidateState::None,
    };
    let hint = hint.filter(|_| candidates == CandidateState::SingleUnresolved);
    Ok((
        HidEvidence {
            inventory,
            interface_count,
            unreadable_interfaces,
            duplicate_interfaces,
            candidates,
            counts,
        },
        hint,
    ))
}

/// Every HID interface of `model`'s exact device whose PnP usage hint is
/// `usage`, sorted by path, from one complete scan. An incomplete scan, or
/// an interface of the device without a usage hint, fails closed, as it
/// leaves the control candidate `Incomplete`; the caller selects among the
/// candidates by their opened metadata.
#[cfg_attr(not(feature = "service-lighting-v1"), allow(dead_code))]
fn discover_role(
    source: &impl DiscoverySource,
    model: &Model<'_>,
    usage: (u16, u16),
    cancelled: &AtomicBool,
) -> io::Result<Vec<CandidateHint>> {
    let expected_id = model
        .device_id()
        .ok_or_else(|| role_refused("no exact HID device ID"))?;
    let scan = scan(source, cancelled)?;
    if scan.inventory != ScanState::Complete {
        return Err(role_refused("HID inventory is incomplete"));
    }
    let mut hints = Vec::new();
    for (path, facts) in scan.interfaces.into_values() {
        let Some(facts) = facts.filter(|facts| facts.device() == Some(expected_id)) else {
            continue;
        };
        match facts.usage {
            None => return Err(role_refused("an exact HID interface has no usage hint")),
            Some(found) if found == usage => hints.push(CandidateHint { path, facts }),
            Some(_) => {}
        }
    }
    hints.sort_by_cached_key(|hint| hint.path.to_ascii_uppercase());
    Ok(hints)
}

fn role_refused(message: &'static str) -> io::Error {
    io::Error::new(io::ErrorKind::PermissionDenied, message)
}

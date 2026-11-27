//! Devnode hardware-ID facts for the bounded HID inventory.
//!
//! HIDClass lists a transport device ID (`HID\VID_v&PID_p...` for USB, a
//! Bluetooth service-UUID ID, or another family such as ACPI), the
//! `HID\VID_v&UP:p_U:u` vendor usage alias and generic usage IDs. Only the
//! alias is built from the device attributes that managed reads from each
//! opened handle (`HidD_GetAttributes`) before it counts an exact interface;
//! the transport ID's VID/PID is the transport's. Every product ID here is
//! therefore a hint that the opened handle must confirm, never admission.

/// One devnode's facts. `collection` is the device and collection path the
/// transport ID names. It is `None` only for a devnode whose IDs establish
/// an unprotected vendor without a parsed device ID. An ID list that could
/// name a protected vendor's interface that this grammar does not read, or
/// that establishes no vendor, has no facts, so the scan is incomplete.
#[derive(Debug, PartialEq, Eq)]
pub(super) struct Facts {
    collection: Option<Collection>,
    pub usage: Option<(u16, u16)>,
}

impl Facts {
    pub(super) fn device(&self) -> Option<(u16, u16)> {
        self.collection.as_ref().map(|collection| collection.device)
    }
}

#[derive(Debug, PartialEq, Eq)]
struct Collection {
    device: (u16, u16),
    interface: Option<u16>,
    // The XInput `IG_nn` field HIDClass lists for game controllers.
    interface_group: Option<u16>,
    // Outermost first: `&COL03&COL01` is collection 1 nested in collection 3.
    collections: Vec<u16>,
}

// HIDClass appends one `&COLnn` per nesting level. Two is the depth present
// on RZ09-0581 (`MI_00&Col03&Col01`); a deeper path is not parsed.
const MAX_COLLECTION_DEPTH: usize = 2;

// The `&` fields after the product ID in HIDClass order. Only a nested
// collection repeats.
const REVISION: u8 = 1;
const INTERFACE: u8 = 2;
const INTERFACE_GROUP: u8 = 3;
const COLLECTION: u8 = 4;

// Bluetooth HID transport IDs: HID over GATT (LE) with a two-digit vendor-ID
// source, and the classic HID profile with a four-digit one. The source is 1
// (Bluetooth SIG) or 2 (USB-IF); either way the four-digit vendor follows.
const LE_PREFIX: &str = r"HID\{00001812-0000-1000-8000-00805F9B34FB}_DEV_VID&";
const CLASSIC_PREFIX: &str = r"HID\{00001124-0000-1000-8000-00805F9B34FB}_VID&";

/// What one hardware ID says about its devnode.
enum Id {
    /// A transport device ID this grammar reads in full.
    Device {
        collection: Collection,
        revision: Option<u16>,
    },
    /// The vendor usage alias.
    UsageAlias { vendor: u16, usage: (u16, u16) },
    /// `HID_DEVICE_UP:p_U:u`.
    Usage((u16, u16)),
    /// A device ID of another family, or an unprotected vendor's device ID
    /// whose remainder is not read, with the vendor when it names one.
    OtherDevice { vendor: Option<u16> },
    /// A generic ID (`HID_DEVICE`, `HID_DEVICE_SYSTEM_*`, a usage range).
    Generic,
}

/// The facts of one devnode's `ids`, or none when they cannot rule out an
/// interface of a `protected` vendor (the catalog's): an ID naming one that
/// the grammar does not read, a protected vendor alias without a product,
/// IDs that disagree, or no established vendor at all. A devnode with a
/// parsed device ID or an unprotected vendor is readable; an unprotected
/// vendor cannot be one managed counts, whatever its device ID's remainder.
/// Use devnode IDs independently of the opaque interface name. Every device
/// ID, alias and usage ID must agree.
pub(super) fn facts(ids: &[String], protected: &[u16]) -> Option<Facts> {
    let mut identity = None;
    let mut vendor = None;
    let mut revision = None;
    let mut usage = None;
    let mut device_id = false;
    for id in ids {
        match classify(&id.to_ascii_uppercase(), protected)? {
            Id::Device {
                collection,
                revision: found,
            } => {
                agree(&mut vendor, collection.device.0)?;
                if let Some(found) = found {
                    agree(&mut revision, found)?;
                }
                agree(&mut identity, collection)?;
            }
            Id::UsageAlias {
                vendor: found,
                usage: pair,
            } => {
                agree(&mut vendor, found)?;
                agree(&mut usage, pair)?;
            }
            Id::Usage(pair) => agree(&mut usage, pair)?,
            Id::OtherDevice { vendor: found } => {
                device_id = true;
                if let Some(found) = found {
                    agree(&mut vendor, found)?;
                }
            }
            Id::Generic => {}
        }
    }
    let foreign = device_id && vendor.is_some_and(|vendor| !protected.contains(&vendor));
    (identity.is_some() || foreign).then_some(Facts {
        collection: identity,
        usage,
    })
}

/// One upper-case ID, or none when it could name a protected vendor's
/// interface in a form this grammar does not read.
fn classify(id: &str, protected: &[u16]) -> Option<Id> {
    if let Some(usage) = id.strip_prefix("HID_DEVICE_UP:") {
        return usage_pair(usage).map(Id::Usage);
    }
    if let Some((vendor, rest, form)) = vendor_id(id) {
        if let Some(parsed) = form(vendor, rest) {
            return Some(parsed);
        }
        // The remainder is not read, so any protected vendor it names, the
        // parsed one included, refuses.
        return (!names_vendor(id, protected)).then_some(Id::OtherDevice {
            vendor: Some(vendor),
        });
    }
    if names_vendor(id, protected) {
        return None;
    }
    Some(
        if id
            .strip_prefix(r"HID\")
            .is_some_and(|name| !name.is_empty())
        {
            Id::OtherDevice { vendor: None }
        } else {
            Id::Generic
        },
    )
}

type Form = fn(u16, &str) -> Option<Id>;

/// The vendor of a USB or Bluetooth device ID, the text after it and the
/// form that reads that text.
fn vendor_id(id: &str) -> Option<(u16, &str, Form)> {
    if let Some(rest) = id.strip_prefix(r"HID\VID_") {
        return Some((hex(rest.get(..4)?, 4)?, &rest[4..], usb));
    }
    for (prefix, width) in [(LE_PREFIX, 2), (CLASSIC_PREFIX, 4)] {
        if let Some(rest) = id.strip_prefix(prefix) {
            if !matches!(hex(rest.get(..width)?, width)?, 1 | 2) {
                return None;
            }
            let rest = &rest[width..];
            return Some((hex(rest.get(..4)?, 4)?, &rest[4..], bluetooth));
        }
    }
    None
}

/// `&UP:p_U:u`, or `&PID_p` and the `&` fields.
fn usb(vendor: u16, rest: &str) -> Option<Id> {
    let rest = rest.strip_prefix('&')?;
    if let Some(usage) = rest.strip_prefix("UP:") {
        return Some(Id::UsageAlias {
            vendor,
            usage: usage_pair(usage)?,
        });
    }
    let mut fields = rest.split('&');
    let product = hex(fields.next()?.strip_prefix("PID_")?, 4)?;
    device((vendor, product), None, fields)
}

/// `_PID&p`, an optional `_REV&r`, and the `&` fields.
fn bluetooth(vendor: u16, rest: &str) -> Option<Id> {
    let rest = rest.strip_prefix("_PID&")?;
    let product = hex(rest.get(..4)?, 4)?;
    let mut rest = &rest[4..];
    let mut revision = None;
    if let Some(value) = rest.strip_prefix("_REV&") {
        revision = Some(hex(value.get(..4)?, 4)?);
        rest = &value[4..];
    }
    let fields = match rest {
        "" => None,
        rest => Some(rest.strip_prefix('&')?.split('&')),
    };
    device((vendor, product), revision, fields.into_iter().flatten())
}

/// The `&` fields after a product ID, in HIDClass order, after any revision
/// the transport ID already carried.
fn device<'a>(
    device: (u16, u16),
    mut revision: Option<u16>,
    fields: impl Iterator<Item = &'a str>,
) -> Option<Id> {
    let mut interface = None;
    let mut interface_group = None;
    let mut collections = Vec::new();
    let mut last_field = if revision.is_some() { REVISION } else { 0 };
    for part in fields {
        let field = if let Some(value) = part.strip_prefix("REV_") {
            revision = Some(hex(value, 4)?);
            REVISION
        } else if let Some(value) = part.strip_prefix("MI_") {
            interface = Some(hex(value, 2)?);
            INTERFACE
        } else if let Some(value) = part.strip_prefix("IG_") {
            interface_group = Some(hex(value, 2)?);
            INTERFACE_GROUP
        } else {
            if collections.len() == MAX_COLLECTION_DEPTH {
                return None;
            }
            collections.push(hex(part.strip_prefix("COL")?, 2)?);
            COLLECTION
        };
        if field < last_field || (field == last_field && field != COLLECTION) {
            return None;
        }
        last_field = field;
    }
    Some(Id::Device {
        collection: Collection {
            device,
            interface,
            interface_group,
            collections,
        },
        revision,
    })
}

/// Whether `id` names a protected vendor after any `VID`: as `VID_v`, or as
/// a Bluetooth `VID&` field with no, a two-digit or a four-digit source.
fn names_vendor(id: &str, protected: &[u16]) -> bool {
    id.match_indices("VID").any(|(at, _)| {
        let field = &id[at + 3..];
        let vendor_at = |offset: usize| {
            field
                .get(offset..offset + 4)
                .and_then(|vendor| hex(vendor, 4))
                .is_some_and(|vendor| protected.contains(&vendor))
        };
        match field.as_bytes().first() {
            Some(b'_') => vendor_at(1),
            Some(b'&') => [1, 3, 5].into_iter().any(vendor_at),
            _ => false,
        }
    })
}

fn agree<T: Eq>(observed: &mut Option<T>, value: T) -> Option<()> {
    if observed.as_ref().is_some_and(|old| old != &value) {
        return None;
    }
    *observed = Some(value);
    Some(())
}

fn usage_pair(id: &str) -> Option<(u16, u16)> {
    let (page, usage) = id.split_once("_U:")?;
    Some((hex(page, 4)?, hex(usage, 4)?))
}

pub(super) fn hex(text: &str, width: usize) -> Option<u16> {
    if text.len() != width || !text.bytes().all(|byte| byte.is_ascii_hexdigit()) {
        return None;
    }
    u16::from_str_radix(text, 16).ok()
}

pub(super) fn hardware_ids(data: &[u16]) -> Option<Vec<String>> {
    let ids = data.strip_suffix(&[0, 0])?;
    if ids.is_empty() {
        return None;
    }
    ids.split(|unit| *unit == 0)
        .map(|id| {
            let id = String::from_utf16(id).ok()?;
            (!id.is_empty() && !id.chars().any(char::is_control)).then_some(id)
        })
        .collect()
}

/// One devnode's hardware-ID facts. `collection` is the exact HID device and
/// collection path the IDs name. It is `None` only for a devnode the IDs
/// establish as foreign: another vendor's, or an ID family with no vendor and
/// nothing naming Razer. An ID list that could name a Razer interface it does
/// not fully parse has no facts, so the scan is incomplete.
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
    // Outermost first: `&COL03&COL01` is collection 1 nested in collection 3.
    collections: Vec<u16>,
}

const RAZER_VENDOR: u16 = 0x1532;

// HIDClass appends one `&COLnn` per nesting level. Two is the depth present
// on RZ09-0581 (`MI_00&Col03&Col01`); a deeper path is not parsed.
const MAX_COLLECTION_DEPTH: usize = 2;

// The `HID\VID_` fields in HIDClass order. Only a nested collection repeats.
const REVISION: u8 = 1;
const INTERFACE: u8 = 2;
const COLLECTION: u8 = 3;

// Use devnode IDs independently of the opaque interface name. The revisioned
// and unrevisioned aliases must agree on VID/PID, MI and collection.
//
// HIDClass builds `HID\VID_v&PID_p...` and the `HID\VID_v&UP:...` alias from
// the device attributes that managed reads from each opened handle
// (`HidD_GetAttributes`) before it counts an exact interface. A devnode whose
// alias names another vendor therefore cannot be one managed would count,
// and is foreign without a product ID. A Razer alias without a product ID,
// or Razer named in any ID outside this grammar, could be the admitted
// device, so it leaves the devnode without facts.
pub(super) fn facts(ids: &[String]) -> Option<Facts> {
    let mut identity = None;
    let mut vendor_hint = None;
    let mut revision = None;
    let mut usage = None;
    let mut other_device_id = false;
    for id in ids {
        let id = id.to_ascii_uppercase();
        if let Some(id) = id.strip_prefix(r"HID\VID_") {
            let mut parts = id.split('&');
            let vendor = hex(parts.next()?, 4)?;
            agree(&mut vendor_hint, vendor)?;
            let selector = parts.next()?;
            if let Some(value) = selector.strip_prefix("UP:") {
                // Some HIDClass lists carry a vendor-qualified usage alias.
                // It adds a usage hint but can never supply a missing PID.
                agree(&mut usage, usage_pair(value)?)?;
                if parts.next().is_some() {
                    return None;
                }
                continue;
            }

            let product = hex(selector.strip_prefix("PID_")?, 4)?;
            let mut interface = None;
            let mut collections = Vec::new();
            let mut last_field = 0;
            for part in parts {
                let field = if let Some(value) = part.strip_prefix("REV_") {
                    agree(&mut revision, hex(value, 4)?)?;
                    REVISION
                } else if let Some(value) = part.strip_prefix("MI_") {
                    interface = Some(hex(value, 2)?);
                    INTERFACE
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
            agree(
                &mut identity,
                Collection {
                    device: (vendor, product),
                    interface,
                    collections,
                },
            )?;
        } else if names_razer_vendor(&id) {
            return None;
        } else if let Some(id) = id.strip_prefix("HID_DEVICE_UP:") {
            agree(&mut usage, usage_pair(id)?)?;
        } else if id
            .strip_prefix(r"HID\")
            .is_some_and(|name| !name.is_empty())
        {
            // A different HID device ID family (e.g. ACPI vendor/device or a
            // converted keyboard) is readable inventory with no VID/PID hint.
            other_device_id = true;
        }
    }
    if identity.is_none() && (!other_device_id || vendor_hint == Some(RAZER_VENDOR)) {
        return None;
    }
    Some(Facts {
        collection: identity,
        usage,
    })
}

/// Whether an ID outside the `HID\VID_` grammar names the Razer vendor: as
/// `VID_1532`, or as a Bluetooth `VID&ss1532` field with its two-digit
/// vendor-ID source first.
fn names_razer_vendor(id: &str) -> bool {
    id.match_indices("VID").any(|(at, _)| {
        let field = &id[at + 3..];
        field
            .strip_prefix('_')
            .is_some_and(|vendor| vendor.starts_with("1532"))
            || field
                .strip_prefix('&')
                .and_then(|vendor| vendor.get(2..))
                .is_some_and(|vendor| vendor.starts_with("1532"))
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

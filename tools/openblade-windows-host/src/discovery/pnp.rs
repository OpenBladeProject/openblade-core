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
    collection: Option<u16>,
}

// Use devnode IDs independently of the opaque interface name. The revisioned
// and unrevisioned aliases must agree on VID/PID, MI and collection.
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
            let mut collection = None;
            let mut last_field = 0;
            for part in parts {
                let field = if let Some(value) = part.strip_prefix("REV_") {
                    agree(&mut revision, hex(value, 4)?)?;
                    1
                } else if let Some(value) = part.strip_prefix("MI_") {
                    interface = Some(hex(value, 2)?);
                    2
                } else {
                    collection = Some(hex(part.strip_prefix("COL")?, 2)?);
                    3
                };
                if field <= last_field {
                    return None;
                }
                last_field = field;
            }
            agree(
                &mut identity,
                Collection {
                    device: (vendor, product),
                    interface,
                    collection,
                },
            )?;
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
    if identity.is_none() && (!other_device_id || vendor_hint.is_some()) {
        return None;
    }
    Some(Facts {
        collection: identity,
        usage,
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

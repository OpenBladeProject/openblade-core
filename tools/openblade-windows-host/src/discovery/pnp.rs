#[derive(Debug, PartialEq, Eq)]
pub(super) struct Facts {
    pub device: (u16, u16),
    interface: Option<u16>,
    collection: Option<u16>,
    pub usage: Option<(u16, u16)>,
}

// HIDClass documents these IDs on the devnode, independently of its opaque interface name.
// Aliases may omit REV. Disagreement on any selector makes the row unusable.
pub(super) fn facts(ids: &[String]) -> Option<Facts> {
    if ids.is_empty() {
        return None;
    }
    let mut identity = None;
    let mut revision = None;
    let mut usage = None;
    for id in ids {
        let id = id.to_ascii_uppercase();
        if let Some(id) = id.strip_prefix(r"HID\VID_") {
            let mut parts = id.split('&');
            let vendor = hex(parts.next()?, 4)?;
            let product = hex(parts.next()?.strip_prefix("PID_")?, 4)?;
            let mut interface = None;
            let mut collection = None;
            // The documented order is REV, MI, COL, with each optional.
            let mut last_field = 0;
            for part in parts {
                let field = if let Some(value) = part.strip_prefix("REV_") {
                    let value = hex(value, 4)?;
                    if revision.replace(value).is_some_and(|old| old != value) {
                        return None;
                    }
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
            let observed = ((vendor, product), interface, collection);
            if identity
                .replace(observed)
                .is_some_and(|old| old != observed)
            {
                return None;
            }
        } else if let Some(id) = id.strip_prefix("HID_DEVICE_UP:") {
            let (page, value) = id.split_once("_U:")?;
            let observed = (hex(page, 4)?, hex(value, 4)?);
            if usage.replace(observed).is_some_and(|old| old != observed) {
                return None;
            }
        }
    }
    let (device, interface, collection) = identity?;
    Some(Facts {
        device,
        interface,
        collection,
        usage,
    })
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

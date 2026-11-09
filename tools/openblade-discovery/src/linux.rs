use crate::snapshot::{
    Entry, Hid, HostMetadata, MAX_HOST_TEXT, Platform, RAZER_VID, Snapshot, host_text,
};
use std::{
    fs::{self, File},
    io::{self, Read},
    path::Path,
};

pub fn discover(sys: &Path) -> Snapshot {
    let dmi = sys.join("class/dmi/id");
    let vendor = read_bounded(&dmi.join("sys_vendor"), MAX_HOST_TEXT + 1);
    let product = read_bounded(&dmi.join("product_name"), MAX_HOST_TEXT + 1);
    let host = classify_host(vendor.as_deref().ok(), product.as_deref().ok());

    let hid = match fs::read_dir(sys.join("bus/hid/devices")) {
        Ok(devices) => Hid::from_entries(devices.map(|device| {
            device
                .and_then(|device| read_bounded(&device.path().join("modalias"), 33))
                .map_or(Entry::Unreadable, |bytes| parse_modalias(&bytes))
        })),
        Err(_) => Hid::unavailable(),
    };
    Snapshot::new(Platform::Linux, host, hid)
}

fn read_bounded(path: &Path, max: usize) -> io::Result<Vec<u8>> {
    let mut bytes = Vec::with_capacity(max + 1);
    File::open(path)?
        .take((max + 1) as u64)
        .read_to_end(&mut bytes)?;
    if bytes.len() > max {
        return Err(io::ErrorKind::InvalidData.into());
    }
    Ok(bytes)
}

fn dmi_text(bytes: Option<&[u8]>) -> Option<&str> {
    host_text(bytes?.strip_suffix(b"\n")?)
}

fn classify_host(vendor: Option<&[u8]>, product: Option<&[u8]>) -> HostMetadata {
    match (dmi_text(vendor), dmi_text(product)) {
        (Some(vendor), product)
            if vendor.eq_ignore_ascii_case("Razer")
                || vendor.eq_ignore_ascii_case("Razer Inc.")
                || vendor.eq_ignore_ascii_case("Razer Inc") =>
        {
            if product.is_some_and(is_blade_name) {
                HostMetadata::RazerBladeReported
            } else {
                HostMetadata::Unverified
            }
        }
        (Some(_), _) => HostMetadata::OtherVendor,
        (None, _) => HostMetadata::Unavailable,
    }
}

fn is_blade_name(name: &str) -> bool {
    let mut words = name.split_ascii_whitespace();
    let mut model = words.next();
    if model.is_some_and(|word| word.eq_ignore_ascii_case("Razer")) {
        model = words.next();
    }
    model.is_some_and(|word| word.eq_ignore_ascii_case("Blade"))
}

fn parse_modalias(bytes: &[u8]) -> Entry {
    // Linux HID modalias_show: hid:bBBBBgGGGGvVVVVVVVVpPPPPPPPP followed by LF.
    let Ok(text) = std::str::from_utf8(bytes) else {
        return Entry::Unreadable;
    };
    let Some(fields) = text
        .strip_prefix("hid:b")
        .and_then(|t| t.strip_suffix('\n'))
    else {
        return Entry::Unreadable;
    };
    let hex = |digits: &str, width| -> Option<u32> {
        if digits.len() != width || !digits.bytes().all(|b| b.is_ascii_hexdigit()) {
            return None;
        }
        u32::from_str_radix(digits, 16).ok()
    };
    let decoded = (|| {
        let (bus, rest) = fields.split_once('g')?;
        let (group, rest) = rest.split_once('v')?;
        let (vid, pid) = rest.split_once('p')?;
        hex(group, 4)?;
        Some((hex(bus, 4)?, hex(vid, 8)?, hex(pid, 8)?))
    })();
    match decoded {
        Some((0x0003, vid, pid)) if vid == u32::from(RAZER_VID) => {
            u16::try_from(pid).map_or(Entry::Unreadable, Entry::Candidate)
        }
        Some(_) => Entry::Irrelevant,
        None => Entry::Unreadable,
    }
}

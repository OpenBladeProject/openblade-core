use serde::Serialize;
use std::collections::BTreeMap;

pub const RAZER_VID: u16 = 0x1532;
pub const MAX_ENTRIES: usize = 256;
pub const MAX_HOST_TEXT: usize = 128;

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub enum Platform {
    #[cfg(any(target_os = "linux", test))]
    Linux,
    #[cfg(any(target_os = "macos", test))]
    Macos,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub enum HostMetadata {
    Unavailable,
    Unverified,
    #[cfg(any(target_os = "linux", test))]
    OtherVendor,
    #[cfg(any(target_os = "linux", test))]
    RazerBladeReported,
}

// No OS strings are retained in the output model.
pub fn host_text(bytes: &[u8]) -> Option<&str> {
    if bytes.len() > MAX_HOST_TEXT || !bytes.iter().all(|b| (b' '..=b'~').contains(b)) {
        return None;
    }
    let text = std::str::from_utf8(bytes).ok()?.trim();
    (!text.is_empty()).then_some(text)
}

pub enum Entry {
    Candidate(u16),
    Irrelevant,
    Unreadable,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
enum HidState {
    Unavailable,
    Incomplete,
    None,
    Single,
    Ambiguous,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct Candidate {
    vendor_id: u16,
    product_id: u16,
    entries: u16,
}

#[derive(Serialize)]
pub struct Hid {
    state: HidState,
    candidates: Vec<Candidate>,
}

impl Hid {
    pub fn unavailable() -> Self {
        Self {
            state: HidState::Unavailable,
            candidates: Vec::new(),
        }
    }

    pub fn from_entries(entries: impl IntoIterator<Item = Entry>) -> Self {
        let mut products = BTreeMap::new();
        let mut incomplete = false;
        for (index, entry) in entries.into_iter().take(MAX_ENTRIES + 1).enumerate() {
            if index == MAX_ENTRIES {
                incomplete = true;
                break;
            }
            match entry {
                Entry::Candidate(pid) => *products.entry(pid).or_insert(0) += 1,
                Entry::Irrelevant => {}
                Entry::Unreadable => incomplete = true,
            }
        }
        let state = match (incomplete, products.values().sum::<u16>()) {
            (true, _) => HidState::Incomplete,
            (false, 0) => HidState::None,
            (false, 1) => HidState::Single,
            (false, _) => HidState::Ambiguous,
        };
        let candidates = products
            .into_iter()
            .map(|(product_id, entries)| Candidate {
                vendor_id: RAZER_VID,
                product_id,
                entries,
            })
            .collect();
        Self { state, candidates }
    }

    #[cfg(target_os = "macos")]
    pub fn invalidate(&mut self) {
        self.state = HidState::Incomplete;
    }
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Snapshot {
    schema_version: u8,
    platform: Platform,
    host_metadata: HostMetadata,
    exact_model: (),
    hid: Hid,
}

impl Snapshot {
    pub fn new(platform: Platform, host_metadata: HostMetadata, hid: Hid) -> Self {
        Self {
            schema_version: 1,
            platform,
            host_metadata,
            exact_model: (),
            hid,
        }
    }
}

#[cfg(test)]
mod tests;

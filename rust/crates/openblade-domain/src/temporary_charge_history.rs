//! A separate Power Mode Control contract; basic battery-v1 rows never change.
use crate::{
    battery_history::{Desired, Request},
    thermal_history::{deny, digest},
};
use serde::{Deserialize, Serialize};
use std::{io, string::String};

// No release has shipped this contract yet, so `superseded_ids` was added to it
// without a new name. The first release that ships a temporary-charge reader
// must include that member in both readers; otherwise adding it needs a new
// contract and capability (docs/windows-rust-thermal-history.md).
pub const CONTRACT: &str = "service-temporary-charge-v1/rz09-0581-v1";
// Source, full flags, optional policy, fresh source, fresh flags, SET,
// independent flags confirmation, one unused ID (pair allocator). No replay.
pub const EXCHANGES: usize = 8;
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Origin {
    pub request: String,
    pub intent: String,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct TemporaryChargeRecord {
    pub contract: String,
    pub requested: Request,
    pub configured: Desired,
    pub desired: Desired,
    pub source: u8,
    pub baseline: u8,
    pub target: u8,
    pub policy: Option<u8>,
    pub ids: [u8; EXCHANGES],
    // A successful enable retains these exact process-pool IDs for one source
    // cancellation. They are not restart authority or fan handback IDs.
    pub cancel_ids: Option<[u8; EXCHANGES]>,
    // An automatic cancel that may not resend its enable's `cancel_ids`, because
    // an earlier attempt sent one without a correlated reply, names them here
    // and carries fresh `ids` instead. Absent on every other row, so older
    // rows keep their bytes; readers that predate it refuse the member.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub superseded_ids: Option<[u8; EXCHANGES]>,
    pub origin: Option<Origin>,
    pub fan_owner: Option<String>,
}
impl TemporaryChargeRecord {
    pub fn validate(&self) -> io::Result<()> {
        let Request::SetFullChargeOverride { enabled } = self.requested else {
            return Err(deny());
        };
        if self.contract != CONTRACT
            || self.source > 2
            || self.baseline & !4 != 0
            || self.target != if enabled { 4 } else { 0 }
            || !(50..=99).contains(&self.configured.limit)
            || self.configured.requested(&self.requested) != self.desired
            || self.policy.is_some_and(|p| {
                !(p == 0x50
                    || p & 0x80 != 0 && (50..=80).contains(&(p & 0x7f)) && (p & 0x7f) % 5 == 0)
            })
            || enabled
                && (self.source == 1
                    || !self.configured.protection
                    || !self.policy.is_some_and(|p| p != 0x50 && p & 0x80 != 0)
                    || self.cancel_ids.is_none()
                    || self.origin.is_some())
            || !enabled && self.cancel_ids.is_some()
            // Only an automatic cancel replaces its enable's reserved IDs.
            || self.superseded_ids.is_some() && (enabled || self.origin.is_none())
            || self.origin.as_ref().is_some_and(|o| {
                self.source != 1
                    || !crate::thermal_history::is_guid(&o.request)
                    || o.intent.len() != 64
                    || !o
                        .intent
                        .bytes()
                        .all(|b| b.is_ascii_hexdigit() && !b.is_ascii_uppercase())
            })
        {
            return Err(deny());
        }
        // Fresh IDs share none with the reserved IDs they replace, which an
        // earlier attempt may have sent without a reply.
        let mut seen = [false; 256];
        for id in self
            .ids
            .iter()
            .chain(self.cancel_ids.iter().flatten())
            .chain(self.superseded_ids.iter().flatten())
        {
            if *id == 0 || *id == 255 || seen[*id as usize] {
                return Err(deny());
            }
            seen[*id as usize] = true;
        }
        Ok(())
    }
    pub fn hash(&self) -> io::Result<String> {
        Ok(digest(&serde_json::to_vec(self)?))
    }
    pub fn valid_progress(&self, p: u8) -> bool {
        matches!(p, 0 | 6 | 70 | 71 | 72 | 73)
    }
    pub fn follows(&self, p: u8, n: u8) -> bool {
        matches!((p, n), (0, 6) | (70, 71) | (71, 72))
            || p == 0 && n == 70 && self.baseline != self.target
            || p == 0 && n == 73 && self.baseline == self.target
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn temporary_charge_shared_rehashed_histories_preserve_family_and_original_creator() {
        let v: serde_json::Value = serde_json::from_str(include_str!(
            "../../../../tools/openblade-windows-host/tests/data/temporary-charge-history-v1.json"
        ))
        .unwrap();
        let record: TemporaryChargeRecord = serde_json::from_value(v["tfc"].clone()).unwrap();
        record.validate().unwrap();
        assert_eq!(serde_json::to_string(&record).unwrap(), v["canonical"]);
        assert_eq!(record.hash().unwrap(), v["intent"]);
        for case in v["cases"].as_array().unwrap() {
            let raw = case["journal"].as_str().unwrap().as_bytes();
            let parsed = crate::thermal_history::rows(raw, |_, _| Ok(()));
            if case["handbackOnly"] == true {
                parsed.unwrap();
                assert!(!crate::thermal_history::handback_complete(raw).unwrap());
            } else {
                assert_eq!(
                    parsed.is_ok(),
                    case["valid"].as_bool().unwrap(),
                    "{}",
                    case["name"]
                );
            }
        }
        // The old family's meanings do not expand with the new capability.
        let old: crate::battery_history::BatteryRecord = serde_json::from_str(include_str!(
            "../../../../tools/openblade-windows-host/tests/data/battery-history-v1.json"
        ))
        .ok()
        .unwrap_or_else(|| {
            serde_json::from_value(
                serde_json::from_str::<serde_json::Value>(include_str!(
                    "../../../../tools/openblade-windows-host/tests/data/battery-history-v1.json"
                ))
                .unwrap()["battery"]
                    .clone(),
            )
            .unwrap()
        });
        let mut old = old;
        old.requested = Request::SetFullChargeOverride { enabled: false };
        assert!(old.validate().is_err());
    }
    fn case(name: &str) -> std::vec::Vec<crate::thermal_history::Entry> {
        let v: serde_json::Value = serde_json::from_str(include_str!(
            "../../../../tools/openblade-windows-host/tests/data/temporary-charge-history-v1.json"
        ))
        .unwrap();
        let case = v["cases"]
            .as_array()
            .unwrap()
            .iter()
            .find(|c| c["name"] == name)
            .unwrap();
        case["journal"]
            .as_str()
            .unwrap()
            .lines()
            .map(|row| serde_json::from_str(row).unwrap())
            .collect()
    }
    fn journal(rows: &[crate::thermal_history::Entry]) -> std::vec::Vec<u8> {
        let mut bytes = std::vec::Vec::new();
        for row in rows {
            bytes.extend(serde_json::to_vec(row).unwrap());
            bytes.push(b'\n');
        }
        bytes
    }
    #[test]
    fn temporary_charge_fresh_cancel_ids_name_the_reserved_ids_they_replace() {
        let rows = case("source-cancel-fresh-ids-after-known-unsent");
        crate::thermal_history::rows(&journal(&rows), |_, _| Ok(())).unwrap();
        let enable = rows[3].tfc.clone().unwrap();
        let fresh = rows.last().unwrap().tfc.clone().unwrap();
        assert_eq!(fresh.superseded_ids, enable.cancel_ids);
        assert!(
            fresh
                .ids
                .iter()
                .all(|id| !enable.cancel_ids.unwrap().contains(id))
        );
        // The member follows `cancel_ids` and is absent from every other row,
        // so rows written before it keep their canonical bytes and intents.
        let canonical = serde_json::to_string(&fresh).unwrap();
        assert!(canonical.contains("\"cancel_ids\":null,\"superseded_ids\":[108,"));
        assert!(
            !serde_json::to_string(&enable)
                .unwrap()
                .contains("superseded_ids")
        );
        // A reader that dropped the member would bind the fresh IDs to the
        // enable's reserved slot, which they do not match: it cannot misread
        // the row as the reserved cancellation, even rehashed.
        let mut stripped = rows.clone();
        for row in &mut stripped[6..] {
            let tfc = row.tfc.as_mut().unwrap();
            tfc.superseded_ids = None;
            row.intent = tfc.hash().unwrap();
        }
        assert!(crate::thermal_history::rows(&journal(&stripped), |_, _| Ok(())).is_err());
        // Each fresh row still validates on its own as a cancel record.
        fresh.validate().unwrap();
        let mut reserved_too = fresh.clone();
        reserved_too.cancel_ids = enable.cancel_ids;
        assert!(reserved_too.validate().is_err());
    }
}

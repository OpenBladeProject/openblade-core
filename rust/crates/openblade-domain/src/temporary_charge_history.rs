//! A separate Power Mode Control contract; basic battery-v1 rows never change.
use crate::{
    battery_history::{Desired, Request},
    thermal_history::{deny, digest},
};
use serde::{Deserialize, Serialize};
use std::{io, string::String};

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
        let mut seen = [false; 256];
        for id in self.ids.iter().chain(self.cancel_ids.iter().flatten()) {
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
}

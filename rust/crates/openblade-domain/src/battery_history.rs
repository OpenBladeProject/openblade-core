//! Versioned battery evidence, independent of thermal/fan confirmation.
use crate::thermal_history::{deny, digest};
use serde::{Deserialize, Serialize};
use std::{io, string::String};
pub const CONTRACT: &str = "service-battery-policy-v1/rz09-0581-v1";
// Actual exchange: source, policy baseline, temporary observation, fresh source,
// one SET, nine independent confirmation GETs. All 14 IDs reserved first.
pub const EXCHANGES: usize = 14;
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(tag = "operation", rename_all = "camelCase", deny_unknown_fields)]
pub enum Request {
    SetBatteryPolicy { value: Policy },
    SetBatteryProtection { enabled: bool },
    SetFullChargeOverride { enabled: bool },
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Policy {
    pub charge_limit_percent: u8,
}
impl Request {
    pub fn validate(&self) -> io::Result<()> {
        if matches!(self,Self::SetBatteryPolicy{value} if !(50..=99).contains(&value.charge_limit_percent))
        {
            Err(deny())
        } else {
            Ok(())
        }
    }
}
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Desired {
    pub limit: u8,
    pub protection: bool,
    pub temporary: bool,
}
impl Desired {
    pub fn raw(self) -> Option<u8> {
        // Saved policy is generic provenance; Off has its own exact command.
        if !(50..=99).contains(&self.limit) {
            return None;
        }
        if !self.protection {
            return Some(0x50);
        }
        ((50..=80).contains(&self.limit) && self.limit.is_multiple_of(5))
            .then_some(self.limit | 0x80)
    }
    pub fn requested(self, request: &Request) -> Self {
        let mut v = self;
        match request {
            Request::SetBatteryPolicy { value } => v.limit = value.charge_limit_percent,
            Request::SetBatteryProtection { enabled } => v.protection = *enabled,
            Request::SetFullChargeOverride { enabled } => v.temporary = *enabled,
        };
        v
    }
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct BatteryRecord {
    pub contract: String,
    pub requested: Request,
    pub configured: Desired,
    pub desired: Desired,
    pub source: u8,
    pub baseline: u8,
    pub target: u8,
    pub temporary: bool,
    pub ids: [u8; EXCHANGES],
    pub fan_owner: Option<String>,
}
impl BatteryRecord {
    pub fn validate(&self) -> io::Result<()> {
        self.requested.validate()?;
        if self.contract != CONTRACT
            || self.source > 2
            || self.temporary
            || self.desired.temporary
            || !(50..=99).contains(&self.configured.limit)
            || matches!(&self.requested, Request::SetBatteryPolicy { value }
                if !(50..=80).contains(&value.charge_limit_percent) || value.charge_limit_percent % 5 != 0)
            || self.desired.raw() != Some(self.target)
            || self.configured.requested(&self.requested) != self.desired
            || matches!(self.requested, Request::SetFullChargeOverride { .. })
            || !(self.baseline == 0x50
                || self.baseline & 0x80 != 0
                    && (50..=80).contains(&(self.baseline & 0x7f))
                    && (self.baseline & 0x7f).is_multiple_of(5))
        {
            return Err(deny());
        }
        let mut seen = [false; 256];
        for id in self.ids {
            if id == 0 || id == 255 || seen[id as usize] {
                return Err(deny());
            }
            seen[id as usize] = true;
        }
        Ok(())
    }
    pub fn hash(&self) -> io::Result<String> {
        Ok(digest(&serde_json::to_vec(self)?))
    }
    pub fn valid_progress(&self, p: u8) -> bool {
        matches!(p, 0 | 6 | 60 | 61 | 62 | 63)
    }
    // 63 is an independently observed unchanged destination; it has no setter ACK.
    pub fn follows(&self, p: u8, n: u8) -> bool {
        matches!((p, n), (0, 6) | (0, 60) | (60, 61) | (61, 62))
            || p == 0 && n == 63 && self.baseline == self.target
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn canonical_battery_vectors_preserve_fan_owner_and_require_own_confirmation() {
        let v: serde_json::Value = serde_json::from_str(include_str!(
            "../../../../tools/openblade-windows-host/tests/data/battery-history-v1.json"
        ))
        .unwrap();
        let record: BatteryRecord = serde_json::from_value(v["battery"].clone()).unwrap();
        record.validate().unwrap();
        assert_eq!(serde_json::to_string(&record).unwrap(), v["canonical"]);
        assert_eq!(record.hash().unwrap(), v["intent"]);
        for key in ["standalone", "interleaved", "released"] {
            crate::thermal_history::rows(v[key].as_str().unwrap().as_bytes(), |_, _| Ok(()))
                .unwrap();
        }
        assert!(
            !crate::thermal_history::handback_complete(
                v["interleaved"].as_str().unwrap().as_bytes()
            )
            .unwrap()
        );
        assert!(
            crate::thermal_history::handback_complete(v["released"].as_str().unwrap().as_bytes())
                .unwrap()
        );
        assert!(
            crate::thermal_history::rows(
                v["legacyRejected"].as_str().unwrap().as_bytes(),
                |_, _| Ok(())
            )
            .is_err()
        );
        // Same complete, rehashed oversized-byte vector as the actual managed reader.
        assert!(
            crate::thermal_history::rows(
                v["oversizedBaselineRejected"].as_str().unwrap().as_bytes(),
                |_, _| Ok(())
            )
            .is_err()
        );
        for text in [
            v["released"]
                .as_str()
                .unwrap()
                .replace("\"caller_pid\":44", "\"caller_pid\":0"),
            v["released"]
                .as_str()
                .unwrap()
                .replace("\"progress\":62", "\"progress\":5"),
            v["released"]
                .as_str()
                .unwrap()
                .replace("\"target\":198", "\"target\":-0"),
        ] {
            assert!(crate::thermal_history::rows(text.as_bytes(), |_, _| Ok(())).is_err());
        }
    }
}

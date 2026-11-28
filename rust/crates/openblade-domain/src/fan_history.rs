//! Versioned exact0581 fan evidence. Owned mode proof is deliberately unsettled
//! until independent Automatic release; callback retirement alone cannot hand back.
use crate::{
    fan_recipe::FanIntent,
    thermal_history::{deny, digest},
};
use serde::{Deserialize, Serialize};
use serde_json::Value;
use std::{io, string::ToString, vec::Vec};
// v3 row grammar adds only progress51: independent Automatic observation tied
// to the exact original owner, without setter claims. Readers ship first.
pub const LEGACY_CONTRACT: &str = "service-fan-mutation-v2/rz09-0581-linked-v1";
pub const PREVIOUS_CONTRACT: &str = "service-fan-mutation-v2/rz09-0581-linked-v2";
pub const CONTRACT: &str = "service-battery-policy-v1/rz09-0581-fan-linked-v3";
#[derive(Clone, Debug, Serialize, Deserialize, PartialEq)]
#[serde(deny_unknown_fields)]
pub struct FanRecord {
    pub contract: std::string::String,
    pub requested: Value,
    pub intent: FanIntent,
    pub selector: u8,
    pub entry: bool,
    pub source: i32,
    pub preflight: [[u8; 2]; 2],
    pub ids: Vec<[u8; 2]>,
    pub cleanup: Vec<[u8; 2]>,
    /// A return to Automatic that writes Custom: its CPU and GPU levels, set
    /// by two 0D/07 setters after the selector-4 pair and read back by 0D/87.
    /// Absent otherwise, so earlier records keep their bytes and hash.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub custom: Option<[u8; 2]>,
}
impl FanRecord {
    pub fn setters(&self) -> usize {
        if self.entry || self.custom.is_some() {
            4
        } else {
            2
        }
    }
    pub fn validate(&self) -> io::Result<()> {
        if !matches!(
            self.contract.as_str(),
            CONTRACT | PREVIOUS_CONTRACT | LEGACY_CONTRACT
        ) || FanIntent::checked(self.intent.mode, self.intent.rpm) != Some(self.intent)
            || !self.destination_selector()
            // 3 is unclassified external power; readers before it refuse the row.
            || !matches!(self.source, 0 | 2 | crate::automation::UNCLASSIFIED_EXTERNAL)
            || self.entry && !self.intent.manual()
            || self.ids.len()
                != self.setters()
                    + 1
                    + usize::from(self.entry && self.selector == 2)
                    + usize::from(self.custom.is_some())
            || self.cleanup.len() != if self.intent.manual() { 5 } else { 0 }
        {
            return Err(deny());
        }
        let mut seen = [false; 256];
        for pair in self
            .preflight
            .iter()
            .chain(self.ids.iter())
            .chain(self.cleanup.iter())
        {
            for &id in pair {
                if id == 0 || id == 255 || seen[id as usize] {
                    return Err(deny());
                }
                seen[id as usize] = true;
            }
        }
        validate_requested(&self.requested)?;
        if let Some(mode) = self.requested["value"]["value"].as_u64() {
            let rpm = self.requested["settings"]["fixedSpeedRpm"]
                .as_u64()
                .unwrap_or(3700) as u16;
            if FanIntent::checked(mode as u8, rpm) != Some(self.intent) {
                return Err(deny());
            }
        }
        Ok(())
    }
    /// Manual fans keep the Balanced (0) or Performance (2) selector. A return
    /// to Automatic may also write Silent (5), Battery Saver (3) or Battery
    /// Balanced (6), the battery pair never on unclassified power, or Custom (4)
    /// with admitted levels (`thermal_mutation::Destination`). Readers before
    /// this grammar refuse those rows.
    fn destination_selector(&self) -> bool {
        let automatic = !self.intent.manual();
        match (self.selector, self.custom) {
            (0 | 2, None) => true,
            (5, None) => automatic,
            (3 | 6, None) => automatic && self.source != crate::automation::UNCLASSIFIED_EXTERNAL,
            (crate::thermal_mutation::CUSTOM_SELECTOR, Some([cpu, gpu])) => {
                automatic
                    && crate::thermal_mutation::Destination::custom(cpu.into(), gpu.into(), false)
                        .is_some()
            }
            _ => false,
        }
    }
    pub fn hash(&self) -> io::Result<std::string::String> {
        Ok(digest(&serde_json::to_vec(self)?))
    }
    pub fn valid_progress(&self, p: u8) -> bool {
        (p == 51
            && matches!(self.contract.as_str(), CONTRACT | PREVIOUS_CONTRACT)
            && self.intent.manual())
            || matches!(p, 0 | 6 | 50)
            || (10..10 + self.setters() as u8 * 2).contains(&p)
            || self.intent.manual() && matches!(p, 30 | 31 | 40..=43)
    }
    pub fn follows(&self, p: u8, n: u8) -> bool {
        (p == 30
            && n == 51
            && matches!(self.contract.as_str(), CONTRACT | PREVIOUS_CONTRACT)
            && self.intent.manual())
            || (p == 0 && matches!(n, 6 | 10))
            || (p >= 10 && p < 9 + self.setters() as u8 * 2 && n == p + 1)
            || (p == 9 + self.setters() as u8 * 2
                && n == if self.intent.manual() { 30 } else { 50 })
            || (self.intent.manual()
                && ((p == 30 && n == 31)
                    || (p == 31 && n == 40)
                    || (matches!(p, 40..=42) && n == p + 1)
                    || (p == 43 && n == 50)))
    }
}
/// A raw fan record: nonnegative integer contracts, and `custom` absent rather
/// than null so both readers bind the same canonical bytes.
pub fn validate_raw(v: &Value) -> io::Result<()> {
    if v.get("custom").is_some_and(Value::is_null) {
        return Err(deny());
    }
    validate_numbers(v)
}
// Fan fields are nonnegative integer contracts. Preserve lexical agreement with
// the managed reader, including rejection of negative zero and exponent forms.
pub fn validate_numbers(v: &Value) -> io::Result<()> {
    match v {
        Value::Number(n) => {
            let value = n.as_u64().ok_or_else(deny)?;
            if n.to_string() != value.to_string() {
                return Err(deny());
            }
        }
        Value::Array(a) => {
            for v in a {
                validate_numbers(v)?;
            }
        }
        Value::Object(o) => {
            for v in o.values() {
                validate_numbers(v)?;
            }
        }
        _ => {}
    }
    Ok(())
}
pub fn validate_settings(v: &Value) -> io::Result<()> {
    fn members(v: &Value, keys: &[&str]) -> io::Result<()> {
        if v.as_object()
            .is_none_or(|o| o.len() != keys.len() || o.keys().any(|k| !keys.contains(&k.as_str())))
        {
            Err(deny())
        } else {
            Ok(())
        }
    }
    fn speed(v: &Value) -> bool {
        matches!(v.as_u64(),Some(n)if(2000..=5400).contains(&n)&&n%100==0)
    }
    members(v, &["mode", "fixedSpeedRpm", "manualCurve"])?;
    if !matches!(v["mode"].as_u64(), Some(0..=3)) || !speed(&v["fixedSpeedRpm"]) {
        return Err(deny());
    }
    let c = &v["manualCurve"];
    members(c, &["cpu", "gpu", "useHigherTemperatureForBoth"])?;
    if !c["useHigherTemperatureForBoth"].is_boolean() {
        return Err(deny());
    }
    for key in ["cpu", "gpu"] {
        let pts = c[key].as_array().ok_or_else(deny)?;
        if !(2..=10).contains(&pts.len()) {
            return Err(deny());
        }
        let mut last = None;
        for p in pts {
            members(p, &["temperatureCelsius", "speedRpm"])?;
            let t = p["temperatureCelsius"].as_u64().ok_or_else(deny)?;
            let rpm = p["speedRpm"].as_u64().ok_or_else(deny)?;
            if !(20..=100).contains(&t)
                || !speed(&p["speedRpm"])
                || last.is_some_and(|(pt, pr)| t <= pt || rpm < pr)
            {
                return Err(deny());
            }
            last = Some((t, rpm));
        }
    }
    Ok(())
}
pub fn validate_requested(v: &Value) -> io::Result<()> {
    let o = v.as_object().ok_or_else(deny)?;
    if o.len() != 2 || !o.contains_key("value") || !o.contains_key("settings") {
        return Err(deny());
    }
    if v["value"].is_null() {
        if !v["settings"].is_null() {
            return Err(deny());
        }
        return Ok(());
    }
    let value = v["value"].as_object().ok_or_else(deny)?;
    if value.len() != 2
        || !matches!(v["value"]["value"].as_u64(), Some(0..=3))
        || !matches!(v["value"]["lifetime"].as_u64(), Some(0..=1))
    {
        return Err(deny());
    }
    if !v["settings"].is_null() {
        validate_settings(&v["settings"])?;
        if v["value"]["value"] != v["settings"]["mode"] {
            return Err(deny());
        }
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn retained_owner_scope_survives_retired_request_chains() {
        let vectors: Value = serde_json::from_str(include_str!(
            "../../../../tools/openblade-windows-host/tests/data/fan-caller-scope-chains.json"
        ))
        .unwrap();
        let mut mismatches = Vec::new();
        for case in vectors["cases"].as_array().unwrap() {
            let raw = case["journal"].as_str().unwrap().as_bytes();
            let valid = case["valid"].as_bool().unwrap();
            let parsed = crate::thermal_history::rows(raw, |_, _| Ok(()));
            if parsed.is_ok() != valid {
                mismatches.push(case["name"].as_str().unwrap());
            }
            if valid {
                assert!(
                    crate::thermal_history::handback_complete(raw).unwrap(),
                    "{}",
                    case["name"]
                );
            }
        }
        assert!(
            mismatches.is_empty(),
            "retained owner scope mismatches: {mismatches:?}"
        );
    }
    /// The managed WindowsNativeFanHistory reader consumes the same vector.
    #[test]
    fn performance_selector_on_limited_power_matches_the_managed_reader() {
        source_vector_matches_the_managed_reader(
            include_str!(
                "../../../../tools/openblade-windows-host/tests/data/fan-history-v3-limited-source.json"
            ),
            2,
        );
    }
    /// Unclassified external power (3); the managed reader consumes the same vector.
    #[test]
    fn performance_selector_on_unclassified_power_matches_the_managed_reader() {
        source_vector_matches_the_managed_reader(
            include_str!(
                "../../../../tools/openblade-windows-host/tests/data/fan-history-v3-unclassified-source.json"
            ),
            3,
        );
    }
    /// A return to Automatic that writes the saved destination directly; the
    /// managed reader consumes the same vector.
    #[test]
    fn destination_return_records_match_the_managed_reader() {
        let v: Value = serde_json::from_str(include_str!(
            "../../../../tools/openblade-windows-host/tests/data/fan-history-v3-destination-return.json"
        ))
        .unwrap();
        for case in v["cases"].as_array().unwrap() {
            let name = &case["name"];
            validate_raw(&case["fan"]).unwrap();
            let r: FanRecord = serde_json::from_value(case["fan"].clone()).unwrap();
            r.validate().unwrap();
            assert_eq!(
                r.setters(),
                if r.custom.is_some() { 4 } else { 2 },
                "{name}"
            );
            assert_eq!(
                serde_json::to_string(&r).unwrap(),
                case["canonical"].as_str().unwrap(),
                "{name}"
            );
            assert_eq!(r.hash().unwrap(), case["intent"], "{name}");
            let released = case["released"].as_str().unwrap().as_bytes();
            assert!(
                crate::thermal_history::handback_complete(released).unwrap(),
                "{name}"
            );
            let mut last = None;
            crate::thermal_history::rows(released, |e, _| {
                last = Some(e.progress);
                Ok(())
            })
            .unwrap();
            assert_eq!(last, Some(50), "{name}");
        }
        for case in v["rejected"].as_array().unwrap() {
            let refused = validate_raw(&case["fan"]).is_err()
                || serde_json::from_value::<FanRecord>(case["fan"].clone())
                    .map_err(io::Error::from)
                    .and_then(|r| r.validate())
                    .is_err();
            assert!(refused, "{}", case["name"]);
        }
        // An explicit null is refused by the row reader, not only the vector.
        let mut row: Value = serde_json::from_str(
            v["cases"][0]["released"]
                .as_str()
                .unwrap()
                .lines()
                .next()
                .unwrap(),
        )
        .unwrap();
        let line = |row: &Value| [serde_json::to_vec(row).unwrap(), b"\n".to_vec()].concat();
        assert!(crate::thermal_history::rows(&line(&row), |_, _| Ok(())).is_ok());
        row["fan"]["custom"] = Value::Null;
        assert!(crate::thermal_history::rows(&line(&row), |_, _| Ok(())).is_err());
    }
    fn source_vector_matches_the_managed_reader(vector: &str, source: i32) {
        let v: Value = serde_json::from_str(vector).unwrap();
        let r: FanRecord = serde_json::from_value(v["fan"].clone()).unwrap();
        assert_eq!((r.selector, r.source), (2, source));
        r.validate().unwrap();
        assert_eq!(
            serde_json::to_string(&r).unwrap(),
            v["canonical"].as_str().unwrap()
        );
        assert_eq!(r.hash().unwrap(), v["intent"]);
        let released = v["released"].as_str().unwrap().as_bytes();
        assert!(crate::thermal_history::handback_complete(released).unwrap());
        for case in v["rejected"].as_array().unwrap() {
            let bad: FanRecord = serde_json::from_value(case["fan"].clone()).unwrap();
            assert!(bad.validate().is_err(), "{}", case["name"]);
        }
    }
    /// The host producer and the managed reader consume the same vector: an
    /// owner recorded under Balanced closed by 51 while firmware reports
    /// Automatic fans under Performance. The row carries no observed selector.
    #[test]
    fn foreign_selector_automatic_close_settles_the_owner_in_both_readers() {
        let v: Value = serde_json::from_str(include_str!(
            "../../../../tools/openblade-windows-host/tests/data/fan-history-v3-foreign-selector-automatic.json"
        ))
        .unwrap();
        let owned = v["owned"].as_str().unwrap().as_bytes();
        let closed = v["closed"].as_str().unwrap().as_bytes();
        assert!(crate::thermal_history::rows(owned, |_, _| Ok(())).is_ok());
        assert!(!crate::thermal_history::handback_complete(owned).unwrap());
        assert!(crate::thermal_history::handback_complete(closed).unwrap());
        let mut last = None;
        crate::thermal_history::rows(closed, |e, _| {
            last = Some(e.clone());
            Ok(())
        })
        .unwrap();
        let last = last.unwrap();
        let r = last.fan.unwrap();
        assert_eq!(
            (last.progress, r.selector, r.contract.as_str()),
            (51, 0, CONTRACT)
        );
    }
    #[test]
    fn complete_cross_scope_histories_are_rejected_and_v3_auto_proof_is_exact() {
        let legacy: Value = serde_json::from_str(include_str!(
            "../../../../tools/openblade-windows-host/tests/data/fan-history-v2.json"
        ))
        .unwrap();
        for (key, valid) in [
            ("sameScope", true),
            ("foreignSid", false),
            ("foreignSession", false),
        ] {
            assert_eq!(
                crate::thermal_history::handback_complete(legacy[key].as_str().unwrap().as_bytes())
                    .is_ok(),
                valid,
                "{key}"
            );
        }
        let v: Value = serde_json::from_str(include_str!(
            "../../../../tools/openblade-windows-host/tests/data/fan-history-v3.json"
        ))
        .unwrap();
        let r: FanRecord = serde_json::from_value(v["fan"].clone()).unwrap();
        r.validate().unwrap();
        assert_eq!(r.hash().unwrap(), v["intent"]);
        assert!(r.valid_progress(51));
        assert!(r.follows(30, 51));
        assert!(!r.follows(31, 51));
        for key in [
            "released",
            "observedAutomatic",
            "retiredThenObservedAutomatic",
        ] {
            let raw = v[key].as_str().unwrap().as_bytes();
            assert!(crate::thermal_history::handback_complete(raw).unwrap());
            let mut rows: Vec<Value> = v[key]
                .as_str()
                .unwrap()
                .lines()
                .map(|l| serde_json::from_str(l).unwrap())
                .collect();
            rows.last_mut().unwrap()["caller_pid"] = serde_json::json!(99);
            let mut bad = Vec::new();
            for row in rows {
                bad.extend(serde_json::to_vec(&row).unwrap());
                bad.push(10);
            }
            assert!(crate::thermal_history::rows(&bad, |_, _| Ok(())).is_err());
        }
    }
    #[test]
    fn shared_fan_vectors_preserve_mode_target_and_handback_contract() {
        let v: Value = serde_json::from_str(include_str!(
            "../../../../tools/openblade-windows-host/tests/data/fan-history-v2.json"
        ))
        .unwrap();
        let r: FanRecord = serde_json::from_value(v["fan"].clone()).unwrap();
        r.validate().unwrap();
        assert_eq!(
            serde_json::to_string(&r).unwrap(),
            v["canonical"].as_str().unwrap()
        );
        assert_eq!(r.hash().unwrap(), v["intent"]);
        let owned = v["owned"].as_str().unwrap().as_bytes();
        let released = v["released"].as_str().unwrap().as_bytes();
        assert!(crate::thermal_history::rows(owned, |_, _| Ok(())).is_ok());
        assert!(!crate::thermal_history::handback_complete(owned).unwrap());
        assert!(crate::thermal_history::handback_complete(released).unwrap());
        for mut bad in [r.clone(), r.clone(), r.clone()] {
            bad.ids[0][0] = bad.cleanup[0][0];
            assert!(bad.validate().is_err());
        }
        let mut bad = r.clone();
        bad.contract.push('x');
        assert!(bad.validate().is_err());
        for p in [1, 5, 7, 18, 32, 44, 51] {
            assert!(!r.valid_progress(p));
        }
        assert!(!r.follows(30, 50));
        assert!(!r.follows(11, 14));
        assert!(r.follows(43, 50));
        let mut b = Vec::from(owned);
        let mut next: Value =
            serde_json::from_str(v["owned"].as_str().unwrap().lines().next().unwrap()).unwrap();
        next["request"] = serde_json::json!("aaaaaaaa-bbbb-cccc-dddd-000000000002");
        for p in [0, 6] {
            next["progress"] = serde_json::json!(p);
            let row: crate::thermal_history::Entry = serde_json::from_value(next.clone()).unwrap();
            b.extend(serde_json::to_vec(&row).unwrap());
            b.push(10);
        }
        assert!(!crate::thermal_history::handback_complete(&b).unwrap());
        let prior = b.clone();
        let owner = next["owner"].as_str().unwrap().to_string();
        let mut prepared = Vec::from(owned);
        next["progress"] = serde_json::json!(0);
        let row: crate::thermal_history::Entry = serde_json::from_value(next.clone()).unwrap();
        prepared.extend(serde_json::to_vec(&row).unwrap());
        prepared.push(10);
        crate::thermal_history::validate_prepared_retirement(&prepared, &prior, &owner).unwrap();
        for line in v["released"].as_str().unwrap().lines().skip(10) {
            b.extend(line.as_bytes());
            b.push(10);
        }
        assert!(crate::thermal_history::handback_complete(&b).unwrap());
        let mut unsafe_resume = prepared.clone();
        for line in v["released"].as_str().unwrap().lines().skip(10) {
            unsafe_resume.extend(line.as_bytes());
            unsafe_resume.push(10);
        }
        assert!(crate::thermal_history::rows(&unsafe_resume, |_, _| Ok(())).is_err());
    }
}

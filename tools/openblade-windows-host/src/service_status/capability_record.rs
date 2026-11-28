//! The managed `DeviceCapabilities` record native publishes: one function of
//! the compiled feature groups (`Composition`) and whether an exact RZ09-0581
//! state is confirmed (`record`). Every publication of one confirmed state,
//! read, retained, unavailable or rebased after a mutation, takes its record
//! from `Snapshot::confirm`, so they agree.
//!
//! The data (`capability_record.json`) is managed's own record, member by
//! member. Unidentified is `DeviceCapabilities.Unavailable()`, managed's
//! record before it reads the device. Identified adds the exact-identity
//! members managed's production factory publishes whatever backend it
//! composes (HyperBoost read only and pad-less observed only, as its
//! read-only fallback does; THX; observed local dimming). Each group then
//! replaces the members its backend makes managed publish, restricted to what
//! native admits; a group whose control native cannot complete yet (GPU
//! clock tuning) keeps managed's absent-backend members. The managed
//! generator (`ServiceControlRuntimeTests.NativeCapabilityRecordVectors`)
//! records each composition through the v91 codec, and the tests replay it.
//!
//! A confirmed state names no model. Every producer of one is a Reader of the
//! exact RZ09-0581 (`OBSERVED_CAPABILITIES_MODEL`): its performance mailbox
//! read and its battery fixture.
use super::FeatureRecord;
use serde_json::{Map, Value};
use std::sync::OnceLock;

/// The feature groups that change the record. Each is the compiled feature
/// that serves its requests; the implications in Cargo.toml make some
/// combinations impossible, which the record does not rely on.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub(crate) struct Composition {
    pub(crate) thermal: bool,
    pub(crate) fans: bool,
    pub(crate) battery: bool,
    pub(crate) temporary_charge: bool,
    pub(crate) profiles: bool,
    pub(crate) lighting: bool,
    pub(crate) hyper_boost: bool,
    pub(crate) gpu_clock_tuning: bool,
    pub(crate) fan_stop: bool,
}

impl Composition {
    /// This build's groups.
    pub(crate) const COMPILED: Self = Self {
        thermal: cfg!(feature = "service-thermal-mutation-v1"),
        fans: cfg!(feature = "service-fan-mutation-v2"),
        battery: cfg!(feature = "service-battery-policy-v1"),
        // Without it `setFullChargeOverride` refuses (`battery_mutation`),
        // as managed's read-only fallback publishes no temporary full charge.
        temporary_charge: cfg!(feature = "service-temporary-charge-v1"),
        profiles: cfg!(feature = "service-profiles-v1"),
        lighting: cfg!(feature = "service-lighting-v1"),
        // HyperBoost writes need the probes the native composition sets only
        // beside the lifecycle triggers (`thermal_mutation`, which asserts
        // this at compile time); without them every HyperBoost request is
        // refused, and the record keeps managed's non-reversible HyperBoost.
        hyper_boost: cfg!(all(
            feature = "service-hyperboost-v1",
            feature = "service-profile-triggers-v1"
        )),
        // Managed's GPU overclock needs what native does not publish yet: the
        // tray's GPU offset editor reads its ranges from
        // `confirmedState.gpuClockOffsets`, which no native status carries,
        // and a GPU-only change while HyperBoost is active is sent as
        // `setGpuClockTuning`, which no composition serves. Until both are,
        // publish managed's absent-backend `gpuOverclock` (null). Each blocker
        // has a tripwire: `serialize_confirmed_state` and `capability_tests`.
        gpu_clock_tuning: false,
        fan_stop: cfg!(feature = "service-fan-stop-v1"),
    };

    /// Each data fragment in merge order, and whether this composition
    /// takes it. Keyboard brightness and refresh rates are served only by
    /// `stepKeyboardBrightness` and `setLightingRefreshRate`, which need
    /// profiles beside lighting (`StatusRequest::serves`).
    fn fragments(self) -> [(&'static str, bool); 9] {
        [
            ("thermal", self.thermal),
            ("fans", self.fans),
            ("battery", self.battery),
            ("temporaryCharge", self.temporary_charge),
            ("lighting", self.lighting),
            ("lightingWithProfiles", self.lighting && self.profiles),
            ("hyperBoost", self.hyper_boost),
            ("gpuClockTuning", self.gpu_clock_tuning),
            ("fanStop", self.fan_stop),
        ]
    }
}

fn table() -> &'static Map<String, Value> {
    static TABLE: OnceLock<Map<String, Value>> = OnceLock::new();
    TABLE.get_or_init(|| {
        serde_json::from_str(include_str!("capability_record.json"))
            .expect("the reviewed capability record")
    })
}

fn fragment(name: &str) -> &'static Map<String, Value> {
    table()[name]
        .as_object()
        .expect("each reviewed capability fragment is an object")
}

/// The record `composition` publishes: managed's unidentified record, or
/// with a confirmed exact state the identified members and each group's.
pub(crate) fn record(composition: Composition, identified: bool) -> FeatureRecord {
    let mut record = FeatureRecord::from(fragment("unidentified").clone());
    if identified {
        let groups = composition.fragments().into_iter().filter(|(_, on)| *on);
        for (name, _) in std::iter::once(("identified", true)).chain(groups) {
            for (member, value) in fragment(name) {
                record.insert(member.clone(), value.clone());
            }
        }
    }
    record
}

/// Serializes a published confirmed state. Managed's GPU overclock record
/// needs the confirmed offsets the tray's editor reads, so a confirmed state
/// that carries non-null `gpuClockOffsets` while this build keeps `gpuOverclock`
/// absent fails in development: whichever projection starts publishing the
/// offsets, the record is reviewed with it.
pub(crate) fn serialize_confirmed_state<S: serde::Serializer>(
    state: &Option<FeatureRecord>,
    serializer: S,
) -> Result<S::Ok, S::Error> {
    debug_assert!(
        Composition::COMPILED.gpu_clock_tuning
            || state
                .as_ref()
                .and_then(|state| state.get("gpuClockOffsets"))
                .is_none_or(Value::is_null),
        "confirmedState.gpuClockOffsets is published while the capability record keeps gpuOverclock absent"
    );
    serde::Serialize::serialize(state, serializer)
}

/// This build's record (`Composition::COMPILED`): built once for each
/// identity and cloned into every publication.
pub(crate) fn compiled(identified: bool) -> FeatureRecord {
    static RECORDS: OnceLock<[FeatureRecord; 2]> = OnceLock::new();
    RECORDS
        .get_or_init(|| [false, true].map(|identified| record(Composition::COMPILED, identified)))
        [usize::from(identified)]
    .clone()
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    fn vectors() -> Value {
        serde_json::from_str(include_str!(
            "../../tests/data/capability-record-vectors.json"
        ))
        .unwrap()
    }

    fn composition(groups: &[Value]) -> Composition {
        let mut composition = Composition::default();
        for group in groups {
            let flag = match group.as_str().unwrap() {
                "thermal" => &mut composition.thermal,
                "fans" => &mut composition.fans,
                "battery" => &mut composition.battery,
                "temporaryCharge" => &mut composition.temporary_charge,
                "profiles" => &mut composition.profiles,
                "lighting" => &mut composition.lighting,
                "hyperBoost" => &mut composition.hyper_boost,
                "gpuClockTuning" => &mut composition.gpu_clock_tuning,
                "fanStop" => &mut composition.fan_stop,
                other => panic!("unknown group {other}"),
            };
            *flag = true;
        }
        composition
    }

    /// Every managed row is what the builder publishes for its composition.
    #[test]
    fn each_composition_publishes_the_managed_record() {
        let vectors = vectors();
        let rows = vectors["rows"].as_array().unwrap();
        assert!(rows.len() >= 13);
        for row in rows {
            let composition = composition(row["groups"].as_array().unwrap());
            let identified = row["identified"].as_bool().unwrap();
            assert_eq!(
                Value::from(record(composition, identified)),
                row["record"],
                "{}",
                row["name"]
            );
        }
    }

    /// Every fragment is taken by some managed row, and only members the
    /// managed record of the full composition carries are replaced. The
    /// unidentified record omits null members the codec leaves out, such as
    /// localDimming.
    #[test]
    fn each_fragment_replaces_members_of_the_managed_record() {
        let vectors = vectors();
        let full = vectors["rows"]
            .as_array()
            .unwrap()
            .iter()
            .find(|row| row["name"] == "every group")
            .unwrap()["record"]
            .as_object()
            .unwrap()
            .clone();
        for (name, value) in table() {
            for member in value.as_object().unwrap().keys() {
                assert!(full.contains_key(member), "{name} {member}");
            }
        }
        let names: Vec<&str> = table().keys().map(String::as_str).collect();
        let mut expected: Vec<&str> = Composition::default()
            .fragments()
            .iter()
            .map(|(name, _)| *name)
            .chain(["unidentified", "identified"])
            .collect();
        expected.sort_unstable();
        assert_eq!(names, expected);
    }

    /// An unidentified record admits nothing: the empty managed record,
    /// whatever this build compiles.
    #[test]
    fn without_identity_the_record_is_managed_unavailable() {
        let unidentified = record(Composition::COMPILED, false);
        assert_eq!(Value::from(unidentified), vectors()["rows"][0]["record"]);
        assert_eq!(vectors()["rows"][0]["identified"], false);
    }

    /// The temporary full charge is the exact model's: its production fixture
    /// captured, decoded, applied, read back and restored both commands. An
    /// identified record admits it exactly with the group that serves it.
    #[test]
    fn the_temporary_full_charge_follows_its_group() {
        let evidence: Vec<Value> = serde_json::from_str(include_str!(
            "../../../../src/OpenBlade.Protocol/Fixtures/Rz09_0581_02e0_3_01_BatteryProduction.json"
        ))
        .unwrap();
        let captured = |action: &str| {
            evidence.iter().any(|entry| {
                entry["action"] == action
                    && entry["device"]["productId"] == 0x02e0
                    && ["captured", "decoded", "applied", "readBack", "restored"]
                        .iter()
                        .all(|key| entry[*key] == true)
            })
        };
        assert!(captured("charge-to-100-enable") && captured("charge-to-100-cancel"));
        for row in vectors()["rows"].as_array().unwrap() {
            let served = row["groups"]
                .as_array()
                .unwrap()
                .contains(&json!("temporaryCharge"));
            assert_eq!(
                row["record"]["supportsTemporaryFullCharge"],
                json!(served),
                "{}",
                row["name"]
            );
        }
    }

    /// The groups this build compiles.
    #[test]
    fn the_compiled_groups_follow_the_features() {
        let c = Composition::COMPILED;
        // Cargo.toml implications.
        assert!(!c.fans || c.thermal);
        assert!(!c.battery || c.fans);
        assert!(!c.temporary_charge || c.battery);
        assert!(!c.profiles || c.temporary_charge);
        assert!(!c.lighting || c.thermal);
        // HyperBoost's probes need the lifecycle triggers, which need profiles.
        assert!(!c.hyper_boost || c.profiles);
        assert!(!c.gpu_clock_tuning || c.profiles);
        assert!(!c.fan_stop || c.profiles);
        // The cached publication is the record of this composition.
        for identity in [false, true] {
            assert_eq!(
                Value::from(compiled(identity)),
                Value::from(record(c, identity))
            );
        }
        let identified = Value::from(record(c, true));
        assert_eq!(
            identified["hyperBoostAccess"],
            json!(if c.hyper_boost { 2 } else { 1 })
        );
        assert_eq!(
            identified["chargeLimits"].as_array().unwrap().is_empty(),
            !c.battery
        );
        assert_eq!(
            identified["supportsKeyboardBrightness"],
            json!(c.lighting && c.profiles)
        );
        assert_eq!(
            identified["supportsTemporaryFullCharge"],
            json!(c.temporary_charge)
        );
        // Manual fans are ranged only where native's recipes plan them.
        let ranged: Vec<&String> = identified["fanSpeedRangesByPerformancePreset"]
            .as_object()
            .unwrap()
            .keys()
            .collect();
        let expected: &[&str] = if c.fans {
            &["Balanced", "Performance"]
        } else {
            &[]
        };
        assert_eq!(ranged, expected);
        assert_eq!(identified["hasFanStopTrialEvidence"], json!(c.fan_stop));
        // `capability_tests` ties the GPU group to what native projects.
        assert_eq!(identified["gpuOverclock"].is_null(), !c.gpu_clock_tuning);
    }
}

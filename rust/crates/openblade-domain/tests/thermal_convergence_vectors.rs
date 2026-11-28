//! The managed vectors for the shared thermal convergence ledger.
//!
//! `NativeThermalConvergenceLedgerVectorsTests` records every expectation
//! through managed `ThermalConvergenceLedgerTransitions` and
//! `ThermalConvergenceStateStore`, and verifies the same file. Here each
//! revision row derives the three revisions and the normalized intent, each
//! document row decodes the recorded bytes, each transition row replays its
//! steps, and each native row is a document this encoder wrote that managed
//! loaded. Set `OPENBLADE_PRINT_NATIVE_LEDGER_ROWS` to print the native rows
//! this encoder writes for the transition states it names.
#![cfg(feature = "thermal-convergence")]
use openblade_domain::thermal_convergence::{
    Candidate, Component, Contradiction, Disposition, Guid, Intent, Load, Observation, Power,
    Quarantine, Refresh, Revisions, State, decode, encode,
};
use serde_json::{Value, json};

const VECTORS: &str = include_str!(
    "../../../../tools/openblade-windows-host/tests/data/thermal-convergence-ledger-vectors.json"
);
const SID: &str = "S-1-5-21-1-2-3-1001";
/// The transition states the native rows were written from.
const NATIVE: [(&str, usize); 4] = [
    ("reserve-and-complete", 1),
    ("reserve-and-complete", 2),
    ("both-components", 1),
    ("fans-then-performance-budgets-sort", 4),
];

fn vectors() -> Value {
    serde_json::from_str(VECTORS).expect("vectors")
}

fn rows<'a>(root: &'a Value, section: &str) -> &'a Vec<Value> {
    root[section].as_array().expect(section)
}

fn int(value: &Value) -> i32 {
    i32::try_from(value.as_i64().expect("integer")).expect("i32")
}

fn components(value: &Value) -> Vec<Component> {
    value
        .as_array()
        .expect("components")
        .iter()
        .map(|component| Component(int(component)))
        .collect()
}

fn read<T: serde::de::DeserializeOwned>(value: &Value) -> T {
    serde_json::from_value(value.clone()).expect("member")
}

#[test]
fn revisions_and_normalized_intent_match_managed() {
    let root = vectors();
    for row in rows(&root, "revisions") {
        let intent: Intent = read(&row["intent"]);
        let normalized = intent.normalized();
        let revisions = Revisions::of(&normalized, int(&row["policy"]), int(&row["disclosure"]));
        assert_eq!(
            json!({
                "performance": revisions.performance,
                "fans": revisions.fans,
                "policy": revisions.policy,
            }),
            row["revisions"],
            "{}",
            row["name"]
        );
        assert_eq!(
            serde_json::to_value(&normalized).unwrap(),
            row["effectiveIntent"],
            "{}",
            row["name"]
        );
    }
}

fn expected_load(load: &Value) -> Result<(Value, Option<String>), i32> {
    if load["admitted"].as_bool().expect("admitted") {
        Ok((
            load["state"].clone(),
            load["sha256"].as_str().map(str::to_owned),
        ))
    } else {
        Err(int(&load["reason"]))
    }
}

fn actual_load(load: Load) -> Result<(Value, Option<String>), i32> {
    match load {
        Load::Admitted { state, sha256 } => Ok((serde_json::to_value(*state).unwrap(), sha256)),
        Load::Quarantined(Quarantine::FutureSchema) => Err(1),
        Load::Quarantined(Quarantine::InvalidDocument) => Err(2),
    }
}

#[test]
fn documents_load_as_managed_loads_them() {
    let root = vectors();
    for row in rows(&root, "documents") {
        let text = row["text"].as_str().expect("text");
        assert_eq!(
            actual_load(decode(Some(text.as_bytes()), SID)),
            expected_load(&row["load"]),
            "{}",
            row["name"]
        );
    }
}

/// Replays a transition row's steps, returning every state.
fn replay(steps: &Value) -> Vec<State> {
    let mut state = State::empty(SID);
    let mut states = vec![];
    for step in steps.as_array().expect("steps") {
        state = match step["op"].as_str().expect("op") {
            "refresh" => {
                let epoch = Guid::parse(step["epoch"].as_str().unwrap()).unwrap();
                let fingerprint = step["fingerprint"].as_str().unwrap();
                state.refresh_intent(
                    Refresh {
                        desired: read(&step["intent"]),
                        policy: int(&step["policy"]),
                        disclosure: int(&step["disclosure"]),
                        destination: step["destination"]
                            .as_i64()
                            .map(|value| Power(value as i32)),
                        fingerprint,
                        device_invalidated: step["invalidated"].as_bool().unwrap(),
                        policy_changed: step["policyChanged"].as_bool().unwrap(),
                    },
                    || epoch,
                )
            }
            "reserve" => {
                let candidate = Candidate {
                    destination: Power(int(&step["destination"])),
                    contradiction: Contradiction(int(&step["contradiction"])),
                    performance_revision: state.performance_desired_revision.clone().unwrap(),
                    fan_revision: state.fan_desired_revision.clone().unwrap(),
                };
                let fresh: Observation = read(&step["fresh"]);
                let desired = state.effective_intent.clone().unwrap();
                state.reserve(
                    &candidate,
                    Guid::parse(step["attempt"].as_str().unwrap()).unwrap(),
                    fresh,
                    desired,
                    &components(&step["affected"]),
                )
            }
            "complete" => state.complete(
                Power(int(&step["destination"])),
                Contradiction(int(&step["contradiction"])),
                &components(&step["affected"]),
                Disposition(int(&step["disposition"])),
            ),
            op => panic!("unknown op {op}"),
        };
        states.push(state.clone());
    }
    states
}

#[test]
fn transitions_replay_managed_states_and_validation() {
    let root = vectors();
    for row in rows(&root, "transitions") {
        let name = row["name"].as_str().unwrap();
        let recorded = row["states"].as_array().expect("states");
        let states = replay(&row["steps"]);
        assert_eq!(states.len(), recorded.len(), "{name}");
        for (index, (state, expected)) in states.iter().zip(recorded).enumerate() {
            assert_eq!(
                serde_json::to_value(state).unwrap(),
                expected["state"],
                "{name} {index}"
            );
            assert_eq!(
                state.validate(SID, true).is_ok(),
                expected["persistable"].as_bool().unwrap(),
                "{name} {index}"
            );
            assert_eq!(
                state.is_expected_reservation(state),
                expected["selfExpected"].as_bool().unwrap(),
                "{name} {index}"
            );
        }
    }
}

#[test]
fn managed_loads_what_this_encoder_writes() {
    let root = vectors();
    let transitions = rows(&root, "transitions");
    let encoded: Vec<(String, String)> = NATIVE
        .iter()
        .map(|&(name, index)| {
            let row = transitions
                .iter()
                .find(|row| row["name"] == name)
                .expect(name);
            let state = &replay(&row["steps"])[index];
            let (bytes, _) = encode(state, SID).expect("persistable");
            (format!("{name}-{index}"), String::from_utf8(bytes).unwrap())
        })
        .collect();
    if std::env::var_os("OPENBLADE_PRINT_NATIVE_LEDGER_ROWS").is_some() {
        for (name, text) in &encoded {
            println!("{}", json!({ "name": name, "text": text }));
        }
    }
    let native = rows(&root, "native");
    assert_eq!(native.len(), encoded.len());
    for (row, (name, text)) in native.iter().zip(&encoded) {
        assert_eq!(row["name"], **name);
        assert_eq!(row["text"].as_str().unwrap(), text, "{name}");
        let decoded = actual_load(decode(Some(text.as_bytes()), SID));
        assert_eq!(decoded, expected_load(&row["load"]), "{name}");
    }
}

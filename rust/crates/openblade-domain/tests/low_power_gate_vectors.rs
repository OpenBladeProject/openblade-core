//! The managed vectors for the native low-power gate.
//!
//! `LowPowerGateNativeVectorsTests` replays each case through managed
//! `LowPowerGate` over a QPC clock of the case's frequency and verifies the
//! same file. Here each case drives `LowPowerGate::apply`, the retention
//! calls and `is_active_for_generation`; every step must match managed
//! exactly, including the boundary timestamp and whether the generation
//! changed.
use core::num::NonZeroU64;
use openblade_domain::low_power_gate::{LowPowerGate, Signal};
use serde_json::{Map, Value, json};

const VECTORS: &str =
    include_str!("../../../../tools/openblade-windows-host/tests/data/low-power-gate-vectors.json");

const SIGNALS: [(&str, Signal); 3] = [
    ("suspend", Signal::Suspend),
    ("resume", Signal::Resume),
    ("displayOn", Signal::DisplayOn),
];

fn shape<'a>(
    value: &'a Value,
    allowed: &[&str],
    required: &[&str],
) -> Result<&'a Map<String, Value>, String> {
    let object = value.as_object().ok_or("expected an object")?;
    if object.keys().any(|name| !allowed.contains(&name.as_str()))
        || required.iter().any(|name| !object.contains_key(*name))
    {
        return Err(format!("unexpected vector row: {value}"));
    }
    Ok(object)
}

fn rows(value: Option<&Value>) -> Result<&Vec<Value>, String> {
    value
        .and_then(Value::as_array)
        .ok_or_else(|| "expected an array".to_owned())
}

fn integer(value: Option<&Value>) -> Result<i64, String> {
    value
        .and_then(Value::as_i64)
        .ok_or_else(|| format!("expected an integer: {value:?}"))
}

fn generation(value: Option<&Value>) -> Result<u64, String> {
    u64::try_from(integer(value)?).map_err(|_| "a generation is not negative".to_owned())
}

enum Step {
    Signal(Signal, i64),
    Retain(u64),
    Release(u64),
    ActiveFor(u64),
}

/// One case's frequency and steps, checked as the managed reader checks them.
fn case(value: &Value) -> Result<(NonZeroU64, Vec<Step>), String> {
    let vector = shape(
        value,
        &["name", "frequency", "steps", "expect"],
        &["name", "frequency", "steps"],
    )?;
    vector
        .get("name")
        .and_then(Value::as_str)
        .ok_or("a case has a name")?;
    let frequency = u64::try_from(integer(vector.get("frequency"))?)
        .ok()
        .and_then(NonZeroU64::new)
        .ok_or("a case has a positive frequency")?;
    let mut steps = Vec::new();
    for row in rows(vector.get("steps"))? {
        let step = shape(
            row,
            &["signal", "at", "retain", "release", "activeFor"],
            &[],
        )?;
        let mut keys: Vec<&str> = step.keys().map(String::as_str).collect();
        keys.sort_unstable();
        steps.push(match keys.as_slice() {
            ["at", "signal"] => {
                let name = step.get("signal").and_then(Value::as_str);
                let signal = SIGNALS
                    .iter()
                    .find(|(text, _)| Some(*text) == name)
                    .ok_or("a signal is suspend, resume or displayOn")?
                    .1;
                Step::Signal(signal, integer(step.get("at"))?)
            }
            ["retain"] => Step::Retain(generation(step.get("retain"))?),
            ["release"] => Step::Release(generation(step.get("release"))?),
            ["activeFor"] => Step::ActiveFor(generation(step.get("activeFor"))?),
            _ => return Err("a step is one signal, retain, release or activeFor".to_owned()),
        });
    }
    if let Some(expect) = vector.get("expect") {
        rows(Some(expect))?;
    }
    Ok((frequency, steps))
}

fn replay(frequency: NonZeroU64, steps: &[Step]) -> Value {
    let mut gate = LowPowerGate::new(frequency);
    let observed: Vec<Value> = steps
        .iter()
        .map(|step| {
            let previous = gate.transition();
            let (result, changed) = match *step {
                Step::Signal(signal, at) => match gate.apply(signal, at) {
                    Some(accepted) => {
                        assert_eq!(accepted.transition, gate.transition());
                        (Some(true), accepted.changed)
                    }
                    None => {
                        assert_eq!(
                            previous,
                            gate.transition(),
                            "a stale signal changes nothing"
                        );
                        (Some(false), false)
                    }
                },
                Step::Retain(generation) => {
                    gate.retain_resume_generation(generation);
                    (None, false)
                }
                Step::Release(generation) => {
                    gate.release_resume_generation(generation);
                    (None, false)
                }
                Step::ActiveFor(generation) => {
                    (Some(gate.is_active_for_generation(generation)), false)
                }
            };
            let current = gate.transition();
            assert_eq!(changed, current.generation != previous.generation);
            assert_eq!(current.suspending(), gate.is_active());
            json!({
                "result": result,
                "changed": changed,
                "generation": current.generation,
                "boundary": current.timestamp,
            })
        })
        .collect();
    Value::Array(observed)
}

#[test]
fn native_gate_matches_every_managed_case() {
    let root: Value = serde_json::from_str(VECTORS).unwrap();
    let sections = ["comment", "cases", "malformed"];
    let root = shape(&root, &sections, &sections).unwrap();
    assert!(root["comment"].is_string());
    let cases = rows(root.get("cases")).unwrap();
    assert!(!cases.is_empty());
    let mut failures = Vec::new();
    for vector in cases {
        let (frequency, steps) = case(vector).unwrap();
        let expect = vector.get("expect").expect("a recorded case");
        let observed = replay(frequency, &steps);
        if &observed != expect {
            failures.push(format!("{}: {observed}", vector["name"]));
        }
    }
    assert!(failures.is_empty(), "{failures:#?}");
}

#[test]
fn native_reader_refuses_every_managed_malformed_case() {
    let root: Value = serde_json::from_str(VECTORS).unwrap();
    let malformed = rows(root.get("malformed")).unwrap();
    assert!(!malformed.is_empty());
    for row in malformed {
        assert!(case(row).is_err(), "{row}");
    }
}

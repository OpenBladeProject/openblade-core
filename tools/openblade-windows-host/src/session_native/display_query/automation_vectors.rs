//! Managed parity: every case of `display-automation-vectors.json`, recorded
//! from the real `DisplayAutomationCoordinator` by
//! `DisplayAutomationCoordinatorTests.NativeAutomationVectors`, replays
//! through the native coordinator and transition runner in the watcher's
//! order (power admission, then automation). A case's profile writes run
//! after each step, one transition at a time as `serve` runs them, with the
//! case's scripted results and the steps it fires during a write.
//!
//! Native differs from managed in exactly the cases `DIFFERENCES` names,
//! each with the members it records instead and why.
use super::{GAME, Panel, Rig, settings};
use crate::session_native::{
    display_query::{
        automation::Applier,
        manual_write::ManualWriteOutcome,
        observation::Snapshot,
        profile_apply::{Applied, ApplyProfile, Settings},
        recovery::{Fields, Status, Target},
    },
    power_admission::PowerContext,
};
use serde_json::{Map, Value, json};
use std::{borrow::Cow, collections::VecDeque, io, sync::atomic::AtomicBool};

const VECTORS: &str = include_str!("../../../tests/data/display-automation-vectors.json");

/// A case name, why native differs, and the members native records instead.
type Difference = (&'static str, &'static str, fn() -> Value);

/// Cases where native records other members than managed, and why.
const DIFFERENCES: &[Difference] = &[(
    "mid-apply/lock-resumes-after-unlock",
    "Managed resumes the latest target as soon as the canceled write \
     completes, and its resumption itself re-admits the confirmed source while \
     the status is still unconfirmed. Native resumes only under the watcher's \
     confirmed power admission: the resumption waits, and the unlock status \
     that confirms the same source again schedules it.",
    || json!({"results": ["BaselineEstablished", "TransitionScheduled", "TransitionScheduled"]}),
)];

#[test]
fn every_managed_vector_replays_through_the_native_coordinator() {
    let root: Value = serde_json::from_str(VECTORS).unwrap();
    let root = members(&root, &["comment", "cases"]);
    assert!(root["comment"].is_string());
    let cases = root["cases"].as_array().unwrap();
    assert!(cases.len() >= 30, "the reviewed cases are present");
    let mut differences = DIFFERENCES
        .iter()
        .map(|(name, ..)| *name)
        .collect::<Vec<_>>();
    let mut failures = Vec::new();
    for case in cases {
        let case = members(case, &["name", "applies", "steps", "expect"]);
        let name = case["name"].as_str().unwrap();
        let mut expected = case["expect"].clone();
        if let Some((_, reason, native)) = DIFFERENCES.iter().find(|(case, ..)| *case == name) {
            assert!(!reason.is_empty());
            differences.retain(|case| *case != name);
            for (member, value) in native().as_object().unwrap() {
                assert_ne!(&expected[member], value, "{name} {member} still differs");
                expected[member] = value.clone();
            }
        }
        let observed = replay(case);
        if observed != expected {
            failures.push(format!(
                "{name}:\n  managed {expected}\n  native  {observed}"
            ));
        }
    }
    assert!(differences.is_empty(), "no case named {differences:?}");
    assert!(failures.is_empty(), "{}", failures.join("\n"));
}

/// The object's members, each of which must be one of `allowed`.
fn members<'a>(value: &'a Value, allowed: &[&str]) -> &'a Map<String, Value> {
    let object = value.as_object().unwrap();
    for member in object.keys() {
        assert!(
            allowed.contains(&member.as_str()),
            "unknown member {member}"
        );
    }
    object
}

fn replay(case: &Map<String, Value>) -> Value {
    let rig = Rig::new();
    let mut replay = Replay {
        rig: &rig,
        panel: Panel::default(),
        applies: case
            .get("applies")
            .and_then(Value::as_array)
            .cloned()
            .unwrap_or_default()
            .into(),
        applied: Vec::new(),
        queries: 0,
    };
    let mut results = Vec::new();
    let mut checkpoints = Vec::new();
    let mut delays = 0;
    for step in case["steps"].as_array().unwrap() {
        results.push(replay.step(step));
        while let Some((result, waited)) = rig.run(&mut replay) {
            result.unwrap();
            delays += waited.len();
        }
        checkpoints.push(checkpoint(&rig));
    }
    let last = rig
        .recovery()
        .last_result()
        .map(|last| json!({"source": last.source, "status": format!("{:?}", last.status)}));
    json!({
        "results": results,
        "checkpoints": checkpoints,
        "applied": replay.applied,
        "queries": replay.queries,
        "delays": delays,
        "last": last,
    })
}

/// The durable members of the latest checkpoint.
fn checkpoint(rig: &Rig) -> Value {
    if rig.file.bytes.lock().unwrap().is_none() {
        return Value::Null;
    }
    let mut record = rig.file.record();
    record.as_object_mut().unwrap().remove("SchemaVersion");
    record
}

struct Replay<'a> {
    rig: &'a Rig,
    panel: Panel,
    applies: VecDeque<Value>,
    applied: Vec<Value>,
    queries: usize,
}

impl Replay<'_> {
    /// One trigger as the watcher or a manual request delivers it.
    fn step(&mut self, step: &Value) -> Value {
        let step = members(
            step,
            &[
                "observe",
                "unavailable",
                "begin",
                "complete",
                "read",
                "panel",
            ],
        );
        assert_eq!(step.len(), 1, "one trigger per step");
        let (kind, value) = step.iter().next().unwrap();
        match kind.as_str() {
            "observe" => name(self.rig.observe(target(value))),
            "unavailable" => name(self.rig.unavailable(value.as_bool().unwrap())),
            "begin" => {
                // The manual writer's admitted source, as `Writer` passes it.
                let source = self.rig.power.capture().map(|power| power.source);
                json!(self.rig.recovery().begin(profile(value).fields(), source))
            }
            "complete" => {
                let status = status(value);
                self.rig.recovery().complete(status, &self.panel.snapshot());
                Value::Null
            }
            "read" => json!(self.rig.recovery().explicit_read(&self.panel.snapshot())),
            "panel" => {
                for (member, value) in members(
                    value,
                    &[
                        "available",
                        "refresh",
                        "hdrSupported",
                        "hdr",
                        "color",
                        "brightness",
                    ],
                ) {
                    let panel = &mut self.panel;
                    match member.as_str() {
                        "available" => panel.available = value.as_bool().unwrap(),
                        "refresh" => panel.refresh = value.as_str().map(str::to_owned),
                        "hdrSupported" => panel.hdr_supported = value.as_bool().unwrap(),
                        "hdr" => panel.hdr = value.as_bool().unwrap(),
                        "color" => panel.color = value.as_str().map(str::to_owned),
                        _ => panel.brightness = byte(value),
                    }
                }
                Value::Null
            }
            _ => unreachable!(),
        }
    }
}

impl Applier for Replay<'_> {
    fn query(&mut self, _: &AtomicBool) -> io::Result<Snapshot> {
        self.queries += 1;
        Ok(self.panel.snapshot())
    }

    fn apply(
        &mut self,
        request: &ApplyProfile,
        admitted: PowerContext,
        _: &AtomicBool,
    ) -> io::Result<Applied> {
        self.applied.push(json!({
            "source": admitted.source,
            "settings": profile_json(&request.settings),
        }));
        let script = self
            .applies
            .pop_front()
            .unwrap_or_else(|| json!({"status": "Succeeded"}));
        let script = members(&script, &["status", "during"]);
        for step in script
            .get("during")
            .and_then(Value::as_array)
            .into_iter()
            .flatten()
        {
            self.step(step);
        }
        let status = status(&script["status"]);
        Ok(Applied {
            outcome: ManualWriteOutcome {
                status,
                error: (status != Status::Succeeded)
                    .then_some(Cow::Borrowed("The scripted profile did not apply.")),
                state: self.panel.snapshot(),
            },
            // The native unresolved-field gate is covered by its own tests;
            // managed has no such gate to record.
            unresolved: Fields::default(),
            fatal: None,
        })
    }
}

fn name(observation: impl std::fmt::Debug) -> Value {
    json!(format!("{observation:?}"))
}

fn status(value: &Value) -> Status {
    match value.as_str().unwrap() {
        "Succeeded" => Status::Succeeded,
        "Blocked" => Status::Blocked,
        "Failed" => Status::Failed,
        "Indeterminate" => Status::Indeterminate,
        other => panic!("unknown status {other}"),
    }
}

fn byte(value: &Value) -> Option<u8> {
    value.as_u64().map(|value| u8::try_from(value).unwrap())
}

fn profile(value: &Value) -> Settings {
    let value = members(value, &["refresh", "hdr", "color", "brightness"]);
    let text = |member| value.get(member).and_then(Value::as_str);
    settings(
        text("refresh"),
        value.get("hdr").and_then(Value::as_bool),
        text("color"),
        value.get("brightness").and_then(byte),
    )
}

fn profile_json(settings: &Settings) -> Value {
    let mut json = Map::new();
    if let Some(refresh) = &settings.refresh_rate_key {
        json.insert("refresh".into(), json!(refresh));
    }
    if let Some(hdr) = settings.hdr_enabled {
        json.insert("hdr".into(), json!(hdr));
    }
    if let Some(color) = &settings.color_profile_key {
        json.insert("color".into(), json!(color));
    }
    if let Some(brightness) = settings.brightness_percent {
        json.insert("brightness".into(), json!(brightness));
    }
    Value::Object(json)
}

/// The target managed `ResolveTarget` takes from the step's status.
fn target(value: &Value) -> Target {
    let value = members(value, &["source", "base", "active", "override"]);
    let source = u8::try_from(value["source"].as_u64().unwrap()).unwrap();
    let active = value.get("active").and_then(Value::as_bool) == Some(true);
    Target {
        source,
        application: active.then(|| GAME.to_owned()),
        base: value.get("base").map(profile).unwrap_or_default(),
        application_settings: value.get("override").filter(|_| active).map(profile),
    }
}

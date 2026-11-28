//! The managed vectors for the native Windows-hook function row.
//!
//! `ServiceReactiveLightingLoopTests.NativeFunctionRowVectors` replays each
//! case through managed `FunctionRowKeyboardHookProcessor` (a hook edge) and
//! `FunctionRowHookRecovery` (a Raw Input edge the hook missed) and verifies
//! the same file. Here each case drives `WindowsHook::handle` and
//! `WindowsHook::recover_missed_edge`. `DIFFERENCES` names each case where the
//! native policy deliberately passes a key that managed suppresses, with the
//! native outcome; every other case must match managed exactly. The status
//! rows are read by the Agent's own tests.
use openblade_domain::function_row::{
    Action, Backend, Device, Event, Lifecycle, Observation, Phase, PrimaryMode, Status, Stroke,
    WindowsHook,
};
use serde_json::{Map, Value};

const VECTORS: &str = include_str!(
    "../../../../tools/openblade-windows-host/tests/data/function-row-hook-vectors.json"
);

/// One step outcome: the result and its `Action:Phase` events.
type Outcome = (bool, &'static [&'static str]);

/// Every case native decides differently, with its native outcome per step.
const DIFFERENCES: &[(&str, &str, &[Outcome])] = &[
    (
        "sequence/started-mid-press",
        // A down the hook first sees as already down may have reached the
        // application before the hook started; native passes the whole press.
        "a first edge with wasDown is not a new press",
        &[(false, &[]), (false, &[]), (false, &[])],
    ),
    (
        "fn/unknown",
        // Managed reads Fn as released until a Raw Input report says otherwise.
        "an unknown Fn admits no press",
        &[(false, &[]), (false, &[]), (false, &[])],
    ),
];

const ACTIONS: [(&str, Action); 12] = [
    ("VolumeMute", Action::VolumeMute),
    ("VolumeDown", Action::VolumeDown),
    ("VolumeUp", Action::VolumeUp),
    ("Project", Action::Project),
    ("PreviousTrack", Action::PreviousTrack),
    ("PlayPause", Action::PlayPause),
    ("NextTrack", Action::NextTrack),
    ("DisplayBrightnessDown", Action::DisplayBrightnessDown),
    ("DisplayBrightnessUp", Action::DisplayBrightnessUp),
    ("KeyboardBrightnessDown", Action::KeyboardBrightnessDown),
    ("KeyboardBrightnessUp", Action::KeyboardBrightnessUp),
    ("PrintScreen", Action::PrintScreen),
];
const PHASES: [(&str, Phase); 3] = [
    ("Down", Phase::Down),
    ("Repeat", Phase::Repeat),
    ("Up", Phase::Up),
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

fn string(value: Option<&Value>) -> Result<&str, String> {
    value
        .and_then(Value::as_str)
        .ok_or_else(|| "expected a string".to_owned())
}

fn boolean(value: Option<&Value>) -> Result<bool, String> {
    value
        .and_then(Value::as_bool)
        .ok_or_else(|| "expected true or false".to_owned())
}

fn name<'a>(value: Option<&'a Value>, names: &[&str]) -> Result<&'a str, String> {
    let text = string(value)?;
    names
        .contains(&text)
        .then_some(text)
        .ok_or_else(|| format!("unexpected name {text}"))
}

/// The file shape, checked before any row runs, as the managed reader checks
/// it. The status and preference rows are only required to be arrays here;
/// the Agent tests read them.
fn read_vectors(text: &str) -> Result<Value, String> {
    let root: Value = serde_json::from_str(text).map_err(|error| error.to_string())?;
    let sections = ["comment", "cases", "status", "preference", "malformed"];
    let object = shape(&root, &sections, &sections)?;
    string(object.get("comment"))?;
    for row in rows(object.get("cases"))? {
        case(row)?;
    }
    rows(object.get("status"))?;
    rows(object.get("preference"))?;
    for row in rows(object.get("malformed"))? {
        let malformed = shape(row, &["section", "row"], &["section", "row"])?;
        name(malformed.get("section"), &["cases", "status", "preference"])?;
    }
    Ok(root)
}

/// One case, as the managed `NativeFunctionRowCase` checks it.
fn case(row: &Value) -> Result<(), String> {
    let all = [
        "name",
        "device",
        "backend",
        "driverMode",
        "fn",
        "primary",
        "steps",
        "expect",
    ];
    let vector = shape(row, &all, &all[..7])?;
    string(vector.get("name"))?;
    name(
        vector.get("device"),
        &["Rz09_0581", "Rz09_0528", "Rz09_0484", "Unsupported"],
    )?;
    state(vector)?;
    let steps = rows(vector.get("steps"))?;
    for step in steps {
        let step = shape(
            step,
            &[
                "key",
                "missed",
                "down",
                "wasDown",
                "backend",
                "driverMode",
                "fn",
                "primary",
            ],
            &["down"],
        )?;
        let hook = step.contains_key("key");
        if hook == step.contains_key("missed") || hook != step.contains_key("wasDown") {
            return Err("a step is one hook edge with wasDown or one missed edge".into());
        }
        scan_code(step.get(if hook { "key" } else { "missed" }))?;
        boolean(step.get("down"))?;
        if hook {
            boolean(step.get("wasDown"))?;
        }
        state(step)?;
    }
    if let Some(expect) = vector.get("expect") {
        let expect = rows(Some(expect))?;
        if expect.len() != steps.len() {
            return Err("one outcome per step".into());
        }
        for outcome in expect {
            outcome_of(outcome)?;
        }
    }
    Ok(())
}

/// Every state member is present on a case and optional on a step.
fn state(row: &Map<String, Value>) -> Result<(), String> {
    if let Some(value) = row.get("backend") {
        name(Some(value), &["WindowsHook", "RazerDriver", "Unknown"])?;
    }
    if let Some(value) = row.get("driverMode") {
        name(Some(value), &["Unknown", "Inactive", "Active"])?;
    }
    if let Some(value) = row.get("fn")
        && !value.is_null()
    {
        boolean(Some(value))?;
    }
    if let Some(value) = row.get("primary")
        && !value.is_null()
    {
        name(Some(value), &["FunctionKeys", "MultimediaKeys"])?;
    }
    Ok(())
}

fn scan_code(value: Option<&Value>) -> Result<u16, String> {
    value
        .and_then(Value::as_u64)
        .and_then(|code| u16::try_from(code).ok())
        .ok_or_else(|| "a scan code is 0 to 65535".to_owned())
}

fn outcome_of(value: &Value) -> Result<(bool, Vec<Event>), String> {
    let outcome = shape(value, &["result", "events"], &["result", "events"])?;
    let result = boolean(outcome.get("result"))?;
    let mut events = Vec::new();
    for event in rows(outcome.get("events"))? {
        let (action, phase) = string(Some(event))?
            .split_once(':')
            .ok_or("an event is Action:Phase")?;
        let action = ACTIONS
            .iter()
            .find(|(text, _)| *text == action)
            .ok_or("an event is Action:Phase")?
            .1;
        let phase = PHASES
            .iter()
            .find(|(text, _)| *text == phase)
            .ok_or("an event is Action:Phase")?
            .1;
        events.push((action, phase));
    }
    // The scan code is the step's; it is filled in by the comparison.
    Ok((
        result,
        events
            .into_iter()
            .map(|(action, phase)| Event {
                scan_code: 0,
                action,
                phase,
            })
            .collect(),
    ))
}

struct Current {
    device: Option<Device>,
    backend: Option<Backend>,
    status: Status,
    fn_pressed: Option<bool>,
    primary: Option<PrimaryMode>,
}

impl Current {
    fn apply(&mut self, row: &Map<String, Value>) {
        if let Some(value) = row.get("backend") {
            self.backend = match value.as_str() {
                Some("WindowsHook") => Some(Backend::WindowsHook),
                Some("RazerDriver") => Some(Backend::RazerDriver),
                _ => None,
            };
        }
        if let Some(value) = row.get("driverMode") {
            self.status = match value.as_str() {
                Some("Active") => Status::FirmwareDriverMode { active: true },
                Some("Inactive") => Status::FirmwareDriverMode { active: false },
                _ => Status::Unknown,
            };
        }
        if let Some(value) = row.get("fn") {
            self.fn_pressed = value.as_bool();
        }
        if let Some(value) = row.get("primary") {
            self.primary = match value.as_str() {
                Some("FunctionKeys") => Some(PrimaryMode::FunctionKeys),
                Some("MultimediaKeys") => Some(PrimaryMode::MultimediaKeys),
                _ => None,
            };
        }
    }

    fn observation(&self) -> Observation {
        Observation {
            exact_device: self.device,
            selected_backend: self.backend,
            status: self.status,
            fn_pressed: self.fn_pressed,
            primary_mode: self.primary,
            lifecycle: Lifecycle::Active,
        }
    }
}

/// The native outcome of each step, with event scan codes cleared as the
/// file's event strings carry none.
fn replay(vector: &Map<String, Value>) -> Vec<(bool, Vec<Event>)> {
    let mut current = Current {
        device: match vector["device"].as_str() {
            Some("Rz09_0581") => Some(Device::Rz09_0581),
            Some("Rz09_0528") => Some(Device::Rz09_0528),
            Some("Rz09_0484") => Some(Device::Rz09_0484),
            _ => None,
        },
        backend: None,
        status: Status::Unknown,
        fn_pressed: None,
        primary: None,
    };
    current.apply(vector);
    let mut policy = WindowsHook::default();
    let mut outcomes = Vec::new();
    for step in vector["steps"].as_array().unwrap() {
        let step = step.as_object().unwrap();
        current.apply(step);
        let down = step["down"].as_bool().unwrap();
        let (result, events): (bool, Vec<Event>) = if let Some(key) = step.get("key") {
            let decision = policy.handle(
                Stroke {
                    scan_code: scan_code(Some(key)).unwrap(),
                    down,
                },
                step["wasDown"].as_bool().unwrap(),
                current.observation(),
            );
            (
                decision.suppress,
                [decision.stale_release, decision.event]
                    .into_iter()
                    .flatten()
                    .collect(),
            )
        } else {
            let recovery = policy.recover_missed_edge(
                Stroke {
                    scan_code: scan_code(step.get("missed")).unwrap(),
                    down,
                },
                false,
                current.observation(),
            );
            (recovery.reinstall, recovery.release.into_iter().collect())
        };
        outcomes.push((
            result,
            events
                .into_iter()
                .map(|event| Event {
                    scan_code: 0,
                    ..event
                })
                .collect(),
        ));
    }
    outcomes
}

fn vectors() -> Value {
    read_vectors(VECTORS).unwrap()
}

#[test]
fn native_hook_policy_matches_the_managed_cases() {
    let root = vectors();
    let mut failures = Vec::new();
    let mut differing = Vec::new();
    for row in root["cases"].as_array().unwrap() {
        let vector = row.as_object().unwrap();
        let name = vector["name"].as_str().unwrap();
        let managed: Vec<_> = vector["expect"]
            .as_array()
            .expect("every case is recorded")
            .iter()
            .map(|outcome| outcome_of(outcome).unwrap())
            .collect();
        let native = replay(vector);
        match DIFFERENCES.iter().find(|(case, ..)| *case == name) {
            Some((_, reason, expected)) => {
                differing.push(name);
                let expected: Vec<_> = expected
                    .iter()
                    .map(|(result, events)| {
                        let events = events.iter().map(|event| {
                            outcome_of(&serde_json::json!({"result": true, "events": [event]}))
                                .unwrap()
                                .1[0]
                        });
                        (*result, events.collect::<Vec<_>>())
                    })
                    .collect();
                assert_ne!(native, managed, "{name} no longer differs: {reason}");
                if native != expected {
                    failures.push(format!("{name} ({reason}): {native:?}"));
                }
            }
            None if native != managed => {
                failures.push(format!("{name}: native {native:?}, managed {managed:?}"));
            }
            None => {}
        }
    }
    assert!(failures.is_empty(), "{failures:#?}");
    // Every listed difference names a recorded case.
    assert_eq!(differing.len(), DIFFERENCES.len());
}

#[test]
fn the_vectors_cover_every_driver_mode_backend_and_fn_state() {
    let root = vectors();
    let names: Vec<&str> = root["cases"]
        .as_array()
        .unwrap()
        .iter()
        .map(|case| case["name"].as_str().unwrap())
        .collect();
    for mode in ["Unknown", "Inactive", "Active"] {
        for backend in ["WindowsHook", "RazerDriver", "Unknown"] {
            for fn_state in ["held", "released"] {
                for primary in ["MultimediaKeys", "FunctionKeys", "unknown"] {
                    let name = format!("decision/{mode}/{backend}/fn-{fn_state}/{primary}");
                    assert!(names.contains(&name.as_str()), "{name}");
                }
            }
        }
    }
    // Only confirmed Driver mode with the hook selected admits a press.
    for row in root["cases"].as_array().unwrap() {
        let name = row["name"].as_str().unwrap();
        let suppressed = row["expect"]
            .as_array()
            .unwrap()
            .iter()
            .any(|outcome| outcome["result"] == true);
        if name.starts_with("decision/") && suppressed {
            assert!(
                name.starts_with("decision/Active/WindowsHook/"),
                "{name} admits a press"
            );
        }
    }
}

#[test]
fn malformed_case_rows_are_refused_by_this_reader_too() {
    let root = vectors();
    let mut refused = 0;
    for row in root["malformed"].as_array().unwrap() {
        if row["section"] == "cases" {
            assert!(case(&row["row"]).is_err(), "accepted {}", row["row"]);
            refused += 1;
        }
    }
    assert!(refused > 10);
    for text in [
        r#"{"comment":"","cases":[],"status":[],"malformed":[]}"#,
        r#"{"comment":"","cases":[],"status":[],"preference":[],"malformed":[],"extra":1}"#,
        r#"{"comment":"","cases":{},"status":[],"preference":[],"malformed":[]}"#,
        r#"{"comment":"","cases":[],"status":[],"preference":{},"malformed":[]}"#,
        r#"{"comment":"","cases":[],"status":[],"preference":[],"malformed":[{"section":"cases"}]}"#,
        r#"{"comment":"","cases":[],"status":[],"preference":[],"malformed":[{"section":"other","row":{}}]}"#,
    ] {
        assert!(read_vectors(text).is_err(), "{text}");
    }
}

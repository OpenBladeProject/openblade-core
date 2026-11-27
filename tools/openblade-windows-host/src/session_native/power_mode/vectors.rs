//! Managed parity: every case of `power-mode-automation-vectors.json`, recorded
//! from the real `WindowsPowerModeAutomationCoordinator` by
//! `WindowsPowerModeAutomationNativeVectorTests`, replays through the native
//! status decode, automation and runner. Each observe step is the v91 status
//! fixture with the case's members, decoded as the watcher decodes it. After
//! each step the runner runs every scheduled apply to its end, as managed runs
//! one inline in `Observe`; a failed call's retry delay runs its case's
//! during steps, as the managed delay does. Native differs in no case.
use super::{tests::status_value, *};
use serde_json::{Map, Value, json};
use std::{cell::RefCell, collections::VecDeque};

const VECTORS: &str = include_str!("../../../tests/data/power-mode-automation-vectors.json");

#[tokio::test(start_paused = true)]
async fn every_managed_vector_replays_through_the_native_automation() {
    let root: Value = serde_json::from_str(VECTORS).unwrap();
    let root = members(&root, &["comment", "cases"]);
    assert!(root["comment"].is_string());
    let cases = root["cases"].as_array().unwrap();
    assert!(cases.len() >= 29, "the reviewed cases are present");
    let mut failures = Vec::new();
    for case in cases {
        let case = members(case, &["name", "applies", "steps", "expect"]);
        let observed = replay(case).await;
        if observed != case["expect"] {
            failures.push(format!(
                "{}:\n  managed {}\n  native  {observed}",
                case["name"], case["expect"]
            ));
        }
    }
    assert!(failures.is_empty(), "{}", failures.join("\n"));
}

/// The object's members, each of which must be one of `allowed`.
fn members<'a>(value: &'a Value, allowed: &[&str]) -> &'a Map<String, Value> {
    let object = value.as_object().unwrap();
    for member in object.keys() {
        assert!(allowed.contains(&member.as_str()), "unexpected {member}");
    }
    object
}

async fn replay(case: &Map<String, Value>) -> Value {
    let automation = Automation::new();
    let applies = RefCell::new(
        case.get("applies")
            .and_then(Value::as_array)
            .cloned()
            .unwrap_or_default()
            .into_iter()
            .collect::<VecDeque<_>>(),
    );
    let calls = RefCell::new(Vec::new());
    let during = RefCell::new(Vec::new());
    let pending = RefCell::new(None::<Vec<Value>>);
    let mut delays = 0;
    let mut results = Vec::new();
    let (_stop, mut receiver) = watch::channel(false);
    for step in case["steps"].as_array().unwrap() {
        results.push(step_result(&automation, step));
        let mut call = async |supply, mode: Mode, _cancel| {
            let supply = match supply {
                Supply::Ac => "AC",
                Supply::Dc => "DC",
            };
            calls.borrow_mut().push(json!([supply, mode as u8]));
            let script = applies.borrow_mut().pop_front();
            *pending.borrow_mut() = script
                .as_ref()
                .and_then(|script| script.get("during"))
                .and_then(Value::as_array)
                .cloned();
            let status = script.as_ref().and_then(|script| script.get("status"));
            Ok(match status {
                Some(status) if status != "Succeeded" => Applied::Failed(Failure::ReadFailed(1)),
                _ => Applied::Succeeded { changed: true },
            })
        };
        let mut delay = async |cancel: &Cancel| {
            delays += 1;
            let steps = pending.borrow_mut().take().unwrap_or_default();
            for step in &steps {
                during.borrow_mut().push(step_result(&automation, step));
            }
            !cancel.is_canceled()
        };
        while run_one(
            &automation,
            &mut call,
            &mut delay,
            &mut receiver,
            &|| Ok(()),
            &mut |_| {},
        )
        .await
        .unwrap()
        {}
    }
    let last = automation.last_result().map(|outcome| {
        json!({
            "generation": outcome.generation,
            "source": outcome.source,
            "mode": outcome.mode as u8,
            "succeeded": matches!(outcome.applied, Applied::Succeeded { .. }),
        })
    });
    json!({
        "results": results,
        "during": during.into_inner(),
        "calls": calls.into_inner(),
        "delays": delays,
        "last": last,
    })
}

fn step_result(automation: &Automation, step: &Value) -> Value {
    let step = members(step, &["observe", "unavailable"]);
    if step.contains_key("unavailable") {
        automation.unavailable().unwrap();
        return Value::Null;
    }
    let observe = members(
        &step["observe"],
        &[
            "source",
            "performance",
            "mode",
            "access",
            "verified",
            "retained",
            "unconfirmed",
            "confirmed",
        ],
    );
    let integer = |name: &str| {
        observe
            .get(name)
            .and_then(Value::as_i64)
            .map(|value| i32::try_from(value).unwrap())
    };
    let mut status = status_value(
        integer("source").unwrap(),
        Some(integer("performance").unwrap()),
        integer("mode"),
    );
    if observe.get("confirmed") == Some(&json!(false)) {
        status["confirmedState"]["performance"] = Value::Null;
    }
    for (member, status_member) in [
        ("access", "controlAccess"),
        ("verified", "deviceVerified"),
        ("unconfirmed", "unconfirmedWrites"),
    ] {
        if let Some(value) = observe.get(member) {
            status[status_member] = value.clone();
        }
    }
    if observe.get("retained") == Some(&json!(true)) {
        status["observationReason"] = json!(3);
    }
    let observed = automation.observe(&tests::snapshot(status)).unwrap();
    json!(format!("{observed:?}"))
}

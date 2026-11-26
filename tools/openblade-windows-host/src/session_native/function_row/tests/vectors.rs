//! The status and preference rows of the managed function-row vectors.
//!
//! `ServiceReactiveLightingLoopTests.NativeFunctionRowVectors` drives managed
//! `ServiceReactiveLightingLoop` with each status and records the device,
//! Driver-mode knowledge and primary mode its hook reads, beside the status
//! members as the IPC codec writes them (`wire`). Here those members replace
//! the v91 fixture's and the Agent's projection must agree. This role admits
//! RZ09-0581 only, so every other recorded device projects to none. Each
//! preference row is a `functionRowBackend` value and the backend managed
//! `UiPreferenceStore.LoadInputPreferencesAsync` reads from a file holding only
//! that member. The hook cases are read by the domain's `function_row_vectors`
//! test.
use openblade_domain::function_row::{Device, PrimaryMode};
use serde_json::{Map, Value};

use super::{Backend, DriverMode, Projection, Snapshot, Unreadable, parse, status};

const VECTORS: &str = include_str!("../../../../tests/data/function-row-hook-vectors.json");
const WIRE: [&str; 4] = [
    "deviceVerified",
    "firmware",
    "keyboardDriverModeActive",
    "functionKeyPrimary",
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

fn named<'a>(value: Option<&'a Value>, names: &[&str]) -> Result<&'a str, String> {
    value
        .and_then(Value::as_str)
        .filter(|text| names.contains(text))
        .ok_or_else(|| format!("unexpected name {value:?}"))
}

fn optional_named<'a>(value: Option<&'a Value>, names: &[&str]) -> Result<Option<&'a str>, String> {
    match value {
        Some(Value::Null) => Ok(None),
        other => named(other, names).map(Some),
    }
}

/// One status row, as the managed `NativeFunctionRowStatusRow` checks it.
fn status_row(row: &Value) -> Result<&Map<String, Value>, String> {
    let vector = shape(row, &["name", "set", "wire", "expect"], &["name", "set"])?;
    vector
        .get("name")
        .and_then(Value::as_str)
        .ok_or("a name is a string")?;
    let all = [
        "deviceVerified",
        "model",
        "keyboardDriverModeActive",
        "functionKeyPrimary",
    ];
    let set = shape(&vector["set"], &all, &all)?;
    set["deviceVerified"]
        .as_bool()
        .ok_or("deviceVerified is true or false")?;
    if !set["model"].is_null() && !set["model"].is_string() {
        return Err("a model is a string or null".into());
    }
    if !set["keyboardDriverModeActive"].is_null() && !set["keyboardDriverModeActive"].is_boolean() {
        return Err("keyboardDriverModeActive is true, false or null".into());
    }
    optional_named(
        set.get("functionKeyPrimary"),
        &["FunctionKeys", "MultimediaKeys"],
    )?;
    if let Some(wire) = vector.get("wire") {
        shape(wire, &WIRE, &["deviceVerified"])?;
    }
    if let Some(expect) = vector.get("expect") {
        let all = ["device", "driverMode", "primary"];
        let expect = shape(expect, &all, &all)?;
        named(
            expect.get("device"),
            &[
                "Unsupported",
                "Rz09_0581",
                "Rz09_0528",
                "Rz09_0484",
                "Rz09_0530",
                "Rz09_0529",
            ],
        )?;
        named(expect.get("driverMode"), &["Unknown", "Inactive", "Active"])?;
        optional_named(expect.get("primary"), &["FunctionKeys", "MultimediaKeys"])?;
    }
    Ok(vector)
}

/// One preference row, as the managed `NativeFunctionRowPreferenceRow` checks it.
fn preference_row(row: &Value) -> Result<&Map<String, Value>, String> {
    let vector = shape(row, &["name", "value", "expect"], &["name", "value"])?;
    vector
        .get("name")
        .and_then(Value::as_str)
        .ok_or("a name is a string")?;
    if vector.contains_key("expect") {
        named(
            vector.get("expect"),
            &["WindowsHook", "RazerDriver", "Unknown", "Unreadable"],
        )?;
    }
    Ok(vector)
}

fn vectors() -> Value {
    let root: Value = serde_json::from_str(VECTORS).unwrap();
    for row in root["status"].as_array().unwrap() {
        status_row(row).unwrap();
    }
    for row in root["preference"].as_array().unwrap() {
        preference_row(row).unwrap();
    }
    root
}

/// The recorded wire members over the v91 fixture; an unwritten member is
/// removed, as an older or newer Service may omit it.
fn snapshot(wire: &Map<String, Value>) -> Snapshot {
    let members: Vec<(&str, Option<Value>)> = WIRE
        .iter()
        .map(|name| (*name, wire.get(*name).cloned()))
        .collect();
    serde_json::from_value(status(&members)).unwrap()
}

#[test]
fn the_agent_projects_each_managed_status_as_the_managed_hook_reads_it() {
    let root = vectors();
    let rows = root["status"].as_array().unwrap();
    assert!(rows.len() >= 10);
    for row in rows {
        let name = row["name"].as_str().unwrap();
        let expect = &row["expect"];
        let projection = Projection::of(&snapshot(row["wire"].as_object().unwrap()));
        let device = match expect["device"].as_str() {
            Some("Rz09_0581") => Some(Device::Rz09_0581),
            _ => None,
        };
        let driver_mode = match expect["driverMode"].as_str() {
            Some("Active") => DriverMode::Active,
            Some("Inactive") => DriverMode::Inactive,
            _ => DriverMode::Unknown,
        };
        let primary = match expect["primary"].as_str() {
            Some("FunctionKeys") => Some(PrimaryMode::FunctionKeys),
            Some("MultimediaKeys") => Some(PrimaryMode::MultimediaKeys),
            _ => None,
        };
        assert_eq!(
            projection,
            Projection {
                device,
                driver_mode,
                primary
            },
            "{name}"
        );
    }
    // Driver mode true, false and null each reached the Agent as recorded.
    for (name, value) in [
        ("driver-mode/active", Value::Bool(true)),
        ("driver-mode/inactive", Value::Bool(false)),
        ("driver-mode/unknown", Value::Null),
    ] {
        let row = rows.iter().find(|row| row["name"] == name).unwrap();
        assert_eq!(row["wire"]["keyboardDriverModeActive"], value, "{name}");
    }
}

#[test]
fn the_agent_reads_each_managed_preference_value_as_the_managed_store_does() {
    let root = vectors();
    let rows = root["preference"].as_array().unwrap();
    assert!(rows.len() >= 50);
    for row in rows {
        let content =
            serde_json::to_vec(&serde_json::json!({ "functionRowBackend": row["value"] })).unwrap();
        let expected = match row["expect"].as_str() {
            Some("WindowsHook") => Ok(Some(Backend::WindowsHook)),
            Some("RazerDriver") => Ok(Some(Backend::RazerDriver)),
            Some("Unknown") => Ok(None),
            _ => Err(Unreadable),
        };
        assert_eq!(parse(Some(&content)), expected, "{}", row["name"]);
    }
    // The comma, sign and white-space forms the review found accepted.
    for value in [
        "1,1",
        "WindowsHook,1",
        "1,WindowsHook",
        "0,1",
        "RazerDriver,0",
        "1\u{a0}",
    ] {
        let row = rows.iter().find(|row| row["value"] == value).unwrap();
        assert_eq!(row["expect"], "Unknown", "{value:?}");
    }
}

#[test]
fn malformed_status_and_preference_rows_are_refused_by_this_reader_too() {
    let root = vectors();
    let (mut status, mut preference) = (0, 0);
    for row in root["malformed"].as_array().unwrap() {
        if row["section"] == "status" {
            assert!(status_row(&row["row"]).is_err(), "accepted {}", row["row"]);
            status += 1;
        } else if row["section"] == "preference" {
            assert!(
                preference_row(&row["row"]).is_err(),
                "accepted {}",
                row["row"]
            );
            preference += 1;
        }
    }
    assert!(status >= 5 && preference >= 5);
}

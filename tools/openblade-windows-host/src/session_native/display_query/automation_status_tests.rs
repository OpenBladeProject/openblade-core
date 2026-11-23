//! Managed `ResolveTarget` and the watcher's hand-off over the managed v85
//! status fixture: display members decode with the profile-set codec, the
//! active application profile is the first enabled match, and a status
//! managed would refuse makes the context unavailable.
use super::{GAME, Rig, settings};
use crate::{
    service_status::Snapshot,
    session_native::display_query::{automation::target, recovery::Observation},
};
use serde_json::{Value, json};

const FRAMES: &[u8] = include_bytes!("../../../tests/fixtures/get-status-v85.frames");

/// The v85 fixture status with `change` applied.
fn status(change: impl FnOnce(&mut Value)) -> Snapshot {
    let request = i32::from_le_bytes(FRAMES[..4].try_into().unwrap()) as usize;
    let mut frame: Value = serde_json::from_slice(&FRAMES[4 + request + 4..]).unwrap();
    let status = &mut frame["status"];
    change(status);
    serde_json::from_value(status.take()).unwrap()
}

fn display(refresh: Option<&str>, brightness: Option<u8>) -> Value {
    json!({"refreshRateKey": refresh, "hdrEnabled": null, "colorProfileKey": null,
        "brightnessPercent": brightness, "isConfigured": refresh.is_some() || brightness.is_some()})
}

fn game(enabled: Option<bool>, battery: Value) -> Value {
    let mut profile = json!({"id": GAME, "name": "Game", "executablePath": "C:\\Games\\Game.exe",
        "activationMode": 0, "overrides": {"battery": {"display": battery}}});
    if let Some(enabled) = enabled {
        profile["enabled"] = json!(enabled);
    }
    profile
}

fn with_game(status: &mut Value, profiles: Vec<Value>, active: Value) {
    status["activePowerSource"] = json!(1);
    status["profiles"]["battery"]["display"] = display(None, Some(30));
    status["profiles"]["applicationProfiles"] = Value::Array(profiles);
    status["activeApplicationProfileId"] = active;
}

#[test]
fn each_source_takes_its_own_power_profile() {
    let snapshot = status(|status| {
        status["profiles"]["pluggedIn"]["display"] = display(Some("fixed-240"), None);
        status["profiles"]["battery"]["display"] = display(Some("fixed-60"), Some(30));
        status["profiles"]["usbC"]["display"] = Value::Null;
    });
    let fields = || snapshot.display_automation();
    let plugged = target::resolve(fields(), 0).unwrap();
    assert_eq!(plugged.base, settings(Some("fixed-240"), None, None, None));
    assert_eq!(plugged.application, None);
    let battery = target::resolve(fields(), 1).unwrap();
    assert_eq!(
        battery.base,
        settings(Some("fixed-60"), None, None, Some(30))
    );
    // A missing display member is unconfigured.
    assert_eq!(
        target::resolve(fields(), 2).unwrap().base,
        Default::default()
    );
}

#[test]
fn the_active_application_override_is_the_first_enabled_match() {
    for (profiles, expected) in [
        (
            vec![game(None, display(Some("fixed-120"), None))],
            Some("fixed-120"),
        ),
        (
            vec![
                game(Some(false), display(Some("fixed-90"), None)),
                game(Some(true), display(Some("fixed-120"), None)),
            ],
            Some("fixed-120"),
        ),
        (vec![game(None, Value::Null)], None),
    ] {
        let snapshot = status(|status| with_game(status, profiles, json!(GAME.to_uppercase())));
        let resolved = target::resolve(snapshot.display_automation(), 1).unwrap();
        assert_eq!(resolved.application.as_deref(), Some(GAME));
        assert_eq!(
            resolved
                .application_settings
                .and_then(|settings| settings.refresh_rate_key),
            expected.map(str::to_owned)
        );
        assert_eq!(resolved.base, settings(None, None, None, Some(30)));
    }
    // Another source's override does not apply.
    let snapshot = status(|status| {
        with_game(
            status,
            vec![game(None, display(Some("fixed-120"), None))],
            json!(GAME),
        );
    });
    let plugged = target::resolve(snapshot.display_automation(), 0).unwrap();
    assert_eq!(plugged.application_settings, None);
}

#[test]
fn a_status_managed_would_refuse_resolves_no_target() {
    type Case = (&'static str, Box<dyn Fn(&mut Value)>);
    let cases: [Case; 5] = [
        (
            "an active ID without an enabled profile",
            Box::new(|status| {
                with_game(status, vec![game(Some(false), Value::Null)], json!(GAME));
            }),
        ),
        (
            "a non-GUID active ID",
            Box::new(|status| with_game(status, vec![], json!("game"))),
        ),
        (
            "a malformed power display",
            Box::new(|status| {
                status["profiles"]["battery"]["display"] = json!({"refreshRateKey": 60});
            }),
        ),
        (
            "a malformed override display",
            Box::new(|status| {
                with_game(status, vec![game(None, json!([]))], json!(GAME));
            }),
        ),
        (
            "a non-boolean enabled flag on the match",
            Box::new(|status| {
                let mut profile = game(None, Value::Null);
                profile["enabled"] = json!(1);
                with_game(status, vec![profile], json!(GAME));
            }),
        ),
    ];
    for (name, change) in cases {
        let snapshot = status(|status| change(status));
        assert!(
            target::resolve(snapshot.display_automation(), 1).is_none(),
            "{name}"
        );
    }
}

#[test]
fn the_watcher_hands_each_status_to_the_coordinator_after_power_admission() {
    let rig = Rig::new();
    let battery = status(|status| status["activePowerSource"] = json!(1));
    rig.power.opened(battery.display_power_source(), false);
    rig.automation.observe(&battery).unwrap();
    assert_eq!(rig.file.record()["LastReconciledPowerSource"], 1);

    // Unconfirmed: ExternalUnknown with manual-external evidence cancels.
    let plugged = status(|status| status["activePowerSource"] = json!(0));
    rig.power.observed(0, false);
    rig.automation.observe(&plugged).unwrap();
    let canceled = rig.recovery().scheduled().unwrap().cancel.clone();
    let external = status(|status| {
        status["activePowerSource"] = json!(3);
        status["externalPowerPresent"] = json!(true);
        status["deviceVerified"] = json!(true);
        status["controlAccess"] = json!(0);
    });
    rig.power
        .observed(3, external.display_manual_external_power());
    rig.automation.observe(&external).unwrap();
    assert!(canceled.canceled());

    // A refused status also withdraws power admission, as managed's failed
    // watcher does.
    rig.power.observed(1, false);
    rig.automation.observe(&battery).unwrap();
    assert!(rig.power.capture().is_some());
    let malformed = status(|status| {
        status["activePowerSource"] = json!(1);
        status["profiles"]["battery"]["display"] = json!(7);
    });
    rig.automation.observe(&malformed).unwrap();
    assert_eq!(rig.power.capture(), None);
    assert_eq!(
        rig.recovery().mark_unavailable(false),
        Observation::IgnoredUnconfirmed
    );
    rig.automation.unavailable().unwrap();
}

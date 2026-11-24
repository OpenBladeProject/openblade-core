//! The profile layer over saved profiles as managed writes them: lighting
//! parameters carry the polymorphic `kind` discriminator first, and members
//! follow it. `Storage::profile_layer` decodes the committed bytes in their
//! own member order, so ordinary keyboard lighting (Static, Breathing and the
//! others with members after `kind`) resolves like `{"kind":"off"}`.
use super::*;
use crate::installed::readiness::thermal_storage::Foreground;

const RGB: &str = r#"{"red":200,"green":10,"blue":5}"#;

/// Plugged-in Static lighting as managed writes it, `kind` first.
fn static_lighting() -> String {
    format!(
        r#"{{"effect":1,"color":{RGB},"brightness":10,"parameters":{{"kind":"static","color":{RGB}}},"logoMode":null}}"#
    )
}

/// Replaces the saved plugged-in lighting with `lighting`, written in the
/// member order given rather than through `Value`, and commits the aggregate.
fn put_plugged_in_lighting(files: &MemoryFiles, lighting: &str) {
    let mut document = files.document("profiles.json");
    document["profiles"]["pluggedIn"]["lighting"] = json!("@@LIGHTING@@");
    let text = serde_json::to_string(&document).unwrap();
    assert!(text.contains(r#""@@LIGHTING@@""#));
    let text = text.replace(r#""@@LIGHTING@@""#, lighting);
    files
        .bytes
        .lock()
        .unwrap()
        .insert("profiles.json".into(), text.into_bytes());
    files.commit_configuration();
}

/// Every managed-recorded aggregate that this host admits and managed does
/// not refuse resolves through the profile layer at every class. Of managed's
/// refusals, the unknown member is one the layer also refuses.
#[test]
fn every_managed_admitted_aggregate_resolves_through_the_profile_layer() {
    use openblade_domain::profile_set::json as pjson;
    let (mut resolved, mut refused_by_managed) = (0, 0);
    for (file, text) in [
        (
            "aggregate",
            include_str!("../../../../../tests/data/profile-aggregate-vectors.json"),
        ),
        (
            "migration",
            include_str!("../../../../../tests/data/profile-migration-vectors.json"),
        ),
    ] {
        let root = pjson::parse(text.as_bytes()).unwrap();
        let Some(pjson::Json::Array(cases)) = root.member("cases") else {
            panic!("{file} has no cases");
        };
        for case in cases {
            let name = case.member("name").and_then(|n| n.text()).unwrap();
            // The recorded bytes, in their own member order.
            let document = |key: &str| -> Option<Vec<u8>> {
                if let Some(text) = case.member(&format!("{key}Text")) {
                    return Some(text.text().unwrap().as_bytes().to_vec());
                }
                case.member(key).map(|value| pjson::write(value, false))
            };
            let profile = document("profile");
            let preferences = document("preferences");
            let schema = case.member("schema").and_then(|s| s.integer(0, 1000));
            let marker = match (case.member("markerText"), schema) {
                (Some(text), _) => Some(text.text().unwrap().as_bytes().to_vec()),
                (None, Some(schema)) => Some(
                    serde_json::to_vec(&json!({
                        "schemaVersion": schema,
                        "profileSha256": crate::configuration::digest_hex(profile.as_deref().unwrap_or_default()),
                        "preferencesSha256": crate::configuration::digest_hex(preferences.as_deref().unwrap_or_default()),
                    }))
                    .unwrap(),
                ),
                (None, None) => None,
            };
            let files = MemoryFiles::default();
            {
                let mut bytes = files.bytes.lock().unwrap();
                for (name, value) in [
                    ("profiles.json", profile),
                    ("battery-preferences.json", preferences),
                    ("battery-configuration.commit.json", marker),
                ] {
                    if let Some(value) = value {
                        bytes.insert(name.into(), value);
                    }
                }
            }
            let Ok(storage) = Storage::open(Box::new(files.clone()), SID) else {
                continue;
            };
            let Ok(Some(_)) = storage.profiles() else {
                continue;
            };
            let admitted = case
                .member("expect")
                .and_then(|e| e.member("admitted"))
                .and_then(|a| a.boolean());
            let layer = storage.profile_layer(&Foreground::default());
            if name == "current profile with an unknown member" {
                assert!(layer.is_err(), "{name}");
            }
            if admitted == Some(false) {
                refused_by_managed += 1;
                continue;
            }
            let layer = layer.unwrap_or_else(|e| panic!("{file} | {name}: {e}"));
            for power in [0, 1, 2] {
                layer
                    .at(power)
                    .unwrap_or_else(|e| panic!("{file} | {name} at {power}: {e}"));
            }
            resolved += 1;
        }
    }
    // With service-profiles-v1 the open migrates schema 1-24 stores, so that
    // lane resolves them too; every lane resolves the schema 25-27 aggregates.
    println!("resolved={resolved} refused_by_managed={refused_by_managed}");
    let expected = if cfg!(feature = "service-profiles-v1") {
        95
    } else {
        17
    };
    assert_eq!(resolved, expected);
}

#[tokio::test]
async fn a_performance_request_resolves_a_profile_with_static_lighting() {
    let files = MemoryFiles::default();
    files.profiles(1);
    put_plugged_in_lighting(&files, &static_lighting());
    let rig = Rig::new(files);
    rig.get().await;
    fresh_observe(&rig).await;
    let result = rig.set(1, value(2, 1)).await;
    assert_eq!(result["result"]["status"], 1, "{result}");
    assert_eq!(rig.port.state.lock().unwrap().writes, 2);
    let saved = rig.files.document("manual-overrides.json");
    assert_eq!(saved["overrides"]["performance"], 2);
    rig.stop().await;
}

/// Managed `ProfileStore` refuses a saved profile with an unknown member, and
/// so does the profile layer. The request resolves that layer before storing
/// its override, so nothing is stored and no setter is submitted.
#[tokio::test]
async fn a_performance_request_over_profiles_the_decoder_refuses_stores_nothing() {
    let files = MemoryFiles::default();
    files.profiles(1);
    let mut document = files.document("profiles.json");
    document["profiles"]["pluggedIn"]["unknown"] = json!(1);
    files.put("profiles.json", document);
    files.commit_configuration();
    let storage = Storage::open(Box::new(files.clone()), SID).unwrap();
    assert!(storage.profiles().unwrap().is_some());
    assert!(storage.profile_layer(&Foreground::default()).is_err());
    let rig = Rig::new(files);
    rig.get().await;
    fresh_observe(&rig).await;
    let before = rig.files.read("manual-overrides.json").unwrap();
    let result = rig.set(1, value(2, 1)).await;
    assert_ne!(result["result"]["status"], 1, "{result}");
    assert_eq!(rig.files.read("manual-overrides.json").unwrap(), before);
    assert_eq!(rig.port.state.lock().unwrap().writes, 0);
    rig.stop().await;
}

#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn a_fan_request_resolves_a_profile_with_static_lighting() {
    let r = fan_rig_with(0, 0, 1);
    put_plugged_in_lighting(&r.files, &static_lighting());
    r.get().await;
    let result = r.request(fan_request(7, Some(1), 4000)).await;
    assert_eq!(result["result"]["status"], 1, "{result}");
    assert_eq!(r.port.state.lock().unwrap().writes, 4);
    assert_eq!(r.port.state.lock().unwrap().fans, [1, 1]);
    let saved = r.files.document("manual-overrides.json");
    assert_eq!(saved["overrides"]["fanControl"]["fixedSpeedRpm"], 4000);
    r.origin.stop();
    r.stop().await;
}

/// OpenBlade-owned manual fans, then a profile save that changes only the
/// plugged-in lighting to Static: the return to Automatic and the clear still
/// resolve the saved profile and reach the firmware.
#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn owned_manual_fans_return_to_automatic_after_a_static_lighting_profile() {
    let r = fan_rig_with(0, 0, 1);
    r.get().await;
    let manual = r.request(fan_request(101, Some(1), 4000)).await;
    assert_eq!(manual["result"]["status"], 1, "{manual}");
    assert_eq!(r.port.state.lock().unwrap().fans, [1, 1]);
    put_plugged_in_lighting(&r.files, &static_lighting());
    let writes = r.port.state.lock().unwrap().writes;
    let automatic = r.request(fan_request(102, Some(0), 3700)).await;
    assert_eq!(automatic["result"]["status"], 1, "{automatic}");
    {
        let state = r.port.state.lock().unwrap();
        assert_eq!(state.fans, [0, 0]);
        assert_eq!(state.writes - writes, 2);
    }
    let writes = r.port.state.lock().unwrap().writes;
    let clear = r.request(fan_request(103, None, 0)).await;
    assert_eq!(clear["result"]["status"], 1, "{clear}");
    assert_eq!(r.port.state.lock().unwrap().writes, writes);
    let status = r.get().await;
    assert!(status["status"]["fanOverride"].is_null());
    assert!(
        !status["status"]["message"]
            .as_str()
            .unwrap()
            .contains("Saved private policy is unavailable"),
        "{status}"
    );
    r.origin.stop();
    r.stop().await;
    assert_eq!(r.port.state.lock().unwrap().fans, [0, 0]);
}

/// The profile's own fan control, not the legacy default for its mode, is
/// what a fan clear applies: its fixed speed reaches the 0D/01 setters.
#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn a_profile_fan_control_reaches_the_fan_setters() {
    let r = fan_rig(0, 0);
    let mut document = r.files.document("profiles.json");
    let mut fan = crate::installed::readiness::thermal_storage::legacy_fan(1);
    assert_ne!(fan["fixedSpeedRpm"], 4200);
    fan["fixedSpeedRpm"] = json!(4200);
    document["profiles"]["pluggedIn"]["fanControl"] = fan;
    document["profiles"]["pluggedIn"]["fans"] = json!(1);
    r.files.put("profiles.json", document);
    r.files.commit_configuration();
    r.get().await;
    let result = r.request(fan_request(104, None, 0)).await;
    assert_eq!(result["result"]["status"], 1, "{result}");
    let recipe = fan_rows(&r).last().unwrap()["fan"].clone();
    assert_eq!(recipe["intent"]["mode"], 1);
    assert_eq!(recipe["intent"]["rpm"], 4200);
    let speeds: Vec<u8> = r
        .port
        .state
        .lock()
        .unwrap()
        .sent
        .iter()
        .filter(|(class, command, _, _)| (*class, *command) == (13, 1))
        .map(|(_, _, payload, _)| payload[2])
        .collect();
    assert_eq!(speeds, [42, 42]);
    r.origin.stop();
    r.stop().await;
}

/// A saved fan control whose fixed speed is a JSON string binds as managed
/// binds it (`AllowReadingFromString`), so the resolved fan intent carries the
/// number. Before the domain resolution, native read the stored member as-is
/// and `validate_settings` refused it; this is a deliberate difference from
/// that build.
#[test]
fn a_profile_fixed_speed_saved_as_a_string_resolves_to_its_number() {
    for text in ["3700", "03700"] {
        let files = MemoryFiles::default();
        files.profiles(1);
        let mut document = files.document("profiles.json");
        let mut stored = crate::installed::readiness::thermal_storage::legacy_fan(1);
        stored["fixedSpeedRpm"] = json!(text);
        assert!(openblade_domain::fan_history::validate_settings(&stored).is_err());
        document["profiles"]["pluggedIn"]["fanControl"] = stored;
        document["profiles"]["pluggedIn"]["fans"] = json!(1);
        files.put("profiles.json", document);
        files.commit_configuration();
        let storage = Storage::open(Box::new(files), SID).unwrap();
        let mut expected = crate::installed::readiness::thermal_storage::legacy_fan(1);
        expected["fixedSpeedRpm"] = json!(3700);
        assert_eq!(saved_fan(&storage, 0).unwrap(), Some(expected), "{text}");
    }
}

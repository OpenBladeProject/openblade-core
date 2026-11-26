//! Installed `updateProfiles` through the real framed dispatcher. The store
//! is the in-memory model; no hardware, pipe or operating-system supplier is
//! reached, and every case asserts that no mailbox command was written.
use super::*;
use openblade_domain::profile_set::{
    self, Capabilities, Dialect, Json, ProfileSet, ValidationPolicy, Validator, encode, normalize,
    preferences, update,
};

const STORED: &str = "Profiles stored without automatic hardware application.";
const VECTORS: &str = include_str!("../../../../../tests/data/profile-set-vectors-v27.json");
const NAMES: [&str; 3] = [
    "profiles.json",
    "battery-preferences.json",
    "battery-configuration.commit.json",
];

/// The set the fixture aggregate holds, as the Tray would send it back.
fn saved(files: &MemoryFiles) -> Value {
    files.document("profiles.json")["profiles"].clone()
}

fn update_request(n: u8, profiles: &Value) -> Value {
    json!({
        "operation": "updateProfiles", "version": 91,
        "requestId": format!("aaaaaaaa-bbbb-cccc-dddd-{n:012}"),
        "profiles": profiles,
    })
}

fn result(status: u8, message: &str, n: u8) -> Value {
    json!({
        "frame": "response", "version": 91,
        "requestId": format!("aaaaaaaa-bbbb-cccc-dddd-{n:012}"),
        "success": true, "error": null, "status": null,
        "result": {"status": status, "message": message},
        "archive": null, "errorCode": null,
    })
}

fn documents(files: &MemoryFiles) -> [Vec<u8>; 3] {
    let bytes = files.bytes.lock().unwrap();
    NAMES.map(|name| bytes[name].clone())
}

/// What managed writes for `profiles` over the saved set, by the domain
/// codec the managed vectors pin.
fn expected_profile_document(profiles: &Value, saved: &Value) -> Vec<u8> {
    fn same_path(left: &str, right: &str) -> bool {
        left.eq_ignore_ascii_case(right)
    }
    let typed = |value: &Value| {
        let tree = profile_set::json::parse(&serde_json::to_vec(value).unwrap()).unwrap();
        update::decode_profiles(&tree, Dialect::Document).unwrap()
    };
    let capabilities = Capabilities::rz09_0581();
    let validator = Validator::new(
        ValidationPolicy::for_capabilities(&capabilities, &capabilities),
        same_path,
    );
    let mut prior: ProfileSet = typed(saved);
    normalize::normalize_legacy_battery_profile(&mut prior, None).unwrap();
    let admitted = update::admit(
        typed(profiles),
        &prior,
        &validator,
        &validator,
        Some(&capabilities),
    )
    .unwrap();
    encode::profile_document(profile_set::CURRENT_SCHEMA, &admitted)
}

/// One framed exchange with exact request bytes. The reply is empty when
/// the Service closes the connection without answering.
async fn exchange(rig: &Rig, payload: &[u8]) -> Vec<u8> {
    exchange_as(rig, payload, USER).await
}

async fn exchange_as(rig: &Rig, payload: &[u8], caller: Caller) -> Vec<u8> {
    let (mut client, server) = duplex(65536);
    let source = rig.source.clone();
    let publications = source.lock().await.publications();
    let served = serve_connection(
        Box::new(SidStream {
            inner: test_stream(server, caller, Fault::None),
            sid: SID.to_owned(),
        }),
        source,
        publications,
        Rc::new(Subscriptions::new(2)),
    );
    let exchange = async {
        crate::service_frame::write_payload(&mut client, payload)
            .await
            .unwrap();
        let mut reply = Vec::new();
        tokio::io::AsyncReadExt::read_to_end(&mut client, &mut reply)
            .await
            .unwrap();
        reply
    };
    let (served, reply) = tokio::join!(served, exchange);
    assert!(
        !matches!(served, Err(ExchangeError::Runtime(_))),
        "a client fault never fails the Service"
    );
    assert_eq!(served.is_ok(), !reply.is_empty());
    reply
}

fn no_hardware(rig: &Rig) {
    let port = rig.port.state.lock().unwrap();
    assert_eq!(port.writes, 0, "saving profiles writes no mailbox command");
}

async fn published_profiles(rig: &Rig) -> (u64, Value, Value) {
    let publications = rig.source.lock().await.publications().unwrap();
    let publication = publications.borrow().clone();
    let active = serde_json::to_value(&publication.active).unwrap();
    let inactive = serde_json::to_value(&publication.inactive).unwrap();
    (
        active["revision"].as_u64().unwrap(),
        active["profiles"].clone(),
        inactive["profiles"].clone(),
    )
}

#[tokio::test]
async fn installed_update_profiles_stores_the_managed_aggregate_and_publishes_it() {
    let files = MemoryFiles::default();
    files.profiles(1);
    let rig = Rig::new(files.clone());
    rig.get().await;
    let (revision, _, _) = published_profiles(&rig).await;
    let prior = documents(&files);
    let prior_set = saved(&files);
    let mut profiles = prior_set.clone();
    profiles["batteryPolicy"]["chargeLimitPercent"] = json!(75);
    profiles["battery"]["batteryProtectionEnabled"] = json!(false);
    profiles["pluggedIn"]["display"]["refreshRateKey"] = json!("240 Hz \u{e9}<&>");

    let response = rig.request(update_request(1, &profiles)).await;

    // Managed's reply: one result body, no status body.
    assert_eq!(response, result(0, STORED, 1));
    let [profile, stored_preferences, marker] = documents(&files);
    assert_eq!(profile, expected_profile_document(&profiles, &prior_set));
    assert!(profile.starts_with(b"{\r\n  \"schemaVersion\": 27,\r\n  \"profiles\": {\r\n"));
    assert!(
        String::from_utf8(profile.clone())
            .unwrap()
            .contains(r#""refreshRateKey": "240 Hz \u00E9\u003C\u0026\u003E""#)
    );
    // The preferences document is carried over; the marker commits both.
    assert_eq!(stored_preferences, prior[1]);
    assert_eq!(
        marker,
        preferences::commit_marker(
            27,
            &crate::configuration::digest_hex(&profile),
            &crate::configuration::digest_hex(&stored_preferences),
        )
    );
    assert!(
        !files
            .bytes
            .lock()
            .unwrap()
            .keys()
            .any(|name| name.contains("recovery-required") || name.contains(".transaction-")),
        "no record or backup remains"
    );
    no_hardware(&rig);

    // The save is published at the next revision, to the owner only.
    let (published, active, inactive) = published_profiles(&rig).await;
    assert!(published > revision);
    assert_eq!(active["batteryPolicy"]["chargeLimitPercent"], 75);
    assert_eq!(active["battery"]["batteryProtectionEnabled"], false);
    assert_ne!(inactive["batteryPolicy"]["chargeLimitPercent"], 75);
    // A later status read shows the same saved set.
    let status = rig.get().await;
    assert_eq!(
        status["status"]["profiles"]["batteryPolicy"]["chargeLimitPercent"],
        75
    );
    // Managed `UpdateProfilesAsync` records the save in the diagnostic
    // history the export reads.
    #[cfg(feature = "service-diagnostics-export-v1")]
    {
        let history = rig.source.lock().await.diagnostics().history;
        let snapshot = history.snapshot().unwrap();
        let recorded: Vec<_> = snapshot
            .results
            .iter()
            .map(|result| {
                (
                    result.subsystem.as_str(),
                    result.action.as_str(),
                    result.status.as_str(),
                    result.detail.as_deref(),
                )
            })
            .collect();
        assert_eq!(recorded, [("Service", "Reconcile", "Stored", Some(STORED))]);
    }
    assert_eq!(
        status["status"]["profiles"]["pluggedIn"]["display"]["refreshRateKey"],
        "240 Hz \u{e9}<&>"
    );
    rig.stop().await;
}

#[tokio::test]
async fn installed_update_profiles_rejects_intent_with_the_managed_message_and_stores_nothing() {
    let files = MemoryFiles::default();
    files.profiles(1);
    let rig = Rig::new(files.clone());
    rig.get().await;
    let prior = files.bytes.lock().unwrap().clone();
    let set = saved(&files);
    let edit = |change: &dyn Fn(&mut Value)| {
        let mut profiles = set.clone();
        change(&mut profiles);
        profiles
    };
    for (n, profiles, message) in [
        // Structural: managed refuses it for any device.
        (
            1,
            edit(&|p| p["battery"]["performance"] = json!(2)),
            "The requested intent was rejected: The battery profile is invalid: The performance preset is not valid for this power source.",
        ),
        // Capability dependent: valid stored intent elsewhere, not selectable
        // on exact RZ09-0581.
        (
            2,
            edit(&|p| p["pluggedIn"]["performance"] = json!(6)),
            "The requested intent was rejected: The performance preset is not supported by the active device capability.",
        ),
        (
            3,
            edit(&|p| {
                p["pluggedIn"]["performance"] = json!(5);
                p["pluggedIn"]["customPerformance"] = json!({"cpu": 2, "gpu": 2});
            }),
            "The requested intent was rejected: The changed custom performance combination is not supported by this device.",
        ),
        (
            4,
            edit(&|p| p["battery"]["stopFansInBatterySaver"] = json!(true)),
            "The requested intent was rejected: Battery Saver fan stop requires explicit support from the current device.",
        ),
        (
            5,
            edit(&|p| p["usbC"]["lighting"] = Value::Null),
            "The requested intent was rejected: Value cannot be null. (Parameter 'settings')",
        ),
    ] {
        // Managed `Failed`, inside a successful response frame.
        assert_eq!(
            rig.request(update_request(n, &profiles)).await,
            result(3, message, n)
        );
        assert_eq!(*files.bytes.lock().unwrap(), prior, "{message}");
    }
    no_hardware(&rig);
    // Whatever is published is still the saved set, never a refused one.
    let (_, active, _) = published_profiles(&rig).await;
    assert_eq!(
        active["battery"]["performance"],
        set["battery"]["performance"]
    );
    assert_eq!(active["pluggedIn"]["performance"], 1);
    assert_eq!(active["battery"]["stopFansInBatterySaver"], false);
    assert_eq!(active["usbC"]["lighting"], set["usbC"]["lighting"]);
    rig.stop().await;
}

#[tokio::test]
async fn installed_update_profiles_closes_on_a_frame_managed_refuses() {
    use crate::service_status::StatusRequest;
    let files = MemoryFiles::default();
    files.profiles(1);
    let set = saved(&files);
    let frame = |change: &dyn Fn(&mut Value)| {
        let mut request = update_request(1, &set);
        change(&mut request);
        serde_json::to_vec(&request).unwrap()
    };
    assert!(StatusRequest::decode(&frame(&|_| {})).is_ok());
    let mut refused = vec![
        frame(&|r| r["profiles"] = Value::Null),
        frame(&|r| drop(r.as_object_mut().unwrap().remove("profiles"))),
        frame(&|r| r["profiles"]["pluggedIn"] = Value::Null),
        frame(&|r| r["profiles"]["pluggedIn"]["unknown"] = json!(1)),
        frame(&|r| r["profiles"]["pluggedIn"]["fans"] = json!("0")),
        frame(&|r| r["profiles"]["batteryPolicy"]["chargeLimitPercent"] = json!(100)),
        frame(&|r| r["profiles"]["windowsPowerModes"]["silent"] = json!(3)),
        frame(&|r| r["profiles"]["applicationProfiles"][0]["executablePath"] = json!("Game.exe")),
        frame(&|r| r["extra"] = json!(1)),
        frame(&|r| r["version"] = json!(PROTOCOL_VERSION - 1)),
        frame(&|r| r["requestId"] = json!("not-a-guid")),
    ];
    // The discriminator of lighting parameters must come first, which only
    // the ordered reader can see.
    refused.push(
        String::from_utf8(frame(&|r| {
            r["profiles"]["pluggedIn"]["lighting"]["parameters"] = json!({"kind": "off"})
        }))
        .unwrap()
        .replacen(r#"{"kind":"off"}"#, r#"{"extra":1,"kind":"off"}"#, 1)
        .into_bytes(),
    );
    refused.push(
        String::from_utf8(frame(&|_| {}))
            .unwrap()
            .replacen(r#""fans":0"#, r#""fans":0,"fans":0"#, 1)
            .into_bytes(),
    );
    let rig = Rig::new(files.clone());
    rig.get().await;
    let prior = files.bytes.lock().unwrap().clone();
    for payload in refused {
        let text = String::from_utf8_lossy(&payload).into_owned();
        assert!(StatusRequest::decode(&payload).is_err(), "{text}");
        // Served and malformed: no reply, and never the unsupported refusal.
        assert!(
            crate::service_status::UnservedRequest::decode(&payload).is_err(),
            "{text}"
        );
        let reply = exchange(&rig, &payload).await;
        assert!(reply.is_empty(), "{text}");
    }
    assert_eq!(*files.bytes.lock().unwrap(), prior);
    assert_eq!(rig.entered.get(), 0, "no refused frame reaches the runtime");
    no_hardware(&rig);
    rig.stop().await;
}

#[tokio::test]
async fn installed_update_profiles_closes_for_a_caller_outside_the_control_session() {
    let files = MemoryFiles::default();
    files.profiles(1);
    let rig = Rig::new(files.clone());
    rig.get().await;
    let prior = files.bytes.lock().unwrap().clone();
    let mut profiles = saved(&files);
    profiles["batteryPolicy"]["chargeLimitPercent"] = json!(60);
    let payload = serde_json::to_vec(&update_request(1, &profiles)).unwrap();
    // Another session holds control: like every installed mutation, the
    // request stores nothing and the connection closes without a reply.
    let other_session = Caller {
        control_session: Some(9),
        ..USER
    };

    assert!(exchange_as(&rig, &payload, other_session).await.is_empty());

    assert_eq!(*files.bytes.lock().unwrap(), prior);
    // The owner's identical request is stored.
    assert_eq!(
        rig.request(update_request(1, &profiles)).await,
        result(0, STORED, 1)
    );
    no_hardware(&rig);
    rig.stop().await;
}

#[tokio::test]
async fn installed_update_profiles_blocks_a_preserved_aggregate() {
    let files = MemoryFiles::default();
    files.profiles(1);
    let rig = Rig::new(files.clone());
    rig.get().await;
    let mut profiles = saved(&files);
    profiles["batteryPolicy"]["chargeLimitPercent"] = json!(60);
    // Another writer leaves a transaction record after this store opened.
    files.bytes.lock().unwrap().insert(
        "battery-configuration.commit.json.recovery-required".into(),
        b"{}".to_vec(),
    );
    let prior = files.bytes.lock().unwrap().clone();

    let response = rig.request(update_request(1, &profiles)).await;

    assert_eq!(
        response,
        result(
            2,
            "OpenBlade preserved an unsupported or invalid configuration. Thermal settings are read-only until the service restarts with a valid configuration.",
            1
        )
    );
    assert_eq!(*files.bytes.lock().unwrap(), prior);
    no_hardware(&rig);
    rig.stop().await;
}

#[tokio::test]
async fn installed_update_profiles_serialises_concurrent_requests() {
    let files = MemoryFiles::default();
    files.profiles(1);
    let rig = Rig::new(files.clone());
    rig.get().await;
    let (revision, _, _) = published_profiles(&rig).await;
    let set = saved(&files);
    let with_limit = |limit: u8| {
        let mut profiles = set.clone();
        profiles["batteryPolicy"]["chargeLimitPercent"] = json!(limit);
        profiles
    };
    let (first, second) = (with_limit(60), with_limit(70));

    // Two saves and a status read arrive together on separate connections.
    let (a, b, status) = tokio::join!(
        rig.request(update_request(1, &first)),
        rig.request(update_request(2, &second)),
        rig.get(),
    );

    assert_eq!(a, result(0, STORED, 1));
    assert_eq!(b, result(0, STORED, 2));
    assert!(status["success"].as_bool().unwrap());
    assert_eq!(rig.entered.get(), 2);
    // Each save committed a whole aggregate on top of the other: the result
    // is one of the two sets, never a mixture, with nothing left behind.
    let [profile, stored_preferences, marker] = documents(&files);
    let limit = saved(&files)["batteryPolicy"]["chargeLimitPercent"]
        .as_u64()
        .unwrap();
    assert!(limit == 60 || limit == 70);
    assert_eq!(
        marker,
        preferences::commit_marker(
            27,
            &crate::configuration::digest_hex(&profile),
            &crate::configuration::digest_hex(&stored_preferences),
        )
    );
    assert!(
        !files
            .bytes
            .lock()
            .unwrap()
            .keys()
            .any(|name| name.contains("recovery-required") || name.contains(".transaction-"))
    );
    let (published, active, _) = published_profiles(&rig).await;
    assert!(published >= revision + 2, "each save publishes");
    assert_eq!(active["batteryPolicy"]["chargeLimitPercent"], limit);
    no_hardware(&rig);
    rig.stop().await;
}

#[tokio::test]
async fn installed_update_profiles_waits_for_a_mutation_in_flight() {
    let files = MemoryFiles::default();
    files.profiles(1);
    let rig = Rig::new(files.clone());
    rig.get().await;
    let prior = documents(&files);
    let mut profiles = saved(&files);
    profiles["batteryPolicy"]["chargeLimitPercent"] = json!(65);
    // A thermal pair is submitted and held on its first mailbox write.
    let release = Arc::new(AtomicBool::new(false));
    rig.port.state.lock().unwrap().held_write = Some((1, release.clone()));
    let thermal = rig.set(1, value(1, 1));
    let save = rig.request(update_request(2, &profiles));
    tokio::pin!(thermal, save);
    let held = async {
        while rig.port.state.lock().unwrap().writes != 1 {
            tokio::task::yield_now().await;
        }
    };
    tokio::select! {
        _ = &mut thermal => panic!("the held mutation completed"),
        result = tokio::time::timeout(HANG_GUARD, held) => result.unwrap(),
    }

    // The save neither answers nor stores while that mutation is in flight.
    tokio::select! {
        biased;
        _ = &mut save => panic!("the save overtook a mutation in flight"),
        _ = &mut thermal => panic!("the held mutation completed"),
        _ = tokio::time::sleep(std::time::Duration::from_millis(100)) => {}
    }
    assert_eq!(documents(&files), prior);

    release.store(true, Ordering::Release);
    let (thermal, save) = tokio::join!(thermal, save);
    assert_eq!(thermal["result"]["status"], 1);
    assert_eq!(save, result(0, STORED, 2));
    assert_eq!(saved(&files)["batteryPolicy"]["chargeLimitPercent"], 65);
    // Only the thermal pair reached the mailbox.
    assert_eq!(rig.port.state.lock().unwrap().writes, 2);
    rig.stop().await;
}

#[tokio::test]
async fn installed_update_profiles_answers_a_save_that_finished_after_the_frame_deadline() {
    let files = MemoryFiles::default();
    files.profiles(1);
    let rig = Rig::new(files.clone());
    rig.get().await;
    let mut profiles = saved(&files);
    profiles["batteryPolicy"]["chargeLimitPercent"] = json!(65);
    // The marker, the commit point, is slow to land.
    *files.stalled_replace.lock().unwrap() = Some((
        NAMES[2],
        crate::service_frame::FRAME_TIMEOUT + std::time::Duration::from_millis(150),
    ));

    let reply = rig.request(update_request(1, &profiles)).await;

    // The set is stored, so the caller is told so and sees it published.
    assert_eq!(reply, result(0, STORED, 1));
    assert_eq!(saved(&files)["batteryPolicy"]["chargeLimitPercent"], 65);
    let (_, published, _) = published_profiles(&rig).await;
    assert_eq!(published["batteryPolicy"]["chargeLimitPercent"], 65);
    no_hardware(&rig);
    rig.stop().await;
}

/// Applies a vector's edits: `[path, value]` sets a member, `[path]` removes it.
fn edited(base: &Json, edits: Option<&Json>) -> Json {
    let mut result = base.clone();
    let Some(Json::Array(edits)) = edits else {
        return result;
    };
    for edit in edits {
        let Json::Array(pair) = edit else {
            panic!("malformed edit");
        };
        let Some(Json::Array(path)) = pair.first() else {
            panic!("malformed edit path");
        };
        let Some((Json::String(name), parents)) = path.split_last() else {
            panic!("an edit names a member");
        };
        let mut parent = &mut result;
        for key in parents {
            parent = match (parent, key) {
                (Json::Object(members), Json::String(name)) => {
                    &mut members
                        .iter_mut()
                        .find(|(member, _)| member == name)
                        .unwrap()
                        .1
                }
                (Json::Array(items), Json::Number(index)) => {
                    &mut items[index.parse::<usize>().unwrap()]
                }
                _ => panic!("unreachable edit path"),
            };
        }
        let Json::Object(members) = parent else {
            panic!("an edit targets an object");
        };
        let at = members.iter().position(|(member, _)| member == name);
        match (pair.get(1), at) {
            (Some(value), Some(at)) => members[at].1 = value.clone(),
            (Some(value), None) => members.push((name.clone(), value.clone())),
            (None, Some(at)) => drop(members.remove(at)),
            (None, None) => {}
        }
    }
    result
}

#[tokio::test]
async fn installed_update_profiles_answers_the_managed_vector_corpus() {
    // Every managed vector saved over the safe defaults, through the
    // installed dispatcher with the exact frame bytes: the reply managed
    // gives and the document it stores, or a closed connection where
    // managed sends no result.
    let vectors = profile_set::json::parse(VECTORS.as_bytes()).unwrap();
    let base = vectors.member("base").unwrap();
    let Some(Json::Array(cases)) = vectors.member("cases") else {
        panic!("the vectors hold cases");
    };
    let text = |value: Option<&Json>| value.and_then(Json::text).map(str::to_owned);
    let seed = profile_set::json::write(
        &Json::Object(vec![
            ("schemaVersion".into(), Json::int(27)),
            ("profiles".into(), base.clone()),
        ]),
        true,
    );
    let (mut stored, mut rejected, mut closed) = (0, 0, 0);
    for case in cases {
        let name = text(case.member("name")).unwrap();
        // The domain test covers the cases saved over another set.
        if case.member("saved").is_some() {
            continue;
        }
        let expect = case.member("expect").unwrap();
        let update = expect.member("update");
        let files = MemoryFiles::default();
        files.profiles(1);
        let marker = json!({
            "schemaVersion": 27,
            "profileSha256": crate::configuration::digest_hex(&seed),
            "preferencesSha256": crate::configuration::digest_hex(&files.bytes.lock().unwrap()[NAMES[1]]),
        });
        files
            .bytes
            .lock()
            .unwrap()
            .insert(NAMES[0].into(), seed.clone());
        files.put(NAMES[2], marker);
        let rig = Rig::new(files.clone());
        rig.get().await;
        let prior = files.bytes.lock().unwrap().clone();
        let payload = profile_set::json::write(
            &Json::Object(vec![
                ("operation".into(), Json::string("updateProfiles")),
                ("version".into(), Json::int(91)),
                (
                    "requestId".into(),
                    Json::string("aaaaaaaa-bbbb-cccc-dddd-000000000001"),
                ),
                ("profiles".into(), edited(base, case.member("edit"))),
            ]),
            false,
        );

        let reply = exchange(&rig, &payload).await;

        let answers = expect.member("decode") == Some(&Json::Bool(true))
            && case.member("nativeDifference").is_none()
            && update.and_then(|update| update.member("throws")).is_none();
        if !answers {
            closed += 1;
            assert!(reply.is_empty(), "{name}");
            assert_eq!(*files.bytes.lock().unwrap(), prior, "{name}");
        } else {
            let reply: Value = serde_json::from_slice(&reply[4..]).unwrap();
            let update = update.unwrap();
            let status = update.member("status").unwrap().integer(0, 3).unwrap();
            assert_eq!(
                reply["result"],
                json!({"status": status, "message": text(update.member("message")).unwrap()}),
                "{name}"
            );
            if status == 0 {
                stored += 1;
                assert_eq!(
                    crate::configuration::digest_hex(&files.bytes.lock().unwrap()[NAMES[0]]),
                    text(expect.member("documentSha256")).unwrap(),
                    "{name}"
                );
            } else {
                rejected += 1;
                assert_eq!(*files.bytes.lock().unwrap(), prior, "{name}");
            }
        }
        no_hardware(&rig);
        rig.stop().await;
    }
    assert!(
        stored >= 60 && rejected >= 75 && closed >= 90,
        "{stored} stored, {rejected} rejected, {closed} closed"
    );
}

#[test]
fn this_composition_declares_profile_serving_in_its_compiled_identity() {
    // `native_package` and the managed package reader both refuse this
    // capability, so the image cannot be packaged as the temporary-charge
    // composition it extends until a packaging stage admits it.
    let identity = std::str::from_utf8(&crate::NATIVE_ARTIFACT_IDENTITY).unwrap();
    let fields: Vec<&str> = identity.split('\n').collect();
    assert_eq!(
        (fields[0], fields[2]),
        ("OpenBlade.NativeArtifact.v2", "service")
    );
    assert!(
        fields[5]
            .split(',')
            .any(|feature| feature == "service-profiles-v1")
    );
    // Automatic reconciliation adds its own marker, which both readers
    // refuse by name as well.
    let triggers = if cfg!(feature = "service-profile-triggers-v1") {
        ",service-profile-triggers-v1"
    } else {
        ""
    };
    let expected = format!(
        "private-service-custody-v1,service-mailbox-observation-v1,service-thermal-mutation-v1,service-fan-mutation-v2,service-battery-policy-v1,service-temporary-charge-v1,service-profiles-v1{triggers}"
    );
    // Thermal convergence follows the triggers it implies with its own.
    #[cfg(feature = "service-thermal-convergence-v1")]
    let expected = format!("{expected},service-thermal-convergence-v1");
    // The read-only Driver-mode build appends its own refused marker.
    #[cfg(feature = "service-keyboard-driver-mode-read-v1")]
    let expected = format!("{expected},service-keyboard-driver-mode-read-v1");
    // Driver-mode ownership follows it with its own.
    #[cfg(feature = "service-keyboard-driver-mode-v1")]
    let expected = format!("{expected},service-keyboard-driver-mode-v1");
    // So does the live-reading build, after it.
    #[cfg(feature = "service-live-readings-v1")]
    let expected = format!("{expected},service-live-readings-v1");
    // And the diagnostic export.
    #[cfg(feature = "service-diagnostics-export-v1")]
    let expected = format!("{expected},service-diagnostics-export-v1");
    // Fan Stop with its guardian unavailable.
    #[cfg(feature = "service-fan-stop-v1")]
    let expected = format!("{expected},service-fan-stop-v1");
    // GPU clock tuning, before the shortcut (build.rs).
    #[cfg(feature = "service-gpu-clock-tuning-v1")]
    let expected = format!("{expected},service-gpu-clock-tuning-v1");
    // The performance shortcut appends its own refused marker after them.
    #[cfg(feature = "service-performance-shortcut-v1")]
    let expected = format!("{expected},service-performance-shortcut-v1");
    // And the HyperBoost exit to a battery preset.
    #[cfg(feature = "service-thermal-hyperboost-exit-v1")]
    let expected = format!("{expected},service-thermal-hyperboost-exit-v1");
    // Owned HyperBoost, which implies the exit.
    #[cfg(feature = "service-hyperboost-v1")]
    let expected = format!("{expected},service-hyperboost-v1");
    // Keyboard lighting, last.
    #[cfg(feature = "service-lighting-v1")]
    let expected = format!("{expected},service-lighting-v1");
    assert_eq!(fields[8], expected);
}

/// A profile save that sets a Static keyboard colour, in the member order the
/// Tray sends (`kind` first), leaves the saved profiles resolvable: an
/// ordinary performance request then reaches the paired setters.
#[tokio::test]
async fn installed_update_profiles_with_static_lighting_keeps_thermal_requests_resolvable() {
    let files = MemoryFiles::default();
    files.profiles(1);
    let rig = Rig::new(files.clone());
    rig.get().await;
    let mut profiles = saved(&files);
    profiles["pluggedIn"]["lighting"] = json!("@@LIGHTING@@");
    let rgb = r#"{"red":200,"green":10,"blue":5}"#;
    let lighting = format!(
        r#"{{"effect":1,"color":{rgb},"brightness":10,"parameters":{{"kind":"static","color":{rgb}}},"logoMode":null}}"#
    );
    let text = serde_json::to_string(&update_request(61, &profiles))
        .unwrap()
        .replace(r#""@@LIGHTING@@""#, &lighting);
    let reply: Value = serde_json::from_slice(&exchange(&rig, text.as_bytes()).await[4..]).unwrap();
    assert_eq!(reply, result(0, STORED, 61));
    no_hardware(&rig);
    let stored = String::from_utf8(files.read("profiles.json").unwrap().unwrap()).unwrap();
    assert!(stored.contains(r#""kind": "static""#), "{stored}");
    fresh_observe(&rig).await;
    let outcome = rig.set(1, value(2, 1)).await;
    assert_eq!(outcome["result"]["status"], 1, "{outcome}");
    assert_eq!(rig.port.state.lock().unwrap().writes, 2);
    rig.stop().await;
}

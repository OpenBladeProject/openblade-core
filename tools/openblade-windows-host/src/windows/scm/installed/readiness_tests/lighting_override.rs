//! `setLightingOverride` through the installed dispatcher on exact RZ09-0581,
//! slice L1: managed's store and reply for every gate, the keyboard Off and
//! logo writes with readback, and stop-on-ambiguity.
use super::*;

const OFF: &str = r#"{"effect":0,"color":{"red":0,"green":0,"blue":0},"brightness":0,"parameters":{"kind":"off"},"logoMode":2}"#;
const STATIC: &str = r#"{"effect":1,"color":{"red":200,"green":10,"blue":5},"brightness":10,"parameters":{"kind":"static","color":{"red":200,"green":10,"blue":5}},"logoMode":null}"#;
const STORED: &str = "Lighting override intent was stored. Hardware result: ";
const CONFIRMED: &str = "Lighting setting applied and confirmed by readback.";
const PENDING: &str =
    "Lighting was not written because OpenBlade must confirm the current laptop state first.";

/// The request as managed's client writes it: a `Value` would sort `kind`
/// out of first place in `parameters`, which managed's polymorphic binding
/// refuses.
fn request(n: u8, settings: Option<&str>, lifetime: u8) -> Vec<u8> {
    let value = settings.map_or("null".to_owned(), |settings| {
        format!(r#"{{"value":{settings},"lifetime":{lifetime}}}"#)
    });
    format!(
        r#"{{"operation":"setLightingOverride","version":91,"requestId":"aaaaaaaa-bbbb-cccc-dddd-{n:012}","value":{value}}}"#
    )
    .into_bytes()
}
async fn set(rig: &Rig, n: u8, settings: Option<&str>, lifetime: u8) -> (i64, String) {
    let response = rig
        .request_payload(
            "setLightingOverride",
            request(n, settings, lifetime),
            USER,
            SID,
        )
        .await;
    let result = &response["result"];
    (
        result["status"].as_i64().unwrap(),
        result["message"].as_str().unwrap().to_owned(),
    )
}
fn lit(rig: &Rig, lighting: [u8; 3]) {
    rig.port.state.lock().unwrap().lighting = lighting;
}
/// The lighting reports the job sent, as (command, mutation).
fn sent(rig: &Rig) -> Vec<(u8, bool)> {
    let s = rig.port.state.lock().unwrap();
    s.lighting_sent.iter().map(|s| (s.0, s.2)).collect()
}
fn setters(rig: &Rig) -> usize {
    sent(rig).iter().filter(|s| s.1).count()
}
/// The saved lighting member exactly as written, member order included.
fn saved(rig: &Rig) -> String {
    let bytes = rig.files.read("manual-overrides.json").unwrap().unwrap();
    let text = String::from_utf8(bytes).unwrap();
    let start = text.find(r#""lighting":"#).unwrap() + r#""lighting":"#.len();
    // The member's own bytes: an object keeps its written order.
    let mut depth = 0;
    for (i, c) in text[start..].char_indices() {
        match c {
            '{' => depth += 1,
            '}' => {
                depth -= 1;
                if depth == 0 {
                    return text[start..start + i + 1].to_owned();
                }
            }
            ',' if depth == 0 => return text[start..start + i].to_owned(),
            _ => {}
        }
    }
    panic!("unterminated lighting member: {text}")
}

/// Managed stores the intent and refuses an effect its capability table
/// lacks. Native admits the keyboard Off only until slice L2 adds the lit
/// effects, so every lit effect gets that refusal, with nothing sent.
#[tokio::test]
async fn lit_effects_are_refused_until_l2_interim_divergence() {
    let rig = Rig::new(MemoryFiles::default());
    let (status, message) = set(&rig, 1, Some(STATIC), 1).await;
    assert_eq!(status, 2);
    assert_eq!(
        message,
        format!("{STORED}Lighting value is not supported by the verified capability table.")
    );
    assert!(sent(&rig).is_empty());
    assert_eq!(saved(&rig), STATIC);
    let status = rig.get().await;
    assert_eq!(status["status"]["lightingOverride"]["lifetime"], 1);
    assert_eq!(
        status["status"]["lightingOverride"]["value"]["parameters"]["kind"],
        "static"
    );
    assert_eq!(status["status"]["desiredState"]["lighting"]["source"], 4);
    // Clearing stores the clear; applying profile lighting is slice L2's.
    let (status, message) = set(&rig, 2, None, 0).await;
    assert_eq!(status, 2);
    assert_eq!(
        message,
        "Lighting override was cleared. Hardware result: Lighting value is not supported by the verified capability table."
    );
    assert_eq!(saved(&rig), "null");
    assert!(sent(&rig).is_empty());
    rig.stop().await;
}

/// Off from a lit keyboard: the brightness and logo setters, each read back,
/// then managed's aggregate read, published in the confirmed state a
/// thermal read established.
#[tokio::test]
async fn off_is_written_confirmed_by_readback_and_published() {
    let rig = Rig::new(MemoryFiles::default());
    lit(&rig, [0xc5, 1, 2]);
    // The lighting members join the state a thermal read confirmed.
    assert!(!fresh_observe(&rig).await["status"]["confirmedState"].is_null());
    let (status, message) = set(&rig, 1, Some(OFF), 1).await;
    assert_eq!((status, message.as_str()), (1, CONFIRMED));
    assert_eq!(rig.port.state.lock().unwrap().lighting, [0, 0, 2]);
    assert_eq!(setters(&rig), 2);
    assert_eq!(
        rig.port.state.lock().unwrap().writes,
        0,
        "no thermal setter"
    );
    assert_eq!(saved(&rig), OFF);
    let status = rig.get().await;
    let status = &status["status"];
    assert_eq!(
        status["confirmedState"]["lighting"]["parameters"]["kind"],
        "off"
    );
    assert_eq!(status["confirmedState"]["logoLighting"], 2);
    assert_eq!(status["lightingOverride"]["value"]["logoMode"], 2);
    assert_eq!(status["desiredState"]["lighting"]["source"], 4);
    // Already confirmed: read, nothing written.
    let (status, message) = set(&rig, 2, Some(OFF), 1).await;
    assert_eq!(
        (status, message.as_str()),
        (1, "Lighting is already confirmed.")
    );
    assert_eq!(setters(&rig), 2);
    rig.stop().await;
}

/// A temporary override is held in memory with its power class and the
/// saved one is cleared, as managed's `SetLightingOverrideCoreAsync` does.
#[tokio::test]
async fn a_temporary_override_clears_the_saved_one() {
    let rig = Rig::new(MemoryFiles::default());
    assert_eq!(set(&rig, 1, Some(OFF), 1).await.0, 1);
    assert_eq!(saved(&rig), OFF);
    assert_eq!(set(&rig, 2, Some(OFF), 0).await.0, 1);
    assert_eq!(saved(&rig), "null");
    let status = rig.get().await;
    assert_eq!(status["status"]["lightingOverride"]["lifetime"], 0);
    assert_eq!(status["status"]["desiredState"]["lighting"]["source"], 3);
    rig.stop().await;
}

/// Without fresh admission nothing is stored, read or written.
#[tokio::test]
async fn refused_admission_stores_and_sends_nothing() {
    for refusal in ["competing controller", "fresh admission"] {
        let rig = Rig::new(MemoryFiles::default());
        lit(&rig, [0xc5, 1, 0]);
        match refusal {
            "competing controller" => rig.custody.controller.store(true, Ordering::Release),
            _ => rig.custody.fresh_refuse.store(true, Ordering::Release),
        }
        let (status, message) = set(&rig, 1, Some(OFF), 1).await;
        assert_eq!(status, 2, "{refusal}: {message}");
        assert!(!message.starts_with(STORED), "{refusal}: {message}");
        assert!(sent(&rig).is_empty(), "{refusal}");
        assert!(
            rig.files.read("manual-overrides.json").unwrap().is_none(),
            "{refusal}"
        );
        assert_eq!(rig.port.state.lock().unwrap().lighting, [0xc5, 1, 0]);
        rig.stop().await;
    }
}

/// A setter that may have been submitted unconfirmed stops the request; the
/// next one resolves it read-only with managed's pending refusal, and only
/// then may a request write again. Nothing is replayed.
#[tokio::test]
async fn an_ambiguous_submission_latches_until_a_read_only_resolution() {
    let rig = Rig::new(MemoryFiles::default());
    lit(&rig, [0xc5, 1, 0]);
    rig.port.state.lock().unwrap().lighting_timeout = Some(1);
    let (status, message) = set(&rig, 1, Some(OFF), 1).await;
    assert_eq!(status, 3);
    assert_eq!(
        message,
        format!(
            "{STORED}Lighting write is indeterminate: A brightness command may have been submitted and was not confirmed."
        )
    );
    assert_eq!(setters(&rig), 1);
    let (status, message) = set(&rig, 2, Some(OFF), 1).await;
    assert_eq!((status, message.as_str()), (2, PENDING));
    assert_eq!(setters(&rig), 1, "the resolution writes nothing");
    // The timed-out setter applied: the next request finds the keyboard Off
    // and writes only the logo.
    let (status, message) = set(&rig, 3, Some(OFF), 1).await;
    assert_eq!((status, message.as_str()), (1, CONFIRMED));
    assert_eq!(setters(&rig), 2);
    assert_eq!(rig.port.state.lock().unwrap().lighting, [0, 0, 0]);
    rig.stop().await;
}

/// A readback that confirms another value restores the confirmed baseline
/// and leaves nothing in doubt.
#[tokio::test]
async fn a_readback_mismatch_restores_and_is_not_latched() {
    let rig = Rig::new(MemoryFiles::default());
    lit(&rig, [0xc5, 1, 0]);
    rig.port.state.lock().unwrap().lighting_ignore = vec![1];
    let (status, message) = set(&rig, 1, Some(OFF), 1).await;
    assert_eq!(status, 3);
    assert_eq!(
        message,
        format!(
            "{STORED}Lighting write is indeterminate: Lighting brightness did not match independent readback."
        )
    );
    assert_eq!(setters(&rig), 2, "the setter and its restore");
    assert_eq!(rig.port.state.lock().unwrap().lighting, [0xc5, 1, 0]);
    let (status, message) = set(&rig, 2, Some(OFF), 1).await;
    assert_eq!((status, message.as_str()), (1, CONFIRMED));
    rig.stop().await;
}

/// A store that fails is managed's rejected intent; nothing is written.
#[tokio::test]
async fn a_persistence_failure_is_rejected_intent_and_writes_nothing() {
    let rig = Rig::new(MemoryFiles::default());
    lit(&rig, [0xc5, 1, 0]);
    rig.files.fail.store(true, Ordering::Release);
    let (status, message) = set(&rig, 1, Some(OFF), 1).await;
    assert_eq!(status, 3);
    assert!(
        message.starts_with("The requested intent was rejected: "),
        "{message}"
    );
    assert!(sent(&rig).is_empty());
    assert_eq!(rig.port.state.lock().unwrap().lighting, [0xc5, 1, 0]);
    rig.stop().await;
}

/// The saved override survives a restart in managed's bytes, and the store
/// loads it as the persistent override; a restart writes no lighting
/// (reconciliation is slice L2's).
#[tokio::test]
async fn a_restart_keeps_the_saved_intent_and_writes_nothing() {
    let files = MemoryFiles::default();
    let rig = Rig::new(files.clone());
    assert_eq!(set(&rig, 1, Some(OFF), 1).await.0, 1);
    rig.stop().await;
    let rig = Rig::restarted(files.clone());
    lit(&rig, [0xc5, 1, 0]);
    assert_eq!(rig.get().await["success"], true);
    assert!(sent(&rig).is_empty());
    assert_eq!(saved(&rig), OFF);
    let storage = Storage::open(Box::new(files), SID).unwrap();
    let saved = storage.current_lighting_override(1).unwrap().unwrap();
    assert_eq!(saved.lifetime, 1);
    assert_eq!(saved.settings.logo_mode.map(|mode| mode.0), Some(2));
    // The next request projects it in status, as every saved override is.
    let (status, message) = set(&rig, 2, Some(OFF), 1).await;
    assert_eq!((status, message.as_str()), (1, CONFIRMED));
    let status = rig.get().await;
    assert_eq!(status["status"]["lightingOverride"]["lifetime"], 1);
    assert_eq!(status["status"]["desiredState"]["lighting"]["source"], 4);
    rig.stop().await;
}

/// Lighting reads no adapter, so on AC it needs managed's unclassified
/// admission: fresh Windows power that agrees with status. An unknown or
/// disagreeing read is no class, and nothing is stored or sent.
#[tokio::test]
async fn power_windows_cannot_confirm_blocks_before_any_store() {
    for windows in [None, Some(true)] {
        let rig = Rig::new(MemoryFiles::default());
        lit(&rig, [0xc5, 1, 0]);
        rig.port.state.lock().unwrap().windows_on_battery = Some(windows);
        let (status, message) = set(&rig, 1, Some(OFF), 1).await;
        assert_eq!(
            (status, message.as_str()),
            (
                2,
                "Hardware application was not confirmed. The external power adapter could not be classified; overrides and control writes are blocked."
            ),
            "{windows:?}"
        );
        assert!(sent(&rig).is_empty(), "{windows:?}");
        assert!(rig.files.read("manual-overrides.json").unwrap().is_none());
        rig.stop().await;
    }
}

/// The compiled identity carries the refused marker, so no package reader
/// takes this image for the composition it extends.
#[test]
fn the_compiled_identity_carries_the_refused_lighting_marker() {
    let identity = std::str::from_utf8(&crate::NATIVE_ARTIFACT_IDENTITY).unwrap();
    let fields: Vec<&str> = identity.split('\n').collect();
    assert_eq!(fields[2], "service");
    assert!(fields[8].ends_with(",service-lighting-v1"), "{}", fields[8]);
    assert!(
        crate::native_package::startup_policy_for_tests(&crate::NATIVE_ARTIFACT_IDENTITY).is_err()
    );
}

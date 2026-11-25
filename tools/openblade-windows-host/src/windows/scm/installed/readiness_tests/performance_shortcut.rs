//! The performance shortcut and its preview through the installed
//! dispatcher. A shortcut is an explicit performance request whose override
//! `RequestPermit::resolve` stages from the fresh 07/8C class and 0D/82
//! reading (managed `CyclePerformanceShortcutAsync`), so it keeps every
//! admission, journal, readback and stop-on-ambiguity path of
//! `setPerformanceOverride`. The preview stores no intent and reaches no
//! firmware (managed `SetPerformanceShortcutPreviewAsync`).
use super::*;

/// Full adapter, limited adapter, battery and an unclassified adapter
/// (`actual` levels the fixture reads).
const FULL: u8 = 15;
const LIMITED: u8 = 10;
const BATTERY: u8 = 0;
const UNCLASSIFIED: u8 = 0x10;
const PENDING: &str = "A thermal command may have been submitted. Read-only reconciliation is required and no mutation will be replayed.";
const UNCLASSIFIED_BLOCKED: &str = "Hardware application was not confirmed. The external power adapter could not be classified; the performance shortcut is blocked.";
const STATE_UNAVAILABLE: &str =
    "The current thermal state is unavailable for ordinary performance cycling.";
const COMBINATION_REJECTED: &str = "The requested intent was rejected: The performance and fan combination is not supported by the active validation policy.";
const SESSION: &str = "0f0e0d0c-0b0a-4908-8706-050403020100";
const OTHER_SESSION: &str = "1f0e0d0c-0b0a-4908-8706-050403020100";
const INTERACTIVE_REQUIRED: &str = "The active interactive Windows user is required.";

fn rows(rig: &Rig) -> Vec<Value> {
    rig.files
        .read("thermal-mutation.journal")
        .unwrap()
        .unwrap_or_default()
        .split(|b| *b == b'\n')
        .filter(|line| !line.is_empty())
        .map(|line| serde_json::from_slice(line).unwrap())
        .collect()
}
fn setters(rig: &Rig) -> Vec<Vec<u8>> {
    rig.port
        .state
        .lock()
        .unwrap()
        .sent
        .iter()
        .filter(|(class, command, _, _)| (*class, *command) == (13, 2))
        .map(|(_, _, payload, _)| payload.clone())
        .collect()
}
fn sent(rig: &Rig) -> usize {
    rig.port.state.lock().unwrap().sent.len()
}
fn rig_at(selector: u8, fans: u8, actual: u8, files: MemoryFiles) -> Rig {
    let rig = Rig::new(files);
    {
        let mut s = rig.port.state.lock().unwrap();
        s.selectors = [selector; 2];
        s.fans = [fans; 2];
        s.actual = actual;
        // Automatic Custom decodes only a reviewed level pair (not High/High).
        if (selector, fans) == (4, 0) {
            s.custom_levels = [1, 0];
        }
    }
    if actual == BATTERY {
        rig.metadata.store(1, Ordering::Release);
    }
    rig
}
/// A saved persistent performance override, which the cycle starts from.
fn saved(performance: i32, fans: Option<(i32, Value)>) -> MemoryFiles {
    let files = MemoryFiles::default();
    let custom = (performance == CUSTOM)
        .then(|| json!({"cpu":1,"gpu":0,"maximumFans":false,"gpuClockOffsets":null}));
    let (fans, fan_control) = fans.unwrap_or((0, Value::Null));
    let fans = (fan_control != Value::Null).then_some(fans);
    files.put("manual-overrides.json",json!({"schemaVersion":8,"overrides":{"performance":performance,"fans":fans,"fanControl":fan_control,"lighting":null,"customPerformance":custom,"gpuClockTuning":null}}));
    files
}
fn manual_curve() -> (i32, Value) {
    (
        2,
        json!({"mode":2,"fixedSpeedRpm":3700,"manualCurve":{"cpu":[{"temperatureCelsius":30,"speedRpm":2000},{"temperatureCelsius":90,"speedRpm":5400}],"gpu":[{"temperatureCelsius":30,"speedRpm":2000},{"temperatureCelsius":90,"speedRpm":5400}],"useHigherTemperatureForBoth":true}}),
    )
}
const CUSTOM: i32 = 5;
/// The firmware selector of each preset this model's 0D/02 pair writes.
fn selector(preset: i32) -> u8 {
    match preset {
        0 => 5,
        1 => 0,
        2 => 2,
        3 => 3,
        4 => 6,
        CUSTOM => 4,
        _ => unreachable!("{preset}"),
    }
}
impl Rig {
    async fn cycle(&self, n: u8, source: i32) -> Value {
        let response = self.request(json!({"operation":"cyclePerformanceShortcut","version":90,"requestId":format!("cccccccc-bbbb-cccc-dddd-{n:012}"),"source":source})).await;
        assert!(response["status"].is_null(), "{response}");
        assert!(!response["result"].is_null(), "{response}");
        response
    }
    async fn preview(&self, n: u8, session: &str, generation: i64, preset: Option<i32>) -> Value {
        self.preview_as(n, session, generation, preset, USER).await
    }
    async fn preview_as(
        &self,
        n: u8,
        session: &str,
        generation: i64,
        preset: Option<i32>,
        caller: Caller,
    ) -> Value {
        self.request_as(json!({"operation":"setPerformanceShortcutPreview","version":90,"requestId":format!("dddddddd-bbbb-cccc-dddd-{n:012}"),"sessionId":session,"generation":generation,"preset":preset}), caller, SID).await
    }
}

#[tokio::test]
async fn each_class_cycles_to_the_next_preset_through_its_reviewed_pair() {
    // (adapter, saved persistent preset, next): the classified cycles start
    // from the saved intent, Battery from its own two presets.
    for (actual, current, next) in [
        (FULL, 1, 0),
        (FULL, 0, 2),
        (FULL, 2, 5),
        (LIMITED, CUSTOM, 4),
        (LIMITED, 4, 3),
        (FULL, 3, 1),
        (BATTERY, 4, 3),
        (BATTERY, 3, 4),
    ] {
        for source in [0, 1] {
            // Confirmed at the saved preset with Automatic fans, so every
            // case writes its own pair.
            let rig = rig_at(selector(current), 0, actual, saved(current, None));
            let result = rig.cycle(1, source).await;
            let case = format!("{actual} {current} -> {next}: {result}");
            let name = openblade_domain::profile_set::PerformancePreset(next)
                .name()
                .unwrap();
            assert_eq!(result["result"]["status"], 1, "{case}");
            assert_eq!(
                result["result"]["message"],
                if source == 0 {
                    format!("The performance button selected {name}.")
                } else {
                    format!("Fn+P selected {name}.")
                },
                "{case}"
            );
            let selector = selector(next);
            assert_eq!(
                setters(&rig),
                [vec![1, 1, selector, 0], vec![1, 2, selector, 0]],
                "{case}"
            );
            // Journaled as the staged UntilPowerSourceChange override.
            let rows = rows(&rig);
            assert!(!rows.is_empty(), "{case}");
            assert!(
                rows.iter().all(|r| r["requested"]["value"] == next),
                "{case}"
            );
            assert!(
                rows.iter().all(|r| r["requested"]["lifetime"] == 0),
                "{case}"
            );
            let status = rig.get().await;
            assert_eq!(status["status"]["confirmedState"]["performance"], next);
            assert_eq!(status["status"]["performanceOverride"]["value"], next);
            assert_eq!(status["status"]["performanceOverride"]["lifetime"], 0);
            // Managed keeps one performance override: the temporary replaces
            // the persistent one, which the saved document no longer holds.
            assert!(
                rig.files.document("manual-overrides.json")["overrides"]["performance"].is_null(),
                "{case}"
            );
            rig.stop().await;
        }
    }
}

#[tokio::test]
async fn without_saved_profiles_the_cycle_starts_from_the_safe_default_preset() {
    // Managed resolves `ProfileDefaults.CreateSafeDefaults` when no profiles
    // are stored: Balanced on either adapter, so the first press selects
    // Silent and the next one Performance. On battery its Battery Saver
    // selects Battery Balanced.
    for actual in [FULL, LIMITED] {
        let rig = rig_at(selector(1), 0, actual, MemoryFiles::default());
        let result = rig.cycle(1, 1).await;
        assert_eq!(
            result["result"],
            json!({"status":1,"message":"Fn+P selected Silent."}),
            "{actual}: {result}"
        );
        assert_eq!(
            setters(&rig),
            [vec![1, 1, 5, 0], vec![1, 2, 5, 0]],
            "{actual}"
        );
        let result = rig.cycle(2, 1).await;
        assert_eq!(
            result["result"],
            json!({"status":1,"message":"Fn+P selected Performance."}),
            "{actual}: {result}"
        );
        assert_eq!(
            setters(&rig)[2..],
            [vec![1, 1, 2, 0], vec![1, 2, 2, 0]],
            "{actual}"
        );
        rig.stop().await;
    }
    let rig = rig_at(selector(3), 0, BATTERY, MemoryFiles::default());
    let result = rig.cycle(1, 0).await;
    assert_eq!(
        result["result"],
        json!({"status":1,"message":"The performance button selected BatteryBalanced."}),
        "{result}"
    );
    assert_eq!(setters(&rig), [vec![1, 1, 6, 0], vec![1, 2, 6, 0]]);
    rig.stop().await;
}

#[tokio::test]
async fn an_ineligible_resolved_preset_starts_the_cycle_from_its_first_preset() {
    // The foreground application's battery override names Balanced, which
    // the battery class does not select, over a Battery Balanced power
    // profile. Managed cycles from that resolved preset, which is outside
    // the battery cycle, so the cycle starts from its first preset. The
    // power profile's own preset is not a fallback while a resolution was
    // read: it would select Battery Saver.
    let files = MemoryFiles::default();
    files.profiles(1);
    let mut aggregate = files.document("profiles.json");
    aggregate["profiles"]["battery"]["performance"] = json!(4);
    aggregate["profiles"]["applicationProfiles"][0]["overrides"] =
        json!({"battery":{"performance":1}});
    files.put("profiles.json", aggregate);
    files.commit_configuration();
    let rig = rig_at(selector(3), 0, BATTERY, files);
    report_game(&rig).await;
    let result = rig.cycle(1, 1).await;
    assert_eq!(
        result["result"],
        json!({"status":1,"message":"Fn+P selected BatteryBalanced."}),
        "{result}"
    );
    assert_eq!(setters(&rig), [vec![1, 1, 6, 0], vec![1, 2, 6, 0]]);
    rig.stop().await;
}

#[tokio::test]
async fn windows_battery_with_an_external_reading_is_refused_as_an_explicit_request_is() {
    // Managed cycles under its Battery power source whenever Windows reports
    // battery. Native admits a performance request there only with a battery
    // reading (`Power::thermal_source`), so an external 07/8C class is
    // refused when the worker admits that reading, before anything is staged,
    // exactly as the explicit temporary Battery Saver the cycle would select
    // is: the same 0D/82 and 07/8C queries are read, and nothing is stored or
    // written.
    let mut outcomes = Vec::new();
    for shortcut in [true, false] {
        let rig = rig_at(selector(1), 0, FULL, saved(4, None));
        rig.port.state.lock().unwrap().windows_on_battery = Some(Some(true));
        let saved = rig.files.read("manual-overrides.json").unwrap();
        let result = if shortcut {
            rig.cycle(1, 1).await
        } else {
            rig.set(1, value(3, 0)).await
        };
        assert_eq!(result["result"]["status"], 2, "{result}");
        assert!(setters(&rig).is_empty());
        assert!(rows(&rig).is_empty());
        assert_eq!(rig.files.read("manual-overrides.json").unwrap(), saved);
        let queries: Vec<(u8, u8, Vec<u8>)> = rig
            .port
            .state
            .lock()
            .unwrap()
            .sent
            .iter()
            .map(|(class, command, payload, _)| (*class, *command, payload.clone()))
            .collect();
        outcomes.push((result["result"].clone(), queries));
        rig.stop().await;
    }
    assert_eq!(outcomes[0], outcomes[1]);
}

#[tokio::test]
async fn a_staged_custom_carries_the_profile_or_default_levels() {
    // Managed stages the power profile's Custom settings, or its default.
    let rig = rig_at(2, 0, FULL, saved(2, None));
    let result = rig.cycle(1, 1).await;
    assert_eq!(result["result"]["status"], 1, "{result}");
    assert_eq!(result["result"]["message"], "Fn+P selected Custom.");
    let requested = &rows(&rig)[0]["requested"];
    assert_eq!(requested["value"], CUSTOM);
    assert_eq!(
        requested["customPerformance"],
        json!({"cpu":1,"gpu":2,"maximumFans":false,"gpuClockOffsets":null})
    );
    rig.stop().await;
}

#[tokio::test]
async fn an_unclassified_adapter_cycles_from_the_confirmed_preset() {
    // Managed cycles the ordinary presets from the confirmed state there, and
    // stages no GPU tuning.
    for (from, fans, next) in [(0, 0, 0), (5, 0, 2), (2, 0, CUSTOM), (4, 0, 1)] {
        let rig = rig_at(from, fans, UNCLASSIFIED, MemoryFiles::default());
        let result = rig.cycle(1, 1).await;
        let case = format!("({from}, {fans}) -> {next}: {result}");
        assert_eq!(result["result"]["status"], 1, "{case}");
        let selector = selector(next);
        assert_eq!(
            setters(&rig)[..2],
            [vec![1, 1, selector, 0], vec![1, 2, selector, 0]],
            "{case}"
        );
        let requested = &rows(&rig)[0]["requested"];
        assert_eq!(requested["value"], next, "{case}");
        assert!(requested["gpuClockTuning"].is_null(), "{case}");
        if next == CUSTOM {
            assert!(requested["customPerformance"]["gpuClockOffsets"].is_null());
        }
        rig.stop().await;
    }
    // HyperBoost admits no ordinary control there, and a fixed maximum
    // confirms no preset to cycle from: nothing is stored or written.
    for (from, fans, message) in [(7, 0, UNCLASSIFIED_BLOCKED), (0, 1, STATE_UNAVAILABLE)] {
        let rig = rig_at(from, fans, UNCLASSIFIED, MemoryFiles::default());
        let result = rig.cycle(1, 0).await;
        assert_eq!(result["result"]["status"], 2, "{result}");
        assert_eq!(result["result"]["message"], message);
        assert!(setters(&rig).is_empty());
        assert!(rows(&rig).is_empty());
        assert!(rig.files.read("manual-overrides.json").unwrap().is_none());
        rig.stop().await;
    }
}

#[tokio::test]
async fn refused_admission_stores_nothing() {
    // A battery reading under Windows AC or unknown power is managed's
    // ExternalUnknown, which this adapter cannot control.
    for windows in [Some(false), None] {
        let rig = rig_at(3, 0, BATTERY, saved(4, None));
        rig.port.state.lock().unwrap().windows_on_battery = Some(windows);
        let saved = rig.files.read("manual-overrides.json").unwrap();
        let result = rig.cycle(1, 1).await;
        assert_eq!(result["result"]["status"], 2, "{result}");
        assert_eq!(result["result"]["message"], UNCLASSIFIED_BLOCKED);
        assert!(setters(&rig).is_empty());
        assert!(rows(&rig).is_empty());
        assert_eq!(rig.files.read("manual-overrides.json").unwrap(), saved);
        rig.stop().await;
    }
    // A caller that is not the interactive user reads nothing.
    let rig = rig_at(2, 0, FULL, MemoryFiles::default());
    let before = sent(&rig);
    let result = rig.request_as(json!({"operation":"cyclePerformanceShortcut","version":90,"requestId":"cccccccc-bbbb-cccc-dddd-000000000001","source":1}), SYSTEM, SID).await;
    assert_eq!(
        result["result"],
        json!({"status":2,"message":INTERACTIVE_REQUIRED}),
        "{result}"
    );
    assert_eq!(sent(&rig), before);
    assert!(rows(&rig).is_empty());
    rig.stop().await;
}

#[tokio::test]
async fn an_unsupported_combination_is_rejected_before_storing() {
    // The cycle reaches Battery Balanced from Custom, which managed admits
    // with no manual curve (`ValidateThermalOverrideCombination`).
    let rig = rig_at(2, 1, FULL, saved(CUSTOM, Some(manual_curve())));
    let saved = rig.files.read("manual-overrides.json").unwrap();
    let result = rig.cycle(1, 1).await;
    assert_eq!(result["result"]["status"], 3, "{result}");
    assert_eq!(result["result"]["message"], COMBINATION_REJECTED);
    assert!(setters(&rig).is_empty());
    assert!(rows(&rig).is_empty());
    assert_eq!(rig.files.read("manual-overrides.json").unwrap(), saved);
    rig.stop().await;
}

#[tokio::test]
async fn saved_profiles_the_domain_decoder_refuses_are_refused_before_the_combination() {
    // The profile layer is read before any override is checked, so saved
    // profiles with a member the domain decoder does not know refuse the
    // request (Blocked) before the unsupported combination would (Failed),
    // for the shortcut and the explicit request alike. Nothing is stored or
    // written.
    for shortcut in [true, false] {
        let files = saved(CUSTOM, Some(manual_curve()));
        files.profiles(1);
        let mut aggregate = files.document("profiles.json");
        aggregate["profiles"]["pluggedIn"]["unknownMember"] = json!(1);
        files.put("profiles.json", aggregate);
        files.commit_configuration();
        let rig = rig_at(2, 1, FULL, files);
        let saved = rig.files.read("manual-overrides.json").unwrap();
        let result = if shortcut {
            rig.cycle(1, 1).await
        } else {
            rig.set(1, value(4, 0)).await
        };
        assert_eq!(result["result"]["status"], 2, "{shortcut}: {result}");
        assert!(setters(&rig).is_empty());
        assert!(rows(&rig).is_empty());
        assert_eq!(rig.files.read("manual-overrides.json").unwrap(), saved);
        rig.stop().await;
    }
}

#[tokio::test]
async fn manual_fans_store_the_selection_for_the_fan_return() {
    let rig = rig_at(2, 1, FULL, saved(1, Some(manual_curve())));
    let result = rig.cycle(1, 1).await;
    assert_eq!(result["result"]["status"], 0, "{result}");
    assert_eq!(
        result["result"]["message"],
        "Fn+P selected Silent. It will be applied when fans return to Automatic."
    );
    assert!(setters(&rig).is_empty());
    assert!(rows(&rig).is_empty());
    let status = rig.get().await;
    assert_eq!(status["status"]["performanceOverride"]["value"], 0);
    assert_eq!(status["status"]["performanceOverride"]["lifetime"], 0);
    rig.stop().await;
}

#[tokio::test]
async fn an_uncertain_submission_fences_and_is_never_replayed() {
    let rig = rig_at(2, 0, FULL, saved(1, None));
    rig.port.state.lock().unwrap().fail_write = Some(1);
    let result = rig.cycle(1, 1).await;
    assert_eq!(result["result"]["status"], 2, "{result}");
    assert_eq!(rig.port.state.lock().unwrap().writes, 1);
    let status = rig.get().await;
    assert_eq!(status["status"]["writesBlocked"], true);
    let restarted = Storage::open(Box::new(rig.files.clone()), SID).unwrap();
    assert!(restarted.pending());
    // A second press is refused at the fence; nothing is resubmitted.
    let again = rig.cycle(2, 1).await;
    assert_eq!(again["result"]["status"], 2, "{again}");
    assert_eq!(rig.port.state.lock().unwrap().writes, 1);
    rig.stop().await;
}

#[tokio::test]
async fn a_readback_mismatch_reports_no_application() {
    let rig = rig_at(2, 0, FULL, saved(1, None));
    rig.port.state.lock().unwrap().mismatch = true;
    let result = rig.cycle(1, 1).await;
    assert_eq!(result["result"]["status"], 2, "{result}");
    assert!(
        !result["result"]["message"]
            .as_str()
            .unwrap()
            .contains("selected"),
        "{result}"
    );
    assert_eq!(rig.get().await["status"]["writesBlocked"], true);
    rig.stop().await;
}

#[tokio::test]
async fn a_power_change_between_setters_stops_the_pair_without_replay() {
    // The staged override lasts until the power source changes, so full to
    // battery, and full to limited, both retire it after the first setter:
    // the pair stops with its fence, exactly as an explicit request for the
    // same temporary Silent does, and a later press replays nothing.
    for changed in [BATTERY, LIMITED] {
        let mut outcomes = Vec::new();
        for shortcut in [true, false] {
            let rig = rig_at(2, 0, FULL, saved(1, None));
            rig.port.state.lock().unwrap().change_power = Some((3, changed));
            let result = if shortcut {
                rig.cycle(1, 1).await
            } else {
                rig.set(1, value(0, 0)).await
            };
            let progress = Storage::open(Box::new(rig.files.clone()), SID)
                .unwrap()
                .progress();
            assert_eq!(result["result"]["status"], 2, "{changed}: {result}");
            assert_eq!(result["result"]["message"], PENDING);
            assert_eq!(setters(&rig), [vec![1, 1, 5, 0]]);
            assert_eq!(progress, Some(2));
            outcomes.push((result["result"].clone(), rows(&rig).len()));
            rig.port.state.lock().unwrap().change_power = None;
            if shortcut {
                assert_eq!(rig.cycle(2, 1).await["result"]["status"], 2);
                assert_eq!(setters(&rig).len(), 1);
            }
            rig.stop().await;
        }
        assert_eq!(outcomes[0], outcomes[1], "{changed}");
    }
}

#[cfg(feature = "service-profile-triggers-v1")]
#[tokio::test]
async fn the_low_power_gate_stores_the_battery_selection_without_reading() {
    use crate::installed::readiness::thermal_mutation::LOW_POWER_PERFORMANCE;
    use windows_sys::Win32::System::Services::SERVICE_CONTROL_POWEREVENT as POWER;
    const SUSPEND: u32 = 4;
    let rig = rig_at(3, 0, BATTERY, saved(4, None));
    // The first request admits the session's storage capture.
    assert_eq!(rig.get().await["status"]["activePowerSource"], 1);
    let custody = rig.custody.clone();
    rig.runtime
        .read_low_power(move || custody.lifecycle.low_power());
    rig.custody.scoped_fresh.store(true, Ordering::Release);
    rig.custody.lifecycle.ingress(POWER, SUSPEND);
    assert!(rig.custody.lifecycle.low_power_or_unknown());
    let before = sent(&rig);
    let result = rig.cycle(1, 1).await;
    assert_eq!(
        result["result"],
        json!({"status":2,"message":format!("Performance override intent was stored. Hardware result: {LOW_POWER_PERFORMANCE}")}),
        "{result}"
    );
    assert_eq!(sent(&rig), before);
    assert_eq!(rig.port.state.lock().unwrap().writes, 0);
    rig.custody.scoped_fresh.store(false, Ordering::Release);
    rig.stop().await;
}

#[tokio::test]
async fn the_preview_follows_the_managed_generation_rules_without_firmware() {
    let rig = rig_at(2, 0, FULL, MemoryFiles::default());
    let before = sent(&rig);
    let preview = |status: &Value| status["status"]["performanceShortcutPreview"].clone();
    let published = json!({"status":0,"message":"Performance shortcut preview was published."});
    let newer =
        json!({"status":0,"message":"A newer performance shortcut preview is already active."});
    assert!(preview(&rig.get().await).is_null());
    assert_eq!(
        rig.preview(1, SESSION, 2, Some(0)).await["result"],
        published
    );
    let shown = json!({"sessionId":SESSION,"generation":2,"preset":0});
    assert_eq!(preview(&rig.get().await), shown);
    // An older generation of the same session is refused; another session
    // replaces it whatever its generation.
    assert_eq!(rig.preview(2, SESSION, 1, Some(2)).await["result"], newer);
    assert_eq!(preview(&rig.get().await), shown);
    // A clear of another generation or session leaves it.
    assert_eq!(rig.preview(3, SESSION, 3, None).await["result"], newer);
    assert_eq!(
        rig.preview(4, OTHER_SESSION, 2, None).await["result"],
        newer
    );
    assert_eq!(preview(&rig.get().await), shown);
    // The same session's GUID in another case is the same session.
    let upper = SESSION.to_ascii_uppercase();
    assert_eq!(
        rig.preview(5, &upper, 2, Some(2)).await["result"],
        published
    );
    assert_eq!(
        preview(&rig.get().await),
        json!({"sessionId":SESSION,"generation":2,"preset":2})
    );
    assert_eq!(
        rig.preview(6, OTHER_SESSION, 1, Some(CUSTOM)).await["result"],
        published
    );
    // Only the active client sees it.
    let inactive = rig
        .request_as(
            json!({"operation":"getStatus","version":90,"requestId":"bbbbbbbb-cccc-dddd-eeee-000000000098"}),
            SETUP,
            SID,
        )
        .await;
    assert_eq!(inactive["status"]["controlAccess"], 1, "{inactive}");
    assert!(preview(&inactive).is_null(), "{inactive}");
    assert_eq!(
        rig.preview(7, OTHER_SESSION, 1, None).await["result"],
        json!({"status":0,"message":"Performance shortcut preview was cleared."})
    );
    assert!(preview(&rig.get().await).is_null());
    // A caller that is not the interactive user publishes nothing.
    let refused = rig.preview_as(8, SESSION, 9, Some(1), SYSTEM).await;
    assert_eq!(
        refused["result"],
        json!({"status":2,"message":INTERACTIVE_REQUIRED}),
        "{refused}"
    );
    assert!(preview(&rig.get().await).is_null());
    // No preview reads or writes firmware, or stores intent.
    assert_eq!(sent(&rig), before);
    assert!(rows(&rig).is_empty());
    assert!(rig.files.read("manual-overrides.json").unwrap().is_none());
    rig.stop().await;
}

#[tokio::test]
async fn an_undefined_shortcut_request_is_refused_by_the_codec() {
    let rig = rig_at(2, 0, FULL, MemoryFiles::default());
    let before = sent(&rig);
    for request in [
        json!({"operation":"cyclePerformanceShortcut","version":90,"requestId":"cccccccc-bbbb-cccc-dddd-000000000001","source":2}),
        json!({"operation":"setPerformanceShortcutPreview","version":90,"requestId":"cccccccc-bbbb-cccc-dddd-000000000002","sessionId":"00000000-0000-0000-0000-000000000000","generation":1,"preset":0}),
        json!({"operation":"setPerformanceShortcutPreview","version":90,"requestId":"cccccccc-bbbb-cccc-dddd-000000000003","sessionId":SESSION,"generation":0,"preset":0}),
        json!({"operation":"setPerformanceShortcutPreview","version":90,"requestId":"cccccccc-bbbb-cccc-dddd-000000000004","sessionId":SESSION,"generation":1,"preset":10}),
        // `preset` is a required member, null included.
        json!({"operation":"setPerformanceShortcutPreview","version":90,"requestId":"cccccccc-bbbb-cccc-dddd-000000000005","sessionId":SESSION,"generation":1}),
    ] {
        assert!(
            StatusRequest::decode(&serde_json::to_vec(&request).unwrap()).is_err(),
            "{request}"
        );
    }
    assert_eq!(sent(&rig), before);
    rig.stop().await;
}

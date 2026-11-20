//! Silent, Battery Saver and Battery Balanced through the installed dispatcher:
//! each is its own reviewed RZ09-0581 0D/02 pair (managed
//! `ProductionBatteryPort.SetPerformanceAsync`), written directly from every
//! confirmed state with the destination's own power-class rule.
use super::*;

/// (wire value, firmware selector) of the three destinations this slice adds.
const SILENT: (i32, u8) = (0, 5);
const BATTERY_SAVER: (i32, u8) = (3, 3);
const BATTERY_BALANCED: (i32, u8) = (4, 6);
/// Full adapter, limited adapter and battery (`actual` levels the fixture reads).
const FULL: u8 = 15;
const LIMITED: u8 = 10;
const BATTERY: u8 = 0;
/// Every confirmed starting state the exact getter decodes, as (selector, fans).
const STATES: [(u8, u8); 13] = [
    (0, 0),
    (2, 0),
    (5, 0),
    (3, 0),
    (6, 0),
    (5, 1),
    (2, 1),
    (3, 1),
    (6, 1),
    (0, 1),
    (7, 0),
    (4, 0),
    (4, 1),
];
const NO_DIRECT_PATH: &str = "Performance override intent was stored. Hardware result: Leaving HyperBoost for a battery preset needs the HyperBoost restore sequence, which this build does not submit; no thermal setter was submitted.";
const PENDING: &str = "A thermal command may have been submitted. Read-only reconciliation is required and no mutation will be replayed.";
const RESUBMIT: &str = "Performance intent stored. Performance is saved, not applied. Manual fan intent is preserved. Submit a new performance request after selecting Automatic fans.";

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
    // Coarse status metadata agrees with the battery reading.
    if actual == BATTERY {
        rig.metadata.store(1, Ordering::Release);
    }
    rig
}

#[tokio::test]
async fn each_preset_is_one_direct_pair_from_every_confirmed_state() {
    // Battery Saver and Battery Balanced are admitted on battery and on both
    // classified adapters; Silent on both adapters.
    for ((preset, selector), actual) in [
        (SILENT, FULL),
        (SILENT, LIMITED),
        (BATTERY_SAVER, BATTERY),
        (BATTERY_SAVER, FULL),
        (BATTERY_BALANCED, BATTERY),
        (BATTERY_BALANCED, LIMITED),
    ] {
        for (from, fans) in STATES {
            let rig = rig_at(from, fans, actual, MemoryFiles::default());
            let result = rig.set(1, value(preset, 1)).await;
            let case = format!("{preset} from ({from}, {fans}) at {actual}: {result}");
            let status = rig.get().await;
            if (from, fans) == (selector, 0) {
                // Already confirmed: nothing is prepared or written.
                assert_eq!(result["result"]["status"], 1, "{case}");
                assert!(setters(&rig).is_empty(), "{case}");
                assert!(rows(&rig).is_empty(), "{case}");
            } else if from == 7 && preset != SILENT.0 {
                // Managed leaves HyperBoost for a battery preset through the
                // unadmitted restore pair: intent is stored, nothing is written.
                assert_eq!(result["result"]["status"], 2, "{case}");
                assert_eq!(result["result"]["message"], NO_DIRECT_PATH, "{case}");
                assert!(setters(&rig).is_empty(), "{case}");
                assert!(rows(&rig).is_empty(), "{case}");
                assert_eq!(status["status"]["performanceOverride"]["value"], preset);
            } else {
                // One pair, the destination's own selector with Automatic fans.
                assert_eq!(result["result"]["status"], 1, "{case}");
                assert_eq!(
                    setters(&rig),
                    [vec![1, 1, selector, 0], vec![1, 2, selector, 0]],
                    "{case}"
                );
                assert_eq!(status["status"]["confirmedState"]["performance"], preset);
                assert_eq!(status["status"]["confirmedState"]["fans"], 0);
                let rows = rows(&rig);
                assert_eq!(
                    rows.iter()
                        .map(|r| r["progress"].clone())
                        .collect::<Vec<_>>(),
                    [0, 1, 2, 3, 4, 5],
                    "{case}"
                );
                assert!(rows.iter().all(|r| r["destination"] == preset), "{case}");
                assert!(rows.iter().all(|r| r["requested"]["value"] == preset));
                // The private projection names the persistent override (source
                // 4). Status metadata reports every external class as the one
                // coarse class 3, where a battery preset is not eligible, so the
                // projection then keeps its default slot; the override is kept.
                let mut source = rig.source.lock().await;
                let projected = source.snapshot(USER, Instant::now()).await.unwrap();
                drop(source);
                let projected = serde_json::to_value(projected).unwrap();
                assert_eq!(projected["performanceOverride"]["value"], preset);
                assert_eq!(
                    projected["desiredState"]["performance"],
                    if actual == BATTERY || preset == SILENT.0 {
                        json!({"value": preset, "source": 4})
                    } else {
                        json!({"value": 1, "source": 1})
                    },
                    "{case}"
                );
            }
            rig.stop().await;
        }
    }
}

#[tokio::test]
async fn power_classes_follow_the_ordinary_and_battery_selector_rules() {
    // Battery admits only the battery presets: a Silent override is saved but
    // not selected there, as managed's battery slot ignores it.
    let rig = rig_at(0, 0, BATTERY, MemoryFiles::default());
    let result = rig.set(1, value(SILENT.0, 1)).await;
    assert_eq!(result["result"]["status"], 2, "{result}");
    assert_eq!(
        result["result"]["message"],
        "Performance override intent was stored. Hardware result: No supported performance destination is selected by the current power and profile policy."
    );
    assert!(setters(&rig).is_empty());
    rig.stop().await;
    // Unclassified external power: managed
    // `ValidateUnclassifiedPerformanceOverride` rejects a battery preset
    // before any intent is stored; Silent is an ordinary command there.
    for (preset, selector) in [BATTERY_SAVER, BATTERY_BALANCED, SILENT] {
        let rig = rig_at(0, 0, 0x10, MemoryFiles::default());
        let result = rig.set(1, value(preset, 1)).await;
        if preset == SILENT.0 {
            assert_eq!(result["result"]["status"], 1, "{result}");
            assert_eq!(
                setters(&rig),
                [vec![1, 1, selector, 0], vec![1, 2, selector, 0]]
            );
        } else {
            assert_eq!(result["result"]["status"], 3, "{result}");
            assert!(setters(&rig).is_empty());
            assert!(rig.files.read("manual-overrides.json").unwrap().is_none());
        }
        rig.stop().await;
    }
}

#[tokio::test]
async fn a_class_change_between_setters_stops_the_pair_without_replay() {
    // (preset, first class, second-setter class, completes). Battery to an
    // adapter, or an adapter to battery, always stops; a battery preset keeps
    // full-to-limited continuity as ordinary presets do.
    for (preset, actual, changed, completes) in [
        (BATTERY_SAVER, BATTERY, FULL, false),
        (BATTERY_SAVER, FULL, BATTERY, false),
        (BATTERY_BALANCED, FULL, LIMITED, true),
        (SILENT, FULL, LIMITED, true),
        (SILENT, FULL, BATTERY, false),
    ] {
        let rig = rig_at(2, 0, actual, MemoryFiles::default());
        rig.port.state.lock().unwrap().change_power = Some((3, changed));
        let result = rig.set(1, value(preset.0, 1)).await;
        let case = format!("{preset:?} {actual} -> {changed}: {result}");
        let progress = Storage::open(Box::new(rig.files.clone()), SID)
            .unwrap()
            .progress();
        if completes {
            assert_eq!(result["result"]["status"], 1, "{case}");
            assert_eq!(setters(&rig).len(), 2, "{case}");
            assert_eq!(progress, Some(5), "{case}");
        } else {
            assert_eq!(result["result"]["status"], 2, "{case}");
            // A possibly submitted first setter reports the pending fence.
            assert_eq!(result["result"]["message"], PENDING, "{case}");
            assert_eq!(setters(&rig), [vec![1, 1, preset.1, 0]], "{case}");
            assert_eq!(progress, Some(2), "{case}");
            // The open pair fences every later request; nothing is replayed.
            rig.port.state.lock().unwrap().change_power = None;
            assert_eq!(rig.set(2, value(preset.0, 1)).await["result"]["status"], 2);
            assert_eq!(setters(&rig).len(), 1, "{case}");
        }
        rig.stop().await;
    }
}

#[tokio::test]
async fn a_lost_windows_battery_reading_stops_a_battery_pair_before_its_next_command() {
    // The second setter's adapter reading still reads battery, but Windows
    // then reports AC before submission: the checkpoint refuses it.
    let rig = rig_at(2, 0, BATTERY, MemoryFiles::default());
    rig.port.state.lock().unwrap().change_windows_after_reply = Some((3, Some(false)));
    let result = rig.set(1, value(BATTERY_SAVER.0, 1)).await;
    assert_eq!(result["result"]["status"], 2, "{result}");
    assert_eq!(setters(&rig), [vec![1, 1, 3, 0]]);
    let reopened = Storage::open(Box::new(rig.files.clone()), SID).unwrap();
    assert!(reopened.pending());
    assert_eq!(reopened.progress(), Some(2));
    rig.stop().await;
}

#[tokio::test]
async fn setter_ack_timeout_and_mismatch_failures_fence_restart_without_replay() {
    // (cut, last journal row, setters written). Every possibly submitted row
    // survives a restart as pending; the Prepared-only cut settles as known
    // not submitted.
    for (preset, actual) in [(SILENT, FULL), (BATTERY_BALANCED, BATTERY)] {
        for (cut, progress, writes) in [
            ("first-write", 1, 1),
            ("second-write", 3, 2),
            ("first-ack", 1, 1),
            ("final-timeout", 4, 2),
            ("final-mismatch", 4, 2),
            ("prepared-only", 6, 0),
        ] {
            let files = MemoryFiles::default();
            let rig = rig_at(2, 0, actual, files.clone());
            {
                let mut s = rig.port.state.lock().unwrap();
                match cut {
                    "first-write" => s.fail_write = Some(1),
                    "second-write" => s.fail_write = Some(2),
                    "first-ack" => s.corrupt_ack = Some(1),
                    "final-timeout" => s.final_read_timeout = true,
                    "final-mismatch" => s.mismatch_after = Some(2),
                    // The first setter's own reading changes class.
                    _ => s.change_power = Some((2, if actual == BATTERY { FULL } else { BATTERY })),
                }
            }
            let result = rig.set(1, value(preset.0, 1)).await;
            let case = format!("{preset:?} {cut}: {result}");
            assert_eq!(result["result"]["status"], 2, "{case}");
            assert_eq!(setters(&rig).len(), writes, "{case}");
            let rows = rows(&rig);
            assert_eq!(rows.last().unwrap()["progress"], progress, "{case}");
            assert!(rows.iter().all(|r| r["destination"] == preset.0), "{case}");
            rig.stop().await;
            // Restart from the same protected files.
            let reopened = Storage::open(Box::new(files.clone()), SID).unwrap();
            assert_eq!(reopened.pending(), progress < 5, "{case}");
            let restarted = rig_at(2, 0, actual, files);
            let retry = restarted.set(2, value(preset.0, 1)).await;
            if progress < 5 {
                assert_eq!(retry["result"]["status"], 2, "{case}");
                assert!(setters(&restarted).is_empty(), "{case}");
            } else {
                assert_eq!(retry["result"]["status"], 1, "{case}");
                assert_eq!(setters(&restarted).len(), 2, "{case}");
            }
            restarted.stop().await;
        }
    }
}

#[tokio::test]
async fn saved_profile_presets_apply_when_explicitly_requested() {
    // A clear selects the saved profile slot, which may now be any admitted
    // pair destination: plugged-in Silent, the fixture's battery Battery
    // Saver, and a USB-C Battery Balanced.
    for (actual, plugged_in, usb_c, (preset, selector)) in [
        (FULL, SILENT.0, None, SILENT),
        (BATTERY, 1, None, BATTERY_SAVER),
        (LIMITED, 1, Some(BATTERY_BALANCED.0), BATTERY_BALANCED),
    ] {
        let files = MemoryFiles::default();
        files.profiles_with(plugged_in, usb_c);
        let rig = rig_at(2, 0, actual, files);
        let result = rig.set(1, Value::Null).await;
        assert_eq!(result["result"]["status"], 1, "{result}");
        assert_eq!(
            setters(&rig),
            [vec![1, 1, selector, 0], vec![1, 2, selector, 0]]
        );
        // As for every clear, desiredState keeps the coarse metadata default
        // (the slot is not claimed as resolved); firmware readback reports
        // the applied destination.
        let status = rig.get().await;
        assert_eq!(status["status"]["confirmedState"]["performance"], preset);
        rig.stop().await;
    }
}

#[tokio::test]
async fn manual_fans_defer_a_destination_the_fan_return_does_not_write() {
    // Managed defers every preset under saved manual fans on external power.
    // The native fan return writes only Balanced or Performance, so the reply
    // asks for a new request instead of promising the return applies it.
    for (preset, _) in [SILENT, BATTERY_SAVER] {
        let rig = rig_at(2, 1, FULL, manual_files());
        let result = rig.set(1, value(preset, 1)).await;
        assert_eq!(result["result"]["status"], 0, "{result}");
        assert_eq!(result["result"]["message"], RESUBMIT);
        assert!(setters(&rig).is_empty());
        assert!(rows(&rig).is_empty());
        rig.stop().await;
    }
}

#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn a_fan_return_keeps_the_selector_and_names_an_unwritten_saved_preset() {
    let r = fan_rig(0, 0);
    r.get().await;
    assert_eq!(
        r.request(fan_request(230, Some(1), 3700)).await["result"]["status"],
        1
    );
    let deferred = r.set(231, value(SILENT.0, 1)).await;
    assert_eq!(deferred["result"]["status"], 0, "{deferred}");
    assert_eq!(deferred["result"]["message"], RESUBMIT);
    let sent = r.port.state.lock().unwrap().sent.len();
    let out = r.request(fan_request(232, None, 0)).await;
    assert_eq!(out["result"]["status"], 1, "{out}");
    assert_eq!(mode_setters(&r, sent), [[1, 1, 0, 0], [1, 2, 0, 0]]);
    assert!(
        out["result"]["message"]
            .as_str()
            .unwrap()
            .ends_with(" The saved Silent performance intent was not applied: submit a new performance request to apply it."),
        "{out}"
    );
    // The new performance request the reply names applies it directly.
    let sent = r.port.state.lock().unwrap().sent.len();
    assert_eq!(r.set(233, value(SILENT.0, 1)).await["result"]["status"], 1);
    assert_eq!(mode_setters(&r, sent), [[1, 1, 5, 0], [1, 2, 5, 0]]);
    r.origin.stop();
    r.stop().await;
}

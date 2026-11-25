//! Silent, Battery Saver, Battery Balanced and Custom through the installed
//! dispatcher: each preset is its own reviewed RZ09-0581 0D/02 pair (managed
//! `ProductionBatteryPort.SetPerformanceAsync`), and Custom adds the 0D/07 CPU
//! and GPU level setters, written directly from every confirmed state with the
//! destination's own power-class rule.
use super::*;
use Change::{After, During};
use Event::{Adapter, WindowsKind};

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
/// Managed `ValidateThermalOverrideCombination`, as `RecordRejectedIntent`
/// reports it (Failed).
const SOURCE_REJECTED: &str =
    "The requested intent was rejected: The performance preset is not valid for this power source.";
const COMBINATION_REJECTED: &str = "The requested intent was rejected: The performance and fan combination is not supported by the active validation policy.";
const RESUBMIT: &str = "Performance intent stored. Performance is saved, not applied. Manual fan intent is preserved. Submit a new performance request after selecting Automatic fans.";

#[cfg(feature = "service-thermal-hyperboost-exit-v1")]
#[path = "hyperboost_exit.rs"]
mod hyperboost_exit;

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

/// When a test's power change lands. The fixture derives Windows from the
/// adapter unless a test sets it, so an adapter change is a real unplug or
/// plug-in. During a power read's query, Windows changes across it, which
/// stops that setter (managed `AdmitOrdinaryThermalCommandAsync`); once a 0D
/// setter (1-based) is sent, the change lands before the next setter's own
/// Windows sample and reading, which admit it afresh.
#[derive(Clone, Copy, Debug)]
enum Change {
    During(usize),
    After(usize),
}
/// A test's adapter change or Windows-only change.
#[derive(Clone, Copy, Debug)]
enum Event {
    Adapter(Change, u8),
    WindowsKind(Change, Option<bool>),
}
fn change(rig: &Rig, event: Event) {
    let mut s = rig.port.state.lock().unwrap();
    match event {
        Adapter(During(read), actual) => s.change_power = Some((read, actual)),
        Adapter(After(write), actual) => s.change_power_after_write = Some((write, actual)),
        WindowsKind(During(read), windows) => s.change_windows = Some((read, windows)),
        WindowsKind(After(write), windows) => s.change_windows_after_write = Some((write, windows)),
    }
}
/// Clears every pending change, so a retry sees a stable source.
fn settle(rig: &Rig) {
    let mut s = rig.port.state.lock().unwrap();
    s.change_power = None;
    s.change_power_after_write = None;
    s.change_windows = None;
    s.change_windows_after_write = None;
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
            } else if from == 7
                && preset != SILENT.0
                && cfg!(feature = "service-thermal-hyperboost-exit-v1")
            {
                // Managed leaves HyperBoost for a battery preset through the
                // restore pair (the Balanced bytes), an intermediate read, then
                // the battery pair.
                assert_eq!(result["result"]["status"], 1, "{case}");
                assert_eq!(
                    setters(&rig),
                    [
                        vec![1, 1, 0, 0],
                        vec![1, 2, 0, 0],
                        vec![1, 1, selector, 0],
                        vec![1, 2, selector, 0]
                    ],
                    "{case}"
                );
                assert_eq!(
                    rows(&rig)
                        .iter()
                        .map(|r| r["progress"].as_u64().unwrap())
                        .collect::<Vec<_>>(),
                    [0, 80, 81, 82, 83, 1, 2, 3, 4, 5],
                    "{case}"
                );
                assert!(
                    rows(&rig).iter().all(|r| r["destination"] == preset),
                    "{case}"
                );
                assert_eq!(status["status"]["confirmedState"]["performance"], preset);
                assert_eq!(status["status"]["confirmedState"]["fans"], 0);
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
    // Battery admits only the battery presets: managed
    // `ValidateThermalOverrideCombination` rejects every other override on
    // battery Windows confirms, before anything is stored.
    for preset in [0, 1, 2, 5] {
        let rig = rig_at(0, 0, BATTERY, MemoryFiles::default());
        let request = if preset == 5 {
            custom(1, 0, false)
        } else {
            value(preset, 1)
        };
        let result = rig.set(1, request).await;
        assert_eq!(result["result"]["status"], 3, "{result}");
        assert_eq!(result["result"]["message"], SOURCE_REJECTED);
        assert!(setters(&rig).is_empty());
        assert!(rig.files.read("manual-overrides.json").unwrap().is_none());
        rig.stop().await;
    }
    // The class decides only where Windows agrees. Under Windows AC or
    // unknown power, managed samples the adapter and maps its Battery reply to
    // ExternalUnknown, which `ExecuteScopedMutationAsync` blocks before any
    // intent is stored, a clear included.
    for windows in [Some(false), None] {
        for request in [
            Some(value(SILENT.0, 1)),
            Some(value(BATTERY_SAVER.0, 0)),
            None,
        ] {
            let rig = rig_at(0, 0, BATTERY, MemoryFiles::default());
            rig.port.state.lock().unwrap().windows_on_battery = Some(windows);
            let result = rig.set(1, request.unwrap_or(Value::Null)).await;
            assert_eq!(result["result"]["status"], 2, "{result}");
            assert_eq!(
                result["result"]["message"],
                crate::installed::readiness::thermal_mutation::UNCLASSIFIED_BLOCKED
            );
            assert!(setters(&rig).is_empty());
            assert!(rig.files.read("manual-overrides.json").unwrap().is_none());
            rig.stop().await;
        }
    }
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
async fn a_class_change_between_setters_continues_only_while_the_preset_is_admitted() {
    // (preset, first class, change, completes). Managed admits each command
    // on its own fresh reading (`AdmitOrdinaryThermalCommandAsync`): a
    // battery preset on Windows battery or an agreeing adapter, so it
    // completes across an unplug, a plug-in or full-to-limited between its
    // setters, as ordinary presets do between external classes. An ordinary
    // preset is refused on battery, and a reading that disagrees with Windows
    // admits no command, so both stop after the first setter without replay.
    // The same changes during the second setter's query (power read 3)
    // continue only while Windows is unchanged across it.
    const BETWEEN: Change = After(1);
    const DURING: Change = During(3);
    for (preset, actual, event, completes) in [
        (BATTERY_SAVER, BATTERY, Adapter(BETWEEN, FULL), true),
        (BATTERY_SAVER, FULL, Adapter(BETWEEN, BATTERY), true),
        (BATTERY_BALANCED, LIMITED, Adapter(BETWEEN, BATTERY), true),
        (BATTERY_BALANCED, BATTERY, Adapter(BETWEEN, LIMITED), true),
        (BATTERY_BALANCED, FULL, Adapter(BETWEEN, LIMITED), true),
        (SILENT, FULL, Adapter(BETWEEN, LIMITED), true),
        (SILENT, FULL, Adapter(BETWEEN, BATTERY), false),
        (BATTERY_SAVER, FULL, WindowsKind(BETWEEN, Some(true)), false),
        (
            BATTERY_SAVER,
            BATTERY,
            WindowsKind(BETWEEN, Some(false)),
            false,
        ),
        (BATTERY_BALANCED, FULL, Adapter(DURING, LIMITED), true),
        (SILENT, LIMITED, Adapter(DURING, FULL), true),
        (BATTERY_SAVER, FULL, Adapter(DURING, BATTERY), false),
        (BATTERY_SAVER, BATTERY, Adapter(DURING, FULL), false),
    ] {
        let rig = rig_at(2, 0, actual, MemoryFiles::default());
        change(&rig, event);
        let result = rig.set(1, value(preset.0, 1)).await;
        let case = format!("{preset:?} {actual} {event:?}: {result}");
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
            settle(&rig);
            assert_eq!(rig.set(2, value(preset.0, 1)).await["result"]["status"], 2);
            assert_eq!(setters(&rig).len(), 1, "{case}");
        }
        rig.stop().await;
    }
}

#[tokio::test]
async fn a_balanced_to_battery_pair_continues_across_a_power_change_between_its_setters() {
    // The shared fence admits each setter of a direct Balanced-to-battery
    // pair on its own Windows samples and reading: power read 2 is the first
    // setter's and 3 the second's. An unplug or a plug-in after the first
    // SET completes the pair with nothing pending, and so does a class
    // change during a query that leaves Windows unchanged. A Windows change
    // across a setter's query stops at that setter, as managed does.
    for (actual, event, writes) in [
        (FULL, Adapter(After(1), BATTERY), 2),
        (LIMITED, Adapter(After(1), BATTERY), 2),
        (BATTERY, Adapter(After(1), FULL), 2),
        (BATTERY, Adapter(After(1), LIMITED), 2),
        (FULL, Adapter(During(3), LIMITED), 2),
        (FULL, Adapter(During(2), BATTERY), 0),
        (BATTERY, Adapter(During(3), FULL), 1),
        (FULL, WindowsKind(During(2), Some(true)), 0),
        (BATTERY, WindowsKind(During(3), Some(false)), 1),
    ] {
        let files = MemoryFiles::default();
        let rig = rig_at(0, 0, actual, files.clone());
        change(&rig, event);
        let result = rig.set(1, value(BATTERY_SAVER.0, 1)).await;
        let case = format!("{actual} {event:?}: {result}");
        let pair = [
            vec![1, 1, BATTERY_SAVER.1, 0],
            vec![1, 2, BATTERY_SAVER.1, 0],
        ];
        assert_eq!(setters(&rig), pair[..writes], "{case}");
        let reopened = Storage::open(Box::new(files), SID).unwrap();
        match writes {
            2 => {
                assert_eq!(result["result"]["status"], 1, "{case}");
                assert!(!reopened.pending(), "{case}");
                assert_eq!(reopened.progress(), Some(5), "{case}");
            }
            // Nothing was submitted: the Prepared row retires.
            0 => {
                assert_eq!(result["result"]["status"], 2, "{case}");
                assert!(!reopened.pending(), "{case}");
                assert_eq!(reopened.progress(), Some(6), "{case}");
            }
            _ => {
                assert_eq!(result["result"]["message"], PENDING, "{case}");
                assert!(reopened.pending(), "{case}");
                assert_eq!(reopened.progress(), Some(2), "{case}");
            }
        }
        rig.stop().await;
    }
}

#[tokio::test]
async fn a_windows_change_after_a_setters_send_is_left_to_the_next_reading() {
    // Managed checks the Windows source only up to a command's SET. A change
    // while the SET is in flight leaves that setter's reply to be read: after
    // the last setter the pair completes, confirmed by its final read, and
    // after the first the second setter's own admission decides: Windows
    // battery refuses Silent before its query, and a battery reading under
    // Windows AC admits no command.
    for (preset, actual, write, windows, completes) in [
        (SILENT, FULL, 2, Some(true), true),
        (BATTERY_SAVER, FULL, 2, Some(true), true),
        (BATTERY_SAVER, BATTERY, 2, Some(false), true),
        (SILENT, FULL, 1, Some(true), false),
        (BATTERY_SAVER, BATTERY, 1, Some(false), false),
    ] {
        let rig = rig_at(2, 0, actual, MemoryFiles::default());
        rig.port.state.lock().unwrap().change_windows_after_write = Some((write, windows));
        let result = rig.set(1, value(preset.0, 1)).await;
        let case = format!("{preset:?} {actual} after write {write} {windows:?}: {result}");
        let progress = Storage::open(Box::new(rig.files.clone()), SID)
            .unwrap()
            .progress();
        if completes {
            assert_eq!(result["result"]["status"], 1, "{case}");
            assert_eq!(setters(&rig).len(), 2, "{case}");
            assert_eq!(progress, Some(5), "{case}");
        } else {
            assert_eq!(result["result"]["message"], PENDING, "{case}");
            assert_eq!(setters(&rig).len(), 1, "{case}");
            assert_eq!(progress, Some(2), "{case}");
        }
        rig.stop().await;
    }
}

#[tokio::test]
async fn a_windows_change_across_a_setters_query_stops_before_its_send() {
    // Managed samples Windows before each command's 07/8C query and again
    // after its reply (`AdmitOrdinaryThermalCommandAsync`). Windows changes
    // after the first SET and would change back while the second setter's
    // query (power read 3) is in flight. Silent's sample before that query is
    // Windows battery, which refuses it before the query is sent, as managed
    // throws before querying; the battery preset's query is sent under
    // Windows AC and its reply read under Windows battery, a change that
    // refuses it. Either stops before its second SET with the first pending,
    // as managed stops Indeterminate.
    for (preset, actual, before, after, queries) in [
        (SILENT, FULL, Some(true), Some(false), 2),
        (BATTERY_SAVER, BATTERY, Some(false), Some(true), 3),
    ] {
        let rig = rig_at(2, 0, actual, MemoryFiles::default());
        {
            let mut s = rig.port.state.lock().unwrap();
            s.change_windows_after_write = Some((1, before));
            s.change_windows = Some((3, after));
        }
        let result = rig.set(1, value(preset.0, 1)).await;
        let case = format!("{preset:?} {actual} {before:?} then {after:?}: {result}");
        assert_eq!(result["result"]["message"], PENDING, "{case}");
        assert_eq!(setters(&rig), [vec![1, 1, preset.1, 0]], "{case}");
        assert_eq!(
            rig.port.state.lock().unwrap().power_reads,
            queries,
            "{case}"
        );
        let reopened = Storage::open(Box::new(rig.files.clone()), SID).unwrap();
        assert!(reopened.pending(), "{case}");
        assert_eq!(reopened.progress(), Some(2), "{case}");
        rig.stop().await;
    }
}

#[tokio::test]
async fn a_battery_reading_under_windows_ac_refuses_a_setter_and_keeps_an_ac_temporary() {
    // A temporary Battery Saver saved on a full adapter. The first setter's
    // own reading reads battery while Windows stays AC. The class rule
    // refuses it before `destination_holds`: a reading that contradicts
    // Windows never becomes the current class, so it observes no battery
    // power (`battery_reading_contradicts_windows`) and the AC temporary
    // survives for status and later requests.
    let rig = rig_at(2, 0, FULL, MemoryFiles::default());
    {
        let mut s = rig.port.state.lock().unwrap();
        s.windows_on_battery = Some(Some(false));
        s.change_power = Some((2, BATTERY));
    }
    let result = rig.set(1, value(BATTERY_SAVER.0, 0)).await;
    let case = format!("{result}");
    assert_eq!(result["result"]["status"], 2, "{case}");
    assert!(setters(&rig).is_empty(), "{case}");
    {
        let mut s = rig.port.state.lock().unwrap();
        s.change_power = None;
        s.actual = FULL;
    }
    let status = rig.get().await;
    let current = &status["status"]["performanceOverride"];
    assert_eq!(current["value"], BATTERY_SAVER.0, "{case} / {status}");
    assert_eq!(current["lifetime"], 0, "{case} / {status}");
    rig.stop().await;
}

#[tokio::test]
async fn a_lost_windows_battery_reading_stops_a_battery_pair_before_its_next_command() {
    // The second setter's adapter reading still reads battery, but Windows
    // then reports AC, or an unknown source, before submission: the
    // checkpoint refuses it. Only a Windows battery reading holds.
    for windows in [Some(false), None] {
        let rig = rig_at(2, 0, BATTERY, MemoryFiles::default());
        rig.port.state.lock().unwrap().change_windows_after_reply = Some((3, windows));
        let result = rig.set(1, value(BATTERY_SAVER.0, 1)).await;
        assert_eq!(result["result"]["status"], 2, "{windows:?} {result}");
        assert_eq!(setters(&rig), [vec![1, 1, 3, 0]], "{windows:?}");
        let reopened = Storage::open(Box::new(rig.files.clone()), SID).unwrap();
        assert!(reopened.pending());
        assert_eq!(reopened.progress(), Some(2));
        rig.stop().await;
    }
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
                    // The first setter's own reading disagrees with Windows.
                    _ => s.change_windows = Some((2, Some(actual != BATTERY))),
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

#[cfg(not(feature = "service-thermal-hyperboost-exit-v1"))]
#[tokio::test]
async fn the_resolved_application_preset_leaving_hyperboost_is_refused_as_no_direct_path() {
    // A clear under confirmed HyperBoost selects the resolved profile. The
    // plugged-in profile's Balanced is a direct exit; the active application's
    // Battery Saver needs managed's restore sequence, and native has no
    // recovered HyperBoost layer to hold instead, so nothing is written.
    const CLEARED: &str = "Performance override was cleared. Hardware result: Leaving HyperBoost for a battery preset needs the HyperBoost restore sequence, which this build does not submit; no thermal setter was submitted.";
    for reported in [false, true] {
        let files = MemoryFiles::default();
        files.profiles(1);
        let mut document = files.document("profiles.json");
        document["profiles"]["applicationProfiles"][0]["overrides"]["pluggedIn"] =
            json!({"performance": BATTERY_SAVER.0});
        files.put("profiles.json", document);
        files.commit_configuration();
        let rig = rig_at(7, 0, FULL, files);
        if reported {
            report_game(&rig).await;
        }
        let result = rig.set(1, Value::Null).await;
        if reported {
            assert_eq!(result["result"]["status"], 2, "{result}");
            assert_eq!(result["result"]["message"], CLEARED);
            assert!(setters(&rig).is_empty(), "{result}");
            assert!(rows(&rig).is_empty(), "{result}");
        } else {
            assert_eq!(result["result"]["status"], 1, "{result}");
            assert!(!setters(&rig).is_empty(), "{result}");
            let status = rig.get().await;
            assert_eq!(status["status"]["confirmedState"]["performance"], 1);
        }
        rig.stop().await;
    }
}

#[tokio::test]
async fn manual_fans_defer_every_destination_to_the_fan_return() {
    // Managed defers every destination the saved manual fans admit on
    // external power, and the native fan return writes each of them. Without
    // the fan composition no native request returns the fans, so that build
    // asks for a new performance request. A battery preset is admitted with
    // Fixed fans, not with a manual curve (below).
    for (request, files) in [
        (value(SILENT.0, 1), manual_files()),
        (custom(1, 0, false), manual_files()),
        (value(BATTERY_SAVER.0, 1), fixed_files()),
        (value(BATTERY_BALANCED.0, 1), fixed_files()),
    ] {
        let rig = rig_at(2, 1, FULL, files);
        let result = rig.set(1, request).await;
        assert_eq!(result["result"]["status"], 0, "{result}");
        assert_eq!(
            result["result"]["message"],
            if cfg!(feature = "service-fan-mutation-v2") {
                "Performance intent was stored and will be applied when fans return to Automatic."
            } else {
                RESUBMIT
            }
        );
        assert!(setters(&rig).is_empty());
        assert!(rows(&rig).is_empty());
        rig.stop().await;
    }
}

#[tokio::test]
async fn a_manual_curve_rejects_the_battery_presets_before_storing() {
    // Managed `ValidatePerformanceFanCombination` admits no battery preset
    // with a manual curve (`CreateUnverifiedSafeDefaults`), so
    // `ValidateThermalOverrideCombination` rejects them under the saved
    // ManualCurve override before anything is stored.
    for (preset, actual) in [
        (BATTERY_SAVER.0, FULL),
        (BATTERY_BALANCED.0, FULL),
        (BATTERY_SAVER.0, LIMITED),
    ] {
        let rig = rig_at(2, 1, actual, manual_files());
        let saved = rig.files.read("manual-overrides.json").unwrap();
        let result = rig.set(1, value(preset, 1)).await;
        assert_eq!(result["result"]["status"], 3, "{result}");
        assert_eq!(result["result"]["message"], COMBINATION_REJECTED);
        assert!(setters(&rig).is_empty());
        assert!(rows(&rig).is_empty());
        assert_eq!(rig.files.read("manual-overrides.json").unwrap(), saved);
        rig.stop().await;
    }
}

fn fixed_files() -> MemoryFiles {
    let files = MemoryFiles::default();
    let fan = json!({"mode":1,"fixedSpeedRpm":3700,"manualCurve":{"cpu":[{"temperatureCelsius":30,"speedRpm":2000},{"temperatureCelsius":90,"speedRpm":5400}],"gpu":[{"temperatureCelsius":30,"speedRpm":2000},{"temperatureCelsius":90,"speedRpm":5400}],"useHigherTemperatureForBoth":true}});
    files.put("manual-overrides.json",json!({"schemaVersion":8,"overrides":{"performance":null,"fans":1,"fanControl":fan,"lighting":null,"customPerformance":null,"gpuClockTuning":null}}));
    files
}

#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn a_fan_return_writes_every_saved_destination_directly() {
    // Managed `ApplyDirectAutomaticFanTransitionOutcomeAsync` leaves manual fans
    // through the saved destination's own setters: a preset's pair, or the
    // Custom pair and its CPU and GPU levels. No fan pair is written first.
    for (actual, source) in [(FULL, 0), (LIMITED, 2)] {
        for (request, name, selector, levels) in [
            (value(SILENT.0, 1), "Silent", SILENT.1, None),
            (
                value(BATTERY_SAVER.0, 1),
                "BatterySaver",
                BATTERY_SAVER.1,
                None,
            ),
            (
                value(BATTERY_BALANCED.0, 1),
                "BatteryBalanced",
                BATTERY_BALANCED.1,
                None,
            ),
            (custom(2, 1, false), "Custom", 4, Some([2u8, 1])),
        ] {
            let r = handback_rig_with(0, actual, request.clone()).await;
            let sent = r.port.state.lock().unwrap().sent.len();
            let out = r.request(fan_request(232, None, 0)).await;
            let case = format!("{name} at {actual}: {out}");
            assert_eq!(out["result"]["status"], 1, "{case}");
            assert_eq!(
                out["result"]["message"],
                format!(
                    "Fans returned to Automatic with {name} performance, confirmed by independent paired readback."
                ),
                "{case}"
            );
            let commands: Vec<_> = r.port.state.lock().unwrap().sent[sent..]
                .iter()
                .map(|(class, command, payload, _)| (*class, *command, payload.clone()))
                .collect();
            let written: Vec<_> = commands
                .iter()
                .filter(|(class, command, _)| *class == 13 && matches!(command, 2 | 7))
                .map(|(_, command, payload)| (*command, payload.clone()))
                .collect();
            let mut expected = vec![(2, vec![1, 1, selector, 0]), (2, vec![1, 2, selector, 0])];
            if let Some([cpu, gpu]) = levels {
                expected.extend([(7, vec![1, 1, cpu]), (7, vec![1, 2, gpu])]);
            }
            assert_eq!(written, expected, "{case}");
            // A fresh 07/8C read precedes each setter.
            for (index, (class, command, _)) in commands.iter().enumerate() {
                if *class == 13 && matches!(command, 2 | 7) {
                    assert_eq!((commands[index - 1].0, commands[index - 1].1), (7, 0x8c));
                }
            }
            let rows = request_rows(&r, 232);
            let progress: Vec<_> = rows.iter().map(|row| row["progress"].clone()).collect();
            if levels.is_some() {
                assert_eq!(progress, [0, 10, 11, 12, 13, 14, 15, 16, 17, 50], "{case}");
            } else {
                assert_eq!(progress, [0, 10, 11, 12, 13, 50], "{case}");
            }
            for row in &rows {
                assert_eq!(row["fan"]["selector"], selector, "{case}");
                assert_eq!(row["fan"]["source"], source, "{case}");
                assert_eq!(row["fan"]["intent"]["mode"], 0, "{case}");
                assert_eq!(row["fan"]["custom"], json!(levels), "{case}");
            }
            assert!(!r.storage.outstanding_fan());
            assert!(!r.storage.pending());
            let observed = fresh_observe(&r).await;
            let confirmed = &observed["status"]["confirmedState"];
            assert_eq!(confirmed["performance"], request["value"], "{case}");
            assert_eq!(confirmed["fans"], 0, "{case}");
            if let Some([cpu, gpu]) = levels {
                assert_eq!(
                    confirmed["customPerformance"],
                    json!({"cpu":cpu,"gpu":gpu,"maximumFans":false})
                );
            }
            r.origin.stop();
            r.stop().await;
            assert_eq!(
                r.port.state.lock().unwrap().writes,
                4 + expected.len(),
                "{case}"
            );
        }
    }
}

#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn a_fan_return_custom_level_mismatch_fences_without_replay() {
    let r = handback_rig_with(0, FULL, custom(1, 2, false)).await;
    r.port.state.lock().unwrap().ignore_levels = true;
    let out = r.request(fan_request(232, None, 0)).await;
    assert_eq!(out["result"]["status"], 2, "{out}");
    assert_eq!(
        out["result"]["message"],
        "Fan submission or owned release remains unresolved; further writes are fenced."
    );
    assert_eq!(request_rows(&r, 232).last().unwrap()["progress"], 17);
    assert!(r.storage.pending());
    // Neither the fan return nor a performance request replays a setter.
    let writes = r.port.state.lock().unwrap().writes;
    assert_eq!(writes, 8);
    assert_eq!(
        r.request(fan_request(233, None, 0)).await["result"]["status"],
        2
    );
    assert_eq!(r.set(234, custom(1, 2, false)).await["result"]["status"], 2);
    assert_eq!(r.port.state.lock().unwrap().writes, writes);
    // Stop keeps the unresolved row and submits nothing.
    r.origin.stop();
    let sent = r.port.state.lock().unwrap().sent.len();
    assert!(r.source.lock().await.drain().await.is_err());
    assert_eq!(r.port.state.lock().unwrap().sent.len(), sent);
}

#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn a_fan_return_class_change_before_a_level_setter_follows_managed() {
    // A full/limited change keeps the carried Custom destination; battery
    // stops before the next setter without replay.
    for (changed, completes) in [(LIMITED, true), (BATTERY, false)] {
        // Reads after the first resolve the class; four more precede setters.
        for setter in 1..=4 {
            let r = handback_rig_with(0, FULL, custom(0, 1, false)).await;
            let reads = r.port.state.lock().unwrap().power_reads;
            r.port.state.lock().unwrap().change_power = Some((reads + 1 + setter, changed));
            let out = r.request(fan_request(232, None, 0)).await;
            let case = format!("{changed} before setter {setter}: {out}");
            let writes = r.port.state.lock().unwrap().writes - 4;
            let last = request_rows(&r, 232).last().unwrap()["progress"].clone();
            if completes {
                assert_eq!(out["result"]["status"], 1, "{case}");
                assert_eq!((writes, last), (4, json!(50)), "{case}");
            } else {
                assert_eq!(out["result"]["status"], 2, "{case}");
                assert_eq!(writes, setter - 1, "{case}");
                // Before the first setter nothing was submitted (6); later the
                // acknowledged row stays pending.
                let expected = if setter == 1 { 6 } else { 9 + 2 * (setter - 1) };
                assert_eq!(last, expected, "{case}");
                assert_eq!(r.storage.pending(), setter > 1, "{case}");
                // Stop claims no release while the fans stay owned under
                // battery or the acknowledged row is open, and writes nothing.
                r.origin.stop();
                let writes = r.port.state.lock().unwrap().writes;
                let drained = r.source.lock().await.drain().await;
                assert!(drained.is_err(), "{case}");
                assert_eq!(r.port.state.lock().unwrap().writes, writes, "{case}");
                continue;
            }
            r.origin.stop();
            r.stop().await;
        }
    }
}

// Custom on RZ09-0581: managed `ProductionBatteryPort.SetCustomPerformanceAsync`
// writes the selector-4 pair, then the 0D/07 CPU and GPU level setters, and
// settles only when the final 0D/82 and 0D/87 reads report all three.
const CUSTOM: i32 = 5;
fn custom(cpu: u8, gpu: u8, maximum_fans: bool) -> Value {
    json!({"value":CUSTOM,"lifetime":1,"customPerformance":{"cpu":cpu,"gpu":gpu,"maximumFans":maximum_fans,"gpuClockOffsets":null},"gpuClockTuning":null})
}
/// Every thermal setter in submission order, as (command, payload).
fn thermal_setters(rig: &Rig) -> Vec<(u8, Vec<u8>)> {
    rig.port
        .state
        .lock()
        .unwrap()
        .sent
        .iter()
        .filter(|(class, command, _, _)| *class == 13 && matches!(command, 2 | 7))
        .map(|(_, command, payload, _)| (*command, payload.clone()))
        .collect()
}
fn custom_setters(cpu: u8, gpu: u8) -> Vec<(u8, Vec<u8>)> {
    vec![
        (2, vec![1, 1, 4, 0]),
        (2, vec![1, 2, 4, 0]),
        (7, vec![1, 1, cpu]),
        (7, vec![1, 2, gpu]),
    ]
}
fn progress(rig: &Rig) -> Vec<Value> {
    rows(rig).iter().map(|r| r["progress"].clone()).collect()
}
const CUSTOM_CHAIN: [u8; 10] = [0, 1, 2, 3, 4, 7, 8, 9, 10, 5];

#[tokio::test]
async fn custom_is_the_pair_then_both_level_setters_from_every_confirmed_state() {
    for actual in [FULL, LIMITED, 0x10] {
        for (from, fans) in STATES {
            let rig = rig_at(from, fans, actual, MemoryFiles::default());
            let result = rig.set(1, custom(2, 1, false)).await;
            let case = format!("from ({from}, {fans}) at {actual}: {result}");
            if (from, actual) == (7, 0x10) {
                // Leaving HyperBoost pins a class that unclassified power lacks.
                assert_eq!(result["result"]["status"], 2, "{case}");
                assert!(thermal_setters(&rig).is_empty(), "{case}");
                rig.stop().await;
                continue;
            }
            assert_eq!(result["result"]["status"], 1, "{case}");
            assert_eq!(thermal_setters(&rig), custom_setters(2, 1), "{case}");
            assert_eq!(progress(&rig), CUSTOM_CHAIN, "{case}");
            let rows = rows(&rig);
            assert!(rows.iter().all(|r| r["destination"] == CUSTOM), "{case}");
            assert!(
                rows.iter()
                    .all(|r| r["requested"]["customPerformance"]["cpu"] == 2),
                "{case}"
            );
            let status = rig.get().await;
            let confirmed = &status["status"]["confirmedState"];
            assert_eq!(confirmed["performance"], CUSTOM, "{case}");
            assert_eq!(confirmed["fans"], 0, "{case}");
            assert_eq!(
                confirmed["customPerformance"],
                json!({"cpu":2,"gpu":1,"maximumFans":false}),
                "{case}"
            );
            if actual != 0x10 {
                // The private projection selects the persistent override and
                // reports the requested levels, not the firmware's.
                let mut source = rig.source.lock().await;
                let projected = source.snapshot(USER, Instant::now()).await.unwrap();
                drop(source);
                let projected = serde_json::to_value(projected).unwrap();
                assert_eq!(
                    projected["desiredState"]["performance"],
                    json!({"value": CUSTOM, "source": 4}),
                    "{case}"
                );
                assert_eq!(
                    projected["desiredState"]["customPerformance"],
                    json!({"cpu":2,"gpu":1,"maximumFans":false}),
                    "{case}"
                );
            }
            rig.stop().await;
        }
    }
}

#[tokio::test]
async fn every_admitted_level_pair_writes_its_own_fixture_payloads() {
    // The RZ09-0581 capability range: every CPU/GPU pair except High/High.
    for (cpu, gpu) in [
        (0, 0),
        (0, 1),
        (0, 2),
        (1, 0),
        (1, 1),
        (1, 2),
        (2, 0),
        (2, 1),
    ] {
        let rig = rig_at(2, 0, FULL, MemoryFiles::default());
        let result = rig.set(1, custom(cpu, gpu, false)).await;
        assert_eq!(result["result"]["status"], 1, "{cpu}/{gpu}: {result}");
        assert_eq!(thermal_setters(&rig), custom_setters(cpu, gpu));
        rig.stop().await;
    }
    // Already confirmed: the getter decodes Custom with the same levels.
    let rig = rig_at(4, 0, FULL, MemoryFiles::default());
    let result = rig.set(1, custom(1, 0, false)).await;
    assert_eq!(result["result"]["status"], 1, "{result}");
    assert!(thermal_setters(&rig).is_empty());
    assert!(rows(&rig).is_empty());
    rig.stop().await;
    // Custom with other levels is written in full, the pair included.
    let rig = rig_at(4, 0, FULL, MemoryFiles::default());
    assert_eq!(rig.set(1, custom(0, 2, false)).await["result"]["status"], 1);
    assert_eq!(thermal_setters(&rig), custom_setters(0, 2));
    rig.stop().await;
}

/// A profile document whose plugged-in profile selects Custom without levels.
fn custom_without_levels_files() -> MemoryFiles {
    let files = MemoryFiles::default();
    files.profiles_with(CUSTOM, None);
    let mut document = files.document("profiles.json");
    document["profiles"]["pluggedIn"]["customPerformance"] = Value::Null;
    files.put("profiles.json", document);
    files.commit_configuration();
    files
}

#[tokio::test]
async fn a_saved_custom_without_levels_applies_the_default_levels() {
    // Managed resolves a selected Custom's missing levels to
    // `CustomPerformanceSettings.Default`, Medium CPU and High GPU, and
    // applies them; it does not report the selection as unsupported.
    let rig = rig_at(2, 0, FULL, custom_without_levels_files());
    let result = rig.set(1, Value::Null).await;
    assert_eq!(result["result"]["status"], 1, "{result}");
    assert_eq!(thermal_setters(&rig), custom_setters(1, 2));
    rig.stop().await;
}

#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn a_fan_return_applies_the_default_levels_for_a_saved_custom_without_them() {
    let r = rig_at(0, 0, FULL, custom_without_levels_files());
    *r.custody.origin_for_fan.lock().unwrap() = Some(r.origin.clone());
    r.get().await;
    let owned = r.request(fan_request(230, Some(1), 3700)).await;
    assert_eq!(owned["result"]["status"], 1, "{owned}");
    let out = r.request(fan_request(232, None, 0)).await;
    assert_eq!(out["result"]["status"], 1, "{out}");
    let setters = thermal_setters(&r);
    assert_eq!(setters[setters.len() - 4..], custom_setters(1, 2)[..]);
    {
        let s = r.port.state.lock().unwrap();
        assert_eq!(
            (s.selectors, s.fans, s.custom_levels),
            ([4; 2], [0; 2], [1, 2])
        );
    }
    r.origin.stop();
    r.stop().await;
}

#[test]
fn saved_custom_settings_resolve_only_when_canonical() {
    use crate::installed::readiness::thermal_storage::custom_destination;
    // An absent maximumFans flag is false; a null one is not a flag at all,
    // as managed's non-nullable `MaximumFans` cannot read it.
    assert!(custom_destination(&json!({"cpu":1,"gpu":0})).is_some());
    assert_eq!(
        custom_destination(&json!({"cpu":1,"gpu":0,"maximumFans":false})),
        custom_destination(&json!({"cpu":1,"gpu":0}))
    );
    for settings in [
        json!({"cpu":1,"gpu":0,"maximumFans":null}),
        json!({"cpu":1,"gpu":0,"maximumFans":true}),
        json!({"cpu":2,"gpu":2}),
        json!({"cpu":1}),
    ] {
        assert!(custom_destination(&settings).is_none(), "{settings}");
    }
}

#[tokio::test]
async fn unsupported_custom_intent_is_stored_or_rejected_without_a_setter() {
    const NO_POLICY: &str = "Performance override intent was stored. Hardware result: No supported performance destination is selected by the current power and profile policy.";
    // High/High, an unreviewed CPU level and maximum fans are outside the
    // RZ09-0581 range. Custom is an ordinary command: on battery managed
    // rejects it before storing it (above).
    for (request, actual) in [
        (custom(2, 2, false), FULL),
        (custom(3, 0, false), FULL),
        (custom(1, 0, true), LIMITED),
    ] {
        let rig = rig_at(2, 0, actual, MemoryFiles::default());
        let result = rig.set(1, request.clone()).await;
        assert_eq!(result["result"]["status"], 2, "{request}: {result}");
        assert_eq!(result["result"]["message"], NO_POLICY, "{request}");
        assert!(thermal_setters(&rig).is_empty());
        assert!(rows(&rig).is_empty());
        let status = rig.get().await;
        assert_eq!(status["status"]["performanceOverride"], request);
        rig.stop().await;
    }
    // Unclassified external power rejects an unsupported Custom intent before
    // it is stored, as managed `ValidateUnclassifiedPerformanceOverride` does.
    for request in [custom(2, 2, false), custom(1, 0, true)] {
        let rig = rig_at(2, 0, 0x10, MemoryFiles::default());
        let result = rig.set(1, request.clone()).await;
        assert_eq!(result["result"]["status"], 3, "{request}: {result}");
        assert_eq!(
            result["result"]["message"],
            "The requested intent was rejected: The complete Custom CPU/GPU and maximum-fan intent is not admitted for this model."
        );
        assert!(thermal_setters(&rig).is_empty());
        assert!(rig.files.read("manual-overrides.json").unwrap().is_none());
        rig.stop().await;
    }
    // Custom GPU clock offsets have no native writer.
    let mut request = custom(1, 0, false);
    request["customPerformance"]["gpuClockOffsets"] =
        json!({"coreMegahertz":10,"memoryMegahertz":20});
    let rig = rig_at(2, 0, FULL, MemoryFiles::default());
    let result = rig.set(1, request).await;
    assert_eq!(result["result"]["status"], 2, "{result}");
    assert!(
        result["result"]["message"]
            .as_str()
            .unwrap()
            .ends_with("GPU clock handling is incomplete; no thermal setter was submitted."),
        "{result}"
    );
    assert!(thermal_setters(&rig).is_empty());
    rig.stop().await;
    // The request codec reads negative zero as the integer 0, so the stored
    // intent is canonical.
    let mut request = custom(1, 0, false);
    request["customPerformance"]["gpuClockOffsets"] =
        serde_json::from_str(r#"{"coreMegahertz":-0,"memoryMegahertz":20}"#).unwrap();
    let rig = rig_at(2, 0, FULL, MemoryFiles::default());
    let result = rig.set(1, request).await;
    assert_eq!(result["result"]["status"], 2, "{result}");
    assert!(thermal_setters(&rig).is_empty());
    let saved: Value =
        serde_json::from_slice(&rig.files.read("manual-overrides.json").unwrap().unwrap()).unwrap();
    assert_eq!(
        saved["overrides"]["customPerformance"]["gpuClockOffsets"].to_string(),
        r#"{"coreMegahertz":0,"memoryMegahertz":20}"#
    );
    rig.stop().await;
}

#[tokio::test]
async fn a_level_readback_mismatch_fences_the_row_without_replay() {
    // The level setters are acknowledged but not applied, so the final 0D/87
    // read disagrees: managed reports an indeterminate result and stops.
    let files = MemoryFiles::default();
    let rig = rig_at(2, 0, FULL, files.clone());
    let before = {
        let mut s = rig.port.state.lock().unwrap();
        s.ignore_levels = true;
        s.custom_levels
    };
    assert_ne!(before, [2, 1]);
    let result = rig.set(1, custom(2, 1, false)).await;
    assert_eq!(result["result"]["status"], 2, "{result}");
    assert_eq!(result["result"]["message"], PENDING);
    assert_eq!(thermal_setters(&rig), custom_setters(2, 1));
    assert_eq!(progress(&rig), CUSTOM_CHAIN[..9]);
    // The requested levels never become a confirmation.
    let status = rig.get().await;
    assert!(status["status"]["confirmedState"].is_null(), "{status}");
    assert_eq!(rig.port.state.lock().unwrap().custom_levels, before);
    assert_eq!(rig.set(2, custom(2, 1, false)).await["result"]["status"], 2);
    assert_eq!(thermal_setters(&rig).len(), 4);
    rig.stop().await;
    let restarted = rig_at(2, 0, FULL, files);
    assert_eq!(
        restarted.set(3, custom(2, 1, false)).await["result"]["status"],
        2
    );
    assert!(thermal_setters(&restarted).is_empty());
    restarted.stop().await;
}

#[tokio::test]
async fn each_level_setter_failure_fences_restart_without_replay() {
    // (cut, last journal row, setters written).
    for (cut, last, writes) in [
        ("third-write", 7, 3),
        ("fourth-write", 9, 4),
        ("third-ack", 7, 3),
        ("fourth-ack", 9, 4),
    ] {
        let files = MemoryFiles::default();
        let rig = rig_at(2, 0, FULL, files.clone());
        {
            let mut s = rig.port.state.lock().unwrap();
            match cut {
                "third-write" => s.fail_write = Some(3),
                "fourth-write" => s.fail_write = Some(4),
                "third-ack" => s.corrupt_ack = Some(3),
                _ => s.corrupt_ack = Some(4),
            }
        }
        let result = rig.set(1, custom(1, 2, false)).await;
        assert_eq!(result["result"]["status"], 2, "{cut}: {result}");
        assert_eq!(thermal_setters(&rig).len(), writes, "{cut}");
        assert_eq!(rows(&rig).last().unwrap()["progress"], last, "{cut}");
        rig.stop().await;
        assert!(
            Storage::open(Box::new(files.clone()), SID)
                .unwrap()
                .pending()
        );
        let restarted = rig_at(2, 0, FULL, files);
        let retry = restarted.set(2, custom(1, 2, false)).await;
        assert_eq!(retry["result"]["status"], 2, "{cut}: {retry}");
        assert!(thermal_setters(&restarted).is_empty(), "{cut}");
        restarted.stop().await;
    }
}

#[tokio::test]
async fn a_class_change_during_custom_stops_before_the_next_setter() {
    // Full to limited keeps ordinary continuity; leaving HyperBoost pins the
    // starting class, and battery never admits Custom.
    for (from, changed, completes) in [(2, LIMITED, true), (2, BATTERY, false), (7, LIMITED, false)]
    {
        // Power read 1 is the initial observation; reads 2-5 precede setters 1-4.
        for at in 2..=5 {
            let rig = rig_at(from, 0, FULL, MemoryFiles::default());
            rig.port.state.lock().unwrap().change_power = Some((at, changed));
            let result = rig.set(1, custom(1, 1, false)).await;
            let case = format!("from {from} -> {changed} at read {at}: {result}");
            let written = thermal_setters(&rig).len();
            if completes {
                assert_eq!(result["result"]["status"], 1, "{case}");
                assert_eq!(written, 4, "{case}");
            } else {
                assert_eq!(result["result"]["status"], 2, "{case}");
                // Read `at` precedes setter `at - 1`: every earlier setter was
                // acknowledged, and a stop before the first one retires the row.
                assert_eq!(written, at - 2, "{case}");
                assert_eq!(
                    rows(&rig).last().unwrap()["progress"],
                    [6, 2, 4, 8][at - 2],
                    "{case}"
                );
                if written == 0 {
                    rig.stop().await;
                    continue;
                }
                // The open row fences the next request.
                rig.port.state.lock().unwrap().change_power = None;
                assert_eq!(rig.set(2, custom(1, 1, false)).await["result"]["status"], 2);
                assert_eq!(thermal_setters(&rig).len(), written, "{case}");
            }
            rig.stop().await;
        }
    }
}

#[tokio::test]
async fn profile_custom_uses_the_application_levels_when_it_names_them() {
    // Managed `ApplicationProfileResolution`: an application's Custom levels
    // replace the power profile's, whichever of the two selected Custom. The
    // fixture's plugged-in profile carries Medium/High.
    for (app, levels) in [
        (None, Some((1, 2))),
        (
            Some(json!({"performance":5,"customPerformance":{"cpu":0,"gpu":1}})),
            Some((0, 1)),
        ),
        (
            Some(json!({"customPerformance":{"cpu":2,"gpu":0}})),
            Some((2, 0)),
        ),
        // High/High is outside the RZ09-0581 range: nothing is selected.
        (Some(json!({"customPerformance":{"cpu":2,"gpu":2}})), None),
    ] {
        let files = MemoryFiles::default();
        match &app {
            Some(app) => files.profiles_custom(app.clone()),
            None => files.profiles(CUSTOM),
        }
        let rig = rig_at(2, 0, FULL, files);
        if app.is_some() {
            report_game(&rig).await;
        }
        let result = rig.set(1, Value::Null).await;
        let case = format!("{app:?}: {result}");
        match levels {
            Some((cpu, gpu)) => {
                assert_eq!(result["result"]["status"], 1, "{case}");
                assert_eq!(thermal_setters(&rig), custom_setters(cpu, gpu), "{case}");
                // A policy-selected row carries no request and no levels.
                let rows = rows(&rig);
                assert!(rows.iter().all(|r| r["destination"] == CUSTOM), "{case}");
                assert!(rows.iter().all(|r| r["requested"].is_null()), "{case}");
                let status = rig.get().await;
                assert_eq!(
                    status["status"]["confirmedState"]["customPerformance"],
                    json!({"cpu":cpu,"gpu":gpu,"maximumFans":false}),
                    "{case}"
                );
            }
            None => {
                assert_eq!(result["result"]["status"], 2, "{case}");
                assert!(thermal_setters(&rig).is_empty(), "{case}");
            }
        }
        rig.stop().await;
    }
}

#[tokio::test]
async fn custom_reserves_exactly_its_complete_chain_before_prepared() {
    // The reservation covers all ten rows, the two-digit level steps included.
    for request in [custom(1, 0, false), value(SILENT.0, 1)] {
        let rig = rig_at(2, 0, FULL, MemoryFiles::default());
        assert_eq!(rig.set(1, request.clone()).await["result"]["status"], 1);
        let journal = rig.files.read("thermal-mutation.journal").unwrap().unwrap();
        assert_eq!(
            rig.files.last_reservation.load(Ordering::Acquire),
            journal.len(),
            "{request}"
        );
        rig.stop().await;
    }
}

#[tokio::test]
async fn a_recipe_runs_within_the_recipe_budget_and_refuses_one_it_cannot_finish() {
    // With a latency the fixture job runs on paused time, so these exchanges
    // cost no wall time. At 150 ms a transfer each exchange takes 330 ms and
    // the Silent recipe's nine take about 3 s: past FRAME_TIMEOUT, inside the
    // 6 s recipe budget.
    let latency = |rig: &Rig, ms| {
        rig.port.state.lock().unwrap().latency = Some(std::time::Duration::from_millis(ms));
    };
    let rig = rig_at(2, 0, FULL, MemoryFiles::default());
    latency(&rig, 150);
    let result = rig.set(1, value(SILENT.0, 1)).await;
    assert_eq!(result["result"]["status"], 1, "{result}");
    assert_eq!(setters(&rig), [vec![1, 1, 5, 0], vec![1, 2, 5, 0]]);
    rig.stop().await;
    // At 500 ms the observation and the 07/8C read take about 3.1 s, and the
    // six remaining exchanges would end past the budget: the saved intent is
    // kept, and the Prepared row is settled as known not submitted.
    let rig = rig_at(2, 0, FULL, MemoryFiles::default());
    latency(&rig, 500);
    let result = rig.set(1, value(SILENT.0, 1)).await;
    assert_eq!(result["result"]["status"], 2, "{result}");
    assert_eq!(
        result["result"]["message"],
        format!(
            "Performance override intent was stored. Hardware result: {}",
            crate::installed::readiness::thermal_mutation::DEADLINE
        )
    );
    assert!(setters(&rig).is_empty());
    assert_eq!(progress(&rig), [json!(0), json!(6)]);
    assert!(
        !Storage::open(Box::new(rig.files.clone()), SID)
            .unwrap()
            .pending()
    );
    assert_eq!(
        rig.files.document("manual-overrides.json")["overrides"]["performance"],
        SILENT.0
    );
    rig.stop().await;
}

#[tokio::test]
async fn one_slow_power_read_refuses_a_custom_recipe_before_its_first_setter() {
    // From a Custom baseline the observation reads the pair and both levels,
    // then 07/8C. With each 07/8C read held 1300 ms on a fast device the mean
    // per exchange stays low enough to start, and the recipe then reached
    // three of its four setters before its deadline. By the pace check two
    // 07/8C reads are slow, so the second longest exchange charges the slow
    // reads left and the recipe is refused before any setter.
    let rig = rig_at(4, 0, FULL, MemoryFiles::default());
    rig.get().await;
    rig.port.state.lock().unwrap().power_latency = Some(std::time::Duration::from_millis(1300));
    let result = rig.set(1, custom(2, 1, false)).await;
    rig.port.state.lock().unwrap().power_latency = None;
    assert_eq!(result["result"]["status"], 2, "{result}");
    assert_eq!(
        result["result"]["message"],
        format!(
            "Performance override intent was stored. Hardware result: {}",
            crate::installed::readiness::thermal_mutation::DEADLINE
        )
    );
    assert!(thermal_setters(&rig).is_empty());
    assert_eq!(progress(&rig), [json!(0), json!(6)]);
    assert!(
        !Storage::open(Box::new(rig.files.clone()), SID)
            .unwrap()
            .pending()
    );
    rig.stop().await;
}

#[tokio::test]
async fn a_slow_journal_append_refuses_a_recipe_before_its_first_setter() {
    // Every journal append the recipe makes takes 1600 ms of fixture time on
    // a healthy device. Paced before the
    // Prepared row, the Silent pair started and split after its first setter
    // ([5, 2]) at the deadline. The Prepared append is timed and charged for
    // each of the five rows left, so the recipe is refused before any setter.
    let rig = rig_at(2, 0, FULL, MemoryFiles::default());
    rig.get().await;
    *rig.files.paced_replace.lock().unwrap() = Some((
        "thermal-mutation.journal",
        std::time::Duration::from_millis(1600),
    ));
    let result = rig.set(1, value(SILENT.0, 1)).await;
    *rig.files.paced_replace.lock().unwrap() = None;
    assert_eq!(result["result"]["status"], 2, "{result}");
    assert_eq!(
        result["result"]["message"],
        format!(
            "Performance override intent was stored. Hardware result: {}",
            crate::installed::readiness::thermal_mutation::DEADLINE
        )
    );
    assert!(setters(&rig).is_empty());
    assert_eq!(progress(&rig), [json!(0), json!(6)]);
    assert!(
        !Storage::open(Box::new(rig.files.clone()), SID)
            .unwrap()
            .pending()
    );
    rig.stop().await;
}

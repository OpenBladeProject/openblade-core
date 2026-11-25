//! Leaving HyperBoost for a battery preset through the installed dispatcher:
//! managed's restore pair (the Balanced bytes), an independent read that must
//! confirm Balanced with Automatic fans, then the battery pair
//! (`ProductionBatteryPort.ExecuteThermalSettersAsync` with its HyperBoost
//! exit actions), journaled as 0, 80-83, 1-4, 5. Every possibly submitted row
//! fences later writes, and nothing is replayed.
use super::*;

const RESTORE: [u64; 10] = [0, 80, 81, 82, 83, 1, 2, 3, 4, 5];
const BATTERY_PRESETS: [(i32, u8); 2] = [BATTERY_SAVER, BATTERY_BALANCED];
/// Battery, a full adapter and a limited (USB-C) adapter.
const SOURCES: [u8; 3] = [BATTERY, FULL, LIMITED];

fn progress_values(rig: &Rig) -> Vec<u64> {
    rows(rig)
        .iter()
        .map(|r| r["progress"].as_u64().unwrap())
        .collect()
}
fn restore_then(selector: u8) -> Vec<Vec<u8>> {
    vec![
        vec![1, 1, 0, 0],
        vec![1, 2, 0, 0],
        vec![1, 1, selector, 0],
        vec![1, 2, selector, 0],
    ]
}
/// The (class, command) exchanges after the baseline observation.
fn exchanges(rig: &Rig, from: usize) -> Vec<(u8, u8)> {
    rig.port.state.lock().unwrap().sent[from..]
        .iter()
        .map(|(class, command, _, _)| (*class, *command))
        .collect()
}
/// The transaction IDs of the exchanges after the baseline observation.
fn ids(rig: &Rig, from: usize) -> Vec<u8> {
    rig.port.state.lock().unwrap().sent[from..]
        .iter()
        .map(|(_, _, _, id)| *id)
        .collect()
}
fn sent_count(rig: &Rig) -> usize {
    rig.port.state.lock().unwrap().sent.len()
}
/// A HyperBoost rig on `actual`, with the baseline observed so the request's
/// own exchanges can be told apart.
async fn hyperboost_rig(actual: u8, files: MemoryFiles) -> Rig {
    let rig = rig_at(7, 0, actual, files);
    rig.get().await;
    rig
}
/// Every later request is fenced and writes nothing, before and after a
/// restart from the same protected files.
async fn fenced(rig: Rig, files: MemoryFiles, preset: i32, case: &str) {
    let writes = setters(&rig).len();
    let retry = rig.set(2, value(preset, 1)).await;
    assert_eq!(retry["result"]["status"], 2, "{case}: {retry}");
    assert_eq!(retry["result"]["message"], PENDING, "{case}");
    assert_eq!(setters(&rig).len(), writes, "{case}");
    rig.stop().await;
    let reopened = Storage::open(Box::new(files.clone()), SID).unwrap();
    assert!(reopened.pending(), "{case}");
    // Firmware may now be anywhere; the restarted Service replays nothing.
    let restarted = rig_at(0, 0, FULL, files);
    let retry = restarted.set(3, value(preset, 1)).await;
    assert_eq!(retry["result"]["status"], 2, "{case}: {retry}");
    assert!(setters(&restarted).is_empty(), "{case}");
    restarted.stop().await;
}

#[tokio::test]
async fn each_battery_preset_is_the_restore_pair_a_balanced_read_then_its_own_pair() {
    for actual in SOURCES {
        for (preset, selector) in BATTERY_PRESETS {
            let rig = hyperboost_rig(actual, MemoryFiles::default()).await;
            let before = sent_count(&rig);
            let result = rig.set(1, value(preset, 1)).await;
            let case = format!("{preset} at {actual}: {result}");
            assert_eq!(result["result"]["status"], 1, "{case}");
            assert_eq!(setters(&rig), restore_then(selector), "{case}");
            // The first 07/8C reading and the request's own pair read precede
            // the recipe; each setter has its own reading, and the restore
            // pair is confirmed by its own independent read.
            let recipe = exchanges(&rig, before);
            let at = recipe.iter().position(|e| *e == (13, 2)).unwrap() - 1;
            assert_eq!(
                recipe[at..],
                [
                    (7, 0x8c),
                    (13, 2),
                    (7, 0x8c),
                    (13, 2),
                    (13, 0x82),
                    (13, 0x82),
                    (7, 0x8c),
                    (13, 2),
                    (7, 0x8c),
                    (13, 2),
                    (13, 0x82),
                    (13, 0x82),
                ],
                "{case}"
            );
            // Every exchange has its own reserved transaction ID: the
            // intermediate read's pair is not the final read's.
            let mut recipe_ids = ids(&rig, before)[at..].to_vec();
            recipe_ids.sort_unstable();
            recipe_ids.dedup();
            assert_eq!(recipe_ids.len(), recipe[at..].len(), "{case}");
            assert_eq!(progress_values(&rig), RESTORE, "{case}");
            assert!(
                rows(&rig).iter().all(|r| r["destination"] == preset),
                "{case}"
            );
            let status = rig.get().await;
            assert_eq!(status["status"]["confirmedState"]["performance"], preset);
            assert_eq!(status["status"]["confirmedState"]["fans"], 0);
            assert!(
                !Storage::open(Box::new(rig.files.clone()), SID)
                    .unwrap()
                    .pending()
            );
            // The reservation covers the whole restore chain, two-digit
            // restore steps included.
            let journal = rig.files.read("thermal-mutation.journal").unwrap().unwrap();
            assert_eq!(
                rig.files.last_reservation.load(Ordering::Acquire),
                journal.len(),
                "{case}"
            );
            rig.stop().await;
        }
    }
}

#[tokio::test]
async fn a_direct_battery_pair_reserves_the_restore_chain_it_may_need() {
    // Storage reserves a battery row's longest legal chain: a direct pair
    // leaves the restore chain's four rows unspent.
    let rig = rig_at(2, 0, FULL, MemoryFiles::default());
    assert_eq!(
        rig.set(1, value(BATTERY_SAVER.0, 1)).await["result"]["status"],
        1
    );
    assert_eq!(progress_values(&rig), [0, 1, 2, 3, 4, 5]);
    let journal = rig.files.read("thermal-mutation.journal").unwrap().unwrap();
    assert!(rig.files.last_reservation.load(Ordering::Acquire) > journal.len());
    rig.stop().await;
}

#[tokio::test]
async fn the_resolved_application_preset_leaving_hyperboost_restores_balanced_first() {
    // A clear under confirmed HyperBoost selects the resolved profile: the
    // active application's Battery Saver now runs the restore sequence.
    let files = MemoryFiles::default();
    files.profiles(1);
    let mut document = files.document("profiles.json");
    document["profiles"]["applicationProfiles"][0]["overrides"]["pluggedIn"] =
        json!({"performance": BATTERY_SAVER.0});
    files.put("profiles.json", document);
    files.commit_configuration();
    let rig = rig_at(7, 0, FULL, files);
    report_game(&rig).await;
    let result = rig.set(1, Value::Null).await;
    assert_eq!(result["result"]["status"], 1, "{result}");
    assert_eq!(setters(&rig), restore_then(BATTERY_SAVER.1));
    assert_eq!(progress_values(&rig), RESTORE);
    let status = rig.get().await;
    assert_eq!(
        status["status"]["confirmedState"]["performance"],
        BATTERY_SAVER.0
    );
    rig.stop().await;
}

#[tokio::test]
async fn each_ambiguous_setter_fences_the_store_without_replay() {
    // (cut, last journal row, setters written). A failed or unconfirmed
    // write may have landed: its intent row stays unsettled.
    for (actual, (preset, _)) in [(BATTERY, BATTERY_SAVER), (FULL, BATTERY_BALANCED)] {
        for (cut, progress, writes) in [
            ("first-restore-write", 80, 1),
            ("second-restore-write", 82, 2),
            ("first-restore-ack", 80, 1),
            ("second-restore-ack", 82, 2),
            ("first-battery-write", 1, 3),
            ("second-battery-write", 3, 4),
            ("first-battery-ack", 1, 3),
            ("second-battery-ack", 3, 4),
        ] {
            let files = MemoryFiles::default();
            let rig = hyperboost_rig(actual, files.clone()).await;
            {
                let mut s = rig.port.state.lock().unwrap();
                let number = writes;
                if cut.ends_with("write") {
                    s.fail_write = Some(number);
                } else {
                    s.corrupt_ack = Some(number);
                }
            }
            let result = rig.set(1, value(preset, 1)).await;
            let case = format!("{preset} at {actual} {cut}: {result}");
            assert_eq!(result["result"]["status"], 2, "{case}");
            assert_eq!(result["result"]["message"], PENDING, "{case}");
            assert_eq!(setters(&rig).len(), writes, "{case}");
            assert_eq!(*progress_values(&rig).last().unwrap(), progress, "{case}");
            {
                let mut s = rig.port.state.lock().unwrap();
                s.fail_write = None;
                s.corrupt_ack = None;
            }
            fenced(rig, files, preset, &case).await;
        }
    }
}

#[tokio::test]
async fn only_a_balanced_automatic_intermediate_read_admits_the_battery_pair() {
    // Managed requires the full state after the restore pair to show
    // Balanced with Automatic fans; HyperBoost still active, a battery preset
    // (the destination's own included), manual fans, Custom or no reading
    // stops with the restore pair's 83 row unsettled. No battery setter is
    // sent and nothing is replayed.
    for (preset, selector) in BATTERY_PRESETS {
        for (name, read) in [
            ("hyperboost", Some((7, 0))),
            ("destination", Some((selector, 0))),
            ("battery-saver", Some((3, 0))),
            ("balanced-manual", Some((0, 1))),
            ("custom", Some((4, 0))),
            ("timeout", None),
            ("malformed", None),
        ] {
            let files = MemoryFiles::default();
            let rig = hyperboost_rig(FULL, files.clone()).await;
            {
                let mut s = rig.port.state.lock().unwrap();
                match (name, read) {
                    (_, Some((selector, fans))) => s.reads_after = Some((2, selector, fans)),
                    ("timeout", _) => s.final_read_timeout = true,
                    _ => s.final_read_malformed = true,
                }
            }
            let result = rig.set(1, value(preset, 1)).await;
            let case = format!("{preset} {name}: {result}");
            assert_eq!(result["result"]["status"], 2, "{case}");
            assert_eq!(result["result"]["message"], PENDING, "{case}");
            assert_eq!(setters(&rig), restore_then(selector)[..2], "{case}");
            assert_eq!(progress_values(&rig), [0, 80, 81, 82, 83], "{case}");
            {
                let mut s = rig.port.state.lock().unwrap();
                s.reads_after = None;
                s.final_read_timeout = false;
                s.final_read_malformed = false;
            }
            fenced(rig, files, preset, &case).await;
        }
    }
}

#[tokio::test]
async fn a_final_mismatch_fences_the_battery_pair_without_replay() {
    for (preset, selector) in BATTERY_PRESETS {
        let files = MemoryFiles::default();
        let rig = hyperboost_rig(BATTERY, files.clone()).await;
        rig.port.state.lock().unwrap().mismatch_after = Some(4);
        let result = rig.set(1, value(preset, 1)).await;
        let case = format!("{preset}: {result}");
        assert_eq!(result["result"]["status"], 2, "{case}");
        assert_eq!(result["result"]["message"], PENDING, "{case}");
        assert_eq!(setters(&rig), restore_then(selector), "{case}");
        assert_eq!(progress_values(&rig), RESTORE[..9], "{case}");
        rig.port.state.lock().unwrap().mismatch_after = None;
        fenced(rig, files, preset, &case).await;
    }
}

#[tokio::test]
async fn a_power_change_mid_exit_continues_only_while_the_battery_preset_is_admitted() {
    // (first class, power read, changed class, Windows change, last row,
    // setters, completes). Power read 2 is the first restore setter's own,
    // 3 the second's, 4 the first battery setter's and 5 the second's. With
    // no Windows change the fixture derives Windows from the adapter, so a
    // changed class is a real unplug or plug-in. Managed admits a battery
    // selector on Windows battery and an agreeing external reading alike
    // (`AdmitOrdinaryThermalCommandAsync`), so a persistent battery preset
    // completes across either; so does every native setter, each admitted
    // by its own fresh reading.
    for (actual, read, changed, windows, progress, writes, completes) in [
        (FULL, 2, BATTERY, None, 5, 4, true),
        (FULL, 3, BATTERY, None, 5, 4, true),
        (FULL, 4, BATTERY, None, 5, 4, true),
        (FULL, 5, BATTERY, None, 5, 4, true),
        (BATTERY, 3, FULL, None, 5, 4, true),
        (BATTERY, 4, FULL, None, 5, 4, true),
        (LIMITED, 4, BATTERY, None, 5, 4, true),
        (FULL, 4, LIMITED, None, 5, 4, true),
        // An adapter reading that disagrees with Windows admits no setter
        // (managed refuses that command), so the exit stops at that setter,
        // the restore setters included, without replay.
        (FULL, 2, FULL, Some(Some(true)), 6, 0, false),
        (FULL, 3, FULL, Some(Some(true)), 81, 1, false),
        (FULL, 4, FULL, Some(Some(true)), 83, 2, false),
        (BATTERY, 4, BATTERY, Some(Some(false)), 83, 2, false),
    ] {
        let files = MemoryFiles::default();
        let rig = hyperboost_rig(actual, files.clone()).await;
        {
            let mut s = rig.port.state.lock().unwrap();
            match windows {
                Some(windows) => s.change_windows = Some((read, windows)),
                None => s.change_power = Some((read, changed)),
            }
        }
        let result = rig.set(1, value(BATTERY_SAVER.0, 1)).await;
        let case = format!("{actual} -> {changed} at read {read} {windows:?}: {result}");
        assert_eq!(setters(&rig).len(), writes, "{case}");
        assert_eq!(*progress_values(&rig).last().unwrap(), progress, "{case}");
        if completes {
            assert_eq!(result["result"]["status"], 1, "{case}");
            rig.stop().await;
        } else if progress == 6 {
            // Nothing was submitted: the Prepared row retires.
            assert_eq!(result["result"]["status"], 2, "{case}");
            assert_eq!(progress_values(&rig), [0, 6], "{case}");
            assert!(
                !Storage::open(Box::new(files.clone()), SID)
                    .unwrap()
                    .pending()
            );
            rig.stop().await;
        } else {
            assert_eq!(result["result"]["status"], 2, "{case}");
            assert_eq!(result["result"]["message"], PENDING, "{case}");
            {
                let mut s = rig.port.state.lock().unwrap();
                s.change_power = None;
                s.change_windows = None;
            }
            fenced(rig, files, BATTERY_SAVER.0, &case).await;
        }
    }
}

#[tokio::test]
async fn an_unclassified_reading_at_any_setter_stops_the_exit_without_replay() {
    // A battery preset never admits unclassified power, and Windows AC
    // presence still holds, so only the setter's own fence admission stops
    // it. Power read 2 is the first restore setter's, 3 the second's and 4
    // the first battery setter's.
    for (read, progress, writes) in [(2, 6, 0), (3, 81, 1), (4, 83, 2)] {
        let files = MemoryFiles::default();
        let rig = hyperboost_rig(FULL, files.clone()).await;
        rig.port.state.lock().unwrap().change_power_status = Some((read, 5));
        let result = rig.set(1, value(BATTERY_SAVER.0, 1)).await;
        let case = format!("read {read}: {result}");
        assert_eq!(result["result"]["status"], 2, "{case}");
        assert_eq!(setters(&rig).len(), writes, "{case}");
        assert_eq!(*progress_values(&rig).last().unwrap(), progress, "{case}");
        rig.port.state.lock().unwrap().change_power_status = None;
        if writes == 0 {
            assert!(
                !Storage::open(Box::new(files.clone()), SID)
                    .unwrap()
                    .pending(),
                "{case}"
            );
            rig.stop().await;
        } else {
            assert_eq!(result["result"]["message"], PENDING, "{case}");
            fenced(rig, files, BATTERY_SAVER.0, &case).await;
        }
    }
}

#[tokio::test]
async fn a_windows_change_between_a_setters_admission_and_its_send_stops_the_exit() {
    // Managed `OrdinarySourceUnchanged` rechecks the Windows source between
    // a command's admission and its SET, so a change there stops that
    // command; only the source a setter was admitted with is held, until
    // its SET is sent. Power read 3 is the second restore setter's and
    // 5 the second battery setter's.
    for (actual, read, windows, progress, writes) in [
        (FULL, 3, Some(true), 81, 1),
        (FULL, 5, Some(true), 2, 3),
        (BATTERY, 3, Some(false), 81, 1),
        (BATTERY, 5, None, 2, 3),
    ] {
        let files = MemoryFiles::default();
        let rig = hyperboost_rig(actual, files.clone()).await;
        rig.port.state.lock().unwrap().change_windows_after_reply = Some((read, windows));
        let result = rig.set(1, value(BATTERY_SAVER.0, 1)).await;
        let case = format!("{actual} at read {read} {windows:?}: {result}");
        assert_eq!(result["result"]["status"], 2, "{case}");
        assert_eq!(result["result"]["message"], PENDING, "{case}");
        assert_eq!(setters(&rig).len(), writes, "{case}");
        assert_eq!(*progress_values(&rig).last().unwrap(), progress, "{case}");
        rig.port.state.lock().unwrap().windows_on_battery = None;
        fenced(rig, files, BATTERY_SAVER.0, &case).await;
    }
}

#[tokio::test]
async fn a_source_change_retires_a_temporary_exit_and_stops_after_the_restore_pair() {
    // A persistent request continues on the new class (above). A temporary
    // override is retired by the source change, full to limited or an
    // unplug, so the request it carried no longer stands: the battery pair
    // is not written and the restore pair's 83 row stays unsettled.
    for changed in [LIMITED, BATTERY] {
        let files = MemoryFiles::default();
        let rig = hyperboost_rig(FULL, files.clone()).await;
        rig.port.state.lock().unwrap().change_power = Some((4, changed));
        let result = rig.set(1, value(BATTERY_SAVER.0, 0)).await;
        let case = format!("{changed}: {result}");
        assert_eq!(result["result"]["status"], 2, "{case}");
        assert_eq!(setters(&rig), restore_then(BATTERY_SAVER.1)[..2], "{case}");
        assert_eq!(progress_values(&rig), [0, 80, 81, 82, 83], "{case}");
        rig.port.state.lock().unwrap().change_power = None;
        rig.port.state.lock().unwrap().actual = FULL;
        fenced(rig, files, BATTERY_SAVER.0, &case).await;
    }
}

#[tokio::test]
async fn a_restore_recipe_its_pace_cannot_finish_is_refused_before_any_setter() {
    // At 200 ms a transfer (paused time, so deterministic) a direct battery
    // pair ends inside the operation budget and the restore recipe cannot,
    // so it is refused at the pace check before its first intent row, and
    // the Prepared row retires as known not submitted. A pace that left out
    // the restore pair's intermediate read would start the recipe at this
    // latency and overrun it with every setter submitted.
    let latency = |rig: &Rig| {
        rig.port.state.lock().unwrap().latency = Some(std::time::Duration::from_millis(200));
    };
    let rig = rig_at(2, 0, FULL, MemoryFiles::default());
    latency(&rig);
    let result = rig.set(1, value(BATTERY_SAVER.0, 1)).await;
    assert_eq!(result["result"]["status"], 1, "{result}");
    rig.stop().await;
    let rig = rig_at(7, 0, FULL, MemoryFiles::default());
    latency(&rig);
    let result = rig.set(1, value(BATTERY_SAVER.0, 1)).await;
    assert_eq!(result["result"]["status"], 2, "{result}");
    assert_eq!(
        result["result"]["message"],
        format!(
            "Performance override intent was stored. Hardware result: {}",
            crate::installed::readiness::thermal_mutation::DEADLINE
        )
    );
    assert!(setters(&rig).is_empty());
    assert_eq!(progress_values(&rig), [0, 6]);
    assert!(
        !Storage::open(Box::new(rig.files.clone()), SID)
            .unwrap()
            .pending()
    );
    rig.stop().await;
}

#[tokio::test]
async fn the_restore_recipe_charges_all_four_setters_rows_once() {
    // Every journal append takes `append` of fixture time (paused, so
    // deterministic) on a healthy device. The pace check charges the timed
    // Prepared append for each row left: two per setter, all four setters
    // of the restore recipe, plus the closing row. At 450 ms the restore
    // recipe fits and completes; checked again before the battery pair, the
    // whole recipe would no longer fit, so it is paced only once. At 600 ms
    // it is refused before any setter, while a direct battery pair, with
    // two setters' rows, still fits.
    for (from, append, completes) in [(7, 450, true), (7, 600, false), (0, 600, true)] {
        let rig = rig_at(from, 0, FULL, MemoryFiles::default());
        rig.get().await;
        *rig.files.paced_replace.lock().unwrap() = Some((
            "thermal-mutation.journal",
            std::time::Duration::from_millis(append),
        ));
        let result = rig.set(1, value(BATTERY_SAVER.0, 1)).await;
        *rig.files.paced_replace.lock().unwrap() = None;
        let case = format!("{from} at {append} ms: {result}");
        if completes {
            assert_eq!(result["result"]["status"], 1, "{case}");
            let chain = if from == 7 {
                &RESTORE[..]
            } else {
                &[0, 1, 2, 3, 4, 5][..]
            };
            assert_eq!(progress_values(&rig), chain, "{case}");
        } else {
            assert_eq!(
                result["result"]["message"],
                format!(
                    "Performance override intent was stored. Hardware result: {}",
                    crate::installed::readiness::thermal_mutation::DEADLINE
                ),
                "{case}"
            );
            assert!(setters(&rig).is_empty(), "{case}");
            assert_eq!(progress_values(&rig), [0, 6], "{case}");
        }
        rig.stop().await;
    }
}

#[tokio::test]
async fn the_restore_recipe_reserves_every_id_before_prepared() {
    // The fewest free pairs each recipe completes with: the restore recipe
    // needs three pairs more than a direct battery pair (the restore pair,
    // its readings and its intermediate read). One pair fewer stores the
    // intent and journals nothing; with fewer still the baseline reads
    // cannot run and nothing is accepted.
    let mut least = Vec::new();
    for from in [2, 7] {
        let (mut fewest, mut last) = (None, None);
        for keep in 1..=12 {
            let rig = rig_at(from, 0, FULL, MemoryFiles::default());
            rig.get().await;
            let free = rig.port.free_transactions();
            rig.port.reserve_for_tests(free / 2 - keep);
            let result = rig.set(1, value(BATTERY_SAVER.0, 1)).await;
            let message = result["result"]["message"].as_str().unwrap().to_owned();
            if result["result"]["status"] == 1 {
                fewest = Some(keep);
                rig.stop().await;
                break;
            }
            assert!(setters(&rig).is_empty(), "{from} {keep}: {message}");
            assert!(rows(&rig).is_empty(), "{from} {keep}: {message}");
            let stored = rig.files.read("manual-overrides.json").unwrap().is_some();
            last = Some((message, stored));
            rig.stop().await;
        }
        // One pair short: stored, refused on capacity, nothing journaled.
        let (message, stored) = last.unwrap();
        assert!(
            message.contains("transaction ID capacity"),
            "{from}: {message}"
        );
        assert!(stored, "{from}");
        least.push(fewest.unwrap());
    }
    assert_eq!(least[1], least[0] + 3, "{least:?}");
}

#[tokio::test]
async fn a_firmware_exit_before_the_request_is_one_direct_battery_pair() {
    // Managed decides the plan from the port's own fresh read: once firmware
    // has left HyperBoost (for Balanced or a battery preset), the request is
    // the direct battery pair, with no restore pair.
    for (from, actual) in [(0, FULL), (6, BATTERY), (3, BATTERY)] {
        let rig = rig_at(from, 0, actual, MemoryFiles::default());
        let result = rig.set(1, value(BATTERY_SAVER.0, 1)).await;
        if from == 3 {
            assert_eq!(result["result"]["status"], 1, "{result}");
            assert!(setters(&rig).is_empty());
        } else {
            assert_eq!(result["result"]["status"], 1, "{result}");
            assert_eq!(setters(&rig), restore_then(3)[2..], "{from}");
            assert_eq!(progress_values(&rig), [0, 1, 2, 3, 4, 5]);
        }
        rig.stop().await;
    }
}

#[tokio::test]
async fn an_unconfirmed_restore_intent_append_reports_a_possible_submission() {
    // Row 80 is the exit's first submission intent. An append whose
    // publication is not confirmed (torn pending, whole pending, published
    // with a failed final sync) may already be durable, so the store keeps
    // the possible submission, not a Prepared-only row: no setter is sent,
    // and a later request is fenced with the same reason.
    for cut in 1..=3 {
        let files = MemoryFiles::default();
        files.fail_first_intent.store(cut, Ordering::Release);
        let rig = hyperboost_rig(FULL, files).await;
        let result = rig.set(1, value(BATTERY_SAVER.0, 1)).await;
        assert_eq!(result["result"]["status"], 2, "{cut}: {result}");
        let message = result["result"]["message"].as_str().unwrap();
        assert!(
            message.contains("submission-intent journal publication could not be confirmed"),
            "{cut}: {message}"
        );
        assert!(setters(&rig).is_empty(), "{cut}");
        let attempted = rig
            .files
            .first_intent_bytes
            .lock()
            .unwrap()
            .clone()
            .unwrap();
        let last = attempted
            .split(|b| *b == b'\n')
            .rfind(|line| !line.is_empty())
            .map(|line| serde_json::from_slice::<Value>(line).unwrap());
        assert_eq!(last.unwrap()["progress"], 80, "{cut}");
        let later = rig.set(2, value(BATTERY_SAVER.0, 1)).await;
        assert_eq!(
            later["result"]["message"], result["result"]["message"],
            "{cut}"
        );
        assert!(setters(&rig).is_empty(), "{cut}");
        let _ = rig.reader.stop().await;
    }
}

#[cfg(feature = "service-profile-triggers-v1")]
async fn observe(rig: &Rig, n: u8) -> Value {
    rig.request(json!({"operation":"observeState","version":90,"requestId":format!("eeeeeeee-bbbb-cccc-dddd-{n:012}"),"includeLiveReadings":false,"requestedLiveReadings":0,"effectiveLiveReadings":0}))
        .await
}
#[cfg(feature = "service-profile-triggers-v1")]
#[tokio::test]
async fn a_stop_after_the_restore_pair_reads_balanced_as_openblades_own_write() {
    // The restore pair writes Balanced, so its intents (80, 82) name Balanced
    // for drift attribution. Firmware read Performance, then entered
    // HyperBoost, which reports no preset. After a stop at 83 a Balanced
    // reading is OpenBlade's own write, which a battery-preset attribution
    // would report as a change outside OpenBlade; Silent set outside
    // OpenBlade is one.
    for (outside, changes) in [(None, json!([])), (Some([5; 2]), json!([0]))] {
        let rig = rig_at(2, 0, FULL, MemoryFiles::default());
        rig.get().await;
        let first = observe(&rig, 1).await;
        assert_eq!(first["success"], true, "{first}");
        {
            let mut s = rig.port.state.lock().unwrap();
            s.selectors = [7; 2];
            s.change_windows = Some((4, Some(true)));
        }
        let result = rig.set(1, value(BATTERY_SAVER.0, 1)).await;
        assert_eq!(result["result"]["status"], 2, "{outside:?}: {result}");
        assert_eq!(progress_values(&rig), [0, 80, 81, 82, 83], "{outside:?}");
        {
            let mut s = rig.port.state.lock().unwrap();
            assert_eq!(s.selectors, [0; 2], "{outside:?}");
            s.windows_on_battery = None;
            if let Some(selectors) = outside {
                s.selectors = selectors;
            }
        }
        let observed = observe(&rig, 2).await;
        assert_eq!(observed["success"], true, "{outside:?}: {observed}");
        let status = rig.get().await;
        assert_eq!(
            status["status"]["externalChanges"], changes,
            "{outside:?}: {status}"
        );
        rig.stop().await;
    }
}

#[cfg(all(
    feature = "service-profile-triggers-v1",
    feature = "service-fan-mutation-v2"
))]
#[tokio::test]
async fn the_low_power_gate_still_stores_a_hyperboost_battery_request_without_reading() {
    use crate::installed::readiness::thermal_mutation::LOW_POWER_PERFORMANCE;
    use windows_sys::Win32::System::Services::SERVICE_CONTROL_POWEREVENT as POWER;
    const SUSPEND: u32 = 4;
    let rig = rig_at(7, 0, BATTERY, MemoryFiles::default());
    assert_eq!(rig.get().await["status"]["activePowerSource"], 1);
    let custody = rig.custody.clone();
    rig.runtime
        .read_low_power(move || custody.lifecycle.low_power());
    rig.custody.scoped_fresh.store(true, Ordering::Release);
    rig.custody.lifecycle.ingress(POWER, SUSPEND);
    assert!(rig.custody.lifecycle.low_power_or_unknown());
    let before = sent_count(&rig);
    let result = rig.set(1, value(BATTERY_SAVER.0, 1)).await;
    assert_eq!(
        result["result"],
        json!({"status":2,"message":format!("Performance override intent was stored. Hardware result: {LOW_POWER_PERFORMANCE}")}),
        "{result}"
    );
    assert_eq!(sent_count(&rig), before);
    assert_eq!(rig.port.state.lock().unwrap().writes, 0);
    assert!(rows(&rig).is_empty());
    rig.custody.scoped_fresh.store(false, Ordering::Release);
    rig.stop().await;
}

const VECTORS: &str =
    include_str!("../../../../../tests/data/hyperboost-battery-exit-vectors.json");

/// The native cut for a managed row: the port stage that failed, with the
/// laptop left either in HyperBoost (prior) or in Balanced (the state the
/// restore pair leaves). Native has no state where the intermediate read
/// fails to confirm and Balanced reads back, so that row cuts the
/// intermediate read itself.
fn managed_cut(rig: &Rig, case: &str, readback: Option<&str>) -> usize {
    let mut s = rig.port.state.lock().unwrap();
    match (case, readback) {
        ("success", None) => 4,
        ("restoreSetter", Some("prior")) => {
            s.fail_write = Some(1);
            1
        }
        ("restoreSetter", Some("balanced")) => {
            s.corrupt_ack = Some(2);
            2
        }
        ("intermediateMismatch", Some("prior")) => {
            s.reads_after = Some((2, 7, 0));
            2
        }
        ("intermediateMismatch", Some("balanced")) => {
            s.final_read_timeout = true;
            2
        }
        ("batterySetter", Some("prior")) => {
            s.fail_write = Some(3);
            3
        }
        ("batterySetter", Some("balanced")) => {
            s.corrupt_ack = Some(4);
            4
        }
        ("finalMismatch", _) => {
            s.mismatch_after = Some(4);
            4
        }
        other => panic!("unrecorded managed case {other:?}"),
    }
}

#[tokio::test]
async fn managed_hyperboost_battery_exit_vectors_replay_through_the_dispatcher() {
    // Recorded by ServiceControlRuntimeTests.NativeHyperBoostBatteryExitVectors.
    // Success matches managed's status and setter sequence. Every failure
    // stage is Failed in managed, which then reconciles from its own fresh
    // readback and leaves nothing pending; native replies Blocked with the
    // reconciliation reason and fences later writes without replay (the
    // existing native deviation for an uncertain submission).
    let vectors: Value = serde_json::from_str(VECTORS).unwrap();
    let replies = vectors["replies"].as_array().unwrap();
    assert_eq!(replies.len(), 3 * 2 * 9);
    for row in replies {
        let actual = match row["source"].as_str().unwrap() {
            "Battery" => BATTERY,
            "PluggedIn" => FULL,
            "UsbC" => LIMITED,
            other => panic!("{other}"),
        };
        let preset = row["preset"].as_i64().unwrap() as i32;
        let (_, selector) = BATTERY_PRESETS
            .into_iter()
            .find(|(p, _)| *p == preset)
            .unwrap();
        let case = row["case"].as_str().unwrap();
        let files = MemoryFiles::default();
        let rig = hyperboost_rig(actual, files.clone()).await;
        let writes = managed_cut(&rig, case, row["readback"].as_str());
        let result = rig.set(1, value(preset, 1)).await;
        let name = format!("{row}: {result}");
        assert_eq!(row["writesBlocked"], false, "{name}");
        assert_eq!(row["unconfirmed"], json!([]), "{name}");
        assert_eq!(row["hyperBoostRestorePending"], false, "{name}");
        assert_eq!(row["performanceWrites"], 1, "{name}");
        assert_eq!(setters(&rig), restore_then(selector)[..writes], "{name}");
        if case == "success" {
            assert_eq!(row["status"], 1, "{name}");
            assert_eq!(result["result"]["status"], row["status"], "{name}");
            assert_eq!(progress_values(&rig), RESTORE, "{name}");
            rig.stop().await;
            continue;
        }
        let message = row["message"].as_str().unwrap();
        assert_eq!(row["status"], 3, "{name}");
        assert!(
            message.starts_with(
                "Performance override intent was stored. Hardware result: Performance write is indeterminate: "
            ),
            "{name}"
        );
        assert_eq!(result["result"]["status"], 2, "{name}");
        assert_eq!(result["result"]["message"], PENDING, "{name}");
        {
            let mut s = rig.port.state.lock().unwrap();
            s.fail_write = None;
            s.corrupt_ack = None;
            s.reads_after = None;
            s.final_read_timeout = false;
            s.mismatch_after = None;
        }
        fenced(rig, files, preset, &name).await;
    }
}

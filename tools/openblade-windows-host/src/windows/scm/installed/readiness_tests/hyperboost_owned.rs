//! Owned HyperBoost through the installed dispatcher (`setHyperBoost`):
//! pad-less entry with the `hyperboost-enable` pair from a confirmed admitted
//! state, settled by an independent read of HyperBoost with Automatic fans,
//! and the exit to saved intent through the direct or restore recipe
//! (managed `SetHyperBoostCoreAsync`). Managed's HyperBoost journal and the
//! cooling pad are read-only inputs; a refusal stores and writes nothing, and
//! a possibly submitted row fences later writes without replay.
use super::*;
use crate::installed::readiness::thermal_mutation::Pad;

const ENABLE: [[u8; 4]; 2] = [[1, 1, 7, 0], [1, 2, 7, 0]];
const PAIR: [u64; 6] = [0, 1, 2, 3, 4, 5];
const ALREADY_ACTIVE: &str = "HyperBoost is already active.";
const ALREADY_OFF: &str = "HyperBoost is already off.";
const ENTERED: &str = "HyperBoost is active through the admitted pad-less path; no cooling pad is required for this owned session.";
const EXTERNAL_POWER: &str = "HyperBoost requires a confirmed external power source.";
const ENTRY_BASE: &str = "HyperBoost requires confirmed Balanced performance with Automatic fans, or admitted direct entry from manual fan control.";
const PAD_ABSENCE: &str = "Pad-less HyperBoost requires a fresh observation confirming that no target cooling-pad device is present. Ambiguous or failed enumeration is blocked.";
const PAD_PRESENT: &str = "HyperBoost with the cooling pad connected is not available in this build. Disconnect the cooling pad to use pad-less HyperBoost.";
const JOURNAL_PENDING: &str = "HyperBoost recovery from the previous OpenBlade service is pending. Start that service once so it can finish, then try again.";
#[cfg(feature = "service-fan-mutation-v2")]
const FAN_HANDBACK_EXIT: &str = "HyperBoost was not turned off because OpenBlade still owns an earlier fan change. The fans are already on firmware Automatic. Restart OpenBlade to hand them back, then turn HyperBoost off.";

impl Rig {
    async fn hyperboost(&self, n: u8, enabled: bool) -> Value {
        let response = self
            .request(json!({"operation":"setHyperBoost","version":90,"requestId":format!("aaaaaaaa-bbbb-cccc-eeee-{n:012}"),"enabled":enabled}))
            .await;
        assert!(response["status"].is_null(), "{response}");
        assert!(!response["result"].is_null(), "{response}");
        response
    }
}
fn progress(rig: &Rig) -> Vec<u64> {
    rows(rig)
        .iter()
        .map(|r| r["progress"].as_u64().unwrap())
        .collect()
}
fn hyperboost_active(status: &Value) -> &Value {
    &status["status"]["confirmedState"]["hyperBoostActive"]
}
/// A committed saved aggregate with Balanced on both adapters, which the
/// entry restores and the exit writes.
fn saved_files() -> MemoryFiles {
    let files = MemoryFiles::default();
    files.profiles(1);
    files
}
fn enable() -> Vec<Vec<u8>> {
    ENABLE.iter().map(|p| p.to_vec()).collect()
}
const JOURNAL_VECTORS: &str =
    include_str!("../../../../../tests/data/hyperboost-journal-vectors.json");
/// The Relinquished journal managed's store writes, with `state` in place
/// of Relinquished (`NativeHyperBoostJournalVectorsTests`).
fn journal_in(state: u8) -> Vec<u8> {
    let vectors: Value = serde_json::from_str(JOURNAL_VECTORS).unwrap();
    let written = vectors["rows"][0]["document"].as_str().unwrap();
    assert_eq!(vectors["rows"][0]["name"], "written/balanced");
    written
        .replace("\"state\": 3,", &format!("\"state\": {state},"))
        .into_bytes()
}
/// A Relinquished managed journal of the latest schema
/// (`IsSafeMaintainHyperBoostJournalBaseline`).
fn relinquished() -> Vec<u8> {
    journal_in(3)
}

/// Managed 7044-7084 and the port's entry precondition on exact RZ09-0581:
/// Silent, Balanced, Performance and Custom with Automatic fans, and every
/// manual-fan state, enter directly with the enable pair on either classified
/// adapter. A battery preset with Automatic fans is no admitted base, and
/// HyperBoost itself is already active; neither prepares or writes anything.
#[tokio::test]
async fn entry_is_the_enable_pair_from_every_admitted_confirmed_state() {
    for actual in [FULL, LIMITED] {
        for (from, fans) in STATES {
            let rig = rig_at(from, fans, actual, saved_files());
            let before = rig.get().await;
            let result = rig.hyperboost(1, true).await;
            let case = format!("({from}, {fans}) at {actual}: {result}");
            let status = rig.get().await;
            match (from, fans) {
                (7, 0) => {
                    assert_eq!(
                        result["result"],
                        json!({"status":1,"message":ALREADY_ACTIVE}),
                        "{case}"
                    );
                    assert!(setters(&rig).is_empty(), "{case}");
                    assert!(rows(&rig).is_empty(), "{case}");
                }
                (3 | 6, 0) => {
                    assert_eq!(
                        result["result"],
                        json!({"status":2,"message":ENTRY_BASE}),
                        "{case}"
                    );
                    assert!(setters(&rig).is_empty(), "{case}");
                    assert!(rows(&rig).is_empty(), "{case}");
                }
                _ => {
                    assert_eq!(
                        result["result"],
                        json!({"status":1,"message":ENTERED}),
                        "{case}"
                    );
                    assert_eq!(setters(&rig), enable(), "{case}");
                    assert_eq!(progress(&rig), PAIR, "{case}");
                    assert!(
                        rows(&rig)
                            .iter()
                            .all(|r| r["destination"] == 100 && r["requested"].is_null()),
                        "{case}"
                    );
                    assert_eq!(hyperboost_active(&status), true, "{case}");
                    assert_eq!(status["status"]["confirmedState"]["fans"], 0, "{case}");
                    // An entry changes no saved intent.
                    assert_eq!(
                        status["status"]["desiredState"]["performance"],
                        before["status"]["desiredState"]["performance"],
                        "{case}"
                    );
                    assert!(
                        !Storage::open(Box::new(rig.files.clone()), SID)
                            .unwrap()
                            .pending(),
                        "{case}"
                    );
                }
            }
            rig.stop_in_hyperboost().await;
        }
    }
}

/// Managed 7134-7140 (`PowerSourcePolicy.IsExternal`): battery refuses the
/// entry before any row, from every state HyperBoost is not active in. The
/// port's base precondition runs after it, so a battery preset with
/// Automatic fans reports external power too.
#[tokio::test]
async fn entry_needs_a_classified_external_adapter() {
    for (from, fans) in STATES.into_iter().filter(|s| *s != (7, 0)) {
        let rig = rig_at(from, fans, BATTERY, saved_files());
        let result = rig.hyperboost(1, true).await;
        assert_eq!(
            result["result"],
            json!({"status":2,"message":EXTERNAL_POWER}),
            "({from}, {fans}): {result}"
        );
        assert!(setters(&rig).is_empty());
        assert!(rows(&rig).is_empty());
        rig.stop().await;
    }
}

/// Managed 7004-7023 and 7142-7159: only the pad-less path is admitted
/// natively, so a connected pad refuses, and so does a failed observation.
/// Neither writes a row.
#[tokio::test]
async fn a_pad_or_a_failed_observation_refuses_entry_before_any_row() {
    for (pad, message) in [
        (Pad::Present, PAD_PRESENT),
        (Pad::ObservationFailed, PAD_ABSENCE),
    ] {
        let rig = rig_at(0, 0, FULL, saved_files());
        *rig.hyperboost.pad.lock().unwrap() = pad;
        let result = rig.hyperboost(1, true).await;
        assert_eq!(
            result["result"],
            json!({"status":2,"message":message}),
            "{pad:?}: {result}"
        );
        assert!(setters(&rig).is_empty());
        assert!(rows(&rig).is_empty());
        rig.stop().await;
    }
}

/// Managed `revalidateEligibilityPerCommand`: the pad is observed again
/// before each enable setter. Before the first nothing is submitted and the
/// Prepared row retires; before the second the first setter's row stays
/// unsettled, fences later writes and nothing is replayed.
#[tokio::test]
async fn the_pad_is_observed_again_before_each_enable_setter() {
    for (later, sent, rows_left, pending) in [
        (vec![Pad::Present], 0, vec![0, 6], false),
        (vec![Pad::ObservationFailed], 0, vec![0, 6], false),
        (vec![Pad::Absent, Pad::Present], 1, vec![0, 1, 2], true),
    ] {
        let rig = rig_at(0, 0, FULL, saved_files());
        let case = format!("{later:?}");
        {
            let mut script = rig.hyperboost.script.lock().unwrap();
            // The request's own observation, then one per setter.
            script.push_back(Pad::Absent);
            script.extend(later);
        }
        let result = rig.hyperboost(1, true).await;
        assert_eq!(result["result"]["status"], 2, "{case}: {result}");
        assert_eq!(setters(&rig), enable()[..sent], "{case}");
        assert_eq!(progress(&rig), rows_left, "{case}");
        let reopened = Storage::open(Box::new(rig.files.clone()), SID).unwrap();
        assert_eq!(reopened.pending(), pending, "{case}");
        if pending {
            assert_eq!(result["result"]["message"], PENDING, "{case}");
            let retry = rig.hyperboost(2, true).await;
            assert_eq!(retry["result"]["status"], 2, "{case}: {retry}");
            assert_eq!(setters(&rig).len(), sent, "{case}");
        } else {
            assert_eq!(result["result"]["message"], PAD_ABSENCE, "{case}");
        }
        rig.stop().await;
    }
}

/// Native writes nothing on managed's behalf: managed's HyperBoost journal,
/// unless absent or Relinquished, refuses entry and exit before any row, and
/// is read again before each setter.
#[tokio::test]
async fn a_pending_or_unreadable_managed_journal_refuses_entry_and_exit() {
    let pending = journal_in(1);
    for (journal, refused) in [
        (Some(Some(pending)), true),
        (None, true),
        (Some(Some(b"{".to_vec())), true),
        (Some(Some(relinquished())), false),
        (Some(None), false),
    ] {
        for (from, enabled) in [(0, true), (7, false)] {
            let rig = rig_at(from, 0, FULL, saved_files());
            *rig.hyperboost.journal.lock().unwrap() = journal.clone();
            let result = rig.hyperboost(1, enabled).await;
            let case = format!("{journal:?} {enabled}: {result}");
            if refused {
                assert_eq!(
                    result["result"],
                    json!({"status":2,"message":JOURNAL_PENDING}),
                    "{case}"
                );
                assert!(setters(&rig).is_empty(), "{case}");
                assert!(rows(&rig).is_empty(), "{case}");
            } else {
                assert_eq!(result["result"]["status"], 1, "{case}");
                assert_eq!(setters(&rig).len(), 2, "{case}");
                // Read for the request and again before each setter.
                assert_eq!(
                    rig.hyperboost.journal_reads.load(Ordering::Acquire),
                    3,
                    "{case}"
                );
            }
            rig.stop_in_hyperboost().await;
        }
    }
    // A journal that becomes pending between the setters stops before the
    // second; the first setter's row fences later writes.
    let rig = rig_at(0, 0, FULL, saved_files());
    let journal = rig.hyperboost.journal.clone();
    let reads = rig.hyperboost.journal_reads.clone();
    let rig_files = rig.files.clone();
    {
        let mut s = rig.port.state.lock().unwrap();
        let journal = journal.clone();
        let previous = s.before_write.take().unwrap();
        // After the first setter's own check, before its send.
        s.before_write = Some(Arc::new(move |write| {
            previous(write);
            if write == 1 {
                *journal.lock().unwrap() = Some(Some(b"{}".to_vec()));
            }
        }));
    }
    let result = rig.hyperboost(1, true).await;
    assert_eq!(result["result"]["message"], PENDING, "{result}");
    assert_eq!(setters(&rig), enable()[..1]);
    assert_eq!(reads.load(Ordering::Acquire), 3);
    assert!(Storage::open(Box::new(rig_files), SID).unwrap().pending());
    rig.stop_in_hyperboost().await;
}

/// Managed's exit restores the saved intent; on RZ09-0581 every saved base is
/// a direct exit, so native writes the saved destination's own pair once,
/// and a battery preset takes the restore recipe. An exit while HyperBoost is
/// off writes nothing.
#[tokio::test]
async fn exit_writes_the_saved_destination_and_already_off_writes_nothing() {
    for (actual, preset, expected) in [
        (FULL, 2, vec![vec![1, 1, 2, 0], vec![1, 2, 2, 0]]),
        (LIMITED, 0, vec![vec![1, 1, 5, 0], vec![1, 2, 5, 0]]),
        (
            BATTERY,
            3,
            vec![
                vec![1, 1, 0, 0],
                vec![1, 2, 0, 0],
                vec![1, 1, 3, 0],
                vec![1, 2, 3, 0],
            ],
        ),
    ] {
        let rig = rig_at(0, 0, actual, saved_files());
        // Save the destination, then let firmware report HyperBoost.
        let saved = rig.set(1, value(preset, 1)).await;
        assert_eq!(saved["result"]["status"], 1, "{saved}");
        let writes = setters(&rig).len();
        {
            let mut s = rig.port.state.lock().unwrap();
            s.selectors = [7; 2];
            s.fans = [0; 2];
        }
        let result = rig.hyperboost(2, false).await;
        let case = format!("{preset} at {actual}: {result}");
        assert_eq!(result["result"]["status"], 1, "{case}");
        assert!(
            result["result"]["message"]
                .as_str()
                .unwrap()
                .starts_with("HyperBoost is off and the prior "),
            "{case}"
        );
        assert_eq!(setters(&rig)[writes..], expected[..], "{case}");
        let status = rig.get().await;
        assert_eq!(hyperboost_active(&status), false, "{case}");
        assert_eq!(status["status"]["confirmedState"]["performance"], preset);
        // Already off: nothing is prepared or written.
        let rows_before = rows(&rig).len();
        let again = rig.hyperboost(3, false).await;
        assert_eq!(
            again["result"],
            json!({"status":1,"message":ALREADY_OFF}),
            "{case}"
        );
        assert_eq!(rows(&rig).len(), rows_before, "{case}");
        assert_eq!(setters(&rig)[writes..], expected[..], "{case}");
        rig.stop().await;
    }
}

const BALANCED_PAIR: [[u8; 4]; 2] = [[1, 1, 0, 0], [1, 2, 0, 0]];
/// Managed's reply when the exit skips the saved intent (7833-7835), with
/// native's reason.
const STAYS_BALANCED: &str =
    "HyperBoost is off and the laptop stays in Balanced until the power source changes. ";

/// Asserts that `result` left HyperBoost through managed's unowned exit
/// alone: the Balanced pair as one durable chain, an independent read of
/// Balanced with Automatic fans, and the saved intent `before` unchanged.
async fn assert_restore_only_exit(
    rig: &Rig,
    before: &Value,
    result: &Value,
    reason: &str,
    case: &str,
) {
    assert_eq!(
        result["result"],
        json!({"status":1,"message":format!("{STAYS_BALANCED}{reason}")}),
        "{case}"
    );
    let sent = setters(rig);
    assert_eq!(
        sent[sent.len() - 2..],
        BALANCED_PAIR.map(|p| p.to_vec()),
        "{case}"
    );
    let exit_rows: Vec<Value> = rows(rig).into_iter().rev().take(PAIR.len()).collect();
    assert!(
        exit_rows
            .iter()
            .all(|r| r["destination"] == 1 && r["requested"].is_null()),
        "{case}: {exit_rows:?}"
    );
    let status = rig.get().await;
    assert_eq!(hyperboost_active(&status), false, "{case}");
    assert_eq!(
        status["status"]["confirmedState"]["performance"], 1,
        "{case}"
    );
    assert_eq!(status["status"]["confirmedState"]["fans"], 0, "{case}");
    assert_eq!(
        status["status"]["desiredState"]["performance"],
        before["status"]["desiredState"]["performance"],
        "{case}"
    );
    assert!(
        !Storage::open(Box::new(rig.files.clone()), SID)
            .unwrap()
            .pending(),
        "{case}"
    );
}

/// Managed 7118-7127: only an entry needs an admitted saved intent. An exit
/// whose saved intent cannot be restored is still submitted: managed writes
/// its unowned exit (the Balanced restore pair), skips the restore step and
/// keeps the saved intent. Natively that is a saved GPU clock effect the exit
/// cannot restore, or no saved destination at the class. The battery class
/// admits that exit (`admits_restore_class`), though the Balanced
/// destination's own rule refuses battery.
#[tokio::test]
async fn an_exit_the_saved_intent_cannot_complete_still_leaves_hyperboost() {
    const GPU: &str = "The saved Performance setting includes GPU clock tuning that this exit can't restore, so it was not applied.";
    const NO_POLICY: &str =
        "No saved performance setting applies to this power source, so none was applied.";
    let gpu = MemoryFiles::default();
    gpu.profiles_gpu(2);
    for (case, files, actual, reason) in [
        ("gpu", gpu, FULL, GPU),
        ("no aggregate", MemoryFiles::default(), FULL, NO_POLICY),
        (
            "no aggregate on battery",
            MemoryFiles::default(),
            BATTERY,
            NO_POLICY,
        ),
    ] {
        let rig = rig_at(7, 0, actual, files);
        let before = rig.get().await;
        let result = rig.hyperboost(1, false).await;
        assert_restore_only_exit(&rig, &before, &result, reason, case).await;
        assert_eq!(setters(&rig).len(), 2, "{case}");
        assert_eq!(progress(&rig), PAIR, "{case}");
        // The saved intent is unchanged, so HyperBoost can be entered again
        // only where entry admits it; an exit already off writes nothing.
        let again = rig.hyperboost(2, false).await;
        assert_eq!(
            again["result"],
            json!({"status":1,"message":ALREADY_OFF}),
            "{case}"
        );
        assert_eq!(setters(&rig).len(), 2, "{case}");
        rig.stop().await;
    }
}

/// Managed's exit admits no ordinary command power, so the restore-only exit
/// continues across a class change between its setters while the new class is
/// classified and agrees with Windows, an unplug to battery included; the
/// saved intent it skipped is not resolved again. An unclassified reading
/// stops it before the next setter, and the sent setter's row fences writes.
#[tokio::test]
async fn the_restore_only_exit_continues_across_a_classified_class_change() {
    const NO_POLICY: &str =
        "No saved performance setting applies to this power source, so none was applied.";
    for (actual, completes) in [(LIMITED, true), (BATTERY, true), (0x10, false)] {
        let rig = rig_at(7, 0, FULL, MemoryFiles::default());
        let before = rig.get().await;
        change(&rig, Adapter(After(1), actual));
        let result = rig.hyperboost(1, false).await;
        let case = format!("{actual}: {result}");
        if completes {
            assert_restore_only_exit(&rig, &before, &result, NO_POLICY, &case).await;
            assert_eq!(setters(&rig).len(), 2, "{case}");
        } else {
            assert_eq!(result["result"]["status"], 2, "{case}");
            assert_eq!(result["result"]["message"], PENDING, "{case}");
            assert_eq!(setters(&rig), vec![BALANCED_PAIR[0].to_vec()], "{case}");
            settle(&rig);
        }
        rig.stop().await;
    }
}

/// An outstanding owned fan change refuses the exit before any row, whatever
/// the saved intent: the durable thermal journal admits no performance row
/// while a fan change this Service owns is outstanding (`thermal_history`
/// refuses it on read), so a restore or a restore-only exit row would leave
/// the journal unreadable and owned Stop unable to hand the fans back. The
/// guard precedes the saved-intent causes a GPU clock effect or a missing
/// destination would otherwise turn into a restore-only exit. The reply says
/// what to do: HyperBoost reads Automatic fans, so owned Stop closes the
/// change without a setter, and after a restart the exit is admitted.
/// Managed has no such journal (a native deviation).
#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn an_owned_fan_change_refuses_the_exit_before_any_row() {
    let saved = MemoryFiles::default();
    saved.profiles_with(1, Some(1));
    let gpu = MemoryFiles::default();
    gpu.profiles_gpu(2);
    for (case, files) in [
        ("saved", saved),
        ("gpu", gpu),
        ("no aggregate", MemoryFiles::default()),
    ] {
        let rig = fan_rig_files(0, 0, files);
        rig.get().await;
        let fan = rig.request(fan_request(180, Some(1), 3700)).await;
        assert_eq!(fan["result"]["status"], 1, "{case}: {fan}");
        assert!(rig.storage.outstanding_fan(), "{case}");
        {
            let mut s = rig.port.state.lock().unwrap();
            s.selectors = [7; 2];
            s.fans = [0; 2];
        }
        let rows_before = rows(&rig).len();
        let sent_before = setters(&rig).len();
        let result = rig.hyperboost(1, false).await;
        assert_eq!(
            result["result"],
            json!({"status":2,"message":FAN_HANDBACK_EXIT}),
            "{case}: {result}"
        );
        assert_eq!(rows(&rig).len(), rows_before, "{case}");
        assert_eq!(setters(&rig).len(), sent_before, "{case}");
        assert!(
            Storage::open(Box::new(rig.files.clone()), SID).is_ok(),
            "{case}"
        );
        // Owned Stop closes the change on the Automatic fans it reads.
        rig.origin.stop();
        rig.stop().await;
        assert_eq!(fan_rows(&rig).last().unwrap()["progress"], 51, "{case}");
        assert!(!rig.storage.outstanding_fan(), "{case}");
        assert!(!rig.storage.pending(), "{case}");
        assert_eq!(setters(&rig).len(), sent_before, "{case}");
        // After the restart the exit is admitted.
        let restarted = rig_at(7, 0, FULL, rig.files.clone());
        let exit = restarted.hyperboost(2, false).await;
        assert_eq!(exit["result"]["status"], 1, "{case}: {exit}");
        assert_eq!(hyperboost_active(&restarted.get().await), false, "{case}");
        restarted.stop().await;
    }
}

/// A readback that does not confirm HyperBoost settles nothing: the
/// unsettled row fences later writes, and nothing is replayed.
#[tokio::test]
async fn an_unconfirmed_entry_fences_later_writes_without_replay() {
    let rig = rig_at(0, 0, FULL, saved_files());
    rig.port.state.lock().unwrap().mismatch = true;
    let result = rig.hyperboost(1, true).await;
    assert_eq!(result["result"]["status"], 2, "{result}");
    assert_eq!(result["result"]["message"], PENDING, "{result}");
    assert_eq!(setters(&rig), enable());
    assert_eq!(progress(&rig), [0, 1, 2, 3, 4]);
    let retry = rig.hyperboost(2, true).await;
    assert_eq!(retry["result"]["status"], 2, "{retry}");
    assert_eq!(setters(&rig), enable());
    rig.stop().await;
}

/// The GPU clock fence holds owned HyperBoost as it holds every native
/// thermal writer: entry and exit are refused before any row or setter.
#[cfg(feature = "service-gpu-clock-tuning-v1")]
#[tokio::test]
async fn a_pending_gpu_confirmation_refuses_entry_and_exit_before_any_row() {
    use crate::installed::readiness::gpu_clock_offsets::fake::Fake;
    use crate::installed::readiness::tests::mutation::gpu_clock_tuning::{
        compose, leave_uncertain,
    };
    use crate::installed::readiness::thermal_mutation::PENDING_PERFORMANCE;
    let gpu = MemoryFiles::default();
    gpu.profiles_gpu(2);
    for (from, enabled, files) in [
        (0, true, saved_files()),
        (7, false, saved_files()),
        (7, false, gpu),
    ] {
        let rig = rig_at(from, 0, FULL, files);
        let fake = Fake::new(0, 0);
        let lane = compose(&rig, &fake, Arc::default());
        leave_uncertain(&rig, &lane, &fake);
        let result = rig.hyperboost(1, enabled).await;
        let case = format!("{enabled}: {result}");
        assert_eq!(result["result"]["status"], 2, "{case}");
        assert_eq!(result["result"]["message"], PENDING_PERFORMANCE, "{case}");
        assert!(setters(&rig).is_empty(), "{case}");
        assert!(rows(&rig).is_empty(), "{case}");
        fake.with(|d| d.queries_fail = false);
        let _ = lane.call_for_tests(|m| {
            m.rearm();
            let _ = m.read();
        });
        let result = rig.hyperboost(2, enabled).await;
        assert_eq!(
            result["result"]["status"], 1,
            "{enabled} resolved: {result}"
        );
        assert_eq!(setters(&rig).len(), 2, "{enabled}");
        rig.stop_in_hyperboost().await;
    }
    // Already active writes nothing, so managed replies before its thermal
    // fence (6965-7002), as native does.
    let rig = rig_at(7, 0, FULL, saved_files());
    let fake = Fake::new(0, 0);
    let lane = compose(&rig, &fake, Arc::default());
    leave_uncertain(&rig, &lane, &fake);
    let result = rig.hyperboost(1, true).await;
    assert_eq!(
        result["result"],
        json!({"status":1,"message":ALREADY_ACTIVE}),
        "{result}"
    );
    assert!(setters(&rig).is_empty());
    assert!(rows(&rig).is_empty());
    rig.stop().await;
}

const OWNED_VECTORS: &str = include_str!("../../../../../tests/data/hyperboost-owned-vectors.json");

/// Recorded by `ServiceControlRuntimeTests.NativeHyperBoostOwnedVectors`:
/// every managed row replays on the native dispatcher from the same
/// confirmed state, source, pad and saved profiles, and matches its status
/// and reply except for these deviations, each asserted literally:
///
/// - An exit names the restored performance only: native restores no fan
///   intent (the fan writer owns it), so " and fan intent" is dropped.
/// - Entry from a confirmed Custom whose saved intent is not Custom: managed
///   cannot build a well-formed ownership journal for a Custom base with a
///   non-Custom restore intent and fails (3) before any write; native keeps
///   no such journal, enters, and its exit restores the saved intent.
/// - A connected pad: managed enters through the pad path; native admits only
///   the pad-less path and refuses before any row.
/// - An exit on an external source neither can classify: both refuse before
///   any write, managed naming its preserved ownership journal and native
///   with its shared unclassified-power refusal (native keeps no such
///   journal).
///
/// Writes: native sends the enable pair for every entry, the saved
/// destination's pair (AC) or the restore recipe (battery) for every exit,
/// and nothing for any refusal or already-reached state. Managed's
/// precondition refusal is counted by its fake as one throwing HyperBoost
/// call; no setter is submitted there either.
#[tokio::test]
async fn managed_owned_hyperboost_vectors_replay_through_the_dispatcher() {
    const MANAGED_CUSTOM_ENTRY: &str =
        "HyperBoost could not be updated. OpenBlade did not retry the write.";
    const MANAGED_PAD_ENTRY: &str =
        "HyperBoost is active. Keep the supported cooling pad connected.";
    const MANAGED_UNCLASSIFIED_EXIT: &str = "HyperBoost restoration requires a confirmed power source; the owned recovery journal was preserved.";
    const UNCLASSIFIED_BLOCKED: &str = "Hardware application was not confirmed. The external power adapter could not be classified; overrides and control writes are blocked.";
    let vectors: Value = serde_json::from_str(OWNED_VECTORS).unwrap();
    let replies = vectors["replies"].as_array().unwrap();
    assert_eq!(replies.len(), 3 * 9 * 2 + 2 + 2);
    for row in replies {
        let actual = match row["source"].as_str().unwrap() {
            "PluggedIn" => FULL,
            "UsbC" => LIMITED,
            "Battery" => BATTERY,
            "ExternalUnknown" => 0x10,
            other => panic!("{other}"),
        };
        let state = row["state"].as_str().unwrap();
        let (from, fans) = match state {
            "SilentAutomatic" => (5, 0),
            "BalancedAutomatic" => (0, 0),
            "PerformanceAutomatic" => (2, 0),
            "CustomAutomatic" => (4, 0),
            "BatterySaverAutomatic" => (3, 0),
            "BatteryBalancedAutomatic" => (6, 0),
            "SilentManual" => (5, 1),
            "FixedMaximum" => (0, 1),
            "HyperBoostObserved" => (7, 0),
            other => panic!("{other}"),
        };
        let pad = match row["pad"].as_str().unwrap() {
            "Absent" => Pad::Absent,
            "PresentExact" => Pad::Present,
            "ObservationFailed" => Pad::ObservationFailed,
            other => panic!("{other}"),
        };
        let enabled = row["enabled"].as_bool().unwrap();
        let managed = (
            row["status"].as_u64().unwrap(),
            row["message"].as_str().unwrap(),
        );
        let rig = rig_at(from, fans, actual, saved_files());
        *rig.hyperboost.pad.lock().unwrap() = pad;
        let result = rig.hyperboost(1, enabled).await;
        let name = format!("{row}: {result}");
        let native = (
            result["result"]["status"].as_u64().unwrap(),
            result["result"]["message"].as_str().unwrap().to_owned(),
        );
        let exit = managed.1.replace(
            " performance and fan intent is restored.",
            " performance is restored.",
        );
        let expected = match (managed, pad) {
            ((3, MANAGED_CUSTOM_ENTRY), Pad::Absent) if state == "CustomAutomatic" => {
                (1, ENTERED.to_owned())
            }
            ((1, MANAGED_PAD_ENTRY), Pad::Present) => (2, PAD_PRESENT.to_owned()),
            ((2, MANAGED_UNCLASSIFIED_EXIT), _) => (2, UNCLASSIFIED_BLOCKED.to_owned()),
            ((1, message), _) if message.starts_with("HyperBoost is off and the prior ") => {
                (1, exit)
            }
            ((status, message), _) => (status, message.to_owned()),
        };
        assert_eq!(native, expected, "{name}");
        let writes: Vec<Vec<u8>> = match (native.0, native.1.as_str(), enabled) {
            (1, ENTERED, true) => enable(),
            (1, message, false) if message != ALREADY_OFF && actual == BATTERY => {
                [[1, 1, 0, 0], [1, 2, 0, 0], [1, 1, 3, 0], [1, 2, 3, 0]]
                    .iter()
                    .map(|p| p.to_vec())
                    .collect()
            }
            (1, message, false) if message != ALREADY_OFF => [[1, 1, 0, 0], [1, 2, 0, 0]]
                .iter()
                .map(|p| p.to_vec())
                .collect(),
            _ => Vec::new(),
        };
        assert_eq!(setters(&rig), writes, "{name}");
        // Managed submits nothing wherever native writes nothing, except its
        // fake's count of the precondition call that threw known-not-applied
        // and the pad path native does not admit.
        if writes.is_empty() && managed.1 != ENTRY_BASE && pad != Pad::Present {
            assert_eq!(row["hyperBoostWrites"], 0, "{name}");
            assert_eq!(row["performanceWrites"], 0, "{name}");
        }
        rig.stop_in_hyperboost().await;
    }
}

/// Managed 7121-7132: an entry needs the saved intent its exit restores to be
/// admitted at this class. Without a committed saved aggregate, or with a
/// saved GPU clock effect native cannot restore, entry is refused before any
/// row or setter.
#[tokio::test]
async fn entry_needs_an_admitted_saved_intent() {
    const INTENT_NOT_ADMITTED: &str = "The current external-power performance or fan intent is not admitted for this verified device.";
    let gpu = MemoryFiles::default();
    gpu.profiles_gpu(1);
    for (case, files) in [("no aggregate", MemoryFiles::default()), ("gpu", gpu)] {
        let rig = rig_at(0, 0, FULL, files);
        let result = rig.hyperboost(1, true).await;
        assert_eq!(
            result["result"],
            json!({"status":2,"message":INTENT_NOT_ADMITTED}),
            "{case}: {result}"
        );
        assert!(setters(&rig).is_empty(), "{case}");
        assert!(rows(&rig).is_empty(), "{case}");
        rig.stop().await;
    }
}

/// Managed 7121-7132 (an owned fan change is no admitted restore intent): a
/// fan change this Service still owns refuses entry before any row or setter.
#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn an_owned_fan_change_refuses_entry_before_any_row() {
    let rig = fan_rig(0, 0);
    rig.get().await;
    let fan = rig.request(fan_request(180, Some(1), 3700)).await;
    assert_eq!(fan["result"]["status"], 1, "{fan}");
    assert!(rig.storage.outstanding_fan());
    let rows_before = rows(&rig).len();
    let sent_before = setters(&rig).len();
    let result = rig.hyperboost(1, true).await;
    assert_eq!(
        result["result"],
        json!({"status":2,"message":"OpenBlade still owns a fan change that has not been handed back to firmware Automatic; no thermal setter was submitted."}),
        "{result}"
    );
    assert_eq!(rows(&rig).len(), rows_before);
    assert_eq!(setters(&rig).len(), sent_before);
    rig.origin.stop();
    rig.stop().await;
}

/// Managed 7134-7140 needs a classified external source. An external
/// reading native cannot classify refuses entry before any row or setter,
/// with managed's reply for ExternalUnknown.
#[tokio::test]
async fn entry_refuses_unclassified_external_power() {
    let rig = rig_at(0, 0, 0x10, saved_files());
    let result = rig.hyperboost(1, true).await;
    assert_eq!(
        result["result"],
        json!({"status":2,"message":EXTERNAL_POWER}),
        "{result}"
    );
    assert!(setters(&rig).is_empty());
    assert!(rows(&rig).is_empty());
    rig.stop().await;
}

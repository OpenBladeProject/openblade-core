//! The Service's own exit of owned HyperBoost at the coordinated Stop and at
//! a confirmed suspend (`hyperboost::cleanup`), under its cleanup admission.
//! Every Stop here is delivered in production order: SCM STOP revokes the
//! loaded origin first (`scm_host::installed_notice`), so no ordinary write
//! capture authorizes, and then the source drains. Each case drives the
//! drain or the suspend preparation over the rig's lifecycle, store and
//! scripted mailbox; no hardware or operating-system supplier is reached.
use super::*;
use std::time::Duration;

/// Managed's unowned exit, the `hyperboost-restore-balanced` pair.
const BALANCED_PAIR: [[u8; 4]; 2] = [[1, 1, 0, 0], [1, 2, 0, 0]];
/// The Balanced destination's wire value, which the exit's rows carry.
const BALANCED: i64 = 1;
const NOT_EXITED: &str = "Owned HyperBoost was not exited at Stop";

/// `owned` with the Service's cleanup admission wired over the rig custody,
/// as `Runtime::native` wires `CleanupGuard`.
async fn owned_with_cleanup() -> Rig {
    let rig = owned().await;
    cleanup(&rig);
    rig
}
fn cleanup(rig: &Rig) {
    let custody = rig.custody.clone();
    rig.runtime
        .read_cleanup(move |passed, scope| CleanupFresh::capture(custody.clone(), passed, scope));
}
/// SCM STOP as production delivers it: the origin is revoked, then the
/// source drains within its own bound.
async fn stop(rig: &Rig) -> io::Result<()> {
    rig.origin.stop();
    let mut source = rig.source.lock().await;
    let drained = source.drain().await;
    source.confirm_drained().unwrap();
    drained
}
fn sent(rig: &Rig) -> usize {
    rig.port.state.lock().unwrap().sent.len()
}
/// The frames sent from `from` that are not the keyboard's.
fn thermal_sent(rig: &Rig, from: usize) -> usize {
    rig.port.state.lock().unwrap().sent[from..]
        .iter()
        .filter(|(class, ..)| *class != 0)
        .count()
}
/// What a Stop that exited nothing left: no setter, no row, HyperBoost on
/// and still owned.
fn assert_left_owned(rig: &Rig, writes_before: usize, rows_before: usize) {
    assert_eq!(writes(rig), writes_before);
    assert_eq!(journal(rig).len(), rows_before);
    assert_eq!(selectors(rig), [SELECTOR_HYPERBOOST; 2]);
    assert!(owns(rig));
    assert!(!pending(rig));
}

/// Managed `PrepareForShutdownAsync` exits owned HyperBoost at shutdown.
/// Native does it under the Service's cleanup admission, with the origin
/// already revoked: managed's unowned exit, the Balanced restore pair, each
/// setter behind its own journal row, settled by the exit's own readback of
/// Balanced with Automatic fans. The rows are the entering session's. No
/// saved intent is resolved or applied: the saved Performance stays saved
/// for the next start, and the documents are unchanged.
#[tokio::test]
async fn stop_exits_owned_hyperboost_to_balanced_with_readback() {
    let rig = owned_with_cleanup().await;
    let entry = journal(&rig).last().unwrap().clone();
    assert_eq!(
        (&entry["destination"], &entry["progress"]),
        (&json!(100), &json!(5))
    );
    let saved = [
        rig.files.read("profiles.json").unwrap(),
        rig.files.read("manual-overrides.json").unwrap(),
    ];
    let (rows, from) = (journal(&rig).len(), sent(&rig));
    stop(&rig).await.unwrap();
    assert_eq!(mode_setters(&rig, from), BALANCED_PAIR.map(|p| p.to_vec()));
    assert_eq!(selectors(&rig), [SELECTOR_BALANCED; 2]);
    assert_eq!(rig.port.state.lock().unwrap().fans, [0; 2]);
    assert_eq!(
        rows_after(&rig, rows),
        [0, 1, 2, 3, 4, 5].map(|progress| (BALANCED, progress))
    );
    let exit = &journal(&rig)[rows..];
    for row in exit {
        assert!(row["requested"].is_null(), "{row}");
        assert_eq!(row["request"], exit[0]["request"], "{row}");
        for member in ["user_sid", "session", "caller_pid"] {
            assert_eq!(row[member], entry[member], "{member}: {row}");
        }
    }
    let id = exit[0]["request"].as_str().unwrap();
    assert!(is_step_request(id) && id != entry["request"], "{id}");
    assert!(!owns(&rig));
    assert!(!pending(&rig));
    assert_eq!(
        [
            rig.files.read("profiles.json").unwrap(),
            rig.files.read("manual-overrides.json").unwrap(),
        ],
        saved
    );
    assert_eq!(saved_performance(&rig), json!(2));
}
/// The saved plugged-in performance of the rig's Game rule.
fn saved_performance(rig: &Rig) -> Value {
    rig.files.document("profiles.json")["profiles"]["applicationProfiles"][0]["overrides"]
        ["pluggedIn"]["performance"]
        .clone()
}

/// A HyperBoost reading native did not enter is external at Stop too:
/// nothing is exited, read from firmware or written, and the Stop succeeds.
#[tokio::test]
async fn stop_leaves_an_external_hyperboost_without_a_read_or_a_write() {
    let files = MemoryFiles::default();
    files.profiles(1);
    let rig = rig_at(files, SELECTOR_HYPERBOOST);
    wired(&rig).await;
    cleanup(&rig);
    assert!(!owns(&rig));
    let (rows, from) = (journal(&rig).len(), sent(&rig));
    stop(&rig).await.unwrap();
    assert_eq!(sent(&rig), from);
    assert_eq!(journal(&rig).len(), rows);
    assert_eq!(selectors(&rig), [SELECTOR_HYPERBOOST; 2]);
}

/// A reading since the entry that showed firmware out of HyperBoost ends the
/// ownership for the Stop exit too (`Tracker::left_hyperboost`). A
/// HyperBoost that appeared afterwards was entered outside OpenBlade: the
/// Stop reads and exits nothing and succeeds. Under its cleanup admission it
/// settles the journal, which still named the entry; without one it writes
/// nothing at all.
#[tokio::test]
async fn stop_leaves_a_hyperboost_entered_after_an_observed_exit() {
    for composed in [true, false] {
        let rig = owned().await;
        if composed {
            cleanup(&rig);
        }
        rig.port.state.lock().unwrap().selectors = [SELECTOR_BALANCED; 2];
        observed(&rig, 1).await;
        rig.port.state.lock().unwrap().selectors = [SELECTOR_HYPERBOOST; 2];
        observed(&rig, 2).await;
        let (rows, from) = (journal(&rig).len(), sent(&rig));
        stop(&rig).await.unwrap();
        // Beside the keyboard owner the Stop still queries the keyboard.
        assert_eq!(thermal_sent(&rig, from), 0, "{composed}");
        assert_eq!(selectors(&rig), [SELECTOR_HYPERBOOST; 2], "{composed}");
        let settled: &[(i64, u64)] = if composed { &[(100, 0), (100, 6)] } else { &[] };
        assert_eq!(rows_after(&rig, rows), settled, "{composed}");
        assert_eq!(owns(&rig), !composed, "{composed}");
        assert!(!pending(&rig), "{composed}");
    }
}

/// Firmware that already left HyperBoost gets no exit write at Stop. The
/// cleanup admission admits the read, so ownership settles in the journal
/// with a HyperBoost row Prepared and KnownNotSubmitted, as at the battery
/// boundary.
#[tokio::test]
async fn stop_settles_ownership_read_only_when_firmware_already_left() {
    let rig = owned_with_cleanup().await;
    rig.port.state.lock().unwrap().selectors = [SELECTOR_BALANCED; 2];
    let (rows, before) = (journal(&rig).len(), writes(&rig));
    stop(&rig).await.unwrap();
    assert_eq!(writes(&rig), before);
    assert_eq!(rows_after(&rig, rows), [(100, 0), (100, 6)]);
    assert!(!owns(&rig));
    assert!(!pending(&rig));
}

/// Every refusal before a setter leaves HyperBoost on and owned, with no row
/// and no setter, and the Stop reports it: no cleanup admission composed, a
/// competing controller, unknown Windows power, a managed journal it has not
/// relinquished or that cannot be read, and a battery reading Windows does
/// not agree with.
#[tokio::test]
async fn a_refused_stop_exit_writes_nothing_and_is_reported() {
    type Arrange = fn(&Rig);
    let cases: [(&str, bool, Arrange); 6] = [
        ("no cleanup admission", false, |_| {}),
        ("competing controller", true, |rig| {
            rig.custody.controller.store(true, Ordering::Release);
        }),
        ("unknown Windows power", true, |rig| {
            rig.port.state.lock().unwrap().windows_on_battery = Some(None);
        }),
        ("managed journal pending", true, |rig| {
            *rig.hyperboost.journal.lock().unwrap() = Some(Some(managed_journal(1)));
        }),
        ("managed journal unreadable", true, |rig| {
            *rig.hyperboost.journal.lock().unwrap() = None;
        }),
        ("battery reading under Windows AC", true, |rig| {
            let mut state = rig.port.state.lock().unwrap();
            state.actual = 0;
            state.windows_on_battery = Some(Some(false));
        }),
    ];
    for (case, composed, arrange) in cases {
        let rig = owned().await;
        if composed {
            cleanup(&rig);
        }
        arrange(&rig);
        let (rows, before) = (journal(&rig).len(), writes(&rig));
        let error = stop(&rig).await.unwrap_err().to_string();
        assert!(error.contains(NOT_EXITED), "{case}: {error}");
        assert!(!error.contains("unresolved"), "{case}: {error}");
        assert_left_owned(&rig, before, rows);
    }
}

/// A competing controller that appears once the exit's job is running ends
/// it at its next authorization: every row and exchange is admitted afresh
/// under the cleanup admission, so nothing follows the lost admission. At
/// the exit's first power reading nothing was resolved. At the first
/// setter's own reading only the Prepared row exists, which retires as known
/// not submitted. At the second setter's, the first was acknowledged: its
/// row stays unsettled, fences the store and is reported unresolved.
#[tokio::test]
async fn a_cleanup_admission_lost_inside_the_exit_stops_before_the_next_setter() {
    type Rows = &'static [(i64, u64)];
    let cases: [(usize, Rows, usize); 3] = [
        (1, &[], 0),
        (2, &[(BALANCED, 0), (BALANCED, 6)], 0),
        (3, &[(BALANCED, 0), (BALANCED, 1), (BALANCED, 2)], 1),
    ];
    for (at, expected, submitted) in cases {
        let rig = owned_with_cleanup().await;
        let (rows, before) = (journal(&rig).len(), writes(&rig));
        let custody = Arc::downgrade(&rig.custody);
        {
            let mut state = rig.port.state.lock().unwrap();
            let read = state.power_reads + at;
            state.on_power = Some(Arc::new(move |reads| {
                if reads == read {
                    let custody = custody.upgrade().unwrap();
                    custody.controller.store(true, Ordering::Release);
                }
            }));
        }
        let error = stop(&rig).await.unwrap_err().to_string();
        assert!(error.contains(NOT_EXITED), "{at}: {error}");
        assert_eq!(error.contains("unresolved"), submitted > 0, "{at}: {error}");
        assert_eq!(rows_after(&rig, rows), expected, "{at}");
        assert_eq!(writes(&rig) - before, submitted, "{at}");
        assert_eq!(pending(&rig), submitted > 0, "{at}");
        assert!(owns(&rig), "{at}");
    }
}

/// A setter that may have been submitted is never replayed. The exit stops,
/// its intent row stays unsettled and fences the store, and the Stop reports
/// the submission unresolved. A restarted Service finds the fence: its Stop
/// reports it again and sends no setter.
#[tokio::test]
async fn an_ambiguous_stop_exit_fences_the_store_without_replay() {
    let rig = owned_with_cleanup().await;
    let (rows, before) = (journal(&rig).len(), writes(&rig));
    rig.port.state.lock().unwrap().fail_write = Some(before + 1);
    let error = stop(&rig).await.unwrap_err().to_string();
    assert!(
        error.contains(NOT_EXITED) && error.contains("unresolved"),
        "{error}"
    );
    assert_eq!(writes(&rig), before + 1);
    assert_eq!(rows_after(&rig, rows), [(BALANCED, 0), (BALANCED, 1)]);
    assert!(pending(&rig));
    assert!(owns(&rig));
    // The next process reads the same fence once a capture gives it the
    // store: its reconciliation and its own Stop exit both send nothing.
    let restarted = Rig::restarted(rig.files.clone());
    *restarted.custody.origin_for_fan.lock().unwrap() = Some(restarted.origin.clone());
    restarted.port.state.lock().unwrap().selectors = [SELECTOR_HYPERBOOST; 2];
    wired(&restarted).await;
    cleanup(&restarted);
    report(&restarted, 1, Some(r"C:\Games\Game.exe")).await;
    let from = sent(&restarted);
    let error = stop(&restarted).await.unwrap_err().to_string();
    assert!(
        error.contains(NOT_EXITED) && error.contains("unresolved"),
        "{error}"
    );
    // The unsettled row stops the exit before any exchange, not only
    // before its setter.
    assert_eq!(thermal_sent(&restarted, from), 0);
    assert_eq!(writes(&restarted), 0);
    assert_eq!(rows_after(&restarted, rows), [(BALANCED, 0), (BALANCED, 1)]);
}

/// The exit is paced to what is left of the drain before its first row
/// (`Pace::covers_before_rows`): at a device pace that cannot finish the
/// recipe by the drain's deadline it writes no row and sends no setter, and
/// the Stop reports HyperBoost left on. With the time, the same pace exits.
#[tokio::test]
async fn the_stop_exit_is_paced_to_the_drain_and_writes_no_row_it_cannot_finish() {
    for (window, exited) in [(1000, false), (5000, true)] {
        let rig = owned_with_cleanup().await;
        let (rows, before) = (journal(&rig).len(), writes(&rig));
        rig.origin.stop();
        rig.port.state.lock().unwrap().latency = Some(Duration::from_millis(35));
        let deadline = Instant::now() + Duration::from_millis(window);
        let mut source = rig.source.lock().await;
        let stopped = source.drain_by(deadline).await;
        drop(source);
        assert_eq!(stopped.is_ok(), exited, "{window}: {stopped:?}");
        if exited {
            assert_eq!(selectors(&rig), [SELECTOR_BALANCED; 2]);
            assert_eq!(rows_after(&rig, rows).last(), Some(&(BALANCED, 5)));
            assert!(!owns(&rig));
        } else {
            assert!(stopped.unwrap_err().to_string().contains(NOT_EXITED));
            assert_left_owned(&rig, before, rows);
        }
    }
}

/// An exit the Service makes on its own journals its rows for the session
/// that entered, so only that user's store names an owner; another user's
/// store reads the ownership and writes nothing for it.
#[tokio::test]
async fn only_the_entering_users_store_names_the_owner() {
    let rig = owned().await;
    let entry = journal(&rig).last().unwrap().clone();
    let own = Storage::open(Box::new(rig.files.clone()), SID).unwrap();
    let owner = own.hyperboost_owner().unwrap();
    assert_eq!(
        json!([owner.sid, owner.caller_pid, owner.session]),
        json!([entry["user_sid"], entry["caller_pid"], entry["session"]])
    );
    let other = Storage::open(Box::new(rig.files.clone()), "S-1-5-21-111-222-333-1002").unwrap();
    assert!(other.owns_hyperboost());
    assert_eq!(other.hyperboost_owner(), None);
    cleanup(&rig);
    stop(&rig).await.unwrap();
    let after = Storage::open(Box::new(rig.files.clone()), SID).unwrap();
    assert_eq!(after.hyperboost_owner(), None);
}

/// The Stop order is the keyboard release, the fan handback, then the
/// HyperBoost exit (`GuardedStatus::drain_by`). The handback goes first
/// because the history admits no performance row behind an open fan owner,
/// which refuses the exit and its read-only settlement alike. Here firmware
/// left owned HyperBoost outside OpenBlade and the user then took manual
/// fans: the Stop hands the fans back and, with that owner closed, settles
/// the HyperBoost ownership in the same drain. In the other order the
/// settlement is refused, the Stop fails and the ownership is left behind.
#[tokio::test]
async fn stop_hands_the_fans_back_before_it_settles_owned_hyperboost() {
    let rig = owned_with_cleanup().await;
    rig.port.state.lock().unwrap().selectors = [SELECTOR_BALANCED; 2];
    let fan = rig.request(fan_request(180, Some(1), 3700)).await;
    assert_eq!(fan["result"]["status"], 1, "{fan}");
    assert_eq!(rig.port.state.lock().unwrap().fans, [1; 2]);
    assert!(rig.storage.outstanding_fan() && owns(&rig));
    let rows = journal(&rig).len();
    stop(&rig).await.unwrap();
    assert_eq!(rig.port.state.lock().unwrap().fans, [0; 2]);
    let after = &journal(&rig)[rows..];
    let handed_back = after
        .iter()
        .position(|row| row.get("fan").is_some() && row["progress"] == 50)
        .unwrap();
    let settled = after
        .iter()
        .position(|row| row["destination"] == 100 && row["progress"] == 0)
        .unwrap();
    assert!(handed_back < settled, "{after:?}");
    assert_eq!(after.last().unwrap()["progress"], 6, "{after:?}");
    assert!(!rig.storage.outstanding_fan());
    assert!(!owns(&rig));
    assert!(!pending(&rig));
}

/// A failed fan handback does not skip the HyperBoost exit's attempt, and
/// the exit never writes behind the fan owner it leaves open: the Stop keeps
/// the handback's error, HyperBoost's ownership stays, and no performance
/// row follows the unsettled fan rows.
#[tokio::test]
async fn a_failed_fan_handback_leaves_owned_hyperboost_without_a_row() {
    let rig = owned_with_cleanup().await;
    rig.port.state.lock().unwrap().selectors = [SELECTOR_BALANCED; 2];
    let fan = rig.request(fan_request(181, Some(1), 3700)).await;
    assert_eq!(fan["result"]["status"], 1, "{fan}");
    let (rows, before) = (journal(&rig).len(), writes(&rig));
    rig.port.state.lock().unwrap().fail_write = Some(before + 1);
    let error = stop(&rig).await.unwrap_err().to_string();
    assert!(!error.contains(NOT_EXITED), "{error}");
    let after = &journal(&rig)[rows..];
    assert!(
        after.iter().all(|row| row.get("fan").is_some()),
        "{after:?}"
    );
    assert!(owns(&rig));
}

/// Beside the keyboard owner (`service-keyboard-driver-mode-v1`), whose
/// Normal release shares the Stop drain and is the suspend preparation the
/// HyperBoost exit follows (`low_power_cleanup`).
#[cfg(feature = "service-keyboard-driver-mode-v1")]
mod beside_the_keyboard {
    use super::*;
    use crate::installed::readiness::thermal_mutation::{HyperBoostCleanupExit as Exit, Release};

    const NORMAL: u8 = 0;
    const DRIVER: u8 = 3;

    /// The serving loop's keyboard step, which prepares a new suspend
    /// generation first (`GuardedStatus::maintain_driver_mode`).
    async fn keyboard_tick(rig: &Rig) {
        rig.held(
            HeldClock::CatchUp,
            Box::pin(async {
                let maintain = rig.source.lock().await.maintain_driver_mode();
                if let Some(maintain) = maintain {
                    maintain.await.unwrap();
                }
            }),
        )
        .await
    }
    fn keyboard(rig: &Rig) -> u8 {
        rig.port.state.lock().unwrap().device_mode
    }
    /// `owned_with_cleanup` whose keyboard the owner then claimed.
    async fn owned_with_keyboard() -> Rig {
        let rig = owned_with_cleanup().await;
        let observed = rig
            .request(json!({"operation":"observeState","version":90,"requestId":"dddddddd-bbbb-cccc-dddd-000000000071","includeLiveReadings":false,"requestedLiveReadings":0,"effectiveLiveReadings":0}))
            .await;
        assert_eq!(observed["success"], true, "{observed}");
        keyboard_tick(&rig).await;
        assert_eq!(keyboard(&rig), DRIVER);
        rig
    }
    /// The positions, among the frames sent from `from`, of the keyboard's
    /// Normal setter and of the first 0D/02 mode setter.
    fn release_and_exit(rig: &Rig, from: usize) -> (Option<usize>, Option<usize>) {
        let state = rig.port.state.lock().unwrap();
        let sent = &state.sent[from..];
        (
            sent.iter().position(|(class, command, payload, _)| {
                (*class, *command, payload.as_slice()) == (0, 0x04, &[0, 0])
            }),
            sent.iter()
                .position(|(class, command, ..)| (*class, *command) == (13, 2)),
        )
    }
    fn prepared(rig: &Rig) -> (Option<Release>, Option<Exit>) {
        (
            rig.runtime.low_power_outcome_for_tests(),
            rig.runtime.low_power_hyperboost_for_tests(),
        )
    }

    /// Stop releases the keyboard before it exits HyperBoost, and both
    /// complete inside the one drain.
    #[tokio::test]
    async fn stop_releases_the_keyboard_before_it_exits_owned_hyperboost() {
        let rig = owned_with_keyboard().await;
        let from = sent(&rig);
        stop(&rig).await.unwrap();
        let (release, exit) = release_and_exit(&rig, from);
        assert!(release.unwrap() < exit.unwrap(), "{release:?} {exit:?}");
        assert_eq!(keyboard(&rig), NORMAL);
        assert_eq!(rig.runtime.driver_mode_release(), Some(Release::Restored));
        assert_eq!(selectors(&rig), [SELECTOR_BALANCED; 2]);
        assert!(!owns(&rig));
    }

    /// No HyperBoost failure skips the keyboard release at Stop: an exit
    /// that is refused, one whose setter may have been submitted and one the
    /// drain has no time for each leave the keyboard restored to Normal. An
    /// exit ahead of the release would have left its unsettled row, at
    /// which the release refuses (`Release::Unsettled`).
    #[tokio::test]
    async fn a_failed_stop_exit_never_skips_the_keyboard_release() {
        type Arrange = fn(&Rig);
        // The drain's bound from the Stop. The keyboard keeps its own share
        // from its start, so a drain with no time left still releases it.
        let cases: [(&str, Duration, Arrange); 3] = [
            ("refused", Duration::from_secs(60), |rig| {
                *rig.hyperboost.journal.lock().unwrap() = Some(Some(managed_journal(1)));
            }),
            ("ambiguous", Duration::from_secs(60), |rig| {
                let mut state = rig.port.state.lock().unwrap();
                state.fail_write = Some(state.writes + 1);
            }),
            ("no time", Duration::ZERO, |_| {}),
        ];
        for (case, window, arrange) in cases {
            let rig = owned_with_keyboard().await;
            arrange(&rig);
            let rows = journal(&rig).len();
            rig.origin.stop();
            let deadline = Instant::now() + window;
            let stopped = rig.source.lock().await.drain_by(deadline).await;
            let error = stopped.unwrap_err().to_string();
            assert!(error.contains(NOT_EXITED), "{case}: {error}");
            if case != "ambiguous" {
                assert_eq!(journal(&rig).len(), rows, "{case}");
            }
            assert_eq!(
                error.contains("unresolved"),
                case == "ambiguous",
                "{case}: {error}"
            );
            assert_eq!(keyboard(&rig), NORMAL, "{case}");
            assert_eq!(
                rig.runtime.driver_mode_release(),
                Some(Release::Restored),
                "{case}"
            );
            assert!(owns(&rig), "{case}");
            // The fixture applies the first channel of a setter whose reply
            // fails, so only an exit that sent nothing leaves both selectors.
            if case != "ambiguous" {
                assert_eq!(selectors(&rig), [SELECTOR_HYPERBOOST; 2], "{case}");
            }
        }
    }

    /// A confirmed suspend releases the keyboard and then exits owned
    /// HyperBoost to Balanced under that generation's cleanup admission,
    /// once. Nothing is acquired or entered while the gate is set. After the
    /// resume the reconciliation applies the saved Performance from the
    /// Balanced the exit left as OpenBlade's own; HyperBoost is never
    /// entered again.
    #[tokio::test]
    async fn a_suspend_releases_the_keyboard_then_exits_owned_hyperboost() {
        let rig = owned_with_keyboard().await;
        let (rows, from) = (journal(&rig).len(), sent(&rig));
        suspend(&rig);
        keyboard_tick(&rig).await;
        assert_eq!(
            prepared(&rig),
            (Some(Release::Restored), Some(Exit::Exited))
        );
        let (release, exit) = release_and_exit(&rig, from);
        assert!(release.unwrap() < exit.unwrap(), "{release:?} {exit:?}");
        assert_eq!(mode_setters(&rig, from), BALANCED_PAIR.map(|p| p.to_vec()));
        assert_eq!(
            (keyboard(&rig), selectors(&rig)),
            (NORMAL, [SELECTOR_BALANCED; 2])
        );
        assert_eq!(rows_after(&rig, rows).last(), Some(&(BALANCED, 5)));
        assert!(!owns(&rig) && !pending(&rig));
        // The same generation is prepared once, and the gate admits nothing.
        let quiet = sent(&rig);
        for _ in 0..2 {
            keyboard_tick(&rig).await;
            tick(&rig).await;
        }
        assert_eq!(sent(&rig), quiet);
        assert_eq!(
            (keyboard(&rig), selectors(&rig)),
            (NORMAL, [SELECTOR_BALANCED; 2])
        );
        // Windows reports the system active again.
        resume(&rig);
        tick(&rig).await;
        assert_eq!(selectors(&rig), [SELECTOR_PERFORMANCE; 2]);
        let setters = mode_setters(&rig, from);
        assert!(
            setters
                .iter()
                .all(|setter| setter[2] != SELECTOR_HYPERBOOST),
            "{setters:?}"
        );
        assert!(!owns(&rig));
        rig.origin.stop();
        rig.stop().await;
    }

    /// No HyperBoost failure skips the keyboard release at a suspend either,
    /// and each leaves HyperBoost owned: a refusal and an exit the window
    /// has no time for write nothing; a setter that may have been submitted
    /// fences the store, and the next suspend replays nothing.
    #[tokio::test]
    async fn a_failed_suspend_exit_never_skips_the_keyboard_release() {
        type Arrange = fn(&Rig);
        let cases: [(&str, Exit, Arrange); 3] = [
            ("refused", Exit::Refused, |rig| {
                *rig.hyperboost.journal.lock().unwrap() = Some(Some(managed_journal(1)));
            }),
            ("ambiguous", Exit::Unresolved, |rig| {
                let mut state = rig.port.state.lock().unwrap();
                state.fail_write = Some(state.writes + 1);
            }),
            ("no time", Exit::Refused, |rig| {
                rig.port.state.lock().unwrap().latency = Some(Duration::from_millis(35));
            }),
        ];
        for (case, expected, arrange) in cases {
            let rig = owned_with_keyboard().await;
            arrange(&rig);
            let (rows, before) = (journal(&rig).len(), writes(&rig));
            suspend(&rig);
            keyboard_tick(&rig).await;
            assert_eq!(
                prepared(&rig),
                (Some(Release::Restored), Some(expected)),
                "{case}"
            );
            assert_eq!(keyboard(&rig), NORMAL, "{case}");
            assert!(owns(&rig), "{case}");
            let ambiguous = expected == Exit::Unresolved;
            assert_eq!(writes(&rig) - before, usize::from(ambiguous), "{case}");
            assert_eq!(pending(&rig), ambiguous, "{case}");
            if !ambiguous {
                assert_eq!(journal(&rig).len(), rows, "{case}");
                continue;
            }
            // The next suspend finds the fence and replays nothing.
            assert_eq!(rows_after(&rig, rows), [(BALANCED, 0), (BALANCED, 1)]);
            rig.port.state.lock().unwrap().fail_write = None;
            resume(&rig);
            tick(&rig).await;
            let from = sent(&rig);
            suspend(&rig);
            keyboard_tick(&rig).await;
            assert_eq!(prepared(&rig).1, Some(Exit::Unresolved), "{case}");
            assert_eq!(thermal_sent(&rig, from), 0, "{case}");
            assert_eq!(writes(&rig) - before, 1, "{case}");
            assert_eq!(rows_after(&rig, rows), [(BALANCED, 0), (BALANCED, 1)]);
        }
    }

    /// A HyperBoost entered after a reading showed firmware out of the
    /// owned one is not OpenBlade's at a suspend either: the keyboard is
    /// released, no thermal frame is sent, and only the journal is settled.
    #[tokio::test]
    async fn a_suspend_leaves_a_hyperboost_entered_after_an_observed_exit() {
        let rig = owned_with_keyboard().await;
        rig.port.state.lock().unwrap().selectors = [SELECTOR_BALANCED; 2];
        observed(&rig, 1).await;
        rig.port.state.lock().unwrap().selectors = [SELECTOR_HYPERBOOST; 2];
        observed(&rig, 2).await;
        let (rows, from) = (journal(&rig).len(), sent(&rig));
        suspend(&rig);
        keyboard_tick(&rig).await;
        assert_eq!(
            prepared(&rig),
            (Some(Release::Restored), Some(Exit::Settled))
        );
        assert_eq!(rows_after(&rig, rows), [(100, 0), (100, 6)]);
        assert!(!owns(&rig));
        let state = rig.port.state.lock().unwrap();
        assert!(state.sent[from..].iter().all(|(class, ..)| *class == 0));
        assert_eq!(state.selectors, [SELECTOR_HYPERBOOST; 2]);
    }

    /// A HyperBoost reading native did not enter stays external at a
    /// suspend: the keyboard is released and no thermal frame is sent.
    #[tokio::test]
    async fn a_suspend_leaves_an_external_hyperboost_untouched() {
        let files = MemoryFiles::default();
        files.profiles(1);
        let rig = rig_at(files, SELECTOR_HYPERBOOST);
        wired(&rig).await;
        cleanup(&rig);
        let from = sent(&rig);
        suspend(&rig);
        keyboard_tick(&rig).await;
        assert_eq!(prepared(&rig).1, Some(Exit::NotOwned));
        let state = rig.port.state.lock().unwrap();
        assert!(state.sent[from..].iter().all(|(class, ..)| *class == 0));
        assert_eq!(state.selectors, [SELECTOR_HYPERBOOST; 2]);
    }
}

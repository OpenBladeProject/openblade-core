//! A user's startup reconciliation and confirmed drift (`triggers::Startup`):
//! when startup ends, what an overwrite without a destination owns, and the
//! causes a competing controller drops. Each case drives the rig's reports,
//! saves and lifecycle tick over the in-memory store and scripted mailbox; no
//! hardware or operating-system supplier is reached.
use super::*;

/// Refusals that compare no reading, here on an adapter not classified yet,
/// settle the debt but leave the user starting, as managed keeps startup
/// pending on unclassified power. Game reported once the adapter classifies
/// applies over the firmware preset instead of pausing over an intent
/// native never confirmed.
#[tokio::test]
async fn refusals_that_compare_no_reading_leave_startup_pending() {
    let rig = rig_at(files_with_game(json!({"pluggedIn":{"performance":2}})), 0);
    // 0x10 is an unmapped level: a completed reading that classifies nothing.
    rig.port.state.lock().unwrap().actual = 0x10;
    for sequence in 1..=3 {
        report(&rig, sequence, Some(r"C:\Tools\Other.exe")).await;
    }
    assert_eq!(owed(&rig), None);
    assert_eq!(writes(&rig), 0);
    rig.port.state.lock().unwrap().actual = 15;
    let applied = report(&rig, 4, Some(r"C:\Games\Game.exe")).await;
    assert_eq!(applied["result"]["status"], 1, "{applied}");
    let status = observe(&rig, 1).await;
    assert_eq!(performance(&status), 2);
    assert_eq!(status["status"]["externalChanges"], json!([]), "{status}");
    rig.stop().await;
}

/// A starting reconciliation whose intent has no destination, here a
/// plugged-in profile with a GPU clock effect native cannot apply, writes no
/// preset and owns the one it leaves. On battery the saved profile then
/// applies, instead of pausing over that preset until an explicit request.
#[tokio::test]
async fn a_startup_without_a_destination_owns_the_preset_it_leaves() {
    let files = MemoryFiles::default();
    files.profiles_gpu(2);
    let rig = rig_at(files, 0);
    rig.get().await;
    wired(&rig).await;
    assert_eq!(writes(&rig), 0);
    assert_eq!(owed(&rig), None);
    assert_eq!(
        observe(&rig, 1).await["status"]["externalChanges"],
        json!([])
    );
    unplug(&rig);
    tick(&rig).await;
    assert!(writes(&rig) > 0);
    assert_eq!(owed(&rig), None);
    let status = observe(&rig, 2).await;
    assert_eq!(performance(&status), &saved_battery_performance(&rig));
    assert_eq!(status["status"]["externalChanges"], json!([]), "{status}");
    rig.stop().await;
}

/// A competing controller that drops the first activation ends startup
/// with nothing of OpenBlade's own confirmed, so the next power change
/// compares only its new intent and pauses, whether firmware moved or not.
/// This follows from native's stricter write admission, not managed parity:
/// managed's startup has no controller gate and writes the saved profile
/// (`Runtime::drop_for_controller`).
#[tokio::test]
async fn a_controller_dropped_startup_pauses_the_next_power_change() {
    let rig = rig_at(files_with_game(json!({"pluggedIn":{"performance":2}})), 0);
    rig.get().await;
    rig.custody.controller.store(true, Ordering::Release);
    wired(&rig).await;
    rig.custody.controller.store(false, Ordering::Release);
    assert_eq!(
        observe(&rig, 1).await["status"]["externalChanges"],
        json!([])
    );
    unplug(&rig);
    tick(&rig).await;
    assert_eq!(writes(&rig), 0);
    assert_eq!(owed(&rig), None);
    let status = observe(&rig, 2).await;
    assert_eq!(status["status"]["externalChanges"], json!([0]), "{status}");
    rig.stop().await;
}

/// A report's or a save's capture refused for a competing controller drops
/// the pair's debt and ends startup, as the lifecycle's does. Once the
/// controller leaves, a renewal writes nothing over a change recorded
/// outside OpenBlade, and a changed report compares that change and pauses.
#[tokio::test]
async fn a_capture_refused_for_a_controller_ends_startup() {
    for save in [false, true] {
        let rig = rig_at(files_with_game(json!({"pluggedIn":{"performance":0}})), 0);
        rig.custody.controller.store(true, Ordering::Release);
        let refused = if save {
            rig.request(update(1, &with_game_performance(&rig.files, 0)))
                .await
        } else {
            report(&rig, 1, Some(r"C:\Tools\Other.exe")).await
        };
        assert_eq!(refused["result"]["status"], 2, "{save}: {refused}");
        assert_eq!(owed(&rig), None, "{save}");
        rig.custody.controller.store(false, Ordering::Release);
        observe(&rig, 1).await;
        // Performance, outside OpenBlade.
        moved(&rig, 2, 0);
        assert_eq!(
            observe(&rig, 2).await["status"]["externalChanges"],
            json!([0])
        );
        let renewed = report(&rig, 2, Some(r"C:\Tools\Other.exe")).await;
        assert_eq!(writes(&rig), 0, "{save}: {renewed}");
        let game = report(&rig, 3, Some(r"C:\Games\Game.exe")).await;
        assert_eq!(writes(&rig), 0, "{save}: {game}");
        let status = observe(&rig, 3).await;
        assert_eq!(performance(&status), 2);
        assert_eq!(status["status"]["externalChanges"], json!([0]), "{status}");
        rig.stop().await;
    }
}

/// Startup is the user's, keyed on the SID as managed keys activation: the
/// same user in a new session is not starting again, so the next power
/// change compares a change recorded outside OpenBlade and pauses.
#[tokio::test]
async fn the_same_user_in_a_new_session_is_not_starting_again() {
    let rig = applied_game().await;
    rig.custody.current_session.store(8, Ordering::Release);
    // WTS_SESSION_LOGON.
    rig.custody.lifecycle.ingress(SESSION_CHANGE, 5);
    tick(&rig).await;
    // Silent, outside OpenBlade.
    moved(&rig, 5, 0);
    assert_eq!(
        observe(&rig, 1).await["status"]["externalChanges"],
        json!([0])
    );
    let before = writes(&rig);
    unplug(&rig);
    tick(&rig).await;
    assert_eq!(writes(&rig), before);
    assert_eq!(rig.runtime.owed_attempts(SID, 8), None);
    let status = observe(&rig, 2).await;
    assert_eq!(status["status"]["externalChanges"], json!([0]), "{status}");
    rig.stop().await;
}

/// An explicit performance write over manual fans OpenBlade does not own
/// returns them to Automatic with the pair: its readback confirms both
/// subsystems as OpenBlade's own (managed `ConfirmedCoupledWrites`), so the
/// recorded fan change clears.
#[tokio::test]
async fn an_explicit_write_over_manual_fans_owns_the_fans_it_returns() {
    let rig = applied_game().await;
    // Performance with manual fans, outside OpenBlade.
    moved(&rig, 2, 1);
    assert_eq!(
        observe(&rig, 1).await["status"]["externalChanges"],
        json!([1])
    );
    let balanced = rig.set(1, value(1, 1)).await;
    assert_eq!(balanced["result"]["status"], 1, "{balanced}");
    assert_eq!(rig.port.state.lock().unwrap().fans, [0; 2]);
    let status = observe(&rig, 2).await;
    assert_eq!(performance(&status), 1);
    assert_eq!(status["status"]["externalChanges"], json!([]), "{status}");
    rig.stop().await;
}

/// A power change seen in the resume window and refused before its steps
/// keeps the resume when a later cause, here the lease expiry, joins it past
/// that window: the tick then holds the change for the window from its
/// reconciliation, and a reading of the destination within it clears the
/// change.
#[tokio::test]
async fn a_joined_cause_keeps_the_resume_of_a_refused_power_change() {
    let rig = applied_game().await;
    let before = writes(&rig);
    suspend(&rig);
    tick(&rig).await;
    // The firmware battery preset before it settles.
    moved(&rig, 6, 0);
    resume(&rig);
    // Locked: every Windows supplier check of the attempt refuses.
    rig.custody.fresh_refuse.store(true, Ordering::Release);
    unplug(&rig);
    tick(&rig).await;
    assert_eq!(owed(&rig), Some(0));
    // Past the window; that gap renews the lease.
    age(&rig, thermal_drift_window());
    tick(&rig).await;
    rig.custody.fresh_refuse.store(false, Ordering::Release);
    rig.runtime.expire_application_lease();
    unlock(&rig);
    tick(&rig).await;
    assert_eq!(writes(&rig), before);
    assert_eq!(owed(&rig), None);
    let row: Value = serde_json::from_str(LIFECYCLE_VECTORS).unwrap();
    let held = vector(&row, "resumeSettledWithinWindow");
    // Battery Saver, within the window.
    moved(&rig, 3, 0);
    let status = observe(&rig, 1).await;
    assert_eq!(
        status["status"]["externalChanges"], held["externalChanges"],
        "{status}"
    );
    rig.stop().await;
}

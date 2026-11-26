//! Lifecycle-driven reconciliation (`Runtime::reconcile_lifecycle`): the
//! origin tick applies what the console pair owes after a resume, a
//! gate-clearing DisplayOn, a Windows power-kind change or an expired
//! application lease. Each case drives the tick the serving loop runs every
//! 500 ms over the rig's lifecycle, store and scripted mailbox; no hardware or
//! operating-system supplier is reached.
use super::*;
use openblade_domain::power_setting::{PBT_POWERSETTINGCHANGE, PowerSetting};

#[cfg(feature = "service-thermal-convergence-v1")]
#[path = "thermal_convergence_loop.rs"]
mod convergence_loop;
#[cfg(feature = "service-hyperboost-v1")]
#[path = "hyperboost_lifecycle.rs"]
mod hyperboost_lifecycle;
#[path = "power_only_release.rs"]
mod power_only_release;
#[path = "request_triggers_startup.rs"]
mod startup;

/// The managed `ApplyFullTransitionAsync` outcomes after each lifecycle
/// transition (`ServiceControlRuntimeTests.NativeLifecycleReconcileVectors`).
const LIFECYCLE_VECTORS: &str =
    include_str!("../../../../../tests/data/lifecycle-reconcile-vectors.json");
/// `PBT_APMPOWERSTATUSCHANGE`, the notice Windows sends for an AC/DC switch.
const POWER_STATUS: u32 = 10;
/// `SERVICE_CONTROL_SESSIONCHANGE` and `WTS_SESSION_UNLOCK`.
const SESSION_CHANGE: u32 = 14;
const UNLOCK: u32 = 8;

/// Wires the rig's lifecycle in as the runtime's gate, write epoch and power
/// generation, and the rig's console user as its console probe, as
/// `Runtime::native` does, and runs the tick's first pass, which activates
/// that user. The returned flag says whether the probe names the user.
async fn wired(rig: &Rig) -> Rc<Cell<bool>> {
    probed(rig, true).await.0
}
/// `wired` with the probe naming the user only once the returned flag is
/// set, starting from `named`, and a count of the probe's reads.
async fn probed(rig: &Rig, named: bool) -> (Rc<Cell<bool>>, Rc<Cell<usize>>) {
    let known = Rc::new(Cell::new(named));
    let reads = Rc::new(Cell::new(0));
    let custody = rig.custody.clone();
    let (named, counted) = (known.clone(), reads.clone());
    rig.runtime.read_console(move || {
        counted.set(counted.get() + 1);
        named.get().then(|| {
            (
                custody.current_sid.lock().unwrap().clone(),
                custody.current_session.load(Ordering::Acquire) as u32,
            )
        })
    });
    unconsoled(rig).await;
    (known, reads)
}
fn age(rig: &Rig, by: std::time::Duration) {
    rig.runtime.age_lifecycle_tick(by);
}
fn pace() -> std::time::Duration {
    crate::installed::readiness::thermal_mutation::Runtime::lifecycle_pace()
}
fn poll() -> std::time::Duration {
    crate::installed::readiness::thermal_mutation::Runtime::lifecycle_unnamed_poll()
}
/// `wired` without a console probe answer: no console user is known.
async fn unconsoled(rig: &Rig) {
    gated(rig);
    let epoch = rig.custody.clone();
    rig.runtime
        .read_admission_epoch(move || epoch.lifecycle.active());
    let custody = rig.custody.clone();
    rig.runtime
        .read_power_generation(move || custody.lifecycle.observe_power_kind(None));
    // The baseline sample `Runtime::native` takes at start.
    Fresh::power_generation(rig.custody.as_ref());
    tick(rig).await;
}
/// One origin tick's lifecycle reconciliation, as the serving loop runs it
/// (`a_serving_loop_tick_runs_the_lifecycle_reconciliation` drives the loop).
async fn tick(rig: &Rig) {
    let reconcile = rig.source.lock().await.reconcile_lifecycle();
    if let Some(reconcile) = reconcile {
        reconcile.await.unwrap();
    }
}
fn captures(rig: &Rig) -> usize {
    rig.factory_calls.load(Ordering::Acquire)
}
fn performance(status: &Value) -> &Value {
    &status["status"]["confirmedState"]["performance"]
}
fn display(rig: &Rig, level: u32) {
    rig.custody.lifecycle.notice(
        POWER,
        PBT_POWERSETTINGCHANGE,
        Some(PowerSetting::ConsoleDisplay(level)),
    );
}
/// An SCM power-status notice as native ingress delivers it: the Windows
/// power kind is sampled, then the lifecycle fences.
fn power_notice(rig: &Rig) {
    Fresh::power_generation(rig.custody.as_ref());
    rig.custody.lifecycle.ingress(POWER, POWER_STATUS);
}
/// Windows reports battery, with its power notice.
fn unplug(rig: &Rig) {
    rig.port.state.lock().unwrap().actual = 0;
    rig.metadata.store(1, Ordering::Release);
    power_notice(rig);
}
fn replug(rig: &Rig, actual: u8) {
    rig.port.state.lock().unwrap().actual = actual;
    rig.metadata.store(0, Ordering::Release);
    power_notice(rig);
}
fn unlock(rig: &Rig) {
    rig.custody.lifecycle.ingress(SESSION_CHANGE, UNLOCK);
}
fn saved_battery_performance(rig: &Rig) -> Value {
    rig.files.document("profiles.json")["profiles"]["battery"]["performance"].clone()
}
/// A rig whose Game rule is applied on AC, with the tick wired, marked and
/// its console user activated. Its saved performance is the managed
/// recorder's.
async fn applied_game() -> Rig {
    let rig = reported_game().await;
    wired(&rig).await;
    rig
}
/// `applied_game` before the tick is wired.
async fn reported_game() -> Rig {
    let vectors: Value = serde_json::from_str(LIFECYCLE_VECTORS).unwrap();
    let rig = rig_at(files_with_game(json!({"pluggedIn":{"performance":2}})), 0);
    let saved = &rig.files.document("profiles.json")["profiles"];
    assert_eq!(
        saved["pluggedIn"]["performance"],
        vectors["profiles"]["pluggedIn"]
    );
    assert_eq!(
        saved_battery_performance(&rig),
        vectors["profiles"]["battery"]
    );
    let applied = report(&rig, 1, Some(r"C:\Games\Game.exe")).await;
    assert_eq!(applied["result"]["status"], 1, "{applied}");
    assert_eq!(performance(&rig.get().await), 2);
    assert_eq!(owed(&rig), None);
    rig
}
fn vector<'a>(vectors: &'a Value, transition: &str) -> &'a Value {
    vectors["transitions"]
        .as_array()
        .unwrap()
        .iter()
        .find(|row| row["transition"] == transition)
        .unwrap_or_else(|| panic!("{transition}"))
}

/// Intent deferred in low power is applied by the tick after the resume
/// boundary, exactly once: ticks in low power and after the reconciliation
/// read nothing, and the next renewal finds the debt settled.
#[tokio::test]
async fn a_debt_deferred_in_low_power_is_reconciled_once_at_the_resume_tick() {
    let rig = rig_at(files_with_game(json!({"pluggedIn":{"performance":2}})), 0);
    // The session's startup debt is settled before the gate is wired.
    report(&rig, 1, Some(r"C:\Tools\Other.exe")).await;
    assert_eq!(owed(&rig), None);
    wired(&rig).await;
    suspend(&rig);
    let deferred = report(&rig, 2, Some(r"C:\Games\Game.exe")).await;
    assert_eq!(deferred["result"], deferred_report());
    assert_eq!(owed(&rig), Some(0));
    let before = sent(&rig);
    let captured = captures(&rig);
    for _ in 0..3 {
        tick(&rig).await;
    }
    // Low power takes no capture at all, not only no write.
    assert_eq!(captures(&rig), captured);
    assert_eq!(sent(&rig), before);
    assert_eq!(owed(&rig), Some(0));
    resume(&rig);
    tick(&rig).await;
    assert!(sent(&rig) > before);
    assert_eq!(owed(&rig), None);
    assert_eq!(performance(&rig.get().await), 2);
    let after = sent(&rig);
    tick(&rig).await;
    let renewed = report(&rig, 3, Some(r"C:\Games\Game.exe")).await;
    assert_eq!(renewed["result"], json!({"status":0,"message":RENEWED}));
    assert_eq!(sent(&rig), after);
    rig.stop().await;
}

/// A DisplayOn that clears a set gate is a resume boundary (managed
/// dispatches `Resumed` for it), so the tick applies the deferred intent
/// once. A display-off never sets the gate or fences a request, and a
/// display-on on a clear gate is no boundary.
#[tokio::test]
async fn display_off_leaves_the_gate_clear_and_a_gate_clearing_display_on_reconciles() {
    let rig = rig_at(files_with_game(json!({"pluggedIn":{"performance":2}})), 0);
    report(&rig, 1, Some(r"C:\Tools\Other.exe")).await;
    wired(&rig).await;
    display(&rig, 1);
    let clear = rig.custody.lifecycle.low_power().unwrap().generation;
    display(&rig, 0);
    assert_eq!(
        rig.custody
            .lifecycle
            .low_power()
            .map(|gate| gate.generation),
        Some(clear)
    );
    tick(&rig).await;
    // Managed does not defer in Modern Standby's display-off: a changed
    // report applies at once.
    let applied = report(&rig, 2, Some(r"C:\Games\Game.exe")).await;
    assert_eq!(applied["result"]["status"], 1, "{applied}");
    display(&rig, 1);
    assert_eq!(
        rig.custody
            .lifecycle
            .low_power()
            .map(|gate| gate.generation),
        Some(clear)
    );
    let before = sent(&rig);
    tick(&rig).await;
    assert_eq!(sent(&rig), before);
    // A suspend the Service saw, a deferred report, then only display-on.
    suspend(&rig);
    let deferred = report(&rig, 3, Some(r"C:\Tools\Other.exe")).await;
    assert_eq!(deferred["result"], deferred_report());
    display(&rig, 0);
    tick(&rig).await;
    assert!(rig.custody.lifecycle.low_power_or_unknown());
    assert_eq!(sent(&rig), before);
    display(&rig, 2);
    assert!(!rig.custody.lifecycle.low_power_or_unknown());
    tick(&rig).await;
    assert!(sent(&rig) > before);
    assert_eq!(owed(&rig), None);
    // Managed selects the same desired preset but submits nothing: its
    // resume applies only on a changed power source, so the deferred report
    // stays unapplied until the next change. Native settles the debt here.
    let vectors: Value = serde_json::from_str(LIFECYCLE_VECTORS).unwrap();
    let resumed = vector(&vectors, "resumeUnchangedAfterDeferral");
    assert_eq!(resumed["submitted"], false, "{resumed}");
    assert_eq!(resumed["source"], 1, "{resumed}");
    assert_eq!(performance(&rig.get().await), &resumed["desired"]);
    let after = sent(&rig);
    tick(&rig).await;
    assert_eq!(sent(&rig), after);
    rig.stop().await;
}

/// A Windows power-kind change applies the saved intent of the new source,
/// as managed `HandlePowerSourceChangedAsync` does on a changed source, once
/// per change; an unchanged source applies nothing.
#[tokio::test]
async fn a_power_kind_change_reconciles_the_new_source_once() {
    let vectors: Value = serde_json::from_str(LIFECYCLE_VECTORS).unwrap();
    let rig = applied_game().await;
    let ac = rig.port.state.lock().unwrap().actual;
    let before = sent(&rig);
    // A power notice that keeps the kind is no power boundary.
    power_notice(&rig);
    tick(&rig).await;
    assert_eq!(sent(&rig), before);
    assert_eq!(vector(&vectors, "powerUnchanged")["submitted"], false);
    unplug(&rig);
    tick(&rig).await;
    assert!(sent(&rig) > before);
    assert_eq!(owed(&rig), None);
    let battery = vector(&vectors, "powerToBattery");
    assert_eq!(battery["submitted"], true, "{battery}");
    assert_eq!(battery["source"], 1, "{battery}");
    assert_eq!(performance(&rig.get().await), &battery["desired"]);
    let after = sent(&rig);
    tick(&rig).await;
    assert_eq!(sent(&rig), after);
    replug(&rig, ac);
    tick(&rig).await;
    assert!(sent(&rig) > after);
    let plugged = vector(&vectors, "powerToPluggedIn");
    assert_eq!(plugged["submitted"], true, "{plugged}");
    assert_eq!(plugged["desired"], 2);
    assert_eq!(performance(&rig.get().await), 2);
    rig.stop().await;
}

/// A power change while the gate is set reads nothing and is applied by the
/// tick after the resume, as managed skips `PowerChanged` in low power and
/// its resume compares the source again.
#[tokio::test]
async fn a_power_change_in_low_power_is_applied_after_the_resume() {
    let rig = applied_game().await;
    let before = sent(&rig);
    let captured = captures(&rig);
    suspend(&rig);
    unplug(&rig);
    tick(&rig).await;
    tick(&rig).await;
    assert_eq!(captures(&rig), captured);
    assert_eq!(sent(&rig), before);
    // The change is owed to the pair the first attempt after the resume
    // resolves; nothing is recorded in low power.
    assert_eq!(owed(&rig), None);
    resume(&rig);
    tick(&rig).await;
    assert!(sent(&rig) > before);
    assert_eq!(owed(&rig), None);
    assert_eq!(
        performance(&rig.get().await),
        &saved_battery_performance(&rig)
    );
    rig.stop().await;
}

/// An expired lease with an application profile active applies the power
/// profile once (managed `ExpireApplicationContextAsync`); the next report
/// selects the application again. Managed selects the same desired source
/// and preset but submits nothing here: `PublishApplicationContextStatus`
/// publishes the new desired state before `ApplyFullTransitionAsync` takes
/// it as the prior one, so its drift tracker reads OpenBlade's own Game
/// preset as an external change and pauses, and its default convergence
/// loop repairs that later. Native deliberately compares OpenBlade's own
/// confirmed state at the expiry, as at every other cause, and applies, so
/// no convergence claim is spent on it (`leaseExpired` diverges).
#[tokio::test]
async fn an_expired_lease_applies_the_power_profile_once() {
    let vectors: Value = serde_json::from_str(LIFECYCLE_VECTORS).unwrap();
    let rig = applied_game().await;
    let before = writes(&rig);
    rig.runtime.expire_application_lease();
    tick(&rig).await;
    assert!(writes(&rig) > before);
    assert_eq!(owed(&rig), None);
    let expired = vector(&vectors, "leaseExpired");
    assert_eq!(expired["source"], 1, "{expired}");
    // The managed row this deliberately diverges from.
    assert_eq!(expired["submitted"], false, "{expired}");
    assert_eq!(expired["externalChanges"], json!([0]), "{expired}");
    let status = rig.get().await;
    assert_eq!(performance(&status), &expired["desired"]);
    assert_eq!(status["status"]["externalChanges"], json!([]), "{status}");
    // The cleared foreground cannot expire again.
    let after = sent(&rig);
    tick(&rig).await;
    assert_eq!(sent(&rig), after);
    let again = report(&rig, 2, Some(r"C:\Games\Game.exe")).await;
    assert_eq!(again["result"]["status"], 1, "{again}");
    assert_eq!(
        performance(&rig.get().await),
        &vector(&vectors, "gameReported")["desired"]
    );
    rig.stop().await;
}

/// The lease expiry hands OpenBlade's own Game state back: the Game rule's
/// Performance and owned Fixed fans return to the power profile's Balanced
/// and Automatic fans, so a later unplug does not leave them on battery.
/// Managed pauses at the expiry and relies on its convergence loop, which
/// native does not have (`an_expired_lease_applies_the_power_profile_once`).
#[tokio::test]
async fn an_expired_lease_hands_back_owned_fixed_fans() {
    let rig = rig_at(
        files_with_game(serde_json::from_str(PRESET_AND_FIXED).unwrap()),
        0,
    );
    let applied = report(&rig, 1, Some(r"C:\Games\Game.exe")).await;
    assert_eq!(applied["result"]["status"], 1, "{applied}");
    wired(&rig).await;
    {
        let s = rig.port.state.lock().unwrap();
        assert_eq!((s.selectors, s.fans), ([2; 2], [1; 2]));
    }
    rig.runtime.expire_application_lease();
    tick(&rig).await;
    assert_eq!(owed(&rig), None);
    {
        let s = rig.port.state.lock().unwrap();
        assert_eq!((s.selectors, s.fans), ([0; 2], [0; 2]));
    }
    let status = observe(&rig, 1).await;
    assert_eq!(performance(&status), 1, "{status}");
    assert_eq!(status["status"]["externalChanges"], json!([]), "{status}");
    rig.stop().await;
}

/// Time asleep is not reporter absence: a gate boundary renews the lease,
/// so a resume after a long sleep keeps the application profile.
#[tokio::test]
async fn a_resume_renews_the_lease_instead_of_expiring_it() {
    let rig = applied_game().await;
    let before = sent(&rig);
    suspend(&rig);
    tick(&rig).await;
    rig.runtime.expire_application_lease();
    resume(&rig);
    tick(&rig).await;
    assert_eq!(sent(&rig), before);
    assert_eq!(owed(&rig), None);
    let renewed = report(&rig, 2, Some(r"C:\Games\Game.exe")).await;
    assert_eq!(renewed["result"], json!({"status":0,"message":RENEWED}));
    rig.stop().await;
}

/// The gate setting during the tick's reconciliation defers it with nothing
/// submitted: the debt stays owed with no retry spent, and the tick after the
/// resume applies it once over OpenBlade's own confirmed preset.
#[tokio::test]
async fn a_suspend_during_the_tick_reconciliation_defers_it_to_the_resume() {
    let rig = applied_game().await;
    let before = (sent(&rig), writes(&rig));
    let custody = rig.custody.clone();
    let once = Arc::new(AtomicBool::new(false));
    let fired = once.clone();
    rig.port.state.lock().unwrap().on_power = Some(Arc::new(move |_| {
        if !fired.swap(true, Ordering::AcqRel) {
            custody.lifecycle.ingress(POWER, SUSPEND);
        }
    }));
    unplug(&rig);
    tick(&rig).await;
    assert!(once.load(Ordering::Acquire), "the step read the adapter");
    assert_eq!(writes(&rig), before.1);
    assert_eq!(owed(&rig), Some(0));
    rig.port.state.lock().unwrap().on_power = None;
    tick(&rig).await;
    assert_eq!(writes(&rig), before.1);
    resume(&rig);
    tick(&rig).await;
    assert!(writes(&rig) > before.1);
    assert_eq!(owed(&rig), None);
    assert_eq!(
        performance(&rig.get().await),
        &saved_battery_performance(&rig)
    );
    let after = sent(&rig);
    tick(&rig).await;
    assert_eq!(sent(&rig), after);
    rig.stop().await;
}

/// A fence that leaves the gate generation in place, here a repeated resume
/// while the tick's performance step reads the adapter, refuses that step's
/// write with nothing submitted. It is no deferral, but the debt stays owed
/// with one retry spent, and since the write epoch moved the first tick after
/// `PACE` runs the cause again under fresh admission (the second debt drop of
/// the low-power slice). The Game preset its first attempt read is still
/// OpenBlade's own confirmed one, so the retry is no drift.
#[tokio::test]
async fn a_fence_without_a_gate_move_is_retried_after_its_pace() {
    let rig = applied_game().await;
    let before = writes(&rig);
    let generation = rig.custody.lifecycle.low_power().unwrap().generation;
    let custody = rig.custody.clone();
    let once = Arc::new(AtomicBool::new(false));
    let fired = once.clone();
    rig.port.state.lock().unwrap().on_power = Some(Arc::new(move |_| {
        if !fired.swap(true, Ordering::AcqRel) {
            custody.lifecycle.ingress(POWER, RESUME);
        }
    }));
    unplug(&rig);
    tick(&rig).await;
    assert!(once.load(Ordering::Acquire), "the step read the adapter");
    assert_eq!(
        rig.custody.lifecycle.low_power().unwrap().generation,
        generation
    );
    assert_eq!(writes(&rig), before);
    assert_eq!(owed(&rig), Some(1));
    rig.port.state.lock().unwrap().on_power = None;
    tick(&rig).await;
    assert_eq!(writes(&rig), before);
    age(&rig, pace());
    tick(&rig).await;
    assert!(writes(&rig) > before);
    assert_eq!(owed(&rig), None);
    assert_eq!(
        performance(&rig.get().await),
        &saved_battery_performance(&rig)
    );
    let after = sent(&rig);
    tick(&rig).await;
    assert_eq!(sent(&rig), after);
    rig.stop().await;
}

/// A step refused at an unmoved write epoch is no overtaken step: its debt
/// settles as the report path settles it, and the tick does not retry it.
#[tokio::test]
async fn a_step_refused_at_an_unmoved_epoch_settles_the_debt() {
    let rig = applied_game().await;
    let before = (sent(&rig), writes(&rig));
    let epoch = rig.custody.lifecycle.active();
    // The tick's own authorization passes; the step's first one refuses.
    rig.custody.refuse_at.store(2, Ordering::Release);
    rig.runtime.expire_application_lease();
    tick(&rig).await;
    assert_eq!(rig.custody.lifecycle.active(), epoch);
    assert_eq!(writes(&rig), before.1);
    assert_eq!(owed(&rig), None);
    let captured = captures(&rig);
    rig.custody.fresh_refuse.store(false, Ordering::Release);
    tick(&rig).await;
    assert_eq!(captures(&rig), captured);
    assert_eq!(writes(&rig), before.1);
    rig.stop().await;
}

/// A resume to the lock screen refuses the attempt before any firmware
/// read, and the cause stays pending: ticks at the same write epoch take no
/// capture, and the unlock's session notice moves the epoch, so the next tick
/// applies the battery profile the unplug owed.
#[tokio::test]
async fn a_cause_refused_at_a_locked_resume_applies_after_the_unlock() {
    let rig = applied_game().await;
    let before = sent(&rig);
    suspend(&rig);
    unplug(&rig);
    // Locked: every Windows supplier check of the attempt refuses.
    rig.custody.fresh_refuse.store(true, Ordering::Release);
    resume(&rig);
    tick(&rig).await;
    assert_eq!(sent(&rig), before);
    assert_eq!(owed(&rig), Some(0));
    let captured = captures(&rig);
    for _ in 0..3 {
        tick(&rig).await;
    }
    assert_eq!(captures(&rig), captured);
    rig.custody.fresh_refuse.store(false, Ordering::Release);
    unlock(&rig);
    tick(&rig).await;
    assert!(sent(&rig) > before);
    assert_eq!(owed(&rig), None);
    assert_eq!(
        performance(&rig.get().await),
        &saved_battery_performance(&rig)
    );
    let after = (sent(&rig), captures(&rig));
    unlock(&rig);
    tick(&rig).await;
    assert_eq!((sent(&rig), captures(&rig)), after);
    rig.stop().await;
}

/// A refused trigger capture reads no firmware and spends nothing, so
/// unrelated epoch moves while it is refused, such as a battery-level power
/// notice or another session's notice, cannot use the cause up: each takes
/// one capture, and once the capture is admitted the tick applies the
/// battery profile the unplug owed, with no report.
#[tokio::test]
async fn a_refused_capture_spends_nothing_and_applies_once_admitted() {
    let rig = applied_game().await;
    rig.custody.scoped_fresh.store(true, Ordering::Release);
    rig.custody.current_console.store(false, Ordering::Release);
    let captured = captures(&rig);
    let written = writes(&rig);
    unplug(&rig);
    tick(&rig).await;
    assert_eq!(captures(&rig), captured + 1);
    tick(&rig).await;
    assert_eq!(captures(&rig), captured + 1);
    for moves in 1..=6 {
        if moves % 2 == 0 {
            power_notice(&rig);
        } else {
            // WTS_SESSION_LOGON for another session fences as well.
            rig.custody.lifecycle.ingress(SESSION_CHANGE, 5);
        }
        tick(&rig).await;
        assert_eq!(captures(&rig), captured + 1 + moves);
    }
    assert_eq!(writes(&rig), written);
    assert_eq!(owed(&rig), Some(0));
    rig.custody.current_console.store(true, Ordering::Release);
    unlock(&rig);
    tick(&rig).await;
    assert!(writes(&rig) > written);
    assert_eq!(owed(&rig), None);
    assert_eq!(
        performance(&rig.get().await),
        &saved_battery_performance(&rig)
    );
    rig.stop().await;
}

/// A step refused transiently at an unmoved write epoch, here a USB-C
/// adapter whose class is not settled yet, leaves the debt owed with a retry
/// spent. The cause is kept: ticks at that epoch read nothing, and the next
/// power notice after the adapter classifies applies the plugged-in profile.
#[tokio::test]
async fn an_unclassified_adapter_keeps_the_cause_until_the_epoch_moves() {
    let rig = applied_game().await;
    let ac = rig.port.state.lock().unwrap().actual;
    unplug(&rig);
    tick(&rig).await;
    assert_eq!(
        performance(&rig.get().await),
        &saved_battery_performance(&rig)
    );
    let written = writes(&rig);
    // 0x10 is an unmapped level: a completed reading that classifies nothing.
    replug(&rig, 0x10);
    let epoch = rig.custody.lifecycle.active();
    tick(&rig).await;
    assert_eq!(rig.custody.lifecycle.active(), epoch);
    assert_eq!(writes(&rig), written);
    assert_eq!(owed(&rig), Some(1));
    let captured = captures(&rig);
    age(&rig, pace());
    tick(&rig).await;
    assert_eq!(captures(&rig), captured);
    rig.port.state.lock().unwrap().actual = ac;
    power_notice(&rig);
    tick(&rig).await;
    assert!(writes(&rig) > written);
    assert_eq!(owed(&rig), None);
    assert_eq!(performance(&rig.get().await), 2);
    let settled = captures(&rig);
    power_notice(&rig);
    tick(&rig).await;
    assert_eq!(captures(&rig), settled);
    rig.stop().await;
}

/// Steps the write epoch overtakes each time spend the debt's own budget,
/// not a fresh one: the cause's owed classes are recorded once, so the
/// second attempt finds one retry spent, and the third settles the debt.
/// Each retry waits `PACE` after the steps before it, although the epoch
/// has moved: ticks within it take no capture.
#[tokio::test]
async fn overtaken_steps_spend_the_debts_own_retries() {
    let rig = applied_game().await;
    let ac = rig.port.state.lock().unwrap().actual;
    unplug(&rig);
    tick(&rig).await;
    let written = writes(&rig);
    let custody = rig.custody.clone();
    rig.port.state.lock().unwrap().on_power = Some(Arc::new(move |_| {
        custody.lifecycle.ingress(POWER, RESUME);
    }));
    replug(&rig, ac);
    tick(&rig).await;
    assert_eq!(owed(&rig), Some(1));
    for spent in [Some(2), None] {
        let paced = captures(&rig);
        tick(&rig).await;
        assert_eq!(captures(&rig), paced);
        age(&rig, pace() - std::time::Duration::from_millis(100));
        tick(&rig).await;
        assert_eq!(captures(&rig), paced);
        age(&rig, std::time::Duration::from_millis(100));
        tick(&rig).await;
        assert!(captures(&rig) > paced);
        assert_eq!(owed(&rig), spent);
    }
    assert_eq!(writes(&rig), written);
    let settled = captures(&rig);
    age(&rig, pace());
    tick(&rig).await;
    assert_eq!(captures(&rig), settled);
    rig.port.state.lock().unwrap().on_power = None;
    rig.stop().await;
}

/// A burst of notices, such as battery-level ones in quick succession,
/// takes one stepped retry per `PACE`, so it cannot settle a debt its steps
/// could not apply yet: here an adapter that classifies only after the
/// burst still gets the plugged-in profile.
#[tokio::test]
async fn a_burst_of_notices_cannot_spend_the_debts_retries_at_once() {
    let rig = applied_game().await;
    let ac = rig.port.state.lock().unwrap().actual;
    unplug(&rig);
    tick(&rig).await;
    let written = writes(&rig);
    replug(&rig, 0x10);
    tick(&rig).await;
    assert_eq!(owed(&rig), Some(1));
    let captured = captures(&rig);
    for _ in 0..3 {
        power_notice(&rig);
        tick(&rig).await;
    }
    assert_eq!((captures(&rig), owed(&rig)), (captured, Some(1)));
    // The notices marked the cause due: the first tick after the pace runs
    // one retry with no further notice.
    age(&rig, pace());
    tick(&rig).await;
    assert!(captures(&rig) > captured);
    assert_eq!(owed(&rig), Some(2));
    let retried = captures(&rig);
    rig.port.state.lock().unwrap().actual = ac;
    power_notice(&rig);
    tick(&rig).await;
    assert_eq!(captures(&rig), retried);
    age(&rig, pace());
    tick(&rig).await;
    assert!(writes(&rig) > written);
    assert_eq!(owed(&rig), None);
    assert_eq!(performance(&rig.get().await), 2);
    rig.stop().await;
}

/// A tick that finds a request holding the mutation gate reads nothing and
/// keeps its mark, so the next tick still sees the power change.
#[tokio::test]
async fn a_busy_mutation_gate_keeps_the_cause_for_the_next_tick() {
    let rig = applied_game().await;
    let before = sent(&rig);
    unplug(&rig);
    {
        let _held = rig.runtime.hold_mutation_gate();
        tick(&rig).await;
        assert_eq!(sent(&rig), before);
    }
    tick(&rig).await;
    assert!(sent(&rig) > before);
    assert_eq!(
        performance(&rig.get().await),
        &saved_battery_performance(&rig)
    );
    rig.stop().await;
}

/// In low power a lease past its end is renewed, not expired: nothing is
/// owed and nothing is captured.
#[tokio::test]
async fn a_lease_in_low_power_is_renewed_not_expired() {
    let rig = applied_game().await;
    suspend(&rig);
    tick(&rig).await;
    let captured = captures(&rig);
    rig.runtime.expire_application_lease();
    tick(&rig).await;
    assert_eq!(owed(&rig), None);
    assert_eq!(captures(&rig), captured);
    rig.stop().await;
}

/// Modern Standby can sleep without a suspend notice: a gap in the ticks as
/// long as `SLEPT` is time the reporter could not renew in, so it renews the
/// lease. A reporter that stops while the ticks keep running still expires,
/// even after a stall just short of the gap.
#[tokio::test]
async fn a_gap_in_the_ticks_renews_the_lease_from_its_threshold() {
    let gap = crate::installed::readiness::thermal_mutation::Runtime::lifecycle_sleep_gap();
    let rig = applied_game().await;
    let before = sent(&rig);
    rig.runtime.age_lifecycle_tick(gap);
    rig.runtime.expire_application_lease();
    tick(&rig).await;
    assert_eq!(sent(&rig), before);
    assert_eq!(owed(&rig), None);
    let renewed = report(&rig, 2, Some(r"C:\Games\Game.exe")).await;
    assert_eq!(renewed["result"], json!({"status":0,"message":RENEWED}));
    // A stall short of the gap is not sleep: the lease expires.
    rig.runtime
        .age_lifecycle_tick(gap - std::time::Duration::from_secs(1));
    rig.runtime.expire_application_lease();
    tick(&rig).await;
    assert!(sent(&rig) > before);
    assert_eq!(performance(&rig.get().await), 1);
    rig.stop().await;
}

/// A request that holds the mutation gate is the Service running, not
/// sleep: a busy tick within the gap keeps its time current, so a request
/// longer than the gap still lets the lease expire at the next free tick.
/// A gap that already reached `SLEPT` is sleep, which a request taking the
/// gate at the wake does not erase: the next free tick renews the lease.
#[tokio::test]
async fn a_busy_tick_keeps_its_time_current_but_never_erases_a_gap() {
    let gap = crate::installed::readiness::thermal_mutation::Runtime::lifecycle_sleep_gap();
    let short = gap - std::time::Duration::from_secs(1);
    let rig = applied_game().await;
    let before = sent(&rig);
    age(&rig, gap);
    {
        let _held = rig.runtime.hold_mutation_gate();
        tick(&rig).await;
    }
    rig.runtime.expire_application_lease();
    tick(&rig).await;
    assert_eq!(sent(&rig), before);
    assert_eq!(owed(&rig), None);
    let renewed = report(&rig, 2, Some(r"C:\Games\Game.exe")).await;
    assert_eq!(renewed["result"], json!({"status":0,"message":RENEWED}));
    // Two stalls each short of the gap, with a busy tick between them.
    age(&rig, short);
    {
        let _held = rig.runtime.hold_mutation_gate();
        tick(&rig).await;
    }
    age(&rig, short);
    rig.runtime.expire_application_lease();
    tick(&rig).await;
    assert!(sent(&rig) > before);
    assert_eq!(performance(&rig.get().await), 1);
    rig.stop().await;
}

/// A cause whose capture meets a competing controller is dropped and owes
/// nothing: once the controller leaves, neither a later notice nor the
/// reporter's next renewal applies the profile it would have. Native refuses
/// every write under a controller, a stricter admission than managed, whose
/// thermal transition has no controller gate. Managed's rows submit nothing
/// for another reason: they compare with the desired state managed published
/// first and record drift (`externalChanges` [0]).
#[tokio::test]
async fn a_competing_controller_drops_the_cause_and_owes_nothing_after_it_leaves() {
    let vectors: Value = serde_json::from_str(LIFECYCLE_VECTORS).unwrap();
    for name in [
        "powerToBatteryWithController",
        "controllerClosedPowerUnchanged",
    ] {
        let row = vector(&vectors, name);
        assert_eq!(row["submitted"], false, "{row}");
    }
    let rig = applied_game().await;
    let ac = rig.port.state.lock().unwrap().actual;
    let (captured, written) = (captures(&rig), writes(&rig));
    rig.custody.controller.store(true, Ordering::Release);
    unplug(&rig);
    tick(&rig).await;
    assert_eq!((captures(&rig), writes(&rig)), (captured + 1, written));
    assert_eq!(owed(&rig), None);
    rig.custody.controller.store(false, Ordering::Release);
    power_notice(&rig);
    tick(&rig).await;
    unlock(&rig);
    age(&rig, pace());
    tick(&rig).await;
    assert_eq!((captures(&rig), writes(&rig)), (captured + 1, written));
    let renewed = report(&rig, 2, Some(r"C:\Games\Game.exe")).await;
    assert_eq!(renewed["result"], json!({"status":0,"message":RENEWED}));
    assert_eq!(writes(&rig), written);
    assert_eq!(
        performance(&rig.get().await),
        &vector(&vectors, "controllerClosedPowerUnchanged")["performance"]
    );
    // The next cause owes afresh.
    replug(&rig, ac);
    tick(&rig).await;
    unplug(&rig);
    tick(&rig).await;
    assert!(writes(&rig) > written);
    assert_eq!(
        performance(&rig.get().await),
        &saved_battery_performance(&rig)
    );
    rig.stop().await;
}

/// The console user is read again once the write epoch moves, so another
/// user is activated and owed every class; the first user returning after
/// it is activated again with every class owed, although its own debt was
/// settled.
#[tokio::test]
async fn a_returning_console_user_is_activated_again_with_every_class_owed() {
    const OTHER: &str = "S-1-5-21-111-222-333-1002";
    let rig = applied_game().await;
    assert_eq!(owed(&rig), None);
    *rig.custody.current_sid.lock().unwrap() = OTHER.to_owned();
    // WTS_SESSION_LOGON.
    rig.custody.lifecycle.ingress(SESSION_CHANGE, 5);
    tick(&rig).await;
    assert_eq!(rig.runtime.owed_attempts(OTHER, 7), Some(0));
    let captured = captures(&rig);
    *rig.custody.current_sid.lock().unwrap() = SID.to_owned();
    rig.custody.lifecycle.ingress(SESSION_CHANGE, 5);
    tick(&rig).await;
    assert!(captures(&rig) > captured);
    assert_eq!(owed(&rig), None);
    rig.stop().await;
}

/// A named console user is read once per write epoch, not at every tick.
#[tokio::test]
async fn a_known_console_user_is_read_once_per_epoch() {
    let rig = reported_game().await;
    let (_, reads) = probed(&rig, true).await;
    // The first tick also read it for its activation's pair.
    let first = reads.get();
    for _ in 0..3 {
        age(&rig, poll());
        tick(&rig).await;
    }
    assert_eq!(reads.get(), first);
    power_notice(&rig);
    tick(&rig).await;
    tick(&rig).await;
    assert_eq!(reads.get(), first + 1);
    rig.stop().await;
}

/// While no console user is named, the probe is read every `UNNAMED_POLL`
/// at an unmoved epoch, so a logon is seen and activated without a notice.
#[tokio::test]
async fn an_unnamed_console_user_is_polled_until_named() {
    let rig = rig_at(files_with_game(json!({"pluggedIn":{"performance":2}})), 0);
    rig.port.state.lock().unwrap().actual = 0;
    rig.metadata.store(1, Ordering::Release);
    // A tick publishes into the status feed, which the first status read opens.
    rig.get().await;
    let (known, reads) = probed(&rig, false).await;
    assert_eq!(reads.get(), 1);
    tick(&rig).await;
    age(&rig, poll() - std::time::Duration::from_millis(100));
    tick(&rig).await;
    assert_eq!(reads.get(), 1);
    age(&rig, std::time::Duration::from_millis(100));
    tick(&rig).await;
    assert_eq!(reads.get(), 2);
    assert_eq!(captures(&rig), 0);
    known.set(true);
    age(&rig, poll());
    tick(&rig).await;
    assert!(writes(&rig) > 0);
    assert_eq!(owed(&rig), None);
    assert_eq!(
        performance(&rig.get().await),
        &saved_battery_performance(&rig)
    );
    rig.stop().await;
}

/// In low power the probe is not read, however long no user is named; the
/// resume moves the write epoch, so its tick reads the user named meanwhile
/// and activates it.
#[tokio::test]
async fn a_console_user_named_in_low_power_is_activated_after_the_resume() {
    let rig = rig_at(files_with_game(json!({"pluggedIn":{"performance":2}})), 0);
    rig.port.state.lock().unwrap().actual = 0;
    rig.metadata.store(1, Ordering::Release);
    rig.get().await;
    let (known, reads) = probed(&rig, false).await;
    suspend(&rig);
    known.set(true);
    for _ in 0..3 {
        age(&rig, poll());
        tick(&rig).await;
    }
    assert_eq!((reads.get(), captures(&rig)), (1, 0));
    resume(&rig);
    tick(&rig).await;
    assert!(writes(&rig) > 0);
    assert_eq!(owed(&rig), None);
    assert_eq!(
        performance(&rig.get().await),
        &saved_battery_performance(&rig)
    );
    rig.stop().await;
}

/// A console user the probe cannot name for a while is no pair, and the
/// reporter is not taken in its place: the cause reads nothing and stays
/// pending. Once the same user is named again it is no new activation, and
/// the next epoch move applies the battery profile the unplug owed.
#[tokio::test]
async fn an_unnamed_console_user_keeps_the_cause_and_is_not_the_reporter() {
    let rig = reported_game().await;
    let known = wired(&rig).await;
    known.set(false);
    let (captured, written) = (captures(&rig), writes(&rig));
    unplug(&rig);
    tick(&rig).await;
    assert_eq!((captures(&rig), writes(&rig)), (captured, written));
    known.set(true);
    tick(&rig).await;
    assert_eq!((captures(&rig), writes(&rig)), (captured, written));
    unlock(&rig);
    tick(&rig).await;
    assert!(writes(&rig) > written);
    assert_eq!(owed(&rig), None);
    assert_eq!(
        performance(&rig.get().await),
        &saved_battery_performance(&rig)
    );
    rig.stop().await;
}

/// A reporter of another user in the console session is not the console
/// pair: the pair is the console user's, whose capture the rig refuses, so
/// nothing is written for the reporter.
#[tokio::test]
async fn a_reporter_of_another_user_in_the_console_session_is_not_the_pair() {
    const OTHER: &str = "S-1-5-21-111-222-333-1002";
    let rig = applied_game().await;
    *rig.custody.current_sid.lock().unwrap() = OTHER.to_owned();
    let (captured, written) = (captures(&rig), writes(&rig));
    unplug(&rig);
    tick(&rig).await;
    assert_eq!(captures(&rig), captured + 1);
    assert_eq!(writes(&rig), written);
    assert_eq!(rig.runtime.owed_attempts(OTHER, 7), Some(0));
    assert_eq!(performance(&rig.get().await), 2);
    rig.stop().await;
}

/// A submission that may have happened is never replayed: its unsettled
/// journal row fences the store, so a later power change, resume and lease
/// expiry each run nothing and keep the debt owed.
#[tokio::test]
async fn an_uncertain_prior_submission_is_never_replayed_by_a_lifecycle_tick() {
    let rig = rig_at(files_with_game(json!({"pluggedIn":{"performance":2}})), 0);
    rig.port.state.lock().unwrap().fail_write = Some(1);
    report(&rig, 1, Some(r"C:\Games\Game.exe")).await;
    assert_eq!(writes(&rig), 1);
    assert!(rig.storage.pending());
    wired(&rig).await;
    let before = sent(&rig);
    unplug(&rig);
    tick(&rig).await;
    suspend(&rig);
    tick(&rig).await;
    resume(&rig);
    tick(&rig).await;
    rig.runtime.expire_application_lease();
    tick(&rig).await;
    assert_eq!(writes(&rig), 1);
    assert_eq!(sent(&rig), before);
    assert!(rig.storage.pending());
    assert_eq!(owed(&rig), Some(0));
    rig.stop().await;
}

/// Delta review F1 and F2 at an AC unplug: an uncertain GPU clock
/// submission with the dGPU idle fences the tick's reconciliation at its
/// admission. The power change rearms the fence's one ungated query: a
/// driver that reads resolves it and the battery profile is applied once;
/// one that does not keeps the cause and the debt, with no firmware read.
/// Before the fix the refused steps settled the debt and the rearm was
/// never taken.
#[cfg(feature = "service-gpu-clock-tuning-v1")]
#[tokio::test]
async fn an_unplug_reconciles_once_a_read_resolves_a_pending_gpu_confirmation() {
    use super::super::super::gpu_clock_tuning::{compose, fenced, leave_uncertain};
    use crate::installed::readiness::{gpu_clock_offsets::fake::Fake, thermal_gpu::CALL_BUDGET};
    let vectors: Value = serde_json::from_str(LIFECYCLE_VECTORS).unwrap();
    for readable in [false, true] {
        let rig = applied_game().await;
        let fake = Fake::new(0, 0);
        let lane = compose(&rig, &fake, Arc::default());
        leave_uncertain(&rig, &lane, &fake);
        let sets = fake.sets();
        fake.with(|d| d.queries_fail = !readable);
        let before = sent(&rig);
        unplug(&rig);
        tick(&rig).await;
        if readable {
            assert!(sent(&rig) > before);
            assert_eq!(owed(&rig), None);
            assert!(!fenced(&rig, CALL_BUDGET));
            assert_eq!(
                performance(&rig.get().await),
                &vector(&vectors, "powerToBattery")["desired"]
            );
        } else {
            assert_eq!(sent(&rig), before);
            assert_eq!(owed(&rig), Some(0));
            assert!(fenced(&rig, CALL_BUDGET));
        }
        assert_eq!(fake.sets(), sets);
        rig.stop().await;
    }
}

/// An unplug whose attempt a pending GPU clock confirmation refuses, with the
/// driver readable again but the dGPU idle and the ungated query spent: the
/// fence holds and the cause is parked by it. Returns the lane and the driver,
/// and the frames sent before the unplug.
#[cfg(feature = "service-gpu-clock-tuning-v1")]
async fn parked_by_gpu(
    rig: &Rig,
) -> (
    Arc<crate::installed::readiness::thermal_gpu::Lane>,
    crate::installed::readiness::gpu_clock_offsets::fake::Fake,
    usize,
) {
    use super::super::super::gpu_clock_tuning::{compose, leave_uncertain};
    let fake = crate::installed::readiness::gpu_clock_offsets::fake::Fake::new(0, 0);
    let lane = compose(rig, &fake, Arc::default());
    leave_uncertain(rig, &lane, &fake);
    let before = sent(rig);
    unplug(rig);
    tick(rig).await;
    assert_eq!((sent(rig), owed(rig)), (before, Some(0)));
    fake.with(|d| d.queries_fail = false);
    (lane, fake, before)
}
/// Resolves the management read-only, as an explicit performance step's
/// query would, so the GPU fence clears without a write epoch move.
#[cfg(feature = "service-gpu-clock-tuning-v1")]
fn resolve_gpu(rig: &Rig, lane: &crate::installed::readiness::thermal_gpu::Lane) {
    use super::super::super::gpu_clock_tuning::fenced;
    let _ = lane.call_for_tests(|m| {
        m.rearm();
        let _ = m.read();
    });
    assert!(!fenced(
        rig,
        crate::installed::readiness::thermal_gpu::CALL_BUDGET
    ));
}

/// Delta review LOW-1: a cause the GPU fence parked runs as soon as the
/// fence clears, which moves no write epoch, so a default install that sends
/// no reports still gets the battery profile (managed reruns its deferred
/// transition after each periodic observation). While the fence holds, the
/// ticks take no capture. Before the fix the cause waited for an unrelated
/// epoch move.
#[cfg(feature = "service-gpu-clock-tuning-v1")]
#[tokio::test]
async fn a_cause_the_gpu_fence_parked_runs_once_the_fence_clears() {
    let vectors: Value = serde_json::from_str(LIFECYCLE_VECTORS).unwrap();
    let rig = applied_game().await;
    let (lane, fake, before) = parked_by_gpu(&rig).await;
    let sets = fake.sets();
    let parked = captures(&rig);
    for _ in 0..2 {
        tick(&rig).await;
    }
    assert_eq!(
        (captures(&rig), sent(&rig), owed(&rig)),
        (parked, before, Some(0))
    );
    resolve_gpu(&rig, &lane);
    tick(&rig).await;
    assert!(sent(&rig) > before);
    assert_eq!(owed(&rig), None);
    assert_eq!(
        performance(&rig.get().await),
        &vector(&vectors, "powerToBattery")["desired"]
    );
    assert_eq!(fake.sets(), sets);
    rig.stop().await;
}

/// A parked cause whose retry after the fence clears is refused for another
/// reason, here a locked session, waits for the write epoch again: the
/// cleared fence does not make it due at every tick. The unlock applies it.
#[cfg(feature = "service-gpu-clock-tuning-v1")]
#[tokio::test]
async fn a_parked_cause_refused_otherwise_waits_for_the_epoch_again() {
    let rig = applied_game().await;
    let (lane, _fake, before) = parked_by_gpu(&rig).await;
    resolve_gpu(&rig, &lane);
    rig.custody.fresh_refuse.store(true, Ordering::Release);
    let parked = captures(&rig);
    tick(&rig).await;
    let refused = captures(&rig);
    assert!(refused > parked);
    for _ in 0..2 {
        tick(&rig).await;
    }
    assert_eq!(
        (captures(&rig), sent(&rig), owed(&rig)),
        (refused, before, Some(0))
    );
    rig.custody.fresh_refuse.store(false, Ordering::Release);
    unlock(&rig);
    tick(&rig).await;
    assert!(sent(&rig) > before);
    assert_eq!(owed(&rig), None);
    rig.stop().await;
}

/// Without any report, the console user's pair is reconciled: managed's
/// power-source change needs no report, and by default no application rule
/// exists, so no reporter ever sends one.
#[tokio::test]
async fn a_power_change_without_a_reporter_applies_the_console_users_profile() {
    let rig = rig_at(files_with_game(json!({"pluggedIn":{"performance":2}})), 0);
    // A tick publishes into the status feed, which the first status read opens.
    rig.get().await;
    wired(&rig).await;
    // The first tick activated the console user on AC.
    assert!(captures(&rig) > 0);
    assert_eq!(owed(&rig), None);
    unplug(&rig);
    tick(&rig).await;
    assert!(writes(&rig) > 0);
    assert_eq!(owed(&rig), None);
    assert_eq!(
        performance(&rig.get().await),
        &saved_battery_performance(&rig)
    );
    rig.stop().await;
}

/// After a switch to another console session the latest reporter is not the
/// console user: the console session's pair is reconciled.
#[tokio::test]
async fn a_console_switch_reconciles_the_console_session_not_the_reporter() {
    let rig = applied_game().await;
    rig.custody.current_session.store(8, Ordering::Release);
    assert_eq!(rig.runtime.owed_attempts(SID, 8), Some(0));
    unplug(&rig);
    tick(&rig).await;
    assert!(writes(&rig) > 0);
    assert_eq!(rig.runtime.owed_attempts(SID, 8), None);
    assert_eq!(
        performance(&rig.get().await),
        &saved_battery_performance(&rig)
    );
    rig.stop().await;
}

/// With no console user known, a tick reads nothing whatever the lifecycle
/// did.
#[tokio::test]
async fn a_tick_without_a_console_user_reads_nothing() {
    let rig = rig_at(files_with_game(json!({"pluggedIn":{"performance":2}})), 0);
    // A tick publishes into the status feed, which the first status read opens.
    rig.get().await;
    rig.runtime.read_console(|| None);
    unconsoled(&rig).await;
    unplug(&rig);
    tick(&rig).await;
    suspend(&rig);
    resume(&rig);
    unlock(&rig);
    tick(&rig).await;
    assert_eq!(captures(&rig), 0);
    assert_eq!(sent(&rig), 0);
    assert_eq!(rig.port.state.lock().unwrap().opened, 0);
    rig.stop().await;
}

/// The first console user after start is activated, as managed
/// `ActivateUserAsync` applies a new active user's profile: here on battery,
/// with no report and no other cause. The same user in a new session, as
/// after a logoff and logon, is no new activation and reads nothing.
#[tokio::test]
async fn the_console_users_activation_applies_its_profile_once() {
    let vectors: Value = serde_json::from_str(LIFECYCLE_VECTORS).unwrap();
    let rig = rig_at(files_with_game(json!({"pluggedIn":{"performance":2}})), 0);
    rig.port.state.lock().unwrap().actual = 0;
    rig.metadata.store(1, Ordering::Release);
    // A tick publishes into the status feed, which the first status read opens.
    rig.get().await;
    wired(&rig).await;
    let activated = vector(&vectors, "userActivatedOnBattery");
    assert_eq!(activated["submitted"], true, "{activated}");
    assert_eq!(activated["source"], 1, "{activated}");
    assert!(writes(&rig) > 0);
    assert_eq!(owed(&rig), None);
    assert_eq!(performance(&rig.get().await), &activated["desired"]);
    assert_eq!(saved_battery_performance(&rig), activated["desired"]);
    let again = vector(&vectors, "sameUserReactivated");
    assert_eq!(again["submitted"], false, "{again}");
    let (captured, written) = (captures(&rig), writes(&rig));
    rig.custody.current_session.store(8, Ordering::Release);
    // WTS_SESSION_LOGON.
    rig.custody.lifecycle.ingress(SESSION_CHANGE, 5);
    tick(&rig).await;
    assert_eq!((captures(&rig), writes(&rig)), (captured, written));
    rig.stop().await;
}

/// An observeState request, a fresh exact-device thermal reading the drift
/// tracker compares with the last one (`thermal_drift::Tracker`), then the
/// published status.
async fn observe(rig: &Rig, n: u8) -> Value {
    rig.get().await;
    let observed = rig
        .request(json!({"operation":"observeState","version":90,"requestId":format!("dddddddd-bbbb-cccc-dddd-{n:012}"),"includeLiveReadings":false,"requestedLiveReadings":0,"effectiveLiveReadings":0}))
        .await;
    assert_eq!(observed["success"], true, "{observed}");
    rig.get().await
}
/// Firmware moved outside OpenBlade to `selector` with `fan`.
fn moved(rig: &Rig, selector: u8, fan: u8) {
    let mut state = rig.port.state.lock().unwrap();
    state.selectors = [selector; 2];
    state.fans = [fan; 2];
}
/// A fresh reading's confirmed preset and fans and the external changes as
/// one managed vector row records them, and the desired preset where the
/// row's desired state is a power profile's. The published desired source
/// is not compared, nor an application preset's desired value: native
/// status reports the plugged-in power profile's preset and source on AC
/// even while an application preset is applied, with or without drift.
async fn assert_row(rig: &Rig, n: u8, row: &Value) {
    let status = observe(rig, n).await;
    let status = &status["status"];
    assert_eq!(
        status["confirmedState"]["performance"], row["performance"],
        "{status}"
    );
    assert_eq!(status["confirmedState"]["fans"], row["fans"], "{status}");
    if row["source"] == 1 {
        assert_eq!(
            status["desiredState"]["performance"]["value"], row["desired"],
            "{status}"
        );
    }
    assert_eq!(
        status["externalChanges"], row["externalChanges"],
        "{status}"
    );
}

/// Performance changed outside OpenBlade excludes both thermal subsystems
/// from each later automatic reconciliation (managed
/// `ResolveAutomaticWriteExclusions`): a power change either way, an expired
/// lease and a report each write nothing and settle their debt.
#[tokio::test]
async fn performance_drift_excludes_every_automatic_reconciliation_and_settles_it() {
    let vectors: Value = serde_json::from_str(LIFECYCLE_VECTORS).unwrap();
    let rig = applied_game().await;
    let ac = rig.port.state.lock().unwrap().actual;
    let before = writes(&rig);
    // Silent.
    moved(&rig, 5, 0);
    assert_row(&rig, 1, vector(&vectors, "performanceDriftObserved")).await;
    for (row, cause) in [
        ("powerToBatteryWithPerformanceDrift", 0),
        ("powerToPluggedInWithPerformanceDrift", 1),
    ] {
        if cause == 0 {
            unplug(&rig);
        } else {
            replug(&rig, ac);
        }
        tick(&rig).await;
        let row = vector(&vectors, row);
        assert_eq!(row["submitted"], false, "{row}");
        assert_eq!(writes(&rig), before);
        assert_eq!(owed(&rig), None);
        assert_row(&rig, 2 + cause, row).await;
    }
    rig.runtime.expire_application_lease();
    tick(&rig).await;
    let row = vector(&vectors, "leaseExpiredWithPerformanceDrift");
    assert_eq!(row["submitted"], false, "{row}");
    assert_eq!(writes(&rig), before);
    assert_row(&rig, 4, row).await;
    let reply = report(&rig, 2, Some(r"C:\Games\Game.exe")).await;
    assert_eq!(reply["result"]["status"], 2, "{reply}");
    let row = vector(&vectors, "gameReportedWithPerformanceDrift");
    assert_eq!(row["submitted"], false, "{row}");
    assert_eq!(writes(&rig), before);
    assert_eq!(owed(&rig), None);
    assert_row(&rig, 5, row).await;
    rig.stop().await;
}

/// Fans changed outside OpenBlade exclude performance too; once firmware
/// returns to the fans OpenBlade resolved, the next reconciliation clears
/// the change.
#[tokio::test]
async fn manual_fan_drift_excludes_both_until_the_fans_return() {
    let vectors: Value = serde_json::from_str(LIFECYCLE_VECTORS).unwrap();
    let rig = applied_game().await;
    let ac = rig.port.state.lock().unwrap().actual;
    let before = writes(&rig);
    // Performance with manual fans.
    moved(&rig, 2, 1);
    assert_eq!(
        observe(&rig, 1).await["status"]["externalChanges"],
        json!([1])
    );
    unplug(&rig);
    tick(&rig).await;
    let row = vector(&vectors, "powerToBatteryWithManualFanDrift");
    assert_eq!(row["submitted"], false, "{row}");
    assert_eq!(writes(&rig), before);
    assert_eq!(owed(&rig), None);
    let status = observe(&rig, 2).await;
    assert_eq!(status["status"]["externalChanges"], row["externalChanges"]);
    assert_eq!(
        status["status"]["desiredState"]["performance"]["value"],
        row["desired"]
    );
    moved(&rig, 2, 0);
    replug(&rig, ac);
    tick(&rig).await;
    let row = vector(&vectors, "powerToPluggedInAfterFansReturnedToAutomatic");
    assert_eq!(row["submitted"], false, "{row}");
    assert_eq!(writes(&rig), before);
    assert_row(&rig, 3, row).await;
    rig.stop().await;
}

/// An observed HyperBoost excludes both thermal subsystems from a power
/// change without recording an external change.
#[tokio::test]
async fn an_observed_hyperboost_excludes_a_power_change() {
    let vectors: Value = serde_json::from_str(LIFECYCLE_VECTORS).unwrap();
    let rig = applied_game().await;
    let before = writes(&rig);
    moved(&rig, 7, 0);
    unplug(&rig);
    tick(&rig).await;
    let row = vector(&vectors, "powerToBatteryHyperBoostActive");
    assert_eq!(row["submitted"], false, "{row}");
    assert_eq!(writes(&rig), before);
    assert_eq!(owed(&rig), None);
    let status = observe(&rig, 1).await;
    assert_eq!(status["status"]["externalChanges"], row["externalChanges"]);
    rig.stop().await;
}

/// A resume whose power change reads neither intent is held: a fresh
/// reading of the destination within the window clears the change, and one
/// past it leaves the change recorded.
#[tokio::test]
async fn a_resume_holds_a_performance_change_for_its_window() {
    let vectors: Value = serde_json::from_str(LIFECYCLE_VECTORS).unwrap();
    for late in [false, true] {
        let rig = applied_game().await;
        let before = writes(&rig);
        suspend(&rig);
        unplug(&rig);
        tick(&rig).await;
        // The firmware battery preset before it settles.
        moved(&rig, 6, 0);
        resume(&rig);
        tick(&rig).await;
        let row = vector(&vectors, "resumePowerToBatteryUnsettled");
        assert_eq!(row["submitted"], false, "{row}");
        assert_eq!(writes(&rig), before);
        assert_eq!(owed(&rig), None);
        assert_row(&rig, 1, row).await;
        // Battery Saver.
        moved(&rig, 3, 0);
        if late {
            rig.runtime
                .age_drift_hold(std::time::Duration::from_secs(60));
        }
        if late {
            let status = observe(&rig, 2).await;
            assert_eq!(status["status"]["externalChanges"], json!([0]), "{status}");
        } else {
            assert_row(&rig, 2, vector(&vectors, "resumeSettledWithinWindow")).await;
        }
        assert_eq!(writes(&rig), before);
        rig.stop().await;
    }
}

/// A user activation applies over drift (managed
/// `preserveObservedExternalChanges: false`), and its own readback clears
/// the change.
#[tokio::test]
async fn an_activation_applies_over_drift() {
    const OTHER: &str = "S-1-5-21-111-222-333-1002";
    let vectors: Value = serde_json::from_str(LIFECYCLE_VECTORS).unwrap();
    let rig = applied_game().await;
    *rig.custody.current_sid.lock().unwrap() = OTHER.to_owned();
    // WTS_SESSION_LOGON.
    rig.custody.lifecycle.ingress(SESSION_CHANGE, 5);
    tick(&rig).await;
    moved(&rig, 5, 0);
    assert_eq!(
        observe(&rig, 1).await["status"]["externalChanges"],
        json!([0])
    );
    let row = vector(&vectors, "performanceDriftObservedBeforeActivation");
    assert_eq!(row["externalChanges"], json!([0]), "{row}");
    let before = writes(&rig);
    *rig.custody.current_sid.lock().unwrap() = SID.to_owned();
    rig.custody.lifecycle.ingress(SESSION_CHANGE, 5);
    tick(&rig).await;
    let row = vector(&vectors, "userActivatedWithPerformanceDrift");
    assert_eq!(row["submitted"], true, "{row}");
    assert!(writes(&rig) > before);
    assert_eq!(owed(&rig), None);
    let status = observe(&rig, 2).await;
    assert_eq!(status["status"]["confirmedState"]["performance"], 2);
    assert_eq!(status["status"]["externalChanges"], row["externalChanges"]);
    rig.stop().await;
}

/// OpenBlade's own explicit write clears a recorded change by its readback
/// (managed `ConfirmIntentional`), and a restart forgets it: the first
/// reading after it only sets the baseline.
#[tokio::test]
async fn an_own_write_clears_drift_and_a_restart_forgets_it() {
    let rig = applied_game().await;
    moved(&rig, 5, 0);
    assert_eq!(
        observe(&rig, 1).await["status"]["externalChanges"],
        json!([0])
    );
    assert_eq!(rig.set(1, value(2, 1)).await["result"]["status"], 1);
    assert_eq!(
        observe(&rig, 2).await["status"]["externalChanges"],
        json!([])
    );
    moved(&rig, 5, 0);
    assert_eq!(
        observe(&rig, 3).await["status"]["externalChanges"],
        json!([0])
    );
    rig.runtime.restart_drift();
    assert_eq!(
        observe(&rig, 4).await["status"]["externalChanges"],
        json!([])
    );
    moved(&rig, 2, 0);
    assert_eq!(
        observe(&rig, 5).await["status"]["externalChanges"],
        json!([0])
    );
    rig.stop().await;
}

/// A competing controller that drops the console user's first activation
/// settles it, which ends that user's startup: a later power change compares
/// the change recorded outside OpenBlade and is paused.
#[tokio::test]
async fn an_activation_dropped_for_a_competing_controller_ends_startup() {
    let rig = rig_at(files_with_game(json!({"pluggedIn":{"performance":2}})), 0);
    assert_ne!(saved_battery_performance(&rig), json!(2));
    rig.get().await;
    rig.custody.controller.store(true, Ordering::Release);
    wired(&rig).await;
    assert_eq!(writes(&rig), 0);
    assert_eq!(owed(&rig), None);
    rig.custody.controller.store(false, Ordering::Release);
    assert_eq!(
        observe(&rig, 1).await["status"]["externalChanges"],
        json!([])
    );
    // Performance, outside OpenBlade.
    moved(&rig, 2, 0);
    assert_eq!(
        observe(&rig, 2).await["status"]["externalChanges"],
        json!([0])
    );
    unplug(&rig);
    tick(&rig).await;
    assert_eq!(writes(&rig), 0);
    assert_eq!(owed(&rig), None);
    let status = observe(&rig, 3).await;
    assert_eq!(status["status"]["externalChanges"], json!([0]), "{status}");
    assert_eq!(performance(&status), 2);
    rig.stop().await;
}

/// A refused activation and a power change merged into one cause keep the
/// activation's overwrite: once admitted, the tick applies the battery
/// profile over the change recorded outside OpenBlade.
#[tokio::test]
async fn a_merged_cause_keeps_a_refused_activations_overwrite() {
    let rig = reported_game().await;
    // Silent, outside OpenBlade.
    moved(&rig, 5, 0);
    assert_eq!(
        observe(&rig, 1).await["status"]["externalChanges"],
        json!([0])
    );
    let before = writes(&rig);
    rig.custody.fresh_refuse.store(true, Ordering::Release);
    wired(&rig).await;
    unplug(&rig);
    tick(&rig).await;
    assert_eq!(writes(&rig), before);
    rig.custody.fresh_refuse.store(false, Ordering::Release);
    unlock(&rig);
    tick(&rig).await;
    assert!(writes(&rig) > before);
    assert_eq!(owed(&rig), None);
    let status = observe(&rig, 2).await;
    assert_eq!(performance(&status), &saved_battery_performance(&rig));
    assert_eq!(status["status"]["externalChanges"], json!([]), "{status}");
    rig.stop().await;
}

/// An observed HyperBoost excludes a report on AC as it does a power change.
#[tokio::test]
async fn an_observed_hyperboost_excludes_a_report_on_ac() {
    let rig = applied_game().await;
    let before = writes(&rig);
    moved(&rig, 7, 0);
    let reply = report(&rig, 2, Some(r"C:\Tools\Other.exe")).await;
    assert_eq!(reply["result"]["status"], 2, "{reply}");
    assert!(
        reply["result"]["message"]
            .as_str()
            .unwrap()
            .contains("HyperBoost remains active"),
        "{reply}"
    );
    assert_eq!(writes(&rig), before);
    assert_eq!(
        observe(&rig, 1).await["status"]["externalChanges"],
        json!([])
    );
    rig.stop().await;
}

/// A power change soon after a resume is held as one during it is: the hold
/// lasts the whole window from the boundary, whether the change comes in
/// low power or after the resume, and a status reading of the destination
/// clears it although the status source reports a production controller
/// warning (the hold compares no controller evidence; the write path keeps
/// its own admission). A change after the window is recorded at once.
#[tokio::test]
async fn a_power_change_within_the_resume_window_is_held() {
    use crate::installed::readiness::status_source::PRODUCTION_CONTROLLERS;
    let vectors: Value = serde_json::from_str(LIFECYCLE_VECTORS).unwrap();
    for (after_resume, late) in [(false, false), (true, false), (true, true)] {
        let rig = applied_game().await;
        let before = writes(&rig);
        suspend(&rig);
        if !after_resume {
            unplug(&rig);
        }
        tick(&rig).await;
        // The firmware battery preset before it settles.
        moved(&rig, 6, 0);
        resume(&rig);
        tick(&rig).await;
        if after_resume {
            if late {
                age(&rig, thermal_drift_window());
            }
            unplug(&rig);
            tick(&rig).await;
        }
        assert_eq!(writes(&rig), before, "{after_resume} {late}");
        assert_eq!(owed(&rig), None);
        let row = vector(&vectors, "resumePowerToBatteryUnsettled");
        assert_row(&rig, 1, row).await;
        rig.metadata
            .store(1 | PRODUCTION_CONTROLLERS, Ordering::Release);
        // Battery Saver.
        moved(&rig, 3, 0);
        let status = observe(&rig, 2).await;
        assert!(
            !status["status"]["controllerDetectionWarning"].is_null(),
            "{status}"
        );
        let expected = if late {
            json!([0])
        } else {
            vector(&vectors, "resumeSettledWithinWindow")["externalChanges"].clone()
        };
        assert_eq!(
            status["status"]["externalChanges"], expected,
            "{after_resume} {late}: {status}"
        );
        assert_eq!(writes(&rig), before);
        rig.stop().await;
    }
}

/// A later boundary ends a resume hold: the destination read after the next
/// resume stays recorded as a change.
#[tokio::test]
async fn a_later_boundary_drops_a_resume_hold() {
    let rig = applied_game().await;
    suspend(&rig);
    unplug(&rig);
    tick(&rig).await;
    moved(&rig, 6, 0);
    resume(&rig);
    tick(&rig).await;
    assert_eq!(
        observe(&rig, 1).await["status"]["externalChanges"],
        json!([0])
    );
    suspend(&rig);
    tick(&rig).await;
    resume(&rig);
    tick(&rig).await;
    moved(&rig, 3, 0);
    assert_eq!(
        observe(&rig, 2).await["status"]["externalChanges"],
        json!([0])
    );
    rig.stop().await;
}
fn thermal_drift_window() -> std::time::Duration {
    crate::installed::readiness::thermal_drift::RESUME_WINDOW
}

/// `otherReportedWithOwnPreset`: managed pauses a report of Other.exe over
/// OpenBlade's own Game preset, because it publishes the new desired state
/// before the transition compares the reading, so its own preset reads as
/// drift. Native compares OpenBlade's own confirmed state instead and
/// applies the power profile, the same deliberate divergence as the lease
/// expiry (`an_expired_lease_applies_the_power_profile_once`). The fans,
/// desired and source columns agree.
#[tokio::test]
async fn a_report_over_its_own_preset_applies_where_managed_pauses() {
    let vectors: Value = serde_json::from_str(LIFECYCLE_VECTORS).unwrap();
    let row = vector(&vectors, "otherReportedWithOwnPreset");
    assert_eq!(
        (&row["submitted"], &row["externalChanges"]),
        (&json!(false), &json!([0]))
    );
    let rig = applied_game().await;
    let before = writes(&rig);
    let reply = report(&rig, 2, Some(r"C:\Tools\Other.exe")).await;
    assert_eq!(reply["result"]["status"], 1, "{reply}");
    assert!(writes(&rig) > before);
    let status = observe(&rig, 1).await;
    let status = &status["status"];
    assert_eq!(status["confirmedState"]["performance"], 1);
    assert_eq!(status["externalChanges"], json!([]));
    assert_eq!(status["confirmedState"]["fans"], row["fans"]);
    assert_eq!(
        status["desiredState"]["performance"]["value"],
        row["desired"]
    );
    assert_eq!(
        status["desiredState"]["performance"]["source"],
        row["source"]
    );
    rig.stop().await;
}

/// `powerToBatteryWithStopFanDrift`: native reads only the manual-fan bit,
/// so Stop fans set outside OpenBlade read as manual fans, as Fixed ones do:
/// the change to battery submits nothing and records the fan change. The
/// submitted, desired, source and external-change columns agree. The fans
/// column is Stop (4) in managed; native publishes every manual-fan reading
/// as 3 (`ObservedPerformance::projection`), Fixed ones included.
#[tokio::test]
async fn stop_fans_set_outside_openblade_read_as_manual_fan_drift() {
    let vectors: Value = serde_json::from_str(LIFECYCLE_VECTORS).unwrap();
    let row = vector(&vectors, "powerToBatteryWithStopFanDrift");
    assert_eq!(
        (&row["submitted"], &row["fans"]),
        (&json!(false), &json!(4))
    );
    let rig = applied_game().await;
    let before = writes(&rig);
    // Performance with the manual-fan bit, as firmware reports Stop.
    moved(&rig, 2, 1);
    unplug(&rig);
    tick(&rig).await;
    assert_eq!(writes(&rig), before);
    assert_eq!(owed(&rig), None);
    let status = observe(&rig, 1).await;
    let status = &status["status"];
    assert_eq!(status["confirmedState"]["performance"], row["performance"]);
    assert_eq!(status["confirmedState"]["fans"], 3);
    assert_eq!(status["externalChanges"], row["externalChanges"]);
    assert_eq!(
        status["desiredState"]["performance"]["value"],
        row["desired"]
    );
    assert_eq!(
        status["desiredState"]["performance"]["source"],
        row["source"]
    );
    rig.stop().await;
}

/// `powerToBatteryHyperBoostUnknown`: managed's HyperBoost status read is
/// unknown. Native has no separate HyperBoost read (selector 7 is
/// HyperBoost), so its nearest case is an undecodable reading, here a
/// selector no reviewed preset uses. The change to battery then submits
/// nothing and records no change, as managed does, but the reading is no
/// evidence either: the cause stays owed for a retry and the confirmed state
/// stays the last reading. The desired, source and external-change columns
/// agree; native has no fans reading to compare.
#[tokio::test]
async fn an_undecodable_reading_keeps_a_power_change_owed() {
    let vectors: Value = serde_json::from_str(LIFECYCLE_VECTORS).unwrap();
    let row = vector(&vectors, "powerToBatteryHyperBoostUnknown");
    assert_eq!(row["submitted"], false, "{row}");
    let rig = applied_game().await;
    let before = writes(&rig);
    moved(&rig, 9, 0);
    unplug(&rig);
    tick(&rig).await;
    assert_eq!(writes(&rig), before);
    assert_eq!(owed(&rig), Some(1));
    let status = rig.get().await;
    let status = &status["status"];
    assert_eq!(status["confirmedState"]["performance"], 2);
    assert_eq!(status["externalChanges"], row["externalChanges"]);
    assert_eq!(
        status["desiredState"]["performance"]["value"],
        row["desired"]
    );
    assert_eq!(
        status["desiredState"]["performance"]["source"],
        row["source"]
    );
    rig.stop().await;
}

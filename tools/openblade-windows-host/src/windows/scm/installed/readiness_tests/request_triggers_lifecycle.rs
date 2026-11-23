//! Lifecycle-driven reconciliation (`Runtime::reconcile_lifecycle`): the
//! origin tick applies what the reporting pair owes after a resume, a
//! gate-clearing DisplayOn, a Windows power-kind change or an expired
//! application lease. Each case drives the tick the serving loop runs every
//! 500 ms over the rig's lifecycle, store and scripted mailbox; no hardware or
//! operating-system supplier is reached.
use super::*;
use openblade_domain::power_setting::{PBT_POWERSETTINGCHANGE, PowerSetting};

/// The managed `ApplyFullTransitionAsync` outcomes after each lifecycle
/// transition (`ServiceControlRuntimeTests.NativeLifecycleReconcileVectors`).
const LIFECYCLE_VECTORS: &str =
    include_str!("../../../../../tests/data/lifecycle-reconcile-vectors.json");

/// Wires the rig's lifecycle in as the runtime's gate, write epoch and power
/// generation,
/// as `Runtime::native` does, and takes the tick's first mark.
async fn wired(rig: &Rig) {
    gated(rig);
    let epoch = rig.custody.clone();
    rig.runtime
        .read_admission_epoch(move || epoch.lifecycle.active());
    let custody = rig.custody.clone();
    rig.runtime
        .read_power_generation(move || custody.lifecycle.observe_power_kind(None));
    tick(rig).await;
}
/// One origin tick's lifecycle reconciliation, as the serving loop runs it.
async fn tick(rig: &Rig) {
    let reconcile = rig.source.lock().await.reconcile_lifecycle();
    if let Some(reconcile) = reconcile {
        reconcile.await.unwrap();
    }
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
/// Windows reports battery, as the SCM power notice samples it.
fn unplug(rig: &Rig) {
    rig.port.state.lock().unwrap().actual = 0;
    rig.metadata.store(1, Ordering::Release);
    rig.custody.lifecycle.observe_power_kind(Some(1));
}
fn replug(rig: &Rig, actual: u8) {
    rig.port.state.lock().unwrap().actual = actual;
    rig.metadata.store(0, Ordering::Release);
    rig.custody.lifecycle.observe_power_kind(Some(2));
}
fn saved_battery_performance(rig: &Rig) -> Value {
    rig.files.document("profiles.json")["profiles"]["battery"]["performance"].clone()
}
/// A rig whose Game rule is applied on AC, with the tick wired and marked.
/// Its saved performance is the managed recorder's.
async fn applied_game() -> Rig {
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
    wired(&rig).await;
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
    for _ in 0..3 {
        tick(&rig).await;
    }
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
    rig.custody.lifecycle.observe_power_kind(Some(2));
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
    suspend(&rig);
    unplug(&rig);
    tick(&rig).await;
    assert_eq!(sent(&rig), before);
    assert_eq!(owed(&rig), Some(0));
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
/// and preset, but submits nothing here: `PublishApplicationContextStatus`
/// publishes the new desired state before `ApplyFullTransitionAsync` takes it
/// as the prior one, so its drift tracker reads OpenBlade's own Game preset as
/// an external change and pauses. Native has no drift tracker and applies, as
/// its report that leaves a rule already does.
#[tokio::test]
async fn an_expired_lease_applies_the_power_profile_once() {
    let vectors: Value = serde_json::from_str(LIFECYCLE_VECTORS).unwrap();
    let rig = applied_game().await;
    let before = sent(&rig);
    rig.runtime.expire_application_lease();
    tick(&rig).await;
    assert!(sent(&rig) > before);
    assert_eq!(owed(&rig), None);
    let expired = vector(&vectors, "leaseExpired");
    assert_eq!(expired["source"], 1, "{expired}");
    assert_eq!(expired["submitted"], false, "{expired}");
    assert_eq!(performance(&rig.get().await), &expired["desired"]);
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
/// resume applies it once.
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
    rig.runtime.expire_application_lease();
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
    assert_eq!(performance(&rig.get().await), 1);
    let after = sent(&rig);
    tick(&rig).await;
    assert_eq!(sent(&rig), after);
    rig.stop().await;
}

/// A fence that leaves the gate generation in place, here a repeated resume
/// while the tick's performance step reads the adapter, refuses that step's
/// write with nothing submitted. It is no deferral, but the debt stays owed
/// with one retry spent, and the next report applies it once under fresh
/// admission (the second debt drop of the low-power slice).
#[tokio::test]
async fn a_fence_without_a_gate_move_keeps_the_debt_for_the_next_report() {
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
    rig.runtime.expire_application_lease();
    tick(&rig).await;
    assert!(once.load(Ordering::Acquire), "the step read the adapter");
    assert_eq!(
        rig.custody.lifecycle.low_power().unwrap().generation,
        generation
    );
    assert_eq!(writes(&rig), before);
    assert_eq!(owed(&rig), Some(1));
    rig.port.state.lock().unwrap().on_power = None;
    // No cause, so the tick reads nothing; the next report reconciles.
    let idle = sent(&rig);
    tick(&rig).await;
    assert_eq!(sent(&rig), idle);
    let applied = report(&rig, 2, Some(r"C:\Tools\Other.exe")).await;
    assert_eq!(applied["result"]["status"], 1, "{applied}");
    assert!(writes(&rig) > before);
    assert_eq!(owed(&rig), None);
    assert_eq!(performance(&rig.get().await), 1);
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

/// A tick without a reporter reads nothing, whatever the lifecycle did.
#[tokio::test]
async fn a_tick_without_a_reporter_reads_nothing() {
    let files = MemoryFiles::default();
    files.profiles(2);
    let rig = Rig::new(files);
    wired(&rig).await;
    unplug(&rig);
    suspend(&rig);
    resume(&rig);
    tick(&rig).await;
    assert_eq!(sent(&rig), 0);
    assert_eq!(rig.port.state.lock().unwrap().opened, 0);
    rig.stop().await;
}

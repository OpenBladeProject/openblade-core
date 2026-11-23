//! Lifecycle-driven reconciliation (`Runtime::reconcile_lifecycle`): the
//! origin tick applies what the console pair owes after a resume, a
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
/// with one retry spent, and since the write epoch moved the first tick after
/// `PACE` runs the cause again under fresh admission (the second debt drop of
/// the low-power slice).
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
    tick(&rig).await;
    assert_eq!(writes(&rig), before);
    age(&rig, pace());
    tick(&rig).await;
    assert!(writes(&rig) > before);
    assert_eq!(owed(&rig), None);
    assert_eq!(performance(&rig.get().await), 1);
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
/// reporter's next renewal applies the profile it would have, as managed
/// applies nothing at a power change with a controller present and nothing
/// once it leaves.
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

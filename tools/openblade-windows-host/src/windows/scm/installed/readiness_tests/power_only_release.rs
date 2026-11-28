//! Managed's stale power-only write block (`ChangePowerSourceCore`,
//! 1a03e358 and 3f7b3838): after a reading whose adapter classes conflict
//! with Windows power, a classified power change applies the destination and
//! publishes the classified source. Each case replays the managed recorder
//! (`ServiceControlRuntimeTests.NativePowerOnlyReleaseVectors`) through one
//! origin tick as the serving loop runs it: `process_power_source`, then the
//! lifecycle reconciliation.
//!
//! Native holds no block to release. Every step reads 07/8C fresh, and a
//! reading that conflicts with Windows power (a battery class under Windows
//! AC, or an unclassified one without Windows AC presence) refuses that step
//! with nothing submitted and nothing published. The cause stays owed, so
//! the next lifecycle epoch's classified reading applies the destination.
//! What the port adds is the source: the tick observes Windows power once
//! per epoch, before it reconciles, so the destination's publication carries
//! the new source instead of the source of the last 2 s metadata refresh.
//!
//! Deviations from managed, each pinned below:
//! - `writesBlocked` stays true: native publications never report thermal
//!   write authority open, where managed reports false after the release.
//! - The refused step publishes nothing, so the source shown during the
//!   conflict is the Windows one (3, any external power) where managed
//!   retains PluggedIn (0). Native reports only 1 for battery and 3 for any
//!   external power.
//! - Native has no transient-read label: both managed release cases replay
//!   the same native case.
//! - The block another owner holds is a store fenced by an earlier submission
//!   that may have reached the device, not an experimental write pause.
//! - `unconfirmedWrites` is not published; the unresolved submission is
//!   pinned by the fenced store and by what is never replayed.
//! - Each managed preset setter is the native pair's two 0D/02 setters.
//! - Recovery is a read-only observation and a notice at the same source,
//!   which replays nothing; the store stays fenced, where managed reopens
//!   write authority.
//! - An explicit request served in the 500 ms before the next tick still
//!   publishes the source of the last observation.
//!
//! One deviation has no native path to pin. Managed's periodic 15 s
//! observation (`ServicePeriodicObservationLoop`) is a second release path
//! through `ChangePowerSourceCore`. Native retries the owed step only at a
//! later lifecycle epoch, and the debt settles after `RETRIES` (2). If the
//! first 07/8C reading after a plug-in conflicts and no further notice
//! arrives, native keeps the previous profile until the next notice or
//! request, where managed applies the destination within about 15 s.
//!
//! Each epoch's observation is attempted once: one that yields no evidence
//! waits for the 2 s subscription refresh, and one that fails closes the feed.
use super::*;

const VECTORS: &str = include_str!("../../../../../tests/data/power-only-release-vectors.json");

/// One origin tick: the power-source pass, then the lifecycle reconciliation
/// (the serving loop also runs the keyboard step between them).
async fn origin_tick(rig: &Rig) {
    rig.source
        .lock()
        .await
        .process_power_source()
        .await
        .unwrap();
    tick(rig).await;
}
/// The subscription feed's active publication, as the console user reads it.
async fn published(rig: &Rig) -> Value {
    let publications = rig.source.lock().await.publications().unwrap();
    let active = publications.borrow().active.clone();
    serde_json::to_value(active.project_private(Some(SID))).unwrap()
}
/// The 0D/02 thermal-mode setters sent since `from`.
fn mode_setters(rig: &Rig, from: usize) -> usize {
    rig.port.state.lock().unwrap().sent[from..]
        .iter()
        .filter(|(class, command, _, _)| (*class, *command) == (13, 2))
        .count()
}
fn case<'a>(vectors: &'a Value, name: &str) -> &'a Value {
    vectors["cases"]
        .as_array()
        .unwrap()
        .iter()
        .find(|row| row["case"] == name)
        .unwrap_or_else(|| panic!("{name}"))
}
fn assert_status(status: &Value, row: &Value) {
    assert_eq!(status["writesBlocked"], true, "{status}");
    assert_eq!(
        status["activePowerSource"], row["activePowerSource"],
        "{status}"
    );
    assert_eq!(
        status["confirmedState"]["performance"], row["confirmedPerformance"],
        "{status}"
    );
    assert_eq!(
        status["desiredState"]["performance"]["value"], row["desiredPerformance"],
        "{status}"
    );
}
/// Balanced confirmed on AC, then a battery 07/8C class under Windows AC:
/// the console user's activation is refused with nothing written or
/// published, and its cause stays owed. `fence` first leaves the store fenced
/// by a setter that may have been submitted. Windows power is then derived
/// from the adapter again.
async fn blocked(fence: bool) -> Rig {
    let files = MemoryFiles::default();
    files.profiles(1);
    let rig = rig_at(files, 0);
    rig.get().await;
    assert_eq!(performance(&observe(&rig, 1).await), 1);
    if fence {
        rig.port.state.lock().unwrap().fail_write = Some(1);
        assert_eq!(rig.set(2, value(2, 0)).await["result"]["status"], 2);
        assert!(rig.storage.pending());
    }
    let before = (writes(&rig), published(&rig).await["revision"].clone());
    {
        let mut port = rig.port.state.lock().unwrap();
        port.windows_on_battery = Some(Some(false));
        port.actual = 0;
    }
    wired(&rig).await;
    assert_eq!(writes(&rig), before.0);
    assert_eq!(owed(&rig), Some(if fence { 0 } else { 1 }));
    let status = published(&rig).await;
    assert_eq!(status["revision"], before.1, "{status}");
    assert_eq!(status["writesBlocked"], true);
    assert_eq!(status["activePowerSource"], 3);
    rig.port.state.lock().unwrap().windows_on_battery = None;
    rig
}
/// The stop of a rig left with an unresolved submission, which Stop reports.
async fn abandon(rig: &Rig) {
    rig.origin.stop();
    let _ = rig.source.lock().await.drain().await;
}

#[tokio::test]
async fn a_classified_power_change_applies_the_destination_and_publishes_its_source() {
    let vectors: Value = serde_json::from_str(VECTORS).unwrap();
    for name in [
        "a classified power change applies the destination over a power-only block",
        "a classified power change applies the destination over a power-only block under a transient read label",
    ] {
        let row = case(&vectors, name);
        assert_eq!(row["blocked"]["activePowerSource"], 0, "{row}");
        let rig = blocked(false).await;
        let sent = sent(&rig);
        unplug(&rig);
        origin_tick(&rig).await;
        let after = &row["after"];
        assert_status(&published(&rig).await, after);
        assert_eq!(
            mode_setters(&rig, sent),
            2 * after["performanceSetters"].as_array().unwrap().len()
        );
        assert_eq!(owed(&rig), None);
        assert!(!rig.storage.pending());
        rig.stop().await;
    }
}

#[tokio::test]
async fn a_fenced_store_is_kept_but_still_publishes_the_classified_source() {
    let vectors: Value = serde_json::from_str(VECTORS).unwrap();
    let row = case(&vectors, "a block another owner holds is kept");
    assert_eq!(row["after"]["performanceSetters"], json!([]), "{row}");
    let rig = blocked(true).await;
    let written = writes(&rig);
    unplug(&rig);
    origin_tick(&rig).await;
    assert_eq!(writes(&rig), written);
    assert!(rig.storage.pending());
    assert_status(&published(&rig).await, &row["after"]);
    abandon(&rig).await;
}

#[tokio::test]
async fn an_unresolved_destination_submission_is_never_replayed() {
    let vectors: Value = serde_json::from_str(VECTORS).unwrap();
    let row = case(
        &vectors,
        "an unresolved destination submission is not replayed",
    );
    let rig = blocked(false).await;
    let sent = sent(&rig);
    rig.port.state.lock().unwrap().mismatch = true;
    unplug(&rig);
    origin_tick(&rig).await;
    let after = &row["after"];
    assert_eq!(
        mode_setters(&rig, sent),
        2 * after["performanceSetters"].as_array().unwrap().len()
    );
    assert!(rig.storage.pending());
    assert_status(&published(&rig).await, after);
    // A read-only observation confirms the destination; the next notice at
    // the same source replays nothing.
    rig.port.state.lock().unwrap().mismatch = false;
    let recovered = &row["recovered"];
    assert_eq!(recovered["performanceSetters"], json!([]), "{row}");
    observe(&rig, 3).await;
    let written = writes(&rig);
    power_notice(&rig);
    origin_tick(&rig).await;
    assert_eq!(writes(&rig), written);
    assert_status(&published(&rig).await, recovered);
    abandon(&rig).await;
}

/// A 07/8C read rejected (FAILED, which classifies nothing) or timed out at
/// the power change admits no step under Windows battery: no setter is sent,
/// but the change's Windows source is published. The cause stays owed, and
/// the next notice's classified reading applies the destination. The rig
/// starts unblocked so the debt has its retry left.
#[tokio::test]
async fn a_failed_power_read_at_the_change_writes_nothing_and_recovers_at_the_next_notice() {
    for timeout in [false, true] {
        let files = MemoryFiles::default();
        files.profiles(1);
        let rig = rig_at(files, 0);
        rig.get().await;
        assert_eq!(performance(&observe(&rig, 1).await), 1);
        wired(&rig).await;
        assert_eq!(owed(&rig), None);
        {
            let mut port = rig.port.state.lock().unwrap();
            if timeout {
                port.power_timeout = Some(port.power_reads + 1);
            } else {
                port.power_status = 3;
            }
        }
        let written = writes(&rig);
        unplug(&rig);
        origin_tick(&rig).await;
        assert_eq!(writes(&rig), written);
        assert!(owed(&rig).is_some());
        let status = published(&rig).await;
        assert_eq!(status["activePowerSource"], 1, "{status}");
        assert_eq!(status["confirmedState"]["performance"], 1, "{status}");
        {
            let mut port = rig.port.state.lock().unwrap();
            port.power_timeout = None;
            port.power_status = 0;
        }
        // A retry waits its pace after the refused step.
        age(&rig, pace());
        power_notice(&rig);
        origin_tick(&rig).await;
        assert!(writes(&rig) > written);
        assert_eq!(owed(&rig), None);
        assert_eq!(published(&rig).await["confirmedState"]["performance"], 3);
        rig.stop().await;
    }
}

/// An unclassified 07/8C reading admits a step only with Windows AC presence:
/// under Windows battery it writes nothing, and the source is still
/// published.
#[tokio::test]
async fn an_unclassified_reading_under_windows_battery_writes_nothing() {
    let rig = blocked(false).await;
    let written = writes(&rig);
    // 0x10 is an unmapped level: a completed reading that classifies nothing.
    rig.port.state.lock().unwrap().windows_on_battery = Some(Some(true));
    rig.port.state.lock().unwrap().actual = 0x10;
    rig.metadata.store(1, Ordering::Release);
    power_notice(&rig);
    origin_tick(&rig).await;
    assert_eq!(writes(&rig), written);
    assert!(owed(&rig).is_some());
    assert_eq!(published(&rig).await["activePowerSource"], 1);
    rig.stop().await;
}

/// Windows power is observed once per lifecycle epoch: metadata that changes
/// within an epoch waits for the next notice, and a notice that keeps the
/// metadata publishes no new revision.
#[tokio::test]
async fn windows_power_is_observed_once_per_epoch() {
    let rig = blocked(false).await;
    unplug(&rig);
    origin_tick(&rig).await;
    let released = published(&rig).await;
    assert_eq!(released["activePowerSource"], 1);
    let written = writes(&rig);
    power_notice(&rig);
    origin_tick(&rig).await;
    assert_eq!(published(&rig).await["revision"], released["revision"]);
    rig.metadata.store(0, Ordering::Release);
    origin_tick(&rig).await;
    assert_eq!(published(&rig).await, released);
    unlock(&rig);
    origin_tick(&rig).await;
    assert_eq!(published(&rig).await["activePowerSource"], 3);
    assert_eq!(writes(&rig), written);
    rig.stop().await;
}

/// One attempt settles an epoch's observation. A refresh that yields no
/// evidence is not retried at later ticks, each of which could hold the
/// source lock for the refresh budget; the 2 s subscription refresh publishes
/// the source instead.
#[tokio::test]
async fn windows_power_without_evidence_is_not_retried_within_its_epoch() {
    use crate::installed::readiness::status_source::OBSERVER_EXPIRES;
    let rig = blocked(false).await;
    let before = published(&rig).await;
    unplug(&rig);
    rig.metadata.store(1 | OBSERVER_EXPIRES, Ordering::Release);
    let started = std::time::Instant::now();
    rig.source
        .lock()
        .await
        .process_power_source()
        .await
        .unwrap();
    assert!(started.elapsed() >= std::time::Duration::from_secs(1));
    assert_eq!(published(&rig).await, before);
    // Windows power is observable again within the epoch; no tick retries it.
    rig.metadata.store(1, Ordering::Release);
    for _ in 0..2 {
        origin_tick(&rig).await;
        let status = published(&rig).await;
        assert_eq!(status["activePowerSource"], 3, "{status}");
    }
    let due = rig
        .source
        .lock()
        .await
        .refresh_publications()
        .await
        .unwrap();
    tokio::time::sleep_until(due).await;
    rig.source
        .lock()
        .await
        .refresh_publications()
        .await
        .unwrap();
    assert_eq!(published(&rig).await["activePowerSource"], 1);
    rig.stop().await;
}

/// A failed observation closes the feed, as a failed subscription refresh
/// does, so no connection can reuse the last successful publication.
#[tokio::test]
async fn a_failed_power_observation_closes_the_feed() {
    use crate::installed::readiness::status_source::OBSERVER_PANICS;
    let rig = blocked(false).await;
    let feed = rig.source.lock().await.publications().unwrap();
    unplug(&rig);
    rig.metadata.store(1 | OBSERVER_PANICS, Ordering::Release);
    assert!(
        rig.source
            .lock()
            .await
            .process_power_source()
            .await
            .is_err()
    );
    assert!(feed.has_changed().is_err());
    assert!(rig.source.lock().await.publications().is_none());
    abandon(&rig).await;
}

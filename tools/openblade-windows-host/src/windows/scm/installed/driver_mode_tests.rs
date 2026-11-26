//! The installed reader's keyboard Driver-mode status over the real ReadSource,
//! Worker, Reader and retirement, with a fake keyboard behind the Port.
use super::super::driver_mode_rig::{DRIVER, FAILED, NORMAL, Rig, status};
use super::super::*;
use super::{Cadence, INTERVAL};
use crate::discovery::performance::{DeviceModeRead, device_mode_tests::keyboard_io_worker};
use openblade_protocol::{REPORT_LENGTH, device_mode::DeviceMode};
use serde_json::Value;
use std::time::Duration;

fn vectors() -> Value {
    serde_json::from_str(include_str!(
        "../../../../tests/data/keyboard-driver-mode-vectors.json"
    ))
    .unwrap()
}

/// Each managed status row, one periodic observation per step: the verified
/// status carries the confirmed mode, and an unavailable read publishes no
/// mode. The subscription feed in this read-only composition is never
/// verified, so it never carries a mode (a known native difference).
#[tokio::test]
async fn status_publishes_the_managed_driver_mode_true_false_and_null() {
    for row in vectors()["status"].as_array().unwrap() {
        let name = row["name"].as_str().unwrap();
        let rig = Rig::new();
        let mut last = None;
        for step in row["steps"].as_array().unwrap() {
            // The next managed tick.
            rig.advance(INTERVAL);
            rig.custody.live.store(true, Ordering::Release);
            match step.as_str().unwrap() {
                "Driver" => rig.reply(DRIVER),
                "Normal" => rig.reply(NORMAL),
                "Failed" => rig.reply(FAILED),
                "Unavailable" => rig.custody.live.store(false, Ordering::Release),
                other => panic!("{name}: unknown step {other}"),
            }
            let observed = rig.observe().await;
            assert_eq!(
                observed.is_some(),
                step.as_str() != Some("Unavailable"),
                "{name}"
            );
            for published in rig.published() {
                assert_eq!(published["deviceVerified"], false, "{name}");
                assert_eq!(published["keyboardDriverModeActive"], Value::Null, "{name}");
            }
            last = Some(observed.unwrap_or_else(|| rig.published()[0].clone()));
        }
        let last = last.unwrap();
        let expect = &row["expect"];
        assert_eq!(
            last["keyboardDriverModeActive"], expect["keyboardDriverModeActive"],
            "{name}"
        );
        // Managed keeps its last verified presentation across a failed device
        // lookup (AGENTS.md architecture rule 4); this reader publishes the
        // unavailable read unverified. Both publish no mode.
        let unavailable = row["steps"].as_array().unwrap().last().unwrap() == "Unavailable";
        assert_eq!(expect["deviceVerified"], true, "{name}");
        assert_eq!(last["deviceVerified"], !unavailable, "{name}");
        rig.assert_reads_only();
    }
}

#[tokio::test]
async fn the_query_runs_once_per_interval_and_its_result_is_published_between() {
    let rig = Rig::new();
    rig.reply(DRIVER);
    let first = rig.observe().await.unwrap();
    assert_eq!(first["keyboardDriverModeActive"], true);
    assert_eq!(rig.io.queries(), 1);
    // Within the interval a firmware change is not seen, as in managed.
    rig.reply(NORMAL);
    let second = rig.observe().await.unwrap();
    assert_eq!(second["keyboardDriverModeActive"], true);
    assert_eq!(rig.io.queries(), 1);
    rig.advance(INTERVAL - Duration::from_secs(1));
    assert_eq!(
        rig.observe().await.unwrap()["keyboardDriverModeActive"],
        true
    );
    assert_eq!(rig.io.queries(), 1);
    rig.advance(Duration::from_secs(1));
    assert_eq!(
        rig.observe().await.unwrap()["keyboardDriverModeActive"],
        false
    );
    assert_eq!(rig.io.queries(), 2);
    // Each observation sent its thermal pair; only two carried the query.
    assert_eq!(rig.io.sent.lock().unwrap().len(), 4 * 2 + 2);
    rig.assert_reads_only();
}

/// A job just inside the interval publishes the cached result and the next one
/// queries again; neither publishes null without a failed read.
#[tokio::test]
async fn a_job_at_the_interval_edge_never_drops_a_confirmed_mode() {
    let rig = Rig::new();
    rig.reply(DRIVER);
    assert_eq!(
        rig.observe().await.unwrap()["keyboardDriverModeActive"],
        true
    );
    for _ in 0..5 {
        rig.advance(INTERVAL - Duration::from_millis(1));
        assert_eq!(
            rig.observe().await.unwrap()["keyboardDriverModeActive"],
            true
        );
    }
    rig.assert_reads_only();
}

/// The job's start instant, not the time it records or publishes, decides the
/// query, dates its result and selects the published value. The first job
/// starts an interval in the past, so a later `Instant::now()` would see its
/// result expired, or recorded after the next job's start.
#[tokio::test]
async fn the_job_start_dates_the_result_and_selects_the_published_mode() {
    let rig = Rig::new();
    rig.clock.set(Instant::now().checked_sub(INTERVAL).unwrap());
    rig.reply(DRIVER);
    assert_eq!(
        rig.observe().await.unwrap()["keyboardDriverModeActive"],
        true
    );
    assert_eq!(rig.io.queries(), 1);
    // Just inside the interval from that start, the cached mode is published.
    rig.advance(INTERVAL - Duration::from_millis(1));
    assert_eq!(
        rig.observe().await.unwrap()["keyboardDriverModeActive"],
        true
    );
    assert_eq!(rig.io.queries(), 1);
    // An interval after that start it is due again.
    rig.advance(Duration::from_millis(1));
    rig.reply(NORMAL);
    assert_eq!(
        rig.observe().await.unwrap()["keyboardDriverModeActive"],
        false
    );
    assert_eq!(rig.io.queries(), 2);
    rig.assert_reads_only();
}

#[tokio::test]
async fn a_failed_query_is_unknown_until_the_next_interval_and_never_retried_in_between() {
    let rig = Rig::new();
    rig.reply(DRIVER);
    assert_eq!(
        rig.observe().await.unwrap()["keyboardDriverModeActive"],
        true
    );
    rig.advance(INTERVAL);
    for (status, payload) in [FAILED, (2, [1, 0]), (2, [3, 1])] {
        rig.reply((status, payload));
        let failed = rig.observe().await.unwrap();
        // The thermal read stands; only the mode is unknown.
        assert_eq!(failed["deviceVerified"], true);
        assert_eq!(failed["keyboardDriverModeActive"], Value::Null);
        rig.reply(DRIVER);
        assert_eq!(
            rig.observe().await.unwrap()["keyboardDriverModeActive"],
            Value::Null
        );
        rig.advance(INTERVAL);
    }
    assert_eq!(rig.io.queries(), 4);
    // No reply at all is unknown too.
    *rig.io.mode.lock().unwrap() = None;
    assert_eq!(
        rig.observe().await.unwrap()["keyboardDriverModeActive"],
        Value::Null
    );
    assert_eq!(rig.io.queries(), 5);
    rig.assert_reads_only();
}

/// A query whose GET never completes ends at its own budget: the thermal read
/// is still published, through the production freshness check on the job
/// clock, and the stalled query is not sent again within the interval.
#[tokio::test]
async fn a_stalled_query_keeps_the_thermal_read_and_is_not_retried_in_between() {
    let rig = Rig::new();
    rig.reply(DRIVER);
    assert_eq!(
        rig.observe().await.unwrap()["keyboardDriverModeActive"],
        true
    );
    rig.advance(INTERVAL);
    rig.io.stall.store(true, Ordering::Release);
    for _ in 0..3 {
        let observed = rig.observe().await.unwrap();
        assert_eq!(observed["deviceVerified"], true);
        assert_eq!(observed["keyboardDriverModeActive"], Value::Null);
    }
    assert_eq!(rig.io.queries(), 2);
    assert_eq!(rig.io.sent.lock().unwrap().len(), 4 * 2 + 2);
    rig.assert_reads_only();
}

/// A job asked to query that brings back nothing is unknown until the next
/// interval and is not asked again in between.
#[tokio::test]
async fn an_asked_job_that_returns_nothing_is_not_asked_again_in_between() {
    let rig = Rig::new();
    rig.reply(DRIVER);
    // The first identity check of the thermal read fails.
    rig.io.fail_verify.store(1, Ordering::Release);
    assert!(rig.observe().await.is_none());
    rig.io.fail_verify.store(0, Ordering::Release);
    let next = rig.observe().await.unwrap();
    assert_eq!(next["deviceVerified"], true);
    assert_eq!(next["keyboardDriverModeActive"], Value::Null);
    assert_eq!(rig.io.queries(), 0);
    rig.advance(INTERVAL);
    assert_eq!(
        rig.observe().await.unwrap()["keyboardDriverModeActive"],
        true
    );
    assert_eq!(rig.io.queries(), 1);
    rig.assert_reads_only();
}

/// A valid report in a short read or under another report ID is unknown; the
/// 90-byte read without its report ID is accepted, as for the thermal getter.
#[tokio::test]
async fn a_query_reply_outside_the_hid_envelope_is_unknown() {
    let rig = Rig::new();
    rig.reply(DRIVER);
    for (envelope, expect) in [
        ((REPORT_LENGTH - 1, 0), Value::Null),
        ((REPORT_LENGTH + 1, 1), Value::Null),
        ((REPORT_LENGTH, 0), Value::Bool(true)),
    ] {
        *rig.io.envelope.lock().unwrap() = envelope;
        rig.advance(INTERVAL);
        let observed = rig.observe().await.unwrap();
        assert_eq!(observed["deviceVerified"], true, "{envelope:?}");
        assert_eq!(observed["keyboardDriverModeActive"], expect, "{envelope:?}");
    }
    assert_eq!(rig.io.queries(), 3);
    rig.assert_reads_only();
}

/// Every status carries the member, null until a verified read confirms a
/// mode, and the builder never puts a mode on an unverified status.
#[test]
fn every_status_carries_the_member_and_an_unverified_one_carries_no_mode() {
    for access in [0, 1] {
        let initial = serde_json::to_value(status(access)).unwrap();
        assert_eq!(initial.get("keyboardDriverModeActive"), Some(&Value::Null));
    }
    for active in [Some(true), Some(false), None] {
        let unverified = serde_json::to_value(status(0).with_keyboard_driver_mode(active)).unwrap();
        assert_eq!(unverified["deviceVerified"], false);
        assert_eq!(unverified["keyboardDriverModeActive"], Value::Null);
    }
}

#[tokio::test]
async fn a_new_read_admission_reads_again_and_never_inherits_the_last_mode() {
    let rig = Rig::new();
    rig.reply(DRIVER);
    assert_eq!(
        rig.observe().await.unwrap()["keyboardDriverModeActive"],
        true
    );
    rig.custody.generation.store(8, Ordering::Release);
    rig.reply(FAILED);
    let replaced = rig.observe().await.unwrap();
    assert_eq!(replaced["deviceVerified"], true);
    assert_eq!(replaced["keyboardDriverModeActive"], Value::Null);
    assert_eq!(rig.io.queries(), 2);
    // A revoked read publishes the unverified status and queries nothing.
    rig.custody.live.store(false, Ordering::Release);
    assert!(rig.observe().await.is_none());
    assert_eq!(rig.published()[0]["keyboardDriverModeActive"], Value::Null);
    assert_eq!(rig.io.queries(), 2);
    rig.assert_reads_only();
}

#[cfg(feature = "service-thermal-mutation-v1")]
#[tokio::test]
async fn a_mutation_rebase_carries_the_mode_only_with_its_fresh_verified_read() {
    let rig = Rig::new();
    rig.reply(DRIVER);
    let verified = rig.snapshot().await.unwrap();
    let latest = rig.publisher.borrow().active.clone();
    let rebased = serde_json::to_value(verified.rebase_thermal_mutation(&latest)).unwrap();
    assert_eq!(rebased["deviceVerified"], true);
    assert_eq!(rebased["keyboardDriverModeActive"], true);
    let stale = serde_json::to_value(latest.rebase_thermal_mutation(&latest)).unwrap();
    assert_eq!(stale["deviceVerified"], false);
    assert_eq!(stale["keyboardDriverModeActive"], Value::Null);
    // Once its read is no longer fresh, the same verified status carries
    // neither its verification nor its mode.
    rig.custody.live.store(false, Ordering::Release);
    let expired = serde_json::to_value(verified.rebase_thermal_mutation(&latest)).unwrap();
    assert_eq!(expired["deviceVerified"], false);
    assert_eq!(expired["keyboardDriverModeActive"], Value::Null);
}

/// A mutation publishes its own fresh thermal read through `with_mailbox_read`
/// without a query, so a mode from the status it replaces is never carried.
#[tokio::test]
async fn a_fresh_read_without_its_own_query_publishes_no_mode() {
    let rig = Rig::new();
    rig.reply(DRIVER);
    let verified = rig.snapshot().await.unwrap();
    assert_eq!(
        serde_json::to_value(&verified).unwrap()["keyboardDriverModeActive"],
        true
    );
    let deadline = Instant::now() + Duration::from_secs(2);
    let custody = rig.custody.clone();
    let reading = keyboard_io_worker(rig.io.clone(), move || custody.authorize())
        .observe(deadline, None)
        .await
        .unwrap()
        .unwrap();
    let reread = serde_json::to_value(verified.with_mailbox_read(
        reading.performance,
        crate::service_status::ReadConfirmation::new(deadline, || true),
    ))
    .unwrap();
    assert_eq!(reread["deviceVerified"], true);
    assert_eq!(reread["keyboardDriverModeActive"], Value::Null);
    assert_eq!(rig.io.queries(), 1);
    rig.assert_reads_only();
}

#[test]
fn the_cadence_binds_each_result_to_its_admission_and_interval() {
    let admission = |generation| ReadAdmission {
        lifecycle: 1,
        generation: [generation; 32],
    };
    let now = Instant::now();
    let cadence = Cadence::default();
    assert!(cadence.due(admission(1), now));
    cadence.record(admission(1), DeviceModeRead::NotRequested, now);
    assert!(cadence.due(admission(1), now));
    for (read, active) in [
        (DeviceModeRead::Confirmed(DeviceMode::Driver), Some(true)),
        (DeviceModeRead::Confirmed(DeviceMode::Normal), Some(false)),
        (DeviceModeRead::Unconfirmed, None),
    ] {
        cadence.record(admission(1), read, now);
        assert!(!cadence.due(admission(1), now));
        assert_eq!(cadence.active(admission(1), now), active);
        // Another lifecycle epoch or generation never sees it.
        assert!(cadence.due(admission(2), now));
        assert_eq!(cadence.active(admission(2), now), None);
        let other_epoch = ReadAdmission {
            lifecycle: 2,
            ..admission(1)
        };
        assert!(cadence.due(other_epoch, now));
        assert_eq!(cadence.active(other_epoch, now), None);
        // A job that did not query keeps the last result.
        cadence.record(admission(1), DeviceModeRead::NotRequested, now);
        assert_eq!(cadence.active(admission(1), now), active);
        let edge = now + INTERVAL - Duration::from_millis(1);
        assert_eq!(cadence.active(admission(1), edge), active);
        assert!(!cadence.due(admission(1), edge));
        assert_eq!(cadence.active(admission(1), now + INTERVAL), None);
        assert!(cadence.due(admission(1), now + INTERVAL));
    }
    assert_eq!(INTERVAL, Duration::from_secs(15));
}

/// A reader that predates the feature refuses this image only because its
/// compiled capabilities end with the marker and so match no admitted
/// composition. This reader refuses it by name.
#[test]
fn the_compiled_identity_ends_with_the_refused_marker() {
    let identity = std::str::from_utf8(&crate::NATIVE_ARTIFACT_IDENTITY).unwrap();
    let fields: Vec<&str> = identity.split('\n').collect();
    assert_eq!(fields[2], "service");
    // An ownership build, then a live-reading build, a diagnostic-export
    // build, an owned HyperBoost build and a display-brightness build
    // append their own markers after it, in that order.
    let marked = [
        ",service-brightness-hid-usage-v1",
        ",service-hyperboost-v1",
        ",service-thermal-hyperboost-exit-v1",
        ",service-diagnostics-export-v1",
        ",service-live-readings-v1",
    ]
    .iter()
    .fold(fields[8], |marked, later| {
        marked.strip_suffix(later).unwrap_or(marked)
    });
    let marked = marked
        .strip_suffix(",service-keyboard-driver-mode-v1")
        .unwrap_or(marked);
    assert!(
        marked.ends_with(",service-keyboard-driver-mode-read-v1"),
        "{}",
        fields[8]
    );
    assert!(
        crate::native_package::startup_policy_for_tests(&crate::NATIVE_ARTIFACT_IDENTITY).is_err()
    );
}

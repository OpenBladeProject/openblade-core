//! The installed reader's keyboard Driver-mode status over the real ReadSource,
//! Worker, Reader and retirement, with a fake keyboard behind the Port.
use super::super::*;
use super::{Cadence, INTERVAL};
use crate::discovery::performance::{
    DeviceModeRead,
    worker_tests::{KeyboardIo, keyboard_io_worker},
};
use openblade_protocol::{REPORT_LENGTH, device_mode::DeviceMode};
use serde_json::Value;
use std::{sync::atomic::AtomicBool, time::Duration};

const DRIVER: (u8, [u8; 2]) = (2, [3, 0]);
const NORMAL: (u8, [u8; 2]) = (2, [0, 0]);
// A correlated FAILED status: a terminal reply that confirms no mode.
const FAILED: (u8, [u8; 2]) = (3, [3, 0]);

struct Custody {
    live: AtomicBool,
    generation: AtomicU8,
}
impl ReadCustody for Custody {
    fn authorize(&self) -> Option<ReadAdmission> {
        self.live.load(Ordering::Acquire).then(|| ReadAdmission {
            lifecycle: 1,
            generation: [self.generation.load(Ordering::Acquire); 32],
        })
    }
    fn revoke(&self) {}
    fn retire(&self) -> io::Result<()> {
        Ok(())
    }
    fn confirm_drained(&self) -> io::Result<()> {
        Ok(())
    }
}

fn status(access: i32) -> Snapshot {
    Snapshot::observed_read_only(0, access, false, false, vec![], None, "Awaiting.".into())
}

struct Rig {
    io: Arc<KeyboardIo>,
    custody: Arc<Custody>,
    source: ReadSource,
    publisher: watch::Sender<Arc<Publication>>,
}

impl Rig {
    fn new() -> Self {
        let io = KeyboardIo::new(None);
        let custody = Arc::new(Custody {
            live: AtomicBool::new(true),
            generation: AtomicU8::new(7),
        });
        let device = io.clone();
        let source = ReadSource::compose(custody.clone(), move |lease| {
            keyboard_io_worker(device, move || lease.authorize())
        });
        let publisher = watch::channel(Arc::new(Publication {
            active: status(0),
            inactive: status(1),
        }))
        .0;
        Self {
            io,
            custody,
            source,
            publisher,
        }
    }

    fn reply(&self, (status, payload): (u8, [u8; 2])) {
        *self.io.mode.lock().unwrap() = Some((status, payload.to_vec()));
    }

    /// A fresh observation, or None when the reader admitted no read.
    async fn observe(&self) -> Option<Value> {
        let observed = self
            .source
            .observe(
                status(0),
                Instant::now() + Duration::from_secs(2),
                self.publisher.clone(),
            )
            .await
            .unwrap()?;
        Some(serde_json::to_value(observed).unwrap())
    }

    /// Both caller projections of the subscription feed.
    fn published(&self) -> [Value; 2] {
        let publication = self.publisher.borrow().clone();
        [&publication.active, &publication.inactive].map(|s| serde_json::to_value(s).unwrap())
    }

    fn assert_reads_only(&self) {
        for (class, id, payload) in self.io.sent.lock().unwrap().iter() {
            match (class, id) {
                (0x0d, 0x82) => assert_eq!(payload[0], 1),
                // The query alone; the 00/04 setters are never submitted.
                (0x00, 0x84) => assert_eq!(payload, &[0, 0]),
                _ => panic!("unadmitted command {class:02X}/{id:02X}"),
            }
        }
    }
}

fn vectors() -> Value {
    serde_json::from_str(include_str!(
        "../../../../tests/data/keyboard-driver-mode-vectors.json"
    ))
    .unwrap()
}

/// Each managed status row, one periodic observation per step: the verified
/// status carries the confirmed mode, and an unavailable read publishes no mode. The subscription feed is never verified
/// in this Service, so it never carries a mode (a known native difference).
#[tokio::test]
async fn status_publishes_the_managed_driver_mode_true_false_and_null() {
    for row in vectors()["status"].as_array().unwrap() {
        let name = row["name"].as_str().unwrap();
        let rig = Rig::new();
        let mut last = None;
        for step in row["steps"].as_array().unwrap() {
            // The next managed tick.
            rig.source.driver_mode.age_for_tests(INTERVAL);
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
async fn the_query_runs_once_per_interval_and_repeats_its_result_between() {
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
    // Real time passes during each exchange; the exact edge is unit-tested below.
    rig.source
        .driver_mode
        .age_for_tests(INTERVAL - Duration::from_secs(1));
    assert_eq!(
        rig.observe().await.unwrap()["keyboardDriverModeActive"],
        true
    );
    assert_eq!(rig.io.queries(), 1);
    rig.source.driver_mode.age_for_tests(Duration::from_secs(1));
    assert_eq!(
        rig.observe().await.unwrap()["keyboardDriverModeActive"],
        false
    );
    assert_eq!(rig.io.queries(), 2);
    // Each observation sent its thermal pair; only two carried the query.
    assert_eq!(rig.io.sent.lock().unwrap().len(), 4 * 2 + 2);
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
    rig.source.driver_mode.age_for_tests(INTERVAL);
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
        rig.source.driver_mode.age_for_tests(INTERVAL);
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
        rig.source.driver_mode.age_for_tests(INTERVAL);
        let observed = rig.observe().await.unwrap();
        assert_eq!(observed["deviceVerified"], true, "{envelope:?}");
        assert_eq!(observed["keyboardDriverModeActive"], expect, "{envelope:?}");
    }
    assert_eq!(rig.io.queries(), 3);
    rig.assert_reads_only();
}

/// The builder never puts a mode on an unverified status.
#[test]
fn an_unverified_status_carries_no_mode() {
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
async fn a_mutation_rebase_carries_the_mode_only_with_its_verified_read() {
    let rig = Rig::new();
    rig.reply(DRIVER);
    let verified = rig
        .source
        .observe(
            status(0),
            Instant::now() + Duration::from_secs(2),
            rig.publisher.clone(),
        )
        .await
        .unwrap()
        .unwrap();
    let latest = rig.publisher.borrow().active.clone();
    let rebased = serde_json::to_value(verified.rebase_thermal_mutation(&latest)).unwrap();
    assert_eq!(rebased["deviceVerified"], true);
    assert_eq!(rebased["keyboardDriverModeActive"], true);
    let stale = serde_json::to_value(latest.rebase_thermal_mutation(&latest)).unwrap();
    assert_eq!(stale["deviceVerified"], false);
    assert_eq!(stale["keyboardDriverModeActive"], Value::Null);
}

/// A mutation publishes its own fresh thermal read through `with_mailbox_read`
/// without a query, so a mode from the status it replaces is never carried.
#[tokio::test]
async fn a_fresh_read_without_its_own_query_publishes_no_mode() {
    let rig = Rig::new();
    rig.reply(DRIVER);
    let verified = rig
        .source
        .observe(
            status(0),
            Instant::now() + Duration::from_secs(2),
            rig.publisher.clone(),
        )
        .await
        .unwrap()
        .unwrap();
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
    assert!(!cadence.due(None, now));
    assert!(cadence.due(Some(admission(1)), now));
    cadence.record(admission(1), DeviceModeRead::NotRequested, now);
    assert!(cadence.due(Some(admission(1)), now));
    for (read, active) in [
        (DeviceModeRead::Confirmed(DeviceMode::Driver), Some(true)),
        (DeviceModeRead::Confirmed(DeviceMode::Normal), Some(false)),
        (DeviceModeRead::Unconfirmed, None),
    ] {
        cadence.record(admission(1), read, now);
        assert!(!cadence.due(Some(admission(1)), now));
        assert_eq!(cadence.active(admission(1), now), active);
        // Another lifecycle epoch or generation never sees it.
        assert!(cadence.due(Some(admission(2)), now));
        assert_eq!(cadence.active(admission(2), now), None);
        let other_epoch = ReadAdmission {
            lifecycle: 2,
            ..admission(1)
        };
        assert!(cadence.due(Some(other_epoch), now));
        assert_eq!(cadence.active(other_epoch, now), None);
        // A job that did not query keeps the last result.
        cadence.record(admission(1), DeviceModeRead::NotRequested, now);
        assert_eq!(cadence.active(admission(1), now), active);
        let edge = now + INTERVAL - Duration::from_millis(1);
        assert_eq!(cadence.active(admission(1), edge), active);
        assert!(!cadence.due(Some(admission(1)), edge));
        assert_eq!(cadence.active(admission(1), now + INTERVAL), None);
        assert!(cadence.due(Some(admission(1)), now + INTERVAL));
    }
    assert_eq!(INTERVAL, Duration::from_secs(15));
}

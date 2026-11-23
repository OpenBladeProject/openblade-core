//! The installed reader's live readings and battery health over the real
//! ReadSource, Worker, Reader and retirement, with a fake fan controller
//! behind the Port and a fake Windows battery behind `Capture`.
use super::super::driver_mode_rig::{Custody, status};
use super::super::*;
use super::{Capture, Host, map_battery_health, map_battery_power_flow};
use crate::discovery::performance::{
    FanRpmRead,
    fan_rpm_tests::{FanIo, fan_io_worker},
};
use serde_json::{Value, json};
use std::{
    cell::{Cell, RefCell},
    sync::atomic::AtomicBool,
    time::Duration,
};

const ALL: u8 = 15;
const HYPER_BOOST: u8 = 7;

fn vectors() -> Value {
    serde_json::from_str(include_str!(
        "../../../../tests/data/live-reading-vectors.json"
    ))
    .unwrap()
}

/// A Windows battery: `state` is the SystemBatteryState answer and
/// `capacities` each capture's answer (None fails the capture).
#[derive(Default)]
struct Battery {
    state: Cell<Option<(bool, u32)>>,
    capacities: RefCell<Option<Vec<(u32, u32)>>>,
    state_reads: Cell<usize>,
    capacity_reads: Cell<usize>,
}
impl Capture for Battery {
    fn battery_state(&self) -> Option<(bool, u32)> {
        self.state_reads.set(self.state_reads.get() + 1);
        self.state.get()
    }
    fn battery_capacities(&self) -> io::Result<Vec<(u32, u32)>> {
        self.capacity_reads.set(self.capacity_reads.get() + 1);
        self.capacities
            .borrow()
            .clone()
            .ok_or_else(|| io::ErrorKind::NotFound.into())
    }
}

struct Rig {
    io: Arc<FanIo>,
    custody: Arc<Custody>,
    battery: Rc<Battery>,
    source: ReadSource,
    publisher: watch::Sender<Arc<Publication>>,
}

impl Rig {
    fn new() -> Self {
        Self::with(FanIo::new([0x20, 0x1e]), Battery::default())
    }

    fn with(io: Arc<FanIo>, battery: Battery) -> Self {
        let custody = Arc::new(Custody {
            live: AtomicBool::new(true),
            generation: AtomicU8::new(7),
        });
        let battery = Rc::new(battery);
        let device = io.clone();
        let source = ReadSource::compose(custody.clone(), move |lease| {
            fan_io_worker(device, move || lease.authorize())
        })
        .with_live_capture_for_tests(battery.clone());
        let publisher = watch::channel(Arc::new(Publication {
            active: status(0),
            inactive: status(1),
        }))
        .0;
        Self {
            io,
            custody,
            battery,
            source,
            publisher,
        }
    }

    /// The managed ObserveState request for `mask`.
    fn request(mask: u8) -> crate::service_status::StatusRequest {
        serde_json::from_value(json!({
            "operation": "observeState",
            "version": 85,
            "requestId": "11111111-2222-4333-8444-555555555555",
            "includeLiveReadings": mask != 0,
            "requestedLiveReadings": mask,
            "effectiveLiveReadings": mask,
        }))
        .unwrap()
    }

    /// One fresh observation, created within the ObserveState scope for
    /// `mask` as readiness creates it.
    async fn snapshot(&self, mask: u8) -> Option<Snapshot> {
        let deadline = Instant::now() + Duration::from_secs(2);
        let publisher = self.publisher.clone();
        let request = Self::request(mask);
        let observation =
            request.scope_live_readings(|| self.source.observe(status(0), deadline, publisher));
        observation.await.unwrap()
    }

    async fn observe(&self, mask: u8) -> Option<Value> {
        Some(serde_json::to_value(self.snapshot(mask).await?).unwrap())
    }

    fn published(&self) -> [Value; 2] {
        let publication = self.publisher.borrow().clone();
        [&publication.active, &publication.inactive].map(|s| serde_json::to_value(s).unwrap())
    }

    fn fan_queries(&self) -> usize {
        self.io.queries().len()
    }

    /// A fresh metadata publication, as the metadata observer sends one.
    fn metadata(&self, on_battery: bool) {
        let status = |access| {
            Snapshot::observed_read_only(
                0,
                access,
                on_battery,
                false,
                vec![],
                None,
                "Awaiting.".into(),
            )
        };
        self.source
            .decorate(
                &self.publisher,
                Some(Arc::new(Publication {
                    active: status(0),
                    inactive: status(1),
                })),
            )
            .unwrap();
    }
}

fn readings(value: &Value) -> [&Value; 5] {
    let live = &value["liveReadings"];
    [
        &live["batteryPowerFlowMilliwatts"],
        &live["fan1Rpm"],
        &live["fan2Rpm"],
        &live["cpuTemperatureCelsius"],
        &live["gpuTemperatureCelsius"],
    ]
}

#[test]
fn battery_mappings_match_the_managed_vectors() {
    let vectors = vectors();
    for row in vectors["batteryHealth"].as_array().unwrap() {
        let capacities = row["capacities"]
            .as_array()
            .unwrap()
            .iter()
            .map(|pair| {
                (
                    pair[0].as_u64().unwrap() as u32,
                    pair[1].as_u64().unwrap() as u32,
                )
            })
            .collect::<Vec<_>>();
        let mapped = map_battery_health(&capacities).map_or(Value::Null, |(design, full)| {
            json!({
                "designCapacityMilliwattHours": design,
                "fullChargeCapacityMilliwattHours": full,
            })
        });
        assert_eq!(mapped, row["expect"], "{}", row["name"]);
    }
    for row in vectors["batteryPowerFlow"].as_array().unwrap() {
        let mapped = map_battery_power_flow(
            row["present"].as_bool().unwrap(),
            row["rate"].as_u64().unwrap() as u32,
        );
        assert_eq!(
            mapped.map_or(Value::Null, Value::from),
            row["expect"],
            "{}",
            row["name"]
        );
    }
}

/// A confirmed HyperBoost read carries the managed observation record, as do
/// its publications, and an expired read none. The managed codec otherwise
/// refuses a confirmed active HyperBoost. Any other read carries no record.
#[tokio::test]
async fn a_confirmed_hyper_boost_read_carries_the_managed_observation_record() {
    let rig = Rig::new();
    let observed = rig.observe(0).await.unwrap();
    assert_eq!(observed["confirmedState"]["hyperBoostActive"], false);
    assert_eq!(observed["capabilities"], Value::Null);
    rig.io.thermal.store(HYPER_BOOST, Ordering::Release);
    let observed = rig.observe(0).await.unwrap();
    let expected = &vectors()["capabilities"][0]["capabilities"];
    assert_eq!(observed["confirmedState"]["hyperBoostActive"], true);
    assert_eq!(&observed["capabilities"], expected);
    for published in rig.published() {
        assert_eq!(published["confirmedState"]["hyperBoostActive"], true);
        assert_eq!(&published["capabilities"], expected);
    }
    rig.source.expire_for_tests();
    rig.metadata(false);
    for published in rig.published() {
        assert_eq!(published["confirmedState"], Value::Null);
        assert_eq!(published["capabilities"], Value::Null);
    }
}

/// Only the sensors an ObserveState asks for are read: fan speeds by the
/// 0D/88 pair after the thermal read, battery power from Windows, and no
/// temperature. A request for none publishes no live readings.
#[tokio::test]
async fn each_observation_reads_only_the_requested_sensors() {
    for (mask, battery, fans, queries, state_reads) in [
        (0, Value::Null, [Value::Null, Value::Null], 0, 0),
        (1, json!(-25000), [Value::Null, Value::Null], 0, 1),
        (2, Value::Null, [json!(3200), json!(3000)], 2, 0),
        (4, Value::Null, [Value::Null, Value::Null], 0, 0),
        (8, Value::Null, [Value::Null, Value::Null], 0, 0),
        (ALL, json!(-25000), [json!(3200), json!(3000)], 2, 1),
    ] {
        let rig = Rig::new();
        rig.battery.state.set(Some((true, (-25000i32) as u32)));
        let observed = rig.observe(mask).await.unwrap();
        assert_eq!(observed["deviceVerified"], true);
        if mask == 0 {
            assert_eq!(observed["liveReadings"], Value::Null);
        } else {
            let [power, fan1, fan2, cpu, gpu] = readings(&observed);
            assert_eq!(power, &battery, "{mask}");
            assert_eq!([fan1, fan2], [&fans[0], &fans[1]], "{mask}");
            assert_eq!([cpu, gpu], [&Value::Null; 2], "{mask}");
        }
        assert_eq!(rig.fan_queries(), queries, "{mask}");
        assert_eq!(rig.battery.state_reads.get(), state_reads, "{mask}");
        for (class, id, payload) in rig.io.sent.lock().unwrap().iter() {
            match (class, id) {
                (0x0d, 0x82) => assert_eq!(payload[0], 1),
                (0x0d, 0x88) => assert!(matches!(payload.as_slice(), [1, 1 | 2, 0])),
                // The Driver-mode query alone, when that read is built too.
                (0x00, 0x84) if cfg!(feature = "service-keyboard-driver-mode-read-v1") => {
                    assert_eq!(payload, &[0, 0])
                }
                _ => panic!("unadmitted command {class:02X}/{id:02X}"),
            }
        }
        // Live readings belong to the ObserveState reply alone.
        for published in rig.published() {
            assert_eq!(published["liveReadings"], Value::Null, "{mask}");
        }
    }
}

/// Another read, such as a trigger's, asks for no live reading.
#[tokio::test]
async fn a_read_outside_an_observe_state_sends_no_fan_query() {
    let rig = Rig::new();
    let deadline = Instant::now() + Duration::from_secs(2);
    let observed = rig
        .source
        .observe(status(0), deadline, rig.publisher.clone())
        .await
        .unwrap()
        .unwrap();
    let observed = serde_json::to_value(observed).unwrap();
    assert_eq!(observed["liveReadings"], Value::Null);
    assert_eq!(rig.fan_queries(), 0);
    assert_eq!(rig.battery.state_reads.get(), 0);
}

/// A failed Windows battery read is null, as managed publishes it.
#[tokio::test]
async fn an_unavailable_battery_power_read_is_null() {
    let rig = Rig::new();
    let observed = rig.observe(1).await.unwrap();
    assert_eq!(readings(&observed)[0], &Value::Null);
    assert_eq!(rig.battery.state_reads.get(), 1);
}

/// A transient fan failure keeps the last confirmed pair for two jobs, the
/// third publishes null, and the next confirmed pair recovers. The thermal
/// read is fresh throughout, and no reading is invented.
#[tokio::test]
async fn transient_fan_failures_retain_the_last_pair_for_two_jobs_then_recover() {
    let rig = Rig::new();
    let fans = |value: &Value| [readings(value)[1].clone(), readings(value)[2].clone()];
    let first = rig.observe(2).await.unwrap();
    assert_eq!(fans(&first), [json!(3200), json!(3000)]);
    // The second selector echoes the first: the pair confirms neither.
    rig.io.replies.lock().unwrap()[1] = Some((2, vec![1, 1, 0x1e]));
    for expect in [json!(3200), json!(3200), Value::Null, Value::Null] {
        let observed = rig.observe(2).await.unwrap();
        assert_eq!(observed["confirmedState"]["performance"], 1);
        assert_eq!(fans(&observed)[0], expect);
    }
    *rig.io.replies.lock().unwrap() = [None, Some((2, vec![1, 2, 0x10]))];
    let recovered = rig.observe(2).await.unwrap();
    assert_eq!(fans(&recovered), [json!(3200), json!(1600)]);
}

/// A speed the managed codec would refuse is not confirmed; it counts as a
/// failed read.
#[tokio::test]
async fn a_speed_above_the_managed_bound_is_not_published() {
    let rig = Rig::with(FanIo::new([200, 201]), Battery::default());
    let observed = rig.observe(2).await.unwrap();
    assert_eq!(readings(&observed)[1], &Value::Null);
    assert_eq!(readings(&observed)[2], &Value::Null);
    let rig = Rig::with(FanIo::new([200, 200]), Battery::default());
    let observed = rig.observe(2).await.unwrap();
    assert_eq!(readings(&observed)[1], &json!(20000));
}

/// A retained pair belongs to the admission it was read under.
#[tokio::test]
async fn a_new_read_admission_drops_the_retained_pair() {
    let rig = Rig::new();
    rig.observe(2).await.unwrap();
    rig.io.replies.lock().unwrap()[0] = Some((3, vec![1, 1, 0x20]));
    rig.custody.generation.store(8, Ordering::Release);
    let observed = rig.observe(2).await.unwrap();
    assert_eq!(readings(&observed)[1], &Value::Null);
    // Back under the first admission, nothing is retained either.
    rig.custody.generation.store(7, Ordering::Release);
    let observed = rig.observe(2).await.unwrap();
    assert_eq!(readings(&observed)[1], &Value::Null);
}

/// Retention ends at the thermal retention window and after two misses,
/// and a job that brought back nothing counts as a miss.
#[test]
fn fan_retention_is_bounded_by_misses_age_and_admission() {
    let admission = ReadAdmission {
        lifecycle: 1,
        generation: [7; 32],
    };
    let other = ReadAdmission {
        generation: [8; 32],
        ..admission
    };
    let now = Instant::now();
    let host = Host::new(Rc::new(Battery::default()));
    let unconfirmed = FanRpmRead::Unconfirmed;
    assert_eq!(host.fans(admission, unconfirmed, now), None);
    let read = FanRpmRead::Confirmed([3200, 3000]);
    assert_eq!(host.fans(admission, read, now), Some([3200, 3000]));
    let late = now + RETENTION_WINDOW - Duration::from_millis(1);
    assert_eq!(host.fans(admission, unconfirmed, late), Some([3200, 3000]));
    assert_eq!(
        host.fans(admission, unconfirmed, now + RETENTION_WINDOW),
        None
    );
    assert_eq!(host.fans(admission, read, now), Some([3200, 3000]));
    assert_eq!(host.missed(None, now), None);
    assert_eq!(
        host.fans(admission, FanRpmRead::NotRequested, now),
        Some([3200, 3000])
    );
    assert_eq!(host.fans(admission, unconfirmed, now), None);
    assert_eq!(host.fans(admission, read, now), Some([3200, 3000]));
    assert_eq!(host.fans(other, unconfirmed, now), None);
    assert_eq!(host.fans(admission, unconfirmed, now), None);
}

fn health(value: &Value) -> Value {
    value["batteryHealth"].clone()
}

const HEALTH: (u32, u32) = (90_000, 85_000);
fn health_json() -> Value {
    json!({"designCapacityMilliwattHours": 90000, "fullChargeCapacityMilliwattHours": 85000})
}

/// The first status captures battery health; once captured it is published
/// in both projections and every reply and never captured again.
#[tokio::test]
async fn battery_health_is_captured_once_at_the_first_status() {
    let battery = Battery::default();
    *battery.capacities.borrow_mut() = Some(vec![HEALTH]);
    let rig = Rig::with(FanIo::new([0x20, 0x1e]), battery);
    rig.metadata(false);
    assert_eq!(rig.battery.capacity_reads.get(), 1);
    for published in rig.published() {
        assert_eq!(health(&published), health_json());
    }
    *rig.battery.capacities.borrow_mut() = None;
    let observed = rig.observe(0).await.unwrap();
    assert_eq!(health(&observed), health_json());
    rig.metadata(true);
    rig.observe(ALL).await.unwrap();
    assert_eq!(rig.battery.capacity_reads.get(), 1);
    for published in rig.published() {
        assert_eq!(health(&published), health_json());
    }
}

/// An unavailable first capture is retried once after the first verified
/// observation and then only on a power-source change, as managed retries it.
#[tokio::test]
async fn unavailable_battery_health_is_retried_after_verification_and_on_power_changes() {
    let rig = Rig::new();
    rig.metadata(false);
    assert_eq!(rig.battery.capacity_reads.get(), 1);
    for published in rig.published() {
        assert_eq!(health(&published), Value::Null);
    }
    rig.observe(0).await.unwrap();
    rig.observe(0).await.unwrap();
    assert_eq!(rig.battery.capacity_reads.get(), 2);
    rig.metadata(false);
    assert_eq!(rig.battery.capacity_reads.get(), 2);
    *rig.battery.capacities.borrow_mut() = Some(vec![(0, 0)]);
    rig.metadata(true);
    assert_eq!(rig.battery.capacity_reads.get(), 3);
    for published in rig.published() {
        assert_eq!(health(&published), Value::Null);
    }
    *rig.battery.capacities.borrow_mut() = Some(vec![HEALTH]);
    rig.metadata(false);
    assert_eq!(rig.battery.capacity_reads.get(), 4);
    for published in rig.published() {
        assert_eq!(health(&published), health_json());
    }
    rig.metadata(true);
    assert_eq!(rig.battery.capacity_reads.get(), 4);
}

/// An observation that precedes any status captures health once for both
/// managed captures.
#[tokio::test]
async fn an_observation_before_any_status_captures_health_once() {
    let battery = Battery::default();
    *battery.capacities.borrow_mut() = Some(vec![HEALTH]);
    let rig = Rig::with(FanIo::new([0x20, 0x1e]), battery);
    let observed = rig.observe(0).await.unwrap();
    assert_eq!(health(&observed), health_json());
    assert_eq!(rig.battery.capacity_reads.get(), 1);
}

const FRAMES: &str = "live-reading-native-frames.json";
const REGENERATE: &str = "OPENBLADE_REGENERATE_NATIVE_LIVE_READING_FRAMES";

/// Records production snapshots as v85 status events for the managed codec
/// test (`NativeLiveReadingFrames_DecodeThroughTheManagedCodec`): an
/// ObserveState reply with every reading, a HyperBoost reply, the
/// subscription feed after it, and an unverified status. A build with the
/// Driver-mode read adds its own member, so only this composition records.
#[cfg(not(feature = "service-keyboard-driver-mode-read-v1"))]
#[tokio::test]
async fn native_frames_match_the_recorded_managed_codec_input() {
    let mut frames = Vec::new();
    let mut record = |name: &str, snapshot: &Snapshot, expect: Value| {
        let frame = crate::service_status::encode_status_event(snapshot).unwrap();
        frames.push(json!({
            "name": name,
            "frame": String::from_utf8(frame).unwrap(),
            "expect": expect,
        }));
    };

    let battery = Battery::default();
    battery.state.set(Some((true, 25_000)));
    *battery.capacities.borrow_mut() = Some(vec![HEALTH]);
    let rig = Rig::with(FanIo::new([0x20, 0x1e]), battery);
    rig.metadata(false);
    let observed = rig.snapshot(ALL).await.unwrap();
    record(
        "balanced reply with every reading",
        &observed,
        json!({
            "hyperBoostActive": false, "hyperBoostObservedOnly": false,
            "fan1Rpm": 3200, "fan2Rpm": 3000, "batteryPowerFlowMilliwatts": 25000,
            "designCapacityMilliwattHours": 90000, "fullChargeCapacityMilliwattHours": 85000,
        }),
    );

    let rig = Rig::new();
    rig.io.thermal.store(HYPER_BOOST, Ordering::Release);
    let observed = rig.snapshot(2).await.unwrap();
    record(
        "hyperboost reply with fan speeds",
        &observed,
        json!({
            "hyperBoostActive": true, "hyperBoostObservedOnly": true,
            "fan1Rpm": 3200, "fan2Rpm": 3000, "batteryPowerFlowMilliwatts": null,
            "designCapacityMilliwattHours": null, "fullChargeCapacityMilliwattHours": null,
        }),
    );
    let published = rig.publisher.borrow().clone();
    record(
        "hyperboost subscription",
        &published.active,
        json!({
            "hyperBoostActive": true, "hyperBoostObservedOnly": true,
            "fan1Rpm": null, "fan2Rpm": null, "batteryPowerFlowMilliwatts": null,
            "designCapacityMilliwattHours": null, "fullChargeCapacityMilliwattHours": null,
        }),
    );
    rig.source.expire_for_tests();
    rig.metadata(false);
    let published = rig.publisher.borrow().clone();
    record(
        "expired subscription",
        &published.active,
        json!({
            "hyperBoostActive": null, "hyperBoostObservedOnly": false,
            "fan1Rpm": null, "fan2Rpm": null, "batteryPowerFlowMilliwatts": null,
            "designCapacityMilliwattHours": null, "fullChargeCapacityMilliwattHours": null,
        }),
    );

    let mut text = String::from("{\"frames\":[\n");
    text.push_str(
        &frames
            .iter()
            .map(|frame| serde_json::to_string(frame).unwrap())
            .collect::<Vec<_>>()
            .join(",\n"),
    );
    text.push_str("\n]}\n");
    let path = std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("tests/data")
        .join(FRAMES);
    if std::env::var_os(REGENERATE).is_some() {
        std::fs::write(&path, &text).unwrap();
    }
    assert_eq!(std::fs::read_to_string(&path).unwrap(), text);
}

/// The compiled identity carries the refused marker, so no package reader
/// takes this image for the composition it extends.
#[test]
fn the_compiled_identity_ends_with_the_refused_marker() {
    let identity = std::str::from_utf8(&crate::NATIVE_ARTIFACT_IDENTITY).unwrap();
    let fields: Vec<&str> = identity.split('\n').collect();
    assert_eq!(fields[2], "service");
    assert!(
        fields[8].ends_with(",service-live-readings-v1"),
        "{}",
        fields[8]
    );
    assert!(
        crate::native_package::startup_policy_for_tests(&crate::NATIVE_ARTIFACT_IDENTITY).is_err()
    );
}

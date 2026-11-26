//! The installed reader's live readings and battery health over the real
//! ReadSource, Worker, Reader and retirement, with a fake fan controller
//! behind the Port and a fake Windows battery behind `Capture`.
use super::super::driver_mode_rig::{Custody, status};
use super::super::*;
use super::{Capture, Host, map_battery_health, map_battery_power_flow};
#[cfg(feature = "service-thermal-mutation-v1")]
use crate::discovery::performance::mutation::Permit;
use crate::discovery::performance::{
    FanRpmRead,
    fan_rpm_tests::{FanIo, fan_io_worker},
};
use serde_json::{Value, json};
use std::{
    cell::{Cell, RefCell},
    sync::atomic::AtomicUsize,
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
        Self::composed(io, battery, |custody, device| {
            ReadSource::compose(custody, move |lease| {
                fan_io_worker(device, move || lease.authorize())
            })
        })
    }

    /// A rig whose source also has the production mutation entries. Its
    /// write factory is never reached: a refused permit registers no worker.
    #[cfg(feature = "service-thermal-mutation-v1")]
    fn with_mutation() -> Self {
        Self::composed(
            FanIo::new([0x20, 0x1e]),
            Battery::default(),
            |custody, device| {
                ReadSource::injected_mutation(
                    custody,
                    MutationFactories {
                        read: Box::new(move |lease| {
                            fan_io_worker(device.clone(), move || lease.authorize())
                        }),
                        write: Box::new(|_| unreachable!("no mutation worker is registered")),
                        // The pool seed has its own tests; this rig ignores it.
                        quarantine: Box::new(|_| {}),
                    },
                )
            },
        )
    }

    fn composed(
        io: Arc<FanIo>,
        battery: Battery,
        compose: impl FnOnce(Arc<dyn ReadCustody>, Arc<FanIo>) -> ReadSource,
    ) -> Self {
        let custody = Arc::new(Custody::new());
        let battery = Rc::new(battery);
        let source =
            compose(custody.clone(), io.clone()).with_live_capture_for_tests(battery.clone());
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
            "version": 91,
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

    /// Ends the current fan back-off, as its interval passing would.
    fn end_backoff(&self) {
        self.source.live.end_backoff_for_tests();
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

/// A GPU for the rig's worker: active and in D0, its NVML reading `first`
/// plus the number of earlier reads. While `hold` is set, each workload
/// sample waits for one message or its sender's drop. `exited` reports
/// when the worker drops its session.
#[derive(Clone, Default)]
struct Gpu {
    workloads: Arc<AtomicUsize>,
    reads: Arc<AtomicUsize>,
    exited: Option<std::sync::mpsc::Sender<()>>,
}

struct GpuProbes(Arc<AtomicUsize>, Option<std::sync::mpsc::Receiver<()>>);

impl gpu_temperature::Probes for GpuProbes {
    fn workload(&mut self) -> Option<bool> {
        self.0.fetch_add(1, Ordering::SeqCst);
        if let Some(hold) = &self.1 {
            let _ = hold.recv();
        }
        Some(true)
    }

    fn power(&mut self) -> gpu_temperature::Power {
        gpu_temperature::Power::On
    }
}

struct GpuSession(i32, Arc<AtomicUsize>, Option<std::sync::mpsc::Sender<()>>);

impl Drop for GpuSession {
    fn drop(&mut self) {
        if let Some(exited) = &self.2 {
            let _ = exited.send(());
        }
    }
}

impl gpu_temperature::TemperatureSession for GpuSession {
    fn try_read(&mut self) -> Option<i32> {
        Some(self.0 + self.1.fetch_add(1, Ordering::SeqCst) as i32)
    }

    fn release(&mut self) {}
}

impl Gpu {
    fn factory(
        &self,
        first: i32,
        hold: Option<std::sync::mpsc::Receiver<()>>,
    ) -> gpu_temperature::Factory {
        let gpu = self.clone();
        Box::new(move || {
            gpu_temperature::Sampler::new(
                Box::new(GpuProbes(gpu.workloads, hold)),
                Box::new(GpuSession(first, gpu.reads, gpu.exited)),
                Box::new(std::time::Instant::now),
            )
        })
    }

    fn workloads(&self) -> usize {
        self.workloads.load(Ordering::SeqCst)
    }

    fn watch_exit(&mut self) -> std::sync::mpsc::Receiver<()> {
        let (exited, exits) = std::sync::mpsc::channel();
        self.exited = Some(exited);
        exits
    }
}

impl Rig {
    fn with_gpu(mut self, factory: gpu_temperature::Factory) -> Self {
        self.source = self.source.with_gpu_for_tests(factory);
        self
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
    let mut expected = vectors()["capabilities"][0]["capabilities"].clone();
    // A Fan Stop build publishes its source and trial evidence.
    let fan_stop = cfg!(feature = "service-fan-stop-v1");
    expected["hasFanStopSourceEvidence"] = json!(fan_stop);
    expected["hasFanStopTrialEvidence"] = json!(fan_stop);
    let expected = &expected;
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
/// third publishes null, and no reading is invented. The failed pair backs
/// off: no job sends it again until the interval passes, after which the
/// next confirmed pair recovers. The thermal read is fresh throughout.
#[tokio::test]
async fn transient_fan_failures_retain_the_last_pair_for_two_jobs_then_recover() {
    let rig = Rig::new();
    let fans = |value: &Value| [readings(value)[1].clone(), readings(value)[2].clone()];
    let first = rig.observe(2).await.unwrap();
    assert_eq!(fans(&first), [json!(3200), json!(3000)]);
    // The second selector echoes the first: the pair confirms neither.
    rig.io.replies.lock().unwrap()[1] = Some((2, vec![1, 1, 0x1e]));
    for (expect, queries) in [
        (json!(3200), 4),
        (json!(3200), 4),
        (Value::Null, 4),
        (Value::Null, 4),
    ] {
        let observed = rig.observe(2).await.unwrap();
        assert_eq!(observed["confirmedState"]["performance"], 1);
        assert_eq!(fans(&observed)[0], expect);
        assert_eq!(rig.fan_queries(), queries);
    }
    *rig.io.replies.lock().unwrap() = [None, Some((2, vec![1, 2, 0x10]))];
    let backing_off = rig.observe(2).await.unwrap();
    assert_eq!(fans(&backing_off), [Value::Null, Value::Null]);
    assert_eq!(rig.fan_queries(), 4);
    rig.end_backoff();
    let recovered = rig.observe(2).await.unwrap();
    assert_eq!(fans(&recovered), [json!(3200), json!(1600)]);
    assert_eq!(rig.fan_queries(), 6);
}

/// A job that asked for fan speeds and brought back no reading without
/// sending the pair, here because its thermal read never confirmed, leaves
/// the pair due: the next job sends it.
#[tokio::test]
async fn an_asked_job_that_sent_no_pair_leaves_it_due() {
    let rig = Rig::new();
    // No admitted thermal mode decodes, so the job confirms nothing.
    rig.io.thermal.store(0xff, Ordering::Release);
    for _ in 0..3 {
        assert!(rig.snapshot(2).await.is_none());
    }
    assert_eq!(rig.fan_queries(), 0);
    rig.io.thermal.store(0, Ordering::Release);
    let observed = rig.observe(2).await.unwrap();
    assert_eq!(observed["confirmedState"]["performance"], 1);
    assert_eq!(readings(&observed)[1], &json!(3200));
    assert_eq!(rig.fan_queries(), 2);
}

/// An observation dropped after the pair's first SET, as a disconnecting
/// client drops it, never brings its result back. The worker counted the
/// pair as it sent it, so the next job backs the pair off.
#[tokio::test]
async fn a_pair_sent_by_a_dropped_observation_backs_off() {
    let rig = Rig::new();
    let (release, hold) = std::sync::mpsc::channel::<()>();
    *rig.io.hold_query.lock().unwrap() = Some(hold);
    {
        let observation = rig.snapshot(2);
        tokio::pin!(observation);
        while rig.fan_queries() == 0 {
            tokio::select! {
                _ = &mut observation => panic!("the held pair completed"),
                () = tokio::time::sleep(Duration::from_millis(1)) => {}
            }
        }
    }
    drop(release);
    // Until the abandoned worker retires, a job starts no read at all.
    let observed = loop {
        if let Some(observed) = rig.observe(2).await {
            break observed;
        }
        tokio::time::sleep(Duration::from_millis(1)).await;
    };
    assert_eq!(observed["confirmedState"]["performance"], 1);
    assert_eq!(readings(&observed)[1], &Value::Null);
    assert_eq!(rig.fan_queries(), 1);
    rig.end_backoff();
    let recovered = rig.observe(2).await.unwrap();
    assert_eq!(readings(&recovered)[1], &json!(3200));
    assert_eq!(rig.fan_queries(), 3);
}

/// The back-off starts at the Driver-mode interval, doubles per consecutive
/// sent and unconfirmed pair up to five minutes, ignores a job that sent no
/// SET whatever it brought back, and ends at the next confirmed pair.
#[test]
fn the_fan_backoff_doubles_to_its_bound_and_a_confirmed_pair_ends_it() {
    let host = Host::new(Rc::new(Battery::default()));
    let start = Instant::now();
    let sends = host.fan_query(start).unwrap();
    let mut now = start;
    for seconds in [15, 30, 60, 120, 240, 300, 300] {
        sends.sent_for_tests();
        host.settle(FanRpmRead::Unconfirmed, now);
        let pause = Duration::from_secs(seconds);
        let paused = now + pause - Duration::from_millis(1);
        assert!(host.fan_query(paused).is_none(), "{seconds}");
        for unsent in [FanRpmRead::Unconfirmed, FanRpmRead::NotRequested] {
            host.settle(unsent, paused);
        }
        assert!(host.fan_query(now + pause).is_some(), "{seconds}");
        now += pause;
    }
    sends.sent_for_tests();
    host.settle(FanRpmRead::Confirmed([3200, 3000]), now);
    assert!(host.fan_query(now).is_some());
    // The confirmation accounted for every pair sent before it.
    host.settle(FanRpmRead::Unconfirmed, now);
    assert!(host.fan_query(now).is_some());
    sends.sent_for_tests();
    host.settle(FanRpmRead::Unconfirmed, now);
    let first = Duration::from_secs(15);
    assert!(
        host.fan_query(now + first - Duration::from_millis(1))
            .is_none()
    );
    assert!(host.fan_query(now + first).is_some());
}

/// A pair whose job never settled, because its observation was dropped, is
/// accounted for at the next query: a sent pair pauses the next one, and a
/// job that sent nothing does not.
#[test]
fn a_pair_with_no_settled_outcome_is_accounted_for_at_the_next_query() {
    let host = Host::new(Rc::new(Battery::default()));
    let now = Instant::now();
    let sends = host.fan_query(now).unwrap();
    assert!(host.fan_query(now).is_some());
    sends.sent_for_tests();
    assert!(host.fan_query(now).is_none());
    assert!(host.fan_query(now + Duration::from_secs(15)).is_some());
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

/// The GPU is sampled only for an ObserveState that asks for its
/// temperature, and the sample is published in that reply alone.
#[tokio::test]
async fn gpu_temperature_is_sampled_and_published_only_for_an_observe_state_that_asks() {
    let gpu = Gpu::default();
    let rig = Rig::new().with_gpu(gpu.factory(52, None));
    for mask in [0, 1, 2, 4, 7] {
        let observed = rig.observe(mask).await.unwrap();
        assert_eq!(readings(&observed)[4], &Value::Null, "{mask}");
    }
    let deadline = Instant::now() + Duration::from_secs(2);
    let outside = rig
        .source
        .observe(status(0), deadline, rig.publisher.clone())
        .await
        .unwrap()
        .unwrap();
    assert_eq!(
        serde_json::to_value(outside).unwrap()["liveReadings"],
        Value::Null
    );
    assert_eq!(gpu.workloads(), 0);

    let observed = rig.observe(8).await.unwrap();
    assert_eq!(readings(&observed)[4], &json!(52));
    let observed = rig.observe(ALL).await.unwrap();
    assert_eq!(readings(&observed)[4], &json!(53));
    assert_eq!(gpu.workloads(), 2);
    for published in rig.published() {
        assert_eq!(published["liveReadings"], Value::Null);
    }
}

/// A temperature the managed codec refuses is null, and the rest of the
/// reply is published.
#[tokio::test]
async fn a_gpu_temperature_above_the_codec_bound_is_null() {
    let rig = Rig::new().with_gpu(Gpu::default().factory(116, None));
    rig.battery.state.set(Some((true, (-25000i32) as u32)));
    let observed = rig.observe(ALL).await.unwrap();
    let [power, fan1, _, _, gpu] = readings(&observed);
    assert_eq!(
        [power, fan1, gpu],
        [&json!(-25000), &json!(3200), &Value::Null]
    );
}

/// A sample is published only by the observation that asked for it: one
/// whose observation was dropped or not accepted is never published, and
/// the next observation publishes the latest read.
#[tokio::test]
async fn gpu_temperature_of_a_dropped_or_unaccepted_observation_is_not_published() {
    for dropped in [true, false] {
        let gpu = Gpu::default();
        let (release, hold) = std::sync::mpsc::channel();
        let rig = Rig::new().with_gpu(gpu.factory(50, Some(hold)));
        {
            let observation = rig.snapshot(8);
            tokio::pin!(observation);
            while gpu.workloads() == 0 {
                tokio::select! {
                    _ = &mut observation => panic!("the held sample completed"),
                    () = tokio::time::sleep(Duration::from_millis(1)) => {}
                }
            }
            if !dropped {
                // The custody ends while the sample runs.
                rig.custody.live.store(false, Ordering::Release);
                release.send(()).unwrap();
                assert!(observation.await.is_none());
            }
            // Otherwise a disconnecting client drops the observation here.
        }
        rig.custody.live.store(true, Ordering::Release);
        drop(release);
        let observed = loop {
            if let Some(observed) = rig.observe(8).await {
                break observed;
            }
            tokio::time::sleep(Duration::from_millis(1)).await;
        };
        // The dropped observation's sample ends before its power read.
        let reads = gpu.reads.load(Ordering::SeqCst);
        assert!(reads >= 1, "{dropped}");
        let latest = json!(50 + reads as i32 - 1);
        assert_eq!(readings(&observed)[4], &latest, "{dropped}");
    }
}

/// An observation that waits for its GPU sample publishes nothing once a
/// later job has published: its reading is the older one. With thermal
/// mutation the later observation is refused instead while it waits.
#[tokio::test]
async fn an_observation_waiting_for_its_gpu_sample_never_overwrites_a_later_reading() {
    let gpu = Gpu::default();
    let (release, hold) = std::sync::mpsc::channel();
    let rig = Rig::new().with_gpu(gpu.factory(50, Some(hold)));
    let earlier = rig.snapshot(8);
    tokio::pin!(earlier);
    // Until the earlier job has sent its reads, then a little longer for
    // its reading to arrive: the held sample keeps it waiting after that.
    let mut polls = 0;
    while gpu.workloads() == 0 || rig.io.sent.lock().unwrap().is_empty() || polls < 5 {
        polls += usize::from(gpu.workloads() > 0);
        tokio::select! {
            _ = &mut earlier => panic!("the held observation completed"),
            () = tokio::time::sleep(Duration::from_millis(1)) => {}
        }
    }
    rig.io.thermal.store(HYPER_BOOST, Ordering::Release);
    if cfg!(feature = "service-thermal-mutation-v1") {
        assert!(rig.observe(0).await.is_none());
        drop(release);
        assert!(earlier.await.is_some());
        return;
    }
    let until = Instant::now() + Duration::from_secs(5);
    let later = loop {
        if let Some(later) = rig.observe(0).await {
            break later;
        }
        assert!(Instant::now() < until, "no later observation was accepted");
        tokio::time::sleep(Duration::from_millis(1)).await;
    };
    assert_eq!(later["confirmedState"]["hyperBoostActive"], true);
    let published = rig.published();
    drop(release);
    assert!(earlier.await.is_none());
    assert_eq!(rig.published(), published);
    for published in published {
        assert_eq!(published["confirmedState"]["hyperBoostActive"], true);
    }
}

/// Stop ends the GPU worker and its held session without waiting for the
/// lease.
#[tokio::test]
async fn stop_ends_the_gpu_worker() {
    let mut gpu = Gpu::default();
    let exits = gpu.watch_exit();
    let rig = Rig::new().with_gpu(gpu.factory(52, None));
    let observed = rig.observe(8).await.unwrap();
    assert_eq!(readings(&observed)[4], &json!(52));
    assert!(exits.try_recv().is_err());
    rig.source.stop().await.unwrap();
    exits
        .recv_timeout(gpu_temperature::LEASE / 2)
        .expect("the worker outlived Stop");
}

/// Each LiveReadings member is published only when its bit was asked for,
/// and an observation that asked for none publishes no LiveReadings.
#[test]
fn live_readings_carry_only_the_asked_sensors() {
    let published = |mask| {
        serde_json::to_value(status(0).with_live_readings(
            mask,
            Some(-25000),
            Some([3200, 3000]),
            Some(52),
        ))
        .unwrap()
    };
    assert_eq!(published(0)["liveReadings"], Value::Null);
    let null = Value::Null;
    for (mask, expected) in [
        (
            1,
            [
                json!(-25000),
                null.clone(),
                null.clone(),
                null.clone(),
                null.clone(),
            ],
        ),
        (
            2,
            [
                null.clone(),
                json!(3200),
                json!(3000),
                null.clone(),
                null.clone(),
            ],
        ),
        (
            4,
            [
                null.clone(),
                null.clone(),
                null.clone(),
                null.clone(),
                null.clone(),
            ],
        ),
        (
            8,
            [
                null.clone(),
                null.clone(),
                null.clone(),
                null.clone(),
                json!(52),
            ],
        ),
        (
            15,
            [
                json!(-25000),
                json!(3200),
                json!(3000),
                null.clone(),
                json!(52),
            ],
        ),
    ] {
        let value = published(mask);
        assert_eq!(readings(&value), expected.each_ref(), "{mask}");
    }
}

const FRAMES: &str = "live-reading-native-frames.json";
const REGENERATE: &str = "OPENBLADE_REGENERATE_NATIVE_LIVE_READING_FRAMES";

/// Records production snapshots as v91 status events for the managed codec
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
    let rig =
        Rig::with(FanIo::new([0x20, 0x1e]), battery).with_gpu(Gpu::default().factory(52, None));
    rig.metadata(false);
    let observed = rig.snapshot(ALL).await.unwrap();
    record(
        "balanced reply with every reading",
        &observed,
        json!({
            "hyperBoostActive": false, "hyperBoostObservedOnly": false,
            "fan1Rpm": 3200, "fan2Rpm": 3000, "batteryPowerFlowMilliwatts": 25000, "gpuTemperatureCelsius": 52,
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
            "fan1Rpm": 3200, "fan2Rpm": 3000, "batteryPowerFlowMilliwatts": null, "gpuTemperatureCelsius": null,
            "designCapacityMilliwattHours": null, "fullChargeCapacityMilliwattHours": null,
        }),
    );
    let published = rig.publisher.borrow().clone();
    record(
        "hyperboost subscription",
        &published.active,
        json!({
            "hyperBoostActive": true, "hyperBoostObservedOnly": true,
            "fan1Rpm": null, "fan2Rpm": null, "batteryPowerFlowMilliwatts": null, "gpuTemperatureCelsius": null,
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
            "fan1Rpm": null, "fan2Rpm": null, "batteryPowerFlowMilliwatts": null, "gpuTemperatureCelsius": null,
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
    // Only the markers build.rs appends after it may follow it.
    assert!(
        crate::native_package::ends_with_marker_for_tests(fields[8], "service-live-readings-v1"),
        "{}",
        fields[8]
    );
    assert!(
        crate::native_package::startup_policy_for_tests(&crate::NATIVE_ARTIFACT_IDENTITY).is_err()
    );
}

/// The marker order is build.rs's: a later marker may follow, an earlier or
/// unknown one may not.
#[test]
fn the_refused_marker_order_is_read_from_build_rs() {
    use crate::native_package::ends_with_marker_for_tests as ends;
    const LIVE: &str = "service-live-readings-v1";
    assert!(ends("a,service-live-readings-v1", LIVE));
    assert!(ends(
        "a,service-live-readings-v1,service-fan-stop-v1,service-lighting-v1",
        LIVE
    ));
    assert!(!ends(
        "a,service-live-readings-v1,service-keyboard-driver-mode-v1",
        LIVE
    ));
    assert!(!ends("a,service-live-readings-v1,service-unknown-v1", LIVE));
    assert!(!ends("a,service-live-readings-v1", "service-unknown-v1"));
}

/// A mutation drops the retained pair, so a speed read before a write is
/// never published after it; the next confirmed pair is published again.
#[cfg(feature = "service-thermal-mutation-v1")]
#[test]
fn a_mutation_drops_the_retained_fan_pair() {
    let admission = ReadAdmission {
        lifecycle: 1,
        generation: [7; 32],
    };
    let now = Instant::now();
    let host = Host::new(Rc::new(Battery::default()));
    let read = FanRpmRead::Confirmed([3200, 3000]);
    assert_eq!(host.fans(admission, read, now), Some([3200, 3000]));
    host.mutating();
    assert_eq!(host.fans(admission, FanRpmRead::NotRequested, now), None);
    assert_eq!(host.fans(admission, read, now), Some([3200, 3000]));
}

/// Each production mutation entry drops the retained pair as it takes the
/// serial lock, so a speed read before a write is never published after it,
/// even when the mutation is then refused. Without a mutation the failed
/// pair below would publish the retained 3200 RPM.
#[cfg(feature = "service-thermal-mutation-v1")]
#[tokio::test]
async fn each_mutation_entry_drops_the_retained_fan_pair() {
    for cleanup in [false, true] {
        let rig = Rig::with_mutation();
        let first = rig.observe(2).await.unwrap();
        assert_eq!(readings(&first)[1], &json!(3200), "{cleanup}");
        rig.io.replies.lock().unwrap()[1] = Some((2, vec![1, 1, 0x1e]));
        let permit: Arc<dyn Permit> = Arc::new(RefusedFanPermit);
        let deadline = Instant::now() + Duration::from_secs(2);
        let result = if cleanup {
            rig.source.mutate_cleanup(permit, deadline).await
        } else {
            rig.source.mutate(permit, deadline).await
        };
        assert!(result.is_err(), "{cleanup}");
        let observed = rig.observe(2).await.unwrap();
        assert_eq!(observed["confirmedState"]["performance"], 1, "{cleanup}");
        assert_eq!(readings(&observed)[1], &Value::Null, "{cleanup}");
        assert_eq!(rig.fan_queries(), 4, "{cleanup}");
    }
}

/// A fan permit refused at admission: it authorizes nothing.
#[cfg(feature = "service-thermal-mutation-v1")]
struct RefusedFanPermit;

#[cfg(feature = "service-thermal-mutation-v1")]
impl Permit for RefusedFanPermit {
    fn authorize(&self) -> Option<ReadAdmission> {
        None
    }
    fn is_fan(&self) -> bool {
        true
    }
    fn cleanup(&self) -> bool {
        true
    }
    fn resolve(
        &self,
        _: i32,
        _: crate::discovery::performance::ObservedPerformance,
        _: &std::sync::atomic::AtomicBool,
    ) -> io::Result<Option<openblade_domain::thermal_mutation::Destination>> {
        Err(io::ErrorKind::PermissionDenied.into())
    }
    fn record(&self, _: openblade_domain::thermal_mutation::Progress) -> io::Result<()> {
        Err(io::ErrorKind::PermissionDenied.into())
    }
}

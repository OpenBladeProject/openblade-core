//! GPU temperature: the gate, the session and its lease against the managed
//! vectors, the NVML session's lifetime over a fake NVML, and the worker,
//! its requests and Stop.
use super::*;
use serde_json::{Map, Value, json};
use std::{
    cell::Cell,
    collections::VecDeque,
    rc::Rc,
    sync::{
        Arc, Mutex,
        atomic::{AtomicUsize, Ordering},
        mpsc,
    },
};

fn vectors() -> Value {
    serde_json::from_str(include_str!(
        "../../../../tests/data/gpu-temperature-vectors.json"
    ))
    .unwrap()
}

fn rows<'a>(vectors: &'a Value, section: &str) -> &'a [Value] {
    let rows = vectors[section].as_array().unwrap();
    assert!(!rows.is_empty(), "{section}");
    rows
}

fn processes(snapshot: &Value) -> Option<BTreeMap<i32, f64>> {
    snapshot.as_array().map(|processes| {
        processes
            .iter()
            .map(|process| {
                (
                    process[0].as_i64().unwrap() as i32,
                    process[1].as_f64().unwrap(),
                )
            })
            .collect()
    })
}

fn hex(text: &str) -> Vec<u8> {
    (0..text.len())
        .step_by(2)
        .map(|at| u8::from_str_radix(&text[at..at + 2], 16).unwrap())
        .collect()
}

fn power_name(power: Power) -> &'static str {
    match power {
        Power::On => "PoweredOn",
        Power::Down => "PoweredDown",
        Power::Unknown => "Unknown",
    }
}

/// The workload is managed's: a fresh query primed once, the window, and
/// the second read decides.
#[test]
fn the_workload_matches_the_managed_activity_vectors() {
    let vectors = vectors();
    for row in rows(&vectors, "activity") {
        let snapshots = [&row["first"], &row["second"]];
        let reads = Cell::new(0);
        let paused = Cell::new(None);
        let active = workload(
            || row["refresh"].as_bool().unwrap().then_some(()),
            |_| {
                let snapshot = snapshots[reads.get()];
                reads.set(reads.get() + 1);
                processes(snapshot)
            },
            |window| paused.set(Some((window, reads.get()))),
        );
        let name = &row["name"];
        assert_eq!(
            json!({"active": active, "reads": reads.get()}),
            row["expect"],
            "{name}"
        );
        let expected_pause = (reads.get() > 0).then_some((SAMPLE_WINDOW, 1));
        assert_eq!(paused.get(), expected_pause, "{name}");
    }
}

#[test]
fn the_power_data_matches_the_managed_classification() {
    let vectors = vectors();
    for row in rows(&vectors, "power") {
        let data = hex(row["data"].as_str().unwrap());
        let kind = row["type"].as_u64().unwrap() as u32;
        assert_eq!(
            power_name(classify_power_data(&data, kind)),
            row["expect"],
            "{}",
            row["name"]
        );
    }
}

fn d_state(state: u32) -> Vec<u8> {
    let mut data = vec![0; POWER_DATA];
    data[..4].copy_from_slice(&(POWER_DATA as u32).to_le_bytes());
    data[4..8].copy_from_slice(&state.to_le_bytes());
    data
}

type PowerCase = (
    &'static str,
    Vec<Option<&'static str>>,
    Option<(u32, Vec<u8>)>,
    Power,
    usize,
);

/// The D-state of the one present NVIDIA display device, read from that
/// device; none, two, a failed step or no property is Unknown.
#[test]
fn the_power_state_is_the_one_nvidia_display_device() {
    const NVIDIA: &str = r"PCI\VEN_10DE&DEV_2C58&SUBSYS_00000000&REV_A1\4&1";
    const LOWER: &str = r"pci\ven_10de&dev_2c58\4&1";
    const INTEL: &str = r"PCI\VEN_8086&DEV_7D67\3&1";
    let d0 = Some((BINARY, d_state(1)));
    let cases: [PowerCase; 9] = [
        (
            "one NVIDIA device",
            vec![Some(INTEL), Some(NVIDIA)],
            d0.clone(),
            Power::On,
            1,
        ),
        (
            "D3",
            vec![Some(NVIDIA)],
            Some((BINARY, d_state(4))),
            Power::Down,
            1,
        ),
        ("no display device", vec![], d0.clone(), Power::Unknown, 0),
        (
            "no NVIDIA device",
            vec![Some(INTEL)],
            d0.clone(),
            Power::Unknown,
            0,
        ),
        (
            "two NVIDIA devices",
            vec![Some(NVIDIA), Some(NVIDIA)],
            d0.clone(),
            Power::Unknown,
            1,
        ),
        (
            "a failed step after it",
            vec![Some(NVIDIA), None],
            d0.clone(),
            Power::Unknown,
            1,
        ),
        (
            "a failed step before it",
            vec![None, Some(NVIDIA)],
            d0.clone(),
            Power::Unknown,
            0,
        ),
        ("no property", vec![Some(NVIDIA)], None, Power::Unknown, 1),
        (
            "a lower-case instance ID",
            vec![Some(LOWER)],
            d0,
            Power::On,
            1,
        ),
    ];
    for (name, devices, property, expected, property_reads) in cases {
        let current = Cell::new(None);
        let reads = Cell::new(0);
        let power = nvidia_power(
            |index| match devices.get(index as usize) {
                None => Enumerated::End,
                Some(None) => Enumerated::Failed,
                Some(Some(instance)) => {
                    current.set(Some(*instance));
                    Enumerated::Device((*instance).into())
                }
            },
            || {
                reads.set(reads.get() + 1);
                let instance = current.get().unwrap().to_ascii_uppercase();
                assert!(instance.starts_with(NVIDIA_INSTANCE), "{name}");
                property.clone()
            },
        );
        assert_eq!(power, expected, "{name}");
        assert_eq!(reads.get(), property_reads, "{name}");
    }
    // A prefix of the vendor alone is not an NVIDIA instance.
    let power = nvidia_power(
        |index| match index {
            0 => Enumerated::Device(r"PCI\VEN_10D".into()),
            _ => Enumerated::End,
        },
        || panic!("no NVIDIA device"),
    );
    assert_eq!(power, Power::Unknown);
}

/// The sampler's fakes share one script, as managed's replay does.
#[derive(Default)]
struct Script {
    active: Option<bool>,
    power: VecDeque<Power>,
    last_power: Option<Power>,
    power_reads: usize,
    workloads: usize,
    temperature: Option<i32>,
    cancel: Option<String>,
    cancelled: bool,
    reads: usize,
    releases: usize,
    /// A step that advances the test clock, and by how much.
    stall: Option<(&'static str, Duration, Rc<Cell<Duration>>)>,
}

impl Script {
    /// Runs `step`: cancels or stalls there when the script says so.
    fn reached(&mut self, step: &str) {
        if self.cancel.as_deref() == Some(step) {
            self.cancelled = true;
        }
        if let Some((_, by, elapsed)) = self.stall.as_ref().filter(|(at, ..)| *at == step) {
            elapsed.set(elapsed.get() + *by);
        }
    }
}

/// A closure is managed's token alone: every sample is wanted.
impl<F: Fn() -> bool> Cancellation for F {
    fn cancelled(&self) -> bool {
        self()
    }

    fn unwanted(&self) -> bool {
        false
    }
}

/// The script's cancellation as an observation that stopped waiting, never
/// managed's token.
struct Unwanted<'a>(&'a RefCell<Script>);

impl Cancellation for Unwanted<'_> {
    fn cancelled(&self) -> bool {
        false
    }

    fn unwanted(&self) -> bool {
        self.0.borrow().cancelled
    }
}

struct ScriptedProbes(Rc<RefCell<Script>>);

impl Probes for ScriptedProbes {
    fn workload(&mut self) -> Option<bool> {
        let mut script = self.0.borrow_mut();
        script.workloads += 1;
        script.reached("activity");
        script.active
    }

    fn power(&mut self) -> Power {
        let mut script = self.0.borrow_mut();
        script.power_reads += 1;
        match script.power_reads {
            1 => script.reached("power"),
            2 => script.reached("recheck"),
            _ => {}
        }
        if let Some(power) = script.power.pop_front() {
            script.last_power = Some(power);
        }
        script.last_power.unwrap_or(Power::Unknown)
    }
}

struct ScriptedSession(Rc<RefCell<Script>>);

impl TemperatureSession for ScriptedSession {
    fn try_read(&mut self) -> Option<i32> {
        let mut script = self.0.borrow_mut();
        script.reads += 1;
        script.reached("read");
        script.temperature
    }

    fn release(&mut self) {
        self.0.borrow_mut().releases += 1;
    }
}

type Clock = Box<dyn Fn() -> Instant>;

/// A test clock: the base instant plus what the test advanced.
fn clock() -> (Instant, Rc<Cell<Duration>>, Clock) {
    let base = Instant::now();
    let elapsed = Rc::new(Cell::new(Duration::ZERO));
    let reader = elapsed.clone();
    (base, elapsed, Box::new(move || base + reader.get()))
}

fn power_states(states: &Value) -> VecDeque<Power> {
    states
        .as_array()
        .unwrap()
        .iter()
        .map(|state| match state.as_str().unwrap() {
            "on" => Power::On,
            "down" => Power::Down,
            _ => Power::Unknown,
        })
        .collect()
}

/// Managed's sampler replay over the native sampler: a read is `read` with
/// its reader (no `reader` for ReadAsync, `live` for
/// ReadForLiveReadingsAsync, the Service's `sample`) that advances the clock
/// by `readMs` during the session's read, the session timer is `expire` and
/// disposal is `release`. Any other reader is a recorder this replay does
/// not know. A step's `cancel` is managed's token, which a closure stands
/// for.
fn replay(steps: &[Value]) -> Vec<Value> {
    let script = Rc::new(RefCell::new(Script::default()));
    let (base, elapsed, clock) = clock();
    let mut sampler = Sampler::new(
        Box::new(ScriptedProbes(script.clone())),
        Box::new(ScriptedSession(script.clone())),
        clock,
    );
    steps
        .iter()
        .map(|step| {
            let advance = Duration::from_millis(step["advanceMs"].as_u64().unwrap_or(0));
            elapsed.set(elapsed.get() + advance);
            let mut expect = Map::new();
            match step["action"].as_str().unwrap_or("read") {
                "read" => {
                    let reader = match &step["reader"] {
                        Value::Null => Reader::Ordinary,
                        Value::String(reader) if reader == "live" => Reader::LiveReadings,
                        reader => panic!("unknown sampler reader {reader}"),
                    };
                    {
                        let mut script = script.borrow_mut();
                        script.active = step["active"].as_bool();
                        script.power = power_states(&step["power"]);
                        script.power_reads = 0;
                        script.temperature = step["session"].as_i64().map(|c| c as i32);
                        script.cancel = step["cancel"].as_str().map(str::to_owned);
                        script.cancelled = false;
                        script.stall = step["readMs"]
                            .as_u64()
                            .map(|ms| ("read", Duration::from_millis(ms), elapsed.clone()));
                    }
                    let celsius = sampler.read(reader, &|| script.borrow().cancelled);
                    let script = script.borrow();
                    expect.insert("celsius".into(), json!(celsius));
                    expect.insert("cancelled".into(), json!(script.cancelled));
                    expect.insert("powerReads".into(), json!(script.power_reads));
                }
                "fire" => sampler.expire(),
                "dispose" => sampler.release(),
                action => panic!("unknown sampler action {action}"),
            }
            let script = script.borrow();
            expect.insert("reads".into(), json!(script.reads));
            expect.insert("releases".into(), json!(script.releases));
            let lease = sampler
                .renewed
                .map(|renewed| (renewed + LEASE - base).as_millis() as u64);
            expect.insert("leaseAtMs".into(), json!(lease));
            Value::Object(expect)
        })
        .collect()
}

/// The gate, the reading, the session's use and its lease match managed's
/// sampler step by step, on both managed readers.
#[test]
fn the_sampler_matches_the_managed_sampler_vectors() {
    let vectors = vectors();
    for row in rows(&vectors, "sampler") {
        let observed = replay(row["steps"].as_array().unwrap());
        let expected = row["expect"].as_array().unwrap();
        assert_eq!(observed.len(), expected.len(), "{}", row["name"]);
        for (index, (observed, expected)) in observed.iter().zip(expected).enumerate() {
            assert_eq!(observed, expected, "{} step {index}", row["name"]);
        }
    }
}

/// Only what the managed v91 codec writes is published.
#[tokio::test]
async fn publication_matches_the_managed_codec() {
    let vectors = vectors();
    for row in rows(&vectors, "publication") {
        let celsius = row["celsius"].as_i64().unwrap() as i32;
        let (reply, receiver) = oneshot::channel();
        reply.send(Some(celsius)).unwrap();
        let pending = Pending {
            reply: receiver,
            until: tokio::time::Instant::now() + Duration::from_secs(1),
        };
        let published = pending.temperature().await;
        assert_eq!(
            published.is_some(),
            row["accepted"].as_bool().unwrap(),
            "{}",
            row["name"]
        );
        assert!(published.is_none_or(|published| published == celsius));
    }
}

/// What a fake NVML returns, and the calls it received.
#[derive(Default)]
struct NvmlScript {
    calls: Vec<String>,
    init: i32,
    device: Option<i32>,
    temperature: Option<i32>,
    celsius: u32,
}

/// A fake NVML. `hold`, when set, blocks `init`; `dropped` reports when
/// this copy is dropped.
#[derive(Clone)]
struct FakeNvml {
    script: Arc<Mutex<NvmlScript>>,
    hold: Option<Hold>,
    dropped: Option<mpsc::Sender<()>>,
}

impl FakeNvml {
    fn new(celsius: u32) -> Self {
        Self {
            script: Arc::new(Mutex::new(NvmlScript {
                celsius,
                ..NvmlScript::default()
            })),
            hold: None,
            dropped: None,
        }
    }

    fn calls(&self) -> Vec<String> {
        self.script.lock().unwrap().calls.clone()
    }

    fn script(&self, change: impl FnOnce(&mut NvmlScript)) {
        change(&mut self.script.lock().unwrap());
    }

    fn call(&self, name: String) {
        self.script.lock().unwrap().calls.push(name);
    }
}

impl Drop for FakeNvml {
    fn drop(&mut self) {
        if let Some(dropped) = &self.dropped {
            let _ = dropped.send(());
        }
    }
}

impl Nvml for FakeNvml {
    fn init(&mut self) -> i32 {
        self.call("init".into());
        if let Some(hold) = &self.hold {
            hold.wait();
        }
        self.script.lock().unwrap().init
    }

    fn device(&mut self, index: u32) -> Result<usize, i32> {
        self.call(format!("device {index}"));
        match self.script.lock().unwrap().device {
            Some(status) => Err(status),
            None => Ok(0x7e57),
        }
    }

    fn temperature(&mut self, device: usize, sensor: u32) -> Result<u32, i32> {
        self.call(format!("temperature {device:x} {sensor}"));
        let script = self.script.lock().unwrap();
        match script.temperature {
            Some(status) => Err(status),
            None => Ok(script.celsius),
        }
    }

    fn shutdown(&mut self) -> i32 {
        self.call("shutdown".into());
        0
    }
}

/// The handle and temperature calls of one read.
const READ: [&str; 2] = ["device 0", "temperature 7e57 0"];

fn calls(groups: &[&[&str]]) -> Vec<String> {
    groups.concat().into_iter().map(str::to_owned).collect()
}

/// Managed `NvidiaNvmlTemperatureSession`: one init per session, a failed
/// init retried by the next read with nothing to shut down, a failed handle
/// or temperature call shut down, and a sensor above 125 C refused alone.
#[test]
fn the_nvml_session_matches_managed() {
    let nvml = FakeNvml::new(52);
    let mut session = NvmlSession::new(nvml.clone());
    nvml.script(|script| script.init = 9);
    assert_eq!(session.try_read(), None);
    session.release();
    assert_eq!(nvml.calls(), calls(&[&["init"]]));

    nvml.script(|script| script.init = 0);
    assert_eq!(session.try_read(), Some(52));
    assert_eq!(session.try_read(), Some(52));
    assert_eq!(nvml.calls(), calls(&[&["init", "init"], &READ, &READ]));

    nvml.script(|script| script.celsius = 126);
    assert_eq!(session.try_read(), None);
    nvml.script(|script| script.celsius = 125);
    assert_eq!(session.try_read(), Some(125));
    session.release();
    session.release();
    assert_eq!(
        nvml.calls(),
        calls(&[&["init", "init"], &READ, &READ, &READ, &READ, &["shutdown"]])
    );

    let failures: [fn(&mut NvmlScript); 2] = [
        |script| script.device = Some(999),
        |script| script.temperature = Some(999),
    ];
    for failure in failures {
        let nvml = FakeNvml::new(52);
        let mut session = NvmlSession::new(nvml.clone());
        nvml.script(failure);
        assert_eq!(session.try_read(), None);
        // The failed read shut the session down itself.
        let after_failure = nvml.calls();
        assert_eq!(after_failure.last().map(String::as_str), Some("shutdown"));
        assert_eq!(after_failure.iter().filter(|c| *c == "shutdown").count(), 1);
        nvml.script(|script| {
            script.device = None;
            script.temperature = None;
        });
        assert_eq!(session.try_read(), Some(52));
        assert_eq!(nvml.calls()[after_failure.len()], "init");
    }
}

fn nvml_sampler(nvml: &FakeNvml, script: &Rc<RefCell<Script>>) -> (Sampler, Rc<Cell<Duration>>) {
    let (_, elapsed, clock) = clock();
    let sampler = Sampler::new(
        Box::new(ScriptedProbes(script.clone())),
        Box::new(NvmlSession::new(nvml.clone())),
        clock,
    );
    (sampler, elapsed)
}

fn set(script: &Rc<RefCell<Script>>, active: Option<bool>, power: &[Power]) {
    let mut script = script.borrow_mut();
    script.active = active;
    script.power = power.iter().copied().collect();
    script.power_reads = 0;
}

/// No sample loads, starts or reads NVML for a GPU outside D0, whatever the
/// counters report and however long the samples go on.
#[test]
fn no_nvml_call_while_the_gpu_is_powered_down_or_unknown() {
    for (active, power) in [
        (Some(true), Power::Down),
        (Some(true), Power::Unknown),
        (Some(false), Power::Down),
        (Some(false), Power::Unknown),
        (None, Power::Down),
        (None, Power::Unknown),
    ] {
        let nvml = FakeNvml::new(52);
        let script = Rc::new(RefCell::new(Script::default()));
        let (mut sampler, elapsed) = nvml_sampler(&nvml, &script);
        for _ in 0..3 {
            set(&script, active, &[power]);
            assert_eq!(sampler.sample(&|| false), None);
            sampler.expire();
            elapsed.set(elapsed.get() + IDLE_DWELL);
        }
        sampler.release();
        assert_eq!(nvml.calls(), Vec::<String>::new(), "{active:?} {power:?}");
    }
}

const MILLISECOND: Duration = Duration::from_millis(1);

/// An idle sample, with idle or unavailable counters, of a GPU that has
/// reported D0 for `IDLE_DWELL` reads NVML in a session that ends before the
/// sample returns. Until that reading is `IDLE_INTERVAL` old the samples
/// reuse it without an NVML call; the first at that age reads again.
#[test]
fn an_idle_d0_gpu_is_read_after_the_dwell_at_most_once_per_interval() {
    for active in [Some(false), None] {
        let nvml = FakeNvml::new(52);
        let script = Rc::new(RefCell::new(Script::default()));
        let (mut sampler, elapsed) = nvml_sampler(&nvml, &script);
        let advance = |by| elapsed.set(elapsed.get() + by);
        set(&script, active, &[Power::On]);
        assert_eq!(sampler.sample(&|| false), None, "{active:?}");
        advance(IDLE_DWELL - MILLISECOND);
        set(&script, active, &[Power::On]);
        assert_eq!(sampler.sample(&|| false), None, "{active:?}");
        assert_eq!(nvml.calls(), Vec::<String>::new(), "{active:?}");

        advance(MILLISECOND);
        set(&script, active, &[Power::On, Power::On]);
        assert_eq!(sampler.sample(&|| false), Some(52), "{active:?}");
        let mut expected = calls(&[&["init"], &READ, &["shutdown"]]);
        assert_eq!(nvml.calls(), expected, "{active:?}");
        assert_eq!(sampler.remaining_lease(), None, "{active:?}");

        nvml.script(|script| script.celsius = 53);
        advance(IDLE_INTERVAL - MILLISECOND);
        set(&script, active, &[Power::On]);
        assert_eq!(sampler.sample(&|| false), Some(52), "{active:?}");
        assert_eq!(nvml.calls(), expected, "{active:?}");

        advance(MILLISECOND);
        set(&script, active, &[Power::On, Power::On]);
        assert_eq!(sampler.sample(&|| false), Some(53), "{active:?}");
        expected.extend(calls(&[&["init"], &READ, &["shutdown"]]));
        assert_eq!(nvml.calls(), expected, "{active:?}");
    }
}

/// A sample outside D0 drops the last reading, active or idle, and restarts
/// the dwell: the D0 samples after it reuse nothing and make no NVML call
/// until the GPU has again reported D0 for `IDLE_DWELL`.
#[test]
fn a_sample_outside_d0_drops_the_reading_and_restarts_the_dwell() {
    for (first, power) in [
        (Some(true), Power::Down),
        (Some(true), Power::Unknown),
        (Some(false), Power::Down),
        (Some(false), Power::Unknown),
    ] {
        let nvml = FakeNvml::new(52);
        let script = Rc::new(RefCell::new(Script::default()));
        let (mut sampler, elapsed) = nvml_sampler(&nvml, &script);
        let advance = |by| elapsed.set(elapsed.get() + by);
        let name = format!("{first:?} {power:?}");
        set(&script, Some(false), &[Power::On]);
        assert_eq!(sampler.sample(&|| false), None, "{name}");
        advance(IDLE_DWELL);
        set(&script, first, &[Power::On, Power::On]);
        assert_eq!(sampler.sample(&|| false), Some(52), "{name}");
        let read = nvml.calls();

        advance(Duration::from_secs(1));
        set(&script, Some(false), &[power]);
        assert_eq!(sampler.sample(&|| false), None, "{name}");
        for wait in [MILLISECOND, IDLE_DWELL - MILLISECOND] {
            advance(wait);
            set(&script, Some(false), &[Power::On]);
            assert_eq!(sampler.sample(&|| false), None, "{name}");
        }
        // Only the active reading's session was still open.
        let ended = &nvml.calls()[read.len()..];
        assert!(ended.iter().all(|call| call == "shutdown"), "{name}");

        advance(MILLISECOND);
        set(&script, Some(false), &[Power::On, Power::On]);
        assert_eq!(sampler.sample(&|| false), Some(52), "{name}");
        assert_eq!(
            nvml.calls().iter().filter(|call| *call == "init").count(),
            2
        );
    }
}

/// A recheck outside D0 after an idle read drops the value, ends the session,
/// keeps nothing to reuse and restarts the dwell.
#[test]
fn the_power_recheck_drops_an_idle_d0_reading() {
    for recheck in [Power::Down, Power::Unknown] {
        let nvml = FakeNvml::new(52);
        let script = Rc::new(RefCell::new(Script::default()));
        let (mut sampler, elapsed) = nvml_sampler(&nvml, &script);
        let advance = |by| elapsed.set(elapsed.get() + by);
        set(&script, Some(false), &[Power::On]);
        assert_eq!(sampler.sample(&|| false), None, "{recheck:?}");
        advance(IDLE_DWELL);
        set(&script, Some(false), &[Power::On, recheck]);
        assert_eq!(sampler.sample(&|| false), None, "{recheck:?}");
        let expected = calls(&[&["init"], &READ, &["shutdown"]]);
        assert_eq!(nvml.calls(), expected, "{recheck:?}");

        for wait in [MILLISECOND, IDLE_DWELL - MILLISECOND] {
            advance(wait);
            set(&script, Some(false), &[Power::On]);
            assert_eq!(sampler.sample(&|| false), None, "{recheck:?}");
        }
        assert_eq!(nvml.calls(), expected, "{recheck:?}");
    }
}

/// An ordinary (`ReadAsync`) idle sample neither reuses a live reading nor
/// reads NVML, and its D0 report keeps the reading for the next live sample.
#[test]
fn an_ordinary_read_does_not_reuse_a_live_reading() {
    let nvml = FakeNvml::new(52);
    let script = Rc::new(RefCell::new(Script::default()));
    let (mut sampler, elapsed) = nvml_sampler(&nvml, &script);
    set(&script, Some(false), &[Power::On]);
    assert_eq!(sampler.sample(&|| false), None);
    elapsed.set(elapsed.get() + IDLE_DWELL);
    set(&script, Some(false), &[Power::On, Power::On]);
    assert_eq!(sampler.sample(&|| false), Some(52));
    let expected = calls(&[&["init"], &READ, &["shutdown"]]);

    set(&script, Some(false), &[Power::On]);
    assert_eq!(sampler.read(Reader::Ordinary, &|| false), None);
    assert_eq!(nvml.calls(), expected);
    set(&script, Some(false), &[Power::On]);
    assert_eq!(sampler.sample(&|| false), Some(52));
    assert_eq!(nvml.calls(), expected);
}

/// The session spans consecutive active samples and ends with the workload,
/// with power leaving D0, at the end of the lease and at release; the next
/// active sample starts a new one.
#[test]
fn the_nvml_session_follows_activity() {
    let nvml = FakeNvml::new(52);
    let script = Rc::new(RefCell::new(Script::default()));
    let (mut sampler, elapsed) = nvml_sampler(&nvml, &script);
    let on = [Power::On, Power::On];
    set(&script, Some(true), &on);
    assert_eq!(sampler.sample(&|| false), Some(52));
    set(&script, Some(true), &on);
    assert_eq!(sampler.sample(&|| false), Some(52));
    assert_eq!(nvml.calls(), calls(&[&["init"], &READ, &READ]));

    // The idle sample reuses the active reading and still ends the session.
    set(&script, Some(false), &[Power::On]);
    assert_eq!(sampler.sample(&|| false), Some(52));
    assert_eq!(
        nvml.calls(),
        calls(&[&["init"], &READ, &READ, &["shutdown"]])
    );

    set(&script, Some(true), &on);
    assert_eq!(sampler.sample(&|| false), Some(52));
    set(&script, Some(true), &[Power::On, Power::Down]);
    assert_eq!(sampler.sample(&|| false), None);
    let mut expected = calls(&[
        &["init"],
        &READ,
        &READ,
        &["shutdown", "init"],
        &READ,
        &READ,
        &["shutdown"],
    ]);
    assert_eq!(nvml.calls(), expected);

    set(&script, Some(true), &on);
    assert_eq!(sampler.sample(&|| false), Some(52));
    elapsed.set(elapsed.get() + LEASE - Duration::from_millis(1));
    sampler.expire();
    expected.extend(calls(&[&["init"], &READ]));
    assert_eq!(nvml.calls(), expected);
    assert_eq!(sampler.remaining_lease(), Some(Duration::from_millis(1)));
    elapsed.set(elapsed.get() + Duration::from_millis(1));
    sampler.expire();
    expected.push("shutdown".into());
    assert_eq!(nvml.calls(), expected);
    assert_eq!(sampler.remaining_lease(), None);

    set(&script, Some(true), &on);
    assert_eq!(sampler.sample(&|| false), Some(52));
    sampler.release();
    sampler.release();
    expected.extend(calls(&[&["init"], &READ, &["shutdown"]]));
    assert_eq!(nvml.calls(), expected);
}

/// A sample ends a held session whose lease ends within `SAMPLE_BUDGET`
/// before its workload read, and one whose lease ran out during the Windows
/// reads before NVML is called; either way the read opens a new session.
#[test]
fn a_sample_never_reads_on_a_session_past_its_lease() {
    let on = [Power::On, Power::On];
    let renewed = || {
        let nvml = FakeNvml::new(52);
        let script = Rc::new(RefCell::new(Script::default()));
        let (mut sampler, elapsed) = nvml_sampler(&nvml, &script);
        set(&script, Some(true), &on);
        assert_eq!(sampler.sample(&|| false), Some(52));
        set(&script, Some(true), &on);
        (nvml, script, sampler, elapsed)
    };
    let kept = calls(&[&["init"], &READ, &READ]);
    let renewed_session = calls(&[&["init"], &READ, &["shutdown", "init"], &READ]);

    for (ahead, expected) in [
        (LEASE - SAMPLE_BUDGET - Duration::from_millis(1), &kept),
        (LEASE - SAMPLE_BUDGET, &renewed_session),
    ] {
        let (nvml, _script, mut sampler, elapsed) = renewed();
        elapsed.set(elapsed.get() + ahead);
        assert_eq!(sampler.sample(&|| false), Some(52), "{ahead:?}");
        assert_eq!(&nvml.calls(), expected, "{ahead:?}");
    }

    for step in ["activity", "power"] {
        let (nvml, script, mut sampler, elapsed) = renewed();
        script.borrow_mut().stall = Some((step, LEASE, elapsed));
        assert_eq!(sampler.sample(&|| false), Some(52), "{step}");
        assert_eq!(nvml.calls(), renewed_session, "{step}");
    }
}

/// Each step of a held session's sample ends it when its observation goes:
/// no NVML call follows, and the session ends unless the sample was
/// cancelled before its power read.
#[test]
fn a_cancelled_sample_makes_no_further_call() {
    for (step, workloads, power_reads, nvml_calls, released) in [
        ("before", 0, 0, 0, false),
        ("activity", 1, 0, 0, false),
        ("power", 1, 1, 0, true),
        ("read", 1, 1, 2, true),
        ("recheck", 1, 2, 2, true),
    ] {
        let nvml = FakeNvml::new(52);
        let script = Rc::new(RefCell::new(Script::default()));
        let (mut sampler, _) = nvml_sampler(&nvml, &script);
        set(&script, Some(true), &[Power::On, Power::On]);
        assert_eq!(sampler.sample(&|| false), Some(52));
        set(&script, Some(true), &[Power::On, Power::On]);
        {
            let mut script = script.borrow_mut();
            script.workloads = 0;
            script.cancel = Some(step.into());
            script.cancelled = step == "before";
        }
        // The real session has no cancellation of its own: "read" is the
        // observation going while NVML is read.
        let before = nvml.calls().len();
        let cancelled =
            || script.borrow().cancelled || (step == "read" && nvml.calls().len() > before);
        assert_eq!(sampler.sample(&cancelled), None, "{step}");
        let script = script.borrow();
        assert_eq!(script.workloads, workloads, "{step}");
        assert_eq!(script.power_reads, power_reads, "{step}");
        let after = &nvml.calls()[before..];
        let reads = after.iter().filter(|c| *c != "shutdown").count();
        assert_eq!(reads, nvml_calls, "{step}");
        assert_eq!(
            after.last().is_some_and(|c| c == "shutdown"),
            released,
            "{step}"
        );
        assert_eq!(sampler.renewed.is_some(), !released, "{step}");
    }
}

/// A dwelled idle sample that becomes unwanted before its NVML read stops
/// there and keeps the dwell. One that becomes unwanted during or after the
/// read still rechecks the D-state and keeps the reading. Either way the
/// next idle sample reads NVML once at most.
#[test]
fn an_unwanted_sample_stops_before_the_nvml_read_and_keeps_one_it_made() {
    for (step, power_reads, reads, kept) in [
        ("activity", 0, 0, false),
        ("power", 1, 0, false),
        ("read", 2, 1, true),
        ("recheck", 2, 1, true),
    ] {
        let script = Rc::new(RefCell::new(Script {
            temperature: Some(52),
            ..Script::default()
        }));
        let (_, elapsed, clock) = clock();
        let mut sampler = Sampler::new(
            Box::new(ScriptedProbes(script.clone())),
            Box::new(ScriptedSession(script.clone())),
            clock,
        );
        set(&script, Some(false), &[Power::On]);
        assert_eq!(sampler.sample(&|| false), None, "{step}");
        elapsed.set(elapsed.get() + IDLE_DWELL);
        set(&script, Some(false), &[Power::On, Power::On]);
        script.borrow_mut().cancel = Some(step.into());
        // The worker publishes nothing for an unwanted sample.
        let celsius = sampler.sample(&Unwanted(&script));
        assert_eq!(celsius, kept.then_some(52), "{step}");
        {
            let mut script = script.borrow_mut();
            assert!(script.cancelled, "{step}");
            assert_eq!(script.power_reads, power_reads, "{step}");
            assert_eq!(script.reads, reads, "{step}");
            script.cancel = None;
            script.cancelled = false;
        }

        set(&script, Some(false), &[Power::On, Power::On]);
        assert_eq!(sampler.sample(&|| false), Some(52), "{step}");
        assert_eq!(script.borrow().reads, 1, "{step}");
    }
}

/// A D0 GPU with a workload or always idle, counting its workload samples.
/// `hold`, when set, blocks each workload read as `FakeNvml` blocks `init`.
struct D0Probes {
    active: bool,
    workloads: Arc<AtomicUsize>,
    hold: Option<Hold>,
}

/// A blocked call: entries are reported on the sender, and each waits until
/// the test sends or drops its end.
#[derive(Clone)]
struct Hold(Arc<Mutex<mpsc::Receiver<()>>>, mpsc::Sender<()>);

impl Hold {
    fn new() -> (Self, mpsc::Sender<()>, mpsc::Receiver<()>) {
        let (release, hold) = mpsc::channel();
        let (entered, entries) = mpsc::channel();
        (Self(Arc::new(Mutex::new(hold)), entered), release, entries)
    }

    fn wait(&self) {
        let _ = self.1.send(());
        let _ = self.0.lock().unwrap().recv();
    }
}

impl Probes for D0Probes {
    fn workload(&mut self) -> Option<bool> {
        self.workloads.fetch_add(1, Ordering::SeqCst);
        if let Some(hold) = &self.hold {
            hold.wait();
        }
        Some(self.active)
    }

    fn power(&mut self) -> Power {
        Power::On
    }
}

/// A worker over `D0Probes`, active unless built `idle`, and a fake NVML.
/// Its clock is the real one plus `ahead`, and `started` counts the
/// factory's runs.
struct Worker {
    active: bool,
    nvml: FakeNvml,
    workloads: Arc<AtomicUsize>,
    workload_hold: Option<Hold>,
    started: Arc<AtomicUsize>,
    ahead: Arc<Mutex<Duration>>,
}

impl Worker {
    fn new() -> Self {
        Self {
            active: true,
            nvml: FakeNvml::new(52),
            workloads: Arc::new(AtomicUsize::new(0)),
            workload_hold: None,
            started: Arc::new(AtomicUsize::new(0)),
            ahead: Arc::new(Mutex::new(Duration::ZERO)),
        }
    }

    fn idle() -> Self {
        Self {
            active: false,
            ..Self::new()
        }
    }

    /// Blocks NVML `init` until the returned sender sends or drops, and
    /// reports each entry on the returned receiver.
    fn hold_init(&mut self) -> (mpsc::Sender<()>, mpsc::Receiver<()>) {
        let (hold, release, entries) = Hold::new();
        self.nvml.hold = Some(hold);
        (release, entries)
    }

    /// Blocks each workload read as `hold_init` blocks `init`.
    fn hold_workload(&mut self) -> (mpsc::Sender<()>, mpsc::Receiver<()>) {
        let (hold, release, entries) = Hold::new();
        self.workload_hold = Some(hold);
        (release, entries)
    }

    /// A receiver that reports when the worker drops its sampler. Only the
    /// next factory's copy of the NVML reports it.
    fn watch_exit(&mut self) -> mpsc::Receiver<()> {
        let (dropped, exits) = mpsc::channel();
        self.nvml.dropped = Some(dropped);
        exits
    }

    fn factory(&mut self, lease: Duration) -> Factory {
        let nvml = self.nvml.clone();
        self.nvml.dropped = None;
        let active = self.active;
        let (workloads, hold, started, ahead) = (
            self.workloads.clone(),
            self.workload_hold.clone(),
            self.started.clone(),
            self.ahead.clone(),
        );
        Box::new(move || {
            started.fetch_add(1, Ordering::SeqCst);
            Sampler {
                lease,
                ..Sampler::new(
                    Box::new(D0Probes {
                        active,
                        workloads,
                        hold,
                    }),
                    Box::new(NvmlSession::new(nvml)),
                    Box::new(move || Instant::now() + *ahead.lock().unwrap()),
                )
            }
        })
    }

    fn host(&mut self) -> Host {
        Host::new(self.factory(LEASE))
    }

    fn wait_for(&self, call: &str, count: usize) {
        let until = Instant::now() + Duration::from_secs(5);
        while self.nvml.calls().iter().filter(|c| *c == call).count() < count {
            assert!(
                Instant::now() < until,
                "{call} x{count}: {:?}",
                self.nvml.calls()
            );
            thread::sleep(Duration::from_millis(1));
        }
    }
}

fn deadline() -> tokio::time::Instant {
    tokio::time::Instant::now() + Duration::from_secs(2)
}

/// A request once the worker has taken the skipped one before it, which
/// holds the one waiting place until then.
fn accepted(host: &Host) -> Pending {
    let until = Instant::now() + Duration::from_secs(5);
    loop {
        if let Some(pending) = host.request(deadline()) {
            return pending;
        }
        assert!(Instant::now() < until, "no request was accepted");
        thread::sleep(Duration::from_millis(1));
    }
}

/// No source never samples, a request without time for a sample neither
/// starts the worker nor samples, and the first request with time starts it.
#[tokio::test]
async fn the_worker_starts_on_the_first_request_with_time_for_a_sample() {
    assert!(Host::none().request(deadline()).is_none());
    let mut worker = Worker::new();
    let host = worker.host();
    let now = tokio::time::Instant::now();
    assert!(host.request(now + RESERVE).is_none());
    assert!(host.request(now).is_none());
    assert_eq!(worker.started.load(Ordering::SeqCst), 0);
    let pending = host.request(deadline()).unwrap();
    assert_eq!(pending.temperature().await, Some(52));
    assert_eq!(worker.started.load(Ordering::SeqCst), 1);
    let pending = host.request(deadline()).unwrap();
    assert_eq!(pending.temperature().await, Some(52));
    assert_eq!(worker.started.load(Ordering::SeqCst), 1);
    assert_eq!(worker.nvml.calls(), calls(&[&["init"], &READ, &READ]));
}

/// While a sample runs, one request waits and any other gets none; a
/// waiting request whose observation went is skipped without a sample.
#[tokio::test]
async fn a_request_behind_a_running_sample_waits_once_and_an_abandoned_one_is_skipped() {
    let mut worker = Worker::new();
    let (release, entries) = worker.hold_init();
    let host = worker.host();
    let running = host.request(deadline()).unwrap();
    entries.recv_timeout(Duration::from_secs(5)).unwrap();
    let waiting = host.request(deadline()).unwrap();
    assert!(host.request(deadline()).is_none());
    drop(waiting);
    release.send(()).unwrap();
    assert_eq!(running.temperature().await, Some(52));
    drop(release);
    let pending = accepted(&host);
    assert_eq!(pending.temperature().await, Some(52));
    assert_eq!(worker.workloads.load(Ordering::SeqCst), 2);
    assert_eq!(worker.nvml.calls(), calls(&[&["init"], &READ, &READ]));
}

/// A waiting request whose time ran out while it waited is skipped, though
/// its observation still holds it.
#[tokio::test]
async fn a_request_whose_time_ran_out_while_it_waited_is_skipped() {
    let mut worker = Worker::new();
    let (release, entries) = worker.hold_init();
    let host = worker.host();
    let running = host.request(deadline()).unwrap();
    entries.recv_timeout(Duration::from_secs(5)).unwrap();
    let short = tokio::time::Instant::now() + RESERVE + Duration::from_millis(200);
    let late = host.request(short).unwrap();
    thread::sleep(Duration::from_millis(400));
    release.send(()).unwrap();
    assert_eq!(running.temperature().await, Some(52));
    drop(release);
    let pending = accepted(&host);
    assert_eq!(pending.temperature().await, Some(52));
    assert_eq!(worker.workloads.load(Ordering::SeqCst), 2);
    drop(late);
}

/// Stop returns while an NVML call hangs, and the observation publishes no
/// value at its own deadline. When the call returns, the sample ends the
/// session it opened and the worker exits.
#[tokio::test]
async fn stop_does_not_wait_for_a_hanging_call() {
    let mut worker = Worker::new();
    let (release, entries) = worker.hold_init();
    let exits = worker.watch_exit();
    let host = worker.host();
    // A first sample whose NVML start fails starts the worker and leaves no
    // session, so the held sample below does not wait for a thread to start.
    worker.nvml.script(|script| script.init = 9);
    release.send(()).unwrap();
    assert_eq!(host.request(deadline()).unwrap().temperature().await, None);
    entries.recv_timeout(Duration::from_secs(5)).unwrap();
    worker.nvml.script(|script| script.init = 0);
    let until = tokio::time::Instant::now() + RESERVE + Duration::from_secs(1);
    let pending = host.request(until).unwrap();
    entries.recv_timeout(Duration::from_secs(5)).unwrap();
    let stopped = Instant::now();
    host.stop();
    assert!(stopped.elapsed() < Duration::from_secs(1));
    assert!(host.request(deadline()).is_none());
    assert_eq!(pending.temperature().await, None);
    assert!(tokio::time::Instant::now() < until);
    assert!(exits.try_recv().is_err());
    drop(release);
    exits.recv_timeout(Duration::from_secs(5)).unwrap();
    assert_eq!(
        worker.nvml.calls(),
        calls(&[&["init", "init"], &READ, &["shutdown"]])
    );
    assert_eq!(worker.started.load(Ordering::SeqCst), 1);
}

/// A held session ends when the worker stops, without a further request.
#[tokio::test]
async fn the_worker_ends_a_held_session_when_it_stops() {
    let mut worker = Worker::new();
    let exits = worker.watch_exit();
    let host = worker.host();
    let pending = host.request(deadline()).unwrap();
    assert_eq!(pending.temperature().await, Some(52));
    host.stop();
    exits.recv_timeout(Duration::from_secs(5)).unwrap();
    assert_eq!(
        worker.nvml.calls(),
        calls(&[&["init"], &READ, &["shutdown"]])
    );
}

/// The worker ends a session at the end of its lease while no request
/// comes, and a lease that ends within `SAMPLE_BUDGET` of a request ends
/// before that sample, which starts a new session.
#[tokio::test]
async fn the_worker_ends_the_session_at_the_end_of_its_lease() {
    let mut worker = Worker::new();
    let host = Host::new(worker.factory(Duration::from_millis(50)));
    let sampled = Instant::now();
    let pending = host.request(deadline()).unwrap();
    assert_eq!(pending.temperature().await, Some(52));
    worker.wait_for("shutdown", 1);
    assert!(sampled.elapsed() >= Duration::from_millis(50));
    assert_eq!(
        worker.nvml.calls(),
        calls(&[&["init"], &READ, &["shutdown"]])
    );

    let mut worker = Worker::new();
    let host = worker.host();
    let pending = host.request(deadline()).unwrap();
    assert_eq!(pending.temperature().await, Some(52));
    *worker.ahead.lock().unwrap() = LEASE - SAMPLE_BUDGET;
    let pending = host.request(deadline()).unwrap();
    assert_eq!(pending.temperature().await, Some(52));
    assert_eq!(
        worker.nvml.calls(),
        calls(&[&["init"], &READ, &["shutdown", "init"], &READ])
    );
}

/// A lease that runs out while the workload read is held ends the session
/// before NVML is called; the read then opens a new one.
#[tokio::test]
async fn a_lease_that_runs_out_during_a_held_workload_read_ends_before_nvml() {
    let mut worker = Worker::new();
    let (release, entries) = worker.hold_workload();
    let lease = SAMPLE_BUDGET + Duration::from_millis(200);
    let host = Host::new(worker.factory(lease));
    release.send(()).unwrap();
    let first = host.request(deadline()).unwrap();
    assert_eq!(first.temperature().await, Some(52));
    entries.recv_timeout(Duration::from_secs(5)).unwrap();
    let held = host.request(deadline()).unwrap();
    entries.recv_timeout(Duration::from_secs(5)).unwrap();
    thread::sleep(lease + Duration::from_millis(100));
    drop(release);
    worker.wait_for("init", 2);
    assert_eq!(
        worker.nvml.calls(),
        calls(&[&["init"], &READ, &["shutdown", "init"], &READ])
    );
    drop(held);
}

/// Stop during a held workload read makes no NVML call, and the request
/// waiting behind it is skipped.
#[tokio::test]
async fn stop_during_a_held_workload_read_makes_no_nvml_call() {
    let mut worker = Worker::new();
    let (release, entries) = worker.hold_workload();
    let exits = worker.watch_exit();
    let host = worker.host();
    let running = host.request(deadline()).unwrap();
    entries.recv_timeout(Duration::from_secs(5)).unwrap();
    let waiting = host.request(deadline()).unwrap();
    host.stop();
    drop(release);
    exits.recv_timeout(Duration::from_secs(5)).unwrap();
    assert_eq!(running.temperature().await, None);
    assert_eq!(waiting.temperature().await, None);
    assert_eq!(worker.workloads.load(Ordering::SeqCst), 1);
    assert_eq!(worker.nvml.calls(), Vec::<String>::new());
}

/// Once the reading has arrived, the reply waits for its sample at most
/// `AFTER_READING`, and never past the request's own time.
#[tokio::test(start_paused = true)]
async fn the_reply_waits_for_a_slow_sample_at_most_after_reading() {
    for (until, waited) in [
        (Duration::from_secs(10), AFTER_READING),
        (Duration::from_millis(100), Duration::from_millis(100)),
    ] {
        let (_sample, reply) = oneshot::channel();
        let asked = tokio::time::Instant::now();
        let pending = Pending {
            reply,
            until: asked + until,
        };
        assert_eq!(pending.temperature().await, None);
        assert_eq!(asked.elapsed(), waited);
    }
}

/// Dropping an NVML session that is still started shuts it down.
#[test]
fn dropping_a_started_nvml_session_ends_it() {
    let nvml = FakeNvml::new(52);
    drop(NvmlSession::new(nvml.clone()));
    assert_eq!(nvml.calls(), Vec::<String>::new());
    let mut session = NvmlSession::new(nvml.clone());
    assert_eq!(session.try_read(), Some(52));
    drop(session);
    assert_eq!(nvml.calls(), calls(&[&["init"], &READ, &["shutdown"]]));
}

/// An idle sample whose NVML read outlasts its observation's wait publishes
/// nothing but keeps the reading, so the idle samples after it within
/// `IDLE_INTERVAL` reuse it instead of each opening a session of their own.
#[tokio::test]
async fn an_idle_read_that_outlasts_the_wait_is_reused_by_the_next_samples() {
    let mut worker = Worker::idle();
    let (release, entries) = worker.hold_init();
    let host = worker.host();
    // The first sample starts the dwell and reads nothing.
    assert_eq!(host.request(deadline()).unwrap().temperature().await, None);
    *worker.ahead.lock().unwrap() = IDLE_DWELL;
    let slow = accepted(&host);
    entries.recv_timeout(Duration::from_secs(5)).unwrap();
    // The observation stops waiting `AFTER_READING` after its reading, well
    // before its deadline, while `init` is held.
    assert_eq!(slow.temperature().await, None);
    drop(release);
    worker.wait_for("shutdown", 1);
    for _ in 0..2 {
        assert_eq!(accepted(&host).temperature().await, Some(52));
    }
    assert_eq!(
        worker.nvml.calls(),
        calls(&[&["init"], &READ, &["shutdown"]])
    );
    assert_eq!(worker.workloads.load(Ordering::SeqCst), 4);
}

/// An idle NVML read that ends after its request's time, `RESERVE` before
/// the observation's deadline, publishes nothing even to an observation
/// that asks for it only then, but keeps the reading as managed does: the
/// next idle samples reuse it without a session of their own.
#[tokio::test]
async fn an_idle_read_past_its_requests_time_is_kept_but_not_published() {
    let mut worker = Worker::idle();
    let (release, entries) = worker.hold_init();
    let host = worker.host();
    assert_eq!(host.request(deadline()).unwrap().temperature().await, None);
    *worker.ahead.lock().unwrap() = IDLE_DWELL;
    let short = Duration::from_millis(200);
    let late = loop {
        if let Some(late) = host.request(tokio::time::Instant::now() + RESERVE + short) {
            break late;
        }
        thread::sleep(Duration::from_millis(1));
    };
    entries.recv_timeout(Duration::from_secs(5)).unwrap();
    thread::sleep(short + Duration::from_millis(100));
    drop(release);
    worker.wait_for("shutdown", 1);
    // The observation asks only after the sample ended.
    assert_eq!(late.temperature().await, None);
    for _ in 0..2 {
        assert_eq!(accepted(&host).temperature().await, Some(52));
    }
    assert_eq!(
        worker.nvml.calls(),
        calls(&[&["init"], &READ, &["shutdown"]])
    );
}

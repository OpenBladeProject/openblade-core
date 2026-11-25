//! GPU clock offsets: the controller against the managed-recorded vectors,
//! the management state machine against the managed provider's rules, and
//! the worker's budget, refusal and Stop. Every NVML here is scripted; no
//! test loads nvml.dll.
use super::{fake::Fake, *};
use serde_json::{Value, json};
use std::{
    collections::BTreeMap,
    sync::{
        Mutex,
        atomic::{AtomicBool, AtomicUsize},
        mpsc,
    },
};

// --- The managed-recorded controller vectors ---

/// The vector model of managed `NativeGpuClockOffsetVectorTests.ScriptedNvml`.
struct Scripted {
    driver: Value,
    faults: Vec<Value>,
    occurrences: BTreeMap<String, i64>,
    calls: Vec<String>,
    core: i32,
    memory: i32,
}

impl Scripted {
    fn new(row: &Value) -> Self {
        Self {
            driver: row["driver"].clone(),
            faults: row["faults"].as_array().unwrap().clone(),
            occurrences: BTreeMap::new(),
            calls: Vec::new(),
            core: row["driver"]["core"][2].as_i64().unwrap() as i32,
            memory: row["driver"]["memory"][2].as_i64().unwrap() as i32,
        }
    }

    fn fault(&mut self, call: &str) -> Option<Value> {
        let occurrence = self.occurrences.entry(call.into()).or_default();
        *occurrence += 1;
        let occurrence = *occurrence;
        self.faults
            .iter()
            .find(|fault| fault["call"] == call && fault["index"] == occurrence)
            .cloned()
    }

    fn status(fault: Option<&Value>) -> i32 {
        fault.and_then(|f| f["status"].as_i64()).unwrap_or(0) as i32
    }
}

impl Nvml for Scripted {
    fn init(&mut self) -> i32 {
        self.calls.push("init".into());
        Self::status(self.fault("init").as_ref())
    }

    fn shutdown(&mut self) -> i32 {
        self.calls.push("shutdown".into());
        0
    }

    fn device_count(&mut self) -> Result<u32, i32> {
        self.calls.push("count".into());
        match Self::status(self.fault("count").as_ref()) {
            SUCCESS => Ok(self.driver["count"].as_u64().unwrap() as u32),
            status => Err(status),
        }
    }

    fn device(&mut self, index: u32) -> Result<usize, i32> {
        self.calls.push(format!("handle {index}"));
        match Self::status(self.fault("handle").as_ref()) {
            SUCCESS => Ok(1),
            status => Err(status),
        }
    }

    fn name(&mut self, _: usize, name: &mut [u8; NAME_CAPACITY]) -> i32 {
        self.calls.push("name".into());
        let status = Self::status(self.fault("name").as_ref());
        if status == SUCCESS {
            let text = self.driver["name"].as_str().unwrap().as_bytes();
            name[..text.len()].copy_from_slice(text);
        }
        status
    }

    fn clock_offsets(&mut self, _: usize, info: &mut ClockOffset) -> i32 {
        self.calls.push(format!("get {}", info.kind));
        let fault = self.fault("get");
        let status = Self::status(fault.as_ref());
        if status != SUCCESS {
            return status;
        }
        let graphics = info.kind == ClockKind::Graphics as i32;
        let range = &self.driver[if graphics { "core" } else { "memory" }];
        info.minimum = range[0].as_i64().unwrap() as i32;
        info.maximum = range[1].as_i64().unwrap() as i32;
        info.current = fault
            .as_ref()
            .and_then(|f| f["current"].as_i64())
            .map_or(if graphics { self.core } else { self.memory }, |c| c as i32);
        SUCCESS
    }

    fn set_clock_offsets(&mut self, _: usize, info: &mut ClockOffset) -> i32 {
        self.calls
            .push(format!("set {} {}", info.kind, info.current));
        let fault = self.fault("set");
        let stored = match fault.as_ref().map(|f| &f["store"]) {
            None | Some(Value::Null) | Some(Value::Bool(true)) => Some(info.current),
            Some(Value::Bool(false)) => None,
            Some(value) => Some(value.as_i64().unwrap() as i32),
        };
        if let Some(offset) = stored {
            if info.kind == ClockKind::Graphics as i32 {
                self.core = offset;
            } else {
                self.memory = offset;
            }
        }
        Self::status(fault.as_ref())
    }
}

fn capabilities_json(capabilities: &Capabilities) -> Value {
    let range = |r: &Range| json!([r.minimum, r.maximum, r.current]);
    json!({"name": capabilities.name, "core": range(&capabilities.core), "memory": range(&capabilities.memory)})
}

#[test]
fn controller_vectors_match_managed() {
    let vectors: Value = serde_json::from_str(include_str!(
        "../../../../tests/data/gpu-clock-offset-vectors.json"
    ))
    .unwrap();
    let rows = vectors["controller"].as_array().unwrap();
    assert_eq!(rows.len(), 38);
    let mut failures = Vec::new();
    for row in rows {
        let mut controller = Controller::new(Scripted::new(row));
        let result = match &row["operation"] {
            Value::String(query) if query == "query" => controller
                .query()
                .as_ref()
                .map_or(Value::Null, capabilities_json),
            operation => {
                let set = &operation["set"];
                let offsets = GpuClockOffsets {
                    core_megahertz: set[0].as_i64().unwrap() as i32,
                    memory_megahertz: set[1].as_i64().unwrap() as i32,
                };
                match controller.set(offsets) {
                    Ok(confirmed) => json!({"confirmed": capabilities_json(&confirmed)}),
                    Err(error) => json!({
                        "failure": match error.failure {
                            Failure::NotApplied => "KnownNotApplied",
                            Failure::Indeterminate => "Indeterminate",
                            Failure::Unavailable => "Unavailable",
                        },
                        "message": error.message,
                    }),
                }
            }
        };
        let nvml = controller.nvml;
        let observed =
            json!({"result": result, "calls": nvml.calls, "driver": [nvml.core, nvml.memory]});
        if observed != row["expect"] {
            failures.push(format!("{}: {observed}", row["name"]));
        }
    }
    assert!(failures.is_empty(), "{failures:#?}");
}

#[test]
fn the_nvml_structure_is_the_v1_layout() {
    assert_eq!(std::mem::size_of::<ClockOffset>(), 24);
    let request = ClockOffset::set(ClockKind::Memory, 500).unwrap();
    assert_eq!(request.version, (1 << 24) | 24);
    assert_eq!(
        (request.kind, request.pstate, request.current),
        (2, 0, 1000)
    );
    let request = ClockOffset::set(ClockKind::Graphics, -125).unwrap();
    assert_eq!((request.kind, request.current), (0, -125));
    let query = ClockOffset::query(ClockKind::Memory);
    assert_eq!(
        (query.version, query.kind, query.current),
        ((1 << 24) | 24, 2, 0)
    );
    // A memory offset whose doubled NVML value does not fit has no request.
    assert_eq!(ClockOffset::set(ClockKind::Memory, i32::MAX / 2 + 1), None);
    assert!(ClockOffset::set(ClockKind::Memory, i32::MAX / 2).is_some());
}

#[test]
fn invalid_offsets_are_refused_before_any_nvml_call() {
    for offsets in [(10_001, 0), (0, -10_001)] {
        let row = json!({"driver":{"count":1,"name":ADMITTED_NAME,"core":[-1000,1000,0],"memory":[-2000,6000,0]},"faults":[]});
        let mut controller = Controller::new(Scripted::new(&row));
        let error = controller.set(offsets_of(offsets)).unwrap_err();
        assert_eq!(error.failure, Failure::NotApplied);
        assert_eq!(error.message, "GPU clock offsets are invalid.");
        assert!(controller.nvml.calls.is_empty());
    }
}

fn offsets_of((core, memory): (i32, i32)) -> GpuClockOffsets {
    GpuClockOffsets {
        core_megahertz: core,
        memory_megahertz: memory,
    }
}

const DEFAULTS: GpuClockOffsets = GpuClockOffsets {
    core_megahertz: 0,
    memory_megahertz: 0,
};

/// The activity probe and how often it was read.
fn activity(active: Option<bool>) -> (Activity, Arc<AtomicUsize>, Arc<Mutex<Option<bool>>>) {
    let reads = Arc::new(AtomicUsize::new(0));
    let value = Arc::new(Mutex::new(active));
    let (r, v) = (reads.clone(), value.clone());
    (
        Box::new(move || {
            r.fetch_add(1, Ordering::SeqCst);
            *v.lock().unwrap()
        }),
        reads,
        value,
    )
}

struct Rig {
    fake: Fake,
    management: Management<Fake>,
    reads: Arc<AtomicUsize>,
    active: Arc<Mutex<Option<bool>>>,
}

impl Rig {
    fn new(core: i32, memory: i32) -> Self {
        Self::over(Fake::new(core, memory))
    }

    fn over(fake: Fake) -> Self {
        let (probe, reads, active) = activity(Some(false));
        Self {
            management: Management::new(fake.clone(), probe, RZ09_0581),
            fake,
            reads,
            active,
        }
    }

    fn activity_reads(&self) -> usize {
        self.reads.load(Ordering::SeqCst)
    }

    fn set_active(&self, active: Option<bool>) {
        *self.active.lock().unwrap() = active;
    }

    /// An enable that fails after writing core, with a restore that fails too.
    fn uncertain(&mut self, offsets: (i32, i32), retain: bool) {
        // The core write applies and fails; its restore fails unapplied.
        self.fake.with(|d| {
            d.sets_fail = true;
            d.storing_failures = 1;
        });
        let error = self
            .management
            .set(offsets_of(offsets), retain)
            .unwrap_err();
        assert_eq!(error.failure, Failure::Indeterminate);
        self.fake.with(|d| d.sets_fail = false);
        assert_eq!(self.management.state(), State::SubmissionUncertain);
    }
}

// --- The management machine ---

#[test]
fn a_fresh_management_is_unknown_and_reads_nothing() {
    let mut rig = Rig::new(50, 200);
    assert_eq!(rig.management.state(), State::Unknown);
    assert_eq!(rig.management.read(), None);
    // Write-to-discover: no NVML session and no activity read wakes the GPU.
    assert_eq!((rig.fake.inits(), rig.activity_reads()), (0, 0));
}

#[test]
fn a_confirmed_enable_is_managed_and_publishes_the_clipped_ranges() {
    let mut rig = Rig::new(0, 0);
    let confirmed = rig
        .management
        .set(offsets_of((125, 500)), true)
        .unwrap()
        .unwrap();
    assert_eq!(rig.management.state(), State::Managed);
    assert_eq!(rig.fake.current(), (125, 500));
    // The driver's -1000..1000 core and -1000..3000 memory, narrowed to the
    // RZ09-0581 envelope.
    assert_eq!(
        (confirmed.core, confirmed.memory),
        (
            Range {
                minimum: -1000,
                maximum: 400,
                current: 125
            },
            Range {
                minimum: -1000,
                maximum: 3000,
                current: 500
            }
        )
    );
    // One preflight query session and one write session.
    assert_eq!(rig.fake.inits(), 2);
    assert_eq!(rig.fake.with(|d| d.shutdowns), 2);
}

#[test]
fn a_confirmed_disable_is_a_reset_with_no_observation() {
    let mut rig = Rig::new(125, 500);
    assert_eq!(rig.management.set(DEFAULTS, false).unwrap(), None);
    assert_eq!(rig.management.state(), State::ResetConfirmed);
    assert!(rig.management.has_confirmed_default_reset());
    assert_eq!(rig.fake.current(), (0, 0));
    assert_eq!(rig.management.read(), None);
}

#[test]
fn refused_admission_writes_nothing_and_keeps_the_state() {
    let mut rig = Rig::new(0, 0);
    rig.management.set(offsets_of((100, 100)), true).unwrap();
    let cases = [
        // Invalid contract values.
        (
            (10_001, 0),
            true,
            Failure::NotApplied,
            "GPU clock offsets are invalid.",
        ),
        // Relinquishing writes only a reset.
        (
            (5, 0),
            false,
            Failure::NotApplied,
            "Relinquishing GPU clock tuning requires a default-offset reset.",
        ),
        // Inside the driver's range, outside the RZ09-0581 envelope.
        (
            (401, 0),
            true,
            Failure::Unavailable,
            "The requested GPU clock offsets are outside the ranges reported by the NVIDIA driver.",
        ),
        (
            (0, 3001),
            true,
            Failure::Unavailable,
            "The requested GPU clock offsets are outside the ranges reported by the NVIDIA driver.",
        ),
        (
            (-1001, 0),
            true,
            Failure::Unavailable,
            "The requested GPU clock offsets are outside the ranges reported by the NVIDIA driver.",
        ),
    ];
    for (offsets, retain, failure, message) in cases {
        let error = rig.management.set(offsets_of(offsets), retain).unwrap_err();
        assert_eq!((error.failure, error.message.as_str()), (failure, message));
        assert_eq!(rig.fake.sets(), 2, "{offsets:?}");
        assert_eq!(rig.management.state(), State::Managed);
    }
    // Inside the envelope, outside the driver's range.
    rig.fake.with(|d| d.core.1 = 300);
    let error = rig.management.set(offsets_of((350, 0)), true).unwrap_err();
    assert_eq!(error.failure, Failure::Unavailable);
    // The envelope's bounds themselves are admitted.
    rig.fake.with(|d| d.core.1 = 1000);
    rig.management.set(offsets_of((400, 3000)), true).unwrap();
    rig.management
        .set(offsets_of((-1000, -1000)), true)
        .unwrap();
    assert_eq!(rig.fake.current(), (-1000, -1000));
}

#[test]
fn an_asleep_or_lost_gpu_at_submission_is_unavailable_with_no_write() {
    let mut rig = Rig::new(50, 200);
    rig.fake.with(|d| d.queries_fail = true);
    let error = rig
        .management
        .set(offsets_of((125, 500)), true)
        .unwrap_err();
    assert_eq!(
        (error.failure, error.message.as_str()),
        (Failure::Unavailable, "GPU clock tuning is unavailable.")
    );
    assert_eq!(rig.fake.sets(), 0);
    assert_eq!(rig.management.state(), State::Unknown);
}

#[test]
fn an_nvml_error_known_not_applied_keeps_the_confirmation() {
    let mut rig = Rig::new(0, 0);
    let before = rig.management.set(offsets_of((100, 100)), true).unwrap();
    // A refused write that the restore readback shows left nothing changed.
    rig.fake.with(|d| d.sets_fail = true);
    let error = rig
        .management
        .set(offsets_of((125, 500)), true)
        .unwrap_err();
    assert_eq!(error.failure, Failure::NotApplied);
    assert!(error.message.contains("were restored"));
    assert_eq!(rig.management.state(), State::Managed);
    rig.fake.with(|d| d.sets_fail = false);
    assert_eq!(rig.management.read(), before);
}

#[test]
fn a_readback_mismatch_that_restores_is_not_applied() {
    let mut rig = Rig::new(0, 0);
    let before = rig.management.set(offsets_of((50, 50)), true).unwrap();
    // A driver that keeps its core at 50 whatever is written.
    rig.fake.with(|d| d.clamp_core = Some(50));
    let error = rig
        .management
        .set(offsets_of((125, 500)), true)
        .unwrap_err();
    assert_eq!(error.failure, Failure::NotApplied);
    assert_eq!(
        error.message,
        "GPU clock tuning failed and the previous offsets were restored. NVIDIA confirmed Core=50;Memory=500 instead of Core=125;Memory=500."
    );
    assert_eq!(rig.management.state(), State::Managed);
    assert_eq!(rig.management.read(), before);
}

#[test]
fn a_restore_failure_is_an_uncertain_submission_that_withdraws_every_confirmation() {
    let mut rig = Rig::new(0, 0);
    rig.management.set(DEFAULTS, false).unwrap();
    assert!(rig.management.has_confirmed_default_reset());
    rig.uncertain((125, 500), true);
    // A later disable cannot reuse the stale reset, and a read publishes no
    // stale offsets.
    assert!(!rig.management.has_confirmed_default_reset());
    assert_eq!(rig.fake.current(), (125, 0));
}

#[test]
fn the_first_read_resolves_an_uncertain_submission_whatever_the_activity() {
    let mut rig = Rig::new(0, 0);
    rig.uncertain((125, 500), true);
    let inits = rig.fake.inits();
    let observed = rig.management.read().unwrap();
    assert_eq!(rig.activity_reads(), 0);
    assert_eq!(rig.fake.inits(), inits + 1);
    assert_eq!(rig.management.state(), State::Managed);
    assert_eq!(observed.current(), offsets_of((125, 0)));
}

#[test]
fn a_reading_outside_the_envelope_resolves_to_managed_with_no_confirmation() {
    let mut rig = Rig::new(0, 0);
    rig.uncertain((125, 500), true);
    // Something else left the core above the envelope's 400.
    rig.fake.with(|d| d.core.2 = 500);
    assert_eq!(rig.management.read(), None);
    assert_eq!(rig.management.state(), State::Managed);
    // Relinquishing still releases what OpenBlade submitted.
    assert!(rig.management.relinquish());
    assert_eq!(rig.management.state(), State::Relinquished);
}

#[test]
fn an_unreadable_uncertain_submission_is_queried_again_only_while_active() {
    let mut rig = Rig::new(0, 0);
    rig.uncertain((125, 500), true);
    rig.fake.with(|d| d.queries_fail = true);
    let inits = rig.fake.inits();
    assert_eq!(rig.management.read(), None);
    assert_eq!(rig.fake.inits(), inits + 1);
    // Idle: no query, so an idle dGPU is not woken.
    assert_eq!(rig.management.read(), None);
    assert_eq!(rig.fake.inits(), inits + 1);
    assert_eq!(rig.activity_reads(), 1);
    rig.fake.with(|d| d.queries_fail = false);
    rig.set_active(Some(true));
    assert!(rig.management.read().is_some());
    assert_eq!(rig.fake.inits(), inits + 2);
    assert_eq!(rig.management.state(), State::Managed);
}

#[test]
fn an_uncertain_resubmission_rearms_the_ungated_query() {
    // Managed provider: every uncertain submission allows one more query
    // whatever the activity, even after an earlier one was spent.
    let mut rig = Rig::new(0, 0);
    rig.uncertain((125, 500), true);
    rig.fake.with(|d| d.queries_fail = true);
    assert_eq!(rig.management.read(), None);
    // The preflight resolves it, and the resubmission is uncertain again.
    rig.fake.with(|d| d.queries_fail = false);
    rig.uncertain((150, 500), true);
    let inits = rig.fake.inits();
    assert!(rig.management.read().is_some());
    assert_eq!(rig.fake.inits(), inits + 1);
    assert_eq!(rig.management.state(), State::Managed);
}

#[test]
fn a_power_change_rearms_one_ungated_query() {
    let mut rig = Rig::new(0, 0);
    rig.uncertain((125, 500), true);
    rig.fake.with(|d| d.queries_fail = true);
    assert_eq!(rig.management.read(), None);
    let inits = rig.fake.inits();
    rig.management.rearm();
    rig.fake.with(|d| d.queries_fail = false);
    assert!(rig.management.read().is_some());
    assert_eq!(rig.fake.inits(), inits + 1);
    // Rearm outside an uncertain submission changes nothing.
    rig.management.rearm();
    assert_eq!(rig.management.state(), State::Managed);
}

#[test]
fn an_uncertain_reset_that_reads_the_defaults_is_a_confirmed_reset() {
    let mut rig = Rig::new(125, 500);
    rig.management.set(offsets_of((125, 500)), true).unwrap();
    rig.uncertain((0, 0), false);
    rig.fake.with(|d| {
        d.core.2 = 0;
        d.memory.2 = 0;
    });
    assert_eq!(rig.management.read(), None);
    assert_eq!(rig.management.state(), State::ResetConfirmed);
}

#[test]
fn an_uncertain_enable_that_left_a_confirmed_default_restores_it() {
    let mut rig = Rig::new(0, 0);
    rig.management.set(DEFAULTS, false).unwrap();
    assert!(rig.management.relinquish());
    assert_eq!(rig.management.state(), State::RelinquishedDefaultConfirmed);
    rig.uncertain((125, 500), true);
    rig.fake.with(|d| d.core.2 = 0);
    assert_eq!(rig.management.read(), None);
    assert_eq!(rig.management.state(), State::RelinquishedDefaultConfirmed);
    // Defaults after an enable from an unconfirmed state are managed.
    let mut rig = Rig::new(0, 0);
    rig.uncertain((125, 500), true);
    rig.fake.with(|d| d.core.2 = 0);
    assert!(rig.management.read().is_some());
    assert_eq!(rig.management.state(), State::Managed);
}

#[test]
fn a_second_uncertain_submission_keeps_the_state_before_the_first() {
    let mut rig = Rig::new(0, 0);
    rig.management.set(DEFAULTS, false).unwrap();
    assert!(rig.management.relinquish());
    rig.uncertain((125, 500), true);
    // A submit without its own preflight, as a paired write's is, fails
    // uncertain again; the prior state stays the confirmed default.
    rig.fake.with(|d| {
        d.sets_fail = true;
        d.storing_failures = 1;
    });
    let error = rig
        .management
        .submit(offsets_of((150, 500)), true)
        .unwrap_err();
    assert_eq!(error.failure, Failure::Indeterminate);
    rig.fake.with(|d| {
        d.sets_fail = false;
        d.core.2 = 0;
    });
    assert_eq!(rig.management.read(), None);
    assert_eq!(rig.management.state(), State::RelinquishedDefaultConfirmed);
}

#[test]
fn the_write_preflight_resolves_an_uncertain_submission_before_writing() {
    let mut rig = Rig::new(0, 0);
    rig.uncertain((125, 500), true);
    let error = rig.management.set(offsets_of((401, 0)), true).unwrap_err();
    assert_eq!(error.failure, Failure::Unavailable);
    // The preflight read the driver and resolved it, though nothing was written.
    assert_eq!(rig.management.state(), State::Managed);
}

#[test]
fn a_managed_read_refreshes_only_an_active_gpu_and_keeps_its_confirmation_on_failure() {
    let mut rig = Rig::new(0, 0);
    let confirmed = rig.management.set(offsets_of((100, 100)), true).unwrap();
    let inits = rig.fake.inits();
    assert_eq!(rig.management.read(), confirmed);
    assert_eq!(rig.fake.inits(), inits);
    rig.set_active(Some(true));
    rig.fake.with(|d| d.core.2 = 150);
    assert_eq!(rig.management.read().unwrap().core.current, 150);
    rig.fake.with(|d| d.queries_fail = true);
    assert_eq!(rig.management.read().unwrap().core.current, 150);
    // An unknown activity is not active.
    rig.set_active(None);
    rig.fake.with(|d| d.queries_fail = false);
    let inits = rig.fake.inits();
    rig.management.read();
    assert_eq!(rig.fake.inits(), inits);
}

#[test]
fn relinquishing_writes_nothing_and_records_a_confirmed_default() {
    // Managed non-default offsets.
    let mut rig = Rig::new(0, 0);
    rig.management.set(offsets_of((100, 100)), true).unwrap();
    let sets = rig.fake.sets();
    assert!(rig.management.relinquish());
    assert_eq!(rig.management.state(), State::Relinquished);
    assert_eq!(rig.fake.sets(), sets);
    assert_eq!(rig.fake.current(), (100, 100));
    assert_eq!(rig.management.read(), None);
    // Managed defaults, and a confirmed reset.
    let mut rig = Rig::new(10, 10);
    rig.management.set(DEFAULTS, true).unwrap();
    assert!(rig.management.relinquish());
    assert_eq!(rig.management.state(), State::RelinquishedDefaultConfirmed);
    let mut rig = Rig::new(10, 10);
    rig.management.set(DEFAULTS, false).unwrap();
    assert!(rig.management.relinquish());
    assert_eq!(rig.management.state(), State::RelinquishedDefaultConfirmed);
    // Unknown stays unconfirmed.
    let mut rig = Rig::new(10, 10);
    assert!(rig.management.relinquish());
    assert_eq!(rig.management.state(), State::Relinquished);
}

#[test]
fn an_unreadable_uncertain_submission_is_never_relinquished() {
    let mut rig = Rig::new(0, 0);
    rig.uncertain((125, 500), true);
    rig.fake.with(|d| d.queries_fail = true);
    let sets = rig.fake.sets();
    assert!(!rig.management.relinquish());
    assert_eq!(rig.management.state(), State::SubmissionUncertain);
    // The ungated query is spent and the dGPU is idle: no further query.
    let inits = rig.fake.inits();
    assert!(!rig.management.relinquish());
    assert_eq!(rig.fake.inits(), inits);
    assert_eq!(rig.activity_reads(), 1);
    // Active and readable again: resolved, then released.
    rig.fake.with(|d| d.queries_fail = false);
    rig.set_active(Some(true));
    assert!(rig.management.relinquish());
    assert_eq!(rig.management.state(), State::Relinquished);
    assert_eq!(rig.fake.sets(), sets);
}

#[test]
fn a_resume_invalidates_every_confirmation() {
    let mut rig = Rig::new(0, 0);
    rig.management.set(offsets_of((100, 100)), true).unwrap();
    assert!(rig.management.invalidate());
    assert_eq!(rig.management.state(), State::Managed);
    // The cached confirmation is gone; an idle read publishes nothing.
    assert_eq!(rig.management.read(), None);
    rig.management.set(DEFAULTS, false).unwrap();
    assert!(rig.management.invalidate());
    assert_eq!(rig.management.state(), State::Unknown);
    let mut rig = Rig::new(0, 0);
    rig.management.set(DEFAULTS, false).unwrap();
    rig.management.relinquish();
    assert!(!rig.management.invalidate());
    assert_eq!(rig.management.state(), State::Relinquished);
    let mut rig = Rig::new(0, 0);
    rig.uncertain((125, 500), true);
    assert!(!rig.management.invalidate());
    assert_eq!(rig.management.state(), State::SubmissionUncertain);
}

#[test]
fn a_confirmed_reset_can_be_reused_by_a_paired_relinquishing_write() {
    let mut rig = Rig::new(0, 0);
    assert!(!rig.management.has_confirmed_default_reset());
    // Nothing to reuse while Unknown.
    assert!(!rig.management.mark_reset_confirmed());
    assert_eq!(rig.management.state(), State::Unknown);
    rig.management.set(DEFAULTS, false).unwrap();
    assert!(rig.management.relinquish());
    let inits = rig.fake.inits();
    assert!(rig.management.mark_reset_confirmed());
    assert_eq!(rig.management.state(), State::ResetConfirmed);
    assert!(rig.management.has_confirmed_default_reset());
    assert_eq!(rig.fake.inits(), inits);
}

#[test]
fn a_reuse_decision_withdrawn_by_an_interleaved_resume_is_refused() {
    let mut rig = Rig::new(0, 0);
    rig.management.set(DEFAULTS, false).unwrap();
    // The paired relinquishing write's preflight decides to reuse the reset.
    assert!(rig.management.has_confirmed_default_reset());
    // A resume lands between that decision and the completion.
    assert!(rig.management.invalidate());
    assert_eq!(rig.management.state(), State::Unknown);
    // The completion cannot reassert the reset the resume withdrew.
    assert!(!rig.management.mark_reset_confirmed());
    assert_eq!(rig.management.state(), State::Unknown);
}

#[test]
fn a_submission_alone_enforces_the_envelope_and_the_reset_rule() {
    let mut rig = Rig::new(0, 0);
    // Above the RZ09-0581 core envelope (400), inside the driver's 1000.
    let error = rig
        .management
        .submit(offsets_of((1000, 0)), true)
        .unwrap_err();
    assert_eq!(error.failure, Failure::Unavailable);
    // A relinquishing write of non-default offsets.
    let error = rig
        .management
        .submit(offsets_of((5, 0)), false)
        .unwrap_err();
    assert_eq!(
        (error.failure, error.message.as_str()),
        (
            Failure::NotApplied,
            "Relinquishing GPU clock tuning requires a default-offset reset."
        )
    );
    // Neither opened NVML or changed the state.
    assert_eq!((rig.fake.inits(), rig.fake.sets()), (0, 0));
    assert_eq!(rig.fake.current(), (0, 0));
    assert_eq!(rig.management.state(), State::Unknown);
    assert!(!rig.management.has_confirmed_default_reset());
}

#[test]
fn an_uncertain_submission_does_not_survive_a_restart_and_the_next_write_reads_the_driver() {
    // Managed keeps uncertainty in memory only: a restarted Service is
    // Unknown, reads nothing, and its first write's preflight reads the
    // offsets the earlier submission left, so its restore uses them.
    let fake = Fake::new(0, 0);
    let mut first = Rig::over(fake.clone());
    first.uncertain((125, 500), true);
    drop(first);
    let mut restarted = Rig::over(fake.clone());
    assert_eq!(restarted.management.state(), State::Unknown);
    assert_eq!(restarted.management.read(), None);
    assert_eq!(restarted.activity_reads(), 0);
    fake.with(|d| d.sets_fail = true);
    let error = restarted
        .management
        .set(offsets_of((200, 200)), true)
        .unwrap_err();
    assert_eq!(error.failure, Failure::NotApplied);
    assert_eq!(fake.current(), (125, 0));
    fake.with(|d| d.sets_fail = false);
    restarted
        .management
        .set(offsets_of((200, 200)), true)
        .unwrap();
    assert_eq!(restarted.management.state(), State::Managed);
}

#[test]
fn the_policy_clip_publishes_no_raw_driver_range() {
    let driver = |core: (i32, i32, i32), memory: (i32, i32, i32)| Capabilities {
        name: ADMITTED_NAME.into(),
        core: Range {
            minimum: core.0,
            maximum: core.1,
            current: core.2,
        },
        memory: Range {
            minimum: memory.0,
            maximum: memory.1,
            current: memory.2,
        },
    };
    let clipped = RZ09_0581
        .clip(&driver((-500, 1000, 0), (-3000, 2000, 10)))
        .unwrap();
    assert_eq!((clipped.core.minimum, clipped.core.maximum), (-500, 400));
    assert_eq!(
        (clipped.memory.minimum, clipped.memory.maximum),
        (-1000, 2000)
    );
    // No overlap, or a current offset outside the envelope.
    assert_eq!(RZ09_0581.clip(&driver((500, 1000, 600), (0, 0, 0))), None);
    assert_eq!(RZ09_0581.clip(&driver((-1000, 1000, 401), (0, 0, 0))), None);
    assert_eq!(
        RZ09_0581.clip(&driver((0, 0, 0), (-1000, 6000, 3001))),
        None
    );
    assert!(
        RZ09_0581
            .clip(&driver((0, 0, 0), (-1000, 3000, 3000)))
            .is_some()
    );
}

// --- The worker ---

fn worker(fake: &Fake) -> Worker<Fake> {
    let fake = fake.clone();
    Worker::spawn(move || {
        let (probe, _, _) = activity(Some(false));
        Management::new(fake, probe, RZ09_0581)
    })
    .unwrap()
}

const BUDGET: Duration = Duration::from_secs(5);

#[tokio::test]
async fn a_call_runs_on_the_worker() {
    let fake = Fake::new(0, 0);
    let worker = worker(&fake);
    let result = worker
        .call(BUDGET, |m| {
            m.set(offsets_of((125, 500)), true).map(|c| c.is_some())
        })
        .await;
    assert_eq!(result, Called::Done(Ok(true)));
    assert_eq!(
        worker.call(BUDGET, |m| m.state()).await,
        Called::Done(State::Managed)
    );
    let thread = worker
        .call(BUDGET, |_| thread::current().name().map(str::to_owned))
        .await;
    assert_eq!(
        thread,
        Called::Done(Some("openblade-gpu-clock-offsets".to_owned()))
    );
}

#[tokio::test]
async fn a_write_that_outlasts_its_budget_is_abandoned_and_its_result_recorded() {
    let fake = Fake::new(0, 0);
    let (release, hold) = mpsc::channel();
    fake.with(|d| d.hold = Some(hold));
    let worker = worker(&fake);
    let result = worker
        .call(Duration::from_millis(100), |m| {
            m.set(offsets_of((125, 500)), true).is_ok()
        })
        .await;
    assert_eq!(result, Called::Abandoned);
    release.send(()).unwrap();
    // The worker finished the write and recorded what the driver holds.
    assert_eq!(
        worker.call(BUDGET, |m| m.state()).await,
        Called::Done(State::Managed)
    );
    assert_eq!(fake.current(), (125, 500));
}

#[tokio::test]
async fn a_call_whose_budget_ends_before_it_starts_never_starts() {
    let fake = Fake::new(0, 0);
    let worker = worker(&fake);
    let (release, hold) = mpsc::channel::<()>();
    let (started, running) = mpsc::channel();
    let first = worker.call(BUDGET, move |_| {
        started.send(()).unwrap();
        hold.recv().unwrap();
    });
    let ran = Arc::new(AtomicBool::new(false));
    let flag = ran.clone();
    let second = async {
        while running.try_recv().is_err() {
            tokio::task::yield_now().await;
        }
        let queued = worker
            .call(Duration::from_millis(100), move |m| {
                flag.store(true, Ordering::SeqCst);
                m.set(offsets_of((125, 500)), true).is_ok()
            })
            .await;
        release.send(()).unwrap();
        queued
    };
    let (first, second) = tokio::join!(first, second);
    assert_eq!(first, Called::Done(()));
    assert_eq!(second, Called::NotStarted);
    assert_eq!(
        worker.call(BUDGET, |m| m.state()).await,
        Called::Done(State::Unknown)
    );
    assert!(!ran.load(Ordering::SeqCst));
    assert_eq!(fake.sets(), 0);
}

#[tokio::test]
async fn a_cancelled_call_is_withdrawn_and_never_runs() {
    let fake = Fake::new(0, 0);
    let worker = Arc::new(worker(&fake));
    let (release, hold) = mpsc::channel::<()>();
    let (started, running) = mpsc::channel();
    let first = {
        let worker = worker.clone();
        tokio::spawn(async move {
            worker
                .call(BUDGET, move |_| {
                    started.send(()).unwrap();
                    hold.recv().unwrap();
                })
                .await
        })
    };
    while running.try_recv().is_err() {
        tokio::task::yield_now().await;
    }
    // A request queues a write and is cancelled well inside its budget.
    let ran = Arc::new(AtomicBool::new(false));
    let flag = ran.clone();
    let cancelled = tokio::time::timeout(
        Duration::from_millis(50),
        worker.call(BUDGET, move |m| {
            flag.store(true, Ordering::SeqCst);
            m.set(offsets_of((125, 500)), true).is_ok()
        }),
    )
    .await;
    assert!(cancelled.is_err());
    release.send(()).unwrap();
    assert_eq!(first.await.unwrap(), Called::Done(()));
    // The queue is in order, so the next call runs after the withdrawn one
    // was skipped.
    assert_eq!(
        worker.call(BUDGET, |m| m.state()).await,
        Called::Done(State::Unknown)
    );
    assert!(!ran.load(Ordering::SeqCst));
    assert_eq!((fake.sets(), fake.current()), (0, (0, 0)));
}

#[tokio::test]
async fn a_call_that_panics_after_its_write_is_abandoned_and_the_worker_serves_on() {
    let fake = Fake::new(0, 0);
    let worker = worker(&fake);
    let result: Called<()> = worker
        .call(BUDGET, |m| {
            let _ = m.set(offsets_of((125, 500)), true);
            panic!("a caller bug after the NVML write");
        })
        .await;
    // The write reached the driver, so the caller must not read it as unsent.
    assert_eq!(result, Called::Abandoned);
    assert_eq!(fake.current(), (125, 500));
    // The completed write was recorded before the panic, and the worker runs on.
    assert_eq!(
        worker.call(BUDGET, |m| m.state()).await,
        Called::Done(State::Managed)
    );
}

#[tokio::test]
async fn a_panic_inside_a_submission_leaves_it_uncertain_and_ends_its_session() {
    let fake = Fake::new(0, 0);
    let worker = worker(&fake);
    worker.call(BUDGET, |m| m.set(DEFAULTS, false)).await;
    fake.with(|d| d.panic_on_set = true);
    let result = worker
        .call(BUDGET, |m| m.set(offsets_of((125, 500)), true).is_ok())
        .await;
    assert_eq!(result, Called::Abandoned);
    // The core offset reached the driver; nothing confirmed it.
    assert_eq!(fake.current(), (125, 0));
    // The caller hears of the panic as the job unwinds; the worker recovers
    // before it runs the next call.
    assert_eq!(
        worker
            .call(BUDGET, |m| (m.state(), m.has_confirmed_default_reset()))
            .await,
        Called::Done((State::SubmissionUncertain, false))
    );
    let (inits, shutdowns) = fake.with(|d| (d.inits, d.shutdowns));
    assert_eq!(shutdowns, inits);
    // The ungated read resolves what the driver holds.
    assert_eq!(
        worker.call(BUDGET, |m| m.read().map(|c| c.current())).await,
        Called::Done(Some(offsets_of((125, 0))))
    );
}

#[tokio::test]
async fn a_busy_worker_refuses_a_further_call_at_once() {
    let fake = Fake::new(0, 0);
    let worker = Arc::new(worker(&fake));
    let (release, hold) = mpsc::channel::<()>();
    let (started, running) = mpsc::channel();
    let first = {
        let worker = worker.clone();
        tokio::spawn(async move {
            worker
                .call(BUDGET, move |_| {
                    started.send(()).unwrap();
                    hold.recv().unwrap();
                })
                .await
        })
    };
    while running.try_recv().is_err() {
        tokio::task::yield_now().await;
    }
    let queued = {
        let worker = worker.clone();
        tokio::spawn(async move { worker.call(BUDGET, |m| m.state()).await })
    };
    // Let the queued call occupy the one waiting slot.
    for _ in 0..100 {
        tokio::task::yield_now().await;
    }
    let refused = worker
        .call(BUDGET, |m| m.set(DEFAULTS, false).is_ok())
        .await;
    assert_eq!(refused, Called::NotStarted);
    release.send(()).unwrap();
    assert_eq!(first.await.unwrap(), Called::Done(()));
    assert_eq!(queued.await.unwrap(), Called::Done(State::Unknown));
    assert_eq!(fake.sets(), 0);
}

#[test]
fn a_dropped_worker_starts_no_queued_call_and_writes_nothing() {
    let fake = Fake::new(100, 100);
    let ran = Arc::new(AtomicBool::new(false));
    let (release, hold) = mpsc::channel::<()>();
    let (started, running) = mpsc::channel();
    let worker = worker(&fake);
    // The first call blocks the worker and a second waits behind it when the
    // worker is dropped, as at Stop.
    let first: Run<Fake> = Box::new(move |_| {
        started.send(()).unwrap();
        hold.recv().unwrap();
    });
    worker
        .jobs
        .try_send((Arc::new(AtomicU8::new(QUEUED)), first))
        .unwrap();
    running.recv().unwrap();
    let flag = ran.clone();
    let (reply, queued) = mpsc::channel();
    let job: Run<Fake> = Box::new(move |m| {
        flag.store(true, Ordering::SeqCst);
        let _ = reply.send(m.set(DEFAULTS, false).is_ok());
    });
    worker
        .jobs
        .try_send((Arc::new(AtomicU8::new(QUEUED)), job))
        .unwrap();
    drop(worker);
    release.send(()).unwrap();
    // The worker exits without running the queued call.
    assert!(queued.recv_timeout(Duration::from_secs(5)).is_err());
    assert!(!ran.load(Ordering::SeqCst));
    assert_eq!(fake.sets(), 0);
    assert_eq!(fake.current(), (100, 100));
}

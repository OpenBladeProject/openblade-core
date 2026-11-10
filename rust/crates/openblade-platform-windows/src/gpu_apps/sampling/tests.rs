use super::*;
use std::{cell::Cell, collections::VecDeque, rc::Rc};

const ADAPTER: NvidiaAdapterLuid = NvidiaAdapterLuid {
    high: -1,
    low: 0x1234,
};
const NOW: i64 = 123_456;
const SID: &[u8] = b"current-user";

struct ScriptQuery(Rc<Cell<usize>>);

impl Drop for ScriptQuery {
    fn drop(&mut self) {
        self.0.set(self.0.get() + 1);
    }
}

#[derive(Default)]
struct Script {
    adapters: VecDeque<Result<NvidiaAdapterLuid, Unavailable>>,
    collect: VecDeque<Result<i64, Unavailable>>,
    arrays: VecDeque<Result<CounterArray, Unavailable>>,
    inspections: VecDeque<Inspection>,
    parents: VecDeque<BTreeMap<i32, i32>>,
    operations: Vec<String>,
    dropped: Rc<Cell<usize>>,
    cancel_on_pause: bool,
}

impl SamplingApi for Script {
    type Query = ScriptQuery;

    fn current_user(&mut self) -> Option<(Vec<u8>, u32)> {
        self.operations.push("user".into());
        Some((SID.to_vec(), 5))
    }

    fn locate_adapter(&mut self) -> Result<NvidiaAdapterLuid, Unavailable> {
        self.operations.push("adapter".into());
        self.adapters.pop_front().unwrap_or(Ok(ADAPTER))
    }

    fn open_query(&mut self) -> Result<ScriptQuery, Unavailable> {
        self.operations.push("open".into());
        Ok(ScriptQuery(Rc::clone(&self.dropped)))
    }

    fn collect(&mut self, _: &mut ScriptQuery) -> Result<i64, Unavailable> {
        self.operations.push("collect".into());
        self.collect.pop_front().unwrap_or(Ok(NOW))
    }

    fn counter_array(
        &mut self,
        _: &mut ScriptQuery,
        bytes: u32,
    ) -> Result<CounterArray, Unavailable> {
        self.operations.push(format!("array:{bytes}"));
        self.arrays.pop_front().unwrap_or(Ok(CounterArray::Size(0)))
    }

    fn inspect(&mut self, pid: i32) -> Inspection {
        self.operations.push(format!("pid:{pid}"));
        self.inspections
            .pop_front()
            .unwrap_or(Inspection::Unavailable)
    }

    fn read_parents(&mut self, _: &AtomicBool) -> BTreeMap<i32, i32> {
        self.parents.pop_front().unwrap_or_default()
    }

    fn pause(&mut self, interval: Duration, cancelled: &AtomicBool) {
        assert_eq!(interval, SAMPLE_INTERVAL);
        self.operations.push("pause".into());
        if self.cancel_on_pause {
            cancelled.store(true, AtomicOrdering::Relaxed);
        }
    }

    fn compare_names(&self, left: &str, right: &str) -> Ordering {
        left.to_uppercase().cmp(&right.to_uppercase())
    }
}

fn counter(pid: &str, luid: &str, percent: f64) -> EngineCounter {
    EngineCounter {
        instance: format!("PID_{pid}_{luid}_phys_0_eng_0_engtype_3D"),
        status: 0,
        utilization_percent: percent,
    }
}

fn record(created_filetime: i64, name: &str) -> Inspection {
    Inspection::Identified(Identity {
        created_filetime,
        user_sid: SID.to_vec(),
        session_id: 5,
        name: name.to_owned(),
    })
}

#[test]
fn links_the_native_entry_point_without_invoking_it() {
    let entry: fn(&AtomicBool) -> Result<GpuAppInventory, QueryUnavailable> =
        sample_windows_gpu_apps;
    std::hint::black_box(entry);
}

#[test]
fn primes_then_uses_two_stable_samples_and_only_inspects_matching_pids() {
    let luid = ADAPTER.counter_token().to_uppercase();
    let mut script = Script {
        arrays: [
            Ok(CounterArray::Size(0)),
            Ok(CounterArray::Size(128)),
            Ok(CounterArray::Items(vec![
                counter("42", &luid, 0.0),
                counter("42", &luid, 1.0),
                counter("5", "luid_0x00000000_0x00001234", 20.0),
                counter("+7", &luid, 60.0),
                counter("8", &luid, f64::NAN),
            ])),
            Ok(CounterArray::Size(64)),
            Ok(CounterArray::Items(vec![counter("42", &luid, 0.0)])),
        ]
        .into(),
        inspections: [record(3, "Browser"), record(3, "browser")].into(),
        ..Script::default()
    };
    let inventory = scan(&mut script, &AtomicBool::new(false)).unwrap();
    assert_eq!(inventory.apps.len(), 1);
    let app = &inventory.apps[0];
    assert_eq!(app.name, "browser");
    assert_eq!(app.start_time_utc_ticks, DOTNET_FILETIME_OFFSET + 3);
    assert_eq!(app.utilization_percent, 0.0);
    assert!(app.can_close());
    assert_eq!(
        script.operations,
        [
            "user",
            "adapter",
            "open",
            "collect",
            "array:0",
            "adapter",
            "pause",
            "collect",
            "array:0",
            "array:128",
            "pid:42",
            "adapter",
            "pause",
            "collect",
            "array:0",
            "array:64",
            "pid:42",
            "adapter",
        ]
    );
    assert_eq!(script.dropped.get(), 1);
}

#[test]
fn samples_the_shared_parent_chain_and_sums_gpu_helpers() {
    let luid = ADAPTER.counter_token();
    let frame = || {
        [
            Ok(CounterArray::Size(128)),
            Ok(CounterArray::Items(vec![
                counter("30", &luid, 4.5),
                counter("31", &luid, 2.0),
            ])),
        ]
    };
    let parents = || BTreeMap::from([(30, 20), (31, 20), (20, 10)]);
    let identities = || {
        [
            record(20, "browser"),
            record(20, "Browser"),
            record(10, "explorer"),
            record(31, "browser"),
        ]
    };
    let mut script = Script {
        arrays: [Ok(CounterArray::Size(0))]
            .into_iter()
            .chain(frame())
            .chain(frame())
            .collect(),
        parents: [parents(), parents()].into(),
        inspections: identities().into_iter().chain(identities()).collect(),
        ..Script::default()
    };
    let inventory = scan(&mut script, &AtomicBool::new(false)).unwrap();
    assert_eq!(
        inventory.apps,
        [GpuApp {
            process_id: 20,
            start_time_utc_ticks: DOTNET_FILETIME_OFFSET + 20,
            name: "Browser".into(),
            utilization_percent: 6.5,
            block_reason: None,
        }]
    );
    assert!(inventory.omissions.is_empty());
    assert_eq!(
        script
            .operations
            .iter()
            .filter(|op| op.starts_with("pid:"))
            .map(String::as_str)
            .collect::<Vec<_>>(),
        [
            "pid:30", "pid:20", "pid:10", "pid:31", "pid:30", "pid:20", "pid:10", "pid:31"
        ]
    );
}

#[test]
fn incomplete_or_ineligible_parent_leaves_the_gpu_helper_visible() {
    let luid = ADAPTER.counter_token();
    let parents = || BTreeMap::from([(30, 20)]);
    let parent_with_identity = |sid: &[u8], session_id| {
        Inspection::Identified(Identity {
            created_filetime: 20,
            user_sid: sid.to_vec(),
            session_id,
            name: "browser".into(),
        })
    };
    for (child, prior, latest) in [
        ("browser", None, Some(record(20, "browser"))),
        (
            "browser",
            Some(Inspection::Unavailable),
            Some(record(20, "browser")),
        ),
        (
            "browser",
            Some(record(20, "browser")),
            Some(Inspection::Stale),
        ),
        (
            "browser",
            Some(record(20, "browser")),
            Some(record(21, "browser")),
        ),
        (
            "browser",
            Some(record(31, "browser")),
            Some(record(31, "browser")),
        ),
        (
            "browser",
            Some(record(20, "terminal")),
            Some(record(20, "terminal")),
        ),
        ("dwm", Some(record(20, "DWM")), Some(record(20, "DWM"))),
        (
            "browser",
            Some(parent_with_identity(b"other-user", 5)),
            Some(parent_with_identity(b"other-user", 5)),
        ),
        (
            "browser",
            Some(parent_with_identity(SID, 6)),
            Some(parent_with_identity(SID, 6)),
        ),
    ] {
        let frame = || {
            [
                Ok(CounterArray::Size(64)),
                Ok(CounterArray::Items(vec![counter("30", &luid, 4.5)])),
            ]
        };
        let mut script = Script {
            arrays: [Ok(CounterArray::Size(0))]
                .into_iter()
                .chain(frame())
                .chain(frame())
                .collect(),
            parents: [
                prior.as_ref().map_or_else(BTreeMap::new, |_| parents()),
                latest.as_ref().map_or_else(BTreeMap::new, |_| parents()),
            ]
            .into(),
            inspections: [
                Some(record(30, child)),
                prior,
                Some(record(30, child)),
                latest,
            ]
            .into_iter()
            .flatten()
            .collect(),
            ..Script::default()
        };
        let inventory = scan(&mut script, &AtomicBool::new(false)).unwrap();
        assert_eq!(inventory.apps.len(), 1);
        assert_eq!(inventory.apps[0].process_id, 30);
        assert_eq!(inventory.apps[0].utilization_percent, 4.5);
        assert!(inventory.omissions.is_empty());
    }
}

#[test]
fn refresh_failure_and_mid_scan_adapter_change_are_distinct() {
    let mut unsupported = Script {
        adapters: [Err(Unavailable::UnsupportedAdapter)].into(),
        ..Script::default()
    };
    assert_eq!(
        scan(&mut unsupported, &AtomicBool::new(false)),
        Err(Unavailable::UnsupportedAdapter)
    );
    assert_eq!(unsupported.operations, ["user", "adapter"]);

    let mut changed = Script {
        adapters: [
            Ok(ADAPTER),
            Ok(ADAPTER),
            Ok(NvidiaAdapterLuid { low: 0, ..ADAPTER }),
        ]
        .into(),
        ..Script::default()
    };
    assert_eq!(
        scan(&mut changed, &AtomicBool::new(false)),
        Err(Unavailable::AdapterChanged)
    );
    assert_eq!(changed.dropped.get(), 1);
}

#[test]
fn empty_missing_malformed_and_failed_are_distinct() {
    let mut empty = Script::default();
    assert!(
        scan(&mut empty, &AtomicBool::new(false))
            .unwrap()
            .apps
            .is_empty()
    );
    for error in [
        Unavailable::MissingData,
        Unavailable::MalformedCounters,
        Unavailable::NativeCounter(6),
    ] {
        let mut script = Script {
            collect: (0..SAMPLE_ATTEMPTS).map(|_| Err(error)).collect(),
            ..Script::default()
        };
        assert_eq!(scan(&mut script, &AtomicBool::new(false)), Err(error));
        assert_eq!(
            script
                .operations
                .iter()
                .filter(|op| *op == "collect")
                .count(),
            SAMPLE_ATTEMPTS
        );
        assert_eq!(
            script.operations.iter().filter(|op| *op == "pause").count(),
            SAMPLE_ATTEMPTS - 1
        );
        assert_eq!(script.dropped.get(), 1);
    }
}

#[test]
fn short_fill_resizes_from_zero_once_and_caps_allocation() {
    let mut script = Script {
        arrays: [
            Ok(CounterArray::Size(128)),
            Ok(CounterArray::Resized(0x800007d2)),
            Ok(CounterArray::Size(256)),
            Ok(CounterArray::Items(Vec::new())),
            Ok(CounterArray::Size(128)),
            Ok(CounterArray::Resized(0x800007d2)),
            Ok(CounterArray::Size(256)),
            Ok(CounterArray::Resized(0x800007d2)),
            Ok(CounterArray::Size(MAX_COUNTER_BYTES + 1)),
        ]
        .into(),
        ..Script::default()
    };
    let mut query = script.open_query().unwrap();
    let cancelled = AtomicBool::new(false);
    assert!(
        read_counter_array(&mut script, &mut query, &cancelled)
            .unwrap()
            .is_empty()
    );
    assert_eq!(
        read_counter_array(&mut script, &mut query, &cancelled).err(),
        Some(Unavailable::NativeCounter(0x800007d2))
    );
    assert_eq!(
        read_counter_array(&mut script, &mut query, &cancelled).err(),
        Some(Unavailable::MalformedCounters)
    );
    assert_eq!(
        script.operations,
        [
            "open",
            "array:0",
            "array:128",
            "array:0",
            "array:256",
            "array:0",
            "array:128",
            "array:0",
            "array:256",
            "array:0",
        ]
    );
}

#[test]
fn rejected_identities_remain_omissions_instead_of_becoming_apps() {
    for (first, second, reason) in [
        (
            record(3, "browser"),
            record(NOW + 1, "browser"),
            OmissionReason::StaleIdentity,
        ),
        (
            record(3, "browser"),
            record(4, "browser"),
            OmissionReason::StaleIdentity,
        ),
        (
            record(3, "browser"),
            Inspection::Stale,
            OmissionReason::StaleIdentity,
        ),
        (
            record(3, "browser"),
            Inspection::Unavailable,
            OmissionReason::ActivityOnly,
        ),
    ] {
        let token = ADAPTER.counter_token();
        let mut script = Script {
            arrays: [
                Ok(CounterArray::Size(0)),
                Ok(CounterArray::Size(64)),
                Ok(CounterArray::Items(vec![counter("42", &token, 0.0)])),
                Ok(CounterArray::Size(64)),
                Ok(CounterArray::Items(vec![counter("42", &token, 60.0)])),
            ]
            .into(),
            inspections: [first, second].into(),
            ..Script::default()
        };
        let inventory = scan(&mut script, &AtomicBool::new(false)).unwrap();
        assert!(inventory.apps.is_empty());
        assert_eq!(inventory.omissions.get(&42), Some(&reason));
    }
}

#[test]
fn cancellation_stops_before_io_or_releases_the_query_after_wait() {
    let mut script = Script {
        cancel_on_pause: true,
        ..Script::default()
    };
    assert_eq!(
        scan(&mut script, &AtomicBool::new(true)),
        Err(Unavailable::Cancelled)
    );
    assert!(script.operations.is_empty());
    assert_eq!(
        scan(&mut script, &AtomicBool::new(false)),
        Err(Unavailable::Cancelled)
    );
    assert_eq!(
        script.operations,
        [
            "user", "adapter", "open", "collect", "array:0", "adapter", "pause"
        ]
    );
    assert_eq!(script.dropped.get(), 1);
}

#[test]
fn win32_image_name_discards_the_path_and_exe_extension() {
    for (path, name) in [
        (r"C:\Program Files\Browser.EXE", Some("Browser")),
        (r"C:\Example\renderer.worker.exe", Some("renderer.worker")),
        (r"C:\Example\游戏.exe", Some("游戏")),
        (r"C:\Example\ .exe", None),
        (r"C:\Example\", None),
    ] {
        assert_eq!(
            process_name(&path.encode_utf16().collect::<Vec<_>>()).as_deref(),
            name
        );
    }
    assert_eq!(process_name(&[0xd800]), None);
}

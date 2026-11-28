use super::*;
use std::collections::VecDeque;

const USER: CurrentUser<'static> = CurrentUser {
    sid: b"current",
    session_id: 5,
};
const ADAPTER: NvidiaAdapterLuid = NvidiaAdapterLuid {
    high: 0,
    low: 0xabcd,
};
const TOKEN: &str = "luid_0x00000000_0x0000abcd";

struct Frame {
    counters: Vec<EngineCounter>,
    processes: BTreeMap<i32, ProcessObservation<'static>>,
}

impl Frame {
    fn with_activities(activities: &[(i32, f64)]) -> Self {
        Self {
            counters: activities
                .iter()
                .map(|(pid, percent)| EngineCounter {
                    instance: format!("pid_{pid}_{TOKEN}_phys_0_eng_0"),
                    status: 0,
                    utilization_percent: *percent,
                })
                .collect(),
            processes: BTreeMap::new(),
        }
    }

    fn process(mut self, pid: i32, parent: Option<i32>, start: i64, name: &'static str) -> Self {
        self.processes.insert(
            pid,
            identified(parent, start, name, USER.sid, USER.session_id),
        );
        self
    }
}

struct Script {
    adapter: Option<NvidiaAdapterLuid>,
    pending: VecDeque<Option<Frame>>,
    frame: Option<Frame>,
    reads: usize,
    waits: Vec<Duration>,
}

impl Script {
    fn new(frames: impl IntoIterator<Item = Option<Frame>>) -> Self {
        Self {
            adapter: Some(ADAPTER),
            pending: frames.into_iter().collect(),
            frame: None,
            reads: 0,
            waits: Vec::new(),
        }
    }
}

impl GpuAppSource for Script {
    fn refresh(&mut self) -> Result<NvidiaAdapterLuid, QueryUnavailable> {
        self.adapter.ok_or(QueryUnavailable::Counters)
    }

    fn read(&mut self) -> Result<Sample<'_>, QueryUnavailable> {
        self.reads += 1;
        self.frame = self.pending.pop_front().flatten();
        self.frame
            .as_ref()
            .map(|frame| Sample {
                counters: &frame.counters,
                processes: frame.processes.clone(),
            })
            .ok_or(QueryUnavailable::Counters)
    }

    fn wait(&mut self, interval: Duration) -> Result<(), QueryUnavailable> {
        self.waits.push(interval);
        Ok(())
    }

    fn compare_names(&self, left: &str, right: &str) -> Ordering {
        // These managed oracle examples have ASCII process names.
        assert!(left.is_ascii() && right.is_ascii());
        left.bytes()
            .map(|b| b.to_ascii_uppercase())
            .cmp(right.bytes().map(|b| b.to_ascii_uppercase()))
    }
}

fn identified(
    parent_pid: Option<i32>,
    start_time_utc_ticks: i64,
    name: &'static str,
    user_sid: &'static [u8],
    session_id: u32,
) -> ProcessObservation<'static> {
    ProcessObservation::Identified {
        identity: ProcessIdentity {
            start_time_utc_ticks,
            user_sid,
            session_id,
            name,
        },
        parent_pid,
    }
}

fn prime() -> Option<Frame> {
    Some(Frame::with_activities(&[]))
}

fn inventory(before: Frame, after: Frame) -> GpuAppInventory {
    query_gpu_apps(
        &mut Script::new([prime(), Some(before), Some(after)]),
        &USER,
    )
    .unwrap()
}

#[test]
fn counter_instances_match_managed_examples_and_only_valid_engines_contribute() {
    // WindowsGpuActivityTests.InstanceParser_* plus counter status/value rules.
    for (instance, expected) in [
        (
            "pid_42_luid_0x00000000_0x0000abcd_phys_0_eng_0_engtype_3D",
            Some(42),
        ),
        (
            "PID_987_LUID_0X00000000_0X0000ABCD_phys_0_eng_1_engtype_Copy",
            Some(987),
        ),
        ("pid_0_luid_0x00000000_0x0000abcd_phys_0_eng_0", None),
        ("pid_bad_luid_0x00000000_0x0000abcd_phys_0_eng_0", None),
        ("pid_42_luid_0x00000000_0x00001234_phys_0_eng_0", None),
        ("luid_0x00000000_0x0000abcd_pid_42_phys_0_eng_0", None),
        ("pid_+42_luid_0x00000000_0x0000abcd_phys_0_eng_0", None),
        (
            "pid_2147483648_luid_0x00000000_0x0000abcd_phys_0_eng_0",
            None,
        ),
    ] {
        assert_eq!(nvidia_pid(instance, TOKEN), expected, "{instance}");
    }
    let before = Frame::with_activities(&[(42, 3.5)]).process(42, None, 10, "browser");
    let mut after = Frame::with_activities(&[(42, 95.0)]).process(42, None, 10, "browser");
    after.counters.extend(
        [
            (1, 7.5),
            (0, -7.0),
            (0, f64::NAN),
            (0, f64::INFINITY),
            (2, 120.0),
        ]
        .map(|(status, value)| EngineCounter {
            instance: format!("pid_42_{TOKEN}_phys_0_eng_1"),
            status,
            utilization_percent: value,
        }),
    );
    let result = inventory(before, after);
    assert_eq!(result.apps.len(), 1);
    assert_eq!(result.apps[0].utilization_percent, 102.5);
}

#[test]
fn sampling_retries_and_resets_prime_and_pair_after_unavailable() {
    // WindowsGpuActivityTests.ActivitySampling_{RetriesTransientStartupFailure,UnavailableRead}*.
    let observed = |value| Frame::with_activities(&[(42, value)]).process(42, None, 10, "browser");
    let mut source = Script::new([
        None,
        prime(),
        Some(observed(3.5)),
        None,
        prime(),
        Some(observed(3.5)),
        Some(observed(2.5)),
        Some(observed(8.5)),
    ]);
    let result = query_gpu_apps(&mut source, &USER).unwrap();
    assert_eq!(result.apps[0].utilization_percent, 2.5);
    assert_eq!(source.reads, 7);
    assert_eq!(source.waits, [Duration::from_millis(150); 6]);
}

#[test]
fn unavailable_and_available_empty_are_distinct() {
    let mut refresh_failed = Script::new([prime(), prime(), prime()]);
    refresh_failed.adapter = None;
    assert_eq!(
        query_gpu_apps(&mut refresh_failed, &USER),
        Err(QueryUnavailable::Counters)
    );
    assert_eq!(refresh_failed.reads, 0);
    let mut never_stable = Script::new(std::iter::repeat_with(|| None).take(21));
    assert_eq!(
        query_gpu_apps(&mut never_stable, &USER),
        Err(QueryUnavailable::Counters)
    );
    assert_eq!(never_stable.reads, 20);
    assert_eq!(never_stable.waits.len(), 19);

    let empty = query_gpu_apps(&mut Script::new([prime(), prime(), prime()]), &USER).unwrap();
    assert_eq!(
        empty,
        GpuAppInventory {
            apps: vec![],
            truncated: false,
            omissions: BTreeMap::new(),
        }
    );
}

#[test]
fn stable_intersection_keeps_idle_holders_and_omits_transient_pids() {
    // WindowsGpuActivityTests.StableProcessIntersection_*.
    let before = Frame::with_activities(&[(40, 8.5), (41, 0.0), (44, 3.0)])
        .process(40, None, 10, "browser")
        .process(41, None, 11, "browser")
        .process(44, None, 12, "browser");
    let after = Frame::with_activities(&[(41, 0.0), (40, 0.0), (45, 4.0)])
        .process(40, None, 10, "browser")
        .process(41, None, 11, "Browser")
        .process(45, None, 13, "browser");
    let result = inventory(before, after);
    assert_eq!(
        result
            .apps
            .iter()
            .map(|app| app.process_id)
            .collect::<Vec<_>>(),
        [40, 41]
    );
    assert!(
        result
            .apps
            .iter()
            .all(|app| app.utilization_percent == 0.0 && app.can_close())
    );
    assert!(result.omissions.is_empty());
}

#[test]
fn stable_pid_without_stable_identity_never_becomes_an_app() {
    let activity = [
        (40, 1.0),
        (41, 1.0),
        (42, 1.0),
        (43, 1.0),
        (44, 1.0),
        (45, 1.0),
    ];
    let before = Frame::with_activities(&activity)
        .process(41, None, 11, "browser")
        .process(42, None, 12, "browser")
        .process(43, None, 13, "browser")
        .process(44, None, 14, "browser")
        .process(45, None, 15, "browser");
    let mut after = Frame::with_activities(&activity)
        .process(40, None, 10, "browser")
        .process(41, None, 21, "browser");
    after.processes.extend([
        (42, identified(None, 12, "browser", b"other", 5)),
        (43, identified(None, 13, "browser", USER.sid, 6)),
        (44, ProcessObservation::Stale),
        (45, identified(None, 15, "browser", b"", 5)),
    ]);
    let result = inventory(before, after);
    assert!(result.apps.is_empty());
    assert_eq!(
        result.omissions,
        BTreeMap::from([
            (40, OmissionReason::ActivityOnly),
            (41, OmissionReason::StaleIdentity),
            (42, OmissionReason::StaleIdentity),
            (43, OmissionReason::StaleIdentity),
            (44, OmissionReason::StaleIdentity),
            (45, OmissionReason::ActivityOnly),
        ])
    );
}

#[test]
fn groups_same_name_browser_helper_without_climbing_to_explorer() {
    // WindowsGpuActivityTests.AppProjection_UsesPopulatedParentChainForGpuHelper.
    let frame = || {
        Frame::with_activities(&[(20, 2.0), (30, 4.5), (31, 1.0)])
            .process(10, None, 5, "explorer")
            .process(20, Some(10), 10, "Browser")
            .process(30, Some(20), 30, "browser")
            .process(31, Some(30), 31, "browser")
    };
    let result = inventory(frame(), frame());
    assert_eq!(
        result.apps,
        [GpuApp {
            process_id: 20,
            start_time_utc_ticks: 10,
            name: "Browser".to_owned(),
            utilization_percent: 7.5,
            block_reason: None,
        }]
    );
}

#[test]
fn parent_walk_requires_a_stable_same_user_session_older_same_name_parent() {
    for (before_parent, after_parent) in [
        // Managed fixtures cover unrelated names, other users and reused newer parents.
        (
            identified(None, 10, "terminal", USER.sid, 5),
            identified(None, 10, "terminal", USER.sid, 5),
        ),
        (
            identified(None, 10, "browser", b"other", 5),
            identified(None, 10, "browser", b"other", 5),
        ),
        (
            identified(None, 10, "browser", USER.sid, 6),
            identified(None, 10, "browser", USER.sid, 6),
        ),
        (
            identified(None, 30, "browser", USER.sid, 5),
            identified(None, 30, "browser", USER.sid, 5),
        ),
        (
            identified(None, 10, "browser", USER.sid, 5),
            identified(None, 11, "browser", USER.sid, 5),
        ),
    ] {
        let frame = || Frame::with_activities(&[(20, 4.5)]).process(20, Some(10), 20, "browser");
        let mut before = frame();
        before.processes.insert(10, before_parent);
        let mut after = frame();
        after.processes.insert(10, after_parent);
        assert_eq!(inventory(before, after).apps[0].process_id, 20);
    }
}

#[test]
fn protection_ignore_rules_and_dispatcher_order_are_preserved() {
    // ClosePolicy_ProtectsOpenBladeAndWindowsShellProcesses,
    // AppProjection_OmitsWhitelistedAmbientProcesses and Query_MapsEligibleAndProtectedApps.
    let frame = || {
        let mut frame = Frame::with_activities(&[
            (10, 100.0),
            (11, 100.0),
            (12, 100.0),
            (13, 100.0),
            (14, 100.0),
            (15, 1.0),
            (16, 2.0),
            (17, 2.0),
            (18, 2.0),
            (19, 80.0),
        ])
        .process(10, None, 10, "Explorer")
        .process(11, None, 11, "NVIDIA Overlay")
        .process(12, None, 12, "oPENbLADE.SessionAgent")
        .process(15, None, 15, "Zed")
        .process(16, None, 16, "zed")
        .process(17, None, 17, "alpha")
        .process(18, None, 18, "Alpha")
        .process(19, None, 19, "dwm");
        frame.processes.extend([
            (13, identified(None, 13, "winlogon", b"other", 6)),
            (14, identified(None, 14, "searchhost", b"other", 5)),
        ]);
        frame
    };
    let result = inventory(frame(), frame());
    assert_eq!(
        result
            .apps
            .iter()
            .map(|app| (app.process_id, app.block_reason))
            .collect::<Vec<_>>(),
        [
            (17, None),
            (18, None),
            (16, None),
            (15, None),
            (12, Some(BlockReason::Protected)),
            (14, Some(BlockReason::OtherAccount)),
            (13, Some(BlockReason::OtherSession)),
            (19, Some(BlockReason::Protected)),
        ]
    );
    assert_eq!(
        BlockReason::OtherSession.message(),
        "The process belongs to another Windows session."
    );
    assert_eq!(
        BlockReason::OtherAccount.message(),
        "The process belongs to another Windows account."
    );
    assert_eq!(
        BlockReason::Protected.message(),
        "OpenBlade does not close Windows or OpenBlade processes."
    );
}

#[test]
fn dispatcher_cap_applies_after_ordering_and_only_marks_a_real_truncation() {
    for count in [128, 129] {
        let frame = || {
            let activity: Vec<_> = (1..=count).map(|pid| (pid, f64::from(pid))).collect();
            let mut frame = Frame::with_activities(&activity);
            for pid in 1..=count {
                frame = frame.process(pid, None, i64::from(pid), "app");
            }
            frame
        };
        let result = inventory(frame(), frame());
        assert_eq!(result.truncated, count == 129);
        assert_eq!(result.apps.len(), 128);
        assert_eq!(result.apps[0].process_id, count);
        assert_eq!(result.apps[127].process_id, count - 127);
    }
}

#[test]
fn counter_overflow_does_not_create_an_invalid_ipc_row() {
    let frame = || {
        Frame::with_activities(&[(42, f64::MAX), (42, f64::MAX)]).process(42, None, 10, "browser")
    };
    let result = inventory(frame(), frame());
    assert!(result.apps.is_empty());
    assert_eq!(
        result.omissions,
        BTreeMap::from([(42, OmissionReason::InvalidUtilization)])
    );
}

#[test]
fn unavailable_current_user_and_invalid_process_names_fail_closed() {
    let mut source = Script::new([prime(), prime(), prime()]);
    let user = CurrentUser {
        sid: b"",
        session_id: USER.session_id,
    };
    assert_eq!(
        query_gpu_apps(&mut source, &user),
        Err(QueryUnavailable::CurrentUser)
    );
    assert_eq!(source.reads, 0);

    let frame = || Frame::with_activities(&[(42, 1.0)]).process(42, None, 10, " browser");
    assert_eq!(
        inventory(frame(), frame()).omissions,
        BTreeMap::from([(42, OmissionReason::ActivityOnly)])
    );
}

#[test]
fn changed_parent_link_or_foreign_child_does_not_transfer_activity() {
    let frame = || {
        Frame::with_activities(&[(30, 4.5)])
            .process(20, None, 10, "browser")
            .process(30, Some(20), 30, "browser")
    };
    let before = frame().process(30, None, 30, "browser");
    assert_eq!(inventory(before, frame()).apps[0].process_id, 30);

    let foreign = || {
        let mut frame = frame();
        frame
            .processes
            .insert(30, identified(Some(20), 30, "browser", b"other", 5));
        frame
    };
    let result = inventory(foreign(), foreign());
    assert_eq!(result.apps[0].process_id, 30);
    assert_eq!(result.apps[0].block_reason, Some(BlockReason::OtherAccount));
}

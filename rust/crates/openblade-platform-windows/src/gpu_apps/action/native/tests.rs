//! The retained-handle policy over a scripted process table. No process is
//! opened, enumerated, signalled or closed.
use super::super::{ACTION_FAILED, CHANGED, NOT_ATTEMPTED, STOPPED, UNCERTAIN};
use super::*;
use crate::gpu_apps::GpuApp;
use std::{
    collections::{BTreeMap, BTreeSet},
    rc::Rc,
    sync::Arc,
};

const USER: CurrentUser<'static> = CurrentUser {
    sid: b"user",
    session_id: 9,
};
const NO_WINDOW: &str = "The application has no window that can be closed gracefully.";
const MEDIUM: u32 = 0x2000;
const HIGH: u32 = 0x3000;

#[derive(Clone, Debug, PartialEq, Eq)]
enum Event {
    Open(i32, bool),
    Release(i32),
    Terminate(i32),
    Wait(i32),
    Children(i32),
    Window(i32),
}

#[derive(Clone)]
struct Spec {
    parent: i32,
    created: i64,
    sid: &'static [u8],
    session: u32,
    image: &'static str,
    exited: bool,
}

fn spec(parent: i32, created: i64, image: &'static str) -> Spec {
    Spec {
        parent,
        created,
        sid: USER.sid,
        session: USER.session_id,
        image,
        exited: false,
    }
}

struct Script {
    table: BTreeMap<i32, Spec>,
    denied: BTreeSet<i32>,
    unreadable: BTreeSet<i32>,
    terminate: BTreeMap<i32, Terminated>,
    windows: BTreeMap<i32, WindowClose>,
    exit: Exit,
    current: i32,
    cancel_on: Option<(Event, Arc<AtomicBool>)>,
    events: Rc<RefCell<Vec<Event>>>,
    live: Rc<Cell<usize>>,
    /// Integrity RIDs by PID; absent is medium, `None` is unreadable.
    integrity: BTreeMap<i32, Option<u32>>,
    current_integrity: Option<u32>,
    /// Once no handle to `.0` is held after its termination, the PID passes
    /// to `.1`, which has already started the unrelated child `.2`.
    recycle: Option<(i32, Spec, (i32, Spec))>,
}

/// The confirmed app 41 (with a verified helper 50 and its worker 60), the
/// second app 42, and three children of 41 that a tree kill must skip.
fn fixture() -> Script {
    Script {
        table: BTreeMap::from([
            (800, spec(0, 10, r"C:\Windows\explorer.exe")),
            (900, spec(800, 500, r"C:\Program Files\OpenBlade\agent.exe")),
            (41, spec(800, 1000, r"C:\Games\GAME.exe")),
            (42, spec(800, 1050, r"C:\Tools\other.exe")),
            (50, spec(41, 1100, r"C:\Games\helper.exe")),
            (60, spec(50, 1400, r"C:\Games\worker.exe")),
            // A PID reused after its creator: created before the target.
            (51, spec(41, 900, r"C:\Games\stale.exe")),
            (
                52,
                Spec {
                    sid: b"other",
                    ..spec(41, 1200, r"C:\Games\elevated.exe")
                },
            ),
            (53, spec(41, 1300, r"C:\Windows\csrss.exe")),
        ]),
        denied: BTreeSet::new(),
        unreadable: BTreeSet::new(),
        terminate: BTreeMap::new(),
        windows: BTreeMap::new(),
        exit: Exit::Exited,
        current: 900,
        cancel_on: None,
        events: Rc::default(),
        live: Rc::default(),
        integrity: BTreeMap::new(),
        current_integrity: Some(MEDIUM),
        recycle: None,
    }
}

struct Handle {
    process_id: i32,
    live: Rc<Cell<usize>>,
    events: Rc<RefCell<Vec<Event>>>,
}

impl Drop for Handle {
    fn drop(&mut self) {
        self.live.set(self.live.get() - 1);
        self.events
            .borrow_mut()
            .push(Event::Release(self.process_id));
    }
}

impl Script {
    fn log(&self, event: Event) {
        if let Some((trigger, cancelled)) = &self.cancel_on
            && *trigger == event
        {
            cancelled.store(true, Relaxed);
        }
        self.events.borrow_mut().push(event);
    }
}

impl ProcessApi for Script {
    type Process = Handle;

    fn open(&mut self, process_id: i32, terminate: bool) -> Result<Option<Handle>, ()> {
        self.log(Event::Open(process_id, terminate));
        if self.denied.contains(&process_id) {
            return Err(());
        }
        Ok(self.table.contains_key(&process_id).then(|| {
            self.live.set(self.live.get() + 1);
            Handle {
                process_id,
                events: Rc::clone(&self.events),
                live: Rc::clone(&self.live),
            }
        }))
    }

    fn identity(&mut self, process: &Handle) -> Option<Held> {
        if self.unreadable.contains(&process.process_id) {
            return None;
        }
        let spec = self.table.get(&process.process_id)?;
        if spec.exited {
            return Some(Held::exited());
        }
        Some(Held {
            process_id: process.process_id,
            created_filetime: spec.created,
            user_sid: spec.sid.to_vec(),
            session_id: spec.session,
            image_path: spec.image.into(),
            parent_process_id: spec.parent,
            exited: false,
        })
    }

    fn terminate(&mut self, process: &Handle) -> Terminated {
        self.log(Event::Terminate(process.process_id));
        self.terminate
            .get(&process.process_id)
            .copied()
            .unwrap_or(Terminated::Submitted)
    }

    fn wait_exit(&mut self, process: &Handle, timeout: Duration, cancelled: &AtomicBool) -> Exit {
        assert_eq!(timeout, EXIT_CONFIRMATION);
        self.log(Event::Wait(process.process_id));
        if cancelled.load(Relaxed) {
            Exit::Unconfirmed
        } else {
            self.exit
        }
    }

    fn child_ids(&mut self, parent: i32) -> Vec<i32> {
        self.log(Event::Children(parent));
        // Windows frees a process ID only when no handle to the process is left.
        if let Some((pid, _, _)) = &self.recycle {
            let pid = *pid;
            let events = self.events.borrow();
            let opened = events
                .iter()
                .filter(|event| matches!(event, Event::Open(p, _) if *p == pid))
                .count();
            let released = events.iter().filter(|e| **e == Event::Release(pid)).count();
            let terminated = events.contains(&Event::Terminate(pid));
            drop(events);
            if terminated && opened > 0 && opened == released {
                let (pid, occupant, (child_pid, child)) = self.recycle.take().unwrap();
                self.table.insert(pid, occupant);
                self.table.insert(child_pid, child);
            }
        }
        self.table
            .iter()
            .filter(|(_, spec)| spec.parent == parent)
            .map(|(&process_id, _)| process_id)
            .collect()
    }

    fn close_main_window(&mut self, process: &Handle, process_id: i32) -> WindowClose {
        assert_eq!(process.process_id, process_id, "acts on the held process");
        self.log(Event::Window(process_id));
        self.windows
            .get(&process_id)
            .copied()
            .unwrap_or(WindowClose::Posted)
    }

    fn current_process_id(&self) -> i32 {
        self.current
    }

    fn integrity(&mut self, process: &Handle) -> Option<u32> {
        self.integrity
            .get(&process.process_id)
            .copied()
            .unwrap_or(Some(MEDIUM))
    }

    fn current_integrity(&mut self) -> Option<u32> {
        self.current_integrity
    }
}

fn ticks(created: i64) -> i64 {
    dotnet_ticks(created).unwrap()
}

/// Confirmed rows: (PID, scan creation time, name).
fn run_with(
    script: Script,
    rows: &[(i32, i64, &str)],
    kind: Kind,
    cancelled: &AtomicBool,
    comparison_failed: bool,
) -> (Report, Vec<Event>) {
    let events = Rc::clone(&script.events);
    let live = Rc::clone(&script.live);
    let inventory = GpuAppInventory {
        apps: rows
            .iter()
            .map(|&(process_id, created, name)| GpuApp {
                process_id,
                start_time_utc_ticks: ticks(created),
                name: name.into(),
                utilization_percent: 1.0,
                block_reason: None,
            })
            .collect(),
        truncated: false,
        omissions: Default::default(),
    };
    let targets: Vec<_> = rows
        .iter()
        .map(|&(process_id, created, name)| Target {
            process_id,
            start_time_utc_ticks: ticks(created),
            name: name.into(),
        })
        .collect();
    let compare =
        |left: &str, right: &str| left.to_ascii_uppercase().cmp(&right.to_ascii_uppercase());
    let failed = Cell::new(comparison_failed);
    let report = close(
        &inventory,
        &targets,
        Context {
            api: RefCell::new(script),
            user: &USER,
            compare: &compare,
            comparison_failed: &failed,
            cancelled,
            kind,
        },
    );
    let events = events.borrow().clone();
    assert_eq!(
        live.get(),
        0,
        "every retained handle is released: {events:?}"
    );
    (report, events)
}

fn run(script: Script, rows: &[(i32, i64, &str)], kind: Kind) -> (Report, Vec<Event>) {
    run_with(script, rows, kind, &AtomicBool::new(false), false)
}

/// The OS actions and tree enumeration, without opens and releases.
fn actions(events: &[Event]) -> Vec<Event> {
    events
        .iter()
        .filter(|event| !matches!(event, Event::Open(..) | Event::Release(_)))
        .cloned()
        .collect()
}

fn errors(report: &Report) -> Vec<(i32, Option<&'static str>)> {
    report
        .results
        .iter()
        .map(|result| (result.process_id, result.error))
        .collect()
}

const GAME: (i32, i64, &str) = (41, 1000, "Game");
const OTHER: (i32, i64, &str) = (42, 1050, "other");

#[test]
fn graceful_close_posts_through_the_retained_owner_only() {
    let (report, events) = run(fixture(), &[GAME], Kind::Close);
    assert_eq!(report.error, None);
    assert_eq!(errors(&report), [(41, None)]);
    assert_eq!(actions(&events), [Event::Window(41)]);
    // Graceful close asks for no terminate right; the owner parent is read.
    assert_eq!(events[0], Event::Open(41, false));
    assert!(events.contains(&Event::Open(800, false)));
    assert_eq!(events.last(), Some(&Event::Release(41)));
}

#[test]
fn force_close_confirms_exit_then_terminates_only_verified_descendants() {
    let (report, events) = run(fixture(), &[GAME], Kind::ForceClose);
    assert_eq!(report.error, None);
    assert_eq!(errors(&report), [(41, None)]);
    assert_eq!(events[0], Event::Open(41, true));
    // The reused PID 51, foreign-account 52 and protected 53 are skipped.
    assert_eq!(
        actions(&events),
        [
            Event::Terminate(41),
            Event::Wait(41),
            Event::Children(41),
            Event::Terminate(50),
            Event::Children(50),
            Event::Terminate(60),
            Event::Children(60),
        ]
    );
}

#[test]
fn unconfirmed_or_interrupted_exit_is_uncertain_and_ends_the_tree() {
    for interrupt in [false, true] {
        let cancelled = Arc::new(AtomicBool::new(false));
        let mut script = fixture();
        if interrupt {
            // Stop arrives while the submitted termination is unconfirmed.
            script.cancel_on = Some((Event::Wait(41), Arc::clone(&cancelled)));
        } else {
            script.exit = Exit::Unconfirmed;
        }
        let (report, events) = run_with(script, &[GAME], Kind::ForceClose, &cancelled, false);
        assert_eq!(report.error, Some(UNCERTAIN));
        assert_eq!(errors(&report), [(41, Some(UNCERTAIN))]);
        assert_eq!(actions(&events), [Event::Terminate(41), Event::Wait(41)]);
    }
}

#[test]
fn refused_termination_is_reported_once_without_fallback_or_tree() {
    let mut script = fixture();
    script.terminate.insert(41, Terminated::Failed);
    let (report, events) = run(script, &[GAME], Kind::ForceClose);
    assert_ne!(report.error, Some(UNCERTAIN));
    assert_eq!(errors(&report), [(41, Some(ACTION_FAILED))]);
    assert_eq!(actions(&events), [Event::Terminate(41)]);
}

#[test]
fn termination_refused_after_exit_is_managed_success() {
    let mut script = fixture();
    script.terminate.insert(41, Terminated::Exited);
    let (report, events) = run(script, &[GAME], Kind::ForceClose);
    assert_eq!(errors(&report), [(41, None)]);
    assert_eq!(
        actions(&events)[..2],
        [Event::Terminate(41), Event::Children(41)]
    );
}

#[test]
fn agent_inside_the_tree_limits_force_close_to_the_target() {
    let mut script = fixture();
    script
        .table
        .insert(70, spec(50, 1500, r"C:\OpenBlade\agent.exe"));
    script.current = 70;
    let (report, events) = run(script, &[GAME], Kind::ForceClose);
    assert_eq!(errors(&report), [(41, None)]);
    assert_eq!(actions(&events), [Event::Terminate(41), Event::Wait(41)]);

    // An unreadable Agent identity is treated the same way.
    let mut script = fixture();
    script.unreadable.insert(900);
    let (_, events) = run(script, &[GAME], Kind::ForceClose);
    assert_eq!(actions(&events), [Event::Terminate(41), Event::Wait(41)]);
}

#[test]
fn identity_change_between_scan_and_close_blocks_both_actions() {
    for kind in [Kind::Close, Kind::ForceClose] {
        let mut changed = vec![];
        // PID reuse (new creation time), another session, another account,
        // a renamed image and exit, each read on the retained handle.
        let mut reused = fixture();
        reused.table.get_mut(&41).unwrap().created = 2000;
        changed.push(reused);
        let mut session = fixture();
        session.table.get_mut(&41).unwrap().session = 10;
        changed.push(session);
        let mut account = fixture();
        account.table.get_mut(&41).unwrap().sid = b"other";
        changed.push(account);
        let mut renamed = fixture();
        renamed.table.get_mut(&41).unwrap().image = r"C:\Games\renamed.exe";
        changed.push(renamed);
        let mut exited = fixture();
        exited.table.get_mut(&41).unwrap().exited = true;
        changed.push(exited);
        let mut missing = fixture();
        missing.table.remove(&41);
        changed.push(missing);
        for script in changed {
            let (report, events) = run(script, &[GAME], kind);
            assert_eq!(errors(&report), [(41, Some(CHANGED))], "{events:?}");
            assert_eq!(
                report.error,
                Some("The selected applications could not be closed.")
            );
            assert!(actions(&events).is_empty(), "{events:?}");
        }
    }
}

#[test]
fn unreadable_or_denied_target_is_a_definite_failure_before_any_action() {
    for denied in [true, false] {
        let mut script = fixture();
        if denied {
            script.denied.insert(41);
        } else {
            script.unreadable.insert(41);
        }
        let (report, events) = run(script, &[GAME], Kind::ForceClose);
        assert_eq!(errors(&report), [(41, Some(ACTION_FAILED))]);
        assert!(actions(&events).is_empty());
    }
}

#[test]
fn a_same_name_parent_owns_the_process_and_makes_the_row_stale() {
    let mut script = fixture();
    script
        .table
        .insert(40, spec(800, 900, r"C:\Games\game.exe"));
    script.table.get_mut(&41).unwrap().parent = 40;
    let (report, events) = run(script, &[GAME], Kind::Close);
    assert_eq!(errors(&report), [(41, Some(CHANGED))]);
    assert!(actions(&events).is_empty());

    // A later, foreign-account or differently named parent does not own it.
    for parent in [
        spec(800, 1001, r"C:\Games\game.exe"),
        Spec {
            sid: b"other",
            ..spec(800, 900, r"C:\Games\game.exe")
        },
        spec(800, 900, r"C:\Games\launcher.exe"),
    ] {
        let mut script = fixture();
        script.table.insert(40, parent);
        script.table.get_mut(&41).unwrap().parent = 40;
        let (report, events) = run(script, &[GAME], Kind::Close);
        assert_eq!(errors(&report), [(41, None)]);
        assert_eq!(actions(&events), [Event::Window(41)]);
    }
}

#[test]
fn failed_name_comparison_refuses_submission() {
    for kind in [Kind::Close, Kind::ForceClose] {
        let (report, events) = run_with(fixture(), &[GAME], kind, &AtomicBool::new(false), true);
        assert_eq!(errors(&report), [(41, Some(ACTION_FAILED))]);
        assert!(actions(&events).is_empty());
    }
}

#[test]
fn graceful_close_maps_missing_window_and_failed_post() {
    for (outcome, expected) in [
        (WindowClose::NoWindow, NO_WINDOW),
        (WindowClose::Failed, ACTION_FAILED),
    ] {
        let mut script = fixture();
        script.windows.insert(41, outcome);
        let (report, _) = run(script, &[GAME], Kind::Close);
        assert_eq!(errors(&report), [(41, Some(expected))]);
        assert_ne!(report.error, Some(UNCERTAIN));
    }
}

#[test]
fn partial_success_and_stop_between_targets() {
    let mut script = fixture();
    script.windows.insert(42, WindowClose::NoWindow);
    let (report, _) = run(script, &[GAME, OTHER], Kind::Close);
    assert_eq!(report.error, Some("Some applications could not be closed."));
    assert_eq!(errors(&report), [(41, None), (42, Some(NO_WINDOW))]);

    // Stop after the first submission: the second target is not opened.
    let cancelled = Arc::new(AtomicBool::new(false));
    let mut script = fixture();
    script.cancel_on = Some((Event::Window(41), Arc::clone(&cancelled)));
    let (report, events) = run_with(script, &[GAME, OTHER], Kind::Close, &cancelled, false);
    assert_eq!(report.error, Some(STOPPED));
    assert_eq!(errors(&report), [(41, None), (42, Some(NOT_ATTEMPTED))]);
    assert!(!events.contains(&Event::Open(42, false)));
}

#[test]
fn stop_during_the_tree_leaves_the_confirmed_target_result() {
    let cancelled = Arc::new(AtomicBool::new(false));
    let mut script = fixture();
    script.cancel_on = Some((Event::Terminate(50), Arc::clone(&cancelled)));
    let (report, events) = run_with(script, &[GAME], Kind::ForceClose, &cancelled, false);
    assert_eq!(errors(&report), [(41, None)]);
    assert!(!events.contains(&Event::Terminate(60)));
}

/// Each descendant stays held until its own children have been listed, as
/// managed `KillTree` holds every child until its subtree is done.
#[test]
fn a_descendant_stays_held_until_its_children_are_enumerated() {
    let (_, events) = run(fixture(), &[GAME], Kind::ForceClose);
    let position = |event: Event| events.iter().position(|e| *e == event).unwrap();
    assert!(position(Event::Terminate(50)) < position(Event::Children(50)));
    assert!(position(Event::Children(50)) < position(Event::Release(50)));
    assert!(position(Event::Release(41)) > position(Event::Children(60)));
    // No PID is released before its children were listed.
    for (index, event) in events.iter().enumerate() {
        if let Event::Children(parent) = event {
            assert!(
                !events[..index].contains(&Event::Release(*parent)),
                "{parent} released before its children were listed: {events:?}"
            );
        }
    }
}

/// A recycled descendant PID cannot hand the walk an unrelated process: the
/// PID stays pinned while its children are listed, and the later owner's
/// child is never reached.
#[test]
fn a_recycled_descendant_pid_never_reaches_an_unrelated_process() {
    let mut script = fixture();
    script.recycle = Some((
        50,
        spec(800, 2000, r"C:\Unrelated\editor.exe"),
        (61, spec(50, 2100, r"C:\Unrelated\document.exe")),
    ));
    let (report, events) = run(script, &[GAME], Kind::ForceClose);
    assert_eq!(errors(&report), [(41, None)]);
    assert!(events.contains(&Event::Terminate(60)), "{events:?}");
    assert!(!events.contains(&Event::Terminate(61)), "{events:?}");
}

/// Managed cannot open a process above its own integrity level: the root is
/// refused before any post or termination and a descendant is skipped with its
/// subtree. An unreadable level is a refusal too.
#[test]
fn both_actions_refuse_higher_or_unreadable_integrity() {
    for level in [Some(HIGH), None] {
        let mut script = fixture();
        script.integrity.insert(41, level);
        let (report, events) = run(script, &[GAME], Kind::ForceClose);
        assert_eq!(errors(&report), [(41, Some(ACTION_FAILED))], "{level:?}");
        assert_ne!(report.error, Some(UNCERTAIN));
        assert!(actions(&events).is_empty(), "{events:?}");
    }
    let mut script = fixture();
    script.current_integrity = None;
    let (report, events) = run(script, &[GAME], Kind::ForceClose);
    assert_eq!(errors(&report), [(41, Some(ACTION_FAILED))]);
    assert!(actions(&events).is_empty());

    for level in [Some(HIGH), None] {
        let mut script = fixture();
        script.integrity.insert(50, level);
        let (report, events) = run(script, &[GAME], Kind::ForceClose);
        assert_eq!(errors(&report), [(41, None)]);
        assert_eq!(
            actions(&events),
            [Event::Terminate(41), Event::Wait(41), Event::Children(41)],
            "{level:?}"
        );
    }

    // An Agent running elevated may act at its own level.
    let mut script = fixture();
    script.current_integrity = Some(HIGH);
    script.integrity.insert(41, Some(HIGH));
    let (report, _) = run(script, &[GAME], Kind::ForceClose);
    assert_eq!(errors(&report), [(41, None)]);

    // Graceful close is refused the same way, before any WM_CLOSE post, so it
    // does not depend on UIPI refusing the post.
    for (level, current) in [
        (Some(HIGH), Some(MEDIUM)),
        (None, Some(MEDIUM)),
        (Some(MEDIUM), None),
    ] {
        let mut script = fixture();
        script.integrity.insert(41, level);
        script.current_integrity = current;
        let (report, events) = run(script, &[GAME], Kind::Close);
        assert_eq!(errors(&report), [(41, Some(ACTION_FAILED))], "{level:?}");
        assert!(actions(&events).is_empty(), "{events:?}");
    }
    let mut script = fixture();
    script.current_integrity = Some(HIGH);
    script.integrity.insert(41, Some(HIGH));
    let (report, events) = run(script, &[GAME], Kind::Close);
    assert_eq!(errors(&report), [(41, None)]);
    assert_eq!(actions(&events), [Event::Window(41)]);
}

/// The action checks the held image with the projection the scan named the
/// row with, so every image shape the scan lists as closable can be closed.
#[test]
fn rows_the_scan_lists_as_closable_can_be_closed() {
    for (image, scan_name) in [
        (r"C:\Windows\System32\Bubbles.scr", "Bubbles.scr"),
        (r"C:\Games\GAME", "GAME"),
        (r"\\nas\games\GAME.exe", "GAME"),
        (
            r"\\?\Volume{01234567-89ab-cdef-0123-456789abcdef}\Games\GAME.exe",
            "GAME",
        ),
    ] {
        assert_eq!(
            executable_stem(image).as_deref(),
            Some(scan_name),
            "{image}"
        );
        for kind in [Kind::Close, Kind::ForceClose] {
            let mut script = fixture();
            script.table.get_mut(&41).unwrap().image = image;
            let (report, events) = run(script, &[(41, 1000, scan_name)], kind);
            assert_eq!(errors(&report), [(41, None)], "{image}");
            assert!(!actions(&events).is_empty(), "{image}");
        }
    }
}

const DISPATCH: &str = include_str!(
    "../../../../../../../tools/openblade-windows-host/tests/fixtures/gpu-requests/managed-dispatch.jsonl"
);

/// The close rows of `managed-dispatch.jsonl`, written by the production
/// managed dispatcher, through the real shared decision and this backend.
/// Each row's request and scan are replayed over a scripted process table;
/// only the OS outcomes the managed script chose (no window, refused post,
/// Stop) are set per case. The results, their order and the error text must
/// be the managed ones.
#[test]
fn managed_dispatch_close_rows_are_derived_by_the_decision() {
    use json::Json;
    let block_reasons = [
        crate::gpu_apps::BlockReason::OtherSession,
        crate::gpu_apps::BlockReason::OtherAccount,
        crate::gpu_apps::BlockReason::Protected,
    ];
    let mut checked = 0;
    for line in DISPATCH.lines().filter(|line| !line.is_empty()) {
        let row = json::parse(line);
        let case = row["case"].as_str();
        let frame = json::base64(row["request"].as_str());
        let request = json::parse(std::str::from_utf8(&frame[4..]).unwrap());
        if request["operation"].as_str() != "close"
            || row["scan"]["isAvailable"] == Json::Bool(false)
        {
            continue;
        }
        let apps: Vec<GpuApp> = row["scan"]["apps"]
            .items()
            .iter()
            .map(|app| GpuApp {
                process_id: app["processId"].as_i32(),
                start_time_utc_ticks: app["startTimeUtcTicks"].as_i64(),
                name: app["name"].as_str().into(),
                utilization_percent: 1.0,
                block_reason: app["blockReason"].as_optional_str().map(|reason| {
                    block_reasons
                        .into_iter()
                        .find(|known| known.message() == reason)
                        .expect("a managed block reason")
                }),
            })
            .collect();
        let mut script = fixture();
        script.table.retain(|pid, _| matches!(pid, 800 | 900));
        for app in &apps {
            let created = app.start_time_utc_ticks - 504_911_232_000_000_000;
            assert_eq!(ticks(created), app.start_time_utc_ticks);
            let image: &'static str = Box::leak(format!(r"C:\Apps\{}.exe", app.name).into());
            script
                .table
                .insert(app.process_id, spec(800, created, image));
        }
        let cancelled = Arc::new(AtomicBool::new(false));
        match case {
            "close-success" | "force-success" | "close-stale-identity" | "close-130-apps" => {}
            "close-partial" => {
                script.windows.insert(43, WindowClose::NoWindow);
            }
            "close-all-failed" => {
                script.windows.insert(41, WindowClose::Failed);
                script.windows.insert(43, WindowClose::Failed);
            }
            "close-stopped-after-first" => {
                script.cancel_on = Some((Event::Window(41), Arc::clone(&cancelled)));
            }
            "close-stopped-before-any" => cancelled.store(true, Relaxed),
            other => panic!("unscripted managed close row {other}"),
        }
        let targets: Vec<Target> = request["targets"]
            .items()
            .iter()
            .map(|target| Target {
                process_id: target["processId"].as_i32(),
                start_time_utc_ticks: target["startTimeUtcTicks"].as_i64(),
                name: target["name"].as_str().into(),
            })
            .collect();
        let kind = if request["force"] == Json::Bool(true) {
            Kind::ForceClose
        } else {
            Kind::Close
        };
        let live = Rc::clone(&script.live);
        let compare =
            |left: &str, right: &str| left.to_ascii_uppercase().cmp(&right.to_ascii_uppercase());
        let failed = Cell::new(false);
        let inventory = GpuAppInventory {
            apps,
            truncated: false,
            omissions: Default::default(),
        };
        let report = close(
            &inventory,
            &targets,
            Context {
                api: RefCell::new(script),
                user: &USER,
                compare: &compare,
                comparison_failed: &failed,
                cancelled: &cancelled,
                kind,
            },
        );
        assert_eq!(live.get(), 0, "{case}");
        let expected = &row["response"];
        let actual: Vec<_> = report
            .results
            .iter()
            .map(|result| {
                (
                    result.process_id,
                    result.start_time_utc_ticks,
                    result.error.map(str::to_owned),
                )
            })
            .collect();
        let managed: Vec<_> = expected["results"]
            .items()
            .iter()
            .map(|result| {
                (
                    result["processId"].as_i32(),
                    result["startTimeUtcTicks"].as_i64(),
                    result["error"].as_optional_str().map(str::to_owned),
                )
            })
            .collect();
        assert_eq!(actual, managed, "{case}");
        assert_eq!(report.error, expected["error"].as_optional_str(), "{case}");
        checked += 1;
    }
    assert_eq!(checked, 8, "every available managed close row is derived");
}

mod json;

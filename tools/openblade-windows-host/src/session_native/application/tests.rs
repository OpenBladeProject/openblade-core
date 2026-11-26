//! The installed application reporter composition: verified status profiles,
//! the scripted platform watcher on the owner thread, and an injected report
//! transport. No OS foreground hook, process, or Service pipe is used; the
//! Service exchange has its own tests beside the Agent's Service client.
use std::{
    cell::{Cell, RefCell},
    collections::{BTreeSet, VecDeque},
    future::pending,
    io,
    rc::Rc,
    sync::{
        Arc, Mutex,
        atomic::{AtomicBool, AtomicU64, AtomicUsize, Ordering},
        mpsc,
    },
    time::Duration,
};

use openblade_application_reporter::{
    ActivationMode, ApplicationProfile, Guid, LEASE_INTERVAL, ReportApplicationContextRequest,
    ReportOutcome,
};
use openblade_platform_windows::application::watcher::{
    Wake,
    replay::{ProcessProbe, Script, ScriptedWatcher},
};
use serde_json::{Value, json};
use tokio::{sync::watch, time::sleep};

use super::{
    ProfileFeed, Profiles, host,
    owner::{Owner, Timing, Watcher},
    profiles, publish_status, serve,
};
use crate::{service_status::Snapshot, session_native::service_client::StatusUpdate};

const GAME: &str = r"C:\Games\Game.exe";
const OTHER: &str = r"C:\Games\Other.exe";
const THIRD: &str = r"C:\Games\Third.exe";
const FRAMES: &[u8] = include_bytes!("../../../tests/fixtures/get-status-v91.frames");

impl Watcher for ScriptedWatcher {
    fn set_notice(&mut self, wake: Wake) {
        ScriptedWatcher::set_notice(self, wake);
    }

    fn wait_ready(&mut self, timeout: Duration) -> io::Result<()> {
        ScriptedWatcher::wait_ready(self, timeout)
    }

    fn observe_profiles(&mut self, profiles: Vec<ApplicationProfile>) {
        ScriptedWatcher::observe_profiles(self, profiles);
    }

    fn poll(
        &mut self,
        elapsed: Duration,
        request_id: Guid,
    ) -> Option<ReportApplicationContextRequest> {
        ScriptedWatcher::poll(self, elapsed, request_id)
    }

    fn complete(&mut self, sequence: i64, outcome: ReportOutcome) {
        ScriptedWatcher::complete(self, sequence, outcome);
    }

    fn shutdown(self) -> io::Result<()> {
        ScriptedWatcher::shutdown(self)
    }
}

fn entry(number: u8, path: &str, mode: i32) -> Value {
    json!({
        "id": format!("00000000-0000-4000-8000-{number:012x}"),
        "name": "Profile",
        "executablePath": path,
        "activationMode": mode,
        "overrides": {"pluggedIn": null, "battery": null, "usbC": null},
        "enabled": true,
    })
}

/// A verified v91 status carrying these application profiles.
fn snapshot(profiles: Value) -> Snapshot {
    let request = i32::from_le_bytes(FRAMES[..4].try_into().unwrap()) as usize;
    let mut frame: Value = serde_json::from_slice(&FRAMES[4 + request + 4..]).unwrap();
    frame["status"]["profiles"]["applicationProfiles"] = profiles;
    serde_json::from_value(frame["status"].take()).unwrap()
}

#[test]
fn status_profiles_become_the_reporter_selection_fields() {
    let list = json!([
        entry(1, GAME, 0),
        entry(2, OTHER, 1),
        {"id": "00000000-0000-4000-8000-000000000003", "name": "Off",
         "executablePath": THIRD, "activationMode": 0, "overrides": null, "enabled": false},
        // Managed defaults a missing Enabled member to true.
        {"id": "00000000-0000-4000-8000-000000000004", "name": "Default",
         "executablePath": THIRD, "activationMode": 1, "overrides": null},
    ]);
    let decoded = profiles::decode(&list).unwrap();
    let expected = [
        (1, GAME, ActivationMode::Foreground, true),
        (2, OTHER, ActivationMode::WhileRunningAfterActivation, true),
        (3, THIRD, ActivationMode::Foreground, false),
        (4, THIRD, ActivationMode::WhileRunningAfterActivation, true),
    ];
    assert_eq!(decoded.len(), expected.len());
    for (profile, (number, path, mode, enabled)) in decoded.iter().zip(expected) {
        assert_eq!(
            profile.id,
            profiles::guid(&format!("00000000-0000-4000-8000-{number:012x}")).unwrap()
        );
        assert_eq!(profile.executable_path, path);
        assert_eq!(profile.mode, mode);
        assert_eq!(profile.enabled, enabled);
    }
    assert!(profiles::decode(&json!([])).unwrap().is_empty());
}

#[test]
fn an_unreadable_profile_list_is_never_partially_applied() {
    let mut bad_mode = entry(1, GAME, 0);
    bad_mode["activationMode"] = 2.into();
    let mut bad_id = entry(1, GAME, 0);
    bad_id["id"] = "00000000-0000-0000-0000-000000000000".into();
    let mut bad_path = entry(1, GAME, 0);
    bad_path["executablePath"] = Value::Null;
    let mut fractional = entry(1, GAME, 0);
    fractional["activationMode"] = 0.5.into();
    for list in [
        json!([entry(2, OTHER, 1), bad_mode]),
        json!([entry(2, OTHER, 1), bad_id]),
        json!([entry(2, OTHER, 1), bad_path]),
        json!([entry(2, OTHER, 1), fractional]),
        json!({"not": "a list"}),
        Value::Null,
    ] {
        assert!(profiles::decode(&list).is_none(), "{list}");
    }
}

#[test]
fn the_feed_publishes_only_a_changed_selection_and_keeps_the_last_good_list() {
    let feed = ProfileFeed::new();
    let receiver = feed.subscribe();
    assert!(receiver.borrow().is_none());

    feed.observe(&snapshot(json!([entry(1, GAME, 0)])));
    let first: Profiles = receiver.borrow().clone();
    assert_eq!(first.as_ref().unwrap().len(), 1);

    // Same selection fields: no new allocation, so the owner sees no change.
    let mut renamed = entry(1, GAME, 0);
    renamed["name"] = "Renamed".into();
    feed.observe(&snapshot(json!([renamed])));
    assert!(Arc::ptr_eq(
        first.as_ref().unwrap(),
        receiver.borrow().as_ref().unwrap()
    ));

    // An unreadable list leaves the earlier one in place.
    let mut broken = entry(2, OTHER, 1);
    broken["activationMode"] = 7.into();
    feed.observe(&snapshot(json!([broken])));
    assert!(Arc::ptr_eq(
        first.as_ref().unwrap(),
        receiver.borrow().as_ref().unwrap()
    ));

    feed.observe(&snapshot(json!([entry(1, GAME, 0), entry(2, OTHER, 1)])));
    assert_eq!(receiver.borrow().as_ref().unwrap().len(), 2);
}

fn guid_source() -> impl FnMut() -> io::Result<Guid> {
    let mut next = 0u8;
    move || {
        next += 1;
        Ok(Guid([next; 16]))
    }
}

#[test]
fn the_owner_applies_profiles_before_the_poll_and_never_reuses_a_request_id() {
    let feed = ProfileFeed::new();
    let mut script = Script::default();
    let _game = script.process(1, 41, 1, GAME);
    script.focus(1);
    let mut watcher = script.start(91, Guid([9; 16]), Arc::default()).unwrap();
    watcher.wait_ready(Duration::from_secs(2)).unwrap();
    let mut owner = Owner::new(watcher, feed.subscribe(), guid_source()).unwrap();

    // Without a profile snapshot nothing is selected, as in managed.
    assert!(owner.step(Duration::ZERO).unwrap().is_none());

    feed.observe(&snapshot(json!([entry(1, GAME, 0)])));
    let first = owner.step(Duration::ZERO).unwrap().expect("first report");
    assert_eq!(first.sequence, 1);
    assert_eq!(first.executable_path.as_deref(), Some(GAME));
    assert_eq!(first.version, 91);
    assert_eq!(first.instance_id, Guid([9; 16]));
    assert_eq!(first.request_id, Guid([1; 16]));
    // One request is in flight; nothing else is produced until it completes.
    assert!(owner.step(Duration::ZERO).unwrap().is_none());
    owner.complete(1, ReportOutcome::Applied);
    assert!(owner.step(Duration::ZERO).unwrap().is_none());

    let renewal = owner
        .step(LEASE_INTERVAL)
        .unwrap()
        .expect("renewal at the lease interval");
    assert_eq!(renewal.sequence, 2);
    assert_eq!(renewal.instance_id, first.instance_id);
    assert_eq!(renewal.request_id, Guid([2; 16]));
    owner.complete(2, ReportOutcome::Stored);

    // Disabling the profile is a profile change observed before the poll.
    let mut off = entry(1, GAME, 0);
    off["enabled"] = false.into();
    feed.observe(&snapshot(json!([off])));
    let clear = owner.step(LEASE_INTERVAL).unwrap().expect("clear");
    assert_eq!(clear.executable_path, None);
    assert_eq!(clear.request_id, Guid([3; 16]));
    owner.shutdown().unwrap();
    assert!(!script.registered());
}

#[test]
fn a_closed_feed_keeps_the_last_profiles() {
    let feed = ProfileFeed::new();
    let mut script = Script::default();
    let _game = script.process(1, 41, 1, GAME);
    script.focus(1);
    let mut watcher = script.start(91, Guid([9; 16]), Arc::default()).unwrap();
    watcher.wait_ready(Duration::from_secs(2)).unwrap();
    let receiver = feed.subscribe();
    feed.observe(&snapshot(json!([entry(1, GAME, 0)])));
    drop(feed);
    let mut owner = Owner::new(watcher, receiver, guid_source()).unwrap();
    let request = owner.step(Duration::ZERO).unwrap().expect("report");
    assert_eq!(request.executable_path.as_deref(), Some(GAME));
    owner.shutdown().unwrap();
}

// ---- The whole composition: owner thread, transport, and shutdown. ----

type Command = Box<dyn FnOnce(&Script, &mut Vec<ProcessProbe>) + Send>;

/// The scripted platform watcher, made on the owner thread because its script
/// is thread-confined. Tests steer it with commands applied before each poll
/// and read its state through atomics, so the owner and transport run for real.
struct Driven {
    watcher: Option<ScriptedWatcher>,
    script: Script,
    probes: Vec<ProcessProbe>,
    commands: mpsc::Receiver<Command>,
    clock: Arc<AtomicU64>,
    handles: Arc<AtomicUsize>,
    registered: Arc<AtomicBool>,
    notice: Notice,
}

/// The owner's wake, installed through `set_notice`, which the test shares so a
/// command wakes the owner as a foreground notice would.
type Notice = Arc<Mutex<Option<Wake>>>;

impl Driven {
    fn publish(&self) {
        self.handles.store(
            self.probes.iter().map(ProcessProbe::handles).sum(),
            Ordering::Release,
        );
        self.registered
            .store(self.script.registered(), Ordering::Release);
    }
}

impl Watcher for Driven {
    fn set_notice(&mut self, wake: Wake) {
        *self.notice.lock().unwrap() = Some(wake.clone());
        self.watcher.as_mut().unwrap().set_notice(wake);
    }

    fn wait_ready(&mut self, timeout: Duration) -> io::Result<()> {
        self.watcher.as_mut().unwrap().wait_ready(timeout)?;
        self.publish();
        Ok(())
    }

    fn observe_profiles(&mut self, profiles: Vec<ApplicationProfile>) {
        self.watcher.as_mut().unwrap().observe_profiles(profiles);
    }

    fn poll(&mut self, _: Duration, request_id: Guid) -> Option<ReportApplicationContextRequest> {
        while let Ok(command) = self.commands.try_recv() {
            command(&self.script, &mut self.probes);
        }
        // The test owns the lease clock; the real elapsed time is irrelevant.
        let elapsed = Duration::from_millis(self.clock.load(Ordering::Acquire));
        let request = self.watcher.as_mut().unwrap().poll(elapsed, request_id);
        self.publish();
        request
    }

    fn complete(&mut self, sequence: i64, outcome: ReportOutcome) {
        self.watcher.as_mut().unwrap().complete(sequence, outcome);
    }

    fn shutdown(mut self) -> io::Result<()> {
        let result = self.watcher.take().unwrap().shutdown();
        self.publish();
        result
    }
}

#[derive(Debug)]
enum Reply {
    Outcome(ReportOutcome),
    Fail(io::ErrorKind),
    Hang,
}

#[derive(Debug)]
struct Sent {
    version: i32,
    request_id: Guid,
    instance_id: Guid,
    sequence: i64,
    path: Option<String>,
}

struct Rig {
    commands: mpsc::Sender<Command>,
    clock: Arc<AtomicU64>,
    handles: Arc<AtomicUsize>,
    registered: Arc<AtomicBool>,
    notice: Notice,
    feed: ProfileFeed,
    stop: watch::Sender<bool>,
    log: Rc<RefCell<Vec<Sent>>>,
    hung: Rc<Cell<bool>>,
    released: Rc<Cell<bool>>,
}

/// Dropped with the pending report future, which is what closes its pipe.
struct Hung(Rc<Cell<bool>>);

impl Drop for Hung {
    fn drop(&mut self) {
        self.0.set(true);
    }
}

impl Rig {
    fn drive(&self, command: impl FnOnce(&Script, &mut Vec<ProcessProbe>) + Send + 'static) {
        self.commands.send(Box::new(command)).unwrap();
        if let Some(wake) = self.notice.lock().unwrap().clone() {
            wake();
        }
    }

    fn tick(&self, ticks: u64) {
        self.clock
            .store(LEASE_INTERVAL.as_millis() as u64 * ticks, Ordering::Release);
    }

    fn sent(&self) -> usize {
        self.log.borrow().len()
    }

    fn last(&self) -> (i64, Option<String>) {
        let log = self.log.borrow();
        let last = log.last().expect("a report was sent");
        (last.sequence, last.path.clone())
    }

    fn open_handles(&self) -> usize {
        self.handles.load(Ordering::Acquire)
    }

    async fn until(&self, what: &str, condition: impl Fn(&Self) -> bool) {
        for _ in 0..1_000 {
            if condition(self) {
                return;
            }
            sleep(Duration::from_millis(1)).await;
        }
        panic!("timed out waiting for {what}; sent {:?}", self.log.borrow());
    }

    async fn report(&self, count: usize, sequence: i64, path: Option<&str>) {
        self.until("a report", |rig| rig.sent() >= count).await;
        assert_eq!(self.sent(), count, "{:?}", self.log.borrow());
        assert_eq!(self.last(), (sequence, path.map(str::to_owned)));
    }

    /// Many poll periods with no further report.
    async fn quiet(&self, count: usize) {
        sleep(Duration::from_millis(60)).await;
        assert_eq!(self.sent(), count, "{:?}", self.log.borrow());
    }

    fn finish(&self) {
        self.stop.send_replace(true);
    }
}

const TIMING: Timing = Timing {
    poll: Duration::from_millis(2),
    ready: Duration::from_secs(2),
};

async fn rig<R>(
    replies: Vec<Reply>,
    verify: impl Fn() -> io::Result<()>,
    scenario: impl AsyncFnOnce(&Rig) -> R,
) -> (io::Result<()>, R, Rig) {
    rig_timed(TIMING, replies, verify, scenario).await
}

async fn rig_timed<R>(
    timing: Timing,
    replies: Vec<Reply>,
    verify: impl Fn() -> io::Result<()>,
    scenario: impl AsyncFnOnce(&Rig) -> R,
) -> (io::Result<()>, R, Rig) {
    let (commands, receiver) = mpsc::channel();
    let clock = Arc::new(AtomicU64::new(0));
    let handles = Arc::new(AtomicUsize::new(0));
    let registered = Arc::new(AtomicBool::new(false));
    let notice = Notice::default();
    let (stop, stopped) = watch::channel(false);
    let rig = Rig {
        commands,
        clock: clock.clone(),
        handles: handles.clone(),
        registered: registered.clone(),
        notice: notice.clone(),
        feed: ProfileFeed::new(),
        stop,
        log: Rc::default(),
        hung: Rc::default(),
        released: Rc::default(),
    };
    let make = move |instance, cancel| {
        let mut script = Script::default();
        let watcher = script.start(91, instance, cancel)?;
        Ok(Driven {
            watcher: Some(watcher),
            script,
            probes: Vec::new(),
            commands: receiver,
            clock,
            handles,
            registered,
            notice,
        })
    };
    let replies = RefCell::new(VecDeque::from(replies));
    let report = async |request: &ReportApplicationContextRequest| {
        rig.log.borrow_mut().push(Sent {
            version: request.version,
            request_id: request.request_id,
            instance_id: request.instance_id,
            sequence: request.sequence,
            path: request.executable_path.clone(),
        });
        let reply = replies
            .borrow_mut()
            .pop_front()
            .unwrap_or(Reply::Outcome(ReportOutcome::Applied));
        match reply {
            Reply::Outcome(outcome) => Ok(outcome),
            Reply::Fail(kind) => Err(kind.into()),
            Reply::Hang => {
                let _guard = Hung(rig.released.clone());
                rig.hung.set(true);
                pending::<()>().await;
                unreachable!()
            }
        }
    };
    let (result, scenario) = tokio::join!(
        serve(make, rig.feed.subscribe(), stopped, report, verify, timing),
        scenario(&rig),
    );
    (result, scenario, rig)
}

fn profiles_for(rig: &Rig, list: Value) {
    rig.feed.observe(&snapshot(list));
}

fn focus(rig: &Rig, window: usize, pid: u32, path: &'static str) {
    rig.drive(move |script, probes| {
        probes.push(script.process(window, pid, 1, path));
        script.focus(window);
    });
}

#[tokio::test]
async fn reports_the_foreground_profile_renews_at_the_lease_and_clears_on_leaving() {
    let (result, (), rig) = rig(
        vec![],
        || Ok(()),
        async |rig| {
            profiles_for(rig, json!([entry(1, GAME, 0), entry(2, OTHER, 1)]));
            focus(rig, 1, 41, GAME);
            rig.report(1, 1, Some(GAME)).await;
            // Re-announcing the same window and waiting less than a lease sends nothing.
            rig.drive(|script, _| script.focus(1));
            rig.quiet(1).await;
            rig.tick(1);
            rig.report(2, 2, Some(GAME)).await;
            rig.quiet(2).await;
            // A Foreground profile ends with the foreground: an explicit clear.
            rig.drive(|script, _| script.focus(0));
            rig.report(3, 3, None).await;
            rig.until("the process handle to retire", |rig| {
                rig.open_handles() == 0
            })
            .await;
            rig.finish();
        },
    )
    .await;
    result.unwrap();
    let log = rig.log.borrow();
    assert!(log.iter().all(|sent| sent.version == 91));
    // One reporter instance, a fresh request id for every attempt.
    assert_eq!(
        log.iter()
            .map(|sent| sent.instance_id.0)
            .collect::<BTreeSet<_>>()
            .len(),
        1
    );
    assert_eq!(
        log.iter()
            .map(|sent| sent.request_id.0)
            .collect::<BTreeSet<_>>()
            .len(),
        log.len()
    );
    assert_eq!(rig.open_handles(), 0);
    assert!(!rig.registered.load(Ordering::Acquire));
}

#[tokio::test]
async fn a_while_running_profile_survives_leaving_the_foreground_until_its_process_exits() {
    let (result, (), _) = rig(
        vec![],
        || Ok(()),
        async |rig| {
            profiles_for(rig, json!([entry(2, OTHER, 1)]));
            focus(rig, 2, 42, OTHER);
            rig.report(1, 1, Some(OTHER)).await;
            rig.drive(|script, _| script.focus(0));
            rig.quiet(1).await;
            rig.until("the retained handle", |rig| rig.open_handles() == 1)
                .await;
            rig.drive(|_, probes| probes[0].exit());
            rig.report(2, 2, None).await;
            rig.until("the exited handle to retire", |rig| rig.open_handles() == 0)
                .await;
            rig.finish();
        },
    )
    .await;
    result.unwrap();
}

#[tokio::test]
async fn reactivation_advances_recency_without_replacing_the_process_lifetime() {
    let (result, (), _) = rig(
        vec![],
        || Ok(()),
        async |rig| {
            profiles_for(rig, json!([entry(2, OTHER, 1), entry(3, THIRD, 1)]));
            rig.drive(|script, probes| {
                probes.push(script.process(2, 42, 1, OTHER));
                probes.push(script.process(3, 43, 1, THIRD));
                script.focus(2);
            });
            rig.report(1, 1, Some(OTHER)).await;
            rig.drive(|script, _| script.focus(3));
            rig.report(2, 2, Some(THIRD)).await;
            rig.drive(|script, _| script.focus(0));
            rig.quiet(2).await;
            rig.drive(|script, _| script.focus(2));
            rig.report(3, 3, Some(OTHER)).await;
            rig.drive(|script, _| script.focus(0));
            rig.quiet(3).await;
            // Both lifetimes are still the handles retained at first activation.
            assert_eq!(rig.open_handles(), 2);
            // The most recently activated retained process exits: the older one is selected.
            rig.drive(|_, probes| probes[0].exit());
            rig.report(4, 4, Some(THIRD)).await;
            rig.finish();
        },
    )
    .await;
    result.unwrap();
}

#[tokio::test]
async fn a_failed_or_refused_report_repeats_only_at_the_next_renewal() {
    let replies = vec![
        Reply::Fail(io::ErrorKind::NotFound), // Service stopped
        Reply::Outcome(ReportOutcome::Rejected),
        Reply::Fail(io::ErrorKind::ConnectionReset), // Service restarting
        Reply::Outcome(ReportOutcome::Stored),
    ];
    let (result, (), rig) = rig(
        replies,
        || Ok(()),
        async |rig| {
            profiles_for(rig, json!([entry(1, GAME, 0)]));
            focus(rig, 1, 41, GAME);
            rig.report(1, 1, Some(GAME)).await;
            // Managed has no immediate retry: only the five-second renewal.
            rig.quiet(1).await;
            for tick in 1..=3 {
                rig.tick(tick);
                rig.report(1 + tick as usize, 1 + tick as i64, Some(GAME))
                    .await;
            }
            // Acknowledged: the next one is again a renewal, not a retry.
            rig.quiet(4).await;
            rig.tick(4);
            rig.report(5, 5, Some(GAME)).await;
            rig.finish();
        },
    )
    .await;
    result.unwrap();
    let ids = rig
        .log
        .borrow()
        .iter()
        .map(|sent| sent.request_id.0)
        .collect::<BTreeSet<_>>();
    assert_eq!(ids.len(), 5, "every retry carries a fresh request id");
}

#[tokio::test]
async fn a_pending_clear_is_retried_after_a_failed_send() {
    let replies = vec![
        Reply::Outcome(ReportOutcome::Applied),
        Reply::Fail(io::ErrorKind::BrokenPipe),
    ];
    let (result, (), _) = rig(
        replies,
        || Ok(()),
        async |rig| {
            profiles_for(rig, json!([entry(1, GAME, 0)]));
            focus(rig, 1, 41, GAME);
            rig.report(1, 1, Some(GAME)).await;
            rig.drive(|script, _| script.focus(0));
            rig.report(2, 2, None).await; // fails
            rig.quiet(2).await;
            rig.tick(1);
            // A clear that never reached the Service is still owed.
            rig.report(3, 3, None).await;
            rig.quiet(3).await;
            rig.tick(2);
            // Acknowledged and idle: no renewal for an empty context.
            rig.quiet(3).await;
            rig.finish();
        },
    )
    .await;
    result.unwrap();
}

#[tokio::test]
async fn stopping_during_a_pending_report_drops_it_and_retires_the_owner() {
    let (result, (), rig) = rig(
        vec![Reply::Hang],
        || Ok(()),
        async |rig| {
            profiles_for(rig, json!([entry(2, OTHER, 1)]));
            focus(rig, 2, 42, OTHER);
            rig.until("the report to be pending", |rig| rig.hung.get())
                .await;
            assert!(!rig.released.get());
            assert_eq!(rig.open_handles(), 1);
            rig.finish();
        },
    )
    .await;
    result.unwrap();
    // The in-flight report future was dropped (closing its connection), the
    // retained process handle was released, and the foreground hooks removed.
    assert!(rig.released.get());
    assert_eq!(rig.open_handles(), 0);
    assert!(!rig.registered.load(Ordering::Acquire));
    assert_eq!(rig.sent(), 1);
}

#[tokio::test]
async fn a_locked_console_keeps_the_retained_context_and_renews_it() {
    let (result, (), _) = rig(
        vec![],
        || Ok(()),
        async |rig| {
            profiles_for(rig, json!([entry(2, OTHER, 1)]));
            focus(rig, 2, 42, OTHER);
            rig.report(1, 1, Some(OTHER)).await;
            rig.drive(|script, _| {
                script.focus(0);
                script.active(false);
            });
            // Managed keeps the tracked process and its renewal through a lock,
            // and the Service accepts reports from the locked console session.
            rig.quiet(1).await;
            assert_eq!(
                rig.open_handles(),
                1,
                "the live process handle was released"
            );
            rig.tick(1);
            rig.report(2, 2, Some(OTHER)).await;
            rig.tick(2);
            rig.report(3, 3, Some(OTHER)).await;
            rig.drive(|script, _| script.active(true));
            rig.quiet(3).await;
            assert_eq!(rig.open_handles(), 1);
            rig.finish();
        },
    )
    .await;
    result.unwrap();
}

#[tokio::test]
async fn a_secure_desktop_switch_keeps_a_background_retention() {
    let (result, (), _) = rig(
        vec![],
        || Ok(()),
        async |rig| {
            profiles_for(rig, json!([entry(2, OTHER, 1)]));
            focus(rig, 2, 42, OTHER);
            rig.report(1, 1, Some(OTHER)).await;
            rig.drive(|script, _| script.focus(0));
            rig.quiet(1).await;
            rig.drive(|script, _| script.desktop_switch());
            rig.quiet(1).await;
            assert_eq!(
                rig.open_handles(),
                1,
                "the live process handle was released"
            );
            rig.tick(1);
            rig.report(2, 2, Some(OTHER)).await;
            rig.finish();
        },
    )
    .await;
    result.unwrap();
}

#[tokio::test]
async fn a_different_session_still_clears_and_releases_every_process() {
    let (result, (), _) = rig(
        vec![],
        || Ok(()),
        async |rig| {
            profiles_for(rig, json!([entry(2, OTHER, 1)]));
            focus(rig, 2, 42, OTHER);
            rig.report(1, 1, Some(OTHER)).await;
            rig.drive(|script, _| {
                script.focus(0);
                script.active(false);
            });
            rig.quiet(1).await;
            rig.drive(|script, _| {
                script.session(2);
                script.active(true);
            });
            rig.report(2, 2, None).await;
            rig.until("the handle to retire", |rig| rig.open_handles() == 0)
                .await;
            rig.finish();
        },
    )
    .await;
    result.unwrap();
}

#[tokio::test]
async fn profile_changes_apply_to_the_current_foreground_without_a_new_window_event() {
    let (result, (), _) = rig(
        vec![],
        || Ok(()),
        async |rig| {
            focus(rig, 1, 41, GAME);
            // No snapshot yet: managed reports nothing before the first status.
            rig.quiet(0).await;
            profiles_for(rig, json!([entry(1, GAME, 0)]));
            rig.report(1, 1, Some(GAME)).await;
            let mut off = entry(1, GAME, 0);
            off["enabled"] = false.into();
            profiles_for(rig, json!([off]));
            rig.report(2, 2, None).await;
            rig.finish();
        },
    )
    .await;
    result.unwrap();
}

#[tokio::test]
async fn origin_loss_before_a_send_is_fatal_and_sends_nothing() {
    // The scenario waits for the event it depends on, not for a fixed time: the
    // origin check ran, or (without it) a report was sent.
    let verified = Arc::new(AtomicUsize::new(0));
    let counted = verified.clone();
    let (result, (), rig) = rig(
        vec![],
        move || {
            counted.fetch_add(1, Ordering::AcqRel);
            Err(io::Error::new(
                io::ErrorKind::PermissionDenied,
                "origin lost",
            ))
        },
        async |rig| {
            profiles_for(rig, json!([entry(1, GAME, 0)]));
            focus(rig, 1, 41, GAME);
            rig.until("the origin check or a send", |rig| {
                verified.load(Ordering::Acquire) > 0 || rig.sent() > 0
            })
            .await;
            // Without the fatal origin error the role would otherwise wait forever.
            rig.finish();
        },
    )
    .await;
    assert_eq!(result.unwrap_err().kind(), io::ErrorKind::PermissionDenied);
    assert_eq!(rig.sent(), 0);
    assert_eq!(rig.open_handles(), 0);
    assert!(!rig.registered.load(Ordering::Acquire));
}

#[tokio::test]
async fn dropping_the_role_without_a_stop_still_retires_the_watcher() {
    let (_commands, receiver) = mpsc::channel::<Command>();
    let handles = Arc::new(AtomicUsize::new(0));
    let registered = Arc::new(AtomicBool::new(false));
    let feed = ProfileFeed::new();
    let (_stop, stopped) = watch::channel(false);
    let make = {
        let registered = registered.clone();
        move |instance, cancel| {
            let mut script = Script::default();
            let watcher = script.start(91, instance, cancel)?;
            Ok(Driven {
                watcher: Some(watcher),
                script,
                probes: Vec::new(),
                commands: receiver,
                clock: Arc::default(),
                handles,
                registered,
                notice: Notice::default(),
            })
        }
    };
    let role = serve(
        make,
        feed.subscribe(),
        stopped,
        async |_: &ReportApplicationContextRequest| Ok(ReportOutcome::Applied),
        || Ok(()),
        TIMING,
    );
    let mut role = Box::pin(role);
    let registered_flag = registered.clone();
    tokio::select! {
        _ = role.as_mut() => panic!("the role ended on its own"),
        () = async {
            for _ in 0..1_000 {
                if registered_flag.load(Ordering::Acquire) { return; }
                sleep(Duration::from_millis(1)).await;
            }
            panic!("the watcher never registered");
        } => {}
    }
    drop(role);
    for _ in 0..1_000 {
        if !registered.load(Ordering::Acquire) {
            return;
        }
        sleep(Duration::from_millis(1)).await;
    }
    panic!("a dropped role left the foreground hooks registered");
}

/// A Send watcher for the startup and self-retirement paths.
struct Fake {
    cancel: Arc<AtomicBool>,
    ready: io::Result<()>,
    retire_on_poll: bool,
    joined: Arc<AtomicBool>,
}

impl Watcher for Fake {
    fn set_notice(&mut self, _: Wake) {}

    fn wait_ready(&mut self, _: Duration) -> io::Result<()> {
        std::mem::replace(&mut self.ready, Ok(()))
    }

    fn observe_profiles(&mut self, _: Vec<ApplicationProfile>) {}

    fn poll(&mut self, _: Duration, _: Guid) -> Option<ReportApplicationContextRequest> {
        if self.retire_on_poll {
            self.cancel.store(true, Ordering::Release);
        }
        None
    }

    fn complete(&mut self, _: i64, _: ReportOutcome) {}

    fn shutdown(self) -> io::Result<()> {
        self.joined.store(true, Ordering::Release);
        Ok(())
    }
}

async fn fake_role(
    start: impl FnOnce(Arc<AtomicBool>, Arc<AtomicBool>) -> io::Result<Fake> + Send + 'static,
) -> (io::Result<()>, Arc<AtomicBool>) {
    let joined = Arc::new(AtomicBool::new(false));
    let (_stop, stopped) = watch::channel(false);
    let feed = ProfileFeed::new();
    let shared = joined.clone();
    let result = serve(
        move |_, cancel| start(cancel, shared),
        feed.subscribe(),
        stopped,
        async |_: &ReportApplicationContextRequest| Ok(ReportOutcome::Applied),
        || Ok(()),
        TIMING,
    )
    .await;
    (result, joined)
}

#[tokio::test]
async fn a_watcher_that_cannot_start_fails_the_role() {
    let (result, _) = fake_role(|_, _| Err(io::Error::other("hook registration failed"))).await;
    assert_eq!(result.unwrap_err().to_string(), "hook registration failed");

    // A registration that never becomes ready is also a startup failure, and
    // the watcher it left behind is retired.
    let (result, joined) = fake_role(|cancel, joined| {
        Ok(Fake {
            cancel,
            ready: Err(io::Error::new(
                io::ErrorKind::TimedOut,
                "foreground worker did not start",
            )),
            retire_on_poll: false,
            joined,
        })
    })
    .await;
    assert_eq!(result.unwrap_err().kind(), io::ErrorKind::TimedOut);
    for _ in 0..1_000 {
        if joined.load(Ordering::Acquire) {
            return;
        }
        sleep(Duration::from_millis(1)).await;
    }
    panic!("the failed startup was not retired");
}

#[tokio::test]
async fn a_watcher_that_retires_itself_fails_the_role_instead_of_going_silent() {
    let (result, joined) = fake_role(|cancel, joined| {
        Ok(Fake {
            cancel,
            ready: Ok(()),
            retire_on_poll: true,
            joined,
        })
    })
    .await;
    assert_eq!(result.unwrap_err().kind(), io::ErrorKind::BrokenPipe);
    assert!(joined.load(Ordering::Acquire));
}

/// A watcher whose startup is stuck inside the OS until the test releases it.
struct Stuck {
    release: Arc<AtomicBool>,
    joined: Arc<AtomicBool>,
}

impl Watcher for Stuck {
    fn set_notice(&mut self, _: Wake) {}

    fn wait_ready(&mut self, _: Duration) -> io::Result<()> {
        while !self.release.load(Ordering::Acquire) {
            std::thread::sleep(Duration::from_millis(1));
        }
        Ok(())
    }

    fn observe_profiles(&mut self, _: Vec<ApplicationProfile>) {}

    fn poll(&mut self, _: Duration, _: Guid) -> Option<ReportApplicationContextRequest> {
        None
    }

    fn complete(&mut self, _: i64, _: ReportOutcome) {}

    fn shutdown(self) -> io::Result<()> {
        self.joined.store(true, Ordering::Release);
        Ok(())
    }
}

#[tokio::test]
async fn a_stop_during_a_stuck_startup_is_bounded_and_the_owner_still_retires() {
    let release = Arc::new(AtomicBool::new(false));
    let joined = Arc::new(AtomicBool::new(false));
    let (_stop, stopped) = watch::channel(true);
    let feed = ProfileFeed::new();
    let (gate, done) = (release.clone(), joined.clone());
    let started = std::time::Instant::now();
    let result = serve(
        move |_, _| {
            Ok(Stuck {
                release: gate,
                joined: done,
            })
        },
        feed.subscribe(),
        stopped,
        async |_: &ReportApplicationContextRequest| Ok(ReportOutcome::Applied),
        || Ok(()),
        Timing {
            poll: Duration::from_millis(2),
            ready: Duration::from_millis(150),
        },
    )
    .await;
    // The Agent's stop is held for the startup budget at most.
    assert_eq!(result.unwrap_err().kind(), io::ErrorKind::TimedOut);
    assert!(started.elapsed() < Duration::from_secs(2));
    assert!(!joined.load(Ordering::Acquire));
    // The stuck call returns later: the detached owner sees the stop and retires.
    release.store(true, Ordering::Release);
    for _ in 0..1_000 {
        if joined.load(Ordering::Acquire) {
            return;
        }
        sleep(Duration::from_millis(1)).await;
    }
    panic!("the detached owner never retired its watcher");
}

/// A housekeeping tick far beyond any test: only a wake can make progress.
const SLOW: Timing = Timing {
    poll: Duration::from_secs(60),
    ready: Duration::from_secs(2),
};

#[tokio::test]
async fn foreground_notices_and_profile_changes_wake_the_owner_at_once() {
    let started = std::time::Instant::now();
    let (result, (), _) = rig_timed(
        SLOW,
        vec![],
        || Ok(()),
        async |rig| {
            // A notice (the command wakes like a foreground callback does).
            focus(rig, 1, 41, GAME);
            profiles_for(rig, json!([entry(1, GAME, 0)]));
            rig.report(1, 1, Some(GAME)).await;
            // A profile change alone, with nothing else to wake the owner.
            let mut off = entry(1, GAME, 0);
            off["enabled"] = false.into();
            profiles_for(rig, json!([off]));
            rig.report(2, 2, None).await;
            // A foreground change.
            profiles_for(rig, json!([entry(1, GAME, 0), entry(2, OTHER, 0)]));
            rig.report(3, 3, Some(GAME)).await;
            rig.drive(|script, probes| {
                probes.push(script.process(2, 42, 1, OTHER));
                script.focus(2);
            });
            rig.report(4, 4, Some(OTHER)).await;
            rig.finish();
        },
    )
    .await;
    result.unwrap();
    // Four reports under a 60 s tick: nothing waited for housekeeping.
    assert!(started.elapsed() < Duration::from_secs(20));
}

/// A watcher that wakes its owner on every poll, so the wake channel is always
/// full when the transport finishes a report.
struct Chatty {
    wake: Option<Wake>,
    sent: bool,
    completed: Arc<AtomicU64>,
}

impl Watcher for Chatty {
    fn set_notice(&mut self, wake: Wake) {
        self.wake = Some(wake);
    }

    fn wait_ready(&mut self, _: Duration) -> io::Result<()> {
        Ok(())
    }

    fn observe_profiles(&mut self, _: Vec<ApplicationProfile>) {}

    fn poll(&mut self, _: Duration, request_id: Guid) -> Option<ReportApplicationContextRequest> {
        if let Some(wake) = &self.wake {
            wake();
        }
        if std::mem::replace(&mut self.sent, true) {
            return None;
        }
        Some(ReportApplicationContextRequest {
            version: 91,
            request_id,
            instance_id: Guid([3; 16]),
            sequence: 1,
            executable_path: Some(GAME.into()),
        })
    }

    fn complete(&mut self, sequence: i64, _: ReportOutcome) {
        self.completed.store(sequence as u64, Ordering::Release);
    }

    fn shutdown(self) -> io::Result<()> {
        Ok(())
    }
}

#[tokio::test]
async fn a_completion_is_never_lost_behind_a_full_wake_channel() {
    let completed = Arc::new(AtomicU64::new(0));
    let (stop, stopped) = watch::channel(false);
    let feed = ProfileFeed::new();
    let seen = completed.clone();
    let watcher = completed.clone();
    let role = serve(
        move |_, _| {
            Ok(Chatty {
                wake: None,
                sent: false,
                completed: watcher,
            })
        },
        feed.subscribe(),
        stopped,
        async |_: &ReportApplicationContextRequest| Ok(ReportOutcome::Applied),
        || Ok(()),
        SLOW,
    );
    let watching = async {
        for _ in 0..1_000 {
            if seen.load(Ordering::Acquire) == 1 {
                stop.send_replace(true);
                return;
            }
            sleep(Duration::from_millis(1)).await;
        }
        stop.send_replace(true);
        panic!("the completion never reached the owner");
    };
    let (result, ()) = tokio::join!(role, watching);
    result.unwrap();
}

#[tokio::test]
async fn the_transport_runs_on_its_own_thread_and_never_blocks_the_callers_runtime() {
    let caller = std::thread::current().id();
    let ticked = Arc::new(AtomicBool::new(false));
    let seen = ticked.clone();
    let work = host(move || async move {
        assert_ne!(std::thread::current().id(), caller);
        // Blocking here, as acquiring a Service generation does, must leave the
        // caller's other roles running.
        std::thread::sleep(Duration::from_millis(400));
        assert!(seen.load(Ordering::Acquire), "the caller was blocked");
        Ok(())
    });
    let ticker = async {
        for _ in 0..5 {
            sleep(Duration::from_millis(10)).await;
        }
        ticked.store(true, Ordering::Release);
    };
    let (result, ()) = tokio::join!(work, ticker);
    result.unwrap();
}

#[tokio::test]
async fn the_hosted_transport_reports_its_failure_and_its_panic() {
    let error = host(|| async { Err(io::Error::new(io::ErrorKind::BrokenPipe, "lost")) })
        .await
        .unwrap_err();
    assert_eq!(error.kind(), io::ErrorKind::BrokenPipe);
    let error = host(|| async { panic!("transport bug") })
        .await
        .unwrap_err();
    assert!(error.to_string().contains("panicked"));
}

// ---- The monitor's status wiring, extracted as `publish_status`. ----

fn initial() -> crate::session_native::service_client::SubscriptionInitial {
    crate::session_native::service_client::tests::fixture_initial()
        .unwrap()
        .1
}

#[test]
fn verified_fresh_status_reaches_the_feed_and_a_reconnect_changes_nothing() {
    let feed = ProfileFeed::new();
    let receiver = feed.subscribe();
    let ok = || Ok(());
    publish_status(&feed, &StatusUpdate::Reconnecting, ok).unwrap();
    assert!(receiver.borrow().is_none());

    publish_status(&feed, &StatusUpdate::Opened(initial()), ok).unwrap();
    assert_eq!(receiver.borrow().as_ref().unwrap().len(), 0);

    let mut observation = initial().into_observation();
    observation.snapshot = snapshot(json!([entry(1, GAME, 0)]));
    publish_status(&feed, &StatusUpdate::Event(observation), ok).unwrap();
    assert_eq!(receiver.borrow().as_ref().unwrap().len(), 1);

    // Managed keeps the last profiles through a reconnect.
    publish_status(&feed, &StatusUpdate::Reconnecting, ok).unwrap();
    assert_eq!(receiver.borrow().as_ref().unwrap().len(), 1);
}

#[test]
fn status_that_is_stale_or_from_a_lost_origin_is_not_published() {
    let feed = ProfileFeed::new();
    let receiver = feed.subscribe();
    let stale = initial();
    // Real time: the observation clock is not paused outside a runtime.
    std::thread::sleep(Duration::from_millis(2100));
    let error = publish_status(&feed, &StatusUpdate::Opened(stale), || Ok(())).unwrap_err();
    assert_eq!(error.kind(), io::ErrorKind::TimedOut);
    assert!(receiver.borrow().is_none());

    let error = publish_status(&feed, &StatusUpdate::Opened(initial()), || {
        Err(io::Error::new(
            io::ErrorKind::PermissionDenied,
            "origin lost",
        ))
    })
    .unwrap_err();
    assert_eq!(error.kind(), io::ErrorKind::PermissionDenied);
    let mut observation = initial().into_observation();
    observation.snapshot = snapshot(json!([entry(1, GAME, 0)]));
    publish_status(&feed, &StatusUpdate::Event(observation), || {
        Err(io::Error::other("origin lost"))
    })
    .unwrap_err();
    assert!(receiver.borrow().is_none());
}

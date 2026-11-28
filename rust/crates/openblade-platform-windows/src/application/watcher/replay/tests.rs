use std::sync::mpsc::sync_channel;

use openblade_application_reporter::{
    ActivationMode, ApplicationProfile, LEASE_INTERVAL, ReportApplicationContextRequest,
    ReportOutcome,
};

use super::*;

const GAME: &str = r"C:\Games\Game.exe";
const OTHER: &str = r"C:\Games\Other.exe";
const READY: Duration = Duration::from_secs(2);

fn start(script: &mut Script, mode: ActivationMode) -> ScriptedWatcher {
    let mut watcher = script.start(79, Guid([1; 16]), Arc::default()).unwrap();
    assert!(poll(&mut watcher, 0).is_none());
    watcher.wait_ready(READY).unwrap();
    watcher.observe_profiles(
        [GAME, OTHER]
            .into_iter()
            .enumerate()
            .map(|(index, path)| ApplicationProfile {
                id: Guid([index as u8 + 2; 16]),
                executable_path: path.into(),
                mode,
                enabled: true,
            })
            .collect(),
    );
    watcher
}

fn poll(watcher: &mut ScriptedWatcher, ticks: u32) -> Option<ReportApplicationContextRequest> {
    watcher.poll(LEASE_INTERVAL * ticks, Guid([ticks as u8 + 8; 16]))
}

fn expect(
    watcher: &mut ScriptedWatcher,
    ticks: u32,
    sequence: i64,
    path: Option<&str>,
) -> ReportApplicationContextRequest {
    let report = poll(watcher, ticks).expect("report");
    assert_eq!(report.sequence, sequence);
    assert_eq!(report.executable_path.as_deref(), path);
    report
}

#[test]
fn native_watcher_links_without_starting() {
    use super::super::WindowsForegroundWatcher;

    type Start = fn(i32, Guid, Arc<AtomicBool>) -> io::Result<WindowsForegroundWatcher>;
    std::hint::black_box(WindowsForegroundWatcher::start as Start);
}

#[test]
fn initial_observation_and_latest_callback_win_including_same_hwnd() {
    let mut script = Script::default();
    let game = script.process(1, 41, 1, GAME);
    let other = script.process(2, 42, 1, OTHER);
    script.focus(1); // No callback exists yet.
    let mut watcher = start(&mut script, ActivationMode::Foreground);
    expect(&mut watcher, 0, 1, Some(GAME));
    watcher.complete(1, ReportOutcome::Stored);

    script.announce(2); // Obsolete callback, even before its successor is delivered.
    assert!(poll(&mut watcher, 1).is_none());
    assert_eq!(other.handles(), 0);
    script.focus(1);
    expect(&mut watcher, 1, 2, Some(GAME));
    watcher.complete(2, ReportOutcome::Stored);
    script.state.borrow_mut().window = 2; // OS transition, callback still in transit.
    assert!(poll(&mut watcher, 2).is_none());
    script.focus(1);
    script.focus(2);
    script.supersede_during_lookup(2); // HWND is unchanged, revision must still win.
    assert!(poll(&mut watcher, 2).is_none());
    assert_eq!(other.handles(), 0);
    assert_eq!(game.handles(), 1);
    expect(&mut watcher, 2, 3, Some(OTHER));
    assert_eq!(game.handles(), 0);
    assert_eq!(other.handles(), 1);
    assert!(script.registered());
    watcher.shutdown().unwrap();
    assert!(!script.registered());
    assert_eq!(other.handles(), 0);
}

#[test]
fn exit_retires_exact_background_handle_and_reused_pid_needs_activation() {
    let mut script = Script::default();
    let first = script.process(1, 42, 1, GAME);
    script.focus(1);
    let mut watcher = start(&mut script, ActivationMode::WhileRunningAfterActivation);
    expect(&mut watcher, 0, 1, Some(GAME));
    watcher.complete(1, ReportOutcome::Stored);
    script.focus(0);
    assert!(poll(&mut watcher, 0).is_none());
    assert_eq!(first.handles(), 1);

    first.exit();
    let second = script.process(1, 42, 2, GAME);
    expect(&mut watcher, 0, 2, None); // No lease tick needed.
    assert_eq!(first.handles(), 0);
    assert_eq!(second.handles(), 0); // No PID reopen.
    watcher.complete(2, ReportOutcome::Stored);
    script.focus(1);
    expect(&mut watcher, 0, 3, Some(GAME));
    watcher.complete(3, ReportOutcome::Stored);
    expect(&mut watcher, 1, 4, Some(GAME));
    second.exit(); // Current foreground exact handle also retires.
    assert!(poll(&mut watcher, 1).is_none()); // Prior send still in flight.
    assert_eq!(second.handles(), 0);
    watcher.complete(4, ReportOutcome::Stored);
    expect(&mut watcher, 1, 5, None);
    watcher.shutdown().unwrap();
}

#[test]
fn a_locked_console_keeps_retained_processes_and_renewals() {
    let mut script = Script::default();
    let game = script.process(1, 42, 1, GAME);
    script.focus(1);
    let mut watcher = start(&mut script, ActivationMode::WhileRunningAfterActivation);
    expect(&mut watcher, 0, 1, Some(GAME));
    watcher.complete(1, ReportOutcome::Stored);
    script.focus(0);
    assert!(poll(&mut watcher, 0).is_none());

    // Managed keeps the tracked process and its five-second renewal through a
    // lock: only the foreground becomes unknown, and no clear is sent.
    script.active(false);
    assert!(poll(&mut watcher, 0).is_none());
    assert_eq!(game.handles(), 1);
    for tick in 1..=3 {
        expect(&mut watcher, tick, i64::from(tick) + 1, Some(GAME));
        watcher.complete(i64::from(tick) + 1, ReportOutcome::Stored);
        assert_eq!(game.handles(), 1);
    }
    // The same session returns: nothing is cleared or resent early.
    script.active(true);
    assert!(poll(&mut watcher, 3).is_none());
    assert_eq!(game.handles(), 1);
    expect(&mut watcher, 4, 5, Some(GAME));
    watcher.shutdown().unwrap();
    assert_eq!(game.handles(), 0);
}

#[test]
fn a_secure_desktop_switch_keeps_a_background_process_but_resamples_the_foreground() {
    let mut script = Script::default();
    let game = script.process(1, 42, 1, GAME);
    script.focus(1);
    let mut watcher = start(&mut script, ActivationMode::WhileRunningAfterActivation);
    expect(&mut watcher, 0, 1, Some(GAME));
    watcher.complete(1, ReportOutcome::Stored);
    script.focus(0);
    script.desktop_switch();
    assert!(poll(&mut watcher, 0).is_none());
    assert_eq!(game.handles(), 1);
    expect(&mut watcher, 1, 2, Some(GAME));
    watcher.complete(2, ReportOutcome::Stored);

    // A Foreground profile has no process to keep: the context ends, and a
    // desktop boundary survives coalescing with a later window notice.
    let mut foreground = Script::default();
    let first = foreground.process(1, 42, 1, GAME);
    foreground.focus(1);
    let mut other = start(&mut foreground, ActivationMode::Foreground);
    expect(&mut other, 0, 1, Some(GAME));
    other.complete(1, ReportOutcome::Stored);
    foreground.desktop_switch();
    foreground.focus(0);
    expect(&mut other, 0, 2, None);
    assert_eq!(first.handles(), 0);
    other.shutdown().unwrap();
    watcher.shutdown().unwrap();
}

#[test]
fn a_locked_foreground_profile_ends_as_managed_reports_an_unknown_foreground() {
    let mut script = Script::default();
    let game = script.process(1, 42, 1, GAME);
    script.focus(1);
    let mut watcher = start(&mut script, ActivationMode::Foreground);
    expect(&mut watcher, 0, 1, Some(GAME));
    watcher.complete(1, ReportOutcome::Stored);
    script.active(false);
    expect(&mut watcher, 0, 2, None);
    assert_eq!(game.handles(), 0);
    watcher.complete(2, ReportOutcome::Stored);
    // Unlocked on the same foreground window: it is sampled again.
    script.active(true);
    expect(&mut watcher, 0, 3, Some(GAME));
    watcher.shutdown().unwrap();
}

#[test]
fn a_different_session_forgets_every_process_and_clears_the_old_context() {
    let mut script = Script::default();
    let game = script.process(1, 42, 1, GAME);
    script.focus(1);
    let mut watcher = start(&mut script, ActivationMode::WhileRunningAfterActivation);
    expect(&mut watcher, 0, 1, Some(GAME));
    watcher.complete(1, ReportOutcome::Stored);
    script.focus(0);
    assert_eq!(game.handles(), 1);
    // Another user takes the console and the session id returns different.
    script.active(false);
    assert!(poll(&mut watcher, 0).is_none());
    script.session(2);
    script.active(true);
    expect(&mut watcher, 0, 2, None);
    assert_eq!(game.handles(), 0);
    watcher.shutdown().unwrap();
}

#[test]
fn bounded_startup_cancels_and_retires_a_late_registration() {
    for externally_cancelled in [false, true] {
        let script = Script::default();
        let registered = Arc::clone(&script.registration);
        let cancellation = Arc::new(AtomicBool::new(false));
        let (entered, starting) = sync_channel(1);
        let (release, blocked) = sync_channel(0);
        let mut watcher = ForegroundWatcher::spawn(
            ScriptedDesktop(Rc::clone(&script.state)),
            79,
            Guid([1; 16]),
            Arc::clone(&cancellation),
            move |_, _| {
                entered.send(()).unwrap();
                let _ = blocked.recv_timeout(READY);
                Ok(ScriptedRegistration::new(registered))
            },
        )
        .unwrap();
        starting.recv_timeout(READY).unwrap();
        let error = if externally_cancelled {
            let ready = Arc::clone(&cancellation);
            let cancel = thread::spawn(move || {
                thread::sleep(Duration::from_millis(10));
                ready.store(true, Ordering::Release);
            });
            let result = watcher.wait_ready(READY).unwrap_err();
            cancel.join().unwrap();
            result
        } else {
            watcher.wait_ready(Duration::from_millis(5)).unwrap_err()
        };
        assert_eq!(
            error.kind(),
            if externally_cancelled {
                io::ErrorKind::Interrupted
            } else {
                io::ErrorKind::TimedOut
            }
        );
        assert!(cancellation.load(Ordering::Acquire));
        assert!(poll(&mut watcher, 3).is_none());
        release.send(()).unwrap();
        watcher.shutdown().unwrap();
        assert!(!script.registered());
    }
}

#[test]
fn the_owner_is_woken_by_every_notice_and_by_the_worker_exit() {
    use std::sync::atomic::AtomicUsize;

    let mut script = Script::default();
    let _game = script.process(1, 42, 1, GAME);
    let mut watcher = start(&mut script, ActivationMode::Foreground);
    let woken = Arc::new(AtomicUsize::new(0));
    let counter = Arc::clone(&woken);
    watcher.set_notice(Arc::new(move || {
        counter.fetch_add(1, Ordering::AcqRel);
    }));
    script.focus(1);
    assert_eq!(woken.load(Ordering::Acquire), 1);
    script.desktop_switch();
    assert_eq!(woken.load(Ordering::Acquire), 2);
    script.announce(0);
    assert_eq!(woken.load(Ordering::Acquire), 3);
    watcher.shutdown().unwrap();
    // The registration thread's exit is also announced.
    assert_eq!(woken.load(Ordering::Acquire), 4);
}

#[test]
fn a_session_lost_during_the_lookup_accepts_no_sample_and_sends_nothing() {
    let mut script = Script::default();
    let game = script.process(1, 42, 1, GAME);
    script.focus(1);
    let mut watcher = start(&mut script, ActivationMode::Foreground);
    expect(&mut watcher, 0, 1, Some(GAME));
    watcher.complete(1, ReportOutcome::Stored);

    // The console locks while the new sample is being resolved. That poll must
    // not accept the half-resolved sample: an unknown foreground accepted from
    // it would report a clear that no authoritative observation supports.
    script.deactivate_during_lookup();
    script.focus(1);
    assert!(poll(&mut watcher, 0).is_none());
    assert_eq!(game.handles(), 0);
    // The next poll takes the locked path, where a Foreground profile ends.
    expect(&mut watcher, 0, 2, None);
    watcher.shutdown().unwrap();
}

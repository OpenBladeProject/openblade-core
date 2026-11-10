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
fn loss_retires_evidence_with_a_send_in_flight_and_blocks_renewals() {
    let mut script = Script::default();
    let game = script.process(1, 42, 1, GAME);
    script.focus(1);
    let mut watcher = start(&mut script, ActivationMode::WhileRunningAfterActivation);
    expect(&mut watcher, 0, 1, Some(GAME));
    script.active(false);
    script.focus(0);
    assert!(poll(&mut watcher, 1).is_none());
    assert_eq!(game.handles(), 0);
    watcher.complete(1, ReportOutcome::Stored);
    watcher.disconnected();
    watcher.reconnected();
    assert!(poll(&mut watcher, 3).is_none());

    script.active(true);
    expect(&mut watcher, 3, 2, None);
    watcher.complete(2, ReportOutcome::Stored);
    assert!(poll(&mut watcher, 4).is_none());
    script.focus(1);
    expect(&mut watcher, 4, 3, Some(GAME));
    watcher.complete(3, ReportOutcome::Stored);

    // A desktop boundary survives coalescing even if the session looks active again.
    script.desktop_switch();
    script.focus(0);
    expect(&mut watcher, 4, 4, None);
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

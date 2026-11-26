//! Scripted current-user caller for the development host's private v91 pipe.
//! No desktop, process, or native device observations are used here.

use std::{cell::Cell, future::Future, rc::Rc, time::Duration};

use openblade_application_reporter::{
    ActivationMode, ApplicationProfile, ApplicationProfileReporter, Foreground, Guid,
    LEASE_INTERVAL, ProcessHandle, ProcessIdentity, ReportApplicationContextRequest, ReportOutcome,
};
use openblade_platform_windows::application::watcher::replay::{Script, ScriptedWatcher};
use openblade_windows_host::reporter_pipe::{Response, VERSION, encode, guid_text, receive, send};
use serde_json::{Value, json};
use tokio::{
    net::windows::named_pipe::NamedPipeClient,
    sync::{mpsc, oneshot},
    time::{Instant, sleep, timeout},
};

use super::tests::{HANG_GUARD, open_pipe, runtime, test_id};
use super::{PREFIX, Phase, Session, fixture, prepare_integrated, run_prepared};

const STATUS_ID: &str = "11111111-2222-4333-8444-555555555555";
const PROFILE_ID: &str = "e4c7ac58-003c-4f40-adb3-76f2d9e6ea9e";
const BALANCED: i32 = 1;
const PERFORMANCE: i32 = 2;

// An exchange owns its connection, so canceling it drops the outstanding pipe.
// Completion carries the sequence of the send that produced it.
struct InFlight {
    pipe: NamedPipeClient,
    request: ReportApplicationContextRequest,
}

impl InFlight {
    async fn start(path: &str, request: ReportApplicationContextRequest) -> Self {
        let pipe = send(open_pipe(path).await, &encode(&request).unwrap())
            .await
            .unwrap();
        Self { pipe, request }
    }

    async fn complete(self, reporter: &mut Reporter) -> (ReportOutcome, String) {
        let (sequence, outcome, message) = self.finish().await;
        reporter.complete(sequence, outcome);
        (outcome, message)
    }

    async fn complete_watcher(self, watcher: &mut ScriptedWatcher) -> ReportOutcome {
        let (sequence, outcome, _) = self.finish().await;
        watcher.complete(sequence, outcome);
        outcome
    }

    async fn finish(self) -> (i64, ReportOutcome, String) {
        let (outcome, message) = receive(self.pipe, &guid_text(self.request.request_id))
            .await
            .unwrap()
            .report_outcome()
            .unwrap();
        (self.request.sequence, outcome, message)
    }
}

type Reporter = ApplicationProfileReporter<ScriptedProcess>;

struct ScriptedProcess {
    identity: ProcessIdentity,
    path: String,
    alive: Rc<Cell<bool>>,
}

impl ProcessHandle for ScriptedProcess {
    fn identity(&self) -> ProcessIdentity {
        self.identity
    }

    fn executable_path(&self) -> &str {
        &self.path
    }

    fn is_alive(&self) -> bool {
        self.alive.get()
    }
}

fn identity(generation: u64) -> ProcessIdentity {
    ProcessIdentity {
        pid: 42.try_into().unwrap(),
        generation: generation.try_into().unwrap(),
    }
}

fn poll(
    reporter: &mut Reporter,
    elapsed: Duration,
    request_number: u8,
    open: impl FnMut(ProcessIdentity) -> Option<ScriptedProcess>,
) -> Option<ReportApplicationContextRequest> {
    reporter.poll(elapsed, Guid([request_number; 16]), open)
}

async fn status(path: &str) -> Value {
    let request = json!({"operation": "getStatus", "version": VERSION, "requestId": STATUS_ID});
    let pipe = send(
        open_pipe(path).await,
        &serde_json::to_vec(&request).unwrap(),
    )
    .await
    .unwrap();
    let response = receive(pipe, STATUS_ID).await.unwrap();
    assert!(response.success);
    assert!(response.result.is_none() && response.error.is_none() && response.error_code.is_none());
    response.status.unwrap()
}

#[track_caller]
fn wait_status<'a>(
    path: &'a str,
    ready: impl Fn(&Value) -> bool + 'a,
) -> impl Future<Output = Value> + 'a {
    let location = std::panic::Location::caller();
    async move {
        let deadline = Instant::now() + HANG_GUARD;
        loop {
            let current = status(path).await;
            if ready(&current) {
                return current;
            }
            assert!(
                Instant::now() < deadline,
                "{location}: status never reached expected state; last message: {}",
                current["message"]
            );
            sleep(Duration::from_millis(25)).await;
        }
    }
}

fn selection(current: &Value, active: bool, desired: &Value, observed: i32) {
    assert_eq!(
        current["activeApplicationProfileId"],
        if active {
            json!(PROFILE_ID)
        } else {
            Value::Null
        },
    );
    assert_eq!(current["desiredState"]["performance"]["value"], *desired);
    assert_eq!(
        current["desiredState"]["performance"]["source"],
        if active { 2 } else { 1 }
    );
    assert_eq!(current["confirmedState"]["performance"], observed);
}

#[test]
fn report_codec_requires_explicit_null_and_correlated_result() {
    let request = ReportApplicationContextRequest {
        version: VERSION,
        request_id: Guid([0x11; 16]),
        instance_id: Guid([0x22; 16]),
        sequence: 1,
        executable_path: None,
    };
    assert_eq!(
        std::str::from_utf8(&encode(&request).unwrap()).unwrap(),
        r#"{"operation":"reportApplicationContext","version":91,"requestId":"11111111-1111-1111-1111-111111111111","instanceId":"22222222-2222-2222-2222-222222222222","sequence":1,"executablePath":null}"#,
    );

    let valid = r#"{"frame":"response","version":91,"requestId":"11111111-2222-4333-8444-555555555555","success":true,"result":{"status":0,"message":"stored"}}"#;
    let outcome =
        |payload: &str, id: &str| Response::decode(payload.as_bytes(), id)?.report_outcome();
    assert_eq!(outcome(valid, STATUS_ID).unwrap().0, ReportOutcome::Stored);
    assert!(outcome(valid, &guid_text(request.request_id)).is_err());
    for malformed in [
        valid.replace("91", "79"),
        valid.replace(r#""status":0"#, r#""status":4"#),
        valid.replace(r#""status":0"#, r#""status":0,"status":1"#),
        valid.replace(r#""success":true"#, r#""success":true,"error":"conflict""#),
        valid.replace(r#""success":true"#, r#""success":true,"status":{}"#),
        valid.replace(r#""success":true"#, r#""success":true,"unknown":null"#),
    ] {
        assert!(outcome(&malformed, STATUS_ID).is_err(), "{malformed}");
    }
}

#[test]
fn reporter_drives_unified_application_replay_over_the_private_pipe() {
    runtime().block_on(async {
        let id = test_id();
        let path = format!("{PREFIX}{id}");
        let (advance, steps) = mpsc::channel(1);
        let (release, renderer) = oneshot::channel();
        let (stop, stopped) = oneshot::channel();
        let (ready, started) = oneshot::channel();
        let mut ready = Some(ready);
        let host = run_prepared(
            &id,
            Session::Active,
            |shutdown| {
                // Keep the fixture's existing cleanup owner in this process.
                let (profiles, writer) = fixture::Fixture::create(true)?.compose_battery(false)?;
                prepare_integrated(
                    Some(steps),
                    Some(renderer),
                    shutdown,
                    profiles,
                    writer,
                    true,
                )
            },
            |phase| {
                if phase == Phase::Running {
                    ready.take().unwrap().send(()).unwrap();
                }
                Ok(())
            },
            async { stopped.await.unwrap() },
        );
        let caller = async {
            started.await.unwrap();
            exercise_reporter(&path, advance).await;
            stop.send(()).unwrap();
            release.send(()).unwrap();
            Ok(())
        };
        timeout(HANG_GUARD, async { tokio::try_join!(host, caller) })
            .await
            .expect("unified reporter test timed out")
            .unwrap();
    });
}

async fn exercise_reporter(path: &str, advance: mpsc::Sender<()>) {
    let initial = wait_status(path, |s| s["message"] == "Renderer frame in flight.").await;
    let rule = &initial["profiles"]["applicationProfiles"][0];
    assert_eq!(rule["id"], PROFILE_ID);
    assert_eq!(rule["activationMode"], 1);
    let executable = rule["executablePath"].as_str().unwrap();
    let power = &initial["profiles"]["pluggedIn"]["performance"];
    let profile = |mode| ApplicationProfile {
        id: Guid(0xe4c7ac58003c4f40adb376f2d9e6ea9e_u128.to_be_bytes()),
        executable_path: executable.to_owned(),
        mode,
        enabled: true,
    };
    let foreground = |process| Foreground::Executable {
        path: executable.to_owned(),
        process,
    };
    let mut reporter = Reporter::new(
        VERSION,
        Guid([0xA1; 16]),
        crate::application_runtime::same_path,
    );
    let mut elapsed = Duration::ZERO;

    reporter.observe_profiles(vec![profile(ActivationMode::Foreground)]);
    reporter.observe_foreground(foreground(None));
    let request = poll(&mut reporter, elapsed, 1, |_| None).unwrap();
    assert_eq!(request.executable_path.as_deref(), Some(executable));
    let active = InFlight::start(path, request)
        .await
        .complete(&mut reporter)
        .await;
    assert_eq!(active.0, ReportOutcome::Stored);
    selection(&status(path).await, true, &json!(PERFORMANCE), BALANCED);

    advance.send(()).await.unwrap();
    let applied = wait_status(path, |s| {
        s["message"]
            .as_str()
            .unwrap_or_default()
            .contains("confirmed by readback")
    })
    .await;
    selection(&applied, true, &json!(PERFORMANCE), PERFORMANCE);

    reporter.observe_foreground(Foreground::Unknown);
    let clear = poll(&mut reporter, elapsed, 2, |_| None).unwrap();
    assert!(clear.executable_path.is_none());
    assert_eq!(
        InFlight::start(path, clear)
            .await
            .complete(&mut reporter)
            .await
            .0,
        ReportOutcome::Stored
    );
    selection(&status(path).await, false, power, PERFORMANCE);

    let alive = Rc::new(Cell::new(true));
    let open = |process| {
        Some(ScriptedProcess {
            identity: process,
            path: executable.to_owned(),
            alive: Rc::clone(&alive),
        })
    };
    reporter.observe_profiles(vec![profile(ActivationMode::WhileRunningAfterActivation)]);
    reporter.observe_foreground(foreground(Some(identity(1))));
    let request = poll(&mut reporter, elapsed, 3, open).unwrap();
    let old_sequence = request.sequence;
    let instance = request.instance_id;
    let old = InFlight::start(path, request).await;
    wait_status(path, |s| s["activeApplicationProfileId"] == PROFILE_ID).await;

    // Deliver the old real pipe reply only after reconnect has a new send in flight.
    reporter.disconnected();
    reporter.observe_foreground(Foreground::Unknown);
    reporter.observe_exit(identity(2)); // Same PID, different generation.
    assert!(poll(&mut reporter, elapsed, 4, open).is_none());
    reporter.reconnected();
    let next = poll(&mut reporter, elapsed, 4, open).unwrap();
    assert_eq!(next.instance_id, instance);
    assert_eq!(next.sequence, old_sequence + 1);
    assert_ne!(next.request_id, old.request.request_id);
    assert_eq!(next.executable_path.as_deref(), Some(executable));
    let new = InFlight::start(path, next).await;
    assert_eq!(old.complete(&mut reporter).await.0, ReportOutcome::Stored);
    assert!(poll(&mut reporter, elapsed + LEASE_INTERVAL, 5, open).is_none());
    assert_eq!(
        new.complete(&mut reporter).await,
        (
            ReportOutcome::Stored,
            "The active application profile lease was renewed.".into()
        )
    );
    assert_eq!(Rc::strong_count(&alive), 2);

    alive.set(false);
    reporter.observe_exit(identity(1));
    let clear = poll(&mut reporter, elapsed, 5, open).unwrap();
    assert!(clear.executable_path.is_none());
    assert_eq!(Rc::strong_count(&alive), 1);
    InFlight::start(path, clear)
        .await
        .complete(&mut reporter)
        .await;
    selection(&status(path).await, false, power, PERFORMANCE);

    alive.set(true);
    reporter.observe_foreground(foreground(Some(identity(2))));
    let request = poll(&mut reporter, elapsed, 6, open).unwrap();
    let canceled = InFlight::start(path, request).await;
    wait_status(path, |s| s["activeApplicationProfileId"] == PROFILE_ID).await;
    reporter.disconnected();
    drop(canceled); // Retire the real connection before consuming its reply.
    reporter.reconnected();
    let request = poll(&mut reporter, elapsed, 7, open).unwrap();
    let sent_at = Instant::now();
    InFlight::start(path, request)
        .await
        .complete(&mut reporter)
        .await;

    sleep(LEASE_INTERVAL).await;
    elapsed += LEASE_INTERVAL;
    let renewal = poll(&mut reporter, elapsed, 8, open).unwrap();
    assert_eq!(
        InFlight::start(path, renewal)
            .await
            .complete(&mut reporter)
            .await,
        (
            ReportOutcome::Stored,
            "The active application profile lease was renewed.".into()
        ),
    );
    reporter.cancel();
    assert_eq!(Rc::strong_count(&alive), 1);
    assert!(poll(&mut reporter, elapsed + LEASE_INTERVAL, 9, open).is_none());

    tokio::time::sleep_until(sent_at + Duration::from_secs(16)).await;
    selection(&status(path).await, true, &json!(PERFORMANCE), PERFORMANCE);
    let expired = wait_status(path, |s| s["activeApplicationProfileId"].is_null()).await;
    selection(&expired, false, power, PERFORMANCE);
    assert!(
        expired["message"]
            .as_str()
            .unwrap()
            .contains("lease expired")
    );

    exercise_session_and_write_fence(path, advance, profile(ActivationMode::Foreground)).await;
}

async fn exercise_session_and_write_fence(
    path: &str,
    advance: mpsc::Sender<()>,
    profile: ApplicationProfile,
) {
    let advance = &advance;
    let step = |message| async move {
        advance.send(()).await.unwrap();
        wait_status(path, |s| {
            s["message"].as_str().unwrap_or_default().contains(message)
        })
        .await
    };
    // Advance to battery and then to the fixture's inactive session. The
    // same-user Windows peer token still passes the endpoint identity check.
    advance.send(()).await.unwrap();
    let inactive = step("no longer controls Windows").await;
    let executable = profile.executable_path.clone();
    let foreground = || Foreground::Executable {
        path: executable.clone(),
        process: None,
    };
    let mut reporter = Reporter::new(
        VERSION,
        Guid([0xA2; 16]),
        crate::application_runtime::same_path,
    );
    reporter.observe_profiles(vec![ApplicationProfile {
        id: profile.id,
        executable_path: executable.clone(),
        mode: profile.mode,
        enabled: profile.enabled,
    }]);
    reporter.observe_foreground(foreground());
    let request = poll(&mut reporter, Duration::ZERO, 10, |_| None).unwrap();
    let denied = InFlight::start(path, request)
        .await
        .complete(&mut reporter)
        .await;
    assert_eq!(denied.0, ReportOutcome::Rejected);
    assert!(
        denied
            .1
            .contains("active local interactive Windows session")
    );
    assert_eq!(status(path).await["revision"], inactive["revision"]);

    let resumed = step("controls Windows again").await;
    let mut script = Script::default();
    let process = script.process(1, 42, 1, &executable);
    script.focus(1);
    let mut watcher = script
        .start(VERSION, Guid([0xA3; 16]), Default::default())
        .unwrap();
    watcher.wait_ready(HANG_GUARD).unwrap();
    watcher.observe_profiles(vec![profile]);

    // A callback delivered during identity resolution supersedes the first
    // sample even with the same HWND. No request or sequence is published.
    script.supersede_during_lookup(1);
    assert!(watcher.poll(LEASE_INTERVAL, Guid([11; 16])).is_none());
    assert_eq!(process.handles(), 0);
    assert_eq!(status(path).await["revision"], resumed["revision"]);

    let request = watcher.poll(LEASE_INTERVAL, Guid([12; 16])).unwrap();
    assert_eq!(request.sequence, 1);
    assert_eq!(
        request.executable_path.as_deref(),
        Some(executable.as_str())
    );
    assert_eq!(
        InFlight::start(path, request)
            .await
            .complete_watcher(&mut watcher)
            .await,
        ReportOutcome::Stored
    );
    step("Full-adapter profile planned.").await;
    let full = step("Performance is already confirmed.").await;
    selection(&full, true, &json!(PERFORMANCE), PERFORMANCE);

    process.exit();
    let clear = watcher.poll(LEASE_INTERVAL, Guid([13; 16])).unwrap();
    assert_eq!(clear.sequence, 2);
    assert_eq!(clear.executable_path, None);
    assert_eq!(process.handles(), 0);
    assert_eq!(
        InFlight::start(path, clear)
            .await
            .complete_watcher(&mut watcher)
            .await,
        ReportOutcome::Stored
    );
    let uncertain = step("write is unconfirmed").await;
    assert_eq!(uncertain["writesBlocked"], true);
    assert_eq!(uncertain["unconfirmedWrites"].as_array().unwrap().len(), 1);

    let replacement = script.process(1, 42, 2, &executable);
    script.focus(1);
    let request = watcher.poll(LEASE_INTERVAL, Guid([14; 16])).unwrap();
    assert_eq!(request.sequence, 3);
    assert_eq!(
        InFlight::start(path, request)
            .await
            .complete_watcher(&mut watcher)
            .await,
        ReportOutcome::Stored
    );
    let retained = status(path).await;
    selection(&retained, true, &json!(PERFORMANCE), PERFORMANCE);
    assert_eq!(
        retained["unconfirmedWrites"],
        uncertain["unconfirmedWrites"]
    );
    assert_eq!(retained["writesBlocked"], true);

    let fenced = step("awaits read-only reconciliation").await;
    assert_eq!(fenced["unconfirmedWrites"], uncertain["unconfirmedWrites"]);
    selection(&fenced, true, &json!(PERFORMANCE), PERFORMANCE);
    advance.send(()).await.unwrap();
    let settled = wait_status(path, |s| s["unconfirmedWrites"] == json!([])).await;
    selection(&settled, true, &json!(PERFORMANCE), BALANCED);
    watcher.shutdown().unwrap();
    assert!(!script.registered());
    assert_eq!(replacement.handles(), 0);
}

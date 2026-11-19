use super::*;
use crate::{discovery::service_fixture, windows::pipe::Identity, wire::Status};
use serde_json::{Value, json};
use std::{rc::Rc, sync::Arc, time::Instant};
use windows_sys::Win32::{
    System::Services::{SERVICE_CONTROL_POWEREVENT, SERVICE_CONTROL_SESSIONCHANGE},
    UI::WindowsAndMessaging::{
        PBT_APMPOWERSTATUSCHANGE, PBT_APMRESUMEAUTOMATIC, PBT_APMSUSPEND, WTS_CONSOLE_CONNECT,
        WTS_SESSION_LOCK,
    },
};

use lifecycle::{Activity, Observation, PowerSource, Work, Worker};

#[test]
fn metadata_lifecycle_refreshes_fresh_evidence_and_drains_on_service_stop() {
    runtime().block_on(async {
        let controls = Controls::new();
        let id = test_id();
        let path = format!("{PREFIX}{id}");
        let peer = Identity::fixture_session().unwrap();
        let (ready, wait_ready) = oneshot::channel();
        let mut ready = Some(ready);
        let (draining, wait_draining) = oneshot::channel();
        let (release, wait_release) = oneshot::channel();
        let (applied, mut applied_work) = mpsc::channel(4);
        let current_phase = Cell::new(SERVICE_START_PENDING);
        let mut phases = Vec::new();

        let (worker, callbacks) = Worker::new();
        let callbacks = Arc::new(callbacks);
        let now = Instant::now();
        let missing = Observation {
            power: PowerSource::Unknown,
            console_session: None,
        };
        let matched = Observation {
            power: PowerSource::Battery,
            console_session: Some(peer),
        };
        let observed = Rc::new(Cell::new(missing));
        let reads = Cell::new(0);
        let (mut lease, script) = service_fixture::Lease::new();

        let host = run_service(
            &controls,
            controls.arm(),
            &id,
            // The private endpoint still checks the connecting token. The
            // Active projection only exposes this fixture's read-only replay.
            Session::Active,
            |shutdown| {
                let reader_gate = Arc::clone(&callbacks);
                let (status, mut publish) = Status::fixture()?
                    .with_metadata_replay(move || reader_gate.metadata_current())?;
                let mut stop_drain = Some((draining, wait_release));
                let callbacks = &callbacks;
                let reads = &reads;
                let observed = &observed;
                let drive = worker.run(
                    shutdown,
                    move || {
                        reads.set(reads.get() + 1);
                        observed.get()
                    },
                    async move |work| {
                        if work == Work::Stop {
                            publish(false);
                            let (draining, release) = stop_drain.take().unwrap();
                            return lease.stop(draining, release).await;
                        }

                        let verified = match work {
                            Work::Refresh(activity, evidence)
                                if activity != Activity::Suspended
                                    && evidence.power != PowerSource::Unknown
                                    && evidence.console_session == Some(peer) =>
                            {
                                lease.refresh()?
                            }
                            Work::Invalidate(_) | Work::Refresh(..) => {
                                lease.invalidate();
                                false
                            }
                            Work::Stop => unreachable!(),
                        };
                        let mut projected = false;
                        if !callbacks.publish_metadata(verified, |current| {
                            projected = current;
                            publish(current);
                        }) {
                            lease.invalidate();
                        }
                        applied.try_send((work, projected, lease.counts())).unwrap();
                        Ok(())
                    },
                );
                Ok((status, Some(drive)))
            },
            |status| {
                phases.push(phase(status));
                current_phase.set(status.dwCurrentState);
                if status.dwCurrentState == SERVICE_RUNNING {
                    ready.take().unwrap().send(()).unwrap();
                }
                Ok(())
            },
        );

        let client = async {
            let power = |event| callbacks.fake_control(SERVICE_CONTROL_POWEREVENT, event, now);
            let session = |event| callbacks.fake_control(SERVICE_CONTROL_SESSIONCHANGE, event, now);
            let mut revision = 0;
            wait_ready.await.unwrap();

            assert_eq!(
                applied_work.recv().await.unwrap(),
                (Work::Refresh(Activity::Unknown, missing), false, (0, 0))
            );
            check_status(&path, false, &mut revision).await;

            observed.set(matched);
            power(PBT_APMPOWERSTATUSCHANGE);
            assert_eq!(
                applied_work.recv().await.unwrap(),
                (Work::Refresh(Activity::Unknown, matched), true, (1, 0))
            );
            check_status(&path, true, &mut revision).await;

            script.defer_retirement();
            power(PBT_APMSUSPEND);
            assert!(!callbacks.metadata_current());
            power(PBT_APMPOWERSTATUSCHANGE);
            assert_eq!(
                applied_work.recv().await.unwrap(),
                (Work::Invalidate(Activity::Suspended), false, (1, 0))
            );
            check_status(&path, false, &mut revision).await;
            assert_eq!(reads.get(), 2);

            // Resume cannot reuse the console or power observed before sleep.
            observed.set(Observation {
                console_session: None,
                ..matched
            });
            power(PBT_APMRESUMEAUTOMATIC);
            assert_eq!(
                applied_work.recv().await.unwrap(),
                (
                    Work::Refresh(Activity::Active, observed.get()),
                    false,
                    (1, 0)
                )
            );
            check_status(&path, false, &mut revision).await;

            observed.set(matched);
            power(PBT_APMPOWERSTATUSCHANGE);
            assert_eq!(
                applied_work.recv().await.unwrap(),
                (Work::Refresh(Activity::Active, matched), true, (2, 1))
            );
            check_status(&path, true, &mut revision).await;

            // A session notification never changes the authenticated pipe
            // peer. A different fresh console cannot verify this fixture.
            let other = Observation {
                console_session: Some(if peer == 1 { 2 } else { 1 }),
                ..matched
            };
            observed.set(other);
            session(WTS_CONSOLE_CONNECT);
            assert_eq!(
                applied_work.recv().await.unwrap(),
                (Work::Invalidate(Activity::Active), false, (2, 2))
            );
            assert_eq!(
                applied_work.recv().await.unwrap(),
                (Work::Refresh(Activity::Active, other), false, (2, 2))
            );
            check_status(&path, false, &mut revision).await;

            // Inject a session change inside the next inert metadata open.
            // Its completed acquisition belongs to stale console evidence.
            script.on_open({
                let callbacks = Arc::clone(&callbacks);
                let observed = Rc::clone(&observed);
                move || {
                    observed.set(other);
                    callbacks.fake_control(SERVICE_CONTROL_SESSIONCHANGE, WTS_SESSION_LOCK, now);
                }
            });
            observed.set(matched);
            power(PBT_APMPOWERSTATUSCHANGE);
            assert_eq!(
                applied_work.recv().await.unwrap(),
                (Work::Refresh(Activity::Active, matched), false, (3, 3))
            );
            assert_eq!(
                applied_work.recv().await.unwrap(),
                (Work::Invalidate(Activity::Active), false, (3, 3))
            );
            assert_eq!(
                applied_work.recv().await.unwrap(),
                (Work::Refresh(Activity::Active, other), false, (3, 3))
            );
            check_status(&path, false, &mut revision).await;

            observed.set(matched);
            power(PBT_APMPOWERSTATUSCHANGE);
            assert_eq!(
                applied_work.recv().await.unwrap(),
                (Work::Refresh(Activity::Active, matched), true, (4, 3))
            );
            check_status(&path, true, &mut revision).await;

            // Unknown power replaces known evidence even when console matches.
            observed.set(Observation {
                power: PowerSource::Unknown,
                ..matched
            });
            power(PBT_APMPOWERSTATUSCHANGE);
            assert_eq!(
                applied_work.recv().await.unwrap(),
                (
                    Work::Refresh(Activity::Active, observed.get()),
                    false,
                    (4, 4)
                )
            );
            check_status(&path, false, &mut revision).await;

            observed.set(matched);
            power(PBT_APMPOWERSTATUSCHANGE);
            assert_eq!(
                applied_work.recv().await.unwrap(),
                (Work::Refresh(Activity::Active, matched), true, (5, 4))
            );
            check_status(&path, true, &mut revision).await;
            let denied = request(
                &path,
                json!({
                    "operation": "setFanOverride",
                    "value": {"value": 1, "lifetime": 1}
                }),
            )
            .await;
            assert_eq!(denied["success"], false);

            let mut idle_client = open_pipe(&path).await;
            idle_client.write_all(&[10]).await.unwrap();
            let before_stop = reads.get();
            script.defer_retirement();
            session(WTS_CONSOLE_CONNECT);
            assert_eq!(controls.handle(SERVICE_CONTROL_STOP), NO_ERROR);
            wait_draining.await.unwrap();
            assert_eq!(current_phase.get(), SERVICE_STOP_PENDING);
            let mut byte = [0];
            assert_closed(idle_client.read(&mut byte).await);
            session(WTS_SESSION_LOCK);
            assert_eq!(reads.get(), before_stop);
            assert!(applied_work.try_recv().is_err());
            assert!(!callbacks.metadata_current());
            assert_eq!(current_phase.get(), SERVICE_STOP_PENDING);
            release.send(()).unwrap();
        };

        timeout(Duration::from_secs(8), async {
            tokio::try_join!(host, async {
                client.await;
                Ok(())
            })
        })
        .await
        .expect("metadata lifecycle fixture did not reach STOP")
        .expect("private metadata service failed");
        assert_eq!(
            phases,
            [
                (SERVICE_START_PENDING, 0, NO_ERROR),
                (SERVICE_RUNNING, RUNNING_CONTROLS, NO_ERROR),
                (SERVICE_STOP_PENDING, 0, NO_ERROR),
                (SERVICE_STOPPED, 0, NO_ERROR),
            ]
        );
        assert_eq!(
            ClientOptions::new().open(&path).unwrap_err().kind(),
            io::ErrorKind::NotFound
        );
    });
}

async fn check_status(path: &str, verified: bool, revision: &mut i64) {
    let response = request(path, json!({"operation": "getStatus"})).await;
    assert_eq!(response["success"], true);
    let status = &response["status"];
    assert_eq!(status["deviceVerified"], verified);
    assert_eq!(status["writesBlocked"], true);
    for field in ["capabilities", "confirmedState", "liveReadings"] {
        assert_eq!(status[field], Value::Null);
    }
    let next = status["revision"].as_i64().unwrap();
    assert!(next > *revision);
    *revision = next;
}

async fn request(path: &str, mut payload: Value) -> Value {
    let mut pipe = open_pipe(path).await;
    payload["version"] = 80.into();
    payload["requestId"] = "11111111-2222-4333-8444-555555555555".into();
    let payload = serde_json::to_vec(&payload).unwrap();
    pipe.write_i32_le(payload.len() as i32).await.unwrap();
    pipe.write_all(&payload).await.unwrap();
    let len = pipe.read_i32_le().await.unwrap();
    assert!((1..=65_536).contains(&len));
    let mut payload = vec![0; len as usize];
    pipe.read_exact(&mut payload).await.unwrap();
    let response: Value = serde_json::from_slice(&payload).unwrap();
    assert_eq!(response["frame"], "response");
    assert_eq!(response["version"], 80);
    response
}

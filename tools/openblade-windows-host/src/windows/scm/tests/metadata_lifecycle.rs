use super::*;
use crate::{discovery::service_fixture, wire::Status};
use serde_json::{Value, json};

#[test]
fn metadata_lease_fences_reentry_and_drains_before_stopped() {
    runtime().block_on(async {
        let controls = Controls::new();
        let id = test_id();
        let path = format!("{PREFIX}{id}");
        let (ready, wait_ready) = oneshot::channel();
        let mut ready = Some(ready);
        let (steps, advance) = mpsc::channel(1);
        let (draining, wait_draining) = oneshot::channel();
        let (release, wait_release) = oneshot::channel();
        let current_phase = Cell::new(SERVICE_START_PENDING);
        let mut phases = Vec::new();

        let host = run_service(
            &controls,
            controls.arm(),
            &id,
            Session::Active,
            |shutdown| {
                let (status, publish) = Status::fixture()?.with_metadata_replay()?;
                let drive =
                    service_fixture::drive(advance, shutdown, draining, wait_release, publish);
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
            wait_ready.await.unwrap();
            let mut revision = 0;
            // Start disconnected, then acquire, remove, replace and lose observation.
            for (step, verified) in [false, true, false, true, false].into_iter().enumerate() {
                if step != 0 {
                    let (done, wait_done) = oneshot::channel();
                    steps.send(done).await.unwrap();
                    wait_done.await.unwrap();
                }
                let response = request(&path, json!({"operation": "getStatus"})).await;
                assert_eq!(response["success"], true);
                let status = &response["status"];
                assert_eq!(status["deviceVerified"], verified);
                assert_eq!(status["writesBlocked"], true);
                assert_eq!(status["capabilities"], Value::Null);
                assert_eq!(status["confirmedState"], Value::Null);
                assert_eq!(status["liveReadings"], Value::Null);
                let next = status["revision"].as_i64().unwrap();
                assert!(next > revision);
                revision = next;

                if step == 1 {
                    let denied = request(
                        &path,
                        json!({
                            "operation": "setFanOverride",
                            "value": {"value": 1, "lifetime": 1}
                        }),
                    )
                    .await;
                    assert_eq!(denied["success"], false);
                }
            }

            let mut idle_client = open_pipe(&path).await;
            idle_client.write_all(&[10]).await.unwrap();
            assert_eq!(controls.handle(SERVICE_CONTROL_STOP), NO_ERROR);
            wait_draining.await.unwrap();
            assert_eq!(current_phase.get(), SERVICE_STOP_PENDING);
            let mut byte = [0];
            assert_closed(idle_client.read(&mut byte).await);
            assert_eq!(current_phase.get(), SERVICE_STOP_PENDING);
            release.send(()).unwrap();
        };

        let (result, ()) = timeout(Duration::from_secs(8), async { tokio::join!(host, client) })
            .await
            .unwrap();
        result.unwrap();
        assert_eq!(
            phases,
            [
                (SERVICE_START_PENDING, 0, NO_ERROR),
                (
                    SERVICE_RUNNING,
                    SERVICE_ACCEPT_STOP | SERVICE_ACCEPT_SHUTDOWN,
                    NO_ERROR
                ),
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

async fn request(path: &str, mut payload: Value) -> Value {
    let mut pipe = open_pipe(path).await;
    payload["version"] = 79.into();
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
    assert_eq!(response["version"], 79);
    response
}

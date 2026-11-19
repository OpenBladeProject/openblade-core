//! The endpoint over scripted suppliers and in-memory pipes. No process is
//! sampled, opened, signalled or closed, and no pipe is created.
use super::*;
use openblade_platform_windows::gpu_apps::{ActionResult as Outcome, BlockReason, GpuApp};
use serde_json::{Value, json};
use std::{cell::Cell, collections::VecDeque, rc::Rc};
use tokio::io::{AsyncReadExt, AsyncWriteExt};

const ID: &str = "9f876742-1234-4321-abcd-d3ea3e1cf102";
const STALE: &str =
    "The process no longer holds GPU resources or its identity changed after confirmation.";
const ACTION_FAILED: &str = "Windows could not close this process.";
const NOT_ATTEMPTED: &str = "This process was not attempted because the GPU app operation stopped.";
const STOPPED: &str =
    "The GPU app operation stopped before all selected applications were processed.";
const UNCERTAIN: &str = "The close submission could not be confirmed.";
const SOME: &str = "Some applications could not be closed.";
const NONE: &str = "The selected applications could not be closed.";

/// Encode like the managed codec: the `operation` discriminator first.
fn request(mut body: Value) -> Vec<u8> {
    let operation = body.as_object_mut().unwrap().remove("operation").unwrap();
    let rest = serde_json::to_string(&body).unwrap();
    let separator = if rest == "{}" { "" } else { "," };
    raw(format!("{{\"operation\":{operation}{separator}{}", &rest[1..]).as_bytes())
}

fn raw(body: &[u8]) -> Vec<u8> {
    [(body.len() as i32).to_le_bytes().as_slice(), body].concat()
}

fn query() -> Vec<u8> {
    request(json!({"operation":"query","version":2,"requestId":ID}))
}

fn close(force: bool, targets: &[(i32, i64, &str)]) -> Vec<u8> {
    let targets: Vec<_> = targets
        .iter()
        .map(|(pid, ticks, name)| json!({"processId":pid,"startTimeUtcTicks":ticks,"name":name}))
        .collect();
    request(json!({
        "operation":"close","version":2,"requestId":ID,"force":force,"targets":targets
    }))
}

fn app(process_id: i32, name: &str, block_reason: Option<BlockReason>) -> GpuApp {
    GpuApp {
        process_id,
        start_time_utc_ticks: 638_900_000_000_000_000 + i64::from(process_id),
        name: name.into(),
        utilization_percent: 12.5,
        block_reason,
    }
}

fn inventory(apps: Vec<GpuApp>) -> GpuAppInventory {
    GpuAppInventory {
        apps,
        truncated: false,
        omissions: Default::default(),
    }
}

fn ticks(process_id: i32) -> i64 {
    638_900_000_000_000_000 + i64::from(process_id)
}

fn report(results: &[(i32, Option<&'static str>)], error: Option<&'static str>) -> Report {
    Report {
        results: results
            .iter()
            .map(|&(process_id, error)| Outcome {
                process_id,
                start_time_utc_ticks: ticks(process_id),
                error,
            })
            .collect(),
        error,
    }
}

#[derive(Clone, Debug, PartialEq)]
enum Call {
    Query,
    Close(Kind, Vec<i32>),
}

/// Scripted results in order. With `hold`, each call waits for cancellation
/// and stays in flight for a while after it, like a blocking worker.
#[derive(Default)]
struct Script {
    queries: VecDeque<Result<QueryResult, WorkerLost>>,
    closes: VecDeque<Result<CloseResult, WorkerLost>>,
    hold: bool,
    calls: Vec<Call>,
    started: Rc<Cell<bool>>,
    observed_cancel: Rc<Cell<bool>>,
    done: Rc<Cell<bool>>,
}

impl Script {
    async fn work(&self, cancelled: &AtomicBool) {
        self.started.set(true);
        if self.hold {
            while !cancelled.load(Ordering::Acquire) {
                sleep(Duration::from_millis(1)).await;
            }
            self.observed_cancel.set(true);
            sleep(Duration::from_millis(20)).await;
        }
        self.done.set(true);
    }
}

impl Supplier for Script {
    async fn query(&mut self, cancelled: Arc<AtomicBool>) -> Result<QueryResult, WorkerLost> {
        self.calls.push(Call::Query);
        self.work(&cancelled).await;
        self.queries.pop_front().expect("unscripted query")
    }

    async fn close(
        &mut self,
        targets: Vec<Target>,
        kind: Kind,
        cancelled: Arc<AtomicBool>,
    ) -> Result<CloseResult, WorkerLost> {
        self.calls.push(Call::Close(
            kind,
            targets.iter().map(|target| target.process_id).collect(),
        ));
        self.work(&cancelled).await;
        self.closes.pop_front().expect("unscripted close")
    }
}

/// One connection carrying `bytes`; returns the serve result and the reply.
async fn exchange(script: &mut Script, bytes: &[u8]) -> (Result<(), ServeError>, Option<Value>) {
    let (mut server, mut client) = tokio::io::duplex(256 * 1024);
    client.write_all(bytes).await.unwrap();
    let (_sender, mut stop) = watch::channel(false);
    let (result, reply) = tokio::join!(
        async {
            let result = serve(&mut server, &mut stop, script, |_| Ok(()), || Ok(())).await;
            drop(server);
            result
        },
        async {
            let mut bytes = Vec::new();
            client.read_to_end(&mut bytes).await.unwrap();
            bytes
        },
    );
    (result, decode(&reply))
}

fn decode(reply: &[u8]) -> Option<Value> {
    if reply.is_empty() {
        return None;
    }
    let (length, body) = reply.split_at(4);
    assert_eq!(
        i32::from_le_bytes(length.try_into().unwrap()) as usize,
        body.len()
    );
    assert!(body.len() <= MAX_FRAME as usize);
    Some(serde_json::from_slice(body).unwrap())
}

fn results(reply: &Value) -> Vec<(i64, Value)> {
    reply["results"]
        .as_array()
        .unwrap()
        .iter()
        .map(|result| {
            assert_eq!(result["succeeded"], result["error"].is_null());
            (
                result["processId"].as_i64().unwrap(),
                result["error"].clone(),
            )
        })
        .collect()
}

#[tokio::test]
async fn query_projects_the_managed_response() {
    let mut script = Script::default();
    script.queries.push_back(Ok(Ok(inventory(vec![
        app(41, "Game", None),
        app(42, "explorer", Some(BlockReason::Protected)),
    ]))));
    let upper = request(json!({"operation":"query","version":2,"requestId":ID.to_uppercase()}));
    let (result, reply) = exchange(&mut script, &upper).await;
    result.unwrap();
    assert_eq!(
        reply.unwrap(),
        json!({
            "version":2,"requestId":ID,"status":0,"error":null,
            "apps":[
                {"processId":41,"startTimeUtcTicks":ticks(41),"name":"Game","utilizationPercent":12.5,"canClose":true,"blockReason":null},
                {"processId":42,"startTimeUtcTicks":ticks(42),"name":"explorer","utilizationPercent":12.5,"canClose":false,
                 "blockReason":"OpenBlade does not close Windows or OpenBlade processes."}
            ],
            "results":[]
        })
    );
    assert_eq!(script.calls, [Call::Query]);
}

#[tokio::test]
async fn truncated_inventory_is_a_partial_query_with_the_first_128_apps() {
    let many = (1..=130).map(|pid| app(pid, "app", None)).collect();
    let mut flagged = inventory(vec![app(7, "app", None)]);
    flagged.truncated = true;
    for (inventory, count) in [(inventory(many), 128), (flagged, 1)] {
        let mut script = Script::default();
        script.queries.push_back(Ok(Ok(inventory)));
        let (result, reply) = exchange(&mut script, &query()).await;
        result.unwrap();
        let reply = reply.unwrap();
        assert_eq!(reply["status"], 1);
        assert_eq!(reply["error"], TRUNCATED);
        assert_eq!(reply["apps"].as_array().unwrap().len(), count);
        assert_eq!(
            reply["apps"][0]["processId"],
            if count == 1 { 7 } else { 1 }
        );
    }
}

#[tokio::test]
async fn unavailable_scans_map_to_managed_replies_and_cancellation_is_silent() {
    for (error, expected) in [
        (QueryUnavailable::Counters, Some(COUNTERS_UNAVAILABLE)),
        (
            QueryUnavailable::UnsupportedAdapter,
            Some(COUNTERS_UNAVAILABLE),
        ),
        (QueryUnavailable::AdapterChanged, Some(COUNTERS_UNAVAILABLE)),
        (QueryUnavailable::MissingData, Some(COUNTERS_UNAVAILABLE)),
        (
            QueryUnavailable::NativeCounter(5),
            Some(COUNTERS_UNAVAILABLE),
        ),
        (QueryUnavailable::CurrentUser, Some(DISPATCH_FAILURE)),
        (QueryUnavailable::NameComparison, Some(DISPATCH_FAILURE)),
        (QueryUnavailable::Cancelled, None),
    ] {
        for close_request in [false, true] {
            let mut script = Script::default();
            let bytes = if close_request {
                script.closes.push_back(Ok(Err(error)));
                close(false, &[(41, ticks(41), "Game")])
            } else {
                script.queries.push_back(Ok(Err(error)));
                query()
            };
            let (result, reply) = exchange(&mut script, &bytes).await;
            result.unwrap();
            assert_eq!(
                reply.as_ref().map(|reply| reply["error"].clone()),
                expected.map(Value::from),
                "{error:?}"
            );
            if let Some(reply) = reply {
                assert_eq!(reply["status"], 2);
                assert_eq!(reply["apps"], json!([]));
                assert_eq!(reply["results"], json!([]));
            }
        }
    }
}

#[tokio::test]
async fn close_reports_stale_action_and_unattempted_results_in_managed_order() {
    // Partial success: a stale confirmation, a closed app, a failed one.
    let mut script = Script::default();
    script.closes.push_back(Ok(Ok((
        inventory(vec![app(41, "Game", None), app(43, "Tool", None)]),
        report(
            &[(40, Some(STALE)), (41, None), (43, Some(ACTION_FAILED))],
            Some(SOME),
        ),
    ))));
    let targets = [
        (40, ticks(40), "Old"),
        (41, ticks(41), "Game"),
        (43, ticks(43), "Tool"),
    ];
    let (result, reply) = exchange(&mut script, &close(false, &targets)).await;
    result.unwrap();
    let reply = reply.unwrap();
    assert_eq!(reply["status"], 1);
    assert_eq!(reply["error"], SOME);
    assert_eq!(reply["apps"].as_array().unwrap().len(), 2);
    assert_eq!(
        results(&reply),
        [
            (40, Value::from(STALE)),
            (41, Value::Null),
            (43, Value::from(ACTION_FAILED))
        ]
    );
    assert_eq!(script.calls, [Call::Close(Kind::Close, vec![40, 41, 43])]);

    // Nothing closed is a failure; everything closed is a success.
    for (outcome, error, status) in [
        (report(&[(41, Some(STALE))], Some(NONE)), Some(NONE), 2),
        (report(&[(41, None)], None), None, 0),
    ] {
        let mut script = Script::default();
        script
            .closes
            .push_back(Ok(Ok((inventory(vec![app(41, "Game", None)]), outcome))));
        let (result, reply) = exchange(&mut script, &close(true, &[(41, ticks(41), "Game")])).await;
        result.unwrap();
        let reply = reply.unwrap();
        assert_eq!(reply["status"], status);
        assert_eq!(reply["error"], error.map_or(Value::Null, Value::from));
        assert_eq!(script.calls, [Call::Close(Kind::ForceClose, vec![41])]);
    }
}

/// The Tray force-closes each remaining app with its own request and sends
/// no query between them (`GpuAppCleanupCoordinator.RunAsync`). An
/// unconfirmed force close of one app reports only that app; the next app,
/// and a repeat of the first, are each attempted with a fresh scan.
#[tokio::test]
async fn an_unconfirmed_close_reports_its_own_app_and_later_closes_are_attempted() {
    let mut script = Script::default();
    script.closes.push_back(Ok(Ok((
        inventory(vec![app(41, "Game", None), app(42, "Other", None)]),
        report(&[(41, Some(UNCERTAIN))], Some(UNCERTAIN)),
    ))));
    script.closes.push_back(Ok(Ok((
        inventory(vec![app(41, "Game", None), app(42, "Other", None)]),
        report(&[(42, None)], None),
    ))));
    // The platform's fresh scan no longer lists the first app.
    script.closes.push_back(Ok(Ok((
        inventory(vec![]),
        report(&[(41, Some(STALE))], Some(NONE)),
    ))));
    let (result, reply) = exchange(&mut script, &close(true, &[(41, ticks(41), "Game")])).await;
    result.unwrap();
    let reply = reply.unwrap();
    assert_eq!(
        (reply["status"].clone(), reply["error"].clone()),
        (json!(2), json!(UNCERTAIN))
    );
    assert_eq!(results(&reply), [(41, Value::from(UNCERTAIN))]);

    let (result, reply) = exchange(&mut script, &close(true, &[(42, ticks(42), "Other")])).await;
    result.unwrap();
    let reply = reply.unwrap();
    assert_eq!(
        (reply["status"].clone(), reply["error"].clone()),
        (json!(0), Value::Null)
    );
    assert_eq!(results(&reply), [(42, Value::Null)]);

    let (result, reply) = exchange(&mut script, &close(true, &[(41, ticks(41), "Game")])).await;
    result.unwrap();
    assert_eq!(results(&reply.unwrap()), [(41, Value::from(STALE))]);
    assert_eq!(
        script.calls,
        [
            Call::Close(Kind::ForceClose, vec![41]),
            Call::Close(Kind::ForceClose, vec![42]),
            Call::Close(Kind::ForceClose, vec![41]),
        ]
    );
}

#[tokio::test]
async fn invalid_frames_close_the_connection_before_any_supplier_call() {
    let target = json!({"processId":41,"startTimeUtcTicks":ticks(41),"name":"Game"});
    let other = json!({"processId":42,"startTimeUtcTicks":ticks(42),"name":"Other"});
    let mut frames = vec![
        // Version mismatch: the managed codec refuses it before dispatch.
        request(json!({"operation":"query","version":1,"requestId":ID})),
        request(json!({"operation":"close","version":3,"requestId":ID,"force":false,"targets":[target]})),
        // Force with more than one target, also refused by the codec.
        request(json!({"operation":"close","version":2,"requestId":ID,"force":true,"targets":[target, other]})),
        request(json!({"operation":"close","version":2,"requestId":ID,"force":false,"targets":[target, target]})),
        request(json!({"operation":"close","version":2,"requestId":ID,"force":false,"targets":[]})),
        request(json!({"operation":"close","version":2,"requestId":ID,"force":null,"targets":[target]})),
        request(json!({"operation":"query","version":2,"requestId":"00000000-0000-0000-0000-000000000000"})),
        // Arrays for the request and for a target.
        raw(format!(r#"["query",2,"{ID}"]"#).as_bytes()),
        raw(format!(r#"{{"operation":"close","version":2,"requestId":"{ID}","force":false,"targets":[[41,{},"Game"]]}}"#, ticks(41)).as_bytes()),
        raw(b"{\"operation\":\"query\""),
        raw(b"not json"),
    ];
    // Lengths outside 1..=64 KiB.
    frames.push(0i32.to_le_bytes().to_vec());
    frames.push((MAX_FRAME + 1).to_le_bytes().to_vec());
    for bytes in frames {
        let mut script = Script::default();
        let (result, reply) = exchange(&mut script, &bytes).await;
        assert!(
            matches!(result, Err(ServeError::Connection(_))),
            "{}",
            String::from_utf8_lossy(&bytes)
        );
        assert!(reply.is_none());
        assert!(script.calls.is_empty());
    }
}

#[tokio::test]
async fn lost_workers_reply_for_a_query_and_end_the_endpoint_for_a_close() {
    let mut script = Script::default();
    script.queries.push_back(Err(WorkerLost));
    let (result, reply) = exchange(&mut script, &query()).await;
    result.unwrap();
    let reply = reply.unwrap();
    assert_eq!(
        (reply["status"].clone(), reply["error"].clone()),
        (json!(2), json!(DISPATCH_FAILURE))
    );

    script.closes.push_back(Err(WorkerLost));
    let (result, reply) = exchange(&mut script, &close(false, &[(41, ticks(41), "Game")])).await;
    assert!(matches!(result, Err(ServeError::Fatal(_))));
    assert!(reply.is_none());
}

/// Stop, the dispatch deadline, a disconnect, an extra byte and Origin loss
/// cancel the operation, wait for the worker and write nothing, whether the
/// worker reports unattempted results or an unconfirmed submission.
#[tokio::test(start_paused = true)]
async fn interruption_cancels_joins_the_worker_and_writes_no_reply() {
    for reason in ["stop", "deadline", "disconnect", "extra", "origin"] {
        for uncertain in [false, true] {
            let mut script = Script {
                hold: true,
                ..Script::default()
            };
            let outcome = if uncertain {
                report(
                    &[(41, Some(UNCERTAIN)), (42, Some(NOT_ATTEMPTED))],
                    Some(UNCERTAIN),
                )
            } else {
                report(&[(41, None), (42, Some(NOT_ATTEMPTED))], Some(STOPPED))
            };
            script
                .closes
                .push_back(Ok(Ok((inventory(vec![]), outcome))));
            let (started, observed, done) = (
                script.started.clone(),
                script.observed_cancel.clone(),
                script.done.clone(),
            );
            let (mut server, mut client) = tokio::io::duplex(8192);
            let targets = [(41, ticks(41), "Game"), (42, ticks(42), "Other")];
            client.write_all(&close(false, &targets)).await.unwrap();
            let (signal, mut stop) = watch::channel(false);
            let origin_live = Cell::new(true);
            let begun = tokio::time::Instant::now();
            let (result, reply) = tokio::join!(
                async {
                    let result = serve(
                        &mut server,
                        &mut stop,
                        &mut script,
                        |_| Ok(()),
                        || {
                            if origin_live.get() {
                                Ok(())
                            } else {
                                Err(io::Error::from_raw_os_error(5))
                            }
                        },
                    )
                    .await;
                    // The worker finished before serve returned.
                    assert!(done.get());
                    drop(server);
                    result
                },
                async {
                    while !started.get() {
                        sleep(Duration::from_millis(1)).await;
                    }
                    match reason {
                        "stop" => signal.send(true).unwrap(),
                        "disconnect" => client.shutdown().await.unwrap(),
                        "extra" => client.write_all(&[0]).await.unwrap(),
                        "origin" => origin_live.set(false),
                        "deadline" => (),
                        _ => unreachable!(),
                    }
                    let mut bytes = Vec::new();
                    client.read_to_end(&mut bytes).await.unwrap();
                    bytes
                },
            );
            assert!(observed.get(), "{reason}");
            assert!(reply.is_empty(), "{reason}");
            if reason == "deadline" {
                assert!(begun.elapsed() >= DISPATCH_TIMEOUT);
            } else {
                assert!(begun.elapsed() < DISPATCH_TIMEOUT, "{reason}");
            }
            if reason == "origin" {
                assert!(matches!(result, Err(ServeError::Fatal(_))));
            } else {
                result.unwrap();
            }
        }
    }
}

#[tokio::test]
async fn an_app_list_beyond_the_frame_keeps_its_ordered_prefix() {
    let name = "\u{e9}".repeat(260);
    let big = || {
        (1..=128)
            .map(|pid| app(pid, &name, None))
            .collect::<Vec<_>>()
    };
    let mut script = Script::default();
    script.queries.push_back(Ok(Ok(inventory(big()))));
    let (result, reply) = exchange(&mut script, &query()).await;
    result.unwrap();
    let reply = reply.unwrap();
    let apps = reply["apps"].as_array().unwrap();
    assert!(!apps.is_empty() && apps.len() < 128);
    assert_eq!(
        (reply["status"].clone(), reply["error"].clone()),
        (json!(1), json!(TRIMMED))
    );
    assert!(
        apps.iter()
            .enumerate()
            .all(|(i, app)| app["processId"] == i + 1)
    );

    // A close keeps its own status and every result.
    script
        .closes
        .push_back(Ok(Ok((inventory(big()), report(&[(1, None)], None)))));
    let (result, reply) = exchange(&mut script, &close(false, &[(1, ticks(1), &name)])).await;
    result.unwrap();
    let reply = reply.unwrap();
    assert_eq!(
        (reply["status"].clone(), reply["error"].clone()),
        (json!(0), Value::Null)
    );
    assert_eq!(results(&reply), [(1, Value::Null)]);
    assert!(reply["apps"].as_array().unwrap().len() < 128);
}

#[tokio::test]
async fn an_invalid_projected_app_is_a_failed_write_like_managed() {
    let mut bad = app(41, "Game", None);
    bad.utilization_percent = f64::NAN;
    let mut script = Script::default();
    script.queries.push_back(Ok(Ok(inventory(vec![bad]))));
    let (result, reply) = exchange(&mut script, &query()).await;
    assert!(matches!(result, Err(ServeError::Connection(_))));
    assert!(reply.is_none());
}

/// Each connection is served on its own; a connection error is contained;
/// Stop ends the loop without accepting again.
#[tokio::test]
async fn connections_are_served_in_turn_and_stop_ends_the_listener() {
    let mut script = Script::default();
    script.closes.push_back(Ok(Ok((
        inventory(vec![app(41, "Game", None)]),
        report(&[(41, Some(UNCERTAIN))], Some(UNCERTAIN)),
    ))));
    script.closes.push_back(Ok(Ok((
        inventory(vec![]),
        report(&[(41, Some(STALE))], Some(NONE)),
    ))));
    let requests = [
        close(true, &[(41, ticks(41), "Game")]),
        raw(b"[]"),
        close(true, &[(41, ticks(41), "Game")]),
    ];
    let mut servers = VecDeque::new();
    let mut clients = Vec::new();
    for bytes in &requests {
        let (server, mut client) = tokio::io::duplex(8192);
        client.write_all(bytes).await.unwrap();
        servers.push_back(server);
        clients.push(client);
    }
    let accepted = Cell::new(0);
    let (signal, mut stop) = watch::channel(false);
    let (result, replies) = tokio::join!(
        run_connections(
            &mut stop,
            &mut script,
            || {
                let next = servers.pop_front();
                accepted.set(accepted.get() + usize::from(next.is_some()));
                async move {
                    match next {
                        Some(server) => Ok(server),
                        None => std::future::pending().await,
                    }
                }
            },
            |_| Ok(()),
            || Ok(()),
        ),
        async {
            let mut replies = Vec::new();
            for mut client in clients {
                let mut bytes = Vec::new();
                client.read_to_end(&mut bytes).await.unwrap();
                replies.push(decode(&bytes));
            }
            signal.send(true).unwrap();
            replies
        },
    );
    result.unwrap();
    assert_eq!(accepted.get(), 3);
    assert_eq!(replies[0].as_ref().unwrap()["error"], UNCERTAIN);
    assert!(replies[1].is_none());
    assert_eq!(replies[2].as_ref().unwrap()["error"], NONE);
    assert_eq!(script.calls.len(), 2);
}

#[test]
fn replayed_managed_vectors_parse_like_the_managed_codec() {
    const REPLAY: &str =
        include_str!("../../../../../tests/OpenBlade.Session.Tests/WindowsReplay/v1/session.json");
    let replay: Value = serde_json::from_str(REPLAY).unwrap();
    let family = &replay["families"]["gpu"];
    assert_eq!(family["version"], VERSION);
    assert_eq!(family["maxMessageBytes"], MAX_FRAME);
    assert_eq!(family["pipeAffix"], "OpenBlade.Session.Gpu.v2");
    let mut checked = 0;
    for vector in family["requests"].as_array().unwrap() {
        let Some(input) = vector.get("input") else {
            continue;
        };
        let parsed = Request::parse(&request(input.clone())[4..]);
        assert_eq!(parsed.is_err(), vector.get("failure").is_some(), "{vector}");
        checked += 1;
    }
    assert_eq!(checked, 4);
    let close = Request::parse(&request(family["requests"][1]["input"].clone())[4..]).unwrap();
    assert_eq!(
        close,
        Request::Close {
            request_id: "11111111-2222-4333-8444-555555555555".into(),
            kind: Kind::Close,
            targets: vec![WireTarget {
                process_id: 123,
                start_time_utc_ticks: 638_900_000_000_000_001,
                name: "synthetic".into(),
            }],
        }
    );
}

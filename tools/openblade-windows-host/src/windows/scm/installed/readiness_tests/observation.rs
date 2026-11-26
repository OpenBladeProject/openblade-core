use super::*;

const REPLAY: &[u8] = include_bytes!("../../../../../tests/fixtures/observe-state-v91.frames");
const ID: &str = "11111111-2222-4333-8444-555555555555";

fn managed_frames() -> (&'static [u8], Value) {
    let length = u32::from_le_bytes(REPLAY[..4].try_into().unwrap()) as usize;
    let (request, response) = REPLAY.split_at(4 + length);
    (request, serde_json::from_slice(&response[4..]).unwrap())
}

struct ReadPort {
    answer: Option<Value>,
    reads: Rc<Cell<usize>>,
    control_session: Rc<Cell<Option<u32>>>,
    retire_session_on_read: bool,
    stall: bool,
    // The live-reading mask current while the observation was created.
    #[cfg(feature = "service-live-readings-v1")]
    mask: Rc<Cell<Option<u8>>>,
}

impl StatusSource for ReadPort {
    fn snapshot(&mut self, _: Caller, _: Instant) -> Pending<'_, Snapshot> {
        panic!("ObserveState must not fall back to GetStatus");
    }

    fn observe_performance(&mut self, _: Caller, _: Instant) -> Observation {
        self.reads.set(self.reads.get() + 1);
        #[cfg(feature = "service-live-readings-v1")]
        self.mask
            .set(Some(crate::service_status::requested_live_readings()));
        let answer = self.answer.take();
        let (control_session, retire, stall) = (
            self.control_session.clone(),
            self.retire_session_on_read,
            self.stall,
        );
        Box::pin(async move {
            if retire {
                control_session.set(None);
            }
            if stall {
                pending().await
            } else {
                answer
                    .map(serde_json::from_value)
                    .transpose()
                    .map_err(io::Error::from)
            }
        })
    }

    fn drain(&mut self) -> Pending<'_, ()> {
        Box::pin(async { Ok(()) })
    }
}

async fn serve(
    request: &[u8],
    caller: Caller,
    port: ReadPort,
) -> (Result<(), ExchangeError>, Vec<u8>) {
    let (mut client, server) = duplex(16_384);
    let active = port.control_session.clone();
    let source: Rc<Mutex<Box<dyn StatusSource>>> = Rc::new(Mutex::new(Box::new(port)));
    let server = test_stream_with_authority(
        server,
        caller,
        Fault::None,
        Rc::new(move || Ok(active.get())),
    );
    let served = serve_connection(server, source, None, Rc::new(Subscriptions::new(1)));
    let sent = async {
        client.write_all(request).await.unwrap();
        let mut reply = Vec::new();
        client.read_to_end(&mut reply).await.unwrap();
        reply
    };
    tokio::join!(served, sent)
}

fn refusal(reply: &[u8]) {
    let frame: Value = serde_json::from_slice(&reply[4..]).unwrap();
    assert_eq!(frame["frame"], "response");
    assert_eq!(frame["version"], 91);
    assert_eq!(frame["requestId"], ID);
    assert_eq!(frame["success"], false);
    assert!(frame["error"].as_str().is_some());
    assert_eq!(frame["status"], Value::Null);
    assert_eq!(frame["result"], Value::Null);
    assert_eq!(frame["errorCode"], Value::Null);
}

fn port(answer: Option<Value>) -> ReadPort {
    ReadPort {
        answer,
        reads: Rc::new(Cell::new(0)),
        control_session: Rc::new(Cell::new(Some(SETUP.session))),
        retire_session_on_read: false,
        stall: false,
        #[cfg(feature = "service-live-readings-v1")]
        mask: Rc::default(),
    }
}

#[tokio::test(start_paused = true)]
async fn in_memory_observe_state_dispatches_only_admitted_fresh_reads() {
    let (request, expected) = managed_frames();
    assert!(StatusRequest::decode_get(&request[4..]).is_err());
    let source = port(Some(expected["status"].clone()));
    let reads = source.reads.clone();
    let (result, reply) = serve(request, SETUP, source).await;
    result.unwrap();
    assert_eq!(reads.get(), 1);
    assert_eq!(
        serde_json::from_slice::<Value>(&reply[4..]).unwrap(),
        expected
    );

    let mut stale = expected["status"].clone();
    stale["writesBlocked"] = json!(true);
    for answer in [None, Some(stale)] {
        let (result, reply) = serve(request, SYSTEM, port(answer)).await;
        result.unwrap();
        refusal(&reply);
    }

    for logon in [Logon::Interactive, Logon::RemoteInteractive] {
        let source = port(Some(expected["status"].clone()));
        let reads = source.reads.clone();
        source.control_session.set(Some(SETUP.session + 1));
        let (result, reply) = serve(request, Caller { logon, ..SETUP }, source).await;
        result.unwrap();
        refusal(&reply);
        assert_eq!(reads.get(), 0);
    }

    let mut source = port(Some(expected["status"].clone()));
    source.retire_session_on_read = true;
    let (result, reply) = serve(request, SETUP, source).await;
    result.unwrap();
    refusal(&reply);
}

#[tokio::test(start_paused = true)]
async fn in_memory_observe_state_rejects_bad_selectors_and_bounds_a_stalled_source() {
    let (managed, expected) = managed_frames();
    let request: Value = serde_json::from_slice(&managed[4..]).unwrap();

    for (field, value) in [
        ("version", json!(78)),
        ("requestedLiveReadings", json!(16)),
        ("requestedLiveReadings", json!(-1)),
        ("effectiveLiveReadings", json!(1)),
        ("requestId", json!("00000000-0000-0000-0000-000000000000")),
    ] {
        let mut bad = request.clone();
        bad[field] = value;
        let frame = framed(&bad);
        let source = port(Some(expected["status"].clone()));
        let reads = source.reads.clone();
        let (result, reply) = serve(&frame, SYSTEM, source).await;
        assert_eq!(
            io::Error::from(result.unwrap_err()).kind(),
            io::ErrorKind::InvalidData
        );
        assert_eq!(reads.get(), 0);
        assert!(reply.is_empty());
    }

    // The older request omits both mask fields, so true still means All.
    let mut legacy = request.clone();
    legacy["includeLiveReadings"] = json!(true);
    legacy
        .as_object_mut()
        .unwrap()
        .remove("requestedLiveReadings");
    legacy
        .as_object_mut()
        .unwrap()
        .remove("effectiveLiveReadings");
    let source = port(Some(expected["status"].clone()));
    let reads = source.reads.clone();
    let (result, reply) = serve(&framed(&legacy), SYSTEM, source).await;
    result.unwrap();
    #[cfg(not(feature = "service-live-readings-v1"))]
    {
        refusal(&reply);
        assert_eq!(reads.get(), 0);
    }
    // With the live-reading read the legacy request is served like any
    // other observation, with every sensor asked for.
    #[cfg(feature = "service-live-readings-v1")]
    {
        let frame: Value = serde_json::from_slice(&reply[4..]).unwrap();
        assert_eq!(frame["success"], true);
        assert_eq!(reads.get(), 1);
    }

    let mut source = port(Some(expected["status"].clone()));
    source.stall = true;
    let (result, reply) = serve(managed, SYSTEM, source).await;
    result.unwrap();
    refusal(&reply);
}

fn framed(frame: &Value) -> Vec<u8> {
    let payload = serde_json::to_vec(frame).unwrap();
    [(payload.len() as i32).to_le_bytes().as_slice(), &payload].concat()
}

#[tokio::test]
async fn original_v79_wire_fixtures_are_rejected_before_dispatch() {
    for legacy in [
        include_bytes!("../../../../../tests/fixtures/get-status-v79.frames").as_slice(),
        include_bytes!("../../../../../tests/fixtures/observe-state-v79.frames").as_slice(),
        include_bytes!("../../../../../tests/fixtures/subscribe-status-v79.frames").as_slice(),
    ] {
        let size = u32::from_le_bytes(legacy[..4].try_into().unwrap()) as usize;
        let old_request = &legacy[4..size + 4];
        assert!(StatusRequest::decode(old_request).is_err());
        let source = port(None);
        let reads = source.reads.clone();
        let (result, response) = serve(&legacy[..size + 4], SYSTEM, source).await;
        assert_eq!(
            io::Error::from(result.unwrap_err()).kind(),
            io::ErrorKind::InvalidData
        );
        assert_eq!(reads.get(), 0);
        assert!(response.is_empty());
        let mut current: Value = serde_json::from_slice(old_request).unwrap();
        current["version"] = json!(91);
        let request = StatusRequest::decode(&serde_json::to_vec(&current).unwrap()).unwrap();
        let tail = &legacy[size + 4..];
        let size = u32::from_le_bytes(tail[..4].try_into().unwrap()) as usize;
        assert!(StatusResponse::verify(&tail[4..size + 4], &request).is_err());
    }
}

/// The served ObserveState's effective mask is current while the source
/// creates its observation, and only then; the advisory wire value plays no
/// part, as admission derives it from the request fields.
#[cfg(feature = "service-live-readings-v1")]
#[tokio::test(start_paused = true)]
async fn in_memory_observe_state_scopes_its_live_reading_mask() {
    let (managed, expected) = managed_frames();
    let request: Value = serde_json::from_slice(&managed[4..]).unwrap();
    assert_eq!(crate::service_status::requested_live_readings(), 0);
    for (include, requested, effective, mask) in [
        (false, 0, Some(0), 0),
        (false, 2, Some(0), 0),
        (true, 2, Some(2), 2),
        (true, 3, None, 3),
        (true, 15, Some(15), 15),
    ] {
        let mut live = request.clone();
        live["includeLiveReadings"] = json!(include);
        live["requestedLiveReadings"] = json!(requested);
        live["effectiveLiveReadings"] = json!(effective);
        let source = port(Some(expected["status"].clone()));
        let (reads, seen) = (source.reads.clone(), source.mask.clone());
        let (result, _) = serve(&framed(&live), SETUP, source).await;
        result.unwrap();
        assert_eq!(reads.get(), 1, "{live}");
        assert_eq!(seen.get(), Some(mask), "{live}");
    }
    assert_eq!(crate::service_status::requested_live_readings(), 0);
}

use super::*;

const REPLAY: &[u8] = include_bytes!("../../../../tests/fixtures/observe-state-v91.frames");

impl InstalledObservationAuthority for Authority {}

fn observation_frames() -> (&'static [u8], &'static [u8]) {
    let length = u32::from_le_bytes(REPLAY[..4].try_into().unwrap()) as usize;
    REPLAY.split_at(4 + length)
}

async fn replay_observation(
    response: &[u8],
    peer: impl Fn(&DuplexStream) -> io::Result<(u32, u32)>,
) -> io::Result<PerformanceObservation> {
    let authority = Authority::live();
    let (client, mut server) = duplex(16_384);
    let read = observe_on(
        Some(&authority),
        pending(),
        || Ok(REQUEST_ID.into()),
        async || Ok(client),
        peer,
    );
    let serve = async {
        let expected = observation_frames().0;
        let mut request = vec![0; expected.len()];
        server.read_exact(&mut request).await.unwrap();
        assert_eq!(request, expected, "request differs from managed codec");
        server.write_all(response).await.unwrap();
    };
    tokio::join!(read, serve).0
}

fn with_response(edit: impl FnOnce(&mut serde_json::Value)) -> Vec<u8> {
    let mut frame = serde_json::from_slice(&observation_frames().1[4..]).unwrap();
    edit(&mut frame);
    let bytes = serde_json::to_vec(&frame).unwrap();
    [(bytes.len() as i32).to_le_bytes().as_slice(), &bytes].concat()
}

#[tokio::test(start_paused = true)]
async fn in_memory_observe_state_returns_confirmed_managed_readback_until_expiry() {
    let observation = replay_observation(observation_frames().1, |_| Ok((PID, 0)))
        .await
        .unwrap();
    assert_eq!(
        observation.confirmed().unwrap(),
        Some(ConfirmedPerformance {
            preset: 2,
            active_power_source: 0
        })
    );
    advance(STATUS_BUDGET).await;
    assert_eq!(
        observation.confirmed().unwrap_err().kind(),
        io::ErrorKind::TimedOut
    );
}

#[tokio::test]
async fn in_memory_observe_state_does_not_confirm_retained_or_unobserved_fields() {
    use serde_json::{Value, json};

    let unknown = [
        ("deviceVerified", json!(false)),
        ("writesBlocked", json!(true)),
        ("controlAccess", json!(1)),
        ("activePowerSource", json!(3)),
        ("confirmedState", Value::Null),
        ("confirmedState", json!({"performance": null})),
        ("confirmedState", json!({"performance": 10})),
        ("confirmedState", json!({"performance": "Performance"})),
        ("observationReason", json!(2)),
        ("observationReason", json!(3)),
        ("unconfirmedWrites", json!([0])),
        ("unconfirmedWrites", json!([1, 1])),
        ("unconfirmedWrites", json!([3, 1])),
        ("unconfirmedWrites", Value::Null),
        ("fanStopStatus", json!({"state": 2})),
        ("fanStopStatus", json!({"state": 5})),
        ("fanStopStatus", json!({"state": 6})),
    ];
    for (field, value) in unknown {
        let reply = with_response(|frame| {
            frame["status"]["desiredState"]["performance"]["value"] = json!(2);
            frame["status"][field] = value;
        });
        let observation = replay_observation(&reply, |_| Ok((PID, 0))).await.unwrap();
        assert_eq!(observation.confirmed().unwrap(), None, "{field}");
    }
}

#[tokio::test]
async fn in_memory_observe_state_rejects_duplicate_confirmation_members() {
    let payload = std::str::from_utf8(&observation_frames().1[4..]).unwrap();
    for (original, duplicate) in [
        (
            r#""observationReason":0"#,
            r#""observationReason":2,"observationReason":0"#,
        ),
        (
            r#""unconfirmedWrites":[]"#,
            r#""unconfirmedWrites":[0],"unconfirmedWrites":[]"#,
        ),
        (
            r#""confirmedState":{"performance":2"#,
            r#""confirmedState":{"performance":null,"performance":2"#,
        ),
        (
            r#""fanStopStatus":{"requestedMode":0,"state":0"#,
            r#""fanStopStatus":{"requestedMode":0,"state":2,"state":0"#,
        ),
    ] {
        assert!(payload.contains(original), "{original}");
        let altered = payload.replacen(original, duplicate, 1);
        let reply = [
            (altered.len() as i32).to_le_bytes().as_slice(),
            altered.as_bytes(),
        ]
        .concat();
        let result = replay_observation(&reply, |_| Ok((PID, 0))).await;
        assert_eq!(result.err().unwrap().kind(), io::ErrorKind::InvalidData);
    }
}

#[tokio::test]
async fn in_memory_observe_state_rejects_invalid_or_lost_peer_replies() {
    use serde_json::{Value, json};

    let missing_authority = observe_on(
        None,
        pending(),
        || panic!("no ID without observation authority"),
        async || -> io::Result<DuplexStream> {
            panic!("no connection without observation authority")
        },
        |_| unreachable!(),
    )
    .await;
    assert_eq!(
        missing_authority.err().unwrap().kind(),
        io::ErrorKind::Unsupported
    );

    let refusal = with_response(|frame| {
        frame["success"] = json!(false);
        frame["error"] = json!("Fresh performance observation is unavailable.");
        frame["status"] = Value::Null;
    });
    let invalid = [
        (refusal, io::ErrorKind::Unsupported),
        (
            with_response(|frame| frame["status"] = Value::Null),
            io::ErrorKind::InvalidData,
        ),
        (
            with_response(|frame| frame["version"] = json!(78)),
            io::ErrorKind::InvalidData,
        ),
        (
            with_response(|frame| {
                frame["requestId"] = json!("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee")
            }),
            io::ErrorKind::InvalidData,
        ),
    ];
    for (reply, expected) in invalid {
        let error = replay_observation(&reply, |_| Ok((PID, 0)))
            .await
            .err()
            .unwrap();
        assert_eq!(error.kind(), expected);
    }
    let verified = Cell::new(false);
    let result = replay_observation(observation_frames().1, |_| {
        Ok((PID + u32::from(verified.replace(true)), 0))
    })
    .await;
    assert_eq!(
        result.err().unwrap().kind(),
        io::ErrorKind::PermissionDenied
    );
}

#[tokio::test(start_paused = true)]
async fn in_memory_observe_state_cancel_and_deadline_drop_partial_frame() {
    for cancel in [true, false] {
        let authority = Authority::live();
        let (client, mut server) = duplex(4096);
        let (stop, stopped) = oneshot::channel();
        let read = observe_on(
            Some(&authority),
            async {
                let _ = stopped.await;
            },
            || Ok(REQUEST_ID.into()),
            async || Ok(client),
            |_| Ok((PID, 0)),
        );
        let serve = async {
            assert_eq!(
                read_payload(&mut server).await.unwrap(),
                &observation_frames().0[4..]
            );
            server
                .write_all(&observation_frames().1[..6])
                .await
                .unwrap();
            let _keep_sender_alive = if cancel {
                stop.send(()).unwrap();
                None
            } else {
                Some(stop)
            };
            let mut trailing = Vec::new();
            server.read_to_end(&mut trailing).await.unwrap();
            assert!(trailing.is_empty());
        };
        let (result, ()) = tokio::join!(read, serve);
        let expected = if cancel {
            io::ErrorKind::Interrupted
        } else {
            io::ErrorKind::TimedOut
        };
        assert_eq!(result.err().unwrap().kind(), expected);
    }
}

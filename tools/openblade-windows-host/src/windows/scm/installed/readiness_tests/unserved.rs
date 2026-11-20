//! Managed's typed unsupported reply for known operations this composition
//! does not serve, and the unchanged fail-closed handling of everything else.
use super::*;
use tokio::io::AsyncReadExt;

const ID: &str = "CCCCCCCC-dddd-4eee-8fff-000000000001";

fn unserved_operations() -> Vec<String> {
    let contract: Value = serde_json::from_str(CONTRACT).unwrap();
    let operations: Vec<String> = contract["operationFamilies"]
        .as_object()
        .unwrap()
        .values()
        .flat_map(|family| family.as_array().unwrap())
        .map(|operation| operation.as_str().unwrap().to_owned())
        .filter(|operation| !StatusRequest::serves(operation))
        .collect();
    // Every installed composition serves at least the three status reads.
    assert!(operations.len() >= 42, "{}", operations.len());
    operations
}

fn source(events: &Events) -> Rc<Mutex<Box<dyn StatusSource>>> {
    Rc::new(Mutex::new(Box::new(TestSource {
        fault: Fault::None,
        events: events.clone(),
        publications: None,
    })))
}

async fn exchange_payload(
    payload: &[u8],
    caller: Caller,
    events: &Events,
) -> (Result<(), ExchangeError>, Vec<u8>) {
    let (mut client, server) = duplex(65_536);
    let served = serve_connection(
        test_stream(server, caller, Fault::None),
        source(events),
        None,
        Rc::new(Subscriptions::new(1)),
    );
    let sent = async {
        crate::service_frame::write_payload(&mut client, payload)
            .await
            .unwrap();
        let mut reply = Vec::new();
        client.read_to_end(&mut reply).await.unwrap();
        reply
    };
    tokio::join!(served, sent)
}

fn unsupported(id: &str) -> Value {
    json!({
        "frame": "response", "version": 85, "requestId": id.to_ascii_lowercase(),
        "success": false, "error": "IPC request operation is unsupported.",
        "status": null, "result": null, "archive": null, "errorCode": null,
    })
}

#[tokio::test]
async fn every_absent_managed_operation_gets_the_typed_unsupported_reply() {
    let inactive = Caller {
        control_session: Some(SETUP.session + 1),
        ..SETUP
    };
    let remote = Caller {
        logon: Logon::RemoteInteractive,
        ..SETUP
    };
    for operation in unserved_operations() {
        for caller in [SETUP, SYSTEM, inactive, remote] {
            let events = Events::default();
            // The body is never interpreted, including members that managed
            // would bind to typed hardware or profile intent.
            let request = json!({
                "operation": operation, "version": 85, "requestId": ID,
                "profiles": {"pluggedIn": {"performance": 1}}, "enabled": true,
            });
            let (result, reply) =
                exchange_payload(&serde_json::to_vec(&request).unwrap(), caller, &events).await;
            result.unwrap();
            let length = i32::from_le_bytes(reply[..4].try_into().unwrap());
            assert_eq!(length as usize, reply.len() - 4, "{operation}");
            assert_eq!(
                serde_json::from_slice::<Value>(&reply[4..]).unwrap(),
                unsupported(ID),
                "{operation}"
            );
            // Exactly one reply, with the managed discriminator first.
            assert!(reply[4..].starts_with(br#"{"frame":"response","#));
            assert!(events.borrow().is_empty(), "{operation} read the source");
        }
    }
}

#[tokio::test]
async fn unknown_malformed_and_refused_requests_still_close_without_a_reply() {
    let operation = unserved_operations().remove(0);
    let mut payloads = vec![
        json!({"operation": "notAManagedOperation", "version": 85, "requestId": ID}).to_string(),
        json!({"operation": operation, "version": 51, "requestId": ID}).to_string(),
        json!({"operation": operation, "version": 86, "requestId": ID}).to_string(),
        json!({"operation": operation, "version": 85, "requestId": "not-a-guid"}).to_string(),
        json!({"operation": operation, "version": 85}).to_string(),
        json!([operation, 85, ID]).to_string(),
        format!(r#"{{"operation":"{operation}","version":85,"version":85,"requestId":"{ID}"}}"#),
        format!(r#"{{"operation":"{operation}","version":85,"requestId":"{ID}""#),
    ];
    // A served operation that fails its own decoder is not reclassified.
    payloads.push(
        json!({"operation": "getStatus", "version": 85, "requestId": ID, "x": 1}).to_string(),
    );
    payloads.push(json!({"operation": "observeState", "version": 85, "requestId": ID}).to_string());
    for payload in payloads {
        let events = Events::default();
        let (result, reply) = exchange_payload(payload.as_bytes(), SETUP, &events).await;
        assert!(
            matches!(&result, Err(ExchangeError::Client(error)) if error.kind() == io::ErrorKind::InvalidData),
            "{payload}: {result:?}"
        );
        assert!(reply.is_empty(), "{payload}");
        assert!(events.borrow().is_empty(), "{payload}");
    }

    // A caller the status endpoint refuses is not answered either.
    let events = Events::default();
    let request = json!({"operation": operation, "version": 85, "requestId": ID});
    let other = Caller {
        logon: Logon::Other,
        ..SETUP
    };
    let (result, reply) =
        exchange_payload(&serde_json::to_vec(&request).unwrap(), other, &events).await;
    assert!(
        matches!(&result, Err(ExchangeError::Client(error)) if error.kind() == io::ErrorKind::PermissionDenied),
        "{result:?}"
    );
    assert!(reply.is_empty());
    assert!(events.borrow().is_empty());
}

#[tokio::test]
async fn concurrent_refusals_stay_correlated_beside_a_status_read() {
    let operations = unserved_operations();
    let events = Events::default();
    let refuse = |n: usize| {
        let id = format!("dddddddd-eeee-4fff-8aaa-{n:012}");
        let request = json!({"operation": operations[n], "version": 85, "requestId": id});
        let events = events.clone();
        async move {
            let (result, reply) =
                exchange_payload(&serde_json::to_vec(&request).unwrap(), SETUP, &events).await;
            result.unwrap();
            assert_eq!(
                serde_json::from_slice::<Value>(&reply[4..]).unwrap(),
                unsupported(&id)
            );
        }
    };
    let status = async {
        let request = json!({"operation": "getStatus", "version": 85, "requestId": ID});
        let (result, reply) =
            exchange_payload(&serde_json::to_vec(&request).unwrap(), SETUP, &events).await;
        result.unwrap();
        serde_json::from_slice::<Value>(&reply[4..]).unwrap()
    };
    let ((), (), (), status) = tokio::join!(refuse(0), refuse(1), refuse(2), status);
    assert_eq!(status["success"], true);
    assert_eq!(status["requestId"], ID.to_ascii_lowercase());
    // Only the status read reached the source.
    assert_eq!(*events.borrow(), ["snapshot"]);
}

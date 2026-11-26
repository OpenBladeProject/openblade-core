//! Installed `setDisplayBrightnessHidUsage` through the real framed
//! dispatcher, over a scripted display-brightness lane: no Windows call,
//! hardware, pipe or operating-system supplier is reached. Every case
//! asserts that no mailbox command was written and no file changed: managed
//! persists nothing for this input. The state machine's managed vectors are
//! replayed in `display_brightness_hid_tests.rs`.
use super::*;
use crate::installed::readiness::thermal_mutation::brightness_hid::{
    AVAILABILITY_AVAILABLE, DEVICE_REQUIRED, REMAINS_ASSERTED, SUBMITTED, fake,
};

const SESSION_REQUIRED: &str = "The active interactive Windows user is required.";

fn usage(n: u8, usage: u16) -> Value {
    json!({
        "operation": "setDisplayBrightnessHidUsage",
        "version": 91,
        "requestId": format!("aaaaaaaa-bbbb-cccc-ffff-{n:012}"),
        "usage": usage,
    })
}

fn usages(rig: &Rig) -> Vec<u16> {
    rig.brightness.lock().unwrap().usages.clone()
}

fn result(status: u8, message: &str) -> Value {
    json!({"status": status, "message": message})
}

/// No mailbox command and no file: the input reaches only the lane.
fn untouched(rig: &Rig, files: &std::collections::BTreeMap<String, Vec<u8>>) {
    assert_eq!(rig.port.state.lock().unwrap().writes, 0);
    assert_eq!(&*rig.files.bytes.lock().unwrap(), files);
}

/// One framed exchange: the server's result and the reply, empty when the
/// Service closes without answering.
async fn exchange_as(rig: &Rig, request: &Value, caller: Caller) -> (bool, Vec<u8>) {
    let (mut client, server) = duplex(65536);
    let source = rig.source.clone();
    let publications = source.lock().await.publications();
    let served = serve_connection(
        Box::new(SidStream {
            inner: test_stream(server, caller, Fault::None),
            sid: SID.to_owned(),
        }),
        source,
        publications,
        Rc::new(Subscriptions::new(2)),
    );
    let payload = serde_json::to_vec(request).unwrap();
    let exchange = async {
        crate::service_frame::write_payload(&mut client, &payload)
            .await
            .unwrap();
        let mut reply = Vec::new();
        tokio::io::AsyncReadExt::read_to_end(&mut client, &mut reply)
            .await
            .unwrap();
        reply
    };
    let (served, reply) = tokio::join!(served, exchange);
    (!matches!(served, Err(ExchangeError::Runtime(_))), reply)
}

#[tokio::test]
async fn a_held_usage_is_submitted_once_then_released_with_managed_replies() {
    let rig = Rig::new(MemoryFiles::default());
    rig.get().await;
    let files = rig.files.bytes.lock().unwrap().clone();
    for (n, (request, message)) in [
        (0x70, SUBMITTED),
        (0x70, REMAINS_ASSERTED),
        (0, SUBMITTED),
        (0x6F, SUBMITTED),
        (0x70, SUBMITTED),
        (0, SUBMITTED),
    ]
    .into_iter()
    .enumerate()
    {
        let response = rig.request(usage(n as u8, request)).await;
        assert_eq!(response["success"], true, "{response}");
        assert!(response["status"].is_null(), "no status follows");
        assert_eq!(response["result"], result(1, message), "{request}");
    }
    // A restarted Service neutralizes first; the opposite direction releases
    // before its press.
    assert_eq!(usages(&rig), [0, 0x70, 0, 0x6F, 0, 0x70, 0]);
    untouched(&rig, &files);
    rig.stop().await;
    // Nothing was held at Stop, so it releases nothing.
    assert_eq!(usages(&rig).len(), 7);
}

#[tokio::test]
async fn an_undefined_usage_is_closed_without_reaching_the_lane() {
    let rig = Rig::new(MemoryFiles::default());
    rig.get().await;
    let files = rig.files.bytes.lock().unwrap().clone();
    for undefined in [
        json!(1),
        json!(0x71),
        json!(65536),
        json!(-1),
        json!("0"),
        json!(null),
    ] {
        let mut request = usage(1, 0);
        request["usage"] = undefined;
        let (served, reply) = exchange_as(&rig, &request, USER).await;
        assert!(served && reply.is_empty(), "{request}");
    }
    assert!(usages(&rig).is_empty());
    assert_eq!(rig.entered.get(), 0);
    untouched(&rig, &files);
    rig.stop().await;
}

#[tokio::test]
async fn a_caller_that_does_not_control_the_session_reaches_nothing() {
    let rig = Rig::new(MemoryFiles::default());
    rig.get().await;
    let files = rig.files.bytes.lock().unwrap().clone();
    // Another interactive session is not served at all.
    let other = Caller {
        control_session: Some(USER.session + 1),
        ..USER
    };
    let (served, reply) = exchange_as(&rig, &usage(1, 0x6F), other).await;
    assert!(served && reply.is_empty());
    // SYSTEM is answered, without a user whose fresh admission it could take.
    let system = Caller {
        logon: Logon::System,
        ..USER
    };
    for request in [0x6F, 0] {
        let (served, reply) = exchange_as(&rig, &usage(2, request), system).await;
        assert!(served && reply.len() > 4);
        let reply: Value = serde_json::from_slice(&reply[4..]).unwrap();
        assert_eq!(reply["result"], result(2, SESSION_REQUIRED));
    }
    assert!(usages(&rig).is_empty());
    untouched(&rig, &files);
    rig.stop().await;
}

#[tokio::test]
async fn a_refused_fresh_admission_blocks_before_the_lane() {
    let rig = Rig::new(MemoryFiles::default());
    rig.get().await;
    let files = rig.files.bytes.lock().unwrap().clone();
    // A competing controller, then a changed console session.
    rig.custody.controller.store(true, Ordering::Release);
    assert_eq!(
        rig.request(usage(1, 0x6F)).await["result"],
        result(2, DEVICE_REQUIRED)
    );
    rig.custody.controller.store(false, Ordering::Release);
    let session = rig.custody.current_session.load(Ordering::Acquire);
    rig.custody
        .current_session
        .store(session + 1, Ordering::Release);
    assert_eq!(
        rig.request(usage(2, 0x70)).await["result"],
        result(2, DEVICE_REQUIRED)
    );
    rig.custody
        .current_session
        .store(session, Ordering::Release);
    // A release needs no device admission, as managed's does not.
    rig.custody.controller.store(true, Ordering::Release);
    assert_eq!(
        rig.request(usage(3, 0)).await["result"],
        result(1, SUBMITTED)
    );
    assert_eq!(usages(&rig), [0]);
    rig.custody.controller.store(false, Ordering::Release);
    // Admitted again, the press follows the neutralization already sent.
    assert_eq!(
        rig.request(usage(4, 0x6F)).await["result"],
        result(1, SUBMITTED)
    );
    assert_eq!(usages(&rig), [0, 0x6F]);
    untouched(&rig, &files);
    rig.stop().await;
    assert_eq!(usages(&rig), [0, 0x6F, 0]);
}

#[tokio::test]
async fn a_fatal_capture_fails_closed_and_is_retained() {
    let rig = Rig::new(MemoryFiles::default());
    rig.get().await;
    let files = rig.files.bytes.lock().unwrap().clone();
    rig.fatal_capture.store(true, Ordering::Release);
    let (served, reply) = exchange_as(&rig, &usage(1, 0x6F), USER).await;
    assert!(
        !served && reply.is_empty(),
        "a fatal capture fails the Service"
    );
    rig.fatal_capture.store(false, Ordering::Release);
    // The retained failure refuses a later release before the lane too.
    let (served, reply) = exchange_as(&rig, &usage(2, 0), USER).await;
    assert!(!served && reply.is_empty());
    assert!(usages(&rig).is_empty());
    untouched(&rig, &files);
}

#[tokio::test]
async fn an_unfinished_press_is_held_never_replayed_and_released_at_stop() {
    let rig = Rig::new(MemoryFiles::default());
    rig.get().await;
    let files = rig.files.bytes.lock().unwrap().clone();
    assert_eq!(
        rig.request(usage(1, 0)).await["result"],
        result(1, SUBMITTED)
    );
    let release = fake::hold(&rig.brightness);
    let started = std::time::Instant::now();
    let response = rig.request(usage(2, 0x70)).await;
    assert_eq!(response["result"]["status"], 3, "{response}");
    assert!(
        response["result"]["message"]
            .as_str()
            .unwrap()
            .ends_with("the submission did not finish within its budget"),
        "{response}"
    );
    assert!(started.elapsed() < Duration::from_secs(10));
    rig.brightness.lock().unwrap().hold = None;
    release();
    assert_eq!(
        rig.request(usage(3, 0x70)).await["result"],
        result(1, REMAINS_ASSERTED)
    );
    assert_eq!(usages(&rig), [0, 0x70]);
    untouched(&rig, &files);
    rig.stop().await;
    assert_eq!(usages(&rig), [0, 0x70, 0]);
}

#[tokio::test]
async fn stop_keeps_its_budget_when_the_release_does_not_finish() {
    let rig = Rig::new(MemoryFiles::default());
    rig.get().await;
    assert_eq!(
        rig.request(usage(1, 0x6F)).await["result"],
        result(1, SUBMITTED)
    );
    let release = fake::hold(&rig.brightness);
    let started = std::time::Instant::now();
    rig.stop().await;
    assert!(started.elapsed() < Duration::from_secs(5));
    assert_eq!(usages(&rig), [0, 0x6F, 0]);
    rig.brightness.lock().unwrap().hold = None;
    release();
}

#[tokio::test]
async fn a_restarted_service_neutralizes_before_its_first_press() {
    let files = MemoryFiles::default();
    let rig = Rig::new(files.clone());
    rig.get().await;
    assert_eq!(
        rig.request(usage(1, 0x6F)).await["result"],
        result(1, SUBMITTED)
    );
    // A Stop that could not release leaves the press unreleased.
    rig.brightness.lock().unwrap().writes.push_back(
        crate::installed::readiness::thermal_mutation::brightness_hid::WriteResult::failed(None),
    );
    rig.stop().await;
    assert_eq!(usages(&rig), [0, 0x6F, 0]);
    let before = files.bytes.lock().unwrap().clone();
    let rig = Rig::restarted(files.clone());
    rig.get().await;
    assert_eq!(
        rig.request(usage(2, 0x70)).await["result"],
        result(1, SUBMITTED)
    );
    assert_eq!(usages(&rig), [0, 0x70]);
    untouched(&rig, &before);
    rig.stop().await;
}

#[tokio::test]
async fn status_publishes_the_lanes_resolution_without_a_usage() {
    let rig = Rig::new(MemoryFiles::default());
    rig.brightness.lock().unwrap().availability = AVAILABILITY_AVAILABLE;
    let deadline = std::time::Instant::now() + Duration::from_secs(10);
    loop {
        let status = fresh_observe(&rig).await;
        let availability = &status["status"]["displayBrightnessHidAvailability"];
        if *availability == AVAILABILITY_AVAILABLE {
            break;
        }
        assert_eq!(*availability, 0, "Unknown until the lane resolves");
        assert!(std::time::Instant::now() < deadline, "the lane resolves");
        tokio::time::sleep(Duration::from_millis(5)).await;
    }
    // An unidentified host publishes Unknown whatever the lane resolved.
    rig.metadata.store(3, Ordering::Release);
    let status = fresh_observe(&rig).await;
    assert_eq!(status["status"]["displayBrightnessHidAvailability"], 0);
    assert!(usages(&rig).is_empty());
    rig.stop().await;
}

//! The Agent's application-context report exchange (managed
//! `ServiceClient.ReportApplicationContextAsync`) against an in-memory Service.
use std::cell::Cell;

use openblade_application_reporter::{Guid, ReportApplicationContextRequest, ReportOutcome};

use super::*;

fn request(path: Option<&str>) -> ReportApplicationContextRequest {
    ReportApplicationContextRequest {
        version: 85,
        request_id: Guid([0x11; 16]),
        instance_id: Guid([0x22; 16]),
        sequence: 7,
        executable_path: path.map(str::to_owned),
    }
}

fn reply(request_id: &str, edit: impl FnOnce(&mut serde_json::Value)) -> Vec<u8> {
    let mut frame = serde_json::json!({
        "frame": "response",
        "version": 85,
        "requestId": request_id,
        "success": true,
        "error": null,
        "status": null,
        "result": {"status": 1, "message": "Applied"},
        "archive": null,
        "errorCode": null,
    });
    edit(&mut frame);
    let payload = serde_json::to_vec(&frame).unwrap();
    [(payload.len() as i32).to_le_bytes().as_slice(), &payload].concat()
}

fn ok_reply(status: u8) -> Vec<u8> {
    reply("11111111-1111-1111-1111-111111111111", |frame| {
        frame["result"]["status"] = status.into();
    })
}

/// Run one report against a scripted server that sees the request payload.
async fn exchange(
    authority: &dyn InstalledServiceAuthority,
    request: &ReportApplicationContextRequest,
    peer: impl Fn(&DuplexStream) -> io::Result<(u32, u32)>,
    serve: impl AsyncFnOnce(DuplexStream, Vec<u8>),
) -> io::Result<ReportOutcome> {
    let (client, mut server) = duplex(16_384);
    let report = report_on(Some(authority), request, async || Ok(client), peer);
    let serving = async move {
        let mut length = [0; 4];
        if server.read_exact(&mut length).await.is_err() {
            // A refused exchange must not have written a byte.
            return serve(server, Vec::new()).await;
        }
        let mut payload = vec![0; i32::from_le_bytes(length) as usize];
        server.read_exact(&mut payload).await.unwrap();
        serve(server, payload).await;
    };
    tokio::join!(report, serving).0
}

#[tokio::test(start_paused = true)]
async fn request_is_the_managed_frame_and_only_stored_or_applied_acknowledge() {
    for (path, expected) in [
        (
            Some(r"C:\Games\Game.exe"),
            r#"{"operation":"reportApplicationContext","version":85,"requestId":"11111111-1111-1111-1111-111111111111","instanceId":"22222222-2222-2222-2222-222222222222","sequence":7,"executablePath":"C:\\Games\\Game.exe"}"#,
        ),
        // An explicit null clears the context; the member is never omitted.
        (
            None,
            r#"{"operation":"reportApplicationContext","version":85,"requestId":"11111111-1111-1111-1111-111111111111","instanceId":"22222222-2222-2222-2222-222222222222","sequence":7,"executablePath":null}"#,
        ),
    ] {
        let seen = Cell::new(false);
        let outcome = exchange(
            &Authority::live(),
            &request(path),
            |_| Ok((PID, 0)),
            async |mut server, payload| {
                assert_eq!(String::from_utf8(payload).unwrap(), expected);
                seen.set(true);
                server.write_all(&ok_reply(0)).await.unwrap();
            },
        )
        .await;
        assert!(seen.get());
        assert_eq!(outcome.unwrap(), ReportOutcome::Stored);
    }
    assert_eq!(
        exchange(
            &Authority::live(),
            &request(None),
            |_| Ok((PID, 0)),
            async |mut server, _| {
                server.write_all(&ok_reply(1)).await.unwrap();
            }
        )
        .await
        .unwrap(),
        ReportOutcome::Applied
    );
    // Blocked and Failed leave the report pending, exactly as managed.
    for status in [2, 3] {
        assert_eq!(
            exchange(
                &Authority::live(),
                &request(None),
                |_| Ok((PID, 0)),
                async |mut server, _| {
                    server.write_all(&ok_reply(status)).await.unwrap();
                }
            )
            .await
            .unwrap(),
            ReportOutcome::Rejected
        );
    }
}

#[tokio::test(start_paused = true)]
async fn refused_malformed_or_uncorrelated_replies_never_acknowledge() {
    let refused = reply("11111111-1111-1111-1111-111111111111", |frame| {
        frame["success"] = false.into();
        frame["result"] = serde_json::Value::Null;
        frame["error"] = "Application context was refused.".into();
    });
    assert_eq!(
        exchange(
            &Authority::live(),
            &request(None),
            |_| Ok((PID, 0)),
            async |mut server, _| {
                server.write_all(&refused).await.unwrap();
            }
        )
        .await
        .unwrap(),
        ReportOutcome::Rejected
    );
    let wrong_id = reply("99999999-9999-9999-9999-999999999999", |_| {});
    let unknown_status = ok_reply(9);
    let wrong_version = reply("11111111-1111-1111-1111-111111111111", |frame| {
        frame["version"] = 84.into();
    });
    let garbage = [4i32.to_le_bytes().as_slice(), b"nope"].concat();
    let oversized = [(70_000i32).to_le_bytes().as_slice(), &[0; 8]].concat();
    for bytes in [wrong_id, unknown_status, wrong_version, garbage, oversized] {
        let error = exchange(
            &Authority::live(),
            &request(None),
            |_| Ok((PID, 0)),
            async |mut server, _| {
                server.write_all(&bytes).await.unwrap();
            },
        )
        .await
        .unwrap_err();
        assert_eq!(error.kind(), io::ErrorKind::InvalidData);
    }
}

#[tokio::test(start_paused = true)]
async fn nothing_is_sent_to_a_peer_that_is_not_the_installed_service() {
    let sent = Cell::new(true);
    let error = exchange(
        &Authority::live(),
        &request(Some(r"C:\Games\Game.exe")),
        |_| Ok((PID + 1, 0)),
        async |_, payload| sent.set(!payload.is_empty()),
    )
    .await
    .unwrap_err();
    assert_eq!(error.kind(), io::ErrorKind::PermissionDenied);
    assert!(
        !sent.get(),
        "the foreground path reached an unverified peer"
    );

    // The generation is verified before the pipe is even opened.
    let authority = Authority::live();
    authority.pid.set(None);
    let connected = Cell::new(false);
    let error = report_on(
        Some(&authority),
        &request(None),
        async || {
            connected.set(true);
            Ok(duplex(64).0)
        },
        |_| Ok((PID, 0)),
    )
    .await
    .unwrap_err();
    assert_eq!(error.kind(), io::ErrorKind::PermissionDenied);
    assert!(!connected.get());
}

#[tokio::test(start_paused = true)]
async fn a_peer_that_changes_before_the_reply_is_not_trusted() {
    let checks = Cell::new(0);
    let error = exchange(
        &Authority::live(),
        &request(None),
        |_| {
            checks.set(checks.get() + 1);
            // Admitted before the send; replaced by the time the reply is read.
            Ok((if checks.get() == 1 { PID } else { PID + 1 }, 0))
        },
        async |mut server, _| server.write_all(&ok_reply(1)).await.unwrap(),
    )
    .await
    .unwrap_err();
    assert_eq!(error.kind(), io::ErrorKind::PermissionDenied);
    assert_eq!(checks.get(), 2);
}

struct Unavailable;

impl InstalledServiceAuthority for Unavailable {
    fn acquire_generation(
        &self,
    ) -> Result<Box<dyn InstalledServiceGeneration + '_>, ServiceAuthorityError> {
        Err(ServiceAuthorityError::Unavailable)
    }
}

#[tokio::test(start_paused = true)]
async fn a_stopped_or_restarting_service_fails_the_report_without_connecting() {
    let connected = Cell::new(false);
    let error = report_on(
        Some(&Unavailable),
        &request(None),
        async || {
            connected.set(true);
            Ok(duplex(64).0)
        },
        |_| Ok((PID, 0)),
    )
    .await
    .unwrap_err();
    assert_eq!(error.kind(), io::ErrorKind::WouldBlock);
    assert!(!connected.get());

    // The Service going away after it read the request fails this report; the
    // policy repeats it with a fresh sequence at the next renewal.
    let error = exchange(
        &Authority::live(),
        &request(None),
        |_| Ok((PID, 0)),
        async |server, payload| {
            assert!(!payload.is_empty());
            drop(server);
        },
    )
    .await
    .unwrap_err();
    assert_eq!(error.kind(), io::ErrorKind::UnexpectedEof);

    assert_eq!(
        report_on(
            None,
            &request(None),
            async || Ok(duplex(64).0),
            |_| Ok((PID, 0))
        )
        .await
        .unwrap_err()
        .kind(),
        io::ErrorKind::Unsupported
    );
}

#[tokio::test(start_paused = true)]
async fn a_silent_service_fails_at_the_shared_budget() {
    // The server end stays open and never replies.
    let (client, _server) = duplex(16_384);
    let authority = Authority::live();
    let started = Instant::now();
    let error = report_on(
        Some(&authority),
        &request(None),
        async || Ok(client),
        |_| Ok((PID, 0)),
    )
    .await
    .unwrap_err();
    assert_eq!(error.kind(), io::ErrorKind::TimedOut);
    assert_eq!(started.elapsed(), STATUS_BUDGET);
}

#[tokio::test(start_paused = true)]
async fn dropping_a_pending_report_closes_its_connection() {
    let (client, mut server) = duplex(16_384);
    let authority = Authority::live();
    let pending_request = request(Some(r"C:\Games\Game.exe"));
    let report = report_on(
        Some(&authority),
        &pending_request,
        async || Ok(client),
        |_| Ok((PID, 0)),
    );
    let mut report = Box::pin(report);
    let mut length = [0; 4];
    tokio::select! {
        _ = report.as_mut() => panic!("no reply was scripted"),
        read = server.read_exact(&mut length) => read.unwrap(),
    };
    let mut payload = vec![0; i32::from_le_bytes(length) as usize];
    server.read_exact(&mut payload).await.unwrap();
    drop(report);
    // The canceled report is never replayed: the server sees only the close.
    let mut rest = Vec::new();
    assert_eq!(server.read_to_end(&mut rest).await.unwrap(), 0);
}

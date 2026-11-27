#[cfg(feature = "agent-session-power-relay-v1")]
mod exchange;
mod observation;
#[cfg(feature = "agent-application-reporter-v1")]
mod report;

use std::{cell::Cell, future::pending};

use tokio::{
    io::{AsyncReadExt, AsyncWriteExt, DuplexStream, duplex},
    sync::oneshot,
    time::advance,
};

use super::*;
use crate::service_frame::read_payload;

mod subscription;
pub(in crate::session_native) use subscription::fixture_initial;

const FRAMES: &[u8] = include_bytes!("../../../tests/fixtures/get-status-v91.frames");
const REQUEST_ID: &str = "11111111-2222-4333-8444-555555555555";
const PID: u32 = 42;

struct Authority {
    pid: Cell<Option<NonZeroU32>>,
}

impl Authority {
    fn live() -> Self {
        Self {
            pid: Cell::new(NonZeroU32::new(PID)),
        }
    }
}

impl InstalledServiceAuthority for Authority {
    fn acquire_generation(
        &self,
    ) -> Result<Box<dyn InstalledServiceGeneration + '_>, ServiceAuthorityError> {
        let pid = self
            .pid
            .get()
            .ok_or_else(|| ServiceAuthorityError::Denied(io::ErrorKind::PermissionDenied.into()))?;
        Ok(Box::new(Generation {
            authority: self,
            pid,
        }))
    }
}

struct Generation<'a> {
    authority: &'a Authority,
    pid: NonZeroU32,
}

impl InstalledServiceGeneration for Generation<'_> {
    fn verify_live_process(&self) -> Result<NonZeroU32, ServiceAuthorityError> {
        if self.authority.pid.get() != Some(self.pid) {
            return Err(ServiceAuthorityError::Denied(
                io::ErrorKind::PermissionDenied.into(),
            ));
        }
        Ok(self.pid)
    }
}

fn frames() -> (&'static [u8], &'static [u8]) {
    let request_len = i32::from_le_bytes(FRAMES[..4].try_into().unwrap()) as usize;
    FRAMES.split_at(4 + request_len)
}

async fn replay(
    authority: &Authority,
    response: &[u8],
    peer: impl Fn(&DuplexStream) -> io::Result<(u32, u32)>,
) -> io::Result<StatusObservation> {
    let (client, mut server) = duplex(16_384);
    let read = read_on(
        Some(authority),
        pending(),
        || Ok(REQUEST_ID.into()),
        async || Ok(client),
        peer,
    );
    let serve = async move {
        let mut actual = vec![0; frames().0.len()];
        match server.read_exact(&mut actual).await {
            Ok(_) => {
                assert_eq!(actual, frames().0, "request differs from managed producer");
                server.write_all(response).await.unwrap();
            }
            Err(error) => {
                assert_eq!(error.kind(), io::ErrorKind::UnexpectedEof);
                assert!(
                    actual.iter().all(|&byte| byte == 0),
                    "unauthorized request bytes"
                );
            }
        }
    };
    tokio::join!(read, serve).0
}

fn response_with(edit: impl FnOnce(&mut serde_json::Value)) -> Vec<u8> {
    let mut response = serde_json::from_slice(&frames().1[4..]).unwrap();
    edit(&mut response);
    let payload = serde_json::to_vec(&response).unwrap();
    [(payload.len() as i32).to_le_bytes().as_slice(), &payload].concat()
}

fn error_kind(result: io::Result<StatusObservation>) -> io::ErrorKind {
    result.err().expect("status must be unavailable").kind()
}

#[tokio::test(start_paused = true)]
async fn accepts_managed_response_without_inventing_device_state() {
    let authority = Authority::live();
    let observation = replay(&authority, frames().1, |_| Ok((PID, 0)))
        .await
        .unwrap();
    let snapshot = serde_json::to_value(&observation.snapshot).unwrap();
    assert_eq!(snapshot["revision"], 1);
    assert_eq!(snapshot["deviceVerified"], false);
    assert_eq!(
        snapshot["confirmedState"]["hyperBoostActive"],
        serde_json::Value::Null
    );
    observation.require_fresh().unwrap();
    advance(STATUS_BUDGET).await;
    assert_eq!(
        observation.require_fresh().unwrap_err().kind(),
        io::ErrorKind::TimedOut
    );

    // An independently verified Service can legitimately project read-only access.
    let response = response_with(|frame| frame["status"]["controlAccess"] = 1.into());
    let observation = replay(&authority, &response, |_| Ok((PID, 0)))
        .await
        .unwrap();
    assert_eq!(
        serde_json::to_value(observation.snapshot).unwrap()["controlAccess"],
        1
    );
}

#[tokio::test]
async fn rejects_uncorrelated_failed_and_partial_snapshots() {
    let invalid_responses = [
        response_with(|frame| frame["version"] = 51.into()),
        response_with(|frame| frame["version"] = 79.into()),
        response_with(|frame| frame["requestId"] = "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee".into()),
        response_with(|frame| frame["frame"] = "statusEvent".into()),
        response_with(|frame| frame["success"] = false.into()),
        response_with(|frame| frame["status"] = serde_json::Value::Null),
        response_with(|frame| frame["status"] = serde_json::json!({"revision": 1})),
        response_with(|frame| frame["status"]["controlAccess"] = 9.into()),
        [1i32.to_le_bytes().as_slice(), b"{"].concat(),
        65_537i32.to_le_bytes().to_vec(),
    ];
    let authority = Authority::live();
    for response in invalid_responses {
        assert_eq!(
            error_kind(replay(&authority, &response, |_| Ok((PID, 0))).await),
            io::ErrorKind::InvalidData,
        );
    }
    for bytes in [&frames().1[..3], &frames().1[..5]] {
        assert_eq!(
            error_kind(replay(&authority, bytes, |_| Ok((PID, 0))).await),
            io::ErrorKind::UnexpectedEof,
        );
    }
}

#[tokio::test]
async fn missing_or_untrusted_authority_never_submits_status() {
    assert_eq!(
        error_kind(get_status(None, pending()).await),
        io::ErrorKind::Unsupported,
    );
    let authority = Authority {
        pid: Cell::new(None),
    };
    let result = read_on(
        Some(&authority),
        pending(),
        || panic!("no ID without authority"),
        async || -> io::Result<DuplexStream> { panic!("no connection without authority") },
        |_| unreachable!(),
    )
    .await;
    assert_eq!(error_kind(result), io::ErrorKind::PermissionDenied);

    let authority = Authority::live();
    for wrong_peer in [(PID + 1, 0), (PID, 3)] {
        assert_eq!(
            error_kind(replay(&authority, frames().1, |_| Ok(wrong_peer)).await),
            io::ErrorKind::PermissionDenied,
        );
    }
}

#[tokio::test]
async fn discards_valid_response_if_peer_or_authority_changes_after_submission() {
    for lose_authority in [false, true] {
        let authority = Authority::live();
        let checked = Cell::new(false);
        let result = replay(&authority, frames().1, |_| {
            if checked.replace(true) {
                if lose_authority {
                    authority.pid.set(None);
                } else {
                    return Ok((PID + 1, 0));
                }
            }
            Ok((PID, 0))
        })
        .await;
        assert_eq!(error_kind(result), io::ErrorKind::PermissionDenied);
    }
}

#[tokio::test(start_paused = true)]
async fn cancel_and_deadline_drop_in_flight_partial_frame() {
    for cancel in [true, false] {
        let authority = Authority::live();
        let (client, mut server) = duplex(4096);
        let (stop, stopped) = oneshot::channel();
        let read = read_on(
            Some(&authority),
            async {
                let _ = stopped.await;
            },
            || Ok(REQUEST_ID.into()),
            async || Ok(client),
            |_| Ok((PID, 0)),
        );
        let serve = async move {
            read_payload(&mut server).await.unwrap();
            server.write_all(&frames().1[..5]).await.unwrap();
            if cancel {
                stop.send(()).unwrap();
            } else {
                advance(STATUS_BUDGET).await;
                assert_eq!(server.read(&mut [0]).await.unwrap(), 0);
                drop(stop);
                return;
            }
            assert_eq!(server.read(&mut [0]).await.unwrap(), 0);
        };
        let (result, ()) = tokio::join!(read, serve);
        assert_eq!(
            error_kind(result),
            if cancel {
                io::ErrorKind::Interrupted
            } else {
                io::ErrorKind::TimedOut
            },
        );
    }

    let result = read_on(
        Some(&Authority::live()),
        std::future::ready(()),
        || panic!("no request ID after cancellation"),
        async || -> io::Result<DuplexStream> { panic!("no connection after cancellation") },
        |_| unreachable!(),
    )
    .await;
    assert_eq!(error_kind(result), io::ErrorKind::Interrupted);
}

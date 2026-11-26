//! Service IPC envelope framing shared by the fixture and installed roles.
use std::{io, time::Duration};

use serde::Serialize;
use tokio::io::{AsyncRead, AsyncReadExt, AsyncWrite, AsyncWriteExt};
use tokio::time::timeout;

/// The Service IPC protocol every native role and fixture speaks, matching
/// managed `OpenBladeIpc.ProtocolVersion`.
pub(crate) const PROTOCOL_VERSION: i32 = 91;
const MAX_FRAME_BYTES: i32 = 65_536;
pub(crate) const FRAME_TIMEOUT: Duration = Duration::from_secs(2);
/// The deadline of a request that may reconcile saved intent
/// (`reportApplicationContext`, `updateProfiles`): a performance step and a
/// fan step after the status snapshot. The Service sizes it next to its
/// recipe budget; the Agent's report waits for it and one frame more.
#[cfg(any(
    feature = "service-profile-triggers-v1",
    feature = "agent-application-reporter-v1"
))]
// The wire fixture binary includes this module too and reads neither role's
// budget.
#[allow(dead_code)]
pub(crate) const RECONCILE_BUDGET: Duration = Duration::from_secs(11);
/// How long past its deadline the Service waits for a reconciliation step
/// whose transfer has not retired before it answers without that step. It is
/// under one frame, so the reply still arrives within the Agent's wait.
#[cfg(feature = "service-profile-triggers-v1")]
pub(crate) const RECONCILE_MARGIN: Duration = Duration::from_secs(1);
#[cfg(feature = "service-profile-triggers-v1")]
const _: () = assert!(RECONCILE_MARGIN.as_nanos() < FRAME_TIMEOUT.as_nanos());

pub(crate) fn is_guid(id: &str) -> bool {
    id.len() == 36
        && id.bytes().enumerate().all(|(index, byte)| match index {
            8 | 13 | 18 | 23 => byte == b'-',
            _ => byte.is_ascii_hexdigit(),
        })
        && id != "00000000-0000-0000-0000-000000000000"
}

pub(crate) async fn read_payload(
    io: &mut (impl AsyncRead + Unpin + ?Sized),
) -> io::Result<Vec<u8>> {
    let length = io.read_i32_le().await?;
    if !(1..=MAX_FRAME_BYTES).contains(&length) {
        return Err(invalid("invalid frame length"));
    }
    let mut payload = vec![0; length as usize];
    io.read_exact(&mut payload).await?;
    Ok(payload)
}

pub(crate) fn encode(frame: &impl Serialize) -> io::Result<Vec<u8>> {
    let payload = serde_json::to_vec(frame)?;
    frame_length(&payload)?;
    Ok(payload)
}

pub(crate) async fn write_frame(
    io: &mut (impl AsyncWrite + Unpin + ?Sized),
    frame: &impl Serialize,
) -> io::Result<()> {
    write_payload(io, &encode(frame)?).await
}

pub(crate) async fn write_payload(
    io: &mut (impl AsyncWrite + Unpin + ?Sized),
    payload: &[u8],
) -> io::Result<()> {
    let length = frame_length(payload)?;
    timeout(FRAME_TIMEOUT, async {
        io.write_i32_le(length).await?;
        io.write_all(payload).await?;
        io.flush().await
    })
    .await?
}

fn frame_length(payload: &[u8]) -> io::Result<i32> {
    i32::try_from(payload.len())
        .ok()
        .filter(|length| (1..=MAX_FRAME_BYTES).contains(length))
        .ok_or_else(|| invalid("invalid frame length"))
}

/// A response that may carry a diagnostic archive, serialized and checked
/// against managed `MaximumDiagnosticResponseBytes` instead of the 64 KiB
/// bound. Encoding a 16 MiB archive is CPU work, so a caller builds this off
/// the runtime thread and the runtime only writes it.
#[cfg(feature = "service-diagnostics-export-v1")]
// The wire fixture binary includes this module too and serves no export.
#[allow(dead_code)]
pub(crate) struct ArchiveFrame {
    length: i32,
    payload: Vec<u8>,
}

#[cfg(feature = "service-diagnostics-export-v1")]
#[allow(dead_code)]
pub(crate) fn encode_archive_frame(frame: &impl Serialize) -> io::Result<ArchiveFrame> {
    let payload = serde_json::to_vec(frame)?;
    // Managed `OpenBladeIpc.MaximumDiagnosticResponseBytes`.
    const MAX_ARCHIVE_RESPONSE_BYTES: i32 = 24 * 1024 * 1024;
    let length = i32::try_from(payload.len())
        .ok()
        .filter(|length| (1..=MAX_ARCHIVE_RESPONSE_BYTES).contains(length))
        .ok_or_else(|| invalid("invalid frame length"))?;
    Ok(ArchiveFrame { length, payload })
}

/// Writes an encoded archive response with the same write timeout as every
/// frame.
#[cfg(feature = "service-diagnostics-export-v1")]
// The wire fixture binary includes this module too and serves no export.
#[allow(dead_code)]
pub(crate) async fn write_encoded_archive_frame(
    io: &mut (impl AsyncWrite + Unpin + ?Sized),
    frame: &ArchiveFrame,
) -> io::Result<()> {
    timeout(FRAME_TIMEOUT, async {
        io.write_i32_le(frame.length).await?;
        io.write_all(&frame.payload).await?;
        io.flush().await
    })
    .await?
}

pub(crate) fn invalid(message: &'static str) -> io::Error {
    io::Error::new(io::ErrorKind::InvalidData, message)
}

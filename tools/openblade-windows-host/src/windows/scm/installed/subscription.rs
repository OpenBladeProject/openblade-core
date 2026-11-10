use std::{sync::Arc, time::Duration};

use tokio::{
    io::{AsyncReadExt, split},
    sync::{Semaphore, watch},
    time::sleep,
};

use super::{
    Caller, ExchangeError, GetStatus, Snapshot, StatusResponse, Stream, denied, resolve_caller,
};
use crate::{
    service_frame::{encode, invalid, write_payload},
    service_status::encode_status_event,
};

const CONNECTION_LIFETIME: Duration = Duration::from_secs(15 * 60);
const CAPACITY_UNAVAILABLE: i32 = 0; // ServiceResponseErrorCode.CapacityUnavailable

// The publisher owns the authoritative projection of private profile and
// intent fields. The listener chooses a view using fresh session authority.
pub(super) struct Publication {
    pub(super) active: Snapshot,
    pub(super) inactive: Snapshot,
}

impl Publication {
    fn for_peer(&self, stream: &dyn Stream, original: Caller) -> Result<&Snapshot, ExchangeError> {
        let peer = stream.caller_after_read().map_err(ExchangeError::Client)?;
        if original.pid.is_none()
            || peer.pid != original.pid
            || peer.session != original.session
            || peer.logon != original.logon
        {
            return Err(ExchangeError::Client(denied(
                "installed status peer changed",
            )));
        }
        let access = resolve_caller(stream, peer)?
            .access()
            .map_err(ExchangeError::Client)?;
        self.active.verify(0).map_err(ExchangeError::Runtime)?;
        self.inactive.verify(1).map_err(ExchangeError::Runtime)?;
        if self.active.revision() != self.inactive.revision() {
            return Err(ExchangeError::Runtime(invalid(
                "status projection revision mismatch",
            )));
        }
        Ok(if access == 0 {
            &self.active
        } else {
            &self.inactive
        })
    }
}

pub(super) async fn serve_subscription(
    stream: &mut dyn Stream,
    request: &GetStatus,
    caller: Caller,
    publications: Option<watch::Receiver<Arc<Publication>>>,
    slots: &Semaphore,
) -> Result<(), ExchangeError> {
    use ExchangeError::{Client, Runtime};
    caller.access().map_err(Client)?;
    if caller.pid.is_none() {
        return Err(Client(denied(
            "installed status subscription requires a peer PID",
        )));
    }
    let Some(mut publications) = publications.filter(|feed| feed.has_changed().is_ok()) else {
        let response = StatusResponse::failure(request, "Status publication is unavailable.", None);
        return write_payload(stream, &encode(&response).map_err(Runtime)?)
            .await
            .map_err(Client);
    };
    let Ok(_slot) = slots.try_acquire() else {
        let response = StatusResponse::failure(
            request,
            "Status subscription capacity is unavailable.",
            Some(CAPACITY_UNAVAILABLE),
        );
        return write_payload(stream, &encode(&response).map_err(Runtime)?)
            .await
            .map_err(Client);
    };

    let stream_updates = async {
        // A publication during the initial write remains pending. Cloning the
        // shared snapshot releases the watch lock before any I/O.
        let initial = Arc::clone(&publications.borrow_and_update());
        let snapshot = initial.for_peer(stream, caller)?;
        let mut revision = snapshot.revision();
        let initial = StatusResponse::success(request, snapshot.clone());
        if !write_while_connected(stream, &encode(&initial).map_err(Runtime)?).await? {
            return Ok(());
        }
        let mut probe = [0];
        loop {
            tokio::select! {
                biased;
                disconnected = stream.read(&mut probe) => return disconnected.map(|_| ()).map_err(Client),
                update = publications.changed() => {
                    if update.is_err() || publications.has_changed().is_err() {
                        return Ok(());
                    }
                }
            }
            let latest = Arc::clone(&publications.borrow_and_update());
            let snapshot = latest.for_peer(stream, caller)?;
            if snapshot.revision() <= revision {
                continue;
            }
            if !write_while_connected(stream, &encode_status_event(snapshot).map_err(Runtime)?)
                .await?
            {
                return Ok(());
            }
            revision = snapshot.revision();
        }
    };
    tokio::select! {
        biased;
        () = sleep(CONNECTION_LIFETIME) => Ok(()),
        result = stream_updates => result,
    }
}

async fn write_while_connected(
    stream: &mut dyn Stream,
    frame: &[u8],
) -> Result<bool, ExchangeError> {
    let (mut reader, mut writer) = split(stream);
    let mut probe = [0];
    tokio::select! {
        biased;
        // EOF and any new input end this single-request connection. In
        // particular, do not reuse impersonation evidence after another read.
        disconnected = reader.read(&mut probe) => disconnected.map(|_| false).map_err(ExchangeError::Client),
        written = write_payload(&mut writer, frame) => written.map(|()| true).map_err(ExchangeError::Client),
    }
}

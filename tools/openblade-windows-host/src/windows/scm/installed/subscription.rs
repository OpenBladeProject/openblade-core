use std::{future::Future, io, sync::Arc, time::Duration};

use tokio::{
    io::{AsyncReadExt, split},
    sync::{Notify, Semaphore, watch},
    time::{Instant, sleep, sleep_until},
};

use super::{
    Caller, ExchangeError, Snapshot, StatusRequest, StatusResponse, Stream, denied, refresh_caller,
};
use crate::{
    service_frame::{encode, invalid, write_payload},
    service_status::encode_status_event,
};

const CONNECTION_LIFETIME: Duration = Duration::from_secs(15 * 60);
const CAPACITY_UNAVAILABLE: i32 = 0; // ServiceResponseErrorCode.CapacityUnavailable
pub(super) const REFRESH_INTERVAL: Duration = Duration::from_secs(2);

// The state every connection of one server shares: the subscription slots
// and, with the export, its single-build gate.
pub(super) struct Subscriptions {
    capacity: usize,
    slots: Semaphore,
    wake: Notify,
    #[cfg(feature = "service-diagnostics-export-v1")]
    exports: super::export::Exports,
}

impl Subscriptions {
    pub(super) fn new(capacity: usize) -> Self {
        Self {
            capacity,
            slots: Semaphore::new(capacity),
            wake: Notify::new(),
            #[cfg(feature = "service-diagnostics-export-v1")]
            exports: super::export::Exports::new(),
        }
    }

    #[cfg(feature = "service-diagnostics-export-v1")]
    pub(super) fn exports(&self) -> &super::export::Exports {
        &self.exports
    }

    pub(super) fn has_subscribers(&self) -> bool {
        self.slots.available_permits() < self.capacity
    }

    pub(super) async fn refresh_while_subscribed<F: Future<Output = io::Result<Instant>>>(
        &self,
        mut refresh: impl FnMut() -> F,
    ) -> io::Result<()> {
        loop {
            while !self.has_subscribers() {
                self.wake.notified().await;
            }
            // The source owns observation freshness, including observations
            // shared with GetStatus and the initial subscription response.
            // A young observation defers only until its actual due time.
            let due = refresh().await?;
            sleep_until(due).await;
        }
    }
}

// The publisher owns the authoritative projection of private profile and
// intent fields. The listener chooses a view using fresh session authority.
pub(super) struct Publication {
    pub(super) active: Snapshot,
    pub(super) inactive: Snapshot,
}

impl Publication {
    fn for_peer(&self, stream: &dyn Stream, original: Caller) -> Result<Snapshot, ExchangeError> {
        let access = refresh_caller(stream, original)?
            .access()
            .map_err(ExchangeError::Client)?;
        self.active.verify(0).map_err(ExchangeError::Runtime)?;
        self.inactive.verify(1).map_err(ExchangeError::Runtime)?;
        if self.active.revision() != self.inactive.revision() {
            return Err(ExchangeError::Runtime(invalid(
                "status projection revision mismatch",
            )));
        }
        let status = if access == 0 {
            &self.active
        } else {
            &self.inactive
        };
        #[cfg(feature = "service-thermal-mutation-v1")]
        return super::project_private_status(stream, original, status);
        #[cfg(not(feature = "service-thermal-mutation-v1"))]
        Ok(status.clone())
    }
}

pub(super) async fn serve_subscription(
    stream: &mut dyn Stream,
    request: &StatusRequest,
    caller: Caller,
    publications: Option<watch::Receiver<Arc<Publication>>>,
    subscriptions: &Subscriptions,
    verify_origin: &dyn Fn() -> io::Result<()>,
    refresh_initial: impl Future<Output = io::Result<()>>,
) -> Result<(), ExchangeError> {
    use ExchangeError::{Client, Runtime};
    verify_origin().map_err(Runtime)?;
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
    let Ok(_slot) = subscriptions.slots.try_acquire() else {
        let response = StatusResponse::failure(
            request,
            "Status subscription capacity is unavailable.",
            Some(CAPACITY_UNAVAILABLE),
        );
        return write_payload(stream, &encode(&response).map_err(Runtime)?)
            .await
            .map_err(Client);
    };
    subscriptions.wake.notify_one();

    let stream_updates = async {
        refresh_initial.await.map_err(Runtime)?;
        if publications.has_changed().is_err() {
            return Ok(());
        }
        // A publication during the initial write remains pending. Cloning the
        // shared snapshot releases the watch lock before any I/O.
        verify_origin().map_err(Runtime)?;
        let initial = Arc::clone(&publications.borrow_and_update());
        let snapshot = initial.for_peer(stream, caller)?;
        let mut revision = snapshot.revision();
        let initial = StatusResponse::success(request, snapshot.clone());
        if !write_while_connected(stream, &encode(&initial).map_err(Runtime)?).await? {
            return Ok(());
        }
        verify_origin().map_err(Runtime)?;
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
            verify_origin().map_err(Runtime)?;
            let latest = Arc::clone(&publications.borrow_and_update());
            let snapshot = latest.for_peer(stream, caller)?;
            if snapshot.revision() <= revision {
                continue;
            }
            if !write_while_connected(stream, &encode_status_event(&snapshot).map_err(Runtime)?)
                .await?
            {
                return Ok(());
            }
            verify_origin().map_err(Runtime)?;
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

#[cfg(test)]
#[path = "subscription_cadence_tests.rs"]
mod cadence_tests;

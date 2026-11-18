//! Installed status/stop supervision with explicitly selected feature endpoints.
#![allow(unsafe_code)]
use super::{scm_peer::WindowsInstalledServiceAuthority, service_client};
use crate::loaded_origin::Origin;
use sha2::{Digest, Sha256};
use std::{
    io,
    os::windows::io::{AsRawHandle, FromRawHandle, OwnedHandle},
    sync::Arc,
    time::Duration,
};
use windows_sys::Win32::{
    Foundation::{ERROR_ALREADY_EXISTS, GetLastError, WAIT_OBJECT_0, WAIT_TIMEOUT},
    System::Threading::{CreateEventW, WaitForSingleObject},
};

pub(super) fn run(origin: Arc<Origin>) -> io::Result<()> {
    super::verify_origin(&origin)?;
    let digest = Sha256::digest(origin.image.to_string_lossy().to_uppercase().as_bytes());
    let hash: String = digest[..16].iter().map(|b| format!("{b:02X}")).collect();
    let name: Vec<u16> = format!("Local\\OpenBlade.SessionAgent.Shutdown.{hash}")
        .encode_utf16()
        .chain([0])
        .collect();
    // Use the managed path-derived session event. Never adopt a preexisting
    // object as ownership. The handle is held until the status watcher drains.
    let raw = unsafe { CreateEventW(std::ptr::null(), 1, 0, name.as_ptr()) };
    if raw.is_null() {
        return Err(io::Error::last_os_error());
    }
    let existing = unsafe { GetLastError() } == ERROR_ALREADY_EXISTS;
    let stop = unsafe { OwnedHandle::from_raw_handle(raw) };
    if existing {
        return Err(io::Error::new(
            io::ErrorKind::AlreadyExists,
            "Agent stop endpoint is already owned",
        ));
    }
    let authority = WindowsInstalledServiceAuthority::from_agent(origin.clone())?;
    let runtime = tokio::runtime::Builder::new_current_thread()
        .enable_all()
        .build()?;
    runtime.block_on(async {
        // Display mutation admission retains only the confirmed power source
        // from verified, fresh status on the authenticated subscription. Stop,
        // Origin loss and a finished watcher each retire it permanently, before
        // the feature drains, so no in-flight write can pass its fence after.
        #[cfg(feature = "agent-display-brightness-write-v1")]
        let power = super::power_admission::PowerAdmission::default();
        #[cfg(feature = "agent-display-brightness-write-v1")]
        let _retire = Retire(power.clone());
        let canceled = async {
            let result = async {
                loop {
                    match unsafe { WaitForSingleObject(stop.as_raw_handle(), 0) } {
                        WAIT_OBJECT_0 => return Ok::<(), io::Error>(()),
                        WAIT_TIMEOUT => (),
                        _ => return Err(io::Error::last_os_error()),
                    }
                    super::verify_origin(&origin)?;
                    tokio::time::sleep(Duration::from_millis(250)).await;
                }
            }
            .await;
            #[cfg(feature = "agent-display-brightness-write-v1")]
            power.retire();
            result
        };
        tokio::pin!(canceled);
        let watch = service_client::watch_status(
            Some(&authority),
            std::future::pending::<()>(),
            |update| {
                #[cfg(feature = "agent-display-brightness-write-v1")]
                let result = (|| {
                    super::verify_origin(&origin)?;
                    match update {
                        service_client::StatusUpdate::Opened(value) => {
                            value.require_fresh()?;
                            let source = value.into_observation().snapshot.active_power_source();
                            power.opened(source);
                        }
                        service_client::StatusUpdate::Event(value) => {
                            value.require_fresh()?;
                            power.observed(value.snapshot.active_power_source());
                        }
                        service_client::StatusUpdate::Reconnecting => power.unavailable(),
                    }
                    Ok(())
                })();
                #[cfg(feature = "agent-display-brightness-write-v1")]
                if result.is_err() {
                    power.unavailable();
                }
                #[cfg(feature = "agent-display-brightness-write-v1")]
                return result;
                // No firmware projection is claimed by a metadata subscription.
                #[cfg(not(feature = "agent-display-brightness-write-v1"))]
                {
                    super::verify_origin(&origin)?;
                    match update {
                        service_client::StatusUpdate::Opened(value) => value.require_fresh(),
                        service_client::StatusUpdate::Event(value) => value.require_fresh(),
                        service_client::StatusUpdate::Reconnecting => Ok(()),
                    }
                }
            },
        );
        #[cfg(feature = "agent-display-brightness-write-v1")]
        let watch = async {
            let result = watch.await;
            power.retire();
            result
        };
        #[cfg(all(
            feature = "agent-display-query-v1",
            not(feature = "agent-display-brightness-write-v1")
        ))]
        let display = |feature_stop| super::display_query::run(origin.clone(), feature_stop);
        #[cfg(feature = "agent-display-brightness-write-v1")]
        let display = |feature_stop| {
            super::display_query::run(origin.clone(), feature_stop, power.clone())
        };
        #[cfg(not(any(
            feature = "agent-battery-report-v1",
            feature = "agent-display-query-v1"
        )))]
        {
            tokio::select! { result = &mut canceled => result, result = watch => result }
        }
        #[cfg(all(
            feature = "agent-battery-report-v1",
            not(feature = "agent-display-query-v1")
        ))]
        {
            super::feature_lifetime::run(canceled, watch, |feature_stop| {
                super::battery::run(origin.clone(), feature_stop)
            })
            .await
        }
        #[cfg(all(
            feature = "agent-display-query-v1",
            not(feature = "agent-battery-report-v1")
        ))]
        {
            super::feature_lifetime::run(canceled, watch, |feature_stop| {
                display(feature_stop)
            })
            .await
        }
        #[cfg(all(
            feature = "agent-battery-report-v1",
            feature = "agent-display-query-v1"
        ))]
        {
            super::feature_lifetime::run_pair(
                canceled,
                watch,
                |feature_stop| super::battery::run(origin.clone(), feature_stop),
                |feature_stop| display(feature_stop),
            )
            .await
        }
    })
}

/// Retires display power admission on every exit from the monitor runtime.
#[cfg(feature = "agent-display-brightness-write-v1")]
struct Retire(super::power_admission::PowerAdmission);
#[cfg(feature = "agent-display-brightness-write-v1")]
impl Drop for Retire {
    fn drop(&mut self) {
        self.0.retire();
    }
}

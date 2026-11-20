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
        #[cfg(openblade_display_write)]
        let power = super::power_admission::PowerAdmission::default();
        #[cfg(openblade_display_write)]
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
            #[cfg(openblade_display_write)]
            power.retire();
            result
        };
        tokio::pin!(canceled);
        let watch = service_client::watch_status(
            Some(&authority),
            std::future::pending::<()>(),
            |update| {
                #[cfg(openblade_display_write)]
                let result = (|| {
                    super::verify_origin(&origin)?;
                    match update {
                        service_client::StatusUpdate::Opened(value) => {
                            value.require_fresh()?;
                            let source = value.into_observation().snapshot.display_power_source();
                            power.opened(source);
                        }
                        service_client::StatusUpdate::Event(value) => {
                            value.require_fresh()?;
                            power.observed(value.snapshot.display_power_source());
                        }
                        service_client::StatusUpdate::Reconnecting => power.unavailable(),
                    }
                    Ok(())
                })();
                #[cfg(openblade_display_write)]
                if result.is_err() {
                    power.unavailable();
                }
                #[cfg(openblade_display_write)]
                return result;
                // No firmware projection is claimed by a metadata subscription.
                #[cfg(not(openblade_display_write))]
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
        #[cfg(openblade_display_write)]
        let watch = async {
            let result = watch.await;
            power.retire();
            result
        };
        #[cfg(all(feature = "agent-display-query-v1", not(openblade_display_write)))]
        let display = |feature_stop| super::display_query::run(origin.clone(), feature_stop);
        #[cfg(openblade_display_write)]
        let display =
            |feature_stop| super::display_query::run(origin.clone(), feature_stop, power.clone());
        #[cfg(not(openblade_agent_endpoint))]
        {
            tokio::select! { result = &mut canceled => result, result = watch => result }
        }
        #[cfg(openblade_agent_endpoint)]
        {
            use super::feature_lifetime::{feature, run_all};
            // Every selected endpoint shares one stop signal and is drained
            // before the stop event, Origin and status custody are released.
            let mut features = Vec::new();
            #[cfg(feature = "agent-battery-report-v1")]
            features.push(feature(|feature_stop| {
                super::battery::run(origin.clone(), feature_stop)
            }));
            #[cfg(feature = "agent-display-query-v1")]
            features.push(feature(display));
            #[cfg(feature = "agent-gpu-v1")]
            features.push(feature(|feature_stop| {
                super::gpu::run(origin.clone(), feature_stop)
            }));
            run_all(canceled, watch, features).await
        }
    })
}

/// Retires display power admission on every exit from the monitor runtime.
#[cfg(openblade_display_write)]
struct Retire(super::power_admission::PowerAdmission);
#[cfg(openblade_display_write)]
impl Drop for Retire {
    fn drop(&mut self) {
        self.0.retire();
    }
}

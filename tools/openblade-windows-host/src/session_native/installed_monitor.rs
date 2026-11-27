//! Installed status/stop supervision with explicitly selected feature endpoints.
#![allow(unsafe_code)]
use super::{
    exit::{Failure, Failure::Denied, classify_run, singleton},
    scm_peer::WindowsInstalledServiceAuthority,
    service_client,
};
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

pub(super) fn run(
    origin: Arc<Origin>,
    mut restart: super::restart::Registration<super::restart::Windows>,
) -> Result<(), Failure> {
    super::verify_origin(&origin).map_err(Denied)?;
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
        return Err(Denied(io::Error::last_os_error()));
    }
    let existing =
        (unsafe { GetLastError() } == ERROR_ALREADY_EXISTS).then_some(ERROR_ALREADY_EXISTS as i32);
    let stop = unsafe { OwnedHandle::from_raw_handle(raw) };
    // Managed exits 0 quietly when another Agent owns its stop event.
    singleton(existing)?;
    let authority = WindowsInstalledServiceAuthority::from_agent(origin.clone()).map_err(Denied)?;
    let runtime = tokio::runtime::Builder::new_current_thread()
        .enable_all()
        .build()
        .map_err(Failure::Fault)?;
    // Set by the stop event alone, so a failure that surfaces while the roles
    // drain after it is reported but never raised as a crash.
    let deliberate = std::cell::Cell::new(false);
    let result = runtime.block_on(async {
        // Display mutation admission retains only the confirmed power source
        // from verified, fresh status on the authenticated subscription. Stop,
        // Origin loss and a finished watcher each retire it permanently, before
        // the feature drains, so no in-flight write can pass its fence after.
        #[cfg(openblade_display_write)]
        let power = super::power_admission::PowerAdmission::default();
        #[cfg(openblade_display_write)]
        let _retire = Retire(power.clone());
        // Managed hands every connected status to the application reporter.
        // The feed keeps the latest profile selection fields; it grants no
        // authority and the reporter never reads status for anything else.
        #[cfg(feature = "agent-application-reporter-v1")]
        let profiles = super::application::ProfileFeed::new();
        // The function-row hook reads Driver mode only from verified, fresh
        // status. A reconnect, a stale status or a lost Origin passes every
        // key through at once; the last two then end the watcher. A changed
        // Service generation or pipe peer ends the watcher without a
        // reconnect, and the role's stop then passes every key through.
        #[cfg(feature = "agent-function-row-v1")]
        let function_row = Arc::new(super::function_row::Admission::default());
        // Display automation observes the same verified statuses, in order
        // (managed ServiceDisplayAutomationLoop). It resolves each status's
        // display members first and only then lets power admission take it,
        // so a status managed would refuse never admits a write. Its checkpoint is loaded before the
        // first status, as managed constructs the coordinator first.
        #[cfg(feature = "agent-display-automation-v1")]
        let automation = {
            let power = power.clone();
            tokio::task::spawn_blocking(move || super::display_query::Automation::load(power))
                .await
                .map_err(|_| io::Error::other("display recovery load failed"))?
        };
        // Power-mode automation observes the same verified statuses, in order
        // (managed ServicePowerModeAutomationLoop); a reconnect, a stale status
        // or a lost Origin marks it unavailable and cancels its running apply.
        #[cfg(feature = "agent-power-mode-automation-v1")]
        let power_mode = super::power_mode::Automation::new();
        let canceled = async {
            let result = async {
                loop {
                    match unsafe { WaitForSingleObject(stop.as_raw_handle(), 0) } {
                        WAIT_OBJECT_0 => {
                            // A deliberate stop must not be restarted.
                            restart.deliberate_stop(&deliberate);
                            return Ok::<(), io::Error>(());
                        }
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
                #[cfg(feature = "agent-application-reporter-v1")]
                super::application::publish_status(&profiles, &update, || {
                    super::verify_origin(&origin)
                })?;
                #[cfg(feature = "agent-function-row-v1")]
                super::function_row::publish_status(&function_row, &update, || {
                    super::verify_origin(&origin)
                })?;
                #[cfg(feature = "agent-power-mode-automation-v1")]
                super::power_mode::publish_status(&power_mode, &update, || {
                    super::verify_origin(&origin)
                })?;
                #[cfg(openblade_display_write)]
                let result = (|| {
                    super::verify_origin(&origin)?;
                    match update {
                        service_client::StatusUpdate::Opened(value) => {
                            value.require_fresh()?;
                            let snapshot = value.into_observation().snapshot;
                            let admit = || {
                                power.opened(
                                    snapshot.display_power_source(),
                                    snapshot.display_manual_external_power(),
                                )
                            };
                            #[cfg(feature = "agent-display-automation-v1")]
                            automation.observe(&snapshot, admit)?;
                            #[cfg(not(feature = "agent-display-automation-v1"))]
                            admit();
                        }
                        service_client::StatusUpdate::Event(value) => {
                            value.require_fresh()?;
                            let snapshot = &value.snapshot;
                            let admit = || {
                                power.observed(
                                    snapshot.display_power_source(),
                                    snapshot.display_manual_external_power(),
                                )
                            };
                            #[cfg(feature = "agent-display-automation-v1")]
                            automation.observe(snapshot, admit)?;
                            #[cfg(not(feature = "agent-display-automation-v1"))]
                            admit();
                        }
                        service_client::StatusUpdate::Reconnecting => {
                            power.unavailable();
                            #[cfg(feature = "agent-display-automation-v1")]
                            automation.unavailable()?;
                        }
                    }
                    Ok(())
                })();
                #[cfg(openblade_display_write)]
                if result.is_err() {
                    power.unavailable();
                    #[cfg(feature = "agent-display-automation-v1")]
                    let _ = automation.unavailable();
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
        #[cfg(all(openblade_display_write, not(feature = "agent-display-automation-v1")))]
        let display =
            |feature_stop| super::display_query::run(origin.clone(), feature_stop, power.clone());
        #[cfg(feature = "agent-display-automation-v1")]
        let display = |feature_stop| {
            super::display_query::run(
                origin.clone(),
                feature_stop,
                power.clone(),
                automation.clone(),
            )
        };
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
            #[cfg(feature = "agent-application-reporter-v1")]
            features.push(feature(|feature_stop| {
                super::application::run(origin.clone(), feature_stop, profiles.subscribe())
            }));
            #[cfg(feature = "agent-display-query-v1")]
            features.push(feature(display));
            #[cfg(feature = "agent-gpu-v1")]
            features.push(feature(|feature_stop| {
                super::gpu::run(origin.clone(), feature_stop)
            }));
            #[cfg(feature = "agent-function-row-v1")]
            features.push(feature(|feature_stop| {
                super::function_row::run(origin.clone(), feature_stop, function_row.clone())
            }));
            #[cfg(feature = "agent-power-mode-automation-v1")]
            features.push(feature(|feature_stop| {
                super::power_mode::run(origin.clone(), feature_stop, power_mode.clone())
            }));
            run_all(canceled, watch, features).await
        }
    });
    classify_run(result, deliberate.get())
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

#![allow(unsafe_code)]

use std::{future::Future, io};

use tokio::sync::oneshot;
use windows_sys::Win32::System::Services::SERVICE_STATUS;

use super::{SCM_ID, Session, Status, replay_status, run_prepared};
use crate::scm_host::{CONTROLS, Controls, dispatch_named, registered_service, report};

#[cfg(any(test, feature = "lifecycle-evidence-compile-check"))]
#[allow(dead_code)]
// The installed status source includes this file for its power read; its tests run here.
mod lifecycle;

const NAME: &str = "OpenBladeRustReadOnlyDev";

#[cfg(feature = "vm-scm-fixture")]
pub(super) mod vm;

pub(super) fn dispatch() -> io::Result<()> {
    dispatch_named(NAME, service_main)
}

unsafe extern "system" fn service_main(_argc: u32, _argv: *mut *mut u16) {
    // FFI callbacks must not unwind across SCM.
    if std::panic::catch_unwind(service).is_err() {
        std::process::abort();
    }
}

fn service() {
    registered_service(NAME, |runtime, handle, receiver| {
        let _ = runtime.block_on(run_service(
            &CONTROLS,
            receiver,
            SCM_ID,
            Session::Active,
            |shutdown| replay_status(None, shutdown),
            |status| report(handle, status),
        ));
    });
}

pub(super) async fn run_service<F: Future<Output = io::Result<()>>>(
    controls: &Controls,
    stop: oneshot::Receiver<()>,
    id: &str,
    session: Session,
    prepare: impl FnOnce(oneshot::Receiver<()>) -> io::Result<(Status, Option<F>)>,
    mut publish: impl FnMut(&SERVICE_STATUS) -> io::Result<()>,
) -> io::Result<()> {
    run_prepared(
        id,
        session,
        prepare,
        |phase| controls.publish(phase, &mut publish),
        async {
            let _ = stop.await;
        },
    )
    .await
}

#[cfg(test)]
mod tests;

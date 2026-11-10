#![allow(unsafe_code)]

use std::{ffi::c_void, future::Future, io, ptr::null_mut, sync::Mutex};

use tokio::{runtime::Builder, sync::oneshot};
use windows_sys::Win32::{
    Foundation::{ERROR_CALL_NOT_IMPLEMENTED, ERROR_GEN_FAILURE, NO_ERROR},
    System::Services::{
        RegisterServiceCtrlHandlerExW, SERVICE_ACCEPT_SHUTDOWN, SERVICE_ACCEPT_STOP,
        SERVICE_CONTROL_INTERROGATE, SERVICE_CONTROL_SHUTDOWN, SERVICE_CONTROL_STOP,
        SERVICE_RUNNING, SERVICE_START_PENDING, SERVICE_STATUS, SERVICE_STATUS_HANDLE,
        SERVICE_STOP_PENDING, SERVICE_STOPPED, SERVICE_TABLE_ENTRYW, SERVICE_WIN32_OWN_PROCESS,
        SetServiceStatus, StartServiceCtrlDispatcherW,
    },
};

use super::{Phase, SCM_ID, Session, Status, replay_status, run_prepared, startup_stopped};

const NAME: &str = "OpenBladeRustReadOnlyDev";
static CONTROLS: Controls = Controls::new();

pub(super) struct Controls(Mutex<Option<oneshot::Sender<()>>>);

impl Controls {
    pub(super) const fn new() -> Self {
        Self(Mutex::new(None))
    }

    pub(super) fn arm(&self) -> oneshot::Receiver<()> {
        let (sender, receiver) = oneshot::channel();
        *self.0.lock().unwrap_or_else(|error| error.into_inner()) = Some(sender);
        receiver
    }

    fn take_stop(&self) -> Option<oneshot::Sender<()>> {
        self.0
            .lock()
            .unwrap_or_else(|error| error.into_inner())
            .take()
    }

    fn handle(&self, code: u32) -> u32 {
        match code {
            SERVICE_CONTROL_STOP | SERVICE_CONTROL_SHUTDOWN => {
                self.request_stop();
                NO_ERROR
            }
            SERVICE_CONTROL_INTERROGATE => NO_ERROR,
            _ => ERROR_CALL_NOT_IMPLEMENTED,
        }
    }

    pub(super) fn request_stop(&self) {
        if let Some(stop) = self.take_stop() {
            let _ = stop.send(());
        }
    }

    fn publish(
        &self,
        phase: Phase,
        publish: &mut impl FnMut(&SERVICE_STATUS) -> io::Result<()>,
    ) -> io::Result<()> {
        let status = status_for(phase);
        if phase == Phase::Running {
            // Commit readiness and accepting STOP in the same order as the callback.
            let stop = self.0.lock().unwrap_or_else(|error| error.into_inner());
            if stop.is_none() {
                return Err(startup_stopped());
            }
            publish(&status)
        } else {
            publish(&status)
        }
    }
}

pub(super) fn dispatch() -> io::Result<()> {
    let mut name: Vec<u16> = NAME.encode_utf16().chain([0]).collect();
    let table = [
        SERVICE_TABLE_ENTRYW {
            lpServiceName: name.as_mut_ptr(),
            lpServiceProc: Some(service_main),
        },
        SERVICE_TABLE_ENTRYW {
            lpServiceName: null_mut(),
            lpServiceProc: None,
        },
    ];
    // SAFETY: the table and NUL-terminated name live until the synchronous dispatcher returns.
    if unsafe { StartServiceCtrlDispatcherW(table.as_ptr()) } == 0 {
        return Err(io::Error::last_os_error());
    }
    Ok(())
}

unsafe extern "system" fn service_main(_argc: u32, _argv: *mut *mut u16) {
    // FFI callbacks must not unwind across SCM.
    if std::panic::catch_unwind(service).is_err() {
        std::process::abort();
    }
}

fn service() {
    let receiver = CONTROLS.arm();
    let name: Vec<u16> = NAME.encode_utf16().chain([0]).collect();
    // SAFETY: the service name is NUL-terminated, handler is static and uses no context pointer.
    let handle = unsafe { RegisterServiceCtrlHandlerExW(name.as_ptr(), Some(control), null_mut()) };
    if handle.is_null() {
        CONTROLS.take_stop();
        return;
    }

    match Builder::new_current_thread().enable_all().build() {
        Ok(runtime) => {
            let _ = runtime.block_on(run_service(
                &CONTROLS,
                receiver,
                SCM_ID,
                Session::Active,
                |shutdown| replay_status(None, shutdown),
                |status| report(handle, status),
            ));
        }
        Err(_) => {
            let _ = report(handle, &status_for(Phase::Stopped { failed: true }));
        }
    }
    CONTROLS.take_stop();
}

unsafe extern "system" fn control(code: u32, _: u32, _: *mut c_void, _: *mut c_void) -> u32 {
    CONTROLS.handle(code)
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

fn status_for(phase: Phase) -> SERVICE_STATUS {
    let (state, controls, checkpoint, wait_hint, error) = match phase {
        Phase::Starting => (SERVICE_START_PENDING, 0, 1, 5000, NO_ERROR),
        Phase::Running => (
            SERVICE_RUNNING,
            SERVICE_ACCEPT_STOP | SERVICE_ACCEPT_SHUTDOWN,
            0,
            0,
            NO_ERROR,
        ),
        Phase::Stopping => (SERVICE_STOP_PENDING, 0, 1, 5000, NO_ERROR),
        Phase::Stopped { failed } => (
            SERVICE_STOPPED,
            0,
            0,
            0,
            if failed { ERROR_GEN_FAILURE } else { NO_ERROR },
        ),
    };
    SERVICE_STATUS {
        dwServiceType: SERVICE_WIN32_OWN_PROCESS,
        dwCurrentState: state,
        dwControlsAccepted: controls,
        dwWin32ExitCode: error,
        dwServiceSpecificExitCode: 0,
        dwCheckPoint: checkpoint,
        dwWaitHint: wait_hint,
    }
}

fn report(handle: SERVICE_STATUS_HANDLE, status: &SERVICE_STATUS) -> io::Result<()> {
    // SAFETY: the registered status handle is used by ServiceMain and the status lives through the call.
    if unsafe { SetServiceStatus(handle, status) } == 0 {
        Err(io::Error::last_os_error())
    } else {
        Ok(())
    }
}

#[cfg(test)]
mod tests;

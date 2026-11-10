#![allow(unsafe_code)]

use std::{ffi::c_void, io, ptr::null_mut, sync::Mutex};

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

use super::{Phase, SCM_ID, Session, run};

const NAME: &str = "OpenBladeRustReadOnlyDev";
static STOP: Mutex<Option<oneshot::Sender<()>>> = Mutex::new(None);

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
    let (sender, receiver) = oneshot::channel();
    *STOP.lock().unwrap_or_else(|error| error.into_inner()) = Some(sender);
    let name: Vec<u16> = NAME.encode_utf16().chain([0]).collect();
    // SAFETY: the service name is NUL-terminated, handler is static and uses no context pointer.
    let handle = unsafe { RegisterServiceCtrlHandlerExW(name.as_ptr(), Some(control), null_mut()) };
    if handle.is_null() {
        return;
    }

    match Builder::new_current_thread().enable_all().build() {
        Ok(runtime) => {
            let _ = runtime.block_on(run(
                SCM_ID,
                Session::Active,
                |phase| report(handle, phase),
                async {
                    let _ = receiver.await;
                },
            ));
        }
        Err(_) => {
            let _ = report(handle, Phase::Stopped { failed: true });
        }
    }
}

unsafe extern "system" fn control(code: u32, _: u32, _: *mut c_void, _: *mut c_void) -> u32 {
    match code {
        SERVICE_CONTROL_STOP | SERVICE_CONTROL_SHUTDOWN => {
            if let Some(stop) = STOP
                .lock()
                .unwrap_or_else(|error| error.into_inner())
                .take()
            {
                let _ = stop.send(());
            }
            NO_ERROR
        }
        SERVICE_CONTROL_INTERROGATE => NO_ERROR,
        _ => ERROR_CALL_NOT_IMPLEMENTED,
    }
}

fn report(handle: SERVICE_STATUS_HANDLE, phase: Phase) -> io::Result<()> {
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
    let status = SERVICE_STATUS {
        dwServiceType: SERVICE_WIN32_OWN_PROCESS,
        dwCurrentState: state,
        dwControlsAccepted: controls,
        dwWin32ExitCode: error,
        dwServiceSpecificExitCode: 0,
        dwCheckPoint: checkpoint,
        dwWaitHint: wait_hint,
    };
    // SAFETY: the registered status handle is used by ServiceMain and the status lives through the call.
    if unsafe { SetServiceStatus(handle, &status) } == 0 {
        Err(io::Error::last_os_error())
    } else {
        Ok(())
    }
}

//! Common SCM dispatcher, status transitions and stop ownership.
#![allow(unsafe_code)]

use std::{ffi::c_void, future::Future, io, ptr::null_mut, sync::Mutex};

use tokio::{
    runtime::{Builder, Runtime},
    sync::oneshot,
};
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

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) enum Phase {
    Starting,
    Running,
    Stopping,
    Stopped { failed: bool },
}

pub(crate) static CONTROLS: Controls = Controls::new();
static INGRESS: Mutex<Option<fn(u32, u32)>> = Mutex::new(None);
pub(crate) fn set_ingress(hook: fn(u32, u32)) -> io::Result<()> {
    let mut slot = INGRESS
        .lock()
        .map_err(|_| io::Error::other("SCM callback ingress lost"))?;
    if slot.is_some() {
        return Err(io::Error::other("SCM callback ingress already bound"));
    }
    *slot = Some(hook);
    Ok(())
}

pub(crate) struct Controls(Mutex<Option<oneshot::Sender<()>>>);

impl Controls {
    pub(crate) const fn new() -> Self {
        Self(Mutex::new(None))
    }

    pub(crate) fn arm(&self) -> oneshot::Receiver<()> {
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

    pub(crate) fn handle(&self, code: u32) -> u32 {
        match code {
            SERVICE_CONTROL_STOP | SERVICE_CONTROL_SHUTDOWN => {
                self.request_stop();
                NO_ERROR
            }
            SERVICE_CONTROL_INTERROGATE => NO_ERROR,
            windows_sys::Win32::System::Services::SERVICE_CONTROL_POWEREVENT
            | windows_sys::Win32::System::Services::SERVICE_CONTROL_SESSIONCHANGE
                if cfg!(feature = "service-thermal-mutation-v1") =>
            {
                NO_ERROR
            }
            _ => ERROR_CALL_NOT_IMPLEMENTED,
        }
    }

    pub(crate) fn request_stop(&self) {
        if let Some(stop) = self.take_stop() {
            let _ = stop.send(());
        }
    }

    pub(crate) fn stop_requested(&self) -> bool {
        self.0
            .lock()
            .unwrap_or_else(|error| error.into_inner())
            .is_none()
    }

    pub(crate) fn publish(
        &self,
        phase: Phase,
        publish: &mut impl FnMut(&SERVICE_STATUS) -> io::Result<()>,
    ) -> io::Result<()> {
        if phase == Phase::Running {
            // The endpoint is live. STOP before this decision cancels startup,
            // and STOP after it retires the ready host. Keep I/O outside the lock.
            if self.stop_requested() {
                return Err(startup_stopped());
            }
        }
        publish(&status_for(phase))
    }
}

pub(crate) fn dispatch_named(
    name: &str,
    callback: unsafe extern "system" fn(u32, *mut *mut u16),
) -> io::Result<()> {
    let mut name: Vec<u16> = name.encode_utf16().chain([0]).collect();
    let table = [
        SERVICE_TABLE_ENTRYW {
            lpServiceName: name.as_mut_ptr(),
            lpServiceProc: Some(callback),
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

pub(crate) fn registered_service(
    name: &str,
    run: impl FnOnce(&Runtime, SERVICE_STATUS_HANDLE, oneshot::Receiver<()>),
) {
    let receiver = CONTROLS.arm();
    let name: Vec<u16> = name.encode_utf16().chain([0]).collect();
    // SAFETY: the service name is NUL-terminated, handler is static and uses no context pointer.
    let handle = unsafe { RegisterServiceCtrlHandlerExW(name.as_ptr(), Some(control), null_mut()) };
    if handle.is_null() {
        CONTROLS.take_stop();
        return;
    }

    match Builder::new_current_thread().enable_all().build() {
        Ok(runtime) => run(&runtime, handle, receiver),
        Err(_) => {
            let _ = report(handle, &status_for(Phase::Stopped { failed: true }));
        }
    }
    CONTROLS.take_stop();
}

pub(crate) unsafe extern "system" fn control(
    code: u32,
    event: u32,
    _: *mut c_void,
    _: *mut c_void,
) -> u32 {
    // Latch before forwarding/coalescing. Unknown callback/registration loss
    // cannot revive an epoch after a later clear sample.
    if let Some(hook) = *INGRESS.lock().unwrap_or_else(|e| e.into_inner()) {
        hook(code, event);
    }
    CONTROLS.handle(code)
}

pub(crate) fn status_for(phase: Phase) -> SERVICE_STATUS {
    let (state, controls, checkpoint, wait_hint, error) = match phase {
        Phase::Starting => (SERVICE_START_PENDING, 0, 1, 5000, NO_ERROR),
        Phase::Running => (
            SERVICE_RUNNING,
            SERVICE_ACCEPT_STOP
                | SERVICE_ACCEPT_SHUTDOWN
                | if cfg!(feature = "service-thermal-mutation-v1") {
                    windows_sys::Win32::System::Services::SERVICE_ACCEPT_POWEREVENT
                        | windows_sys::Win32::System::Services::SERVICE_ACCEPT_SESSIONCHANGE
                } else {
                    0
                },
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

pub(crate) fn report(handle: SERVICE_STATUS_HANDLE, status: &SERVICE_STATUS) -> io::Result<()> {
    // SAFETY: the registered status handle is used by ServiceMain and the status lives through the call.
    if unsafe { SetServiceStatus(handle, status) } == 0 {
        Err(io::Error::last_os_error())
    } else {
        Ok(())
    }
}

pub(crate) async fn run_lifecycle<R: FnMut(Phase) -> io::Result<()>>(
    mut report: R,
    run: impl AsyncFnOnce(&mut R) -> io::Result<()>,
) -> io::Result<()> {
    let result = match report(Phase::Starting) {
        Ok(()) => run(&mut report).await,
        Err(error) => Err(error),
    };
    let result = normalize_startup_stop(result);
    let stopped = report(Phase::Stopped {
        failed: result.is_err(),
    });
    result.and(stopped)
}

pub(crate) fn normalize_startup_stop(result: io::Result<()>) -> io::Result<()> {
    match result {
        Err(error)
            if error
                .get_ref()
                .is_some_and(|cause| cause.is::<StartupStopped>()) =>
        {
            Ok(())
        }
        result => result,
    }
}

pub(crate) async fn check_starting(
    stop: std::pin::Pin<&mut impl Future<Output = ()>>,
) -> io::Result<()> {
    tokio::select! {
        biased;
        () = stop => Err(startup_stopped()),
        () = std::future::ready(()) => Ok(()),
    }
}

#[derive(Debug)]
struct StartupStopped;

impl std::fmt::Display for StartupStopped {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        formatter.write_str("service startup stopped")
    }
}

impl std::error::Error for StartupStopped {}

pub(crate) fn startup_stopped() -> io::Error {
    io::Error::new(io::ErrorKind::Interrupted, StartupStopped)
}

/// The installed entry callback and injected outer service use this same action.
/// Only terminal controls revoke loaded-build custody; routine events fence work.
pub(crate) fn installed_ingress(lifecycle: &Lifecycle, code: u32, event: u32, stop: impl FnOnce()) {
    use windows_sys::Win32::System::Services::{SERVICE_CONTROL_SHUTDOWN, SERVICE_CONTROL_STOP};
    if matches!(code, SERVICE_CONTROL_STOP | SERVICE_CONTROL_SHUTDOWN) {
        stop();
    } else {
        lifecycle.ingress(code, event);
    }
}

/// Callback-established active boundary. Unknown events fence old operations but
/// never establish resume; fresh device/session/controller proof is still required.
#[derive(Default)]
pub(crate) struct Lifecycle(Mutex<LifecycleState>);
struct LifecycleState {
    epoch: u64,
    awake: bool,
}
impl Default for LifecycleState {
    // Match managed LowPowerGate's initial clear low-power latch. This is not
    // an independent awake measurement: only a confirmed suspend enters low power.
    // Display-off is not sleep (ServiceLifecycleRefreshLoop.cs:134-138).
    fn default() -> Self {
        Self {
            epoch: 1,
            awake: true,
        }
    }
}
impl Lifecycle {
    pub(crate) fn ingress(&self, code: u32, event: u32) {
        use windows_sys::Win32::System::Services::{
            SERVICE_CONTROL_POWEREVENT, SERVICE_CONTROL_SESSIONCHANGE,
        };
        if !matches!(
            code,
            SERVICE_CONTROL_POWEREVENT | SERVICE_CONTROL_SESSIONCHANGE
        ) {
            return;
        }
        let Ok(mut state) = self.0.lock() else {
            return;
        };
        if state.epoch == 0 {
            return;
        } // Exhaustion is sticky; never revive epoch one.
        state.epoch = state.epoch.checked_add(1).unwrap_or(0);
        match (code, event) {
            (SERVICE_CONTROL_POWEREVENT, 4) => state.awake = false, // PBT_APMSUSPEND
            (SERVICE_CONTROL_POWEREVENT, 7 | 18) => state.awake = true, // resume suspend/automatic
            // Session notices fence old work; their payload never authorizes a user.
            // Fresh exact WTSInfoEx state is checked by the mutation supplier.
            _ => {}
        }
    }
    pub(crate) fn active(&self) -> Option<u64> {
        let state = self.0.lock().ok()?;
        (state.epoch != 0 && state.awake).then_some(state.epoch)
    }
}

#[cfg(test)]
mod lifecycle_tests {
    use super::*;
    #[test]
    fn correction_lifecycle_overflow_is_sticky_and_display_status_cannot_resume() {
        use windows_sys::Win32::System::Services::{
            SERVICE_CONTROL_POWEREVENT, SERVICE_CONTROL_SESSIONCHANGE,
        };
        let lifecycle = Lifecycle::default();
        assert_eq!(lifecycle.active(), Some(1));
        lifecycle.ingress(SERVICE_CONTROL_POWEREVENT, 4);
        assert_eq!(lifecycle.active(), None);
        lifecycle.ingress(SERVICE_CONTROL_POWEREVENT, 32787);
        assert_eq!(lifecycle.active(), None);
        lifecycle.ingress(SERVICE_CONTROL_SESSIONCHANGE, 8);
        assert_eq!(lifecycle.active(), None);
        lifecycle.ingress(SERVICE_CONTROL_POWEREVENT, 18);
        assert!(lifecycle.active().is_some());
        lifecycle.0.lock().unwrap().epoch = u64::MAX;
        lifecycle.ingress(SERVICE_CONTROL_POWEREVENT, 10);
        assert_eq!(lifecycle.active(), None);
        lifecycle.ingress(SERVICE_CONTROL_POWEREVENT, 18);
        assert_eq!(lifecycle.active(), None);
    }
}

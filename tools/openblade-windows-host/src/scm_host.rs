//! Common SCM dispatcher, status transitions and stop ownership.
#![allow(unsafe_code)]

use std::{ffi::c_void, future::Future, io, ptr::null_mut, sync::Mutex};

use openblade_domain::power_setting::{self, PowerSetting};

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
/// Code, event and the decoded `PBT_POWERSETTINGCHANGE` payload, if any.
type IngressHook = fn(u32, u32, Option<PowerSetting>);
static INGRESS: Mutex<Option<IngressHook>> = Mutex::new(None);
// The installed entry that binds the ingress is compiled out of test builds.
#[cfg_attr(test, allow(dead_code))]
pub(crate) fn set_ingress(hook: IngressHook) -> io::Result<()> {
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
    event_data: *mut c_void,
    _: *mut c_void,
) -> u32 {
    // Latch before forwarding/coalescing. Unknown callback/registration loss
    // cannot revive an epoch after a later clear sample.
    if let Some(hook) = *INGRESS.lock().unwrap_or_else(|e| e.into_inner()) {
        // SAFETY: SCM passes this control's own event data, live for the call.
        let setting = unsafe { power_setting_of(code, event, event_data) };
        hook(code, event, setting);
    }
    CONTROLS.handle(code)
}

/// The decoded `POWERBROADCAST_SETTING` of a power-setting change, or `None`
/// for any other control, a null pointer or a payload the decoder refuses.
/// It copies the header and at most one DWORD of the payload `DataLength`
/// declares, byte by byte, so it neither assumes alignment nor reads past a
/// shorter payload.
///
/// # Safety
/// For `SERVICE_CONTROL_POWEREVENT` with `PBT_POWERSETTINGCHANGE`, a non-null
/// `event_data` must point to a `POWERBROADCAST_SETTING` header followed by
/// the `DataLength` payload bytes it declares, as SCM delivers it to a service
/// registered with `DEVICE_NOTIFY_SERVICE_HANDLE`.
pub(crate) unsafe fn power_setting_of(
    code: u32,
    event: u32,
    event_data: *const c_void,
) -> Option<PowerSetting> {
    use windows_sys::Win32::System::Services::SERVICE_CONTROL_POWEREVENT;
    if code != SERVICE_CONTROL_POWEREVENT
        || event != power_setting::PBT_POWERSETTINGCHANGE
        || event_data.is_null()
    {
        return None;
    }
    let source = event_data.cast::<u8>();
    let mut bytes = [0u8; power_setting::MAX_LEN];
    // SAFETY: the caller guarantees the header is readable.
    unsafe { std::ptr::copy_nonoverlapping(source, bytes.as_mut_ptr(), power_setting::HEADER_LEN) };
    let payload = payload_len(&bytes);
    // SAFETY: at most the declared payload, which the caller guarantees.
    unsafe {
        std::ptr::copy_nonoverlapping(
            source.add(power_setting::HEADER_LEN),
            bytes.as_mut_ptr().add(power_setting::HEADER_LEN),
            payload,
        );
    }
    power_setting::decode(&bytes[..power_setting::HEADER_LEN + payload])
}

/// The payload bytes to copy after a header: what its `DataLength` declares,
/// at most the one DWORD the decoder reads.
fn payload_len(header: &[u8; power_setting::MAX_LEN]) -> usize {
    let declared = u32::from_le_bytes([header[16], header[17], header[18], header[19]]);
    usize::try_from(declared)
        .unwrap_or(usize::MAX)
        .min(power_setting::MAX_LEN - power_setting::HEADER_LEN)
}

/// Controls accepted while running. The thermal composition also takes power and
/// session events, for every service host compiled with it.
const RUNNING_CONTROLS: u32 = SERVICE_ACCEPT_STOP
    | SERVICE_ACCEPT_SHUTDOWN
    | if cfg!(feature = "service-thermal-mutation-v1") {
        windows_sys::Win32::System::Services::SERVICE_ACCEPT_POWEREVENT
            | windows_sys::Win32::System::Services::SERVICE_ACCEPT_SESSIONCHANGE
    } else {
        0
    };

pub(crate) fn status_for(phase: Phase) -> SERVICE_STATUS {
    let (state, controls, checkpoint, wait_hint, error) = match phase {
        Phase::Starting => (SERVICE_START_PENDING, 0, 1, 5000, NO_ERROR),
        Phase::Running => (SERVICE_RUNNING, RUNNING_CONTROLS, 0, 0, NO_ERROR),
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

/// `installed_notice` without a power-setting payload, for injected services.
#[cfg(test)]
pub(crate) fn installed_ingress(lifecycle: &Lifecycle, code: u32, event: u32, stop: impl FnOnce()) {
    installed_notice(lifecycle, code, event, None, stop);
}

/// The installed entry callback and injected outer service use this same action.
/// Only terminal controls revoke loaded-build custody; routine events reach the
/// lifecycle, which decides what they fence.
pub(crate) fn installed_notice(
    lifecycle: &Lifecycle,
    code: u32,
    event: u32,
    setting: Option<PowerSetting>,
    stop: impl FnOnce(),
) {
    use windows_sys::Win32::System::Services::{SERVICE_CONTROL_SHUTDOWN, SERVICE_CONTROL_STOP};
    if matches!(code, SERVICE_CONTROL_STOP | SERVICE_CONTROL_SHUTDOWN) {
        stop();
    } else {
        lifecycle.notice(code, event, setting);
    }
}

pub(crate) use crate::origin_lifetime::Lifecycle;

#[cfg(test)]
mod power_setting_tests {
    use std::ffi::c_void;

    use openblade_domain::power_setting::{
        ACDC_POWER_SOURCE, CONSOLE_DISPLAY_STATE, PBT_POWERSETTINGCHANGE, PowerSetting,
    };
    use windows_sys::Win32::System::{
        Services::{SERVICE_CONTROL_POWEREVENT, SERVICE_CONTROL_SESSIONCHANGE},
        SystemServices::{GUID_ACDC_POWER_SOURCE, GUID_CONSOLE_DISPLAY_STATE},
    };

    use super::power_setting_of;

    const VECTORS: &str = include_str!("../tests/data/power-setting-vectors.json");

    fn read(bytes: &[u8]) -> Option<PowerSetting> {
        // SAFETY: the buffer is exactly the header and the bytes it holds.
        unsafe {
            power_setting_of(
                SERVICE_CONTROL_POWEREVENT,
                PBT_POWERSETTINGCHANGE,
                bytes.as_ptr().cast::<c_void>(),
            )
        }
    }

    fn guid_bytes(guid: windows_sys::core::GUID) -> [u8; 16] {
        let mut bytes = [0; 16];
        bytes[..4].copy_from_slice(&guid.data1.to_le_bytes());
        bytes[4..6].copy_from_slice(&guid.data2.to_le_bytes());
        bytes[6..8].copy_from_slice(&guid.data3.to_le_bytes());
        bytes[8..].copy_from_slice(&guid.data4);
        bytes
    }

    #[test]
    fn the_domain_guids_are_the_windows_guids_in_memory_order() {
        assert_eq!(guid_bytes(GUID_ACDC_POWER_SOURCE), ACDC_POWER_SOURCE);
        assert_eq!(
            guid_bytes(GUID_CONSOLE_DISPLAY_STATE),
            CONSOLE_DISPLAY_STATE
        );
        #[cfg(feature = "lifecycle-evidence-compile-check")]
        assert_eq!(
            std::mem::offset_of!(
                windows_sys::Win32::System::Power::POWERBROADCAST_SETTING,
                Data
            ),
            openblade_domain::power_setting::HEADER_LEN
        );
    }

    #[test]
    fn the_scm_payload_read_matches_the_managed_decode_rows() {
        let root: serde_json::Value = serde_json::from_str(VECTORS).unwrap();
        let rows = root["decode"].as_array().unwrap();
        assert!(!rows.is_empty());
        for row in rows {
            let hex = row["bytes"].as_str().unwrap();
            // Exactly the recorded buffer: a read past it would be a bug.
            let bytes: Vec<u8> = (0..hex.len())
                .step_by(2)
                .map(|at| u8::from_str_radix(&hex[at..at + 2], 16).unwrap())
                .collect();
            let value = |name: &str| row["expect"][name].as_u64().map(|v| v as u32);
            let expected = match (value("powerSource"), value("consoleDisplay")) {
                (Some(source), None) => Some(PowerSetting::PowerSource(source)),
                (None, Some(display)) => Some(PowerSetting::ConsoleDisplay(display)),
                (None, None) => None,
                _ => panic!("a row decodes under one GUID at most"),
            };
            assert_eq!(read(&bytes), expected, "{}", row["name"]);
        }
    }

    #[test]
    fn only_a_non_null_power_setting_change_is_read() {
        let mut on = CONSOLE_DISPLAY_STATE.to_vec();
        on.extend_from_slice(&4u32.to_le_bytes());
        on.extend_from_slice(&1u32.to_le_bytes());
        assert_eq!(read(&on), Some(PowerSetting::ConsoleDisplay(1)));
        let data = on.as_ptr().cast::<c_void>();
        for (code, event, data) in [
            (
                SERVICE_CONTROL_POWEREVENT,
                PBT_POWERSETTINGCHANGE,
                std::ptr::null(),
            ),
            (SERVICE_CONTROL_POWEREVENT, 10, data),
            (SERVICE_CONTROL_POWEREVENT, 4, data),
            (SERVICE_CONTROL_SESSIONCHANGE, PBT_POWERSETTINGCHANGE, data),
        ] {
            // SAFETY: the pointer, when read at all, is the buffer above.
            assert_eq!(
                unsafe { power_setting_of(code, event, data) },
                None,
                "{code}/{event}"
            );
        }
    }

    #[test]
    fn the_copy_takes_the_declared_payload_up_to_one_dword() {
        // 0x0100_0000 pins the top byte: a three-byte length would read 0.
        for (declared, copied) in [
            (0, 0),
            (1, 1),
            (3, 3),
            (4, 4),
            (8, 4),
            (0x0100_0000, 4),
            (u32::MAX, 4),
        ] {
            let mut header = [0; openblade_domain::power_setting::MAX_LEN];
            header[16..20].copy_from_slice(&u32::to_le_bytes(declared));
            assert_eq!(super::payload_len(&header), copied, "{declared}");
        }
    }

    #[test]
    fn a_payload_shorter_than_one_dword_is_refused() {
        let mut short = CONSOLE_DISPLAY_STATE.to_vec();
        short.extend_from_slice(&1u32.to_le_bytes());
        short.push(1);
        assert_eq!(read(&short), None);
        let mut empty = ACDC_POWER_SOURCE.to_vec();
        empty.extend_from_slice(&0u32.to_le_bytes());
        assert_eq!(read(&empty), None);
    }

    /// Two pages, the second `PAGE_NOACCESS`: a buffer placed to end at the
    /// boundary faults the test process on any read past what it declares.
    struct GuardPage {
        base: *mut u8,
        page: usize,
    }

    impl GuardPage {
        fn new() -> Self {
            use windows_sys::Win32::System::{
                Memory::{
                    MEM_COMMIT, MEM_RESERVE, PAGE_NOACCESS, PAGE_READWRITE, VirtualAlloc,
                    VirtualProtect,
                },
                SystemInformation::{GetSystemInfo, SYSTEM_INFO},
            };
            // SAFETY: plain out-parameter.
            let page = unsafe {
                let mut info: SYSTEM_INFO = std::mem::zeroed();
                GetSystemInfo(&mut info);
                info.dwPageSize as usize
            };
            // SAFETY: a fresh private allocation this guard owns and frees.
            let base = unsafe {
                VirtualAlloc(
                    std::ptr::null(),
                    2 * page,
                    MEM_RESERVE | MEM_COMMIT,
                    PAGE_READWRITE,
                )
            }
            .cast::<u8>();
            assert!(!base.is_null(), "VirtualAlloc");
            let mut old = 0;
            // SAFETY: the second page of the allocation above.
            let protected = unsafe {
                VirtualProtect(
                    base.add(page).cast::<c_void>(),
                    page,
                    PAGE_NOACCESS,
                    &mut old,
                )
            };
            assert_ne!(protected, 0, "VirtualProtect");
            Self { base, page }
        }

        /// Copies `bytes` so the last one is the last readable byte.
        fn read_ending_at_boundary(&self, bytes: &[u8]) -> Option<PowerSetting> {
            assert!(bytes.len() <= self.page);
            // SAFETY: the destination lies within the readable first page.
            let start = unsafe { self.base.add(self.page - bytes.len()) };
            unsafe { std::ptr::copy_nonoverlapping(bytes.as_ptr(), start, bytes.len()) };
            // SAFETY: the buffer is the header and the payload it declares.
            unsafe {
                power_setting_of(
                    SERVICE_CONTROL_POWEREVENT,
                    PBT_POWERSETTINGCHANGE,
                    start.cast::<c_void>(),
                )
            }
        }
    }

    impl Drop for GuardPage {
        fn drop(&mut self) {
            use windows_sys::Win32::System::Memory::{MEM_RELEASE, VirtualFree};
            // SAFETY: the allocation this guard owns.
            unsafe { VirtualFree(self.base.cast::<c_void>(), 0, MEM_RELEASE) };
        }
    }

    #[test]
    fn the_read_stops_at_the_declared_payload_before_a_guard_page() {
        let guard = GuardPage::new();
        let with = |guid: [u8; 16], declared: u32, payload: &[u8]| {
            let mut bytes = guid.to_vec();
            bytes.extend_from_slice(&declared.to_le_bytes());
            bytes.extend_from_slice(payload);
            bytes
        };
        // Header only, then three of a DWORD: either read faults if it copies
        // further than DataLength.
        assert_eq!(
            guard.read_ending_at_boundary(&with(ACDC_POWER_SOURCE, 0, &[])),
            None
        );
        assert_eq!(
            guard.read_ending_at_boundary(&with(CONSOLE_DISPLAY_STATE, 3, &[1, 0, 0])),
            None
        );
        // A full DWORD that ends at the boundary still decodes.
        assert_eq!(
            guard.read_ending_at_boundary(&with(CONSOLE_DISPLAY_STATE, 4, &1u32.to_le_bytes())),
            Some(PowerSetting::ConsoleDisplay(1))
        );
    }
}

/// `control` forwards each SCM control, with its decoded payload, to the
/// bound ingress. The ingress binds once per process, and only this test in
/// each test binary binds it. Other tests call `control` too (the stop
/// trampoline test in `windows/scm/tests.rs`), so once it is bound their
/// controls are recorded as well, concurrently.
#[cfg(test)]
mod control_tests {
    use std::{ffi::c_void, sync::Mutex};

    use openblade_domain::power_setting::{
        CONSOLE_DISPLAY_STATE, PBT_POWERSETTINGCHANGE, PowerSetting,
    };
    use windows_sys::Win32::System::Services::SERVICE_CONTROL_POWEREVENT;

    use super::{control, set_ingress};

    type Seen = (u32, u32, Option<PowerSetting>);
    static SEEN: Mutex<Vec<Seen>> = Mutex::new(Vec::new());

    /// Runs inside the `extern "system"` handler, where a panic aborts the
    /// process: it tolerates a lock poisoned by a failed assertion elsewhere.
    fn record(code: u32, event: u32, setting: Option<PowerSetting>) {
        SEEN.lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner())
            .push((code, event, setting));
    }

    #[test]
    fn control_forwards_the_code_event_and_decoded_setting_to_the_ingress() {
        set_ingress(record).unwrap();
        assert!(set_ingress(record).is_err(), "the ingress binds once");
        let mut on = CONSOLE_DISPLAY_STATE.to_vec();
        on.extend_from_slice(&4u32.to_le_bytes());
        on.extend_from_slice(&1u32.to_le_bytes());
        // SAFETY: the buffer is a complete POWERBROADCAST_SETTING; the
        // suspend control carries no data.
        unsafe {
            control(
                SERVICE_CONTROL_POWEREVENT,
                PBT_POWERSETTINGCHANGE,
                on.as_mut_ptr().cast::<c_void>(),
                std::ptr::null_mut(),
            );
            control(
                SERVICE_CONTROL_POWEREVENT,
                4,
                std::ptr::null_mut(),
                std::ptr::null_mut(),
            );
        }
        // Only the power events this test sent; copied out so no assertion
        // fails while the recorder's lock is held.
        let power: Vec<Seen> = SEEN
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner())
            .iter()
            .filter(|(code, _, _)| *code == SERVICE_CONTROL_POWEREVENT)
            .copied()
            .collect();
        assert_eq!(
            power,
            [
                (
                    SERVICE_CONTROL_POWEREVENT,
                    PBT_POWERSETTINGCHANGE,
                    Some(PowerSetting::ConsoleDisplay(1))
                ),
                (SERVICE_CONTROL_POWEREVENT, 4, None),
            ]
        );
    }
}

#[cfg(test)]
mod installed_notice_tests {
    use openblade_domain::power_setting::{PBT_POWERSETTINGCHANGE, PowerSetting};
    use windows_sys::Win32::System::Services::{
        SERVICE_CONTROL_POWEREVENT, SERVICE_CONTROL_SHUTDOWN, SERVICE_CONTROL_STOP,
    };

    use super::{Lifecycle, installed_notice};

    #[test]
    fn a_power_setting_notice_reaches_the_lifecycle_and_terminal_controls_only_stop() {
        let lifecycle = Lifecycle::default();
        let mut stops = 0;
        installed_notice(&lifecycle, SERVICE_CONTROL_POWEREVENT, 4, None, || {
            stops += 1
        });
        for level in [0, 1] {
            installed_notice(
                &lifecycle,
                SERVICE_CONTROL_POWEREVENT,
                PBT_POWERSETTINGCHANGE,
                Some(PowerSetting::ConsoleDisplay(level)),
                || stops += 1,
            );
        }
        let recovered = lifecycle.low_power().unwrap();
        assert!(!recovered.suspending(), "off then on cleared the gate");
        let epoch = lifecycle.active().unwrap();
        for code in [SERVICE_CONTROL_STOP, SERVICE_CONTROL_SHUTDOWN] {
            installed_notice(&lifecycle, code, 4, None, || stops += 1);
        }
        assert_eq!(stops, 2);
        assert_eq!(
            lifecycle.active(),
            Some(epoch),
            "a terminal control does not fence"
        );
    }
}

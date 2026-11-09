//! Event-based Win32 counterpart to the managed IOCP path. There is no
//! handle opener here. The tests supply only a fresh non-HID kernel handle.

use super::io::{
    CancelResult, Completion, Direction, FeatureIo, Frame, Limit, Pending, Transfer, finish_pending,
};
use super::{HandleIdentity, OpenedHandle};
use std::{
    cell::UnsafeCell,
    os::windows::io::{AsRawHandle, FromRawHandle, OwnedHandle},
    ptr::{null, null_mut},
    thread,
    time::Duration,
};
use windows_sys::Win32::{
    Devices::HumanInterfaceDevice::{
        HIDD_ATTRIBUTES, HIDP_CAPS, HIDP_STATUS_SUCCESS, HidD_FreePreparsedData,
        HidD_GetAttributes, HidD_GetPreparsedData, HidP_GetCaps, PHIDP_PREPARSED_DATA,
    },
    Foundation::{ERROR_IO_INCOMPLETE, ERROR_IO_PENDING, GetLastError, HANDLE, WAIT_FAILED},
    System::{
        IO::{CancelIoEx, DeviceIoControl, GetOverlappedResult, OVERLAPPED},
        Threading::{CreateEventW, WaitForSingleObject},
    },
};

// Matches WindowsHidNative and WindowsRazerHidSession. The pinned
// windows-sys crate does not export these HIDClass control codes.
const IOCTL_HID_SET_FEATURE: u32 = 0x000B0191;
const IOCTL_HID_GET_FEATURE: u32 = 0x000B0192;

struct Native {
    handle: OwnedHandle,
}

impl OpenedHandle for Native {
    fn identity(&self) -> Option<HandleIdentity> {
        let handle = self.handle.as_raw_handle();
        let mut attributes = HIDD_ATTRIBUTES {
            Size: size_of::<HIDD_ATTRIBUTES>() as u32,
            ..Default::default()
        };
        // SAFETY: The owned handle is live and the output has the declared size.
        if !unsafe { HidD_GetAttributes(handle, &mut attributes) } {
            return None;
        }
        let mut raw = 0;
        // SAFETY: HIDClass writes an owned preparsed data pointer on success.
        if !unsafe { HidD_GetPreparsedData(handle, &mut raw) } || raw == 0 {
            return None;
        }
        let data = PreparsedData(raw);
        let mut caps = HIDP_CAPS::default();
        // SAFETY: The preparsed data stays live and caps is writable.
        if unsafe { HidP_GetCaps(data.0, &mut caps) } != HIDP_STATUS_SUCCESS {
            return None;
        }
        Some(HandleIdentity {
            vendor: attributes.VendorID,
            product: attributes.ProductID,
            usage_page: caps.UsagePage,
            usage: caps.Usage,
            feature_bytes: caps.FeatureReportByteLength,
        })
    }
}

struct PreparsedData(PHIDP_PREPARSED_DATA);

impl Drop for PreparsedData {
    fn drop(&mut self) {
        // SAFETY: The successful HIDClass allocation is freed exactly once.
        unsafe { HidD_FreePreparsedData(self.0) };
    }
}

#[test]
fn win32_opened_handle_evidence_rejects_a_non_hid_handle() {
    assert_eq!(fresh_pipe().identity(), None);
}

impl FeatureIo for Native {
    fn transfer(&mut self, direction: Direction, frame: &mut Frame, limit: Limit<'_>) -> Transfer {
        self.run(frame, limit, |handle, buffer, overlapped| {
            let length = size_of::<Frame>() as u32;
            // SAFETY: run owns the 91-byte buffer and OVERLAPPED through
            // completion. SET uses input only and GET uses output only.
            unsafe {
                match direction {
                    Direction::Set => DeviceIoControl(
                        handle,
                        IOCTL_HID_SET_FEATURE,
                        buffer.cast(),
                        length,
                        null_mut(),
                        0,
                        null_mut(),
                        overlapped,
                    ),
                    Direction::Get => DeviceIoControl(
                        handle,
                        IOCTL_HID_GET_FEATURE,
                        null(),
                        0,
                        buffer.cast(),
                        length,
                        null_mut(),
                        overlapped,
                    ),
                }
            }
        })
    }
}

impl Native {
    fn run(
        &mut self,
        frame: &mut Frame,
        limit: Limit<'_>,
        submit: impl FnOnce(HANDLE, *mut Frame, *mut OVERLAPPED) -> i32,
    ) -> Transfer {
        if let Some(reason) = limit.stop() {
            return Transfer::BeforeSubmission(reason);
        }

        // SAFETY: An unnamed, private, manual-reset event. A null return
        // indicates failure. The event lives at least as long as the request.
        let raw_event = unsafe { CreateEventW(null(), 1, 0, null()) };
        if raw_event.is_null() {
            return Transfer::PreparationFailed(last_error());
        }
        // SAFETY: CreateEventW returned a new owned handle.
        let event = unsafe { OwnedHandle::from_raw_handle(raw_event) };
        let mut operation = Operation {
            buffer: UnsafeCell::new(*frame),
            overlapped: UnsafeCell::new(OVERLAPPED {
                hEvent: event.as_raw_handle(),
                ..OVERLAPPED::default()
            }),
            event,
        };
        if let Some(reason) = limit.stop() {
            return Transfer::BeforeSubmission(reason);
        }

        // The guard borrows all the storage that must survive until completion.
        let mut pending = NativePending {
            handle: &self.handle,
            operation: &operation,
            outstanding: false,
        };
        let result = if submit(
            self.handle.as_raw_handle(),
            operation.buffer.get(),
            operation.overlapped.get(),
        ) != 0
        {
            pending.outstanding = true;
            finish_pending(&mut pending, limit)
        } else {
            let error = last_error();
            if error == ERROR_IO_PENDING {
                pending.outstanding = true;
                finish_pending(&mut pending, limit)
            } else {
                Transfer::Rejected(error)
            }
        };
        drop(pending);
        *frame = *operation.buffer.get_mut();
        result
    }
}

struct Operation {
    buffer: UnsafeCell<Frame>,
    overlapped: UnsafeCell<OVERLAPPED>,
    event: OwnedHandle,
}

struct NativePending<'a> {
    handle: &'a OwnedHandle,
    operation: &'a Operation,
    outstanding: bool,
}

impl Pending for NativePending<'_> {
    fn poll(&mut self) -> Option<Completion> {
        let mut transferred = 0;
        // SAFETY: Both handles and all submission storage are still live.
        // No other code polls or cancels this OVERLAPPED.
        let ok = unsafe {
            GetOverlappedResult(
                self.handle.as_raw_handle(),
                self.operation.overlapped.get(),
                &mut transferred,
                0,
            )
        };
        let completion = if ok != 0 {
            Ok(transferred)
        } else {
            let error = last_error();
            if error == ERROR_IO_INCOMPLETE {
                return None;
            }
            Err(error)
        };
        self.outstanding = false;
        Some(completion)
    }

    fn cancel(&mut self) -> CancelResult {
        // SAFETY: Target this operation only. All storage stays alive through
        // its own completion, including when no pending request is found.
        if unsafe { CancelIoEx(self.handle.as_raw_handle(), self.operation.overlapped.get()) } != 0
        {
            Ok(())
        } else {
            Err(last_error())
        }
    }
    fn wait_for_progress(&mut self, timeout: Duration) {
        let milliseconds = timeout.as_nanos().div_ceil(1_000_000) as u32;
        // SAFETY: The request owns this event through terminal completion.
        // A wait result never substitutes for GetOverlappedResult.
        if unsafe { WaitForSingleObject(self.operation.event.as_raw_handle(), milliseconds) }
            == WAIT_FAILED
        {
            thread::sleep(timeout);
        }
    }
}

impl Drop for NativePending<'_> {
    fn drop(&mut self) {
        if self.outstanding {
            // During unwinding, the caller may not have drained the request.
            // There is no safe deadline for releasing kernel-owned storage.
            let _ = self.cancel();
            while self.poll().is_none() {
                self.wait_for_progress(Duration::from_millis(100));
            }
        }
    }
}

fn last_error() -> u32 {
    // SAFETY: Read the current thread's last error immediately after failure.
    unsafe { GetLastError() }
}

#[test]
fn win32_ioctls_link_and_a_non_hid_handle_rejects_get() {
    let mut native = fresh_pipe();
    let canceled = std::sync::atomic::AtomicBool::new(false);
    let mut blank = [0; super::io::FRAME_LENGTH];
    let result = native.transfer(
        Direction::Get,
        &mut blank,
        Limit {
            canceled: &canceled,
            deadline: std::time::Instant::now() + Duration::from_secs(2),
        },
    );
    assert!(
        matches!(result, Transfer::Rejected(_) | Transfer::Completed(Err(_))),
        "non-HID handle unexpectedly accepted a HID feature GET: {result:?}"
    );
}

#[test]
fn win32_pending_pipe_connect_completes_after_targeted_cancel() {
    use windows_sys::Win32::{
        Foundation::ERROR_OPERATION_ABORTED, System::Pipes::ConnectNamedPipe,
    };

    let mut native = fresh_pipe();
    let canceled = std::sync::atomic::AtomicBool::new(false);
    let limit = Limit {
        canceled: &canceled,
        deadline: std::time::Instant::now() + Duration::from_secs(2),
    };
    let mut unused = [0; super::io::FRAME_LENGTH];
    let result = native.run(&mut unused, limit, |handle, _, overlapped| {
        // SAFETY: handle is an overlapped named pipe. The fresh name has no
        // client, so this starts pending without device I/O.
        unsafe { ConnectNamedPipe(handle, overlapped) }
    });
    assert_eq!(
        result,
        Transfer::Canceled {
            reason: super::io::Stop::Deadline,
            request: Ok(()),
            completion: Err(ERROR_OPERATION_ABORTED),
        }
    );
}

fn fresh_pipe() -> Native {
    use std::sync::atomic::{AtomicU32, Ordering};
    use windows_sys::Win32::{
        Foundation::INVALID_HANDLE_VALUE,
        Storage::FileSystem::{
            FILE_FLAG_FIRST_PIPE_INSTANCE, FILE_FLAG_OVERLAPPED, PIPE_ACCESS_DUPLEX,
        },
        System::Pipes::{CreateNamedPipeW, PIPE_REJECT_REMOTE_CLIENTS},
    };
    static INSTANCE: AtomicU32 = AtomicU32::new(0);
    let name: Vec<u16> = format!(
        r"\\.\pipe\OpenBlade.Rust.Transport.Test.{}.{}",
        std::process::id(),
        INSTANCE.fetch_add(1, Ordering::Relaxed),
    )
    .encode_utf16()
    .chain([0])
    .collect();
    // SAFETY: terminated UTF-16 name and default security attributes.
    let handle = unsafe {
        CreateNamedPipeW(
            name.as_ptr(),
            PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED | FILE_FLAG_FIRST_PIPE_INSTANCE,
            PIPE_REJECT_REMOTE_CLIENTS,
            1,
            64,
            64,
            0,
            null(),
        )
    };
    assert_ne!(
        handle,
        INVALID_HANDLE_VALUE,
        "CreateNamedPipeW: {}",
        last_error()
    );
    // SAFETY: successful call returned a new handle owned by this test.
    Native {
        handle: unsafe { OwnedHandle::from_raw_handle(handle) },
    }
}

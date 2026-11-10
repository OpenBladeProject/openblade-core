//! Private Win32 composition. No installed caller has live owner admission yet.
#![allow(unsafe_code)]

use super::{
    Authorize, Frame, Limit, Port, Reader, Selection, Transactions, Transfer, blocked,
    reviewed_descriptor, reviewed_selection, verify_admission,
};
use crate::discovery::{
    hid_lease::{LeaseIdentity, windows::MetadataHandle},
    windows::WindowsMetadata,
};
use std::{
    cell::UnsafeCell,
    io,
    os::windows::io::{AsRawHandle, FromRawHandle, OwnedHandle},
    ptr::{NonNull, null, null_mut},
    sync::{
        atomic::{AtomicBool, Ordering},
        mpsc,
    },
    thread::{self, JoinHandle},
};
use windows_sys::Win32::{
    Foundation::{
        ERROR_IO_INCOMPLETE, ERROR_IO_PENDING, GENERIC_READ, GENERIC_WRITE, WAIT_OBJECT_0,
    },
    System::{
        IO::{CancelIoEx, DeviceIoControl, GetOverlappedResult, OVERLAPPED},
        Threading::{CreateEventW, WaitForSingleObject},
    },
};

// The first is a HID transport request carrying the reviewed firmware getter.
const IOCTL_HID_SET_FEATURE: u32 = 0x000B0191;
const IOCTL_HID_GET_FEATURE: u32 = 0x000B0192;
static OCCUPIED: AtomicBool = AtomicBool::new(false);
// Canceling Windows I/O does not flush the firmware reply mailbox. Carry
// correlation across every lease in this process and refuse exhaustion.
static TRANSACTIONS: Transactions = Transactions::new();

struct Slot;

impl Slot {
    fn acquire() -> io::Result<Self> {
        OCCUPIED
            .compare_exchange(false, true, Ordering::AcqRel, Ordering::Acquire)
            .map(|_| Self)
            .map_err(|_| blocked("firmware HID owner is occupied or retirement is unconfirmed"))
    }
}

impl Drop for Slot {
    fn drop(&mut self) {
        OCCUPIED.store(false, Ordering::Release);
    }
}

pub(super) struct NativePort {
    lease: Option<Lease>,
    identity: Option<LeaseIdentity>,
    retirement: Option<JoinHandle<io::Result<()>>>,
    retirement_failed: bool,
}

struct Lease {
    operation: Option<Operation>,
    handle: MetadataHandle,
    slot: Slot,
}

struct Operation {
    storage: NonNull<Storage>,
    synchronous: bool,
    cancel_requested: bool,
    outstanding: bool,
}

impl Operation {
    fn storage(&self) -> &Storage {
        // SAFETY: this allocation is owned by the operation and kept through
        // terminal completion. Win32 can only access its UnsafeCell fields.
        unsafe { self.storage.as_ref() }
    }
}

impl Drop for Operation {
    fn drop(&mut self) {
        if !self.outstanding {
            // SAFETY: The operation is the sole owner of this allocation.
            // Win32 never acquired it, or terminal completion returned it.
            unsafe { drop(Box::from_raw(self.storage.as_ptr())) };
        }
    }
}

struct Storage {
    buffer: UnsafeCell<Frame>,
    overlapped: UnsafeCell<OVERLAPPED>,
    event: OwnedHandle,
}

impl Reader<NativePort> {
    pub(super) fn open(authorize: Authorize<'_>, limit: Limit<'_>) -> io::Result<Self> {
        limit.check()?;
        let admission = authorize()
            .filter(|admission| admission.is_clear())
            .ok_or_else(|| blocked("no live firmware HID owner"))?;
        let slot = Slot::acquire()?;
        let selection = Selection::fresh(&WindowsMetadata, limit.cancelled)?;
        if !reviewed_selection(&selection) {
            return Err(blocked("unsupported firmware performance model or role"));
        }
        verify_admission(authorize, admission)?;
        limit.check()?;
        // Match the managed operational open. Shared OS access is not proof
        // of sole service ownership. The independent admission is mandatory.
        let handle = MetadataHandle::open(&selection.hint.path, GENERIC_READ | GENERIC_WRITE)?;
        let mut port = NativePort {
            lease: Some(Lease {
                operation: None,
                handle,
                slot,
            }),
            identity: None,
            retirement: None,
            retirement_failed: false,
        };
        let identity = LeaseIdentity::verify(
            selection,
            &WindowsMetadata,
            &port.lease.as_ref().unwrap().handle,
            limit.cancelled,
        )?;
        if !reviewed_descriptor(identity.descriptor) {
            return Err(blocked("unsupported opened firmware HID role"));
        }
        verify_admission(authorize, admission)?;
        limit.check()?;
        port.identity = Some(identity);
        Ok(Self::new(port, admission))
    }
}

impl Port for NativePort {
    fn reserve_pair(&mut self) -> Option<[u8; 2]> {
        TRANSACTIONS.reserve_pair()
    }
    fn verify(&mut self, limit: Limit<'_>) -> io::Result<()> {
        let lease = self
            .lease
            .as_ref()
            .ok_or_else(|| blocked("firmware HID lease retired"))?;
        let identity = self
            .identity
            .as_ref()
            .ok_or_else(|| blocked("firmware HID identity unavailable"))?;
        identity.revalidate(&WindowsMetadata, &lease.handle, limit.cancelled)
    }

    fn submit(&mut self, transfer: Transfer) -> io::Result<()> {
        let lease = self
            .lease
            .as_mut()
            .ok_or_else(|| blocked("firmware HID lease retired"))?;
        if lease.operation.is_some() {
            return Err(blocked("firmware HID operation still outstanding"));
        }
        // SAFETY: An unnamed, private, manual-reset event owned by this operation.
        let event = unsafe { CreateEventW(null(), 1, 0, null()) };
        if event.is_null() {
            return Err(io::Error::last_os_error());
        }
        // SAFETY: Successful CreateEventW returned an owned handle.
        let event = unsafe { OwnedHandle::from_raw_handle(event) };
        let sending = matches!(transfer, Transfer::SendGetter(_));
        let buffer = match transfer {
            Transfer::SendGetter(frame) => frame,
            Transfer::ReceiveReply => [0; super::FRAME_LENGTH],
        };
        let mut operation = Operation {
            storage: NonNull::new(Box::into_raw(Box::new(Storage {
                buffer: UnsafeCell::new(buffer),
                overlapped: UnsafeCell::new(OVERLAPPED {
                    hEvent: event.as_raw_handle(),
                    ..OVERLAPPED::default()
                }),
                event,
            })))
            .expect("Box has a nonnull allocation"),
            synchronous: false,
            cancel_requested: false,
            outstanding: false,
        };
        let storage = operation.storage();
        let length = super::FRAME_LENGTH as u32;
        let frame = storage.buffer.get().cast();
        let raw = lease.handle.as_raw_handle();
        // SAFETY: The retained lease owns the handle. The stable allocation
        // owns buffer, event and OVERLAPPED until terminal completion.
        let result = unsafe {
            if sending {
                DeviceIoControl(
                    raw,
                    IOCTL_HID_SET_FEATURE,
                    frame,
                    length,
                    null_mut(),
                    0,
                    null_mut(),
                    storage.overlapped.get(),
                )
            } else {
                DeviceIoControl(
                    raw,
                    IOCTL_HID_GET_FEATURE,
                    null(),
                    0,
                    frame,
                    length,
                    null_mut(),
                    storage.overlapped.get(),
                )
            }
        };
        if result == 0 {
            let error = io::Error::last_os_error();
            if error.raw_os_error() != Some(ERROR_IO_PENDING as i32) {
                return Err(error);
            }
        }
        operation.synchronous = result != 0;
        operation.outstanding = true;
        lease.operation = Some(operation);
        Ok(())
    }

    fn poll(&mut self) -> Option<io::Result<(usize, Frame)>> {
        let lease = self.lease.as_mut()?;
        let operation = lease.operation.as_ref()?;
        // SAFETY: The operation owns this event throughout the nonblocking wait.
        if !operation.synchronous
            && unsafe { WaitForSingleObject(operation.storage().event.as_raw_handle(), 0) }
                != WAIT_OBJECT_0
        {
            return None;
        }

        let mut bytes = 0;
        // SAFETY: All operation storage and the original handle are still live.
        let ok = unsafe {
            GetOverlappedResult(
                lease.handle.as_raw_handle(),
                operation.storage().overlapped.get(),
                &mut bytes,
                0,
            )
        };
        let error = if ok == 0 {
            let error = io::Error::last_os_error();
            if error.raw_os_error() == Some(ERROR_IO_INCOMPLETE as i32) {
                return None;
            }
            Some(error)
        } else {
            None
        };
        let mut operation = lease.operation.take().expect("operation was polled");
        operation.outstanding = false;
        Some(match error {
            Some(error) => Err(error),
            // SAFETY: Terminal completion returned the buffer to this owner.
            None => Ok((bytes as usize, unsafe { *operation.storage().buffer.get() })),
        })
    }

    fn cancel(&mut self) {
        let Some(lease) = self.lease.as_mut() else {
            return;
        };
        let Some(operation) = lease.operation.as_mut() else {
            return;
        };
        if operation.cancel_requested {
            return;
        }
        operation.cancel_requested = true;
        // SAFETY: Targets the operation on its original handle. All storage
        // remains owned even when cancellation races with completion or fails.
        unsafe {
            CancelIoEx(
                lease.handle.as_raw_handle(),
                operation.storage().overlapped.get(),
            )
        };
    }

    fn poll_retirement(&mut self) -> io::Result<bool> {
        if self.retirement_failed {
            return Err(blocked("firmware HID callback retirement is unconfirmed"));
        }
        if let Some(worker) = &self.retirement {
            if !worker.is_finished() {
                return Ok(false);
            }
            let worker = self.retirement.take().expect("retirement worker finished");
            let result = worker
                .join()
                .map_err(|_| io::Error::other("firmware HID retirement panicked"))
                .and_then(|result| result);
            self.retirement_failed = result.is_err();
            return result.map(|()| true);
        }
        let _ = self.poll();
        let Some(lease) = &self.lease else {
            return Ok(true);
        };
        if lease.operation.is_some() {
            return Ok(false);
        }

        // ConfigMgr waits for callback drain synchronously. Start one owned
        // reaper before transferring the handle, so spawn failure preserves
        // the lease and the caller never waits in ConfigMgr.
        let (send, receive) = mpsc::channel::<(MetadataHandle, Slot)>();
        let worker = thread::Builder::new()
            .name("openblade-hid-retire".into())
            .spawn(move || {
                let (mut handle, slot) = receive.recv().map_err(io::Error::other)?;
                if let Err(error) = handle.retire() {
                    std::mem::forget((handle, slot));
                    return Err(error);
                }
                drop(handle);
                drop(slot);
                Ok(())
            });
        let worker = match worker {
            Ok(worker) => worker,
            Err(error) => {
                self.retirement_failed = true;
                return Err(error);
            }
        };
        let Lease { handle, slot, .. } = self.lease.take().expect("retiring an owned lease");
        if let Err(error) = send.send((handle, slot)) {
            self.retirement_failed = true;
            std::mem::forget(error.0);
            return Err(blocked("firmware HID retirement worker lost"));
        }
        self.retirement = Some(worker);
        Ok(false)
    }
}

impl Drop for NativePort {
    fn drop(&mut self) {
        self.cancel();
        if !matches!(self.poll_retirement(), Ok(true)) {
            // An outstanding kernel operation keeps its one process-wide slot
            // until exit. A callback reaper already owns the slot if started
            // and releases it only after the handle has closed.
            std::mem::forget(self.lease.take());
        }
    }
}

#[cfg(test)]
#[test]
fn native_firmware_observation_links_and_refuses_without_owner() {
    // Link the real open/read/retire code. Execute only the admission refusal.
    let _ = std::hint::black_box(Reader::<NativePort>::observe);
    let _ = std::hint::black_box(Reader::<NativePort>::retire);
    let cancelled = AtomicBool::new(false);
    let limit = Limit {
        deadline: tokio::time::Instant::now() + std::time::Duration::from_secs(1),
        cancelled: &cancelled,
    };
    let error = Reader::open(&mut || std::hint::black_box(None), limit)
        .err()
        .expect("no installed owner authority");
    assert_eq!(error.kind(), io::ErrorKind::PermissionDenied);
    assert_eq!(error.to_string(), "no live firmware HID owner");
}

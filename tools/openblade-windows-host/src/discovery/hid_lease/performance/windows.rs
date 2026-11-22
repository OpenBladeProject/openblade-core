//! Private Win32 composition. Construct and retire the lease on the observation thread.
#![allow(unsafe_code)]

use super::{
    Admission, Authorize, Frame, Limit, ObservationProof, Port, ReadAdmission, Reader, Selection,
    Transactions, Transfer, blocked, reviewed_descriptor, reviewed_selection, verify_admission,
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
    sync::atomic::AtomicBool,
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
// Canceling Windows I/O or reopening the handle does not flush the firmware
// reply mailbox, so one pool spans every lease in this process and frees an
// ID only with proof that its reply is gone (see `transactions`).
static TRANSACTIONS: Transactions = Transactions::new();

pub(super) struct NativePort {
    lease: Option<Lease>,
    identity: Option<LeaseIdentity>,
}

struct Lease {
    operation: Option<Operation>,
    handle: MetadataHandle,
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

use super::worker::{Job, Reading, Worker};

pub(crate) fn native_worker(
    authorize: impl Fn() -> Option<Admission> + Send + Sync + 'static,
) -> Worker {
    Worker::new(move |job| run(&authorize, job), Some(&OCCUPIED))
}

pub(crate) fn native_read_worker(
    authorize: impl Fn() -> Option<ReadAdmission> + Send + Sync + 'static,
) -> Worker<ReadAdmission> {
    Worker::new(move |job| run(&authorize, job), Some(&OCCUPIED))
}

fn run<A: ObservationProof>(
    authorize: &impl Fn() -> Option<A>,
    job: Job,
) -> io::Result<Option<Reading<A>>> {
    let Ok(runtime) = tokio::runtime::Builder::new_current_thread()
        .enable_time()
        .build()
    else {
        return Ok(None);
    };
    let limit = job.limit();
    let mut authorize = || authorize().filter(|admission| admission.matches_session(job.session));
    let Some(admission) = authorize().filter(|admission| admission.readable()) else {
        return Ok(None);
    };
    let Ok((port, selection)) = NativePort::open(&mut authorize, admission, limit) else {
        return Ok(None);
    };
    let mut reader = Reader::new(port, admission);
    runtime.block_on(async {
        let bound = reader
            .port
            .bind(selection, &mut authorize, admission, limit)
            .is_ok();
        reader.job(&mut authorize, &job, bound).await
    })
}

impl NativePort {
    fn open<A: ObservationProof>(
        authorize: Authorize<'_, A>,
        admission: A,
        limit: Limit<'_>,
    ) -> io::Result<(Self, Selection)> {
        limit.check()?;
        let selection = Selection::fresh(&WindowsMetadata, limit.cancelled)?;
        if !reviewed_selection(&selection) {
            return Err(blocked("unsupported firmware performance model or role"));
        }
        verify_admission(authorize, admission)?;
        limit.check()?;
        // Shared OS access is not proof of sole service ownership.
        let handle = MetadataHandle::open(&selection.hint.path, GENERIC_READ | GENERIC_WRITE)?;
        let port = Self {
            lease: Some(Lease {
                operation: None,
                handle,
            }),
            identity: None,
        };
        Ok((port, selection))
    }

    fn bind<A: ObservationProof>(
        &mut self,
        selection: Selection,
        authorize: Authorize<'_, A>,
        admission: A,
        limit: Limit<'_>,
    ) -> io::Result<()> {
        limit.check()?;
        let identity = LeaseIdentity::verify(
            selection,
            &WindowsMetadata,
            &self.lease.as_ref().expect("opened firmware lease").handle,
            limit.cancelled,
        )?;
        if !reviewed_descriptor(identity.descriptor) {
            return Err(blocked("unsupported opened firmware HID role"));
        }
        verify_admission(authorize, admission)?;
        limit.check()?;
        self.identity = Some(identity);
        Ok(())
    }
}

impl Port for NativePort {
    fn transactions(&self) -> &Transactions {
        &TRANSACTIONS
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

    fn submit(&mut self, transfer: Transfer, limit: Limit<'_>) -> io::Result<()> {
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
        let sending = !matches!(transfer, Transfer::ReceiveReply);
        let buffer = match transfer {
            Transfer::SendGetter(frame) => frame,
            #[cfg(feature = "service-thermal-mutation-v1")]
            Transfer::SendMutation(frame) => frame,
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
        limit.check()?;
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
        let _ = self.poll();
        let Some(lease) = &mut self.lease else {
            return Ok(true);
        };
        if lease.operation.is_some() {
            return Ok(false);
        }

        // ConfigMgr drains callbacks synchronously. A stalled unregister
        // keeps this thread and the process-wide fence occupied.
        lease.handle.retire()?;
        drop(self.lease.take());
        Ok(true)
    }
}

impl Drop for NativePort {
    fn drop(&mut self) {
        // Only explicit retirement proves that Windows returned all resources.
        // On unregister error or unwind retain the handle and any operation.
        std::mem::forget(self.lease.take());
    }
}

#[cfg(test)]
#[test]
fn native_firmware_observation_links() {
    // Retain the Windows composition in the test image without invoking it.
    let link = |authorize: fn() -> Option<Admission>| native_worker(authorize);
    std::hint::black_box(link as fn(fn() -> Option<Admission>) -> Worker);
}

#[cfg(feature = "service-thermal-mutation-v1")]
pub(super) fn mutation_worker(
    permit: std::sync::Arc<dyn super::mutation::Permit>,
) -> Worker<ReadAdmission> {
    Worker::new(
        move |job| {
            let runtime = tokio::runtime::Builder::new_current_thread()
                .enable_time()
                .build()?;
            let limit = job.limit();
            let mut authorize = || permit.authorize();
            let Some(admission) = authorize() else {
                return Ok(None);
            };
            let (port, selection) = NativePort::open(&mut authorize, admission, limit)?;
            let mut reader = Reader::new(port, admission);
            let observed = runtime.block_on(async {
                let result = if reader
                    .port
                    .bind(selection, &mut authorize, admission, limit)
                    .is_ok()
                {
                    reader.apply_preset(&*permit, limit).await
                } else {
                    None
                };
                reader.retire(None).await?;
                Ok::<_, io::Error>(result)
            })?;
            Ok(observed
                .filter(|_| limit.check().is_ok() && permit.result_admitted(admission))
                .map(|performance| Reading::thermal(admission, performance)))
        },
        Some(&OCCUPIED),
    )
}

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
    os::windows::io::{AsRawHandle, FromRawHandle, OwnedHandle, RawHandle},
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
static TRANSACTIONS: Transactions = Transactions::randomized();

#[cfg(feature = "service-thermal-mutation-v1")]
pub(super) fn quarantine(ids: &[u8]) {
    TRANSACTIONS.quarantine(ids);
}

/// Whether `quarantine` withholds a free ID of the pool the native workers
/// share: it quarantines one the pool just reserved and released.
#[cfg(all(test, feature = "service-thermal-mutation-v1"))]
pub(super) fn quarantines_process_pool(quarantine: &dyn Fn(&[u8])) -> bool {
    // Callers share the one pool, so they count its free IDs one at a time.
    static SERIAL: std::sync::Mutex<()> = std::sync::Mutex::new(());
    let _serial = SERIAL
        .lock()
        .unwrap_or_else(std::sync::PoisonError::into_inner);
    let Some(pair) = TRANSACTIONS.reserve_pair() else {
        return false;
    };
    pair.into_iter().for_each(|id| TRANSACTIONS.release(id));
    let free = TRANSACTIONS.free();
    quarantine(&pair[..1]);
    TRANSACTIONS.free() + 1 == free
}

#[cfg(all(test, feature = "service-thermal-mutation-v1"))]
#[test]
fn the_native_quarantine_reaches_the_process_pool() {
    assert!(quarantines_process_pool(
        &super::mutation::quarantine_native
    ));
}

pub(super) struct NativePort {
    lease: Option<Lease>,
    identity: Option<LeaseIdentity>,
    /// The exact matrix collection (`Port::open_matrix`), retired with the
    /// control lease.
    #[cfg(feature = "service-lighting-v1")]
    matrix: Option<MatrixLease>,
}

struct Lease {
    operation: Option<Operation<{ super::FRAME_LENGTH }>>,
    handle: MetadataHandle,
}

#[cfg(feature = "service-lighting-v1")]
struct MatrixLease {
    operation: Option<Operation<{ openblade_protocol::lighting::matrix::FEATURE_REPORT_LENGTH }>>,
    handle: MetadataHandle,
    identity: super::super::RoleIdentity,
}

struct Operation<const N: usize> {
    storage: NonNull<Storage<N>>,
    synchronous: bool,
    cancel_requested: bool,
    outstanding: bool,
}

impl<const N: usize> Operation<N> {
    fn storage(&self) -> &Storage<N> {
        // SAFETY: this allocation is owned by the operation and kept through
        // terminal completion. Win32 can only access its UnsafeCell fields.
        unsafe { self.storage.as_ref() }
    }

    /// Starts one overlapped feature IOCTL on `raw`: a set from `buffer`, or
    /// a get into it. An error from the operation's own storage sent nothing
    /// (`new`); one from the IOCTL is the driver's refusal of a call that
    /// reached it (`issue`).
    fn start(raw: RawHandle, sending: bool, buffer: [u8; N]) -> io::Result<Self> {
        let mut operation = Self::new(buffer)?;
        operation.issue(raw, sending)?;
        Ok(operation)
    }

    /// The operation's buffer, event and OVERLAPPED, before any IOCTL: an
    /// error here sent nothing.
    fn new(buffer: [u8; N]) -> io::Result<Self> {
        // SAFETY: An unnamed, private, manual-reset event owned by this operation.
        let event = unsafe { CreateEventW(null(), 1, 0, null()) };
        if event.is_null() {
            return Err(io::Error::last_os_error());
        }
        // SAFETY: Successful CreateEventW returned an owned handle.
        let event = unsafe { OwnedHandle::from_raw_handle(event) };
        Ok(Self {
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
        })
    }

    /// Issues the IOCTL on `raw`: a set from the buffer, or a get into it.
    /// An error is the driver's refusal of the call, which reached it.
    fn issue(&mut self, raw: RawHandle, sending: bool) -> io::Result<()> {
        let storage = self.storage();
        let length = N as u32;
        let frame = storage.buffer.get().cast();
        // SAFETY: The caller's retained lease owns the handle. The stable
        // allocation owns buffer, event and OVERLAPPED until terminal completion.
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
        self.synchronous = result != 0;
        self.outstanding = true;
        Ok(())
    }

    /// The terminal result of `slot`'s operation on `raw`, taking it, or
    /// `None` while it is still pending.
    fn complete(raw: RawHandle, slot: &mut Option<Self>) -> Option<io::Result<(usize, [u8; N])>> {
        let operation = slot.as_ref()?;
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
            GetOverlappedResult(raw, operation.storage().overlapped.get(), &mut bytes, 0)
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
        let mut operation = slot.take().expect("operation was polled");
        operation.outstanding = false;
        Some(match error {
            Some(error) => Err(error),
            // SAFETY: Terminal completion returned the buffer to this owner.
            None => Ok((bytes as usize, unsafe { *operation.storage().buffer.get() })),
        })
    }

    /// Requests cancellation of `slot`'s operation on `raw`, once.
    fn cancel(raw: RawHandle, slot: &mut Option<Self>) {
        let Some(operation) = slot.as_mut() else {
            return;
        };
        if operation.cancel_requested {
            return;
        }
        operation.cancel_requested = true;
        // SAFETY: Targets the operation on its original handle. All storage
        // remains owned even when cancellation races with completion or fails.
        unsafe { CancelIoEx(raw, operation.storage().overlapped.get()) };
    }
}

impl<const N: usize> Drop for Operation<N> {
    fn drop(&mut self) {
        if !self.outstanding {
            // SAFETY: The operation is the sole owner of this allocation.
            // Win32 never acquired it, or terminal completion returned it.
            unsafe { drop(Box::from_raw(self.storage.as_ptr())) };
        }
    }
}

struct Storage<const N: usize> {
    buffer: UnsafeCell<[u8; N]>,
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
            #[cfg(feature = "service-lighting-v1")]
            matrix: None,
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
        let sending = !matches!(transfer, Transfer::ReceiveReply);
        let buffer = match transfer {
            Transfer::SendGetter(frame) => frame,
            #[cfg(feature = "service-thermal-mutation-v1")]
            Transfer::SendMutation(frame) => frame,
            Transfer::ReceiveReply => [0; super::FRAME_LENGTH],
        };
        limit.check()?;
        lease.operation = Some(Operation::start(
            lease.handle.as_raw_handle(),
            sending,
            buffer,
        )?);
        Ok(())
    }

    fn poll(&mut self) -> Option<io::Result<(usize, Frame)>> {
        let lease = self.lease.as_mut()?;
        Operation::complete(lease.handle.as_raw_handle(), &mut lease.operation)
    }

    fn cancel(&mut self) {
        if let Some(lease) = self.lease.as_mut() {
            Operation::cancel(lease.handle.as_raw_handle(), &mut lease.operation);
        }
        #[cfg(feature = "service-lighting-v1")]
        if let Some(matrix) = self.matrix.as_mut() {
            Operation::cancel(matrix.handle.as_raw_handle(), &mut matrix.operation);
        }
    }

    fn poll_retirement(&mut self) -> io::Result<bool> {
        let _ = self.poll();
        // The matrix lease retires first; the control lease, the port's
        // proof of retirement, only once it has.
        #[cfg(feature = "service-lighting-v1")]
        {
            let _ = self.poll_matrix();
            if let Some(matrix) = &mut self.matrix {
                if matrix.operation.is_some() {
                    return Ok(false);
                }
                matrix.handle.retire()?;
                drop(self.matrix.take());
            }
        }
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

    /// Managed `ProductionLightingFactory`'s matrix opener: exactly one
    /// exact consumer collection, opened for writing, whose opened metadata
    /// and fresh candidates match its selection. Only while the control
    /// lease is live, so both retire together.
    #[cfg(feature = "service-lighting-v1")]
    fn open_matrix(&mut self, limit: Limit<'_>) -> io::Result<()> {
        if self.lease.is_none() || self.identity.is_none() {
            return Err(blocked("firmware HID lease retired"));
        }
        if self.matrix.is_some() {
            return Ok(());
        }
        limit.check()?;
        let identity = super::select_matrix(&WindowsMetadata, limit.cancelled)?;
        limit.check()?;
        // Shared OS access is not proof of sole service ownership.
        let handle = MetadataHandle::open(identity.path(), GENERIC_READ | GENERIC_WRITE)?;
        identity.verify(&WindowsMetadata, &handle, limit.cancelled)?;
        self.matrix = Some(MatrixLease {
            operation: None,
            handle,
            identity,
        });
        Ok(())
    }

    #[cfg(feature = "service-lighting-v1")]
    fn submit_matrix(
        &mut self,
        report: &super::MatrixReport,
        limit: Limit<'_>,
    ) -> Result<(), super::Unwritten> {
        use super::Unwritten;
        let matrix = self.matrix.as_mut().ok_or(Unwritten::Unsent)?;
        if matrix.operation.is_some() {
            return Err(Unwritten::Unsent);
        }
        matrix
            .identity
            .verify(&WindowsMetadata, &matrix.handle, limit.cancelled)
            .map_err(|_| Unwritten::Unsent)?;
        limit.check().map_err(|_| Unwritten::Unsent)?;
        let mut operation = Operation::new(*report).map_err(|_| Unwritten::Unsent)?;
        // Managed poisons its writer on any native matrix failure: a call
        // the driver refused may still have reached the keyboard.
        operation
            .issue(matrix.handle.as_raw_handle(), true)
            .map_err(|_| Unwritten::InDoubt)?;
        matrix.operation = Some(operation);
        Ok(())
    }

    #[cfg(feature = "service-lighting-v1")]
    fn poll_matrix(&mut self) -> Option<io::Result<()>> {
        let matrix = self.matrix.as_mut()?;
        Operation::complete(matrix.handle.as_raw_handle(), &mut matrix.operation)
            .map(|result| result.map(|_| ()))
    }
}

impl Drop for NativePort {
    fn drop(&mut self) {
        // Only explicit retirement proves that Windows returned all resources.
        // On unregister error or unwind retain the handle and any operation.
        std::mem::forget(self.lease.take());
        #[cfg(feature = "service-lighting-v1")]
        std::mem::forget(self.matrix.take());
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
            // Nothing is sent before the port opens, so a refusal or failure
            // up to then is a job that was not applied: `Ok(None)`, as the read
            // worker reports it (`run`). An error here would mark the lane's
            // stop unconfirmed and fence every later mutation and owned-Stop
            // cleanup behind a write that never started.
            let Ok(runtime) = tokio::runtime::Builder::new_current_thread()
                .enable_time()
                .build()
            else {
                return Ok(None);
            };
            let limit = job.limit();
            let mut authorize = || permit.authorize();
            let Some(admission) = authorize() else {
                return Ok(None);
            };
            let Ok((port, selection)) = NativePort::open(&mut authorize, admission, limit) else {
                return Ok(None);
            };
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

/// The write worker sends nothing before its port opens, so an open that
/// fails is a job that was not applied, as for the read worker: an error
/// would leave the mutation lane's stop unconfirmed and fence every later
/// mutation and owned-Stop cleanup (`ReadSource::drain_mutation`). The
/// service tests drive the same outcome through the fixture
/// (`State::mutation_open_fails`); this pins the production worker to it.
#[cfg(all(test, feature = "service-thermal-mutation-v1"))]
#[test]
fn a_mutation_port_that_fails_to_open_is_not_applied() {
    let source = include_str!("windows.rs");
    let start = source.find("pub(super) fn mutation_worker(").unwrap();
    let worker = &source[start..start + source[start..].find("Some(&OCCUPIED)").unwrap()];
    assert!(worker.contains(
        "let Ok((port, selection)) = NativePort::open(&mut authorize, admission, limit) else {\n                return Ok(None);"
    ));
    assert!(worker.contains(".build()\n            else {\n                return Ok(None);"));
    assert!(!worker.contains("limit)?;") && !worker.contains(".build()?"));
}

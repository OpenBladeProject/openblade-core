//! The Windows matrix stream (`MatrixStream`): a dedicated thread owns one
//! lease on the exact reviewed matrix collection, opened as the render
//! jobs' lease is (`NativePort::open_matrix`: `select_matrix`, the same
//! access and a fresh identity verification), and writes one 03/0B feature
//! report at a time. Before each frame it checks that the open handle still
//! describes the selected collection. Matrix I/O is serialized with the
//! render jobs' lease by `MATRIX_IO`, held from a report's issue until its
//! terminal completion.
use super::super::{MatrixReport, MatrixStream, Unwritten, select_matrix};
use super::{MATRIX_IO, MatrixLease, Operation};
use crate::discovery::{
    hid_lease::{LeaseSource, windows::MetadataHandle},
    windows::WindowsMetadata,
};
use std::{
    future::Future,
    os::windows::io::AsRawHandle,
    pin::Pin,
    sync::{Mutex, atomic::AtomicBool, atomic::Ordering, mpsc},
    time::Duration,
};
use windows_sys::Win32::Foundation::{GENERIC_READ, GENERIC_WRITE};

/// How often the thread polls a submitted report for completion.
const POLL: Duration = Duration::from_millis(1);

enum Request {
    Frame(
        Box<MatrixReport>,
        std::time::Instant,
        tokio::sync::oneshot::Sender<Result<(), Unwritten>>,
    ),
    Retire,
}

/// The native stream; its thread starts with the first frame and ends when
/// the stream is dropped, retiring the lease.
pub(crate) fn native_matrix_stream() -> std::rc::Rc<dyn MatrixStream> {
    std::rc::Rc::new(NativeStream {
        requests: Mutex::new(None),
    })
}

struct NativeStream {
    requests: Mutex<Option<mpsc::Sender<Request>>>,
}

impl NativeStream {
    fn send(&self, request: Request) -> bool {
        let Ok(mut requests) = self.requests.lock() else {
            return false;
        };
        if requests.is_none() {
            let (sender, receiver) = mpsc::channel();
            let started = std::thread::Builder::new()
                .name("openblade-matrix-stream".into())
                .spawn(move || run(receiver));
            if started.is_err() {
                return false;
            }
            *requests = Some(sender);
        }
        requests
            .as_ref()
            .is_some_and(|sender| sender.send(request).is_ok())
    }
}

impl MatrixStream for NativeStream {
    fn frame(
        &self,
        report: MatrixReport,
        deadline: tokio::time::Instant,
    ) -> Pin<Box<dyn Future<Output = Result<(), Unwritten>>>> {
        let (reply, result) = tokio::sync::oneshot::channel();
        let sent = self.send(Request::Frame(Box::new(report), deadline.into_std(), reply));
        Box::pin(async move {
            if !sent {
                return Err(Unwritten::Unsent);
            }
            // A thread that ended without replying may have submitted it.
            result.await.unwrap_or(Err(Unwritten::InDoubt))
        })
    }

    fn retire(&self) {
        let _ = self.send(Request::Retire);
    }
}

/// The stream thread's state: the open lease, and whether this thread holds
/// `MATRIX_IO` for its outstanding report.
struct Lane {
    lease: Option<MatrixLease>,
    io: bool,
}

fn run(requests: mpsc::Receiver<Request>) {
    let mut lane = Lane {
        lease: None,
        io: false,
    };
    while let Ok(request) = requests.recv() {
        match request {
            Request::Frame(report, deadline, reply) => {
                let _ = reply.send(lane.frame(&report, deadline));
            }
            Request::Retire => lane.retire(),
        }
    }
    lane.retire();
    // A report still outstanding keeps its storage and its handle.
    std::mem::forget(lane.lease.take());
}

impl Lane {
    /// Polls an outstanding report; `true` once none is.
    fn settle(&mut self) -> bool {
        let Some(lease) = self.lease.as_mut() else {
            return true;
        };
        if lease.operation.is_some() {
            let _ = Operation::complete(lease.handle.as_raw_handle(), &mut lease.operation);
        }
        if lease.operation.is_none() && self.io {
            MATRIX_IO.store(false, Ordering::Release);
            self.io = false;
        }
        lease.operation.is_none()
    }

    fn retire(&mut self) {
        if let Some(lease) = self.lease.as_mut() {
            Operation::cancel(lease.handle.as_raw_handle(), &mut lease.operation);
        }
        if self.settle()
            && let Some(mut lease) = self.lease.take()
            && lease.handle.retire().is_err()
        {
            std::mem::forget(lease);
        }
    }

    fn open(&mut self) -> Result<(), Unwritten> {
        if self.lease.is_some() {
            return Ok(());
        }
        let cancelled = AtomicBool::new(false);
        let identity =
            select_matrix(&WindowsMetadata, &cancelled).map_err(|_| Unwritten::Unsent)?;
        let handle = MetadataHandle::open(identity.path(), GENERIC_READ | GENERIC_WRITE)
            .map_err(|_| Unwritten::Unsent)?;
        identity
            .verify(&WindowsMetadata, &handle, &cancelled)
            .map_err(|_| Unwritten::Unsent)?;
        self.lease = Some(MatrixLease {
            operation: None,
            handle,
            identity,
        });
        Ok(())
    }

    fn frame(
        &mut self,
        report: &MatrixReport,
        deadline: std::time::Instant,
    ) -> Result<(), Unwritten> {
        // A report left outstanding by an earlier deadline sends nothing new.
        if !self.settle() {
            return Err(Unwritten::Unsent);
        }
        self.open()?;
        let lease = self.lease.as_mut().ok_or(Unwritten::Unsent)?;
        let described = WindowsMetadata.describe(&lease.handle);
        if described.ok() != Some(lease.identity.descriptor()) {
            self.retire();
            return Err(Unwritten::Unsent);
        }
        if std::time::Instant::now() >= deadline {
            return Err(Unwritten::Unsent);
        }
        let mut operation = Operation::new(*report).map_err(|_| Unwritten::Unsent)?;
        if MATRIX_IO
            .compare_exchange(false, true, Ordering::AcqRel, Ordering::Acquire)
            .is_err()
        {
            return Err(Unwritten::Unsent);
        }
        self.io = true;
        let raw = lease.handle.as_raw_handle();
        // A call the driver refused may still have reached the keyboard.
        if operation.issue(raw, true).is_err() {
            MATRIX_IO.store(false, Ordering::Release);
            self.io = false;
            return Err(Unwritten::InDoubt);
        }
        lease.operation = Some(operation);
        loop {
            if let Some(result) = Operation::complete(raw, &mut lease.operation) {
                MATRIX_IO.store(false, Ordering::Release);
                self.io = false;
                return result.map(|_| ()).map_err(|_| Unwritten::InDoubt);
            }
            if std::time::Instant::now() >= deadline {
                Operation::cancel(raw, &mut lease.operation);
                return Err(Unwritten::InDoubt);
            }
            std::thread::sleep(POLL);
        }
    }
}

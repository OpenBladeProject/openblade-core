//! The Windows matrix stream's I/O (`MatrixIo`) for its lane
//! (`matrix_lane`): one lease on the exact reviewed matrix collection,
//! opened as the render jobs' lease is (`NativePort::open_matrix`:
//! `select_matrix`, the same access and a fresh identity verification),
//! and one overlapped 03/0B feature report at a time. The lane serializes
//! its reports with the render jobs' lease through `MATRIX_IO`.
use super::super::{
    MatrixReport, MatrixStream, matrix_lane::LaneStream, matrix_lane::MatrixIo, select_matrix,
};
use super::{MATRIX_IO, MatrixLease, Operation};
use crate::discovery::{
    hid_lease::{LeaseSource, windows::MetadataHandle},
    windows::WindowsMetadata,
};
use std::{os::windows::io::AsRawHandle, sync::atomic::AtomicBool};
use windows_sys::Win32::Foundation::{GENERIC_READ, GENERIC_WRITE};

/// The native stream; its lane's thread starts with the first request.
pub(crate) fn native_matrix_stream() -> std::rc::Rc<dyn MatrixStream> {
    std::rc::Rc::new(LaneStream::new(WindowsMatrix::default, &MATRIX_IO))
}

type Report = Operation<{ openblade_protocol::lighting::matrix::FEATURE_REPORT_LENGTH }>;

/// The lane's lease and the report it prepared but has not issued.
#[derive(Default)]
struct WindowsMatrix {
    lease: Option<MatrixLease>,
    prepared: Option<Report>,
}

impl MatrixIo for WindowsMatrix {
    fn open(&mut self) -> bool {
        let cancelled = AtomicBool::new(false);
        let Ok(identity) = select_matrix(&WindowsMetadata, &cancelled) else {
            return false;
        };
        let Ok(handle) = MetadataHandle::open(identity.path(), GENERIC_READ | GENERIC_WRITE) else {
            return false;
        };
        if identity
            .verify(&WindowsMetadata, &handle, &cancelled)
            .is_err()
        {
            return false;
        }
        self.lease = Some(MatrixLease {
            operation: None,
            handle,
            identity,
        });
        true
    }

    fn is_open(&self) -> bool {
        self.lease.is_some()
    }

    fn describes(&self) -> bool {
        self.lease.as_ref().is_some_and(|lease| {
            WindowsMetadata.describe(&lease.handle).ok() == Some(lease.identity.descriptor())
        })
    }

    fn prepare(&mut self, report: &MatrixReport) -> bool {
        self.prepared = Report::new(*report).ok();
        self.prepared.is_some()
    }

    fn issue(&mut self) -> bool {
        let (Some(lease), Some(mut operation)) = (self.lease.as_mut(), self.prepared.take()) else {
            return false;
        };
        if operation.issue(lease.handle.as_raw_handle(), true).is_err() {
            return false;
        }
        lease.operation = Some(operation);
        true
    }

    fn poll(&mut self) -> Option<bool> {
        let lease = self.lease.as_mut()?;
        Operation::complete(lease.handle.as_raw_handle(), &mut lease.operation)
            .map(|result| result.is_ok())
    }

    fn outstanding(&self) -> bool {
        self.lease
            .as_ref()
            .is_some_and(|lease| lease.operation.is_some())
    }

    fn cancel(&mut self) {
        if let Some(lease) = self.lease.as_mut() {
            Operation::cancel(lease.handle.as_raw_handle(), &mut lease.operation);
        }
    }

    fn close(&mut self) {
        if let Some(mut lease) = self.lease.take()
            && lease.handle.retire().is_err()
        {
            std::mem::forget(lease);
        }
    }
}

impl Drop for WindowsMatrix {
    fn drop(&mut self) {
        // A report still outstanding keeps its storage and its handle.
        if self.outstanding() {
            std::mem::forget(self.lease.take());
        }
    }
}

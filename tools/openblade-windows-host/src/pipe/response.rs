//! Write completion for a connected, IOCP-registered Windows named pipe.
use std::{
    cell::UnsafeCell,
    fmt,
    future::Future,
    io,
    os::windows::io::{AsHandle, AsRawHandle, FromRawHandle, OwnedHandle, RawHandle},
    pin::Pin,
    ptr::{null, null_mut},
    task::{Context, Poll},
    time::Duration,
};

use tokio::{
    io::{AsyncRead, AsyncWrite, ReadBuf},
    net::windows::named_pipe::NamedPipeServer,
    time::{Instant, Sleep, sleep},
};
use windows_sys::Win32::{
    Foundation::{ERROR_IO_INCOMPLETE, ERROR_IO_PENDING, WAIT_OBJECT_0},
    Storage::FileSystem::WriteFile,
    System::{
        IO::{CancelIoEx, GetOverlappedResult, OVERLAPPED},
        Threading::{CreateEventW, INFINITE, WaitForSingleObject},
    },
};

// Battery.v1 has the largest admitted body. The length prefix is part of the write.
pub const MAX_WRITE_BYTES: usize = 256 * 1024 + 4;
const POLL_INTERVAL: Duration = Duration::from_millis(10);

pub struct Connection {
    // Drop (including cancellation and confirmed retirement) precedes dropping the listener.
    write: Option<WriteOperation>,
    pipe: NamedPipeServer,
    failed: bool,
}

impl fmt::Debug for Connection {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("Connection")
            .field("write_pending", &self.write.is_some())
            .field("failed", &self.failed)
            .finish_non_exhaustive()
    }
}

impl Connection {
    pub(super) fn new(pipe: NamedPipeServer) -> Self {
        Self {
            write: None,
            pipe,
            failed: false,
        }
    }

    pub async fn connect(&self) -> io::Result<()> {
        self.pipe.connect().await
    }

    fn poll_completion(
        &mut self,
        cx: &mut Context<'_>,
        buffer: Option<&[u8]>,
    ) -> Poll<io::Result<usize>> {
        let Some(write) = &mut self.write else {
            return Poll::Ready(Ok(0));
        };
        let result = std::task::ready!(write.poll(cx));
        // A canceled caller must retire the connection. Never count the old
        // operation as a successful write of a different caller's bytes.
        let result = result.and_then(|count| {
            if count != write.bytes.len() {
                Err(io::Error::new(
                    io::ErrorKind::WriteZero,
                    "Incomplete named pipe response.",
                ))
            } else if buffer.is_some_and(|bytes| bytes != write.bytes.as_ref()) {
                Err(io::Error::new(
                    io::ErrorKind::InvalidData,
                    "A different write was pending.",
                ))
            } else {
                Ok(count)
            }
        });
        self.write = None;
        self.failed = result.is_err();
        Poll::Ready(result)
    }

    fn check_write(&self) -> io::Result<()> {
        if self.failed {
            Err(io::Error::new(
                io::ErrorKind::BrokenPipe,
                "The named pipe write failed.",
            ))
        } else {
            Ok(())
        }
    }
}

impl AsRawHandle for Connection {
    fn as_raw_handle(&self) -> RawHandle {
        self.pipe.as_raw_handle()
    }
}

impl AsyncRead for Connection {
    fn poll_read(
        mut self: Pin<&mut Self>,
        cx: &mut Context<'_>,
        buf: &mut ReadBuf<'_>,
    ) -> Poll<io::Result<()>> {
        Pin::new(&mut self.pipe).poll_read(cx, buf)
    }
}

impl AsyncWrite for Connection {
    fn poll_write(
        mut self: Pin<&mut Self>,
        cx: &mut Context<'_>,
        buf: &[u8],
    ) -> Poll<io::Result<usize>> {
        self.check_write()?;
        if self.write.is_none() {
            if buf.is_empty() {
                return Poll::Ready(Ok(0));
            }
            if buf.len() > MAX_WRITE_BYTES {
                self.failed = true;
                return Poll::Ready(Err(io::Error::new(
                    io::ErrorKind::InvalidInput,
                    "Named pipe response exceeds the write limit.",
                )));
            }
            match WriteOperation::start(&self.pipe, buf) {
                Ok(write) => self.write = Some(write),
                Err(error) => {
                    self.failed = true;
                    return Poll::Ready(Err(error));
                }
            }
        }
        self.poll_completion(cx, Some(buf))
    }

    fn poll_flush(mut self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<io::Result<()>> {
        self.check_write()?;
        self.poll_completion(cx, None).map_ok(|_| ())
    }

    fn poll_shutdown(self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<io::Result<()>> {
        self.poll_flush(cx)
    }
}

struct WriteOperation {
    pipe: OwnedHandle,
    event: OwnedHandle,
    overlapped: Box<UnsafeCell<OVERLAPPED>>,
    bytes: Box<[u8]>,
    timer: Option<Pin<Box<Sleep>>>,
    outstanding: bool,
}

// SAFETY: moving this owner never moves either allocation borrowed by Windows.
// Rust polls or cancels only through exclusive access. Both handles travel with
// the owner, and Drop retains all storage until terminal completion.
unsafe impl Send for WriteOperation {}

impl WriteOperation {
    fn start(server: &NamedPipeServer, bytes: &[u8]) -> io::Result<Self> {
        let pipe = server.as_handle().try_clone_to_owned()?;
        // SAFETY: no security attributes or name; a fresh manual-reset, unsignaled event.
        let raw_event = unsafe { CreateEventW(null(), 1, 0, null()) };
        if raw_event.is_null() {
            return Err(io::Error::last_os_error());
        }
        // SAFETY: successful CreateEventW transfers sole ownership of this event.
        let event = unsafe { OwnedHandle::from_raw_handle(raw_event) };
        // Suppress this operation's completion packet on the pipe's existing
        // completion port. Mio must never receive our foreign OVERLAPPED pointer.
        let overlapped = OVERLAPPED {
            hEvent: raw_event.map_addr(|address| address | 1),
            ..Default::default()
        };
        let mut write = Self {
            pipe,
            event,
            overlapped: Box::new(UnsafeCell::new(overlapped)),
            bytes: bytes.into(),
            timer: None,
            outstanding: false,
        };
        // SAFETY: the duplicate names this pipe, the byte count was bounded before
        // allocation, and both stable buffers survive through result collection.
        let submitted = unsafe {
            WriteFile(
                write.pipe.as_raw_handle(),
                write.bytes.as_ptr(),
                write.bytes.len() as u32,
                null_mut(),
                write.overlapped.get(),
            )
        };
        if submitted == 0 {
            let error = io::Error::last_os_error();
            if error.raw_os_error() != Some(ERROR_IO_PENDING as i32) {
                return Err(error);
            }
        }
        write.outstanding = true;
        Ok(write)
    }

    fn result(&mut self) -> Option<io::Result<usize>> {
        let mut count = 0;
        // SAFETY: use the submitting handle and exact OVERLAPPED allocation.
        // No reference into the kernel-owned OVERLAPPED fields is created.
        let ok = unsafe {
            GetOverlappedResult(
                self.pipe.as_raw_handle(),
                self.overlapped.get(),
                &mut count,
                0,
            )
        };
        let result = if ok != 0 {
            Ok(count as usize)
        } else {
            let error = io::Error::last_os_error();
            if matches!(error.raw_os_error(), Some(code) if code == ERROR_IO_INCOMPLETE as i32 || code == ERROR_IO_PENDING as i32)
            {
                return None;
            }
            // A result-query failure alone must not free kernel-owned storage.
            // The private event also establishes completion for error results.
            if unsafe { WaitForSingleObject(self.event.as_raw_handle(), 0) } != WAIT_OBJECT_0 {
                return None;
            }
            Err(error)
        };
        self.outstanding = false;
        Some(result)
    }

    fn poll(&mut self, cx: &mut Context<'_>) -> Poll<io::Result<usize>> {
        if let Some(result) = self.result() {
            return Poll::Ready(result);
        }
        let timer = self
            .timer
            .get_or_insert_with(|| Box::pin(sleep(POLL_INTERVAL)));
        if timer.as_mut().poll(cx).is_ready() {
            timer.as_mut().reset(Instant::now() + POLL_INTERVAL);
            cx.waker().wake_by_ref();
        }
        Poll::Pending
    }
}

impl Drop for WriteOperation {
    fn drop(&mut self) {
        if !self.outstanding {
            return;
        }
        // SAFETY: targeted cancellation on the submitting handle. Success and
        // ERROR_NOT_FOUND can both race with completion; neither returns storage.
        unsafe { CancelIoEx(self.pipe.as_raw_handle(), self.overlapped.get()) };
        // The request deadline can bound cancellation initiation only. The owning
        // connection, frame and OVERLAPPED remain live if Windows delays retirement.
        while self.result().is_none() {
            // SAFETY: wait on the original, untagged manual-reset event.
            let status = unsafe { WaitForSingleObject(self.event.as_raw_handle(), INFINITE) };
            let _ = status;
            // Bound retry CPU even if a signaled event and result query disagree.
            std::thread::sleep(POLL_INTERVAL);
        }
    }
}

//! Win32 handle setup. All blocking launch calls run on a blocking executor.
#![allow(unsafe_code)]

use std::{
    ffi::OsStr,
    fs::{self, File},
    io,
    os::windows::{
        ffi::OsStrExt,
        fs::OpenOptionsExt,
        io::{AsRawHandle, FromRawHandle, IntoRawHandle, OwnedHandle},
    },
    path::Path,
    ptr::{null, null_mut},
};

use tokio::{io::join, net::windows::named_pipe::NamedPipeServer};
use windows_sys::{
    Win32::{
        Foundation::{
            ERROR_INSUFFICIENT_BUFFER, GENERIC_READ, GENERIC_WRITE, HANDLE, HANDLE_FLAG_INHERIT,
            INVALID_HANDLE_VALUE, SetHandleInformation, WAIT_FAILED, WAIT_OBJECT_0, WAIT_TIMEOUT,
        },
        Security::SECURITY_ATTRIBUTES,
        Storage::FileSystem::{
            CreateFileW, FILE_FLAG_FIRST_PIPE_INSTANCE, FILE_FLAG_OVERLAPPED, FILE_SHARE_READ,
            OPEN_EXISTING, PIPE_ACCESS_INBOUND, PIPE_ACCESS_OUTBOUND, SECURITY_IDENTIFICATION,
            SECURITY_SQOS_PRESENT,
        },
        System::{
            Com::CoCreateGuid,
            JobObjects::{
                CreateJobObjectW, JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
                JOBOBJECT_EXTENDED_LIMIT_INFORMATION, JobObjectExtendedLimitInformation,
                SetInformationJobObject,
            },
            Pipes::{CreateNamedPipeW, GetNamedPipeClientProcessId, PIPE_REJECT_REMOTE_CLIENTS},
            Threading::{
                CREATE_NO_WINDOW, CREATE_UNICODE_ENVIRONMENT, CreateProcessW,
                DeleteProcThreadAttributeList, EXTENDED_STARTUPINFO_PRESENT, GetCurrentProcessId,
                InitializeProcThreadAttributeList, LPPROC_THREAD_ATTRIBUTE_LIST,
                PROC_THREAD_ATTRIBUTE_HANDLE_LIST, PROC_THREAD_ATTRIBUTE_JOB_LIST,
                PROCESS_INFORMATION, STARTF_USESTDHANDLES, STARTUPINFOEXW,
                UpdateProcThreadAttribute, WaitForSingleObject,
            },
        },
    },
    core::GUID,
};

use super::{FIXTURE_NAME, Fault, Pipes, ProcessOwner, Stage};

const PIPE_BUFFER: u32 = 4096;

pub(super) struct Process {
    job: Option<OwnedHandle>,
    process: OwnedHandle,
}

impl ProcessOwner for Process {
    fn close_job(&mut self) {
        drop(self.job.take());
    }

    fn exited(&self) -> Result<bool, Fault> {
        // SAFETY: the retained process handle is live for the whole zero-time wait.
        match unsafe { WaitForSingleObject(self.process.as_raw_handle(), 0) } {
            WAIT_OBJECT_0 => Ok(true),
            WAIT_TIMEOUT => Ok(false),
            WAIT_FAILED => Err(last(Stage::ObserveExit)),
            _ => Err(Fault::new(
                Stage::ObserveExit,
                io::ErrorKind::InvalidData.into(),
            )),
        }
    }
}

pub(super) struct ParentPipes {
    stdout: OwnedHandle,
    stdin: OwnedHandle,
}

impl ParentPipes {
    pub(super) fn register(self) -> Result<Pipes, Fault> {
        // SAFETY: these are distinct, connected, overlapped server ends. Tokio
        // takes ownership immediately, including on registration failure.
        let stdout = unsafe { NamedPipeServer::from_raw_handle(self.stdout.into_raw_handle()) }
            .map_err(|err| Fault::new(Stage::RegisterPipe, err))?;
        let stdin = unsafe { NamedPipeServer::from_raw_handle(self.stdin.into_raw_handle()) }
            .map_err(|err| Fault::new(Stage::RegisterPipe, err))?;
        Ok(join(stdout, stdin))
    }
}

pub(super) fn launch(directory: &Path) -> Result<(Process, ParentPipes), Fault> {
    let image_error = |err| Fault::new(Stage::Image, err);
    let directory = fs::canonicalize(directory).map_err(image_error)?;
    let image_path = directory.join(FIXTURE_NAME);
    let image = fs::canonicalize(&image_path).map_err(image_error)?;
    if image != image_path {
        return Err(image_error(io::ErrorKind::InvalidInput.into()));
    }
    // Pin the fixture file against ordinary write/delete sharing during launch.
    let pinned_image = File::options()
        .read(true)
        .share_mode(FILE_SHARE_READ)
        .open(&image)
        .map_err(image_error)?;
    if !pinned_image.metadata().map_err(image_error)?.is_file() {
        return Err(image_error(io::ErrorKind::InvalidInput.into()));
    }
    let application = wide(image.as_os_str())?;
    let cwd = wide(directory.as_os_str())?;
    // CreateProcessW is given an explicit application path, so this string only
    // supplies a fixed argv[0]. There are no other arguments.
    let mut command = wide(OsStr::new(FIXTURE_NAME))?;
    let empty_environment = [0_u16; 2];

    let job = kill_job()?;
    let (stdin, child_in) = pipe(PIPE_ACCESS_OUTBOUND, GENERIC_READ)?;
    let (stdout, child_out) = pipe(PIPE_ACCESS_INBOUND, GENERIC_WRITE)?;
    let stderr = File::options()
        .write(true)
        .open("NUL")
        .map_err(|err| Fault::new(Stage::Stderr, err))?;
    inherit(stderr.as_raw_handle(), Stage::Stderr)?;

    let handles = [
        child_in.as_raw_handle(),
        child_out.as_raw_handle(),
        stderr.as_raw_handle(),
    ];
    let jobs = [job.as_raw_handle()];
    let mut attributes = Attributes::new()?;
    attributes.set(
        PROC_THREAD_ATTRIBUTE_HANDLE_LIST,
        &handles,
        Stage::HandleList,
    )?;
    attributes.set(PROC_THREAD_ATTRIBUTE_JOB_LIST, &jobs, Stage::JobList)?;

    let mut info = STARTUPINFOEXW::default();
    info.StartupInfo.cb = size_of::<STARTUPINFOEXW>() as u32;
    info.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
    info.StartupInfo.hStdInput = handles[0];
    info.StartupInfo.hStdOutput = handles[1];
    info.StartupInfo.hStdError = handles[2];
    info.lpAttributeList = attributes.raw();
    let mut created = PROCESS_INFORMATION::default();

    // SAFETY: both attribute arrays and all referenced handles outlive the
    // initialized list. All strings and the environment are terminated. The
    // command buffer and result are writable.
    check(
        unsafe {
            CreateProcessW(
                application.as_ptr(),
                command.as_mut_ptr(),
                null(),
                null(),
                1,
                EXTENDED_STARTUPINFO_PRESENT | CREATE_NO_WINDOW | CREATE_UNICODE_ENVIRONMENT,
                empty_environment.as_ptr().cast(),
                cwd.as_ptr(),
                &info.StartupInfo,
                &mut created,
            )
        },
        Stage::CreateProcessWithJob,
    )?;

    // SAFETY: success returns unique, owned process and primary-thread handles.
    let process = unsafe { OwnedHandle::from_raw_handle(created.hProcess) };
    drop(unsafe { OwnedHandle::from_raw_handle(created.hThread) });
    Ok((
        Process {
            job: Some(job),
            process,
        },
        ParentPipes { stdout, stdin },
    ))
}

fn kill_job() -> Result<OwnedHandle, Fault> {
    // SAFETY: null attributes create a non-inheritable, unnamed handle.
    let job = owned(
        unsafe { CreateJobObjectW(null(), null()) },
        Stage::CreateJob,
    )?;
    let mut limits = JOBOBJECT_EXTENDED_LIMIT_INFORMATION::default();
    limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
    // SAFETY: a live job handle and a correctly sized extended limit structure.
    check(
        unsafe {
            SetInformationJobObject(
                job.as_raw_handle(),
                JobObjectExtendedLimitInformation,
                (&raw const limits).cast(),
                size_of_val(&limits) as u32,
            )
        },
        Stage::ConfigureJob,
    )?;
    Ok(job)
}

fn pipe(server_access: u32, client_access: u32) -> Result<(OwnedHandle, OwnedHandle), Fault> {
    let mut nonce = GUID::default();
    // SAFETY: writable GUID storage.
    let result = unsafe { CoCreateGuid(&mut nonce) };
    if result < 0 {
        return Err(Fault::new(
            Stage::Pipe,
            io::Error::from_raw_os_error(result),
        ));
    }
    let name = format!(
        r"\\.\pipe\OpenBlade-CpuFixture-{:08x}{:04x}{:04x}{:016x}",
        nonce.data1,
        nonce.data2,
        nonce.data3,
        u64::from_be_bytes(nonce.data4),
    );
    let name: Vec<u16> = name.encode_utf16().chain([0]).collect();
    // SAFETY: the name is terminated, with default security and one local instance.
    let server = owned(
        unsafe {
            CreateNamedPipeW(
                name.as_ptr(),
                server_access | FILE_FLAG_FIRST_PIPE_INSTANCE | FILE_FLAG_OVERLAPPED,
                PIPE_REJECT_REMOTE_CLIENTS,
                1,
                PIPE_BUFFER,
                PIPE_BUFFER,
                0,
                null(),
            )
        },
        Stage::Pipe,
    )?;
    let security = SECURITY_ATTRIBUTES {
        nLength: size_of::<SECURITY_ATTRIBUTES>() as u32,
        lpSecurityDescriptor: null_mut(),
        bInheritHandle: 0,
    };
    // SAFETY: the terminated name identifies the server held above. The client
    // end uses synchronous byte-mode I/O for the fixture's standard streams.
    let client = owned(
        unsafe {
            CreateFileW(
                name.as_ptr(),
                client_access,
                0,
                &security,
                OPEN_EXISTING,
                SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION,
                null_mut(),
            )
        },
        Stage::Pipe,
    )?;
    let mut peer = 0;
    // SAFETY: the server is connected and peer has space for a process ID.
    check(
        unsafe { GetNamedPipeClientProcessId(server.as_raw_handle(), &mut peer) },
        Stage::Pipe,
    )?;
    if peer != unsafe { GetCurrentProcessId() } {
        return Err(Fault::new(
            Stage::Pipe,
            io::ErrorKind::PermissionDenied.into(),
        ));
    }
    inherit(client.as_raw_handle(), Stage::Pipe)?;
    Ok((server, client))
}

struct Attributes(Vec<usize>);

impl Attributes {
    fn new() -> Result<Self, Fault> {
        let mut size = 0;
        // SAFETY: the null-list call queries the required storage.
        let result = unsafe { InitializeProcThreadAttributeList(null_mut(), 2, 0, &mut size) };
        let error = last(Stage::HandleList);
        if result != 0 || error.win32 != Some(ERROR_INSUFFICIENT_BUFFER as i32) || size == 0 {
            return Err(error);
        }
        let mut buffer = vec![0_usize; size.div_ceil(size_of::<usize>())];
        // SAFETY: pointer-aligned writable storage of at least the returned size.
        check(
            unsafe {
                InitializeProcThreadAttributeList(buffer.as_mut_ptr().cast(), 2, 0, &mut size)
            },
            Stage::HandleList,
        )?;
        Ok(Self(buffer))
    }

    fn raw(&mut self) -> LPPROC_THREAD_ATTRIBUTE_LIST {
        self.0.as_mut_ptr().cast()
    }

    fn set(&mut self, key: u32, handles: &[HANDLE], stage: Stage) -> Result<(), Fault> {
        // SAFETY: the caller keeps the backing arrays and handles alive until
        // this attribute list is deleted.
        check(
            unsafe {
                UpdateProcThreadAttribute(
                    self.raw(),
                    0,
                    key as usize,
                    handles.as_ptr().cast(),
                    size_of_val(handles),
                    null_mut(),
                    null(),
                )
            },
            stage,
        )
    }
}

impl Drop for Attributes {
    fn drop(&mut self) {
        // SAFETY: constructed only after a successful initialization.
        unsafe { DeleteProcThreadAttributeList(self.raw()) };
    }
}

fn wide(value: &OsStr) -> Result<Vec<u16>, Fault> {
    let mut units: Vec<u16> = value.encode_wide().collect();
    if units.contains(&0) {
        return Err(Fault::new(Stage::Image, io::ErrorKind::InvalidInput.into()));
    }
    units.push(0);
    Ok(units)
}

fn inherit(handle: HANDLE, stage: Stage) -> Result<(), Fault> {
    // SAFETY: the caller holds a live owned handle.
    check(
        unsafe { SetHandleInformation(handle, HANDLE_FLAG_INHERIT, HANDLE_FLAG_INHERIT) },
        stage,
    )
}

fn owned(handle: HANDLE, stage: Stage) -> Result<OwnedHandle, Fault> {
    if handle.is_null() || handle == INVALID_HANDLE_VALUE {
        return Err(last(stage));
    }
    // SAFETY: used only for new owned handles returned by successful Win32 calls.
    Ok(unsafe { OwnedHandle::from_raw_handle(handle) })
}

fn check(result: i32, stage: Stage) -> Result<(), Fault> {
    if result == 0 {
        Err(last(stage))
    } else {
        Ok(())
    }
}

fn last(stage: Stage) -> Fault {
    Fault::new(stage, io::Error::last_os_error())
}

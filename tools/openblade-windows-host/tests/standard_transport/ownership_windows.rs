//! Win32 resources for the offline ownership fixture.

use serde::{Deserialize, Serialize};
use std::{
    env,
    ffi::{OsStr, OsString},
    fs::{File, OpenOptions},
    io,
    mem::{MaybeUninit, size_of, size_of_val},
    os::windows::{
        ffi::OsStrExt,
        fs::OpenOptionsExt,
        io::{AsRawHandle, FromRawHandle, OwnedHandle},
    },
    path::Path,
    ptr,
    time::Duration,
};
use windows_sys::Win32::{
    Foundation::{
        DUPLICATE_SAME_ACCESS, DuplicateHandle, HANDLE, HANDLE_FLAG_INHERIT, SetHandleInformation,
        WAIT_OBJECT_0, WAIT_TIMEOUT,
    },
    Security::Cryptography::{BCRYPT_USE_SYSTEM_PREFERRED_RNG, BCryptGenRandom},
    Storage::FileSystem::{FILE_ID_INFO, FileIdInfo, GetFileInformationByHandleEx, SYNCHRONIZE},
    System::{
        JobObjects::{
            CreateJobObjectW, JOB_OBJECT_LIMIT_ACTIVE_PROCESS, JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
            JOBOBJECT_EXTENDED_LIMIT_INFORMATION, JobObjectExtendedLimitInformation,
            SetInformationJobObject,
        },
        Pipes::CreatePipe,
        Threading::{
            CREATE_NO_WINDOW, CREATE_UNICODE_ENVIRONMENT, CreateProcessW,
            DeleteProcThreadAttributeList, EXTENDED_STARTUPINFO_PRESENT, GetCurrentProcess,
            GetExitCodeProcess, InitializeProcThreadAttributeList, LPPROC_THREAD_ATTRIBUTE_LIST,
            PROC_THREAD_ATTRIBUTE_HANDLE_LIST, PROC_THREAD_ATTRIBUTE_JOB_LIST, PROCESS_INFORMATION,
            STARTF_USESTDHANDLES, STARTUPINFOEXW, TerminateProcess, UpdateProcThreadAttribute,
            WaitForSingleObject,
        },
    },
};

const REPLY: &str = "OPENBLADE_HANDOFF_TEST_REPLY";
const WATCH: &str = "OPENBLADE_HANDOFF_TEST_WATCH";

pub(super) fn attempt() -> io::Result<[u8; 16]> {
    let mut bytes = [0; 16];
    // SAFETY: the buffer is writable. The system RNG flag requires a null algorithm handle.
    let status = unsafe {
        BCryptGenRandom(
            ptr::null_mut(),
            bytes.as_mut_ptr(),
            bytes.len() as u32,
            BCRYPT_USE_SYSTEM_PREFERRED_RNG,
        )
    };
    if status != 0 {
        return Err(io::Error::other(format!("BCryptGenRandom: {status:#x}")));
    }
    Ok(bytes)
}

pub(super) fn exclusive(path: impl AsRef<Path>, create: bool) -> io::Result<File> {
    OpenOptions::new()
        .read(true)
        .write(true)
        .create_new(create)
        .share_mode(0)
        .open(path)
}

#[derive(Clone, Copy, Debug, Deserialize, Eq, PartialEq, Serialize)]
pub(super) struct FileIdentity {
    volume: u64,
    id: [u8; 16],
}

pub(super) fn identity(file: &File) -> io::Result<FileIdentity> {
    let mut info = MaybeUninit::<FILE_ID_INFO>::uninit();
    // SAFETY: the file keeps the handle alive and info has the required size.
    let info = unsafe {
        checked(GetFileInformationByHandleEx(
            file.as_raw_handle(),
            FileIdInfo,
            info.as_mut_ptr().cast(),
            size_of::<FILE_ID_INFO>() as u32,
        ))?;
        info.assume_init()
    };
    Ok(FileIdentity {
        volume: info.VolumeSerialNumber,
        id: info.FileId.Identifier,
    })
}

fn checked(result: i32) -> io::Result<()> {
    if result == 0 {
        Err(io::Error::last_os_error())
    } else {
        Ok(())
    }
}

pub(super) struct WorkerJob(OwnedHandle);

impl WorkerJob {
    pub(super) fn new() -> io::Result<Self> {
        // SAFETY: null security attributes give us a non-inheritable handle.
        // The job is unnamed and the new handle is owned exactly once.
        let job = unsafe {
            let handle = CreateJobObjectW(ptr::null(), ptr::null());
            if handle.is_null() {
                return Err(io::Error::last_os_error());
            }
            OwnedHandle::from_raw_handle(handle)
        };
        let mut limits = JOBOBJECT_EXTENDED_LIMIT_INFORMATION::default();
        limits.BasicLimitInformation.LimitFlags =
            JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | JOB_OBJECT_LIMIT_ACTIVE_PROCESS;
        limits.BasicLimitInformation.ActiveProcessLimit = 1;
        // SAFETY: both the handle and sized limit structure live through the call.
        unsafe {
            checked(SetInformationJobObject(
                job.as_raw_handle(),
                JobObjectExtendedLimitInformation,
                ptr::from_ref(&limits).cast(),
                size_of_val(&limits) as u32,
            ))?;
        }
        Ok(Self(job))
    }
}

fn pipe() -> io::Result<(File, File)> {
    let mut read = ptr::null_mut();
    let mut write = ptr::null_mut();
    // SAFETY: the output pointers are valid and the successful call transfers
    // one non-inheritable handle for each end.
    unsafe {
        checked(CreatePipe(&mut read, &mut write, ptr::null(), 0))?;
        Ok((
            OwnedHandle::from_raw_handle(read).into(),
            OwnedHandle::from_raw_handle(write).into(),
        ))
    }
}

fn duplicate_in_process(
    source: HANDLE,
    access: u32,
    inherit: bool,
    options: u32,
) -> io::Result<OwnedHandle> {
    let mut duplicate = ptr::null_mut();
    // SAFETY: the kernel validates the source and owns no reference to the
    // output pointer after the call. Success returns a new handle to us.
    unsafe {
        let process = GetCurrentProcess();
        checked(DuplicateHandle(
            process,
            source,
            process,
            &mut duplicate,
            access,
            i32::from(inherit),
            options,
        ))?;
        Ok(OwnedHandle::from_raw_handle(duplicate))
    }
}

struct Attributes(Vec<usize>);

impl Attributes {
    fn new(count: u32) -> io::Result<Self> {
        let mut bytes = 0;
        // SAFETY: the null-list sizing call only writes the size. The next call
        // receives aligned writable storage with at least the requested size.
        unsafe {
            InitializeProcThreadAttributeList(ptr::null_mut(), count, 0, &mut bytes);
            if bytes == 0 {
                return Err(io::Error::last_os_error());
            }
            let mut buffer = vec![0usize; bytes.div_ceil(size_of::<usize>())];
            checked(InitializeProcThreadAttributeList(
                buffer.as_mut_ptr().cast(),
                count,
                0,
                &mut bytes,
            ))?;
            Ok(Self(buffer))
        }
    }

    fn raw(&mut self) -> LPPROC_THREAD_ATTRIBUTE_LIST {
        self.0.as_mut_ptr().cast()
    }
}

impl Drop for Attributes {
    fn drop(&mut self) {
        // SAFETY: this owns the initialized list and its backing storage.
        unsafe { DeleteProcThreadAttributeList(self.raw()) };
    }
}

fn wide(value: &OsStr) -> Vec<u16> {
    value.encode_wide().chain([0]).collect()
}

pub(super) struct ChildProcess(OwnedHandle);

impl ChildProcess {
    pub(super) fn launch(root: &Path, job: Option<&WorkerJob>) -> io::Result<(Self, File, File)> {
        let (read_request, write_request) = pipe()?;
        let (read_reply, write_reply) = pipe()?;
        let null = OpenOptions::new().write(true).open("NUL")?;
        let watch = if job.is_some() {
            // SAFETY: duplicate the actual launching process with only wait access.
            Some(duplicate_in_process(
                unsafe { GetCurrentProcess() },
                SYNCHRONIZE,
                true,
                0,
            )?)
        } else {
            None
        };
        let mut inherited = vec![
            read_request.as_raw_handle(),
            write_reply.as_raw_handle(),
            null.as_raw_handle(),
        ];
        for &handle in &inherited {
            // SAFETY: these newly opened handles belong to this launch only.
            unsafe {
                checked(SetHandleInformation(
                    handle,
                    HANDLE_FLAG_INHERIT,
                    HANDLE_FLAG_INHERIT,
                ))?
            };
        }
        if let Some(watch) = &watch {
            inherited.push(watch.as_raw_handle());
        }
        let job_list = job.map(|job| [job.0.as_raw_handle()]);
        let mut attributes = Attributes::new(1 + u32::from(job.is_some()))?;

        let exe = env::current_exe()?;
        let mut command = OsString::from("\"");
        command.push(&exe);
        command.push("\" --exact ownership::child_role --test-threads=1");
        let mut command = wide(&command);
        let app = wide(exe.as_os_str());
        let directory = wide(root.as_os_str());
        let mut environment = wide(OsStr::new(&format!(
            "{REPLY}={}",
            write_reply.as_raw_handle() as usize
        )));
        if let Some(watch) = &watch {
            environment.extend(wide(OsStr::new(&format!(
                "{WATCH}={}",
                watch.as_raw_handle() as usize
            ))));
        }
        let mut system_root = OsString::from("SystemRoot=");
        system_root
            .push(env::var_os("SystemRoot").ok_or_else(|| io::Error::other("missing SystemRoot"))?);
        environment.extend(wide(&system_root));
        environment.push(0);

        let mut startup = STARTUPINFOEXW::default();
        startup.StartupInfo.cb = size_of::<STARTUPINFOEXW>() as u32;
        startup.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
        startup.StartupInfo.hStdInput = read_request.as_raw_handle();
        startup.StartupInfo.hStdOutput = null.as_raw_handle();
        startup.StartupInfo.hStdError = null.as_raw_handle();
        startup.lpAttributeList = attributes.raw();
        let mut info = PROCESS_INFORMATION::default();
        // SAFETY: attribute values and handles remain live until list deletion.
        // CreateProcessW gets terminated UTF-16 strings and a writable command
        // buffer. Only listed handles transfer, and the job is set at creation.
        unsafe {
            checked(UpdateProcThreadAttribute(
                attributes.raw(),
                0,
                PROC_THREAD_ATTRIBUTE_HANDLE_LIST as usize,
                inherited.as_ptr().cast(),
                size_of_val(inherited.as_slice()),
                ptr::null_mut(),
                ptr::null_mut(),
            ))?;
            if let Some(job_list) = &job_list {
                checked(UpdateProcThreadAttribute(
                    attributes.raw(),
                    0,
                    PROC_THREAD_ATTRIBUTE_JOB_LIST as usize,
                    job_list.as_ptr().cast(),
                    size_of_val(job_list),
                    ptr::null_mut(),
                    ptr::null_mut(),
                ))?;
            }
            checked(CreateProcessW(
                app.as_ptr(),
                command.as_mut_ptr(),
                ptr::null(),
                ptr::null(),
                1,
                CREATE_NO_WINDOW | CREATE_UNICODE_ENVIRONMENT | EXTENDED_STARTUPINFO_PRESENT,
                environment.as_ptr().cast(),
                directory.as_ptr(),
                &startup.StartupInfo,
                &mut info,
            ))?;
            drop(OwnedHandle::from_raw_handle(info.hThread));
            Ok((
                Self(OwnedHandle::from_raw_handle(info.hProcess)),
                write_request,
                read_reply,
            ))
        }
    }

    pub(super) fn wait(&self, timeout: Duration) -> io::Result<Option<bool>> {
        if !signaled(&self.0, timeout)? {
            return Ok(None);
        }
        let mut code = 0;
        // SAFETY: the owned process handle and the output pointer are valid.
        unsafe { checked(GetExitCodeProcess(self.0.as_raw_handle(), &mut code))? };
        Ok(Some(code == 0))
    }

    pub(super) fn kill(&self) -> io::Result<()> {
        // SAFETY: this handle belongs to the process launched above.
        unsafe { checked(TerminateProcess(self.0.as_raw_handle(), 71)) }
    }
}

fn signaled(handle: &OwnedHandle, timeout: Duration) -> io::Result<bool> {
    let timeout = timeout.as_millis().try_into().map_err(io::Error::other)?;
    // SAFETY: the handle is live throughout the wait.
    match unsafe { WaitForSingleObject(handle.as_raw_handle(), timeout) } {
        WAIT_OBJECT_0 => Ok(true),
        WAIT_TIMEOUT => Ok(false),
        _ => Err(io::Error::last_os_error()),
    }
}

pub(super) struct OwnerWatch(OwnedHandle);

impl OwnerWatch {
    pub(super) fn alive(&self) -> io::Result<bool> {
        Ok(!signaled(&self.0, Duration::ZERO)?)
    }
}

pub(super) fn child_endpoints() -> io::Result<Option<(File, Option<OwnerWatch>)>> {
    fn inherited(name: &str) -> io::Result<Option<OwnedHandle>> {
        let Some(value) = env::var_os(name) else {
            return Ok(None);
        };
        let raw = value
            .to_string_lossy()
            .parse::<usize>()
            .map_err(io::Error::other)?;
        // Treat the inherited handle value as untrusted input. Only the
        // successful kernel-created duplicate is ever turned into OwnedHandle.
        duplicate_in_process(raw as HANDLE, 0, false, DUPLICATE_SAME_ACCESS).map(Some)
    }

    let Some(reply) = inherited(REPLY)? else {
        return Ok(None);
    };
    Ok(Some((reply.into(), inherited(WATCH)?.map(OwnerWatch))))
}

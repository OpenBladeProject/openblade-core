//! The child enters a non-breakaway, kill-on-close job at creation. The
//! primary thread stays suspended until cancellation/deadline admission.
#![allow(unsafe_code)]
use super::{ChildControl, Failure, custody::pin_directories};
use std::{
    ffi::{OsStr, OsString},
    fs::File,
    os::windows::{
        ffi::{OsStrExt, OsStringExt},
        fs::{MetadataExt, OpenOptionsExt},
        io::{AsRawHandle, FromRawHandle, OwnedHandle},
    },
    path::{Path, PathBuf},
    ptr::{null, null_mut},
};
use windows_sys::Win32::{
    Foundation::{ERROR_INSUFFICIENT_BUFFER, GetLastError, WAIT_OBJECT_0, WAIT_TIMEOUT},
    Storage::FileSystem::{
        FILE_ATTRIBUTE_REPARSE_POINT, FILE_FLAG_OPEN_REPARSE_POINT, FILE_SHARE_READ,
    },
    System::{
        JobObjects::{
            CreateJobObjectW, JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
            JOBOBJECT_BASIC_ACCOUNTING_INFORMATION, JOBOBJECT_EXTENDED_LIMIT_INFORMATION,
            JobObjectBasicAccountingInformation, JobObjectExtendedLimitInformation,
            QueryInformationJobObject, SetInformationJobObject, TerminateJobObject,
        },
        SystemInformation::GetSystemDirectoryW,
        Threading::{
            CREATE_NO_WINDOW, CREATE_SUSPENDED, CreateProcessW, DeleteProcThreadAttributeList,
            EXTENDED_STARTUPINFO_PRESENT, GetExitCodeProcess, InitializeProcThreadAttributeList,
            LPPROC_THREAD_ATTRIBUTE_LIST, PROC_THREAD_ATTRIBUTE_JOB_LIST, PROCESS_INFORMATION,
            ResumeThread, STARTUPINFOEXW, UpdateProcThreadAttribute, WaitForSingleObject,
        },
    },
};

pub(super) struct Child {
    job: OwnedHandle,
    process: OwnedHandle,
    thread: OwnedHandle,
}

impl Child {
    pub(super) fn launch(output: &Path) -> Result<Self, Failure> {
        let mut buffer = [0u16; 32768];
        // SAFETY: the output buffer is initialized and sized in WCHARs.
        let n = unsafe { GetSystemDirectoryW(buffer.as_mut_ptr(), buffer.len() as u32) } as usize;
        if n == 0 || n >= buffer.len() {
            return Err(Failure::Start);
        }
        let system = PathBuf::from(OsString::from_wide(&buffer[..n]));
        let _directories = pin_directories(&system).map_err(|_| Failure::Start)?;
        let image = system.join("powercfg.exe");
        let pinned = File::options()
            .read(true)
            .share_mode(FILE_SHARE_READ)
            .custom_flags(FILE_FLAG_OPEN_REPARSE_POINT)
            .open(&image)
            .map_err(|_| Failure::Start)?;
        let info = pinned.metadata().map_err(|_| Failure::Start)?;
        if !info.is_file() || info.file_attributes() & FILE_ATTRIBUTE_REPARSE_POINT != 0 {
            return Err(Failure::Start);
        }

        let image = wide(image.as_os_str());
        let cwd = wide(system.as_os_str());
        let mut command = command_line(output)?;
        Self::create_suspended(&image, &cwd, &mut command)
    }

    // The production caller supplies only its pinned System32 powercfg image
    // and fixed typed arguments. Tests exercise the same creation/retirement
    // primitive with two copies of the platform test executable.
    fn create_suspended(image: &[u16], cwd: &[u16], command: &mut [u16]) -> Result<Self, Failure> {
        let job = create_job()?;
        let jobs = [job.as_raw_handle()];
        let mut attributes = Attributes::new()?;
        // SAFETY: the attribute array and its job handle live through CreateProcessW.
        if unsafe {
            UpdateProcThreadAttribute(
                attributes.raw(),
                0,
                PROC_THREAD_ATTRIBUTE_JOB_LIST as usize,
                jobs.as_ptr().cast(),
                size_of_val(&jobs),
                null_mut(),
                null(),
            )
        } == 0
        {
            return Err(Failure::Start);
        }
        let mut startup = STARTUPINFOEXW::default();
        startup.StartupInfo.cb = size_of::<STARTUPINFOEXW>() as u32;
        startup.lpAttributeList = attributes.raw();
        let mut created = PROCESS_INFORMATION::default();
        // SAFETY: explicit pinned image, trusted working directory, mutable
        // terminated command, live job attribute, no inherited handles/pipes.
        if unsafe {
            CreateProcessW(
                image.as_ptr(),
                command.as_mut_ptr(),
                null(),
                null(),
                0,
                EXTENDED_STARTUPINFO_PRESENT | CREATE_NO_WINDOW | CREATE_SUSPENDED,
                null(),
                cwd.as_ptr(),
                &startup.StartupInfo,
                &mut created,
            )
        } == 0
        {
            return Err(Failure::Start);
        }
        // SAFETY: creation transferred two distinct owned handles. From here
        // even a failed resume is retired and observed through the owned job.
        Ok(Self {
            job,
            process: unsafe { OwnedHandle::from_raw_handle(created.hProcess) },
            thread: unsafe { OwnedHandle::from_raw_handle(created.hThread) },
        })
    }
}

fn wide(value: &OsStr) -> Vec<u16> {
    value.encode_wide().chain([0]).collect()
}

fn command_line(output: &Path) -> Result<Vec<u16>, Failure> {
    if output
        .as_os_str()
        .encode_wide()
        .any(|c| c == 0 || c == u16::from(b'"'))
    {
        return Err(Failure::Start);
    }
    let mut value = OsString::from("powercfg.exe /batteryreport /duration 1 /xml /output \"");
    value.push(output);
    value.push("\"");
    Ok(wide(&value))
}

fn create_job() -> Result<OwnedHandle, Failure> {
    // SAFETY: null attributes/name request a private, non-inheritable job.
    let raw = unsafe { CreateJobObjectW(null(), null()) };
    if raw.is_null() {
        return Err(Failure::Start);
    }
    let job = unsafe { OwnedHandle::from_raw_handle(raw) };
    let mut limits = JOBOBJECT_EXTENDED_LIMIT_INFORMATION::default();
    limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
    // SAFETY: limits has the required size/layout and raw is held by job.
    if unsafe {
        SetInformationJobObject(
            raw,
            JobObjectExtendedLimitInformation,
            (&raw const limits).cast(),
            size_of_val(&limits) as u32,
        )
    } == 0
    {
        return Err(Failure::Start);
    }
    Ok(job)
}

impl ChildControl for Child {
    fn resume(&self) -> Result<(), Failure> {
        // SAFETY: the primary thread is owned and was created suspended.
        if unsafe { ResumeThread(self.thread.as_raw_handle()) } != 1 {
            Err(Failure::Start)
        } else {
            Ok(())
        }
    }

    fn exited(&self) -> Result<Option<u32>, Failure> {
        // SAFETY: retained process handle, zero-time observation.
        match unsafe { WaitForSingleObject(self.process.as_raw_handle(), 0) } {
            WAIT_OBJECT_0 => (),
            WAIT_TIMEOUT => return Ok(None),
            _ => return Err(Failure::Wait),
        }
        let mut accounting = JOBOBJECT_BASIC_ACCOUNTING_INFORMATION::default();
        let mut code = 0;
        // SAFETY: correctly typed/sized outputs and owned process/job handles.
        unsafe {
            if QueryInformationJobObject(
                self.job.as_raw_handle(),
                JobObjectBasicAccountingInformation,
                (&raw mut accounting).cast(),
                size_of_val(&accounting) as u32,
                null_mut(),
            ) == 0
            {
                return Err(Failure::Wait);
            }
            if accounting.ActiveProcesses != 0 {
                return Ok(None);
            }
            if GetExitCodeProcess(self.process.as_raw_handle(), &mut code) == 0 {
                return Err(Failure::Wait);
            }
        }
        Ok(Some(code))
    }

    fn terminate(&self) {
        // SAFETY: own this private job. Termination requests are asynchronous;
        // the caller independently polls both original process and job counts.
        unsafe {
            TerminateJobObject(self.job.as_raw_handle(), 1);
        }
    }
}

struct Attributes(Vec<usize>);
impl Attributes {
    fn new() -> Result<Self, Failure> {
        let mut size = 0;
        // SAFETY: null-list sizing query, followed by aligned, sized storage.
        if unsafe { InitializeProcThreadAttributeList(null_mut(), 1, 0, &mut size) } != 0
            || unsafe { GetLastError() } != ERROR_INSUFFICIENT_BUFFER
            || size == 0
        {
            return Err(Failure::Start);
        }
        let mut words = vec![0usize; size.div_ceil(size_of::<usize>())];
        if unsafe { InitializeProcThreadAttributeList(words.as_mut_ptr().cast(), 1, 0, &mut size) }
            == 0
        {
            return Err(Failure::Start);
        }
        Ok(Self(words))
    }
    fn raw(&mut self) -> LPPROC_THREAD_ATTRIBUTE_LIST {
        self.0.as_mut_ptr().cast()
    }
}
impl Drop for Attributes {
    fn drop(&mut self) {
        // SAFETY: successful initialization is the only constructor.
        unsafe { DeleteProcThreadAttributeList(self.raw()) };
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn fixed_arguments_quote_the_whole_output_and_reject_ambiguous_text() {
        let output = Path::new(r"C:\user space\generated\battery-report.xml");
        let line = command_line(output).unwrap();
        assert_eq!(
            String::from_utf16(&line[..line.len() - 1]).unwrap(),
            r#"powercfg.exe /batteryreport /duration 1 /xml /output "C:\user space\generated\battery-report.xml""#
        );
        assert!(command_line(Path::new("a\"b")).is_err());
        assert!(command_line(Path::new("a\0b")).is_err());
    }

    const FIXTURE_ROOT: &str = "battery_report::process::tests::inert_fixture_parent";
    const FIXTURE_DESCENDANT: &str = "battery_report::process::tests::inert_fixture_descendant";

    #[test]
    #[ignore = "internal child of the job supervision test"]
    #[allow(clippy::zombie_processes)] // The supervision test needs the descendant to outlive this root.
    fn inert_fixture_parent() {
        let _descendant = std::process::Command::new(std::env::current_exe().unwrap())
            .args(["--ignored", "--exact", FIXTURE_DESCENDANT])
            .spawn()
            .unwrap();
    }

    #[test]
    #[ignore = "internal descendant of the job supervision test"]
    fn inert_fixture_descendant() {
        std::thread::sleep(std::time::Duration::from_secs(30));
    }

    #[test]
    fn root_exit_with_live_descendant_requires_job_retirement() {
        use std::time::{Duration, Instant};
        let image = std::env::current_exe().unwrap();
        let cwd = wide(image.parent().unwrap().as_os_str());
        let mut command = wide(OsStr::new(&format!(
            "fixture.exe --ignored --exact {FIXTURE_ROOT}"
        )));
        let child = Child::create_suspended(&wide(image.as_os_str()), &cwd, &mut command).unwrap();
        child.resume().unwrap();
        // The root test harness exits only after successfully starting the
        // descendant. Assert success separately; a failed root proves nothing.
        assert_eq!(
            unsafe { WaitForSingleObject(child.process.as_raw_handle(), 10_000) },
            WAIT_OBJECT_0
        );
        let mut code = 1;
        assert_ne!(
            unsafe { GetExitCodeProcess(child.process.as_raw_handle(), &mut code) },
            0
        );
        assert_eq!(code, 0);
        assert_eq!(child.exited().unwrap(), None);
        child.terminate();
        let deadline = Instant::now() + Duration::from_secs(2);
        loop {
            if let Some(code) = child.exited().unwrap() {
                assert_eq!(code, 0);
                break;
            }
            assert!(
                Instant::now() < deadline,
                "descendant termination unconfirmed"
            );
            super::super::pause(deadline);
        }
    }
}

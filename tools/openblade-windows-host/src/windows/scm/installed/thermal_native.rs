//! Real Windows suppliers, compile-only during offline verification.
#![allow(unsafe_code)]
use super::{mailbox_read::ReadCustody, thermal_storage::Files};
use crate::discovery::performance::ReadAdmission;
use crate::{
    loaded_origin::Origin,
    windows_protected_path::{self as paths, Policy},
};
use std::{
    ffi::OsStr,
    fs::File,
    io::{self, Read, Seek, Write},
    os::windows::io::{FromRawHandle, OwnedHandle},
    path::Path,
    sync::{Arc, Mutex},
};
use windows_sys::{
    Wdk::Storage::FileSystem::{
        FILE_CREATE, FILE_NON_DIRECTORY_FILE, FILE_OPEN, FILE_OPEN_IF, FILE_OPEN_REPARSE_POINT,
    },
    Win32::{
        Storage::FileSystem::{DELETE, FILE_GENERIC_READ, FILE_GENERIC_WRITE, FILE_SHARE_READ},
        System::{
            Power::{GetSystemPowerStatus, SYSTEM_POWER_STATUS},
            RemoteDesktop::WTSQueryUserToken,
        },
    },
};
fn deny() -> io::Error {
    io::Error::new(
        io::ErrorKind::PermissionDenied,
        "fresh installed mutation admission is unavailable",
    )
}

pub(super) struct WindowsGuard {
    origin: Arc<Origin>,
    custody: Arc<dyn ReadCustody>,
    sid: String,
    session: u32,
    authentication: (u32, i32),
    lifecycle: u64,
    controllers:
        Mutex<openblade_platform_windows::controller_presence::admission::ReadOnlyAdmission>,
}
impl WindowsGuard {
    pub(super) fn capture(
        origin: Arc<Origin>,
        custody: Arc<dyn ReadCustody>,
        sid: String,
        session: u32,
    ) -> io::Result<Arc<Self>> {
        origin.lifecycle.verify_health().map_err(|_| {
            super::thermal_mutation::fatal_admission("lifecycle state lock poisoned")
        })?;
        origin.mutation_artifact()?;
        let identity = session_identity(session)?;
        if identity.sid != sid || identity.session != session || !identity.primary {
            return Err(deny());
        }
        origin.lifecycle.verify_health().map_err(|_| {
            super::thermal_mutation::fatal_admission("lifecycle state lock poisoned")
        })?;
        let lifecycle = origin.lifecycle.active().ok_or_else(deny)?;
        let guard=Arc::new(Self{lifecycle,origin,custody,sid,session,authentication:(identity.authentication.LowPart,identity.authentication.HighPart),controllers:Mutex::new(openblade_platform_windows::controller_presence::admission::ReadOnlyAdmission::native())});
        let proof = guard.authorize();
        guard.verify_health()?;
        proof.ok_or_else(deny)?;
        Ok(guard)
    }
    pub(super) fn verify_health(&self) -> io::Result<()> {
        self.custody.verify_integrity()?;
        self.origin.lifecycle.verify_health().map_err(|_| {
            super::thermal_mutation::fatal_admission("lifecycle state lock poisoned")
        })?;
        self.controllers.lock().map(|_| ()).map_err(|_| {
            super::thermal_mutation::fatal_admission("controller admission lock poisoned")
        })
    }
    pub(super) fn authorize(&self) -> Option<ReadAdmission> {
        self.authorize_for(false)
    }
    pub(super) fn authorize_owned_stop(&self) -> Option<ReadAdmission> {
        self.authorize_for(true)
    }
    fn authorize_for(&self, stop: bool) -> Option<ReadAdmission> {
        let verify = || {
            if stop {
                self.origin.verify_owned_stop()
            } else {
                self.origin.verify()
            }
        };
        verify().ok()?;
        if self.origin.lifecycle.active() != Some(self.lifecycle) {
            return None;
        }
        if !if stop {
            self.origin.owned_stop_committed().ok()?
        } else {
            self.origin.committed().ok()?
        } || crate::console_session::observe() != Some(self.session)
        {
            return None;
        }
        if !session_unlocked(self.session) {
            return None;
        }
        let identity = session_identity(self.session).ok()?;
        if !identity.primary
            || identity.session != self.session
            || identity.sid != self.sid
            || (
                identity.authentication.LowPart,
                identity.authentication.HighPart,
            ) != self.authentication
        {
            return None;
        }
        let mut status = SYSTEM_POWER_STATUS::default();
        // SAFETY: writable native output. This excludes an unknown coarse source only;
        // typed 07/8C independently decides sufficient versus limited adapter.
        if unsafe { GetSystemPowerStatus(&mut status) } == 0 || status.ACLineStatus > 1 {
            return None;
        }
        if !self.controllers.lock().ok()?.confirms_fresh_absence() {
            return None;
        }
        // Mutable callback fencing cannot revive an earlier operation. A new
        // request captures a fresh epoch and rechecks every Windows supplier.
        verify().ok()?;
        if self.origin.lifecycle.active() != Some(self.lifecycle) {
            return None;
        }
        if stop {
            self.custody.authorize_owned_stop()
        } else {
            self.custody.authorize()
        }
    }
}
// Exact current-session read; source/compile-only during offline validation.
fn session_unlocked(session: u32) -> bool {
    use windows_sys::Win32::System::RemoteDesktop::{
        WTS_SESSIONSTATE_UNLOCK, WTSActive, WTSFreeMemory, WTSINFOEXW, WTSQuerySessionInformationW,
        WTSSessionInfoEx,
    };
    let mut buffer = std::ptr::null_mut();
    let mut bytes = 0;
    // SAFETY: writable returned buffer/length. Windows owns the allocation.
    if unsafe {
        WTSQuerySessionInformationW(
            std::ptr::null_mut(),
            session,
            WTSSessionInfoEx,
            &mut buffer,
            &mut bytes,
        )
    } == 0
    {
        return false;
    }
    let valid = if !buffer.is_null() && bytes as usize >= std::mem::size_of::<WTSINFOEXW>() {
        // SAFETY: checked complete structure, then its documented level-one union.
        let info = unsafe { buffer.cast::<WTSINFOEXW>().read_unaligned() };
        if info.Level == 1 {
            let level = unsafe { info.Data.WTSInfoExLevel1 };
            level.SessionId == session
                && level.SessionState == WTSActive
                && level.SessionFlags == WTS_SESSIONSTATE_UNLOCK as i32
        } else {
            false
        }
    } else {
        false
    };
    // SAFETY: release only the returned Windows allocation, on every success path.
    if !buffer.is_null() {
        unsafe { WTSFreeMemory(buffer.cast()) };
    }
    valid
}
fn session_identity(session: u32) -> io::Result<crate::windows_token::Identity> {
    if session == 0 {
        return Err(deny());
    }
    let mut token = std::ptr::null_mut();
    // SAFETY: LocalSystem query of the independently authenticated session;
    // successful output is owned until all identity fields have been copied.
    if unsafe { WTSQueryUserToken(session, &mut token) } == 0 {
        return Err(io::Error::last_os_error());
    }
    let token = unsafe { OwnedHandle::from_raw_handle(token) };
    crate::windows_token::Identity::read(&token)
}

// Existing per-user directories can be SYSTEM-owned and inherit the protected
// machine ACL. They must not grant untrusted mutation or child replacement.
const USER_ROOT: Policy = Policy::Directory { extra_rights: 0 };

pub(super) struct ProtectedFiles {
    root: File,
    _ancestors: Vec<File>,
    _writer: File,
    #[cfg(test)]
    verifier: Option<Arc<dyn Fn(&File) -> io::Result<()> + Send + Sync>>,
}
impl ProtectedFiles {
    /// No default root, environment resolution or token query occurs here.
    pub(super) fn open_at(path: &Path) -> io::Result<Self> {
        let (root, ancestors, _) = paths::pin_root(path)?;
        paths::require_ntfs(&root)?;
        paths::verify_security(&root, USER_ROOT)?;
        for (i, parent) in ancestors.iter().enumerate() {
            paths::verify_security(
                parent,
                if i + 3 == ancestors.len() {
                    Policy::PROGRAM_DATA
                } else {
                    Policy::ANCESTOR
                },
            )?;
        }
        let writer = paths::open_relative(
            &root,
            OsStr::new("configuration.writer-lock"),
            FILE_GENERIC_READ | FILE_GENERIC_WRITE,
            0,
            FILE_OPEN_IF,
            FILE_NON_DIRECTORY_FILE | FILE_OPEN_REPARSE_POINT,
        )?;
        paths::check_regular_file(&writer)?;
        paths::verify_security(&writer, Policy::Record)?;
        if writer.metadata()?.len() != 0 {
            return Err(deny());
        }
        Ok(Self {
            root,
            _ancestors: ancestors,
            _writer: writer,
            #[cfg(test)]
            verifier: None,
        })
    }
    fn check(&self, file: &File, policy: Policy) -> io::Result<()> {
        #[cfg(test)]
        if let Some(verify) = &self.verifier {
            return verify(file);
        }
        paths::verify_security(file, policy)
    }
    #[cfg(test)]
    pub(super) fn injected(
        root: File,
        verify: Arc<dyn Fn(&File) -> io::Result<()> + Send + Sync>,
    ) -> io::Result<Self> {
        verify(&root)?;
        let writer = paths::open_relative(
            &root,
            OsStr::new("configuration.writer-lock"),
            FILE_GENERIC_READ | FILE_GENERIC_WRITE,
            0,
            FILE_OPEN_IF,
            FILE_NON_DIRECTORY_FILE | FILE_OPEN_REPARSE_POINT,
        )?;
        paths::check_regular_file(&writer)?;
        verify(&writer)?;
        if writer.metadata()?.len() != 0 {
            return Err(deny());
        }
        Ok(Self {
            root,
            _ancestors: vec![],
            _writer: writer,
            verifier: Some(verify),
        })
    }
    fn file(&self, name: &str, create: bool) -> io::Result<File> {
        if name.is_empty()
            || !name
                .bytes()
                .all(|b| b.is_ascii_alphanumeric() || b".-".contains(&b))
        {
            return Err(deny());
        }
        self.check(&self.root, USER_ROOT)?;
        let file = paths::open_relative(
            &self.root,
            OsStr::new(name),
            FILE_GENERIC_READ
                | if create {
                    FILE_GENERIC_WRITE | DELETE
                } else {
                    0
                },
            if create { 0 } else { FILE_SHARE_READ },
            if create { FILE_CREATE } else { FILE_OPEN },
            FILE_NON_DIRECTORY_FILE | FILE_OPEN_REPARSE_POINT,
        )?;
        paths::check_regular_file(&file)?;
        self.check(&file, Policy::Record)?;
        Ok(file)
    }
}
impl Files for ProtectedFiles {
    fn read(&self, name: &str) -> io::Result<Option<Vec<u8>>> {
        let mut f = match self.file(name, false) {
            Ok(f) => f,
            Err(e) if paths::missing_leaf(&e) => return Ok(None),
            Err(e) => return Err(e),
        };
        if f.metadata()?.len() > 8 * 1024 * 1024 {
            return Err(deny());
        }
        let mut bytes = Vec::new();
        f.read_to_end(&mut bytes)?;
        Ok(Some(bytes))
    }
    fn replace(&self, name: &str, expected: Option<&[u8]>, bytes: &[u8]) -> io::Result<()> {
        if self.read(name)?.as_deref() != expected {
            return Err(deny());
        }
        let mut staged = self.file(&format!("{name}.pending"), true)?;
        staged.write_all(bytes)?;
        staged.sync_all()?;
        staged.rewind()?;
        let mut check = Vec::new();
        staged.read_to_end(&mut check)?;
        if check != bytes {
            return Err(deny());
        }
        if self.read(name)?.as_deref() != expected {
            return Err(deny());
        }
        paths::rename_same_directory(&staged, name, true)?;
        staged.sync_all()?;
        drop(staged);
        if self.read(name)?.as_deref() != Some(bytes) {
            return Err(deny());
        }
        Ok(())
    }
}

// Configuration remains per-user; submission ambiguity is machine-global under
// the SAME durable owner Store. Switching SID cannot bypass another user's fence.
pub(super) struct CustodialFiles {
    pub(super) user: ProtectedFiles,
    pub(super) custody: Arc<dyn ReadCustody>,
}
impl Files for CustodialFiles {
    fn replace_retired(&self, name: &str, expected: Option<&[u8]>, bytes: &[u8]) -> io::Result<()> {
        if name != "thermal-mutation.journal" {
            return Err(deny());
        }
        self.custody.journal_retire_prepared(expected, bytes)
    }
    fn replace_cleanup(&self, name: &str, expected: Option<&[u8]>, bytes: &[u8]) -> io::Result<()> {
        if name != "thermal-mutation.journal" {
            return Err(deny());
        }
        self.custody.journal_replace_cleanup(expected, bytes)
    }
    fn reserve(&self, expected: Option<&[u8]>, needed: usize) -> io::Result<Option<Vec<u8>>> {
        self.custody.journal_reserve(expected, needed)
    }
    fn read(&self, name: &str) -> io::Result<Option<Vec<u8>>> {
        match name {
            "thermal-mutation.journal" => self.custody.journal_read(false),
            "thermal-mutation.journal.pending" => self.custody.journal_read(true),
            _ => self.user.read(name),
        }
    }
    fn replace(&self, name: &str, expected: Option<&[u8]>, bytes: &[u8]) -> io::Result<()> {
        if name == "thermal-mutation.journal" {
            self.custody.journal_replace(expected, bytes)
        } else {
            self.user.replace(name, expected, bytes)
        }
    }
}

pub(super) fn same_path(left: &str, right: &str) -> bool {
    let left: Vec<u16> = left.encode_utf16().collect();
    let right: Vec<u16> = right.encode_utf16().collect();
    // SAFETY: bounded immutable Unicode buffers. This is the managed ordinal
    // case-insensitive contract, not path normalization or a filesystem lookup.
    unsafe {
        windows_sys::Win32::Globalization::CompareStringOrdinal(
            left.as_ptr(),
            left.len() as i32,
            right.as_ptr(),
            right.len() as i32,
            1,
        ) == 2
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    fn descriptor(sddl: &str) -> Vec<u8> {
        let input: Vec<u16> = sddl.encode_utf16().chain([0]).collect();
        let mut output = std::ptr::null_mut();
        let mut length = 0;
        // SAFETY: Pure conversion of the literal test descriptor into owned memory.
        assert_ne!(
            unsafe {
                windows_sys::Win32::Security::Authorization::ConvertStringSecurityDescriptorToSecurityDescriptorW(input.as_ptr(),1,&mut output,&mut length)
            },
            0
        );
        let descriptor = paths::LocalDescriptor(output);
        unsafe { std::slice::from_raw_parts(descriptor.0.cast::<u8>(), length as usize) }.to_vec()
    }
    #[test]
    fn existing_managed_user_root_allows_system_inheritance_without_untrusted_mutation() {
        for sddl in [
            "O:SYD:AI(A;OICIID;FA;;;SY)(A;OICIID;FA;;;BA)(A;OICIID;FR;;;BU)",
            "O:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)",
        ] {
            assert_eq!(
                paths::accepts_descriptor(&descriptor(sddl), USER_ROOT),
                Some(true)
            );
        }
        for sddl in [
            "O:BUD:P(A;OICI;FA;;;BU)",
            "O:SYD:AI(A;OICIID;FA;;;SY)(A;OICIID;FW;;;BU)",
            "O:SYD:P(A;OICI;FA;;;SY)(A;;0x4;;;BU)",
        ] {
            assert_ne!(
                paths::accepts_descriptor(&descriptor(sddl), USER_ROOT),
                Some(true)
            );
        }
    }
}

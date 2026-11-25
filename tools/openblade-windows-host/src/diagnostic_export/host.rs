//! The Windows host metadata managed `WindowsDiagnosticMetadataSource` and
//! `WindowsAuthorizedUserTokenSource` read, and the `DiagnosticText`
//! default tokens. Each token read that fails is skipped, as managed skips a
//! failed account, profile or directory read; only the clock and version
//! reads, or a cancellation between sources, can fail the capture.

#![allow(unsafe_code)]

use std::{
    path::{Path, PathBuf},
    ptr::{null, null_mut},
};

use windows_sys::{
    Wdk::System::SystemServices::RtlGetVersion,
    Win32::{
        Foundation::{FILETIME, LocalFree},
        Security::{Authorization::ConvertStringSidToSidW, LookupAccountSidW, PSID},
        Storage::FileSystem::FileTimeToLocalFileTime,
        System::{
            Registry::{HKEY_LOCAL_MACHINE, RRF_RT_REG_EXPAND_SZ, RRF_RT_REG_SZ, RegGetValueW},
            SystemInformation::{GetSystemTimePreciseAsFileTime, OSVERSIONINFOW},
            WindowsProgramming::{GetComputerNameW, GetUserNameW},
        },
        UI::Shell::{
            FOLDERID_LocalAppData, FOLDERID_Profile, FOLDERID_ProgramData, FOLDERID_ProgramFiles,
        },
    },
};

use super::{Cancel, Failure, HostMetadata, Metadata, Timestamp};
use crate::service_install_identity::known_folder;

/// The Service process's host.
pub(crate) struct WindowsHost;

impl Metadata for WindowsHost {
    fn capture(&self, cancel: &Cancel) -> Result<HostMetadata, Failure> {
        cancel.check()?;
        let filetime = system_filetime();
        let windows_version = windows_version()?;
        Ok(HostMetadata {
            windows_version,
            tokens: sensitive_tokens(cancel)?,
            captured_at: Timestamp::from_filetime(filetime),
            local_time: Timestamp::from_filetime(local_filetime(filetime)?),
        })
    }
}

pub(crate) fn now() -> Timestamp {
    Timestamp::from_filetime(system_filetime())
}

fn system_filetime() -> u64 {
    let mut time = FILETIME::default();
    // SAFETY: The output is a writable FILETIME.
    unsafe { GetSystemTimePreciseAsFileTime(&mut time) };
    u64::from(time.dwHighDateTime) << 32 | u64::from(time.dwLowDateTime)
}

fn local_filetime(utc: u64) -> Result<u64, Failure> {
    let utc = FILETIME {
        dwLowDateTime: utc as u32,
        dwHighDateTime: (utc >> 32) as u32,
    };
    let mut local = FILETIME::default();
    // SAFETY: Both arguments are valid FILETIME values.
    if unsafe { FileTimeToLocalFileTime(&utc, &mut local) } == 0 {
        return Err(Failure::new("The local time is unavailable."));
    }
    Ok(u64::from(local.dwHighDateTime) << 32 | u64::from(local.dwLowDateTime))
}

/// Managed `Environment.OSVersion.VersionString`: the RtlGetVersion
/// version with a zero revision, then any service-pack text.
fn windows_version() -> Result<String, Failure> {
    let mut version = OSVERSIONINFOW {
        dwOSVersionInfoSize: size_of::<OSVERSIONINFOW>() as u32,
        ..Default::default()
    };
    // SAFETY: The version output is writable and sized.
    if unsafe { RtlGetVersion(&mut version) } != 0 {
        return Err(Failure::new("The Windows version is unavailable."));
    }
    let mut text = format!(
        "Microsoft Windows NT {}.{}.{}.0",
        version.dwMajorVersion, version.dwMinorVersion, version.dwBuildNumber
    );
    let service_pack = wide(&version.szCSDVersion);
    if !service_pack.is_empty() {
        text.push(' ');
        text.push_str(&service_pack);
    }
    Ok(text)
}

fn wide(buffer: &[u16]) -> String {
    let end = buffer
        .iter()
        .position(|unit| *unit == 0)
        .unwrap_or(buffer.len());
    String::from_utf16_lossy(&buffer[..end])
}

fn path_text(path: &Path) -> String {
    path.to_string_lossy().into_owned()
}

fn user_name() -> Option<String> {
    let mut buffer = [0u16; 257];
    let mut length = buffer.len() as u32;
    // SAFETY: The buffer holds `length` UTF-16 units.
    (unsafe { GetUserNameW(buffer.as_mut_ptr(), &mut length) } != 0).then(|| wide(&buffer))
}

fn machine_name() -> Option<String> {
    let mut buffer = [0u16; 32];
    let mut length = buffer.len() as u32;
    // SAFETY: The buffer holds `length` UTF-16 units.
    (unsafe { GetComputerNameW(buffer.as_mut_ptr(), &mut length) } != 0).then(|| wide(&buffer))
}

fn folder(id: &windows_sys::core::GUID) -> Option<PathBuf> {
    known_folder(id).ok()
}

/// Managed `DiagnosticText.DefaultSensitiveTokens`.
// Read only for the history a composition records into.
#[cfg(feature = "service-thermal-mutation-v1")]
#[cfg_attr(test, allow(dead_code))]
pub(crate) fn default_tokens() -> Vec<String> {
    let mut tokens = Vec::new();
    tokens.extend(folder(&FOLDERID_Profile).as_deref().map(path_text));
    tokens.extend(folder(&FOLDERID_LocalAppData).as_deref().map(path_text));
    tokens.extend(machine_name());
    tokens.extend(user_name());
    tokens
}

fn add(tokens: &mut Vec<String>, value: Option<String>) {
    if let Some(value) = value.filter(|value| !super::redact::is_blank(value))
        && !tokens.contains(&value)
    {
        tokens.push(value);
    }
}

/// Managed `ReadSensitiveTokens`, in its insertion order. `cancel` is checked
/// between the sources, each of which can block in the OS.
fn sensitive_tokens(cancel: &Cancel) -> Result<Vec<String>, Failure> {
    let mut tokens = Vec::new();
    for token in authorized_user_tokens(cancel)? {
        add(&mut tokens, Some(token));
    }
    cancel.check()?;
    add(&mut tokens, user_name());
    add(&mut tokens, machine_name());
    cancel.check()?;
    let profile = folder(&FOLDERID_Profile);
    let program_files = folder(&FOLDERID_ProgramFiles);
    let common = folder(&FOLDERID_ProgramData);
    let local = folder(&FOLDERID_LocalAppData);
    add(&mut tokens, profile.as_deref().map(path_text));
    add(
        &mut tokens,
        std::env::var_os("USERPROFILE").map(|value| value.to_string_lossy().into_owned()),
    );
    for root in [&program_files, &common, &local] {
        add(&mut tokens, root.as_deref().map(path_text));
    }
    for root in [&program_files, &common, &local] {
        add(
            &mut tokens,
            root.as_ref().map(|root| path_text(&root.join("OpenBlade"))),
        );
    }
    add(
        &mut tokens,
        std::env::current_exe()
            .ok()
            .and_then(|image| image.parent().map(path_text))
            .map(|directory| directory.trim_end_matches('\\').to_owned()),
    );
    Ok(tokens)
}

fn is_account_sid(text: &str) -> bool {
    text.starts_with("S-1-5-21-")
        && text
            .bytes()
            .all(|b| b.is_ascii_digit() || b"S-".contains(&b))
}

/// Managed `WindowsAuthorizedUserTokenSource.ReadTokens`.
fn authorized_user_tokens(cancel: &Cancel) -> Result<Vec<String>, Failure> {
    let Some(common) = folder(&FOLDERID_ProgramData) else {
        return Ok(Vec::new());
    };
    let root = common.join("OpenBlade");
    let mut sids: Vec<String> = Vec::new();
    let configured = std::env::var("OPENBLADE_USER_SID")
        .ok()
        .filter(|value| !super::redact::is_blank(value))
        .map(|value| value.trim().to_owned())
        .or_else(|| {
            std::fs::read_to_string(root.join("authorized-user.sid"))
                .ok()
                .map(|value| value.trim().to_owned())
        });
    add(&mut sids, configured);
    if let Ok(directories) = std::fs::read_dir(root.join("users")) {
        for directory in directories.flatten() {
            let name = directory.file_name().to_string_lossy().into_owned();
            if is_account_sid(&name) && !sids.iter().any(|sid| sid.eq_ignore_ascii_case(&name)) {
                sids.push(name);
            }
        }
    }
    let mut tokens = Vec::new();
    for sid in sids {
        cancel.check()?;
        add(&mut tokens, Some(sid.clone()));
        if let Some(account) = account_name(&sid) {
            let leaf = account.rsplit('\\').next().map(str::to_owned);
            add(&mut tokens, Some(account));
            add(&mut tokens, leaf);
        }
        if let Some(profile) = profile_path(&sid) {
            let leaf = Path::new(profile.trim_end_matches(['\\', '/']))
                .file_name()
                .map(|leaf| leaf.to_string_lossy().into_owned());
            add(&mut tokens, Some(profile));
            add(&mut tokens, leaf);
        }
    }
    Ok(tokens)
}

fn wide_text(text: &str) -> Vec<u16> {
    text.encode_utf16().chain([0]).collect()
}

/// Managed `NTAccount` translation: `DOMAIN\user`, or the name alone.
fn account_name(sid: &str) -> Option<String> {
    let text = wide_text(sid);
    let mut binary: PSID = null_mut();
    // SAFETY: The string is NUL-terminated; Windows allocates the SID.
    if unsafe { ConvertStringSidToSidW(text.as_ptr(), &mut binary) } == 0 {
        return None;
    }
    let mut name = [0u16; 256];
    let mut domain = [0u16; 256];
    let (mut name_length, mut domain_length) = (name.len() as u32, domain.len() as u32);
    let mut kind = 0;
    // SAFETY: The SID is live and both buffers hold their stated lengths.
    let found = unsafe {
        LookupAccountSidW(
            null(),
            binary,
            name.as_mut_ptr(),
            &mut name_length,
            domain.as_mut_ptr(),
            &mut domain_length,
            &mut kind,
        )
    } != 0;
    // SAFETY: The SID was allocated by ConvertStringSidToSidW.
    unsafe { LocalFree(binary) };
    if !found {
        return None;
    }
    let (name, domain) = (wide(&name), wide(&domain));
    Some(if domain.is_empty() {
        name
    } else {
        format!("{domain}\\{name}")
    })
}

/// Managed `ReadProfilePath`: ProfileImagePath, its variables expanded.
fn profile_path(sid: &str) -> Option<String> {
    let key = wide_text(&format!(
        r"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\{sid}"
    ));
    let value = wide_text("ProfileImagePath");
    let mut buffer = vec![0u16; 1024];
    let mut bytes = (buffer.len() * 2) as u32;
    // SAFETY: The key and value names are NUL-terminated and the buffer holds
    // `bytes` bytes. Without RRF_NOEXPAND an expandable string is expanded.
    let status = unsafe {
        RegGetValueW(
            HKEY_LOCAL_MACHINE,
            key.as_ptr(),
            value.as_ptr(),
            RRF_RT_REG_SZ | RRF_RT_REG_EXPAND_SZ,
            null_mut(),
            buffer.as_mut_ptr().cast(),
            &mut bytes,
        )
    };
    // `bytes` counts the value as stored, before expansion, so it does not
    // bound the expanded text: read up to its terminating NUL.
    (status == 0).then(|| wide(&buffer))
}

#[cfg(test)]
mod tests {
    use super::wide;

    /// `RegGetValueW` expands a REG_EXPAND_SZ value but reports the
    /// unexpanded byte count, so the buffer holds the expanded text, a NUL,
    /// then whatever the buffer held before. Only the text may become a
    /// redaction token.
    #[test]
    fn an_expanded_registry_value_ends_at_its_first_nul() {
        let mut buffer: Vec<u16> = r"C:\Users\Operator".encode_utf16().collect();
        buffer.extend([
            0,
            u16::from(b'j'),
            u16::from(b'u'),
            u16::from(b'n'),
            u16::from(b'k'),
        ]);
        buffer.resize(1024, 0);
        assert_eq!(wide(&buffer), r"C:\Users\Operator");
    }

    #[test]
    fn an_unterminated_buffer_is_read_whole() {
        let buffer: Vec<u16> = "abc".encode_utf16().collect();
        assert_eq!(wide(&buffer), "abc");
    }
}

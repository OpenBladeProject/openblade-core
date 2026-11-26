//! The Windows side of `driver_trust`: registry, DriverStore and protected
//! file reads for `package::capture`, and managed `WindowsDriverApiTrust`'s
//! WinVerifyTrust checks. Every call is read-only.
#![allow(unsafe_code)]

use std::{
    ffi::c_void,
    fs::{File, OpenOptions},
    io::{self, Read},
    mem::size_of,
    os::windows::{
        fs::{FileExt, MetadataExt, OpenOptionsExt},
        io::AsRawHandle,
    },
    ptr::{null, null_mut},
};

use sha2::{Digest, Sha256};
use windows_sys::{
    Win32::{
        Devices::DeviceAndDriverInstallation::SetupGetInfDriverStoreLocationW,
        Foundation::{
            ERROR_FILE_NOT_FOUND, ERROR_MORE_DATA, ERROR_PATH_NOT_FOUND, ERROR_SUCCESS, HWND,
            INVALID_HANDLE_VALUE,
        },
        Security::{
            Cryptography::{
                CERT_CONTEXT, CERT_NAME_STR_REVERSE_FLAG, CERT_X500_NAME_STR,
                Catalog::{
                    CryptCATAdminAcquireContext2, CryptCATAdminCalcHashFromFileHandle2,
                    CryptCATAdminReleaseContext,
                },
                CertNameToStrW, X509_ASN_ENCODING,
            },
            WinTrust::{
                DRIVER_ACTION_VERIFY, WINTRUST_ACTION_GENERIC_VERIFY_V2, WINTRUST_CATALOG_INFO,
                WINTRUST_DATA, WINTRUST_DATA_0, WINTRUST_FILE_INFO, WTD_CACHE_ONLY_URL_RETRIEVAL,
                WTD_CHOICE_CATALOG, WTD_CHOICE_FILE, WTD_DISABLE_MD2_MD4,
                WTD_REVOCATION_CHECK_CHAIN_EXCLUDE_ROOT, WTD_REVOKE_WHOLECHAIN,
                WTD_STATEACTION_CLOSE, WTD_STATEACTION_VERIFY, WTD_UI_NONE,
                WTHelperGetProvCertFromChain, WTHelperGetProvSignerFromChain,
                WTHelperProvDataFromStateData, WinVerifyTrust,
            },
        },
        Storage::FileSystem::{
            FILE_ATTRIBUTE_REPARSE_POINT, FILE_FLAG_SEQUENTIAL_SCAN, FILE_SHARE_READ,
        },
        System::Registry::{
            HKEY, HKEY_LOCAL_MACHINE, KEY_READ, KEY_WOW64_64KEY, REG_DWORD, REG_EXPAND_SZ, REG_SZ,
            RegCloseKey, RegOpenKeyExW, RegQueryValueExW,
        },
    },
    core::GUID,
};

use super::package::{MAXIMUM_PACKAGE_FILE, Machine as PackageMachine, Opened, Registry};
use super::paths::{eq_ci, file_name, full_path, is_fully_qualified, parent};
use super::{
    MICROSOFT_HARDWARE_COMPATIBILITY_PUBLISHER, Reject, matches_publisher, reject,
    validate_publishers, verify_member_algorithms,
};

const MAXIMUM_MEMBERS: usize = 128;
const MAXIMUM_PATH: usize = 4096;
const MAXIMUM_FILE: u64 = 256 * 1024 * 1024;
const MAXIMUM_TOTAL: u64 = 1024 * 1024 * 1024;
const MAXIMUM_VALUE: u32 = 64 * 1024;
const STORE_BUFFER: usize = 32_768;

fn wide(text: &str) -> Vec<u16> {
    text.encode_utf16().chain([0]).collect()
}

fn other(message: impl Into<String>) -> io::Error {
    io::Error::other(message.into())
}

/// The production package reads.
pub(crate) struct Machine;

impl PackageMachine for Machine {
    fn windows(&self) -> io::Result<String> {
        let path = crate::service_install_identity::known_folder(
            &windows_sys::Win32::UI::Shell::FOLDERID_Windows,
        )?;
        path.into_os_string()
            .into_string()
            .map_err(|_| other("The Windows directory is not valid Unicode."))
    }

    fn registry(&self, key: &str, name: &str) -> io::Result<Registry> {
        let mut raw: HKEY = null_mut();
        // SAFETY: The path is terminated and the output is writable.
        let status = unsafe {
            RegOpenKeyExW(
                HKEY_LOCAL_MACHINE,
                wide(key).as_ptr(),
                0,
                KEY_READ | KEY_WOW64_64KEY,
                &mut raw,
            )
        };
        match status {
            ERROR_SUCCESS => {}
            ERROR_FILE_NOT_FOUND | ERROR_PATH_NOT_FOUND => return Ok(Registry::NoKey),
            error => return Err(io::Error::from_raw_os_error(error as i32)),
        }
        let key = RegistryKey(raw);
        let name = wide(name);
        let mut kind = 0;
        let mut size = 0u32;
        // SAFETY: A null buffer asks only for the type and size.
        let status = unsafe {
            RegQueryValueExW(
                key.0,
                name.as_ptr(),
                null(),
                &mut kind,
                null_mut(),
                &mut size,
            )
        };
        match status {
            ERROR_SUCCESS => {}
            ERROR_FILE_NOT_FOUND => return Ok(Registry::NoValue),
            error => return Err(io::Error::from_raw_os_error(error as i32)),
        }
        if size > MAXIMUM_VALUE {
            return Ok(Registry::Other);
        }
        let mut data = vec![0u8; size as usize];
        // SAFETY: The buffer has the advertised size.
        let status = unsafe {
            RegQueryValueExW(
                key.0,
                name.as_ptr(),
                null(),
                &mut kind,
                data.as_mut_ptr(),
                &mut size,
            )
        };
        match status {
            ERROR_SUCCESS => {}
            ERROR_FILE_NOT_FOUND => return Ok(Registry::NoValue),
            // The value grew between the two calls: it is not read.
            ERROR_MORE_DATA => return Ok(Registry::Other),
            error => return Err(io::Error::from_raw_os_error(error as i32)),
        }
        data.truncate(size as usize);
        Ok(match kind {
            REG_SZ | REG_EXPAND_SZ => {
                let mut units: Vec<u16> = data
                    .as_chunks::<2>()
                    .0
                    .iter()
                    .map(|pair| u16::from_le_bytes([pair[0], pair[1]]))
                    .collect();
                while units.last() == Some(&0) {
                    units.pop();
                }
                Registry::Text(String::from_utf16_lossy(&units))
            }
            REG_DWORD => data.try_into().map_or(Registry::Other, |bytes| {
                Registry::Number(i32::from_le_bytes(bytes))
            }),
            _ => Registry::Other,
        })
    }

    fn exists(&self, path: &str) -> bool {
        std::fs::metadata(path).is_ok_and(|metadata| metadata.is_file())
    }

    fn store_location(&self, published_inf: &str) -> io::Result<String> {
        let mut buffer = vec![0u16; STORE_BUFFER];
        let mut required = 0;
        // SAFETY: The name is terminated, no platform or locale is passed,
        // and the buffer has the advertised capacity.
        let located = unsafe {
            SetupGetInfDriverStoreLocationW(
                wide(published_inf).as_ptr(),
                null(),
                null(),
                buffer.as_mut_ptr(),
                buffer.len() as u32,
                &mut required,
            )
        };
        if located == 0 {
            return Err(io::Error::last_os_error());
        }
        let end = buffer
            .iter()
            .position(|unit| *unit == 0)
            .unwrap_or(buffer.len());
        Ok(String::from_utf16_lossy(&buffer[..end]))
    }

    fn open_protected(&self, path: &str) -> Result<Opened, Reject> {
        let full = full_path(path).ok_or_else(|| {
            reject(
                "UnprotectedPath",
                "The driver path is outside its protected Windows directory.",
            )
        })?;
        let mut current = Some(full.as_str());
        while let Some(path) = current {
            if std::fs::symlink_metadata(path)?.file_attributes() & FILE_ATTRIBUTE_REPARSE_POINT
                != 0
            {
                return Err(reject(
                    "ReparsePoint",
                    "Driver package paths may not contain reparse points.",
                ));
            }
            current = parent(path);
        }
        let mut file = OpenOptions::new()
            .read(true)
            .share_mode(FILE_SHARE_READ)
            .open(&full)?;
        let length = file.metadata()?.len();
        if length > MAXIMUM_PACKAGE_FILE {
            return Err(reject(
                "PackageFileTooLarge",
                "The driver package file exceeds the inspection bound.",
            ));
        }
        let mut data = Vec::with_capacity(length as usize);
        (&mut file)
            .take(MAXIMUM_PACKAGE_FILE + 1)
            .read_to_end(&mut data)?;
        if data.len() as u64 > MAXIMUM_PACKAGE_FILE {
            return Err(reject(
                "PackageFileTooLarge",
                "The driver package file exceeds the inspection bound.",
            ));
        }
        Ok(Opened {
            data,
            hold: Some(file),
        })
    }

    fn verify_catalog(&self, catalog: &str, members: &[&str]) -> io::Result<()> {
        verify_catalog_members(
            catalog,
            members,
            &[MICROSOFT_HARDWARE_COMPATIBILITY_PUBLISHER],
        )
        .map(drop)
    }
}

struct RegistryKey(HKEY);

impl Drop for RegistryKey {
    fn drop(&mut self) {
        // SAFETY: A successful RegOpenKeyExW transferred ownership here.
        unsafe { RegCloseKey(self.0) };
    }
}

/// A file opened for reading while denying writers, as managed's
/// `LockedFile` holds it.
struct LockedFile {
    path: String,
    file: File,
    length: u64,
}

/// Managed `NormalizeFilePath`: a bounded, fully qualified local drive path
/// without alternate streams.
fn normalize(path: &str) -> io::Result<String> {
    if path.trim().is_empty() || path.len() > MAXIMUM_PATH || !is_fully_qualified(path) {
        return Err(other(
            "Driver API paths must be bounded, fully qualified local paths.",
        ));
    }
    match full_path(path) {
        Some(full) if full.len() <= MAXIMUM_PATH && !full[3..].contains(':') => Ok(full),
        _ => Err(other(
            "Driver API paths must use a bounded local drive path without alternate streams.",
        )),
    }
}

/// Managed `EnsureNoReparsePoints`: every component below the drive root.
fn ensure_no_reparse_points(path: &str) -> io::Result<()> {
    let mut current = path;
    while current.len() > 3 {
        if std::fs::symlink_metadata(current)?.file_attributes() & FILE_ATTRIBUTE_REPARSE_POINT != 0
        {
            return Err(other("Driver API paths cannot contain reparse points."));
        }
        current = parent(current)
            .ok_or_else(|| other("The driver API path has no local filesystem root."))?;
    }
    Ok(())
}

fn open_locked(path: &str) -> io::Result<LockedFile> {
    ensure_no_reparse_points(path)?;
    let file = OpenOptions::new()
        .read(true)
        .share_mode(FILE_SHARE_READ)
        .custom_flags(FILE_FLAG_SEQUENTIAL_SCAN)
        .open(path)?;
    ensure_no_reparse_points(path)?;
    let length = file.metadata()?.len();
    if length == 0 || length > MAXIMUM_FILE {
        return Err(other(format!(
            "Driver API files must contain between 1 and {MAXIMUM_FILE} bytes."
        )));
    }
    Ok(LockedFile {
        path: path.to_owned(),
        file,
        length,
    })
}

/// Managed `VerifyCatalogMembers`: the catalog's Authenticode trust under
/// one of `publishers`, then each member's catalog membership. Returns the
/// observed content identity.
pub(crate) fn verify_catalog_members(
    catalog: &str,
    members: &[&str],
    publishers: &[&str],
) -> io::Result<String> {
    if members.is_empty() || members.len() > MAXIMUM_MEMBERS {
        return Err(other(format!(
            "A driver catalog must have between 1 and {MAXIMUM_MEMBERS} members."
        )));
    }
    let catalog = normalize(catalog)?;
    let mut paths = vec![catalog];
    for member in members {
        let member = normalize(member)?;
        if paths.iter().any(|known| eq_ci(known, &member)) {
            return Err(other(
                "A catalog or member path was supplied more than once.",
            ));
        }
        paths.push(member);
    }
    let publishers = validate_publishers(publishers)?;
    let files = paths
        .iter()
        .map(|path| open_locked(path))
        .collect::<io::Result<Vec<_>>>()?;
    let mut total = 0u64;
    for file in &files {
        total = total.saturating_add(file.length);
        if total > MAXIMUM_TOTAL {
            return Err(other(
                "The driver catalog and member files exceed the total size bound.",
            ));
        }
    }
    verify_file_trust(&files[0], &publishers, "driver catalog")?;
    for member in &files[1..] {
        verify_member_algorithms(|algorithm| {
            verify_catalog_member(&files[0].path, member, algorithm)
        })?;
    }
    content_identity(&files)
}

/// Managed `VerifyAuthenticodeFile`: one file's embedded Authenticode trust
/// under one of `publishers`. Returns the observed content identity.
#[allow(dead_code)] // The NVML loader's module check is its consumer.
pub(crate) fn verify_authenticode_file(path: &str, publishers: &[&str]) -> io::Result<String> {
    let path = normalize(path)?;
    let publishers = validate_publishers(publishers)?;
    let file = open_locked(&path)?;
    verify_file_trust(&file, &publishers, "driver API file")?;
    content_identity(std::slice::from_ref(&file))
}

fn verify_file_trust(file: &LockedFile, publishers: &[&str], description: &str) -> io::Result<()> {
    let path = wide(&file.path);
    let mut info = WINTRUST_FILE_INFO {
        cbStruct: size_of::<WINTRUST_FILE_INFO>() as u32,
        pcwszFilePath: path.as_ptr(),
        hFile: file.file.as_raw_handle(),
        pgKnownSubject: null_mut(),
    };
    let status = verify_object(
        WTD_CHOICE_FILE,
        WINTRUST_DATA_0 { pFile: &mut info },
        WINTRUST_ACTION_GENERIC_VERIFY_V2,
        Some(publishers),
        description,
    )?;
    if status != 0 {
        return Err(other(format!(
            "Windows rejected {description} trust (0x{:08X}).",
            status as u32
        )));
    }
    Ok(())
}

struct CatalogAdmin(isize);

impl Drop for CatalogAdmin {
    fn drop(&mut self) {
        // SAFETY: A successful CryptCATAdminAcquireContext2 transferred
        // ownership here.
        unsafe { CryptCATAdminReleaseContext(self.0, 0) };
    }
}

fn verify_catalog_member(catalog: &str, member: &LockedFile, algorithm: &str) -> io::Result<i32> {
    let mut admin = 0;
    let name = wide(algorithm);
    // SAFETY: The subsystem and algorithm are valid and no policy is passed.
    if unsafe {
        CryptCATAdminAcquireContext2(&mut admin, &DRIVER_ACTION_VERIFY, name.as_ptr(), null(), 0)
    } == 0
    {
        return Err(io::Error::last_os_error());
    }
    let admin = CatalogAdmin(admin);
    let handle = member.file.as_raw_handle();
    let mut length = 0u32;
    // SAFETY: A null buffer asks only for the hash size.
    let sized = unsafe {
        CryptCATAdminCalcHashFromFileHandle2(admin.0, handle, &mut length, null_mut(), 0)
    };
    if sized == 0 || length == 0 || length > 128 {
        return Err(other(
            "Windows could not size the driver catalog member hash.",
        ));
    }
    let mut hash = vec![0u8; length as usize];
    // SAFETY: The buffer has the advertised size.
    if unsafe {
        CryptCATAdminCalcHashFromFileHandle2(admin.0, handle, &mut length, hash.as_mut_ptr(), 0)
    } == 0
    {
        return Err(io::Error::last_os_error());
    }
    let tag = wide(
        &hash[..length as usize]
            .iter()
            .map(|byte| format!("{byte:02X}"))
            .collect::<String>(),
    );
    let catalog = wide(catalog);
    let path = wide(&member.path);
    let mut info = WINTRUST_CATALOG_INFO {
        cbStruct: size_of::<WINTRUST_CATALOG_INFO>() as u32,
        dwCatalogVersion: 0,
        pcwszCatalogFilePath: catalog.as_ptr(),
        pcwszMemberTag: tag.as_ptr(),
        pcwszMemberFilePath: path.as_ptr(),
        hMemberFile: handle,
        pbCalculatedFileHash: hash.as_mut_ptr(),
        cbCalculatedFileHash: length,
        pcCatalogContext: null_mut(),
        hCatAdmin: admin.0,
    };
    verify_object(
        WTD_CHOICE_CATALOG,
        WINTRUST_DATA_0 {
            pCatalog: &mut info,
        },
        DRIVER_ACTION_VERIFY,
        None,
        "driver catalog member",
    )
}

/// Managed `VerifyObjectStatus`: WinVerifyTrust with no UI, whole-chain
/// revocation from the cache only, and the verified signer's subject checked
/// against `publishers` before the state is closed.
fn verify_object(
    choice: u32,
    object: WINTRUST_DATA_0,
    action: GUID,
    publishers: Option<&[&str]>,
    description: &str,
) -> io::Result<i32> {
    let mut action = action;
    let mut data = WINTRUST_DATA {
        cbStruct: size_of::<WINTRUST_DATA>() as u32,
        dwUIChoice: WTD_UI_NONE,
        fdwRevocationChecks: WTD_REVOKE_WHOLECHAIN,
        dwUnionChoice: choice,
        Anonymous: object,
        dwStateAction: WTD_STATEACTION_VERIFY,
        dwProvFlags: WTD_REVOCATION_CHECK_CHAIN_EXCLUDE_ROOT
            | WTD_CACHE_ONLY_URL_RETRIEVAL
            | WTD_DISABLE_MD2_MD4,
        ..Default::default()
    };
    let window = INVALID_HANDLE_VALUE as HWND;
    // SAFETY: The data and the object it points to outlive the call.
    let status = unsafe { WinVerifyTrust(window, &mut action, (&raw mut data).cast::<c_void>()) };
    let checked = if status == 0
        && let Some(publishers) = publishers
    {
        verified_publisher(&data, description).and_then(|subject| {
            if matches_publisher(&subject, publishers) {
                Ok(status)
            } else {
                Err(other(format!(
                    "The verified {description} publisher is not allowed."
                )))
            }
        })
    } else {
        Ok(status)
    };
    data.dwStateAction = WTD_STATEACTION_CLOSE;
    // SAFETY: Closes the state the verify call above opened.
    unsafe { WinVerifyTrust(window, &mut action, (&raw mut data).cast::<c_void>()) };
    checked
}

/// Managed `GetVerifiedPublisher`: the subject of the first signer's
/// certificate in the verified provider state, as .NET's
/// `X509Certificate2.Subject` formats it.
fn verified_publisher(data: &WINTRUST_DATA, description: &str) -> io::Result<String> {
    // SAFETY: The state is the one a successful verify call just opened.
    let provider = unsafe { WTHelperProvDataFromStateData(data.hWVTStateData) };
    let signer = if provider.is_null() {
        null_mut()
    } else {
        // SAFETY: The provider data belongs to the open state.
        unsafe { WTHelperGetProvSignerFromChain(provider, 0, 0, 0) }
    };
    let certificate = if signer.is_null() {
        null_mut()
    } else {
        // SAFETY: The signer belongs to the open state.
        unsafe { WTHelperGetProvCertFromChain(signer, 0) }
    };
    if certificate.is_null() {
        return Err(other(format!(
            "Windows did not provide the verified {description} signer."
        )));
    }
    // SAFETY: A non-null provider certificate of the open state.
    let context: *const CERT_CONTEXT = unsafe { (*certificate).pCert };
    // SAFETY: A non-null context's certificate information is valid.
    if context.is_null() || unsafe { (*context).pCertInfo }.is_null() {
        return Err(other(format!(
            "Windows did not provide the verified {description} certificate."
        )));
    }
    // SAFETY: Checked non-null above; the state is still open.
    let subject = unsafe { &(*(*context).pCertInfo).Subject };
    let kind = CERT_X500_NAME_STR | CERT_NAME_STR_REVERSE_FLAG;
    // SAFETY: A null buffer asks only for the length, terminator included.
    let length = unsafe { CertNameToStrW(X509_ASN_ENCODING, subject, kind, null_mut(), 0) };
    if length == 0 || length > 4096 {
        return Err(other(format!(
            "Windows did not provide the verified {description} certificate."
        )));
    }
    let mut buffer = vec![0u16; length as usize];
    // SAFETY: The buffer has the advertised capacity.
    let written = unsafe {
        CertNameToStrW(
            X509_ASN_ENCODING,
            subject,
            kind,
            buffer.as_mut_ptr(),
            length,
        )
    };
    let end = (written as usize).saturating_sub(1).min(buffer.len());
    Ok(String::from_utf16_lossy(&buffer[..end]))
}

/// Managed `ComputeObservedContentIdentity`.
fn content_identity(files: &[LockedFile]) -> io::Result<String> {
    let mut identity = Sha256::new();
    identity.update(b"OpenBlade.WindowsDriverApiTrust.v1\0");
    identity.update((files.len() as i32).to_le_bytes());
    for file in files {
        let name = file_name(&file.path).to_uppercase();
        identity.update((name.len() as i32).to_le_bytes());
        identity.update(name.as_bytes());
        identity.update((file.length as i64).to_le_bytes());
        identity.update(hash_file(file)?);
    }
    Ok(format!(
        "SHA256:{}",
        identity
            .finalize()
            .iter()
            .map(|byte| format!("{byte:02X}"))
            .collect::<String>()
    ))
}

fn hash_file(file: &LockedFile) -> io::Result<[u8; 32]> {
    let mut hash = Sha256::new();
    let mut buffer = vec![0u8; 64 * 1024];
    let mut offset = 0u64;
    while offset < file.length {
        let wanted = buffer.len().min((file.length - offset) as usize);
        let read = file.file.seek_read(&mut buffer[..wanted], offset)?;
        if read == 0 {
            return Err(io::Error::new(
                io::ErrorKind::UnexpectedEof,
                "A locked driver API file changed while its identity was read.",
            ));
        }
        hash.update(&buffer[..read]);
        offset += read as u64;
    }
    if file.file.metadata()?.len() != file.length {
        return Err(other(
            "A locked driver API file changed during verification.",
        ));
    }
    Ok(hash.finalize().into())
}

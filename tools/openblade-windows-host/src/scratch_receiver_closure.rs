//! Scratch-only app-local FDD receiver closure. Installed shared frameworks are outside this proof.
#![allow(unsafe_code)]

use crate::{
    scratch_receipt_policy,
    service_image::{self, ImageSnapshot},
    windows_protected_path::{
        FileIdentity, check_regular_file, denied, directory_info, dword, file_identity,
        open_relative, section, sid, verify_descriptor, word,
    },
};
use serde::Deserialize;
use serde_json::Value;
use sha2::{Digest, Sha256};
use std::{
    collections::{BTreeMap, BTreeSet},
    ffi::OsStr,
    fs::File,
    io::{self, Read, Seek},
    os::windows::io::AsRawHandle,
};
use windows_sys::{
    Wdk::Storage::FileSystem::{
        FILE_DIRECTORY_FILE, FILE_NON_DIRECTORY_FILE, FILE_OPEN, FILE_OPEN_REPARSE_POINT,
    },
    Win32::{
        Foundation::ERROR_NO_MORE_FILES,
        Security::{SE_DACL_PRESENT, SE_SELF_RELATIVE},
        Storage::FileSystem::{
            FILE_ALL_ACCESS, FILE_ATTRIBUTE_DIRECTORY, FILE_ATTRIBUTE_REPARSE_POINT,
            FILE_GENERIC_READ, FILE_ID_BOTH_DIR_INFO, FILE_SHARE_READ, FILE_SHARE_WRITE,
            FileIdBothDirectoryInfo, FileIdBothDirectoryRestartInfo, GetFileInformationByHandleEx,
        },
    },
};

const STEM: &str = "OpenBlade.CandidateReceipt.Scratch";
const READINESS: &str = "openblade-scratch-candidate-receipt.exe";
const MAX_FILES: usize = 128;
const MAX_JSON_BYTES: u64 = 256 * 1024;
const PREFIX: &str = "OpenBlade.ScratchReceiverClosure.v1";
const OWNER_RIGHTS: &[u8] = &[1, 1, 0, 0, 0, 0, 0, 3, 4, 0, 0, 0];
// CreateFiles | CreateDirectories | Delete | DeleteSubdirectoriesAndFiles |
// ChangePermissions | TakeOwnership, exactly WindowsCandidateReceiptStorage.SealAce.
const SEAL_MASK: u32 = 0x000d0046;

struct RetainedDirectory {
    file: File,
    identity: FileIdentity,
}
struct RetainedFile {
    file: File,
    identity: FileIdentity,
    snapshot: ImageSnapshot,
}

pub(crate) struct ScratchReceiverClosure {
    owner: Vec<u8>,
    volume: u32,
    directories: BTreeMap<String, RetainedDirectory>,
    files: BTreeMap<String, RetainedFile>,
    digest: [u8; 32],
}

impl ScratchReceiverClosure {
    pub(crate) fn retain(
        scratch: &File,
        owner: &[u8],
        expected_digest: [u8; 32],
    ) -> io::Result<Self> {
        let volume = directory_info(scratch)?.dwVolumeSerialNumber;
        let receiver = open_directory(scratch, "receiver", owner, volume)?;
        let mut closure = Self {
            owner: owner.to_vec(),
            volume,
            directories: BTreeMap::from([(String::new(), receiver)]),
            files: BTreeMap::new(),
            digest: expected_digest,
        };
        let deps_name = format!("{STEM}.deps.json");
        closure.retain_file(&deps_name)?;
        let names = required_files(&closure.read_json(&deps_name)?)?;
        for name in names {
            closure.retain_file(&name)?;
        }
        validate_runtime_config(&closure.read_json(&format!("{STEM}.runtimeconfig.json"))?)?;
        require_loader_environment()?;
        closure.require_unchanged()?;
        Ok(closure)
    }

    fn retain_file(&mut self, name: &str) -> io::Result<()> {
        if self.files.contains_key(name) {
            return Ok(());
        }
        let mut parent = String::new();
        let mut parts = name.split('\\').peekable();
        while let Some(part) = parts.next() {
            if parts.peek().is_none() {
                let file = open_relative(
                    &self.directories[&parent].file,
                    OsStr::new(part),
                    FILE_GENERIC_READ,
                    FILE_SHARE_READ,
                    FILE_OPEN,
                    FILE_NON_DIRECTORY_FILE | FILE_OPEN_REPARSE_POINT,
                )?;
                self.verify_file(&file)?;
                let snapshot = service_image::snapshot(&file)?;
                self.files.insert(
                    name.to_owned(),
                    RetainedFile {
                        identity: check_regular_file(&file)?,
                        file,
                        snapshot,
                    },
                );
            } else {
                let next = if parent.is_empty() {
                    part.to_owned()
                } else {
                    format!("{parent}\\{part}")
                };
                if !self.directories.contains_key(&next) {
                    let directory = open_directory(
                        &self.directories[&parent].file,
                        part,
                        &self.owner,
                        self.volume,
                    )?;
                    self.directories.insert(next.clone(), directory);
                }
                parent = next;
            }
        }
        Ok(())
    }

    fn verify_file(&self, file: &File) -> io::Result<()> {
        let identity = check_regular_file(file)?;
        if identity.index == 0 || identity.volume != self.volume {
            return Err(invalid("receiver member has an invalid identity or volume"));
        }
        verify_descriptor(file, |bytes| {
            scratch_receipt_policy::accepts(bytes, &self.owner, false, false)
        })
    }

    fn read_json(&mut self, name: &str) -> io::Result<Vec<u8>> {
        let member = self
            .files
            .get_mut(name)
            .ok_or_else(|| invalid("missing retained receiver JSON"))?;
        if !(1..=MAX_JSON_BYTES).contains(&member.snapshot.size) {
            return Err(invalid("receiver JSON exceeds its size bound"));
        }
        member.file.rewind()?;
        let mut bytes = Vec::new();
        (&mut member.file)
            .take(MAX_JSON_BYTES + 1)
            .read_to_end(&mut bytes)?;
        if bytes.len() as u64 != member.snapshot.size
            || service_image::snapshot(&member.file)? != member.snapshot
        {
            return Err(invalid("receiver JSON changed during verification"));
        }
        Ok(bytes)
    }

    pub(crate) fn require_unchanged(&mut self) -> io::Result<[u8; 32]> {
        require_loader_environment()?;
        self.require_members()?;
        let mut encoded = Vec::new();
        write_string(&mut encoded, PREFIX);
        for (name, member) in &mut self.files {
            let identity = check_regular_file(&member.file)?;
            require_identity(identity, member.identity, self.volume)?;
            if service_image::snapshot(&member.file)? != member.snapshot {
                return Err(invalid("retained receiver member changed"));
            }
            verify_descriptor(&member.file, |bytes| {
                scratch_receipt_policy::accepts(bytes, &self.owner, false, false)
            })?;
            let size = member.snapshot.size;
            if !(1..=service_image::MAX_IMAGE_BYTES).contains(&size) {
                return Err(invalid("receiver member exceeds its size bound"));
            }
            member.file.rewind()?;
            let mut hash = Sha256::new();
            let read = io::copy(&mut (&mut member.file).take(size + 1), &mut hash)?;
            if read != size || service_image::snapshot(&member.file)? != member.snapshot {
                return Err(invalid("receiver member changed while hashing"));
            }
            write_member(&mut encoded, name, size, hash.finalize().into());
            check_regular_file(&member.file)?;
            verify_descriptor(&member.file, |bytes| {
                scratch_receipt_policy::accepts(bytes, &self.owner, false, false)
            })?;
        }
        self.require_members()?;
        let actual: [u8; 32] = Sha256::digest(&encoded).into();
        if actual != self.digest {
            return Err(invalid(
                "receiver closure differs from authenticated launch evidence",
            ));
        }
        Ok(actual)
    }

    fn require_members(&self) -> io::Result<()> {
        let mut actual = BTreeSet::new();
        for (parent, directory) in &self.directories {
            let info = directory_info(&directory.file)?;
            require_identity(file_identity(&info), directory.identity, self.volume)?;
            verify_sealed_directory(&directory.file, &self.owner)?;
            let expected_children = self
                .files
                .keys()
                .chain(self.directories.keys())
                .filter(|name| {
                    !name.is_empty()
                        && name.rsplit_once("\\").map_or("", |(parent, _)| parent) == parent
                })
                .count();
            for (name, attributes) in enumerate(
                &directory.file,
                expected_children + usize::from(parent.is_empty()),
            )? {
                let relative = if parent.is_empty() {
                    name.clone()
                } else {
                    format!("{parent}\\{name}")
                };
                // Only this exact independently build-pinned readiness executable
                // is outside the receiver's managed load closure.
                if parent.is_empty()
                    && name == READINESS
                    && attributes & (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT) == 0
                {
                    continue;
                }
                let expected_directory = self.directories.contains_key(&relative);
                if attributes & FILE_ATTRIBUTE_REPARSE_POINT != 0
                    || (attributes & FILE_ATTRIBUTE_DIRECTORY != 0) != expected_directory
                    || !actual.insert(relative)
                {
                    return Err(invalid("receiver namespace contains an unexpected entry"));
                }
            }
        }
        let expected: BTreeSet<_> = self
            .files
            .keys()
            .chain(self.directories.keys().filter(|name| !name.is_empty()))
            .cloned()
            .collect();
        if actual != expected {
            return Err(invalid(
                "receiver closure contains missing or unexpected members",
            ));
        }
        Ok(())
    }
}

fn open_directory(
    parent: &File,
    name: &str,
    owner: &[u8],
    volume: u32,
) -> io::Result<RetainedDirectory> {
    // C# retains its original WRITE_DAC namespace seal handle. Permit that
    // existing handle while denying rename/delete; the exact seal blocks new writers.
    let file = open_relative(
        parent,
        OsStr::new(name),
        FILE_GENERIC_READ,
        FILE_SHARE_READ | FILE_SHARE_WRITE,
        FILE_OPEN,
        FILE_DIRECTORY_FILE | FILE_OPEN_REPARSE_POINT,
    )?;
    let info = directory_info(&file)?;
    let identity = file_identity(&info);
    if identity.index == 0 || identity.volume != volume {
        return Err(invalid(
            "receiver directory has an invalid identity or volume",
        ));
    }
    verify_sealed_directory(&file, owner)?;
    Ok(RetainedDirectory { file, identity })
}

pub(crate) fn verify_sealed_directory(file: &File, owner: &[u8]) -> io::Result<()> {
    verify_descriptor(file, |bytes| accepts_sealed_descriptor(bytes, owner))
}

// This narrow scratch-only policy never changes the installed-path DACL policy.
pub(crate) fn accepts_sealed_descriptor(bytes: &[u8], owner: &[u8]) -> Option<bool> {
    let control = word(bytes, 2)?;
    if *bytes.first()? != 1
        || control & (SE_SELF_RELATIVE | SE_DACL_PRESENT) != SE_SELF_RELATIVE | SE_DACL_PRESENT
        || sid(section(bytes, 4)?)? != owner
    {
        return Some(false);
    }
    let acl = section(bytes, 16)?;
    if !matches!(acl.first()?, 2 | 4) || word(acl, 4)? != 2 {
        return Some(false);
    }
    let mut aces = acl.get(8..usize::from(word(acl, 2)?))?;
    for index in 0..2 {
        let length = usize::from(word(aces, 2)?);
        let ace = aces.get(..length)?;
        let trustee = sid(ace.get(8..)?)?;
        if trustee.len() + 8 != length {
            return Some(false);
        }
        if index == 0 {
            if ace[0] != 1 || ace[1] != 0 || dword(ace, 4)? != SEAL_MASK || trustee != OWNER_RIGHTS
            {
                return Some(false);
            }
        } else if ace[0] != 0
            || !matches!(ace[1], 3 | 0x13)
            || dword(ace, 4)? != FILE_ALL_ACCESS
            || trustee != owner
        {
            return Some(false);
        }
        aces = &aces[length..];
    }
    Some(aces.is_empty())
}

fn enumerate(directory: &File, maximum_members: usize) -> io::Result<Vec<(String, u32)>> {
    let mut result = Vec::new();
    // u64 storage provides the alignment required by FILE_ID_BOTH_DIR_INFO.
    let mut buffer = vec![0u64; 8192];
    let mut class = FileIdBothDirectoryRestartInfo;
    loop {
        // SAFETY: directory is a live retained directory handle; the aligned
        // allocation is writable and its exact byte capacity is supplied.
        let ok = unsafe {
            GetFileInformationByHandleEx(
                directory.as_raw_handle(),
                class,
                buffer.as_mut_ptr().cast(),
                (buffer.len() * 8) as u32,
            )
        };
        if ok == 0 {
            let error = io::Error::last_os_error();
            if error.raw_os_error() == Some(ERROR_NO_MORE_FILES as i32) {
                break;
            }
            return Err(error);
        }
        class = FileIdBothDirectoryInfo;
        // SAFETY: buffer owns this many initialized bytes throughout parsing.
        let bytes =
            unsafe { std::slice::from_raw_parts(buffer.as_ptr().cast::<u8>(), buffer.len() * 8) };
        let mut offset = 0;
        loop {
            let entry = bytes
                .get(offset..)
                .ok_or_else(|| invalid("invalid receiver directory enumeration"))?;
            let next =
                dword(entry, 0).ok_or_else(|| invalid("short receiver directory entry"))? as usize;
            let length = dword(
                entry,
                std::mem::offset_of!(FILE_ID_BOTH_DIR_INFO, FileNameLength),
            )
            .ok_or_else(|| invalid("short receiver directory entry"))?
                as usize;
            let start = std::mem::offset_of!(FILE_ID_BOTH_DIR_INFO, FileName);
            if length == 0 || length % 2 != 0 || length > 510 {
                return Err(invalid("invalid receiver member name length"));
            }
            let name_bytes = entry
                .get(start..start + length)
                .ok_or_else(|| invalid("short receiver member name"))?;
            let utf16: Vec<_> = name_bytes
                .chunks_exact(2)
                .map(|v| u16::from_le_bytes([v[0], v[1]]))
                .collect();
            let name =
                String::from_utf16(&utf16).map_err(|_| invalid("invalid receiver member name"))?;
            let attributes = dword(
                entry,
                std::mem::offset_of!(FILE_ID_BOTH_DIR_INFO, FileAttributes),
            )
            .ok_or_else(|| invalid("short receiver directory entry"))?;
            if name != "." && name != ".." {
                result.push((name, attributes));
                // Bound allocation to the exact expected direct children, plus
                // the sole readiness exception at the receiver root.
                if result.len() > maximum_members {
                    return Err(invalid("receiver namespace exceeds its member bound"));
                }
            }
            if next == 0 {
                break;
            }
            if next < start + length || !next.is_multiple_of(8) {
                return Err(invalid("invalid receiver directory entry offset"));
            }
            offset = offset
                .checked_add(next)
                .ok_or_else(|| invalid("receiver enumeration overflow"))?;
        }
    }
    Ok(result)
}

fn required_files(bytes: &[u8]) -> io::Result<BTreeSet<String>> {
    let mut names: BTreeSet<_> = ["exe", "dll", "deps.json", "runtimeconfig.json"]
        .map(|suffix| format!("{STEM}.{suffix}"))
        .into_iter()
        .collect();
    let deps = json(bytes)?;
    let targets = object(field(&deps, "targets")?)?;
    if targets.len() != 1 {
        return Err(invalid("receiver requires one framework-dependent target"));
    }
    for library in object(targets.values().next().expect("one target"))?.values() {
        let library = object(library)?;
        if library.contains_key("native") {
            return Err(invalid("receiver requires managed runtime assets"));
        }
        if let Some(runtime) = library.get("runtime") {
            for asset in object(runtime)?.keys() {
                add_name(
                    &mut names,
                    asset.rsplit('/').next().unwrap_or_default(),
                    true,
                )?;
            }
        }
        if let Some(resources) = library.get("resources") {
            for (name, asset) in object(resources)? {
                let parts: Vec<_> = name.split('/').collect();
                if parts.len() != 2 || field(asset, "locale")?.as_str() != Some(parts[0]) {
                    return Err(invalid("receiver has an unsupported satellite asset"));
                }
                add_name(&mut names, &name.replace('/', "\\"), false)?;
            }
        }
        if let Some(runtime_targets) = library.get("runtimeTargets") {
            for (name, asset) in object(runtime_targets)? {
                if field(asset, "rid")?.as_str() != Some("win")
                    || field(asset, "assetType")?.as_str() != Some("runtime")
                    || !name.starts_with("runtimes/win/lib/")
                {
                    return Err(invalid("receiver has an unsupported RID asset"));
                }
                add_name(&mut names, &name.replace('/', "\\"), false)?;
            }
        }
    }
    Ok(names)
}

fn add_name(names: &mut BTreeSet<String>, name: &str, allow_entry: bool) -> io::Result<()> {
    let invalid_name = !name.ends_with(".dll")
        || name.len() > 192
        || name.split('\\').any(|part| {
            part.is_empty()
                || matches!(part, "." | "..")
                || !part
                    .bytes()
                    .all(|ch| ch.is_ascii_alphanumeric() || b".-_".contains(&ch))
        })
        || names.iter().any(|old| old.eq_ignore_ascii_case(name));
    if invalid_name {
        if allow_entry && name == format!("{STEM}.dll") {
            return Ok(());
        }
        return Err(invalid(
            "receiver has an unexpected or duplicate runtime asset",
        ));
    }
    names.insert(name.to_owned());
    if names.len() > MAX_FILES {
        return Err(invalid("receiver closure exceeds its file bound"));
    }
    Ok(())
}

fn validate_runtime_config(bytes: &[u8]) -> io::Result<()> {
    let config = json(bytes)?;
    for (name, value) in object(field(&config, "runtimeOptions")?)? {
        if !matches!(
            name.as_str(),
            "tfm" | "framework" | "frameworks" | "configProperties"
        ) {
            return Err(invalid("receiver has an unsupported loader option"));
        }
        if name == "configProperties" {
            for (switch, value) in object(value)? {
                if !matches!(
                    switch.as_str(),
                    "System.Reflection.Metadata.MetadataUpdater.IsSupported"
                        | "System.Runtime.Serialization.EnableUnsafeBinaryFormatterSerialization"
                        | "CSWINRT_USE_WINDOWS_UI_XAML_PROJECTIONS"
                ) || !value.is_boolean()
                {
                    return Err(invalid("receiver has an unsupported runtime switch"));
                }
            }
        }
    }
    Ok(())
}

fn require_loader_environment() -> io::Result<()> {
    for name in [
        "DOTNET_ADDITIONAL_DEPS",
        "DOTNET_SHARED_STORE",
        "DOTNET_STARTUP_HOOKS",
    ] {
        if std::env::var_os(name).is_some_and(|value| !value.is_empty()) {
            return Err(invalid("receiver has an external loader override"));
        }
    }
    Ok(())
}

fn object(value: &Value) -> io::Result<&serde_json::Map<String, Value>> {
    value
        .as_object()
        .ok_or_else(|| invalid("receiver JSON requires an object"))
}
fn field<'a>(value: &'a Value, name: &str) -> io::Result<&'a Value> {
    object(value)?
        .get(name)
        .ok_or_else(|| invalid("receiver JSON is missing a required field"))
}
fn invalid(message: &'static str) -> io::Error {
    denied(message)
}

// BinaryWriter writes UTF-8 byte length as a 7-bit integer, then bytes. All
// admitted member names are ASCII, so BTreeMap order equals StringComparer.Ordinal.
fn write_string(encoded: &mut Vec<u8>, value: &str) {
    let mut length = value.len();
    while length >= 128 {
        encoded.push((length as u8 & 0x7f) | 0x80);
        length >>= 7;
    }
    encoded.push(length as u8);
    encoded.extend_from_slice(value.as_bytes());
}
fn write_member(encoded: &mut Vec<u8>, name: &str, size: u64, hash: [u8; 32]) {
    write_string(encoded, name);
    encoded.extend_from_slice(&(size as i64).to_le_bytes());
    encoded.extend_from_slice(&hash);
}

// Preserve duplicate-field refusal instead of serde_json::Value's last-value
// wins behavior. RawValue retains each nested object until this bounded walk.
fn json(bytes: &[u8]) -> io::Result<Value> {
    fn parse(raw: &serde_json::value::RawValue, depth: usize) -> io::Result<Value> {
        if depth > 64 {
            return Err(invalid("receiver JSON exceeds its nesting bound"));
        }
        let text = raw.get().trim_start();
        if text.starts_with('{') {
            struct Object(BTreeMap<String, Box<serde_json::value::RawValue>>);
            impl<'de> Deserialize<'de> for Object {
                fn deserialize<D: serde::Deserializer<'de>>(
                    deserializer: D,
                ) -> Result<Self, D::Error> {
                    struct Visitor;
                    impl<'de> serde::de::Visitor<'de> for Visitor {
                        type Value = Object;
                        fn expecting(
                            &self,
                            formatter: &mut std::fmt::Formatter,
                        ) -> std::fmt::Result {
                            formatter.write_str("an object with unique properties")
                        }
                        fn visit_map<M: serde::de::MapAccess<'de>>(
                            self,
                            mut map: M,
                        ) -> Result<Object, M::Error> {
                            let mut fields = BTreeMap::new();
                            while let Some((name, value)) =
                                map.next_entry::<String, Box<serde_json::value::RawValue>>()?
                            {
                                if fields.insert(name, value).is_some() {
                                    return Err(serde::de::Error::custom(
                                        "duplicate receiver JSON property",
                                    ));
                                }
                            }
                            Ok(Object(fields))
                        }
                    }
                    deserializer.deserialize_map(Visitor)
                }
            }
            let fields: Object = serde_json::from_str(text).map_err(io::Error::other)?;
            Ok(Value::Object(
                fields
                    .0
                    .into_iter()
                    .map(|(name, value)| Ok((name, parse(&value, depth + 1)?)))
                    .collect::<io::Result<_>>()?,
            ))
        } else if text.starts_with('[') {
            let values: Vec<Box<serde_json::value::RawValue>> =
                serde_json::from_str(text).map_err(io::Error::other)?;
            Ok(Value::Array(
                values
                    .iter()
                    .map(|value| parse(value, depth + 1))
                    .collect::<io::Result<_>>()?,
            ))
        } else {
            serde_json::from_str(text).map_err(io::Error::other)
        }
    }
    if bytes.is_empty() || bytes.len() as u64 > MAX_JSON_BYTES {
        return Err(invalid("receiver JSON exceeds its size bound"));
    }
    let raw: Box<serde_json::value::RawValue> =
        serde_json::from_slice(bytes).map_err(io::Error::other)?;
    parse(&raw, 0)
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn binary_writer_string_and_member_encoding_is_exact() {
        let mut bytes = Vec::new();
        write_string(&mut bytes, PREFIX);
        assert_eq!(bytes[0], 35);
        assert_eq!(&bytes[1..], PREFIX.as_bytes());
        bytes.clear();
        write_string(&mut bytes, &"a".repeat(128));
        assert_eq!(&bytes[..2], &[0x80, 1]);
        bytes.clear();
        write_member(&mut bytes, "a.dll", 0x01020304, [0x55; 32]);
        assert_eq!(
            &bytes[..14],
            &[5, b'a', b'.', b'd', b'l', b'l', 4, 3, 2, 1, 0, 0, 0, 0]
        );
        assert_eq!(&bytes[14..], &[0x55; 32]);
    }
    #[test]
    fn required_files_include_managed_fallbacks_satellites_and_windows_rid_assets() {
        let files = required_files(br#"{"targets":{"net10.0":{"main":{"runtime":{"OpenBlade.CandidateReceipt.Scratch.dll":{},"lib/A.dll":{}},"resources":{"fr/A.resources.dll":{"locale":"fr"}},"runtimeTargets":{"runtimes/win/lib/net8.0/B.dll":{"rid":"win","assetType":"runtime"}}}}}}"#).unwrap();
        assert_eq!(files.len(), 7);
        for name in [
            "A.dll",
            "fr\\A.resources.dll",
            "runtimes\\win\\lib\\net8.0\\B.dll",
        ] {
            assert!(files.contains(name));
        }
    }
    #[test]
    fn refuses_ambiguous_unsafe_and_unbounded_loader_assets() {
        for deps in [
            r#"{"targets":{}}"#,
            r#"{"targets":{"a":{},"b":{}}}"#,
            r#"{"targets":{"a":{"lib":{"native":{}}}}}"#,
            r#"{"targets":{"a":{"lib":{"runtime":{"A.dll":{},"a.dll":{}}}}}}"#,
            r#"{"targets":{"a":{"lib":{"runtimeTargets":{"runtimes/linux/lib/A.dll":{"rid":"linux","assetType":"runtime"}}}}}}"#,
            r#"{"targets":{"a":{"lib":{"resources":{"fr/A.dll":{"locale":"en"}}}}}}"#,
            r#"{"targets":{},"targets":{"a":{}}}"#,
        ] {
            assert!(required_files(deps.as_bytes()).is_err(), "{deps}");
        }
        let mut names = BTreeSet::new();
        for name in [
            "..\\A.dll",
            "C:A.dll",
            "a\\\\B.dll",
            "A.DLL",
            "a/b.dll",
            "é.dll",
        ] {
            assert!(add_name(&mut names, name, false).is_err());
        }
        for i in 0..MAX_FILES {
            add_name(&mut names, &format!("A{i}.dll"), false).unwrap();
        }
        assert!(add_name(&mut names, "overflow.dll", false).is_err());
    }
    #[test]
    fn loader_config_refuses_probing_hooks_unknown_and_nonboolean_switches() {
        validate_runtime_config(br#"{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"},"configProperties":{"System.Reflection.Metadata.MetadataUpdater.IsSupported":false}}}"#).unwrap();
        for config in [
            r#"{"runtimeOptions":{"additionalProbingPaths":[]}}"#,
            r#"{"runtimeOptions":{"configProperties":{"System.StartupHookProvider.IsSupported":true}}}"#,
            r#"{"runtimeOptions":{"configProperties":{"System.Reflection.Metadata.MetadataUpdater.IsSupported":"false"}}}"#,
            r#"{"runtimeOptions":{},"runtimeOptions":{}}"#,
        ] {
            assert!(validate_runtime_config(config.as_bytes()).is_err());
        }
    }
}

#[cfg(test)]
mod parity_and_security_tests {
    use super::*;
    #[test]
    fn canonical_digest_matches_shared_csharp_binary_writer_vector() {
        let vector: Value = serde_json::from_str(include_str!(
            "../tests/data/scratch-receiver-closure-v1.json"
        ))
        .unwrap();
        let members: BTreeMap<_, _> = vector["members"]
            .as_object()
            .unwrap()
            .iter()
            .map(|(name, value)| (name.clone(), value.as_str().unwrap().as_bytes().to_vec()))
            .collect();
        let declared = required_files(&members[&format!("{STEM}.deps.json")]).unwrap();
        assert_eq!(declared, members.keys().cloned().collect());
        validate_runtime_config(&members[&format!("{STEM}.runtimeconfig.json")]).unwrap();
        let mut encoded = Vec::new();
        write_string(&mut encoded, PREFIX);
        for (name, bytes) in members {
            write_member(
                &mut encoded,
                &name,
                bytes.len() as u64,
                Sha256::digest(bytes).into(),
            );
        }
        let actual = Sha256::digest(encoded)
            .iter()
            .map(|byte| format!("{byte:02x}"))
            .collect::<String>();
        assert_eq!(Some(actual.as_str()), vector["canonicalSha256"].as_str());
    }

    fn sealed_descriptor() -> Vec<u8> {
        let owner = crate::windows_protected_path::SYSTEM;
        let mut bytes = vec![0u8; 20];
        bytes[0] = 1;
        bytes[2..4].copy_from_slice(&(SE_SELF_RELATIVE | SE_DACL_PRESENT).to_le_bytes());
        bytes[4..8].copy_from_slice(&20u32.to_le_bytes());
        bytes.extend_from_slice(owner);
        let acl_offset = bytes.len() as u32;
        bytes[16..20].copy_from_slice(&acl_offset.to_le_bytes());
        bytes.extend_from_slice(&[2, 0, 48, 0, 2, 0, 0, 0]);
        for (kind, flags, mask, trustee) in [
            (1, 0, SEAL_MASK, OWNER_RIGHTS),
            (0, 0x13, FILE_ALL_ACCESS, owner),
        ] {
            bytes.extend_from_slice(&[kind, flags, (8 + trustee.len()) as u8, 0]);
            bytes.extend_from_slice(&mask.to_le_bytes());
            bytes.extend_from_slice(trustee);
        }
        bytes
    }

    #[test]
    fn seal_requires_exact_owner_rights_deny_and_private_owner_grant() {
        let owner = crate::windows_protected_path::SYSTEM;
        let valid = sealed_descriptor();
        assert_eq!(accepts_sealed_descriptor(&valid, owner), Some(true));
        for offset in [0, 20, 32, 36, 40, 41, 42, 44, 59, 60, 61, 62, 64, 79] {
            let mut changed = valid.clone();
            changed[offset] ^= 0x40;
            assert_ne!(
                accepts_sealed_descriptor(&changed, owner),
                Some(true),
                "mutation offset {offset}"
            );
        }
        let mut explicit = valid.clone();
        explicit[61] = 3;
        assert_eq!(accepts_sealed_descriptor(&explicit, owner), Some(true));
        let mut unsealed = valid.clone();
        unsealed[36] = 1;
        assert_eq!(accepts_sealed_descriptor(&unsealed, owner), Some(false));
        assert_ne!(accepts_sealed_descriptor(&valid, OWNER_RIGHTS), Some(true));
        for size in 0..valid.len() {
            assert_ne!(accepts_sealed_descriptor(&valid[..size], owner), Some(true));
        }
    }

    #[test]
    fn malformed_json_and_missing_declarations_are_refused() {
        for input in [
            b"".as_slice(),
            b"null",
            b"[]",
            b"{",
            b"{}",
            b"{\"targets\":null}",
            b"{\"targets\":{\"a\":[]}}",
            b"{\"targets\":{\"a\":{\"b\":null}}}",
        ] {
            assert!(required_files(input).is_err());
        }
        for input in [
            b"{}".as_slice(),
            b"{\"runtimeOptions\":null}",
            b"{\"runtimeOptions\":[]}",
            b"{\"runtimeOptions\":{\"configProperties\":null}}",
        ] {
            assert!(validate_runtime_config(input).is_err());
        }
        assert!(required_files(&vec![b' '; MAX_JSON_BYTES as usize + 1]).is_err());
    }
}

fn require_identity(actual: FileIdentity, expected: FileIdentity, volume: u32) -> io::Result<()> {
    if actual.index == 0 || actual != expected || actual.volume != volume {
        return Err(invalid("retained receiver identity changed"));
    }
    Ok(())
}

#[cfg(test)]
mod identity_tests {
    use super::*;
    #[test]
    fn changed_retained_identity_or_volume_is_refused() {
        let expected = FileIdentity {
            volume: 7,
            index: 42,
        };
        require_identity(expected, expected, 7).unwrap();
        for actual in [
            FileIdentity {
                volume: 7,
                index: 43,
            },
            FileIdentity {
                volume: 8,
                index: 42,
            },
            FileIdentity {
                volume: 7,
                index: 0,
            },
        ] {
            assert!(require_identity(actual, expected, 7).is_err());
        }
        assert!(require_identity(expected, expected, 8).is_err());
    }
}

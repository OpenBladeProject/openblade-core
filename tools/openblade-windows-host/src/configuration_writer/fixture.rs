//! Fixed managed-byte fixture for the foreground process harness.

use super::profile::{LEGACY_SCHEMA, ProfilePolicy, ProfileTemplate};
use super::{
    Boundary, CURRENT_SCHEMA, FILE_SHARE_READ, Inspection, NAMES, Outcome, TestStore,
    file_identity, handle_information, pin_directory_with_share, transaction_id,
};
use crate::valid_dev_id;
use serde::Deserialize;
use serde_json::Value;
use std::{
    ffi::OsStr,
    fs::File,
    io::{self, Write},
    os::windows::{
        ffi::OsStrExt,
        fs::MetadataExt,
        io::{AsRawHandle, FromRawHandle},
    },
    path::PathBuf,
    ptr,
};
use windows_sys::{
    Wdk::{
        Foundation::OBJECT_ATTRIBUTES,
        Storage::FileSystem::{
            FILE_CREATE, FILE_DIRECTORY_FILE, FILE_SYNCHRONOUS_IO_NONALERT, NtCreateFile,
        },
    },
    Win32::{
        Foundation::{
            OBJ_CASE_INSENSITIVE, OBJ_DONT_REPARSE, RtlNtStatusToDosError, UNICODE_STRING,
        },
        Storage::FileSystem::{
            FILE_ATTRIBUTE_DIRECTORY, FILE_ATTRIBUTE_REPARSE_POINT, FILE_GENERIC_READ,
            GetDriveTypeW,
        },
        System::IO::IO_STATUS_BLOCK,
        System::{Com::CoTaskMemFree, WindowsProgramming::DRIVE_FIXED},
        UI::Shell::{FOLDERID_LocalAppData, SHGetKnownFolderPath},
    },
};

const FIXTURE: &str = include_str!(
    "../../../../tests/OpenBlade.Service.Tests/WindowsReplay/v1/configuration-aggregate.json"
);

const PROFILE_FIXTURE: &str = include_str!(
    "../../../../tests/OpenBlade.Service.Tests/WindowsReplay/v1/configuration-profile.json"
);

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct MigrationFixture {
    charge_limit_percent: u8,
    legacy_fan_stop: Value,
}

#[derive(Deserialize)]
struct Documents {
    prior: [String; 3],
    committed: [String; 3],
}

pub struct Fixture {
    id: String,
    store: TestStore,
    documents: Documents,
    template: ProfileTemplate,
}

impl Fixture {
    /// This is the only constructor with write authority. An existing root
    /// (including a reparse point) makes the exclusive create fail.
    pub fn create() -> io::Result<Self> {
        let documents: Documents = serde_json::from_str(FIXTURE)?;
        let template = ProfileTemplate::from_managed(&documents.prior)?;
        let (temp, pins) = TestStore::pin_temp(&local_temp()?)?;
        let id = transaction_id()?;
        let root = root(temp, &id)?;
        let parent = pins
            .last()
            .ok_or_else(|| io::Error::other("fixture parent is unpinned"))?;
        let name = root
            .file_name()
            .ok_or_else(|| io::Error::other("fixture name missing"))?;
        let creation_pin = create_relative(parent, name)?;
        if parent.metadata()?.file_attributes() & FILE_ATTRIBUTE_REPARSE_POINT != 0 {
            return Err(io::Error::other("fixture parent became a reparse point"));
        }
        let store = TestStore::initialize(root, pins, Some(creation_pin))?;
        Ok(Self {
            id,
            store,
            documents,
            template,
        })
    }

    pub fn restart_key(&self) -> io::Result<(&str, String)> {
        Ok((&self.id, identity(&self.store)?))
    }

    /// The harness now has the root ID and takes responsibility for teardown.
    pub fn hand_off_cleanup(&mut self) {
        self.store.cleanup_on_drop = false;
    }

    pub fn seed(&self) -> io::Result<u8> {
        self.write(&self.documents.prior, false)
    }

    pub fn bootstrap(&self) -> io::Result<u8> {
        let intent = self.template.encode(ProfilePolicy::default())?;
        self.materialize(&intent, [None; 3])
    }

    pub fn migrate(&self) -> io::Result<u8> {
        let legacy = legacy_documents(&self.template)?;
        let legacy = legacy.each_ref().map(|doc| doc.as_bytes());
        for (name, bytes) in NAMES.into_iter().zip(legacy) {
            self.store.create_durable(name, bytes)?;
        }

        let (inspection, prior) = self.store.inspect_snapshot()?;
        let prior = prior.each_ref().map(Option::as_deref);
        if prior != legacy.map(Some)
            || inspection
                != (Inspection::CommittedEnvelope {
                    schema: LEGACY_SCHEMA,
                })
        {
            return Err(io::Error::other(format!(
                "fixture migration refused: {inspection:?}"
            )));
        }
        let policy = self.template.decode(legacy)?;
        let intent = self.template.encode(policy)?;
        self.materialize(&intent, prior)
    }

    fn materialize(&self, intent: &[String; 3], expected: [Option<&[u8]>; 3]) -> io::Result<u8> {
        let bytes = intent.each_ref().map(|doc| doc.as_bytes());
        self.template.decode(bytes)?;
        let report = self.store.commit_from(bytes, Some(expected), |_| Ok(()));
        self.verify_write(intent, false, &report)
    }

    pub fn update(&self, interrupt: bool) -> io::Result<u8> {
        self.write(&self.documents.committed, interrupt)
    }

    fn write(&self, intent: &[String; 3], interrupt: bool) -> io::Result<u8> {
        let report = self
            .store
            .commit(intent.each_ref().map(|doc| doc.as_bytes()), |at| {
                if interrupt && matches!(at, Boundary::AfterReplace(0) | Boundary::BeforeRestore(0))
                {
                    return Err(io::Error::other("injected developer interruption"));
                }
                Ok(())
            });
        self.verify_write(intent, interrupt, &report)
    }

    fn verify_write(
        &self,
        intent: &[String; 3],
        interrupt: bool,
        report: &super::Report,
    ) -> io::Result<u8> {
        match &report.outcome {
            Outcome::Committed if !interrupt => {
                // As in the managed writer, failure to remove a backup does
                // not invalidate a verified commit with no recovery record.
                for error in &report.artifact_errors {
                    let _ = writeln!(io::stderr().lock(), "fixture artifact cleanup: {error}");
                }
                load(&self.store, &self.template, &[intent])
            }
            Outcome::RecoveryRequired {
                cause,
                resolution: Some(resolution),
            } if interrupt => Err(io::Error::other(format!(
                "fixture interrupted: {cause}; {resolution}"
            ))),
            Outcome::InvalidIntent(inspection) | Outcome::Refused(inspection) => Err(
                io::Error::other(format!("fixture transaction refused: {inspection:?}")),
            ),
            Outcome::Unchanged(error) | Outcome::Restored(error) => Err(io::Error::other(format!(
                "fixture transaction left the prior configuration in place: {error}"
            ))),
            _ => Err(io::Error::other(format!(
                "fixture transaction failed: {report:?}"
            ))),
        }
    }
}

/// Reopening is read-only, even with a valid identity and known committed data.
/// No path, file names, or payload bytes are supplied by the caller.
pub fn restart(id: &str, expected: &str) -> io::Result<u8> {
    let (temp, mut pins) = TestStore::pin_temp(&local_temp()?)?;
    let root = root(temp, id)?;
    pins.push(pin_directory_with_share(&root, FILE_SHARE_READ)?);
    let store = TestStore {
        root,
        pins,
        cleanup_on_drop: false,
        held_lock: None,
    };
    if identity(&store)? != expected {
        return Err(io::Error::other("fixture root identity changed"));
    }
    let documents: Documents = serde_json::from_str(FIXTURE)?;
    let template = ProfileTemplate::from_managed(&documents.prior)?;
    let bootstrap = template.encode(ProfilePolicy::default())?;
    let legacy = legacy_documents(&template)?;
    let migrated =
        template.encode(template.decode(legacy.each_ref().map(|doc| doc.as_bytes()))?)?;
    load(
        &store,
        &template,
        &[
            &documents.prior,
            &documents.committed,
            &bootstrap,
            &migrated,
        ],
    )
}

fn legacy_documents(template: &ProfileTemplate) -> io::Result<[String; 3]> {
    let fixture: MigrationFixture = serde_json::from_str(PROFILE_FIXTURE)?;
    template.documents(
        LEGACY_SCHEMA,
        fixture.charge_limit_percent,
        fixture.legacy_fan_stop,
    )
}

fn load(store: &TestStore, template: &ProfileTemplate, allowed: &[&[String; 3]]) -> io::Result<u8> {
    let (inspection, bytes) = store.inspect_snapshot()?;
    if inspection
        != (Inspection::CommittedEnvelope {
            schema: CURRENT_SCHEMA,
        })
    {
        return Err(io::Error::other(format!(
            "fixture startup refused: {inspection:?}"
        )));
    }
    let [Some(profile), Some(preferences), Some(marker)] = bytes.each_ref().map(Option::as_deref)
    else {
        return Err(io::Error::other("fixture documents missing"));
    };
    let docs = [profile, preferences, marker];
    let policy = template.decode(docs)?;
    if !allowed
        .iter()
        .any(|allowed| allowed.each_ref().map(|doc| doc.as_bytes()) == docs)
    {
        return Err(io::Error::other(
            "configuration outside the managed fixture",
        ));
    }
    Ok(policy.charge_limit)
}

fn root(temp: PathBuf, id: &str) -> io::Result<PathBuf> {
    if !valid_dev_id(id) {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "invalid fixture ID",
        ));
    }
    Ok(temp.join(format!("openblade-durable-host-{id}")))
}

fn identity(store: &TestStore) -> io::Result<String> {
    let directory = store
        .pins
        .last()
        .ok_or_else(|| io::Error::other("fixture root is unpinned"))?;
    let id = file_identity(&handle_information(directory)?);
    Ok(format!("{:08x}{:016x}", id.volume, id.index))
}

// Create and pin one child relative to the already opened Temp directory.
// There is no absolute path resolution or write-sharing restriction on Temp.
fn create_relative(parent: &File, name: &OsStr) -> io::Result<File> {
    let mut wide: Vec<u16> = name.encode_wide().collect();
    let length = u16::try_from(wide.len() * size_of::<u16>()).map_err(io::Error::other)?;
    let mut name = UNICODE_STRING {
        Length: length,
        MaximumLength: length,
        Buffer: wide.as_mut_ptr(),
    };
    let attributes = OBJECT_ATTRIBUTES {
        Length: size_of::<OBJECT_ATTRIBUTES>() as u32,
        RootDirectory: parent.as_raw_handle(),
        ObjectName: &mut name,
        Attributes: OBJ_CASE_INSENSITIVE | OBJ_DONT_REPARSE,
        ..Default::default()
    };
    let mut handle = ptr::null_mut();
    let mut io_status = IO_STATUS_BLOCK::default();
    // SAFETY: The parent owns a live directory handle. The name and structures
    // stay valid for the synchronous call. FILE_CREATE refuses an existing child.
    let status = unsafe {
        NtCreateFile(
            &mut handle,
            FILE_GENERIC_READ,
            &attributes,
            &mut io_status,
            ptr::null(),
            FILE_ATTRIBUTE_DIRECTORY,
            FILE_SHARE_READ,
            FILE_CREATE,
            FILE_DIRECTORY_FILE | FILE_SYNCHRONOUS_IO_NONALERT,
            ptr::null(),
            0,
        )
    };
    if status < 0 {
        // SAFETY: The returned NTSTATUS is the sole input.
        return Err(io::Error::from_raw_os_error(
            unsafe { RtlNtStatusToDosError(status) } as i32,
        ));
    }
    // SAFETY: Success transferred ownership of the new handle.
    Ok(unsafe { File::from_raw_handle(handle) })
}

fn local_temp() -> io::Result<PathBuf> {
    use std::{ffi::OsString, os::windows::ffi::OsStringExt, slice};

    // Resolve the Windows known folder, independently of TEMP, TMP, CWD and
    // command-line arguments. The shared store pins each plain path component.
    let mut path = ptr::null_mut();
    // SAFETY: The known folder ID and output pointer are valid; a null token
    // requests the current user's known folder.
    let result =
        unsafe { SHGetKnownFolderPath(&FOLDERID_LocalAppData, 0, ptr::null_mut(), &mut path) };
    let folder = if result >= 0 && !path.is_null() {
        // SAFETY: On success the API returns a NUL-terminated UTF-16 string.
        let wide = unsafe {
            let mut end = path;
            while *end != 0 {
                end = end.add(1);
            }
            slice::from_raw_parts(path, end.offset_from(path) as usize)
        };
        Ok(PathBuf::from(OsString::from_wide(wide)).join("Temp"))
    } else {
        Err(io::Error::other(
            "could not resolve the local fixture directory",
        ))
    };
    // SAFETY: The API allocates the output with the COM task allocator, and
    // the allocator accepts null.
    unsafe { CoTaskMemFree(path.cast()) };
    let folder = folder?;
    let drive = folder
        .ancestors()
        .filter(|path| path.has_root())
        .last()
        .ok_or_else(|| io::Error::other("fixture directory has no drive root"))?;
    let drive: Vec<u16> = drive.as_os_str().encode_wide().chain([0]).collect();
    // SAFETY: drive is a NUL-terminated UTF-16 drive-root path.
    if unsafe { GetDriveTypeW(drive.as_ptr()) } != DRIVE_FIXED {
        return Err(io::Error::other(
            "fixture directory requires a local fixed drive",
        ));
    }
    Ok(folder)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn typed_profile_settings_distinguish_missing_null_and_unknown_values() {
        use serde_json::json;

        let documents: Documents = serde_json::from_str(FIXTURE).unwrap();
        let template = ProfileTemplate::from_managed(&documents.prior).unwrap();
        let decode = |schema, limit, settings| {
            let docs = template.documents(schema, limit, settings).unwrap();
            template.decode(docs.each_ref().map(|doc| doc.as_bytes()))
        };

        let legacy = legacy_documents(&template).unwrap();
        let effective = template
            .decode(legacy.each_ref().map(|doc| doc.as_bytes()))
            .unwrap();
        let spec: Value = serde_json::from_str(PROFILE_FIXTURE).unwrap();
        assert_eq!(
            serde_json::to_value(effective.fan_stop).unwrap(),
            spec["currentFanStop"]
        );
        assert_eq!(effective.charge_limit, 77);
        assert_eq!(
            decode(LEGACY_SCHEMA, 80, json!({})).unwrap(),
            ProfilePolicy::default()
        );
        assert_eq!(
            decode(
                CURRENT_SCHEMA,
                80,
                json!({
                    "cpuImmediateCutoffCelsius": "65",
                    "gpuImmediateCutoffCelsius": 65
                })
            )
            .unwrap(),
            ProfilePolicy::default()
        );

        for (schema, limit, settings) in [
            (23, 80, json!({})),
            (26, 80, json!({})),
            (LEGACY_SCHEMA, 49, json!({})),
            (LEGACY_SCHEMA, 100, json!({})),
            (LEGACY_SCHEMA, 80, Value::Null),
            (LEGACY_SCHEMA, 80, json!({"endMode": "1"})),
            (LEGACY_SCHEMA, 80, json!({"endMode": 2})),
            (
                LEGACY_SCHEMA,
                80,
                json!({"automaticEnabled": true, "automaticCpuBelowCelsius": 55}),
            ),
            (LEGACY_SCHEMA, 80, json!({"secondsAboveLimit": null})),
            (LEGACY_SCHEMA, 80, json!({"secondsBelowLimits": 3601})),
            (LEGACY_SCHEMA, 80, json!({"gpuLimitCelsius": 2147483647})),
            (LEGACY_SCHEMA, 80, json!({"unknown": false})),
            (CURRENT_SCHEMA, 80, json!({"cpuImmediateCutoffCelsius": 65})),
            (
                CURRENT_SCHEMA,
                80,
                json!({"cpuImmediateCutoffCelsius": null, "gpuImmediateCutoffCelsius": 65}),
            ),
            (
                CURRENT_SCHEMA,
                80,
                json!({"cpuImmediateCutoffCelsius": 54, "gpuImmediateCutoffCelsius": 65}),
            ),
        ] {
            assert!(
                decode(schema, limit, settings.clone()).is_err(),
                "{schema} {limit} {settings}"
            );
        }
    }

    #[test]
    fn a_different_valid_legacy_preimage_never_reaches_the_writer() {
        let fixture = Fixture::create().unwrap();
        let legacy = legacy_documents(&fixture.template).unwrap();
        let mut spec: MigrationFixture = serde_json::from_str(PROFILE_FIXTURE).unwrap();
        spec.charge_limit_percent += 1;
        let different = fixture
            .template
            .documents(
                LEGACY_SCHEMA,
                spec.charge_limit_percent,
                spec.legacy_fan_stop,
            )
            .unwrap();
        for (name, doc) in NAMES.into_iter().zip(&different) {
            fixture.store.create_durable(name, doc.as_bytes()).unwrap();
        }
        let policy = fixture
            .template
            .decode(legacy.each_ref().map(|doc| doc.as_bytes()))
            .unwrap();
        let intent = fixture.template.encode(policy).unwrap();
        let error = fixture
            .materialize(&intent, legacy.each_ref().map(|doc| Some(doc.as_bytes())))
            .unwrap_err();
        assert!(error.to_string().contains("preimage changed"), "{error}");
        let (inspection, actual) = fixture.store.inspect_snapshot().unwrap();
        assert_eq!(
            inspection,
            Inspection::CommittedEnvelope {
                schema: LEGACY_SCHEMA
            }
        );
        assert_eq!(actual, different.map(|doc| Some(doc.into_bytes())));
        assert_eq!(std::fs::read_dir(&fixture.store.root).unwrap().count(), 4);
    }

    #[test]
    fn an_unannounced_fixture_is_removed_when_the_owner_exits() {
        let fixture = Fixture::create().unwrap();
        let root = fixture.store.root.clone();
        assert!(root.is_dir());
        drop(fixture);
        assert!(!root.exists());
    }
}

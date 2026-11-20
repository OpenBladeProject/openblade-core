//! Fixed managed-byte fixture for the foreground process harness.

use super::profile::{LEGACY_SCHEMA, ProfilePolicy, ProfileTemplate, commit_marker};
use super::{
    Boundary, CURRENT_SCHEMA, ExpectedPreimage, FILE_SHARE_READ, Inspection, NAMES, Outcome,
    ProtectedWriter, RecordIdentities, TestStore, file_identity, handle_information, inspect_bytes,
    open_relative, pin_directory_with_share, transaction_id,
};
use crate::valid_dev_id;
use serde::Deserialize;
use serde_json::Value;
use std::{
    ffi::OsStr,
    fs::File,
    io::{self, Write},
    os::windows::{ffi::OsStrExt, fs::MetadataExt},
    path::{Path, PathBuf},
    ptr,
    sync::Arc,
};
use windows_sys::{
    Wdk::Storage::FileSystem::{FILE_CREATE, FILE_DIRECTORY_FILE},
    Win32::{
        Storage::FileSystem::{FILE_ATTRIBUTE_REPARSE_POINT, FILE_GENERIC_READ, GetDriveTypeW},
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

#[cfg(feature = "vm-scm-fixture")]
pub const VM_DATA_ROOT: &str = r"C:\OpenBladeRustPhase5jVm\data";

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
    pub fn create(seed_legacy: bool) -> io::Result<Self> {
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
        let fixture = Self {
            id,
            store,
            documents,
            template,
        };
        if seed_legacy {
            // Keep cleanup ownership until all synthetic inputs are ready.
            for (name, document) in NAMES.into_iter().zip(legacy_documents(&fixture.template)?) {
                fixture.store.create_durable(name, document.as_bytes())?;
            }
        }
        Ok(fixture)
    }

    pub fn restart_key(&self) -> io::Result<(&str, String)> {
        Ok((&self.id, identity(&self.store)?))
    }

    /// The harness now has the root ID and takes responsibility for teardown.
    pub fn hand_off_cleanup(&mut self) {
        self.store.cleanup_on_drop = false;
    }

    pub fn seed(&self) -> io::Result<u8> {
        self.write(
            &self.documents.prior,
            ExpectedPreimage::Fixture([None; 3]),
            false,
        )
    }

    pub fn bootstrap(&self) -> io::Result<u8> {
        let intent = self.template.encode(ProfilePolicy::default())?;
        self.write(&intent, ExpectedPreimage::Fixture([None; 3]), false)
    }

    pub fn migrate(&self) -> io::Result<u8> {
        self.migrate_documents()
            .map(|(policy, _)| policy.charge_limit)
    }

    fn migrate_documents(&self) -> io::Result<(ProfilePolicy, [String; 3])> {
        let legacy = legacy_documents(&self.template)?;
        let legacy = document_bytes(&legacy);

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
        let preimage = self.template.validate_legacy(legacy)?;
        let policy = preimage.policy;
        let intent = self.template.encode(policy)?;
        self.write(&intent, ExpectedPreimage::Migration(preimage), false)?;
        Ok((policy, intent))
    }

    pub fn compose_battery(self, interrupt: bool) -> io::Result<(Value, BatteryProfileStore)> {
        let (policy, verified) = self.migrate_documents()?;
        let profiles = profiles_from_document(verified[0].as_bytes())?;
        Ok((
            profiles,
            BatteryProfileStore {
                store: self.store,
                template: self.template,
                prior: verified.map(String::into_bytes),
                identities: None,
                policy,
                interrupt,
            },
        ))
    }

    pub fn update(&self, interrupt: bool) -> io::Result<u8> {
        let prior = ExpectedPreimage::Fixture(document_bytes(&self.documents.prior).map(Some));
        self.write(&self.documents.committed, prior, interrupt)
    }

    fn write(
        &self,
        intent: &[String; 3],
        expected: ExpectedPreimage<'_>,
        interrupt: bool,
    ) -> io::Result<u8> {
        write(
            &self.store,
            &self.template,
            intent,
            expected,
            interrupt,
            None,
        )
    }
}

fn write(
    store: &TestStore,
    template: &ProfileTemplate,
    intent: &[String; 3],
    expected: ExpectedPreimage<'_>,
    interrupt: bool,
    tracked: Option<&mut RecordIdentities>,
) -> io::Result<u8> {
    let bytes = document_bytes(intent);
    template.decode_verified(bytes, inspect_bytes(bytes.map(Some)))?;
    let report = store.commit_from(bytes, expected, |at| {
        if interrupt && matches!(at, Boundary::AfterReplace(0) | Boundary::BeforeRestore(0)) {
            return Err(io::Error::other("injected developer interruption"));
        }
        Ok(())
    });
    match &report.outcome {
        Outcome::Committed if !interrupt => {
            // As in the managed writer, failure to remove a backup does
            // not invalidate a verified commit with no recovery record.
            for error in &report.artifact_errors {
                let _ = writeln!(io::stderr().lock(), "fixture artifact cleanup: {error}");
            }
            let (inspection, actual, identities) = store.inspect_with_identities()?;
            let actual = current_documents(inspection, &actual)?;
            let policy = template.decode_verified(actual, inspection)?;
            if actual != bytes || identities != report.identities {
                return Err(io::Error::other("configuration commit readback changed"));
            }
            if let Some(tracked) = tracked {
                *tracked = identities;
            }
            Ok(policy.charge_limit)
        }
        Outcome::RecoveryRequired {
            cause,
            resolution: Some(resolution),
        } if interrupt => Err(io::Error::other(format!(
            "fixture interrupted: {cause}; {resolution}"
        ))),
        Outcome::InvalidIntent(inspection) | Outcome::Refused(inspection) => Err(io::Error::other(
            format!("fixture transaction refused: {inspection:?}"),
        )),
        Outcome::Unchanged(error) | Outcome::Restored(error) => {
            if matches!(report.outcome, Outcome::Restored(_))
                && let Some(tracked) = tracked
            {
                // Rollback installed independently verified copies of the
                // prior bytes. Verify those identities again in verify_prior.
                *tracked = report.identities;
            }
            Err(io::Error::other(format!(
                "fixture transaction left the prior configuration in place: {error}"
            )))
        }
        _ => Err(io::Error::other(format!(
            "fixture transaction failed: {report:?}"
        ))),
    }
}

/// Owns the admitted aggregate and the private writer lifetime.
pub struct BatteryProfileStore {
    store: TestStore,
    template: ProfileTemplate,
    prior: [Vec<u8>; 3],
    identities: Option<RecordIdentities>,
    policy: ProfilePolicy,
    interrupt: bool,
}

impl BatteryProfileStore {
    /// Developer-only root, deliberately separate from the installed data path.
    pub fn open_protected(root: &Path) -> io::Result<(Value, Self)> {
        let name = root
            .file_name()
            .and_then(OsStr::to_str)
            .and_then(|name| name.strip_prefix("OpenBlade-Phase4j-"))
            .filter(|id| valid_dev_id(id));
        if name.is_none() || root.components().count() != 3 {
            return Err(io::Error::other(
                "expected a dedicated Phase 4j fixture at a drive root",
            ));
        }

        let storage = super::protected::ProtectedStorage::open(root)?;
        Self::open_protected_store(root, storage)
    }

    #[cfg(feature = "vm-scm-fixture")]
    pub fn open_vm_protected(
        storage: super::protected::ProtectedStorage,
    ) -> io::Result<(Value, Self)> {
        Self::open_protected_store(Path::new(VM_DATA_ROOT), storage)
    }

    fn open_protected_store(
        root: &Path,
        storage: super::protected::ProtectedStorage,
    ) -> io::Result<(Value, Self)> {
        let lease = storage.acquire_writer()?;
        let store = TestStore {
            root: root.to_owned(),
            pins: Vec::new(),
            cleanup_on_drop: false,
            held_lock: None,
            protected: Some(Arc::new(ProtectedWriter {
                storage,
                _lease: lease,
            })),
        };
        let documents: Documents = serde_json::from_str(FIXTURE)?;
        let template = ProfileTemplate::from_managed(&documents.prior)?;
        let (inspection, prior, identities) = store.inspect_with_identities()?;
        let docs = current_documents(inspection, &prior)?;
        let policy = template.decode_verified(docs, inspection)?;
        let profiles = profiles_from_document(docs[0])?;
        Ok((
            profiles,
            Self {
                store,
                template,
                prior: docs.map(<[u8]>::to_vec),
                identities: Some(identities),
                policy,
                interrupt: false,
            },
        ))
    }

    pub fn save(&mut self, charge_limit: u8) -> io::Result<()> {
        let policy = ProfilePolicy {
            charge_limit,
            ..self.policy
        };
        let mut intent = self.template.encode(policy)?;
        let prior = self.prior.each_ref().map(Vec::as_slice);
        let expected = match self.identities {
            Some(identities) => {
                preserve_preferences(&mut intent, prior[1])?;
                ExpectedPreimage::Protected(prior, identities)
            }
            None => ExpectedPreimage::Fixture(prior.map(Some)),
        };
        write(
            &self.store,
            &self.template,
            &intent,
            expected,
            self.interrupt,
            self.identities.as_mut(),
        )?;
        self.prior = intent.map(String::into_bytes);
        self.policy = policy;
        Ok(())
    }

    pub fn verify_prior(&self) -> io::Result<()> {
        let (inspection, bytes, identities) = self.store.inspect_with_identities()?;
        if current_documents(inspection, &bytes)? != self.prior.each_ref().map(Vec::as_slice)
            || self.identities.is_some_and(|prior| prior != identities)
        {
            return Err(io::Error::other(
                "configuration no longer matches the admitted preimage",
            ));
        }
        Ok(())
    }
}

fn preserve_preferences(intent: &mut [String; 3], prior: &[u8]) -> io::Result<()> {
    // A protected battery-only edit leaves the independently admitted
    // managed preferences intact, including their original byte order.
    intent[1] = std::str::from_utf8(prior)
        .map_err(io::Error::other)?
        .to_owned();
    intent[2] = commit_marker(CURRENT_SCHEMA, intent[0].as_bytes(), prior)?;
    Ok(())
}

fn current_documents(
    inspection: Inspection,
    bytes: &[Option<Vec<u8>>; 3],
) -> io::Result<[&[u8]; 3]> {
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
    Ok([profile, preferences, marker])
}

fn migrated_policy(template: &ProfileTemplate) -> io::Result<ProfilePolicy> {
    let legacy = legacy_documents(template)?;
    Ok(template.validate_legacy(document_bytes(&legacy))?.policy)
}

fn profiles_from_document(profile: &[u8]) -> io::Result<Value> {
    let envelope: Value = serde_json::from_slice(profile)?;
    Ok(envelope["profiles"].clone())
}

pub fn restart_battery_profile(id: &str, expected: &str) -> io::Result<Value> {
    let (store, _, template) = open_restart(id, expected)?;
    let mut profiles = Value::Null;
    let baseline = migrated_policy(&template)?;
    load(&store, &template, |actual, decoded| {
        let policy = ProfilePolicy {
            charge_limit: decoded.charge_limit,
            ..baseline
        };
        if document_bytes(&template.encode(policy)?) != actual {
            return Ok(false);
        }
        profiles = profiles_from_document(actual[0])?;
        Ok(true)
    })?;
    Ok(profiles)
}

/// Reopening is read-only, even with a valid identity and known committed data.
/// No path, file names, or payload bytes are supplied by the caller.
fn open_restart(id: &str, expected: &str) -> io::Result<(TestStore, Documents, ProfileTemplate)> {
    let (temp, mut pins) = TestStore::pin_temp(&local_temp()?)?;
    let root = root(temp, id)?;
    pins.push(pin_directory_with_share(&root, FILE_SHARE_READ)?);
    let store = TestStore {
        root,
        pins,
        cleanup_on_drop: false,
        held_lock: None,
        protected: None,
    };
    if identity(&store)? != expected {
        return Err(io::Error::other("fixture root identity changed"));
    }
    let documents: Documents = serde_json::from_str(FIXTURE)?;
    let template = ProfileTemplate::from_managed(&documents.prior)?;
    Ok((store, documents, template))
}

pub fn restart(id: &str, expected: &str) -> io::Result<u8> {
    let (store, documents, template) = open_restart(id, expected)?;
    load(&store, &template, |actual, _| {
        if [&documents.prior, &documents.committed]
            .into_iter()
            .any(|allowed| document_bytes(allowed) == actual)
        {
            return Ok(true);
        }
        let bootstrap = template.encode(ProfilePolicy::default())?;
        if document_bytes(&bootstrap) == actual {
            return Ok(true);
        }
        let legacy = legacy_documents(&template)?;
        let migrated =
            template.encode(template.validate_legacy(document_bytes(&legacy))?.policy)?;
        Ok(document_bytes(&migrated) == actual)
    })
}

fn document_bytes(documents: &[String; 3]) -> [&[u8]; 3] {
    documents.each_ref().map(|doc| doc.as_bytes())
}

fn legacy_documents(template: &ProfileTemplate) -> io::Result<[String; 3]> {
    let fixture: MigrationFixture = serde_json::from_str(PROFILE_FIXTURE)?;
    template.documents(
        LEGACY_SCHEMA,
        fixture.charge_limit_percent,
        fixture.legacy_fan_stop,
    )
}

fn load(
    store: &TestStore,
    template: &ProfileTemplate,
    allowed: impl FnOnce([&[u8]; 3], ProfilePolicy) -> io::Result<bool>,
) -> io::Result<u8> {
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
    let policy = template.decode_verified(docs, inspection)?;
    if !allowed(docs, policy)? {
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
    open_relative(
        parent,
        name,
        FILE_GENERIC_READ,
        FILE_SHARE_READ,
        FILE_CREATE,
        FILE_DIRECTORY_FILE,
    )
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
    #[test]
    fn protected_battery_edit_retains_managed_preferences_bytes() {
        let docs: Documents = serde_json::from_str(FIXTURE).unwrap();
        let template = ProfileTemplate::from_managed(&docs.prior).unwrap();
        let mut policy = template
            .decode_verified(
                document_bytes(&docs.prior),
                Inspection::CommittedEnvelope {
                    schema: CURRENT_SCHEMA,
                },
            )
            .unwrap();
        policy.charge_limit = 75;
        let mut intent = template.encode(policy).unwrap();
        preserve_preferences(&mut intent, docs.prior[1].as_bytes()).unwrap();

        assert_eq!(intent[1], docs.prior[1]);
        let bytes = document_bytes(&intent);
        let verified = template
            .decode_verified(bytes, inspect_bytes(bytes.map(Some)))
            .unwrap();
        assert_eq!(verified, policy);
    }

    use super::*;

    // Managed ProfileStore and BatteryConfigurationStore read every schema from
    // 24 to 27, derive Fan Stop cutoffs only below 25, and reject a non-false
    // keyboard-effect member below 26 and a non-null zone control below 27.
    #[test]
    fn older_schemas_decode_with_managed_lighting_rejections() {
        use serde_json::json;

        let documents: Documents = serde_json::from_str(FIXTURE).unwrap();
        let template = ProfileTemplate::from_managed(&documents.prior).unwrap();
        let current = template
            .decode_verified(
                document_bytes(&documents.prior),
                Inspection::CommittedEnvelope {
                    schema: CURRENT_SCHEMA,
                },
            )
            .unwrap();
        let fan_stop = serde_json::to_value(current.fan_stop).unwrap();
        let decode = |schema: i32, edit: &dyn Fn(&mut Value)| {
            let docs = template
                .documents(schema, current.charge_limit, fan_stop.clone())
                .unwrap();
            let mut profile: Value = serde_json::from_str(&docs[0]).unwrap();
            edit(&mut profile);
            let profile = serde_json::to_string_pretty(&profile).unwrap();
            let marker = commit_marker(schema, profile.as_bytes(), docs[1].as_bytes()).unwrap();
            let bytes = [profile.as_bytes(), docs[1].as_bytes(), marker.as_bytes()];
            template.decode_verified(bytes, inspect_bytes(bytes.map(Some)))
        };
        for schema in [25, 26, CURRENT_SCHEMA] {
            assert_eq!(decode(schema, &|_| {}).unwrap(), current, "{schema}");
        }
        let kind = |result: io::Result<ProfilePolicy>| result.unwrap_err().kind();
        let zone = |profile: &mut Value| {
            profile["profiles"]["battery"]["lighting"] =
                json!({"TouchpadStatusIndicatorEnabled": false});
        };
        let keyboard = |profile: &mut Value| {
            profile["profiles"]["battery"]["lighting"] = json!({"controlsKeyboardEffect": null});
        };
        let keyboard_false = |profile: &mut Value| {
            profile["profiles"]["battery"]["lighting"] = json!({"controlsKeyboardEffect": false});
        };
        for schema in [24, 25, 26] {
            assert_eq!(
                kind(decode(schema, &zone)),
                io::ErrorKind::InvalidData,
                "{schema}"
            );
        }
        for schema in [24, 25] {
            assert_eq!(
                kind(decode(schema, &keyboard)),
                io::ErrorKind::InvalidData,
                "{schema}"
            );
        }
        // Admitted members stay outside the scoped fixture rather than invalid.
        assert_eq!(kind(decode(26, &keyboard)), io::ErrorKind::Other);
        assert_eq!(kind(decode(CURRENT_SCHEMA, &zone)), io::ErrorKind::Other);
        assert_eq!(kind(decode(25, &keyboard_false)), io::ErrorKind::Other);
    }

    #[test]
    fn typed_profile_settings_distinguish_missing_null_and_unknown_values() {
        use serde_json::json;

        let documents: Documents = serde_json::from_str(FIXTURE).unwrap();
        let template = ProfileTemplate::from_managed(&documents.prior).unwrap();
        let decode = |schema, limit, settings| {
            let docs = template.documents(schema, limit, settings).unwrap();
            let bytes = document_bytes(&docs);
            template.decode_verified(bytes, inspect_bytes(bytes.map(Some)))
        };

        let legacy = legacy_documents(&template).unwrap();
        let effective = template
            .validate_legacy(document_bytes(&legacy))
            .unwrap()
            .policy;
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
        let fixture = Fixture::create(false).unwrap();
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
        let preimage = fixture
            .template
            .validate_legacy(document_bytes(&legacy))
            .unwrap();
        let intent = fixture.template.encode(preimage.policy).unwrap();
        let unvalidated = fixture.store.commit_from(
            document_bytes(&intent),
            ExpectedPreimage::Fixture(document_bytes(&different).map(Some)),
            |_| panic!("an exact legacy preimage still needs typed migration admission"),
        );
        assert!(matches!(
            unvalidated.outcome,
            Outcome::Refused(Inspection::CommittedEnvelope {
                schema: LEGACY_SCHEMA,
            })
        ));
        let error = fixture
            .write(&intent, ExpectedPreimage::Migration(preimage), false)
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
    fn durable_update_preserves_an_integrity_valid_but_damaged_profile() {
        let fixture = Fixture::create(false).unwrap();
        let damaged = fixture
            .template
            .documents(
                CURRENT_SCHEMA,
                80,
                serde_json::json!({"cpuImmediateCutoffCelsius": null}),
            )
            .unwrap();
        for (name, doc) in NAMES.into_iter().zip(&damaged) {
            fixture.store.create_durable(name, doc.as_bytes()).unwrap();
        }
        let error = fixture.update(false).unwrap_err();
        assert!(error.to_string().contains("preimage changed"), "{error}");
        let (inspection, actual) = fixture.store.inspect_snapshot().unwrap();
        assert_eq!(
            inspection,
            Inspection::CommittedEnvelope {
                schema: CURRENT_SCHEMA
            }
        );
        assert_eq!(actual, damaged.map(|doc| Some(doc.into_bytes())));
        assert_eq!(std::fs::read_dir(&fixture.store.root).unwrap().count(), 4);
    }

    #[test]
    fn rejected_battery_profile_save_leaves_the_exact_prior_verified() {
        let fixture = Fixture::create(true).unwrap();
        let (_, mut store) = fixture.compose_battery(false).unwrap();
        assert!(store.save(0).is_err());
        store.verify_prior().unwrap();
        store.save(75).unwrap();
        store.verify_prior().unwrap();
    }

    #[test]
    fn an_unannounced_fixture_is_removed_when_the_owner_exits() {
        let fixture = Fixture::create(false).unwrap();
        let root = fixture.store.root.clone();
        assert!(root.is_dir());
        drop(fixture);
        assert!(!root.exists());
    }
}

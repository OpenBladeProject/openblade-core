//! Managed's HyperBoost ownership journal, read-only. The convergence loop
//! and owned HyperBoost (`hyperboost`) both read it: managed owns its
//! recovery (`ReconcileUnexpectedExitAsync`), and native writes nothing on
//! its behalf, so native refuses a write while the journal is pending.
use openblade_domain::{profile_set::FanMode, thermal_convergence as ledger};
use serde_json::Value;
use std::{io, sync::Arc};

/// Reads managed's HyperBoost ownership journal: its bytes, `None` when the
/// file is absent.
pub(in super::super) type Journal = Arc<dyn Fn() -> io::Result<Option<Vec<u8>>> + Send + Sync>;

/// Managed `HyperBoostOwnershipJournalState.Relinquished`.
pub(super) const RELINQUISHED: i64 = 3;
/// Managed `PerformancePreset.Custom` and its last value, `Creator`.
const CUSTOM_PRESET: i64 = 5;
const PRESETS: i64 = 9;

/// Managed `IsSafeMaintainHyperBoostJournalBaseline` over the journal's
/// bytes: absent, or a document of a schema managed recognizes, with the
/// members that schema requires, that managed's `IsWellFormed` admits and
/// whose state is Relinquished. Members are read as managed writes them
/// (camelCase, numeric enums). Settings records inside the restore intent
/// are checked for presence and kind only; a Relinquished journal restores
/// nothing.
pub(super) fn journal_safe(bytes: Option<&[u8]>) -> bool {
    let Some(bytes) = bytes else {
        return true;
    };
    let Ok(Value::Object(document)) = serde_json::from_slice::<Value>(bytes) else {
        return false;
    };
    let member = |name: &str| document.get(name).filter(|value| !value.is_null());
    let int = |name: &str| member(name).and_then(Value::as_i64);
    let Some(schema) = int("schemaVersion") else {
        return false;
    };
    let recognized = match schema {
        1 => member("requiresCoolingPad").is_none(),
        2 => member("requiresCoolingPad").is_some(),
        3..=8 => {
            member("requiresCoolingPad").is_some()
                && member("restorePerformance").is_some()
                && member("restoreFanControl").is_some()
        }
        _ => false,
    };
    // Managed `HyperBoostRestoreIntent`: a legacy schema restores its base
    // mode with Automatic fans.
    let (performance, custom, gpu, fan_mode) = if schema >= 3 {
        (
            int("restorePerformance"),
            member("restoreCustomPerformance"),
            member("restoreGpuClockTuning"),
            member("restoreFanControl")
                .and_then(|control| control.get("mode"))
                .and_then(Value::as_i64),
        )
    } else {
        (
            int("baseMode"),
            None,
            None,
            Some(FanMode::AUTOMATIC.0.into()),
        )
    };
    let restore = performance.is_some_and(|preset| (0..=PRESETS).contains(&preset))
        && (performance == Some(CUSTOM_PRESET)) == custom.is_some_and(Value::is_object)
        && custom.is_none_or(Value::is_object)
        && gpu.is_none_or(Value::is_object)
        && fan_mode.is_some_and(|mode| (0..=FanMode::STOP.0.into()).contains(&mode));
    let base = match int("baseMode") {
        Some(0..=2) => true,
        Some(CUSTOM_PRESET) => custom.is_some(),
        _ => false,
    };
    // Members of later schemas: a power source the restore is bound to, no
    // retry, a transition ID that is not empty and, outside RestorePending,
    // no recovery baseline.
    let power = schema != 8 || matches!(int("restorePowerSource"), Some(0..=2));
    // Managed reads each only from the schema that added it.
    let since = |added: i64, name: &str| (schema >= added).then(|| member(name)).flatten();
    let retry = [(5, "restoreRetryDisposition"), (6, "restoreRetryTarget")]
        .into_iter()
        .all(|(added, name)| since(added, name).is_none_or(|value| value.as_i64() == Some(0)));
    let transition = since(7, "transitionId").is_none_or(|id| {
        id.as_str()
            .and_then(ledger::Guid::parse)
            .is_some_and(|id| id != ledger::Guid::EMPTY)
    });
    let updated = member("updatedAtUtc")
        .and_then(Value::as_str)
        .is_some_and(|at| !at.starts_with("0001-01-01"));
    recognized
        && int("state") == Some(RELINQUISHED)
        && base
        && restore
        && power
        && retry
        && transition
        && since(4, "recoveryBaseline").is_none()
        && updated
}

/// Reads managed's HyperBoost ownership journal,
/// `ProgramData\OpenBlade\hyperboost-ownership.json`, read-only through the
/// protected path checks every machine record is read with. `None` when the
/// file does not exist in the verified directory.
#[cfg(not(test))]
pub(super) fn read() -> io::Result<Option<Vec<u8>>> {
    use crate::windows_protected_path::{self as paths, Policy};
    use std::{ffi::OsStr, io::Read};
    use windows_sys::{
        Wdk::Storage::FileSystem::{FILE_NON_DIRECTORY_FILE, FILE_OPEN, FILE_OPEN_REPARSE_POINT},
        Win32::Storage::FileSystem::{FILE_GENERIC_READ, FILE_SHARE_READ},
    };
    let path = crate::service_install_identity::known_folder(
        &windows_sys::Win32::UI::Shell::FOLDERID_ProgramData,
    )?
    .join("OpenBlade");
    let (root, ancestors, _) = paths::pin_root(&path)?;
    paths::require_ntfs(&root)?;
    paths::verify_security(&root, Policy::ROOT)?;
    for (i, parent) in ancestors.iter().enumerate() {
        paths::verify_security(
            parent,
            if i + 1 == ancestors.len() {
                Policy::PROGRAM_DATA
            } else {
                Policy::ANCESTOR
            },
        )?;
    }
    let mut file = match paths::open_relative(
        &root,
        OsStr::new("hyperboost-ownership.json"),
        FILE_GENERIC_READ,
        FILE_SHARE_READ,
        FILE_OPEN,
        FILE_NON_DIRECTORY_FILE | FILE_OPEN_REPARSE_POINT,
    ) {
        Ok(file) => file,
        Err(error) if paths::missing_leaf(&error) => return Ok(None),
        Err(error) => return Err(error),
    };
    paths::check_regular_file(&file)?;
    paths::verify_security(&file, Policy::Record)?;
    if file.metadata()?.len() > 65536 {
        return Err(paths::denied("the HyperBoost journal is too large"));
    }
    let mut bytes = Vec::new();
    file.read_to_end(&mut bytes)?;
    Ok(Some(bytes))
}

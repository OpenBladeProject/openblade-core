//! Managed's HyperBoost ownership journal, read-only. The convergence loop
//! and owned HyperBoost (`hyperboost`) both read it: managed owns its
//! recovery (`ReconcileUnexpectedExitAsync`), and native writes nothing on
//! its behalf, so native refuses a write while the journal is pending.
use openblade_domain::profile_set::{
    FanMode, decode,
    json::{self, Json},
};
use openblade_domain::thermal_convergence as ledger;
use std::{io, sync::Arc};

/// Reads managed's HyperBoost ownership journal: its bytes, `None` when the
/// file is absent.
pub(in super::super) type Journal = Arc<dyn Fn() -> io::Result<Option<Vec<u8>>> + Send + Sync>;

/// Managed `HyperBoostOwnershipJournalState.Relinquished`.
pub(super) const RELINQUISHED: i64 = 3;
/// Managed `HyperBoostOwnershipJournalState.RestorePending`.
#[cfg(feature = "service-hyperboost-v1")]
const RESTORE_PENDING: i64 = 2;
/// Managed `PerformancePreset.Custom` and its last value, `Creator`.
const CUSTOM_PRESET: i64 = 5;
const PRESETS: i64 = 9;
/// The members of managed `HyperBoostOwnershipJournalDocument`, as its
/// serializer writes them.
const MEMBERS: [&str; 15] = [
    "schemaVersion",
    "state",
    "baseMode",
    "updatedAtUtc",
    "requiresCoolingPad",
    "restorePerformance",
    "restoreCustomPerformance",
    "restoreGpuClockTuning",
    "restoreFanControl",
    "recoveryBaseline",
    "restoreRetryDisposition",
    "restoreRetryTarget",
    "transitionId",
    "restorePowerSource",
    "restoreUsesCurrentPowerDestination",
];

/// Managed `IsSafeMaintainHyperBoostJournalBaseline` over the journal's
/// bytes: absent, or a document managed's store reads
/// (`HyperBoostOwnershipJournalStore.LoadAsync`) as a recognized schema that
/// `IsWellFormed` admits, whose state is Relinquished.
///
/// It fails closed: a document native cannot read exactly as managed does
/// blocks, even where managed would read it. Managed's `JsonSerializerDefaults.Web`
/// reader matches names in any case, takes the last of duplicate members,
/// ignores unknown members and reads numbers from strings. Native accepts only
/// what managed writes: the camelCase members, each once, JSON numbers for the
/// document's own numbers and enums, and the update time as a full timestamp
/// with its offset (`timestamp`). Every member is type-checked whatever the
/// schema, since managed binds each one before it ignores any. The restore
/// intent's settings records are bound as stored profiles bind them
/// (`profile_set::decode`), which refuses their unknown members but reads
/// their integers through `Json::web_integer`, so a number in a string is
/// accepted there, as managed's `AllowReadingFromString` reads it. Every
/// object's names are exact and distinct (`json::parse` refuses a
/// duplicate), so no two of them are one member to managed's
/// case-insensitive reader. A recovery baseline is refused in every schema:
/// only a RestorePending journal may carry one. A Turbo base, which managed
/// admits, is refused as before: no native model enters HyperBoost from it.
pub(super) fn journal_safe(bytes: Option<&[u8]>) -> bool {
    let Some(bytes) = bytes else {
        return true;
    };
    json::parse(bytes).is_ok_and(|document| relinquished(&document).is_some())
}

/// Whether managed's journal names a restore it has not finished: a document
/// of managed's own members, each once, whose state is RestorePending. That
/// is what managed's `hyperBoostRestorePending` starts from
/// (`load.Journal?.State == RestorePending`).
///
/// It reads the state only. Managed also refuses a document `IsWellFormed`
/// rejects ("invalid and was preserved for manual inspection") and then
/// reports no pending restore; native reads only a Relinquished document
/// exactly as managed does (`relinquished`), so a RestorePending document
/// managed would call invalid still reads as pending here. Either way
/// `journal_safe` refuses every HyperBoost write, and this answer admits
/// nothing.
#[cfg(feature = "service-hyperboost-v1")]
pub(super) fn restore_pending(bytes: Option<&[u8]>) -> bool {
    bytes
        .and_then(|bytes| json::parse(bytes).ok())
        .is_some_and(|document| {
            document.members().is_some_and(|members| {
                members
                    .iter()
                    .all(|(name, _)| MEMBERS.contains(&name.as_str()))
            }) && document
                .member("state")
                .and_then(|state| state.integer(i32::MIN.into(), i32::MAX.into()))
                == Some(RESTORE_PENDING)
        })
}

/// `Some` for a recognized, well-formed Relinquished document.
fn relinquished(document: &Json) -> Option<()> {
    let members = document.members()?;
    if !members
        .iter()
        .all(|(name, _)| MEMBERS.contains(&name.as_str()))
    {
        return None;
    }
    let get = |name: &str| document.member(name);
    // A nullable member: absent or `null` is None, anything else is read.
    fn nullable<'a, T>(
        value: Option<&'a Json>,
        read: impl FnOnce(&'a Json) -> Option<T>,
    ) -> Option<Option<T>> {
        match value {
            None | Some(Json::Null) => Some(None),
            Some(value) => read(value).map(Some),
        }
    }
    let int = |value: &Json| value.integer(i32::MIN.into(), i32::MAX.into());
    let nullable_int = |name: &str| nullable(get(name), int);
    // The constructor's required parameters, which bind no `null`.
    let schema = int(get("schemaVersion")?)?;
    let state = int(get("state")?)?;
    let base = int(get("baseMode")?)?;
    if !timestamp(get("updatedAtUtc")?.text()?) {
        return None;
    }
    let requires_pad = nullable(get("requiresCoolingPad"), Json::boolean)?;
    let restore_performance = nullable_int("restorePerformance")?;
    let custom = nullable(get("restoreCustomPerformance"), |value| {
        decode::custom_performance(value).ok()
    })?;
    // `GpuClockTuningSettings.IsWellFormed` needs its offsets.
    nullable(get("restoreGpuClockTuning"), |value| {
        decode::gpu_clock_tuning(value)
            .ok()
            .filter(|tuning| tuning.offsets.is_some())
    })?;
    let fan_control = nullable(get("restoreFanControl"), |value| {
        decode::fan_control(value).ok()
    })?;
    let disposition = nullable_int("restoreRetryDisposition")?;
    let target = nullable_int("restoreRetryTarget")?;
    let transition = nullable(get("transitionId"), |value| {
        value.text().and_then(ledger::Guid::parse)
    })?;
    let power_source = nullable_int("restorePowerSource")?;
    // An init-only `bool`: its default when absent, never `null`.
    get("restoreUsesCurrentPowerDestination").map_or(Some(false), Json::boolean)?;
    if get("recoveryBaseline").is_some_and(|value| !value.is_null()) {
        return None;
    }
    let recognized = match schema {
        1 => requires_pad.is_none(),
        2 => requires_pad.is_some(),
        3..=8 => requires_pad.is_some() && restore_performance.is_some() && fan_control.is_some(),
        _ => false,
    };
    // Managed `HyperBoostRestoreIntent`: a legacy schema restores its base
    // mode with Automatic fans.
    let (performance, custom, fan_mode) = if schema >= 3 {
        (restore_performance?, custom, fan_control?.mode)
    } else {
        (base, None, FanMode::AUTOMATIC)
    };
    let restore = (0..=PRESETS).contains(&performance)
        && (performance == CUSTOM_PRESET) == custom.is_some()
        && fan_mode.is_defined();
    let base = match base {
        0..=2 => true,
        CUSTOM_PRESET => custom.is_some(),
        _ => false,
    };
    // Managed reads each later member only from the schema that added it:
    // the power source the restore is bound to, no retry, and a transition
    // ID that is not empty.
    let since = |added: i64, value: Option<i64>| if schema >= added { value } else { None };
    let power = schema != 8 || matches!(power_source, Some(0..=2));
    let retry = since(5, disposition).is_none_or(|value| value == 0)
        && since(6, target).is_none_or(|value| value == 0);
    let transition = schema < 7 || transition.is_none_or(|id| id != ledger::Guid::EMPTY);
    (recognized && state == RELINQUISHED && base && restore && power && retry && transition)
        .then_some(())
}

/// The update time as managed writes a `DateTimeOffset`:
/// `yyyy-MM-ddTHH:mm:ss`, an optional fraction of one to seven digits, and
/// `Z` or a `+hh:mm`/`-hh:mm` offset of at most 14 hours. Managed also reads
/// a date alone, a time without seconds or offset and longer fractions;
/// native refuses them. The first and last calendar days are refused too:
/// managed's `default` (`IsWellFormed`) and every time whose UTC instant
/// leaves `DateTimeOffset`'s range lie on them.
fn timestamp(text: &str) -> bool {
    let bytes = text.as_bytes();
    let number = |range: std::ops::Range<usize>| -> Option<u32> {
        let digits = bytes.get(range)?;
        digits
            .iter()
            .all(u8::is_ascii_digit)
            .then(|| digits.iter().fold(0, |n, d| n * 10 + u32::from(d - b'0')))
    };
    let at = |index: usize, byte: u8| bytes.get(index) == Some(&byte);
    let (Some(year), Some(month), Some(day), Some(hour), Some(minute), Some(second)) = (
        number(0..4),
        number(5..7),
        number(8..10),
        number(11..13),
        number(14..16),
        number(17..19),
    ) else {
        return false;
    };
    if !(at(4, b'-') && at(7, b'-') && at(10, b'T') && at(13, b':') && at(16, b':')) {
        return false;
    }
    let leap = year % 4 == 0 && (year % 100 != 0 || year % 400 == 0);
    let days = match month {
        1 | 3 | 5 | 7 | 8 | 10 | 12 => 31,
        4 | 6 | 9 | 11 => 30,
        2 if leap => 29,
        2 => 28,
        _ => return false,
    };
    let mut rest = &bytes[19..];
    if let Some(fraction) = rest.strip_prefix(b".") {
        let digits = fraction.iter().take_while(|b| b.is_ascii_digit()).count();
        if !(1..=7).contains(&digits) {
            return false;
        }
        rest = &fraction[digits..];
    }
    let offset = match rest {
        b"Z" => true,
        [b'+' | b'-', h1, h2, b':', m1, m2] => {
            let two = |a: u8, b: u8| {
                (a.is_ascii_digit() && b.is_ascii_digit())
                    .then(|| u32::from(a - b'0') * 10 + u32::from(b - b'0'))
            };
            matches!(
                (two(*h1, *h2), two(*m1, *m2)),
                (Some(h), Some(m)) if m < 60 && h * 60 + m <= 14 * 60
            )
        }
        _ => false,
    };
    offset
        && (1..=9999).contains(&year)
        && (1..=days).contains(&day)
        && hour < 24
        && minute < 60
        && second < 60
        && !(year == 1 && month == 1 && day == 1)
        && !(year == 9999 && month == 12 && day == 31)
}

/// Reads managed's HyperBoost ownership journal,
/// `ProgramData\OpenBlade\hyperboost-ownership.json`, read-only through the
/// protected path checks every machine record is read with. `None` when the
/// file does not exist in the verified directory.
#[cfg(not(test))]
// Its readers are the HyperBoost probes the lifecycle triggers compose and
// the convergence loop.
#[cfg_attr(
    not(any(
        feature = "service-profile-triggers-v1",
        feature = "service-thermal-convergence-v1"
    )),
    allow(dead_code)
)]
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

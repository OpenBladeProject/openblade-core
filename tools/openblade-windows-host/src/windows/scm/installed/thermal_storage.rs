//! Compatible saved intent and durable per-submission fence. Constructors take
//! explicit storage first; the injected path never resolves a product root.
use crate::configuration::{self, File as ConfigFile, Inspection, RecoveryRecord};
use openblade_domain::{
    automation::{UNCLASSIFIED_EXTERNAL, temporary_after, temporary_expired},
    profile_set::{
        ApplicationProfile, CustomPerformance, Dialect, FanControl, FanMode, PowerProfile,
        PowerSource, ProfileSet,
        application::{self, ApplicationContext, Confirmed, Power, Resolution},
    },
    thermal_mutation::{CUSTOM, Destination, Preset, Progress, progress_follows, settled},
};
// The selection a report makes and the terms a profile save compares
// (`SaveIntent`) belong to automatic reconciliation.
#[cfg(feature = "service-profile-triggers-v1")]
use openblade_domain::{
    application_context::{self, Rule},
    automation::Provenance,
    profile_set::{
        BatteryPolicy, GpuClockTuning, LaptopBehavior, LightingSettings, PerformancePreset,
    },
};
use serde_json::{Value, json};

use super::mailbox_read::CleanupScope;
use std::{
    io,
    sync::{Arc, Mutex},
};

#[path = "battery_storage.rs"]
mod battery;
pub(super) use battery::TemporaryRelease;
#[cfg(feature = "service-temporary-charge-v1")]
pub(super) use battery::TemporaryTaken;
#[cfg(feature = "service-profiles-v1")]
#[path = "profile_storage.rs"]
mod profiles;
#[cfg(feature = "service-fan-stop-v1")]
pub(super) use profiles::PreferencesSave;
#[cfg(feature = "service-profiles-v1")]
pub(super) use profiles::ProfileSave;
#[cfg(feature = "service-profile-triggers-v1")]
pub(super) use profiles::{PolicyChange, PolicySave, SavedPolicy};
#[cfg(feature = "service-thermal-convergence-v1")]
#[path = "thermal_convergence_ledger.rs"]
mod convergence;

pub(super) const RELEASE_PAUSED: &str = "Temporary charge journal release is deferred.";
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(super) enum ReleasePause {
    Busy,
    ReadUnavailable,
    Reconciliation(&'static str),
}
impl ReleasePause {
    pub(super) fn message(self) -> String {
        let reason = match self {
            Self::Busy => {
                "New mutations are paused until active work settles; no hardware command will be replayed."
            }
            Self::ReadUnavailable => {
                "New mutations are paused while a read-only check is retried; no hardware command will be replayed."
            }
            Self::Reconciliation(reason) => reason,
        };
        format!("{RELEASE_PAUSED} {reason}")
    }
    pub(super) fn compose(self, message: &str) -> String {
        // An unresolved settlement is the authoritative public reason. Keep
        // it equal to the owned pending outcome so existing typed rendering
        // emits it once; generic release wording must not demote that cause.
        match self {
            Self::Reconciliation(reason) => reason.into(),
            _ => format!("{message} {}", self.message()).trim().to_owned(),
        }
    }
}
#[derive(Debug)]
pub(super) struct ReleaseDeferred {
    pub(super) cause: ReleasePause,
    source: Option<io::Error>,
}
impl ReleaseDeferred {
    pub(super) fn busy() -> Self {
        Self {
            cause: ReleasePause::Busy,
            source: None,
        }
    }
    pub(super) fn reconciliation(reason: &'static str) -> Self {
        Self {
            cause: ReleasePause::Reconciliation(reason),
            source: None,
        }
    }
    pub(super) fn unreadable(error: io::Error) -> Self {
        Self {
            cause: ReleasePause::ReadUnavailable,
            source: Some(error),
        }
    }
    pub(super) fn into_error(self) -> io::Error {
        io::Error::new(io::ErrorKind::WouldBlock, self)
    }
}
impl std::fmt::Display for ReleaseDeferred {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(&self.cause.message())
    }
}
impl std::error::Error for ReleaseDeferred {
    fn source(&self) -> Option<&(dyn std::error::Error + 'static)> {
        self.source.as_ref().map(|e| e as _)
    }
}
pub(super) fn is_release_deferred(error: &io::Error) -> bool {
    error.get_ref().is_some_and(|e| e.is::<ReleaseDeferred>())
}
// Created only at the raw open/read operations in the release-only adapter.
// Trust, custody, parsing, metadata and aggregate I/O errors are never tagged.
#[derive(Debug)]
struct ReleaseReadUnavailable(io::Error);
impl std::fmt::Display for ReleaseReadUnavailable {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        self.0.fmt(f)
    }
}
impl std::error::Error for ReleaseReadUnavailable {
    fn source(&self) -> Option<&(dyn std::error::Error + 'static)> {
        Some(&self.0)
    }
}
pub(super) fn release_open_error(error: io::Error) -> io::Error {
    if error.raw_os_error() == Some(windows_sys::Win32::Foundation::ERROR_SHARING_VIOLATION as i32)
    {
        io::Error::new(io::ErrorKind::WouldBlock, ReleaseReadUnavailable(error))
    } else {
        error
    }
}
pub(super) fn release_bytes_error(error: io::Error) -> io::Error {
    if error.raw_os_error() == Some(windows_sys::Win32::Foundation::ERROR_LOCK_VIOLATION as i32) {
        io::Error::new(io::ErrorKind::WouldBlock, ReleaseReadUnavailable(error))
    } else {
        error
    }
}
fn is_release_read_unavailable(error: &io::Error) -> bool {
    error
        .get_ref()
        .is_some_and(|e| e.is::<ReleaseReadUnavailable>())
}
const OVERRIDES: &str = "manual-overrides.json";
const JOURNAL: &str = "thermal-mutation.journal";
pub(super) trait Files: Send + Sync {
    fn read(&self, name: &str) -> io::Result<Option<Vec<u8>>>;
    fn read_release(&self, _name: &str) -> io::Result<Option<Vec<u8>>> {
        Err(denied())
    }
    fn reserve(&self, expected: Option<&[u8]>, needed: usize) -> io::Result<Option<Vec<u8>>> {
        if needed > crate::thermal_history::SEGMENT
            || expected
                .unwrap_or_default()
                .len()
                .checked_add(needed)
                .is_none_or(|n| n > crate::thermal_history::SEGMENT)
        {
            return Err(io::Error::new(
                io::ErrorKind::StorageFull,
                "Thermal progress capacity unavailable before Prepared.",
            ));
        }
        Ok(expected.map(<[u8]>::to_vec))
    }
    fn replace(&self, name: &str, expected: Option<&[u8]>, bytes: &[u8]) -> io::Result<()>;
    /// A cleanup row under `scope`, which only a custodial store checks.
    fn replace_cleanup(
        &self,
        name: &str,
        _scope: CleanupScope,
        expected: Option<&[u8]>,
        bytes: &[u8],
    ) -> io::Result<()> {
        self.replace(name, expected, bytes)
    }
    fn replace_retired(&self, name: &str, expected: Option<&[u8]>, bytes: &[u8]) -> io::Result<()> {
        self.replace(name, expected, bytes)
    }
    /// Deletes `name` only while it still holds exactly `expected`. A store
    /// that cannot prove the removal refuses it.
    #[cfg(feature = "service-profiles-v1")]
    fn remove(&self, _name: &str, _expected: &[u8]) -> io::Result<()> {
        Err(denied())
    }
    /// Removes the staging file a `replace` of `name` leaves where it fails.
    /// None is success. Only the owner of `name` may call this, and only for
    /// a file whose stage is never evidence: the override document's and the
    /// journal's are preserved for the handback. A store that cannot prove
    /// the removal refuses it.
    #[cfg(feature = "service-profiles-v1")]
    fn discard_stage(&self, _name: &str) -> io::Result<()> {
        Err(denied())
    }
}
/// Whether the journal holds a fan change this Service owns without its
/// handback; a journal the history cannot read counts as outstanding.
fn fan_owner_outstanding(journal: Option<&[u8]>) -> bool {
    !crate::thermal_history::handback_complete(journal.unwrap_or_default()).unwrap_or(false)
}
fn denied() -> io::Error {
    io::Error::new(
        io::ErrorKind::PermissionDenied,
        "thermal configuration or submission journal is quarantined",
    )
}
fn decode(bytes: &[u8]) -> io::Result<Value> {
    configuration::validate_json_members(bytes).map_err(io::Error::other)?;
    serde_json::from_slice(bytes).map_err(io::Error::other)
}
// Managed PersistentOverrideStore.CurrentSchemaVersion. Schema 9 added keyboard
// effect control and schema 10 lighting zone controls; schema 8 is the oldest
// document whose fan details this bounded reader accepts without migration.
const OVERRIDE_SCHEMA: i64 = 10;
const OVERRIDE_KEYBOARD_EFFECT_SCHEMA: i64 = 9;
const OVERRIDE_FAN_DETAIL_SCHEMA: i64 = 8;
fn overrides(bytes: Option<&[u8]>) -> io::Result<Value> {
    let mut document = match bytes {
        Some(bytes) => decode(bytes)?,
        None => {
            json!({"schemaVersion":OVERRIDE_SCHEMA,"overrides":{"performance":null,"fans":null,"lighting":null,"customPerformance":null,"fanControl":null,"gpuClockTuning":null}})
        }
    };
    let root = document.as_object_mut().ok_or_else(denied)?;
    if root.len() != 2
        || !matches!(
            root.get("schemaVersion").and_then(Value::as_i64),
            Some(1..=OVERRIDE_SCHEMA)
        )
    {
        return Err(denied());
    }
    let schema = root
        .get("schemaVersion")
        .and_then(Value::as_i64)
        .ok_or_else(denied)?;
    // Managed rejects these before typed migration, so the document is invalid
    // rather than preserved for later migration.
    if configuration::legacy_lighting_intent(
        &Value::Object(root.clone()),
        schema < OVERRIDE_SCHEMA,
        schema < OVERRIDE_KEYBOARD_EFFECT_SCHEMA,
    ) {
        return Err(denied());
    }
    if schema < OVERRIDE_FAN_DETAIL_SCHEMA {
        return Err(io::Error::new(
            io::ErrorKind::Unsupported,
            "Legacy overrides require managed migration; original bytes are preserved.",
        ));
    }
    let value = root
        .get_mut("overrides")
        .and_then(Value::as_object_mut)
        .ok_or_else(denied)?;
    if value.keys().any(|key| {
        ![
            "performance",
            "fans",
            "lighting",
            "customPerformance",
            "fanControl",
            "gpuClockTuning",
        ]
        .contains(&key.as_str())
    }) {
        return Err(denied());
    }
    for key in ["performance", "fans", "lighting"] {
        if !value.contains_key(key) {
            return Err(denied());
        }
    }
    if !value["performance"].is_null() && !matches!(value["performance"].as_i64(), Some(0..=9)) {
        return Err(denied());
    }
    if !value["fans"].is_null() && !matches!(value["fans"].as_i64(), Some(0..=3)) {
        return Err(denied());
    }
    for key in [
        "lighting",
        "customPerformance",
        "fanControl",
        "gpuClockTuning",
    ] {
        if value
            .get(key)
            .is_some_and(|v| !v.is_null() && !v.is_object())
        {
            return Err(denied());
        }
    }
    if value["performance"].as_i64() != Some(5)
        && value.get("customPerformance").is_some_and(|v| !v.is_null())
    {
        return Err(denied());
    }
    // Preserve all unrelated fields. Legacy fan detail normalization is the
    // managed FromLegacy rule; never replace a supplied manual curve/target.
    if let Some(mode) = value["fans"].as_i64() {
        if value.get("fanControl").is_none_or(Value::is_null) {
            return Err(io::Error::new(
                io::ErrorKind::Unsupported,
                "Legacy fan details require managed normalization; original bytes are preserved.",
            ));
        } else if value["fanControl"]["mode"].as_i64() != Some(mode) {
            return Err(denied());
        }
    } else if value.get("fanControl").is_some_and(|v| !v.is_null()) {
        return Err(denied());
    }
    if let Some(v) = value.get("gpuClockTuning").filter(|v| !v.is_null()) {
        validate_gpu(v)?;
    }
    if let Some(v) = value.get("customPerformance").filter(|v| !v.is_null()) {
        members(v, &["cpu", "gpu", "maximumFans", "gpuClockOffsets"])?;
        if !matches!(v["cpu"].as_i64(), Some(0..=3))
            || !matches!(v["gpu"].as_i64(), Some(0..=2))
            || v.get("maximumFans").is_some_and(|v| !v.is_boolean())
        {
            return Err(denied());
        }
        if !v["gpuClockOffsets"].is_null() {
            offsets(&v["gpuClockOffsets"])?;
            if value.get("gpuClockTuning").is_some_and(|v| !v.is_null()) {
                return Err(denied());
            }
        }
    } else if value["performance"].as_i64() == Some(5) {
        return Err(denied());
    }
    if let Some(v) = value.get("fanControl").filter(|v| !v.is_null()) {
        fan_control(v)?;
    }
    // Schema 9 independent-intent preservation rewrites only non-null lighting.
    if !value["lighting"].is_null() {
        return Err(io::Error::new(
            io::ErrorKind::Unsupported,
            "Saved lighting override validation is outside this bounded composition; bytes are preserved.",
        ));
    }
    Ok(document)
}
fn members(v: &Value, allowed: &[&str]) -> io::Result<()> {
    if v.as_object()
        .is_none_or(|o| o.keys().any(|k| !allowed.contains(&k.as_str())))
    {
        Err(denied())
    } else {
        Ok(())
    }
}
fn offsets(v: &Value) -> io::Result<()> {
    members(v, &["coreMegahertz", "memoryMegahertz"])?;
    if ["coreMegahertz", "memoryMegahertz"]
        .iter()
        .any(|k| !matches!(v[k].as_i64(), Some(-10000..=10000)))
    {
        return Err(denied());
    }
    Ok(())
}
fn validate_gpu(v: &Value) -> io::Result<()> {
    crate::service_status::validate_gpu_tuning(v)
}
fn fan_control(v: &Value) -> io::Result<()> {
    members(v, &["mode", "fixedSpeedRpm", "manualCurve"])?;
    // Managed FanControlSettings.ValidateSpeed admits MaximumSupportedSpeedRpm
    // in stored intent for every model. Exact-model write admission remains
    // fan_history::validate_settings and the fan recipe, both at 5400.
    let speed = |v: &Value| matches!(v.as_i64(),Some(n) if (2000..=5500).contains(&n)&&n%100==0);
    if !matches!(v["mode"].as_i64(), Some(0..=3)) || !speed(&v["fixedSpeedRpm"]) {
        return Err(denied());
    }
    let curve = &v["manualCurve"];
    members(curve, &["cpu", "gpu", "useHigherTemperatureForBoth"])?;
    if !curve["useHigherTemperatureForBoth"].is_boolean() {
        return Err(denied());
    }
    for key in ["cpu", "gpu"] {
        let points = curve[key].as_array().ok_or_else(denied)?;
        if !(2..=10).contains(&points.len()) {
            return Err(denied());
        }
        let mut previous = None;
        for p in points {
            members(p, &["temperatureCelsius", "speedRpm"])?;
            let t = p["temperatureCelsius"].as_i64().ok_or_else(denied)?;
            if !(20..=100).contains(&t) || !speed(&p["speedRpm"]) {
                return Err(denied());
            }
            let rpm = p["speedRpm"].as_i64().unwrap();
            if previous.is_some_and(|(pt, ps)| t <= pt || rpm < ps) {
                return Err(denied());
            }
            previous = Some((t, rpm));
        }
    }
    Ok(())
}

pub(super) use crate::service_status::PerformanceOverride as Override;
/// The destination saved Custom settings name on exact RZ09-0581, if the
/// model admits them (`Destination::custom`). Settings without both levels
/// name none; `Selected::profile` supplies a profile's missing levels first.
pub(super) fn custom_destination(settings: &Value) -> Option<Destination> {
    Destination::custom(
        settings["cpu"].as_u64()?,
        settings["gpu"].as_u64()?,
        settings
            .get("maximumFans")
            .map_or(Some(false), Value::as_bool)?,
    )
}
/// Offsets in the saved override's JSON form.
fn offsets_value(offsets: openblade_domain::profile_set::model::GpuClockOffsets) -> Value {
    json!({
        "coreMegahertz": offsets.core_megahertz,
        "memoryMegahertz": offsets.memory_megahertz,
    })
}
/// A selected wire value, the destination it names on exact RZ09-0581 and
/// its provenance. `legacy_offsets` is legacy offsets saved inside the
/// selected Custom settings, a GPU clock effect (managed
/// `NormalizeLegacyGpuClockTuning`), in the saved offsets' JSON form.
struct Selected {
    value: i32,
    destination: Option<Destination>,
    legacy_offsets: Option<Value>,
    source: i32,
}
impl Selected {
    /// An override layer's value with the Custom settings saved beside it.
    /// A Custom override without them names no destination.
    fn saved(value: i32, custom: Option<&Value>, source: i32) -> Self {
        let custom = custom.filter(|c| !c.is_null()).filter(|_| value == CUSTOM);
        Self {
            value,
            destination: if value == CUSTOM {
                custom.and_then(custom_destination)
            } else {
                Preset::from_wire(value).map(Destination::Preset)
            },
            legacy_offsets: custom
                .and_then(|c| c.get("gpuClockOffsets"))
                .filter(|o| !o.is_null())
                .cloned(),
            source,
        }
    }
    /// The resolved profile's preset. Managed resolves a selected Custom's
    /// levels as the override's, then the profile's, then the confirmed
    /// state's, then `CustomPerformanceSettings.Default`
    /// (`ProfileAutomationState`), and its profile saves store those defaults
    /// in place of missing levels, so a profile without them selects
    /// Medium/High here too. The confirmed state is not consulted: a stored
    /// override Custom always carries its levels, and managed's normalized
    /// profiles never reach it.
    fn profile(profile: &PowerProfile, source: i32) -> Self {
        let value = profile.performance.0;
        let custom = (value == CUSTOM).then(|| {
            profile
                .custom_performance
                .unwrap_or(CustomPerformance::DEFAULT)
        });
        let level = |level: i32| u64::try_from(level).unwrap_or(u64::MAX);
        Self {
            value,
            destination: match custom {
                Some(c) => Destination::custom(level(c.cpu.0), level(c.gpu.0), c.maximum_fans),
                None => Preset::from_wire(value).map(Destination::Preset),
            },
            legacy_offsets: custom.and_then(|c| c.gpu_clock_offsets).map(offsets_value),
            source,
        }
    }
}
/// The classified source a native power class selects a power profile for.
/// Unclassified external power and an unknown class select none.
pub(super) fn power_source(power: i32) -> Option<PowerSource> {
    match power {
        0 => Some(PowerSource::PluggedIn),
        1 => Some(PowerSource::Battery),
        2 => Some(PowerSource::UsbC),
        _ => None,
    }
}
/// `profile` when it resolves a power profile for class `power`. Native
/// applies no profile on unclassified external or unknown power, where
/// managed `Resolve` would take `CreateObservedFallbackProfile`: there every
/// saved destination comes from an explicit override. A resolution for
/// another class selects nothing rather than that class's profile.
fn profile_layer<'r, 'a>(
    profile: Option<&'r Resolution<'a>>,
    power: i32,
) -> Option<&'r Resolution<'a>> {
    profile.filter(|r| power_source(power).is_some_and(|source| r.desired_source() == Some(source)))
}
/// The saved application rules, as managed `ProfileAutomationState` reads them.
#[cfg(feature = "service-profile-triggers-v1")]
fn rules(set: &ProfileSet) -> impl Iterator<Item = Rule<'_>> {
    set.application_profiles
        .iter()
        .map(ApplicationProfile::rule)
}
/// A resolved fan control as stored intent is written, so it compares with
/// the override layers' and the fan journal's settings.
fn fan_settings(settings: &FanControl) -> Value {
    let points = |points: &[openblade_domain::profile_set::FanCurvePoint]| {
        points
            .iter()
            .map(|p| json!({"temperatureCelsius":p.temperature_celsius,"speedRpm":p.speed_rpm}))
            .collect::<Vec<_>>()
    };
    let curve = &settings.manual_curve;
    json!({"mode":settings.mode.0,"fixedSpeedRpm":settings.fixed_speed_rpm,"manualCurve":{"cpu":points(&curve.cpu),"gpu":points(&curve.gpu),"useHigherTemperatureForBoth":curve.use_higher_temperature_for_both}})
}
/// The foreground application one interactive session reported: the path
/// in `NormalizeExecutablePath` form and a context for the active pair
/// managed `SetApplicationContext` keeps for it. Without automatic
/// reconciliation the context stays empty: each read selects the reported
/// path again in the profiles it read (`Storage::profile_layer`).
#[cfg(not(feature = "service-profile-triggers-v1"))]
#[derive(Clone, Debug, Default)]
pub(super) struct Foreground {
    reported: Option<String>,
    context: ApplicationContext,
}
#[cfg(not(feature = "service-profile-triggers-v1"))]
impl Foreground {
    pub(super) fn reported(path: Option<String>) -> Self {
        Self {
            reported: path,
            context: ApplicationContext::default(),
        }
    }
}
/// The active application pair one interactive session's report selected,
/// as managed `SetApplicationContext` keeps it: the rule id and the reported
/// path in `NormalizeExecutablePath` form. The report selects once
/// (`Storage::select_application`); a profile save retires the pair through
/// `ApplicationContext::update` (`Storage::retain_application`) and never
/// selects, so a rule that gains the path takes effect at the next report.
#[cfg(feature = "service-profile-triggers-v1")]
#[derive(Clone, Debug, Default, PartialEq)]
pub(super) struct Foreground {
    context: ApplicationContext,
}
#[cfg(feature = "service-profile-triggers-v1")]
impl Foreground {
    pub(super) fn active_id(&self) -> Option<&str> {
        self.context.active().map(|active| active.id)
    }
}
/// What a profile save compares before and after it stores, managed
/// `activeApplicationIntentChanged` (`Storage::save_intent`): the active id,
/// the desired state and the effective laptop behaviour at one class.
#[cfg(feature = "service-profile-triggers-v1")]
#[derive(Debug, PartialEq)]
pub(super) struct SaveIntent {
    application_profile_id: Option<String>,
    desired: Option<SaveDesired>,
    laptop_behavior: Option<LaptopBehavior>,
}
#[cfg(feature = "service-profile-triggers-v1")]
impl SaveIntent {
    /// `intent_changed` over two reads of the same class.
    pub(super) fn changed(&self, next: &Self) -> bool {
        fn view(
            i: &SaveIntent,
        ) -> application_context::Intent<'_, &Option<SaveDesired>, &Option<LaptopBehavior>>
        {
            application_context::Intent {
                application_profile_id: i.application_profile_id.as_deref(),
                desired: &i.desired,
                laptop_behavior: &i.laptop_behavior,
            }
        }
        application_context::intent_changed(&view(self), &view(next))
    }
    /// Whether the change reaches what a reconciliation applies: the active
    /// id, or the performance or fan members. Native composes only those
    /// subsystems, so a save that changes only lighting, battery policy or
    /// laptop behaviour has nothing to apply and replies Stored.
    pub(super) fn thermal_changed(&self, next: &Self) -> bool {
        self.changed(next) && self.thermal() != next.thermal()
    }
    fn thermal(&self) -> impl PartialEq + '_ {
        (
            self.application_profile_id.as_deref(),
            self.desired.as_ref().map(|d| {
                (
                    &d.performance,
                    &d.custom_performance,
                    &d.gpu_clock_tuning,
                    &d.fans,
                    &d.destination,
                    &d.fan,
                )
            }),
        )
    }
}
/// Managed `ResolveAutomationDesired` as native composes it: the profile
/// layer's `desired_intent` with every member an explicit override decides
/// masked, and the composed destination and fan intent those overrides
/// select (`Storage::desired`, `Storage::desired_fan`).
#[cfg(feature = "service-profile-triggers-v1")]
#[derive(Debug, PartialEq)]
struct SaveDesired {
    performance: Option<application::Setting<PerformancePreset>>,
    custom_performance: Option<CustomPerformance>,
    gpu_clock_tuning: Option<GpuClockTuning>,
    fans: Option<(application::Setting<FanMode>, FanControl)>,
    lighting: (Option<LightingSettings>, Provenance),
    battery_policy: Option<BatteryPolicy>,
    battery_protection_enabled: bool,
    destination: Option<(Destination, i32)>,
    fan: Option<Value>,
}
/// The saved profiles, typed, with a session's foreground application
/// selected in them: one aggregate read, from which each class resolves
/// (`Self::at`) through the domain composition.
pub(super) struct ProfileLayer {
    set: Option<ProfileSet>,
    context: ApplicationContext,
}
impl ProfileLayer {
    /// Managed `Resolve`'s profile layer at class `power`, or `None` when no
    /// aggregate is stored. Confirmed firmware state is left unknown: it
    /// shapes only the observed fallback profile, which native never applies
    /// (`profile_layer`), and Custom settings a destination does not carry,
    /// which `Selected::profile` defaults as managed's saves do.
    pub(super) fn at(&self, power: i32) -> io::Result<Option<Resolution<'_>>> {
        let Some(set) = &self.set else {
            return Ok(None);
        };
        application::resolve(
            &self.context,
            set,
            Power::retained(power_source(power)),
            Confirmed::default(),
        )
        .map(Some)
        .map_err(|invalid| io::Error::new(io::ErrorKind::InvalidData, invalid.0))
    }
}
use crate::thermal_history::Entry;
struct State {
    quarantined: bool,
    configuration_failed: bool,
    configuration_changed: bool,
    preparation_failed: bool,
    intent_append_failed: bool,
    bytes: Option<Vec<u8>>,
    document: Value,
    journal: Option<Vec<u8>>,
    last: Option<Entry>,
    temporary: Option<(Override, i32)>,
    temporary_fan: Option<(Value, i32)>,
    // Windows power generations for `automation::temporary_after`: the latest
    // fresh power reading's, and each temporary's at its save.
    generation: Option<u64>,
    // The class that latest reading classified (`Storage::observe_power`),
    // current only while the Windows power generation stays `generation`.
    #[cfg(feature = "service-profile-triggers-v1")]
    power: Option<i32>,
    temporary_generation: Option<u64>,
    temporary_fan_generation: Option<u64>,
    battery_limit: Option<u8>,
    battery_protection: Option<(bool, u8)>,
    battery_temporary: Option<bool>,
    temporary_capacity: usize,
    temporary_capacity_origin: Option<openblade_domain::temporary_charge_history::Origin>,
    // The part of a live enable's cancellation reservation that a fresh-ID
    // retry needs (`battery_storage::retry_capacity`); it outlives the
    // reserved cancel's Prepared row, which spends the reservation.
    temporary_retry_capacity: usize,
    // The shared thermal convergence ledger, read once at open.
    #[cfg(feature = "service-thermal-convergence-v1")]
    ledger: convergence::Ledger,
}
pub(super) struct Storage {
    same_path: fn(&str, &str) -> bool,
    user_sid: String,
    files: Box<dyn Files>,
    state: Mutex<State>,
    #[cfg(test)]
    battery_append_hook: Mutex<Option<AppendHook>>,
}
#[cfg(test)]
type AppendHook = Arc<dyn Fn(&str) + Send + Sync>;
fn pending_reason_locked(s: &State) -> &'static str {
    if s.last
        .as_ref()
        .is_some_and(|entry| entry.progress >= 1 && !settled(entry.progress))
    {
        "A thermal command may have been submitted. Read-only reconciliation is required and no mutation will be replayed."
    } else if s.intent_append_failed {
        "Thermal submission-intent journal publication could not be confirmed. Pending or published journal evidence is retained. Read-only reconciliation is required and no mutation will be replayed."
    } else if s.preparation_failed {
        "Thermal journal preparation could not be confirmed; storage reconciliation is required. The failed preparation reached no submission intent or thermal setter. Pending or published journal evidence is retained."
    } else if s.last.as_ref().is_some_and(|entry| entry.progress == 0) {
        "Intent stored and journal prepared; no mutation submission intent was reached. Explicit read-only reconciliation is required."
    } else if s.configuration_changed {
        "Saved configuration changed unexpectedly; storage reconciliation is required. No thermal submission is established by this change."
    } else if s.configuration_failed {
        "Saved configuration replacement failed; storage reconciliation is required. No thermal submission is established."
    } else {
        "An earlier thermal submission requires read-only reconciliation."
    }
}
impl Storage {
    #[cfg(test)]
    pub(super) fn open(files: Box<dyn Files>, user_sid: &str) -> io::Result<Arc<Self>> {
        Self::open_with(files, user_sid, str::eq_ignore_ascii_case)
    }
    pub(super) fn open_with(
        files: Box<dyn Files>,
        user_sid: &str,
        same_path: fn(&str, &str) -> bool,
    ) -> io::Result<Arc<Self>> {
        if !user_sid.starts_with("S-1-5-21-")
            || !user_sid
                .bytes()
                .all(|b| b.is_ascii_digit() || b"S-".contains(&b))
        {
            return Err(denied());
        }
        for name in [
            "manual-overrides.json.pending",
            "thermal-mutation.journal.pending",
            "battery-configuration.commit.json.recovery-required",
        ] {
            if files.read(name)?.is_some() {
                return Err(denied());
            }
        }
        // Managed migrates an older aggregate when it loads it. An open that
        // writes no migration keeps this order: the aggregate, then the
        // override document, then the journal. One that does admits those
        // two first, so a refused open does not rewrite the configuration.
        #[cfg(feature = "service-profiles-v1")]
        let migration = profiles::plan_migration(files.as_ref(), user_sid, same_path)?;
        #[cfg(not(feature = "service-profiles-v1"))]
        let migration = None::<()>;
        if migration.is_none() {
            aggregate(files.as_ref(), same_path)?;
        }
        let bytes = files.read(OVERRIDES)?;
        let document = overrides(bytes.as_deref())?;
        let journal = files.read(JOURNAL)?;
        let last =
            crate::thermal_history::rows(journal.as_deref().unwrap_or_default(), |_, _| Ok(()))?.1;
        #[cfg(feature = "service-profiles-v1")]
        if let Some(migration) = migration {
            migration.write(files.as_ref())?;
            aggregate(files.as_ref(), same_path)?;
        }
        #[cfg(feature = "service-profiles-v1")]
        profiles::opened(user_sid);
        // Read last, and never a reason to refuse the open: a ledger that is
        // not authoritative closes convergence only.
        #[cfg(feature = "service-thermal-convergence-v1")]
        let ledger = convergence::Ledger::load(files.as_ref(), user_sid);
        Ok(Arc::new(Self {
            #[cfg(test)]
            battery_append_hook: Mutex::new(None),
            same_path,
            user_sid: user_sid.into(),
            files,
            state: Mutex::new(State {
                quarantined: false,
                configuration_failed: false,
                configuration_changed: false,
                preparation_failed: false,
                intent_append_failed: false,
                bytes,
                document,
                journal,
                last,
                temporary: None,
                temporary_fan: None,
                generation: None,
                #[cfg(feature = "service-profile-triggers-v1")]
                power: None,
                temporary_generation: None,
                temporary_fan_generation: None,
                battery_limit: None,
                battery_protection: None,
                battery_temporary: None,
                temporary_capacity: 0,
                temporary_capacity_origin: None,
                temporary_retry_capacity: 0,
                #[cfg(feature = "service-thermal-convergence-v1")]
                ledger,
            }),
        }))
    }
    pub(super) fn outstanding_fan(&self) -> bool {
        self.state
            .lock()
            .map_or(true, |s| fan_owner_outstanding(s.journal.as_deref()))
    }
    /// Whether native owns HyperBoost (`thermal_history::hyperboost_owned`
    /// over the active journal tail): only then is a HyperBoost reading
    /// OpenBlade's own, which the lifecycle exits leave. A poisoned state or
    /// an unreadable journal reads as not owned, so the reading stays
    /// external and nothing is written. A rollover moves rows into a sealed
    /// segment, so a session whose entry rolled out reads as not owned too.
    #[cfg(all(
        feature = "service-hyperboost-v1",
        feature = "service-profile-triggers-v1"
    ))]
    pub(super) fn owns_hyperboost(&self) -> bool {
        self.state.lock().is_ok_and(|s| {
            crate::thermal_history::hyperboost_owned(s.journal.as_deref().unwrap_or_default())
                .unwrap_or(false)
        })
    }
    /// Settles native HyperBoost ownership with no firmware write, after an
    /// exit's fresh reading found firmware already out of HyperBoost: a
    /// HyperBoost row of `request` Prepared and then KnownNotSubmitted, which
    /// `hyperboost_owned` reads as the end of ownership. Both appends keep
    /// `record`'s admission: the Prepared row the ordinary per-write
    /// authorization and reservation, its retirement the settled-tail checks.
    #[cfg(all(
        feature = "service-hyperboost-v1",
        feature = "service-profile-triggers-v1"
    ))]
    pub(super) fn settle_hyperboost(
        &self,
        request: &str,
        owner: &str,
        caller_pid: u32,
        session: u32,
    ) -> io::Result<()> {
        use sha2::{Digest, Sha256};
        let intent = format!(
            "{:x}",
            Sha256::digest(serde_json::to_vec(&None::<Override>)?)
        );
        for progress in [Progress::Prepared, Progress::KnownNotSubmitted] {
            self.record(
                request,
                owner,
                Destination::HyperBoost,
                &intent,
                &None,
                caller_pid,
                session,
                progress,
            )?;
        }
        Ok(())
    }
    pub(super) fn pending(&self) -> bool {
        self.state.lock().map_or(true, |s| {
            s.quarantined || s.last.as_ref().is_some_and(|e| !settled(e.progress))
        })
    }
    /// Whether a fence may hide a submitted setter (managed
    /// `SubmissionPossible`): an unsettled row past its intent, or an intent
    /// row whose publication was not confirmed. A changed or failed
    /// configuration, a failed preparation and a Prepared-only row reached no
    /// submission (`pending_reason_locked`).
    #[cfg(feature = "service-profile-triggers-v1")]
    pub(super) fn submission_possible(&self) -> bool {
        self.state.lock().map_or(true, |s| {
            s.intent_append_failed
                || s.last
                    .as_ref()
                    .is_some_and(|e| e.progress >= 1 && !settled(e.progress))
        })
    }
    pub(super) fn save(&self, value: Option<Override>, power: i32) -> io::Result<()> {
        if let Some(v) = &value {
            v.validate()?;
        }
        aggregate(self.files.as_ref(), self.same_path)?;
        let mut s = self.state.lock().map_err(|_| denied())?;
        if s.quarantined || s.last.as_ref().is_some_and(|e| !settled(e.progress)) {
            return Err(denied());
        }
        let mut next = s.document.clone();
        let saved = value.as_ref().filter(|v| v.lifetime == 1);
        next["overrides"]["performance"] = saved.map_or(Value::Null, |v| json!(v.value));
        // Managed `PersistentOverrideState` keeps Custom settings only beside
        // a persistent Custom override; validation already paired them.
        next["overrides"]["customPerformance"] = saved
            .and_then(|v| v.custom_performance.clone())
            .unwrap_or(Value::Null);
        next["overrides"]["gpuClockTuning"] = saved
            .and_then(|v| v.gpu_clock_tuning.clone())
            .unwrap_or(Value::Null);
        next["schemaVersion"] = json!(OVERRIDE_SCHEMA);
        let bytes = serde_json::to_vec(&next)?;
        // Accepted intent is published only after atomic protected replacement
        // and independent byte readback. Unrelated overrides remain intact.
        if let Err(error) = self.files.replace(OVERRIDES, s.bytes.as_deref(), &bytes) {
            s.quarantined = true;
            s.configuration_failed = true;
            return Err(error);
        }
        s.bytes = Some(bytes);
        s.document = next;
        s.temporary = value.filter(|v| v.lifetime == 0).map(|v| (v, power));
        s.temporary_generation = s.generation;
        Ok(())
    }
    /// A fresh power class read at Windows power `generation`: keeps, refines
    /// or retires both temporaries (`automation::temporary_after`) before any
    /// read.
    pub(super) fn observe_power(&self, power: i32, generation: u64) -> io::Result<()> {
        let mut s = self.state.lock().map_err(|_| denied())?;
        s.generation = Some(generation);
        #[cfg(feature = "service-profile-triggers-v1")]
        {
            s.power = Some(power);
        }
        let s = &mut *s;
        s.temporary = s.temporary.take().and_then(|(value, saved)| {
            temporary_after(saved, s.temporary_generation, power, generation)
                .map(|next| (value, next))
        });
        s.temporary_fan = s.temporary_fan.take().and_then(|(value, saved)| {
            temporary_after(saved, s.temporary_fan_generation, power, generation)
                .map(|next| (value, next))
        });
        Ok(())
    }
    /// The power class the latest step classified (`observe_power`) while
    /// Windows power is still at that reading's `generation`. A kind change
    /// advances the generation and retires it; a swap between external
    /// classes under unchanged AC presence is not seen here.
    #[cfg(feature = "service-profile-triggers-v1")]
    pub(super) fn observed_power(&self, generation: u64) -> io::Result<Option<i32>> {
        let s = self.state.lock().map_err(|_| denied())?;
        Ok(s.power.filter(|_| s.generation == Some(generation)))
    }
    /// One read of the saved profiles with `foreground` selected in them, for
    /// one request step: every class the step reads resolves from it
    /// (`ProfileLayer::at`). The selection is managed `SetApplicationContext`
    /// for the reported path over the profiles just read, so a profile edit
    /// takes effect at the next read without a new report.
    #[cfg(not(feature = "service-profile-triggers-v1"))]
    pub(super) fn profile_layer(&self, foreground: &Foreground) -> io::Result<ProfileLayer> {
        let set = committed_aggregate(self.files.as_ref(), self.same_path)?
            .map(|committed| typed_profiles(&committed))
            .transpose()?;
        let mut context = foreground.context.clone();
        context.report(
            set.iter().flat_map(|set| {
                set.application_profiles
                    .iter()
                    .map(ApplicationProfile::rule)
            }),
            foreground.reported.as_deref(),
            self.same_path,
        );
        Ok(ProfileLayer { set, context })
    }
    /// One read of the saved profiles with `foreground`'s active pair, for
    /// one request step: every class the step reads resolves from it
    /// (`ProfileLayer::at`). The pair is the one the last report selected;
    /// a read never selects. A pair the saved rules no longer retain applies
    /// no override (`ApplicationContext::retained`), so a store changed by
    /// anything but `retain_application` cannot hand the active id's
    /// overrides to another rule.
    #[cfg(feature = "service-profile-triggers-v1")]
    pub(super) fn profile_layer(&self, foreground: &Foreground) -> io::Result<ProfileLayer> {
        let set = self.typed_set()?;
        let retained = set.as_ref().is_some_and(|set| {
            foreground
                .context
                .retained(rules(set), self.same_path)
                .is_some()
        });
        let context = if retained {
            foreground.context.clone()
        } else {
            ApplicationContext::default()
        };
        Ok(ProfileLayer { set, context })
    }
    #[cfg(feature = "service-profile-triggers-v1")]
    fn typed_set(&self) -> io::Result<Option<ProfileSet>> {
        committed_aggregate(self.files.as_ref(), self.same_path)?
            .map(|committed| typed_profiles(&committed))
            .transpose()
    }
    /// Managed `SetApplicationContext` for one admitted report: the first
    /// enabled saved rule for `reported` becomes the active pair, over the
    /// profiles saved now. Returns the new pair and whether it changed from
    /// `current`. No saved aggregate selects nothing.
    #[cfg(feature = "service-profile-triggers-v1")]
    pub(super) fn select_application(
        &self,
        current: &Foreground,
        reported: Option<&str>,
    ) -> io::Result<(Foreground, bool)> {
        let set = self.typed_set()?;
        let mut next = current.clone();
        let changed = match &set {
            Some(set) => next.context.report(rules(set), reported, self.same_path),
            None => next.context.clear(),
        };
        Ok((next, changed))
    }
    /// Managed `ProfileAutomationState.UpdateProfiles` after a save: the
    /// active pair survives only while an enabled rule with its id still
    /// holds its path (`ApplicationContext::update`). It never selects.
    #[cfg(feature = "service-profile-triggers-v1")]
    pub(super) fn retain_application(&self, current: &Foreground) -> io::Result<Foreground> {
        let mut next = current.clone();
        match self.typed_set()? {
            Some(set) => {
                next.context.update(rules(&set), self.same_path);
            }
            None => {
                next.context.clear();
            }
        }
        Ok(next)
    }
    /// The terms managed `UpdateProfilesAsync` compares around a save, read
    /// at classified class `power` (`SaveIntent::changed`), without an
    /// adapter reading (`unexpired`).
    #[cfg(feature = "service-profile-triggers-v1")]
    pub(super) fn save_intent(
        &self,
        foreground: &Foreground,
        power: i32,
    ) -> io::Result<SaveIntent> {
        self.unexpired(|| self.save_intent_at(foreground, power))
    }
    /// Runs `read` over saved intent at classes no adapter reading
    /// established, so it retires no temporary: the override reads expire one
    /// kept for another class, and that state is put back afterwards. Give
    /// each class its own scope, as `save_intent` does: a read at one class
    /// expires the temporary another class in the same scope would select.
    #[cfg(feature = "service-profile-triggers-v1")]
    pub(super) fn unexpired<T>(&self, read: impl FnOnce() -> io::Result<T>) -> io::Result<T> {
        let kept = {
            let s = self.state.lock().map_err(|_| denied())?;
            (s.temporary.clone(), s.temporary_fan.clone())
        };
        let result = read();
        let mut s = self.state.lock().map_err(|_| denied())?;
        (s.temporary, s.temporary_fan) = kept;
        result
    }
    #[cfg(feature = "service-profile-triggers-v1")]
    fn save_intent_at(&self, foreground: &Foreground, power: i32) -> io::Result<SaveIntent> {
        let layer = self.profile_layer(foreground)?;
        let profile = layer.at(power)?;
        let desired = match profile_layer(profile.as_ref(), power) {
            None => None,
            Some(resolution) => {
                let intent = application::desired_intent(
                    resolution,
                    openblade_domain::profile_set::Capabilities::rz09_0581()
                        .supports_manual_fans_on_limited_power,
                )
                .map_err(|invalid| io::Error::new(io::ErrorKind::InvalidData, invalid.0))?;
                // An explicit override decides its members whatever the
                // profile says, so a profile edit under it is no change.
                let performance_overridden = self.current_override(power)?.is_some();
                let fans_overridden = self.current_fan_override(power)?.is_some();
                Some(SaveDesired {
                    performance: (!performance_overridden).then_some(intent.performance),
                    custom_performance: intent
                        .custom_performance
                        .filter(|_| !performance_overridden),
                    gpu_clock_tuning: intent.gpu_clock_tuning.filter(|_| !performance_overridden),
                    fans: (!fans_overridden).then(|| (intent.fans, intent.fan_control.clone())),
                    lighting: (intent.lighting.value.cloned(), intent.lighting.source),
                    battery_policy: intent.battery_policy,
                    battery_protection_enabled: intent.battery_protection_enabled,
                    destination: self.desired(power, Some(resolution))?,
                    fan: self.desired_fan(power, Some(resolution))?,
                })
            }
        };
        Ok(SaveIntent {
            application_profile_id: layer.context.active().map(|a| a.id.to_owned()),
            desired,
            laptop_behavior: profile.as_ref().map(|r| r.laptop_behavior),
        })
    }
    /// The saved destination for class `power`, with its provenance. Custom
    /// settings the model does not admit and unadmitted presets select
    /// nothing; a profile's Custom without levels selects managed's default
    /// levels (`Selected::profile`). `profile` is the domain resolution at
    /// `power` (`ProfileLayer::at`).
    pub(super) fn desired(
        &self,
        power: i32,
        profile: Option<&Resolution<'_>>,
    ) -> io::Result<Option<(Destination, i32)>> {
        Ok(self
            .selected(power, profile)?
            .and_then(|s| s.destination.map(|d| (d, s.source))))
    }
    /// The composition managed `Resolve` layers for performance: a
    /// persistent override (source 4), then a temporary one (3), then the
    /// resolved profile, from the active application (2) when its override
    /// sets the preset and from the power profile (1) otherwise. Native keeps
    /// no recovered HyperBoost layer: it never selects HyperBoost, and the
    /// HyperBoost exit to a battery preset is refused as `Plan::NoDirectPath`
    /// (or, with the exit feature, runs the restore sequence first).
    fn selected(
        &self,
        power: i32,
        profile: Option<&Resolution<'_>>,
    ) -> io::Result<Option<Selected>> {
        let mut s = self.state.lock().map_err(|_| denied())?;
        if s.temporary
            .as_ref()
            .is_some_and(|(_, p)| temporary_expired(*p, power))
        {
            s.temporary = None;
        }
        let overrides = &s.document["overrides"];
        let persistent = overrides["performance"]
            .as_i64()
            .map(|n| (n as i32, Some(&overrides["customPerformance"])));
        let temporary = s
            .temporary
            .as_ref()
            .map(|(v, _)| (v.value, v.custom_performance.as_ref()));
        for (value, source) in [(persistent, 4), (temporary, 3)] {
            if let Some((value, custom)) = value
                && openblade_domain::automation::override_source_eligible(value, power)
            {
                return Ok(Some(Selected::saved(value, custom, source)));
            }
        }
        drop(s);
        let Some(resolution) = profile_layer(profile, power) else {
            return Ok(None);
        };
        let value = resolution.profile.performance.0;
        if !openblade_domain::automation::performance_source_eligible(value, power) {
            return Ok(None);
        }
        // Managed `ApplicationProfileResolution`: an application's Custom
        // settings replace the power profile's, whichever selected Custom.
        // The provenance names the application when its override sets the
        // preset, Custom settings or GPU tuning, as managed `Resolve` does
        // (`DesiredIntent::performance`): the managed vector case "an
        // override of only custom levels or only gpu tuning is an application
        // source" (profile-resolution-vectors.json) records source 2 for an
        // override that sets only Custom levels or only GPU tuning. Every
        // composition reports it, with or without request triggers.
        let source = if resolution.application_override().is_some_and(|o| {
            o.performance.is_some()
                || o.custom_performance.is_some()
                || o.gpu_clock_tuning.is_some()
        }) {
            2
        } else {
            1
        };
        Ok(Some(Selected::profile(&resolution.profile, source)))
    }
    /// The selected policy is Custom with settings outside the RZ09-0581
    /// capability, which managed `ApplyPerformanceOutcomeAsync` reports as
    /// unsupported without a write.
    pub(super) fn custom_unsupported(
        &self,
        power: i32,
        profile: Option<&Resolution<'_>>,
    ) -> io::Result<bool> {
        Ok(self
            .selected(power, profile)?
            .is_some_and(|s| s.value == CUSTOM && s.destination.is_none()))
    }
    /// Whether the saved policy at class `power` carries a GPU clock effect,
    /// which has no native writer: an override's tuning, legacy offsets in the
    /// selected Custom settings, or the resolved profile's tuning.
    pub(super) fn gpu_incomplete(
        &self,
        power: i32,
        profile: Option<&Resolution<'_>>,
    ) -> io::Result<bool> {
        let mut s = self.state.lock().map_err(|_| denied())?;
        // The same class expiry as `desired`, so the answer does not depend on
        // which of the two a caller reads first.
        if s.temporary
            .as_ref()
            .is_some_and(|(_, p)| temporary_expired(*p, power))
        {
            s.temporary = None;
        }
        if !s.document["overrides"]["gpuClockTuning"].is_null()
            || s.temporary
                .as_ref()
                .is_some_and(|(v, _)| v.gpu_clock_tuning.is_some())
        {
            return Ok(true);
        }
        drop(s);
        if self
            .selected(power, profile)?
            .is_some_and(|s| s.legacy_offsets.is_some())
        {
            return Ok(true);
        }
        if profile.is_none() {
            return Ok(false);
        }
        Ok(match power {
            // Unclassified external power selects no profile: only the
            // explicit overrides checked above can carry a GPU effect.
            UNCLASSIFIED_EXTERNAL => false,
            _ => profile_layer(profile, power).is_none_or(|r| r.profile.gpu_clock_tuning.is_some()),
        })
    }
    /// The GPU clock tuning managed `ProfileAutomationState.Resolve` selects
    /// at class `power`, in the saved override's JSON form: on a classified
    /// adapter only (`PowerSourcePolicy.IsExternal`), the active persistent
    /// override's tuning, then the active temporary one's, then the resolved
    /// profile's (an application's replaces its power profile's), and else,
    /// for a selected Custom, its legacy offsets as enabled tuning (managed
    /// `NormalizeLegacyGpuClockTuning`). An override is active where its
    /// preset is (`override_source_eligible`), whichever layer supplies the
    /// preset, so a profile's tuning applies beneath an override's preset.
    #[cfg(any(
        feature = "service-performance-shortcut-v1",
        feature = "service-gpu-clock-tuning-v1"
    ))]
    pub(super) fn gpu_tuning(
        &self,
        power: i32,
        profile: Option<&Resolution<'_>>,
    ) -> io::Result<Option<Value>> {
        // `PluggedIn` and `UsbC`; battery and unclassified power select none.
        if !matches!(power, 0 | 2) {
            return Ok(None);
        }
        let mut s = self.state.lock().map_err(|_| denied())?;
        // The same class expiry as `desired`.
        if s.temporary
            .as_ref()
            .is_some_and(|(_, p)| temporary_expired(*p, power))
        {
            s.temporary = None;
        }
        let eligible =
            |value: i32| openblade_domain::automation::override_source_eligible(value, power);
        let overrides = &s.document["overrides"];
        let persistent = overrides["performance"]
            .as_i64()
            .filter(|value| eligible(*value as i32))
            .map(|_| &overrides["gpuClockTuning"])
            .filter(|tuning| !tuning.is_null());
        let temporary = s
            .temporary
            .as_ref()
            .filter(|(v, _)| eligible(v.value))
            .and_then(|(v, _)| v.gpu_clock_tuning.as_ref());
        if let Some(tuning) = persistent.or(temporary) {
            return Ok(Some(tuning.clone()));
        }
        drop(s);
        if let Some(tuning) = profile_layer(profile, power).and_then(|r| r.profile.gpu_clock_tuning)
        {
            return Ok(Some(json!({
                "enabled": tuning.enabled,
                "offsets": tuning.offsets.map(offsets_value),
            })));
        }
        Ok(self
            .selected(power, profile)?
            .filter(|s| s.value == CUSTOM)
            .and_then(|s| s.legacy_offsets)
            .map(|offsets| json!({"enabled": true, "offsets": offsets})))
    }
    pub(super) fn pending_reason(&self) -> io::Result<&'static str> {
        let s = self.state.lock().map_err(|_| {
            super::thermal_mutation::fatal_admission("Thermal storage state is poisoned.")
        })?;
        Ok(pending_reason_locked(&s))
    }

    pub(super) fn quarantine_settlement(&self) -> io::Result<()> {
        self.state
            .lock()
            .map_err(|_| {
                super::thermal_mutation::fatal_admission("Thermal storage state is poisoned.")
            })?
            .quarantined = true;
        Ok(())
    }
    pub(super) fn configuration_failed(&self) -> bool {
        self.state.lock().map_or(true, |s| s.configuration_failed)
    }
    pub(super) fn current_override(&self, power: i32) -> io::Result<Option<Override>> {
        let mut s = self.state.lock().map_err(|_| denied())?;
        if self.files.read(OVERRIDES)? != s.bytes {
            s.quarantined = true;
            s.configuration_changed = true;
            return Err(denied());
        }
        if s.temporary
            .as_ref()
            .is_some_and(|(_, p)| temporary_expired(*p, power))
        {
            s.temporary = None;
        }
        if let Some((v, _)) = &s.temporary {
            return Ok(Some(v.clone()));
        }
        let saved = &s.document["overrides"];
        Ok(saved["performance"].as_i64().map(|n| Override {
            value: n as i32,
            lifetime: 1,
            custom_performance: saved
                .get("customPerformance")
                .filter(|v| !v.is_null())
                .cloned(),
            gpu_clock_tuning: saved
                .get("gpuClockTuning")
                .filter(|v| !v.is_null())
                .cloned(),
        }))
    }
    pub(super) fn save_fan(
        &self,
        value: Option<crate::service_status::FanOverride>,
        settings: Option<Value>,
        power: i32,
    ) -> io::Result<()> {
        openblade_domain::fan_history::validate_requested(
            &json!({"value":value,"settings":settings}),
        )?;
        aggregate(self.files.as_ref(), self.same_path)?;
        let mut s = self.state.lock().map_err(|_| denied())?;
        if s.quarantined || s.last.as_ref().is_some_and(|e| !settled(e.progress)) {
            return Err(denied());
        }
        let resolved = value.as_ref().map(|v| resolved_fan(v, settings));
        let saved = value.as_ref().is_some_and(|v| v.lifetime == 1);
        let mut next = s.document.clone();
        next["overrides"]["fans"] = if saved {
            json!(value.as_ref().unwrap().value)
        } else {
            Value::Null
        };
        next["overrides"]["fanControl"] = if saved {
            resolved.clone().unwrap()
        } else {
            Value::Null
        };
        next["schemaVersion"] = json!(OVERRIDE_SCHEMA);
        let bytes = serde_json::to_vec(&next)?;
        if let Err(e) = self.files.replace(OVERRIDES, s.bytes.as_deref(), &bytes) {
            s.quarantined = true;
            s.configuration_failed = true;
            return Err(e);
        }
        s.bytes = Some(bytes);
        s.document = next;
        s.temporary_fan = if !saved {
            resolved.map(|v| (v, power))
        } else {
            None
        };
        s.temporary_fan_generation = s.generation;
        Ok(())
    }
    /// The saved performance override managed validates a fan request
    /// against (`Automation.PerformanceOverride`): the persistent value, or an
    /// unexpired temporary one, without the source eligibility that `selected`
    /// applies. A saved Silent on battery is therefore still Silent here.
    pub(super) fn current_performance_override(&self, power: i32) -> io::Result<Option<i32>> {
        let s = self.state.lock().map_err(|_| denied())?;
        if let Some(n) = s.document["overrides"]["performance"].as_i64() {
            return Ok(Some(n as i32));
        }
        Ok(s.temporary
            .as_ref()
            .filter(|(_, p)| !temporary_expired(*p, power))
            .map(|(v, _)| v.value))
    }
    pub(super) fn current_fan_override(
        &self,
        power: i32,
    ) -> io::Result<Option<crate::service_status::FanOverride>> {
        let s = self.state.lock().map_err(|_| denied())?;
        if let Some((v, p)) = &s.temporary_fan
            && !temporary_expired(*p, power)
        {
            return Ok(Some(crate::service_status::FanOverride {
                value: v["mode"].as_u64().ok_or_else(denied)? as u8,
                lifetime: 0,
            }));
        }
        Ok(s.document["overrides"]["fans"].as_u64().map(|mode| {
            crate::service_status::FanOverride {
                value: mode as u8,
                lifetime: 1,
            }
        }))
    }
    /// The check `save` makes before it replaces the override document, for
    /// a reconciliation that stores nothing: a document changed outside the
    /// Service fences writes (`configuration_changed`) before any submission.
    pub(super) fn verify_overrides(&self) -> io::Result<()> {
        let mut s = self.state.lock().map_err(|_| denied())?;
        if s.quarantined || s.last.as_ref().is_some_and(|e| !settled(e.progress)) {
            return Err(denied());
        }
        if self.files.read(OVERRIDES)? != s.bytes {
            s.quarantined = true;
            s.configuration_changed = true;
            return Err(denied());
        }
        Ok(())
    }
    // Integrity remains mandatory for release; mutable policy equality is not.
    pub(super) fn verify_fan_configuration(&self) -> io::Result<()> {
        aggregate(self.files.as_ref(), self.same_path)?;
        let mut s = self.state.lock().map_err(|_| denied())?;
        if self.files.read(OVERRIDES)? != s.bytes {
            s.quarantined = true;
            s.configuration_changed = true;
        }
        if s.quarantined {
            return Err(denied());
        }
        Ok(())
    }
    /// What a `save_fan` of `value` and `settings` at class `power` leaves,
    /// read before it commits: whether the saved intent it replaces is manual
    /// (`desired_manual_fans`), and the settings `desired_fan` then selects.
    /// Every check of that later read but the override document's, which the
    /// save itself replaces, runs here, so a request whose resulting fan
    /// settings the domain decoder refuses stores nothing.
    pub(super) fn plan_fan_save(
        &self,
        value: Option<&crate::service_status::FanOverride>,
        settings: Option<&Value>,
        power: i32,
        profile: Option<&Resolution<'_>>,
    ) -> io::Result<FanSave> {
        openblade_domain::fan_history::validate_requested(
            &json!({"value":value,"settings":settings}),
        )?;
        let leaving_manual = self.desired_manual_fans(power, profile)?;
        let settings = match value {
            // A temporary override is selected as resolved; a persistent one
            // is admitted as `desired_fan` admits the saved document.
            Some(v) => {
                let resolved = resolved_fan(v, settings.cloned());
                if v.lifetime == 1 {
                    openblade_domain::fan_history::validate_settings(&resolved)?;
                }
                Some(resolved)
            }
            None => profile_fan(power, profile)?,
        };
        Ok(FanSave {
            leaving_manual,
            settings,
        })
    }
    pub(super) fn desired_fan(
        &self,
        power: i32,
        profile: Option<&Resolution<'_>>,
    ) -> io::Result<Option<Value>> {
        let mut s = self.state.lock().map_err(|_| denied())?;
        if self.files.read(OVERRIDES)? != s.bytes {
            s.quarantined = true;
            s.configuration_changed = true;
            return Err(denied());
        }
        if s.temporary_fan
            .as_ref()
            .is_some_and(|(_, p)| temporary_expired(*p, power))
        {
            s.temporary_fan = None;
        }
        if let Some((v, _)) = &s.temporary_fan {
            return Ok(Some(v.clone()));
        }
        if !s.document["overrides"]["fanControl"].is_null() {
            // Stored intent may exceed the fan write bound; admit it here.
            let selected = s.document["overrides"]["fanControl"].clone();
            openblade_domain::fan_history::validate_settings(&selected)?;
            return Ok(Some(selected));
        }
        drop(s);
        profile_fan(power, profile)
    }
    pub(super) fn record_fan(
        &self,
        mut entry: Entry,
        cleanup: Option<CleanupScope>,
    ) -> io::Result<()> {
        let mut s = self.state.lock().map_err(|_| denied())?;
        if s.quarantined {
            return Err(denied());
        }
        entry.user_sid = self.user_sid.clone();
        let record = entry.fan.as_ref().ok_or_else(denied)?;
        record.validate()?;
        if entry.progress == 6
            && (s.last.as_ref().is_none_or(|p| p.progress != 0)
                || self
                    .files
                    .read("thermal-mutation.journal.pending")?
                    .is_some()
                || self.files.read(JOURNAL)? != s.journal)
        {
            return Err(denied());
        }
        if entry.progress == 0 {
            if s.last.as_ref().is_some_and(|e| !settled(e.progress)) {
                return Err(denied());
            }
            // Full manual operation plus owned Stop release, including two-digit
            // progress values, must fit without rollover while ownership is live.
            let mut largest = entry.clone();
            largest.progress = 50;
            let row = serde_json::to_vec(&largest)?.len() + 1;
            let rows = if record.intent.manual() {
                8 + 2 * record.setters()
            } else {
                2 + 2 * record.setters()
            };
            if row > crate::thermal_history::ROW {
                return Err(denied());
            }
            let mut held = None;
            crate::thermal_history::rows(s.journal.as_deref().unwrap_or_default(), |e, _| {
                if e.fan.is_some() {
                    if e.progress == 30 {
                        held = Some(e.clone());
                    } else if matches!(e.progress, 50 | 51) {
                        held = None;
                    }
                }
                Ok(())
            })?;
            let retained = if let Some(mut old) = held {
                old.progress = 50;
                (serde_json::to_vec(&old)?.len() + 1)
                    .checked_mul(6)
                    .ok_or_else(denied)?
            } else {
                0
            };
            let needed = row
                .checked_mul(rows)
                .and_then(|n| n.checked_add(retained))
                .ok_or_else(denied)?;
            if s.temporary_capacity > 0
                && s.journal
                    .as_deref()
                    .unwrap_or_default()
                    .len()
                    .checked_add(needed)
                    .and_then(|n| n.checked_add(s.temporary_capacity))
                    .is_none_or(|n| n > crate::thermal_history::SEGMENT)
            {
                return Err(crate::thermal_history::capacity_refusal());
            }
            match self.files.reserve(
                s.journal.as_deref(),
                needed
                    .checked_add(s.temporary_capacity)
                    .ok_or_else(denied)?,
            ) {
                Ok(j) => s.journal = j,
                Err(e) if crate::thermal_history::known_capacity_refusal(&e) => return Err(e),
                Err(e) => {
                    s.quarantined = true;
                    s.preparation_failed = true;
                    return Err(e);
                }
            }
        }
        let mut bytes = s.journal.clone().unwrap_or_default();
        bytes.extend(serde_json::to_vec(&entry)?);
        bytes.push(b'\n');
        crate::thermal_history::rows(&bytes, |_, _| Ok(()))?;
        let result = if entry.progress == 6 {
            self.files
                .replace_retired(JOURNAL, s.journal.as_deref(), &bytes)
        } else if let Some(scope) = cleanup {
            self.files
                .replace_cleanup(JOURNAL, scope, s.journal.as_deref(), &bytes)
        } else {
            self.files.replace(JOURNAL, s.journal.as_deref(), &bytes)
        };
        if let Err(e) = result {
            s.quarantined = true;
            s.preparation_failed = entry.progress == 0;
            s.intent_append_failed = matches!(entry.progress, 10 | 12 | 14 | 16 | 40 | 42);
            return Err(e);
        }
        s.journal = Some(bytes);
        s.last = Some(entry);
        Ok(())
    }
    /// A digest of every input saved-policy resolution reads: the protected
    /// override and configuration files, the override bytes this storage
    /// accepted, and both temporaries. Resolution is a function of these and
    /// of the class and application, so equal digests resolve equally and an
    /// operation need not parse them again (`FanPermit::authorize`).
    pub(super) fn policy_inputs(&self) -> io::Result<[u8; 32]> {
        use sha2::{Digest, Sha256};
        fn push(digest: &mut Sha256, bytes: Option<&[u8]>) {
            match bytes {
                None => digest.update([0]),
                Some(bytes) => {
                    digest.update([1]);
                    digest.update((bytes.len() as u64).to_le_bytes());
                    digest.update(bytes);
                }
            }
        }
        let mut digest = Sha256::new();
        for name in [
            OVERRIDES,
            configuration::RECOVERY_REQUIRED,
            configuration::PROFILE,
            configuration::PREFERENCES,
            configuration::COMMIT,
        ] {
            push(&mut digest, self.files.read(name)?.as_deref());
        }
        let s = self.state.lock().map_err(|_| denied())?;
        push(&mut digest, s.bytes.as_deref());
        let temporaries = serde_json::to_vec(&(&s.temporary, &s.temporary_fan))?;
        push(&mut digest, Some(&temporaries));
        Ok(digest.finalize().into())
    }
    /// The power profile's own performance and fan mode for class `power`, as
    /// managed `ValidateThermalOverrideCombination` reads them from
    /// `Automation.Profiles` (application profiles take no part), or managed
    /// `ProfileDefaults.CreateSafeDefaults` when no aggregate is stored.
    pub(super) fn profile_thermal(&self, power: i32) -> io::Result<Option<(i32, i32)>> {
        let key = match power {
            0 => "pluggedIn",
            1 => "battery",
            2 => "usbC",
            _ => return Ok(None),
        };
        let Some(doc) = aggregate(self.files.as_ref(), self.same_path)? else {
            let defaults = openblade_domain::profile_set::ProfileSet::safe_defaults();
            let profile = match power {
                0 => defaults.plugged_in,
                1 => defaults.battery,
                _ => defaults.usb_c,
            };
            return Ok(Some((profile.performance.0, profile.fans.0)));
        };
        let profile = &doc["profiles"][key];
        let performance = profile["performance"].as_i64().ok_or_else(denied)?;
        let fans = profile["fans"].as_i64().ok_or_else(denied)?;
        Ok(Some((performance as i32, fans as i32)))
    }
    /// The power profile's own Custom settings for classified class `power`,
    /// as a performance shortcut that selects Custom reads them
    /// (`Automation.Profiles.ForPowerSource(source).CustomPerformance`), or
    /// managed `ProfileDefaults.CreateSafeDefaults`'s when no aggregate is
    /// stored. `None` is a profile without Custom settings.
    #[cfg(feature = "service-performance-shortcut-v1")]
    pub(super) fn profile_custom_performance(&self, power: i32) -> io::Result<Option<Value>> {
        let key = match power {
            0 => "pluggedIn",
            1 => "battery",
            2 => "usbC",
            _ => return Ok(None),
        };
        let Some(doc) = aggregate(self.files.as_ref(), self.same_path)? else {
            let defaults = openblade_domain::profile_set::ProfileSet::safe_defaults();
            let profile = match power {
                0 => defaults.plugged_in,
                1 => defaults.battery,
                _ => defaults.usb_c,
            };
            return Ok(profile.custom_performance.map(|custom| {
                serde_json::json!({
                    "cpu": custom.cpu.0,
                    "gpu": custom.gpu.0,
                    "maximumFans": custom.maximum_fans,
                    "gpuClockOffsets": custom.gpu_clock_offsets.map(|offsets| serde_json::json!({
                        "coreMegahertz": offsets.core_megahertz,
                        "memoryMegahertz": offsets.memory_megahertz,
                    })),
                })
            }));
        };
        Ok(doc["profiles"][key]
            .get("customPerformance")
            .filter(|custom| !custom.is_null())
            .cloned())
    }
    /// The wire value managed `Automation.Resolve(..).Performance` selects
    /// at classified class `power`, whether or not it names a native
    /// destination: `desired`'s selection, then, when no override applies
    /// and no profile resolution was read, the power profile's preset
    /// (`profile_thermal`, managed `ProfileDefaults.CreateSafeDefaults`'s
    /// when no aggregate is stored).
    /// `profile` is the domain resolution at `power` (`ProfileLayer::at`).
    #[cfg(feature = "service-performance-shortcut-v1")]
    pub(super) fn selected_performance(
        &self,
        power: i32,
        profile: Option<&Resolution<'_>>,
    ) -> io::Result<Option<i32>> {
        if let Some(selected) = self.selected(power, profile)? {
            return Ok(Some(selected.value));
        }
        if profile.is_some() {
            return Ok(None);
        }
        Ok(self
            .profile_thermal(power)?
            .map(|(performance, _)| performance))
    }
    /// Whether saved fan intent at class `power` is manual: an unexpired
    /// temporary fan override, then a persistent one, then the resolved
    /// profile's mode, which is the application's fan mode or fan control
    /// mode when its override sets one (`resolve::apply`).
    pub(super) fn desired_manual_fans(
        &self,
        power: i32,
        profile: Option<&Resolution<'_>>,
    ) -> io::Result<bool> {
        Ok(self.desired_fan_mode(power, profile)? != FanMode::AUTOMATIC)
    }
    /// The saved fan mode at class `power`, in `desired_manual_fans`'s
    /// order. A temporary override without a numeric mode names no defined
    /// mode, so it is manual with no native recipe.
    pub(super) fn desired_fan_mode(
        &self,
        power: i32,
        profile: Option<&Resolution<'_>>,
    ) -> io::Result<FanMode> {
        let undefined = FanMode(-1);
        let s = self.state.lock().map_err(|_| denied())?;
        if let Some((v, p)) = &s.temporary_fan
            && !temporary_expired(*p, power)
        {
            return Ok(v["mode"]
                .as_i64()
                .and_then(|mode| i32::try_from(mode).ok())
                .map_or(undefined, FanMode));
        }
        if let Some(mode) = s.document["overrides"]["fans"].as_i64() {
            return Ok(i32::try_from(mode).map_or(undefined, FanMode));
        }
        drop(s);
        Ok(profile_layer(profile, power).map_or(FanMode::AUTOMATIC, |r| r.profile.fans))
    }
    pub(super) fn profiles(&self) -> io::Result<Option<Value>> {
        Ok(aggregate(self.files.as_ref(), self.same_path)?.map(|v| v["profiles"].clone()))
    }
    /// The request id of the latest journal row.
    #[cfg(feature = "service-profile-triggers-v1")]
    pub(super) fn last_request(&self) -> Option<String> {
        self.state
            .lock()
            .ok()?
            .last
            .as_ref()
            .map(|e| e.request.clone())
    }
    pub(super) fn progress(&self) -> Option<u8> {
        self.state.lock().ok()?.last.as_ref().map(|e| e.progress)
    }
    // Every caller, including the shared mutation tests, passes the journal fields individually.
    #[allow(clippy::too_many_arguments)]
    pub(super) fn record(
        &self,
        request: &str,
        owner: &str,
        destination: Destination,
        intent: &str,
        requested: &Option<Override>,
        caller_pid: u32,
        session: u32,
        progress: Progress,
    ) -> io::Result<()> {
        let mut s = self.state.lock().map_err(|_| denied())?;
        if s.quarantined {
            return Err(denied());
        }
        // The history refuses a performance row while a fan change this
        // Service owns is outstanding (`thermal_history`), so such a row is
        // refused before it is written rather than leaving the journal
        // unreadable. A request's later rows follow its Prepared row, and no
        // fan owner can open in between.
        if progress == Progress::Prepared && fan_owner_outstanding(s.journal.as_deref()) {
            return Err(denied());
        }
        if progress == Progress::KnownNotSubmitted
            && (s.last.as_ref().is_none_or(|e| e.progress != 0)
                || self
                    .files
                    .read("thermal-mutation.journal.pending")?
                    .is_some()
                || self.files.read(JOURNAL)? != s.journal)
        {
            return Err(denied());
        }
        if let Some(prior) = &s.last {
            if prior.request == request {
                if prior.owner != owner
                    || prior.user_sid != self.user_sid
                    || prior.caller_pid != caller_pid
                    || prior.session != session
                    || prior.intent != intent
                    || prior.destination != destination.wire()
                    || !progress_follows(destination.wire(), prior.progress, progress as u8)
                {
                    return Err(denied());
                }
            } else if !settled(prior.progress) || progress != Progress::Prepared {
                return Err(denied());
            }
        } else if progress != Progress::Prepared {
            return Err(denied());
        }
        let entry = Entry {
            tfc: None,
            battery: None,
            fan: None,
            request: request.into(),
            owner: owner.into(),
            user_sid: self.user_sid.clone(),
            caller_pid,
            session,
            requested: requested.clone(),
            destination: destination.wire(),
            intent: intent.into(),
            progress: progress as u8,
        };
        if progress == Progress::Prepared {
            // Reserve the exact request's largest legal branch, its whole chain
            // (0..5, Custom's 0..4, 7..10, 5, or a battery preset's HyperBoost
            // restore chain 0, 80..83, 1..5). The Prepared row's progress is
            // one digit; each step adds its own extra digits. The 0->6
            // retirement path is shorter.
            let row = serde_json::to_vec(&entry)?
                .len()
                .checked_add(1)
                .ok_or_else(denied)?;
            let chain = Progress::longest_chain(destination.wire()).ok_or_else(denied)?;
            let extra = |step: &Progress| (*step as u8).to_string().len() - 1;
            if chain
                .iter()
                .any(|step| row + extra(step) > crate::thermal_history::ROW)
            {
                return Err(denied());
            }
            let needed = chain
                .iter()
                .try_fold(0usize, |total, step| total.checked_add(row + extra(step)))
                .and_then(|n| n.checked_add(s.temporary_capacity))
                .ok_or_else(denied)?;
            // A live process-scoped TFC origin and its reserved cancellation
            // must stay together. Performance cannot spend or roll this quota.
            if s.temporary_capacity > 0
                && s.journal
                    .as_deref()
                    .unwrap_or_default()
                    .len()
                    .checked_add(needed)
                    .is_none_or(|n| n > crate::thermal_history::SEGMENT)
            {
                return Err(crate::thermal_history::capacity_refusal());
            }
            match self.files.reserve(s.journal.as_deref(), needed) {
                Ok(journal) => s.journal = journal,
                Err(error) if crate::thermal_history::known_capacity_refusal(&error) => {
                    return Err(error);
                }
                Err(error) => {
                    s.quarantined = true;
                    s.preparation_failed = true;
                    return Err(error);
                }
            }
        }
        let mut bytes = s.journal.clone().unwrap_or_default();
        bytes.extend(serde_json::to_vec(&entry)?);
        bytes.push(b'\n');
        if bytes.len() > 1024 * 1024 {
            return Err(io::Error::new(
                io::ErrorKind::StorageFull,
                "Thermal journal capacity exhausted; no further progress was recorded.",
            ));
        }
        // Settling a Prepared-only row as KnownNotSubmitted is the retirement
        // custody admits without a live lifecycle epoch, as the fan journal
        // does: the low-power gate or a stop that ends the request must not
        // strand its own Prepared row. Every other append keeps the ordinary
        // per-write authorization.
        let result = if progress == Progress::KnownNotSubmitted {
            self.files
                .replace_retired(JOURNAL, s.journal.as_deref(), &bytes)
        } else {
            self.files.replace(JOURNAL, s.journal.as_deref(), &bytes)
        };
        if let Err(error) = result {
            s.quarantined = true;
            // Only a new Prepared append establishes this local failure cause.
            // Staging or publication may already have left durable bytes.
            // Existing unresolved history cannot reach this append.
            s.preparation_failed = progress == Progress::Prepared;
            // An unsuccessful intent append may already be pending or published.
            // Cached Prepared is not proof that no intent evidence exists.
            s.intent_append_failed = progress.is_intent();
            return Err(error);
        }
        s.journal = Some(bytes);
        s.last = Some(entry);
        Ok(())
    }
}

// This reader never migrates or writes the aggregate; with service-profiles-v1
// the open has already migrated an older one (profiles::plan_migration).
// Validate its durable envelope before touching overrides, and consume only
// explicitly represented performance fields. Missing configuration does not
// materialize or activate a default.
fn aggregate(files: &dyn Files, same_path: fn(&str, &str) -> bool) -> io::Result<Option<Value>> {
    Ok(committed_aggregate(files, same_path)?.map(|committed| committed.document))
}

/// An aggregate `aggregate` admitted, with the committed `profiles.json` bytes
/// it was read from and their schema.
struct CommittedAggregate {
    document: Value,
    profile: Vec<u8>,
    schema: i32,
}

fn committed_aggregate(
    files: &dyn Files,
    same_path: fn(&str, &str) -> bool,
) -> io::Result<Option<CommittedAggregate>> {
    if files.read(configuration::RECOVERY_REQUIRED)?.is_some() {
        return Err(denied());
    }
    let profile = files.read(configuration::PROFILE)?;
    let preferences = files.read(configuration::PREFERENCES)?;
    let commit = files.read(configuration::COMMIT)?;
    if profile.is_none() && preferences.is_none() && commit.is_none() {
        return Ok(None);
    }
    fn file(v: Option<&[u8]>) -> ConfigFile<'_> {
        v.map_or(ConfigFile::Missing, ConfigFile::Bytes)
    }
    // Schemas 25 and 26 are read as managed ProfileStore reads them: lighting
    // members newer than the document are rejected, and schema 26 also gets the
    // PreserveLegacyIndependentProfileIntent normalisation below. Schema 24 and
    // older are refused: with service-profiles-v1 the open migrated them first,
    // so they reach this only on other lanes or when that migration was not
    // written.
    let Inspection::CommittedEnvelope {
        schema: schema @ 25..=configuration::CURRENT_SCHEMA,
    } = configuration::inspect(configuration::Snapshot {
        profile: file(profile.as_deref()),
        preferences: file(preferences.as_deref()),
        commit: file(commit.as_deref()),
        recovery: RecoveryRecord::Missing,
    })
    else {
        return Err(io::Error::new(
            io::ErrorKind::Unsupported,
            "Uncommitted or non-current profile aggregate is preserved; managed recovery or migration is required.",
        ));
    };
    let mut doc = decode(profile.as_deref().ok_or_else(denied)?)?;
    let prefs = decode(preferences.as_deref().ok_or_else(denied)?)?;
    if [&doc, &prefs]
        .into_iter()
        .any(|document| configuration::legacy_lighting_intent(document, schema < 27, schema < 26))
    {
        return Err(denied());
    }
    let profiles = doc["profiles"].as_object().ok_or_else(denied)?;
    for key in ["pluggedIn", "battery", "usbC"] {
        let p = profiles
            .get(key)
            .and_then(Value::as_object)
            .ok_or_else(denied)?;
        if !matches!(p.get("performance").and_then(Value::as_i64), Some(0..=9))
            || !matches!(p.get("fans").and_then(Value::as_i64), Some(0..=3))
        {
            return Err(denied());
        }
    }
    if let Some(apps) = profiles.get("applicationProfiles") {
        let apps = apps.as_array().ok_or_else(denied)?;
        if apps.len() > 128 {
            return Err(denied());
        }
        let mut paths: Vec<String> = vec![];
        let mut ids = std::collections::BTreeSet::new();
        for app in apps {
            let id = app["id"].as_str().ok_or_else(denied)?;
            let path = app["executablePath"].as_str().ok_or_else(denied)?;
            if !crate::service_frame::is_guid(id)
                || !ids.insert(id.to_ascii_lowercase())
                || paths.iter().any(|saved| same_path(saved, path))
                || !valid_application_path(path)
                || !app["enabled"].is_boolean()
                || !matches!(app["activationMode"].as_i64(), Some(0..=1))
                || !app["overrides"].is_object()
            {
                return Err(denied());
            }
            paths.push(path.to_owned());
            for key in ["pluggedIn", "battery", "usbC"] {
                let p = &app["overrides"][key];
                if !p.is_null()
                    && (!p.is_object()
                        || !p["performance"].is_null()
                            && !matches!(p["performance"].as_i64(), Some(0..=9)))
                {
                    return Err(denied());
                }
                // Entirely absent/null optional policy inherits. A present value
                // must satisfy the same typed fan contract as managed loading.
                if !p["fans"].is_null() && !matches!(p["fans"].as_i64(), Some(0..=3)) {
                    return Err(denied());
                }
                if !p["fanControl"].is_null() {
                    fan_control(&p["fanControl"])?;
                    let legacy_maximum = p["fans"].as_i64() == Some(3)
                        && p["fanControl"]["mode"].as_i64() == Some(1)
                        && p["fanControl"]["fixedSpeedRpm"].as_i64() == Some(5400);
                    if !p["fans"].is_null()
                        && p["fans"] != p["fanControl"]["mode"]
                        && !legacy_maximum
                    {
                        return Err(denied());
                    }
                }
            }
        }
    }
    crate::service_status::Snapshot::validate_thermal_profiles(&doc["profiles"])?;
    if schema == 26 {
        preserve_legacy_independent_intent(&mut doc["profiles"]);
    }
    Ok(Some(CommittedAggregate {
        document: doc,
        profile: profile.ok_or_else(denied)?,
        schema,
    }))
}

/// The typed set of an aggregate `committed_aggregate` admitted: the
/// `profiles` member of its committed bytes as managed `ProfileStore` binds
/// it, with the schema-26 `PreserveLegacyIndependentProfileIntent` that
/// `aggregate` applies to its `Value`. The bytes go through the domain's
/// ordered reader, never through `Value`: this host's serde_json keeps no
/// member order, so a `Value` writes members alphabetically, and the decoder
/// needs the lighting `kind` discriminator first. A round trip would refuse
/// every Static, Breathing or other effect with members after `kind`. The
/// decoder refuses a member managed would not bind, and then nothing resolves.
fn typed_profiles(committed: &CommittedAggregate) -> io::Result<ProfileSet> {
    use openblade_domain::profile_set::{decode, json, normalize};
    let root = json::parse(&committed.profile).map_err(|_| denied())?;
    let profiles = root.member("profiles").ok_or_else(denied)?;
    let mut set = decode::profile_set(profiles, Dialect::Document).map_err(|_| denied())?;
    if committed.schema == 26 {
        normalize::preserve_legacy_independent_profile_intent(&mut set);
    }
    Ok(set)
}

/// Managed `LightingSettingsFactory.PreserveLegacyIndependentProfileIntent`,
/// which `ProfileStore` applies only when it reads a schema-26 document: a
/// lighting setting that controls the keyboard effect and also carries
/// independent controls keeps them without the keyboard effect. `profiles` is
/// the document's `profiles` member; power profiles and application overrides
/// are both covered.
fn preserve_legacy_independent_intent(profiles: &mut Value) {
    const POWER: [&str; 3] = ["pluggedIn", "battery", "usbC"];
    fn preserve(profile: Option<&mut Value>) {
        let Some(Value::Object(lighting)) = profile.and_then(|p| p.get_mut("lighting")) else {
            return;
        };
        // ProfileStore binds these case-sensitively.
        if lighting.get("controlsKeyboardEffect") == Some(&Value::Bool(true))
            && lighting
                .get("independentControls")
                .is_some_and(|v| !v.is_null())
        {
            lighting.insert("controlsKeyboardEffect".into(), Value::Bool(false));
        }
    }
    for key in POWER {
        preserve(profiles.get_mut(key));
    }
    if let Some(Value::Array(apps)) = profiles.get_mut("applicationProfiles") {
        for app in apps {
            for key in POWER {
                preserve(app.get_mut("overrides").and_then(|o| o.get_mut(key)));
            }
        }
    }
}

/// One absolute executable path in the form managed stores. `GetFullPath`
/// returns a `\\?\` or `\??\` path as written, so its segments are not
/// inspected, and it keeps the `\\.\` device prefix; any other `.` or `..`
/// segment means the path was never normalised.
pub(super) fn valid_application_path(p: &str) -> bool {
    let verbatim = p.starts_with(r"\\?\") || p.starts_with(r"\??\");
    let body = p.strip_prefix(r"\\.\").unwrap_or(p);
    p.encode_utf16().count() <= 32767
        && p.trim() == p
        && p.to_ascii_lowercase().ends_with(".exe")
        && !p.chars().any(char::is_control)
        && (verbatim
            || std::path::Path::new(p).is_absolute()
                && !body
                    .split(['\\', '/'])
                    .any(|part| matches!(part, "." | "..")))
}

// Exact managed FromLegacy defaults; only intent, never firmware confirmation.
pub(super) fn legacy_fan(mode: u8) -> Value {
    let points = |temps: [u8; 6], speeds: [u16; 6]| {
        temps
            .into_iter()
            .zip(speeds)
            .map(|(t, r)| json!({"temperatureCelsius":t,"speedRpm":r}))
            .collect::<Vec<_>>()
    };
    json!({"mode":mode,"fixedSpeedRpm":if mode==3{5400}else{3700},"manualCurve":{"cpu":points([40,50,60,70,80,90],[2000,2100,2600,3400,4400,5400]),"gpu":points([35,45,55,65,75,85],[2000,2100,2700,3600,4500,5400]),"useHigherTemperatureForBoth":true}})
}
/// The settings a fan override saves: the requested ones, or the legacy
/// defaults for its mode, with Maximum at its fixed bound.
fn resolved_fan(value: &crate::service_status::FanOverride, settings: Option<Value>) -> Value {
    let mut s = settings.unwrap_or_else(|| legacy_fan(value.value));
    if value.value == 3 {
        s["fixedSpeedRpm"] = json!(5400);
    }
    s
}
/// The resolved profile's fan control at class `power`: the application's
/// when its override sets one, its legacy default when the override sets
/// only the mode, and otherwise the power profile's, or the legacy default
/// for the profile's mode (`resolve::apply`), admitted by the domain decoder.
fn profile_fan(power: i32, profile: Option<&Resolution<'_>>) -> io::Result<Option<Value>> {
    let Some(resolution) = profile_layer(profile, power) else {
        return Ok(None);
    };
    let selected = match &resolution.profile.fan_control {
        Some(settings) => fan_settings(settings),
        None => legacy_fan(resolution.profile.fans.0 as u8),
    };
    openblade_domain::fan_history::validate_settings(&selected)?;
    Ok(Some(selected))
}
/// What a fan save leaves, read before it commits (`Storage::plan_fan_save`).
pub(super) struct FanSave {
    /// The saved intent the save replaces is manual.
    pub(super) leaving_manual: bool,
    /// The settings `desired_fan` selects after the save.
    pub(super) settings: Option<Value>,
}
#[cfg(test)]
#[path = "thermal_storage_open_tests.rs"]
mod open_tests;
#[cfg(test)]
#[path = "thermal_storage_resolution_tests.rs"]
mod resolution_tests;
#[cfg(test)]
mod application_path_tests {
    use super::valid_application_path;

    #[test]
    fn saved_application_paths_are_read_in_the_form_managed_stores() {
        for stored in [
            r"C:\Games\Game.exe",
            r"\\server\share\Game.EXE",
            r"\\server\Game.exe",
            // Managed stores these as written.
            r"\\?\C:\Games\..\Game.exe",
            r"\\?\C:\Games\.\Game.exe",
            r"\\?\C:\Games\\Game.exe",
            r"\\?\UNC\server\share\Game.exe",
            r"\??\C:\Games\..\Game.exe",
            // A device root is a prefix, not a current-directory segment.
            r"\\.\C:\Game.exe",
            r"\\.\Game.exe",
        ] {
            assert!(valid_application_path(stored), "{stored}");
        }
        for refused in [
            r"C:\Games\..\Game.exe",
            r"C:\Games\.\Game.exe",
            r"\\.\C:\Games\..\Game.exe",
            r"\\.\C:\Games\.\Game.exe",
            r"\\server\share\..\Game.exe",
            r"Games\Game.exe",
            r"C:Game.exe",
            r"\Games\Game.exe",
            r"C:\Games\Game.txt",
            r"\\?\C:\Games\Game.txt",
            " C:\\Games\\Game.exe",
            "C:\\Games\\Ga\u{1}me.exe",
        ] {
            assert!(!valid_application_path(refused), "{refused}");
        }
    }
}
#[cfg(test)]
mod release_read_tests {
    use super::*;
    #[test]
    fn tfc_release_only_exact_read_operations_tag_retryable_errors() {
        for code in [2, 5, 32, 33, 995, 1117] {
            let open = release_open_error(io::Error::from_raw_os_error(code));
            let read = release_bytes_error(io::Error::from_raw_os_error(code));
            assert_eq!(is_release_read_unavailable(&open), code == 32);
            assert_eq!(is_release_read_unavailable(&read), code == 33);
            assert!(
                !is_release_read_unavailable(&io::Error::from_raw_os_error(code)),
                "aggregate errors are never tagged by number"
            );
        }
        let tagged = release_open_error(io::Error::from_raw_os_error(32));
        assert_eq!(
            tagged
                .get_ref()
                .unwrap()
                .downcast_ref::<ReleaseReadUnavailable>()
                .unwrap()
                .0
                .raw_os_error(),
            Some(32)
        );
        assert!(is_release_deferred(
            &ReleaseDeferred::unreadable(tagged).into_error()
        ));
        assert!(!is_release_deferred(&io::Error::other("unclassified")));
    }
}

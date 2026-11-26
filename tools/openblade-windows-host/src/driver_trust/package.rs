//! Managed `WindowsRazerDriverApiDiscovery.Capture`: the read-only binding of
//! one device endpoint to its installed, catalog-trusted driver package.

use std::{
    collections::HashMap,
    io,
    sync::Mutex,
    time::{Duration, Instant},
};

use sha2::{Digest, Sha256};

use super::paths::{
    combine, full_path, parent, require_file_name, require_under, resolve_service_image,
};
use super::{Reject, inf, reject};

/// Managed re-verifies an unchanged package's trust after 60 s and keeps at
/// most 16 identities.
pub(crate) const TRUST_LIFETIME: Duration = Duration::from_secs(60);
const TRUST_ENTRIES: usize = 16;
/// Managed `OpenProtectedFile`'s bound on one package file.
#[cfg(not(test))]
pub(crate) const MAXIMUM_PACKAGE_FILE: u64 = 64 * 1024 * 1024;

/// A registry value as managed's `RegistryKey.GetValue` types it.
#[derive(Clone, Debug, PartialEq, Eq)]
pub(crate) enum Registry {
    /// The key is absent.
    NoKey,
    /// The key exists without the value.
    NoValue,
    /// REG_SZ or REG_EXPAND_SZ, unexpanded.
    Text(String),
    /// REG_DWORD.
    Number(i32),
    /// Any other type.
    Other,
}

/// One package file, opened for reading while denying writers, with its
/// contents.
pub(crate) struct Opened {
    pub(crate) data: Vec<u8>,
    #[allow(dead_code)] // Held, not read: it denies writers while the package is compared.
    pub(crate) hold: Option<std::fs::File>,
}

impl Opened {
    fn hash(&self) -> String {
        hex(&Sha256::digest(&self.data))
    }
}

/// The Windows calls a capture makes (`windows::Machine` in production).
pub(crate) trait Machine {
    /// `Environment.GetFolderPath(SpecialFolder.Windows)`.
    fn windows(&self) -> io::Result<String>;
    /// One HKLM value in the 64-bit view.
    fn registry(&self, key: &str, name: &str) -> io::Result<Registry>;
    /// `File.Exists`.
    fn exists(&self, path: &str) -> bool;
    /// `SetupGetInfDriverStoreLocation` of a published INF.
    fn store_location(&self, published_inf: &str) -> io::Result<String>;
    /// Managed `OpenProtectedFile`: no reparse point on the path or any
    /// ancestor, read access sharing only reads, at most 64 MiB.
    fn open_protected(&self, path: &str) -> Result<Opened, Reject>;
    /// `WindowsDriverApiTrust.VerifyCatalogMembers` under the Microsoft
    /// Windows Hardware Compatibility Publisher.
    fn verify_catalog(&self, catalog: &str, members: &[&str]) -> io::Result<()>;
}

/// The outcome managed reports as `RazerDriverApiPackageEvidence`.
#[derive(Clone, Debug, PartialEq, Eq)]
pub(crate) enum Package {
    Trusted {
        identity: String,
    },
    Rejected {
        installed: bool,
        stage: &'static str,
        reason: &'static str,
        message: String,
    },
}

impl Package {
    pub(crate) fn trusted(&self) -> bool {
        matches!(self, Self::Trusted { .. })
    }

    pub(crate) fn failure(&self) -> Option<&str> {
        match self {
            Self::Trusted { .. } => None,
            Self::Rejected { message, .. } => Some(message),
        }
    }
}

/// Verified package identities and when their trust was last verified.
#[derive(Default)]
pub(crate) struct TrustCache(HashMap<String, Instant>);

/// The process-wide cache the production capture uses.
#[cfg(not(test))]
pub(crate) fn trust_cache() -> &'static Mutex<TrustCache> {
    static CACHE: std::sync::OnceLock<Mutex<TrustCache>> = std::sync::OnceLock::new();
    CACHE.get_or_init(Mutex::default)
}

fn hex(bytes: &[u8]) -> String {
    bytes.iter().map(|byte| format!("{byte:02X}")).collect()
}

fn unavailable(stage: &'static str, reason: &'static str, installed: bool) -> Package {
    Package::Rejected {
        installed,
        stage,
        reason,
        message: match reason {
            "ServiceMissing" => "The selected Razer API service is not installed.",
            "ServiceTypeMismatch" => "The Razer API service is not a kernel driver.",
            "ServiceImageNotFound" => "The installed Razer API service image is missing.",
            "EndpointMissing" => "The exact Razer API endpoint binding is absent or ambiguous.",
            _ => "The Razer API package could not be verified.",
        }
        .to_owned(),
    }
}

/// Managed `Capture(instanceId, service, hardwareId)`.
pub(crate) fn capture(
    machine: &dyn Machine,
    cache: &Mutex<TrustCache>,
    now: Instant,
    instance_id: Option<&str>,
    service: &str,
    hardware_id: &str,
) -> Package {
    let mut stage = "ServiceBinding";
    match capture_core(
        machine,
        cache,
        now,
        instance_id,
        service,
        hardware_id,
        &mut stage,
    ) {
        Ok(package) => package,
        Err(rejected) => Package::Rejected {
            installed: true,
            stage,
            reason: rejected.reason,
            message: rejected.message,
        },
    }
}

fn text(value: Registry) -> Option<String> {
    match value {
        Registry::Text(text) => Some(text),
        _ => None,
    }
}

fn capture_core(
    machine: &dyn Machine,
    cache: &Mutex<TrustCache>,
    now: Instant,
    instance_id: Option<&str>,
    service: &str,
    hardware_id: &str,
    stage: &mut &'static str,
) -> Result<Package, Reject> {
    let service_key = format!(r"SYSTEM\CurrentControlSet\Services\{service}");
    let kind = machine.registry(&service_key, "Type")?;
    if kind == Registry::NoKey {
        return Ok(unavailable("ServiceBinding", "ServiceMissing", false));
    }
    if kind != Registry::Number(1) {
        return Ok(unavailable("ServiceBinding", "ServiceTypeMismatch", true));
    }
    let windows = machine.windows()?;
    *stage = "ServiceImage";
    let image_value = text(machine.registry(&service_key, "ImagePath")?);
    let image = resolve_service_image(image_value.as_deref(), &windows, service)?;
    if !machine.exists(&image) {
        return Ok(unavailable("ServiceImage", "ServiceImageNotFound", false));
    }
    let Some(instance_id) = instance_id.filter(|id| !id.trim().is_empty()) else {
        return Ok(unavailable("EndpointBinding", "EndpointMissing", true));
    };
    *stage = "EndpointBinding";
    let driver_key = text(machine.registry(
        &format!(r"SYSTEM\CurrentControlSet\Enum\{instance_id}"),
        "Driver",
    )?)
    .ok_or_else(|| {
        reject(
            "EndpointDriverMissing",
            "The endpoint has no installed driver package.",
        )
    })?;
    if driver_key.contains("..") || driver_key.starts_with('\\') {
        return Err(reject("InvalidDriverKey", "Invalid installed driver key."));
    }
    let inf_name = text(machine.registry(
        &format!(r"SYSTEM\CurrentControlSet\Control\Class\{driver_key}"),
        "InfPath",
    )?)
    .ok_or_else(|| {
        reject(
            "InstalledInfMissing",
            "The installed driver INF is unavailable.",
        )
    })?;
    require_file_name(&inf_name)?;
    let published_inf = combine(&combine(&windows, "INF"), &inf_name);
    *stage = "DriverStoreLocation";
    let located = machine.store_location(&published_inf)?;
    *stage = "DriverStorePath";
    let store_root = combine(
        &combine(&combine(&windows, "System32"), "DriverStore"),
        "FileRepository",
    );
    require_under(&located, &store_root)?;
    let store_inf = full_path(&located).expect("a path under the driver store is a drive path");
    let root = parent(&store_inf)
        .expect("a path under the driver store has a directory")
        .to_owned();
    // Hold the published and store INFs while selecting their catalog and
    // declarations.
    *stage = "PublishedInf";
    let published = machine.open_protected(&published_inf)?;
    *stage = "StoreInf";
    let stored_inf = machine.open_protected(&store_inf)?;
    *stage = "InfComparison";
    let published_identity = published.hash();
    if published_identity != stored_inf.hash() {
        return Err(reject(
            "PublishedInfMismatch",
            "The installed INF differs from its driver-store package.",
        ));
    }
    *stage = "InfContract";
    let catalog_name =
        inf::validate_contract(&inf::decode(&stored_inf.data), service, hardware_id)?;
    let catalog_path = combine(&root, &catalog_name);
    let store_image = combine(&root, &format!("{service}.sys"));
    *stage = "CatalogFile";
    let catalog = machine.open_protected(&catalog_path)?;
    *stage = "StoredDriver";
    let stored = machine.open_protected(&store_image)?;
    *stage = "ActiveDriver";
    let active = machine.open_protected(&image)?;
    *stage = "DriverComparison";
    let active_identity = active.hash();
    if active_identity != stored.hash() {
        return Err(reject(
            "ActiveDriverMismatch",
            "The active driver image differs from its installed package.",
        ));
    }
    let identity = hex(&Sha256::digest(
        [
            instance_id.to_uppercase(),
            service.to_uppercase(),
            hardware_id.to_uppercase(),
            image.to_uppercase(),
            store_inf.to_uppercase(),
            published_identity,
            active_identity,
            catalog.hash(),
        ]
        .join("\n")
        .as_bytes(),
    ));
    let mut cache = cache
        .lock()
        .map_err(|_| io::Error::other("driver trust cache poisoned"))?;
    let fresh = cache
        .0
        .get(&identity)
        .is_some_and(|verified| now.saturating_duration_since(*verified) < TRUST_LIFETIME);
    if !fresh {
        *stage = "CatalogTrust";
        machine.verify_catalog(&catalog_path, &[&store_inf, &store_image, &image])?;
        if cache.0.len() >= TRUST_ENTRIES {
            cache.0.clear();
        }
        cache.0.insert(identity.clone(), now);
    }
    drop((published, stored_inf, catalog, stored, active));
    Ok(Package::Trusted { identity })
}

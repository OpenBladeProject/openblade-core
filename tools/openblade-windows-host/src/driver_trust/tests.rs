//! Managed `WindowsRazerDriverApiDiscovery` and `WindowsDriverApiTrust`
//! parity for the pure parts and for `package::capture` over a scripted
//! machine. No Windows call is reached.

use std::{
    cell::{Cell, RefCell},
    collections::{HashMap, HashSet},
    io,
    sync::Mutex,
    time::Instant,
};

use super::package::{self, Machine, Opened, Package, Registry, TRUST_LIFETIME, TrustCache};
use super::paths::{
    combine, file_name, full_path, is_fully_qualified, parent, require_file_name, require_under,
    resolve_image, resolve_service_image,
};
use super::{
    MICROSOFT_HARDWARE_COMPATIBILITY_PUBLISHER, Reject, TRUST_E_NOSIGNATURE, inf,
    matches_publisher, reject, validate_publishers, verify_member_algorithms,
};

const WINDOWS: &str = r"C:\Windows";
const SERVICE: &str = "RzCommon";
const HARDWARE_ID: &str = r"RAZER\DeviceContol";
const INSTANCE: &str = r"ROOT\RZCOMMON\0000";
const DRIVER_KEY: &str = r"{4d36e97d-e325-11ce-bfc1-08002be10318}\0042";
const PUBLISHED_INF: &str = r"C:\Windows\INF\oem42.inf";
const STORE: &str = r"C:\Windows\System32\DriverStore\FileRepository\rzcommon.inf_amd64_0123";
const ACTIVE: &str = r"C:\Windows\System32\drivers\RzCommon.sys";

fn store(name: &str) -> String {
    format!(r"{STORE}\{name}")
}

const INF: &str = "\
[Version]\r
Signature=\"$WINDOWS NT$\"\r
Provider=%ManufacturerName% ; the vendor\r
CatalogFile=rzcommon.cat\r
\r
[Manufacturer]\r
%ManufacturerName%=Standard,NTamd64\r
\r
[Standard.NTamd64]\r
%DeviceDesc%=RzCommon_Device, RAZER\\DeviceContol\r
\r
[RzCommon_Device.NT.Services]\r
AddService = RzCommon,%SPSVCINST_ASSOCSERVICE%, RzCommon_Service_Inst\r
\r
[RzCommon_Service_Inst]\r
ServiceType = 1\r
ServiceBinary = %12%\\RzCommon.sys\r
\r
[Strings]\r
ManufacturerName=\"Razer Inc\"\r
DeviceDesc=\"Razer Device Control\"\r
";

fn utf16(text: &str) -> Vec<u8> {
    let mut bytes = vec![0xFF, 0xFE];
    for unit in text.encode_utf16() {
        bytes.extend_from_slice(&unit.to_le_bytes());
    }
    bytes
}

fn contract(text: &str) -> Result<String, Reject> {
    inf::validate_contract(text, SERVICE, HARDWARE_ID)
}

fn reason(result: Result<String, Reject>) -> &'static str {
    result.expect_err("the contract is refused").reason
}

#[test]
fn paths_resolve_managed_aliases_and_refuse_relative_or_ambiguous_images() {
    let drivers = r"C:\Windows\System32\drivers\RzCommon.sys";
    for alias in [
        r"\SystemRoot\System32\drivers\RzCommon.sys",
        r"%SystemRoot%\System32\drivers\RzCommon.sys",
        r"\??\C:\Windows\System32\drivers\RzCommon.sys",
        r"\DosDevices\C:\Windows\System32\drivers\RzCommon.sys",
        r"System32\drivers\RzCommon.sys",
        r"C:\Windows\System32\drivers\RzCommon.sys",
    ] {
        assert_eq!(resolve_image(alias, WINDOWS, true).as_deref(), Ok(drivers));
    }
    assert!(resolve_image(r"System32\drivers\RzCommon.sys", WINDOWS, false).is_err());
    assert_eq!(
        resolve_image(r"\SystemRoot\\C:\x.sys", WINDOWS, true),
        Err("The driver image has an ambiguous SystemRoot path.")
    );
    assert_eq!(
        resolve_image(r"drivers\RzCommon.sys", WINDOWS, true),
        Err("The driver image path is not absolute or a supported Windows alias.")
    );
    assert!(is_fully_qualified(r"\\server\share\x"));
    assert!(!is_fully_qualified(r"C:x"));
    assert_eq!(combine(WINDOWS, r"D:\x"), r"D:\x");
    assert_eq!(combine(r"C:\", "x"), r"C:\x");
    assert_eq!(
        full_path(r"c:/Windows//System32\.\drivers\..\drivers\x.sys").as_deref(),
        Some(r"C:\Windows\System32\drivers\x.sys")
    );
    assert_eq!(full_path(r"C:\..\..").as_deref(), Some(r"C:\"));
    assert_eq!(full_path(r"\\server\share\x"), None);
    assert_eq!(file_name(r"C:\a\b.sys"), "b.sys");
    assert_eq!(parent(r"C:\a\b.sys"), Some(r"C:\a"));
    assert_eq!(parent(r"C:\b.sys"), Some(r"C:\"));
    assert_eq!(parent(r"C:\"), None);
}

#[test]
fn paths_keep_images_and_packages_inside_their_protected_directories() {
    let drivers = r"C:\Windows\System32\drivers";
    assert!(require_under(r"C:\Windows\System32\drivers\x.sys", drivers).is_ok());
    assert!(require_under(r"c:\windows\system32\DRIVERS\x.sys", drivers).is_ok());
    for outside in [
        r"C:\Windows\System32\drivers",
        r"C:\Windows\System32\drivers2\x.sys",
        r"C:\Windows\System32\drivers\..\x.sys",
        r"\\server\share\Windows\System32\drivers\x.sys",
        r"drivers\x.sys",
    ] {
        assert_eq!(
            require_under(outside, drivers).unwrap_err().reason,
            "UnprotectedPath",
            "{outside}"
        );
    }
    assert!(require_file_name("oem42.inf").is_ok());
    for name in ["", "  ", r"..\oem42.inf", "a/b.inf", "C:oem42.inf"] {
        assert_eq!(
            require_file_name(name).unwrap_err().reason,
            "InvalidPackageFileName"
        );
    }
}

#[test]
fn service_image_matches_managed_resolve_service_image() {
    assert_eq!(
        resolve_service_image(
            Some(r" \SystemRoot\System32\drivers\rzcommon.SYS "),
            WINDOWS,
            SERVICE
        )
        .as_deref(),
        Ok(r"C:\Windows\System32\drivers\rzcommon.SYS")
    );
    let refused = |value: Option<&str>| resolve_service_image(value, WINDOWS, SERVICE).unwrap_err();
    assert_eq!(refused(None).reason, "ServiceImageMissing");
    assert_eq!(refused(Some(" ")).reason, "ServiceImageMissing");
    assert_eq!(
        refused(Some(r"drivers\RzCommon.sys")).reason,
        "ServiceImagePathAmbiguous"
    );
    assert_eq!(
        refused(Some(r"C:\ProgramData\RzCommon.sys")).reason,
        "UnprotectedPath"
    );
    assert_eq!(
        refused(Some(r"\SystemRoot\System32\drivers\RzDev_02e0.sys")).reason,
        "ServiceImageNameMismatch"
    );
}

#[test]
fn inf_contract_selects_the_catalog_of_the_exact_x64_model_and_service() {
    assert_eq!(contract(INF).as_deref(), Ok("rzcommon.cat"));
    assert_eq!(
        contract(&inf::decode(&utf16(INF))).as_deref(),
        Ok("rzcommon.cat")
    );
    let mut big_endian = vec![0xFE, 0xFF];
    for unit in INF.encode_utf16() {
        big_endian.extend_from_slice(&unit.to_be_bytes());
    }
    assert_eq!(inf::decode(&big_endian), INF);
    let mut utf8 = vec![0xEF, 0xBB, 0xBF];
    utf8.extend_from_slice(INF.as_bytes());
    assert_eq!(inf::decode(&utf8), INF);
    // A decorated model and the x64 catalog key bind as well.
    let decorated = INF
        .replace("Standard,NTamd64", "Standard,NTx86,NTamd64.10.0")
        .replace("[Standard.NTamd64]", "[Standard.NTamd64.10.0]")
        .replace("CatalogFile=", "CatalogFile.NTamd64=");
    assert_eq!(contract(&decorated).as_deref(), Ok("rzcommon.cat"));
    // The same catalog declared twice is one catalog.
    let repeated = INF.replace(
        "CatalogFile=rzcommon.cat",
        "CatalogFile=rzcommon.cat\r\nCatalogFile.NTamd64=RZCOMMON.CAT",
    );
    assert_eq!(contract(&repeated).as_deref(), Ok("rzcommon.cat"));
}

#[test]
fn inf_contract_refuses_every_unbound_or_indirect_declaration() {
    let cases: [(String, &str); 11] = [
        (
            INF.replace("\"Razer Inc\"", "\"Contoso\""),
            "ProviderMismatch",
        ),
        (
            INF.replace(r"RAZER\DeviceContol", r"RAZER\Other"),
            "HardwareServiceMismatch",
        ),
        (
            INF.replace("Standard,NTamd64", "Standard,NTx86"),
            "HardwareServiceMismatch",
        ),
        (
            INF.replace("AddService = RzCommon,", "AddService = RzDev_02e0,"),
            "HardwareServiceMismatch",
        ),
        (
            INF.replace("ServiceType = 1", "ServiceType = 3"),
            "HardwareServiceMismatch",
        ),
        (
            INF.replace(r"%12%\RzCommon.sys", r"%13%\RzCommon.sys"),
            "HardwareServiceMismatch",
        ),
        (
            INF.replace(
                "CatalogFile=rzcommon.cat",
                "CatalogFile=rzcommon.cat\r\nCatalogFile.NTamd64=other.cat",
            ),
            "CatalogMissingOrAmbiguous",
        ),
        (
            INF.replace("CatalogFile=rzcommon.cat\r\n", ""),
            "CatalogMissingOrAmbiguous",
        ),
        (
            INF.replace("rzcommon.cat", "rzcommon.dll"),
            "CatalogExtensionMismatch",
        ),
        (
            INF.replace("rzcommon.cat", r"..\rzcommon.cat"),
            "InvalidPackageFileName",
        ),
        (
            format!("{INF}[Version]\r\nProvider=Razer\r\n"),
            "DuplicateInfSection",
        ),
    ];
    for (text, expected) in cases {
        assert_eq!(reason(contract(&text)), expected, "{text}");
    }
    assert_eq!(
        reason(contract(&INF.replace("ServiceType = 1", "ServiceType = %"))),
        "PackageInspectionFailed"
    );
    assert_eq!(
        reason(contract(&format!("{INF};{}", "x".repeat(1_048_576)))),
        "InfTooLarge"
    );
}

#[test]
fn member_algorithms_fall_back_to_sha1_only_for_an_unsigned_lookup() {
    let mut asked = Vec::new();
    verify_member_algorithms(|algorithm| {
        asked.push(algorithm.to_owned());
        Ok(0)
    })
    .unwrap();
    assert_eq!(asked, ["SHA256"]);

    let mut asked = Vec::new();
    verify_member_algorithms(|algorithm| {
        asked.push(algorithm.to_owned());
        Ok(if algorithm == "SHA256" {
            TRUST_E_NOSIGNATURE
        } else {
            0
        })
    })
    .unwrap();
    assert_eq!(asked, ["SHA256", "SHA1"]);

    let mut asked = Vec::new();
    let error = verify_member_algorithms(|algorithm| {
        asked.push(algorithm.to_owned());
        Ok(0x800B_0109_u32 as i32)
    })
    .unwrap_err();
    assert_eq!(asked, ["SHA256"]);
    assert_eq!(
        error.to_string(),
        "Windows rejected driver catalog member trust (0x800B0109)."
    );

    let error = verify_member_algorithms(|_| Ok(TRUST_E_NOSIGNATURE)).unwrap_err();
    assert_eq!(
        error.to_string(),
        "Windows rejected driver catalog member trust (0x800B0100)."
    );
}

#[test]
fn publishers_are_bounded_unique_full_subjects_matched_exactly() {
    let microsoft = MICROSOFT_HARDWARE_COMPATIBILITY_PUBLISHER;
    assert_eq!(validate_publishers(&[microsoft]).unwrap(), [microsoft]);
    let long = "x".repeat(513);
    for refused in [
        &[][..],
        &[""][..],
        &[" CN=Razer"][..],
        &[microsoft, microsoft][..],
        &[long.as_str()][..],
        &["CN=a"; 17][..],
    ] {
        assert!(validate_publishers(refused).is_err());
    }
    assert!(matches_publisher(microsoft, &[microsoft]));
    assert!(!matches_publisher(&microsoft.to_lowercase(), &[microsoft]));
    assert!(!matches_publisher(
        "CN=Microsoft Windows Hardware Compatibility Publisher",
        &[microsoft]
    ));
}

/// A machine with the trusted RzCommon package installed.
struct Fake {
    registry: RefCell<HashMap<(String, String), Registry>>,
    files: RefCell<HashMap<String, Vec<u8>>>,
    missing: RefCell<HashSet<String>>,
    reparse: RefCell<HashSet<String>>,
    location: RefCell<io::Result<String>>,
    trust: RefCell<io::Result<()>>,
    verified: Cell<usize>,
    members: RefCell<Vec<String>>,
}

fn key(path: &str) -> String {
    path.to_uppercase()
}

impl Fake {
    fn installed() -> Self {
        let fake = Self {
            registry: RefCell::default(),
            files: RefCell::default(),
            missing: RefCell::default(),
            reparse: RefCell::default(),
            location: RefCell::new(Ok(store("rzcommon.inf"))),
            trust: RefCell::new(Ok(())),
            verified: Cell::new(0),
            members: RefCell::default(),
        };
        let services = format!(r"SYSTEM\CurrentControlSet\Services\{SERVICE}");
        fake.set(&services, "Type", Registry::Number(1));
        fake.set(
            &services,
            "ImagePath",
            Registry::Text(r"\SystemRoot\System32\drivers\RzCommon.sys".into()),
        );
        fake.set(
            &format!(r"SYSTEM\CurrentControlSet\Enum\{INSTANCE}"),
            "Driver",
            Registry::Text(DRIVER_KEY.into()),
        );
        fake.set(
            &format!(r"SYSTEM\CurrentControlSet\Control\Class\{DRIVER_KEY}"),
            "InfPath",
            Registry::Text("oem42.inf".into()),
        );
        fake.file(PUBLISHED_INF, utf16(INF));
        fake.file(&store("rzcommon.inf"), utf16(INF));
        fake.file(&store("rzcommon.cat"), b"catalog".to_vec());
        fake.file(&store("RzCommon.sys"), b"driver".to_vec());
        fake.file(ACTIVE, b"driver".to_vec());
        fake
    }

    fn set(&self, path: &str, name: &str, value: Registry) {
        self.registry
            .borrow_mut()
            .insert((key(path), key(name)), value);
    }

    fn file(&self, path: &str, data: Vec<u8>) {
        self.files.borrow_mut().insert(key(path), data);
    }

    fn capture(&self, cache: &Mutex<TrustCache>, now: Instant) -> Package {
        package::capture(self, cache, now, Some(INSTANCE), SERVICE, HARDWARE_ID)
    }
}

impl Machine for Fake {
    fn windows(&self) -> io::Result<String> {
        Ok(WINDOWS.to_owned())
    }

    fn registry(&self, path: &str, name: &str) -> io::Result<Registry> {
        let registry = self.registry.borrow();
        if let Some(value) = registry.get(&(key(path), key(name))) {
            return Ok(value.clone());
        }
        Ok(if registry.keys().any(|(known, _)| *known == key(path)) {
            Registry::NoValue
        } else {
            Registry::NoKey
        })
    }

    fn exists(&self, path: &str) -> bool {
        !self.missing.borrow().contains(&key(path)) && self.files.borrow().contains_key(&key(path))
    }

    fn store_location(&self, published_inf: &str) -> io::Result<String> {
        assert_eq!(published_inf, PUBLISHED_INF);
        match &*self.location.borrow() {
            Ok(location) => Ok(location.clone()),
            Err(error) => Err(io::Error::new(error.kind(), error.to_string())),
        }
    }

    fn open_protected(&self, path: &str) -> Result<Opened, Reject> {
        if self.reparse.borrow().contains(&key(path)) {
            return Err(reject(
                "ReparsePoint",
                "A driver package path contains a reparse point.",
            ));
        }
        let data = self.files.borrow().get(&key(path)).cloned();
        let data = data
            .ok_or_else(|| io::Error::new(io::ErrorKind::NotFound, format!("{path} is missing")))?;
        Ok(Opened { data, hold: None })
    }

    fn verify_catalog(&self, catalog: &str, members: &[&str]) -> io::Result<()> {
        assert_eq!(catalog, store("rzcommon.cat"));
        self.verified.set(self.verified.get() + 1);
        *self.members.borrow_mut() = members.iter().map(|member| (*member).to_owned()).collect();
        match &*self.trust.borrow() {
            Ok(()) => Ok(()),
            Err(error) => Err(io::Error::other(error.to_string())),
        }
    }
}

fn rejected(package: &Package) -> (bool, &'static str, &'static str, &str) {
    match package {
        Package::Rejected {
            installed,
            stage,
            reason,
            message,
        } => (*installed, stage, reason, message),
        Package::Trusted { .. } => panic!("the package is refused"),
    }
}

#[test]
fn capture_binds_and_trusts_the_installed_package_once_per_lifetime() {
    let fake = Fake::installed();
    let cache = Mutex::default();
    let start = Instant::now();
    let first = fake.capture(&cache, start);
    let Package::Trusted { identity } = &first else {
        panic!("the installed package is trusted: {first:?}");
    };
    assert_eq!(identity.len(), 64);
    assert!(first.trusted() && first.failure().is_none());
    assert_eq!(fake.verified.get(), 1);
    assert_eq!(
        *fake.members.borrow(),
        [
            store("rzcommon.inf"),
            store("RzCommon.sys"),
            ACTIVE.to_owned()
        ]
    );
    // Within the lifetime the same identity is not verified again.
    assert_eq!(fake.capture(&cache, start + TRUST_LIFETIME / 2), first);
    assert_eq!(fake.verified.get(), 1);
    // After it, it is.
    assert_eq!(fake.capture(&cache, start + TRUST_LIFETIME), first);
    assert_eq!(fake.verified.get(), 2);
    // A compatible update is a new identity, verified at once and admitted:
    // nothing is allowlisted by hash.
    fake.file(&store("RzCommon.sys"), b"driver 2".to_vec());
    fake.file(ACTIVE, b"driver 2".to_vec());
    let updated = fake.capture(&cache, start + TRUST_LIFETIME);
    assert!(updated.trusted());
    assert_ne!(updated, first);
    assert_eq!(fake.verified.get(), 3);
}

#[test]
fn capture_refuses_a_catalog_windows_does_not_trust_and_does_not_cache_it() {
    let fake = Fake::installed();
    *fake.trust.borrow_mut() = Err(io::Error::other(
        "Windows rejected driver catalog member trust (0x800B0109).",
    ));
    let cache = Mutex::default();
    let now = Instant::now();
    for attempt in 1..=2 {
        let package = fake.capture(&cache, now);
        assert_eq!(
            rejected(&package),
            (
                true,
                "CatalogTrust",
                "PackageInspectionFailed",
                "Windows rejected driver catalog member trust (0x800B0109)."
            )
        );
        assert_eq!(fake.verified.get(), attempt);
    }
}

#[test]
fn capture_reports_managed_stages_for_an_absent_or_misbound_stack() {
    let services = format!(r"SYSTEM\CurrentControlSet\Services\{SERVICE}");
    let enum_key = format!(r"SYSTEM\CurrentControlSet\Enum\{INSTANCE}");
    let class_key = format!(r"SYSTEM\CurrentControlSet\Control\Class\{DRIVER_KEY}");
    type Case = (Box<dyn Fn(&Fake)>, (bool, &'static str, &'static str));
    let cases: Vec<Case> = vec![
        (
            Box::new({
                let services = services.clone();
                move |fake: &Fake| {
                    fake.registry
                        .borrow_mut()
                        .retain(|(path, _), _| *path != key(&services));
                }
            }),
            (false, "ServiceBinding", "ServiceMissing"),
        ),
        (
            Box::new({
                let services = services.clone();
                move |fake: &Fake| fake.set(&services, "Type", Registry::Number(16))
            }),
            (true, "ServiceBinding", "ServiceTypeMismatch"),
        ),
        (
            Box::new({
                let services = services.clone();
                move |fake: &Fake| fake.set(&services, "Type", Registry::Text("1".into()))
            }),
            (true, "ServiceBinding", "ServiceTypeMismatch"),
        ),
        (
            Box::new({
                let services = services.clone();
                move |fake: &Fake| fake.set(&services, "ImagePath", Registry::Other)
            }),
            (true, "ServiceImage", "ServiceImageMissing"),
        ),
        (
            Box::new(move |fake: &Fake| {
                fake.set(
                    &services,
                    "ImagePath",
                    Registry::Text(r"\SystemRoot\System32\drivers\RzDev_02e0.sys".into()),
                );
            }),
            (true, "ServiceImage", "ServiceImageNameMismatch"),
        ),
        (
            Box::new(|fake: &Fake| {
                fake.missing.borrow_mut().insert(key(ACTIVE));
            }),
            (false, "ServiceImage", "ServiceImageNotFound"),
        ),
        (
            Box::new(move |fake: &Fake| {
                fake.registry
                    .borrow_mut()
                    .retain(|(path, _), _| *path != key(&enum_key));
            }),
            (true, "EndpointBinding", "EndpointDriverMissing"),
        ),
        (
            Box::new(|fake: &Fake| {
                fake.set(
                    &format!(r"SYSTEM\CurrentControlSet\Enum\{INSTANCE}"),
                    "Driver",
                    Registry::Text(r"..\0042".into()),
                );
            }),
            (true, "EndpointBinding", "InvalidDriverKey"),
        ),
        (
            Box::new(move |fake: &Fake| fake.set(&class_key, "InfPath", Registry::NoValue)),
            (true, "EndpointBinding", "InstalledInfMissing"),
        ),
        (
            Box::new(|fake: &Fake| {
                fake.set(
                    &format!(r"SYSTEM\CurrentControlSet\Control\Class\{DRIVER_KEY}"),
                    "InfPath",
                    Registry::Text(r"..\oem42.inf".into()),
                );
            }),
            (true, "EndpointBinding", "InvalidPackageFileName"),
        ),
        (
            Box::new(|fake: &Fake| {
                *fake.location.borrow_mut() = Err(io::Error::other("no store location"));
            }),
            (true, "DriverStoreLocation", "PackageInspectionFailed"),
        ),
        (
            Box::new(|fake: &Fake| {
                *fake.location.borrow_mut() = Ok(r"C:\ProgramData\rzcommon.inf".into());
            }),
            (true, "DriverStorePath", "UnprotectedPath"),
        ),
        (
            Box::new(|fake: &Fake| {
                fake.reparse.borrow_mut().insert(key(PUBLISHED_INF));
            }),
            (true, "PublishedInf", "ReparsePoint"),
        ),
        (
            Box::new(|fake: &Fake| {
                fake.files.borrow_mut().remove(&key(&store("rzcommon.inf")));
            }),
            (true, "StoreInf", "PackageInspectionFailed"),
        ),
        (
            Box::new(|fake: &Fake| {
                fake.file(PUBLISHED_INF, utf16(&format!("{INF};")));
            }),
            (true, "InfComparison", "PublishedInfMismatch"),
        ),
        (
            Box::new(|fake: &Fake| {
                let misbound = utf16(&INF.replace(r"RAZER\DeviceContol", r"RAZER\Other"));
                fake.file(PUBLISHED_INF, misbound.clone());
                fake.file(&store("rzcommon.inf"), misbound);
            }),
            (true, "InfContract", "HardwareServiceMismatch"),
        ),
        (
            Box::new(|fake: &Fake| {
                fake.files.borrow_mut().remove(&key(&store("rzcommon.cat")));
            }),
            (true, "CatalogFile", "PackageInspectionFailed"),
        ),
        (
            Box::new(|fake: &Fake| {
                fake.files.borrow_mut().remove(&key(&store("RzCommon.sys")));
            }),
            (true, "StoredDriver", "PackageInspectionFailed"),
        ),
        (
            Box::new(|fake: &Fake| {
                fake.reparse.borrow_mut().insert(key(ACTIVE));
            }),
            (true, "ActiveDriver", "ReparsePoint"),
        ),
        (
            Box::new(|fake: &Fake| fake.file(ACTIVE, b"replaced".to_vec())),
            (true, "DriverComparison", "ActiveDriverMismatch"),
        ),
    ];
    for (change, expected) in cases {
        let fake = Fake::installed();
        change(&fake);
        let package = fake.capture(&Mutex::default(), Instant::now());
        let (installed, stage, reason, message) = rejected(&package);
        assert_eq!((installed, stage, reason), expected, "{message}");
        assert_eq!(package.failure(), Some(message));
        assert!(!message.is_empty());
        assert_eq!(fake.verified.get(), 0, "{expected:?} never reaches trust");
    }
}

#[test]
fn capture_refuses_an_endpoint_it_cannot_name() {
    let fake = Fake::installed();
    for instance in [None, Some(""), Some("  ")] {
        let package = package::capture(
            &fake,
            &Mutex::default(),
            Instant::now(),
            instance,
            SERVICE,
            HARDWARE_ID,
        );
        assert_eq!(
            rejected(&package),
            (
                true,
                "EndpointBinding",
                "EndpointMissing",
                "The exact Razer API endpoint binding is absent or ambiguous."
            )
        );
    }
    // The wrong model's service and hardware are the misbinding they are.
    let package = package::capture(
        &fake,
        &Mutex::default(),
        Instant::now(),
        Some(INSTANCE),
        "RzDev_02c6",
        HARDWARE_ID,
    );
    assert_eq!(rejected(&package).2, "ServiceMissing");
    let package = package::capture(
        &fake,
        &Mutex::default(),
        Instant::now(),
        Some(INSTANCE),
        SERVICE,
        r"RZVIRTUAL\VID_1532&PID_02C6&MI_00&COL03",
    );
    assert_eq!(
        rejected(&package),
        (
            true,
            "InfContract",
            "HardwareServiceMismatch",
            "The installed INF does not bind the exact hardware and Razer API service."
        )
    );
}

/// Managed `NormalizeFilePath`: a bounded (in UTF-16 units), fully
/// qualified local drive path, normalized, without alternate streams.
#[test]
fn normalize_admits_only_bounded_local_drive_paths_without_streams() {
    use super::paths::{MAXIMUM_PATH, normalize};
    const UNQUALIFIED: &str = "Driver API paths must be bounded, fully qualified local paths.";
    const NOT_LOCAL: &str =
        "Driver API paths must use a bounded local drive path without alternate streams.";
    assert_eq!(
        normalize(r"c:/Windows\System32\.\drivers\..\drivers\x.sys").as_deref(),
        Ok(r"C:\Windows\System32\drivers\x.sys")
    );
    for unqualified in ["", "   ", r"x.sys", r"C:x.sys", r"\Windows\x.sys"] {
        assert_eq!(normalize(unqualified), Err(UNQUALIFIED), "{unqualified:?}");
    }
    for not_local in [r"\\server\share\x.sys", r"\\?\C:\x.sys", r"C:\x.sys:stream"] {
        assert_eq!(normalize(not_local), Err(NOT_LOCAL), "{not_local:?}");
    }
    // The bound counts UTF-16 units, as managed's string length does, not
    // UTF-8 bytes.
    let at_bound = format!(r"C:\{}", "\u{e9}".repeat(MAXIMUM_PATH - 3));
    assert!(at_bound.len() > MAXIMUM_PATH);
    assert_eq!(normalize(&at_bound).as_deref(), Ok(at_bound.as_str()));
    let past_bound = format!(r"C:\{}", "\u{1F600}".repeat((MAXIMUM_PATH - 2) / 2));
    assert!(past_bound.encode_utf16().count() > MAXIMUM_PATH);
    assert_eq!(normalize(&past_bound), Err(UNQUALIFIED));
}

/// Managed `RejectsAReparsePointInAnyPathComponent`: the file and every
/// directory above it are read, the drive root is not, and a reparse point
/// anywhere refuses.
#[test]
fn the_reparse_walk_reads_every_component_above_the_root() {
    use super::paths::ensure_no_reparse_points;
    let path = r"C:\safe\linked\driver.sys";
    let mut read = Vec::new();
    ensure_no_reparse_points(path, |component| {
        read.push(component.to_owned());
        Ok(false)
    })
    .unwrap();
    assert_eq!(read, [path, r"C:\safe\linked", r"C:\safe"]);
    let refused = ensure_no_reparse_points(path, |component| {
        Ok(component.eq_ignore_ascii_case(r"C:\SAFE\LINKED"))
    })
    .unwrap_err();
    assert_eq!(
        refused.to_string(),
        "Driver API paths cannot contain reparse points."
    );
    // A component that cannot be read refuses with its own error.
    let unreadable = ensure_no_reparse_points(path, |_| {
        Err(io::Error::from(io::ErrorKind::PermissionDenied))
    })
    .unwrap_err();
    assert_eq!(unreadable.kind(), io::ErrorKind::PermissionDenied);
    // The root alone has nothing to read.
    ensure_no_reparse_points(r"C:\", |_| panic!("the root is not read")).unwrap();
}

/// Managed `ComputeObservedContentIdentity`'s byte layout, spelled out: the
/// versioned prefix, the count as a little-endian i32, then per file its
/// upper-cased file name's UTF-8 length (i32) and bytes, its length (i64)
/// and its SHA256.
#[test]
fn the_content_identity_is_managed_byte_layout_over_file_names_and_hashes() {
    use super::{Content, content_identity};
    use sha2::{Digest, Sha256};
    let catalog = Content {
        path: r"C:\Windows\System32\DriverStore\FileRepository\x\rzcommon.cat",
        length: 7,
        hash: Sha256::digest(b"catalog").into(),
    };
    let image = Content {
        path: r"C:\Windows\System32\drivers\RzCommon.sys",
        length: 6,
        hash: Sha256::digest(b"driver").into(),
    };
    let mut bytes = b"OpenBlade.WindowsDriverApiTrust.v1\0".to_vec();
    bytes.extend(2i32.to_le_bytes());
    for (name, file) in [("RZCOMMON.CAT", &catalog), ("RZCOMMON.SYS", &image)] {
        bytes.extend((name.len() as i32).to_le_bytes());
        bytes.extend(name.as_bytes());
        bytes.extend((file.length as i64).to_le_bytes());
        bytes.extend(file.hash);
    }
    let expected: String = Sha256::digest(&bytes)
        .iter()
        .map(|byte| format!("{byte:02X}"))
        .collect();
    let identity = content_identity(&[catalog, image]);
    assert_eq!(identity, format!("SHA256:{expected}"));
    // The directory and the name's case are not part of it; the order and
    // every length and hash are.
    let named = |path, length, data: &[u8]| Content {
        path,
        length,
        hash: Sha256::digest(data).into(),
    };
    let one = content_identity(&[named(r"C:\a\RzCommon.sys", 6, b"driver")]);
    assert_eq!(
        one,
        content_identity(&[named(r"D:\b\rzcommon.SYS", 6, b"driver")])
    );
    assert_ne!(
        one,
        content_identity(&[named(r"C:\a\RzCommon.sys", 7, b"driver")])
    );
    assert_ne!(
        one,
        content_identity(&[named(r"C:\a\RzCommon.sys", 6, b"driver 2")])
    );
    assert_ne!(
        content_identity(&[named(r"C:\a", 1, b"a"), named(r"C:\b", 1, b"b")]),
        content_identity(&[named(r"C:\b", 1, b"b"), named(r"C:\a", 1, b"a")])
    );
}

/// The trust cache keeps at most `TRUST_ENTRIES` identities: a new one when
/// it is full clears it first, so every earlier identity is verified again
/// on its next capture. A poisoned cache refuses the package without
/// verifying it, rather than trust an identity it cannot read.
#[test]
fn the_trust_cache_clears_when_full_and_refuses_when_poisoned() {
    let fake = Fake::installed();
    let cache = Mutex::default();
    let now = Instant::now();
    let capture = |build: usize| {
        let data = format!("driver {build}").into_bytes();
        fake.file(&store("RzCommon.sys"), data.clone());
        fake.file(ACTIVE, data);
        fake.capture(&cache, now)
    };
    for build in 0..16 {
        assert!(capture(build).trusted(), "{build}");
    }
    assert_eq!(fake.verified.get(), 16);
    for build in 0..16 {
        assert!(capture(build).trusted(), "{build}");
    }
    assert_eq!(fake.verified.get(), 16, "every identity is still cached");
    assert!(capture(16).trusted());
    assert_eq!(fake.verified.get(), 17);
    assert!(capture(16).trusted());
    assert_eq!(fake.verified.get(), 17, "the new identity is kept");
    assert!(capture(0).trusted());
    assert_eq!(fake.verified.get(), 18, "the earlier ones were cleared");

    let _ = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        let _held = cache.lock().unwrap();
        panic!("poisoned for the trust-cache test");
    }));
    let package = capture(0);
    let (installed, _, reason, message) = rejected(&package);
    assert_eq!(
        (installed, reason, message),
        (
            true,
            "PackageInspectionFailed",
            "driver trust cache poisoned"
        )
    );
    assert_eq!(fake.verified.get(), 18);
}

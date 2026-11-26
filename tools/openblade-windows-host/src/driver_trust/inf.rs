//! Managed `WindowsRazerDriverApiDiscovery.ValidateInfContract`: only the
//! installed x64 model's explicit service declaration binds a package.
//! Unsupported INF indirection fails closed; a file name, a provider string
//! or an unrelated signed model is not enough.

use std::collections::HashMap;

use super::paths::{ends_with_ci, eq_ci, require_file_name, starts_with_ci};
use super::{Reject, reject};

/// Managed's bound on the INF text, in UTF-16 code units.
const MAXIMUM_LENGTH: usize = 1_048_576;

/// The INF bytes as managed's `StreamReader(stream, Encoding.UTF8, true)`
/// reads them: a UTF-32 LE, UTF-16 LE or BE, or UTF-8 byte-order mark
/// selects the encoding, and UTF-8 without one. Invalid sequences become
/// U+FFFD.
pub(crate) fn decode(bytes: &[u8]) -> String {
    let units = |bytes: &[u8], big: bool| -> Vec<u16> {
        bytes
            .as_chunks::<2>()
            .0
            .iter()
            .map(|pair| {
                if big {
                    u16::from_be_bytes([pair[0], pair[1]])
                } else {
                    u16::from_le_bytes([pair[0], pair[1]])
                }
            })
            .collect()
    };
    if let Some(rest) = bytes.strip_prefix(&[0xFF, 0xFE, 0x00, 0x00]) {
        return rest
            .as_chunks::<4>()
            .0
            .iter()
            .map(|quad| {
                char::from_u32(u32::from_le_bytes([quad[0], quad[1], quad[2], quad[3]]))
                    .unwrap_or(char::REPLACEMENT_CHARACTER)
            })
            .collect();
    }
    if let Some(rest) = bytes.strip_prefix(&[0xFF, 0xFE]) {
        return String::from_utf16_lossy(&units(rest, false));
    }
    if let Some(rest) = bytes.strip_prefix(&[0xFE, 0xFF]) {
        return String::from_utf16_lossy(&units(rest, true));
    }
    let rest = bytes.strip_prefix(&[0xEF, 0xBB, 0xBF]).unwrap_or(bytes);
    String::from_utf8_lossy(rest).into_owned()
}

struct Inf<'a> {
    sections: HashMap<String, Vec<&'a str>>,
    strings: HashMap<String, String>,
}

impl<'a> Inf<'a> {
    fn section(&self, name: &str) -> &[&'a str] {
        self.sections
            .get(&name.to_uppercase())
            .map_or(&[], Vec::as_slice)
    }

    /// Managed `Expand`: a whole `%name%` value from `[Strings]`. A bare
    /// `%` is the range exception managed reports as an inspection failure.
    fn expand(&self, value: &str) -> Result<String, Reject> {
        let value = value.trim().trim_matches('"');
        if value.starts_with('%') && value.ends_with('%') {
            if value.len() < 2 {
                return Err(reject(
                    "PackageInspectionFailed",
                    "The driver INF has an invalid string reference.",
                ));
            }
            if let Some(expanded) = self.strings.get(&value[1..value.len() - 1].to_uppercase()) {
                return Ok(expanded.clone());
            }
        }
        Ok(value.to_owned())
    }

    /// Managed `Values`: the expanded, comma-separated values after the
    /// first `=`, or none.
    fn values(&self, line: &str) -> Result<Vec<String>, Reject> {
        match line.split_once('=') {
            Some((_, values)) => values.split(',').map(|value| self.expand(value)).collect(),
            None => Ok(Vec::new()),
        }
    }
}

/// Managed `Key`: the trimmed text before the first `=`.
fn key(line: &str, name: &str) -> bool {
    eq_ci(line.split('=').next().unwrap_or("").trim(), name)
}

fn any<'a>(
    entries: &[&'a str],
    mut predicate: impl FnMut(&'a str) -> Result<bool, Reject>,
) -> Result<bool, Reject> {
    for entry in entries {
        if predicate(entry)? {
            return Ok(true);
        }
    }
    Ok(false)
}

fn parse(text: &str) -> Result<Inf<'_>, Reject> {
    let mut sections: HashMap<String, Vec<&str>> = HashMap::new();
    let mut current: Option<String> = None;
    for raw in text.split('\n') {
        let line = raw.split(';').next().unwrap_or("").trim();
        if line.len() >= 2 && line.starts_with('[') && line.ends_with(']') {
            let name = line[1..line.len() - 1].to_uppercase();
            if sections.contains_key(&name) {
                return Err(reject(
                    "DuplicateInfSection",
                    "Duplicate driver INF section.",
                ));
            }
            sections.insert(name.clone(), Vec::new());
            current = Some(name);
        } else if !line.is_empty()
            && let Some(section) = current.as_ref().and_then(|name| sections.get_mut(name))
        {
            section.push(line);
        }
    }
    let mut strings = HashMap::new();
    for line in sections.get("STRINGS").map_or(&[][..], Vec::as_slice) {
        if let Some((name, value)) = line.split_once('=') {
            strings.insert(
                name.trim().to_uppercase(),
                value.trim().trim_matches('"').to_owned(),
            );
        }
    }
    Ok(Inf { sections, strings })
}

/// The catalog file name the INF declares for the model that installs
/// `service` on `hardware_id`.
pub(crate) fn validate_contract(
    text: &str,
    service: &str,
    hardware_id: &str,
) -> Result<String, Reject> {
    if text.encode_utf16().count() > MAXIMUM_LENGTH {
        return Err(reject(
            "InfTooLarge",
            "The driver INF exceeds the inspection bound.",
        ));
    }
    let inf = parse(text)?;
    let version = inf.section("Version");
    let provider = any(version, |entry| {
        Ok(key(entry, "Provider")
            && matches!(
                inf.values(entry)?.as_slice(),
                [provider] if matches!(provider.as_str(), "Razer Inc" | "Razer Inc." | "Razer")
            ))
    })?;
    if !provider {
        return Err(reject(
            "ProviderMismatch",
            "The signed INF does not identify the Razer provider.",
        ));
    }
    let mut models = Vec::new();
    for line in inf.section("Manufacturer") {
        let values = inf.values(line)?;
        if values.len() < 2 {
            continue;
        }
        for decoration in values[1..]
            .iter()
            .filter(|value| eq_ci(value, "NTamd64") || starts_with_ci(value, "NTamd64."))
        {
            models.push(format!("{}.{decoration}", values[0]));
        }
    }
    let binary = format!("%12%\\{service}.sys");
    for model in &models {
        for line in inf.section(model) {
            let values = inf.values(line)?;
            if values.len() < 2 || !values[1..].iter().any(|id| eq_ci(id, hardware_id)) {
                continue;
            }
            for suffix in [".NTamd64.Services", ".NT.Services", ".Services"] {
                for service_line in inf.section(&format!("{}{suffix}", values[0])) {
                    let declaration = inf.values(service_line)?;
                    if !key(service_line, "AddService")
                        || declaration.len() < 3
                        || !eq_ci(&declaration[0], service)
                    {
                        continue;
                    }
                    let section = inf.section(&declaration[2]);
                    let kernel = any(section, |entry| {
                        Ok(key(entry, "ServiceType")
                            && matches!(inf.values(entry)?.as_slice(), [kind] if kind == "1"))
                    })?;
                    let image = any(section, |entry| {
                        Ok(key(entry, "ServiceBinary")
                            && matches!(inf.values(entry)?.as_slice(), [path] if eq_ci(path, &binary)))
                    })?;
                    if !kernel || !image {
                        continue;
                    }
                    let mut catalogs: Vec<String> = Vec::new();
                    for entry in version {
                        if key(entry, "CatalogFile.NTamd64") || key(entry, "CatalogFile") {
                            for value in inf.values(entry)? {
                                if !catalogs.iter().any(|known| eq_ci(known, &value)) {
                                    catalogs.push(value);
                                }
                            }
                        }
                    }
                    let [catalog] = catalogs.as_slice() else {
                        return Err(reject(
                            "CatalogMissingOrAmbiguous",
                            "The installed package catalog is ambiguous or missing.",
                        ));
                    };
                    require_file_name(catalog)?;
                    if !ends_with_ci(catalog, ".cat") {
                        return Err(reject(
                            "CatalogExtensionMismatch",
                            "The package catalog has an invalid extension.",
                        ));
                    }
                    return Ok(catalog.clone());
                }
            }
        }
    }
    Err(reject(
        "HardwareServiceMismatch",
        "The installed INF does not bind the exact hardware and Razer API service.",
    ))
}

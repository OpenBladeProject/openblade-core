//! Installed Windows driver API trust: managed `WindowsRazerDriverApiDiscovery`
//! and `WindowsDriverApiTrust` (`src/OpenBlade.Platform`).
//!
//! `package::capture` binds an exact device endpoint to its installed driver
//! package: the kernel service and its System32\drivers image, the endpoint's
//! published INF and its DriverStore copy, the INF's x64 model, service and
//! catalog declarations, the active image against the stored one, and
//! WinVerifyTrust of the catalog and its members under the Microsoft Windows
//! Hardware Compatibility Publisher. Nothing is allowlisted by build or hash:
//! a compatible update that keeps the binding and the trust is admitted.
//!
//! The pure parts (path resolution, the INF contract, the member-algorithm
//! policy and the trust cache) are tested here; the Windows calls are in
//! `windows` and are reached only through the `Machine` boundary.
//!
//! The NVML loader is the next consumer of `verify_authenticode_file` and
//! `verify_catalog_members`; it does not call them yet.

pub(crate) mod inf;
pub(crate) mod package;
pub(crate) mod paths;
#[cfg(not(test))]
pub(crate) mod windows;

#[cfg(test)]
mod tests;

/// Managed `WindowsDriverApiTrust.MicrosoftHardwareCompatibilityPublisher`.
pub(crate) const MICROSOFT_HARDWARE_COMPATIBILITY_PUBLISHER: &str = "CN=Microsoft Windows Hardware Compatibility Publisher, O=Microsoft Corporation, L=Redmond, S=Washington, C=US";

/// Managed `TRUST_E_NOSIGNATURE`.
pub(crate) const TRUST_E_NOSIGNATURE: i32 = 0x800B_0100_u32 as i32;

/// A validator's own rejection: its managed reason code and message
/// (`WindowsRazerDriverApiDiscovery.Reject`).
#[derive(Clone, Debug, PartialEq, Eq)]
pub(crate) struct Reject {
    pub(crate) reason: &'static str,
    pub(crate) message: String,
}

pub(crate) fn reject(reason: &'static str, message: impl Into<String>) -> Reject {
    Reject {
        reason,
        message: message.into(),
    }
}

impl From<std::io::Error> for Reject {
    fn from(error: std::io::Error) -> Self {
        reject("PackageInspectionFailed", error.to_string())
    }
}

/// Managed `VerifyCatalogMemberAlgorithms`: the member is looked up by its
/// SHA256 hash, and by SHA1 only when Windows reports the SHA256 lookup as
/// unsigned. The content identity stays SHA256 either way.
pub(crate) fn verify_member_algorithms(
    mut verify: impl FnMut(&str) -> std::io::Result<i32>,
) -> std::io::Result<()> {
    let mut status = verify("SHA256")?;
    if status == 0 {
        return Ok(());
    }
    if status == TRUST_E_NOSIGNATURE {
        status = verify("SHA1")?;
        if status == 0 {
            return Ok(());
        }
    }
    Err(std::io::Error::other(format!(
        "Windows rejected driver catalog member trust (0x{:08X}).",
        status as u32
    )))
}

/// Managed `ValidatePublishers`: one to 16 unique, non-empty, trimmed full
/// subjects of at most 512 characters.
pub(crate) fn validate_publishers<'a>(publishers: &[&'a str]) -> std::io::Result<Vec<&'a str>> {
    if publishers.is_empty() || publishers.len() > 16 {
        return Err(std::io::Error::other(
            "At least one bounded publisher subject is required.",
        ));
    }
    let mut unique = Vec::with_capacity(publishers.len());
    for publisher in publishers {
        if publisher.is_empty()
            || publisher.encode_utf16().count() > 512
            || *publisher != publisher.trim()
            || unique.contains(publisher)
        {
            return Err(std::io::Error::other(
                "Publisher subjects must be non-empty, bounded, unique full subjects.",
            ));
        }
        unique.push(*publisher);
    }
    Ok(unique)
}

/// Managed `MatchesExpectedPublisher`: an exact, ordinal subject match.
pub(crate) fn matches_publisher(subject: &str, expected: &[&str]) -> bool {
    expected.contains(&subject)
}

/// One verified file as the content identity reads it: its path, length
/// and SHA256 hash.
pub(crate) struct Content<'a> {
    pub(crate) path: &'a str,
    pub(crate) length: u64,
    pub(crate) hash: [u8; 32],
}

/// Managed `ComputeObservedContentIdentity`: a SHA256 over a versioned
/// prefix, the file count and, per file in order, its upper-cased file
/// name's UTF-8 length and bytes, its length and its own hash. It names an
/// observed runtime instance; it is never compared with an allowlist.
pub(crate) fn content_identity(files: &[Content<'_>]) -> String {
    use sha2::{Digest, Sha256};
    let mut identity = Sha256::new();
    identity.update(b"OpenBlade.WindowsDriverApiTrust.v1\0");
    identity.update((files.len() as i32).to_le_bytes());
    for file in files {
        let name = paths::file_name(file.path).to_uppercase();
        identity.update((name.len() as i32).to_le_bytes());
        identity.update(name.as_bytes());
        identity.update((file.length as i64).to_le_bytes());
        identity.update(file.hash);
    }
    let digest: String = identity
        .finalize()
        .iter()
        .map(|byte| format!("{byte:02X}"))
        .collect();
    format!("SHA256:{digest}")
}

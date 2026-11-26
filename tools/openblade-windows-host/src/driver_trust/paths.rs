//! Windows path spellings as managed resolves them: `Path.GetFullPath`,
//! `Path.Combine`, `WindowsDriverServiceImagePath.Resolve` and the
//! discovery's `ResolveServiceImage`, `RequireUnder` and `RequireFileName`.
//! Only local drive paths are produced; a UNC or device path is never under
//! a protected Windows directory, so it fails `require_under` as managed's
//! does.

use super::{Reject, reject};

pub(crate) fn starts_with_ci(text: &str, prefix: &str) -> bool {
    text.len() >= prefix.len()
        && text.is_char_boundary(prefix.len())
        && text[..prefix.len()].eq_ignore_ascii_case(prefix)
}

pub(crate) fn ends_with_ci(text: &str, suffix: &str) -> bool {
    text.len() >= suffix.len()
        && text.is_char_boundary(text.len() - suffix.len())
        && text[text.len() - suffix.len()..].eq_ignore_ascii_case(suffix)
}

pub(crate) fn eq_ci(left: &str, right: &str) -> bool {
    left.eq_ignore_ascii_case(right) || left.to_uppercase() == right.to_uppercase()
}

fn separator(character: char) -> bool {
    matches!(character, '\\' | '/')
}

fn drive(path: &str) -> bool {
    let bytes = path.as_bytes();
    bytes.len() >= 2 && bytes[0].is_ascii_alphabetic() && bytes[1] == b':'
}

/// `Path.IsPathRooted`: a leading separator or a drive.
pub(crate) fn is_rooted(path: &str) -> bool {
    path.starts_with(separator) || drive(path)
}

/// `Path.IsPathFullyQualified`: a drive with a separator, or a UNC or
/// device prefix.
pub(crate) fn is_fully_qualified(path: &str) -> bool {
    let mut characters = path.chars();
    match (characters.next(), characters.next()) {
        (Some(first), Some(second)) if separator(first) && separator(second) => true,
        _ => drive(path) && path[2..].starts_with(separator),
    }
}

/// `Path.Combine(root, child)`: the child when it is rooted.
pub(crate) fn combine(root: &str, child: &str) -> String {
    if is_rooted(child) || root.is_empty() {
        return child.to_owned();
    }
    if root.ends_with(separator) {
        format!("{root}{child}")
    } else {
        format!("{root}\\{child}")
    }
}

/// `Path.GetFullPath` of a fully qualified drive path: separators become
/// `\`, repeats collapse, `.` and `..` resolve (never above the root), and
/// the root keeps its trailing separator. Anything else is None.
pub(crate) fn full_path(path: &str) -> Option<String> {
    if !drive(path) || !path[2..].starts_with(separator) {
        return None;
    }
    let mut segments: Vec<&str> = Vec::new();
    for segment in path[3..].split(separator) {
        match segment {
            "" | "." => {}
            ".." => {
                segments.pop();
            }
            other => segments.push(other),
        }
    }
    let root = format!("{}:\\", path[..1].to_ascii_uppercase());
    let mut full = root;
    full.push_str(&segments.join("\\"));
    if path.ends_with(separator) && !segments.is_empty() {
        full.push('\\');
    }
    Some(full)
}

/// `Path.GetFileName`.
pub(crate) fn file_name(path: &str) -> &str {
    path.rsplit(separator).next().unwrap_or(path)
}

/// `Path.GetDirectoryName` of a full path; None at the root.
pub(crate) fn parent(path: &str) -> Option<&str> {
    let trimmed = path.trim_end_matches(separator);
    if trimmed.len() <= 2 {
        return None;
    }
    let index = trimmed.rfind(separator)?;
    Some(if index == 2 {
        &path[..3]
    } else {
        &trimmed[..index]
    })
}

/// Managed `WindowsDriverServiceImagePath.Resolve`.
pub(crate) fn resolve_image(
    value: &str,
    windows: &str,
    allow_system32_relative: bool,
) -> Result<String, &'static str> {
    const AMBIGUOUS: &str = "The driver image has an ambiguous SystemRoot path.";
    let system_root = |suffix: &str| {
        if is_rooted(suffix) {
            Err(AMBIGUOUS)
        } else {
            Ok(combine(windows, suffix))
        }
    };
    let path = if starts_with_ci(value, r"\SystemRoot\") {
        system_root(&value[12..])?
    } else if starts_with_ci(value, r"%SystemRoot%\") {
        system_root(&value[13..])?
    } else if let Some(rest) = value.strip_prefix(r"\??\") {
        rest.to_owned()
    } else if starts_with_ci(value, r"\DosDevices\") {
        value[12..].to_owned()
    } else if allow_system32_relative && starts_with_ci(value, r"System32\") {
        combine(windows, value)
    } else {
        value.to_owned()
    };
    // GetFullPath alone would resolve a relative path against the process
    // directory, which is not the driver's Windows root.
    if !is_fully_qualified(&path) {
        return Err("The driver image path is not absolute or a supported Windows alias.");
    }
    Ok(path)
}

/// Managed `RequireUnder`.
pub(crate) fn require_under(path: &str, root: &str) -> Result<(), Reject> {
    let outside = || {
        reject(
            "UnprotectedPath",
            "The driver path is outside its protected Windows directory.",
        )
    };
    let path = full_path(path).ok_or_else(outside)?;
    let root = full_path(root).ok_or_else(outside)?;
    let prefix = format!("{}\\", root.trim_end_matches('\\'));
    if starts_with_ci(&path, &prefix) {
        Ok(())
    } else {
        Err(outside())
    }
}

/// Managed `RequireFileName`.
pub(crate) fn require_file_name(name: &str) -> Result<(), Reject> {
    if name.trim().is_empty() || name.contains(separator) || name.contains(':') {
        return Err(reject(
            "InvalidPackageFileName",
            "Invalid installed package filename.",
        ));
    }
    Ok(())
}

/// Managed `ResolveServiceImage`: the unexpanded ImagePath, resolved, under
/// `%windir%\System32\drivers` and named `<service>.sys`.
pub(crate) fn resolve_service_image(
    value: Option<&str>,
    windows: &str,
    service: &str,
) -> Result<String, Reject> {
    let Some(value) = value.filter(|value| !value.trim().is_empty()) else {
        return Err(reject(
            "ServiceImageMissing",
            "The driver service image is unavailable.",
        ));
    };
    let path = resolve_image(value.trim(), windows, true)
        .map_err(|message| reject("ServiceImagePathAmbiguous", message))?;
    let drivers = combine(&combine(windows, "System32"), "drivers");
    require_under(&path, &drivers)?;
    let path = full_path(&path).expect("a path under the drivers directory is a drive path");
    if !eq_ci(file_name(&path), &format!("{service}.sys")) {
        return Err(reject(
            "ServiceImageNameMismatch",
            "The service image is not the selected Razer API driver.",
        ));
    }
    Ok(path)
}

/// Managed `WindowsDriverApiTrust.MaximumPathLength`, in UTF-16 units.
pub(crate) const MAXIMUM_PATH: usize = 4096;

/// Managed `NormalizeFilePath`: a bounded, fully qualified local drive path
/// without alternate streams.
pub(crate) fn normalize(path: &str) -> Result<String, &'static str> {
    let bounded = |path: &str| path.encode_utf16().count() <= MAXIMUM_PATH;
    if path.trim().is_empty() || !bounded(path) || !is_fully_qualified(path) {
        return Err("Driver API paths must be bounded, fully qualified local paths.");
    }
    match full_path(path) {
        Some(full) if bounded(&full) && !full[3..].contains(':') => Ok(full),
        _ => Err("Driver API paths must use a bounded local drive path without alternate streams."),
    }
}

/// Managed `EnsureNoReparsePoints` over `is_reparse_point`, which reads one
/// path's attributes: the path and every directory above it, up to but not
/// including the drive root.
pub(crate) fn ensure_no_reparse_points(
    path: &str,
    mut is_reparse_point: impl FnMut(&str) -> std::io::Result<bool>,
) -> std::io::Result<()> {
    let mut current = path;
    while current.len() > 3 {
        if is_reparse_point(current)? {
            return Err(std::io::Error::other(
                "Driver API paths cannot contain reparse points.",
            ));
        }
        current = parent(current).ok_or_else(|| {
            std::io::Error::other("The driver API path has no local filesystem root.")
        })?;
    }
    Ok(())
}

/// Managed `OpenProtectedFile`'s walk over `is_reparse_point`, which reads
/// one path's attributes: the path and every directory above it, the drive
/// root included.
pub(crate) fn ensure_protected_components(
    path: &str,
    mut is_reparse_point: impl FnMut(&str) -> std::io::Result<bool>,
) -> Result<(), Reject> {
    let mut current = Some(path);
    while let Some(path) = current {
        if is_reparse_point(path)? {
            return Err(reject(
                "ReparsePoint",
                "Driver package paths may not contain reparse points.",
            ));
        }
        current = parent(path);
    }
    Ok(())
}

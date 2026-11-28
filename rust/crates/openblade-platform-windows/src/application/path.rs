pub(super) const MAX_PATH_UNITS: usize = 32_767;
pub(super) const PATH_BUFFER_UNITS: usize = MAX_PATH_UNITS + 1;

// The length excludes the terminator. Lossy UTF-16 could identify a different path.
pub(super) fn decode(buffer: &[u16], length: usize) -> Option<String> {
    if !(1..=MAX_PATH_UNITS).contains(&length) || buffer.get(length) != Some(&0) {
        return None;
    }
    let units = &buffer[..length];
    if units.contains(&0) {
        return None;
    }
    String::from_utf16(units).ok()
}

pub(super) fn normalize(
    path: &str,
    full_path: impl FnOnce(&str) -> Option<String>,
) -> Option<String> {
    if path.len() > MAX_PATH_UNITS * 3 {
        return None;
    }
    let input = path.trim();
    let extended = qualified(input)?;
    let normalized = if extended {
        input.to_owned()
    } else {
        full_path(input)?
    };
    let normalized = normalized.trim_end_matches(['\\', '/']);
    // .NET can expand short aliases on ordinary paths. Decline a spelling
    // that could need a filesystem lookup in this observation-only adapter.
    if extended != qualified(normalized)? || (!extended && normalized.contains('~')) {
        return None;
    }
    let file = normalized.rsplit(['\\', '/']).next()?;
    let (_, extension) = file.rsplit_once('.')?;
    extension
        .eq_ignore_ascii_case("exe")
        .then(|| normalized.to_owned())
}

fn qualified(path: &str) -> Option<bool> {
    if path.len() > MAX_PATH_UNITS * 3
        || path.encode_utf16().count() > MAX_PATH_UNITS
        || path.chars().any(char::is_control)
    {
        return None;
    }
    if let Some(rest) = path.strip_prefix(r"\\?\") {
        if rest.contains('/') {
            return None;
        }
        if let Some(prefix) = rest.get(..4)
            && prefix.eq_ignore_ascii_case("UNC\\")
        {
            return unc(&rest[4..]).then_some(true);
        }
        return drive(rest).then_some(true);
    }
    if drive(path) {
        return Some(false);
    }
    let bytes = path.as_bytes();
    (bytes.len() >= 2 && separator(bytes[0]) && separator(bytes[1]) && unc(&path[2..]))
        .then_some(false)
}

fn separator(byte: u8) -> bool {
    matches!(byte, b'\\' | b'/')
}

fn drive(path: &str) -> bool {
    let bytes = path.as_bytes();
    bytes.len() >= 3
        && bytes[0].is_ascii_alphabetic()
        && bytes[1] == b':'
        && separator(bytes[2])
        && !path[3..].contains(':')
}

fn unc(rest: &str) -> bool {
    let mut parts = rest.splitn(3, ['\\', '/']);
    let root_component = |name: &str| !matches!(name, "" | "." | "?" | "..") && !name.contains(':');
    parts.next().is_some_and(root_component)
        && parts.next().is_some_and(root_component)
        && parts
            .next()
            .is_some_and(|name| !name.is_empty() && !name.contains(':'))
}

/// Match .NET 10 ordinal comparison in system ICU mode. BMP casing uses ICU
/// with two exceptions; supplementary casing uses the managed Unicode data.
pub(super) fn ordinal_ignore_case(
    first: &str,
    second: &str,
    uppercase: impl Fn(char) -> u32,
) -> bool {
    if first.len().max(second.len()) > MAX_PATH_UNITS * 3 {
        return false;
    }
    let length = first.encode_utf16().count();
    if length > MAX_PATH_UNITS || length != second.encode_utf16().count() {
        return false;
    }
    let ordinal_upper = |c: char| match c {
        '\u{0131}' | '\u{017f}' => c as u32,
        c if c.is_ascii() => c.to_ascii_uppercase() as u32,
        c if c.len_utf16() == 2 => supplementary_uppercase(c as u32),
        c => uppercase(c),
    };
    first
        .chars()
        .map(ordinal_upper)
        .eq(second.chars().map(ordinal_upper))
}

// These are the non-identity ranges of .NET 10's CharUnicodeInfo.ToUpper(uint)
// for supplementary scalars. SurrogateCasing uses that table rather than ICU.
// Do not use Rust's newer Unicode table here: it also folds Beria Erfe.
pub(super) fn supplementary_uppercase(scalar: u32) -> u32 {
    scalar
        - match scalar {
            0x10428..=0x1044f | 0x104d8..=0x104fb => 0x28,
            0x10597..=0x105a1 | 0x105a3..=0x105b1 | 0x105b3..=0x105b9 | 0x105bb..=0x105bc => 0x27,
            0x10cc0..=0x10cf2 => 0x40,
            0x10d70..=0x10d85 | 0x118c0..=0x118df | 0x16e60..=0x16e7f => 0x20,
            0x1e922..=0x1e943 => 0x22,
            _ => 0,
        }
}

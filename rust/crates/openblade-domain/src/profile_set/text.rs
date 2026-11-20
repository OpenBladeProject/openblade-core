//! The .NET string and path rules the profile contract depends on, without
//! the operating system: lengths are UTF-16 code units, as `string.Length` is.

use super::ordinal_case::FOLDS;
use std::string::String;

/// `string.Length`.
pub fn utf16_length(text: &str) -> usize {
    text.encode_utf16().count()
}

/// `text.Any(char.IsControl)`: the Unicode `Cc` category.
pub fn has_control(text: &str) -> bool {
    text.chars().any(char::is_control)
}

/// `string.IsNullOrWhiteSpace` for a non-null string.
pub fn is_white_space(text: &str) -> bool {
    text.chars().all(char::is_whitespace)
}

/// `Guid` text as `Utf8JsonReader.GetGuid` reads it and `JsonSerializer`
/// writes it back: 36 hyphenated hexadecimal characters, lower case.
pub fn guid(text: &str) -> Option<String> {
    let bytes = text.as_bytes();
    let shaped = bytes.len() == 36
        && bytes.iter().enumerate().all(|(index, byte)| match index {
            8 | 13 | 18 | 23 => *byte == b'-',
            _ => byte.is_ascii_hexdigit(),
        });
    shaped.then(|| text.to_ascii_lowercase())
}

/// `string.ToLowerInvariant`: simple case mapping, one character at a time.
fn lower_invariant(text: &str) -> String {
    text.chars()
        .map(|character| {
            let mut lower = character.to_lowercase();
            match (lower.next(), lower.next()) {
                (Some(single), None) => single,
                // U+0130 is the one character whose full lower case is longer;
                // the invariant mapping leaves it alone.
                _ => character,
            }
        })
        .collect()
}

/// The character managed `StringComparison.OrdinalIgnoreCase` compares
/// `character` as: the least member of the set it holds equal. The sets are
/// managed's own, recorded by a managed test and compiled in as a table, so
/// this does not follow the case tables of the Rust standard library or of
/// Windows, which both differ from managed's for some letters.
pub fn fold_ordinal_case(character: char) -> char {
    let scalar = character as u32;
    let index = FOLDS.partition_point(|&(_, last, _, _)| last < scalar);
    match FOLDS.get(index) {
        Some(&(first, _, step, folded))
            if scalar >= first && (scalar - first).is_multiple_of(step) =>
        {
            char::from_u32(folded + (scalar - first)).unwrap_or(character)
        }
        _ => character,
    }
}

/// Managed `StringComparison.OrdinalIgnoreCase` equality, which finds
/// duplicate executable paths and rule names. Characters are compared one to
/// one: a sharp s is not `SS`, and a dotless i is not `I`.
pub fn same_ordinal_ignoring_case(left: &str, right: &str) -> bool {
    left.chars()
        .map(fold_ordinal_case)
        .eq(right.chars().map(fold_ordinal_case))
}

/// Managed `ThxApplicationAudioRule.NormalizeExecutableName`; `None` where
/// its constructor throws.
pub fn normalize_thx_executable_name(name: &str) -> Option<String> {
    let normalized = lower_invariant(name.trim());
    let valid = !normalized.is_empty()
        && utf16_length(&normalized) <= 260
        && !has_control(&normalized)
        && !normalized.contains(['<', '>', ':', '"', '/', '\\', '|', '?', '*'])
        && !normalized.ends_with('.');
    valid.then_some(normalized)
}

/// Whether `path` equals managed
/// `ApplicationProfileDefinition.NormalizeExecutablePath(path)`: one fully
/// qualified Windows path to an `.exe`, already in `Path.GetFullPath` form.
///
/// `GetFullPath` returns a `\\?\` or `\??\` path as written. For every other
/// path this accepts only what it returns unchanged: a drive, a `\\.\` device
/// or a `\\` network root, backslash separators, no empty, `.` or `..`
/// segment, and no segment ending in a period or, for the last one, a space.
/// It does not expand an 8.3 short name, which managed does when that alias
/// exists on disk.
pub fn is_normalized_executable_path(path: &str) -> bool {
    if path.is_empty()
        || path.trim() != path
        || utf16_length(path) > 32767
        || has_control(path)
        || !path.to_ascii_lowercase().ends_with(".exe")
    {
        return false;
    }
    if path.starts_with(r"\\?\") || path.starts_with(r"\??\") {
        return !path.ends_with(['\\', '/']);
    }
    let bytes = path.as_bytes();
    let rest = if bytes.len() >= 3
        && bytes[0].is_ascii_alphabetic()
        && bytes[1] == b':'
        && bytes[2] == b'\\'
    {
        &path[3..]
    } else if let Some(rooted) = path.strip_prefix(r"\\.\").or(path.strip_prefix(r"\\")) {
        rooted
    } else {
        return false;
    };
    !rest.contains('/')
        && rest.split('\\').all(|segment| {
            !segment.is_empty() && !segment.ends_with('.')
                || segment.len() > 2 && segment.bytes().all(|b| b == b'.')
        })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn guids_bind_in_the_hyphenated_form_only() {
        assert_eq!(
            guid("0F8FAD5B-D9CB-469F-A165-70867728950E").as_deref(),
            Some("0f8fad5b-d9cb-469f-a165-70867728950e")
        );
        for refused in [
            "0f8fad5bd9cb469fa16570867728950e",
            "{0f8fad5b-d9cb-469f-a165-70867728950e}",
            "0f8fad5b-d9cb-469f-a165-70867728950",
            "0f8fad5b-d9cb-469f-a165-70867728950g",
            "0f8fad5b-d9cb-469f-a16570867-728950e",
        ] {
            assert_eq!(guid(refused), None, "{refused}");
        }
    }

    #[test]
    fn thx_names_normalise_like_the_managed_constructor() {
        assert_eq!(
            normalize_thx_executable_name(" Game.EXE ").as_deref(),
            Some("game.exe")
        );
        assert_eq!(
            normalize_thx_executable_name("\u{130}.exe").as_deref(),
            Some("\u{130}.exe")
        );
        for refused in [
            "",
            "  ",
            ".",
            "..",
            "a.",
            "a/b.exe",
            "a\\b.exe",
            "a:b",
            "a\u{1}.exe",
            "a?.exe",
        ] {
            assert_eq!(normalize_thx_executable_name(refused), None, "{refused:?}");
        }
        assert!(normalize_thx_executable_name(&"a".repeat(260)).is_some());
        assert!(normalize_thx_executable_name(&"a".repeat(261)).is_none());
    }

    #[test]
    fn executable_paths_must_already_be_normalised() {
        for accepted in [
            r"C:\Games\Game.exe",
            r"c:\a b\x.EXE",
            r"\\server\share\x.exe",
            r"\\?\C:\Games\..\x.exe",
            r"C:\a\...\x.exe",
            r"C:\.exe",
        ] {
            assert!(is_normalized_executable_path(accepted), "{accepted}");
        }
        for refused in [
            "",
            r"Game.exe",
            r"C:Game.exe",
            r"\Games\Game.exe",
            r"C:/Games/Game.exe",
            r"C:\Games/Game.exe",
            r"C:\Games\\Game.exe",
            r"C:\Games\.\Game.exe",
            r"C:\Games\..\Game.exe",
            r"C:\Games.\Game.exe",
            r"C:\Games\Game.exe ",
            r" C:\Games\Game.exe",
            r"C:\Games\Game.exe\",
            r"C:\Games\Game.txt",
            "C:\\Games\\Ga\u{7}me.exe",
            r"1:\Game.exe",
        ] {
            assert!(!is_normalized_executable_path(refused), "{refused:?}");
        }
    }
}

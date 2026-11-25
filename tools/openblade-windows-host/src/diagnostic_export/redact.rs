//! Managed redaction. `redact_text` is `DiagnosticText.Redact`, which the
//! history applies to every recorded value; `Plan` is the exporter's
//! `CreateRedactionPlan` and `RedactString`, which it applies to the archive
//! text with the host's sensitive tokens.
//!
//! The four managed patterns are matched by hand, with their leftmost,
//! non-overlapping replacement and backtracking order. Character classes use
//! Rust's Unicode properties: `\s` is `char::is_whitespace` (the same set as
//! .NET), and the letter, number and word classes are `is_alphanumeric` and
//! `_`, which also count the few `Other_Alphabetic` marks .NET does not.

// `redact_text` serves the history, which only the profile store records.
#![cfg_attr(not(any(test, feature = "service-profiles-v1")), allow(dead_code))]

const REDACTED: &str = "[REDACTED]";

/// Managed `DiagnosticText.Redact(value, tokens)`.
pub(crate) fn redact_text(value: &str, tokens: &[String]) -> String {
    let value = replace_matches(value, url_credential);
    let value = replace_matches(&value, authorization);
    let value = replace_matches(&value, credential);
    let mut value = replace_matches(&value, user_path);
    let mut distinct: Vec<&str> = Vec::new();
    for token in tokens.iter().filter(|token| !is_blank(token)) {
        if !distinct
            .iter()
            .any(|seen| ordinal_ignore_case_eq(seen, token))
        {
            distinct.push(token);
        }
    }
    // OrderByDescending is stable: equal lengths keep their first-seen order.
    distinct.sort_by_key(|token| std::cmp::Reverse(utf16_len(token)));
    for token in distinct {
        value = replace_matches(&value, |text, start| bounded_token(text, start, token));
    }
    value
}

/// Managed `CreateRedactionPlan`: the non-blank tokens, longest first, and
/// the replacement, which is empty when a token occurs inside `[REDACTED]`.
pub(crate) struct Plan {
    tokens: Vec<String>,
    replacement: &'static str,
}

impl Plan {
    pub(crate) fn new(tokens: &[String]) -> Self {
        let mut tokens: Vec<String> = tokens
            .iter()
            .filter(|token| !is_blank(token))
            .cloned()
            .collect();
        tokens.sort_by_key(|token| std::cmp::Reverse(utf16_len(token)));
        let replacement = if tokens
            .iter()
            .any(|token| find_ignore_case(REDACTED, token, 0).is_some())
        {
            ""
        } else {
            REDACTED
        };
        Self {
            tokens,
            replacement,
        }
    }

    /// Managed `RedactString`: each token in turn, every ordinal
    /// case-insensitive occurrence.
    pub(crate) fn apply(&self, text: &str) -> String {
        let mut text = text.to_owned();
        for token in &self.tokens {
            let mut result = String::with_capacity(text.len());
            let mut from = 0;
            while let Some((start, length)) = find_ignore_case(&text, token, from) {
                result.push_str(&text[from..start]);
                result.push_str(self.replacement);
                from = start + length;
            }
            result.push_str(&text[from..]);
            text = result;
        }
        text
    }
}

/// `string.IsNullOrWhiteSpace`.
pub(crate) fn is_blank(value: &str) -> bool {
    value.chars().all(char::is_whitespace)
}

/// Managed string length: UTF-16 code units.
pub(crate) fn utf16_len(value: &str) -> usize {
    value.chars().map(char::len_utf16).sum()
}

/// .NET `OrdinalIgnoreCase`: the simple invariant uppercase of each character.
fn fold(character: char) -> char {
    let mut upper = character.to_uppercase();
    match (upper.next(), upper.next()) {
        (Some(single), None) => single,
        _ => character,
    }
}

fn ordinal_ignore_case_eq(left: &str, right: &str) -> bool {
    left.chars().map(fold).eq(right.chars().map(fold))
}

/// The byte offset and byte length in `text` of the first ordinal
/// case-insensitive occurrence of `token` at or after `from`.
fn find_ignore_case(text: &str, token: &str, from: usize) -> Option<(usize, usize)> {
    text[from..].char_indices().find_map(|(offset, _)| {
        let start = from + offset;
        prefix_len(&text[start..], token, fold).map(|length| (start, length))
    })
}

/// The byte length in `text` of a prefix equal to `pattern` under `map`.
fn prefix_len(text: &str, pattern: &str, map: impl Fn(char) -> char) -> Option<usize> {
    let mut length = 0;
    let mut characters = text.chars();
    for expected in pattern.chars() {
        let actual = characters.next()?;
        if map(actual) != map(expected) {
            return None;
        }
        length += actual.len_utf8();
    }
    Some(length)
}

fn ascii_prefix(text: &str, pattern: &str) -> Option<usize> {
    prefix_len(text, pattern, |character| character.to_ascii_lowercase())
}

/// Replaces every leftmost non-overlapping match. `matcher` returns the
/// match's end and its replacement for a match starting at `start`.
fn replace_matches(text: &str, matcher: impl Fn(&str, usize) -> Option<(usize, String)>) -> String {
    let mut result = String::with_capacity(text.len());
    let mut copied = 0;
    let mut position = 0;
    while position <= text.len() {
        if let Some((end, replacement)) = matcher(text, position) {
            result.push_str(&text[copied..position]);
            result.push_str(&replacement);
            copied = end;
            position = if end > position {
                end
            } else {
                next_boundary(text, position)
            };
        } else {
            position = next_boundary(text, position);
        }
    }
    result.push_str(&text[copied..]);
    result
}

fn next_boundary(text: &str, position: usize) -> usize {
    text[position..]
        .chars()
        .next()
        .map_or(text.len() + 1, |character| position + character.len_utf8())
}

fn is_word(character: char) -> bool {
    character.is_alphanumeric() || character == '_'
}

fn previous(text: &str, position: usize) -> Option<char> {
    text[..position].chars().next_back()
}

/// `\b` before a keyword that starts with a word character.
fn word_start(text: &str, position: usize) -> bool {
    !previous(text, position).is_some_and(is_word)
}

/// The byte length of the longest run of characters `accept` takes.
fn run(text: &str, accept: impl Fn(char) -> bool) -> usize {
    text.char_indices()
        .find(|&(_, character)| !accept(character))
        .map_or(text.len(), |(offset, _)| offset)
}

/// `://[^/\s:@]+:[^/\s@]+@` -> `://[REDACTED]@`.
fn url_credential(text: &str, start: usize) -> Option<(usize, String)> {
    let rest = text[start..].strip_prefix("://")?;
    let user = run(rest, |c| {
        !(c == '/' || c.is_whitespace() || c == ':' || c == '@')
    });
    let rest = rest[user..].strip_prefix(':').filter(|_| user > 0)?;
    let secret = run(rest, |c| !(c == '/' || c.is_whitespace() || c == '@'));
    rest[secret..].strip_prefix('@').filter(|_| secret > 0)?;
    let end = start + 3 + user + 1 + secret + 1;
    Some((end, format!("://{REDACTED}@")))
}

/// `\s*[:=]\s*` after a keyword and its optional quote: the bytes it takes.
fn assignment(text: &str) -> Option<usize> {
    let quote = usize::from(text.starts_with(['"', '\'']));
    let rest = &text[quote..];
    let space = run(rest, char::is_whitespace);
    let rest = rest[space..].strip_prefix([':', '='])?;
    Some(quote + space + 1 + run(rest, char::is_whitespace))
}

/// `(?i)\bauthorization["']?\s*[:=]\s*(?:Bearer|Basic)\s+[^\s,;]+`.
fn authorization(text: &str, start: usize) -> Option<(usize, String)> {
    if !word_start(text, start) {
        return None;
    }
    let rest = &text[start..];
    let keyword = ascii_prefix(rest, "authorization")?;
    let mut end = keyword + assignment(&rest[keyword..])?;
    end += ascii_prefix(&rest[end..], "bearer").or_else(|| ascii_prefix(&rest[end..], "basic"))?;
    let space = run(&rest[end..], char::is_whitespace);
    let value = run(&rest[end + space..], |c| {
        !(c.is_whitespace() || c == ',' || c == ';')
    });
    if space == 0 || value == 0 {
        return None;
    }
    Some((
        start + end + space + value,
        format!("Authorization={REDACTED}"),
    ))
}

/// The credential keywords' candidate lengths at `text`, in the pattern's
/// alternation and greedy-quantifier order.
fn keywords(text: &str) -> Vec<usize> {
    let separated = |stem: &str, then: &str| -> Vec<usize> {
        let Some(stem) = ascii_prefix(text, stem) else {
            return Vec::new();
        };
        let mut candidates = Vec::new();
        if text[stem..].starts_with(['_', '-'])
            && let Some(tail) = ascii_prefix(&text[stem + 1..], then)
        {
            candidates.push(stem + 1 + tail);
        }
        if let Some(tail) = ascii_prefix(&text[stem..], then) {
            candidates.push(stem + tail);
        }
        candidates
    };
    let mut candidates = Vec::new();
    for word in ["password", "passwd", "pwd"] {
        candidates.extend(ascii_prefix(text, word));
    }
    for stem in ["access", "refresh", "id"] {
        candidates.extend(separated(stem, "token"));
    }
    candidates.extend(ascii_prefix(text, "token"));
    candidates.extend(separated("client", "secret"));
    candidates.extend(ascii_prefix(text, "secret"));
    candidates.extend(ascii_prefix(text, "authorization"));
    candidates.extend(separated("api", "key"));
    candidates
}

/// `(?i)\b(password|passwd|pwd|(?:access[_-]?|refresh[_-]?|id[_-]?)?token|
/// (?:client[_-]?)?secret|authorization|api[_-]?key)["']?\s*[:=]\s*
/// ("[^"]*"|'[^']*'|[^\s,;]+)` -> `$1=[REDACTED]`.
fn credential(text: &str, start: usize) -> Option<(usize, String)> {
    if !word_start(text, start) {
        return None;
    }
    let rest = &text[start..];
    keywords(rest).into_iter().find_map(|keyword| {
        let value_start = keyword + assignment(&rest[keyword..])?;
        let value = &rest[value_start..];
        let quoted = |quote: char| {
            let inner = value.strip_prefix(quote)?;
            inner.find(quote).map(|close| close + 2)
        };
        let length = quoted('"').or_else(|| quoted('\'')).or_else(|| {
            Some(run(value, |c| !(c.is_whitespace() || c == ',' || c == ';'))).filter(|n| *n > 0)
        })?;
        Some((
            start + value_start + length,
            format!("{}={REDACTED}", &rest[..keyword]),
        ))
    })
}

/// `(?i)((?:[A-Z]:\\Users\\|/Users/|/home/))[^\\/\s"']+` -> `$1[USER]`.
fn user_path(text: &str, start: usize) -> Option<(usize, String)> {
    let rest = &text[start..];
    let drive = || {
        let mut characters = rest.chars();
        characters.next().filter(char::is_ascii_alphabetic)?;
        let tail = &rest[1..];
        let tail = tail.strip_prefix(":\\")?;
        Some(3 + ascii_prefix(tail, "users\\")?)
    };
    let prefix = drive()
        .or_else(|| ascii_prefix(rest, "/users/"))
        .or_else(|| ascii_prefix(rest, "/home/"))?;
    let name = run(&rest[prefix..], |c| {
        !(c == '\\' || c == '/' || c.is_whitespace() || c == '"' || c == '\'')
    });
    (name > 0).then(|| (start + prefix + name, format!("{}[USER]", &rest[..prefix])))
}

/// `(?<![\p{L}\p{N}_])token(?![\p{L}\p{N}_])`, case-insensitive.
fn bounded_token(text: &str, start: usize, token: &str) -> Option<(usize, String)> {
    if previous(text, start).is_some_and(is_word) {
        return None;
    }
    let lower = |character: char| {
        let mut lower = character.to_lowercase();
        match (lower.next(), lower.next()) {
            (Some(single), None) => single,
            _ => character,
        }
    };
    let length = prefix_len(&text[start..], token, lower)?;
    let end = start + length;
    if text[end..].chars().next().is_some_and(is_word) {
        return None;
    }
    Some((end, REDACTED.to_owned()))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_plan_with_a_token_inside_the_replacement_removes_matches() {
        let plan = Plan::new(&["DACT".into(), " ".into()]);
        assert_eq!(plan.apply("REDACTED dact"), "REED "); // "[REDACTED]" holds "DACT"
    }

    #[test]
    fn plans_replace_longest_tokens_first() {
        let plan = Plan::new(&["ab".into(), "abc".into()]);
        assert_eq!(plan.apply("xABCx ab"), "x[REDACTED]x [REDACTED]");
    }
}

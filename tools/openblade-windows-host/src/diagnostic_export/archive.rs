//! Managed `DiagnosticArchiveProvider.CreateAsync` and
//! `DiagnosticBundleExporter.ExportAsync`: the entries, their order, text and
//! redaction, the 16 MiB bound and the suggested file name.
//!
//! Gaps, each written as managed writes the section when its source is empty:
//! - `device-verification.json` is `NotObserved`: native keeps no cached
//!   advisory probe (`DiagnosticDeviceVerification`).
//! - `firmware-queries.json` is `[]`: native records no firmware query log
//!   (`FirmwareQueryDiagnostics`).
//! - `hyperboost-ownership.json` is `Missing`: native keeps no HyperBoost
//!   ownership journal.
//! - `experimental-support.json` is absent: native detects no experimental
//!   support evidence, and managed writes the entry only when it does.
//! - the three `capability.rz09-0581.*` configuration keys are
//!   `Unavailable`: native has no `Rz090581CapabilityDiagnostics` source.
//! - `logs.jsonl` is empty: native has no `ILogger` or trace producer.

use serde_json::Value;

use super::{
    Cancel, Failure, HostMetadata, Timestamp,
    history::{CommandResult, Snapshot},
    json::{Json, timestamp},
    redact::Plan,
    sections,
    zip::Writer,
};

/// What one export reads: the caller-projected status, the history and the
/// host metadata, all captured before the build starts.
pub(crate) struct Request {
    pub(crate) status: Value,
    pub(crate) history: Snapshot,
    pub(crate) metadata: HostMetadata,
    pub(crate) live_readings_enabled: bool,
}

/// Managed `DiagnosticArchive`.
#[derive(Debug)]
pub(crate) struct Archive {
    pub(crate) file_name: String,
    pub(crate) bytes: Vec<u8>,
}

/// Managed `DiagnosticBundle` with the sections native has.
pub(crate) struct Bundle {
    pub(crate) versions: Json,
    pub(crate) device: Json,
    pub(crate) capabilities: Vec<(String, bool)>,
    pub(crate) configuration: Vec<(String, String)>,
    pub(crate) history: Snapshot,
    pub(crate) tokens: Vec<String>,
}

pub(crate) fn build(request: Request, cancel: &Cancel) -> Result<Archive, Failure> {
    build_bounded(request, cancel, super::MAXIMUM_ARCHIVE_BYTES)
}

pub(crate) fn build_bounded(
    request: Request,
    cancel: &Cancel,
    limit: usize,
) -> Result<Archive, Failure> {
    cancel.check()?;
    let status = &request.status;
    let bundle = Bundle {
        versions: sections::versions(status, &request.metadata.windows_version),
        device: sections::device(status),
        capabilities: sections::capabilities(status)?.into_iter().collect(),
        configuration: sections::configuration(status, request.live_readings_enabled)?
            .into_iter()
            .collect(),
        history: request.history,
        tokens: request.metadata.tokens,
    };
    let mut writer = Writer::bounded(request.metadata.local_time.dos(), limit);
    for (name, text) in entries(&bundle, cancel)? {
        writer.add(name, text.as_bytes(), cancel)?;
    }
    let bytes = writer.finish()?;
    Ok(Archive {
        file_name: file_name(request.metadata.captured_at),
        bytes,
    })
}

/// Managed `OpenBlade-diagnostics-{yyyyMMdd-HHmmss}Z.zip`.
pub(crate) fn file_name(captured_at: Timestamp) -> String {
    format!("OpenBlade-diagnostics-{}Z.zip", captured_at.compact())
}

/// The exporter's entries, in its order, as UTF-8 text without a BOM.
pub(crate) fn entries(
    bundle: &Bundle,
    cancel: &Cancel,
) -> Result<Vec<(&'static str, String)>, Failure> {
    let plan = Plan::new(&bundle.tokens);
    let redact = |text: &str| plan.apply(text);
    // `WriteJsonEntryAsync` with the bundle tokens: names and values.
    let redacted = |value: &Json| value.redacted(&redact, true).write(true);
    let mut entries = Vec::with_capacity(10);
    entries.push(("versions.json", redacted(&bundle.versions)));
    entries.push(("device.json", redacted(&bundle.device)));
    entries.push((
        "device-verification.json",
        Json::object([
            ("latestAttempt", Json::Null),
            ("lastFailure", Json::Null),
            ("evidenceSource", Json::str("CachedServiceAdvisoryProbe")),
        ])
        .write(true),
    ));
    entries.push((
        "capabilities.json",
        redacted(&Json::Object(
            bundle
                .capabilities
                .iter()
                .map(|(key, value)| (key.clone(), Json::Bool(*value)))
                .collect(),
        )),
    ));
    entries.push((
        "configuration.json",
        redacted(&Json::Object(
            bundle
                .configuration
                .iter()
                .map(|(key, value)| (key.clone(), Json::str(value.as_str())))
                .collect(),
        )),
    ));
    cancel.check()?;
    // `WriteCommandResultsEntryAsync`: the four text members redacted, the
    // timestamp, transition ID and member names kept.
    entries.push((
        "command-results.json",
        Json::Array(
            bundle
                .history
                .results
                .iter()
                .map(|result| command_result(result, &redact))
                .collect(),
        )
        .write(true),
    ));
    let history = &bundle.history;
    let count = |value: u64| Json::Int(i64::try_from(value).unwrap_or(i64::MAX));
    entries.push((
        "service-history.json",
        Json::object([
            ("evictedEntries", count(history.evicted)),
            ("droppedEntries", count(history.dropped)),
            ("truncatedValues", count(history.truncated)),
            ("retainedEntries", count(history.retained_entries as u64)),
            (
                "retainedCharacters",
                count(history.retained_characters as u64),
            ),
        ])
        .write(true),
    ));
    entries.push(("firmware-queries.json", Json::Array(Vec::new()).write(true)));
    entries.push((
        "hyperboost-ownership.json",
        Json::object([
            ("exists", Json::Bool(false)),
            ("valid", Json::Bool(true)),
            ("state", Json::Null),
            ("baseMode", Json::Null),
            ("requiresCoolingPad", Json::Null),
            ("restoreIntent", Json::Null),
            ("sha256", Json::Null),
            ("restoreRetryDisposition", Json::Null),
            ("restoreRetryTarget", Json::Null),
            ("transitionId", Json::Null),
        ])
        .write(true),
    ));
    cancel.check()?;
    // `WriteLogEntryAsync`: one compact `{ line }` per line, its member name
    // redacted too, each followed by `Environment.NewLine`.
    let mut logs = String::new();
    for line in &history.log_lines {
        logs.push_str(
            &Json::object([("line", Json::str(line.as_str()))])
                .redacted(&redact, true)
                .write(false),
        );
        logs.push_str("\r\n");
    }
    entries.push(("logs.jsonl", logs));
    Ok(entries)
}

fn command_result(result: &CommandResult, redact: &impl Fn(&str) -> String) -> Json {
    Json::object([
        ("timestamp", Json::Str(timestamp(result.timestamp))),
        ("subsystem", Json::Str(redact(&result.subsystem))),
        ("action", Json::Str(redact(&result.action))),
        ("status", Json::Str(redact(&result.status))),
        (
            "detail",
            Json::nullable(result.detail.as_deref().map(redact)),
        ),
        ("transitionId", Json::Null),
    ])
}

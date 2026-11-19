//! The profile composition over a scripted host: the managed step order and
//! skip conditions, power admission, the pause gate and checkpoint before any
//! attempt, stop-on-first-non-success, staleness, and per-field certainty in
//! the native write gate beside the managed durable record. Each step's own
//! attempt is the reviewed single-field writer, covered by its own tests; here
//! a scripted outcome stands in for it. No Windows call is used.
use super::*;
use crate::session_native::display_query::{
    test_support::{Memory, Rig, flag, result},
    write::{CHANGED_POWER, CHECKPOINT, UNCONFIRMED_POWER},
};
use std::{
    collections::VecDeque,
    sync::{Arc, atomic::Ordering},
};

const REFUSED: &str = "The scripted write was refused.";
const FAILED: &str = "The scripted write failed.";
const UNCERTAIN: &str = "The scripted write may have been submitted.";
const CANCELED: &str = "The scripted write was canceled.";

/// The scripted panel, as the reply query reports it.
#[derive(Clone)]
struct Panel {
    available: bool,
    message: Option<&'static str>,
    refresh: Option<String>,
    hdr_supported: bool,
    hdr: bool,
    color: Option<String>,
    brightness: Option<u8>,
    levels: Vec<u8>,
}

impl Default for Panel {
    /// Every field differs from `full()`.
    fn default() -> Self {
        Self {
            available: true,
            message: None,
            refresh: Some("fixed-240".into()),
            hdr_supported: true,
            hdr: true,
            color: Some("srgb".into()),
            brightness: Some(100),
            levels: vec![0, 25, 50, 75, 100],
        }
    }
}

impl Panel {
    fn snapshot(&self) -> Snapshot {
        if !self.available {
            let mut state = serde_json::to_value(Snapshot::unavailable("unused")).unwrap();
            state["message"] = self.message.into();
            return serde_json::from_value(state).unwrap();
        }
        serde_json::from_value(serde_json::json!({
            "isAvailable": true,
            "displayName": "Synthetic panel",
            "width": 2560,
            "height": 1600,
            "currentRefreshKey": self.refresh,
            "refreshOptions": [],
            "hdrSupported": self.hdr_supported,
            "hdrEnabled": self.hdr,
            "colorProfilesEnabled": true,
            "activeColorProfileId": self.color,
            "colorProfiles": [],
            "message": self.message,
            "bitsPerColorChannel": 8,
            "brightnessPercent": self.brightness,
            "supportedBrightnessLevels": self.levels,
        }))
        .unwrap()
    }
}

/// One scripted writer result.
#[derive(Clone, Copy, Debug)]
enum Outcome {
    /// Submitted and confirmed; the panel shows it.
    Applies,
    /// Submitted and confirmed, then the trusted power context changed.
    Drifts,
    /// Nothing submitted (the writer's own read matched), then power changed.
    UnchangedDrifts,
    /// Submitted and confirmed, but the refresh rate fell back.
    Disturbs,
    /// The brightness worker confirmed a different supported level.
    AppliesLevel(u8),
    /// Confirmed by the writer's readback, but the reply does not show it.
    Unshown,
    /// Nothing submitted: the writer's own read already showed the request.
    Unchanged,
    /// Blocked before any possible submission.
    Refused,
    /// Failed before any possible submission; the panel changes only when the
    /// reply resolves the failure (managed `ResolveHdrMutationResult`).
    Failed { reply_resolves: bool, applied: bool },
    /// Possibly submitted without a confirmed result.
    Uncertain { applied: bool },
    /// The brightness worker failed fatally.
    Fatal { possible: bool },
}

/// The step a writer was called for, as the panel applies it.
enum Write {
    Refresh(String),
    Hdr(bool),
    Color(String),
    Brightness(u8),
}

struct Scripted {
    panel: Panel,
    power: Option<PowerContext>,
    script: VecDeque<Outcome>,
    log: Vec<String>,
    queries: usize,
    /// The query with this 1-based number fails fatally.
    fail_query: Option<usize>,
    /// Cancellation arrives while the query with this number runs.
    cancel_in_query: Option<usize>,
    cancel: Arc<AtomicBool>,
    /// The durable record seen by each attempt.
    checkpoint: Option<Memory>,
    at_attempt: Vec<serde_json::Value>,
}

const ADMITTED: PowerContext = PowerContext {
    source: 1,
    generation: 7,
};

impl Scripted {
    fn new(panel: Panel, script: &[Outcome]) -> Self {
        Self {
            panel,
            power: Some(ADMITTED),
            script: script.iter().copied().collect(),
            log: Vec::new(),
            queries: 0,
            fail_query: None,
            cancel_in_query: None,
            cancel: Arc::default(),
            checkpoint: None,
            at_attempt: Vec::new(),
        }
    }

    /// The scripted writer: the fence's cancellation check, then the outcome.
    fn write(&mut self, write: Write, admitted: PowerContext, canceled: &AtomicBool) -> Outcome {
        assert_eq!(
            Some(admitted),
            self.power,
            "each step binds the admitted context"
        );
        assert!(
            std::ptr::eq(canceled, &*self.cancel),
            "each step observes the operation's cancellation"
        );
        self.log.push(match &write {
            Write::Refresh(key) => format!("refresh {key}"),
            Write::Hdr(enabled) => format!("hdr {enabled}"),
            Write::Color(key) => format!("color {key}"),
            Write::Brightness(level) => format!("brightness {level}"),
        });
        if let Some(file) = &self.checkpoint {
            self.at_attempt.push(file.record());
        }
        if canceled.load(Ordering::SeqCst) {
            return Outcome::Refused;
        }
        let outcome = self.script.pop_front().expect("a scripted outcome");
        let applied = match outcome {
            Outcome::Applies | Outcome::Drifts | Outcome::Disturbs | Outcome::AppliesLevel(_) => {
                true
            }
            Outcome::Failed { applied, .. } | Outcome::Uncertain { applied } => applied,
            _ => false,
        };
        if applied {
            match write {
                Write::Refresh(key) => self.panel.refresh = Some(key),
                Write::Hdr(enabled) => self.panel.hdr = enabled,
                Write::Color(key) => self.panel.color = Some(key),
                Write::Brightness(level) => {
                    self.panel.brightness = Some(match outcome {
                        Outcome::AppliesLevel(other) => other,
                        _ => level,
                    })
                }
            }
        }
        if matches!(outcome, Outcome::Disturbs) {
            self.panel.refresh = Some("fixed-240".into());
        }
        if matches!(outcome, Outcome::Drifts | Outcome::UnchangedDrifts) {
            self.power = Some(PowerContext {
                source: 1,
                generation: 8,
            });
        }
        outcome
    }

    fn ccd(&mut self, write: Write, admitted: PowerContext, canceled: &AtomicBool) -> Attempt {
        match self.write(write, admitted, canceled) {
            Outcome::Applies
            | Outcome::Drifts
            | Outcome::Disturbs
            | Outcome::AppliesLevel(_)
            | Outcome::Unshown => Attempt::Confirmed,
            Outcome::Unchanged | Outcome::UnchangedDrifts => Attempt::Unchanged,
            Outcome::Refused if canceled.load(Ordering::SeqCst) => {
                Attempt::Done(Status::Blocked, Cow::Borrowed(CANCELED))
            }
            Outcome::Refused => Attempt::Done(Status::Blocked, Cow::Borrowed(REFUSED)),
            Outcome::Failed { reply_resolves, .. } => Attempt::Failed {
                error: Cow::Borrowed(FAILED),
                reply_resolves,
            },
            Outcome::Uncertain { .. } => {
                Attempt::Done(Status::Indeterminate, Cow::Borrowed(UNCERTAIN))
            }
            Outcome::Fatal { .. } => unreachable!("only the brightness worker fails fatally"),
        }
    }
}

impl ProfileHost for Scripted {
    fn power(&self) -> Option<PowerContext> {
        self.power
    }

    fn query(&mut self, _: &SharedBrightness, canceled: &AtomicBool) -> io::Result<Snapshot> {
        self.queries += 1;
        self.log.push("query".into());
        if self.fail_query == Some(self.queries) {
            return Err(io::Error::other("injected query failure"));
        }
        if canceled.load(Ordering::SeqCst) {
            return Err(io::ErrorKind::Interrupted.into());
        }
        if self.cancel_in_query == Some(self.queries) {
            self.cancel.store(true, Ordering::SeqCst);
        }
        Ok(self.panel.snapshot())
    }

    fn refresh(
        &mut self,
        request: &SetRefreshRate,
        admitted: PowerContext,
        canceled: &AtomicBool,
    ) -> Attempt {
        assert_eq!(request.expected_power_source, admitted.source);
        self.ccd(Write::Refresh(request.key.clone()), admitted, canceled)
    }

    fn hdr(&mut self, request: &SetHdr, admitted: PowerContext, canceled: &AtomicBool) -> Attempt {
        assert_eq!(request.expected_power_source, admitted.source);
        self.ccd(Write::Hdr(request.enabled), admitted, canceled)
    }

    fn color(
        &mut self,
        request: &SetColorProfile,
        admitted: PowerContext,
        canceled: &AtomicBool,
    ) -> Attempt {
        assert_eq!(request.expected_power_source, admitted.source);
        self.ccd(Write::Color(request.key.clone()), admitted, canceled)
    }

    fn brightness(
        &mut self,
        _: &SharedBrightness,
        percent: u8,
        admitted: PowerContext,
        canceled: &AtomicBool,
    ) -> brightness_write::Attempt {
        Ok(
            match self.write(Write::Brightness(percent), admitted, canceled) {
                Outcome::Applies | Outcome::Drifts | Outcome::Disturbs | Outcome::Unshown => {
                    (Status::Succeeded, None, Some(percent))
                }
                Outcome::AppliesLevel(other) => (Status::Succeeded, None, Some(other)),
                Outcome::Unchanged | Outcome::UnchangedDrifts => (Status::Succeeded, None, None),
                Outcome::Refused if canceled.load(Ordering::SeqCst) => {
                    (Status::Blocked, Some(CANCELED), None)
                }
                Outcome::Refused => (Status::Blocked, Some(REFUSED), None),
                Outcome::Failed { .. } => (Status::Failed, Some(FAILED), None),
                Outcome::Uncertain { .. } => (Status::Indeterminate, Some(UNCERTAIN), None),
                Outcome::Fatal { possible } => {
                    return Err((possible, io::Error::other("injected worker failure")));
                }
            },
        )
    }
}

/// Every field configured, each differing from the default panel.
fn full() -> Settings {
    Settings {
        refresh_rate_key: Some("fixed-120".into()),
        hdr_enabled: Some(false),
        color_profile_key: Some("dci-p3".into()),
        brightness_percent: Some(60),
    }
}

fn only(fields: Fields) -> Settings {
    let all = full();
    Settings {
        refresh_rate_key: all.refresh_rate_key.filter(|_| fields.refresh),
        hdr_enabled: all.hdr_enabled.filter(|_| fields.hdr),
        color_profile_key: all.color_profile_key.filter(|_| fields.color),
        brightness_percent: all.brightness_percent.filter(|_| fields.brightness),
    }
}

/// The full profile's writes in the managed order (60 % selects level 50).
const FULL_WRITES: [&str; 4] = [
    "refresh fixed-120",
    "hdr false",
    "color dci-p3",
    "brightness 50",
];

fn fresh() -> Rig {
    Rig::new(Memory::legacy(1))
}

fn apply(rig: &Rig, host: &mut Scripted, settings: Settings) -> io::Result<ManualWriteOutcome> {
    apply_expecting(rig, host, 1, settings)
}

fn apply_expecting(
    rig: &Rig,
    host: &mut Scripted,
    expected_power_source: u8,
    settings: Settings,
) -> io::Result<ManualWriteOutcome> {
    let cancel = host.cancel.clone();
    let request = ApplyProfile {
        expected_power_source,
        settings,
    };
    run(host, &rig.owner, &rig.recovery, &request, &cancel)
}

/// The scripted writes, without the queries between them.
fn writes(host: &Scripted) -> Vec<&str> {
    host.log
        .iter()
        .map(String::as_str)
        .filter(|entry| *entry != "query")
        .collect()
}

fn uncertain(record: &serde_json::Value) -> Fields {
    Fields {
        refresh: flag(record, "UncertainRefreshRate"),
        hdr: flag(record, "UncertainHdr"),
        color: flag(record, "UncertainColorProfile"),
        brightness: flag(record, "UncertainBrightness"),
    }
}

fn all_fields() -> Fields {
    full().fields()
}

#[test]
fn a_full_profile_writes_each_field_once_in_the_managed_order() {
    let rig = fresh();
    let mut host = Scripted::new(Panel::default(), &[Outcome::Applies; 4]);
    host.checkpoint = Some(rig.file.clone());
    let outcome = apply(&rig, &mut host, full()).unwrap();
    assert_eq!(result(&outcome), (Status::Succeeded, None));
    // One query selects the steps, then one reply query follows each write.
    assert_eq!(
        host.log,
        [
            "query",
            FULL_WRITES[0],
            "query",
            FULL_WRITES[1],
            "query",
            FULL_WRITES[2],
            "query",
            FULL_WRITES[3],
            "query"
        ]
    );
    // The checkpoint covered every configured field before the first write.
    for record in &host.at_attempt {
        assert!(flag(record, "ManualHold") && flag(record, "AutomaticWritesPaused"));
        assert_eq!(uncertain(record), all_fields());
    }
    let record = rig.file.record();
    assert!(flag(&record, "ManualHold") && !flag(&record, "AutomaticWritesPaused"));
    assert_eq!(uncertain(&record), Fields::default());
    assert_eq!(rig.unresolved(), Fields::default());
    let state = outcome.state.observed();
    assert_eq!(state.refresh_key, Some("fixed-120"));
    assert_eq!(
        (state.hdr_enabled, state.color_profile),
        (false, Some("dci-p3"))
    );
    assert_eq!(state.brightness, Some(50));
}

#[test]
fn fields_the_snapshot_already_shows_are_not_written() {
    // The color ID compares ignoring case; the refresh key compares exactly.
    let panel = Panel {
        refresh: Some("FIXED-120".into()),
        hdr: false,
        color: Some("DCI-P3".into()),
        brightness: Some(50),
        ..Panel::default()
    };
    let rig = fresh();
    let mut host = Scripted::new(panel, &[Outcome::Applies]);
    let outcome = apply(&rig, &mut host, full()).unwrap();
    assert_eq!(result(&outcome), (Status::Succeeded, None));
    assert_eq!(writes(&host), ["refresh fixed-120"]);

    // Already matching: one selecting query and no write at all.
    let mut host = Scripted::new(host.panel.clone(), &[]);
    let outcome = apply(&rig, &mut host, full()).unwrap();
    assert_eq!(result(&outcome), (Status::Succeeded, None));
    assert_eq!(host.log, ["query"]);
}

#[test]
fn unsupported_hdr_is_not_written_and_fails_the_final_match() {
    let panel = Panel {
        hdr_supported: false,
        ..Panel::default()
    };
    let rig = fresh();
    let mut host = Scripted::new(panel, &[]);
    let outcome = apply(&rig, &mut host, only(Fields::HDR)).unwrap();
    // Nothing was submitted: managed Failure without submission.
    assert_eq!(result(&outcome), (Status::Failed, Some(UNCONFIRMED)));
    assert!(writes(&host).is_empty());
    let record = rig.file.record();
    assert!(!flag(&record, "AutomaticWritesPaused"));
    assert_eq!(rig.unresolved(), Fields::default());
}

#[test]
fn hdr_on_skips_the_color_write_and_satisfies_it() {
    let settings = Settings {
        hdr_enabled: Some(true),
        ..full()
    };
    let rig = fresh();
    let mut host = Scripted::new(Panel::default(), &[Outcome::Applies; 2]);
    let outcome = apply(&rig, &mut host, settings).unwrap();
    assert_eq!(result(&outcome), (Status::Succeeded, None));
    assert_eq!(writes(&host), ["refresh fixed-120", "brightness 50"]);

    // HDR turned on by the profile also skips color, using the step's reply.
    let settings = Settings {
        hdr_enabled: Some(true),
        ..only(Fields::HDR.include(Fields::COLOR))
    };
    let panel = Panel {
        hdr: false,
        ..Panel::default()
    };
    let mut host = Scripted::new(panel, &[Outcome::Applies]);
    let outcome = apply(&rig, &mut host, settings).unwrap();
    assert_eq!(result(&outcome), (Status::Succeeded, None));
    assert_eq!(writes(&host), ["hdr true"]);
}

#[test]
fn brightness_selects_the_nearest_supported_level_ties_lower() {
    assert_eq!(nearest(60, &[0, 25, 50, 75, 100]), Some(50));
    assert_eq!(nearest(50, &[40, 60]), Some(40));
    assert_eq!(nearest(51, &[40, 60]), Some(60));
    assert_eq!(nearest(0, &[30]), Some(30));
    assert_eq!(nearest(100, &[0, 100]), Some(100));
    assert_eq!(nearest(50, &[]), None);
    assert_eq!(nearest(50, &[101, 60]), Some(60));

    let panel = Panel {
        levels: vec![40, 60],
        brightness: Some(60),
        ..Panel::default()
    };
    let rig = fresh();
    let settings = Settings {
        brightness_percent: Some(50),
        ..Settings::default()
    };
    let mut host = Scripted::new(panel, &[Outcome::Applies]);
    let outcome = apply(&rig, &mut host, settings).unwrap();
    assert_eq!(result(&outcome), (Status::Succeeded, None));
    assert_eq!(writes(&host), ["brightness 40"]);
}

#[test]
fn an_unconfigured_profile_is_one_query_without_power_admission() {
    let rig = fresh();
    let mut host = Scripted::new(Panel::default(), &[]);
    host.power = None;
    let outcome = apply_expecting(&rig, &mut host, 2, Settings::default()).unwrap();
    assert_eq!(result(&outcome), (Status::Succeeded, None));
    assert_eq!(host.log, ["query"]);
    // Managed still begins (and completes) the manual operation.
    let record = rig.file.record();
    assert!(flag(&record, "ManualHold") && !flag(&record, "AutomaticWritesPaused"));
}

#[test]
fn an_unconfirmed_or_changed_power_source_blocks_before_any_write() {
    for (power, expected, error) in [
        (None, 1, UNCONFIRMED_POWER),
        (Some(ADMITTED), 2, CHANGED_POWER),
        (
            Some(PowerContext {
                source: 0,
                generation: 7,
            }),
            1,
            CHANGED_POWER,
        ),
    ] {
        let rig = fresh();
        let mut host = Scripted::new(Panel::default(), &[]);
        host.power = power;
        let outcome = apply_expecting(&rig, &mut host, expected, full()).unwrap();
        assert_eq!(result(&outcome), (Status::Blocked, Some(error)));
        assert_eq!(host.log, ["query"], "the reply is queried, nothing written");
        assert!(outcome.state.observed().available);
        let record = rig.file.record();
        assert!(flag(&record, "ManualHold") && !flag(&record, "AutomaticWritesPaused"));
        assert_eq!(rig.unresolved(), Fields::default());
    }
}

#[test]
fn a_checkpoint_failure_blocks_before_any_write() {
    let rig = fresh();
    rig.file.allow.store(0, Ordering::SeqCst);
    let mut host = Scripted::new(Panel::default(), &[]);
    let outcome = apply(&rig, &mut host, full()).unwrap();
    assert_eq!(result(&outcome), (Status::Blocked, Some(CHECKPOINT)));
    assert_eq!(host.log, ["query"]);
}

#[test]
fn the_pause_gate_refuses_a_profile_touching_an_unresolved_field() {
    let rig = fresh();
    let mut host = Scripted::new(Panel::default(), &[Outcome::Uncertain { applied: false }]);
    let outcome = apply(&rig, &mut host, only(Fields::HDR)).unwrap();
    assert_eq!(result(&outcome), (Status::Indeterminate, Some(UNCERTAIN)));
    assert_eq!(rig.unresolved(), Fields::HDR);
    let writes_before = rig.file.writes.load(Ordering::SeqCst);

    // Any configured unresolved field refuses the whole profile, before the
    // checkpoint and without a write; the reply is still queried.
    let mut host = Scripted::new(Panel::default(), &[]);
    let outcome = apply(&rig, &mut host, full()).unwrap();
    assert_eq!(result(&outcome), (Status::Blocked, Some(PAUSED)));
    assert_eq!(host.log, ["query"]);
    assert!(outcome.state.observed().available);
    assert_eq!(rig.file.writes.load(Ordering::SeqCst), writes_before);

    // A profile without that field proceeds.
    let mut host = Scripted::new(Panel::default(), &[Outcome::Applies]);
    let outcome = apply(&rig, &mut host, only(Fields::REFRESH)).unwrap();
    assert_eq!(result(&outcome), (Status::Succeeded, None));
    assert_eq!(rig.unresolved(), Fields::HDR);

    // An explicit read that observed HDR reopens it.
    rig.explicit_read(&Panel::default().snapshot());
    assert_eq!(rig.unresolved(), Fields::default());
    let mut host = Scripted::new(Panel::default(), &[Outcome::Applies; 4]);
    let outcome = apply(&rig, &mut host, full()).unwrap();
    assert_eq!(result(&outcome), (Status::Succeeded, None));
}

#[test]
fn a_first_step_refused_or_failed_before_submission_writes_nothing_else() {
    for (outcome, expected) in [
        (Outcome::Refused, (Status::Blocked, Some(REFUSED))),
        (
            Outcome::Failed {
                reply_resolves: false,
                applied: false,
            },
            (Status::Failed, Some(FAILED)),
        ),
        // A reply-resolved failure whose reply does not show the request.
        (
            Outcome::Failed {
                reply_resolves: true,
                applied: false,
            },
            (Status::Failed, Some(FAILED)),
        ),
    ] {
        let rig = fresh();
        let mut host = Scripted::new(Panel::default(), &[outcome]);
        let reply = apply(&rig, &mut host, full()).unwrap();
        assert_eq!(result(&reply), expected, "{outcome:?}");
        assert_eq!(writes(&host), FULL_WRITES[..1]);
        assert_eq!(host.queries, 2, "the selecting query and the reply");
        // Managed Failure/Blocked without submission restores the prior pause.
        let record = rig.file.record();
        assert!(flag(&record, "ManualHold") && !flag(&record, "AutomaticWritesPaused"));
        assert_eq!(uncertain(&record), Fields::default());
        assert_eq!(rig.unresolved(), Fields::default());
    }
}

#[test]
fn a_reply_resolved_failure_that_the_reply_shows_continues() {
    let rig = fresh();
    let failed = Outcome::Failed {
        reply_resolves: true,
        applied: true,
    };
    let mut host = Scripted::new(
        Panel::default(),
        &[Outcome::Applies, failed, Outcome::Applies, Outcome::Applies],
    );
    let outcome = apply(&rig, &mut host, full()).unwrap();
    assert_eq!(result(&outcome), (Status::Succeeded, None));
    assert_eq!(writes(&host), FULL_WRITES);
}

#[test]
fn a_no_op_step_is_not_a_submission() {
    // The writer's own read already showed the refresh rate (the selecting
    // snapshot was older), so the later refusal is not after a submission.
    // Managed counts the no-op setter as submitted (Indeterminate).
    let fresh_rig = fresh();
    let mut host = Scripted::new(Panel::default(), &[Outcome::Unchanged, Outcome::Refused]);
    let outcome = apply(&fresh_rig, &mut host, full()).unwrap();
    assert_eq!(result(&outcome), (Status::Blocked, Some(REFUSED)));
    assert_eq!(writes(&host), FULL_WRITES[..2]);
    assert!(!flag(&fresh_rig.file.record(), "AutomaticWritesPaused"));
    assert_eq!(fresh_rig.unresolved(), Fields::default());
}

#[test]
fn a_refusal_after_a_confirmed_step_is_indeterminate_without_gating_it() {
    let rig = fresh();
    let mut host = Scripted::new(Panel::default(), &[Outcome::Applies, Outcome::Refused]);
    let outcome = apply(&rig, &mut host, full()).unwrap();
    // Managed Failure(error, state, submitted).
    assert_eq!(result(&outcome), (Status::Indeterminate, Some(REFUSED)));
    assert_eq!(writes(&host), FULL_WRITES[..2]);
    // The durable managed record keeps every configured field uncertain ...
    let record = rig.file.record();
    assert!(flag(&record, "AutomaticWritesPaused"));
    assert_eq!(uncertain(&record), all_fields());
    // ... while the native gate holds no field: the refresh write is shown and
    // the HDR refusal submitted nothing.
    assert_eq!(rig.unresolved(), Fields::default());
    let mut host = Scripted::new(host.panel.clone(), &[Outcome::Applies; 3]);
    let outcome = apply(&rig, &mut host, full()).unwrap();
    assert_eq!(result(&outcome), (Status::Succeeded, None));
    assert_eq!(writes(&host), FULL_WRITES[1..]);
    assert!(!flag(&rig.file.record(), "AutomaticWritesPaused"));
}

#[test]
fn a_failure_after_a_submission_is_indeterminate() {
    for second in [
        Outcome::Failed {
            reply_resolves: false,
            applied: false,
        },
        Outcome::Refused,
    ] {
        let rig = fresh();
        let mut host = Scripted::new(
            Panel::default(),
            &[Outcome::Applies, Outcome::Applies, second],
        );
        let outcome = apply(&rig, &mut host, full()).unwrap();
        assert_eq!(outcome.status, Status::Indeterminate, "{second:?}");
        assert_eq!(writes(&host), FULL_WRITES[..3]);
        assert_eq!(rig.unresolved(), Fields::default());
    }
}

#[test]
fn an_uncertain_step_gates_exactly_its_field_and_stops() {
    for (index, field) in [
        (0, Fields::REFRESH),
        (1, Fields::HDR),
        (2, Fields::COLOR),
        (3, Fields::BRIGHTNESS),
    ] {
        for applied in [false, true] {
            let mut script = vec![Outcome::Applies; index];
            script.push(Outcome::Uncertain { applied });
            let rig = fresh();
            let mut host = Scripted::new(Panel::default(), &script);
            let outcome = apply(&rig, &mut host, full()).unwrap();
            assert_eq!(result(&outcome), (Status::Indeterminate, Some(UNCERTAIN)));
            assert_eq!(writes(&host), FULL_WRITES[..=index], "no later field");
            assert_eq!(rig.unresolved(), field);
            let record = rig.file.record();
            assert!(flag(&record, "AutomaticWritesPaused"));
            assert_eq!(uncertain(&record), all_fields());
        }
    }
}

#[test]
fn a_confirmed_step_its_reply_does_not_show_stops_and_is_gated() {
    let rig = fresh();
    let mut host = Scripted::new(Panel::default(), &[Outcome::Applies, Outcome::Unshown]);
    let outcome = apply(&rig, &mut host, full()).unwrap();
    assert_eq!(
        result(&outcome),
        (Status::Indeterminate, Some(hdr_write::UNCONFIRMED))
    );
    assert_eq!(writes(&host), FULL_WRITES[..2]);
    assert_eq!(rig.unresolved(), Fields::HDR);
}

#[test]
fn a_confirmed_step_the_final_reply_no_longer_shows_is_gated() {
    let rig = fresh();
    let mut host = Scripted::new(
        Panel::default(),
        &[
            Outcome::Applies,
            Outcome::Applies,
            Outcome::Applies,
            Outcome::Disturbs,
        ],
    );
    let outcome = apply(&rig, &mut host, full()).unwrap();
    assert_eq!(result(&outcome), (Status::Indeterminate, Some(UNCONFIRMED)));
    assert_eq!(writes(&host), FULL_WRITES);
    // Only the refresh rate, which the final reply contradicts.
    assert_eq!(rig.unresolved(), Fields::REFRESH);
}

#[test]
fn a_worker_confirmed_level_is_the_one_checked() {
    let rig = fresh();
    let mut host = Scripted::new(Panel::default(), &[Outcome::AppliesLevel(75)]);
    let outcome = apply(&rig, &mut host, only(Fields::BRIGHTNESS)).unwrap();
    // The worker's readback (75) shows; the profile asked for 50.
    assert_eq!(result(&outcome), (Status::Indeterminate, Some(UNCONFIRMED)));
    assert_eq!(rig.unresolved(), Fields::default());

    // A worker level that the reply contradicts is gated.
    let rig = fresh();
    let mut host = Scripted::new(Panel::default(), &[Outcome::Unshown]);
    let outcome = apply(&rig, &mut host, only(Fields::BRIGHTNESS)).unwrap();
    assert_eq!(
        result(&outcome),
        (Status::Indeterminate, Some(brightness_write::UNCONFIRMED))
    );
    assert_eq!(rig.unresolved(), Fields::BRIGHTNESS);
}

#[test]
fn a_power_change_after_a_submitted_step_is_stale_and_gated() {
    let rig = fresh();
    let mut host = Scripted::new(Panel::default(), &[Outcome::Applies, Outcome::Drifts]);
    let outcome = apply(&rig, &mut host, full()).unwrap();
    assert_eq!(result(&outcome), (Status::Indeterminate, Some(STALE_AFTER)));
    assert_eq!(writes(&host), FULL_WRITES[..2]);
    assert_eq!(rig.unresolved(), Fields::HDR);
}

#[test]
fn a_power_change_after_a_no_op_step_is_stale_without_gating() {
    let rig = fresh();
    let mut host = Scripted::new(Panel::default(), &[Outcome::UnchangedDrifts]);
    let outcome = apply(&rig, &mut host, full()).unwrap();
    // Managed EnsureCurrent after any successful setter.
    assert_eq!(result(&outcome), (Status::Indeterminate, Some(STALE_AFTER)));
    assert_eq!(writes(&host), FULL_WRITES[..1]);
    assert_eq!(rig.unresolved(), Fields::default());
    assert!(flag(&rig.file.record(), "AutomaticWritesPaused"));
}

#[test]
fn configured_brightness_without_levels_fails_before_writing_it() {
    let panel = Panel {
        brightness: None,
        levels: Vec::new(),
        ..Panel::default()
    };
    let rig = fresh();
    let mut host = Scripted::new(panel.clone(), &[]);
    let outcome = apply(&rig, &mut host, only(Fields::BRIGHTNESS)).unwrap();
    assert_eq!(
        result(&outcome),
        (Status::Failed, Some(brightness_write::UNAVAILABLE))
    );
    assert_eq!(host.log, ["query"]);

    // After an earlier submission the same failure is Indeterminate.
    let mut host = Scripted::new(panel, &[Outcome::Applies; 3]);
    let outcome = apply(&rig, &mut host, full()).unwrap();
    assert_eq!(
        result(&outcome),
        (Status::Indeterminate, Some(brightness_write::UNAVAILABLE))
    );
    assert_eq!(writes(&host), FULL_WRITES[..3]);
    assert_eq!(rig.unresolved(), Fields::default());
}

#[test]
fn an_unavailable_snapshot_fails_with_its_message_before_any_write() {
    for (message, error) in [
        (Some("Synthetic message."), "Synthetic message."),
        (None, UNAVAILABLE),
    ] {
        let panel = Panel {
            available: false,
            message,
            ..Panel::default()
        };
        let rig = fresh();
        let mut host = Scripted::new(panel, &[]);
        let outcome = apply(&rig, &mut host, full()).unwrap();
        assert_eq!(result(&outcome), (Status::Failed, Some(error)));
        assert_eq!(host.log, ["query"]);
        assert!(!flag(&rig.file.record(), "AutomaticWritesPaused"));
    }
}

#[test]
fn a_fatal_selecting_query_is_recorded_indeterminate_then_returned() {
    let rig = fresh();
    let mut host = Scripted::new(Panel::default(), &[]);
    host.fail_query = Some(1);
    assert!(apply(&rig, &mut host, full()).is_err());
    assert!(writes(&host).is_empty());
    let record = rig.file.record();
    assert!(flag(&record, "AutomaticWritesPaused"));
    assert_eq!(uncertain(&record), all_fields());
    // Nothing was submitted, so this Agent's gate stays open ...
    assert_eq!(rig.unresolved(), Fields::default());
    // ... and after a restart the durable record gates every field.
    assert_eq!(Rig::new(rig.file.clone()).unresolved(), all_fields());
}

#[test]
fn a_fatal_reply_query_after_a_submission_gates_that_field() {
    let rig = fresh();
    let mut host = Scripted::new(Panel::default(), &[Outcome::Applies; 2]);
    host.fail_query = Some(3);
    assert!(apply(&rig, &mut host, full()).is_err());
    assert_eq!(writes(&host), FULL_WRITES[..2]);
    // The HDR write was never read back, and the unavailable final reply no
    // longer shows the confirmed refresh write.
    assert_eq!(rig.unresolved(), Fields::REFRESH.include(Fields::HDR));
    assert!(flag(&rig.file.record(), "AutomaticWritesPaused"));
}

#[test]
fn a_fatal_brightness_worker_is_recorded_before_it_is_returned() {
    for (possible, gated) in [(true, Fields::BRIGHTNESS), (false, Fields::default())] {
        let rig = fresh();
        let mut host = Scripted::new(Panel::default(), &[Outcome::Fatal { possible }]);
        assert!(apply(&rig, &mut host, only(Fields::BRIGHTNESS)).is_err());
        assert_eq!(host.log, ["query", "brightness 50"], "no reply query");
        assert_eq!(rig.unresolved(), gated);
        let record = rig.file.record();
        assert!(flag(&record, "AutomaticWritesPaused") && flag(&record, "UncertainBrightness"));
    }
}

#[test]
fn cancellation_reaches_each_step_and_stops_the_profile() {
    // Canceled during the selecting query: the first write is refused by its
    // fence and nothing was submitted.
    let rig = fresh();
    let mut host = Scripted::new(Panel::default(), &[]);
    host.cancel_in_query = Some(1);
    let outcome = apply(&rig, &mut host, full()).unwrap();
    assert_eq!(result(&outcome), (Status::Blocked, Some(CANCELED)));
    assert_eq!(writes(&host), FULL_WRITES[..1]);
    assert!(!flag(&rig.file.record(), "AutomaticWritesPaused"));

    // Canceled during a later reply: the next step is refused, and the final
    // reply (interrupted, so unavailable) no longer shows the confirmed one.
    let rig = fresh();
    let mut host = Scripted::new(Panel::default(), &[Outcome::Applies]);
    host.cancel_in_query = Some(2);
    let outcome = apply(&rig, &mut host, full()).unwrap();
    assert_eq!(result(&outcome), (Status::Indeterminate, Some(CANCELED)));
    assert_eq!(writes(&host), FULL_WRITES[..2]);
    assert_eq!(rig.unresolved(), Fields::REFRESH);

    // Already canceled: the interrupted selecting read is unavailable.
    let rig = fresh();
    let mut host = Scripted::new(Panel::default(), &[]);
    host.cancel.store(true, Ordering::SeqCst);
    let outcome = apply(&rig, &mut host, full()).unwrap();
    assert_eq!(result(&outcome), (Status::Failed, Some(UNREFRESHED)));
    assert!(writes(&host).is_empty());

    // An interrupted reply after a submission leaves that write unshown.
    let rig = fresh();
    let mut host = Scripted::new(Panel::default(), &[Outcome::Applies]);
    let cancel = host.cancel.clone();
    let request = ApplyProfile {
        expected_power_source: 1,
        settings: only(Fields::REFRESH),
    };
    // Cancel arrives while the refresh step runs.
    struct CancelOnWrite<'a>(&'a mut Scripted);
    impl ProfileHost for CancelOnWrite<'_> {
        fn power(&self) -> Option<PowerContext> {
            self.0.power()
        }
        fn query(
            &mut self,
            brightness: &SharedBrightness,
            canceled: &AtomicBool,
        ) -> io::Result<Snapshot> {
            self.0.query(brightness, canceled)
        }
        fn refresh(
            &mut self,
            request: &SetRefreshRate,
            admitted: PowerContext,
            canceled: &AtomicBool,
        ) -> Attempt {
            let attempt = self.0.refresh(request, admitted, canceled);
            self.0.cancel.store(true, Ordering::SeqCst);
            attempt
        }
        fn hdr(
            &mut self,
            request: &SetHdr,
            admitted: PowerContext,
            canceled: &AtomicBool,
        ) -> Attempt {
            self.0.hdr(request, admitted, canceled)
        }
        fn color(
            &mut self,
            request: &SetColorProfile,
            admitted: PowerContext,
            canceled: &AtomicBool,
        ) -> Attempt {
            self.0.color(request, admitted, canceled)
        }
        fn brightness(
            &mut self,
            brightness: &SharedBrightness,
            percent: u8,
            admitted: PowerContext,
            canceled: &AtomicBool,
        ) -> brightness_write::Attempt {
            self.0.brightness(brightness, percent, admitted, canceled)
        }
    }
    let outcome = run(
        &mut CancelOnWrite(&mut host),
        &rig.owner,
        &rig.recovery,
        &request,
        &cancel,
    )
    .unwrap();
    assert_eq!(
        result(&outcome),
        (Status::Indeterminate, Some(refresh_write::UNCONFIRMED))
    );
    assert!(!outcome.state.observed().available);
    assert_eq!(rig.unresolved(), Fields::REFRESH);
}

#[test]
fn settings_report_their_fields_and_the_final_match() {
    assert_eq!(Settings::default().fields(), Fields::default());
    assert!(!Settings::default().configured());
    for field in [
        Fields::REFRESH,
        Fields::HDR,
        Fields::COLOR,
        Fields::BRIGHTNESS,
    ] {
        assert_eq!(only(field).fields(), field);
        assert!(only(field).configured());
    }

    let shown = Panel {
        refresh: Some("fixed-120".into()),
        hdr: false,
        color: Some("DCI-P3".into()),
        brightness: Some(50),
        ..Panel::default()
    };
    assert!(full().shown_by(&shown.snapshot().observed()));
    assert!(Settings::default().shown_by(&shown.snapshot().observed()));
    let differs = |panel: Panel| !full().shown_by(&panel.snapshot().observed());
    assert!(differs(Panel {
        available: false,
        ..shown.clone()
    }));
    assert!(differs(Panel {
        refresh: Some("FIXED-120".into()),
        ..shown.clone()
    }));
    assert!(differs(Panel {
        refresh: None,
        ..shown.clone()
    }));
    assert!(differs(Panel {
        hdr: true,
        ..shown.clone()
    }));
    assert!(differs(Panel {
        color: Some("srgb".into()),
        ..shown.clone()
    }));
    assert!(differs(Panel {
        color: None,
        ..shown.clone()
    }));
    assert!(differs(Panel {
        brightness: Some(75),
        ..shown.clone()
    }));
    assert!(differs(Panel {
        brightness: None,
        levels: Vec::new(),
        ..shown.clone()
    }));
    // Under HDR the color member is satisfied whatever the active ID.
    let hdr = Settings {
        hdr_enabled: Some(true),
        ..full()
    };
    assert!(
        hdr.shown_by(
            &Panel {
                hdr: true,
                color: None,
                ..shown.clone()
            }
            .snapshot()
            .observed()
        )
    );
    // An unavailable snapshot never shows even an unconfigured profile.
    assert!(
        !Settings::default().shown_by(
            &Panel {
                available: false,
                ..shown
            }
            .snapshot()
            .observed()
        )
    );
}

#[test]
fn color_ids_compare_ordinally_ignoring_case() {
    assert!(same_id(Some("DCI-P3"), "dci-p3"));
    assert!(!same_id(Some("dci-p3 "), "dci-p3"));
    assert!(!same_id(None, "dci-p3"));
}

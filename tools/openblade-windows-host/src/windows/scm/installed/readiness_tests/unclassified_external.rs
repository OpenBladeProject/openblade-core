//! Ordinary thermal setters on unclassified external power (managed f6502f85):
//! fresh Windows AC presence with a completed 07/8C query that classifies
//! nothing. No profile applies there, every setter needs its own fresh reading
//! that agrees with Windows AC, and battery policy keeps its classified-only
//! admission.
use super::*;
use crate::discovery::performance::mutation_fixture::State as Port;

/// The completed 07/8C forms managed decodes as `AdapterPowerState.Unknown`
/// with `UnclassifiedAdapterResponseAvailable`.
#[derive(Clone, Copy, Debug)]
enum Form {
    /// A correlated FAILED (3) or NOT_SUPPORTED (5) rejection.
    Rejected(u8),
    /// A successful reply with these actual and recommended levels.
    Levels(u8, u8),
}
const FORMS: [Form; 6] = [
    Form::Rejected(3),
    Form::Rejected(5),
    // 0x10 is the one unmapped level below 0x14.
    Form::Levels(0x10, 15),
    Form::Levels(15, 0x10),
    Form::Levels(15, 0),
    Form::Levels(15, 0x20),
];
const PENDING: &str = "A thermal command may have been submitted. Read-only reconciliation is required and no mutation will be replayed.";
const CLEARED: &str =
    "Override cleared. Power-profile application waits for adapter classification.";
/// Managed `PublishScopedUnavailable` for unclassified power it does not admit.
const BLOCKED: &str = "Hardware application was not confirmed. The external power adapter could not be classified; overrides and control writes are blocked.";

fn unclassify(rig: &Rig, form: Form) {
    let mut s = rig.port.state.lock().unwrap();
    match form {
        Form::Rejected(status) => s.power_status = status,
        Form::Levels(actual, recommended) => {
            s.actual = actual;
            s.recommended = recommended;
        }
    }
}
fn classify(rig: &Rig, actual: u8) {
    let mut s = rig.port.state.lock().unwrap();
    (s.power_status, s.actual, s.recommended) = (0, actual, 15);
}
fn unclassified_rig(form: Form) -> Rig {
    let files = MemoryFiles::default();
    files.profiles(1);
    let rig = Rig::new(files);
    unclassify(&rig, form);
    rig
}
fn writes(rig: &Rig) -> usize {
    rig.port.state.lock().unwrap().writes
}
fn power_reads(rig: &Rig) -> usize {
    rig.port.state.lock().unwrap().power_reads
}
/// Writes, mode selectors and fan modes, read without holding the fixture lock.
fn hardware(rig: &Rig) -> (usize, [u8; 2], [u8; 2]) {
    let s = rig.port.state.lock().unwrap();
    (s.writes, s.selectors, s.fans)
}
fn progress(rig: &Rig) -> Option<u8> {
    Storage::open(Box::new(rig.files.clone()), SID)
        .unwrap()
        .progress()
}
/// Re-seal a raw 07/8C reply frame after an edit (byte 0 is the report ID).
fn reseal(frame: &mut [u8; 91]) {
    frame[89] = openblade_protocol::calculate_checksum(&frame[1..]).unwrap();
}
#[cfg(feature = "service-fan-mutation-v2")]
impl Rig {
    /// An SCM notice as native ingress delivers it (`native::entry`): a power
    /// notice samples the Windows power kind before the lifecycle sees it.
    fn notice(&self, code: u32, event: u32) {
        if code == 13 {
            Fresh::power_generation(self.custody.as_ref());
        }
        self.custody.lifecycle.ingress(code, event);
    }
}
/// What happens between two requests.
#[cfg(feature = "service-fan-mutation-v2")]
#[derive(Clone, Copy, Debug, PartialEq)]
enum Between {
    Nothing,
    /// Lock, unlock and a power-status notice that keeps the Windows kind.
    Notices,
    /// Windows AC to battery and back, each with its power notice.
    KindChange,
    /// An unknown ACLineStatus at a power notice, then the kind again.
    UnknownSample,
}
#[cfg(feature = "service-fan-mutation-v2")]
const BETWEEN: [Between; 4] = [
    Between::Nothing,
    Between::Notices,
    Between::UnknownSample,
    Between::KindChange,
];
#[cfg(feature = "service-fan-mutation-v2")]
fn pass(rig: &Rig, between: Between) {
    match between {
        Between::Nothing => {}
        Between::Notices => {
            // WTS_SESSION_LOCK, WTS_SESSION_UNLOCK, PBT_APMPOWERSTATUSCHANGE.
            rig.notice(14, 7);
            rig.notice(14, 8);
            rig.notice(13, 10);
        }
        Between::UnknownSample | Between::KindChange => {
            let prior = rig.port.state.lock().unwrap().windows_on_battery;
            let between = (between == Between::KindChange).then_some(true);
            rig.port.state.lock().unwrap().windows_on_battery = Some(between);
            rig.notice(13, 10);
            rig.port.state.lock().unwrap().windows_on_battery = prior;
            rig.notice(13, 10);
        }
    }
}

#[tokio::test]
async fn unclassified_power_applies_ordinary_presets_with_a_fresh_read_per_setter() {
    for form in FORMS {
        for (preset, selector) in [(1, 0), (2, 2)] {
            let rig = unclassified_rig(form);
            let out = rig.set(1, value(preset, 1)).await;
            assert_eq!(out["result"]["status"], 1, "{form:?} {out}");
            {
                let s = rig.port.state.lock().unwrap();
                assert_eq!((s.writes, s.selectors), (2, [selector; 2]), "{form:?}");
                // One initial reading and one before each setter.
                assert_eq!(s.power_reads, 3, "{form:?}");
            }
            // The projection reports the unclassified class and the override as
            // the destination; no profile slot is selected.
            let status = rig.get().await["status"].clone();
            assert_eq!(status["activePowerSource"], 3, "{form:?}");
            assert_eq!(status["confirmedState"]["performance"], preset);
            assert_eq!(status["performanceOverride"]["value"], preset);
            assert_eq!(status["desiredState"]["performance"]["value"], preset);
            assert_eq!(status["desiredState"]["performance"]["source"], 4);
            rig.stop().await;
        }
    }
}

// Managed admits Unknown only with fresh Windows external presence, and
// replies with `PublishScopedUnavailable` before storing anything.
#[tokio::test]
async fn unclassified_power_without_windows_presence_stores_and_writes_nothing() {
    for form in [Form::Rejected(3), Form::Levels(15, 0x10)] {
        for windows in [Some(true), None] {
            let rig = unclassified_rig(form);
            rig.port.state.lock().unwrap().windows_on_battery = Some(windows);
            let out = rig.set(1, value(2, 1)).await;
            assert_eq!(out["result"]["status"], 2, "{form:?} {out}");
            assert_eq!(out["result"]["message"], BLOCKED, "{form:?}");
            assert_eq!(writes(&rig), 0);
            assert!(rig.files.read("manual-overrides.json").unwrap().is_none());
            rig.stop().await;
        }
    }
}

// Managed `AdmitOrdinaryThermalCommandAsync` requires every external reading,
// classified ones included, to agree with a fresh Windows external source.
#[tokio::test]
async fn classified_power_without_windows_presence_stores_and_writes_nothing() {
    for windows in [Some(true), None] {
        let rig = Rig::new(MemoryFiles::default());
        rig.port.state.lock().unwrap().windows_on_battery = Some(windows);
        let out = rig.set(1, value(2, 1)).await;
        assert_eq!(out["result"]["status"], 2, "{windows:?} {out}");
        assert_eq!(writes(&rig), 0);
        assert_eq!(power_reads(&rig), 1);
        assert!(rig.files.read("manual-overrides.json").unwrap().is_none());
        rig.stop().await;
    }
}

// Managed `CanAdmitUnclassifiedExternalControl` needs confirmed HyperBoost to be
// inactive, and its exit needs classified power: Blocked, nothing stored.
#[tokio::test]
async fn unclassified_power_refuses_a_hyperboost_baseline_before_any_intent() {
    for form in [Form::Rejected(3), Form::Levels(15, 0)] {
        for value in [value(2, 1), value(1, 0), Value::Null] {
            let rig = unclassified_rig(form);
            {
                let mut s = rig.port.state.lock().unwrap();
                (s.selectors, s.fans) = ([7; 2], [0; 2]);
            }
            let out = rig.set(1, value.clone()).await;
            assert_eq!(out["result"]["status"], 2, "{form:?} {value} {out}");
            assert_eq!(out["result"]["message"], BLOCKED);
            assert_eq!(hardware(&rig), (0, [7; 2], [0; 2]));
            assert!(rig.files.read("manual-overrides.json").unwrap().is_none());
            assert!(
                rig.files
                    .read("thermal-mutation.journal")
                    .unwrap()
                    .is_none()
            );
            rig.stop().await;
        }
    }
}

// A failed 07/8C read is never unclassified power: with Windows AC present,
// every one of these stores and writes nothing.
#[tokio::test]
async fn failed_adapter_reads_are_not_unclassified_power() {
    type Edit = fn(&mut [u8; 91]);
    let cases: [(&str, Option<u8>, Option<Edit>, bool); 10] = [
        // Raw statuses managed treats as no completed query.
        ("new", Some(1), None, false),
        ("timeout status", Some(4), None, false),
        ("unknown status", Some(6), None, false),
        (
            "transaction",
            None,
            Some(|f| {
                f[2] ^= 1;
                reseal(f);
            }),
            false,
        ),
        (
            "class",
            None,
            Some(|f| {
                f[7] ^= 1;
                reseal(f);
            }),
            false,
        ),
        (
            "command",
            None,
            Some(|f| {
                f[8] ^= 1;
                reseal(f);
            }),
            false,
        ),
        ("checksum", None, Some(|f| f[89] ^= 1), false),
        (
            "payload length",
            None,
            Some(|f| {
                f[6] = 3;
                reseal(f);
            }),
            false,
        ),
        // A correlated rejection with a corrupt checksum is not completed either.
        ("rejection checksum", Some(3), Some(|f| f[89] ^= 1), false),
        ("receive timeout", None, None, true),
    ];
    for (name, status, edit, timeout) in cases {
        let rig = Rig::new(MemoryFiles::default());
        {
            let mut s = rig.port.state.lock().unwrap();
            s.windows_on_battery = Some(Some(false));
            s.power_status = status.unwrap_or(0);
            s.power_edit = edit.map(|e| Arc::new(e) as _);
            s.power_timeout = timeout.then_some(1);
        }
        let out = rig.set(1, value(2, 1)).await;
        assert_eq!(out["result"]["status"], 2, "{name} {out}");
        assert_eq!(writes(&rig), 0, "{name}");
        assert!(
            rig.files.read("manual-overrides.json").unwrap().is_none(),
            "{name}"
        );
        rig.stop().await;
    }
}

// Managed maps a success reply to unclassified power only with single-report
// geometry (`WindowsRazerHidSession`); without it there is no reading.
#[tokio::test]
async fn unclassified_success_replies_need_single_report_geometry() {
    type Edit = fn(&mut [u8; 91]);
    let edits: [(&str, Edit); 4] = [
        ("byte 89", |f| f[90] = 1),
        ("byte 2", |f| {
            f[3] = 1;
            reseal(f);
        }),
        ("byte 3", |f| {
            f[4] = 1;
            reseal(f);
        }),
        ("byte 4", |f| {
            f[5] = 1;
            reseal(f);
        }),
    ];
    for (name, edit) in edits {
        for form in [Form::Levels(0x10, 15), Form::Levels(15, 0)] {
            let rig = unclassified_rig(form);
            rig.port.state.lock().unwrap().power_edit = Some(Arc::new(edit));
            let out = rig.set(1, value(2, 1)).await;
            assert_eq!(out["result"]["status"], 2, "{name} {form:?} {out}");
            assert_eq!(writes(&rig), 0, "{name} {form:?}");
            assert!(rig.files.read("manual-overrides.json").unwrap().is_none());
            rig.stop().await;
        }
    }
}

// Each setter re-reads 07/8C (reads 2 and 3 of the pair). Managed
// `AdmitOrdinaryThermalCommandAsync` admits any fresh external reading that
// agrees with Windows AC, so a class change either way keeps this explicit
// destination and the pair completes. Battery or lost presence stops it
// before the next command; nothing is replayed.
#[tokio::test]
async fn class_change_between_setters_follows_managed_admission() {
    type Change = fn(&mut Port);
    let cases: [(&str, Change, bool); 6] = [
        (
            "unclassified to full",
            |s| {
                s.power_status = 3;
                s.change_power_status = Some((3, 0));
            },
            true,
        ),
        (
            "unclassified to limited",
            |s| {
                (s.actual, s.recommended) = (0x10, 15);
                s.change_power = Some((3, 10));
            },
            true,
        ),
        (
            "full to unclassified",
            |s| s.change_power_status = Some((3, 5)),
            true,
        ),
        (
            "limited to unclassified",
            |s| {
                s.actual = 10;
                s.change_power = Some((3, 0x10));
            },
            true,
        ),
        (
            "unclassified to battery",
            |s| {
                s.power_status = 3;
                s.change_power_status = Some((3, 0));
                s.change_power = Some((3, 0));
                s.change_windows = Some((3, Some(true)));
            },
            false,
        ),
        (
            "presence lost",
            |s| {
                s.power_status = 3;
                s.change_windows = Some((3, Some(true)));
            },
            false,
        ),
    ];
    for (name, change, completes) in cases {
        let rig = Rig::new(MemoryFiles::default());
        change(&mut rig.port.state.lock().unwrap());
        let out = rig.set(2, value(2, 1)).await;
        assert_eq!(power_reads(&rig), 3, "{name}");
        if completes {
            assert_eq!(out["result"]["status"], 1, "{name} {out}");
            assert_eq!(hardware(&rig).0, 2, "{name}");
            assert_eq!(progress(&rig), Some(5), "{name}");
        } else {
            assert_eq!(out["result"]["status"], 2, "{name} {out}");
            // A setter may already be submitted, so reconciliation owns the reply.
            assert_eq!(out["result"]["message"], PENDING, "{name}");
            assert_eq!(writes(&rig), 1, "{name}");
            // The first setter's acknowledgement is the last durable stage.
            assert_eq!(progress(&rig), Some(2), "{name}");
        }
        rig.stop().await;
    }
}

// A profile destination that differs on the new class stops the pair: a clear
// on full power selects the plugged-in profile, and unclassified selects none.
#[tokio::test]
async fn class_change_to_another_destination_stops_without_replay() {
    let files = MemoryFiles::default();
    files.profiles(1);
    let rig = Rig::new(files);
    rig.port.state.lock().unwrap().change_power_status = Some((3, 5));
    let out = rig.set(1, Value::Null).await;
    assert_eq!(out["result"]["status"], 2, "{out}");
    assert_eq!(out["result"]["message"], PENDING);
    assert_eq!(writes(&rig), 1);
    assert_eq!(progress(&rig), Some(2));
    rig.stop().await;
}

// Presence lost at a setter's reading is a power change before any submission.
#[tokio::test]
async fn presence_lost_at_the_first_setter_read_is_a_power_change() {
    for form in [None, Some(Form::Rejected(3))] {
        let rig = Rig::new(MemoryFiles::default());
        if let Some(form) = form {
            unclassify(&rig, form);
        }
        rig.port.state.lock().unwrap().change_windows = Some((2, Some(true)));
        let out = rig.set(1, value(2, 1)).await;
        assert_eq!(out["result"]["status"], 2, "{form:?} {out}");
        assert!(
            out["result"]["message"]
                .as_str()
                .unwrap()
                .contains("power source changed"),
            "{form:?} {out}"
        );
        assert_eq!(writes(&rig), 0);
        assert_eq!(progress(&rig), Some(6));
        rig.stop().await;
    }
}

// Presence lost after a setter's reading admitted it, before its SET: the
// checkpoint before submission stops it.
#[tokio::test]
async fn presence_lost_after_the_setter_read_stops_before_its_set() {
    for (at, submitted) in [(2, 0), (3, 1)] {
        for form in [None, Some(Form::Rejected(3))] {
            let rig = Rig::new(MemoryFiles::default());
            if let Some(form) = form {
                unclassify(&rig, form);
            }
            rig.port.state.lock().unwrap().change_windows_after_reply = Some((at, Some(true)));
            let out = rig.set(1, value(2, 1)).await;
            assert_eq!(out["result"]["status"], 2, "{at} {form:?} {out}");
            assert_eq!(writes(&rig), submitted, "{at} {form:?}");
            assert_eq!(power_reads(&rig), at, "{at} {form:?}");
            rig.stop().await;
        }
    }
}

// Managed rejects native overclocking on unclassified power before storing it,
// GPU clock tuning and legacy Custom offsets alike.
#[tokio::test]
async fn unclassified_power_rejects_gpu_tuning_before_any_intent() {
    let mut tuned = value(2, 1);
    tuned["gpuClockTuning"] =
        json!({"enabled":true,"offsets":{"coreMegahertz":50,"memoryMegahertz":0}});
    let mut legacy = value(5, 1);
    legacy["customPerformance"] = json!({"cpu":1,"gpu":0,"maximumFans":false,
        "gpuClockOffsets":{"coreMegahertz":50,"memoryMegahertz":0}});
    for request in [tuned, legacy] {
        let rig = unclassified_rig(Form::Rejected(3));
        let out = rig.set(1, request).await;
        assert_eq!(out["result"]["status"], 3, "{out}");
        assert_eq!(
            out["result"]["message"],
            "The requested intent was rejected: This request needs classified power or includes native overclocking; ordinary external presence does not admit it."
        );
        assert_eq!(writes(&rig), 0);
        assert!(rig.files.read("manual-overrides.json").unwrap().is_none());
        rig.stop().await;
    }
}

// No power profile or application profile applies while unclassified; the same
// clear applies the pluggedIn or usbC slot once the adapter is classified.
#[tokio::test]
async fn profiles_wait_for_classification_and_apply_after_it() {
    for (actual, application, preset) in [(15, false, 1), (15, true, 2), (10, false, 2)] {
        let files = MemoryFiles::default();
        files.profiles_with(1, Some(2));
        let rig = Rig::new(files);
        if application {
            report_game(&rig).await;
        }
        assert_eq!(rig.set(1, value(2, 1)).await["result"]["status"], 1);
        {
            // Back to the fixture's Custom selector with manual fans.
            let mut s = rig.port.state.lock().unwrap();
            (s.selectors, s.fans) = ([4; 2], [1; 2]);
        }
        unclassify(&rig, Form::Rejected(5));
        let cleared = rig.set(2, Value::Null).await;
        assert_eq!(cleared["result"]["status"], 0, "{cleared}");
        assert_eq!(cleared["result"]["message"], CLEARED);
        assert_eq!(writes(&rig), 2);
        assert!(rig.files.document("manual-overrides.json")["overrides"]["performance"].is_null());
        classify(&rig, actual);
        let applied = rig.set(3, Value::Null).await;
        assert_eq!(
            applied["result"]["status"], 1,
            "{actual} {application} {applied}"
        );
        assert_eq!(writes(&rig), 4);
        let selector = if preset == 2 { 2 } else { 0 };
        assert_eq!(rig.port.state.lock().unwrap().selectors, [selector; 2]);
        rig.stop().await;
    }
}

// A request on unclassified power reads, refines nothing and writes only its
// own override; classification later applies the profile without replaying it.
#[tokio::test]
async fn classified_and_unclassified_requests_alternate_without_inherited_writes() {
    let rig = unclassified_rig(Form::Levels(15, 0));
    assert_eq!(rig.set(1, value(2, 1)).await["result"]["status"], 1);
    classify(&rig, 15);
    rig.port.state.lock().unwrap().selectors = [0; 2];
    assert_eq!(rig.set(2, value(2, 1)).await["result"]["status"], 1);
    unclassify(&rig, Form::Rejected(3));
    assert_eq!(rig.set(3, value(1, 1)).await["result"]["status"], 1);
    let (writes, selectors, _) = hardware(&rig);
    assert_eq!((writes, selectors), (6, [0; 2]));
    rig.stop().await;
}

// Temporary intent: kept while unclassified (managed keeps its automation
// source until classification), retired by a classified class it was not
// saved on, and battery intent is retired once AC arrives unclassified. A
// temporary saved unclassified follows the first classified class within one
// Windows power generation (managed
// `PreserveOverridesForExternalClassification`).
#[test]
fn temporary_overrides_follow_classification_boundaries() {
    let files = MemoryFiles::default();
    files.profiles(1);
    let storage = Storage::open(Box::new(files), SID).unwrap();
    let temporary = |value| super::super::thermal_storage::Override {
        value,
        lifetime: 0,
        custom_performance: None,
        gpu_clock_tuning: None,
    };
    let desired = |power| {
        saved_desired(&storage, power)
            .unwrap()
            .map(|(p, s)| (p.wire(), s))
    };
    storage.save(Some(temporary(2)), 0).unwrap();
    assert_eq!(desired(3), Some((2, 3)));
    assert_eq!(desired(0), Some((2, 3)));
    // Saved unclassified with no generation, classified later: retired.
    storage.save(Some(temporary(2)), 3).unwrap();
    assert_eq!(desired(3), Some((2, 3)));
    assert_eq!(desired(0), Some((1, 1)));
    assert_eq!(desired(3), None);
    // Saved unclassified at generation 7 and classified at 7: refined to full, so
    // a later limited class retires it.
    storage.observe_power(3, 7).unwrap();
    storage.save(Some(temporary(2)), 3).unwrap();
    storage.observe_power(0, 7).unwrap();
    assert_eq!(desired(0), Some((2, 3)));
    storage.observe_power(3, 7).unwrap();
    assert_eq!(desired(3), Some((2, 3)));
    assert_eq!(desired(0), Some((2, 3)));
    storage.observe_power(2, 7).unwrap();
    assert_eq!(desired(2), Some((1, 1)));
    // A Windows power kind change between the save and the classification
    // retires it.
    storage.observe_power(3, 8).unwrap();
    storage.save(Some(temporary(2)), 3).unwrap();
    storage.observe_power(0, 9).unwrap();
    assert_eq!(desired(0), Some((1, 1)));
    // Saved on battery (no AC preset is eligible there, so seed it directly
    // through a classified save first): unclassified AC retires it.
    storage.save(Some(temporary(2)), 1).unwrap();
    assert!(storage.current_override(1).unwrap().is_some());
    assert!(storage.current_override(3).unwrap().is_none());
    assert_eq!(desired(3), None);
    // A real Windows kind change (AC, battery, AC) retires a temporary saved
    // unclassified even when the adapter is still unclassified; a sample
    // that keeps the kind or fails does not.
    let lifecycle = crate::scm_host::Lifecycle::default();
    let generation = |kind| lifecycle.observe_power_kind(kind).unwrap();
    storage.observe_power(3, generation(Some(2))).unwrap();
    storage.save(Some(temporary(2)), 3).unwrap();
    generation(None);
    storage.observe_power(3, generation(Some(2))).unwrap();
    assert_eq!(desired(3), Some((2, 3)));
    generation(Some(1));
    storage.observe_power(3, generation(Some(2))).unwrap();
    assert_eq!(desired(3), None);
}

// The status base may be older than the read: it reports 1 for battery and 3
// for any other power. Saved intent is read at that class only where fresh
// Windows power agrees (`status_query_power`); a stale or unknown read is no
// power boundary (managed `CaptureControlPowerSource`) and retires nothing.
#[tokio::test]
async fn status_reads_saved_intent_only_where_fresh_windows_power_agrees() {
    let lifetime = |status: &Value| status["status"]["performanceOverride"]["lifetime"].clone();
    // An AC temporary under a stale battery base, then an agreeing one.
    let files = MemoryFiles::default();
    files.profiles(1);
    let rig = Rig::new(files);
    assert_eq!(rig.set(1, value(2, 0)).await["result"]["status"], 1);
    rig.metadata.store(1, Ordering::Release);
    assert_eq!(lifetime(&rig.get().await), 0);
    rig.port.state.lock().unwrap().windows_on_battery = Some(Some(true));
    assert!(rig.get().await["status"]["performanceOverride"].is_null());
    rig.stop().await;
    // A battery temporary under a stale AC base and under unknown Windows
    // power, then an agreeing AC base.
    let files = MemoryFiles::default();
    files.profiles(1);
    let rig = Rig::new(files);
    rig.port.state.lock().unwrap().actual = 0;
    rig.metadata.store(1, Ordering::Release);
    rig.set(1, value(3, 0)).await;
    assert_eq!(lifetime(&rig.get().await), 0);
    rig.metadata.store(0, Ordering::Release);
    assert_eq!(lifetime(&rig.get().await), 0);
    rig.port.state.lock().unwrap().windows_on_battery = Some(None);
    let unknown = rig.get().await;
    assert_eq!(unknown["status"]["activePowerSource"], 3);
    assert_eq!(lifetime(&unknown), 0);
    rig.port.state.lock().unwrap().windows_on_battery = None;
    rig.port.state.lock().unwrap().actual = 15;
    assert!(rig.get().await["status"]["performanceOverride"].is_null());
    rig.stop().await;
}

// Managed `requireExternalPowerOnDirectHyperBoostExit`: a direct exit to a
// non-Balanced preset needs every command's reading in the first class, so a
// class change stops it before the next command and nothing is replayed. The
// Balanced exit has no class rule and follows ordinary admission.
#[tokio::test]
async fn hyperboost_exits_keep_the_first_class_except_to_balanced() {
    type Change = fn(&mut Port);
    let cases: [(&str, i32, Change, usize); 5] = [
        ("steady", 2, |_| {}, 2),
        (
            "unclassified at the first setter",
            2,
            |s| s.change_power_status = Some((2, 5)),
            0,
        ),
        (
            "unclassified at the second setter",
            2,
            |s| s.change_power_status = Some((3, 5)),
            1,
        ),
        (
            "limited at the second setter",
            2,
            |s| s.change_power = Some((3, 10)),
            1,
        ),
        (
            "Balanced, limited at the second setter",
            1,
            |s| s.change_power = Some((3, 10)),
            2,
        ),
    ];
    for (name, preset, change, submitted) in cases {
        let rig = Rig::new(MemoryFiles::default());
        {
            let mut s = rig.port.state.lock().unwrap();
            (s.selectors, s.fans) = ([7; 2], [0; 2]);
            change(&mut s);
        }
        let out = rig.set(1, value(preset, 1)).await;
        assert_eq!(writes(&rig), submitted, "{name} {out}");
        let status = if submitted == 2 { 1 } else { 2 };
        assert_eq!(out["result"]["status"], status, "{name} {out}");
        if submitted == 1 {
            assert_eq!(out["result"]["message"], PENDING, "{name}");
        }
        rig.stop().await;
    }
}

// A temporary saved unclassified and classified at a setter's reading is
// refined to the new class within the power generation, so its destination
// holds there and the pair completes.
#[tokio::test]
async fn unclassified_temporary_refined_mid_pair_completes() {
    let rig = unclassified_rig(Form::Rejected(3));
    rig.port.state.lock().unwrap().change_power_status = Some((3, 0));
    let out = rig.set(1, value(2, 0)).await;
    assert_eq!(out["result"]["status"], 1, "{out}");
    assert_eq!(writes(&rig), 2);
    let status = rig.get().await["status"].clone();
    assert_eq!(status["performanceOverride"]["lifetime"], 0, "{status}");
    rig.stop().await;
}

// A class change onto a profile with a GPU effect that has no native pair
// leaves the destination incomplete there, so the pair stops.
#[tokio::test]
async fn class_change_onto_an_incomplete_gpu_profile_stops_the_pair() {
    let files = MemoryFiles::default();
    files.profiles_gpu(1);
    let rig = Rig::new(files);
    unclassify(&rig, Form::Rejected(3));
    rig.port.state.lock().unwrap().change_power_status = Some((3, 0));
    let out = rig.set(1, value(2, 1)).await;
    assert_eq!(out["result"]["status"], 2, "{out}");
    assert_eq!(out["result"]["message"], PENDING);
    assert_eq!(writes(&rig), 1);
    rig.stop().await;
}

// Battery policy keeps classified-only admission: no write on any unclassified form.
#[cfg(feature = "service-battery-policy-v1")]
#[tokio::test]
async fn battery_policy_is_unaffected_by_unclassified_power() {
    for form in FORMS {
        let rig = unclassified_rig(form);
        let out = rig
            .request(battery_request(
                1,
                "setBatteryPolicy",
                json!({"chargeLimitPercent":70}),
            ))
            .await;
        assert_ne!(out["result"]["status"], 1, "{form:?} {out}");
        assert_eq!(writes(&rig), 0, "{form:?}");
        rig.stop().await;
    }
}

#[cfg(feature = "service-fan-mutation-v2")]
mod fans {
    use super::*;

    fn unclassified_fan_rig(selector: u8, manual: u8, form: Form) -> Rig {
        let rig = fan_rig(selector, manual);
        unclassify(&rig, form);
        rig
    }
    fn last_source(rig: &Rig) -> Value {
        fan_rows(rig).last().unwrap()["fan"]["source"].clone()
    }

    #[tokio::test]
    async fn explicit_fan_modes_apply_and_journal_the_unclassified_source() {
        for form in [Form::Rejected(3), Form::Levels(15, 0x10)] {
            for (selector, mode, rpm) in [(0, 1, 3700), (2, 1, 2600), (0, 3, 5400), (2, 3, 5400)] {
                let rig = unclassified_fan_rig(selector, 0, form);
                let out = rig.request(fan_request(1, Some(mode), rpm)).await;
                assert_eq!(out["result"]["status"], 1, "{form:?} {out}");
                assert_eq!(last_source(&rig), 3);
                assert_eq!(fan_rows(&rig).last().unwrap()["progress"], 30);
                assert!(rig.storage.outstanding_fan());
                let (_, selectors, fans) = hardware(&rig);
                assert_eq!((selectors, fans), ([selector; 2], [1; 2]), "{form:?}");
                // The explicit return to Automatic hands back on the same class.
                let back = rig.request(fan_request(2, Some(0), 3700)).await;
                assert_eq!(back["result"]["status"], 1, "{form:?} {back}");
                assert_eq!(fan_rows(&rig).last().unwrap()["progress"], 50);
                assert!(!rig.storage.outstanding_fan());
                assert_eq!(rig.port.state.lock().unwrap().fans, [0; 2]);
                let status = rig.get().await["status"].clone();
                assert_eq!(status["activePowerSource"], 3);
                rig.origin.stop();
                rig.stop().await;
            }
        }
    }

    // A fan setter's fresh reading may change class while the saved fan
    // settings, the same on every external class, hold (managed admission);
    // battery stops the chain without replay. A manual change that keeps the
    // Performance selector has no carried preset, so managed pins its class
    // (`EnsureThermalWritePowerAsync`) and a class change stops it too.
    #[tokio::test]
    async fn fan_setters_follow_managed_class_admission() {
        for (name, selector, battery, completes) in [
            ("unclassified to full", 0, false, true),
            ("unclassified to battery", 0, true, false),
            ("unclassified to full under Performance", 2, false, false),
        ] {
            let rig = fan_rig(selector, 0);
            let reads = power_reads(&rig);
            {
                let mut s = rig.port.state.lock().unwrap();
                s.power_status = 3;
                s.change_power_status = Some((reads + 3, 0));
                if battery {
                    s.change_power = Some((reads + 3, 0));
                }
            }
            let out = rig.request(fan_request(1, Some(1), 3700)).await;
            // The row keeps the first class.
            assert_eq!(fan_rows(&rig)[0]["fan"]["source"], 3, "{name}");
            if completes {
                assert_eq!(out["result"]["status"], 1, "{name} {out}");
                assert_eq!(hardware(&rig), (4, [0; 2], [1; 2]), "{name}");
                assert_eq!(fan_rows(&rig).last().unwrap()["progress"], 30);
                rig.origin.stop();
                rig.stop().await;
            } else {
                assert_eq!(out["result"]["status"], 2, "{name} {out}");
                assert_eq!(writes(&rig), 1, "{name}");
                assert_eq!(fan_rows(&rig).last().unwrap()["progress"], 11);
                rig.origin.stop();
                // The partial submission stays fenced for read-only reconciliation.
                assert!(rig.source.lock().await.drain().await.is_err());
            }
        }
    }

    // A battery reading while Windows still reports AC stops the chain: no
    // fan destination holds on battery.
    #[tokio::test]
    async fn fan_setters_stop_on_a_battery_reading_under_windows_ac() {
        let rig = fan_rig(0, 0);
        let reads = power_reads(&rig);
        {
            let mut s = rig.port.state.lock().unwrap();
            s.windows_on_battery = Some(Some(false));
            s.change_power = Some((reads + 3, 0));
        }
        let out = rig.request(fan_request(1, Some(1), 3700)).await;
        assert_eq!(out["result"]["status"], 2, "{out}");
        assert_eq!(writes(&rig), 1);
        assert_eq!(fan_rows(&rig).last().unwrap()["progress"], 11);
        rig.origin.stop();
        assert!(rig.source.lock().await.drain().await.is_err());
    }

    // A setter reading admitted with Windows AC needs it until its SET, even
    // after earlier readings were admitted unchecked under Windows battery
    // (managed rechecks `OrdinarySourceUnchanged` per command).
    #[tokio::test]
    async fn fan_setter_admitted_on_windows_ac_needs_it_until_its_set() {
        let rig = fan_rig(0, 0);
        let reads = power_reads(&rig);
        {
            let mut s = rig.port.state.lock().unwrap();
            s.windows_on_battery = Some(Some(true));
            s.change_windows = Some((reads + 3, Some(false)));
            s.change_windows_after_reply = Some((reads + 3, Some(true)));
        }
        let out = rig.request(fan_request(1, Some(1), 3700)).await;
        assert_eq!(out["result"]["status"], 2, "{out}");
        assert_eq!(writes(&rig), 1);
        assert_eq!(fan_rows(&rig).last().unwrap()["progress"], 11);
        rig.origin.stop();
        assert!(rig.source.lock().await.drain().await.is_err());
    }

    // On classified power managed admits a fan command without any check
    // while Windows reports battery (`allowBattery`), so a fan request and its
    // owned Stop both apply; unknown Windows power admits neither.
    #[tokio::test]
    async fn classified_fan_commands_follow_managed_windows_admission() {
        for windows in [Some(true), None] {
            let rig = fan_rig(0, 0);
            rig.port.state.lock().unwrap().windows_on_battery = Some(windows);
            let out = rig.request(fan_request(1, Some(1), 3700)).await;
            if windows.is_some() {
                assert_eq!(out["result"]["status"], 1, "{out}");
                assert_eq!(hardware(&rig), (4, [0; 2], [1; 2]));
                rig.origin.stop();
                rig.stop().await;
                assert_eq!(fan_rows(&rig).last().unwrap()["progress"], 50);
                assert_eq!(hardware(&rig).2, [0; 2]);
            } else {
                assert_eq!(out["result"]["status"], 2, "{out}");
                assert_eq!(writes(&rig), 0);
                assert!(fan_rows(&rig).is_empty());
                rig.origin.stop();
                rig.stop().await;
            }
        }
    }

    // Lost presence stops the fan chain: at a setter's reading, or after it
    // admitted the setter and before that setter's SET.
    #[tokio::test]
    async fn fan_setters_stop_when_presence_is_lost() {
        for after_reply in [false, true] {
            let rig = unclassified_fan_rig(0, 0, Form::Rejected(3));
            let reads = power_reads(&rig);
            {
                let mut s = rig.port.state.lock().unwrap();
                if after_reply {
                    s.change_windows_after_reply = Some((reads + 3, Some(true)));
                } else {
                    s.change_windows = Some((reads + 3, Some(true)));
                }
            }
            let out = rig.request(fan_request(1, Some(1), 3700)).await;
            assert_eq!(out["result"]["status"], 2, "{after_reply} {out}");
            assert_eq!(writes(&rig), 1, "{after_reply}");
            assert_eq!(fan_rows(&rig).last().unwrap()["progress"], 11);
            rig.origin.stop();
            assert!(rig.source.lock().await.drain().await.is_err());
            assert_eq!(writes(&rig), 1, "{after_reply}");
        }
    }

    // Managed refuses fan control on unclassified power under HyperBoost.
    #[tokio::test]
    async fn fan_requests_refuse_a_hyperboost_baseline_on_unclassified_power() {
        for (mode, rpm) in [(Some(1), 3700), (Some(0), 3700), (None, 0)] {
            let rig = unclassified_fan_rig(7, 0, Form::Rejected(5));
            let out = rig.request(fan_request(1, mode, rpm)).await;
            assert_eq!(out["result"]["status"], 2, "{mode:?} {out}");
            assert_eq!(out["result"]["message"], BLOCKED);
            assert_eq!(hardware(&rig), (0, [7; 2], [0; 2]));
            assert!(rig.files.read("manual-overrides.json").unwrap().is_none());
            assert!(fan_rows(&rig).is_empty());
            rig.origin.stop();
            rig.stop().await;
        }
    }

    // Managed hands back only an owned manual change on a clear, then Stores.
    #[tokio::test]
    async fn clear_hands_back_an_owned_change_then_stores() {
        for form in [Form::Rejected(5), Form::Levels(0x10, 15)] {
            let rig = unclassified_fan_rig(2, 0, form);
            assert_eq!(
                rig.request(fan_request(1, Some(3), 5400)).await["result"]["status"],
                1
            );
            let before = writes(&rig);
            let out = rig.request(fan_request(2, None, 0)).await;
            assert_eq!(out["result"]["status"], 0, "{form:?} {out}");
            assert_eq!(out["result"]["message"], CLEARED);
            assert!(!rig.storage.outstanding_fan());
            // The observed Performance selector is kept; no saved preset is applied.
            let (total, selectors, fans) = hardware(&rig);
            assert_eq!((total - before, selectors, fans), (2, [2; 2], [0; 2]));
            // Nothing owned: the next clear writes nothing.
            let again = rig.request(fan_request(3, None, 0)).await;
            assert_eq!(again["result"]["status"], 0, "{again}");
            assert_eq!(again["result"]["message"], CLEARED);
            assert_eq!(writes(&rig), before + 2);
            rig.origin.stop();
            rig.stop().await;
        }
    }

    // A clear with nothing owned is accepted, writes nothing and Stores. A
    // gate generation that moves while it runs (set and cleared again) does
    // not relabel that Stored reply as a low-power deferral: only a Blocked
    // reply takes it (`overtaken_by_gate`). This rig never reported, so its
    // pair still owes startup and the debt cannot tell the two apart.
    #[cfg(feature = "service-profile-triggers-v1")]
    #[tokio::test]
    async fn a_stored_clear_the_gate_generation_passes_keeps_its_reply() {
        use openblade_domain::low_power_gate::Transition;
        let rig = unclassified_fan_rig(2, 0, Form::Rejected(5));
        let moved = Arc::new(AtomicBool::new(false));
        let probe = moved.clone();
        rig.runtime.read_low_power(move || {
            Some(Transition {
                generation: if probe.load(Ordering::Acquire) { 2 } else { 0 },
                timestamp: 0,
            })
        });
        let hook = moved.clone();
        rig.port.state.lock().unwrap().on_mutation_retired = Some(Arc::new(move || {
            hook.store(true, Ordering::Release);
        }));
        let out = rig.request(fan_request(1, None, 0)).await;
        assert!(moved.load(Ordering::Acquire), "{out}");
        assert_eq!(out["result"]["status"], 0, "{out}");
        assert_eq!(out["result"]["message"], CLEARED);
        assert_eq!(writes(&rig), 0);
        rig.origin.stop();
        rig.stop().await;
    }

    // With manual fans saved, a performance request is deferred on every
    // external class, as the managed coordinator leaves manual fans in place.
    #[tokio::test]
    async fn performance_defers_under_manual_fans_on_unclassified_power() {
        let rig = unclassified_fan_rig(0, 0, Form::Rejected(3));
        assert_eq!(
            rig.request(fan_request(1, Some(1), 3700)).await["result"]["status"],
            1
        );
        let deferred = rig.set(2, value(2, 1)).await;
        assert_eq!(deferred["result"]["status"], 0, "{deferred}");
        assert_eq!(deferred["result"]["message"], DEFERRED);
        assert_eq!(writes(&rig), 4);
        // The return to Automatic applies it in the same pair, on the same class.
        let back = rig.request(fan_request(3, Some(0), 3700)).await;
        assert_eq!(back["result"]["status"], 1, "{back}");
        assert_eq!(hardware(&rig), (6, [2; 2], [0; 2]));
        rig.origin.stop();
        rig.stop().await;
    }

    // A temporary Performance override saved unclassified is kept when the
    // adapter is then classified within one Windows power generation, so the
    // return to Automatic applies it (managed
    // `PreserveOverridesForExternalClassification`). Lock, unlock and a status
    // notice that keep the Windows kind do not end the generation; a kind
    // change does, and the plugged-in profile's Balanced applies.
    #[tokio::test]
    async fn unclassified_temporary_survives_classification_within_one_power_generation() {
        for between in BETWEEN {
            let rig = unclassified_fan_rig(0, 0, Form::Rejected(3));
            let performance = rig.set(1, value(2, 0)).await;
            assert_eq!(performance["result"]["status"], 1, "{performance}");
            let fixed = rig.request(fan_request(2, Some(1), 3700)).await;
            assert_eq!(fixed["result"]["status"], 1, "{fixed}");
            classify(&rig, 15);
            pass(&rig, between);
            let back = rig.request(fan_request(3, Some(0), 3700)).await;
            assert_eq!(back["result"]["status"], 1, "{between:?} {back}");
            let lapsed = between == Between::KindChange;
            let selector = if lapsed { 0 } else { 2 };
            assert_eq!(hardware(&rig).1, [selector; 2], "{between:?}");
            let status = rig.get().await["status"].clone();
            assert_eq!(status["performanceOverride"].is_null(), lapsed, "{status}");
            rig.origin.stop();
            rig.stop().await;
        }
    }

    // A temporary fan override saved unclassified follows the same rule:
    // classified within the generation it stays the manual intent, so a
    // performance request is still deferred; after a kind change it lapses.
    #[tokio::test]
    async fn unclassified_fan_temporary_survives_classification_within_one_power_generation() {
        for between in BETWEEN {
            let rig = unclassified_fan_rig(0, 0, Form::Rejected(3));
            let mut fixed = fan_request(1, Some(1), 3700);
            fixed["value"]["lifetime"] = json!(0);
            assert_eq!(rig.request(fixed).await["result"]["status"], 1);
            classify(&rig, 15);
            pass(&rig, between);
            let out = rig.set(2, value(2, 1)).await;
            let status = rig.get().await["status"].clone();
            assert_eq!(hardware(&rig), (4, [0; 2], [1; 2]), "{between:?}");
            if between == Between::KindChange {
                // The owned manual change is still in hardware, so nothing is
                // submitted.
                assert_eq!(out["result"]["status"], 2, "{out}");
                assert!(status["fanOverride"].is_null(), "{status}");
            } else {
                assert_eq!(out["result"]["status"], 0, "{between:?} {out}");
                assert_eq!(out["result"]["message"], DEFERRED);
                assert_eq!(status["fanOverride"], json!({"lifetime": 0, "value": 1}));
            }
            rig.origin.stop();
            if between == Between::Nothing {
                rig.stop().await;
            } else {
                // After any notice Stop takes the settle-only permit and fails
                // closed, as in the classified tests.
                assert!(rig.source.lock().await.drain().await.is_err());
            }
        }
    }

    // A temporary fan override saved unclassified and classified at a
    // setter's reading is refined to the new class, so the pair completes.
    #[tokio::test]
    async fn unclassified_fan_temporary_refined_mid_pair_completes() {
        let rig = unclassified_fan_rig(0, 0, Form::Rejected(3));
        let reads = power_reads(&rig);
        rig.port.state.lock().unwrap().change_power_status = Some((reads + 3, 0));
        let mut fixed = fan_request(1, Some(1), 3700);
        fixed["value"]["lifetime"] = json!(0);
        let out = rig.request(fixed).await;
        assert_eq!(out["result"]["status"], 1, "{out}");
        assert_eq!(hardware(&rig), (4, [0; 2], [1; 2]));
        rig.origin.stop();
        rig.stop().await;
    }

    // An unclassified clear hands back to Automatic with no saved settings;
    // like owned Stop it continues across a classification under Balanced,
    // and the Performance selector without a carried preset pins its class.
    #[tokio::test]
    async fn unclassified_clear_handback_follows_the_class_pin() {
        for (selector, completes) in [(0, true), (2, false)] {
            let rig = unclassified_fan_rig(selector, 0, Form::Rejected(5));
            assert_eq!(
                rig.request(fan_request(1, Some(1), 3700)).await["result"]["status"],
                1
            );
            let before = writes(&rig);
            let reads = power_reads(&rig);
            rig.port.state.lock().unwrap().change_power_status = Some((reads + 3, 0));
            let out = rig.request(fan_request(2, None, 0)).await;
            rig.origin.stop();
            if completes {
                assert_eq!(out["result"]["status"], 0, "{selector} {out}");
                assert_eq!(out["result"]["message"], CLEARED);
                assert_eq!(writes(&rig) - before, 2);
                assert_eq!(hardware(&rig).2, [0; 2]);
                assert!(!rig.storage.outstanding_fan());
                rig.stop().await;
            } else {
                assert_eq!(out["result"]["status"], 2, "{selector} {out}");
                assert_eq!(writes(&rig) - before, 1);
                assert!(rig.source.lock().await.drain().await.is_err());
            }
        }
    }

    // A temporary fan override saved on full power is kept by the status
    // projection (3 on any AC), still returns through the saved preset, and
    // battery retires it for good.
    #[tokio::test]
    async fn temporary_fan_override_survives_status_and_lapses_on_battery() {
        let rig = fan_rig_with(0, 0, 2);
        let mut fixed = fan_request(1, Some(1), 3700);
        fixed["value"]["lifetime"] = json!(0);
        assert_eq!(rig.request(fixed).await["result"]["status"], 1);
        for _ in 0..2 {
            let status = rig.get().await["status"].clone();
            assert_eq!(status["fanOverride"]["lifetime"], 0, "{status}");
            assert_eq!(status["fanOverride"]["value"], 1);
        }
        // Leaving the saved manual intent applies the plugged-in Performance.
        let back = rig.request(fan_request(2, Some(0), 3700)).await;
        assert_eq!(back["result"]["status"], 1, "{back}");
        assert_eq!(hardware(&rig).1, [2; 2]);
        let mut fixed = fan_request(3, Some(1), 3700);
        fixed["value"]["lifetime"] = json!(0);
        assert_eq!(rig.request(fixed).await["result"]["status"], 1);
        rig.metadata.store(1, Ordering::Release);
        rig.port.state.lock().unwrap().actual = 0;
        assert!(rig.get().await["status"]["fanOverride"].is_null());
        // Back on AC, the lapsed temporary stays gone and Stop hands the owned
        // change back.
        rig.metadata.store(0, Ordering::Release);
        rig.port.state.lock().unwrap().actual = 15;
        assert!(rig.get().await["status"]["fanOverride"].is_null());
        rig.origin.stop();
        rig.stop().await;
    }

    // An owner recorded on full power hands back at a steady Stop on another
    // external class: the commands are the same, and the rows keep source 0.
    // Under Performance the class pin compares with Stop's own first reading,
    // not the recorded source, so nothing is left pending.
    #[tokio::test]
    async fn classified_owner_hands_back_at_stop_on_unclassified_power() {
        for selector in [0, 2] {
            for unclassified in [true, false] {
                let rig = fan_rig(selector, 0);
                assert_eq!(
                    rig.request(fan_request(1, Some(1), 3700)).await["result"]["status"],
                    1
                );
                assert_eq!(last_source(&rig), 0);
                if unclassified {
                    unclassify(&rig, Form::Rejected(3));
                } else {
                    rig.port.state.lock().unwrap().actual = 10;
                }
                rig.origin.stop();
                rig.stop().await;
                let name = format!("{selector} {unclassified}");
                assert_eq!(fan_rows(&rig).last().unwrap()["progress"], 50, "{name}");
                assert_eq!(last_source(&rig), 0);
                assert_eq!(hardware(&rig).2, [0; 2]);
                assert!(!rig.storage.pending(), "{name}");
            }
        }
    }

    // A return to Automatic that carries the saved Performance preset applies
    // it through managed `SetPerformanceAsync` (`allowBattery: false`):
    // Windows AC applies it, unknown Windows power fails fresh admission, and
    // Windows battery or a battery reading before the first setter refuses it
    // before any setter. Under Windows battery managed checks the request
    // against Battery (`ResolveCurrentPowerSourceAsync`), where a saved
    // Performance override is not valid, so it is rejected before storing.
    #[tokio::test]
    async fn preset_return_to_automatic_needs_windows_ac() {
        for windows in [Some(false), Some(true), None] {
            let rig = handback_rig(0, 15, 2).await;
            rig.port.state.lock().unwrap().windows_on_battery = Some(windows);
            let out = rig.request(fan_request(232, Some(0), 3700)).await;
            if windows == Some(false) {
                assert_eq!(out["result"]["status"], 1, "{out}");
                assert_eq!(hardware(&rig), (6, [2; 2], [0; 2]));
            } else {
                if windows == Some(true) {
                    assert_eq!(out["result"]["status"], 3, "{out}");
                    assert_eq!(
                        out["result"]["message"],
                        "The requested intent was rejected: The performance preset is not valid for this power source."
                    );
                } else {
                    assert_eq!(out["result"]["status"], 2, "{out}");
                    assert!(
                        !out["result"]["message"]
                            .as_str()
                            .unwrap()
                            .ends_with("No applied target is claimed."),
                        "{out}"
                    );
                }
                assert_eq!(
                    rig.files.document("manual-overrides.json")["overrides"]["fans"],
                    1,
                    "{windows:?}"
                );
                assert_eq!(hardware(&rig), (4, [0; 2], [1; 2]), "{windows:?}");
                assert!(request_rows(&rig, 232).is_empty(), "{windows:?}");
                assert!(!rig.storage.pending());
                assert!(rig.storage.outstanding_fan());
            }
            rig.origin.stop();
            let drained = rig.source.lock().await.drain().await;
            // Owned Stop hands back unchecked under Windows battery; unknown
            // Windows power admits no fan command.
            assert_eq!(
                drained.is_ok(),
                windows.is_some(),
                "{windows:?} {drained:?}"
            );
        }
        // The rejection reserves no setter or cleanup IDs: a later request on
        // Windows AC starts its IDs after only the rejected request's own
        // preflight. The rejected request stored nothing, so the later one
        // still leaves saved manual intent and carries the preset.
        let first_ids = |rig: &Rig, n| request_rows(rig, n)[0]["fan"]["ids"][0][0].clone();
        let rig = handback_rig(0, 15, 2).await;
        assert_eq!(
            rig.request(fan_request(232, Some(0), 3700)).await["result"]["status"],
            1
        );
        let unrefused = first_ids(&rig, 232).as_u64().unwrap();
        rig.origin.stop();
        let rig = handback_rig(0, 15, 2).await;
        rig.port.state.lock().unwrap().windows_on_battery = Some(Some(true));
        assert_eq!(
            rig.request(fan_request(232, Some(0), 3700)).await["result"]["status"],
            3
        );
        rig.port.state.lock().unwrap().windows_on_battery = Some(Some(false));
        let out = rig.request(fan_request(233, Some(0), 3700)).await;
        assert_eq!(out["result"]["status"], 1, "{out}");
        assert_eq!(hardware(&rig), (6, [2; 2], [0; 2]));
        assert_eq!(first_ids(&rig, 233), unrefused + 4);
        rig.origin.stop();
        // Windows battery at the first setter's own reading: once the preset
        // needs AC, no reading of the operation is admitted unchecked, and the
        // prepared request is retired without a setter.
        let rig = handback_rig(0, 15, 2).await;
        let reads = power_reads(&rig);
        rig.port.state.lock().unwrap().change_windows = Some((reads + 2, Some(true)));
        let out = rig.request(fan_request(232, Some(0), 3700)).await;
        assert_eq!(out["result"]["status"], 2, "{out}");
        assert_eq!(writes(&rig), 4);
        let progress: Vec<_> = request_rows(&rig, 232)
            .iter()
            .map(|r| r["progress"].clone())
            .collect();
        assert_eq!(progress, [0, 6]);
        rig.origin.stop();
    }

    // A target-only change under manual fans writes no state pair, so managed
    // has no acPreset to pin it with: under Performance, as under Balanced, it
    // continues across a class change between its setters.
    #[tokio::test]
    async fn retarget_continues_across_a_class_change() {
        type Change = fn(&mut Port, usize);
        let changes: [(&str, Change); 2] = [
            ("limited", |s, at| s.change_power = Some((at, 10))),
            ("unclassified", |s, at| {
                s.change_power_status = Some((at, 5))
            }),
        ];
        for selector in [2, 0] {
            for (name, change) in changes {
                let rig = fan_rig(selector, 0);
                assert_eq!(
                    rig.request(fan_request(1, Some(1), 2600)).await["result"]["status"],
                    1
                );
                let before = writes(&rig);
                let reads = power_reads(&rig);
                change(&mut rig.port.state.lock().unwrap(), reads + 3);
                let out = rig.request(fan_request(2, Some(1), 3700)).await;
                assert_eq!(out["result"]["status"], 1, "{selector} {name} {out}");
                assert_eq!(writes(&rig) - before, 2, "{selector} {name}");
                assert_eq!(fan_rows(&rig).last().unwrap()["progress"], 30);
                rig.origin.stop();
                rig.stop().await;
            }
        }
    }

    // Owned Stop hands back on the owner's class; unclassified needs presence,
    // and without it Stop records and writes nothing.
    #[tokio::test]
    async fn owned_stop_hands_back_on_unclassified_power_with_presence() {
        for presence in [true, false] {
            let rig = unclassified_fan_rig(0, 0, Form::Rejected(3));
            assert_eq!(
                rig.request(fan_request(1, Some(1), 3700)).await["result"]["status"],
                1
            );
            let owner = fan_rows(&rig).last().unwrap().clone();
            assert_eq!(owner["progress"], 30);
            if !presence {
                rig.port.state.lock().unwrap().windows_on_battery = Some(None);
            }
            rig.origin.stop();
            let drained = rig.source.lock().await.drain().await;
            assert_eq!(drained.is_ok(), presence, "{drained:?}");
            assert_eq!(!rig.storage.outstanding_fan(), presence);
            assert_eq!(hardware(&rig).2, if presence { [0; 2] } else { [1; 2] });
            if !presence {
                assert_eq!(*fan_rows(&rig).last().unwrap(), owner);
                assert!(!rig.storage.pending());
            }
        }
    }

    // Owned Stop re-reads 07/8C before each setter: a battery reading while
    // Windows still reports AC stops it, and so does a class change under the
    // Performance selector, which carries no preset (managed acPreset pin).
    // Under Balanced a change between external classes completes.
    #[tokio::test]
    async fn owned_stop_checks_every_setter_reading() {
        for (selector, actual, completes) in [(0, 0, false), (2, 10, false), (0, 10, true)] {
            let rig = fan_rig(selector, 0);
            rig.port.state.lock().unwrap().windows_on_battery = Some(Some(false));
            assert_eq!(
                rig.request(fan_request(1, Some(1), 3700)).await["result"]["status"],
                1
            );
            let before = writes(&rig);
            let reads = power_reads(&rig);
            rig.port.state.lock().unwrap().change_power = Some((reads + 3, actual));
            rig.origin.stop();
            let drained = rig.source.lock().await.drain().await;
            assert_eq!(
                drained.is_ok(),
                completes,
                "{selector} {actual} {drained:?}"
            );
            if completes {
                assert_eq!(writes(&rig) - before, 2);
                assert_eq!(fan_rows(&rig).last().unwrap()["progress"], 50);
                assert_eq!(hardware(&rig).2, [0; 2]);
            } else {
                assert_eq!(writes(&rig) - before, 1, "{selector}");
                assert_eq!(fan_rows(&rig).last().unwrap()["progress"], 41, "{selector}");
                assert!(rig.storage.outstanding_fan(), "{selector}");
            }
        }
    }
}

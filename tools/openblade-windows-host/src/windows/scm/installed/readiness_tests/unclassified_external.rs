//! Ordinary thermal setters on unclassified external power (managed f6502f85):
//! fresh Windows AC presence with a completed 07/8C query that classifies
//! nothing. No profile applies there, every setter needs its own fresh reading
//! in the first class, and battery policy keeps its classified-only admission.
use super::*;

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

// Managed admits Unknown only with fresh Windows external presence.
#[tokio::test]
async fn unclassified_power_without_windows_presence_stores_and_writes_nothing() {
    for form in [Form::Rejected(3), Form::Levels(15, 0x10)] {
        for windows in [Some(true), None] {
            let rig = unclassified_rig(form);
            rig.port.state.lock().unwrap().windows_on_battery = Some(windows);
            let out = rig.set(1, value(2, 1)).await;
            assert_eq!(out["result"]["status"], 2, "{form:?} {out}");
            assert_eq!(writes(&rig), 0);
            assert!(rig.files.read("manual-overrides.json").unwrap().is_none());
            rig.stop().await;
        }
    }
}

// Each setter re-reads 07/8C (reads 2 and 3 of the pair). A refinement either
// way, or lost presence, stops before the next command; nothing is replayed.
#[tokio::test]
async fn class_or_presence_change_between_setters_stops_without_replay() {
    type Change = fn(&mut crate::discovery::performance::mutation_fixture::State);
    let cases: [(&str, Change); 5] = [
        ("unclassified to full", |s| {
            s.power_status = 3;
            s.change_power_status = Some((3, 0));
        }),
        ("unclassified to limited", |s| {
            (s.actual, s.recommended) = (0x10, 15);
            s.change_power = Some((3, 10));
        }),
        ("full to unclassified", |s| {
            s.change_power_status = Some((3, 5))
        }),
        ("limited to unclassified", |s| {
            s.actual = 10;
            s.change_power = Some((3, 0x10));
        }),
        ("presence lost", |s| {
            s.power_status = 3;
            s.change_windows = Some((3, Some(true)));
        }),
    ];
    for (name, change) in cases {
        let rig = Rig::new(MemoryFiles::default());
        change(&mut rig.port.state.lock().unwrap());
        let out = rig.set(2, value(2, 1)).await;
        assert_eq!(out["result"]["status"], 2, "{name} {out}");
        // A setter may already be submitted, so reconciliation owns the reply.
        assert_eq!(out["result"]["message"], PENDING, "{name}");
        assert_eq!(writes(&rig), 1, "{name}");
        assert_eq!(power_reads(&rig), 3, "{name}");
        // The first setter's acknowledgement is the last durable stage.
        assert_eq!(
            Storage::open(Box::new(rig.files.clone()), SID)
                .unwrap()
                .progress(),
            Some(2),
            "{name}"
        );
        rig.stop().await;
    }
}

// Managed rejects native overclocking on unclassified power before storing it.
#[tokio::test]
async fn unclassified_power_rejects_gpu_tuning_before_any_intent() {
    let rig = unclassified_rig(Form::Rejected(3));
    let mut tuned = value(2, 1);
    tuned["gpuClockTuning"] =
        json!({"enabled":true,"offsets":{"coreMegahertz":50,"memoryMegahertz":0}});
    let out = rig.set(1, tuned).await;
    assert_eq!(out["result"]["status"], 3, "{out}");
    assert_eq!(
        out["result"]["message"],
        "The requested intent was rejected: This request needs classified power or includes native overclocking; ordinary external presence does not admit it."
    );
    assert_eq!(writes(&rig), 0);
    assert!(rig.files.read("manual-overrides.json").unwrap().is_none());
    rig.stop().await;
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
// saved on, and battery intent is retired once AC arrives unclassified.
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
        storage
            .desired(power, None)
            .unwrap()
            .map(|(p, s)| (p as i32, s))
    };
    storage.save(Some(temporary(2)), 0).unwrap();
    assert_eq!(desired(3), Some((2, 3)));
    assert_eq!(desired(0), Some((2, 3)));
    // Saved unclassified, classified later: native cannot prove managed's
    // same-generation preservation, so the class retires it.
    storage.save(Some(temporary(2)), 3).unwrap();
    assert_eq!(desired(3), Some((2, 3)));
    assert_eq!(desired(0), Some((1, 1)));
    assert_eq!(desired(3), None);
    // Saved on battery (no AC preset is eligible there, so seed it directly
    // through a classified save first): unclassified AC retires it.
    storage.save(Some(temporary(2)), 1).unwrap();
    assert!(storage.current_override(1).unwrap().is_some());
    assert!(storage.current_override(3).unwrap().is_none());
    assert_eq!(desired(3), None);
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

    // A fan setter's fresh reading must match the first class too.
    #[tokio::test]
    async fn fan_setters_stop_on_a_class_change_without_replay() {
        for unclassified_first in [true, false] {
            let rig = fan_rig(0, 0);
            let reads = power_reads(&rig);
            {
                let mut s = rig.port.state.lock().unwrap();
                if unclassified_first {
                    // Unclassified, then classified full before the second setter.
                    s.power_status = 3;
                    s.change_power_status = Some((reads + 3, 0));
                } else {
                    // Full, then a rejection before the second setter.
                    s.change_power_status = Some((reads + 3, 5));
                }
            }
            let out = rig.request(fan_request(1, Some(1), 3700)).await;
            assert_eq!(out["result"]["status"], 2, "{unclassified_first} {out}");
            assert_eq!(writes(&rig), 1, "{unclassified_first}");
            assert_eq!(fan_rows(&rig).last().unwrap()["progress"], 11);
            rig.origin.stop();
            // The partial submission stays fenced for read-only reconciliation.
            assert!(rig.source.lock().await.drain().await.is_err());
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

    // Owned Stop hands back on the owner's class; unclassified needs presence.
    #[tokio::test]
    async fn owned_stop_hands_back_on_unclassified_power_with_presence() {
        for presence in [true, false] {
            let rig = unclassified_fan_rig(0, 0, Form::Rejected(3));
            assert_eq!(
                rig.request(fan_request(1, Some(1), 3700)).await["result"]["status"],
                1
            );
            if !presence {
                rig.port.state.lock().unwrap().windows_on_battery = Some(None);
            }
            rig.origin.stop();
            let drained = rig.source.lock().await.drain().await;
            assert_eq!(drained.is_ok(), presence, "{drained:?}");
            assert_eq!(!rig.storage.outstanding_fan(), presence);
            assert_eq!(hardware(&rig).2, if presence { [0; 2] } else { [1; 2] });
        }
    }
}

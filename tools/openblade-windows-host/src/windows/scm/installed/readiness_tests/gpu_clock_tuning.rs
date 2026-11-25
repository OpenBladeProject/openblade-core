//! GPU clock tuning on `setPerformanceOverride` through the installed
//! dispatcher (`thermal_gpu`): the paired write's NVML preflight before any
//! firmware row and its submission after the independent readback, the
//! GPU-only setter, a refused preflight, an NVML failure mid-pair, an
//! uncertain submission that stops further writes until a read-only
//! resolution, and the resume and power-change boundaries. Every NVML call
//! is scripted (`gpu_clock_offsets::fake`).
use super::*;
use crate::installed::readiness::thermal_gpu::CALL_BUDGET;
use crate::installed::readiness::thermal_mutation::{PENDING_OTHER, PENDING_PERFORMANCE};
use crate::installed::readiness::{
    gpu_clock_offsets::{Management, RZ09_0581, Worker, fake::Fake},
    thermal_gpu::{Backend, Lane},
};
use std::sync::atomic::AtomicU64;

const FULL: u8 = 15;
const STORED: &str = "Performance override intent was stored. Hardware result: ";

/// A rig at `selector` with Automatic fans on a full adapter, its GPU
/// clock tuning over `fake` with an idle dGPU, its gate generation and its
/// Windows power generation read from the returned counters.
fn rig_at(selector: u8, fake: &Fake) -> (Rig, Arc<AtomicU64>, Arc<AtomicU64>) {
    let (rig, _, gate, power) = rig_with(MemoryFiles::default(), selector, fake, Arc::default());
    (rig, gate, power)
}

/// `rig_at` over `files`, returning its lane too; the dGPU activity read
/// sleeps `slow` ms once when it is set.
fn rig_with(
    files: MemoryFiles,
    selector: u8,
    fake: &Fake,
    slow: Arc<AtomicU64>,
) -> (Rig, Arc<Lane>, Arc<AtomicU64>, Arc<AtomicU64>) {
    let rig = Rig::new(files);
    {
        let mut s = rig.port.state.lock().unwrap();
        s.selectors = [selector; 2];
        s.fans = [0; 2];
        s.actual = FULL;
    }
    let driver = fake.clone();
    let worker = Worker::spawn(move || {
        Management::new(
            Box::new(driver) as Backend,
            Box::new(move || {
                let ms = slow.swap(0, Ordering::AcqRel);
                if ms > 0 {
                    std::thread::sleep(Duration::from_millis(ms));
                }
                Some(false)
            }),
            RZ09_0581,
        )
    })
    .unwrap();
    let lane = Arc::new(Lane::new(worker));
    rig.runtime.compose_gpu(lane.clone());
    let gate = Arc::new(AtomicU64::new(0));
    let power = Arc::new(AtomicU64::new(1));
    let (g, p) = (gate.clone(), power.clone());
    rig.runtime.read_low_power(move || {
        Some(openblade_domain::low_power_gate::Transition {
            generation: g.load(Ordering::Acquire),
            timestamp: 0,
        })
    });
    rig.runtime
        .read_power_generation(move || Some(p.load(Ordering::Acquire)));
    (rig, lane, gate, power)
}

fn tuned(preset: i32, enabled: bool, core: i32, memory: i32) -> Value {
    let mut request = value(preset, 1);
    let offsets = json!({"coreMegahertz":core,"memoryMegahertz":memory});
    request["gpuClockTuning"] = json!({
        "enabled": enabled,
        "offsets": offsets,
        "effectiveOffsets": if enabled {
            offsets.clone()
        } else {
            json!({"coreMegahertz":0,"memoryMegahertz":0})
        },
    });
    request
}

fn writes(rig: &Rig) -> usize {
    rig.port.state.lock().unwrap().writes
}

fn message(reply: &Value) -> &str {
    reply["result"]["message"].as_str().unwrap()
}

#[tokio::test]
async fn a_paired_write_submits_the_offsets_after_the_confirmed_preset() {
    let fake = Fake::new(0, 0);
    let (rig, _, _) = rig_at(0, &fake);
    let reply = rig.set(1, tuned(2, true, 125, 500)).await;
    assert_eq!(reply["result"]["status"], 1, "{reply}");
    assert_eq!(
        message(&reply),
        "Bounded thermal preset and GPU clock tuning confirmed by independent readback. Broad device capabilities remain unavailable; other native controls are incomplete."
    );
    assert_eq!(rig.port.state.lock().unwrap().selectors, [2; 2]);
    assert_eq!(fake.current(), (125, 500));
    // One preflight query, and the core and memory writes.
    assert_eq!(fake.sets(), 2);
    rig.stop().await;
}

#[tokio::test]
async fn a_confirmed_preset_takes_the_gpu_only_setter_and_a_match_writes_nothing() {
    let fake = Fake::new(0, 0);
    let (rig, _, _) = rig_at(2, &fake);
    let reply = rig.set(1, tuned(2, true, 125, 500)).await;
    assert_eq!(reply["result"]["status"], 1, "{reply}");
    assert_eq!(writes(&rig), 0);
    assert_eq!(fake.current(), (125, 500));
    let sets = fake.sets();
    // Already confirmed: neither firmware nor NVML is written.
    let reply = rig.set(2, tuned(2, true, 125, 500)).await;
    assert_eq!(reply["result"]["status"], 1, "{reply}");
    assert_eq!((writes(&rig), fake.sets()), (0, sets));
    rig.stop().await;
}

#[tokio::test]
async fn no_tuning_relinquishes_and_writes_no_offsets() {
    let fake = Fake::new(50, 50);
    let (rig, _, _) = rig_at(0, &fake);
    let reply = rig.set(1, value(2, 1)).await;
    assert_eq!(reply["result"]["status"], 1, "{reply}");
    assert_eq!(
        message(&reply),
        "Bounded thermal preset confirmed by independent paired readback. Broad device capabilities remain unavailable; other native controls are incomplete."
    );
    // The driver keeps what it holds: nothing restores it.
    assert_eq!(
        (fake.sets(), fake.inits(), fake.current()),
        (0, 0, (50, 50))
    );
    rig.stop().await;
}

#[tokio::test]
async fn a_refused_preflight_stores_the_intent_and_writes_nothing() {
    let fake = Fake::new(0, 0);
    let (rig, _, _) = rig_at(0, &fake);
    // Inside the contract, outside the RZ09-0581 envelope.
    let reply = rig.set(1, tuned(2, true, 401, 0)).await;
    assert_eq!(reply["result"]["status"], 2, "{reply}");
    assert_eq!(
        message(&reply),
        format!(
            "{STORED}Performance is unavailable: The requested GPU clock offsets are outside the ranges reported by the NVIDIA driver."
        )
    );
    assert_eq!((writes(&rig), fake.sets()), (0, 0));
    assert!(
        rig.files
            .read("thermal-mutation.journal")
            .unwrap()
            .is_none()
    );
    let saved: Value =
        serde_json::from_slice(&rig.files.read("manual-overrides.json").unwrap().unwrap()).unwrap();
    assert_eq!(
        saved["overrides"]["gpuClockTuning"]["offsets"]["coreMegahertz"],
        401
    );
    rig.stop().await;
}

#[tokio::test]
async fn a_refused_write_admission_stores_nothing_and_calls_no_nvml() {
    let fake = Fake::new(0, 0);
    let (rig, _, _) = rig_at(0, &fake);
    rig.custody.activation_refusal.store(1, Ordering::Release);
    let reply = rig.set(2, tuned(0, true, 200, 600)).await;
    assert_eq!(reply["result"]["status"], 2, "{reply}");
    assert_eq!((writes(&rig), fake.inits(), fake.sets()), (0, 0, 0));
    assert!(rig.files.read("manual-overrides.json").unwrap().is_none());
    rig.stop().await;
}

/// Makes the NVML submission fail after its core write, with a failing
/// restore, from the first firmware write on: after the preflight, before
/// the submission. `lost` also makes the driver unreadable then.
fn fail_after_firmware(rig: &Rig, fake: &Fake, lost: bool) {
    let mut state = rig.port.state.lock().unwrap();
    let previous = state.before_write.take().unwrap();
    let fake = fake.clone();
    state.before_write = Some(Arc::new(move |number| {
        previous(number);
        fake.with(|d| {
            d.sets_fail = true;
            d.storing_failures = 1;
            d.lose_on_set = lost;
        });
    }));
}

#[tokio::test]
async fn an_nvml_failure_mid_pair_is_indeterminate_and_is_not_replayed() {
    let fake = Fake::new(0, 0);
    let (rig, _, _) = rig_at(0, &fake);
    fail_after_firmware(&rig, &fake, false);
    let reply = rig.set(1, tuned(2, true, 125, 500)).await;
    assert_eq!(reply["result"]["status"], 3, "{reply}");
    let message = message(&reply);
    assert!(
        message.starts_with(&format!(
            "{STORED}Performance write is indeterminate: The power mode was applied, but GPU clock tuning failed. "
        )),
        "{message}"
    );
    assert!(
        message.ends_with("Fresh readback did not match the requested or prior performance state."),
        "{message}"
    );
    // The firmware half stands; the core write and its failed restore are
    // the only NVML writes.
    assert_eq!(rig.port.state.lock().unwrap().selectors, [2; 2]);
    assert_eq!(fake.sets(), 2);
    assert_eq!(fake.current(), (125, 0));
    rig.stop().await;
}

#[tokio::test]
async fn an_uncertain_submission_stops_writes_until_a_read_only_resolution() {
    let fake = Fake::new(0, 0);
    let (rig, _, power) = rig_at(0, &fake);
    fail_after_firmware(&rig, &fake, true);
    let reply = rig.set(1, tuned(2, true, 125, 500)).await;
    assert_eq!(reply["result"]["status"], 3, "{reply}");
    assert!(
        message(&reply).ends_with("Fresh readback cannot confirm the GPU clock offsets."),
        "{reply}"
    );
    fake.with(|d| {
        d.sets_fail = false;
        d.queries_fail = false;
        d.lose_on_set = false;
    });
    let (firmware, sets, inits) = (writes(&rig), fake.sets(), fake.inits());
    // The dGPU is idle and the ungated query was spent: every next step is
    // refused before any firmware row, with or without tuning.
    for (n, request) in [(2, tuned(0, true, 125, 500)), (3, value(0, 1))] {
        let reply = rig.set(n, request).await;
        assert_eq!(reply["result"]["status"], 2, "{reply}");
        assert_eq!(
            message(&reply),
            format!(
                "{STORED}A thermal write requires read-only reconciliation before the performance command can be submitted."
            )
        );
    }
    assert_eq!(
        (writes(&rig), fake.sets(), fake.inits()),
        (firmware, sets, inits)
    );
    // A power change allows one more read-only query, which resolves it:
    // the core landed, so the confirmed preset takes the GPU-only setter.
    power.fetch_add(1, Ordering::AcqRel);
    let reply = rig.set(4, tuned(2, true, 125, 500)).await;
    assert_eq!(reply["result"]["status"], 1, "{reply}");
    assert_eq!(writes(&rig), firmware);
    assert_eq!(fake.current(), (125, 500));
    rig.stop().await;
}

#[tokio::test]
async fn a_resume_withdraws_a_confirmed_reset() {
    let fake = Fake::new(0, 0);
    let (rig, gate, _) = rig_at(0, &fake);
    // A paired disable submits the reset.
    let reply = rig.set(1, tuned(2, false, 125, 500)).await;
    assert_eq!(reply["result"]["status"], 1, "{reply}");
    let (sets, inits) = (fake.sets(), fake.inits());
    // The next paired disable reuses it: no query and no write.
    let reply = rig.set(2, tuned(0, false, 125, 500)).await;
    assert_eq!(reply["result"]["status"], 1, "{reply}");
    assert_eq!((fake.sets(), fake.inits()), (sets, inits));
    // After a suspend and resume it is not evidence: the paired disable
    // queries and submits the reset again.
    gate.fetch_add(2, Ordering::AcqRel);
    let reply = rig.set(3, tuned(2, false, 125, 500)).await;
    assert_eq!(reply["result"]["status"], 1, "{reply}");
    assert_eq!(fake.inits(), inits + 2);
    assert_eq!(fake.sets(), sets + 2);
    rig.stop().await;
}

/// Write admission lost after the independent firmware readback, before the
/// NVML submission: the submission is not made and the paired write is
/// indeterminate. Found by refusing each successive admission in turn.
#[tokio::test]
async fn admission_lost_before_the_submission_submits_no_offsets() {
    let mut found = false;
    for n in 1..=200 {
        let fake = Fake::new(0, 0);
        let (rig, _, _) = rig_at(0, &fake);
        rig.custody.read_refuse_at.store(n, Ordering::Release);
        let reply = rig.set(1, tuned(2, true, 125, 500)).await;
        let landed = rig.port.state.lock().unwrap().selectors == [2; 2];
        let refused = reply["result"]["message"].as_str().is_some_and(|m| {
            m.contains("GPU clock tuning lost its write admission before it was submitted.")
        });
        rig.stop().await;
        if landed && refused {
            assert_eq!(reply["result"]["status"], 3, "{reply}");
            assert_eq!(fake.sets(), 0);
            assert_eq!(fake.current(), (0, 0));
            found = true;
            break;
        }
        // Any other refusal point submits no offsets either.
        assert!(
            fake.sets() == 0 || fake.current() == (125, 500),
            "{n}: {reply}"
        );
    }
    assert!(found);
}

/// A firmware half its independent readback does not confirm submits no
/// offsets: only the preflight reached NVML.
#[tokio::test]
async fn an_unconfirmed_firmware_half_submits_no_offsets() {
    for timeout in [false, true] {
        let fake = Fake::new(0, 0);
        let (rig, _, _) = rig_at(0, &fake);
        {
            let mut state = rig.port.state.lock().unwrap();
            if timeout {
                state.final_read_timeout = true;
            } else {
                state.mismatch = true;
            }
        }
        let reply = rig.set(1, tuned(2, true, 125, 500)).await;
        assert_ne!(reply["result"]["status"], 1, "{reply}");
        assert!(writes(&rig) > 0, "{reply}");
        assert_eq!((fake.sets(), fake.current()), (0, (0, 0)), "{reply}");
        rig.stop().await;
    }
}

/// Leaves the lane's management with an unconfirmable submission on a
/// driver that stays unreadable, so its fence's read cannot resolve it.
fn leave_uncertain(lane: &Lane, fake: &Fake) {
    fake.with(|d| {
        d.sets_fail = true;
        d.storing_failures = 1;
        d.lose_on_set = true;
    });
    let _ = lane.call_for_tests(|m| {
        let _ = m.set(
            openblade_domain::profile_set::model::GpuClockOffsets {
                core_megahertz: 125,
                memory_megahertz: 500,
            },
            true,
        );
    });
    fake.with(|d| {
        d.sets_fail = false;
        d.lose_on_set = false;
    });
    assert!(lane.fenced(CALL_BUDGET));
}

async fn report(rig: &Rig, sequence: i64, path: Option<&str>) -> Value {
    rig.request(json!({"operation":"reportApplicationContext","version":90,"requestId":format!("bbbbbbbb-bbbb-cccc-dddd-{sequence:012}"),"instanceId":"cccccccc-bbbb-cccc-dddd-000000000003","sequence":sequence,"executablePath":path})).await
}

/// Review F-A: the GPU half is bounded by the request. A submission that
/// outlasts it is replied Failed and indeterminate before the deadline, and
/// fences every thermal writer until it ends on the worker. Before the fix
/// no reply arrived and the offsets landed after the request was dropped.
#[tokio::test]
async fn a_gpu_submission_outlasting_its_request_replies_failed_and_fences_writes() {
    keep_real_clock();
    let fake = Fake::new(0, 0);
    let slow = Arc::new(AtomicU64::new(0));
    let (rig, lane, _, _) = rig_with(MemoryFiles::default(), 0, &fake, slow.clone());
    let reply = rig.set(1, tuned(2, true, 125, 500)).await;
    assert_eq!(reply["result"]["status"], 1, "{reply}");
    // The preflight's activity read takes 1.3 s and the submission blocks
    // in the driver.
    slow.store(1300, Ordering::Release);
    let (release, hold) = std::sync::mpsc::channel();
    fake.with(|d| d.hold = Some(hold));
    let started = std::time::Instant::now();
    let reply = rig.set(2, tuned(0, true, 200, 600)).await;
    assert!(started.elapsed() < Duration::from_secs(6), "{reply}");
    assert_eq!(reply["result"]["status"], 3, "{reply}");
    assert_eq!(
        message(&reply),
        format!(
            "{STORED}Performance write is indeterminate: The power mode was applied, but GPU clock tuning failed. GPU clock tuning did not finish within its budget."
        )
    );
    assert_eq!(rig.port.state.lock().unwrap().selectors, [5; 2]);
    // While it runs no thermal writer reaches firmware.
    assert!(lane.fenced(Duration::ZERO));
    let firmware = writes(&rig);
    let reply = rig.set(3, value(2, 1)).await;
    assert_eq!(reply["result"]["status"], 2, "{reply}");
    assert_eq!(message(&reply), format!("{STORED}{PENDING_PERFORMANCE}"));
    assert_eq!(writes(&rig), firmware);
    release.send(()).unwrap();
    let end = std::time::Instant::now() + Duration::from_secs(5);
    while lane.fenced(Duration::ZERO) && std::time::Instant::now() < end {
        std::thread::sleep(Duration::from_millis(10));
    }
    // It ended on the worker, which recorded what the driver holds.
    assert!(!lane.fenced(CALL_BUDGET));
    assert_eq!(fake.current(), (200, 600));
    let reply = rig.set(4, tuned(2, true, 200, 600)).await;
    assert_eq!(reply["result"]["status"], 1, "{reply}");
    assert_eq!(rig.port.state.lock().unwrap().selectors, [2; 2]);
    rig.stop().await;
}

/// Review F-B: an uncertain GPU submission fences the fan writers too,
/// with managed's `ScopedThermalWriteBlockReason`: a manual fan request and
/// a return to Automatic that carries the saved preset store their intent
/// and write nothing. Before the fix both wrote firmware.
#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn an_uncertain_gpu_submission_fences_fan_writes() {
    let fake = Fake::new(0, 0);
    let files = MemoryFiles::default();
    files.profiles_with(2, Some(2));
    let (rig, _, _, _) = rig_with(files, 0, &fake, Arc::default());
    *rig.custody.origin_for_fan.lock().unwrap() = Some(rig.origin.clone());
    rig.get().await;
    fail_after_firmware(&rig, &fake, true);
    let reply = rig.set(1, tuned(2, true, 125, 500)).await;
    assert!(
        message(&reply).ends_with("Fresh readback cannot confirm the GPU clock offsets."),
        "{reply}"
    );
    fake.with(|d| {
        d.sets_fail = false;
        d.queries_fail = false;
        d.lose_on_set = false;
    });
    // The saved preset becomes Silent; its step is refused.
    let reply = rig.set(3, value(0, 1)).await;
    assert_eq!(message(&reply), format!("{STORED}{PENDING_PERFORMANCE}"));
    let before = (writes(&rig), rig.port.state.lock().unwrap().fans);
    let manual = rig.request(fan_request(10, Some(1), 3700)).await;
    assert_eq!(manual["result"]["status"], 2, "{manual}");
    assert_eq!(
        manual["result"]["message"],
        format!("Fan override intent was stored. Hardware result: {PENDING_OTHER}")
    );
    let back = rig.request(fan_request(11, None, 0)).await;
    assert_eq!(back["result"]["status"], 2, "{back}");
    assert_eq!(
        back["result"]["message"],
        format!("Fan override was cleared. Hardware result: {PENDING_OTHER}")
    );
    let after = {
        let s = rig.port.state.lock().unwrap();
        (s.writes, s.fans, s.selectors)
    };
    assert_eq!((after.0, after.1), before);
    assert_eq!(after.2, [2; 2]);
    rig.stop().await;
}

/// A request-driven reconciliation is fenced as every other thermal writer
/// is: the changed report's steps are refused with nothing written, and the
/// writers are released once the submission is resolved read-only.
#[tokio::test]
async fn an_uncertain_gpu_submission_fences_a_trigger_reconciliation() {
    let fake = Fake::new(0, 0);
    let files = MemoryFiles::default();
    files.profiles(1);
    let (rig, lane, _, power) = rig_with(files, 0, &fake, Arc::default());
    // The lane has seen this power generation, as after an earlier step.
    let seen = crate::installed::readiness::thermal_gpu::Boundary {
        gate: Some(0),
        power: Some(1),
        source: None,
    };
    lane.prepare(seen, None, true, CALL_BUDGET);
    leave_uncertain(&lane, &fake);
    let refused = report(&rig, 1, Some(r"C:\Games\Game.exe")).await;
    assert!(
        refused["result"]["message"]
            .as_str()
            .unwrap()
            .contains(PENDING_OTHER),
        "{refused}"
    );
    assert_ne!(refused["result"]["status"], 1, "{refused}");
    assert_eq!(writes(&rig), 0);
    assert_eq!(rig.port.state.lock().unwrap().selectors, [0; 2]);
    // A power change rearms one query; the driver reads again and the next
    // explicit step resolves it read-only, releasing the writers.
    fake.with(|d| d.queries_fail = false);
    power.fetch_add(1, Ordering::AcqRel);
    let reply = rig.set(2, tuned(2, true, 125, 0)).await;
    assert_eq!(reply["result"]["status"], 1, "{reply}");
    assert!(!lane.fenced(CALL_BUDGET));
    rig.stop().await;
}

/// Owned Stop's handback to firmware Automatic is fenced too (managed
/// blocks prior-authority cleanup while a confirmation is pending): the
/// fans stay as OpenBlade left them and nothing is written.
#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn an_uncertain_gpu_submission_fences_the_owned_stop_handback() {
    for composed in [true, false] {
        let fake = Fake::new(0, 0);
        let (rig, lane, _, _) = rig_with(MemoryFiles::default(), 0, &fake, Arc::default());
        rig.custody.scoped_fresh.store(true, Ordering::Release);
        *rig.custody.origin_for_fan.lock().unwrap() = Some(rig.origin.clone());
        rig.get().await;
        let manual = rig.request(fan_request(10, Some(1), 3700)).await;
        assert_eq!(manual["result"]["status"], 1, "{manual}");
        if composed {
            let custody = rig.custody.clone();
            rig.runtime.read_cleanup(move |passed, scope| {
                CleanupFresh::capture(custody.clone(), passed, scope)
            });
        }
        leave_uncertain(&lane, &fake);
        let firmware = writes(&rig);
        rig.origin.stop();
        let _ = rig.source.lock().await.drain().await;
        let state = rig.port.state.lock().unwrap();
        assert_eq!((state.writes, state.fans), (firmware, [1, 1]), "{composed}");
    }
}

/// A profile save is a reconciliation, and a reconciliation leaves every
/// GPU clock effect refused as incomplete: saving an active rule that
/// carries GPU clock tuning writes neither firmware nor NVML.
#[tokio::test]
async fn a_profile_save_with_gpu_clock_tuning_is_refused_as_incomplete() {
    let fake = Fake::new(0, 0);
    let files = MemoryFiles::default();
    files.profiles(1);
    let (rig, _, _, _) = rig_with(files, 0, &fake, Arc::default());
    let applied = report(&rig, 1, Some(r"C:\Games\Game.exe")).await;
    assert_eq!(applied["result"]["status"], 1, "{applied}");
    let (firmware, inits) = (writes(&rig), fake.inits());
    let mut profiles = rig.files.document("profiles.json")["profiles"].clone();
    let game = &mut profiles["applicationProfiles"][0]["overrides"]["pluggedIn"];
    game["performance"] = json!(0);
    game["gpuClockTuning"] =
        json!({"enabled":true,"offsets":{"coreMegahertz":125,"memoryMegahertz":500}});
    let saved = rig
        .request(json!({"operation":"updateProfiles","version":90,"requestId":"aaaaaaaa-bbbb-cccc-dddd-000000000009","profiles":profiles}))
        .await;
    assert!(
        saved["result"]["message"]
            .as_str()
            .unwrap()
            .contains("GPU clock handling is incomplete; no thermal setter was submitted."),
        "{saved}"
    );
    assert_eq!(
        (writes(&rig), fake.inits(), fake.sets()),
        (firmware, inits, 0)
    );
    rig.stop().await;
}

/// A paired step's pace keeps time for its NVML submission
/// (`Permit::reserve`). At this fixture exchange pace the firmware recipe
/// alone fits the request, but not with the submission's 1 s reserve: the
/// paired step is refused before any row or setter, with nothing written.
#[tokio::test]
async fn a_paired_step_without_time_for_its_submission_writes_nothing() {
    for paired in [false, true] {
        let fake = Fake::new(0, 0);
        let (rig, _, _) = rig_at(0, &fake);
        rig.port.state.lock().unwrap().latency = Some(Duration::from_millis(285));
        let request = if paired {
            tuned(2, true, 125, 500)
        } else {
            value(2, 1)
        };
        let reply = rig.set(1, request).await;
        rig.port.state.lock().unwrap().latency = None;
        if paired {
            assert_eq!(reply["result"]["status"], 2, "{reply}");
            assert_eq!(
                message(&reply),
                format!(
                    "{STORED}The remaining request time cannot cover this recipe at the measured device pace; no thermal setter was submitted."
                )
            );
            assert_eq!((writes(&rig), fake.sets()), (0, 0));
        } else {
            assert_eq!(reply["result"]["status"], 1, "{reply}");
        }
        rig.stop().await;
    }
}

/// The preflight's own call is bounded by what remains of the request, not
/// by the 5 s call cap: after slow exchanges have spent part of the
/// request, a preflight that outlasts the rest is refused as unresponsive,
/// with nothing written.
#[tokio::test]
async fn a_preflight_outlasting_the_rest_of_its_request_is_refused() {
    let fake = Fake::new(0, 0);
    let slow = Arc::new(AtomicU64::new(0));
    let (rig, _, _, _) = rig_with(MemoryFiles::default(), 0, &fake, slow.clone());
    let reply = rig.set(1, tuned(2, true, 125, 500)).await;
    assert_eq!(reply["result"]["status"], 1, "{reply}");
    let (firmware, sets) = (writes(&rig), fake.sets());
    // The exchanges before the preflight spend fixture time; the preflight's
    // activity read then takes 4 s, under the call cap but past what is
    // left of the request.
    rig.port.state.lock().unwrap().latency = Some(Duration::from_millis(600));
    slow.store(4000, Ordering::Release);
    let reply = rig.set(2, tuned(0, true, 200, 600)).await;
    rig.port.state.lock().unwrap().latency = None;
    assert_eq!(reply["result"]["status"], 2, "{reply}");
    assert_eq!(
        message(&reply),
        format!("{STORED}GPU clock management did not respond; no thermal setter was submitted.")
    );
    assert_eq!((writes(&rig), fake.sets()), (firmware, sets));
    rig.stop().await;
}

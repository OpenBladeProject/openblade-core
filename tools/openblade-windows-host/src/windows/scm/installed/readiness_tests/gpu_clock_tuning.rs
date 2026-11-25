//! GPU clock tuning on `setPerformanceOverride` through the installed
//! dispatcher (`thermal_gpu`): the paired write's NVML preflight before any
//! firmware row and its submission after the independent readback, the
//! GPU-only setter, a refused preflight, an NVML failure mid-pair, an
//! uncertain submission that stops further writes until a read-only
//! resolution, and the resume and power-change boundaries. Every NVML call
//! is scripted (`gpu_clock_offsets::fake`).
use super::*;
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
    let rig = Rig::new(MemoryFiles::default());
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
            Box::new(|| Some(false)),
            RZ09_0581,
        )
    })
    .unwrap();
    rig.runtime.compose_gpu(Arc::new(Lane::new(worker)));
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
    (rig, gate, power)
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
    let reply = rig.set(1, tuned(2, true, 125, 500)).await;
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
    assert_eq!(reply["result"]["status"], 2, "{reply}");
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
    assert_eq!(reply["result"]["status"], 2, "{reply}");
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
            assert_eq!(reply["result"]["status"], 2, "{reply}");
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

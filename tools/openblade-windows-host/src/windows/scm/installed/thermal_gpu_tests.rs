//! The GPU half of a performance step over a scripted driver: what each
//! decision writes, and how a failed or uncertain submission is judged
//! read-only and never replayed. The managed-recorded decisions are replayed
//! by `managed_decision_vectors`.
use super::super::gpu_clock_offsets::{Activity, RZ09_0581, fake::Fake};
use super::*;
use serde_json::json;

fn tuning(enabled: bool, core: i32, memory: i32) -> Tuning {
    Tuning {
        enabled,
        offsets: GpuClockOffsets {
            core_megahertz: core,
            memory_megahertz: memory,
        },
    }
}

fn inactive() -> Activity {
    Box::new(|| Some(false))
}

fn management(fake: &Fake) -> Management<Fake> {
    Management::new(fake.clone(), inactive(), RZ09_0581)
}

/// A submission whose core write applies and fails, with a restore that
/// fails too: uncertain, as `gpu_clock_offsets_tests::Rig::uncertain`.
fn fail_mid_pair(fake: &Fake) {
    fake.with(|d| {
        d.sets_fail = true;
        d.storing_failures = 1;
    });
}

#[test]
fn tuning_parses_the_saved_form_and_refuses_null_offsets() {
    assert_eq!(
        Tuning::parse(
            &json!({"enabled":true,"offsets":{"coreMegahertz":125,"memoryMegahertz":500}})
        ),
        Some(tuning(true, 125, 500))
    );
    assert_eq!(
        Tuning::parse(
            &json!({"enabled":false,"offsets":{"coreMegahertz":125,"memoryMegahertz":500},"effectiveOffsets":{"coreMegahertz":0,"memoryMegahertz":0}})
        ),
        Some(tuning(false, 125, 500))
    );
    assert_eq!(Tuning::parse(&json!({"enabled":true,"offsets":null})), None);
    assert_eq!(
        tuning(false, 125, 500).effective(),
        GpuClockOffsets::default()
    );
}

#[test]
fn no_tuning_relinquishes_and_writes_nothing() {
    let fake = Fake::new(0, 0);
    let mut m = management(&fake);
    assert_eq!(
        prepare(&mut m, None, false),
        Prepared::Proceed(Half::Relinquished)
    );
    assert_eq!(m.state(), State::Relinquished);
    assert_eq!((fake.sets(), fake.inits()), (0, 0));
}

#[test]
fn a_paired_write_is_preflighted_before_any_firmware_write_and_submitted_after() {
    let fake = Fake::new(0, 0);
    let mut m = management(&fake);
    let wanted = tuning(true, 125, 500);
    assert_eq!(
        prepare(&mut m, Some(wanted), false),
        Prepared::Proceed(Half::Paired(wanted))
    );
    // The preflight queried and wrote nothing.
    assert_eq!((fake.sets(), fake.inits()), (0, 1));
    assert_eq!(
        complete(&mut m, Half::Paired(wanted)),
        Completed::applied(APPLIED)
    );
    assert_eq!(fake.current(), (125, 500));
    assert_eq!(m.state(), State::Managed);
}

#[test]
fn a_refused_preflight_writes_nothing() {
    // Outside the envelope, outside the driver's range, and unreadable.
    for (offsets, queries_fail, message) in [
        (
            (401, 0),
            false,
            "Performance is unavailable: The requested GPU clock offsets are outside the ranges reported by the NVIDIA driver.",
        ),
        (
            (0, 125),
            true,
            "Performance is unavailable: GPU clock tuning is unavailable.",
        ),
    ] {
        let fake = Fake::new(0, 0);
        fake.with(|d| d.queries_fail = queries_fail);
        let mut m = management(&fake);
        assert_eq!(
            prepare(&mut m, Some(tuning(true, offsets.0, offsets.1)), false),
            Prepared::Refused(message.into())
        );
        assert_eq!(fake.sets(), 0);
        assert_eq!(m.state(), State::Unknown);
    }
}

#[test]
fn a_matching_tuning_writes_nothing_and_a_differing_one_is_gpu_only() {
    let fake = Fake::new(0, 0);
    let mut m = management(&fake);
    m.set(tuning(true, 125, 500).effective(), true).unwrap();
    let sets = fake.sets();
    assert_eq!(
        prepare(&mut m, Some(tuning(true, 125, 500)), true),
        Prepared::Proceed(Half::Matches)
    );
    let wanted = tuning(true, 50, 500);
    let Prepared::Proceed(half) = prepare(&mut m, Some(wanted), true) else {
        panic!("GPU-only");
    };
    assert_eq!(
        half,
        Half::GpuOnly {
            tuning: wanted,
            initial: Some(tuning(true, 125, 500).offsets)
        }
    );
    assert_eq!(fake.sets(), sets);
    assert_eq!(complete(&mut m, half), Completed::applied(APPLIED));
    assert_eq!(fake.current(), (50, 500));
}

#[test]
fn a_disabled_tuning_matches_only_a_confirmed_reset() {
    let fake = Fake::new(0, 0);
    let mut m = management(&fake);
    let disabled = tuning(false, 125, 500);
    // Defaults the driver happens to hold are not OpenBlade's reset.
    assert!(matches!(
        prepare(&mut m, Some(disabled), true),
        Prepared::Proceed(Half::GpuOnly { .. })
    ));
    m.set(GpuClockOffsets::default(), false).unwrap();
    assert_eq!(
        prepare(&mut m, Some(disabled), true),
        Prepared::Proceed(Half::Matches)
    );
    // A paired disable reuses the confirmed reset without a query.
    let inits = fake.inits();
    assert_eq!(
        prepare(&mut m, Some(disabled), false),
        Prepared::Proceed(Half::PairedReuse)
    );
    assert_eq!(fake.inits(), inits);
    let sets = fake.sets();
    assert_eq!(
        complete(&mut m, Half::PairedReuse),
        Completed::applied(APPLIED)
    );
    assert_eq!(fake.sets(), sets);
    assert_eq!(m.state(), State::ResetConfirmed);
}

#[test]
fn an_nvml_failure_mid_pair_is_indeterminate_judged_read_only_and_never_replayed() {
    let fake = Fake::new(0, 0);
    let mut m = management(&fake);
    let wanted = tuning(true, 125, 500);
    assert_eq!(
        prepare(&mut m, Some(wanted), false),
        Prepared::Proceed(Half::Paired(wanted))
    );
    fail_mid_pair(&fake);
    let completed = complete(&mut m, Half::Paired(wanted));
    assert!(!completed.applied);
    assert!(
        completed.message.starts_with(
            "Performance write is indeterminate: The power mode was applied, but GPU clock tuning failed. "
        ),
        "{}",
        completed.message
    );
    // The follow-up read found the core written and the memory not: neither
    // the request nor the prior state.
    assert!(
        completed
            .message
            .ends_with("Fresh readback did not match the requested or prior performance state."),
        "{}",
        completed.message
    );
    // One core write and its failed restore; nothing after the read.
    assert_eq!(fake.sets(), 2);
    assert_eq!(m.state(), State::Managed);
}

#[test]
fn an_unreadable_uncertain_submission_blocks_the_next_step_until_resolved() {
    let fake = Fake::new(0, 0);
    let mut m = management(&fake);
    let wanted = tuning(true, 125, 500);
    fail_mid_pair(&fake);
    assert_eq!(
        m.set(wanted.effective(), true).unwrap_err().failure,
        Failure::Indeterminate
    );
    // The driver is lost before the follow-up read.
    fake.with(|d| {
        d.sets_fail = false;
        d.queries_fail = true;
    });
    assert_eq!(
        resolve(&mut m, wanted, "lost.", |_, _| false),
        Completed::failed(
            "Performance write is indeterminate: lost. Fresh readback cannot confirm the GPU clock offsets."
        )
    );
    let (sets, inits) = (fake.sets(), fake.inits());
    // Every next step is refused while the driver stays unreadable and the
    // dGPU idle, with or without tuning; nothing is submitted or queried.
    fake.with(|d| d.queries_fail = false);
    for next in [Some(wanted), None] {
        for unchanged in [false, true] {
            assert_eq!(prepare(&mut m, next, unchanged), Prepared::Pending);
        }
    }
    assert_eq!((fake.sets(), fake.inits()), (sets, inits));
    // A power-change rearm allows one query, which resolves it read-only:
    // the core landed and the memory did not.
    m.rearm();
    assert_eq!(
        prepare(&mut m, Some(tuning(true, 125, 0)), true),
        Prepared::Proceed(Half::Matches)
    );
    assert_eq!(fake.sets(), sets);
}

#[test]
fn a_gpu_only_failure_is_reported_as_managed_reports_it() {
    // Unavailable: the dGPU cannot be read at the setter's own preflight.
    let fake = Fake::new(0, 0);
    let mut m = management(&fake);
    let wanted = tuning(true, 125, 500);
    fake.with(|d| d.queries_fail = true);
    let half = Half::GpuOnly {
        tuning: wanted,
        initial: None,
    };
    assert_eq!(
        complete(&mut m, half),
        Completed::failed("Performance is unavailable: GPU clock tuning is unavailable.")
    );
    // Not applied: the write failed and its restore confirmed the prior state.
    fake.with(|d| {
        d.queries_fail = false;
        d.sets_fail = true;
    });
    let completed = complete(&mut m, half);
    assert!(
        completed
            .message
            .starts_with("Performance write was not applied: "),
        "{}",
        completed.message
    );
    assert_eq!(fake.current(), (0, 0));
    // Indeterminate with the prior managed offsets read back: the prior
    // state remained active.
    fake.with(|d| d.sets_fail = false);
    m.set(tuning(true, 50, 50).effective(), true).unwrap();
    fake.with(|d| {
        d.sets_fail = true;
        d.storing_failures = 0;
        d.clamp_core = None;
    });
    let completed = resolve(&mut m, wanted, "x", |current, _| {
        current.map(Capabilities::current) == Some(tuning(true, 50, 50).offsets)
    });
    assert_eq!(
        completed.message,
        "Performance write is indeterminate: x Fresh readback confirmed that the prior performance state remained active."
    );
}

#[test]
fn a_moved_gate_generation_withdraws_a_confirmed_reset_before_the_next_decision() {
    let fake = Fake::new(0, 0);
    let driver = fake.clone();
    let lane = Lane::new(
        Worker::spawn(move || Management::new(Box::new(driver) as Backend, inactive(), RZ09_0581))
            .unwrap(),
    );
    let before = Boundary {
        gate: Some(0),
        power: Some(1),
    };
    let disabled = tuning(false, 125, 500);
    // A confirmed reset.
    let Prepared::Proceed(half) = lane.prepare(before, Some(disabled), true) else {
        panic!("GPU-only");
    };
    assert!(matches!(half, Half::GpuOnly { .. }));
    assert!(lane.complete(before, half).unwrap().applied);
    assert_eq!(
        lane.prepare(before, Some(disabled), false),
        Prepared::Proceed(Half::PairedReuse)
    );
    // After a resume the reset is not evidence: the paired disable queries
    // and submits it again, and it no longer matches a disabled tuning.
    let resumed = Boundary {
        gate: Some(2),
        ..before
    };
    let inits = fake.inits();
    assert_eq!(
        lane.prepare(resumed, Some(disabled), false),
        Prepared::Proceed(Half::Paired(disabled))
    );
    assert_eq!(fake.inits(), inits + 1);
    assert!(matches!(
        lane.prepare(resumed, Some(disabled), true),
        Prepared::Proceed(Half::GpuOnly { .. })
    ));
}

#[test]
fn a_moved_power_generation_rearms_one_ungated_query() {
    let fake = Fake::new(0, 0);
    let driver = fake.clone();
    let lane = Lane::new(
        Worker::spawn(move || Management::new(Box::new(driver) as Backend, inactive(), RZ09_0581))
            .unwrap(),
    );
    let first = Boundary {
        gate: Some(0),
        power: Some(1),
    };
    let wanted = tuning(true, 125, 500);
    assert_eq!(
        lane.prepare(first, Some(wanted), false),
        Prepared::Proceed(Half::Paired(wanted))
    );
    // The paired submission fails after writing core, and the driver is
    // lost before its follow-up read.
    fail_mid_pair(&fake);
    fake.with(|d| d.lose_on_set = true);
    let completed = lane.complete(first, Half::Paired(wanted)).unwrap();
    assert_eq!(
        completed.message,
        format!(
            "Performance write is indeterminate: The power mode was applied, but GPU clock tuning failed. {} Fresh readback cannot confirm the GPU clock offsets.",
            completed
                .message
                .strip_prefix("Performance write is indeterminate: The power mode was applied, but GPU clock tuning failed. ")
                .and_then(|rest| rest.strip_suffix(" Fresh readback cannot confirm the GPU clock offsets."))
                .unwrap()
        )
    );
    fake.with(|d| {
        d.sets_fail = false;
        d.queries_fail = false;
        d.lose_on_set = false;
    });
    let (sets, inits) = (fake.sets(), fake.inits());
    // The dGPU is idle and the one ungated query was spent: refused, with
    // no query and no write.
    assert_eq!(lane.prepare(first, Some(wanted), true), Prepared::Pending);
    assert_eq!(lane.prepare(first, None, false), Prepared::Pending);
    assert_eq!(fake.inits(), inits);
    // A power change rearms one query, which resolves it read-only.
    let changed = Boundary {
        power: Some(2),
        ..first
    };
    assert_eq!(
        lane.prepare(changed, Some(wanted), true),
        Prepared::Proceed(Half::GpuOnly {
            tuning: wanted,
            initial: Some(tuning(true, 125, 0).offsets),
        })
    );
    assert_eq!((fake.sets(), fake.inits()), (sets, inits + 1));
}

#[test]
fn a_lost_admission_submits_nothing_and_names_the_applied_firmware_half() {
    let wanted = tuning(true, 125, 500);
    assert_eq!(unadmitted(Half::Matches), None);
    assert_eq!(
        unadmitted(Half::GpuOnly {
            tuning: wanted,
            initial: None
        })
        .unwrap()
        .message,
        "Performance write was not applied: GPU clock tuning lost its write admission before it was submitted."
    );
    assert_eq!(
        unadmitted(Half::Paired(wanted)).unwrap().message,
        "Performance write is indeterminate: The power mode was applied, but GPU clock tuning failed. GPU clock tuning lost its write admission before it was submitted."
    );
}

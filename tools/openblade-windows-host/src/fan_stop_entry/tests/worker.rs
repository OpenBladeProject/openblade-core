use super::*;
use crate::{
    cpu_worker::{
        Exit, Failure, PrivateChild, Supervisor,
        protocol::{MilliCelsius, Reply, Sequence},
    },
    fan_stop_entry::worker::{
        AttemptSample, GpuRead, GpuState, WorkerAttempt, WorkerThermal, replay_worker,
    },
};
use std::io;
use tokio::{
    io::{AsyncReadExt, AsyncWriteExt, DuplexStream, duplex},
    sync::watch,
    time::{advance, sleep},
};

fn child() -> (
    PrivateChild<DuplexStream>,
    DuplexStream,
    watch::Sender<Exit>,
) {
    let (io, peer) = duplex(64);
    let (stop, _) = watch::channel(None);
    let (exited, exit) = watch::channel(Exit::Pending);
    (PrivateChild { io, stop, exit }, peer, exited)
}

fn no_attach() -> io::Result<PrivateChild<DuplexStream>> {
    panic!("the current worker must keep its slot")
}

async fn request(peer: &mut DuplexStream, sequence: i64) {
    let mut wire = [0; 8];
    peer.read_exact(&mut wire).await.unwrap();
    assert_eq!(Sequence::decode(&wire), Sequence::new(sequence));
}

async fn respond(peer: &mut DuplexStream, sequence: i64, reply: Reply) {
    peer.write_all(&reply.encode(Sequence::new(sequence).unwrap()))
        .await
        .unwrap();
}

fn value(milli: i32) -> Reply {
    Reply::Value(MilliCelsius::new(milli).unwrap())
}

fn initial() -> VolatileEvidence {
    safety(40).volatile
}

fn inactive() -> GpuState {
    GpuState::Inactive { celsius: None }
}

fn worker_entry<'a>(
    attempt: &'a WorkerAttempt,
    cpu: Option<&'a AttemptSample<'a>>,
    gpu: GpuState,
) -> ManualEntry<'static, WorkerThermal<'a>, &'a WorkerAttempt> {
    let mut e = admitted_with(
        WorkerThermal {
            cpu,
            gpu: Some(GpuRead {
                capture: span(46, 50),
                state: gpu,
            }),
            canceled: false,
        },
        attempt,
    );
    e.entry = None;
    e
}

const MISSING_CPU: Result<(), Refusal> = Err(Refusal::InitialSafety(SafetyRefusal::NotObserved(
    ThermalNotObserved::Unavailable,
)));

#[tokio::test(start_paused = true)]
async fn ready_is_not_a_sample_and_gpu_has_independent_provenance() {
    let (child, mut peer, _exit) = child();
    let mut supervisor = Supervisor::default();
    let mut attempt = WorkerAttempt::new(7);
    let script = tokio::spawn(async move {
        request(&mut peer, 1).await;
        respond(&mut peer, 1, Reply::Ready).await;
        request(&mut peer, 2).await;
        respond(&mut peer, 2, value(54_999)).await;
    });

    assert_eq!(
        replay_worker(&worker_entry(&attempt, None, inactive())),
        Err(Refusal::CpuReaderPreparation)
    );
    attempt
        .prepare(supervisor.prepare_readiness(|| Ok(child)))
        .await;
    assert_eq!(
        replay_worker(&worker_entry(&attempt, None, inactive())),
        MISSING_CPU
    );
    advance(ms(46)).await;
    let cpu = attempt
        .sample(initial(), supervisor.sample(no_attach))
        .await
        .unwrap();
    script.await.unwrap();

    let mut e = worker_entry(&attempt, Some(&cpu), inactive());
    assert_eq!(replay_worker(&e), Ok(()));
    for policy in [
        ThermalPolicy::Auto,
        ThermalPolicy::Custom {
            cpu_cutoff: 80,
            gpu_cutoff: 75,
        },
    ] {
        e.policy = policy;
        let input = &mut e.initial.as_mut().unwrap().thermal;
        input.gpu.as_mut().unwrap().state = GpuState::Unknown;
        assert!(matches!(
            replay_worker(&e),
            Err(Refusal::InitialSafety(SafetyRefusal::Thermal(
                ThermalDiagnostic {
                    reading: Readings {
                        cpu: Some(55),
                        gpu: None,
                        ..
                    },
                    decision: None,
                    ..
                }
            )))
        ));
    }

    e.initial
        .as_mut()
        .unwrap()
        .thermal
        .gpu
        .as_mut()
        .unwrap()
        .state = GpuState::PoweredDown {
        inactive: false,
        celsius: None,
    };
    assert_eq!(replay_worker(&e), Ok(())); // Custom accepts explicit D3.
    e.policy = ThermalPolicy::Auto;
    assert!(matches!(
        replay_worker(&e),
        Err(Refusal::InitialSafety(SafetyRefusal::Thermal(_)))
    ));
    e.initial
        .as_mut()
        .unwrap()
        .thermal
        .gpu
        .as_mut()
        .unwrap()
        .state = inactive();
    e.initial
        .as_mut()
        .unwrap()
        .thermal
        .gpu
        .as_mut()
        .unwrap()
        .capture
        .run = 8;
    assert!(matches!(
        replay_worker(&e),
        Err(Refusal::InitialSafety(SafetyRefusal::Thermal(_)))
    ));

    e.initial
        .as_mut()
        .unwrap()
        .thermal
        .gpu
        .as_mut()
        .unwrap()
        .capture
        .run = 7;
    e.apply.canceled = true;
    assert_eq!(replay_worker(&e), Err(Refusal::EntryCanceled));
    e.apply.canceled = false;
    e.entry_canceled = true;
    assert_eq!(replay_worker(&e), Err(Refusal::EntryCanceled));
    e.entry_canceled = false;
    e.initial.as_mut().unwrap().thermal.canceled = true;
    assert!(matches!(
        replay_worker(&e),
        Err(Refusal::InitialSafety(SafetyRefusal::Thermal(
            ThermalDiagnostic {
                canceled: true,
                decision: None,
                ..
            }
        )))
    ));
    e.service.caller_controls_session = false;
    assert_eq!(replay_worker(&e), Err(Refusal::DispatchSession));
}

#[tokio::test(start_paused = true)]
async fn response_cannot_complete_an_earlier_or_a_different_observation() {
    let (child, mut peer, _exit) = child();
    let mut supervisor = Supervisor::default();
    let mut attempt = WorkerAttempt::new(7);
    let script = tokio::spawn(async move {
        request(&mut peer, 1).await;
        respond(&mut peer, 1, Reply::Ready).await;
        request(&mut peer, 2).await;
        sleep(ms(16)).await;
        respond(&mut peer, 2, value(52_000)).await;
    });

    attempt
        .prepare(supervisor.prepare_readiness(|| Ok(child)))
        .await;
    advance(ms(46)).await;
    let cpu = attempt
        .sample(initial(), supervisor.sample(no_attach))
        .await
        .unwrap();
    script.await.unwrap();

    let mut e = worker_entry(&attempt, Some(&cpu), inactive());
    assert_eq!(replay_worker(&e), MISSING_CPU); // Sample arrived at 62, proof completed at 60.
    e.initial.as_mut().unwrap().capture.completed = ms(62);
    assert_eq!(replay_worker(&e), Ok(()));
    e.initial.as_mut().unwrap().volatile = volatile(42, 45);
    assert_eq!(replay_worker(&e), MISSING_CPU);

    // Separate activation, same wire generation and deliberately reused run
    // number. Its replay cannot consume a previous activation's completion.
    let mut changed = WorkerAttempt::new(7);
    changed
        .prepare(supervisor.prepare_readiness(no_attach))
        .await;
    let mut e = worker_entry(&changed, Some(&cpu), inactive());
    e.initial.as_mut().unwrap().capture.completed = ms(62);
    assert_eq!(replay_worker(&e), MISSING_CPU);
    e.binding.run = 8;
    e.owner.capture.run = 8;
    e.service.readback.as_mut().unwrap().run = 8;
    assert_eq!(replay_worker(&e), Err(Refusal::CpuReaderPreparation));
}

#[tokio::test(start_paused = true)]
async fn stale_entry_proof_and_uncertain_exit_cannot_authorize_stop() {
    let (child, mut peer, exit) = child();
    let mut supervisor = Supervisor::default();
    let mut attempt = WorkerAttempt::new(7);
    let script = tokio::spawn(async move {
        request(&mut peer, 1).await;
        respond(&mut peer, 1, Reply::Ready).await;
        request(&mut peer, 2).await;
        respond(&mut peer, 2, value(52_000)).await;
    });
    attempt
        .prepare(supervisor.prepare_readiness(|| Ok(child)))
        .await;
    advance(ms(46)).await;
    let cpu = attempt
        .sample(initial(), supervisor.sample(no_attach))
        .await
        .unwrap();
    script.await.unwrap();

    let mut e = worker_entry(&attempt, Some(&cpu), inactive());
    e.apply.now = ms(2040);
    assert_eq!(replay_worker(&e), Ok(()));
    e.apply.now = ms(2041);
    assert_eq!(
        replay_worker(&e),
        Err(Refusal::EntryCached(CachedRefusal::Safety(
            SafetyRefusal::MissingOrExpired
        )))
    );
    e.apply.now = ms(155);
    drop(exit); // Pending with its observer gone is not independently observed exit.
    assert!(supervisor.diagnostics().exit_unconfirmed);
    assert_eq!(replay_worker(&e), MISSING_CPU);
}

#[tokio::test(start_paused = true)]
async fn private_exchange_rejects_out_of_order_delayed_and_canceled_replies() {
    for (delay, sequence, canceled, expected) in [
        (0, 1, false, Some(Failure::MalformedReply)),
        (1001, 2, false, Some(Failure::Timeout)),
        (100, 2, true, None),
    ] {
        let (child, mut peer, _exit) = child();
        let mut supervisor = Supervisor::default();
        let mut attempt = WorkerAttempt::new(7);
        let script = tokio::spawn(async move {
            request(&mut peer, 1).await;
            respond(&mut peer, 1, Reply::Ready).await;
            request(&mut peer, 2).await;
            sleep(ms(delay)).await;
            if delay == 0 {
                respond(&mut peer, sequence, value(52_000)).await;
            }
        });
        attempt
            .prepare(supervisor.prepare_readiness(|| Ok(child)))
            .await;
        advance(ms(46)).await;
        let acquire = attempt.sample(initial(), supervisor.sample(no_attach));
        let cpu = if canceled {
            tokio::select! {
                biased;
                sample = acquire => sample,
                () = sleep(ms(1)) => None,
            }
        } else {
            acquire.await
        };
        assert!(cpu.is_none());
        assert_eq!(supervisor.diagnostics().last_failure, expected);
        assert_eq!(
            replay_worker(&worker_entry(&attempt, cpu.as_ref(), inactive())),
            MISSING_CPU
        );
        script.await.unwrap();
    }
}

#[tokio::test(start_paused = true)]
async fn cached_boundary_requires_the_selected_observations_own_completed_sample() {
    let (child, mut peer, _exit) = child();
    let mut supervisor = Supervisor::default();
    let mut attempt = WorkerAttempt::new(7);
    let script = tokio::spawn(async move {
        request(&mut peer, 1).await;
        respond(&mut peer, 1, Reply::Ready).await;
        for sequence in 2..=4 {
            request(&mut peer, sequence).await;
            respond(&mut peer, sequence, value(52_000)).await;
        }
    });
    attempt
        .prepare(supervisor.prepare_readiness(|| Ok(child)))
        .await;
    advance(ms(46)).await;
    let initial_cpu = attempt
        .sample(initial(), supervisor.sample(no_attach))
        .await
        .unwrap();
    advance(ms(80)).await;
    let entry_cpu = attempt
        .sample(safety(120).volatile, supervisor.sample(no_attach))
        .await
        .unwrap();
    advance(ms(20)).await;
    let newer_cpu = attempt
        .sample(safety(140).volatile, supervisor.sample(no_attach))
        .await
        .unwrap();
    script.await.unwrap();

    let proof = |started, cpu| {
        safety_with(
            started,
            WorkerThermal {
                cpu: Some(cpu),
                gpu: Some(GpuRead {
                    capture: span(started + 6, started + 7),
                    state: inactive(),
                }),
                canceled: false,
            },
        )
    };
    let mut e = worker_entry(&attempt, Some(&initial_cpu), inactive());
    e.entry = Some(proof(120, &entry_cpu));
    assert_eq!(replay_worker(&e), Ok(()));
    e.newer_safety = Some(proof(140, &newer_cpu));
    assert_eq!(
        replay_worker(&e),
        Err(Refusal::EntryCached(CachedRefusal::Safety(
            SafetyRefusal::MissingOrExpired
        )))
    );
    e.apply.now = ms(160);
    assert_eq!(replay_worker(&e), Ok(()));
    e.newer_safety.as_mut().unwrap().thermal.cpu = Some(&entry_cpu);
    assert_eq!(
        replay_worker(&e),
        Err(Refusal::EntryCached(CachedRefusal::Safety(
            SafetyRefusal::NotObserved(ThermalNotObserved::Unavailable)
        )))
    );
}

#[tokio::test(start_paused = true)]
async fn worker_samples_match_the_managed_thermal_corpus_within_the_wire_budget() {
    let corpus = include_str!("../../../tests/oracles/manual_fan_stop/manual-fan-stop-entry.tsv");
    let mut checked = 0;
    for line in corpus.lines().filter(|line| !line.starts_with('#')) {
        let f: Vec<_> = line.split('\t').collect();
        let Ok(cpu) = f[2].parse::<i32>() else {
            continue;
        };
        let Some(milli) = MilliCelsius::new(cpu * 1000) else {
            continue;
        };
        let Ok(age) = f[6].parse::<u64>() else {
            continue;
        };
        if age > 1_000 {
            // The worker deadline and whole-proof expiry are tested separately.
            continue;
        }

        let (child, mut peer, _exit) = child();
        let mut supervisor = Supervisor::default();
        let mut attempt = WorkerAttempt::new(7);
        let script = tokio::spawn(async move {
            request(&mut peer, 1).await;
            respond(&mut peer, 1, Reply::Ready).await;
            request(&mut peer, 2).await;
            respond(&mut peer, 2, Reply::Value(milli)).await;
        });
        attempt
            .prepare(supervisor.prepare_readiness(|| Ok(child)))
            .await;
        advance(ms(46)).await;
        let cpu = attempt
            .sample(initial(), supervisor.sample(no_attach))
            .await
            .unwrap();
        script.await.unwrap();
        advance(ms(age)).await;

        let celsius = f[3].parse::<i32>().ok();
        let inactive = f[4].parse().unwrap();
        let gpu = match (inactive, f[5].parse::<bool>().unwrap()) {
            (_, true) => GpuState::PoweredDown { inactive, celsius },
            (true, false) => GpuState::Inactive { celsius },
            (false, false) => GpuState::Active { celsius },
        };
        let mut e = worker_entry(&attempt, Some(&cpu), gpu);
        e.policy = if f[1] == "Auto" {
            ThermalPolicy::Auto
        } else {
            ThermalPolicy::Custom {
                cpu_cutoff: 80,
                gpu_cutoff: 75,
            }
        };
        let observation = e.initial.as_mut().unwrap();
        observation.thermal.canceled = f[7].parse().unwrap();
        observation.thermal.gpu.as_mut().unwrap().capture = span(46, 46);
        observation.capture.completed = ms(46 + age);
        e.initial_check_at = ms(46 + age);
        e.initial_power = Some(power(47 + age, 48 + age));
        e.entry_started = ms(49 + age);
        e.entry_volatile = volatile(49 + age, 50 + age);
        e.refreshed_at = ms(51 + age);
        e.apply.now = ms(52 + age);

        let (rule, cpu, gpu) = match replay_worker(&e) {
            Ok(()) => ("Admitted", None, None),
            Err(Refusal::InitialSafety(SafetyRefusal::Thermal(ThermalDiagnostic {
                decision: None,
                age: actual,
                ..
            }))) => {
                assert_eq!(actual, Some(ms(age)));
                ("SafetyCondition", None, None)
            }
            Err(Refusal::InitialSafety(SafetyRefusal::Thermal(ThermalDiagnostic {
                decision: Some(ThermalDecision { rule, cpu, gpu }),
                age: actual,
                ..
            }))) => {
                assert_eq!(actual, Some(ms(age)));
                let rule = match rule {
                    ThermalRule::AutoCpuLimit => "AutoCpuLimit",
                    ThermalRule::ImmediateCutoff => "ImmediateCutoff",
                };
                (rule, cpu, gpu)
            }
            other => panic!("{}: {other:?}", f[0]),
        };
        let trigger = |reading: Option<(i32, i32)>| {
            reading.map_or("-".to_owned(), |(actual, limit)| {
                format!("{actual}:{limit}")
            })
        };
        assert_eq!(
            (rule, trigger(cpu), trigger(gpu)),
            (f[8], f[9].to_owned(), f[10].to_owned()),
            "{}",
            f[0]
        );
        checked += 1;
    }
    assert_eq!(checked, 14);
}

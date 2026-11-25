//! The pace check on the Reader directly, on paused time: every transfer of
//! the in-memory port completes a fixed latency after its submission, so the
//! measured pace and the deadline arithmetic are exact.
use super::super::mutation::{Permit, Refusal};
use super::*;
use openblade_domain::{
    fan_recipe::FanIntent,
    thermal_mutation::{Destination, Preset, Progress},
};

const ADMITTED: ReadAdmission = ReadAdmission {
    lifecycle: 1,
    generation: [9; 32],
};
/// A send, the 30 ms settle and the reply: 100 + 30 + 100 ms.
const LATENCY: Duration = Duration::from_millis(100);

#[derive(Default)]
struct Paced {
    destination: Option<Destination>,
    fan: Option<FanIntent>,
    // Every journal append takes this long, and the Prepared append
    // `prepared_append` more.
    append: Duration,
    prepared_append: Duration,
    // Every authorization takes this long: discovery, and each checkpoint's
    // two.
    authorization: Duration,
    // The saved destination a fan request returns to.
    fan_destination: Option<Destination>,
    // What the pace must leave after the setters (`Permit::reserve`).
    reserve: Duration,
    rows: Mutex<Vec<Progress>>,
    fan_rows: Mutex<Vec<u8>>,
    refusals: Mutex<Vec<Refusal>>,
    // A keyboard Driver-mode write job (`Reader::apply_device_mode`).
    #[cfg(feature = "service-keyboard-driver-mode-v1")]
    job: Option<DeviceModeJob>,
}
impl Paced {
    fn append(&self, prepared: bool) {
        spend(
            self.append
                + if prepared {
                    self.prepared_append
                } else {
                    Duration::ZERO
                },
        );
    }
}
/// Spends `duration` of the paused clock inside a synchronous journal append.
/// `tokio::time::advance` moves the clock on its first poll and then only
/// yields, so the future is dropped after that poll.
fn spend(duration: Duration) {
    if duration.is_zero() {
        return;
    }
    let mut advance = std::pin::pin!(tokio::time::advance(duration));
    let _ = advance
        .as_mut()
        .poll(&mut std::task::Context::from_waker(std::task::Waker::noop()));
}
impl Permit for Paced {
    #[cfg(feature = "service-keyboard-driver-mode-v1")]
    fn device_mode(&self) -> Option<&DeviceModeJob> {
        self.job.as_ref()
    }
    fn authorize(&self) -> Option<ReadAdmission> {
        spend(self.authorization);
        Some(ADMITTED)
    }
    fn windows_on_battery(&self) -> Option<bool> {
        Some(false)
    }
    fn reserve(&self) -> Duration {
        self.reserve
    }
    fn is_fan(&self) -> bool {
        self.fan.is_some()
    }
    fn resolve_fan(
        &self,
        _: i32,
        _: ObservedPerformance,
    ) -> io::Result<Option<(FanIntent, Option<Destination>)>> {
        Ok(self.fan.map(|intent| (intent, self.fan_destination)))
    }
    fn resolve(&self, _: i32, _: ObservedPerformance) -> io::Result<Option<Destination>> {
        Ok(self.destination)
    }
    fn refused(&self, cause: Refusal) {
        self.refusals.lock().unwrap().push(cause);
    }
    fn record(&self, progress: Progress) -> io::Result<()> {
        self.append(progress == Progress::Prepared);
        self.rows.lock().unwrap().push(progress);
        Ok(())
    }
    fn fan_record(&self) -> Option<openblade_domain::fan_history::FanRecord> {
        Some(openblade_domain::fan_history::FanRecord {
            contract: openblade_domain::fan_history::CONTRACT.into(),
            requested: serde_json::json!({"value": null, "settings": null}),
            intent: FanIntent::checked(0, 3700)?,
            selector: 0,
            entry: false,
            source: 0,
            preflight: [[0; 2]; 2],
            ids: Vec::new(),
            cleanup: Vec::new(),
            custom: None,
        })
    }
    fn prepare_fan(&self, _: openblade_domain::fan_history::FanRecord) -> io::Result<()> {
        Ok(())
    }
    fn record_fan(&self, progress: u8) -> io::Result<()> {
        self.append(progress == 0);
        self.fan_rows.lock().unwrap().push(progress);
        Ok(())
    }
}

/// Balanced with Automatic fans on a full adapter.
fn balanced() -> Fixture {
    let fixture = Fixture::new();
    {
        let mut s = fixture.state.lock().unwrap();
        s.selectors = [0, 0];
        s.fans = [0, 0];
        s.latency = Some(LATENCY);
    }
    fixture
}

async fn apply(fixture: &Fixture, permit: &Paced, budget: Duration) -> Option<ObservedPerformance> {
    let cancelled = AtomicBool::new(false);
    let limit = Limit {
        deadline: Instant::now() + budget,
        cancelled: &cancelled,
    };
    let mut reader = Reader::new(fixture.port(), ADMITTED);
    let observed = reader.apply_preset(permit, limit).await;
    reader.retire(None).await.unwrap();
    observed
}

#[tokio::test(start_paused = true)]
async fn a_preset_recipe_starts_only_when_its_measured_pace_fits_the_deadline() {
    // The observation pair, the 07/8C read and the first setter's own 07/8C
    // read take 4 × 230 ms. The rest is two setters, the second setter's power
    // read and the final pair: 5 × 230 ms, so the recipe ends 2070 ms after it
    // starts. Every exchange takes 230 ms, so the slowest and the second
    // longest agree and the check is exact.
    let silent = || Paced {
        destination: Some(Destination::Preset(Preset::Silent)),
        ..Paced::default()
    };
    let fixture = balanced();
    let permit = silent();
    let started = Instant::now();
    assert_eq!(
        apply(&fixture, &permit, Duration::from_millis(2060)).await,
        None
    );
    // Refused after the Prepared row and the first setter's power read, and
    // before any setter.
    assert_eq!(*permit.refusals.lock().unwrap(), [Refusal::Deadline]);
    assert_eq!(*permit.rows.lock().unwrap(), [Progress::Prepared]);
    {
        let s = fixture.state.lock().unwrap();
        assert_eq!((s.writes, s.selectors), (0, [0, 0]));
    }
    assert_eq!(started.elapsed(), Duration::from_millis(920));
    // With 10 ms to spare the same recipe runs and is confirmed in 2070 ms.
    let fixture = balanced();
    let permit = silent();
    let started = Instant::now();
    assert_eq!(
        apply(&fixture, &permit, Duration::from_millis(2080)).await,
        Some(ObservedPerformance::SilentAutomatic)
    );
    assert!(permit.refusals.lock().unwrap().is_empty());
    assert_eq!(
        permit.rows.lock().unwrap().last(),
        Some(&Progress::Confirmed)
    );
    assert_eq!(started.elapsed(), Duration::from_millis(2070));
}

/// The permit's reserve is charged after the recipe's measured pace
/// (`Permit::reserve`), as a paired NVML submission needs it: the recipe
/// that ends 2070 ms after it starts, with a 100 ms reserve, is refused
/// before any setter with 2160 ms and runs with 2180 ms.
#[tokio::test(start_paused = true)]
async fn a_reserve_is_charged_after_the_recipe_measured_pace() {
    let reserved = || Paced {
        destination: Some(Destination::Preset(Preset::Silent)),
        reserve: Duration::from_millis(100),
        ..Paced::default()
    };
    let fixture = balanced();
    let permit = reserved();
    assert_eq!(
        apply(&fixture, &permit, Duration::from_millis(2160)).await,
        None
    );
    assert_eq!(*permit.refusals.lock().unwrap(), [Refusal::Deadline]);
    assert_eq!(fixture.state.lock().unwrap().writes, 0);
    let fixture = balanced();
    let permit = reserved();
    let started = Instant::now();
    assert_eq!(
        apply(&fixture, &permit, Duration::from_millis(2180)).await,
        Some(ObservedPerformance::SilentAutomatic)
    );
    assert!(permit.refusals.lock().unwrap().is_empty());
    assert_eq!(started.elapsed(), Duration::from_millis(2070));
}

#[tokio::test(start_paused = true)]
async fn custom_counts_its_level_setters_and_level_reads() {
    // Four setters with their power reads, the final pair and its levels:
    // 12 × 230 ms after the same 690 ms, ending at 3450 ms.
    let custom = || Paced {
        destination: Destination::custom(1, 0, false),
        ..Paced::default()
    };
    let fixture = balanced();
    let permit = custom();
    assert_eq!(
        apply(&fixture, &permit, Duration::from_millis(3440)).await,
        None
    );
    assert_eq!(*permit.refusals.lock().unwrap(), [Refusal::Deadline]);
    assert_eq!(*permit.rows.lock().unwrap(), [Progress::Prepared]);
    assert_eq!(fixture.state.lock().unwrap().writes, 0);
    let fixture = balanced();
    let permit = custom();
    let started = Instant::now();
    assert!(
        apply(&fixture, &permit, Duration::from_millis(3460))
            .await
            .is_some()
    );
    assert_eq!(fixture.state.lock().unwrap().writes, 4);
    assert_eq!(started.elapsed(), Duration::from_millis(3450));
}

#[tokio::test(start_paused = true)]
async fn a_fan_recipe_adds_its_fixed_spacing_before_the_first_setter() {
    // Fixed fans from Balanced: the entry state pair and the target pair (4
    // setters with their power reads) and the final pair, 10 × 230 ms, plus
    // 210 + 65 ms of spacing after the same 690 ms: 3265 ms. Balanced has no
    // intermediate pair (selector 0).
    let fixed = || Paced {
        fan: FanIntent::checked(1, 3700),
        ..Paced::default()
    };
    let fixture = balanced();
    let permit = fixed();
    assert_eq!(
        apply(&fixture, &permit, Duration::from_millis(3260)).await,
        None
    );
    assert_eq!(*permit.refusals.lock().unwrap(), [Refusal::Deadline]);
    assert_eq!(*permit.fan_rows.lock().unwrap(), [0]);
    assert_eq!(fixture.state.lock().unwrap().writes, 0);
    // With 5 ms to spare the check passes and the four setters run.
    let fixture = balanced();
    let permit = fixed();
    apply(&fixture, &permit, Duration::from_millis(3270)).await;
    assert!(permit.refusals.lock().unwrap().is_empty());
    assert_eq!(fixture.state.lock().unwrap().writes, 4);
}

#[tokio::test(start_paused = true)]
async fn each_operation_measures_its_own_pace_and_a_refusal_leaves_the_reader_open() {
    // A 07/8C read held 1300 ms refuses the first operation after its Prepared
    // row. That refusal submitted nothing, so it does not seal the Reader as an
    // abandoned transfer would, and `Pace::start` discards its slow interval:
    // the next operation on the same Reader runs at its own pace.
    for fan in [None, FanIntent::checked(1, 3700)] {
        let operation = || Paced {
            destination: fan.is_none().then_some(Destination::Preset(Preset::Silent)),
            fan,
            ..Paced::default()
        };
        let fixture = balanced();
        fixture.state.lock().unwrap().power_latency = Some(Duration::from_millis(1300));
        let cancelled = AtomicBool::new(false);
        let limit = || Limit {
            deadline: Instant::now() + Duration::from_secs(6),
            cancelled: &cancelled,
        };
        let mut reader = Reader::new(fixture.port(), ADMITTED);
        let refused = operation();
        assert_eq!(reader.apply_preset(&refused, limit()).await, None);
        assert_eq!(
            *refused.refusals.lock().unwrap(),
            [Refusal::Deadline],
            "{fan:?}"
        );
        assert!(!reader.poisoned, "{fan:?}");
        assert_eq!(fixture.state.lock().unwrap().writes, 0);
        fixture.state.lock().unwrap().power_latency = None;
        let next = operation();
        assert!(
            reader.apply_preset(&next, limit()).await.is_some(),
            "{fan:?}"
        );
        assert!(next.refusals.lock().unwrap().is_empty(), "{fan:?}");
        assert!(fixture.state.lock().unwrap().writes > 0, "{fan:?}");
        reader.retire(None).await.unwrap();
    }
}

/// One overrun shape: the destination, the extra latency on every 07/8C read,
/// every setter exchange and every journal append, and the instant of the
/// refusal.
struct Overrun {
    name: &'static str,
    destination: Option<Destination>,
    power: u64,
    setter: u64,
    append: u64,
    refused_at: u64,
}

#[tokio::test(start_paused = true)]
async fn every_overrun_shape_is_refused_before_any_setter() {
    // Each shape would end past the 6 s budget if it started:
    // - every 07/8C read 1300 ms slower, Custom: each read takes 1530 ms. At
    //   the check (460 + 1530 + 1530 ms) the slowest and second longest are
    //   both 1530 ms; the eleven exchanges left end at 3520 + 1530 + 10 × 1530
    //   = 20350 ms. Run, the recipe would end at 9950 ms;
    // - every journal append 1600 ms slower, Silent: the Prepared append is
    //   1600 ms, so the five rows left cost 8000 ms on top of five 230 ms
    //   exchanges, ending at 2520 + 1150 + 8000 = 11670 ms;
    // - 350 ms more on every 07/8C read, setter exchange and append, Custom:
    //   1970 + 580 + 10 × 580 + 9 × 350 = 11500 ms. Run, it would end at
    //   10100 ms with every setter submitted;
    // - the same at 650 ms, Silent: 2870 + 880 + 4 × 880 + 5 × 650 = 10520 ms.
    let shapes = [
        Overrun {
            name: "slow 07/8C reads",
            destination: Destination::custom(1, 0, false),
            power: 1300,
            setter: 0,
            append: 0,
            refused_at: 3520,
        },
        Overrun {
            name: "slow appends",
            destination: Some(Destination::Preset(Preset::Silent)),
            power: 0,
            setter: 0,
            append: 1600,
            refused_at: 2520,
        },
        Overrun {
            name: "slow device and journal, Custom",
            destination: Destination::custom(1, 0, false),
            power: 350,
            setter: 350,
            append: 350,
            refused_at: 1970,
        },
        Overrun {
            name: "slow device and journal, Silent",
            destination: Some(Destination::Preset(Preset::Silent)),
            power: 650,
            setter: 650,
            append: 650,
            refused_at: 2870,
        },
    ];
    for shape in shapes {
        let fixture = balanced();
        {
            let mut s = fixture.state.lock().unwrap();
            s.power_latency = Some(Duration::from_millis(shape.power));
            s.setter_latency = Some(Duration::from_millis(shape.setter));
        }
        let permit = Paced {
            destination: shape.destination,
            append: Duration::from_millis(shape.append),
            ..Paced::default()
        };
        let started = Instant::now();
        assert_eq!(
            apply(&fixture, &permit, Duration::from_secs(6)).await,
            None,
            "{}",
            shape.name
        );
        assert_eq!(
            *permit.refusals.lock().unwrap(),
            [Refusal::Deadline],
            "{}",
            shape.name
        );
        assert_eq!(
            *permit.rows.lock().unwrap(),
            [Progress::Prepared],
            "{}",
            shape.name
        );
        assert_eq!(fixture.state.lock().unwrap().writes, 0, "{}", shape.name);
        assert_eq!(
            started.elapsed(),
            Duration::from_millis(shape.refused_at),
            "{}",
            shape.name
        );
    }
}

#[tokio::test(start_paused = true)]
async fn a_single_stalled_power_read_refuses_no_recipe() {
    // One 07/8C read held 300 to 1000 ms is charged once. The worst case,
    // Custom with its first read held 1000 ms, checks at 460 + 1230 + 230 ms
    // and ends by 1920 + 1230 + 10 × 230 = 5450 ms, inside 6 s. The stall may
    // be the recipe's first reading, the first setter's own reading or a later
    // one.
    let recipes = || {
        [
            Paced {
                destination: Some(Destination::Preset(Preset::Silent)),
                ..Paced::default()
            },
            Paced {
                destination: Some(Destination::Preset(Preset::Performance)),
                ..Paced::default()
            },
            Paced {
                destination: Destination::custom(1, 0, false),
                ..Paced::default()
            },
            Paced {
                fan: FanIntent::checked(1, 3700),
                ..Paced::default()
            },
        ]
    };
    for stall in [300, 650, 1000] {
        for read in [1, 2, 3] {
            for permit in recipes() {
                let fixture = balanced();
                fixture.state.lock().unwrap().power_stall =
                    Some((read, Duration::from_millis(stall)));
                let case = format!(
                    "{stall} ms on read {read}: {:?} {:?}",
                    permit.destination, permit.fan
                );
                assert!(
                    apply(&fixture, &permit, Duration::from_secs(6))
                        .await
                        .is_some(),
                    "{case}"
                );
                assert!(permit.refusals.lock().unwrap().is_empty(), "{case}");
            }
        }
    }
}

#[tokio::test(start_paused = true)]
async fn a_stalled_power_read_is_charged_once_at_its_own_length() {
    // Silent with one 07/8C read held S ms, the recipe's first reading or the
    // first setter's own, checks at 920 + S ms. Five exchanges are left: one is
    // charged at the slowest (230 + S) and four at the second longest (230), so
    // the check ends at 2070 + 2S ms, which must be before the 6 s deadline:
    // replies are polled every 5 ms, so it runs up to S = 1960 ms and is
    // refused at 1965 ms, where the check ends exactly at the deadline.
    // Charging the stall at the second longest would let it run.
    for read in [1, 2] {
        for (stall, runs) in [(1960, true), (1965, false)] {
            let fixture = balanced();
            fixture.state.lock().unwrap().power_stall = Some((read, Duration::from_millis(stall)));
            let permit = Paced {
                destination: Some(Destination::Preset(Preset::Silent)),
                ..Paced::default()
            };
            let started = Instant::now();
            let observed = apply(&fixture, &permit, Duration::from_secs(6)).await;
            let case = format!("{stall} ms on read {read}");
            if runs {
                assert_eq!(
                    observed,
                    Some(ObservedPerformance::SilentAutomatic),
                    "{case}"
                );
                assert!(permit.refusals.lock().unwrap().is_empty(), "{case}");
                assert_eq!(
                    started.elapsed(),
                    Duration::from_millis(2070 + stall),
                    "{case}"
                );
            } else {
                assert_eq!(observed, None, "{case}");
                assert_eq!(
                    *permit.refusals.lock().unwrap(),
                    [Refusal::Deadline],
                    "{case}"
                );
                assert_eq!(*permit.rows.lock().unwrap(), [Progress::Prepared], "{case}");
                assert_eq!(fixture.state.lock().unwrap().writes, 0, "{case}");
                assert_eq!(
                    started.elapsed(),
                    Duration::from_millis(920 + stall),
                    "{case}"
                );
            }
        }
    }
}

#[tokio::test(start_paused = true)]
async fn the_first_exchange_and_an_earlier_slowest_are_both_charged() {
    // Silent checks after four exchanges at 920 ms plus their stalls, with
    // five left. A stall of S on the first exchange, the observation's first
    // getter, is timed from the start of the recipe: the check ends at
    // 2070 + 2S ms, so it runs at S = 1960 and is refused at 1965 against the
    // 6 s deadline. Left unmeasured, the stall would never be charged.
    for (stall, runs) in [(1960, true), (1965, false)] {
        let fixture = balanced();
        fixture.state.lock().unwrap().exchange_stall = vec![(1, Duration::from_millis(stall))];
        let permit = Paced {
            destination: Some(Destination::Preset(Preset::Silent)),
            ..Paced::default()
        };
        let started = Instant::now();
        let observed = apply(&fixture, &permit, Duration::from_secs(6)).await;
        let case = format!("{stall} ms on the first exchange");
        assert_eq!(observed.is_some(), runs, "{case}");
        if runs {
            assert_eq!(
                started.elapsed(),
                Duration::from_millis(2070 + stall),
                "{case}"
            );
        } else {
            assert_eq!(
                *permit.refusals.lock().unwrap(),
                [Refusal::Deadline],
                "{case}"
            );
            assert_eq!(fixture.state.lock().unwrap().writes, 0, "{case}");
            assert_eq!(
                started.elapsed(),
                Duration::from_millis(920 + stall),
                "{case}"
            );
        }
    }
    // 500 ms on the first exchange and 1000 ms on the third check at 2420 ms.
    // The 1230 ms exchange is the slowest and the earlier 730 ms one becomes
    // the second longest, so the check ends at 2420 + 1230 + 4 × 730 = 6570 ms
    // and the recipe is refused. Keeping 230 ms as the second longest would
    // end it at 4570 ms and let it run.
    let fixture = balanced();
    fixture.state.lock().unwrap().exchange_stall = vec![
        (1, Duration::from_millis(500)),
        (3, Duration::from_millis(1000)),
    ];
    let permit = Paced {
        destination: Some(Destination::Preset(Preset::Silent)),
        ..Paced::default()
    };
    let started = Instant::now();
    assert_eq!(apply(&fixture, &permit, Duration::from_secs(6)).await, None);
    assert_eq!(*permit.refusals.lock().unwrap(), [Refusal::Deadline]);
    assert_eq!(fixture.state.lock().unwrap().writes, 0);
    assert_eq!(started.elapsed(), Duration::from_millis(2420));
}

#[tokio::test(start_paused = true)]
async fn a_stalled_prepared_append_refuses_at_the_derived_threshold() {
    // With every exchange at 230 ms, a Prepared append of P ms checks at
    // 920 + P ms. Silent has four exchanges after its next one and five rows
    // left: it ends at 2070 + 6P ms, which must be before the 6 s deadline,
    // so it runs up to P = 654 ms and is refused at 655 ms, where the check
    // ends exactly at the deadline. Custom has ten exchanges after its next
    // one and nine rows left: it ends at 3450 + 10P ms, so it runs up to
    // P = 254 ms and is refused at 255 ms.
    for (destination, threshold) in [
        (Some(Destination::Preset(Preset::Silent)), 654),
        (Destination::custom(1, 0, false), 254),
    ] {
        for (stall, runs) in [(threshold, true), (threshold + 1, false)] {
            let fixture = balanced();
            let permit = Paced {
                destination,
                prepared_append: Duration::from_millis(stall),
                ..Paced::default()
            };
            let started = Instant::now();
            let observed = apply(&fixture, &permit, Duration::from_secs(6)).await;
            let case = format!("{destination:?} {stall} ms");
            assert_eq!(observed.is_some(), runs, "{case}");
            if runs {
                assert!(permit.refusals.lock().unwrap().is_empty(), "{case}");
            } else {
                assert_eq!(
                    *permit.refusals.lock().unwrap(),
                    [Refusal::Deadline],
                    "{case}"
                );
                assert_eq!(*permit.rows.lock().unwrap(), [Progress::Prepared], "{case}");
                assert_eq!(fixture.state.lock().unwrap().writes, 0, "{case}");
                assert_eq!(
                    started.elapsed(),
                    Duration::from_millis(920 + stall),
                    "{case}"
                );
            }
        }
    }
}

#[tokio::test(start_paused = true)]
async fn a_fan_recipe_charges_a_stalled_prepared_append_for_each_row() {
    // Fixed fans from Balanced with a Prepared append of P ms check at
    // 920 + P ms. Nine exchanges follow the next one, the spacing is 275 ms
    // and nine rows are left (two per setter and the settled row): the check
    // ends at 3265 + 10P ms, before the 6 s deadline up to P = 273 ms.
    for (stall, runs) in [(273, true), (274, false)] {
        let fixture = balanced();
        let permit = Paced {
            fan: FanIntent::checked(1, 3700),
            prepared_append: Duration::from_millis(stall),
            ..Paced::default()
        };
        let started = Instant::now();
        let observed = apply(&fixture, &permit, Duration::from_secs(6)).await;
        let case = format!("{stall} ms");
        assert_eq!(observed.is_some(), runs, "{case}");
        if runs {
            assert!(permit.refusals.lock().unwrap().is_empty(), "{case}");
            assert_eq!(fixture.state.lock().unwrap().writes, 4, "{case}");
        } else {
            assert_eq!(
                *permit.refusals.lock().unwrap(),
                [Refusal::Deadline],
                "{case}"
            );
            assert_eq!(*permit.fan_rows.lock().unwrap(), [0], "{case}");
            assert_eq!(fixture.state.lock().unwrap().writes, 0, "{case}");
            assert_eq!(
                started.elapsed(),
                Duration::from_millis(920 + stall),
                "{case}"
            );
        }
    }
}

#[tokio::test(start_paused = true)]
async fn a_slow_checkpoint_refuses_before_any_setter() {
    // On a fast device (5 ms transfers) each checkpoint authorizes twice at
    // C/2. Exchanges are timed from the previous reply, so the checkpoints
    // before each send are charged with it; timed from send to reply, these
    // recipes passed the check and then overran with setters submitted. Each
    // recipe runs at the first C below and is refused at the second.
    let silent = Some(Destination::Preset(Preset::Silent));
    let custom = Destination::custom(1, 0, false);
    let fixed = FanIntent::checked(1, 3700);
    for (name, from_custom, destination, fan, runs, refused) in [
        ("Silent", false, silent, None, 200, 210),
        ("Custom from Balanced", false, custom, None, 116, 120),
        ("Custom from Custom", true, custom, None, 100, 110),
        ("fixed fan entry", false, None, fixed, 140, 150),
    ] {
        for (checkpoint, confirmed) in [(runs, true), (refused, false)] {
            let fixture = Fixture::new();
            {
                let mut s = fixture.state.lock().unwrap();
                s.selectors = if from_custom { [4, 4] } else { [0, 0] };
                s.fans = [0, 0];
                if from_custom {
                    s.custom_levels = [0, 0];
                }
                s.latency = Some(Duration::from_millis(5));
            }
            let permit = Paced {
                destination,
                fan,
                authorization: Duration::from_micros(checkpoint * 500),
                ..Paced::default()
            };
            let observed = apply(&fixture, &permit, Duration::from_secs(6)).await;
            let case = format!("{name} at {checkpoint} ms per checkpoint");
            assert_eq!(observed.is_some(), confirmed, "{case}");
            if confirmed {
                assert!(permit.refusals.lock().unwrap().is_empty(), "{case}");
            } else {
                assert_eq!(
                    *permit.refusals.lock().unwrap(),
                    [Refusal::Deadline],
                    "{case}"
                );
                assert_eq!(fixture.state.lock().unwrap().writes, 0, "{case}");
                if fan.is_some() {
                    assert_eq!(*permit.fan_rows.lock().unwrap(), [0], "{case}");
                } else {
                    assert_eq!(*permit.rows.lock().unwrap(), [Progress::Prepared], "{case}");
                }
            }
        }
    }
}

#[tokio::test(start_paused = true)]
async fn fan_recipes_count_their_intermediate_pair_and_custom_levels() {
    // Fixed fans from Performance write its intermediate pair: 15 exchanges
    // at 230 ms take 3450 ms and the check also charges the 275 ms of
    // spacing, so the recipe is refused at a 3725 ms budget, where its check
    // ends exactly at the deadline, and runs at 3730 ms (on the 5 ms grid).
    // A return to Automatic with a saved Custom also reads the levels: 15
    // exchanges and no spacing, refused at 3450 ms and run at 3455 ms.
    // Leaving either pair out of the count would let the recipe start at the
    // refused budget.
    let custom = Destination::custom(1, 0, false);
    for (name, fans, fan, fan_destination, refused, runs) in [
        (
            "fixed entry from Performance",
            0,
            FanIntent::checked(1, 3700),
            None,
            3725,
            3730,
        ),
        (
            "return to a saved Custom",
            1,
            FanIntent::checked(0, 3700),
            custom,
            3450,
            3455,
        ),
    ] {
        for (budget, confirmed) in [(refused, false), (runs, true)] {
            let fixture = Fixture::new();
            {
                let mut s = fixture.state.lock().unwrap();
                s.selectors = [2, 2];
                s.fans = [fans, fans];
                s.custom_levels = [2, 2];
                s.latency = Some(LATENCY);
            }
            let permit = Paced {
                fan,
                fan_destination,
                ..Paced::default()
            };
            let observed = apply(&fixture, &permit, Duration::from_millis(budget)).await;
            let case = format!("{name} at {budget} ms");
            assert_eq!(observed.is_some(), confirmed, "{case}");
            if confirmed {
                assert!(permit.refusals.lock().unwrap().is_empty(), "{case}");
                assert_eq!(fixture.state.lock().unwrap().writes, 4, "{case}");
            } else {
                assert_eq!(
                    *permit.refusals.lock().unwrap(),
                    [Refusal::Deadline],
                    "{case}"
                );
                assert_eq!(*permit.fan_rows.lock().unwrap(), [0], "{case}");
                assert_eq!(fixture.state.lock().unwrap().writes, 0, "{case}");
            }
        }
    }
}

#[tokio::test(start_paused = true)]
async fn a_fan_return_charges_the_prepared_append_once_with_slow_checkpoints() {
    // Automatic from manual fans under Performance, at 5 ms transfers, 60 ms
    // checkpoints (two 30 ms authorizations) and 60 ms journal appends: the
    // recipe takes 2160 ms. Its first exchange after the Prepared append is
    // timed from after that append, so the append is charged once, with the
    // rows left. The recipe is refused with no setter below 2225 ms and runs
    // there inside its budget. Taking the append out of that exchange twice
    // would start it from 1985 ms and overrun with setters written.
    for (budget, confirmed) in [(2050, false), (2220, false), (2225, true)] {
        let fixture = Fixture::new();
        {
            let mut s = fixture.state.lock().unwrap();
            s.selectors = [2, 2];
            s.fans = [1, 1];
            s.custom_levels = [2, 2];
            s.latency = Some(Duration::from_millis(5));
        }
        let permit = Paced {
            fan: FanIntent::checked(0, 3700),
            authorization: Duration::from_millis(30),
            append: Duration::from_millis(60),
            ..Paced::default()
        };
        let started = Instant::now();
        let observed = apply(&fixture, &permit, Duration::from_millis(budget)).await;
        let case = format!("{budget} ms");
        assert_eq!(observed.is_some(), confirmed, "{case}");
        if confirmed {
            assert!(permit.refusals.lock().unwrap().is_empty(), "{case}");
            assert_eq!(fixture.state.lock().unwrap().writes, 2, "{case}");
            assert_eq!(started.elapsed(), Duration::from_millis(2160), "{case}");
        } else {
            assert_eq!(
                *permit.refusals.lock().unwrap(),
                [Refusal::Deadline],
                "{case}"
            );
            assert_eq!(*permit.fan_rows.lock().unwrap(), [0], "{case}");
            assert_eq!(fixture.state.lock().unwrap().writes, 0, "{case}");
        }
    }
}

/// A Driver-mode job's claim is charged its allowance before it is made, and
/// once it returns the job checks again with the claim's real time spent,
/// charging that time once (`DeviceModeStep::Claimed`). Every exchange takes
/// 230 ms: the baseline read ends at 230 ms, and the setter and readback need
/// 460 ms more, with the baseline's length charged again as fixed time
/// (`setter_fixed`). Within a 900 ms budget a 200 ms claim leaves the setter
/// 10 ms to spare and it is confirmed at 890 ms; charging the claim twice
/// would refuse it. A 220 ms claim overruns by 10 ms, so no setter is sent.
#[cfg(feature = "service-keyboard-driver-mode-v1")]
#[tokio::test(start_paused = true)]
async fn a_driver_mode_setter_is_sent_only_while_it_fits_after_the_claim() {
    use openblade_protocol::device_mode::DeviceMode;
    // (claim, setters sent, the job's last step, when it ends)
    for (claim, sets, step, ends) in [
        (200, 1, DeviceModeStep::Confirmed, 890),
        (220, 0, DeviceModeStep::Claimed, 450),
    ] {
        let fixture = Fixture::new();
        fixture.state.lock().unwrap().latency = Some(LATENCY);
        let claims = Arc::new(Mutex::new(Vec::new()));
        let recorded = claims.clone();
        let permit = Paced {
            job: Some(DeviceModeJob::new(
                DeviceMode::Driver,
                Some(DeviceMode::Normal),
                Duration::from_millis(25),
                move |ids| {
                    recorded.lock().unwrap().push(*ids);
                    spend(Duration::from_millis(claim));
                    Ok(())
                },
            )),
            ..Paced::default()
        };
        let started = Instant::now();
        assert_eq!(
            apply(&fixture, &permit, Duration::from_millis(900)).await,
            None
        );
        let case = format!("{claim} ms claim");
        assert_eq!(permit.job.as_ref().unwrap().step(), step, "{case}");
        assert_eq!(claims.lock().unwrap().len(), 1, "{case}");
        let s = fixture.state.lock().unwrap();
        assert_eq!(s.device_mode_sets, sets, "{case}");
        assert_eq!(s.device_mode_queries, 1 + sets, "{case}");
        assert_eq!(started.elapsed(), Duration::from_millis(ends), "{case}");
    }
}

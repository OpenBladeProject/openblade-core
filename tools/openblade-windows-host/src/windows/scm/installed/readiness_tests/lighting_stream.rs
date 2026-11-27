//! Slice L3 on exact RZ09-0581 through the installed dispatcher: the animated
//! effects on the Service's matrix stream, `stepKeyboardBrightness` and
//! `setLightingRefreshRate`. The stream is driven frame by frame
//! (`Runtime::stream_frame`) on named instants, so no test depends on how fast
//! the host runs it.
use super::*;
use crate::discovery::performance::{
    Animation, Effect, FailingRandom, MatrixReport, Random, RefreshRate, Rgb, ScriptedRandom,
    SystemRandom, stream_report,
};
use openblade_protocol::lighting::matrix::MatrixFrame;

const RENDERED: &str =
    "Lighting command completed, but this laptop does not report the resulting state.";
const PENDING: &str =
    "Lighting was not written because OpenBlade must confirm the current laptop state first.";
const SPECTRUM: &str = r#"{"effect":2,"color":{"red":0,"green":0,"blue":0},"brightness":50,"parameters":{"kind":"spectrumCycling"},"logoMode":null}"#;
const WAVE: &str = r#"{"effect":7,"color":{"red":0,"green":0,"blue":0},"brightness":50,"parameters":{"kind":"wave","direction":1},"logoMode":null}"#;
const RANDOM_BREATHING: &str = r#"{"effect":3,"color":{"red":0,"green":0,"blue":0},"brightness":50,"parameters":{"kind":"breathing","colorMode":2,"primaryColor":null,"secondaryColor":null},"logoMode":null}"#;

fn request(n: u8, settings: &str) -> Vec<u8> {
    format!(
        r#"{{"operation":"setLightingOverride","version":91,"requestId":"aaaaaaaa-bbbb-cccc-dddd-{n:012}","value":{{"value":{settings},"lifetime":1}}}}"#
    )
    .into_bytes()
}
async fn set(rig: &Rig, n: u8, settings: &str) -> (i64, String) {
    let response = rig
        .request_payload("setLightingOverride", request(n, settings), USER, SID)
        .await;
    reply(&response)
}
fn reply(response: &Value) -> (i64, String) {
    let result = &response["result"];
    (
        result["status"].as_i64().unwrap(),
        result["message"].as_str().unwrap().to_owned(),
    )
}
fn lit(rig: &Rig, lighting: [u8; 3]) {
    rig.port.state.lock().unwrap().lighting = lighting;
}
fn sent(rig: &Rig) -> Vec<(u8, bool)> {
    let s = rig.port.state.lock().unwrap();
    s.lighting_sent.iter().map(|s| (s.0, s.2)).collect()
}
/// The matrix frames submitted, decoded: the transaction byte is the
/// transport's.
fn frames(rig: &Rig) -> Vec<MatrixFrame> {
    let s = rig.port.state.lock().unwrap();
    s.matrix_frames
        .iter()
        .map(|frame| decoded(&frame.1))
        .collect()
}
fn decoded(report: &MatrixReport) -> MatrixFrame {
    openblade_protocol::lighting::matrix::decode_feature_report(report)
        .expect("a valid matrix report")
        .1
}
fn retired(rig: &Rig) -> usize {
    rig.port.state.lock().unwrap().stream_retired
}
/// The animated stream's next deadline, effect and start.
fn due(rig: &Rig) -> (Instant, Effect, Instant) {
    rig.runtime
        .stream_due_for_tests()
        .expect("an animated renderer")
}
/// Ticks of 100 ns from `started` to `at`, as managed's stopwatch counts.
fn ticks(started: Instant, at: Instant) -> i64 {
    i64::try_from(at.duration_since(started).as_nanos() / 100).unwrap()
}
/// The frame `effect` draws at `elapsed` on the default (ANSI) layout.
fn expected(effect: Effect, color: Rgb, random: Box<dyn Random>, elapsed: i64) -> MatrixFrame {
    let mut animation = Animation::new(effect, color, random).unwrap();
    decoded(&stream_report(&mut animation, 0, elapsed).unwrap())
}
const BLACK: Rgb = Rgb {
    red: 0,
    green: 0,
    blue: 0,
};

/// One origin tick's lighting step at `at`: an animated renderer's clean
/// controller reading.
async fn tick(rig: &Rig, at: Instant) {
    let maintain = rig.source.lock().await.maintain_lighting(at);
    if let Some(maintain) = maintain {
        maintain.await.unwrap();
    }
}

/// Spectrum is drawn by the request's own job, first frame included, and
/// replied as managed's setter completion; the stream then draws each frame
/// at the time elapsed since the effect started, at managed's deadlines.
/// Every frame is managed's (the effect vectors pin the math).
#[tokio::test]
async fn an_animated_effect_streams_managed_frames_at_managed_deadlines() {
    let rig = Rig::new(MemoryFiles::default());
    lit(&rig, [0x40, 1, 0]);
    assert_eq!(set(&rig, 1, SPECTRUM).await, (1, RENDERED.into()));
    let first = frames(&rig);
    assert_eq!(first.len(), 1);
    assert_eq!(
        first[0],
        expected(Effect::Spectrum, BLACK, Box::new(SystemRandom), 0)
    );
    let (next, effect, started) = due(&rig);
    assert_eq!(effect, Effect::Spectrum);
    assert_eq!(
        ticks(started, next),
        Effect::Spectrum.next_delay(0, RefreshRate::ThirtyHz)
    );
    // Not yet due: nothing is sent, and the deadline is returned.
    let early = next - Duration::from_millis(1);
    assert_eq!(rig.runtime.stream_frame(early).await.unwrap(), Some(next));
    assert_eq!(frames(&rig).len(), 1);
    // Due: the frame at that instant's elapsed time.
    let after = rig.runtime.stream_frame(next).await.unwrap().unwrap();
    let frames_now = frames(&rig);
    assert_eq!(frames_now.len(), 2);
    assert_eq!(
        frames_now[1],
        expected(
            Effect::Spectrum,
            BLACK,
            Box::new(SystemRandom),
            ticks(started, next)
        )
    );
    assert!(after > next, "the next deadline advances past the frame");
    assert_eq!(due(&rig).0, after);
    // The brightness and handshake went once, with the first frame.
    assert_eq!(sent(&rig).iter().filter(|s| s.1).count(), 2);
    rig.stop().await;
}

/// Each streamed effect is admitted by the table and continues on the
/// stream; a random palette is drawn once, from the injected source, when
/// the request starts the effect.
#[tokio::test]
async fn random_palettes_come_from_the_injected_source() {
    let rig = Rig::new(MemoryFiles::default());
    lit(&rig, [0x40, 1, 0]);
    let draws = Arc::new(AtomicUsize::new(0));
    let counted = draws.clone();
    rig.runtime.script_lighting_random(move || {
        Box::new(ScriptedRandom {
            indices: [3, 1, 4, 1, 5, 2, 6].into(),
            seed: [7; 32],
            draws: counted.clone(),
        })
    });
    assert_eq!(
        set(&rig, 1, RANDOM_BREATHING).await,
        (1, RANDOM_BREATHING_REPLY.into())
    );
    let drawn = draws.load(Ordering::Relaxed);
    assert!(drawn > 0);
    let random = Effect::Breathing {
        mode: crate::discovery::performance::ColorMode::Random,
        primary: None,
        secondary: None,
    };
    let scripted = || {
        Box::new(ScriptedRandom {
            indices: [3, 1, 4, 1, 5, 2, 6].into(),
            seed: [7; 32],
            draws: Arc::new(AtomicUsize::new(0)),
        })
    };
    assert_eq!(frames(&rig)[0], expected(random, BLACK, scripted(), 0));
    let (next, effect, _) = due(&rig);
    assert_eq!(effect, random);
    rig.runtime.stream_frame(next).await.unwrap();
    assert_eq!(frames(&rig).len(), 2);
    // Wave from the same stream, and a failed source, which managed's
    // generator throws for: the job draws the first frame before it sends
    // anything, so nothing is sent and nothing is in doubt.
    assert_eq!(set(&rig, 2, WAVE).await, (1, RENDERED.into()));
    assert_eq!(due(&rig).1, Effect::Wave { rightward: true });
    rig.runtime
        .script_lighting_random(|| Box::new(FailingRandom));
    let frames_before = frames(&rig).len();
    let setters = sent(&rig).iter().filter(|s| s.1).count();
    let (status, message) = set(&rig, 3, RANDOM_BREATHING).await;
    assert_eq!(status, 3);
    assert!(
        message.ends_with("no lighting setter was submitted."),
        "{message}"
    );
    assert_eq!(frames(&rig).len(), frames_before);
    assert_eq!(sent(&rig).iter().filter(|s| s.1).count(), setters);
    assert!(!rig.runtime.lighting_latched_for_tests());
    rig.stop().await;
}
const RANDOM_BREATHING_REPLY: &str = RENDERED;

/// Q1: the stream writes only under a clean competing-controller reading
/// from the origin tick no older than 1 s. A reading 1 s old is enough; one
/// older writes nothing, and the cadence continues without a frame until a
/// tick confirms a new one. Deliberate change from managed's per-write
/// inventory: a controller that appears between two ticks is seen at the
/// next tick, so up to one more second of frames can go.
#[tokio::test]
async fn a_clean_reading_older_than_a_second_writes_no_frame() {
    let rig = Rig::new(MemoryFiles::default());
    lit(&rig, [0x40, 1, 0]);
    let before = Instant::now();
    assert_eq!(set(&rig, 1, SPECTRUM).await.0, 1);
    let at = Instant::now();
    // A controller that appears now is not inventoried per frame.
    rig.custody.controller.store(true, Ordering::Release);
    let (next, _, _) = due(&rig);
    rig.runtime.stream_frame(next).await.unwrap();
    assert_eq!(frames(&rig).len(), 2, "within 1 s of the request's reading");
    rig.custody.controller.store(false, Ordering::Release);
    // More than 1 s after the request's reading: nothing is sent.
    let stale = before.max(at) + Duration::from_millis(1_100);
    let later = rig.runtime.stream_frame(stale).await.unwrap().unwrap();
    assert_eq!(frames(&rig).len(), 2, "a stale reading writes nothing");
    assert!(later > stale);
    // A tick at `stale` confirms a clean reading; the next frame goes, and
    // one 1 s after it still does.
    tick(&rig, stale).await;
    rig.runtime.stream_frame(later).await.unwrap();
    assert_eq!(frames(&rig).len(), 3);
    let (next, _, _) = due(&rig);
    let edge = stale + Duration::from_secs(1);
    assert!(next <= edge);
    rig.runtime.stream_frame(edge).await.unwrap();
    assert_eq!(frames(&rig).len(), 4, "a reading exactly 1 s old admits");
    // A tick whose capture is refused for any other reason leaves no clean
    // reading.
    rig.custody.fresh_refuse.store(true, Ordering::Release);
    let refused = edge + Duration::from_millis(100);
    tick(&rig, refused).await;
    rig.custody.fresh_refuse.store(false, Ordering::Release);
    let (next, _, _) = due(&rig);
    rig.runtime.stream_frame(next.max(refused)).await.unwrap();
    assert_eq!(frames(&rig).len(), 4, "no clean reading, no frame");
    assert!(rig.runtime.stream_due_for_tests().is_some(), "still owned");
    rig.stop().await;
}

/// A competing controller in the origin tick's inventory retires the
/// animated renderer without a write, as managed's controller refresh
/// relinquishes lighting: the stream sends nothing more.
#[tokio::test]
async fn a_competing_controller_retires_the_stream_without_a_write() {
    let rig = Rig::new(MemoryFiles::default());
    lit(&rig, [0x40, 1, 0]);
    assert_eq!(set(&rig, 1, SPECTRUM).await.0, 1);
    let reports = sent(&rig).len();
    rig.custody.controller.store(true, Ordering::Release);
    tick(&rig, Instant::now()).await;
    assert_eq!(rig.runtime.stream_due_for_tests(), None);
    assert_eq!(retired(&rig), 1, "the stream's matrix lane is released");
    assert_eq!(
        rig.runtime
            .stream_frame(Instant::now() + Duration::from_secs(1))
            .await
            .unwrap(),
        None
    );
    assert_eq!((frames(&rig).len(), sent(&rig).len()), (1, reports));
    assert!(!rig.runtime.lighting_latched_for_tests());
    rig.stop().await;
}

/// A frame that may have been submitted without completing latches the
/// doubt and ends the stream; the next request resolves read-only. One
/// refused before submission ends the stream without the latch.
#[tokio::test]
async fn an_uncertain_frame_latches_and_a_refused_one_stops_the_stream() {
    let rig = Rig::new(MemoryFiles::default());
    lit(&rig, [0x40, 1, 0]);
    assert_eq!(set(&rig, 1, SPECTRUM).await.0, 1);
    rig.port.state.lock().unwrap().matrix_refuse = Some(2);
    let (next, _, _) = due(&rig);
    assert_eq!(rig.runtime.stream_frame(next).await.unwrap(), None);
    assert_eq!(frames(&rig).len(), 1, "refused before submission");
    assert_eq!(rig.runtime.stream_due_for_tests(), None);
    assert!(!rig.runtime.lighting_latched_for_tests());
    assert_eq!(set(&rig, 2, SPECTRUM).await, (1, RENDERED.into()));
    assert_eq!(frames(&rig).len(), 2, "drawn anew");
    rig.port.state.lock().unwrap().matrix_fail = Some(3);
    let (next, _, _) = due(&rig);
    assert_eq!(rig.runtime.stream_frame(next).await.unwrap(), None);
    assert_eq!(frames(&rig).len(), 3);
    assert!(rig.runtime.lighting_latched_for_tests());
    assert_eq!(retired(&rig), 2);
    let setters = sent(&rig).iter().filter(|s| s.1).count();
    assert_eq!(set(&rig, 3, SPECTRUM).await, (2, PENDING.into()));
    assert_eq!(
        sent(&rig).iter().filter(|s| s.1).count(),
        setters,
        "the resolution writes nothing"
    );
    assert_eq!(frames(&rig).len(), 3, "nothing is replayed");
    rig.stop().await;
}

/// While a request or the origin tick holds the operation gate, the frame
/// is skipped and tried again one interval later: a visible pause, which
/// managed's stream (outside its operation gate) does not have.
#[tokio::test]
async fn a_held_gate_skips_the_frame_one_interval_later_divergence() {
    let rig = Rig::new(MemoryFiles::default());
    lit(&rig, [0x40, 1, 0]);
    assert_eq!(set(&rig, 1, SPECTRUM).await.0, 1);
    let (next, _, _) = due(&rig);
    let interval =
        Duration::from_nanos(u64::try_from(RefreshRate::ThirtyHz.interval()).unwrap() * 100);
    {
        let _held = rig.runtime.hold_lighting_gate_for_tests();
        assert_eq!(
            rig.runtime.stream_frame(next).await.unwrap(),
            Some(next + interval)
        );
    }
    assert_eq!(frames(&rig).len(), 1);
    assert_eq!(due(&rig).0, next, "the deadline is not consumed");
    rig.runtime.stream_frame(next + interval).await.unwrap();
    assert_eq!(frames(&rig).len(), 2);
    rig.stop().await;
}

/// An animated request refused at any checkpoint of its job leaves no
/// stream drawing over what it may have changed, and never replays: a
/// possible submission latches, and no Spectrum stream survives a refused
/// Wave request unless the Wave job sent nothing.
#[tokio::test]
async fn an_animated_request_refused_mid_sequence_leaves_no_stale_stream() {
    for at in 1..200 {
        let rig = Rig::new(MemoryFiles::default());
        lit(&rig, [0x40, 1, 0]);
        assert_eq!(set(&rig, 1, SPECTRUM).await.0, 1);
        let before = (sent(&rig).len(), frames(&rig).len());
        rig.custody.refuse_at.store(at, Ordering::Release);
        let (status, message) = set(&rig, 2, WAVE).await;
        rig.custody.refuse_at.store(0, Ordering::Release);
        rig.custody.fresh_refuse.store(false, Ordering::Release);
        if status == 1 {
            assert_eq!(due(&rig).1, Effect::Wave { rightward: true });
            rig.stop().await;
            assert!(at > 1, "every checkpoint was exercised");
            return;
        }
        let wrote = sent(&rig).len() > before.0 && sent(&rig)[before.0..].iter().any(|s| s.1)
            || frames(&rig).len() > before.1;
        if let Some((_, effect, _)) = rig.runtime.stream_due_for_tests() {
            assert_eq!(effect, Effect::Spectrum, "{at}: {message}");
            assert!(
                !wrote,
                "{at}: a stream kept over a changed keyboard: {message}"
            );
        }
        if rig.runtime.lighting_latched_for_tests() {
            let counted = (sent(&rig).len(), frames(&rig).len());
            assert_eq!(set(&rig, 3, WAVE).await, (2, PENDING.into()), "{at}");
            assert_eq!(frames(&rig).len(), counted.1, "{at}: nothing is replayed");
            assert!(
                sent(&rig)[counted.0..].iter().all(|s| !s.1),
                "{at}: the resolution writes nothing"
            );
        }
        rig.stop().await;
    }
    panic!("the Wave request never completed");
}

/// A suspend during an animated effect ends the stream at its next frame
/// without a write, and nothing is drawn after the resume.
#[cfg(feature = "service-profile-triggers-v1")]
#[tokio::test]
async fn a_low_power_boundary_retires_the_stream_without_a_write() {
    use windows_sys::Win32::System::Services::SERVICE_CONTROL_POWEREVENT as POWER;
    const SUSPEND: u32 = 4;
    const RESUME: u32 = 18;
    let rig = Rig::new(MemoryFiles::default());
    lit(&rig, [0x40, 1, 0]);
    let custody = rig.custody.clone();
    rig.runtime
        .read_low_power(move || custody.lifecycle.low_power());
    assert_eq!(set(&rig, 1, SPECTRUM).await.0, 1);
    let reports = sent(&rig).len();
    let (next, _, _) = due(&rig);
    rig.custody.scoped_fresh.store(true, Ordering::Release);
    rig.custody.lifecycle.ingress(POWER, SUSPEND);
    assert_eq!(rig.runtime.stream_frame(next).await.unwrap(), None);
    assert_eq!((frames(&rig).len(), sent(&rig).len()), (1, reports));
    assert_eq!(retired(&rig), 1);
    rig.custody.lifecycle.ingress(POWER, RESUME);
    rig.custody.scoped_fresh.store(false, Ordering::Release);
    assert_eq!(rig.runtime.stream_frame(next).await.unwrap(), None);
    assert_eq!(
        frames(&rig).len(),
        1,
        "nothing owns the matrix after resume"
    );
    rig.stop().await;
}

/// `stepKeyboardBrightness` and `setLightingRefreshRate` decode only
/// managed's defined values.
#[test]
fn undefined_step_and_refresh_values_are_refused_by_the_codec() {
    use crate::service_status::StatusRequest;
    for request in [
        json!({"operation":"stepKeyboardBrightness","version":91,"requestId":"cccccccc-bbbb-cccc-dddd-000000000001","direction":2,"lifetime":0}),
        json!({"operation":"stepKeyboardBrightness","version":91,"requestId":"cccccccc-bbbb-cccc-dddd-000000000002","direction":0,"lifetime":2}),
        json!({"operation":"stepKeyboardBrightness","version":91,"requestId":"cccccccc-bbbb-cccc-dddd-000000000003","direction":-1,"lifetime":1}),
        json!({"operation":"setLightingRefreshRate","version":91,"requestId":"cccccccc-bbbb-cccc-dddd-000000000004","refreshRate":2}),
        json!({"operation":"setLightingRefreshRate","version":91,"requestId":"cccccccc-bbbb-cccc-dddd-000000000005","refreshRate":"60"}),
    ] {
        assert!(
            StatusRequest::decode(&serde_json::to_vec(&request).unwrap()).is_err(),
            "{request}"
        );
    }
    for request in [
        json!({"operation":"stepKeyboardBrightness","version":91,"requestId":"cccccccc-bbbb-cccc-dddd-000000000006","direction":1,"lifetime":1}),
        json!({"operation":"setLightingRefreshRate","version":91,"requestId":"cccccccc-bbbb-cccc-dddd-000000000007","refreshRate":1}),
    ] {
        let decoded = StatusRequest::decode(&serde_json::to_vec(&request).unwrap());
        assert_eq!(
            decoded.is_ok(),
            cfg!(feature = "service-profiles-v1"),
            "{request}"
        );
    }
}

#[cfg(feature = "service-profiles-v1")]
mod controls {
    use super::*;

    const STATIC: &str = r#"{"effect":1,"color":{"red":200,"green":10,"blue":5},"brightness":10,"parameters":{"kind":"static","color":{"red":200,"green":10,"blue":5}},"logoMode":null}"#;

    async fn step(rig: &Rig, n: u8, increase: bool, lifetime: u8) -> (i64, String) {
        let response = rig
            .request(json!({
                "operation": "stepKeyboardBrightness",
                "version": 91,
                "requestId": format!("dddddddd-bbbb-cccc-dddd-{n:012}"),
                "direction": u8::from(increase),
                "lifetime": lifetime,
            }))
            .await;
        reply(&response)
    }
    async fn rate(rig: &Rig, n: u8, rate: u8) -> (i64, String) {
        let response = rig
            .request(json!({
                "operation": "setLightingRefreshRate",
                "version": 91,
                "requestId": format!("eeeeeeee-bbbb-cccc-dddd-{n:012}"),
                "refreshRate": rate,
            }))
            .await;
        reply(&response)
    }
    /// The lighting override in effect, as status publishes it.
    async fn override_member(rig: &Rig) -> Value {
        rig.get().await["status"]["lightingOverride"].clone()
    }
    fn brightness(rig: &Rig) -> u8 {
        rig.port.state.lock().unwrap().lighting[0]
    }

    /// Managed's ±0x10 raw step on a lit keyboard: the 03/03 fast setter with
    /// no readback, the first step reading 03/83 and the next reusing the
    /// raw it set. Each step saves the override with the request's lifetime
    /// and replies the rounded percent. At zero the keyboard is Off with its
    /// logo; a decrease from Off replies managed's minimum and an increase
    /// draws managed's visible baseline at 6%.
    #[tokio::test]
    async fn steps_saturate_save_and_wake_from_off() {
        let rig = Rig::new(MemoryFiles::default());
        lit(&rig, [0x40, 1, 0]);
        assert_eq!(set(&rig, 1, STATIC).await, (1, RENDERED.into()));
        assert_eq!(brightness(&rig), 0x1a);
        let reports = sent(&rig).len();
        assert_eq!(
            step(&rig, 2, true, 1).await,
            (1, "Keyboard brightness changed to 16%.".into())
        );
        assert_eq!(brightness(&rig), 0x2a);
        assert_eq!(sent(&rig)[reports..], [(0x83, false), (0x03, true)]);
        assert_eq!(override_member(&rig).await["value"]["brightness"], 16);
        assert_eq!(override_member(&rig).await["value"]["effect"], 1);
        let reports = sent(&rig).len();
        assert_eq!(
            step(&rig, 3, false, 1).await,
            (1, "Keyboard brightness changed to 10%.".into())
        );
        assert_eq!(sent(&rig)[reports..], [(0x03, true)], "the raw is reused");
        assert_eq!(
            step(&rig, 4, false, 1).await,
            (1, "Keyboard brightness changed to 4%.".into())
        );
        assert_eq!(
            step(&rig, 5, false, 1).await,
            (1, "Keyboard brightness changed to 0%.".into())
        );
        assert_eq!(brightness(&rig), 0);
        assert_eq!(override_member(&rig).await["value"]["effect"], 0);
        assert_eq!(
            rig.runtime.keepalive_due_for_tests(),
            None,
            "no renderer at zero"
        );
        let reports = sent(&rig).len();
        assert_eq!(
            step(&rig, 6, false, 1).await,
            (1, "Keyboard brightness is already at its minimum.".into())
        );
        assert_eq!(sent(&rig).len(), reports, "nothing is read or written");
        // Increase from Off: LastKnownLighting is the Off just stepped to, so
        // the baseline is the default visible color.
        assert_eq!(step(&rig, 7, true, 1).await, (1, RENDERED.into()));
        let value = override_member(&rig).await["value"].clone();
        assert_eq!(value["effect"], 1);
        assert_eq!(value["brightness"], 6);
        assert_eq!(value["color"], json!({"red":71,"green":225,"blue":12}));
        assert_eq!(brightness(&rig), 0x10, "6% is raw 0x10 (managed rounds up)");
        rig.stop().await;
    }

    /// The top step saturates at 0xFF and replies 100%; a step that leaves
    /// the value unchanged writes nothing.
    #[tokio::test]
    async fn a_step_at_the_top_saturates_and_then_writes_nothing() {
        let rig = Rig::new(MemoryFiles::default());
        lit(&rig, [0x40, 1, 0]);
        assert_eq!(set(&rig, 1, STATIC).await.0, 1);
        rig.port.state.lock().unwrap().lighting[0] = 0xf8;
        assert_eq!(
            step(&rig, 2, true, 0).await,
            (1, "Keyboard brightness changed to 100%.".into())
        );
        assert_eq!(brightness(&rig), 0xff);
        assert_eq!(override_member(&rig).await["lifetime"], 0);
        let setters = sent(&rig).iter().filter(|s| s.1).count();
        assert_eq!(
            step(&rig, 3, true, 0).await,
            (1, "Keyboard brightness changed to 100%.".into())
        );
        assert_eq!(sent(&rig).iter().filter(|s| s.1).count(), setters);
        rig.stop().await;
    }

    /// A step setter whose acknowledgement is lost latches the doubt: the
    /// next step resolves read-only and replays nothing, and no override is
    /// saved for the uncertain value.
    #[tokio::test]
    async fn an_uncertain_step_write_latches_without_saving() {
        let rig = Rig::new(MemoryFiles::default());
        lit(&rig, [0x40, 1, 0]);
        assert_eq!(set(&rig, 1, STATIC).await.0, 1);
        let setters = sent(&rig).iter().filter(|s| s.1).count();
        rig.port.state.lock().unwrap().lighting_timeout = Some(setters + 1);
        let (status, _) = step(&rig, 2, true, 1).await;
        assert_eq!(status, 3);
        assert_eq!(override_member(&rig).await["value"]["brightness"], 10);
        assert!(rig.runtime.lighting_latched_for_tests());
        let counted = sent(&rig).len();
        assert_eq!(step(&rig, 3, true, 1).await, (2, PENDING.into()));
        assert!(
            sent(&rig)[counted..].iter().all(|s| !s.1),
            "nothing is replayed"
        );
        rig.stop().await;
    }

    /// A refresh change saves managed's preference and retimes the stream
    /// from the effect's current phase. A change requested while a frame is
    /// in flight waits for that frame on the operation gate.
    #[tokio::test]
    async fn a_refresh_change_during_a_frame_waits_and_retimes_the_stream() {
        let rig = Rig::new(MemoryFiles::default());
        lit(&rig, [0x40, 1, 0]);
        assert_eq!(set(&rig, 1, SPECTRUM).await.0, 1);
        assert_eq!(rig.runtime.lighting_rate_for_tests(), RefreshRate::ThirtyHz);
        let release = Arc::new(AtomicBool::new(false));
        rig.port.state.lock().unwrap().stream_held = Some(release.clone());
        let (next, _, started) = due(&rig);
        let replied = std::cell::Cell::new(false);
        let frame = rig.runtime.stream_frame(next);
        let change = async {
            let reply = rate(&rig, 2, 1).await;
            replied.set(true);
            reply
        };
        let watch = async {
            while frames(&rig).len() < 2 {
                tokio::task::yield_now().await;
            }
            for _ in 0..50 {
                tokio::task::yield_now().await;
            }
            assert!(!replied.get(), "the change waited for the frame");
            release.store(true, Ordering::Release);
        };
        let (written, change, ()) = tokio::join!(frame, change, watch);
        assert!(written.unwrap().is_some(), "the frame completed");
        assert_eq!(change, (1, "Keyboard lighting is running at 60 Hz.".into()));
        assert!(!rig.runtime.lighting_latched_for_tests());
        assert_eq!(rig.runtime.lighting_rate_for_tests(), RefreshRate::SixtyHz);
        let (retimed, _, same) = due(&rig);
        assert_eq!(same, started, "the effect's phase is kept");
        assert!(retimed > next);
        rig.port.state.lock().unwrap().stream_held = None;
        // The saved preference survives as managed's, and unchanged is Applied.
        assert_eq!(
            rate(&rig, 3, 1).await,
            (1, "Keyboard lighting is running at 60 Hz.".into())
        );
        assert_eq!(
            rate(&rig, 4, 0).await,
            (1, "Keyboard lighting is running at 30 Hz.".into())
        );
        rig.stop().await;
    }

    /// An applied lighting request is remembered as managed's
    /// `LastKnownLighting`; a stored Off that was not applied leaves it, so a
    /// step from that Off wakes the remembered effect at 6%.
    #[tokio::test]
    async fn an_applied_effect_is_remembered_for_a_wake_from_off() {
        const OFF: &str = r#"{"effect":0,"color":{"red":0,"green":0,"blue":0},"brightness":0,"parameters":{"kind":"off"},"logoMode":null}"#;
        let rig = Rig::new(MemoryFiles::default());
        lit(&rig, [0x40, 1, 0]);
        assert_eq!(set(&rig, 1, SPECTRUM).await.0, 1);
        // Too little time for the Off recipe: stored, nothing written, and
        // nothing remembered.
        rig.port.state.lock().unwrap().latency = Some(Duration::from_millis(400));
        assert_eq!(set(&rig, 2, OFF).await.0, 3);
        rig.port.state.lock().unwrap().latency = None;
        assert_eq!(override_member(&rig).await["value"]["effect"], 0);
        assert_eq!(step(&rig, 3, true, 1).await, (1, RENDERED.into()));
        let value = override_member(&rig).await["value"].clone();
        assert_eq!(value["effect"], 2);
        assert_eq!(value["brightness"], 6);
        assert_eq!(due(&rig).1, Effect::Spectrum);
        rig.stop().await;
    }
}

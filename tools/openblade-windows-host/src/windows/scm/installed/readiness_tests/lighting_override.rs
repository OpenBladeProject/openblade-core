//! `setLightingOverride` through the installed dispatcher on exact RZ09-0581,
//! slices L1 and L2: managed's store and reply for every gate, the keyboard
//! Off and logo writes with readback, the Static renderer and its keepalive,
//! and stop-on-ambiguity.
use super::*;

const OFF: &str = r#"{"effect":0,"color":{"red":0,"green":0,"blue":0},"brightness":0,"parameters":{"kind":"off"},"logoMode":2}"#;
const STATIC: &str = r#"{"effect":1,"color":{"red":200,"green":10,"blue":5},"brightness":10,"parameters":{"kind":"static","color":{"red":200,"green":10,"blue":5}},"logoMode":null}"#;
const STORED: &str = "Lighting override intent was stored. Hardware result: ";
const CONFIRMED: &str = "Lighting setting applied and confirmed by readback.";
const PENDING: &str =
    "Lighting was not written because OpenBlade must confirm the current laptop state first.";

/// The request as managed's client writes it: a `Value` would sort `kind`
/// out of first place in `parameters`, which managed's polymorphic binding
/// refuses.
fn request(n: u8, settings: Option<&str>, lifetime: u8) -> Vec<u8> {
    let value = settings.map_or("null".to_owned(), |settings| {
        format!(r#"{{"value":{settings},"lifetime":{lifetime}}}"#)
    });
    format!(
        r#"{{"operation":"setLightingOverride","version":91,"requestId":"aaaaaaaa-bbbb-cccc-dddd-{n:012}","value":{value}}}"#
    )
    .into_bytes()
}
async fn set(rig: &Rig, n: u8, settings: Option<&str>, lifetime: u8) -> (i64, String) {
    let response = rig
        .request_payload(
            "setLightingOverride",
            request(n, settings, lifetime),
            USER,
            SID,
        )
        .await;
    let result = &response["result"];
    (
        result["status"].as_i64().unwrap(),
        result["message"].as_str().unwrap().to_owned(),
    )
}
fn lit(rig: &Rig, lighting: [u8; 3]) {
    rig.port.state.lock().unwrap().lighting = lighting;
}
/// The lighting reports the job sent, as (command, mutation).
fn sent(rig: &Rig) -> Vec<(u8, bool)> {
    let s = rig.port.state.lock().unwrap();
    s.lighting_sent.iter().map(|s| (s.0, s.2)).collect()
}
fn setters(rig: &Rig) -> usize {
    sent(rig).iter().filter(|s| s.1).count()
}
/// The saved lighting member exactly as written, member order included.
fn saved(rig: &Rig) -> String {
    let bytes = rig.files.read("manual-overrides.json").unwrap().unwrap();
    let text = String::from_utf8(bytes).unwrap();
    let start = text.find(r#""lighting":"#).unwrap() + r#""lighting":"#.len();
    // The member's own bytes: an object keeps its written order.
    let mut depth = 0;
    for (i, c) in text[start..].char_indices() {
        match c {
            '{' => depth += 1,
            '}' => {
                depth -= 1;
                if depth == 0 {
                    return text[start..start + i + 1].to_owned();
                }
            }
            ',' if depth == 0 => return text[start..start + i].to_owned(),
            _ => {}
        }
    }
    panic!("unterminated lighting member: {text}")
}

/// Managed's reply to a lit effect: the keyboard reports no lit state, so
/// the setter readbacks are the completion evidence.
const RENDERED: &str =
    "Lighting command completed, but this laptop does not report the resulting state.";
const SPECTRUM: &str = r#"{"effect":2,"color":{"red":0,"green":0,"blue":0},"brightness":50,"parameters":{"kind":"spectrumCycling"},"logoMode":null}"#;
const STATIC_GREEN: &str = r#"{"effect":1,"color":{"red":71,"green":225,"blue":12},"brightness":10,"parameters":{"kind":"static","color":{"red":71,"green":225,"blue":12}},"logoMode":null}"#;

/// The matrix frames submitted.
fn frames(rig: &Rig) -> usize {
    rig.port.state.lock().unwrap().matrix_frames.len()
}

/// The keepalive step of one origin tick at `at`, run as a request's
/// exchange is (`Rig::held`). Each test names its tick instants on the test
/// clock, so real time passing between steps moves no keepalive.
async fn keepalive(rig: &Rig, at: Instant) {
    rig.held(
        HeldClock::Keep,
        Box::pin(async {
            let maintain = rig.source.lock().await.maintain_lighting(at);
            match maintain {
                Some(maintain) => maintain.await,
                None => Ok(()),
            }
        }),
    )
    .await
    .unwrap();
}

/// Runs `request` with one origin tick's keepalive step, due at `at`,
/// arriving while the request's `held`th lighting setter is in flight (the
/// tick loop and requests are separate local tasks). The tick must return at
/// once, starting no job behind the request's; the setter is released after
/// it.
async fn with_tick_during(
    rig: &Rig,
    held: usize,
    at: Instant,
    request: impl std::future::Future<Output = (i64, String)>,
) -> (i64, String) {
    use std::task::Poll;
    let release = Arc::new(AtomicBool::new(false));
    rig.port.state.lock().unwrap().lighting_held = Some((held, release.clone()));
    let tick = async {
        while setters(rig) < held {
            tokio::time::sleep(Duration::from_millis(5)).await;
        }
        let maintain = rig.source.lock().await.maintain_lighting(at);
        let returned = match maintain {
            Some(mut maintain) => std::future::poll_fn(|cx| {
                Poll::Ready(maintain.as_mut().poll(cx).map(|result| result.unwrap()))
            })
            .await
            .is_ready(),
            None => true,
        };
        release.store(true, Ordering::Release);
        assert!(returned, "the tick waited behind the request's job");
    };
    let (reply, ()) = tokio::join!(request, tick);
    rig.port.state.lock().unwrap().lighting_held = None;
    reply
}

/// Static is drawn by the Service's renderer in managed's order (the
/// 00/86 layout once a session, the brightness, the 00/84 read and the
/// 03/0A:0500 handshake, then the frame), stored first, and replied as
/// managed's setter completion. The lit state is never published as
/// confirmed.
#[tokio::test]
async fn static_is_rendered_and_replied_as_setter_completion() {
    let rig = Rig::new(MemoryFiles::default());
    lit(&rig, [0x40, 1, 0]);
    let (status, message) = set(&rig, 1, Some(STATIC), 1).await;
    assert_eq!((status, message.as_str()), (1, RENDERED));
    assert_eq!(saved(&rig), STATIC);
    assert_eq!(frames(&rig), 1);
    assert_eq!(
        sent(&rig),
        [
            (0x83, false),
            (0x86, false),
            (0x03, true),
            (0x83, false),
            (0x0a, true)
        ]
    );
    // 10% is raw 0x1A.
    assert_eq!(rig.port.state.lock().unwrap().lighting[0], 0x1a);
    let status = rig.get().await;
    assert_eq!(status["status"]["confirmedState"]["lighting"], Value::Null);
    assert_eq!(
        status["status"]["lightingOverride"]["value"]["parameters"]["kind"],
        "static"
    );
    // The same color retains the renderer and draws nothing; the session
    // layout byte is not read again.
    assert_eq!(set(&rig, 2, Some(STATIC), 1).await, (1, RENDERED.into()));
    assert_eq!(frames(&rig), 1);
    assert_eq!(sent(&rig).len(), 5 + 1);
    // Another color draws a new frame after its own handshake.
    assert_eq!(
        set(&rig, 3, Some(STATIC_GREEN), 1).await,
        (1, RENDERED.into())
    );
    assert_eq!(frames(&rig), 2);
    assert_eq!(
        sent(&rig)[6..],
        [(0x83, false), (0x0a, true)],
        "no second 00/86"
    );
    rig.stop().await;
}

/// Managed stores the intent and refuses an effect its capability table
/// lacks. Native admits Off and Static; the animated effects wait for slice
/// C, so each gets that refusal, with nothing sent.
#[tokio::test]
async fn animated_effects_are_refused_until_slice_c_divergence() {
    let rig = Rig::new(MemoryFiles::default());
    let (status, message) = set(&rig, 1, Some(SPECTRUM), 1).await;
    assert_eq!(status, 2);
    assert_eq!(
        message,
        format!("{STORED}Lighting value is not supported by the verified capability table.")
    );
    assert!(sent(&rig).is_empty());
    assert_eq!(frames(&rig), 0);
    assert_eq!(saved(&rig), SPECTRUM);
    let status = rig.get().await;
    assert_eq!(status["status"]["lightingOverride"]["lifetime"], 1);
    assert_eq!(status["status"]["desiredState"]["lighting"]["source"], 4);
    rig.stop().await;
}

/// When the owned renderer's next keepalive frame is due.
fn due(rig: &Rig) -> Instant {
    rig.runtime
        .keepalive_due_for_tests()
        .expect("an owned renderer")
}

/// The owned renderer's keepalive frame goes every 5 s on the grid the
/// drawing request's completion started: none before it is due, one when it
/// is, and one for a late tick, which skips the instants it missed. Every
/// tick names its instant on that grid, so the test does not depend on how
/// fast the host runs it.
#[tokio::test]
async fn the_keepalive_frame_goes_every_five_seconds() {
    let rig = Rig::new(MemoryFiles::default());
    lit(&rig, [0x40, 1, 0]);
    let before = Instant::now();
    assert_eq!(set(&rig, 1, Some(STATIC), 1).await.0, 1);
    let first = due(&rig);
    assert!(
        first >= before + Duration::from_secs(5),
        "the grid starts at the reply"
    );
    assert_eq!(frames(&rig), 1);
    let at = |ms: u64| first + Duration::from_millis(ms);
    keepalive(&rig, first - Duration::from_millis(1)).await;
    assert_eq!(frames(&rig), 1, "not yet due");
    keepalive(&rig, at(0)).await;
    assert_eq!(frames(&rig), 2);
    assert_eq!(due(&rig), at(5_000));
    keepalive(&rig, at(0)).await;
    keepalive(&rig, at(4_999)).await;
    assert_eq!(frames(&rig), 2, "the next is due 5 s later");
    keepalive(&rig, at(5_000)).await;
    assert_eq!(frames(&rig), 3);
    // A tick 7 s late sends one frame, not the two it missed, and the next
    // is due on the same grid.
    keepalive(&rig, at(17_000)).await;
    keepalive(&rig, at(17_000)).await;
    assert_eq!(frames(&rig), 4);
    assert_eq!(due(&rig), at(20_000));
    keepalive(&rig, at(19_999)).await;
    assert_eq!(frames(&rig), 4);
    keepalive(&rig, at(20_000)).await;
    assert_eq!(frames(&rig), 5);
    // Each keepalive is a frame alone.
    assert_eq!(sent(&rig).len(), 5);
    rig.stop().await;
}

/// A keepalive refused before submission keeps the renderer for the next
/// instant; one that failed after submission ends it in doubt, so the next
/// request resolves read-only and then draws anew.
#[tokio::test]
async fn a_failed_keepalive_ends_the_renderer_in_doubt() {
    let rig = Rig::new(MemoryFiles::default());
    lit(&rig, [0x40, 1, 0]);
    assert_eq!(set(&rig, 1, Some(STATIC), 1).await.0, 1);
    let done = Instant::now();
    let at = |ms: u64| done + Duration::from_millis(ms);
    rig.port.state.lock().unwrap().matrix_refuse = Some(2);
    keepalive(&rig, at(5_500)).await;
    assert_eq!(frames(&rig), 1, "refused before submission");
    keepalive(&rig, at(10_500)).await;
    assert_eq!(frames(&rig), 2, "the renderer was kept");
    rig.port.state.lock().unwrap().matrix_fail = Some(3);
    keepalive(&rig, at(15_500)).await;
    assert_eq!(frames(&rig), 3);
    keepalive(&rig, at(20_500)).await;
    assert_eq!(frames(&rig), 3, "nothing owns the matrix");
    let before = setters(&rig);
    assert_eq!(set(&rig, 2, Some(STATIC), 1).await, (2, PENDING.into()));
    assert_eq!(setters(&rig), before, "the resolution writes nothing");
    assert_eq!(set(&rig, 3, Some(STATIC), 1).await.0, 1);
    assert_eq!(frames(&rig), 4, "a new renderer, not a retained one");
    rig.stop().await;
}

/// A request whose frame may have been submitted unconfirmed ends the
/// renderer it replaced: after the resolution no keepalive draws the old
/// color, and the old color is drawn anew rather than retained.
#[tokio::test]
async fn an_unconfirmed_frame_leaves_no_renderer() {
    let rig = Rig::new(MemoryFiles::default());
    lit(&rig, [0x40, 1, 0]);
    assert_eq!(set(&rig, 1, Some(STATIC), 1).await.0, 1);
    rig.port.state.lock().unwrap().matrix_fail = Some(2);
    assert_eq!(set(&rig, 2, Some(STATIC_GREEN), 1).await.0, 3);
    assert_eq!(set(&rig, 3, Some(STATIC), 1).await, (2, PENDING.into()));
    keepalive(&rig, Instant::now() + Duration::from_secs(6)).await;
    assert_eq!(frames(&rig), 2, "no renderer survives a latched doubt");
    assert_eq!(set(&rig, 4, Some(STATIC), 1).await.0, 1);
    assert_eq!(frames(&rig), 3, "the old color is drawn, not retained");
    rig.stop().await;
}

/// A tick that arrives while a request holds the operation gate leaves the
/// session to it. A retained Static at a new brightness replies Applied
/// and keeps its keepalive.
#[tokio::test]
async fn a_tick_during_a_retaining_request_keeps_the_keepalive() {
    const BRIGHT: &str = r#"{"effect":1,"color":{"red":200,"green":10,"blue":5},"brightness":50,"parameters":{"kind":"static","color":{"red":200,"green":10,"blue":5}},"logoMode":null}"#;
    let rig = Rig::new(MemoryFiles::default());
    lit(&rig, [0x40, 1, 0]);
    assert_eq!(set(&rig, 1, Some(STATIC), 1).await.0, 1);
    let late = Instant::now() + Duration::from_secs(6);
    let held = setters(&rig) + 1;
    let (status, message) = with_tick_during(&rig, held, late, set(&rig, 2, Some(BRIGHT), 1)).await;
    assert_eq!((status, message.as_str()), (1, RENDERED));
    assert_eq!(frames(&rig), 1, "retained: no new frame, and no tick frame");
    keepalive(&rig, late).await;
    assert_eq!(frames(&rig), 2, "the retained renderer keeps its keepalive");
    rig.stop().await;
}

/// An Off whose brightness readback reports the lit value restores its
/// baseline, so the keyboard stays lit and the renderer is kept; a tick
/// during it leaves the keepalive running.
#[tokio::test]
async fn a_tick_during_an_off_that_restores_keeps_the_keepalive() {
    let rig = Rig::new(MemoryFiles::default());
    lit(&rig, [0x40, 1, 0]);
    assert_eq!(set(&rig, 1, Some(STATIC), 1).await.0, 1);
    let late = Instant::now() + Duration::from_secs(6);
    let held = setters(&rig) + 1;
    rig.port.state.lock().unwrap().lighting_ignore = vec![held];
    let (status, _) = with_tick_during(&rig, held, late, set(&rig, 2, Some(OFF), 1)).await;
    rig.port.state.lock().unwrap().lighting_ignore.clear();
    assert_eq!(status, 3);
    assert_eq!(rig.port.state.lock().unwrap().lighting[0], 0x1a);
    keepalive(&rig, late).await;
    assert_eq!(
        frames(&rig),
        2,
        "the renderer of a lit keyboard keeps its keepalive"
    );
    rig.stop().await;
}

/// A request dropped after it drew a new frame latches the doubt and ends
/// the renderer it replaced, so the old renderer's keepalive cannot draw
/// over the newer frame, even when the resolution clears the latch before
/// the next tick.
#[tokio::test]
async fn a_dropped_request_ends_the_renderer_it_replaced() {
    const GREEN_BREATHING_LOGO: &str = r#"{"effect":1,"color":{"red":71,"green":225,"blue":12},"brightness":10,"parameters":{"kind":"static","color":{"red":71,"green":225,"blue":12}},"logoMode":1}"#;
    let rig = Rig::new(MemoryFiles::default());
    lit(&rig, [0x1a, 1, 0]);
    assert_eq!(set(&rig, 1, Some(STATIC), 1).await.0, 1);
    // The green request's handshake, then its first logo setter, held.
    let held = setters(&rig) + 2;
    let release = Arc::new(AtomicBool::new(false));
    rig.port.state.lock().unwrap().lighting_held = Some((held, release.clone()));
    let mut request = Box::pin(set(&rig, 2, Some(GREEN_BREATHING_LOGO), 1));
    tokio::select! {
        _ = &mut request => panic!("the held setter completed"),
        _ = async {
            while setters(&rig) < held {
                tokio::time::sleep(Duration::from_millis(5)).await;
            }
        } => {}
    }
    assert_eq!(frames(&rig), 2, "the green frame was drawn");
    drop(request);
    release.store(true, Ordering::Release);
    rig.reader.drain_active().await.unwrap();
    rig.port.state.lock().unwrap().lighting_held = None;
    // A quick retry, before any origin tick: the read-only resolution.
    assert_eq!(
        set(&rig, 3, Some(GREEN_BREATHING_LOGO), 1).await,
        (2, PENDING.to_owned())
    );
    keepalive(&rig, Instant::now() + Duration::from_secs(6)).await;
    assert_eq!(frames(&rig), 2, "no renderer survives a latched doubt");
    rig.stop().await;
}

/// A tick dropped while its keepalive frame is in flight latches the doubt:
/// no renderer is left, the next request resolves read-only, and the one
/// after draws anew.
#[tokio::test]
async fn a_dropped_keepalive_latches_the_doubt() {
    let rig = Rig::new(MemoryFiles::default());
    lit(&rig, [0x40, 1, 0]);
    assert_eq!(set(&rig, 1, Some(STATIC), 1).await.0, 1);
    let first = due(&rig);
    // A hold no setter reaches keeps the job on the real clock, so the
    // muted frame is still in flight when the tick is dropped.
    let release = Arc::new(AtomicBool::new(false));
    {
        let mut state = rig.port.state.lock().unwrap();
        state.matrix_mute = true;
        state.lighting_held = Some((usize::MAX, release.clone()));
    }
    {
        let mut tick = Box::pin(async {
            let maintain = rig.source.lock().await.maintain_lighting(first).unwrap();
            maintain.await
        });
        tokio::select! {
            _ = &mut tick => panic!("the muted frame completed"),
            _ = async {
                while frames(&rig) < 2 {
                    tokio::time::sleep(Duration::from_millis(1)).await;
                }
            } => {}
        }
    }
    release.store(true, Ordering::Release);
    rig.reader.drain_active().await.unwrap();
    {
        let mut state = rig.port.state.lock().unwrap();
        state.matrix_mute = false;
        state.lighting_held = None;
    }
    keepalive(&rig, first + Duration::from_secs(10)).await;
    assert_eq!(frames(&rig), 2, "no renderer survives a dropped keepalive");
    assert_eq!(set(&rig, 2, Some(STATIC), 1).await, (2, PENDING.into()));
    assert_eq!(set(&rig, 3, Some(STATIC), 1).await.0, 1);
    assert_eq!(frames(&rig), 3, "drawn anew");
    rig.stop().await;
}

/// Keepalive frames pause while their fresh admission refuses them, a
/// competing controller present or the owning console session inactive:
/// each refused capture waits for the next instant, and the frames resume
/// by themselves, with no handshake, when the admission returns.
#[tokio::test]
async fn the_keepalive_pauses_while_its_admission_refuses_it() {
    let rig = Rig::new(MemoryFiles::default());
    lit(&rig, [0x40, 1, 0]);
    assert_eq!(set(&rig, 1, Some(STATIC), 1).await.0, 1);
    let first = due(&rig);
    let at = |ms: u64| first + Duration::from_millis(ms);
    let reports = sent(&rig).len();
    rig.custody.controller.store(true, Ordering::Release);
    keepalive(&rig, at(0)).await;
    keepalive(&rig, at(500)).await;
    keepalive(&rig, at(5_000)).await;
    assert_eq!(frames(&rig), 1, "no frame while a controller is present");
    assert_eq!(due(&rig), at(10_000));
    rig.custody.controller.store(false, Ordering::Release);
    keepalive(&rig, at(5_500)).await;
    assert_eq!(
        frames(&rig),
        1,
        "a refused capture waits for the next instant"
    );
    keepalive(&rig, at(10_000)).await;
    assert_eq!(frames(&rig), 2, "the frames resume when it leaves");
    rig.custody.scoped_fresh.store(true, Ordering::Release);
    rig.custody.current_console.store(false, Ordering::Release);
    keepalive(&rig, at(15_000)).await;
    assert_eq!(frames(&rig), 2, "no frame while the session is inactive");
    assert_eq!(due(&rig), at(20_000));
    rig.custody.current_console.store(true, Ordering::Release);
    keepalive(&rig, at(20_000)).await;
    rig.custody.scoped_fresh.store(false, Ordering::Release);
    assert_eq!(frames(&rig), 3, "the frames resume with the session");
    assert_eq!(
        sent(&rig).len(),
        reports,
        "no handshake before a resumed frame"
    );
    rig.stop().await;
}

/// A request's target needs the semantic admission: without it a Static
/// request stores its intent and replies as for a value the capability
/// table lacks, with nothing sent.
#[tokio::test]
async fn a_request_without_the_semantic_admission_sends_nothing() {
    let rig = Rig::new(MemoryFiles::default());
    lit(&rig, [0x40, 1, 0]);
    rig.runtime.withhold_lighting_approval_for_tests();
    let (status, message) = set(&rig, 1, Some(STATIC), 1).await;
    assert_eq!(status, 2);
    assert_eq!(
        message,
        format!("{STORED}Lighting value is not supported by the verified capability table.")
    );
    assert!(sent(&rig).is_empty());
    assert_eq!(frames(&rig), 0);
    assert_eq!(saved(&rig), STATIC);
    rig.stop().await;
}

/// Off ends the renderer as managed's does: no further keepalive.
#[tokio::test]
async fn off_retires_the_renderer() {
    let rig = Rig::new(MemoryFiles::default());
    lit(&rig, [0x40, 1, 0]);
    assert_eq!(set(&rig, 1, Some(STATIC), 1).await.0, 1);
    assert_eq!(set(&rig, 2, Some(OFF), 1).await.0, 1);
    keepalive(&rig, Instant::now() + Duration::from_secs(6)).await;
    assert_eq!(frames(&rig), 1);
    rig.stop().await;
}

/// A keepalive frame still running at Stop is cancelled by the worker's
/// stop, in doubt, and nothing is replayed; the renderer ends without a
/// write.
#[tokio::test]
async fn stop_cancels_a_running_keepalive() {
    let rig = Rig::new(MemoryFiles::default());
    lit(&rig, [0x40, 1, 0]);
    assert_eq!(set(&rig, 1, Some(STATIC), 1).await.0, 1);
    rig.port.state.lock().unwrap().matrix_mute = true;
    let due = Instant::now() + Duration::from_secs(6);
    let tick = async {
        let maintain = rig.source.lock().await.maintain_lighting(due).unwrap();
        maintain.await
    };
    let stop = async {
        tokio::time::timeout(HANG_GUARD, async {
            while frames(&rig) < 2 {
                tokio::time::sleep(Duration::from_millis(1)).await;
            }
        })
        .await
        .unwrap();
        rig.stop().await;
    };
    let (ticked, ()) = tokio::join!(tick, stop);
    ticked.unwrap();
    assert_eq!(frames(&rig), 2);
    assert_eq!(sent(&rig).len(), 5, "nothing after the cancelled frame");
}

/// A clear is stored and its reply says what waits. On an unclassified
/// adapter that lighting admits it is managed's: profile application waits
/// for adapter classification. On a classified adapter managed applies the
/// profile's lighting, which native does not yet, and the reply says so
/// (interim divergence). Nothing is sent either way.
#[tokio::test]
async fn a_clear_is_stored_and_says_what_waits() {
    for (unclassified, reply) in [
        (
            false,
            "Lighting override was cleared. The profile's lighting was not applied.",
        ),
        (
            true,
            "Override cleared. Power-profile application waits for adapter classification.",
        ),
    ] {
        let rig = Rig::new(MemoryFiles::default());
        assert_eq!(set(&rig, 1, Some(SPECTRUM), 1).await.0, 2);
        assert_eq!(saved(&rig), SPECTRUM);
        if unclassified {
            rig.port.state.lock().unwrap().power_status = 3;
        }
        let (status, message) = set(&rig, 2, None, 0).await;
        assert_eq!((status, message.as_str()), (0, reply), "{unclassified}");
        assert_eq!(saved(&rig), "null", "{unclassified}");
        assert!(sent(&rig).is_empty(), "{unclassified}");
        assert_eq!(
            rig.get().await["status"]["lightingOverride"],
            Value::Null,
            "{unclassified}"
        );
        rig.stop().await;
    }
}

/// Off from a lit keyboard: the brightness and logo setters, each read back,
/// then managed's aggregate read, published in the confirmed state a
/// thermal read established.
#[tokio::test]
async fn off_is_written_confirmed_by_readback_and_published() {
    let rig = Rig::new(MemoryFiles::default());
    lit(&rig, [0xc5, 1, 2]);
    // The lighting members join the state a thermal read confirmed.
    assert!(!fresh_observe(&rig).await["status"]["confirmedState"].is_null());
    let (status, message) = set(&rig, 1, Some(OFF), 1).await;
    assert_eq!((status, message.as_str()), (1, CONFIRMED));
    assert_eq!(rig.port.state.lock().unwrap().lighting, [0, 0, 2]);
    assert_eq!(setters(&rig), 2);
    assert_eq!(
        rig.port.state.lock().unwrap().writes,
        0,
        "no thermal setter"
    );
    assert_eq!(saved(&rig), OFF);
    let status = rig.get().await;
    let status = &status["status"];
    assert_eq!(
        status["confirmedState"]["lighting"]["parameters"]["kind"],
        "off"
    );
    assert_eq!(status["confirmedState"]["logoLighting"], 2);
    assert_eq!(status["lightingOverride"]["value"]["logoMode"], 2);
    assert_eq!(status["desiredState"]["lighting"]["source"], 4);
    // Already confirmed: read, nothing written.
    let (status, message) = set(&rig, 2, Some(OFF), 1).await;
    assert_eq!(
        (status, message.as_str()),
        (1, "Lighting is already confirmed.")
    );
    assert_eq!(setters(&rig), 2);
    rig.stop().await;
}

/// A temporary override is held in memory with its power class and the
/// saved one is cleared, as managed's `SetLightingOverrideCoreAsync` does.
#[tokio::test]
async fn a_temporary_override_clears_the_saved_one() {
    let rig = Rig::new(MemoryFiles::default());
    assert_eq!(set(&rig, 1, Some(OFF), 1).await.0, 1);
    assert_eq!(saved(&rig), OFF);
    assert_eq!(set(&rig, 2, Some(OFF), 0).await.0, 1);
    assert_eq!(saved(&rig), "null");
    let status = rig.get().await;
    assert_eq!(status["status"]["lightingOverride"]["lifetime"], 0);
    assert_eq!(status["status"]["desiredState"]["lighting"]["source"], 3);
    rig.stop().await;
}

/// Without fresh admission nothing is stored, read or written. For a
/// competing controller this is stricter than managed, whose refresh only
/// observes the controller and relinquishes lighting: managed stores the
/// intent and then refuses the apply.
#[tokio::test]
async fn refused_admission_stores_and_sends_nothing() {
    for refusal in ["competing controller", "fresh admission"] {
        let rig = Rig::new(MemoryFiles::default());
        lit(&rig, [0xc5, 1, 0]);
        match refusal {
            "competing controller" => rig.custody.controller.store(true, Ordering::Release),
            _ => rig.custody.fresh_refuse.store(true, Ordering::Release),
        }
        let (status, message) = set(&rig, 1, Some(OFF), 1).await;
        assert_eq!(status, 2, "{refusal}: {message}");
        assert!(!message.starts_with(STORED), "{refusal}: {message}");
        assert!(sent(&rig).is_empty(), "{refusal}");
        assert!(
            rig.files.read("manual-overrides.json").unwrap().is_none(),
            "{refusal}"
        );
        assert_eq!(rig.port.state.lock().unwrap().lighting, [0xc5, 1, 0]);
        rig.stop().await;
    }
}

/// A setter that may have been submitted unconfirmed stops the request; the
/// next one resolves it read-only with managed's pending refusal, and only
/// then may a request write again. Nothing is replayed.
#[tokio::test]
async fn an_ambiguous_submission_latches_until_a_read_only_resolution() {
    let rig = Rig::new(MemoryFiles::default());
    lit(&rig, [0xc5, 1, 0]);
    rig.port.state.lock().unwrap().lighting_timeout = Some(1);
    let (status, message) = set(&rig, 1, Some(OFF), 1).await;
    assert_eq!(status, 3);
    assert_eq!(
        message,
        format!(
            "{STORED}Lighting write is indeterminate: A brightness command may have been submitted and was not confirmed."
        )
    );
    assert_eq!(setters(&rig), 1);
    let (status, message) = set(&rig, 2, Some(OFF), 1).await;
    assert_eq!((status, message.as_str()), (2, PENDING));
    assert_eq!(setters(&rig), 1, "the resolution writes nothing");
    // The timed-out setter applied: the next request finds the keyboard Off
    // and writes only the logo.
    let (status, message) = set(&rig, 3, Some(OFF), 1).await;
    assert_eq!((status, message.as_str()), (1, CONFIRMED));
    assert_eq!(setters(&rig), 2);
    assert_eq!(rig.port.state.lock().unwrap().lighting, [0, 0, 0]);
    rig.stop().await;
}

/// A resolution that cannot read the keyboard leaves the latch set: the
/// next request resolves again, and only a complete read lets one write.
#[tokio::test]
async fn an_unread_resolution_keeps_the_latch() {
    let rig = Rig::new(MemoryFiles::default());
    lit(&rig, [0xc5, 1, 0]);
    rig.port.state.lock().unwrap().lighting_timeout = Some(1);
    assert_eq!(set(&rig, 1, Some(OFF), 1).await.0, 3);
    {
        let mut s = rig.port.state.lock().unwrap();
        let queries = s.lighting_sent.iter().filter(|sent| !sent.2).count();
        s.lighting_query_timeout = Some(queries + 1);
    }
    assert_eq!(set(&rig, 2, Some(OFF), 1).await, (2, PENDING.into()));
    rig.port.state.lock().unwrap().lighting_query_timeout = None;
    assert_eq!(set(&rig, 3, Some(OFF), 1).await, (2, PENDING.into()));
    assert_eq!(setters(&rig), 1, "neither resolution writes");
    assert_eq!(set(&rig, 4, Some(OFF), 1).await, (1, CONFIRMED.into()));
    rig.stop().await;
}

/// A readback that confirms another value restores the confirmed baseline
/// and leaves nothing in doubt.
#[tokio::test]
async fn a_readback_mismatch_restores_and_is_not_latched() {
    let rig = Rig::new(MemoryFiles::default());
    lit(&rig, [0xc5, 1, 0]);
    rig.port.state.lock().unwrap().lighting_ignore = vec![1];
    let (status, message) = set(&rig, 1, Some(OFF), 1).await;
    assert_eq!(status, 3);
    assert_eq!(
        message,
        format!(
            "{STORED}Lighting write is indeterminate: Lighting brightness did not match independent readback."
        )
    );
    assert_eq!(setters(&rig), 2, "the setter and its restore");
    assert_eq!(rig.port.state.lock().unwrap().lighting, [0xc5, 1, 0]);
    let (status, message) = set(&rig, 2, Some(OFF), 1).await;
    assert_eq!((status, message.as_str()), (1, CONFIRMED));
    rig.stop().await;
}

/// A store that fails is managed's rejected intent; nothing is written.
#[tokio::test]
async fn a_persistence_failure_is_rejected_intent_and_writes_nothing() {
    let rig = Rig::new(MemoryFiles::default());
    lit(&rig, [0xc5, 1, 0]);
    rig.files.fail.store(true, Ordering::Release);
    let (status, message) = set(&rig, 1, Some(OFF), 1).await;
    assert_eq!(status, 3);
    assert!(
        message.starts_with("The requested intent was rejected: "),
        "{message}"
    );
    assert!(sent(&rig).is_empty());
    assert_eq!(rig.port.state.lock().unwrap().lighting, [0xc5, 1, 0]);
    rig.stop().await;
}

/// The saved override survives a restart in managed's bytes, and the store
/// loads it as the persistent override; a restart writes no lighting
/// (an interim divergence: native runs no lighting reconciliation yet).
#[tokio::test]
async fn a_restart_keeps_the_saved_intent_and_writes_nothing() {
    let files = MemoryFiles::default();
    let rig = Rig::new(files.clone());
    assert_eq!(set(&rig, 1, Some(OFF), 1).await.0, 1);
    rig.stop().await;
    let rig = Rig::restarted(files.clone());
    lit(&rig, [0xc5, 1, 0]);
    assert_eq!(rig.get().await["success"], true);
    assert!(sent(&rig).is_empty());
    assert_eq!(saved(&rig), OFF);
    let storage = Storage::open(Box::new(files), SID).unwrap();
    let saved = storage.current_lighting_override(1).unwrap().unwrap();
    assert_eq!(saved.lifetime, 1);
    assert_eq!(saved.settings.logo_mode.map(|mode| mode.0), Some(2));
    // The next request projects it in status, as every saved override is.
    let (status, message) = set(&rig, 2, Some(OFF), 1).await;
    assert_eq!((status, message.as_str()), (1, CONFIRMED));
    let status = rig.get().await;
    assert_eq!(status["status"]["lightingOverride"]["lifetime"], 1);
    assert_eq!(status["status"]["desiredState"]["lighting"]["source"], 4);
    rig.stop().await;
}

const UNCLASSIFIED_BLOCKED: &str = "Hardware application was not confirmed. The external power adapter could not be classified; overrides and control writes are blocked.";

/// The keyboard Off with no logo mode.
const OFF_ONLY: &str = r#"{"effect":0,"color":{"red":0,"green":0,"blue":0},"brightness":0,"parameters":{"kind":"off"},"logoMode":null}"#;

fn power_reads(rig: &Rig) -> usize {
    rig.port.state.lock().unwrap().power_reads
}
/// Whether a lighting override is saved.
fn stored(rig: &Rig) -> bool {
    rig.files.read("manual-overrides.json").unwrap().is_some() && saved(rig) != "null"
}

/// Managed's power resolution, in the job under its own admission: Windows
/// battery is the battery class with no adapter read, and Windows AC
/// classifies one fresh 07/8C reading. Windows unknown, or an adapter battery
/// reading Windows AC contradicts, is no class: nothing is stored or sent.
#[tokio::test]
async fn power_is_classified_in_the_job_before_the_store() {
    // (Windows on battery, adapter actual level, adapter reads)
    for (windows, actual, reads) in [(Some(Some(true)), 15, 0), (None, 15, 1)] {
        let rig = Rig::new(MemoryFiles::default());
        lit(&rig, [0xc5, 1, 0]);
        {
            let mut s = rig.port.state.lock().unwrap();
            s.windows_on_battery = windows;
            s.actual = actual;
        }
        let before = power_reads(&rig);
        let (status, message) = set(&rig, 1, Some(OFF), 0).await;
        assert_eq!((status, message.as_str()), (1, CONFIRMED), "{windows:?}");
        assert_eq!(power_reads(&rig) - before, reads, "{windows:?}");
        let status = rig.get().await;
        assert_eq!(status["status"]["lightingOverride"]["lifetime"], 0);
        rig.stop().await;
    }
    for case in ["Windows unknown", "adapter battery under Windows AC"] {
        let rig = Rig::new(MemoryFiles::default());
        lit(&rig, [0xc5, 1, 0]);
        {
            let mut s = rig.port.state.lock().unwrap();
            match case {
                "Windows unknown" => s.windows_on_battery = Some(None),
                _ => (s.windows_on_battery, s.actual) = (Some(Some(false)), 0),
            }
        }
        let (status, message) = set(&rig, 1, Some(OFF), 1).await;
        assert_eq!(
            (status, message.as_str()),
            (2, UNCLASSIFIED_BLOCKED),
            "{case}"
        );
        assert!(sent(&rig).is_empty(), "{case}");
        assert!(!stored(&rig), "{case}");
        rig.stop().await;
    }
}

/// Managed `CanAdmitUnclassifiedExternalControl`: on an adapter 07/8C cannot
/// classify, lighting needs fresh Windows AC, a fresh firmware observation of
/// HyperBoost inactive and no unsettled thermal step. A classified adapter
/// needs none of them; an unsettled thermal step does not block its store,
/// since no thermal recovery rewrites the lighting member.
#[tokio::test]
async fn an_unclassified_adapter_admits_lighting_only_without_hyperboost_or_a_pending_step() {
    // (case, unclassified, HyperBoost in firmware, unsettled thermal step, applies)
    for (case, unclassified, hyperboost, pending, applies) in [
        ("unclassified", true, false, false, true),
        ("unclassified, HyperBoost", true, true, false, false),
        ("unclassified, pending step", true, false, true, false),
        ("classified, HyperBoost", false, true, false, true),
        ("classified, pending step", false, false, true, true),
    ] {
        let rig = Rig::new(MemoryFiles::default());
        lit(&rig, [0xc5, 1, 0]);
        if pending {
            rig.port.state.lock().unwrap().fail_write = Some(1);
            rig.set(9, value(2, 1)).await;
            assert!(
                Storage::open(Box::new(rig.files.clone()), SID)
                    .unwrap()
                    .pending(),
                "{case}"
            );
        }
        {
            let mut s = rig.port.state.lock().unwrap();
            if hyperboost {
                (s.selectors, s.fans) = ([7; 2], [0; 2]);
            }
            if unclassified {
                s.power_status = 3;
            }
        }
        let (status, message) = set(&rig, 1, Some(OFF), 1).await;
        if applies {
            assert_eq!((status, message.as_str()), (1, CONFIRMED), "{case}");
            assert_eq!(saved(&rig), OFF, "{case}");
        } else {
            assert_eq!(
                (status, message.as_str()),
                (2, UNCLASSIFIED_BLOCKED),
                "{case}"
            );
            assert!(sent(&rig).is_empty(), "{case}");
            assert!(!stored(&rig), "{case}");
        }
        rig.stop().await;
    }
}

/// With HyperBoost compiled in, managed's journal safety block and pending
/// restore refuse lighting on an unclassified adapter too: the job reads
/// managed's HyperBoost journal fresh, and a failed read admits nothing.
#[cfg(feature = "service-hyperboost-v1")]
#[tokio::test]
async fn a_hyperboost_journal_that_is_not_relinquished_blocks_unclassified_lighting() {
    let journal = |state: u8| {
        let vectors: Value = serde_json::from_str(include_str!(
            "../../../../../tests/data/hyperboost-journal-vectors.json"
        ))
        .unwrap();
        vectors["rows"][0]["document"]
            .as_str()
            .unwrap()
            .replace("\"state\": 3,", &format!("\"state\": {state},"))
            .into_bytes()
    };
    // (case, journal read, unclassified, applies)
    for (case, read, unclassified, applies) in [
        ("absent", Some(None), true, true),
        ("relinquished", Some(Some(journal(3))), true, true),
        ("pending restore", Some(Some(journal(1))), true, false),
        ("unreadable", None, true, false),
        (
            "classified, pending restore",
            Some(Some(journal(1))),
            false,
            true,
        ),
    ] {
        let rig = Rig::new(MemoryFiles::default());
        lit(&rig, [0xc5, 1, 0]);
        *rig.hyperboost.journal.lock().unwrap() = read;
        if unclassified {
            rig.port.state.lock().unwrap().power_status = 3;
        }
        let (status, message) = set(&rig, 1, Some(OFF), 1).await;
        if applies {
            assert_eq!((status, message.as_str()), (1, CONFIRMED), "{case}");
        } else {
            assert_eq!(
                (status, message.as_str()),
                (2, UNCLASSIFIED_BLOCKED),
                "{case}"
            );
            assert!(sent(&rig).is_empty(), "{case}");
            assert!(!stored(&rig), "{case}");
        }
        rig.stop().await;
    }
}

/// A temporary override carries the Windows power generation its request
/// classified, as a thermal step's store does, so the next thermal step on
/// the same power keeps it; managed retires a temporary on a power change.
#[tokio::test]
async fn a_temporary_override_survives_a_thermal_step_on_the_same_power() {
    let files = MemoryFiles::default();
    files.profiles(1);
    let rig = Rig::new(files);
    lit(&rig, [0xc5, 1, 0]);
    assert_eq!(set(&rig, 1, Some(OFF), 0).await.0, 1);
    let out = rig.set(2, value(2, 1)).await;
    assert_eq!(out["result"]["status"], 1, "{out}");
    let status = rig.get().await;
    assert_eq!(status["status"]["lightingOverride"]["lifetime"], 0);
    assert_eq!(status["status"]["desiredState"]["lighting"]["source"], 3);
    rig.stop().await;
}

/// The confirmed lighting members follow the job: a member whose setter may
/// have been submitted is cleared until a read confirms it, and a restore
/// that read back the baseline publishes the baseline.
#[tokio::test]
async fn status_publishes_only_what_the_last_job_confirmed() {
    let rig = Rig::new(MemoryFiles::default());
    lit(&rig, [0xc5, 1, 2]);
    fresh_observe(&rig).await;
    assert_eq!(set(&rig, 1, Some(OFF), 1).await.0, 1);
    let confirmed = |status: &Value| {
        let state = &status["status"]["confirmedState"];
        (state["lighting"].clone(), state["logoLighting"].clone())
    };
    let off = rig.get().await["status"]["confirmedState"]["lighting"].clone();
    assert_eq!(off["parameters"]["kind"], "off");
    assert_eq!(confirmed(&rig.get().await), (off.clone(), json!(2)));
    // The backlight is raised outside OpenBlade; the next brightness setter
    // applies and its acknowledgement is lost.
    {
        let mut s = rig.port.state.lock().unwrap();
        s.lighting = [0x80, 1, 0];
        s.lighting_timeout = Some(s.lighting_sent.iter().filter(|s| s.2).count() + 1);
    }
    assert_eq!(set(&rig, 2, Some(OFF), 1).await.0, 3);
    // Its baseline read confirmed the logo; nothing confirms the brightness.
    assert_eq!(confirmed(&rig.get().await), (Value::Null, json!(0)));
    // The read-only resolution confirms both.
    assert_eq!(set(&rig, 3, Some(OFF), 1).await, (2, PENDING.to_owned()));
    assert_eq!(confirmed(&rig.get().await), (off, json!(0)));
    // A mismatch restores the lit baseline, which is not Off.
    {
        let mut s = rig.port.state.lock().unwrap();
        s.lighting = [0x80, 1, 0];
        s.lighting_ignore = vec![s.lighting_sent.iter().filter(|s| s.2).count() + 1];
    }
    assert_eq!(set(&rig, 4, Some(OFF), 1).await.0, 3);
    assert_eq!(rig.port.state.lock().unwrap().lighting, [0x80, 1, 0]);
    assert_eq!(confirmed(&rig.get().await), (Value::Null, json!(0)));
    rig.stop().await;
}

/// The status writes lighting parameters with `kind` first wherever it
/// carries them, which managed's polymorphic binding requires.
#[tokio::test]
async fn status_writes_lighting_parameters_kind_first() {
    let rig = Rig::new(MemoryFiles::default());
    assert_eq!(set(&rig, 1, Some(STATIC), 1).await.0, 1);
    let request = br#"{"operation":"getStatus","version":91,"requestId":"bbbbbbbb-cccc-dddd-eeee-000000000098"}"#;
    let bytes = rig
        .request_bytes("getStatus", request.to_vec(), USER, SID)
        .await;
    let text = String::from_utf8(bytes).unwrap();
    let members: Vec<usize> = text
        .match_indices(r#""parameters":{"#)
        .map(|(at, found)| at + found.len())
        .collect();
    // The override and the desired state carry the stored static effect.
    assert!(members.len() >= 2, "{text}");
    for at in members {
        assert!(text[at..].starts_with(r#""kind":"#), "{}", &text[at..]);
    }
    assert!(
        text.contains(r#""parameters":{"kind":"static","color":"#),
        "{text}"
    );
    rig.stop().await;
}

/// Managed `IsKeyboardLightingDelegated`: with the keyboard delegated to
/// Windows Dynamic Lighting nothing is stored, read or written. Stricter than
/// managed, which stores the intent first and replies with the committed
/// prefix ("Lighting override intent was stored. Hardware result: ...",
/// ServiceControlRuntime.cs 14965, 15054, 15213).
#[cfg(feature = "service-profiles-v1")]
#[tokio::test]
async fn a_keyboard_delegated_to_dynamic_lighting_blocks_before_any_store() {
    let files = MemoryFiles::default();
    files.profiles(1);
    let mut preferences = files.document("battery-preferences.json");
    preferences["preferences"]["keyboardLightingControlMode"] = json!(1);
    files.put("battery-preferences.json", preferences);
    files.commit_configuration();
    let rig = Rig::new(files);
    lit(&rig, [0xc5, 1, 0]);
    let (status, message) = set(&rig, 1, Some(OFF), 1).await;
    assert_eq!(
        (status, message.as_str()),
        (
            2,
            "Keyboard lighting is delegated to Windows Dynamic Lighting. OpenBlade did not write the keyboard."
        )
    );
    assert!(sent(&rig).is_empty());
    assert!(!stored(&rig));
    rig.stop().await;
}

/// Managed defers lighting while the low-power gate is set and applies it
/// after standby; native stores it and says so, with no promise, since
/// nothing applies a stored lighting intent yet (interim divergence).
#[cfg(feature = "service-profile-triggers-v1")]
#[tokio::test]
async fn the_low_power_store_promises_no_resume_apply_interim_divergence() {
    use windows_sys::Win32::System::Services::SERVICE_CONTROL_POWEREVENT as POWER;
    const SUSPEND: u32 = 4;
    const RESUME: u32 = 18;
    let rig = Rig::new(MemoryFiles::default());
    lit(&rig, [0xc5, 1, 0]);
    let custody = rig.custody.clone();
    rig.runtime
        .read_low_power(move || custody.lifecycle.low_power());
    rig.custody.scoped_fresh.store(true, Ordering::Release);
    rig.custody.lifecycle.ingress(POWER, SUSPEND);
    let (status, message) = set(&rig, 1, Some(OFF), 1).await;
    assert_eq!(
        (status, message.as_str()),
        (0, "Lighting override intent was stored.")
    );
    assert_eq!(saved(&rig), OFF);
    assert!(sent(&rig).is_empty());
    rig.custody.lifecycle.ingress(POWER, RESUME);
    rig.custody.scoped_fresh.store(false, Ordering::Release);
    assert_eq!(rig.get().await["success"], true);
    assert!(sent(&rig).is_empty(), "nothing applies it after standby");
    rig.stop().await;
}

/// At the low-power boundary the origin tick ends the renderer without a
/// write, before the keyboard step; after the resume no keepalive goes until
/// a request draws a new renderer.
#[cfg(feature = "service-profile-triggers-v1")]
#[tokio::test]
async fn low_power_retires_the_renderer_without_a_write() {
    use windows_sys::Win32::System::Services::SERVICE_CONTROL_POWEREVENT as POWER;
    const SUSPEND: u32 = 4;
    const RESUME: u32 = 18;
    let rig = Rig::new(MemoryFiles::default());
    lit(&rig, [0x40, 1, 0]);
    let custody = rig.custody.clone();
    rig.runtime
        .read_low_power(move || custody.lifecycle.low_power());
    assert_eq!(set(&rig, 1, Some(STATIC), 1).await.0, 1);
    let reports = sent(&rig).len();
    rig.custody.scoped_fresh.store(true, Ordering::Release);
    rig.custody.lifecycle.ingress(POWER, SUSPEND);
    keepalive(&rig, Instant::now() + Duration::from_secs(6)).await;
    assert_eq!((frames(&rig), sent(&rig).len()), (1, reports));
    rig.custody.lifecycle.ingress(POWER, RESUME);
    rig.custody.scoped_fresh.store(false, Ordering::Release);
    keepalive(&rig, Instant::now() + Duration::from_secs(6)).await;
    assert_eq!(frames(&rig), 1, "nothing owns the matrix after the resume");
    rig.stop().await;
}

/// A suspend and resume that both arrive while a request holds the
/// operation gate, so no tick finds the gate free between them, still end
/// the renderer: the first tick after sends nothing, and a same-color
/// request draws anew instead of retaining it.
#[cfg(feature = "service-profile-triggers-v1")]
#[tokio::test]
async fn a_low_power_boundary_while_the_gate_is_held_ends_the_renderer() {
    use windows_sys::Win32::System::Services::SERVICE_CONTROL_POWEREVENT as POWER;
    const SUSPEND: u32 = 4;
    const RESUME: u32 = 18;
    const BRIGHT: &str = r#"{"effect":1,"color":{"red":200,"green":10,"blue":5},"brightness":50,"parameters":{"kind":"static","color":{"red":200,"green":10,"blue":5}},"logoMode":null}"#;
    let rig = Rig::new(MemoryFiles::default());
    lit(&rig, [0x40, 1, 0]);
    let custody = rig.custody.clone();
    rig.runtime
        .read_low_power(move || custody.lifecycle.low_power());
    assert_eq!(set(&rig, 1, Some(STATIC), 1).await.0, 1);
    let first = due(&rig);
    {
        let _held = rig.runtime.hold_mutation_gate();
        rig.custody.scoped_fresh.store(true, Ordering::Release);
        rig.custody.lifecycle.ingress(POWER, SUSPEND);
        keepalive(&rig, first).await;
        rig.custody.lifecycle.ingress(POWER, RESUME);
        rig.custody.scoped_fresh.store(false, Ordering::Release);
    }
    keepalive(&rig, first).await;
    assert_eq!(frames(&rig), 1, "no keepalive after the boundary");
    assert_eq!(rig.runtime.keepalive_due_for_tests(), None);
    // The same color at a new brightness would retain an owned renderer.
    assert_eq!(set(&rig, 2, Some(BRIGHT), 1).await, (1, RENDERED.into()));
    assert_eq!(frames(&rig), 2, "drawn anew after the boundary");
    rig.stop().await;
}

/// The same boundary with no tick at all before the next request: the
/// request ends the renderer itself and draws anew.
#[cfg(feature = "service-profile-triggers-v1")]
#[tokio::test]
async fn a_request_after_a_low_power_boundary_draws_anew() {
    use windows_sys::Win32::System::Services::SERVICE_CONTROL_POWEREVENT as POWER;
    const SUSPEND: u32 = 4;
    const RESUME: u32 = 18;
    let rig = Rig::new(MemoryFiles::default());
    lit(&rig, [0x40, 1, 0]);
    let custody = rig.custody.clone();
    rig.runtime
        .read_low_power(move || custody.lifecycle.low_power());
    assert_eq!(set(&rig, 1, Some(STATIC), 1).await.0, 1);
    rig.custody.scoped_fresh.store(true, Ordering::Release);
    rig.custody.lifecycle.ingress(POWER, SUSPEND);
    rig.custody.lifecycle.ingress(POWER, RESUME);
    rig.custody.scoped_fresh.store(false, Ordering::Release);
    let reports = sent(&rig).len();
    assert_eq!(set(&rig, 2, Some(STATIC), 1).await, (1, RENDERED.into()));
    assert_eq!(frames(&rig), 2, "drawn anew, not retained");
    assert!(
        sent(&rig)[reports..].contains(&(0x86, false)),
        "the layout byte is read again"
    );
    rig.stop().await;
}

/// While the low-power gate is set only Windows battery classifies power
/// without an adapter read, so a temporary override needs it. Managed stores
/// one with no power class (ServiceControlRuntime.cs 14903-14918); native
/// refuses it, naming the low-power state, and keeps the saved override.
#[cfg(feature = "service-profile-triggers-v1")]
#[tokio::test]
async fn a_low_power_temporary_override_needs_windows_battery_divergence() {
    use windows_sys::Win32::System::Services::SERVICE_CONTROL_POWEREVENT as POWER;
    const SUSPEND: u32 = 4;
    let rig = Rig::new(MemoryFiles::default());
    lit(&rig, [0xc5, 1, 0]);
    let custody = rig.custody.clone();
    rig.runtime
        .read_low_power(move || custody.lifecycle.low_power());
    rig.custody.scoped_fresh.store(true, Ordering::Release);
    rig.custody.lifecycle.ingress(POWER, SUSPEND);
    assert_eq!(set(&rig, 1, Some(OFF), 1).await.0, 0);
    // Windows AC, which classifies no adapter without a read.
    rig.port.state.lock().unwrap().windows_on_battery = Some(Some(false));
    let (status, message) = set(&rig, 2, Some(OFF), 0).await;
    assert_eq!(
        (status, message.as_str()),
        (
            2,
            "A temporary lighting override was not stored: OpenBlade does not read the power source while the laptop is entering or leaving a low-power state."
        )
    );
    assert_eq!(saved(&rig), OFF);
    rig.port.state.lock().unwrap().windows_on_battery = Some(Some(true));
    assert_eq!(set(&rig, 3, Some(OFF), 0).await.0, 0);
    assert_eq!(saved(&rig), "null");
    assert!(sent(&rig).is_empty());
    rig.stop().await;
}

/// H1: a zero baseline is Off only after a second zero sample 750 ms later,
/// so a keyboard lit again in between is written.
#[tokio::test]
async fn off_writes_a_keyboard_whose_second_baseline_sample_is_lit() {
    let rig = Rig::new(MemoryFiles::default());
    lit(&rig, [0, 1, 0]);
    rig.port.state.lock().unwrap().brightness_script = [0, 0x10].into();
    let (status, message) = set(&rig, 1, Some(OFF_ONLY), 1).await;
    assert_eq!((status, message.as_str()), (1, CONFIRMED));
    assert_eq!(setters(&rig), 1);
    assert_eq!(rig.port.state.lock().unwrap().lighting, [0, 1, 0]);
    rig.stop().await;
}

/// A single zero brightness sample confirms no Off. A brightness setter's
/// readback is one sample, so when the logo setter after it is in doubt the
/// status publishes no Off record. The read-only resolution samples a zero
/// twice, so a keyboard lit again between its samples is not published Off.
#[tokio::test]
async fn a_single_zero_brightness_sample_publishes_no_confirmed_off() {
    let confirmed = |status: &Value| status["status"]["confirmedState"].clone();
    let rig = Rig::new(MemoryFiles::default());
    lit(&rig, [0xc5, 1, 2]);
    fresh_observe(&rig).await;
    // The brightness setter is confirmed by its readback; the logo setter
    // after it applies and its acknowledgement is lost.
    rig.port.state.lock().unwrap().lighting_timeout = Some(2);
    assert_eq!(set(&rig, 1, Some(OFF), 1).await.0, 3);
    assert_eq!(rig.port.state.lock().unwrap().lighting[0], 0);
    let state = confirmed(&rig.get().await);
    assert!(state.is_object(), "{state}");
    assert_eq!(state["lighting"], Value::Null);
    // The resolution reads a zero, then a lit keyboard 750 ms later.
    rig.port.state.lock().unwrap().brightness_script = [0, 0x10].into();
    assert_eq!(set(&rig, 2, Some(OFF), 1).await, (2, PENDING.to_owned()));
    let state = confirmed(&rig.get().await);
    assert_eq!(state["lighting"], Value::Null);
    assert_eq!(state["logoLighting"], json!(2));
    rig.stop().await;
}

/// Each stop the job can reach, through the dispatcher: managed's reply, and
/// whether the next request is held for a read-only resolution.
#[tokio::test]
async fn each_stop_replies_as_managed_and_only_possible_submission_latches() {
    let indeterminate = |reason: &str| format!("{STORED}Lighting write is indeterminate: {reason}");
    // The keyboard before the request and the fault it meets.
    type Fault = fn(&mut crate::discovery::performance::mutation_fixture::State);
    type Setup = ([u8; 3], Fault);
    // (case, setup, reply, setters sent, latched)
    let cases: [(&str, Setup, String, usize, bool); 5] = [
        (
            "logo mismatch, restored",
            ([0, 1, 0], |s| s.lighting_ignore = vec![1]),
            indeterminate("Logo lighting did not match independent readback."),
            3,
            false,
        ),
        (
            "restore in doubt",
            ([0xc5, 1, 2], |s| {
                (s.lighting_ignore, s.lighting_timeout) = (vec![1], Some(2))
            }),
            indeterminate(
                "The lighting change failed and the previous lighting state could not be fully restored.",
            ),
            2,
            true,
        ),
        (
            "baseline unread",
            ([0xc5, 1, 0], |s| s.lighting_query_timeout = Some(1)),
            format!(
                "{STORED}Lighting could not be read before writing: Lighting brightness could not be read."
            ),
            0,
            false,
        ),
        (
            "final read unread",
            // The baseline's three queries, the two readbacks, then the
            // final brightness query.
            ([0xc5, 1, 2], |s| s.lighting_query_timeout = Some(7)),
            indeterminate("Lighting brightness could not be read."),
            2,
            true,
        ),
        (
            "baseline logo outside the contract",
            ([0xc5, 2, 0], |_| {}),
            format!(
                "{STORED}Lighting could not be read before writing: Logo lighting response was malformed or unsupported."
            ),
            0,
            false,
        ),
    ];
    for (case, (keyboard, fault), reply, count, latched) in cases {
        let rig = Rig::new(MemoryFiles::default());
        lit(&rig, keyboard);
        fault(&mut rig.port.state.lock().unwrap());
        let (status, message) = set(&rig, 1, Some(OFF), 1).await;
        assert_eq!((status, message), (3, reply), "{case}");
        assert_eq!(setters(&rig), count, "{case}");
        {
            let mut s = rig.port.state.lock().unwrap();
            (
                s.lighting_ignore,
                s.lighting_timeout,
                s.lighting_query_timeout,
            ) = (Vec::new(), None, None);
            s.lighting = [0xc5, 1, 0];
        }
        let next = set(&rig, 2, Some(OFF), 1).await;
        if latched {
            assert_eq!(next, (2, PENDING.to_owned()), "{case}");
            assert_eq!(
                setters(&rig),
                count,
                "{case}: the resolution writes nothing"
            );
        } else {
            assert_eq!(next, (1, CONFIRMED.to_owned()), "{case}");
        }
        rig.stop().await;
    }
}

/// A fresh admission refused at any checkpoint of the job stops it there:
/// before the first setter nothing is written, after a confirmed setter the
/// change stays and nothing is in doubt (managed's partial submission), and
/// only a refusal between a setter's transfer and its reply leaves it in
/// doubt. A refused restore is known not restored.
#[tokio::test]
async fn a_refusal_at_any_checkpoint_stops_the_job_without_replay() {
    let mut replies = std::collections::BTreeSet::new();
    for mismatch in [false, true] {
        for at in 1..200 {
            let rig = Rig::new(MemoryFiles::default());
            lit(&rig, [0xc5, 1, 2]);
            if mismatch {
                rig.port.state.lock().unwrap().lighting_ignore = vec![1];
            }
            rig.custody.refuse_at.store(at, Ordering::Release);
            let (status, message) = set(&rig, 1, Some(OFF), 1).await;
            rig.custody.refuse_at.store(0, Ordering::Release);
            rig.custody.fresh_refuse.store(false, Ordering::Release);
            let finished = status == 1 || (mismatch && message.contains("did not match"));
            let count = setters(&rig);
            let next = set(&rig, 2, Some(OFF), 1).await;
            let latched = next.1 == PENDING;
            // A setter that may have been submitted latches; a stop before a
            // setter's transfer, or after its confirmed readback, does not.
            if message.contains("may have been submitted") {
                assert!(latched, "{at} {mismatch}: {message}");
            }
            if message.contains("partial submission") || message.contains("not applied") {
                assert!(!latched, "{at} {mismatch}: {message}");
            }
            if latched {
                assert_eq!(setters(&rig), count, "{at} {mismatch}: nothing is replayed");
            }
            replies.insert(message.replace(STORED, ""));
            rig.stop().await;
            if finished {
                break;
            }
        }
    }
    for reply in [
        "Lighting write is indeterminate: The lighting transition stopped after partial submission.",
        "Lighting write is indeterminate: The lighting change failed and the previous lighting state could not be fully restored.",
        "Lighting write is indeterminate: A brightness command may have been submitted and was not confirmed.",
        "Lighting write was not applied: Current lighting write admission is unavailable; no lighting setter was submitted.",
    ] {
        assert!(replies.contains(reply), "{reply}: {replies:#?}");
    }
}

/// Too little request time for the recipe at the measured pace writes
/// nothing and leaves nothing in doubt.
#[tokio::test]
async fn a_recipe_without_time_to_finish_writes_nothing() {
    let rig = Rig::new(MemoryFiles::default());
    lit(&rig, [0xc5, 1, 2]);
    rig.port.state.lock().unwrap().latency = Some(Duration::from_millis(400));
    let (status, message) = set(&rig, 1, Some(OFF), 1).await;
    assert_eq!(
        (status, message.as_str()),
        (
            3,
            "Lighting override intent was stored. Hardware result: Lighting write was not applied: The remaining request time cannot cover this recipe at the measured device pace; no lighting setter was submitted."
        )
    );
    assert_eq!(setters(&rig), 0);
    rig.port.state.lock().unwrap().latency = None;
    assert_eq!(set(&rig, 2, Some(OFF), 1).await, (1, CONFIRMED.to_owned()));
    rig.stop().await;
}

/// A request dropped while its setter is in flight leaves the latch set: the
/// next request resolves read-only and replays nothing.
#[tokio::test]
async fn a_request_dropped_mid_job_leaves_the_latch_set() {
    let rig = Rig::new(MemoryFiles::default());
    lit(&rig, [0xc5, 1, 0]);
    let release = Arc::new(AtomicBool::new(false));
    rig.port.state.lock().unwrap().lighting_held = Some((1, release.clone()));
    let mut request = Box::pin(set(&rig, 1, Some(OFF), 1));
    tokio::select! {
        _ = &mut request => panic!("the held setter completed"),
        _ = async {
            while setters(&rig) == 0 {
                tokio::time::sleep(Duration::from_millis(5)).await;
            }
        } => {}
    }
    drop(request);
    release.store(true, Ordering::Release);
    rig.reader.drain_active().await.unwrap();
    rig.port.state.lock().unwrap().lighting_held = None;
    assert_eq!(set(&rig, 2, Some(OFF), 1).await, (2, PENDING.to_owned()));
    assert_eq!(setters(&rig), 1);
    rig.stop().await;
}

/// A resolution whose logo pair is outside the contract is a complete read:
/// it clears the latch, and the next write's own baseline refuses the pair.
#[tokio::test]
async fn a_resolution_reading_a_malformed_logo_clears_the_latch() {
    let rig = Rig::new(MemoryFiles::default());
    lit(&rig, [0xc5, 1, 0]);
    assert!(!fresh_observe(&rig).await["status"]["confirmedState"].is_null());
    rig.port.state.lock().unwrap().lighting_timeout = Some(1);
    assert_eq!(set(&rig, 1, Some(OFF), 1).await.0, 3);
    {
        let mut s = rig.port.state.lock().unwrap();
        s.lighting_timeout = None;
        s.lighting = [0, 2, 0];
    }
    assert_eq!(set(&rig, 2, Some(OFF), 1).await, (2, PENDING.to_owned()));
    assert_eq!(
        rig.get().await["status"]["confirmedState"]["logoLighting"],
        Value::Null
    );
    let (status, message) = set(&rig, 3, Some(OFF), 1).await;
    assert_eq!(status, 3);
    assert!(message.ends_with("Logo lighting response was malformed or unsupported."));
    rig.port.state.lock().unwrap().lighting = [0, 1, 0];
    assert_eq!(set(&rig, 4, Some(OFF), 1).await, (1, CONFIRMED.to_owned()));
    rig.stop().await;
}

/// The compiled identity carries the refused marker, so no package reader
/// takes this image for the composition it extends.
#[test]
fn the_compiled_identity_carries_the_refused_lighting_marker() {
    let identity = std::str::from_utf8(&crate::NATIVE_ARTIFACT_IDENTITY).unwrap();
    let fields: Vec<&str> = identity.split('\n').collect();
    assert_eq!(fields[2], "service");
    assert!(
        crate::native_package::ends_with_marker_for_tests(fields[8], "service-lighting-v1"),
        "{}",
        fields[8]
    );
    assert!(
        crate::native_package::startup_policy_for_tests(&crate::NATIVE_ARTIFACT_IDENTITY).is_err()
    );
}

/// Every saved member is projected into the status a request publishes, so a
/// fan request keeps the saved lighting override in it (`project_saved_members`).
#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn a_fan_request_keeps_the_saved_lighting_override_in_status() {
    let rig = fan_rig_with(0, 0, 2);
    lit(&rig, [0xc5, 1, 0]);
    fresh_observe(&rig).await;
    assert_eq!(set(&rig, 1, Some(OFF_ONLY), 1).await.0, 1);
    let fan = rig.request(fan_request(101, Some(1), 3700)).await;
    assert_eq!(fan["result"]["status"], 1, "{fan}");
    let status = rig.get().await;
    assert_eq!(status["status"]["lightingOverride"]["lifetime"], 1);
    assert_eq!(status["status"]["desiredState"]["lighting"]["source"], 4);
    assert_eq!(saved(&rig), OFF_ONLY);
    rig.stop().await;
}

/// The periodic thermal observation reads no lighting, so a thermal read
/// publishes the lighting members as null until a lighting request reads them
/// again; managed reads lighting in every observation. Slice L2 reads them.
#[tokio::test]
async fn a_thermal_read_publishes_no_lighting_members_interim_divergence() {
    let rig = Rig::new(MemoryFiles::default());
    lit(&rig, [0xc5, 1, 2]);
    fresh_observe(&rig).await;
    assert_eq!(set(&rig, 1, Some(OFF), 1).await.0, 1);
    let state = rig.get().await["status"]["confirmedState"].clone();
    assert_eq!(state["lighting"]["parameters"]["kind"], "off");
    assert_eq!(state["logoLighting"], 2);
    let state = fresh_observe(&rig).await["status"]["confirmedState"].clone();
    assert!(!state.is_null());
    assert_eq!(state["lighting"], Value::Null);
    assert_eq!(state["logoLighting"], Value::Null);
    rig.stop().await;
}

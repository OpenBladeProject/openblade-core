//! The Static renderer recipe and its keepalive frame on the Reader directly,
//! against the in-memory keyboard and its matrix collection: the order of
//! every report and frame, what each failure restores, and what each stop
//! leaves in doubt.
use super::lighting_tests::{BRIGHTNESS, EFFECT, Lit, STATE, keyboard, run};
use super::*;
use crate::discovery::performance::{
    LightingJob, LightingOutcome, MatrixState, RenderTarget, Renderer, Rgb,
};
use openblade_protocol::lighting::{
    LogoMode,
    matrix::{MatrixFrame, RegionalProjection, decode_feature_report},
};

const GREEN: Rgb = Rgb {
    red: 0x47,
    green: 0xe1,
    blue: 0x0c,
};
const RED: Rgb = Rgb {
    red: 0xc8,
    green: 0x0a,
    blue: 0x05,
};
const LAYOUT: &str = "00/86:000000";
const MODE: &str = "00/84:0000";
const HANDSHAKE: &str = "03/0A:0500";
const FRAME: &str = "MATRIX";

fn renderer(color: Rgb) -> Renderer {
    Renderer { color, layout: 0 }
}

/// A Static request for `color` at raw 0xC5 with no logo, no layout read
/// yet and no owned renderer.
fn target(color: Rgb) -> RenderTarget {
    RenderTarget {
        brightness: 0xc5,
        logo: None,
        color,
        layout: None,
        previous: None,
    }
}

fn render(target: RenderTarget) -> Lit {
    Lit::with(LightingJob::render(target))
}

/// Every mailbox report sent, as `CC/II:payload`, with `MATRIX` where each
/// matrix frame was submitted.
fn log(fixture: &Fixture) -> Vec<String> {
    let s = fixture.state.lock().unwrap();
    assert_eq!(s.writes, 0, "no thermal setter");
    let mut log: Vec<String> = s
        .sent
        .iter()
        .map(|(class, command, payload, _)| {
            let payload: String = payload.iter().map(|b| format!("{b:02X}")).collect();
            format!("{class:02X}/{command:02X}:{payload}")
        })
        .collect();
    for (at, _) in s.matrix_frames.iter().rev() {
        log.insert(*at, FRAME.into());
    }
    log
}

/// The colors each submitted frame carries.
fn frames(fixture: &Fixture) -> Vec<MatrixFrame> {
    let s = fixture.state.lock().unwrap();
    s.matrix_frames
        .iter()
        .map(|(_, report)| decode_feature_report(report).unwrap().1)
        .collect()
}

/// The frame a renderer projects onto layout 0.
fn projected(color: Rgb) -> MatrixFrame {
    let maps = super::super::lighting_layout::source_maps().unwrap();
    RegionalProjection::new(&maps.layout(), 0).project(&MatrixFrame::uniform(color))
}

fn same_colors(actual: &MatrixFrame, color: Rgb) -> bool {
    actual.slots() == projected(color).slots()
}

/// Managed's order from a lit keyboard: the baseline, the layout byte once
/// per session, the brightness, the 00/84 read and the 03/0A:0500
/// handshake, then the first frame of the uniform canvas projected onto the
/// selected physical map. The lit state is setter-completion evidence only.
#[tokio::test]
async fn a_static_request_reads_the_layout_handshakes_and_sends_its_frame() {
    let fixture = keyboard([0x40, 1, 0]);
    let permit = render(target(GREEN));
    run(&fixture, &permit).await;
    assert_eq!(permit.job.outcome(), LightingOutcome::Rendered);
    assert_eq!(permit.job.matrix(), MatrixState::Owned(renderer(GREEN)));
    assert_eq!(permit.job.layout(), Some(0));
    assert_eq!(
        log(&fixture),
        [
            BRIGHTNESS,
            LAYOUT,
            "03/03:0105C5",
            BRIGHTNESS,
            MODE,
            HANDSHAKE,
            FRAME
        ]
    );
    let frames = frames(&fixture);
    assert_eq!(frames.len(), 1);
    assert!(same_colors(&frames[0], GREEN));
    assert_eq!(fixture.state.lock().unwrap().lighting[0], 0xc5);
    // A lit reading confirms the brightness member, never a lit state.
    assert_eq!(permit.job.confirmation().brightness, Some(Some(0xc5)));
}

/// Waking from dark keeps the keyboard dark until the frame is shown: the
/// brightness follows the frame. A layout the session already read is not
/// queried again, and selects its own physical map.
#[tokio::test]
async fn waking_from_dark_raises_the_brightness_after_the_frame() {
    let fixture = keyboard([0, 1, 0]);
    let permit = render(RenderTarget {
        layout: Some(8),
        ..target(GREEN)
    });
    run(&fixture, &permit).await;
    assert_eq!(permit.job.outcome(), LightingOutcome::Rendered);
    assert_eq!(
        permit.job.matrix(),
        MatrixState::Owned(Renderer {
            color: GREEN,
            layout: 8
        })
    );
    assert_eq!(permit.job.layout(), None);
    assert_eq!(
        log(&fixture),
        [
            BRIGHTNESS,
            MODE,
            HANDSHAKE,
            FRAME,
            "03/03:0105C5",
            BRIGHTNESS
        ]
    );
    let maps = super::super::lighting_layout::source_maps().unwrap();
    let iso = RegionalProjection::new(&maps.layout(), 8).project(&MatrixFrame::uniform(GREEN));
    assert_eq!(frames(&fixture)[0].slots(), iso.slots());
}

/// A request for the owned renderer's color keeps it (managed
/// `CanRetainRenderer`): only the brightness and logo that differ are
/// written, with no handshake or frame, and the renderer keeps its cadence.
#[tokio::test]
async fn the_owned_renderer_is_retained_for_its_own_color() {
    let fixture = keyboard([0x40, 1, 2]);
    let permit = render(RenderTarget {
        logo: Some(LogoMode::Static),
        layout: Some(0),
        previous: Some(renderer(GREEN)),
        ..target(GREEN)
    });
    run(&fixture, &permit).await;
    assert_eq!(permit.job.outcome(), LightingOutcome::Rendered);
    assert_eq!(permit.job.matrix(), MatrixState::Kept);
    assert_eq!(
        log(&fixture),
        [
            BRIGHTNESS,
            STATE,
            EFFECT,
            "03/03:0105C5",
            BRIGHTNESS,
            "03/02:010400",
            "03/00:010401",
            STATE,
            EFFECT
        ]
    );
    // From dark, the same color is drawn again before the brightness.
    let fixture = keyboard([0, 1, 0]);
    let permit = render(RenderTarget {
        layout: Some(0),
        previous: Some(renderer(GREEN)),
        ..target(GREEN)
    });
    run(&fixture, &permit).await;
    assert_eq!(permit.job.matrix(), MatrixState::Owned(renderer(GREEN)));
    assert_eq!(frames(&fixture).len(), 1);
}

/// A device mode outside Normal and Driver refuses the handshake before its
/// setter; the brightness written before it is restored, and the matrix,
/// which nothing changed, keeps the previous renderer.
#[tokio::test]
async fn an_unsupported_device_mode_rolls_the_brightness_back() {
    let fixture = keyboard([0x40, 1, 0]);
    fixture.state.lock().unwrap().device_mode = 2;
    let permit = render(RenderTarget {
        layout: Some(0),
        previous: Some(renderer(RED)),
        ..target(GREEN)
    });
    run(&fixture, &permit).await;
    assert_eq!(
        permit.job.outcome(),
        LightingOutcome::Mismatched {
            failure: "The device-mode query 00/84 returned an unsupported mode.",
            restored: Some(true),
        }
    );
    assert_eq!(permit.job.matrix(), MatrixState::Kept);
    assert_eq!(
        log(&fixture),
        [
            BRIGHTNESS,
            "03/03:0105C5",
            BRIGHTNESS,
            MODE,
            "03/03:010540",
            BRIGHTNESS
        ]
    );
    assert_eq!(fixture.state.lock().unwrap().lighting[0], 0x40);
}

/// A frame refused before submission is a known failure: the brightness,
/// then the previous renderer's handshake and frame, are restored once, as
/// managed's rollback does.
#[tokio::test]
async fn a_refused_frame_restores_the_brightness_and_the_previous_renderer() {
    let fixture = keyboard([0x40, 1, 0]);
    fixture.state.lock().unwrap().matrix_refuse = Some(1);
    let permit = render(RenderTarget {
        layout: Some(0),
        previous: Some(renderer(RED)),
        ..target(GREEN)
    });
    run(&fixture, &permit).await;
    assert_eq!(
        permit.job.outcome(),
        LightingOutcome::Mismatched {
            failure: "The keyboard matrix frame could not be sent.",
            restored: Some(true),
        }
    );
    assert_eq!(permit.job.matrix(), MatrixState::Owned(renderer(RED)));
    assert_eq!(
        log(&fixture),
        [
            BRIGHTNESS,
            "03/03:0105C5",
            BRIGHTNESS,
            MODE,
            HANDSHAKE,
            "03/03:010540",
            BRIGHTNESS,
            MODE,
            HANDSHAKE,
            FRAME
        ]
    );
    assert!(same_colors(&frames(&fixture)[0], RED));
    // Without a previous renderer nothing owns the matrix after the
    // handshake.
    let fixture = keyboard([0x40, 1, 0]);
    fixture.state.lock().unwrap().matrix_refuse = Some(1);
    let permit = render(RenderTarget {
        layout: Some(0),
        ..target(GREEN)
    });
    run(&fixture, &permit).await;
    assert_eq!(permit.job.matrix(), MatrixState::Lost);
    assert!(frames(&fixture).is_empty());
}

/// A frame that may have been submitted without completing, a failed
/// feature-report write, stops the job in doubt: nothing more is sent,
/// nothing restores and nothing is replayed.
#[tokio::test]
async fn an_unconfirmed_frame_stops_the_job_in_doubt() {
    let fixture = keyboard([0, 1, 0]);
    fixture.state.lock().unwrap().matrix_fail = Some(1);
    let permit = render(RenderTarget {
        layout: Some(0),
        previous: Some(renderer(RED)),
        ..target(GREEN)
    });
    run(&fixture, &permit).await;
    assert_eq!(
        permit.job.outcome(),
        LightingOutcome::InDoubt(
            "A keyboard matrix frame may have been submitted and was not confirmed."
        )
    );
    assert_eq!(permit.job.matrix(), MatrixState::Lost);
    assert_eq!(log(&fixture), [BRIGHTNESS, MODE, HANDSHAKE, FRAME]);
    assert_eq!(fixture.state.lock().unwrap().lighting[0], 0);
}

/// A handshake setter whose acknowledgement does not arrive stops the job
/// in doubt before any frame.
#[tokio::test]
async fn an_unacknowledged_handshake_stops_the_job_in_doubt() {
    let fixture = keyboard([0, 1, 0]);
    fixture.state.lock().unwrap().lighting_timeout = Some(1);
    let permit = render(RenderTarget {
        layout: Some(0),
        ..target(GREEN)
    });
    run(&fixture, &permit).await;
    assert_eq!(
        permit.job.outcome(),
        LightingOutcome::InDoubt(
            "The lighting transition handshake may have been submitted and was not confirmed."
        )
    );
    assert_eq!(log(&fixture), [BRIGHTNESS, MODE, HANDSHAKE]);
    assert!(frames(&fixture).is_empty());
}

/// A restore that is itself in doubt stops the rollback there (managed
/// `RollbackIndeterminate`), and nothing owns the matrix; a restore
/// confirmed as another value is a known incomplete rollback.
#[tokio::test]
async fn an_indeterminate_rollback_stops_on_ambiguity() {
    let fixture = keyboard([0x40, 1, 0]);
    {
        let mut s = fixture.state.lock().unwrap();
        s.matrix_refuse = Some(1);
        s.matrix_fail = Some(1);
    }
    let permit = render(RenderTarget {
        layout: Some(0),
        previous: Some(renderer(RED)),
        ..target(GREEN)
    });
    run(&fixture, &permit).await;
    assert_eq!(
        permit.job.outcome(),
        LightingOutcome::Mismatched {
            failure: "The keyboard matrix frame could not be sent.",
            restored: None,
        }
    );
    assert_eq!(permit.job.matrix(), MatrixState::Lost);
    assert_eq!(log(&fixture).last().map(String::as_str), Some(FRAME));
    // The restored brightness reads back as another value.
    let fixture = keyboard([0x40, 1, 0]);
    {
        let mut s = fixture.state.lock().unwrap();
        s.matrix_refuse = Some(1);
        s.brightness_script.extend([0x40, 0xc5, 0x41]);
    }
    let permit = render(RenderTarget {
        layout: Some(0),
        previous: Some(renderer(RED)),
        ..target(GREEN)
    });
    run(&fixture, &permit).await;
    assert_eq!(
        permit.job.outcome(),
        LightingOutcome::Mismatched {
            failure: "The keyboard matrix frame could not be sent.",
            restored: Some(false),
        }
    );
}

/// The matrix collection that does not open sends no lighting report.
#[tokio::test]
async fn an_unavailable_matrix_collection_sends_nothing() {
    let fixture = keyboard([0x40, 1, 0]);
    fixture.state.lock().unwrap().matrix_open_fails = true;
    let permit = render(target(GREEN));
    run(&fixture, &permit).await;
    assert_eq!(permit.job.outcome(), LightingOutcome::Unavailable);
    assert!(log(&fixture).is_empty());
}

/// An admission refused at any checkpoint sends nothing after it: what was
/// sent is a prefix of the recipe, and a refusal after the confirmed
/// brightness setter leaves it confirmed, with nothing in doubt.
#[tokio::test]
async fn a_refused_admission_sends_no_further_report() {
    let full = [
        BRIGHTNESS,
        "03/03:0105C5",
        BRIGHTNESS,
        MODE,
        HANDSHAKE,
        FRAME,
    ];
    let mut interrupted = false;
    for from in 1..=40 {
        let fixture = keyboard([0x40, 1, 0]);
        let mut permit = render(RenderTarget {
            layout: Some(0),
            ..target(GREEN)
        });
        permit.refuse_from = Some(from);
        run(&fixture, &permit).await;
        let log = log(&fixture);
        assert!(
            log.len() <= full.len() && log.iter().zip(full).all(|(sent, step)| sent == step),
            "{from}: {log:?}"
        );
        // Only a setter refused between its submission and its confirmed
        // readback is in doubt.
        let outcome = permit.job.outcome();
        if matches!(outcome, LightingOutcome::InDoubt(_)) {
            assert!([2, 3, 5].contains(&log.len()), "{from}: {outcome:?}");
        }
        if log.len() < full.len() {
            assert_ne!(outcome, LightingOutcome::Rendered, "{from}");
        }
        if log.len() == 3 && outcome == LightingOutcome::Interrupted {
            assert_eq!(permit.job.matrix(), MatrixState::Kept, "{from}");
            interrupted = true;
        }
    }
    assert!(interrupted);
}

/// One keepalive frame of the owned renderer and nothing else; one refused
/// before submission keeps the renderer, and one that failed after it ends
/// the renderer in doubt.
#[tokio::test]
async fn a_keepalive_sends_one_frame_or_leaves_what_it_could_not() {
    let fixture = keyboard([0xc5, 1, 0]);
    let permit = Lit::with(LightingJob::keepalive(renderer(GREEN)));
    run(&fixture, &permit).await;
    assert_eq!(permit.job.outcome(), LightingOutcome::Rendered);
    assert_eq!(permit.job.matrix(), MatrixState::Owned(renderer(GREEN)));
    assert_eq!(log(&fixture), [FRAME]);
    assert!(same_colors(&frames(&fixture)[0], GREEN));
    for (fault, matrix) in [("refuse", MatrixState::Kept), ("fail", MatrixState::Lost)] {
        let fixture = keyboard([0xc5, 1, 0]);
        {
            let mut s = fixture.state.lock().unwrap();
            match fault {
                "refuse" => s.matrix_refuse = Some(1),
                _ => s.matrix_fail = Some(1),
            }
        }
        let permit = Lit::with(LightingJob::keepalive(renderer(GREEN)));
        run(&fixture, &permit).await;
        assert_eq!(permit.job.matrix(), matrix, "{fault}");
        assert!(fixture.state.lock().unwrap().sent.is_empty(), "{fault}");
    }
    // A refused admission sends nothing.
    let fixture = keyboard([0xc5, 1, 0]);
    let mut permit = Lit::with(LightingJob::keepalive(renderer(GREEN)));
    permit.refuse_from = Some(2);
    run(&fixture, &permit).await;
    assert_eq!(permit.job.matrix(), MatrixState::Kept);
    assert!(log(&fixture).is_empty());
}

/// The transaction IDs a renderer's frames carry cycle through 1 to 255.
#[tokio::test]
async fn matrix_transaction_ids_are_never_zero() {
    let fixture = keyboard([0xc5, 1, 0]);
    for _ in 0..3 {
        run(
            &fixture,
            &Lit::with(LightingJob::keepalive(renderer(GREEN))),
        )
        .await;
    }
    let s = fixture.state.lock().unwrap();
    let ids: Vec<u8> = s
        .matrix_frames
        .iter()
        .map(|(_, report)| decode_feature_report(report).unwrap().0)
        .collect();
    assert_eq!(ids.len(), 3);
    assert!(ids.iter().all(|&id| id != 0));
}

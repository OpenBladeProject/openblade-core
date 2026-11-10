use super::*;
use std::{env, sync::mpsc};

const MODE: &str = "OPENBLADE_BATTERY_INERT_CHILD";
const OUTPUT: &str = "OPENBLADE_BATTERY_INERT_OUTPUT";
const LIMIT: Duration = Duration::from_secs(5);

#[test]
fn inert_child() {
    let Some(mode) = env::var_os(MODE) else {
        return;
    };
    let output = PathBuf::from(env::var_os(OUTPUT).unwrap());
    match mode.to_str().unwrap() {
        "ok" => fs::write(&output, super::super::TIMELINE).unwrap(),
        "invalid" => fs::write(&output, b"<BatteryReport>").unwrap(),
        "empty" => fs::write(&output, []).unwrap(),
        "oversized" => File::create(&output)
            .unwrap()
            .set_len(MAX_BYTES as u64 + 1)
            .unwrap(),
        "missing" => {}
        "exit" => std::process::exit(7),
        "stall" | "race" => {
            fs::write(&output, super::super::TIMELINE).unwrap();
            let release = output.parent().unwrap().parent().unwrap().join("release");
            let deadline = Instant::now() + LIMIT;
            while Instant::now() < deadline && (mode == "stall" || !release.is_file()) {
                thread::sleep(POLL);
            }
        }
        _ => panic!("unrecognized inert child mode"),
    }
}

fn child(mode: &str, output: &Path) -> Command {
    let mut command = Command::new(env::current_exe().unwrap());
    command
        .args([
            "--exact",
            "session_battery::acquisition::tests::inert_child",
        ])
        .env(MODE, mode)
        .env(OUTPUT, output);
    command
}

#[test]
fn reads_only_bounded_valid_output_and_cleans_each_attempt() {
    let root = Attempt::create(&env::temp_dir()).unwrap();
    let neighbor = root.path.join("neighbor");
    fs::write(&neighbor, b"keep").unwrap();
    let cancelled = AtomicBool::new(false);
    let mut paths = Vec::new();
    for (mode, expected) in [
        ("ok", Ok(())),
        ("missing", Err(Failure::Output)),
        ("empty", Err(Failure::Output)),
        ("invalid", Err(Failure::InvalidReport)),
        ("oversized", Err(Failure::Output)),
        ("exit", Err(Failure::ProcessExit)),
    ] {
        let result = acquire(
            &root.path,
            |output| {
                paths.push(output.parent().unwrap().to_owned());
                child(mode, output)
            },
            &cancelled,
            LIMIT,
        );
        assert_eq!(
            result.map(|report| {
                assert!(report.valid_on_wire());
                assert_eq!(
                    serde_json::to_value(report).unwrap()["generatedAtUtc"],
                    "2026-01-02T12:00:00+00:00"
                );
            }),
            expected,
            "{mode}"
        );
        assert!(!paths.last().unwrap().exists(), "{mode}");
    }
    paths.sort();
    paths.dedup();
    assert_eq!(paths.len(), 6);
    assert_eq!(fs::read(&neighbor).unwrap(), b"keep");
    fs::remove_file(neighbor).unwrap();
}

#[test]
fn deadline_stops_the_child_before_cleaning_its_attempt() {
    let root = Attempt::create(&env::temp_dir()).unwrap();
    let started = Instant::now();
    let result = acquire(
        &root.path,
        |output| child("stall", output),
        &AtomicBool::new(false),
        Duration::from_millis(500),
    );
    assert_eq!(result.err(), Some(Failure::Timeout));
    assert!(started.elapsed() < LIMIT);
    assert!(fs::read_dir(&root.path).unwrap().next().is_none());
}

#[test]
fn cancellation_at_child_exit_discards_the_report_and_cleans_the_attempt() {
    let root = Attempt::create(&env::temp_dir()).unwrap();
    let cancelled = AtomicBool::new(false);
    let (path_tx, path_rx) = mpsc::channel();
    let release = root.path.join("release");
    thread::scope(|scope| {
        let owner = scope.spawn(|| {
            acquire(
                &root.path,
                |output| {
                    path_tx.send(output.to_owned()).unwrap();
                    child("race", output)
                },
                &cancelled,
                LIMIT,
            )
        });
        let output = path_rx.recv_timeout(LIMIT).unwrap();
        let deadline = Instant::now() + LIMIT;
        while !output.is_file() {
            assert!(
                Instant::now() < deadline,
                "inert child never produced output"
            );
            thread::sleep(POLL);
        }
        cancelled.store(true, Ordering::Release);
        fs::write(&release, []).unwrap();
        assert_eq!(owner.join().unwrap().err(), Some(Failure::Cancelled));
        assert!(!output.parent().unwrap().exists());
    });
    fs::remove_file(release).unwrap();
}

#[test]
fn pre_cancelled_caller_never_allocates_or_launches() {
    let root = Attempt::create(&env::temp_dir()).unwrap();
    let result = acquire(
        &root.path,
        |_| panic!("cancelled before launch"),
        &AtomicBool::new(true),
        LIMIT,
    );
    assert_eq!(result.err(), Some(Failure::Cancelled));
    assert!(fs::read_dir(&root.path).unwrap().next().is_none());
}

#[test]
fn cleanup_leaves_unexpected_files_in_the_attempt_alone() {
    let root = Attempt::create(&env::temp_dir()).unwrap();
    let mut unexpected = None;
    let result = acquire(
        &root.path,
        |output| {
            let path = output.with_file_name("unexpected");
            fs::write(&path, b"keep").unwrap();
            unexpected = Some(path);
            child("ok", output)
        },
        &AtomicBool::new(false),
        LIMIT,
    );
    assert!(result.is_ok());
    let unexpected = unexpected.unwrap();
    assert!(!unexpected.with_file_name(REPORT_FILE).exists());
    assert_eq!(fs::read(&unexpected).unwrap(), b"keep");
    fs::remove_file(&unexpected).unwrap();
    fs::remove_dir(unexpected.parent().unwrap()).unwrap();
}

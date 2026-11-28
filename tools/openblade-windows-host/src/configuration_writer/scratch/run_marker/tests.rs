use super::*;
use crate::configuration_writer::{WRITER_LOCK, file_identity, handle_information, pin_directory};
use std::fs;

const NOW: &str = "2026-08-06T08:00:00+00:00";
const ID: &str = "12345678-1234-4234-8234-123456789abc";

// Exact UTF-8 emitted by ServiceRunMarkerDocument with the pinned managed
// Web/indented options at d3c9fa4fd0f04856f2d50270a1ed4c0900d492cf.
const MANAGED_BOUND: &str = r#"{
  "schemaVersion": 2,
  "startedAtUtc": "2026-08-06T08:00:00.1234567+00:00",
  "uptimeAtStart": "1.02:03:04.5678901",
  "runId": "12345678-1234-4234-8234-123456789abc"
}"#;
const MANAGED_UNBOUND: &str = r#"{
  "schemaVersion": 2,
  "startedAtUtc": "2026-08-06T08:00:00.1234567+00:00",
  "uptimeAtStart": "1.02:03:04.5678901"
}"#;

struct Scratch {
    path: PathBuf,
    root: String,
    lock: String,
}

impl Scratch {
    fn new() -> Self {
        let path = std::env::temp_dir().join(format!(
            "openblade-writer-fence-{}",
            transaction_id().unwrap()
        ));
        fs::create_dir(&path).unwrap();
        let lock = File::create_new(path.join(WRITER_LOCK)).unwrap();
        let root = pin_directory(&path).unwrap();
        let identity = |file| {
            let id = file_identity(&handle_information(file).unwrap());
            format!("{:08x}:{:016x}", id.volume, id.index)
        };
        Self {
            path,
            root: identity(&root),
            lock: identity(&lock),
        }
    }

    fn store(&self) -> RunMarkerStore {
        RunMarkerStore::new(&self.path, &self.root, &self.lock)
    }

    fn marker(&self) -> PathBuf {
        self.path.join(MARKER)
    }

    fn temp(&self) -> PathBuf {
        self.path.join(TEMP)
    }
}

impl Drop for Scratch {
    fn drop(&mut self) {
        for name in [MARKER, TEMP, WRITER_LOCK] {
            match fs::remove_file(self.path.join(name)) {
                Ok(()) => {}
                Err(error) if error.kind() == io::ErrorKind::NotFound => {}
                Err(error) => panic!("scratch entry cleanup failed: {error}"),
            }
        }
        fs::remove_dir(&self.path).unwrap();
    }
}

fn sample() -> io::Result<Clock> {
    Ok(Clock::scripted(NOW, "01:00:00"))
}

fn begin(store: &mut RunMarkerStore) -> io::Result<StartupKind> {
    store.begin(sample, None::<fn() -> io::Result<()>>, |_| Ok(()))
}

fn fail_at(expected: Boundary) -> impl FnMut(Boundary) -> io::Result<()> {
    move |boundary| {
        if boundary == expected {
            return Err(io::Error::other("scripted failure"));
        }
        Ok(())
    }
}

#[test]
fn managed_v2_bytes_round_trip_and_both_forms_can_prove_a_restart() {
    let scripted = Clock::scripted("2026-08-06T08:00:00.1234567Z", "1.02:03:04.5678901");
    assert_eq!(
        format::encode(scripted, ID).unwrap(),
        MANAGED_BOUND.replace('\n', "\r\n").as_bytes()
    );
    for fixture in [MANAGED_BOUND, MANAGED_UNBOUND] {
        let scratch = Scratch::new();
        fs::write(scratch.marker(), fixture.replace('\n', "\r\n")).unwrap();
        let mut store = scratch.store();
        let later_boot = || Ok(Clock::scripted("2026-08-07T08:00:00Z", "00:05:00"));
        assert_eq!(
            store
                .begin(later_boot, None::<fn() -> io::Result<()>>, |_| Ok(()))
                .unwrap(),
            StartupKind::Normal
        );
        store.complete_run().unwrap();
        assert!(!scratch.marker().exists());
    }
}

#[test]
fn recovery_requires_both_strict_clock_inequalities_for_a_normal_restart() {
    let classify = |utc: &str, uptime: &str, prior_utc: &str, prior_uptime: &str| {
        let prior = serde_json::json!({
            "schemaVersion": 2, "startedAtUtc": prior_utc, "uptimeAtStart": prior_uptime
        });
        format::classify(Clock::scripted(utc, uptime), prior.to_string().as_bytes())
    };
    for (utc, uptime, prior_utc, prior_uptime) in [
        (
            "2026-08-06T08:10:00Z",
            "01:10:00",
            "2026-08-06T08:00:00Z",
            "01:00:00",
        ),
        (
            "2026-08-06T07:06:00Z",
            "00:01:00",
            "2026-08-06T07:00:00Z",
            "00:00:30",
        ),
        (
            "2026-08-06T06:30:00Z",
            "00:05:00",
            "2026-08-06T07:00:00Z",
            "02:00:00",
        ),
        (NOW, "00:05:00", "2026-08-06T08:55:00+01:00", "02:00:00"),
        (NOW, "02:00:00", "2026-08-05T12:00:00Z", "02:00:00"),
        (NOW, "00:05:00", "0001-01-01T00:00:00Z", "02:00:00"),
        (
            "0001-01-01T00:00:00Z",
            "00:05:00",
            "0001-01-01T00:00:01Z",
            "02:00:00",
        ),
        (NOW, "-00:05:00", "2026-08-05T12:00:00Z", "02:00:00"),
    ] {
        assert_eq!(
            classify(utc, uptime, prior_utc, prior_uptime),
            StartupKind::UnexpectedExitRecovery,
            "{utc}, {uptime}, {prior_utc}, {prior_uptime}"
        );
    }
    assert_eq!(
        classify(NOW, "00:05:00", "2026-08-05T12:00:00Z", "02:00:00"),
        StartupKind::Normal
    );
    assert_eq!(
        classify(
            NOW,
            "00:05:00",
            "2026-08-06T08:54:59.9999999+01:00",
            "02:00:00"
        ),
        StartupKind::Normal
    );
}

#[test]
fn invalid_markers_fail_closed_and_ordinary_startup_replaces_them() {
    let scratch = Scratch::new();
    for prior in [
        "null".to_owned(),
        "{".to_owned(),
        r#"{"schemaVersion":1,"startedAtUtc":"2026-08-05T12:00:00+00:00"}"#.to_owned(),
        MANAGED_BOUND.replace("Version\": 2", "Version\": 3"),
        MANAGED_BOUND.replace("1.02", "-1.02"),
        MANAGED_BOUND.replace("12345678-1234-4234-8234-123456789abc", "invalid-guid"),
        MANAGED_BOUND.replace("2026-08-06", "2026-02-29"),
    ] {
        fs::write(scratch.marker(), &prior).unwrap();
        let mut store = scratch.store();
        assert_eq!(
            begin(&mut store).unwrap(),
            StartupKind::UnexpectedExitRecovery,
            "{prior}"
        );
        assert_ne!(fs::read(scratch.marker()).unwrap(), prior.as_bytes());
        store.complete_run().unwrap();
    }
}

#[test]
fn exact_owned_bytes_and_run_id_fence_stale_or_unstarted_cleanup() {
    let scratch = Scratch::new();
    let mut first = scratch.store();
    let mut second = scratch.store();
    assert_eq!(begin(&mut first).unwrap(), StartupKind::Normal);
    let first_bytes = fs::read(scratch.marker()).unwrap();
    assert!(begin(&mut first).is_err());
    assert_eq!(
        begin(&mut second).unwrap(),
        StartupKind::UnexpectedExitRecovery
    );
    let second_bytes = fs::read(scratch.marker()).unwrap();
    assert_ne!(first_bytes, second_bytes);
    assert!(first.complete_run().is_err());
    assert!(scratch.store().complete_run().is_err());
    assert_eq!(fs::read(scratch.marker()).unwrap(), second_bytes);
    let mut edited_bytes = second_bytes.clone();
    edited_bytes.push(b'\n');
    fs::write(scratch.marker(), &edited_bytes).unwrap();
    assert!(second.complete_run().is_err());
    assert_eq!(fs::read(scratch.marker()).unwrap(), edited_bytes);
    fs::write(scratch.marker(), &second_bytes).unwrap();
    second.complete_run().unwrap();
    assert!(!scratch.marker().exists());
    assert_eq!(begin(&mut second).unwrap(), StartupKind::Normal);
}

#[test]
fn lease_covers_clock_handoff_and_cleanup_and_rejects_changed_enrollment() {
    let scratch = Scratch::new();
    let mut store = scratch.store();
    let held = store.lease().unwrap();
    assert_eq!(begin(&mut store).unwrap_err().raw_os_error(), Some(32));
    assert!(!scratch.marker().exists());
    drop(held);
    let clock = || {
        assert_eq!(
            scratch.store().lease().err().unwrap().raw_os_error(),
            Some(32)
        );
        sample()
    };
    store
        .begin(clock, None::<fn() -> io::Result<()>>, |_| Ok(()))
        .unwrap();
    let bytes = fs::read(scratch.marker()).unwrap();
    let held = store.lease().unwrap();
    assert_eq!(store.complete_run().unwrap_err().raw_os_error(), Some(32));
    drop(held);
    let original = store.lock_id.clone();
    store.lock_id = "00000000:0000000000000001".into();
    assert!(store.complete_run().is_err());
    assert_eq!(fs::read(scratch.marker()).unwrap(), bytes);
    store.lock_id = original;
    store.complete_run().unwrap();
}

#[test]
fn interrupted_stage_and_failed_replacement_keep_both_artifacts() {
    use std::os::windows::fs::OpenOptionsExt;

    let scratch = Scratch::new();
    let mut outgoing = scratch.store();
    begin(&mut outgoing).unwrap();
    let bytes = fs::read(scratch.marker()).unwrap();
    let mut incoming = scratch.store();
    let failed = incoming.begin(
        sample,
        None::<fn() -> io::Result<()>>,
        fail_at(Boundary::BeforeStage),
    );
    assert!(failed.is_err());
    assert!(!scratch.temp().exists());

    let blocked = fs::OpenOptions::new()
        .read(true)
        .share_mode(FILE_SHARE_READ)
        .open(scratch.marker())
        .unwrap();
    assert!(outgoing.complete_run().is_err());
    assert!(begin(&mut incoming).is_err());
    drop(blocked);
    let staged = fs::read(scratch.temp()).unwrap();
    assert_eq!(fs::read(scratch.marker()).unwrap(), bytes);
    assert!(begin(&mut incoming).is_err());
    assert!(outgoing.complete_run().is_err());
    assert_eq!(fs::read(scratch.temp()).unwrap(), staged);
    assert_eq!(fs::read(scratch.marker()).unwrap(), bytes);

    fs::remove_file(scratch.temp()).unwrap();
    assert!(
        incoming
            .begin(
                sample,
                None::<fn() -> io::Result<()>>,
                fail_at(Boundary::Staged)
            )
            .is_err()
    );
    assert!(scratch.temp().exists());
    assert_eq!(fs::read(scratch.marker()).unwrap(), bytes);
}

#[test]
fn failed_confirmation_leaves_marker_and_grants_no_cleanup_authority() {
    let scratch = Scratch::new();
    let mut store = scratch.store();
    assert!(
        store
            .begin(
                sample,
                None::<fn() -> io::Result<()>>,
                fail_at(Boundary::Published)
            )
            .is_err()
    );
    let bytes = fs::read(scratch.marker()).unwrap();
    assert!(!scratch.temp().exists());
    assert!(store.complete_run().is_err());
    assert_eq!(fs::read(scratch.marker()).unwrap(), bytes);
    assert_eq!(
        begin(&mut store).unwrap(),
        StartupKind::UnexpectedExitRecovery
    );
    let bytes = fs::read(scratch.marker()).unwrap();
    assert!(store.complete(fail_at(Boundary::BeforeClear)).is_err());
    assert_eq!(fs::read(scratch.marker()).unwrap(), bytes);
    store.complete_run().unwrap();
}

#[test]
fn explicit_handoff_needs_retirement_of_the_expected_generation_and_absence() {
    let scratch = Scratch::new();
    let mut incoming = scratch.store();
    let expected = "outgoing-generation";
    let verify = |receipt| {
        assert_eq!(
            scratch.store().lease().err().unwrap().raw_os_error(),
            Some(32)
        );
        if receipt != Some(expected) {
            return Err(io::Error::other(
                "outgoing generation retirement is unconfirmed",
            ));
        }
        Ok(())
    };

    for receipt in [None, Some("different-generation")] {
        assert!(
            incoming
                .begin(sample, Some(|| verify(receipt)), |_| Ok(()))
                .is_err()
        );
        assert!(!scratch.marker().exists());
        assert!(!scratch.temp().exists());
    }
    let mut outgoing = scratch.store();
    begin(&mut outgoing).unwrap();
    let bytes = fs::read(scratch.marker()).unwrap();
    assert!(
        incoming
            .begin(sample, Some(|| verify(Some(expected))), |_| Ok(()))
            .is_err()
    );
    assert_eq!(fs::read(scratch.marker()).unwrap(), bytes);
    outgoing.complete_run().unwrap();

    fs::write(scratch.temp(), b"interrupted outgoing marker").unwrap();
    assert!(
        incoming
            .begin(
                sample,
                Some(|| panic!("temp should block before verification")),
                |_| Ok(())
            )
            .is_err()
    );
    assert_eq!(
        fs::read(scratch.temp()).unwrap(),
        b"interrupted outgoing marker"
    );
    fs::remove_file(scratch.temp()).unwrap();
    assert_eq!(
        incoming
            .begin(sample, Some(|| verify(Some(expected))), |_| Ok(()))
            .unwrap(),
        StartupKind::Normal
    );
    incoming.complete_run().unwrap();
}

#[test]
fn native_clocks_and_public_scratch_entry_points() {
    let scratch = Scratch::new();
    let mut store = scratch.store();
    assert_eq!(store.begin_handoff(|| Ok(())).unwrap(), StartupKind::Normal);
    assert_eq!(
        scratch.store().begin_run().unwrap(),
        StartupKind::UnexpectedExitRecovery
    );
}

#[test]
fn cleanup_does_not_report_success_while_a_delete_sharing_reader_holds_the_marker() {
    use std::os::windows::fs::OpenOptionsExt;
    use windows_sys::Win32::Storage::FileSystem::FILE_SHARE_DELETE;

    let scratch = Scratch::new();
    let mut store = scratch.store();
    begin(&mut store).unwrap();
    let reader = fs::OpenOptions::new()
        .read(true)
        .share_mode(FILE_SHARE_READ | FILE_SHARE_DELETE)
        .open(scratch.marker())
        .unwrap();
    let bytes = fs::read(scratch.marker()).unwrap();
    assert_eq!(store.complete_run().unwrap_err().raw_os_error(), Some(32));
    assert_eq!(fs::read(scratch.marker()).unwrap(), bytes);
    drop(reader);
    store.complete_run().unwrap();
    assert!(!scratch.marker().exists());
}

#[test]
fn managed_web_decoder_forms_keep_the_same_reboot_classification() {
    let clock = Clock::scripted(NOW, "00:05:00");
    for (date, uptime) in [
        ("2026-08-05", "1"),
        ("2026-08-05T08:00:00", "1.02:03"),
        ("2026-08-05T08:00Z", "12:34"),
        ("2026-08-05T08:00:00.123456789+02", "1.2:3:4.5"),
        ("2026-08-05T08:00:00-14:00", "1:2:3"),
    ] {
        let marker = serde_json::json!({
            "SCHEMAVERSION": "+2",
            "STARTEDATUTC": date,
            "uptimeAtStart": uptime,
            "runId": null,
            "ignored": ["value"]
        });
        assert_eq!(
            format::classify(clock, marker.to_string().as_bytes()),
            StartupKind::Normal,
            "{date}, {uptime}"
        );
    }
}

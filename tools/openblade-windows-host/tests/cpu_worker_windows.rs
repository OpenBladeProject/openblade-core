#![cfg(all(windows, feature = "cpu-worker-fixture"))]

use std::{
    env, fs, io,
    path::PathBuf,
    time::{Duration, SystemTime, UNIX_EPOCH},
};

use openblade_windows_host::cpu_worker::windows::{FixtureSupervisor, Stage};
use tokio::time::Instant;

const FIXTURE_IMAGE: &str = "openblade-cpu-worker-fixture.exe";

struct FixtureDirectory(PathBuf);

impl FixtureDirectory {
    fn empty() -> io::Result<Self> {
        let nonce = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap()
            .as_nanos();
        let path = env::temp_dir().join(format!(
            "openblade-cpu-fixture-{}-{nonce}",
            std::process::id()
        ));
        fs::create_dir(&path)?;
        Ok(Self(path))
    }

    fn with_fixture() -> io::Result<Self> {
        let owned = Self::empty()?;
        fs::copy(
            env!("CARGO_BIN_EXE_openblade-cpu-worker-fixture"),
            owned.0.join(FIXTURE_IMAGE),
        )?;
        Ok(owned)
    }
}

impl Drop for FixtureDirectory {
    fn drop(&mut self) {
        let _ = fs::remove_file(self.0.join(FIXTURE_IMAGE));
        let _ = fs::remove_dir(&self.0);
    }
}

#[tokio::test]
#[ignore = "explicit ordinary-user Win32 process and private pipe smoke"]
async fn exchanges_with_an_inert_job_owned_fixture() {
    let directory = FixtureDirectory::with_fixture().unwrap();
    let mut worker = FixtureSupervisor::new(&directory.0).unwrap();
    assert!(
        worker.prepare().await,
        "readiness failed: {:?}",
        worker.diagnostics()
    );
    let start = Instant::now();
    let sample = worker.sample().await.unwrap_or_else(|| {
        panic!("fixture sample failed: {:?}", worker.diagnostics());
    });
    assert_eq!(
        sample.celsius_for(start, Instant::now(), Duration::from_secs(1)),
        Some(42)
    );
    assert!(
        worker.retire().await,
        "exact exit was not confirmed: {:?}",
        worker.diagnostics()
    );
    assert_eq!(worker.diagnostics().1, Default::default());
    println!("fixture ready, sample 42 C, and original process handle signaled");
}

#[tokio::test]
async fn missing_fixed_image_fails_before_creating_a_process() {
    let directory = FixtureDirectory::empty().unwrap();
    let mut worker = FixtureSupervisor::new(&directory.0).unwrap();
    assert!(!worker.prepare().await);
    let (supervisor, native) = worker.diagnostics();
    assert!(!supervisor.exit_unconfirmed);
    let fault = native.transport.unwrap();
    assert_eq!(fault.stage, Stage::Image);
    assert!(matches!(fault.win32, Some(2 | 3)));
}
